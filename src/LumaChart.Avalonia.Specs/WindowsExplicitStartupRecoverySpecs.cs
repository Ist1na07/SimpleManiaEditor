using System.Security.Cryptography;
using LumaChart.Application.Workspaces;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsExplicitStartupRecoverySpecs
{
    public static async Task<IReadOnlyList<WindowsExplicitStartupRecoverySpecResult>> RunAsync(
        string root,
        string fixturePath,
        string osuFixturePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixturePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(osuFixturePath);
        var libraryRoot = Path.Combine(root, "library");
        var editingRoot = Path.Combine(root, "editing");
        Directory.CreateDirectory(libraryRoot);
        var priorSource = Path.Combine(libraryRoot, "prior.mc");
        var explicitSource = Path.Combine(libraryRoot, "explicit.mc");
        File.Copy(fixturePath, priorSource, overwrite: false);
        File.Copy(fixturePath, explicitSource, overwrite: false);

        var first = new DesktopDocumentWorkspace(
            libraryRoot,
            editingRoot,
            "AvaloniaSpecs");
        await first.InitializeAsync().ConfigureAwait(false);
        var priorTab = await first.OpenAsync(priorSource).ConfigureAwait(false);
        priorTab.Session.Mutate(document =>
            document.Metadata.Version = "Suspended explicit-startup working copy");
        await first.PersistAsync().ConfigureAwait(false);
        var priorCacheDirectory = priorTab.CacheDirectory;
        var priorWorkingHash = ComputeSha256(priorTab.WorkingPath);

        var explicitLaunch = new DesktopDocumentWorkspace(
            libraryRoot,
            editingRoot,
            "AvaloniaSpecs");
        await explicitLaunch.InitializeForExplicitStartupAsync().ConfigureAwait(false);
        var previousTabsWereSuppressed = explicitLaunch.Tabs.Count == 0 &&
                                         explicitLaunch.ActiveTab == null;
        var explicitTab = await explicitLaunch.OpenAsync(explicitSource).ConfigureAwait(false);
        await explicitLaunch.PersistAsync().ConfigureAwait(false);
        var priorWorkingPreserved =
            File.Exists(priorTab.WorkingPath) &&
            string.Equals(
                priorWorkingHash,
                ComputeSha256(priorTab.WorkingPath),
                StringComparison.OrdinalIgnoreCase);

        var ordinaryRestart = new DesktopDocumentWorkspace(
            libraryRoot,
            editingRoot,
            "AvaloniaSpecs");
        await ordinaryRestart.InitializeAsync().ConfigureAwait(false);
        var ordinarySources = ordinaryRestart.Tabs
            .Select(tab => Path.GetFullPath(tab.SourcePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordinaryRestoredBoth =
            ordinaryRestart.Tabs.Count == 2 &&
            ordinarySources.SetEquals(
            [
                Path.GetFullPath(priorSource),
                Path.GetFullPath(explicitSource)
            ]) &&
            string.Equals(
                ordinaryRestart.ActiveTab?.SourcePath,
                explicitSource,
                StringComparison.OrdinalIgnoreCase);

        var matchingLaunch = new DesktopDocumentWorkspace(
            libraryRoot,
            editingRoot,
            "AvaloniaSpecs");
        await matchingLaunch.InitializeForExplicitStartupAsync().ConfigureAwait(false);
        var matchingTab = await matchingLaunch.OpenAsync(priorSource).ConfigureAwait(false);
        var matchingReusedCache =
            matchingLaunch.Tabs.Count == 1 &&
            string.Equals(
                matchingTab.CacheDirectory,
                priorCacheDirectory,
                StringComparison.OrdinalIgnoreCase);
        await matchingLaunch.PersistAsync().ConfigureAwait(false);

        var finalRestart = new DesktopDocumentWorkspace(
            libraryRoot,
            editingRoot,
            "AvaloniaSpecs");
        await finalRestart.InitializeAsync().ConfigureAwait(false);
        var finalSources = finalRestart.Tabs
            .Select(tab => Path.GetFullPath(tab.SourcePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var noDuplicateOrLoss =
            finalRestart.Tabs.Count == 2 &&
            finalSources.SetEquals(
            [
                Path.GetFullPath(priorSource),
                Path.GetFullPath(explicitSource)
            ]) &&
            string.Equals(
                finalRestart.ActiveTab?.SourcePath,
                priorSource,
                StringComparison.OrdinalIgnoreCase);

        var osuRoot = Path.Combine(root, "osu-cache-choice");
        var osuLibraryRoot = Path.Combine(osuRoot, "library");
        var osuEditingRoot = Path.Combine(osuRoot, "editing");
        Directory.CreateDirectory(osuLibraryRoot);
        var osuSource = Path.Combine(osuLibraryRoot, "cached.osu");
        File.Copy(osuFixturePath, osuSource, overwrite: false);
        var osuFirst = new DesktopDocumentWorkspace(
            osuLibraryRoot,
            osuEditingRoot,
            "AvaloniaSpecs");
        await osuFirst.InitializeAsync().ConfigureAwait(false);
        _ = await osuFirst.OpenAsync(
                osuSource,
                LumaChart.Core.IO.OsuNoteTimingImportMode.CalibrateCommonSubdivisions)
            .ConfigureAwait(false);
        await osuFirst.PersistAsync().ConfigureAwait(false);
        var osuExplicit = new DesktopDocumentWorkspace(
            osuLibraryRoot,
            osuEditingRoot,
            "AvaloniaSpecs");
        await osuExplicit.InitializeForExplicitStartupAsync().ConfigureAwait(false);
        var synchronizedMode =
            await osuExplicit.TryGetSynchronizedSuspendedOsuTimingModeAsync(osuSource)
                .ConfigureAwait(false);
        await File.AppendAllTextAsync(osuSource, Environment.NewLine)
            .ConfigureAwait(false);
        var changedSourceMode =
            await osuExplicit.TryGetSynchronizedSuspendedOsuTimingModeAsync(osuSource)
                .ConfigureAwait(false);
        var osuChoiceParity =
            synchronizedMode ==
            LumaChart.Core.IO.OsuNoteTimingImportMode.CalibrateCommonSubdivisions &&
            changedSourceMode == null;

        return
        [
            Result(
                "windows-explicit-startup-suppresses-previous-tabs-without-touching-cache",
                previousTabsWereSuppressed &&
                explicitLaunch.Tabs.Count == 1 &&
                explicitLaunch.ActiveTab?.Id == explicitTab.Id &&
                priorWorkingPreserved,
                $"suppressed={previousTabsWereSuppressed}; visible={explicitLaunch.Tabs.Count}; " +
                $"priorHashPreserved={priorWorkingPreserved}"),
            Result(
                "windows-ordinary-restart-restores-suspended-and-explicit-startup-tabs",
                ordinaryRestoredBoth,
                $"tabs={ordinaryRestart.Tabs.Count}; " +
                $"active={Path.GetFileName(ordinaryRestart.ActiveTab?.SourcePath)}; " +
                $"sources={string.Join(',', ordinarySources.Select(Path.GetFileName))}"),
            Result(
                "windows-explicit-startup-reuses-matching-cache-without-duplicate-or-loss",
                matchingReusedCache && noDuplicateOrLoss,
                $"reused={matchingReusedCache}; finalTabs={finalRestart.Tabs.Count}; " +
                $"active={Path.GetFileName(finalRestart.ActiveTab?.SourcePath)}"),
            Result(
                "windows-explicit-osu-startup-skips-choice-only-for-synchronized-cache",
                osuChoiceParity,
                $"synchronized={synchronizedMode}; changed={changedSourceMode?.ToString() ?? "<prompt>"}")
        ];
    }

    private static string ComputeSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static WindowsExplicitStartupRecoverySpecResult Result(
        string name,
        bool passed,
        string detail) =>
        new(name, passed, detail);
}

internal sealed record WindowsExplicitStartupRecoverySpecResult(
    string Name,
    bool Passed,
    string Detail);
