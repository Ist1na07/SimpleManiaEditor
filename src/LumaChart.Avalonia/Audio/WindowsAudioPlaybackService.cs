using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using NAudio.Wave;

namespace LumaChart.Avalonia.Audio;

public enum WindowsAudioPlaybackState
{
    Empty,
    Loading,
    Stopped,
    Playing,
    Paused,
    Faulted,
    Disposed
}

public sealed record WindowsAudioPlaybackSnapshot(
    WindowsAudioPlaybackState State,
    string? SourcePath,
    TimeSpan Position,
    TimeSpan Duration,
    double PlaybackRate,
    bool PreservePitch,
    float Volume,
    Exception? LastError)
{
    public bool IsLoaded => State is WindowsAudioPlaybackState.Stopped or
        WindowsAudioPlaybackState.Playing or WindowsAudioPlaybackState.Paused;

    public bool IsPlaying => State == WindowsAudioPlaybackState.Playing;
}

public sealed class WindowsAudioPlaybackStateChangedEventArgs(
    WindowsAudioPlaybackSnapshot previous,
    WindowsAudioPlaybackSnapshot current) : EventArgs
{
    public WindowsAudioPlaybackSnapshot Previous { get; } = previous;
    public WindowsAudioPlaybackSnapshot Current { get; } = current;
}

internal readonly record struct WindowsAudioPlaybackClockSample(
    bool IsLoaded,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration);

/// <summary>
/// UI-independent Windows transport. Decoder/output mutations run behind an
/// asynchronous serialization gate, and rapid timeline seeks collapse into one
/// backend rebuild after a short quiet window.
/// </summary>
public sealed class WindowsAudioPlaybackService : IDisposable, IAsyncDisposable
{
    public static readonly TimeSpan DefaultSeekCoalescingWindow = TimeSpan.FromMilliseconds(24);
    private static readonly TimeSpan DisposeOperationWaitTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly AudioPlaybackEpoch playbackEpoch = new();
    private readonly ConcurrentQueue<Action> pendingNotifications = new();
    private readonly object seekGate = new();
    private readonly object snapshotGate = new();
    private readonly TimeSpan seekCoalescingWindow;
    private readonly Func<IWindowsAudioDeviceEngine> engineFactory;
    private readonly Dictionary<IWindowsAudioDeviceEngine, long> engineGenerations =
        new(ReferenceEqualityComparer.Instance);
    private IWindowsAudioDeviceEngine? engine;
    private IWindowsAudioDeviceEngine? stagedEngine;
    private WindowsAudioPlaybackSnapshot snapshot = new(
        WindowsAudioPlaybackState.Empty,
        null,
        TimeSpan.Zero,
        TimeSpan.Zero,
        1,
        false,
        1,
        null);
    private CancellationTokenSource? seekDelayCancellation;
    private List<TaskCompletionSource> seekWaiters = [];
    private TimeSpan pendingSeekPosition;
    private long pendingSeekLoadGeneration;
    private long seekGeneration;
    private long loadGeneration;
    private long engineGenerationSerial;
    private long activeEngineGeneration;
    private int drainingNotifications;
    private WindowsAudioPlaybackState recoverablePlaybackState = WindowsAudioPlaybackState.Empty;
    private WindowsAudioPlaybackSnapshot stableBeforeLoad = new(
        WindowsAudioPlaybackState.Empty,
        null,
        TimeSpan.Zero,
        TimeSpan.Zero,
        1,
        false,
        1,
        null);
    private int disposed;

    internal Func<CancellationToken, Task>? BeforeSeekExecutionAsync { get; set; }

    public WindowsAudioPlaybackService(TimeSpan? seekCoalescingWindow = null)
        : this(() => new WindowsAudioDeviceEngine(), seekCoalescingWindow)
    {
    }

    internal WindowsAudioPlaybackService(
        double requestedOutputLatencyMilliseconds,
        TimeSpan? seekCoalescingWindow = null)
        : this(
            () => new WindowsAudioDeviceEngine(requestedOutputLatencyMilliseconds),
            seekCoalescingWindow)
    {
    }

