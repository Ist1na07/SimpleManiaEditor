using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace LumaChart.Avalonia.Infrastructure;

public readonly record struct WindowsDisplayScaleMetrics(
    double Scaling,
    PixelRect ScreenPixelBounds,
    PixelRect WorkAreaPixelBounds,
    double EffectiveScreenWidth,
    double EffectiveScreenHeight,
    double EffectiveWorkAreaWidth,
    double EffectiveWorkAreaHeight)
{
    public static WindowsDisplayScaleMetrics Empty { get; } = new(
        1,
        default,
        default,
        0,
        0,
        0,
        0);
}

public sealed record WindowsDisplayScaleDiagnostics(
    double RequestedUiScale,
    double RequestedFontScale,
    double ActualRootScaleX,
    double ActualRootScaleY,
    double ActualFontScale,
    bool UsesLayoutTransform,
    double ScreenScaling,
    double EffectiveScreenWidth,
    double EffectiveScreenHeight,
    double EffectiveWorkAreaWidth,
    double EffectiveWorkAreaHeight,
    double WindowMinimumWidth,
    double WindowMinimumHeight,
    double WindowFontSize,
    int FontScannedObjectCount,
    int FontCandidateCount,
    int FontScaledElementCount,
    int FontInheritedOrDefaultSkippedCount,
    int FontCapturedBaselineCount);

public static class WindowsDisplayScaleStepPolicy
{
    public const double StepPercent = 5;

    public static double ResolvePercent(
        double current,
        double minimum,
        double maximum,
        int direction)
    {
        if (!double.IsFinite(minimum) ||
            !double.IsFinite(maximum) ||
            minimum > maximum)
            throw new ArgumentOutOfRangeException(nameof(minimum));
        var safeCurrent = double.IsFinite(current)
            ? Math.Clamp(current, minimum, maximum)
            : minimum;
        if (direction == 0) return safeCurrent;
        var next = direction > 0
            ? (Math.Floor(safeCurrent / StepPercent) + 1) * StepPercent
            : (Math.Ceiling(safeCurrent / StepPercent) - 1) * StepPercent;
        return Math.Clamp(next, minimum, maximum);
    }
}

/// <summary>
/// Owns Windows Avalonia's WPF-compatible layout and font scaling behavior.
/// Screen bounds supplied by Avalonia are physical pixels and are converted to
/// effective DIPs with <see cref="Screen.Scaling"/> before applying policy.
/// </summary>
public sealed class WindowsDisplayScaleController
{
    private const double StartupBaseWidth = 1320;
    private const double StartupBaseHeight = 800;
    private const double WorkAreaStartupFraction = .94;

    private readonly PlatformWindow window;
    private readonly LayoutTransformControl interfaceRoot;
    private readonly AvaloniaWindowFontScaleController fontScaleController;
    private readonly ScaleTransform interfaceScaleTransform = new(1, 1);
    private WindowsDisplayScaleMetrics lastMetrics = WindowsDisplayScaleMetrics.Empty;
    private bool hasScreenMetrics;

    public WindowsDisplayScaleController(
        PlatformWindow window,
        LayoutTransformControl interfaceRoot,
        AvaloniaWindowFontScaleController fontScaleController,
        WindowsDisplayScaleSettings settings)
    {
        this.window = window ?? throw new ArgumentNullException(nameof(window));
        this.interfaceRoot = interfaceRoot ?? throw new ArgumentNullException(nameof(interfaceRoot));
        this.fontScaleController = fontScaleController ??
                                   throw new ArgumentNullException(nameof(fontScaleController));
        CurrentSettings = (settings ?? throw new ArgumentNullException(nameof(settings))).Normalize();

        // LayoutTransformControl performs a real measure/arrange transform. Its
        // render-transform compatibility switch must stay disabled so pointer
        // coordinates and desired sizes follow the scaled layout.
        interfaceRoot.UseRenderTransform = false;
        interfaceRoot.LayoutTransform = interfaceScaleTransform;
        ApplyCore(lastMetrics);
    }

    public WindowsDisplayScaleSettings CurrentSettings { get; private set; }

    public ScaleTransform InterfaceScaleTransform => interfaceScaleTransform;

    public double ActualUiScale => interfaceScaleTransform.ScaleX;

    public double ActualFontScale => fontScaleController.CurrentScale;

    public double ScreenScaling => lastMetrics.Scaling;

