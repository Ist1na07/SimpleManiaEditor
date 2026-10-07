using LumaChart.Avalonia.Audio;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Avalonia.Rendering;

public sealed class TimelineSpectrogramPixels : IDisposable
{
    private byte[] bgraPixels;
    private readonly bool returnToPool;

    public TimelineSpectrogramPixels(int width, int height, byte[] bgraPixels)
        : this(width, height, bgraPixels, returnToPool: false)
    {
    }

    internal TimelineSpectrogramPixels(
        int width,
        int height,
        byte[] bgraPixels,
        bool returnToPool)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        ByteLength = checked(Width * Height * 4);
        ArgumentNullException.ThrowIfNull(bgraPixels);
        if (bgraPixels.Length < ByteLength)
            throw new ArgumentException("The spectrogram pixel buffer is too small.", nameof(bgraPixels));
        this.bgraPixels = bgraPixels;
        this.returnToPool = returnToPool;
    }

    public int Width { get; }
    public int Height { get; }
    public int ByteLength { get; }
    public byte[] BgraPixels => Volatile.Read(ref bgraPixels);

    public void Dispose()
    {
        var released = Interlocked.Exchange(ref bgraPixels, Array.Empty<byte>());
        if (returnToPool && released.Length > 0)
            TimelineSpectrogramBufferPool.Return(released);
    }
}

internal static class TimelineSpectrogramBufferPool
{
    private const long MaximumRetainedBytes = 32L * 1024 * 1024;
    private const int MaximumBuffersPerSize = 2;
    private static readonly object Gate = new();
    private static readonly Dictionary<int, Stack<byte[]>> Buffers = [];
    private static long retainedBytes;

    public static byte[] Rent(int length)
    {
        lock (Gate)
        {
            if (Buffers.TryGetValue(length, out var candidates) &&
                candidates.TryPop(out var buffer))
            {
                retainedBytes -= buffer.Length;
                if (candidates.Count == 0) Buffers.Remove(length);
                return buffer;
            }
        }
        return new byte[length];
    }

    public static void Return(byte[] buffer)
    {
        if (buffer.Length <= 0 || buffer.LongLength > MaximumRetainedBytes) return;
        lock (Gate)
        {
            if (retainedBytes + buffer.LongLength > MaximumRetainedBytes) return;
            if (!Buffers.TryGetValue(buffer.Length, out var candidates))
            {
                candidates = new Stack<byte[]>();
                Buffers.Add(buffer.Length, candidates);
            }
            if (candidates.Count >= MaximumBuffersPerSize) return;
            candidates.Push(buffer);
            retainedBytes += buffer.Length;
        }
    }
}

/// <param name="PixelsPerBeat">Zoom the scene was built for.</param>
/// <param name="JudgementY">Judgement-line y the scene was built for.</param>
public sealed record TimelineAudioBackgroundScene(
    double SceneBeat,
    double TopY,
    double RowPixels,
    float[]? WaveformPeaks,
    TimelineSpectrogramPixels? Spectrogram,
    double PixelsPerBeat = 0,
    double JudgementY = double.NaN);

public sealed record TimelineAudioBackgroundDiagnostics(
    bool VisualizationAttached,
    bool WaveformVisible,
    bool SpectrogramVisible,
    bool SceneReady,
    bool BuildPending,
    double? SceneBeat,
    int WaveformRows,
    int SpectrogramWidth,
    int SpectrogramRows,
    bool SpectrogramBitmapReady);

internal static class TimelineRetainedAudioScenePolicy
{
    public static double Translation(
        double currentBeat,
        double sceneBeat,
        double pixelsPerBeat,
        double viewportHeight)
    {
        var height = Math.Max(1, viewportHeight);
        var translation = (currentBeat - sceneBeat) * Math.Max(1, pixelsPerBeat);
        return Math.Clamp(translation, -height, height);
    }

