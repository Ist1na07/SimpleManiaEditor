using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;

namespace LumaChart.Avalonia.Controls;

/// <summary>
/// Vector recreation of the WPF 1.1.0 default-user-avatar asset. The 512-unit
/// geometry and colors are taken from the checked-in WPF SVG source so every
/// Windows Avalonia account surface has the same fallback instead of initials.
/// </summary>
public sealed class WindowsDefaultUserAvatar : Border
{
    public const string ContractVersion = "wpf-1.1.0-default-user-avatar-r1";
    public const string BackgroundColor = "#181D20";
    public const string ForegroundColor = "#8AADAF";
    public const string BodyGeometry =
        "M126,400 C134,322 186,274 256,274 S378,322 386,400";
    private readonly Viewbox defaultArtwork;

    public WindowsDefaultUserAvatar(
        double size,
        IBrush? borderBrush = null,
        double borderThickness = 0,
        string automationId = "Online.DefaultUserAvatar")
    {
        if (!double.IsFinite(size) || size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        if (!double.IsFinite(borderThickness) || borderThickness < 0)
            throw new ArgumentOutOfRangeException(nameof(borderThickness));

        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(size / 2);
        BorderBrush = borderBrush;
        BorderThickness = new Thickness(borderThickness);
        ClipToBounds = true;

        var art = new Canvas
        {
            Width = 512,
            Height = 512,
            Background = Brush(BackgroundColor)
        };
        var head = new Ellipse
        {
            Width = 136,
            Height = 136,
            Fill = Brush(ForegroundColor)
        };
        Canvas.SetLeft(head, 188);
        Canvas.SetTop(head, 122);
        art.Children.Add(head);
        art.Children.Add(new AvaloniaPath
        {
            Width = 512,
            Height = 512,
            Data = Geometry.Parse(BodyGeometry),
            Stretch = Stretch.None,
            Stroke = Brush(ForegroundColor),
            StrokeThickness = 34,
            StrokeLineCap = PenLineCap.Round
        });

        RemoteImage = new Image
        {
            Width = size,
            Height = size,
            Stretch = Stretch.UniformToFill
        };
        defaultArtwork = new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.UniformToFill,
            Child = art
        };
        RemoteImage.PropertyChanged += (_, change) =>
        {
            if (change.Property == Image.SourceProperty)
                defaultArtwork.IsVisible = ShouldShowDefaultArtwork(RemoteImage.Source);
        };
        Child = new Grid
        {
            Children =
            {
                defaultArtwork,
                RemoteImage
            }
        };
        ArgumentException.ThrowIfNullOrWhiteSpace(automationId);
        AutomationProperties.SetAutomationId(this, automationId);
        AutomationProperties.SetName(this, "默认用户头像");
    }

    public Image RemoteImage { get; }

    public bool IsDefaultArtworkVisible => defaultArtwork.IsVisible;

    public static bool ShouldShowDefaultArtwork(object? remoteSource) => remoteSource == null;

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new ControlAutomationPeer(this);

    private static IBrush Brush(string value) =>
        new SolidColorBrush(Color.Parse(value));
}
