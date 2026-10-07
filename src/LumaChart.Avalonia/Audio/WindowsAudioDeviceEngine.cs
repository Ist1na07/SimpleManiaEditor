using LumaChart.Avalonia.Infrastructure;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundTouch;
using SoundTouch.Net.NAudioSupport;

namespace LumaChart.Avalonia.Audio;

internal interface IWindowsAudioDeviceEngine : IDisposable
{
    bool IsLoaded { get; }
    bool IsPlaying { get; }
    bool HasOutput { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    double OutputLatencyMilliseconds { get; }
    double PlaybackRate { get; set; }
    bool PreservePitch { get; set; }
    float Volume { get; set; }
    WindowsAudioDeviceDiagnostics Diagnostics => default;
    event EventHandler? PlaybackStopped;
    event EventHandler<AudioOutputDeviceChangedEventArgs>? DefaultOutputDeviceChanged;
    void Load(string path);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);
    void RefreshDefaultOutputDevice(bool resumePlayback);
    bool TryPlayOverlay(
        ISampleProvider provider,
        AudioPlaybackEpochToken playbackEpoch);
}

internal readonly record struct WindowsAudioDeviceDiagnostics(
    long UnexpectedPlaybackStopCount,
    long TransientMusicZeroCount,
    string? LastUnexpectedPlaybackStopError);

/// <summary>
/// Windows output implementation. All mutating calls are serialized by
/// <see cref="WindowsAudioPlaybackService"/>; the render callback only consumes
/// sample providers and concurrent overlay descriptors. The engine also
/// serializes them itself behind <see cref="operationLock"/>, because the
/// service disposes an engine without waiting for its gate and an output's
/// render thread reports stops on its own schedule.
/// </summary>
internal sealed class WindowsAudioDeviceEngine : IWindowsAudioDeviceEngine
{
    internal const double DefaultRequestedOutputLatencyMilliseconds = 40;
    internal const double MinimumRequestedOutputLatencyMilliseconds = 20;
    internal const double MaximumRequestedOutputLatencyMilliseconds = 250;
    private static readonly TimeSpan TelemetryProbeInterval = TimeSpan.FromMilliseconds(40);
    // How long Dispose waits for an operation already inside a decoder or
    // driver call. Past it the operation tears the playback down itself when
    // it returns, so nothing it created outlives the engine.
    private static readonly TimeSpan DisposeOperationWaitTimeout = TimeSpan.FromSeconds(2);
    // WASAPI's device clock can remain at its anchor for a few periods after
    // Play() returns. Do not let the UI clock run ahead during that startup
    // window; otherwise the first real device position sample makes playback
    // visibly jump backwards.
    private static readonly TimeSpan AudioClockStartupGracePeriod =
        TimeSpan.FromMilliseconds(500);
    private static readonly long AudioClockProbeInterval = Math.Max(1, Stopwatch.Frequency / 125);
    // Half a probe interval, so that an admitted reader which lands on the
    // probe's own throttle and leaves with the cached value has not spent the
    // window: the throttle above stays the one limit that decides the cadence.
    private static readonly long AudioClockStartupAdmissionInterval =
        Math.Max(1, AudioClockProbeInterval / 2);
    private static readonly FieldInfo? WasapiAudioClientField = typeof(WasapiOut).GetField(
        "audioClient", BindingFlags.Instance | BindingFlags.NonPublic);

    private IWavePlayer? output;
    private TransportEnvelopeSampleProvider? transportEnvelope;
    private WaveStream? source;
    private RateAdjustingSampleProvider? rateProvider;
    private WindowsPhaseVocoderTimeStretchSampleProvider? phaseVocoderProvider;
    private SoundTouchWaveProvider[] soundTouchProviders = [];
    private VolumeSampleProvider? localVolumeProvider;
    private volatile QueuedOverlayMixerSampleProvider? outputMixer;
    private double playbackRate = 1;
    private bool preservePitch;
    private float volume = 1;
    private readonly Stopwatch positionClock = new();
    private long positionAnchorTicks;
    private double measuredOutputLatencyMilliseconds = DefaultRequestedOutputLatencyMilliseconds;
    private AudioClockClient? audioClockClient;
    private ulong audioClockAnchor;
    private ulong audioClockFrequency;
    private bool audioClockArmed;
    private int audioClockHasAdvanced;
    private readonly object audioClockGate = new();
    private long audioClockArmTimestamp;
    // The last device-clock probe: the elapsed device time and when it was
    // taken, published as one object. Kept in two fields, a probe landing
    // between a reader's two reads paired the new elapsed time with the old
    // timestamp and flashed the playhead one probe interval ahead for a
    // single frame.
    private AudioClockProbeSample? lastAudioClockProbe;
    private long lastAudioClockProbeAttemptTimestamp;
    private long unexpectedPlaybackStopCount;
    private long transientMusicZeroCount;
    private string? lastUnexpectedPlaybackStopError;
    // Endpoint enumeration is a WASAPI concept. Platforms without it simply
    // never raise a default-device change, which the engine already tolerates.
    private readonly MMDeviceEnumerator? endpointEnumerator;
    private readonly OutputDeviceNotificationClient endpointNotifications;
    private readonly CancellationTokenSource telemetryCancellation = new();
    private readonly Task telemetryTask;
    private readonly double requestedOutputLatencyMilliseconds;
    private long telemetryComProbeCount;
    private int disposed;
    private readonly object operationLock = new();
    private readonly ConcurrentQueue<StoppedOutput> stoppedOutputs = new();
    // Cached at load: a disposed VorbisWaveReader answers TotalTime with a
    // NullReferenceException, and the playhead reads it at frame rate.
    private long durationTicks;
    // Seqlock over the anchor, the stopwatch and the device clock. An odd
    // version means a transition is moving them; readers then report the
    // position being moved to instead of the new anchor plus the old clock's
    // elapsed time, which is what made a rate change flash the playhead ahead.
    private long clockVersion;
    private long transitionPositionTicks;
    private int clockTransitionDepth;