    /// <summary>
    /// Maps scene y to viewport y as <c>viewY = Scale * sceneY + Offset</c>.
    /// A retained scene is redrawn until its replacement is built. Drawing it
    /// with only a translation kept it on the old zoom and the old judgement
    /// line, so while zooming or after moving the judgement line the waveform
    /// and spectrogram visibly slid away from the Notes. Scaling about the
    /// scene's own judgement line keeps every row at its true Beat, only at the
    /// retained scene's resolution, until the rebuilt scene arrives.
    /// </summary>
    public static (double Scale, double Offset) SceneToView(
        TimelineAudioBackgroundScene scene,
        double currentBeat,
        double pixelsPerBeat,
        double judgementY,
        double viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(scene);
        pixelsPerBeat = Math.Max(1, pixelsPerBeat);
        var scale = scene.PixelsPerBeat > 0 ? pixelsPerBeat / scene.PixelsPerBeat : 1;
        var sceneJudgementY = double.IsFinite(scene.JudgementY) ? scene.JudgementY : judgementY;
        var translation = Translation(currentBeat, scene.SceneBeat, pixelsPerBeat, viewportHeight);
        return (scale, judgementY - sceneJudgementY * scale + translation);
    }
}

/// <summary>
/// Builds a three-viewport audio scene without touching Avalonia UI objects.
/// TimelineSurface only uploads/draws the completed result, so FFT output and
/// TimingMap sampling never run on the input/render thread.
/// </summary>
public static class TimelineAudioBackgroundSceneBuilder
{
    public const double RowPixels = 1;

    public static TimelineAudioBackgroundScene Build(
        AudioVisualization visualization,
        TimingMap timingMap,
        double sceneBeat,
        double pixelsPerBeat,
        double judgementY,
        double viewportHeight,
        bool includeWaveform,
        bool includeSpectrogram,
        CancellationToken cancellationToken = default,
        double spectrogramDetail = SpectrogramLevelMapping.DefaultDetail)
    {
        ArgumentNullException.ThrowIfNull(visualization);
        ArgumentNullException.ThrowIfNull(timingMap);
        return BuildCore(
            visualization,
            timingMap,
            sceneBeat,
            pixelsPerBeat,
            judgementY,
            viewportHeight,
            includeWaveform,
            includeSpectrogram,
            RowPixels,
            BackgroundSpectrogramMaximumAlpha,
            spectrogramDetail,
            cancellationToken);
    }

    /// <summary>
    /// Peak alpha baked into the background spectrogram. The loudest cells are
    /// fully opaque; how strongly the whole layer shows is the user's
    /// spectrogram strength, applied as opacity when it is drawn so the slider
    /// responds without rebuilding the scene.
    /// </summary>
    public const byte BackgroundSpectrogramMaximumAlpha = 254;

    /// <summary>
    /// Builds the opaque, one-row-per-DIP scene used by the compact WPF-parity
    /// side panels. Like the timeline background scene, the returned object is
    /// UI-framework independent and safe to build on a worker thread.
    /// </summary>
    public static TimelineAudioBackgroundScene BuildSidePanel(
        AudioVisualization visualization,
        TimingMap timingMap,
        double sceneBeat,
        double pixelsPerBeat,
        double judgementY,
        double viewportHeight,
        bool includeWaveform,
        bool includeSpectrogram,
        CancellationToken cancellationToken = default,
        double spectrogramDetail = SpectrogramLevelMapping.DefaultDetail) =>
        BuildCore(
            visualization,
            timingMap,
            sceneBeat,
            pixelsPerBeat,
            judgementY,
            viewportHeight,
            includeWaveform,
            includeSpectrogram,
            1,
            255,
            spectrogramDetail,
            cancellationToken);

    private static TimelineAudioBackgroundScene BuildCore(
        AudioVisualization visualization,
        TimingMap timingMap,
        double sceneBeat,
        double pixelsPerBeat,
        double judgementY,
        double viewportHeight,
        bool includeWaveform,
        bool includeSpectrogram,
        double rowPixels,
        byte spectrogramMaximumAlpha,
        double spectrogramDetail,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(visualization);
        ArgumentNullException.ThrowIfNull(timingMap);
        pixelsPerBeat = Math.Max(1, pixelsPerBeat);
        viewportHeight = Math.Max(1, viewportHeight);
        var topY = -viewportHeight;
        rowPixels = Math.Clamp(rowPixels, .25, 8);
        var rows = Math.Max(1, (int)Math.Ceiling(viewportHeight * 3 / rowPixels));
        var waveform = includeWaveform && visualization.Waveform is { Count: > 0 } sourceWaveform
            ? BuildWaveformRows(sourceWaveform, timingMap, sceneBeat, pixelsPerBeat, judgementY, topY, rows,
                rowPixels, cancellationToken)
            : null;
        var spectrogram = includeSpectrogram && visualization.Spectrogram is { FrameCount: > 0 } sourceSpectrum
            ? BuildSpectrogramRows(sourceSpectrum, timingMap, sceneBeat, pixelsPerBeat, judgementY, topY, rows,
                rowPixels, spectrogramMaximumAlpha, sourceSpectrum.DisplayLookup(spectrogramDetail), cancellationToken)
            : null;
        return new TimelineAudioBackgroundScene(
            sceneBeat,
            topY,
            rowPixels,
            waveform,
            spectrogram,
            pixelsPerBeat,
            judgementY);
    }

