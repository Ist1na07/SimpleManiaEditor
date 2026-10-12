using LumaChart.Application.Audio;

namespace LumaChart.Specs;

internal static class TempoOffsetBiasSpecs
{
    public static void Run()
    {
        foreach (var rate in new[] { 44100, 48000 })
        foreach (var offset in new[] { 237d, 475d })
        {
            var energies = new List<double>(); var reducer = new ExactRateEnergyReducer(rate, 200);
            for (var sample = 0; sample < rate * 24; sample++)
            {
                var time = sample * 1000d / rate; var age = (time - offset) % 500;
                var value = time < offset || age < 0 || age > 80 ? 0 : Math.Sin(2 * Math.PI * 1000 * sample / rate) * Math.Exp(-age / 20);
                if (reducer.AddSample(value, out var energy)) energies.Add(energy);
            }
            var detected = BeatAnalysisEngine.Analyze(BeatAnalysisEngine.CreateSignal(energies));
            var error = -detected.OffsetMilliseconds - offset;
            if (Math.Abs(detected.Bpm - 120) > .01 || Math.Abs(error) > 5.01)
                throw new InvalidOperationException($"PCM offset bias: {rate}Hz, expected {offset}ms, error {error}ms");
        }
    }
}
