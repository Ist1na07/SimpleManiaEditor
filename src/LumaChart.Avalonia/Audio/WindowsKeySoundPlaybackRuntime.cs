using LumaChart.Avalonia.Infrastructure;
using System.Collections.Immutable;
using System.Diagnostics;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Audio;

internal enum WindowsKeySoundPlaybackOutput
{
    None,
    SharedMusicClock,
    SilentEditorClock
}

internal sealed record WindowsKeySoundPlaybackRuntimeCounters(
    long PlanRequests,
    long PlansPublished,
    long PlansSuperseded,
    long PlanFailures,
    long PreloadAttempts,
    long PreloadAccepted,
    long PreloadRejected,
    long PumpCalls,
    long TriggersDue,
    long TriggersAccepted,
    long TriggersRejected,
    long SharedAccepted,
    long SharedRejected,
    long StandaloneAccepted,
    long StandaloneRejected,
    long PlaybackInvalidations,
    long CursorResets,
    long SilentStarts,
    long SilentPauses,
    long SilentStops,
    long SilentSeeks,
    long SilentRateChanges,
    long PreviewAccepted,
    long PreviewRejected,
    long ClockSamplesPublished,
    long ClockSamplesReplaced,
    long ClockSamplesProcessed,
    long SubmittedVoices);

internal sealed record WindowsKeySoundPlaybackRuntimeSnapshot(
    Guid? RequestedTabId,
    Guid? PlannedTabId,
    long PlanGeneration,
    bool IsPreparing,
    bool HasPlan,
    bool IsEnabled,
    bool IsSilentPlaying,
    double PositionMilliseconds,
    double PlaybackRate,
    int PlannedEventCount,
    int PlannedPreloadCount,
    string Status,
    Exception? LastError,
    bool OwnsKeySoundService,
    WindowsKeySoundPlaybackRuntimeCounters Counters);

internal readonly record struct WindowsKeySoundPlaybackPumpResult(
    WindowsKeySoundPlaybackOutput Output,
    double PositionMilliseconds,
    double PlaybackRate,
    int Due,
    int Accepted,
    int Rejected)
{
    public static WindowsKeySoundPlaybackPumpResult Idle => new(
        WindowsKeySoundPlaybackOutput.None,
        0,
        1,
        0,
        0,
        0);
}

