using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OggVorbisEncoder;
using LumaChart.Avalonia.Audio;
using SoundTouch.Net.NAudioSupport;

namespace LumaChart.Avalonia.Services;

/// <summary>
/// Windows-only streaming audio conversion used by the Avalonia export adapter.
/// Its decoder, rate-processing, resampling, channel normalization, and Vorbis
/// settings intentionally match the WPF 1.1.0 implementation.
/// </summary>
internal static class WindowsAudioTranscoder
{
    private const int TargetSampleRate = 44_100;
    private const int TargetChannels = 2;
    private const int FramesPerBlock = 4096;

    public static Task ConvertToOggAsync(
        string sourcePath,
        string destinationPath,
        double rate = 1,
        bool preservePitch = false,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => ConvertToOgg(
                sourcePath,
                destinationPath,
                rate,
                preservePitch,
                progress,
                cancellationToken),
            cancellationToken);

    private static void ConvertToOgg(
        string sourcePath,
        string destinationPath,
        double rate,
        bool preservePitch,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        rate = Math.Clamp(rate, .1, 4);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        using var source = OpenAudio(sourcePath);
        // Do not ask VorbisWaveReader for one more block after it has already
        // returned every PCM frame declared by the stream. Some otherwise
        // playable OGG files spin indefinitely in NVorbis on that final EOF
        // probe; playback/visualization already use the same bounded wrapper.
        ISampleProvider samples = LengthCappedSampleProvider.FromWaveStream(source);
        if (preservePitch && Math.Abs(rate - 1) > .000001)
        {
            var soundTouch = new SoundTouchWaveProvider(samples.ToWaveProvider())
            {
                Tempo = (float)rate,
                Pitch = 1,
                Rate = 1
            };
            samples = soundTouch.ToSampleProvider();
        }
        else if (Math.Abs(rate - 1) > .000001)
        {
            samples = new WindowsRateAdjustingSampleProvider(samples) { PlaybackRate = rate };
        }

        if (samples.WaveFormat.SampleRate != TargetSampleRate)
            samples = new WdlResamplingSampleProvider(samples, TargetSampleRate);
        samples = new StereoNormalizingSampleProvider(samples);

        var temporary = destinationPath + ".luma-part";
        try
        {
            using (var output = new FileStream(
                       temporary,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       128 * 1024,
                       FileOptions.SequentialScan))
            {
                EncodeVorbis(samples, output, source, progress, cancellationToken);
            }

            File.Move(temporary, destinationPath, true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // Preserve the conversion exception; best-effort cleanup matches WPF.
            }

            throw;
        }
    }

    private static WaveStream OpenAudio(string path) => AudioDecoder.OpenWaveStream(path);

    private static void EncodeVorbis(
        ISampleProvider source,
        Stream output,
        WaveStream decoder,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var info = VorbisInfo.InitVariableBitRate(TargetChannels, TargetSampleRate, .5f);
        var ogg = new OggStream(Random.Shared.Next());
        var comments = new Comments();
        comments.AddTag("ENCODER", "LumaChart Studio");
        ogg.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
        ogg.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(comments));
        ogg.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));
        FlushPages(ogg, output, true);

        var processing = ProcessingState.Create(info);
        var interleaved = new float[FramesPerBlock * TargetChannels];
        var planar = new[] { new float[FramesPerBlock], new float[FramesPerBlock] };
        var lastProgress = -1d;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(interleaved, 0, interleaved.Length);
            if (read <= 0) break;
            var frames = read / TargetChannels;
            for (var frame = 0; frame < frames; frame++)
            {
                planar[0][frame] = interleaved[frame * 2];
                planar[1][frame] = interleaved[frame * 2 + 1];
            }

            processing.WriteData(planar, frames);
            DrainPackets(processing, ogg, output);
            if (progress != null && decoder.TotalTime > TimeSpan.Zero)
            {
                var value = Math.Clamp(
                    decoder.CurrentTime.TotalMilliseconds / decoder.TotalTime.TotalMilliseconds,
                    0,
                    1);
                if (value - lastProgress >= .005)
                {
                    lastProgress = value;
                    progress.Report(value);
                }
            }
        }

        processing.WriteEndOfStream();
        DrainPackets(processing, ogg, output);
        FlushPages(ogg, output, true);
        progress?.Report(1);
    }

    private static void DrainPackets(ProcessingState processing, OggStream ogg, Stream output)
    {
        while (!ogg.Finished && processing.PacketOut(out var packet))
        {
            ogg.PacketIn(packet);
            FlushPages(ogg, output, false);
        }
    }

    private static void FlushPages(OggStream ogg, Stream output, bool force)
    {
        while (ogg.PageOut(out var page, force))
        {
            output.Write(page.Header, 0, page.Header.Length);
            output.Write(page.Body, 0, page.Body.Length);
        }
    }

    private sealed class StereoNormalizingSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly int channels;
        private float[] sourceBuffer = [];

        public StereoNormalizingSampleProvider(ISampleProvider source)
        {
            this.source = source;
            channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                source.WaveFormat.SampleRate,
                TargetChannels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var requestedFrames = count / TargetChannels;
            var requestedSamples = requestedFrames * channels;
            if (sourceBuffer.Length < requestedSamples)
                Array.Resize(ref sourceBuffer, requestedSamples);
            var read = source.Read(sourceBuffer, 0, requestedSamples);
            var frames = read / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sourceOffset = frame * channels;
                if (channels == 1)
                {
                    buffer[offset + frame * 2] = sourceBuffer[sourceOffset];
                    buffer[offset + frame * 2 + 1] = sourceBuffer[sourceOffset];
                }
                else
                {
                    buffer[offset + frame * 2] = sourceBuffer[sourceOffset];
                    buffer[offset + frame * 2 + 1] = sourceBuffer[sourceOffset + 1];
                }
            }

            return frames * TargetChannels;
        }
    }
}
