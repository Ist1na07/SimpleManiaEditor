using Avalonia.Threading;
using LumaChart.Application.Collaboration;

namespace LumaChart.Avalonia.Services;

/// <summary>
/// Marshals the platform-neutral room document contract onto Avalonia's UI
/// dispatcher. Completion means the corresponding editor mutation has completed.
/// </summary>
public sealed class AvaloniaRoomChartSyncDocumentHost : IRoomChartSyncDocumentHost
{
    private readonly Func<RoomChartLocalSnapshot> captureLocalSnapshot;
    private readonly Action<RoomChartApplyPlan> applyPlan;
    private readonly Action<RoomChartSyncViewState> publishViewState;
    private readonly Func<long>? currentChangeSerial;
    private readonly Action<RoomChartRejectedLocalChanges>? preserveRejectedLocalChanges;

    /// <param name="currentChangeSerial">
    /// Returns the editor change serial without copying the document. When omitted,
    /// the serial check before applying a plan falls back to a full capture.
    /// </param>
    /// <param name="preserveRejectedLocalChanges">
    /// Stores the local document a deterministic server rejection is about to
    /// discard (for example as a recoverable draft). Runs on the UI thread.
    /// </param>
    public AvaloniaRoomChartSyncDocumentHost(
        Func<RoomChartLocalSnapshot> captureLocalSnapshot,
        Action<RoomChartApplyPlan> applyPlan,
        Action<RoomChartSyncViewState> publishViewState,
        Func<long>? currentChangeSerial = null,
        Action<RoomChartRejectedLocalChanges>? preserveRejectedLocalChanges = null)
    {
        this.captureLocalSnapshot = captureLocalSnapshot ??
                                    throw new ArgumentNullException(nameof(captureLocalSnapshot));
        this.applyPlan = applyPlan ?? throw new ArgumentNullException(nameof(applyPlan));
        this.publishViewState = publishViewState ??
                                throw new ArgumentNullException(nameof(publishViewState));
        this.currentChangeSerial = currentChangeSerial;
        this.preserveRejectedLocalChanges = preserveRejectedLocalChanges;
    }

    public ValueTask<RoomChartLocalSnapshot> CaptureLocalSnapshotAsync(
        CancellationToken cancellationToken) =>
        InvokeAsync(captureLocalSnapshot, cancellationToken);

    public ValueTask ApplyPlanAsync(
        RoomChartApplyPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return InvokeAsync(
            () =>
            {
                // The plan was merged off the UI thread from an earlier capture. The
                // check and the apply run in one dispatcher job, so no edit can land
                // between them; a moved serial means the merge must be redone rather
                // than letting the whole-document replace erase that edit.
                if (plan.ApplyDocument && !plan.ClearHistory &&
                    plan.LocalChangeSerial is { } expected)
                {
                    var current = currentChangeSerial?.Invoke() ?? captureLocalSnapshot().ChangeSerial;
                    if (current != expected)
                        throw new RoomChartStaleLocalSnapshotException(expected, current);
                }
                applyPlan(plan);
            },
            cancellationToken);
    }

    public ValueTask PublishViewStateAsync(
        RoomChartSyncViewState viewState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        return InvokeAsync(() => publishViewState(viewState), cancellationToken);
    }

    public ValueTask PreserveRejectedLocalChangesAsync(
        RoomChartRejectedLocalChanges rejected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rejected);
        var preserve = preserveRejectedLocalChanges;
        return preserve == null
            ? ValueTask.CompletedTask
            : InvokeAsync(() => preserve(rejected), cancellationToken);
    }

    private static async ValueTask<TResult> InvokeAsync<TResult>(
        Func<TResult> callback,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            cancellationToken.ThrowIfCancellationRequested();
            return callback();
        }

        return await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return callback();
            },
            DispatcherPriority.Send,
            cancellationToken);
    }

    private static async ValueTask InvokeAsync(
        Action callback,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            cancellationToken.ThrowIfCancellationRequested();
            callback();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                callback();
            },
            DispatcherPriority.Send,
            cancellationToken);
    }
}
