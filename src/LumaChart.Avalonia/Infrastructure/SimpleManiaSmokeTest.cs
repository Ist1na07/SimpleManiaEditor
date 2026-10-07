using LumaChart.Core.IO;
using LumaChart.Core.Models;
using SkiaSharp;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>Checks the published native/runtime payload without opening a desktop window or user data.</summary>
internal static class SimpleManiaSmokeTest
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "sme-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var bitmap = new SKBitmap(16, 16);
            if (bitmap.Width != 16) throw new InvalidOperationException("Skia native runtime failed.");
            var document = new ChartDocument { KeyCount = 7 };
            document.Notes.Add(new ChartNote { Lane = 3, Beat = BeatPosition.FromDouble(2), EndBeat = BeatPosition.FromDouble(4) });
            var file = Path.Combine(root, "smoke.osu");
            OsuManiaSerializer.Save(document, file);
            var reloaded = OsuManiaSerializer.Load(file);
            if (reloaded.KeyCount != 7 || reloaded.Notes.Count != 1 || !reloaded.Notes[0].IsHold)
                throw new InvalidOperationException("Published 7K format runtime failed.");
            Console.WriteLine($"PASS {SimpleManiaProduct.Name} {SimpleManiaProduct.Version}, runtime {Environment.Version}, Skia and 7K LN read/write");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { Directory.Delete(root, recursive: true); }
    }
}
