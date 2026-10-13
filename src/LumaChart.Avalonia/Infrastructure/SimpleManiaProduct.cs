namespace LumaChart.Avalonia.Infrastructure;

public static class SimpleManiaProduct
{
    public const string Name = "SimpleManiaEditor";
    public const string Version = "0.1.30";
    public const string Repository = "https://github.com/Ist1na07/SimpleManiaEditor";
    public const string Releases = Repository + "/releases";
    public const string Issues = Repository + "/issues/new";
    public const string AboutText = "SimpleManiaEditor\n面向 osu!mania / 7K 的本地编辑器\nCopyright © 2026 Ist1na\n基于 LumaChart Studio（MIT）改造；保留上游与第三方许可证。";
    // The upstream production account/update service is deliberately not part of this fork.
    public static bool OnlineEnabled => false;
}
