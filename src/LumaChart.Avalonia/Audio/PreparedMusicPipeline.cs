using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundTouch;
using SoundTouch.Net.NAudioSupport;

namespace LumaChart.Avalonia.Audio;

internal sealed record PreparedMusicPipeline(
    RateAdjustingSampleProvider? Rate,
    WindowsPhaseVocoderTimeStretchSampleProvider? PhaseVocoder,
    SoundTouchWaveProvider[] Stages,
    VolumeSampleProvider Volume,
    Func<bool> Ended) : IDisposable
{
    public static PreparedMusicPipeline Build(WaveStream source, TimeSpan position,
        double playbackRate, bool preservePitch, float volume, Action throwIfDisposed)
    {
        RateAdjustingSampleProvider? createdRateProvider = null;
        WindowsPhaseVocoderTimeStretchSampleProvider? createdPhaseVocoderProvider = null;
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
            SoundTouchContinuitySampleProvider? createdContinuityProvider = null;
            Func<bool> musicReallyEnded = RateSourceReallyEnded;
            // 1x needs no time stretching, including when preserve-pitch is checked.
            if (preservePitch && Math.Abs(playbackRate - 1) > .000001)
            {
                if (WindowsPitchPreservingTimeStretchPolicy.UsesPhaseVocoder(
                        playbackRate,
                        boundedSource.WaveFormat.Channels))
                {
                    createdPhaseVocoderProvider =
                        new WindowsPhaseVocoderTimeStretchSampleProvider(
                            boundedSource,
                            RateSourceReallyEnded,
                            playbackRate);
                    _ = createdPhaseVocoderProvider.Prime();
                    musicProvider = createdPhaseVocoderProvider;
                    musicReallyEnded = () => createdPhaseVocoderProvider.IsEnded;
                }
                else
                {
                    IWaveProvider stageSource = boundedSource.ToWaveProvider();
                    var processor = new SoundTouchProcessor();
                    var stageProvider = new SoundTouchWaveProvider(stageSource, processor)
                    {
                        Tempo = (float)WindowsPitchPreservingTimeStretchPolicy
                            .ResolveSoundTouchTempo(playbackRate),
                        Pitch = 1,
                        Rate = 1
                    };
                    WindowsPitchPreservingTimeStretchPolicy.Configure(processor);
                    createdSoundTouchProviders.Add(stageProvider);
                    stageSource = stageProvider;
                    createdContinuityProvider = new SoundTouchContinuitySampleProvider(
                        stageSource.ToSampleProvider(),
                        () => boundedSource.IsEnded);
                    musicProvider = createdContinuityProvider;
                    musicReallyEnded = () => createdContinuityProvider.IsEnded;
                }
            }
            else
            {
                createdRateProvider = new RateAdjustingSampleProvider(
                    boundedSource,
                    RateSourceReallyEnded)
                {
                    PlaybackRate = playbackRate
                };
                createdRateProvider.Reset(position);
                musicProvider = createdRateProvider;
            }

            createdVolumeProvider = new VolumeSampleProvider(musicProvider) { Volume = volume };
            return new PreparedMusicPipeline(createdRateProvider, createdPhaseVocoderProvider,
                [.. createdSoundTouchProviders], createdVolumeProvider, musicReallyEnded);
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