    public double EffectiveScreenWidth => lastMetrics.EffectiveScreenWidth;

    public double EffectiveScreenHeight => lastMetrics.EffectiveScreenHeight;

    public double EffectiveWorkAreaWidth => lastMetrics.EffectiveWorkAreaWidth;

    public double EffectiveWorkAreaHeight => lastMetrics.EffectiveWorkAreaHeight;

    public WindowsDisplayScaleMetrics ScreenMetrics => lastMetrics;

    public WindowsDisplayScaleDiagnostics Diagnostics => new(
        CurrentSettings.UiScale,
        CurrentSettings.FontScale,
        interfaceScaleTransform.ScaleX,
        interfaceScaleTransform.ScaleY,
        fontScaleController.CurrentScale,
        !interfaceRoot.UseRenderTransform &&
        ReferenceEquals(interfaceRoot.LayoutTransform, interfaceScaleTransform),
        lastMetrics.Scaling,
        lastMetrics.EffectiveScreenWidth,
        lastMetrics.EffectiveScreenHeight,
        lastMetrics.EffectiveWorkAreaWidth,
        lastMetrics.EffectiveWorkAreaHeight,
        window.MinWidth,
        window.MinHeight,
        window.FontSize,
        fontScaleController.LastScannedObjectCount,
        fontScaleController.LastFontCandidateCount,
        fontScaleController.LastScaledElementCount,
        fontScaleController.LastInheritedOrDefaultSkippedCount,
        fontScaleController.CapturedBaselineCount);

    /// <summary>
    /// Applies the first-screen recommendation once, applies saved scaling, and
    /// initializes a normal window to the WPF 1320x800 scaled startup size. This
    /// is the only method that changes the current Width/Height and Position.
    /// </summary>
    public WindowsDisplayScaleSettings InitializeForScreen(Screen? screen)
    {
        var metrics = ResolveMetrics(screen);
        // A temporarily unavailable platform screen must not permanently burn
        // the one-shot recommendation flag at 100%. Leave an uninitialized
        // setting untouched so a later real Windows screen can initialize it.
        if (CurrentSettings.DisplayScaleInitialized ||
            metrics.EffectiveScreenWidth > 0 && metrics.EffectiveScreenHeight > 0)
        {
            CurrentSettings = CurrentSettings.InitializeForFirstScreen(
                metrics.EffectiveScreenWidth,
                metrics.EffectiveScreenHeight);
        }
        ApplyCore(metrics);

        if (window.SupportsDesktopWindowSizing &&
            window.WindowState == WindowState.Normal)
            InitializeNormalWindowBounds(metrics);
        return CurrentSettings;
    }

    /// <summary>
    /// Applies current settings and minimum sizes for the supplied/current
    /// screen without resizing the current window.
    /// </summary>
    public void Apply(Screen? screen = null) => ApplyCore(ResolveMetrics(screen));

    public WindowsDisplayScaleSettings UpdateScales(
        double requestedUiScale,
        double requestedFontScale,
        Screen? screen = null)
    {
        CurrentSettings = new WindowsDisplayScaleSettings(
            requestedUiScale,
            requestedFontScale,
            displayScaleInitialized: true);
        Apply(screen);
        return CurrentSettings;
    }

    public WindowsDisplayScaleSettings UpdateUiScale(
        double requestedUiScale,
        Screen? screen = null) =>
        UpdateScales(requestedUiScale, CurrentSettings.FontScale, screen);

    public WindowsDisplayScaleSettings UpdateFontScale(
        double requestedFontScale,
        Screen? screen = null) =>
        UpdateScales(CurrentSettings.UiScale, requestedFontScale, screen);

    /// <summary>
    /// Explicitly rescans content created since the previous Apply. Callers
    /// queue this after materialization; the controller never scans from a
    /// recurring UI timer.
    /// </summary>
    public void ApplyFontToMaterializedContent() =>
        fontScaleController.Apply(CurrentSettings.FontScale);

