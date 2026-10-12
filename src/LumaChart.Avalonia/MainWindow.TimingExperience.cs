using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LumaChart.Application.Editing;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Dialogs;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.IO;
using LumaChart.Core.Timing;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private static string FormatTimingMilliseconds(double value)
    {
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return (rounded == 0 ? 0 : rounded).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    }

    private Button BuildOffsetHereButton()
    {
        var button = CommandButton("将此处设为 Offset", (_, _) =>
        {
            if (viewModel.ActiveTab is not { } tab) return;
            var milliseconds = Math.Round(GetAudioTimingMap(tab).BeatToMilliseconds(
                BeatPosition.FromDouble(timeline.CurrentBeat, BeatPosition.MaximumPreservedDenominator)), MidpointRounding.AwayFromZero);
            if (!OsuSourceEnvelope.HasSource(tab.Session.Document)) return;
            try
            {
                var first = TimingMap.FindSongOffsetTiming(tab.Session.Document.Timing);
                if (first == null) return;
                OsuTimingEditingService.Apply(tab.Session, first.Id,
                    new OsuTimingEdit(milliseconds, true, first.Bpm!.Value, OsuTimingPointSettings.Read(first)));
                SetProgrammaticPlaybackBeat(GetAudioTimingMap(tab).MillisecondsToBeat(milliseconds, BeatPosition.MaximumPreservedDenominator).Value);
                UpdateUi();
                viewModel.SetStatus($"已将 {milliseconds:0} ms 设为 Offset，可撤销。");
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            { viewModel.SetStatus(exception.Message); }
        });
        AutomationProperties.SetAutomationId(button, "Timing.OffsetHere");
        return button;
    }
    private string ReadTimingOffsetDraft(DesktopChartTab tab)
    {
        var text = metadataOffsetBox.Text ?? "";
        if (OsuSourceEnvelope.HasSource(tab.Session.Document) && double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
            return (-value).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return text;
    }
    private Button? toolbarPlaytestButton;
    private Button? toolbarUndoButton;
    private Button? toolbarRedoButton;
    private CheckBox? snapAllBox;
    private Control? nativeTimingActions;
    private readonly List<Control> legacyTimingControls = [];
    private Button? nativeTimingEditButton;
    private Button? nativeTimingDeleteButton;
    private Button? nativeTimingAddButton;
    private Control? timingEditorRoot;
    private TextBlock? timingLayoutCaption;

    private Control BuildSnapAllOption()
    {
        snapAllBox = new CheckBox { Content = "全部吸附（放置、拖动按当前网格）", IsChecked = EditorPresentationSettingsStore.LoadSnapAll() };
        timeline.SnapAllNotes = snapAllBox.IsChecked == true;
        AutomationProperties.SetAutomationId(snapAllBox, "Editor.SnapAll");
        ToolTip.SetTip(snapAllBox, "移动选中的按键及 LN 端点时吸附到网格；仅切换网格不会修改任何已有按键。");
        snapAllBox.IsCheckedChanged += (_, _) =>
        {
            timeline.SnapAllNotes = snapAllBox.IsChecked == true;
            EditorPresentationSettingsStore.SaveSnapAll(timeline.SnapAllNotes);
            SyncTrackViewports();
        };
        return new StackPanel { Spacing = 6, Children = { snapAllBox, BuildGridSnapActions() } };
    }

    private Control BuildNativeTimingActions()
    {
        nativeTimingAddButton = CommandButton("新增事件…", async (_, _) => await EditNativeTimingAsync(null));
        nativeTimingEditButton = CommandButton("编辑所选…", async (_, _) =>
        { if (timingList.SelectedItem is TimingListItem selected) await EditNativeTimingAsync(selected.Id); });
        nativeTimingDeleteButton = CommandButton("删除", (_, _) => DeleteSelectedTiming());
        var jump = CommandButton("定位", (_, _) => JumpToSelectedTiming());
        AutomationProperties.SetAutomationId(nativeTimingAddButton, "Timing.Native.Add");
        AutomationProperties.SetAutomationId(nativeTimingEditButton, "Timing.Native.Edit");
        AutomationProperties.SetAutomationId(nativeTimingDeleteButton, "Timing.Native.Delete");
        nativeTimingActions = new StackPanel { Spacing = 8, Children =
        {
            new TextBlock { Text = "选择事件后编辑；新增会打开独立草稿，应用后才写入谱面。", Foreground = MutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap },
            new WrapPanel { Orientation = Orientation.Horizontal, Children = { nativeTimingAddButton, nativeTimingEditButton, nativeTimingDeleteButton, jump } }
        } };
        return nativeTimingActions;
    }

    private void UpdateNativeTimingControls(EditorSession? session)
    {
        var native = session != null && OsuSourceEnvelope.HasSource(session.Document);
        if (timingEditorRoot != null) timingEditorRoot.MinHeight = native ? 0 : 420;
        timingList.MinHeight = native ? 80 : 160;
        if (timingLayoutCaption != null) timingLayoutCaption.Text = native ? (osuMinimalMode ? "ms · BPM · Kiai" : "ms · BPM / SV · 音量 · Kiai") : "Beat · Delay · BPM · Effect";
        if (nativeTimingActions != null) nativeTimingActions.IsVisible = native;
        foreach (var control in legacyTimingControls) control.IsVisible = !native;
        var editable = session != null && !session.IsReadOnly && session.CanEditTiming;
        if (nativeTimingAddButton != null) nativeTimingAddButton.IsEnabled = editable;
        if (nativeTimingEditButton != null) nativeTimingEditButton.IsEnabled = editable && timingList.SelectedItem != null;
        if (nativeTimingDeleteButton != null) nativeTimingDeleteButton.IsEnabled = editable && timingList.SelectedItems?.Count > 0;
    }

    private async Task EditNativeTimingAsync(Guid? id)
    {
        if (viewModel.ActiveTab is not { } tab || !OsuSourceEnvelope.HasSource(tab.Session.Document)) return;
        var existing = id.HasValue ? tab.Session.Document.Timing.FirstOrDefault(t => t.Id == id) : null;
        if (id.HasValue && (existing == null || osuMinimalMode && !existing.Bpm.HasValue)) return;
        var map = new TimingMap(tab.Session.Document.Timing);
        var milliseconds = existing == null ? map.BeatToMilliseconds(BeatPosition.FromDouble(timeline.CurrentBeat)) : map.BeatToMilliseconds(existing.Beat);
        var inherited = existing ?? tab.Session.Document.Timing.Where(t => map.BeatToMilliseconds(t.Beat) <= milliseconds)
            .OrderBy(t => t.Beat).ThenBy(t => t.Bpm.HasValue ? 0 : 1).LastOrDefault();
        var initial = new OsuTimingEdit(milliseconds, existing?.Bpm.HasValue ?? true, existing?.Bpm ?? existing?.EffectValue ?? map.BpmAt(BeatPosition.FromDouble(timeline.CurrentBeat)),
            inherited == null ? new OsuTimingPointSettings() : OsuTimingPointSettings.Read(inherited));
        var result = await new OsuTimingDialog(id.HasValue, initial, osuMinimalMode).ShowDialog<OsuTimingEdit?>(this);
        if (result == null) return;
        try
        {
            var applied = OsuTimingEditingService.Apply(tab.Session, id, result);
            if (viewModel.ActiveTab != tab) return;
            RefreshTimingList(applied == Guid.Empty ? id : applied, force: true);
            UpdateSelectionUi();
            viewModel.SetStatus(id.HasValue ? "Timing 修改已应用，可撤销。" : "Timing 事件已添加，可撤销。");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        { viewModel.SetStatus("Timing 未应用：" + exception.Message); }
    }
    private static TimingListItem CreateNativeTimingListItem(TimingEvent timing, TimingMap map)
    {
        var settings = OsuTimingPointSettings.Read(timing);
        var time = map.BeatToMilliseconds(timing.Beat).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms";
        var main = timing.Bpm.HasValue ? $"BPM {timing.Bpm.Value:0.######}" : $"SV {timing.EffectValue:0.######}x";
        return new TimingListItem(timing.Id, time, main, (settings.Kiai ? "Kiai · " : "") + $"音量 {settings.Volume}%");
    }
}
