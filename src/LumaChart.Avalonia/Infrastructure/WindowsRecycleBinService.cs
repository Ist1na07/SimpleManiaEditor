using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LumaChart.Avalonia.Infrastructure;

public sealed record WindowsRecycleBinOperationDescriptor(
    string FullPath,
    uint Operation,
    ushort Flags,
    string DoubleNullTerminatedSource,
    bool AllowUndoRequested);

public sealed record WindowsRecycleBinOperationResult(
    string FullPath,
    int ShellResult,
    bool AnyOperationsAborted,
    bool AllowUndoRequested,
    WindowsRecycleBinAvailability Availability = WindowsRecycleBinAvailability.Available);

/// <summary>
/// Whether a delete of a path can be undone from the Recycle Bin.
/// </summary>
public enum WindowsRecycleBinAvailability
{
    /// <summary>A fixed local volume; the Shell normally recycles.</summary>
    Available,

    /// <summary>
    /// Removable, network, optical or RAM volumes and UNC paths have no
    /// Recycle Bin; Windows deletes them permanently (after its own warning).
    /// </summary>
    LikelyPermanent,

    /// <summary>The platform has no Recycle Bin at all.</summary>
    Unsupported
}

/// <summary>
/// Windows Shell adapter matching the WPF 1.1.0 recycle-bin operation for a
/// single file or directory. The library deletion planner owns workspace-
/// boundary decisions; this adapter repeats filesystem safety checks
/// immediately before invoking the Shell.
/// </summary>
public static class WindowsRecycleBinService
{
    public const uint FileOperationDelete = 3;
    public const ushort AllowUndo = 0x0040;
    public const ushort Silent = 0x0004;
    public const ushort NoConfirmation = 0x0010;
    public const ushort NoErrorUi = 0x0400;

    /// <summary>
    /// FOF_WANTNUKEWARNING. With FOF_NOCONFIRMATION alone, an item that cannot
    /// be recycled (USB stick, network share, over the bin's quota) is deleted
    /// permanently and SHFileOperation still reports success. This flag makes
    /// Windows ask first; declining aborts the operation and nothing is deleted.
    /// </summary>
    public const ushort WantNukeWarning = 0x4000;

    public const ushort WpfCompatibleFlags =
        AllowUndo | Silent | NoConfirmation | NoErrorUi;

    /// <summary>The flags actually passed to the Shell.</summary>
    public const ushort SafeRecycleFlags = WpfCompatibleFlags | WantNukeWarning;

    public static WindowsRecycleBinOperationDescriptor Describe(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("回收站路径不能为空。", nameof(path));
        var fullPath = Path.GetFullPath(path);
        return new WindowsRecycleBinOperationDescriptor(
            fullPath,
            FileOperationDelete,
            SafeRecycleFlags,
            fullPath + '\0' + '\0',
            AllowUndoRequested: true);
    }