    internal WindowsAudioPlaybackService(
        Func<IWindowsAudioDeviceEngine> engineFactory,
        TimeSpan? seekCoalescingWindow = null)
    {
        this.engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
        this.seekCoalescingWindow = seekCoalescingWindow ?? DefaultSeekCoalescingWindow;
        if (this.seekCoalescingWindow < TimeSpan.Zero ||
            this.seekCoalescingWindow > TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(
                nameof(seekCoalescingWindow),
                "Seek 合并窗口必须位于 0 到 1 秒之间。");
    }

    public WindowsAudioPlaybackSnapshot Snapshot
    {
        get
        {
            var current = Volatile.Read(ref snapshot);
            var activeEngine = Volatile.Read(ref engine);
            if (activeEngine == null || !current.IsLoaded) return current;
            try
            {
                return current with
                {
                    Position = activeEngine.Position,
                    Duration = activeEngine.Duration
                };
            }
            catch (Exception exception) when (!IsCriticalException(exception))
            {
                // An engine retired underneath this read can fail with more
                // than ObjectDisposedException (a disposed VorbisWaveReader
                // answers with a NullReferenceException). The UI thread polls
                // here, so any of them falls back to the committed snapshot.
                return Volatile.Read(ref snapshot);
            }
        }
    }

    internal WindowsAudioDeviceDiagnostics DeviceDiagnostics =>
        Volatile.Read(ref engine)?.Diagnostics ?? default;

    public WindowsAudioPlaybackState State => Volatile.Read(ref snapshot).State;
    public bool IsLoaded => Volatile.Read(ref snapshot).IsLoaded;
    public bool IsPlaying => Volatile.Read(ref snapshot).IsPlaying;
    public TimeSpan Position => SampleClock().Position;
    public TimeSpan Duration => SampleClock().Duration;
    public double PlaybackRate => Volatile.Read(ref snapshot).PlaybackRate;
    public bool PreservePitch => Volatile.Read(ref snapshot).PreservePitch;
    public float Volume => Volatile.Read(ref snapshot).Volume;
    public double OutputLatencyMilliseconds =>
        Volatile.Read(ref engine)?.OutputLatencyMilliseconds ?? 80;

    internal WindowsAudioPlaybackClockSample SampleClock()
    {
        var current = Volatile.Read(ref snapshot);
        var activeEngine = Volatile.Read(ref engine);
        if (activeEngine == null || !current.IsLoaded)
            return new WindowsAudioPlaybackClockSample(
                current.IsLoaded,
                current.IsPlaying,
                current.Position,
                current.Duration);
        try
        {
            return new WindowsAudioPlaybackClockSample(
                true,
                current.IsPlaying,
                activeEngine.Position,
                activeEngine.Duration);
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            current = Volatile.Read(ref snapshot);
            return new WindowsAudioPlaybackClockSample(
                current.IsLoaded,
                current.IsPlaying,
                current.Position,
                current.Duration);
        }
    }

    private static bool IsCriticalException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    /// <remarks>
    /// Events are raised on the audio worker or device callback thread. A UI
    /// consumer must marshal them through its own dispatcher.
    /// </remarks>
    public event EventHandler<WindowsAudioPlaybackStateChangedEventArgs>? StateChanged;
    public event EventHandler? PlaybackStopped;
    public event EventHandler<AudioOutputDeviceChangedEventArgs>? DefaultOutputDeviceChanged;

    public async Task LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("找不到音频文件。", fullPath);
        _ = playbackEpoch.Advance();
        var generation = BeginLoad(fullPath);
        CancelPendingSeeks(new OperationCanceledException("载入新音频时取消了尚未提交的 Seek。"));

        try
        {
            await LoadAndCommitAsync(fullPath, generation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RestoreAfterCanceledLoad(generation);
            throw;
        }
        catch (Exception exception)
        {
            PublishForLoad(generation, current => current with
            {
                State = WindowsAudioPlaybackState.Faulted,
                SourcePath = fullPath,
                Position = TimeSpan.Zero,
                Duration = TimeSpan.Zero,
                LastError = exception
            });
            throw;
        }
    }

    public async Task PlayAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        _ = playbackEpoch.Advance();
        await RunSerializedSnapshotTransitionAsync(
                activeEngine =>
                {
                    var duration = activeEngine.Duration;
                    var position = activeEngine.Position;
                    if (duration > TimeSpan.Zero &&
                        position >= duration - TimeSpan.FromMilliseconds(20))
                    {
                        activeEngine.Seek(TimeSpan.Zero);
                    }

                    activeEngine.Play();
                },
                (current, activeEngine) => !current.IsLoaded
                    ? null
                    : current with
                    {
                        State = WindowsAudioPlaybackState.Playing,
                        Position = activeEngine.Position,
                        Duration = activeEngine.Duration,
                        LastError = null
                    },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        _ = playbackEpoch.Advance();
        CancelPendingSeeks(new OperationCanceledException(
            "暂停播放时取消了尚未提交的 Seek。"));
        await RunSerializedSnapshotTransitionAsync(
                activeEngine => activeEngine.Pause(),
                (current, activeEngine) => !current.IsLoaded
                    ? null
                    : current with
                    {
                        State = WindowsAudioPlaybackState.Paused,
                        Position = activeEngine.Position,
                        Duration = activeEngine.Duration,
                        LastError = null
                    },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        _ = playbackEpoch.Advance();
        CancelPendingSeeks(new OperationCanceledException("停止播放时取消了尚未提交的 Seek。"));
        await RunSerializedSnapshotTransitionAsync(
                activeEngine => activeEngine.Stop(),
                (current, activeEngine) => !current.IsLoaded
                    ? null
                    : current with
                    {
                        State = WindowsAudioPlaybackState.Stopped,
                        Position = activeEngine.Position,
                        Duration = activeEngine.Duration,
                        LastError = null
                    },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Requests a seek. Calls arriving inside the coalescing window share one
    /// decoder/output rebuild and all complete when the newest position is applied.
    /// </summary>
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        _ = playbackEpoch.Advance();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource delayCancellation;
        var sourceGeneration = CurrentLoadGeneration;
        long generation;
        lock (seekGate)
        {
            ThrowIfDisposed();
            pendingSeekPosition = position;
            pendingSeekLoadGeneration = sourceGeneration;
            seekWaiters.Add(completion);
            seekDelayCancellation?.Cancel();
            seekDelayCancellation?.Dispose();
            seekDelayCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            delayCancellation = seekDelayCancellation;
            generation = ++seekGeneration;
        }

        _ = CommitSeekAfterDelayAsync(generation, delayCancellation);
        return cancellationToken.CanBeCanceled
            ? completion.Task.WaitAsync(cancellationToken)
            : completion.Task;
    }

    public async Task SetPlaybackRateAsync(
        double playbackRate,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!double.IsFinite(playbackRate)) throw new ArgumentOutOfRangeException(nameof(playbackRate));
        cancellationToken.ThrowIfCancellationRequested();
        playbackRate = Math.Clamp(playbackRate, .2, 1);
        _ = playbackEpoch.Advance();
        await RunSerializedSnapshotTransitionAsync(
                activeEngine => activeEngine.PlaybackRate = playbackRate,
                (current, activeEngine) => current with
                {
                    PlaybackRate = playbackRate,
                    Position = current.IsLoaded ? activeEngine.Position : current.Position,
                    Duration = current.IsLoaded ? activeEngine.Duration : current.Duration,
                    LastError = null
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetPreservePitchAsync(
        bool preservePitch,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        _ = playbackEpoch.Advance();
        var operationLoadGeneration = CurrentLoadGeneration;
        var stateAtOperation = Snapshot;
        var intendedState = RecoveryStateFor(stateAtOperation.State);
        var position = stateAtOperation.Position;
        var duration = stateAtOperation.Duration;
        ExceptionDispatchInfo? failure = null;
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var activeEngine = GetOrCreateEngine();
            try
            {
                await Task.Run(
                    () =>
                    {
                        stateAtOperation = Snapshot;
                        intendedState = RecoveryStateFor(stateAtOperation.State);
                        activeEngine.PreservePitch = preservePitch;
                        if (activeEngine.IsLoaded && !activeEngine.HasOutput)
                            throw new InvalidOperationException("音频输出重建失败，当前没有可用输出。");
                        if (intendedState == WindowsAudioPlaybackState.Playing &&
                            !activeEngine.IsPlaying)
                            activeEngine.Play();
                        if (intendedState == WindowsAudioPlaybackState.Playing &&
                            !activeEngine.IsPlaying)
                            throw new InvalidOperationException("音频输出已重建，但无法恢复播放状态。");
                        position = activeEngine.Position;
                        duration = activeEngine.Duration;
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException and not ObjectDisposedException)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
                lock (snapshotGate)
                {
                    QueueSnapshotChange(CommitRecoverableFaultLocked(
                        operationLoadGeneration,
                        stateAtOperation,
                        exception));
                }
            }

            if (failure == null)
            {
                lock (snapshotGate)
                {
                    QueueSnapshotChange(CommitRecoveredLocked(
                        operationLoadGeneration,
                        current => current with
                        {
                            State = intendedState,
                            PreservePitch = preservePitch,
                            Position = position,
                            Duration = duration,
                            LastError = null
                        }));
                }
            }
        }
        finally
        {
            operationGate.Release();
        }
        DrainNotifications();
        failure?.Throw();
    }

    public async Task SetVolumeAsync(float volume, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(volume)) throw new ArgumentOutOfRangeException(nameof(volume));
        cancellationToken.ThrowIfCancellationRequested();
        volume = Math.Clamp(volume, 0, 1);
        await RunSerializedSnapshotTransitionAsync(
                activeEngine => activeEngine.Volume = volume,
                (current, _) => current with { Volume = volume, LastError = null },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RefreshDefaultOutputDeviceAsync(
        bool resumePlayback,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        _ = playbackEpoch.Advance();
        var operationLoadGeneration = CurrentLoadGeneration;
        WindowsAudioPlaybackSnapshot? stateAtRefresh = null;
        var shouldResume = false;
        TimeSpan position = TimeSpan.Zero;
        TimeSpan duration = TimeSpan.Zero;
        ExceptionDispatchInfo? failure = null;
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var activeEngine = GetOrCreateEngine();
            try
            {
                await Task.Run(
                    () =>
                    {
                        stateAtRefresh = Snapshot;
                        var intendedState = RecoveryStateFor(stateAtRefresh.State);
                        shouldResume = intendedState == WindowsAudioPlaybackState.Playing &&
                            resumePlayback;
                        activeEngine.RefreshDefaultOutputDevice(shouldResume);
                        if (activeEngine.IsLoaded && !activeEngine.HasOutput)
                            throw new InvalidOperationException("刷新音频设备后没有可用输出。");
                        if (shouldResume && !activeEngine.IsPlaying)
                            throw new InvalidOperationException("刷新音频设备后无法恢复播放状态。");
                        position = activeEngine.Position;
                        duration = activeEngine.Duration;
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException and not ObjectDisposedException)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
                lock (snapshotGate)
                {
                    QueueSnapshotChange(CommitRecoverableFaultLocked(
                        operationLoadGeneration,
                        stateAtRefresh ?? snapshot,
                        exception));
                }
            }

            if (failure == null)
            {
                var previous = stateAtRefresh ?? Snapshot;
                var intended = RecoveryStateFor(previous.State);
                var resultingState = intended switch
                {
                    WindowsAudioPlaybackState.Stopped => WindowsAudioPlaybackState.Stopped,
                    WindowsAudioPlaybackState.Paused => WindowsAudioPlaybackState.Paused,
                    WindowsAudioPlaybackState.Playing when shouldResume => WindowsAudioPlaybackState.Playing,
                    WindowsAudioPlaybackState.Playing => WindowsAudioPlaybackState.Paused,
                    _ => intended
                };
                lock (snapshotGate)
                {
                    QueueSnapshotChange(CommitRecoveredLocked(
                        operationLoadGeneration,
                        value => value with
                        {
                            State = resultingState,
                            Position = position,
                            Duration = duration,
                            LastError = null
                        }));
                }
            }
        }
        finally
        {
            operationGate.Release();
        }
        DrainNotifications();
        failure?.Throw();
    }

    /// <summary>
    /// Parks a transport whose output keeps stopping at the same place, so that
    /// automatic recovery stops rebuilding it. The engine is paused, which also
    /// clears the Playing state a dead WASAPI render thread leaves behind, and
    /// the snapshot turns Faulted with <paramref name="error"/>. The next
    /// endpoint change or an explicit refresh recovers it into Paused rather
    /// than straight back into the playback that kept failing.
    /// </summary>
    internal async Task FaultAfterRepeatedOutputStopsAsync(
        Exception error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var operationLoadGeneration = CurrentLoadGeneration;
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            // A pause, stop or seek from the user already broke the loop.
            if (State is not (WindowsAudioPlaybackState.Playing or WindowsAudioPlaybackState.Faulted))
                return;
            _ = playbackEpoch.Advance();
            var activeEngine = Volatile.Read(ref engine);
            if (activeEngine != null)
            {
                try
                {
                    await Task.Run(activeEngine.Pause, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not ObjectDisposedException)
                {
                    // The fault is published regardless: an endpoint that cannot
                    // even pause has nothing left to stop.
                }
            }

            var stateAtFault = Snapshot;
            lock (snapshotGate)
            {
                if (Volatile.Read(ref disposed) == 0 &&
                    operationLoadGeneration == loadGeneration &&
                    snapshot.State != WindowsAudioPlaybackState.Loading)
                {
                    recoverablePlaybackState = WindowsAudioPlaybackState.Paused;
                    QueueSnapshotChange(CommitSnapshotLocked(current => current with
                    {
                        State = WindowsAudioPlaybackState.Faulted,
                        Position = stateAtFault.Position,
                        Duration = stateAtFault.Duration,
                        LastError = error
                    }));
                }
            }
        }
        finally
        {
            operationGate.Release();
        }
        DrainNotifications();
    }

    internal AudioPlaybackEpochToken CapturePlaybackEpoch() => playbackEpoch.Capture();

    internal AudioPlaybackEpochToken InvalidatePlaybackEpoch() => playbackEpoch.Advance();

    internal bool IsPlaybackEpochCurrent(AudioPlaybackEpochToken token) =>
        playbackEpoch.IsCurrent(token);

    // Compatibility boundary for immediate overlays. Playback schedulers must
    // capture a token before building their batch and use the strict overload.
    internal bool TryPlayOverlay(ISampleProvider provider) =>
        TryPlayOverlay(provider, CapturePlaybackEpoch());

    internal bool TryPlayOverlay(
        ISampleProvider provider,
        AudioPlaybackEpochToken playbackEpochToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (Volatile.Read(ref disposed) != 0 ||
            !playbackEpoch.IsCurrent(playbackEpochToken)) return false;
        try
        {
            if (!operationGate.Wait(0)) return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        try
        {
            return Volatile.Read(ref disposed) == 0 &&
                playbackEpoch.IsCurrent(playbackEpochToken) &&
                Volatile.Read(ref engine)?.TryPlayOverlay(provider, playbackEpochToken) == true;
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal long CurrentLoadGeneration
    {
        get
        {
            lock (snapshotGate) return loadGeneration;
        }
    }

    private async Task CommitSeekAfterDelayAsync(
        long generation,
        CancellationTokenSource delayCancellation)
    {
        try
        {
            await Task.Delay(seekCoalescingWindow, delayCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            delayCancellation.Dispose();
            return;
        }

        TimeSpan position;
        long sourceGeneration;
        List<TaskCompletionSource> waiters;
        lock (seekGate)
        {
            if (generation != seekGeneration ||
                !ReferenceEquals(delayCancellation, seekDelayCancellation))
            {
                delayCancellation.Dispose();
                return;
            }
            position = pendingSeekPosition;
            sourceGeneration = pendingSeekLoadGeneration;
            waiters = seekWaiters;
            seekWaiters = [];
            seekDelayCancellation = null;
        }

        try
        {
            await RunSeekSerializedAsync(
                    position,
                    generation,
                    sourceGeneration,
                    lifetime.Token)
                .ConfigureAwait(false);
            foreach (var waiter in waiters) waiter.TrySetResult();
        }
        catch (OperationCanceledException exception)
        {
            foreach (var waiter in waiters) waiter.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            foreach (var waiter in waiters) waiter.TrySetException(exception);
        }
        finally
        {
            delayCancellation.Dispose();
        }
    }

    private async Task RunSeekSerializedAsync(
        TimeSpan position,
        long expectedSeekGeneration,
        long expectedLoadGeneration,
        CancellationToken cancellationToken)
    {
        ThrowIfSeekStale(expectedSeekGeneration, expectedLoadGeneration);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ExceptionDispatchInfo? failure = null;
        try
        {
            ThrowIfDisposed();
            ThrowIfSeekStale(expectedSeekGeneration, expectedLoadGeneration);
            var hook = BeforeSeekExecutionAsync;
            if (hook != null) await hook(cancellationToken).ConfigureAwait(false);
            ThrowIfSeekStale(expectedSeekGeneration, expectedLoadGeneration);
            IWindowsAudioDeviceEngine? activeEngine = null;
            try
            {
                await Task.Run(() =>
                {
                    ThrowIfSeekStale(expectedSeekGeneration, expectedLoadGeneration);
                    activeEngine = GetOrCreateEngine();
                    ThrowIfSeekStale(expectedSeekGeneration, expectedLoadGeneration);
                    activeEngine.Seek(position);
                    ThrowIfSeekStale(expectedSeekGeneration, expectedLoadGeneration);
                }, CancellationToken.None).ConfigureAwait(false);

                lock (snapshotGate)
                {
                    lock (seekGate)
                    {
                        if (Volatile.Read(ref disposed) != 0 ||
                            expectedLoadGeneration != loadGeneration ||
                            expectedSeekGeneration != seekGeneration)
                            throw new OperationCanceledException(
                                "Seek 已被更新的谱面或定位请求取代。");
                    }
                    QueueSnapshotChange(CommitSnapshotLocked(current =>
                        !current.IsLoaded || activeEngine == null
                            ? null
                            : current with
                            {
                                Position = activeEngine.Position,
                                Duration = activeEngine.Duration,
                                LastError = null
                            }));
                }
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException and not ObjectDisposedException)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
                lock (snapshotGate)
                {
                    QueueSnapshotChange(CommitSnapshotLocked(current => current with
                    {
                        State = WindowsAudioPlaybackState.Faulted,
                        LastError = exception
                    }));
                }
            }
        }
        finally
        {
            operationGate.Release();
        }
        DrainNotifications();
        failure?.Throw();
    }

    private void ThrowIfSeekStale(long expectedSeekGeneration, long expectedLoadGeneration)
    {
        long currentLoadGeneration;
        lock (snapshotGate) currentLoadGeneration = loadGeneration;
        long currentSeekGeneration;
        lock (seekGate) currentSeekGeneration = seekGeneration;
        if (Volatile.Read(ref disposed) != 0 ||
            expectedLoadGeneration != currentLoadGeneration ||
            expectedSeekGeneration != currentSeekGeneration)
            throw new OperationCanceledException("Seek 已被更新的谱面或定位请求取代。");
    }

    private async Task RunSerializedSnapshotTransitionAsync(
        Action<IWindowsAudioDeviceEngine> operation,
        Func<
            WindowsAudioPlaybackSnapshot,
            IWindowsAudioDeviceEngine,
            WindowsAudioPlaybackSnapshot?> update,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var activeEngine = GetOrCreateEngine();
            await Task.Run(() => operation(activeEngine), CancellationToken.None)
                .ConfigureAwait(false);
            lock (snapshotGate)
            {
                ThrowIfDisposed();
                QueueSnapshotChange(CommitSnapshotLocked(current => update(current, activeEngine)));
            }
        }
        finally
        {
            operationGate.Release();
        }
        DrainNotifications();
    }

    private long BeginLoad(string fullPath)
    {
        WindowsAudioPlaybackSnapshot previous;
        WindowsAudioPlaybackSnapshot current;
        long generation;
        lock (snapshotGate)
        {
            ThrowIfDisposed();
            previous = snapshot;
            if (previous.State != WindowsAudioPlaybackState.Loading)
                stableBeforeLoad = previous;
            generation = ++loadGeneration;
            current = previous with
            {
                State = WindowsAudioPlaybackState.Loading,
                SourcePath = fullPath,
                Position = TimeSpan.Zero,
                Duration = TimeSpan.Zero,
                LastError = null
            };
            Volatile.Write(ref snapshot, current);
        }
        RaiseSnapshotChanged(previous, current);
        return generation;
    }

    private async Task LoadAndCommitAsync(
        string fullPath,
        long generation,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var change = await Task.Run(() =>
            {
                IWindowsAudioDeviceEngine? replacement = null;
                try
                {
                    var settings = Volatile.Read(ref snapshot);
                    replacement = CreateConfiguredEngine(settings);
                    lock (snapshotGate)
                    {
                        ThrowIfDisposed();
                        stagedEngine = replacement;
                    }
                    replacement.Load(fullPath);
                    cancellationToken.ThrowIfCancellationRequested();

                    IWindowsAudioDeviceEngine? replaced;
                    WindowsAudioPlaybackSnapshot previous;
                    WindowsAudioPlaybackSnapshot current;
                    lock (snapshotGate)
                    {
                        if (Volatile.Read(ref disposed) != 0 || generation != loadGeneration)
                            throw new OperationCanceledException("音频载入已被更新请求取代。");
                        replaced = engine;
                        if (replaced != null) DetachEngineEventsLocked(replaced);
                        AttachEngineEventsLocked(replacement);
                        Volatile.Write(ref engine, replacement);
                        if (ReferenceEquals(stagedEngine, replacement)) stagedEngine = null;
                        replacement = null;
                        previous = snapshot;
                        current = previous with
                        {
                            State = WindowsAudioPlaybackState.Stopped,
                            SourcePath = fullPath,
                            Position = TimeSpan.Zero,
                            Duration = engine!.Duration,
                            LastError = null
                        };
                        stableBeforeLoad = current;
                        recoverablePlaybackState = WindowsAudioPlaybackState.Stopped;
                        Volatile.Write(ref snapshot, current);
                    }

                    if (replaced != null)
                    {
                        try
                        {
                            replaced.Dispose();
                        }
                        catch
                        {
                            // The replacement is already committed. A failing old
                            // endpoint teardown must not turn the new load Faulted.
                        }
                    }
                    return new SnapshotChange(previous, current);
                }
                finally
                {
                    lock (snapshotGate)
                    {
                        if (ReferenceEquals(stagedEngine, replacement)) stagedEngine = null;
                    }
                    replacement?.Dispose();
                }
            }, CancellationToken.None).ConfigureAwait(false);
            QueueSnapshotChange(change);
        }
        finally
        {
            operationGate.Release();
        }
        DrainNotifications();
    }

    private void RestoreAfterCanceledLoad(long generation)
    {
        WindowsAudioPlaybackSnapshot previous;
        WindowsAudioPlaybackSnapshot current;
        lock (snapshotGate)
        {
            if (generation != loadGeneration || Volatile.Read(ref disposed) != 0) return;
            previous = snapshot;
            current = stableBeforeLoad;
            Volatile.Write(ref snapshot, current);
        }
        RaiseSnapshotChanged(previous, current);
    }

    private void PublishForLoad(
        long generation,
        Func<WindowsAudioPlaybackSnapshot, WindowsAudioPlaybackSnapshot> update)
    {
        WindowsAudioPlaybackSnapshot previous;
        WindowsAudioPlaybackSnapshot current;
        lock (snapshotGate)
        {
            if (generation != loadGeneration || Volatile.Read(ref disposed) != 0) return;
            previous = snapshot;
            current = update(previous);
            Volatile.Write(ref snapshot, current);
        }
        RaiseSnapshotChanged(previous, current);
    }

    private WindowsAudioPlaybackState RecoveryStateFor(WindowsAudioPlaybackState state)
    {
        if (state is WindowsAudioPlaybackState.Stopped or
            WindowsAudioPlaybackState.Paused or WindowsAudioPlaybackState.Playing)
            return state;
        lock (snapshotGate)
        {
            return recoverablePlaybackState is WindowsAudioPlaybackState.Stopped or
                WindowsAudioPlaybackState.Paused or WindowsAudioPlaybackState.Playing
                ? recoverablePlaybackState
                : state;
        }
    }

    private SnapshotChange? CommitRecoverableFaultLocked(
        long expectedLoadGeneration,
        WindowsAudioPlaybackSnapshot operationSnapshot,
        Exception exception)
    {
        if (Volatile.Read(ref disposed) != 0 ||
            expectedLoadGeneration != loadGeneration ||
            snapshot.State == WindowsAudioPlaybackState.Loading) return null;
        var intended = operationSnapshot.State == WindowsAudioPlaybackState.Faulted
            ? recoverablePlaybackState
            : operationSnapshot.State;
        if (intended is WindowsAudioPlaybackState.Stopped or
            WindowsAudioPlaybackState.Paused or WindowsAudioPlaybackState.Playing)
            recoverablePlaybackState = intended;
        return CommitSnapshotLocked(previous => previous with
        {
            State = WindowsAudioPlaybackState.Faulted,
            Position = operationSnapshot.Position,
            Duration = operationSnapshot.Duration,
            LastError = exception
        });
    }

    private SnapshotChange? CommitRecoveredLocked(
        long expectedLoadGeneration,
        Func<WindowsAudioPlaybackSnapshot, WindowsAudioPlaybackSnapshot> update)
    {
        if (Volatile.Read(ref disposed) != 0 ||
            expectedLoadGeneration != loadGeneration ||
            snapshot.State == WindowsAudioPlaybackState.Loading) return null;
        return CommitSnapshotLocked(current => update(current));
    }

    private IWindowsAudioDeviceEngine GetOrCreateEngine()
    {
        var current = engine;
        if (current != null) return current;
        current = CreateConfiguredEngine(Volatile.Read(ref snapshot));
        lock (snapshotGate)
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                AttachEngineEventsLocked(current);
                Volatile.Write(ref engine, current);
                return current;
            }
        }

        // DisposeAsync has already collected the engines it will release; one
        // published now would never be disposed.
        current.Dispose();
        throw new ObjectDisposedException(nameof(WindowsAudioPlaybackService));
    }

    private IWindowsAudioDeviceEngine CreateConfiguredEngine(
        WindowsAudioPlaybackSnapshot settings)
    {
        var created = engineFactory();
        try
        {
            created.Volume = settings.Volume;
            created.PlaybackRate = settings.PlaybackRate;
            created.PreservePitch = settings.PreservePitch;
            return created;
        }
        catch
        {
            created.Dispose();
            throw;
        }
    }

    private void OnEnginePlaybackStopped(object? sender, EventArgs e)
    {
        if (sender is not IWindowsAudioDeviceEngine callbackEngine) return;
        WindowsAudioPlaybackSnapshot previous;
        WindowsAudioPlaybackSnapshot current;
        lock (snapshotGate)
        {
            if (!IsCurrentEngineCallbackLocked(callbackEngine) || !snapshot.IsLoaded) return;
            _ = playbackEpoch.Advance();
            previous = snapshot;
            TimeSpan position;
            TimeSpan duration;
            try
            {
                position = callbackEngine.Position;
                duration = callbackEngine.Duration;
            }
            catch
            {
                position = previous.Position;
                duration = previous.Duration;
            }
            current = previous with
            {
                State = WindowsAudioPlaybackState.Stopped,
                Position = position,
                Duration = duration,
                LastError = null
            };
            recoverablePlaybackState = WindowsAudioPlaybackState.Stopped;
            Volatile.Write(ref snapshot, current);
            QueueSnapshotChange(new SnapshotChange(previous, current));
            QueueNotification(() => SafeRaise(PlaybackStopped, EventArgs.Empty));
        }
        DrainNotifications();
    }

    private void OnDefaultOutputDeviceChanged(
        object? sender,
        AudioOutputDeviceChangedEventArgs eventArgs)
    {
        if (sender is not IWindowsAudioDeviceEngine callbackEngine) return;
        lock (snapshotGate)
        {
            if (!IsCurrentEngineCallbackLocked(callbackEngine)) return;
            _ = playbackEpoch.Advance();
            QueueNotification(() => SafeRaise(DefaultOutputDeviceChanged, eventArgs));
        }
        DrainNotifications();
    }

    private void AttachEngineEventsLocked(IWindowsAudioDeviceEngine target)
    {
        var generation = ++engineGenerationSerial;
        engineGenerations[target] = generation;
        activeEngineGeneration = generation;
        target.PlaybackStopped += OnEnginePlaybackStopped;
        target.DefaultOutputDeviceChanged += OnDefaultOutputDeviceChanged;
    }

    private void DetachEngineEventsLocked(IWindowsAudioDeviceEngine target)
    {
        target.PlaybackStopped -= OnEnginePlaybackStopped;
        target.DefaultOutputDeviceChanged -= OnDefaultOutputDeviceChanged;
        engineGenerations.Remove(target);
        if (ReferenceEquals(target, engine)) activeEngineGeneration = 0;
    }

    private bool IsCurrentEngineCallbackLocked(IWindowsAudioDeviceEngine callbackEngine) =>
        Volatile.Read(ref disposed) == 0 &&
        ReferenceEquals(callbackEngine, engine) &&
        engineGenerations.TryGetValue(callbackEngine, out var callbackGeneration) &&
        callbackGeneration == activeEngineGeneration;

    private SnapshotChange? CommitSnapshotLocked(
        Func<WindowsAudioPlaybackSnapshot, WindowsAudioPlaybackSnapshot?> update)
    {
        if (Volatile.Read(ref disposed) != 0) return null;
        var previous = snapshot;
        var current = update(previous);
        if (current == null || current == previous) return null;
        if (previous.State == WindowsAudioPlaybackState.Loading &&
            current.State == WindowsAudioPlaybackState.Loading)
        {
            stableBeforeLoad = stableBeforeLoad with
            {
                PlaybackRate = current.PlaybackRate,
                PreservePitch = current.PreservePitch,
                Volume = current.Volume
            };
        }
        if (current.State is WindowsAudioPlaybackState.Stopped or
            WindowsAudioPlaybackState.Paused or WindowsAudioPlaybackState.Playing)
            recoverablePlaybackState = current.State;
        Volatile.Write(ref snapshot, current);
        return new SnapshotChange(previous, current);
    }

    private void RaiseSnapshotChanged(
        WindowsAudioPlaybackSnapshot previous,
        WindowsAudioPlaybackSnapshot current)
    {
        QueueSnapshotChange(new SnapshotChange(previous, current));
        DrainNotifications();
    }

    private void QueueSnapshotChange(SnapshotChange? change)
    {
        if (change == null || change.Current == change.Previous) return;
        pendingNotifications.Enqueue(() => SafeRaise(
            StateChanged,
            new WindowsAudioPlaybackStateChangedEventArgs(change.Previous, change.Current)));
    }

    private void QueueNotification(Action notification) =>
        pendingNotifications.Enqueue(notification);

    private void DrainNotifications()
    {
        while (Interlocked.CompareExchange(ref drainingNotifications, 1, 0) == 0)
        {
            try
            {
                while (pendingNotifications.TryDequeue(out var notification)) notification();
            }
            finally
            {
                Volatile.Write(ref drainingNotifications, 0);
            }

            if (pendingNotifications.IsEmpty) return;
        }
    }

    private void CancelPendingSeeks(Exception exception)
    {
        List<TaskCompletionSource> waiters;
        lock (seekGate)
        {
            seekGeneration++;
            seekDelayCancellation?.Cancel();
            seekDelayCancellation?.Dispose();
            seekDelayCancellation = null;
            waiters = seekWaiters;
            seekWaiters = [];
        }
        foreach (var waiter in waiters) waiter.TrySetException(exception);
    }

    private void SafeRaise<TEventArgs>(EventHandler<TEventArgs>? handlers, TEventArgs args)
        where TEventArgs : EventArgs
    {
        if (handlers == null) return;
        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // Transport integrity cannot depend on a consumer event handler.
            }
        }
    }

    private void SafeRaise(EventHandler? handlers, EventArgs args)
    {
        if (handlers == null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // Transport integrity cannot depend on a consumer event handler.
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref disposed) != 0,
        this);

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        _ = playbackEpoch.Advance();
        lifetime.Cancel();
        CancelPendingSeeks(new ObjectDisposedException(nameof(WindowsAudioPlaybackService)));
        IWindowsAudioDeviceEngine? activeEngine;
        IWindowsAudioDeviceEngine? pendingEngine;
        lock (snapshotGate)
        {
            loadGeneration++;
            activeEngine = engine;
            pendingEngine = stagedEngine;
            if (activeEngine != null) DetachEngineEventsLocked(activeEngine);
            Volatile.Write(ref engine, null);
            stagedEngine = null;
            activeEngineGeneration = 0;
            engineGenerations.Clear();
        }
        QueueDisposedSnapshot();
        DrainNotifications();

        // Stop the endpoint before waiting for the serialized operation gate.
        // A decoder/driver call can otherwise hold that gate while the old
        // endpoint keeps producing audio after the UI has switched tabs.
        var engineDisposals = new HashSet<IWindowsAudioDeviceEngine>(
            ReferenceEqualityComparer.Instance);
        if (activeEngine != null) engineDisposals.Add(activeEngine);
        if (pendingEngine != null) engineDisposals.Add(pendingEngine);
        var engineDisposal = Task.WhenAll(engineDisposals.Select(
            value => Task.Run(value.Dispose)));
        ExceptionDispatchInfo? disposalFailure = null;
        try
        {
            await engineDisposal.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            disposalFailure = ExceptionDispatchInfo.Capture(exception);
        }

        // In-flight operations see the canceled lifetime or their disposed
        // engine and leave promptly. One stuck inside a driver call must not
        // hold a tab switch or a window close forever; it still releases the
        // gate when it returns, and the gate is disposed after that instead of
        // underneath its Release.
        if (await operationGate.WaitAsync(DisposeOperationWaitTimeout).ConfigureAwait(false))
        {
            ReleaseGateAfterDispose();
        }
        else
        {
            _ = operationGate.WaitAsync().ContinueWith(
                _ => ReleaseGateAfterDispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        GC.SuppressFinalize(this);
        disposalFailure?.Throw();
    }

    private void ReleaseGateAfterDispose()
    {
        operationGate.Release();
        operationGate.Dispose();
        lifetime.Dispose();
    }

    private void QueueDisposedSnapshot()
    {
        WindowsAudioPlaybackSnapshot previous;
        WindowsAudioPlaybackSnapshot current;
        lock (snapshotGate)
        {
            previous = snapshot;
            current = previous with
            {
                State = WindowsAudioPlaybackState.Disposed,
                Position = TimeSpan.Zero,
                Duration = TimeSpan.Zero,
                LastError = null
            };
            Volatile.Write(ref snapshot, current);
            QueueSnapshotChange(new SnapshotChange(previous, current));
        }
    }

    private sealed record SnapshotChange(
        WindowsAudioPlaybackSnapshot Previous,
        WindowsAudioPlaybackSnapshot Current);
}
