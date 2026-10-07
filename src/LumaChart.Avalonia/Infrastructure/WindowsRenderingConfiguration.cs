// Only Program.cs, the Windows desktop entry point, applies these options, and
// the mobile hosts do not compile it. iOS additionally omits the Avalonia.Win32
// package that declares these types, so the whole file compiles out there.
#if !LUMACHART_IOS
using Avalonia;

namespace LumaChart.Avalonia.Infrastructure;

internal static class WindowsRenderingConfiguration
{
    public static Win32PlatformOptions Create() =>
        new()
        {
            RenderingMode =
            [
                Win32RenderingMode.AngleEgl,
                Win32RenderingMode.Software
            ],
            CompositionMode =
            [
                Win32CompositionMode.LowLatencyDxgiSwapChain,
                Win32CompositionMode.WinUIComposition,
                Win32CompositionMode.DirectComposition,
                Win32CompositionMode.RedirectionSurface
            ]
        };
}
#endif
