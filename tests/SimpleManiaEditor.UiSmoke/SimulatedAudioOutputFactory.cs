using System.Collections.Concurrent;
using LumaChart.Avalonia.Audio;
using NAudio.Wave;

/// <summary>Consumes the real decoder/DSP chain without opening a physical output device.</summary>
internal sealed class SimulatedAudioOutputFactory : IPlatformAudioOutputFactory
{
    public ConcurrentQueue<Output> Outputs { get; } = new();
    public IWavePlayer CreateOutput(IWaveProvider provider, PlatformAudioOutputRequest request)
    {
        var output = new Output(provider);
        Outputs.Enqueue(output);
        return output;
    }
    public sealed class Output : IWavePlayer
    {
        private readonly IWaveProvider provider;
        private readonly CancellationTokenSource lifetime = new();
        private int state;
        private int pauses;
        private long bytesRead;
        public Exception? ReadFailure { get; private set; }
        public Output(IWaveProvider provider)
        {
            this.provider = provider;
            _ = Task.Run(ConsumeAsync);
        }
        public WaveFormat Format => provider.WaveFormat;
        public WaveFormat OutputWaveFormat => provider.WaveFormat;
        public int PauseCalls => Volatile.Read(ref pauses);
        public long BytesRead => Interlocked.Read(ref bytesRead);
        public PlaybackState PlaybackState => (PlaybackState)Volatile.Read(ref state);
        public float Volume { get; set; } = 1;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public void Init(IWaveProvider waveProvider) => throw new NotSupportedException("The host supplies an initialized provider.");
        public void Play() => Volatile.Write(ref state, (int)PlaybackState.Playing);
        public void Pause() { Interlocked.Increment(ref pauses); Volatile.Write(ref state, (int)PlaybackState.Paused); }
        public void Stop() { Volatile.Write(ref state, (int)PlaybackState.Stopped); PlaybackStopped?.Invoke(this, new StoppedEventArgs()); }
        public void Dispose() { lifetime.Cancel(); }
        private async Task ConsumeAsync()
        {
            var bytes = Math.Max(provider.WaveFormat.BlockAlign,
                provider.WaveFormat.AverageBytesPerSecond / 200 / provider.WaveFormat.BlockAlign * provider.WaveFormat.BlockAlign);
            var buffer = new byte[bytes];
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    if (PlaybackState == PlaybackState.Playing)
                    {
                        var read = provider.Read(buffer, 0, buffer.Length);
                        Interlocked.Add(ref bytesRead, read);
                        if (read == 0) Stop();
                    }
                    await Task.Delay(5, lifetime.Token);
                }
            }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
            catch (Exception exception)
            {
                ReadFailure = exception;
                Volatile.Write(ref state, (int)PlaybackState.Stopped);
                PlaybackStopped?.Invoke(this, new StoppedEventArgs(exception));
            }
        }
    }
}
