namespace LumaChart.Avalonia.Specs;

internal static class WindowsFormatGuiHarnessSpecs
{
    public static WindowsFormatGuiHarnessSpecResult[] Run()
    {
        var projectRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var mainWindowPath = Path.Combine(
            projectRoot,
            "LumaChart.Avalonia",
            "MainWindow.cs");
        var capturePath = Path.Combine(
            projectRoot,
            "tools",
            "Capture-WindowsAvaloniaGui.ps1");
        var importCapturePath = Path.Combine(
            projectRoot,
            "tools",
            "Capture-WindowsAvaloniaFormatImportGui.ps1");
        var mainWindow = File.ReadAllText(mainWindowPath);
        var capture = File.ReadAllText(capturePath);
        var importCapture = File.ReadAllText(importCapturePath);

        return
        [
            Check(
                mainWindow.Contains(
                    "--evidence-osu-import-dialog",
                    StringComparison.Ordinal) &&
                mainWindow.Contains(
                    "!evidenceShowOsuImportDialog",
                    StringComparison.Ordinal) &&
                importCapture.Contains(
                    "$start.ArgumentList.Add(\"--evidence-osu-import-dialog\")",
                    StringComparison.Ordinal) &&
                importCapture.Contains(
                    "\"OsuImportMode.Calibrate\"",
                    StringComparison.Ordinal) &&
                importCapture.Contains(
                    "\"OsuImportMode.Preserve\"",
                    StringComparison.Ordinal),
                "format-gui-direct-osu-evidence-exercises-real-import-choice",
                $"main={mainWindowPath}; capture={importCapturePath}; both osu import choices stay on the real owner-modal path"),
            Check(
                capture.Contains(
                    "[ValidateSet(\"SingleChart\", \"SingleRatePackage\", \"RatePack\")]",
                    StringComparison.Ordinal) &&
                capture.Contains(
                    "-not $chartExportModesActualExportEnabled",
                    StringComparison.Ordinal) &&
                capture.Contains(
                    "\"RatePack\" { 90 }",
                    StringComparison.Ordinal) &&
                capture.Contains(
                    "\"SingleRatePackage\" { 60 }",
                    StringComparison.Ordinal) &&
                capture.Contains(
                    "$ChartExportModesExportMinimumRate",
                    StringComparison.Ordinal) &&
                capture.Contains(
                    "$ChartExportModesExportMaximumRate",
                    StringComparison.Ordinal) &&
                capture.Contains(
                    "$ChartExportModesExportRateStep",
                    StringComparison.Ordinal),
                "format-gui-all-export-modes-have-physical-and-bounded-completion-paths",
                $"capture={capturePath}; SingleChart/SingleRatePackage/RatePack; actual export may omit premature interaction snapshot; bounded 18/60/90-second atomic completion waits"),
            Check(
                importCapture.Contains(
                    "if (-not (\"LumaChartFormatImportNative\" -as [type]))",
                    StringComparison.Ordinal) &&
                importCapture.Contains(
                    "$seenRuntimeIds",
                    StringComparison.Ordinal) &&
                importCapture.Contains(
                    "$seenRuntimeIds.Add($runtimeId)",
                    StringComparison.Ordinal) &&
                importCapture.Contains(
                    "walking multiple top-level",
                    StringComparison.Ordinal),
                "format-gui-installed-app-multi-run-reuses-native-helper-and-deduplicates-uia-peers",
                $"capture={importCapturePath}; one PowerShell lifecycle may fresh-open MC/MCZ/osu/OSZ sequentially; repeated Add-Type is guarded and duplicate descendants from multiple installed-app top-level UIA roots count once"),
            Check(
                importCapture.Contains(
                    "Set-ForegroundVerified $currentMainHandle",
                    StringComparison.Ordinal) &&
                importCapture.Contains(
                    "Capture-Window $currentMainHandle $finalScreenshotPath",
                    StringComparison.Ordinal) &&
                importCapture.IndexOf(
                    "Set-ForegroundVerified $currentMainHandle",
                    StringComparison.Ordinal) <
                importCapture.IndexOf(
                    "Capture-Window $currentMainHandle $finalScreenshotPath",
                    StringComparison.Ordinal),
                "format-gui-final-editor-screenshot-foregrounds-target-window",
                $"capture={importCapturePath}; screen-copy evidence explicitly foregrounds the installed Avalonia editor immediately before final capture so unrelated foreground applications cannot replace the evidence image")
        ];
    }

    private static WindowsFormatGuiHarnessSpecResult Check(
        bool passed,
        string name,
        string detail) =>
        new(name, passed, detail);
}

internal sealed record WindowsFormatGuiHarnessSpecResult(
    string Name,
    bool Passed,
    string Detail);
