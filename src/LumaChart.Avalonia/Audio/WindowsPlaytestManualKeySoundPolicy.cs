namespace LumaChart.Avalonia.Audio;

internal enum WindowsPlaytestManualKeySoundOutput
{
    SharedMusic,
    StandaloneLowLatency
}

internal readonly record struct WindowsPlaytestManualKeySoundRoute(
    WindowsPlaytestManualKeySoundOutput Output,
    float Gain,
    double OutputLatencyMilliseconds);

internal static class WindowsPlaytestManualKeySoundPolicy
{
    internal const double SharedOutputLatencyMilliseconds = 40;

    internal static bool RequiresStandaloneWarmUp(bool musicLoaded) => !musicLoaded;

    internal static WindowsPlaytestManualKeySoundRoute Resolve(bool musicLoaded) => new(
        musicLoaded
            ? WindowsPlaytestManualKeySoundOutput.SharedMusic
            : WindowsPlaytestManualKeySoundOutput.StandaloneLowLatency,
        WindowsKeySoundService.ChordVoiceGain(1),
        musicLoaded
            ? SharedOutputLatencyMilliseconds
            : WindowsKeySoundService.StandaloneOutputLatencyMilliseconds);
}
