using LumaChart.Application.Libraries;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsLibraryLauncherSpecs
{
    public static IReadOnlyList<WindowsLibraryLauncherSpecResult> Run()
    {
        return
        [
            VerifyWpfLibraryLayoutGeometry(),
            VerifyWpfPickerAndNoticeContract(),
            VerifyProjectDirectoryValidationIsTypedAndBounded(),
            VerifyPersistedWorkspaceRootIsRecoveredWithoutOverride(),
            VerifyLongUnicodeProjectDirectoryEnumeration(),
            VerifyOperationErrorsMatchWpf()
        ];
    }

    private static WindowsLibraryLauncherSpecResult
        VerifyWpfLibraryLayoutGeometry()
    {
        var passed =
            WindowsLibraryLayoutPolicy.ContractVersion ==
            "wpf-1.1.0-library-layout-r3-stable-expand" &&
            WindowsLibraryLayoutPolicy.HeaderActionOuterMargin == 4 &&
            WindowsLibraryLayoutPolicy.HeaderActionGap == 8 &&
            WindowsLibraryLayoutPolicy.FilterColumnSpacing == 0 &&
            WindowsLibraryLayoutPolicy.SearchSortGap == 10 &&
            WindowsLibraryLayoutPolicy.SearchFontSize == 13 &&
            WindowsLibraryLayoutPolicy.FilterMargin ==
            new global::Avalonia.Thickness(20, 16, 20, 0) &&
            WindowsLibraryLayoutPolicy.ContentMargin ==
            new global::Avalonia.Thickness(20, 0, 20, 12) &&
            WindowsLibraryLayoutPolicy.HeaderTitleForeground == "#E1E5E6" &&
            WindowsLibraryLayoutPolicy.EmptyTitleForeground == "#E1E6E7" &&
            WindowsLibraryLayoutPolicy.EmptySubtitleForeground == "#829095" &&
            WindowsLibraryLayoutPolicy.WorkspaceForeground == "#7E898E" &&
            WindowsLibraryLayoutPolicy.SearchForeground == "#E0E4E5" &&
            WindowsLibraryLayoutPolicy.SearchCaret == "#9EB8BA" &&
            WindowsLibraryLayoutPolicy.SortBackground == "#15181A" &&
            WindowsLibraryLayoutPolicy.SortForeground == "#E3E6E8" &&
            WindowsLibraryLayoutPolicy.SortBorder == "#383E43" &&
            WindowsLibraryLayoutPolicy.FolderItemPadding ==
            new global::Avalonia.Thickness(10, 8) &&
            WindowsLibraryLayoutPolicy.FolderActionBackground == "#1A1D20" &&
            WindowsLibraryLayoutPolicy.FolderActionForeground == "#D0D4D6" &&
            WindowsLibraryLayoutPolicy.FolderActionBorder == "#34393D" &&
            WindowsLibraryLayoutPolicy.FolderActionCornerRadius == 3;
        return Result(
            "windows-library-layout-spacing-and-typography-match-wpf",
            passed,
            $"actions={WindowsLibraryLayoutPolicy.HeaderActionGap:0}dip; " +
            $"searchSort={WindowsLibraryLayoutPolicy.SearchSortGap:0}dip; " +
            $"filter={WindowsLibraryLayoutPolicy.FilterMargin}; " +
            $"content={WindowsLibraryLayoutPolicy.ContentMargin}; " +
            $"folderPadding={WindowsLibraryLayoutPolicy.FolderItemPadding}; " +
            $"searchFont={WindowsLibraryLayoutPolicy.SearchFontSize:0}dip");
    }

    private static WindowsLibraryLauncherSpecResult VerifyWpfPickerAndNoticeContract()
    {
        var empty = WindowsLibraryLauncherParityPolicy.CreateProjectDirectoryNotice(
            new DesktopProjectDirectoryValidationException(
                DesktopProjectDirectoryValidationFailure.NoSupportedCharts,
                0,
                DesktopDocumentWorkspace.MaximumProjectChartFiles,
                "empty"));
        var tooMany = WindowsLibraryLauncherParityPolicy.CreateProjectDirectoryNotice(
            new DesktopProjectDirectoryValidationException(
                DesktopProjectDirectoryValidationFailure.TooManyCharts,
                129,
                DesktopDocumentWorkspace.MaximumProjectChartFiles,
                "too many"));
        var passed =
            WindowsLibraryLauncherParityPolicy.ContractVersion ==
            "wpf-1.1.0-library-launcher-r1" &&
            WindowsLibraryLauncherParityPolicy.OpenProjectPickerTitle ==
            "选择具体谱面目录" &&
            WindowsLibraryLauncherParityPolicy.ChooseWorkspacePickerTitle ==
            "选择 LumaChart 工作目录或 Malody chart 目录" &&
            empty.Caption == "打开项目" &&
            empty.Icon == WindowsNativeMessageBoxIcon.Information &&
            empty.Message ==
            "这个目录中没有可直接打开的 .mc 或 .osu 谱面。\n\n" +
            "请选择具体的歌曲 / 谱面目录。" &&
            tooMany.Caption == "打开项目" &&
            tooMany.Icon == WindowsNativeMessageBoxIcon.Warning &&
            tooMany.Message ==
            "这个目录直属包含 129 张谱面，像是曲库根目录。\n\n" +
            "请改选更具体的歌曲目录（最多 128 张）。";
        return Result(
            "windows-library-launcher-picker-and-validation-notices-match-wpf",
            passed,
            $"openTitle={WindowsLibraryLauncherParityPolicy.OpenProjectPickerTitle}; " +
            $"workspaceTitle={WindowsLibraryLauncherParityPolicy.ChooseWorkspacePickerTitle}; " +
            $"emptyIcon={empty.Icon}; tooManyIcon={tooMany.Icon}");
    }

    private static WindowsLibraryLauncherSpecResult VerifyProjectDirectoryValidationIsTypedAndBounded()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"lumachart-library-launcher-spec-{Guid.NewGuid():N}");
        var empty = Path.Combine(root, "empty");
        var crowded = Path.Combine(root, "crowded");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(crowded);
        try
        {
            for (var index = 0;
                 index <= DesktopDocumentWorkspace.MaximumProjectChartFiles;
                 index++)
            {
                File.WriteAllText(
                    Path.Combine(crowded, $"chart-{index:D3}.mc"),
                    "{}");
            }

            var emptyFailure = CaptureValidation(empty);
            var crowdedFailure = CaptureValidation(crowded);
            var passed =
                emptyFailure?.Failure ==
                DesktopProjectDirectoryValidationFailure.NoSupportedCharts &&
                emptyFailure.ChartCount == 0 &&
                emptyFailure.MaximumChartCount ==
                DesktopDocumentWorkspace.MaximumProjectChartFiles &&
                crowdedFailure?.Failure ==
                DesktopProjectDirectoryValidationFailure.TooManyCharts &&
                crowdedFailure.ChartCount ==
                DesktopDocumentWorkspace.MaximumProjectChartFiles + 1 &&
                crowdedFailure.MaximumChartCount ==
                DesktopDocumentWorkspace.MaximumProjectChartFiles;
            return Result(
                "windows-library-launcher-project-directory-validation-is-typed-and-bounded",
                passed,
                $"empty={emptyFailure?.Failure}/{emptyFailure?.ChartCount}; " +
                $"crowded={crowdedFailure?.Failure}/{crowdedFailure?.ChartCount}; " +
                $"maximum={DesktopDocumentWorkspace.MaximumProjectChartFiles}");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static DesktopProjectDirectoryValidationException? CaptureValidation(
        string directoryPath)
    {
        try
        {
            _ = DesktopDocumentWorkspace.EnumerateProjectChartPaths(directoryPath);
            return null;
        }
        catch (DesktopProjectDirectoryValidationException exception)
        {
            return exception;
        }
    }

    private static WindowsLibraryLauncherSpecResult
        VerifyPersistedWorkspaceRootIsRecoveredWithoutOverride()
    {
        const string localDataVariable =
            "LUMACHART_AVALONIA_LOCAL_DATA_ROOT";
        const string workspaceVariable =
            "LUMACHART_AVALONIA_WORKSPACE_ROOT";
        var previousLocalData =
            Environment.GetEnvironmentVariable(localDataVariable);
        var previousWorkspace =
            Environment.GetEnvironmentVariable(workspaceVariable);
        var root = Path.Combine(
            Path.GetTempPath(),
            $"lumachart-library-launcher-persistence-spec-{Guid.NewGuid():N}");
        var localData = Path.Combine(root, "local-data");
        var persistedWorkspace =
            Path.Combine(root, "Persisted Workspace 中文");
        try
        {
            Directory.CreateDirectory(localData);
            Directory.CreateDirectory(persistedWorkspace);
            Environment.SetEnvironmentVariable(
                localDataVariable,
                localData);
            Environment.SetEnvironmentVariable(
                workspaceVariable,
                null);
            AvaloniaAppPaths.SaveWorkspacePreferences(
                persistedWorkspace,
                DesktopLibrarySort.AddedDescending);
            var resolved = AvaloniaAppPaths.ResolveInitialWorkspaceRoot();
            var settingsPath = AvaloniaAppPaths.WorkspaceSettingsPath;
            var orphanTemporaryFiles = Directory
                .EnumerateFiles(localData, ".workspace.json.*.tmp")
                .Count();
            var passed =
                string.Equals(
                    Path.GetFullPath(resolved),
                    Path.GetFullPath(persistedWorkspace),
                    StringComparison.OrdinalIgnoreCase) &&
                File.Exists(settingsPath) &&
                orphanTemporaryFiles == 0;
            return Result(
                "windows-library-launcher-persisted-workspace-root-recovers-without-override",
                passed,
                $"resolved={resolved}; settings={settingsPath}; " +
                $"temp={orphanTemporaryFiles}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                localDataVariable,
                previousLocalData);
            Environment.SetEnvironmentVariable(
                workspaceVariable,
                previousWorkspace);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static WindowsLibraryLauncherSpecResult
        VerifyLongUnicodeProjectDirectoryEnumeration()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"lumachart-library-launcher-long-path-spec-{Guid.NewGuid():N}");
        var project = root;
        while (project.Length < 330)
        {
            project = Path.Combine(
                project,
                $"Long Segment 中文 {project.Length:D3}");
        }

        var nested = Path.Combine(project, "nested");
        try
        {
            Directory.CreateDirectory(nested);
            var mc = Path.Combine(project, "01 长路径.mc");
            var osu = Path.Combine(project, "02 long path.osu");
            File.WriteAllText(mc, "{}");
            File.WriteAllText(osu, "osu file format v14");
            File.WriteAllText(Path.Combine(nested, "ignored.mc"), "{}");

            var paths =
                DesktopDocumentWorkspace.EnumerateProjectChartPaths(project);
            var passed =
                project.Length >= 330 &&
                paths.Count == 2 &&
                string.Equals(paths[0], mc, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(paths[1], osu, StringComparison.OrdinalIgnoreCase) &&
                paths.All(path =>
                    string.Equals(
                        Path.GetDirectoryName(path),
                        project,
                        StringComparison.OrdinalIgnoreCase));
            return Result(
                "windows-library-launcher-long-unicode-project-directory-enumerates-direct-charts",
                passed,
                $"length={project.Length}; count={paths.Count}; " +
                $"names={string.Join("|", paths.Select(Path.GetFileName))}");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static WindowsLibraryLauncherSpecResult VerifyOperationErrorsMatchWpf()
    {
        var denied = WindowsLibraryLauncherParityPolicy.CreateOperationError(
            "无法打开项目目录",
            new UnauthorizedAccessException("拒绝访问测试"));
        var longPath = WindowsLibraryLauncherParityPolicy.CreateOperationError(
            "无法打开项目目录",
            new PathTooLongException("路径过长测试"));
        var missing = WindowsLibraryLauncherParityPolicy.CreateOperationError(
            "无法打开项目目录",
            new DirectoryNotFoundException("断链目录测试"));
        var chart = WindowsLibraryLauncherParityPolicy.CreateOperationError(
            "无法打开谱面",
            new InvalidDataException("谱面损坏测试"));
        var archive = WindowsLibraryLauncherParityPolicy.CreateOperationError(
            "无法导入谱包",
            new InvalidDataException("谱包损坏测试"));
        var save = WindowsLibraryLauncherParityPolicy.CreateOperationError(
            "无法保存原谱面；软件旁缓存已保留",
            new UnauthorizedAccessException("保存拒绝测试"));
        var image = WindowsLibraryLauncherParityPolicy.CreateOperationError(
            "无法复制图片到谱面目录",
            new IOException("图片复制失败测试"));
        var passed =
            denied.Caption == "LumaChart Studio" &&
            denied.Icon == WindowsNativeMessageBoxIcon.Error &&
            denied.Message == "无法打开项目目录\n\n拒绝访问测试" &&
            longPath.Message == "无法打开项目目录\n\n路径过长测试" &&
            missing.Message == "无法打开项目目录\n\n断链目录测试" &&
            chart.Caption == "LumaChart Studio" &&
            chart.Icon == WindowsNativeMessageBoxIcon.Error &&
            chart.Message == "无法打开谱面\n\n谱面损坏测试" &&
            archive.Caption == "LumaChart Studio" &&
            archive.Icon == WindowsNativeMessageBoxIcon.Error &&
            archive.Message == "无法导入谱包\n\n谱包损坏测试" &&
            save.Caption == "LumaChart Studio" &&
            save.Icon == WindowsNativeMessageBoxIcon.Error &&
            save.Message ==
            "无法保存原谱面；软件旁缓存已保留\n\n保存拒绝测试" &&
            image.Caption == "LumaChart Studio" &&
            image.Icon == WindowsNativeMessageBoxIcon.Error &&
            image.Message == "无法复制图片到谱面目录\n\n图片复制失败测试";
        return Result(
            "windows-library-launcher-operation-errors-match-wpf",
            passed,
            $"denied={denied.Icon}/{denied.Caption}; " +
            $"long={longPath.Message}; missing={missing.Message}; " +
            $"chart={chart.Message}; archive={archive.Message}; " +
            $"save={save.Message}; image={image.Message}");
    }

    private static WindowsLibraryLauncherSpecResult Result(
        string name,
        bool passed,
        string detail) =>
        new(name, passed, detail);
}

internal sealed record WindowsLibraryLauncherSpecResult(
    string Name,
    bool Passed,
    string Detail);
