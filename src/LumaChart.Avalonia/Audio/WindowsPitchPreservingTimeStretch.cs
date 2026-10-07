using NAudio.Dsp;
using NAudio.Wave;
using SoundTouch;

namespace LumaChart.Avalonia.Audio;

internal static class WindowsPitchPreservingTimeStretchPolicy
{
    public const int SequenceDurationMilliseconds = 100;
    public const int SeekWindowDurationMilliseconds = 28;
    public const int OverlapDurationMilliseconds = 20;
    public const int MaximumReadAttemptsPerDevicePeriod = 4;
    public const double ExtremeSlowdownMaximumRate = .3;
    public const int PhaseVocoderStageCount = 3;
    public const int PhaseVocoderFftSize = 4_096;
    public const int PhaseVocoderOversampling = 8;
    public const int StartupFadeMilliseconds = 10;

    public static bool UsesPhaseVocoder(double playbackRate, int channels = 2) =>
        channels is 1 or 2 &&
        NormalizeRate(playbackRate) <= ExtremeSlowdownMaximumRate;

    public static double ResolveSoundTouchTempo(double playbackRate) =>
        NormalizeRate(playbackRate);

    public static float ResolvePhaseVocoderStagePitchFactor(double playbackRate) =>
        (float)Math.Pow(1d / NormalizeRate(playbackRate), 1d / PhaseVocoderStageCount);

    public static int PhaseVocoderLatencyFrames =>
        PhaseVocoderStageCount *
        (PhaseVocoderFftSize - PhaseVocoderFftSize / PhaseVocoderOversampling);

    public static void Configure(SoundTouchProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        processor.SetSetting(SettingId.UseQuickSeek, 0);
        processor.SetSetting(SettingId.SequenceDurationMs, SequenceDurationMilliseconds);
        processor.SetSetting(SettingId.SeekWindowDurationMs, SeekWindowDurationMilliseconds);
        processor.SetSetting(SettingId.OverlapDurationMs, OverlapDurationMilliseconds);
    }

    private static double NormalizeRate(double playbackRate) =>
        double.IsFinite(playbackRate) ? Math.Clamp(playbackRate, .2, 1) : 1;
}

/// <summary>
/// Handles the five-times expansion needed at 0.2x in the time domain, then
/// restores pitch through three moderate phase-vocoder shifts. Buffers and DSP
/// state are reused so the WASAPI render callback stays allocation-free.
/// </summary>
internal sealed class WindowsPhaseVocoderTimeStretchSampleProvider : ISampleProvider
{
    private readonly RateAdjustingSampleProvider rateProvider;
    private readonly SmbPitchShiftStageSampleProvider[] pitchStages;
    private readonly SoundTouchContinuitySampleProvider outputContinuity;
    private readonly float[] startupBuffer;
    private readonly int startupFadeFrames;
    private int startupSamplesRemaining;
    private int startupFadeFrame;
    private bool primed;

    public WindowsPhaseVocoderTimeStretchSampleProvider(
        ISampleProvider source,
        Func<bool> sourceReallyEnded,
        double playbackRate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceReallyEnded);
        if (source.WaveFormat.Channels is not (1 or 2))
            throw new NotSupportedException(
                "SMB phase-vocoder playback supports mono or stereo audio only.");

