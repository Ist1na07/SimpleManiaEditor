using System.ComponentModel;
using System.Runtime.InteropServices;
using LumaChart.Application.Archives;

namespace LumaChart.Avalonia.Services;

/// <summary>
/// Windows create-new directory boundary for archive staging. Native interop is
/// intentionally kept out of LumaChart.Application.
/// </summary>
public sealed class WindowsArchiveDirectoryCreator : IArchiveDirectoryCreator
{
    public static WindowsArchiveDirectoryCreator Instance { get; } = new();

    public bool TryCreate(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (CreateDirectoryW(ToExtendedLengthPath(absolutePath), IntPtr.Zero)) return true;

        var error = Marshal.GetLastWin32Error();
        if (error is 80 or 183) return false;
        throw new Win32Exception(error, "Unable to create an owned archive staging directory.");
    }

    private static string ToExtendedLengthPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return fullPath;
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + fullPath[2..];

        return @"\\?\" + fullPath;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);
}
