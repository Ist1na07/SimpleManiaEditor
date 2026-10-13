using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private CheckBox? automaticUiScaleBox;
    private ComboBox? librarySourcePicker;
    private Button? removeLibraryRootButton;
    private string librarySourceChoiceSignature = "";

    private string monitorUiScaleSignature = "";
    private void RefreshMonitorUiScale()
    {
        if (!initialized || !displayScaleSettings.AutomaticUiScale) return;
        var screen = ResolveDisplayScaleScreen();
        if (screen == null) return;
        var signature = screen.Bounds + "/" + screen.Scaling;
        if (signature == monitorUiScaleSignature) return;
        monitorUiScaleSignature = signature;
        displayScaleController.Apply(screen);
        displayScaleSettings = displayScaleController.CurrentSettings;
        displayScaleSettingsDirty = true;
        RefreshDisplayScaleSettingsUi();
        ScheduleDisplayScaleSettingsSave();
    }

    private Control BuildAutomaticUiScaleOption()
    {
        automaticUiScaleBox = new CheckBox { Content = "根据屏幕自动调整 UI 大小", IsChecked = displayScaleSettings.AutomaticUiScale };
        AutomationProperties.SetAutomationId(automaticUiScaleBox, "Settings.AutomaticUiScale");
        automaticUiScaleBox.IsCheckedChanged += (_, _) =>
        {
            if (suppressDisplayScaleControls) return;
            displayScaleSettings = displayScaleController.SetAutomaticUiScale(automaticUiScaleBox.IsChecked == true, ResolveDisplayScaleScreen());
            displayScaleSettingsDirty = true;
            RefreshDisplayScaleSettingsUi();
            ScheduleDisplayScaleSettingsSave();
        };
        return automaticUiScaleBox;
    }

    private Button BuildAddLibraryRootButton()
    {
        var button = PrimaryButton("添加只读曲库…", async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择只读谱面库（可选 osu! Songs）", AllowMultiple = false });
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            try { await viewModel.AddLibraryRootAsync(path); }
            catch (Exception exception) { viewModel.SetStatus("添加曲库失败：" + exception.Message); }
        });
        ApplyWpfToolButtonStyle(button);
        button.Padding = new Thickness(15, 8);
        button.Margin = new Thickness(0, 0, 8, 4);
        AutomationProperties.SetAutomationId(button, "Library.AddReadOnlyRoot");
        return button;
    }

    private Control BuildLibrarySourcePicker()
    {
        librarySourcePicker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
        AutomationProperties.SetAutomationId(librarySourcePicker, "Library.Source");
        ToolTip.SetTip(librarySourcePicker, "筛选创作目录或只读曲库；不会改变保存位置");
        librarySourcePicker.SelectionChanged += (_, _) =>
        {
            if (librarySourcePicker.SelectedItem is not LibrarySourceChoice choice) return;
            viewModel.SetLibrarySourceFilter(choice.Path);
            if (removeLibraryRootButton != null) removeLibraryRootButton.IsVisible = choice.Linked;
            ToolTip.SetTip(librarySourcePicker, choice.Path ?? "显示创作目录和所有已收录的谱面");
        };
        removeLibraryRootButton = CommandButton("移除", async (_, _) =>
        {
            if (librarySourcePicker.SelectedItem is LibrarySourceChoice { Linked: true, Path: { } path })
                await viewModel.RemoveLibraryRootAsync(path);
        });
        removeLibraryRootButton.IsVisible = false;
        ToolTip.SetTip(removeLibraryRootButton, "仅移除曲库链接，保留所有原文件");
        AutomationProperties.SetAutomationId(removeLibraryRootButton, "Library.RemoveReadOnlyRoot");
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 5, Margin = new Thickness(0, 0, 0, 12) };
        root.Children.Add(librarySourcePicker);
        Grid.SetColumn(removeLibraryRootButton, 1); root.Children.Add(removeLibraryRootButton);
        RefreshLibrarySourceChoices();
        return root;
    }

    private void RefreshLibrarySourceChoices()
    {
        if (librarySourcePicker == null) return;
        var signature = viewModel.CreationRoot + "\n" + string.Join('\n', viewModel.LinkedLibraryRoots);
        if (signature == librarySourceChoiceSignature) return;
        librarySourceChoiceSignature = signature;
        var choices = new List<LibrarySourceChoice>
        {
            new("全部谱面", null, false), new("创作目录", viewModel.CreationRoot, false)
        };
        choices.AddRange(viewModel.LinkedLibraryRoots.Select(path => new LibrarySourceChoice("只读 · " + Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), path, true)));
        librarySourcePicker.ItemsSource = choices;
        librarySourcePicker.SelectedItem = choices.FirstOrDefault(choice => choice.Path == viewModel.LibrarySourceFilter) ?? choices[0];
    }

    private sealed record LibrarySourceChoice(string Label, string? Path, bool Linked)
    {
        public override string ToString() => Label;
    }
}
