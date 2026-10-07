using LumaChart.Application.Workspaces;

internal static class WorkspaceUtilitySpecs
{
    public static void Run()
    {
        Assert(ChartWorkspaceSafetyPolicy.GetClosePrompt(isDirty: false, isPreservedRoomDraft: false) ==
               ChartTabClosePrompt.None,
            "a clean local chart unexpectedly required a close prompt");
        Assert(ChartWorkspaceSafetyPolicy.GetClosePrompt(isDirty: true, isPreservedRoomDraft: false) ==
               ChartTabClosePrompt.UnsavedChanges,
            "an unsaved local chart lost its close prompt");
        Assert(ChartWorkspaceSafetyPolicy.GetClosePrompt(isDirty: false, isPreservedRoomDraft: true) ==
               ChartTabClosePrompt.PreservedRoomDraft &&
               ChartWorkspaceSafetyPolicy.GetClosePrompt(isDirty: true, isPreservedRoomDraft: true) ==
               ChartTabClosePrompt.PreservedRoomDraft,
            "a preserved room draft could become silently disposable when its session was clean");
        Assert(ChartWorkspaceSafetyPolicy.AllowsLocalNoteSoundImport(isOnlineRoomChart: false) &&
               !ChartWorkspaceSafetyPolicy.AllowsLocalNoteSoundImport(isOnlineRoomChart: true),
            "local Note sound import was not blocked for an online room chart");

        var root = Path.Combine(Path.GetTempPath(), "lumachart-workspace-spec-" + Guid.NewGuid().ToString("N"));
        try
        {
            var chartRoot = Path.Combine(root, "chart");
            var sourceA = Path.Combine(root, "source-a");
            var sourceB = Path.Combine(root, "source-b");
            Directory.CreateDirectory(chartRoot);
            Directory.CreateDirectory(sourceA);
            Directory.CreateDirectory(sourceB);
            var firstSource = Path.Combine(sourceA, "click.wav");
            var secondSource = Path.Combine(sourceB, "click.wav");
            File.WriteAllBytes(firstSource, [1, 2, 3]);
            File.WriteAllBytes(secondSource, [4, 5, 6]);
            var chartPath = Path.Combine(chartRoot, "chart.mc");

            var first = ChartAssetImportService.ImportNoteSound(chartPath, firstSource);
            var duplicate = ChartAssetImportService.ImportNoteSound(chartPath, firstSource);
            var conflict = ChartAssetImportService.ImportNoteSound(chartPath, secondSource);
            Assert(first.FileName == "click.wav" && first.Created && File.Exists(first.DestinationPath),
                "note sound was not copied into the flat chart root");
            Assert(duplicate.FileName == "click.wav" && !duplicate.Created,
                "an identical note sound was copied twice");
            Assert(conflict.FileName == "click_1.wav" && conflict.Created &&
                   !conflict.FileName.Contains(Path.DirectorySeparatorChar) &&
                   !conflict.FileName.Contains(Path.AltDirectorySeparatorChar),
                "a conflicting note sound did not receive a unique path-free file name");
            var eventualRoot = Path.Combine(root, "eventual-chart");
            var reservedEditingRoot = Path.Combine(root, "reserved-editing");
            Directory.CreateDirectory(eventualRoot);
            Directory.CreateDirectory(reservedEditingRoot);
            File.WriteAllBytes(Path.Combine(eventualRoot, "click.wav"), [9, 9, 9]);
            var reserved = ChartAssetImportService.ImportNoteSound(
                Path.Combine(reservedEditingRoot, "working.mc"),
                firstSource,
                Path.Combine(eventualRoot, "chart.mc"));
            Assert(reserved.FileName == "click_1.wav" && File.Exists(reserved.DestinationPath),
                "note sound import did not reserve a stable name against the eventual chart directory");
            AssertThrows<NotSupportedException>(() =>
            {
                var unsupported = Path.Combine(sourceA, "click.txt");
                File.WriteAllText(unsupported, "not audio");
                ChartAssetImportService.ImportNoteSound(chartPath, unsupported);
            }, "an unsupported note sound extension was accepted");

            Assert(ChartAssetImportService.IsSupportedNoteSoundFile("upper.WaV") &&
                   ChartAssetImportService.IsSupportedNoteSoundFile("voice.OGG") &&
                   !ChartAssetImportService.IsSupportedNoteSoundFile("voice.wav.exe") &&
                   !ChartAssetImportService.IsSupportedNoteSoundFile("voice.txt") &&
                   !ChartAssetImportService.IsSupportedNoteSoundFile(null),
                "the reusable Note sound extension check accepted or rejected the wrong path");

            var candidateA = Path.Combine(root, "candidate-a");
            var candidateB = Path.Combine(root, "candidate-b");
            var nestedCandidate = Path.Combine(candidateA, "nested");
            Directory.CreateDirectory(candidateA);
            Directory.CreateDirectory(candidateB);
            Directory.CreateDirectory(nestedCandidate);
            File.WriteAllBytes(Path.Combine(candidateA, "B.WAV"), [1]);
            File.WriteAllBytes(Path.Combine(candidateA, "a.ogg"), [2]);
            File.WriteAllBytes(Path.Combine(candidateA, "c.Mp3"), [3]);
            File.WriteAllBytes(Path.Combine(candidateB, "D.FLAC"), [4]);
            File.WriteAllBytes(Path.Combine(candidateB, "E.m4A"), [5]);
            File.WriteAllBytes(Path.Combine(candidateB, "F.AAC"), [6]);
            File.WriteAllBytes(Path.Combine(candidateA, "ignored.txt"), [7]);
            File.WriteAllBytes(Path.Combine(candidateA, "ignored.wav.exe"), [8]);
            File.WriteAllBytes(Path.Combine(nestedCandidate, "nested.wav"), [9]);

            var candidates = ChartAssetImportService.EnumerateNoteSoundCandidates(
                [candidateB, candidateA, candidateA, Path.Combine(candidateA, "."), null, ""]);
            var reversedCandidates = ChartAssetImportService.EnumerateNoteSoundCandidates(
                [candidateA, candidateB]);
            Assert(candidates.Select(candidate => candidate.FileName).SequenceEqual(
                       ["a.ogg", "B.WAV", "c.Mp3", "D.FLAC", "E.m4A", "F.AAC"]) &&
                   candidates.All(candidate => Path.IsPathFullyQualified(candidate.FullPath) &&
                                               File.Exists(candidate.FullPath)),
                "top-level Note sound candidates were not filtered or deterministically sorted");
            Assert(candidates.Select(candidate => candidate.FullPath).SequenceEqual(
                       reversedCandidates.Select(candidate => candidate.FullPath),
                       OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal),
                "Note sound candidate order depended on directory enumeration order or retained duplicates");
            Assert(candidates.All(candidate => !candidate.FullPath.Contains(
                       $"{Path.DirectorySeparatorChar}nested{Path.DirectorySeparatorChar}",
                       StringComparison.OrdinalIgnoreCase)),
                "Note sound candidate enumeration recursed below the requested chart directory");

            var cacheRoot = Path.Combine(root, "cache");
            var disposable = Path.Combine(cacheRoot, "old", "stale.bin");
            var protectedRoot = Path.Combine(cacheRoot, "active");
            var protectedFile = Path.Combine(protectedRoot, "working.mc");
            Directory.CreateDirectory(Path.GetDirectoryName(disposable)!);
            Directory.CreateDirectory(protectedRoot);
            File.WriteAllBytes(disposable, new byte[11]);
            File.WriteAllBytes(protectedFile, new byte[7]);
            var cleanup = ManagedStorageCleanupService.Clean(
                [new ManagedStorageCleanupTarget(cacheRoot, [protectedRoot])]);
            Assert(cleanup.FilesDeleted == 1 && cleanup.BytesFreed == 11 &&
                   !File.Exists(disposable) && File.Exists(protectedFile) && cleanup.Failures.Count == 0,
                "managed cache cleanup did not delete stale data while retaining the active workspace");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    private static void AssertThrows<TException>(Action action, string message) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
