using LumaChart.Application.Audio;
using NAudio.Wave;

namespace LumaChart.Avalonia.Audio;

/// <summary>Windows decoder adapter for the shared beat-analysis engine.</summary>
internal static class WindowsBeatAnalyzer
{
    public static Task<BeatAnalysisResult> AnalyzeAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        RunOnAnalysisWorker(
            () => BeatAnalysisEngine.Analyze(ReadSignal(path, cancellationToken), cancellationToken),
            cancellationToken);

    public static Task<TempoTimelineAnalysisResult> AnalyzeTimelineAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        RunOnAnalysisWorker(
            () =>
            {
                var signal = ReadSignal(path, cancellationToken);
                return BeatAnalysisEngine.AnalyzeTimeline(signal, cancellationToken);
            },
            cancellationToken);

    private static Task<TResult> RunOnAnalysisWorker<TResult>(
        Func<TResult> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Factory.StartNew(
            () =>
            {
                try
                {
                    // Four minutes of correlation scanning is intentionally CPU
                    // heavy. A dedicated below-normal worker prevents it from
                    // competing with Avalonia input/rendering and WASAPI.
                    Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                }
                catch (ThreadStateException)
                {
                    // The analysis remains correct if a restricted host refuses
                    // the optional Windows scheduling hint.
                }
                cancellationToken.ThrowIfCancellationRequested();
                return operation();
            },
            cancellationToken,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private static BeatAnalysisSignal ReadSignal(
        string path,
        CancellationToken cancellationToken)
    {
        using var source = AudioDecoder.OpenWaveStream(path);
        return ReadSignal(source, cancellationToken);
    }

    internal static BeatAnalysisSignal ReadSignal(
        WaveStream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        // Do not issue an extra read after the stream's declared PCM length.
        // NVorbis can spin forever in that final EOF probe on otherwise valid
        // OGG files; waveform/SPEC decoding uses the same boundary.
        var reader = LengthCappedSampleProvider.FromWaveStream(source);
        var channels = reader.WaveFormat.Channels;
        var sourceRate = reader.WaveFormat.SampleRate;
        var analysisRate = BeatAnalysisEngine.DefaultAnalysisRate;
        var maximumFrames = analysisRate * BeatAnalysisEngine.MaximumAnalysisSeconds;
        var samplesPerFrame = Math.Max(1, (int)Math.Ceiling(sourceRate / (double)analysisRate));
        var buffer = new float[samplesPerFrame * channels * 32];
        var energies = new List<double>(Math.Min(maximumFrames, analysisRate * 180));
        var reducer = new ExactRateEnergyReducer(sourceRate, analysisRate, channels);

        while (energies.Count < maximumFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            for (var index = 0; index < read; index++)
            {
                if (!reducer.AddSample(buffer[index], out var energy)) continue;
                energies.Add(energy);
                if (energies.Count >= maximumFrames) break;
            }
        }

        return BeatAnalysisEngine.CreateSignal(energies, analysisRate);
    }
}
