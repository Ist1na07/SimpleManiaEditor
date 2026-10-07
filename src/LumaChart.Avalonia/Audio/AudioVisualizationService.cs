using NAudio.Dsp;
using NAudio.Wave;

namespace LumaChart.Avalonia.Audio;

public sealed record AudioWaveform(
    double BucketMilliseconds,
    float[] Peaks,
    double DurationMilliseconds)
{
    public int Count => Peaks.Length;
}

/// <param name="FrameOffsetMilliseconds">
/// Audio time that frame 0 describes. Each frame is an FFT over a window of
/// samples, and its content belongs to the window's centre, not its start;
/// placing frames at their start drew every onset half a window (about 23 ms
/// at 44.1 kHz) earlier than the Notes and the waveform.
/// </param>
/// <param name="BandLevelHistograms">
/// When present, <see cref="Intensities"/> hold raw levels (see
/// <see cref="SpectrogramLevelMapping"/>) and this holds 256 counts per band;
/// <see cref="DisplayLookup"/> turns them into display intensities for the
/// chosen amount of detail. When absent the intensities are already display
/// values.
/// </param>
public sealed record AudioSpectrogram(
    double FrameMilliseconds,
    int FrequencyBands,
    byte[] Intensities,
    double DurationMilliseconds,
    double MinimumFrequency,
    double MaximumFrequency,
    double FrameOffsetMilliseconds = 0,
    long[]? BandLevelHistograms = null)
{
    private readonly object lookupGate = new();
    private double cachedLookupDetail = double.NaN;
    private byte[]? cachedLookup;

    public double FrameIndexAt(double milliseconds) =>
        (milliseconds - FrameOffsetMilliseconds) / FrameMilliseconds;

    public int FrameCount => FrequencyBands <= 0 ? 0 : Intensities.Length / FrequencyBands;
    public byte this[int frame, int band] => Intensities[frame * FrequencyBands + band];

    /// <summary>
    /// Per-band table (<c>band * 256 + level</c>) from stored level to display
    /// intensity for <paramref name="detail"/> (0 = only the strongest onsets,
    /// 1 = everything above the noise floor), or null when the intensities are
    /// already display values.
    /// </summary>
    public byte[]? DisplayLookup(double detail)
    {
        if (BandLevelHistograms == null) return null;
        detail = SpectrogramLevelMapping.NormalizeDetail(detail);
        lock (lookupGate)
        {
            if (cachedLookup != null && Math.Abs(cachedLookupDetail - detail) < 1e-9) return cachedLookup;
            cachedLookup = SpectrogramLevelMapping.BuildLookup(BandLevelHistograms, FrequencyBands, detail);
            cachedLookupDetail = detail;
            return cachedLookup;
        }
    }
}

/// <summary>
/// Maps stored spectrum levels to display intensities for a chosen amount of
/// detail. Levels are decibels in half-dB steps from -120 dB (0) to +7.5 dB
/// (255), so the mapping can change instantly without decoding the audio again.
///
/// A fixed dB range made loudly mastered songs one flat wash and quiet songs
/// nearly invisible, so the ceiling and floor come from the track's own level
/// distribution. At low detail each band additionally fades out below its own
/// typical level, which drops sustained pads and a loud mix bus and leaves the
/// onsets and drum hits; at high detail that per-band floor is gone and quiet
/// content is lifted, so harmonics, vocals and tails stay visible.
/// </summary>
public static class SpectrogramLevelMapping
{
    public const double DefaultDetail = .6;
    public const double DefaultBackgroundStrength = .85;
    public const double QuantizeFloorDecibels = -120;
    public const double QuantizeStepsPerDecibel = 2;
    private const double CeilingPercentile = .997;
    private const double MinimumCeilingDecibels = -60;
    private const double MinimumRangeDecibels = 35;
    private const double MinimumBandVisibleRangeDecibels = 12;

    public static double NormalizeDetail(double detail) =>
        double.IsFinite(detail) ? Math.Clamp(detail, 0, 1) : DefaultDetail;

