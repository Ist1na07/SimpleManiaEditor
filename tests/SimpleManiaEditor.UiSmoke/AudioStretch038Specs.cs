/// <summary>
/// Non-1x stretch tearing/stutter suite. AudioStretch038Specs is owned by one agent in this round; replace the body with
/// the real checks and return normally (any exception fails the suite).
/// </summary>
internal static class AudioStretch038Specs
{
    public static void Run(string root, string[] extraArgs)
    {
        _ = root;
        _ = extraArgs;
        throw new NotSupportedException("AudioStretch038Specs: suite not implemented yet");
    }
}
