namespace LumaChart.Avalonia.Specs;

internal static class WindowsAvaloniaInstallerSpecs
{
    private const string WpfAppId = "F55FC1AC-55BB-4D97-B0A4-990B983715B2";
    private const string AvaloniaPreviewAppId =
        "AE886E9E-335F-4F03-B891-75591AC6CB57";

    public static WindowsAvaloniaInstallerSpecResult[] Run()
    {
        var projectRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var installerPath = Path.Combine(
            projectRoot,
            "installer",
            "LumaChartAvaloniaPreview.iss");
        var buildScriptPath = Path.Combine(
            projectRoot,
            "tools",
            "Build-WindowsAvaloniaPreviewInstaller.ps1");
        var lifecycleScriptPath = Path.Combine(
            projectRoot,
            "tools",
            "Test-WindowsAvaloniaPreviewInstaller.ps1");
        var formalCaptureScriptPath = Path.Combine(
            projectRoot,
            "tools",
            "Capture-WindowsAvaloniaInstallerLifecycleSlice.ps1");
        var formalVerificationScriptPath = Path.Combine(
            projectRoot,
            "tools",
            "New-WindowsAvaloniaInstallerLifecycleSliceEvidence.ps1");
        var runningRepairCaptureScriptPath = Path.Combine(
            projectRoot,
            "tools",
            "Capture-WindowsAvaloniaRunningClientRepairSlice.ps1");
        var runningRepairVerificationScriptPath = Path.Combine(
            projectRoot,
            "tools",
            "New-WindowsAvaloniaRunningClientRepairSliceEvidence.ps1");
        var mainWindowPath = Path.Combine(
            projectRoot,
            "LumaChart.Avalonia",
            "MainWindow.cs");
        var script = File.ReadAllText(installerPath);
        var buildScript = File.ReadAllText(buildScriptPath);
        var lifecycleScript = File.ReadAllText(lifecycleScriptPath);
        var formalCaptureScript = File.ReadAllText(formalCaptureScriptPath);
        var formalVerificationScript =
            File.ReadAllText(formalVerificationScriptPath);
        var runningRepairCaptureScript =
            File.ReadAllText(runningRepairCaptureScriptPath);
        var runningRepairVerificationScript =
            File.ReadAllText(runningRepairVerificationScriptPath);
        var mainWindowSource = File.ReadAllText(mainWindowPath);

        return
        [
            Check(
                script.Contains(
                    $"AppId={{{{{AvaloniaPreviewAppId}}}",
                    StringComparison.Ordinal) &&
                !script.Contains(WpfAppId, StringComparison.OrdinalIgnoreCase) &&
                script.Contains(
                    "#define AppExeName \"LumaChart.Avalonia.exe\"",
                    StringComparison.Ordinal) &&
                !script.Contains(
                    "LumaChart.App.exe",
                    StringComparison.OrdinalIgnoreCase) &&
                script.Contains(
                    "OutputBaseFilename=LumaChartStudio-Avalonia-Preview-Setup",
                    StringComparison.Ordinal) &&
                !script.Contains(
                    "OutputBaseFilename=LumaChartStudio-Setup",
                    StringComparison.Ordinal),
                "avalonia-installer-preview-identity-cannot-replace-frozen-wpf-installation",
                $"path={installerPath}; appId={AvaloniaPreviewAppId}; exe=LumaChart.Avalonia.exe"),
            Check(
                script.Contains(
                    "#define AppVersion \"1.1.0\"",
                    StringComparison.Ordinal) &&
                script.Contains(
                    "#define AppFileVersion \"1.1.0.0\"",
                    StringComparison.Ordinal) &&
                script.Contains(
                    "VersionInfoVersion={#AppFileVersion}",
                    StringComparison.Ordinal) &&
                script.Contains(
                    "Source: \"{#SourceDir}\\*\"",
                    StringComparison.Ordinal) &&
                buildScript.Contains(
                    "PreviewFileVersion",
                    StringComparison.Ordinal) &&
                buildScript.Contains(
                    "PreviewDisplayVersion",
                    StringComparison.Ordinal) &&
                buildScript.Contains(
                    "(?:-[0-9A-Za-z]",
                    StringComparison.Ordinal) &&
                buildScript.Contains(
                    "/DAppFileVersion=$PreviewFileVersion",
                    StringComparison.Ordinal) &&
                buildScript.Contains(
                    "-p:PublishSingleFile=false",
                    StringComparison.Ordinal) &&
                buildScript.Contains(
                    "-p:PublishAot=false",
                    StringComparison.Ordinal) &&
                buildScript.Contains(
                    "-p:PublishTrimmed=false",
                    StringComparison.Ordinal) &&
                buildScript.Contains(
                    "Get-AuthenticodeSignature",
                    StringComparison.Ordinal) &&
                buildScript.Contains(
                    ".codex-nuget-packages",
                    StringComparison.Ordinal) &&
                buildScript.Contains(
                    ".codex-nuget-http-cache",
                    StringComparison.Ordinal) &&
                buildScript.Contains(
                    "Avalonia preview installer must not write to the reserved release root.",
                    StringComparison.Ordinal) &&
                !buildScript.Contains(
                    "frozenWpfInstaller",
                    StringComparison.OrdinalIgnoreCase) &&
                !buildScript.Contains(
                    "02C1184E6F3CF12DD0BECD323BF64B5B698ADA38C89C572D48DE60527582EDA4",
                    StringComparison.Ordinal) &&
                !script.Contains("OLLVM", StringComparison.OrdinalIgnoreCase) &&
                !script.Contains("NativeAOT", StringComparison.OrdinalIgnoreCase) &&
                !script.Contains("PublishAot", StringComparison.OrdinalIgnoreCase) &&
                !script.Contains("ClientSecurity.dll", StringComparison.OrdinalIgnoreCase) &&
                !buildScript.Contains(
                    "-p:PublishAot=true",
                    StringComparison.OrdinalIgnoreCase) &&
                !buildScript.Contains(
                    "-p:PublishTrimmed=true",
                    StringComparison.OrdinalIgnoreCase),
                "avalonia-installer-stages-ordinary-dotnet-release-with-stable-and-beta-version-metadata",
                $"default=1.1.0/1.1.0.0; beta semantic/display/file fields; script={buildScriptPath}; signed ISCC; D-drive caches; no retired WPF input, release-root output, protection, native ClientSecurity, or AOT"),
            Check(
                new[] { ".mc", ".mcz", ".osu", ".osz" }.All(extension =>
                    script.Contains(
                        $"Root: HKA; Subkey: \"Software\\Classes\\{extension}\\OpenWithProgids\"",
                        StringComparison.Ordinal)) &&
                !script.Contains("Root: HKCR", StringComparison.Ordinal) &&
                !new[] { ".mc", ".mcz", ".osu", ".osz" }.Any(extension =>
                    script.Contains(
                        $"Subkey: \"Software\\Classes\\{extension}\"; ValueType: string; ValueName: \"\"",
                        StringComparison.Ordinal)) &&
                // The recoverable editing cache is per user under %LOCALAPPDATA%;
                // the legacy folder beside the executable stays readable (for
                // migration) but no longer grants every user write access.
                script.Contains(
                    "Name: \"{app}\\LumaChart Editing Cache\"; Permissions: users-readexec; Flags: uninsneveruninstall",
                    StringComparison.Ordinal) &&
                !script.Contains(
                    "Permissions: users-modify",
                    StringComparison.Ordinal) &&
                script.Contains(
                    "Flags: uninsdeletekey; Tasks: fileassoc",
                    StringComparison.Ordinal) &&
                lifecycleScript.Contains(
                    "sameIdentityRepairPreservedEditingCache = $true",
                    StringComparison.Ordinal) &&
                lifecycleScript.Contains(
                    "upgrade-preservation-sentinel.json",
                    StringComparison.Ordinal),
                "avalonia-installer-open-with-registration-and-uninstall-boundaries-are-safe",
                "MC/MCZ/osu/OSZ use OpenWithProgids only; WPF default handler is not replaced; preview keys are uninstallable; same-identity repair must preserve recoverable editing cache"),
            Check(
                lifecycleScript.Contains(
                    "Invoke-HiddenProcessExpectFailure",
                    StringComparison.Ordinal) &&
                lifecycleScript.Contains(
                    "corrupted-current-setup.exe",
                    StringComparison.Ordinal) &&
                lifecycleScript.Contains(
                    "Generated invalid upgrade package is not a distinct truncation.",
                    StringComparison.Ordinal) &&
                lifecycleScript.Contains(
                    "Rejected corrupted upgrade changed the installed application.",
                    StringComparison.Ordinal) &&
                lifecycleScript.Contains(
                    "Rejected corrupted upgrade changed the recoverable editing cache.",
                    StringComparison.Ordinal) &&
                lifecycleScript.Contains(
                    "oldVersionPreserved = $true",
                    StringComparison.Ordinal),
                "avalonia-installer-corrupted-upgrade-fails-closed-before-valid-upgrade",
                "a generated truncated current setup must be rejected with the lower-version binary, exact editing-cache sentinel, and old version preserved"),
            Check(
                lifecycleScript.Contains(
                    "[switch]$ExerciseInstalledAssociationsGui",
                    StringComparison.Ordinal) &&
                lifecycleScript.Contains(
                    "Capture-WindowsAvaloniaFormatImportGui.ps1",
                    StringComparison.Ordinal) &&
                new[] { "mc", "mcz", "osu", "osz" }.All(name =>
                    lifecycleScript.Contains(
                        $"{name} = [ordered]@{{",
                        StringComparison.Ordinal)) &&
                lifecycleScript.Contains(
                    "installedOpenWithTargetGuiExercised",
                    StringComparison.Ordinal) &&
                lifecycleScript.Contains(
                    "Invoke-ForegroundCaptureWithRetry",
                    StringComparison.Ordinal) &&
                lifecycleScript.Contains(
                    "requires at least 3000 ms of system idle time",
                    StringComparison.Ordinal),
                "avalonia-installer-installed-open-with-target-exercises-four-formats-safely",
                "installed current payload fresh-opens MC/MCZ/osu/OSZ through the registered target while each foreground capture retains consent and the three-second idle gate"),
            Check(
                lifecycleScript.Contains(
                    "[switch]$ExerciseRunningClientRepair",
                    StringComparison.Ordinal) &&
                lifecycleScript.Contains(
                    "\"/CLOSEAPPLICATIONS\"",
                    StringComparison.Ordinal) &&
                lifecycleScript.Contains(
                    "Repair completed without closing the isolated installed client.",
                    StringComparison.Ordinal) &&
                lifecycleScript.Contains(
                    "Need to restart Windows? No",
                    StringComparison.Ordinal) &&
                lifecycleScript.Contains(
                    "processExitedDuringRepair = $true",
                    StringComparison.Ordinal) &&
                lifecycleScript.Contains(
                    "realChartAndAudioReopened = $true",
                    StringComparison.Ordinal) &&
                lifecycleScript.Contains(
                    "runningClientRepair = $runningClientRepairEvidence",
                    StringComparison.Ordinal) &&
                mainWindowSource.Contains(
                    "WindowsExternalApplicationShutdownPolicy.CanCloseImmediately",
                    StringComparison.Ordinal) &&
                LumaChart.Avalonia.Infrastructure
                    .WindowsExternalApplicationShutdownPolicy
                    .CanCloseImmediately(
                        global::Avalonia.Controls.WindowCloseReason.ApplicationShutdown,
                        isProgrammatic: false,
                        hasUnsavedDocuments: false,
                        hasBlockingWork: false,
                        hasActiveOnlineRoom: false) &&
                LumaChart.Avalonia.Infrastructure
                    .WindowsExternalApplicationShutdownPolicy
                    .CanCloseImmediately(
                        global::Avalonia.Controls.WindowCloseReason.WindowClosing,
                        isProgrammatic: false,
                        hasUnsavedDocuments: false,
                        hasBlockingWork: false,
                        hasActiveOnlineRoom: false) &&
                !LumaChart.Avalonia.Infrastructure
                    .WindowsExternalApplicationShutdownPolicy
                    .CanCloseImmediately(
                        global::Avalonia.Controls.WindowCloseReason.WindowClosing,
                        isProgrammatic: true,
                        hasUnsavedDocuments: false,
                        hasBlockingWork: false,
                        hasActiveOnlineRoom: false) &&
                !LumaChart.Avalonia.Infrastructure
                    .WindowsExternalApplicationShutdownPolicy
                    .CanCloseImmediately(
                        global::Avalonia.Controls.WindowCloseReason.ApplicationShutdown,
                        isProgrammatic: false,
                        hasUnsavedDocuments: true,
                        hasBlockingWork: false,
                        hasActiveOnlineRoom: false) &&
                !LumaChart.Avalonia.Infrastructure
                    .WindowsExternalApplicationShutdownPolicy
                    .CanCloseImmediately(
                        global::Avalonia.Controls.WindowCloseReason.ApplicationShutdown,
                        isProgrammatic: false,
                        hasUnsavedDocuments: false,
                        hasBlockingWork: true,
                        hasActiveOnlineRoom: false) &&
                !LumaChart.Avalonia.Infrastructure
                    .WindowsExternalApplicationShutdownPolicy
                    .CanCloseImmediately(
                        global::Avalonia.Controls.WindowCloseReason.ApplicationShutdown,
                        isProgrammatic: false,
                        hasUnsavedDocuments: false,
                        hasBlockingWork: false,
                        hasActiveOnlineRoom: true) &&
                runningRepairCaptureScript.Contains(
                    "-ExerciseRunningClientRepair",
                    StringComparison.Ordinal) &&
                runningRepairCaptureScript.Contains(
                    "-FormalMigrationAuthority",
                    StringComparison.Ordinal) &&
                runningRepairVerificationScript.Contains(
                    "RestartManager found an application using one of our files: LumaChart.Avalonia",
                    StringComparison.Ordinal) &&
                runningRepairVerificationScript.Contains(
                    "dirtyOrBusyOrOnlineCloseStillBlocked = $true",
                    StringComparison.Ordinal),
                "avalonia-installer-running-client-repair-closes-only-isolated-client-and-reopens-real-chart",
                "the isolated clean installed process can answer Restart Manager's non-programmatic WM_CLOSE or ApplicationShutdown synchronously, while app-initiated close, dirty documents, blocking work, and active rooms retain the interactive preflight; no reboot is required and the real MC/OGG reopens after repair"),
            Check(
                formalCaptureScript.Contains(
                    "Build-WindowsAvaloniaPreviewInstaller.ps1",
                    StringComparison.Ordinal) &&
                formalCaptureScript.Contains(
                    "-FormalMigrationAuthority",
                    StringComparison.Ordinal) &&
                formalCaptureScript.Contains(
                    "-ExerciseInstalledGui",
                    StringComparison.Ordinal) &&
                formalCaptureScript.Contains(
                    "-ExerciseInstalledAssociationsGui",
                    StringComparison.Ordinal) &&
                formalCaptureScript.Contains(
                    "avalonia-specs-evidence.json",
                    StringComparison.Ordinal) &&
                formalCaptureScript.Contains(
                    "main-specs-run.log",
                    StringComparison.Ordinal) &&
                formalCaptureScript.Contains(
                    "02C1184E6F3CF12DD0BECD323BF64B5B698ADA38C89C572D48DE60527582EDA4",
                    StringComparison.Ordinal) &&
                formalCaptureScript.Contains(
                    "0064F79585F4AED8A0B9EB8F4BA265B6F156606FB818234EF0885D31A39A2353",
                    StringComparison.Ordinal),
                "avalonia-installer-formal-slice-runs-current-build-full-gates-and-real-lifecycle",
                $"formal tool={formalCaptureScriptPath}; fresh ordinary Release setup; strict Avalonia/Avalonia Specs/main Specs; installed MC plus MCZ/osu/OSZ OpenWith target GUI; frozen baselines"),
            Check(
                formalCaptureScript.Contains(
                    "Remove-ExactStagingDirectory",
                    StringComparison.Ordinal) &&
                formalCaptureScript.Contains(
                    "Refusing to remove staging outside the formal root",
                    StringComparison.Ordinal) &&
                formalCaptureScript.Contains(
                    "publishedPayloadRetained = $false",
                    StringComparison.Ordinal) &&
                formalCaptureScript.Contains(
                    "compiledSetupRetained = $true",
                    StringComparison.Ordinal) &&
                formalCaptureScript.Contains(
                    "ollvmObfuscationPackingVirtualizationOrAotUsed = $false",
                    StringComparison.Ordinal),
                "avalonia-installer-formal-artifact-keeps-setup-but-discards-regenerable-publish-staging",
                "the exact compiled setup/evidence/logs remain while only verified staging under the fresh formal root is deleted; no OLLVM, protection, virtualization, or AOT"),
            Check(
                formalVerificationScript.Contains(
                    "corruptedCurrentPackageRejected = $true",
                    StringComparison.Ordinal) &&
                formalVerificationScript.Contains(
                    "installedOpenWithTargetMcMczOsuOszPassed = $true",
                    StringComparison.Ordinal) &&
                formalVerificationScript.Contains(
                    "defaultHandlersPreserved = $true",
                    StringComparison.Ordinal) &&
                formalVerificationScript.Contains(
                    "registryResidueCount = 0",
                    StringComparison.Ordinal) &&
                formalVerificationScript.Contains(
                    "installDirectoryResidueCount = 0",
                    StringComparison.Ordinal) &&
                formalVerificationScript.Contains(
                    "manualVisualReviewPassed = $true",
                    StringComparison.Ordinal) &&
                formalVerificationScript.Contains(
                    "fullWindowsMigrationComplete = $false",
                    StringComparison.Ordinal),
                "avalonia-installer-independent-verifier-rechecks-lifecycle-residue-and-reviewed-gui",
                $"verifier={formalVerificationScriptPath}; corrupt-package rejection, upgrade/repair/uninstall, four installed OpenWith targets, registry/directory residue, seven reviewed screenshots, and incomplete migration markers are independently asserted")
        ];
    }

    private static WindowsAvaloniaInstallerSpecResult Check(
        bool passed,
        string name,
        string detail) =>
        new(name, passed, detail);
}

internal sealed record WindowsAvaloniaInstallerSpecResult(
    string Name,
    bool Passed,
    string Detail);