    public WindowsAudioDeviceEngine(
        double requestedOutputLatencyMilliseconds = DefaultRequestedOutputLatencyMilliseconds)
    {
        if (!double.IsFinite(requestedOutputLatencyMilliseconds) ||
            requestedOutputLatencyMilliseconds < MinimumRequestedOutputLatencyMilliseconds ||
            requestedOutputLatencyMilliseconds > MaximumRequestedOutputLatencyMilliseconds)
            throw new ArgumentOutOfRangeException(
                nameof(requestedOutputLatencyMilliseconds),
                AvaloniaLocalizationCatalog.Format("输出延迟必须位于 {0:0} 到 {1:0} 毫秒之间。", MinimumRequestedOutputLatencyMilliseconds, MaximumRequestedOutputLatencyMilliseconds));
        this.requestedOutputLatencyMilliseconds = requestedOutputLatencyMilliseconds;
        measuredOutputLatencyMilliseconds = requestedOutputLatencyMilliseconds;
        endpointNotifications = new OutputDeviceNotificationClient(this);
        // The Windows check is load bearing, not a tidy-up of the catch below.
        // MMDeviceEnumerator is a COM coclass, and on iOS its construction does
        // not throw — the runtime ships with BuiltInComInterop disabled and
        // activating one aborts the process outright:
        //
        //   Got a SIGABRT while executing native code.
        //     at NAudio.CoreAudioApi.Interfaces.MMDeviceEnumeratorComObject:.ctor
        //
        // That abort took the app down every time a chart was created from an
        // imported audio file, which is the first thing that builds an engine.
        // A catch cannot help: there is no managed exception to catch.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                endpointEnumerator = new MMDeviceEnumerator();
                endpointEnumerator.RegisterEndpointNotificationCallback(endpointNotifications);
            }
            catch
            {
                // Remote and virtual sessions can expose audio without
                // notifications.
            }
        }
        telemetryTask = Task.Run(() => RunTelemetryAsync(telemetryCancellation.Token));
    }

    public bool IsLoaded => source != null;
    public bool IsPlaying => output?.PlaybackState == PlaybackState.Playing;
    public bool HasOutput => output != null;
    public WindowsAudioDeviceDiagnostics Diagnostics => new(
        Interlocked.Read(ref unexpectedPlaybackStopCount),
        Interlocked.Read(ref transientMusicZeroCount),
        Volatile.Read(ref lastUnexpectedPlaybackStopError));

    public TimeSpan Position
    {
        get
        {
            ThrowIfDisposed();
            return TimeSpan.FromTicks(ReadPositionTicks());
        }
    }

    public TimeSpan Duration
    {
        get
        {
            ThrowIfDisposed();
            return TimeSpan.FromTicks(Interlocked.Read(ref durationTicks));
        }
    }

    private long ReadPositionTicks()
    {
        var version = Interlocked.Read(ref clockVersion);
        long ticks;
        if ((version & 1) != 0 || !TryReadClockPositionTicks(version, out ticks))
            ticks = Interlocked.Read(ref transitionPositionTicks);
        return Math.Clamp(ticks, 0, Math.Max(0, Interlocked.Read(ref durationTicks)));
    }

    private bool TryReadClockPositionTicks(long version, out long ticks)
    {
        ticks = Interlocked.Read(ref positionAnchorTicks);
        if (positionClock.IsRunning)
        {
            long audibleTicks;
            if (TryGetCachedAudioClockElapsedTicks(out var deviceTicks))
            {
                audibleTicks = deviceTicks;
            }
            else
            {
                var queuedTicks = (long)(
                    Volatile.Read(ref measuredOutputLatencyMilliseconds) *
                    TimeSpan.TicksPerMillisecond);
                audibleTicks = Math.Max(0, positionClock.Elapsed.Ticks - queuedTicks);
            }
            ticks += (long)(audibleTicks * playbackRate);
        }

        // Interlocked.Read is a full fence, so none of the reads above can
        // drift past this check of whether a transition overlapped them.
        return Interlocked.Read(ref clockVersion) == version;
    }

    /// <summary>
    /// Opens a window in which the anchor and the clocks may be inconsistent.
    /// Callers hold <see cref="operationLock"/>, and compute
    /// <paramref name="positionTicks"/> before opening it, because a
    /// <see cref="Position"/> read inside the window reports that value.
    /// </summary>
    private void BeginClockTransition(long positionTicks)
    {
        Interlocked.Exchange(ref transitionPositionTicks, positionTicks);
        if (clockTransitionDepth++ == 0) Interlocked.Increment(ref clockVersion);
    }

    private void EndClockTransition()
    {
        if (--clockTransitionDepth != 0) return;
        Interlocked.Increment(ref clockVersion);
    }

    public double OutputLatencyMilliseconds =>
        Volatile.Read(ref measuredOutputLatencyMilliseconds);

    internal long TelemetryComProbeCount => Interlocked.Read(ref telemetryComProbeCount);

    public double PlaybackRate
    {
        get => playbackRate;
        set => RunSerialized(() => SetPlaybackRateLocked(value), throwIfDisposed: false);
    }

    private void SetPlaybackRateLocked(double value)
    {
        var boundedRate = Math.Clamp(value, .2, 1);
        var requiresPhaseVocoder = source != null &&
            WindowsPitchPreservingTimeStretchPolicy.UsesPhaseVocoder(
                boundedRate,
                source.WaveFormat.Channels);
        var hasPitchPreservingPipeline =
            phaseVocoderProvider != null || soundTouchProviders.Length > 0;
        if (preservePitch && source != null && hasPitchPreservingPipeline &&
            (phaseVocoderProvider != null) != requiresPhaseVocoder)
        {
            var position = TimeSpan.FromTicks(ReadPositionTicks());
            var resume = IsPlaying;
            BeginClockTransition(position.Ticks);
            try
            {
                playbackRate = boundedRate;
                RecreateOutputAt(position, resume);
            }
            finally
            {
                EndClockTransition();
            }
            return;
        }

        CapturePosition(
            positionClock.IsRunning,
            () => playbackRate = boundedRate);
        if (rateProvider != null) rateProvider.PlaybackRate = playbackRate;
        if (phaseVocoderProvider != null)
            phaseVocoderProvider.PlaybackRate = playbackRate;
        var stageTempo = WindowsPitchPreservingTimeStretchPolicy.ResolveSoundTouchTempo(
            playbackRate);
        for (var index = soundTouchProviders.Length - 1; index >= 0; index--)
            soundTouchProviders[index].Tempo = (float)stageTempo;
    }

    public bool PreservePitch
    {
        get => preservePitch;
        set => RunSerialized(
            () =>
            {
                if (preservePitch == value)
                {
                    if (source != null && output == null) RecreateOutput();
                    return;
                }
                preservePitch = value;
                if (source != null) RecreateOutput();
            },
            throwIfDisposed: false);
    }

    public float Volume
    {
        get => volume;
        set => RunSerialized(
            () =>
            {
                volume = Math.Clamp(value, 0, 1);
                if (localVolumeProvider != null) localVolumeProvider.Volume = volume;
            },
            throwIfDisposed: false);
    }

    public event EventHandler? PlaybackStopped;
    public event EventHandler<AudioOutputDeviceChangedEventArgs>? DefaultOutputDeviceChanged;

    public void Load(string path) => RunSerialized(() =>
    {
        DisposePlayback();
        var opened = AudioDecoder.OpenWaveStream(path);
        source = opened;
        Interlocked.Exchange(ref durationTicks, Math.Max(0, opened.TotalTime.Ticks));
        BeginClockTransition(0);
        try
        {
            Interlocked.Exchange(ref positionAnchorTicks, 0);
            positionClock.Reset();
        }
        finally
        {
            EndClockTransition();
        }
        try
        {
            RecreateOutput();
        }
        catch
        {
            DisposePlayback();
            throw;
        }
    });

    public void Play() => RunSerialized(() =>
    {
        if (output == null) return;
        if (output.PlaybackState == PlaybackState.Playing && positionClock.IsRunning) return;
        BeginClockTransition(ReadPositionTicks());
        try
        {
            if (output.PlaybackState == PlaybackState.Playing) output.Pause();
            transportEnvelope?.FadeIn();
            ArmAudioClock();
            output.Play();
            positionClock.Restart();
        }
        finally
        {
            EndClockTransition();
        }
    });

    public void Pause() => RunSerialized(() =>
    {
        if (output == null) return;
        FadeOutAndDrain();
        if (positionClock.IsRunning) CapturePosition(false);
        if (output.PlaybackState == PlaybackState.Playing) output.Pause();
    });

    public void Stop() => RunSerialized(() =>
    {
        if (source == null) return;
        RecreateOutputAt(TimeSpan.Zero, false);
    });

    public void Seek(TimeSpan position) => RunSerialized(() =>
    {
        if (source == null) return;
        var ticks = Math.Clamp(position.Ticks, 0, Interlocked.Read(ref durationTicks));
        RecreateOutputAt(TimeSpan.FromTicks(ticks), IsPlaying);
    });

    public void RefreshDefaultOutputDevice(bool resumePlayback) => RunSerialized(() =>
    {
        if (source == null) return;
        RecreateOutputAt(TimeSpan.FromTicks(ReadPositionTicks()), resumePlayback);
    });

    /// <summary>
    /// Runs one mutating operation under <see cref="operationLock"/>. A
    /// <see cref="Dispose"/> that gave up waiting for the operation leaves the
    /// teardown to it: the operation releases whatever it opened or built on
    /// its way out instead of leaking a file handle or a WASAPI client until
    /// finalization. Stops reported by a render thread while the operation ran
    /// are applied once the lock is released.
    /// </summary>
    private void RunSerialized(Action operation, bool throwIfDisposed = true)
    {
        try
        {
            lock (operationLock)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    if (throwIfDisposed) ThrowIfDisposed();
                    return;
                }

                try
                {
                    operation();
                }
                finally
                {
                    if (Volatile.Read(ref disposed) != 0) DisposePlayback();
                }
            }
        }
        finally
        {
            DrainStoppedOutputs();
        }
    }

    public bool TryPlayOverlay(
        ISampleProvider provider,
        AudioPlaybackEpochToken playbackEpoch)
    {
        try
        {
            var activeOutput = output;
            var mixer = outputMixer;
            if (activeOutput == null || mixer == null || !mixer.IsActive ||
                activeOutput.PlaybackState != PlaybackState.Playing) return false;
            if (provider.WaveFormat.SampleRate != mixer.WaveFormat.SampleRate)
                provider = new WdlResamplingSampleProvider(provider, mixer.WaveFormat.SampleRate);
            if (provider.WaveFormat.Channels != mixer.WaveFormat.Channels)
            {
                provider = (provider.WaveFormat.Channels, mixer.WaveFormat.Channels) switch
                {
                    (1, 2) => new MonoToStereoSampleProvider(provider),
                    (2, 1) => new StereoToMonoSampleProvider(provider)
                    {
                        LeftVolume = .5f,
                        RightVolume = .5f
                    },
                    // Surround and multichannel music used to reject every
                    // shared-clock voice here, silently. Key sounds are mono
                    // or stereo, so they belong on the front pair.
                    (<= 2, > 2) => new FrontPairChannelMapSampleProvider(
                        provider,
                        mixer.WaveFormat.Channels),
                    _ => throw new NotSupportedException("Key 音暂不支持当前音乐的声道布局。")
                };
            }
            // This guard must remain outside every buffering conversion. If it
            // were wrapped by the resampler, samples cached before a seek could
            // still escape after the transport epoch changes.
            provider = new EpochGuardSampleProvider(provider, playbackEpoch);
            if (!playbackEpoch.IsCurrent) return false;
            return mixer.TryEnqueue(provider);
        }
        catch
        {
            return false;
        }
    }

    private void RecreateOutput()
    {
        if (source == null) return;
        RecreateOutputAt(TimeSpan.FromTicks(ReadPositionTicks()), IsPlaying);
    }

    private void RecreateOutputAt(TimeSpan position, bool resume)
    {
        if (source == null) return;
        position = TimeSpan.FromTicks(
            Math.Clamp(position.Ticks, 0, Interlocked.Read(ref durationTicks)));
        // Readers see the target position for the whole rebuild; the anchor is
        // written before the stopwatch stops, and neither write may be observed
        // alone.
        BeginClockTransition(position.Ticks);
        try
        {
            RecreateOutputAtCore(position, resume);
        }
        finally
        {
            EndClockTransition();
        }
    }

    private void RecreateOutputAtCore(TimeSpan position, bool resume)
    {
        if (source == null) return;
        FadeOutAndDrain();
        // Land on the new position before the old output goes away. Leaving the
        // clock running would have Position keep extrapolating from the anchor
        // this rebuild is moving away from, over an output that is already
        // disposed and silent, so the playhead creeps on for the whole rebuild
        // and then snaps. Every caller passes the position playback is to
        // continue from, which is what the tail of this method anchors anyway.
        Interlocked.Exchange(ref positionAnchorTicks, position.Ticks);
        positionClock.Reset();
        DisposeOutput();
        RateAdjustingSampleProvider? createdRateProvider = null;
        WindowsPhaseVocoderTimeStretchSampleProvider? createdPhaseVocoderProvider = null;
        var createdSoundTouchProviders = new List<SoundTouchWaveProvider>();
        VolumeSampleProvider? createdVolumeProvider = null;
        QueuedOverlayMixerSampleProvider? createdMixer = null;
        IWavePlayer? createdOutput = null;
        try
        {
            source.CurrentTime = position;
            // A decoder seek can be slow; do not build a device for an engine
            // whose disposal is already waiting on this call.
            ThrowIfDisposed();
            var boundedSource = LengthCappedSampleProvider.FromWaveStream(source);
            var activeSource = source;
            bool RateSourceReallyEnded() =>
                boundedSource.IsEnded ||
                activeSource.CurrentTime >=
                activeSource.TotalTime - TimeSpan.FromMilliseconds(250);
            ISampleProvider musicProvider;
            SoundTouchContinuitySampleProvider? createdContinuityProvider = null;
            Func<bool> musicReallyEnded = RateSourceReallyEnded;
            if (preservePitch)
            {
                if (WindowsPitchPreservingTimeStretchPolicy.UsesPhaseVocoder(
                        playbackRate,
                        boundedSource.WaveFormat.Channels))
                {
                    createdPhaseVocoderProvider =
                        new WindowsPhaseVocoderTimeStretchSampleProvider(
                            boundedSource,
                            RateSourceReallyEnded,
                            playbackRate);
                    _ = createdPhaseVocoderProvider.Prime();
                    musicProvider = createdPhaseVocoderProvider;
                    musicReallyEnded = () => createdPhaseVocoderProvider.IsEnded;
                }
                else
                {
                    IWaveProvider stageSource = boundedSource.ToWaveProvider();
                    var processor = new SoundTouchProcessor();
                    var stageProvider = new SoundTouchWaveProvider(stageSource, processor)
                    {
                        Tempo = (float)WindowsPitchPreservingTimeStretchPolicy
                            .ResolveSoundTouchTempo(playbackRate),
                        Pitch = 1,
                        Rate = 1
                    };
                    WindowsPitchPreservingTimeStretchPolicy.Configure(processor);
                    createdSoundTouchProviders.Add(stageProvider);
                    stageSource = stageProvider;
                    createdContinuityProvider = new SoundTouchContinuitySampleProvider(
                        stageSource.ToSampleProvider(),
                        () => boundedSource.IsEnded);
                    musicProvider = createdContinuityProvider;
                    musicReallyEnded = () => createdContinuityProvider.IsEnded;
                }
            }
            else
            {
                createdRateProvider = new RateAdjustingSampleProvider(
                    boundedSource,
                    RateSourceReallyEnded)
                {
                    PlaybackRate = playbackRate
                };
                createdRateProvider.Reset(position);
                musicProvider = createdRateProvider;
            }

            createdVolumeProvider = new VolumeSampleProvider(musicProvider) { Volume = volume };
            createdMixer = new QueuedOverlayMixerSampleProvider(
                createdVolumeProvider,
                musicReallyEnded,
                () => Interlocked.Increment(ref transientMusicZeroCount));
            var envelope = new TransportEnvelopeSampleProvider(createdMixer);
            createdOutput = CreateOutput(envelope.ToWaveProvider());
            createdOutput.PlaybackStopped += OnPlaybackStopped;
            rateProvider = createdRateProvider;
            phaseVocoderProvider = createdPhaseVocoderProvider;
            soundTouchProviders = [.. createdSoundTouchProviders];
            localVolumeProvider = createdVolumeProvider;
            outputMixer = createdMixer;
            output = createdOutput;
            transportEnvelope = envelope;
            Volatile.Write(
                ref measuredOutputLatencyMilliseconds,
                requestedOutputLatencyMilliseconds);
            Interlocked.Exchange(ref positionAnchorTicks, position.Ticks);
            positionClock.Reset();
            if (!resume) return;
            ThrowIfDisposed();
            envelope.FadeIn();
            ArmAudioClock();
            createdOutput.Play();
            positionClock.Restart();
        }
        catch
        {
            createdMixer?.Complete();
            if (createdOutput != null)
            {
                createdOutput.PlaybackStopped -= OnPlaybackStopped;
                try { createdOutput.Stop(); } catch { }
                try { createdOutput.Dispose(); } catch { }
            }
            foreach (var createdSoundTouchProvider in createdSoundTouchProviders)
            {
                if (createdSoundTouchProvider is not IDisposable disposableSoundTouch) continue;
                try { disposableSoundTouch.Dispose(); } catch { }
            }
            output = null;
            outputMixer = null;
            localVolumeProvider = null;
            soundTouchProviders = [];
            rateProvider = null;
            phaseVocoderProvider = null;
            ResetAudioClock();
            positionClock.Reset();
            throw;
        }
    }

    private IWavePlayer CreateOutput(IWaveProvider provider) =>
        PlatformAudioOutputs.CreateOutput(
            provider,
            new PlatformAudioOutputRequest(
                requestedOutputLatencyMilliseconds,
                PrioritizeLatencyOverStability: true,
                FallbackLatencyMilliseconds: 80));

    /// <summary>
    /// Runs on the stopped output's render thread, which can be the very thread
    /// an operation holding <see cref="operationLock"/> is joining through
    /// <see cref="IWavePlayer.Stop"/>: waiting for the lock here would deadlock
    /// that join, and acting without it let a late stop reset the clock of the
    /// output that had just replaced this one. The stop is only recorded, then
    /// applied under the lock once nothing holds it, after checking that the
    /// output which stopped is still the current one.
    /// </summary>
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (sender is not IWavePlayer stoppedOutput) return;
        stoppedOutputs.Enqueue(new StoppedOutput(stoppedOutput, e.Exception));
        DrainStoppedOutputs();
    }

    private void DrainStoppedOutputs()
    {
        while (!stoppedOutputs.IsEmpty)
        {
            // An operation running on this thread, or on another, applies the
            // queue itself as soon as it leaves the lock.
            if (Monitor.IsEntered(operationLock) || !Monitor.TryEnter(operationLock)) return;
            List<Action>? notifications = null;
            try
            {
                while (stoppedOutputs.TryDequeue(out var stopped))
                {
                    try
                    {
                        ApplyPlaybackStoppedLocked(stopped, ref notifications);
                    }
                    catch
                    {
                        // This can run on a render thread, where an escaping
                        // exception takes the process down.
                    }
                }
            }
            finally
            {
                Monitor.Exit(operationLock);
            }

            if (notifications == null) continue;
            foreach (var notification in notifications)
            {
                try
                {
                    notification();
                }
                catch
                {
                    // Same render-thread constraint as above.
                }
            }
        }
    }

    private void ApplyPlaybackStoppedLocked(
        StoppedOutput stopped,
        ref List<Action>? notifications)
    {
        if (Volatile.Read(ref disposed) != 0 || !ReferenceEquals(stopped.Output, output)) return;
        var wasRunning = positionClock.IsRunning;
        var activeSource = source;
        var endTicks = Interlocked.Read(ref durationTicks);
        var reachedEnd = activeSource != null &&
            (activeSource.CurrentTime >= activeSource.TotalTime - TimeSpan.FromMilliseconds(250) ||
             // The pipeline ran out on its own. For a decoder that dried up
             // before its declared length this is the only sign of the end;
             // treating it as an interruption rebuilt the output at the same
             // spot and stopped again, indefinitely.
             stopped.Error == null && outputMixer?.MusicEnded == true);
        if (reachedEnd)
        {
            BeginClockTransition(endTicks);
            try
            {
                Interlocked.Exchange(ref positionAnchorTicks, endTicks);
                Volatile.Write(ref audioClockArmed, false);
                positionClock.Reset();
            }
            finally
            {
                EndClockTransition();
            }
        }
        else
        {
            CapturePosition(false);
        }

        notifications ??= [];
        if (!reachedEnd && wasRunning)
        {
            Interlocked.Increment(ref unexpectedPlaybackStopCount);
            Volatile.Write(
                ref lastUnexpectedPlaybackStopError,
                stopped.Error?.GetType().Name ?? "none");
            var error = stopped.Error;
            notifications.Add(() => DefaultOutputDeviceChanged?.Invoke(
                this,
                new AudioOutputDeviceChangedEventArgs(
                    true,
                    AudioOutputDeviceChangeReason.UnexpectedPlaybackStop,
                    error)));
            return;
        }
        notifications.Add(() => PlaybackStopped?.Invoke(this, EventArgs.Empty));
    }

    private void NotifyDefaultOutputDeviceChanged(DataFlow flow, Role role)
    {
        if (Volatile.Read(ref disposed) != 0 || flow != DataFlow.Render ||
            role is not (Role.Console or Role.Multimedia)) return;
        DefaultOutputDeviceChanged?.Invoke(
            this,
            new AudioOutputDeviceChangedEventArgs(
                positionClock.IsRunning || IsPlaying,
                AudioOutputDeviceChangeReason.DefaultEndpointChanged));
    }

    private void CapturePosition(bool keepRunning, Action? applyWithinTransition = null)
    {
        var positionTicks = ReadPositionTicks();
        BeginClockTransition(positionTicks);
        try
        {
            applyWithinTransition?.Invoke();
            Interlocked.Exchange(ref positionAnchorTicks, positionTicks);
            if (keepRunning)
            {
                ArmAudioClock();
                positionClock.Restart();
            }
            else
            {
                Volatile.Write(ref audioClockArmed, false);
                positionClock.Reset();
            }
        }
        finally
        {
            EndClockTransition();
        }
    }

    private void ArmAudioClock()
    {
        Volatile.Write(ref audioClockArmed, false);
        lock (audioClockGate)
        {
            try
            {
                if (output is not WasapiOut wasapi ||
                    WasapiAudioClientField?.GetValue(wasapi) is not AudioClient client) return;
                audioClockClient ??= client.AudioClockClient;
                if (!audioClockClient.GetPosition(out var position, out _)) return;
                var frequency = audioClockClient.Frequency;
                if (frequency == 0) return;
                audioClockAnchor = position;
                audioClockFrequency = frequency;
                Interlocked.Exchange(ref audioClockHasAdvanced, 0);
                Interlocked.Exchange(ref audioClockArmTimestamp, Stopwatch.GetTimestamp());
                Volatile.Write(
                    ref lastAudioClockProbe,
                    new AudioClockProbeSample(0, Stopwatch.GetTimestamp()));
                // Zero admits the first reader of the new hold without waiting
                // out an interval measured against the previous one.
                Interlocked.Exchange(ref lastAudioClockProbeAttemptTimestamp, 0);
                Volatile.Write(ref audioClockArmed, true);
            }
            catch
            {
                // Stopwatch remains the safe fallback for WaveOut and unusual drivers.
            }
        }
    }

    private bool TryGetCachedAudioClockElapsedTicks(out long ticks)
    {
        ticks = 0;
        if (!Volatile.Read(ref audioClockArmed) ||
            audioClockClient == null || audioClockFrequency == 0) return false;

        if (Volatile.Read(ref audioClockHasAdvanced) == 0)
        {
            var holdTimestamp = Stopwatch.GetTimestamp();
            var armTimestamp = Interlocked.Read(ref audioClockArmTimestamp);
            if (ShouldHoldAudioClockStartup(false, armTimestamp, holdTimestamp))
            {
                // Releasing the hold is the one thing a caller in this window is
                // waiting on, and leaving it to the telemetry sampler costs a
                // motionless playhead for up to a full sampler period, ended by
                // a jump of everything the endpoint rendered in the meantime.
                if (TryAdmitStartupProbe(holdTimestamp) &&
                    ProbeAudioClockElapsedTicks(out var probedTicks) &&
                    Volatile.Read(ref audioClockHasAdvanced) != 0)
                {
                    ticks = probedTicks;
                    return true;
                }

                // Hold at the anchor until the endpoint consumes its first sample.
                ticks = 0;
                return true;
            }

            // Drivers without a useful device clock keep the established
            // Stopwatch/latency fallback instead of freezing the editor.
            return false;
        }

        var now = Stopwatch.GetTimestamp();
        var lastProbe = Volatile.Read(ref lastAudioClockProbe);
        if (lastProbe == null || lastProbe.Timestamp <= 0) return false;
        ticks = Math.Max(0, lastProbe.ElapsedTicks +
            Stopwatch.GetElapsedTime(lastProbe.Timestamp, now).Ticks);
        return true;
    }

    /// <summary>
    /// Admits at most one startup probe per <see cref="AudioClockProbeInterval"/>
    /// without touching <see cref="audioClockGate"/>. <see cref="Position"/> is
    /// sampled by the high-rate frame pump at up to 1000 Hz, and every reader
    /// this turns away leaves on the lock-free path it has always used. The
    /// stamp moves whether or not the probe that follows succeeds, so a
    /// GetPosition failing through an endpoint transition cannot decay into one
    /// COM call per sample.
    /// </summary>
    private bool TryAdmitStartupProbe(long now)
    {
        var lastAttempt = Interlocked.Read(ref lastAudioClockProbeAttemptTimestamp);
        if (lastAttempt != 0 && now - lastAttempt < AudioClockStartupAdmissionInterval)
            return false;
        return Interlocked.CompareExchange(
            ref lastAudioClockProbeAttemptTimestamp,
            now,
            lastAttempt) == lastAttempt;
    }

    private bool ProbeAudioClockElapsedTicks(out long ticks)
    {
        ticks = 0;
        if (!Volatile.Read(ref audioClockArmed) ||
            audioClockClient == null || audioClockFrequency == 0) return false;
        lock (audioClockGate)
        {
            if (!Volatile.Read(ref audioClockArmed) ||
                audioClockClient == null || audioClockFrequency == 0) return false;
            var now = Stopwatch.GetTimestamp();
            var lastProbe = Volatile.Read(ref lastAudioClockProbe);
            if (lastProbe != null && lastProbe.Timestamp > 0 &&
                now - lastProbe.Timestamp < AudioClockProbeInterval)
            {
                ticks = Math.Max(0, lastProbe.ElapsedTicks +
                    Stopwatch.GetElapsedTime(lastProbe.Timestamp, now).Ticks);
                return true;
            }

            try
            {
                Interlocked.Increment(ref telemetryComProbeCount);
                if (!audioClockClient.GetPosition(out var position, out _) ||
                    position < audioClockAnchor) return false;
                var elapsed = (position - audioClockAnchor) / (double)audioClockFrequency;
                ticks = Math.Max(0, (long)(elapsed * TimeSpan.TicksPerSecond));
                Volatile.Write(ref lastAudioClockProbe, new AudioClockProbeSample(ticks, now));
                if (ticks > 0)
                {
                    var fallbackTicks = Math.Max(
                        0,
                        positionClock.Elapsed.Ticks -
                        (long)(Volatile.Read(ref measuredOutputLatencyMilliseconds) *
                            TimeSpan.TicksPerMillisecond));
                    // If startup exceeded the grace period, wait until the real
                    // clock catches the fallback rather than switching backwards.
                    if (CanActivateAudioClock(ticks, fallbackTicks))
                        Interlocked.Exchange(ref audioClockHasAdvanced, 1);
                }
                return true;
            }
            catch
            {
                Volatile.Write(ref audioClockArmed, false);
                return false;
            }
        }
    }

    internal static bool ShouldHoldAudioClockStartup(
        bool hasAdvanced,
        long armTimestamp,
        long nowTimestamp) =>
        !hasAdvanced &&
        armTimestamp > 0 &&
        nowTimestamp >= armTimestamp &&
        Stopwatch.GetElapsedTime(armTimestamp, nowTimestamp) <=
        AudioClockStartupGracePeriod;

    internal static bool CanActivateAudioClock(
        long deviceElapsedTicks,
        long fallbackElapsedTicks) =>
        deviceElapsedTicks > 0 &&
        deviceElapsedTicks >= Math.Max(0, fallbackElapsedTicks);

    private void ProbeOutputLatency()
    {
        // The same gate as the device clock probe. DisposeOutput detaches the
        // output under it, so the client queried here cannot be released by a
        // rebuild in the middle of the COM call.
        lock (audioClockGate)
        {
            try
            {
                if (output is not WasapiOut wasapi ||
                    WasapiAudioClientField?.GetValue(wasapi) is not AudioClient client) return;
                Interlocked.Increment(ref telemetryComProbeCount);
                var sampleRate = wasapi.OutputWaveFormat?.SampleRate ??
                                 source?.WaveFormat.SampleRate ?? 0;
                var queuedFrames = client.CurrentPadding;
                var measured = sampleRate <= 0 ? 0 : queuedFrames * 1000d / sampleRate;
                if (measured is >= 3 and <= 250)
                    Volatile.Write(ref measuredOutputLatencyMilliseconds, measured);
            }
            catch
            {
                // Retain the safe cached estimate during endpoint transitions.
            }
        }
    }

    private async Task RunTelemetryAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TelemetryProbeInterval, cancellationToken).ConfigureAwait(false);
                if (Volatile.Read(ref disposed) != 0 || !positionClock.IsRunning) continue;
                _ = ProbeAudioClockElapsedTicks(out _);
                ProbeOutputLatency();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Authoritative engine disposal ends the low-frequency sampler.
        }
    }

    private void DisposePlayback()
    {
        DisposeOutput();
        var previousSource = source;
        source = null;
        try { previousSource?.Dispose(); } catch { }
        BeginClockTransition(0);
        try
        {
            Interlocked.Exchange(ref durationTicks, 0);
            Interlocked.Exchange(ref positionAnchorTicks, 0);
            positionClock.Reset();
        }
        finally
        {
            EndClockTransition();
        }
    }

    private void FadeOutAndDrain()
    {
        if (output?.PlaybackState != PlaybackState.Playing || transportEnvelope == null) return;
        var latency = Math.Clamp(Volatile.Read(ref measuredOutputLatencyMilliseconds), 1, 80);
        // This runs on the serialized audio worker, never the UI thread. The sink
        // must consume the faded buffer before Pause/Stop cuts the device stream.
        _ = transportEnvelope.FadeOut().Wait(TimeSpan.FromMilliseconds(latency + TransportEnvelopeSampleProvider.FadeMilliseconds + 10));
        Thread.Sleep((int)Math.Ceiling(latency) + 2);
    }

    private void DisposeOutput()
    {
        IWavePlayer? previousOutput;
        lock (audioClockGate)
        {
            ResetAudioClock();
            previousOutput = output;
            output = null;
        }
        var previousMixer = outputMixer;
        var previousSoundTouchProviders = soundTouchProviders;
        outputMixer = null;
        transportEnvelope = null;
        rateProvider = null;
        phaseVocoderProvider = null;
        soundTouchProviders = [];
        localVolumeProvider = null;
        previousMixer?.Complete();
        if (previousOutput != null)
        {
            previousOutput.PlaybackStopped -= OnPlaybackStopped;
            try { previousOutput.Stop(); } catch { }
            try { previousOutput.Dispose(); } catch { }
        }
        foreach (var previousSoundTouch in previousSoundTouchProviders)
        {
            if (previousSoundTouch is not IDisposable disposableSoundTouch) continue;
            try { disposableSoundTouch.Dispose(); } catch { }
        }
    }

    private void ResetAudioClock()
    {
        Volatile.Write(ref audioClockArmed, false);
        lock (audioClockGate)
        {
            try
            {
                audioClockClient?.Dispose();
            }
            catch
            {
                // A removed endpoint can reject its final COM release call.
            }
            finally
            {
                audioClockClient = null;
                audioClockFrequency = 0;
                Interlocked.Exchange(ref audioClockHasAdvanced, 0);
                Interlocked.Exchange(ref audioClockArmTimestamp, 0);
                Volatile.Write(ref lastAudioClockProbe, null);
                Interlocked.Exchange(ref lastAudioClockProbeAttemptTimestamp, 0);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        telemetryCancellation.Cancel();
        try
        {
            endpointEnumerator?.UnregisterEndpointNotificationCallback(endpointNotifications);
        }
        catch
        {
            // The endpoint can disappear before the callback is unregistered.
        }
        var lockTaken = false;
        try
        {
            // An operation still inside a decoder or driver call keeps the
            // lock. Past the timeout it releases the playback itself on its
            // way out (RunSerialized), instead of this call tearing the
            // decoder and client down underneath it.
            Monitor.TryEnter(operationLock, DisposeOperationWaitTimeout, ref lockTaken);
            if (lockTaken) DisposePlayback();
        }
        finally
        {
            if (lockTaken) Monitor.Exit(operationLock);
        }
        endpointEnumerator?.Dispose();
        _ = telemetryTask.ContinueWith(
            _ => telemetryCancellation.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref disposed) != 0,
        this);

    internal sealed class QueuedOverlayMixerSampleProvider : ISampleProvider
    {
        // Decoders and time-stretch stages can emit a NaN or a runaway value,
        // and one such sample poisons every float stage after it: the overlay
        // limiter, the endpoint's own effects. The ceiling sits well above any
        // legitimate decoder overshoot, so real material passes untouched.
        internal const float MaximumMusicSampleMagnitude = 4f;

        private readonly ISampleProvider music;
        private readonly Func<bool> musicSourceEnded;
        private readonly Action? onTransientMusicZero;
        private readonly ConcurrentQueue<ISampleProvider> pending = new();
        private readonly List<ISampleProvider> active = [];
        private readonly object completionGate = new();
        private readonly object readGate = new();
        private float[] scratch = [];
        private float[] overlayScratch = [];
        private bool musicEnded;
        private bool terminalSilenceReturned;
        private volatile bool completed;

        public QueuedOverlayMixerSampleProvider(
            ISampleProvider music,
            Func<bool> musicSourceEnded,
            Action? onTransientMusicZero = null)
        {
            this.music = music;
            this.musicSourceEnded = musicSourceEnded;
            this.onTransientMusicZero = onTransientMusicZero;
            WaveFormat = music.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }
        public bool IsActive => !completed;

        /// <summary>
        /// Whether the music pipeline reported its own end, as opposed to the
        /// output stopping underneath it.
        /// </summary>
        public bool MusicEnded => Volatile.Read(ref musicEnded);

        public bool TryEnqueue(ISampleProvider provider)
        {
            lock (completionGate)
            {
                if (completed) return false;
                pending.Enqueue(provider);
                return true;
            }
        }

        public void Complete()
        {
            lock (completionGate) completed = true;
            // WaveOutEvent.Stop returns without joining its callback thread,
            // which can still be inside Read and therefore inside the decoder
            // the caller seeks or disposes next. Taking the read gate waits
            // that read out; every later read sees the flag and leaves before
            // touching the music.
            lock (readGate) { }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            lock (readGate) return ReadLocked(buffer, offset, count);
        }

        private int ReadLocked(float[] buffer, int offset, int count)
        {
            if (completed) return 0;
            var musicRead = musicEnded ? 0 : music.Read(buffer, offset, count);
            if (musicRead <= 0 && !musicEnded && !musicSourceEnded())
            {
                // SoundTouch can transiently return no samples while priming its pipeline.
                // Retry once, then keep the output alive with silence until the real source ends.
                musicRead = music.Read(buffer, offset, count);
            }

            if (completed) return 0;
            if (musicRead <= 0 && !musicEnded && !musicSourceEnded())
                onTransientMusicZero?.Invoke();
            if (musicRead <= 0 && (musicEnded || musicSourceEnded())) Volatile.Write(ref musicEnded, true);
            if (musicRead < count) Array.Clear(buffer, offset + musicRead, count - musicRead);
            SanitizeMusic(buffer, offset, Math.Max(0, musicRead));
            while (pending.TryDequeue(out var provider)) active.Add(provider);
            if (scratch.Length < count) scratch = new float[count];
            if (overlayScratch.Length < count) overlayScratch = new float[count];
            Array.Clear(overlayScratch, 0, count);

            var overlayContributed = false;
            for (var index = active.Count - 1; index >= 0; index--)
            {
                Array.Clear(scratch, 0, count);
                var read = active[index].Read(scratch, 0, count);
                for (var sample = 0; sample < read; sample++)
                {
                    var value = scratch[sample];
                    overlayScratch[sample] += value;
                    overlayContributed |= MathF.Abs(value) > 1e-8f;
                }
                if (read == 0) active.RemoveAt(index);
            }

            if (overlayContributed)
            {
                for (var sample = 0; sample < count; sample++)
                {
                    var overlay = overlayScratch[sample];
                    if (MathF.Abs(overlay) <= 1e-8f) continue;
                    buffer[offset + sample] += overlay;
                }
                terminalSilenceReturned = false;
                WindowsKeySoundSoftLimiter.Process(buffer, offset, count);
            }
            if (!musicEnded || active.Count > 0 || !pending.IsEmpty) return count;
            if (terminalSilenceReturned) return 0;

            // WASAPI does not submit the managed buffer when a provider returns
            // zero. Emit one explicit silent device period before EOF so the
            // endpoint cannot keep replaying its previous short buffer.
            terminalSilenceReturned = true;
            Array.Clear(buffer, offset, count);
            return count;
        }

        private static void SanitizeMusic(float[] buffer, int offset, int count)
        {
            var end = offset + count;
            for (var index = offset; index < end; index++)
            {
                var sample = buffer[index];
                if (MathF.Abs(sample) <= MaximumMusicSampleMagnitude) continue;
                // NaN fails the comparison above as well and lands here.
                buffer[index] = float.IsFinite(sample)
                    ? MathF.CopySign(MaximumMusicSampleMagnitude, sample)
                    : 0;
            }
        }
    }

    /// <summary>
    /// Places a mono or stereo overlay on the first two channels of a wider
    /// music layout. Every standard WAVEFORMATEXTENSIBLE layout starts with
    /// front left and front right; the remaining channels stay silent rather
    /// than doubling each click into the centre and LFE.
    /// </summary>
    internal sealed class FrontPairChannelMapSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly int sourceChannels;
        private readonly int outputChannels;
        private float[] sourceBuffer = [];

        public FrontPairChannelMapSampleProvider(ISampleProvider source, int outputChannels)
        {
            ArgumentNullException.ThrowIfNull(source);
            sourceChannels = source.WaveFormat.Channels;
            if (sourceChannels is not (1 or 2) || outputChannels < 2)
                throw new NotSupportedException(
                    "Front-pair mapping needs a mono or stereo source and at least two output channels.");
            this.source = source;
            this.outputChannels = outputChannels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                source.WaveFormat.SampleRate,
                outputChannels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var frames = count / outputChannels;
            var sourceCount = frames * sourceChannels;
            if (sourceBuffer.Length < sourceCount) sourceBuffer = new float[sourceCount];
            var readFrames = source.Read(sourceBuffer, 0, sourceCount) / sourceChannels;
            var samples = readFrames * outputChannels;
            Array.Clear(buffer, offset, samples);
            for (var frame = 0; frame < readFrames; frame++)
            {
                var input = frame * sourceChannels;
                var target = offset + frame * outputChannels;
                buffer[target] = sourceBuffer[input];
                buffer[target + 1] = sourceBuffer[input + sourceChannels - 1];
            }
            return samples;
        }
    }

    private readonly record struct StoppedOutput(IWavePlayer Output, Exception? Error);

    private sealed record AudioClockProbeSample(long ElapsedTicks, long Timestamp);

    private sealed class OutputDeviceNotificationClient(WindowsAudioDeviceEngine owner)
        : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) =>
            owner.NotifyDefaultOutputDeviceChanged(flow, role);

        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}

public enum AudioOutputDeviceChangeReason
{
    DefaultEndpointChanged,
    UnexpectedPlaybackStop
}

public sealed class AudioOutputDeviceChangedEventArgs(
    bool wasPlaying,
    AudioOutputDeviceChangeReason reason = AudioOutputDeviceChangeReason.DefaultEndpointChanged,
    Exception? error = null) : EventArgs
{
    public bool WasPlaying { get; } = wasPlaying;
    public AudioOutputDeviceChangeReason Reason { get; } = reason;
    public Exception? Error { get; } = error;
}
