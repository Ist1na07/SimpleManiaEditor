using System.Collections.Concurrent;
using System.Collections.Immutable;
using LumaChart.Avalonia.Infrastructure;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LumaChart.Avalonia.Audio;

public enum KeySoundPreset
{
    Snare,
    HandDrum,
    HandDrum2,
    BrightTick,
    DeepPulse,
    Custom
}

internal readonly record struct WindowsKeySoundBatchPlaybackResult(
    int AcceptedTriggers,
    int RejectedTriggers,
    int SubmittedVoices);

/// <summary>
/// Pins decoded revisions used by one immutable editor plan. Active plans may
/// temporarily exceed the ordinary audition-cache budget; replacement,
/// deactivation and disposal release the pins deterministically.
/// </summary>
internal sealed class WindowsKeySoundPlanPreloadLease : IDisposable
{
    private WindowsKeySoundService? owner;
    private readonly string revisionKey;

    internal WindowsKeySoundPlanPreloadLease()
    {
        revisionKey = string.Empty;
    }

    internal WindowsKeySoundPlanPreloadLease(
        WindowsKeySoundService owner,
        string revisionKey)
    {
        this.owner = owner;
        this.revisionKey = revisionKey;
    }

    public void Dispose() =>
        Interlocked.Exchange(ref owner, null)?.ReleasePlanPin(revisionKey);
}

