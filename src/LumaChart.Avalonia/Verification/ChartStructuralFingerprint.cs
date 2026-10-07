using System.Security.Cryptography;
using System.Text;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Verification;

/// <summary>
/// Canonical semantic fingerprint used by save/reopen gates. Runtime GUIDs,
/// source paths, and online creator attribution are intentionally absent from
/// the compact MC compatibility representation.
/// </summary>
public static class ChartStructuralFingerprint
{
    public static string Compute(ChartDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        // Native osu verification compares the exported payload, including retained
        // events/metadata. Recovery envelopes change on reload and are not chart data.
        if (OsuSourceEnvelope.TrySerialize(document, null, null, out var nativeText))
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nativeText.Replace("\r\n", "\n", StringComparison.Ordinal))));
        var snapshot = document.Clone();
        snapshot.FilePath = null;
        foreach (var note in snapshot.Notes) note.OnlineCreatorUserId = 0;
        foreach (var timing in snapshot.Timing) timing.OnlineCreatorUserId = 0;
        var canonical = MalodyMcSerializer.Serialize(snapshot);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static ChartStructureSummary Summarize(ChartDocument document) =>
        new(
            document.KeyCount,
            document.Notes.Count(note => !OsuSourceEnvelope.HasSource(document) || !note.IsDraft),
            document.Notes.Count(note => note.IsHold && (!OsuSourceEnvelope.HasSource(document) || !note.IsDraft)),
            document.Timing.Count,
            Compute(document));
}

public sealed record ChartStructureSummary(
    int KeyCount,
    int NoteCount,
    int LongNoteCount,
    int TimingCount,
    string Sha256);
