using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LumaChart.Application.Editing;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Editing;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Rendering;
using LumaChart.Core.Editing;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;
using LumaChart.Core.Validation;
using System.Runtime.InteropServices;

namespace LumaChart.Avalonia.Controls;

public enum TimelineTool
{
    Select,
    Tap,
    Hold,
    Erase
}

public sealed record OnlineCreatorAppearance(string Name, IImage? Avatar);


public sealed record TimelineHoldTailEvidenceTarget(
    Guid NoteId,
    int Lane,
    BeatPosition HeadBeat,
    BeatPosition EndBeat,
    Point SurfacePoint);

public sealed class TimelineManualNavigationEventArgs(bool changesBeat) : EventArgs
{
    public bool ChangesBeat { get; } = changesBeat;
}

public sealed record TimelinePlacementEvidenceTarget(
    int Lane,
    BeatPosition Beat,
    Point SurfacePoint);

/// <summary>
/// A document-independent, immutable pointer probe captured once for the current
/// TimelineSurface session binding. Keeping all three targets together prevents
/// the interaction evidence capture from recomputing coordinates after the Tap
/// or Hold created by the first real pointer gesture changes the document.
/// </summary>
public sealed record TimelinePlacementEvidenceProbe(
    TimelinePlacementEvidenceTarget TapTarget,
    TimelinePlacementEvidenceTarget HoldStartTarget,
    TimelinePlacementEvidenceTarget HoldEndTarget);

/// <summary>
/// Read-only, deterministic pointer targets for the real ProEditor fixture GUI
/// exercise. The caller must still enable ProEditor and deliver physical pointer
/// input; obtaining this probe never changes the mode, document or selection.
/// </summary>
public sealed record TimelineProEditorEvidenceProbe(
    TimelinePlacementEvidenceTarget DraftTapTarget,
    TimelinePlacementEvidenceTarget OfficialMoveTarget,
    TimelinePlacementEvidenceTarget DraftHoldStartTarget,
    TimelinePlacementEvidenceTarget DraftHoldEndTarget);

public readonly record struct TimelineProEditorGeometryDiagnostics(
    double LaneWidthDip,
    double OfficialTrackLeftDip,
    double OfficialTrackRightDip);

