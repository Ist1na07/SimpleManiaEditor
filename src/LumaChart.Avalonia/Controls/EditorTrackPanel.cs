using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;

namespace LumaChart.Avalonia.Controls;

/// <summary>Shared board container for every track; content decides note/analysis/audio rendering.</summary>
public sealed class EditorTrackPanel : Border
{
    public string TrackKey { get; }
    public TextBox TitleBox { get; }
    public StackPanel HeaderActions { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    public Grid BodyGrid { get; } = new() { RowDefinitions = new RowDefinitions("Auto,*") };
    public Control? Surface { get; private set; }
    public event EventHandler<double>? MoveRequested;
    private double dragX;
    private double dragStartX;
    private bool dragging;

    public EditorTrackPanel(string key, string title, double width, bool compact = false)
    {
        TrackKey = key;
        Width = width;
        Background = new SolidColorBrush(Color.Parse("#15191C"));
        BorderBrush = new SolidColorBrush(Color.Parse("#343B3F"));
        BorderThickness = new Thickness(1);
        var grip = new Border { Width = 16, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeAll) };
        ToolTip.SetTip(grip, "拖动调整轨道顺序");
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            dragging = true; dragX = 0; dragStartX = e.GetPosition(TopLevel.GetTopLevel(grip)).X;
            e.Pointer.Capture(grip); e.Handled = true;
        };
        grip.PointerMoved += (_, e) => { if (dragging) dragX = e.GetPosition(TopLevel.GetTopLevel(grip)).X - dragStartX; };
        grip.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragX = e.GetPosition(TopLevel.GetTopLevel(grip)).X - dragStartX;
            dragging = false; e.Pointer.Capture(null); e.Handled = true;
            if (Math.Abs(dragX) >= 24) MoveRequested?.Invoke(this, dragX);
        };
        global::Avalonia.Automation.AutomationProperties.SetAutomationId(grip, "Editor.Board.Grip." + key);
        TitleBox = new TextBox { Text = title, FontSize = compact ? 10 : 12, Padding = new Thickness(1, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
        var header = new Grid { Height = 32, ColumnDefinitions = new ColumnDefinitions("16,*,Auto"), Margin = new Thickness(3, 0) };
        header.Children.Add(grip); Grid.SetColumn(TitleBox, 1); header.Children.Add(TitleBox);
        header.Children.Add(new TextBlock { Text = "⋮", Foreground = Brushes.Gray, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(HeaderActions, 2); header.Children.Add(HeaderActions);
        var root = new Grid { RowDefinitions = new RowDefinitions("32,*") };
        root.Children.Add(header); Grid.SetRow(BodyGrid, 1); root.Children.Add(BodyGrid);
        Child = root;
        global::Avalonia.Automation.AutomationProperties.SetAutomationId(this, "Editor.Board.Track." + key);
    }

    public void SetContent(Control content, Control? tools = null)
    {
        BodyGrid.Children.Clear();
        Surface = content;
        if (tools != null) BodyGrid.Children.Add(tools);
        Grid.SetRow(content, 1); BodyGrid.Children.Add(content);
    }
}