        WaveFormat = source.WaveFormat;
        rateProvider = new RateAdjustingSampleProvider(source, sourceReallyEnded);
        rateProvider.Reset(TimeSpan.Zero);
        var rateContinuity = new SoundTouchContinuitySampleProvider(
            rateProvider,
            sourceReallyEnded);
        ISampleProvider stageSource = rateContinuity;
        var stageEnded = new Func<bool>(() => rateContinuity.IsEnded);
        pitchStages = new SmbPitchShiftStageSampleProvider[
            WindowsPitchPreservingTimeStretchPolicy.PhaseVocoderStageCount];
        for (var stage = 0; stage < pitchStages.Length; stage++)
        {
            var pitchStage = new SmbPitchShiftStageSampleProvider(
                stageSource,
                stageEnded,
                WindowsPitchPreservingTimeStretchPolicy.PhaseVocoderFftSize,
                WindowsPitchPreservingTimeStretchPolicy.PhaseVocoderOversampling);
            pitchStages[stage] = pitchStage;
            stageSource = pitchStage;
            stageEnded = () => pitchStage.IsEnded;
        }
        var finalStage = pitchStages[^1];
        outputContinuity = new SoundTouchContinuitySampleProvider(
            stageSource,
            () => finalStage.IsEnded);
        startupSamplesRemaining = checked(
            WindowsPitchPreservingTimeStretchPolicy.PhaseVocoderLatencyFrames *
            WaveFormat.Channels);
        startupBuffer = new float[Math.Min(
            startupSamplesRemaining,
            WindowsPitchPreservingTimeStretchPolicy.PhaseVocoderFftSize *
            WaveFormat.Channels)];
        startupFadeFrames = Math.Max(
            1,
            WaveFormat.SampleRate *
            WindowsPitchPreservingTimeStretchPolicy.StartupFadeMilliseconds / 1_000);
        PlaybackRate = playbackRate;
    }

    public WaveFormat WaveFormat { get; }
    public bool IsEnded => primed && outputContinuity.IsEnded;

    public double PlaybackRate
    {
        get => rateProvider.PlaybackRate;
        set
        {
            rateProvider.PlaybackRate = Math.Clamp(
                double.IsFinite(value) ? value : 1,
                .2,
                WindowsPitchPreservingTimeStretchPolicy.ExtremeSlowdownMaximumRate);
            var pitchFactor =
                WindowsPitchPreservingTimeStretchPolicy.ResolvePhaseVocoderStagePitchFactor(
                    rateProvider.PlaybackRate);
            foreach (var stage in pitchStages) stage.PitchFactor = pitchFactor;
        }
    }

    public bool Prime()
    {
        while (startupSamplesRemaining > 0)
        {
            var requested = Math.Min(startupSamplesRemaining, startupBuffer.Length);
            var read = outputContinuity.Read(startupBuffer, 0, requested);
            if (read <= 0)
            {
                if (!outputContinuity.IsEnded) return false;
                startupSamplesRemaining = 0;
                primed = true;
                return true;
            }
            startupSamplesRemaining -= read;
        }
        primed = true;
        return true;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (count == 0) return 0;
        if (!primed && !Prime()) return 0;

        var read = outputContinuity.Read(buffer, offset, count);
        ApplyStartupFade(buffer, offset, read);
        return read;
    }

    private void ApplyStartupFade(float[] buffer, int offset, int count)
    {
        if (startupFadeFrame >= startupFadeFrames || count <= 0) return;
        var channels = WaveFormat.Channels;
        var frames = count / channels;
        for (var frame = 0; frame < frames && startupFadeFrame < startupFadeFrames; frame++)
        {
            var gain = (startupFadeFrame + 1f) / startupFadeFrames;
            var sample = offset + frame * channels;
            for (var channel = 0; channel < channels; channel++)
                buffer[sample + channel] *= gain;
            startupFadeFrame++;
        }
    }
}

