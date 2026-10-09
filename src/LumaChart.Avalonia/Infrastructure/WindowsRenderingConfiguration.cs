// Only Program.cs, the Windows desktop entry point, applies these options, and
// the mobile hosts do not compile it. iOS additionally omits the Avalonia.Win32
// package that declares these types, so the whole file compiles out there.
#if !LUMACHART_IOS
using Avalonia;

namespace LumaChart.Avalonia.Infrastructure;

internal static class WindowsRenderingConfiguration
{
    public static Win32PlatformOptions Create()
    {
        var backend = EditorPresentationSettingsStore.LoadRenderBackend();
        var overrideValue = Environment.GetEnvironmentVariable("SIMPLEMANIA_RENDERER");
        if (overrideValue == "angle") backend = EditorRenderBackend.CompatibleAngle;
        if (overrideValue == "software") backend = EditorRenderBackend.Software;
        return new()
        {
            RenderingMode = backend == EditorRenderBackend.Software ? [Win32RenderingMode.Software] :
                backend == EditorRenderBackend.CompatibleAngle ? [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software] :
            [
                // Direct OpenGL avoids the extra WinUI commit-clock handoff.
                // Keep ANGLE and software available for drivers without WGL.
                Win32RenderingMode.Wgl,
                Win32RenderingMode.AngleEgl,
                Win32RenderingMode.Software
            ],
            CompositionMode = backend == EditorRenderBackend.CompatibleAngle ?
                [Win32CompositionMode.WinUIComposition, Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface] :
            [
                // Desktop composition still presents complete frames; this is
                // distinct from the tearing-prone low-latency DXGI swapchain.
                Win32CompositionMode.RedirectionSurface
            ]
        };
    }
}
#endif
