using Avalonia.Controls;
using LumaChart.Avalonia.Controls;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private readonly EventTimeRuler eventTimeRuler = new();
    private Control BuildEventTimeRuler()
    {
        eventTimeRuler.BookmarkDeleteRequested += RemoveNavigationMarker;
        eventTimeRuler.EditEventRequested += async id =>
        {
            if (playtestInProgress || viewModel.ActiveTab == null) return;
            if (osuMinimalMode && !viewModel.ActiveTab.Session.Document.Timing.Any(t => t.Id == id && t.Bpm.HasValue)) return;
            timeline.SetSelectedTiming(id);
            editorInspectorTabs.SelectedItem = timingInspectorTab;
            await EditNativeTimingAsync(id);
        };
        eventTimeRuler.SeekRequested += beat =>
        {
            if (playtestInProgress) return;
            timeline.NavigateToBeat(beat);
            timeline.Focus();
        };
        return eventTimeRuler;
    }
    private void RefreshEventTimeRuler()
    {
        var tab = viewModel.ActiveTab;
        eventTimeRuler.SetDocument(tab?.Session, tab == null ? null : GetAudioTimingMap(tab),
            audioTransport.PlaybackSnapshot?.Duration.TotalMilliseconds ?? 0, tab?.NavigationMarkers ?? []);
        eventTimeRuler.SetCurrentBeat(timeline.CurrentBeat);
    }
}
