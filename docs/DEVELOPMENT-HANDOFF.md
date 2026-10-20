## 0.1.38 read-only edit entry, osu search, full-field incremental index and prefetched non-1x audio

Read-only charts now expose an accent 编辑 button as the leftmost item of the fixed top toolbar (automation id Editor.TopBar.EditReadOnly, 67x32 at buttonY 51, above the timeline top 107.3). Activating it clones the whole mapset under CreationRoot and opens an editable tab; the status bar and toast report the copy directory, and the button disables with 正在复制… while cloning. The old left-toolbar 克隆并编辑 button (Editor.CloneReadOnly) is removed, the left bar is back to note tools/transforms, and the read-only copy points at the top entry. Evidence from one run: cloneMs 297–453, source and copy SHA256 both 0D35F1D4…48A9, source files and the read-only tab remained, a double click produced exactly one copy, and locking the source .osu produced no new tab, an unchanged source hash and the status 克隆未完成，原文件保留：…. Clone timing for a real large mapset was not measured.

Both library search columns now speak osu! syntax: mapper=/creator=/author=, artist=, title=, diff=/version=, key=/keys= (comma list and ranges), bpm, cs, source, tag, format, state, note and star:true (favourite), with = and : synonyms, !=/!:, < <= <: > >= >:, quoted phrases, "..."! equality, [difficulty] and -term negation. Unknown fields and malformed input fall back to plain text without exceptions; ar/od/hp/dr, star rating, ln and status emit 暂不支持 X（该条件按文本搜索）. On a 20k identical data set the old SQLite/FTS text path took 34–42 ms against 2.6–4.1 ms in the in-memory evaluator; operator queries take 1.2 ms, parsing 4–7 µs and per-chart matching allocates 0 bytes. Twelve shared-vocabulary queries agree between SQLite and memory, and tag: is substring on both. The ★ 收藏 and 未发布 buttons now filter both columns (right column 3 rows to 1, clearing restores 3) rather than only the left one.

Library indexing moved to an osu-model v2. Import parses Title/Artist/Creator/Version/KeyCount/Format/Source/BPM/Length once into SQLite together with file size/mtime; personal tags stay in the personal table. Length is playable duration (last note END minus first note START in seconds), not audio length; .osu holds use objectParams end, Malody is converted through TimingMap, and Malody without timing/notes keeps null with a hint. Enumeration hits the cache by size/mtime, parses only new/changed files and removes vanished rows: 20k first import 10,446.2 ms, unchanged refresh 287.8 ms (enumeration alone 114.3 ms), 1% (200 files) 443.4 ms against 10,090.9 ms for a full parse (~22.7x), and deleting 10 files leaves 19,990 rows with SQLite in sync. Old headers get in-place ALTERs and a background backfill: 1200 old-schema rows migrate+backfill in 927.4 ms, length<60 goes 0 to 335, search stays usable during backfill and personal tags survive. Null Length rows are excluded from length comparisons with 部分谱面时长未知，刷新索引后补齐 until backfill finishes. Entry remains cache-first (--library-037, 20k cached rows: first browser build 147–160 ms, re-entry 105–106 ms, no directory scan). The real 46k user-library migration/backfill was not tested; mania star rating is not implemented and no values were faked; ar/od/hp/dr/ln/status stay text fallbacks.

Non-1x playback uses the new PrefetchingSampleProvider: a dedicated producer thread keeps a 65536-frame (~1.4 s) ring buffer and the render callback only copies bytes. 1x remains direct, with no prefetch and no stretch, because gate-036 rate/seek/commit semantics depend on decoder-time EOF. A read-only real Albemuth MP3 read at device pace (2 s warm-up, 15 s steady) measured callback p99 0.012–0.28 ms, over10=0, underrun=0 and max ≤1.5 ms, independent of rate (2x 0.014 ms, 0.1x 0.012 ms at 5 ms blocks); the pre-change 0.1x path had p99 15.75 ms and 189/4000 calls over 10 ms. The per-note thump was traced to 64 ms history replay plus zero fill at every segment end; both are removed, segment ends flush real data, and sub-100 ms segments loop their own last 24 ms through a 4 ms crossfade. 24 clicks sound 24 times at 2x/1.5x/1.25x/1.1x/0.9x/0.75x/0.5x/0.25x/0.1x (48 before, 96 at 0.1x), peak 0.73 (92% of the 0.79 source, 0.58 before), onset error ≤0.2 ms. The chord false-cut detector now runs two passes (0.1.37 baseline position plus 5 ms after, 8x threshold against the past 200 ms mean); an 8 s three-chord fixture dropped from 66 segments to 1, and its 220 Hz component matches the continuous path (0.005–0.036 versus 0.04–0.22 before). The 50 ms burst internal zero gap went 10.85 ms to 0 ms; kick-plus-bed join step went 80 to 0 with maxJump 0.008–0.030 (0.037 threshold 0.05). Trade-off: continuous SoundTouch at 0.5x–0.1x moves onsets 24–48 ms earlier and fails 027's unchanged ±5 ms criterion, so edge-only segmentation stays and steady chords use one segment. Known limitation: join step 0.0299 is slightly above the internal 0.02 target (equal to the 0.03 bass amplitude, not a pop, within the 0.037 threshold). All measurements are pre-device PCM and callback timing; no sound-card/headphone listening test was done.

Validation: core specs 59/59; `--mapper-033/034/035/036`, `--regression-030`, `--library-037`, `--library-038`, `--tabs-037`, `--search-038`, `--readonly-038`, `--audio-026`, `--audio-noise-037`, `--audio-onsets-036`, `--audio-gate-036` and `--audio-stretch-038` all pass (`artifacts/battery-038.txt`). The packaged 0.1.38 build passes its editor smoke and fixed playtest-storage checks. Real-device listening, a real 46k user-library migration/backfill and real-machine tab feel remain unverified.

Known limitations: real 46k user-library migration/backfill, real-device listening, tab-strip feel, high-DPI/fullscreen/tablet layout and real large-mapset clone timing remain unverified. star rating and ar/od/hp/dr/ln/status have no data. Leftovers recorded in BACKLOG: BuildLibraryView() at src/LumaChart.Avalonia/MainWindow.cs:4516 is zero-call dead code and makes 0.1.28's --workbench-test/--library-028 suites stale (already ineffective before 0.1.37's e60a1a6); --mapper-033's Space-playback wait in MapperWorkflow033Specs:183 still times out intermittently without a root cause; length/bpm hint i18n keys are not added and still go through DescribeLibrarySearch→Status.

Final local package: dist/SimpleManiaEditor-0.1.38-Local-20261021-005605 (app/SimpleManiaEditor.exe); editor smoke 0.1.38 and fixed playtest-storage checks pass; Start-Editor.cmd points there. Version 0.1.38 is a local build only; no Release, tag or ZIP was created.

## 0.1.37 instant library cache, tab overflow and non-1x audio clicks

Library entry is now cache-only. ShowLibrary() no longer starts a directory scan on any of its three entry points: it calls an idempotent EnsureLibrarySnapshotLoadedAsync() that reads the persisted index (SQLite headers + JSON annotations), and the only full scan is the explicit 刷新索引 button (or adding a root/switching the creation directory). Startup also stops awaiting a scan. Watcher overflow (>256 paths) only sets a hint that the directory changed; incremental save/create/delete still update the visible rows immediately (delete regroups only the affected folders instead of stat-ing every folder on the UI thread). Last refresh time is persisted in a new index_metadata table and shown with the cache state. Warm path on a read-only copy of the real 46,599-46,606 chart index: LoadCachedLibrarySnapshotAsync 1940.8/1189.7 ms -> 1034.1/1036.4 ms (headers 929.8 -> 502.5 ms with parallel source-generated JSON, GroupByFolder 678.2 -> 367.4 ms with lazy SearchText/parsed-folder caching), re-entering the library 46 ms (74 ms forced full rebuild), startup to usable 46k list 2765 ms with the cache ready about 1.28 s after window init. First adding a new Songs root still scans, by design.

Document tabs overflow horizontally instead of being clipped. The strip scrolls with the mouse wheel (120 DIP per notch, only consumed while overflowing), shows 18x24 chevrons at both ends only while overflowing (one page 85% of the viewport per click, hold to repeat at 70 ms), keeps the active tab revealed after activation/creation/neighbour selection/resize, and preserves a manually scrolled offset. Long titles are ellipsized instead of hard-clipped, which also fixed two tabs losing their distinguishing suffix. 36 real tabs were opened and each was clicked through in headless tests; the editor timeline/library wheels are untouched.