/// <summary>
/// UI-agnostic Key 音 scheduler. UI calls publish one capacity-1 latest clock
/// sample and return immediately. A single worker exclusively owns the mutable
/// coordinator cursor, preserves complete chord ordering, and submits only
/// already-decoded sample batches.
/// </summary>
internal sealed class WindowsKeySoundPlaybackRuntime : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly WindowsAudioTransportViewModel transport;
    private readonly WindowsKeySoundService keySounds;
    private readonly bool ownsKeySoundService;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationTokenSource schedulerCancellation = new();
    private readonly SemaphoreSlim preparationGate = new(1, 1);
    private readonly SemaphoreSlim schedulerSignal = new(0, 1);
    private readonly Queue<SchedulerControl> schedulerControls = [];
    private readonly HashSet<Task> preparationTasks = [];
    private readonly Task schedulerTask;

    private CancellationTokenSource? preparationCancellation;
    private Task currentPreparation = Task.CompletedTask;
    private SchedulerReset? pendingReset;
    private ClockSample? pendingClockSample;
    private bool schedulerSignaled;

    // UI/snapshot metadata. The worker owns the actual coordinator and pin set.
    private Guid? requestedTabId;
    private Guid? plannedTabId;
    private long planGeneration;
    private bool isPreparing;
    private bool hasPlan;
    private bool planEnabled;
    private int plannedEventCount;
    private int plannedPreloadCount;
    private string status = "Key 音计划尚未准备";
    private Exception? lastError;
    private bool silentPlaying;
    private double silentBasePositionMilliseconds;
    private double silentPlaybackRate = 1;
    private long silentStartedTimestamp;
    private double latestPlaybackPositionMilliseconds;
    private double latestPlaybackRate = 1;
    private long latestPlaybackSampleGeneration;
    private int disposed;

    // Evidence counters are always read/written under gate.
    private long planRequests;
    private long plansPublished;
    private long plansSuperseded;
    private long planFailures;
    private long preloadAttempts;
    private long preloadAccepted;
    private long preloadRejected;
    private long pumpCalls;
    private long triggersDue;
    private long triggersAccepted;
    private long triggersRejected;
    private long sharedAccepted;
    private long sharedRejected;
    private long standaloneAccepted;
    private long standaloneRejected;
    private long playbackInvalidations;
    private long cursorResets;
    private long silentStarts;
    private long silentPauses;
    private long silentStops;
    private long silentSeeks;
    private long silentRateChanges;
    private long previewAccepted;
    private long previewRejected;
    private long clockSamplesPublished;
    private long clockSamplesReplaced;
    private long clockSamplesProcessed;
    private long submittedVoices;

    // Worker-only state.
    private WindowsKeySoundPlaybackCoordinator? workerCoordinator;
    private ImmutableArray<WindowsKeySoundPlanPreloadLease> workerPlanPins = [];
    private Guid? workerTabId;
    private long workerPlanGeneration;

    public WindowsKeySoundPlaybackRuntime(WindowsAudioTransportViewModel transport)
        : this(transport, new WindowsKeySoundService(), ownsKeySoundService: true)
    {
    }

    public WindowsKeySoundPlaybackRuntime(
        WindowsAudioTransportViewModel transport,
        WindowsKeySoundService keySoundService,
        bool ownsKeySoundService)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        keySounds = keySoundService ?? throw new ArgumentNullException(nameof(keySoundService));
        this.ownsKeySoundService = ownsKeySoundService;
        schedulerTask = Task.Run(() => SchedulerLoopAsync(schedulerCancellation.Token));
    }

    /// <remarks>Raised on the caller or preparation worker thread.</remarks>
    public event EventHandler? StateChanged;

    public bool OwnsKeySoundService => ownsKeySoundService;

    public float Volume
    {
        get => keySounds.Volume;
        set => keySounds.Volume = value;
    }

    public Task CurrentPreparation
    {
        get { lock (gate) return currentPreparation; }
    }

    public WindowsKeySoundPlaybackRuntimeSnapshot Snapshot
    {
        get { lock (gate) return CreateSnapshotLocked(Stopwatch.GetTimestamp()); }
    }

    public Task RequestPlanAsync(
        Guid tabId,
        ChartDocument documentClone,
        string sourceChartPath,
        IReadOnlySet<int> hiddenGroupsSnapshot,
        WindowsKeySoundPlaybackOptions options,
        double currentPositionMilliseconds,
        double playbackRate,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(documentClone);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceChartPath);
        ArgumentNullException.ThrowIfNull(hiddenGroupsSnapshot);
        ValidateClock(currentPositionMilliseconds, playbackRate);

        var immutableHiddenGroups = hiddenGroupsSnapshot.ToImmutableHashSet();
        CancellationTokenSource? previousCancellation;
        CancellationTokenSource requestCancellation;
        Task preparation;
        Guid? invalidatedTab;
        long generation;
        lock (gate)
        {
            ThrowIfDisposedLocked();
            previousCancellation = preparationCancellation;
            if (isPreparing) plansSuperseded++;
            var switchesTab = (requestedTabId ?? plannedTabId) is { } currentTabId &&
                              currentTabId != tabId;
            invalidatedTab = plannedTabId ?? requestedTabId;
            generation = ++planGeneration;
            planRequests++;
            requestedTabId = tabId;
            plannedTabId = null;
            hasPlan = false;
            planEnabled = false;
            plannedEventCount = 0;
            plannedPreloadCount = 0;
            isPreparing = true;
            pendingClockSample = null;
            latestPlaybackPositionMilliseconds = currentPositionMilliseconds;
            latestPlaybackRate = playbackRate;
            latestPlaybackSampleGeneration = generation;
            if (switchesTab)
            {
                silentPlaying = false;
                silentBasePositionMilliseconds = currentPositionMilliseconds;
                silentPlaybackRate = playbackRate;
                silentStartedTimestamp = Stopwatch.GetTimestamp();
            }
            status = AvaloniaLocalizationCatalog.Format("正在准备 Key 音计划：{0}", Path.GetFileName(sourceChartPath));
            lastError = null;
            EnqueueControlLocked(new SchedulerClear(generation));

            requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                lifetime.Token,
                cancellationToken);
            preparationCancellation = requestCancellation;
            preparation = PreparePlanAsync(
                tabId,
                documentClone,
                sourceChartPath,
                immutableHiddenGroups,
                options,
                currentPositionMilliseconds,
                playbackRate,
                generation,
                requestCancellation);
            currentPreparation = preparation;
            preparationTasks.Add(preparation);
        }

        InvalidateOutputs(invalidatedTab);
        CancelNoThrow(previousCancellation);
        _ = ObservePreparationCompletionAsync(preparation);
        RaiseStateChanged();
        return preparation;
    }

    public Task RequestPlan(
        Guid tabId,
        ChartDocument documentClone,
        string sourceChartPath,
        IReadOnlySet<int> hiddenGroupsSnapshot,
        WindowsKeySoundPlaybackOptions options,
        double currentPositionMilliseconds,
        double playbackRate,
        CancellationToken cancellationToken = default) =>
        RequestPlanAsync(
            tabId,
            documentClone,
            sourceChartPath,
            hiddenGroupsSnapshot,
            options,
            currentPositionMilliseconds,
            playbackRate,
            cancellationToken);

    private async Task PreparePlanAsync(
        Guid tabId,
        ChartDocument documentClone,
        string sourceChartPath,
        ImmutableHashSet<int> hiddenGroupsSnapshot,
        WindowsKeySoundPlaybackOptions options,
        double requestedPositionMilliseconds,
        double requestedPlaybackRate,
        long generation,
        CancellationTokenSource requestCancellation)
    {
        var token = requestCancellation.Token;
        var attempted = 0L;
        var accepted = 0L;
        var rejected = 0L;
        var pins = ImmutableArray.CreateBuilder<WindowsKeySoundPlanPreloadLease>();
        var preparationGateHeld = false;
        var preloadCountersCommitted = false;
        try
        {
            await preparationGate.WaitAsync(token).ConfigureAwait(false);
            preparationGateHeld = true;
            var prepared = await Task.Run(
                    () => new WindowsKeySoundPlaybackCoordinator(
                        documentClone,
                        sourceChartPath,
                        hiddenGroupsSnapshot,
                        options,
                        File.Exists,
                        token),
                    CancellationToken.None)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            foreach (var path in prepared.PreloadPaths)
            {
                token.ThrowIfCancellationRequested();
                attempted++;
                var pin = await keySounds.PreloadPinnedAsync(path, token).ConfigureAwait(false);
                if (pin == null) rejected++;
                else
                {
                    pins.Add(pin);
                    accepted++;
                }
            }
            token.ThrowIfCancellationRequested();

            double resetPosition;
            double resetRate;
            TaskCompletionSource<bool> installed;
            lock (gate)
            {
                AddPreloadCountersLocked(attempted, accepted, rejected);
                preloadCountersCommitted = true;
                if (!IsCurrentPreparationLocked(tabId, generation, requestCancellation)) return;
                resetPosition = latestPlaybackSampleGeneration == generation
                    ? latestPlaybackPositionMilliseconds
                    : requestedPositionMilliseconds;
                resetRate = latestPlaybackSampleGeneration == generation
                    ? latestPlaybackRate
                    : requestedPlaybackRate;
                installed = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                EnqueueControlLocked(new SchedulerInstall(
                    generation,
                    tabId,
                    prepared,
                    pins.ToImmutable(),
                    resetPosition,
                    resetRate,
                    installed));
            }

            // Once install is queued, only the scheduler may decide whether it
            // takes ownership. Do not cancel this await: otherwise cancellation
            // can dispose the builder pins while the worker concurrently installs
            // the same leases. A current canceled request enqueues Clear below.
            if (!await installed.Task.ConfigureAwait(false)) return;
            pins.Clear(); // Worker exclusively owns every installed pin now.
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (!IsCurrentPreparationLocked(tabId, generation, requestCancellation)) return;
                plannedTabId = tabId;
                hasPlan = true;
                planEnabled = prepared.Options.Enabled;
                plannedEventCount = prepared.Events.Length;
                plannedPreloadCount = prepared.PreloadPaths.Length;
                plansPublished++;
                cursorResets++;
                isPreparing = false;
                status = rejected == 0
                    ? AvaloniaLocalizationCatalog.Format("Key 音计划已准备：{0} 个事件", prepared.Events.Length)
                    : AvaloniaLocalizationCatalog.Format("Key 音计划已准备：{0} 个事件，{1} 个采样预载失败", prepared.Events.Length, rejected);
                lastError = rejected == 0 ? null : keySounds.LastError;
            }
            RaiseStateChanged();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (gate)
            {
                if (!preloadCountersCommitted)
                    AddPreloadCountersLocked(attempted, accepted, rejected);
                if (!IsCurrentPreparationLocked(tabId, generation, requestCancellation)) return;
                EnqueueControlLocked(new SchedulerClear(generation));
                isPreparing = false;
                status = "Key 音计划准备已取消；旧计划不会恢复";
                lastError = null;
            }
            RaiseStateChanged();
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                if (!preloadCountersCommitted)
                    AddPreloadCountersLocked(attempted, accepted, rejected);
                if (!IsCurrentPreparationLocked(tabId, generation, requestCancellation)) return;
                if (pins.Count == 0)
                    EnqueueControlLocked(new SchedulerClear(generation));
                isPreparing = false;
                planFailures++;
                status = AvaloniaLocalizationCatalog.Format("Key 音计划准备失败：{0}", AvaloniaLocalizationCatalog.Text(exception.Message));
                lastError = exception;
            }
            RaiseStateChanged();
        }
        finally
        {
            if (preparationGateHeld) preparationGate.Release();
            foreach (var pin in pins) pin.Dispose();
            lock (gate)
            {
                if (ReferenceEquals(preparationCancellation, requestCancellation))
                    preparationCancellation = null;
            }
            requestCancellation.Dispose();
        }
    }

    private async Task ObservePreparationCompletionAsync(Task preparation)
    {
        try { await preparation.ConfigureAwait(false); }
        catch { /* PreparePlanAsync owns status publication. */ }
        finally
        {
            lock (gate) preparationTasks.Remove(preparation);
        }
    }

    public void InvalidatePlayback()
    {
        Guid? tab;
        lock (gate)
        {
            ThrowIfDisposedLocked();
            tab = plannedTabId ?? requestedTabId;
            pendingClockSample = null;
            status = "Key 音播放代际已推进";
        }
        InvalidateOutputs(tab);
        RaiseStateChanged();
    }

    public void Deactivate()
    {
        CancellationTokenSource? cancellation;
        Guid? tab;
        lock (gate)
        {
            ThrowIfDisposedLocked();
            if (isPreparing) plansSuperseded++;
            tab = plannedTabId ?? requestedTabId;
            var generation = ++planGeneration;
            cancellation = preparationCancellation;
            preparationCancellation = null;
            requestedTabId = null;
            plannedTabId = null;
            hasPlan = false;
            planEnabled = false;
            plannedEventCount = 0;
            plannedPreloadCount = 0;
            isPreparing = false;
            silentPlaying = false;
            silentBasePositionMilliseconds = 0;
            silentPlaybackRate = 1;
            silentStartedTimestamp = Stopwatch.GetTimestamp();
            latestPlaybackPositionMilliseconds = 0;
            latestPlaybackRate = 1;
            latestPlaybackSampleGeneration = generation;
            pendingClockSample = null;
            pendingReset = null;
            EnqueueControlLocked(new SchedulerClear(generation));
            status = "没有活动编辑器，Key 音计划已清除";
            lastError = null;
        }
        InvalidateOutputs(tab);
        CancelNoThrow(cancellation);
        RaiseStateChanged();
    }

    public int ResetPlaybackCursor(double positionMilliseconds, double playbackRate)
    {
        ValidateClock(positionMilliseconds, playbackRate);
        Guid? tab;
        lock (gate)
        {
            ThrowIfDisposedLocked();
            tab = plannedTabId ?? requestedTabId;
            silentBasePositionMilliseconds = positionMilliseconds;
            silentPlaybackRate = playbackRate;
            silentStartedTimestamp = Stopwatch.GetTimestamp();
            latestPlaybackPositionMilliseconds = positionMilliseconds;
            latestPlaybackRate = playbackRate;
            latestPlaybackSampleGeneration = planGeneration;
            pendingClockSample = null;
            pendingReset = new SchedulerReset(planGeneration, positionMilliseconds, playbackRate);
            SignalSchedulerLocked();
            cursorResets++;
            status = AvaloniaLocalizationCatalog.Format("Key 音游标已请求重置：{0:F3} ms", positionMilliseconds);
        }
        InvalidateOutputs(tab);
        RaiseStateChanged();
        // The cursor is worker-owned; returning zero preserves the legacy UI
        // signature without blocking the input thread on a 100k-note search.
        return 0;
    }

    /// <summary>
    /// Publishes one latest clock sample. Due/Accepted/Rejected are asynchronous
    /// completion counters and therefore remain zero in this immediate result.
    /// </summary>
    public WindowsKeySoundPlaybackPumpResult Pump()
    {
        Guid? tabId;
        long generation;
        bool silentWasPlaying;
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0) return WindowsKeySoundPlaybackPumpResult.Idle;
            pumpCalls++;
            tabId = requestedTabId;
            generation = planGeneration;
            silentWasPlaying = silentPlaying;
        }
        if (tabId == null) return WindowsKeySoundPlaybackPumpResult.Idle;

        var music = transport.CaptureKeySoundClock(tabId.Value);
        WindowsKeySoundPlaybackOutput output;
        double positionMilliseconds;
        double playbackRate;
        ClockSample? sample = null;
        if (music.IsCurrentTab && music.Snapshot is { IsPlaying: true } musicSnapshot)
        {
            output = WindowsKeySoundPlaybackOutput.SharedMusicClock;
            positionMilliseconds = Math.Max(0, musicSnapshot.Position.TotalMilliseconds);
            playbackRate = musicSnapshot.PlaybackRate;
            if (music.Lease != null)
                sample = new ClockSample(
                    generation,
                    tabId.Value,
                    output,
                    positionMilliseconds,
                    playbackRate,
                    music.OutputLatencyMilliseconds,
                    music.Lease,
                    default);
        }
        else if (silentWasPlaying)
        {
            lock (gate)
            {
                if (generation != planGeneration || requestedTabId != tabId || !silentPlaying)
                    return WindowsKeySoundPlaybackPumpResult.Idle;
                positionMilliseconds = GetSilentPositionLocked(Stopwatch.GetTimestamp());
                playbackRate = silentPlaybackRate;
            }
            output = WindowsKeySoundPlaybackOutput.SilentEditorClock;
            sample = new ClockSample(
                generation,
                tabId.Value,
                output,
                positionMilliseconds,
                playbackRate,
                WindowsKeySoundService.StandaloneOutputLatencyMilliseconds,
                null,
                keySounds.CaptureScheduledPlaybackEpoch());
        }
        else
        {
            return WindowsKeySoundPlaybackPumpResult.Idle;
        }

        lock (gate)
        {
            if (generation != planGeneration || requestedTabId != tabId)
                return WindowsKeySoundPlaybackPumpResult.Idle;
            latestPlaybackPositionMilliseconds = positionMilliseconds;
            latestPlaybackRate = playbackRate;
            latestPlaybackSampleGeneration = generation;
            if (sample != null)
            {
                if (pendingClockSample != null) clockSamplesReplaced++;
                pendingClockSample = sample;
                clockSamplesPublished++;
                SignalSchedulerLocked();
            }
        }
        return new WindowsKeySoundPlaybackPumpResult(
            output,
            positionMilliseconds,
            playbackRate,
            0,
            0,
            0);
    }

    internal Task WaitForSchedulerIdleAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            ThrowIfDisposedLocked();
            EnqueueControlLocked(new SchedulerBarrier(completion));
        }
        return completion.Task.WaitAsync(cancellationToken);
    }

    private async Task SchedulerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await schedulerSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                while (TryTakeSchedulerWork(out var work))
                {
                    if (work is SchedulerShutdown shutdown)
                    {
                        DisposeWorkerPlan();
                        shutdown.Completion.TrySetResult();
                        return;
                    }
                    try
                    {
                        switch (work)
                        {
                            case SchedulerClear clear:
                                DisposeWorkerPlan();
                                workerPlanGeneration = clear.Generation;
                                break;
                            case SchedulerInstall install:
                                ProcessInstall(install);
                                break;
                            case SchedulerReset reset:
                                if (workerCoordinator != null &&
                                    workerPlanGeneration == reset.Generation)
                                    workerCoordinator.ResetCursor(
                                        reset.PositionMilliseconds,
                                        reset.PlaybackRate);
                                break;
                            case SchedulerBarrier barrier:
                                barrier.Completion.TrySetResult();
                                break;
                            case ClockSample clock:
                                ProcessClockSample(clock);
                                break;
                        }
                    }
                    catch (Exception exception)
                    {
                        if (work is SchedulerInstall failedInstall)
                        {
                            foreach (var pin in failedInstall.Pins) pin.Dispose();
                            failedInstall.Completion.TrySetException(exception);
                        }
                        lock (gate)
                        {
                            lastError = exception;
                            status = AvaloniaLocalizationCatalog.Format("Key 音调度失败：{0}", AvaloniaLocalizationCatalog.Text(exception.Message));
                        }
                        RaiseStateChanged();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DisposeWorkerPlan();
        }
    }

    private bool TryTakeSchedulerWork(out object work)
    {
        lock (gate)
        {
            if (schedulerControls.Count > 0 &&
                schedulerControls.Peek() is not SchedulerBarrier)
            {
                work = schedulerControls.Dequeue();
                return true;
            }
            if (pendingReset != null)
            {
                work = pendingReset;
                pendingReset = null;
                return true;
            }
            if (pendingClockSample != null)
            {
                work = pendingClockSample;
                pendingClockSample = null;
                return true;
            }
            if (schedulerControls.Count > 0)
            {
                work = schedulerControls.Dequeue();
                return true;
            }
            schedulerSignaled = false;
            work = null!;
            return false;
        }
    }

    private void ProcessInstall(SchedulerInstall install)
    {
        var current = false;
        lock (gate)
            current = Volatile.Read(ref disposed) == 0 &&
                      planGeneration == install.Generation &&
                      requestedTabId == install.TabId;
        if (!current)
        {
            foreach (var pin in install.Pins) pin.Dispose();
            install.Completion.TrySetResult(false);
            return;
        }

        var previousPins = workerPlanPins;
        workerCoordinator = install.Coordinator;
        workerPlanPins = install.Pins;
        workerTabId = install.TabId;
        workerPlanGeneration = install.Generation;
        workerCoordinator.ResetCursor(
            install.PositionMilliseconds,
            install.PlaybackRate);
        foreach (var pin in previousPins) pin.Dispose();
        install.Completion.TrySetResult(true);
    }

    private void ProcessClockSample(ClockSample sample)
    {
        if (workerCoordinator == null || workerTabId != sample.TabId ||
            workerPlanGeneration != sample.Generation) return;
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0 ||
                planGeneration != sample.Generation ||
                requestedTabId != sample.TabId || isPreparing || !hasPlan) return;
        }

        // A stale lease/epoch must never advance the mutable cursor.
        if (sample.Output == WindowsKeySoundPlaybackOutput.SharedMusicClock)
        {
            if (sample.Lease?.IsCurrent != true) return;
        }
        else if (!keySounds.IsScheduledPlaybackEpochCurrent(sample.StandaloneEpoch))
        {
            return;
        }

        var due = workerCoordinator.TakeDue(
            sample.PositionMilliseconds,
            sample.PlaybackRate,
            sample.OutputLatencyMilliseconds);
        var result = sample.Output == WindowsKeySoundPlaybackOutput.SharedMusicClock
            ? sample.Lease!.TryPlayBatch(keySounds, due)
            : keySounds.PlayScheduledBatch(sample.StandaloneEpoch, due);
        lock (gate)
        {
            clockSamplesProcessed++;
            triggersDue += due.Length;
            triggersAccepted += result.AcceptedTriggers;
            triggersRejected += result.RejectedTriggers;
            submittedVoices += result.SubmittedVoices;
            if (sample.Output == WindowsKeySoundPlaybackOutput.SharedMusicClock)
            {
                sharedAccepted += result.AcceptedTriggers;
                sharedRejected += result.RejectedTriggers;
            }
            else
            {
                standaloneAccepted += result.AcceptedTriggers;
                standaloneRejected += result.RejectedTriggers;
            }
        }
    }

    private void DisposeWorkerPlan()
    {
        var pins = workerPlanPins;
        workerPlanPins = [];
        workerCoordinator = null;
        workerTabId = null;
        foreach (var pin in pins) pin.Dispose();
    }

    public async Task<bool> StartSilentAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token,
            cancellationToken);
        while (true)
        {
            Task preparation;
            lock (gate)
            {
                ThrowIfDisposedLocked();
                preparation = currentPreparation;
            }
            await preparation.WaitAsync(linked.Token).ConfigureAwait(false);
            lock (gate)
            {
                ThrowIfDisposedLocked();
                if (ReferenceEquals(preparation, currentPreparation)) break;
            }
        }

        long generation;
        bool enabled;
        lock (gate)
        {
            ThrowIfDisposedLocked();
            if (!hasPlan || plannedTabId == null)
            {
                status = "没有可启动的 Key 音计划";
                return false;
            }
            generation = planGeneration;
            enabled = planEnabled;
        }
        if (enabled && !await keySounds.WarmUpAsync(linked.Token).ConfigureAwait(false))
        {
            lock (gate)
            {
                if (generation == planGeneration)
                {
                    status = AvaloniaLocalizationCatalog.Format("Key 音独立输出预热失败：{0}", keySounds.LastError?.Message ?? AvaloniaLocalizationCatalog.Text("未知错误"));
                    lastError = keySounds.LastError;
                }
            }
            RaiseStateChanged();
            return false;
        }

        Guid? tab;
        lock (gate)
        {
            ThrowIfDisposedLocked();
            if (generation != planGeneration || !hasPlan || plannedTabId == null) return false;
            tab = plannedTabId;
            silentStartedTimestamp = Stopwatch.GetTimestamp();
            silentPlaying = true;
            pendingClockSample = null;
            pendingReset = new SchedulerReset(
                generation,
                silentBasePositionMilliseconds,
                silentPlaybackRate);
            SignalSchedulerLocked();
            cursorResets++;
            silentStarts++;
            status = "无音乐谱面 Key 音时钟正在播放";
            lastError = null;
        }
        InvalidateOutputs(tab);
        RaiseStateChanged();
        return true;
    }

    public void PauseSilent()
    {
        Guid? tab;
        lock (gate)
        {
            ThrowIfDisposedLocked();
            if (silentPlaying)
                silentBasePositionMilliseconds = GetSilentPositionLocked(Stopwatch.GetTimestamp());
            silentPlaying = false;
            tab = plannedTabId ?? requestedTabId;
            pendingClockSample = null;
            silentPauses++;
            status = "无音乐谱面 Key 音时钟已暂停";
        }
        InvalidateOutputs(tab);
        RaiseStateChanged();
    }

    public void StopSilent()
    {
        Guid? tab;
        lock (gate)
        {
            ThrowIfDisposedLocked();
            silentPlaying = false;
            silentBasePositionMilliseconds = 0;
            silentStartedTimestamp = Stopwatch.GetTimestamp();
            tab = plannedTabId ?? requestedTabId;
            pendingClockSample = null;
            pendingReset = new SchedulerReset(planGeneration, 0, silentPlaybackRate);
            SignalSchedulerLocked();
            cursorResets++;
            silentStops++;
            status = "无音乐谱面 Key 音时钟已停止";
        }
        InvalidateOutputs(tab);
        RaiseStateChanged();
    }

    public void SeekSilent(double positionMilliseconds)
    {
        if (!double.IsFinite(positionMilliseconds) || positionMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(positionMilliseconds));
        Guid? tab;
        lock (gate)
        {
            ThrowIfDisposedLocked();
            silentBasePositionMilliseconds = positionMilliseconds;
            silentStartedTimestamp = Stopwatch.GetTimestamp();
            tab = plannedTabId ?? requestedTabId;
            pendingClockSample = null;
            pendingReset = new SchedulerReset(
                planGeneration,
                positionMilliseconds,
                silentPlaybackRate);
            SignalSchedulerLocked();
            cursorResets++;
            silentSeeks++;
            status = AvaloniaLocalizationCatalog.Format("无音乐谱面 Key 音时钟已定位：{0:F3} ms", positionMilliseconds);
        }
        InvalidateOutputs(tab);
        RaiseStateChanged();
    }

    public void SetSilentRate(double playbackRate)
    {
        if (!double.IsFinite(playbackRate) || playbackRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(playbackRate));
        Guid? tab;
        lock (gate)
        {
            ThrowIfDisposedLocked();
            var timestamp = Stopwatch.GetTimestamp();
            var position = GetSilentPositionLocked(timestamp);
            silentBasePositionMilliseconds = position;
            silentStartedTimestamp = timestamp;
            silentPlaybackRate = playbackRate;
            tab = plannedTabId ?? requestedTabId;
            pendingClockSample = null;
            pendingReset = new SchedulerReset(planGeneration, position, playbackRate);
            SignalSchedulerLocked();
            cursorResets++;
            silentRateChanges++;
            status = AvaloniaLocalizationCatalog.Format("无音乐谱面 Key 音时钟速率：{0:F4}×", playbackRate);
        }
        InvalidateOutputs(tab);
        RaiseStateChanged();
    }

    public bool Preview(KeySoundPreset preset, string? customFile = null, float gain = 1)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        // Synchronous preview is IO-free. A null Custom selection has an
        // unambiguous WPF fallback; non-null paths must follow PreviewAsync's
        // cold-path existence/preload contract.
        if (preset == KeySoundPreset.Custom && string.IsNullOrWhiteSpace(customFile))
            preset = KeySoundPreset.Snare;
        var accepted = keySounds.Play(preset, customFile, gain);
        lock (gate)
        {
            if (accepted) previewAccepted++;
            else previewRejected++;
        }
        RaiseStateChanged();
        return accepted;
    }

    public async Task<bool> PreviewAsync(
        KeySoundPreset preset,
        string? customFile = null,
        float gain = 1,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token,
            cancellationToken);
        if (preset == KeySoundPreset.Custom)
        {
            var exists = false;
            if (!string.IsNullOrWhiteSpace(customFile))
            {
                try { exists = File.Exists(Path.GetFullPath(customFile)); }
                catch { exists = false; }
            }
            if (!exists)
            {
                preset = KeySoundPreset.Snare;
                customFile = null;
            }
            else if (!await keySounds.PreloadAsync(customFile, linked.Token).ConfigureAwait(false))
            {
                lock (gate) previewRejected++;
                RaiseStateChanged();
                return false;
            }
        }
        if (!await keySounds.WarmUpAsync(linked.Token).ConfigureAwait(false))
        {
            lock (gate) previewRejected++;
            RaiseStateChanged();
            return false;
        }
        return Preview(preset, customFile, gain);
    }

    private bool IsCurrentPreparationLocked(
        Guid tabId,
        long generation,
        CancellationTokenSource requestCancellation) =>
        Volatile.Read(ref disposed) == 0 && generation == planGeneration &&
        requestedTabId == tabId &&
        ReferenceEquals(preparationCancellation, requestCancellation);

    private void AddPreloadCountersLocked(long attempted, long accepted, long rejected)
    {
        preloadAttempts += attempted;
        preloadAccepted += accepted;
        preloadRejected += rejected;
    }

    private void InvalidateOutputs(Guid? tabId)
    {
        _ = keySounds.InvalidateScheduledPlaybackEpoch();
        if (tabId.HasValue) _ = transport.InvalidateKeySoundPlaybackEpoch(tabId.Value);
        lock (gate) playbackInvalidations++;
    }

    private void EnqueueControlLocked(SchedulerControl control)
    {
        schedulerControls.Enqueue(control);
        SignalSchedulerLocked();
    }

    private void SignalSchedulerLocked()
    {
        if (schedulerSignaled) return;
        schedulerSignaled = true;
        schedulerSignal.Release();
    }

    private double GetSilentPositionLocked(long timestamp)
    {
        if (!silentPlaying) return silentBasePositionMilliseconds;
        return silentBasePositionMilliseconds +
               Stopwatch.GetElapsedTime(silentStartedTimestamp, timestamp).TotalMilliseconds *
               silentPlaybackRate;
    }

    private WindowsKeySoundPlaybackRuntimeSnapshot CreateSnapshotLocked(long timestamp) => new(
        requestedTabId,
        plannedTabId,
        planGeneration,
        isPreparing,
        hasPlan,
        planEnabled,
        silentPlaying,
        GetSilentPositionLocked(timestamp),
        silentPlaybackRate,
        plannedEventCount,
        plannedPreloadCount,
        status,
        lastError,
        ownsKeySoundService,
        new WindowsKeySoundPlaybackRuntimeCounters(
            planRequests,
            plansPublished,
            plansSuperseded,
            planFailures,
            preloadAttempts,
            preloadAccepted,
            preloadRejected,
            pumpCalls,
            triggersDue,
            triggersAccepted,
            triggersRejected,
            sharedAccepted,
            sharedRejected,
            standaloneAccepted,
            standaloneRejected,
            playbackInvalidations,
            cursorResets,
            silentStarts,
            silentPauses,
            silentStops,
            silentSeeks,
            silentRateChanges,
            previewAccepted,
            previewRejected,
            clockSamplesPublished,
            clockSamplesReplaced,
            clockSamplesProcessed,
            submittedVoices));

    private static void CancelNoThrow(CancellationTokenSource? cancellation)
    {
        if (cancellation == null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void ThrowIfDisposedLocked() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    private static void ValidateClock(double positionMilliseconds, double playbackRate)
    {
        if (!double.IsFinite(positionMilliseconds) || positionMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(positionMilliseconds));
        if (!double.IsFinite(playbackRate) || playbackRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(playbackRate));
    }

    private void RaiseStateChanged()
    {
        var handlers = StateChanged;
        if (handlers == null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Task[] preparations;
        TaskCompletionSource shutdown;
        CancellationTokenSource? preparationToCancel;
        Guid? tab;
        lock (gate)
        {
            tab = plannedTabId ?? requestedTabId;
            planGeneration++;
            preparationToCancel = preparationCancellation;
            preparations = [.. preparationTasks];
            silentPlaying = false;
            hasPlan = false;
            plannedTabId = null;
            requestedTabId = null;
            isPreparing = false;
            pendingClockSample = null;
            pendingReset = null;
            shutdown = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EnqueueControlLocked(new SchedulerShutdown(shutdown));
            status = "Key 音运行时已释放";
        }
        lifetime.Cancel();
        CancelNoThrow(preparationToCancel);
        InvalidateOutputs(tab);
        try { await Task.WhenAll(preparations).ConfigureAwait(false); }
        catch { }
        await shutdown.Task.ConfigureAwait(false);
        schedulerCancellation.Cancel();
        try { await schedulerTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        schedulerCancellation.Dispose();
        schedulerSignal.Dispose();
        preparationGate.Dispose();
        lifetime.Dispose();
        if (ownsKeySoundService)
            await keySounds.DisposeAsync().ConfigureAwait(false);
    }

    private abstract record SchedulerControl;
    private sealed record SchedulerClear(long Generation) : SchedulerControl;
    private sealed record SchedulerInstall(
        long Generation,
        Guid TabId,
        WindowsKeySoundPlaybackCoordinator Coordinator,
        ImmutableArray<WindowsKeySoundPlanPreloadLease> Pins,
        double PositionMilliseconds,
        double PlaybackRate,
        TaskCompletionSource<bool> Completion) : SchedulerControl;
    private sealed record SchedulerReset(
        long Generation,
        double PositionMilliseconds,
        double PlaybackRate) : SchedulerControl;
    private sealed record SchedulerBarrier(TaskCompletionSource Completion) : SchedulerControl;
    private sealed record SchedulerShutdown(TaskCompletionSource Completion) : SchedulerControl;
    private sealed record ClockSample(
        long Generation,
        Guid TabId,
        WindowsKeySoundPlaybackOutput Output,
        double PositionMilliseconds,
        double PlaybackRate,
        double OutputLatencyMilliseconds,
        WindowsAudioKeySoundLease? Lease,
        AudioPlaybackEpochToken StandaloneEpoch);
}
