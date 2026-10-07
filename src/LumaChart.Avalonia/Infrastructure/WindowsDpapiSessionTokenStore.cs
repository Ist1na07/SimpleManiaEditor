using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LumaChart.Storage;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Protects the revocable online token with Windows DPAPI for the current OS
/// user. Corrupt data fails closed and is removed; a transient read failure
/// (sharing violation, access denied) fails closed but keeps the file so the
/// session survives the next launch.
/// </summary>
public sealed class WindowsDpapiSessionTokenStore(IAppStorageLocations locations) : ISessionTokenStore
{
    private const int CryptProtectUiForbidden = 0x1;
    private const int MaximumPlaintextTokenBytes = 8 * 1024;
    private const int MaximumProtectedTokenBytes = 64 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly IAppStorageLocations locations =
        locations ?? throw new ArgumentNullException(nameof(locations));

    public string StorePath => Path.Combine(locations.Settings, "online-session.bin");

    public ValueTask<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        if (!File.Exists(StorePath)) return ValueTask.FromResult<string?>(null);

        try
        {
            var file = new FileInfo(StorePath);
            if (file.Length <= 0 || file.Length > MaximumProtectedTokenBytes)
                throw new InvalidDataException("The protected session token has an invalid size.");
            var protectedBytes = File.ReadAllBytes(StorePath);
            var plaintextBytes = Unprotect(protectedBytes);
            try
            {
                var value = StrictUtf8.GetString(plaintextBytes);
                return ValueTask.FromResult(string.IsNullOrWhiteSpace(value) ? null : value);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }
        }
        catch (Exception exception) when (
            exception is CryptographicException or InvalidDataException or DecoderFallbackException)
        {
            // Deterministic: this ciphertext can never be decrypted by this
            // user, or its size/encoding is invalid. Remove it.
            TryDelete();
            return ValueTask.FromResult<string?>(null);
        }
        catch
        {
            // I/O and sharing failures are possibly transient (antivirus,
            // backup, a second instance). The protected token is kept; this
            // launch simply starts signed out.
            return ValueTask.FromResult<string?>(null);
        }
    }

    public ValueTask SaveAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("A session token cannot be empty.", nameof(token));

        Directory.CreateDirectory(locations.Settings);
        var plaintextBytes = StrictUtf8.GetBytes(token);
        if (plaintextBytes.Length > MaximumPlaintextTokenBytes)
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
            throw new ArgumentException("The session token is too large.", nameof(token));
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = Protect(plaintextBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
        if (protectedBytes.Length > MaximumProtectedTokenBytes)
            throw new InvalidOperationException("The protected session token is too large.");

        var temporary = $"{StorePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, StorePath, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // Preserve the primary secure-write failure.
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        if (File.Exists(StorePath)) File.Delete(StorePath);
        return ValueTask.CompletedTask;
    }

    private void TryDelete()
    {
        try
        {
            if (File.Exists(StorePath)) File.Delete(StorePath);
        }
        catch
        {
            // Loading corrupt state must remain fail-closed even when cleanup is denied.
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows DPAPI is only available on Windows.");
    }

    private static byte[] Protect(byte[] data) => Transform(data, protect: true);
    private static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = new DataBlob();
        var output = new DataBlob();
        try
        {
            input.Size = data.Length;
            input.Data = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, input.Data, data.Length);
            var success = protect
                ? CryptProtectData(
                    ref input,
                    null,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output)
                : CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output);
            if (!success)
            {
                var error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                // Unprotect failures are deterministic for this ciphertext and
                // user; LoadAsync removes the unusable file on this type.
                if (!protect)
                    throw new CryptographicException(
                        "The protected session token could not be decrypted.",
                        error);
                throw new InvalidOperationException(
                    "无法保护本地登录会话。",
                    error);
            }

            var maximumOutputBytes = protect
                ? MaximumProtectedTokenBytes
                : MaximumPlaintextTokenBytes;
            if (output.Size <= 0 || output.Size > maximumOutputBytes)
                throw new InvalidDataException("Windows DPAPI returned an invalid session token size.");
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                ZeroUnmanaged(input.Data, input.Size);
                Marshal.FreeHGlobal(input.Data);
            }
            if (output.Data != IntPtr.Zero)
            {
                ZeroUnmanaged(output.Data, output.Size);
                _ = LocalFree(output.Data);
            }
        }
    }

    private static void ZeroUnmanaged(IntPtr data, int size)
    {
        if (data == IntPtr.Zero || size <= 0) return;
        for (var index = 0; index < size; index++) Marshal.WriteByte(data, index, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