    /// <summary>
    /// Predicts, without touching the file, whether deleting
    /// <paramref name="path"/> can be undone. The UI should say "permanently
    /// delete" for anything but <see cref="WindowsRecycleBinAvailability.Available"/>.
    /// A full or undersized bin on a fixed volume cannot be predicted here; the
    /// Shell's own warning (<see cref="WantNukeWarning"/>) covers that case.
    /// </summary>
    public static WindowsRecycleBinAvailability GetAvailability(string path)
    {
        if (!OperatingSystem.IsWindows()) return WindowsRecycleBinAvailability.Unsupported;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) ||
                fullPath.StartsWith("//", StringComparison.Ordinal))
                return WindowsRecycleBinAvailability.LikelyPermanent;
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return WindowsRecycleBinAvailability.LikelyPermanent;
            return new DriveInfo(root).DriveType == DriveType.Fixed
                ? WindowsRecycleBinAvailability.Available
                : WindowsRecycleBinAvailability.LikelyPermanent;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or
                UnauthorizedAccessException)
        {
            return WindowsRecycleBinAvailability.LikelyPermanent;
        }
    }

    /// <summary>
    /// Convenience for the deletion planners' <c>permanentDeletion</c> argument.
    /// </summary>
    public static bool IsLikelyPermanentDeletion(string path) =>
        GetAvailability(path) != WindowsRecycleBinAvailability.Available;

    public static WindowsRecycleBinOperationResult MoveToRecycleBin(string path) =>
        MoveToRecycleBin(path, IntPtr.Zero);

    /// <param name="ownerWindow">
    /// Native owner for the Shell's "delete permanently?" prompt that
    /// <see cref="WantNukeWarning"/> may show; zero leaves it unowned.
    /// </param>
    public static WindowsRecycleBinOperationResult MoveToRecycleBin(string path, IntPtr ownerWindow)
    {
        var descriptor = Describe(path);
        if (!OperatingSystem.IsWindows())
        {
            // Throwing here left library deletion unusable wherever there is no
            // Shell. Platforms without a recycle bin delete outright; the
            // caller has already confirmed the removal and the planner has
            // already enforced the workspace boundary.
            return DeleteWithoutRecycleBin(descriptor);
        }

        if (!Directory.Exists(descriptor.FullPath) &&
            !File.Exists(descriptor.FullPath))
            throw new FileNotFoundException(
                AvaloniaLocalizationCatalog.Format("待删除文件或文件夹不存在：{0}", descriptor.FullPath),
                descriptor.FullPath);
        var attributes = File.GetAttributes(descriptor.FullPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("为避免越过工作目录边界，不能回收重解析点文件或文件夹。");

        var operation = new ShellFileOperation
        {
            WindowHandle = ownerWindow,
            Operation = descriptor.Operation,
            Source = descriptor.DoubleNullTerminatedSource,
            Destination = null,
            Flags = descriptor.Flags,
            AnyOperationsAborted = false,
            NameMappings = IntPtr.Zero,
            ProgressTitle = null
        };
        var availability = GetAvailability(descriptor.FullPath);
        var shellResult = SHFileOperation(ref operation);
        EnsureSucceeded(shellResult, operation.AnyOperationsAborted);
        return new WindowsRecycleBinOperationResult(
            descriptor.FullPath,
            shellResult,
            operation.AnyOperationsAborted,
            descriptor.AllowUndoRequested,
            availability);
    }

    /// <summary>
    /// Permanent removal for platforms with no recycle bin. The same filesystem
    /// safety checks have already run, so this only performs the delete and
    /// reports it in the shape callers expect; AllowUndoRequested stays false
    /// because nothing can be restored afterwards.
    /// </summary>
    private static WindowsRecycleBinOperationResult DeleteWithoutRecycleBin(
        WindowsRecycleBinOperationDescriptor descriptor)
    {
        if (Directory.Exists(descriptor.FullPath))
            Directory.Delete(descriptor.FullPath, recursive: true);
        else if (File.Exists(descriptor.FullPath))
            File.Delete(descriptor.FullPath);
        else
            throw new FileNotFoundException(
                AvaloniaLocalizationCatalog.Format("待删除文件或文件夹不存在：{0}", descriptor.FullPath),
                descriptor.FullPath);
        return new WindowsRecycleBinOperationResult(
            descriptor.FullPath,
            0,
            AnyOperationsAborted: false,
            AllowUndoRequested: false,
            WindowsRecycleBinAvailability.Unsupported);
    }

    public static void EnsureSucceeded(int shellResult, bool anyOperationsAborted)
    {
        if (shellResult != 0)
            throw new IOException(
                AvaloniaLocalizationCatalog.Format("Windows 回收站操作失败（0x{0:X4}）：{1}", shellResult, AvaloniaLocalizationCatalog.Text(new Win32Exception(shellResult).Message)));
        if (anyOperationsAborted)
            throw new IOException("Windows 已取消回收站操作，文件未删除。");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileOperation
    {
        public IntPtr WindowHandle;
        public uint Operation;
        [MarshalAs(UnmanagedType.LPWStr)] public string Source;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Destination;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref ShellFileOperation operation);
}