internal sealed class SmbPitchShiftStageSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly Func<bool> sourceReallyEnded;
    private readonly SmbPitchShifter leftShifter = new();
    private readonly SmbPitchShifter rightShifter = new();
    private readonly int fftSize;
    private readonly int oversampling;
    private readonly int latencyFrames;
    private float[] left = new float[4_096];
    private float[] right = new float[4_096];
    private int flushFramesRemaining;
    private bool sourceEnded;

    public SmbPitchShiftStageSampleProvider(
        ISampleProvider source,
        Func<bool> sourceReallyEnded,
        int fftSize,
        int oversampling)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceReallyEnded);
        if (source.WaveFormat.Channels is not (1 or 2))
            throw new NotSupportedException("SMB pitch shifting supports mono or stereo audio only.");
        if (fftSize <= 0 || fftSize > 4_096 || (fftSize & (fftSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(fftSize));
        if (oversampling < 4 || oversampling > fftSize || fftSize % oversampling != 0)
            throw new ArgumentOutOfRangeException(nameof(oversampling));

        this.source = source;
        this.sourceReallyEnded = sourceReallyEnded;
        this.fftSize = fftSize;
        this.oversampling = oversampling;
        latencyFrames = fftSize - fftSize / oversampling;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }
    public float PitchFactor { get; set; } = 1;
    public bool IsEnded => sourceEnded && flushFramesRemaining <= 0;

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(offset));
        var channels = WaveFormat.Channels;
        count -= count % channels;
        if (count <= 0 || IsEnded) return 0;

        int read;
        if (!sourceEnded)
        {
            read = source.Read(buffer, offset, count);
            read -= read % channels;
            if (read <= 0)
            {
                if (!sourceReallyEnded()) return 0;
                sourceEnded = true;
                flushFramesRemaining = latencyFrames;
                read = FillFlush(buffer, offset, count);
            }
        }
        else
        {
            read = FillFlush(buffer, offset, count);
        }

        Process(buffer, offset, read);
        return read;
    }

    private int FillFlush(float[] buffer, int offset, int count)
    {
        var frames = Math.Min(count / WaveFormat.Channels, flushFramesRemaining);
        var samples = frames * WaveFormat.Channels;
        Array.Clear(buffer, offset, samples);
        flushFramesRemaining -= frames;
        return samples;
    }

    private void Process(float[] buffer, int offset, int count)
    {
        var channels = WaveFormat.Channels;
        var frames = count / channels;
        EnsureCapacity(frames);
        if (channels == 1)
        {
            Array.Copy(buffer, offset, left, 0, frames);
            leftShifter.PitchShift(
                PitchFactor,
                frames,
                fftSize,
                oversampling,
                WaveFormat.SampleRate,
                left);
            for (var frame = 0; frame < frames; frame++)
                buffer[offset + frame] = WindowsKeySoundSoftLimiter.Limit(left[frame]);
            return;
        }

        for (var frame = 0; frame < frames; frame++)
        {
            var sample = offset + frame * 2;
            left[frame] = buffer[sample];
            right[frame] = buffer[sample + 1];
        }
        leftShifter.PitchShift(
            PitchFactor,
            frames,
            fftSize,
            oversampling,
            WaveFormat.SampleRate,
            left);
        rightShifter.PitchShift(
            PitchFactor,
            frames,
            fftSize,
            oversampling,
            WaveFormat.SampleRate,
            right);
        for (var frame = 0; frame < frames; frame++)
        {
            var sample = offset + frame * 2;
            buffer[sample] = WindowsKeySoundSoftLimiter.Limit(left[frame]);
            buffer[sample + 1] = WindowsKeySoundSoftLimiter.Limit(right[frame]);
        }
    }

    private void EnsureCapacity(int frames)
    {
        if (frames <= left.Length) return;
        var size = Math.Max(frames, left.Length * 2);
        Array.Resize(ref left, size);
        Array.Resize(ref right, size);
    }
}

/// <summary>
/// Keeps transient empty SoundTouch reads from being interpreted as end-of-stream
/// without decoding a large future block on the real-time WASAPI callback.
/// </summary>
internal sealed class SoundTouchContinuitySampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly Func<bool> sourceReallyEnded;
    private readonly int maximumReadAttemptsPerDevicePeriod;
    private bool sourceEnded;

    public SoundTouchContinuitySampleProvider(
        ISampleProvider source,
        Func<bool> sourceReallyEnded,
        int maximumReadAttemptsPerDevicePeriod =
            WindowsPitchPreservingTimeStretchPolicy.MaximumReadAttemptsPerDevicePeriod)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceReallyEnded);
        if (maximumReadAttemptsPerDevicePeriod <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumReadAttemptsPerDevicePeriod));

        this.source = source;
        this.sourceReallyEnded = sourceReallyEnded;
        this.maximumReadAttemptsPerDevicePeriod = maximumReadAttemptsPerDevicePeriod;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }
    public bool IsEnded => sourceEnded;

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (count == 0) return 0;

        var written = 0;
        for (var attempt = 0;
             attempt < maximumReadAttemptsPerDevicePeriod &&
             written < count &&
             !sourceEnded;
             attempt++)
        {
            var read = source.Read(buffer, offset + written, count - written);
            if (read <= 0)
            {
                if (sourceReallyEnded()) sourceEnded = true;
                continue;
            }

            written += read;
        }
        return written;
    }
}
