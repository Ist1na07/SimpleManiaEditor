using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LumaChart.Avalonia.Audio;

/// <summary>
/// What the caller needs from a sink. The music transport favours a stable
/// render at the configured latency; the standalone Key-sound output favours
/// the shortest path to audible feedback and accepts a shallower buffer.
/// </summary>
/// <param name="FallbackLatencyMilliseconds">
/// Latency to ask a fallback sink for when it cannot honour the primary
/// request. A shallow WASAPI request is not safe to repeat verbatim on the
/// older WaveOut path, so the caller states the safer figure instead of the
/// sink guessing one. Null means reuse
/// <paramref name="RequestedLatencyMilliseconds"/>.
/// </param>
public readonly record struct PlatformAudioOutputRequest(
    double RequestedLatencyMilliseconds,
    bool PrioritizeLatencyOverStability,
    double? FallbackLatencyMilliseconds = null);

/// <summary>
/// Platform output boundary for the audio stack. The shared decode, rate,
/// pitch and overlay-mixing chain always ends in an <see cref="IWaveProvider"/>;
/// only the sink that consumes it is platform code. Windows renders through
/// WASAPI, Android through AudioTrack, and neither side needs a second copy of
/// the mixing pipeline.
/// </summary>
public interface IPlatformAudioOutputFactory
{
    /// <summary>
    /// Creates a sink already initialized with <paramref name="provider"/> and
    /// ready for <see cref="IWavePlayer.Play"/>.
    /// </summary>
    IWavePlayer CreateOutput(IWaveProvider provider, PlatformAudioOutputRequest request);
}

public static class PlatformAudioOutputs
{
    private static IPlatformAudioOutputFactory? current;

    /// <summary>
    /// Set once by the host during startup. Windows registers the WASAPI
    /// factory and Android the AudioTrack factory. Leaving it unset is a host
    /// wiring bug rather than something to paper over with a desktop default,
    /// because the desktop sink fails on Android with an opaque winmm error.
    /// </summary>
    public static IPlatformAudioOutputFactory Current
    {
        get => current ?? throw new InvalidOperationException(
            "音频输出平台适配器尚未注册。");
        set => current = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static IWavePlayer CreateOutput(
        IWaveProvider provider,
        PlatformAudioOutputRequest request) =>
        Current.CreateOutput(provider, request);
}

/// <summary>
/// Windows sink. Shared-mode WASAPI is preferred for its low latency and audio
/// clock; WaveOut remains the fallback for sessions where WASAPI cannot open.
/// </summary>
public sealed class WindowsAudioOutputFactory : IPlatformAudioOutputFactory
{
    public IWavePlayer CreateOutput(
        IWaveProvider provider,
        PlatformAudioOutputRequest request)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var latency = (int)Math.Round(request.RequestedLatencyMilliseconds);
        IWavePlayer? wasapi = null;
        MMDevice? endpoint = null;
        try
        {
            // The endpoint is resolved here rather than by the WasapiOut
            // default constructor, which leaves its enumerator and device to
            // the finalizer. Every seek builds a fresh output, so those piled
            // up between collections.
            using (var enumerator = new MMDeviceEnumerator())
                endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            var owned = new EndpointOwningWasapiOut(endpoint, latency);
            endpoint = null;
            wasapi = owned;
            owned.Init(provider);
            return owned;
        }
        catch
        {
            try { wasapi?.Dispose(); } catch { }
            try { endpoint?.Dispose(); } catch { }
            WaveOutEvent? fallback = null;
            try
            {
                fallback = new WaveOutEvent
                {
                    DesiredLatency = (int)Math.Round(
                        request.FallbackLatencyMilliseconds
                        ?? request.RequestedLatencyMilliseconds),
                    NumberOfBuffers = request.PrioritizeLatencyOverStability ? 2 : 3
                };
                fallback.Init(provider);
                return fallback;
            }
            catch
            {
                try { fallback?.Dispose(); } catch { }
                throw;
            }
        }
    }

    /// <summary>
    /// A shared-mode <see cref="WasapiOut"/> that releases what NAudio leaves to
    /// the finalizer: the endpoint it was created for, and the frame event
    /// Init allocates but Dispose never closes. It stays a WasapiOut, so the
    /// engine's audio-clock and padding probes keep working on it. The
    /// release goes through <see cref="IWavePlayer"/>, which is how every
    /// owner of an output disposes it.
    /// </summary>
    private sealed class EndpointOwningWasapiOut : WasapiOut, IDisposable
    {
        private static readonly FieldInfo? FrameEventField = typeof(WasapiOut).GetField(
            "frameEventWaitHandle",
            BindingFlags.Instance | BindingFlags.NonPublic);
        // MMDevice.Dispose releases only the sub-objects it cached, never the
        // IMMDevice itself. One ReleaseComObject balances the one reference
        // this output's endpoint lookup brought into the runtime.
        private static readonly FieldInfo? EndpointInterfaceField = typeof(MMDevice).GetField(
            "deviceInterface",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private readonly MMDevice endpoint;
        private int released;

        public EndpointOwningWasapiOut(MMDevice endpoint, int latency)
            : base(endpoint, AudioClientShareMode.Shared, true, latency)
        {
            this.endpoint = endpoint;
        }

        void IDisposable.Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            try
            {
                // Stops and joins the render thread before the client goes, so
                // nothing waits on the event once it is closed below.
                Dispose();
            }
            finally
            {
                try { (FrameEventField?.GetValue(this) as IDisposable)?.Dispose(); } catch { }
                try { endpoint.Dispose(); } catch { }
                try
                {
                    if (EndpointInterfaceField?.GetValue(endpoint) is { } endpointInterface &&
                        Marshal.IsComObject(endpointInterface))
                        Marshal.ReleaseComObject(endpointInterface);
                }
                catch
                {
                    // A removed endpoint can reject its final release.
                }
            }
        }
    }
}
