using Avalonia.Controls;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.ViewModels;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private readonly EventTimeRuler eventTimeRuler = new();
    private (Guid? TabId, string? Source, double Duration) eventRulerAudio;
    private (Guid? TabId, string? WorkingPath, string? SourcePath, string? AudioReference) eventRulerAudioSourceKey;
    private string? eventRulerAudioSource;
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
            GetEventRulerAudio(refreshSource: true).Duration, tab?.NavigationMarkers ?? []);
        eventTimeRuler.SetCurrentBeat(timeline.CurrentBeat);
    }

    private (Guid? TabId, string? Source, double Duration) GetEventRulerAudio(bool refreshSource = false)
    {
        var tab = viewModel.ActiveTab;
        var sourceKey = (tab?.Id, tab?.WorkingPath, tab?.SourcePath, tab?.Session.Document.Metadata.AudioFile);
        if (refreshSource || eventRulerAudioSourceKey != sourceKey)
        {
            eventRulerAudioSourceKey = sourceKey;
            eventRulerAudioSource = WindowsAudioTransportViewModel.ResolveDocumentAudioPath(tab);
        }
        var source = eventRulerAudioSource;
        var audio = audioTransport.PlaybackSnapshot;
        // A tab/audio change starts an asynchronous decoder switch. Until it
        // completes, the previous snapshot must not give this song its duration.
        var duration = tab != null && audioTransport.ActiveTabId == tab.Id && audio?.IsLoaded == true &&
            source != null && string.Equals(source, audio.SourcePath, StringComparison.OrdinalIgnoreCase)
            ? audio.Duration.TotalMilliseconds : 0;
        return (tab?.Id, source, duration);
    }

    private void RefreshEventTimeRulerAudioDuration()
    {
        var current = GetEventRulerAudio();
        if (eventRulerAudio == current) return;
        eventRulerAudio = current;
        RefreshEventTimeRuler();
    }
}
