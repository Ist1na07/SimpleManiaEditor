using System.Security.Cryptography;
using LumaChart.Avalonia.Audio;
using NAudio.Wave;

/// <summary>Deterministic barriers around real decoder preparation and native mixer commits.</summary>
internal static class AudioCommit036Specs
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Wait(ManualResetEventSlim barrier, string message)
    { if (!barrier.Wait(TimeSpan.FromSeconds(12))) throw new TimeoutException(message); }
    public static void Run(string root) => RunAsync(root).GetAwaiter().GetResult();

    private static async Task RunAsync(string root)
    {
        var wave = Path.Combine(root, "audio-gate036.wav"); Program.WriteSimulationWave(wave);
        var before = SHA256.HashData(File.ReadAllBytes(wave)); var attributes = File.GetAttributes(wave);
        File.SetAttributes(wave, attributes | FileAttributes.ReadOnly);
        var previousOutputs = PlatformAudioOutputs.Current;
        var outputs = new SimulatedAudioOutputFactory(); PlatformAudioOutputs.Current = outputs;
        Transition? current = null; var controls = new List<Transition>(); var decoderOpens = 0; var factoryCalls = 0;
        WindowsAudioDeviceEngine? engine = null;
        try
        {
            await using var audio = new WindowsAudioPlaybackService(() =>
            {
                Interlocked.Increment(ref factoryCalls);
                return engine = new WindowsAudioDeviceEngine(openDecoder: path =>
                {
                    Interlocked.Increment(ref decoderOpens);
                    var transition = Volatile.Read(ref current);
                    if (transition != null && Interlocked.Exchange(ref transition.OpenSeen, 1) == 0)
                    { transition.OpenEntered.Set(); Wait(transition.ContinueOpen, "decoder preparation barrier timed out"); }
                    return new WaveFileReader(path);
                });
            }, TimeSpan.Zero);
            await audio.LoadAsync(wave); await audio.SetPreservePitchAsync(true); await audio.PlayAsync();
            // Service subscribes before this observer. When this barrier fires,
            // the commit epoch is already invalid but source replacement has
            // deliberately not yet happened.
            engine!.SourceCommitStarting += (_, _) =>
            {
                var transition = Volatile.Read(ref current); if (transition == null) return;
                transition.CommitEntered.Set(); Wait(transition.ContinueCommit, "source commit barrier timed out");
                if (transition.FailCommit) throw new InvalidOperationException("synthetic commit failure");
            };
            Transition Arm(bool fail = false)
            { var transition = new Transition { FailCommit = fail }; controls.Add(transition); Volatile.Write(ref current, transition); return transition; }
            void CheckPreparationOverlays(Transition transition)
            {
                Wait(transition.OpenEntered, "rate/seek did not enter decoder preparation");
                var token = audio.CapturePlaybackEpoch(); var opens = Volatile.Read(ref decoderOpens);
                Require(audio.IsPlaybackEpochCurrent(token) && audio.Snapshot.IsPlaying, "preparation suspended the still-running music clock");
                for (var i = 0; i < 4; i++)
                Require(audio.TryPlayOverlay(new SourceTimedSampleProvider(new ConstantProvider(), audio.Snapshot.Position.TotalMilliseconds + 30 + i * 5, 0), token),
                "control operation gate rejected a valid key sound while old music continued");
                Require(Volatile.Read(ref decoderOpens) == opens && factoryCalls == 1,
                "enqueuing key sounds opened another decoder or device engine");
            }
            void CheckCommitBlocked(Transition transition, AudioPlaybackEpochToken previous)
            {
                Wait(transition.CommitEntered, "source commit hook was not reached");
                Require(!audio.IsPlaybackEpochCurrent(previous) && !audio.CapturePlaybackEpoch().IsCurrent &&
                !audio.TryPlayOverlay(new ConstantProvider(), previous),
                "source commit exposed a new lease or accepted an old-timeline batch");
            }

            try
            {
                var rate = Arm(); var changingRate = audio.SetPlaybackRateAsync(.5);
                CheckPreparationOverlays(rate);
                var bytes = outputs.Outputs.Sum(o => o.BytesRead);
                await Task.Delay(80);
                Require(outputs.Outputs.Sum(o => o.BytesRead) > bytes && engine.IsPlaying,
                "independent rate preparation stopped the old music render stream");
                var oldRateToken = audio.CapturePlaybackEpoch(); rate.ContinueOpen.Set();
                CheckCommitBlocked(rate, oldRateToken); rate.ContinueCommit.Set(); await changingRate;
                Require(!audio.IsPlaybackEpochCurrent(oldRateToken) && audio.IsPlaybackEpochCurrent(audio.CapturePlaybackEpoch()) &&
                audio.TryPlayOverlay(new ConstantProvider(), audio.CapturePlaybackEpoch()),
                "rate commit failed to resume fresh-epoch scheduling");

                var seek = Arm(); var seeking = audio.SeekAsync(TimeSpan.FromSeconds(3));
                CheckPreparationOverlays(seek);
                var oldSeekToken = audio.CapturePlaybackEpoch();
                var lateProvider = new BarrierFormatProvider();
                var lateBatch = Task.Run(() => audio.TryPlayOverlay(lateProvider, oldSeekToken));
                try
                {
                    Wait(lateProvider.FormatEntered, "late overlay did not enter its WaveFormat conversion");
                    seek.ContinueOpen.Set(); CheckCommitBlocked(seek, oldSeekToken);
                    seek.ContinueCommit.Set(); await seeking;
                    lateProvider.ContinueFormat.Set();
                    Require(!await lateBatch && !audio.IsPlaybackEpochCurrent(oldSeekToken),
                    "an overlay delayed across seek conversion leaked into the new source timeline");
                    Require(audio.TryPlayOverlay(new ConstantProvider(), audio.CapturePlaybackEpoch()),
                    "fresh key sound was rejected after seek commit");
                }
                finally { lateProvider.ContinueFormat.Set(); }

                var failing = Arm(fail: true); var failedRate = audio.SetPlaybackRateAsync(.75);
                CheckPreparationOverlays(failing); var failureToken = audio.CapturePlaybackEpoch();
                failing.ContinueOpen.Set(); CheckCommitBlocked(failing, failureToken); failing.ContinueCommit.Set();
                try { await failedRate; throw new Exception("synthetic commit failure was not propagated"); }
                catch (InvalidOperationException ex) when (ex.Message == "synthetic commit failure") { }
                Require(audio.IsPlaybackEpochCurrent(audio.CapturePlaybackEpoch()) &&
                audio.TryPlayOverlay(new ConstantProvider(), audio.CapturePlaybackEpoch()) && engine.IsPlaying,
                "failed commit left the old music clock permanently locked against overlays");
                Volatile.Write(ref current, null);

                var beforePause = audio.CapturePlaybackEpoch(); await audio.PauseAsync();
                Require(!audio.IsPlaybackEpochCurrent(beforePause) && !audio.TryPlayOverlay(new ConstantProvider(), audio.CapturePlaybackEpoch()),
                "paused transport accepted scheduled key sounds");
                await audio.PlayAsync(); Require(audio.TryPlayOverlay(new ConstantProvider(), audio.CapturePlaybackEpoch()), "resume rejected fresh scheduled key sounds");
                await audio.StopAsync(); Require(!audio.TryPlayOverlay(new ConstantProvider(), audio.CapturePlaybackEpoch()), "stopped transport accepted scheduled key sounds");
                Require(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(wave))) && (File.GetAttributes(wave) & FileAttributes.ReadOnly) != 0,
                "audio scheduling modified its read-only source fixture");
                Require(outputs.Outputs.All(o => o.ReadFailure == null), "native render callback failed during scheduling/commit barriers");
                Console.WriteLine("PASS 036 real native mixer: rate/seek preparation keeps music+keys running, no enqueue I/O, exact commit epoch barrier, late batch rejection, failed-commit recovery, pause/resume/stop, read-only source hash");
            }
            finally
            {
                foreach (var control in controls) { control.ContinueOpen.Set(); control.ContinueCommit.Set(); }
                Volatile.Write(ref current, null);
            }
        }
        finally
        {
            foreach (var control in controls) { control.ContinueOpen.Set(); control.ContinueCommit.Set(); }
            Volatile.Write(ref current, null); PlatformAudioOutputs.Current = previousOutputs;
            File.SetAttributes(wave, attributes);
        }
    }

    private sealed class Transition
    {
        public readonly ManualResetEventSlim OpenEntered = new(), ContinueOpen = new(), CommitEntered = new(), ContinueCommit = new();
        public int OpenSeen;
        public bool FailCommit;
    }
    private class ConstantProvider : ISampleProvider
    {
        public virtual WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
        public int Read(float[] buffer, int offset, int count) { for (var i = offset; i < offset + count; i++) buffer[i] = .025f; return count; }
    }
    private sealed class BarrierFormatProvider : ConstantProvider
    {
        public readonly ManualResetEventSlim FormatEntered = new(), ContinueFormat = new();
        public override WaveFormat WaveFormat
        { get { FormatEntered.Set(); Wait(ContinueFormat, "late batch format barrier timed out"); return base.WaveFormat; } }
    }
}
