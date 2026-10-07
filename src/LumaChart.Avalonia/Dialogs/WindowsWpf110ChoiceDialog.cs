using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Dialogs;

internal enum WindowsWpf110DialogTone
{
    Information,
    Warning,
    Error,
    Question
}

internal sealed record WindowsWpf110DialogButton<TResult>(
    TResult Result,
    string Label,
    string AutomationId,
    bool IsDefault = false,
    bool IsCancel = false,
    string? AutomationName = null,
    bool IsDestructive = false);

internal sealed record WindowsWpf110DialogDiagnostics(
    string ContractVersion,
    double RequestedUiScale,
    double RequestedFontScale,
    double ActualRootScaleX,
    double ActualRootScaleY,
    double WindowWidth,
    double HeaderHeight,
    double FooterHeight,
    double AccentWidth,
    double MessageFontSize,
    bool UsesLayoutTransform,
    bool HasSystemDecorations,
    bool ShowsInTaskbar);

internal sealed record WindowsWpf110DialogPalette(
    Color Accent,
    Color Badge,
    Color GlyphForeground);

/// <summary>
/// Shared Windows Avalonia equivalent of WPF 1.1.0 LumaDialogWindow.  Dialog
/// behavior stays in each feature-specific wrapper while the chrome, density,
/// owner modality and keyboard/copy contract have one exact source of truth.
/// </summary>
internal class WindowsWpf110ChoiceDialog<TResult> : PlatformWindow
{
    public const string ContractVersion = "wpf-1.1.0-dialog-r1";
    public const double BaseWidth = 500;
    public const double BaseMinWidth = 420;
    public const double BaseMaxWidth = 680;
    public const double BaseMaxHeight = 620;
    public const double HeaderHeight = 44;
    public const double FooterHeight = 58;
    public const double AccentWidth = 3;
    public const double MessageFontSize = 13;

    private static readonly IBrush WindowBrush = Brush("#131619");
    private static readonly IBrush PanelBrush = Brush("#15191C");
    private static readonly IBrush HeaderBrush = Brush("#1A1E21");
    private static readonly IBrush FooterBrush = Brush("#171B1E");
    private static readonly IBrush StrokeBrush = Brush("#3A4246");
    private static readonly IBrush DividerBrush = Brush("#30363A");
    private static readonly IBrush ToolBrush = Brush("#1A1D20");
    private static readonly IBrush ToolBorderBrush = Brush("#34393D");
    private static readonly IBrush ToolForeground = Brush("#D0D4D6");
    private static readonly IBrush PrimaryBrush = Brush("#38676C");
    private static readonly IBrush PrimaryBorderBrush = Brush("#4A777B");
    private static readonly IBrush CaptionBrush = Brush("#E1E6E7");
    private static readonly IBrush MessageBrush = Brush("#D7DDDE");
    private static readonly IBrush HintBrush = Brush("#687579");
    private static readonly IBrush BrandBrush = Brush("#75969A");
    private readonly TResult closeResult;
    private readonly string message;

    protected WindowsWpf110ChoiceDialog(
        string caption,
        string message,
        WindowsWpf110DialogTone tone,
        IReadOnlyList<WindowsWpf110DialogButton<TResult>> buttons,
        TResult closeResult,
        double uiScale,
        double fontScale,
        string automationId,
        string messageAutomationId = "Dialog.Message",
        string? windowTitle = null,
        string iconAutomationId = "Dialog.IconBadge",
        string? iconAutomationHelpText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caption);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(buttons);
        if (buttons.Count == 0) throw new ArgumentException("At least one dialog button is required.", nameof(buttons));

        uiScale = Math.Clamp(double.IsFinite(uiScale) ? uiScale : 1, .75, 2);
        fontScale = Math.Clamp(double.IsFinite(fontScale) ? fontScale : 1, .75, 2);
        this.closeResult = closeResult;
        this.message = message;

        Title = string.IsNullOrWhiteSpace(windowTitle) ? caption : windowTitle;
        Width = BaseWidth * uiScale;
        MinWidth = BaseMinWidth * uiScale;
        MaxWidth = BaseMaxWidth * uiScale;
        MaxHeight = BaseMaxHeight * uiScale;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowDecorations = WindowDecorations.None;
        Background = WindowBrush;
        AutomationProperties.SetAutomationId(this, automationId);
        AutomationProperties.SetHelpText(this, AvaloniaLocalizationCatalog.Format("契约={0}; 标题={1}", ContractVersion, caption));