Non-1x playback no longer clicks at every note. Root causes measured on synthetic fixtures: a transient SoundTouch empty read during segment flush was treated as end-of-stream, so the rest of the segment budget was zero-filled and the waveform stepped by up to 0.30 of full scale; the output silence guard also stepped its gain (muting sustained audio or un-muting borrowed audio in one sample). The provider now retries transient empty reads boundedly, decays the last frame over the same 3 ms fade instead of zero filling, applies raised-cosine fades at every segment join with a fade that waits for the first real sample, keeps attack boundaries at least 80 ms apart (releases still cut immediately so short bursts stay intact), and the output gain guard is retired from the pipeline because its muting was one of the click sources. Measured on a kick train over a quiet bed (ordinary material) at 2/1.5/1.25/1.1/0.9/0.75/0.5/0.25/0.1x: max sample step 0.006-0.014, versus 0.005-0.011 for the unsegmented processor and 0.30 before the fix. Digital silence between notes still shows a bounded 0.082 step once per note instead of 0.30; a steady tone is no worse than the unsegmented reference (0.34 versus 0.49 steps, 4 versus 16). --audio-026/027 (ten rates, bursts, stereo image, spectral purity), --audio-gate-036 and the new --audio-noise-037 pass. A read-only Albemuth MP3 onset/seek test now reads 206.68-207.70 ms against a raw 207.94 ms (0.1.36: 207.53-207.76 ms), so attacks moved at most 1.3 ms earlier and stay inside the 8 ms bound; the original SHA256 is unchanged. These are pre-device PCM measurements, not proof of subjective quality on real hardware; the remaining digital-silence step and any residual WSOLA character still need the user's ears.

Source incident and recovery: while wiring temporary playback diagnostics, the Lead agent read MainWindow.cs through a default-limited read (about 950 of 25,257 lines), edited that fragment, and wrote it back as the whole file. The 0.1.37 work was uncommitted and no git blob, editor backup, shadow copy or temp copy existed. Recovery decompiled the 0.1.37 assembly that had been compiled from the intact source (ilspycmd into artifacts/decompiled), rebuilt MainWindow.cs from the decompiled MainWindow type by dropping the 313 members owned by the 16 intact MainWindow.* partial files, re-appended the three helper types that lived at the end of the lost file (WindowsAvaloniaEditorInputPolicy, WindowsOnlineRoomEditorAccess, WindowsOnlineRoomPermissionPolicy), rewrote ILSpy's read-only collection helper types back to plain arrays, removed two decompiler state artifacts, and hoisted usings. Consequences: comments, the original formatting and any code inside #if LUMACHART_SHARED in that one file are gone (that configuration is not built in this checkout), and the file now starts with #nullable disable plus a warning pragma because decompiled code carries no nullability annotations and loses discard assignments. Treat MainWindow.cs as recovered code pending review; every other file is the untouched original. Evidence: artifacts/decompiled, artifacts/MainWindow.split.cs, artifacts/MainWindow.truncated.cs, artifacts/splitter. The recovered file compiles with zero warnings and zero errors under -warnaserror and the full suite below was re-run on it.

Validation: core specs 59/59; --mapper-034/035/036, --regression-030, --library-037, --tabs-037, --audio-026, --audio-gate-036 and --audio-noise-037 pass; the read-only Albemuth MP3 onset/seek run passes with the original SHA256 unchanged. --mapper-033's Space-playback wait in MapperWorkflow033Specs timed out intermittently on the same binary (a failure mode already recorded after 0.1.33); the flake happened several times in this session and also passed, so it is recorded here without a root cause and without being attributed to this batch. Final local package: dist/SimpleManiaEditor-0.1.37-Local-20261020-212306, built from the recovered source (editor smoke 0.1.37 and fixed playtest storage both PASS); Start-Editor.cmd points there. Version 0.1.37 is a local build only; no Release, tag or ZIP was created.

## 0.1.36 symmetric tails, musical pattern precision and source-aligned audio

SymmetricHoldTailResizeCommand plans source-tail plus reflected-head changes atomically, retaining paired IDs/properties and single-step undo. TimelineSurface.HoldTailSymmetry previews both without mutating the document and snaps only on release; same-column union/collapse/occupancy and locks are covered. Marquee and other drag edge scrolling now begin outside the viewport, never in an interior edge band.

BeatArithmetic uses BigInteger affine arithmetic, reduces before the storage bound, and retains an existing common subdivision when representable. Pattern capture conservatively restores native integer-ms quantization within 1ms on known common beat divisions, without modifying the source; deliberate off-grid offsets remain. JSON -> 2x scaling -> seventh-beat anchor placement is tested across 104.04/173.123/240 BPM. Previously saved imprecise pattern JSON is preserved rather than silently rewritten; recapture from its native source to recover musical positions. Track-local capture explicitly names the destination and main selection. InheritedTrackTimingService projects current and undo/redo snapshots together, keeps absolute song positions, and excludes independent references.

Root audio evidence: in actual WASAPI's pre-endpoint PCM, a quiet tonal bed plus attacks exposed the old WSOLA future-grain attack at ~791ms instead of 1000ms at 0.1x, while relative queue-delay keys were late. TransientAnchoredStretchSampleProvider limits each SoundTouch source to detected multiband attacks/quiet releases; absolute output budgets prevent cumulative rounding drift. Bounded past-only context prevents flush zero padding truncating short tones; SourceSilenceGuard still protects raw quiet regions. Removed empirical 14ms search padding. 1x bypass and pitch-changing resampling remain. This is streaming with bounded buffers, no whole-song PCM or new native dependency.

SourceTimedSampleProvider carries absolute source milliseconds through custom-key decoding/rate conversion and epoch guard. Native mixer resolves each voice on its render sample cursor/current rate rather than UI or estimated device queue delay. Slow prepare keeps old-epoch music/key scheduling alive; SourceCommitStarting invalidates only at actual source replacement, with an exception-safe commit flag. NonEOF short/zero reads retry boundedly and advance source-timed overlays only with actual music PCM; final partial PCM survives terminal silence. Native gate/barrier and alias-buffer tests cover these races and EOF.

Validation logs: artifacts/mapper-036-final036.txt (pointer symmetric tail/marquee, inherited timing, target capture, exact chain), audio-gate-036-tests.txt (commit races plus underrun), audio-036-final-quality.txt (ten rates, mono/stereo, exact stereo identity, block invariance, durations, bounded peaks, harmonic/nonharmonic purity, quiet bursts), audio-036-final-alignment.txt (real WASAPI pre-endpoint mixed PCM at 1/.5/.25/.1, about .15ms music/key delta including full 44.1k custom key -> 48k transport). Albemuth real MP3 read-only onset/seek test at five rates has raw 207.94ms, stretched 207.53–207.76ms in source time, original SHA256 unchanged. This is not an acoustic/screen latency measurement or proof of arbitrary-song subjective quality. Retain hardware acceptance and existing global calibration.

Final local package: dist/SimpleManiaEditor-0.1.36-Local-20261019-041124/app/SimpleManiaEditor.exe; Start-Editor.cmd points there. Packaged 0.1.36 runtime smoke and fixed-storage host checks pass. Core 59/59 and mapper .33/.34/.35/.36 plus .30 regression pass. One .33 playback wait timed out during concurrent DSP stress; unchanged isolated rerun passed, retained logs distinguish the two.

Only local Build-Local workflow; no Release/tag/ZIP. No real osu installation mutated. An experimental native stretcher was rejected and is not referenced/shipped; diagnostic artifacts are ignored. User maps, settings, skins, track manifests and patterns remain intact.

## 0.1.35 interaction hierarchy and selection stretch

User explicitly corrected the tool hierarchy: four base tools remain separate; pattern library is a peer of center symmetry under the new 排列与变换 group, alongside 拉伸. The permanent 放入排列轨 button is removed; capture remains within pattern-track actions. All three extension buttons have consistent full width / 34 DIP height. Selecting Stretch enters Select and updates its tool highlight; choosing a basic tool (including Select again) dismisses the frame.

NoteStretchService is an affine musical-time mapping using exact BigInteger fractions where representable, with bounded fallback. TimelineSurface.Stretch is a pure pointer preview: selected-note frame encloses visible caps, orange top/bottom grips resize against the opposite fixed boundary; frame body translates time and columns. Edges use current-grid pointer deltas, interior positions retain ratios, source LN and sound fields/identities persist. Release commits one EditorSession mutation; Escape during drag cancels preview, a second Escape/blank click/base tool exits. Capture loss, session/selection changes discard stale preview; read-only and owner locks still protect edits. A zero-duration selection translates but has no resize grips. Refuse collapsed LN tails rather than corrupting the source.