    public static double MillisecondsAtY(
        TimingMap timingMap,
        double sceneBeat,
        double pixelsPerBeat,
        double judgementY,
        double y)
    {
        var beat = BeatAtY(sceneBeat, pixelsPerBeat, judgementY, y);
        return timingMap.BeatToMilliseconds(BeatPosition.FromDouble(beat));
    }

    private static double BeatAtY(
        double sceneBeat,
        double pixelsPerBeat,
        double judgementY,
        double y) =>
        sceneBeat + (judgementY - y) / Math.Max(1, pixelsPerBeat);

    private static float[] BuildWaveformRows(
        AudioWaveform waveform,
        TimingMap timingMap,
        double sceneBeat,
        double pixelsPerBeat,
        double judgementY,
        double topY,
        int rows,
        double rowPixels,
        CancellationToken cancellationToken)
    {
        var result = new float[rows];
        for (var row = 0; row < rows; row++)
        {
            if ((row & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            var y0 = topY + row * rowPixels;
            var y1 = y0 + rowPixels;
            var firstBeat = BeatAtY(sceneBeat, pixelsPerBeat, judgementY, y0);
            var lastBeat = BeatAtY(sceneBeat, pixelsPerBeat, judgementY, y1);
            if (Math.Max(firstBeat, lastBeat) < 0) continue;
            firstBeat = Math.Max(0, firstBeat);
            lastBeat = Math.Max(0, lastBeat);
            var firstMs = timingMap.BeatToMilliseconds(BeatPosition.FromDouble(firstBeat));
            var lastMs = timingMap.BeatToMilliseconds(BeatPosition.FromDouble(lastBeat));
            if (firstMs > lastMs) (firstMs, lastMs) = (lastMs, firstMs);
            if (lastMs < 0 || firstMs > waveform.DurationMilliseconds) continue;
            // Bucket j covers [j * size, (j + 1) * size). Taking Ceiling(last)
            // inclusively also pulled in the bucket that starts after this row,
            // so every onset was drawn one bucket (~2 ms) early.
            var first = Math.Clamp((int)Math.Floor(firstMs / waveform.BucketMilliseconds), 0, waveform.Count - 1);
            var last = Math.Clamp(
                (int)Math.Ceiling(lastMs / waveform.BucketMilliseconds) - 1,
                first,
                waveform.Count - 1);
            var peak = 0f;
            for (var index = first; index <= last; index++) peak = Math.Max(peak, waveform.Peaks[index]);
            result[row] = peak;
        }
        return result;
    }

    private static TimelineSpectrogramPixels BuildSpectrogramRows(
        AudioSpectrogram spectrogram,
        TimingMap timingMap,
        double sceneBeat,
        double pixelsPerBeat,
        double judgementY,
        double topY,
        int rows,
        double rowPixels,
        byte maximumAlpha,
        byte[]? lookup,
        CancellationToken cancellationToken)
    {
        var width = spectrogram.FrequencyBands;
        var pixelByteLength = checked(width * rows * 4);
        var pixels = TimelineSpectrogramBufferPool.Rent(pixelByteLength);
        Array.Clear(pixels, 0, pixelByteLength);
        try
        {
            for (var row = 0; row < rows; row++)
            {
                if ((row & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                // Sample at the row centre, the same point the waveform row is
                // drawn at, and read the frame whose window is centred there.
                var y = topY + (row + .5) * rowPixels;
                var beat = BeatAtY(sceneBeat, pixelsPerBeat, judgementY, y);
                if (beat < 0) continue;
                var milliseconds = timingMap.BeatToMilliseconds(BeatPosition.FromDouble(beat));
                if (milliseconds < 0 || milliseconds > spectrogram.DurationMilliseconds) continue;
                var exactFrame = spectrogram.FrameIndexAt(milliseconds);
                var firstFrame = Math.Clamp((int)Math.Floor(exactFrame), 0, spectrogram.FrameCount - 1);
                var secondFrame = Math.Min(spectrogram.FrameCount - 1, firstFrame + 1);
                var fraction = Math.Clamp(exactFrame - firstFrame, 0, 1);
                for (var band = 0; band < width; band++)
                {
                    int first = spectrogram[firstFrame, band];
                    int second = spectrogram[secondFrame, band];
                    if (lookup != null)
                    {
                        first = lookup[band * 256 + first];
                        second = lookup[band * 256 + second];
                    }
                    var intensity = (byte)Math.Round(first + (second - first) * fraction);
                    WriteColor(
                        pixels,
                        (row * width + band) * 4,
                        intensity,
                        band / (double)Math.Max(1, width - 1),
                        maximumAlpha);
                }
            }

            return new TimelineSpectrogramPixels(
                width,
                rows,
                pixels,
                returnToPool: true);
        }
        catch
        {
            TimelineSpectrogramBufferPool.Return(pixels);
            throw;
        }
    }

    private static void WriteColor(
        byte[] pixels,
        int offset,
        byte intensity,
        double frequency,
        byte maximumAlpha)
    {
        var level = intensity / 255d;
        var opaque = maximumAlpha == 255;
        if (level < (opaque ? .035 : .04))
        {
            pixels[offset + 3] = opaque ? (byte)255 : (byte)0;
            return;
        }
        var gain = Math.Pow(level, 1.08);
        var highlight = Math.Clamp((gain - .72) / .28, 0, 1);
        (double R, double G, double B) color;
        byte alpha;
        if (opaque)
        {
            // Side panels sit on their own dark background: brightness carries
            // the level and every cell is opaque.
            var baseColor = frequency switch
            {
                < .34 => Lerp((35d, 71d, 139d), (38d, 133d, 159d), frequency / .34),
                < .68 => Lerp((38d, 133d, 159d), (69d, 166d, 130d), (frequency - .34) / .34),
                _ => Lerp((69d, 166d, 130d), (218d, 154d, 68d), (frequency - .68) / .32)
            };
            var dark = (R: 5d, G: 11d, B: 15d);
            color = Lerp(Lerp(dark, baseColor, gain), (243d, 234d, 202d), highlight * .72);
            alpha = 255;
        }
        else
        {
            // The background spectrogram is composited over the dimmed track,
            // so its colour stays bright at every level and the alpha ramp
            // carries the level. The side panels' deep blue low end all but
            // disappeared on the track; this palette keeps every band well
            // above the track's brightness. Loud peaks warm towards amber, not
            // white, so the pale grey-blue Notes stay distinct.
            var brightColor = frequency switch
            {
                < .34 => Lerp((96d, 132d, 255d), (56d, 192d, 242d), frequency / .34),
                < .68 => Lerp((56d, 192d, 242d), (86d, 232d, 162d), (frequency - .34) / .34),
                _ => Lerp((86d, 232d, 162d), (255d, 192d, 84d), (frequency - .68) / .32)
            };
            color = Lerp(
                Lerp(Scale(brightColor, .72), brightColor, gain),
                (255d, 206d, 120d),
                highlight * .6);
            var ramp = SmoothStep((level - .04) / .8);
            alpha = (byte)Math.Clamp(Math.Round(maximumAlpha * Math.Pow(ramp, .9)), 0, maximumAlpha);
        }
        pixels[offset] = (byte)Math.Clamp(Math.Round(color.B), 0, 255);
        pixels[offset + 1] = (byte)Math.Clamp(Math.Round(color.G), 0, 255);
        pixels[offset + 2] = (byte)Math.Clamp(Math.Round(color.R), 0, 255);
        pixels[offset + 3] = alpha;
    }

    private static (double R, double G, double B) Scale(
        (double R, double G, double B) color,
        double factor) =>
        (Math.Min(255, color.R * factor), Math.Min(255, color.G * factor), Math.Min(255, color.B * factor));

    private static double SmoothStep(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - 2 * value);
    }

    private static (double R, double G, double B) Lerp(
        (double R, double G, double B) first,
        (double R, double G, double B) second,
        double amount) =>
        (Lerp(first.R, second.R, amount), Lerp(first.G, second.G, amount), Lerp(first.B, second.B, amount));

    private static double Lerp(double first, double second, double amount) =>
        first + (second - first) * Math.Clamp(amount, 0, 1);
}
