namespace LumaChart.Application.Workspaces;

public enum DesktopProjectDirectoryValidationFailure
{
    NoSupportedCharts,
    TooManyCharts
}

public sealed class DesktopProjectDirectoryValidationException : IOException
{
    public DesktopProjectDirectoryValidationException(
        DesktopProjectDirectoryValidationFailure failure,
        int chartCount,
        int maximumChartCount,
        string message)
        : base(message)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chartCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumChartCount);
        Failure = failure;
        ChartCount = chartCount;
        MaximumChartCount = maximumChartCount;
    }

    public DesktopProjectDirectoryValidationFailure Failure { get; }

    public int ChartCount { get; }

    public int MaximumChartCount { get; }
}
