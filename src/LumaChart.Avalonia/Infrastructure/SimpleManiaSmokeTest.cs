using LumaChart.Core.IO;
using LumaChart.Core.Models;
using SkiaSharp;
using LumaChart.Application.Libraries;

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
            var library = new MapperLibraryStore(root);
            library.SetAnnotation(file, new(["smoke"], "payload", "未发布", true));
            var cache = new DesktopLibraryMetadataCache(library); cache.Read(file, root); cache.Flush();
            if (!library.Search("tag:smoke keys:7").Contains(file)) throw new InvalidOperationException("Published SQLite/FTS runtime failed.");
            var versions = new MapperVersionStore(root, library);
            var version = versions.SaveAsync(file, reloaded, "payload", false).GetAwaiter().GetResult();
            if (version == null || versions.Read(file, version.Id).Notes.Count != 1) throw new InvalidOperationException("Published Git native runtime failed.");
            Console.WriteLine($"PASS {SimpleManiaProduct.Name} {SimpleManiaProduct.Version}, runtime {Environment.Version}, Skia, 7K LN, SQLite/FTS and Git");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var ownedRoot = Path.GetFullPath(root);
            if (!ownedRoot.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(ownedRoot).StartsWith("sme-payload-", StringComparison.Ordinal))
                throw new IOException("Refusing cleanup outside the owned payload fixture.");
            // Git marks object files read-only on Windows; this temporary repository
            // belongs exclusively to this payload check, never to the user's history.
            foreach (var temporary in Directory.EnumerateFiles(ownedRoot, "*", SearchOption.AllDirectories))
                File.SetAttributes(temporary, File.GetAttributes(temporary) & ~FileAttributes.ReadOnly);
            Directory.Delete(ownedRoot, recursive: true);
        }
    }
}
