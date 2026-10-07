using LumaChart.Application.Collaboration;
using LumaChart.Online;

namespace LumaChart.Avalonia.Services;

/// <summary>Binds the shared coordinator to one authenticated Avalonia room.</summary>
public sealed class AvaloniaRoomChartSyncTransport(
    OnlineAccountSession session,
    Guid roomId) : IRoomChartSyncTransport
{
    private readonly OnlineAccountSession session = session ??
        throw new ArgumentNullException(nameof(session));

    public Task<OnlineChartDelta> GetDeltaAsync(
        long afterRevision,
        CancellationToken cancellationToken) =>
        session.GetRoomChartDeltaAsync(roomId, afterRevision, cancellationToken);

    public Task<OnlineChartDelta> ApplyOperationsDeltaAsync(
        OnlineChartOperationBatch batch,
        CancellationToken cancellationToken) =>
        session.ApplyRoomChartOperationsDeltaAsync(roomId, batch, cancellationToken);

    public Task<OnlineChartSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        session.GetRoomChartAsync(roomId, cancellationToken);

    public Task<OnlineChartRollbackResult> RollbackAsync(
        Guid snapshotId,
        Guid operationId,
        CancellationToken cancellationToken) =>
        session.RollbackRoomChartSnapshotAsync(
            roomId,
            snapshotId,
            operationId,
            cancellationToken);

    public Task ListenForRevisionsAsync(
        Func<long, Task> revisionAvailable,
        CancellationToken cancellationToken) =>
        session.ListenForRoomChartEventsAsync(
            roomId,
            revisionAvailable,
            cancellationToken);
}
