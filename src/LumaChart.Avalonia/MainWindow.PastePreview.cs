using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LumaChart.Avalonia.Controls;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private ChartNote[]? pendingNotePaste;
    private Guid? pendingNotePasteTab;
    private int pendingNotePasteWidth;
    private TimelineSurface? pendingPasteSurface;
    private TimelineSurface? pasteHoverSurface;

    private void StartPendingNotePaste(ChartNote[] notes, TimelineSurface surface)
    {
        if (surface.Session?.IsReadOnly != false || !surface.Session.CanEditNotes) { viewModel.SetStatus("请在主轨或草稿轨放置。"); return; }
        var width = notes.Length == 0 ? 0 : notes.Max(n => n.Lane) - notes.Min(n => n.Lane) + 1;
        if (width == 0 || width > surface.Session.Document.KeyCount)
        { viewModel.SetStatus("排列超出目标轨道键数，未进入粘贴状态。"); return; }
        CancelNotePaste(); pendingNotePasteWidth = width; pendingNotePaste = notes; pendingNotePasteTab = viewModel.ActiveTab?.Id;
        pendingPasteSurface = surface; surface.BeginPendingPaste(notes);
        viewModel.SetStatus("移动音符虚影并单击放置；可移至草稿轨，Esc / 右键取消。");
    }

    private void CancelNotePaste()
    { pendingPasteSurface?.ClearPendingPastePreview(); pendingPasteSurface = null; pendingNotePaste = null; pendingNotePasteTab = null; pendingNotePasteWidth = 0; }

    private void RegisterPasteSurface(TimelineSurface surface)
    {
        if (ReferenceEquals(surface, timeline))
            AddHandler(InputElement.PointerPressedEvent, (_, e) =>
            {
                if (pendingNotePaste != null && e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
                { CancelNotePaste(); e.Handled = true; }
            }, RoutingStrategies.Tunnel);
        surface.PointerExited += (_, _) => { if (pasteHoverSurface == surface) pasteHoverSurface = null; };
        surface.PendingPasteFinished += (_, _) => { CancelNotePaste(); UpdateSelectionUi(); };
        surface.AddHandler(InputElement.PointerMovedEvent, (_, e) => Follow(e), RoutingStrategies.Tunnel);
        surface.AddHandler(InputElement.PointerPressedEvent, (_, e) => Follow(e), RoutingStrategies.Tunnel);
        surface.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        { if (pendingNotePaste != null && e.Key == Key.Escape) { CancelNotePaste(); e.Handled = true; } }, RoutingStrategies.Tunnel);
        void Follow(PointerEventArgs e)
        {
            if (surface.Session?.IsReadOnly == false && surface.Session.CanEditNotes) pasteHoverSurface = surface;
            if (pendingNotePaste == null) return;
            if (playtestInProgress || pendingNotePasteTab != viewModel.ActiveTab?.Id) { CancelNotePaste(); return; }
            if (surface.Session?.IsReadOnly != false || !surface.Session.CanEditNotes ||
                pendingNotePasteWidth > surface.Session.Document.KeyCount) return;
            if (pendingPasteSurface != surface)
            { pendingPasteSurface?.ClearPendingPastePreview(); pendingPasteSurface = surface; surface.BeginPendingPaste(pendingNotePaste); }
            surface.MovePendingPaste(e.GetPosition(surface));
        }
    }
}
