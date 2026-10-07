using System.Collections.Immutable;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Avalonia.Audio;

internal readonly record struct WindowsKeySoundPlaybackOptions(
    bool Enabled,
    KeySoundPreset Preset,
    string? CustomFilePath,
    bool PlayLongNoteEndSound,
    double GlobalOffsetMilliseconds,
    double ChartOffsetMilliseconds)
{
    public double EffectiveOffsetMilliseconds =>
        GlobalOffsetMilliseconds + ChartOffsetMilliseconds;
}

internal readonly record struct WindowsKeySoundPlaybackEvent(
    Guid NoteId,
    bool IsLongNoteEnd,
    double Milliseconds,
    KeySoundPreset Preset,
    string? CustomFilePath,
    float Gain);

internal readonly record struct WindowsKeySoundPlaybackTrigger(
    Guid NoteId,
    bool IsLongNoteEnd,
    double EventMilliseconds,
    KeySoundPreset Preset,
    string? CustomFilePath,
    float Gain,
    double DelayMilliseconds,
    double WallClockDelayMilliseconds,
    int ChordVoiceCount);

/// <summary>
/// Pure editor-transport Key 音 plan and cursor. It snapshots only immutable
/// event data; decoding, device output and UI dispatch remain backend concerns.
/// One transport owner calls <see cref="ResetCursor"/> after a discontinuity and
/// <see cref="TakeDue"/> while its playback clock advances.
/// </summary>
internal sealed class WindowsKeySoundPlaybackCoordinator
{
    internal const double SchedulingLookAheadMilliseconds = 180;
    internal const double ChordToleranceMilliseconds = .5;
    internal const double MaximumLatePlaybackMilliseconds = 100;
    internal const double ResetToleranceMilliseconds = .75;

    private readonly ImmutableArray<WindowsKeySoundPlaybackEvent> events;
    private int nextEventIndex;

    public WindowsKeySoundPlaybackCoordinator(
        ChartDocument document,
        string sourceChartPath,
        IReadOnlySet<int> hiddenGroups,
        WindowsKeySoundPlaybackOptions options)
        : this(
            document,
            sourceChartPath,
            hiddenGroups,
            options,
            File.Exists,
            CancellationToken.None)
    {
    }

    internal WindowsKeySoundPlaybackCoordinator(
        ChartDocument document,
        string sourceChartPath,
        IReadOnlySet<int> hiddenGroups,
        WindowsKeySoundPlaybackOptions options,
        Func<string, bool> fileExists,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceChartPath);
        ArgumentNullException.ThrowIfNull(hiddenGroups);
        ArgumentNullException.ThrowIfNull(fileExists);
        ValidateOffset(options.GlobalOffsetMilliseconds, nameof(options.GlobalOffsetMilliseconds));
        ValidateOffset(options.ChartOffsetMilliseconds, nameof(options.ChartOffsetMilliseconds));
        cancellationToken.ThrowIfCancellationRequested();

        Options = options;
        if (!options.Enabled)
        {
            events = [];
            PreloadPaths = [];
            return;
        }

