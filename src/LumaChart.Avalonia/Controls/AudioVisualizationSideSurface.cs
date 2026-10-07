using System.Runtime.InteropServices;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Rendering;
using LumaChart.Core.Timing;

namespace LumaChart.Avalonia.Controls;

public enum AudioVisualizationSideKind
{
    Waveform,
    Spectrogram
}

public sealed record AudioVisualizationSideDiagnostics(
    AudioVisualizationSideKind Kind,
    bool VisualizationAttached,
    bool SceneReady,
    bool BuildPending,
    double? SceneBeat,
    int WaveformRows,
    int SpectrogramWidth,
    int SpectrogramRows,
    bool SpectrogramBitmapReady,
    long CompletedSceneBuilds,
    double LastUiThreadCpuBitmapMaterializationMilliseconds,
    double MaximumUiThreadCpuBitmapMaterializationMilliseconds,
    TimelinePerformanceSnapshot Performance);

public readonly record struct AudioVisualizationSideRuntimeCounters(
    bool SceneReady,
    long CompletedSceneBuilds,
    double LastUiThreadCpuBitmapMaterializationMilliseconds,
    double MaximumUiThreadCpuBitmapMaterializationMilliseconds);

/// <summary>
/// One retained custom control for an entire waveform or spectrogram side bar.
/// Sampling and TimingMap projection run on a worker thread; Render only draws
/// the cached three-viewport scene and clips it to the currently visible rows.
/// </summary>
public sealed class AudioVisualizationSideSurface : Control
{
    private const double ScenePrefetchViewportFraction = .65;

    private static readonly IBrush WaveformBackgroundBrush =
        new SolidColorBrush(Color.Parse("#E8171A1C"));
    private static readonly IBrush SpectrogramBackgroundBrush =
        new SolidColorBrush(Color.Parse("#F016191C"));
    private static readonly IPen CenterPen = new Pen(
        new SolidColorBrush(Color.Parse("#775C666A")), 1);
    private static readonly IPen WavePen = new Pen(
        new SolidColorBrush(Color.Parse("#D17FA4A7")), 1);
    private static readonly IPen GuidePen = new Pen(
        new SolidColorBrush(Color.Parse("#4D839195")), 1);
    private static readonly IPen PlayheadPen = new Pen(
        new SolidColorBrush(Color.Parse("#FFE05D69")), 1.5);

    private AudioVisualization? visualization;
    private TimingMap? timingMap;
    private double currentBeat;
    private double pixelsPerBeat = 150;
    private double playheadPosition = .79;
    private double spectrogramDetail = SpectrogramLevelMapping.DefaultDetail;
    private double sceneDetail = SpectrogramLevelMapping.DefaultDetail;
    private TimelineAudioBackgroundScene? scene;
    private AudioVisualization? sceneSource;
    private TimingMap? sceneMap;
    private double sceneScale;
    private double sceneHeight;
    private StreamGeometry? waveformGeometry;
    private WriteableBitmap? spectrogramBitmap;
    private WriteableBitmap? spectrogramBackBitmap;
    private CancellationTokenSource? sceneCancellation;
    private long sceneBuildVersion;
    private bool sceneBuildPending;
    private double sceneBuildBeat;
    private long completedSceneBuilds;
    private double lastUiThreadCpuBitmapMaterializationMilliseconds;
    private double maximumUiThreadCpuBitmapMaterializationMilliseconds;

    public AudioVisualizationSideSurface(AudioVisualizationSideKind kind)
    {
        Kind = kind;
        ClipToBounds = true;
        SizeChanged += (_, _) => InvalidateScene(preserveCompletedScene: true);
        DetachedFromVisualTree += (_, _) => InvalidateScene();
    }

    public AudioVisualizationSideKind Kind { get; }
    public TimelinePerformanceRecorder Performance { get; } = new();

    public void SetVisualization(AudioVisualization? value, TimingMap? map)
    {
        if (ReferenceEquals(visualization, value) && ReferenceEquals(timingMap, map)) return;
        visualization = value;
        timingMap = map;
        InvalidateScene();
    }

    /// <summary>
    /// Same detail setting as the background spectrogram, so both views of the
    /// spectrum show the same content.
    /// </summary>
    public void SetSpectrogramDetail(double detail)
    {
        var next = SpectrogramLevelMapping.NormalizeDetail(detail);
        if (Math.Abs(next - spectrogramDetail) < 1e-6) return;
        spectrogramDetail = next;
        if (Kind == AudioVisualizationSideKind.Spectrogram) InvalidateScene(preserveCompletedScene: true);
    }

