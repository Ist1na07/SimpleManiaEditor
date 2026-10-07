using LumaChart.Application.Workspaces;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

internal static class LocalDocumentWorkspaceSpecs
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lumachart-local-workspace-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "Library");
        var editing = Path.Combine(root, "_editing");
        var outside = Path.Combine(root, "outside.mc");
        Directory.CreateDirectory(library);
        try
        {
            var mcPath = Path.Combine(library, "alpha.mc");
            var osuPath = Path.Combine(library, "beta.osu");
            MalodyMcSerializer.Save(CreateDocument("Alpha", lane: 0, beat: 0), mcPath);
            OsuManiaSerializer.Save(CreateDocument("Beta", lane: 1, beat: 1), osuPath, 8, 7);
            MalodyMcSerializer.Save(CreateDocument("Outside", lane: 2, beat: 2), outside);

            var workspace = new LocalDocumentWorkspace(library, editing, "Android Workspace Spec");
            await workspace.InitializeAsync();
            var scanned = await workspace.ScanLibraryAsync();
            Assert(scanned.Count == 2 && scanned.Any(entry => entry.Title == "Alpha") &&
                   scanned.Any(entry => entry.Title == "Beta"),
                "private Library scan did not return both supported source formats");
            await AssertThrowsAsync<InvalidDataException>(
                () => workspace.OpenAsync(outside),
                "the local workspace opened a source outside the platform-owned Library");

            var alpha = await workspace.OpenAsync(mcPath);
            var beta = await workspace.OpenAsync(osuPath);
            Assert(workspace.Tabs.Count == 2 && ReferenceEquals(workspace.ActiveTab, beta),
                "opening two sources did not create and activate independent tabs");
            Assert(!ReferenceEquals(alpha.Session, beta.Session) &&
                   alpha.WorkingPath.EndsWith("working.mc", StringComparison.OrdinalIgnoreCase) &&
                   beta.WorkingPath.EndsWith("working.mc", StringComparison.OrdinalIgnoreCase) &&
                   alpha.Session.Document.FilePath == alpha.WorkingPath &&
                   beta.Session.Document.FilePath == beta.WorkingPath,
                "tabs did not receive independent sessions and MC working copies");

            alpha.Session.Mutate(document => document.Notes.Add(new ChartNote
            {
                Lane = 3,
                Beat = BeatPosition.FromDouble(3)
            }));
            var alphaCount = alpha.Session.Document.Notes.Count;
            Assert(beta.Session.Document.Notes.Count == 1,
                "an edit in the MC tab leaked into the OSU tab");
            beta.Session.Mutate(document => document.Notes.Add(new ChartNote
            {
                Lane = 2,
                Beat = BeatPosition.FromDouble(4)
            }));
            Assert(alpha.Session.Document.Notes.Count == alphaCount,
                "an edit in the OSU tab leaked into the MC tab");

            var alphaSave = await workspace.SaveAsync(alpha.Id);
            var betaSave = await workspace.SaveAsync(beta.Id);
            Assert(alphaSave.Saved && betaSave.Saved &&
                   MalodyMcSerializer.Load(mcPath).Notes.Count == 2 &&
                   OsuManiaSerializer.Load(osuPath).Notes.Count == 2,
                "MC/OSU tabs were not atomically saved back through their source formats");

            alpha.Session.Mutate(document => document.Notes.Add(new ChartNote
            {
                Lane = 0,
                Beat = BeatPosition.FromDouble(6)
            }));
            beta.Session.Mutate(document => document.Notes.Add(new ChartNote
            {
                Lane = 1,
                Beat = BeatPosition.FromDouble(7)
            }));
            workspace.UpdateViewState(alpha.Id, 12.5);
            workspace.UpdateViewState(beta.Id, 27.25);
            workspace.Activate(alpha.Id);
            await workspace.PersistAsync();

            var restored = new LocalDocumentWorkspace(library, editing, "Android Workspace Spec");
            await restored.InitializeAsync();
            Assert(restored.Tabs.Count == 2 && restored.ActiveTab?.SourcePath == mcPath,
                "the active multi-document tab was not restored");
            var restoredAlpha = restored.Tabs.Single(tab => tab.SourcePath == mcPath);
            var restoredBeta = restored.Tabs.Single(tab => tab.SourcePath == osuPath);
            Assert(restoredAlpha.Session.IsDirty && restoredBeta.Session.IsDirty &&
                   restoredAlpha.Session.Document.Notes.Count == 3 &&
                   restoredBeta.Session.Document.Notes.Count == 3 &&
                   Math.Abs(restoredAlpha.CurrentBeat - 12.5) < .000001 &&
                   Math.Abs(restoredBeta.CurrentBeat - 27.25) < .000001,
                "dirty working copies, independent content, or per-tab view state were not restored");

            var invalidBeat = BeatPosition.FromDouble(8);
            restoredAlpha.Session.Mutate(document =>
            {
                document.Notes.Add(new ChartNote { Lane = 0, Beat = invalidBeat });
                document.Notes.Add(new ChartNote { Lane = 0, Beat = invalidBeat });
            });
            var validationFailure = await restored.SaveAsync(restoredAlpha.Id);
            Assert(validationFailure.Status == LocalChartSaveStatus.ValidationFailed &&
                   validationFailure.Issues.Count > 0 && MalodyMcSerializer.Load(mcPath).Notes.Count == 2,
                "invalid overlapping notes were written to the source instead of being blocked");

            await restored.PersistAsync();
            MalodyMcSerializer.Save(CreateDocument("Externally Replaced", lane: 1, beat: 10), mcPath);
            var sourceWins = new LocalDocumentWorkspace(library, editing, "Android Workspace Spec");
            await sourceWins.InitializeAsync();
            var sourceWinsAlpha = sourceWins.Tabs.Single(tab => tab.SourcePath == mcPath);
            Assert(sourceWinsAlpha.Session.Document.Metadata.Title == "Externally Replaced" &&
                   sourceWinsAlpha.Session.Document.Notes.Count == 1 &&
                   !sourceWinsAlpha.Session.IsDirty &&
                   sourceWins.RecoveryWarnings.Any(warning => warning.Contains("source changed", StringComparison.Ordinal)),
                "an externally changed source did not replace the stale dirty working cache");

            var desktopLibrary = Path.Combine(root, "desktop-library");
            var desktopEditing = Path.Combine(root, "desktop-editing");
            var desktopSource = Path.Combine(desktopLibrary, "committed-snapshot.mc");
            Directory.CreateDirectory(desktopLibrary);
            MalodyMcSerializer.Save(CreateDocument("Committed Snapshot", lane: 0, beat: 0), desktopSource);
            var desktop = new DesktopDocumentWorkspace(desktopLibrary, desktopEditing, "Avalonia Workspace Spec");
            await desktop.InitializeAsync();
            var desktopTab = await desktop.OpenAsync(desktopSource);
            desktopTab.Session.BeginInteractiveMutation();
            desktopTab.Session.UpdateInteractiveMutation(document => document.Notes.Add(new ChartNote
            {
                Lane = 0,
                Beat = BeatPosition.Zero
            }));
            await desktop.PersistAsync();
            var cachedDuringInteraction = MalodyMcSerializer.Load(desktopTab.WorkingPath);
            var savedDuringInteraction = await desktop.SaveAsync(desktopTab.Id);
            Assert(savedDuringInteraction.Saved &&
                   cachedDuringInteraction.Notes.Count == 1 &&
                   MalodyMcSerializer.Load(desktopSource).Notes.Count == 1 &&
                   desktopTab.Session.Document.Notes.Count == 2 &&
                   desktopTab.Session.IsInteractiveMutationActive,
                "the Avalonia workspace persisted, validated, or saved an in-progress interactive edit");
            desktopTab.Session.CancelInteractiveMutation();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ChartDocument CreateDocument(string title, int lane, double beat) => new()
    {
        KeyCount = 4,
        Metadata = new ChartMetadata
        {
            Title = title,
            Artist = "Workspace Artist",
            Creator = "Workspace Creator",
            Version = "Normal",
            OverallDifficulty = 8,
            HpDrainRate = 7
        },
        Timing =
        {
            new TimingEvent
            {
                Beat = BeatPosition.Zero,
                Bpm = 120
            }
        },
        Notes =
        {
            new ChartNote
            {
                Lane = lane,
                Beat = BeatPosition.FromDouble(beat)
            }
        }
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
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
}
