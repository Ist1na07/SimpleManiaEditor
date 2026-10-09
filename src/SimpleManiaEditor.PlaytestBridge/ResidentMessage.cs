using System.Text.Json;

namespace SimpleManiaEditor.PlaytestBridge;

public sealed record ResidentMessage(string Kind, string Path = "", double Milliseconds = 0, string Detail = "")
{
    public string ToJson() => JsonSerializer.Serialize(this);
    public static ResidentMessage? Parse(string line)
    {
        try { return JsonSerializer.Deserialize<ResidentMessage>(line); }
        catch (JsonException) { return null; }
    }
}
