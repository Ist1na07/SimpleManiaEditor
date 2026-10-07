using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Rendering;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Avalonia.Specs;

/// <summary>
/// Pins where the waveform and spectrogram draw an onset relative to the Beat
/// the Notes use. Users reported the two did not line up: the spectrogram was
/// placed at the start of each FFT window, half a window (~23 ms) early, and
/// every waveform row also took the peak of the bucket after it.
///
/// A synthetic click track goes through the real decoder and scene builder at
/// one pixel per millisecond, so a row offset is directly an offset in time.
/// </summary>
internal static class WindowsAudioVisualizationAlignmentSpecs
{
    private const int SampleRate = 44_100;
    private const double SceneBeat = 4;
    private const double PixelsPerBeat = 500;
    private const double JudgementY = 900;
    private const double ViewportHeight = 900;
    private static readonly double[] Onsets = [2500, 3000, 3500, 4000];

    public static async Task<IReadOnlyList<WindowsAudioVisualizationAlignmentSpecResult>> RunAsync(
        string isolationRoot)
    {
        var results = new List<WindowsAudioVisualizationAlignmentSpecResult>();
        Directory.CreateDirectory(isolationRoot);
        var path = Path.Combine(isolationRoot, "click-track.wav");
        WriteClickTrack(path);
        AudioVisualization visualization;
        try
        {
            visualization = await AudioWaveformService.LoadVisualizationAsync(path).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            results.Add(new("audio-visualization-alignment-click-track-decodes", false, exception.Message));
            return results;
        }

        // 120 BPM with the first beat at 1000 ms: beat b sits at 1000 + 500 b ms.
        var timing = new TimingMap([new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120, Delay = -1000 }]);
        var scene = TimelineAudioBackgroundSceneBuilder.Build(
            visualization,
            timing,
            SceneBeat,
            PixelsPerBeat,
            JudgementY,
            ViewportHeight,
            includeWaveform: true,
            includeSpectrogram: true);

