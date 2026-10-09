using System.Diagnostics;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

internal static class ResidentSupervisorSpecs
{
    public static int Run(string executable, string root)
    {
        try { RunAsync(executable, root).GetAwaiter().GetResult(); return 0; }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
    private static async Task RunAsync(string executable, string root)
    {
        var runner = new OfficialPlaytestProcess(executable, verify: true);
        var original = OfficialPlaytestLauncher.ProcessRunner;
        OfficialPlaytestLauncher.ProcessRunner = runner;
        var source = Path.Combine(root, "supervisor-map"); Directory.CreateDirectory(source);
        var file = Path.Combine(source, "practice.osu");
        Program.WriteSimulationWave(Path.Combine(source, "song.wav"));
        var chart = new ChartDocument { FilePath = file, KeyCount = 7 }; chart.Metadata.AudioFile = "song.wav";
        for (var index = 0; index < 32; index++) chart.Notes.Add(new ChartNote { Lane = index % 7, Beat = BeatPosition.FromDouble(2 + index * .5) });
        OsuManiaSerializer.Save(chart, file);
        var times = new List<double>();
        try
        {
            var cold = Stopwatch.StartNew(); await runner.PrewarmAsync();
            Console.WriteLine($"Resident cold prewarm: {cold.Elapsed.TotalMilliseconds:0} ms");
            var pid = runner.ProcessId;
            for (var iteration = 0; iteration < 3; iteration++)
            {
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnReady() => ready.TrySetResult(true);
                runner.BecameReady += OnReady;
                var clock = Stopwatch.StartNew();
                if (iteration > 0) chart.Notes[0].Lane = iteration; // Include a current unsaved edit.
                var play = OfficialPlaytestLauncher.RunAsync(chart, file, 0, WindowsPlaytestSettings.Default, 1, true, 0);
                try
                {
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    times.Add(clock.Elapsed.TotalMilliseconds);
                    await play.WaitAsync(TimeSpan.FromSeconds(30));
                }
                finally { runner.BecameReady -= OnReady; }
                if (runner.ProcessId != pid) throw new InvalidOperationException("Supervisor restarted between playtests.");
            }
            runner.InterruptForVerification();
            await Task.Delay(300);
            await runner.PrewarmAsync();
            if (runner.ProcessId == pid) throw new InvalidOperationException("Supervisor did not replace its exited process.");
            await runner.ShutdownAsync();
            if (runner.ProcessId != null) throw new InvalidOperationException("Supervisor leaked its child.");
            Console.WriteLine("PASS production supervisor prewarm/IPC, same PID, edited snapshot/cache, crash restart and shutdown; end-to-end click ready ms=" + string.Join(",", times.Select(t => t.ToString("0.0"))));
        }
        finally { await runner.ShutdownAsync(); OfficialPlaytestLauncher.ProcessRunner = original; }
    }
}
