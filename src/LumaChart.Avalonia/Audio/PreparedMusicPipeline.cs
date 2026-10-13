using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundTouch;
using SoundTouch.Net.NAudioSupport;

namespace LumaChart.Avalonia.Audio;

internal sealed record PreparedMusicPipeline(
    SoundTouchWaveProvider[] Stages,
    VolumeSampleProvider Volume,
    Func<bool> Ended) : IDisposable
{
    public static PreparedMusicPipeline Build(WaveStream source, TimeSpan position,
        double playbackRate, bool preservePitch, float volume, Action throwIfDisposed)
    {
        var createdSoundTouchProviders = new List<SoundTouchWaveProvider>();
        VolumeSampleProvider? createdVolumeProvider = null;
        try
        {
            source.CurrentTime = position;
            // A decoder seek can be slow; do not build a device for an engine
            // whose disposal is already waiting on this call.
            throwIfDisposed();
            var boundedSource = LengthCappedSampleProvider.FromWaveStream(source);
            var activeSource = source;
            bool RateSourceReallyEnded() =>
                boundedSource.IsEnded ||
                activeSource.CurrentTime >=
                activeSource.TotalTime - TimeSpan.FromMilliseconds(250);
            ISampleProvider musicProvider;
            Func<bool> musicReallyEnded = RateSourceReallyEnded;
            // 1x needs no time stretching, including when preserve-pitch is checked.
            if (preservePitch && Math.Abs(playbackRate - 1) > .000001)
            {
                // One stereo-linked time-domain processor for every rate. Avoid
                // serial pitch shifts and their per-channel phase/limiter artefacts.
                var recordedSource = new SourceSilenceGuard.RecordingSource(boundedSource);
                // The correlation search is forward-looking. Centre that
                // search for slow playback, then cap the padded output to the
                // original duration. The guard still reads unpadded PCM time.
                var searchPaddingFrames = playbackRate <= .5
                    ? (int)Math.Round(boundedSource.WaveFormat.SampleRate * WindowsPitchPreservingTimeStretchPolicy.SeekWindowDurationMilliseconds / 2000d)
                    : 0;
                var originalOutputSamples = (long)Math.Round(boundedSource.RemainingSamples / (boundedSource.WaveFormat.Channels * playbackRate)) * boundedSource.WaveFormat.Channels;
                IWaveProvider stageSource = new LeadingSilenceSampleProvider(recordedSource, searchPaddingFrames).ToWaveProvider();
                var processor = new SoundTouchProcessor();
                var stageProvider = new SoundTouchWaveProvider(stageSource, processor)
                {
                    Tempo = (float)WindowsPitchPreservingTimeStretchPolicy.ResolveSoundTouchTempo(playbackRate),
                    Pitch = 1,
                    Rate = 1
                };
                WindowsPitchPreservingTimeStretchPolicy.Configure(processor);
                createdSoundTouchProviders.Add(stageProvider);
                var createdContinuityProvider = new SoundTouchContinuitySampleProvider(
                    stageProvider.ToSampleProvider(), () => boundedSource.IsEnded);
                var aligned = new LengthCappedSampleProvider(new SourceSilenceGuard(createdContinuityProvider, recordedSource, playbackRate), originalOutputSamples);
                musicProvider = aligned;
                musicReallyEnded = () => aligned.IsEnded;
            }
            else
            {
                var createdRateProvider = new RateAdjustingSampleProvider(
                    boundedSource,
                    RateSourceReallyEnded)
                {
                    PlaybackRate = playbackRate
                };
                createdRateProvider.Reset(position);
                musicProvider = createdRateProvider;
            }

            // Precompute the first 20ms on the preparation worker for every
            // rate, including decoder-only 1x. Retain it to preserve song onset.
            var primed = new PrimedMusicSampleProvider(musicProvider, musicReallyEnded);
            primed.Prime();
            throwIfDisposed();
            createdVolumeProvider = new VolumeSampleProvider(primed) { Volume = volume };
            return new PreparedMusicPipeline([.. createdSoundTouchProviders], createdVolumeProvider, () => primed.IsEnded);
        }
        catch
        {
            foreach (var stage in createdSoundTouchProviders)
                if (stage is IDisposable disposable) disposable.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var stage in Stages)
            if (stage is IDisposable disposable)
                try { disposable.Dispose(); } catch { }
    }
}
