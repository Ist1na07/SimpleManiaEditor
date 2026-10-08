using System.IO.Compression;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using SkiaSharp;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>Immutable, owned texture bank. Decode once off the input/render path.</summary>
internal sealed class OsuManiaSkin : IDisposable
{
    private readonly Dictionary<string, SkinAnimation?> textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Bitmap> owned = [];
    private readonly Dictionary<string, IBrush> colours = new(StringComparer.OrdinalIgnoreCase);
    private long pixelBudget;
    public string Root { get; }
    public string Name { get; }
    public OsuIniDocument.Section Config { get; }
    public OsuIniDocument.Section Fonts { get; }
    public string[] Warnings => warnings.ToArray();
    private readonly List<string> warnings = [];
    public double[] Widths { get; }
    public double[] Spacing { get; }
    public double[] LineWidths { get; }
    public IBrush[] LaneColours { get; }
    public SkinAnimation?[] Notes { get; }
    public SkinAnimation?[] Heads { get; }
    public SkinAnimation?[] Bodies { get; }
    public SkinAnimation?[] Tails { get; }
    public SkinAnimation?[] Keys { get; }
    public SkinAnimation?[] PressedKeys { get; }
    public SkinAnimation?[] StageLights { get; }
    public double HeightScaleWidth { get; }
    public bool UpsideDown => Config.Flag("UpsideDown", false);
    public double HitPosition => Math.Clamp(Config.Number("HitPosition", 402), 1, 479);
    public double ComboPosition => Math.Clamp(Config.Number("ComboPosition", 111), 0, 480);
    public double ScorePosition => Math.Clamp(Config.Number("ScorePosition", 300), 0, 480);
    public double ColumnStart => Math.Clamp(Config.Number("ColumnStart", 136), 0, 1500);
    public bool KeysUnderNotes => Config.Flag("KeysUnderNotes", false);
    public bool SplitStages { get; }
    public double StageSeparation => Math.Clamp(Config.Number("StageSeparation", 40), 0, 300);
    public string? HitSoundPath { get; }
    public double AnimationFps { get; }
    public bool ModernSkin { get; }