public sealed partial class TimelineSurface : Control
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new TimelineSurfaceAutomationPeer(this);

    private sealed class TimelineSurfaceAutomationPeer(TimelineSurface owner)
        : ControlAutomationPeer(owner)
    {
        protected override string GetNameCore()
        {
            var configuredName = base.GetNameCore();
            return string.IsNullOrWhiteSpace(configuredName) ? "谱面编辑时间轴" : configuredName;
        }

        protected override string GetHelpTextCore() => owner.BuildAutomationHelpText();
    }

    private const double TimingRailWidth = 112;
    private const double MeasureNumberRailWidth = 28;
    private const double MeasureNumberRailPadding = 4;
    private const double TouchPanActivationDistance = 10;
    private const long TouchHoldPlacementDelayMilliseconds = 420;

    /// <summary>
    /// Contacts that scroll the chart instead of editing it.
    ///
    /// A desktop mouse has a wheel and a left button, so scrolling and drawing
    /// never compete. A finger is one channel, and the single-finger gesture is
    /// already spoken for over the note columns — a swipe there places notes and
    /// draws holds. Two fingers give the chart back a scroll that works
    /// anywhere, including over the columns, without redefining anything a
    /// single finger already does.
    /// </summary>
    private const int TouchScrollContactCount = 2;
    private const int NoWorkspaceColumn = int.MinValue;
    private static readonly IBrush SurfaceBrush = new SolidColorBrush(Color.Parse("#11171D"));
    private static readonly IBrush TrackMaskBrush = new SolidColorBrush(
        Color.Parse(WindowsTrackTransparencyPolicy.OpaqueTrackColor));
    private static readonly IBrush LaneGridBrush =
        new SolidColorBrush(Color.Parse("#7B6C7377"));
    private static readonly IBrush NoteBrush = new SolidColorBrush(Color.Parse("#9FB5C2"));
    private static readonly IBrush SelectedNoteBrush = new SolidColorBrush(Color.Parse("#7ED7DA"));
    private static readonly IBrush HoldBrush = new SolidColorBrush(Color.Parse("#567B8B"));
    private static readonly IBrush NotePlateBrush = new SolidColorBrush(Color.Parse("#F2252C2F"));
    private static readonly IBrush NoteShellBrush = new SolidColorBrush(Color.Parse("#FF15191B"));
    private static readonly IBrush NoteRailBrush = new SolidColorBrush(Color.Parse("#FFD0DEDC"));
    private static readonly IBrush HoldOuterBrush = new SolidColorBrush(Color.Parse("#FFD9DDDE"));
    private static readonly IBrush HoldFaceBrush = new SolidColorBrush(Color.Parse("#FF858D91"));
    private static readonly IBrush HoldStartBrush = new SolidColorBrush(Color.Parse("#FFD79A4F"));
    private static readonly IBrush HoldEndBrush = new SolidColorBrush(Color.Parse("#FF71B8C2"));
    private const double HiddenHoldHeadBorderThickness = 3;
    private static readonly IPen HiddenHoldHeadPen = new Pen(
        new SolidColorBrush(Color.Parse("#FF9A86B8")),
        HiddenHoldHeadBorderThickness);
    private static readonly IBrush SelectedNoteOverlayBrush =
        new SolidColorBrush(Color.Parse("#396FB7BC"));
    private static readonly IBrush ValidationIssueOverlayBrush =
        new SolidColorBrush(Color.Parse("#42FF3D49"));
    private static readonly IBrush[] GroupBrushes =
    [
        new SolidColorBrush(Color.Parse("#FFE7B6A2")),
        new SolidColorBrush(Color.Parse("#FFE4CE88")),
        new SolidColorBrush(Color.Parse("#FFC5D98A")),
        new SolidColorBrush(Color.Parse("#FFA8D887")),
        new SolidColorBrush(Color.Parse("#FF8FD4BC")),
        new SolidColorBrush(Color.Parse("#FF8BCBDD")),
        new SolidColorBrush(Color.Parse("#FF9CB7E2")),
        new SolidColorBrush(Color.Parse("#FFB7A8DE")),
        new SolidColorBrush(Color.Parse("#FFD5A5CA")),
        new SolidColorBrush(Color.Parse("#FFE4A2AE")),
        new SolidColorBrush(Color.Parse("#FFB9C4C8")),
        new SolidColorBrush(Color.Parse("#FFD0B58C"))
    ];
    private static readonly IBrush GroupTextBrush = new SolidColorBrush(Color.Parse("#FF172024"));
    private static readonly IBrush CreatorTextBrush = new SolidColorBrush(Color.Parse("#FF111719"));
    private static readonly IPen GroupBadgePen = new Pen(
        new SolidColorBrush(Color.Parse("#FF445159")),
        1);
    private static readonly IPen CreatorMarkerPen = new Pen(
        new SolidColorBrush(Color.Parse("#FFDCE7E8")),
        1.1);
    private static readonly IBrush CreatorToolTipBackgroundBrush =
        new SolidColorBrush(Color.Parse("#F2262C30"));
    private static readonly IBrush CreatorToolTipTextBrush =
        new SolidColorBrush(Color.Parse("#FFF0F1EF"));
    private static readonly IPen CreatorToolTipPen = new Pen(
        new SolidColorBrush(Color.Parse("#FF59666D")),
        1);
    private static readonly IBrush TimingLabelBackgroundBrush = new SolidColorBrush(Color.Parse("#EE1A1D1F"));
    private static readonly IBrush TimingTextBrush = new SolidColorBrush(Color.Parse("#FFF0F1EF"));
    private static readonly IBrush TimingBpmBrush = new SolidColorBrush(Color.Parse("#FF7FA4A7"));
    private static readonly IBrush TimingScrollBrush = new SolidColorBrush(Color.Parse("#FFD0A05F"));
    private static readonly IBrush TimingJumpBrush = new SolidColorBrush(Color.Parse("#FFC97968"));
    private static readonly IBrush TimingHsBrush = new SolidColorBrush(Color.Parse("#FF73A7A5"));
    private static readonly IBrush TimingSignBrush = new SolidColorBrush(Color.Parse("#FF8EA77A"));
    private static readonly IBrush TimingCustomBrush = new SolidColorBrush(Color.Parse("#FFA58AAB"));
    private static readonly IBrush OwnerRegionFillBrush = new SolidColorBrush(Color.Parse("#32C49155"));
    private static readonly IBrush MeasureNumberRailBrush =
        new SolidColorBrush(Color.Parse("#C9101519"));
    private static readonly IBrush MeasureNumberTextBrush =
        new SolidColorBrush(Color.Parse("#E8C6CBCC"));
    private static readonly IBrush JudgementBrush = new SolidColorBrush(Color.Parse("#D35E64"));
    private static readonly IBrush MarqueeFillBrush = new SolidColorBrush(Color.Parse("#287FA4A7"));
    private static readonly IPen LaneGridPen = new Pen(LaneGridBrush, 1);
    private static readonly IReadOnlyDictionary<WindowsTimelineGridLineKind, IPen>
        TimelineGridPens = Enum
            .GetValues<WindowsTimelineGridLineKind>()
            .ToDictionary(
                kind => kind,
                kind =>
                {
                    var style =
                        WindowsTimelineVisualParityPolicy.ResolveGridLineStyle(kind);
                    return (IPen)new Pen(
                        new SolidColorBrush(Color.Parse(style.Color)),
                        style.Thickness);
                });
    private static readonly IPen ActiveBoundaryPen = new Pen(
        new SolidColorBrush(Color.Parse("#CCCA5963")),
        1.5);
    private static readonly IPen NoteContourPen = new Pen(
        new SolidColorBrush(Color.Parse("#FF819B9D")),
        2.1);
    private static readonly IPen SelectedNotePen = new Pen(
        new SolidColorBrush(Color.Parse("#FF9ED8DC")),
        2.6);
    private static readonly IPen MarqueePen = new Pen(new SolidColorBrush(Color.Parse("#FF8DB7BA")), 1.2);
    private static readonly IPen TimingConnectorPen = new Pen(new SolidColorBrush(Color.Parse("#9A727A7D")), 1);
    private static readonly IPen SelectedTimingPen = new Pen(new SolidColorBrush(Color.Parse("#FFF2EEE7")), 2);
    private static readonly IPen OwnerRegionPen = new Pen(new SolidColorBrush(Color.Parse("#B8D2A363")), 1.25);
    private static readonly IPen MeasureNumberRailPen =
        new Pen(new SolidColorBrush(Color.Parse("#B879858A")), 1);
    private static readonly IPen TimingBpmPen = new Pen(TimingBpmBrush, 1.15);
    private static readonly IPen TimingScrollPen = new Pen(TimingScrollBrush, 1.15);
    private static readonly IPen TimingJumpPen = new Pen(TimingJumpBrush, 1.15);
    private static readonly IPen TimingHsPen = new Pen(TimingHsBrush, 1.15);
    private static readonly IPen TimingSignPen = new Pen(TimingSignBrush, 1.15);
    private static readonly IPen TimingCustomPen = new Pen(TimingCustomBrush, 1.15);
    private static readonly IPen HoldTailPen = new Pen(new SolidColorBrush(Color.Parse("#FFEDF5F6")), 1.4);
    private static readonly IPen ValidationIssuePen = new Pen(
        new SolidColorBrush(Color.Parse("#FFFF5964")),
        2.8);
    private static readonly IReadOnlyDictionary<int, IPen> SubdivisionCheckPens =
        TimelineSubdivisionCheckPalette.SupportedDenominators.ToDictionary(
            denominator => denominator,
            denominator =>
            {
                var style = TimelineSubdivisionCheckPalette.Resolve(denominator);
                return (IPen)new Pen(new SolidColorBrush(style.Color), 3.4);
            });
    private static readonly IPen SubdivisionWarningPen = new Pen(
        new SolidColorBrush(TimelineSubdivisionCheckPalette.WarningColor),
        4.2);
    // The background waveform is drawn above the track mask as a faint fill
    // with a crisp contour. A solid band of full-strength lines turned loudly
    // mastered songs into one flat slab over the Notes, while the contour is
    // what carries the onsets a charter reads.
    private static readonly IBrush BackgroundWaveformFillBrush =
        new SolidColorBrush(Color.Parse("#2E76A6AA"));
    private static readonly IPen BackgroundWaveformOutlinePen = new Pen(
        new SolidColorBrush(Color.Parse("#A89CC9CD")),
        1);
    private static Cursor? arrowCursor;
    private static Cursor? holdTailCursor;
    private static Cursor ArrowCursor => arrowCursor ??= new Cursor(StandardCursorType.Arrow);
    private static Cursor HoldTailCursor =>
        holdTailCursor ??= new Cursor(StandardCursorType.SizeNorthSouth);

    private readonly TimelineNoteIndex noteIndex = new();
    private readonly TimelineTimingIndex timingIndex = new();
    private readonly TimelineTimingIndex timingMovePreviewIndex = new();
    private readonly List<ChartNote> visibleNotes = [];
    private readonly List<ChartNote> previewNotes = [];
    private readonly List<ChartNote> hitScratch = [];
    private readonly List<ChartNote> holdTailScratch = [];
    private readonly List<TimingEvent> visibleTimings = [];
    private readonly List<TimingEvent> visibleTimingMovePreviews = [];
    private readonly List<TimingEvent> timingMovePreviewEvents = [];
    private readonly List<TimingEvent> timingDrawEvents = [];
    private readonly Dictionary<Guid, int> timingSourceOrders = [];
    private readonly List<TimelineTimingLabel> visibleTimingLabels = [];
    private readonly List<TimelineOwnerRegionVisual> visibleOwnerRegions = [];
    private readonly DispatcherTimer edgeScrollTimer;
    private readonly Dictionary<string, FormattedText> timingTextCache = new(StringComparer.Ordinal);
    private readonly Dictionary<int, FormattedText> measureNumberTextCache = [];
    private readonly Dictionary<int, FormattedText> groupTextCache = [];
    private readonly Dictionary<string, FormattedText> creatorTextCache = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> selectedTimingIds = [];
    private HashSet<Guid> validationIssueNoteIds = [];
    private IReadOnlyList<ChartIssue> chartIssues = [];
    private HashSet<int> hiddenGroups = [];
    private readonly List<ChartNote> marqueeCandidates = [];
    private readonly HashSet<Guid> marqueeBaseSelection = [];
    private readonly HashSet<Guid> marqueeNextSelection = [];
    private readonly HashSet<Guid> marqueeBaseTimingSelection = [];
    private readonly HashSet<Guid> marqueeNextTimingSelection = [];
    private readonly HashSet<Guid> erasedNoteIds = [];
    private readonly HashSet<Guid> eraseSegmentNoteIds = [];
    private readonly HashSet<Guid> erasedTimingIds = [];
    private readonly HashSet<Guid> eraseSegmentTimingIds = [];
    private readonly List<TimelineTimingLabel> eraseTimingLabels = [];
    private readonly HashSet<Guid> timingMoveRequestedIds = [];
    private readonly Dictionary<Guid, BeatPosition> timingMoveOrigins = [];
    private readonly List<TimelineNoteMoveOrigin> noteMoveOrigins = [];
    private EditorSession? session;
    private IPointer? interactionPointer;
    private MouseButton interactionButton;
    private long indexedChangeSerial = long.MinValue;
    private long lastEdgeScrollTick;
    private long lastTailHoverHitTestTick;
    private string? lastHoverText;
    private long lastTooltipCreatorId;
    private Point creatorHoverPoint;
    private string? creatorHoverToolTip;
    private FormattedText? creatorHoverFormattedText;
    private Point edgeScrollPoint;
    private Point dragStartPoint;
    private bool dragging;
    private int previewLaneDelta;
    private double previewBeatDelta;
    private RelativeBeatMove previewBeatMove;
    private BeatPosition noteMoveAnchorOrigin;
    private double noteMoveMinimumOriginBeat;
    private int noteMoveMinimumLane;
    private int noteMoveMaximumLane;
    private long noteMoveStartChangeSerial;
    private double noteMovePointerStartBeat;
    private bool resizingHoldTail;
    private Guid resizingHoldId;
    private int resizingHoldLane;
    private BeatPosition resizingHoldHead;
    private BeatPosition? resizingHoldOriginalEnd;
    private BeatPosition? resizingHoldCurrentEnd;
    private long resizingHoldStartChangeSerial;
    private bool movingTiming;
    private Guid timingMoveAnchorId;
    private BeatPosition timingMoveAnchorOrigin;
    private BeatPosition timingMoveMinimumOrigin;
    private BeatPosition timingMovePreviewAnchor;
    private double timingMovePointerStartBeat;
    private double timingMovePreviewDelta;
    private long timingMoveStartChangeSerial;
    private ChartDocument? timingMoveStartDocument;
    private bool marqueeSelecting;
    private Point marqueeStartPoint;
    private Point marqueePointerPoint;
    private long marqueeSelectionFrameTick;
    private double marqueeStartBeat;
    private Rect marqueeRect;
    private bool placingNote;
    private int placementLane = NoWorkspaceColumn;
    private BeatPosition placementStartBeat;
    private Guid? placementReplacedNoteId;
    private Guid? placementReplacedCompanionId;
    private long placementStartChangeSerial;
    private Point? placementHoverPoint;
    private BeatPosition placementCurrentBeat;
    private TimelineTool placementGestureTool;
    private bool erasingStroke;
    private bool eraseStrokeChanged;
    private bool eraseLockedStatusShown;
    private long eraseStartChangeSerial;
    private Point eraseLastPoint;
    private TimelineTool tool = TimelineTool.Select;
    public EditorInteractionProfile InteractionProfile { get; set; } = EditorInteractionProfile.OsuMania;
    public bool ReverseTimeWheel { get; set; } = true;
    // A synchronized track displays its own Timing, but navigation belongs to
    // the main song grid. Never snap in the projected track and convert back.
    public TimelineSurface? SynchronizedNavigationSource { get; set; }
    public EditorNoteStyle NoteStyle { get; set; } = EditorNoteStyle.Osu;
    public const double MinimumZoom = 12;
    public const double MaximumZoom = 4480;
    public bool SnapAllNotes { get; set; } = true;
    private CenterSymmetrySettings? symmetrySettings;
    public CenterSymmetrySettings? SymmetrySettings
    {
        get => symmetrySettings;
        set { if (ReferenceEquals(symmetrySettings, value)) return; symmetrySettings = value; InvalidateVisual(); }
    }
    private bool AllowsNegativeBeats => session != null && LumaChart.Core.IO.OsuSourceEnvelope.HasSource(session.Document);
    private long minimumBeatSerial = long.MinValue;
    private double minimumBeatCache;
    private double MinimumEditableBeat
    {
        get
        {
            if (!AllowsNegativeBeats) return 0;
            if (minimumBeatSerial != session!.ChangeSerial)
            {
                minimumBeatCache = Math.Min(-4, new TimingMap(session.Document.Timing).MillisecondsToBeat(0).Value - 4);
                minimumBeatSerial = session.ChangeSerial;
            }
            return minimumBeatCache;
        }
    }
    public const double PlaybackWheelStepMilliseconds = 500;
    public event EventHandler<int>? SnapDivisionChangeRequested;
    public event EventHandler<int>? PlaybackRateChangeRequested;
    public event EventHandler<Point>? BlankContextRequested;
    private bool proEditorEnabled;
    private int proGridColumnCount = ProEditorWorkspaceGeometry.DefaultTotalColumns;
    private double gridLaneWidth = WindowsEditorViewSettings.DefaultGridLaneWidth;
    private double mainTrackOffset;
    private double noteWidth = WindowsEditorViewSettings.DefaultNoteWidth;
    private double noteHeight = WindowsEditorViewSettings.DefaultNoteHeight;
    private double noteOpacity = 1;
    private double trackOpacity = WindowsEditorViewSettings.DefaultTrackOpacity;
    private double backgroundOpacity =
        1 - WindowsEditorBackgroundSettings.DefaultDarkness;
    private WindowsNoteAnchorMode noteAnchor = WindowsNoteAnchorMode.CellBottom;
    private WindowsNoteAnchorMode EffectiveNoteAnchor => InteractionProfile == EditorInteractionProfile.OsuMania ? WindowsNoteAnchorMode.LineAbove : noteAnchor;
    private TimingMap? sourceAxisMap;
    private TimingMap? displayAxisMap;

    public void SetTimeAxisProjection(TimingMap? sourceMap, TimingMap? displayMap)
    {
        if (ReferenceEquals(sourceAxisMap, sourceMap) && ReferenceEquals(displayAxisMap, displayMap)) return;
        sourceAxisMap = sourceMap; displayAxisMap = displayMap;
        InvalidateAudioScene(); InvalidateVisual();
    }

    private double ProjectBeat(double beat) => sourceAxisMap == null || displayAxisMap == null ? beat :
        displayAxisMap.MillisecondsToBeat(sourceAxisMap.BeatToMilliseconds(BeatPosition.FromDouble(beat, 1_000_000)), 1_000_000).Value;

    private double UnprojectBeat(double beat) => sourceAxisMap == null || displayAxisMap == null ? beat :
        sourceAxisMap.MillisecondsToBeat(displayAxisMap.BeatToMilliseconds(BeatPosition.FromDouble(beat, 1_000_000)), 1_000_000).Value;
    private double judgementLinePosition =
        WindowsEditorViewSettings.DefaultJudgementLinePosition;
    private double judgementLineThickness =
        WindowsEditorViewSettings.DefaultJudgementLineThickness;
    private double judgementLineOpacity =
        WindowsEditorViewSettings.DefaultJudgementLineOpacity;
    private double barLineThickness =
        WindowsEditorViewSettings.DefaultBarLineThickness;
    private double subdivisionLineThickness =
        WindowsEditorViewSettings.DefaultSubdivisionLineThickness;
    private IPen judgementPen = new Pen(
        JudgementBrush,
        WindowsEditorViewSettings.DefaultJudgementLineThickness);
    private IPen barGridPen = TimelineGridPens[WindowsTimelineGridLineKind.Bar];
    private IReadOnlyDictionary<WindowsTimelineGridLineKind, IPen> subdivisionGridPens =
        TimelineGridPens;
    private double proTrackOffset;
    private bool workspacePanning;
    private bool horizontalPanOnly;
    private bool verticalPanOnly;
    private bool touchBlankGesturePending;
    private int touchBlankGestureLane = NoWorkspaceColumn;
    private TimelineTool touchBlankGestureTool;
    private long touchBlankGestureStartedAt;
    private bool spacePanModifier;
    private bool spacePanUsed;
    private Point panStartPoint;
    private double panStartBeat;
    private double panStartTrackOffset;
    private readonly Dictionary<int, Point> touchContacts = [];
    private bool touchScrolling;
    private double touchScrollStartY;
    private double touchScrollStartBeat;
    private AudioVisualization? audioVisualization;
    private TimingMap? audioVisualizationTimingMap;
    private bool backgroundWaveformVisible;
    private bool backgroundSpectrogramVisible;
    private TimelineAudioBackgroundScene? audioScene;
    private WriteableBitmap? audioSpectrogramBitmap;
    private StreamGeometry? audioWaveformGeometry;
    private TimelineAudioBackgroundScene? audioWaveformGeometryScene;
    private double audioWaveformGeometryCenter;
    private double audioWaveformGeometryHalfWidth;
    private WriteableBitmap? audioSpectrogramBackBitmap;
    private CancellationTokenSource? audioSceneCancellation;
    private long audioSceneBuildVersion;
    private bool audioSceneBuildPending;
    private double audioSceneBuildBeat;
    private double audioSceneBuildScale;
    private double audioSceneBuildJudgementY;
    private double audioSceneBuildDetail = SpectrogramLevelMapping.DefaultDetail;
    private double audioSceneDetail = SpectrogramLevelMapping.DefaultDetail;
    private double spectrogramDetail = SpectrogramLevelMapping.DefaultDetail;
    private double spectrogramStrength = DefaultBackgroundSpectrogramStrength;

    public const double DefaultBackgroundSpectrogramStrength = SpectrogramLevelMapping.DefaultBackgroundStrength;
    private AudioVisualization? audioSceneSource;
    private TimingMap? audioSceneMap;
    private double audioSceneScale;
    private double audioSceneHeight;
    private bool audioSceneWaveformVisible;
    private bool audioSceneSpectrogramVisible;
    private Bitmap? chartBackgroundImage;
    private TimelinePlacementEvidenceProbe? placementEvidenceProbe;
    private IReadOnlyDictionary<long, OnlineCreatorAppearance> onlineCreators =
        new Dictionary<long, OnlineCreatorAppearance>();
    private bool isPlaybackActive;
    private string roomSyncWorkStatus = "none";
    private string roomSyncFeedStatus = "none";
    private long roomSyncRevision = -1;
    private Guid? roomSyncPendingOperationId;
    private int roomSyncConsecutiveWorkFailures;
    private int roomSyncConsecutiveFeedFailures;
    private bool chartIssuesChangedPending;
    private bool subdivisionCheckEnabled;
    private bool hideLongNoteBodies;

    public TimelineSurface()
    {
        edgeScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        edgeScrollTimer.Tick += EdgeScrollTimer_Tick;
        ClipToBounds = true;
        Focusable = true;
        MinWidth = 360;
        MinHeight = 320;
        DetachedFromVisualTree += (_, _) =>
        {
            InvalidateAudioScene();
            SetChartBackgroundImage(null);
        };
    }

    public EditorSession? Session
    {
        get => session;
        set
        {
            if (ReferenceEquals(session, value)) return;
            EndStretch();
            ClearPendingPastePreview();
            IsDefiningSymmetry = false; symmetryFirstPoint = null; symmetryDragEdge = null;
            symmetryPointer?.Capture(null); symmetryPointer = null; SymmetrySettings = null;
            if (resizingHoldTail) FinishHoldTailResize(releaseCapture: false);
            if (erasingStroke) FinishEraseStroke();
            ReleaseInteractionCapture();
            ResetPointerInteraction();
            if (session != null) session.Changed -= Session_Changed;
            var timingSelectionChanged = selectedTimingIds.Count > 0;
            selectedTimingIds.Clear();
            PrimarySelectedTimingId = null;
            timingTextCache.Clear();
            creatorTextCache.Clear();
            InvalidateAudioScene();
            placementEvidenceProbe = null;
            session = value;
            minimumBeatSerial = long.MinValue;
            if (session != null) session.Changed += Session_Changed;
            indexedChangeSerial = long.MinValue;
            chartIssues = [];
            validationIssueNoteIds.Clear();
            if (session != null)
            {
                proGridColumnCount = ProEditorWorkspaceGeometry.NormalizeTotalColumns(
                    session.Document.KeyCount,
                    proGridColumnCount);
                PruneNonInteractiveNoteSelection();
            }
            CurrentBeat = 0;
            InvalidateVisual();
            RaiseTrackPanChanged();
            if (timingSelectionChanged) TimingSelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double CurrentBeat { get; private set; }
    public double PixelsPerBeat { get; private set; } =
        WindowsEditorViewSettings.DefaultGridBeatHeight;
    public double GridLaneWidth => gridLaneWidth;
    public double MainTrackOffset => mainTrackOffset;
    public double NoteWidth => noteWidth;
    public double NoteHeight => noteHeight;
    public double NoteOpacity => noteOpacity;
    public double TrackOpacity => trackOpacity;
    public double BackgroundOpacity => backgroundOpacity;
    public WindowsNoteAnchorMode NoteAnchor => noteAnchor;
    public double JudgementLinePosition => judgementLinePosition;
    public double JudgementLineThickness => judgementLineThickness;
    public double JudgementLineOpacity => judgementLineOpacity;
    public double BarLineThickness => barLineThickness;
    public double SubdivisionLineThickness => subdivisionLineThickness;
    public bool SubdivisionCheckEnabled => subdivisionCheckEnabled;
    public bool HideLongNoteBodies => hideLongNoteBodies;
    public bool HideSvEditing { get; set; }
    public int PlacementGroup { get; set; }
    public bool ProEditorEnabled => proEditorEnabled;
    public int ProGridColumnCount => proGridColumnCount;
    public IReadOnlySet<int> HiddenGroups => hiddenGroups;
    public bool IsPlaybackActive => isPlaybackActive;
    public TimelineTool Tool
    {
        get => tool;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (tool == value) return;
            if (resizingHoldTail) FinishHoldTailResize(releaseCapture: false);
            if (erasingStroke) FinishEraseStroke();
            ReleaseInteractionCapture();
            ResetPointerInteraction();
            EndStretch();
            tool = value;
            InvalidateVisual();
            StatusChanged?.Invoke(this, AvaloniaLocalizationCatalog.Format("当前工具：{0}", ToolDisplayName(value)));
        }
    }
    public TimelinePerformanceRecorder Performance { get; } = new();

    public void SetChartBackgroundImage(Bitmap? image)
    {
        if (ReferenceEquals(chartBackgroundImage, image)) return;
        var previous = chartBackgroundImage;
        chartBackgroundImage = image;
        previous?.Dispose();
        InvalidateVisual();
    }

    private string BuildAutomationHelpText()
    {
        var performance = Performance.Snapshot();
        var selectedNote = session?.Document.Notes.FirstOrDefault(note =>
            session.Selection.Contains(note.Id));
        var selectedState = selectedNote == null
            ? "SelectedNoteId=none; SelectedNoteLane=-1; SelectedNoteBeat=none"
            : string.Concat(
                "SelectedNoteId=", selectedNote.Id.ToString("D"),
                "; SelectedNoteLane=", selectedNote.Lane.ToString(CultureInfo.InvariantCulture),
                "; SelectedNoteBeat=", selectedNote.Beat.ToString());
        var backgroundState = chartBackgroundImage == null
            ? "背景预览未加载"
            : AvaloniaLocalizationCatalog.Format("背景预览已加载 {0}×{1}", chartBackgroundImage.PixelSize.Width, chartBackgroundImage.PixelSize.Height);
        return string.Concat(
            "时间轴实时状态; CurrentBeat=", CurrentBeat.ToString("R", CultureInfo.InvariantCulture),
            "; PixelsPerBeat=", PixelsPerBeat.ToString("R", CultureInfo.InvariantCulture),
            "; SnapDivision=", (session?.SnapDivision ?? 0).ToString(CultureInfo.InvariantCulture),
            "; SelectedNoteCount=", (session?.Selection.Count ?? 0).ToString(CultureInfo.InvariantCulture),
            "; SelectedTimingCount=", selectedTimingIds.Count.ToString(CultureInfo.InvariantCulture),
            "; OnlineCreatorCount=", onlineCreators.Count.ToString(CultureInfo.InvariantCulture),
            "; OnlineCreatorAvatarCount=", onlineCreators.Values.Count(creator =>
                creator.Avatar != null).ToString(CultureInfo.InvariantCulture),
            "; CreatorAttributedNoteCount=", (session?.Document.Notes.Count(note =>
                note.OnlineCreatorUserId > 0) ?? 0).ToString(CultureInfo.InvariantCulture),
            "; CreatorAttributedTimingCount=", (session?.Document.Timing.Count(timing =>
                timing.OnlineCreatorUserId > 0) ?? 0).ToString(CultureInfo.InvariantCulture),
            "; LastCreatorHoverUserId=", lastTooltipCreatorId.ToString(CultureInfo.InvariantCulture),
            "; CreatorHoverTooltip=", creatorHoverToolTip ?? "none",
            "; CreatorMarkersPlaybackSuppressed=", isPlaybackActive ? "true" : "false",
            "; RoomSyncWorkStatus=", roomSyncWorkStatus,
            "; RoomSyncFeedStatus=", roomSyncFeedStatus,
            "; RoomSyncRevision=", roomSyncRevision.ToString(CultureInfo.InvariantCulture),
            "; RoomSyncPendingOperationId=",
            roomSyncPendingOperationId?.ToString("D") ?? "none",
            "; RoomSyncConsecutiveWorkFailures=",
            roomSyncConsecutiveWorkFailures.ToString(CultureInfo.InvariantCulture),
            "; RoomSyncConsecutiveFeedFailures=",
            roomSyncConsecutiveFeedFailures.ToString(CultureInfo.InvariantCulture),
            "; OwnerProtectedRegionCount=", (session?.OwnerProtectedRegions.Count ?? 0)
                .ToString(CultureInfo.InvariantCulture),
            "; ", selectedState,
            "; ", backgroundState,
            "; GridLaneWidth=", gridLaneWidth.ToString("R", CultureInfo.InvariantCulture),
            "; NoteWidth=", noteWidth.ToString("R", CultureInfo.InvariantCulture),
            "; NoteHeight=", noteHeight.ToString("R", CultureInfo.InvariantCulture),
            "; NoteOpacity=", noteOpacity.ToString("R", CultureInfo.InvariantCulture),
            "; MainTrackOffset=", mainTrackOffset.ToString("R", CultureInfo.InvariantCulture),
            "; JudgementLinePosition=",
            judgementLinePosition.ToString("R", CultureInfo.InvariantCulture),
            "; JudgementLineThickness=",
            judgementLineThickness.ToString("R", CultureInfo.InvariantCulture),
            "; JudgementLineOpacity=",
            judgementLineOpacity.ToString("R", CultureInfo.InvariantCulture),
            "; BarLineThickness=",
            barLineThickness.ToString("R", CultureInfo.InvariantCulture),
            "; SubdivisionLineThickness=",
            subdivisionLineThickness.ToString("R", CultureInfo.InvariantCulture),
            "; SubdivisionCheckEnabled=", subdivisionCheckEnabled ? "true" : "false",
            "; NoteAnchor=", noteAnchor.ToString(),
            "; RenderSamples=", performance.RenderMilliseconds.Samples
                .ToString(CultureInfo.InvariantCulture),
            "; InputToRenderSamples=", performance.InputToRenderMilliseconds.Samples
                .ToString(CultureInfo.InvariantCulture),
            "; InputToRenderMsP50=", performance.InputToRenderMilliseconds.P50
                .ToString("R", CultureInfo.InvariantCulture),
            "; InputToRenderMsP95=", performance.InputToRenderMilliseconds.P95
                .ToString("R", CultureInfo.InvariantCulture),
            "; InputToRenderMsP99=", performance.InputToRenderMilliseconds.P99
                .ToString("R", CultureInfo.InvariantCulture),
            "; Note 对齐 ", TimelinePlacementGeometry.DisplayName(noteAnchor));
    }

    public event EventHandler<double>? CurrentBeatChanged;
    public event EventHandler<double>? PixelsPerBeatChanged;
    public event EventHandler? SelectionChanged;
    public event EventHandler? TimingSelectionChanged;
    public event EventHandler? ChartIssuesChanged;
    public event EventHandler<string>? EditPermissionDenied;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? HoverChanged;
    public event EventHandler<ProEditorTrackPanState>? TrackPanChanged;
    public event EventHandler<double>? ZoomChanged;
    public event EventHandler<TimelineManualNavigationEventArgs>? ManualNavigationRequested;

    public IReadOnlyCollection<Guid> SelectedTimingIds => selectedTimingIds;
    public Guid? PrimarySelectedTimingId { get; private set; }
    public IReadOnlyList<ChartIssue> ChartIssues => chartIssues;

    public TimelineHoldTailEvidenceTarget? GetSelectedHoldTailEvidenceTarget()
    {
        if (session == null) return null;
        var note = session.Document.Notes.FirstOrDefault(candidate =>
            session.Selection.Contains(candidate.Id) &&
            !hiddenGroups.Contains(candidate.Group) &&
            (proEditorEnabled || !candidate.IsDraft) &&
            candidate.EndBeat.HasValue);
        if (note?.EndBeat is not { } endBeat) return null;
        var geometry = GetGeometry(session.Document.KeyCount);
        var column = ProEditorWorkspaceGeometry.GetColumn(note);
        return new TimelineHoldTailEvidenceTarget(
            note.Id,
            column,
            note.Beat,
            endBeat,
            new Point(
                geometry.TrackLeft + (column + .5) * geometry.LaneWidth,
                EvidencePointerY(endBeat, geometry)));
    }

    public TimelinePlacementEvidenceTarget? GetFreeTapEvidenceTarget() =>
        GetPlacementEvidenceProbe()?.TapTarget;

    /// <summary>
    /// Returns one immutable Tap target and one immutable Hold gesture for the
    /// current session binding. A successful probe remains frozen even when the
    /// document changes, so primary and post-interaction captures use identical
    /// surface coordinates. Assigning a different Session starts a new probe.
    /// </summary>
    public TimelinePlacementEvidenceProbe? GetPlacementEvidenceProbe()
    {
        if (placementEvidenceProbe != null) return placementEvidenceProbe;
        placementEvidenceProbe = CreatePlacementEvidenceProbe();
        return placementEvidenceProbe;
    }

    public TimelineProEditorEvidenceProbe? GetProEditorEvidenceProbe()
    {
        if (session == null || Bounds.Width <= 0 || Bounds.Height <= 0 ||
            session.Document.KeyCount < 4)
            return null;

        const int draftColumn = 4;
        const int officialColumn = 3;
        var range = GetProEditorColumnRange();
        if (!range.Contains(draftColumn) || !range.Contains(officialColumn)) return null;

        var geometry = GetGeometry(session.Document.KeyCount);
        var division = Math.Clamp(session.SnapDivision, 1, 1920);
        if (!TryCreateEvidenceBeat(0, division, out var tapBeat) ||
            !TryCreateEvidenceBeat(2, division, out var holdEndBeat))
            return null;
        Point PointAt(int column, BeatPosition beat) => new(
            geometry.TrackLeft + (column + .5) * geometry.LaneWidth,
            EvidencePointerY(beat, geometry));

        var draftTap = new TimelinePlacementEvidenceTarget(
            draftColumn,
            tapBeat,
            PointAt(draftColumn, tapBeat));
        return new TimelineProEditorEvidenceProbe(
            draftTap,
            new TimelinePlacementEvidenceTarget(
                officialColumn,
                tapBeat,
                PointAt(officialColumn, tapBeat)),
            draftTap,
            new TimelinePlacementEvidenceTarget(
                draftColumn,
                holdEndBeat,
                PointAt(draftColumn, holdEndBeat)));
    }

    private TimelinePlacementEvidenceProbe? CreatePlacementEvidenceProbe()
    {
        if (session == null || Bounds.Width <= 0 || Bounds.Height <= 0) return null;
        var safeInset = TimelineEdgeScroll.EdgeBand + noteHeight / 2;
        if (Bounds.Height <= safeInset * 2) return null;

        EnsureIndex();
        var geometry = GetGeometry(session.Document.KeyCount);
        var division = Math.Clamp(session.SnapDivision, 1, 1920);
        var minimumBeat = Math.Max(0, BeatAtY(Bounds.Height - safeInset, geometry));
        var maximumBeat = Math.Max(minimumBeat, BeatAtY(safeInset, geometry));
        if (!double.IsFinite(minimumBeat) || !double.IsFinite(maximumBeat)) return null;

        var firstCell = Math.Max(0L, checked((long)Math.Floor(minimumBeat * division)));
        var lastCell = Math.Max(firstCell, checked((long)Math.Ceiling(maximumBeat * division)));
        var cellStride = Math.Max(
            1L,
            checked((long)Math.Ceiling((noteHeight + 8) * division / Math.Max(1, PixelsPerBeat))));
        var holdCellSpan = Math.Max(
            1L,
            checked((long)Math.Ceiling((noteHeight * 2) * division / Math.Max(1, PixelsPerBeat))));
        var hitTolerance = noteHeight / Math.Max(1, PixelsPerBeat);
        var tapCandidates = BuildTapEvidenceCandidates(
            geometry,
            division,
            firstCell,
            lastCell,
            cellStride,
            safeInset,
            hitTolerance);
        var holdCandidates = BuildHoldEvidenceCandidates(
            geometry,
            division,
            firstCell,
            lastCell,
            cellStride,
            holdCellSpan,
            safeInset,
            hitTolerance);

        foreach (var tap in tapCandidates)
        foreach (var hold in holdCandidates)
        {
            if (tap.Lane == hold.Start.Lane && RangesIntersect(
                    tap.Beat.Value - hitTolerance,
                    tap.Beat.Value + hitTolerance,
                    hold.Start.Beat.Value - hitTolerance,
                    hold.End.Beat.Value + hitTolerance))
                continue;
            return new TimelinePlacementEvidenceProbe(tap, hold.Start, hold.End);
        }
        return null;
    }

    private List<TimelinePlacementEvidenceTarget> BuildTapEvidenceCandidates(
        TimelineGeometry geometry,
        int division,
        long firstCell,
        long lastCell,
        long cellStride,
        double safeInset,
        double hitTolerance)
    {
        const int candidateLimit = 256;
        var candidates = new List<TimelinePlacementEvidenceTarget>();
        foreach (var cell in EnumerateEvidenceCells(firstCell, lastCell, cellStride))
        {
            if (!TryCreateEvidenceBeat(cell, division, out var beat)) continue;
            var y = EvidencePointerY(beat, geometry);
            if (!IsEvidencePointSafe(y, safeInset) ||
                session!.IntersectsOwnerProtectedRegion(beat, beat))
                continue;
            for (var lane = 0; lane < session.Document.KeyCount; lane++)
            {
                if (!IsEvidenceRangeFree(
                        lane,
                        beat.Value - hitTolerance,
                        beat.Value + hitTolerance))
                    continue;
                candidates.Add(new TimelinePlacementEvidenceTarget(
                    lane,
                    beat,
                    new Point(
                        geometry.TrackLeft + (lane + .5) * geometry.LaneWidth,
                        y)));
                if (candidates.Count >= candidateLimit) return candidates;
            }
        }
        return candidates;
    }

    private List<(TimelinePlacementEvidenceTarget Start, TimelinePlacementEvidenceTarget End)>
        BuildHoldEvidenceCandidates(
            TimelineGeometry geometry,
            int division,
            long firstCell,
            long lastCell,
            long cellStride,
            long holdCellSpan,
            double safeInset,
            double hitTolerance)
    {
        const int candidateLimit = 256;
        var candidates = new List<(
            TimelinePlacementEvidenceTarget Start,
            TimelinePlacementEvidenceTarget End)>();
        foreach (var startCell in EnumerateEvidenceCells(firstCell, lastCell, cellStride))
        {
            if (startCell > lastCell - holdCellSpan) continue;
            var endCell = startCell + holdCellSpan;
            if (!TryCreateEvidenceBeat(startCell, division, out var startBeat) ||
                !TryCreateEvidenceBeat(endCell, division, out var endBeat))
                continue;
            var startY = EvidencePointerY(startBeat, geometry);
            var endY = EvidencePointerY(endBeat, geometry);
            if (!IsEvidencePointSafe(startY, safeInset) ||
                !IsEvidencePointSafe(endY, safeInset) ||
                session!.IntersectsOwnerProtectedRegion(startBeat, endBeat))
                continue;
            for (var lane = 0; lane < session.Document.KeyCount; lane++)
            {
                if (!IsEvidenceRangeFree(
                        lane,
                        startBeat.Value - hitTolerance,
                        endBeat.Value + hitTolerance))
                    continue;
                var x = geometry.TrackLeft + (lane + .5) * geometry.LaneWidth;
                candidates.Add((
                    new TimelinePlacementEvidenceTarget(lane, startBeat, new Point(x, startY)),
                    new TimelinePlacementEvidenceTarget(lane, endBeat, new Point(x, endY))));
                if (candidates.Count >= candidateLimit) return candidates;
            }
        }
        return candidates;
    }

    private IEnumerable<long> EnumerateEvidenceCells(long firstCell, long lastCell, long stride)
    {
        stride = Math.Max(1, stride);
        var span = lastCell - firstCell;
        for (var offset = 0L; offset < stride && offset <= span; offset++)
        {
            var cell = firstCell + offset;
            while (cell <= lastCell)
            {
                yield return cell;
                if (lastCell - cell < stride) break;
                cell += stride;
            }
        }
    }

    private bool TryCreateEvidenceBeat(long cell, int division, out BeatPosition beat)
    {
        beat = BeatPosition.Zero;
        var whole = Math.DivRem(cell, division, out var subdivision);
        if (whole is < 0 or > int.MaxValue) return false;
        beat = EffectiveNoteAnchor == WindowsNoteAnchorMode.CellCenter
            ? BeatPosition.Preserve(
                (int)whole,
                checked((int)(subdivision * 2 + 1)),
                checked(division * 2))
            : BeatPosition.Preserve(
                (int)whole,
                checked((int)subdivision),
                division);
        return true;
    }

    private bool IsEvidencePointSafe(double y, double safeInset) =>
        double.IsFinite(y) && y >= safeInset && y <= Bounds.Height - safeInset;

    private bool IsEvidenceRangeFree(int lane, double minimumBeat, double maximumBeat)
    {
        noteIndex.Query(minimumBeat, maximumBeat, hitScratch);
        return hitScratch.All(note => note.Lane != lane);
    }

    private static bool RangesIntersect(
        double firstStart,
        double firstEnd,
        double secondStart,
        double secondEnd) =>
        firstStart <= secondEnd && secondStart <= firstEnd;

    public void SetSelectedTiming(Guid? timingId) =>
        SetSelectedTimings(timingId.HasValue ? [timingId.Value] : [], timingId);

    public void SetSubdivisionCheckEnabled(bool enabled)
    {
        if (subdivisionCheckEnabled == enabled) return;
        subdivisionCheckEnabled = enabled;
        InvalidateVisual();
    }

    public void SetHideLongNoteBodies(bool enabled)
    {
        if (hideLongNoteBodies == enabled) return;
        hideLongNoteBodies = enabled;
        InvalidateVisual();
    }

    public void SetSelectedTimings(IEnumerable<Guid> timingIds, Guid? primaryTimingId = null)
    {
        ArgumentNullException.ThrowIfNull(timingIds);
        var next = timingIds.ToHashSet();
        if (session != null)
        {
            var existing = session.Document.Timing.Select(timing => timing.Id).ToHashSet();
            next.IntersectWith(existing);
        }
        var nextPrimary = primaryTimingId.HasValue && next.Contains(primaryTimingId.Value)
            ? primaryTimingId
            : PrimarySelectedTimingId.HasValue && next.Contains(PrimarySelectedTimingId.Value)
                ? PrimarySelectedTimingId
                : next.Cast<Guid?>().FirstOrDefault();
        if (selectedTimingIds.SetEquals(next) && PrimarySelectedTimingId == nextPrimary) return;
        selectedTimingIds.Clear();
        selectedTimingIds.UnionWith(next);
        PrimarySelectedTimingId = nextPrimary;
        TimingSelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void SetCurrentBeat(double beat)
    {
        beat = double.IsFinite(beat) ? Math.Max(MinimumEditableBeat, beat) : 0;
        if (Math.Abs(CurrentBeat - beat) < 1e-9) return;
        CurrentBeat = beat;
        if (marqueeSelecting && session != null)
        {
            marqueeStartPoint = new Point(marqueeStartPoint.X, YAtBeat(marqueeStartBeat, GetGeometry(session.Document.KeyCount)));
            marqueeRect = TimelineMarqueeSelection.Normalize(marqueeStartPoint, marqueePointerPoint);
            if (Environment.TickCount64 - marqueeSelectionFrameTick >= 16)
            {
                marqueeSelectionFrameTick = Environment.TickCount64;
                UpdateMarqueeSelection(marqueePointerPoint);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        InvalidateVisual();
        CurrentBeatChanged?.Invoke(this, beat);
    }

    private void SetCurrentBeatFromManualNavigation(double beat)
    {
        ManualNavigationRequested?.Invoke(
            this,
            new TimelineManualNavigationEventArgs(changesBeat: true));
        SetCurrentBeat(beat);
    }

    public void NavigateToBeat(double beat) => SetCurrentBeatFromManualNavigation(Math.Max(MinimumEditableBeat, beat));

    public void SetViewportAnchor(double position)
    {
        var value = Math.Clamp(position, .05, .98);
        if (Math.Abs(judgementLinePosition - value) < .0001) return;
        judgementLinePosition = value;
        InvalidateAudioScene();
        InvalidateVisual();
    }

    public void SetZoom(double pixelsPerBeat)
    {
        var next = Math.Clamp(pixelsPerBeat, MinimumZoom, MaximumZoom);
        if (Math.Abs(PixelsPerBeat - next) < .001) return;
        PixelsPerBeat = next;
        InvalidateAudioScene();
        InvalidateVisual();
        PixelsPerBeatChanged?.Invoke(this, next);
    }

    public void ConfigureView(
        double laneWidth,
        double beatHeight,
        double requestedNoteWidth,
        double requestedNoteHeight,
        double requestedNoteOpacity,
        double requestedJudgementLinePosition,
        double requestedJudgementLineThickness,
        double requestedJudgementLineOpacity,
        WindowsNoteAnchorMode requestedNoteAnchor = WindowsNoteAnchorMode.CellBottom,
        double requestedBarLineThickness =
            WindowsEditorViewSettings.DefaultBarLineThickness,
        double requestedSubdivisionLineThickness =
            WindowsEditorViewSettings.DefaultSubdivisionLineThickness,
        double requestedTrackOpacity = WindowsEditorViewSettings.DefaultTrackOpacity)
    {
        gridLaneWidth = Math.Clamp(
            double.IsFinite(laneWidth) ? laneWidth : WindowsEditorViewSettings.DefaultGridLaneWidth,
            32,
            180);
        noteWidth = Math.Clamp(
            double.IsFinite(requestedNoteWidth)
                ? requestedNoteWidth
                : WindowsEditorViewSettings.DefaultNoteWidth,
            12,
            172);
        noteHeight = Math.Clamp(
            double.IsFinite(requestedNoteHeight)
                ? requestedNoteHeight
                : WindowsEditorViewSettings.DefaultNoteHeight,
            8,
            128);
        noteOpacity = Math.Clamp(
            double.IsFinite(requestedNoteOpacity) ? requestedNoteOpacity : 1,
            .05,
            1);
        trackOpacity = Math.Clamp(
            double.IsFinite(requestedTrackOpacity)
                ? requestedTrackOpacity
                : WindowsEditorViewSettings.DefaultTrackOpacity,
            0,
            1);
        judgementLinePosition = Math.Clamp(
            double.IsFinite(requestedJudgementLinePosition)
                ? requestedJudgementLinePosition
                : WindowsEditorViewSettings.DefaultJudgementLinePosition,
            .15,
            .95);
        judgementLineThickness = Math.Clamp(
            double.IsFinite(requestedJudgementLineThickness)
                ? requestedJudgementLineThickness
                : WindowsEditorViewSettings.DefaultJudgementLineThickness,
            .5,
            6);
        judgementLineOpacity = Math.Clamp(
            double.IsFinite(requestedJudgementLineOpacity)
                ? requestedJudgementLineOpacity
                : WindowsEditorViewSettings.DefaultJudgementLineOpacity,
            .1,
            1);
        barLineThickness = Math.Clamp(
            double.IsFinite(requestedBarLineThickness)
                ? requestedBarLineThickness
                : WindowsEditorViewSettings.DefaultBarLineThickness,
            .5,
            6);
        subdivisionLineThickness = Math.Clamp(
            double.IsFinite(requestedSubdivisionLineThickness)
                ? requestedSubdivisionLineThickness
                : WindowsEditorViewSettings.DefaultSubdivisionLineThickness,
            .3,
            3);
        noteAnchor = TimelinePlacementGeometry.NormalizeAnchor(requestedNoteAnchor);
        judgementPen = new Pen(JudgementBrush, judgementLineThickness);
        var barStyle = WindowsTimelineVisualParityPolicy.ResolveGridLineStyle(
            WindowsTimelineGridLineKind.Bar);
        barGridPen = new Pen(
            new SolidColorBrush(Color.Parse(barStyle.Color)),
            barLineThickness);
        subdivisionGridPens = Enum
            .GetValues<WindowsTimelineGridLineKind>()
            .ToDictionary(
                kind => kind,
                kind =>
                {
                    var style = WindowsTimelineVisualParityPolicy.ResolveGridLineStyle(kind);
                    return (IPen)new Pen(
                        new SolidColorBrush(Color.Parse(style.Color)),
                        WindowsTimelineVisualParityPolicy.ResolveConfiguredGridLineThickness(
                            kind,
                            subdivisionLineThickness,
                            barLineThickness));
                });
        SetZoom(beatHeight);
        InvalidateAudioScene();
        InvalidateVisual();
        RaiseTrackPanChanged();
    }

    public void SetMainTrackOffset(double offset)
    {
        var next = Math.Clamp(double.IsFinite(offset) ? Math.Round(offset) : 0, -600, 600);
        if (Math.Abs(mainTrackOffset - next) < .001) return;
        mainTrackOffset = next;
        InvalidateAudioScene();
        InvalidateVisual();
        RaiseTrackPanChanged();
    }

    public void SetBackgroundOpacity(double opacity)
    {
        var normalized = Math.Clamp(double.IsFinite(opacity) ? opacity : 0, 0, 1);
        if (Math.Abs(backgroundOpacity - normalized) < .000001) return;
        backgroundOpacity = normalized;
        InvalidateVisual();
    }

    public void SetProEditor(bool enabled)
    {
        if (proEditorEnabled == enabled) return;
        if (touchBlankGesturePending || workspacePanning ||
            dragging || movingTiming || resizingHoldTail ||
            marqueeSelecting || placingNote || erasingStroke)
        {
            ReleaseInteractionCapture();
            ResetPointerInteraction();
        }

        proEditorEnabled = enabled;
        var selectionChanged = PruneNonInteractiveNoteSelection();

        InvalidateVisual();
        RaiseTrackPanChanged();
        if (selectionChanged) SelectionChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, enabled
            ? AvaloniaLocalizationCatalog.Format("ProEditor：{0} 列 · 中键 / Space+左拖 / Shift+滚轮横移", proGridColumnCount)
            : "普通工作区：草稿 Note 已隐藏");
    }

    public void SetProGridColumnCount(int totalColumns)
    {
        var keyCount = session?.Document.KeyCount ?? 1;
        var next = ProEditorWorkspaceGeometry.NormalizeTotalColumns(keyCount, totalColumns);
        if (proGridColumnCount == next) return;
        proGridColumnCount = next;
        InvalidateVisual();
        RaiseTrackPanChanged();
    }

    public ProEditorColumnRange GetWorkspaceColumnRange()
    {
        if (session == null) return new ProEditorColumnRange(0, -1);
        return proEditorEnabled
            ? GetProEditorColumnRange()
            : new ProEditorColumnRange(0, session.Document.KeyCount - 1);
    }

    private ProEditorColumnRange GetProEditorColumnRange()
    {
        if (session == null) return new ProEditorColumnRange(0, -1);
        EnsureIndex();
        var configured = ProEditorWorkspaceGeometry.ResolveColumnRange(
            session.Document.KeyCount,
            proGridColumnCount);
        return noteIndex.DraftColumnExtent is { } drafts
            ? new ProEditorColumnRange(
                Math.Min(configured.Minimum, drafts.Minimum),
                Math.Max(configured.Maximum, drafts.Maximum))
            : configured;
    }

    public ProEditorTrackPanState GetTrackPanState()
    {
        if (session == null || !proEditorEnabled || Bounds.Width <= 0)
            return new ProEditorTrackPanState(0, 0, 0, false);
        var geometry = GetGeometry(session.Document.KeyCount);
        return ProEditorWorkspaceGeometry.ResolveTrackPan(
            Bounds.Width,
            geometry.BaseTrackLeft,
            geometry.OfficialTrackWidth,
            geometry.LaneWidth,
            proTrackOffset,
            enabled: true);
    }

    public Rect GetActiveTrackBounds()
    {
        if (session == null || Bounds.Width <= 0 || Bounds.Height <= 0) return default;
        var geometry = GetGeometry(session.Document.KeyCount);
        return new Rect(
            geometry.TrackLeft,
            0,
            geometry.OfficialTrackWidth,
            Bounds.Height);
    }

    public TimelineProEditorGeometryDiagnostics GetProEditorGeometryDiagnostics()
    {
        if (session == null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return default;
        var bounds = GetActiveTrackBounds();
        return new TimelineProEditorGeometryDiagnostics(
            bounds.Width / Math.Max(1, session.Document.KeyCount),
            bounds.Left,
            bounds.Right);
    }

    public void SetTrackPanOffset(double offset)
    {
        if (session == null || !proEditorEnabled) return;
        var state = GetTrackPanState();
        var next = Math.Clamp(
            double.IsFinite(offset) ? offset : 0,
            state.Minimum,
            state.Maximum);
        if (Math.Abs(proTrackOffset - next) < .001) return;
        proTrackOffset = next;
        InvalidateVisual();
        RaiseTrackPanChanged();
    }

    public void SetSpacePanModifier(bool enabled)
    {
        spacePanModifier = enabled;
        if (enabled) spacePanUsed = false;
    }

    public bool EndSpacePanModifier()
    {
        var used = spacePanUsed;
        spacePanModifier = false;
        spacePanUsed = false;
        return used;
    }

    private void RaiseTrackPanChanged() => TrackPanChanged?.Invoke(this, GetTrackPanState());

    public void SetHiddenGroups(IEnumerable<int> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var next = groups.Where(group => group is >= 0 and <= ushort.MaxValue).ToHashSet();
        if (hiddenGroups.SetEquals(next)) return;
        if (dragging || movingTiming || resizingHoldTail || marqueeSelecting || placingNote || erasingStroke)
        {
            ReleaseInteractionCapture();
            ResetPointerInteraction();
        }
        hiddenGroups = next;
        var selectionChanged = PruneNonInteractiveNoteSelection();
        InvalidateVisual();
        if (selectionChanged) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetOnlineCreators(IReadOnlyDictionary<long, OnlineCreatorAppearance> creators)
    {
        ArgumentNullException.ThrowIfNull(creators);
        if (onlineCreators.Count == creators.Count &&
            creators.All(pair =>
                onlineCreators.TryGetValue(pair.Key, out var current) &&
                string.Equals(current.Name, pair.Value.Name, StringComparison.Ordinal) &&
                ReferenceEquals(current.Avatar, pair.Value.Avatar)))
            return;

        onlineCreators = new Dictionary<long, OnlineCreatorAppearance>(creators);
        creatorTextCache.Clear();
        InvalidateVisual();
    }

    public void SetRoomSyncDiagnostics(
        string workStatus,
        string feedStatus,
        long revision,
        Guid? pendingOperationId,
        int consecutiveWorkFailures,
        int consecutiveFeedFailures)
    {
        roomSyncWorkStatus = string.IsNullOrWhiteSpace(workStatus) ? "none" : workStatus;
        roomSyncFeedStatus = string.IsNullOrWhiteSpace(feedStatus) ? "none" : feedStatus;
        roomSyncRevision = revision;
        roomSyncPendingOperationId = pendingOperationId;
        roomSyncConsecutiveWorkFailures = Math.Max(0, consecutiveWorkFailures);
        roomSyncConsecutiveFeedFailures = Math.Max(0, consecutiveFeedFailures);
    }

    public void ResetRoomSyncDiagnostics() =>
        SetRoomSyncDiagnostics("none", "none", -1, null, 0, 0);

    public void SetPlaybackActive(bool active)
    {
        if (isPlaybackActive == active) return;
        isPlaybackActive = active;
        InvalidateVisual();
    }

    public bool PruneNonInteractiveNoteSelection()
    {
        if (session == null || session.Selection.Count == 0) return false;
        EnsureIndex();
        var interactiveIds = session.Selection
            .Where(id => noteIndex.FindById(id) is { } note &&
                         (proEditorEnabled || !note.IsDraft) &&
                         !hiddenGroups.Contains(note.Group))
            .ToHashSet();
        if (session.Selection.SetEquals(interactiveIds)) return false;
        session.Selection.Clear();
        session.Selection.UnionWith(interactiveIds);
        return true;
    }

    public void SetAudioVisualization(
        AudioVisualization? visualization,
        TimingMap? timingMap,
        bool waveformVisible,
        bool spectrogramVisible)
    {
        if (ReferenceEquals(audioVisualization, visualization) &&
            ReferenceEquals(audioVisualizationTimingMap, timingMap) &&
            backgroundWaveformVisible == waveformVisible &&
            backgroundSpectrogramVisible == spectrogramVisible) return;
        audioVisualization = visualization;
        audioVisualizationTimingMap = timingMap;
        backgroundWaveformVisible = waveformVisible;
        backgroundSpectrogramVisible = spectrogramVisible;
        InvalidateAudioScene();
        InvalidateVisual();
    }

    /// <summary>
    /// Applies the user's background-spectrogram strength (how prominent the
    /// layer is, 0.2–1) and detail (0 = only onsets, 1 = everything above the
    /// noise floor). Strength only changes how the retained scene is drawn;
    /// detail changes the pixels, so it rebuilds the scene.
    /// </summary>
    public void SetSpectrogramAppearance(double strength, double detail)
    {
        var nextStrength = Math.Clamp(double.IsFinite(strength) ? strength : DefaultBackgroundSpectrogramStrength, .2, 1);
        var nextDetail = SpectrogramLevelMapping.NormalizeDetail(detail);
        var detailChanged = Math.Abs(nextDetail - spectrogramDetail) > 1e-6;
        if (!detailChanged && Math.Abs(nextStrength - spectrogramStrength) < 1e-6) return;
        spectrogramStrength = nextStrength;
        spectrogramDetail = nextDetail;
        if (detailChanged && backgroundSpectrogramVisible) CancelAudioSceneBuild();
        InvalidateVisual();
    }

    public TimelineAudioBackgroundDiagnostics GetAudioBackgroundDiagnostics() => new(
        VisualizationAttached: audioVisualization != null && audioVisualizationTimingMap != null,
        WaveformVisible: backgroundWaveformVisible,
        SpectrogramVisible: backgroundSpectrogramVisible,
        SceneReady: audioScene != null,
        BuildPending: audioSceneBuildPending,
        SceneBeat: audioScene?.SceneBeat,
        WaveformRows: audioScene?.WaveformPeaks?.Length ?? 0,
        SpectrogramWidth: audioScene?.Spectrogram?.Width ?? 0,
        SpectrogramRows: audioScene?.Spectrogram?.Height ?? 0,
        SpectrogramBitmapReady: audioSpectrogramBitmap != null);

    public void MarkSyntheticInput()
    {
        Performance.MarkInput();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var startedAt = Performance.BeginRender();
        var visited = 0;
        try
        {
            base.Render(context);
            context.FillRectangle(SurfaceBrush, new Rect(Bounds.Size));
            if (session == null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
            EnsureIndex();

            var geometry = GetGeometry(session.Document.KeyCount);
        var minimumBeat = Math.Max(MinimumEditableBeat, BeatAtY(Bounds.Height + noteHeight, geometry));
        var maximumBeat = Math.Max(minimumBeat, BeatAtY(-noteHeight, geometry));
        var minimumStoredBeat = Math.Max(
            MinimumEditableBeat,
            minimumBeat - GetMaximumStoredRenderBeatAdjustment());
            DrawChartBackground(context);
            // The track mask dims the chart background image so Notes stay
            // readable. The waveform and spectrogram are editing aids, not part
            // of that image: drawn under the mask they lost ~70% of their
            // contrast at the default track opacity and were barely visible.
            DrawTrackMask(context, geometry);
            DrawAudioBackground(context, geometry);
            DrawLaneLines(context, geometry);
            DrawOwnerProtectedRegions(context, geometry, minimumBeat, maximumBeat);
            DrawGrid(context, geometry);
            DrawSymmetryRange(context, geometry);

            using (context.PushOpacity(judgementLineOpacity))
            {
                context.DrawLine(
                    judgementPen,
                    new Point(geometry.GridLeft, geometry.JudgementY),
                    new Point(geometry.GridRight, geometry.JudgementY));
            }
            visited = noteIndex.Query(
                minimumStoredBeat,
                maximumBeat,
                visibleNotes,
                includeDrafts: proEditorEnabled);
            DrawNotes(context, geometry, visibleNotes, draggingSelectionPass: false);
            if (dragging)
            {
                // The document/index stays immutable throughout a drag. Query the
                // source range that maps into the viewport so a long edge-scroll
                // move remains visible without scanning the full selection.
                visited += noteIndex.Query(
                    minimumStoredBeat - previewBeatMove.Delta,
                    maximumBeat - previewBeatMove.Delta,
                    previewNotes,
                    includeDrafts: proEditorEnabled);
                DrawNotes(context, geometry, previewNotes, draggingSelectionPass: true);
            }
            if (pendingPasteNotes != null)
            {
                using var pasteOpacity = context.PushOpacity(.55);
                DrawNotes(context, geometry, pendingPastePreview.Where(n => n.Beat.Value <= maximumBeat && (n.EndBeat ?? n.Beat).Value >= minimumStoredBeat), draggingSelectionPass: false);
            }
            DrawStretchPreview(context, geometry);
            DrawHoldTailResizePreview(context, geometry);
            DrawPlacementPreview(context, geometry);
            DrawPlacementHover(context, geometry);
            DrawTiming(context, geometry, minimumBeat, maximumBeat);
            if (marqueeSelecting)
                context.DrawRectangle(MarqueeFillBrush, MarqueePen, marqueeRect);
            DrawCreatorHoverToolTip(context);
            DrawPatternPasteHint(context);
        }
        finally
        {
            Performance.EndRender(startedAt, visited);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs eventArgs)
    {
        base.OnPointerPressed(eventArgs);
        if (session == null) return;
        if (HandleStretchPress(eventArgs)) return;
        if (HandlePendingPastePress(eventArgs)) return;
        if (eventArgs.Pointer.Type == PointerType.Touch)
        {
            touchContacts[eventArgs.Pointer.Id] = eventArgs.GetPosition(this);
            if (TryBeginTouchScroll())
            {
                eventArgs.Handled = true;
                return;
            }
        }

        // The state machine tracks exactly one captured gesture and its button.
        // A second button pressed mid-gesture (right-click while dragging, left
        // while marquee-selecting) must not start another gesture on top of it;
        // the original button's release still finishes the first one.
        if (touchBlankGesturePending || workspacePanning ||
            dragging || movingTiming || resizingHoldTail ||
            marqueeSelecting || placingNote || erasingStroke)
        {
            if (interactionPointer?.Captured == this)
            {
                eventArgs.Handled = true;
                return;
            }

            // No capture means the old gesture can never see its release. Drop
            // it uncommitted instead of letting it block every later press.
            ReleaseInteractionCapture();
            ResetPointerInteraction();
        }

        var pointerPoint = eventArgs.GetCurrentPoint(this);
        var leftPressed = pointerPoint.Properties.IsLeftButtonPressed;
        var rightPressed = pointerPoint.Properties.IsRightButtonPressed;
        var middlePressed = pointerPoint.Properties.IsMiddleButtonPressed;
        if (!leftPressed && !rightPressed && !middlePressed) return;
        Focus();
        Performance.MarkInput();
        EnsureIndex();
        var point = eventArgs.GetPosition(this);
        var geometry = GetGeometry(session.Document.KeyCount);
        var drawingTool = InteractionProfile == EditorInteractionProfile.OsuMania && (Tool is TimelineTool.Tap or TimelineTool.Hold);
        if (HandleSymmetryPress(eventArgs, point, geometry)) return;
        if (middlePressed && !drawingTool || leftPressed && spacePanModifier && proEditorEnabled)
        {
            workspacePanning = true;
            horizontalPanOnly = !middlePressed;
            verticalPanOnly = false;
            panStartPoint = point;
            panStartBeat = CurrentBeat;
            panStartTrackOffset = proTrackOffset;
            CaptureInteraction(
                eventArgs.Pointer,
                middlePressed ? MouseButton.Middle : MouseButton.Left,
                point,
                useEdgeScroll: false);
            eventArgs.Handled = true;
            return;
        }

        var lane = WorkspaceColumnAtX(point.X, geometry);
        var timingHit = HitTestTimingLabel(point, geometry);
        if (middlePressed && drawingTool && timingHit.HasValue)
        {
            eventArgs.Handled = true;
            return;
        }
        var tailHit = !leftPressed || rightPressed || timingHit.HasValue || Tool == TimelineTool.Erase
            ? null
            : HitTestHoldTail(point, geometry);
        var bodyHit = leftPressed && drawingTool ? HitTestPlacementReplacement(lane, point, geometry) : rightPressed || drawingTool ? HitTestVisibleNote(point, geometry) : lane == NoWorkspaceColumn
            ? null
            : HitTestDisplayedNote(
                lane,
                point.Y,
                geometry,
                excludedGroups: hiddenGroups,
                includeDrafts: proEditorEnabled);
        var hit = tailHit ?? bodyHit;
        if (rightPressed && hit == null && !timingHit.HasValue)
        {
            ClearObjectSelection();
            BlankContextRequested?.Invoke(this, point);
            eventArgs.Handled = true;
            return;
        }

        if (leftPressed &&
            eventArgs.Pointer.Type == PointerType.Touch &&
            Tool != TimelineTool.Select &&
            !timingHit.HasValue &&
            hit == null &&
            (!ProEditorWorkspaceGeometry.IsOfficialColumn(
                 lane,
                 session.Document.KeyCount) ||
             Tool is not (TimelineTool.Tap or TimelineTool.Hold)))
        {
            touchBlankGesturePending = true;
            touchBlankGestureLane = lane;
            touchBlankGestureTool = Tool;
            touchBlankGestureStartedAt = Environment.TickCount64;
            panStartPoint = point;
            panStartBeat = CurrentBeat;
            panStartTrackOffset = proTrackOffset;
            CaptureInteraction(
                eventArgs.Pointer,
                MouseButton.Left,
                point,
                useEdgeScroll: false);
            eventArgs.Handled = true;
            return;
        }

        if (leftPressed && Tool == TimelineTool.Erase)
        {
            if (!CanBeginEraseEdit())
            {
                eventArgs.Handled = true;
                return;
            }
            BeginEraseStroke(point);
            CaptureInteraction(eventArgs.Pointer, MouseButton.Left, point);
            EraseAlongPath(point, point);
            eventArgs.Handled = true;
            return;
        }

        if (timingHit.HasValue)
        {
            var timing = timingHit.Value.Timing;
            if (rightPressed)
            {
                StatusChanged?.Invoke(this, "Timing 请先左键选择，再使用删除按钮或 Delete。");
                eventArgs.Handled = true;
                return;
            }

            if (EditorInteractionPolicy.AdditiveSelection(InteractionProfile, eventArgs.KeyModifiers))
            {
                if (selectedTimingIds.Add(timing.Id)) PrimarySelectedTimingId = timing.Id;
                else
                {
                    selectedTimingIds.Remove(timing.Id);
                    if (PrimarySelectedTimingId == timing.Id)
                        PrimarySelectedTimingId = selectedTimingIds.Cast<Guid?>().FirstOrDefault();
                }
                TimingSelectionChanged?.Invoke(this, EventArgs.Empty);
                StatusChanged?.Invoke(this, AvaloniaLocalizationCatalog.Format("已选择 {0} 个 Timing", selectedTimingIds.Count));
                InvalidateVisual();
                eventArgs.Handled = true;
                return;
            }

            if (session.IsReadOnly || !session.CanEditTiming)
            {
                TimelineDirectObjectSelection.SelectTiming(
                    session.Selection,
                    selectedTimingIds,
                    timing.Id);
                PrimarySelectedTimingId = timing.Id;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                TimingSelectionChanged?.Invoke(this, EventArgs.Empty);
                _ = BeginTimingMove(timing, point);
                InvalidateVisual();
                eventArgs.Handled = true;
                return;
            }

            if (!session.AllowOwnerProtectedEdits && session.IsTimingOwnerProtected(timing))
            {
                StatusChanged?.Invoke(this, "该 Effect 位于房主锁定选区，成员不能移动。");
                eventArgs.Handled = true;
                return;
            }

            // Match the Windows/WPF direct-object contract: wide and mixed
            // selections are retained only by marquee/Shift. A plain Timing
            // gesture always collapses to the hit row before starting a drag.
            TimelineDirectObjectSelection.SelectTiming(
                session.Selection,
                selectedTimingIds,
                timing.Id);
            PrimarySelectedTimingId = timing.Id;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            TimingSelectionChanged?.Invoke(this, EventArgs.Empty);
            if (BeginTimingMove(timing, point))
                CaptureInteraction(eventArgs.Pointer, MouseButton.Left, point);
            InvalidateVisual();
            eventArgs.Handled = true;
            return;
        }

        if (rightPressed)
        {
            if (hit == null)
            {
                if (InteractionProfile == EditorInteractionProfile.OsuMania)
                {
                    eventArgs.Handled = true;
                    return;
                }
                BeginMarquee(point, eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift));
                CaptureInteraction(eventArgs.Pointer, MouseButton.Right, point);
            }
            else
            {
                DeleteHitNote(hit);
            }
            eventArgs.Handled = true;
            return;
        }

        if (leftPressed && drawingTool && !eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control) && tailHit == null)
        {
            if (lane != NoWorkspaceColumn && CanBeginNoteEdit("放置"))
            {
                if (hit != null && !session.AllowOwnerProtectedEdits && session.IsOwnerProtected(hit))
                    StatusChanged?.Invoke(this, "该按键位于锁定选区，不能替换");
                else
                {
                    BeginPlacement(lane, point, Tool);
                    if (placingNote)
                    {
                        placementReplacedNoteId = hit?.Id;
                        placementReplacedCompanionId = null;
                        if (hit != null && SymmetrySettings != null &&
                            CenterSymmetryService.CreateCompanion(hit, SymmetrySettings, session.Document.KeyCount) is { } oldPair)
                            placementReplacedCompanionId = session.Document.Notes.FirstOrDefault(n =>
                                n.IsDraft == hit.IsDraft && CenterSymmetryService.SamePosition(n, oldPair))?.Id;
                        CaptureInteraction(eventArgs.Pointer, MouseButton.Left, point);
                    }
                }
            }
            eventArgs.Handled = true;
            return;
        }
        if (middlePressed && drawingTool && hit == null)
        {
            eventArgs.Handled = true;
            return;
        }

        if (hit == null && (Tool == TimelineTool.Select ||
            InteractionProfile == EditorInteractionProfile.OsuMania && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            BeginMarquee(point, EditorInteractionPolicy.AdditiveSelection(InteractionProfile, eventArgs.KeyModifiers));
            CaptureInteraction(eventArgs.Pointer, MouseButton.Left, point);
            eventArgs.Handled = true;
            return;
        }

        if (hit != null)
        {
            var controlPressed = eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control);
            var shiftPressed = eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var tailResizeRequested = leftPressed && tailHit?.Id == hit.Id &&
                !controlPressed && !shiftPressed;
            if (session.IsReadOnly || !session.CanEditNotes)
            {
                var deniedHadTimingSelection = selectedTimingIds.Count > 0;
                if (EditorInteractionPolicy.AdditiveSelection(InteractionProfile, eventArgs.KeyModifiers))
                {
                    if (!session.Selection.Add(hit.Id)) session.Selection.Remove(hit.Id);
                }
                else
                {
                    TimelineDirectObjectSelection.SelectNote(
                        session.Selection,
                        selectedTimingIds,
                        hit.Id);
                    PrimarySelectedTimingId = null;
                }
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                if (deniedHadTimingSelection && selectedTimingIds.Count == 0)
                    TimingSelectionChanged?.Invoke(this, EventArgs.Empty);
                _ = CanBeginNoteEdit(tailResizeRequested ? "调整 LN 面尾" : "移动");
                InvalidateVisual();
                eventArgs.Handled = true;
                return;
            }
            if (tailResizeRequested)
            {
                if (!session.AllowOwnerProtectedEdits && session.IsOwnerProtected(hit))
                {
                    StatusChanged?.Invoke(this, "该 LN 位于房主锁定选区，成员不能调整面尾");
                    eventArgs.Handled = true;
                    return;
                }
                BeginHoldTailResize(hit);
                if (resizingHoldTail) CaptureInteraction(eventArgs.Pointer, MouseButton.Left, point);
                eventArgs.Handled = true;
                return;
            }
            if (EditorInteractionPolicy.AdditiveSelection(InteractionProfile, eventArgs.KeyModifiers))
            {
                if (!session.Selection.Add(hit.Id)) session.Selection.Remove(hit.Id);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                StatusChanged?.Invoke(this, AvaloniaLocalizationCatalog.Format("已选择 {0} 个 Note", session.Selection.Count));
                InvalidateVisual();
                eventArgs.Handled = true;
                return;
            }

            var hadTimingSelection = selectedTimingIds.Count > 0;
            TimelineDirectObjectSelection.SelectNote(
                session.Selection,
                selectedTimingIds,
                hit.Id,
                preserveNoteBatch: EditorInteractionPolicy.PreserveDragBatch(
                    InteractionProfile, eventArgs.KeyModifiers, session.Selection.Contains(hit.Id)));
            PrimarySelectedTimingId = null;
            if (!CanBeginNoteEdit("移动"))
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                if (hadTimingSelection) TimingSelectionChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
                eventArgs.Handled = true;
                return;
            }
            if (!session.AllowOwnerProtectedEdits && session.IsOwnerProtected(hit))
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                if (hadTimingSelection) TimingSelectionChanged?.Invoke(this, EventArgs.Empty);
                StatusChanged?.Invoke(this, "该 Note 位于房主锁定选区，成员不能拖动");
                InvalidateVisual();
                eventArgs.Handled = true;
                return;
            }
            if (!session.AllowOwnerProtectedEdits)
            {
                var movableIds = session.Document.Notes
                    .Where(note => session.Selection.Contains(note.Id) &&
                                   !hiddenGroups.Contains(note.Group) &&
                                   !session.IsOwnerProtected(note))
                    .Select(note => note.Id)
                    .ToHashSet();
                session.Selection.IntersectWith(movableIds);
            }
            dragStartPoint = point;
            if (!BeginNoteMove(hit.Beat))
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                if (hadTimingSelection) TimingSelectionChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
                eventArgs.Handled = true;
                return;
            }
            dragging = true;
            CaptureInteraction(eventArgs.Pointer, middlePressed ? MouseButton.Middle : MouseButton.Left, point);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            if (hadTimingSelection) TimingSelectionChanged?.Invoke(this, EventArgs.Empty);
            var columnText = hit.IsDraft
                ? AvaloniaLocalizationCatalog.Format("草稿 {0:+0;-0;0}", hit.DraftColumn)
                : AvaloniaLocalizationCatalog.Format("轨道 {0}", hit.Lane + 1);
            StatusChanged?.Invoke(this, AvaloniaLocalizationCatalog.Format("已选择 {0} · {1}", hit.Beat, columnText));
            InvalidateVisual();
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control) || lane == NoWorkspaceColumn)
        {
            eventArgs.Handled = true;
            return;
        }
        if (!CanBeginNoteEdit("放置"))
        {
            eventArgs.Handled = true;
            return;
        }
        BeginPlacement(lane, point, Tool);
        if (placingNote) CaptureInteraction(eventArgs.Pointer, MouseButton.Left, point);
        eventArgs.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs eventArgs)
    {
        base.OnPointerMoved(eventArgs);
        placementHoverPoint = eventArgs.GetPosition(this);
        if (pendingPasteNotes != null) { MovePendingPaste(placementHoverPoint.Value); eventArgs.Handled = true; return; }
        if (HandleStretchMove(eventArgs)) return;
        if (HandleSymmetryMove(eventArgs)) return;
        if (Tool is TimelineTool.Tap or TimelineTool.Hold) InvalidateVisual();
        if (session == null) return;
        if (eventArgs.Pointer.Type == PointerType.Touch &&
            touchContacts.ContainsKey(eventArgs.Pointer.Id))
            touchContacts[eventArgs.Pointer.Id] = eventArgs.GetPosition(this);
        if (touchScrolling)
        {
            Performance.MarkInput();
            SetCurrentBeatFromManualNavigation(
                touchScrollStartBeat +
                (AverageTouchContactY() - touchScrollStartY) / PixelsPerBeat);
            eventArgs.Handled = true;
            return;
        }

        if (touchBlankGesturePending)
        {
            if (eventArgs.Pointer.Captured != this) return;
            var touchPoint = eventArgs.GetPosition(this);
            var deltaX = touchPoint.X - panStartPoint.X;
            var deltaY = touchPoint.Y - panStartPoint.Y;
            if (Math.Sqrt(deltaX * deltaX + deltaY * deltaY) <
                TouchPanActivationDistance)
            {
                eventArgs.Handled = true;
                return;
            }

            Performance.MarkInput();
            touchBlankGesturePending = false;
            var holdPlacementRequested =
                touchBlankGestureTool is TimelineTool.Tap or TimelineTool.Hold &&
                touchBlankGestureLane != NoWorkspaceColumn &&
                Environment.TickCount64 - touchBlankGestureStartedAt >=
                TouchHoldPlacementDelayMilliseconds;
            if (holdPlacementRequested)
            {
                BeginPlacement(
                    touchBlankGestureLane,
                    panStartPoint,
                    touchBlankGestureTool);
                touchBlankGestureLane = NoWorkspaceColumn;
                touchBlankGestureStartedAt = 0;
                if (placingNote)
                {
                    placementCurrentBeat = PointerToNoteBeat(touchPoint.Y);
                    InvalidateVisual();
                }
                else
                {
                    ReleaseInteractionCapture();
                }
                eventArgs.Handled = true;
                return;
            }

            touchBlankGestureLane = NoWorkspaceColumn;
            touchBlankGestureStartedAt = 0;
            workspacePanning = true;
            horizontalPanOnly = false;
            verticalPanOnly = true;
                SetCurrentBeatFromManualNavigation(
                    panStartBeat + deltaY / PixelsPerBeat);
            eventArgs.Handled = true;
            return;
        }
        if (workspacePanning)
        {
            if (eventArgs.Pointer.Captured != this) return;
            Performance.MarkInput();
            var panPoint = eventArgs.GetPosition(this);
            var deltaX = panPoint.X - panStartPoint.X;
            if (!horizontalPanOnly)
                SetCurrentBeatFromManualNavigation(
                    panStartBeat + (panPoint.Y - panStartPoint.Y) / PixelsPerBeat);
            if (proEditorEnabled && !verticalPanOnly)
                SetTrackPanOffset(panStartTrackOffset + deltaX);
            if (horizontalPanOnly && Math.Abs(deltaX) > 2) spacePanUsed = true;
            eventArgs.Handled = true;
            return;
        }
        var hasActiveInteraction = dragging || movingTiming || resizingHoldTail ||
            marqueeSelecting || placingNote || erasingStroke;
        if (!hasActiveInteraction)
        {
            var now = Environment.TickCount64;
            if (now - lastTailHoverHitTestTick < 32) return;
            lastTailHoverHitTestTick = now;
            EnsureIndex();
            var hoverPoint = eventArgs.GetPosition(this);
            var hoverGeometry = GetGeometry(session.Document.KeyCount);
            var timingHit = HitTestTimingLabel(hoverPoint, hoverGeometry);
            Cursor = Tool != TimelineTool.Erase && !timingHit.HasValue &&
                HitTestHoldTail(hoverPoint, hoverGeometry) != null
                    ? HoldTailCursor
                    : ArrowCursor;
            UpdateHoverPresentation(hoverPoint, hoverGeometry, timingHit);
            return;
        }
        if (eventArgs.Pointer.Captured != this) return;
        Performance.MarkInput();
        var point = eventArgs.GetPosition(this);
        edgeScrollPoint = point;
        if (erasingStroke)
        {
            EraseAlongPath(eraseLastPoint, point);
            eraseLastPoint = point;
        }
        else if (movingTiming) UpdateTimingMovePreview(point);
        else if (resizingHoldTail) UpdateHoldTailResizePreview(point);
        else if (placingNote)
        {
            placementCurrentBeat = PointerToDragPreviewBeat(point.Y);
        }
        else if (marqueeSelecting) UpdateMarqueeSelection(point);
        else UpdateMovePreview(point);
        InvalidateVisual();
        eventArgs.Handled = true;
    }

    protected override void OnPointerEntered(PointerEventArgs eventArgs)
    {
        base.OnPointerEntered(eventArgs);
        placementHoverPoint = eventArgs.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs eventArgs)
    {
        base.OnPointerExited(eventArgs);
        placementHoverPoint = null;
        InvalidateVisual();
        if (lastTooltipCreatorId != 0)
        {
            lastTooltipCreatorId = 0;
        }
        if (creatorHoverToolTip != null)
        {
            creatorHoverToolTip = null;
            creatorHoverFormattedText = null;
            InvalidateVisual();
        }
        lastHoverText = null;
    }

    private void UpdateHoverPresentation(
        Point point,
        TimelineGeometry geometry,
        TimelineTimingLabel? timingHit)
    {
        if (session == null) return;
        var lane = WorkspaceColumnAtX(point.X, geometry);
        var noteHit = lane == NoWorkspaceColumn
            ? null
            : HitTestDisplayedNote(
                lane,
                point.Y,
                geometry,
                excludedGroups: hiddenGroups,
                includeDrafts: proEditorEnabled);
        var creatorId = noteHit?.OnlineCreatorUserId ?? 0;
        if (creatorId <= 0) creatorId = timingHit?.Timing.OnlineCreatorUserId ?? 0;
        var creatorName = creatorId > 0 &&
                          onlineCreators.TryGetValue(creatorId, out var creator)
            ? creator.Name
            : null;

        if (creatorId != lastTooltipCreatorId)
        {
            lastTooltipCreatorId = creatorId;
            var toolTipText =
                WindowsOnlineCreatorMarkerPolicy.ToolTipText(creatorName);
            creatorHoverPoint = point;
            creatorHoverToolTip = toolTipText;
            creatorHoverFormattedText = toolTipText == null
                ? null
                : new FormattedText(
                    toolTipText,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    11.5,
                    CreatorToolTipTextBrush);
            InvalidateVisual();
        }

        var prefix = lane >= 0 && lane < session.Document.KeyCount
            ? $"L{lane + 1}"
            : lane != NoWorkspaceColumn
                ? AvaloniaLocalizationCatalog.Format("草稿 {0:+0;-0}", lane)
                : "轨道外";
        var pointerBeat = PointerToNoteBeat(point.Y);
        var text = $"{prefix}  ·  {pointerBeat}" +
                   WindowsOnlineCreatorMarkerPolicy.StatusSuffix(creatorName);
        if (string.Equals(text, lastHoverText, StringComparison.Ordinal)) return;
        lastHoverText = text;
        HoverChanged?.Invoke(this, text);
    }

    private void DrawCreatorHoverToolTip(DrawingContext context)
    {
        if (creatorHoverToolTip == null || creatorHoverFormattedText == null) return;
        const double horizontalPadding = 9;
        const double verticalPadding = 5;
        var width = creatorHoverFormattedText.WidthIncludingTrailingWhitespace +
                    horizontalPadding * 2;
        var height = creatorHoverFormattedText.Height + verticalPadding * 2;
        var x = Math.Clamp(
            creatorHoverPoint.X + 14,
            4,
            Math.Max(4, Bounds.Width - width - 4));
        var requestedY = creatorHoverPoint.Y + 18;
        if (requestedY + height > Bounds.Height - 4)
            requestedY = creatorHoverPoint.Y - height - 12;
        var y = Math.Clamp(
            requestedY,
            4,
            Math.Max(4, Bounds.Height - height - 4));
        var bounds = new Rect(x, y, width, height);
        context.DrawRectangle(
            CreatorToolTipBackgroundBrush,
            CreatorToolTipPen,
            bounds,
            3,
            3);
        context.DrawText(
            creatorHoverFormattedText,
            new Point(x + horizontalPadding, y + verticalPadding));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs eventArgs)
    {
        base.OnPointerReleased(eventArgs);
        if (HandleStretchRelease(eventArgs)) return;
        if (HandleSymmetryRelease(eventArgs)) return;
        if (eventArgs.Pointer.Type == PointerType.Touch)
        {
            touchContacts.Remove(eventArgs.Pointer.Id);
            if (EndTouchScrollIfLifted())
            {
                eventArgs.Handled = true;
                return;
            }
        }

        if ((!touchBlankGesturePending && !workspacePanning &&
             !dragging && !movingTiming && !resizingHoldTail &&
             !marqueeSelecting && !placingNote && !erasingStroke) ||
            session == null) return;
        if (!IsInteractionButtonRelease(eventArgs)) return;
        Performance.MarkInput();

        if (touchBlankGesturePending)
        {
            var lane = touchBlankGestureLane;
            var gestureTool = touchBlankGestureTool;
            var touchPoint = panStartPoint;
            touchBlankGesturePending = false;
            touchBlankGestureLane = NoWorkspaceColumn;
            touchBlankGestureStartedAt = 0;
            if (lane != NoWorkspaceColumn &&
                gestureTool is TimelineTool.Tap or TimelineTool.Hold)
            {
                BeginPlacement(lane, touchPoint, gestureTool);
                if (placingNote)
                    FinishPlacement(touchPoint);
                else
                    ReleaseInteractionCapture();
            }
            else
            {
                ReleaseInteractionCapture();
            }
            eventArgs.Handled = true;
            return;
        }

        if (workspacePanning)
        {
            workspacePanning = false;
            horizontalPanOnly = false;
            verticalPanOnly = false;
            ReleaseInteractionCapture();
            eventArgs.Handled = true;
            return;
        }

        if (erasingStroke)
        {
            var releasePoint = eventArgs.GetPosition(this);
            EraseAlongPath(eraseLastPoint, releasePoint);
            FinishEraseStroke();
            ReleaseInteractionCapture();
            InvalidateVisual();
            eventArgs.Handled = true;
            return;
        }

        if (movingTiming)
        {
            FinishTimingMove(eventArgs.GetPosition(this));
            eventArgs.Handled = true;
            return;
        }

        if (resizingHoldTail)
        {
            UpdateHoldTailResizePreview(eventArgs.GetPosition(this));
            FinishHoldTailResize(releaseCapture: true);
            InvalidateVisual();
            eventArgs.Handled = true;
            return;
        }

        if (placingNote)
        {
            FinishPlacement(eventArgs.GetPosition(this));
            eventArgs.Handled = true;
            return;
        }

        if (marqueeSelecting)
        {
            // A fast drag can release without a final move event. Re-sample here so
            // the committed selection is exactly the rectangle last shown to users.
            UpdateMarqueeSelection(eventArgs.GetPosition(this));
            marqueeSelecting = false;
            marqueeRect = default;
            marqueeStartBeat = 0;
            marqueeBaseSelection.Clear();
            marqueeNextSelection.Clear();
            marqueeBaseTimingSelection.Clear();
            marqueeNextTimingSelection.Clear();
            marqueeCandidates.Clear();
            if (!PrimarySelectedTimingId.HasValue || !selectedTimingIds.Contains(PrimarySelectedTimingId.Value))
                PrimarySelectedTimingId = selectedTimingIds.Cast<Guid?>().FirstOrDefault();
            ReleaseInteractionCapture();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            TimingSelectionChanged?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke(this,
                AvaloniaLocalizationCatalog.Format("已选择 {0} 个 Note · {1} 个 Timing", session.Selection.Count, selectedTimingIds.Count));
            InvalidateVisual();
            eventArgs.Handled = true;
            return;
        }

        UpdateMovePreview(eventArgs.GetPosition(this));
        if (!dragging)
        {
            eventArgs.Handled = true;
            return;
        }
        var laneDelta = previewLaneDelta;
        var beatMove = previewBeatMove;
        var expectedSerial = noteMoveStartChangeSerial;
        var selectedIds = session.Selection.ToArray();
        ClearNoteMoveState();
        ReleaseInteractionCapture();
        if (!CanBeginNoteEdit("移动"))
        {
            InvalidateVisual();
            eventArgs.Handled = true;
            return;
        }
        try
        {
            var moved = WindowsProEditorCommand.MoveSelection(
                session,
                laneDelta,
                beatMove,
                GetWorkspaceColumnRange(),
                selectedIds,
                hiddenGroups,
                expectedSerial);
            if (moved > 0)
            {
                var note = session.Document.Notes.First(candidate => session.Selection.Contains(candidate.Id));
                StatusChanged?.Invoke(this, AvaloniaLocalizationCatalog.Format("已移动 {0} 个 Note，精确位置 {1}", moved, note.Beat));
            }
        }
        catch (Exception exception) when (IsExpectedEditException(exception))
        {
            StatusChanged?.Invoke(this, exception.Message);
        }
        InvalidateVisual();
        eventArgs.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs eventArgs)
    {
        base.OnPointerCaptureLost(eventArgs);
        CancelStretchDrag();
        if (symmetryDragEdge != null)
        {
            symmetryDragEdge = null; symmetryPointer = null;
            SymmetryRangeChanged?.Invoke(this, SymmetrySettings);
        }
        touchContacts.Remove(eventArgs.Pointer.Id);
        EndTouchScrollIfLifted();
        interactionPointer = null;
        var selectionWasActive = marqueeSelecting;
        // Losing capture is not a release: the window was deactivated, another
        // control took the pointer, or the surface was reset. Committing here
        // would write an erase / LN tail the user never confirmed, so both are
        // abandoned like the other uncommitted previews.
        if (erasingStroke) CancelEraseStroke("连续擦除已取消");
        if (resizingHoldTail) CancelHoldTailResize("LN 面尾调整已取消");
        ResetPointerInteraction();
        if (selectionWasActive && session != null)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            TimingSelectionChanged?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke(this,
                AvaloniaLocalizationCatalog.Format("已选择 {0} 个 Note · {1} 个 Timing", session.Selection.Count, selectedTimingIds.Count));
        }
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs eventArgs)
    {
        base.OnPointerWheelChanged(eventArgs);
        HandleWheel(eventArgs);
    }

    /// <summary>
    /// Applies a wheel gesture that landed on a companion view aligned with this
    /// timeline, such as the waveform or spectrogram side panel. Scrolling over
    /// the waveform is where people naturally reach to move through the song;
    /// the panels used to swallow that gesture and do nothing.
    /// </summary>
    public void HandleCompanionWheel(PointerWheelEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        if (eventArgs.Handled) return;
        HandleWheel(eventArgs);
    }

    public void ClearObjectSelection()
    {
        if (session == null) return;
        var hadNotes = session.Selection.Count > 0;
        var hadTiming = selectedTimingIds.Count > 0;
        session.Selection.Clear();
        selectedTimingIds.Clear();
        PrimarySelectedTimingId = null;
        if (hadNotes) SelectionChanged?.Invoke(this, EventArgs.Empty);
        if (hadTiming) TimingSelectionChanged?.Invoke(this, EventArgs.Empty);
        if (hadNotes || hadTiming) InvalidateVisual();
    }

    private void HandleWheel(PointerWheelEventArgs eventArgs)
    {
        if (session == null) return;
        if (HandlePatternPasteWheel(eventArgs)) return;
        if (SynchronizedNavigationSource is { } main && !ReferenceEquals(main, this))
        {
            main.HandleCompanionWheel(eventArgs);
            return;
        }
        Performance.MarkInput();
        if (InteractionProfile == EditorInteractionProfile.OsuMania && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var direction = Math.Sign(eventArgs.Delta.Y);
            if (direction != 0) SnapDivisionChangeRequested?.Invoke(this, direction);
        }
        else if (InteractionProfile == EditorInteractionProfile.OsuMania && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var direction = Math.Sign(eventArgs.Delta.Y);
            if (direction != 0) PlaybackRateChangeRequested?.Invoke(this, direction);
        }
        else if (proEditorEnabled && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            ManualNavigationRequested?.Invoke(
                this,
                new TimelineManualNavigationEventArgs(changesBeat: false));
            SetTrackPanOffset(proTrackOffset +
                (eventArgs.Delta.Y > 0 ? gridLaneWidth : -gridLaneWidth));
        }
        else if (EditorInteractionPolicy.ZoomWheel(InteractionProfile, eventArgs.KeyModifiers))
        {
            ManualNavigationRequested?.Invoke(
                this,
                new TimelineManualNavigationEventArgs(changesBeat: false));
            SetZoom(PixelsPerBeat * (eventArgs.Delta.Y > 0 ? 1.08 : 1 / 1.08));
            ZoomChanged?.Invoke(this, PixelsPerBeat);
        }
        else if (WindowsTimelineVisualParityPolicy.ResolveWheelDirection(
                     ReverseTimeWheel ? -eventArgs.Delta.Y : eventArgs.Delta.Y) is { } direction &&
                 direction != 0)
        {
            if (isPlaybackActive && InteractionProfile == EditorInteractionProfile.OsuMania)
            {
                var map = audioVisualizationTimingMap ?? new TimingMap(session.Document.Timing);
                var milliseconds = map.BeatToMilliseconds(BeatPosition.FromDouble(CurrentBeat, BeatPosition.MaximumPreservedDenominator)) +
                    (ReverseTimeWheel ? -eventArgs.Delta.Y : eventArgs.Delta.Y) * PlaybackWheelStepMilliseconds;
                SetCurrentBeatFromManualNavigation(Math.Max(MinimumEditableBeat, map.MillisecondsToBeat(milliseconds, BeatPosition.MaximumPreservedDenominator).Value));
            }
            else SetCurrentBeatFromManualNavigation(AllowsNegativeBeats
                ? TimelinePlacementGeometry.NearestGrid(CurrentBeat + direction / (double)session.SnapDivision, session.SnapDivision).Value
                : TimelineGridNavigation.Step(CurrentBeat, session.SnapDivision, direction).Value);
        }
        eventArgs.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        base.OnKeyDown(eventArgs);
        if (eventArgs.Key == Key.Escape && IsStretching) { CancelStretch(); eventArgs.Handled = true; return; }
        if (eventArgs.Key == Key.Escape && pendingPasteNotes != null) { CancelPendingPaste(); eventArgs.Handled = true; return; }
        if (eventArgs.Key == Key.Escape && IsDefiningSymmetry)
        { CancelSymmetryRange(); eventArgs.Handled = true; return; }
        if (session == null) return;
        var direction = eventArgs.Key switch
        {
            Key.Up => 1,
            Key.Down => -1,
            _ => 0
        };
        if (direction == 0) return;
        Performance.MarkInput();
        if (!CanBeginNoteEdit("移动"))
        {
            eventArgs.Handled = true;
            return;
        }
        try
        {
            var movableIds = WindowsGroupManagerCommand.VisibleEditableSelectionIds(
                session,
                hiddenGroups);
            var moved = BasicChartEditingService.NudgeSelection(session, direction, movableIds);
            if (moved > 0)
            {
                var note = session.Document.Notes.First(candidate => movableIds.Contains(candidate.Id));
                StatusChanged?.Invoke(this, AvaloniaLocalizationCatalog.Format("分度移动 {0} 个 Note → {1}", moved, note.Beat));
            }
        }
        catch (Exception exception) when (IsExpectedEditException(exception))
        {
            StatusChanged?.Invoke(this, exception.Message);
        }
        eventArgs.Handled = true;
    }

    private void EnsureIndex()
    {
        if (session == null || indexedChangeSerial == session.ChangeSerial) return;
        noteIndex.Rebuild(session.Document.Notes);
        timingIndex.Rebuild(session.Document.Timing);
        timingSourceOrders.Clear();
        for (var index = 0; index < session.Document.Timing.Count; index++)
            timingSourceOrders[session.Document.Timing[index].Id] = index;
        chartIssues = ChartValidator.Validate(session.Document);
        validationIssueNoteIds = chartIssues
            .SelectMany(issue => issue.NoteIds)
            .ToHashSet();
        indexedChangeSerial = session.ChangeSerial;
        ScheduleChartIssuesChanged();
    }

    private void ScheduleChartIssuesChanged()
    {
        if (chartIssuesChangedPending) return;
        chartIssuesChangedPending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                chartIssuesChangedPending = false;
                ChartIssuesChanged?.Invoke(this, EventArgs.Empty);
            },
            DispatcherPriority.Background);
    }

    private void Session_Changed(object? sender, EventArgs eventArgs)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Session_Changed(sender, eventArgs));
            return;
        }
        if (!ReferenceEquals(sender, session)) return;

        indexedChangeSerial = long.MinValue;
        if (dragging && session != null &&
            TimelineNoteMovePreview.IsInterrupted(noteMoveStartChangeSerial, session.ChangeSerial))
            CancelNoteMove("Note 移动已按房间最新状态重新同步");
        if (resizingHoldTail && session != null &&
            TimelineHoldTailResize.IsInterrupted(resizingHoldStartChangeSerial, session.ChangeSerial))
            CancelHoldTailResize("LN 面尾调整已按房间最新状态重新同步");
        if (movingTiming && session != null)
        {
            var remoteInterrupted = TimelineTimingMovePreview.IsInterrupted(
                timingMoveStartChangeSerial,
                session.ChangeSerial);
            // Save markers (MarkSaved/TryMarkSaved) and owner-lock bookkeeping
            // raise Changed without touching the document, and autosave fires
            // them at any time. Only a new serial or a swapped document instance
            // (restore, ReplaceDocument) invalidates the preview; the commit
            // itself re-filters owner-locked Timing by id.
            if (remoteInterrupted || !ReferenceEquals(session.Document, timingMoveStartDocument))
                CancelTimingMove(
                    remoteInterrupted
                        ? "时间记录移动已按房间最新状态重新同步"
                        : "会话状态已变化，时间记录移动预览已取消");
        }
        if (erasingStroke && session != null && session.ChangeSerial != eraseStartChangeSerial)
            CancelEraseStroke("连续擦除已按房间最新状态重新同步");
        if (session != null)
        {
            if (PruneNonInteractiveNoteSelection())
                SelectionChanged?.Invoke(this, EventArgs.Empty);

            var existingTimingIds = session.Document.Timing.Select(timing => timing.Id).ToHashSet();
            if (selectedTimingIds.RemoveWhere(id => !existingTimingIds.Contains(id)) > 0)
            {
                if (!PrimarySelectedTimingId.HasValue || !selectedTimingIds.Contains(PrimarySelectedTimingId.Value))
                    PrimarySelectedTimingId = selectedTimingIds.Cast<Guid?>().FirstOrDefault();
                TimingSelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        InvalidateVisual();
    }

    private bool CanBeginNoteEdit(string operation)
    {
        if (session == null) return false;
        var denial = TimelineEditPreflight.GetNoteEditDenial(session, operation);
        if (denial == null) return true;
        StatusChanged?.Invoke(this, denial);
        EditPermissionDenied?.Invoke(this, "Note");
        return false;
    }

    private bool CanBeginEraseEdit()
    {
        if (session == null) return false;
        if (session.IsReadOnly)
        {
            StatusChanged?.Invoke(this, "当前谱面为只读状态，不能擦除 Note / Effect。");
            EditPermissionDenied?.Invoke(this, "Note / Effect");
            return false;
        }
        if (session.CanEditNotes || session.CanEditTiming) return true;
        StatusChanged?.Invoke(this, "当前没有编辑 Note / Effect 的权限。");
        EditPermissionDenied?.Invoke(this, "Note / Effect");
        return false;
    }

    private bool BeginTimingMove(TimingEvent anchor, Point point)
    {
        if (session == null) return false;
        if (session.IsReadOnly)
        {
            StatusChanged?.Invoke(this, "当前谱面为只读状态，不能移动 Timing / Effect。");
            EditPermissionDenied?.Invoke(this, "Effect");
            return false;
        }
        if (!session.CanEditTiming)
        {
            StatusChanged?.Invoke(this, "当前没有编辑 Timing / Effect 的权限。");
            EditPermissionDenied?.Invoke(this, "Effect");
            return false;
        }
        if (!session.AllowOwnerProtectedEdits && session.IsTimingOwnerProtected(anchor))
        {
            StatusChanged?.Invoke(this, "该 Effect 位于房主锁定选区，成员不能移动。");
            return false;
        }

        ClearTimingMoveState();
        var selected = session.Document.Timing
            .Where(timing => selectedTimingIds.Contains(timing.Id))
            .ToArray();
        timingMoveRequestedIds.UnionWith(selected.Select(timing => timing.Id));
        foreach (var timing in selected.Where(timing =>
                     session.AllowOwnerProtectedEdits || !session.IsTimingOwnerProtected(timing)))
        {
            timingMoveOrigins[timing.Id] = timing.Beat;
            timingMovePreviewEvents.Add(timing.Clone());
        }
        if (!timingMoveOrigins.TryGetValue(anchor.Id, out var anchorOrigin))
        {
            ClearTimingMoveState();
            StatusChanged?.Invoke(this, "选中的时间记录均位于房主锁定选区，成员不能移动。");
            return false;
        }

        timingMoveAnchorId = anchor.Id;
        timingMoveAnchorOrigin = anchorOrigin;
        timingMoveMinimumOrigin = timingMoveOrigins.Values.Aggregate(
            (left, right) => left.CompareTo(right) <= 0 ? left : right);
        timingMovePreviewAnchor = anchorOrigin;
        timingMovePointerStartBeat = BeatAtY(point.Y, GetGeometry(session.Document.KeyCount));
        timingMovePreviewDelta = 0;
        timingMoveStartChangeSerial = session.ChangeSerial;
        timingMoveStartDocument = session.Document;
        timingMovePreviewIndex.Rebuild(timingMovePreviewEvents);
        movingTiming = true;
        StatusChanged?.Invoke(this, AvaloniaLocalizationCatalog.Format("已选择 Timing {0} · 拖动 {1} 个时间记录", anchor.Beat, timingMoveOrigins.Count));
        return true;
    }

    private void UpdateTimingMovePreview(Point point)
    {
        if (session == null || !movingTiming) return;
        if (TimelineTimingMovePreview.IsInterrupted(timingMoveStartChangeSerial, session.ChangeSerial))
        {
            CancelTimingMove("时间记录移动已按房间最新状态重新同步");
            return;
        }

        var pointerBeat = BeatAtY(point.Y, GetGeometry(session.Document.KeyCount));
        var requestedAnchor = TimelineTimingMovePreview.RequestedAnchorBeat(
            timingMoveAnchorOrigin,
            timingMovePointerStartBeat,
            pointerBeat);
        var preview = TimelineTimingMovePreview.Resolve(
            timingMoveAnchorOrigin,
            timingMoveMinimumOrigin,
            requestedAnchor,
            session.SnapDivision);
        if (!session.AllowOwnerProtectedEdits && timingMoveOrigins.Values.Any(origin =>
                session.IsBeatOwnerProtected(TimelineTimingMovePreview.Apply(
                    origin,
                    preview.BeatDelta,
                    session.SnapDivision))))
        {
            StatusChanged?.Invoke(this, "目标位置位于房主锁定选区，成员不能移动时间记录。");
            return;
        }
        if (preview.AnchorBeat == timingMovePreviewAnchor &&
            Math.Abs(preview.BeatDelta - timingMovePreviewDelta) < .0000001) return;

        timingMovePreviewAnchor = preview.AnchorBeat;
        timingMovePreviewDelta = preview.BeatDelta;
        foreach (var timing in timingMovePreviewEvents)
            timing.Beat = TimelineTimingMovePreview.Apply(
                timingMoveOrigins[timing.Id],
                timingMovePreviewDelta,
                session.SnapDivision);
        timingMovePreviewIndex.Rebuild(timingMovePreviewEvents);
        StatusChanged?.Invoke(this,
            AvaloniaLocalizationCatalog.Format("移动 {0} 个时间记录 · {1}", timingMoveOrigins.Count, timingMovePreviewAnchor));
    }

    private void FinishTimingMove(Point point)
    {
        if (session == null || !movingTiming) return;
        if (TimelineTimingMovePreview.IsInterrupted(timingMoveStartChangeSerial, session.ChangeSerial))
        {
            CancelTimingMove("时间记录移动已按房间最新状态重新同步");
            return;
        }

        var activeSession = session;
        var pointerBeat = BeatAtY(point.Y, GetGeometry(activeSession.Document.KeyCount));
        var pointerDelta = pointerBeat - timingMovePointerStartBeat;
        UpdateTimingMovePreview(point);
        if (!movingTiming) return;
        var requestedAnchor = timingMovePreviewAnchor;
        var requestedIds = timingMoveRequestedIds.ToArray();
        var anchorId = timingMoveAnchorId;
        ClearTimingMoveState();
        ReleaseInteractionCapture();

        // A click selects Timing without rewriting a legitimately authored
        // off-grid expression. Any actual vertical displacement is committed by
        // exactly one shared command below, even when it snaps back to a no-op.
        if (Math.Abs(pointerDelta) < .0000001)
        {
            StatusChanged?.Invoke(this, AvaloniaLocalizationCatalog.Format("已选择 {0} 个 Timing", selectedTimingIds.Count));
            InvalidateVisual();
            return;
        }

        try
        {
            var result = TimingBatchMoveService.MoveTo(
                activeSession,
                requestedIds,
                anchorId,
                requestedAnchor);
            var selectionChanged = !selectedTimingIds.SetEquals(result.MovableTimingIds);
            selectedTimingIds.Clear();
            selectedTimingIds.UnionWith(result.MovableTimingIds);
            PrimarySelectedTimingId = selectedTimingIds.Contains(anchorId)
                ? anchorId
                : selectedTimingIds.Cast<Guid?>().FirstOrDefault();
            if (selectionChanged) TimingSelectionChanged?.Invoke(this, EventArgs.Empty);

            if (result.MovedCount > 0)
            {
                var filtered = result.FilteredTimingIds.Count > 0
                    ? AvaloniaLocalizationCatalog.Format(" · 已跳过 {0} 个锁定条目", result.FilteredTimingIds.Count)
                    : string.Empty;
                StatusChanged?.Invoke(this,
                    AvaloniaLocalizationCatalog.Format("已移动 {0} 个时间记录 → {1}{2}", result.MovedCount, result.AnchorBeat, filtered));
            }
            else
            {
                StatusChanged?.Invoke(this, "Timing 位置未变化");
            }
        }
        catch (Exception exception) when (IsExpectedEditException(exception))
        {
            StatusChanged?.Invoke(this, exception.Message);
        }
        InvalidateVisual();
    }

    private void CancelTimingMove(string? status = null)
    {
        if (!movingTiming) return;
        ClearTimingMoveState();
        ReleaseInteractionCapture();
        if (!string.IsNullOrWhiteSpace(status)) StatusChanged?.Invoke(this, status);
        InvalidateVisual();
    }

    private void ClearTimingMoveState()
    {
        movingTiming = false;
        timingMoveAnchorId = Guid.Empty;
        timingMoveAnchorOrigin = BeatPosition.Zero;
        timingMoveMinimumOrigin = BeatPosition.Zero;
        timingMovePreviewAnchor = BeatPosition.Zero;
        timingMovePointerStartBeat = 0;
        timingMovePreviewDelta = 0;
        timingMoveStartChangeSerial = 0;
        timingMoveStartDocument = null;
        timingMoveRequestedIds.Clear();
        timingMoveOrigins.Clear();
        timingMovePreviewEvents.Clear();
        visibleTimingMovePreviews.Clear();
        timingDrawEvents.Clear();
    }

    private void FinishPlacement(Point point)
    {
        if (session == null || !placingNote) return;
        placementCurrentBeat = PointerToNoteBeat(point.Y);
        TimelinePlacement placement;
        try
        {
            placement = TimelinePlacementGeometry.Resolve(
                placementGestureTool,
                placementStartBeat,
                placementCurrentBeat,
                session.SnapDivision);
        }
        catch (Exception exception) when (IsExpectedEditException(exception))
        {
            placingNote = false;
            placementLane = NoWorkspaceColumn;
            ReleaseInteractionCapture();
            StatusChanged?.Invoke(this, exception.Message);
            InvalidateVisual();
            return;
        }

        var lane = placementLane;
        var replacedNoteId = placementReplacedNoteId;
        var expectedSerial = placementStartChangeSerial;
        placingNote = false;
        placementReplacedNoteId = null;
        placementReplacedCompanionId = null;
        placementLane = NoWorkspaceColumn;
        ReleaseInteractionCapture();
        if (!CanBeginNoteEdit("放置"))
        {
            InvalidateVisual();
            return;
        }
        try
        {
            var placedId = placement.Kind == TimelinePlacementKind.Hold
                ? WindowsProEditorCommand.PlaceHold(
                    session,
                    lane,
                    placement.StartBeat,
                    placement.EndBeat!.Value,
                    GetWorkspaceColumnRange(),
                    BasicBeatPlacementPolicy.PreserveAuthoredBeat,
                    PlacementGroup, replacedNoteId, expectedSerial, SymmetrySettings)
                : WindowsProEditorCommand.PlaceTap(
                    session,
                    lane,
                    placement.StartBeat,
                    GetWorkspaceColumnRange(),
                    BasicBeatPlacementPolicy.PreserveAuthoredBeat,
                    PlacementGroup, replacedNoteId, expectedSerial, SymmetrySettings);
            if (placedId.HasValue)
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                StatusChanged?.Invoke(this, placement.Kind == TimelinePlacementKind.Hold
                    ? AvaloniaLocalizationCatalog.Format("已放置 LN {0} → {1}", placement.StartBeat, placement.EndBeat)
                    : ProEditorWorkspaceGeometry.IsOfficialColumn(lane, session.Document.KeyCount)
                        ? AvaloniaLocalizationCatalog.Format("已放置 Note {0} · 轨道 {1}", placement.StartBeat, lane + 1)
                        : AvaloniaLocalizationCatalog.Format("已放置 Note {0} · 草稿 {1:+0;-0;0}", placement.StartBeat, lane));
            }
            else
            {
                StatusChanged?.Invoke(this, "该位置已有 Note，未创建重复对象");
            }
        }
        catch (Exception exception) when (IsExpectedEditException(exception))
        {
            StatusChanged?.Invoke(this, exception.Message);
        }
        InvalidateVisual();
    }

    private void BeginPlacement(int lane, Point point, TimelineTool gestureTool)
    {
        if (session == null || gestureTool is not (TimelineTool.Tap or TimelineTool.Hold)) return;
        var beat = PointerToNoteBeat(point.Y);
        if (!session.AllowOwnerProtectedEdits && session.IsBeatOwnerProtected(beat))
        {
            StatusChanged?.Invoke(this, "该位置位于房主锁定选区，成员不能放置 Note");
            return;
        }
        placingNote = true;
        placementReplacedNoteId = null;
        placementReplacedCompanionId = null;
        placementStartChangeSerial = session.ChangeSerial;
        placementLane = lane;
        placementStartBeat = beat;
        placementCurrentBeat = beat;
        placementGestureTool = gestureTool;
        InvalidateVisual();
    }

    private void BeginEraseStroke(Point point)
    {
        erasingStroke = true;
        eraseStrokeChanged = false;
        eraseLockedStatusShown = false;
        eraseStartChangeSerial = session?.ChangeSerial ?? 0;
        eraseLastPoint = point;
        erasedNoteIds.Clear();
        eraseSegmentNoteIds.Clear();
        erasedTimingIds.Clear();
        eraseSegmentTimingIds.Clear();
        eraseTimingLabels.Clear();
        InvalidateVisual();
    }

    private void EraseAlongPath(Point from, Point to)
    {
        if (session == null || !erasingStroke) return;
        EnsureIndex();
        var geometry = GetGeometry(session.Document.KeyCount);
        var deltaX = to.X - from.X;
        var deltaY = to.Y - from.Y;
        var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        var samples = Math.Clamp((int)Math.Ceiling(distance / 6), 1, 4096);
        var lockedHit = false;
        eraseSegmentNoteIds.Clear();
        eraseSegmentTimingIds.Clear();
        eraseTimingLabels.Clear();


        for (var sampleIndex = 0; sampleIndex <= samples; sampleIndex++)
        {
            var amount = (double)sampleIndex / samples;
            var sample = new Point(from.X + deltaX * amount, from.Y + deltaY * amount);
            if (session.CanEditNotes)
            {
                var lane = WorkspaceColumnAtX(sample.X, geometry);
                if (lane != NoWorkspaceColumn)
                {
                    var noteHit = HitTestDisplayedNote(
                        lane,
                        sample.Y,
                        geometry,
                        excludedIds: erasedNoteIds,
                        excludedGroups: hiddenGroups,
                        includeDrafts: proEditorEnabled);
                    if (noteHit != null)
                    {
                        if (!session.AllowOwnerProtectedEdits && session.IsOwnerProtected(noteHit))
                            lockedHit = true;
                        else
                            eraseSegmentNoteIds.Add(noteHit.Id);
                    }
                }
            }


        }

        if (eraseSegmentNoteIds.Count == 0 && eraseSegmentTimingIds.Count == 0)
        {
            if (lockedHit && !eraseLockedStatusShown)
            {
                eraseLockedStatusShown = true;
                StatusChanged?.Invoke(this, "房主锁定选区中的 Note / Effect 未被擦除");
            }
            return;
        }
        erasedNoteIds.UnionWith(eraseSegmentNoteIds);
        erasedTimingIds.UnionWith(eraseSegmentTimingIds);
        eraseStrokeChanged = true;
        StatusChanged?.Invoke(this,
            AvaloniaLocalizationCatalog.Format("持续擦除 · Note {0} · Effect {1}", erasedNoteIds.Count, erasedTimingIds.Count));
        InvalidateVisual();
    }

    private void FinishEraseStroke()
    {
        if (!erasingStroke) return;
        if (session != null && session.ChangeSerial != eraseStartChangeSerial)
        {
            CancelEraseStroke("连续擦除已按房间最新状态重新同步");
            return;
        }

        // Stop treating session.Changed from the one final mutation as a remote
        // interruption; the document has remained untouched throughout preview.
        erasingStroke = false;
        var removedNotes = 0;
        var removedEffects = 0;
        if (session != null && eraseStrokeChanged &&
            (erasedNoteIds.Count > 0 || erasedTimingIds.Count > 0))
        {
            try
            {
                var result = TimelineEraseCommit.Commit(session, CenterSymmetryService.ExpandDeletion(session, erasedNoteIds, SymmetrySettings), erasedTimingIds);
                removedNotes = result.DeletedNoteCount;
                removedEffects = result.DeletedEffectCount;
                if (result.DeletedTimingIds.Count > 0)
                {
                    selectedTimingIds.ExceptWith(result.DeletedTimingIds);
                    if (!PrimarySelectedTimingId.HasValue || !selectedTimingIds.Contains(PrimarySelectedTimingId.Value))
                        PrimarySelectedTimingId = selectedTimingIds.Cast<Guid?>().FirstOrDefault();
                }
            }
            catch (Exception exception) when (IsExpectedEditException(exception))
            {
                StatusChanged?.Invoke(this, exception.Message);
            }
        }

        eraseStrokeChanged = false;
        eraseLockedStatusShown = false;
        eraseStartChangeSerial = 0;
        erasedNoteIds.Clear();
        eraseSegmentNoteIds.Clear();
        erasedTimingIds.Clear();
        eraseSegmentTimingIds.Clear();
        eraseTimingLabels.Clear();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        TimingSelectionChanged?.Invoke(this, EventArgs.Empty);
        if (removedNotes > 0 || removedEffects > 0)
            StatusChanged?.Invoke(this,
                AvaloniaLocalizationCatalog.Format("已擦除 Note {0} · Effect {1}（单次撤销）", removedNotes, removedEffects));
        InvalidateVisual();
    }

    private void CancelEraseStroke(string status)
    {
        if (!erasingStroke) return;
        erasingStroke = false;
        eraseStrokeChanged = false;
        eraseLockedStatusShown = false;
        eraseStartChangeSerial = 0;
        erasedNoteIds.Clear();
        eraseSegmentNoteIds.Clear();
        erasedTimingIds.Clear();
        eraseSegmentTimingIds.Clear();
        eraseTimingLabels.Clear();
        ReleaseInteractionCapture();
        StatusChanged?.Invoke(this, status);
        InvalidateVisual();
    }

    private void DeleteHitNote(ChartNote hit)
    {
        if (session == null || !CanBeginNoteEdit("删除")) return;
        var ids = session.Selection.Count > 1 && session.Selection.Contains(hit.Id)
            ? session.Selection.ToHashSet() : new HashSet<Guid> { hit.Id };
        try { ids = CenterSymmetryService.ExpandDeletion(session, ids, SymmetrySettings); }
        catch (InvalidOperationException exception) { StatusChanged?.Invoke(this, exception.Message); return; }
        if (!session.AllowOwnerProtectedEdits && session.Document.Notes.Any(n => ids.Contains(n.Id) && session.IsOwnerProtected(n)))
        {
            StatusChanged?.Invoke(this, "该 Note 位于房主锁定选区，成员不能删除");
            return;
        }
        var serial = session.ChangeSerial;
        try
        {
            session.Mutate(document => document.Notes.RemoveAll(note => ids.Contains(note.Id)));
            session.Selection.ExceptWith(ids);
            if (session.ChangeSerial != serial)
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                StatusChanged?.Invoke(this, $"已删除 {ids.Count} 个 Note");
            }
        }
        catch (Exception exception) when (IsExpectedEditException(exception))
        {
            StatusChanged?.Invoke(this, exception.Message);
        }
    }

    private void CaptureInteraction(
        IPointer pointer,
        MouseButton button,
        Point point,
        bool useEdgeScroll = true)
    {
        interactionPointer = pointer;
        interactionButton = button;
        edgeScrollPoint = point;
        lastEdgeScrollTick = Environment.TickCount64;
        pointer.Capture(this);
        if (useEdgeScroll) edgeScrollTimer.Start();
    }

    /// <summary>
    /// Takes over as soon as a second finger lands, whatever the first one was
    /// doing. A placement is only committed on release, so abandoning it here is
    /// a clean cancel rather than a half-written note, and it is what the user
    /// means: the second finger says "I am scrolling, not drawing".
    /// </summary>
    private bool TryBeginTouchScroll()
    {
        if (touchScrolling) return true;
        if (touchContacts.Count < TouchScrollContactCount) return false;

        if (erasingStroke) FinishEraseStroke();
        if (resizingHoldTail) FinishHoldTailResize(releaseCapture: false);
        // Releasing the first finger's capture raises CaptureLost synchronously,
        // which forgets that contact. The finger is still down and is half of
        // this very gesture, so the contact set is kept across the release.
        var contacts = touchContacts.ToArray();
        ResetPointerInteraction();
        ReleaseInteractionCapture();
        foreach (var contact in contacts) touchContacts[contact.Key] = contact.Value;
        touchScrolling = true;
        touchScrollStartY = AverageTouchContactY();
        touchScrollStartBeat = CurrentBeat;
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// Ends the scroll once fewer than two fingers remain. The gesture is not
    /// resumed by putting a finger back down: that would be indistinguishable
    /// from starting a fresh one, and the anchor beat would be stale.
    /// </summary>
    private bool EndTouchScrollIfLifted()
    {
        if (!touchScrolling || touchContacts.Count >= TouchScrollContactCount)
            return false;
        touchScrolling = false;
        InvalidateVisual();
        return true;
    }

    private double AverageTouchContactY()
    {
        if (touchContacts.Count == 0) return touchScrollStartY;
        var total = 0d;
        foreach (var contact in touchContacts.Values) total += contact.Y;
        return total / touchContacts.Count;
    }

    private void ReleaseInteractionCapture()
    {
        edgeScrollTimer.Stop();
        lastEdgeScrollTick = 0;
        var pointer = interactionPointer;
        interactionPointer = null;
        interactionButton = default;
        if (pointer?.Captured == this) pointer.Capture(null);
    }

    private bool IsInteractionButtonRelease(PointerReleasedEventArgs eventArgs)
    {
        var updateKind = eventArgs.GetCurrentPoint(this).Properties.PointerUpdateKind;
        return interactionButton switch
        {
            MouseButton.Left => updateKind == PointerUpdateKind.LeftButtonReleased,
            MouseButton.Right => updateKind == PointerUpdateKind.RightButtonReleased,
            MouseButton.Middle => updateKind == PointerUpdateKind.MiddleButtonReleased,
            _ => false
        };
    }

    private void EdgeScrollTimer_Tick(object? sender, EventArgs eventArgs)
    {
        if (session == null || interactionPointer?.Captured != this ||
            (!dragging && !movingTiming && !resizingHoldTail && !marqueeSelecting &&
             !placingNote && !erasingStroke))
        {
            edgeScrollTimer.Stop();
            lastEdgeScrollTick = 0;
            return;
        }

        var now = Environment.TickCount64;
        var elapsedSeconds = lastEdgeScrollTick == 0
            ? 0
            : (now - lastEdgeScrollTick) / 1000d;
        lastEdgeScrollTick = now;
        // Only deliberate dragging beyond the viewport scrolls time. Interior
        // bottom/top bands are valid placement and marquee positions.
        if (edgeScrollPoint.Y>=0 && edgeScrollPoint.Y<=Bounds.Height ||
            placingNote && placementGestureTool==TimelineTool.Tap) return;
        var step = TimelineEdgeScroll.Resolve(
            CurrentBeat,
            edgeScrollPoint.Y,
            Bounds.Height,
            elapsedSeconds);
        if (Math.Abs(step.Intensity) < .001 || !step.MovedFrom(CurrentBeat)) return;

        SetCurrentBeatFromManualNavigation(step.NextBeat);
        if (marqueeSelecting)
        {
            var geometry = GetGeometry(session.Document.KeyCount);
            marqueeStartPoint = new Point(
                marqueeStartPoint.X,
                YAtBeat(marqueeStartBeat, geometry));
            UpdateMarqueeSelection(edgeScrollPoint);
        }
        else if (dragging)
        {
            UpdateMovePreview(edgeScrollPoint);
        }
        else if (movingTiming)
        {
            UpdateTimingMovePreview(edgeScrollPoint);
        }
        else if (resizingHoldTail)
        {
            UpdateHoldTailResizePreview(edgeScrollPoint);
        }
        else if (placingNote)
        {
            placementCurrentBeat = PointerToDragPreviewBeat(edgeScrollPoint.Y);
        }
        else if (erasingStroke)
        {
            EraseAlongPath(edgeScrollPoint, edgeScrollPoint);
            eraseLastPoint = edgeScrollPoint;
        }
        InvalidateVisual();
    }

    private void BeginMarquee(Point point, bool additive)
    {
        if (session == null) return;
        dragging = false;
        previewLaneDelta = 0;
        previewBeatDelta = 0;
        marqueeSelecting = true;
        marqueeStartPoint = point; marqueePointerPoint = point;
        marqueeStartBeat = BeatAtY(point.Y, GetGeometry(session.Document.KeyCount));
        marqueeRect = TimelineMarqueeSelection.Normalize(point, point);
        marqueeBaseSelection.Clear();
        marqueeBaseTimingSelection.Clear();
        if (additive)
        {
            marqueeBaseSelection.UnionWith(session.Selection);
            marqueeBaseTimingSelection.UnionWith(selectedTimingIds);
        }
        else
        {
            session.Selection.Clear();
            selectedTimingIds.Clear();
            PrimarySelectedTimingId = null;
        }
        marqueeNextSelection.Clear();
        marqueeNextSelection.UnionWith(marqueeBaseSelection);
        marqueeNextTimingSelection.Clear();
        marqueeNextTimingSelection.UnionWith(marqueeBaseTimingSelection);
        marqueeCandidates.Clear();
        InvalidateVisual();
    }

    private void UpdateMarqueeSelection(Point point)
    {
        if (session == null || !marqueeSelecting) return;
        marqueePointerPoint = point;
        var geometry = GetGeometry(session.Document.KeyCount);
        marqueeRect = TimelineMarqueeSelection.Normalize(marqueeStartPoint, point);
        var padding = noteHeight / (2 * Math.Max(1, PixelsPerBeat));
        var minimumBeat = PointerToRawNoteBeat(marqueeRect.Bottom, geometry) -
                          GetMaximumStoredRenderBeatAdjustment() -
                          padding;
        var maximumBeat = PointerToRawNoteBeat(marqueeRect.Top, geometry) + padding;

        marqueeNextSelection.Clear();
        marqueeNextSelection.UnionWith(marqueeBaseSelection);
        _ = TimelineMarqueeSelection.AddIntersectingNotes(
            noteIndex,
            minimumBeat,
            maximumBeat,
            marqueeRect,
            note => GetNoteBounds(note, geometry),
            marqueeNextSelection,
            marqueeCandidates,
            hiddenGroups,
            includeDrafts: proEditorEnabled);
        _ = TimelineMarqueeSelection.ReplaceSelection(session.Selection, marqueeNextSelection);

        marqueeNextTimingSelection.Clear();
        marqueeNextTimingSelection.UnionWith(marqueeBaseTimingSelection);
        var timingPadding = TimelineTimingLayout.LabelHeight / (2 * Math.Max(1, PixelsPerBeat));
        BuildTimingLabelsForRange(
            geometry,
            BeatAtY(marqueeRect.Bottom, geometry) - timingPadding,
            BeatAtY(marqueeRect.Top, geometry) + timingPadding);
        _ = TimelineTimingLayout.AddMarqueeHits(
            visibleTimingLabels,
            marqueeRect,
            geometry.TrackLeft,
            marqueeNextTimingSelection);
        _ = TimelineMarqueeSelection.ReplaceSelection(selectedTimingIds, marqueeNextTimingSelection);
    }

    private ChartNote? HitTestHoldTail(Point point, TimelineGeometry geometry)
    {
        var pointerBeat = InteractionProfile == EditorInteractionProfile.OsuMania
            ? BeatAtY(point.Y, geometry) : PointerToRawNoteBeat(point.Y, geometry);
        var beatRadius = TimelineHoldTailResize.HitTolerance(noteHeight) /
            Math.Max(1, PixelsPerBeat);
        var bottomAnchor = InteractionProfile == EditorInteractionProfile.OsuMania;
        noteIndex.QueryHoldTails(
            bottomAnchor ? BeatAtY(point.Y + noteHeight, geometry) : pointerBeat - GetMaximumStoredRenderBeatAdjustment() - beatRadius,
            bottomAnchor ? BeatAtY(point.Y - TimelineHoldTailResize.HitTolerance(noteHeight), geometry) : pointerBeat + beatRadius,
            holdTailScratch,
            includeDrafts: proEditorEnabled);
        ChartNote? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var note in holdTailScratch)
        {
            if (stretchDragging && stretchIds.Contains(note.Id) && !drawingStretchPreview) continue;
            if (hiddenGroups.Contains(note.Group)) continue;
            var bounds = GetNoteBounds(note, geometry);
            var tailY = InteractionProfile == EditorInteractionProfile.OsuMania
                ? YAtBeat(note.EndBeat!.Value.Value, geometry) : NoteYAtBeat(note.EndBeat!.Value, geometry);
            if (!TimelineHoldTailResize.IsHit(
                    point,
                    bounds.Left,
                    bounds.Right,
                    InteractionProfile == EditorInteractionProfile.OsuMania
                        ? YAtBeat(note.Beat.Value, geometry) : NoteYAtBeat(note.Beat, geometry),
                    tailY,
                noteHeight, bottomAnchor)) continue;
            var distance = Math.Abs(point.Y - tailY);
            if (distance >= bestDistance) continue;
            best = note;
            bestDistance = distance;
        }
        return best;
    }

    private void BeginHoldTailResize(ChartNote note)
    {
        if (session == null || !note.EndBeat.HasValue) return;
        ClearHoldTailResizeState();
        var hadTimingSelection = selectedTimingIds.Count > 0;
        TimelineDirectObjectSelection.SelectNote(
            session.Selection,
            selectedTimingIds,
            note.Id);
        PrimarySelectedTimingId = null;
        resizingHoldTail = true;
        resizingHoldId = note.Id;
        resizingHoldLane = ProEditorWorkspaceGeometry.GetColumn(note);
        resizingHoldHead = note.Beat;
        resizingHoldOriginalEnd = note.EndBeat;
        resizingHoldCurrentEnd = note.EndBeat;
        resizingHoldStartChangeSerial = session.ChangeSerial;
        resizingHoldSymmetry = SymmetrySettings is { } settings
            ? settings with { Columns = settings.Columns.ToArray() } : null;
        if (!UpdateSymmetricHoldTailPlan(note.EndBeat))
        { ClearHoldTailResizeState(); return; }
        Cursor = HoldTailCursor;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        if (hadTimingSelection) TimingSelectionChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, AvaloniaLocalizationCatalog.Format("拖动 LN 面尾 · {0}", note.EndBeat.Value));
        InvalidateVisual();
    }

    private void UpdateHoldTailResizePreview(Point point)
    {
        if (session == null || !resizingHoldTail) return;
        if (TimelineHoldTailResize.IsInterrupted(
                resizingHoldStartChangeSerial,
                session.ChangeSerial))
        {
            CancelHoldTailResize("LN 面尾调整已按房间最新状态重新同步");
            return;
        }

        var candidate = TimelineHoldTailResize.ResolveTarget(
            resizingHoldHead,
            PointerToDragPreviewBeat(point.Y));
        if (!session.AllowOwnerProtectedEdits && session.OwnerProtectedRegions.Count > 0)
        {
            candidate = TimelineHoldTailResize.KeepLastLegal(
                resizingHoldHead,
                resizingHoldCurrentEnd,
                candidate,
                session.IntersectsOwnerProtectedRegion,
                out var accepted);
            if (!accepted)
            {
                StatusChanged?.Invoke(this, "LN 面尾目标范围进入了房主锁定区域，保留最后合法位置");
                return;
            }
        }
        if (Nullable.Equals(candidate, resizingHoldCurrentEnd)) return;
        if (!UpdateSymmetricHoldTailPlan(candidate)) return;
        resizingHoldCurrentEnd = candidate;
        StatusChanged?.Invoke(this, candidate.HasValue
            ? AvaloniaLocalizationCatalog.Format("LN 面尾预览 · {0}", candidate.Value)
            : "LN 面尾预览 · 松开后缩成 Tap");
    }

    private void FinishHoldTailResize(bool releaseCapture)
    {
        if (session == null || !resizingHoldTail) return;
        if (TimelineHoldTailResize.IsInterrupted(
                resizingHoldStartChangeSerial,
                session.ChangeSerial))
        {
            CancelHoldTailResize("LN 面尾调整已按房间最新状态重新同步");
            return;
        }

        var activeSession = session;
        var noteId = resizingHoldId;
        var headBeat = resizingHoldHead;
        var originalEnd = resizingHoldOriginalEnd;
        var targetEnd = resizingHoldCurrentEnd;
        var symmetry = resizingHoldSymmetry;
        var expectedSerial = resizingHoldStartChangeSerial;
        if (targetEnd.HasValue && InteractionProfile == EditorInteractionProfile.OsuMania)
            targetEnd = TimelineHoldTailResize.ResolveTarget(headBeat,
                TimelinePlacementGeometry.NearestGrid(targetEnd.Value.Value, activeSession.SnapDivision));
        var hasChange = TimelineHoldTailResize.HasChange(originalEnd, targetEnd);
        ClearHoldTailResizeState();
        if (releaseCapture) ReleaseInteractionCapture();
        Cursor = ArrowCursor;
        if (!hasChange)
        {
            StatusChanged?.Invoke(this, AvaloniaLocalizationCatalog.Format("已选择 LN 面尾 · {0}", originalEnd));
            return;
        }
        if (!CanBeginNoteEdit("调整 LN 面尾")) return;

        try
        {
            var changed = SymmetricHoldTailResizeCommand.Commit(
                activeSession,
                noteId,
                targetEnd ?? headBeat,
                symmetry,
                expectedSerial);
            if (changed)
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                StatusChanged?.Invoke(this, targetEnd.HasValue
                    ? AvaloniaLocalizationCatalog.Format("LN 面尾已调整到 {0}", targetEnd.Value)
                    : "LN 已缩成 Tap");
            }
        }
        catch (Exception exception) when (IsExpectedEditException(exception))
        {
            StatusChanged?.Invoke(this, exception.Message);
        }
    }

    private void CancelHoldTailResize(string status)
    {
        if (!resizingHoldTail) return;
        ClearHoldTailResizeState();
        ReleaseInteractionCapture();
        Cursor = ArrowCursor;
        StatusChanged?.Invoke(this, status);
        InvalidateVisual();
    }

    private void ClearHoldTailResizeState()
    {
        resizingHoldTail = false;
        resizingHoldId = Guid.Empty;
        resizingHoldLane = 0;
        resizingHoldHead = BeatPosition.Zero;
        resizingHoldOriginalEnd = null;
        resizingHoldCurrentEnd = null;
        resizingHoldStartChangeSerial = 0;
        resizingHoldSymmetry = null;
        resizingHoldPlan = null;
    }

    private void UpdateMovePreview(Point point)
    {
        if (session == null || !dragging) return;
        if (TimelineNoteMovePreview.IsInterrupted(noteMoveStartChangeSerial, session.ChangeSerial))
        {
            CancelNoteMove("Note 移动已按房间最新状态重新同步");
            return;
        }

        var geometry = GetGeometry(session.Document.KeyCount);
        var requestedLaneDelta = (int)Math.Round(
            (point.X - dragStartPoint.X) / geometry.LaneWidth,
            MidpointRounding.AwayFromZero);
        var requestedBeatDelta = PointerToRawNoteBeat(point.Y, geometry) -
                                 noteMovePointerStartBeat;
        TimelineNoteMovePreviewState candidate;
        try
        {
            candidate = TimelineNoteMovePreview.Resolve(
                noteMoveAnchorOrigin,
                noteMoveMinimumOriginBeat,
                noteMoveMinimumLane,
                noteMoveMaximumLane,
                GetWorkspaceColumnRange(),
                session.SnapDivision,
                requestedLaneDelta,
                requestedBeatDelta, SnapAllNotes && InteractionProfile == EditorInteractionProfile.OsuMania, AllowsNegativeBeats);
            if (!session.AllowOwnerProtectedEdits && session.OwnerProtectedRegions.Count > 0)
            {
                candidate = TimelineNoteMovePreview.KeepLastLegal(
                    new TimelineNoteMovePreviewState(previewLaneDelta, previewBeatMove),
                    candidate,
                    noteMoveOrigins,
                    session.IntersectsOwnerProtectedRegion,
                    out var accepted);
                if (!accepted)
                {
                    StatusChanged?.Invoke(this, "目标位置位于房主锁定选区，成员不能移动 Note");
                    return;
                }
            }
            // Probe the move against every origin before accepting it. Mixed
            // authored denominators can push the common denominator past the
            // preserved limit, and Render (which re-applies the move per Note)
            // must never be the first place that throws. A rejected candidate
            // keeps the last legal preview, which is also what release commits.
            else if (candidate.BeatMove != previewBeatMove)
            {
                foreach (var origin in noteMoveOrigins)
                    _ = TimelineNoteMovePreview.Apply(origin, candidate);
            }
        }
        catch (Exception exception) when (IsExpectedEditException(exception))
        {
            StatusChanged?.Invoke(this, exception.Message);
            return;
        }

        previewLaneDelta = candidate.LaneDelta;
        previewBeatMove = candidate.BeatMove;
        previewBeatDelta = candidate.BeatDelta;
    }

    private bool BeginNoteMove(BeatPosition anchor)
    {
        if (session == null) return false;
        ClearNoteMoveState();
        var targets = session.Document.Notes.Where(note =>
                session.Selection.Contains(note.Id) &&
                !hiddenGroups.Contains(note.Group) &&
                (proEditorEnabled || !note.IsDraft) &&
                (session.AllowOwnerProtectedEdits || !session.IsOwnerProtected(note)))
            .ToArray();
        session.Selection.IntersectWith(targets.Select(note => note.Id));
        foreach (var note in targets)
        {
            noteMoveAnchorOrigin = anchor;
            noteMoveOrigins.Add(new TimelineNoteMoveOrigin(
                ProEditorWorkspaceGeometry.GetColumn(note),
                note.Beat,
                note.EndBeat));
        }
        if (noteMoveOrigins.Count == 0) return false;

        noteMoveMinimumOriginBeat = noteMoveOrigins.Min(origin => origin.Beat.Value);
        noteMoveMinimumLane = noteMoveOrigins.Min(origin => origin.Lane);
        noteMoveMaximumLane = noteMoveOrigins.Max(origin => origin.Lane);
        noteMoveStartChangeSerial = session.ChangeSerial;
        noteMovePointerStartBeat = PointerToRawNoteBeat(
            dragStartPoint.Y,
            GetGeometry(session.Document.KeyCount));
        previewBeatMove = new RelativeBeatMove(0, Math.Clamp(session.SnapDivision, 1, 1920));
        return true;
    }

    private void CancelNoteMove(string status)
    {
        if (!dragging) return;
        ClearNoteMoveState();
        ReleaseInteractionCapture();
        StatusChanged?.Invoke(this, status);
        InvalidateVisual();
    }

    private void ClearNoteMoveState()
    {
        dragging = false;
        previewLaneDelta = 0;
        previewBeatDelta = 0;
        previewBeatMove = default;
        noteMoveAnchorOrigin = BeatPosition.Zero;
        noteMoveMinimumOriginBeat = 0;
        noteMoveMinimumLane = 0;
        noteMoveMaximumLane = 0;
        noteMoveStartChangeSerial = 0;
        noteMovePointerStartBeat = 0;
        noteMoveOrigins.Clear();
    }

    private void ResetPointerInteraction()
    {
        interactionButton = default;
        touchBlankGesturePending = false;
        touchBlankGestureLane = NoWorkspaceColumn;
        touchBlankGestureStartedAt = 0;
        workspacePanning = false;
        horizontalPanOnly = false;
        verticalPanOnly = false;
        ClearNoteMoveState();
        ClearHoldTailResizeState();
        edgeScrollTimer.Stop();
        lastEdgeScrollTick = 0;
        ClearTimingMoveState();
        marqueeSelecting = false;
        marqueeRect = default;
        marqueeStartBeat = 0;
        marqueeBaseSelection.Clear();
        marqueeNextSelection.Clear();
        marqueeBaseTimingSelection.Clear();
        marqueeNextTimingSelection.Clear();
        marqueeCandidates.Clear();
        placingNote = false;
        placementReplacedNoteId = null;
        placementReplacedCompanionId = null;
        placementLane = NoWorkspaceColumn;
        placementStartBeat = BeatPosition.Zero;
        placementCurrentBeat = BeatPosition.Zero;
        erasingStroke = false;
        eraseStrokeChanged = false;
        eraseLockedStatusShown = false;
        eraseStartChangeSerial = 0;
        erasedNoteIds.Clear();
        eraseSegmentNoteIds.Clear();
        erasedTimingIds.Clear();
        eraseSegmentTimingIds.Clear();
        eraseTimingLabels.Clear();
    }

    public void CancelInteraction()
    {
        EndStretch();
        IsDefiningSymmetry = false; symmetryFirstPoint = null;
        symmetryDragEdge = null; symmetryPointer?.Capture(null); symmetryPointer = null;
        ReleaseInteractionCapture();
        ResetPointerInteraction();
        InvalidateVisual();
    }

    private static bool IsExpectedEditException(Exception exception) =>
        exception is InvalidOperationException or ArgumentException or ArithmeticException;

    private static string ToolDisplayName(TimelineTool value) => value switch
    {
        TimelineTool.Select => "选择",
        TimelineTool.Tap => "Tap",
        TimelineTool.Hold => "Hold",
        TimelineTool.Erase => "擦除",
        _ => value.ToString()
    };

    private void DrawAudioBackground(DrawingContext context, TimelineGeometry geometry)
    {
        if ((!backgroundWaveformVisible && !backgroundSpectrogramVisible) ||
            audioVisualization == null || audioVisualizationTimingMap == null) return;
        EnsureAudioScene(geometry);
        var scene = audioScene;
        if (scene == null) return;

        var trackBounds = new Rect(
            geometry.TrackLeft,
            0,
            Math.Max(0, geometry.TrackRight - geometry.TrackLeft),
            Bounds.Height);
        if (trackBounds.Width <= 0 || trackBounds.Height <= 0) return;
        using (context.PushClip(trackBounds))
        {
            var (scale, offset) = TimelineRetainedAudioScenePolicy.SceneToView(
                scene,
                CurrentBeat,
                PixelsPerBeat,
                geometry.JudgementY,
                Bounds.Height);
            if (backgroundSpectrogramVisible && audioSpectrogramBitmap != null && scene.Spectrogram != null)
            {
                // With the spectrogram on, a colourful chart image still showed
                // through the track mask and the translucent spectrum blended
                // into it. A dark backing plate, deeper the stronger the
                // spectrogram is set, gives it a clean surface to read on.
                using (context.PushOpacity(BackgroundSpectrogramBackplateOpacity(spectrogramStrength)))
                    context.FillRectangle(Brushes.Black, trackBounds);
                using (context.PushOpacity(spectrogramStrength))
                {
                    context.DrawImage(
                        audioSpectrogramBitmap,
                        new Rect(
                            geometry.TrackLeft,
                            scene.TopY * scale + offset,
                            trackBounds.Width,
                            scene.Spectrogram.Height * scene.RowPixels * scale));
                }
            }

            if (backgroundWaveformVisible && scene.WaveformPeaks != null &&
                EnsureBackgroundWaveformGeometry(
                        scene,
                        (geometry.TrackLeft + geometry.TrackRight) / 2,
                        Math.Max(2, trackBounds.Width * .48)) is { } waveform)
            {
                // Only the vertical axis is transformed, so the contour keeps
                // its 1 px width on the near-vertical edges that carry onsets.
                using (context.PushTransform(new Matrix(1, 0, 0, scale, 0, offset)))
                    context.DrawGeometry(BackgroundWaveformFillBrush, BackgroundWaveformOutlinePen, waveform);
            }
        }
    }

    internal static double BackgroundSpectrogramBackplateOpacity(double strength) =>
        Math.Clamp(strength, 0, 1) * .6;

    /// <summary>
    /// Builds the background waveform once per scene and track width as a
    /// closed envelope in scene coordinates, instead of issuing one line per
    /// visible row on every frame.
    /// </summary>
    private StreamGeometry? EnsureBackgroundWaveformGeometry(
        TimelineAudioBackgroundScene scene,
        double center,
        double maximumHalfWidth)
    {
        if (ReferenceEquals(audioWaveformGeometryScene, scene) &&
            Math.Abs(audioWaveformGeometryCenter - center) < .25 &&
            Math.Abs(audioWaveformGeometryHalfWidth - maximumHalfWidth) < .25)
            return audioWaveformGeometry;
        audioWaveformGeometryScene = scene;
        audioWaveformGeometryCenter = center;
        audioWaveformGeometryHalfWidth = maximumHalfWidth;
        audioWaveformGeometry = null;
        if (scene.WaveformPeaks is not { Length: > 0 } peaks) return null;

        var result = new StreamGeometry();
        using (var envelope = result.Open())
        {
            double RowY(int row) => scene.TopY + (row + .5) * scene.RowPixels;
            double HalfWidth(int row) => peaks[row] <= .002f ? 0 : peaks[row] * maximumHalfWidth;
            envelope.BeginFigure(new Point(center - HalfWidth(0), RowY(0)), isFilled: true);
            for (var row = 1; row < peaks.Length; row++)
                envelope.LineTo(new Point(center - HalfWidth(row), RowY(row)));
            for (var row = peaks.Length - 1; row >= 0; row--)
                envelope.LineTo(new Point(center + HalfWidth(row), RowY(row)));
            envelope.EndFigure(isClosed: true);
        }
        audioWaveformGeometry = result;
        return result;
    }

    private void DrawChartBackground(DrawingContext context)
    {
        var image = chartBackgroundImage;
        if (image == null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var sourceSize = image.Size;
        var scale = Math.Max(
            Bounds.Width / Math.Max(1, sourceSize.Width),
            Bounds.Height / Math.Max(1, sourceSize.Height));
        var width = sourceSize.Width * scale;
        var height = sourceSize.Height * scale;
        using (context.PushOpacity(backgroundOpacity))
        {
            context.DrawImage(
                image,
                new Rect(
                    (Bounds.Width - width) / 2,
                    (Bounds.Height - height) / 2,
                    width,
                    height));
        }
    }

    private void EnsureAudioScene(TimelineGeometry geometry)
    {
        if (Bounds.Height <= 1 || audioVisualization == null || audioVisualizationTimingMap == null) return;
        var reusable = audioScene != null &&
                       ReferenceEquals(audioSceneSource, audioVisualization) &&
                       ReferenceEquals(audioSceneMap, audioVisualizationTimingMap) &&
                       Math.Abs(audioSceneScale - PixelsPerBeat) < .001 &&
                       Math.Abs(audioSceneHeight - Bounds.Height) < .5 &&
                       // Moving the judgement line in the view settings keeps the
                       // height, so the scene has to be rebuilt for the new line.
                       (!double.IsFinite(audioScene.JudgementY) ||
                        Math.Abs(audioScene.JudgementY - geometry.JudgementY) < .5) &&
                       (!backgroundSpectrogramVisible ||
                        Math.Abs(audioSceneDetail - spectrogramDetail) < 1e-6) &&
                       audioSceneWaveformVisible == backgroundWaveformVisible &&
                       audioSceneSpectrogramVisible == backgroundSpectrogramVisible &&
                       Math.Abs(CurrentBeat - audioScene.SceneBeat) * PixelsPerBeat <= Bounds.Height * .65;
        if (reusable) return;
        // Only keep waiting for a pending build that was started for the
        // current zoom and judgement line; one queued before a zoom step would
        // otherwise land already stale and cost a second full round.
        if (audioSceneBuildPending &&
            Math.Abs(audioSceneBuildScale - PixelsPerBeat) < .001 &&
            Math.Abs(audioSceneBuildJudgementY - geometry.JudgementY) < .5 &&
            Math.Abs(audioSceneBuildDetail - spectrogramDetail) < 1e-6 &&
            Math.Abs(CurrentBeat - audioSceneBuildBeat) * PixelsPerBeat <= Bounds.Height * .65) return;
        QueueAudioScene(geometry);
    }

    private void QueueAudioScene(TimelineGeometry geometry)
    {
        if (audioVisualization == null || audioVisualizationTimingMap == null) return;
        CancelAudioSceneBuild();
        var source = audioVisualization;
        var map = audioVisualizationTimingMap;
        var waveformVisible = backgroundWaveformVisible;
        var spectrogramVisible = backgroundSpectrogramVisible;
        var sceneBeat = CurrentBeat;
        var scale = PixelsPerBeat;
        var height = Bounds.Height;
        var judgementY = geometry.JudgementY;
        var version = ++audioSceneBuildVersion;
        var cancellation = audioSceneCancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        audioSceneBuildPending = true;
        audioSceneBuildBeat = sceneBeat;
        audioSceneBuildScale = scale;
        audioSceneBuildJudgementY = judgementY;
        var detail = spectrogramDetail;
        audioSceneBuildDetail = detail;
        _ = Task.Run(
                () => TimelineAudioBackgroundSceneBuilder.Build(
                    source,
                    map,
                    sceneBeat,
                    scale,
                    judgementY,
                    height,
                    waveformVisible,
                    spectrogramVisible,
                    token,
                    detail),
                token)
            .ContinueWith(task => Dispatcher.UIThread.Post(() =>
            {
                var completedScene = task.Status == TaskStatus.RanToCompletion
                    ? task.Result
                    : null;
                if (task.IsFaulted) _ = task.Exception;
                try
                {
                    if (version != audioSceneBuildVersion) return;
                    audioSceneBuildPending = false;
                    if (ReferenceEquals(audioSceneCancellation, cancellation))
                    {
                        audioSceneCancellation.Dispose();
                        audioSceneCancellation = null;
                    }
                    if (token.IsCancellationRequested || task.IsCanceled) return;
                    if (task.IsFaulted) return;
                    if (!ReferenceEquals(source, audioVisualization) ||
                        !ReferenceEquals(map, audioVisualizationTimingMap) ||
                        waveformVisible != backgroundWaveformVisible ||
                        spectrogramVisible != backgroundSpectrogramVisible) return;
                    WriteableBitmap? nextSpectrogramBitmap = null;
                    if (completedScene!.Spectrogram != null)
                    {
                        var reusableBackBitmap = audioSpectrogramBackBitmap;
                        audioSpectrogramBackBitmap = null;
                        nextSpectrogramBitmap = MaterializeSpectrogramBitmap(
                            completedScene.Spectrogram,
                            reusableBackBitmap);
                    }

                    var previousSpectrogramBitmap = audioSpectrogramBitmap;
                    audioScene = completedScene;
                    audioSceneSource = source;
                    audioSceneMap = map;
                    audioSceneScale = scale;
                    audioSceneHeight = height;
                    audioSceneWaveformVisible = waveformVisible;
                    audioSceneSpectrogramVisible = spectrogramVisible;
                    audioSceneDetail = detail;
                    audioSpectrogramBitmap = nextSpectrogramBitmap;
                    if (previousSpectrogramBitmap != null)
                    {
                        if (nextSpectrogramBitmap != null &&
                            previousSpectrogramBitmap.PixelSize == nextSpectrogramBitmap.PixelSize)
                            audioSpectrogramBackBitmap = previousSpectrogramBitmap;
                        else
                            previousSpectrogramBitmap.Dispose();
                    }
                    if (nextSpectrogramBitmap == null)
                    {
                        audioSpectrogramBackBitmap?.Dispose();
                        audioSpectrogramBackBitmap = null;
                    }
                    InvalidateVisual();
                }
                finally
                {
                    completedScene?.Spectrogram?.Dispose();
                }
            }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static WriteableBitmap MaterializeSpectrogramBitmap(
        TimelineSpectrogramPixels source,
        WriteableBitmap? reusable)
    {
        var expectedSize = new PixelSize(source.Width, source.Height);
        if (reusable != null && reusable.PixelSize != expectedSize)
        {
            reusable.Dispose();
            reusable = null;
        }
        var bitmap = reusable
            ?? new WriteableBitmap(
                expectedSize,
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);
        try
        {
            using var buffer = bitmap.Lock();
            var sourceStride = source.Width * 4;
            if (buffer.RowBytes == sourceStride)
                Marshal.Copy(source.BgraPixels, 0, buffer.Address, source.ByteLength);
            else
                for (var row = 0; row < source.Height; row++)
                    Marshal.Copy(
                        source.BgraPixels,
                        row * sourceStride,
                        IntPtr.Add(buffer.Address, row * buffer.RowBytes),
                        sourceStride);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        return bitmap;
    }

    private void CancelAudioSceneBuild()
    {
        audioSceneBuildVersion++;
        audioSceneCancellation?.Cancel();
        audioSceneCancellation?.Dispose();
        audioSceneCancellation = null;
        audioSceneBuildPending = false;
    }

    private void InvalidateAudioScene()
    {
        CancelAudioSceneBuild();
        audioScene = null;
        audioSceneSource = null;
        audioSceneMap = null;
        audioSpectrogramBitmap?.Dispose();
        audioSpectrogramBitmap = null;
        audioSpectrogramBackBitmap?.Dispose();
        audioSpectrogramBackBitmap = null;
    }

    private void DrawTrackMask(DrawingContext context, TimelineGeometry geometry)
    {
        using (context.PushOpacity(trackOpacity))
        {
            for (var column = geometry.MinimumColumn; column <= geometry.MaximumColumn; column++)
            {
                var left = geometry.TrackLeft + column * geometry.LaneWidth;
                context.FillRectangle(
                    TrackMaskBrush,
                    new Rect(left, 0, geometry.LaneWidth, Bounds.Height));
            }
        }
    }

    private void DrawLaneLines(DrawingContext context, TimelineGeometry geometry)
    {
        for (var column = geometry.MinimumColumn; column <= geometry.MaximumColumn; column++)
        {
            var left = geometry.TrackLeft + column * geometry.LaneWidth;
            context.DrawLine(
                LaneGridPen,
                new Point(left, 0),
                new Point(left, Bounds.Height));
        }
        context.DrawLine(
            LaneGridPen,
            new Point(geometry.GridRight, 0),
            new Point(geometry.GridRight, Bounds.Height));
        if (!proEditorEnabled) return;
        context.DrawLine(
            ActiveBoundaryPen,
            new Point(geometry.TrackLeft, 0),
            new Point(geometry.TrackLeft, Bounds.Height));
        context.DrawLine(
            ActiveBoundaryPen,
            new Point(geometry.TrackRight, 0),
            new Point(geometry.TrackRight, Bounds.Height));
    }

    private void DrawGrid(DrawingContext context, TimelineGeometry geometry)
    {
        if (session == null) return;
        var minimumBeat = Math.Max(MinimumEditableBeat, BeatAtY(Bounds.Height, geometry));
        var maximumBeat = Math.Max(minimumBeat, BeatAtY(0, geometry));
        if (!double.IsFinite(minimumBeat) || !double.IsFinite(maximumBeat)) return;
        var division = Math.Clamp(session.SnapDivision, 1, 1920);
        // Step numbers are absolute grid indices. Computing them in long and
        // capping below int.MaxValue keeps a far-away viewport from overflowing
        // the conversion (or wrapping the loop counter), and every drawn step
        // still fits the int-based grid policy, including its checked measure+1.
        const double maximumGridStep = int.MaxValue - 1;
        var firstStep = (long)Math.Clamp(Math.Floor(minimumBeat * division), -maximumGridStep, maximumGridStep);
        var lastStep = (long)Math.Clamp(Math.Ceiling(maximumBeat * division), -maximumGridStep, maximumGridStep);
        var minimumPixelSpacing = PixelsPerBeat / division;
        var stride = Math.Max(1, (int)Math.Ceiling(5 / Math.Max(.001, minimumPixelSpacing)));
        DrawMeasureNumberRail(context, geometry);
        for (var absoluteStep = firstStep; absoluteStep <= lastStep; absoluteStep++)
        {
            var step = (int)absoluteStep;
            var kind =
                WindowsTimelineVisualParityPolicy.ResolveGridLineKind(division, step);
            // Thin only irregular subdivisions, keyed on the absolute step so the
            // surviving lines stay attached to the chart while scrolling. Keying on
            // the viewport's first row made the kept set shift (flicker) every time
            // that row changed, and could drop half/quarter lines of the hierarchy.
            if (kind == WindowsTimelineGridLineKind.Irregular && step % stride != 0) continue;
            var beat = (double)step / division;
            var y = YAtBeat(beat, geometry);
            context.DrawLine(
                kind == WindowsTimelineGridLineKind.Bar
                    ? barGridPen
                    : subdivisionGridPens[kind],
                new Point(geometry.GridLeft, y),
                new Point(geometry.GridRight, y));
            var measureNumber =
                WindowsTimelineVisualParityPolicy.ResolveMeasureNumber(division, step);
            if (measureNumber.HasValue)
                DrawMeasureNumber(context, geometry, y, measureNumber.Value);
        }
    }

    private void DrawMeasureNumberRail(
        DrawingContext context,
        TimelineGeometry geometry)
    {
        var right = Math.Clamp(geometry.TrackLeft, 0, Bounds.Width);
        var left = Math.Max(0, right - MeasureNumberRailWidth);
        if (right - left < 8) return;
        context.FillRectangle(
            MeasureNumberRailBrush,
            new Rect(left, 0, right - left, Bounds.Height));
        context.DrawLine(
            MeasureNumberRailPen,
            new Point(right - .5, 0),
            new Point(right - .5, Bounds.Height));
    }

    private void DrawMeasureNumber(
        DrawingContext context,
        TimelineGeometry geometry,
        double lineY,
        int measureNumber)
    {
        var text = GetMeasureNumberText(measureNumber);
        if (lineY < 0 || lineY - text.WidthIncludingTrailingWhitespace > Bounds.Height)
            return;
        var railRight = Math.Clamp(geometry.TrackLeft, 0, Bounds.Width);
        var textX = Math.Max(
            1,
            railRight - text.Height - MeasureNumberRailPadding);
        var transform =
            Matrix.CreateRotation(-Math.PI / 2) *
            Matrix.CreateTranslation(
                textX,
                lineY - MeasureNumberRailPadding);
        using (context.PushTransform(transform))
            context.DrawText(text, default);
    }

    private FormattedText GetMeasureNumberText(int measureNumber)
    {
        if (measureNumberTextCache.TryGetValue(measureNumber, out var formatted))
            return formatted;
        if (measureNumberTextCache.Count >= 4096) measureNumberTextCache.Clear();
        formatted = new FormattedText(
            measureNumber.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold),
            11,
            MeasureNumberTextBrush);
        measureNumberTextCache[measureNumber] = formatted;
        return formatted;
    }

    private void DrawNotes(
        DrawingContext context,
        TimelineGeometry geometry,
        IEnumerable<ChartNote> notes,
        bool draggingSelectionPass)
    {
        if (session == null) return;
        using var opacity = context.PushOpacity(noteOpacity);
        var selected = session.Selection;
        var previewSteps = dragging
            ? previewBeatMove
            : new RelativeBeatMove(0, session.SnapDivision);
        foreach (var note in notes)
        {
            if (stretchDragging && stretchIds.Contains(note.Id) && !drawingStretchPreview) continue;
            if (hiddenGroups.Contains(note.Group)) continue;
            if (erasingStroke && erasedNoteIds.Contains(note.Id)) continue;
            if (placingNote && (note.Id == placementReplacedNoteId || note.Id == placementReplacedCompanionId)) continue;
            // The resizing LN is drawn once from the pure preview state below.
            // Keeping the document copy here would leave a misleading old tail
            // visible while shortening or converting the LN to a Tap.
            if (resizingHoldTail && (note.Id == resizingHoldId || resizingHoldPlan?.ReplacedIds.Contains(note.Id) == true)) continue;
            var isSelected = selected.Contains(note.Id);
            var isValidationIssue = validationIssueNoteIds.Contains(note.Id);
            if (dragging && isSelected != draggingSelectionPass) continue;
            if (!dragging && draggingSelectionPass) continue;
            var lane = ProEditorWorkspaceGeometry.GetColumn(note);
            var beat = note.Beat;
            var endBeat = note.EndBeat;
            if (dragging && isSelected)
            {
                lane += previewLaneDelta;
                if (lane < geometry.MinimumColumn || lane > geometry.MaximumColumn) continue;
                // UpdateMovePreview only accepts moves every origin can take, so
                // this should not fail. Still, an exception escaping Render closes
                // the application, so a Note that cannot be previewed is skipped.
                try
                {
                    beat = BasicChartEditingService.ApplyRelativeMove(beat, previewSteps);
                    if (endBeat.HasValue)
                        endBeat = BasicChartEditingService.ApplyRelativeMove(endBeat.Value, previewSteps);
                }
                catch (Exception exception) when (IsExpectedEditException(exception))
                {
                    continue;
                }
            }

            var width = GetRenderedNoteWidth(geometry.LaneWidth);
            var left = geometry.TrackLeft + lane * geometry.LaneWidth +
                       (geometry.LaneWidth - width) / 2;
            var headY = NoteYAtBeat(beat, geometry);
            var headSubdivisionPen = subdivisionCheckEnabled
                ? ResolveSubdivisionCheckPen(beat.Denominator)
                : null;
            if (endBeat.HasValue)
            {
                var tailY = NoteYAtBeat(endBeat.Value, geometry);
                if (hideLongNoteBodies)
                {
                    var headRect = new Rect(
                        left,
                        headY - noteHeight / 2,
                        width,
                        noteHeight);
                    DrawTapNote(context, headRect, headSubdivisionPen ?? HiddenHoldHeadPen);
                    if (isSelected || isValidationIssue)
                        DrawNoteStateOutline(context, headRect, isValidationIssue);
                }
                else
                {
                    var tailSubdivisionPen = subdivisionCheckEnabled
                        ? ResolveSubdivisionCheckPen(endBeat.Value.Denominator)
                        : null;
                    DrawLongNote(
                        context,
                        left,
                        headY,
                        tailY,
                        width,
                        noteHeight,
                        headSubdivisionPen);
                    if (tailSubdivisionPen != null)
                        context.DrawLine(
                            tailSubdivisionPen,
                            new Point(left + 2, tailY),
                            new Point(left + width - 2, tailY));
                    if (isSelected || isValidationIssue)
                    {
                        var outlineTop = Math.Min(headY, tailY) - noteHeight / 2;
                        var outlineHeight = Math.Max(
                            noteHeight,
                            Math.Abs(tailY - headY) + noteHeight);
                        DrawNoteStateOutline(context,
                            new Rect(left, outlineTop, width, outlineHeight), isValidationIssue);
                        if (isSelected)
                            context.DrawLine(
                                SelectedNotePen,
                                new Point(left + 7, tailY),
                                new Point(left + width - 7, tailY));
                    }
                }
            }
            else
            {
                var noteRect = new Rect(left, headY - noteHeight / 2, width, noteHeight);
                DrawTapNote(context, noteRect, headSubdivisionPen);
                if (isSelected || isValidationIssue)
                {
                    DrawNoteStateOutline(context, noteRect, isValidationIssue);
                }
            }
            if (WindowsOnlineCreatorMarkerPolicy.ShouldDraw(
                    isPlaybackActive,
                    note.OnlineCreatorUserId))
                DrawCreatorMarker(
                    context,
                    note.OnlineCreatorUserId,
                    left - 6,
                    headY,
                    WindowsOnlineCreatorMarkerPolicy.NoteMarkerSize(noteHeight));
        }
    }

    private static void DrawTapNote(DrawingContext context, Rect rect, IPen? contourPen = null)
    {
        var radius = Math.Clamp(rect.Height * .1, 2, 4);
        context.DrawRectangle(NotePlateBrush, null, rect, radius, radius);
        var railInset = Math.Clamp(rect.Height * .14, 3.5, 5.5);
        var railWidth = Math.Max(2, rect.Width - railInset * 2);
        var railHeight = Math.Max(2, rect.Height - railInset * 2);
        if (railWidth > 0 && railHeight > 0)
        {
            var railRadius = Math.Clamp(radius * .72, 1.5, 3);
            context.DrawRectangle(
                NoteRailBrush,
                null,
                new Rect(rect.X + railInset, rect.Y + railInset, railWidth, railHeight),
                railRadius,
                railRadius);
        }
        context.DrawRectangle(null, contourPen ?? NoteContourPen, rect, radius, radius);
    }

    private static void DrawNoteStateOutline(DrawingContext context, Rect noteRect, bool validationIssue)
    {
        var pen = validationIssue ? ValidationIssuePen : SelectedNotePen;
        var inset = pen.Thickness / 2;
        using var clip = context.PushClip(noteRect);
        context.DrawRectangle(validationIssue ? ValidationIssueOverlayBrush : SelectedNoteOverlayBrush, pen,
            noteRect.Deflate(inset), 3, 3);
    }

    private void DrawLongNote(
        DrawingContext context,
        double left,
        double startY,
        double endY,
        double width,
        double renderedNoteHeight,
        IPen? contourPen = null)
    {
        if (NoteStyle == EditorNoteStyle.Osu)
        {
            var inset = Math.Max(2, width * .12);
            context.FillRectangle(HoldFaceBrush, new Rect(left + inset, Math.Min(startY, endY),
                Math.Max(1, width - 2 * inset), Math.Abs(endY - startY)));
            DrawTapNote(context, new Rect(left, startY - renderedNoteHeight / 2, width, renderedNoteHeight), contourPen);
            DrawTapNote(context, new Rect(left, endY - renderedNoteHeight / 2, width, renderedNoteHeight), contourPen);
            return;
        }
        var top = Math.Min(startY, endY) - renderedNoteHeight / 2;
        var totalHeight = Math.Max(renderedNoteHeight, Math.Abs(endY - startY) + renderedNoteHeight);
        var rect = new Rect(left, top, width, totalHeight);
        var radius = Math.Clamp(renderedNoteHeight * .06, 1, 2.5);
        context.DrawRectangle(HoldOuterBrush, null, rect, radius, radius);
        var rim = Math.Clamp(width * .035, 1.5, 2.5);
        var shell = Deflate(rect, rim);
        context.DrawRectangle(
            NoteShellBrush,
            null,
            shell,
            Math.Max(.5, radius - .5),
            Math.Max(.5, radius - .5));
        var face = Deflate(shell, Math.Clamp(width * .04, 2, 3));
        context.FillRectangle(HoldFaceBrush, face);
        if (contourPen != null)
            context.DrawRectangle(null, contourPen, rect, radius, radius);

        var stripWidth = Math.Max(12, face.Width * .68);
        var stripHeight = Math.Clamp(renderedNoteHeight * .1, 2, 4);
        if (face.Height < stripHeight + 6) return;
        var stripY = Math.Clamp(
            startY - stripHeight / 2,
            face.Top + 3,
            face.Bottom - stripHeight - 3);
        context.DrawRectangle(
            HoldStartBrush,
            null,
            new Rect(face.X + (face.Width - stripWidth) / 2, stripY, stripWidth, stripHeight),
            stripHeight / 2,
            stripHeight / 2);
        var tailStripY = Math.Clamp(
            endY - stripHeight / 2,
            face.Top + 3,
            face.Bottom - stripHeight - 3);
        context.DrawRectangle(
            HoldEndBrush,
            null,
            new Rect(
                face.X + (face.Width - stripWidth) / 2,
                tailStripY,
                stripWidth,
                stripHeight),
            stripHeight / 2,
            stripHeight / 2);
    }

    private static IPen ResolveSubdivisionCheckPen(int denominator)
    {
        denominator = TimelineSubdivisionCheckPalette.NormalizeDenominator(denominator);
        return SubdivisionCheckPens.TryGetValue(denominator, out var pen)
            ? pen
            : SubdivisionWarningPen;
    }

    private static Rect Deflate(Rect rect, double amount) => new(
        rect.X + amount,
        rect.Y + amount,
        Math.Max(1, rect.Width - amount * 2),
        Math.Max(1, rect.Height - amount * 2));

    private void DrawGroupBadge(
        DrawingContext context,
        int group,
        double centerX,
        double centerY)
    {
        var badgeHeight = Math.Clamp(noteHeight * .72, 12, 18);
        if (!groupTextCache.TryGetValue(group, out var text))
        {
            if (groupTextCache.Count >= 2048) groupTextCache.Clear();
            text = new FormattedText(
                group.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                Math.Clamp(badgeHeight * .62, 8, 11),
                GroupTextBrush);
            groupTextCache[group] = text;
        }
        var badgeWidth = Math.Max(badgeHeight, text.Width + 8);
        var rect = new Rect(
            centerX - badgeWidth / 2,
            centerY - badgeHeight / 2,
            badgeWidth,
            badgeHeight);
        context.DrawRectangle(
            GetGroupBrush(group),
            GroupBadgePen,
            rect,
            badgeHeight / 2,
            badgeHeight / 2);
        context.DrawText(
            text,
            new Point(centerX - text.Width / 2, centerY - text.Height / 2));
    }

    private void DrawCreatorMarker(
        DrawingContext context,
        long userId,
        double centerX,
        double centerY,
        double size)
    {
        var fill = GroupBrushes[
            WindowsOnlineCreatorMarkerPolicy.ColorIndex(userId, GroupBrushes.Length)];
        var center = new Point(centerX, centerY);
        context.DrawEllipse(fill, CreatorMarkerPen, center, size / 2, size / 2);
        if (!onlineCreators.TryGetValue(userId, out var creator) ||
            string.IsNullOrWhiteSpace(creator.Name))
            return;
        if (creator.Avatar != null)
        {
            var bounds = new Rect(
                centerX - size / 2 + 1,
                centerY - size / 2 + 1,
                size - 2,
                size - 2);
            using (context.PushClip(new RoundedRect(bounds, size / 2)))
                context.DrawImage(creator.Avatar, bounds);
            context.DrawEllipse(null, CreatorMarkerPen, center, size / 2, size / 2);
            return;
        }

        var initial = WindowsOnlineCreatorMarkerPolicy.FallbackInitial(creator.Name);
        if (initial.Length == 0) return;
        var fontSize = Math.Clamp(size * .53, 6.5, 8.5);
        var cacheKey = string.Concat(
            initial,
            "\0",
            fontSize.ToString("R", CultureInfo.InvariantCulture));
        if (!creatorTextCache.TryGetValue(cacheKey, out var text))
        {
            if (creatorTextCache.Count >= 256) creatorTextCache.Clear();
            text = new FormattedText(
                initial,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                fontSize,
                CreatorTextBrush);
            creatorTextCache[cacheKey] = text;
        }
        context.DrawText(
            text,
            new Point(centerX - text.Width / 2, centerY - text.Height / 2));
    }

    private void DrawHoldTailResizePreview(
        DrawingContext context,
        TimelineGeometry geometry)
    {
        if (resizingHoldTail && resizingHoldPlan != null)
        { DrawSymmetricHoldTailPlan(context, geometry); return; }
        if (!resizingHoldTail || resizingHoldLane < geometry.MinimumColumn ||
            resizingHoldLane > geometry.MaximumColumn) return;
        var width = GetRenderedNoteWidth(geometry.LaneWidth);
        var left = geometry.TrackLeft + resizingHoldLane * geometry.LaneWidth +
                   (geometry.LaneWidth - width) / 2;
        var headY = NoteYAtBeat(resizingHoldHead, geometry);
        if (resizingHoldCurrentEnd.HasValue)
        {
            var tailY = NoteYAtBeat(resizingHoldCurrentEnd.Value, geometry);
            DrawHoldInteractionPreview(
                context,
                left,
                headY,
                tailY,
                width,
                noteHeight);
        }
        else
        {
            var tapRect = new Rect(
                left,
                headY - noteHeight / 2,
                width,
                noteHeight);
            using (context.PushOpacity(
                       WindowsTimelineVisualParityPolicy.HoldInteractionStyle.Opacity))
                DrawTapNote(context, tapRect);
            DrawNoteStateOutline(context, tapRect, false);
        }
    }

    private void DrawPlacementHover(DrawingContext context, TimelineGeometry geometry)
    {
        if (pendingPasteNotes != null) return;
        if (InteractionProfile != EditorInteractionProfile.OsuMania || session == null || session.IsReadOnly || !session.CanEditNotes ||
            Tool is not (TimelineTool.Tap or TimelineTool.Hold) || placementHoverPoint is not { } point ||
            movingTiming || marqueeSelecting) return;
        if (WorkspaceColumnAtX(point.X, geometry) == NoWorkspaceColumn || !new Rect(Bounds.Size).Contains(point)) return;
        var width = GetRenderedNoteWidth(geometry.LaneWidth);
        using (context.PushOpacity(.65))
            DrawTapNote(context, new Rect(point.X - width / 2, point.Y - noteHeight, width, noteHeight));
        if (!placingNote)
            DrawSymmetryCompanionPreview(context, geometry, WorkspaceColumnAtX(point.X, geometry), PointerToNoteBeat(point.Y), null);
    }

    private void DrawPlacementPreview(DrawingContext context, TimelineGeometry geometry)
    {
        if (session == null || !placingNote || placementLane == NoWorkspaceColumn) return;
        TimelinePlacement placement;
        try
        {
            placement = TimelinePlacementGeometry.Resolve(
                placementGestureTool,
                placementStartBeat,
                placementCurrentBeat,
                session.SnapDivision);
        }
        catch (Exception exception) when (IsExpectedEditException(exception))
        {
            return;
        }

        var width = GetRenderedNoteWidth(geometry.LaneWidth);
        var left = geometry.TrackLeft + placementLane * geometry.LaneWidth +
                   (geometry.LaneWidth - width) / 2;
        var startY = NoteYAtBeat(placement.StartBeat, geometry);
        DrawSymmetryCompanionPreview(context, geometry, placementLane, placement.StartBeat, placement.EndBeat);
        if (placement.Kind == TimelinePlacementKind.Hold && placement.EndBeat.HasValue)
        {
            var endY = NoteYAtBeat(placement.EndBeat.Value, geometry);
            DrawHoldInteractionPreview(
                context,
                left,
                startY,
                endY,
                width,
                noteHeight);
            return;
        }
        var tapRect = new Rect(
            left,
            startY - noteHeight / 2,
            width,
            noteHeight);
        using (context.PushOpacity(
                   WindowsTimelineVisualParityPolicy.HoldInteractionStyle.Opacity))
            DrawTapNote(context, tapRect);
    }

    private void DrawHoldInteractionPreview(
        DrawingContext context,
        double left,
        double headY,
        double tailY,
        double width,
        double renderedNoteHeight)
    {
        var style = WindowsTimelineVisualParityPolicy.HoldInteractionStyle;
        using (context.PushOpacity(style.Opacity))
            DrawLongNote(
                context,
                left,
                headY,
                tailY,
                width,
                renderedNoteHeight);
        context.DrawLine(
            HoldTailPen,
            new Point(left + style.TailLineInset, tailY),
            new Point(left + width - style.TailLineInset, tailY));
    }

    private Rect GetNoteBounds(
        ChartNote note,
        TimelineGeometry geometry,
        double pixelsPerBeat,
        double currentBeat)
    {
        var column = ProEditorWorkspaceGeometry.GetColumn(note);
        var width = GetRenderedNoteWidth(geometry.LaneWidth);
        var left = geometry.TrackLeft + column * geometry.LaneWidth +
                   (geometry.LaneWidth - width) / 2;
        var headY = NoteYAtBeat(note.Beat, geometry, pixelsPerBeat, currentBeat);
        if (!note.EndBeat.HasValue)
            return new Rect(left, headY - noteHeight / 2, width, noteHeight);

        var tailY = NoteYAtBeat(note.EndBeat.Value, geometry, pixelsPerBeat, currentBeat);
        return new Rect(
            left,
            Math.Min(headY, tailY) - noteHeight / 2,
            width,
            Math.Abs(tailY - headY) + noteHeight);
    }

    private Rect GetNoteBounds(ChartNote note, TimelineGeometry geometry) =>
        GetNoteBounds(note, geometry, PixelsPerBeat, CurrentBeat);

    private double GetRenderedNoteWidth(double laneWidth) =>
        Math.Min(noteWidth, Math.Max(12, laneWidth - 6));

    private void DrawTiming(
        DrawingContext context,
        TimelineGeometry geometry,
        double minimumBeat,
        double maximumBeat)
    {
        if (session == null) return;
        if (movingTiming)
        {
            _ = timingIndex.Query(minimumBeat, maximumBeat, visibleTimings);
            visibleTimings.RemoveAll(timing => timingMoveOrigins.ContainsKey(timing.Id));
            _ = timingMovePreviewIndex.Query(minimumBeat, maximumBeat, visibleTimingMovePreviews);
            timingDrawEvents.Clear();
            timingDrawEvents.AddRange(visibleTimings);
            timingDrawEvents.AddRange(visibleTimingMovePreviews);
            timingDrawEvents.Sort(CompareTimingDrawOrder);
            if (HideSvEditing) timingDrawEvents.RemoveAll(t => !t.Bpm.HasValue);
            TimelineTimingLayout.BuildLabels(
                timingDrawEvents,
                geometry.TrackLeft - MeasureNumberRailWidth,
                beat => YAtBeat(beat, geometry),
                text => GetTimingText(text).WidthIncludingTrailingWhitespace,
                visibleTimingLabels);
        }
        else
        {
            BuildTimingLabelsForRange(geometry, minimumBeat, maximumBeat);
        }
        foreach (var label in visibleTimingLabels)
        {
            if (erasingStroke && erasedTimingIds.Contains(label.Timing.Id)) continue;
            var bounds = label.Bounds;
            var anchorY = label.AnchorY;

            var color = TimingCategoryBrush(label.Category);
            context.DrawLine(
                TimingConnectorPen,
                new Point(bounds.Right, bounds.Top + bounds.Height / 2),
                new Point(geometry.TrackLeft, anchorY));
            context.DrawRectangle(
                TimingLabelBackgroundBrush,
                selectedTimingIds.Contains(label.Timing.Id) ? SelectedTimingPen : TimingCategoryPen(label.Category),
                bounds,
                3,
                3);
            context.FillRectangle(
                color,
                new Rect(bounds.X + 4, bounds.Y + 4, 3, bounds.Height - 8));
            var text = GetTimingText(label.Text);
            context.DrawText(
                text,
                new Point(
                    bounds.X + 12,
                    bounds.Y + (bounds.Height - text.Height) / 2));
            if (WindowsOnlineCreatorMarkerPolicy.ShouldDraw(
                    isPlaybackActive,
                    label.Timing.OnlineCreatorUserId))
                DrawCreatorMarker(
                    context,
                    label.Timing.OnlineCreatorUserId,
                    bounds.Left - 7,
                    bounds.Top + bounds.Height / 2,
                    WindowsOnlineCreatorMarkerPolicy.TimingMarkerSize);
            context.FillRectangle(color, new Rect(geometry.TrackLeft, anchorY - 1, 12, 2));
        }
    }

    private void DrawOwnerProtectedRegions(
        DrawingContext context,
        TimelineGeometry geometry,
        double minimumBeat,
        double maximumBeat)
    {
        if (session == null || session.OwnerProtectedRegions.Count == 0) return;
        TimelineOwnerRegionLayout.BuildVisible(
            session.OwnerProtectedRegions,
            minimumBeat,
            maximumBeat,
            geometry.GridLeft,
            geometry.GridRight,
            beat => YAtBeat(beat, geometry),
            visibleOwnerRegions);
        foreach (var visual in visibleOwnerRegions)
            context.DrawRectangle(OwnerRegionFillBrush, OwnerRegionPen, visual.Bounds, 3, 3);
    }

    private void BuildTimingLabelsForRange(
        TimelineGeometry geometry,
        double minimumBeat,
        double maximumBeat)
    {
        EnsureIndex();
        _ = timingIndex.Query(minimumBeat, maximumBeat, visibleTimings);
        if (HideSvEditing) visibleTimings.RemoveAll(t => !t.Bpm.HasValue);
        TimelineTimingLayout.BuildLabels(
            visibleTimings,
            geometry.TrackLeft - MeasureNumberRailWidth,
            beat => YAtBeat(beat, geometry),
            text => GetTimingText(text).WidthIncludingTrailingWhitespace,
            visibleTimingLabels);
    }

    private int CompareTimingDrawOrder(TimingEvent left, TimingEvent right)
    {
        var beatOrder = left.Beat.CompareTo(right.Beat);
        if (beatOrder != 0) return beatOrder;
        var leftOrder = timingSourceOrders.GetValueOrDefault(left.Id, int.MaxValue);
        var rightOrder = timingSourceOrders.GetValueOrDefault(right.Id, int.MaxValue);
        return leftOrder.CompareTo(rightOrder);
    }

    private TimelineTimingLabel? HitTestTimingLabel(Point point, TimelineGeometry geometry)
    {
        var padding = TimelineTimingLayout.LabelHeight / (2 * Math.Max(1, PixelsPerBeat));
        var minimumBeat = BeatAtY(Bounds.Height, geometry) - padding;
        var maximumBeat = BeatAtY(0, geometry) + padding;
        BuildTimingLabelsForRange(geometry, minimumBeat, maximumBeat);
        return TimelineTimingLayout.HitTestTopmost(visibleTimingLabels, point);
    }

    private FormattedText GetTimingText(string text)
    {
        if (timingTextCache.TryGetValue(text, out var formatted)) return formatted;
        if (timingTextCache.Count >= 2048) timingTextCache.Clear();
        formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            10.5,
            TimingTextBrush);
        timingTextCache[text] = formatted;
        return formatted;
    }

    private static IBrush TimingCategoryBrush(TimelineTimingCategory category) => category switch
    {
        TimelineTimingCategory.Scroll => TimingScrollBrush,
        TimelineTimingCategory.Jump => TimingJumpBrush,
        TimelineTimingCategory.HS => TimingHsBrush,
        TimelineTimingCategory.Sign => TimingSignBrush,
        TimelineTimingCategory.Custom => TimingCustomBrush,
        _ => TimingBpmBrush
    };

    private static IPen TimingCategoryPen(TimelineTimingCategory category) => category switch
    {
        TimelineTimingCategory.Scroll => TimingScrollPen,
        TimelineTimingCategory.Jump => TimingJumpPen,
        TimelineTimingCategory.HS => TimingHsPen,
        TimelineTimingCategory.Sign => TimingSignPen,
        TimelineTimingCategory.Custom => TimingCustomPen,
        _ => TimingBpmPen
    };

    public static int GetGroupColorIndex(int group)
    {
        if (group is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(group));
        return (group - 1) % GroupBrushes.Length;
    }

    public static IBrush GetGroupBrush(int group) => GroupBrushes[GetGroupColorIndex(group)];

    private TimelineGeometry GetGeometry(int keyCount)
    {
        keyCount = Math.Clamp(keyCount, 1, 18);
        var available = Math.Max(120, Bounds.Width - TimingRailWidth - 28);
        var laneWidth = Math.Min(gridLaneWidth, available / keyCount);
        var trackWidth = laneWidth * keyCount;
        var centeredTrackLeft = Math.Max(TimingRailWidth, (Bounds.Width - trackWidth) / 2);
        if (centeredTrackLeft + trackWidth > Bounds.Width)
            centeredTrackLeft = Math.Max(TimingRailWidth, Bounds.Width - trackWidth);
        var minimumMainOffset = TimingRailWidth - trackWidth + laneWidth - centeredTrackLeft;
        var maximumMainOffset = Bounds.Width - laneWidth - centeredTrackLeft;
        var baseTrackLeft = centeredTrackLeft + Math.Clamp(
            mainTrackOffset,
            minimumMainOffset,
            maximumMainOffset);
        var pan = ProEditorWorkspaceGeometry.ResolveTrackPan(
            Bounds.Width,
            baseTrackLeft,
            trackWidth,
            laneWidth,
            proTrackOffset,
            proEditorEnabled);
        var effectiveTrackOffset = proEditorEnabled ? pan.Value : 0;
        if (proEditorEnabled) proTrackOffset = effectiveTrackOffset;
        var trackLeft = baseTrackLeft + effectiveTrackOffset;
        var range = GetWorkspaceColumnRange();
        return new TimelineGeometry(
            keyCount,
            laneWidth,
            baseTrackLeft,
            trackLeft,
            trackLeft + trackWidth,
            trackLeft + range.Minimum * laneWidth,
            trackLeft + (range.Maximum + 1) * laneWidth,
            range.Minimum,
            range.Maximum,
            Bounds.Height * judgementLinePosition);
    }

    private double YAtBeat(double beat, TimelineGeometry geometry) =>
        geometry.JudgementY - (ProjectBeat(beat) - ProjectBeat(CurrentBeat)) * PixelsPerBeat;

    private double BeatAtY(double y, TimelineGeometry geometry) =>
        UnprojectBeat(ProjectBeat(CurrentBeat) + (geometry.JudgementY - y) / PixelsPerBeat);

    private double GetNoteAnchorBeatOffset(double pixelsPerBeat) =>
        TimelinePlacementGeometry.NoteAnchorBeatOffset(
            EffectiveNoteAnchor,
            session?.SnapDivision ?? WindowsEditorViewSettings.DefaultSnapDivision,
            noteHeight,
            pixelsPerBeat);

    private double GetMaximumStoredRenderBeatAdjustment() =>
        TimelinePlacementGeometry.MaximumStoredRenderBeatAdjustment(
            EffectiveNoteAnchor,
            session?.SnapDivision ?? WindowsEditorViewSettings.DefaultSnapDivision);

    private ChartNote? HitTestDisplayedNote(
        int lane,
        double pointerY,
        TimelineGeometry geometry,
        IReadOnlySet<Guid>? excludedIds = null,
        IReadOnlySet<int>? excludedGroups = null,
        bool includeDrafts = false)
    {
        var rawStoredBeat = PointerToRawNoteBeat(pointerY, geometry);
        var toleranceBeats = Math.Max(
            .04,
            noteHeight / Math.Max(1, PixelsPerBeat));
        noteIndex.Query(
            rawStoredBeat - GetMaximumStoredRenderBeatAdjustment() - toleranceBeats,
            rawStoredBeat + toleranceBeats,
            hitScratch,
            includeDrafts);
        var tolerancePixels = toleranceBeats * Math.Max(1, PixelsPerBeat);
        ChartNote? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var note in hitScratch)
        {
            if (ProEditorWorkspaceGeometry.GetColumn(note) != lane ||
                excludedIds?.Contains(note.Id) == true ||
                excludedGroups?.Contains(note.Group) == true)
                continue;
            var startY = NoteYAtBeat(note.Beat, geometry);
            var endY = note.EndBeat.HasValue
                ? NoteYAtBeat(note.EndBeat.Value, geometry)
                : startY;
            var distance = hideLongNoteBodies && note.EndBeat.HasValue
                ? Math.Abs(pointerY - startY)
                : DistanceToVerticalRange(pointerY, startY, endY);
            if (distance > tolerancePixels || distance >= bestDistance) continue;
            best = note;
            bestDistance = distance;
        }
        return best;
    }

    // Drawing replaces a head in the snapped grid cell, including the area
    // below its bottom anchor. Deletion and middle-button movement remain visual.
    private ChartNote? HitTestPlacementReplacement(int lane, Point point, TimelineGeometry geometry)
    {
        var snapped = PointerToNoteBeat(point.Y);
        noteIndex.Query(snapped.Value - .000002, snapped.Value + .000002, hitScratch, includeDrafts: proEditorEnabled);
        var head = hitScratch.FirstOrDefault(n => !hiddenGroups.Contains(n.Group) &&
            (n.IsDraft ? n.DraftColumn : n.Lane) == lane && Math.Abs(n.Beat.Value - snapped.Value) < .000002);
        return head ?? HitTestVisibleNote(point, geometry);
    }

    // Destructive input uses the drawn rectangle, never the forgiving resize handle.
    private ChartNote? HitTestVisibleNote(Point point, TimelineGeometry geometry)
    {
        var beat = PointerToRawNoteBeat(point.Y, geometry);
        var radius = noteHeight / (2 * Math.Max(1, PixelsPerBeat));
        noteIndex.Query(beat - GetMaximumStoredRenderBeatAdjustment() - radius,
            beat + radius, hitScratch, includeDrafts: proEditorEnabled);
        ChartNote? best = null;
        var bestRank = int.MaxValue;
        var bestDistance = double.PositiveInfinity;
        foreach (var note in hitScratch)
        {
            if (stretchDragging && stretchIds.Contains(note.Id) && !drawingStretchPreview) continue;
            if (hiddenGroups.Contains(note.Group)) continue;
            var bounds = GetNoteBounds(note, geometry);
            var head = NoteYAtBeat(note.Beat, geometry);
            var tail = note.EndBeat.HasValue ? NoteYAtBeat(note.EndBeat.Value, geometry) : head;
            if (hideLongNoteBodies && note.IsHold)
                bounds = new Rect(bounds.Left, head - noteHeight / 2, bounds.Width, noteHeight);
            if (!bounds.Contains(point)) continue;
            var distance = Math.Abs(point.Y - head);
            var rank = distance <= noteHeight / 2 ? 0 : Math.Abs(point.Y - tail) <= noteHeight / 2 ? 1 : 2;
            if (rank == 1) distance = Math.Abs(point.Y - tail);
            if (rank > bestRank || rank == bestRank && distance >= bestDistance) continue;
            best = note; bestRank = rank; bestDistance = distance;
        }
        return best;
    }

    private static double DistanceToVerticalRange(
        double pointerY,
        double startY,
        double endY)
    {
        var top = Math.Min(startY, endY);
        var bottom = Math.Max(startY, endY);
        return pointerY < top
            ? top - pointerY
            : pointerY > bottom
                ? pointerY - bottom
                : 0;
    }

    private double PointerToRawNoteBeat(double y, TimelineGeometry geometry) =>
        InteractionProfile == EditorInteractionProfile.OsuMania
        ? BeatAtY(y + noteHeight / 2, geometry)
        : TimelinePlacementGeometry.PointerToRawStoredBeat(
            BeatAtY(y, geometry),
            EffectiveNoteAnchor,
            session?.SnapDivision ?? WindowsEditorViewSettings.DefaultSnapDivision,
            noteHeight,
            PixelsPerBeat);

    private BeatPosition PointerToNoteBeat(double y)
    {
        if (session == null) return BeatPosition.Zero;
        var geometry = GetGeometry(session.Document.KeyCount);
        if (InteractionProfile == EditorInteractionProfile.OsuMania)
            return TimelinePlacementGeometry.NearestGrid(Math.Max(MinimumEditableBeat, BeatAtY(y, geometry)), session.SnapDivision);
        return TimelinePlacementGeometry.PointerToStoredBeat(
            BeatAtY(y, geometry),
            EffectiveNoteAnchor,
            session.SnapDivision,
            noteHeight,
            PixelsPerBeat);
    }

    private BeatPosition PointerToDragPreviewBeat(double y) =>
        InteractionProfile == EditorInteractionProfile.OsuMania
            ? BeatPosition.FromDouble(Math.Max(MinimumEditableBeat, BeatAtY(y, GetGeometry(session!.Document.KeyCount))), BeatPosition.MaximumPreservedDenominator)
            : PointerToNoteBeat(y);

    private double GridLineHalfThickness(BeatPosition beat)
    {
        var division = session?.SnapDivision ?? 4;
        var step = beat.Value * division;
        if (Math.Abs(step - Math.Round(step)) > 1e-6 || step < int.MinValue || step > int.MaxValue) return 0;
        var kind = WindowsTimelineVisualParityPolicy.ResolveGridLineKind(division, (int)Math.Round(step));
        return (kind == WindowsTimelineGridLineKind.Bar ? barGridPen : subdivisionGridPens[kind]).Thickness / 2;
    }

    private double NoteYAtBeat(
        BeatPosition beat,
        TimelineGeometry geometry,
        double pixelsPerBeat,
        double currentBeat) =>
        InteractionProfile == EditorInteractionProfile.OsuMania
        ? geometry.JudgementY - (ProjectBeat(beat.Value) - ProjectBeat(currentBeat)) * pixelsPerBeat - noteHeight / 2 - GridLineHalfThickness(beat)
        : geometry.JudgementY -
        (ProjectBeat(TimelinePlacementGeometry.StoredToRenderBeat(
             beat,
             EffectiveNoteAnchor,
             session?.SnapDivision ?? WindowsEditorViewSettings.DefaultSnapDivision,
             noteHeight,
             pixelsPerBeat)) - ProjectBeat(currentBeat)) *
        pixelsPerBeat;

    private double NoteYAtBeat(BeatPosition beat, TimelineGeometry geometry) =>
        NoteYAtBeat(beat, geometry, PixelsPerBeat, CurrentBeat);

    private double EvidencePointerY(BeatPosition beat, TimelineGeometry geometry) =>
        InteractionProfile == EditorInteractionProfile.OsuMania
            ? YAtBeat(beat.Value, geometry) - GridLineHalfThickness(beat) - .01 : NoteYAtBeat(beat, geometry);

    private static int WorkspaceColumnAtX(double x, TimelineGeometry geometry)
    {
        if (x < geometry.GridLeft || x >= geometry.GridRight) return NoWorkspaceColumn;
        var column = (int)Math.Floor((x - geometry.TrackLeft) / geometry.LaneWidth);
        return column < geometry.MinimumColumn || column > geometry.MaximumColumn
            ? NoWorkspaceColumn
            : column;
    }

    private readonly record struct TimelineGeometry(
        int KeyCount,
        double LaneWidth,
        double BaseTrackLeft,
        double TrackLeft,
        double TrackRight,
        double GridLeft,
        double GridRight,
        int MinimumColumn,
        int MaximumColumn,
        double JudgementY)
    {
        public double OfficialTrackWidth => TrackRight - TrackLeft;
    }
}