    public void SetViewport(double beat, double beatHeight, double judgementLinePosition)
    {
        var nextBeat = double.IsFinite(beat) ? Math.Max(0, beat) : 0;
        var nextScale = Math.Clamp(double.IsFinite(beatHeight) ? beatHeight : 150, 24, 480);
        // This is the timeline's judgement line translated into panel space.
        // The panel starts below the timeline's top and is shorter, so a line
        // near the top or bottom of the timeline lands outside the panel's own
        // 15–95% band; clamping it there shifted every row of the panel away
        // from the Notes. Only a wildly wrong value is bounded.
        var nextLine = Math.Clamp(
            double.IsFinite(judgementLinePosition) ? judgementLinePosition : .79,
            -1,
            2);
        var sceneInvalid = Math.Abs(nextScale - pixelsPerBeat) > .001 ||
                           Math.Abs(nextLine - playheadPosition) > .0001;
        var beatChanged = Math.Abs(currentBeat - nextBeat) > 1e-9;
        currentBeat = nextBeat;
        pixelsPerBeat = nextScale;
        playheadPosition = nextLine;
        if (beatChanged) Performance.MarkInput();
        if (sceneInvalid) InvalidateScene(preserveCompletedScene: true);
        else InvalidateVisual();
    }

    /// <summary>
    /// Advances the retained scene with the editor's visual frame. Layout and
    /// scale stay unchanged, so this is intentionally cheaper than recalculating
    /// panel-to-timeline transforms on every playback frame.
    /// </summary>
    public void SetPlaybackBeat(double beat)
    {
        var nextBeat = double.IsFinite(beat) ? Math.Max(0, beat) : 0;
        if (Math.Abs(currentBeat - nextBeat) <= 1e-9) return;
        currentBeat = nextBeat;
        Performance.MarkInput();
        if (IsVisible) InvalidateVisual();
    }

    public AudioVisualizationSideDiagnostics GetDiagnostics() => new(
        Kind,
        visualization != null && timingMap != null,
        scene != null,
        sceneBuildPending,
        scene?.SceneBeat,
        scene?.WaveformPeaks?.Length ?? 0,
        scene?.Spectrogram?.Width ?? 0,
        scene?.Spectrogram?.Height ?? 0,
        spectrogramBitmap != null,
        completedSceneBuilds,
        lastUiThreadCpuBitmapMaterializationMilliseconds,
        maximumUiThreadCpuBitmapMaterializationMilliseconds,
        Performance.Snapshot());

