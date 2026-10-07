using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LumaChart.Avalonia.Dialogs;

internal sealed record InAppSaveAsResult(string FileName, string Extension);

/// <summary>
/// Save As naming surface for a platform whose save picker cannot return a
/// writable file-system path. It offers the same Malody/osu! destination choice
/// as the Windows save picker, but the file lands in an app-owned folder the
/// editor can keep the tab bound to.
/// </summary>
internal sealed class InAppSaveAsDialog : PlatformWindow
{
    internal const string DialogAutomationId = "SaveAs.InAppDialog";
    internal const string NameBoxAutomationId = "SaveAs.InAppDialog.Name";
    internal const string ConfirmAutomationId = "SaveAs.InAppDialog.Confirm";
    internal const string CancelAutomationId = "SaveAs.InAppDialog.Cancel";

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

    private readonly TextBox nameBox;
    private readonly ComboBox formatBox;

    public InAppSaveAsDialog(
        string suggestedName,
        string defaultExtension,
        string destinationDescription,
        double uiScale,
        double fontScale)
    {
        ArgumentNullException.ThrowIfNull(suggestedName);
        ArgumentNullException.ThrowIfNull(destinationDescription);

        uiScale = Math.Clamp(double.IsFinite(uiScale) ? uiScale : 1, .75, 2);
        fontScale = Math.Clamp(double.IsFinite(fontScale) ? fontScale : 1, .75, 2);

        Title = "另存谱面";
        Width = 520 * uiScale;
        MaxWidth = 680 * uiScale;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowDecorations = WindowDecorations.None;
        Background = PanelBrush;
        AutomationProperties.SetAutomationId(this, DialogAutomationId);

        nameBox = new TextBox
        {
            Text = suggestedName,
            FontSize = 14 * fontScale,
            MinHeight = 40,
            Padding = new Thickness(10, 8),
            Background = Brush("#101315"),
            Foreground = MessageBrush,
            BorderBrush = ToolBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };
        AutomationProperties.SetAutomationId(nameBox, NameBoxAutomationId);
        AutomationProperties.SetName(nameBox, "谱面文件名");

        formatBox = new ComboBox
        {
            ItemsSource = new[] { "Malody 谱面 (.mc)", "osu!mania 谱面 (.osu)" },
            SelectedIndex = defaultExtension.Equals(".osu", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0,
            FontSize = 13 * fontScale,
            MinHeight = 40,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(formatBox, "SaveAs.InAppDialog.Format");
        AutomationProperties.SetName(formatBox, "保存格式");

        Content = BuildContent(destinationDescription, uiScale, fontScale);
        AddHandler(InputElement.KeyDownEvent, HandlePreviewKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => Dispatcher.UIThread.Post(
            () =>
            {
                nameBox.Focus();
                nameBox.SelectAll();
            },
            DispatcherPriority.Input);
    }

    /// <summary>Extension the user chose, always one the editor can save.</summary>
    public string SelectedExtension => formatBox.SelectedIndex == 1 ? ".osu" : ".mc";

    private Control BuildContent(
        string destinationDescription,
        double uiScale,
        double fontScale)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("44,Auto,58") };

        var header = new Border
        {
            Background = HeaderBrush,
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9,
                Margin = new Thickness(15, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "LUMA",
                        Foreground = BrandBrush,
                        FontSize = 9 * fontScale,
                        FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = "另存谱面",
                        Foreground = CaptionBrush,
                        FontSize = 14 * fontScale,
                        FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
        grid.Children.Add(header);

        var body = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(21, 18, 21, 18)
        };
        body.Children.Add(new TextBlock
        {
            Text = destinationDescription,
            Foreground = HintBrush,
            FontSize = 11 * fontScale,
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new TextBlock
        {
            Text = "文件名",
            Foreground = MessageBrush,
            FontSize = 12 * fontScale
        });
        body.Children.Add(nameBox);
        body.Children.Add(new TextBlock
        {
            Text = "格式",
            Foreground = MessageBrush,
            FontSize = 12 * fontScale
        });
        body.Children.Add(formatBox);
        Grid.SetRow(body, 1);
        grid.Children.Add(body);

        var confirm = FooterButton("保存", ConfirmAutomationId, primary: true, fontScale);
        confirm.Click += (_, _) => Confirm();
        var cancel = FooterButton("取消", CancelAutomationId, primary: false, fontScale);
        cancel.Click += (_, _) => Close(null);

        var footer = new Border
        {
            Background = FooterBrush,
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 5,
                Margin = new Thickness(17, 9),
                Children = { cancel, confirm }
            }
        };
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);

        return new Border
        {
            Background = PanelBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            MinWidth = 360 * uiScale,
            Child = grid
        };
    }

    private Button FooterButton(
        string label,
        string automationId,
        bool primary,
        double fontScale)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 96,
            Height = 38,
            Padding = new Thickness(9, 6),
            Foreground = primary ? Brushes.White : ToolForeground,
            Background = primary ? PrimaryBrush : ToolBrush,
            BorderBrush = primary ? PrimaryBorderBrush : ToolBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            FontSize = 12 * fontScale,
            FontWeight = primary ? FontWeight.SemiBold : FontWeight.Normal,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, label);
        return button;
    }

    private void Confirm()
    {
        var name = nameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        Close(new InAppSaveAsResult(name, SelectedExtension));
    }

    private void HandlePreviewKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        switch (eventArgs.Key)
        {
            case Key.Escape:
                eventArgs.Handled = true;
                Close(null);
                return;
            // The name box owns Enter on a soft keyboard, so confirm here rather
            // than through a default Button that never receives the key.
            case Key.Enter:
                eventArgs.Handled = true;
                Confirm();
                return;
        }
    }

    private static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));
}
