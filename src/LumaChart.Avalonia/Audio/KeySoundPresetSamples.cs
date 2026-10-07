namespace LumaChart.Avalonia.Audio;

public static class KeySoundPresetSamples
{
    public const int SampleRate = 44_100;
    public const int Channels = 2;

    public static float[] Create(KeySoundPreset preset) => preset switch
    {
        KeySoundPreset.Snare => CreateSnare(),
        KeySoundPreset.BrightTick => CreateTambourine(),
        KeySoundPreset.HandDrum => CreateWoodTap(),
        KeySoundPreset.HandDrum2 => CreateClap(),
        KeySoundPreset.DeepPulse => CreateKick(),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null)
    };

    // All presets are deterministic, original procedural synthesis. No recorded
    // source sample is embedded or reconstructed here.
    private static float[] CreateSnare()
    {
        var frames = (int)(SampleRate * .125);
        var data = new float[frames * Channels];
        var random = new Random(0x000C2515);
        var lowNoise = 0d;
        var bandNoise = 0d;
        var bandNoiseStageTwo = 0d;
        var lowAlpha = 1 - Math.Exp(-2 * Math.PI * 1_500 / SampleRate);
        var highAlpha = 1 - Math.Exp(-2 * Math.PI * 7_500 / SampleRate);
        var bodyPhase = 0d;
        for (var frame = 0; frame < frames; frame++)
        {
            var time = frame / (double)SampleRate;
            var attack = Math.Min(1, time * 5_000);
            var wireEnvelope = attack * Math.Exp(-time * 38);
            var snapEnvelope = attack * Math.Exp(-time * 125);
            var bodyEnvelope = attack * Math.Exp(-time * 52);
            var bodyFrequency = 178 + 42 * Math.Exp(-time * 42);
            bodyPhase += 2 * Math.PI * bodyFrequency / SampleRate;
            var noise = random.NextDouble() * 2 - 1;
            lowNoise += lowAlpha * (noise - lowNoise);
            var highPassedNoise = noise - lowNoise;
            bandNoise += highAlpha * (highPassedNoise - bandNoise);
            bandNoiseStageTwo += highAlpha * (bandNoise - bandNoiseStageTwo);
            var sample = (float)Math.Clamp(
                bandNoiseStageTwo * (wireEnvelope * .52 + snapEnvelope * .18) +
                Math.Sin(bodyPhase) * bodyEnvelope * .075,
                -.94,
                .94);
            data[frame * Channels] = sample;
            data[frame * Channels + 1] = sample * .985f;
        }
        return data;
    }

    private static float[] CreateTambourine()
    {
        var frames = (int)(SampleRate * .13);
        var data = new float[frames * Channels];
        var random = new Random(0x000B6177);
        var lowNoise = 0d;
        var bandNoise = 0d;
        var phases = new double[5];
        var frequencies = new[] { 5_200d, 6_500d, 7_800d, 9_200d, 10_800d };
        var gains = new[] { .34d, .27d, .20d, .13d, .06d };
        var noiseLowAlpha = 1 - Math.Exp(-2 * Math.PI * 3_600 / SampleRate);
        var noiseHighAlpha = 1 - Math.Exp(-2 * Math.PI * 9_000 / SampleRate);
        for (var frame = 0; frame < frames; frame++)
        {
            var time = frame / (double)SampleRate;
            var attack = Math.Min(1, time * 4_800);
            var toneEnvelope = attack * Math.Exp(-time * 34) *
                (.88 + Math.Sin(2 * Math.PI * 58 * time) * .12);
            var noiseEnvelope = attack * Math.Exp(-time * 82);
            var tone = 0d;
            for (var mode = 0; mode < phases.Length; mode++)
            {
                phases[mode] += 2 * Math.PI *
                    (frequencies[mode] + mode * 31 * Math.Exp(-time * 36)) / SampleRate;
                tone += Math.Sin(phases[mode] + mode * .61) * gains[mode];
            }
            var noise = random.NextDouble() * 2 - 1;
            lowNoise += noiseLowAlpha * (noise - lowNoise);
            var highPassedNoise = noise - lowNoise;
            bandNoise += noiseHighAlpha * (highPassedNoise - bandNoise);
            var sample = (float)Math.Clamp(
                tone * toneEnvelope * .50 + bandNoise * noiseEnvelope * .075,
                -.94,
                .94);
            data[frame * Channels] = sample;
            data[frame * Channels + 1] = sample * .98f;
        }
        return data;
    }

    private static float[] CreateWoodTap()
    {
        var frames = (int)(SampleRate * .13);
        var data = new float[frames * Channels];
        var random = new Random(0x0000700D);
        var smoothedNoise = 0d;
        var phases = new double[4];
        var frequencies = new[] { 640d, 970d, 1_375d, 1_970d };
        var gains = new[] { .55d, .26d, .13d, .06d };
        for (var frame = 0; frame < frames; frame++)
        {
            var time = frame / (double)SampleRate;
            var attack = Math.Min(1, time * 2_200);
            var bodyEnvelope = attack * Math.Exp(-time * 48);
            var clickEnvelope = attack * Math.Exp(-time * 95);
            var body = 0d;
            for (var mode = 0; mode < phases.Length; mode++)
            {
                phases[mode] += 2 * Math.PI *
                    (frequencies[mode] + 35 * Math.Exp(-time * 35)) / SampleRate;
                body += Math.Sin(phases[mode] + mode * .43) * gains[mode];
            }
            var noise = random.NextDouble() * 2 - 1;
            smoothedNoise = smoothedNoise * .72 + noise * .28;
            var sample = (float)Math.Clamp(
                body * bodyEnvelope * .66 + smoothedNoise * clickEnvelope * .13,
                -.94,
                .94);
            data[frame * Channels] = sample;
            data[frame * Channels + 1] = sample * .99f;
        }
        return data;
    }

    private static float[] CreateClap()
    {
        var frames = (int)(SampleRate * .13);
        var data = new float[frames * Channels];
        var random = new Random(0x000D1617);
        var lowNoise = 0d;
        var highNoise = 0d;
        var highNoiseStageTwo = 0d;
        var lowAlpha = 1 - Math.Exp(-2 * Math.PI * 450 / SampleRate);
        var highAlpha = 1 - Math.Exp(-2 * Math.PI * 4_200 / SampleRate);
        for (var frame = 0; frame < frames; frame++)
        {
            var time = frame / (double)SampleRate;
            var firstBurst = BurstEnvelope(time, 0, 185, 7_000);
            var secondBurst = BurstEnvelope(time, .009, 195, 7_200) * .88;
            var thirdBurst = BurstEnvelope(time, .019, 205, 7_400) * .70;
            var fourthBurst = BurstEnvelope(time, .030, 215, 7_600) * .48;
            var tailTime = time - .019;
            var tailEnvelope = tailTime > 0
                ? Math.Exp(-tailTime * 56) * .16
                : 0;
            var noise = random.NextDouble() * 2 - 1;
            lowNoise += lowAlpha * (noise - lowNoise);
            highNoise += highAlpha * (noise - highNoise);
            highNoiseStageTwo += highAlpha * (highNoise - highNoiseStageTwo);
            var bandNoise = highNoiseStageTwo - lowNoise;
            var sample = (float)Math.Clamp(
                bandNoise * (firstBurst * 1.08 + secondBurst * .95 +
                    thirdBurst * .83 + fourthBurst * .65 + tailEnvelope * .75),
                -.94,
                .94);
            data[frame * Channels] = sample;
            data[frame * Channels + 1] = sample * .975f;
        }
        return data;
    }

    private static float[] CreateKick()
    {
        var frames = (int)(SampleRate * .145);
        var data = new float[frames * Channels];
        var random = new Random(0x0000DEE9);
        var clickNoise = 0d;
        var fundamentalPhase = 0d;
        var secondPhase = 0d;
        for (var frame = 0; frame < frames; frame++)
        {
            var time = frame / (double)SampleRate;
            var attack = Math.Min(1, time * 3_200);
            var bodyEnvelope = attack * Math.Exp(-time * 26);
            var transientEnvelope = attack * Math.Exp(-time * 240);
            var frequency = 52 + 108 * Math.Exp(-time * 39);
            fundamentalPhase += 2 * Math.PI * frequency / SampleRate;
            secondPhase += 2 * Math.PI * frequency * 2.01 / SampleRate;
            var body = Math.Sin(fundamentalPhase) * .91 +
                Math.Sin(secondPhase + .24) * .09;
            var noise = random.NextDouble() * 2 - 1;
            clickNoise = clickNoise * .58 + noise * .42;
            var sample = (float)Math.Clamp(
                body * bodyEnvelope * .72 + clickNoise * transientEnvelope * .025,
                -.94,
                .94);
            data[frame * Channels] = sample;
            data[frame * Channels + 1] = sample * .99f;
        }
        return data;
    }

    private static double BurstEnvelope(
        double time,
        double delay,
        double decay,
        double attackRate)
    {
        var localTime = time - delay;
        return localTime <= 0
            ? 0
            : Math.Min(1, localTime * attackRate) * Math.Exp(-localTime * decay);
    }
}
