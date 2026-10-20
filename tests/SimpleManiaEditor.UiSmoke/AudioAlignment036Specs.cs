using System.Diagnostics;
using System.Text.Json;
using LumaChart.Avalonia.Audio;
using LumaChart.Core.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

internal static class AudioAlignment036Specs
{
    public static void Measure(string root)
    {
        const int sr=48000; var path=Path.Combine(root,"alignment036.wav");
        using(var writer=new WaveFileWriter(path,WaveFormat.CreateIeeeFloatWaveFormat(sr,2)))
        {
            var samples=new float[sr*2*6];
            for(var f=0;f<sr*6;f++)
            {
                var t=f/(double)sr;
                var pulse=Enumerable.Range(0,12).Any(k=>t>=.1+k*.4 && t<.12+k*.4);
                samples[f*2]=(float)((pulse?.3:.012)*Math.Sin(2*Math.PI*1000*t));
            }
            writer.WriteSamples(samples,0,samples.Length);
        }
        var previous=PlatformAudioOutputs.Current;
        try
        {
            foreach(var rate in new[]{1d,.5,.25,.1})
            {
                var factory=new CaptureFactory(); PlatformAudioOutputs.Current=factory;
                using var engine=new WindowsAudioDeviceEngine();engine.PreservePitch=true;engine.PlaybackRate=rate;engine.Load(path);
                var document=new ChartDocument();
                for(var k=0;k<12;k++) document.Notes.Add(new ChartNote{Beat=BeatPosition.FromDouble((.1+k*.4)*2,1000)});
                var coordinator=new WindowsKeySoundPlaybackCoordinator(document,path,new HashSet<int>(),new(true,KeySoundPreset.Snare,null,false,0,0));
                coordinator.ResetCursor(0,rate);var epoch=new AudioPlaybackEpoch();
                engine.Play();var clock=Stopwatch.StartNew();
                while(clock.Elapsed.TotalSeconds<Math.Min(5.5,2/rate))
                {
                    foreach(var trigger in coordinator.TakeDue(engine.Position.TotalMilliseconds,rate,engine.OutputLatencyMilliseconds))
                    {
                        var voice=new SourceTimedSampleProvider(new RightPulse(sr),trigger.ScheduledSourceMilliseconds!.Value,trigger.DelayMilliseconds);
                        engine.TryPlayOverlay(voice,epoch.Capture());
                    }
                    Thread.Sleep(2);
                }
                engine.Pause();var pcm=factory.Samples.ToArray();
                double[] Attacks(int ch)=>Enumerable.Range(1,pcm.Length/2-1).Where(f=>Math.Abs(pcm[f*2+ch])>.12 && Math.Abs(pcm[(f-1)*2+ch])<=.12).Select(f=>f*1000d/sr)
                    .Aggregate(new List<double>(),(list,t)=>{if(list.Count==0||t-list[^1]>100)list.Add(t);return list;}).ToArray();
                var music = Attacks(0); var keys = Attacks(1);
                Require(music.Length >= 2 && keys.Length >= 2, "alignment fixture lost its attacks");
                for (var i = 0; i < Math.Min(music.Length, keys.Length); i++)
                    Require(Math.Abs(music[i] - keys[i]) < 2, $"{rate}x source/music and keys differ by {music[i]-keys[i]:F3}ms");
                Console.WriteLine(JsonSerializer.Serialize(new{Rate=rate,Output=factory.OutputName,Music=music,Keys=keys,Latency=engine.OutputLatencyMilliseconds,Probes=engine.TelemetryComProbeCount}));
            }
            CheckResampledKeyService(root, path);
        }
        finally{PlatformAudioOutputs.Current=previous;}
    }
    private static void CheckResampledKeyService(string root, string musicPath)
    {
        const int keyRate = 44100;
        var keyPath = Path.Combine(root, "right-key44100.wav");
        using (var writer = new WaveFileWriter(keyPath, WaveFormat.CreateIeeeFloatWaveFormat(keyRate, 2)))
        {
            var samples = new float[keyRate / 50 * 2];
            for (var f = 0; f < samples.Length / 2; f++)
                samples[f * 2 + 1] = (float)(.3 * Math.Sin(2 * Math.PI * 2000 * f / keyRate));
            writer.WriteSamples(samples, 0, samples.Length);
        }
        var factory = new CaptureFactory(); PlatformAudioOutputs.Current = factory;
        using var audio = new WindowsAudioPlaybackService(TimeSpan.Zero);
        using var keys = new WindowsKeySoundService();
        Require(keys.PreloadAsync(keyPath).GetAwaiter().GetResult(), "custom 44.1k key preload failed");
        audio.LoadAsync(musicPath).GetAwaiter().GetResult();
        audio.SetPreservePitchAsync(true).GetAwaiter().GetResult();
        audio.SetPlaybackRateAsync(.1).GetAwaiter().GetResult();
        audio.PlayAsync().GetAwaiter().GetResult();
        Require(keys.PlayThrough(audio, audio.CapturePlaybackEpoch(), KeySoundPreset.Custom, keyPath,
            scheduledSourceMilliseconds: 100), "key service rejected absolute-time custom key");
        Thread.Sleep(1400);
        audio.PauseAsync().GetAwaiter().GetResult();
        var pcm = factory.Samples.ToArray();
        double First(int channel) => Enumerable.Range(0, pcm.Length / 2)
            .First(f => Math.Abs(pcm[f * 2 + channel]) > .12) * 1000d / 48000;
        var musicOnset = First(0); var keyOnset = First(1);
        Require(Math.Abs(musicOnset-keyOnset) < 2, $"44.1k custom key resampling shifted alignment: music {musicOnset:F3}, key {keyOnset:F3}");
        Console.WriteLine($"PASS 036 full transport + custom key service 44.1k→48k at 0.1x: music {musicOnset:F3}ms / key {keyOnset:F3}ms");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class RightPulse(int sr):ISampleProvider
    {
        private int frame;
        public WaveFormat WaveFormat{get;}=WaveFormat.CreateIeeeFloatWaveFormat(sr,2);
        public int Read(float[] b,int o,int c)
        {
            var frames=Math.Min(c/2,sr/50-frame);if(frames<=0)return 0;
            for(var f=0;f<frames;f++){b[o+f*2]=0;b[o+f*2+1]=(float)(.3*Math.Sin(2*Math.PI*2000*(frame+f)/sr));}frame+=frames;return frames*2;
        }
    }
    private sealed class CaptureFactory:IPlatformAudioOutputFactory
    {
        public List<float> Samples{get;}=[];public string? OutputName;
        public IWavePlayer CreateOutput(IWaveProvider provider,PlatformAudioOutputRequest request)
        {var output=new WindowsAudioOutputFactory().CreateOutput(new Tap(provider,Samples),request);OutputName=output.GetType().Name;return output;}
    }
    private sealed class Tap(IWaveProvider source,List<float> target):IWaveProvider
    {
        public WaveFormat WaveFormat=>source.WaveFormat;
        public int Read(byte[] b,int o,int c)
        {var n=source.Read(b,o,c);var values=new float[n/4];Buffer.BlockCopy(b,o,values,0,n);target.AddRange(values);return n;}
    }
}
