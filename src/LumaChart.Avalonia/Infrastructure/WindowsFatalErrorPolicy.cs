namespace LumaChart.Avalonia.Infrastructure;

internal static class WindowsFatalErrorPolicy
{
    public const string ContractVersion = "windows-managed-fatal-r1";
    public const string ControlledFaultArgument = "--evidence-controlled-fatal";
    public const string ControlledFaultEnvironment =
        "LUMACHART_AVALONIA_ENABLE_CONTROLLED_FATAL";
    public const int ControlledFaultDelayMilliseconds = 1800;

    public static bool ShouldTriggerControlledFault(
        IReadOnlyList<string> arguments,
        string? enableValue,
        string? isolatedLocalDataRoot) =>
        arguments.Contains(
            ControlledFaultArgument,
            StringComparer.OrdinalIgnoreCase) &&
        string.Equals(enableValue, "1", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(isolatedLocalDataRoot);

    public static WindowsNativeMessageBoxRequest CreateNotice(string? logPath)
    {
        var location = string.IsNullOrWhiteSpace(logPath)
            ? "诊断日志未能写入。"
            : AvaloniaLocalizationCatalog.Format("诊断日志：\n{0}", Path.GetFullPath(logPath));
        return new WindowsNativeMessageBoxRequest(
            AvaloniaLocalizationCatalog.Format("LumaChart Studio 遇到无法继续运行的错误，已安全停止。\n\n{0}", location),
            "LumaChart Studio - 运行错误",
            WindowsNativeMessageBoxIcon.Error);
    }
}