        Check(results, "audio-visualization-spectrogram-centres-frames-on-onsets", () =>
        {
            var offsets = Onsets.Select(onset => SpectrumCentroid(scene, timing, onset) - onset).ToArray();
            // The click's energy is centred ~1 ms after its start; the old
            // window-start placement measured about -23 ms here.
            Require(
                offsets.All(offset => offset is > -4 and < 6),
                $"Spectrogram onsets are drawn off their time: {Format(offsets)} ms.");
            return $"offsets={Format(offsets)} ms; frameOffset=" +
                   $"{visualization.Spectrogram!.FrameOffsetMilliseconds:F2} ms";
        });
        Check(results, "audio-visualization-waveform-rows-do-not-lead-onsets", () =>
        {
            var offsets = Onsets.Select(onset => WaveformLeadingEdge(scene, timing, onset) - onset).ToArray();
            // One ~2 ms bucket of quantisation is inherent; reading the next
            // bucket as well used to put the edge up to 3.4 ms early.
            Require(
                offsets.All(offset => offset is > -2.6 and < 2.6),
                $"Waveform onsets are drawn off their time: {Format(offsets)} ms.");
            return $"offsets={Format(offsets)} ms";
        });
        Check(results, "audio-retained-scene-rescales-to-current-zoom-and-judgement-line", () =>
        {
            // A scene built at 500 px/beat drawn after zooming to 250 px/beat
            // and moving the judgement line must still put every row on its
            // own Beat until the rebuilt scene arrives.
            const double zoomed = 250, movedLine = 700, currentBeat = 4.2;
            var (scale, offset) = TimelineRetainedAudioScenePolicy.SceneToView(
                scene,
                currentBeat,
                zoomed,
                movedLine,
                ViewportHeight);
            foreach (var sceneY in new[] { 100d, 450, 900 })
            {
                var beat = SceneBeat + (JudgementY - sceneY) / PixelsPerBeat;
                var expected = movedLine - (beat - currentBeat) * zoomed;
                var actual = sceneY * scale + offset;
                Require(
                    Math.Abs(actual - expected) < 1e-6,
                    $"Scene y {sceneY} maps to {actual:F3} instead of {expected:F3}.");
            }
            return $"scale={scale:F3}; offset={offset:F3}";
        });
        Check(results, "audio-spectrogram-detail-shows-more-as-it-rises", () =>
        {
            var spectrum = visualization.Spectrogram!;
            int Visible(double detail)
            {
                var lookup = spectrum.DisplayLookup(detail)
                    ?? throw new InvalidOperationException("Decoded spectrum has no level histograms.");
                var count = 0;
                for (var index = 0; index < spectrum.Intensities.Length; index++)
                    if (lookup[index % spectrum.FrequencyBands * 256 + spectrum.Intensities[index]] > 10) count++;
                return count;
            }
            var low = Visible(0);
            var middle = Visible(SpectrogramLevelMapping.DefaultDetail);
            var high = Visible(1);
            Require(
                low <= middle && middle <= high && high > low,
                $"Visible cells did not grow with detail: {low}/{middle}/{high}.");
            return $"visibleCells={low}/{middle}/{high}";
        });
        Check(results, "windows-audio-panel-settings-keep-spectrogram-strength-and-detail", () =>
        {
            const string localDataVariable = "LUMACHART_AVALONIA_LOCAL_DATA_ROOT";
            var previous = Environment.GetEnvironmentVariable(localDataVariable);
            try
            {
                Environment.SetEnvironmentVariable(localDataVariable, Path.Combine(isolationRoot, "settings"));
                var saved = WindowsAudioPanelSettings.Default.WithSpectrogramAppearance(.45, .9);
                WindowsAudioPanelSettingsStore.Save(saved);
                var loaded = WindowsAudioPanelSettingsStore.Load();
                // Moving or clamping a panel replaces its layout; that must not
                // reset how the spectrogram looks.
                var relaid = loaded.WithPanels(loaded.Waveform.WithCollapsed(true), loaded.Spectrogram);
                Require(
                    Math.Abs(loaded.BackgroundSpectrogramStrength - .45) < 1e-9 &&
                    Math.Abs(loaded.SpectrogramDetail - .9) < 1e-9 &&
                    Math.Abs(relaid.BackgroundSpectrogramStrength - .45) < 1e-9 &&
                    Math.Abs(relaid.SpectrogramDetail - .9) < 1e-9,
                    $"Loaded {loaded.BackgroundSpectrogramStrength}/{loaded.SpectrogramDetail}, " +
                    $"after relayout {relaid.BackgroundSpectrogramStrength}/{relaid.SpectrogramDetail}.");
                var clamped = new WindowsAudioPanelSettings(backgroundSpectrogramStrength: 0, spectrogramDetail: 7);
                Require(
                    Math.Abs(clamped.BackgroundSpectrogramStrength -
                             WindowsAudioPanelSettings.MinimumBackgroundSpectrogramStrength) < 1e-9 &&
                    Math.Abs(clamped.SpectrogramDetail - 1) < 1e-9,
                    "Out-of-range strength or detail was not clamped.");
                return $"strength={loaded.BackgroundSpectrogramStrength:F2}; detail={loaded.SpectrogramDetail:F2}";
            }
            finally
            {
                Environment.SetEnvironmentVariable(localDataVariable, previous);
            }
        });
        return results;
    }

    private static double MillisecondsAtRow(TimelineAudioBackgroundScene scene, TimingMap timing, int row) =>
        TimelineAudioBackgroundSceneBuilder.MillisecondsAtY(
            timing,
            SceneBeat,
            PixelsPerBeat,
            JudgementY,
            scene.TopY + (row + .5) * scene.RowPixels);

    private static double WaveformLeadingEdge(TimelineAudioBackgroundScene scene, TimingMap timing, double onset)
    {
        var peaks = scene.WaveformPeaks ?? throw new InvalidOperationException("The scene has no waveform.");
        var edge = double.PositiveInfinity;
        for (var row = 0; row < peaks.Length; row++)
        {
            var milliseconds = MillisecondsAtRow(scene, timing, row);
            if (Math.Abs(milliseconds - onset) <= 60 && peaks[row] >= .25)
                edge = Math.Min(edge, milliseconds);
        }
        return double.IsFinite(edge)
            ? edge
            : throw new InvalidOperationException($"No waveform onset near {onset} ms.");
    }

    private static double SpectrumCentroid(TimelineAudioBackgroundScene scene, TimingMap timing, double onset)
    {
        var spectrum = scene.Spectrogram ?? throw new InvalidOperationException("The scene has no spectrogram.");
        var pixels = spectrum.BgraPixels;
        double weighted = 0, total = 0;
        for (var row = 0; row < spectrum.Height; row++)
        {
            var milliseconds = MillisecondsAtRow(scene, timing, row);
            if (Math.Abs(milliseconds - (onset + 20)) > 100) continue;
            double energy = 0;
            for (var band = 0; band < spectrum.Width; band++)
            {
                var offset = (row * spectrum.Width + band) * 4;
                energy += (pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) * pixels[offset + 3] / 255d;
            }
            weighted += energy * milliseconds;
            total += energy;
        }
        return total > 0
            ? weighted / total
            : throw new InvalidOperationException($"No spectrogram energy near {onset} ms.");
    }

    private static void WriteClickTrack(string path)
    {
        var samples = new short[SampleRate * 5];
        var random = new Random(7);
        foreach (var onset in Onsets)
        {
            var start = (int)Math.Round(onset / 1000 * SampleRate);
            // A 4 ms noise burst with a 1.5 ms decay is effectively an impulse.
            for (var index = 0; index < SampleRate * 4 / 1000; index++)
            {
                var envelope = Math.Exp(-index / (SampleRate * .0015));
                samples[start + index] = (short)Math.Round((random.NextDouble() * 2 - 1) * 24_000 * envelope);
            }
        }
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + samples.Length * 2);
        writer.Write("WAVEfmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(samples.Length * 2);
        foreach (var sample in samples) writer.Write(sample);
    }

    private static string Format(IEnumerable<double> values) =>
        string.Join(",", values.Select(value => value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)));

    private static void Check(
        ICollection<WindowsAudioVisualizationAlignmentSpecResult> results,
        string name,
        Func<string> action)
    {
        try
        {
            results.Add(new WindowsAudioVisualizationAlignmentSpecResult(name, true, action()));
        }
        catch (Exception exception)
        {
            results.Add(new WindowsAudioVisualizationAlignmentSpecResult(name, false, exception.Message));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed record WindowsAudioVisualizationAlignmentSpecResult(
    string Name,
    bool Passed,
    string Detail);
