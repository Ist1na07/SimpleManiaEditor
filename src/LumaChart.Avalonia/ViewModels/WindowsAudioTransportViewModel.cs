using System.Diagnostics;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.ViewModels;

/// <summary>
/// Owns the Windows audio transport for the active editor tab.  The window only
/// binds controls to this adapter; decoder, device and seek serialization stay in
/// <see cref="WindowsAudioPlaybackService"/>.
/// </summary>
public sealed class WindowsAudioTransportViewModel : IAsyncDisposable
{
    internal static readonly TimeSpan DefaultDeviceRefreshDebounce =
        TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan DefaultAudioLoadTimeout =
        TimeSpan.FromSeconds(12);
    // An output that stops unexpectedly is rebuilt at once. When it stops
    // again at the same place — a decoder error at that sample stops it every
    // time — each further rebuild waits twice as long, and after the last one
    // the transport is parked Faulted instead of spinning the device forever.
    internal const int MaximumUnexpectedStopRecoveries = 4;
    internal static readonly TimeSpan UnexpectedStopRecoveryBaseDelay =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan UnexpectedStopSamePositionTolerance =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan UnexpectedStopStreakWindow = TimeSpan.FromSeconds(30);

    private readonly object gate = new();
    private readonly Func<WindowsAudioPlaybackService> serviceFactory;
    private readonly TimeSpan deviceRefreshDebounce;
    private readonly TimeSpan audioLoadTimeout;
    private WindowsAudioPlaybackService? service;
    private bool serviceReady;
    private CancellationTokenSource switchCancellation = new();
    private CancellationTokenSource? deviceRefreshCancellation;
    private Guid? activeTabId;
    private string? requestedAudioPath;
    private string status = "没有打开的谱面";
    private Exception? lastError;
    private long switchGeneration;
    private readonly HashSet<Task> switchTasks = [];
    private readonly HashSet<Task> retiredServiceTasks = [];
    private readonly HashSet<Task> deviceRefreshTasks = [];
    private Task? activeSwitchTask;
    private bool deviceRefreshResumeRequested;
    private WindowsAudioPlaybackService? unexpectedStopService;
    private TimeSpan unexpectedStopPosition;
    private long unexpectedStopTimestamp;
    private int unexpectedStopStreak;
    private int disposed;
    private float volume = .8f;
    private double playbackRate = 1;
    private bool preservePitch;

    public WindowsAudioTransportViewModel()
        : this(
            static () => new WindowsAudioPlaybackService(),
            DefaultDeviceRefreshDebounce,
            DefaultAudioLoadTimeout)
    {
    }

