namespace LumaChart.Avalonia.Infrastructure;

internal static class WindowsChartExportDialogParityPolicy
{
    public const string ContractVersion = "wpf-1.1.0-chart-export-dialog-r2";

    public const double WindowWidth = 540;
    public const double WindowHeight = 700;
    public const double HeaderHeight = 44;
    public const double HeaderLeftMargin = 16;
    public const double HeaderRightMargin = 7;
    public const double HeaderCloseWidth = 40;

    public const double ContentLeftMargin = 24;
    public const double ContentTopMargin = 18;
    public const double ContentRightMargin = 24;
    public const double ContentBottomMargin = 20;

    public const double ComboBoxHeight = 16;
    public const double NumberBoxHeight = 32;
    public const double ActionTopMargin = 18;
    public const double ActionGap = 6;
    public const double ActionHeight = 36;
    public const double CancelWidth = 62;
    public const double ConfirmWidth = 122;

    // WPF 1.1.0's SelectedIndex is applied before all named fields exist, so
    // its initial SelectionChanged handler returns early. The initial
    // FullPackage checkbox is therefore enabled until the mode is changed.
    public const bool InitialPreservePitchEnabled = true;

    public static WindowsNativeMessageBoxRequest CreateSuccessResult(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        return new WindowsNativeMessageBoxRequest(
            AvaloniaLocalizationCatalog.Format("导出完成\n\n{0}", destinationPath),
            "导出",
            WindowsNativeMessageBoxIcon.Information);
    }

    public static WindowsNativeMessageBoxRequest CreateFailureResult(
        string operation,
        string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        return new WindowsNativeMessageBoxRequest(
            $"{operation}\n\n{errorMessage}",
            "LumaChart Studio",
            WindowsNativeMessageBoxIcon.Error);
    }

    public static WindowsChartExportDestinationResolution ResolveDestinationPath(
        string selectedPath,
        string expectedExtension,
        Func<string, bool>? destinationExists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExtension);
        expectedExtension = expectedExtension.StartsWith(
            ".",
            StringComparison.Ordinal)
            ? expectedExtension
            : "." + expectedExtension;
        if (!AllowedDestinationExtensions.Contains(expectedExtension))
            throw new ArgumentOutOfRangeException(
                nameof(expectedExtension),
                expectedExtension,
                "Unsupported Windows chart-export destination extension.");

        var fullSelectedPath = Path.GetFullPath(selectedPath);
        var extensionAlreadyMatches = Path
            .GetExtension(fullSelectedPath)
            .Equals(expectedExtension, StringComparison.OrdinalIgnoreCase);
        var filenameWithoutLastExtension =
            Path.GetFileNameWithoutExtension(fullSelectedPath);
        var pickerAppendedAfterExpectedExtension =
            !extensionAlreadyMatches &&
            Path.GetExtension(filenameWithoutLastExtension).Equals(
                expectedExtension,
                StringComparison.OrdinalIgnoreCase);
        var destinationPath = extensionAlreadyMatches
            ? fullSelectedPath
            : pickerAppendedAfterExpectedExtension
                ? Path.Combine(
                    Path.GetDirectoryName(fullSelectedPath)!,
                    filenameWithoutLastExtension)
                : Path.ChangeExtension(fullSelectedPath, expectedExtension);
        var corrected = !string.Equals(
            fullSelectedPath,
            destinationPath,
            StringComparison.OrdinalIgnoreCase);
        var exists = destinationExists ?? File.Exists;
        return new WindowsChartExportDestinationResolution(
            fullSelectedPath,
            destinationPath,
            corrected,
            corrected && exists(destinationPath));
    }

    public static WindowsNativeMessageBoxRequest CreateOverwriteConfirmation(
        string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        return new WindowsNativeMessageBoxRequest(
            AvaloniaLocalizationCatalog.Format("“{0}”已存在。\n\n是否替换它？", Path.GetFileName(destinationPath)),
            "确认另存为",
            WindowsNativeMessageBoxIcon.Warning,
            WindowsNativeMessageBoxButtons.YesNo);
    }

    private static readonly HashSet<string> AllowedDestinationExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".mc",
            ".mcz",
            ".osu",
            ".osz"
        };
}

internal sealed record WindowsChartExportDestinationResolution(
    string SelectedPath,
    string DestinationPath,
    bool ExtensionCorrected,
    bool RequiresOverwriteConfirmation);
