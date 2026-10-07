using System.Text;
using LumaChart.Core.IO;

internal static class ChartDocumentImporterSpecs
{
    private const string McWithoutCreator = """
        {
          "meta": {
            "mode": 0,
            "song": { "title": "Stream MC", "artist": "Artist" },
            "mode_ext": { "column": 4 }
          },
          "time": [{ "beat": [0, 0, 1], "bpm": 120 }],
          "note": []
        }
        """;

    private const string OsuWithoutCreator = """
        osu file format v14

        [General]
        Mode: 3
        AudioFilename: song.ogg

        [Metadata]
        Title:Stream osu
        Artist:Artist
        Version:Normal

        [Difficulty]
        CircleSize:4

        [TimingPoints]
        0,500,4,2,1,70,1,0

        [HitObjects]
        """;

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        TestTextAndFormatBoundaries();
        await TestShortReadsBomAndOwnershipAsync();
        await TestCancellationAndFailureOwnershipAsync();
    }

    private static void TestTextAndFormatBoundaries()
    {
        var mc = ChartDocumentImporter.ImportText(
            McWithoutCreator,
            ChartImportFormat.MalodyMc,
            "Android MC Creator");
        Assert(mc.Metadata.Creator == "Android MC Creator",
            "MC import did not use the caller's explicit creator fallback");
        Assert(mc.FilePath == null, "text import assigned a source path to the MC document");
        var bomText = ChartDocumentImporter.ImportText(
            "\uFEFF" + McWithoutCreator,
            ChartImportFormat.MalodyMc,
            "BOM Text Creator");
        Assert(bomText.Metadata.Creator == "BOM Text Creator",
            "the text import boundary did not tolerate a decoded UTF-8 BOM");

        var osu = ChartDocumentImporter.ImportText(
            OsuWithoutCreator,
            "picked.OSU",
            "Android osu Creator");
        Assert(osu.Metadata.Creator == "Android osu Creator",
            "osu import did not use the caller's explicit creator fallback");
        Assert(osu.FilePath == null, "text import assigned a source name to the osu document");

        Assert(ChartDocumentImporter.GetFormatFromSourceName(".mc") == ChartImportFormat.MalodyMc &&
               ChartDocumentImporter.GetFormatFromSourceName("folder/chart.OsU") == ChartImportFormat.OsuMania,
            "chart source extension detection is not case-insensitive");
        AssertThrows<NotSupportedException>(
            () => ChartDocumentImporter.GetFormatFromSourceName("chart.txt"),
            "unsupported chart extension was accepted");

        const string mcWithCreator = """
            {
              "meta": {
                "mode": 0,
                "creator": "Embedded Creator",
                "song": {},
                "mode_ext": { "column": 4 }
              },
              "note": []
            }
            """;
        var embedded = ChartDocumentImporter.ImportText(
            mcWithCreator,
            ChartImportFormat.MalodyMc,
            "Fallback Creator");
        Assert(embedded.Metadata.Creator == "Embedded Creator",
            "explicit creator fallback overwrote embedded chart metadata");

        var legacy = MalodyMcSerializer.Parse(McWithoutCreator, @"D:\legacy\chart.mc");
        Assert(legacy.FilePath == @"D:\legacy\chart.mc" &&
               legacy.Metadata.Creator == Environment.UserName,
            "the existing Windows Parse overload changed compatibility behavior");
    }

    private static async Task TestShortReadsBomAndOwnershipAsync()
    {
        var mcBytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(McWithoutCreator))
            .ToArray();
        var mcSource = new ShortReadStream(mcBytes, maximumRead: 1);
        var mc = await ChartDocumentImporter.ImportAsync(
            mcSource,
            "content://charts.provider/document/outside.MC",
            "SAF Creator");

        Assert(mc.Metadata.Title == "Stream MC" && mc.Metadata.Creator == "SAF Creator",
            "UTF-8 BOM or one-byte short reads corrupted the MC import");
        Assert(mc.FilePath == null,
            "a content URI leaked into ChartDocument.FilePath");
        Assert(mcSource.ReadCount > 1 && mcSource.DisposeCount == 0 && mcSource.CanRead,
            "MC importer did not preserve its caller-owned short-read stream");

        var osuBytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(OsuWithoutCreator))
            .ToArray();
        var osuSource = new ShortReadStream(osuBytes, maximumRead: 3);
        var osu = await ChartDocumentImporter.ImportAsync(
            osuSource,
            ChartImportFormat.OsuMania,
            "Explicit Format Creator");

        Assert(osu.Metadata.Title == "Stream osu" &&
               osu.Metadata.Creator == "Explicit Format Creator",
            "UTF-8 BOM or short reads corrupted the explicit-format osu import");
        Assert(osu.FilePath == null && osuSource.DisposeCount == 0 && osuSource.CanRead,
            "explicit-format import took ownership of its source or assigned a path");

        var externalSource = new ShortReadStream(Encoding.UTF8.GetBytes(McWithoutCreator), maximumRead: 7);
        var external = await ChartDocumentImporter.ImportAsync(
            externalSource,
            @"E:\Downloads\external.mc",
            "External Creator");
        Assert(external.FilePath == null,
            "an external filesystem path was retained before the chart entered app-owned storage");
        Assert(externalSource.DisposeCount == 0,
            "external source stream ownership changed after import");
    }

    private static async Task TestCancellationAndFailureOwnershipAsync()
    {
        var blocking = new BlockingReadStream();
        using var cancellation = new CancellationTokenSource();
        var import = ChartDocumentImporter.ImportAsync(
            blocking,
            ChartImportFormat.MalodyMc,
            "Cancelled Creator",
            cancellation.Token);
        await blocking.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(
            () => import,
            "blocked asynchronous chart import ignored cancellation");
        Assert(blocking.DisposeCount == 0 && blocking.CanRead,
            "cancelled import disposed its caller-owned stream");

        var invalid = new ShortReadStream(Encoding.UTF8.GetBytes("not JSON"), maximumRead: 2);
        await AssertThrowsAsync<Exception>(
            () => ChartDocumentImporter.ImportAsync(
                invalid,
                ChartImportFormat.MalodyMc,
                "Failure Creator"),
            "invalid MC unexpectedly imported");
        Assert(invalid.DisposeCount == 0 && invalid.CanRead,
            "failed import disposed its caller-owned stream");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private sealed class ShortReadStream(byte[] data, int maximumRead) : Stream
    {
        private int position;

        public int ReadCount { get; private set; }
        public int DisposeCount { get; private set; }
        public override bool CanRead => DisposeCount == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(DisposeCount != 0, this);
            ReadCount++;
            var count = Math.Min(Math.Min(buffer.Length, maximumRead), data.Length - position);
            if (count <= 0) return 0;
            data.AsSpan(position, count).CopyTo(buffer);
            position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource readStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => readStarted.Task;
        public int DisposeCount { get; private set; }
        public override bool CanRead => DisposeCount == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Synchronous reads are not expected.");

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