    internal WindowsAudioTransportViewModel(
        Func<WindowsAudioPlaybackService> serviceFactory,
        TimeSpan deviceRefreshDebounce,
        TimeSpan? audioLoadTimeout = null)
    {
        this.serviceFactory =
            serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
        if (deviceRefreshDebounce < TimeSpan.Zero ||
            deviceRefreshDebounce > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(
                nameof(deviceRefreshDebounce),
                "默认输出设备刷新合并窗口必须位于 0 到 5 秒之间。");
        this.deviceRefreshDebounce = deviceRefreshDebounce;
        this.audioLoadTimeout = audioLoadTimeout ?? DefaultAudioLoadTimeout;
        if (this.audioLoadTimeout < TimeSpan.FromMilliseconds(50) ||
            this.audioLoadTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(
                nameof(audioLoadTimeout),
                "音频载入超时必须位于 50 毫秒到 2 分钟之间。");
    }

    public event EventHandler? StateChanged;

    public Guid? ActiveTabId
    {
        get { lock (gate) return activeTabId; }
    }

    public string Status
    {
        get { lock (gate) return status; }
    }

    public Exception? LastError
    {
        get { lock (gate) return lastError; }
    }

    internal WindowsAudioDeviceDiagnostics DeviceDiagnostics
    {
        get
        {
            lock (gate) return service?.DeviceDiagnostics ?? default;
        }
    }

    public float Volume
    {
        get { lock (gate) return volume; }
    }

    public double PlaybackRate
    {
        get { lock (gate) return playbackRate; }
    }

    public bool PreservePitch
    {
        get { lock (gate) return preservePitch; }
    }

    public WindowsAudioPlaybackSnapshot? PlaybackSnapshot
    {
        get
        {
            lock (gate)
            {
                try { return service?.Snapshot; }
                catch (ObjectDisposedException) { return null; }
            }
        }
    }

    public bool IsLoaded
    {
        get
        {
            lock (gate)
            {
                try { return serviceReady && service?.IsLoaded == true; }
                catch (ObjectDisposedException) { return false; }
            }
        }
    }

    public bool IsPlaying
    {
        get
        {
            lock (gate)
            {
                try { return serviceReady && service?.IsPlaying == true; }
                catch (ObjectDisposedException) { return false; }
            }
        }
    }

    public void Activate(DesktopChartTab? tab) => _ = BeginAudioSwitch(tab, force: false);

    internal async Task<bool> EnsureAudioReadyAsync(
        DesktopChartTab tab,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var path = ResolveDocumentAudioPath(tab);
        lock (gate)
        {
            if (activeTabId != tab.Id ||
                !string.Equals(requestedAudioPath, path, StringComparison.OrdinalIgnoreCase) ||
                path == null)
                return false;
            try
            {
                if (serviceReady && service?.IsLoaded == true) return true;
            }
            catch (ObjectDisposedException)
            {
                // A stale endpoint is about to be replaced below.
            }
        }

        var transition = BeginAudioSwitch(tab, force: true);
        await transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            if (activeTabId != tab.Id ||
                !string.Equals(requestedAudioPath, path, StringComparison.OrdinalIgnoreCase))
                return false;
            try { return serviceReady && service?.IsLoaded == true; }
            catch (ObjectDisposedException) { return false; }
        }
    }

    private Task BeginAudioSwitch(DesktopChartTab? tab, bool force)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var path = ResolveDocumentAudioPath(tab);
        WindowsAudioPlaybackService? previous;
        CancellationToken cancellationToken;
        long generation;
        Task transition;
        TaskCompletionSource transitionStart;

        lock (gate)
        {
            if (!force &&
                activeTabId == tab?.Id &&
                string.Equals(requestedAudioPath, path, StringComparison.OrdinalIgnoreCase))
                return activeSwitchTask ?? Task.CompletedTask;

            activeTabId = tab?.Id;
            requestedAudioPath = path;
            generation = ++switchGeneration;
            CancelPendingDeviceRefreshLocked();
            switchCancellation.Cancel();
            switchCancellation.Dispose();
            switchCancellation = new CancellationTokenSource();
            cancellationToken = switchCancellation.Token;
            previous = service;
            service = null;
            serviceReady = false;
            lastError = null;
            status = tab == null
                ? "没有打开的谱面"
                : string.IsNullOrWhiteSpace(tab.Session.Document.Metadata.AudioFile)
                    ? "当前谱面没有引用音频"
                    : path == null
                        ? AvaloniaLocalizationCatalog.Format("未找到谱面引用的音频：{0}", tab.Session.Document.Metadata.AudioFile)
                        : force
                            ? AvaloniaLocalizationCatalog.Format("音频未就绪，客户端正在自动恢复：{0}", Path.GetFileName(path))
                            : AvaloniaLocalizationCatalog.Format("正在载入音频：{0}", Path.GetFileName(path));
            transitionStart = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            transition = SwitchAfterRegistrationAsync(
                transitionStart.Task,
                previous,
                path,
                generation,
                cancellationToken);
            switchTasks.Add(transition);
            activeSwitchTask = transition;
        }

        previous?.InvalidatePlaybackEpoch();
        if (previous != null) DetachService(previous);
        RaiseStateChanged();
        transitionStart.TrySetResult();
        _ = ObserveSwitchCompletionAsync(transition);
        return transition;
    }

