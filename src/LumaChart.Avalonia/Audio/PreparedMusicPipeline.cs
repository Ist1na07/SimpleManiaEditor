using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LumaChart.Avalonia.Audio;

internal sealed record PreparedMusicPipeline(
    object[] Stages,
    VolumeSampleProvider Volume,
    Func<bool> Ended) : IDisposable
{
    public static PreparedMusicPipeline Build(WaveStream source, TimeSpan position,
        double playbackRate, bool preservePitch, float volume, Action throwIfDisposed)
    {
        var createdSoundTouchProviders = new List<object>();
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
                var aligned = new TransientAnchoredStretchSampleProvider(
                    boundedSource, boundedSource.RemainingSamples, playbackRate, () => boundedSource.IsEnded);
                createdSoundTouchProviders.Add(aligned);
                // The transient-anchored provider bounds every processor before an
                // attack, so the former output-gain silence guard is not needed to
                // keep attacks out of quiet gaps; its per-sample gain steps were an
                // audible click source on percussive material.
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

            // Everything above this line used to run inside the render callback.
            // A background producer now keeps a 1.4 s ring full, so the callback
            // only copies samples at every rate (see PrefetchingSampleProvider).
            // The plain 1x path stays direct: it only decodes (no stretch work),
            // and its existing end-of-stream/decoder-time contract is what the
            // seek and rate-change commit tests rely on.
            ISampleProvider staged = musicProvider;
            if (preservePitch && Math.Abs(playbackRate - 1) > .000001)
            {
                var prefetched = new PrefetchingSampleProvider(musicProvider);
                createdSoundTouchProviders.Add(prefetched);
                staged = prefetched;
                musicReallyEnded = () => prefetched.IsEnded;
            }

            // Precompute the first 20ms on the preparation worker for every
            // rate, including decoder-only 1x. Retain it to preserve song onset.
            var primed = new PrimedMusicSampleProvider(staged, musicReallyEnded);
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