    public static WindowsDisplayScaleMetrics CalculateMetrics(
        PixelRect screenBounds,
        PixelRect workingArea,
        double scaling)
    {
        var safeScaling = double.IsFinite(scaling) && scaling > 0 ? scaling : 1;
        var effectiveWorkingArea = workingArea.Width > 0 && workingArea.Height > 0
            ? workingArea
            : screenBounds;
        return new WindowsDisplayScaleMetrics(
            safeScaling,
            screenBounds,
            effectiveWorkingArea,
            screenBounds.Width > 0 ? screenBounds.Width / safeScaling : 0,
            screenBounds.Height > 0 ? screenBounds.Height / safeScaling : 0,
            effectiveWorkingArea.Width > 0 ? effectiveWorkingArea.Width / safeScaling : 0,
            effectiveWorkingArea.Height > 0 ? effectiveWorkingArea.Height / safeScaling : 0);
    }

    private WindowsDisplayScaleMetrics ResolveMetrics(Screen? requestedScreen)
    {
        var screen = requestedScreen;
        if (screen == null)
        {
            try
            {
                screen = window.ScreenFromHost();
            }
            catch (InvalidOperationException)
            {
                // The TopLevel may not have a platform handle yet. Constructor
                // application still uses persisted scales and startup calls this
                // method again after Opened with a real Screen.
            }
        }

        if (screen == null) return hasScreenMetrics ? lastMetrics : WindowsDisplayScaleMetrics.Empty;
        var metrics = CalculateMetrics(screen.Bounds, screen.WorkingArea, screen.Scaling);
        lastMetrics = metrics;
        hasScreenMetrics = true;
        return metrics;
    }

    private void ApplyCore(WindowsDisplayScaleMetrics metrics)
    {
        CurrentSettings = CurrentSettings.Normalize();
        lastMetrics = metrics;
        if (metrics.EffectiveScreenWidth > 0 && metrics.EffectiveScreenHeight > 0)
            hasScreenMetrics = true;

        interfaceRoot.UseRenderTransform = false;
        if (!ReferenceEquals(interfaceRoot.LayoutTransform, interfaceScaleTransform))
            interfaceRoot.LayoutTransform = interfaceScaleTransform;
        interfaceScaleTransform.ScaleX = CurrentSettings.UiScale;
        interfaceScaleTransform.ScaleY = CurrentSettings.UiScale;
        fontScaleController.Apply(CurrentSettings.FontScale);

        if (!window.SupportsDesktopWindowSizing)
        {
            window.MinWidth = 0;
            window.MinHeight = 0;
            return;
        }

        var scaledMinimumWidth =
            WindowsTitleBarParityPolicy.MinimumWindowWidth * CurrentSettings.UiScale;
        var scaledMinimumHeight =
            WindowsTitleBarParityPolicy.MinimumWindowHeight * CurrentSettings.UiScale;
        window.MinWidth = metrics.EffectiveWorkAreaWidth > 0
            ? Math.Min(scaledMinimumWidth, metrics.EffectiveWorkAreaWidth)
            : scaledMinimumWidth;
        window.MinHeight = metrics.EffectiveWorkAreaHeight > 0
            ? Math.Min(scaledMinimumHeight, metrics.EffectiveWorkAreaHeight)
            : scaledMinimumHeight;
    }

    private void InitializeNormalWindowBounds(WindowsDisplayScaleMetrics metrics)
    {
        var requestedWidth = StartupBaseWidth * CurrentSettings.UiScale;
        var requestedHeight = StartupBaseHeight * CurrentSettings.UiScale;
        var maximumWidth = metrics.EffectiveWorkAreaWidth > 0
            ? metrics.EffectiveWorkAreaWidth * WorkAreaStartupFraction
            : requestedWidth;
        var maximumHeight = metrics.EffectiveWorkAreaHeight > 0
            ? metrics.EffectiveWorkAreaHeight * WorkAreaStartupFraction
            : requestedHeight;
        window.Width = Math.Min(requestedWidth, maximumWidth);
        window.Height = Math.Min(requestedHeight, maximumHeight);

        var workArea = metrics.WorkAreaPixelBounds;
        if (workArea.Width <= 0 || workArea.Height <= 0) return;
        var physicalWidth = window.Width * metrics.Scaling;
        var physicalHeight = window.Height * metrics.Scaling;
        var left = workArea.X + Math.Max(0, (workArea.Width - physicalWidth) / 2);
        var top = workArea.Y + Math.Max(0, (workArea.Height - physicalHeight) / 2);
        window.Position = new PixelPoint(RoundPixelCoordinate(left), RoundPixelCoordinate(top));
    }

    private static int RoundPixelCoordinate(double value)
    {
        if (!double.IsFinite(value)) return 0;
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(rounded, int.MinValue, int.MaxValue);
    }
}
