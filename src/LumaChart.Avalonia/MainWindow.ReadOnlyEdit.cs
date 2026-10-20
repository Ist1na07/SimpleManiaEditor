using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia;

/// <summary>
/// Read-only document top bar action (0.1.38). A library read-only tab shows a
/// single clear "编辑" command in the fixed top toolbar; it runs the existing
/// clone-to-workspace flow (MainWindow.Patterns.CloneActiveReadOnlyAsync), then
/// reports the copied folder. The left tool rail no longer carries a second
/// clone button, because the rail is reserved for note tools and transforms.
/// </summary>
public sealed partial class MainWindow
{
    private Button? readOnlyEditTopBarButton;
    private TextBlock? readOnlyEditTopBarLabel;
    private bool readOnlyEditInFlight;

    private Button BuildReadOnlyEditTopBarButton()
    {
        readOnlyEditTopBarLabel = new TextBlock
        {
            Text = "编辑",
            Foreground = Brushes.White,
            FontWeight = FontWeight.DemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7.0, 0.0, 0.0, 0.0)
        };
        var icon = new global::Avalonia.Controls.Shapes.Path
        {
            Width = 16.0,
            Height = 16.0,
            Data = Geometry.Parse("M2.5,13.5 L3.4,10.4 L10.6,3.2 L13.3,5.9 L6.1,13.1 Z M9.4,4.4 L12.1,7.1"),
            Stroke = Brushes.White,
            StrokeThickness = 1.4,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { (Control)icon, (Control)readOnlyEditTopBarLabel }
            },
            Padding = new Thickness(9.0, 7.0),
            Margin = new Thickness(1.0, 0.0, 4.0, 0.0),
            CornerRadius = new CornerRadius(3.0),
            Background = new SolidColorBrush(Color.Parse("#38676C")),
            BorderBrush = new SolidColorBrush(Color.Parse("#4A777B")),
            BorderThickness = new Thickness(1.0),
            Foreground = Brushes.White,
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += async (object? _, RoutedEventArgs _) => await EditActiveReadOnlyChartAsync();
        AutomationProperties.SetAutomationId(button, "Editor.TopBar.EditReadOnly");
        AutomationProperties.SetName(button, "编辑只读谱面（复制到工作区）");
        AutomationProperties.SetHelpText(button, "把整套谱面资源复制到工作区并以可编辑标签打开；只读曲库中的原文件逐字节保持不变");
        ToolTip.SetTip(button, "复制这份只读谱面到工作区并打开编辑；原文件保持不变");
        readOnlyEditTopBarButton = button;
        return button;
    }

    /// <summary>Visibility/enablement refresh. Called from UpdateEditorAvailability
    /// (through RefreshFixedTools) on every tab switch and state change.</summary>
    private void RefreshReadOnlyEditTopBar()
    {
        if (readOnlyEditTopBarButton is null) return;
        var readOnly = viewModel.ActiveTab is { IsLibraryReadOnly: true };
        readOnlyEditTopBarButton.IsVisible = readOnly;
        readOnlyEditTopBarButton.IsEnabled = readOnly && !readOnlyEditInFlight;
        var text = readOnlyEditInFlight ? "正在复制…" : "编辑";
        if (readOnlyEditTopBarLabel is not null && !string.Equals(readOnlyEditTopBarLabel.Text, text, StringComparison.Ordinal))
        {
            readOnlyEditTopBarLabel.Text = text;
        }

        AutomationProperties.SetHelpText(readOnlyEditTopBarButton, readOnlyEditInFlight
            ? "正在把整套谱面复制到工作区并校验资源，请稍候…"
            : "把整套谱面资源复制到工作区并以可编辑标签打开；只读曲库中的原文件逐字节保持不变");
    }

    /// <summary>Clone the active read-only chart into the workspace and open the
    /// copy for editing. Re-entry is rejected while a clone is running so a
    /// double click can never publish two copies. The failure path intentionally
    /// leaves the read-only tab (and its source file) untouched.</summary>
    private async Task EditActiveReadOnlyChartAsync()
    {
        if (readOnlyEditInFlight) return;
        if (viewModel.ActiveTab is not { IsLibraryReadOnly: true }) return;
        var tabsBefore = viewModel.Tabs.Select(tab => tab.Id).ToHashSet();
        readOnlyEditInFlight = true;
        RefreshReadOnlyEditTopBar();
        try
        {
            viewModel.SetStatus("正在复制只读谱面到工作区并校验资源，请稍候…（只读原文件不会被修改）");
            await CloneActiveReadOnlyAsync();
            var opened = viewModel.Tabs.FirstOrDefault(tab => !tabsBefore.Contains(tab.Id) && !tab.IsLibraryReadOnly);
            if (opened is not null)
            {
                var copyFile = opened.SourcePath;
                var copyFolder = System.IO.Path.GetDirectoryName(copyFile) ?? copyFile;
                viewModel.SetStatus(AvaloniaLocalizationCatalog.Format(
                    "已复制到工作区并打开编辑：{0} · 只读原文件未改动", copyFolder));
                ShowEditorToast("已复制到工作区：" + copyFolder);
            }
            else if (viewModel.Status.StartsWith("正在复制", StringComparison.Ordinal))
            {
                // CloneLibraryChartAsync reports its own explicit failure text; only
                // replace the progress text when nothing else was reported.
                viewModel.SetStatus("未复制：没有找到这张只读谱面的曲库索引；请回到曲库用「克隆到工作目录并编辑」。");
            }
        }
        catch (Exception exception)
        {
            viewModel.SetStatus("复制只读谱面失败，原文件保留：" + exception.Message);
        }
        finally
        {
            readOnlyEditInFlight = false;
            RefreshReadOnlyEditTopBar();
        }
    }
}
