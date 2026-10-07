using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsSingleInstanceSpecs
{
    public static async Task<WindowsSingleInstanceSpecResult> RunAsync(
        string isolationRoot)
    {
        Directory.CreateDirectory(isolationRoot);
        var mcPath = Path.Combine(isolationRoot, "first chart 中文.mc");
        var osuPath = Path.Combine(isolationRoot, "second chart.osu");
        var ignoredPath = Path.Combine(isolationRoot, "ignored.txt");
        await File.WriteAllTextAsync(mcPath, "{}").ConfigureAwait(false);
        await File.WriteAllTextAsync(osuPath, "osu file format v14").ConfigureAwait(false);
        await File.WriteAllTextAsync(ignoredPath, "ignored").ConfigureAwait(false);

        var extracted = WindowsSingleInstancePolicy.ExtractSupportedPaths(
            [
                "--unknown",
                mcPath,
                ignoredPath,
                osuPath,
                mcPath
            ]);
        var policyPassed =
            WindowsSingleInstancePolicy.ShouldCoordinate([], null) &&
            !WindowsSingleInstancePolicy.ShouldCoordinate(
                ["--evidence-chart", mcPath],
                null) &&
            WindowsSingleInstancePolicy.ShouldCoordinate(
                [
                    WindowsSingleInstancePolicy.EvidenceForceCoordinateArgument,
                    "--evidence-chart",
                    mcPath
                ],
                null) &&
            !WindowsSingleInstancePolicy.ShouldCoordinate([], "1") &&
            WindowsSingleInstancePolicy.IsSupportedOpenPath("chart.MCZ") &&
            WindowsSingleInstancePolicy.IsSupportedOpenPath("chart.osz") &&
            !WindowsSingleInstancePolicy.IsSupportedOpenPath("chart.zip") &&
            extracted.Count == 2 &&
            string.Equals(extracted[0], Path.GetFullPath(mcPath), StringComparison.Ordinal) &&
            string.Equals(extracted[1], Path.GetFullPath(osuPath), StringComparison.Ordinal);

        var scope = $"spec-{Guid.NewGuid():N}";
        var primary = await WindowsSingleInstanceCoordinator.CreateOrForwardAsync(
                [],
                scope,
                allowMultipleInstances: null)
            .ConfigureAwait(false);
        using var coordinator = primary.Coordinator ??
                                throw new InvalidOperationException(
                                    "The first isolated launch did not become primary.");
        var requestAvailable = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.RequestAvailable += (_, _) => requestAvailable.TrySetResult();

        var secondary = await WindowsSingleInstanceCoordinator.CreateOrForwardAsync(
                [mcPath, ignoredPath, osuPath, mcPath],
                scope,
                allowMultipleInstances: null)
            .ConfigureAwait(false);
        await requestAvailable.Task
            .WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        var received = coordinator.TryDequeue(out var request);
        var requestPaths = received
            ? request.Paths
            : Array.Empty<string>();
        var transportPassed =
            primary.ContinueStartup &&
            !primary.ForwardedToPrimary &&
            !secondary.ContinueStartup &&
            secondary.ForwardedToPrimary &&
            secondary.Coordinator == null &&
            received &&
            request.Activate &&
            request.SenderProcessId == Environment.ProcessId &&
            requestPaths.Count == 2 &&
            string.Equals(requestPaths[0], Path.GetFullPath(mcPath), StringComparison.Ordinal) &&
            string.Equals(requestPaths[1], Path.GetFullPath(osuPath), StringComparison.Ordinal) &&
            !coordinator.TryDequeue(out _);

        return new WindowsSingleInstanceSpecResult(
            "windows-single-instance-forwards-deduplicated-mc-osu-to-primary",
            policyPassed && transportPassed,
            $"policy={policyPassed}; transport={transportPassed}; " +
            $"paths={string.Join('|', requestPaths.Select(Path.GetFileName))}; " +
            $"secondaryExited={!secondary.ContinueStartup}");
    }
}

internal sealed record WindowsSingleInstanceSpecResult(
    string Name,
    bool Passed,
    string Detail);
