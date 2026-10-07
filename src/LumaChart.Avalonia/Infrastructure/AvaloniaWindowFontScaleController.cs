using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using LumaChart.Application.Settings;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Applies the independent font multiplier used by the Windows client without
/// double-scaling controls whose font size is inherited from the Window.
/// Explicit local/style/template font sizes are captured once and always scaled
/// from that stable baseline, including controls materialized after startup.
/// </summary>
public sealed class AvaloniaWindowFontScaleController
{
    private sealed record FontBaseline(StyledProperty<double> Property, double Value);

    private readonly PlatformWindow window;
    private readonly ConditionalWeakTable<AvaloniaObject, FontBaseline> baselines = new();
    private readonly double windowFontSizeBaseline;
    private int capturedBaselineCount;

    public AvaloniaWindowFontScaleController(PlatformWindow window)
    {
        this.window = window ?? throw new ArgumentNullException(nameof(window));
        windowFontSizeBaseline = IsUsableFontSize(window.FontSize) ? window.FontSize : 12;
        CurrentScale = 1;
        AppliedWindowFontSize = windowFontSizeBaseline;
    }

    public double WindowFontSizeBaseline => windowFontSizeBaseline;

    public double CurrentScale { get; private set; }

    public double AppliedWindowFontSize { get; private set; }

    /// <summary>
    /// Number of unique visual/logical objects inspected by the most recent
    /// explicit Apply call. No recurring timer is owned by this controller.
    /// </summary>
    public int LastScannedObjectCount { get; private set; }

    public int LastFontCandidateCount { get; private set; }

    public int LastScaledElementCount { get; private set; }

    public int LastInheritedOrDefaultSkippedCount { get; private set; }

    /// <summary>
    /// Cumulative number of stable baselines captured. The baselines themselves
    /// are held in a ConditionalWeakTable, so this counter intentionally is not
    /// a count of live controls and does not keep dynamic controls alive.
    /// </summary>
    public int CapturedBaselineCount => capturedBaselineCount;

    public void Apply(double requestedScale)
    {
        var scale = DisplayScalePolicy.NormalizeFontScale(requestedScale);
        CurrentScale = scale;
        AppliedWindowFontSize = windowFontSizeBaseline * scale;
        window.SetCurrentValue(TemplatedControl.FontSizeProperty, AppliedWindowFontSize);

        var visited = new HashSet<AvaloniaObject>(ReferenceEqualityComparer.Instance);
        var candidateCount = 0;
        var scaledCount = 0;
        var skippedCount = 0;

        foreach (var visual in window.GetSelfAndVisualDescendants())
            ApplyToObject(visual, scale, visited, ref candidateCount, ref scaledCount, ref skippedCount);

        foreach (var logical in window.GetSelfAndLogicalDescendants())
        {
            if (logical is AvaloniaObject avaloniaObject)
                ApplyToObject(
                    avaloniaObject,
                    scale,
                    visited,
                    ref candidateCount,
                    ref scaledCount,
                    ref skippedCount);
        }

        LastScannedObjectCount = visited.Count;
        LastFontCandidateCount = candidateCount;
        LastScaledElementCount = scaledCount;
        LastInheritedOrDefaultSkippedCount = skippedCount;
    }

    /// <summary>
    /// Registers an explicit font baseline for content hosted outside the main
    /// window visual tree, such as an Avalonia Popup. The next window-scale pass
    /// will keep using this baseline instead of treating the already-scaled value
    /// as a new source size.
    /// </summary>
    public void Rebase(AvaloniaObject element, double baseFontSize)
    {
        ArgumentNullException.ThrowIfNull(element);
        var property = GetFontSizeProperty(element)
                       ?? throw new ArgumentException(
                           "The supplied object does not expose FontSize.",
                           nameof(element));
        if (!IsUsableFontSize(baseFontSize))
            throw new ArgumentOutOfRangeException(nameof(baseFontSize));

        baselines.Remove(element);
        baselines.Add(element, new FontBaseline(property, baseFontSize));
        capturedBaselineCount++;
        element.SetCurrentValue(property, baseFontSize * CurrentScale);
    }

    private void ApplyToObject(
        AvaloniaObject element,
        double scale,
        HashSet<AvaloniaObject> visited,
        ref int candidateCount,
        ref int scaledCount,
        ref int skippedCount)
    {
        if (ReferenceEquals(element, window) || !visited.Add(element)) return;

        var property = GetFontSizeProperty(element);
        if (property == null) return;
        candidateCount++;

        if (!baselines.TryGetValue(element, out var baseline))
        {
            var diagnostic = element.GetDiagnostic(property);
            if (diagnostic.Priority is BindingPriority.Unset or BindingPriority.Inherited)
            {
                skippedCount++;
                return;
            }

            var value = element.GetValue(property);
            if (!IsUsableFontSize(value)) return;
            baseline = new FontBaseline(property, value);
            baselines.Add(element, baseline);
            capturedBaselineCount++;
        }

        element.SetCurrentValue(baseline.Property, baseline.Value * scale);
        scaledCount++;
    }

    private static StyledProperty<double>? GetFontSizeProperty(AvaloniaObject element) => element switch
    {
        // AccessText derives from TextBlock in Avalonia 12.1 and therefore uses
        // the same inheritable FontSize property.
        AccessText => TextBlock.FontSizeProperty,
        TextBlock => TextBlock.FontSizeProperty,
        TemplatedControl => TemplatedControl.FontSizeProperty,
        _ => null
    };

    private static bool IsUsableFontSize(double value) =>
        double.IsFinite(value) && value > 0;
}