    private OsuManiaSkin(string root, int count)
    {
        Root = Path.GetFullPath(root);
        var iniPath = Path.Combine(Root, "skin.ini");
        if (File.Exists(iniPath) && new FileInfo(iniPath).Length > 2 * 1024 * 1024) throw new InvalidDataException("skin.ini 过大。");
        var ini = new OsuIniDocument(File.Exists(iniPath) ? File.ReadAllText(iniPath) : "");
        Config = ini.Mania(count);
        Fonts = ini.Find("Fonts") ?? new OsuIniDocument.Section("Fonts");
        var general = ini.Find("General");
        Name = general?.Text("Name", Path.GetFileName(Root)) ?? Path.GetFileName(Root);
        ModernSkin = general == null || general.Text("Version") == "latest" || general.Number("Version", 1) >= 2.5;
        AnimationFps = Math.Clamp(general?.Number("AnimationFramerate", 60) ?? 60, -1, 240);
        SplitStages = count > 1 && Config.Flag("SplitStages", count > 9);
        Widths = Config.Numbers("ColumnWidth", count, 30, 1, 200);
        Spacing = Config.Numbers("ColumnSpacing", count, 0, 0, 100);
        LineWidths = Config.Numbers("ColumnLineWidth", count + 1, 2, 0, 20);
        HeightScaleWidth = Math.Clamp(Config.Number("WidthForNoteHeightScale", Widths.Min()), 1, 200);
        LaneColours = Enumerable.Range(1, count).Select(i => Colour("Colour" + i, Colors.Black)).ToArray();
        Notes = new SkinAnimation?[count]; Heads = new SkinAnimation?[count]; Bodies = new SkinAnimation?[count]; Tails = new SkinAnimation?[count];
        Keys = new SkinAnimation?[count]; PressedKeys = new SkinAnimation?[count];
        for (var lane = 0; lane < count; lane++)
        {
            var kind = DefaultKind(count, lane, (int)Config.Number("SpecialStyle", 0));
            Notes[lane] = Load(Config.Text($"NoteImage{lane}", $"mania-note{kind}"));
            Heads[lane] = Load(Config.Text($"NoteImage{lane}H", $"mania-note{kind}H")) ?? Notes[lane];
            Bodies[lane] = Load(Config.Text($"NoteImage{lane}L", $"mania-note{kind}L"));
            Tails[lane] = Load(Config.Text($"NoteImage{lane}T", $"mania-note{kind}T")) ?? Heads[lane];
            Keys[lane] = Load(Config.Text($"KeyImage{lane}", $"mania-key{kind}"));
            PressedKeys[lane] = Load(Config.Text($"KeyImage{lane}D", $"mania-key{kind}D")) ?? Keys[lane];
        }
        foreach (var (key, fallback) in new[] { ("StageLeft","mania-stage-left"), ("StageRight","mania-stage-right"), ("StageBottom","mania-stage-bottom"),
            ("StageHint","mania-stage-hint"), ("StageLight","mania-stage-light"), ("LightingN","lightingN"), ("LightingL","lightingL"), ("WarningArrow","mania-warningarrow"),
            ("Hit0","mania-hit0"), ("Hit50","mania-hit50"), ("Hit100","mania-hit100"), ("Hit200","mania-hit200"), ("Hit300","mania-hit300"), ("Hit300g","mania-hit300g") })
            Load(Config.Text(key, fallback));
        foreach (var name in new[] { "comboburst-mania", "scorebar-bg", "scorebar-colour", "scorebar-marker", "pause-overlay", "pause-continue", "pause-retry", "pause-back", "play-skip" }) Load(name);
        foreach (var prefix in new[] { Fonts.Text("ScorePrefix", "score"), Fonts.Text("ComboPrefix", "score") }.Distinct())
            foreach (var glyph in new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "dot", "percent", "x" }) Load(prefix + "-" + glyph);
        var stageLight = Texture("StageLight", "mania-stage-light");
        StageLights = Enumerable.Range(1, count).Select(i => Tint(stageLight, ((ISolidColorBrush)Colour("ColourLight" + i, Color.FromRgb(55, 255, 255))).Color)).ToArray();
        HitSoundPath = new[] { "normal-hitnormal.wav", "normal-hitnormal.ogg", "normal-hitnormal.mp3" }.Select(SafePath).FirstOrDefault(p => p != null && File.Exists(p));
        if (Config.Values.Count == 0) warnings.Add($"没有 {count}K 的 [Mania] 配置，使用默认布局和可用素材。");
    }

    public static Task<OsuManiaSkin> LoadAsync(string path, int count, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("皮肤目录不存在。");
        var result = new OsuManiaSkin(path, count);
        if (token.IsCancellationRequested) { result.Dispose(); token.ThrowIfCancellationRequested(); }
        return result;
    }, token);

    public SkinAnimation? Texture(string key, string fallback) => textures.GetValueOrDefault(Config.Text(key, fallback));
    public SkinAnimation? Texture(string name) => textures.GetValueOrDefault(name);
    public IBrush Colour(string key, Color fallback)
    {
        if (colours.TryGetValue(key, out var existing)) return existing;
        var parts = Config.Text(key).Split(','); var bytes = new List<byte>();
        foreach (var part in parts) { if (!byte.TryParse(part.Trim(), out var b)) return colours[key] = new ImmutableSolidColorBrush(fallback); bytes.Add(b); }
        return colours[key] = bytes.Count is 3 or 4 ? new ImmutableSolidColorBrush(Color.FromArgb(bytes.Count == 4 ? bytes[3] : (byte)255, bytes[0], bytes[1], bytes[2])) : new ImmutableSolidColorBrush(fallback);
    }
    public bool Flip(string key, int lane, string suffix, bool fallback) => ModernSkin && Config.Flag($"{key}{lane}{suffix}", Config.Flag(key + suffix, Config.Flag(key, fallback)));

    public static string DefaultKind(int count, int lane, int specialStyle = 0)
    {
        if (count % 2 == 1 && lane == count / 2 || count > 4 && count % 2 == 0 && (specialStyle == 1 && lane == 0 || specialStyle == 2 && lane == count - 1)) return "S";
        var mirroredLane = Math.Min(lane, count - 1 - lane);
        return mirroredLane % 2 == 0 ? "1" : "2";
    }

    private SkinAnimation? Load(string name)
    {
        name = name.Trim();
        if (textures.TryGetValue(name, out var existing)) return existing;
        var frames = new List<Bitmap>(); var density = 1;
        for (var i = 0; i < 120; i++)
        {
            var file = ImagePath(name + "-" + i, out var d);
            if (file == null) break;
            var image = Decode(file); if (image == null) break;
            density = d; frames.Add(image);
        }
        if (frames.Count == 0)
        {
            var file = ImagePath(name, out density); if (file != null && Decode(file) is { } bitmap) frames.Add(bitmap);
        }
        var result = frames.Count > 0 ? new SkinAnimation(frames.ToArray(), density) : null;
        textures[name] = result; return result;
    }
    private string? ImagePath(string name, out int density)
    {
        if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        else if (name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
        foreach (var (suffix, d) in new[] { ("@2x.png",2), (".png",1), ("@2x.jpg",2), (".jpg",1), ("@2x.jpeg",2), (".jpeg",1) })
        { var path = SafePath(name + suffix); if (path != null && File.Exists(path)) { density = d; return path; } }
        density = 1; return null;
    }
    private Bitmap? Decode(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("图像文件过大");
            using var data = SKData.Create(path); using var codec = SKCodec.Create(data);
            if (codec == null || codec.Info.Width is <= 0 or > 8192 || codec.Info.Height is <= 0 or > 8192 ||
                pixelBudget + (long)codec.Info.Width * codec.Info.Height > 32 * 1024 * 1024) throw new InvalidDataException("图像解码预算超限");
            var bitmap = new Bitmap(path); pixelBudget += (long)codec.Info.Width * codec.Info.Height; owned.Add(bitmap); return bitmap;
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException)
        { warnings.Add($"忽略图像 {Path.GetFileName(path)}：{e.Message}"); return null; }
    }
    private SkinAnimation? Tint(SkinAnimation? source, Color colour)
    {
        if (source == null || colour == Colors.White) return source;
        var key = "tint:" + Config.Text("StageLight", "mania-stage-light") + ":" + colour;
        if (textures.TryGetValue(key, out var cached)) return cached;
        var pixels = source.Frames.Sum(f => (long)f.PixelSize.Width * f.PixelSize.Height);
        if (pixelBudget + pixels > 32 * 1024 * 1024) { warnings.Add("列光颜色纹理超出预算，保留原素材。"); return source; }
        var frames = new List<Bitmap>();
        foreach (var frame in source.Frames)
        {
            using var encoded = new MemoryStream(); frame.Save(encoded, PngBitmapEncoderOptions.Default); encoded.Position = 0;
            using var decoded = SKBitmap.Decode(encoded); using var target = new SKBitmap(decoded.Width, decoded.Height);
            using (var canvas = new SKCanvas(target))
            using (var paint = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(new SKColor(colour.R, colour.G, colour.B, colour.A), SKBlendMode.Modulate) })
                canvas.DrawBitmap(decoded, 0, 0, paint);
            using var png = target.Encode(SKEncodedImageFormat.Png, 100); using var buffer = new MemoryStream(); png.SaveTo(buffer); buffer.Position = 0;
            var bitmap = new Bitmap(buffer); owned.Add(bitmap); frames.Add(bitmap);
        }
        pixelBudget += pixels;
        return textures[key] = new SkinAnimation(frames.ToArray(), source.Density);
    }
    private string? SafePath(string name)
    {
        try
        {
            var candidate = Path.GetFullPath(Path.Combine(Root, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            var current = new FileInfo(candidate) as FileSystemInfo;
            while (current != null && !current.FullName.Equals(Root, StringComparison.OrdinalIgnoreCase))
            { if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) return null; current = current is FileInfo f ? f.Directory : ((DirectoryInfo)current).Parent; }
            return candidate;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return null; }
    }
    public void Dispose() { foreach (var image in owned) image.Dispose(); owned.Clear(); textures.Clear(); }

    public static Task<string> ImportArchiveAsync(string path, CancellationToken token) => Task.Run(() =>
    {
        var root = Path.Combine(AvaloniaAppPaths.LocalDataRoot, "skins"); Directory.CreateDirectory(root);
        var staging = Path.Combine(root, "import-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count > 20000) throw new InvalidDataException("皮肤文件数量过多。");
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                total += entry.Length;
                if (total > 512L * 1024 * 1024 || entry.Length > 32L * 1024 * 1024) throw new InvalidDataException("皮肤包解压大小超限。");
                var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("皮肤包包含越界路径。");
                var ext = Path.GetExtension(destination).ToLowerInvariant();
                if (ext is not (".ini" or ".png" or ".jpg" or ".jpeg" or ".wav" or ".ogg" or ".mp3")) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); entry.ExtractToFile(destination, false);
            }
            var ini = File.Exists(Path.Combine(staging, "skin.ini")) ? Path.Combine(staging, "skin.ini") : Directory.EnumerateFiles(staging, "skin.ini", SearchOption.AllDirectories).Order().FirstOrDefault();
            return ini == null ? staging : Path.GetDirectoryName(ini)!;
        }
        catch
        {
            // Only this operation's fresh staging directory can be removed.
            var resolved = Path.GetFullPath(staging); var allowed = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("import-", StringComparison.Ordinal))
                try { Directory.Delete(resolved, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }, token);
}

internal sealed record SkinAnimation(Bitmap[] Frames, int Density)
{
    public Bitmap Frame(double seconds, double fps = 60, bool loop = true)
    {
        var rate = fps < 0 ? Frames.Length : fps;
        var index = Math.Max(0, (long)(seconds * rate));
        return Frames[loop ? index % Frames.Length : Math.Min(index, Frames.Length - 1)];
    }
    public double LogicalWidth => Frames[0].PixelSize.Width / (double)Density;
    public double LogicalHeight => Frames[0].PixelSize.Height / (double)Density;
}