        var timingMap = new TimingMap(document.Timing);
        cancellationToken.ThrowIfCancellationRequested();
        var sourceDirectory = GetSourceDirectory(sourceChartPath);
        var globalCustomFile = options.Preset == KeySoundPreset.Custom
            ? ResolveExistingReference(
                options.CustomFilePath,
                sourceDirectory,
                fileExists)
            : null;
        var globalPreset = options.Preset == KeySoundPreset.Custom && globalCustomFile == null
            ? KeySoundPreset.Snare
            : options.Preset;
        var planned = new List<(WindowsKeySoundPlaybackEvent Event, int Sequence)>();
        var noteIndex = 0;
        foreach (var note in document.Notes)
        {
            if ((noteIndex++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (note.IsDraft || hiddenGroups.Contains(note.Group)) continue;

            var noteSound = ResolveExistingReference(note.Sound, sourceDirectory, fileExists);
            var preset = noteSound == null ? globalPreset : KeySoundPreset.Custom;
            var customFilePath = noteSound ?? globalCustomFile;
            // The global editor click is a timing aid and ignores Note.Volume.
            // Only a real per-Note sample applies that Note's authored volume.
            var gain = noteSound == null ? 1f : Math.Clamp(note.Volume / 100f, 0, 1);
            planned.Add((
                new WindowsKeySoundPlaybackEvent(
                    note.Id,
                    IsLongNoteEnd: false,
                    timingMap.BeatToMilliseconds(note.Beat),
                    preset,
                    customFilePath,
                    gain),
                planned.Count));
            if (options.PlayLongNoteEndSound && note.EndBeat.HasValue)
            {
                planned.Add((
                    new WindowsKeySoundPlaybackEvent(
                        note.Id,
                        IsLongNoteEnd: true,
                        timingMap.BeatToMilliseconds(note.EndBeat.Value),
                        preset,
                        customFilePath,
                        gain),
                    planned.Count));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var sorted = SortEvents(planned, cancellationToken);
        var eventBuilder = ImmutableArray.CreateBuilder<WindowsKeySoundPlaybackEvent>(planned.Count);
        foreach (var item in sorted)
        {
            if ((eventBuilder.Count & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            eventBuilder.Add(item.Event);
        }
        events = eventBuilder.MoveToImmutable();

        var preloadBuilder = ImmutableArray.CreateBuilder<string>();
        var seenPreloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < events.Length; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var path = events[index].CustomFilePath;
            if (!string.IsNullOrWhiteSpace(path) && seenPreloads.Add(path))
                preloadBuilder.Add(path);
        }
        // The builder may skip duplicate custom paths, so Count is commonly
        // smaller than Capacity; MoveToImmutable would throw in that case.
        PreloadPaths = preloadBuilder.ToImmutable();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static (WindowsKeySoundPlaybackEvent Event, int Sequence)[] SortEvents(
        List<(WindowsKeySoundPlaybackEvent Event, int Sequence)> planned,
        CancellationToken cancellationToken)
    {
        var source = planned.ToArray();
        if (source.Length < 2) return source;
        var target = new (WindowsKeySoundPlaybackEvent Event, int Sequence)[source.Length];
        for (var width = 1; width < source.Length; width = width > source.Length / 2
                 ? source.Length
                 : width * 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var start = 0; start < source.Length; start += width * 2)
            {
                var middle = Math.Min(start + width, source.Length);
                var end = Math.Min(start + width * 2, source.Length);
                var left = start;
                var right = middle;
                for (var destination = start; destination < end; destination++)
                {
                    if ((destination & 255) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    if (left < middle && (right >= end || Compare(
                            source[left],
                            source[right]) <= 0))
                        target[destination] = source[left++];
                    else
                        target[destination] = source[right++];
                }
            }
            (source, target) = (target, source);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return source;

        static int Compare(
            (WindowsKeySoundPlaybackEvent Event, int Sequence) left,
            (WindowsKeySoundPlaybackEvent Event, int Sequence) right)
        {
            var order = left.Event.Milliseconds.CompareTo(right.Event.Milliseconds);
            return order != 0 ? order : left.Sequence.CompareTo(right.Sequence);
        }
    }

    public WindowsKeySoundPlaybackOptions Options { get; }
    public ImmutableArray<WindowsKeySoundPlaybackEvent> Events => events;
    public ImmutableArray<string> PreloadPaths { get; }
    internal int NextEventIndex => nextEventIndex;

    /// <summary>
    /// Positions the event cursor with the WPF 1.1.0 tolerance formula. The
    /// returned index is useful to deterministic transport and Specs callers.
    /// </summary>
    public int ResetCursor(double positionMilliseconds, double playbackRate)
    {
        ValidateClock(positionMilliseconds, playbackRate);
        var chartThreshold = ResetThresholdMilliseconds(
            positionMilliseconds,
            playbackRate,
            Options.EffectiveOffsetMilliseconds);
        var low = 0;
        var high = events.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (events[middle].Milliseconds < chartThreshold) low = middle + 1;
            else high = middle;
        }

        nextEventIndex = low;
        return low;
    }

    /// <summary>
    /// Consumes every event inside the scheduling horizon and returns only
    /// audible backend triggers. Late and zero-gain events still advance the
    /// cursor exactly as in the WPF editor.
    /// </summary>
    public ImmutableArray<WindowsKeySoundPlaybackTrigger> TakeDue(
        double positionMilliseconds,
        double playbackRate,
        double outputLatencyMilliseconds)
    {
        ValidateClock(positionMilliseconds, playbackRate);
        if (!double.IsFinite(outputLatencyMilliseconds) || outputLatencyMilliseconds < 0)
            throw new ArgumentOutOfRangeException(
                nameof(outputLatencyMilliseconds),
                "Output latency must be finite and non-negative.");
        if (nextEventIndex >= events.Length) return [];

        var effectiveOffsetMilliseconds = Options.EffectiveOffsetMilliseconds;
        ImmutableArray<WindowsKeySoundPlaybackTrigger>.Builder? triggers = null;
        while (nextEventIndex < events.Length &&
               WallClockDelay(
                   events[nextEventIndex].Milliseconds,
                   positionMilliseconds,
                   playbackRate,
                   effectiveOffsetMilliseconds) <=
               outputLatencyMilliseconds + SchedulingLookAheadMilliseconds)
        {
            var chordStart = nextEventIndex;
            var chordMilliseconds = events[chordStart].Milliseconds;
            do
            {
                nextEventIndex++;
            }
            while (nextEventIndex < events.Length &&
                   Math.Abs(events[nextEventIndex].Milliseconds - chordMilliseconds) <=
                   ChordToleranceMilliseconds);

            var wallClockDelayMilliseconds = WallClockDelay(
                chordMilliseconds,
                positionMilliseconds,
                playbackRate,
                effectiveOffsetMilliseconds);
            if (wallClockDelayMilliseconds < -MaximumLatePlaybackMilliseconds) continue;

            var audibleVoices = 0;
            for (var index = chordStart; index < nextEventIndex; index++)
            {
                if (events[index].Gain > .001f) audibleVoices++;
            }
            if (audibleVoices == 0) continue;

            var chordVoiceGain = ChordVoiceGain(audibleVoices);
            var sampleDelayMilliseconds = Math.Max(
                0,
                wallClockDelayMilliseconds - outputLatencyMilliseconds);
            for (var index = chordStart; index < nextEventIndex; index++)
            {
                var item = events[index];
                if (item.Gain <= .001f) continue;
                triggers ??= ImmutableArray.CreateBuilder<WindowsKeySoundPlaybackTrigger>();
                triggers.Add(new WindowsKeySoundPlaybackTrigger(
                    item.NoteId,
                    item.IsLongNoteEnd,
                    item.Milliseconds,
                    item.Preset,
                    item.CustomFilePath,
                    item.Gain * chordVoiceGain,
                    sampleDelayMilliseconds,
                    wallClockDelayMilliseconds,
                    audibleVoices));
            }
        }

        // Builder capacity follows its growth policy, not the number of due
        // voices. MoveToImmutable requires Count == Capacity and therefore
        // throws for ordinary partial batches; ToImmutable copies the exact
        // populated range deterministically.
        return triggers?.ToImmutable() ?? [];
    }

    /// <summary>
    /// Both compensations are chart time, so neither is scaled here; only the
    /// scheduling tolerance is, because that one is real seconds of slack.
    /// </summary>
    internal static double ResetThresholdMilliseconds(
        double positionMilliseconds,
        double playbackRate,
        double effectiveOffsetMilliseconds) =>
        positionMilliseconds -
        effectiveOffsetMilliseconds -
        ResetToleranceMilliseconds * playbackRate;

    /// <summary>
    /// When a key sound has to fire, in real seconds from now.
    ///
    /// The offset is inside the division, and that is the whole fix. It used to
    /// be added afterwards, which treated it as real time: a compensation dialled
    /// in by ear at 1x then shrank in proportion to the rate — at 0.5x a 30 ms
    /// setting landed 15 chart-milliseconds from the note — so key sounds drifted
    /// against the music at every rate except 1x, by an amount proportional to
    /// the setting.
    ///
    /// Both compensations are chart time because both are calibrated the same
    /// way: the user listens against the music and dials until it sits right.
    /// What they matched at one rate has to stay matched at every other, which
    /// is what dividing by the rate along with the note distance does.
    /// </summary>
    internal static double WallClockDelay(
        double eventMilliseconds,
        double positionMilliseconds,
        double playbackRate,
        double effectiveOffsetMilliseconds) =>
        (eventMilliseconds - positionMilliseconds + effectiveOffsetMilliseconds) /
        playbackRate;

    internal static float ChordVoiceGain(int voiceCount) =>
        WindowsKeySoundService.ChordVoiceGain(voiceCount);

    private static string GetSourceDirectory(string sourceChartPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(sourceChartPath);
            return Path.GetDirectoryName(fullPath) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? ResolveExistingReference(
        string? reference,
        string sourceDirectory,
        Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        try
        {
            var candidate = Path.IsPathRooted(reference)
                ? Path.GetFullPath(reference)
                : string.IsNullOrWhiteSpace(sourceDirectory)
                    ? Path.GetFullPath(reference)
                    : Path.GetFullPath(Path.Combine(sourceDirectory, reference));
            return fileExists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ValidateOffset(double value, string parameterName)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName, "Key 音 offset must be finite.");
    }

    private static void ValidateClock(double positionMilliseconds, double playbackRate)
    {
        if (!double.IsFinite(positionMilliseconds))
            throw new ArgumentOutOfRangeException(
                nameof(positionMilliseconds),
                "Playback position must be finite.");
        if (!double.IsFinite(playbackRate) || playbackRate <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(playbackRate),
                "Playback rate must be finite and positive.");
    }
}
