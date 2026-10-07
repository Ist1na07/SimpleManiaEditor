using LumaChart.Avalonia.Infrastructure;
using NAudio.Vorbis;
using NAudio.Wave;

namespace LumaChart.Avalonia.Audio;

/// <summary>
/// Opens the canonical decoded timeline shared by playback and visualization.
/// Every consumer goes through here, so one decoder family serves playback,
/// waveform/spectrum, BPM analysis, Key sounds and export transcoding, and no
/// encoder-delay mismatch can appear between them.
///
/// Windows keeps NAudio's default MP3 path, whose ACM decompressor establishes
/// the current sample-accurate baseline. Every other platform gets the fully
/// managed NLayer decompressor instead, because ACM is a Windows component and
/// its absence otherwise fails all five consumers for any MP3 chart.
/// </summary>
internal static class AudioDecoder
{
    public static WaveStream OpenWaveStream(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("找不到音频文件。", fullPath);

        var extension = Path.GetExtension(fullPath);
        Span<byte> header = stackalloc byte[12];
        using (var stream = new FileStream(
                   fullPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            var read = stream.Read(header);
            header = header[..read];
        }

        return DetectContainer(header, extension) switch
        {
            AudioContainerKind.OggVorbis => new VorbisWaveReader(fullPath),
            AudioContainerKind.Mp3 => OpenMp3(fullPath),
            _ => OpenOtherContainer(fullPath, extension)
        };
    }

    private static WaveStream OpenMp3(string fullPath) =>
        OperatingSystem.IsWindows()
            ? new Mp3FileReader(fullPath)
            // Mp3FileReader hard-codes the ACM decompressor; the base type is
            // the only way to supply a different one, and it owns the file
            // stream exactly the same way.
            : new Mp3FileReaderBase(
                fullPath,
                waveFormat => new NLayer.NAudioSupport.Mp3FrameDecompressor(waveFormat));

    /// <summary>
    /// Wave data decodes managed everywhere. The remaining containers only ever
    /// worked through Media Foundation, so off Windows they fail with a
    /// platform error that says nothing about the user's file; name the format
    /// instead.
    /// </summary>
    private static WaveStream OpenOtherContainer(string fullPath, string extension)
    {
        if (OperatingSystem.IsWindows()) return new AudioFileReader(fullPath);

        try
        {
            return new AudioFileReader(fullPath);
        }
        catch (Exception exception) when (
            exception is not (FileNotFoundException or UnauthorizedAccessException))
        {
            var label = string.IsNullOrWhiteSpace(extension)
                ? "该音频格式"
                : extension.TrimStart('.').ToUpperInvariant();
            throw new NotSupportedException(
                AvaloniaLocalizationCatalog.Format("当前平台无法解码 {0} 音频；请改用 OGG、WAV 或 MP3。", label),
                exception);
        }
    }

    internal static AudioContainerKind DetectContainer(
        ReadOnlySpan<byte> header,
        string extension)
    {
        if (header.Length >= 4 &&
            header[0] == (byte)'O' && header[1] == (byte)'g' &&
            header[2] == (byte)'g' && header[3] == (byte)'S')
            return AudioContainerKind.OggVorbis;
        if (header.Length >= 3 &&
            header[0] == (byte)'I' && header[1] == (byte)'D' && header[2] == (byte)'3')
            return AudioContainerKind.Mp3;
        if (LooksLikeMpegAudioFrame(header)) return AudioContainerKind.Mp3;
        if (extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
            return AudioContainerKind.OggVorbis;
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            return AudioContainerKind.Mp3;
        return AudioContainerKind.Other;
    }

    private static bool LooksLikeMpegAudioFrame(ReadOnlySpan<byte> header)
    {
        if (header.Length < 2 || header[0] != byte.MaxValue || (header[1] & 0xE0) != 0xE0)
            return false;
        var version = (header[1] >> 3) & 0x03;
        var layer = (header[1] >> 1) & 0x03;
        return version != 0x01 && layer != 0;
    }
}

internal enum AudioContainerKind
{
    Other,
    OggVorbis,
    Mp3
}