/// <summary>
/// Low-latency polyphonic Key 音 boundary. Producers enqueue compact voice
/// descriptors; device initialization and custom-file decoding never run on the
/// input thread.
/// </summary>
public sealed class WindowsKeySoundService : IDisposable, IAsyncDisposable
{
    private const int SampleRate = 44_100;
    private const int MaximumCustomSoundSeconds = 10;
    private const int MaximumCustomCacheEntries = 32;
    private const long MaximumCustomCacheBytes = 32L * 1024 * 1024;
    private const int MaximumConcurrentCustomDecodes = 2;
    public const float SingleVoiceGain = .68f;
    public const float OutputBoost = 4f;
    public const double StandaloneOutputLatencyMilliseconds = 20;
    private static readonly WaveFormat MixFormat =
        WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);
    private static readonly WindowsKeySoundPlanPreloadLease UnpinnedPreloadSuccess = new();

    private readonly object cacheGate = new();
    private readonly Dictionary<string, CustomSoundCacheEntry> cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> currentRevisionByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim customDecodeGate = new(
        MaximumConcurrentCustomDecodes,
        MaximumConcurrentCustomDecodes);
    private readonly CachedSound snare = new(
        KeySoundPresetSamples.Create(KeySoundPreset.Snare),
        MixFormat);
    private readonly CachedSound brightTick = new(
        KeySoundPresetSamples.Create(KeySoundPreset.BrightTick),
        MixFormat);
    private readonly CachedSound handDrum = new(
        KeySoundPresetSamples.Create(KeySoundPreset.HandDrum),
        MixFormat);
    private readonly CachedSound handDrum2 = new(
        KeySoundPresetSamples.Create(KeySoundPreset.HandDrum2),
        MixFormat);
    private readonly CachedSound deepPulse = new(
        KeySoundPresetSamples.Create(KeySoundPreset.DeepPulse),
        MixFormat);
    private readonly QueuedCachedSoundMixer mixer = new(MixFormat);
    private readonly AudioPlaybackEpoch scheduledPlaybackEpoch = new();
    private readonly VolumeSampleProvider localVolume;
    private readonly SemaphoreSlim outputGate = new(1, 1);
    // Taken by both the trigger path and the output's own render thread, which
    // reports stops through OnOutputPlaybackStopped. Never held across Stop():
    // WasapiOut.Stop joins the render thread that may be waiting for it.
    private readonly object outputStateGate = new();
    private readonly KeySoundEndpointNotificationClient endpointNotifications;
    private IWavePlayer? output;
    private bool outputHasStarted;
    // The output whose render thread died with an exception. WasapiOut leaves
    // PlaybackState at Playing in that case, so the state alone read as healthy
    // forever while every later voice queued for a thread that was gone.
    private IWavePlayer? faultedOutput;
    // The output that must start again as soon as its render thread reports
    // that it has stopped; see TryStartOutput.
    private IWavePlayer? restartPendingOutput;
    // The output started last whose render thread has not reported its stop.
    private IWavePlayer? outputAwaitingStopReport;
    private long outputStartEndOfStreamSerial;
    private int defaultEndpointSerial;
    private int outputEndpointSerial;
    private MMDeviceEnumerator? endpointEnumerator;
    private bool endpointNotificationsAttempted;
    private Exception? lastError;
    private float volume = (float)WindowsKeySoundSettings.DefaultVolume;
    private long cacheAccessSerial;
    private long cachedCustomSoundBytes;
    private int disposed;

    public WindowsKeySoundService()
    {
        localVolume = new VolumeSampleProvider(mixer) { Volume = volume };
        endpointNotifications = new KeySoundEndpointNotificationClient(this);
    }

    public float Volume
    {
        get => volume;
        set
        {
            volume = Math.Clamp(value, 0, 1);
            localVolume.Volume = volume;
        }
    }

    public Exception? LastError => Volatile.Read(ref lastError);
    public bool IsWarm
    {
        get
        {
            return Volatile.Read(ref disposed) == 0 && Volatile.Read(ref output) != null;
        }
    }
    internal int CachedCustomSoundCount
    {
        get
        {
            lock (cacheGate) return cache.Count;
        }
    }
    internal long CachedCustomSoundBytes
    {
        get
        {
            lock (cacheGate) return cachedCustomSoundBytes;
        }
    }

    /// <summary>
    /// Returns the gain for each voice of a simultaneous chord. Identical clicks
    /// otherwise add linearly and hit the output ceiling almost immediately. This
    /// louder Avalonia curve keeps a clearly audible single hit while reducing each
    /// additional voice enough for chords to grow without collapsing into clipping.
    /// </summary>
    public static float ChordVoiceGain(int voiceCount)
    {
        voiceCount = Math.Max(1, voiceCount);
        var targetCombinedGain = voiceCount switch
        {
            1 => SingleVoiceGain,
            2 => 1.08f,
            3 => 1.44f,
            4 => 1.76f,
            _ => Math.Min(2.30f, 1.76f + (voiceCount - 4) * .12f)
        };
        return targetCombinedGain / voiceCount;
    }

    /// <summary>
    /// Enqueues a standalone Key 音 without waiting for the audio device. Call
    /// <see cref="WarmUpAsync"/> during editor startup for deterministic first-hit
    /// latency.
    /// </summary>
    public bool Play(
        KeySoundPreset preset,
        string? customFile = null,
        float noteGain = 1,
        double delayMilliseconds = 0) =>
        PlayStandalone(
            preset,
            customFile,
            noteGain,
            delayMilliseconds,
            scheduledEpoch: null,
            softLimit: true);

    /// <summary>
    /// Enqueues a manual playtest voice on the pre-warmed 20 ms standalone
    /// output. The shared mixer applies a soft limiter only while one of these
    /// latency-sensitive voices contributes, preventing dense manual chords from
    /// hard-clipping without adding the music transport's larger output buffer.
    /// </summary>
    internal bool PlayLowLatency(
        KeySoundPreset preset,
        string? customFile = null,
        float noteGain = 1) =>
        PlayStandalone(
            preset,
            customFile,
            noteGain,
            delayMilliseconds: 0,
            scheduledEpoch: null,
            softLimit: true);

    /// <summary>
    /// Enqueues a playback-clock-bound voice on the standalone output. Preview
    /// voices use <see cref="Play"/> and intentionally do not carry this epoch.
    /// </summary>
    internal bool PlayScheduled(
        AudioPlaybackEpochToken scheduledEpoch,
        KeySoundPreset preset,
        string? customFile = null,
        float noteGain = 1,
        double delayMilliseconds = 0) =>
        PlayStandalone(
            preset,
            customFile,
            noteGain,
            delayMilliseconds,
            scheduledEpoch,
            softLimit: true);

    internal AudioPlaybackEpochToken CaptureScheduledPlaybackEpoch() =>
        scheduledPlaybackEpoch.Capture();

    internal AudioPlaybackEpochToken InvalidateScheduledPlaybackEpoch() =>
        scheduledPlaybackEpoch.Advance();

    internal bool IsScheduledPlaybackEpochCurrent(AudioPlaybackEpochToken token) =>
        scheduledPlaybackEpoch.IsCurrent(token);

    internal WindowsKeySoundBatchPlaybackResult PlayScheduledBatch(
        AudioPlaybackEpochToken scheduledEpoch,
        ImmutableArray<WindowsKeySoundPlaybackTrigger> triggers)
    {
        if (triggers.IsDefaultOrEmpty) return default;
        if (Volatile.Read(ref disposed) != 0 ||
            !scheduledPlaybackEpoch.IsCurrent(scheduledEpoch))
            return new WindowsKeySoundBatchPlaybackResult(0, triggers.Length, 0);
        var groups = ResolveBatchGroups(triggers, out _);
        if (Volatile.Read(ref disposed) != 0 ||
            !scheduledPlaybackEpoch.IsCurrent(scheduledEpoch))
            return new WindowsKeySoundBatchPlaybackResult(0, triggers.Length, 0);

        var accepted = 0;
        var submitted = 0;
        var gateHeld = false;
        try
        {
            if (!outputGate.Wait(0))
                return new WindowsKeySoundBatchPlaybackResult(0, triggers.Length, 0);
            gateHeld = true;
            var activeOutput = GetOrCreateOutputForTrigger(out var outputError);
            if (activeOutput == null || !scheduledPlaybackEpoch.IsCurrent(scheduledEpoch))
            {
                if (outputError != null) Volatile.Write(ref lastError, outputError);
                return new WindowsKeySoundBatchPlaybackResult(0, triggers.Length, 0);
            }

            foreach (var group in groups)
            {
                if (!scheduledPlaybackEpoch.IsCurrent(scheduledEpoch)) break;
                if (!mixer.TryEnqueue(
                        group.Sound,
                        ApplyOutputBoost(group.Gain, 2.1f),
                        group.DelayMilliseconds,
                        scheduledEpoch,
                        softLimit: true)) continue;
                accepted += group.TriggerCount;
                submitted++;
            }
            if (accepted == 0 && mixer.IsBacklogged)
                Volatile.Write(ref lastError, new InvalidOperationException("Key 音输出已停止。"));
            if (accepted > 0 && !TryStartOutput(activeOutput, out var startError))
            {
                mixer.DiscardPending();
                DisposeOutputLocked(activeOutput);
                Volatile.Write(ref lastError, startError);
                return new WindowsKeySoundBatchPlaybackResult(0, triggers.Length, 0);
            }
            if (accepted > 0) outputHasStarted = true;
            if (accepted > 0) Volatile.Write(ref lastError, null);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref lastError, exception);
        }
        finally
        {
            if (gateHeld) outputGate.Release();
        }
        return new WindowsKeySoundBatchPlaybackResult(
            accepted,
            triggers.Length - accepted,
            submitted);
    }

    private bool PlayStandalone(
        KeySoundPreset preset,
        string? customFile,
        float noteGain,
        double delayMilliseconds,
        AudioPlaybackEpochToken? scheduledEpoch,
        bool softLimit)
    {
        if (Volatile.Read(ref disposed) != 0 ||
            scheduledEpoch.HasValue &&
            !scheduledPlaybackEpoch.IsCurrent(scheduledEpoch.Value)) return false;
        var gateHeld = false;
        try
        {
            if (!outputGate.Wait(0))
            {
                Volatile.Write(
                    ref lastError,
                    new InvalidOperationException("Key 音输出正在切换，当前触发未入队。"));
                return false;
            }
            gateHeld = true;
            if (Volatile.Read(ref disposed) != 0 ||
                scheduledEpoch.HasValue &&
                !scheduledPlaybackEpoch.IsCurrent(scheduledEpoch.Value)) return false;
            if (!TryResolveSoundWithoutIo(preset, customFile, out var sound)) return false;
            var activeOutput = GetOrCreateOutputForTrigger(out var outputError);
            if (activeOutput == null)
            {
                Volatile.Write(
                    ref lastError,
                    outputError ?? new InvalidOperationException("Key 音独立输出尚未成功预热。"));
                return false;
            }
            if (!mixer.TryEnqueue(
                    sound,
                    ApplyOutputBoost(noteGain, 1.5f),
                    delayMilliseconds,
                    scheduledEpoch,
                    softLimit))
            {
                // A full queue means nothing has been draining it: the output
                // is not rendering, whatever its state claims.
                Volatile.Write(
                    ref lastError,
                    mixer.IsBacklogged
                        ? new InvalidOperationException("Key 音输出已停止。")
                        : new ObjectDisposedException(nameof(WindowsKeySoundService)));
                return false;
            }
            if (!TryStartOutput(activeOutput, out var startError))
            {
                mixer.DiscardPending();
                DisposeOutputLocked(activeOutput);
                Volatile.Write(ref lastError, startError);
                return false;
            }
            outputHasStarted = true;
            if (!IsOutputRunningOrRestarting(activeOutput) ||
                scheduledEpoch.HasValue &&
                !scheduledPlaybackEpoch.IsCurrent(scheduledEpoch.Value))
            {
                Volatile.Write(ref lastError, new InvalidOperationException("Key 音输出已停止。"));
                return false;
            }
            Volatile.Write(ref lastError, null);
            return true;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref lastError, exception);
            return false;
        }
        finally
        {
            if (gateHeld) outputGate.Release();
        }
    }

    /// <summary>
    /// Adds a Key 音 to the music transport's shared output clock. A rejected or
    /// stale playback-bound voice is never redirected to the standalone clock.
    /// </summary>
    public bool PlayThrough(
        WindowsAudioPlaybackService audio,
        KeySoundPreset preset,
        string? customFile = null,
        float noteGain = 1,
        double delayMilliseconds = 0)
    {
        ArgumentNullException.ThrowIfNull(audio);
        return PlayThrough(
            audio,
            audio.CapturePlaybackEpoch(),
            preset,
            customFile,
            noteGain,
            delayMilliseconds);
    }

    internal bool PlayThrough(
        WindowsAudioPlaybackService audio,
        AudioPlaybackEpochToken playbackEpoch,
        KeySoundPreset preset,
        string? customFile = null,
        float noteGain = 1,
        double delayMilliseconds = 0)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (Volatile.Read(ref disposed) != 0) return false;
        try
        {
            if (!TryResolveSoundWithoutIo(preset, customFile, out var sound)) return false;
            ISampleProvider provider = new CachedSoundProvider(
                sound,
                ApplyOutputBoost(noteGain, 1.5f) * volume);
            provider = Delay(provider, delayMilliseconds);
            if (audio.TryPlayOverlay(provider, playbackEpoch))
            {
                Volatile.Write(ref lastError, null);
                return true;
            }
            Volatile.Write(
                ref lastError,
                new InvalidOperationException("音乐播放时钟已失效或当前没有可用的共享输出。"));
            return false;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref lastError, exception);
            return false;
        }
    }

    internal WindowsKeySoundBatchPlaybackResult PlayThroughBatch(
        WindowsAudioPlaybackService audio,
        AudioPlaybackEpochToken playbackEpoch,
        ImmutableArray<WindowsKeySoundPlaybackTrigger> triggers)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (triggers.IsDefaultOrEmpty) return default;
        if (Volatile.Read(ref disposed) != 0 ||
            !audio.IsPlaybackEpochCurrent(playbackEpoch))
            return new WindowsKeySoundBatchPlaybackResult(0, triggers.Length, 0);
        var groups = ResolveBatchGroups(triggers, out _);
        if (Volatile.Read(ref disposed) != 0 ||
            !audio.IsPlaybackEpochCurrent(playbackEpoch))
            return new WindowsKeySoundBatchPlaybackResult(0, triggers.Length, 0);
        var accepted = 0;
        var submitted = 0;
        try
        {
            foreach (var group in groups)
            {
                if (!audio.IsPlaybackEpochCurrent(playbackEpoch)) break;
                ISampleProvider provider = new CachedSoundProvider(
                    group.Sound,
                    ApplyOutputBoost(group.Gain, 2.1f) * volume);
                provider = Delay(provider, group.DelayMilliseconds);
                if (!audio.TryPlayOverlay(provider, playbackEpoch)) continue;
                accepted += group.TriggerCount;
                submitted++;
            }
            if (accepted > 0) Volatile.Write(ref lastError, null);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref lastError, exception);
        }
        return new WindowsKeySoundBatchPlaybackResult(
            accepted,
            triggers.Length - accepted,
            submitted);
    }

    public async Task<bool> WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref disposed) != 0) return false;
        await outputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureEndpointNotificationsAsync().ConfigureAwait(false);
            var existing = Volatile.Read(ref output);
            if (existing != null)
            {
                try
                {
                    // Running, parking or parked after an idle stop, or built
                    // and not yet started: warm already. A faulted output, or
                    // one left on a former default endpoint, is rebuilt here,
                    // off the input thread.
                    if (IsOutputReusable(existing, out var faulted)) return true;
                    if (faulted) mixer.DiscardPending();
                }
                catch
                {
                    // A removed or failed endpoint must be detached before retrying.
                }
                DisposeOutputLocked(existing);
            }
            var endpointSerial = Volatile.Read(ref defaultEndpointSerial);
            var created = await Task.Run(
                    () => CreateOutput(localVolume.ToWaveProvider()),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (Volatile.Read(ref disposed) != 0)
            {
                created.PlaybackStopped -= OnOutputPlaybackStopped;
                try { created.Stop(); } catch { }
                try { created.Dispose(); } catch { }
                return false;
            }
            mixer.PrimeIdlePeriod();
            PublishOutputLocked(created, endpointSerial);
            Volatile.Write(ref lastError, null);
            return true;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref lastError, exception);
            return false;
        }
        finally
        {
            outputGate.Release();
        }
    }

    public async Task<bool> PreloadAsync(
        string? file,
        CancellationToken cancellationToken = default) =>
        await PreloadCoreAsync(file, pinForPlan: false, cancellationToken)
            .ConfigureAwait(false) != null;

    internal Task<WindowsKeySoundPlanPreloadLease?> PreloadPinnedAsync(
        string? file,
        CancellationToken cancellationToken = default) =>
        PreloadCoreAsync(file, pinForPlan: true, cancellationToken);

    private async Task<WindowsKeySoundPlanPreloadLease?> PreloadCoreAsync(
        string? file,
        bool pinForPlan,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0 || string.IsNullOrWhiteSpace(file)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(file);
        if (!info.Exists)
        {
            Volatile.Write(ref lastError, new FileNotFoundException("找不到 Key 音文件。", info.FullName));
            return null;
        }

        var fullPath = info.FullName;
        var revisionKey = CreateRevisionKey(info);
        CustomSoundCacheEntry entry;
        List<CustomSoundCacheEntry> removed = [];
        lock (cacheGate)
        {
            if (Volatile.Read(ref disposed) != 0) return null;
            if (currentRevisionByPath.TryGetValue(fullPath, out var previousRevision) &&
                !previousRevision.Equals(revisionKey, StringComparison.OrdinalIgnoreCase))
                RemoveCacheEntryLocked(previousRevision, removed);

            if (!cache.TryGetValue(revisionKey, out entry!))
            {
                var entryCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                entry = new CustomSoundCacheEntry(
                    fullPath,
                    revisionKey,
                    entryCancellation,
                    DecodeCustomSoundAsync(fullPath, entryCancellation.Token));
                cache.Add(revisionKey, entry);
                _ = ObserveCustomEntryCompletionAsync(entry);
            }
            entry.WaiterCount++;
            entry.LastAccess = ++cacheAccessSerial;
            TrimCustomCacheLocked(revisionKey, removed);
        }
        CancelEntries(removed);

        try
        {
            var sound = await entry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!FinalizeCustomEntry(entry, sound)) return null;
            WindowsKeySoundPlanPreloadLease? lease = null;
            if (pinForPlan)
            {
                lock (cacheGate)
                {
                    if (Volatile.Read(ref disposed) != 0 ||
                        !cache.TryGetValue(entry.RevisionKey, out var current) ||
                        !ReferenceEquals(current, entry)) return null;
                    entry.PinCount++;
                    lease = new WindowsKeySoundPlanPreloadLease(this, entry.RevisionKey);
                }
            }
            Volatile.Write(ref lastError, null);
            return lease ?? UnpinnedPreloadSuccess;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            removed = [];
            lock (cacheGate)
            {
                if (cache.TryGetValue(revisionKey, out var current) && ReferenceEquals(current, entry))
                    RemoveCacheEntryLocked(revisionKey, removed);
            }
            CancelEntries(removed);
            Volatile.Write(ref lastError, exception);
            return null;
        }
        finally
        {
            ReleaseCustomWaiter(entry);
        }
    }

    private bool TryResolveSoundWithoutIo(
        KeySoundPreset preset,
        string? customFile,
        out CachedSound sound)
    {
        sound = snare;
        if (preset == KeySoundPreset.Snare) return true;
        if (preset == KeySoundPreset.BrightTick)
        {
            sound = brightTick;
            return true;
        }
        if (preset == KeySoundPreset.HandDrum)
        {
            sound = handDrum;
            return true;
        }
        if (preset == KeySoundPreset.HandDrum2)
        {
            sound = handDrum2;
            return true;
        }
        if (preset == KeySoundPreset.DeepPulse)
        {
            sound = deepPulse;
            return true;
        }
        if (string.IsNullOrWhiteSpace(customFile))
        {
            Volatile.Write(ref lastError, new InvalidOperationException("自定义 Key 音未指定文件。"));
            return false;
        }

        var fullPath = Path.GetFullPath(customFile);
        lock (cacheGate)
        {
            if (currentRevisionByPath.TryGetValue(fullPath, out var revisionKey) &&
                cache.TryGetValue(revisionKey, out var entry) &&
                entry.Task.IsCompletedSuccessfully)
            {
                entry.LastAccess = ++cacheAccessSerial;
                sound = entry.Task.Result;
                return true;
            }
        }
        Volatile.Write(
            ref lastError,
            new InvalidOperationException("自定义 Key 音尚未预载，已拒绝用其他音色替代。"));
        return false;
    }

    internal static float ApplyOutputBoost(float gain, float maximumGain) =>
        Math.Clamp(gain, 0, maximumGain) * OutputBoost;

    private List<ResolvedBatchGroup> ResolveBatchGroups(
        ImmutableArray<WindowsKeySoundPlaybackTrigger> triggers,
        out int rejected)
    {
        var byIdentity = new Dictionary<ResolvedBatchKey, ResolvedBatchGroup>();
        rejected = 0;
        foreach (var trigger in triggers)
        {
            if (!TryResolveSoundWithoutIo(
                    trigger.Preset,
                    trigger.CustomFilePath,
                    out var sound))
            {
                rejected++;
                continue;
            }
            var key = new ResolvedBatchKey(
                sound,
                BitConverter.DoubleToInt64Bits(trigger.DelayMilliseconds));
            if (!byIdentity.TryGetValue(key, out var group))
            {
                group = new ResolvedBatchGroup(sound, trigger.DelayMilliseconds);
                byIdentity.Add(key, group);
            }
            group.Gain += trigger.Gain;
            group.TriggerCount++;
        }
        return [.. byIdentity.Values];
    }

    private async Task<CachedSound> DecodeCustomSoundAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        await customDecodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                    () => LoadSound(fullPath, cancellationToken),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            customDecodeGate.Release();
        }
    }

    private async Task ObserveCustomEntryCompletionAsync(CustomSoundCacheEntry entry)
    {
        try
        {
            var sound = await entry.Task.ConfigureAwait(false);
            _ = FinalizeCustomEntry(entry, sound);
        }
        catch
        {
            List<CustomSoundCacheEntry> removed = [];
            lock (cacheGate)
            {
                if (cache.TryGetValue(entry.RevisionKey, out var current) &&
                    ReferenceEquals(current, entry))
                    RemoveCacheEntryLocked(entry.RevisionKey, removed);
            }
            CancelEntries(removed);
        }
    }

    private bool FinalizeCustomEntry(CustomSoundCacheEntry entry, CachedSound sound)
    {
        var completedInfo = new FileInfo(entry.FullPath);
        if (!completedInfo.Exists ||
            !CreateRevisionKey(completedInfo).Equals(
                entry.RevisionKey,
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("Key 音文件在预载期间发生了变化，请重新预载。");

        List<CustomSoundCacheEntry> removed = [];
        lock (cacheGate)
        {
            if (Volatile.Read(ref disposed) != 0 ||
                !cache.TryGetValue(entry.RevisionKey, out var current) ||
                !ReferenceEquals(current, entry)) return false;
            if (!entry.SizeAccounted)
            {
                entry.PcmBytes = sound.PcmBytes;
                entry.SizeAccounted = true;
                cachedCustomSoundBytes += entry.PcmBytes;
            }
            entry.LastAccess = ++cacheAccessSerial;
            currentRevisionByPath[entry.FullPath] = entry.RevisionKey;
            TrimCustomCacheLocked(entry.RevisionKey, removed);
        }
        CancelEntries(removed);
        return true;
    }

    private static string CreateRevisionKey(FileInfo info) =>
        $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";

    private void TrimCustomCacheLocked(
        string? protectedRevisionKey,
        List<CustomSoundCacheEntry> removed)
    {
        while (cache.Count > MaximumCustomCacheEntries ||
               cachedCustomSoundBytes > MaximumCustomCacheBytes)
        {
            var candidate = cache.Values
                .Where(entry => entry.WaiterCount == 0 && entry.PinCount == 0 &&
                    (protectedRevisionKey == null || !entry.RevisionKey.Equals(
                        protectedRevisionKey,
                        StringComparison.OrdinalIgnoreCase)))
                .MinBy(entry => entry.LastAccess);
            if (candidate == null) break;
            RemoveCacheEntryLocked(candidate.RevisionKey, removed);
        }
    }

    private void ReleaseCustomWaiter(CustomSoundCacheEntry entry)
    {
        List<CustomSoundCacheEntry> removed = [];
        lock (cacheGate)
        {
            entry.WaiterCount = Math.Max(0, entry.WaiterCount - 1);
            if (entry.WaiterCount == 0 && !entry.Task.IsCompleted &&
                cache.TryGetValue(entry.RevisionKey, out var current) &&
                ReferenceEquals(current, entry))
                RemoveCacheEntryLocked(entry.RevisionKey, removed);
            else if (entry.WaiterCount == 0)
                TrimCustomCacheLocked(null, removed);
        }
        CancelEntries(removed);
    }

    internal void ReleasePlanPin(string revisionKey)
    {
        List<CustomSoundCacheEntry> removed = [];
        lock (cacheGate)
        {
            if (cache.TryGetValue(revisionKey, out var entry))
                entry.PinCount = Math.Max(0, entry.PinCount - 1);
            TrimCustomCacheLocked(null, removed);
        }
        CancelEntries(removed);
    }

    private void RemoveCacheEntryLocked(
        string revisionKey,
        List<CustomSoundCacheEntry> removed)
    {
        if (!cache.Remove(revisionKey, out var entry)) return;
        if (entry.SizeAccounted)
            cachedCustomSoundBytes = Math.Max(0, cachedCustomSoundBytes - entry.PcmBytes);
        if (currentRevisionByPath.TryGetValue(entry.FullPath, out var currentRevision) &&
            currentRevision.Equals(revisionKey, StringComparison.OrdinalIgnoreCase))
            currentRevisionByPath.Remove(entry.FullPath);
        removed.Add(entry);
    }

    private static void CancelEntries(IEnumerable<CustomSoundCacheEntry> entries)
    {
        foreach (var entry in entries) entry.Cancel();
    }

    private static ISampleProvider Delay(ISampleProvider provider, double delayMilliseconds)
    {
        if (delayMilliseconds <= .1) return provider;
        return new OffsetSampleProvider(provider)
        {
            DelayBy = TimeSpan.FromMilliseconds(Math.Clamp(delayMilliseconds, 0, 2_000))
        };
    }

    private IWavePlayer CreateOutput(IWaveProvider provider)
    {
        // NAudio posts PlaybackStopped to the SynchronizationContext current at
        // construction, and a trigger can build the output on the UI thread.
        // The stop handler belongs on the render thread itself: it restarts a
        // parked output the moment that thread lets go of the client, and a
        // fault has to be recorded before the next trigger looks, not whenever
        // the UI thread gets round to it.
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        IWavePlayer created;
        try
        {
            created = PlatformAudioOutputs.CreateOutput(
                provider,
                new PlatformAudioOutputRequest(
                    StandaloneOutputLatencyMilliseconds,
                    PrioritizeLatencyOverStability: true,
                    FallbackLatencyMilliseconds: 30));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        created.PlaybackStopped += OnOutputPlaybackStopped;
        return created;
    }

    private bool TryStartOutput(IWavePlayer activeOutput, out Exception? error)
    {
        try
        {
            lock (outputStateGate)
            {
                if (!ReferenceEquals(activeOutput, Volatile.Read(ref output)) ||
                    ReferenceEquals(faultedOutput, activeOutput))
                {
                    error = new InvalidOperationException("Key 音输出已停止。");
                    return false;
                }

                var state = activeOutput.PlaybackState;
                if (state == PlaybackState.Playing &&
                    mixer.EndOfStreamSerial == outputStartEndOfStreamSerial)
                {
                    // Running: the render thread takes the voice on its next period.
                    error = null;
                    return true;
                }
                if (state == PlaybackState.Playing ||
                    ReferenceEquals(outputAwaitingStopReport, activeOutput))
                {
                    // The mixer ended this run before the voice landed. NAudio's
                    // render thread then sleeps out one last period with
                    // PlaybackState still Playing, so Play() is a no-op, and
                    // reports Stopped a moment before it has released the
                    // client. Its stop handler starts the output again the
                    // instant it lets go. Tearing the device down here instead
                    // cost 30-70 ms on the input thread for every note that
                    // landed in that window.
                    restartPendingOutput = activeOutput;
                    error = null;
                    return true;
                }

                StartOutputLocked(activeOutput);
                if (activeOutput.PlaybackState == PlaybackState.Playing)
                {
                    error = null;
                    return true;
                }
                error = new InvalidOperationException("Key 音输出未进入播放状态。");
                return false;
            }
        }
        catch (Exception exception)
        {
            error = exception;
            return false;
        }
    }

    /// <summary>
    /// Caller holds <see cref="outputStateGate"/>. Priming matters: a voice
    /// still inside its delay would otherwise meet a mixer that ends the stream
    /// on the first read, and a WasapiOut whose first read is empty never
    /// leaves Playing again.
    /// </summary>
    private void StartOutputLocked(IWavePlayer activeOutput)
    {
        outputStartEndOfStreamSerial = mixer.EndOfStreamSerial;
        mixer.PrimeIdlePeriod();
        outputAwaitingStopReport = activeOutput;
        restartPendingOutput = null;
        activeOutput.Play();
    }

    private bool IsOutputRunningOrRestarting(IWavePlayer activeOutput)
    {
        lock (outputStateGate)
        {
            return ReferenceEquals(activeOutput, Volatile.Read(ref output)) &&
                   !ReferenceEquals(faultedOutput, activeOutput) &&
                   (activeOutput.PlaybackState == PlaybackState.Playing ||
                    ReferenceEquals(restartPendingOutput, activeOutput));
        }
    }

    /// <summary>
    /// Whether the output stopped because this service let the mixer run out of
    /// voices, rather than because the endpoint failed. The mixer parks an idle
    /// output by returning end of stream, which any rest longer than the
    /// scheduler's look-ahead produces and every pause produces; the device
    /// itself stays initialized and starts again in well under a millisecond.
    /// An endpoint that actually died stops without the mixer having ended, and
    /// is rebuilt as before. Caller holds <see cref="outputStateGate"/>.
    /// </summary>
    private bool IsRestartableIdleStop(PlaybackState state) =>
        state == PlaybackState.Stopped && outputHasStarted &&
        mixer.EndOfStreamSerial != outputStartEndOfStreamSerial;

    /// <summary>
    /// Whether the next voice can go to <paramref name="existing"/> without a
    /// rebuild: running, still parking after an idle stop (TryStartOutput
    /// restarts it once parked), parked, or built and not yet started.
    /// </summary>
    private bool IsOutputReusable(IWavePlayer existing, out bool faulted)
    {
        lock (outputStateGate)
        {
            faulted = ReferenceEquals(faultedOutput, existing);
            // A default-endpoint change leaves this output on the former
            // device: new headphones stayed silent while Key sounds went on
            // playing through the speakers.
            if (faulted || outputEndpointSerial != Volatile.Read(ref defaultEndpointSerial))
                return false;
            var state = existing.PlaybackState;
            if (state == PlaybackState.Playing) return true;
            if (state != PlaybackState.Stopped) return false;
            return !outputHasStarted || IsRestartableIdleStop(state);
        }
    }

    private IWavePlayer? GetOrCreateOutputForTrigger(out Exception? error)
    {
        var existing = Volatile.Read(ref output);
        if (existing != null)
        {
            var faulted = false;
            try
            {
                if (IsOutputReusable(existing, out faulted))
                {
                    error = null;
                    return existing;
                }
            }
            catch
            {
                // Recreate an endpoint whose state can no longer be queried.
            }
            DisposeOutputLocked(existing);
            // Voices queued after the render thread died were never mixed.
            // Played now on the replacement they would land arbitrarily late.
            if (faulted) mixer.DiscardPending();
        }

        try
        {
            mixer.PrimeIdlePeriod();
            var endpointSerial = Volatile.Read(ref defaultEndpointSerial);
            var created = CreateOutput(localVolume.ToWaveProvider());
            PublishOutputLocked(created, endpointSerial);
            error = null;
            return created;
        }
        catch (Exception exception)
        {
            error = exception;
            return null;
        }
    }

    private void PublishOutputLocked(IWavePlayer created, int endpointSerial)
    {
        lock (outputStateGate)
        {
            faultedOutput = null;
            restartPendingOutput = null;
            outputAwaitingStopReport = null;
            outputEndpointSerial = endpointSerial;
            Volatile.Write(ref output, created);
        }
        outputHasStarted = false;
    }

    private void DisposeOutputLocked(IWavePlayer activeOutput)
    {
        lock (outputStateGate)
        {
            if (ReferenceEquals(activeOutput, Volatile.Read(ref output)))
                Volatile.Write(ref output, null);
            if (ReferenceEquals(faultedOutput, activeOutput)) faultedOutput = null;
            if (ReferenceEquals(restartPendingOutput, activeOutput)) restartPendingOutput = null;
            if (ReferenceEquals(outputAwaitingStopReport, activeOutput))
                outputAwaitingStopReport = null;
        }
        outputHasStarted = false;
        activeOutput.PlaybackStopped -= OnOutputPlaybackStopped;
        try { activeOutput.Stop(); } catch { }
        try { activeOutput.Dispose(); } catch { }
    }

    /// <summary>
    /// Raised on the output's render thread as it exits. Detached outputs are
    /// ignored: DisposeOutputLocked clears <see cref="output"/> under the same
    /// gate before it stops one.
    /// </summary>
    private void OnOutputPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (sender is not IWavePlayer stopped) return;
        lock (outputStateGate)
        {
            if (!ReferenceEquals(stopped, Volatile.Read(ref output))) return;
            if (ReferenceEquals(outputAwaitingStopReport, stopped)) outputAwaitingStopReport = null;
            var restart = ReferenceEquals(restartPendingOutput, stopped);
            if (restart) restartPendingOutput = null;
            if (e.Exception != null)
            {
                // The endpoint failed underneath the render thread. The next
                // trigger or warm-up rebuilds on whatever the default is now.
                faultedOutput = stopped;
                return;
            }
            if (!restart) return;
            try
            {
                if (stopped.PlaybackState == PlaybackState.Stopped) StartOutputLocked(stopped);
            }
            catch
            {
                faultedOutput = stopped;
            }
        }
    }

    private async Task EnsureEndpointNotificationsAsync()
    {
        // The Windows check is load bearing: constructing MMDeviceEnumerator
        // aborts the process on iOS (see WindowsAudioDeviceEngine). It is made
        // on a pool thread so the COM object never belongs to the UI apartment,
        // which DisposeAsync may be blocking.
        if (!OperatingSystem.IsWindows() || endpointNotificationsAttempted) return;
        endpointNotificationsAttempted = true;
        await Task.Run(RegisterEndpointNotifications, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private void RegisterEndpointNotifications()
    {
        MMDeviceEnumerator? created = null;
        try
        {
            created = new MMDeviceEnumerator();
            if (created.RegisterEndpointNotificationCallback(endpointNotifications) != 0) return;
            endpointEnumerator = created;
            created = null;
        }
        catch
        {
            // Remote and virtual sessions can expose audio without
            // notifications; the output then keeps its endpoint, as before.
        }
        finally
        {
            created?.Dispose();
        }
    }

    private void NotifyDefaultEndpointChanged(DataFlow flow, Role role)
    {
        if (Volatile.Read(ref disposed) != 0 || flow != DataFlow.Render ||
            role is not (Role.Console or Role.Multimedia)) return;
        // Only marks the output stale. The COM callback must return promptly
        // and cannot take the output gate; the next trigger or warm-up
        // rebuilds on the new default endpoint.
        Interlocked.Increment(ref defaultEndpointSerial);
    }

    private static CachedSound LoadSound(string path, CancellationToken cancellationToken)
    {
        using var source = AudioDecoder.OpenWaveStream(path);
        ISampleProvider samples = source.ToSampleProvider();
        if (samples.WaveFormat.SampleRate != SampleRate)
            samples = new WdlResamplingSampleProvider(samples, SampleRate);
        samples = new StereoProvider(samples);
        var maximum = SampleRate * 2 * MaximumCustomSoundSeconds;
        var buffer = new float[SampleRate * 2];
        var data = new List<float>(Math.Min(maximum, SampleRate * 4));
        while (data.Count < maximum)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = samples.Read(buffer, 0, Math.Min(buffer.Length, maximum - data.Count));
            if (read <= 0) break;
            data.AddRange(buffer.AsSpan(0, read).ToArray());
        }
        return new CachedSound(TrimLeadingSilence(data.ToArray()), MixFormat);
    }

    private static float[] TrimLeadingSilence(float[] samples)
    {
        if (samples.Length < 4) return samples;
        var peak = 0f;
        for (var index = 0; index < samples.Length; index++)
            peak = Math.Max(peak, Math.Abs(samples[index]));
        if (peak < .001f) return samples;

        var threshold = Math.Max(.0015f, peak * .015f);
        var maximumScanFrames = Math.Min(samples.Length / 2, SampleRate / 2);
        var onsetFrame = 0;
        for (; onsetFrame < maximumScanFrames; onsetFrame++)
        {
            var offset = onsetFrame * 2;
            if (Math.Max(Math.Abs(samples[offset]), Math.Abs(samples[offset + 1])) >= threshold)
                break;
        }
        if (onsetFrame >= maximumScanFrames) return samples;
        var startFrame = Math.Max(0, onsetFrame - (int)(SampleRate * .002));
        return startFrame == 0 ? samples : samples[(startFrame * 2)..];
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        _ = scheduledPlaybackEpoch.Advance();
        lifetime.Cancel();
        List<CustomSoundCacheEntry> cacheEntries;
        lock (cacheGate)
        {
            cacheEntries = [.. cache.Values];
            cache.Clear();
            currentRevisionByPath.Clear();
            cachedCustomSoundBytes = 0;
        }
        CancelEntries(cacheEntries);

        await outputGate.WaitAsync().ConfigureAwait(false);
        try
        {
            IWavePlayer? activeOutput;
            lock (outputStateGate)
            {
                activeOutput = Interlocked.Exchange(ref output, null);
                faultedOutput = null;
                restartPendingOutput = null;
                outputAwaitingStopReport = null;
            }
            outputHasStarted = false;
            if (activeOutput != null)
            {
                activeOutput.PlaybackStopped -= OnOutputPlaybackStopped;
                await Task.Run(() =>
                {
                    try { activeOutput.Stop(); } catch { }
                    try { activeOutput.Dispose(); } catch { }
                }).ConfigureAwait(false);
            }
            var enumerator = endpointEnumerator;
            endpointEnumerator = null;
            if (enumerator != null)
            {
                await Task.Run(() =>
                {
                    try { enumerator.UnregisterEndpointNotificationCallback(endpointNotifications); } catch { }
                    try { enumerator.Dispose(); } catch { }
                }).ConfigureAwait(false);
            }
            mixer.Complete();
        }
        finally
        {
            outputGate.Release();
            outputGate.Dispose();
        }

        try
        {
            await Task.WhenAll(cacheEntries.Select(entry => entry.Task)).ConfigureAwait(false);
        }
        catch
        {
            // Cancellation/faulted custom samples have already been detached.
        }
        foreach (var entry in cacheEntries) entry.DisposeCancellation();
        // Evicted decode tasks are canceled but may still be unwinding their
        // decoder finally blocks. Let the managed semaphore be collected with
        // the service instead of disposing it underneath a late Release().
        lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    internal sealed record CachedSound(float[] Samples, WaveFormat Format)
    {
        public long PcmBytes => Samples.LongLength * sizeof(float);
    }

    private readonly record struct ResolvedBatchKey(CachedSound Sound, long DelayBits);

    private sealed class ResolvedBatchGroup(
        CachedSound sound,
        double delayMilliseconds)
    {
        public CachedSound Sound { get; } = sound;
        public double DelayMilliseconds { get; } = delayMilliseconds;
        public float Gain { get; set; }
        public int TriggerCount { get; set; }
    }

    private sealed class CustomSoundCacheEntry(
        string fullPath,
        string revisionKey,
        CancellationTokenSource cancellation,
        Task<CachedSound> task)
    {
        private int cancellationReleaseScheduled;
        public string FullPath { get; } = fullPath;
        public string RevisionKey { get; } = revisionKey;
        public Task<CachedSound> Task { get; } = task;
        public int WaiterCount { get; set; }
        public int PinCount { get; set; }
        public long LastAccess { get; set; }
        public long PcmBytes { get; set; }
        public bool SizeAccounted { get; set; }

        public void Cancel()
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Concurrent disposal already released the cancellation source.
            }
            _ = Task.ContinueWith(
                _ => DisposeCancellation(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public void DisposeCancellation()
        {
            if (Interlocked.Exchange(ref cancellationReleaseScheduled, 1) == 0)
                cancellation.Dispose();
        }
    }

    internal sealed class QueuedCachedSoundMixer(WaveFormat format) : ISampleProvider
    {
        // A healthy render thread drains the queue every device period. The
        // bound only matters once nothing drains it, and turns a dead output
        // into rejected triggers instead of memory that grows with every note.
        internal const int MaximumPendingVoices = 256;

        private readonly ConcurrentQueue<Voice> pending = new();
        private readonly List<Voice> active = [];
        private readonly object completionGate = new();
        private volatile bool terminalSilenceReturned;
        private volatile bool endOfStreamReturned;
        private volatile bool completed;
        private int pendingCount;
        private long endOfStreamSerial;

        public WaveFormat WaveFormat { get; } = format;

        public bool TryEnqueue(
            CachedSound sound,
            float gain,
            double delayMilliseconds,
            AudioPlaybackEpochToken? scheduledEpoch,
            bool softLimit = false)
        {
            lock (completionGate)
            {
                if (completed || Volatile.Read(ref pendingCount) >= MaximumPendingVoices)
                    return false;
                terminalSilenceReturned = false;
                endOfStreamReturned = false;
                var delayFrames = (int)Math.Round(
                    Math.Clamp(delayMilliseconds, 0, 2_000) * SampleRate / 1_000d);
                pending.Enqueue(new Voice(
                    sound,
                    gain,
                    delayFrames * WaveFormat.Channels,
                    scheduledEpoch,
                    softLimit));
                Interlocked.Increment(ref pendingCount);
                return true;
            }
        }

        public void Complete()
        {
            lock (completionGate) completed = true;
        }

        public bool EndOfStreamReturned => endOfStreamReturned;

        public bool IsBacklogged => Volatile.Read(ref pendingCount) >= MaximumPendingVoices;

        /// <summary>
        /// Counts the reads that ended the stream. A caller that enqueued a
        /// voice and then sees this move knows the render thread returned end
        /// of stream without it, and must start the output again.
        /// </summary>
        public long EndOfStreamSerial => Interlocked.Read(ref endOfStreamSerial);

        public void PrimeIdlePeriod()
        {
            terminalSilenceReturned = false;
            endOfStreamReturned = false;
        }

        public void DiscardPending()
        {
            while (pending.TryDequeue(out _)) Interlocked.Decrement(ref pendingCount);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (completed) return 0;
            Array.Clear(buffer, offset, count);
            while (pending.TryDequeue(out var voice))
            {
                Interlocked.Decrement(ref pendingCount);
                active.Add(voice);
            }
            var requiresSoftLimit = false;
            var voiceProcessed = false;
            for (var voiceIndex = active.Count - 1; voiceIndex >= 0; voiceIndex--)
            {
                var voice = active[voiceIndex];
                var contributed = voice.TryMix(buffer, offset, count);
                voiceProcessed |= contributed;
                if (contributed && voice.SoftLimit) requiresSoftLimit = true;
                if (!contributed || voice.Position >= voice.Sound.Samples.Length)
                    active.RemoveAt(voiceIndex);
            }
            if (requiresSoftLimit)
                WindowsKeySoundSoftLimiter.Process(buffer, offset, count);
            if (voiceProcessed || active.Count > 0 || !pending.IsEmpty)
            {
                terminalSilenceReturned = false;
                endOfStreamReturned = false;
                return count;
            }
            if (terminalSilenceReturned)
            {
                lock (completionGate)
                {
                    // Decided under the enqueue lock. Otherwise a voice could
                    // land between the empty check above and this return: it
                    // cleared the idle flags, the render thread still ended the
                    // stream, and the voice sat in the queue until some later
                    // note restarted the output and played it late.
                    if (pending.IsEmpty)
                    {
                        endOfStreamReturned = true;
                        Interlocked.Increment(ref endOfStreamSerial);
                        return 0;
                    }
                }

                // The buffer is already silent; the new voice mixes next period.
                terminalSilenceReturned = false;
                endOfStreamReturned = false;
                return count;
            }

            // Ending an infinite low-latency provider on the same callback that
            // consumed its final voice can leave a short non-zero device buffer
            // repeating. Submit one explicit silent period before EOF; the next
            // trigger restarts the already initialized output.
            terminalSilenceReturned = true;
            endOfStreamReturned = false;
            return count;
        }

        private sealed class Voice(
            CachedSound sound,
            float gain,
            int delaySamples,
            AudioPlaybackEpochToken? scheduledEpoch,
            bool softLimit)
        {
            public CachedSound Sound { get; } = sound;
            public float Gain { get; } = gain;
            public bool SoftLimit { get; } = softLimit;
            public int DelaySamples { get; set; } = delaySamples;
            public int Position { get; set; }

            public bool TryMix(float[] buffer, int offset, int count)
            {
                if (scheduledEpoch.HasValue && !scheduledEpoch.Value.IsCurrent) return false;

                var nextDelay = DelaySamples;
                var targetOffset = offset;
                var targetCount = count;
                if (nextDelay > 0)
                {
                    var skipped = Math.Min(targetCount, nextDelay);
                    nextDelay -= skipped;
                    targetOffset += skipped;
                    targetCount -= skipped;
                }

                var available = Sound.Samples.Length - Position;
                var mixed = Math.Min(available, targetCount);
                for (var sample = 0; sample < mixed; sample++)
                    buffer[targetOffset + sample] += Sound.Samples[Position + sample] * Gain;

                if (scheduledEpoch.HasValue && !scheduledEpoch.Value.IsCurrent)
                {
                    // Remove only this stale voice's contribution. Audition voices
                    // and voices from the new playback epoch remain untouched.
                    for (var sample = 0; sample < mixed; sample++)
                        buffer[targetOffset + sample] -= Sound.Samples[Position + sample] * Gain;
                    return false;
                }

                DelaySamples = nextDelay;
                Position += mixed;
                return true;
            }
        }
    }

    private sealed class CachedSoundProvider(CachedSound sound, float gain) : ISampleProvider
    {
        private int position;
        public WaveFormat WaveFormat => sound.Format;

        public int Read(float[] buffer, int offset, int count)
        {
            var available = sound.Samples.Length - position;
            var read = Math.Min(available, count);
            for (var index = 0; index < read; index++)
                buffer[offset + index] = sound.Samples[position + index] * gain;
            position += read;
            return read;
        }
    }

    private sealed class KeySoundEndpointNotificationClient(WindowsKeySoundService owner)
        : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) =>
            owner.NotifyDefaultEndpointChanged(flow, role);

        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    private sealed class StereoProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly int channels;
        private float[] sourceBuffer = [];

        public StereoProvider(ISampleProvider source)
        {
            this.source = source;
            channels = source.WaveFormat.Channels;
            WaveFormat = MixFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var frames = count / 2;
            var sourceCount = frames * channels;
            if (sourceBuffer.Length < sourceCount) Array.Resize(ref sourceBuffer, sourceCount);
            var read = source.Read(sourceBuffer, 0, sourceCount);
            var readFrames = read / channels;
            for (var frame = 0; frame < readFrames; frame++)
            {
                var sourceOffset = frame * channels;
                buffer[offset + frame * 2] = sourceBuffer[sourceOffset];
                buffer[offset + frame * 2 + 1] = channels == 1
                    ? sourceBuffer[sourceOffset]
                    : sourceBuffer[sourceOffset + 1];
            }
            return readFrames * 2;
        }
    }
}
