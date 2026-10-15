using osu.Framework.Testing;
using osu.Game.IO;

namespace SimpleManiaEditor.OsuHost;

internal static class PlaytestStorageVerification
{
    public static int Run()
    {
        using var owned = new TemporaryNativeStorage("sme-owned-" + Guid.NewGuid().ToString("N"));
        using var external = new TemporaryNativeStorage("sme-external-" + Guid.NewGuid().ToString("N"));
        var outside = external.GetFullPath("osu!.cfg");
        File.WriteAllText(outside, "external fixture must stay intact");
        var original = File.ReadAllBytes(outside);
        var redirect = owned.GetFullPath("storage.ini");
        File.WriteAllText(redirect, "FullPath = " + external.GetFullPath(string.Empty));
        var originalRedirect = File.ReadAllBytes(redirect);
        using var stream = new FixedPlaytestStorage(owned).GetStream("client.realm", FileAccess.Write, FileMode.Create);
        stream.WriteByte(42); stream.Close();
        var storage = new FixedPlaytestStorage(owned);
        if ((object)storage is OsuStorage || !File.Exists(owned.GetFullPath("client.realm")) || File.Exists(external.GetFullPath("client.realm")))
            throw new InvalidOperationException("Host followed a standalone client's storage redirect.");
        static void Reject(Action action)
        {
            try { action(); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException) { return; }
            throw new InvalidOperationException("Storage path escaped the owned root.");
        }
        Reject(() => storage.GetStream("../outside.txt", FileAccess.Write, FileMode.Create).Dispose());
        Reject(() => storage.GetStream(outside, FileAccess.Write, FileMode.Create).Dispose());
        Reject(() => storage.Move("client.realm", outside));
        Reject(() => storage.Delete(outside));
        if (OperatingSystem.IsWindows())
        {
            var link = owned.GetFullPath("external-link");
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, external.GetFullPath(string.Empty) }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException("Cannot construct the junction safety fixture.");
            try
            {
                Reject(() => storage.GetStream("external-link/osu!.cfg", FileAccess.Write, FileMode.Create).Dispose());
                Reject(() => storage.GetStorageForDirectory("external-link/new-directory"));
            }
            finally { Directory.Delete(link); } // Unlink only the junction created in this owned fixture.
        }
        if (!original.SequenceEqual(File.ReadAllBytes(outside)) || !originalRedirect.SequenceEqual(File.ReadAllBytes(redirect)))
            throw new InvalidOperationException("Storage verification changed an external file or silently rewrote configuration.");
        Console.WriteLine("PASS fixed playtest profile, ignored storage.ini redirect, no migration capability, traversal/absolute/junction writes blocked, external files unchanged");
        return 0;
    }
}