    public static byte Quantize(double decibels) =>
        (byte)Math.Clamp(Math.Round((decibels - QuantizeFloorDecibels) * QuantizeStepsPerDecibel), 0, 255);

    public static double DecibelsOf(int level) => QuantizeFloorDecibels + level / QuantizeStepsPerDecibel;

    public static byte[] BuildLookup(long[] bandHistograms, int bands, double detail)
    {
        ArgumentNullException.ThrowIfNull(bandHistograms);
        detail = NormalizeDetail(detail);
        bands = Math.Max(1, Math.Min(bands, bandHistograms.Length / 256));
        var lookup = new byte[bands * 256];
        var global = new long[256];
        for (var index = 0; index < bands * 256; index++) global[index & 255] += bandHistograms[index];
        long total = 0;
        foreach (var count in global) total += count;
        if (total == 0) return lookup;

        var globalFloorPercentile = Lerp(.72, .2, detail);
        var bandFloorPercentile = Lerp(.85, 0, detail);
        var maximumRange = Lerp(50, 85, detail);
        var gamma = Lerp(1, .7, detail);
        var ceiling = Math.Max(
            DecibelsOf(PercentileLevel(global, 0, total, CeilingPercentile)),
            MinimumCeilingDecibels);
        var globalFloor = Math.Clamp(
            DecibelsOf(PercentileLevel(global, 0, total, globalFloorPercentile)),
            ceiling - maximumRange,
            ceiling - MinimumRangeDecibels);
        for (var band = 0; band < bands; band++)
        {
            long bandTotal = 0;
            for (var level = 0; level < 256; level++) bandTotal += bandHistograms[band * 256 + level];
            var floor = bandTotal == 0 || bandFloorPercentile < .01
                ? globalFloor
                : Math.Clamp(
                    DecibelsOf(PercentileLevel(bandHistograms, band * 256, bandTotal, bandFloorPercentile)),
                    globalFloor,
                    ceiling - MinimumBandVisibleRangeDecibels);
            for (var level = 0; level < 256; level++)
            {
                var normalized = Math.Clamp((DecibelsOf(level) - floor) / (ceiling - floor), 0, 1);
                lookup[band * 256 + level] = (byte)Math.Round(Math.Pow(normalized, gamma) * 255);
            }
        }
        return lookup;
    }

    private static int PercentileLevel(long[] histogram, int start, long total, double percentile)
    {
        var target = (long)Math.Ceiling(total * percentile);
        long seen = 0;
        for (var level = 0; level < 256; level++)
        {
            seen += histogram[start + level];
            if (seen >= target) return level;
        }
        return 255;
    }

    private static double Lerp(double from, double to, double amount) => from + (to - from) * amount;
}

public sealed record AudioVisualization(
    AudioWaveform? Waveform,
    AudioSpectrogram? Spectrogram);

/// <summary>
/// Revision-keyed waveform/spectrum decoder. Shared callers await one decode;
/// caller cancellation only removes that waiter, while revision replacement,
/// eviction, or the final waiter leaving cancels obsolete decoder work.
/// </summary>
public static class AudioWaveformService
{
    private const double DesiredBucketMilliseconds = 2;
    private const int MaximumBuckets = 2_000_000;
    private const int MaximumCachedVisualizations = 4;
    // One maximum-size visualization is about 25 MiB (8 MiB peaks plus
    // 17 MiB spectrum). 128 MiB keeps four full WPF-parity entries without the
    // old 24 MiB ceiling immediately evicting every neighbouring tab.
    private const long MaximumCachedBytes = 128L * 1024 * 1024;
    private const int SpectrogramFftSize = 2048;
    private const int SpectrogramFftExponent = 11;
    private const int SpectrogramBands = 96;
    private const int DesiredSpectrogramHop = 512;
    private const int MaximumSpectrogramFrames = 180_000;
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim DecodeGate = new(1, 1);
    private static long accessSerial;
    private static long cachedBytes;