    private async Task SwitchAfterRegistrationAsync(
        Task registration,
        WindowsAudioPlaybackService? previous,
        string? path,
        long generation,
        CancellationToken cancellationToken)
    {
        await registration.ConfigureAwait(false);
        await SwitchAsync(previous, path, generation, cancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveSwitchCompletionAsync(Task transition)
    {
        try { await transition.ConfigureAwait(false); }
        catch
        {
            // SwitchAsync owns user-visible error publication; this observer only
            // maintains the lifetime set used by DisposeAsync.
        }
        finally
        {
            lock (gate)
            {
                switchTasks.Remove(transition);
                if (ReferenceEquals(activeSwitchTask, transition)) activeSwitchTask = null;
            }
        }
    }

    public Task TogglePlayPauseAsync(CancellationToken cancellationToken = default) =>
        RunLoadedCommandAsync(
            current => current.IsPlaying
                ? current.PauseAsync(cancellationToken)
                : current.PlayAsync(cancellationToken),
            "播放 / 暂停音频失败");

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        RunLoadedCommandAsync(
            current => current.StopAsync(cancellationToken),
            "停止音频失败");

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) =>
        RunLoadedCommandAsync(
            current => current.SeekAsync(position, cancellationToken),
            "定位音频失败");

    public async Task SetVolumeAsync(float value, CancellationToken cancellationToken = default)
    {
        value = Math.Clamp(value, 0, 1);
        WindowsAudioPlaybackService? current;
        long generation;
        lock (gate)
        {
            volume = value;
            current = service;
            generation = switchGeneration;
        }
        if (current != null)
            await RunCommandAsync(
                () => current.SetVolumeAsync(value, cancellationToken),
                "设置音乐音量失败",
                current,
                generation).ConfigureAwait(false);
        else if (IsCurrentCommand(null, generation)) RaiseStateChanged();
    }

    public async Task SetPlaybackRateAsync(double value, CancellationToken cancellationToken = default)
    {
        value = Math.Round(Math.Clamp(value, .2, 1), 2);
        WindowsAudioPlaybackService? current;
        long generation;
        lock (gate)
        {
            playbackRate = value;
            current = service;
            generation = switchGeneration;
        }
        if (current != null)
            await RunCommandAsync(
                () => current.SetPlaybackRateAsync(value, cancellationToken),
                "设置播放倍速失败",
                current,
                generation).ConfigureAwait(false);
        else if (IsCurrentCommand(null, generation)) RaiseStateChanged();
    }

    public async Task SetPreservePitchAsync(bool value, CancellationToken cancellationToken = default)
    {
        WindowsAudioPlaybackService? current;
        long generation;
        lock (gate)
        {
            preservePitch = value;
            current = service;
            generation = switchGeneration;
        }
        if (current != null)
            await RunCommandAsync(
                () => current.SetPreservePitchAsync(value, cancellationToken),
                "切换变速不变调失败",
                current,
                generation).ConfigureAwait(false);
        else if (IsCurrentCommand(null, generation)) RaiseStateChanged();
    }

    public async Task RefreshDefaultOutputDeviceAsync(
        CancellationToken cancellationToken = default)
    {
        WindowsAudioPlaybackService? current;
        long generation;
        bool resumePlayback;
        lock (gate)
        {
            current = service;
            generation = switchGeneration;
            resumePlayback = deviceRefreshResumeRequested || current?.IsPlaying == true;
            CancelPendingDeviceRefreshLocked();
            // An explicit refresh is the user's way out of a parked fault; give
            // it the full automatic-recovery budget again.
            unexpectedStopStreak = 0;
        }
        if (current == null ||
            current.State is not (
                WindowsAudioPlaybackState.Stopped or
                WindowsAudioPlaybackState.Playing or
                WindowsAudioPlaybackState.Paused or
                WindowsAudioPlaybackState.Faulted))
        {
            PublishCommandError(
                "刷新默认输出设备失败",
                new InvalidOperationException("当前谱面没有可播放的音频。"),
                current,
                generation);
            return;
        }
        await RunCommandAsync(
                () => current.RefreshDefaultOutputDeviceAsync(
                    resumePlayback,
                    cancellationToken),
                "刷新默认输出设备失败",
                current,
                generation)
            .ConfigureAwait(false);
    }

    public static string? ResolveDocumentAudioPath(DesktopChartTab? tab)
    {
        if (tab == null) return null;
        return ChartAssetImportService.ResolveAssetPath(
            tab.WorkingPath,
            !string.IsNullOrWhiteSpace(tab.SourcePath)
                ? tab.SourcePath
                : tab.Session.Document.FilePath,
            tab.Session.Document.Metadata.AudioFile);
    }

    internal WindowsAudioKeySoundClockCapture CaptureKeySoundClock(Guid tabId)
    {
        WindowsAudioPlaybackService? current;
        long generation;
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0 || activeTabId != tabId)
                return default;
            current = service;
            generation = switchGeneration;
        }
        if (current == null)
            return new WindowsAudioKeySoundClockCapture(
                IsCurrentTab: true,
                Snapshot: null,
                OutputLatencyMilliseconds: 80,
                Lease: null);

