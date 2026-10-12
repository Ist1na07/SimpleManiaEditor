using Avalonia.Threading;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private bool editorSeekCommitScheduled;
    private long editorSeekScheduleVersion;

    // One delay from the first input, rather than restarting a dispatcher timer
    // on every event. Cancelled callbacks cannot commit a later tab's seek.
    private void ScheduleEditorSeekCommit()
    {
        if (editorSeekCommitScheduled || !editorSeekPending || windowCloseTeardownStarted) return;
        editorSeekCommitScheduled = true;
        var version = ++editorSeekScheduleVersion;
        _ = PostCommitAsync();
        async Task PostCommitAsync()
        {
            await Task.Delay(12).ConfigureAwait(false);
            if (Volatile.Read(ref windowCloseTeardownStarted)) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (version != editorSeekScheduleVersion) return;
                editorSeekCommitScheduled = false;
                if (!editorSeekPending || editorSeekCommitRunning || windowCloseTeardownStarted) return;
                editorSeekCommitTask = CommitPendingEditorSeekAsync();
            }, DispatcherPriority.Normal);
        }
    }

    private void CancelScheduledEditorSeekCommit()
    { editorSeekCommitScheduled = false; editorSeekScheduleVersion++; }
}
