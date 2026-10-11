using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using LumaChart.Application.Editing;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private string? lastNoteClipboardText;
    private Task clipboardWriteTask = Task.CompletedTask;
    private readonly TextBox noteCodeBox = new() { AcceptsReturn = true, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
        MinHeight = 95, MaxHeight = 210, PlaceholderText = "粘贴 .osu HitObjects 音符行…" };

    private void WriteNoteClipboard(ChartDocument document, IEnumerable<ChartNote> notes)
    {
        var text = NoteTextCodeService.Export(document, notes);
        if (text.Length == 0) return;
        clipboardWriteTask = WriteAsync();
        async Task WriteAsync()
        {
            try
            {
                if (Clipboard == null) throw new InvalidOperationException("当前无法访问系统剪贴板。");
                await Clipboard.SetTextAsync(text); lastNoteClipboardText = text;
            }
            catch (Exception ex) { viewModel.SetStatus($"音符已复制到编辑器，系统剪贴板写入失败：{ex.Message}"); }
        }
    }

    private async Task PasteNoteClipboardAsync()
    {
        var tab = viewModel.ActiveTab; var session = activeSecondaryTrack?.Track.Session ?? tab?.Session;
        var surface = activeSecondaryTrack?.Surface ?? timeline;
        if (tab == null || session == null || playtestInProgress) return;
        var beat = surface.CurrentBeat; var serial = session.ChangeSerial;
        try
        {
            await clipboardWriteTask;
            var text = Clipboard == null ? null : await Clipboard.TryGetTextAsync();
            if (playtestInProgress || viewModel.ActiveTab != tab || !ReferenceEquals(session, activeSecondaryTrack?.Track.Session ?? viewModel.ActiveTab?.Session) || serial != session.ChangeSerial) return;
            if (!string.IsNullOrWhiteSpace(text) && (text != lastNoteClipboardText || viewModel.EditorClipboard?.EffectCount == 0))
            {
                var map = new TimingMap(session.Document.Timing);
                var count = NoteTextCodeService.Import(session, text, map.BeatToMilliseconds(BeatPosition.FromDouble(beat, 1_000_000)));
                viewModel.SetStatus($"已在播放位置粘贴 {count} 个音符"); UpdateSelectionUi(); return;
            }
            PasteInternalSelection();
        }
        catch (Exception ex) { viewModel.SetStatus($"粘贴失败：{ex.Message}"); }
    }

    private Control BuildNoteCodeImport()
    {
        AutomationProperties.SetAutomationId(noteCodeBox, "Inspector.Note.Code");
        var import = new Button { Content = "按代码原始时间导入", Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetAutomationId(import, "Inspector.Note.ImportCode");
        import.Click += (_, _) =>
        {
            if (playtestInProgress) return;
            try
            {
                var session = activeSecondaryTrack?.Track.Session ?? viewModel.ActiveTab?.Session;
                if (session == null) return;
                var count = NoteTextCodeService.Import(session, noteCodeBox.Text ?? "");
                viewModel.SetStatus($"已按原始毫秒时间导入 {count} 个音符，可一次撤销"); UpdateSelectionUi();
            }
            catch (Exception ex) { viewModel.SetStatus($"音符代码未导入：{ex.Message}"); }
        };
        return new Expander { Header = "音符文本代码", IsExpanded = true, Margin = new Thickness(0, 14, 0, 0),
            Content = new StackPanel { Children = { noteCodeBox, import, new TextBlock { Text = "Ctrl+C 复制选中音符代码；Ctrl+V 放到播放位置。此处导入保留代码中的原始时间；代码不包含音频文件。", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontSize = 11 } } } };
    }
}