Board menus capture the insertion point before the next frame. Added tracks join that order (including hidden-track positions), save the order, and compensate horizontal pan so existing main stays in place. Right-delete now deletes a multi-selection only when the hit note belongs to it; unrelated old notes no longer cause a newer automatic symmetric selection to be removed. Interior-edge placement and LN-tail editing no longer seek; taps never auto-scroll, LN drags may edge-scroll only outside the surface. The previous 42-DIP interior edge band reproduces the reported lower-edge drift in isolated pointer tests.

Pattern endpoint key is Enter, freeing Space for standard transport even while a ghost exists. Text hints stay next to the ghost (two cached formatted layouts); shelf/status/tooltips/docs updated. CenterSymmetryService merges overlapping or touching same-column source/reflected holds into a single continuous union, with atomic undo and lock validation; differing columns or disjoint intervals remain pairs. Its preview uses the same union.

Validation: new --mapper-035 real pointer and visible-control tests cover upper/lower proportional stretch, immutable preview, body lane/time movement, sound properties, cancel, exact sevenths, undo/redo, read-only, deletion targeting, timed bottom placement, union and actual left-blank menu insertion with stable main screen position. .33 regression uses the track-local capture button and verifies actual Space play/pause during a ghost followed by Enter endpoint/key-repeat; .34 lifecycle regression and --regression-030 drawing/audio/symmetry pass. Core 59/59 pass; warn-as-error build is clean. Rendered stretch/toolbar/ghost-hint screenshots were inspected under isolated Temp fixtures. Final local build uses Build-Local.ps1, no Release/ZIP/tag request. Real user interaction and hardware remain human acceptance.

Final .35 local launcher targets dist/SimpleManiaEditor-0.1.35-Local-20261018-045012/app/SimpleManiaEditor.exe. Packaged native editor smoke (0.1.35 / Skia / 7K / SQLite-FTS / Git) and fixed playtest-storage verification pass. Final --mapper-035 includes selected-tool highlight and explicit basic-tool exit checks; isolated render fixture sme-ui-991faf437d774b30a59975a3df2a7e37. Space transport / Enter pattern endpoint workflow passes in sme-ui-b29fa11987e3419d9316acff8699136f. No real osu/user source files modified, no release publishing.

## 0.1.34 read-only lifecycle, workspace discovery and fast snapshots

User reported 0 left library charts, local-read-only "observer" permission loops preventing tab changes/close/app exit, marquee not following playback, missing pattern default names. User explicitly requested killing the hung processes: verified and stopped editor PID29052 and its owned host PID15876 only. No osu process stopped and no recovery files deleted.

Root causes: left pane used only the newest CreationRoot prefix, excluding six personal .osu files still present under old dist/LumaChart Workspace locations. Remembered-chart scans also used ReadChart directly and never persisted their headers. CreationRoot was not remembered on default startup, so versioned local packages changed it. IsWorkspaceChart now classifies indexed personal/non-protected/non-linked paths using cached roots/folder safety; indexed legacy files open through OpenManagedChartAsync in place, other external files still use import copies. Default root is remembered only if no preference exists. Old files are not moved.

Local library read-only IsLibraryReadOnly now bypasses implicit metadata parsing/apply on navigation; control refresh cannot create an actionable dirty draft. Permission notices become non-modal clone guidance for this local mode, not online-observer overlays. tests --mapper-034 exercise actual tab click, close preflight and whole-window close with forced invalid metadata; editing protection remains. LibraryReconciled prevents rewalking Songs on each return. LoadCachedLibrarySnapshotAsync runs JSON headers/annotations/grouping on a worker before reconciliation, fills missing remembered headers only, and uses cached times to avoid directory-stat calls per song. Real SQLite was opened read-only and backed up to a private Temp profile for a benchmark: 46,599 charts, six personal, 3199ms first snapshot / 2013ms warm, no full-directory scan. These are index snapshot timings, not whole app startup or first import. Original source DB/profile were not mutated by this benchmark.

Marquee anchors its start in beat coordinates and recomputes geometry on playback-clock changes even with a stationary pointer; selection membership capped at ~60Hz and final release is exact. Default pattern name uses source title plus selected beat span, capped120chars. --mapper-034 verifies editable/read-only marquee growth, legacy work listing/edit-in-place, invalid read-only draft lifecycle, and naming. Existing --mapper-033 / --mapper-031 remain regression targets. Preserve all existing changes and only build local 0.1.34; no Release request.

Final .34 validation: 59/59 core specs, --mapper-031, --mapper-033 and --mapper-034 pass. The .33 synthetic 20k-row fixture pauses its watcher only while injecting nonexistent synthetic headers, then restores it; real file-watcher reconciliation otherwise correctly replaces nonexistent rows. Final 20k UI run: 367ms / 15 materialized rows. Build-Local.ps1 produced dist/SimpleManiaEditor-0.1.34-Local-20261018-013654/app/SimpleManiaEditor.exe and updated Start-Editor.cmd. Packaged editor smoke (0.1.34 / Skia / 7K / SQLite-FTS / Git), official-host fixed-storage verification and native settings-only entry all pass. Native settings fixture: artifacts/official-playtest/run-15cb8657b27044d587a63940fb726cd3. No editor/host process remains running after tests. No release ZIP, tag or GitHub Release created. Real hardware mapper interaction still needs human acceptance.

## 0.1.33 local mapper browsing and musical patterns

Windows library now uses MainWindow.LibraryBrowser.cs: editable workspace left, selected read-only index right, independently debounced worker searches and virtualized rows. DesktopChartLibraryService excludes missing Mode (defaults to standard); header_parser metadata revision 2 invalidates only header/FTS tables once, preserving identities/annotations/backups/history. A 20k-row UI fixture materializes about 15 rows; partition/layout/far-scroll measured 350–390 ms on this machine (not real Songs cold-scan timing).

DesktopDocumentWorkspace.OpenReadOnlyAsync skips editable-copy/asset-localization and persists IsLibraryReadOnly. Session note/timing/metadata mutations, save and save-as are blocked; original audio resolves via source path. Restore retains protection. Explicit Clone uses verified whole-mapset copy to a unique workspace directory; copy-out may run with osu open, but moves and writes into Songs still require it closed. Read-only tabs show a badge and a clone entry on the left rail.

Reference selection uses existing indexed headers and worker FTS, no file chooser. Reference tracks have their own density/time index and ms or mm:ss jump, which disengage scroll sync. EditorTrackKind.Pattern is appended (keeps existing enum values), is an independent writable track even when the source is read-only, and participates in workspace persistence. Left rail can add the current selection to it; the track can name/save its contents.

PatternLibrary stores bounded, atomic, plain JSON under profile/Patterns, relative musical beat strings and lanes; no music, source path or skin. Invalid files remain intact and are skipped with warnings. Shared exact rational scaling keeps common subdivisions such as sevenths on grid. MainWindow.Patterns.cs owns shape/search shelf and capture. Pending paste supports lateral movement, Ctrl-wheel scaling (1/64–64), Space pins start then endpoint/commits, Escape/right-click cancels, one command per placement. Default scale is one source beat per target beat. PatternShapeSurface and MapperDiffSurface use solid equal-width rectangles for taps and holds.

Targeted --mapper-033 checks original/read-only/restart/save guards, clone while game runs, index migration preserving annotations, relative BPM/scaling/atomic undo/cancel, 20k virtualized row recycling, actual capture/name/save/shelf/place UI, indexed reference selection and navigation. --mapper-031 and --regression-030 cover prior workflows. App version is local 0.1.33; use Build-Local.ps1, no Release/ZIP/tag without a new explicit request. See MAPPER-WORKFLOW.md. No real osu files were modified for synthetic tests.

