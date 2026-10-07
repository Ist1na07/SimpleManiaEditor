using System.Security.Cryptography;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Services;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Verification;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.IO;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsSaveFailureRecoverySpecs
{
    public static async Task<WindowsSaveFailureRecoverySpecResult> RunAsync(
        string root,
        string fixturePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixturePath);

        var fullRoot = Path.GetFullPath(root);
        if (Directory.Exists(fullRoot))
            throw new InvalidOperationException(
                $"Save-failure evidence root must start empty: {fullRoot}");

        var libraryRoot = Path.Combine(fullRoot, "library");
        var editingRoot = Path.Combine(fullRoot, "editing");
        var sourcePath = Path.Combine(libraryRoot, "locked-source.mc");
        Directory.CreateDirectory(libraryRoot);
        File.Copy(fixturePath, sourcePath, overwrite: false);

        var sourceHashBefore = await ComputeSha256Async(sourcePath).ConfigureAwait(false);
        var workspace = new DesktopDocumentWorkspace(
            libraryRoot,
            editingRoot,
            "AvaloniaSpecs");
        await workspace.InitializeAsync().ConfigureAwait(false);
        var tab = await workspace.OpenAsync(sourcePath).ConfigureAwait(false);

        const string unsavedVersion = "Locked source recovery evidence";
        tab.Session.Mutate(document => document.Metadata.Version = unsavedVersion);
        var dirtyFingerprint = ChartStructuralFingerprint.Compute(tab.Session.Document);

        Exception? saveFailure = null;
        await using (var sourceLock = new FileStream(
                         sourcePath,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.None,
                         1,
                         FileOptions.None))
        {
            try
            {
                _ = await workspace.SaveAsync(tab.Id).ConfigureAwait(false);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                saveFailure = exception;
            }
        }

        var sourceHashAfterFailure =
            await ComputeSha256Async(sourcePath).ConfigureAwait(false);
        var workingDocument = MalodyMcSerializer.Load(tab.WorkingPath);
        var workingFingerprint = ChartStructuralFingerprint.Compute(workingDocument);
        var temporaryParts = Directory
            .EnumerateFiles(libraryRoot, "*.luma-part", SearchOption.AllDirectories)
            .ToArray();

        var restarted = new DesktopDocumentWorkspace(
            libraryRoot,
            editingRoot,
            "AvaloniaSpecs");
        await restarted.InitializeAsync().ConfigureAwait(false);
        var restored = restarted.Tabs.Single(candidate =>
            string.Equals(
                Path.GetFullPath(candidate.SourcePath),
                Path.GetFullPath(sourcePath),
                StringComparison.OrdinalIgnoreCase));
        var restoredFingerprint =
            ChartStructuralFingerprint.Compute(restored.Session.Document);

        var viewModelLibraryRoot = Path.Combine(fullRoot, "viewmodel-library");
        var viewModelEditingRoot = Path.Combine(fullRoot, "viewmodel-editing");
        var viewModelSourcePath = Path.Combine(viewModelLibraryRoot, "source.mc");
        Directory.CreateDirectory(viewModelLibraryRoot);
        File.Copy(fixturePath, viewModelSourcePath, overwrite: false);
        var controller = new AvaloniaWorkspaceController(
            viewModelLibraryRoot,
            viewModelEditingRoot,
            "AvaloniaSpecs");
        await controller.InitializeAsync().ConfigureAwait(false);
        var viewModelTab = await controller.OpenAsync(viewModelSourcePath)
            .ConfigureAwait(false);
        var viewModel = new MainWindowViewModel(controller);
        viewModelTab.Session.Mutate(
            document => document.Metadata.Version = "ViewModel persist failure");
        bool viewModelPersisted;
        await using (var workingLock = new FileStream(
                         viewModelTab.WorkingPath,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.None,
                         1,
                         FileOptions.None))
        {
            viewModelPersisted = await viewModel.PersistAsync().ConfigureAwait(false);
        }
        var viewModelFailureVisible =
            !viewModelPersisted &&
            viewModel.Status.StartsWith(
                "工作区缓存写入失败：",
                StringComparison.Ordinal) &&
            viewModelTab.Session.IsDirty;

        var corruptLibraryRoot = Path.Combine(fullRoot, "corrupt-library");
        var corruptEditingRoot = Path.Combine(fullRoot, "corrupt-editing");
        Directory.CreateDirectory(corruptLibraryRoot);
        var corruptManifestPath = Path.Combine(
            corruptEditingRoot,
            "desktop-workspace",
            "workspace.json");
        Directory.CreateDirectory(Path.GetDirectoryName(corruptManifestPath)!);
        await File.WriteAllTextAsync(
                corruptManifestPath,
                "{\"schemaVersion\":1,\"tabs\":[\"unterminated")
            .ConfigureAwait(false);
        var corruptController = new AvaloniaWorkspaceController(
            corruptLibraryRoot,
            corruptEditingRoot,
            "AvaloniaSpecs");
        var corruptViewModel = new MainWindowViewModel(corruptController);
        await corruptViewModel.InitializeAsync().ConfigureAwait(false);
        var corruptManifestNotice =
            corruptViewModel.RecoveryWarnings.Count == 1 &&
            WindowsWorkspaceRecoveryNoticePolicy.Create(
                corruptViewModel.RecoveryWarnings) is
            {
                Caption: "工作区恢复提示",
                Icon: WindowsNativeMessageBoxIcon.Warning
            };

        var passed =
            saveFailure != null &&
            tab.Session.IsDirty &&
            string.Equals(
                sourceHashBefore,
                sourceHashAfterFailure,
                StringComparison.OrdinalIgnoreCase) &&
            workingDocument.Metadata.Version == unsavedVersion &&
            string.Equals(
                dirtyFingerprint,
                workingFingerprint,
                StringComparison.Ordinal) &&
            temporaryParts.Length == 0 &&
            restored.Session.IsDirty &&
            restored.Session.Document.Metadata.Version == unsavedVersion &&
            string.Equals(
                dirtyFingerprint,
                restoredFingerprint,
                StringComparison.Ordinal) &&
            viewModelFailureVisible &&
            corruptManifestNotice;

        return new WindowsSaveFailureRecoverySpecResult(
            "windows-failed-source-save-retains-dirty-cache-across-restart",
            passed,
            $"failure={saveFailure?.GetType().Name ?? "<none>"}; " +
            $"sourceUnchanged={sourceHashBefore == sourceHashAfterFailure}; " +
            $"workingExact={dirtyFingerprint == workingFingerprint}; " +
            $"restoredDirty={restored.Session.IsDirty}; " +
            $"restoredExact={dirtyFingerprint == restoredFingerprint}; " +
            $"temporaryParts={temporaryParts.Length}; " +
            $"viewModelPersisted={viewModelPersisted}; " +
            $"viewModelFailureVisible={viewModelFailureVisible}; " +
            $"corruptManifestNotice={corruptManifestNotice}");
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream).ConfigureAwait(false));
    }
}

internal sealed record WindowsSaveFailureRecoverySpecResult(
    string Name,
    bool Passed,
    string Detail);
