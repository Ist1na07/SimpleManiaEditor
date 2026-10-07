namespace LumaChart.Avalonia.Infrastructure;

internal static class WindowsWorkspaceRecoveryNoticePolicy
{
    public const string ContractVersion = "windows-workspace-recovery-r1";
    private const int MaximumVisibleWarnings = 8;
    private const int MaximumWarningLength = 280;

    public static WindowsNativeMessageBoxRequest? Create(
        IReadOnlyList<string> recoveryWarnings)
    {
        ArgumentNullException.ThrowIfNull(recoveryWarnings);
        var warnings = recoveryWarnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Select(Normalize)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (warnings.Length == 0) return null;

        var visible = warnings
            .Take(MaximumVisibleWarnings)
            .Select((warning, index) => $"{index + 1}. {warning}")
            .ToArray();
        var omitted = warnings.Length - visible.Length;
        var suffix = omitted > 0
            ? AvaloniaLocalizationCatalog.Format("\n\n另有 {0} 条提示未在此窗口展开。", omitted)
            : string.Empty;
        return new WindowsNativeMessageBoxRequest(
            AvaloniaLocalizationCatalog.Format("工作区已尽可能恢复，但以下缓存或清单项目未能按原状态载入：\n\n{0}{1}\n\n程序不会用这些缓存静默覆盖可读取的源谱面。请检查已恢复标签，源文件缺失时请使用“另存为”保留内容。", string.Join("\n", visible), suffix),
            "工作区恢复提示",
            WindowsNativeMessageBoxIcon.Warning);
    }

    private static string Normalize(string warning)
    {
        var normalized = string.Join(
            ' ',
            warning
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaximumWarningLength
            ? normalized
            : normalized[..(MaximumWarningLength - 1)] + "…";
    }
}