        WindowsAudioPlaybackSnapshot currentSnapshot;
        AudioPlaybackEpochToken playbackEpoch;
        double outputLatencyMilliseconds;
        try
        {
            currentSnapshot = current.Snapshot;
            playbackEpoch = current.CapturePlaybackEpoch();
            outputLatencyMilliseconds = current.OutputLatencyMilliseconds;
        }
        catch (ObjectDisposedException)
        {
            return default;
        }

        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0 || activeTabId != tabId)
                return default;
            if (generation != switchGeneration || !ReferenceEquals(current, service))
                return new WindowsAudioKeySoundClockCapture(
                    IsCurrentTab: true,
                    currentSnapshot,
                    outputLatencyMilliseconds,
                    Lease: null);
            var lease = serviceReady &&
                        currentSnapshot.IsLoaded &&
                        currentSnapshot.IsPlaying &&
                        current.IsPlaybackEpochCurrent(playbackEpoch)
                ? new WindowsAudioKeySoundLease(
                    this,
                    current,
                    tabId,
                    generation,
                    playbackEpoch,
                    currentSnapshot,
                    outputLatencyMilliseconds)
                : null;
            return new WindowsAudioKeySoundClockCapture(
                IsCurrentTab: true,
                currentSnapshot,
                outputLatencyMilliseconds,
                lease);
        }
    }

    internal WindowsAudioKeySoundLease? CaptureKeySoundLease(Guid tabId) =>
        CaptureKeySoundClock(tabId).Lease;

    internal bool InvalidateKeySoundPlaybackEpoch(Guid expectedTabId)
    {
        WindowsAudioPlaybackService? current;
        long generation;
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0 || activeTabId != expectedTabId)
                return false;
            current = service;
            generation = switchGeneration;
        }
        if (current == null) return false;
        _ = current.InvalidatePlaybackEpoch();
        lock (gate)
            return Volatile.Read(ref disposed) == 0 &&
                   activeTabId == expectedTabId &&
                   generation == switchGeneration &&
                   ReferenceEquals(current, service);
    }

    internal bool IsKeySoundLeaseCurrent(
        WindowsAudioPlaybackService candidate,
        Guid tabId,
        long generation,
        AudioPlaybackEpochToken playbackEpoch)
    {
        lock (gate)
        {
            return Volatile.Read(ref disposed) == 0 &&
                   generation == switchGeneration &&
                   activeTabId == tabId &&
                   serviceReady &&
                   ReferenceEquals(candidate, service) &&
                   candidate.IsLoaded &&
                   candidate.IsPlaying &&
                   candidate.IsPlaybackEpochCurrent(playbackEpoch);
        }
    }

    private async Task SwitchAsync(
        WindowsAudioPlaybackService? previous,
        string? path,
        long generation,
        CancellationToken cancellationToken)
    {
        if (previous != null) RetireService(previous);

        if (path == null || cancellationToken.IsCancellationRequested) return;

        WindowsAudioPlaybackService? replacement = null;
        Task? initialization = null;
        try
        {
            replacement = serviceFactory()
                ?? throw new InvalidOperationException("音频播放服务工厂返回了空实例。");
            replacement.StateChanged += Service_StateChanged;
            replacement.PlaybackStopped += Service_PlaybackStopped;
            replacement.DefaultOutputDeviceChanged += Service_DefaultOutputDeviceChanged;
            lock (gate)
            {
                if (generation != switchGeneration || cancellationToken.IsCancellationRequested)
                    return;
                service = replacement;
            }

            float requestedVolume;
            double requestedRate;
            bool requestedPreservePitch;
            lock (gate)
            {
                requestedVolume = volume;
                requestedRate = playbackRate;
                requestedPreservePitch = preservePitch;
            }
            initialization = InitializeReplacementAsync(
                replacement,
                path,
                requestedVolume,
                requestedRate,
                requestedPreservePitch,
                cancellationToken);
            using var timeoutCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(audioLoadTimeout);
            try
            {
                await initialization.WaitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested &&
                timeoutCancellation.IsCancellationRequested)
            {
                throw new TimeoutException(
                    AvaloniaLocalizationCatalog.Format("音频载入超过 {0:0.#} 秒，已自动终止异常播放链。", audioLoadTimeout.TotalSeconds));
            }

            lock (gate)
            {
                if (generation != switchGeneration || cancellationToken.IsCancellationRequested)
                    return;
                serviceReady = true;
                status = AvaloniaLocalizationCatalog.Format("音频已载入：{0}", Path.GetFileName(path));
                lastError = null;
            }
            RaiseStateChanged();
            replacement = null;
        }
        catch (OperationCanceledException)
        {
            // A tab switch or close intentionally superseded this load.
        }
        catch (Exception exception)
        {
            var changed = false;
            lock (gate)
            {
                if (generation == switchGeneration)
                {
                    status = AvaloniaLocalizationCatalog.Format("音频载入失败：{0}", AvaloniaLocalizationCatalog.Text(exception.Message));
                    lastError = exception;
                    changed = true;
                }
            }
            if (changed) RaiseStateChanged();
        }
        finally
        {
            if (initialization is { IsCompleted: false })
                _ = ObserveAbandonedInitializationAsync(initialization);
            if (replacement != null)
            {
                lock (gate)
                {
                    if (ReferenceEquals(service, replacement))
                    {
                        service = null;
                        serviceReady = false;
                    }
                }
                DetachService(replacement);
                RetireService(replacement);
            }
        }
    }

    private static async Task InitializeReplacementAsync(
        WindowsAudioPlaybackService replacement,
        string path,
        float volume,
        double playbackRate,
        bool preservePitch,
        CancellationToken cancellationToken)
    {
        await replacement.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        await replacement.SetVolumeAsync(volume, cancellationToken).ConfigureAwait(false);
        await replacement.SetPlaybackRateAsync(playbackRate, cancellationToken).ConfigureAwait(false);
        await replacement.SetPreservePitchAsync(preservePitch, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ObserveAbandonedInitializationAsync(Task initialization)
    {
        try { await initialization.ConfigureAwait(false); }
        catch
        {
            // The authoritative switch already published cancellation/timeout.
        }
    }

    private void RetireService(WindowsAudioPlaybackService value)
    {
        Task retirement;
        try
        {
            retirement = value.DisposeAsync().AsTask();
        }
        catch
        {
            return;
        }
        lock (gate) retiredServiceTasks.Add(retirement);
        _ = ObserveRetiredServiceAsync(retirement);
    }

    private async Task ObserveRetiredServiceAsync(Task retirement)
    {
        try { await retirement.ConfigureAwait(false); }
        catch
        {
            // A retired chain can no longer overwrite or feed the active chain.
        }
        finally
        {
            lock (gate) retiredServiceTasks.Remove(retirement);
        }
    }

    private async Task RunLoadedCommandAsync(
        Func<WindowsAudioPlaybackService, Task> operation,
        string failurePrefix)
    {
        WindowsAudioPlaybackService? current;
        long generation;
        lock (gate)
        {
            current = serviceReady ? service : null;
            generation = switchGeneration;
        }
        if (current?.IsLoaded != true)
        {
            PublishCommandError(
                failurePrefix,
                new InvalidOperationException("当前谱面没有可播放的音频。"),
                current,
                generation);
            return;
        }
        await RunCommandAsync(
                () => operation(current),
                failurePrefix,
                current,
                generation)
            .ConfigureAwait(false);
    }

    private async Task RunCommandAsync(
        Func<Task> operation,
        string failurePrefix,
        WindowsAudioPlaybackService? commandService,
        long generation)
    {
        try
        {
            await operation().ConfigureAwait(false);
            var changed = false;
            lock (gate)
            {
                if (generation == switchGeneration && ReferenceEquals(commandService, service))
                {
                    lastError = null;
                    changed = true;
                }
            }
            if (changed) RaiseStateChanged();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            PublishCommandError(failurePrefix, exception, commandService, generation);
        }
    }

    private void PublishCommandError(
        string failurePrefix,
        Exception exception,
        WindowsAudioPlaybackService? commandService,
        long generation)
    {
        var changed = false;
        lock (gate)
        {
            if (generation != switchGeneration || !ReferenceEquals(commandService, service)) return;
            status = $"{failurePrefix}：{exception.Message}";
            lastError = exception;
            changed = true;
        }
        if (changed) RaiseStateChanged();
    }

    private bool IsCurrentCommand(
        WindowsAudioPlaybackService? commandService,
        long generation)
    {
        lock (gate)
            return generation == switchGeneration && ReferenceEquals(commandService, service);
    }

    private void Service_StateChanged(object? sender, WindowsAudioPlaybackStateChangedEventArgs eventArgs)
    {
        lock (gate)
        {
            if (!ReferenceEquals(sender, service)) return;
            if (eventArgs.Current.LastError != null)
            {
                lastError = eventArgs.Current.LastError;
                status = AvaloniaLocalizationCatalog.Format("音频错误：{0}", AvaloniaLocalizationCatalog.Text(eventArgs.Current.LastError.Message));
            }
            else
            {
                status = eventArgs.Current.State switch
                {
                    WindowsAudioPlaybackState.Loading => AvaloniaLocalizationCatalog.Format("正在载入音频：{0}", Path.GetFileName(eventArgs.Current.SourcePath)),
                    WindowsAudioPlaybackState.Playing => "正在播放",
                    WindowsAudioPlaybackState.Paused => "已暂停",
                    WindowsAudioPlaybackState.Stopped => "已停止",
                    _ => status
                };
            }
        }
        RaiseStateChanged();
    }

    private void Service_PlaybackStopped(object? sender, EventArgs eventArgs)
    {
        lock (gate)
        {
            if (!ReferenceEquals(sender, service)) return;
            status = "播放已结束";
        }
        RaiseStateChanged();
    }

    private void Service_DefaultOutputDeviceChanged(object? sender, AudioOutputDeviceChangedEventArgs eventArgs)
    {
        if (sender is not WindowsAudioPlaybackService current) return;
        var unexpectedStop =
            eventArgs.Reason == AudioOutputDeviceChangeReason.UnexpectedPlaybackStop;
        // Read before taking the gate: this runs on the output's render
        // thread, and the service clock never throws or blocks.
        var stopPosition = unexpectedStop ? current.Position : TimeSpan.Zero;
        CancellationTokenSource cancellation;
        Task refreshTask;
        lock (gate)
        {
            if (!ReferenceEquals(current, service)) return;
            deviceRefreshCancellation?.Cancel();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                switchCancellation.Token);
            deviceRefreshCancellation = cancellation;
            var recoveryAttempt = unexpectedStop
                ? RegisterUnexpectedStopLocked(current, stopPosition)
                : 0;
            // A different endpoint is a fresh start for a failure that was
            // tied to the old one.
            if (!unexpectedStop) unexpectedStopStreak = 0;
            if (recoveryAttempt > MaximumUnexpectedStopRecoveries)
            {
                var error = new InvalidOperationException(
                    "音频输出在同一位置反复意外中断，已停止自动恢复。",
                    eventArgs.Error);
                deviceRefreshResumeRequested = false;
                lastError = error;
                status = AvaloniaLocalizationCatalog.Format("音频错误：{0}", AvaloniaLocalizationCatalog.Text(error.Message));
                refreshTask = FaultAfterRepeatedUnexpectedStopsAsync(
                    current,
                    switchGeneration,
                    cancellation,
                    error);
            }
            else
            {
                deviceRefreshResumeRequested |= eventArgs.WasPlaying || current.IsPlaying;
                status = unexpectedStop
                    ? "音频输出意外中断，正在恢复播放"
                    : "默认输出设备已变化，正在重建播放链";
                refreshTask = RecoverDefaultOutputDeviceAfterDelayAsync(
                    current,
                    switchGeneration,
                    cancellation,
                    eventArgs.Reason,
                    UnexpectedStopRecoveryDelay(recoveryAttempt));
            }
            deviceRefreshTasks.Add(refreshTask);
        }
        RaiseStateChanged();
        _ = ObserveDeviceRefreshCompletionAsync(refreshTask, cancellation);
    }

    /// <summary>
    /// Counts consecutive unexpected stops of one service at one place. Stops
    /// further apart than the tolerance, or after a long quiet spell, start a
    /// new streak: playback got past whatever stopped it.
    /// </summary>
    private int RegisterUnexpectedStopLocked(
        WindowsAudioPlaybackService current,
        TimeSpan position)
    {
        var now = Stopwatch.GetTimestamp();
        var sameFailure =
            unexpectedStopStreak > 0 &&
            ReferenceEquals(unexpectedStopService, current) &&
            (position - unexpectedStopPosition).Duration() <= UnexpectedStopSamePositionTolerance &&
            Stopwatch.GetElapsedTime(unexpectedStopTimestamp, now) <= UnexpectedStopStreakWindow;
        unexpectedStopService = current;
        unexpectedStopPosition = position;
        unexpectedStopTimestamp = now;
        unexpectedStopStreak = sameFailure ? unexpectedStopStreak + 1 : 1;
        return unexpectedStopStreak;
    }

    internal static TimeSpan UnexpectedStopRecoveryDelay(int recoveryAttempt) =>
        recoveryAttempt <= 1
            ? TimeSpan.Zero
            : UnexpectedStopRecoveryBaseDelay *
              Math.Pow(2, Math.Min(recoveryAttempt, MaximumUnexpectedStopRecoveries) - 2);

    private async Task FaultAfterRepeatedUnexpectedStopsAsync(
        WindowsAudioPlaybackService expectedService,
        long expectedGeneration,
        CancellationTokenSource cancellation,
        Exception error)
    {
        try
        {
            lock (gate)
            {
                if (Volatile.Read(ref disposed) != 0 ||
                    expectedGeneration != switchGeneration ||
                    !ReferenceEquals(expectedService, service) ||
                    !ReferenceEquals(cancellation, deviceRefreshCancellation))
                    return;
                deviceRefreshCancellation = null;
            }

            await expectedService.FaultAfterRepeatedOutputStopsAsync(error, cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer endpoint notification, tab switch, or disposal superseded
            // parking this transport.
        }
        catch (ObjectDisposedException)
        {
            // The service was retired while the fault was being published.
        }
        catch (Exception exception)
        {
            var changed = false;
            lock (gate)
            {
                if (expectedGeneration == switchGeneration &&
                    ReferenceEquals(expectedService, service))
                {
                    lastError = exception;
                    changed = true;
                }
            }
            if (changed) RaiseStateChanged();
        }
    }

    private async Task RecoverDefaultOutputDeviceAfterDelayAsync(
        WindowsAudioPlaybackService expectedService,
        long expectedGeneration,
        CancellationTokenSource cancellation,
        AudioOutputDeviceChangeReason reason,
        TimeSpan retryDelay)
    {
        try
        {
            if (reason == AudioOutputDeviceChangeReason.DefaultEndpointChanged)
                await Task.Delay(deviceRefreshDebounce, cancellation.Token).ConfigureAwait(false);
            else if (retryDelay > TimeSpan.Zero)
                await Task.Delay(retryDelay, cancellation.Token).ConfigureAwait(false);
            bool resumePlayback;
            lock (gate)
            {
                if (Volatile.Read(ref disposed) != 0 ||
                    expectedGeneration != switchGeneration ||
                    !ReferenceEquals(expectedService, service) ||
                    !ReferenceEquals(cancellation, deviceRefreshCancellation))
                    return;
                resumePlayback = deviceRefreshResumeRequested || expectedService.IsPlaying;
                deviceRefreshResumeRequested = false;
                deviceRefreshCancellation = null;
            }

            await expectedService.RefreshDefaultOutputDeviceAsync(
                    resumePlayback,
                    cancellation.Token)
                .ConfigureAwait(false);
            var changed = false;
            lock (gate)
            {
                if (expectedGeneration == switchGeneration &&
                    ReferenceEquals(expectedService, service))
                {
                    status = reason == AudioOutputDeviceChangeReason.UnexpectedPlaybackStop
                        ? resumePlayback
                            ? "音频输出已恢复，播放已继续"
                            : "音频输出已恢复"
                        : resumePlayback
                            ? "默认播放设备已切换，播放已继续"
                            : "默认播放设备已切换";
                    lastError = null;
                    changed = true;
                }
            }
            if (changed) RaiseStateChanged();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer endpoint notification, tab switch, or disposal superseded
            // this recovery attempt.
        }
        catch (Exception exception)
        {
            var changed = false;
            lock (gate)
            {
                if (expectedGeneration == switchGeneration &&
                    ReferenceEquals(expectedService, service))
                {
                    status = "默认播放设备暂不可用，请检查系统输出设备";
                    lastError = exception;
                    changed = true;
                }
            }
            if (changed) RaiseStateChanged();
        }
    }

    private async Task ObserveDeviceRefreshCompletionAsync(
        Task refreshTask,
        CancellationTokenSource cancellation)
    {
        try
        {
            await refreshTask.ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                deviceRefreshTasks.Remove(refreshTask);
                if (ReferenceEquals(deviceRefreshCancellation, cancellation))
                    deviceRefreshCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private void CancelPendingDeviceRefreshLocked()
    {
        deviceRefreshResumeRequested = false;
        var cancellation = deviceRefreshCancellation;
        deviceRefreshCancellation = null;
        cancellation?.Cancel();
    }

    private void DetachService(WindowsAudioPlaybackService value)
    {
        value.StateChanged -= Service_StateChanged;
        value.PlaybackStopped -= Service_PlaybackStopped;
        value.DefaultOutputDeviceChanged -= Service_DefaultOutputDeviceChanged;
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        WindowsAudioPlaybackService? current;
        Task[] pendingSwitches;
        Task[] pendingRetirements;
        Task[] pendingDeviceRefreshes;
        lock (gate)
        {
            switchGeneration++;
            CancelPendingDeviceRefreshLocked();
            switchCancellation.Cancel();
            current = service;
            service = null;
            serviceReady = false;
            status = "音频服务已释放";
            pendingSwitches = [.. switchTasks];
            pendingRetirements = [.. retiredServiceTasks];
            pendingDeviceRefreshes = [.. deviceRefreshTasks];
        }
        if (current != null)
        {
            DetachService(current);
            try { await current.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception)
            {
                lock (gate)
                {
                    status = AvaloniaLocalizationCatalog.Format("音频服务释放失败：{0}", AvaloniaLocalizationCatalog.Text(exception.Message));
                    lastError = exception;
                }
            }
        }
        try
        {
            await Task.WhenAll(
                    pendingSwitches
                        .Concat(pendingRetirements)
                        .Concat(pendingDeviceRefreshes))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            lock (gate)
            {
                status = AvaloniaLocalizationCatalog.Format("音频切换任务结束失败：{0}", AvaloniaLocalizationCatalog.Text(exception.Message));
                lastError = exception;
            }
        }
        finally
        {
            switchCancellation.Dispose();
        }
    }
}

/// <summary>
/// Short-lived, tab- and transport-generation-bound Key 音 output capability.
/// It deliberately does not expose the mutable playback service.
/// </summary>
internal sealed class WindowsAudioKeySoundLease(
    WindowsAudioTransportViewModel owner,
    WindowsAudioPlaybackService service,
    Guid tabId,
    long switchGeneration,
    AudioPlaybackEpochToken playbackEpoch,
    WindowsAudioPlaybackSnapshot snapshot,
    double outputLatencyMilliseconds)
{
    public bool IsCurrent => owner.IsKeySoundLeaseCurrent(
        service,
        tabId,
        switchGeneration,
        playbackEpoch);

    public WindowsAudioPlaybackSnapshot Snapshot { get; } = snapshot;
    public double OutputLatencyMilliseconds { get; } = outputLatencyMilliseconds;

    public bool TryPlay(
        WindowsKeySoundService keySounds,
        WindowsKeySoundPlaybackTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(keySounds);
        return IsCurrent && keySounds.PlayThrough(
            service,
            playbackEpoch,
            trigger.Preset,
            trigger.CustomFilePath,
            trigger.Gain,
            trigger.DelayMilliseconds);
    }

    public WindowsKeySoundBatchPlaybackResult TryPlayBatch(
        WindowsKeySoundService keySounds,
        System.Collections.Immutable.ImmutableArray<WindowsKeySoundPlaybackTrigger> triggers)
    {
        ArgumentNullException.ThrowIfNull(keySounds);
        return !IsCurrent
            ? new WindowsKeySoundBatchPlaybackResult(0, triggers.Length, 0)
            : keySounds.PlayThroughBatch(service, playbackEpoch, triggers);
    }
}

internal readonly record struct WindowsAudioKeySoundClockCapture(
    bool IsCurrentTab,
    WindowsAudioPlaybackSnapshot? Snapshot,
    double OutputLatencyMilliseconds,
    WindowsAudioKeySoundLease? Lease);
