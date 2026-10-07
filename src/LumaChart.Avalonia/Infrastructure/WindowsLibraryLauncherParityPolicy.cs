using LumaChart.Application.Workspaces;

namespace LumaChart.Avalonia.Infrastructure;

internal static class WindowsLibraryLauncherParityPolicy
{
    public const string ContractVersion = "wpf-1.1.0-library-launcher-r1";
    public const string OpenProjectPickerTitle = "选择具体谱面目录";
    public const string ChooseWorkspacePickerTitle =
        "选择 LumaChart 工作目录或 Malody chart 目录";

    public static WindowsNativeMessageBoxRequest CreateProjectDirectoryNotice(
        DesktopProjectDirectoryValidationException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Failure switch
        {
            DesktopProjectDirectoryValidationFailure.NoSupportedCharts =>
                new WindowsNativeMessageBoxRequest(
                    AvaloniaLocalizationCatalog.Format("这个目录中没有可直接打开的 .mc 或 .osu 谱面。\n\n请选择具体的歌曲 / 谱面目录。"),
                    "打开项目",
                    WindowsNativeMessageBoxIcon.Information),
            DesktopProjectDirectoryValidationFailure.TooManyCharts =>
                new WindowsNativeMessageBoxRequest(
                    AvaloniaLocalizationCatalog.Format("这个目录直属包含 {0} 张谱面，像是曲库根目录。\n\n请改选更具体的歌曲目录（最多 {1} 张）。", exception.ChartCount, exception.MaximumChartCount),
                    "打开项目",
                    WindowsNativeMessageBoxIcon.Warning),
            _ => throw new ArgumentOutOfRangeException(
                nameof(exception),
                exception.Failure,
                "Unsupported project-directory validation failure.")
        };
    }

    public static WindowsNativeMessageBoxRequest CreateOperationError(
        string operation,
        Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(exception);
        return new WindowsNativeMessageBoxRequest(
            $"{operation}\n\n{exception.Message}",
            "LumaChart Studio",
            WindowsNativeMessageBoxIcon.Error);
    }
}
