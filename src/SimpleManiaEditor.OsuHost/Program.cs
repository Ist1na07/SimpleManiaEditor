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
            var resident = args.Contains("--resident");
            using var game = new PlaytestGame(request, requestPath, args.Contains("--verify"), args.Contains("--verify-settings"), resident,
                resident ? message => { Console.WriteLine(message.ToJson()); Console.Out.Flush(); } : null);
            if (resident)
            {
                _ = Task.Run(async () =>
                {
                    while (await Console.In.ReadLineAsync() is { } line)
                    {
                        var message = ResidentMessage.Parse(line);
                        if (message?.Kind == "open") game.Enqueue(() => game.AcceptRequest(message.Path));
                        else if (message?.Kind == "return") game.Enqueue(game.ReturnToEditor);
                        else if (message?.Kind == "shutdown") { host.Exit(); return; }
                    }
                    host.Exit(); // Parent closed its pipe or exited unexpectedly.
                });
            }
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
