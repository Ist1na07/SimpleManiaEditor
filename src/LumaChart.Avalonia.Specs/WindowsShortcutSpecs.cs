using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using LumaChart.Application.Editing;
using LumaChart.Avalonia.Editing;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsShortcutSpecs
{
    private static readonly ExpectedShortcut[] Wpf110Catalog =
    [
        new("New", "新建谱面", false, "Ctrl+N"),
        new("Open", "打开谱面", false, "Ctrl+O"),
        new("Save", "保存", false, "Ctrl+S"),
        new("SaveAs", "另存为", false, "Ctrl+Shift+S"),
        new("Undo", "撤销", false, "Ctrl+Z"),
        new("Redo", "重做", false, "Ctrl+Y"),
        new("Cut", "剪切 Note", true, "Ctrl+X"),
        new("Copy", "复制 Note", true, "Ctrl+C"),
        new("Paste", "粘贴 Note", true, "Ctrl+V"),
        new("SelectAll", "全选 Note", true, "Ctrl+A"),
        new("ToolSelect", "选择工具", true, "Q"),
        new("ToolTap", "单键工具", true, "W"),
        new("ToolHold", "长键工具", true, "E"),
        new("ToolErase", "擦除工具", true, "R"),
        new("PlayPause", "播放 / 暂停", true, "Space"),
        new("Delete", "删除 Note", true, "Delete"),
        new("StepForward", "向前一格", true, "Up"),
        new("StepBack", "向后一格", true, "Down"),
        new("GoHome", "回到开头", true, "Home"),
        new("TogglePro", "切换 ProEditor", true, "P"),
        new("ToggleFullscreen", "全屏 / 退出全屏", true, "F11"),
        new("Group0", "切换分组 0", true, "D0"),
        new("Group1", "切换分组 1", true, "D1"),
        new("Group2", "切换分组 2", true, "D2"),
        new("Group3", "切换分组 3", true, "D3"),
        new("Group4", "切换分组 4", true, "D4"),
        new("Group5", "切换分组 5", true, "D5"),
        new("Group6", "切换分组 6", true, "D6"),
        new("Group7", "切换分组 7", true, "D7"),
        new("Group8", "切换分组 8", true, "D8"),
        new("Group9", "切换分组 9", true, "D9"),
        new("SnapIncrease", "加分度", true, "="),
        new("SnapDecrease", "减分度", true, "-"),
        new("SnapSetFixed", "切换到固定分度", true, "Ctrl+=")
    ];

    public static IReadOnlyList<WindowsShortcutSpecResult> Run(string isolationRoot)
    {
        Directory.CreateDirectory(isolationRoot);
        var results = new List<WindowsShortcutSpecResult>();
        Check(results, "shortcuts-wpf-1-1-0-catalog-and-defaults-exact", VerifyCatalog);
        Check(results, "shortcuts-normalize-preserves-explicit-empty-and-restores-missing",
            VerifyNormalize);
        Check(results, "shortcuts-conflict-reject-replace-and-clear-contract", VerifyAssignment);
        Check(results, "shortcuts-gesture-format-and-capture-control-contract", VerifyCapture);
        Check(results, "shortcuts-space-pan-repeat-and-playback-conflict-contract",
            VerifySpacePan);
        Check(results, "shortcuts-playback-command-fires-once-until-key-up",
            VerifyOneShotPlayback);
        Check(results, "shortcuts-text-input-focus-suppresses-editor-actions-only",
            VerifyTextInputSuppression);
        Check(results, "shortcuts-wechat-ime-shift-duplicate-commit-is-filtered-exactly-once",
            VerifyImeDuplicateCommitGuard);
        Check(results, "shortcuts-all-actions-have-executable-routes", VerifyRoutes);
        Check(results, "shortcuts-toolbar-and-palette-history-preserve-live-room-scoped-undo",
            VerifyHistoryCommand);
        Check(results, "shortcuts-atomic-json-roundtrip-and-wpf-file-isolation",
            () => VerifyStore(isolationRoot));
        return results;
    }

    private static string VerifyCatalog()
    {
        var actual = WindowsEditorShortcutSettings.Catalog;
        var defaults = WindowsEditorShortcutSettings.CreateDefaults();
        Require(actual.Count == Wpf110Catalog.Length,
            $"Expected {Wpf110Catalog.Length} WPF actions, got {actual.Count}.");
        Require(defaults.Count == Wpf110Catalog.Length,
            $"Expected {Wpf110Catalog.Length} WPF bindings, got {defaults.Count}.");
        for (var index = 0; index < Wpf110Catalog.Length; index++)
        {
            var expected = Wpf110Catalog[index];
            var descriptor = actual[index];
            Require(descriptor.Id == expected.Id &&
                    descriptor.Label == expected.Label &&
                    descriptor.SuppressWhenEditing == expected.SuppressWhenEditing,
                $"Catalog mismatch at ordinal {index}: {descriptor}.");
            Require(defaults.TryGetValue(expected.Id, out var gesture) &&
                    gesture == expected.Gesture,
                $"Default binding mismatch for {expected.Id}: {gesture}.");
        }

        var reset = new WindowsEditorShortcutSettings(
            new Dictionary<string, string> { ["Save"] = "F6" });
        reset.Reset();
        Require(Wpf110Catalog.All(item => reset.Bindings[item.Id] == item.Gesture),
            "Reset did not restore the complete WPF catalog.");
        return $"actions={actual.Count}; first={actual[0].Id}/{defaults[actual[0].Id]}; " +
               $"last={actual[^1].Id}/{defaults[actual[^1].Id]}";
    }

    private static string VerifyNormalize()
    {
        var settings = new WindowsEditorShortcutSettings(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["New"] = string.Empty,
                ["Open"] = null!,
                ["ForeignFutureAction"] = "F24"
            });
        Require(settings.Bindings["New"] == string.Empty,
            "Explicit empty binding was replaced by the default.");
        Require(settings.Bindings["Open"] == "Ctrl+O" &&
                settings.Bindings["Save"] == "Ctrl+S",
            "Null or missing WPF binding was not normalized.");
        Require(settings.Bindings["foreignfutureaction"] == "F24",
            "Unknown future binding or case-insensitive lookup was lost.");
        return $"explicitEmpty={settings.Bindings["New"].Length == 0}; " +
               $"restored={settings.Bindings["Open"]}; retainedUnknown=" +
               settings.Bindings["ForeignFutureAction"];
    }

    private static string VerifyAssignment()
    {
        var settings = new WindowsEditorShortcutSettings();
        var rejected = settings.Assign("Save", "Ctrl+O", replaceConflict: false);
        Require(!rejected.Applied && rejected.ConflictAction == "Open" &&
                settings.Bindings["Save"] == "Ctrl+S" &&
                settings.Bindings["Open"] == "Ctrl+O",
            "Conflict rejection changed one of the existing bindings.");
        var replaced = settings.Assign("Save", "Ctrl+O", replaceConflict: true);
        Require(replaced.Applied && replaced.ConflictAction == "Open" &&
                settings.Bindings["Save"] == "Ctrl+O" &&
                settings.Bindings["Open"] == string.Empty,
            "Conflict replacement did not clear the old action.");
        var cleared = settings.Assign("Save", string.Empty, replaceConflict: true);
        Require(cleared.Applied && cleared.ConflictAction == null &&
                settings.Bindings["Save"] == string.Empty,
            "Explicit shortcut clearing failed.");
        var unknownRejected = false;
        try
        {
            settings.Assign("Unknown", "F8", replaceConflict: false);
        }
        catch (ArgumentOutOfRangeException)
        {
            unknownRejected = true;
        }
        Require(unknownRejected, "Unknown shortcut action was accepted.");
        return $"rejected={rejected.ConflictAction}; replaced={replaced.ConflictAction}; " +
               $"cleared={settings.Bindings["Save"].Length == 0}";
    }

    private static string VerifyCapture()
    {
        Require(WindowsEditorShortcutGesture.Format(
                    Key.S,
                    KeyModifiers.Control | KeyModifiers.Shift) == "Ctrl+Shift+S",
            "Modifier ordering drifted from WPF.");
        Require(WindowsEditorShortcutGesture.Format(
                    Key.F8,
                    KeyModifiers.Control | KeyModifiers.Shift |
                    KeyModifiers.Alt | KeyModifiers.Meta) == "Ctrl+Shift+Alt+Win+F8",
            "Four-modifier gesture format drifted from WPF.");
        Require(WindowsEditorShortcutGesture.Format(Key.Space, KeyModifiers.None) == "Space" &&
                WindowsEditorShortcutGesture.Format(Key.D7, KeyModifiers.None) == "D7" &&
                WindowsEditorShortcutGesture.Format(
                    Key.OemSemicolon,
                    KeyModifiers.None) == "OemSemicolon" &&
                WindowsEditorShortcutGesture.Format(
                    Key.Add,
                    KeyModifiers.None) == "Add",
            "Space, number-row, OEM, or numpad gesture format drifted from WPF.");
        Require(WindowsEditorShortcutGesture.ResolveReportedKey(
                    Key.ImeProcessed,
                    PhysicalKey.Q) == Key.Q &&
                WindowsEditorShortcutGesture.ResolveReportedKey(
                    Key.DeadCharProcessed,
                    PhysicalKey.Digit7) == Key.D7 &&
                WindowsEditorShortcutGesture.ResolveReportedKey(
                    Key.System,
                    PhysicalKey.F11) == Key.F11 &&
                WindowsEditorShortcutGesture.ResolveReportedKey(
                    Key.ImeProcessed,
                    PhysicalKey.Semicolon) == Key.OemSemicolon &&
                WindowsEditorShortcutGesture.ResolveReportedKey(
                    Key.ImeProcessed,
                    PhysicalKey.NumPadAdd) == Key.Add &&
                WindowsEditorShortcutGesture.ResolveReportedKey(
                    Key.ImeProcessed,
                    PhysicalKey.None,
                    "q") == Key.Q &&
                WindowsEditorShortcutGesture.ResolveReportedKey(
                    Key.ImeProcessed,
                    PhysicalKey.None,
                    "7") == Key.D7 &&
                WindowsEditorShortcutGesture.ResolveReportedKey(
                    Key.ImeProcessed,
                    PhysicalKey.None,
                    keySymbol: null,
                    pressedKeyFallback: Key.Q) == Key.Q &&
                WindowsEditorShortcutGesture.ResolveReportedKey(
                    Key.W,
                    PhysicalKey.Q) == Key.W,
            "IME/System physical-key recovery or regular logical-key preservation drifted from WPF.");

        var escape = WindowsEditorShortcutGesture.InterpretCapture(
            Key.Escape,
            KeyModifiers.None);
        var modifier = WindowsEditorShortcutGesture.InterpretCapture(
            Key.LeftCtrl,
            KeyModifiers.Control);
        var backspace = WindowsEditorShortcutGesture.InterpretCapture(
            Key.Back,
            KeyModifiers.None);
        var modifiedBackspace = WindowsEditorShortcutGesture.InterpretCapture(
            Key.Back,
            KeyModifiers.Control);
        Require(escape.Kind == WindowsEditorShortcutCaptureKind.Cancel &&
                modifier.Kind == WindowsEditorShortcutCaptureKind.IgnoreModifier &&
                backspace.Kind == WindowsEditorShortcutCaptureKind.Clear &&
                modifiedBackspace.Kind == WindowsEditorShortcutCaptureKind.Assign &&
                modifiedBackspace.Gesture == "Ctrl+Back",
            "Escape, modifier-only, Backspace, or modified-Backspace capture drifted from WPF.");
        return $"format=Ctrl+Shift+Alt+Win+F8; imeQ=Q; oem=OemSemicolon; " +
               $"numpad=Add; escape={escape.Kind}; " +
               $"backspace={backspace.Kind}; modifiedBackspace={modifiedBackspace.Gesture}";
    }

    private static string VerifySpacePan()
    {
        var state = new WindowsEditorSpacePanGestureState();
        Require(state.HandleKeyDown(
                    Key.Space,
                    KeyModifiers.None,
                    proEditorEnabled: true,
                    pointerOverTimeline: true) ==
                WindowsEditorSpacePanKeyDownAction.Arm &&
                state.IsArmed,
            "The first eligible Space key-down did not arm ProEditor panning.");
        Require(state.HandleKeyDown(
                    Key.Space,
                    KeyModifiers.None,
                    proEditorEnabled: true,
                    pointerOverTimeline: false) ==
                WindowsEditorSpacePanKeyDownAction.SuppressRepeat &&
                state.IsArmed,
            "A repeated Space key-down escaped the armed pan gesture.");
        Require(state.HandleKeyUp(Key.Space, panned: true) ==
                WindowsEditorSpacePanKeyUpAction.ConsumedPan &&
                !state.IsArmed &&
                state.HandleKeyUp(Key.Space, panned: true) ==
                WindowsEditorSpacePanKeyUpAction.None,
            "A completed pan toggled playback or a duplicate key-up was accepted.");

        Require(state.HandleKeyDown(
                    Key.Space,
                    KeyModifiers.None,
                    proEditorEnabled: true,
                    pointerOverTimeline: true) ==
                WindowsEditorSpacePanKeyDownAction.Arm &&
                state.HandleKeyUp(Key.Space, panned: false) ==
                WindowsEditorSpacePanKeyUpAction.TogglePlayback,
            "An unused Space-pan press did not fall back to playback.");
        Require(state.HandleKeyDown(
                    Key.Space,
                    KeyModifiers.Control,
                    proEditorEnabled: true,
                    pointerOverTimeline: true) ==
                WindowsEditorSpacePanKeyDownAction.None &&
                state.HandleKeyDown(
                    Key.Space,
                    KeyModifiers.None,
                    proEditorEnabled: false,
                    pointerOverTimeline: true) ==
                WindowsEditorSpacePanKeyDownAction.None &&
                state.HandleKeyDown(
                    Key.Space,
                    KeyModifiers.None,
                    proEditorEnabled: true,
                    pointerOverTimeline: false) ==
                WindowsEditorSpacePanKeyDownAction.None,
            "Modified Space or an ineligible surface armed ProEditor panning.");
        return "arm=first; repeat=suppressed; panned=consumed; unused=playback; " +
               "modified/ineligible=none";
    }

    private static string VerifyOneShotPlayback()
    {
        var state = new WindowsEditorOneShotShortcutState();
        Require(state.TryPress(Key.Space) &&
                !state.TryPress(Key.Space) &&
                state.TryPress(Key.F8),
            "Repeated key-down was accepted before the matching key-up.");
        Require(state.Release(Key.Space) &&
                state.TryPress(Key.Space) &&
                !state.Release(Key.F9),
            "Key-up did not release exactly the matching one-shot key.");
        state.Clear();
        Require(state.TryPress(Key.Space) && state.TryPress(Key.F8),
            "Focus-loss clearing left a one-shot shortcut latched.");
        return "first=accepted; repeat=suppressed; key-up=rearmed; clear=rearmed-all";
    }

    private static string VerifyTextInputSuppression()
    {
        var settings = new WindowsEditorShortcutSettings();
        var editorQ = WindowsEditorShortcutGesture.Resolve(
            settings,
            Key.Q,
            KeyModifiers.None,
            textEditing: false);
        var textQ = WindowsEditorShortcutGesture.Resolve(
            settings,
            Key.Q,
            KeyModifiers.None,
            textEditing: true);
        var textSave = WindowsEditorShortcutGesture.Resolve(
            settings,
            Key.S,
            KeyModifiers.Control,
            textEditing: true);
        var textSelectAll = WindowsEditorShortcutGesture.Resolve(
            settings,
            Key.A,
            KeyModifiers.Control,
            textEditing: true);
        Require(editorQ?.Id == "ToolSelect" && textQ == null,
            "Q was not routed to the editor or was not suppressed in text input.");
        Require(textSave?.Id == "Save" && textSelectAll == null,
            "Global Save or native text SelectAll suppression drifted from WPF.");
        Require(WindowsAvaloniaEditorInputPolicy.IsTextEditingControl(new TextBox()) &&
                WindowsAvaloniaEditorInputPolicy.IsTextEditingControl(new ComboBox()) &&
                !WindowsAvaloniaEditorInputPolicy.IsTextEditingControl(new Button()),
            "Text editing focus classification is incorrect.");
        Require(WindowsAvaloniaEditorInputPolicy.IsTextEditingShortcut(
                    Key.Delete,
                    KeyModifiers.None) &&
                WindowsAvaloniaEditorInputPolicy.IsTextEditingShortcut(
                    Key.C,
                    KeyModifiers.Control) &&
                !WindowsAvaloniaEditorInputPolicy.IsTextEditingShortcut(
                    Key.S,
                    KeyModifiers.Control),
            "Native text-edit command preservation is incorrect.");
        Require(WindowsAvaloniaEditorInputPolicy.DeferToTextInputMethod(
                    Key.ImeProcessed,
                    KeyModifiers.Shift) &&
                WindowsAvaloniaEditorInputPolicy.DeferToTextInputMethod(
                    Key.DeadCharProcessed,
                    KeyModifiers.None) &&
                WindowsAvaloniaEditorInputPolicy.DeferToTextInputMethod(
                    Key.ImeProcessed,
                    KeyModifiers.Control) &&
                WindowsAvaloniaEditorInputPolicy.DeferToTextInputMethod(
                    Key.Q,
                    KeyModifiers.None) &&
                WindowsAvaloniaEditorInputPolicy.DeferToTextInputMethod(
                    Key.Q,
                    KeyModifiers.Shift) &&
                WindowsAvaloniaEditorInputPolicy.DeferToTextInputMethod(
                    Key.LeftShift,
                    KeyModifiers.Shift) &&
                !WindowsAvaloniaEditorInputPolicy.DeferToTextInputMethod(
                    Key.S,
                    KeyModifiers.Control),
            "IME composition or unmodified text input can still enter global physical-key fallback.");
        return $"editorQ={editorQ?.Id}; textQ=none; textSave={textSave?.Id}; " +
               "textSelectAll=native; qwe+Shift=fully-deferred";
    }

    private static string VerifyImeDuplicateCommitGuard()
    {
        var target = new object();
        var guard = new WindowsImeDuplicateCommitGuard();
        guard.ObserveKeyDown(Key.ImeProcessed, KeyModifiers.None, true, 100);
        guard.ObserveKeyDown(Key.ImeProcessed, KeyModifiers.None, true, 180);
        guard.ObserveKeyDown(Key.ImeProcessed, KeyModifiers.None, true, 260);
        guard.ObserveKeyDown(Key.ImeProcessed, KeyModifiers.Shift, true, 900);
        Require(!guard.ShouldSuppressReplay(target, "qwe", 1_050) &&
                guard.ShouldSuppressReplay(target, "q", 1_060) &&
                guard.ShouldSuppressReplay(target, "w", 1_061) &&
                guard.ShouldSuppressReplay(target, "e", 1_062),
            "The exact qwe + Shift duplicate replay sequence was not filtered.");

        guard.Reset();
        Require(!guard.ShouldSuppressReplay(target, "a", 2_000) &&
                !guard.ShouldSuppressReplay(target, "a", 2_001),
            "Ordinary repeated text was suppressed without an IME Shift commit.");

        guard.ObserveKeyDown(Key.ImeProcessed, KeyModifiers.Shift, true, 3_000);
        Require(!guard.ShouldSuppressReplay(target, "a", 3_010) &&
                !guard.ShouldSuppressReplay(target, "a", 3_011),
            "Shift without preceding composition activity armed the replay filter.");

        guard.ObserveKeyDown(Key.ImeProcessed, KeyModifiers.None, true, 4_000);
        guard.ObserveKeyDown(Key.ImeProcessed, KeyModifiers.Shift, true, 4_050);
        Require(!guard.ShouldSuppressReplay(target, "qwe", 4_100) &&
                !guard.ShouldSuppressReplay(target, "x", 4_101) &&
                !guard.ShouldSuppressReplay(target, "q", 4_102),
            "A mismatched replay was partially suppressed or left stale state.");

        guard.ObserveKeyDown(Key.ImeProcessed, KeyModifiers.None, true, 5_000);
        guard.ObserveKeyDown(Key.ImeProcessed, KeyModifiers.Shift, true, 5_050);
        Require(!guard.ShouldSuppressReplay(target, "qwe", 5_900) &&
                !guard.ShouldSuppressReplay(target, "q", 5_901),
            "An expired Shift commit window still suppressed text.");
        return "commit=qwe; replay=q,w,e suppressed; ordinary/mismatch/timeout preserved";
    }

    private static string VerifyRoutes()
    {
        var routes = Wpf110Catalog.ToDictionary(
            item => item.Id,
            item => WindowsEditorShortcutRouting.Resolve(item.Id),
            StringComparer.OrdinalIgnoreCase);
        Require(routes.Count == Wpf110Catalog.Length,
            "Not every WPF action has an executable route.");
        Require(routes["ToggleFullscreen"].Route ==
                WindowsEditorShortcutRoute.ToggleFullscreen &&
                routes["ToolSelect"].Route == WindowsEditorShortcutRoute.ToolSelect &&
                routes["ToolTap"].Route == WindowsEditorShortcutRoute.ToolTap &&
                routes["ToolHold"].Route == WindowsEditorShortcutRoute.ToolHold &&
                routes["ToolErase"].Route == WindowsEditorShortcutRoute.ToolErase &&
                routes["StepForward"].Route == WindowsEditorShortcutRoute.StepForward &&
                routes["StepBack"].Route == WindowsEditorShortcutRoute.StepBack &&
                routes["GoHome"].Route == WindowsEditorShortcutRoute.GoHome &&
                routes["SnapIncrease"].Route == WindowsEditorShortcutRoute.SnapIncrease &&
                routes["SnapDecrease"].Route == WindowsEditorShortcutRoute.SnapDecrease &&
                routes["SnapSetFixed"].Route == WindowsEditorShortcutRoute.SnapSetFixed,
            "F11, tool, step, Home, or snap-division command route is incorrect.");
        for (var group = 0; group <= 9; group++)
        {
            var command = routes[$"Group{group}"];
            Require(command.Route == WindowsEditorShortcutRoute.Group &&
                    command.Group == group,
                $"Group{group} did not route to placement group {group}.");
        }

        var unknownRejected = false;
        try
        {
            WindowsEditorShortcutRouting.Resolve("Group10");
        }
        catch (ArgumentOutOfRangeException)
        {
            unknownRejected = true;
        }
        Require(unknownRejected, "Out-of-catalog group action was routed.");
        return $"routes={routes.Count}; fullscreen={routes["ToggleFullscreen"].Route}; " +
               $"step={routes["StepForward"].Route}/{routes["StepBack"].Route}; groups=0-9";
    }

    private static string VerifyHistoryCommand()
    {
        var local = CreateSessionWithNote(out var localNoteId);
        local.Mutate(document => document.Notes.Single(note => note.Id == localNoteId).Lane = 1);
        var localUndo = WindowsEditorHistoryCommand.Execute(local, liveOnlineRoom: false, redo: false);
        var localRedo = WindowsEditorHistoryCommand.Execute(local, liveOnlineRoom: false, redo: true);
        Require(localUndo.Changed &&
                localUndo.ScopedResult == null &&
                localUndo.Status == "已撤销" &&
                localRedo.Changed &&
                localRedo.Status == "已重做" &&
                local.Document.Notes.Single(note => note.Id == localNoteId).Lane == 1,
            "Offline undo/redo no longer uses the normal WPF history path.");

        var live = CreateSessionWithNote(out var liveNoteId);
        live.Mutate(document => document.Notes.Single(note => note.Id == liveNoteId).Lane = 1);
        var remoteNoteId = Guid.NewGuid();
        live.ApplyRemoteMutation(document => document.Notes.Add(new ChartNote
        {
            Id = remoteNoteId,
            Lane = 3
        }));
        var liveUndo = WindowsEditorHistoryCommand.Execute(live, liveOnlineRoom: true, redo: false);
        Require(liveUndo.Changed &&
                liveUndo.ScopedResult == ScopedHistoryResult.Applied &&
                liveUndo.Status == "已撤销自己的上一项操作" &&
                live.Document.Notes.Single(note => note.Id == liveNoteId).Lane == 0 &&
                live.Document.Notes.Any(note => note.Id == remoteNoteId),
            "Live-room undo overwrote or discarded an unrelated remote change.");
        var liveRedo = WindowsEditorHistoryCommand.Execute(live, liveOnlineRoom: true, redo: true);
        Require(liveRedo.Changed &&
                liveRedo.ScopedResult == ScopedHistoryResult.Applied &&
                liveRedo.Status == "已重做自己的上一项操作" &&
                live.Document.Notes.Single(note => note.Id == liveNoteId).Lane == 1 &&
                live.Document.Notes.Any(note => note.Id == remoteNoteId),
            "Live-room redo overwrote or discarded an unrelated remote change.");

        var conflict = CreateSessionWithNote(out var conflictNoteId);
        conflict.Mutate(document =>
            document.Notes.Single(note => note.Id == conflictNoteId).Lane = 1);
        conflict.ApplyRemoteMutation(document =>
            document.Notes.Single(note => note.Id == conflictNoteId).Lane = 2);
        var conflictUndo = WindowsEditorHistoryCommand.Execute(
            conflict,
            liveOnlineRoom: true,
            redo: false);
        Require(!conflictUndo.Changed &&
                conflictUndo.ScopedResult == ScopedHistoryResult.Conflict &&
                conflictUndo.Status.Contains("其他成员修改", StringComparison.Ordinal) &&
                conflict.Document.Notes.Single(note => note.Id == conflictNoteId).Lane == 2,
            "Live-room conflicting undo did not preserve the remote edit.");

        var denied = CreateSessionWithNote(out var deniedNoteId);
        denied.Mutate(document => document.Notes.Single(note => note.Id == deniedNoteId).Lane = 1);
        denied.CanEditNotes = false;
        var deniedUndo = WindowsEditorHistoryCommand.Execute(
            denied,
            liveOnlineRoom: true,
            redo: false);
        Require(!deniedUndo.Changed &&
                deniedUndo.ScopedResult == ScopedHistoryResult.PermissionDenied &&
                deniedUndo.Status == "当前房间权限不允许撤销/重做这项操作" &&
                denied.Document.Notes.Single(note => note.Id == deniedNoteId).Lane == 1,
            "Live-room permission-denied undo changed the document.");

        return "offline=undo+redo; live=scoped+remote-preserved; " +
               "conflict=rejected; permission=rejected";
    }

    private static EditorSession CreateSessionWithNote(out Guid noteId)
    {
        noteId = Guid.NewGuid();
        var session = new EditorSession();
        session.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes =
            [
                new ChartNote
                {
                    Id = noteId,
                    Lane = 0
                }
            ]
        });
        return session;
    }

    private static string VerifyStore(string isolationRoot)
    {
        const string variable = "LUMACHART_AVALONIA_LOCAL_DATA_ROOT";
        var previous = Environment.GetEnvironmentVariable(variable);
        var root = Path.Combine(isolationRoot, "shortcut-store");
        Directory.CreateDirectory(root);
        var wpfPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LumaChart Studio",
            "shortcuts.json");
        var wpfBefore = Snapshot(wpfPath);
        try
        {
            Environment.SetEnvironmentVariable(variable, root);
            var settings = new WindowsEditorShortcutSettings();
            settings.Assign("ToolSelect", string.Empty, replaceConflict: true);
            settings.Assign("SaveAs", "Ctrl+Alt+S", replaceConflict: true);
            WindowsEditorShortcutSettingsStore.Save(settings);
            settings.Assign("ToolTap", "F6", replaceConflict: true);
            WindowsEditorShortcutSettingsStore.Save(settings);

            var loaded = WindowsEditorShortcutSettingsStore.Load();
            var path = WindowsEditorShortcutSettingsStore.SettingsPath;
            using var persisted = JsonDocument.Parse(File.ReadAllBytes(path));
            var bindings = persisted.RootElement.GetProperty("Bindings");
            var temporaryCount = Directory
                .EnumerateFiles(root, ".shortcuts.json.*.tmp", SearchOption.TopDirectoryOnly)
                .Count();
            Require(PathsEqual(path, Path.Combine(root, "shortcuts.json")),
                "Shortcut store did not honor the isolated Avalonia root.");
            Require(loaded.Bindings["ToolSelect"] == string.Empty &&
                    loaded.Bindings["SaveAs"] == "Ctrl+Alt+S" &&
                    loaded.Bindings["ToolTap"] == "F6",
                "Shortcut JSON did not preserve cleared and customized bindings.");
            Require(bindings.GetProperty("ToolSelect").GetString() == string.Empty &&
                    bindings.GetProperty("SaveAs").GetString() == "Ctrl+Alt+S",
                "Persisted JSON shape or values do not match the WPF Bindings contract.");
            Require(temporaryCount == 0,
                "Atomic shortcut save left a temporary file.");
            var wpfAfter = Snapshot(wpfPath);
            Require(wpfBefore == wpfAfter,
                "Avalonia shortcut save changed the WPF shortcuts file.");
            Require(!PathsEqual(path, wpfPath),
                "Avalonia and WPF shortcut paths are not isolated.");
            return $"path={path}; cleared={loaded.Bindings["ToolSelect"].Length == 0}; " +
                   $"custom={loaded.Bindings["ToolTap"]}; temp={temporaryCount}; " +
                   $"wpfUnchanged={wpfBefore == wpfAfter}";
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static FileSnapshot Snapshot(string path)
    {
        if (!File.Exists(path)) return new FileSnapshot(false, 0, string.Empty);
        using var stream = File.OpenRead(path);
        return new FileSnapshot(
            true,
            stream.Length,
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static void Check(
        ICollection<WindowsShortcutSpecResult> results,
        string name,
        Func<string> verify)
    {
        try
        {
            results.Add(new WindowsShortcutSpecResult(name, true, verify()));
        }
        catch (Exception exception)
        {
            results.Add(new WindowsShortcutSpecResult(name, false, exception.Message));
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record ExpectedShortcut(
        string Id,
        string Label,
        bool SuppressWhenEditing,
        string Gesture);

    private readonly record struct FileSnapshot(
        bool Exists,
        long Length,
        string Sha256);
}

internal sealed record WindowsShortcutSpecResult(
    string Name,
    bool Passed,
    string Detail);
