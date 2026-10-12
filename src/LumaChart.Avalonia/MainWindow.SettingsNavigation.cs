using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private Control? settingsAppearanceContent;

    private Control BuildCategorizedSettingsPanel()
    {
        var editor = BuildEditorViewSettings();
        var sections = new (string Name, string Keywords, Control Content)[]
        {
            ("编辑", "edit snap mode 网格 吸附 模式", new StackPanel { Spacing = 16, Children = { editor, BuildEditorModeSettings() } }),
            ("操作", "input keyboard shortcut wheel 快捷键 滚轮", BuildShortcutSettings()),
            ("外观", "appearance skin display renderer 帧率 皮肤 显示 缩放", settingsAppearanceContent!),
            ("音频", "audio music volume pitch 音量 音高", SettingsSection("音频", "", "Settings.AudioSection", BuildAudioTransport())),
            ("试玩", "playtest osu skin key 皮肤 按键 落速", BuildOfficialPlaytestSettings()),
            ("存储", "storage backup cache recovery 备份 恢复 缓存", BuildBackupCacheSettings()),
            ("通用", "general language about 语言 版本", new StackPanel { Spacing = 16, Children = { BuildLanguageSettings(), BuildApplicationInformationSettings() } })
        };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(12) };
        var search = new TextBox { PlaceholderText = "搜索设置…", FontSize = 13, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetAutomationId(search, "Settings.Search");
        root.Children.Add(search);
        var nav = new UniformGrid { Columns = 4, Rows = 2, Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(nav, 1); root.Children.Add(nav);
        var content = new StackPanel { Spacing = 16 };
        var empty = new TextBlock { Text = "没有匹配的设置", FontSize = 13, Foreground = MutedBrush, IsVisible = false };
        var scroll = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(scroll, 2); root.Children.Add(scroll);
        var buttons = new List<Button>();
        var offsets = new Dictionary<int, Vector>();
        var expandedBeforeSearch = new Dictionary<Expander, bool>();
        var active = 0;
        for (var index = 0; index < sections.Length; index++)
        {
            var section = sections[index];
            var wrapper = new StackPanel { Spacing = 10, Children =
            {
                new TextBlock { Text = section.Name, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White },
                section.Content
            } };
            AutomationProperties.SetAutomationId(wrapper, "Settings.Category." + index);
            content.Children.Add(wrapper);
            NormalizeSettingsTypography(section.Content);
            foreach (var title in section.Content.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Text == section.Name && t.FontWeight == FontWeight.SemiBold))
                title.IsVisible = false;
            var selectedIndex = index;
            var button = CommandButton(section.Name, (_, _) =>
            {
                offsets[active] = scroll.Offset; active = selectedIndex;
                search.Text = ""; Filter();
                scroll.Offset = offsets.GetValueOrDefault(active);
            });
            button.FontSize = 13; button.Padding = new Thickness(9, 5); button.Margin = new Thickness(0, 0, 4, 4);
            AutomationProperties.SetAutomationId(button, "Settings.Navigate." + index);
            buttons.Add(button); nav.Children.Add(button);
        }
        content.Children.Add(empty);
        search.TextChanged += (_, _) => { Filter(); scroll.Offset = default; };
        search.KeyDown += (_, e) => { if (e.Key == Key.Escape) { search.Text = ""; e.Handled = true; } };
        Filter();
        return root;

        void Filter()
        {
            var terms = (search.Text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var searching = terms.Length != 0;
            var count = 0;
            for (var index = 0; index < sections.Length; index++)
            {
                var section = sections[index];
                var texts = section.Content.GetLogicalDescendants().Prepend(section.Content).Select(node => node switch
                {
                    TextBlock text => text.Text,
                    HeaderedContentControl header => header.Header?.ToString(),
                    ContentControl control => control.Content as string,
                    _ => null
                });
                var haystack = section.Name + " " + section.Keywords + " " + string.Join(' ', texts);
                var visible = searching ? terms.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase)) : index == active;
                content.Children[index].IsVisible = visible;
                if (visible) count++;
                buttons[index].Background = !searching && index == active ? AccentBrush : PanelBrush;
                foreach (var expander in section.Content.GetLogicalDescendants().OfType<Expander>().Concat(section.Content is Expander self ? [self] : []))
                {
                    if (searching && visible)
                    {
                        expandedBeforeSearch.TryAdd(expander, expander.IsExpanded); expander.IsExpanded = true;
                    }
                    else if (expandedBeforeSearch.Remove(expander, out var previous)) expander.IsExpanded = previous;
                }
            }
            empty.IsVisible = count == 0;
        }
    }

    private static void NormalizeSettingsTypography(Control root)
    {
        foreach (var control in root.GetLogicalDescendants().OfType<Control>().Prepend(root))
        {
            if (control is TextBlock text)
                text.SetCurrentValue(TextBlock.FontSizeProperty, text.FontWeight == FontWeight.SemiBold || text.FontWeight == FontWeight.Bold ? 16 : text.FontSize < 13 ? 12 : 13);
            else if (control is TemplatedControl templated) templated.SetCurrentValue(TemplatedControl.FontSizeProperty, 13d);
        }
    }
}
