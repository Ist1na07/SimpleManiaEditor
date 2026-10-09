using System.Text.Json;

namespace SimpleManiaEditor.PlaytestBridge;

/// <summary>Versioned local handoff. No account credentials, and no writes to Songs.</summary>
public sealed record PlaytestRequest
{
    public int ProtocolVersion { get; init; } = 1;
    public string BeatmapArchive { get; init; } = "";
    public string ChartEntry { get; init; } = "";
    public string DataRoot { get; init; } = "";
    public double StartMilliseconds { get; init; }
    public double Rate { get; init; } = 1;
    public bool PreservePitch { get; init; } = true;
    public bool AutoPlay { get; init; }
    public double ManiaSpeed { get; init; } = 20;
    public bool UpsideDown { get; init; }
    public double InputOffsetMilliseconds { get; init; }
    public bool FullScreen { get; init; } = true;
    public double Volume { get; init; } = 1;
    public string SelectedSkinPath { get; init; } = "";
    public string[] SkinPaths { get; init; } = [];
    public Dictionary<int, string[]> KeyBindings { get; init; } = [];
    public string SettingsImportRevision { get; init; } = "";
    public bool SettingsOnly { get; init; }
    public bool WarmupOnly { get; init; }

    public static PlaytestRequest Read(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("试玩请求过大。");
        var request = JsonSerializer.Deserialize<PlaytestRequest>(File.ReadAllText(path)) ?? throw new InvalidDataException("试玩请求为空。");
        if (request.ProtocolVersion != 1) throw new InvalidDataException("试玩协议版本不兼容。");
        if (!request.SettingsOnly && !File.Exists(request.BeatmapArchive)) throw new FileNotFoundException("试玩工作副本不存在。", request.BeatmapArchive);
        if (!Path.IsPathFullyQualified(request.DataRoot)) throw new InvalidDataException("试玩数据目录必须是绝对路径。");
        if (!double.IsFinite(request.StartMilliseconds) || request.StartMilliseconds < 0 ||
            !double.IsFinite(request.Rate) || request.Rate < .1 || request.Rate > 2)
            throw new InvalidDataException("试玩时间或倍速无效。");
        return request;
    }

    public void Write(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}