Build restore also detected GHSA-qhrr-8q5h-9q3h in transitive MessagePack 3.1.8 (https://github.com/advisories/GHSA-qhrr-8q5h-9q3h). Official host now pins the compatible patched 3.1.9 and corresponding lock entries; audit remains enabled. Native runtime/storage/settings/gameplay verification must pass with this pin.

Final local launcher: dist/SimpleManiaEditor-0.1.33-Local-20261018-005359/app/SimpleManiaEditor.exe. Self-contained smoke reports 0.1.33 and passes Skia/7K/SQLite/FTS/Git; --verify-storage passes. Official native settings pass with MessagePack 3.1.9; tested host/MessagePack DLL hashes match final package. Final native GPU 7K argon gameplay/perfect autoplay/archive-retention passes in isolated synthetic run-5365c4e0222d4a61a7da8ca6b48276de. Final --mapper-033 includes key-repeat protection for Space endpoint selection and completes with 20k partition/layout/far-scroll 349 ms / 15 materialized rows. Prior --mapper-031 and --regression-030 pass; core specs 59/59. No tag, ZIP or GitHub Release created. Existing diagnostic scripts/MAINTENANCE.md changes from the previous crash investigation remain preserved.

## 0.1.32 local maintenance and isolation

User policy: do not create tags/GitHub Releases or upload distributions without an explicit human release request. Default to scripts/Build-Local.ps1, which publishes a fresh local directory and updates Start-Editor only after native/runtime checks. Version 0.1.32 is a local revision, not a Release.

Actual cleanup 2026-10-15: 47,005 generated files / 43,725,817,834 bytes deleted, 13,915 retained hashes verified, zero failures. New root Clean-LocalBuilds.cmd and scripts/Clean-LocalBuilds.ps1 regenerate a current plan on every invocation. Planner discovers live/demo runtime from launchers, protects tracked files/data, checks unique archive content, and uses per-file native PowerShell deletion. tests/Cleanup.Tests.ps1 covers rebuild after preview and unique archive recovery.

Stable repair warning remains unconfirmed: original E:/osu! update log says NoUpdate; isolated published editor prewarm/close plus real-skin GPU gameplay produced zero events across full original tree, zero hash/time/attribute differences in 190 core/selected-skin files. Private report under C:/Users/Administrator/AppData/Local/Temp/sme-isolation-0f7abc9336c44fbfafc457e064ae32af. Do not claim the symptom fixed.

Found a separate real boundary gap: OsuStorage follows its storage.ini FullPath without the editor's guard, and official InstallationSettings permits external directory migration. FixedPlaytestStorage now wraps only editor-owned storage, ignores storage.ini, blocks path/junction escapes; standalone migration button disabled/hidden on official settings load. Pure --verify-storage host test covers external sentinel protection, redirect, absolute/traversal/move/delete/junction cases. Official settings GPU test passed after handling lazy section creation: restrict InstallationSettings before its first visible frame, not just SettingsOverlay.LoadComplete. Earlier overlay-only hook was insufficient and the native test caught it. Real-skin GPU gameplay/190-file hash+whole-tree observer also passed on the first local revision. Current Start-Editor targets dist/SimpleManiaEditor-0.1.32-Local-20261015-152451/app; pure storage and editor native smoke pass. Final local revision also passed real-skin GPU gameplay and full original-directory observation with zero events/zero differences: private fixture C:/Users/Administrator/AppData/Local/Temp/sme-isolation-f4600129d29a4544bd8e7ab5e9839522. Post-build cleanup removed 2,106 more generated files / 1.37 GiB, 15,348 retained files verified; E free42.85GiB. Two cleanups total42.09GiB. No Release/ZIP/tag created.

Restore now reports new SignalR 10.0.12 advisories (GHSA-334f-vvp4-7g6p and GHSA-86xp-94pg-8r6r); host pins patched Common and MessagePack 10.0.13, keeps auditing enabled. Run net10 host tests with the D:/CodexBuildTools/SimpleManiaEditor/sdk/dotnet.exe runtime, not the older C:/Program Files runtime.

## 0.1.31 mapper library and local Git

MapperLibraryStore is SQLite WAL/FULL, FTS5 trigram plus short-CJK fallback, independent identities/personal/header tables and atomic readable library-personal.json backup. Headers are lazy-loaded on a worker; watcher notifications coalesce 750ms, text search debounces 180ms and runs off UI. Native package dependencies: Microsoft.Data.Sqlite 10.0.12 + LibGit2Sharp 0.32.0, with editor publish notices and packaged native smoke checks.

MapsetTransferService is the only explicit Songs writer. Preview whole-mapset SHA256 manifest; default copy, move backs up source, durable JSON states, no target overwrite, reject game-running and links, rollback refuses newer edits, recognizes truncated staged prefixes and resumes partial rollback. VersionStore uses bare per-identity Git under profile/Versions, editor.mc + native chart.osu + optional deduplicated resources; snapshots are not persistent command journals. Restore is EditorSession.Mutate with pre-restore commit. Visual diff shares actual ms axis; metadata/timing text plus resource-parent listing. MainWindow.MapperLibrary.cs owns task dialogs; sidebar settings do not duplicate these flows.

Bench: 200k synthetic entries index62.3s/287MiB, reload2.91s, common combined search15-70ms, broad200k-result creator1.4s. Actual50k tiny.osu cold+persist21.66s, warm (headers already loaded)2.16s/zero reparses. --library-benchmark-031 is explicit heavy workload; --mapper-031 targets storage, faults and visible UI. Tests use Temp fake Songs, never actual user sources. Full workflow/limits in LIBRARY-VERSIONS.md; branch/merge/remote, fine-grained journal and all-field diff remain backlog.

The old Temp SDK cache vanished externally. Persistent SDK 10.0.401 now at D:/CodexBuildTools/SimpleManiaEditor/sdk/dotnet.exe, NUGET_PACKAGES=D:/CodexBuildTools/SimpleManiaEditor/nuget, DOTNET_CLI_HOME=D:/CodexBuildTools/SimpleManiaEditor/cli. Runtime runner C:/Program Files/dotnet/dotnet.exe with --roll-forward Major. No SDK included in repository/distribution.

Validation: 59/59 core specs; full headless suite passed on rerun (first run hit an existing 45ms visual-clock sampling assertion; threshold unchanged). Final --mapper-031 includes actual dialog annotation/batch/preview plus restore/undo, new game-root rejection, truncated-stage and multi-phase fault recovery, native git fsck and readable backup rebuild. Package self-contained smoke verifies Skia/7K/SQLite/FTS/Git. Git object files are read-only on Windows: payload test now clears attributes only inside its verified owned Temp GUID directory before removal. Final clean ZIP has 981 manifest files, 282816859 bytes, SHA256 a808f3abc022f7be2c09b82bb866a009ae9f9537d1c687f11a507123daf37012. No real user library/audio or osu installation was modified by these tests.

## 0.1.30 continuous rate and shared navigation

Engine rate prep targets a future queued tail; ClockPlaybackRate/Position model the remaining old queued segment before the new rate. Main frame uses pump.PlaybackRate. Native PCM clear must use typed writes (WaveBuffer float[] view may have byte[] runtime type). Sync track wheel forwards to main, while note snapping remains in the track Timing. Source replacement expands original exact symmetry pair before adding new reflected notes. --regression-030 isolates these checks; full suite includes them.

## 0.1.29 public repository

https://github.com/Ist1na07/SimpleManiaEditor · main. Curves remain .28; double-click library and GitHub releases/branding added. Release packaging and tests use SDK 10. Original source copyright retained with Ist1na in root and src LICENSE.

# 0.1.28 当前基线

UI scale schema2新增AutomaticUiScale，Recommend基准2560x1440，最低55%，ScreenFromWindow有效DIP。旧schema1的100%视为自动，其余手动保留。正常启动2300x1300×比例按工作区限制，旧指针UI规格强制100%因为其历史坐标辅助未乘缩放；新LibraryDisplay028Specs单独验1080/1440。

AvaloniaWorkspaceController在当前CreationRoot外建立引用资源副本，核心DesktopDocumentWorkspace仍允许泛用外部原件，但保护整个osu安装及Songs。外部非游戏单文件绝不递归复制父目录；游戏mapset保留子资源，跳过reparse与游戏程序/数据库/配置。OsuFileSafetyPolicy逐路径组件解析junction，导出/删除/资源目标/缓存等共用；官方host链接同一文件并验证DataRoot/请求路径。未触碰用户osu实文件做实机复现。

EditorLibraryCatalog只存roots/charts路径；后台扫描合并CurrentRoot/CreationRoot/只读roots/已知files，头部缓存；ViewModel.LibraryUpdates增量更新曲目并合并扫描期间的新变更。来源筛选在内存中，不在每项上做文件系统探测。切换创作目录仍保持旧收录。品牌按钮ShowLibrary暂停音频并刷新磁盘。路径不可用保留索引其余来源并给提示。

新增--library-028用于独立UI/存储回归，完整scripts/Test.ps1也包含该规格。构建分发使用Package-Distribution.ps1的新空目录，根项目Start-Editor直接启动新分发app EXE以保留Preview配置；分发根入口用独立Distribution配置。Package旁Maps默认；已选工作区不自动搬动；旧Distribution本机Maps加链接后切换新默认。发行包不得包含用户目录/曲目/皮肤/设置。

# 0.1.27 历史基线

本轮定位到SoundTouch长粒度的局部时间扭曲，并非MP3播放器/频谱使用不同decoder。用户原曲只读207.94ms首声，旧.1x119.06ms；新原PCMquiet envelope保护输出207.53ms，从100ms seek约207.76ms。SoundTouch24/28/12 full search；quick search及极短grain虽快但非谐波音色回归失败。慢倍率原PCM前置半seek窗口14ms，并以原样本数/倍率限制总时长。该14ms是本DSP搜索策略，不是声卡/谱面/检测器的全局延迟。

SourceSilenceGuard只限制原PCM<-60dB区域，1ms桶4096环形记录，双声道同gain；原PCM始终独立于前置静音，避免时间基准被补偿污染。它不保证连续音乐内部所有瞬态逐采样定位。1x绕过stretch与guard。AudioVisualizationService改每声道FFT功率平均，防止反相抵消；首帧中心/音频范围保持原有定义。

EventTimeRuler有音频时严格0..audioDuration，异步加载结束按(tab,source,duration)刷新；跨tab校验源路径，避免旧snapshot赋错时长。设置外观/快捷键是直接StackPanel，删除无用搜索自动展开逻辑和旧Expander测试。

新增Audio027Specs：8倍率多段延迟脉冲、读块一致性、antiphase频谱；--audio-song-027只读用户样本首声/100ms seek/hash。Audio026Specs不再以同算法reference等同当成音质指标，增加非谐波四音频谱。TimelineSettings027Specs真实导入空谱音乐、全曲范围与直接设置分类。

# 0.1.26 历史基线

编辑保音高使用所有倍率单SoundTouchWaveProvider（100/28/20ms、高质量搜索），1x旁路；删除SMB多段和每段soft limiter。旧0.1x已知三音和弦实测带外能量约左43%/右76%，反映单正弦测试不足；新Audio026Specs覆盖10倍率mono/stereo、复杂扫频/非谐波/短噪声、同声道与静音声道、FFT、EOF、512/2048/8192块不变性，并逐样本对照未预热SoundTouch，确保没有因预热丢失开头。PrimedMusicSampleProvider保留20ms首块；NAudio WaveBuffer以byte[]重叠视图提供float[]，必须用Buffer.BlockCopy而非Array.Copy（真实device-path integration已复现并修复）。

WindowsAudioDeviceEngine所有Seek使用独立decoder+PreparedMusicPipeline；SetPlaybackRateLocked同样准备新的倍率链后在transport envelope读边界swap，保留原输出与Playing状态，不并发修改Tempo。旧source/stages仅交换后dispose；取消验证在建链后执行，取消不改旧流。去掉已无用途的rate/phase字段；PreparedMusicPipeline统一预热包括1x decoder。SeekPerformanceSpecs扩展多倍率取消/设备复用，benchmark另测低于0.3x连续2s/其余500ms持续播放，避免只测首块缓存。

MainWindow.Track相关配置tuple变化时一次full SyncTrackViewports；播放SetProgrammaticPlaybackBeat仅AdvanceTrackPlaybackViewports，缓存metric物理锚，波形/频谱SetPlaybackBeat推进。主尺寸、zoom、tracks变化仍full同步。不同BPM/offset百万分拍时间投影仍正确，独立轨不受主播放影响且取消同步清active。TrackPlayback026Specs测500帧无full调用，验证配置变更兜底和布局/滚动语义；测试环境light500约0.36–0.41ms、full约1.02–1.03ms，不代表GPU Present或整体帧率。

BuildAudioTransport把现存RuntimeStatus/RefreshDevice真正挂入音频设置，移除未挂载的重复play/stop/rate布局容器。SyncFixedPlaybackRate缓存当前选项倍率，相同值不重建ItemsSource，打开自定义倍率选项不受20Hz轮询扰动。AudioSettings026Specs实际可见树、输出重建、自定义1.03x打开选项轮询和截图验证。

59核心规格、完整headless UI、10倍率实际DSP和压缩48k双声道跳转/持续输出检查通过；历史Avalonia.Specs仅构建兼容验证，不声称整套旧行为规格通过。官方游戏逻辑未改，宿主仅版本更新。实机音质/校准/180fps仍未验收。具体基准见performance/AUDIO-PLAYBACK-0.1.26.md。

历史记录：

# 0.1.25 当前基线

校准跳转：连续暂停/seek压力测试复现DispatcherTimer启用但不再提交的pending状态；提交改为Task.Delay(12ms)+UI单次post，独立schedule generation取消旧回调，正在执行时由finally调度最新目标，维持首次事件启动/连续滚动合并。

Timeline_CurrentBeatChanged仅在音乐播放/待继续音乐seek时用AudioTime逆变换；暂停导航是正常歌曲时间。记录校准后的人工目标供暂停继续逆变换。CompletePendingEditorSeek重置visualclock，并在100ms内绕开跳转前的高频缓存；恢复首帧也读取新clock。播放滚轮的BeatToMs/MsToBeat采用1m精度，避免默认分母造成的偏差。真实decoder/DSP+SimulatedAudioOutput覆盖带±200ms校准的小滚轮、0.1x跳转/暂停继续显示连续性。

0.1x：保留四段pitch恢复，但仅低于.125时FFT2048/overlap4，其他速度保持4096/8；Prime依据实际窗口裁掉对应延迟。48k mono/stereo440Hz与连续有限输出通过；同机双声道Prime旧约204ms、新25–50ms，1s输出旧637ms、新196–350ms，负载波动，不是硬件音频呈现基准；真实歌曲音质待验收。

AudioCalibrationWindow使用独立WindowsAudioPlaybackService和1x规律1200ms click，跟相同音频输出clock画移动光点；相位范围±600ms避免±500ms校准周期歧义。按钮±1/±10、slider、试听Space、使用/取消；窗口关闭停止/释放输出及删除随机临时WAV。不使用点拍时间推断音画偏移，避免人体反应延迟。不声称客观1ms自动测量。主窗口入口先取消seek/暂停音乐和silent clock，再打开modal；确认才写global calibration。

NoteTextCodeService.Parse分离解析/写入。NotePastePlacement纯预览保持相对拍与LN长度、允许有限横移；Commit预检权限/保护区、exact重复省略、一笔undo。TimelineSurface.Paste共享主轨/草稿预览绘制和点击状态。MainWindow.PastePreview按hover/点击选择目标，支持跨轨拖影，Esc/右键取消；不Focus造成底板跳动，异步clipboard验证tab/session/serial。文本导入不折叠，属性应用挪回属性区并命名应用属性，代码应用为主按钮。草稿转主轨用原始ms代码，清空用Delete原子命令。Draft新建clone主文档再清notes，旧draft在工作区载入时补native标记；不改源歌曲文件。同步draft的ZoomChanged同步main，非同步仅本轨。

59核心规格、完整UI、连续3轮seek压力测试通过。UI025新增鼠标/跨轨/控件/PCM刺激测试；历史即时Ctrl+V断言改为延迟点击提交。官方宿主仅版本更新，游戏逻辑无修改。

历史记录：

# 0.1.24 当前基线

选中/错误装饰 DrawNoteStateOutline PushClip+Deflate penhalf，Tap/LN外形不扩张；预览同样内部高光。audio side SetViewport/SetPlaybackBeat不再clamp0，TimelineAudioBackgroundScene波形/频谱不再按beat0剪，按真实ms0-duration裁剪，投影百万分拍。编辑器rate全链最低.1：UI/transport/service/device/settings；phasevocoder .1四级，其他极慢3级，级数变化重建设备链，Prime按实际级数扣延迟；440Hz实测。试玩单独preset .25–1.5且旧越界回1x。

Settings Appearance/Shortcuts默认展开。Timing.OffsetHere更明显全宽高亮；Timing.Offset.Nudge.-10/-1/1/10用当前offset草稿立即nativeApply，保留歌曲位置和绝对notes。Global音画校准单独editor-presentation AudioVisualOffsetMilliseconds（±500）；只SharedMusicClock画面ms += walloffset*rate，不改文件/键音/官方。保留上次calibratedCursor的tab/time/rate/offset，暂停继续将其逆变换到audio time，不随新补偿累计seek。人工导航位置不匹配历史cursor则按歌曲时间处理。音频category有Input/Apply/Slider/Reset，默认0。

TempoOffsetBiasSpecs用已知PCM起音/衰减正弦和ExactRateEnergyReducer测44.1/48kHz，237->240、475->480；偏差3/5ms，未确认固定10ms，不写死补偿。还未测声卡/屏幕实际感知延迟或所有压缩格式。新增024真实DSP与可视化/Offset按钮/校准持久化及暂停逆变换测试；历史UI默认折叠断言更新为展开。

历史记录：

# 0.1.23 当前基线

旧缓存 Lament 的 timing.UnknownData._sme_osu_object 为 line=0,... / time=475，而当前 Delay=-475。0.1.22 阻止新损坏但不能修复已经损坏的基准。本版 HasStaleTimingRows/IsStaleTimingRow 比较 raw row 与 baseline；Beat0 严格，其他点容忍导入时 <1ms 的量化误差（保留原始 timing 精度）。序列化在基准陈旧时重建 timing，即使 signature 已匹配；合法未编辑源保留原文。实际用户 cache 只读导出 `.osu`/`.osz` 后，官方 ppy 解码器均确认475ms/104.04BPM/190notes，原源文件/缓存hash不变；审计工具及导出仅在 artifacts/offset-audit-023。

Playtest 快捷键catalog/router新增F5，TextBox中也可触发，旧自定义冲突保留。CenterSymmetryService.ExpandDeletion按完全相同lane/head/tail匹配，保护区预检，无永久链接；右键/Delete/擦除/轨道与剪切统一，剪切复制整对。NoteYAtBeat只调整绘制为line upper edge，不改stored beat和PointerToNoteBeat。

MainWindow.SettingsNavigation.cs固定搜索/4列分类导航/单内容滚动区，构建一次控件，7类：编辑、操作、外观、音频、试玩、存储、通用。外观从编辑吸附区拆出，当前网格设置重复入口隐藏但同步控件保留。字体16标题/13选项/12辅助；搜索匹配类别/标签/关键词并临时展开，清空恢复，分类滚动位置会话内保留。保留AutomationId。新的023实际指针/分类搜索测试与旧工作流导航更新；57项核心规格增加单谱/谱包旧cache回归。

历史记录：

# 开发交接摘要

## 基线

0.1.22：EditorTrackWorkspace v1 增加可选 MainViewBeat/BoardPan/BoardWidths/ViewZoom。真实播放位置记入 sidecar，恢复仅在工作区切换时执行；Rebuild 调用 ApplyBoardOrder。关闭标签/正常退出保存已完成的轨道手势。Combo caption 根因是 Localization 直接写 TextBlock.Text 覆盖 Binding，改 SetCurrentValue。导入音乐移除自动分析；Timing.OffsetHere 用当前绝对 ms 移动首红线、保持音符绝对时间。OsuSourceEnvelope 的 raw-row time 基准不随重锚改写；原生导入不再添加 synthetic lead-in，先标 native 保留负拍；旧缓存只迁移未编辑 synthetic record，明确编辑过的旧 offset 不猜测。新建最小模式 Timing.Volume 0（不是 Note.Volume 0），canonical osu serializer 也应用 Settings。LN preview millionth beat 连续，release 才 snap。官方宿主 Windows 默认 SDL2 / 禁 menu mnemonics，启动与 resident idle 键消费，重复/释放成对；verify 增加 loading Space + gameplay Space 检查。核心57项、完整UI与三次实际GPU试玩通过；用户真实提示音验收待补。

历史基线：

0.1.21：默认 osuMinimalMode 持久化在 editor-presentation.json；隐藏 hitsound/SV 的编辑 UI、轨道与命中标签，完整模式恢复，未删数据。分组暂时屏蔽，旧文档数据和服务保留。ChartExportDialog 仅 osu，OD/HP 从文档传入，无重复表单。TimelineSurface 分离 snapped-head replacement 与 visual deletion/middle hit；红线在 DrawNotes 前；Symmetry 上下边只 Select。NoteTimeFlipEditingService 复用有理数反射、交换 LN 头尾和单次 Mutate；Ctrl+H/G/B 可自定义。Overview range 共享 DisplayEndBeat（含音频/书签），标题两轨同高，标记和 ruler 右键删除。滑条 thumb 连续，track-click snap，倍率/缩放 piecewise interpolation。56 核心及完整 UI/便携包检查；宿主逻辑未改。

历史 0.1.20：精确播放位置在每次可视更新同步到工作区，避免编辑刷新回跳；原生负拍视图保留。中心对称入口为快捷区两点/框边操作，CWT 保留每会话范围，移除旧数值弹层/sidecar；occupied mirrored head 跳过。NoteTextCodeService 使用原生音符行、毫秒位移、原始 sample 信息与一次撤销；系统剪贴板 async 路由捕获目标会话/serial，第三方文本优先，混合 Effect 继续保留内部剪贴板。

历史 0.1.19：`CenterSymmetryService` 提供精确有理数反射、局部有序列、LN 端点交换、目标索引和冲突预检。WindowsProEditorCommand 的可选 symmetry 参数将主键/被替换键/伴随键放在一份 Mutate 历史；不建立永久键间链接。TimelineSurface 新 partial 绘制范围与预览；左栏弹层、F6 catalog/router、每图/轨 sidecar。F6 新默认遇旧自定义冲突时留空。工具设置在 LocalDataRoot/drawing-tools，只读引用禁用，CWT 避免关闭会话后滞留文档。54核心、完整UI与最终工作台回归通过，宿主只版本号变化。默认关闭，性能按0.1.18限制描述，不声称180稳定达标。

0.1.18：默认 osu 导入从 Preserve 切换 Calibrate；补齐 5/7/9/10/64，红线/对象百万分拍，保留原始源封套。`_sme_osu_grid_calibration` 仅存编辑器缓存，Timing 重锚沿用校准；未编辑保存仍原文。`NoteGridSnapService` 单次 Mutate，负拍/LN/保护/只读/undo，短 LN 不塌；UI 设置内选中/全部入口。窗口 Tunnel 空白点击释放文本焦点。默认 WGL→ANGLE→software / RedirectionSurface，兼容选项持久化在 editor-presentation.json，重启生效，启动环境覆盖 SIMPLEMANIA_RENDERER。高采样线程最多240Hz单pending UI派发，暂停/试玩/关闭停止；timeBeginPeriod/endPeriod成对。结束范围按tab/document/serial缓存，ruler文本和globaldifficulty屏幕行缓存；同步投影精度提高。53核心及完整UI通过，真实窗口测量见performance/EDITOR-RENDER-0.1.18.md；稳定180/low180未达标，不得称已解决全部卡顿。官方宿主仅版本号变化。

- 工作目录：`E:\Files\Developing\SimpleManiaEditor`，Windows / PowerShell。
- 用户负责功能和真实 osu!/音频设备验收；代码、测试和便携包由助手完成。不要反复请求已授权的开发确认。
- 用户允许调整 Python/Web 技术栈，优先复用成熟代码。当前为 MIT 的 Luma 客户端改造，C#/.NET + Avalonia。
- 最新交付 0.1.25；0.1.11 基线提交 `0b536b9`。源码位于 `src/`。
- 原生 `.osu` 源资料保真、外部修改检测、保存前备份、恢复工作副本已实现；完整 command journal / 版本树仍未完成。
- osu 键鼠方案默认；网格内部分母是每四分音符拍的切分数，osu 1/4 = general 1/16 = 120 BPM 下 125 ms。不要再把内部网格盲目乘四。
- 编辑播放常规 seek 复用设备；0.25x 保音高采用后台准备。性能数字仅为模拟输出引擎时间，不能称为硬件端到端延迟。
- 附加草稿/参考/hitsound/密度/实验负荷轨已交付。轨道资料为独立明文 JSON/.mc，不进入主谱 `.osu`。官方/Sunny 难度未接入。

## 0.1.5 已完成的请求

1. 回答上下文压缩入口：官方桌面端文档有 `/compact`、`/status`，命令可用性依环境；自动阈值配置 `model_auto_compact_token_limit`。不擅自改 Codex 配置，不声称已执行压缩。
2. Songs 原件疑似引起 osu 警告/修复；等待用户可选提供提示原文，独立推进安全创作副本和写入保护。
3. 倍速默认保音高。
4. 修快捷编辑图标/边框错位。
5. 修同步轨道和波形/频谱不同流速，特别是残留 480 缩放上限。
6. LN 尾只在精确尾部区域触发调整。
7. 上方命令和倍速/网格合成一栏。
8. 所有轨道统一底板地位；density/diff/音频面板也在底板内，支持拖动顺序；底部横向滚动条控制整个底板。新增轨不自动改宽或跳滚动位置。
9. 主轨/草稿/对照复用相同轨道容器与绘制/放置逻辑；波形频谱不再限制在主轨范围内移动。
10. osu 点击两个格线之间始终选时间上前一格，和 note 厚度无关；略减默认厚度。
11. 仅底板空白处右键显示添加菜单；轨道内部不显示。
12. 菜单显示后第一次左键仅关闭，不放键。

## 0.1.6 修正

- 用户更改吸附要求：osu 放置和 LN 拖动取鼠标最近格线，替代 0.1.5 前一格；鼠标时间不减去按键厚度。
- osu 的绘制中心为实际时间 Y 减去半个按键高度，确保底边表示时间；使用像素偏移，避免不同 BPM 的参考轨投影放大偏移。
- 精确面尾拖动命中以实际时间底边为入口；空白右键取消 Note/Timing 选择，底板空白也取消各轨选择。
- 底板未处理滚轮交给主时间轴；横向条 Visibility=Visible、AllowAutoHide=false、高 24 DIP、柄 12 DIP。
- ZCode 窗口可发现，但两次 Computer Use 捕获返回 FrameArrived/window capture timed out；本轮未提交外部模型任务。

## 0.1.12 常驻试玩与资源缓存

右侧 Settings 首部 BuildOfficialPlaytestSettings 显式按钮替代右键菜单，并显示预热状态。OfficialPlaytestProcess 是编辑器所有的单实例游戏监督器，Opened 异步预热，Closed 关闭；JSON ResidentMessage 通过继承管道通信。宿主 EOF/shutdown 退出，异常退出下一次重建，AllowSetForegroundWindow 仅授权自己的子进程。后台隐藏/静音/停音乐/10 Hz，实际试玩恢复帧率和音量。PlaytestGame 用轻量 OsuScreen 加载官方 PracticePlayer，保留重试与高性能会话，移除 PlayerLoader 私有固定 650+500 ms 转场。TryTransferTrack 复用音轨。OfficialPlaytestPackageCache 监控源/工作资源目录，复用压缩资源并仅重写 .osu；官方导出可选 CaptureNormalizedDocument 回调保留路径规范化映射，测试覆盖冲突、新编辑、切片、资源更新和原件不写。Protected 根和监控异常禁用缓存。原生 GPU 常驻测试 scripts/Test-ResidentPlaytest.ps1；实际监督器/缓存/异常重建端到端入口 UiSmoke --resident-process-test <self-contained host exe>。首次预热/素材不能声称全部秒开；重复热启动实测数百毫秒，报告位置见 RELEASE-0.1.12。

## 0.1.11 官方宿主

用户要求右下角可拖动滑条卡片，质疑自制皮肤渲染并授权优先官方客户端。MainWindow 唯一顶部行 + BuildQuickValuesPalette 三滑条竖排，旧卡片拖动/保存/夹取复用。OfficialPlaytestLauncher 用独立测试副本 OsuFullPackageExportService 生成 .osz，将音频/资源/起点/倍率和迁移设定写 PlaytestRequest（独立 net8 DTO）。SimpleManiaEditor.OsuHost 为 net10，官方 NuGet 2026.916.0 + Framework 2026.914.0，不与 Avalonia 同进程；OsuGame/Player/官方 skin 与 Ctrl+O 设置真实运行。首次迁移，显式 cfg revision 一次性应用，随后不覆盖官方配置。源皮肤四份包括 Wilben 已实际导入，截图仅在 artifacts，不分发用户图。RetainedImportTask 禁止官方导入后删除归档。PracticePlayer 无失败/上传，时钟当前位置、Tab 自动演奏和 Ctrl+P，回放截取保留空帧避免第一下漏判。旧窗口仍留在源码用于历史测试，实际入口没有降级。新 Test-OfficialPlaytest.ps1 使用真实 GPU、自动退出、隔离数据，可对便携 host exe 验证。官方资源/字体/native 许可与窄范围 NuGet advisory 记录见 notices/UPSTREAM；公开发行前重新核对。

## 0.1.10 皮肤工作流

调整控件并入唯一 Editor.Toolbar.Fixed，移除独立调整 Expander，保留一个 Editor.Toolbar.Playtest。新增 OsuSkinLibrary 明文持久化目录；SkinSelector/Preview 与皮肤、游戏、按键三页，移除 Note 外观控件与旧外观值对渲染的影响。SelectSkinAsync 先持久化选择再异步加载；generation 只允许最后选择应用；图像不依赖声音解码成功；settingsUiReady 在初始化完成即打开，不丢音频等待期间设置。父窗口全屏不覆盖已保存的试玩窗口选择。OsuIniDocument 支持同一 Mania 多 Keys、扩展名动画与旧 K.ini；按官方顶部锚定 LN 尾。SkinWorkflowSpecs 测关闭加载、反复重进、快速切换、损坏声音与失效目录；固定工具栏与 LN 像素检查补齐。没有收到异常皮肤实际路径，不声称已对其验收。

## 0.1.9 试玩与皮肤

用户允许改为 lazer 界面/落速。新增 OsuIniDocument、OsuManiaSkin（后台纹理、HD/动画/颜色/预算/归档）、OsuPlaytestImport（cfg 键位/落速/皮肤/全局键）；PlaytestSurface.Skin 与 PlaytestWindow.Presentation 分离游戏渲染和 UI/导入。默认全屏，F1 设置浮层，Esc 暂停，F11 全屏，F3/F4 1–40；官方公式 11485/speed，视觉 402/480 标定及倍率补偿。stable 判定保留。鼠标按住时绘制预览保留。测试含皮肤/色光/声音/配置隐私/.osk/100k/浮层不改画面大小。范围与尚未完成的完整 HP/Mods/谱面 HS/特殊皮肤见 PLAYTEST.md；不要声称嵌入整个官方游戏。

## 已定位的原因

0.1.8：用户明确要求 playtest 对齐 osu! stable。PlaytestRuleset.OsuStable 为 Windows 设置默认，LegacyCustom 保留。StableManiaRules 轻量移植 ppy/osu 固定提交 Classic/native/ScoreV1 窗口，官方 wiki 的单次 LN 头尾合并（非 lazer 1.5x 松手/双判定）、非对称晚侧截止、准确率及无 Mods base/bonus 分数。PlaytestSession.Stable.cs 持续 hold/regrab/timeout 和单次计分；ManiaScrollMap 连续毫秒积分 BPM/SV、按时长主 BPM 归一化。绘制头尾底边/按住 head/clipping/7K 列色，误差条和判定反馈。Render 与输入读取实时音频时钟，偏移按实际毫秒乘倍率，frame miss 使用相同手动时钟。真实 Avalonia 7K Space、重复、松手和窗口/偏移、profile/persistence、100k 可见区回归；核心 50/50。

上游参考 `third_party/osu/UPSTREAM.md`，源码 `ManiaHitWindows.cs`，完整 MIT 许可 `src/licenses/osu-MIT.txt` 随包复制。不是嵌入完整游戏；HP/Mods/replay/完整 combo tick/外部 skin/native chart hitsound bank 待补，像素流速不是 stable 1–40 值，硬件延迟不能用 headless 结果代替。

0.1.7：TimelinePlacementGeometry 的 Tap 永远返回单键，Hold 零长度也返回单键。osu 绘制工具左键替换（整块尾部优先调整）、中键移动，其他工具中键平移。替换对象按手势仅隐藏预览，release 在 WindowsProEditorCommand 的一次 Mutate 中删旧添新，包含 changeSerial/只读/锁校验。hover 按键按原鼠标位置画、底边中心对尖部，不写文档。默认 NoteHeight=28，layout revision 4 迁移原默认 32。ChartValidator 同轨尾头接触（含 tap）也报重叠。

- `MainWindow.Tracks.cs` 新增后 `UpdateMainTrackWidth` 改主轨宽度，且 `AddEditorTrackAsync` 主动滚到底。
- `AudioVisualizationSideSurface.SetViewport` 仍将缩放夹到 24～480。
- 同步参考轨只按歌曲时间定位光标但仍按本轨 beat 绘制；不同 BPM 时同样 px/beat 不等于同样 px/歌曲毫秒。
- 草稿的 ConfigureView 硬编码了 42 / 37 / 26 等，与主轨不同。
- `QuickPaletteButton` 的内容固定 84 宽，合并到较窄双列工具栏后内容溢出。
- `TimelineHoldTailResize` 使用高达 40 px 纵向容错和左右额外 12 px。
- `TimelinePlacementGeometry` 基于 note 中心/anchor 偏移后四舍五入，不是时间前一格。
- 添加菜单被绑定到各 TimelineSurface 空白事件，而非专属底板空白；菜单关闭点击未吞掉。

## 构建/测试

使用 SDK 10.0.401（Avalonia 生成器不支持 SDK 8 的编译器），运行目标 net8.0-windows。

当前隔离 SDK：

```powershell
# Optional: set NUGET_PACKAGES to a local package cache.
# Use an installed .NET 10 SDK.
./scripts/Test.ps1
```

- 50 项核心 specs；自有 `tests/SimpleManiaEditor.UiSmoke` 测试实际 Avalonia 控件、真实解码/DSP、模拟设备和 Skia 截图。
- 不运行依赖作者私有 D:\ 数据的全量上游 UI specs。
- 发布 `scripts/Publish.ps1 -Dotnet <SDK> -Zip`，自包含 Windows x64 包；发布前提交以便内嵌版本 SHA 正确。
- 最终 ZIP 必须解压到新的 artifacts 子目录，隐藏运行 `--smoke-test`，检查真实 ExitCode、版本 SHA、演示、说明、许可；PowerShell 直接调用 GUI exe 的 LASTEXITCODE 不可信。
- 构建物在忽略的 `dist/`、`artifacts/`；不要删除旧包或用户数据。
- 不把 headless 测试当作真实音频设备或官方 osu 比较验收。

需求和边界见 `BACKLOG.md`、`WORKBENCH-TRACKS.md`、历次 RELEASE 文档。当前代码状态以 Git 为准；0.1.5 请求和自动回归已完成。

## 0.1.5 完成状态

已实现上述请求，新增 EditorTrackPanel / MainWindow.TrackBoard.cs 统一底板，Songs 导入副本与写保护、时间投影、格线前置放置、精确尾部、默认保音高和单一顶部栏。核心49/49通过；新增 Songs、精确放置、菜单关闭吞点击、实际拖动与顺序恢复、不同 BPM 高缩放、波形高缩放等界面回归通过。用户已说明警告原文忘记，后续再次触发再补，勿声称复现官方报错。

## 0.1.13 试玩焦点交接

PlaytestWindowActivation 在 RunAsync 发出 open 后等待自有子进程可见窗口，再由仍持有前台的编辑器调用 SetForegroundWindow；取消、退出或 10 秒截止终止等待，用户主动切到其他进程则停止交接。避免仅授权子进程/调用 SDL Raise 却没有实际切换前台。ResidentSupervisorSpecs 使用响应消息循环的原生父进程窗口，三个回合都断言 Windows 前台 PID 为实际 GPU 游戏宿主；保留资源缓存、同 PID、异常重建和关闭测试。

## 0.1.14 试玩与选项交互

官方游戏 Content 顶层 PracticeShortcutHandler 在 replay/ruleset 输入之前接管 Shift 快速暂停/继续和 Esc 直接 ReturnToEditor；不改官方渲染、皮肤、计分。--verify 的 VerificationInputManager 注入标准 KeyboardKeyInput 走完整队列，检查左右 Shift、按住不重复、暂停时钟停止、Esc 返回和连续三次完美演奏（artifacts/resident-playtest/d26fb72f25084ef6b82541d9f842b2ab）。MainWindow.OptionControls 统一滚轮和逐项默认值按钮，显式引用出厂值；逻辑树要 Distinct，调整卡片的复原放在标题行保证滑条与刻度对齐。外观 Expander 默认收起，编辑器皮肤从快捷键区移入。OD/HP 原已有模型/读写但控件未入 UI，现在进入元数据页；提交前两项都校验 0–10，原子提交/撤销。底板两侧 padding=Viewport.Width；重建轨道临时缩小 extent 会强制偏移，因此 RestoreBoardPanAfterLayout 在最终布局后恢复平移。UiSmoke --workbench-test 提供快速编辑器回归（输出在隔离临时截图目录），完整脚本仍覆盖历史音频/皮肤用例。

## 0.1.15 输入修正与原生 Timing

0.1.14 的 Content 级 PracticeShortcutHandler 在真实输入中可能被全局 Back 抢先处理，已经移除。PracticeInputManager 在 HandleKeyboardKeyStateChange 拦截 Shift/Esc，其他行为对应 upstream OsuUserInputManager；VerificationInputManager 继承它，验证和实际输入走同一路径。Shift 调用官方 Pause/Resume；官方 Pause 拒绝 replay，所以自动演奏薄覆写 Stop+PauseOverlay.Show，仍使用官方面板和 Resume。真实 GPU 回归覆盖播放中、自动暂停和手动暂停面板中的 Escape。

独立 playtestRate 和 SnapAll 存于 editor-presentation.json。RelativeBeatMove 支持绝对吸附修正，导入大分母使用当前分度；已写入的合法细分用 <=1920 的公共网格保留，防止粗网格压短 LN。未吸附移动的 LCM 超限使用半微拍误差以内的有界表达。原生负拍允许编辑；MinimumEditableBeat 按 session/serial 缓存，避免逐帧建 TimingMap。调整卡片默认仅跨工具/底板的前三列，避免遮住右侧栏。

OsuTimingEditingService 使用绝对整数毫秒 DTO，草稿弹窗不占历史。_sme_requested_timing_ms 由原生 ReanchorTiming 消费，再重新建立时间映射，保留旧音符绝对时间。OsuTimingPointSettings 在 UnknownData 中保留节拍数/bank/index/volume/effects；原生写入现有字段，MC recovery 的红/绿线均保存。新增事件继承当时音效/Kiai 设置，编辑 Kiai 保留其他 effects 位。UI 原生 Offset 对 Malody Delay 在捕获/填表边界反号；BPM 显示六位小数，时间一毫秒，原文件未编辑行继续保真。MainWindow.TimingExperience 和 Dialogs/OsuTimingDialog 隔离新功能，MC 保留原来的 Timing 表单。

## 0.1.16 工作台与音频所有权

- 试玩入口先设置 playtestInProgress、取消 Timeline 指针预览、禁用 editorView、暂停音频再保存。ToggleAudioPlaybackAsync 由 editorPlaybackCommandGate 串行化；PauseAll 使用 idempotent transport.PauseAsync，取消 seek 与 silent clock。主窗口键盘、帧泵与 seek 恢复均检查 playtestInProgress；结束 finally 解锁。
- WindowsRenderingConfiguration 使用 WinUIComposition / DirectComposition / RedirectionSurface，不再优先 LowLatencyDxgiSwapChain；具体撕裂效果需实机。
- EventTimeRuler 按 session serial 缓存事件/Kiai，播放只更新位置；时间轴可拖动 seek、双击原生事件编辑。Timing 右键/擦除删除入口完全移除，保留选中后删除。
- MainWindow.GlobalOverviews 固定左侧全局视图与显隐偏好；DifficultyOverview 未有 Rework 结果时显示实验负荷。EditorMetricTrackSurface.CalculateLocal 为 500 ms 窗口、8 ms 采样（最多 250k）、连续屏幕绘制；Calculate 保留全局每秒分桶语义。
- WorkbenchSafetySpecs 覆盖实际编辑器音乐播放 + 滚轮后试玩、输入锁定/返回、silent clock、固定全局/持久化、Timing 右键/擦除、Kiai 范围/时间定位和局部 NPS。OptionControlsSpecs 改为只允许全局偏好恢复按钮。

## 0.1.17 恢复、布局与帧时钟

- DesktopDocumentWorkspace.PersistCoreAsync 必须对所有实际写入的缓存更新 WorkingSha256；旧代码仅 retained room draft 更新，导致普通 checkpoint 的误判和丢弃。RequiresRecoverySaveAs 跨清单持久化：不可信但有效的旧脏副本保留，Save/同路径 SaveAs 阻止覆盖；不同路径成功另存为或从源重新载入后解除。无效缓存保留到 editingRoot/unverified，独立于标签缓存并受清理保护；关闭恢复标签不删除它。
- 当前用户 FEHLT 只读检查：源 hash 匹配、缓存不匹配。没有改写用户清单、谱面或缓存。历史已被旧版覆盖的内容不保证可找回。
- TrackBoard 使用 desiredBoardPan/pendingBoardPan，实际 Extent 就绪才应用 Offset；LayoutUpdated/ExtentProperty 触发提交。顶部 Views flyout 同步原全局显隐设置，Arrange 归位主轨和卡片。
- EditorPlaybackVisualClock 只影响显示，使用 compositionTimestamp 和有界 2.5% slew；音频/打击音依旧采用原设备时钟。UpdateUi 播放中禁止把 timeline 写回 stale ActiveTab.CurrentBeat。
- WorkspaceCheckpointSpecs：checkpoint hash/dirty reopen/旧 hash recovery/原谱保护/SaveAs/损坏副本保留。StartupPlaybackSpecs：布局/resize/归位/全局恢复/长路径提示/播放刷新不回跳/离散音频时钟匀速及漂移。