    internal static (int Entries, long Bytes) CacheDiagnostics
    {
        get
        {
            lock (CacheGate) return (Cache.Count, cachedBytes);
        }
    }

    internal static (int MaximumBuckets, int MaximumSpectrogramFrames, long MaximumCachedBytes)
        CapacityDiagnostics => (MaximumBuckets, MaximumSpectrogramFrames, MaximumCachedBytes);

    public static async Task<AudioWaveform?> LoadAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        (await LoadVisualizationAsync(path, cancellationToken).ConfigureAwait(false)).Waveform;

    public static async Task<AudioVisualization> LoadVisualizationAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("找不到音频文件。", info.FullName);
        var key = CreateRevisionKey(info);
        CacheEntry entry;
        List<CacheEntry> removed = [];
        lock (CacheGate)
        {
            foreach (var stale in Cache.Values
                         .Where(item => item.FullPath.Equals(
                             info.FullName,
                             StringComparison.OrdinalIgnoreCase) &&
                             !item.RevisionKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                         .ToArray())
                RemoveEntryLocked(stale.RevisionKey, removed);

            if (!Cache.TryGetValue(key, out entry!))
            {
                entry = new CacheEntry(info.FullName, key);
                Cache.Add(key, entry);
            }
            entry.WaiterCount++;
            entry.LastAccess = ++accessSerial;
            TrimCacheLocked(key, removed);
        }
        CancelEntries(removed);

        var task = entry.Task;
        entry.EnsureObserved(task);
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseWaiter(entry);
        }
    }

    public static void ClearCache()
    {
        List<CacheEntry> removed;
        lock (CacheGate)
        {
            removed = [.. Cache.Values];
            Cache.Clear();
            cachedBytes = 0;
        }
        CancelEntries(removed);
    }

    private static string CreateRevisionKey(FileInfo info) =>
        $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";

    private static void ReleaseWaiter(CacheEntry entry)
    {
        List<CacheEntry> removed = [];
        lock (CacheGate)
        {
            entry.WaiterCount = Math.Max(0, entry.WaiterCount - 1);
            if (entry.WaiterCount == 0 && !entry.IsDecodeCompleted &&
                Cache.TryGetValue(entry.RevisionKey, out var current) &&
                ReferenceEquals(current, entry))
                RemoveEntryLocked(entry.RevisionKey, removed);
            else if (entry.WaiterCount == 0)
                TrimCacheLocked(null, removed);
        }
        CancelEntries(removed);
    }

    private static async Task ObserveEntryAsync(CacheEntry entry, Task<AudioVisualization> task)
    {
        List<CacheEntry> removed = [];
        try
        {
            var result = await task.ConfigureAwait(false);
            var size = VisualizationBytes(result);
            lock (CacheGate)
            {
                if (!Cache.TryGetValue(entry.RevisionKey, out var current) ||
                    !ReferenceEquals(current, entry)) return;
                if (!entry.SizeAccounted)
                {
                    entry.SizeBytes = size;
                    entry.SizeAccounted = true;
                    cachedBytes += size;
                }
                entry.LastAccess = ++accessSerial;
                TrimCacheLocked(entry.RevisionKey, removed);
            }
        }
        catch
        {
            lock (CacheGate)
            {
                if (Cache.TryGetValue(entry.RevisionKey, out var current) &&
                    ReferenceEquals(current, entry))
                    RemoveEntryLocked(entry.RevisionKey, removed);
            }
        }
        finally
        {
            CancelEntries(removed);
            entry.DisposeCancellation();
        }
    }

    private static long VisualizationBytes(AudioVisualization visualization) =>
        (visualization.Waveform?.Peaks.LongLength ?? 0) * sizeof(float) +
        (visualization.Spectrogram?.Intensities.LongLength ?? 0) +
        (visualization.Spectrogram?.BandLevelHistograms?.LongLength ?? 0) * sizeof(long);

    private static void TrimCacheLocked(string? protectedKey, List<CacheEntry> removed)
    {
        while (Cache.Count > MaximumCachedVisualizations || cachedBytes > MaximumCachedBytes)
        {
            var candidate = Cache.Values
                .Where(entry => entry.WaiterCount == 0 &&
                    (protectedKey == null || !entry.RevisionKey.Equals(
                        protectedKey,
                        StringComparison.OrdinalIgnoreCase)))
                .MinBy(entry => entry.LastAccess);
            if (candidate == null) break;
            RemoveEntryLocked(candidate.RevisionKey, removed);
        }
    }

    private static void RemoveEntryLocked(string key, List<CacheEntry> removed)
    {
        if (!Cache.Remove(key, out var entry)) return;
        if (entry.SizeAccounted) cachedBytes = Math.Max(0, cachedBytes - entry.SizeBytes);
        removed.Add(entry);
    }

    private static void CancelEntries(IEnumerable<CacheEntry> entries)
    {
        foreach (var entry in entries) entry.Cancel();
    }

    private static async Task<AudioVisualization> DecodeQueuedAsync(
        string path,
        string revisionKey,
        CancellationToken cancellationToken)
    {
        await DecodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A long MP3 can require hundreds of thousands of FFT windows. Running
            // that work on a normal ThreadPool worker competes directly with
            // WASAPI callbacks and Avalonia's render work. Use a dedicated
            // below-normal worker so visualization loading yields to audible
            // playback while retaining the same WPF-compatible output.
            var visualization = await Task.Factory.StartNew(
                    () =>
                    {
                        try
                        {
                            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                        }
                        catch (ThreadStateException)
                        {
                            // The decode remains correct if a restricted host
                            // refuses the optional scheduling hint.
                        }
                        return Decode(path, cancellationToken);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default)
                .ConfigureAwait(false);
            var completedInfo = new FileInfo(path);
            if (!completedInfo.Exists ||
                !CreateRevisionKey(completedInfo).Equals(
                    revisionKey,
                    StringComparison.OrdinalIgnoreCase))
                throw new IOException("音频文件在可视化解码期间发生了变化，请重新载入。");
            return visualization;
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    private static AudioVisualization Decode(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = AudioDecoder.OpenWaveStream(path);
        var provider = LengthCappedSampleProvider.FromWaveStream(stream);
        var durationMilliseconds = Math.Max(1, stream.TotalTime.TotalMilliseconds);
        var sampleRate = provider.WaveFormat.SampleRate;
        var totalFrames = Math.Max(1d, durationMilliseconds / 1000d * sampleRate);
        var precisionFrames = (int)Math.Ceiling(
            sampleRate * DesiredBucketMilliseconds / 1000d);
        var capacityFrames = (int)Math.Ceiling(totalFrames / MaximumBuckets);
        var framesPerBucket = Math.Max(32, Math.Max(precisionFrames, capacityFrames));
        var bucketMilliseconds = framesPerBucket * 1000d / sampleRate;
        var expectedBuckets = Math.Max(
            1,
            (int)Math.Min(
                MaximumBuckets,
                Math.Ceiling(totalFrames / framesPerBucket)));
        var peaks = GC.AllocateUninitializedArray<float>(expectedBuckets);
        var peakCount = 0;
        var channels = provider.WaveFormat.Channels;
        var buffer = new float[Math.Max(16_384, framesPerBucket * channels)];
        var frames = 0;
        var peak = 0f;

        var spectrogramHop = Math.Max(
            DesiredSpectrogramHop,
            (int)Math.Ceiling(totalFrames / MaximumSpectrogramFrames));
        var spectrogramFrameMilliseconds = spectrogramHop * 1000d / sampleRate;
        var expectedSpectrumFrames = Math.Max(
            1,
            (int)Math.Min(
                MaximumSpectrogramFrames,
                Math.Ceiling(totalFrames / spectrogramHop)));
        var spectrumRows = GC.AllocateUninitializedArray<byte>(
            expectedSpectrumFrames * SpectrogramBands);
        var spectrumFrameCount = 0;
        var ring = new float[SpectrogramFftSize];
        var ringPosition = 0;
        var ringCount = 0;
        long processedFrames = 0;
        long nextSpectrumEnd = SpectrogramFftSize;
        var fftBuffer = new Complex[SpectrogramFftSize];
        var fftWindow = BuildFftWindow();
        var bandRanges = BuildBandRanges(sampleRate);
        var spectrumHistogram = new long[SpectrogramBands * 256];

        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var sample = 0; sample < read; sample += channels)
            {
                var framePeak = 0f;
                var mono = 0f;
                var frameChannels = 0;
                for (var channel = 0; channel < channels && sample + channel < read; channel++)
                {
                    var value = buffer[sample + channel];
                    // Math.Max propagates NaN, so one bad decoded sample used to
                    // turn a whole bucket peak, and every FFT row whose window
                    // it entered, into NaN. Full scale is also the most the
                    // waveform can draw.
                    value = float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f;
                    framePeak = Math.Max(framePeak, Math.Abs(value));
                    mono += value;
                    frameChannels++;
                }
                if (frameChannels > 0) mono /= frameChannels;
                peak = Math.Max(peak, framePeak);
                ring[ringPosition] = mono;
                ringPosition = (ringPosition + 1) % SpectrogramFftSize;
                ringCount = Math.Min(SpectrogramFftSize, ringCount + 1);
                processedFrames++;
                if (ringCount == SpectrogramFftSize && processedFrames >= nextSpectrumEnd)
                {
                    if (spectrumFrameCount < expectedSpectrumFrames)
                    {
                        AppendSpectrumRow(
                            ring,
                            ringPosition,
                            fftBuffer,
                            fftWindow,
                            bandRanges,
                            spectrumRows,
                            spectrumFrameCount++,
                            spectrumHistogram);
                    }
                    nextSpectrumEnd += spectrogramHop;
                }
                if (++frames < framesPerBucket) continue;
                if (peakCount < peaks.Length) peaks[peakCount++] = Math.Clamp(peak, 0, 1);
                frames = 0;
                peak = 0;
            }
        }

        if (frames > 0 && peakCount < peaks.Length)
            peaks[peakCount++] = Math.Clamp(peak, 0, 1);
        if (spectrumFrameCount == 0 && ringCount > 0)
        {
            AppendSpectrumRow(
                ring,
                ringPosition,
                fftBuffer,
                fftWindow,
                bandRanges,
                spectrumRows,
                spectrumFrameCount++,
                spectrumHistogram,
                ringCount);
        }

        if (peakCount != peaks.Length) Array.Resize(ref peaks, peakCount);
        var spectrumBytes = spectrumFrameCount * SpectrogramBands;
        if (spectrumBytes != spectrumRows.Length) Array.Resize(ref spectrumRows, spectrumBytes);
        var waveform = peakCount == 0
            ? null
            : new AudioWaveform(bucketMilliseconds, peaks, durationMilliseconds);
        var maximumFrequency = Math.Min(18_000, sampleRate / 2d);
        var spectrogram = spectrumFrameCount == 0
            ? null
            : new AudioSpectrogram(
                spectrogramFrameMilliseconds,
                SpectrogramBands,
                spectrumRows,
                durationMilliseconds,
                35,
                maximumFrequency,
                // Frame k is the FFT of samples [k * hop, k * hop + size); it
                // describes the moment at the centre of that window.
                SpectrogramFftSize / 2d * 1000d / sampleRate,
                spectrumHistogram);
        return new AudioVisualization(waveform, spectrogram);
    }


    private static (int First, int Last, double CenterFrequency)[] BuildBandRanges(int sampleRate)
    {
        var result = new (int, int, double)[SpectrogramBands];
        const double minimum = 35;
        var maximum = Math.Max(minimum * 2, Math.Min(18_000, sampleRate / 2d));
        for (var band = 0; band < SpectrogramBands; band++)
        {
            var low = minimum * Math.Pow(maximum / minimum, band / (double)SpectrogramBands);
            var high = minimum * Math.Pow(maximum / minimum, (band + 1d) / SpectrogramBands);
            var first = Math.Clamp(
                (int)Math.Floor(low * SpectrogramFftSize / sampleRate),
                1,
                SpectrogramFftSize / 2 - 1);
            var last = Math.Clamp(
                (int)Math.Ceiling(high * SpectrogramFftSize / sampleRate),
                first,
                SpectrogramFftSize / 2 - 1);
            result[band] = (first, last, Math.Sqrt(low * high));
        }
        return result;
    }

    private static float[] BuildFftWindow()
    {
        var result = new float[SpectrogramFftSize];
        for (var index = 0; index < result.Length; index++)
            result[index] = (float)(
                .5 - .5 * Math.Cos(2 * Math.PI * index / (SpectrogramFftSize - 1)));
        return result;
    }

    private static void AppendSpectrumRow(
        float[] ring,
        int ringPosition,
        Complex[] fft,
        float[] window,
        (int First, int Last, double CenterFrequency)[] ranges,
        byte[] destination,
        int destinationFrame,
        long[] histogram,
        int availableFrames = SpectrogramFftSize)
    {
        var padding = SpectrogramFftSize - Math.Min(SpectrogramFftSize, availableFrames);
        var sourceStart = availableFrames >= SpectrogramFftSize ? ringPosition : 0;
        for (var index = 0; index < SpectrogramFftSize; index++)
        {
            var sourceIndex = index < padding
                ? -1
                : (sourceStart + index - padding) % SpectrogramFftSize;
            var sample = sourceIndex < 0 ? 0 : ring[sourceIndex];
            fft[index].X = sample * window[index];
            fft[index].Y = 0;
        }

        FastFourierTransform.FFT(true, SpectrogramFftExponent, fft);
        var destinationOffset = destinationFrame * SpectrogramBands;
        var destinationBand = 0;
        foreach (var (first, last, centerFrequency) in ranges)
        {
            var energy = 0d;
            for (var bin = first; bin <= last; bin++)
            {
                energy = Math.Max(
                    energy,
                    Math.Sqrt(fft[bin].X * fft[bin].X + fft[bin].Y * fft[bin].Y));
            }
            var magnitude = energy * 2;
            var decibels = 20 * Math.Log10(magnitude + 1e-8);
            decibels += 1.5 * Math.Log(centerFrequency / 1000, 2);
            var level = SpectrogramLevelMapping.Quantize(decibels);
            histogram[destinationBand * 256 + level]++;
            destination[destinationOffset + destinationBand++] = level;
        }
    }

    private sealed class CacheEntry
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly Lazy<Task<AudioVisualization>> task;
        private int observerStarted;
        private int cancellationDisposed;

        public CacheEntry(string fullPath, string revisionKey)
        {
            FullPath = fullPath;
            RevisionKey = revisionKey;
            task = new Lazy<Task<AudioVisualization>>(
                () => DecodeQueuedAsync(fullPath, revisionKey, cancellation.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public string FullPath { get; }
        public string RevisionKey { get; }
        public Task<AudioVisualization> Task => task.Value;
        public bool IsDecodeCompleted => task.IsValueCreated && task.Value.IsCompleted;
        public int WaiterCount { get; set; }
        public long LastAccess { get; set; }
        public long SizeBytes { get; set; }
        public bool SizeAccounted { get; set; }

        public void EnsureObserved(Task<AudioVisualization> decodeTask)
        {
            if (Interlocked.Exchange(ref observerStarted, 1) == 0)
                _ = ObserveEntryAsync(this, decodeTask);
        }

        public void Cancel()
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion already released this source.
            }
        }

        public void DisposeCancellation()
        {
            if (Interlocked.Exchange(ref cancellationDisposed, 1) == 0)
                cancellation.Dispose();
        }
    }
}
