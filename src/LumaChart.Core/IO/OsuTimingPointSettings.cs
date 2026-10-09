using System.Globalization;
using System.Text.Json.Nodes;
using LumaChart.Core.Models;

namespace LumaChart.Core.IO;

/// <summary>Native timing attributes retained in recovery data, never emitted as osu extensions.</summary>
public sealed record OsuTimingPointSettings(int Meter = 4, int SampleSet = 0, int SampleIndex = 0, int Volume = 100, int Effects = 0)
{
    public const string Key = "_sme_osu_timing_settings";
    public bool Kiai => (Effects & 1) != 0;
    public static OsuTimingPointSettings Read(TimingEvent timing)
    {
        if (timing.UnknownData?[Key] is JsonObject data)
            return new(Read(data, "meter", 4), Read(data, "sampleSet", 0), Read(data, "sampleIndex", 0), Read(data, "volume", 100), Read(data, "effects", 0));
        var parts = timing.UnknownData?["_sme_osu_object"]?["line"]?.GetValue<string>().Split(',');
        int Part(int index, int fallback) => parts != null && parts.Length > index && int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        return new(Part(2, 4), Part(3, 0), Part(4, 0), Part(5, 100), Part(7, 0));
    }
    private static int Read(JsonObject data, string name, int fallback) => data[name]?.GetValue<int>() ?? fallback;
    public void Apply(TimingEvent timing)
    {
        if (Meter is < 1 or > 32 || SampleSet is < 0 or > 3 || SampleIndex < 0 || Volume is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(timing), "Timing 属性超出有效范围。");
        timing.UnknownData ??= new();
        timing.UnknownData[Key] = new JsonObject { ["meter"] = Meter, ["sampleSet"] = SampleSet,
            ["sampleIndex"] = SampleIndex, ["volume"] = Volume, ["effects"] = Effects };
    }
    public static void WriteFields(TimingEvent timing, string[] fields)
    {
        if (timing.UnknownData?[Key] == null) return;
        var value = Read(timing);
        fields[2] = value.Meter.ToString(CultureInfo.InvariantCulture);
        fields[3] = value.SampleSet.ToString(CultureInfo.InvariantCulture);
        fields[4] = value.SampleIndex.ToString(CultureInfo.InvariantCulture);
        fields[5] = value.Volume.ToString(CultureInfo.InvariantCulture);
        fields[7] = value.Effects.ToString(CultureInfo.InvariantCulture);
    }
}
