namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Active timeline-track horizontal bounds in the audio panels' parent-space,
/// expressed in effective DIPs.
/// </summary>
public readonly record struct WindowsAudioTrackBounds(double Left, double Right)
{
    public bool IsUsable =>
        double.IsFinite(Left) &&
        double.IsFinite(Right) &&
        Right > Left;
}

public readonly record struct WindowsAudioPanelLayout(
    double X,
    double Y,
    double Width,
    double Height,
    bool ToolbarOnly)
{
    public double Right => X + Width;
}

public readonly record struct WindowsAudioPanelsLayout(
    WindowsAudioPanelLayout Waveform,
    WindowsAudioPanelLayout Spectrogram);

/// <summary>
/// Pure WPF-compatible Windows audio-panel layout rules. No Controls are
/// allocated here; callers can apply the returned DIP geometry to one custom
/// waveform surface and one custom spectrogram surface.
/// </summary>
public static class WindowsAudioPanelLayoutPolicy
{
    public const double TrackGap = 8;
    public const double WorkspaceEdgeInset = 4;
    public const double ToolbarWidth = 92;
    public const double ToolbarHeight = 25;
    public const double MinimumToolbarY = -40;
    public const double ToolbarBottomReservation = 105;

    public static WindowsAudioPanelsLayout Resolve(
        WindowsAudioPanelSettings settings,
        double parentWidth,
        double parentHeight,
        WindowsAudioTrackBounds track)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();
        var normalizedParent = NormalizeParent(parentWidth, parentHeight);
        var waveform = ResolveWaveform(
            normalized.Waveform,
            normalizedParent.Width,
            normalizedParent.Height,
            track);
        var spectrogram = ResolveSpectrogram(
            normalized.Spectrogram,
            normalizedParent.Width,
            normalizedParent.Height,
            track,
            waveform);
        return new WindowsAudioPanelsLayout(waveform, spectrogram);
    }

    public static WindowsAudioPanelLayout ResolveWaveform(
        WindowsAudioPanelState state,
        double parentWidth,
        double parentHeight,
        WindowsAudioTrackBounds track)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalized = state.Normalize(WindowsAudioPanelKind.Waveform);
        var bounds = NormalizeParent(parentWidth, parentHeight);
        var width = EffectiveWidth(normalized);
        var x = normalized.Docked
            ? ResolveWaveformDockX(width, bounds.Width, track)
            : ClampX(normalized.X, width, bounds.Width);
        var y = ResolveY(normalized, bounds.Height);
        return new WindowsAudioPanelLayout(
            x,
            y,
            width,
            normalized.IsToolbarOnly ? ToolbarHeight : bounds.Height,
            normalized.IsToolbarOnly);
    }

    public static WindowsAudioPanelLayout ResolveSpectrogram(
        WindowsAudioPanelState state,
        double parentWidth,
        double parentHeight,
        WindowsAudioTrackBounds track,
        WindowsAudioPanelLayout waveform)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalized = state.Normalize(WindowsAudioPanelKind.Spectrogram);
        var bounds = NormalizeParent(parentWidth, parentHeight);
        var width = EffectiveWidth(normalized);
        var x = normalized.Docked
            ? ResolveSpectrogramDockX(width, bounds.Width, track, waveform)
            : ClampX(normalized.X, width, bounds.Width);
        var y = ResolveY(normalized, bounds.Height);
        return new WindowsAudioPanelLayout(
            x,
            y,
            width,
            normalized.IsToolbarOnly ? ToolbarHeight : bounds.Height,
            normalized.IsToolbarOnly);
    }

    /// <summary>
    /// Applies a user drag and detaches the panel from automatic track docking.
    /// Expanded Side panels accept horizontal movement only; collapsed and
    /// Background toolbars accept both axes.
    /// </summary>
    public static WindowsAudioPanelState ApplyDrag(
        WindowsAudioPanelState state,
        WindowsAudioPanelKind kind,
        double proposedX,
        double proposedY,
        double parentWidth,
        double parentHeight)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalized = state.Normalize(kind);
        var bounds = NormalizeParent(parentWidth, parentHeight);
        var x = ClampX(proposedX, EffectiveWidth(normalized), bounds.Width);
        var y = normalized.IsToolbarOnly
            ? ClampToolbarY(proposedY, bounds.Height)
            : 0;
        return new WindowsAudioPanelState(
            x,
            y,
            normalized.Width,
            normalized.Collapsed,
            docked: false,
            normalized.Mode).Normalize(kind);
    }

    /// <summary>
    /// Clamps a free panel after a workspace resize without changing its
    /// collapsed, docked, or display-mode state. Docked positioning is resolved
    /// by <see cref="Resolve"/> because it also needs the active track.
    /// </summary>
    public static WindowsAudioPanelState ClampAfterResize(
        WindowsAudioPanelState state,
        WindowsAudioPanelKind kind,
        double parentWidth,
        double parentHeight)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalized = state.Normalize(kind);
        if (normalized.Docked) return normalized;
        var bounds = NormalizeParent(parentWidth, parentHeight);
        return new WindowsAudioPanelState(
            ClampX(normalized.X, EffectiveWidth(normalized), bounds.Width),
            ResolveY(normalized, bounds.Height),
            normalized.Width,
            normalized.Collapsed,
            normalized.Docked,
            normalized.Mode).Normalize(kind);
    }

    /// <summary>
    /// Changes the remembered expanded width, clamps it to the WPF range, and
    /// keeps a free panel inside the resized workspace.
    /// </summary>
    public static WindowsAudioPanelState ApplyWidth(
        WindowsAudioPanelState state,
        WindowsAudioPanelKind kind,
        double proposedWidth,
        double parentWidth,
        double parentHeight)
    {
        ArgumentNullException.ThrowIfNull(state);
        var withWidth = state.WithWidth(proposedWidth).Normalize(kind);
        return ClampAfterResize(withWidth, kind, parentWidth, parentHeight);
    }

    public static WindowsAudioPanelSettings Reset() => WindowsAudioPanelSettings.Reset();

    private static double ResolveWaveformDockX(
        double panelWidth,
        double parentWidth,
        WindowsAudioTrackBounds track)
    {
        var maximum = MaximumX(parentWidth, panelWidth);
        if (!track.IsUsable) return maximum;

        var left = track.Left - TrackGap - panelWidth;
        if (left >= WorkspaceEdgeInset) return Math.Clamp(left, 0, maximum);
        return Math.Clamp(track.Right + TrackGap, 0, maximum);
    }

    private static double ResolveSpectrogramDockX(
        double panelWidth,
        double parentWidth,
        WindowsAudioTrackBounds track,
        WindowsAudioPanelLayout waveform)
    {
        var maximum = MaximumX(parentWidth, panelWidth);
        if (!track.IsUsable) return maximum;

        var right = track.Right + TrackGap;
        var rightFitsWorkspace = right + panelWidth <= parentWidth - WorkspaceEdgeInset;
        var waveformOccupiesRight =
            waveform.X >= track.Right - 1 &&
            right < waveform.Right + TrackGap;
        if (rightFitsWorkspace && !waveformOccupiesRight)
            return Math.Clamp(right, 0, maximum);

        var left = track.Left - TrackGap - panelWidth;
        if (waveform.Right <= track.Left + 1)
            left = waveform.X - TrackGap - panelWidth;
        return Math.Clamp(left, 0, maximum);
    }

    private static double EffectiveWidth(WindowsAudioPanelState state) =>
        state.IsToolbarOnly ? ToolbarWidth : state.Width;

    private static double ResolveY(WindowsAudioPanelState state, double parentHeight) =>
        state.IsToolbarOnly ? ClampToolbarY(state.Y, parentHeight) : 0;

    private static double ClampX(double x, double panelWidth, double parentWidth)
    {
        var finiteX = double.IsFinite(x) ? x : 0;
        return Math.Clamp(finiteX, 0, MaximumX(parentWidth, panelWidth));
    }

    private static double ClampToolbarY(double y, double parentHeight)
    {
        var finiteY = double.IsFinite(y) ? y : 0;
        var maximum = Math.Max(MinimumToolbarY, parentHeight - ToolbarBottomReservation);
        return Math.Clamp(finiteY, MinimumToolbarY, maximum);
    }

    private static double MaximumX(double parentWidth, double panelWidth) =>
        Math.Max(0, parentWidth - panelWidth);

    private static (double Width, double Height) NormalizeParent(double width, double height) => (
        double.IsFinite(width) ? Math.Max(0, width) : 0,
        double.IsFinite(height) ? Math.Max(0, height) : 0);
}
