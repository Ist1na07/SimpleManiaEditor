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
        var tab = viewModel.ActiveTab;
        var surface = pasteHoverSurface?.Session is { IsReadOnly: false, CanEditNotes: true } ? pasteHoverSurface : activeSecondaryTrack?.Surface ?? timeline;
        var session = surface.Session;
        if (tab == null || session == null || playtestInProgress) return;
        var serial = session.ChangeSerial;
        try
        {
            await clipboardWriteTask;
            var text = Clipboard == null ? null : await Clipboard.TryGetTextAsync();
            if (playtestInProgress || viewModel.ActiveTab != tab || !ReferenceEquals(session, surface.Session) || serial != session.ChangeSerial) return;
            if (!string.IsNullOrWhiteSpace(text) && (text != lastNoteClipboardText || viewModel.EditorClipboard == null))
            {
                StartPendingNotePaste(NoteTextCodeService.Parse(session.Document, text), surface);
                return;
            }
            if (viewModel.EditorClipboard is { NoteCount: > 0 } clipboard)
            { StartPendingNotePaste(clipboard.SnapshotNotes().ToArray(), surface); return; }
            PasteInternalSelection();
        }
        catch (Exception ex) { viewModel.SetStatus($"粘贴失败：{ex.Message}"); }
    }

    private Control BuildNoteCodeImport()
    {
        AutomationProperties.SetAutomationId(noteCodeBox, "Inspector.Note.Code");
        var import = new Button { Content = "应用", Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch };
        ApplyWpfPrimaryButtonStyle(import);
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
        return new StackPanel { Spacing = 6, Margin = new Thickness(0, 14, 0, 0), Children = {
            new TextBlock { Text = "音符文本代码", FontSize = 14 }, noteCodeBox, import,
            new TextBlock { Text = "应用：按代码原始时间添加到当前轨道。Ctrl+V：移动虚影后单击放置，Esc / 右键取消。代码不包含音频文件。", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontSize = 12 } } };
    }
}
