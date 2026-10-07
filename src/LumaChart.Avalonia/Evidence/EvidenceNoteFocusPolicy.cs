using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Evidence;

internal static class EvidenceNoteFocusPolicy
{
    public static ChartNote? Resolve(
        IEnumerable<ChartNote> notes,
        BeatPosition? authoredBeat)
    {
        ArgumentNullException.ThrowIfNull(notes);

        var orderedNotes = notes
            .Where(note => !note.IsDraft)
            .OrderBy(note => note.Beat)
            .ThenBy(note => note.Lane)
            .ThenBy(note => note.Id)
            .ToArray();
        if (authoredBeat is { } exactBeat)
        {
            // Evidence for an unreduced subdivision must not silently match a
            // numerically equivalent spelling such as 5/6 instead of 10/12.
            return orderedNotes.FirstOrDefault(note =>
                note.Beat.Bar == exactBeat.Bar &&
                note.Beat.Numerator == exactBeat.Numerator &&
                note.Beat.Denominator == exactBeat.Denominator);
        }

        var orderedTaps = orderedNotes
            .Where(note => !note.EndBeat.HasValue)
            .ToArray();
        return orderedTaps.ElementAtOrDefault(orderedTaps.Length / 2);
    }
}
