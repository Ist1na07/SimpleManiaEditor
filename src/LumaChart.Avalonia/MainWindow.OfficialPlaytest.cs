using Avalonia.Controls;
using Avalonia.Platform.Storage;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private readonly TextBlock officialPlaytestWarmStatus = new()
    { Text = "首次打开时后台准备试玩", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontSize = 11 };
    private Control BuildOfficialPlaytestSettings()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "试玩", Foreground = MutedBrush });
        officialPlaytestWarmStatus.Foreground = MutedBrush;
        panel.Children.Add(officialPlaytestWarmStatus);
        var settings = new Button { Content = "官方试玩设置：皮肤、按键、游戏…" };
        settings.Click += async (_, _) => await StartPlaytestAsync(0, settingsOnly: true);
        global::Avalonia.Automation.AutomationProperties.SetAutomationId(settings, "Editor.Settings.Playtest.Open");
        var cfg = new Button { Content = "导入 osu! 键位、落速与皮肤…" };
        var folder = new Button { Content = "添加皮肤目录 / Skins 目录…" };
        var archive = new Button { Content = "导入 .osk 皮肤包…" };
        cfg.Click += async (_, _) => await ImportOfficialPlaytestConfigAsync();
        folder.Click += async (_, _) => await ImportOfficialSkinAsync(false);
        archive.Click += async (_, _) => await ImportOfficialSkinAsync(true);
        foreach (var button in new[] { settings, cfg, folder, archive })
        {
            button.HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch;
            panel.Children.Add(button);
        }
        return panel;
    }

    private async Task ImportOfficialPlaytestConfigAsync()
    {
        if (playtestInProgress) { viewModel.SetStatus("请先退出试玩再导入配置。"); return; }
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            { Title = "选择 osu!.用户名.cfg", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("osu! 用户配置") { Patterns = ["osu!.*.cfg", "*.cfg"] }] });
            var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path == null) return;
            var result = await Task.Run(() => OsuPlaytestImport.Read(path, WindowsPlaytestSettingsStore.Load()));
            if (!WindowsPlaytestSettingsStore.Save(result.Settings)) throw new IOException("导入的试玩配置无法保存。");
            // An explicit import may replace host settings once. Ordinary re-entry never does.
            File.WriteAllText(Path.Combine(AvaloniaAppPaths.LocalDataRoot, "official-settings-import.txt"), Guid.NewGuid().ToString("N"));
            viewModel.SetStatus(result.Summary + "下次试玩应用；官方游戏内设置随后持续保留。");
        }
        catch (Exception exception) { viewModel.SetStatus("试玩配置导入失败：" + exception.Message); }
    }

    private async Task ImportOfficialSkinAsync(bool archive)
    {
        try
        {
            if (archive)
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                { Title = "导入 osu! 皮肤包", AllowMultiple = true,
                    FileTypeFilter = [new FilePickerFileType("osu! 皮肤") { Patterns = ["*.osk"] }] });
                var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToArray();
                if (paths.Length == 0) return;
                var root = Path.Combine(AvaloniaAppPaths.LocalDataRoot, "official-skin-imports");
                Directory.CreateDirectory(root);
                await Task.Run(() => { foreach (var path in paths) File.Copy(path!, Path.Combine(root, Guid.NewGuid().ToString("N") + ".osk")); });
                viewModel.SetStatus("皮肤包已保留副本。下次试玩由官方导入；Ctrl+O → 皮肤选择。");
            }
            else
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                { Title = "选择皮肤或 osu! Skins 目录", AllowMultiple = false });
                var path = folders.FirstOrDefault()?.TryGetLocalPath(); if (path == null) return;
                var skins = await Task.Run(() => OsuSkinLibrary.RegisterFolder(path));
                viewModel.SetStatus($"已登记 {skins.Count} 个皮肤。下次试玩由官方导入；Ctrl+O → 皮肤选择。");
            }
        }
        catch (Exception exception) { viewModel.SetStatus("皮肤导入失败：" + exception.Message); }
    }
}