        var rootScale = new ScaleTransform(uiScale, uiScale);
        var transformedRoot = new LayoutTransformControl
        {
            UseRenderTransform = false,
            LayoutTransform = rootScale,
            Child = BuildContent(
                caption,
                message,
                tone,
                buttons,
                fontScale,
                messageAutomationId,
                iconAutomationId,
                iconAutomationHelpText)
        };
        Content = transformedRoot;
        Diagnostics = new WindowsWpf110DialogDiagnostics(
            ContractVersion,
            uiScale,
            fontScale,
            rootScale.ScaleX,
            rootScale.ScaleY,
            Width,
            HeaderHeight,
            FooterHeight,
            AccentWidth,
            MessageFontSize * fontScale,
            !transformedRoot.UseRenderTransform &&
            ReferenceEquals(transformedRoot.LayoutTransform, rootScale),
            WindowDecorations != WindowDecorations.None,
            ShowInTaskbar);

        AddHandler(
            InputElement.KeyDownEvent,
            HandlePreviewKeyDown,
            RoutingStrategies.Tunnel);
    }

    public WindowsWpf110DialogDiagnostics Diagnostics { get; }

    private Control BuildContent(
        string caption,
        string dialogMessage,
        WindowsWpf110DialogTone tone,
        IReadOnlyList<WindowsWpf110DialogButton<TResult>> buttons,
        double fontScale,
        string messageAutomationId,
        string iconAutomationId,
        string? iconAutomationHelpText)
    {
        var (glyph, palette) = ToneVisuals(tone);
        var accent = new SolidColorBrush(palette.Accent);
        var badge = new SolidColorBrush(palette.Badge);
        var glyphForeground = new SolidColorBrush(palette.GlyphForeground);
        var shell = new Grid { ColumnDefinitions = new ColumnDefinitions($"{AccentWidth},*") };
        shell.Children.Add(new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(4, 0, 0, 4)
        });

        var root = new Border
        {
            Background = PanelBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = shell
        };
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions($"{HeaderHeight},Auto,{FooterHeight}")
        };
        Grid.SetColumn(grid, 1);
        shell.Children.Add(grid);

        var header = BuildHeader(caption, fontScale);
        grid.Children.Add(header);

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("50,*"),
            Margin = new Thickness(21, 20, 23, 21)
        };
        var iconGlyph = new TextBlock
        {
            Text = glyph,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Cascadia Mono,Consolas"),
            FontSize = 19 * fontScale,
            FontWeight = FontWeight.SemiBold,
            Foreground = glyphForeground
        };
        AutomationProperties.SetAutomationId(iconGlyph, iconAutomationId);
        if (!string.IsNullOrWhiteSpace(iconAutomationHelpText))
            AutomationProperties.SetHelpText(iconGlyph, iconAutomationHelpText);
        var iconBadge = new Border
        {
            Width = 38,
            Height = 38,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = badge,
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = iconGlyph
        };
        body.Children.Add(iconBadge);

        var messageText = new TextBlock
        {
            Text = dialogMessage,
            Foreground = MessageBrush,
            FontSize = MessageFontSize * fontScale,
            LineHeight = 21 * fontScale,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(messageText, messageAutomationId);
        var messageScroll = new ScrollViewer
        {
            MaxHeight = 350,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = messageText
        };
        Grid.SetColumn(messageScroll, 1);
        body.Children.Add(messageScroll);
        Grid.SetRow(body, 1);
        grid.Children.Add(body);

        var footer = new Border
        {
            Background = FooterBrush,
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new Grid { Margin = new Thickness(17, 9) }
        };
        var footerGrid = (Grid)footer.Child;
        footerGrid.Children.Add(new TextBlock
        {
            Text = "ESC  返回   ·   Ctrl+C  复制",
            Foreground = HintBrush,
            FontSize = 9 * fontScale,
            VerticalAlignment = VerticalAlignment.Center
        });
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 5
        };
        foreach (var button in buttons)
            buttonPanel.Children.Add(CreateButton(button, fontScale));
        footerGrid.Children.Add(buttonPanel);
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);
        return root;
    }

    private Border BuildHeader(string caption, double fontScale)
    {
        var header = new Border
        {
            Background = HeaderBrush,
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,38"),
                Margin = new Thickness(15, 0, 7, 0)
            }
        };
        header.PointerPressed += (_, eventArgs) =>
        {
            if (eventArgs.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
                BeginMoveDrag(eventArgs);
        };
        var headerGrid = (Grid)header.Child;
        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.Children.Add(new TextBlock
        {
            Text = "LUMA",
            Foreground = BrandBrush,
            FontSize = 9 * fontScale,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        title.Children.Add(new TextBlock
        {
            Text = caption,
            Foreground = CaptionBrush,
            FontSize = 14 * fontScale,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        headerGrid.Children.Add(title);

        var close = new Button
        {
            Content = "×",
            Width = 38,
            MinWidth = 38,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brush("#D3D7D9"),
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 19 * fontScale,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAutomationId(close, "Dialog.Close");
        AutomationProperties.SetName(close, "关闭");
        close.Click += (_, _) => Close(closeResult);
        Grid.SetColumn(close, 1);
        headerGrid.Children.Add(close);
        return header;
    }

    private Button CreateButton(
        WindowsWpf110DialogButton<TResult> option,
        double fontScale)
    {
        var button = new Button
        {
            Content = option.Label,
            MinWidth = 86,
            Height = 34,
            Padding = new Thickness(9, 6),
            Foreground = option.IsDestructive
                ? Brushes.White
                : option.IsDefault ? Brushes.White : ToolForeground,
            Background = option.IsDestructive
                ? Brush("#B23A3A")
                : option.IsDefault ? PrimaryBrush : ToolBrush,
            BorderBrush = option.IsDestructive
                ? Brush("#C94A4A")
                : option.IsDefault ? PrimaryBorderBrush : ToolBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            FontSize = 12 * fontScale,
            FontWeight = option.IsDefault ? FontWeight.SemiBold : FontWeight.Normal,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = option.IsDefault,
            IsCancel = option.IsCancel
        };
        AutomationProperties.SetAutomationId(button, option.AutomationId);
        AutomationProperties.SetName(
            button,
            option.AutomationName ?? option.Label);
        AutomationProperties.SetHelpText(
            button,
            AvaloniaLocalizationCatalog.Format("视觉标签={0}; 默认={1}; 取消={2}", option.Label, option.IsDefault, option.IsCancel));
        button.Click += (_, _) => Close(option.Result);
        return button;
    }

    private async void HandlePreviewKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            Close(closeResult);
            return;
        }
        if (eventArgs.Key != Key.C ||
            !eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        eventArgs.Handled = true;
        IClipboard? clipboard = Clipboard;
        if (clipboard == null) return;
        try { await clipboard.SetTextAsync(message); }
        catch
        {
            // Clipboard contention must not block the modal result path.
        }
    }

    internal static WindowsWpf110DialogPalette ResolveTonePalette(
        WindowsWpf110DialogTone tone) => tone switch
        {
            WindowsWpf110DialogTone.Warning =>
                new(Color.Parse("#D39A4B"), Color.Parse("#2C251B"), Color.Parse("#FFC56F")),
            WindowsWpf110DialogTone.Error =>
                new(Color.Parse("#C96268"), Color.Parse("#2B1E21"), Color.Parse("#FF969B")),
            WindowsWpf110DialogTone.Question =>
                new(Color.Parse("#6F9DB1"), Color.Parse("#1E282E"), Color.Parse("#A9D4E5")),
            _ => new(Color.Parse("#6E969A"), Color.Parse("#20292B"), Color.Parse("#9FC3C6"))
        };

    private static (string Glyph, WindowsWpf110DialogPalette Palette)
        ToneVisuals(WindowsWpf110DialogTone tone) => tone switch
        {
            WindowsWpf110DialogTone.Warning =>
                ("!", ResolveTonePalette(tone)),
            WindowsWpf110DialogTone.Error =>
                ("×", ResolveTonePalette(tone)),
            WindowsWpf110DialogTone.Question =>
                ("?", ResolveTonePalette(tone)),
            _ => ("i", ResolveTonePalette(tone))
        };

    private static IBrush Brush(string value) =>
        new SolidColorBrush(Color.Parse(value));
}
