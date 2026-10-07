using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace LumaChart.Avalonia.Infrastructure;

public readonly record struct AvaloniaPopupScalePlan(
    double UiScale,
    double FontScale,
    double BaseFontSize,
    double AppliedFontSize);

public sealed record AvaloniaPopupScaleDiagnostics(
    double UiScale,
    double FontScale,
    double BaseFontSize,
    double AppliedFontSize,
    bool PopupRootFound,
    double PopupRootScaleX,
    double PopupRootScaleY,
    double PopupRootWidthDip,
    double PopupRootHeightDip);

/// <summary>
/// Context menus live in an independent PopupRoot and therefore do not inherit
/// the main window's LayoutTransformControl.  This controller reapplies the two
/// WPF-compatible multipliers from stable baselines on every open.
/// </summary>
public sealed class AvaloniaPopupScaleController
{
    private readonly Func<WindowsDisplayScaleSettings> settingsProvider;

    public AvaloniaPopupScaleController(Func<WindowsDisplayScaleSettings> settingsProvider)
    {
        this.settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
    }

    public AvaloniaPopupScaleDiagnostics? LastDiagnostics { get; private set; }

    public static AvaloniaPopupScalePlan Resolve(
        double baseFontSize,
        WindowsDisplayScaleSettings settings)
    {
        if (!double.IsFinite(baseFontSize) || baseFontSize <= 0) baseFontSize = 12;
        settings = (settings ?? throw new ArgumentNullException(nameof(settings))).Normalize();
        return new AvaloniaPopupScalePlan(
            settings.UiScale,
            settings.FontScale,
            baseFontSize,
            baseFontSize * settings.FontScale);
    }

    public void Attach(ContextMenu menu, double baseFontSize = 12)
    {
        ArgumentNullException.ThrowIfNull(menu);
        menu.Opening += (_, _) => ApplyBeforeOpen(menu, baseFontSize);
        menu.Opened += (_, _) => ApplyOpenedRoot(menu, baseFontSize);
    }

    public AvaloniaPopupScalePlan ApplyBeforeOpen(ContextMenu menu, double baseFontSize = 12)
    {
        ArgumentNullException.ThrowIfNull(menu);
        var plan = Resolve(baseFontSize, settingsProvider());
        menu.FontSize = plan.AppliedFontSize;
        return plan;
    }

    public AvaloniaPopupScaleDiagnostics ApplyOpenedRoot(
        ContextMenu menu,
        double baseFontSize = 12)
    {
        ArgumentNullException.ThrowIfNull(menu);
        var plan = ApplyBeforeOpen(menu, baseFontSize);
        var popupRoot = TopLevel.GetTopLevel(menu) as PopupRoot;
        if (popupRoot != null)
            popupRoot.Transform = new ScaleTransform(plan.UiScale, plan.UiScale);

        LastDiagnostics = new AvaloniaPopupScaleDiagnostics(
            plan.UiScale,
            plan.FontScale,
            plan.BaseFontSize,
            plan.AppliedFontSize,
            popupRoot != null,
            popupRoot?.Transform is ScaleTransform scale ? scale.ScaleX : 0,
            popupRoot?.Transform is ScaleTransform scaleY ? scaleY.ScaleY : 0,
            popupRoot?.Bounds.Width ?? 0,
            popupRoot?.Bounds.Height ?? 0);
        return LastDiagnostics;
    }
}