    public AudioVisualizationSideRuntimeCounters GetRuntimeCounters() => new(
        scene != null,
        completedSceneBuilds,
        lastUiThreadCpuBitmapMaterializationMilliseconds,
        maximumUiThreadCpuBitmapMaterializationMilliseconds);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty) return;
        Performance.Reset();
        if (IsVisible) InvalidateVisual();
        else CancelSceneBuild();
    }

    public override void Render(DrawingContext context)
    {
        if (!IsVisible)
        {
            base.Render(context);
            return;
        }
        var startedAt = Performance.BeginRender();
        try
        {
            base.Render(context);
            var bounds = new Rect(Bounds.Size);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            context.DrawRectangle(
                Kind == AudioVisualizationSideKind.Waveform
                    ? WaveformBackgroundBrush
                    : SpectrogramBackgroundBrush,
                null,
                bounds,
                3,
                3);

            if (Kind == AudioVisualizationSideKind.Waveform)
            {
                var center = bounds.Width / 2;
                context.DrawLine(CenterPen, new Point(center, 0), new Point(center, bounds.Height));
            }

            EnsureScene();
            if (scene != null)
            {
                var (scale, offset) = TimelineRetainedAudioScenePolicy.SceneToView(
                    scene,
                    currentBeat,
                    pixelsPerBeat,
                    bounds.Height * playheadPosition,
                    bounds.Height);
                if (Kind == AudioVisualizationSideKind.Spectrogram &&
                    scene.Spectrogram != null && spectrogramBitmap != null)
                {
                    context.DrawImage(
                        spectrogramBitmap,
                        new Rect(
                            0,
                            scene.TopY * scale + offset,
                            bounds.Width,
                            scene.Spectrogram.Height * scene.RowPixels * scale));
                }
                else if (Kind == AudioVisualizationSideKind.Waveform &&
                         waveformGeometry != null)
                {
                    using (context.PushTransform(new Matrix(1, 0, 0, scale, 0, offset)))
                        context.DrawGeometry(null, WavePen, waveformGeometry);
                }
            }

            if (Kind == AudioVisualizationSideKind.Spectrogram)
            {
                context.DrawLine(GuidePen, new Point(bounds.Width * .34, 0), new Point(bounds.Width * .34, bounds.Height));
                context.DrawLine(GuidePen, new Point(bounds.Width * .68, 0), new Point(bounds.Width * .68, bounds.Height));
            }
            var playheadY = bounds.Height * playheadPosition;
            if (playheadY >= 0 && playheadY <= bounds.Height)
                context.DrawLine(PlayheadPen, new Point(0, playheadY), new Point(bounds.Width, playheadY));
        }
        finally
        {
            Performance.EndRender(startedAt, visitedNotes: 0);
        }
    }

    private static StreamGeometry? CreateWaveformGeometry(
        TimelineAudioBackgroundScene source,
        Rect bounds)
    {
        if (source.WaveformPeaks is not { Length: > 0 } peaks) return null;
        var geometry = new StreamGeometry();
        using var geometryContext = geometry.Open();
        var center = bounds.Width / 2;
        var maximumHalfWidth = Math.Max(2, bounds.Width * .46);
        for (var row = 0; row < peaks.Length; row++)
        {
            var peak = peaks[row];
            if (peak <= .002f) continue;
            var y = source.TopY + (row + .5) * source.RowPixels;
            var halfWidth = peak * maximumHalfWidth;
            geometryContext.BeginFigure(new Point(center - halfWidth, y), false);
            geometryContext.LineTo(new Point(center + halfWidth, y), true);
            geometryContext.EndFigure(false);
        }
        return geometry;
    }

    private void EnsureScene()
    {
        if (Bounds.Height <= 1 || visualization == null || timingMap == null) return;
        var sceneMatches = scene != null &&
                           ReferenceEquals(sceneSource, visualization) &&
                           ReferenceEquals(sceneMap, timingMap) &&
                           Math.Abs(sceneScale - pixelsPerBeat) < .001 &&
                           Math.Abs(sceneHeight - Bounds.Height) < .5 &&
                           (!double.IsFinite(scene.JudgementY) ||
                            Math.Abs(scene.JudgementY - Bounds.Height * playheadPosition) < .5) &&
                           (Kind != AudioVisualizationSideKind.Spectrogram ||
                            Math.Abs(sceneDetail - spectrogramDetail) < 1e-6);
        if (sceneMatches &&
            Math.Abs(currentBeat - scene!.SceneBeat) * pixelsPerBeat <=
            Bounds.Height * ScenePrefetchViewportFraction) return;
        if (sceneBuildPending &&
            Math.Abs(currentBeat - sceneBuildBeat) * pixelsPerBeat <=
            Bounds.Height * ScenePrefetchViewportFraction) return;
        QueueScene();
    }

    private void QueueScene()
    {
        if (visualization == null || timingMap == null) return;
        CancelSceneBuild();
        var source = visualization;
        var map = timingMap;
        var targetBeat = currentBeat;
        var scale = pixelsPerBeat;
        var height = Bounds.Height;
        var judgementY = height * playheadPosition;
        var includeWaveform = Kind == AudioVisualizationSideKind.Waveform;
        var includeSpectrogram = Kind == AudioVisualizationSideKind.Spectrogram;
        var detail = spectrogramDetail;
        var version = ++sceneBuildVersion;
        var cancellation = sceneCancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        sceneBuildPending = true;
        sceneBuildBeat = targetBeat;
        _ = Task.Run(
                () => TimelineAudioBackgroundSceneBuilder.BuildSidePanel(
                    source,
                    map,
                    targetBeat,
                    scale,
                    judgementY,
                    height,
                    includeWaveform,
                    includeSpectrogram,
                    token,
                    detail),
                token)
            .ContinueWith(task => Dispatcher.UIThread.Post(() =>
            {
                var completedScene = task.Status == TaskStatus.RanToCompletion
                    ? task.Result
                    : null;
                if (task.IsFaulted) _ = task.Exception;
                try
                {
                    if (version != sceneBuildVersion) return;
                    sceneBuildPending = false;
                    if (ReferenceEquals(sceneCancellation, cancellation))
                    {
                        sceneCancellation.Dispose();
                        sceneCancellation = null;
                    }
                    if (token.IsCancellationRequested || task.IsCanceled) return;
                    if (task.IsFaulted) return;
                    if (!ReferenceEquals(source, visualization) || !ReferenceEquals(map, timingMap)) return;
                    var nextWaveformGeometry = completedScene!.WaveformPeaks != null
                        ? CreateWaveformGeometry(completedScene, new Rect(Bounds.Size))
                        : null;
                    WriteableBitmap? nextSpectrogramBitmap = null;
                    if (completedScene.Spectrogram != null)
                    {
                        var materializationStartedAt = Stopwatch.GetTimestamp();
                        var reusableBackBitmap = spectrogramBackBitmap;
                        spectrogramBackBitmap = null;
                        nextSpectrogramBitmap = MaterializeSpectrogramBitmap(
                            completedScene.Spectrogram,
                            reusableBackBitmap);
                        lastUiThreadCpuBitmapMaterializationMilliseconds =
                            Stopwatch.GetElapsedTime(materializationStartedAt).TotalMilliseconds;
                        maximumUiThreadCpuBitmapMaterializationMilliseconds = Math.Max(
                            maximumUiThreadCpuBitmapMaterializationMilliseconds,
                            lastUiThreadCpuBitmapMaterializationMilliseconds);
                    }

                    var previousSpectrogramBitmap = spectrogramBitmap;
                    scene = completedScene;
                    sceneSource = source;
                    sceneMap = map;
                    sceneScale = scale;
                    sceneHeight = height;
                    sceneDetail = detail;
                    waveformGeometry = nextWaveformGeometry;
                    spectrogramBitmap = nextSpectrogramBitmap;
                    completedSceneBuilds++;
                    if (previousSpectrogramBitmap != null)
                    {
                        if (nextSpectrogramBitmap != null &&
                            previousSpectrogramBitmap.PixelSize == nextSpectrogramBitmap.PixelSize)
                            spectrogramBackBitmap = previousSpectrogramBitmap;
                        else
                            previousSpectrogramBitmap.Dispose();
                    }
                    if (nextSpectrogramBitmap == null)
                    {
                        spectrogramBackBitmap?.Dispose();
                        spectrogramBackBitmap = null;
                    }
                    InvalidateVisual();
                }
                finally
                {
                    completedScene?.Spectrogram?.Dispose();
                }
            }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static WriteableBitmap MaterializeSpectrogramBitmap(
        TimelineSpectrogramPixels source,
        WriteableBitmap? reusable)
    {
        var expectedSize = new PixelSize(source.Width, source.Height);
        if (reusable != null && reusable.PixelSize != expectedSize)
        {
            reusable.Dispose();
            reusable = null;
        }
        var bitmap = reusable
            ?? new WriteableBitmap(
                expectedSize,
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);
        try
        {
            using var buffer = bitmap.Lock();
            var sourceStride = source.Width * 4;
            if (buffer.RowBytes == sourceStride)
                Marshal.Copy(source.BgraPixels, 0, buffer.Address, source.ByteLength);
            else
                for (var row = 0; row < source.Height; row++)
                    Marshal.Copy(
                        source.BgraPixels,
                        row * sourceStride,
                        IntPtr.Add(buffer.Address, row * buffer.RowBytes),
                        sourceStride);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        return bitmap;
    }

    private void CancelSceneBuild()
    {
        sceneBuildVersion++;
        sceneCancellation?.Cancel();
        sceneCancellation?.Dispose();
        sceneCancellation = null;
        sceneBuildPending = false;
    }

    private void InvalidateScene(bool preserveCompletedScene = false)
    {
        CancelSceneBuild();
        if (preserveCompletedScene)
        {
            InvalidateVisual();
            return;
        }
        scene = null;
        sceneSource = null;
        sceneMap = null;
        waveformGeometry = null;
        spectrogramBitmap?.Dispose();
        spectrogramBitmap = null;
        spectrogramBackBitmap?.Dispose();
        spectrogramBackBitmap = null;
        InvalidateVisual();
    }
}
