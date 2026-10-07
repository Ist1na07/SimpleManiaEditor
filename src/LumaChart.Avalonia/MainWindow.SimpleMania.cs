using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer recoveryCheckpointTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    private bool recoveryCheckpointInProgress;
    private string checkpointRevision = "";

    private void InitializeSimpleMania()
    {
        if (initializeFactoryFloatingLayout && shortcutSettings.Profile == EditorInteractionProfile.OsuMania)
            editorViewSettings = editorViewSettings.WithQuickPalette(0, 0, collapsed: true)
                .WithQuickValuesPalette(0, 0, collapsed: true);
        timeline.InteractionProfile = shortcutSettings.Profile;
        recoveryCheckpointTimer.Tick += async (_, _) => await CheckpointRecoveryAsync();
        recoveryCheckpointTimer.Start();
        Closed += (_, _) => recoveryCheckpointTimer.Stop();
    }

    private async Task CheckpointRecoveryAsync()
    {
        if (!initialized || windowClosePreflightInProgress || recoveryCheckpointInProgress) return;
        var revision = string.Join(';', viewModel.Tabs.Select(t => $"{t.Id}:{t.Session.ChangeSerial}"));
        if (revision == checkpointRevision || !viewModel.Tabs.Any(t => t.Session.IsDirty)) return;
        recoveryCheckpointInProgress = true;
        try
        {
            if (await viewModel.PersistAsync()) checkpointRevision = revision;
        }
        catch (Exception exception)
        {
            viewModel.SetStatus($"恢复副本保存失败：{exception.Message}");
        }
        finally { recoveryCheckpointInProgress = false; }
    }

    private Control BuildInteractionProfileSelector()
    {
        var picker = new ComboBox
        {
            ItemsSource = new[] { "osu!mania（默认）", "Luma legacy" },
            SelectedIndex = shortcutSettings.Profile == EditorInteractionProfile.OsuMania ? 0 : 1,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch
        };
        picker.SelectionChanged += (_, _) =>
        {
            var profile = picker.SelectedIndex == 1 ? EditorInteractionProfile.LumaLegacy : EditorInteractionProfile.OsuMania;
            if (profile == shortcutSettings.Profile) return;
            CancelShortcutCapture();
            shortcutSettings.SelectProfile(profile);
            timeline.InteractionProfile = profile;
            RefreshShortcutButtons();
            RefreshShortcutLabels();
            shortcutSettingsSaveTimer.Stop();
            shortcutSettingsSaveTimer.Start();
            viewModel.SetStatus(profile == EditorInteractionProfile.OsuMania
                ? "osu!mania：1 选择 / 2 单键 / 3 长键，Ctrl 多选，Alt+滚轮缩放"
                : "已切换到 Luma legacy 操作方案");
        };
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "操作方案（切换时重置快捷键）", Foreground = Brushes.White },
                picker,
                new TextBlock
                {
                    Text = "osu!mania：1/2/3 工具 · Ctrl 多选 · 左拖框选 · 右键删键 · Alt+滚轮缩放",
                    Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, FontSize = 11
                }
            }
        };
    }
}
