using osu.Framework;
using osu.Framework.Platform;
using SimpleManiaEditor.PlaytestBridge;

namespace SimpleManiaEditor.OsuHost;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? requestPath = null;
        try
        {
            if (args.Length < 2 || args[0] != "--request") throw new ArgumentException("Use --request <manifest.json>.");
            requestPath = Path.GetFullPath(args[1]);
            var request = PlaytestRequest.Read(requestPath);
            Directory.CreateDirectory(request.DataRoot);
            // The framework's portable data directory follows the working directory.
            // Every database, imported skin, log and configuration belongs to this host.
            Directory.SetCurrentDirectory(request.DataRoot);
            using var host = Host.GetSuitableDesktopHost("SimpleManiaEditor.OsuHost", new HostOptions
            {
                IPCPipeName = "SimpleManiaEditor-playtest-" + Environment.ProcessId,
                FriendlyGameName = "SimpleManiaEditor · osu!mania",
                PortableInstallation = true
            });
            using var game = new PlaytestGame(request, requestPath, args.Contains("--verify"), args.Contains("--verify-settings"));
            host.Run(game);
            return game.Failure == null ? 0 : 1;
        }
        catch (Exception exception)
        {
            if (requestPath != null) File.WriteAllText(requestPath + ".error.txt", exception.ToString());
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
