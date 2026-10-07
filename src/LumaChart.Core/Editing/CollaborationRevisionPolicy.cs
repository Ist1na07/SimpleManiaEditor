namespace LumaChart.Core.Editing;

/// <summary>Guards a client-side collaboration baseline against server generation resets.</summary>
public static class CollaborationRevisionPolicy
{
    private static readonly TimeSpan DefaultWriteBudgetDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaximumWriteBudgetDelay = TimeSpan.FromHours(1);

    public static bool RequiresFullSnapshot(long localRevision, long serverRevision) =>
        localRevision >= 0 && serverRevision >= 0 && serverRevision < localRevision;

    /// <summary>
    /// A snapshot rollback keeps revisions monotonic, but starts a new authoritative
    /// editing baseline. Only a baseline newer than the document already installed by
    /// this client should clear local pending/history state; this also makes a delayed
    /// delta harmless after the room owner has applied the rollback response directly.
    /// </summary>
    public static bool RequiresAuthoritativeReset(
        long localRevision,
        long? authoritativeBaselineRevision) =>
        localRevision >= 0 &&
        authoritativeBaselineRevision is >= 0 &&
        authoritativeBaselineRevision.Value > localRevision;

    public static bool IsAuthoritativeBaselineMarkerValid(
        long? authoritativeBaselineRevision,
        IEnumerable<long> deliveredOperationRevisions) =>
        authoritativeBaselineRevision == null ||
        authoritativeBaselineRevision is >= 0 &&
        deliveredOperationRevisions.Contains(authoritativeBaselineRevision.Value);

    public static bool IsWriteBudgetThrottle(string? errorCode) =>
        string.Equals(errorCode, "chart_write_budget", StringComparison.OrdinalIgnoreCase);

    public static bool IsPermanentRoomWriteBlock(string? errorCode) =>
        string.Equals(errorCode, "chart_room_capacity", StringComparison.OrdinalIgnoreCase);

    public static string? GetPermissionDeniedCategory(string? errorCode) =>
        errorCode?.ToLowerInvariant() switch
        {
            "note_permission_required" => "Note",
            "timing_permission_required" => "Effect",
            "metadata_permission_required" => "谱面信息",
            // viewer_read_only is the current server code; retain the older spelling
            // so mixed client/server builds still show an explicit read-only notice.
            "viewer_read_only" or "observer_read_only" => "谱面",
            _ => null
        };

    public static TimeSpan NormalizeWriteBudgetRetryAfter(TimeSpan? retryAfter)
    {
        if (retryAfter is not { } delay || delay <= TimeSpan.Zero)
            return DefaultWriteBudgetDelay;
        return delay > MaximumWriteBudgetDelay ? MaximumWriteBudgetDelay : delay;
    }
}
