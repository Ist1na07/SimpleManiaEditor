using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsAudioLibraryImportSpecs
{
    public static async Task<IReadOnlyList<WindowsAudioLibraryImportSpecResult>> RunAsync(
        string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var libraryRoot = Path.Combine(root, "library");
        var editingRoot = Path.Combine(root, "editing");
        var dropRoot = Path.Combine(root, "drop");
        Directory.CreateDirectory(libraryRoot);
        Directory.CreateDirectory(dropRoot);
        Directory.CreateDirectory(Path.Combine(libraryRoot, new string('A', 80)));
        var audioPath = Path.Combine(dropRoot, "song.MP3");
        var chartPath = Path.Combine(dropRoot, "chart.mc");
        var archivePath = Path.Combine(dropRoot, "pack.osz");
        var unsupportedPath = Path.Combine(dropRoot, "readme.txt");
        File.WriteAllText(audioPath, "audio");
        File.WriteAllText(chartPath, "{}");
        File.WriteAllText(archivePath, "archive");
        File.WriteAllText(unsupportedPath, "text");

        var now = new DateTimeOffset(2026, 7, 29, 8, 9, 10, TimeSpan.Zero);
        var timestamp = now.ToUnixTimeSeconds().ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var workspace = new DesktopDocumentWorkspace(
            libraryRoot,
            editingRoot,
            "guest",
            new FixedTimeProvider(now));

        var longName = new string('A', 90);
        var longCreation = await workspace.CreateNamedAudioChartAsync(
                longName,
                "  signed-in-user  ")
            .ConfigureAwait(false);
        var chineseCreation = await workspace.CreateNamedAudioChartAsync(
                "中文曲",
                creator: null)
            .ConfigureAwait(false);
        var fallbackCreation = await workspace.CreateNamedAudioChartAsync(
                "...",
                creator: null)
            .ConfigureAwait(false);
        var timestampCreation = await workspace.CreateNamedAudioChartAsync(
                " ",
                creator: null)
            .ConfigureAwait(false);

        var longDocument = longCreation.Tab.Session.Document;
        var chineseDocument = chineseCreation.Tab.Session.Document;
        var audioOnlyDrop = WindowsFileDropPolicy.CreatePlan(
            [unsupportedPath, audioPath, audioPath]);
        var mixedDrop = WindowsFileDropPolicy.CreatePlan(
            [audioPath, chartPath, archivePath]);
        var unsupportedDrop = WindowsFileDropPolicy.CreatePlan(
            [unsupportedPath, Path.Combine(dropRoot, "missing.ogg")]);
        return
        [
            Result(
                "windows-audio-library-import-name-allocation-matches-wpf",
                longCreation.Name == new string('A', 80) + "_1" &&
                Path.GetFileName(Path.GetDirectoryName(longCreation.Tab.SourcePath)) ==
                longCreation.Name &&
                Path.GetFileName(longCreation.Tab.SourcePath) == $"{timestamp}.mc" &&
                longDocument.Metadata.Title == longCreation.Name &&
                longDocument.Metadata.Creator == "signed-in-user",
                $"nameLength={longCreation.Name.Length}; " +
                $"folder={Path.GetFileName(Path.GetDirectoryName(longCreation.Tab.SourcePath))}; " +
                $"file={Path.GetFileName(longCreation.Tab.SourcePath)}; " +
                $"creator={longDocument.Metadata.Creator}"),
            Result(
                "windows-audio-library-import-unicode-title-and-guest-match-wpf",
                chineseCreation.Name == "中文曲" &&
                chineseDocument.Metadata.Title == "Untitled" &&
                chineseDocument.Metadata.TitleOriginal == "中文曲" &&
                chineseDocument.Metadata.Creator == "guest",
                $"name={chineseCreation.Name}; title={chineseDocument.Metadata.Title}; " +
                $"original={chineseDocument.Metadata.TitleOriginal}; " +
                $"creator={chineseDocument.Metadata.Creator}"),
            Result(
                "windows-audio-library-import-empty-sanitized-name-matches-wpf",
                fallbackCreation.Name == "resource" &&
                Path.GetFileName(Path.GetDirectoryName(fallbackCreation.Tab.SourcePath)) ==
                "resource",
                $"name={fallbackCreation.Name}; " +
                $"folder={Path.GetFileName(Path.GetDirectoryName(fallbackCreation.Tab.SourcePath))}"),
            Result(
                "windows-audio-library-import-blank-name-uses-unix-timestamp",
                timestampCreation.Name == timestamp &&
                Path.GetFileName(Path.GetDirectoryName(timestampCreation.Tab.SourcePath)) ==
                timestamp &&
                Path.GetFileName(timestampCreation.Tab.SourcePath) == $"{timestamp}.mc",
                $"name={timestampCreation.Name}; " +
                $"folder={Path.GetFileName(Path.GetDirectoryName(timestampCreation.Tab.SourcePath))}; " +
                $"file={Path.GetFileName(timestampCreation.Tab.SourcePath)}"),
            Result(
                "windows-audio-only-drop-routes-first-audio-to-wpf-import-flow",
                WindowsFileDropPolicy.IsSupportedExistingFile(audioPath) &&
                !WindowsFileDropPolicy.IsSupportedExistingFile(
                    Path.Combine(dropRoot, "missing.ogg")) &&
                audioOnlyDrop.ChartAndArchivePaths.Count == 0 &&
                string.Equals(
                    audioOnlyDrop.AudioPath,
                    Path.GetFullPath(audioPath),
                    StringComparison.OrdinalIgnoreCase),
                $"charts={audioOnlyDrop.ChartAndArchivePaths.Count}; " +
                $"audio={audioOnlyDrop.AudioPath}"),
            Result(
                "windows-mixed-drop-prioritizes-archives-then-charts-over-audio",
                mixedDrop.ChartAndArchivePaths.SequenceEqual(
                    [Path.GetFullPath(archivePath), Path.GetFullPath(chartPath)],
                    StringComparer.OrdinalIgnoreCase) &&
                mixedDrop.AudioPath == null,
                $"charts={string.Join(',', mixedDrop.ChartAndArchivePaths.Select(Path.GetFileName))}; " +
                $"audio={mixedDrop.AudioPath ?? "<null>"}"),
            Result(
                "windows-unsupported-drop-is-rejected",
                !unsupportedDrop.HasSupportedFiles,
                $"charts={unsupportedDrop.ChartAndArchivePaths.Count}; " +
                $"audio={unsupportedDrop.AudioPath ?? "<null>"}")
        ];
    }

    private static WindowsAudioLibraryImportSpecResult Result(
        string name,
        bool passed,
        string detail) =>
        new(name, passed, detail);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

internal sealed record WindowsAudioLibraryImportSpecResult(
    string Name,
    bool Passed,
    string Detail);
