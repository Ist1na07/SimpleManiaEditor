#nullable disable
#pragma warning disable CS4014, CS1998, CS8600, CS8602, CS8604, CS8618, CS8619, CS8625, CS0649, CS0169, CS0414, CS8632, CS0219, CS0162, CS0067
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Logging;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LumaChart.Application.Audio;
using LumaChart.Application.Collaboration;
using LumaChart.Application.Editing;
using LumaChart.Application.Exporting;
using LumaChart.Application.Libraries;
using LumaChart.Application.Validation;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Dialogs;
using LumaChart.Avalonia.Editing;
using LumaChart.Avalonia.Evidence;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Rendering;
using LumaChart.Avalonia.Services;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.Analysis;
using LumaChart.Core.Editing;
using LumaChart.Core.IO;
using LumaChart.Core.Localization;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;
using LumaChart.Core.Validation;
using LumaChart.Online;

namespace LumaChart.Avalonia
{
public sealed partial class MainWindow : PlatformWindow
{
private enum TempoAnalysisKind
{
    None,
    Single,
    Variable,
    Manual
}

private sealed record OnlineLatencyVisual(TextBlock Label, AutomationBorder Badge, string BadgePrefix);

private sealed class DifficultyTabState
{
    public ReworkDifficultyResult? Result { get; set; }
    public long ChangeSerial { get; set; } = -1L;
    public double? OverallDifficulty { get; set; }
}

private enum InspectorButtonTone
{
    Tool,
    Primary,
    Assign
}

private sealed class InspectorFlexibleField : Decorator
{
    protected override Size MeasureOverride(Size availableSize)
    {
        Control child = Child;
        if (child == null)
        {
            return default;
        }

        child.Measure(availableSize);
        return new Size(0.0, child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}

private sealed record TimingListItem(Guid Id, string Beat, string Main, string Effect);

private sealed record LibrarySortChoice(string Label, DesktopLibrarySort Sort)
{
    public override string ToString()
    {
        return Label;
    }
}

private sealed record LibraryRealizedContainerSnapshot(IReadOnlyList<ListBoxItem> ListBoxItems, IReadOnlyList<LibraryFolderCard> Cards);

private sealed record KeySoundPresetChoice(string Label, KeySoundPreset Preset)
{
    public override string ToString()
    {
        return Label;
    }
}

private sealed record PreparedOnlineRoomUpload(Guid TabId, long ChangeSerial, WindowsOnlineRoomUploadPackage Package);

private static readonly int[] QuickSnapDivisions = new int[16]
{
    1,
    2,
    3,
    4,
    5,
    6,
    7,
    8,
    9,
    10,
    12,
    16,
    24,
    32,
    48,
    64
};

private static readonly int[] WheelSnapDivisions = new int[11]
{
    1,
    2,
    3,
    4,
    6,
    8,
    12,
    16,
    24,
    32,
    48
};

private static readonly double[] QuickGridScales = new double[6]
{
    0.5,
    1.0,
    1.5,
    2.0,
    2.5,
    3.0
};

private static readonly double[] QuickPlaybackRates = new double[4]
{
    0.1,
    0.4,
    0.6,
    1.0
};

private static readonly Color WindowColor = Color.Parse("#111315");

private static readonly IBrush PanelBrush = new SolidColorBrush(Color.Parse("#1A1D20"));

private static readonly IBrush RaisedBrush = new SolidColorBrush(Color.Parse("#22272A"));

private static readonly IBrush StrokeBrush = new SolidColorBrush(Color.Parse("#30353A"));

private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#7FA4A7"));

private static readonly IBrush QuickActiveBrush = new SolidColorBrush(Color.Parse("#22282A"));

private static readonly IBrush QuickActiveBorderBrush = new SolidColorBrush(Color.Parse("#5F7E82"));

private static readonly IBrush QuickActiveForegroundBrush = new SolidColorBrush(Color.Parse("#F0F2F2"));

private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#929A9F"));

private readonly MainWindowViewModel viewModel = new MainWindowViewModel();

private readonly WindowsOnlineAccountHost? onlineAccountHost;

private readonly string? onlineHostError;

private readonly WindowsOnlineRoomResourceService onlineRoomResourceService = WindowsOnlineRoomResourceService.CreateDefault();

private readonly WindowsChartAudioImportWorkflow chartAudioImportWorkflow = WindowsChartAudioImportWorkflow.CreateDefault();

private readonly WindowsAudioTransportViewModel audioTransport = new WindowsAudioTransportViewModel();

private readonly WindowsAudioVisualizationViewModel audioVisualization = new WindowsAudioVisualizationViewModel();

private readonly WindowsKeySoundPlaybackRuntime keySoundRuntime;

private readonly WindowsTimingEffectMemory timingEffectMemory = new WindowsTimingEffectMemory();

private readonly IReadOnlyList<string> startupChartPaths;

private readonly bool evidenceChartMode;

private readonly bool evidenceShowOsuImportDialog;

private readonly bool evidenceFocusTiming;

private readonly bool evidenceFocusHoldTail;

private readonly bool evidenceFocusNoteProperty;

private readonly bool evidenceFocusGroup;

private readonly bool evidenceFocusProEditor;

private readonly bool evidenceFocusKeySound;

private readonly bool evidenceFocusAudioTransport;

private readonly bool evidenceFocusDisplayScale;

private readonly bool evidenceFocusSettingsCache;

private readonly bool evidenceFocusAudioPanels;

private readonly bool evidenceFocusLibraryDeletion;

private readonly bool evidenceFocusLibraryLauncher;

private readonly bool evidenceFocusChartValidation;

private readonly bool evidenceFocusTitleBarAnimation;

private readonly bool evidenceFocusLibraryVirtualization;

private readonly bool evidenceSuppressExportReveal;

private readonly bool evidenceFollowActiveTab;

private readonly int evidenceAutoBackupDelayMilliseconds;

private readonly int evidenceAudioPermissionRevokeDelayMilliseconds;

private readonly int evidenceAudioPreCommitDelayMilliseconds;

private readonly string? evidenceFilePickerStartDirectory;

private readonly bool evidenceUsePlacementProbeViewport;

private readonly BeatPosition? evidenceFocusNotePropertyBeat;

private readonly BeatPosition? evidenceFocusGroupBeat;

private readonly bool evidenceEnableBackgroundWaveform;

private readonly bool evidenceEnableBackgroundSpectrogram;

private readonly string? evidenceOutputPath;

private readonly string? evidenceInteractionOutputPath;

private readonly int evidenceSyntheticNoteCount;

private readonly int evidenceInteractionDelayMilliseconds;

private double evidenceLibraryInitializationMilliseconds;

private readonly Grid libraryView = new Grid();

private readonly Grid editorView = new Grid();

private readonly StackPanel tabBar = new StackPanel
{
    Orientation = Orientation.Horizontal
};

private readonly List<OnlineLatencyVisual> onlineLatencyVisuals = new List<OnlineLatencyVisual>();

private readonly Grid shellRoot = new Grid
{
    RowDefinitions = new RowDefinitions($"{38.0},*")
};

private readonly AutomationBorder titleBar = new AutomationBorder();

private readonly TranslateTransform titleBarTranslate = new TranslateTransform();

private readonly AutomationBorder fullscreenTitleRevealHotZone = new AutomationBorder();

private readonly DispatcherTimer fullscreenTitleHideTimer = new DispatcherTimer(DispatcherPriority.Input)
{
    Interval = TimeSpan.FromMilliseconds(480.0)
};

private readonly DispatcherTimer fullscreenTitleAnimationTimer = new DispatcherTimer(DispatcherPriority.Render)
{
    Interval = TimeSpan.FromMilliseconds(8.0)
};

private bool fullscreenTitleBarRevealed;

private bool fullscreenTitleAnimationRunning;

private bool fullscreenTitleAnimationRevealed;

private bool tabletAppChromeHidden;

private double fullscreenTitleAnimationFrom;

private double fullscreenTitleAnimationTarget;

private readonly Stopwatch fullscreenTitleAnimationStopwatch = new Stopwatch();

private readonly TextBlock titleSaveStateText = new TextBlock();

private readonly TextBlock titleUserNameText = new TextBlock();

private readonly TextBlock titleUserStateText = new TextBlock();

private readonly WindowsDefaultUserAvatar titleUserAvatar = new WindowsDefaultUserAvatar(24.0, new SolidColorBrush(Color.Parse("#59666A")), 1.0, "OnlineAccount.Title.Avatar");

private readonly Border commandMenu = new Border();

private readonly Button commandMenuButton = new Button();

private Button? commandPlayButton;

private Button? commandStopButton;

private Button? commandFullscreenButton;

private Button? difficultyAnalysisButton;

private readonly TextBlock proModeLabel = new TextBlock();

private readonly Border quickEditPalette = new Border();

private readonly StackPanel quickEditPaletteContent = new StackPanel();

private readonly Viewbox quickEditPaletteScaleHost = new Viewbox();

private readonly StackPanel quickEditPaletteBody = new StackPanel();

private readonly Button quickEditPaletteCollapseButton = new Button();

private readonly Border quickEditPaletteResizeGrip = new Border();

private readonly TranslateTransform quickEditPaletteTransform = new TranslateTransform();

private readonly Border quickValuesPalette = new Border();

private readonly StackPanel quickValuesPaletteBody = new StackPanel();

private readonly Button quickValuesPaletteCollapseButton = new Button();

private readonly TranslateTransform quickValuesPaletteTransform = new TranslateTransform();

private readonly TextBlock quickSnapText = new TextBlock();

private readonly TextBlock quickGridScaleText = new TextBlock();

private readonly TextBlock quickPlaybackRateText = new TextBlock();

private readonly Border extensionPalette = new Border();

private readonly TranslateTransform extensionPaletteTransform = new TranslateTransform();

private readonly AutomationBorder roomCollaborationPanel = new AutomationBorder();

private readonly Grid roomCollaborationBody = new Grid();

private readonly TranslateTransform roomCollaborationTransform = new TranslateTransform();

private readonly TextBlock roomCollaborationTitle = new TextBlock();

private readonly TextBlock roomMemberCountText = new TextBlock();

private readonly AutomationBorder roomChatUnreadDot = new AutomationBorder();

private readonly ListBox roomMemberList = new ListBox();

private bool suppressRoomMemberSelectionChanged;

private readonly ListBox roomChatList = new ListBox();

private readonly TextBlock roomChatStatusText = new TextBlock();

private readonly TextBox roomChatInputBox = new TextBox();

private readonly Button roomCollaborationToggleButton = new Button();

private readonly Button sendRoomChatButton = new Button();

private readonly List<OnlineRoomChatMessageDto> roomChatMessages = new List<OnlineRoomChatMessageDto>();

private bool roomCollaborationCollapsed;

private bool roomCollaborationDragging;

private Point roomCollaborationDragStart;

private double roomCollaborationDragStartX;

private double roomCollaborationDragStartY;

private double roomCollaborationBaseLeft;

private double roomCollaborationBaseTop;

private bool roomChatHasUnread;

private bool roomChatInitialized;

private bool roomChatSendInProgress;

private long latestRoomMessageId;

private TextBlock? quickGroupLabel;

private TextBlock? quickOwnerLockLabel;

private readonly Grid editorWorkbench = new Grid();

private readonly Border inspectorPanel = new Border();

private readonly Button inspectorToggleButton = new Button();

private readonly EditorOverviewSurface editorOverview = new EditorOverviewSurface();

private readonly TextBlock miniNoteCountText = new TextBlock();

private readonly Button navigationMarkerAddButton = new Button();

private readonly Button navigationMarkerListButton = new Button();

private readonly Popup navigationMarkerPopup = new Popup();

private readonly Border navigationMarkerPopupSurface = new Border();

private readonly StackPanel navigationMarkerList = new StackPanel();

private readonly TextBlock navigationMarkerTitleText = new TextBlock();

private readonly TextBlock navigationMarkerEmptyText = new TextBlock();

private readonly TextBlock navigationMarkerCountText = new TextBlock();

private readonly List<Button> navigationMarkerButtons = new List<Button>();

private readonly List<Button> navigationMarkerDeleteButtons = new List<Button>();

private readonly SemaphoreSlim navigationMarkerPersistenceGate = new SemaphoreSlim(1, 1);

private readonly DispatcherTimer navigationMarkerSaveTimer;

private readonly DifficultyOverviewSurface difficultyOverview = new DifficultyOverviewSurface();

private readonly TextBlock difficultyStarText = new TextBlock();

private readonly AutomationBorder difficultyInfoCard = new AutomationBorder();

private readonly AutomationBorder difficultyInfoCardHeader = new AutomationBorder();

private readonly StackPanel difficultyInfoCardBody = new StackPanel();

private readonly Button difficultyCardCollapseButton = new Button();

private readonly TextBlock difficultyCardStateText = new TextBlock();

private readonly TextBlock difficultyCardTitleText = new TextBlock();

private readonly TextBlock difficultyCardStarText = new TextBlock();

private readonly TextBlock difficultyCardReferenceText = new TextBlock();

private readonly TextBlock difficultyCardLnText = new TextBlock();

private readonly TextBlock difficultyCardNoteText = new TextBlock();

private readonly TextBlock difficultyCardOdText = new TextBlock();

private readonly TextBlock difficultyCardPatternFamilyText = new TextBlock();

private readonly StackPanel difficultyCardPatternList = new StackPanel
{
    Spacing = 0.0
};

private readonly TextBlock difficultyCardPatternHintText = new TextBlock();

private readonly TranslateTransform difficultyInfoCardTransform = new TranslateTransform();

private readonly ListBox libraryList = new ListBox();

private readonly WindowsLibraryFolderExpansionState libraryFolderExpansionState = new WindowsLibraryFolderExpansionState();

private readonly TextBlock libraryTitle = new TextBlock();

private readonly TextBlock libraryCountText = new TextBlock();

private readonly TextBlock workspaceText = new TextBlock();

private readonly TextBlock libraryStatusText = new TextBlock();

private readonly TextBlock statusText = new TextBlock();

private readonly TextBlock frameRateStatusText = new TextBlock();

private bool frameRateStatusIdleShown;

private string? audioPlayPauseButtonContentSource;

private string? audioStatusTextSource;

private string? commandPlayButtonNameSource;

private readonly Border statusBar = new Border();

private Button? onlineAccountButton;

private readonly AutomationBorder friendsPanel = new AutomationBorder();

private readonly TextBlock friendsOnlineCountText = new TextBlock();

private readonly TextBlock friendsStatusText = new TextBlock();

private readonly ListBox friendsList = new ListBox();

private readonly Dictionary<long, DateTimeOffset> friendInviteCooldowns = new Dictionary<long, DateTimeOffset>();

private readonly Dictionary<long, Button> friendInviteButtons = new Dictionary<long, Button>();

private readonly DispatcherTimer friendInviteCooldownTimer = new DispatcherTimer(DispatcherPriority.Background)
{
    Interval = TimeSpan.FromSeconds(1.0)
};

private CancellationTokenSource? friendsPanelLoadCancellation;

private readonly AutomationBorder onlinePlayerPopupLayer = new AutomationBorder();

private readonly Grid onlinePlayerPopupContent = new Grid();

private CancellationTokenSource? onlinePlayerPopupLoadCancellation;

private long onlinePlayerPopupUserId;

private readonly Grid onlineSurfaceContent = new Grid();

private readonly AutomationBorder onlineSurfaceLayer = new AutomationBorder
{
    IsVisible = false,
    Background = new SolidColorBrush(Color.Parse("#F7111315"))
};

private TaskCompletionSource<bool>? onlineSurfaceCompletion;

private bool onlineAccountEntryInProgress;

private OnlineCommunityDialog? activeHostedOnlineCommunitySurface;

private OnlineCommunityHostedSection pendingOnlineCommunitySection;

private readonly AutomationBorder audioImportNameLayer = new AutomationBorder();

private readonly TextBlock audioImportSourceText = new TextBlock();

private readonly TextBox audioImportNameBox = new TextBox();

private readonly TextBlock audioImportNameStatusText = new TextBlock();

private Button? audioImportNameConfirmButton;

private TaskCompletionSource<string?>? audioImportNameCompletion;

private bool audioChartCreationInProgress;

private bool startupAccountChoiceHandled;

private OnlineAppConfigurationDto? appConfiguration;

private WindowsAppUpdateSnapshot? appUpdateSnapshot;

private bool githubUpdateChecking;

private readonly TextBlock settingsVersionText = new TextBlock();

private readonly AutomationBorder aboutLayer = new AutomationBorder();

private readonly TextBlock aboutContentText = new TextBlock();

private readonly TextBlock aboutVersionText = new TextBlock();

private Button? aboutAdminButton;

private readonly AutomationBorder updateLayer = new AutomationBorder();

private readonly TextBlock updateTitleText = new TextBlock();

private readonly TextBlock updateVersionText = new TextBlock();

private readonly TextBlock updateNoticeText = new TextBlock();

private Button? updateLaterButton;

private Button? updateDownloadButton;

private TaskCompletionSource<bool>? updatePromptCompletion;

private bool updatePromptBlocksLogin;

private readonly AutomationBorder roomInvitationToast = new AutomationBorder();

private readonly AutomationBorder followNotificationToast = new AutomationBorder();

private readonly TextBlock followNotificationToastText = new TextBlock();

private readonly AutomationBorder roomUploadProgressPopup = new AutomationBorder();

private readonly ProgressBar roomUploadProgressBar = new ProgressBar();

private readonly TextBlock roomUploadProgressTitleText = new TextBlock();

private readonly TextBlock roomUploadProgressDetailText = new TextBlock();

private readonly TextBlock roomUploadProgressValueText = new TextBlock();

private Guid? roomUploadProgressRoomId;

private DateTimeOffset roomUploadProgressShownUtc;

private readonly AutomationBorder roomDownloadProgressLayer = new AutomationBorder();

private readonly ProgressBar roomDownloadProgressBar = new ProgressBar();

private readonly TextBlock roomDownloadProgressTitleText = new TextBlock();

private readonly TextBlock roomDownloadProgressDetailText = new TextBlock();

private readonly TextBlock roomDownloadProgressValueText = new TextBlock();

private Guid? roomDownloadProgressRoomId;

private DateTimeOffset roomDownloadProgressShownUtc;

private readonly Queue<string> followNotificationToastQueue = new Queue<string>();

private readonly DispatcherTimer followNotificationToastTimer = new DispatcherTimer(DispatcherPriority.Background)
{
    Interval = TimeSpan.FromMilliseconds(4000.0)
};

private readonly DispatcherTimer followNotificationTimer = new DispatcherTimer(DispatcherPriority.Background)
{
    Interval = TimeSpan.FromMilliseconds(5000.0)
};

private readonly DispatcherTimer chartingHeartbeatTimer = new DispatcherTimer(DispatcherPriority.Background)
{
    Interval = TimeSpan.FromSeconds(30.0)
};

private readonly CancellationTokenSource followNotificationPollingCancellation = new CancellationTokenSource();

private readonly CancellationTokenSource chartingHeartbeatLifetime = new CancellationTokenSource();

private readonly Dictionary<Guid, Guid> chartingHeartbeatSessionIds = new Dictionary<Guid, Guid>();

private Task activeChartingHeartbeatTask = Task.CompletedTask;

private bool chartingHeartbeatInProgress;

private string chartingHeartbeatStatus = "等待有效谱面操作";

private readonly AutomationBorder editorToast = new AutomationBorder();

private readonly TextBlock editorToastText = new TextBlock();

private readonly DispatcherTimer editorToastTimer = new DispatcherTimer(DispatcherPriority.Background)
{
    Interval = TimeSpan.FromMilliseconds(3000.0)
};

private readonly TextBlock roomInvitationTitleText = new TextBlock();

private readonly TextBlock roomInvitationDetailText = new TextBlock();

private Button? roomInvitationCancelButton;

private Button? roomInvitationJoinButton;

private readonly AutomationBorder roomPermissionNoticeLayer = new AutomationBorder();

private readonly TextBlock roomPermissionNoticeTitle = new TextBlock();

private readonly TextBlock roomPermissionNoticeText = new TextBlock();

private Button? roomPermissionNoticeDismissButton;

private readonly TextBlock inspectorText = new TextBlock();

private readonly TextBlock noteSelectionHint = new TextBlock();

private readonly TextBox noteBeatBox = new TextBox();

private readonly TextBox noteEndBeatBox = new TextBox();

private readonly TextBox noteLaneBox = new TextBox();

private readonly TextBox noteVolumeBox = new TextBox();

private readonly TextBox noteSoundBox = new TextBox();

private readonly Button noteSoundExistingButton = new Button();

private readonly Button noteSoundSelectButton = new Button();

private readonly Popup noteSoundExistingPopup = new Popup();

private readonly Border noteSoundPickerSurface = new Border();

private readonly TextBlock noteSoundPickerTitleText = new TextBlock();

private readonly TextBlock noteSoundPickerSubtitleText = new TextBlock();

private readonly TextBlock noteSoundPickerCountText = new TextBlock();

private readonly TextBlock noteSoundPickerEmptyText = new TextBlock();

private readonly ScrollViewer noteSoundPickerScrollViewer = new ScrollViewer();

private readonly StackPanel noteSoundPickerItems = new StackPanel();

private readonly Button noteSoundPickerClearButton = new Button();

private readonly List<Button> noteSoundPickerFocusableButtons = new List<Button>();

private string noteSoundPickerLastNavigation = "none";

private string noteSoundPickerLastRawKey = "none";

private string noteSoundPickerLastResolvedKey = "none";

private string noteSoundPickerLastInputRoute = "none";

private bool noteSoundPickerEscapeRestoredFocus;

private readonly TextBox noteGroupBox = new TextBox();

private Guid? noteInspectorNoteId;

private WindowsNotePropertyFields? noteInspectorFields;

private readonly Button noteApplyButton = new Button();

private readonly Button noteDeleteButton = new Button();

private readonly TabControl editorInspectorTabs = new TabControl();

private readonly TabItem noteInspectorTab = new TabItem();

private readonly TabItem metadataInspectorTab = new TabItem();

private readonly TabItem timingInspectorTab = new TabItem();

private readonly TabItem groupInspectorTab = new TabItem();

private readonly TabItem settingsInspectorTab = new TabItem();

private readonly ContentControl tabletInspectorContent = new ContentControl();

private readonly Dictionary<TabItem, Control> tabletInspectorContents = new Dictionary<TabItem, Control>();

private readonly Dictionary<TabItem, Button> tabletInspectorButtons = new Dictionary<TabItem, Button>();

private readonly TextBox metadataTitleBox = new TextBox();

private readonly TextBox metadataTitleOriginalBox = new TextBox();

private readonly Button metadataRomanizeTitleButton = new Button();

private readonly TextBox metadataArtistBox = new TextBox();

private readonly TextBox metadataArtistOriginalBox = new TextBox();

private readonly Button metadataRomanizeArtistButton = new Button();

private readonly TextBox metadataCreatorBox = new TextBox();

private readonly TextBox metadataVersionBox = new TextBox();

private readonly TextBox metadataBpmBox = new TextBox();

private readonly TextBox metadataOffsetBox = new TextBox();

private readonly ComboBox metadataKeyCountBox = new ComboBox();

private readonly TextBox metadataPreviewTimeBox = new TextBox();

private readonly TextBox metadataOverallDifficultyBox = new TextBox();

private readonly TextBox metadataHpDrainRateBox = new TextBox();

private readonly TextBox metadataAudioFileBox = new TextBox
{
    IsReadOnly = true
};

private readonly Button metadataAudioSelectButton = new Button();

private readonly TextBox metadataCoverFileBox = new TextBox
{
    IsReadOnly = true
};

private readonly Button metadataCoverSelectButton = new Button();

private readonly TextBox metadataBackgroundFileBox = new TextBox
{
    IsReadOnly = true
};

private readonly Button metadataBackgroundSelectButton = new Button();

private readonly Button metadataAnalyzeSingleTempoButton = new Button();

private readonly Button metadataAnalyzeVariableTempoButton = new Button();

private readonly Button metadataAnalyzeManualTempoButton = new Button();

private readonly TextBlock metadataAudioAnalysisText = new TextBlock();

private readonly Button metadataPreviewHereButton = new Button();

private readonly Button metadataApplyButton = new Button();

private readonly TextBlock metadataStatusText = new TextBlock();

private readonly TextBlock metadataStatsNotesText = new TextBlock
{
    Text = "0"
};

private readonly TextBlock metadataStatsHoldsText = new TextBlock
{
    Text = "0"
};

private readonly TextBlock metadataStatsTimingText = new TextBlock
{
    Text = "0"
};

private readonly Slider uiScaleSlider = new Slider();

private readonly Slider fontScaleSlider = new Slider();

private readonly ComboBox languagePreferenceBox = new ComboBox();

private readonly Button uiScaleDecreaseButton = new Button();

private readonly Button uiScaleIncreaseButton = new Button();

private readonly Button fontScaleDecreaseButton = new Button();

private readonly Button fontScaleIncreaseButton = new Button();

private readonly TextBlock uiScaleValueText = new TextBlock();

private readonly TextBlock fontScaleValueText = new TextBlock();

private readonly TextBlock displayScaleStatusText = new TextBlock();

private readonly TextBlock languageStatusText = new TextBlock();

private readonly ComboBox settingsSnapBox = new ComboBox();

private readonly ComboBox settingsFixedSnapBox = new ComboBox();

private readonly Button anchorLineButton = new Button();

private readonly Button anchorLineAboveButton = new Button();

private readonly Button anchorCellButton = new Button();

private readonly Button anchorCellBottomButton = new Button();

private readonly Slider gridLaneWidthSlider = new Slider();

private readonly Slider gridBeatHeightSlider = new Slider();

private readonly Slider mainEditorHorizontalOffsetSlider = new Slider();

private readonly Slider noteWidthSlider = new Slider();

private readonly Slider noteHeightSlider = new Slider();

private readonly Slider noteOpacitySlider = new Slider();

private readonly Slider trackOpacitySlider = new Slider();

private readonly Slider backgroundDarknessSlider = new Slider();

private readonly Slider spectrogramStrengthSlider = new Slider();

private readonly Slider spectrogramDetailSlider = new Slider();

private readonly TextBlock spectrogramStrengthValueText = new TextBlock();

private readonly TextBlock spectrogramDetailValueText = new TextBlock();

private readonly Slider spectrogramFlyoutStrengthSlider = new Slider();

private readonly Slider spectrogramFlyoutDetailSlider = new Slider();

private readonly TextBlock spectrogramFlyoutStrengthValueText = new TextBlock();

private readonly TextBlock spectrogramFlyoutDetailValueText = new TextBlock();

private bool suppressSpectrogramAppearanceControls;

private readonly Slider barLineThicknessSlider = new Slider();

private readonly Slider subdivisionLineThicknessSlider = new Slider();

private readonly Slider judgementLinePositionSlider = new Slider();

private readonly Slider judgementLineThicknessSlider = new Slider();

private readonly Slider judgementLineOpacitySlider = new Slider();

private readonly TextBlock gridLaneWidthValueText = new TextBlock();

private readonly TextBlock gridBeatHeightValueText = new TextBlock();

private readonly TextBlock mainEditorHorizontalOffsetValueText = new TextBlock();

private readonly TextBlock noteWidthValueText = new TextBlock();

private readonly TextBlock noteHeightValueText = new TextBlock();

private readonly TextBlock noteOpacityValueText = new TextBlock();

private readonly TextBlock trackOpacityValueText = new TextBlock();

private readonly TextBlock backgroundDarknessValueText = new TextBlock();

private readonly TextBlock barLineThicknessValueText = new TextBlock();

private readonly TextBlock subdivisionLineThicknessValueText = new TextBlock();

private readonly TextBlock judgementLinePositionValueText = new TextBlock();

private readonly TextBlock judgementLineThicknessValueText = new TextBlock();

private readonly TextBlock judgementLineOpacityValueText = new TextBlock();

private readonly ComboBox frameRateLimitBox = new ComboBox();

private readonly Button resetEditorViewButton = new Button();

private readonly CheckBox autoBackupEnabledBox = new CheckBox();

private readonly Slider autoBackupIntervalSlider = new Slider();

private readonly TextBlock autoBackupIntervalText = new TextBlock();

private readonly TextBlock backupFolderText = new TextBlock();

private readonly TextBlock backupCacheStatusText = new TextBlock();

private readonly CheckBox cacheBackupFilesBox = new CheckBox();

private readonly CheckBox cacheEditingFilesBox = new CheckBox();

private readonly CheckBox cacheResourceFilesBox = new CheckBox();

private readonly Button cleanSelectedCacheButton = new Button();

private readonly StackPanel shortcutListPanel = new StackPanel
{
    Spacing = 6.0
};

private readonly TextBlock shortcutStatusText = new TextBlock();

private readonly Dictionary<string, Button> shortcutButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);

private readonly LayoutTransformControl displayScaleRoot = new LayoutTransformControl();

private readonly AvaloniaWindowFontScaleController fontScaleController;

private readonly AvaloniaLocalizationController localizationController;

private readonly WindowsDisplayScaleController displayScaleController;

private readonly AvaloniaPopupScaleController popupScaleController;

private readonly TextBox groupIdBox = new TextBox
{
    Text = "1"
};

private readonly TextBlock currentPlacementGroupText = new TextBlock();

private readonly TextBlock groupManagerHint = new TextBlock();

private readonly StackPanel groupListPanel = new StackPanel
{
    Spacing = 7.0
};

private readonly Button groupCreateAndUseButton = new Button();

private readonly Button groupSetCurrentButton = new Button();

private readonly Button groupAssignButton = new Button();

private readonly Button groupShowAllButton = new Button();

private readonly Button groupHideNonzeroButton = new Button();

private readonly Button groupClearSelectedButton = new Button();

private readonly ListBox timingList = new ListBox();

private readonly TextBlock timingPermissionText = new TextBlock();

private readonly TextBox timingBeatBox = new TextBox();

private readonly TextBox timingBpmBox = new TextBox();

private readonly TextBox timingDelayBox = new TextBox();

private readonly ComboBox timingEffectBox = new ComboBox();

private readonly TextBox timingValueBox = new TextBox();

private readonly TextBox timingCustomFieldBox = new TextBox();

private readonly Border emptyLibraryPanel = new Border();

private readonly TextBlock emptyLibraryTitleText = new TextBlock();

private readonly TextBlock emptyLibrarySubtitleText = new TextBlock();

private readonly StackPanel emptyLibraryActions = new StackPanel();

private readonly TextBox librarySearchBox = new TextBox();

private readonly ComboBox librarySortBox = new ComboBox();

private readonly Button libraryDeleteSelectedButton = new Button();

private ListBoxItem? pendingLibraryContextMenuItem;

private DesktopLibraryFolderView? libraryLongPressFolder;

private Control? libraryLongPressHost;

private DispatcherTimer? libraryLongPressTimer;

private Point libraryLongPressOrigin;

private readonly TimelineSurface timeline = new TimelineSurface();

private readonly Grid timelineHost = new Grid
{
    RowDefinitions = new RowDefinitions("*,34")
};

private readonly Border waveformPanel = new Border();

private readonly Border spectrogramPanel = new Border();

private readonly Border waveformHeader = new Border();

private readonly Border spectrogramHeader = new Border();

private readonly TextBlock waveformTitleText = new TextBlock();

private readonly TextBlock spectrogramTitleText = new TextBlock();

private readonly Button waveformModeButton = new Button();

private readonly Button waveformCollapseButton = new Button();

private readonly Button spectrogramModeButton = new Button();

private readonly Button spectrogramCollapseButton = new Button();

private readonly AudioVisualizationSideSurface waveformSideSurface = new AudioVisualizationSideSurface(AudioVisualizationSideKind.Waveform);

private readonly AudioVisualizationSideSurface spectrogramSideSurface = new AudioVisualizationSideSurface(AudioVisualizationSideKind.Spectrogram);

private readonly TranslateTransform waveformPanelTransform = new TranslateTransform();

private readonly TranslateTransform spectrogramPanelTransform = new TranslateTransform();

private readonly ComboBox divisionPicker = new ComboBox();

private readonly ToggleButton subdivisionCheckButton = new ToggleButton();

private readonly Button proEditorButton = new Button();

private readonly Slider proGridColumnSlider = new Slider();

private readonly TextBlock proGridColumnText = new TextBlock();

private readonly Border proTrackPanBar = new Border();

private readonly Slider proTrackPanSlider = new Slider();

private readonly Button audioPlayPauseButton = new Button();

private readonly Button audioStopButton = new Button();

private readonly Button audioRefreshDeviceButton = new Button();

private readonly Slider audioPositionSlider = new Slider();

private readonly Slider audioVolumeSlider = new Slider();

private readonly Slider audioRateSlider = new Slider();

private readonly CheckBox audioPreservePitchBox = new CheckBox();

private readonly TextBlock audioTimeText = new TextBlock();

private readonly TextBlock audioRateText = new TextBlock();

private readonly TextBlock audioVolumeText = new TextBlock();

private readonly TextBlock audioStatusText = new TextBlock();

private readonly TextBlock audioVisualizationStatusText = new TextBlock();

private readonly CheckBox waveformBackgroundBox = new CheckBox();

private readonly CheckBox spectrogramBackgroundBox = new CheckBox();

private readonly Border keySoundPanel = new Border();

private readonly CheckBox keySoundEnabledBox = new CheckBox();

private readonly ComboBox keySoundPresetBox = new ComboBox();

private readonly Button keySoundCustomFileButton = new Button();

private readonly TextBlock keySoundCustomFileText = new TextBlock();

private readonly Slider keySoundVolumeSlider = new Slider();

private readonly TextBlock keySoundVolumeText = new TextBlock();

private readonly CheckBox keySoundLongNoteEndBox = new CheckBox();

private readonly Slider keySoundGlobalOffsetSlider = new Slider();

private readonly TextBlock keySoundGlobalOffsetText = new TextBlock();

private readonly Slider keySoundChartOffsetSlider = new Slider();

private readonly TextBlock keySoundChartOffsetText = new TextBlock();

private readonly TextBlock keySoundRuntimeStatusText = new TextBlock();

private readonly Dictionary<TimelineTool, Button> timelineToolButtons = new Dictionary<TimelineTool, Button>();

private readonly Dictionary<TimelineTool, Button> quickTimelineToolButtons = new Dictionary<TimelineTool, Button>();

private readonly Dictionary<string, (Button Button, string Description)> quickShortcutButtons = new Dictionary<string, (Button, string)>(StringComparer.Ordinal);

private Button? mixedCopyButton;

private Button? mixedPasteButton;

private Button? mixedDeleteButton;

private Button? ownerLockButton;

private Button? quickOwnerLockButton;

private Button? quickMirrorButton;

private Button? quickEffectButton;

private Button? extensionMenuButton;

private Button? extensionSelectLongNotesButton;

private Button? extensionSelectTapsButton;

private Button? hideLongNoteBodiesButton;

private Button? longToTapButton;

private Button? tapToLongButton;

private Button? randomPermutationButton;

private Button? fullRandomButton;

private Button? exportButton;

private Button? timingAddButton;

private Button? timingApplyButton;

private Button? timingDeleteButton;

private Button? timingCopyButton;

private Button? timingPasteButton;

private DispatcherTimer? evidenceTimer;

private readonly DispatcherTimer audioUiTimer;

private readonly DispatcherTimer proGridColumnSaveTimer;

private readonly DispatcherTimer keySoundPlanDebounceTimer;

private readonly DispatcherTimer keySoundSettingsSaveTimer;

private readonly DispatcherTimer displayScaleSettingsSaveTimer;

private readonly DispatcherTimer editorViewSettingsSaveTimer;

private readonly DispatcherTimer settingsSnapCommitTimer;

private readonly DispatcherTimer settingsFixedSnapCommitTimer;

private readonly DispatcherTimer shortcutSettingsSaveTimer;

private readonly DispatcherTimer audioPanelSettingsSaveTimer;

private readonly DispatcherTimer autoBackupTimer;

private readonly DispatcherTimer roomInvitationTimer;

private readonly WindowsHighRateFramePump editorHighRateFramePump = new WindowsHighRateFramePump();

private readonly WindowsFrameRateGate editorCompositionFrameGate = new WindowsFrameRateGate(1000);

private readonly Stopwatch editorFrameClock = Stopwatch.StartNew();

private readonly Stopwatch audioPositionUiClock = Stopwatch.StartNew();

private Stopwatch? evidenceStopwatch;

private double evidenceBaseBeat;

private int evidenceTicks;

private bool initialized;

private bool startupInitializationCompleted;

private bool singleInstanceDrainRunning;

private bool singleInstanceDrainRetryScheduled;

private readonly Queue<WindowsSingleInstanceOpenRequest> singleInstanceRequests = new Queue<WindowsSingleInstanceOpenRequest>();

private bool windowClosePreflightComplete;

private bool windowClosePreflightInProgress;

private bool windowCloseTeardownStarted;

private readonly HashSet<Guid> closingTabIds = new HashSet<Guid>();

private bool playtestInProgress;

private bool saveInProgress;

private bool emergencyPersistAttempted;

private readonly Dictionary<Guid, TaskCompletionSource<SaveRoundTripResult?>> pendingFollowUpSaves = new Dictionary<Guid, TaskCompletionSource<SaveRoundTripResult>>();

private TaskCompletionSource saveSlotReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

private bool exportInProgress;

private Task activeExportTask = Task.CompletedTask;

private CancellationTokenSource? activeExportCancellation;

private Guid? timingListTabId;

private long timingListChangeSerial = -1L;

private bool suppressTimingSelection;

private Guid? timingEditorTimingId;

private bool timingEditorDirty;

private bool suppressTimingEditorControls;

private KeyModifiers timingListSelectionModifiers;

private long timingListInputVersion;

private Guid? metadataEditorTabId;

private long metadataEditorChangeSerial = -1L;

private bool suppressMetadataEditorControls;

private bool metadataEditorDirty;

private bool metadataKeyCountChangeInProgress;

private ChartMetadataDraft? metadataEditorBaseline;

private string? lastMetadataApplyError;

private CancellationTokenSource? audioAnalysisCancellation;

private Task activeAudioAnalysisTask = Task.CompletedTask;

private Guid? audioAnalysisTabId;

private bool audioAnalysisInProgress;

private TempoAnalysisKind activeTempoAnalysisKind;

private CancellationTokenSource? audioImportCancellation;

private Task activeAudioImportTask = Task.CompletedTask;

private bool audioImportInProgress;

private CancellationTokenSource? missingAudioRecoveryCancellation;

private Task activeMissingAudioRecoveryTask = Task.CompletedTask;

private string? missingAudioRecoveryKey;

private CancellationTokenSource? imageImportCancellation;

private Task activeImageImportTask = Task.CompletedTask;

private Guid? imageImportTabId;

private bool imageImportInProgress;

private CancellationTokenSource? titleRomanizationCancellation;

private CancellationTokenSource? artistRomanizationCancellation;

private Task activeTitleRomanizationTask = Task.CompletedTask;

private Task activeArtistRomanizationTask = Task.CompletedTask;

private Guid? titleRomanizationTabId;

private Guid? artistRomanizationTabId;

private bool titleRomanizationInProgress;

private bool artistRomanizationInProgress;

private CancellationTokenSource? backgroundImageLoadCancellation;

private long backgroundImageLoadGeneration;

private Guid? backgroundImageTabId;

private string? backgroundImagePath;

private bool suppressAudioControls;

private bool audioPositionPointerDown;

private bool suppressKeySoundControls;

private bool suppressDisplayScaleControls;

private bool suppressLanguageControls;

private bool suppressEditorViewControls;

private bool suppressAudioPanelControls;

private WindowsEditorBackgroundSettings editorBackgroundSettings = WindowsEditorBackgroundSettingsStore.Load();

private bool editorBackgroundSettingsDirty;

private bool navigationMarkerSettingsDirty;

private long navigationMarkerRevision;

private static readonly TimeSpan NavigationMarkerSaveDelay = TimeSpan.FromMilliseconds(350.0);

private const int NavigationMarkerSaveMaximumRetries = 6;

private int navigationMarkerSaveFailureCount;

private WindowsAudioPanelSettings audioPanelSettings = WindowsAudioPanelSettingsStore.Load();

private bool audioPanelSettingsDirty;

private bool audioPanelLayoutQueued;

private WindowsAudioPanelKind? draggingAudioPanelKind;

private Point audioPanelDragStart;

private double audioPanelDragStartX;

private double audioPanelDragStartY;

private bool quickEditPaletteDragging;

private Point quickEditPaletteDragStart;

private double quickEditPaletteDragStartX;

private double quickEditPaletteDragStartY;

private double quickEditPaletteBaseLeft;

private double quickEditPaletteBaseTop;

private bool quickEditPaletteResizing;

private Point quickEditPaletteResizeStart;

private double quickEditPaletteResizeStartWidth;

private double quickEditPaletteResizeStartHeight;

private double quickEditPaletteResizeStartTransformX;

private double quickEditPaletteWidth = 224.0;

private double? quickEditPaletteHeight;

private bool quickValuesPaletteDragging;

private Point quickValuesPaletteDragStart;

private double quickValuesPaletteDragStartX;

private double quickValuesPaletteDragStartY;

private double quickValuesPaletteBaseLeft;

private double quickValuesPaletteBaseTop;

private bool extensionPaletteDragging;

private Point extensionPaletteDragStart;

private double extensionPaletteDragStartX;

private double extensionPaletteDragStartY;

private double extensionPaletteBaseLeft;

private double extensionPaletteBaseTop;

private bool difficultyCardDragging;

private bool difficultyCardLayoutQueued;

private readonly bool initializeFactoryFloatingLayout = !File.Exists(WindowsEditorViewSettingsStore.SettingsPath);

private bool factoryFloatingLayoutPending;

private bool factoryFloatingLayoutQueued;

private bool applyingFactoryFloatingLayout;

private bool floatingPaletteClampQueued;

private Point difficultyCardDragStart;

private double difficultyCardDragStartX;

private double difficultyCardDragStartY;

private bool displayScaleSettingsDirty;

private bool editorViewSettingsDirty;

private bool displayScaleMaterializedScanQueued;

private bool libraryDeletionInProgress;

private DesktopLibraryDeletionPlan? lastLibraryDeletionPlan;

private LibraryDeleteDialogScaleDiagnostics? lastLibraryDeleteDialogScale;

private IReadOnlyList<WindowsRecycleBinOperationResult> lastLibraryRecycleResults = Array.Empty<WindowsRecycleBinOperationResult>();

private IReadOnlyList<string> lastLibraryRecycleFailures = Array.Empty<string>();

private bool lastLibraryDeletionConfirmed;

private bool lastLibraryDeletionCanceled;

private DesktopChartValidationPresentation? lastChartValidationPresentation;

private DesktopChartIssueNavigationPlan? lastChartValidationNavigation;

private ChartValidationDialogScaleDiagnostics? chartValidationManualDialogScale;

private ChartValidationDialogScaleDiagnostics? chartValidationSaveDialogScale;

private bool chartValidationManualCheckShown;

private bool chartValidationSaveBlockedShown;

private string? lastChartValidationDialogMode;

private bool chartValidationEvidenceInjected;

private bool settingProgrammaticPlaybackBeat;

private bool suppressProEditorControls;

private readonly WindowsEditorSpacePanGestureState spacePanGestureState = new WindowsEditorSpacePanGestureState();

private readonly WindowsEditorOneShotShortcutState oneShotShortcutState = new WindowsEditorOneShotShortcutState();

private readonly WindowsImeDuplicateCommitGuard imeDuplicateCommitGuard = new WindowsImeDuplicateCommitGuard();

private int persistedProGridColumns = AvaloniaAppPaths.LoadProGridColumns();

private readonly WindowsAudioTimingMapCache audioTimingMapCache = new WindowsAudioTimingMapCache();

private WindowsKeySoundSettings keySoundSettings = WindowsKeySoundSettingsStore.Load();

private WindowsDisplayScaleSettings displayScaleSettings = WindowsDisplayScaleSettingsStore.Load();

private AvaloniaLanguageSettings languageSettings = AvaloniaLanguageSettingsStore.Load();

private WindowsEditorViewSettings editorViewSettings = WindowsEditorViewSettingsStore.Load();

private WindowsEditorShortcutSettings shortcutSettings = WindowsEditorShortcutSettingsStore.Load();

private bool shortcutSettingsDirty;

private string? capturingShortcutAction;

private WindowState windowStateBeforeFullscreen;

private WindowsWindowSnapTarget? activeWindowSnapTarget;

private PixelRect? windowSnapRestoreBounds;

private DispatcherTimer? windowMoveCompletionTimer;

private int windowMoveSnapScheduleCount;

private int windowMoveSnapApplyCount;

private int windowMoveSnapNoTargetCount;

private PixelPoint? windowMoveSnapLastPointer;

private string windowMoveSnapLastOutcome = "None";

private readonly Dictionary<Guid, long> lastBackupChangeSerials = new Dictionary<Guid, long>();

private bool autoBackupInProgress;

private Task activeBackupTask = Task.CompletedTask;

private Task startupLibraryScanTask = Task.CompletedTask;

private DesktopChartTab? keySoundActiveTab;

private EditorSession? keySoundActiveSession;

private ChartDocument? keySoundObservedDocument;

private long keySoundObservedChangeSerial = -1L;

private string? keySoundObservedSourcePath;

private string? keySoundResolvedAudioPath;

private HashSet<int> keySoundObservedHiddenGroups = new HashSet<int>();

private bool keySoundPlanRebuildPending;

private bool keySoundSettingsDirty;

private bool keySoundRuntimeDisposed;

private bool editorHighRateFramePumpRunning;

private bool frameRateRuntimeReady;

private bool editorAnimationFrameRequested;

private bool editorPlaybackWasActive;

private long editorFrameUpdates;

private long editorHighRatePositionBits;

private int editorHighRateOutput;

private long editorHighRateSampleVersion;

private bool libraryVisible;

private readonly Dictionary<Guid, DifficultyTabState> difficultyStates = new Dictionary<Guid, DifficultyTabState>();

private CancellationTokenSource? difficultyAnalysisCancellation;

private Task activeDifficultyAnalysisTask = Task.CompletedTask;

private long editorSeekGeneration;

private long editorSeekTargetVersion;

private bool editorSeekPending;

private bool editorSeekCommitRunning;

private bool editorSeekUsesMusic;

private bool editorSeekResumeRequested;

private DateTimeOffset editorSeekResumeSuppressedUntilUtc;

private long editorManualNavigationPauseVersion;

private Task editorManualNavigationPauseTask = Task.CompletedTask;

private double pendingEditorSeekMilliseconds;

private DesktopChartTab? editorSeekTab;

private Task editorSeekPauseTask = Task.CompletedTask;

private Task editorSeekCommitTask = Task.CompletedTask;

private CancellationTokenSource? onlineRestoreCancellation;

private RoomChartSyncCoordinator? roomChartSyncCoordinator;

private DesktopChartTab? roomChartSyncTab;

private Guid? roomChartSyncRoomId;

private long roomChartSyncRoomNumber;

private string roomChartSyncRoomName = string.Empty;

private PreparedOnlineRoomUpload? preparedOnlineRoomUpload;

private readonly HashSet<Guid> retainedOnlineRoomTabIds = new HashSet<Guid>();

private WindowsFrozenRoomResourceRegistry? roomChartFrozenResources;

private long roomChartLocalChangeSerial;

private long roomChartObservedSessionSerial;

private bool applyingRemoteChartSync;

private readonly SemaphoreSlim roomPresenceLifecycleGate = new SemaphoreSlim(1, 1);

private CancellationTokenSource? roomPresenceLifetime;

private Task roomPresenceTask = Task.CompletedTask;

private Guid? roomPresenceRoomId;

private long roomPresenceGeneration;

private long onlineRoomBindingReconcileGeneration;

private int? onlineRoundTripLatencyMs;

private bool roomInvitationPollInProgress;

private bool roomInvitationResponseInProgress;

private OnlineRoomInvitationDto? pendingRoomInvitation;

private long followNotificationUserId;

private bool followNotificationPollInProgress;

private bool followNotificationToastVisible;

private DateTimeOffset followNotificationRetryNotBefore;

private readonly SemaphoreSlim roomChartSyncStopGate = new SemaphoreSlim(1, 1);

private string? lastRoomChartPermissionErrorCode;

private WindowsRoomAccessSnapshot? roomAccessSnapshot;

private readonly CancellationTokenSource onlineCreatorAvatarLifetime = new CancellationTokenSource();

private readonly Dictionary<string, Bitmap> onlineCreatorAvatarCache = new Dictionary<string, Bitmap>(StringComparer.Ordinal);

private readonly Dictionary<string, Task<Bitmap?>> onlineCreatorAvatarLoads = new Dictionary<string, Task<Bitmap>>(StringComparer.Ordinal);

private IReadOnlyList<OnlineRoomMemberDto> onlineCreatorMembers = Array.Empty<OnlineRoomMemberDto>();

private readonly EditorPlaybackVisualClock editorVisualClock = new EditorPlaybackVisualClock();

private readonly Stopwatch editorVisualTime = Stopwatch.StartNew();

private int editorVisualUpdatePending;

private readonly WindowsFrameRateGate editorHighRateVisualGate = new WindowsFrameRateGate(240);

private long editorStatusRenderCount;

private Guid? editorRangeTab;

private long editorRangeSerial = -1L;

private object? editorRangeDocument;

private double editorRangeEndBeat;

private readonly SemaphoreSlim editorPlaybackCommandGate = new SemaphoreSlim(1, 1);

public Control SharedContent => (Content as Control) ?? throw new InvalidOperationException("编辑器视图尚未构建。");

public void InitializeEmbedded()
{
    if (!initialized)
    {
        Window_Opened(this, EventArgs.Empty);
    }
}

public MainWindow(string[] args)
{
    factoryFloatingLayoutPending = initializeFactoryFloatingLayout || !SupportsDesktopWindowSizing;
    WindowsOnlineAccountHost windowsOnlineAccountHost = null;
    string text = null;
    try
    {
        if (SimpleManiaProduct.OnlineEnabled)
        {
            windowsOnlineAccountHost = WindowsOnlineAccountHost.Create();
            windowsOnlineAccountHost.Session.Changed += OnlineAccountSession_Changed;
            windowsOnlineAccountHost.Session.NotificationsReceived += OnlineAccountSession_NotificationsReceived;
        }
    }
    catch (Exception ex)
    {
        text = ex.Message;
    }

    onlineAccountHost = windowsOnlineAccountHost;
    onlineHostError = text;
    keySoundRuntime = new WindowsKeySoundPlaybackRuntime(audioTransport);
    keySoundRuntime.Volume = (float)keySoundSettings.Volume;
    keySoundPlanDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(50.0)
    };
    keySoundPlanDebounceTimer.Tick += (object? _, EventArgs _) =>
    {
        keySoundPlanDebounceTimer.Stop();
        RequestKeySoundPlanNow();
    };
    keySoundSettingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(350.0)
    };
    keySoundSettingsSaveTimer.Tick += (object? _, EventArgs _) =>
    {
        PersistPendingKeySoundSettings();
    };
    displayScaleSettingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(350.0)
    };
    displayScaleSettingsSaveTimer.Tick += (object? _, EventArgs _) =>
    {
        PersistPendingDisplayScaleSettings();
    };
    editorViewSettingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(350.0)
    };
    editorViewSettingsSaveTimer.Tick += (object? _, EventArgs _) =>
    {
        PersistPendingEditorViewSettings();
    };
    navigationMarkerSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(350.0)
    };
    navigationMarkerSaveTimer.Tick += async (object? _, EventArgs _) =>
    {
        await PersistPendingNavigationMarkersAsync();
    };
    settingsSnapCommitTimer = new DispatcherTimer(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(300.0)
    };
    settingsSnapCommitTimer.Tick += (object? _, EventArgs _) =>
    {
        CommitSettingsSnapBoxText(restoreInvalidText: false, addCustomDivisionItem: false);
    };
    settingsFixedSnapCommitTimer = new DispatcherTimer(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(300.0)
    };
    settingsFixedSnapCommitTimer.Tick += (object? _, EventArgs _) =>
    {
        CommitSettingsFixedSnapBoxText(restoreInvalidText: false, addCustomDivisionItem: false);
    };
    shortcutSettingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(350.0)
    };
    shortcutSettingsSaveTimer.Tick += (object? _, EventArgs _) =>
    {
        PersistPendingShortcutSettings();
    };
    audioPanelSettingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(350.0)
    };
    audioPanelSettingsSaveTimer.Tick += (object? _, EventArgs _) =>
    {
        PersistPendingAudioPanelSettings();
    };
    editorToastTimer.Tick += (object? _, EventArgs _) =>
    {
        HideEditorToast();
    };
    autoBackupTimer = new DispatcherTimer(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMinutes(editorViewSettings.AutoBackupMinutes)
    };
    autoBackupTimer.Tick += AutoBackupTimer_Tick;
    roomInvitationTimer = new DispatcherTimer(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(2.0)
    };
    roomInvitationTimer.Tick += async (object? _, EventArgs _) =>
    {
        await PollRoomInvitationsAsync();
    };
    friendInviteCooldownTimer.Tick += (object? _, EventArgs _) =>
    {
        RefreshFriendInviteCooldownButtons();
    };
    followNotificationTimer.Tick += async (object? _, EventArgs _) =>
    {
        await PollFollowNotificationsAsync();
    };
    chartingHeartbeatTimer.Tick += ChartingHeartbeatTimer_Tick;
    followNotificationToastTimer.Tick += (object? _, EventArgs _) =>
    {
        AdvanceFollowNotificationToast();
    };
    fullscreenTitleHideTimer.Tick += FullscreenTitleHideTimer_Tick;
    fullscreenTitleAnimationTimer.Tick += FullscreenTitleAnimationTimer_Tick;
    suppressKeySoundControls = true;
    string text2 = ParseArgument(args, "--evidence-chart");
    evidenceChartMode = !string.IsNullOrWhiteSpace(text2);
    string text3 = ParseArgument(args, "--evidence-second-chart");
    if (!string.IsNullOrWhiteSpace(text3) && !evidenceChartMode)
    {
        throw new InvalidOperationException("A second evidence chart requires the primary direct evidence chart.");
    }

    IReadOnlyList<string> readOnlyList;
    if (string.IsNullOrWhiteSpace(text2))
    {
        readOnlyList = WindowsSingleInstancePolicy.ExtractSupportedPaths(args);
    }
    else if (!string.IsNullOrWhiteSpace(text3))
    {
        IReadOnlyList<string> readOnlyList2 = new string[2] { System.IO.Path.GetFullPath(text2), System.IO.Path.GetFullPath(text3) };
        readOnlyList = readOnlyList2;
    }
    else
    {
        IReadOnlyList<string> readOnlyList2 = new string[]
{
    System.IO.Path.GetFullPath(text2)
};
        readOnlyList = readOnlyList2;
    }

    startupChartPaths = readOnlyList;
    evidenceShowOsuImportDialog = args.Contains("--evidence-osu-import-dialog", StringComparer.OrdinalIgnoreCase);
    if (evidenceShowOsuImportDialog && (!evidenceChartMode || startupChartPaths.Count != 1 || !System.IO.Path.GetExtension(startupChartPaths[0]).Equals(".osu", StringComparison.OrdinalIgnoreCase)))
    {
        throw new InvalidOperationException("Evidence osu import-dialog mode requires one direct .osu evidence chart.");
    }

    libraryVisible = startupChartPaths.Count == 0;
    evidenceOutputPath = ParseArgument(args, "--evidence-output");
    evidenceInteractionOutputPath = ParseArgument(args, "--evidence-interaction-output");
    evidenceSyntheticNoteCount = ParsePositiveIntArgument(args, "--evidence-synthetic-notes");
    int num = ParsePositiveIntArgument(args, "--evidence-interaction-delay-ms");
    evidenceInteractionDelayMilliseconds = ((num == 0) ? 1800 : Math.Clamp(num, 1000, 60000));
    evidenceFocusTiming = args.Contains("--evidence-focus-timing", StringComparer.OrdinalIgnoreCase);
    evidenceFocusHoldTail = args.Contains("--evidence-focus-hold-tail", StringComparer.OrdinalIgnoreCase);
    evidenceFocusNoteProperty = args.Contains("--evidence-focus-note-property", StringComparer.OrdinalIgnoreCase);
    evidenceFocusGroup = args.Contains("--evidence-focus-group", StringComparer.OrdinalIgnoreCase);
    evidenceFocusProEditor = args.Contains("--evidence-focus-proeditor", StringComparer.OrdinalIgnoreCase);
    evidenceFocusKeySound = args.Contains("--evidence-focus-key-sound", StringComparer.OrdinalIgnoreCase);
    evidenceFocusAudioTransport = args.Contains("--evidence-focus-audio-transport", StringComparer.OrdinalIgnoreCase);
    evidenceFocusDisplayScale = args.Contains("--evidence-focus-display-scale", StringComparer.OrdinalIgnoreCase);
    evidenceFocusSettingsCache = args.Contains("--evidence-focus-settings-cache", StringComparer.OrdinalIgnoreCase);
    evidenceFocusAudioPanels = args.Contains("--evidence-focus-audio-panels", StringComparer.OrdinalIgnoreCase);
    evidenceFocusLibraryDeletion = args.Contains("--evidence-focus-library-deletion", StringComparer.OrdinalIgnoreCase);
    evidenceFocusLibraryLauncher = args.Contains("--evidence-focus-library-launcher", StringComparer.OrdinalIgnoreCase);
    bool flag = args.Contains("--evidence-use-persisted-workspace-root", StringComparer.OrdinalIgnoreCase);
    if (flag && !evidenceFocusLibraryLauncher)
    {
        throw new InvalidOperationException("Persisted-workspace evidence requires library-launcher evidence mode.");
    }

    evidenceFocusChartValidation = args.Contains("--evidence-focus-chart-validation", StringComparer.OrdinalIgnoreCase);
    evidenceFocusTitleBarAnimation = args.Contains("--evidence-focus-titlebar-animation", StringComparer.OrdinalIgnoreCase);
    evidenceFocusLibraryVirtualization = args.Contains("--evidence-focus-library-virtualization", StringComparer.OrdinalIgnoreCase);
    if (evidenceFocusLibraryVirtualization)
    {
        if (evidenceChartMode)
        {
            throw new InvalidOperationException("Library virtualization evidence cannot open an editor chart.");
        }

        if (string.IsNullOrWhiteSpace(evidenceOutputPath))
        {
            throw new InvalidOperationException("Library virtualization evidence requires --evidence-output.");
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LUMACHART_AVALONIA_LOCAL_DATA_ROOT")) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LUMACHART_AVALONIA_WORKSPACE_ROOT")))
        {
            throw new InvalidOperationException("Library virtualization evidence requires isolated local-data and workspace roots.");
        }
    }

    evidenceSuppressExportReveal = args.Contains("--evidence-suppress-export-reveal", StringComparer.OrdinalIgnoreCase);
    evidenceFollowActiveTab = args.Contains("--evidence-follow-active-tab", StringComparer.OrdinalIgnoreCase);
    evidenceAutoBackupDelayMilliseconds = ParsePositiveIntArgument(args, "--evidence-auto-backup-delay-ms");
    if (evidenceAutoBackupDelayMilliseconds > 5000)
    {
        throw new ArgumentOutOfRangeException("evidenceAutoBackupDelayMilliseconds", "Evidence automatic-backup delay must be at most 5000 ms.");
    }

    evidenceAudioPermissionRevokeDelayMilliseconds = ParsePositiveIntArgument(args, "--evidence-audio-permission-revoke-delay-ms");
    if (evidenceAudioPermissionRevokeDelayMilliseconds > 5000)
    {
        throw new ArgumentOutOfRangeException("evidenceAudioPermissionRevokeDelayMilliseconds", "Evidence audio-permission revoke delay must be at most 5000 ms.");
    }

    if (evidenceAudioPermissionRevokeDelayMilliseconds > 0 && !evidenceChartMode)
    {
        throw new InvalidOperationException("Evidence audio-permission revocation requires a direct evidence chart.");
    }

    evidenceAudioPreCommitDelayMilliseconds = ParsePositiveIntArgument(args, "--evidence-audio-pre-commit-delay-ms");
    if (evidenceAudioPreCommitDelayMilliseconds > 5000)
    {
        throw new ArgumentOutOfRangeException("evidenceAudioPreCommitDelayMilliseconds", "Evidence audio pre-commit delay must be at most 5000 ms.");
    }

    if (evidenceAudioPreCommitDelayMilliseconds > 0 && !evidenceChartMode)
    {
        throw new InvalidOperationException("Evidence audio pre-commit delay requires a direct evidence chart.");
    }

    string text4 = ParseArgument(args, "--evidence-file-picker-start-directory");
    evidenceFilePickerStartDirectory = (((evidenceChartMode || evidenceFocusLibraryLauncher) && !string.IsNullOrWhiteSpace(text4)) ? System.IO.Path.GetFullPath(text4) : null);
    if (evidenceFilePickerStartDirectory != null && !Directory.Exists(evidenceFilePickerStartDirectory))
    {
        throw new DirectoryNotFoundException("Evidence file-picker start directory does not exist: " + evidenceFilePickerStartDirectory);
    }

    if (evidenceFocusLibraryLauncher)
    {
        string? environmentVariable = Environment.GetEnvironmentVariable("LUMACHART_AVALONIA_LOCAL_DATA_ROOT");
        string environmentVariable2 = Environment.GetEnvironmentVariable("LUMACHART_AVALONIA_EDITING_ROOT");
        string environmentVariable3 = Environment.GetEnvironmentVariable("LUMACHART_AVALONIA_WORKSPACE_ROOT");
        if (string.IsNullOrWhiteSpace(environmentVariable) || string.IsNullOrWhiteSpace(environmentVariable2) || (!flag && string.IsNullOrWhiteSpace(environmentVariable3)))
        {
            throw new InvalidOperationException("Library launcher evidence requires isolated local-data, editing and workspace roots.");
        }

        if (flag && !string.IsNullOrWhiteSpace(environmentVariable3))
        {
            throw new InvalidOperationException("Persisted-workspace evidence must omit the workspace-root override.");
        }

        if (flag && !File.Exists(AvaloniaAppPaths.WorkspaceSettingsPath))
        {
            throw new InvalidOperationException("Persisted-workspace evidence requires an isolated workspace.json.");
        }
    }

    evidenceUsePlacementProbeViewport = args.Contains("--evidence-placement-probe-viewport", StringComparer.OrdinalIgnoreCase);
    evidenceFocusNotePropertyBeat = (BeatPosition.TryParse(ParseArgument(args, "--evidence-focus-note-property-beat"), out var position) ? new BeatPosition? (position) : ((BeatPosition? )null));
    evidenceFocusGroupBeat = (BeatPosition.TryParse(ParseArgument(args, "--evidence-focus-group-beat"), out var position2) ? new BeatPosition? (position2) : ((BeatPosition? )null));
    evidenceEnableBackgroundWaveform = args.Contains("--evidence-background-waveform", StringComparer.OrdinalIgnoreCase);
    evidenceEnableBackgroundSpectrogram = args.Contains("--evidence-background-spectrogram", StringComparer.OrdinalIgnoreCase);
    if (evidenceEnableBackgroundWaveform)
    {
        audioPanelSettings = audioPanelSettings.WithWaveform(audioPanelSettings.Waveform.WithMode(WindowsAudioPanelDisplayMode.Background).WithCollapsed(collapsed: false));
    }

    if (evidenceEnableBackgroundSpectrogram)
    {
        audioPanelSettings = audioPanelSettings.WithSpectrogram(audioPanelSettings.Spectrogram.WithMode(WindowsAudioPanelDisplayMode.Background).WithCollapsed(collapsed: false));
    }

    Title = "SimpleManiaEditor";
    InitializeSimpleMania();
    TryApplyApplicationIcon();
    ConfigureInitialWindowBounds(1720.0, 960.0, 1080.0, 680.0);
    Background = new SolidColorBrush(WindowColor);
    FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI");
    FontSize = 12.0;
    WindowDecorations = WindowDecorations.None;
    CanResize = true;
    AddHandler(InputElement.PointerPressedEvent, WindowResize_PointerPressed, RoutingStrategies.Tunnel);
    WindowStartupLocation = WindowStartupLocation.CenterScreen;
    localizationController = new AvaloniaLocalizationController(this, languageSettings.Preference);
    AvaloniaLocalizationRuntime.SetPreference(languageSettings.Preference);
    displayScaleRoot.Child = BuildShell();
    Content = displayScaleRoot;
    ConfigureEditorOptionControls();
    ConfigureBlankClickFocus();
    fontScaleController = new AvaloniaWindowFontScaleController(this);
    localizationController.Apply();
    displayScaleController = new WindowsDisplayScaleController(this, displayScaleRoot, fontScaleController, displayScaleSettings);
    popupScaleController = new AvaloniaPopupScaleController(() => displayScaleController.CurrentSettings);
    PositionChanged += (object? _, PixelPointEventArgs _) =>
    {
        RefreshMonitorUiScale();
    };
    PropertyChanged += (object? _, AvaloniaPropertyChangedEventArgs e) =>
    {
        if (e.Property.Name == "RenderScaling")
        {
            RefreshMonitorUiScale();
        }
    };
    libraryList.ContextMenu = CreateLibraryFolderContextMenu();
    displayScaleSettings = displayScaleController.CurrentSettings;
    RefreshDisplayScaleSettingsUi();
    ApplyEditorViewSettings();
    audioTransport.SetVolumeAsync((float)editorViewSettings.MusicVolume);
    audioTransport.SetPlaybackRateAsync(editorViewSettings.PlaybackRate);
    audioTransport.SetPreservePitchAsync(editorViewSettings.PreservePitch);
    timeline.SetProGridColumnCount(persistedProGridColumns);
    suppressKeySoundControls = false;
    RefreshKeySoundSettingsUi();
    UpdateAudioUi();
    viewModel.StateChanged += ViewModel_StateChanged;
    audioTransport.StateChanged += AudioTransport_StateChanged;
    audioVisualization.StateChanged += AudioVisualization_StateChanged;
    keySoundRuntime.StateChanged += KeySoundRuntime_StateChanged;
    RegisterPasteSurface(timeline);
    timeline.ZoomChanged += (object? _, double _) =>
    {
        SyncTrackViewports();
    };
    timeline.CurrentBeatChanged += Timeline_CurrentBeatChanged;
    timeline.ManualNavigationRequested += Timeline_ManualNavigationRequested;
    timeline.SnapDivisionChangeRequested += (object? _, int direction) =>
    {
        AdjustQuickSnap(direction);
    };
    timeline.PixelsPerBeatChanged += Timeline_PixelsPerBeatChanged;
    timeline.SelectionChanged += (object? _, EventArgs _) =>
    {
        UpdateSelectionUi();
    };
    timeline.TimingSelectionChanged += Timeline_TimingSelectionChanged;
    timeline.ChartIssuesChanged += (object? _, EventArgs _) =>
    {
        editorOverview.SetIssues(timeline.ChartIssues);
    };
    timeline.StatusChanged += (object? _, string status) =>
    {
        viewModel.SetStatus(status);
    };
    timeline.HoverChanged += (object? _, string status) =>
    {
        statusText.Text = status;
    };
    timeline.EditPermissionDenied += (object? _, string category) =>
    {
        ShowRoomPermissionNotice(WindowsRoomPermissionNoticePolicy.Denied(category));
    };
    timeline.TrackPanChanged += Timeline_TrackPanChanged;
    timeline.SizeChanged += (object? _, SizeChangedEventArgs _) =>
    {
        RefreshProEditorControls();
        QueueAudioPanelLayout();
        QueueDifficultyCardLayout();
        QueueFactoryFloatingLayout();
    };
    timelineHost.SizeChanged += (object? _, SizeChangedEventArgs _) =>
    {
        QueueAudioPanelLayout(clampFreePanels: true);
        QueueDifficultyCardLayout();
    };
    editorWorkbench.SizeChanged += (object? _, SizeChangedEventArgs _) =>
    {
        QueueFloatingPaletteClamp();
    };
    Opened += Window_Opened;
    Opened += async (object? _, EventArgs _) =>
    {
        IOfficialPlaytestProcess processRunner = OfficialPlaytestLauncher.ProcessRunner;
        if (processRunner is OfficialPlaytestProcess process)
        {
            try
            {
                officialPlaytestWarmStatus.Text = "正在后台准备试玩，编辑不受影响…";
                await process.PrewarmAsync();
                officialPlaytestWarmStatus.Text = (process.ProcessId.HasValue ? "试玩已预热就绪" : "试玩组件会在完整便携包中预热");
            }
            catch (Exception)
            {
                officialPlaytestWarmStatus.Text = "预热未完成，点击试玩时重试";
            }
        }
    };
    Closed += async (object? _, EventArgs _) =>
    {
        viewModel.StopLibraryWatching();
        if (OfficialPlaytestLauncher.ProcessRunner is OfficialPlaytestProcess officialPlaytestProcess)
        {
            await officialPlaytestProcess.ShutdownAsync();
        }
    };
    Activated += (object? _, EventArgs _) =>
    {
        RefreshFollowSystemLanguage();
        if (roomChatHasUnread && IsRoomChatActivelyVisible())
        {
            SetRoomChatUnread(unread: false);
        }
    };
    Deactivated += (object? _, EventArgs _) =>
    {
        oneShotShortcutState.Clear();
        imeDuplicateCommitGuard.Reset();
        if (spacePanGestureState.Cancel())
        {
            timeline.EndSpacePanModifier();
        }
    };
    Closing += Window_Closing;
    AddHandler(InputElement.KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    AddHandler(InputElement.KeyUpEvent, Window_KeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
    AddHandler(InputElement.TextInputEvent, Window_TextInput, RoutingStrategies.Tunnel, handledEventsToo: true);
    DragDrop.SetAllowDrop(this, value: true);
    DragDrop.AddDragOverHandler(this, Window_DragOver);
    DragDrop.AddDropHandler(this, Window_Drop);
    audioUiTimer = new DispatcherTimer(DispatcherPriority.Render)
    {
        Interval = WindowsEditorFrameInterval(EffectiveFrameRateLimit())
    };
    audioUiTimer.Tick += AudioUiTimer_Tick;
    frameRateRuntimeReady = true;
    RefreshFrameRateStatus(isPlaying: false);
    proGridColumnSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(350.0)
    };
    proGridColumnSaveTimer.Tick += (object? _, EventArgs _) =>
    {
        PersistPendingProGridColumns();
    };
}

private Control BuildShell()
{
    Grid grid = shellRoot;
    grid.RowDefinitions = new RowDefinitions($"{38.0},*");
    grid.Children.Add(BuildTitleBar());
    if (SupportsDesktopWindowSizing)
    {
        fullscreenTitleRevealHotZone.Height = 4.0;
        fullscreenTitleRevealHotZone.VerticalAlignment = VerticalAlignment.Top;
        fullscreenTitleRevealHotZone.Background = Brushes.Transparent;
        fullscreenTitleRevealHotZone.IsVisible = false;
        fullscreenTitleRevealHotZone.PointerEntered += (object? _, PointerEventArgs _) =>
        {
            SetFullscreenTitleBarRevealed(revealed: true);
        };
        AutomationProperties.SetAutomationId(fullscreenTitleRevealHotZone, "Title.FullscreenRevealHotZone");
        Grid.SetRowSpan(fullscreenTitleRevealHotZone, 3);
        fullscreenTitleRevealHotZone.SetValue(Visual.ZIndexProperty, 210);
        grid.Children.Add(fullscreenTitleRevealHotZone);
    }

    Grid grid2 = new Grid
    {
        Background = new SolidColorBrush(WindowColor)
    };
    Grid.SetRow(grid2, 1);
    Grid grid3 = new Grid();
    BuildMapperLibraryBrowser();
    BuildEditorView();
    AutomationProperties.SetAutomationId(libraryView, "Library.Surface");
    AutomationProperties.SetAutomationId(editorView, "Editor.Surface");
    grid3.Children.Add(libraryView);
    grid3.Children.Add(editorView);
    grid2.Children.Add(grid3);
    grid.Children.Add(grid2);
    statusText.Foreground = MutedBrush;
    statusText.FontSize = 12.0;
    statusText.VerticalAlignment = VerticalAlignment.Center;
    statusText.Margin = new Thickness(14.0, 0.0);
    AutomationProperties.SetAutomationId(statusText, "Editor.Status");
    frameRateStatusText.Foreground = MutedBrush;
    frameRateStatusText.FontSize = 11.0;
    frameRateStatusText.FontFamily = new FontFamily("Cascadia Mono,Consolas");
    frameRateStatusText.VerticalAlignment = VerticalAlignment.Center;
    frameRateStatusText.HorizontalAlignment = HorizontalAlignment.Right;
    frameRateStatusText.Margin = new Thickness(14.0, 0.0);
    frameRateStatusText.Text = "空闲 · 播放时测量帧率";
    AutomationProperties.SetAutomationId(frameRateStatusText, "Editor.FrameRateStatus");
    Grid grid4 = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto")
    };
    grid4.Children.Add(statusText);
    Grid.SetColumn(frameRateStatusText, 1);
    grid4.Children.Add(frameRateStatusText);
    statusBar.Height = 20.0;
    if (!SupportsDesktopWindowSizing)
    {
        grid2.Margin = new Thickness(0.0, 0.0, 0.0, statusBar.Height);
    }

    statusBar.Background = new SolidColorBrush(Color.Parse("#D9171A1D"));
    statusBar.BorderBrush = StrokeBrush;
    statusBar.BorderThickness = new Thickness(0.0, 1.0, 0.0, 0.0);
    statusBar.VerticalAlignment = VerticalAlignment.Bottom;
    statusBar.IsHitTestVisible = false;
    statusBar.Child = grid4;
    Grid.SetRow(statusBar, 1);
    statusBar.SetValue(Visual.ZIndexProperty, 18);
    grid.Children.Add(statusBar);
    Control control = BuildOnlineToastStack();
    Grid.SetRowSpan(control, 3);
    control.SetValue(Visual.ZIndexProperty, 1000);
    grid.Children.Add(control);
    Control control2 = BuildEditorToast();
    Grid.SetRowSpan(control2, 3);
    control2.SetValue(Visual.ZIndexProperty, 1001);
    grid.Children.Add(control2);
    Control control3 = BuildRoomPermissionNotice();
    Grid.SetRow(control3, 1);
    Grid.SetRowSpan(control3, 2);
    control3.SetValue(Visual.ZIndexProperty, 1100);
    grid.Children.Add(control3);
    Control control4 = BuildFriendsPanel();
    Grid.SetRowSpan(control4, 3);
    control4.SetValue(Visual.ZIndexProperty, 850);
    grid.Children.Add(control4);
    Control control5 = BuildOnlinePlayerPopupLayer();
    Grid.SetRow(control5, 1);
    Grid.SetRowSpan(control5, 2);
    control5.SetValue(Visual.ZIndexProperty, 1050);
    grid.Children.Add(control5);
    Control control6 = BuildAudioImportNameLayer();
    Grid.SetRow(control6, 1);
    Grid.SetRowSpan(control6, 2);
    control6.SetValue(Visual.ZIndexProperty, 950);
    grid.Children.Add(control6);
    Grid.SetRow(onlineSurfaceLayer, 1);
    Grid.SetRowSpan(onlineSurfaceLayer, 2);
    onlineSurfaceLayer.SetValue(Visual.ZIndexProperty, 900);
    AutomationProperties.SetAutomationId(onlineSurfaceLayer, "Online.SurfaceLayer");
    onlineSurfaceLayer.Child = onlineSurfaceContent;
    grid.Children.Add(onlineSurfaceLayer);
    Control control7 = BuildRoomUploadProgressPopup();
    Grid.SetRow(control7, 1);
    Grid.SetRowSpan(control7, 2);
    control7.SetValue(Visual.ZIndexProperty, 1300);
    grid.Children.Add(control7);
    Control control8 = BuildRoomDownloadProgressLayer();
    Grid.SetRow(control8, 1);
    Grid.SetRowSpan(control8, 2);
    control8.SetValue(Visual.ZIndexProperty, 1301);
    grid.Children.Add(control8);
    Control control9 = BuildAboutLayer();
    Grid.SetRow(control9, 1);
    Grid.SetRowSpan(control9, 2);
    control9.SetValue(Visual.ZIndexProperty, 1150);
    grid.Children.Add(control9);
    Control control10 = BuildUpdateLayer();
    Grid.SetRow(control10, 1);
    Grid.SetRowSpan(control10, 2);
    control10.SetValue(Visual.ZIndexProperty, 1200);
    grid.Children.Add(control10);
    return grid;
}

private Control BuildRoomUploadProgressPopup()
{
    roomUploadProgressTitleText.Text = "正在创建联机房间";
    roomUploadProgressTitleText.FontSize = 18.0;
    roomUploadProgressTitleText.FontWeight = FontWeight.DemiBold;
    roomUploadProgressTitleText.Foreground = new SolidColorBrush(Color.Parse("#E4E9EA"));
    roomUploadProgressTitleText.TextTrimming = TextTrimming.CharacterEllipsis;
    AutomationProperties.SetAutomationId(roomUploadProgressTitleText, "RoomUpload.Progress.Title");
    roomUploadProgressDetailText.Foreground = new SolidColorBrush(Color.Parse("#93A0A4"));
    roomUploadProgressDetailText.FontSize = 11.0;
    roomUploadProgressDetailText.Margin = new Thickness(0.0, 7.0, 0.0, 12.0);
    roomUploadProgressDetailText.TextTrimming = TextTrimming.CharacterEllipsis;
    AutomationProperties.SetAutomationId(roomUploadProgressDetailText, "RoomUpload.Progress.Detail");
    roomUploadProgressBar.Minimum = 0.0;
    roomUploadProgressBar.Maximum = 100.0;
    roomUploadProgressBar.Height = 9.0;
    roomUploadProgressBar.HorizontalAlignment = HorizontalAlignment.Stretch;
    roomUploadProgressBar.Foreground = new SolidColorBrush(Color.Parse("#6FA1A5"));
    roomUploadProgressBar.Background = new SolidColorBrush(Color.Parse("#252B2E"));
    AutomationProperties.SetAutomationId(roomUploadProgressBar, "RoomUpload.Progress.Bar");
    roomUploadProgressValueText.Foreground = new SolidColorBrush(Color.Parse("#A8B5B7"));
    roomUploadProgressValueText.FontFamily = new FontFamily("Cascadia Mono,Consolas");
    roomUploadProgressValueText.FontSize = 10.0;
    roomUploadProgressValueText.Margin = new Thickness(0.0, 8.0, 0.0, 0.0);
    roomUploadProgressValueText.HorizontalAlignment = HorizontalAlignment.Right;
    AutomationProperties.SetAutomationId(roomUploadProgressValueText, "RoomUpload.Progress.Value");
    roomUploadProgressPopup.Width = 430.0;
    roomUploadProgressPopup.MaxWidth = 430.0;
    roomUploadProgressPopup.Padding = new Thickness(22.0, 19.0);
    roomUploadProgressPopup.HorizontalAlignment = HorizontalAlignment.Center;
    roomUploadProgressPopup.VerticalAlignment = VerticalAlignment.Center;
    roomUploadProgressPopup.Background = new SolidColorBrush(Color.Parse("#FC1A1E21"));
    roomUploadProgressPopup.BorderBrush = new SolidColorBrush(Color.Parse("#60777B"));
    roomUploadProgressPopup.BorderThickness = new Thickness(1.0);
    roomUploadProgressPopup.CornerRadius = new CornerRadius(6.0);
    roomUploadProgressPopup.BoxShadow = new BoxShadows(new BoxShadow { Blur = 24.0, OffsetY = 8.0, Color = Color.Parse("#A0000000") });
    roomUploadProgressPopup.IsVisible = false;
    roomUploadProgressPopup.IsHitTestVisible = false;
    roomUploadProgressPopup.Child = new StackPanel
    {
        Children =
        {
            (Control)roomUploadProgressTitleText,
            (Control)roomUploadProgressDetailText,
            (Control)roomUploadProgressBar,
            (Control)roomUploadProgressValueText
        }
    };
    AutomationProperties.SetAutomationId(roomUploadProgressPopup, "RoomUpload.Progress.Popup");
    AutomationProperties.SetName(roomUploadProgressPopup, "创建房间资源上传进度");
    return roomUploadProgressPopup;
}

private void ShowRoomUploadProgress(Guid roomId, string roomName, WindowsOnlineRoomUploadPackage package)
{
    roomUploadProgressRoomId = roomId;
    roomUploadProgressShownUtc = DateTimeOffset.UtcNow;
    roomUploadProgressTitleText.Text = AvaloniaLocalizationCatalog.Format("正在创建“{0}”", roomName);
    roomUploadProgressDetailText.Text = AvaloniaLocalizationCatalog.Format("准备上传 {0:N0} 个文件", package.Files.Count);
    roomUploadProgressBar.Value = 0.0;
    roomUploadProgressValueText.Text = "0%  ·  0 B / " + FormatTransferBytes(package.TotalBytes);
    roomUploadProgressPopup.IsVisible = true;
    AutomationProperties.SetHelpText(roomUploadProgressPopup, $"RoomId={roomId}; Visible=True; Progress=0; Files={package.Files.Count}; TotalBytes={package.TotalBytes}");
}

private void UpdateRoomUploadProgress(Guid roomId, WindowsOnlineRoomTransferProgress progress)
{
    if (!(roomUploadProgressRoomId != roomId))
    {
        double value = Math.Clamp(progress.Fraction * 100.0, 0.0, 100.0);
        roomUploadProgressBar.Value = value;
        roomUploadProgressDetailText.Text = (string.IsNullOrWhiteSpace(progress.CurrentPath) ? "正在上传房间资源" : progress.CurrentPath);
        roomUploadProgressValueText.Text = $"{value:0}%  ·  {FormatTransferBytes(progress.TransferredBytes)} / " + FormatTransferBytes(progress.TotalBytes);
        AutomationProperties.SetHelpText(roomUploadProgressPopup, $"RoomId={roomId}; Visible=True; Progress={value:R}; TransferredBytes={progress.TransferredBytes}; TotalBytes={progress.TotalBytes}; CurrentPath={progress.CurrentPath}");
    }
}

private async Task HideRoomUploadProgressAsync(Guid roomId, bool succeeded)
{
    if (!(roomUploadProgressRoomId != roomId))
    {
        if (succeeded)
        {
            roomUploadProgressBar.Value = 100.0;
            roomUploadProgressDetailText.Text = "资源上传完成，正在进入房间";
            roomUploadProgressValueText.Text = "100%  ·  已完成";
        }

        TimeSpan timeSpan = (succeeded ? TimeSpan.FromMilliseconds(650.0) : TimeSpan.FromMilliseconds(900.0)) - (DateTimeOffset.UtcNow - roomUploadProgressShownUtc);
        if (timeSpan > TimeSpan.Zero)
        {
            await Task.Delay(timeSpan);
        }

        if (!(roomUploadProgressRoomId != roomId))
        {
            roomUploadProgressPopup.IsVisible = false;
            roomUploadProgressRoomId = null;
            AutomationProperties.SetHelpText(roomUploadProgressPopup, $"RoomId={roomId}; Visible=False; Succeeded={succeeded}");
        }
    }
}

private Control BuildRoomDownloadProgressLayer()
{
    roomDownloadProgressTitleText.Text = "正在进入联机房间";
    roomDownloadProgressTitleText.FontSize = 18.0;
    roomDownloadProgressTitleText.FontWeight = FontWeight.DemiBold;
    roomDownloadProgressTitleText.Foreground = new SolidColorBrush(Color.Parse("#E3E9EA"));
    roomDownloadProgressTitleText.TextTrimming = TextTrimming.CharacterEllipsis;
    AutomationProperties.SetAutomationId(roomDownloadProgressTitleText, "RoomDownload.Progress.Title");
    roomDownloadProgressDetailText.Foreground = new SolidColorBrush(Color.Parse("#ABB5B8"));
    roomDownloadProgressDetailText.FontSize = 11.0;
    roomDownloadProgressDetailText.Margin = new Thickness(0.0, 8.0, 0.0, 13.0);
    roomDownloadProgressDetailText.TextTrimming = TextTrimming.CharacterEllipsis;
    AutomationProperties.SetAutomationId(roomDownloadProgressDetailText, "RoomDownload.Progress.Detail");
    roomDownloadProgressBar.Minimum = 0.0;
    roomDownloadProgressBar.Maximum = 100.0;
    roomDownloadProgressBar.Height = 9.0;
    roomDownloadProgressBar.HorizontalAlignment = HorizontalAlignment.Stretch;
    roomDownloadProgressBar.Foreground = new SolidColorBrush(Color.Parse("#38676C"));
    roomDownloadProgressBar.Background = new SolidColorBrush(Color.Parse("#252B2E"));
    AutomationProperties.SetAutomationId(roomDownloadProgressBar, "RoomDownload.Progress.Bar");
    roomDownloadProgressValueText.Foreground = new SolidColorBrush(Color.Parse("#9DB7B9"));
    roomDownloadProgressValueText.FontFamily = new FontFamily("Cascadia Mono,Consolas");
    roomDownloadProgressValueText.FontSize = 10.0;
    roomDownloadProgressValueText.Margin = new Thickness(0.0, 8.0, 0.0, 0.0);
    roomDownloadProgressValueText.HorizontalAlignment = HorizontalAlignment.Right;
    AutomationProperties.SetAutomationId(roomDownloadProgressValueText, "RoomDownload.Progress.Value");
    Border child = new Border
    {
        Width = 430.0,
        MaxWidth = 430.0,
        Padding = new Thickness(22.0, 19.0),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.Parse("#1A2023")),
        BorderBrush = new SolidColorBrush(Color.Parse("#557075")),
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(7.0),
        BoxShadow = new BoxShadows(new BoxShadow { Blur = 24.0, OffsetY = 8.0, Color = Color.Parse("#A0000000") }),
        Child = new StackPanel
        {
            Children =
            {
                (Control)roomDownloadProgressTitleText,
                (Control)roomDownloadProgressDetailText,
                (Control)roomDownloadProgressBar,
                (Control)roomDownloadProgressValueText
            }
        }
    };
    roomDownloadProgressLayer.HorizontalAlignment = HorizontalAlignment.Stretch;
    roomDownloadProgressLayer.VerticalAlignment = VerticalAlignment.Stretch;
    roomDownloadProgressLayer.Padding = new Thickness(18.0);
    roomDownloadProgressLayer.Background = new SolidColorBrush(Color.Parse("#A8111416"));
    roomDownloadProgressLayer.IsVisible = false;
    roomDownloadProgressLayer.Child = child;
    AutomationProperties.SetAutomationId(roomDownloadProgressLayer, "RoomDownload.Progress.Layer");
    AutomationProperties.SetName(roomDownloadProgressLayer, "下载并进入房间进度");
    return roomDownloadProgressLayer;
}

private void ShowRoomDownloadProgress(Guid roomId, string roomName, WindowsOnlineRoomResourceCacheStatus cacheStatus)
{
    roomDownloadProgressRoomId = roomId;
    roomDownloadProgressShownUtc = DateTimeOffset.UtcNow;
    roomDownloadProgressTitleText.Text = AvaloniaLocalizationCatalog.Format("正在进入“{0}”", roomName);
    roomDownloadProgressBar.IsIndeterminate = cacheStatus.MissingBytes == 0;
    roomDownloadProgressBar.Value = 0.0;
    roomDownloadProgressDetailText.Text = ((cacheStatus.MissingBytes == 0L) ? AvaloniaLocalizationCatalog.Format("已复用 {0:N0} 个资源，正在生成编辑工作区", cacheStatus.ReusedCount) : AvaloniaLocalizationCatalog.Format("正在下载缺少的房间资源，共 {0}", FormatTransferBytes(cacheStatus.MissingBytes)));
    roomDownloadProgressValueText.Text = ((cacheStatus.MissingBytes == 0L) ? AvaloniaLocalizationCatalog.Format("本地缓存已校验  ·  {0}", FormatTransferBytes(cacheStatus.TotalBytes)) : ("0%  ·  0 B / " + FormatTransferBytes(cacheStatus.MissingBytes)));
    roomDownloadProgressLayer.IsVisible = true;
    AutomationProperties.SetHelpText(roomDownloadProgressLayer, $"RoomId={roomId}; Visible=True; MissingBytes={cacheStatus.MissingBytes}; TotalBytes={cacheStatus.TotalBytes}; ReusedCount={cacheStatus.ReusedCount}");
}

private void UpdateRoomDownloadProgress(Guid roomId, WindowsOnlineRoomTransferProgress progress)
{
    if (!(roomDownloadProgressRoomId != roomId))
    {
        double value = Math.Clamp(progress.Fraction * 100.0, 0.0, 100.0);
        roomDownloadProgressBar.IsIndeterminate = progress.TotalBytes <= 0;
        roomDownloadProgressBar.Value = value;
        roomDownloadProgressDetailText.Text = (string.IsNullOrWhiteSpace(progress.CurrentPath) ? "正在下载房间资源" : progress.CurrentPath);
        roomDownloadProgressValueText.Text = ((progress.TotalBytes <= 0) ? "正在复用本地缓存" : ($"{value:0}%  ·  {FormatTransferBytes(progress.TransferredBytes)} / " + FormatTransferBytes(progress.TotalBytes)));
        AutomationProperties.SetHelpText(roomDownloadProgressLayer, $"RoomId={roomId}; Visible=True; Progress={value:R}; TransferredBytes={progress.TransferredBytes}; TotalBytes={progress.TotalBytes}; CurrentPath={progress.CurrentPath}");
    }
}

private void SetRoomDownloadProgressStage(Guid roomId, string detail, string value)
{
    if (!(roomDownloadProgressRoomId != roomId))
    {
        roomDownloadProgressBar.IsIndeterminate = false;
        roomDownloadProgressBar.Value = 100.0;
        roomDownloadProgressDetailText.Text = detail;
        roomDownloadProgressValueText.Text = value;
    }
}

private async Task HideRoomDownloadProgressAsync(Guid roomId, bool succeeded)
{
    if (!(roomDownloadProgressRoomId != roomId))
    {
        if (succeeded)
        {
            SetRoomDownloadProgressStage(roomId, "谱面与资源已校验，正在完成进入", "100%  ·  已完成");
        }

        TimeSpan timeSpan = (succeeded ? TimeSpan.FromMilliseconds(650.0) : TimeSpan.FromMilliseconds(900.0)) - (DateTimeOffset.UtcNow - roomDownloadProgressShownUtc);
        if (timeSpan > TimeSpan.Zero)
        {
            await Task.Delay(timeSpan);
        }

        if (!(roomDownloadProgressRoomId != roomId))
        {
            roomDownloadProgressLayer.IsVisible = false;
            roomDownloadProgressRoomId = null;
            roomDownloadProgressBar.IsIndeterminate = false;
            AutomationProperties.SetHelpText(roomDownloadProgressLayer, $"RoomId={roomId}; Visible=False; Succeeded={succeeded}");
        }
    }
}

private Control BuildFriendsPanel()
{
    Button button = new Button
    {
        Content = "×",
        Width = 28.0,
        Height = 26.0,
        Padding = new Thickness(0.0),
        Background = Brushes.Transparent,
        Foreground = new SolidColorBrush(Color.Parse("#D2D8D9")),
        BorderBrush = new SolidColorBrush(Color.Parse("#364146")),
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(3.0)
    };
    button.Click += (object? _, RoutedEventArgs _) =>
    {
        HideFriendsPanel();
    };
    AutomationProperties.SetAutomationId(button, "FriendsPanel.Close");
    friendsOnlineCountText.Text = "在线 0";
    friendsOnlineCountText.Margin = new Thickness(8.0, 1.0, 0.0, 0.0);
    friendsOnlineCountText.VerticalAlignment = VerticalAlignment.Center;
    friendsOnlineCountText.Foreground = new SolidColorBrush(Color.Parse("#78979A"));
    friendsOnlineCountText.FontSize = 10.0;
    AutomationProperties.SetAutomationId(friendsOnlineCountText, "FriendsPanel.OnlineCount");
    StackPanel stackPanel = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            (Control)new TextBlock
            {
                Text = "好友",
                FontSize = 14.0,
                FontWeight = FontWeight.DemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#E2E7E8")),
                VerticalAlignment = VerticalAlignment.Center
            },
            (Control)friendsOnlineCountText
        }
    };
    Grid grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        Background = new SolidColorBrush(Color.Parse("#20262A")),
        Margin = new Thickness(-6.0, -6.0, -6.0, 0.0)
    };
    stackPanel.Margin = new Thickness(10.0, 7.0);
    button.Margin = new Thickness(0.0, 7.0, 10.0, 7.0);
    grid.Children.Add(stackPanel);
    Grid.SetColumn(button, 1);
    grid.Children.Add(button);
    TextBlock textBlock = new TextBlock
    {
        Text = "只有互相关注的好友可以相互邀请",
        Foreground = new SolidColorBrush(Color.Parse("#7D8A8E")),
        FontSize = 9.0,
        Margin = new Thickness(4.0, 9.0, 4.0, 8.0)
    };
    friendsList.Background = Brushes.Transparent;
    friendsList.BorderThickness = new Thickness(0.0);
    friendsList.Padding = new Thickness(0.0);
    friendsList.MaxHeight = 420.0;
    friendsList.ItemTemplate = new FuncDataTemplate<OnlinePlayerProfileDto>((OnlinePlayerProfileDto friend, INameScope _) => BuildCompactFriendRow(friend));
    ApplyCompactPlayerListStyles(friendsList, showSelection: true, "#1D2225");
    AutomationProperties.SetAutomationId(friendsList, "FriendsPanel.List");
    friendsStatusText.MinHeight = 20.0;
    friendsStatusText.Margin = new Thickness(4.0, 7.0, 4.0, 0.0);
    friendsStatusText.Foreground = new SolidColorBrush(Color.Parse("#87969A"));
    friendsStatusText.FontSize = 9.0;
    friendsStatusText.TextWrapping = TextWrapping.Wrap;
    AutomationProperties.SetAutomationId(friendsStatusText, "FriendsPanel.Status");
    Grid grid2 = new Grid
    {
        RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
    };
    grid2.Children.Add(grid);
    Grid.SetRow(textBlock, 1);
    grid2.Children.Add(textBlock);
    Grid.SetRow(friendsList, 2);
    grid2.Children.Add(friendsList);
    Grid.SetRow(friendsStatusText, 3);
    grid2.Children.Add(friendsStatusText);
    friendsPanel.Width = 330.0;
    friendsPanel.MaxHeight = 520.0;
    friendsPanel.HorizontalAlignment = HorizontalAlignment.Right;
    friendsPanel.VerticalAlignment = VerticalAlignment.Top;
    friendsPanel.Margin = new Thickness(0.0, 44.0, 8.0, 0.0);
    friendsPanel.Padding = new Thickness(6.0);
    friendsPanel.Background = new SolidColorBrush(Color.Parse("#FC181C1F"));
    friendsPanel.BorderBrush = new SolidColorBrush(Color.Parse("#465257"));
    friendsPanel.BorderThickness = new Thickness(1.0);
    friendsPanel.CornerRadius = new CornerRadius(6.0);
    friendsPanel.IsVisible = false;
    friendsPanel.Child = grid2;
    AutomationProperties.SetAutomationId(friendsPanel, "FriendsPanel");
    AutomationProperties.SetName(friendsPanel, "好友快捷面板");
    return friendsPanel;
}

private Control BuildCompactFriendRow(OnlinePlayerProfileDto? friend)
{
    if (friend == null)
    {
        return new Border
        {
            Height = 1.0
        };
    }

    Grid grid = new Grid
    {
        Width = 306.0,
        Height = 50.0,
        ColumnDefinitions = new ColumnDefinitions("38,*,48,48"),
        Margin = new Thickness(0.0, 0.0, 0.0, 5.0),
        Background = Brushes.Transparent
    };
    WindowsDefaultUserAvatar windowsDefaultUserAvatar = new WindowsDefaultUserAvatar(31.0, new SolidColorBrush(Color.Parse("#59666A")), 1.0, $"FriendsPanel.Friend.{friend.Id}.Avatar")
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };
    BindRemoteAvatar(windowsDefaultUserAvatar, friend.AvatarKey, friend.DisplayName);
    grid.Children.Add(windowsDefaultUserAvatar);
    StackPanel stackPanel = new StackPanel
    {
        Margin = new Thickness(4.0, 0.0, 4.0, 0.0),
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            (Control)new TextBlock
            {
                Text = friend.DisplayName,
                Foreground = new SolidColorBrush(Color.Parse("#E1E6E7")),
                FontWeight = FontWeight.DemiBold,
                FontSize = 11.0,
                TextTrimming = TextTrimming.CharacterEllipsis
            },
            (Control)new TextBlock
            {
                Text = (friend.IsOnline ? "在线" : "离线"),
                Foreground = new SolidColorBrush(Color.Parse(friend.IsOnline ? "#789B87" : "#6F7A7E")),
                FontSize = 9.0
            }
        }
    };
    Grid.SetColumn(stackPanel, 1);
    grid.Children.Add(stackPanel);
    Button button = CompactOnlineActionButton("主页");
    AutomationProperties.SetAutomationId(button, $"FriendsPanel.Friend.{friend.Id}.Profile");
    button.Click += async (object? _, RoutedEventArgs _) =>
    {
        HideFriendsPanel();
        await ShowOnlinePlayerPopupAsync(friend.Id, null);
    };
    Grid.SetColumn(button, 2);
    grid.Children.Add(button);
    Button invite = CompactOnlineActionButton("邀请");
    invite.Tag = friend.IsOnline;
    invite.IsEnabled = onlineAccountHost?.Session.CurrentRoom != null && onlineAccountHost.Session.CurrentUser?.Id != friend.Id && friend.IsOnline;
    friendInviteButtons[friend.Id] = invite;
    ApplyFriendInviteCooldown(friend.Id, invite);
    AutomationProperties.SetAutomationId(invite, $"FriendsPanel.Friend.{friend.Id}.Invite");
    invite.Click += async (object? _, RoutedEventArgs _) =>
    {
        await InviteFriendToCurrentRoomAsync(friend, invite);
    };
    Grid.SetColumn(invite, 3);
    grid.Children.Add(invite);
    AutomationProperties.SetName(grid, AvaloniaLocalizationCatalog.Format("好友 {0} · {1}", friend.DisplayName, friend.IsOnline ? AvaloniaLocalizationCatalog.Text("在线") : AvaloniaLocalizationCatalog.Text("离线")));
    return grid;
}

private static Button CompactOnlineActionButton(string text)
{
    return new Button
    {
        Content = text,
        Width = 44.0,
        Height = 32.0,
        Padding = new Thickness(0.0),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Background = Brushes.Transparent,
        Foreground = new SolidColorBrush(Color.Parse("#D3DADB")),
        BorderBrush = new SolidColorBrush(Color.Parse("#344044")),
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(3.0)
    };
}

private async Task ToggleFriendsPanelAsync()
{
    startupAccountChoiceHandled = true;
    if (friendsPanel.IsVisible)
    {
        HideFriendsPanel();
    }
    else
    {
        if (onlineSurfaceCompletion != null || onlineAccountEntryInProgress)
        {
            return;
        }

        if (onlineAccountHost != null)
        {
            try
            {
                if (!onlineAccountHost.Session.IsAuthenticated)
                {
                    bool flag = await ShowHostedOnlineAccountAsync(onlineAccountHost.Session);
                    UpdateOnlineAccountUi();
                    if (!flag || !onlineAccountHost.Session.IsAuthenticated)
                    {
                        return;
                    }
                }

                friendsPanel.IsVisible = true;
                friendsStatusText.Text = "正在载入好友…";
                await RefreshFriendsPanelAsync();
                return;
            }
            catch (Exception ex)
            {
                if (!windowCloseTeardownStarted)
                {
                    viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("好友功能不可用：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
                }

                return;
            }
        }

        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("好友功能不可用：{0}", onlineHostError ?? AvaloniaLocalizationCatalog.Text("联机配置无效")));
    }
}

private void HideFriendsPanel()
{
    friendsPanelLoadCancellation?.Cancel();
    friendsPanelLoadCancellation = null;
    friendsPanel.IsVisible = false;
    friendsStatusText.Text = string.Empty;
}

private async Task RefreshFriendsPanelAsync()
{
    if (friendsPanelLoadCancellation != null)
    {
        return;
    }

    WindowsOnlineAccountHost? windowsOnlineAccountHost = onlineAccountHost;
    if (windowsOnlineAccountHost == null || !windowsOnlineAccountHost.Session.IsAuthenticated)
    {
        return;
    }

    using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15.0));
    friendsPanelLoadCancellation = timeout;
    try
    {
        OnlinePlayerDirectoryDto onlinePlayerDirectoryDto = await onlineAccountHost.Session.GetFriendsAsync(timeout.Token);
        if (friendsPanelLoadCancellation == timeout && friendsPanel.IsVisible)
        {
            friendsOnlineCountText.Text = AvaloniaLocalizationCatalog.Format("在线 {0:N0}", onlinePlayerDirectoryDto.OnlineCount);
            friendInviteButtons.Clear();
            friendsList.ItemsSource = onlinePlayerDirectoryDto.Players;
            friendsStatusText.Text = ((onlinePlayerDirectoryDto.Players.Count == 0) ? "暂无互相关注的好友" : AvaloniaLocalizationCatalog.Format("共 {0:N0} 名好友", onlinePlayerDirectoryDto.Players.Count));
        }
    }
    catch (OperationCanceledException)when (friendsPanelLoadCancellation != timeout || !friendsPanel.IsVisible)
    {
    }
    catch (Exception ex2)when ((ex2 is OnlineApiException || ex2 is HttpRequestException || ex2 is OperationCanceledException || ex2 is JsonException) ? true : false)
    {
        if (friendsPanelLoadCancellation == timeout)
        {
            friendsStatusText.Text = AvaloniaLocalizationCatalog.Format("载入好友失败：{0}", AvaloniaLocalizationCatalog.Text(ex2.Message));
        }
    }
    finally
    {
        if (friendsPanelLoadCancellation == timeout)
        {
            friendsPanelLoadCancellation = null;
        }
    }
}

private async Task InviteFriendToCurrentRoomAsync(OnlinePlayerProfileDto friend, Button inviteButton)
{
    OnlineAccountSession session = onlineAccountHost?.Session;
    if (session?.CurrentRoom == null)
    {
        friendsStatusText.Text = "请先进入联机房间";
        return;
    }

    inviteButton.IsEnabled = false;
    friendsStatusText.Text = AvaloniaLocalizationCatalog.Format("正在邀请 {0}…", friend.DisplayName);
    try
    {
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15.0));
        await session.InviteToCurrentRoomAsync(friend.Id, timeout.Token);
        friendInviteCooldowns[friend.Id] = DateTimeOffset.UtcNow.AddSeconds(15.0);
        ApplyFriendInviteCooldown(friend.Id, inviteButton);
        if (!friendInviteCooldownTimer.IsEnabled)
        {
            friendInviteCooldownTimer.Start();
        }

        friendsStatusText.Text = AvaloniaLocalizationCatalog.Format("已邀请 {0}", friend.DisplayName);
    }
    catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is OperationCanceledException || ex is JsonException) ? true : false)
    {
        friendsStatusText.Text = AvaloniaLocalizationCatalog.Format("邀请失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message));
        inviteButton.IsEnabled = session.CurrentRoom != null;
    }
}

private void RefreshFriendInviteCooldownButtons()
{
    KeyValuePair<long, Button>[] array = friendInviteButtons.ToArray();
    for (int i = 0; i < array.Length; i++)
    {
        KeyValuePair<long, Button> keyValuePair = array[i];
        ApplyFriendInviteCooldown(keyValuePair.Key, keyValuePair.Value);
    }

    if (friendInviteCooldowns.Count == 0)
    {
        friendInviteCooldownTimer.Stop();
    }
}

private void ApplyFriendInviteCooldown(long userId, Button button)
{
    if (friendInviteCooldowns.TryGetValue(userId, out var value))
    {
        TimeSpan timeSpan = value - DateTimeOffset.UtcNow;
        if (timeSpan > TimeSpan.Zero)
        {
            button.Content = $"{Math.Max(1, (int)Math.Ceiling(timeSpan.TotalSeconds))}s";
            button.IsEnabled = false;
            return;
        }

        friendInviteCooldowns.Remove(userId);
    }

    button.Content = "邀请";
    OnlineAccountSession onlineAccountSession = onlineAccountHost?.Session;
    int isEnabled;
    if (onlineAccountSession?.CurrentRoom != null)
    {
        OnlineUserProfileDto? currentUser = onlineAccountSession.CurrentUser;
        if ((object)currentUser == null || currentUser.Id != userId)
        {
            object tag = button.Tag;
            isEnabled = ((tag is bool && (bool)tag) ? 1 : 0);
            goto IL_00e5;
        }
    }

    isEnabled = 0;
    goto IL_00e5;
    IL_00e5:
        button.IsEnabled = (byte)isEnabled != 0;
}

private Control BuildOnlinePlayerPopupLayer()
{
    onlinePlayerPopupContent.HorizontalAlignment = HorizontalAlignment.Center;
    onlinePlayerPopupContent.VerticalAlignment = VerticalAlignment.Center;
    onlinePlayerPopupContent.MaxWidth = 560.0;
    onlinePlayerPopupContent.MaxHeight = 760.0;
    onlinePlayerPopupLayer.HorizontalAlignment = HorizontalAlignment.Stretch;
    onlinePlayerPopupLayer.VerticalAlignment = VerticalAlignment.Stretch;
    onlinePlayerPopupLayer.Padding = new Thickness(18.0);
    onlinePlayerPopupLayer.Background = new SolidColorBrush(Color.Parse("#B0101214"));
    onlinePlayerPopupLayer.IsVisible = false;
    onlinePlayerPopupLayer.Child = onlinePlayerPopupContent;
    AutomationProperties.SetAutomationId(onlinePlayerPopupLayer, "Online.PlayerPopup");
    AutomationProperties.SetName(onlinePlayerPopupLayer, "玩家主页");
    return onlinePlayerPopupLayer;
}

private async Task ShowOnlinePlayerPopupAsync(long userId, OnlineRoomMemberDto? roomMember)
{
    OnlineAccountSession onlineAccountSession = onlineAccountHost?.Session;
    if (userId <= 0 || onlineAccountSession == null || !onlineAccountSession.IsAuthenticated)
    {
        return;
    }

    onlinePlayerPopupLoadCancellation?.Cancel();
    onlinePlayerPopupLoadCancellation?.Dispose();
    CancellationTokenSource timeout = (onlinePlayerPopupLoadCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20.0)));
    onlinePlayerPopupUserId = userId;
    onlinePlayerPopupLayer.IsVisible = true;
    SetOnlinePlayerPopupContent(BuildOnlinePlayerLoadingCard("正在载入玩家主页…"));
    try
    {
        OnlinePlayerProfileDto profile = await onlineAccountSession.GetPlayerAsync(userId, (roomMember == null) ? ((Guid? )null) : onlineAccountSession.CurrentRoom?.Id, timeout.Token);
        if (onlinePlayerPopupLoadCancellation == timeout && onlinePlayerPopupUserId == userId)
        {
            SetOnlinePlayerPopupContent(BuildOnlinePlayerCard(profile, roomMember));
        }
    }
    catch (OperationCanceledException)when (onlinePlayerPopupLoadCancellation != timeout || onlinePlayerPopupUserId != userId)
    {
    }
    catch (Exception ex2)when ((ex2 is OnlineApiException || ex2 is HttpRequestException || ex2 is OperationCanceledException || ex2 is JsonException) ? true : false)
    {
        if (onlinePlayerPopupLoadCancellation == timeout)
        {
            SetOnlinePlayerPopupContent(BuildOnlinePlayerLoadingCard(AvaloniaLocalizationCatalog.Format("载入玩家主页失败：{0}", AvaloniaLocalizationCatalog.Text(ex2.Message))));
        }
    }
}

private void SetOnlinePlayerPopupContent(Control content)
{
    onlinePlayerPopupContent.Children.Clear();
    onlinePlayerPopupContent.Children.Add(content);
}

private Control BuildOnlinePlayerLoadingCard(string message)
{
    Button button = CompactOnlineActionButton("×");
    button.HorizontalAlignment = HorizontalAlignment.Right;
    button.Click += (object? _, RoutedEventArgs _) =>
    {
        CloseOnlinePlayerPopup();
    };
    Grid grid = new Grid
    {
        RowDefinitions = new RowDefinitions("Auto,*"),
        Children =
        {
            (Control)button
        }
    };
    TextBlock textBlock = new TextBlock
    {
        Text = message,
        Margin = new Thickness(0.0, 28.0, 0.0, 10.0),
        Foreground = new SolidColorBrush(Color.Parse("#A8B4B7")),
        TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center
    };
    Grid.SetRow(textBlock, 1);
    grid.Children.Add(textBlock);
    return OnlinePlayerPopupCard(grid);
}

private Control BuildOnlinePlayerCard(OnlinePlayerProfileDto profile, OnlineRoomMemberDto? initialRoomMember)
{
    OnlineAccountSession session = onlineAccountHost?.Session ?? throw new InvalidOperationException("联机账号会话不可用。");
    TextBlock status = new TextBlock
    {
        Text = (profile.IsOnline ? "当前在线" : "当前离线"),
        Foreground = new SolidColorBrush(Color.Parse(profile.IsOnline ? "#8FC4A5" : "#788589")),
        FontSize = 9.0,
        Margin = new Thickness(0.0, 8.0, 0.0, 0.0),
        TextWrapping = TextWrapping.Wrap
    };
    Button button = CompactOnlineActionButton("×");
    button.Width = 26.0;
    button.Height = 24.0;
    button.HorizontalAlignment = HorizontalAlignment.Right;
    button.VerticalAlignment = VerticalAlignment.Top;
    button.Click += (object? _, RoutedEventArgs _) =>
    {
        CloseOnlinePlayerPopup();
    };
    WindowsDefaultUserAvatar windowsDefaultUserAvatar = new WindowsDefaultUserAvatar(62.0, new SolidColorBrush(Color.Parse("#59666A")), 1.0, $"Online.PlayerPopup.{profile.Id}.Avatar");
    BindRemoteAvatar(windowsDefaultUserAvatar, profile.AvatarKey, profile.DisplayName);
    StackPanel stackPanel = new StackPanel();
    stackPanel.Margin = new Thickness(10.0, 0.0, 0.0, 0.0);
    stackPanel.VerticalAlignment = VerticalAlignment.Center;
    stackPanel.Spacing = 4.0;
    stackPanel.Children.Add(new TextBlock { Text = profile.DisplayName, Foreground = new SolidColorBrush(Color.Parse("#E1E6E7")), FontSize = 19.0, FontWeight = FontWeight.DemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
    stackPanel.Children.Add(new TextBlock { Text = AvaloniaLocalizationCatalog.Format("关注 {0:N0} · 粉丝 {1:N0} · 制谱 {2}", profile.FollowingCount, profile.FollowerCount, FormatOnlineChartingDuration(profile.ChartingSeconds)), Foreground = new SolidColorBrush(Color.Parse("#839397")), FontSize = 11.0, TextWrapping = TextWrapping.Wrap });
    StackPanel stackPanel2 = stackPanel;
    Grid grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto")
    };
    grid.Children.Add(new TextBlock { Text = "玩家主页", Foreground = new SolidColorBrush(Color.Parse("#829B9E")), FontSize = 10.0, FontWeight = FontWeight.DemiBold, VerticalAlignment = VerticalAlignment.Center });
    Grid.SetColumn(button, 1);
    grid.Children.Add(button);
    Grid grid2 = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("72,*"),
        Margin = new Thickness(0.0, 12.0, 0.0, 0.0)
    };
    grid2.Children.Add(windowsDefaultUserAvatar);
    Grid.SetColumn(stackPanel2, 1);
    grid2.Children.Add(stackPanel2);
    StackPanel stackPanel3 = new StackPanel();
    stackPanel3.Children.Add(grid);
    stackPanel3.Children.Add(grid2);
    stackPanel3.Children.Add(new TextBlock { Text = (string.IsNullOrWhiteSpace(profile.Bio) ? "暂无简介" : profile.Bio), Foreground = new SolidColorBrush(Color.Parse("#ABB5B8")), Margin = new Thickness(0.0, 14.0, 0.0, 0.0), MinHeight = 42.0, TextWrapping = TextWrapping.Wrap });
    Button button2 = null;
    if (session.CurrentUser?.Id != profile.Id)
    {
        Button followButton = null;
        followButton = PrimaryButton(profile.IsFollowing ? "取消关注" : "关注", async (object? _, RoutedEventArgs _) =>
        {
            if (!followButton.IsEnabled)
            {
                return;
            }

            followButton.IsEnabled = false;
            status.Text = (profile.IsFollowing ? "正在取消关注…" : "正在关注…");
            try
            {
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15.0));
                await session.SetFollowAsync(profile.Id, !profile.IsFollowing, timeout.Token);
                profile = profile with
                {
                    IsFollowing = !profile.IsFollowing
                };
                followButton.Content = (profile.IsFollowing ? "取消关注" : "关注");
                OnlinePlayerProfileDto profile2 = await session.GetPlayerAsync(profile.Id, (initialRoomMember == null) ? ((Guid? )null) : session.CurrentRoom?.Id, timeout.Token);
                if (onlinePlayerPopupUserId == profile.Id)
                {
                    SetOnlinePlayerPopupContent(BuildOnlinePlayerCard(profile2, initialRoomMember));
                }
            }
            catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is OperationCanceledException || ex is JsonException) ? true : false)
            {
                status.Text = ex.Message;
                status.Foreground = new SolidColorBrush(Color.Parse("#D39A88"));
                followButton.IsEnabled = true;
            }
        });
        button2 = followButton;
        button2.HorizontalAlignment = HorizontalAlignment.Stretch;
        ApplyOnlinePlayerPrimaryButtonStyle(button2, 36.0);
    }

    OnlineRoomSummaryDto currentRoom = session.CurrentRoom;
    bool flag = initialRoomMember != null && currentRoom?.OwnerUserId == session.CurrentUser?.Id && initialRoomMember.UserId != session.CurrentUser?.Id && !string.Equals(initialRoomMember.MemberRole, "owner", StringComparison.OrdinalIgnoreCase);
    if (flag && initialRoomMember != null)
    {
        string memberRole = (string.Equals(initialRoomMember.MemberRole, "viewer", StringComparison.OrdinalIgnoreCase) ? "viewer" : "editor");
        CheckBox metadata = PlayerPermissionCheckBox("允许编辑谱面信息", initialRoomMember.CanEditMetadata, $"Online.PlayerPopup.{profile.Id}.Metadata");
        CheckBox timing = PlayerPermissionCheckBox("允许编辑 Effect（Timing Edit 全部功能）", initialRoomMember.CanEditTiming, $"Online.PlayerPopup.{profile.Id}.Timing");
        CheckBox notes = PlayerPermissionCheckBox("允许编辑 Note", initialRoomMember.CanEditNotes, $"Online.PlayerPopup.{profile.Id}.Notes");
        metadata.Margin = new Thickness(0.0, 9.0, 0.0, 0.0);
        timing.Margin = new Thickness(0.0, 7.0, 0.0, 0.0);
        notes.Margin = new Thickness(0.0, 7.0, 0.0, 0.0);
        Button roleButton = null;
        roleButton = PrimaryButton((memberRole == "viewer") ? "设为编辑者" : "设为观察者", async (object? _, RoutedEventArgs _) =>
        {
            roleButton.IsEnabled = false;
            string nextRole = ((memberRole == "viewer") ? "editor" : "viewer");
            status.Text = ((nextRole == "viewer") ? "正在设为观察者…" : "正在设为编辑者…");
            try
            {
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15.0));
                bool allowMetadata = nextRole == "editor" && metadata.IsChecked == true;
                bool allowTiming = nextRole == "editor" && timing.IsChecked == true;
                bool allowNotes = nextRole == "editor" && notes.IsChecked == true;
                await session.UpdateRoomMemberAccessAsync(profile.Id, nextRole, allowMetadata, allowTiming, allowNotes, timeout.Token);
                memberRole = nextRole;
                initialRoomMember = initialRoomMember with
                {
                    MemberRole = nextRole,
                    CanEditMetadata = allowMetadata,
                    CanEditTiming = allowTiming,
                    CanEditNotes = allowNotes
                };
                ReplaceOnlineCreatorMember(initialRoomMember);
                metadata.IsChecked = allowMetadata;
                timing.IsChecked = allowTiming;
                notes.IsChecked = allowNotes;
                bool flag2 = memberRole == "editor";
                metadata.IsEnabled = flag2;
                timing.IsEnabled = flag2;
                notes.IsEnabled = flag2;
                roleButton.Content = (flag2 ? "设为观察者" : "设为编辑者");
                status.Text = (flag2 ? "已设为编辑者" : "已设为观察者");
            }
            catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is OperationCanceledException || ex is JsonException) ? true : false)
            {
                status.Text = ex.Message;
                status.Foreground = new SolidColorBrush(Color.Parse("#D39A88"));
            }
            finally
            {
                roleButton.IsEnabled = true;
            }
        });
        roleButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        ApplyOnlinePlayerSecondaryButtonStyle(roleButton, 36.0);
        Button save = null;
        save = PrimaryButton("保存房间权限", async (object? _, RoutedEventArgs _) =>
        {
            save.IsEnabled = false;
            status.Text = "正在保存房间权限…";
            try
            {
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15.0));
                await session.UpdateRoomMemberAccessAsync(profile.Id, memberRole, metadata.IsChecked == true, timing.IsChecked == true, notes.IsChecked == true, timeout.Token);
                initialRoomMember = initialRoomMember with
                {
                    CanEditMetadata = (metadata.IsChecked == true),
                    CanEditTiming = (timing.IsChecked == true),
                    CanEditNotes = (notes.IsChecked == true)
                };
                ReplaceOnlineCreatorMember(initialRoomMember);
                status.Text = "房间权限已保存";
            }
            catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is OperationCanceledException || ex is JsonException) ? true : false)
            {
                status.Text = ex.Message;
                status.Foreground = new SolidColorBrush(Color.Parse("#D39A88"));
            }
            finally
            {
                save.IsEnabled = true;
            }
        });
        save.HorizontalAlignment = HorizontalAlignment.Stretch;
        save.Margin = new Thickness(0.0, 10.0, 0.0, 0.0);
        ApplyOnlinePlayerPrimaryButtonStyle(save, 34.0);
        Border item = new Border
        {
            Margin = new Thickness(0.0, 10.0, 0.0, 0.0),
            Padding = new Thickness(11.0),
            Background = new SolidColorBrush(Color.Parse("#20272A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#425256")),
            BorderThickness = new Thickness(1.0),
            CornerRadius = new CornerRadius(4.0),
            Child = new StackPanel
            {
                Children =
                {
                    (Control)new TextBlock
                    {
                        Text = "房间编辑权限 · 由房主持久分配",
                        Foreground = new SolidColorBrush(Color.Parse("#9DB7B9")),
                        FontSize = 10.0,
                        FontWeight = FontWeight.DemiBold
                    },
                    (Control)metadata,
                    (Control)timing,
                    (Control)notes,
                    (Control)save
                }
            }
        };
        bool isEnabled = memberRole == "editor";
        metadata.IsEnabled = isEnabled;
        timing.IsEnabled = isEnabled;
        notes.IsEnabled = isEnabled;
        Grid grid3 = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            Margin = new Thickness(0.0, 14.0, 0.0, 0.0)
        };
        if (button2 != null)
        {
            button2.Margin = new Thickness(0.0, 0.0, 4.0, 0.0);
            grid3.Children.Add(button2);
            roleButton.Margin = new Thickness(4.0, 0.0, 0.0, 0.0);
            Grid.SetColumn(roleButton, 1);
        }
        else
        {
            Grid.SetColumnSpan(roleButton, 2);
        }

        grid3.Children.Add(roleButton);
        stackPanel3.Children.Add(grid3);
        stackPanel3.Children.Add(item);
        Button button3 = PrimaryButton("踢出", async (object? _, RoutedEventArgs _) =>
        {
            await RemoveOnlinePlayerFromRoomAsync(profile, 0, status);
        });
        Button button4 = PrimaryButton("暂时封禁 30 分钟", async (object? _, RoutedEventArgs _) =>
        {
            await RemoveOnlinePlayerFromRoomAsync(profile, 30, status);
        });
        Button[] array = new Button[2]
        {
            button3,
            button4
        };
        foreach (Button obj in array)
        {
            ApplyOnlinePlayerDangerButtonStyle(obj, 34.0);
            obj.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        Grid grid4 = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
        };
        button3.Margin = new Thickness(0.0, 0.0, 4.0, 0.0);
        button4.Margin = new Thickness(4.0, 0.0, 0.0, 0.0);
        grid4.Children.Add(button3);
        Grid.SetColumn(button4, 1);
        grid4.Children.Add(button4);
        stackPanel3.Children.Add(grid4);
    }

    if (!flag && button2 != null)
    {
        button2.Margin = new Thickness(0.0, 14.0, 0.0, 0.0);
        stackPanel3.Children.Add(button2);
    }

    stackPanel3.Children.Add(status);
    return OnlinePlayerPopupCard(new ScrollViewer { MaxHeight = 704.0, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = stackPanel3 });
}

private static CheckBox PlayerPermissionCheckBox(string text, bool value, string automationId)
{
    CheckBox checkBox = new CheckBox
    {
        Content = text,
        IsChecked = value,
        Foreground = new SolidColorBrush(Color.Parse("#E1E6E7")),
        FontSize = 11.0
    };
    AutomationProperties.SetAutomationId(checkBox, automationId);
    return checkBox;
}

private static Control OnlinePlayerPopupCard(Control content)
{
    return new Border
    {
        Width = 390.0,
        MaxWidth = 390.0,
        MaxHeight = 740.0,
        Padding = new Thickness(18.0),
        Background = new SolidColorBrush(Color.Parse("#1A1F22")),
        BorderBrush = new SolidColorBrush(Color.Parse("#465358")),
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(7.0),
        Child = content
    };
}

private static void ApplyOnlinePlayerPrimaryButtonStyle(Button button, double height)
{
    button.Height = height;
    button.Padding = new Thickness(9.0, 6.0);
    button.Background = new SolidColorBrush(Color.Parse("#38676C"));
    button.BorderBrush = new SolidColorBrush(Color.Parse("#4A777B"));
    button.Foreground = Brushes.White;
    button.BorderThickness = new Thickness(1.0);
    button.CornerRadius = new CornerRadius(3.0);
    button.FontWeight = FontWeight.DemiBold;
}

private static void ApplyOnlinePlayerSecondaryButtonStyle(Button button, double height)
{
    button.Height = height;
    button.Padding = new Thickness(9.0, 6.0);
    button.Background = new SolidColorBrush(Color.Parse("#1A1D20"));
    button.BorderBrush = new SolidColorBrush(Color.Parse("#34393D"));
    button.Foreground = new SolidColorBrush(Color.Parse("#D0D4D6"));
    button.BorderThickness = new Thickness(1.0);
    button.CornerRadius = new CornerRadius(3.0);
}

private static void ApplyOnlinePlayerDangerButtonStyle(Button button, double height)
{
    button.Height = height;
    button.Padding = new Thickness(9.0, 6.0);
    button.Background = new SolidColorBrush(Color.Parse("#4A292B"));
    button.BorderBrush = new SolidColorBrush(Color.Parse("#754548"));
    button.Foreground = new SolidColorBrush(Color.Parse("#FFD8D8"));
    button.BorderThickness = new Thickness(1.0);
    button.CornerRadius = new CornerRadius(3.0);
    button.FontWeight = FontWeight.DemiBold;
}

private async Task RemoveOnlinePlayerFromRoomAsync(OnlinePlayerProfileDto profile, int banMinutes, TextBlock status)
{
    OnlineAccountSession onlineAccountSession = onlineAccountHost?.Session;
    if (onlineAccountSession?.CurrentRoom == null)
    {
        return;
    }

    status.Text = ((banMinutes > 0) ? "正在暂时封禁并移出…" : "正在移出房间…");
    try
    {
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15.0));
        await onlineAccountSession.RemoveRoomMemberAsync(profile.Id, banMinutes, timeout.Token);
        onlineCreatorMembers = onlineCreatorMembers.Where((OnlineRoomMemberDto member) => member.UserId != profile.Id).ToArray();
        roomMemberList.ItemsSource = onlineCreatorMembers;
        RefreshTimelineOnlineCreators();
        CloseOnlinePlayerPopup();
    }
    catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is OperationCanceledException || ex is JsonException) ? true : false)
    {
        status.Text = ex.Message;
        status.Foreground = new SolidColorBrush(Color.Parse("#D39A88"));
    }
}

private void ReplaceOnlineCreatorMember(OnlineRoomMemberDto updated)
{
    onlineCreatorMembers = onlineCreatorMembers.Select((OnlineRoomMemberDto member) => (member.UserId != updated.UserId) ? member : updated).ToArray();
    suppressRoomMemberSelectionChanged = true;
    try
    {
        roomMemberList.ItemsSource = onlineCreatorMembers;
        roomMemberList.SelectedItem = updated;
    }
    finally
    {
        suppressRoomMemberSelectionChanged = false;
    }

    RefreshTimelineOnlineCreators();
}

private void CloseOnlinePlayerPopup()
{
    onlinePlayerPopupLoadCancellation?.Cancel();
    onlinePlayerPopupLoadCancellation?.Dispose();
    onlinePlayerPopupLoadCancellation = null;
    onlinePlayerPopupUserId = 0L;
    onlinePlayerPopupLayer.IsVisible = false;
    onlinePlayerPopupContent.Children.Clear();
    roomMemberList.SelectedItem = null;
    timeline.Focus();
}

private static string FormatOnlineChartingDuration(long seconds)
{
    TimeSpan timeSpan = TimeSpan.FromSeconds(Math.Max(0L, seconds));
    if (!(timeSpan.TotalHours >= 1.0))
    {
        return AvaloniaLocalizationCatalog.Format("{0:N0} 分钟", timeSpan.Minutes);
    }

    return AvaloniaLocalizationCatalog.Format("{0:N0} 小时 {1:N0} 分钟", (int)timeSpan.TotalHours, timeSpan.Minutes);
}

private Control BuildAudioImportNameLayer()
{
    TextBlock textBlock = new TextBlock
    {
        Text = "导入音频并新建谱面",
        FontSize = 21.0,
        FontWeight = FontWeight.DemiBold,
        Foreground = new SolidColorBrush(Color.Parse("#E3E8E9"))
    };
    AutomationProperties.SetAutomationId(textBlock, "Library.AudioImport.Title");
    audioImportSourceText.Foreground = new SolidColorBrush(Color.Parse("#87969A"));
    audioImportSourceText.TextTrimming = TextTrimming.CharacterEllipsis;
    audioImportSourceText.Margin = new Thickness(0.0, 8.0, 0.0, 0.0);
    AutomationProperties.SetAutomationId(audioImportSourceText, "Library.AudioImport.Source");
    TextBlock item = new TextBlock
    {
        Text = "歌曲文件夹名称",
        Foreground = MutedBrush,
        FontSize = 11.0,
        Margin = new Thickness(0.0, 19.0, 0.0, 0.0)
    };
    audioImportNameBox.MaxLength = 80;
    audioImportNameBox.ContextMenu = null;
    audioImportNameBox.VerticalContentAlignment = VerticalAlignment.Center;
    AutomationProperties.SetAutomationId(audioImportNameBox, "Library.AudioImport.Name");
    AutomationProperties.SetName(audioImportNameBox, "歌曲文件夹名称");
    TextBlock item2 = new TextBlock
    {
        Text = AvaloniaLocalizationCatalog.Format("默认使用音频文件名。重名会自动追加 _1、_2；清空后使用时间戳。谱面先在软件旁缓存编辑，点击保存才写回曲库目录。"),
        Foreground = new SolidColorBrush(Color.Parse("#7F8D91")),
        FontSize = 10.0,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 18.0,
        Margin = new Thickness(0.0, 10.0, 0.0, 0.0)
    };
    audioImportNameStatusText.Foreground = new SolidColorBrush(Color.Parse("#C58C79"));
    audioImportNameStatusText.MinHeight = 20.0;
    audioImportNameStatusText.Margin = new Thickness(0.0, 8.0, 0.0, 0.0);
    audioImportNameStatusText.TextWrapping = TextWrapping.Wrap;
    AutomationProperties.SetAutomationId(audioImportNameStatusText, "Library.AudioImport.Status");
    Button button = PrimaryButton("取消", (object? _, RoutedEventArgs _) =>
    {
        CompleteAudioImportNamePrompt(confirm: false);
    });
    ApplyWpfToolButtonStyle(button);
    button.Height = 40.0;
    button.Margin = new Thickness(0.0, 0.0, 6.0, 0.0);
    AutomationProperties.SetAutomationId(button, "Library.AudioImport.Cancel");
    Button button2 = PrimaryButton("创建并导入", (object? _, RoutedEventArgs _) =>
    {
        CompleteAudioImportNamePrompt(confirm: true);
    });
    ApplyWpfPrimaryButtonStyle(button2);
    button2.Height = 40.0;
    button2.Margin = new Thickness(6.0, 0.0, 0.0, 0.0);
    audioImportNameConfirmButton = button2;
    AutomationProperties.SetAutomationId(button2, "Library.AudioImport.Confirm");
    Grid grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,*"),
        Margin = new Thickness(0.0, 15.0, 0.0, 0.0)
    };
    grid.Children.Add(button);
    Grid.SetColumn(button2, 1);
    grid.Children.Add(button2);
    StackPanel child = new StackPanel
    {
        Children =
        {
            (Control)textBlock,
            (Control)audioImportSourceText,
            (Control)item,
            (Control)audioImportNameBox,
            (Control)item2,
            (Control)audioImportNameStatusText,
            (Control)grid
        }
    };
    audioImportNameLayer.IsVisible = false;
    audioImportNameLayer.Background = new SolidColorBrush(Color.Parse("#E8111315"));
    audioImportNameLayer.Child = new Border
    {
        Width = 470.0,
        Padding = new Thickness(26.0),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.Parse("#1A1E21")),
        BorderBrush = new SolidColorBrush(Color.Parse("#455257")),
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(7.0),
        Child = child
    };
    AutomationProperties.SetAutomationId(audioImportNameLayer, "Library.AudioImport.Layer");
    AutomationProperties.SetName(audioImportNameLayer, "导入音频并新建谱面");
    return audioImportNameLayer;
}

private Control BuildAboutLayer()
{
    aboutVersionText.Text = AvaloniaLocalizationCatalog.Format("版本 {0}", "0.1.37");
    aboutVersionText.Foreground = new SolidColorBrush(Color.Parse("#7FA1A4"));
    aboutVersionText.FontFamily = new FontFamily("Cascadia Mono,Consolas");
    aboutVersionText.FontSize = 10.0;
    aboutVersionText.Margin = new Thickness(0.0, 5.0, 0.0, 0.0);
    AutomationProperties.SetAutomationId(aboutVersionText, "About.Version");
    aboutContentText.Text = "SimpleManiaEditor\n面向 osu!mania / 7K 的本地编辑器\nCopyright © 2026 Ist1na\n基于 LumaChart Studio（MIT）改造；保留上游与第三方许可证。";
    aboutContentText.Foreground = new SolidColorBrush(Color.Parse("#B8C1C3"));
    aboutContentText.TextWrapping = TextWrapping.Wrap;
    aboutContentText.LineHeight = 22.0;
    AutomationProperties.SetAutomationId(aboutContentText, "About.Content");
    Button button = PrimaryButton("关闭", (object? _, RoutedEventArgs _) =>
    {
        CloseAbout();
    });
    button.Padding = new Thickness(12.0, 6.0);
    ApplyWpfToolButtonStyle(button);
    AutomationProperties.SetAutomationId(button, "About.Close");
    Grid grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto")
    };
    grid.Children.Add(new StackPanel { Children = { (Control)new TextBlock { Text = "关于 SimpleManiaEditor", FontSize = 22.0, FontWeight = FontWeight.DemiBold, Foreground = new SolidColorBrush(Color.Parse("#E3E8E9")) }, (Control)aboutVersionText } });
    Grid.SetColumn(button, 1);
    grid.Children.Add(button);
    Button button2 = PrimaryButton("GitHub 仓库", (object? _, RoutedEventArgs _) =>
    {
        OpenExternalUrl("https://github.com/Ist1na07/SimpleManiaEditor");
    });
    button2.Height = 38.0;
    button2.HorizontalAlignment = HorizontalAlignment.Stretch;
    button2.Margin = new Thickness(5.0, 0.0);
    ApplyWpfToolButtonStyle(button2);
    AutomationProperties.SetAutomationId(button2, "About.Repository");
    Button button3 = PrimaryButton("问题反馈", async (object? _, RoutedEventArgs _) =>
    {
        await ShowIssueReportAsync();
    });
    button3.Height = 38.0;
    button3.HorizontalAlignment = HorizontalAlignment.Stretch;
    button3.Margin = new Thickness(0.0, 0.0, 5.0, 0.0);
    ApplyWpfPrimaryButtonStyle(button3);
    AutomationProperties.SetAutomationId(button3, "About.IssueReport");
    aboutAdminButton = PrimaryButton("内容后台", async (object? _, RoutedEventArgs _) =>
    {
        await OpenOwnerAppConfigurationAsync();
    });
    aboutAdminButton.Height = 38.0;
    aboutAdminButton.HorizontalAlignment = HorizontalAlignment.Stretch;
    aboutAdminButton.Margin = new Thickness(0.0, 10.0, 0.0, 0.0);
    aboutAdminButton.IsVisible = false;
    AutomationProperties.SetAutomationId(aboutAdminButton, "About.Admin");
    Grid grid2 = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,*")
    };
    grid2.Children.Add(button3);
    Grid.SetColumn(button2, 1);
    grid2.Children.Add(button2);
    Grid grid3 = new Grid
    {
        RowDefinitions = new RowDefinitions("Auto,*,Auto")
    };
    grid3.Children.Add(grid);
    ScrollViewer scrollViewer = new ScrollViewer
    {
        MaxHeight = 350.0,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(0.0, 20.0, 0.0, 12.0),
        Content = aboutContentText
    };
    Grid.SetRow(scrollViewer, 1);
    grid3.Children.Add(scrollViewer);
    Grid.SetRow(grid2, 2);
    grid3.Children.Add(grid2);
    aboutLayer.IsVisible = false;
    aboutLayer.Background = new SolidColorBrush(Color.Parse("#E8111315"));
    aboutLayer.Child = new Border
    {
        Width = 570.0,
        MaxHeight = 650.0,
        Padding = new Thickness(27.0),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.Parse("#1A1E21")),
        BorderBrush = new SolidColorBrush(Color.Parse("#475459")),
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(7.0),
        Child = grid3
    };
    AutomationProperties.SetAutomationId(aboutLayer, "About.Layer");
    AutomationProperties.SetName(aboutLayer, "关于 SimpleManiaEditor");
    return aboutLayer;
}

private Control BuildUpdateLayer()
{
    updateTitleText.Text = "发现新版本";
    updateTitleText.FontSize = 23.0;
    updateTitleText.FontWeight = FontWeight.DemiBold;
    updateTitleText.Foreground = new SolidColorBrush(Color.Parse("#E5E9EA"));
    AutomationProperties.SetAutomationId(updateTitleText, "Update.Title");
    updateVersionText.Text = AvaloniaLocalizationCatalog.Format("当前 {0}  →  最新 {1}", "0.1.37", "0.1.37");
    updateVersionText.Foreground = new SolidColorBrush(Color.Parse("#A8C3C5"));
    updateVersionText.FontFamily = new FontFamily("Cascadia Mono,Consolas");
    AutomationProperties.SetAutomationId(updateVersionText, "Update.Version");
    updateNoticeText.Text = "暂无更新公告。";
    updateNoticeText.Foreground = new SolidColorBrush(Color.Parse("#B7C0C2"));
    updateNoticeText.TextWrapping = TextWrapping.Wrap;
    updateNoticeText.LineHeight = 22.0;
    AutomationProperties.SetAutomationId(updateNoticeText, "Update.Notice");
    updateLaterButton = PrimaryButton("以后再说", (object? _, RoutedEventArgs _) =>
    {
        CloseUpdatePrompt();
    });
    updateLaterButton.Height = 40.0;
    updateLaterButton.HorizontalAlignment = HorizontalAlignment.Stretch;
    updateLaterButton.Margin = new Thickness(0.0, 0.0, 6.0, 0.0);
    ApplyWpfToolButtonStyle(updateLaterButton);
    AutomationProperties.SetAutomationId(updateLaterButton, "Update.Later");
    updateDownloadButton = PrimaryButton("打开下载链接", (object? _, RoutedEventArgs _) =>
    {
        OpenCurrentUpdateDownload();
    });
    updateDownloadButton.Height = 40.0;
    updateDownloadButton.HorizontalAlignment = HorizontalAlignment.Stretch;
    updateDownloadButton.Margin = new Thickness(6.0, 0.0, 0.0, 0.0);
    AutomationProperties.SetAutomationId(updateDownloadButton, "Update.Download");
    Border border = new Border
    {
        Background = new SolidColorBrush(Color.Parse("#20272A")),
        BorderBrush = new SolidColorBrush(Color.Parse("#354348")),
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(4.0),
        Padding = new Thickness(12.0, 9.0),
        Margin = new Thickness(0.0, 15.0, 0.0, 0.0),
        Child = updateVersionText
    };
    ScrollViewer scrollViewer = new ScrollViewer
    {
        MaxHeight = 330.0,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(0.0, 17.0, 0.0, 15.0),
        Content = updateNoticeText
    };
    Grid grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,*")
    };
    grid.Children.Add(updateLaterButton);
    Grid.SetColumn(updateDownloadButton, 1);
    grid.Children.Add(updateDownloadButton);
    Grid grid2 = new Grid
    {
        RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
    };
    grid2.Children.Add(updateTitleText);
    Grid.SetRow(border, 1);
    grid2.Children.Add(border);
    Grid.SetRow(scrollViewer, 2);
    grid2.Children.Add(scrollViewer);
    Grid.SetRow(grid, 3);
    grid2.Children.Add(grid);
    updateLayer.IsVisible = false;
    updateLayer.Background = new SolidColorBrush(Color.Parse("#ED111315"));
    updateLayer.Child = new Border
    {
        Width = 590.0,
        MaxHeight = 680.0,
        Padding = new Thickness(28.0),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.Parse("#1A1E21")),
        BorderBrush = new SolidColorBrush(Color.Parse("#53666B")),
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(7.0),
        Child = grid2
    };
    AutomationProperties.SetAutomationId(updateLayer, "Update.Layer");
    AutomationProperties.SetName(updateLayer, "客户端更新");
    return updateLayer;
}

private Control BuildRoomPermissionNotice()
{
    roomPermissionNoticeTitle.FontSize = 21.0;
    roomPermissionNoticeTitle.FontWeight = FontWeight.DemiBold;
    roomPermissionNoticeTitle.Foreground = new SolidColorBrush(Color.Parse("#E3E9EA"));
    AutomationProperties.SetAutomationId(roomPermissionNoticeTitle, "OnlineRoom.PermissionNotice.Title");
    roomPermissionNoticeText.Margin = new Thickness(0.0, 13.0, 0.0, 18.0);
    roomPermissionNoticeText.TextWrapping = TextWrapping.Wrap;
    roomPermissionNoticeText.LineHeight = 22.0;
    roomPermissionNoticeText.Foreground = new SolidColorBrush(Color.Parse("#B4C0C2"));
    AutomationProperties.SetAutomationId(roomPermissionNoticeText, "OnlineRoom.PermissionNotice.Message");
    roomPermissionNoticeDismissButton = PrimaryButton("知道了", (object? _, RoutedEventArgs _) =>
    {
        CloseRoomPermissionNotice();
    });
    roomPermissionNoticeDismissButton.Height = 38.0;
    roomPermissionNoticeDismissButton.HorizontalAlignment = HorizontalAlignment.Right;
    roomPermissionNoticeDismissButton.Padding = new Thickness(28.0, 7.0);
    AutomationProperties.SetAutomationId(roomPermissionNoticeDismissButton, "OnlineRoom.PermissionNotice.Dismiss");
    roomPermissionNoticeLayer.HorizontalAlignment = HorizontalAlignment.Stretch;
    roomPermissionNoticeLayer.VerticalAlignment = VerticalAlignment.Stretch;
    roomPermissionNoticeLayer.Background = new SolidColorBrush(Color.Parse("#A8111416"));
    roomPermissionNoticeLayer.IsVisible = false;
    roomPermissionNoticeLayer.Child = new Border
    {
        Width = 470.0,
        Padding = new Thickness(24.0),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.Parse("#1A2023")),
        BorderBrush = new SolidColorBrush(Color.Parse("#557075")),
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(7.0),
        Child = new StackPanel
        {
            Children =
            {
                (Control)roomPermissionNoticeTitle,
                (Control)roomPermissionNoticeText,
                (Control)roomPermissionNoticeDismissButton
            }
        }
    };
    AutomationProperties.SetAutomationId(roomPermissionNoticeLayer, "OnlineRoom.PermissionNotice");
    AutomationProperties.SetName(roomPermissionNoticeLayer, "房间权限提示");
    return roomPermissionNoticeLayer;
}

private void ShowRoomPermissionNotice(WindowsRoomPermissionNoticePresentation presentation)
{
    DesktopChartTab? activeTab = viewModel.ActiveTab;
    if (activeTab != null && activeTab.IsLibraryReadOnly)
    {
        viewModel.SetStatus("只读查看：编辑前请克隆到工作目录。播放、选择、复制、切换和关闭不受限制。");
        return;
    }

    roomPermissionNoticeTitle.Text = presentation.Title;
    roomPermissionNoticeText.Text = presentation.Message;
    AutomationProperties.SetName(roomPermissionNoticeLayer, presentation.Title + "：" + presentation.Message);
    roomPermissionNoticeLayer.IsVisible = true;
    roomPermissionNoticeDismissButton?.Focus();
}

private void CloseRoomPermissionNotice()
{
    roomPermissionNoticeLayer.IsVisible = false;
    timeline.Focus();
}

private Control BuildOnlineToastStack()
{
    StackPanel stackPanel = new StackPanel
    {
        Width = 360.0,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0.0, 50.0, 14.0, 0.0),
        Children =
        {
            BuildRoomInvitationToast(),
            BuildFollowNotificationToast()
        }
    };
    AutomationProperties.SetAutomationId(stackPanel, "Online.NotificationToastStack");
    AutomationProperties.SetName(stackPanel, "联机通知");
    return stackPanel;
}

private Control BuildRoomInvitationToast()
{
    roomInvitationTitleText.Foreground = new SolidColorBrush(Color.Parse("#E0E5E6"));
    roomInvitationTitleText.FontSize = 14.0;
    roomInvitationTitleText.FontWeight = FontWeight.DemiBold;
    roomInvitationTitleText.TextWrapping = TextWrapping.Wrap;
    roomInvitationTitleText.Margin = new Thickness(0.0, 6.0, 0.0, 0.0);
    AutomationProperties.SetAutomationId(roomInvitationTitleText, "OnlineInvitation.Toast.Title");
    roomInvitationDetailText.Foreground = new SolidColorBrush(Color.Parse("#879599"));
    roomInvitationDetailText.FontSize = 12.0;
    roomInvitationDetailText.TextWrapping = TextWrapping.Wrap;
    roomInvitationDetailText.Margin = new Thickness(0.0, 5.0, 0.0, 0.0);
    AutomationProperties.SetAutomationId(roomInvitationDetailText, "OnlineInvitation.Toast.Detail");
    roomInvitationCancelButton = PrimaryButton("拒绝", async (object? _, RoutedEventArgs _) =>
    {
        await RespondToPendingRoomInvitationAsync("cancel");
    });
    roomInvitationJoinButton = PrimaryButton("同意", async (object? _, RoutedEventArgs _) =>
    {
        await RespondToPendingRoomInvitationAsync("join");
    });
    AutomationProperties.SetAutomationId(roomInvitationCancelButton, "OnlineInvitation.Toast.Cancel");
    AutomationProperties.SetAutomationId(roomInvitationJoinButton, "OnlineInvitation.Toast.Accept");
    AutomationProperties.SetHelpText(roomInvitationJoinButton, "凭服务器邀请直接进入房间；隐藏密码不会显示，也不需要再次输入。");
    roomInvitationJoinButton.Background = AccentBrush;
    roomInvitationJoinButton.Foreground = Brushes.Black;
    roomInvitationJoinButton.BorderBrush = AccentBrush;
    roomInvitationJoinButton.Classes.Add("accent");
    roomInvitationCancelButton.Height = 34.0;
    roomInvitationJoinButton.Height = 34.0;
    roomInvitationCancelButton.HorizontalAlignment = HorizontalAlignment.Stretch;
    roomInvitationJoinButton.HorizontalAlignment = HorizontalAlignment.Stretch;
    roomInvitationCancelButton.Margin = new Thickness(0.0, 0.0, 4.0, 0.0);
    roomInvitationJoinButton.Margin = new Thickness(4.0, 0.0, 0.0, 0.0);
    Grid grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,*"),
        Margin = new Thickness(0.0, 12.0, 0.0, 0.0)
    };
    grid.Children.Add(roomInvitationCancelButton);
    Grid.SetColumn(roomInvitationJoinButton, 1);
    grid.Children.Add(roomInvitationJoinButton);
    roomInvitationToast.HorizontalAlignment = HorizontalAlignment.Stretch;
    roomInvitationToast.Margin = new Thickness(0.0, 0.0, 0.0, 10.0);
    roomInvitationToast.Padding = new Thickness(14.0);
    roomInvitationToast.Background = new SolidColorBrush(Color.Parse("#1B2023"));
    roomInvitationToast.BorderBrush = new SolidColorBrush(Color.Parse("#71999C"));
    roomInvitationToast.BorderThickness = new Thickness(1.0);
    roomInvitationToast.CornerRadius = new CornerRadius(6.0);
    roomInvitationToast.BoxShadow = default;
    roomInvitationToast.IsVisible = false;
    roomInvitationToast.Child = new StackPanel
    {
        Children =
        {
            (Control)new TextBlock
            {
                Text = "联机写谱邀请",
                Foreground = new SolidColorBrush(Color.Parse("#83AEB1")),
                FontSize = 10.0,
                FontWeight = FontWeight.DemiBold
            },
            (Control)roomInvitationTitleText,
            (Control)roomInvitationDetailText,
            (Control)grid
        }
    };
    AutomationProperties.SetAutomationId(roomInvitationToast, "OnlineInvitation.Toast");
    AutomationProperties.SetHelpText(roomInvitationToast, $"contract={"windows-online-surface-r8"}; width={360.0:0}; top={50.0:0}; right={14.0:0}; padding={14.0:0}; radius={6.0:0}; buttons={34.0:0}/{8.0:0}");
    return roomInvitationToast;
}

private Control BuildFollowNotificationToast()
{
    followNotificationToastText.Foreground = new SolidColorBrush(Color.Parse("#FFE0E7E6"));
    followNotificationToastText.FontSize = 12.0;
    followNotificationToastText.FontWeight = FontWeight.DemiBold;
    followNotificationToastText.TextWrapping = TextWrapping.Wrap;
    followNotificationToastText.VerticalAlignment = VerticalAlignment.Center;
    AutomationProperties.SetAutomationId(followNotificationToastText, "Online.FollowNotificationToast.Text");
    Grid grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*")
    };
    grid.Children.Add(new Ellipse { Width = 8.0, Height = 8.0, Fill = new SolidColorBrush(Color.Parse("#FF78AAA2")), Margin = new Thickness(0.0, 0.0, 10.0, 0.0), VerticalAlignment = VerticalAlignment.Center });
    Grid.SetColumn(followNotificationToastText, 1);
    grid.Children.Add(followNotificationToastText);
    followNotificationToast.Padding = new Thickness(14.0, 11.0);
    followNotificationToast.Background = new SolidColorBrush(Color.Parse("#F51B2023"));
    followNotificationToast.BorderBrush = new SolidColorBrush(Color.Parse("#FF5D8581"));
    followNotificationToast.BorderThickness = new Thickness(1.0);
    followNotificationToast.CornerRadius = new CornerRadius(6.0);
    followNotificationToast.IsHitTestVisible = false;
    followNotificationToast.IsVisible = false;
    followNotificationToast.Child = grid;
    AutomationProperties.SetAutomationId(followNotificationToast, "Online.FollowNotificationToast");
    AutomationProperties.SetName(followNotificationToast, "关注通知");
    UpdateFollowNotificationToastAutomation();
    return followNotificationToast;
}

private void OnlineAccountSession_NotificationsReceived(object? sender, OnlineUserNotificationsReceivedEventArgs eventArgs)
{
    if (!global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            OnlineAccountSession_NotificationsReceived(sender, eventArgs);
        });
    }
    else
    {
        if (windowCloseTeardownStarted || onlineAccountHost?.Session.CurrentUser?.Id != eventArgs.UserId)
        {
            return;
        }

        foreach (OnlineUserNotificationDto item in eventArgs.Notifications.OrderBy((OnlineUserNotificationDto item) => item.Id))
        {
            string text = (item.Type ?? string.Empty).ToLowerInvariant() switch
            {
                "follow" => WindowsTransientToastParityPolicy.FormatFollowNotification(item.ActorDisplayName),
                "room_invite_accepted" => AvaloniaLocalizationCatalog.Format("{0} 已同意加入“{1}”", item.ActorDisplayName, item.ContextText),
                "room_invite_rejected" => AvaloniaLocalizationCatalog.Format("{0} 已拒绝加入“{1}”", item.ActorDisplayName, item.ContextText),
                _ => string.Empty,
            };
            if (!string.IsNullOrWhiteSpace(text))
            {
                EnqueueOnlineNotificationToast(text);
            }
        }
    }
}

private void EnqueueFollowNotificationToast(string? actorDisplayName)
{
    EnqueueOnlineNotificationToast(WindowsTransientToastParityPolicy.FormatFollowNotification(actorDisplayName));
}

private void EnqueueOnlineNotificationToast(string message)
{
    followNotificationToastQueue.Enqueue(message);
    UpdateFollowNotificationToastAutomation();
    if (!followNotificationToastVisible)
    {
        ShowNextFollowNotificationToast();
    }
}

private void ShowNextFollowNotificationToast()
{
    if (!windowCloseTeardownStarted && followNotificationToastQueue.Count != 0)
    {
        followNotificationToastText.Text = followNotificationToastQueue.Dequeue();
        followNotificationToast.IsVisible = true;
        followNotificationToastVisible = true;
        followNotificationToastTimer.Stop();
        followNotificationToastTimer.Start();
        UpdateFollowNotificationToastAutomation();
    }
}

private void AdvanceFollowNotificationToast()
{
    followNotificationToastTimer.Stop();
    followNotificationToast.IsVisible = false;
    followNotificationToastVisible = false;
    UpdateFollowNotificationToastAutomation();
    ShowNextFollowNotificationToast();
}

private void ResetFollowNotificationToastUi()
{
    followNotificationToastTimer.Stop();
    followNotificationToastQueue.Clear();
    followNotificationToastText.Text = string.Empty;
    followNotificationToast.IsVisible = false;
    followNotificationToastVisible = false;
    UpdateFollowNotificationToastAutomation();
}

private void UpdateFollowNotificationToastAutomation()
{
    AutomationProperties.SetHelpText(followNotificationToast, $"Visible={followNotificationToast.IsVisible}; Message={followNotificationToastText.Text}; DurationMilliseconds={4000}; Width={360.0:R}; Top={50.0:R}; Right={14.0:R}; Pending={followNotificationToastQueue.Count}");
}

private Control BuildEditorToast()
{
    editorToastText.Text = "自动备份成功";
    editorToastText.Foreground = new SolidColorBrush(Color.Parse("#FFD9E4E3"));
    editorToastText.FontSize = 11.0;
    editorToastText.FontWeight = FontWeight.DemiBold;
    editorToastText.VerticalAlignment = VerticalAlignment.Center;
    AutomationProperties.SetAutomationId(editorToastText, "Editor.Toast.Text");
    editorToast.HorizontalAlignment = HorizontalAlignment.Right;
    editorToast.VerticalAlignment = VerticalAlignment.Bottom;
    editorToast.Margin = new Thickness(0.0, 0.0, 18.0, 18.0);
    editorToast.Padding = new Thickness(13.0, 9.0);
    editorToast.Background = new SolidColorBrush(Color.Parse("#F21C2425"));
    editorToast.BorderBrush = new SolidColorBrush(Color.Parse("#FF5D8581"));
    editorToast.BorderThickness = new Thickness(1.0);
    editorToast.CornerRadius = new CornerRadius(5.0);
    editorToast.IsHitTestVisible = false;
    editorToast.IsVisible = false;
    editorToast.Child = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Children =
        {
            (Control)new Border
            {
                Width = 7.0,
                Height = 7.0,
                Margin = new Thickness(0.0, 0.0, 9.0, 0.0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.Parse("#FF78AAA2")),
                CornerRadius = new CornerRadius(3.5)
            },
            (Control)editorToastText
        }
    };
    AutomationProperties.SetAutomationId(editorToast, "Editor.Toast");
    AutomationProperties.SetName(editorToast, "编辑器通知");
    UpdateEditorToastAutomation();
    return editorToast;
}

private void ShowEditorToast(string message)
{
    if (!windowCloseTeardownStarted && !windowClosePreflightComplete)
    {
        editorToastText.Text = message;
        editorToast.IsVisible = true;
        editorToastTimer.Stop();
        editorToastTimer.Start();
        UpdateEditorToastAutomation();
    }
}

private void HideEditorToast()
{
    editorToastTimer.Stop();
    editorToast.IsVisible = false;
    UpdateEditorToastAutomation();
}

private void UpdateEditorToastAutomation()
{
    AutomationProperties.SetHelpText(editorToast, $"Visible={editorToast.IsVisible}; Message={editorToastText.Text}; DurationMilliseconds={3000}; Right={18.0:R}; Bottom={18.0:R}");
}

private Control BuildTitleBar()
{
    bool flag = !SupportsDesktopWindowSizing;
    Grid bar = new Grid
    {
        ColumnDefinitions = (flag ? new ColumnDefinitions("Auto,Auto,*,Auto") : new ColumnDefinitions($"Auto,Auto,*,Auto,{46.0},{46.0},{46.0}"))
    };
    bar.PointerPressed += (object? _, PointerPressedEventArgs eventArgs) =>
    {
        TitleBar_PointerPressed(bar, eventArgs);
    };
    StackPanel content = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(flag ? 10 : 14, 0.0, 0.0, 0.0),
        Children =
        {
            (Control)new Border
            {
                Width = 20.0,
                Height = 20.0,
                BorderBrush = AccentBrush,
                BorderThickness = new Thickness(1.0),
                CornerRadius = new CornerRadius(3.0),
                Child = new global::Avalonia.Controls.Shapes.Path
                {
                    Width = 16.0,
                    Height = 16.0,
                    Data = Geometry.Parse("M4,14 L4,9 M8,14 L8,5 M12,14 L12,7 M16,14 L16,3"),
                    Stroke = new SolidColorBrush(Color.Parse("#B4C5C6")),
                    StrokeThickness = 1.5,
                    Stretch = Stretch.None,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            },
            (Control)new TextBlock
            {
                Text = "SimpleMania",
                FontSize = 13.0,
                FontWeight = FontWeight.DemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8.0, 0.0, 0.0, 0.0)
            },
            (Control)new TextBlock
            {
                Text = "  EDITOR",
                FontSize = 9.0,
                Foreground = new SolidColorBrush(Color.Parse("#777F84")),
                VerticalAlignment = VerticalAlignment.Center
            }
        }
    };
    Button button = new Button
    {
        Content = content,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0.0),
        Padding = new Thickness(0.0),
        Focusable = false
    };
    AutomationProperties.SetAutomationId(button, "Library.Home");
    ToolTip.SetTip(button, "返回曲库");
    button.Click += (object? _, RoutedEventArgs _) =>
    {
        ShowLibrary(focusSearch: true);
    };
    bar.Children.Add(button);
    titleUserNameText.Text = "Guest";
    titleUserNameText.FontSize = 11.0;
    titleUserNameText.FontWeight = FontWeight.DemiBold;
    titleUserNameText.Foreground = new SolidColorBrush(Color.Parse("#D6DCDE"));
    titleUserStateText.Text = "离线";
    titleUserStateText.FontSize = 8.0;
    titleUserStateText.Foreground = new SolidColorBrush(Color.Parse("#778388"));
    onlineAccountButton = TitleBarToolButton(string.Empty, async (object? _, RoutedEventArgs _) =>
    {
        await ShowOnlineAccountAsync();
    });
    onlineAccountButton.Margin = new Thickness(flag ? 8 : 16, 3.0, 0.0, 3.0);
    onlineAccountButton.Padding = new Thickness(flag ? 5 : 7, 2.0);
    onlineAccountButton.Content = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Children =
        {
            (Control)titleUserAvatar,
            (Control)new StackPanel
            {
                Margin = new Thickness(7.0, 0.0, 0.0, 0.0),
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    (Control)titleUserNameText,
                    (Control)titleUserStateText
                }
            }
        }
    };
    AutomationProperties.SetAutomationId(onlineAccountButton, "OnlineAccount.Navigation");
    AutomationProperties.SetName(onlineAccountButton, "账号与个人主页");
    Grid.SetColumn(onlineAccountButton, 1);
    bar.Children.Add(onlineAccountButton);
    Control control = BuildDocumentTabStrip(flag);
    Grid.SetColumn(control, 2);
    bar.Children.Add(control);
    titleSaveStateText.Text = "· 已保存";
    titleSaveStateText.Foreground = new SolidColorBrush(Color.Parse("#7FAE93"));
    titleSaveStateText.Margin = new Thickness(4.0, 0.0, 10.0, 0.0);
    titleSaveStateText.VerticalAlignment = VerticalAlignment.Center;
    AutomationProperties.SetAutomationId(titleSaveStateText, "Title.SaveState");
    Button button2 = TitleBarToolButton("好友", async (object? _, RoutedEventArgs _) =>
    {
        await ToggleFriendsPanelAsync();
    });
    AutomationProperties.SetAutomationId(button2, "Title.Friends");
    Button button3 = TitleBarToolButton("联机", async (object? _, RoutedEventArgs _) =>
    {
        await ShowOnlineAccountAsync(OnlineCommunityHostedSection.Rooms);
    });
    AutomationProperties.SetAutomationId(button3, "Title.Online");
    Button button4 = TitleBarToolButton("曲库", (object? _, RoutedEventArgs _) =>
    {
        ShowLibrary(focusSearch: true);
    });
    button4.Margin = new Thickness(0.0);
    AutomationProperties.SetAutomationId(button4, "Title.Library");
    StackPanel stackPanel = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0.0, 0.0, flag ? 4 : 8, 0.0),
        Children =
        {
            (Control)titleSaveStateText,
            (Control)button2,
            (Control)button3,
            (Control)button4
        }
    };
    if (!SimpleManiaProduct.OnlineEnabled)
    {
        button2.IsVisible = false;
        button3.IsVisible = false;
    }

    Grid.SetColumn(stackPanel, 3);
    bar.Children.Add(stackPanel);
    if (!flag)
    {
        Button button5 = CaptionButton("—", (object? _, RoutedEventArgs _) =>
        {
            WindowState = WindowState.Minimized;
        });
        AutomationProperties.SetAutomationId(button5, "Title.Minimize");
        Grid.SetColumn(button5, 4);
        bar.Children.Add(button5);
        Button button6 = CaptionButton("□", (object? _, RoutedEventArgs _) =>
        {
            ToggleMaximize();
        });
        AutomationProperties.SetAutomationId(button6, "Title.MaximizeRestore");
        Grid.SetColumn(button6, 5);
        bar.Children.Add(button6);
        Button button7 = CaptionButton("×", (object? _, RoutedEventArgs _) =>
        {
            Close();
        });
        AutomationProperties.SetAutomationId(button7, "Title.Close");
        Grid.SetColumn(button7, 6);
        bar.Children.Add(button7);
    }

    titleBar.Background = new SolidColorBrush(Color.Parse("#171A1D"));
    titleBar.BorderBrush = new SolidColorBrush(Color.Parse("#2C3135"));
    titleBar.BorderThickness = new Thickness(0.0, 0.0, 0.0, 1.0);
    titleBar.RenderTransform = titleBarTranslate;
    titleBar.Child = bar;
    titleBar.PointerEntered += (object? _, PointerEventArgs _) =>
    {
        if (WindowState == WindowState.FullScreen)
        {
            fullscreenTitleHideTimer.Stop();
        }
    };
    titleBar.PointerExited += (object? _, PointerEventArgs _) =>
    {
        if (WindowState == WindowState.FullScreen)
        {
            fullscreenTitleHideTimer.Start();
        }
    };
    AutomationProperties.SetAutomationId(titleBar, "Title.Chrome");
    UpdateTitleBarAutomationHelpText();
    titleBar.SetValue(Visual.ZIndexProperty, 205);
    return titleBar;
}

private void UpdateTitleBarAutomationHelpText()
{
    AutomationProperties.SetHelpText(titleBar, $"Contract={"wpf-1.1.0-titlebar-r1"}; Height={38.0}; CaptionWidth={46.0}; ResizeBorder={6.0}; Minimum={1080.0}x{680.0}; " + "SnapMode=ManualPhysicalEdge; " + $"SnapScheduled={windowMoveSnapScheduleCount}; " + $"SnapApplied={windowMoveSnapApplyCount}; " + $"SnapNoTarget={windowMoveSnapNoTargetCount}; " + "LastSnapOutcome=" + windowMoveSnapLastOutcome + "; LastSnapPointer=" + FormatWindowSnapPointer(windowMoveSnapLastPointer) + "; " + $"Reveal={145}; " + $"Hide={125}; " + "Easing=CubicOut/CubicIn; CurrentY=" + titleBarTranslate.Y.ToString("0.###", CultureInfo.InvariantCulture) + "; " + $"Animating={fullscreenTitleAnimationRunning}");
}

private void WindowResize_PointerPressed(object? sender, PointerPressedEventArgs eventArgs)
{
    if (CanResize && WindowState == WindowState.Normal && eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
    {
        WindowEdge? windowEdge = WindowsTitleBarParityPolicy.ResolveResizeEdge(eventArgs.GetPosition(this), Bounds.Size);
        if (windowEdge.HasValue)
        {
            BeginResizeDrag(windowEdge.Value, eventArgs);
            eventArgs.Handled = true;
        }
    }
}

private void TitleBar_PointerPressed(Control titleBarSurface, PointerPressedEventArgs eventArgs)
{
    if (!eventArgs.GetCurrentPoint(titleBarSurface).Properties.IsLeftButtonPressed || (eventArgs.Source is Control control && (control is Button || control.GetVisualAncestors().OfType<Button>().Any())))
    {
        return;
    }

    if (eventArgs.ClickCount >= 2)
    {
        activeWindowSnapTarget = null;
        windowSnapRestoreBounds = null;
        ToggleMaximize();
        eventArgs.Handled = true;
        return;
    }

    PixelRect? restoreBoundsBeforeMove = ((WindowState == WindowState.Normal) ? new PixelRect? (GetCurrentWindowPhysicalBounds()) : ((PixelRect? )null));
    WindowsWindowSnapTarget? windowsWindowSnapTarget = activeWindowSnapTarget;
    bool flag;
    if (windowsWindowSnapTarget.HasValue)
    {
        WindowsWindowSnapTarget valueOrDefault = windowsWindowSnapTarget.GetValueOrDefault();
        if ((uint)valueOrDefault <= 1u)
        {
            flag = true;
            goto IL_00bb;
        }
    }

    flag = false;
    goto IL_00bb;
    IL_00bb:
        if (flag)
        {
            PixelRect? pixelRect = windowSnapRestoreBounds;
            if (pixelRect.HasValue)
            {
                PixelRect valueOrDefault2 = pixelRect.GetValueOrDefault();
                if (!WindowsWindowSnapPolicy.TryGetCursorPosition(out var point))
                {
                    return;
                }

                PixelRect currentWindowPhysicalBounds = GetCurrentWindowPhysicalBounds();
                Screen screen = Screens?.ScreenFromPoint(point) ?? ScreenFromHost() ?? Screens?.Primary;
                if (screen != null)
                {
                    int titleOffsetPixels = Math.Max(0, point.Y - currentWindowPhysicalBounds.Y);
                    PixelRect pixelRect2 = WindowsWindowSnapPolicy.ResolveRestoreUnderPointer(point, currentWindowPhysicalBounds, valueOrDefault2.Size, screen.WorkingArea, titleOffsetPixels);
                    ApplyPhysicalWindowBounds(pixelRect2, screen.Scaling);
                    restoreBoundsBeforeMove = pixelRect2;
                }

                activeWindowSnapTarget = null;
            }
        }

    try
    {
        BeginMoveDrag(eventArgs);
        ScheduleWindowsSnapAfterMove(restoreBoundsBeforeMove);
        eventArgs.Handled = true;
    }
    catch
    {
    }
}

private void ScheduleWindowsSnapAfterMove(PixelRect? restoreBoundsBeforeMove)
{
    CancelPendingWindowSnapApplication();
    windowMoveSnapScheduleCount++;
    windowMoveSnapLastOutcome = "Scheduled";
    UpdateTitleBarAutomationHelpText();
    Stopwatch timeout = Stopwatch.StartNew();
    bool observedPressed = WindowsWindowSnapPolicy.IsPrimaryMouseButtonPressed();
    DispatcherTimer timer = null;
    timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16.0), DispatcherPriority.Input, (object? _, EventArgs _) =>
    {
        if (WindowsWindowSnapPolicy.IsPrimaryMouseButtonPressed())
        {
            observedPressed = true;
        }
        else if (WindowsWindowSnapPolicy.ShouldApplyAfterMove(observedPressed, isPressed: false, timeout.Elapsed))
        {
            timer.Stop();
            if (windowMoveCompletionTimer == timer)
            {
                windowMoveCompletionTimer = null;
            }

            ApplyWindowsSnapAfterMove(restoreBoundsBeforeMove);
        }
    });
    timer.Start();
    windowMoveCompletionTimer = timer;
}

private void CancelPendingWindowSnapApplication()
{
    windowMoveCompletionTimer?.Stop();
    windowMoveCompletionTimer = null;
}

private void ApplyWindowsSnapAfterMove(PixelRect? restoreBoundsBeforeMove)
{
    if (!WindowsWindowSnapPolicy.TryGetCursorPosition(out var point))
    {
        windowMoveSnapLastOutcome = "CursorUnavailable";
        UpdateTitleBarAutomationHelpText();
        return;
    }

    windowMoveSnapApplyCount++;
    windowMoveSnapLastPointer = point;
    Screen screen = Screens?.ScreenFromPoint(point) ?? ScreenFromHost() ?? Screens?.Primary;
    if (screen == null)
    {
        windowMoveSnapLastOutcome = "ScreenUnavailable";
        UpdateTitleBarAutomationHelpText();
        return;
    }

    WindowsWindowSnapTarget? windowsWindowSnapTarget = WindowsWindowSnapPolicy.ResolveTarget(point, screen.WorkingArea);
    if (windowsWindowSnapTarget == WindowsWindowSnapTarget.Maximize)
    {
        activeWindowSnapTarget = null;
        windowSnapRestoreBounds = null;
        WindowState = WindowState.Maximized;
        windowMoveSnapLastOutcome = "Maximize";
        UpdateTitleBarAutomationHelpText();
        return;
    }

    bool flag;
    if (windowsWindowSnapTarget.HasValue)
    {
        WindowsWindowSnapTarget valueOrDefault = windowsWindowSnapTarget.GetValueOrDefault();
        if ((uint)valueOrDefault <= 1u)
        {
            flag = true;
            goto IL_00ea;
        }
    }

    flag = false;
    goto IL_00ea;
    IL_00ea:
        if (flag)
        {
            windowSnapRestoreBounds = restoreBoundsBeforeMove ?? GetCurrentWindowPhysicalBounds();
            WindowsWindowSnapPlacement windowsWindowSnapPlacement = WindowsWindowSnapPolicy.ResolvePlacement(windowsWindowSnapTarget.Value, screen.WorkingArea, screen.Scaling, 1080.0, 680.0);
            ApplyPhysicalWindowBounds(new PixelRect(windowsWindowSnapPlacement.Position, windowsWindowSnapPlacement.PhysicalSize), screen.Scaling);
            activeWindowSnapTarget = windowsWindowSnapTarget;
            windowMoveSnapLastOutcome = windowsWindowSnapTarget.Value.ToString();
            UpdateTitleBarAutomationHelpText();
            return;
        }

    activeWindowSnapTarget = null;
    windowMoveSnapNoTargetCount++;
    windowMoveSnapLastOutcome = "None";
    if (WindowState == WindowState.Normal)
    {
        windowSnapRestoreBounds = GetCurrentWindowPhysicalBounds();
    }

    UpdateTitleBarAutomationHelpText();
}

private static string FormatWindowSnapPointer(PixelPoint? pointer)
{
    if (pointer.HasValue)
    {
        PixelPoint valueOrDefault = pointer.GetValueOrDefault();
        return $"{valueOrDefault.X},{valueOrDefault.Y}";
    }

    return "None";
}

private PixelRect GetCurrentWindowPhysicalBounds()
{
    if (WindowsWindowSnapPolicy.TryGetNativeWindowBounds(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, out var bounds))
    {
        return bounds;
    }

    double num = (ScreenFromHost() ?? Screens?.Primary)?.Scaling ?? 1.0;
    return new PixelRect(Position, checked(new PixelSize(Math.Max(1, (int)Math.Round(Bounds.Width * num)), Math.Max(1, (int)Math.Round(Bounds.Height * num)))));
}

private void ApplyPhysicalWindowBounds(PixelRect bounds, double scaling)
{
    if (!double.IsFinite(scaling) || scaling <= 0.0)
    {
        throw new ArgumentOutOfRangeException("scaling");
    }

    WindowState = WindowState.Normal;
    Width = (double)bounds.Width / scaling;
    Height = (double)bounds.Height / scaling;
    Position = bounds.Position;
    WindowsWindowSnapPolicy.TryApplyNativeWindowBounds(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, bounds);
}

private Control BuildNavigation()
{
    StackPanel stackPanel = new StackPanel
    {
        Spacing = 8.0,
        Margin = new Thickness(12.0)
    };
    stackPanel.Children.Add(new TextBlock { Text = "AVALONIA STUDIO", FontSize = 11.0, FontWeight = FontWeight.Bold, Foreground = AccentBrush, Margin = new Thickness(8.0, 8.0, 8.0, 14.0) });
    stackPanel.Children.Add(NavigationButton("曲库首页", (object? _, RoutedEventArgs _) =>
    {
        ShowLibrary();
    }));
    stackPanel.Children.Add(NavigationButton("新建谱面", async (object? _, RoutedEventArgs _) =>
    {
        await CreateChartAsync();
    }));
    stackPanel.Children.Add(NavigationButton("打开谱面", async (object? _, RoutedEventArgs _) =>
    {
        await PickAndOpenChartAsync();
    }));
    stackPanel.Children.Add(NavigationButton("打开项目", async (object? _, RoutedEventArgs _) =>
    {
        await PickAndOpenProjectAsync();
    }));
    stackPanel.Children.Add(NavigationButton("选择工作目录", async (object? _, RoutedEventArgs _) =>
    {
        await PickWorkspaceAsync();
    }));
    onlineAccountButton = NavigationButton((onlineAccountHost == null) ? "账号 · 配置错误" : "账号 · 登录", async (object? _, RoutedEventArgs _) =>
    {
        await ShowOnlineAccountAsync();
    });
    AutomationProperties.SetAutomationId(onlineAccountButton, "OnlineAccount.Navigation");
    stackPanel.Children.Add(onlineAccountButton);
    stackPanel.Children.Add(new Border { Height = 1.0, Background = StrokeBrush, Margin = new Thickness(4.0, 12.0) });
    stackPanel.Children.Add(new TextBlock { Text = "首个纵向片\n曲库 · MC/osu · 标签\n单控件时间轴 · 精确移动\n撤销 · 原格式保存 · 复核", Foreground = MutedBrush, FontSize = 12.0, LineHeight = 21.0, Margin = new Thickness(8.0) });
    Border border = new Border
    {
        Background = PanelBrush,
        BorderBrush = StrokeBrush,
        BorderThickness = new Thickness(0.0, 0.0, 1.0, 0.0),
        Child = stackPanel
    };
    Grid.SetColumn(border, 0);
    return border;
}

private void BuildLibraryView()
{
    libraryView.RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto");
    libraryView.Margin = new Thickness(0.0);
    Grid grid = new Grid
    {
        RowDefinitions = new RowDefinitions("Auto,Auto"),
        RowSpacing = 12.0
    };
    Thickness padding = new Thickness(20.0, 15.0, 20.0, 14.0);
    Thickness margin = new Thickness(4.0, 0.0);
    libraryTitle.Text = "曲库";
    libraryTitle.FontSize = 24.0;
    libraryTitle.FontWeight = FontWeight.DemiBold;
    libraryTitle.Foreground = new SolidColorBrush(Color.Parse("#E1E5E6"));
    libraryCountText.Text = "0 个文件夹";
    libraryCountText.Foreground = new SolidColorBrush(Color.Parse("#829094"));
    libraryCountText.Margin = new Thickness(12.0, 6.0, 0.0, 0.0);
    libraryCountText.VerticalAlignment = VerticalAlignment.Center;
    TextBlock item = new TextBlock
    {
        Text = "创作谱面按歌曲归组，点击展开难度并打开编辑。可添加只读 Songs 曲库；打开外部谱面时建立创作副本。",
        Foreground = new SolidColorBrush(Color.Parse("#7F898E")),
        FontSize = 11.0,
        TextWrapping = TextWrapping.Wrap
    };
    StackPanel stackPanel = new StackPanel
    {
        Orientation = Orientation.Horizontal
    };
    stackPanel.Children.Add(libraryTitle);
    stackPanel.Children.Add(libraryCountText);
    StackPanel item2 = new StackPanel
    {
        Spacing = 4.0,
        Children =
        {
            (Control)stackPanel,
            (Control)item
        }
    };
    workspaceText.Foreground = new SolidColorBrush(Color.Parse("#7E898E"));
    workspaceText.FontSize = 12.0;
    workspaceText.TextTrimming = TextTrimming.CharacterEllipsis;
    grid.Children.Add(item2);
    WrapPanel headerActions = new WrapPanel
    {
        Orientation = Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center
    };
    ApplyDangerButtonStyle(libraryDeleteSelectedButton);
    libraryDeleteSelectedButton.Content = "删除所选";
    libraryDeleteSelectedButton.IsEnabled = false;
    libraryDeleteSelectedButton.Padding = new Thickness(15.0, 8.0);
    libraryDeleteSelectedButton.Margin = margin;
    ToolTip.SetTip(libraryDeleteSelectedButton, "将选中的歌曲文件夹移入回收站（Ctrl / Shift 多选）");
    libraryDeleteSelectedButton.Click += async (object? _, RoutedEventArgs _) =>
    {
        await DeleteSelectedLibraryFoldersAsync();
    };
    AutomationProperties.SetAutomationId(libraryDeleteSelectedButton, "Library.DeleteSelected");
    AddHeaderAction(libraryDeleteSelectedButton);
    Button button = PrimaryButton("导入谱面 / 谱包", async (object? _, RoutedEventArgs _) =>
    {
        await PickAndOpenChartAsync();
    });
    ApplyWpfPrimaryButtonStyle(button);
    button.Padding = new Thickness(15.0, 8.0);
    button.Margin = margin;
    AutomationProperties.SetAutomationId(button, "Library.ImportChart");
    AddHeaderAction(button);
    Button button2 = PrimaryButton("导入音频", async (object? _, RoutedEventArgs _) =>
    {
        await CreateChartFromAudioAsync();
    });
    ApplyWpfPrimaryButtonStyle(button2);
    button2.Padding = new Thickness(15.0, 8.0);
    button2.Margin = margin;
    AutomationProperties.SetAutomationId(button2, "Library.ImportAudio");
    AddHeaderAction(button2);
    Button button3 = PrimaryButton("新建谱面", async (object? _, RoutedEventArgs _) =>
    {
        await CreateChartAsync();
    });
    ApplyWpfToolButtonStyle(button3);
    button3.Padding = new Thickness(15.0, 8.0);
    button3.Margin = margin;
    AutomationProperties.SetAutomationId(button3, "Library.NewChart");
    AddHeaderAction(button3);
    Button button4 = PrimaryButton("选择工作目录", async (object? _, RoutedEventArgs _) =>
    {
        await PickWorkspaceAsync();
    });
    ApplyWpfToolButtonStyle(button4);
    button4.Padding = new Thickness(15.0, 8.0);
    button4.Margin = margin;
    AutomationProperties.SetAutomationId(button4, "Library.ChooseWorkspace");
    IPlatformFileImportService? current = PlatformFileImportServices.Current;
    if (current == null || current.SupportsWorkspaceDirectorySelection)
    {
        AddHeaderAction(button4);
    }

    AddHeaderAction(BuildAddLibraryRootButton());
    AddHeaderAction(MapperButton("批量整理", "Library.OrganizeSelected", async () =>
    {
        await ShowBatchAnnotationsAsync();
    }));
    AddHeaderAction(MapperButton("★ 收藏", "Library.Favorites", () =>
    {
        librarySearchBox.Text = "star:true";
    }));
    AddHeaderAction(MapperButton("未发布", "Library.Unpublished", () =>
    {
        librarySearchBox.Text = "state:未发布";
    }));
    AddHeaderAction(MapperButton("迁移记录", "Library.TransferHistory", async () =>
    {
        await ShowTransferHistoryAsync();
    }));
    Button button5 = PrimaryButton("刷新索引", async (object? _, RoutedEventArgs _) =>
    {
        await RefreshLibraryWithErrorUiAsync();
    });
    ApplyWpfPrimaryButtonStyle(button5);
    button5.Padding = new Thickness(15.0, 8.0);
    button5.Margin = margin;
    AutomationProperties.SetAutomationId(button5, "Library.Refresh");
    AddHeaderAction(button5);
    Grid.SetRow(headerActions, 1);
    grid.Children.Add(headerActions);
    libraryView.Children.Add(new Border { Padding = padding, Background = new SolidColorBrush(Color.Parse("#171A1D")), BorderBrush = new SolidColorBrush(Color.Parse("#30363A")), BorderThickness = new Thickness(0.0, 0.0, 0.0, 1.0), Child = grid });
    Grid grid2 = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,240,200"),
        ColumnSpacing = 0.0,
        Margin = WindowsLibraryLayoutPolicy.FilterMargin
    };
    librarySearchBox.PlaceholderText = string.Empty;
    librarySearchBox.Background = Brushes.Transparent;
    librarySearchBox.Foreground = new SolidColorBrush(Color.Parse("#E0E4E5"));
    librarySearchBox.CaretBrush = new SolidColorBrush(Color.Parse("#9EB8BA"));
    librarySearchBox.BorderThickness = new Thickness(0.0);
    librarySearchBox.Padding = new Thickness(0.0, 9.0);
    librarySearchBox.Margin = new Thickness(40.0, 0.0, 10.0, 0.0);
    librarySearchBox.FontSize = 13.0;
    librarySearchBox.VerticalContentAlignment = VerticalAlignment.Center;
    AutomationProperties.SetAutomationId(librarySearchBox, "Library.Search");
    AutomationProperties.SetName(librarySearchBox, "搜索曲库");
    AutomationProperties.SetHelpText(librarySearchBox, "搜索标题、原文、作者、版本或路径；中文输入法组合文本按正常失焦提交，不依赖 Enter。");
    librarySearchBox.PlaceholderText = "搜索曲名 / 备注 · tag:LN state:未发布 keys:7 star:true";
    DispatcherTimer searchDelay = new DispatcherTimer
    {
        Interval = TimeSpan.FromMilliseconds(180.0)
    };
    searchDelay.Tick += async (object? _, EventArgs _) =>
    {
        searchDelay.Stop();
        try
        {
            await viewModel.SetLibrarySearchAsync(librarySearchBox.Text);
        }
        catch (Exception ex)
        {
            viewModel.SetStatus("搜索失败：" + ex.Message);
        }
    };
    librarySearchBox.TextChanged += (object? _, TextChangedEventArgs _) =>
    {
        searchDelay.Stop();
        searchDelay.Start();
    };
    Border item3 = new Border
    {
        Margin = new Thickness(0.0, 0.0, 10.0, 12.0),
        Background = new SolidColorBrush(Color.Parse("#1A1E21")),
        BorderBrush = new SolidColorBrush(Color.Parse("#343B3F")),
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(4.0),
        Child = new Grid
        {
            Children =
            {
                (Control)new TextBlock
                {
                    Text = "⌕",
                    FontSize = 19.0,
                    Foreground = new SolidColorBrush(Color.Parse("#849095")),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12.0, 0.0, 0.0, 1.0),
                    IsHitTestVisible = false
                },
                (Control)librarySearchBox
            }
        }
    };
    grid2.Children.Add(item3);
    LibrarySortChoice[] array = new LibrarySortChoice[4]
    {
        new LibrarySortChoice("添加时间：新到旧", DesktopLibrarySort.AddedDescending),
        new LibrarySortChoice("添加时间：旧到新", DesktopLibrarySort.AddedAscending),
        new LibrarySortChoice("标题：A → Z", DesktopLibrarySort.TitleAscending),
        new LibrarySortChoice("标题：Z → A", DesktopLibrarySort.TitleDescending)
    };
    librarySortBox.ItemsSource = array;
    librarySortBox.SelectedItem = array.First((LibrarySortChoice choice) => choice.Sort == viewModel.LibrarySort);
    librarySortBox.Background = new SolidColorBrush(Color.Parse("#15181A"));
    librarySortBox.Foreground = new SolidColorBrush(Color.Parse("#E3E6E8"));
    librarySortBox.BorderBrush = new SolidColorBrush(Color.Parse("#383E43"));
    librarySortBox.BorderThickness = new Thickness(1.0);
    librarySortBox.Padding = new Thickness(10.0, 7.0);
    librarySortBox.Margin = new Thickness(0.0, 0.0, 0.0, 12.0);
    librarySortBox.HorizontalAlignment = HorizontalAlignment.Stretch;
    librarySortBox.SelectionChanged += (object? _, SelectionChangedEventArgs _) =>
    {
        if (librarySortBox.SelectedItem is LibrarySortChoice librarySortChoice)
        {
            viewModel.SetLibrarySort(librarySortChoice.Sort);
        }
    };
    Grid.SetColumn(librarySortBox, 2);
    Control control = BuildLibrarySourcePicker();
    Grid.SetColumn(control, 1);
    grid2.Children.Add(control);
    grid2.Children.Add(librarySortBox);
    Grid.SetRow(grid2, 1);
    libraryView.Children.Add(grid2);
    libraryList.Margin = WindowsLibraryLayoutPolicy.ContentMargin;
    libraryList.Background = Brushes.Transparent;
    libraryList.BorderThickness = new Thickness(0.0);
    libraryList.SelectionMode = SelectionMode.Multiple;
    ScrollViewer.SetBringIntoViewOnFocusChange(libraryList, value: false);
    ConfigureLibraryListItemStyles();
    AutomationProperties.SetAutomationId(libraryList, "Library.List");
    libraryList.ContainerPrepared += (object? _, ContainerPreparedEventArgs eventArgs) =>
    {
        ConfigureLibraryListItemAutomation(eventArgs.Container, eventArgs.Index);
        QueueDisplayScaleMaterializedContentScan();
    };
    libraryList.ContextRequested += LibraryList_ContextRequested;
    libraryList.SelectionChanged += (object? _, SelectionChangedEventArgs _) =>
    {
        UpdateLibrarySelectionUi();
    };
    libraryList.KeyDown += async (object? _, KeyEventArgs eventArgs) =>
    {
        if (eventArgs.Key == Key.Delete && eventArgs.KeyModifiers == KeyModifiers.None)
        {
            eventArgs.Handled = true;
            await DeleteSelectedLibraryFoldersAsync();
        }
    };
    Grid.SetRow(libraryList, 2);
    libraryView.Children.Add(libraryList);
    StackPanel stackPanel2 = new StackPanel
    {
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    emptyLibraryTitleText.Text = "开始创作 osu!mania 谱面";
    emptyLibraryTitleText.FontSize = 22.0;
    emptyLibraryTitleText.FontWeight = FontWeight.DemiBold;
    emptyLibraryTitleText.Foreground = new SolidColorBrush(Color.Parse("#E1E6E7"));
    emptyLibraryTitleText.HorizontalAlignment = HorizontalAlignment.Center;
    AutomationProperties.SetAutomationId(emptyLibraryTitleText, "Library.EmptyTitle");
    stackPanel2.Children.Add(emptyLibraryTitleText);
    emptyLibrarySubtitleText.Text = "打开现有 .osu 谱面，或新建一个 7K 项目。";
    emptyLibrarySubtitleText.Foreground = new SolidColorBrush(Color.Parse("#829095"));
    emptyLibrarySubtitleText.HorizontalAlignment = HorizontalAlignment.Center;
    emptyLibrarySubtitleText.Margin = new Thickness(0.0, 7.0, 0.0, 24.0);
    AutomationProperties.SetAutomationId(emptyLibrarySubtitleText, "Library.EmptySubtitle");
    stackPanel2.Children.Add(emptyLibrarySubtitleText);
    emptyLibraryActions.Orientation = Orientation.Vertical;
    emptyLibraryActions.Spacing = 0.0;
    emptyLibraryActions.HorizontalAlignment = HorizontalAlignment.Stretch;
    Button button6 = LibraryLauncherButton("＋", "创建新项目", "在当前工作目录创建新谱面，并立即进入编辑", async (object? _, RoutedEventArgs _) =>
    {
        await CreateChartAsync();
    }, primary: true, new Thickness(0.0, 0.0, 0.0, 9.0));
    AutomationProperties.SetAutomationId(button6, "Library.CreateProject");
    emptyLibraryActions.Children.Add(button6);
    Button button7 = LibraryLauncherButton("▱", "打开项目", "选择具体谱面目录；目录内的 MC / OSU 会作为标签打开", async (object? _, RoutedEventArgs _) =>
    {
        await PickAndOpenProjectAsync();
    }, primary: false, new Thickness(0.0, 0.0, 0.0, 9.0));
    AutomationProperties.SetAutomationId(button7, "Library.OpenProject");
    emptyLibraryActions.Children.Add(button7);
    Button button8 = LibraryLauncherButton("…", "选择自定义项目目录", "切换曲库工作目录，并在这里索引其中的谱面", async (object? _, RoutedEventArgs _) =>
    {
        await PickWorkspaceAsync();
    }, primary: false, new Thickness(0.0));
    AutomationProperties.SetAutomationId(button8, "Library.EmptyChooseWorkspace");
    IPlatformFileImportService? current2 = PlatformFileImportServices.Current;
    if (current2 == null || current2.SupportsWorkspaceDirectorySelection)
    {
        emptyLibraryActions.Children.Add(button8);
    }

    AutomationProperties.SetAutomationId(emptyLibraryActions, "Library.LauncherActions");
    stackPanel2.Children.Add(emptyLibraryActions);
    emptyLibraryPanel.Background = Brushes.Transparent;
    emptyLibraryPanel.BorderThickness = new Thickness(0.0);
    emptyLibraryPanel.Padding = new Thickness(0.0);
    emptyLibraryPanel.Margin = WindowsLibraryLayoutPolicy.ContentMargin;
    emptyLibraryPanel.Child = new Border
    {
        Width = 520.0,
        Padding = new Thickness(26.0),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.Parse("#171B1E")),
        BorderBrush = new SolidColorBrush(Color.Parse("#343D41")),
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(8.0),
        Child = stackPanel2
    };
    AutomationProperties.SetAutomationId(emptyLibraryPanel, "Library.EmptyPanel");
    Grid.SetRow(emptyLibraryPanel, 2);
    libraryView.Children.Add(emptyLibraryPanel);
    libraryStatusText.Foreground = new SolidColorBrush(Color.Parse("#A6B5B7"));
    libraryStatusText.HorizontalAlignment = HorizontalAlignment.Right;
    libraryStatusText.TextTrimming = TextTrimming.CharacterEllipsis;
    AutomationProperties.SetAutomationId(libraryStatusText, "Library.Status");
    AutomationProperties.SetAutomationId(workspaceText, "Library.WorkspacePath");
    Grid child = new Grid
    {
        Children =
        {
            (Control)workspaceText,
            (Control)libraryStatusText
        }
    };
    Border border = new Border
    {
        Background = new SolidColorBrush(Color.Parse("#171A1D")),
        BorderBrush = new SolidColorBrush(Color.Parse("#2E3438")),
        BorderThickness = new Thickness(0.0, 1.0, 0.0, 0.0),
        Padding = new Thickness(20.0, 9.0),
        Child = child
    };
    Grid.SetRow(border, 3);
    libraryView.Children.Add(border);
    void AddHeaderAction(Button action)
    {
        headerActions.Children.Add(action);
    }
}

private ContextMenu CreateLibraryFolderContextMenu()
{
    MenuItem deleteMenuItem = new MenuItem
    {
        Header = "删除此歌曲文件夹",
        Foreground = new SolidColorBrush(Color.Parse("#FFD4D4")),
        Background = Brushes.Transparent,
        Padding = new Thickness(13.0, 8.0),
        MinWidth = 190.0
    };
    deleteMenuItem.Classes.Add("danger");
    AutomationProperties.SetAutomationId(deleteMenuItem, "Library.DeleteContextMenuItem");
    ContextMenu contextMenu = new ContextMenu();
    contextMenu.ItemsSource = new MenuItem[1]
    {
        deleteMenuItem
    };
    contextMenu.Background = new SolidColorBrush(Color.Parse("#1A1E21"));
    contextMenu.Foreground = new SolidColorBrush(Color.Parse("#E7EAEB"));
    contextMenu.BorderBrush = new SolidColorBrush(Color.Parse("#3A4246"));
    contextMenu.BorderThickness = new Thickness(1.0);
    contextMenu.Padding = new Thickness(4.0);
    ContextMenu contextMenu2 = contextMenu;
    AutomationProperties.SetAutomationId(contextMenu2, "Library.FolderContextMenu");
    popupScaleController.Attach(contextMenu2);
    contextMenu2.Opening += (object? _, CancelEventArgs eventArgs) =>
    {
        ListBoxItem listBoxItem = pendingLibraryContextMenuItem;
        if (listBoxItem == null || libraryList.IndexFromContainer(listBoxItem) < 0)
        {
            pendingLibraryContextMenuItem = null;
            eventArgs.Cancel = true;
        }
        else
        {
            int index = libraryList.IndexFromContainer(listBoxItem);
            int num = ((!libraryList.Selection.IsSelected(index)) ? 1 : (libraryList.SelectedItems?.OfType<DesktopLibraryFolderView>().Count() ?? 0));
            deleteMenuItem.Header = ((num > 1) ? AvaloniaLocalizationCatalog.Format("删除所选 {0} 个歌曲文件夹", num) : "删除此歌曲文件夹");
        }
    };
    contextMenu2.Closed += (object? _, RoutedEventArgs _) =>
    {
        pendingLibraryContextMenuItem = null;
    };
    deleteMenuItem.Click += async (object? _, RoutedEventArgs _) =>
    {
        await DeleteSelectedLibraryFoldersAsync();
    };
    return contextMenu2;
}

private ContextMenu CreateLibraryChartContextMenu(DesktopLibraryChartEntry chart)
{
    MenuItem menuItem = new MenuItem
    {
        Header = "在文件资源管理器中打开",
        Background = Brushes.Transparent,
        Foreground = new SolidColorBrush(Color.Parse("#E7EAEB")),
        Padding = new Thickness(13.0, 8.0),
        MinWidth = 190.0
    };
    AutomationProperties.SetAutomationId(menuItem, "Library.ChartReveal");
    menuItem.Click += (object? _, RoutedEventArgs _) =>
    {
        RevealLibraryChart(chart);
    };
    menuItem.IsVisible = OperatingSystem.IsWindows();
    MenuItem menuItem2 = new MenuItem
    {
        Header = "删除此谱面",
        Background = Brushes.Transparent,
        Foreground = new SolidColorBrush(Color.Parse("#FFD4D4")),
        Padding = new Thickness(13.0, 8.0),
        MinWidth = 190.0
    };
    menuItem2.Classes.Add("danger");
    AutomationProperties.SetAutomationId(menuItem2, "Library.ChartDelete");
    menuItem2.Click += async (object? _, RoutedEventArgs _) =>
    {
        await DeleteLibraryChartAsync(chart);
    };
    ContextMenu contextMenu = new ContextMenu();
    contextMenu.ItemsSource = new object[4]
    {
        new MenuItem
        {
            Header = "标签 / 备注 / 迁移…",
            Command = new MapperActionCommand(async () =>
            {
                await ShowLibraryChartDetailsAsync(chart);
            })
        },
        menuItem,
        new Separator(),
        menuItem2
    };
    contextMenu.Background = new SolidColorBrush(Color.Parse("#1A1E21"));
    contextMenu.Foreground = new SolidColorBrush(Color.Parse("#E7EAEB"));
    contextMenu.BorderBrush = new SolidColorBrush(Color.Parse("#3A4246"));
    contextMenu.BorderThickness = new Thickness(1.0);
    contextMenu.Padding = new Thickness(4.0);
    ContextMenu contextMenu2 = contextMenu;
    AutomationProperties.SetAutomationId(contextMenu2, "Library.ChartContextMenu");
    popupScaleController.Attach(contextMenu2);
    return contextMenu2;
}

private void RevealLibraryChart(DesktopLibraryChartEntry chart)
{
    try
    {
        string fullPath = System.IO.Path.GetFullPath(chart.ChartPath);
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { "/select,", fullPath } });
    }
    catch (Exception ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("无法在文件资源管理器中打开谱面：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private async Task DeleteLibraryChartAsync(DesktopLibraryChartEntry chart)
{
    if (libraryDeletionInProgress)
    {
        return;
    }

    bool flag = IsLibraryDeletionPermanent(chart.ChartPath);
    DesktopLibraryChartDeletionPlan plan = DesktopLibraryChartDeletionPlanner.Create(viewModel.ResolvedWorkspaceRoot, chart, CaptureLibraryOpenDocuments(), flag);
    if (!plan.CanExecute)
    {
        LibraryDeleteConfirmationDialog libraryDeleteConfirmationDialog = new LibraryDeleteConfirmationDialog(plan.BlockMessage ?? "当前谱面不能移入回收站。", displayScaleSettings.UiScale, displayScaleSettings.FontScale, (plan.BlockReason == DesktopLibraryChartDeletionBlockReason.OpenDocument) ? "无法删除" : "删除谱面", LibraryDeleteDialogTone.Warning, LibraryDeleteDialogButtons.Confirm, flag);
        lastLibraryDeleteDialogScale = libraryDeleteConfirmationDialog.ScaleDiagnostics;
        await libraryDeleteConfirmationDialog.ShowDialog<LibraryDeleteChoice>(this);
        return;
    }

    LibraryDeleteConfirmationDialog libraryDeleteConfirmationDialog2 = new LibraryDeleteConfirmationDialog(plan.ConfirmationMessage ?? string.Empty, displayScaleSettings.UiScale, displayScaleSettings.FontScale, "删除谱面", LibraryDeleteDialogTone.Question, LibraryDeleteDialogButtons.CancelRecycle, plan.PermanentDeletion);
    lastLibraryDeleteDialogScale = libraryDeleteConfirmationDialog2.ScaleDiagnostics;
    if (await libraryDeleteConfirmationDialog2.ShowDialog<LibraryDeleteChoice>(this) != LibraryDeleteChoice.Recycle)
    {
        return;
    }

    DesktopLibraryChartDeletionPlan desktopLibraryChartDeletionPlan = DesktopLibraryChartDeletionPlanner.Create(viewModel.ResolvedWorkspaceRoot, chart, CaptureLibraryOpenDocuments(), IsLibraryDeletionPermanent(chart.ChartPath));
    if (desktopLibraryChartDeletionPlan.CanExecute && desktopLibraryChartDeletionPlan.PermanentDeletion == plan.PermanentDeletion && string.Equals(desktopLibraryChartDeletionPlan.FullPath, plan.FullPath, StringComparison.OrdinalIgnoreCase))
    {
        libraryDeletionInProgress = true;
        UpdateLibrarySelectionUi();
        viewModel.CancelLibraryScan();
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("正在删除 {0}…", chart.PickerTitle));
        nint ownerWindow = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        string error = null;
        try
        {
            await Task.Run(() =>
            {
                try
                {
                    if (File.Exists(plan.FullPath))
                    {
                        WindowsRecycleBinService.MoveToRecycleBin(plan.FullPath, ownerWindow);
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
            });
            await RefreshLibraryWithErrorUiAsync();
            MainWindowViewModel mainWindowViewModel = viewModel;
            string status;
            if (error != null)
            {
                status = AvaloniaLocalizationCatalog.Format("{0} 未能删除", chart.PickerTitle);
            }
            else
            {
                status = (plan.PermanentDeletion ? AvaloniaLocalizationCatalog.Format("已永久删除 {0}", chart.PickerTitle) : AvaloniaLocalizationCatalog.Format("已将 {0} 移入回收站", chart.PickerTitle));
            }

            mainWindowViewModel.SetStatus(status);
            if (error != null)
            {
                LibraryDeleteConfirmationDialog libraryDeleteConfirmationDialog3 = new LibraryDeleteConfirmationDialog(AvaloniaLocalizationCatalog.Format("谱面未能删除：\n\n{0}", error), displayScaleSettings.UiScale, displayScaleSettings.FontScale, "删除失败", LibraryDeleteDialogTone.Warning, LibraryDeleteDialogButtons.Confirm, plan.PermanentDeletion);
                lastLibraryDeleteDialogScale = libraryDeleteConfirmationDialog3.ScaleDiagnostics;
                await libraryDeleteConfirmationDialog3.ShowDialog<LibraryDeleteChoice>(this);
            }

            return;
        }
        finally
        {
            libraryDeletionInProgress = false;
            UpdateLibrarySelectionUi();
        }
    }

    await DeleteLibraryChartAsync(chart);
}

private void AttachLibraryCardLongPress(Control host, DesktopLibraryFolderView view)
{
    if (libraryLongPressTimer == null)
    {
        libraryLongPressTimer = CreateLibraryLongPressTimer();
    }

    host.AddHandler(InputElement.PointerPressedEvent, (object? _, PointerPressedEventArgs eventArgs) =>
    {
        if (eventArgs.Pointer.Type != PointerType.Mouse)
        {
            libraryLongPressFolder = view;
            libraryLongPressOrigin = eventArgs.GetPosition(host);
            libraryLongPressHost = host;
            libraryLongPressTimer?.Start();
        }
    }, RoutingStrategies.Tunnel);
    host.AddHandler(InputElement.PointerMovedEvent, (object? _, PointerEventArgs eventArgs) =>
    {
        if (!(libraryLongPressFolder == null) && libraryLongPressHost != null)
        {
            Point position = eventArgs.GetPosition(libraryLongPressHost);
            if (Math.Abs(position.X - libraryLongPressOrigin.X) > 12.0 || Math.Abs(position.Y - libraryLongPressOrigin.Y) > 12.0)
            {
                CancelLibraryLongPress();
            }
        }
    }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    host.AddHandler(InputElement.PointerReleasedEvent, (object? _, PointerReleasedEventArgs _) =>
    {
        CancelLibraryLongPress();
    }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    host.AddHandler(InputElement.PointerCaptureLostEvent, (object? _, PointerCaptureLostEventArgs _) =>
    {
        CancelLibraryLongPress();
    }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
}

private DispatcherTimer CreateLibraryLongPressTimer()
{
    DispatcherTimer dispatcherTimer = new DispatcherTimer();
    dispatcherTimer.Interval = TimeSpan.FromMilliseconds(500.0);
    dispatcherTimer.Tick += (object? _, EventArgs _) =>
    {
        DesktopLibraryFolderView folder = libraryLongPressFolder;
        Control control = libraryLongPressHost;
        CancelLibraryLongPress();
        if (!(folder == null) && control != null)
        {
            MenuItem menuItem = new MenuItem
            {
                Header = "删除",
                Foreground = new SolidColorBrush(Color.Parse("#FF8A8A")),
                Background = Brushes.Transparent,
                Padding = new Thickness(13.0, 8.0),
                MinWidth = 140.0
            };
            menuItem.Classes.Add("danger");
            AutomationProperties.SetAutomationId(menuItem, "Library.TabletDelete");
            menuItem.Click += async (object? obj, RoutedEventArgs e) =>
            {
                await DeleteLibraryFoldersAsync(new DesktopLibraryFolderEntry[]
{
    folder.Folder
});
            };
            ContextMenu contextMenu = new ContextMenu
            {
                ItemsSource = new MenuItem[1]
                {
                    menuItem
                },
                Background = new SolidColorBrush(Color.Parse("#1A1E21")),
                Foreground = new SolidColorBrush(Color.Parse("#E7EAEB")),
                BorderBrush = new SolidColorBrush(Color.Parse("#3A4246")),
                BorderThickness = new Thickness(1.0),
                Padding = new Thickness(4.0)
            };
            popupScaleController.Attach(contextMenu);
            contextMenu.Open(control);
        }
    };
    return dispatcherTimer;
}

private void CancelLibraryLongPress()
{
    libraryLongPressTimer?.Stop();
    libraryLongPressFolder = null;
    libraryLongPressHost = null;
}

private void LibraryList_ContextRequested(object? sender, ContextRequestedEventArgs eventArgs)
{
    bool flag = eventArgs.TryGetPosition(libraryList, out var _);
    ListBoxItem listBoxItem = (eventArgs.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
    if (listBoxItem == null && !flag && eventArgs.Source == libraryList && libraryList.SelectedItem != null)
    {
        listBoxItem = libraryList.ContainerFromItem(libraryList.SelectedItem) as ListBoxItem;
    }

    int num = ((listBoxItem == null) ? (-1) : libraryList.IndexFromContainer(listBoxItem));
    pendingLibraryContextMenuItem = ((num >= 0) ? listBoxItem : null);
    if (flag && num < 0)
    {
        eventArgs.Handled = true;
    }
    else if (num >= 0 && !libraryList.Selection.IsSelected(num))
    {
        libraryList.SelectedIndex = num;
    }
}

private void ConfigureLibraryListItemStyles()
{
    ControlTheme controlTheme = new ControlTheme(typeof(ListBoxItem));
    controlTheme.Setters.Add(new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<ListBoxItem>((ListBoxItem _, INameScope _) =>
    {
        LibraryFolderCard libraryFolderCard = new LibraryFolderCard(OpenChartAsync, CreateLibraryChartContextMenu, libraryFolderExpansionState, null, ShowLibraryChartDetailsAsync)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        libraryFolderCard.Bind(StyledElement.DataContextProperty, new TemplateBinding(ContentControl.ContentProperty));
        Border border = new Border();
        border.Child = libraryFolderCard;
        border.Bind(Border.BackgroundProperty, new TemplateBinding(TemplatedControl.BackgroundProperty));
        border.Bind(Border.BorderBrushProperty, new TemplateBinding(TemplatedControl.BorderBrushProperty));
        border.Bind(Border.BorderThicknessProperty, new TemplateBinding(TemplatedControl.BorderThicknessProperty));
        border.Bind(Border.CornerRadiusProperty, new TemplateBinding(TemplatedControl.CornerRadiusProperty));
        border.Bind(Decorator.PaddingProperty, new TemplateBinding(TemplatedControl.PaddingProperty));
        return border;
    })));
    libraryList.ItemContainerTheme = controlTheme;
    Style style = new Style((Selector? selector) => selector.OfType<ListBoxItem>());
    style.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent));
    style.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, new SolidColorBrush(Color.Parse("#34393D"))));
    style.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1.0)));
    style.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(3.0)));
    style.Setters.Add(new Setter(TemplatedControl.PaddingProperty, WindowsLibraryLayoutPolicy.FolderItemPadding));
    style.Setters.Add(new Setter(Layoutable.MarginProperty, new Thickness(0.0, 0.0, 0.0, 6.0)));
    style.Setters.Add(new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
    libraryList.Styles.Add(style);
    Style style2 = new Style((Selector? selector) => selector.OfType<ListBoxItem>().Class(":pointerover"));
    style2.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.Parse("#222629"))));
    libraryList.Styles.Add(style2);
    Style style3 = new Style((Selector? selector) => selector.OfType<ListBoxItem>().Class(":selected"));
    style3.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.Parse("#293235"))));
    style3.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, new SolidColorBrush(Color.Parse("#698B8E"))));
    libraryList.Styles.Add(style3);
}

private void ConfigureLibraryListItemAutomation(Control container, int index)
{
    if (container is ListBoxItem listBoxItem)
    {
        DesktopLibraryFolderView desktopLibraryFolderView = listBoxItem.DataContext as DesktopLibraryFolderView;
        if (desktopLibraryFolderView == null && index >= 0 && index < viewModel.LibraryFolders.Count)
        {
            desktopLibraryFolderView = viewModel.LibraryFolders[index];
        }

        if (!(desktopLibraryFolderView == null))
        {
            AutomationProperties.SetAutomationId(listBoxItem, "Library.FolderItem");
            AutomationProperties.SetName(listBoxItem, desktopLibraryFolderView.Folder.DisplayTitle + " · " + desktopLibraryFolderView.Folder.RelativeFolderPath);
        }
    }
}

private void UpdateLibrarySelectionUi()
{
    int num = libraryList.SelectedItems?.OfType<DesktopLibraryFolderView>().Count() ?? 0;
    libraryDeleteSelectedButton.Content = ((num > 0) ? AvaloniaLocalizationCatalog.Format("删除所选  {0}", num) : "删除所选");
    libraryDeleteSelectedButton.IsEnabled = num > 0 && !libraryDeletionInProgress;
}

private DesktopLibraryOpenDocumentReference[] CaptureLibraryOpenDocuments()
{
    return viewModel.Tabs.Select((DesktopChartTab tab) => new DesktopLibraryOpenDocumentReference(tab.DisplayTitle, (
        from path in new string[3] { tab.SourcePath, tab.WorkingPath, tab.Session.Document.FilePath }where !string.IsNullOrWhiteSpace(path)select (path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();
}

private bool IsLibraryDeletionPermanent(string path)
{
    if (SupportsDesktopWindowSizing)
    {
        return WindowsRecycleBinService.IsLikelyPermanentDeletion(path);
    }

    return true;
}

private bool IsLibraryFolderDeletionPermanent(IReadOnlyList<DesktopLibraryFolderEntry> folders)
{
    if (SupportsDesktopWindowSizing)
    {
        return folders.Any((DesktopLibraryFolderEntry folder) => WindowsRecycleBinService.IsLikelyPermanentDeletion(folder.FolderPath));
    }

    return true;
}

private Task DeleteSelectedLibraryFoldersAsync()
{
    return DeleteLibraryFoldersAsync(libraryList.SelectedItems?.OfType<DesktopLibraryFolderView>().Select((DesktopLibraryFolderView view) => view.Folder).ToArray() ?? Array.Empty<DesktopLibraryFolderEntry>());
}

private async Task DeleteLibraryFoldersAsync(IReadOnlyList<DesktopLibraryFolderEntry> selectedFolders)
{
    if (libraryDeletionInProgress)
    {
        return;
    }

    bool flag = IsLibraryFolderDeletionPermanent(selectedFolders);
    DesktopLibraryDeletionPlan plan = DesktopLibraryDeletionPlanner.Create(viewModel.ResolvedWorkspaceRoot, selectedFolders, CaptureLibraryOpenDocuments(), flag);
    lastLibraryDeletionPlan = plan;
    lastLibraryRecycleResults = Array.Empty<WindowsRecycleBinOperationResult>();
    lastLibraryRecycleFailures = Array.Empty<string>();
    lastLibraryDeletionConfirmed = false;
    lastLibraryDeletionCanceled = false;
    if (plan.BlockReason == DesktopLibraryDeletionBlockReason.EmptySelection)
    {
        return;
    }

    if (!plan.CanExecute)
    {
        LibraryDeleteConfirmationDialog libraryDeleteConfirmationDialog = new LibraryDeleteConfirmationDialog(plan.BlockMessage ?? "当前选择不能移入回收站。", displayScaleSettings.UiScale, displayScaleSettings.FontScale, (plan.BlockReason == DesktopLibraryDeletionBlockReason.OpenDocument) ? "无法删除" : "删除歌曲", LibraryDeleteDialogTone.Warning, LibraryDeleteDialogButtons.Confirm, flag);
        lastLibraryDeleteDialogScale = libraryDeleteConfirmationDialog.ScaleDiagnostics;
        await libraryDeleteConfirmationDialog.ShowDialog<LibraryDeleteChoice>(this);
        return;
    }

    LibraryDeleteConfirmationDialog libraryDeleteConfirmationDialog2 = new LibraryDeleteConfirmationDialog(plan.ConfirmationMessage ?? string.Empty, displayScaleSettings.UiScale, displayScaleSettings.FontScale, "删除歌曲", LibraryDeleteDialogTone.Question, LibraryDeleteDialogButtons.CancelRecycle, plan.PermanentDeletion);
    lastLibraryDeleteDialogScale = libraryDeleteConfirmationDialog2.ScaleDiagnostics;
    if (await libraryDeleteConfirmationDialog2.ShowDialog<LibraryDeleteChoice>(this) != LibraryDeleteChoice.Recycle)
    {
        lastLibraryDeletionCanceled = true;
        return;
    }

    DesktopLibraryDeletionPlan desktopLibraryDeletionPlan = DesktopLibraryDeletionPlanner.Create(viewModel.ResolvedWorkspaceRoot, selectedFolders, CaptureLibraryOpenDocuments(), IsLibraryFolderDeletionPermanent(selectedFolders));
    if (desktopLibraryDeletionPlan.CanExecute && desktopLibraryDeletionPlan.PermanentDeletion == plan.PermanentDeletion && desktopLibraryDeletionPlan.Targets.Select((DesktopLibraryDeletionTarget target) => target.FullPath).SequenceEqual(plan.Targets.Select((DesktopLibraryDeletionTarget target) => target.FullPath), StringComparer.OrdinalIgnoreCase))
    {
        lastLibraryDeletionConfirmed = true;
        libraryDeletionInProgress = true;
        UpdateLibrarySelectionUi();
        viewModel.CancelLibraryScan();
        viewModel.SetStatus(plan.PermanentDeletion ? AvaloniaLocalizationCatalog.Format("正在永久删除 {0} 个文件夹…", plan.FolderCount) : AvaloniaLocalizationCatalog.Format("正在将 {0} 个文件夹移入回收站…", plan.FolderCount));
        nint ownerWindow = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        try
        {
            (IReadOnlyList<WindowsRecycleBinOperationResult> Results, IReadOnlyList<string> Failures) operation = await Task.Run(() =>
            {
                List<WindowsRecycleBinOperationResult> list = new List<WindowsRecycleBinOperationResult>();
                List<string> list2 = new List<string>();
                foreach (DesktopLibraryDeletionTarget target in plan.Targets)
                {
                    try
                    {
                        if (Directory.Exists(target.FullPath))
                        {
                            list.Add(WindowsRecycleBinService.MoveToRecycleBin(target.FullPath, ownerWindow));
                        }
                    }
                    catch (Exception ex)
                    {
                        list2.Add(target.Folder.DisplayTitle + "：" + ex.Message);
                    }
                }

                return ((IReadOnlyList<WindowsRecycleBinOperationResult> Results, IReadOnlyList<string> Failures))(Results: list, Failures: list2);
            });
            (lastLibraryRecycleResults, lastLibraryRecycleFailures) = operation;
            await RefreshLibraryWithErrorUiAsync();
            MainWindowViewModel mainWindowViewModel = viewModel;
            string status;
            if (plan.PermanentDeletion)
            {
                status = ((operation.Failures.Count == 0) ? AvaloniaLocalizationCatalog.Format("已永久删除 {0} 个歌曲文件夹", operation.Results.Count) : AvaloniaLocalizationCatalog.Format("已永久删除 {0} 个文件夹；{1} 个失败", operation.Results.Count, operation.Failures.Count));
            }
            else
            {
                status = ((operation.Failures.Count == 0) ? AvaloniaLocalizationCatalog.Format("已将 {0} 个歌曲文件夹移入回收站", operation.Results.Count) : AvaloniaLocalizationCatalog.Format("已回收 {0} 个文件夹；{1} 个失败", operation.Results.Count, operation.Failures.Count));
            }

            mainWindowViewModel.SetStatus(status);
            if (operation.Failures.Count > 0)
            {
                LibraryDeleteConfirmationDialog libraryDeleteConfirmationDialog3 = new LibraryDeleteConfirmationDialog(AvaloniaLocalizationCatalog.Format("有 {0} 个文件夹未能删除：\n\n{1}", operation.Failures.Count, string.Join("\n", operation.Failures.Take(8))), displayScaleSettings.UiScale, displayScaleSettings.FontScale, "部分删除失败", LibraryDeleteDialogTone.Warning, LibraryDeleteDialogButtons.Confirm, plan.PermanentDeletion);
                lastLibraryDeleteDialogScale = libraryDeleteConfirmationDialog3.ScaleDiagnostics;
                await libraryDeleteConfirmationDialog3.ShowDialog<LibraryDeleteChoice>(this);
            }

            return;
        }
        finally
        {
            libraryDeletionInProgress = false;
            UpdateLibrarySelectionUi();
        }
    }

    await DeleteLibraryFoldersAsync(selectedFolders);
}

private void BuildEditorView()
{
    bool tabletLayout = !SupportsDesktopWindowSizing;
    if (tabletLayout)
    {
        editorViewSettings = editorViewSettings.WithView(Math.Min(editorViewSettings.GridLaneWidth, 62.0), editorViewSettings.GridBeatHeight, 0.0, Math.Min(editorViewSettings.NoteWidth, 54.0), editorViewSettings.NoteHeight, editorViewSettings.NoteOpacity, editorViewSettings.JudgementLinePosition, editorViewSettings.JudgementLineThickness, editorViewSettings.JudgementLineOpacity).WithQuickPalette(0.0, 0.0, collapsed: false).WithQuickValuesPalette(0.0, 0.0, collapsed: false).WithDifficultyCard(0.0, 0.0, collapsed: false);
        audioPanelSettings = audioPanelSettings.WithPanels(audioPanelSettings.Waveform.WithCollapsed(collapsed: true).WithDocked(docked: false).WithPosition(4.0, 340.0), audioPanelSettings.Spectrogram.WithCollapsed(collapsed: true).WithDocked(docked: false).WithPosition(4.0, 374.0));
    }

    editorView.RowDefinitions = new RowDefinitions("*");
    Grid grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,18,Auto,*,Auto")
    };
    StackPanel stackPanel = new StackPanel
    {
        Orientation = Orientation.Horizontal
    };
    Button button = CommandIconTextButton("新建", "M3,2 H10 L14,6 V15 H3 Z M10,2 V6 H14 M8.5,9 V13 M6.5,11 H10.5", async (object? _, RoutedEventArgs _) =>
    {
        await CreateChartAsync();
    });
    AutomationProperties.SetAutomationId(button, "Editor.New");
    ToolTip.SetTip(button, "新建谱面");
    stackPanel.Children.Add(button);
    Button button2 = CommandIconTextButton("打开", "M2,5 H7 L9,7 H16 L14,14 H2 Z M2,5 V3 H7 L9,5", async (object? _, RoutedEventArgs _) =>
    {
        await PickAndOpenChartAsync();
    }, primary: false, 17.0);
    AutomationProperties.SetAutomationId(button2, "Editor.Open");
    ToolTip.SetTip(button2, "打开 .mc / .osu");
    stackPanel.Children.Add(button2);
    Button button3 = CommandIconTextButton("保存", "M2,2 H14 V14 H2 Z M5,2 V7 H11 V2 M5,10 H11 V14", async (object? _, RoutedEventArgs _) =>
    {
        await SaveAndVerifyWithPreflightAsync();
    }, primary: true);
    AutomationProperties.SetAutomationId(button3, "Editor.Save");
    ToolTip.SetTip(button3, "保存谱面");
    stackPanel.Children.Add(button3);
    if (PlatformFileExportServices.Current?.InAppSaveAsRoot != null)
    {
        Button button4 = CommandIconTextButton("另存为", "M2,2 H14 V14 H2 Z M5,2 V7 H11 V2 M5,10 H11 V14 M12,4 L14,6", async (object? _, RoutedEventArgs _) =>
        {
            await PickAndSaveAsAsync();
        });
        AutomationProperties.SetAutomationId(button4, "Editor.SaveAs");
        ToolTip.SetTip(button4, "另存为新的谱面文件");
        stackPanel.Children.Add(button4);
    }

    exportButton = CommandIconTextButton("导出", "M8,2 V10 M5,7 L8,10 L11,7 M3,12 V14 H13 V12", async (object? _, RoutedEventArgs _) =>
    {
        await BeginChartExportAsync();
    }, primary: false, 17.0);
    AutomationProperties.SetAutomationId(exportButton, "Editor.Export");
    AutomationProperties.SetHelpText(exportButton, "导出 Malody 或 osu!mania 单谱、整谱或倍速谱包");
    ToolTip.SetTip(exportButton, "导出 Malody / osu!mania / 倍速包");
    stackPanel.Children.Add(exportButton);
    Button button5 = CommandIconTextButton("备份", "M2,2 H14 V14 H2 Z M5,2 V7 H11 V2 M5,10 H11 V14 M8,8 V12 M6,10 H10", async (object? _, RoutedEventArgs _) =>
    {
        await BackupCurrentTabAsync();
    });
    AutomationProperties.SetAutomationId(button5, "Editor.Backup");
    ToolTip.SetTip(button5, "立即备份当前谱面");
    stackPanel.Children.Add(button5);
    Button button6 = CommandIconTextButton("恢复", "M3,6 V2 M3,2 H7 M3.5,3.5 A6,6 0 1 1 2.8,10 M8,5 V9 L11,11", async (object? _, RoutedEventArgs _) =>
    {
        await RestoreBackupAsync();
    });
    AutomationProperties.SetAutomationId(button6, "Editor.RestoreBackup");
    ToolTip.SetTip(button6, "从备份恢复谱面");
    stackPanel.Children.Add(button6);
    stackPanel.Children.Add(MapperButton("版本历史", "Editor.Versions", async () =>
    {
        await ShowMapperVersionsAsync();
    }));
    commandPlayButton = CommandIconButton("M2,1 L13,7.5 L2,14 Z", async (object? _, RoutedEventArgs _) =>
    {
        await ToggleAudioPlaybackAsync();
    }, filled: true, 38.0);
    AutomationProperties.SetAutomationId(commandPlayButton, "Editor.Command.PlayPause");
    AutomationProperties.SetName(commandPlayButton, "播放");
    stackPanel.Children.Add(commandPlayButton);
    commandStopButton = CommandGlyphButton("■", async (object? _, RoutedEventArgs _) =>
    {
        await StopAudioAsync();
    });
    AutomationProperties.SetAutomationId(commandStopButton, "Editor.Command.Stop");
    AutomationProperties.SetName(commandStopButton, "停止");
    ToolTip.SetTip(commandStopButton, "停止");
    stackPanel.Children.Add(commandStopButton);
    commandFullscreenButton = CommandGlyphButton("⛶", (object? _, RoutedEventArgs _) =>
    {
        ToggleFullscreen();
    });
    AutomationProperties.SetAutomationId(commandFullscreenButton, "Editor.Command.Fullscreen");
    AutomationProperties.SetName(commandFullscreenButton, tabletLayout ? "隐藏顶部标签" : "全屏窗口化");
    stackPanel.Children.Add(commandFullscreenButton);
    grid.Children.Add(stackPanel);
    Border border = new Border
    {
        Width = 1.0,
        Background = new SolidColorBrush(Color.Parse("#2C3135")),
        Margin = new Thickness(8.0, 4.0)
    };
    Grid.SetColumn(border, 1);
    grid.Children.Add(border);
    StackPanel stackPanel2 = new StackPanel
    {
        Orientation = Orientation.Horizontal
    };
    Button button7 = (toolbarUndoButton = CommandIconButton("M6,4 L2,8 L6,12 M3,8 H9 C12.5,8 15,10 15,14", (object? _, RoutedEventArgs _) =>
    {
        ExecuteHistoryCommand(redo: false);
    }));
    AutomationProperties.SetAutomationId(button7, "Editor.Command.Undo");
    AutomationProperties.SetName(button7, "撤销");
    ToolTip.SetTip(button7, "撤销");
    stackPanel2.Children.Add(button7);
    Button button8 = (toolbarRedoButton = CommandIconButton("M12,4 L16,8 L12,12 M15,8 H9 C5.5,8 3,10 3,14", (object? _, RoutedEventArgs _) =>
    {
        ExecuteHistoryCommand(redo: true);
    }));
    AutomationProperties.SetAutomationId(button8, "Editor.Command.Redo");
    AutomationProperties.SetName(button8, "重做");
    ToolTip.SetTip(button8, "重做");
    stackPanel2.Children.Add(button8);
    Button button9 = CommandButton("检查", async (object? _, RoutedEventArgs _) =>
    {
        await CheckActiveChartAsync();
    });
    AutomationProperties.SetAutomationId(button9, "Editor.ChartCheck");
    ToolTip.SetTip(button9, "检查重叠键与 LN 交叉");
    stackPanel2.Children.Add(button9);
    ConfigureCommandToggleButton(subdivisionCheckButton, "分度检查");
    AutomationProperties.SetAutomationId(subdivisionCheckButton, "Editor.SubdivisionCheck");
    ToolTip.SetTip(subdivisionCheckButton, "按精确分度为 Note 边框着色；非常规分度统一显示红色");
    subdivisionCheckButton.Click += (object? _, RoutedEventArgs _) =>
    {
        ToggleSubdivisionCheck();
    };
    stackPanel2.Children.Add(subdivisionCheckButton);
    difficultyAnalysisButton = CommandButton("难度", async (object? _, RoutedEventArgs _) =>
    {
        await ShowDifficultyAnalysisAsync();
    });
    AutomationProperties.SetAutomationId(difficultyAnalysisButton, "Editor.DifficultyAnalysis");
    AutomationProperties.SetHelpText(difficultyAnalysisButton, "使用 Rework 分析正式 4K 谱面难度");
    ToolTip.SetTip(difficultyAnalysisButton, "使用 Rework 分析正式 4K 谱面难度");
    stackPanel2.Children.Add(difficultyAnalysisButton);
    extensionMenuButton = CommandButton("拓展功能", (object? _, RoutedEventArgs _) =>
    {
        ToggleExtensionPalette();
    });
    AutomationProperties.SetAutomationId(extensionMenuButton, "Editor.Extension.Toggle");
    ToolTip.SetTip(extensionMenuButton, "打开可移动的谱面拓展工具");
    Grid.SetColumn(stackPanel2, 2);
    grid.Children.Add(stackPanel2);
    mixedCopyButton = PrimaryButton("复制", (object? _, RoutedEventArgs _) =>
    {
        CopyMixedSelection();
    });
    mixedPasteButton = PrimaryButton("粘贴（虚影）", (object? _, RoutedEventArgs _) =>
    {
        PasteMixedSelection();
    });
    mixedDeleteButton = PrimaryButton("删除选择", (object? _, RoutedEventArgs _) =>
    {
        DeleteMixedSelection();
    });
    ownerLockButton = PrimaryButton("锁定选区", async (object? _, RoutedEventArgs _) =>
    {
        await ToggleOwnerLockSelectionAsync();
    });
    ownerLockButton.IsVisible = false;
    AutomationProperties.SetAutomationId(ownerLockButton, "Editor.OwnerLock");
    AutomationProperties.SetHelpText(ownerLockButton, "房主锁定或解除当前选择精确覆盖的 Beat 范围；末端不含");
    StackPanel toolbar = new StackPanel
    {
        IsVisible = false
    };
    AddTimelineToolButton(toolbar, TimelineTool.Select, "选择");
    AddTimelineToolButton(toolbar, TimelineTool.Tap, "Tap");
    AddTimelineToolButton(toolbar, TimelineTool.Hold, "Hold");
    AddTimelineToolButton(toolbar, TimelineTool.Erase, "擦除");
    UpdateTimelineToolButtons();
    ConfigureCommandModeButton(proEditorButton, proModeLabel);
    proEditorButton.Click += (object? _, RoutedEventArgs _) =>
    {
        timeline.SetProEditor(!timeline.ProEditorEnabled);
        RefreshProEditorControls();
        timeline.Focus();
    };
    AutomationProperties.SetName(proEditorButton, "Pro Editor Toggle");
    AutomationProperties.SetAutomationId(proEditorButton, "ProEditor.Toggle");
    Grid.SetColumn(proEditorButton, 4);
    grid.Children.Add(proEditorButton);
    proGridColumnText.Text = "12 列";
    proGridColumnText.Foreground = MutedBrush;
    proGridColumnText.VerticalAlignment = VerticalAlignment.Center;
    proGridColumnSlider.Minimum = 1.0;
    proGridColumnSlider.Maximum = 64.0;
    proGridColumnSlider.Value = persistedProGridColumns;
    proGridColumnSlider.MinWidth = 0.0;
    proGridColumnSlider.MaxWidth = 286.0;
    proGridColumnSlider.HorizontalAlignment = HorizontalAlignment.Stretch;
    proGridColumnSlider.SmallChange = 1.0;
    proGridColumnSlider.LargeChange = 4.0;
    proGridColumnSlider.ValueChanged += (object? _, RangeBaseValueChangedEventArgs eventArgs) =>
    {
        if (!suppressProEditorControls)
        {
            int min = viewModel.ActiveTab?.Session.Document.KeyCount ?? 1;
            int proGridColumnCount = Math.Clamp((int)Math.Round(eventArgs.NewValue, MidpointRounding.AwayFromZero), min, 64);
            timeline.SetProGridColumnCount(proGridColumnCount);
            persistedProGridColumns = timeline.ProGridColumnCount;
            ScheduleProGridColumnSave();
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("ProEditor 工作区已设为 {0} 列", persistedProGridColumns));
            RefreshProEditorControls();
        }
    };
    AutomationProperties.SetName(proGridColumnSlider, "Pro Editor Column Count");
    AutomationProperties.SetAutomationId(proGridColumnSlider, "Settings.ProEditorColumns");
    divisionPicker.ItemsSource = TimelineSubdivisionCheckPalette.EditorDivisions;
    divisionPicker.SelectedItem = editorViewSettings.SnapDivision;
    divisionPicker.Width = 86.0;
    divisionPicker.SelectionChanged += (object? _, SelectionChangedEventArgs _) =>
    {
        if (viewModel.ActiveTab != null && divisionPicker.SelectedItem is int division)
        {
            SetQuickSnapDivision(division);
        }
    };
    commandMenu.Background = new SolidColorBrush(Color.Parse("#F515181A"));
    commandMenu.BorderBrush = new SolidColorBrush(Color.Parse("#3A4045"));
    commandMenu.BorderThickness = new Thickness(1.0);
    commandMenu.CornerRadius = new CornerRadius(4.0);
    commandMenu.Padding = new Thickness(8.0, 6.0);
    commandMenu.Margin = new Thickness(52.0, 8.0, 8.0, 0.0);
    commandMenu.HorizontalAlignment = HorizontalAlignment.Left;
    commandMenu.VerticalAlignment = VerticalAlignment.Top;
    commandMenu.IsVisible = false;
    commandMenu.ZIndex = 35;
    commandMenu.Child = new ScrollViewer
    {
        MaxWidth = 1180.0,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = grid
    };
    AutomationProperties.SetAutomationId(commandMenu, "Editor.CommandMenu");
    commandMenuButton.Content = "⚙";
    commandMenuButton.Width = 38.0;
    commandMenuButton.Height = 38.0;
    commandMenuButton.Margin = new Thickness(8.0);
    commandMenuButton.Padding = new Thickness(0.0);
    commandMenuButton.HorizontalAlignment = HorizontalAlignment.Left;
    commandMenuButton.VerticalAlignment = VerticalAlignment.Top;
    commandMenuButton.FontFamily = new FontFamily("Segoe UI Symbol");
    commandMenuButton.FontSize = 19.0;
    commandMenuButton.Background = PanelBrush;
    commandMenuButton.Foreground = Brushes.White;
    commandMenuButton.BorderBrush = StrokeBrush;
    commandMenuButton.BorderThickness = new Thickness(1.0);
    commandMenuButton.CornerRadius = new CornerRadius(3.0);
    commandMenuButton.ZIndex = 40;
    ToolTip.SetTip(commandMenuButton, "展开编辑菜单");
    AutomationProperties.SetAutomationId(commandMenuButton, "Editor.CommandMenuToggle");
    commandMenuButton.Click += (object? _, RoutedEventArgs _) =>
    {
        commandMenu.IsVisible = !commandMenu.IsVisible;
        commandMenuButton.Content = (commandMenu.IsVisible ? "×" : "⚙");
        ToolTip.SetTip(commandMenuButton, commandMenu.IsVisible ? "收起编辑菜单" : "展开编辑菜单");
        AutomationProperties.SetHelpText(commandMenuButton, commandMenu.IsVisible ? "编辑菜单已展开" : "编辑菜单已收起");
        timeline.Focus();
    };
    Grid workbench = editorWorkbench;
    workbench.ColumnDefinitions = new ColumnDefinitions(tabletLayout ? "0,0,*,28,350" : "0,0,*,34,350");
    workbench.Margin = new Thickness(tabletLayout ? 4 : 8);
    timeline.Margin = new Thickness(5.0);
    AutomationProperties.SetAutomationId(timeline, "Timeline.Surface");
    timelineHost.Children.Add(timeline);
    Border border2 = new Border
    {
        MinWidth = 150.0,
        Height = 28.0,
        Padding = new Thickness(10.0, 0.0),
        Margin = new Thickness(0.0, 7.0, 0.0, 0.0),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        Background = new SolidColorBrush(Color.Parse("#E51B2023")),
        BorderBrush = new SolidColorBrush(Color.Parse("#3E484C")),
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(3.0),
        IsHitTestVisible = false,
        Child = audioTimeText
    };
    audioTimeText.HorizontalAlignment = HorizontalAlignment.Center;
    audioTimeText.VerticalAlignment = VerticalAlignment.Center;
    audioTimeText.TextAlignment = TextAlignment.Center;
    border2.SetValue(Visual.ZIndexProperty, 44);
    AutomationProperties.SetAutomationId(border2, "Editor.PlaybackTimeBadge");
    timelineHost.Children.Add(border2);
    AddAudioVisualizationPanels();
    BuildDifficultyInfoCard();
    proTrackPanSlider.Minimum = 0.0;
    proTrackPanSlider.Maximum = 0.0;
    proTrackPanSlider.Value = 0.0;
    proTrackPanSlider.Margin = new Thickness(12.0, 4.0);
    proTrackPanSlider.ValueChanged += (object? _, RangeBaseValueChangedEventArgs eventArgs) =>
    {
        if (!suppressProEditorControls)
        {
            timeline.SetTrackPanOffset(eventArgs.NewValue);
        }
    };
    AutomationProperties.SetName(proTrackPanSlider, "Pro Editor Horizontal Pan");
    AutomationProperties.SetAutomationId(proTrackPanSlider, "ProEditor.HorizontalPan");
    proTrackPanBar.Background = RaisedBrush;
    proTrackPanBar.BorderBrush = StrokeBrush;
    proTrackPanBar.BorderThickness = new Thickness(0.0, 1.0, 0.0, 0.0);
    proTrackPanBar.Child = proTrackPanSlider;
    proTrackPanBar.IsVisible = false;
    Grid.SetRow(proTrackPanBar, 1);
    timelineHost.Children.Add(proTrackPanBar);
    Border frame = new Border
    {
        Background = new SolidColorBrush(Color.Parse("#111315")),
        BorderBrush = StrokeBrush,
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(4.0),
        Padding = new Thickness(1.0),
        Child = timelineHost
    };
    Grid grid2 = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("165,*")
    };
    grid2.Children.Add(BuildFixedToolRail());
    Control control = BuildTrackWorkbench(frame);
    Grid.SetColumn(control, 1);
    grid2.Children.Add(control);
    Grid.SetColumn(grid2, 2);
    workbench.Children.Add(grid2);
    inspectorText.Foreground = MutedBrush;
    inspectorText.FontSize = 12.0;
    inspectorText.LineHeight = 22.0;
    inspectorText.TextWrapping = TextWrapping.Wrap;
    AutomationProperties.SetAutomationId(inspectorText, "Editor.InspectorSummary");
    Control control2 = WrapInspectorContent(BuildMetadataEditor());
    Control control3 = BuildSettingsPanel();
    Control control4 = WrapInspectorContent(BuildNoteInspector());
    Control control5 = BuildTimingEditor();
    Control control6 = WrapInspectorContent(BuildGroupManager(), new Thickness(18.0, 14.0, 18.0, 24.0));
    metadataInspectorTab.Header = WindowsInspectorParityPolicy.Wpf110TabHeaders[0];
    AutomationProperties.SetName(metadataInspectorTab, WindowsInspectorParityPolicy.Wpf110TabHeaders[0]);
    AutomationProperties.SetAutomationId(metadataInspectorTab, "Inspector.MetadataTab");
    settingsInspectorTab.Header = WindowsInspectorParityPolicy.Wpf110TabHeaders[4];
    AutomationProperties.SetName(settingsInspectorTab, "设置");
    AutomationProperties.SetAutomationId(settingsInspectorTab, "Inspector.SettingsTab");
    noteInspectorTab.Header = WindowsInspectorParityPolicy.Wpf110TabHeaders[2];
    AutomationProperties.SetName(noteInspectorTab, WindowsInspectorParityPolicy.Wpf110TabHeaders[2]);
    AutomationProperties.SetAutomationId(noteInspectorTab, "Inspector.NoteTab");
    timingInspectorTab.Header = WindowsInspectorParityPolicy.Wpf110TabHeaders[1];
    AutomationProperties.SetName(timingInspectorTab, WindowsInspectorParityPolicy.Wpf110TabHeaders[1]);
    AutomationProperties.SetAutomationId(timingInspectorTab, "Inspector.TimingTab");
    groupInspectorTab.Header = WindowsInspectorParityPolicy.Wpf110TabHeaders[3];
    AutomationProperties.SetName(groupInspectorTab, WindowsInspectorParityPolicy.Wpf110TabHeaders[3]);
    AutomationProperties.SetAutomationId(groupInspectorTab, "Inspector.GroupTab");
    TabItem[] array = new TabItem[4]
    {
        metadataInspectorTab,
        timingInspectorTab,
        noteInspectorTab,
        settingsInspectorTab
    };
    editorInspectorTabs.ItemsSource = array;
    editorInspectorTabs.Background = Brushes.Transparent;
    editorInspectorTabs.BorderThickness = new Thickness(0.0);
    editorInspectorTabs.Padding = new Thickness(0.0);
    if (tabletLayout)
    {
        tabletInspectorContents[metadataInspectorTab] = control2;
        tabletInspectorContents[timingInspectorTab] = control5;
        tabletInspectorContents[noteInspectorTab] = control4;
        tabletInspectorContents[groupInspectorTab] = control6;
        tabletInspectorContents[settingsInspectorTab] = control3;
    }
    else
    {
        metadataInspectorTab.Content = control2;
        timingInspectorTab.Content = control5;
        noteInspectorTab.Content = control4;
        groupInspectorTab.Content = control6;
        settingsInspectorTab.Content = control3;
    }

    editorInspectorTabs.SelectionChanged += (object? _, SelectionChangedEventArgs _) =>
    {
        if (tabletLayout)
        {
            UpdateTabletInspectorSelection();
        }

        QueueDisplayScaleMaterializedContentScan();
        localizationController.ScheduleApply();
    };
    editorInspectorTabs.SelectedItem = timingInspectorTab;
    inspectorPanel.Background = PanelBrush;
    inspectorPanel.BorderBrush = StrokeBrush;
    inspectorPanel.BorderThickness = new Thickness(1.0);
    inspectorPanel.CornerRadius = new CornerRadius(4.0);
    inspectorPanel.Child = (tabletLayout ? BuildTabletInspectorSurface(array) : editorInspectorTabs);
    Grid.SetColumn(inspectorPanel, 4);
    workbench.Children.Add(inspectorPanel);
    inspectorToggleButton.Content = "−";
    inspectorToggleButton.Width = 28.0;
    inspectorToggleButton.Height = 28.0;
    inspectorToggleButton.Padding = new Thickness(0.0);
    inspectorToggleButton.Margin = new Thickness(3.0, 0.0);
    inspectorToggleButton.VerticalAlignment = VerticalAlignment.Top;
    inspectorToggleButton.Background = PanelBrush;
    inspectorToggleButton.Foreground = Brushes.White;
    inspectorToggleButton.BorderBrush = StrokeBrush;
    inspectorToggleButton.BorderThickness = new Thickness(1.0);
    inspectorToggleButton.CornerRadius = new CornerRadius(3.0);
    ToolTip.SetTip(inspectorToggleButton, "收起右侧面板");
    AutomationProperties.SetAutomationId(inspectorToggleButton, "Inspector.Toggle");
    inspectorToggleButton.Click += (object? _, RoutedEventArgs _) =>
    {
        bool isVisible = inspectorPanel.IsVisible;
        inspectorPanel.IsVisible = !isVisible;
        workbench.ColumnDefinitions[4].Width = (isVisible ? new GridLength(0.0) : new GridLength(350.0));
        inspectorToggleButton.Content = (isVisible ? "+" : "−");
        ToolTip.SetTip(inspectorToggleButton, isVisible ? "展开右侧面板" : "收起右侧面板");
    };
    Grid.SetColumn(inspectorToggleButton, 3);
    workbench.Children.Add(inspectorToggleButton);
    Grid.SetColumnSpan(commandMenu, 5);
    workbench.Children.Add(commandMenu);
    Grid.SetColumnSpan(commandMenuButton, 5);
    workbench.Children.Add(commandMenuButton);
    Control element = BuildQuickEditPalette();
    Control control7 = BuildRoomCollaborationPanel();
    Grid.SetColumn(element, 2);
    Grid.SetColumnSpan(element, 1);
    MergeQuickPaletteIntoToolRail();
    Grid.SetColumn(control7, 2);
    Grid.SetColumnSpan(control7, 1);
    workbench.Children.Add(control7);
    Control control8 = BuildExtensionPalette();
    Grid.SetColumn(control8, 2);
    workbench.Children.Add(control8);
    workbench.RowDefinitions = new RowDefinitions("Auto,*,64");
    foreach (Control child in workbench.Children)
    {
        Grid.SetRow(child, 1);
    }

    commandMenuButton.IsVisible = false;
    workbench.Children.Remove(commandMenu);
    Control control9 = BuildUnifiedTopToolbar(commandMenu, BuildFixedGridToolbar());
    Grid.SetColumnSpan(control9, 5);
    Grid.SetRow(control9, 0);
    workbench.Children.Add(control9);
    Control control10 = BuildQuickValuesPalette();
    Grid.SetColumnSpan(control10, 3);
    Grid.SetRow(control10, 1);
    workbench.Children.Add(control10);
    ApplyQuickValuesPaletteLayout();
    Control control11 = BuildEventTimeRuler();
    Grid.SetRow(control11, 2);
    Grid.SetColumnSpan(control11, 5);
    workbench.Children.Add(control11);
    editorView.Children.Add(workbench);
}

private Control BuildTabletInspectorSurface(IReadOnlyList<TabItem> tabs)
{
    Grid grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"),
        ColumnSpacing = 0.0,
        Margin = new Thickness(4.0, 4.0, 4.0, 0.0)
    };
    for (int i = 0; i < tabs.Count; i++)
    {
        TabItem tab = tabs[i];
        string text = tab.Header?.ToString() ?? string.Empty;
        Button button = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                FontSize = 11.0,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            },
            MinWidth = 0.0,
            Height = 36.0,
            Padding = new Thickness(3.0, 0.0),
            Margin = new Thickness(0.0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
            Background = Brushes.Transparent,
            Foreground = MutedBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(0.0, 0.0, 0.0, 1.0),
            CornerRadius = new CornerRadius(0.0)
        };
        button.Click += (object? _, RoutedEventArgs _) =>
        {
            editorInspectorTabs.SelectedItem = tab;
        };
        AutomationProperties.SetName(button, AvaloniaLocalizationCatalog.Format("{0}面板", tab.Header));
        AutomationProperties.SetAutomationId(button, "Tablet." + AutomationProperties.GetAutomationId(tab));
        Grid.SetColumn(button, i);
        grid.Children.Add(button);
        tabletInspectorButtons[tab] = button;
    }

    tabletInspectorContent.HorizontalContentAlignment = HorizontalAlignment.Stretch;
    tabletInspectorContent.VerticalContentAlignment = VerticalAlignment.Stretch;
    Grid grid2 = new Grid
    {
        RowDefinitions = new RowDefinitions("Auto,*")
    };
    Style item = new Style((Selector? selector) => selector.OfType<TextBox>())
    {
        Setters =
        {
            (SetterBase)new Setter(Layoutable.MinHeightProperty, 34.0)
        }
    };
    grid2.Styles.Add(item);
    Style item2 = new Style((Selector? selector) => selector.OfType<ComboBox>())
    {
        Setters =
        {
            (SetterBase)new Setter(Layoutable.MinHeightProperty, 34.0)
        }
    };
    grid2.Styles.Add(item2);
    Style item3 = new Style((Selector? selector) => selector.OfType<ScrollBar>().Class(":vertical"))
    {
        Setters =
        {
            (SetterBase)new Setter(Layoutable.WidthProperty, 22.0)
        }
    };
    grid2.Styles.Add(item3);
    Style item4 = new Style((Selector? selector) => selector.OfType<ScrollBar>().Class(":vertical").Template().OfType<Rectangle>().Name("TrackRect"))
    {
        Setters =
        {
            (SetterBase)new Setter(Layoutable.WidthProperty, 10.0)
        }
    };
    grid2.Styles.Add(item4);
    Style item5 = new Style((Selector? selector) => selector.OfType<ScrollBar>().Class(":vertical").Template().OfType<Thumb>())
    {
        Setters =
        {
            (SetterBase)new Setter(Layoutable.WidthProperty, 14.0),
            (SetterBase)new Setter(Layoutable.MinHeightProperty, 52.0),
            (SetterBase)new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(7.0))
        }
    };
    grid2.Styles.Add(item5);
    grid2.Children.Add(grid);
    Grid.SetRow(tabletInspectorContent, 1);
    grid2.Children.Add(tabletInspectorContent);
    UpdateTabletInspectorSelection();
    return grid2;
}

private void UpdateTabletInspectorSelection()
{
    TabItem tabItem = (editorInspectorTabs.SelectedItem as TabItem) ?? timingInspectorTab;
    if (tabletInspectorContents.TryGetValue(tabItem, out Control value) && tabletInspectorContent.Content != value)
    {
        tabletInspectorContent.Content = value;
    }

    foreach (KeyValuePair<TabItem, Button> tabletInspectorButton in tabletInspectorButtons)
    {
        bool flag = tabletInspectorButton.Key == tabItem;
        Button value2 = tabletInspectorButton.Value;
        IBrush background;
        if (!flag)
        {
            IBrush transparent = Brushes.Transparent;
            background = transparent;
        }
        else
        {
            background = RaisedBrush;
        }

        value2.Background = background;
        Button value3 = tabletInspectorButton.Value;
        IBrush foreground;
        if (!flag)
        {
            foreground = MutedBrush;
        }
        else
        {
            IBrush transparent = Brushes.White;
            foreground = transparent;
        }

        value3.Foreground = foreground;
        tabletInspectorButton.Value.BorderBrush = (flag ? AccentBrush : StrokeBrush);
        tabletInspectorButton.Value.BorderThickness = new Thickness(0.0, 0.0, 0.0, (!flag) ? 1 : 2);
    }
}

private Control BuildOverviewPanel()
{
    editorOverview.Margin = new Thickness(3.0, 0.0);
    editorOverview.SeekRequested += (BeatPosition beat) =>
    {
        DesktopChartTab activeTab = viewModel.ActiveTab;
        if (activeTab != null)
        {
            BeatPosition beatPosition = TimelineGridNavigation.Snap(beat.Value, activeTab.Session.SnapDivision);
            timeline.SetCurrentBeat(beatPosition.Value);
            viewModel.UpdateCurrentBeat(beatPosition.Value);
            timeline.Focus();
        }
    };
    editorOverview.NavigationMarkerSeekRequested += (BeatPosition beat) =>
    {
        if (viewModel.ActiveTab != null)
        {
            timeline.SetCurrentBeat(beat.Value);
            viewModel.UpdateCurrentBeat(beat.Value);
            timeline.Focus();
        }
    };
    editorOverview.IssueSeekRequested += (ChartIssue issue) =>
    {
        DesktopChartTab activeTab = viewModel.ActiveTab;
        if (activeTab != null)
        {
            ApplyChartIssueNavigation(activeTab, DesktopChartIssueNavigationPlanner.Create(activeTab.Session.Document, issue, activeTab.HiddenGroups));
        }
    };
    editorOverview.NavigationMarkerDeleteRequested += RemoveNavigationMarker;
    AutomationProperties.SetAutomationId(editorOverview, "Editor.Overview");
    AutomationProperties.SetName(editorOverview, "Note 密度峰值");
    miniNoteCountText.Text = "0";
    miniNoteCountText.FontSize = 20.0;
    miniNoteCountText.FontWeight = FontWeight.DemiBold;
    miniNoteCountText.Foreground = new SolidColorBrush(Color.Parse("#E2E5E6"));
    miniNoteCountText.HorizontalAlignment = HorizontalAlignment.Center;
    StackPanel stackPanel = new StackPanel
    {
        Margin = new Thickness(0.0, 10.0, 0.0, 0.0),
        Children =
        {
            (Control)miniNoteCountText,
            (Control)new TextBlock
            {
                Text = "NOTES",
                FontSize = 8.0,
                Foreground = new SolidColorBrush(Color.Parse("#7F878C")),
                HorizontalAlignment = HorizontalAlignment.Center
            }
        }
    };
    Grid.SetRow(stackPanel, 2);
    TextBlock item = new TextBlock
    {
        Text = "DENSITY",
        FontSize = 9.0,
        Foreground = new SolidColorBrush(Color.Parse("#7F878C")),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };
    StackPanel item2 = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Center,
        Spacing = 3.0,
        Children =
        {
            (Control)navigationMarkerAddButton,
            (Control)navigationMarkerListButton
        }
    };
    StackPanel item3 = new StackPanel
    {
        Margin = new Thickness(0.0, 36.0, 0.0, 6.0),
        Spacing = 4.0,
        Children =
        {
            (Control)item,
            (Control)item2
        }
    };
    Grid grid = new Grid
    {
        RowDefinitions = new RowDefinitions("Auto,*,Auto")
    };
    grid.Children.Add(item3);
    Grid.SetRow(editorOverview, 1);
    grid.Children.Add(editorOverview);
    grid.Children.Add(stackPanel);
    BuildNavigationMarkerPopup();
    return new Border
    {
        Background = new SolidColorBrush(Color.Parse("#181B1D")),
        BorderBrush = StrokeBrush,
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(4.0),
        Padding = new Thickness(8.0),
        Child = grid
    };
}

private void BuildNavigationMarkerPopup()
{
    navigationMarkerAddButton.Content = "+";
    navigationMarkerAddButton.Width = 24.0;
    navigationMarkerAddButton.Height = 24.0;
    navigationMarkerAddButton.Padding = new Thickness(0.0);
    navigationMarkerAddButton.Margin = new Thickness(0.0);
    navigationMarkerAddButton.FontFamily = new FontFamily("Segoe UI Symbol");
    navigationMarkerAddButton.HorizontalContentAlignment = HorizontalAlignment.Center;
    navigationMarkerAddButton.VerticalContentAlignment = VerticalAlignment.Center;
    ApplyWpfToolButtonStyle(navigationMarkerAddButton);
    navigationMarkerAddButton.Padding = new Thickness(0.0);
    navigationMarkerAddButton.FontSize = 15.0;
    ToolTip.SetTip(navigationMarkerAddButton, "在当前时间添加跳转标记");
    AutomationProperties.SetName(navigationMarkerAddButton, "添加跳转标记");
    AutomationProperties.SetAutomationId(navigationMarkerAddButton, "Editor.Overview.NavigationMarker.Add");
    navigationMarkerAddButton.Click += (object? _, RoutedEventArgs _) =>
    {
        AddNavigationMarker();
    };
    navigationMarkerListButton.Content = "≡";
    navigationMarkerListButton.Width = 24.0;
    navigationMarkerListButton.Height = 24.0;
    navigationMarkerListButton.Padding = new Thickness(0.0);
    navigationMarkerListButton.Margin = new Thickness(0.0);
    navigationMarkerListButton.FontFamily = new FontFamily("Segoe UI Symbol");
    navigationMarkerListButton.HorizontalContentAlignment = HorizontalAlignment.Center;
    navigationMarkerListButton.VerticalContentAlignment = VerticalAlignment.Center;
    ApplyWpfToolButtonStyle(navigationMarkerListButton);
    navigationMarkerListButton.Padding = new Thickness(0.0);
    navigationMarkerListButton.FontSize = 15.0;
    ToolTip.SetTip(navigationMarkerListButton, "打开跳转标记列表");
    AutomationProperties.SetName(navigationMarkerListButton, "跳转标记列表");
    AutomationProperties.SetAutomationId(navigationMarkerListButton, "Editor.Overview.NavigationMarker.List");
    navigationMarkerListButton.Click += (object? _, RoutedEventArgs _) =>
    {
        if (viewModel.ActiveTab != null)
        {
            if (navigationMarkerPopup.IsOpen)
            {
                navigationMarkerPopup.IsOpen = false;
                navigationMarkerListButton.Focus();
            }
            else
            {
                RefreshNavigationMarkerUi();
                navigationMarkerPopup.IsOpen = true;
            }
        }
    };
    navigationMarkerTitleText.Text = "跳转标记";
    navigationMarkerTitleText.Foreground = Brushes.White;
    navigationMarkerTitleText.FontWeight = FontWeight.DemiBold;
    navigationMarkerTitleText.VerticalAlignment = VerticalAlignment.Center;
    navigationMarkerCountText.Foreground = MutedBrush;
    navigationMarkerCountText.FontFamily = new FontFamily("Cascadia Mono, Consolas");
    navigationMarkerCountText.VerticalAlignment = VerticalAlignment.Center;
    navigationMarkerEmptyText.Foreground = MutedBrush;
    navigationMarkerEmptyText.Margin = new Thickness(6.0, 14.0);
    navigationMarkerEmptyText.TextWrapping = TextWrapping.Wrap;
    navigationMarkerEmptyText.IsVisible = false;
    navigationMarkerList.Spacing = 3.0;
    Grid item = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        Margin = new Thickness(5.0, 3.0, 5.0, 7.0),
        Children =
        {
            (Control)navigationMarkerTitleText,
            (Control)navigationMarkerCountText
        }
    };
    Grid.SetColumn(navigationMarkerCountText, 1);
    ScrollViewer item2 = new ScrollViewer
    {
        MaxHeight = 360.0,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = navigationMarkerList
    };
    Grid grid = new Grid
    {
        Children =
        {
            (Control)navigationMarkerEmptyText,
            (Control)item2
        }
    };
    Grid grid2 = new Grid
    {
        RowDefinitions = new RowDefinitions("Auto,Auto,*"),
        Children =
        {
            (Control)item,
            (Control)new Border
            {
                Height = 1.0,
                Background = StrokeBrush,
                Margin = new Thickness(4.0, 0.0, 4.0, 6.0)
            },
            (Control)grid
        }
    };
    Grid.SetRow(grid2.Children[1], 1);
    Grid.SetRow(grid, 2);
    navigationMarkerPopupSurface.Width = (SupportsDesktopWindowSizing ? 280 : 300);
    navigationMarkerPopupSurface.Padding = new Thickness(7.0);
    navigationMarkerPopupSurface.Background = new SolidColorBrush(Color.Parse("#F51A1D20"));
    navigationMarkerPopupSurface.BorderBrush = new SolidColorBrush(Color.Parse("#465257"));
    navigationMarkerPopupSurface.BorderThickness = new Thickness(1.0);
    navigationMarkerPopupSurface.CornerRadius = new CornerRadius(5.0);
    navigationMarkerPopupSurface.Child = grid2;
    AutomationProperties.SetName(navigationMarkerPopupSurface, "跳转标记列表");
    AutomationProperties.SetAutomationId(navigationMarkerPopupSurface, "Editor.Overview.NavigationMarker.Popup");
    navigationMarkerPopupSurface.AddHandler(InputElement.KeyDownEvent, NavigationMarkerPopup_KeyDown, RoutingStrategies.Tunnel);
    navigationMarkerPopup.PlacementTarget = navigationMarkerListButton;
    navigationMarkerPopup.Placement = PlacementMode.Bottom;
    navigationMarkerPopup.VerticalOffset = 4.0;
    navigationMarkerPopup.IsLightDismissEnabled = true;
    navigationMarkerPopup.TakesFocusFromNativeControl = true;
    navigationMarkerPopup.WindowManagerAddShadowHint = true;
    navigationMarkerPopup.Child = navigationMarkerPopupSurface;
    navigationMarkerPopup.Opened += (object? _, EventArgs _) =>
    {
        RefreshNavigationMarkerUi();
        ApplyNavigationMarkerPopupScale();
        localizationController.ScheduleApply(navigationMarkerPopupSurface);
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (navigationMarkerPopup.IsOpen && navigationMarkerButtons.Count > 0)
            {
                navigationMarkerButtons[0].Focus();
            }
            else if (navigationMarkerPopup.IsOpen)
            {
                navigationMarkerListButton.Focus();
            }
        }, DispatcherPriority.Input);
    };
    navigationMarkerPopup.Closed += (object? _, EventArgs _) =>
    {
        navigationMarkerListButton.Focus();
        timeline.Focus();
    };
}

private void RefreshNavigationMarkerUi()
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    IReadOnlyList<DesktopNavigationMarker> readOnlyList = activeTab?.NavigationMarkers ?? Array.Empty<DesktopNavigationMarker>();
    navigationMarkerAddButton.IsEnabled = activeTab != null;
    navigationMarkerListButton.IsEnabled = activeTab != null;
    navigationMarkerCountText.Text = $"{readOnlyList.Count}/{64}";
    navigationMarkerEmptyText.Text = ((activeTab == null) ? "没有打开的谱面" : "当前谱面还没有跳转标记");
    navigationMarkerEmptyText.IsVisible = readOnlyList.Count == 0;
    navigationMarkerList.IsVisible = readOnlyList.Count > 0;
    navigationMarkerButtons.Clear();
    navigationMarkerDeleteButtons.Clear();
    navigationMarkerList.Children.Clear();
    if (activeTab == null)
    {
        return;
    }

    TimingMap timingMap = null;
    try
    {
        timingMap = GetAudioTimingMap(activeTab);
    }
    catch (Exception)
    {
    }

    for (int i = 0; i < readOnlyList.Count; i++)
    {
        DesktopNavigationMarker marker = readOnlyList[i];
        TextBlock item = new TextBlock
        {
            Text = marker.Beat.ToString(),
            Foreground = new SolidColorBrush(Color.Parse("#E4E9EA")),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        TextBlock item2 = new TextBlock
        {
            Text = ((timingMap == null) ? "音频时间不可用" : FormatAudioTime(TimeSpan.FromMilliseconds(Math.Max(0.0, timingMap.BeatToMilliseconds(marker.Beat))))),
            Foreground = MutedBrush,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Button button = new Button
        {
            Content = new StackPanel
            {
                Spacing = 2.0,
                Children =
                {
                    (Control)item,
                    (Control)item2
                }
            },
            Padding = new Thickness(7.0, 5.0),
            Margin = new Thickness(0.0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ApplyWpfToolButtonStyle(button);
        ToolTip.SetTip(button, AvaloniaLocalizationCatalog.Format("精确跳转到 {0}", marker.Beat));
        AutomationProperties.SetName(button, AvaloniaLocalizationCatalog.Format("跳转到 {0}", marker.Beat));
        AutomationProperties.SetAutomationId(button, $"Editor.Overview.NavigationMarker.Item.{i}");
        button.Click += (object? _, RoutedEventArgs _) =>
        {
            NavigateNavigationMarker(marker.Beat);
            navigationMarkerPopup.IsOpen = false;
        };
        Button button2 = new Button
        {
            Content = "×",
            Width = 30.0,
            Height = 34.0,
            Padding = new Thickness(0.0),
            Margin = new Thickness(0.0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ApplyDangerButtonStyle(button2);
        ToolTip.SetTip(button2, AvaloniaLocalizationCatalog.Format("删除跳转标记 {0}", marker.Beat));
        AutomationProperties.SetName(button2, AvaloniaLocalizationCatalog.Format("删除跳转标记 {0}", marker.Beat));
        AutomationProperties.SetAutomationId(button2, $"Editor.Overview.NavigationMarker.Delete.{i}");
        button2.Click += (object? _, RoutedEventArgs _) =>
        {
            RemoveNavigationMarker(marker.Id);
        };
        Grid item3 = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,30"),
            ColumnSpacing = 4.0,
            Children =
            {
                (Control)button,
                (Control)button2
            }
        };
        Grid.SetColumn(button2, 1);
        navigationMarkerList.Children.Add(item3);
        navigationMarkerButtons.Add(button);
        navigationMarkerDeleteButtons.Add(button2);
    }

    if (navigationMarkerPopup.IsOpen)
    {
        ApplyNavigationMarkerPopupScale();
    }
}

private void ApplyNavigationMarkerPopupScale()
{
    WindowsDisplayScaleSettings settings = displayScaleController.CurrentSettings.Normalize();
    AvaloniaPopupScalePlan avaloniaPopupScalePlan = AvaloniaPopupScaleController.Resolve(10.0, settings);
    if (TopLevel.GetTopLevel(navigationMarkerPopupSurface)is PopupRoot popupRoot)
    {
        popupRoot.Transform = new ScaleTransform(avaloniaPopupScalePlan.UiScale, avaloniaPopupScalePlan.UiScale);
    }

    fontScaleController.Rebase(navigationMarkerTitleText, 11.0);
    fontScaleController.Rebase(navigationMarkerCountText, 9.0);
    fontScaleController.Rebase(navigationMarkerEmptyText, 10.0);
    foreach (Button navigationMarkerButton in navigationMarkerButtons)
    {
        fontScaleController.Rebase(navigationMarkerButton, 10.0);
    }

    foreach (Button navigationMarkerDeleteButton in navigationMarkerDeleteButtons)
    {
        fontScaleController.Rebase(navigationMarkerDeleteButton, 12.0);
    }
}

private void NavigationMarkerPopup_KeyDown(object? sender, KeyEventArgs eventArgs)
{
    if (eventArgs.Key == Key.Escape)
    {
        navigationMarkerPopup.IsOpen = false;
        eventArgs.Handled = true;
    }
}

private void AddNavigationMarker()
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab != null)
    {
        DesktopNavigationMarker marker = WindowsNavigationMarkerPolicy.Create(timeline.CurrentBeat);
        IReadOnlyList<DesktopNavigationMarker> readOnlyList = WindowsNavigationMarkerPolicy.Add(activeTab.NavigationMarkers, marker, out var duplicate);
        if (duplicate)
        {
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("当前位置已有跳转标记 · {0}", marker.Beat));
            timeline.Focus();
            return;
        }

        if (!readOnlyList.Any((DesktopNavigationMarker candidate) => candidate.Id == marker.Id))
        {
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("跳转标记最多保留 {0} 个", 64));
            timeline.Focus();
            return;
        }

        viewModel.ReplaceNavigationMarkers(activeTab.Id, readOnlyList);
        editorOverview.SetNavigationMarkers(activeTab.NavigationMarkers);
        difficultyOverview.SetEndBeat(editorOverview.DisplayEndBeat);
        RefreshEventTimeRuler();
        RefreshNavigationMarkerUi();
        ScheduleNavigationMarkerSave();
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已添加跳转标记 · {0}", marker.Beat));
        timeline.Focus();
    }
}

private void RemoveNavigationMarker(Guid markerId)
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab != null)
    {
        IReadOnlyList<DesktopNavigationMarker> readOnlyList = WindowsNavigationMarkerPolicy.Remove(activeTab.NavigationMarkers, markerId);
        if (readOnlyList.Count != activeTab.NavigationMarkers.Count)
        {
            viewModel.ReplaceNavigationMarkers(activeTab.Id, readOnlyList);
            editorOverview.SetNavigationMarkers(activeTab.NavigationMarkers);
            difficultyOverview.SetEndBeat(editorOverview.DisplayEndBeat);
            RefreshEventTimeRuler();
            RefreshNavigationMarkerUi();
            ScheduleNavigationMarkerSave();
            viewModel.SetStatus("已删除跳转标记");
        }
    }
}

private void NavigateNavigationMarker(BeatPosition beat)
{
    if (viewModel.ActiveTab != null)
    {
        timeline.SetCurrentBeat(beat.Value);
        viewModel.UpdateCurrentBeat(beat.Value);
        timeline.Focus();
    }
}

private void ScheduleNavigationMarkerSave()
{
    navigationMarkerSettingsDirty = true;
    navigationMarkerRevision++;
    navigationMarkerSaveFailureCount = 0;
    navigationMarkerSaveTimer.Interval = NavigationMarkerSaveDelay;
    navigationMarkerSaveTimer.Stop();
    navigationMarkerSaveTimer.Start();
}

private void ScheduleNavigationMarkerRetry()
{
    navigationMarkerSaveFailureCount++;
    if (navigationMarkerSaveFailureCount <= 6)
    {
        double value = Math.Min(NavigationMarkerSaveDelay.TotalMilliseconds * Math.Pow(2.0, navigationMarkerSaveFailureCount), 30000.0);
        navigationMarkerSaveTimer.Interval = TimeSpan.FromMilliseconds(value);
        navigationMarkerSaveTimer.Stop();
        navigationMarkerSaveTimer.Start();
    }
}

private async Task<bool> PersistPendingNavigationMarkersAsync(bool force = false)
{
    navigationMarkerSaveTimer.Stop();
    if (!navigationMarkerSettingsDirty && !force)
    {
        return true;
    }

    await navigationMarkerPersistenceGate.WaitAsync();
    try
    {
        while (navigationMarkerSettingsDirty | force)
        {
            long revision = navigationMarkerRevision;
            if (!(await viewModel.PersistNavigationStateAsync()))
            {
                if (!force)
                {
                    ScheduleNavigationMarkerRetry();
                }

                return false;
            }

            if (revision != navigationMarkerRevision)
            {
                force = false;
                continue;
            }

            navigationMarkerSettingsDirty = false;
            navigationMarkerSaveFailureCount = 0;
            navigationMarkerSaveTimer.Interval = NavigationMarkerSaveDelay;
            return true;
        }

        return true;
    }
    finally
    {
        navigationMarkerPersistenceGate.Release();
    }
}

private Control BuildDifficultyOverviewPanel()
{
    difficultyStarText.Text = "—";
    difficultyStarText.FontFamily = new FontFamily("Cascadia Mono, Consolas");
    difficultyStarText.FontSize = 10.0;
    difficultyStarText.FontWeight = FontWeight.DemiBold;
    difficultyStarText.Foreground = new SolidColorBrush(Color.Parse("#ADB9BB"));
    difficultyStarText.HorizontalAlignment = HorizontalAlignment.Center;
    difficultyOverview.Margin = new Thickness(7.0, 0.0);
    AutomationProperties.SetAutomationId(difficultyOverview, "Editor.DifficultyOverview");
    AutomationProperties.SetName(difficultyOverview, "Rework 高难区段");
    difficultyOverview.SeekRequested += NavigateDifficultyOverview;
    Grid.SetRow(difficultyOverview, 1);
    StackPanel stackPanel = new StackPanel
    {
        Margin = new Thickness(0.0, 10.0, 0.0, 0.0),
        Children =
        {
            (Control)difficultyStarText,
            (Control)new TextBlock
            {
                Text = "4K",
                FontSize = 8.0,
                Foreground = new SolidColorBrush(Color.Parse("#7F878C")),
                HorizontalAlignment = HorizontalAlignment.Center
            }
        }
    };
    Grid.SetRow(stackPanel, 2);
    Grid grid = new Grid
    {
        RowDefinitions = new RowDefinitions("Auto,*,Auto")
    };
    grid.Children.Add(new TextBlock { Text = "DIFF", FontSize = 9.0, Foreground = new SolidColorBrush(Color.Parse("#7F878C")), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0.0, 42.0, 0.0, 8.0) });
    grid.Children.Add(difficultyOverview);
    grid.Children.Add(stackPanel);
    return new Border
    {
        Margin = new Thickness(4.0, 0.0, 0.0, 0.0),
        Background = new SolidColorBrush(Color.Parse("#181B1D")),
        BorderBrush = StrokeBrush,
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(4.0),
        Padding = new Thickness(6.0),
        Child = grid
    };
}

private void BuildDifficultyInfoCard()
{
    difficultyInfoCard.Width = 220.0;
    difficultyInfoCard.Height = WindowsDifficultyUiPolicy.ResolveCardHeight(editorViewSettings.DifficultyCardCollapsed);
    difficultyInfoCard.ClipToBounds = true;
    difficultyInfoCard.HorizontalAlignment = HorizontalAlignment.Left;
    difficultyInfoCard.VerticalAlignment = VerticalAlignment.Top;
    difficultyInfoCard.Margin = new Thickness(11.0, 14.0, 0.0, 0.0);
    difficultyInfoCard.Padding = new Thickness(11.0, 10.0);
    difficultyInfoCard.Background = new SolidColorBrush(Color.Parse("#DC171B1E"));
    difficultyInfoCard.BorderBrush = new SolidColorBrush(Color.Parse("#394246"));
    difficultyInfoCard.BorderThickness = new Thickness(1.0);
    difficultyInfoCard.CornerRadius = new CornerRadius(5.0);
    difficultyInfoCard.RenderTransform = difficultyInfoCardTransform;
    difficultyInfoCard.ZIndex = 18;
    difficultyInfoCard.IsVisible = false;
    AutomationProperties.SetAutomationId(difficultyInfoCard, "Editor.DifficultyCard");
    AutomationProperties.SetName(difficultyInfoCard, "4K Rework 难度卡片");
    difficultyInfoCardHeader.Height = 22.0;
    difficultyInfoCardHeader.Margin = new Thickness(-3.0, -3.0, -3.0, 0.0);
    difficultyInfoCardHeader.Background = new SolidColorBrush(Color.Parse("#202529"));
    difficultyInfoCardHeader.CornerRadius = new CornerRadius(3.0);
    difficultyInfoCardHeader.Cursor = new Cursor(StandardCursorType.SizeAll);
    ToolTip.SetTip(difficultyInfoCardHeader, "拖动难度卡片");
    AutomationProperties.SetAutomationId(difficultyInfoCardHeader, "Editor.DifficultyCard.Header");
    difficultyInfoCardHeader.PointerPressed += DifficultyCardHeader_PointerPressed;
    difficultyInfoCardHeader.PointerMoved += DifficultyCardHeader_PointerMoved;
    difficultyInfoCardHeader.PointerReleased += DifficultyCardHeader_PointerReleased;
    difficultyInfoCardHeader.PointerCaptureLost += (object? _, PointerCaptureLostEventArgs _) =>
    {
        EndDifficultyCardDrag();
    };
    Grid grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto,22"),
        Margin = new Thickness(6.0, 0.0, 2.0, 0.0)
    };
    grid.Children.Add(new TextBlock { Text = "4K REWORK", Foreground = new SolidColorBrush(Color.Parse("#91A4A8")), FontSize = 9.0, FontWeight = FontWeight.DemiBold, VerticalAlignment = VerticalAlignment.Center });
    difficultyCardStateText.Text = "待分析";
    difficultyCardStateText.Margin = new Thickness(0.0, 0.0, 4.0, 0.0);
    difficultyCardStateText.Foreground = new SolidColorBrush(Color.Parse("#718084"));
    difficultyCardStateText.FontSize = 9.0;
    difficultyCardStateText.VerticalAlignment = VerticalAlignment.Center;
    Grid.SetColumn(difficultyCardStateText, 1);
    grid.Children.Add(difficultyCardStateText);
    difficultyCardCollapseButton.Content = "—";
    difficultyCardCollapseButton.Width = 20.0;
    difficultyCardCollapseButton.Height = 18.0;
    difficultyCardCollapseButton.Padding = new Thickness(0.0);
    difficultyCardCollapseButton.Focusable = false;
    difficultyCardCollapseButton.Background = RaisedBrush;
    difficultyCardCollapseButton.Foreground = Brushes.White;
    difficultyCardCollapseButton.BorderBrush = StrokeBrush;
    difficultyCardCollapseButton.BorderThickness = new Thickness(1.0);
    difficultyCardCollapseButton.CornerRadius = new CornerRadius(3.0);
    difficultyCardCollapseButton.Click += (object? _, RoutedEventArgs eventArgs) =>
    {
        eventArgs.Handled = true;
        ToggleDifficultyCardCollapsed();
    };
    AutomationProperties.SetAutomationId(difficultyCardCollapseButton, "Editor.DifficultyCard.Collapse");
    Grid.SetColumn(difficultyCardCollapseButton, 2);
    grid.Children.Add(difficultyCardCollapseButton);
    difficultyInfoCardHeader.Child = grid;
    difficultyInfoCardBody.IsHitTestVisible = false;
    difficultyCardTitleText.Text = "Untitled / Normal";
    difficultyCardTitleText.Margin = new Thickness(0.0, 7.0, 0.0, 0.0);
    difficultyCardTitleText.Foreground = new SolidColorBrush(Color.Parse("#DDE3E4"));
    difficultyCardTitleText.FontSize = 12.0;
    difficultyCardTitleText.FontWeight = FontWeight.DemiBold;
    difficultyCardTitleText.TextTrimming = TextTrimming.CharacterEllipsis;
    difficultyInfoCardBody.Children.Add(difficultyCardTitleText);
    difficultyCardStarText.Text = "—";
    difficultyCardStarText.FontFamily = new FontFamily("Cascadia Mono, Consolas");
    difficultyCardStarText.FontSize = 30.0;
    difficultyCardStarText.FontWeight = FontWeight.DemiBold;
    difficultyCardStarText.Foreground = new SolidColorBrush(Color.Parse("#E5EBEC"));
    difficultyCardReferenceText.Text = "运行顶部“难度”分析";
    difficultyCardReferenceText.Margin = new Thickness(11.0, 0.0, 0.0, 1.0);
    difficultyCardReferenceText.VerticalAlignment = VerticalAlignment.Bottom;
    difficultyCardReferenceText.Foreground = new SolidColorBrush(Color.Parse("#AFC1C3"));
    difficultyCardReferenceText.FontSize = 11.0;
    difficultyCardReferenceText.FontWeight = FontWeight.DemiBold;
    difficultyCardReferenceText.TextWrapping = TextWrapping.Wrap;
    Grid grid2 = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
    };
    grid2.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { (Control)difficultyCardStarText, (Control)new TextBlock { Text = "★", Margin = new Thickness(5.0, 5.0, 0.0, 0.0), VerticalAlignment = VerticalAlignment.Center, FontSize = 13.0, Foreground = new SolidColorBrush(Color.Parse("#82AEB1")) } } });
    Grid.SetColumn(difficultyCardReferenceText, 1);
    grid2.Children.Add(difficultyCardReferenceText);
    difficultyInfoCardBody.Children.Add(grid2);
    difficultyInfoCardBody.Children.Add(new Border { Height = 1.0, Background = new SolidColorBrush(Color.Parse("#30373B")), Margin = new Thickness(0.0, 10.0, 0.0, 9.0) });
    Grid grid3 = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,*,*")
    };
    AddDifficultyCardMetric(grid3, difficultyCardLnText, "LN", 0);
    AddDifficultyCardMetric(grid3, difficultyCardNoteText, "NOTES", 1);
    AddDifficultyCardMetric(grid3, difficultyCardOdText, "SETTING", 2);
    difficultyInfoCardBody.Children.Add(grid3);
    Grid grid4 = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        Margin = new Thickness(0.0, 13.0, 0.0, 6.0)
    };
    grid4.Children.Add(new TextBlock { Text = "/pattern", FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 10.0, Foreground = new SolidColorBrush(Color.Parse("#7E969A")), FontWeight = FontWeight.DemiBold });
    difficultyCardPatternFamilyText.Text = "等待分析";
    difficultyCardPatternFamilyText.HorizontalAlignment = HorizontalAlignment.Right;
    difficultyCardPatternFamilyText.Foreground = new SolidColorBrush(Color.Parse("#D6DFE0"));
    difficultyCardPatternFamilyText.FontSize = 11.0;
    difficultyCardPatternFamilyText.FontWeight = FontWeight.DemiBold;
    Grid.SetColumn(difficultyCardPatternFamilyText, 1);
    grid4.Children.Add(difficultyCardPatternFamilyText);
    difficultyInfoCardBody.Children.Add(grid4);
    AutomationProperties.SetAutomationId(difficultyCardPatternList, "Editor.DifficultyCard.Patterns");
    difficultyInfoCardBody.Children.Add(difficultyCardPatternList);
    difficultyCardPatternHintText.Text = "分析后显示主要 Pattern 构成";
    difficultyCardPatternHintText.Foreground = new SolidColorBrush(Color.Parse("#687579"));
    difficultyCardPatternHintText.FontSize = 9.0;
    difficultyCardPatternHintText.Margin = new Thickness(0.0, 2.0, 0.0, 0.0);
    difficultyInfoCardBody.Children.Add(difficultyCardPatternHintText);
    StackPanel child = new StackPanel
    {
        Children =
        {
            (Control)difficultyInfoCardHeader,
            (Control)difficultyInfoCardBody
        }
    };
    difficultyInfoCard.Child = child;
    ApplyDifficultyCardCollapsedState();
    Grid.SetRow(difficultyInfoCard, 0);
    timelineHost.Children.Add(difficultyInfoCard);
}

private static void AddDifficultyCardMetric(Grid parent, TextBlock value, string label, int column)
{
    value.Text = column switch
    {
        0 => "—",
        1 => "0",
        _ => "OD8.0",
    };
    value.FontFamily = new FontFamily("Cascadia Mono, Consolas");
    value.FontSize = 12.0;
    value.FontWeight = FontWeight.DemiBold;
    value.Foreground = new SolidColorBrush(Color.Parse("#D5DDDE"));
    StackPanel stackPanel = new StackPanel
    {
        Children =
        {
            (Control)value,
            (Control)new TextBlock
            {
                Text = label,
                FontSize = 8.0,
                Foreground = new SolidColorBrush(Color.Parse("#758085"))
            }
        }
    };
    Grid.SetColumn(stackPanel, column);
    parent.Children.Add(stackPanel);
}

private DifficultyTabState GetDifficultyState(DesktopChartTab tab)
{
    if (difficultyStates.TryGetValue(tab.Id, out DifficultyTabState value))
    {
        return value;
    }

    value = new DifficultyTabState
    {
        OverallDifficulty = Math.Clamp(tab.Session.Document.Metadata.OverallDifficulty, 0.0, 10.0)
    };
    difficultyStates.Add(tab.Id, value);
    return value;
}

private async Task ShowDifficultyAnalysisAsync()
{
    DesktopChartTab targetTab = viewModel.ActiveTab;
    if (targetTab == null)
    {
        await ShowSettingsMessageAsync("难度分析", "请先打开一张谱面。");
        return;
    }

    if (targetTab.Session.Document.KeyCount == 4)
    {
        long targetSerial = targetTab.Session.ChangeSerial;
        ChartDocument snapshot = targetTab.Session.CaptureCommittedDocument();
        DifficultyTabState state = GetDifficultyState(targetTab);
        double analysisOd = Math.Clamp(state.OverallDifficulty ?? snapshot.Metadata.OverallDifficulty, 0.0, 10.0);
        difficultyAnalysisCancellation?.Cancel();
        difficultyAnalysisCancellation?.Dispose();
        CancellationTokenSource cancellation = (difficultyAnalysisCancellation = new CancellationTokenSource());
        if (difficultyAnalysisButton != null)
        {
            difficultyAnalysisButton.IsEnabled = false;
        }

        Cursor = new Cursor(StandardCursorType.Wait);
        viewModel.SetStatus("正在计算 4K Rework 难度…");
        try
        {
            try
            {
                ReworkDifficultyResult reworkDifficultyResult = await (Task<ReworkDifficultyResult>)(activeDifficultyAnalysisTask = Task.Run(() => ReworkDifficultyAnalyzer.Analyze(snapshot, 1.0, analysisOd), cancellation.Token));
                cancellation.Token.ThrowIfCancellationRequested();
                if (!WindowsDifficultyUiPolicy.IsAnalysisCurrent(targetTab.Id, targetSerial, viewModel.ActiveTab?.Id, viewModel.ActiveTab?.Session.ChangeSerial ?? (-1)))
                {
                    viewModel.SetStatus("谱面已变化，请重新运行难度分析");
                    return;
                }

                state.Result = reworkDifficultyResult;
                state.ChangeSerial = targetSerial;
                state.OverallDifficulty = reworkDifficultyResult.OverallDifficulty;
                RefreshDifficultyUi();
                viewModel.SetStatus($"Rework {reworkDifficultyResult.StarRating:0.00}★ · {reworkDifficultyResult.ReferenceDifficulty}");
                await new DifficultyAnalysisWindow(reworkDifficultyResult, NavigateDifficultyOverview, (double od, CancellationToken token) => Task.Run(() => ReworkDifficultyAnalyzer.Analyze(snapshot, 1.0, od), token), (ReworkDifficultyResult updatedResult) =>
                {
                    if (WindowsDifficultyUiPolicy.IsAnalysisCurrent(targetTab.Id, targetSerial, viewModel.ActiveTab?.Id, viewModel.ActiveTab?.Session.ChangeSerial ?? (-1)))
                    {
                        state.Result = updatedResult;
                        state.ChangeSerial = targetSerial;
                        state.OverallDifficulty = updatedResult.OverallDifficulty;
                        RefreshDifficultyUi();
                        viewModel.SetStatus($"Rework {updatedResult.StarRating:0.00}★ · OD{updatedResult.OverallDifficulty:0.0}");
                    }
                }, displayScaleController.CurrentSettings.UiScale, displayScaleController.CurrentSettings.FontScale).ShowDialog<bool>(this);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex2)when ((ex2 is InvalidOperationException || ex2 is NotSupportedException) ? true : false)
            {
                await ShowSettingsMessageAsync("难度分析", ex2.Message);
            }
            catch (Exception ex3)
            {
                await ShowSettingsMessageAsync("难度分析失败", ex3.Message);
            }

            return;
        }
        finally
        {
            if (difficultyAnalysisCancellation == cancellation)
            {
                difficultyAnalysisCancellation.Dispose();
                difficultyAnalysisCancellation = null;
            }

            Cursor = null;
            if (difficultyAnalysisButton != null)
            {
                difficultyAnalysisButton.IsEnabled = viewModel.ActiveTab != null;
            }
        }
    }

    await ShowSettingsMessageAsync("难度分析", "Rework 难度分析目前只用于 4K 谱面。");
}

private void NavigateDifficultyOverview(double beat)
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab != null)
    {
        BeatPosition beatPosition = TimelineGridNavigation.Snap(double.IsFinite(beat) ? Math.Max(0.0, beat) : 0.0, activeTab.Session.SnapDivision);
        timeline.SetCurrentBeat(beatPosition.Value);
        viewModel.UpdateCurrentBeat(beatPosition.Value);
        timeline.InvalidateVisual();
        difficultyOverview.SetCurrentBeat(beatPosition.Value);
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            timeline.Focus();
        }, DispatcherPriority.Input);
    }
}

private async Task StartPlaytestAsync(double startBeat, bool settingsOnly = false)
{
    if (playtestInProgress)
    {
        return;
    }

    playtestInProgress = true;
    CancelNotePaste();
    timeline.CancelInteraction();
    foreach (TrackView value in secondaryTrackViews.Values)
    {
        value.Surface?.CancelInteraction();
    }

    addTrackContextMenu?.Close();
    editorView.IsEnabled = false;
    StopEditorHighRateFramePump();
    try
    {
        await PauseAllEditorPlaybackAsync();
        await StartPlaytestCoreAsync(startBeat, settingsOnly);
    }
    catch (Exception ex)
    {
        await ShowSettingsMessageAsync("无法开始试玩", ex.Message);
    }
    finally
    {
        playtestInProgress = false;
        editorView.IsEnabled = true;
        ConfigureEditorFrameRateRuntime(resetCounters: true);
    }
}

private async Task StartPlaytestCoreAsync(double startBeat, bool settingsOnly)
{
    DesktopChartTab tab = viewModel.ActiveTab;
    if (tab == null)
    {
        await ShowSettingsMessageAsync("测试演奏", "请先打开一张谱面。");
        return;
    }

    ChartDocument snapshot = tab.Session.CaptureCommittedDocument();
    if (settingsOnly || !snapshot.Notes.All((ChartNote note) => note.IsDraft))
    {
        try
        {
            try
            {
                viewModel.SetStatus("正在保存测试工作副本…");
                if (!(await viewModel.PersistAsync()))
                {
                    throw new IOException(viewModel.Status);
                }

                if (!File.Exists(tab.WorkingPath))
                {
                    throw new IOException("当前谱面没有可用的编辑缓存工作副本。");
                }

                snapshot = tab.Session.CaptureCommittedDocument();
                await PauseAllEditorPlaybackAsync();
                WindowsPlaytestSettings settings = WindowsPlaytestSettingsStore.Load();
                double startBeat2 = (double.IsFinite(startBeat) ? startBeat : 0.0);
                StopEditorHighRateFramePump();
                viewModel.SetStatus("正在启动官方 osu!mania 试玩…");
                await OfficialPlaytestLauncher.RunAsync(snapshot, File.Exists(tab.SourcePath) ? tab.SourcePath : tab.WorkingPath, startBeat2, settings, playtestRate, preservePitch: true, audioTransport.Volume, settingsOnly);
                viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("测试演奏已退出 · 源谱未写入 · 缓存 {0}", System.IO.Path.GetFileName(tab.WorkingPath)));
            }
            catch (Exception ex)
            {
                await ShowSettingsMessageAsync("无法开始测试演奏；原谱面未被修改", ex.Message);
            }

            return;
        }
        finally
        {
            InvalidateChartBackgroundCache();
            UpdateUi();
            if (IsVisible && WindowState != WindowState.Minimized)
            {
                Activate();
            }

            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                timeline.Focus();
            }, DispatcherPriority.Input);
        }
    }

    await ShowSettingsMessageAsync("测试演奏", "当前谱面没有可测试的正式 Note。");
}

private async Task PauseAllEditorPlaybackAsync()
{
    if (keySoundRuntimeDisposed)
    {
        return;
    }

    await editorPlaybackCommandGate.WaitAsync();
    try
    {
        await CancelPendingEditorSeek();
        keySoundRuntime.InvalidatePlayback();
        await audioTransport.PauseAsync();
        if (audioTransport.IsPlaying)
        {
            throw new IOException("编辑音频未能暂停，请重试试玩。");
        }

        keySoundRuntime.PauseSilent();
        keySoundRuntime.InvalidatePlayback();
    }
    finally
    {
        editorPlaybackCommandGate.Release();
    }

    UpdateQuickPlayPresentation();
}

private void RefreshDifficultyUi()
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    UpdateGlobalLoad(activeTab?.Session);
    if (activeTab == null)
    {
        difficultyOverview.SetResult(null);
        difficultyOverview.SetEndBeat(1.0);
        difficultyOverview.SetCurrentBeat(0.0);
        difficultyStarText.Text = "—";
        difficultyInfoCard.IsVisible = false;
        if (difficultyAnalysisButton != null)
        {
            difficultyAnalysisButton.IsEnabled = false;
        }

        return;
    }

    DifficultyTabState difficultyState = GetDifficultyState(activeTab);
    if (difficultyState.Result != null && difficultyState.ChangeSerial != activeTab.Session.ChangeSerial)
    {
        difficultyState.Result = null;
    }

    ReworkDifficultyResult reworkDifficultyResult = ((difficultyState.ChangeSerial == activeTab.Session.ChangeSerial) ? difficultyState.Result : null);
    ChartDocument document = activeTab.Session.Document;
    difficultyOverview.SetResult(reworkDifficultyResult);
    difficultyOverview.SetEndBeat(editorOverview.DisplayEndBeat);
    difficultyOverview.SetCurrentBeat(timeline.CurrentBeat);
    TextBlock textBlock = difficultyStarText;
    string? text;
    if (document.KeyCount != 4)
    {
        text = "N/A";
    }
    else
    {
        text = ((reworkDifficultyResult == null) ? "—" : reworkDifficultyResult.StarRating.ToString("0.00", CultureInfo.InvariantCulture));
    }

    textBlock.Text = text;
    if (difficultyAnalysisButton != null)
    {
        difficultyAnalysisButton.IsEnabled = document.KeyCount == 4 && difficultyAnalysisCancellation == null;
    }

    RefreshDifficultyCard(activeTab, difficultyState, reworkDifficultyResult);
    QueueDifficultyCardLayout();
}

private void RefreshDifficultyCard(DesktopChartTab tab, DifficultyTabState state, ReworkDifficultyResult? result)
{
    ChartDocument document = tab.Session.Document;
    string text = (string.IsNullOrWhiteSpace(document.Metadata.Title) ? "Untitled" : document.Metadata.Title.Trim());
    string text2 = (string.IsNullOrWhiteSpace(document.Metadata.Version) ? "Normal" : document.Metadata.Version.Trim());
    difficultyCardTitleText.Text = text + "  /  " + text2;
    ToolTip.SetTip(difficultyCardTitleText, difficultyCardTitleText.Text);
    ChartNote[] array = document.Notes.Where((ChartNote note) => !note.IsDraft).ToArray();
    int num = array.Count((ChartNote note) => note.IsHold);
    double num2 = ((array.Length == 0) ? 0.0 : ((double)num / (double)array.Length));
    double num3 = Math.Clamp(state.OverallDifficulty ?? document.Metadata.OverallDifficulty, 0.0, 10.0);
    difficultyCardNoteText.Text = (result?.NoteCount ?? array.Length).ToString("N0", CultureInfo.InvariantCulture);
    difficultyCardLnText.Text = (result?.LongNoteRatio ?? num2).ToString("P1", CultureInfo.InvariantCulture);
    difficultyCardOdText.Text = $"OD{result?.OverallDifficulty ?? num3:0.0}";
    difficultyCardPatternList.Children.Clear();
    if (result == null)
    {
        difficultyCardStateText.Text = "待分析";
        difficultyCardStarText.Text = "—";
        difficultyCardReferenceText.Text = ((array.Length < 2) ? "至少需要 2 个正式 Note" : "运行顶部“难度”分析");
        difficultyCardPatternFamilyText.Text = "等待分析";
        difficultyCardPatternList.IsVisible = false;
        difficultyCardPatternHintText.IsVisible = true;
        return;
    }

    difficultyCardStateText.Text = "已分析";
    difficultyCardStarText.Text = result.StarRating.ToString("0.00", CultureInfo.InvariantCulture);
    difficultyCardReferenceText.Text = result.ReferenceDifficulty;
    difficultyCardPatternFamilyText.Text = result.PatternFamily;
    ReworkPatternContribution[] array2 = result.Patterns.Take(5).ToArray();
    double maximumRatio = Math.Max(0.0001, array2.FirstOrDefault()?.Ratio ?? 1.0);
    ReworkPatternContribution[] array3 = array2;
    foreach (ReworkPatternContribution pattern in array3)
    {
        difficultyCardPatternList.Children.Add(BuildDifficultyPatternRow(pattern, maximumRatio));
    }

    difficultyCardPatternList.IsVisible = array2.Length != 0;
    difficultyCardPatternHintText.IsVisible = array2.Length == 0;
}

private static Control BuildDifficultyPatternRow(ReworkPatternContribution pattern, double maximumRatio)
{
    Grid grid = new Grid
    {
        Height = 21.0,
        ColumnDefinitions = new ColumnDefinitions("62,*,34"),
        Children =
        {
            (Control)new TextBlock
            {
                Text = pattern.Name,
                Foreground = new SolidColorBrush(Color.Parse("#AEB8BB")),
                FontSize = 9.0,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        }
    };
    Border border = new Border
    {
        Height = 4.0,
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.Parse("#293034")),
        CornerRadius = new CornerRadius(2.0),
        Child = new Border
        {
            Width = Math.Max(7.0, Math.Round(50.0 * pattern.Ratio / maximumRatio)),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.Parse("#6FA7AA")),
            CornerRadius = new CornerRadius(2.0)
        }
    };
    Grid.SetColumn(border, 1);
    grid.Children.Add(border);
    TextBlock textBlock = new TextBlock
    {
        Text = pattern.Ratio.ToString("P1", CultureInfo.InvariantCulture),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        Foreground = new SolidColorBrush(Color.Parse("#879498")),
        FontSize = 9.0
    };
    Grid.SetColumn(textBlock, 2);
    grid.Children.Add(textBlock);
    return grid;
}

private void QueueDifficultyCardLayout()
{
    if (!difficultyCardLayoutQueued)
    {
        difficultyCardLayoutQueued = true;
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            difficultyCardLayoutQueued = false;
            ApplyDifficultyCardLayout();
        }, DispatcherPriority.Loaded);
    }
}

private void ApplyDifficultyCardLayout()
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null || activeTab.Session.Document.KeyCount != 4 || timelineHost.Bounds.Width <= 0.0 || timelineHost.Bounds.Height <= 0.0)
    {
        difficultyInfoCard.IsVisible = false;
        return;
    }

    Rect activeTrackBounds = timeline.GetActiveTrackBounds();
    Point? point = ((activeTrackBounds.Width > 0.0) ? timeline.TranslatePoint(new Point(activeTrackBounds.Left, 0.0), timelineHost) : ((Point? )null));
    if (point.HasValue)
    {
        Point valueOrDefault = point.GetValueOrDefault();
        double? leftDockedPanelLeft = null;
        if (waveformPanel.IsVisible)
        {
            Point? point2 = waveformPanel.TranslatePoint(new Point(0.0, 0.0), timelineHost);
            if (point2.HasValue)
            {
                Point valueOrDefault2 = point2.GetValueOrDefault();
                if (valueOrDefault2.X + waveformPanel.Bounds.Width <= valueOrDefault.X + 1.0)
                {
                    leftDockedPanelLeft = valueOrDefault2.X;
                }
            }
        }

        WindowsDifficultyCardLayout windowsDifficultyCardLayout = WindowsDifficultyUiPolicy.ResolveCardLayout(hasFourKeyChart: true, timelineHost.Bounds.Width, timelineHost.Bounds.Height, valueOrDefault.X, leftDockedPanelLeft, editorViewSettings.DifficultyCardCollapsed, difficultyInfoCardTransform.X, difficultyInfoCardTransform.Y, difficultyInfoCard.Bounds.Height);
        if (windowsDifficultyCardLayout.Collapsed != editorViewSettings.DifficultyCardCollapsed)
        {
            editorViewSettings = editorViewSettings.WithDifficultyCard(windowsDifficultyCardLayout.X, windowsDifficultyCardLayout.Y, windowsDifficultyCardLayout.Collapsed);
            ApplyDifficultyCardCollapsedState();
            MarkEditorViewSettingsDirty();
        }

        difficultyInfoCard.Width = windowsDifficultyCardLayout.Width;
        difficultyInfoCard.Height = WindowsDifficultyUiPolicy.ResolveCardHeight(windowsDifficultyCardLayout.Collapsed);
        difficultyInfoCardTransform.X = windowsDifficultyCardLayout.X;
        difficultyInfoCardTransform.Y = windowsDifficultyCardLayout.Y;
        difficultyInfoCard.IsVisible = windowsDifficultyCardLayout.Visible;
        AutomationProperties.SetHelpText(difficultyInfoCard, $"FixedSize={difficultyInfoCard.Width:0}x{difficultyInfoCard.Height:0}; Collapsed={windowsDifficultyCardLayout.Collapsed}; X={windowsDifficultyCardLayout.X:0.###}; Y={windowsDifficultyCardLayout.Y:0.###}; NeighborAvailableWidth={windowsDifficultyCardLayout.AvailableWidth:0.###}");
    }
    else
    {
        difficultyInfoCard.IsVisible = false;
    }
}

private void DifficultyCardHeader_PointerPressed(object? sender, PointerPressedEventArgs eventArgs)
{
    if (eventArgs.GetCurrentPoint(difficultyInfoCardHeader).Properties.IsLeftButtonPressed && !difficultyCardCollapseButton.IsPointerOver)
    {
        difficultyCardDragging = true;
        difficultyCardDragStart = eventArgs.GetPosition(timelineHost);
        difficultyCardDragStartX = difficultyInfoCardTransform.X;
        difficultyCardDragStartY = difficultyInfoCardTransform.Y;
        eventArgs.Pointer.Capture(difficultyInfoCardHeader);
        eventArgs.Handled = true;
    }
}

private void DifficultyCardHeader_PointerMoved(object? sender, PointerEventArgs eventArgs)
{
    if (difficultyCardDragging && eventArgs.GetCurrentPoint(difficultyInfoCardHeader).Properties.IsLeftButtonPressed)
    {
        Point position = eventArgs.GetPosition(timelineHost);
        (double, double) tuple = WindowsDifficultyUiPolicy.ApplyDrag(difficultyCardDragStartX + position.X - difficultyCardDragStart.X, difficultyCardDragStartY + position.Y - difficultyCardDragStart.Y, timelineHost.Bounds.Width, timelineHost.Bounds.Height, difficultyInfoCard.Bounds.Width, difficultyInfoCard.Bounds.Height);
        difficultyInfoCardTransform.X = tuple.Item1;
        difficultyInfoCardTransform.Y = tuple.Item2;
        eventArgs.Handled = true;
    }
}

private void DifficultyCardHeader_PointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
{
    if (difficultyCardDragging)
    {
        EndDifficultyCardDrag();
        eventArgs.Pointer.Capture(null);
        eventArgs.Handled = true;
    }
}

private void EndDifficultyCardDrag()
{
    if (difficultyCardDragging)
    {
        difficultyCardDragging = false;
        editorViewSettings = editorViewSettings.WithDifficultyCard(difficultyInfoCardTransform.X, difficultyInfoCardTransform.Y, editorViewSettings.DifficultyCardCollapsed);
        MarkEditorViewSettingsDirty();
    }
}

private void ToggleDifficultyCardCollapsed()
{
    editorViewSettings = editorViewSettings.WithDifficultyCard(difficultyInfoCardTransform.X, difficultyInfoCardTransform.Y, !editorViewSettings.DifficultyCardCollapsed);
    ApplyDifficultyCardCollapsedState();
    MarkEditorViewSettingsDirty();
    QueueDifficultyCardLayout();
}

private void ApplyDifficultyCardCollapsedState()
{
    bool difficultyCardCollapsed = editorViewSettings.DifficultyCardCollapsed;
    difficultyInfoCardBody.IsVisible = !difficultyCardCollapsed;
    difficultyCardCollapseButton.Content = (difficultyCardCollapsed ? "+" : "—");
    ToolTip.SetTip(difficultyCardCollapseButton, difficultyCardCollapsed ? "展开难度卡片" : "收起难度卡片");
    difficultyInfoCard.Padding = (difficultyCardCollapsed ? new Thickness(6.0, 5.0) : new Thickness(11.0, 10.0));
    difficultyInfoCard.Width = 220.0;
    difficultyInfoCard.Height = WindowsDifficultyUiPolicy.ResolveCardHeight(difficultyCardCollapsed);
}

private static Control WrapInspectorContent(Control content, Thickness? padding = null)
{
    return new ScrollViewer
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = new Border
        {
            Padding = (padding ?? new Thickness(22.0, 14.0, 22.0, 24.0)),
            Child = content
        }
    };
}

private Control BuildQuickEditPalette()
{
    StackPanel stackPanel = new StackPanel
    {
        Spacing = 1.0
    };
    Button button = QuickPaletteButton("剪切", "M5,2 A2.5,2.5 0 1 0 5,7 A2.5,2.5 0 1 0 5,2 M5,10 A2.5,2.5 0 1 0 5,15 A2.5,2.5 0 1 0 5,10 M7,6 L15,14 M7,11 L15,3", (object? _, RoutedEventArgs _) =>
    {
        CutMixedSelection();
    }, out TextBlock label);
    AutomationProperties.SetAutomationId(button, "Editor.QuickCut");
    RegisterQuickShortcutButton("Cut", "剪切选中的 Note / Effect", button);
    stackPanel.Children.Add(button);
    Button button2 = QuickPaletteButton("复制", "M5,2 H15 V12 H5 Z M2,5 H5 M2,5 V15 H12 V12", (object? _, RoutedEventArgs _) =>
    {
        CopyMixedSelection();
    }, out label);
    AutomationProperties.SetAutomationId(button2, "Editor.QuickCopy");
    RegisterQuickShortcutButton("Copy", "复制选中的 Note / Effect", button2);
    stackPanel.Children.Add(button2);
    Button button3 = QuickPaletteButton("粘贴", "M5,3 H13 V15 H3 V3 H6 M6,2 H10 V5 H6 Z M7,9 H11 M7,12 H11", (object? _, RoutedEventArgs _) =>
    {
        PasteMixedSelection();
    }, out label);
    AutomationProperties.SetAutomationId(button3, "Editor.QuickPaste");
    RegisterQuickShortcutButton("Paste", "移动虚影后单击粘贴", button3);
    stackPanel.Children.Add(button3);
    Button button4 = QuickPaletteButton("全选", "M2,2 H15 V15 H2 Z M5,5 H12 V12 H5 Z", (object? _, RoutedEventArgs _) =>
    {
        SelectAllVisibleObjects();
    }, out label);
    AutomationProperties.SetAutomationId(button4, "Editor.QuickSelectAll");
    RegisterQuickShortcutButton("SelectAll", "全选可见 Note 与 Effect", button4);
    stackPanel.Children.Add(button4);
    Button button5 = (quickMirrorButton = QuickPaletteButton("镜像", "M8.5,1 V16 M6,4 L2,8.5 L6,13 M11,4 L15,8.5 L11,13", (object? _, RoutedEventArgs _) =>
    {
        MirrorVisibleSelection();
    }, out label));
    AutomationProperties.SetAutomationId(button5, "Editor.QuickMirror");
    RegisterQuickShortcutButton("Mirror", "左右镜像选中的 Note", button5);
    stackPanel.Children.Add(button5);
    Button button6 = QuickPaletteButton("翻转", "M1,8 H16 M4,5 L8,1 L12,5 M4,11 L8,15 L12,11", (object? _, RoutedEventArgs _) =>
    {
        FlipVisibleSelection();
    }, out label);
    AutomationProperties.SetAutomationId(button6, "Editor.QuickFlip");
    RegisterQuickShortcutButton("Flip", "上下翻转选中的 Note", button6);
    stackPanel.Children.Add(button6);
    StackPanel stackPanel2 = new StackPanel
    {
        Spacing = 1.0
    };
    AddQuickTimelineToolButton(stackPanel2, TimelineTool.Select, "选择", "M1,1 L13,9 L8.5,10 L11,15 L8.5,16 L5.8,11 L1,14 Z", filledIcon: true);
    AddQuickTimelineToolButton(stackPanel2, TimelineTool.Tap, "单键", "M1,5 H16 V12 H1 Z M5,8 H12");
    AddQuickTimelineToolButton(stackPanel2, TimelineTool.Hold, "长键", "M4,1 H13 V17 H4 Z M7,4 H10 V14 H7 Z");
    AddQuickTimelineToolButton(stackPanel2, TimelineTool.Erase, "擦除", "M2,11 L10,2 L15,7 L8,14 H4 Z M8,14 H15");
    Button button7 = QuickPaletteButton("分组 0", "M8.5,1 A7.5,7.5 0 1 1 8.5,16 A7.5,7.5 0 1 1 8.5,1", (object? _, RoutedEventArgs _) =>
    {
        editorInspectorTabs.SelectedItem = groupInspectorTab;
        groupIdBox.Focus();
        groupIdBox.SelectAll();
    }, out TextBlock label2);
    quickGroupLabel = label2;
    AutomationProperties.SetAutomationId(button7, "Editor.QuickGroup");
    ToolTip.SetTip(button7, "设置当前放置分组");
    button7.IsVisible = false;
    Button button8 = (quickEffectButton = QuickPaletteButton("效果", "M8.5,2 V15 M2,8.5 H15", (object? _, RoutedEventArgs _) =>
    {
        BeginQuickEffectEdit();
    }, out label));
    AutomationProperties.SetAutomationId(button8, "Editor.QuickEffect");
    ToolTip.SetTip(button8, "在红线位置准备添加 Effect");
    stackPanel2.Children.Add(FullModeOnly(button8));
    quickOwnerLockButton = QuickPaletteButton("锁定选区", "M5,8 V5.5 A3.5,3.5 0 0 1 12,5.5 V8 M3,8 H14 V15 H3 Z", async (object? _, RoutedEventArgs _) =>
    {
        await ToggleOwnerLockSelectionAsync();
    }, out TextBlock label3);
    quickOwnerLockLabel = label3;
    quickOwnerLockButton.IsVisible = false;
    quickOwnerLockButton.IsEnabled = false;
    AutomationProperties.SetAutomationId(quickOwnerLockButton, "Editor.QuickOwnerLock");
    ToolTip.SetTip(quickOwnerLockButton, "房主锁定 / 解锁当前选择精确覆盖的 Beat 范围；末端不含");
    stackPanel2.Children.Add(quickOwnerLockButton);
    quickEditPaletteBody.Orientation = Orientation.Horizontal;
    quickEditPaletteBody.Spacing = 5.0;
    quickEditPaletteBody.Margin = new Thickness(0.0, 5.0, 0.0, 0.0);
    quickEditPaletteBody.Children.Add(new Border { Width = 108.0, Child = stackPanel });
    quickEditPaletteBody.Children.Add(new Border { Width = 1.0, Background = new SolidColorBrush(Color.Parse("#343A3E")), Margin = new Thickness(1.0, 3.0) });
    quickEditPaletteBody.Children.Add(new Border { Width = 108.0, Child = stackPanel2 });
    quickEditPaletteCollapseButton.Content = "−";
    quickEditPaletteCollapseButton.Width = 23.0;
    quickEditPaletteCollapseButton.Height = 20.0;
    quickEditPaletteCollapseButton.Padding = new Thickness(0.0);
    quickEditPaletteCollapseButton.Background = PanelBrush;
    quickEditPaletteCollapseButton.Foreground = Brushes.White;
    quickEditPaletteCollapseButton.BorderBrush = StrokeBrush;
    quickEditPaletteCollapseButton.BorderThickness = new Thickness(1.0);
    quickEditPaletteCollapseButton.CornerRadius = new CornerRadius(3.0);
    AutomationProperties.SetAutomationId(quickEditPaletteCollapseButton, "Editor.QuickPalette.Toggle");
    quickEditPaletteCollapseButton.Click += (object? _, RoutedEventArgs _) =>
    {
        editorViewSettings = editorViewSettings.WithQuickPalette(quickEditPaletteTransform.X, quickEditPaletteTransform.Y, !editorViewSettings.QuickPaletteCollapsed);
        ApplyQuickEditPaletteLayout();
        editorViewSettingsDirty = true;
        ScheduleEditorViewSettingsSave();
    };
    Border border = new Border
    {
        Width = 20.0,
        Height = 2.0,
        Margin = new Thickness(0.0, 0.0, 5.0, 0.0),
        Background = new SolidColorBrush(Color.Parse("#566166")),
        CornerRadius = new CornerRadius(1.0),
        VerticalAlignment = VerticalAlignment.Center
    };
    Border border2 = new Border
    {
        Height = 28.0,
        Cursor = new Cursor(StandardCursorType.SizeAll),
        Background = new SolidColorBrush(Color.Parse("#ED1A1E20")),
        BorderBrush = new SolidColorBrush(Color.Parse("#31383B")),
        BorderThickness = new Thickness(0.0, 0.0, 0.0, 1.0),
        CornerRadius = new CornerRadius(3.0, 3.0, 0.0, 0.0),
        Child = new Grid
        {
            Margin = new Thickness(8.0, 0.0, 3.0, 0.0),
            ColumnDefinitions = new ColumnDefinitions("*,Auto,25"),
            Children =
            {
                (Control)new TextBlock
                {
                    Text = "快捷编辑",
                    Foreground = new SolidColorBrush(Color.Parse("#AAB4B7")),
                    FontSize = 10.0,
                    FontWeight = FontWeight.DemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                },
                (Control)border,
                (Control)quickEditPaletteCollapseButton
            }
        }
    };
    Grid.SetColumn(border, 1);
    Grid.SetColumn(quickEditPaletteCollapseButton, 2);
    ToolTip.SetTip(border2, "按住拖动快捷浮窗");
    border2.PointerPressed += QuickEditPaletteHeader_PointerPressed;
    border2.PointerMoved += QuickEditPaletteHeader_PointerMoved;
    border2.PointerReleased += QuickEditPaletteHeader_PointerReleased;
    border2.PointerCaptureLost += (object? _, PointerCaptureLostEventArgs _) =>
    {
        EndQuickEditPaletteDrag();
    };
    quickEditPaletteResizeGrip.Width = 14.0;
    quickEditPaletteResizeGrip.Height = 14.0;
    quickEditPaletteResizeGrip.HorizontalAlignment = HorizontalAlignment.Right;
    quickEditPaletteResizeGrip.VerticalAlignment = VerticalAlignment.Bottom;
    quickEditPaletteResizeGrip.Margin = new Thickness(0.0, 0.0, 1.0, 1.0);
    quickEditPaletteResizeGrip.Background = new SolidColorBrush(Color.Parse("#664B565B"));
    quickEditPaletteResizeGrip.Cursor = new Cursor(StandardCursorType.SizeAll);
    ToolTip.SetTip(quickEditPaletteResizeGrip, "拖动调整快捷编辑浮窗尺寸");
    quickEditPaletteResizeGrip.PointerPressed += QuickEditPaletteResizeGrip_PointerPressed;
    quickEditPaletteResizeGrip.PointerMoved += QuickEditPaletteResizeGrip_PointerMoved;
    quickEditPaletteResizeGrip.PointerReleased += QuickEditPaletteResizeGrip_PointerReleased;
    quickEditPaletteResizeGrip.PointerCaptureLost += (object? _, PointerCaptureLostEventArgs _) =>
    {
        EndQuickEditPaletteResize();
    };
    quickEditPalette.Background = new SolidColorBrush(Color.Parse("#F51A1D20"));
    quickEditPalette.BorderBrush = new SolidColorBrush(Color.Parse("#3B4246"));
    quickEditPalette.BorderThickness = new Thickness(1.0);
    quickEditPalette.CornerRadius = new CornerRadius(4.0);
    quickEditPalette.Padding = new Thickness(5.0);
    quickEditPalette.Margin = new Thickness(0.0, 54.0, 18.0, 0.0);
    quickEditPalette.HorizontalAlignment = HorizontalAlignment.Right;
    quickEditPalette.VerticalAlignment = VerticalAlignment.Top;
    quickEditPalette.ZIndex = 33;
    quickEditPalette.RenderTransform = quickEditPaletteTransform;
    quickEditPaletteContent.Children.Add(border2);
    quickEditPaletteContent.Children.Add(quickEditPaletteBody);
    quickEditPaletteScaleHost.Stretch = Stretch.Uniform;
    quickEditPaletteScaleHost.Child = quickEditPaletteContent;
    Grid grid = new Grid();
    grid.Children.Add(quickEditPaletteScaleHost);
    grid.Children.Add(quickEditPaletteResizeGrip);
    quickEditPalette.Child = grid;
    AutomationProperties.SetAutomationId(quickEditPalette, "Editor.QuickPalette");
    return quickEditPalette;
}

private static Button QuickPaletteButton(string text, string geometry, EventHandler<RoutedEventArgs> handler, out TextBlock label, bool filledIcon = false)
{
    global::Avalonia.Controls.Shapes.Path icon;
    return QuickPaletteButton(text, geometry, handler, out label, out icon, filledIcon);
}

private static Button QuickPaletteButton(string text, string geometry, EventHandler<RoutedEventArgs> handler, out TextBlock label, out global::Avalonia.Controls.Shapes.Path icon, bool filledIcon = false)
{
    SolidColorBrush solidColorBrush = new SolidColorBrush(Color.Parse("#B7C0C4"));
    icon = new global::Avalonia.Controls.Shapes.Path
    {
        Data = StreamGeometry.Parse(geometry),
        Stroke = (filledIcon ? null : solidColorBrush),
        StrokeThickness = 1.5,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
        Fill = (filledIcon ? solidColorBrush : null)
    };
    Canvas child = new Canvas
    {
        Width = 17.0,
        Height = 17.0,
        Children =
        {
            (Control)icon
        }
    };
    Viewbox item = new Viewbox
    {
        Width = 18.0,
        Height = 18.0,
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Child = child
    };
    label = new TextBlock
    {
        Text = text,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center
    };
    Grid grid = new Grid
    {
        Width = double.NaN,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        ColumnDefinitions = new ColumnDefinitions("18,*"),
        ColumnSpacing = 4.0
    };
    grid.Children.Add(item);
    Grid.SetColumn(label, 1);
    grid.Children.Add(label);
    Button button = new Button();
    button.Height = 35.0;
    button.Padding = new Thickness(4.0, 2.0);
    button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
    button.VerticalContentAlignment = VerticalAlignment.Center;
    button.Background = Brushes.Transparent;
    button.Foreground = Brushes.White;
    button.BorderBrush = Brushes.Transparent;
    button.BorderThickness = new Thickness(1.0);
    button.CornerRadius = new CornerRadius(3.0);
    button.Content = grid;
    button.Click += handler;
    return button;
}

private void RegisterQuickShortcutButton(string action, string description, Button button)
{
    quickShortcutButtons[action] = (button, description);
}

private void SetQuickShortcutEnabled(string action, bool enabled)
{
    if (quickShortcutButtons.TryGetValue(action, out (Button, string) value))
    {
        value.Item1.IsEnabled = enabled;
    }
}

private void QuickEditPaletteHeader_PointerPressed(object? sender, PointerPressedEventArgs eventArgs)
{
    if (sender is Control control && !quickEditPaletteCollapseButton.IsPointerOver && eventArgs.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
    {
        Point? point = quickEditPalette.TranslatePoint(new Point(0.0, 0.0), editorWorkbench);
        if (point.HasValue)
        {
            quickEditPaletteDragging = true;
            quickEditPaletteDragStart = eventArgs.GetPosition(editorWorkbench);
            quickEditPaletteDragStartX = quickEditPaletteTransform.X;
            quickEditPaletteDragStartY = quickEditPaletteTransform.Y;
            quickEditPaletteBaseLeft = point.Value.X - quickEditPaletteTransform.X;
            quickEditPaletteBaseTop = point.Value.Y - quickEditPaletteTransform.Y;
            eventArgs.Pointer.Capture(control);
            eventArgs.Handled = true;
        }
    }
}

private void QuickEditPaletteHeader_PointerMoved(object? sender, PointerEventArgs eventArgs)
{
    if (quickEditPaletteDragging && sender is Control relativeTo && eventArgs.GetCurrentPoint(relativeTo).Properties.IsLeftButtonPressed)
    {
        Point position = eventArgs.GetPosition(editorWorkbench);
        double value = quickEditPaletteDragStartX + position.X - quickEditPaletteDragStart.X;
        double value2 = quickEditPaletteDragStartY + position.Y - quickEditPaletteDragStart.Y;
        quickEditPaletteTransform.X = Math.Clamp(value, 4.0 - quickEditPaletteBaseLeft, Math.Max(4.0 - quickEditPaletteBaseLeft, editorWorkbench.Bounds.Width - quickEditPalette.Bounds.Width - quickEditPaletteBaseLeft - 4.0));
        quickEditPaletteTransform.Y = Math.Clamp(value2, 4.0 - quickEditPaletteBaseTop, Math.Max(4.0 - quickEditPaletteBaseTop, editorWorkbench.Bounds.Height - quickEditPalette.Bounds.Height - quickEditPaletteBaseTop - 4.0));
        eventArgs.Handled = true;
    }
}

private void QuickEditPaletteHeader_PointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
{
    if (quickEditPaletteDragging)
    {
        EndQuickEditPaletteDrag();
        eventArgs.Pointer.Capture(null);
        eventArgs.Handled = true;
    }
}

private void EndQuickEditPaletteDrag()
{
    if (quickEditPaletteDragging)
    {
        quickEditPaletteDragging = false;
        editorViewSettings = editorViewSettings.WithQuickPalette(quickEditPaletteTransform.X, quickEditPaletteTransform.Y, editorViewSettings.QuickPaletteCollapsed);
        editorViewSettingsDirty = true;
        ScheduleEditorViewSettingsSave();
    }
}

private void QuickEditPaletteResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs eventArgs)
{
    if (sender is Control control && eventArgs.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
    {
        quickEditPaletteResizing = true;
        quickEditPaletteResizeStart = eventArgs.GetPosition(editorWorkbench);
        quickEditPaletteResizeStartTransformX = quickEditPaletteTransform.X;
        quickEditPaletteResizeStartWidth = Math.Max(180.0, (quickEditPaletteScaleHost.Bounds.Width > 0.0) ? quickEditPaletteScaleHost.Bounds.Width : quickEditPaletteWidth);
        quickEditPaletteResizeStartHeight = Math.Max(60.0, (quickEditPaletteScaleHost.Bounds.Height > 0.0) ? quickEditPaletteScaleHost.Bounds.Height : (quickEditPaletteHeight ?? 60.0));
        eventArgs.Pointer.Capture(control);
        eventArgs.Handled = true;
    }
}

private void QuickEditPaletteResizeGrip_PointerMoved(object? sender, PointerEventArgs eventArgs)
{
    if (quickEditPaletteResizing && sender is Control relativeTo && eventArgs.GetCurrentPoint(relativeTo).Properties.IsLeftButtonPressed)
    {
        Point position = eventArgs.GetPosition(editorWorkbench);
        WindowsQuickEditPaletteSize windowsQuickEditPaletteSize = WindowsQuickEditPaletteResizePolicy.Resolve(quickEditPaletteResizeStartWidth, quickEditPaletteResizeStartHeight, position.X - quickEditPaletteResizeStart.X, position.Y - quickEditPaletteResizeStart.Y, editorWorkbench.Bounds.Width, editorWorkbench.Bounds.Height);
        quickEditPaletteWidth = windowsQuickEditPaletteSize.Width;
        quickEditPaletteHeight = windowsQuickEditPaletteSize.Height;
        quickEditPaletteTransform.X = quickEditPaletteResizeStartTransformX + windowsQuickEditPaletteSize.Width - quickEditPaletteResizeStartWidth;
        quickEditPaletteScaleHost.Width = quickEditPaletteWidth;
        quickEditPaletteScaleHost.Height = quickEditPaletteHeight.Value;
        eventArgs.Handled = true;
    }
}

private void QuickEditPaletteResizeGrip_PointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
{
    if (quickEditPaletteResizing)
    {
        EndQuickEditPaletteResize();
        eventArgs.Pointer.Capture(null);
        eventArgs.Handled = true;
    }
}

private void EndQuickEditPaletteResize()
{
    if (quickEditPaletteResizing)
    {
        quickEditPaletteResizing = false;
        ClampFloatingControl(quickEditPalette, quickEditPaletteTransform);
        editorViewSettings = editorViewSettings.WithQuickPalette(quickEditPaletteTransform.X, quickEditPaletteTransform.Y, editorViewSettings.QuickPaletteCollapsed);
        MarkEditorViewSettingsDirty();
    }
}

private void ApplyQuickEditPaletteLayout()
{
    quickEditPaletteTransform.X = editorViewSettings.QuickPaletteX;
    quickEditPaletteTransform.Y = editorViewSettings.QuickPaletteY;
    quickEditPaletteBody.IsVisible = !editorViewSettings.QuickPaletteCollapsed;
    quickEditPaletteContent.Width = (editorViewSettings.QuickPaletteCollapsed ? 124.0 : 224.0);
    quickEditPaletteContent.Height = double.NaN;
    quickEditPaletteScaleHost.Width = (editorViewSettings.QuickPaletteCollapsed ? 124.0 : quickEditPaletteWidth);
    quickEditPaletteScaleHost.Height = (editorViewSettings.QuickPaletteCollapsed ? double.NaN : (quickEditPaletteHeight ?? double.NaN));
    quickEditPaletteResizeGrip.IsVisible = !editorViewSettings.QuickPaletteCollapsed;
    quickEditPaletteCollapseButton.Content = (editorViewSettings.QuickPaletteCollapsed ? "+" : "−");
    ToolTip.SetTip(quickEditPaletteCollapseButton, editorViewSettings.QuickPaletteCollapsed ? "展开快捷编辑" : "收起快捷编辑");
}

private Control BuildQuickValuesPalette()
{
    ConfigureQuickValueText(quickSnapText);
    ConfigureQuickValueText(quickGridScaleText);
    ConfigureQuickValueText(quickPlaybackRateText);
    quickValuesPaletteBody.Children.Add(BuildAdjustmentSliders());
    quickValuesPaletteBody.Margin = new Thickness(0.0, 4.0, 0.0, 0.0);
    quickValuesPaletteBody.Width = 290.0;
    quickValuesPaletteBody.HorizontalAlignment = HorizontalAlignment.Stretch;
    quickValuesPaletteCollapseButton.Content = "−";
    quickValuesPaletteCollapseButton.Width = 23.0;
    quickValuesPaletteCollapseButton.Height = 20.0;
    quickValuesPaletteCollapseButton.Padding = new Thickness(0.0);
    quickValuesPaletteCollapseButton.Background = PanelBrush;
    quickValuesPaletteCollapseButton.Foreground = Brushes.White;
    quickValuesPaletteCollapseButton.BorderBrush = StrokeBrush;
    quickValuesPaletteCollapseButton.BorderThickness = new Thickness(1.0);
    quickValuesPaletteCollapseButton.CornerRadius = new CornerRadius(3.0);
    AutomationProperties.SetAutomationId(quickValuesPaletteCollapseButton, "Editor.QuickValues.Toggle");
    quickValuesPaletteCollapseButton.Click += (object? _, RoutedEventArgs _) =>
    {
        editorViewSettings = editorViewSettings.WithQuickValuesPalette(quickValuesPaletteTransform.X, quickValuesPaletteTransform.Y, !editorViewSettings.QuickValuesPaletteCollapsed);
        ApplyQuickValuesPaletteLayout();
        MarkEditorViewSettingsDirty();
    };
    Border border = new Border
    {
        Width = 20.0,
        Height = 2.0,
        Margin = new Thickness(0.0, 0.0, 5.0, 0.0),
        Background = new SolidColorBrush(Color.Parse("#566166")),
        CornerRadius = new CornerRadius(1.0),
        VerticalAlignment = VerticalAlignment.Center
    };
    Border border2 = new Border
    {
        Height = 28.0,
        Cursor = new Cursor(StandardCursorType.SizeAll),
        Background = new SolidColorBrush(Color.Parse("#ED1A1E20")),
        BorderBrush = new SolidColorBrush(Color.Parse("#31383B")),
        BorderThickness = new Thickness(0.0, 0.0, 0.0, 1.0),
        CornerRadius = new CornerRadius(3.0, 3.0, 0.0, 0.0),
        Child = new Grid
        {
            Margin = new Thickness(8.0, 0.0, 3.0, 0.0),
            ColumnDefinitions = new ColumnDefinitions("*,Auto,25"),
            Children =
            {
                (Control)new TextBlock
                {
                    Text = "调整",
                    Foreground = new SolidColorBrush(Color.Parse("#AAB4B7")),
                    FontSize = 10.0,
                    FontWeight = FontWeight.DemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                },
                (Control)border,
                (Control)quickValuesPaletteCollapseButton
            }
        }
    };
    Grid.SetColumn(border, 1);
    Grid.SetColumn(quickValuesPaletteCollapseButton, 2);
    ToolTip.SetTip(border2, "按住拖动调整浮窗");
    border2.PointerPressed += QuickValuesPaletteHeader_PointerPressed;
    border2.PointerMoved += QuickValuesPaletteHeader_PointerMoved;
    border2.PointerReleased += QuickValuesPaletteHeader_PointerReleased;
    border2.PointerCaptureLost += (object? _, PointerCaptureLostEventArgs _) =>
    {
        EndQuickValuesPaletteDrag();
    };
    StackPanel stackPanel = new StackPanel
    {
        Width = 290.0,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    stackPanel.Children.Add(border2);
    stackPanel.Children.Add(quickValuesPaletteBody);
    quickValuesPalette.Width = 310.0;
    quickValuesPalette.Background = new SolidColorBrush(Color.Parse("#1A1D20"));
    quickValuesPalette.BorderBrush = new SolidColorBrush(Color.Parse("#293135"));
    quickValuesPalette.BorderThickness = new Thickness(0.7);
    quickValuesPalette.CornerRadius = new CornerRadius(4.0);
    quickValuesPalette.Padding = new Thickness(5.0);
    quickValuesPalette.Margin = new Thickness(0.0, 0.0, 12.0, 36.0);
    quickValuesPalette.HorizontalAlignment = HorizontalAlignment.Right;
    quickValuesPalette.VerticalAlignment = VerticalAlignment.Bottom;
    quickValuesPalette.ZIndex = 31;
    quickValuesPalette.RenderTransform = quickValuesPaletteTransform;
    quickValuesPalette.Child = stackPanel;
    AutomationProperties.SetAutomationId(quickValuesPalette, "Editor.QuickValuesPalette");
    RefreshQuickValuesPaletteValues();
    return quickValuesPalette;
}

private Control BuildQuickValueSection(string heading, TextBlock valueText, EventHandler<RoutedEventArgs> increase, EventHandler<RoutedEventArgs> decrease, Action<Button> showPresets, string kind)
{
    Button valueButton = QuickValueButton(string.Empty, (object? _, RoutedEventArgs _) =>
    {
    });
    valueButton.Content = valueText;
    valueButton.Click += (object? _, RoutedEventArgs _) =>
    {
        showPresets(valueButton);
    };
    AutomationProperties.SetAutomationId(valueButton, "Editor.QuickValues." + kind + ".Preset");
    ToolTip.SetTip(valueButton, "左键选择预设 · 右键自定义");
    TextBox editor = new TextBox
    {
        Tag = kind,
        IsVisible = false,
        Height = 26.0,
        MinHeight = 26.0,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 10.0,
        Padding = new Thickness(2.0, 0.0),
        Margin = new Thickness(0.0),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center
    };
    AutomationProperties.SetAutomationId(editor, "Editor.QuickValues." + kind + ".Editor");
    editor.LostFocus += (object? _, FocusChangedEventArgs _) =>
    {
        if (editor.IsVisible)
        {
            CommitQuickValueEdit(valueButton, editor);
        }
    };
    editor.KeyDown += (object? _, KeyEventArgs eventArgs) =>
    {
        if (eventArgs.Key == Key.Return)
        {
            CommitQuickValueEdit(valueButton, editor);
            timeline.Focus();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Escape)
        {
            EndQuickValueEdit(valueButton, editor);
            timeline.Focus();
            eventArgs.Handled = true;
        }
    };
    editor.ContextRequested += (object? _, ContextRequestedEventArgs eventArgs) =>
    {
        editor.Focus();
        editor.SelectAll();
        eventArgs.Handled = true;
    };
    editor.AddHandler(InputElement.PointerPressedEvent, (object? _, PointerPressedEventArgs eventArgs) =>
    {
        if (eventArgs.GetCurrentPoint(editor).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            editor.Focus();
            editor.SelectAll();
            eventArgs.Handled = true;
        }
    }, RoutingStrategies.Tunnel, handledEventsToo: true);
    editor.AddHandler(InputElement.PointerReleasedEvent, (object? _, PointerReleasedEventArgs eventArgs) =>
    {
        if (eventArgs.GetCurrentPoint(editor).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
        {
            editor.Focus();
            editor.SelectAll();
            eventArgs.Handled = true;
        }
    }, RoutingStrategies.Tunnel, handledEventsToo: true);
    valueButton.AddHandler(InputElement.PointerPressedEvent, (object? _, PointerPressedEventArgs eventArgs) =>
    {
        if (eventArgs.GetCurrentPoint(valueButton).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            eventArgs.Handled = true;
        }
    }, RoutingStrategies.Tunnel, handledEventsToo: true);
    valueButton.AddHandler(InputElement.PointerReleasedEvent, (object? _, PointerReleasedEventArgs eventArgs) =>
    {
        if (eventArgs.GetCurrentPoint(valueButton).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
        {
            BeginQuickValueEdit(valueButton, editor, kind);
            eventArgs.Handled = true;
        }
    }, RoutingStrategies.Tunnel, handledEventsToo: true);
    Grid item = new Grid
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Children =
        {
            (Control)valueButton,
            (Control)editor
        }
    };
    Button button = QuickValueButton("▲", increase, new Thickness(0.0, 3.0, 0.0, 0.0));
    Button button2 = QuickValueButton("▼", decrease);
    button.Click += (object? _, RoutedEventArgs _) =>
    {
        timeline.Focus();
    };
    button2.Click += (object? _, RoutedEventArgs _) =>
    {
        timeline.Focus();
    };
    AutomationProperties.SetAutomationId(button, "Editor.QuickValues." + kind + ".Up");
    AutomationProperties.SetAutomationId(button2, "Editor.QuickValues." + kind + ".Down");
    return new StackPanel
    {
        Width = 100.0,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Margin = ((kind == "Rate") ? new Thickness(0.0) : new Thickness(0.0, 0.0, 0.0, 3.0)),
        Children =
        {
            (Control)new TextBlock
            {
                Text = heading,
                Foreground = new SolidColorBrush(Color.Parse("#7F898D")),
                FontSize = 9.0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextAlignment = TextAlignment.Center
            },
            (Control)button,
            (Control)item,
            (Control)button2
        }
    };
}

private static Button QuickValueButton(string content, EventHandler<RoutedEventArgs> handler, Thickness? margin = null)
{
    Button button = new Button();
    button.Content = content;
    button.Height = 26.0;
    button.MinHeight = 26.0;
    button.Padding = new Thickness(0.0);
    button.Margin = margin ?? new Thickness(0.0);
    button.HorizontalAlignment = HorizontalAlignment.Stretch;
    button.HorizontalContentAlignment = HorizontalAlignment.Center;
    button.VerticalContentAlignment = VerticalAlignment.Center;
    button.Background = Brushes.Transparent;
    button.Foreground = Brushes.White;
    button.BorderBrush = Brushes.Transparent;
    button.BorderThickness = new Thickness(1.0);
    button.CornerRadius = new CornerRadius(3.0);
    button.Click += handler;
    return button;
}

private static void ConfigureQuickValueText(TextBlock text)
{
    text.FontFamily = new FontFamily("Cascadia Mono, Consolas");
    text.FontSize = 10.0;
    text.HorizontalAlignment = HorizontalAlignment.Stretch;
    text.VerticalAlignment = VerticalAlignment.Center;
    text.TextAlignment = TextAlignment.Center;
}

private void ShowQuickPresetMenu(Button anchor, IEnumerable<(string Label, Action Apply)> entries)
{
    MenuItem[] itemsSource = entries.Select(((string Label, Action Apply) entry) =>
    {
        MenuItem menuItem = new MenuItem();
        menuItem.Header = entry.Label;
        menuItem.Padding = new Thickness(14.0, 6.0);
        menuItem.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        menuItem.Background = RaisedBrush;
        menuItem.Foreground = Brushes.White;
        menuItem.Click += (object? _, RoutedEventArgs _) =>
        {
            entry.Apply();
        };
        return menuItem;
    }).ToArray();
    ContextMenu contextMenu = new ContextMenu
    {
        ItemsSource = itemsSource,
        Background = RaisedBrush,
        Foreground = Brushes.White,
        BorderBrush = StrokeBrush,
        BorderThickness = new Thickness(1.0),
        Padding = new Thickness(3.0)
    };
    popupScaleController.Attach(contextMenu);
    anchor.ContextMenu = contextMenu;
    contextMenu.Open(anchor);
}

private void BeginQuickValueEdit(Button button, TextBox editor, string kind)
{
    string text;
    if (kind == "Snap")
    {
        text = $"1/{viewModel.ActiveTab?.Session.SnapDivision ?? editorViewSettings.SnapDivision}";
    }
    else
    {
        text = ((!(kind == "Grid")) ? audioTransport.PlaybackRate.ToString("0.00", CultureInfo.InvariantCulture) : (timeline.PixelsPerBeat / 140.0).ToString("0.00", CultureInfo.InvariantCulture));
    }

    editor.Text = text;
    button.IsVisible = false;
    editor.IsVisible = true;
    editor.Focus();
    editor.SelectAll();
}

private void CommitQuickValueEdit(Button button, TextBox editor)
{
    string text = (editor.Text ?? string.Empty).Trim().TrimEnd('x', 'X');
    bool flag = false;
    double value;
    if (object.Equals(editor.Tag, "Snap"))
    {
        if (text.StartsWith("1/", StringComparison.Ordinal))
        {
            text = text.Substring(2);
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result >= 1 && result <= 1920)
        {
            SetQuickSnapDivision(result);
            flag = true;
        }
    }
    else if (TryTimingNumber(text, out value))
    {
        if (object.Equals(editor.Tag, "Grid") && value >= 3.0 / 35.0 && value <= 32.0)
        {
            SetQuickGridScale(value);
            flag = true;
        }
        else if (object.Equals(editor.Tag, "Rate") && value >= 0.1 && value <= 2.0)
        {
            SetQuickPlaybackRateAsync(value);
            flag = true;
        }
    }

    if (!flag)
    {
        MainWindowViewModel mainWindowViewModel = viewModel;
        string status;
        if (object.Equals(editor.Tag, "Snap"))
        {
            status = "分度范围：1–1920";
        }
        else
        {
            status = (object.Equals(editor.Tag, "Grid") ? "缩放范围：0.09–27.43" : "倍速范围：0.1–2.0");
        }

        mainWindowViewModel.SetStatus(status);
    }

    EndQuickValueEdit(button, editor);
}

private static void EndQuickValueEdit(Button button, TextBox editor)
{
    editor.IsVisible = false;
    button.IsVisible = true;
}

private void AdjustQuickSnap(int direction)
{
    int current = viewModel.ActiveTab?.Session.SnapDivision ?? editorViewSettings.SnapDivision;
    int num = Array.IndexOf(WheelSnapDivisions, current);
    int division;
    if (num >= 0)
    {
        division = WheelSnapDivisions[Math.Clamp(num + direction, 0, WheelSnapDivisions.Length - 1)];
    }
    else
    {
        division = ((direction > 0) ? WheelSnapDivisions.FirstOrDefault((int value) => value > current, current) : WheelSnapDivisions.LastOrDefault((int value) => value < current, current));
    }

    SetQuickSnapDivision(division);
}

private void SetQuickSnapDivision(int division, bool addCustomDivisionItem = true)
{
    division = Math.Clamp(division, 1, 1920);
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab != null)
    {
        activeTab.Session.SnapDivision = division;
    }

    divisionPicker.SelectedItem = division;
    editorViewSettings = editorViewSettings.WithSnapDivision(division);
    SyncSettingsSnapBox(division, addCustomDivisionItem);
    SyncFixedGridPicker(division);
    RefreshQuickValuesPaletteValues();
    timeline.InvalidateVisual();
    SyncTrackViewports();
    viewModel.SetStatus("当前网格 " + GridLabel(division));
    MarkEditorViewSettingsDirty();
}

private void AdjustQuickGrid(int direction)
{
    double num = Math.Round(timeline.PixelsPerBeat / 140.0 * 10.0) / 10.0;
    SetQuickGridScale(Math.Clamp(num + (double)direction * 0.1, 3.0 / 35.0, 32.0));
}

private void SetQuickGridScale(double scale)
{
    timeline.SetZoom(Math.Round(140.0 * scale));
    RefreshQuickValuesPaletteValues();
}

private Task AdjustQuickPlaybackRateAsync(double delta)
{
    return SetQuickPlaybackRateAsync(audioTransport.PlaybackRate + delta);
}

private async Task SetQuickPlaybackRateAsync(double rate)
{
    rate = Math.Round(Math.Clamp(rate, 0.1, 2.0), 2);
    await SetEditorPlaybackRateAsync(rate);
    RefreshQuickValuesPaletteValues();
    viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("音频与谱面倍速：{0:0.00}x", rate));
}

private void RefreshQuickValuesPaletteValues()
{
    SyncFixedGridPicker(viewModel.ActiveTab?.Session.SnapDivision ?? editorViewSettings.SnapDivision);
    int division = viewModel.ActiveTab?.Session.SnapDivision ?? editorViewSettings.SnapDivision;
    quickSnapText.Text = GridLabel(division);
    quickGridScaleText.Text = $"{timeline.PixelsPerBeat / 140.0:0.00}x";
    quickPlaybackRateText.Text = $"{audioTransport.PlaybackRate:0.00}x";
    SyncFixedPlaybackRate();
    SyncAdjustmentZoom();
}

private void ApplyQuickValuesPaletteLayout()
{
    quickValuesPaletteTransform.X = editorViewSettings.QuickValuesPaletteX;
    quickValuesPaletteTransform.Y = editorViewSettings.QuickValuesPaletteY;
    quickValuesPaletteBody.IsVisible = !editorViewSettings.QuickValuesPaletteCollapsed;
    quickValuesPaletteCollapseButton.Content = (editorViewSettings.QuickValuesPaletteCollapsed ? "+" : "−");
    ToolTip.SetTip(quickValuesPaletteCollapseButton, editorViewSettings.QuickValuesPaletteCollapsed ? "展开调整" : "收起调整");
}

private void QueueFactoryFloatingLayout()
{
    if (!factoryFloatingLayoutPending || factoryFloatingLayoutQueued)
    {
        return;
    }

    factoryFloatingLayoutQueued = true;
    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        factoryFloatingLayoutQueued = false;
        if (ApplyFactoryFloatingLayout())
        {
            factoryFloatingLayoutPending = false;
        }
    }, DispatcherPriority.Loaded);
}

private void QueueFloatingPaletteClamp()
{
    if (floatingPaletteClampQueued)
    {
        return;
    }

    floatingPaletteClampQueued = true;
    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        floatingPaletteClampQueued = false;
        if (editorView.IsVisible && !(editorWorkbench.Bounds.Width <= 0.0) && !(editorWorkbench.Bounds.Height <= 0.0))
        {
            if (factoryFloatingLayoutPending)
            {
                QueueFactoryFloatingLayout();
            }
            else
            {
                ClampFloatingControl(quickEditPalette, quickEditPaletteTransform);
                ClampFloatingControl(quickValuesPalette, quickValuesPaletteTransform);
                ClampRoomCollaborationPosition();
                SeparateFloatingPalettesIfNeeded();
                editorViewSettings = editorViewSettings.WithQuickPalette(quickEditPaletteTransform.X, quickEditPaletteTransform.Y, editorViewSettings.QuickPaletteCollapsed).WithQuickValuesPalette(quickValuesPaletteTransform.X, quickValuesPaletteTransform.Y, editorViewSettings.QuickValuesPaletteCollapsed);
                MarkEditorViewSettingsDirty();
            }
        }
    }, DispatcherPriority.Loaded);
}

private void SeparateFloatingPalettesIfNeeded()
{
    Rect floatingControlRect = GetFloatingControlRect(quickEditPalette);
    Rect floatingControlRect2 = GetFloatingControlRect(quickValuesPalette);
    if (!(floatingControlRect.Width <= 0.0) && !(floatingControlRect.Height <= 0.0) && !(floatingControlRect2.Width <= 0.0) && !(floatingControlRect2.Height <= 0.0) && !(floatingControlRect.Right <= floatingControlRect2.Left) && !(floatingControlRect2.Right <= floatingControlRect.Left) && !(floatingControlRect.Bottom <= floatingControlRect2.Top) && !(floatingControlRect2.Bottom <= floatingControlRect.Top))
    {
        WindowsFloatingPalettePlacement windowsFloatingPalettePlacement = WindowsFloatingPaletteLayoutPolicy.Resolve(editorWorkbench.Bounds.Width, editorWorkbench.Bounds.Height, Math.Min(editorWorkbench.Bounds.Width - 4.0, Math.Max(floatingControlRect.Right, floatingControlRect2.Right)), floatingControlRect.Width, floatingControlRect.Height, floatingControlRect2.Width, floatingControlRect2.Height, Math.Min(floatingControlRect.Top, 54.0), 8.0);
        MoveFloatingControlTo(quickEditPalette, quickEditPaletteTransform, windowsFloatingPalettePlacement.QuickLeft, windowsFloatingPalettePlacement.QuickTop);
        MoveFloatingControlTo(quickValuesPalette, quickValuesPaletteTransform, windowsFloatingPalettePlacement.ValuesLeft, windowsFloatingPalettePlacement.ValuesTop);
        ClampFloatingControl(quickEditPalette, quickEditPaletteTransform);
        ClampFloatingControl(quickValuesPalette, quickValuesPaletteTransform);
    }
}

private bool ApplyFactoryFloatingLayout()
{
    if (applyingFactoryFloatingLayout || viewModel.ActiveTab == null || editorWorkbench.Bounds.Width <= 0.0 || editorWorkbench.Bounds.Height <= 0.0 || timeline.Bounds.Width <= 0.0 || timeline.Bounds.Height <= 0.0)
    {
        return false;
    }

    Rect activeTrackBounds = timeline.GetActiveTrackBounds();
    Point? point = timeline.TranslatePoint(new Point(activeTrackBounds.Left, activeTrackBounds.Top), editorWorkbench);
    Point? point2 = timeline.TranslatePoint(new Point(activeTrackBounds.Right, activeTrackBounds.Bottom), editorWorkbench);
    if (!(activeTrackBounds.Width <= 0.0) && point.HasValue)
    {
        Point valueOrDefault = point.GetValueOrDefault();
        if (point2.HasValue)
        {
            Point valueOrDefault2 = point2.GetValueOrDefault();
            if (!(valueOrDefault2.X <= valueOrDefault.X))
            {
                applyingFactoryFloatingLayout = true;
                try
                {
                    Rect rect = new Rect(valueOrDefault.X, valueOrDefault.Y, valueOrDefault2.X - valueOrDefault.X, Math.Max(1.0, valueOrDefault2.Y - valueOrDefault.Y));
                    double num = Math.Max(4.0, editorWorkbench.Bounds.Width - 4.0);
                    Rect floatingControlRect = GetFloatingControlRect(inspectorPanel);
                    if (inspectorPanel.IsVisible && floatingControlRect.Width > 0.0 && floatingControlRect.Left > rect.Right + 40.0)
                    {
                        num = Math.Min(num, floatingControlRect.Left - 6.0);
                    }

                    Rect floatingControlRect2 = GetFloatingControlRect(quickEditPalette);
                    if (floatingControlRect2.Width <= 0.0 || floatingControlRect2.Height <= 0.0)
                    {
                        return false;
                    }

                    double quickWidth = Math.Max(floatingControlRect2.Width, editorViewSettings.QuickPaletteCollapsed ? 134.0 : 234.0);
                    Rect floatingControlRect3 = GetFloatingControlRect(quickValuesPalette);
                    if (floatingControlRect3.Width <= 0.0 || floatingControlRect3.Height <= 0.0)
                    {
                        return false;
                    }

                    WindowsFloatingPalettePlacement windowsFloatingPalettePlacement = WindowsFloatingPaletteLayoutPolicy.Resolve(editorWorkbench.Bounds.Width, editorWorkbench.Bounds.Height, num, quickWidth, floatingControlRect2.Height, floatingControlRect3.Width, floatingControlRect3.Height, floatingControlRect2.Top, 8.0);
                    MoveFloatingControlTo(quickEditPalette, quickEditPaletteTransform, windowsFloatingPalettePlacement.QuickLeft, windowsFloatingPalettePlacement.QuickTop);
                    ClampFloatingControl(quickEditPalette, quickEditPaletteTransform);
                    if (GetFloatingControlRect(quickEditPalette).Width <= 0.0 || floatingControlRect3.Width <= 0.0)
                    {
                        return false;
                    }

                    MoveFloatingControlTo(quickValuesPalette, quickValuesPaletteTransform, windowsFloatingPalettePlacement.ValuesLeft, windowsFloatingPalettePlacement.ValuesTop);
                    ClampFloatingControl(quickValuesPalette, quickValuesPaletteTransform);
                    editorViewSettings = editorViewSettings.WithQuickPalette(quickEditPaletteTransform.X, quickEditPaletteTransform.Y, editorViewSettings.QuickPaletteCollapsed).WithQuickValuesPalette(quickValuesPaletteTransform.X, quickValuesPaletteTransform.Y, editorViewSettings.QuickValuesPaletteCollapsed);
                    MarkEditorViewSettingsDirty();
                    return true;
                }
                finally
                {
                    applyingFactoryFloatingLayout = false;
                }
            }
        }
    }

    return false;
}

private Rect GetFloatingControlRect(Control control)
{
    if (control.IsVisible && !(control.Bounds.Width <= 0.0) && !(control.Bounds.Height <= 0.0))
    {
        Point? point = control.TranslatePoint(new Point(0.0, 0.0), editorWorkbench);
        if (point.HasValue)
        {
            Point valueOrDefault = point.GetValueOrDefault();
            return new Rect(valueOrDefault.X, valueOrDefault.Y, control.Bounds.Width, control.Bounds.Height);
        }
    }

    return default;
}

private void MoveFloatingControlTo(Control control, TranslateTransform transform, double desiredLeft, double desiredTop)
{
    Point? point = control.TranslatePoint(new Point(0.0, 0.0), editorWorkbench);
    if (point.HasValue)
    {
        Point valueOrDefault = point.GetValueOrDefault();
        double num = valueOrDefault.X - transform.X;
        double num2 = valueOrDefault.Y - transform.Y;
        transform.X = desiredLeft - num;
        transform.Y = desiredTop - num2;
    }
}

private void ClampFloatingControl(Control control, TranslateTransform transform)
{
    Point? point = control.TranslatePoint(new Point(0.0, 0.0), editorWorkbench);
    if (point.HasValue)
    {
        Point valueOrDefault = point.GetValueOrDefault();
        double num = valueOrDefault.X - transform.X;
        double num2 = valueOrDefault.Y - transform.Y;
        transform.X = Math.Clamp(transform.X, 4.0 - num, Math.Max(4.0 - num, editorWorkbench.Bounds.Width - control.Bounds.Width - num - 4.0));
        transform.Y = Math.Clamp(transform.Y, 4.0 - num2, Math.Max(4.0 - num2, editorWorkbench.Bounds.Height - control.Bounds.Height - num2 - 4.0));
    }
}

private void QuickValuesPaletteHeader_PointerPressed(object? sender, PointerPressedEventArgs eventArgs)
{
    if (sender is Control control && !quickValuesPaletteCollapseButton.IsPointerOver && eventArgs.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
    {
        Point? point = quickValuesPalette.TranslatePoint(new Point(0.0, 0.0), editorWorkbench);
        if (point.HasValue)
        {
            quickValuesPaletteDragging = true;
            quickValuesPaletteDragStart = eventArgs.GetPosition(editorWorkbench);
            quickValuesPaletteDragStartX = quickValuesPaletteTransform.X;
            quickValuesPaletteDragStartY = quickValuesPaletteTransform.Y;
            quickValuesPaletteBaseLeft = point.Value.X - quickValuesPaletteTransform.X;
            quickValuesPaletteBaseTop = point.Value.Y - quickValuesPaletteTransform.Y;
            eventArgs.Pointer.Capture(control);
            eventArgs.Handled = true;
        }
    }
}

private void QuickValuesPaletteHeader_PointerMoved(object? sender, PointerEventArgs eventArgs)
{
    if (quickValuesPaletteDragging && sender is Control relativeTo && eventArgs.GetCurrentPoint(relativeTo).Properties.IsLeftButtonPressed)
    {
        Point position = eventArgs.GetPosition(editorWorkbench);
        double value = quickValuesPaletteDragStartX + position.X - quickValuesPaletteDragStart.X;
        double value2 = quickValuesPaletteDragStartY + position.Y - quickValuesPaletteDragStart.Y;
        quickValuesPaletteTransform.X = Math.Clamp(value, 4.0 - quickValuesPaletteBaseLeft, Math.Max(4.0 - quickValuesPaletteBaseLeft, editorWorkbench.Bounds.Width - quickValuesPalette.Bounds.Width - quickValuesPaletteBaseLeft - 4.0));
        quickValuesPaletteTransform.Y = Math.Clamp(value2, 4.0 - quickValuesPaletteBaseTop, Math.Max(4.0 - quickValuesPaletteBaseTop, editorWorkbench.Bounds.Height - quickValuesPalette.Bounds.Height - quickValuesPaletteBaseTop - 4.0));
        eventArgs.Handled = true;
    }
}

private void QuickValuesPaletteHeader_PointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
{
    if (quickValuesPaletteDragging)
    {
        EndQuickValuesPaletteDrag();
        eventArgs.Pointer.Capture(null);
        eventArgs.Handled = true;
    }
}

private void EndQuickValuesPaletteDrag()
{
    if (quickValuesPaletteDragging)
    {
        quickValuesPaletteDragging = false;
        editorViewSettings = editorViewSettings.WithQuickValuesPalette(quickValuesPaletteTransform.X, quickValuesPaletteTransform.Y, editorViewSettings.QuickValuesPaletteCollapsed);
        MarkEditorViewSettingsDirty();
    }
}

private Control BuildRoomCollaborationPanel()
{
    roomCollaborationTitle.Text = "联机房间";
    roomCollaborationTitle.Foreground = new SolidColorBrush(Color.Parse("#E0E6E7"));
    roomCollaborationTitle.FontWeight = FontWeight.DemiBold;
    roomCollaborationTitle.TextTrimming = TextTrimming.CharacterEllipsis;
    AutomationProperties.SetAutomationId(roomCollaborationTitle, "RoomCollaboration.Title");
    StackPanel item = new StackPanel
    {
        Children =
        {
            (Control)roomCollaborationTitle,
            (Control)new TextBlock
            {
                Text = "成员与房间聊天",
                Foreground = new SolidColorBrush(Color.Parse("#758488")),
                FontSize = 9.0,
                Margin = new Thickness(0.0, 2.0, 0.0, 0.0)
            }
        }
    };
    roomChatUnreadDot.Width = 7.0;
    roomChatUnreadDot.Height = 7.0;
    roomChatUnreadDot.Background = new SolidColorBrush(Color.Parse("#E34D4D"));
    roomChatUnreadDot.BorderBrush = new SolidColorBrush(Color.Parse("#5B2024"));
    roomChatUnreadDot.BorderThickness = new Thickness(1.0);
    roomChatUnreadDot.CornerRadius = new CornerRadius(4.0);
    roomChatUnreadDot.Margin = new Thickness(0.0, 0.0, 6.0, 0.0);
    roomChatUnreadDot.IsVisible = false;
    AutomationProperties.SetAutomationId(roomChatUnreadDot, "RoomCollaboration.UnreadDot");
    roomMemberCountText.Text = "0 / 8";
    roomMemberCountText.Foreground = new SolidColorBrush(Color.Parse("#86A5A7"));
    roomMemberCountText.FontFamily = new FontFamily("Cascadia Mono,Consolas");
    roomMemberCountText.FontSize = 10.0;
    roomMemberCountText.VerticalAlignment = VerticalAlignment.Center;
    AutomationProperties.SetAutomationId(roomMemberCountText, "RoomCollaboration.MemberCount");
    StackPanel stackPanel = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8.0, 0.0),
        Children =
        {
            (Control)roomChatUnreadDot,
            (Control)roomMemberCountText
        }
    };
    roomCollaborationToggleButton.Content = "−";
    roomCollaborationToggleButton.Width = 25.0;
    roomCollaborationToggleButton.Height = 23.0;
    roomCollaborationToggleButton.Padding = new Thickness(0.0);
    roomCollaborationToggleButton.Background = Brushes.Transparent;
    roomCollaborationToggleButton.Foreground = new SolidColorBrush(Color.Parse("#D8DEDF"));
    roomCollaborationToggleButton.BorderBrush = new SolidColorBrush(Color.Parse("#384347"));
    roomCollaborationToggleButton.BorderThickness = new Thickness(1.0);
    roomCollaborationToggleButton.CornerRadius = new CornerRadius(3.0);
    roomCollaborationToggleButton.Click += (object? _, RoutedEventArgs _) =>
    {
        roomCollaborationCollapsed = !roomCollaborationCollapsed;
        ApplyRoomCollaborationCollapsedState();
        if (!roomCollaborationCollapsed)
        {
            SetRoomChatUnread(unread: false);
        }

        QueueFloatingPaletteClamp();
    };
    AutomationProperties.SetAutomationId(roomCollaborationToggleButton, "RoomCollaboration.Toggle");
    Grid grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto")
    };
    grid.Children.Add(item);
    Grid.SetColumn(stackPanel, 1);
    grid.Children.Add(stackPanel);
    Grid.SetColumn(roomCollaborationToggleButton, 2);
    grid.Children.Add(roomCollaborationToggleButton);
    AutomationBorder automationBorder = new AutomationBorder
    {
        Background = new SolidColorBrush(Color.Parse("#20262A")),
        CornerRadius = new CornerRadius(4.0, 4.0, 0.0, 0.0),
        Padding = new Thickness(10.0, 7.0),
        Cursor = new Cursor(StandardCursorType.SizeAll),
        Child = grid
    };
    ToolTip.SetTip(automationBorder, "拖动成员与聊天窗口");
    automationBorder.PointerPressed += RoomCollaborationHeader_PointerPressed;
    automationBorder.PointerMoved += RoomCollaborationHeader_PointerMoved;
    automationBorder.PointerReleased += RoomCollaborationHeader_PointerReleased;
    automationBorder.PointerCaptureLost += (object? _, PointerCaptureLostEventArgs _) =>
    {
        roomCollaborationDragging = false;
    };
    AutomationProperties.SetAutomationId(automationBorder, "RoomCollaboration.Header");
    roomMemberList.Background = Brushes.Transparent;
    roomMemberList.BorderThickness = new Thickness(0.0);
    roomMemberList.Padding = new Thickness(0.0);
    roomMemberList.MaxHeight = 180.0;
    roomMemberList.ItemTemplate = new FuncDataTemplate<OnlineRoomMemberDto>((OnlineRoomMemberDto member, INameScope _) => BuildRoomMemberRow(member));
    ApplyCompactPlayerListStyles(roomMemberList);
    roomMemberList.SelectionChanged += async (object? _, SelectionChangedEventArgs _) =>
    {
        if (!suppressRoomMemberSelectionChanged && roomMemberList.SelectedItem is OnlineRoomMemberDto onlineRoomMemberDto)
        {
            await ShowOnlinePlayerPopupAsync(onlineRoomMemberDto.UserId, onlineRoomMemberDto);
        }
    };
    AutomationProperties.SetAutomationId(roomMemberList, "RoomCollaboration.Members");
    roomChatList.Background = Brushes.Transparent;
    roomChatList.BorderThickness = new Thickness(0.0);
    roomChatList.Padding = new Thickness(0.0);
    roomChatList.Height = 178.0;
    roomChatList.ItemTemplate = new FuncDataTemplate<OnlineRoomChatMessageDto>((OnlineRoomChatMessageDto message, INameScope _) => BuildRoomChatRow(message));
    ApplyCompactPlayerListStyles(roomChatList, showSelection: false);
    AutomationProperties.SetAutomationId(roomChatList, "RoomCollaboration.Chat");
    roomChatStatusText.Foreground = new SolidColorBrush(Color.Parse("#C58C79"));
    roomChatStatusText.FontSize = 9.0;
    roomChatStatusText.MinHeight = 20.0;
    roomChatStatusText.TextWrapping = TextWrapping.Wrap;
    roomChatStatusText.Margin = new Thickness(2.0, 3.0, 0.0, 1.0);
    AutomationProperties.SetAutomationId(roomChatStatusText, "RoomCollaboration.ChatStatus");
    roomChatInputBox.Height = 34.0;
    roomChatInputBox.MaxLength = 500;
    roomChatInputBox.Padding = new Thickness(8.0, 5.0);
    roomChatInputBox.VerticalContentAlignment = VerticalAlignment.Center;
    roomChatInputBox.KeyDown += (object? _, KeyEventArgs eventArgs) =>
    {
        if (eventArgs.Key == Key.Return)
        {
            eventArgs.Handled = true;
            _ = SendRoomChatAsync();
        }
    };
    AutomationProperties.SetAutomationId(roomChatInputBox, "RoomCollaboration.ChatInput");
    sendRoomChatButton.Content = "发送";
    sendRoomChatButton.Width = 56.0;
    sendRoomChatButton.Margin = new Thickness(5.0, 0.0, 0.0, 0.0);
    sendRoomChatButton.HorizontalContentAlignment = HorizontalAlignment.Center;
    sendRoomChatButton.VerticalContentAlignment = VerticalAlignment.Center;
    sendRoomChatButton.Background = new SolidColorBrush(Color.Parse("#355E62"));
    sendRoomChatButton.Foreground = Brushes.White;
    sendRoomChatButton.BorderBrush = new SolidColorBrush(Color.Parse("#4E777A"));
    sendRoomChatButton.BorderThickness = new Thickness(1.0);
    sendRoomChatButton.CornerRadius = new CornerRadius(3.0);
    sendRoomChatButton.Click += async (object? _, RoutedEventArgs _) =>
    {
        await SendRoomChatAsync();
    };
    AutomationProperties.SetAutomationId(sendRoomChatButton, "RoomCollaboration.Send");
    Grid grid2 = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,56")
    };
    grid2.Children.Add(roomChatInputBox);
    Grid.SetColumn(sendRoomChatButton, 1);
    grid2.Children.Add(sendRoomChatButton);
    roomCollaborationBody.RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto");
    roomCollaborationBody.Margin = new Thickness(9.0, 7.0, 9.0, 9.0);
    roomCollaborationBody.Children.Add(RoomPanelHeading("房间成员"));
    Grid.SetRow(roomMemberList, 1);
    roomCollaborationBody.Children.Add(roomMemberList);
    Border border = new Border
    {
        Height = 1.0,
        Background = new SolidColorBrush(Color.Parse("#30383C")),
        Margin = new Thickness(0.0, 5.0, 0.0, 7.0)
    };
    Grid.SetRow(border, 2);
    roomCollaborationBody.Children.Add(border);
    TextBlock textBlock = RoomPanelHeading("房间聊天");
    Grid.SetRow(textBlock, 3);
    roomCollaborationBody.Children.Add(textBlock);
    Grid.SetRow(roomChatList, 4);
    roomCollaborationBody.Children.Add(roomChatList);
    Grid.SetRow(roomChatStatusText, 5);
    roomCollaborationBody.Children.Add(roomChatStatusText);
    Grid.SetRow(grid2, 6);
    roomCollaborationBody.Children.Add(grid2);
    StackPanel child = new StackPanel
    {
        Children =
        {
            (Control)automationBorder,
            (Control)roomCollaborationBody
        }
    };
    roomCollaborationPanel.Width = 292.0;
    roomCollaborationPanel.HorizontalAlignment = HorizontalAlignment.Right;
    roomCollaborationPanel.VerticalAlignment = VerticalAlignment.Bottom;
    roomCollaborationPanel.Margin = new Thickness(0.0, 0.0, 78.0, 14.0);
    roomCollaborationPanel.Background = new SolidColorBrush(Color.Parse("#F5181C1F"));
    roomCollaborationPanel.BorderBrush = new SolidColorBrush(Color.Parse("#3B484C"));
    roomCollaborationPanel.BorderThickness = new Thickness(1.0);
    roomCollaborationPanel.CornerRadius = new CornerRadius(5.0);
    roomCollaborationPanel.RenderTransform = roomCollaborationTransform;
    roomCollaborationPanel.ZIndex = 32;
    roomCollaborationPanel.IsVisible = false;
    roomCollaborationPanel.Child = child;
    roomCollaborationPanel.SizeChanged += (object? _, SizeChangedEventArgs _) =>
    {
        QueueFloatingPaletteClamp();
    };
    roomCollaborationPanel.PointerPressed += (object? _, PointerPressedEventArgs _) =>
    {
        if (roomCollaborationBody.IsVisible)
        {
            SetRoomChatUnread(unread: false);
        }
    };
    AutomationProperties.SetAutomationId(roomCollaborationPanel, "RoomCollaboration.Panel");
    AutomationProperties.SetName(roomCollaborationPanel, "联机房间成员与聊天");
    return roomCollaborationPanel;
}

private static TextBlock RoomPanelHeading(string text)
{
    return new TextBlock
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.Parse("#89979B")),
        FontSize = 9.0,
        Margin = new Thickness(2.0, 0.0, 0.0, 5.0)
    };
}

private static void ApplyCompactPlayerListStyles(ListBox list, bool showSelection = true, string itemBackground = "#00000000")
{
    Style style = new Style((Selector? selector) => selector.OfType<ListBoxItem>());
    style.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.Parse(itemBackground))));
    style.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, Brushes.Transparent));
    style.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0.0)));
    style.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(0.0)));
    style.Setters.Add(new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
    list.Styles.Add(style);
    Style style2 = new Style((Selector? selector) => selector.OfType<ListBoxItem>().Class(":pointerover"));
    style2.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.Parse("#22292C"))));
    list.Styles.Add(style2);
    Style style3 = new Style((Selector? selector) => selector.OfType<ListBoxItem>().Class(":selected"));
    style3.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, showSelection ? ((ISolidColorBrush)new SolidColorBrush(Color.Parse("#252D30"))) : ((ISolidColorBrush)Brushes.Transparent)));
    style3.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, showSelection ? ((ISolidColorBrush)new SolidColorBrush(Color.Parse("#6C979A"))) : ((ISolidColorBrush)Brushes.Transparent)));
    style3.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, showSelection ? new Thickness(1.0) : new Thickness(0.0)));
    list.Styles.Add(style3);
}

private Control BuildRoomMemberRow(OnlineRoomMemberDto? member)
{
    if (member == null)
    {
        return new Border
        {
            Height = 1.0
        };
    }

    WindowsDefaultUserAvatar windowsDefaultUserAvatar = new WindowsDefaultUserAvatar(25.0, new SolidColorBrush(Color.Parse("#56666A")), 1.0, $"RoomCollaboration.Member.{member.UserId}.Avatar");
    BindRemoteAvatar(windowsDefaultUserAvatar, member.AvatarKey, member.DisplayName);
    Grid item = new Grid
    {
        Width = 25.0,
        Height = 25.0,
        Children =
        {
            (Control)windowsDefaultUserAvatar,
            (Control)new Ellipse
            {
                Width = 7.0,
                Height = 7.0,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Fill = new SolidColorBrush(Color.Parse(member.IsOnline ? "#668070" : "#626B6F")),
                Stroke = new SolidColorBrush(Color.Parse("#182023")),
                StrokeThickness = 1.0,
                Opacity = (member.IsOnline ? 1.0 : 0.65)
            }
        }
    };
    Grid grid = new Grid
    {
        Width = 270.0,
        Height = 34.0,
        ColumnDefinitions = new ColumnDefinitions("30,*"),
        Background = Brushes.Transparent,
        Children =
        {
            (Control)item
        }
    };
    TextBlock textBlock = new TextBlock
    {
        Text = member.DisplayName,
        Margin = new Thickness(5.0, 0.0, 0.0, 0.0),
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Color.Parse("#D7DEDF")),
        FontSize = 11.0,
        FontWeight = FontWeight.DemiBold,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    Grid.SetColumn(textBlock, 1);
    grid.Children.Add(textBlock);
    AutomationProperties.SetName(grid, $"{member.DisplayName} · {(member.IsOnline ? "在线" : "离线")} · {member.MemberRole}");
    return grid;
}

private Control BuildRoomChatRow(OnlineRoomChatMessageDto? message)
{
    if (message == null)
    {
        return new Border
        {
            Height = 1.0
        };
    }

    WindowsDefaultUserAvatar windowsDefaultUserAvatar = new WindowsDefaultUserAvatar(17.0, new SolidColorBrush(Color.Parse("#56666A")), 1.0, $"RoomCollaboration.Message.{message.Id}.Avatar");
    BindRemoteAvatar(windowsDefaultUserAvatar, message.AvatarKey, message.DisplayName);
    Grid grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("20,*,Auto")
    };
    grid.Children.Add(windowsDefaultUserAvatar);
    TextBlock textBlock = new TextBlock
    {
        Text = message.DisplayName,
        Foreground = new SolidColorBrush(Color.Parse("#DDE4E5")),
        FontWeight = FontWeight.DemiBold,
        FontSize = 10.0,
        VerticalAlignment = VerticalAlignment.Center
    };
    Grid.SetColumn(textBlock, 1);
    grid.Children.Add(textBlock);
    TextBlock textBlock2 = new TextBlock
    {
        Text = message.CreatedAt.ToLocalTime().ToString("HH:mm"),
        Foreground = new SolidColorBrush(Color.Parse("#68767A")),
        FontFamily = new FontFamily("Cascadia Mono,Consolas"),
        FontSize = 8.0
    };
    Grid.SetColumn(textBlock2, 2);
    grid.Children.Add(textBlock2);
    return new Border
    {
        Width = 270.0,
        Background = new SolidColorBrush(Color.Parse("#1D2225")),
        CornerRadius = new CornerRadius(3.0),
        Padding = new Thickness(7.0, 6.0),
        Margin = new Thickness(0.0, 0.0, 0.0, 4.0),
        Child = new StackPanel
        {
            Children =
            {
                (Control)grid,
                (Control)new TextBlock
                {
                    Text = message.Message,
                    Foreground = new SolidColorBrush(Color.Parse("#D2D9DA")),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 10.0,
                    Margin = new Thickness(0.0, 3.0, 0.0, 0.0)
                }
            }
        }
    };
}

private void ApplyRoomCollaborationCollapsedState()
{
    roomCollaborationBody.IsVisible = !roomCollaborationCollapsed;
    roomCollaborationPanel.Width = (roomCollaborationCollapsed ? 188 : 292);
    roomCollaborationToggleButton.Content = (roomCollaborationCollapsed ? "+" : "−");
    ToolTip.SetTip(roomCollaborationToggleButton, roomCollaborationCollapsed ? "展开成员与聊天" : "收起成员与聊天");
    QueueFloatingPaletteClamp();
}

private void RefreshRoomCollaborationPanelVisibility()
{
    Guid? guid = onlineAccountHost?.Session.CurrentRoom?.Id;
    Guid? guid2 = roomChartSyncRoomId;
    bool flag = guid.HasValue == guid2.HasValue && (!guid.HasValue || guid.GetValueOrDefault() == guid2.GetValueOrDefault()) && roomChartSyncTab != null && viewModel.ActiveTab == roomChartSyncTab && editorView.IsVisible;
    roomCollaborationPanel.IsVisible = flag;
    if (flag && IsRoomChatActivelyVisible())
    {
        SetRoomChatUnread(unread: false);
    }

    if (flag)
    {
        QueueFloatingPaletteClamp();
    }
}

private bool IsRoomChatActivelyVisible()
{
    if (IsActive && roomCollaborationPanel.IsVisible)
    {
        return roomCollaborationBody.IsVisible;
    }

    return false;
}

private void SetRoomChatUnread(bool unread)
{
    roomChatHasUnread = unread;
    roomChatUnreadDot.IsVisible = unread;
    AutomationProperties.SetHelpText(roomCollaborationPanel, $"Unread={unread}; Messages={roomChatMessages.Count}; LatestMessageId={latestRoomMessageId}; Collapsed={roomCollaborationCollapsed}");
}

private void ResetRoomChatUi(bool reveal)
{
    roomChatMessages.Clear();
    latestRoomMessageId = 0L;
    roomChatInitialized = false;
    roomChatStatusText.Text = string.Empty;
    roomChatInputBox.Text = string.Empty;
    roomChatList.ItemsSource = Array.Empty<OnlineRoomChatMessageDto>();
    roomMemberList.ItemsSource = Array.Empty<OnlineRoomMemberDto>();
    roomMemberCountText.Text = "0 / 8";
    roomCollaborationTitle.Text = ((roomChartSyncRoomName.Length == 0) ? "联机房间" : roomChartSyncRoomName);
    if (reveal)
    {
        roomCollaborationCollapsed = false;
        ApplyRoomCollaborationCollapsedState();
    }

    SetRoomChatUnread(unread: false);
    RefreshRoomCollaborationPanelVisibility();
}

private void AppendRoomChatMessages(IReadOnlyList<OnlineRoomChatMessageDto> messages, long latestMessageId)
{
    long valueOrDefault = (onlineAccountHost?.Session.CurrentUser?.Id).GetValueOrDefault();
    OnlineRoomChatMessageDto[] array = (
        from message in messages
        where roomChatMessages.All((OnlineRoomChatMessageDto existing) => existing.Id != message.Id)orderby message.Id
        select message).ToArray();
    bool flag = roomChatInitialized;
    OnlineRoomChatMessageDto[] array2 = array;
    foreach (OnlineRoomChatMessageDto onlineRoomChatMessageDto in array2)
    {
        roomChatMessages.Add(onlineRoomChatMessageDto);
        latestRoomMessageId = Math.Max(latestRoomMessageId, onlineRoomChatMessageDto.Id);
        if (flag && onlineRoomChatMessageDto.UserId != valueOrDefault && !IsRoomChatActivelyVisible())
        {
            SetRoomChatUnread(unread: true);
        }
    }

    latestRoomMessageId = Math.Max(latestRoomMessageId, latestMessageId);
    roomChatInitialized = true;
    if (roomChatMessages.Count > 200)
    {
        roomChatMessages.RemoveRange(0, roomChatMessages.Count - 200);
    }

    if (array.Length != 0)
    {
        roomChatList.ItemsSource = roomChatMessages.ToArray();
        ListBox listBox = roomChatList;
        List<OnlineRoomChatMessageDto> list = roomChatMessages;
        listBox.ScrollIntoView(list[list.Count - 1]);
    }
}

private async Task SendRoomChatAsync()
{
    OnlineAccountSession onlineAccountSession = onlineAccountHost?.Session;
    string message = roomChatInputBox.Text?.Trim() ?? string.Empty;
    if (roomChatSendInProgress || onlineAccountSession?.CurrentRoom == null || message.Length == 0)
    {
        return;
    }

    roomChatSendInProgress = true;
    sendRoomChatButton.IsEnabled = false;
    roomChatStatusText.Text = "正在发送…";
    try
    {
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15.0));
        OnlineRoomLifecycleResult<OnlineRoomChatMessageDto> onlineRoomLifecycleResult = await onlineAccountSession.SendRoomChatAsync(message, timeout.Token);
        if (onlineRoomLifecycleResult.Outcome != OnlineRoomLifecycleOutcome.Success || onlineRoomLifecycleResult.Value == null)
        {
            roomChatStatusText.Text = "消息未发送：房间已结束或无发送权限";
            return;
        }

        AppendRoomChatMessages(new OnlineRoomChatMessageDto[]
{
    onlineRoomLifecycleResult.Value
}, Math.Max(latestRoomMessageId, onlineRoomLifecycleResult.Value.Id));
        if (string.Equals(roomChatInputBox.Text?.Trim() ?? string.Empty, message, StringComparison.Ordinal))
        {
            roomChatInputBox.Text = string.Empty;
        }

        roomChatStatusText.Text = string.Empty;
        SetRoomChatUnread(unread: false);
    }
    catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is OperationCanceledException || ex is JsonException) ? true : false)
    {
        roomChatStatusText.Text = AvaloniaLocalizationCatalog.Format("发送失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message));
    }
    finally
    {
        roomChatSendInProgress = false;
        sendRoomChatButton.IsEnabled = onlineAccountHost?.Session.CurrentRoom != null;
    }
}

private void RoomCollaborationHeader_PointerPressed(object? sender, PointerPressedEventArgs eventArgs)
{
    if (sender is Control control && !roomCollaborationToggleButton.IsPointerOver && eventArgs.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
    {
        Point? point = roomCollaborationPanel.TranslatePoint(new Point(0.0, 0.0), editorWorkbench);
        if (point.HasValue)
        {
            roomCollaborationDragging = true;
            roomCollaborationDragStart = eventArgs.GetPosition(editorWorkbench);
            roomCollaborationDragStartX = roomCollaborationTransform.X;
            roomCollaborationDragStartY = roomCollaborationTransform.Y;
            roomCollaborationBaseLeft = point.Value.X - roomCollaborationTransform.X;
            roomCollaborationBaseTop = point.Value.Y - roomCollaborationTransform.Y;
            eventArgs.Pointer.Capture(control);
            eventArgs.Handled = true;
        }
    }
}

private void RoomCollaborationHeader_PointerMoved(object? sender, PointerEventArgs eventArgs)
{
    if (roomCollaborationDragging && sender is Control relativeTo && eventArgs.GetCurrentPoint(relativeTo).Properties.IsLeftButtonPressed)
    {
        Point position = eventArgs.GetPosition(editorWorkbench);
        double value = roomCollaborationDragStartX + position.X - roomCollaborationDragStart.X;
        double value2 = roomCollaborationDragStartY + position.Y - roomCollaborationDragStart.Y;
        roomCollaborationTransform.X = Math.Clamp(value, 4.0 - roomCollaborationBaseLeft, Math.Max(4.0 - roomCollaborationBaseLeft, editorWorkbench.Bounds.Width - roomCollaborationPanel.Bounds.Width - roomCollaborationBaseLeft - 4.0));
        roomCollaborationTransform.Y = Math.Clamp(value2, 4.0 - roomCollaborationBaseTop, Math.Max(4.0 - roomCollaborationBaseTop, editorWorkbench.Bounds.Height - roomCollaborationPanel.Bounds.Height - roomCollaborationBaseTop - 4.0));
        eventArgs.Handled = true;
    }
}

private void RoomCollaborationHeader_PointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
{
    if (roomCollaborationDragging)
    {
        roomCollaborationDragging = false;
        eventArgs.Pointer.Capture(null);
        eventArgs.Handled = true;
    }
}

private void ClampRoomCollaborationPosition()
{
    if (!roomCollaborationDragging && !(editorWorkbench.Bounds.Width <= 0.0) && !(editorWorkbench.Bounds.Height <= 0.0) && !(roomCollaborationPanel.Bounds.Width <= 0.0))
    {
        Point? point = roomCollaborationPanel.TranslatePoint(new Point(0.0, 0.0), editorWorkbench);
        if (point.HasValue)
        {
            double num = point.Value.X - roomCollaborationTransform.X;
            double num2 = point.Value.Y - roomCollaborationTransform.Y;
            roomCollaborationTransform.X = Math.Clamp(roomCollaborationTransform.X, 4.0 - num, Math.Max(4.0 - num, editorWorkbench.Bounds.Width - roomCollaborationPanel.Bounds.Width - num - 4.0));
            roomCollaborationTransform.Y = Math.Clamp(roomCollaborationTransform.Y, 4.0 - num2, Math.Max(4.0 - num2, editorWorkbench.Bounds.Height - roomCollaborationPanel.Bounds.Height - num2 - 4.0));
        }
    }
}

private Control BuildExtensionPalette()
{
    Border icon = new Border
    {
        Width = 9.0,
        Height = 20.0,
        BorderBrush = new SolidColorBrush(Color.Parse("#8FB3B6")),
        BorderThickness = new Thickness(1.5),
        CornerRadius = new CornerRadius(2.0)
    };
    Button button = (extensionSelectLongNotesButton = ExtensionPaletteButton("只选中面", icon, (object? _, RoutedEventArgs _) =>
    {
        FilterSelectionByNoteType(longNotes: true);
    }, modeButton: true));
    AutomationProperties.SetAutomationId(button, "Editor.Extension.SelectLongNotes");
    ToolTip.SetTip(button, "从当前选区中只保留 LN（面）");
    Border icon2 = new Border
    {
        Width = 17.0,
        Height = 8.0,
        BorderBrush = new SolidColorBrush(Color.Parse("#8FB3B6")),
        BorderThickness = new Thickness(1.5),
        CornerRadius = new CornerRadius(2.0)
    };
    Button button2 = (extensionSelectTapsButton = ExtensionPaletteButton("只选中米", icon2, (object? _, RoutedEventArgs _) =>
    {
        FilterSelectionByNoteType(longNotes: false);
    }, modeButton: true));
    AutomationProperties.SetAutomationId(button2, "Editor.Extension.SelectTaps");
    ToolTip.SetTip(button2, "从当前选区中只保留 Tap（米）");
    Border icon3 = new Border
    {
        Width = 16.0,
        Height = 8.0,
        Background = new SolidColorBrush(Color.Parse("#252C2F")),
        BorderBrush = new SolidColorBrush(Color.Parse("#9A86B8")),
        BorderThickness = new Thickness(3.0),
        CornerRadius = new CornerRadius(2.0)
    };
    Button button3 = (hideLongNoteBodiesButton = ExtensionPaletteButton("隐藏面身", icon3, (object? _, RoutedEventArgs _) =>
    {
        ToggleLongNoteBodyVisibility();
    }, modeButton: true));
    AutomationProperties.SetAutomationId(button3, "Editor.Extension.HideLongNoteBodies");
    ToolTip.SetTip(button3, "隐藏 LN 身体和尾部，仅用边框色区分头部 Note");
    Button button4 = (longToTapButton = ExtensionPaletteButton("面 → 米", null, async (object? _, RoutedEventArgs _) =>
    {
        await ConvertLongNotesToTapsAsync();
    }));
    AutomationProperties.SetAutomationId(button4, "Editor.Extension.LongToTap");
    ToolTip.SetTip(button4, "把选中的面转换为米");
    Button button5 = (tapToLongButton = ExtensionPaletteButton("米 → 面", null, (object? _, RoutedEventArgs _) =>
    {
        ConvertTapsToLongNotes();
    }));
    AutomationProperties.SetAutomationId(button5, "Editor.Extension.TapToLong");
    ToolTip.SetTip(button5, "把选中的米延伸到同列下一颗 Note 前一格");
    Button button6 = (randomPermutationButton = ExtensionPaletteButton("随机排列", null, (object? _, RoutedEventArgs _) =>
    {
        RandomizeVisibleSelection(perBeat: false);
    }));
    AutomationProperties.SetAutomationId(button6, "Editor.Extension.RandomPermutation");
    ToolTip.SetTip(button6, "整组选区使用同一套随机轨道映射");
    Button button7 = (fullRandomButton = ExtensionPaletteButton("完全随机", null, (object? _, RoutedEventArgs _) =>
    {
        RandomizeVisibleSelection(perBeat: true);
    }));
    AutomationProperties.SetAutomationId(button7, "Editor.Extension.FullRandom");
    ToolTip.SetTip(button7, "每一排分别生成随机轨道映射");
    Grid grid = new Grid
    {
        Margin = new Thickness(0.0, 5.0, 0.0, 0.0),
        ColumnDefinitions = new ColumnDefinitions("*,*"),
        RowDefinitions = new RowDefinitions("38,38,38,38")
    };
    button.Margin = new Thickness(0.0, 0.0, 2.0, 2.0);
    grid.Children.Add(button);
    button2.Margin = new Thickness(2.0, 0.0, 0.0, 2.0);
    Grid.SetColumn(button2, 1);
    grid.Children.Add(button2);
    button4.Margin = new Thickness(0.0, 2.0, 2.0, 0.0);
    Grid.SetRow(button4, 1);
    grid.Children.Add(button4);
    button5.Margin = new Thickness(2.0, 2.0, 0.0, 0.0);
    Grid.SetRow(button5, 1);
    Grid.SetColumn(button5, 1);
    grid.Children.Add(button5);
    button6.Margin = new Thickness(0.0, 2.0, 2.0, 0.0);
    Grid.SetRow(button6, 2);
    grid.Children.Add(button6);
    button7.Margin = new Thickness(2.0, 2.0, 0.0, 0.0);
    Grid.SetRow(button7, 2);
    Grid.SetColumn(button7, 1);
    grid.Children.Add(button7);
    button3.Margin = new Thickness(0.0, 2.0, 0.0, 0.0);
    Grid.SetRow(button3, 3);
    Grid.SetColumnSpan(button3, 2);
    grid.Children.Add(button3);
    Grid grid2 = new Grid
    {
        Margin = new Thickness(8.0, 0.0),
        ColumnDefinitions = new ColumnDefinitions("*,Auto")
    };
    grid2.Children.Add(new TextBlock { Text = "拓展功能", Foreground = new SolidColorBrush(Color.Parse("#D6DADB")), FontSize = 11.0, VerticalAlignment = VerticalAlignment.Center });
    TextBlock textBlock = new TextBlock
    {
        Text = "⠿",
        Foreground = new SolidColorBrush(Color.Parse("#7D878C")),
        FontSize = 15.0,
        VerticalAlignment = VerticalAlignment.Center
    };
    Grid.SetColumn(textBlock, 1);
    grid2.Children.Add(textBlock);
    Border border = new Border
    {
        Height = 28.0,
        Background = new SolidColorBrush(Color.Parse("#202428")),
        CornerRadius = new CornerRadius(3.0),
        Cursor = new Cursor(StandardCursorType.SizeAll),
        Child = grid2
    };
    ToolTip.SetTip(border, "按住拖动拓展功能浮窗");
    AutomationProperties.SetAutomationId(border, "Editor.Extension.Header");
    border.PointerPressed += ExtensionPaletteHeader_PointerPressed;
    border.PointerMoved += ExtensionPaletteHeader_PointerMoved;
    border.PointerReleased += ExtensionPaletteHeader_PointerReleased;
    border.PointerCaptureLost += (object? _, PointerCaptureLostEventArgs _) =>
    {
        extensionPaletteDragging = false;
    };
    StackPanel child = new StackPanel
    {
        Width = 194.0,
        Children =
        {
            (Control)border,
            (Control)grid,
            (Control)new TextBlock
            {
                Text = "视图模式与可见正式轨道 Note 工具",
                Foreground = new SolidColorBrush(Color.Parse("#748186")),
                FontSize = 9.0,
                Margin = new Thickness(5.0, 6.0, 3.0, 2.0)
            }
        }
    };
    extensionPalette.Background = new SolidColorBrush(Color.Parse("#F51A1D20"));
    extensionPalette.BorderBrush = new SolidColorBrush(Color.Parse("#3B4246"));
    extensionPalette.BorderThickness = new Thickness(1.0);
    extensionPalette.CornerRadius = new CornerRadius(4.0);
    extensionPalette.Padding = new Thickness(5.0);
    extensionPalette.Margin = new Thickness(176.0, 54.0, 0.0, 0.0);
    extensionPalette.HorizontalAlignment = HorizontalAlignment.Left;
    extensionPalette.VerticalAlignment = VerticalAlignment.Top;
    extensionPalette.ZIndex = 26;
    extensionPalette.IsVisible = false;
    extensionPalette.RenderTransform = extensionPaletteTransform;
    extensionPalette.Child = child;
    AutomationProperties.SetAutomationId(extensionPalette, "Editor.Extension.Palette");
    return extensionPalette;
}

private static Button ExtensionPaletteButton(string text, Control? icon, EventHandler<RoutedEventArgs> handler, bool modeButton = false)
{
    Control content = ((icon != null) ? ((Control)new StackPanel
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            icon,
            (Control)new TextBlock
            {
                Text = text,
                Margin = new Thickness(7.0, 0.0, 0.0, 0.0),
                VerticalAlignment = VerticalAlignment.Center
            }
        }
    }

    ) : ((Control)new TextBlock
    {
        Text = text,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    }

    ));
    Button button = new Button
    {
        Padding = new Thickness(7.0, 4.0)
    };
    IBrush background;
    if (!modeButton)
    {
        IBrush transparent = Brushes.Transparent;
        background = transparent;
    }
    else
    {
        IBrush transparent = new SolidColorBrush(Color.Parse("#202428"));
        background = transparent;
    }

    button.Background = background;
    button.Foreground = Brushes.White;
    button.BorderBrush = StrokeBrush;
    button.BorderThickness = new Thickness(1.0);
    button.CornerRadius = new CornerRadius(3.0);
    button.Content = content;
    button.Click += handler;
    return button;
}

private void ToggleExtensionPalette()
{
    extensionPalette.IsVisible = !extensionPalette.IsVisible;
    if (extensionMenuButton != null)
    {
        extensionMenuButton.Tag = (extensionPalette.IsVisible ? "Active" : null);
        Button? button = extensionMenuButton;
        IBrush background;
        if (!extensionPalette.IsVisible)
        {
            IBrush transparent = Brushes.Transparent;
            background = transparent;
        }
        else
        {
            IBrush transparent = new SolidColorBrush(Color.Parse("#22282A"));
            background = transparent;
        }

        button.Background = background;
        Button? button2 = extensionMenuButton;
        IBrush borderBrush;
        if (!extensionPalette.IsVisible)
        {
            IBrush transparent = Brushes.Transparent;
            borderBrush = transparent;
        }
        else
        {
            IBrush transparent = new SolidColorBrush(Color.Parse("#5F7E82"));
            borderBrush = transparent;
        }

        button2.BorderBrush = borderBrush;
        extensionMenuButton.Foreground = (extensionPalette.IsVisible ? new SolidColorBrush(Color.Parse("#F0F2F2")) : new SolidColorBrush(Color.Parse("#D0D4D6")));
        AutomationProperties.SetHelpText(extensionMenuButton, extensionPalette.IsVisible ? "拓展功能已展开" : "拓展功能已收起");
    }

    if (extensionPalette.IsVisible)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(ClampExtensionPalettePosition, DispatcherPriority.Loaded);
    }

    timeline.Focus();
}

private void FilterSelectionByNoteType(bool longNotes)
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab != null)
    {
        int num = WindowsNoteTypeTransformCommand.FilterSelectionByNoteType(activeTab.Session, longNotes, activeTab.HiddenGroups);
        timeline.SetSelectedTiming(null);
        SyncTimingListSelection(Array.Empty<Guid>());
        UpdateSelectionUi();
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已保留 {0} 个{1}", num, longNotes ? AvaloniaLocalizationCatalog.Text("面") : AvaloniaLocalizationCatalog.Text("米")));
    }
}

private void ToggleLongNoteBodyVisibility()
{
    bool flag = !timeline.HideLongNoteBodies;
    timeline.SetHideLongNoteBodies(flag);
    if (hideLongNoteBodiesButton != null)
    {
        ApplyQuickPaletteModeState(hideLongNoteBodiesButton, flag);
        AutomationProperties.SetHelpText(hideLongNoteBodiesButton, flag ? "隐藏面身已开启" : "隐藏面身已关闭");
    }

    viewModel.SetStatus(flag ? "已隐藏 LN 身体和尾部：头部 Note 以边框色区分" : "已恢复 LN 身体显示");
    timeline.Focus();
}

private void ExtensionPaletteHeader_PointerPressed(object? sender, PointerPressedEventArgs eventArgs)
{
    if (sender is Control control && eventArgs.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
    {
        Point? point = extensionPalette.TranslatePoint(new Point(0.0, 0.0), timeline);
        if (point.HasValue)
        {
            extensionPaletteDragging = true;
            extensionPaletteDragStart = eventArgs.GetPosition(timeline);
            extensionPaletteDragStartX = extensionPaletteTransform.X;
            extensionPaletteDragStartY = extensionPaletteTransform.Y;
            extensionPaletteBaseLeft = point.Value.X - extensionPaletteTransform.X;
            extensionPaletteBaseTop = point.Value.Y - extensionPaletteTransform.Y;
            eventArgs.Pointer.Capture(control);
            eventArgs.Handled = true;
        }
    }
}

private void ExtensionPaletteHeader_PointerMoved(object? sender, PointerEventArgs eventArgs)
{
    if (extensionPaletteDragging && sender is Control relativeTo && eventArgs.GetCurrentPoint(relativeTo).Properties.IsLeftButtonPressed)
    {
        Point position = eventArgs.GetPosition(timeline);
        extensionPaletteTransform.X = extensionPaletteDragStartX + position.X - extensionPaletteDragStart.X;
        extensionPaletteTransform.Y = extensionPaletteDragStartY + position.Y - extensionPaletteDragStart.Y;
        ClampExtensionPalettePosition();
        eventArgs.Handled = true;
    }
}

private void ExtensionPaletteHeader_PointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
{
    if (extensionPaletteDragging)
    {
        extensionPaletteDragging = false;
        eventArgs.Pointer.Capture(null);
        eventArgs.Handled = true;
    }
}

private void ClampExtensionPalettePosition()
{
    if (!(timeline.Bounds.Width <= 0.0) && !(timeline.Bounds.Height <= 0.0) && !(extensionPalette.Bounds.Width <= 0.0) && !(extensionPalette.Bounds.Height <= 0.0))
    {
        Point? point = extensionPalette.TranslatePoint(new Point(0.0, 0.0), timeline);
        if (point.HasValue)
        {
            double num = point.Value.X - extensionPaletteTransform.X;
            double num2 = point.Value.Y - extensionPaletteTransform.Y;
            extensionPaletteTransform.X = Math.Clamp(extensionPaletteTransform.X, 4.0 - num, Math.Max(4.0 - num, timeline.Bounds.Width - extensionPalette.Bounds.Width - num - 4.0));
            extensionPaletteTransform.Y = Math.Clamp(extensionPaletteTransform.Y, 4.0 - num2, Math.Max(4.0 - num2, timeline.Bounds.Height - extensionPalette.Bounds.Height - num2 - 4.0));
        }
    }
}

private void MarkEditorViewSettingsDirty()
{
    editorViewSettingsDirty = true;
    ScheduleEditorViewSettingsSave();
}

private void AddQuickTimelineToolButton(StackPanel panel, TimelineTool tool, string text, string geometry, bool filledIcon = false)
{
    Button button = QuickPaletteButton(text, geometry, (object? _, RoutedEventArgs _) =>
    {
        SetTimelineTool(tool);
    }, out TextBlock _, filledIcon);
    AutomationProperties.SetAutomationId(button, $"Editor.QuickTool.{tool}");
    quickTimelineToolButtons.Add(tool, button);
    RegisterQuickShortcutButton(tool switch
    {
        TimelineTool.Select => "ToolSelect",
        TimelineTool.Tap => "ToolTap",
        TimelineTool.Hold => "ToolHold",
        TimelineTool.Erase => "ToolErase",
        _ => throw new ArgumentOutOfRangeException("tool", tool, null),
    }, AvaloniaLocalizationCatalog.Format("{0}工具", text), button);
    panel.Children.Add(button);
}

private void AddAudioVisualizationPanels()
{
    ConfigureAudioPanel(waveformPanel, waveformHeader, waveformTitleText, waveformModeButton, waveformCollapseButton, waveformSideSurface, waveformPanelTransform, WindowsAudioPanelKind.Waveform);
    ConfigureAudioPanel(spectrogramPanel, spectrogramHeader, spectrogramTitleText, spectrogramModeButton, spectrogramCollapseButton, spectrogramSideSurface, spectrogramPanelTransform, WindowsAudioPanelKind.Spectrogram);
    spectrogramPanel.ZIndex = 18;
    waveformPanel.ZIndex = 19;
    Grid.SetRowSpan(spectrogramPanel, 2);
    Grid.SetRowSpan(waveformPanel, 2);
    timelineHost.Children.Add(spectrogramPanel);
    timelineHost.Children.Add(waveformPanel);
}

private void ConfigureAudioPanel(Border panel, Border header, TextBlock title, Button modeButton, Button collapseButton, AudioVisualizationSideSurface surface, TranslateTransform transform, WindowsAudioPanelKind kind)
{
    panel.HorizontalAlignment = HorizontalAlignment.Left;
    panel.VerticalAlignment = VerticalAlignment.Stretch;
    panel.Margin = new Thickness(0.0, 48.0, 0.0, 32.0);
    panel.Background = new SolidColorBrush(Color.Parse((kind == WindowsAudioPanelKind.Waveform) ? "#F2171A1C" : "#F216191C"));
    panel.BorderBrush = new SolidColorBrush(Color.Parse((kind == WindowsAudioPanelKind.Waveform) ? "#FF3B4246" : "#FF3B464A"));
    panel.BorderThickness = new Thickness(1.0);
    panel.CornerRadius = new CornerRadius(4.0);
    panel.RenderTransform = transform;
    AutomationProperties.SetName(panel, (kind == WindowsAudioPanelKind.Waveform) ? "音频波形面板" : "音频频谱面板");
    AutomationProperties.SetAutomationId(panel, (kind == WindowsAudioPanelKind.Waveform) ? "Audio.WaveformPanel" : "Audio.SpectrogramPanel");
    Grid grid = new Grid
    {
        RowDefinitions = new RowDefinitions("25,*")
    };
    header.Background = new SolidColorBrush(Color.Parse("#ED1A1E20"));
    header.BorderBrush = new SolidColorBrush(Color.Parse("#FF31383B"));
    header.BorderThickness = new Thickness(0.0, 0.0, 0.0, 1.0);
    header.CornerRadius = new CornerRadius(3.0, 3.0, 0.0, 0.0);
    header.Cursor = new Cursor(StandardCursorType.SizeWestEast);
    ToolTip.SetTip(header, (kind == WindowsAudioPanelKind.Waveform) ? "横向拖动波形栏（收起后仍可拖动）" : "横向拖动频谱栏（收起后仍可拖动）；右键调整频谱强度与细节");
    if (kind == WindowsAudioPanelKind.Spectrogram)
    {
        header.ContextFlyout = BuildSpectrogramAppearanceFlyout();
    }

    AutomationProperties.SetAutomationId(header, (kind == WindowsAudioPanelKind.Waveform) ? "Audio.WaveformHeader" : "Audio.SpectrogramHeader");
    header.PointerPressed += AudioPanelHeader_PointerPressed;
    header.PointerMoved += AudioPanelHeader_PointerMoved;
    header.PointerReleased += AudioPanelHeader_PointerReleased;
    header.PointerCaptureLost += (object? _, PointerCaptureLostEventArgs _) =>
    {
        EndAudioPanelDrag();
    };
    Grid grid2 = new Grid
    {
        Margin = new Thickness(7.0, 0.0, 3.0, 0.0),
        ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto")
    };
    title.Foreground = new SolidColorBrush(Color.Parse((kind == WindowsAudioPanelKind.Waveform) ? "#FF879397" : "#FF87979A"));
    title.FontSize = 8.0;
    title.FontWeight = FontWeight.DemiBold;
    title.VerticalAlignment = VerticalAlignment.Center;
    grid2.Children.Add(title);
    ConfigureAudioPanelButton(modeButton);
    ConfigureAudioPanelButton(collapseButton);
    ToolTip.SetTip(modeButton, "切换侧栏 / 谱面背景");
    ToolTip.SetTip(collapseButton, (kind == WindowsAudioPanelKind.Waveform) ? "收起 / 展开音频波形" : "收起 / 展开音频频谱");
    AutomationProperties.SetAutomationId(modeButton, (kind == WindowsAudioPanelKind.Waveform) ? "Audio.WaveformMode" : "Audio.SpectrogramMode");
    AutomationProperties.SetAutomationId(collapseButton, (kind == WindowsAudioPanelKind.Waveform) ? "Audio.WaveformCollapse" : "Audio.SpectrogramCollapse");
    modeButton.Click += (object? _, RoutedEventArgs _) =>
    {
        ToggleAudioPanelMode(kind);
    };
    collapseButton.Click += (object? _, RoutedEventArgs _) =>
    {
        ToggleAudioPanelCollapsed(kind);
    };
    Grid.SetColumn(modeButton, 1);
    Grid.SetColumn(collapseButton, 2);
    grid2.Children.Add(modeButton);
    grid2.Children.Add(collapseButton);
    header.Child = grid2;
    grid.Children.Add(header);
    surface.Margin = new Thickness(3.0);
    surface.IsHitTestVisible = false;
    panel.PointerWheelChanged += (object? _, PointerWheelEventArgs eventArgs) =>
    {
        timeline.HandleCompanionWheel(eventArgs);
        eventArgs.Handled = true;
    };
    AutomationProperties.SetAutomationId(surface, (kind == WindowsAudioPanelKind.Waveform) ? "Audio.WaveformSurface" : "Audio.SpectrogramSurface");
    Grid.SetRow(surface, 1);
    grid.Children.Add(surface);
    panel.Child = grid;
    if (kind == WindowsAudioPanelKind.Spectrogram)
    {
        ToolTip.SetTip(panel, "频谱横轴：左侧低频 Bass / 底鼓，中间鼓与主体，右侧高频镲片 / 人声辅音起音");
    }
}

private static void ConfigureAudioPanelButton(Button button)
{
    button.Width = 21.0;
    button.Height = 19.0;
    button.Padding = new Thickness(0.0);
    button.Margin = new Thickness(1.0, 0.0, 0.0, 0.0);
    button.FontSize = 10.0;
    button.Foreground = Brushes.White;
    button.Background = Brushes.Transparent;
    button.BorderBrush = new SolidColorBrush(Color.Parse("#553B464A"));
    button.BorderThickness = new Thickness(1.0);
    button.VerticalAlignment = VerticalAlignment.Center;
}

private WindowsAudioPanelState GetAudioPanelState(WindowsAudioPanelKind kind)
{
    if (kind != WindowsAudioPanelKind.Waveform)
    {
        return audioPanelSettings.Spectrogram;
    }

    return audioPanelSettings.Waveform;
}

private TranslateTransform GetAudioPanelTransform(WindowsAudioPanelKind kind)
{
    if (kind != WindowsAudioPanelKind.Waveform)
    {
        return spectrogramPanelTransform;
    }

    return waveformPanelTransform;
}

private void SetAudioPanelState(WindowsAudioPanelKind kind, WindowsAudioPanelState state)
{
    audioPanelSettings = ((kind == WindowsAudioPanelKind.Waveform) ? audioPanelSettings.WithWaveform(state) : audioPanelSettings.WithSpectrogram(state));
    audioPanelSettingsDirty = true;
    ApplyAudioPanelLayout();
    UpdateAudioVisualizationUi();
    ScheduleAudioPanelSettingsSave();
}

private void ToggleAudioPanelMode(WindowsAudioPanelKind kind)
{
    WindowsAudioPanelState audioPanelState = GetAudioPanelState(kind);
    TranslateTransform audioPanelTransform = GetAudioPanelTransform(kind);
    WindowsAudioPanelDisplayMode mode = ((audioPanelState.Mode != WindowsAudioPanelDisplayMode.Background) ? WindowsAudioPanelDisplayMode.Background : WindowsAudioPanelDisplayMode.Side);
    SetAudioPanelState(kind, audioPanelState.WithPosition(audioPanelTransform.X, audioPanelTransform.Y).WithDocked(docked: false).WithMode(mode).WithCollapsed(collapsed: false));
}

private void ToggleAudioPanelCollapsed(WindowsAudioPanelKind kind)
{
    WindowsAudioPanelState windowsAudioPanelState = GetAudioPanelState(kind);
    if (!windowsAudioPanelState.Collapsed)
    {
        TranslateTransform audioPanelTransform = GetAudioPanelTransform(kind);
        windowsAudioPanelState = windowsAudioPanelState.WithPosition(audioPanelTransform.X, audioPanelTransform.Y).WithDocked(docked: false);
    }

    SetAudioPanelState(kind, windowsAudioPanelState.WithCollapsed(!windowsAudioPanelState.Collapsed));
}

private void SetAudioPanelBackgroundMode(WindowsAudioPanelKind kind, bool background)
{
    WindowsAudioPanelState audioPanelState = GetAudioPanelState(kind);
    WindowsAudioPanelDisplayMode windowsAudioPanelDisplayMode = (background ? WindowsAudioPanelDisplayMode.Background : WindowsAudioPanelDisplayMode.Side);
    if (audioPanelState.Mode != windowsAudioPanelDisplayMode || audioPanelState.Collapsed)
    {
        TranslateTransform audioPanelTransform = GetAudioPanelTransform(kind);
        SetAudioPanelState(kind, audioPanelState.WithPosition(audioPanelTransform.X, audioPanelTransform.Y).WithDocked(docked: false).WithMode(windowsAudioPanelDisplayMode).WithCollapsed(collapsed: false));
    }
}

private void AudioPanelHeader_PointerPressed(object? sender, PointerPressedEventArgs eventArgs)
{
    if (sender is Border border && eventArgs.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
    {
        WindowsAudioPanelKind windowsAudioPanelKind = ((border != waveformHeader) ? WindowsAudioPanelKind.Spectrogram : WindowsAudioPanelKind.Waveform);
        Button button = ((windowsAudioPanelKind == WindowsAudioPanelKind.Waveform) ? waveformModeButton : spectrogramModeButton);
        Button button2 = ((windowsAudioPanelKind == WindowsAudioPanelKind.Waveform) ? waveformCollapseButton : spectrogramCollapseButton);
        if (!button.IsPointerOver && !button2.IsPointerOver)
        {
            draggingAudioPanelKind = windowsAudioPanelKind;
            audioPanelDragStart = eventArgs.GetPosition(timelineHost);
            TranslateTransform audioPanelTransform = GetAudioPanelTransform(windowsAudioPanelKind);
            audioPanelDragStartX = audioPanelTransform.X;
            audioPanelDragStartY = audioPanelTransform.Y;
            eventArgs.Pointer.Capture(border);
            eventArgs.Handled = true;
        }
    }
}

private void AudioPanelHeader_PointerMoved(object? sender, PointerEventArgs eventArgs)
{
    if (!(sender is Border relativeTo))
    {
        return;
    }

    WindowsAudioPanelKind? windowsAudioPanelKind = draggingAudioPanelKind;
    if (windowsAudioPanelKind.HasValue)
    {
        WindowsAudioPanelKind valueOrDefault = windowsAudioPanelKind.GetValueOrDefault();
        if (eventArgs.GetCurrentPoint(relativeTo).Properties.IsLeftButtonPressed)
        {
            Point position = eventArgs.GetPosition(timelineHost);
            WindowsAudioPanelState windowsAudioPanelState = WindowsAudioPanelLayoutPolicy.ApplyDrag(GetAudioPanelState(valueOrDefault), valueOrDefault, audioPanelDragStartX + position.X - audioPanelDragStart.X, audioPanelDragStartY + position.Y - audioPanelDragStart.Y, timelineHost.Bounds.Width, timelineHost.Bounds.Height);
            TranslateTransform audioPanelTransform = GetAudioPanelTransform(valueOrDefault);
            audioPanelTransform.X = windowsAudioPanelState.X;
            audioPanelTransform.Y = windowsAudioPanelState.Y;
            eventArgs.Handled = true;
        }
    }
}

private void AudioPanelHeader_PointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
{
    if (sender is Border && draggingAudioPanelKind.HasValue)
    {
        EndAudioPanelDrag();
        eventArgs.Pointer.Capture(null);
        eventArgs.Handled = true;
    }
}

private void EndAudioPanelDrag()
{
    WindowsAudioPanelKind? windowsAudioPanelKind = draggingAudioPanelKind;
    if (windowsAudioPanelKind.HasValue)
    {
        WindowsAudioPanelKind valueOrDefault = windowsAudioPanelKind.GetValueOrDefault();
        draggingAudioPanelKind = null;
        TranslateTransform audioPanelTransform = GetAudioPanelTransform(valueOrDefault);
        WindowsAudioPanelState state = WindowsAudioPanelLayoutPolicy.ApplyDrag(GetAudioPanelState(valueOrDefault), valueOrDefault, audioPanelTransform.X, audioPanelTransform.Y, timelineHost.Bounds.Width, timelineHost.Bounds.Height);
        SetAudioPanelState(valueOrDefault, state);
    }
}

private void QueueAudioPanelLayout(bool clampFreePanels = false)
{
    if (clampFreePanels && timelineHost.Bounds.Width > 0.0 && timelineHost.Bounds.Height > 0.0)
    {
        audioPanelSettings = audioPanelSettings.WithPanels(WindowsAudioPanelLayoutPolicy.ClampAfterResize(audioPanelSettings.Waveform, WindowsAudioPanelKind.Waveform, timelineHost.Bounds.Width, timelineHost.Bounds.Height), WindowsAudioPanelLayoutPolicy.ClampAfterResize(audioPanelSettings.Spectrogram, WindowsAudioPanelKind.Spectrogram, timelineHost.Bounds.Width, timelineHost.Bounds.Height));
    }

    if (!audioPanelLayoutQueued)
    {
        audioPanelLayoutQueued = true;
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            audioPanelLayoutQueued = false;
            ApplyAudioPanelLayout();
        }, DispatcherPriority.Loaded);
    }
}

private void ApplyAudioPanelLayout()
{
    if (boardFrames.Count > 0)
    {
        waveformPanel.IsVisible = false;
        spectrogramPanel.IsVisible = false;
        SyncBoardAudioViewports();
        return;
    }

    if (timelineHost.Bounds.Width <= 0.0 || timelineHost.Bounds.Height <= 0.0)
    {
        return;
    }

    Rect activeTrackBounds = timeline.GetActiveTrackBounds();
    Point? point = timeline.TranslatePoint(new Point(activeTrackBounds.Left, 0.0), timelineHost);
    Point? point2 = timeline.TranslatePoint(new Point(activeTrackBounds.Right, 0.0), timelineHost);
    WindowsAudioTrackBounds windowsAudioTrackBounds;
    if (activeTrackBounds.Width > 0.0 && point.HasValue)
    {
        Point valueOrDefault = point.GetValueOrDefault();
        if (point2.HasValue)
        {
            Point valueOrDefault2 = point2.GetValueOrDefault();
            windowsAudioTrackBounds = new WindowsAudioTrackBounds(valueOrDefault.X, valueOrDefault2.X);
            goto IL_011d;
        }
    }

    windowsAudioTrackBounds = default;
    goto IL_011d;
    IL_011d:
        WindowsAudioTrackBounds track = windowsAudioTrackBounds;
    WindowsAudioPanelsLayout windowsAudioPanelsLayout = WindowsAudioPanelLayoutPolicy.Resolve(audioPanelSettings, timelineHost.Bounds.Width, timelineHost.Bounds.Height, track);
    ApplyAudioPanelVisual(waveformPanel, waveformTitleText, waveformModeButton, waveformCollapseButton, waveformSideSurface, waveformPanelTransform, WindowsAudioPanelKind.Waveform, audioPanelSettings.Waveform, windowsAudioPanelsLayout.Waveform);
    ApplyAudioPanelVisual(spectrogramPanel, spectrogramTitleText, spectrogramModeButton, spectrogramCollapseButton, spectrogramSideSurface, spectrogramPanelTransform, WindowsAudioPanelKind.Spectrogram, audioPanelSettings.Spectrogram, windowsAudioPanelsLayout.Spectrogram);
    UpdateAudioPanelViewports();
    QueueDifficultyCardLayout();
}

private static void ApplyAudioPanelVisual(Border panel, TextBlock title, Button modeButton, Button collapseButton, AudioVisualizationSideSurface surface, TranslateTransform transform, WindowsAudioPanelKind kind, WindowsAudioPanelState state, WindowsAudioPanelLayout layout)
{
    panel.Width = layout.Width;
    panel.Height = (layout.ToolbarOnly ? 25.0 : double.NaN);
    panel.VerticalAlignment = (layout.ToolbarOnly ? VerticalAlignment.Top : VerticalAlignment.Stretch);
    transform.X = layout.X;
    transform.Y = layout.Y;
    modeButton.Content = ((state.Mode == WindowsAudioPanelDisplayMode.Background) ? "侧" : "背");
    collapseButton.Content = (state.Collapsed ? "+" : "—");
    TextBlock textBlock = title;
    string text;
    if (kind != WindowsAudioPanelKind.Waveform)
    {
        text = (layout.ToolbarOnly ? "SPEC" : "SPEC  L · M · H");
    }
    else
    {
        text = "WAVE";
    }

    textBlock.Text = text;
    surface.IsVisible = !layout.ToolbarOnly;
    Button element = modeButton;
    string value;
    if (state.Mode != WindowsAudioPanelDisplayMode.Background)
    {
        value = "铺满谱面背景";
    }
    else
    {
        value = ((kind == WindowsAudioPanelKind.Waveform) ? "改为右侧波形栏" : "改为侧边频谱栏");
    }

    ToolTip.SetTip(element, value);
}

private void UpdateAudioPanelViewports()
{
    if (boardFrames.Count > 0)
    {
        SyncBoardAudioViewports();
        return;
    }

    UpdateAudioPanelViewport(waveformSideSurface);
    UpdateAudioPanelViewport(spectrogramSideSurface);
}

private void UpdateAudioPanelViewport(AudioVisualizationSideSurface surface)
{
    double judgementLinePosition = timeline.JudgementLinePosition;
    if (surface.IsVisible && surface.Bounds.Height > 1.0 && timeline.Bounds.Height > 1.0)
    {
        Point? point = timeline.TranslatePoint(new Point(0.0, timeline.Bounds.Height * timeline.JudgementLinePosition), surface);
        if (point.HasValue)
        {
            judgementLinePosition = Math.Clamp(point.GetValueOrDefault().Y / surface.Bounds.Height, -1.0, 2.0);
        }
    }

    surface.SetViewport(timeline.CurrentBeat, timeline.PixelsPerBeat, judgementLinePosition);
}

private Control BuildSettingsPanel()
{
    return BuildCategorizedSettingsPanel();
}

private Control BuildLanguageSettings()
{
    bool flag = suppressLanguageControls;
    suppressLanguageControls = true;
    try
    {
        languagePreferenceBox.Items.Clear();
        LumaChartLanguagePreference[] array = new LumaChartLanguagePreference[20]
        {
            LumaChartLanguagePreference.FollowSystem,
            LumaChartLanguagePreference.SimplifiedChinese,
            LumaChartLanguagePreference.TraditionalChinese,
            LumaChartLanguagePreference.English,
            LumaChartLanguagePreference.Japanese,
            LumaChartLanguagePreference.Korean,
            LumaChartLanguagePreference.Spanish,
            LumaChartLanguagePreference.French,
            LumaChartLanguagePreference.German,
            LumaChartLanguagePreference.PortugueseBrazil,
            LumaChartLanguagePreference.Russian,
            LumaChartLanguagePreference.Italian,
            LumaChartLanguagePreference.Dutch,
            LumaChartLanguagePreference.Polish,
            LumaChartLanguagePreference.Turkish,
            LumaChartLanguagePreference.Vietnamese,
            LumaChartLanguagePreference.Arabic,
            LumaChartLanguagePreference.Hindi,
            LumaChartLanguagePreference.Thai,
            LumaChartLanguagePreference.Indonesian
        };
        foreach (LumaChartLanguagePreference lumaChartLanguagePreference in array)
        {
            languagePreferenceBox.Items.Add(new ComboBoxItem { Content = LanguagePreferenceDisplayName(lumaChartLanguagePreference), Tag = lumaChartLanguagePreference });
        }

        languagePreferenceBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        languagePreferenceBox.MinWidth = 0.0;
        languagePreferenceBox.MaxWidth = 286.0;
        AutomationProperties.SetAutomationId(languagePreferenceBox, "Settings.Language");
        AutomationProperties.SetName(languagePreferenceBox, "显示语言");
        ToolTip.SetTip(languagePreferenceBox, "选择界面显示语言");
        RefreshLanguageSettingsUi();
    }
    finally
    {
        suppressLanguageControls = flag;
    }

    languagePreferenceBox.SelectionChanged += LanguagePreferenceBox_SelectionChanged;
    languageStatusText.Foreground = MutedBrush;
    languageStatusText.TextWrapping = TextWrapping.Wrap;
    languageStatusText.Margin = new Thickness(2.0, 4.0, 0.0, 1.0);
    AutomationProperties.SetAutomationId(languageStatusText, "Settings.LanguageStatus");
    return SettingsSection("语言与地区", string.Empty, "Settings.LanguageSection", TimingField("显示语言", languagePreferenceBox), languageStatusText, new TextBlock { Text = "切换后立即生效；未翻译的内容会优先显示英文，并始终保留原文。", Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2.0, 2.0, 0.0, 2.0) });
}

private static string LanguagePreferenceDisplayName(LumaChartLanguagePreference preference)
{
    return preference switch
    {
        LumaChartLanguagePreference.FollowSystem => "跟随系统",
        LumaChartLanguagePreference.SimplifiedChinese => "简体中文",
        LumaChartLanguagePreference.TraditionalChinese => "繁體中文",
        LumaChartLanguagePreference.English => "English",
        LumaChartLanguagePreference.Japanese => "日本語",
        LumaChartLanguagePreference.Korean => "한국어",
        LumaChartLanguagePreference.Spanish => "Español",
        LumaChartLanguagePreference.French => "Français",
        LumaChartLanguagePreference.German => "Deutsch",
        LumaChartLanguagePreference.PortugueseBrazil => "Português (Brasil)",
        LumaChartLanguagePreference.Russian => "Русский",
        LumaChartLanguagePreference.Italian => "Italiano",
        LumaChartLanguagePreference.Dutch => "Nederlands",
        LumaChartLanguagePreference.Polish => "Polski",
        LumaChartLanguagePreference.Turkish => "Türkçe",
        LumaChartLanguagePreference.Vietnamese => "Tiếng Việt",
        LumaChartLanguagePreference.Arabic => "العربية",
        LumaChartLanguagePreference.Hindi => "ह\u093fन\u094dद\u0940",
        LumaChartLanguagePreference.Thai => "ไทย",
        LumaChartLanguagePreference.Indonesian => "Bahasa Indonesia",
        _ => "跟随系统",
    };
}

private void LanguagePreferenceBox_SelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
{
    if (suppressLanguageControls || !(languagePreferenceBox.SelectedItem is ComboBoxItem comboBoxItem) || !(comboBoxItem.Tag is LumaChartLanguagePreference preference))
    {
        return;
    }

    languageSettings = languageSettings.WithPreference(preference);
    try
    {
        bool flag = AvaloniaLanguageSettingsStore.Save(languageSettings);
        localizationController.SetPreference(languageSettings.Preference);
        AvaloniaLocalizationRuntime.SetPreference(languageSettings.Preference);
        RefreshLanguageSettingsUi();
        if (flag)
        {
            viewModel.SetStatus(AvaloniaLocalizationCatalog.LanguageUpdatedText(localizationController.EffectiveLanguage));
            return;
        }

        languageStatusText.Text = AvaloniaLocalizationCatalog.LanguageSaveFailedText(localizationController.EffectiveLanguage, AvaloniaLanguageSettingsStore.SettingsPath);
        languageStatusText.Foreground = Brushes.IndianRed;
    }
    catch (Exception ex)
    {
        localizationController.SetPreference(languageSettings.Preference);
        AvaloniaLocalizationRuntime.SetPreference(languageSettings.Preference);
        languageStatusText.Text = AvaloniaLocalizationCatalog.LanguageSaveFailedText(localizationController.EffectiveLanguage, ex.Message);
        languageStatusText.Foreground = Brushes.IndianRed;
    }
}

private void RefreshLanguageSettingsUi()
{
    bool flag = suppressLanguageControls;
    suppressLanguageControls = true;
    try
    {
        languagePreferenceBox.SelectedItem = languagePreferenceBox.Items.OfType<ComboBoxItem>().FirstOrDefault((ComboBoxItem item) => item.Tag is LumaChartLanguagePreference lumaChartLanguagePreference && lumaChartLanguagePreference == languageSettings.Preference);
        LumaChartDisplayLanguage language = AvaloniaLanguageSettings.ResolveEffectiveLanguage(languageSettings.Preference);
        languageStatusText.Text = AvaloniaLocalizationCatalog.CurrentLanguageStatus(languageSettings.Preference, language);
        languageStatusText.Foreground = MutedBrush;
    }
    finally
    {
        suppressLanguageControls = flag;
    }
}

private void RefreshFollowSystemLanguage()
{
    if (languageSettings.Preference == LumaChartLanguagePreference.FollowSystem && localizationController.RefreshForSystemLanguage())
    {
        localizationController.Apply();
        RefreshLanguageSettingsUi();
    }
}

private Control BuildApplicationInformationSettings()
{
    settingsVersionText.Text = "当前版本 0.1.37";
    settingsVersionText.Foreground = new SolidColorBrush(Color.Parse("#7E8D91"));
    settingsVersionText.FontSize = 10.0;
    settingsVersionText.Margin = new Thickness(0.0, 3.0, 0.0, 9.0);
    AutomationProperties.SetAutomationId(settingsVersionText, "Settings.Version");
    Button button = PrimaryButton("关于", async (object? _, RoutedEventArgs _) =>
    {
        await ShowAboutAsync();
    });
    button.Height = 37.0;
    button.HorizontalAlignment = HorizontalAlignment.Stretch;
    button.Margin = new Thickness(0.0, 2.0, 0.0, 0.0);
    ApplyWpfToolButtonStyle(button);
    AutomationProperties.SetAutomationId(button, "Settings.About");
    Button button2 = PrimaryButton("问题反馈", async (object? _, RoutedEventArgs _) =>
    {
        await ShowIssueReportAsync();
    });
    button2.Height = 37.0;
    button2.HorizontalAlignment = HorizontalAlignment.Stretch;
    button2.Margin = new Thickness(0.0, 6.0, 0.0, 0.0);
    ApplyWpfPrimaryButtonStyle(button2);
    AutomationProperties.SetAutomationId(button2, "Settings.IssueReport");
    Button button3 = PrimaryButton("GitHub 仓库", (object? _, RoutedEventArgs _) =>
    {
        OpenExternalUrl("https://github.com/Ist1na07/SimpleManiaEditor");
    });
    button3.Height = 37.0;
    button3.HorizontalAlignment = HorizontalAlignment.Stretch;
    button3.Margin = new Thickness(0.0, 6.0, 0.0, 0.0);
    ApplyWpfToolButtonStyle(button3);
    AutomationProperties.SetAutomationId(button3, "Settings.Repository");
    Button button4 = PrimaryButton("检查更新", async (object? _, RoutedEventArgs _) =>
    {
        await RefreshUpdatePolicyAndShowAsync(blocksLogin: false);
    });
    button4.Height = 37.0;
    button4.HorizontalAlignment = HorizontalAlignment.Stretch;
    button4.Margin = new Thickness(0.0, 6.0, 0.0, 2.0);
    ApplyWpfPrimaryButtonStyle(button4);
    AutomationProperties.SetAutomationId(button4, "Settings.CheckForUpdates");
    return SettingsSection("SimpleManiaEditor", string.Empty, "Settings.ApplicationInformationSection", settingsVersionText, button, button2, button3, button4);
}

private Control BuildEditorViewSettings()
{
    suppressEditorViewControls = true;
    int[] array;
    try
    {
        settingsSnapBox.Items.Clear();
        array = new int[13]
        {
            1,
            2,
            3,
            4,
            5,
            6,
            7,
            8,
            9,
            10,
            11,
            12,
            16
        };
        foreach (int num in array)
        {
            settingsSnapBox.Items.Add(new ComboBoxItem { Content = GridLabel(num), Tag = num });
        }

        settingsSnapBox.IsEditable = true;
        settingsSnapBox.IsTextSearchEnabled = false;
        settingsSnapBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        SyncSettingsSnapBox(editorViewSettings.SnapDivision);
        ConfigureNoteAnchorButton(anchorLineButton, "穿线居中", "网格线穿过 Note 中心", WindowsNoteAnchorMode.GridLine, "Settings.AnchorLine");
        ConfigureNoteAnchorButton(anchorLineAboveButton, "贴在线上", "Note 位于网格线上方，底边贴线", WindowsNoteAnchorMode.LineAbove, "Settings.AnchorLineAbove");
        ConfigureNoteAnchorButton(anchorCellButton, "格内居中", "Note 位于网格单元正中", WindowsNoteAnchorMode.CellCenter, "Settings.AnchorCell");
        ConfigureNoteAnchorButton(anchorCellBottomButton, "格内靠下", "Note 完整位于网格内并靠近下边线", WindowsNoteAnchorMode.CellBottom, "Settings.AnchorCellBottom");
        ConfigureSettingsSlider(gridLaneWidthSlider, 32.0, 180.0, 1.0, editorViewSettings.GridLaneWidth, "Settings.GridLaneWidth", "栅格宽度");
        ConfigureSettingsSlider(mainEditorHorizontalOffsetSlider, -600.0, 600.0, 10.0, editorViewSettings.MainEditorHorizontalOffset, "Settings.MainEditorHorizontalOffset", "主编辑区域 X 轴偏移");
        ConfigureSettingsSlider(gridBeatHeightSlider, 12.0, 4480.0, 1.0, editorViewSettings.GridBeatHeight, "Settings.GridBeatHeight", "栅格高度");
        ConfigureSettingsSlider(noteWidthSlider, 12.0, Math.Max(12.0, editorViewSettings.GridLaneWidth - 6.0), 1.0, Math.Min(editorViewSettings.NoteWidth, Math.Max(12.0, editorViewSettings.GridLaneWidth - 6.0)), "Settings.NoteWidth", "Note 宽度");
        ConfigureSettingsSlider(noteHeightSlider, 8.0, 128.0, 1.0, editorViewSettings.NoteHeight, "Settings.NoteHeight", "Note 高度");
        ConfigureSettingsSlider(noteOpacitySlider, 5.0, 100.0, 5.0, editorViewSettings.NoteOpacity * 100.0, "Settings.NoteOpacity", "Note 透明度");
        ConfigureSettingsSlider(trackOpacitySlider, 0.0, 100.0, 5.0, WindowsTrackTransparencyPolicy.ToTransparency(editorViewSettings.TrackOpacity) * 100.0, "Settings.TrackOpacity", "轨道区域透明度");
        ConfigureSettingsSlider(backgroundDarknessSlider, 0.0, 100.0, 5.0, editorBackgroundSettings.Darkness * 100.0, "Settings.BackgroundDarkness", "整体背景暗度");
        ConfigureSpectrogramAppearanceSliders(spectrogramStrengthSlider, spectrogramDetailSlider, "Settings");
        RefreshSpectrogramAppearanceUi();
        ConfigureSettingsSlider(barLineThicknessSlider, 0.5, 6.0, 0.05, editorViewSettings.BarLineThickness, "Settings.BarLineThickness", "小节线粗细");
        ConfigureSettingsSlider(subdivisionLineThicknessSlider, 0.3, 3.0, 0.05, editorViewSettings.SubdivisionLineThickness, "Settings.SubdivisionLineThickness", "分度线粗细");
        ConfigureSettingsSlider(judgementLinePositionSlider, 15.0, 95.0, 1.0, editorViewSettings.JudgementLinePosition * 100.0, "Settings.JudgementLinePosition", "判定线位置");
        ConfigureSettingsSlider(judgementLineThicknessSlider, 0.5, 6.0, 0.5, editorViewSettings.JudgementLineThickness, "Settings.JudgementLineThickness", "判定线粗细");
        ConfigureSettingsSlider(judgementLineOpacitySlider, 10.0, 100.0, 5.0, editorViewSettings.JudgementLineOpacity * 100.0, "Settings.JudgementLineOpacity", "判定线透明度");
        frameRateLimitBox.Items.Clear();
        ComboBoxItem selectedItem = null;
        int num2 = EffectiveFrameRateLimit();
        foreach (int item2 in WindowsFrameRateOptions.PresentablePresets(PlatformDisplayRefreshRates.Current))
        {
            ComboBoxItem comboBoxItem = new ComboBoxItem
            {
                Content = $"{item2} FPS",
                Tag = item2
            };
            frameRateLimitBox.Items.Add(comboBoxItem);
            if (item2 == num2)
            {
                selectedItem = comboBoxItem;
            }
        }

        frameRateLimitBox.SelectedItem = selectedItem;
    }
    finally
    {
        suppressEditorViewControls = false;
    }

    settingsSnapBox.SelectionChanged += SettingsSnapBox_SelectionChanged;
    settingsSnapBox.PropertyChanged += (object? _, AvaloniaPropertyChangedEventArgs eventArgs) =>
    {
        if (!(eventArgs.Property != ComboBox.TextProperty) && !suppressEditorViewControls)
        {
            settingsSnapCommitTimer.Stop();
            settingsSnapCommitTimer.Start();
        }
    };
    settingsSnapBox.LostFocus += (object? _, FocusChangedEventArgs _) =>
    {
        CommitSettingsSnapBoxText();
    };
    settingsSnapBox.KeyDown += (object? _, KeyEventArgs eventArgs) =>
    {
        if (eventArgs.Key == Key.Return)
        {
            CommitSettingsSnapBoxText();
            eventArgs.Handled = true;
        }
    };
    frameRateLimitBox.SelectionChanged += FrameRateLimitBox_SelectionChanged;
    AutomationProperties.SetAutomationId(settingsSnapBox, "Settings.Snap");
    AutomationProperties.SetName(settingsSnapBox, "网格分度");
    AutomationProperties.SetHelpText(settingsSnapBox, "可输入 1/任意整数，范围 1–1920");
    settingsFixedSnapBox.Items.Clear();
    array = new int[13]
    {
        1,
        2,
        3,
        4,
        5,
        6,
        7,
        8,
        9,
        10,
        11,
        12,
        16
    };
    foreach (int num3 in array)
    {
        settingsFixedSnapBox.Items.Add(new ComboBoxItem { Content = GridLabel(num3), Tag = num3 });
    }

    settingsFixedSnapBox.IsEditable = true;
    settingsFixedSnapBox.IsTextSearchEnabled = false;
    settingsFixedSnapBox.HorizontalAlignment = HorizontalAlignment.Stretch;
    SyncSettingsFixedSnapBox(editorViewSettings.FixedSnapDivision);
    settingsFixedSnapBox.SelectionChanged += SettingsFixedSnapBox_SelectionChanged;
    settingsFixedSnapBox.PropertyChanged += (object? _, AvaloniaPropertyChangedEventArgs eventArgs) =>
    {
        if (!(eventArgs.Property != ComboBox.TextProperty) && !suppressEditorViewControls)
        {
            settingsFixedSnapCommitTimer.Stop();
            settingsFixedSnapCommitTimer.Start();
        }
    };
    settingsFixedSnapBox.LostFocus += (object? _, FocusChangedEventArgs _) =>
    {
        CommitSettingsFixedSnapBoxText();
    };
    settingsFixedSnapBox.KeyDown += (object? _, KeyEventArgs eventArgs) =>
    {
        if (eventArgs.Key == Key.Return)
        {
            CommitSettingsFixedSnapBoxText();
            eventArgs.Handled = true;
        }
    };
    AutomationProperties.SetAutomationId(settingsFixedSnapBox, "Settings.FixedSnap");
    AutomationProperties.SetName(settingsFixedSnapBox, "固定分度");
    AutomationProperties.SetHelpText(settingsFixedSnapBox, "可输入 1/任意整数，范围 1–1920；快捷键一键切换到此分度");
    AutomationProperties.SetAutomationId(frameRateLimitBox, "FrameRateLimitBox");
    AutomationProperties.SetName(frameRateLimitBox, "帧率上限");
    AutomationProperties.SetHelpText(frameRateLimitBox, $"FrameRateLimit={editorViewSettings.FrameRateLimit}; Presets={string.Join(',', WindowsFrameRateOptions.Presets)}");
    Slider[] array2 = new Slider[13]
    {
        gridLaneWidthSlider,
        gridBeatHeightSlider,
        mainEditorHorizontalOffsetSlider,
        noteWidthSlider,
        noteHeightSlider,
        noteOpacitySlider,
        trackOpacitySlider,
        backgroundDarknessSlider,
        barLineThicknessSlider,
        subdivisionLineThicknessSlider,
        judgementLinePositionSlider,
        judgementLineThicknessSlider,
        judgementLineOpacitySlider
    };
    for (int i = 0; i < array2.Length; i++)
    {
        array2[i].ValueChanged += EditorViewSettingsSlider_ValueChanged;
    }

    TextBlock[] array3 = new TextBlock[15]
    {
        gridLaneWidthValueText,
        gridBeatHeightValueText,
        mainEditorHorizontalOffsetValueText,
        noteWidthValueText,
        noteHeightValueText,
        noteOpacityValueText,
        trackOpacityValueText,
        backgroundDarknessValueText,
        spectrogramStrengthValueText,
        spectrogramDetailValueText,
        barLineThicknessValueText,
        subdivisionLineThicknessValueText,
        judgementLinePositionValueText,
        judgementLineThicknessValueText,
        judgementLineOpacityValueText
    };
    foreach (TextBlock obj in array3)
    {
        obj.Foreground = MutedBrush;
        obj.FontSize = 10.0;
        obj.Margin = new Thickness(0.0, 2.0, 0.0, 3.0);
    }

    resetEditorViewButton.Content = "恢复默认视图比例";
    resetEditorViewButton.HorizontalAlignment = HorizontalAlignment.Stretch;
    resetEditorViewButton.Margin = new Thickness(0.0, 7.0, 0.0, 0.0);
    ApplyWpfToolButtonStyle(resetEditorViewButton);
    AutomationProperties.SetAutomationId(resetEditorViewButton, "Settings.ResetEditorView");
    resetEditorViewButton.Click += (object? _, RoutedEventArgs _) =>
    {
        ResetEditorViewSettings();
    };
    RefreshEditorViewSettingsUi();
    Grid grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,*"),
        RowDefinitions = new RowDefinitions("Auto,Auto"),
        ColumnSpacing = 6.0,
        RowSpacing = 6.0
    };
    grid.Children.Add(anchorLineButton);
    grid.Children.Add(anchorLineAboveButton);
    grid.Children.Add(anchorCellButton);
    grid.Children.Add(anchorCellBottomButton);
    Grid.SetColumn(anchorLineAboveButton, 1);
    Grid.SetRow(anchorCellButton, 1);
    Grid.SetRow(anchorCellBottomButton, 1);
    Grid.SetColumn(anchorCellBottomButton, 1);
    Grid grid2 = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,12,*")
    };
    grid2.Children.Add(new StackPanel { Children = { (Control)TimingField("Note 宽度", noteWidthSlider), (Control)noteWidthValueText } });
    StackPanel stackPanel = new StackPanel
    {
        Children =
        {
            (Control)TimingField("Note 高度", noteHeightSlider),
            (Control)noteHeightValueText
        }
    };
    Grid.SetColumn(stackPanel, 2);
    grid2.Children.Add(stackPanel);
    Grid grid3 = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,12,*")
    };
    grid3.Children.Add(new StackPanel { Children = { (Control)TimingField("判定线粗细", judgementLineThicknessSlider), (Control)judgementLineThicknessValueText } });
    StackPanel stackPanel2 = new StackPanel
    {
        Children =
        {
            (Control)TimingField("判定线透明度", judgementLineOpacitySlider),
            (Control)judgementLineOpacityValueText
        }
    };
    Grid.SetColumn(stackPanel2, 2);
    grid3.Children.Add(stackPanel2);
    Grid item = BuildSpectrogramAppearanceGrid(spectrogramStrengthSlider, spectrogramStrengthValueText, spectrogramDetailSlider, spectrogramDetailValueText);
    StackPanel stackPanel3 = new StackPanel
    {
        Spacing = 10.0,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Children =
        {
            BuildDisplayScaleSettings(),
            (Control)TimingField("Note 对齐", grid),
            (Control)TimingField("栅格宽度（每列）", gridLaneWidthSlider),
            (Control)gridLaneWidthValueText,
            (Control)TimingField("主编辑区域 X 轴偏移", mainEditorHorizontalOffsetSlider),
            (Control)mainEditorHorizontalOffsetValueText,
            (Control)TimingField("缩放（DIP / beat，Alt + 滚轮）", gridBeatHeightSlider),
            (Control)gridBeatHeightValueText,
            BuildNoteStyleSelector(),
            (Control)grid2,
            (Control)TimingField("Note 透明度", noteOpacitySlider),
            (Control)noteOpacityValueText,
            (Control)TimingField("轨道区域透明度", trackOpacitySlider),
            (Control)trackOpacityValueText,
            (Control)TimingField("整体背景暗度", backgroundDarknessSlider),
            (Control)backgroundDarknessValueText,
            (Control)item,
            (Control)TimingField("小节线粗细", barLineThicknessSlider),
            (Control)barLineThicknessValueText,
            (Control)TimingField("分度线粗细（保留层级）", subdivisionLineThicknessSlider),
            (Control)subdivisionLineThicknessValueText,
            (Control)TimingField("判定线位置（画面高度）", judgementLinePositionSlider),
            (Control)judgementLinePositionValueText,
            (Control)grid3,
            BuildRenderBackendSelector()
        }
    };
    AutomationProperties.SetAutomationId(stackPanel3, "Settings.Appearance");
    settingsAppearanceContent = new StackPanel
    {
        Spacing = 10.0,
        Children =
        {
            (Control)stackPanel3,
            (Control)TimingField("画布列数", proGridColumnSlider),
            (Control)proGridColumnText
        }
    };
    return SettingsSection("编辑", "吸附与视图", "Settings.EditorViewSection", TimingField("快捷键目标网格", settingsFixedSnapBox), BuildSnapAllOption());
}

private void ConfigureNoteAnchorButton(Button button, string content, string toolTip, WindowsNoteAnchorMode anchor, string automationId)
{
    button.Content = content;
    button.Tag = anchor;
    button.HorizontalAlignment = HorizontalAlignment.Stretch;
    button.MinHeight = 34.0;
    ToolTip.SetTip(button, toolTip);
    AutomationProperties.SetAutomationId(button, automationId);
    AutomationProperties.SetName(button, content);
    ApplyWpfToolButtonStyle(button);
    button.Click += NoteAnchorButton_Click;
}

private void NoteAnchorButton_Click(object? sender, RoutedEventArgs eventArgs)
{
    if (sender is Button button && button.Tag is WindowsNoteAnchorMode windowsNoteAnchorMode)
    {
        editorViewSettings = editorViewSettings.WithNoteAnchor(windowsNoteAnchorMode);
        ApplyEditorViewSettings();
        RefreshEditorViewSettingsUi();
        MarkEditorViewSettingsDirty();
        RefreshOptionResets();
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("Note 对齐：{0}", NoteAnchorDisplayName(windowsNoteAnchorMode)));
    }
}

private void RefreshNoteAnchorButtons()
{
    Button[] array = new Button[4]
    {
        anchorLineButton,
        anchorLineAboveButton,
        anchorCellButton,
        anchorCellBottomButton
    };
    foreach (Button button in array)
    {
        ApplyWpfToolButtonStyle(button);
        bool flag = button.Tag is WindowsNoteAnchorMode windowsNoteAnchorMode && windowsNoteAnchorMode == editorViewSettings.NoteAnchor;
        if (flag)
        {
            button.Background = new SolidColorBrush(Color.Parse("#38676C"));
            button.BorderBrush = new SolidColorBrush(Color.Parse("#4A777B"));
            button.Foreground = Brushes.White;
            button.FontWeight = FontWeight.DemiBold;
        }
        else
        {
            button.FontWeight = FontWeight.Normal;
        }

        AutomationProperties.SetHelpText(button, $"NoteAnchor={button.Tag}; Active={flag}");
    }
}

private static string NoteAnchorDisplayName(WindowsNoteAnchorMode anchor)
{
    return TimelinePlacementGeometry.DisplayName(anchor);
}

private void SettingsSnapBox_SelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
{
    if (!suppressEditorViewControls && settingsSnapBox.SelectedItem is ComboBoxItem comboBoxItem && comboBoxItem.Tag is int division)
    {
        SetQuickSnapDivision(division);
    }
}

private void CommitSettingsSnapBoxText(bool restoreInvalidText = true, bool addCustomDivisionItem = true)
{
    settingsSnapCommitTimer.Stop();
    if (!suppressEditorViewControls && WindowsInspectorParityPolicy.AllowWindowUiMutation(windowCloseTeardownStarted, windowClosePreflightComplete))
    {
        if (TryParseSnapDivisionText(settingsSnapBox.Text, out var division))
        {
            SetQuickSnapDivision(division, addCustomDivisionItem);
        }
        else if (restoreInvalidText)
        {
            SyncSettingsSnapBox(viewModel.ActiveTab?.Session.SnapDivision ?? editorViewSettings.SnapDivision);
            viewModel.SetStatus((gridNotation == EditorGridNotation.General) ? "General 网格分母：4–7680，须为 4 的倍数。" : "osu 网格分母：1–1920。");
        }
    }
}

private bool TryParseSnapDivisionText(string? text, out int division)
{
    division = 0;
    if (string.IsNullOrWhiteSpace(text))
    {
        return false;
    }

    string text2 = text.Trim();
    int num = text2.LastIndexOf('/');
    if (num >= 0)
    {
        text2 = text2.Substring(num + 1).Trim();
    }

    if (!int.TryParse(text2, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
    {
        return false;
    }

    try
    {
        division = EditorGridNotationPolicy.FromDisplayedDenominator(result, gridNotation);
    }
    catch (ArgumentOutOfRangeException)
    {
        return false;
    }

    int num2 = division;
    if (num2 >= 1)
    {
        return num2 <= 1920;
    }

    return false;
}

private void SyncSettingsSnapBox(int division, bool addCustomDivisionItem = true)
{
    division = Math.Clamp(division, 1, 1920);
    bool flag = suppressEditorViewControls;
    suppressEditorViewControls = true;
    try
    {
        ComboBoxItem comboBoxItem = settingsSnapBox.Items.OfType<ComboBoxItem>().FirstOrDefault((ComboBoxItem candidate) => candidate.Tag is int num && num == division);
        if ((comboBoxItem == null) & addCustomDivisionItem)
        {
            comboBoxItem = new ComboBoxItem
            {
                Content = GridLabel(division),
                Tag = division
            };
            settingsSnapBox.Items.Add(comboBoxItem);
        }

        settingsSnapBox.SelectedItem = comboBoxItem;
        settingsSnapBox.Text = GridLabel(division);
        AutomationProperties.SetHelpText(settingsSnapBox, $"SnapDivision={division}; Range=1-1920; Editable=True");
    }
    finally
    {
        suppressEditorViewControls = flag;
    }
}

private void SettingsFixedSnapBox_SelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
{
    if (!suppressEditorViewControls && settingsFixedSnapBox.SelectedItem is ComboBoxItem comboBoxItem && comboBoxItem.Tag is int division)
    {
        SetFixedSnapDivision(division);
    }
}

private void CommitSettingsFixedSnapBoxText(bool restoreInvalidText = true, bool addCustomDivisionItem = true)
{
    settingsFixedSnapCommitTimer.Stop();
    if (!suppressEditorViewControls && WindowsInspectorParityPolicy.AllowWindowUiMutation(windowCloseTeardownStarted, windowClosePreflightComplete))
    {
        if (TryParseSnapDivisionText(settingsFixedSnapBox.Text, out var division))
        {
            SetFixedSnapDivision(division, addCustomDivisionItem);
        }
        else if (restoreInvalidText)
        {
            SyncSettingsFixedSnapBox(editorViewSettings.FixedSnapDivision);
            viewModel.SetStatus((gridNotation == EditorGridNotation.General) ? "General 网格分母：4–7680，须为 4 的倍数。" : "osu 网格分母：1–1920。");
        }
    }
}

private void SetFixedSnapDivision(int division, bool addCustomDivisionItem = true)
{
    division = Math.Clamp(division, 1, 1920);
    editorViewSettings = editorViewSettings.WithFixedSnapDivision(division);
    MarkEditorViewSettingsDirty();
    SyncSettingsFixedSnapBox(division, addCustomDivisionItem);
    viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("固定分度设为 1/{0}（{1} 一键切换）", division, CompactShortcut("SnapSetFixed")));
}

private void SyncSettingsFixedSnapBox(int division, bool addCustomDivisionItem = true)
{
    division = Math.Clamp(division, 1, 1920);
    bool flag = suppressEditorViewControls;
    suppressEditorViewControls = true;
    try
    {
        ComboBoxItem comboBoxItem = settingsFixedSnapBox.Items.OfType<ComboBoxItem>().FirstOrDefault((ComboBoxItem candidate) => candidate.Tag is int num && num == division);
        if ((comboBoxItem == null) & addCustomDivisionItem)
        {
            comboBoxItem = new ComboBoxItem
            {
                Content = GridLabel(division),
                Tag = division
            };
            settingsFixedSnapBox.Items.Add(comboBoxItem);
        }

        settingsFixedSnapBox.SelectedItem = comboBoxItem;
        settingsFixedSnapBox.Text = GridLabel(division);
        AutomationProperties.SetHelpText(settingsFixedSnapBox, $"FixedSnapDivision={division}; Range=1-1920; Editable=True");
    }
    finally
    {
        suppressEditorViewControls = flag;
    }
}

private Control BuildBackupCacheSettings()
{
    autoBackupEnabledBox.Content = "启用自动备份";
    autoBackupEnabledBox.IsChecked = editorViewSettings.AutoBackupEnabled;
    autoBackupEnabledBox.Margin = new Thickness(2.0, 5.0, 0.0, 3.0);
    AutomationProperties.SetAutomationId(autoBackupEnabledBox, "Settings.AutoBackupEnabled");
    autoBackupEnabledBox.IsCheckedChanged += AutoBackupSettings_Changed;
    ConfigureSettingsSlider(autoBackupIntervalSlider, 1.0, 120.0, 1.0, editorViewSettings.AutoBackupMinutes, "Settings.AutoBackupMinutes", "自动备份频率");
    autoBackupIntervalSlider.ValueChanged += AutoBackupIntervalSlider_ValueChanged;
    autoBackupIntervalText.Foreground = MutedBrush;
    autoBackupIntervalText.FontSize = 10.0;
    autoBackupIntervalText.TextWrapping = TextWrapping.Wrap;
    backupFolderText.Foreground = MutedBrush;
    backupFolderText.FontSize = 9.0;
    backupFolderText.TextWrapping = TextWrapping.Wrap;
    backupFolderText.Text = AvaloniaAppPaths.EditorBackupRoot;
    ToolTip.SetTip(backupFolderText, AvaloniaAppPaths.EditorBackupRoot);
    AutomationProperties.SetAutomationId(backupFolderText, "Settings.BackupFolder");
    Button button = new Button
    {
        Content = "立即备份"
    };
    ApplyPrimaryButtonStyle(button);
    button.Click += async (object? _, RoutedEventArgs _) =>
    {
        await BackupCurrentTabAsync();
    };
    AutomationProperties.SetAutomationId(button, "Settings.BackupNow");
    Button button2 = new Button
    {
        Content = "恢复备份"
    };
    button2.Click += async (object? _, RoutedEventArgs _) =>
    {
        await RestoreBackupAsync();
    };
    AutomationProperties.SetAutomationId(button2, "Settings.RestoreBackup");
    Button button3 = new Button
    {
        Content = "打开目录"
    };
    button3.Click += (object? _, RoutedEventArgs _) =>
    {
        OpenBackupFolder();
    };
    AutomationProperties.SetAutomationId(button3, "Settings.OpenBackupFolder");
    bool flag = (button3.IsVisible = OperatingSystem.IsWindows());
    Grid grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions(flag ? "*,6,*,6,*" : "*,6,*,0,0")
    };
    grid.Children.Add(button);
    Grid.SetColumn(button2, 2);
    grid.Children.Add(button2);
    Grid.SetColumn(button3, 4);
    grid.Children.Add(button3);
    cacheBackupFilesBox.Content = "自动备份文件";
    cacheEditingFilesBox.Content = "未打开的编辑缓存";
    cacheResourceFilesBox.Content = "联机资源缓存";
    CheckBox[] array = new CheckBox[3]
    {
        cacheBackupFilesBox,
        cacheEditingFilesBox,
        cacheResourceFilesBox
    };
    for (int num = 0; num < array.Length; num++)
    {
        array[num].Margin = new Thickness(2.0, 3.0, 0.0, 2.0);
    }

    ToolTip.SetTip(cacheEditingFilesBox, "当前已打开的谱面工作目录始终会被保护并跳过");
    ToolTip.SetTip(cacheResourceFilesBox, "活动联机房间存在时会拒绝清理该缓存");
    AutomationProperties.SetAutomationId(cacheBackupFilesBox, "Settings.Cache.Backups");
    AutomationProperties.SetAutomationId(cacheEditingFilesBox, "Settings.Cache.UnopenedEditing");
    AutomationProperties.SetAutomationId(cacheResourceFilesBox, "Settings.Cache.OnlineResources");
    cleanSelectedCacheButton.Content = "清理勾选项目";
    cleanSelectedCacheButton.HorizontalAlignment = HorizontalAlignment.Stretch;
    cleanSelectedCacheButton.Click += async (object? _, RoutedEventArgs _) =>
    {
        await CleanSelectedCacheAsync();
    };
    AutomationProperties.SetAutomationId(cleanSelectedCacheButton, "Settings.Cache.CleanSelected");
    backupCacheStatusText.Text = "请选择需要清理的类别";
    backupCacheStatusText.Foreground = MutedBrush;
    backupCacheStatusText.FontSize = 10.0;
    backupCacheStatusText.TextWrapping = TextWrapping.Wrap;
    AutomationProperties.SetAutomationId(backupCacheStatusText, "Settings.BackupCacheStatus");
    RefreshAutoBackupSettingsUi();
    return SettingsSection("备份与恢复", string.Empty, "Settings.BackupCacheSection", autoBackupEnabledBox, TimingField("自动备份频率", autoBackupIntervalSlider), autoBackupIntervalText, grid, backupFolderText, new Border { Height = 1.0, Background = StrokeBrush, Margin = new Thickness(0.0, 9.0) }, new TextBlock { Text = "缓存管理", Foreground = Brushes.White, FontSize = 17.0, FontWeight = FontWeight.DemiBold, Margin = new Thickness(0.0, 8.0, 0.0, 4.0) }, cacheBackupFilesBox, cacheEditingFilesBox, cacheResourceFilesBox, cleanSelectedCacheButton, backupCacheStatusText);
}

private Control BuildShortcutSettings()
{
    shortcutButtons.Clear();
    shortcutListPanel.Children.Clear();
    Button button = new Button
    {
        Content = "恢复默认",
        MinWidth = 102.0,
        Padding = new Thickness(12.0, 7.0),
        Background = PanelBrush,
        Foreground = Brushes.White,
        BorderBrush = StrokeBrush,
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(4.0)
    };
    AutomationProperties.SetAutomationId(button, "Settings.Shortcuts.Reset");
    shortcutResetAllButton = button;
    button.Click += (object? _, RoutedEventArgs _) =>
    {
        ResetShortcutSettings();
    };
    Grid grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto")
    };
    grid.Children.Add(new StackPanel { Children = { (Control)new TextBlock { Text = "点击按键框后录入组合键", Foreground = MutedBrush, FontSize = 10.0 } } });
    Grid.SetColumn(button, 1);
    grid.Children.Add(button);
    foreach (WindowsEditorShortcutDescriptor item in WindowsEditorShortcutSettings.Catalog)
    {
        string action = item.Id;
        Grid grid2 = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,122")
        };
        grid2.Children.Add(new TextBlock { Text = item.Label, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        Button button2 = new Button
        {
            MinWidth = 116.0,
            Padding = new Thickness(8.0, 6.0),
            Background = PanelBrush,
            Foreground = Brushes.White,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1.0),
            CornerRadius = new CornerRadius(4.0),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        ToolTip.SetTip(button2, "点击后按下新快捷键；Backspace 清除；Escape 取消");
        AutomationProperties.SetAutomationId(button2, "Shortcut_" + action);
        AutomationProperties.SetName(button2, AvaloniaLocalizationCatalog.Format("{0}快捷键", item.Label));
        button2.Click += (object? _, RoutedEventArgs _) =>
        {
            BeginShortcutCapture(action);
        };
        Grid.SetColumn(button2, 1);
        grid2.Children.Add(button2);
        shortcutListPanel.Children.Add(grid2);
        shortcutButtons[action] = button2;
    }

    shortcutStatusText.Text = "Backspace 清除绑定 · Escape 取消录入";
    shortcutStatusText.Foreground = MutedBrush;
    shortcutStatusText.FontSize = 10.0;
    shortcutStatusText.TextWrapping = TextWrapping.Wrap;
    AutomationProperties.SetAutomationId(shortcutStatusText, "Settings.Shortcuts.Status");
    RefreshShortcutButtons();
    RefreshShortcutLabels();
    StackPanel stackPanel = new StackPanel
    {
        Spacing = 10.0,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Children =
        {
            (Control)grid,
            BuildInteractionProfileSelector(),
            (Control)shortcutListPanel,
            (Control)shortcutStatusText
        }
    };
    AutomationProperties.SetAutomationId(stackPanel, "Settings.ShortcutsSection");
    return stackPanel;
}

private void BeginShortcutCapture(string action)
{
    CancelShortcutCapture(updateStatus: false);
    capturingShortcutAction = action;
    shortcutStatusText.Text = AvaloniaLocalizationCatalog.Format("正在录入“{0}” · 直接按组合键，Backspace 清除，Escape 取消", ShortcutLabel(action));
    RefreshShortcutButtons();
    if (shortcutButtons.TryGetValue(action, out Button value))
    {
        value.Focus();
    }
}

private void CancelShortcutCapture(bool updateStatus = true)
{
    capturingShortcutAction = null;
    if (updateStatus)
    {
        shortcutStatusText.Text = "已取消快捷键录入";
    }

    RefreshShortcutButtons();
}

private void ResetShortcutSettings()
{
    CancelShortcutCapture(updateStatus: false);
    shortcutSettings.Reset();
    shortcutStatusText.Text = "快捷键已恢复当前操作方案的默认值";
    MarkShortcutSettingsDirty();
    RefreshShortcutButtons();
    RefreshShortcutLabels();
}

private void RefreshShortcutButtons()
{
    RefreshOptionResets();
    foreach (WindowsEditorShortcutDescriptor item in WindowsEditorShortcutSettings.Catalog)
    {
        if (shortcutButtons.TryGetValue(item.Id, out Button value))
        {
            bool flag = item.Id.Equals(capturingShortcutAction, StringComparison.OrdinalIgnoreCase);
            string text = shortcutSettings.Bindings.GetValueOrDefault(item.Id) ?? string.Empty;
            Button button = value;
            string content;
            if (flag)
            {
                content = "请按键…";
            }
            else
            {
                content = (string.IsNullOrWhiteSpace(text) ? "未设置" : text);
            }

            button.Content = content;
            value.BorderBrush = (flag ? AccentBrush : StrokeBrush);
            Button element = value;
            string value2;
            if (flag)
            {
                value2 = "正在录入快捷键";
            }
            else
            {
                value2 = (string.IsNullOrWhiteSpace(text) ? "未设置" : text);
            }

            AutomationProperties.SetHelpText(element, value2);
        }
    }
}

private void RefreshShortcutLabels()
{
    RefreshFixedTools();
    foreach (KeyValuePair<string, (Button, string)> quickShortcutButton in quickShortcutButtons)
    {
        ToolTip.SetTip(quickShortcutButton.Value.Item1, quickShortcutButton.Value.Item2 + "  " + CompactShortcut(quickShortcutButton.Key));
    }

    if (toolbarPlaytestButton != null)
    {
        ToolTip.SetTip(toolbarPlaytestButton, "官方 osu!mania 试玩  " + CompactShortcut("Playtest") + " · 设置与导入位于右侧设置栏");
    }

    ToolTip.SetTip(audioPlayPauseButton, AvaloniaLocalizationCatalog.Format("播放 / 暂停  {0}", CompactShortcut("PlayPause")));
    if (commandPlayButton != null)
    {
        ToolTip.SetTip(commandPlayButton, AvaloniaLocalizationCatalog.Format("播放 / 暂停  {0}", CompactShortcut("PlayPause")));
    }

    if (commandFullscreenButton != null)
    {
        ToolTip.SetTip(commandFullscreenButton, SupportsDesktopWindowSizing ? AvaloniaLocalizationCatalog.Format("全屏窗口化（覆盖任务栏）/ 退出  {0}", CompactShortcut("ToggleFullscreen")) : AvaloniaLocalizationCatalog.Format("隐藏 / 显示顶部标签  {0}", CompactShortcut("ToggleFullscreen")));
    }

    if (mixedCopyButton != null)
    {
        ToolTip.SetTip(mixedCopyButton, AvaloniaLocalizationCatalog.Format("复制选中的 Note / Effect  {0}", CompactShortcut("Copy")));
    }

    if (mixedPasteButton != null)
    {
        ToolTip.SetTip(mixedPasteButton, AvaloniaLocalizationCatalog.Format("移动虚影后单击粘贴  {0}", CompactShortcut("Paste")));
    }

    if (mixedDeleteButton != null)
    {
        ToolTip.SetTip(mixedDeleteButton, AvaloniaLocalizationCatalog.Format("删除选中的 Note / Effect  {0}", CompactShortcut("Delete")));
    }

    if (timelineToolButtons.TryGetValue(TimelineTool.Select, out Button value))
    {
        ToolTip.SetTip(value, AvaloniaLocalizationCatalog.Format("选择工具  {0}", CompactShortcut("ToolSelect")));
    }

    if (timelineToolButtons.TryGetValue(TimelineTool.Tap, out Button value2))
    {
        ToolTip.SetTip(value2, AvaloniaLocalizationCatalog.Format("单键工具  {0}", CompactShortcut("ToolTap")));
    }

    if (timelineToolButtons.TryGetValue(TimelineTool.Hold, out Button value3))
    {
        ToolTip.SetTip(value3, AvaloniaLocalizationCatalog.Format("长键工具  {0}", CompactShortcut("ToolHold")));
    }

    if (timelineToolButtons.TryGetValue(TimelineTool.Erase, out Button value4))
    {
        ToolTip.SetTip(value4, AvaloniaLocalizationCatalog.Format("擦除工具  {0}", CompactShortcut("ToolErase")));
    }

    ToolTip.SetTip(proEditorButton, AvaloniaLocalizationCatalog.Format("切换 ProEditor  {0}", CompactShortcut("TogglePro")));
}

private string CompactShortcut(string action)
{
    string text = shortcutSettings.Bindings.GetValueOrDefault(action) ?? string.Empty;
    if (!string.IsNullOrWhiteSpace(text))
    {
        return text.Replace("Ctrl+", "C+", StringComparison.Ordinal).Replace("Shift+", "S+", StringComparison.Ordinal).Replace("Alt+", "A+", StringComparison.Ordinal);
    }

    return "—";
}

private static string ShortcutLabel(string action)
{
    return WindowsEditorShortcutSettings.Catalog.FirstOrDefault((WindowsEditorShortcutDescriptor item) => item.Id.Equals(action, StringComparison.OrdinalIgnoreCase))?.Label ?? action;
}

private void MarkShortcutSettingsDirty()
{
    shortcutSettingsDirty = true;
    shortcutSettingsSaveTimer.Stop();
    shortcutSettingsSaveTimer.Start();
}

private void PersistPendingShortcutSettings(bool force = false)
{
    shortcutSettingsSaveTimer.Stop();
    if (!shortcutSettingsDirty && !force)
    {
        return;
    }

    try
    {
        WindowsEditorShortcutSettingsStore.Save(shortcutSettings);
        shortcutSettingsDirty = false;
    }
    catch (Exception ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("快捷键保存失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private void ConfigureSpectrogramAppearanceSliders(Slider strength, Slider detail, string automationPrefix)
{
    ConfigureSettingsSlider(strength, 20.0, 100.0, 5.0, audioPanelSettings.BackgroundSpectrogramStrength * 100.0, automationPrefix + ".SpectrogramStrength", "背景频谱强度");
    ConfigureSettingsSlider(detail, 0.0, 100.0, 5.0, audioPanelSettings.SpectrogramDetail * 100.0, automationPrefix + ".SpectrogramDetail", "频谱细节");
    ToolTip.SetTip(strength, "背景频谱有多醒目；越高越亮，并更多压暗轨道里的背景图");
    ToolTip.SetTip(detail, "越高保留越多弱信号与泛音；越低只留鼓点和起音");
    strength.ValueChanged += (object? _, RangeBaseValueChangedEventArgs eventArgs) =>
    {
        if (!suppressSpectrogramAppearanceControls)
        {
            ApplySpectrogramAppearance(eventArgs.NewValue / 100.0, audioPanelSettings.SpectrogramDetail);
        }
    };
    detail.ValueChanged += (object? _, RangeBaseValueChangedEventArgs eventArgs) =>
    {
        if (!suppressSpectrogramAppearanceControls)
        {
            ApplySpectrogramAppearance(audioPanelSettings.BackgroundSpectrogramStrength, eventArgs.NewValue / 100.0);
        }
    };
}

private static Grid BuildSpectrogramAppearanceGrid(Slider strength, TextBlock strengthValue, Slider detail, TextBlock detailValue)
{
    Grid grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,12,*"),
        Children =
        {
            (Control)new StackPanel
            {
                Children =
                {
                    (Control)TimingField("背景频谱强度", strength),
                    (Control)strengthValue
                }
            }
        }
    };
    StackPanel stackPanel = new StackPanel
    {
        Children =
        {
            (Control)TimingField("频谱细节", detail),
            (Control)detailValue
        }
    };
    Grid.SetColumn(stackPanel, 2);
    grid.Children.Add(stackPanel);
    return grid;
}

private Flyout BuildSpectrogramAppearanceFlyout()
{
    ConfigureSpectrogramAppearanceSliders(spectrogramFlyoutStrengthSlider, spectrogramFlyoutDetailSlider, "Audio.SpectrogramFlyout");
    TextBlock[] array = new TextBlock[2]
    {
        spectrogramFlyoutStrengthValueText,
        spectrogramFlyoutDetailValueText
    };
    foreach (TextBlock obj in array)
    {
        obj.Foreground = MutedBrush;
        obj.FontSize = 10.0;
        obj.Margin = new Thickness(0.0, 2.0, 0.0, 3.0);
    }

    RefreshSpectrogramAppearanceUi();
    return new Flyout
    {
        Content = new StackPanel
        {
            Width = 240.0,
            Spacing = 4.0,
            Children =
            {
                (Control)new TextBlock
                {
                    Text = "频谱显示",
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.DemiBold,
                    Margin = new Thickness(0.0, 0.0, 0.0, 4.0)
                },
                (Control)TimingField("背景频谱强度", spectrogramFlyoutStrengthSlider),
                (Control)spectrogramFlyoutStrengthValueText,
                (Control)TimingField("频谱细节", spectrogramFlyoutDetailSlider),
                (Control)spectrogramFlyoutDetailValueText
            }
        }
    };
}

private void ApplySpectrogramAppearance(double strength, double detail)
{
    audioPanelSettings = audioPanelSettings.WithSpectrogramAppearance(strength, detail);
    audioPanelSettingsDirty = true;
    ScheduleAudioPanelSettingsSave();
    timeline.SetSpectrogramAppearance(audioPanelSettings.BackgroundSpectrogramStrength, audioPanelSettings.SpectrogramDetail);
    spectrogramSideSurface.SetSpectrogramDetail(audioPanelSettings.SpectrogramDetail);
    RefreshSpectrogramAppearanceUi();
}

private void RefreshSpectrogramAppearanceUi()
{
    double backgroundSpectrogramStrength = audioPanelSettings.BackgroundSpectrogramStrength;
    double spectrogramDetail = audioPanelSettings.SpectrogramDetail;
    bool flag = suppressSpectrogramAppearanceControls;
    suppressSpectrogramAppearanceControls = true;
    try
    {
        Slider[] array = new Slider[2]
        {
            spectrogramStrengthSlider,
            spectrogramFlyoutStrengthSlider
        };
        foreach (Slider slider in array)
        {
            if (Math.Abs(slider.Value - backgroundSpectrogramStrength * 100.0) > 0.01)
            {
                slider.Value = backgroundSpectrogramStrength * 100.0;
            }
        }

        array = new Slider[2]
        {
            spectrogramDetailSlider,
            spectrogramFlyoutDetailSlider
        };
        foreach (Slider slider2 in array)
        {
            if (Math.Abs(slider2.Value - spectrogramDetail * 100.0) > 0.01)
            {
                slider2.Value = spectrogramDetail * 100.0;
            }
        }
    }
    finally
    {
        suppressSpectrogramAppearanceControls = flag;
    }

    string text = backgroundSpectrogramStrength.ToString("P0", CultureInfo.CurrentCulture);
    CultureInfo currentCulture = CultureInfo.CurrentCulture;
    object arg = spectrogramDetail;
    string arg2;
    if (spectrogramDetail < 0.34)
    {
        arg2 = AvaloniaLocalizationCatalog.Text("突出起音");
    }
    else
    {
        arg2 = ((spectrogramDetail < 0.67) ? AvaloniaLocalizationCatalog.Text("均衡") : AvaloniaLocalizationCatalog.Text("完整细节"));
    }

    string text2 = string.Format(currentCulture, "{0:P0} · {1}", arg, arg2);
    spectrogramStrengthValueText.Text = text;
    spectrogramFlyoutStrengthValueText.Text = text;
    spectrogramDetailValueText.Text = text2;
    spectrogramFlyoutDetailValueText.Text = text2;
}

private static void ConfigureSettingsSlider(Slider slider, double minimum, double maximum, double tick, double value, string automationId, string name)
{
    slider.Minimum = minimum;
    slider.Maximum = maximum;
    slider.TickFrequency = tick;
    slider.IsSnapToTickEnabled = true;
    slider.SmallChange = tick;
    slider.LargeChange = Math.Max(tick, (maximum - minimum) / 10.0);
    slider.Value = Math.Clamp(value, minimum, maximum);
    ConstrainInspectorSlider(slider);
    AutomationProperties.SetAutomationId(slider, automationId);
    AutomationProperties.SetName(slider, name);
}

private static void ConstrainInspectorSlider(Slider slider)
{
    slider.MinWidth = 0.0;
    slider.MaxWidth = 286.0;
    slider.HorizontalAlignment = HorizontalAlignment.Stretch;
}

private static Border SettingsSection(string title, string subtitle, string automationId, params Control[] controls)
{
    StackPanel stackPanel = new StackPanel
    {
        Spacing = 5.0
    };
    TextBlock textBlock = new TextBlock
    {
        Text = title,
        Foreground = Brushes.White,
        FontSize = 16.0,
        FontWeight = FontWeight.DemiBold
    };
    AutomationProperties.SetAutomationId(textBlock, automationId);
    stackPanel.Children.Add(textBlock);
    if (!string.IsNullOrWhiteSpace(subtitle))
    {
        stackPanel.Children.Add(new TextBlock { Text = subtitle, Foreground = MutedBrush, FontSize = 10.0, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0.0, 0.0, 0.0, 4.0) });
    }

    foreach (Control item in controls)
    {
        stackPanel.Children.Add(item);
    }

    return new Border
    {
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0.0),
        Padding = new Thickness(0.0),
        Child = stackPanel
    };
}

private Control BuildDisplayScaleSettings()
{
    uiScaleSlider.Minimum = 55.0;
    uiScaleSlider.Maximum = 200.0;
    uiScaleSlider.Value = displayScaleSettings.UiScale * 100.0;
    uiScaleSlider.TickFrequency = 5.0;
    uiScaleSlider.IsSnapToTickEnabled = true;
    uiScaleSlider.SmallChange = 5.0;
    uiScaleSlider.LargeChange = 10.0;
    uiScaleSlider.MinWidth = 0.0;
    uiScaleSlider.MaxWidth = 286.0;
    uiScaleSlider.HorizontalAlignment = HorizontalAlignment.Stretch;
    AutomationProperties.SetName(uiScaleSlider, "界面缩放");
    AutomationProperties.SetAutomationId(uiScaleSlider, "Settings.UiScale");
    Grid control = BuildDisplayScaleStepper(uiScaleSlider, uiScaleDecreaseButton, uiScaleIncreaseButton, "界面缩放", "Settings.UiScale");
    fontScaleSlider.Minimum = 80.0;
    fontScaleSlider.Maximum = 160.0;
    fontScaleSlider.Value = displayScaleSettings.FontScale * 100.0;
    fontScaleSlider.TickFrequency = 5.0;
    fontScaleSlider.IsSnapToTickEnabled = true;
    fontScaleSlider.SmallChange = 5.0;
    fontScaleSlider.LargeChange = 10.0;
    fontScaleSlider.MinWidth = 0.0;
    fontScaleSlider.MaxWidth = 286.0;
    fontScaleSlider.HorizontalAlignment = HorizontalAlignment.Stretch;
    AutomationProperties.SetName(fontScaleSlider, "字体缩放");
    AutomationProperties.SetAutomationId(fontScaleSlider, "Settings.FontScale");
    Grid control2 = BuildDisplayScaleStepper(fontScaleSlider, fontScaleDecreaseButton, fontScaleIncreaseButton, "字体缩放", "Settings.FontScale");
    uiScaleValueText.Foreground = MutedBrush;
    uiScaleValueText.Margin = new Thickness(0.0, 4.0, 0.0, 2.0);
    AutomationProperties.SetAutomationId(uiScaleValueText, "Settings.UiScaleValue");
    fontScaleValueText.Foreground = MutedBrush;
    fontScaleValueText.Margin = new Thickness(0.0, 4.0, 0.0, 2.0);
    AutomationProperties.SetAutomationId(fontScaleValueText, "Settings.FontScaleValue");
    displayScaleStatusText.Foreground = MutedBrush;
    displayScaleStatusText.FontSize = 10.0;
    displayScaleStatusText.TextWrapping = TextWrapping.Wrap;
    displayScaleStatusText.Margin = new Thickness(2.0, 6.0, 0.0, 2.0);
    AutomationProperties.SetAutomationId(displayScaleStatusText, "Settings.DisplayScaleStatus");
    uiScaleSlider.ValueChanged += DisplayScaleSlider_ValueChanged;
    fontScaleSlider.ValueChanged += DisplayScaleSlider_ValueChanged;
    Button button = new Button
    {
        Content = "重置浮窗布局",
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
    };
    ApplyWpfToolButtonStyle(button);
    ToolTip.SetTip(button, "将 REWORK、WAVE、SPEC、快捷编辑、调整和联机房间面板重新吸附到谱面轨道外");
    AutomationProperties.SetName(button, "重置浮窗布局");
    AutomationProperties.SetAutomationId(button, "Settings.ResetFloatingLayout");
    button.Click += (object? _, RoutedEventArgs _) =>
    {
        ResetFloatingLayout();
    };
    Border border = new Border();
    border.Background = Brushes.Transparent;
    border.BorderThickness = new Thickness(0.0);
    border.Padding = new Thickness(0.0);
    border.Child = new StackPanel
    {
        Spacing = 7.0,
        Children =
        {
            (Control)new TextBlock
            {
                Text = "显示与缩放",
                Foreground = Brushes.White,
                FontSize = 17.0,
                FontWeight = FontWeight.DemiBold
            },
            BuildAutomaticUiScaleOption(),
            (Control)TimingField("界面缩放", control),
            (Control)uiScaleValueText,
            (Control)TimingField("字体缩放", control2),
            (Control)fontScaleValueText,
            (Control)new TextBlock
            {
                Text = AvaloniaLocalizationCatalog.Format("自动模式按当前屏幕有效分辨率计算：1080p 约 75%，1440p 约 100%；已计入系统 DPI。手动调节界面缩放后退出自动模式。"),
                Foreground = MutedBrush,
                FontSize = 10.0,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2.0, 4.0, 0.0, 8.0)
            },
            (Control)TimingField("帧率上限", frameRateLimitBox),
            (Control)new TextBlock
            {
                Text = AvaloniaLocalizationCatalog.Format("同时作用于谱面预览和测试演奏；高于 165 FPS 时使用独立时间采样。{0}", SupportsDesktopWindowSizing ? AvaloniaLocalizationCatalog.Text("实际画面仍受显示器刷新率与桌面合成限制。") : AvaloniaLocalizationCatalog.Text("实际画面仍受屏幕刷新率与系统合成限制。")),
                Foreground = MutedBrush,
                FontSize = 10.0,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2.0, 4.0, 0.0, 2.0)
            },
            (Control)resetEditorViewButton,
            (Control)button
        }
    };
    return border;
}

private Grid BuildDisplayScaleStepper(Slider slider, Button decreaseButton, Button increaseButton, string label, string automationIdPrefix)
{
    ConfigureDisplayScaleStepButton(decreaseButton, "−", AvaloniaLocalizationCatalog.Format("{0}减少 5%", label), automationIdPrefix + ".Decrease");
    ConfigureDisplayScaleStepButton(increaseButton, "+", AvaloniaLocalizationCatalog.Format("{0}增加 5%", label), automationIdPrefix + ".Increase");
    decreaseButton.Click += (object? _, RoutedEventArgs _) =>
    {
        StepDisplayScaleSlider(slider, -1);
    };
    increaseButton.Click += (object? _, RoutedEventArgs _) =>
    {
        StepDisplayScaleSlider(slider, 1);
    };
    Grid result = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("32,6,*,6,32"),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Children =
        {
            (Control)decreaseButton,
            (Control)slider,
            (Control)increaseButton
        }
    };
    Grid.SetColumn(slider, 2);
    Grid.SetColumn(increaseButton, 4);
    return result;
}

private static void ConfigureDisplayScaleStepButton(Button button, string content, string name, string automationId)
{
    button.Content = content;
    button.Width = 32.0;
    button.Height = 30.0;
    button.Padding = new Thickness(0.0);
    button.FontSize = 16.0;
    ApplyWpfToolButtonStyle(button);
    button.Margin = new Thickness(0.0);
    AutomationProperties.SetName(button, name);
    AutomationProperties.SetAutomationId(button, automationId);
    ToolTip.SetTip(button, name);
}

private static void StepDisplayScaleSlider(Slider slider, int direction)
{
    slider.Value = WindowsDisplayScaleStepPolicy.ResolvePercent(slider.Value, slider.Minimum, slider.Maximum, direction);
}

private void EditorViewSettingsSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs)
{
    if (suppressEditorViewControls)
    {
        return;
    }

    suppressEditorViewControls = true;
    try
    {
        noteWidthSlider.Maximum = Math.Max(12.0, gridLaneWidthSlider.Value - 6.0);
        if (noteWidthSlider.Value > noteWidthSlider.Maximum)
        {
            noteWidthSlider.Value = noteWidthSlider.Maximum;
        }

        editorViewSettings = editorViewSettings.WithView(Math.Round(gridLaneWidthSlider.Value), Math.Round(gridBeatHeightSlider.Value), Math.Round(mainEditorHorizontalOffsetSlider.Value), Math.Round(noteWidthSlider.Value), Math.Round(noteHeightSlider.Value), noteOpacitySlider.Value / 100.0, judgementLinePositionSlider.Value / 100.0, judgementLineThicknessSlider.Value, judgementLineOpacitySlider.Value / 100.0).WithTrackOpacity(WindowsTrackTransparencyPolicy.ToFillOpacity(trackOpacitySlider.Value / 100.0)).WithBarLineThickness(barLineThicknessSlider.Value).WithSubdivisionLineThickness(subdivisionLineThicknessSlider.Value);
        editorBackgroundSettings = editorBackgroundSettings.WithDarkness(backgroundDarknessSlider.Value / 100.0);
    }
    finally
    {
        suppressEditorViewControls = false;
    }

    ApplyEditorViewSettings();
    RefreshEditorViewSettingsUi();
    editorViewSettingsDirty = true;
    editorBackgroundSettingsDirty = true;
    ScheduleEditorViewSettingsSave();
}

private void FrameRateLimitBox_SelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
{
    if (!suppressEditorViewControls && frameRateLimitBox.SelectedItem is ComboBoxItem comboBoxItem && comboBoxItem.Tag is int value)
    {
        int num = WindowsFrameRateOptions.ClampToPresentable(value, PlatformDisplayRefreshRates.Current);
        editorViewSettings = editorViewSettings.WithFrameRateLimit(num);
        AutomationProperties.SetHelpText(frameRateLimitBox, $"FrameRateLimit={num}; Presets={string.Join(',', WindowsFrameRateOptions.PresentablePresets(PlatformDisplayRefreshRates.Current))}");
        ConfigureEditorFrameRateRuntime(resetCounters: true);
        MarkEditorViewSettingsDirty();
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("帧率上限：{0} FPS", num));
    }
}

private void Timeline_PixelsPerBeatChanged(object? sender, double pixelsPerBeat)
{
    SyncTrackViewports();
    SyncAdjustmentZoom();
    UpdateAudioPanelViewports();
    if (!suppressEditorViewControls && !(Math.Abs(editorViewSettings.GridBeatHeight - pixelsPerBeat) < 0.001))
    {
        editorViewSettings = editorViewSettings.WithView(editorViewSettings.GridLaneWidth, pixelsPerBeat, editorViewSettings.MainEditorHorizontalOffset, editorViewSettings.NoteWidth, editorViewSettings.NoteHeight, editorViewSettings.NoteOpacity, editorViewSettings.JudgementLinePosition, editorViewSettings.JudgementLineThickness, editorViewSettings.JudgementLineOpacity);
        suppressEditorViewControls = true;
        try
        {
            gridBeatHeightSlider.Value = pixelsPerBeat;
        }
        finally
        {
            suppressEditorViewControls = false;
        }

        RefreshEditorViewSettingsUi();
        editorViewSettingsDirty = true;
        ScheduleEditorViewSettingsSave();
    }
}

private void ApplyEditorViewSettings()
{
    editorViewSettings = editorViewSettings.Normalize();
    ApplyQuickEditPaletteLayout();
    ApplyQuickValuesPaletteLayout();
    difficultyInfoCardTransform.X = editorViewSettings.DifficultyCardX;
    difficultyInfoCardTransform.Y = editorViewSettings.DifficultyCardY;
    ApplyDifficultyCardCollapsedState();
    timeline.ConfigureView(editorViewSettings.GridLaneWidth, editorViewSettings.GridBeatHeight, editorViewSettings.NoteWidth, editorViewSettings.NoteHeight, editorViewSettings.NoteOpacity, editorViewSettings.JudgementLinePosition, editorViewSettings.JudgementLineThickness, editorViewSettings.JudgementLineOpacity, editorViewSettings.NoteAnchor, editorViewSettings.BarLineThickness, editorViewSettings.SubdivisionLineThickness, editorViewSettings.TrackOpacity);
    timeline.SetMainTrackOffset(editorViewSettings.MainEditorHorizontalOffset);
    timeline.SetBackgroundOpacity(1.0 - editorBackgroundSettings.Darkness);
    RefreshQuickValuesPaletteValues();
    UpdateAudioPanelViewports();
    QueueAudioPanelLayout(clampFreePanels: true);
    QueueDifficultyCardLayout();
}

private void RefreshEditorViewSettingsUi()
{
    SyncSettingsSnapBox(viewModel.ActiveTab?.Session.SnapDivision ?? editorViewSettings.SnapDivision);
    SyncSettingsFixedSnapBox(editorViewSettings.FixedSnapDivision);
    RefreshNoteAnchorButtons();
    gridLaneWidthValueText.Text = $"{editorViewSettings.GridLaneWidth:0} DIP";
    gridBeatHeightValueText.Text = $"{editorViewSettings.GridBeatHeight:0} DIP / beat";
    mainEditorHorizontalOffsetValueText.Text = ((Math.Abs(editorViewSettings.MainEditorHorizontalOffset) < 0.5) ? "居中（0 DIP）" : $"{editorViewSettings.MainEditorHorizontalOffset:+0;-0;0} DIP");
    noteWidthValueText.Text = $"{editorViewSettings.NoteWidth:0} DIP";
    noteHeightValueText.Text = $"{editorViewSettings.NoteHeight:0} DIP";
    noteOpacityValueText.Text = $"{editorViewSettings.NoteOpacity:P0}";
    trackOpacityValueText.Text = $"{WindowsTrackTransparencyPolicy.ToTransparency(editorViewSettings.TrackOpacity):P0}";
    backgroundDarknessValueText.Text = $"{editorBackgroundSettings.Darkness:P0}";
    barLineThicknessValueText.Text = $"{editorViewSettings.BarLineThickness:0.00} DIP";
    subdivisionLineThicknessValueText.Text = AvaloniaLocalizationCatalog.Format("{0:0.00} DIP 基准", editorViewSettings.SubdivisionLineThickness);
    judgementLinePositionValueText.Text = $"{editorViewSettings.JudgementLinePosition:P0}";
    judgementLineThicknessValueText.Text = $"{editorViewSettings.JudgementLineThickness:0.0} DIP";
    judgementLineOpacityValueText.Text = $"{editorViewSettings.JudgementLineOpacity:P0}";
}

private void ResetEditorViewSettings()
{
    WindowsEditorViewSettings windowsEditorViewSettings = WindowsEditorViewSettings.Default;
    editorBackgroundSettings = WindowsEditorBackgroundSettings.Default;
    editorViewSettings = editorViewSettings.WithView(windowsEditorViewSettings.GridLaneWidth, windowsEditorViewSettings.GridBeatHeight, windowsEditorViewSettings.MainEditorHorizontalOffset, windowsEditorViewSettings.NoteWidth, windowsEditorViewSettings.NoteHeight, windowsEditorViewSettings.NoteOpacity, windowsEditorViewSettings.JudgementLinePosition, windowsEditorViewSettings.JudgementLineThickness, windowsEditorViewSettings.JudgementLineOpacity).WithTrackOpacity(windowsEditorViewSettings.TrackOpacity).WithNoteAnchor(windowsEditorViewSettings.NoteAnchor).WithBarLineThickness(windowsEditorViewSettings.BarLineThickness).WithSubdivisionLineThickness(windowsEditorViewSettings.SubdivisionLineThickness);
    suppressEditorViewControls = true;
    try
    {
        gridLaneWidthSlider.Value = editorViewSettings.GridLaneWidth;
        gridBeatHeightSlider.Value = editorViewSettings.GridBeatHeight;
        mainEditorHorizontalOffsetSlider.Value = editorViewSettings.MainEditorHorizontalOffset;
        noteWidthSlider.Maximum = Math.Max(12.0, editorViewSettings.GridLaneWidth - 6.0);
        noteWidthSlider.Value = editorViewSettings.NoteWidth;
        noteHeightSlider.Value = editorViewSettings.NoteHeight;
        noteOpacitySlider.Value = editorViewSettings.NoteOpacity * 100.0;
        trackOpacitySlider.Value = WindowsTrackTransparencyPolicy.ToTransparency(editorViewSettings.TrackOpacity) * 100.0;
        backgroundDarknessSlider.Value = editorBackgroundSettings.Darkness * 100.0;
        barLineThicknessSlider.Value = editorViewSettings.BarLineThickness;
        subdivisionLineThicknessSlider.Value = editorViewSettings.SubdivisionLineThickness;
        judgementLinePositionSlider.Value = editorViewSettings.JudgementLinePosition * 100.0;
        judgementLineThicknessSlider.Value = editorViewSettings.JudgementLineThickness;
        judgementLineOpacitySlider.Value = editorViewSettings.JudgementLineOpacity * 100.0;
    }
    finally
    {
        suppressEditorViewControls = false;
    }

    ApplyEditorViewSettings();
    RefreshEditorViewSettingsUi();
    editorViewSettingsDirty = true;
    editorBackgroundSettingsDirty = true;
    PersistPendingEditorViewSettings();
    viewModel.SetStatus("已恢复 WPF 1.1.0 推荐视图比例");
}

private void ScheduleEditorViewSettingsSave()
{
    editorViewSettingsSaveTimer.Stop();
    editorViewSettingsSaveTimer.Start();
}

private void PersistPendingEditorViewSettings(bool force = false)
{
    editorViewSettingsSaveTimer.Stop();
    if (!editorViewSettingsDirty && !editorBackgroundSettingsDirty && !force)
    {
        return;
    }

    try
    {
        if (editorViewSettingsDirty | force)
        {
            WindowsEditorViewSettingsStore.Save(editorViewSettings);
            editorViewSettingsDirty = false;
        }

        if (editorBackgroundSettingsDirty | force)
        {
            WindowsEditorBackgroundSettingsStore.Save(editorBackgroundSettings);
            editorBackgroundSettingsDirty = false;
        }
    }
    catch (Exception ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("编辑器设置保存失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private void AutoBackupSettings_Changed(object? sender, RoutedEventArgs eventArgs)
{
    if (!suppressEditorViewControls)
    {
        editorViewSettings = editorViewSettings.WithAutoBackup(autoBackupEnabledBox.IsChecked == true, (int)Math.Round(autoBackupIntervalSlider.Value));
        RefreshAutoBackupSettingsUi();
        ConfigureAutoBackupTimer();
        editorViewSettingsDirty = true;
        ScheduleEditorViewSettingsSave();
        viewModel.SetStatus(editorViewSettings.AutoBackupEnabled ? AvaloniaLocalizationCatalog.Format("自动备份已启用：每 {0} 分钟", editorViewSettings.AutoBackupMinutes) : "自动备份已关闭");
    }
}

private void AutoBackupIntervalSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs)
{
    if (!suppressEditorViewControls)
    {
        editorViewSettings = editorViewSettings.WithAutoBackup(autoBackupEnabledBox.IsChecked == true, (int)Math.Round(autoBackupIntervalSlider.Value));
        RefreshAutoBackupSettingsUi();
        ConfigureAutoBackupTimer();
        editorViewSettingsDirty = true;
        ScheduleEditorViewSettingsSave();
    }
}

private void RefreshAutoBackupSettingsUi()
{
    autoBackupIntervalText.Text = AvaloniaLocalizationCatalog.Format("每 {0} 分钟 · 仅在谱面有新改动时生成", editorViewSettings.AutoBackupMinutes);
    autoBackupIntervalSlider.IsEnabled = editorViewSettings.AutoBackupEnabled;
    backupFolderText.Text = AvaloniaAppPaths.EditorBackupRoot;
}

private void ConfigureAutoBackupTimer()
{
    autoBackupTimer.Stop();
    autoBackupTimer.Interval = ((evidenceChartMode && evidenceAutoBackupDelayMilliseconds > 0) ? TimeSpan.FromMilliseconds(evidenceAutoBackupDelayMilliseconds) : TimeSpan.FromMinutes(editorViewSettings.AutoBackupMinutes));
    if (initialized && (editorViewSettings.AutoBackupEnabled || (evidenceChartMode && evidenceAutoBackupDelayMilliseconds > 0)) && !windowClosePreflightInProgress)
    {
        autoBackupTimer.Start();
    }
}

private async void AutoBackupTimer_Tick(object? sender, EventArgs eventArgs)
{
    if (!autoBackupInProgress && editorViewSettings.AutoBackupEnabled)
    {
        activeBackupTask = RunAutomaticBackupAsync();
        await activeBackupTask;
    }
}

private async Task RunAutomaticBackupAsync()
{
    DesktopChartTab[] array = viewModel.Tabs.Where((DesktopChartTab desktopChartTab) => desktopChartTab.Session.IsDirty && (!lastBackupChangeSerials.TryGetValue(desktopChartTab.Id, out var value) || value != desktopChartTab.Session.ChangeSerial)).ToArray();
    if (array.Length == 0)
    {
        return;
    }

    autoBackupInProgress = true;
    int completed = 0;
    List<string> failures = new List<string>();
    try
    {
        DesktopChartTab[] array2 = array;
        foreach (DesktopChartTab tab in array2)
        {
            try
            {
                await BackupTabAsync(tab);
                completed++;
            }
            catch (Exception ex)
            {
                failures.Add(tab.DisplayTitle + "：" + ex.Message);
            }
        }

        if (completed > 0)
        {
            string text = WindowsTransientToastParityPolicy.FormatAutomaticBackupSuccess(completed);
            backupCacheStatusText.Text = text;
            ShowEditorToast(text);
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("自动备份完成：{0} 张谱面", completed));
        }

        if (failures.Count > 0)
        {
            backupCacheStatusText.Text = AvaloniaLocalizationCatalog.Format("自动备份失败：{0}", failures[0]);
            viewModel.SetStatus(backupCacheStatusText.Text);
        }
    }
    finally
    {
        autoBackupInProgress = false;
    }
}

private async Task<string> BackupTabAsync(DesktopChartTab tab)
{
    ArgumentNullException.ThrowIfNull(tab, "tab");
    ChartDocument snapshot = tab.Session.CaptureCommittedDocument();
    long serial = tab.Session.ChangeSerial;
    string sourcePath = tab.SourcePath;
    string result = await Task.Run(() => ChartBackupArchiveService.CreateBackup(AvaloniaAppPaths.EditorBackupRoot, snapshot, sourcePath));
    lastBackupChangeSerials[tab.Id] = serial;
    return result;
}

private async Task BackupCurrentTabAsync()
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null)
    {
        await ShowSettingsMessageAsync("备份", "请先打开一张谱面。");
    }
    else
    {
        if (!TryApplyPendingMetadataEditor(activeTab, enforceCommunityRules: false))
        {
            return;
        }

        if (!autoBackupInProgress)
        {
            autoBackupInProgress = true;
            try
            {
                try
                {
                    backupCacheStatusText.Text = "正在备份…";
                    string text = await BackupTabAsync(activeTab);
                    backupCacheStatusText.Text = AvaloniaLocalizationCatalog.Format("已备份：{0}", System.IO.Path.GetFileName(text));
                    viewModel.SetStatus(backupCacheStatusText.Text);
                    await ShowSettingsMessageAsync("备份完成", text);
                }
                catch (Exception ex)
                {
                    backupCacheStatusText.Text = AvaloniaLocalizationCatalog.Format("备份失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message));
                    await ShowSettingsMessageAsync("备份失败", ex.Message);
                }

                return;
            }
            finally
            {
                autoBackupInProgress = false;
            }
        }

        backupCacheStatusText.Text = "自动备份正在进行，请稍后。";
    }
}

private async Task RestoreBackupAsync()
{
    try
    {
        await RestoreBackupCoreAsync();
    }
    catch (Exception ex)
    {
        if (windowCloseTeardownStarted)
        {
            return;
        }

        backupCacheStatusText.Text = AvaloniaLocalizationCatalog.Format("恢复备份失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message));
        await ShowSettingsMessageAsync("恢复备份失败", ex.Message);
    }
}

private async Task RestoreBackupCoreAsync()
{
    Directory.CreateDirectory(AvaloniaAppPaths.EditorBackupRoot);
    IStorageFolder suggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(AvaloniaAppPaths.EditorBackupRoot);
    string path = await PickLocalFileAsync(new FilePickerOpenOptions { Title = "选择 LumaChart 谱面备份", AllowMultiple = false, SuggestedStartLocation = suggestedStartLocation, FileTypeFilter = new FilePickerFileType[]
{
    new FilePickerFileType("LumaChart 备份")
    {
        Patterns = new string[]
{
    "*.mcbackup"
},
        MimeTypes = new string[]
{
    "*/*"
}
    }
} }, PickedFileRetention.Transient);
    if (string.IsNullOrWhiteSpace(path))
    {
        return;
    }

    ChartBackupArchive backup;
    try
    {
        backup = await Task.Run(() => ChartBackupArchiveService.LoadBackup(path));
    }
    catch (Exception ex)
    {
        await ShowSettingsMessageAsync("无法读取备份", ex.Message);
        return;
    }

    DesktopChartTab existing = ((!string.IsNullOrWhiteSpace(backup.OriginalPath)) ? viewModel.Tabs.FirstOrDefault((DesktopChartTab tab) => PathsEqual(tab.SourcePath, backup.OriginalPath)) : null);
    if (existing?.Session.IsReadOnly ?? false)
    {
        await ShowSettingsMessageAsync("恢复备份", "当前谱面为只读状态，不能执行该编辑命令。");
        return;
    }

    bool originalExists = !string.IsNullOrWhiteSpace(backup.OriginalPath) && File.Exists(backup.OriginalPath);
    string text;
    if (existing != null)
    {
        text = AvaloniaLocalizationCatalog.Format("已打开的标签“{0}”", existing.DisplayTitle);
    }
    else
    {
        text = (originalExists ? backup.OriginalPath : "工作目录中的新建恢复谱面");
    }

    string text2 = (string.IsNullOrWhiteSpace(backup.OriginalPath) ? "原路径未记录" : backup.OriginalPath);
    if (!(await ShowSettingsConfirmationAsync("恢复备份", AvaloniaLocalizationCatalog.Format("备份：{0} [{1}]\n时间：{2:yyyy-MM-dd HH:mm:ss}\n原谱面：{3}\n\n将恢复到：{4}\n恢复后会标记为未保存，确认内容后再手动保存。", backup.Document.Metadata.Title, backup.Document.Metadata.Version, backup.CreatedAt.ToLocalTime(), text2, text), "恢复", dangerous: false)))
    {
        return;
    }

    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab != null && !TryApplyPendingMetadataEditor(activeTab, enforceCommunityRules: false))
    {
        return;
    }

    try
    {
        if (existing?.Session.IsDirty ?? false)
        {
            await BackupTabAsync(existing);
        }

        DesktopChartTab desktopChartTab;
        if (existing != null)
        {
            desktopChartTab = existing;
            viewModel.Activate(desktopChartTab.Id);
        }
        else if (originalExists)
        {
            await viewModel.OpenAsync(backup.OriginalPath);
            desktopChartTab = viewModel.ActiveTab ?? throw new InvalidOperationException("原谱面未能进入编辑缓存。");
        }
        else
        {
            await viewModel.CreateAsync(AvaloniaLocalizationCatalog.Format("{0} 恢复", backup.Document.Metadata.Title), backup.Document.Metadata.Creator);
            desktopChartTab = viewModel.ActiveTab ?? throw new InvalidOperationException("恢复谱面未能进入编辑缓存。");
        }

        if (desktopChartTab.Session.IsReadOnly)
        {
            throw new InvalidOperationException("当前谱面为只读状态，不能执行该编辑命令。");
        }

        ChartDocument chartDocument = backup.Document.Clone();
        chartDocument.FilePath = desktopChartTab.WorkingPath;
        desktopChartTab.Session.ReplaceDocument(chartDocument, markDirty: true);
        lastBackupChangeSerials[desktopChartTab.Id] = desktopChartTab.Session.ChangeSerial;
        if (!(await viewModel.PersistAsync()))
        {
            throw new IOException(viewModel.Status);
        }

        timeline.SetCurrentBeat(0.0);
        viewModel.UpdateCurrentBeat(0.0);
        ShowEditorSurface();
        backupCacheStatusText.Text = AvaloniaLocalizationCatalog.Format("已恢复备份：{0}", System.IO.Path.GetFileName(backup.BackupPath));
        viewModel.SetStatus(backupCacheStatusText.Text);
    }
    catch (Exception ex2)
    {
        backupCacheStatusText.Text = AvaloniaLocalizationCatalog.Format("恢复备份失败：{0}", AvaloniaLocalizationCatalog.Text(ex2.Message));
        await ShowSettingsMessageAsync("恢复备份失败", ex2.Message);
    }
}

private void OpenBackupFolder()
{
    try
    {
        Directory.CreateDirectory(AvaloniaAppPaths.EditorBackupRoot);
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + AvaloniaAppPaths.EditorBackupRoot + "\"", UseShellExecute = true });
    }
    catch (Exception ex)
    {
        backupCacheStatusText.Text = AvaloniaLocalizationCatalog.Format("无法打开备份目录：{0}", AvaloniaLocalizationCatalog.Text(ex.Message));
    }
}

private async Task CleanSelectedCacheAsync()
{
    if (autoBackupInProgress)
    {
        backupCacheStatusText.Text = "自动备份正在进行，请稍后再清理。";
        return;
    }

    if (cacheResourceFilesBox.IsChecked == true && roomChartSyncRoomId.HasValue)
    {
        backupCacheStatusText.Text = "活动联机房间正在使用资源；请先离开房间再清理联机缓存。";
        return;
    }

    List<string> list = new List<string>();
    if (cacheBackupFilesBox.IsChecked == true)
    {
        list.Add("自动备份文件");
    }

    if (cacheEditingFilesBox.IsChecked == true)
    {
        list.Add("未打开的编辑缓存");
    }

    if (cacheResourceFilesBox.IsChecked == true)
    {
        list.Add("联机资源缓存");
    }

    if (list.Count == 0)
    {
        backupCacheStatusText.Text = "请至少勾选一个需要清理的类别。";
        return;
    }

    string text = ((cacheEditingFilesBox.IsChecked == true) ? AvaloniaLocalizationCatalog.Format("\n\n未打开的编辑缓存可能包含上次异常退出后尚未保存的内容；当前打开的谱面会自动跳过。") : string.Empty);
    autoBackupTimer.Stop();
    cleanSelectedCacheButton.IsEnabled = false;
    try
    {
        if (!(await ShowSettingsConfirmationAsync("缓存管理", AvaloniaLocalizationCatalog.Format("即将清理：{0}。此操作不可撤销。{1}\n\n是否继续？", string.Join("、", list), text), "继续清理", dangerous: true)))
        {
            return;
        }

        if (autoBackupInProgress)
        {
            backupCacheStatusText.Text = "自动备份正在进行，请稍后再清理。";
            return;
        }

        if (cacheResourceFilesBox.IsChecked == true && roomChartSyncRoomId.HasValue)
        {
            backupCacheStatusText.Text = "活动联机房间正在使用资源；请先离开房间再清理联机缓存。";
            return;
        }

        List<ManagedStorageCleanupTarget> list2 = new List<ManagedStorageCleanupTarget>();
        if (cacheBackupFilesBox.IsChecked == true)
        {
            list2.Add(new ManagedStorageCleanupTarget(AvaloniaAppPaths.EditorBackupRoot));
        }

        if (cacheEditingFilesBox.IsChecked == true)
        {
            list2.Add(new ManagedStorageCleanupTarget(AvaloniaAppPaths.DesktopDocumentCacheRoot));
        }

        if (cacheResourceFilesBox.IsChecked == true)
        {
            list2.Add(new ManagedStorageCleanupTarget(AvaloniaAppPaths.OnlineRoomResourceCacheRoot));
        }

        backupCacheStatusText.Text = "正在安全清理…";
        ManagedStorageCleanupResult managedStorageCleanupResult = await viewModel.RunCacheCleanupAsync(list2);
        string text2 = AvaloniaLocalizationCatalog.Format("已清理 {0} 个文件、{1} 个目录，释放 {2}", managedStorageCleanupResult.FilesDeleted, managedStorageCleanupResult.DirectoriesDeleted, FormatTransferBytes(managedStorageCleanupResult.BytesFreed));
        if (managedStorageCleanupResult.Failures.Count > 0)
        {
            text2 += AvaloniaLocalizationCatalog.Format("；{0} 项因占用或安全检查被跳过", managedStorageCleanupResult.Failures.Count);
        }

        backupCacheStatusText.Text = text2;
        viewModel.SetStatus(text2);
        if (managedStorageCleanupResult.Failures.Count > 0)
        {
            await ShowSettingsMessageAsync("缓存清理完成", text2 + "\n\n" + string.Join("\n", managedStorageCleanupResult.Failures.Take(6)));
        }
    }
    catch (Exception ex)
    {
        backupCacheStatusText.Text = "缓存清理失败。";
        await ShowSettingsMessageAsync("缓存清理失败", ex.Message);
    }
    finally
    {
        cleanSelectedCacheButton.IsEnabled = true;
        ConfigureAutoBackupTimer();
    }
}

private Task<bool> ShowSettingsConfirmationAsync(string title, string message, string primaryText, bool dangerous)
{
    return new SettingsActionDialog(title, message, primaryText, showCancel: true, dangerous, displayScaleSettings.UiScale, displayScaleSettings.FontScale).ShowDialog<bool>(this);
}

private async Task ShowSettingsMessageAsync(string title, string message)
{
    await new SettingsActionDialog(title, message, "知道了", showCancel: false, dangerous: false, displayScaleSettings.UiScale, displayScaleSettings.FontScale).ShowDialog<bool>(this);
}

private static bool PathsEqual(string left, string? right)
{
    if (string.IsNullOrWhiteSpace(right))
    {
        return false;
    }

    try
    {
        return string.Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(left)), System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    }
    catch (Exception)
    {
        return false;
    }
}

private Control BuildMetadataEditor()
{
    metadataTitleBox.PlaceholderText = "本地谱面不限字符";
    metadataTitleOriginalBox.PlaceholderText = "歌曲名原文";
    metadataArtistBox.PlaceholderText = "本地谱面不限字符";
    metadataArtistOriginalBox.PlaceholderText = "艺术家原文";
    metadataCreatorBox.PlaceholderText = "制作者";
    metadataVersionBox.PlaceholderText = "版本";
    metadataBpmBox.PlaceholderText = "120";
    metadataOffsetBox.PlaceholderText = "0";
    metadataPreviewTimeBox.PlaceholderText = "0";
    metadataOverallDifficultyBox.PlaceholderText = "0–10";
    metadataHpDrainRateBox.PlaceholderText = "0–10";
    metadataKeyCountBox.ItemsSource = WindowsInspectorParityPolicy.Wpf110MetadataKeyCountOptions;
    (Control, string, string)[] array = new (Control, string, string)[12]
    {
        (metadataTitleBox, "Metadata.Title", "歌曲名"),
        (metadataTitleOriginalBox, "Metadata.TitleOriginal", "歌曲名原文"),
        (metadataArtistBox, "Metadata.Artist", "艺术家"),
        (metadataArtistOriginalBox, "Metadata.ArtistOriginal", "艺术家原文"),
        (metadataCreatorBox, "Metadata.Creator", "制作者"),
        (metadataVersionBox, "Metadata.Version", "版本"),
        (metadataBpmBox, "Metadata.Bpm", "谱面 BPM"),
        (metadataOffsetBox, "Metadata.Offset", "音频 Offset"),
        (metadataKeyCountBox, "Metadata.KeyCount", "轨道数"),
        (metadataPreviewTimeBox, "Metadata.PreviewTime", "预览时间"),
        (metadataOverallDifficultyBox, "Metadata.OverallDifficulty", "OD"),
        (metadataHpDrainRateBox, "Metadata.HpDrainRate", "HP")
    };
    (Control, string, string)[] array2 = array;
    for (int i = 0; i < array2.Length; i++)
    {
        (Control, string, string) tuple = array2[i];
        AutomationProperties.SetAutomationId(tuple.Item1, tuple.Item2);
        AutomationProperties.SetName(tuple.Item1, tuple.Item3);
    }

    foreach (TextBox item3 in array.Select(((Control Control, string Id, string Name) entry) => entry.Control).OfType<TextBox>())
    {
        item3.TextChanged += MetadataEditorControlChanged;
    }

    metadataKeyCountBox.SelectionChanged += MetadataKeyCountSelectionChanged;
    metadataRomanizeTitleButton.Content = "转罗马音";
    metadataRomanizeTitleButton.Click += async (object? _, RoutedEventArgs _) =>
    {
        await RomanizeMetadataAsync(title: true);
    };
    ApplyWpfToolButtonStyle(metadataRomanizeTitleButton);
    metadataRomanizeTitleButton.Margin = WindowsInspectorParityPolicy.MetadataToolButtonMargin;
    AutomationProperties.SetAutomationId(metadataRomanizeTitleButton, "Metadata.RomanizeTitle");
    AutomationProperties.SetName(metadataRomanizeTitleButton, "歌曲名转罗马音");
    metadataRomanizeArtistButton.Content = "转罗马音";
    metadataRomanizeArtistButton.Click += async (object? _, RoutedEventArgs _) =>
    {
        await RomanizeMetadataAsync(title: false);
    };
    ApplyWpfToolButtonStyle(metadataRomanizeArtistButton);
    metadataRomanizeArtistButton.Margin = WindowsInspectorParityPolicy.MetadataToolButtonMargin;
    AutomationProperties.SetAutomationId(metadataRomanizeArtistButton, "Metadata.RomanizeArtist");
    AutomationProperties.SetName(metadataRomanizeArtistButton, "艺术家转罗马音");
    AutomationProperties.SetAutomationId(metadataAudioFileBox, "Metadata.AudioFile");
    AutomationProperties.SetName(metadataAudioFileBox, "音频文件");
    metadataAudioSelectButton.Content = "选择…";
    metadataAudioSelectButton.Click += async (object? _, RoutedEventArgs _) =>
    {
        await ImportCurrentChartAudioAsync();
    };
    ApplyWpfToolButtonStyle(metadataAudioSelectButton);
    metadataAudioSelectButton.Margin = WindowsInspectorParityPolicy.MetadataToolButtonMargin;
    AutomationProperties.SetAutomationId(metadataAudioSelectButton, "Metadata.AudioSelect");
    AutomationProperties.SetAutomationId(metadataCoverFileBox, "Metadata.CoverFile");
    AutomationProperties.SetName(metadataCoverFileBox, "歌曲封面");
    metadataCoverSelectButton.Content = "选择…";
    metadataCoverSelectButton.Click += async (object? _, RoutedEventArgs _) =>
    {
        await ImportCurrentChartImageAsync(background: false);
    };
    ApplyWpfToolButtonStyle(metadataCoverSelectButton);
    metadataCoverSelectButton.Margin = WindowsInspectorParityPolicy.MetadataToolButtonMargin;
    AutomationProperties.SetAutomationId(metadataCoverSelectButton, "Metadata.CoverSelect");
    AutomationProperties.SetName(metadataCoverSelectButton, "选择歌曲封面");
    AutomationProperties.SetAutomationId(metadataBackgroundFileBox, "Metadata.BackgroundFile");
    AutomationProperties.SetName(metadataBackgroundFileBox, "背景图片");
    metadataBackgroundSelectButton.Content = "选择…";
    metadataBackgroundSelectButton.Click += async (object? _, RoutedEventArgs _) =>
    {
        await ImportCurrentChartImageAsync(background: true);
    };
    ApplyWpfToolButtonStyle(metadataBackgroundSelectButton);
    metadataBackgroundSelectButton.Margin = WindowsInspectorParityPolicy.MetadataToolButtonMargin;
    AutomationProperties.SetAutomationId(metadataBackgroundSelectButton, "Metadata.BackgroundSelect");
    AutomationProperties.SetName(metadataBackgroundSelectButton, "选择背景图片");
    metadataAnalyzeSingleTempoButton.Content = "检测 BPM 与 Offset";
    metadataAnalyzeSingleTempoButton.Click += async (object? _, RoutedEventArgs _) =>
    {
        await StartSingleTempoAnalysisAsync();
    };
    ApplyWpfAssignButtonStyle(metadataAnalyzeSingleTempoButton);
    metadataAnalyzeSingleTempoButton.Margin = WindowsInspectorParityPolicy.MetadataSingleTempoActionMargin;
    AutomationProperties.SetAutomationId(metadataAnalyzeSingleTempoButton, "Metadata.AnalyzeSingleTempo");
    metadataAnalyzeVariableTempoButton.Content = "预测多个 BPM";
    metadataAnalyzeVariableTempoButton.Click += async (object? _, RoutedEventArgs _) =>
    {
        await StartVariableTempoAnalysisAsync();
    };
    ApplyWpfToolButtonStyle(metadataAnalyzeVariableTempoButton);
    metadataAnalyzeVariableTempoButton.Margin = WindowsInspectorParityPolicy.MetadataVariableTempoActionMargin;
    AutomationProperties.SetAutomationId(metadataAnalyzeVariableTempoButton, "Metadata.AnalyzeVariableTempo");
    metadataAnalyzeManualTempoButton.Content = "手动测量 BPM 与 Offset";
    metadataAnalyzeManualTempoButton.Click += async (object? _, RoutedEventArgs _) =>
    {
        await StartManualTempoMeasurementAsync();
    };
    ApplyWpfToolButtonStyle(metadataAnalyzeManualTempoButton);
    metadataAnalyzeManualTempoButton.Margin = WindowsInspectorParityPolicy.MetadataVariableTempoActionMargin;
    AutomationProperties.SetAutomationId(metadataAnalyzeManualTempoButton, "Metadata.AnalyzeManualTempo");
    metadataAudioAnalysisText.Text = "可自动检测、手动打拍或分窗预测多个 BPM；结果都只在确认后写入 Timing。";
    metadataAudioAnalysisText.Foreground = MutedBrush;
    metadataAudioAnalysisText.FontSize = 10.0;
    metadataAudioAnalysisText.TextWrapping = TextWrapping.Wrap;
    AutomationProperties.SetAutomationId(metadataAudioAnalysisText, "Metadata.AudioAnalysisStatus");
    metadataPreviewHereButton.Content = "选择此处";
    metadataPreviewHereButton.Click += (object? _, RoutedEventArgs _) =>
    {
        SetMetadataPreviewToCurrentPosition();
    };
    ApplyWpfToolButtonStyle(metadataPreviewHereButton);
    metadataPreviewHereButton.Margin = WindowsInspectorParityPolicy.MetadataToolButtonMargin;
    AutomationProperties.SetAutomationId(metadataPreviewHereButton, "Metadata.PreviewHere");
    AutomationProperties.SetHelpText(metadataPreviewHereButton, "播放中使用音频时钟；未播放时使用当前红线与 TimingMap");
    metadataApplyButton.Content = "应用谱面信息";
    metadataApplyButton.Click += (object? _, RoutedEventArgs _) =>
    {
        ApplyMetadataCategory();
    };
    ApplyWpfPrimaryButtonStyle(metadataApplyButton);
    metadataApplyButton.Margin = WindowsInspectorParityPolicy.MetadataApplyActionMargin;
    AutomationProperties.SetAutomationId(metadataApplyButton, "Metadata.Apply");
    ApplyWpf110InspectorTabOrder("Metadata", new Control[22] { metadataTitleBox, metadataRomanizeTitleButton, metadataTitleOriginalBox, metadataArtistBox, metadataRomanizeArtistButton, metadataArtistOriginalBox, metadataCreatorBox, metadataVersionBox, metadataBpmBox, metadataKeyCountBox, metadataAudioFileBox, metadataAudioSelectButton, metadataAnalyzeSingleTempoButton, metadataAnalyzeVariableTempoButton, metadataOffsetBox, metadataPreviewTimeBox, metadataPreviewHereButton, metadataCoverFileBox, metadataCoverSelectButton, metadataBackgroundFileBox, metadataBackgroundSelectButton, metadataApplyButton }, WindowsInspectorParityPolicy.ResolveMetadataTabIndex);
    metadataStatusText.Foreground = MutedBrush;
    metadataStatusText.FontSize = 10.0;
    metadataStatusText.TextWrapping = TextWrapping.Wrap;
    metadataStatusText.Text = "Metadata、KeyCount 与 Offset 以一个撤销步骤原子应用。";
    AutomationProperties.SetAutomationId(metadataStatusText, "Metadata.Status");
    Grid grid = new Grid
    {
        ColumnDefinitions = MetadataPairColumns()
    };
    grid.Children.Add(MetadataPairField("制作者", metadataCreatorBox));
    StackPanel stackPanel = MetadataPairField("版本", metadataVersionBox);
    Grid.SetColumn(stackPanel, 2);
    grid.Children.Add(stackPanel);
    Grid grid2 = new Grid
    {
        ColumnDefinitions = MetadataPairColumns()
    };
    StackPanel stackPanel2 = MetadataPairField("轨道数", metadataKeyCountBox);
    Grid.SetColumnSpan(stackPanel2, 3);
    grid2.Children.Add(stackPanel2);
    metadataAudioFileBox.Margin = new Thickness(0.0, 0.0, 7.0, 0.0);
    Grid control = MetadataActionRow(metadataAudioFileBox, metadataAudioSelectButton, 86.0);
    Grid control2 = MetadataActionRow(metadataTitleBox, metadataRomanizeTitleButton, 84.0);
    Grid control3 = MetadataActionRow(metadataArtistBox, metadataRomanizeArtistButton, 84.0);
    metadataCoverFileBox.Margin = new Thickness(0.0, 0.0, 7.0, 0.0);
    Grid control4 = MetadataActionRow(metadataCoverFileBox, metadataCoverSelectButton, 86.0);
    metadataBackgroundFileBox.Margin = new Thickness(0.0, 0.0, 7.0, 0.0);
    Grid control5 = MetadataActionRow(metadataBackgroundFileBox, metadataBackgroundSelectButton, 86.0);
    StackPanel item = new StackPanel
    {
        Spacing = 0.0,
        Children =
        {
            (Control)metadataAnalyzeSingleTempoButton,
            (Control)metadataAnalyzeVariableTempoButton,
            (Control)metadataAnalyzeManualTempoButton
        }
    };
    metadataPreviewTimeBox.Margin = new Thickness(0.0, 0.0, 7.0, 0.0);
    Grid control6 = MetadataActionRow(metadataPreviewTimeBox, metadataPreviewHereButton, 76.0);
    StackPanel item2 = TimingField("预览时间（ms）", control6);
    timingMetadataSection = new StackPanel
    {
        Spacing = 8.0,
        Margin = new Thickness(16.0, 12.0),
        Children =
        {
            (Control)new TextBlock
            {
                Text = "参考 BPM / 音频 Offset",
                FontSize = 16.0,
                Foreground = Brushes.White
            },
            (Control)TimingField("参考 BPM（显示/速度补偿）", MetadataActionRow(metadataBpmBox, CommandButton("应用", (object? _, RoutedEventArgs _) =>
            {
                ApplyTimingMetadata();
            }), 62.0)),
            (Control)TimingField("音频 Offset（ms，1 ms）", MetadataActionRow(metadataOffsetBox, CommandButton("应用", (object? _, RoutedEventArgs _) =>
            {
                ApplyTimingMetadata();
            }), 62.0)),
            BuildTimingOffsetNudges(),
            (Control)BuildOffsetHereButton(),
            (Control)item,
            (Control)metadataAudioAnalysisText,
            (Control)PrimaryButton("应用 Timing 基础信息", (object? _, RoutedEventArgs _) =>
            {
                ApplyTimingMetadata();
            })
        }
    };
    Grid grid3 = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,*,*")
    };
    TextBlock[] array3 = new TextBlock[3]
    {
        metadataStatsNotesText,
        metadataStatsHoldsText,
        metadataStatsTimingText
    };
    for (int num = 0; num < array3.Length; num++)
    {
        TextBlock textBlock = array3[num];
        textBlock.FontSize = 20.0;
        textBlock.FontWeight = FontWeight.DemiBold;
        textBlock.Foreground = new SolidColorBrush(Color.Parse("#E2E5E6"));
        AutomationProperties.SetAutomationId(textBlock, "Metadata.Stats." + WindowsInspectorParityPolicy.Wpf110MetadataStatLabels[num]);
        TextBlock textBlock2 = new TextBlock
        {
            Text = WindowsInspectorParityPolicy.Wpf110MetadataStatLabels[num],
            Foreground = new SolidColorBrush(Color.Parse("#858D92")),
            FontSize = 10.0
        };
        AutomationProperties.SetAutomationId(textBlock2, "Metadata.Stats." + WindowsInspectorParityPolicy.Wpf110MetadataStatLabels[num] + ".Label");
        StackPanel stackPanel3 = new StackPanel
        {
            Children =
            {
                (Control)textBlock,
                (Control)textBlock2
            }
        };
        Grid.SetColumn(stackPanel3, num);
        grid3.Children.Add(stackPanel3);
    }

    Border border = new Border
    {
        Background = new SolidColorBrush(Color.Parse("#202326")),
        BorderBrush = new SolidColorBrush(Color.Parse("#30353A")),
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(3.0),
        Padding = new Thickness(12.0),
        Margin = new Thickness(0.0, 22.0, 0.0, 0.0),
        Child = grid3
    };
    AutomationProperties.SetAutomationId(border, "Metadata.Stats");
    return new StackPanel
    {
        Spacing = 8.0,
        Margin = new Thickness(0.0, 0.0, 0.0, 64.0),
        Children =
        {
            (Control)new TextBlock
            {
                Text = "谱面信息",
                Foreground = Brushes.White,
                FontSize = 25.0,
                FontWeight = FontWeight.DemiBold
            },
            (Control)new TextBlock
            {
                Text = "元数据、键数、资源与难度设置",
                Foreground = MutedBrush,
                FontSize = 10.0,
                TextWrapping = TextWrapping.Wrap
            },
            (Control)TimingField("歌曲名", control2),
            (Control)TimingField("歌曲名原文", metadataTitleOriginalBox),
            (Control)TimingField("艺术家", control3),
            (Control)TimingField("艺术家原文", metadataArtistOriginalBox),
            (Control)grid,
            (Control)grid2,
            (Control)TimingField("OD · 判定难度（0–10）", metadataOverallDifficultyBox),
            (Control)TimingField("HP · 掉血难度（0–10）", metadataHpDrainRateBox),
            (Control)TimingField("音频", control),
            (Control)item2,
            (Control)TimingField("歌曲封面", control4),
            (Control)TimingField("背景图片", control5),
            (Control)new TextBlock
            {
                Text = "降低轨道数会删除超出新轨道范围的正式 Note，因此同时需要 Note 权限。",
                Foreground = MutedBrush,
                FontSize = 10.0,
                TextWrapping = TextWrapping.Wrap
            },
            (Control)metadataApplyButton,
            (Control)border,
            (Control)metadataStatusText
        }
    };
    static ColumnDefinitions MetadataActionColumns(double width)
    {
        return new ColumnDefinitions("*," + width.ToString(CultureInfo.InvariantCulture));
    }

    static Grid MetadataActionRow(Control field, Button action, double actionWidth)
    {
        field.MinWidth = 0.0;
        field.HorizontalAlignment = HorizontalAlignment.Stretch;
        field.ClipToBounds = true;
        action.MinWidth = 0.0;
        action.MaxWidth = actionWidth - 4.0;
        action.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid grid4 = new Grid
        {
            MinWidth = 0.0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ClipToBounds = true,
            ColumnDefinitions = MetadataActionColumns(actionWidth),
            Children =
            {
                (Control)new InspectorFlexibleField
                {
                    Child = field
                }
            }
        };
        Grid.SetColumn(action, 1);
        grid4.Children.Add(action);
        return grid4;
    }

    static ColumnDefinitions MetadataPairColumns()
    {
        return new ColumnDefinitions("*," + 10.0.ToString(CultureInfo.InvariantCulture) + ",*");
    }

    static StackPanel MetadataPairField(string label, Control value)
    {
        return TimingField(label, new InspectorFlexibleField { Child = value });
    }
}

private Control BuildNoteInspector()
{
    noteSelectionHint.Text = "选择一个 Note 进行编辑";
    noteSelectionHint.Foreground = MutedBrush;
    noteSelectionHint.FontSize = 12.0;
    noteSelectionHint.Margin = new Thickness(0.0, 3.0, 0.0, 3.0);
    noteSelectionHint.TextWrapping = TextWrapping.Wrap;
    AutomationProperties.SetAutomationId(noteSelectionHint, "Inspector.Note.SelectionHint");
    noteBeatBox.PlaceholderText = "1:0/1 或十进制";
    noteEndBeatBox.PlaceholderText = "Tap 可留空";
    noteLaneBox.PlaceholderText = "主轨 1–K；草稿 D:列号";
    noteVolumeBox.PlaceholderText = "0–100";
    noteSoundBox.PlaceholderText = "谱面目录中的纯文件名";
    noteGroupBox.PlaceholderText = "0–65535";
    AutomationProperties.SetName(noteBeatBox, "Note Beat");
    AutomationProperties.SetName(noteEndBeatBox, "Note End Beat");
    AutomationProperties.SetName(noteLaneBox, "Note Lane");
    AutomationProperties.SetName(noteVolumeBox, "Note Volume");
    AutomationProperties.SetName(noteSoundBox, "Note Sound");
    AutomationProperties.SetName(noteGroupBox, "Note Group");
    AutomationProperties.SetAutomationId(noteBeatBox, "Inspector.Note.Beat");
    AutomationProperties.SetAutomationId(noteEndBeatBox, "Inspector.Note.EndBeat");
    AutomationProperties.SetAutomationId(noteLaneBox, "Inspector.Note.Lane");
    AutomationProperties.SetAutomationId(noteVolumeBox, "Inspector.Note.Volume");
    AutomationProperties.SetAutomationId(noteSoundBox, "Inspector.Note.Sound");
    AutomationProperties.SetAutomationId(noteGroupBox, "Inspector.Note.Group");
    noteSoundExistingButton.Content = "▾";
    noteSoundExistingButton.Padding = new Thickness(0.0);
    noteSoundExistingButton.Margin = new Thickness(5.0, 0.0, 0.0, 0.0);
    ApplyWpfToolButtonStyle(noteSoundExistingButton);
    ToolTip.SetTip(noteSoundExistingButton, "选择谱面目录中已有的 Key 音");
    AutomationProperties.SetName(noteSoundExistingButton, "选择已有 Note Key 音");
    AutomationProperties.SetAutomationId(noteSoundExistingButton, "Inspector.Note.SoundExisting");
    noteSoundExistingButton.Click += (object? _, RoutedEventArgs _) =>
    {
        OpenExistingNoteSounds();
    };
    BuildNoteSoundPickerPopup();
    noteSoundSelectButton.Content = "选择文件";
    noteSoundSelectButton.Margin = new Thickness(6.0, 0.0, 0.0, 0.0);
    ApplyWpfToolButtonStyle(noteSoundSelectButton);
    AutomationProperties.SetName(noteSoundSelectButton, "导入 Note Key 音文件");
    AutomationProperties.SetAutomationId(noteSoundSelectButton, "Inspector.Note.SoundSelect");
    noteSoundSelectButton.Click += async (object? _, RoutedEventArgs _) =>
    {
        await ImportSelectedNoteSoundAsync();
    };
    noteApplyButton.Content = "应用属性";
    noteApplyButton.Click += async (object? _, RoutedEventArgs _) =>
    {
        await ApplyNotePropertiesAsync();
    };
    noteApplyButton.Width = 110.0;
    ApplyWpfPrimaryButtonStyle(noteApplyButton);
    AutomationProperties.SetName(noteApplyButton, "应用 Note");
    AutomationProperties.SetAutomationId(noteApplyButton, "Inspector.Note.Apply");
    noteDeleteButton.Content = "删除";
    noteDeleteButton.Click += (object? _, RoutedEventArgs _) =>
    {
        DeleteMixedSelection();
    };
    noteDeleteButton.Width = 90.0;
    noteDeleteButton.Margin = new Thickness(4.0, 0.0, 0.0, 0.0);
    ApplyWpfToolButtonStyle(noteDeleteButton);
    AutomationProperties.SetName(noteDeleteButton, "删除选择");
    AutomationProperties.SetAutomationId(noteDeleteButton, "Inspector.Note.Delete");
    ApplyWpf110NoteInspectorTabOrder();
    Grid grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,10,*"),
        Children =
        {
            (Control)TimingField("主轨 1–K；草稿写 D:列号", noteLaneBox),
            FullModeOnly(TimingField("音量 0–100", noteVolumeBox))
        }
    };
    Grid.SetColumn(grid.Children[1], 2);
    Grid control = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,30,92"),
        Children =
        {
            (Control)noteSoundBox,
            (Control)noteSoundExistingButton,
            (Control)noteSoundSelectButton,
            (Control)noteSoundExistingPopup
        }
    };
    Grid.SetColumn(noteSoundExistingButton, 1);
    Grid.SetColumn(noteSoundSelectButton, 2);
    Grid.SetColumnSpan(noteSoundExistingPopup, 3);
    StackPanel stackPanel = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(0.0, 18.0, 0.0, 0.0)
    };
    stackPanel.Children.Add(noteApplyButton);
    stackPanel.Children.Add(noteDeleteButton);
    return new StackPanel
    {
        Spacing = 0.0,
        MinWidth = 0.0,
        MaxWidth = 286.0,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Children =
        {
            (Control)new TextBlock
            {
                Text = "Note 属性",
                Foreground = Brushes.White,
                FontSize = 25.0,
                FontWeight = FontWeight.DemiBold
            },
            (Control)noteSelectionHint,
            (Control)TimingField("Beat", noteBeatBox),
            (Control)TimingField("End Beat（Tap 可留空）", noteEndBeatBox),
            (Control)grid,
            FullModeOnly(TimingField("Key 音文件", control)),
            (Control)stackPanel,
            BuildNoteCodeImport(),
            (Control)new Border
            {
                MaxWidth = 286.0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ClipToBounds = true,
                Background = new SolidColorBrush(Color.Parse("#202326")),
                BorderBrush = StrokeBrush,
                BorderThickness = new Thickness(1.0),
                CornerRadius = new CornerRadius(3.0),
                Padding = new Thickness(14.0),
                Margin = new Thickness(0.0, 22.0, 0.0, 0.0),
                Child = new TextBlock
                {
                    Text = "提示：Q 选择后拖框可批量选择；左键拖动单个 Note；Ctrl + 左键拖动整批选择；Shift 追加选择；右键始终快速删除。",
                    Foreground = new SolidColorBrush(Color.Parse("#9BA2A6")),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 258.0,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    LineHeight = 20.0
                }
            }
        }
    };
}

private void BuildNoteSoundPickerPopup()
{
    noteSoundPickerTitleText.Text = "选择已有 Key 音";
    noteSoundPickerTitleText.Foreground = new SolidColorBrush(Color.Parse("#F0F3F3"));
    noteSoundPickerTitleText.FontWeight = FontWeight.DemiBold;
    noteSoundPickerTitleText.FontSize = 13.0;
    noteSoundPickerSubtitleText.Text = "仅显示当前谱面可用的音频资源";
    noteSoundPickerSubtitleText.Foreground = new SolidColorBrush(Color.Parse("#7F9095"));
    noteSoundPickerSubtitleText.FontSize = 9.0;
    noteSoundPickerSubtitleText.Margin = new Thickness(0.0, 3.0, 0.0, 0.0);
    noteSoundPickerCountText.Foreground = new SolidColorBrush(Color.Parse("#82A7AA"));
    noteSoundPickerCountText.FontFamily = new FontFamily("Cascadia Mono, Consolas");
    noteSoundPickerCountText.FontSize = 10.0;
    noteSoundPickerCountText.VerticalAlignment = VerticalAlignment.Center;
    AutomationProperties.SetAutomationId(noteSoundPickerCountText, "Inspector.Note.SoundPicker.Count");
    Grid item = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        Margin = new Thickness(5.0, 3.0, 5.0, 7.0),
        Children =
        {
            (Control)new StackPanel
            {
                Spacing = 0.0,
                Children =
                {
                    (Control)noteSoundPickerTitleText,
                    (Control)noteSoundPickerSubtitleText
                }
            },
            (Control)noteSoundPickerCountText
        }
    };
    Grid.SetColumn(noteSoundPickerCountText, 1);
    ConfigureNoteSoundPickerItemButton(noteSoundPickerClearButton, "×", "无 Key 音（清除当前 Note 引用）", "#D69A8D");
    AutomationProperties.SetName(noteSoundPickerClearButton, "清除当前 Note 的 Key 音引用");
    AutomationProperties.SetAutomationId(noteSoundPickerClearButton, "Inspector.Note.SoundPicker.Clear");
    KeyboardNavigation.SetTabIndex(noteSoundPickerClearButton, 0);
    noteSoundPickerClearButton.Click += async (object? _, RoutedEventArgs _) =>
    {
        DesktopChartTab activeTab = viewModel.ActiveTab;
        noteSoundExistingPopup.IsOpen = false;
        if (activeTab != null)
        {
            await AssignSelectedNoteSoundAsync(activeTab, null, import: false);
        }
    };
    noteSoundPickerEmptyText.Foreground = new SolidColorBrush(Color.Parse("#7E8B8F"));
    noteSoundPickerEmptyText.Margin = new Thickness(11.0, 10.0);
    noteSoundPickerEmptyText.TextWrapping = TextWrapping.Wrap;
    noteSoundPickerEmptyText.IsVisible = false;
    AutomationProperties.SetAutomationId(noteSoundPickerEmptyText, "Inspector.Note.SoundPicker.Empty");
    noteSoundPickerScrollViewer.MaxHeight = 250.0;
    noteSoundPickerScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    noteSoundPickerScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
    noteSoundPickerScrollViewer.Content = noteSoundPickerItems;
    Grid grid = new Grid
    {
        Children =
        {
            (Control)noteSoundPickerEmptyText,
            (Control)noteSoundPickerScrollViewer
        }
    };
    Border border = new Border
    {
        Height = 1.0,
        Background = new SolidColorBrush(Color.Parse("#30393D")),
        Margin = new Thickness(4.0, 6.0)
    };
    Grid child = new Grid
    {
        RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"),
        Children =
        {
            (Control)item,
            (Control)noteSoundPickerClearButton,
            (Control)border,
            (Control)grid
        }
    };
    Grid.SetRow(noteSoundPickerClearButton, 1);
    Grid.SetRow(border, 2);
    Grid.SetRow(grid, 3);
    noteSoundPickerSurface.Width = 330.0;
    noteSoundPickerSurface.MaxHeight = 370.0;
    noteSoundPickerSurface.Padding = new Thickness(9.0);
    noteSoundPickerSurface.Background = new SolidColorBrush(Color.Parse("#181D20"));
    noteSoundPickerSurface.BorderBrush = new SolidColorBrush(Color.Parse("#4A5A60"));
    noteSoundPickerSurface.BorderThickness = new Thickness(1.0);
    noteSoundPickerSurface.CornerRadius = new CornerRadius(6.0);
    noteSoundPickerSurface.Child = child;
    AutomationProperties.SetName(noteSoundPickerSurface, "选择已有 Key 音");
    AutomationProperties.SetAutomationId(noteSoundPickerSurface, "Inspector.Note.SoundPicker");
    KeyboardNavigation.SetTabNavigation(noteSoundPickerSurface, WindowsNoteSoundPickerParityPolicy.PopupTabNavigationMode);
    noteSoundPickerSurface.AddHandler(InputElement.KeyDownEvent, NoteSoundPicker_KeyDown, RoutingStrategies.Tunnel);
    noteSoundExistingPopup.PlacementTarget = noteSoundExistingButton;
    noteSoundExistingPopup.Placement = PlacementMode.Bottom;
    noteSoundExistingPopup.VerticalOffset = 5.0;
    noteSoundExistingPopup.IsLightDismissEnabled = true;
    noteSoundExistingPopup.TakesFocusFromNativeControl = true;
    noteSoundExistingPopup.WindowManagerAddShadowHint = true;
    noteSoundExistingPopup.Child = noteSoundPickerSurface;
    noteSoundExistingPopup.Opened += (object? _, EventArgs _) =>
    {
        NoteSoundPicker_Opened();
    };
}

private void ApplyWpf110NoteInspectorTabOrder()
{
    ApplyWpf110InspectorTabOrder("Note", new Control[10] { noteBeatBox, noteEndBeatBox, noteLaneBox, noteVolumeBox, noteSoundBox, noteSoundExistingButton, noteSoundSelectButton, noteGroupBox, noteApplyButton, noteDeleteButton }, WindowsInspectorParityPolicy.ResolveNoteTabIndex);
}

private static void ApplyWpf110InspectorTabOrder(string inspectorName, IReadOnlyList<Control> controls, Func<string?, int> resolveTabIndex)
{
    HashSet<int> hashSet = new HashSet<int>();
    foreach (Control control in controls)
    {
        string automationId = AutomationProperties.GetAutomationId(control);
        int num = resolveTabIndex(automationId);
        if (num < 0 || !hashSet.Add(num))
        {
            throw new InvalidOperationException($"Invalid WPF 1.1.0 {inspectorName} focus target: {automationId ?? "<null>"} -> {num}");
        }

        KeyboardNavigation.SetTabIndex(control, num);
    }

    if (hashSet.Count != controls.Count || !Enumerable.Range(0, controls.Count).All(hashSet.Contains))
    {
        throw new InvalidOperationException("WPF 1.1.0 " + inspectorName + " focus order is not contiguous.");
    }
}

private Control BuildGroupManager()
{
    groupIdBox.PlaceholderText = "0–65535";
    AutomationProperties.SetName(groupIdBox, "Group ID");
    currentPlacementGroupText.Text = "未分组（0）";
    currentPlacementGroupText.Foreground = MutedBrush;
    currentPlacementGroupText.FontWeight = FontWeight.DemiBold;
    groupManagerHint.Text = "选择 Note 后可批量设置分组";
    groupManagerHint.Foreground = MutedBrush;
    groupManagerHint.FontSize = 11.0;
    groupManagerHint.TextWrapping = TextWrapping.Wrap;
    ConfigureGroupButton(groupCreateAndUseButton, "新建并使用", "Group 新建并使用", CreateAndUseGroup);
    ConfigureGroupButton(groupSetCurrentButton, "设为当前放置组", "Group 设为当前", SetCurrentPlacementGroup);
    ConfigureGroupButton(groupAssignButton, "将该组分配给选中 Note", "Group 分配选择", AssignSelectionToGroup);
    ConfigureGroupButton(groupShowAllButton, "全部显示", "Group 全部显示", ShowAllGroups);
    ConfigureGroupButton(groupHideNonzeroButton, "隐藏非零组", "Group 隐藏非零", HideNonzeroGroups);
    ConfigureGroupButton(groupClearSelectedButton, "清除分组", "Group 清除分组", ClearSelectedGroups);
    ApplyWpfPrimaryButtonStyle(groupCreateAndUseButton);
    ApplyWpfPrimaryButtonStyle(groupSetCurrentButton);
    ApplyWpfAssignButtonStyle(groupAssignButton);
    ApplyWpfToolButtonStyle(groupShowAllButton);
    ApplyWpfToolButtonStyle(groupHideNonzeroButton);
    ApplyWpfToolButtonStyle(groupClearSelectedButton);
    AutomationProperties.SetAutomationId(groupCreateAndUseButton, "Inspector.Group.CreateAndUse");
    AutomationProperties.SetAutomationId(groupIdBox, "Inspector.Group.Id");
    AutomationProperties.SetAutomationId(groupSetCurrentButton, "Inspector.Group.SetCurrent");
    AutomationProperties.SetAutomationId(groupAssignButton, "Inspector.Group.AssignSelection");
    AutomationProperties.SetAutomationId(groupShowAllButton, "Inspector.Group.ShowAll");
    AutomationProperties.SetAutomationId(groupHideNonzeroButton, "Inspector.Group.HideNonzero");
    AutomationProperties.SetAutomationId(groupClearSelectedButton, "Inspector.Group.ClearSelection");
    ApplyWpf110InspectorTabOrder("Group", new Control[7] { groupCreateAndUseButton, groupIdBox, groupSetCurrentButton, groupAssignButton, groupShowAllButton, groupHideNonzeroButton, groupClearSelectedButton }, WindowsInspectorParityPolicy.ResolveGroupStaticTabIndex);
    Grid grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto")
    };
    grid.Children.Add(new StackPanel { Children = { (Control)new TextBlock { Text = "Group Manager", Foreground = Brushes.White, FontSize = 24.0, FontWeight = FontWeight.DemiBold }, (Control)new TextBlock { Text = "按音色组织 Note · 隐藏只影响编辑显示", Foreground = MutedBrush, FontSize = 10.0 } } });
    Grid.SetColumn(groupCreateAndUseButton, 1);
    grid.Children.Add(groupCreateAndUseButton);
    Border item = new Border
    {
        Background = PanelBrush,
        BorderBrush = StrokeBrush,
        BorderThickness = new Thickness(1.0),
        CornerRadius = new CornerRadius(4.0),
        Padding = new Thickness(9.0, 7.0),
        Margin = new Thickness(0.0, 12.0, 0.0, 2.0),
        Child = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                (Control)new TextBlock
                {
                    Text = "当前放置分组",
                    Foreground = MutedBrush,
                    VerticalAlignment = VerticalAlignment.Center
                },
                (Control)currentPlacementGroupText
            }
        }
    };
    Grid.SetColumn(currentPlacementGroupText, 1);
    Grid grid2 = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("82,8,*")
    };
    grid2.Children.Add(groupIdBox);
    Grid.SetColumn(groupSetCurrentButton, 2);
    grid2.Children.Add(groupSetCurrentButton);
    UniformGrid uniformGrid = new UniformGrid
    {
        Columns = 3
    };
    uniformGrid.Children.Add(groupShowAllButton);
    uniformGrid.Children.Add(groupHideNonzeroButton);
    uniformGrid.Children.Add(groupClearSelectedButton);
    return new StackPanel
    {
        Spacing = 8.0,
        Children =
        {
            (Control)grid,
            (Control)item,
            (Control)groupManagerHint,
            (Control)grid2,
            (Control)groupAssignButton,
            (Control)uniformGrid,
            (Control)new Border
            {
                Height = 1.0,
                Background = StrokeBrush,
                Margin = new Thickness(0.0, 4.0)
            },
            (Control)groupListPanel
        }
    };
}

private static void ConfigureGroupButton(Button button, string content, string automationName, Action action)
{
    button.Content = content;
    button.Click += (object? _, RoutedEventArgs _) =>
    {
        action();
    };
    ApplyPrimaryButtonStyle(button);
    button.Padding = new Thickness(7.0, 6.0);
    AutomationProperties.SetName(button, automationName);
}

private Control BuildAudioTransport()
{
    audioPlayPauseButton.Content = "▶ 播放";
    audioPlayPauseButton.Click += async (object? _, RoutedEventArgs _) =>
    {
        await ToggleAudioPlaybackAsync();
    };
    ApplyPrimaryButtonStyle(audioPlayPauseButton);
    AutomationProperties.SetAutomationId(audioPlayPauseButton, "Audio.PlayPause");
    audioStopButton.Content = "■ 停止";
    audioStopButton.Click += async (object? _, RoutedEventArgs _) =>
    {
        await StopAudioAsync();
    };
    ApplyPrimaryButtonStyle(audioStopButton);
    AutomationProperties.SetAutomationId(audioStopButton, "Audio.Stop");
    audioRefreshDeviceButton.Content = "重建音频输出";
    ToolTip.SetTip(audioRefreshDeviceButton, "更换音频设备或音频异常时，重新连接音频输出。");
    audioRefreshDeviceButton.Click += async (object? _, RoutedEventArgs _) =>
    {
        await RefreshEditorAudioDeviceAsync();
    };
    ApplyPrimaryButtonStyle(audioRefreshDeviceButton);
    AutomationProperties.SetAutomationId(audioRefreshDeviceButton, "Audio.RefreshDevice");
    audioPositionSlider.Minimum = 0.0;
    audioPositionSlider.Maximum = 1.0;
    audioPositionSlider.Value = 0.0;
    ConstrainInspectorSlider(audioPositionSlider);
    AutomationProperties.SetAutomationId(audioPositionSlider, "Audio.Position");
    audioPositionSlider.AddHandler(InputElement.PointerPressedEvent, (object? _, PointerPressedEventArgs _) =>
    {
        audioPositionPointerDown = true;
    }, RoutingStrategies.Tunnel);
    audioPositionSlider.AddHandler(InputElement.PointerReleasedEvent, async (object? _, PointerReleasedEventArgs _) =>
    {
        audioPositionPointerDown = false;
        await SeekAudioFromSliderAsync();
    }, RoutingStrategies.Tunnel);
    audioPositionSlider.PointerCaptureLost += async (object? _, PointerCaptureLostEventArgs _) =>
    {
        if (audioPositionPointerDown)
        {
            audioPositionPointerDown = false;
            await SeekAudioFromSliderAsync();
        }
    };
    audioPositionSlider.ValueChanged += async (object? _, RangeBaseValueChangedEventArgs _) =>
    {
        if (!suppressAudioControls && !audioPositionPointerDown && audioPositionSlider.IsFocused)
        {
            await SeekAudioFromSliderAsync();
        }
    };
    audioVolumeSlider.Minimum = 0.0;
    audioVolumeSlider.Maximum = 100.0;
    audioVolumeSlider.Value = 80.0;
    ConstrainInspectorSlider(audioVolumeSlider);
    AutomationProperties.SetAutomationId(audioVolumeSlider, "Audio.Volume");
    audioVolumeSlider.ValueChanged += async (object? _, RangeBaseValueChangedEventArgs eventArgs) =>
    {
        if (!suppressAudioControls)
        {
            audioVolumeText.Text = $"{eventArgs.NewValue:0}%";
            double volume = Math.Clamp(eventArgs.NewValue / 100.0, 0.0, 1.0);
            await audioTransport.SetVolumeAsync((float)volume);
            editorViewSettings = editorViewSettings.WithMusicVolume(volume);
            MarkEditorViewSettingsDirty();
        }
    };
    audioRateSlider.Minimum = 0.1;
    audioRateSlider.Maximum = 2.0;
    audioRateSlider.Value = 1.0;
    audioRateSlider.SmallChange = 0.05;
    audioRateSlider.LargeChange = 0.1;
    ConstrainInspectorSlider(audioRateSlider);
    AutomationProperties.SetAutomationId(audioRateSlider, "Audio.Rate");
    audioRateSlider.ValueChanged += async (object? _, RangeBaseValueChangedEventArgs eventArgs) =>
    {
        if (!suppressAudioControls)
        {
            double num = Math.Round(eventArgs.NewValue, 2);
            audioRateText.Text = $"{num:0.00}x";
            await SetEditorPlaybackRateAsync(num);
        }
    };
    audioPreservePitchBox.Content = "变速不变调";
    audioPreservePitchBox.Foreground = Brushes.White;
    AutomationProperties.SetAutomationId(audioPreservePitchBox, "Audio.PreservePitch");
    audioPreservePitchBox.IsCheckedChanged += async (object? _, RoutedEventArgs _) =>
    {
        if (!suppressAudioControls)
        {
            await SetEditorPreservePitchAsync(audioPreservePitchBox.IsChecked == true);
        }
    };
    waveformBackgroundBox.Content = "背景波形";
    waveformBackgroundBox.Foreground = Brushes.White;
    waveformBackgroundBox.IsChecked = evidenceEnableBackgroundWaveform;
    ToolTip.SetTip(waveformBackgroundBox, "WPF 默认是展开的侧栏波形；背景模式默认关闭，勾选后铺在可玩轨道背景");
    waveformBackgroundBox.IsCheckedChanged += (object? _, RoutedEventArgs _) =>
    {
        if (!suppressAudioPanelControls)
        {
            SetAudioPanelBackgroundMode(WindowsAudioPanelKind.Waveform, waveformBackgroundBox.IsChecked == true);
        }
    };
    spectrogramBackgroundBox.Content = "背景频谱";
    spectrogramBackgroundBox.Foreground = Brushes.White;
    spectrogramBackgroundBox.IsChecked = evidenceEnableBackgroundSpectrogram;
    ToolTip.SetTip(spectrogramBackgroundBox, "WPF 默认折叠频谱；勾选后在可玩轨道背景显示 L/M/H 频谱");
    spectrogramBackgroundBox.IsCheckedChanged += (object? _, RoutedEventArgs _) =>
    {
        if (!suppressAudioPanelControls)
        {
            SetAudioPanelBackgroundMode(WindowsAudioPanelKind.Spectrogram, spectrogramBackgroundBox.IsChecked == true);
        }
    };
    audioVisualizationStatusText.Text = "没有打开的谱面";
    audioVisualizationStatusText.Foreground = MutedBrush;
    audioVisualizationStatusText.FontSize = 11.0;
    audioVisualizationStatusText.TextWrapping = TextWrapping.Wrap;
    new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 12.0,
        Children =
        {
            (Control)waveformBackgroundBox,
            (Control)spectrogramBackgroundBox
        }
    };
    audioTimeText.Text = "00:00.000 / 00:00.000";
    audioTimeText.Foreground = Brushes.White;
    audioTimeText.FontFamily = new FontFamily("Cascadia Mono, Consolas");
    audioTimeText.FontSize = 11.0;
    AutomationProperties.SetAutomationId(audioTimeText, "Audio.Time");
    audioRateText.Text = "1.00x";
    audioRateText.Foreground = MutedBrush;
    AutomationProperties.SetAutomationId(audioRateText, "Audio.RateText");
    audioVolumeText.Text = "80%";
    audioVolumeText.Foreground = MutedBrush;
    AutomationProperties.SetAutomationId(audioVolumeText, "Audio.VolumeText");
    audioStatusText.Text = "没有打开的谱面";
    audioStatusText.Foreground = MutedBrush;
    audioStatusText.FontSize = 13.0;
    audioStatusText.TextWrapping = TextWrapping.Wrap;
    AutomationProperties.SetAutomationId(audioStatusText, "Audio.RuntimeStatus");
    audioVolumeText.Margin = new Thickness(0.0, 5.0, 0.0, 10.0);
    audioPreservePitchBox.Margin = new Thickness(2.0, 10.0, 0.0, 2.0);
    ToolTip.SetTip(audioPreservePitchBox, "开启后只改变播放速度，不改变音高；切换时保持当前播放位置");
    return new StackPanel
    {
        Spacing = 7.0,
        Children =
        {
            (Control)TimingField("音频状态", audioStatusText),
            (Control)audioRefreshDeviceButton,
            (Control)TimingField("音乐音量", audioVolumeSlider),
            (Control)audioVolumeText,
            BuildAudioVisualCalibrationSettings(),
            FullModeOnly(BuildKeySoundPanel()),
            (Control)audioPreservePitchBox,
            (Control)new TextBlock
            {
                Text = "开启会使用时间伸缩处理；关闭则随倍速改变音高，性能开销更低。",
                Foreground = MutedBrush,
                FontSize = 10.0,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(22.0, 3.0, 0.0, 2.0)
            }
        }
    };
}

private Control BuildKeySoundPanel()
{
    keySoundEnabledBox.Content = "播放时启用 Key 音";
    keySoundEnabledBox.Foreground = Brushes.White;
    keySoundEnabledBox.IsCheckedChanged += (object? _, RoutedEventArgs _) =>
    {
        if (!suppressKeySoundControls)
        {
            ApplyKeySoundSettings(new WindowsKeySoundSettings(keySoundEnabledBox.IsChecked == true, keySoundSettings.Volume, keySoundSettings.Preset, keySoundSettings.CustomFilePath, keySoundSettings.PlayLongNoteEndSound, keySoundSettings.GlobalOffset, keySoundSettings.PerChartOffsets));
        }
    };
    AutomationProperties.SetAutomationId(keySoundEnabledBox, "KeySound.Enabled");
    keySoundPresetBox.ItemsSource = new KeySoundPresetChoice[6]
    {
        new KeySoundPresetChoice("Snare", KeySoundPreset.Snare),
        new KeySoundPresetChoice("Tambourine", KeySoundPreset.BrightTick),
        new KeySoundPresetChoice("Hand Drum", KeySoundPreset.HandDrum),
        new KeySoundPresetChoice("Clap", KeySoundPreset.HandDrum2),
        new KeySoundPresetChoice("Kick", KeySoundPreset.DeepPulse),
        new KeySoundPresetChoice("自定义", KeySoundPreset.Custom)
    };
    keySoundPresetBox.SelectionChanged += (object? _, SelectionChangedEventArgs _) =>
    {
        if (!suppressKeySoundControls && keySoundPresetBox.SelectedItem is KeySoundPresetChoice keySoundPresetChoice)
        {
            ApplyKeySoundSettings(new WindowsKeySoundSettings(keySoundSettings.EnableKeySounds, keySoundSettings.Volume, keySoundPresetChoice.Preset, keySoundSettings.CustomFilePath, keySoundSettings.PlayLongNoteEndSound, keySoundSettings.GlobalOffset, keySoundSettings.PerChartOffsets));
        }
    };
    AutomationProperties.SetAutomationId(keySoundPresetBox, "KeySound.Preset");
    keySoundCustomFileButton.Content = "选择文件";
    keySoundCustomFileButton.Click += async (object? _, RoutedEventArgs _) =>
    {
        await SelectKeySoundFileAsync();
    };
    ApplyPrimaryButtonStyle(keySoundCustomFileButton);
    AutomationProperties.SetAutomationId(keySoundCustomFileButton, "KeySound.CustomFile");
    Grid item = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions($"*,{90.0}"),
        ColumnSpacing = 6.0,
        Children =
        {
            (Control)keySoundPresetBox,
            (Control)keySoundCustomFileButton
        }
    };
    Grid.SetColumn(keySoundCustomFileButton, 1);
    keySoundCustomFileText.Text = "未选择自定义音频";
    keySoundCustomFileText.Foreground = MutedBrush;
    keySoundCustomFileText.FontSize = 10.0;
    keySoundCustomFileText.TextTrimming = TextTrimming.CharacterEllipsis;
    AutomationProperties.SetAutomationId(keySoundCustomFileText, "KeySound.CustomFilePath");
    keySoundVolumeSlider.Minimum = 0.0;
    keySoundVolumeSlider.Maximum = 100.0;
    keySoundVolumeSlider.Value = 85.0;
    ConstrainInspectorSlider(keySoundVolumeSlider);
    keySoundVolumeSlider.ValueChanged += (object? _, RangeBaseValueChangedEventArgs eventArgs) =>
    {
        if (!suppressKeySoundControls)
        {
            ApplyKeySoundSettings(new WindowsKeySoundSettings(keySoundSettings.EnableKeySounds, eventArgs.NewValue / 100.0, keySoundSettings.Preset, keySoundSettings.CustomFilePath, keySoundSettings.PlayLongNoteEndSound, keySoundSettings.GlobalOffset, keySoundSettings.PerChartOffsets));
        }
    };
    AutomationProperties.SetAutomationId(keySoundVolumeSlider, "KeySound.Volume");
    keySoundVolumeText.Foreground = MutedBrush;
    Grid item2 = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*,42"),
        ColumnSpacing = 8.0,
        Children =
        {
            (Control)new TextBlock
            {
                Text = "Key 音音量",
                Foreground = MutedBrush,
                VerticalAlignment = VerticalAlignment.Center
            },
            (Control)keySoundVolumeSlider,
            (Control)keySoundVolumeText
        }
    };
    Grid.SetColumn(keySoundVolumeSlider, 1);
    Grid.SetColumn(keySoundVolumeText, 2);
    keySoundVolumeText.VerticalAlignment = VerticalAlignment.Center;
    keySoundLongNoteEndBox.Content = "LN 尾部也播放 Key 音";
    keySoundLongNoteEndBox.Foreground = Brushes.White;
    keySoundLongNoteEndBox.IsCheckedChanged += (object? _, RoutedEventArgs _) =>
    {
        if (!suppressKeySoundControls)
        {
            ApplyKeySoundSettings(new WindowsKeySoundSettings(keySoundSettings.EnableKeySounds, keySoundSettings.Volume, keySoundSettings.Preset, keySoundSettings.CustomFilePath, keySoundLongNoteEndBox.IsChecked == true, keySoundSettings.GlobalOffset, keySoundSettings.PerChartOffsets));
        }
    };
    AutomationProperties.SetAutomationId(keySoundLongNoteEndBox, "KeySound.LongNoteEnd");
    ConfigureKeySoundOffsetSlider(keySoundGlobalOffsetSlider, "KeySound.GlobalOffset");
    keySoundGlobalOffsetSlider.ValueChanged += (object? _, RangeBaseValueChangedEventArgs eventArgs) =>
    {
        if (!suppressKeySoundControls)
        {
            ApplyKeySoundSettings(new WindowsKeySoundSettings(keySoundSettings.EnableKeySounds, keySoundSettings.Volume, keySoundSettings.Preset, keySoundSettings.CustomFilePath, keySoundSettings.PlayLongNoteEndSound, (int)Math.Round(eventArgs.NewValue), keySoundSettings.PerChartOffsets));
        }
    };
    keySoundGlobalOffsetText.Foreground = MutedBrush;
    keySoundGlobalOffsetText.FontSize = 10.0;
    ConfigureKeySoundOffsetSlider(keySoundChartOffsetSlider, "KeySound.ChartOffset");
    keySoundChartOffsetSlider.ValueChanged += (object? _, RangeBaseValueChangedEventArgs eventArgs) =>
    {
        if (!suppressKeySoundControls && keySoundActiveTab != null)
        {
            ApplyKeySoundSettings(keySoundSettings.SetChartOffset(keySoundActiveTab.SourcePath, eventArgs.NewValue));
        }
    };
    keySoundChartOffsetText.Foreground = MutedBrush;
    keySoundChartOffsetText.FontSize = 10.0;
    keySoundChartOffsetText.TextWrapping = TextWrapping.Wrap;
    keySoundRuntimeStatusText.Foreground = MutedBrush;
    keySoundRuntimeStatusText.FontSize = 10.0;
    keySoundRuntimeStatusText.TextWrapping = TextWrapping.Wrap;
    AutomationProperties.SetAutomationId(keySoundRuntimeStatusText, "KeySound.RuntimeStatus");
    TextBlock textBlock = new TextBlock
    {
        Text = "Key 音",
        Foreground = Brushes.White,
        FontSize = 17.0,
        FontWeight = FontWeight.DemiBold,
        Margin = new Thickness(0.0, 14.0, 0.0, 2.0)
    };
    AutomationProperties.SetAutomationId(textBlock, "KeySound.Panel");
    keySoundPanel.Background = Brushes.Transparent;
    keySoundPanel.BorderThickness = new Thickness(0.0);
    keySoundPanel.Padding = new Thickness(0.0);
    keySoundPanel.Margin = new Thickness(0.0);
    keySoundPanel.Child = new StackPanel
    {
        Spacing = 6.0,
        Children =
        {
            (Control)textBlock,
            (Control)keySoundEnabledBox,
            (Control)new TextBlock
            {
                Text = "Note 自带 sound 时优先播放真实采音；否则使用下面的全局音色。",
                Foreground = MutedBrush,
                FontSize = 10.0,
                TextWrapping = TextWrapping.Wrap
            },
            (Control)item,
            (Control)keySoundCustomFileText,
            (Control)item2,
            (Control)keySoundLongNoteEndBox,
            (Control)TimingField("Key 音全局同步补偿", keySoundGlobalOffsetSlider),
            (Control)keySoundGlobalOffsetText,
            (Control)TimingField("当前谱面额外补偿", keySoundChartOffsetSlider),
            (Control)keySoundChartOffsetText,
            (Control)new TextBlock
            {
                Text = "负数让 Key 音提前，正数让 Key 音延后；当前谱面的值会单独记忆。",
                Foreground = MutedBrush,
                FontSize = 10.0,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0.0, 2.0, 0.0, 8.0)
            },
            (Control)new TextBlock
            {
                Text = AvaloniaLocalizationCatalog.Format("程序会实时读取当前音频输出队列并自动补偿；上面两项只用于特殊驱动或素材的微调。"),
                Foreground = new SolidColorBrush(Color.Parse("#789DA0")),
                FontSize = 10.0,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
            },
            (Control)keySoundRuntimeStatusText
        }
    };
    return keySoundPanel;
}

private static void ConfigureKeySoundOffsetSlider(Slider slider, string automationId)
{
    slider.Minimum = -500.0;
    slider.Maximum = 500.0;
    slider.SmallChange = 1.0;
    slider.LargeChange = 10.0;
    ConstrainInspectorSlider(slider);
    AutomationProperties.SetAutomationId(slider, automationId);
}

private Control BuildTimingEditor()
{
    timingBeatBox.Text = "0:0/1";
    timingBeatBox.PlaceholderText = "1:0/1 或十进制";
    timingBpmBox.Text = "120";
    timingBpmBox.PlaceholderText = "留空表示无 BPM";
    timingDelayBox.Text = "0";
    timingValueBox.Text = "1.0";
    timingCustomFieldBox.PlaceholderText = "custom.field";
    timingEffectBox.ItemsSource = WindowsInspectorParityPolicy.Wpf110TimingEffectOptions;
    timingEffectBox.SelectedIndex = 0;
    ConfigureInspectorInput(timingBeatBox, "Inspector.Timing.Beat", "Timing Beat");
    ConfigureInspectorInput(timingDelayBox, "Inspector.Timing.Delay", "Timing Delay");
    ConfigureInspectorInput(timingBpmBox, "Inspector.Timing.Bpm", "Timing BPM");
    ConfigureInspectorInput(timingValueBox, "Inspector.Timing.Value", "Timing Effect Value");
    ConfigureInspectorInput(timingCustomFieldBox, "Inspector.Timing.CustomField", "Timing Custom Field");
    ConfigureInspectorComboBox(timingEffectBox, "Inspector.Timing.Effect", "Timing Effect");
    StackPanel customFieldPanel = InspectorField("自定义字段名", timingCustomFieldBox);
    customFieldPanel.Margin = new Thickness(0.0, 8.0, 0.0, 0.0);
    customFieldPanel.IsVisible = false;
    timingEffectBox.SelectionChanged += (object? _, SelectionChangedEventArgs _) =>
    {
        customFieldPanel.IsVisible = timingEffectBox.SelectedItem is WindowsTimingEffectOption windowsTimingEffectOption && windowsTimingEffectOption.Value == "Custom";
        if (customFieldPanel.IsVisible && string.IsNullOrWhiteSpace(timingCustomFieldBox.Text))
        {
            timingCustomFieldBox.Text = timingEffectMemory.Current.CustomFieldName;
        }

        MarkTimingEditorDirty();
    };
    TextBox[] array = new TextBox[5]
    {
        timingBeatBox,
        timingBpmBox,
        timingDelayBox,
        timingValueBox,
        timingCustomFieldBox
    };
    for (int num = 0; num < array.Length; num++)
    {
        array[num].TextChanged += (object? _, TextChangedEventArgs _) =>
        {
            MarkTimingEditorDirty();
        };
    }

    timingPermissionText.Foreground = new SolidColorBrush(Color.Parse("#C28B79"));
    timingPermissionText.FontSize = 10.0;
    timingPermissionText.TextWrapping = TextWrapping.Wrap;
    timingPermissionText.Margin = new Thickness(0.0, 0.0, 0.0, 5.0);
    timingPermissionText.IsVisible = false;
    AutomationProperties.SetAutomationId(timingPermissionText, "Inspector.Timing.Permission");
    timingList.Height = double.NaN;
    timingList.MinHeight = 80.0;
    timingList.HorizontalAlignment = HorizontalAlignment.Stretch;
    timingList.VerticalAlignment = VerticalAlignment.Stretch;
    timingList.Margin = new Thickness(0.0, 14.0, 0.0, 0.0);
    timingList.Background = Brushes.Transparent;
    timingList.BorderThickness = new Thickness(0.0);
    timingList.SelectionMode = SelectionMode.Multiple;
    timingList.ItemTemplate = new FuncDataTemplate<TimingListItem>((TimingListItem timingListItem, INameScope _) => (!(timingListItem == null)) ? BuildTimingListRow(timingListItem) : new Border());
    ConfigureTimingListStyles();
    AutomationProperties.SetAutomationId(timingList, "Inspector.Timing.List");
    AutomationProperties.SetName(timingList, "Timing 列表");
    timingList.SelectionChanged += TimingList_SelectionChanged;
    timingList.AddHandler(InputElement.PointerPressedEvent, TimingList_PointerPressed, RoutingStrategies.Tunnel);
    timingList.AddHandler(InputElement.KeyDownEvent, TimingList_KeyDown, RoutingStrategies.Tunnel);
    timingList.DoubleTapped += async (object? _, TappedEventArgs _) =>
    {
        DesktopChartTab activeTab = viewModel.ActiveTab;
        if (activeTab != null && OsuSourceEnvelope.HasSource(activeTab.Session.Document) && timingList.SelectedItem is TimingListItem timingListItem)
        {
            await EditNativeTimingAsync(timingListItem.Id);
        }
        else
        {
            JumpToSelectedTiming();
        }
    };
    timingAddButton = InspectorActionButton(WindowsInspectorParityPolicy.Wpf110TimingActionLabels[0], "Inspector.Timing.Add", InspectorButtonTone.Primary, (object? _, RoutedEventArgs _) =>
    {
        AddTimingFromFields();
    });
    timingApplyButton = InspectorActionButton(WindowsInspectorParityPolicy.Wpf110TimingActionLabels[1], "Inspector.Timing.Apply", InspectorButtonTone.Tool, (object? _, RoutedEventArgs _) =>
    {
        ApplyTimingFromFields();
    });
    timingDeleteButton = InspectorActionButton(WindowsInspectorParityPolicy.Wpf110TimingActionLabels[2], "Inspector.Timing.Delete", InspectorButtonTone.Tool, (object? _, RoutedEventArgs _) =>
    {
        DeleteSelectedTiming();
    });
    timingCopyButton = InspectorActionButton(WindowsInspectorParityPolicy.Wpf110TimingActionLabels[3], "Inspector.Timing.CopyEffect", InspectorButtonTone.Tool, (object? _, RoutedEventArgs _) =>
    {
        CopyTimingSelection();
    });
    timingPasteButton = InspectorActionButton(WindowsInspectorParityPolicy.Wpf110TimingActionLabels[4], "Inspector.Timing.PasteAtRedLine", InspectorButtonTone.Assign, (object? _, RoutedEventArgs _) =>
    {
        PasteTimingSelection();
    });
    ApplyWpf110InspectorTabOrder("Timing", new Control[12] { timingBeatBox, timingDelayBox, timingBpmBox, timingEffectBox, timingValueBox, timingCustomFieldBox, timingList, timingAddButton, timingApplyButton, timingDeleteButton, timingCopyButton, timingPasteButton }, WindowsInspectorParityPolicy.ResolveTimingTabIndex);
    StackPanel stackPanel = new StackPanel
    {
        Children =
        {
            (Control)new TextBlock
            {
                Text = "Timing Edit",
                Foreground = Brushes.White,
                FontSize = 25.0,
                FontWeight = FontWeight.DemiBold
            }
        }
    };
    global::Avalonia.Controls.Controls children = stackPanel.Children;
    StackPanel stackPanel2 = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(0.0, 3.0),
        Children =
        {
            (Control)new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.Parse("#485054")),
                BorderThickness = new Thickness(1.0),
                CornerRadius = new CornerRadius(3.0),
                Padding = new Thickness(7.0, 3.0),
                Child = new TextBlock
                {
                    Text = "BPM / SV",
                    Foreground = new SolidColorBrush(Color.Parse("#A7B1B4")),
                    FontSize = 9.0
                }
            }
        }
    };
    stackPanel2.Children.Add(timingLayoutCaption = new TextBlock { Text = "Beat · Delay · BPM · Effect", Foreground = MutedBrush, FontSize = 11.0, Margin = new Thickness(8.0, 0.0, 0.0, 0.0), VerticalAlignment = VerticalAlignment.Center });
    children.Add(stackPanel2);
    stackPanel.Children.Add(timingPermissionText);
    StackPanel stackPanel3 = stackPanel;
    Grid grid = new Grid
    {
        RowDefinitions = new RowDefinitions("Auto,Auto"),
        ColumnDefinitions = new ColumnDefinitions("*,10,*")
    };
    StackPanel item = InspectorField("Beat（新增时自动取红线）", timingBeatBox);
    StackPanel stackPanel4 = InspectorField("Delay（ms）", timingDelayBox);
    StackPanel stackPanel5 = InspectorField("BPM（可留空）", timingBpmBox);
    Grid.SetColumn(stackPanel4, 2);
    Grid.SetRow(stackPanel5, 1);
    grid.Children.Add(item);
    grid.Children.Add(stackPanel4);
    grid.Children.Add(stackPanel5);
    Grid grid2 = new Grid
    {
        RowDefinitions = new RowDefinitions("Auto,Auto,*")
    };
    Grid grid3 = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,10,*")
    };
    StackPanel item2 = InspectorField("Effect", timingEffectBox);
    StackPanel stackPanel6 = InspectorField("Value", timingValueBox);
    Grid.SetColumn(stackPanel6, 2);
    grid3.Children.Add(item2);
    grid3.Children.Add(stackPanel6);
    grid2.Children.Add(grid3);
    Grid.SetRow(customFieldPanel, 1);
    grid2.Children.Add(customFieldPanel);
    Grid.SetRow(timingList, 2);
    grid2.Children.Add(timingList);
    Grid grid4 = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,*,*")
    };
    Grid.SetColumn(timingApplyButton, 1);
    Grid.SetColumn(timingDeleteButton, 2);
    grid4.Children.Add(timingAddButton);
    grid4.Children.Add(timingApplyButton);
    grid4.Children.Add(timingDeleteButton);
    Grid grid5 = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,*"),
        Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
    };
    Grid.SetColumn(timingPasteButton, 1);
    grid5.Children.Add(timingCopyButton);
    grid5.Children.Add(timingPasteButton);
    StackPanel stackPanel7 = new StackPanel
    {
        Margin = new Thickness(0.0, 12.0, 0.0, 0.0),
        Children =
        {
            (Control)grid4,
            (Control)grid5
        }
    };
    Grid grid6 = new Grid
    {
        Margin = WindowsInspectorParityPolicy.TimingContentMargin,
        RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
    };
    grid6.Children.Add(stackPanel3);
    Grid.SetRow(grid, 1);
    grid6.Children.Add(grid);
    Grid.SetRow(grid2, 2);
    grid6.Children.Add(grid2);
    Grid.SetRow(stackPanel7, 3);
    Control item3 = BuildNativeTimingActions();
    stackPanel3.Children.Add(item3);
    grid6.Children.Add(stackPanel7);
    timingEditorRoot = grid6;
    legacyTimingControls.AddRange(new Control[4] { grid, grid3, customFieldPanel, stackPanel7 });
    AutomationProperties.SetAutomationId(grid6, "Inspector.Timing.Root");
    timingList.MinHeight = 160.0;
    timingList.MaxHeight = 240.0;
    grid6.MinHeight = 420.0;
    return new ScrollViewer
    {
        Content = new StackPanel
        {
            Children =
            {
                timingMetadataSection,
                (Control)grid6
            }
        },
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
}

private static StackPanel InspectorField(string label, Control control)
{
    return new StackPanel
    {
        Children =
        {
            (Control)new TextBlock
            {
                Text = label,
                Foreground = MutedBrush,
                FontSize = 11.0,
                Margin = new Thickness(0.0, 10.0, 0.0, 5.0)
            },
            control
        }
    };
}

private static void ConfigureInspectorInput(TextBox textBox, string automationId, string automationName)
{
    SolidColorBrush normalBorder = new SolidColorBrush(Color.Parse("#383E43"));
    textBox.Background = new SolidColorBrush(Color.Parse("#15181A"));
    textBox.Foreground = new SolidColorBrush(Color.Parse("#E3E6E8"));
    textBox.CaretBrush = new SolidColorBrush(Color.Parse("#B9C7C8"));
    textBox.BorderBrush = normalBorder;
    textBox.BorderThickness = new Thickness(1.0);
    textBox.CornerRadius = new CornerRadius(3.0);
    textBox.Padding = new Thickness(8.0, 6.0);
    textBox.MinHeight = 34.0;
    textBox.VerticalContentAlignment = VerticalAlignment.Center;
    textBox.SelectionBrush = new SolidColorBrush(Color.Parse("#526E71"));
    textBox.AcceptsReturn = false;
    textBox.MaxLines = 1;
    textBox.GotFocus += (object? _, FocusChangedEventArgs _) =>
    {
        textBox.BorderBrush = new SolidColorBrush(Color.Parse("#718F92"));
    };
    textBox.LostFocus += (object? _, FocusChangedEventArgs _) =>
    {
        textBox.BorderBrush = normalBorder;
    };
    AutomationProperties.SetAutomationId(textBox, automationId);
    AutomationProperties.SetName(textBox, automationName);
}

private static void ConfigureInspectorComboBox(ComboBox comboBox, string automationId, string automationName)
{
    comboBox.Background = new SolidColorBrush(Color.Parse("#15181A"));
    comboBox.Foreground = new SolidColorBrush(Color.Parse("#E3E6E8"));
    comboBox.BorderBrush = new SolidColorBrush(Color.Parse("#383E43"));
    comboBox.BorderThickness = new Thickness(1.0);
    comboBox.CornerRadius = new CornerRadius(3.0);
    comboBox.Padding = new Thickness(8.0, 6.0);
    comboBox.MinHeight = 34.0;
    comboBox.VerticalContentAlignment = VerticalAlignment.Center;
    AutomationProperties.SetAutomationId(comboBox, automationId);
    AutomationProperties.SetName(comboBox, automationName);
}

private static Button InspectorActionButton(string text, string automationId, InspectorButtonTone tone, EventHandler<RoutedEventArgs> handler)
{
    Button button = new Button();
    button.Content = text;
    IBrush foreground;
    if (tone != InspectorButtonTone.Assign)
    {
        IBrush white = Brushes.White;
        foreground = white;
    }
    else
    {
        IBrush white = new SolidColorBrush(Color.Parse("#FFF4E3"));
        foreground = white;
    }

    button.Foreground = foreground;
    Button button2 = button;
    button2.Background = tone switch
    {
        InspectorButtonTone.Primary => (IBrush? )new SolidColorBrush(Color.Parse("#38676C")),
        InspectorButtonTone.Assign => new SolidColorBrush(Color.Parse("#76572F")),
        _ => new SolidColorBrush(Color.Parse("#1A1D20")),
    };
    Button button3 = button;
    button3.BorderBrush = tone switch
    {
        InspectorButtonTone.Primary => (IBrush? )new SolidColorBrush(Color.Parse("#4A777B")),
        InspectorButtonTone.Assign => new SolidColorBrush(Color.Parse("#9B7542")),
        _ => new SolidColorBrush(Color.Parse("#34393D")),
    };
    button.BorderThickness = new Thickness(1.0);
    button.CornerRadius = new CornerRadius(3.0);
    button.Padding = new Thickness(9.0, 6.0);
    button.Margin = new Thickness(3.0, 0.0);
    button.MinHeight = 37.0;
    button.HorizontalAlignment = HorizontalAlignment.Stretch;
    button.FontWeight = ((tone == InspectorButtonTone.Tool) ? FontWeight.Normal : FontWeight.DemiBold);
    button.HorizontalContentAlignment = HorizontalAlignment.Center;
    button.VerticalContentAlignment = VerticalAlignment.Center;
    Button button4 = button;
    if (tone == InspectorButtonTone.Primary)
    {
        button4.Classes.Add("accent");
    }

    button4.Click += handler;
    AutomationProperties.SetAutomationId(button4, automationId);
    AutomationProperties.SetName(button4, text);
    return button4;
}

private void ConfigureTimingListStyles()
{
    ControlTheme controlTheme = new ControlTheme(typeof(ListBoxItem));
    controlTheme.Setters.Add(new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<ListBoxItem>((ListBoxItem _, INameScope _) =>
    {
        ContentPresenter contentPresenter = new ContentPresenter
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        contentPresenter.Bind(ContentPresenter.ContentProperty, new TemplateBinding(ContentControl.ContentProperty));
        contentPresenter.Bind(ContentPresenter.ContentTemplateProperty, new TemplateBinding(ContentControl.ContentTemplateProperty));
        contentPresenter.Bind(ContentPresenter.PaddingProperty, new TemplateBinding(TemplatedControl.PaddingProperty));
        Border border = new Border();
        border.Child = contentPresenter;
        border.Bind(Border.BackgroundProperty, new TemplateBinding(TemplatedControl.BackgroundProperty));
        border.Bind(Border.BorderBrushProperty, new TemplateBinding(TemplatedControl.BorderBrushProperty));
        border.Bind(Border.BorderThicknessProperty, new TemplateBinding(TemplatedControl.BorderThicknessProperty));
        border.Bind(Border.CornerRadiusProperty, new TemplateBinding(TemplatedControl.CornerRadiusProperty));
        return border;
    })));
    timingList.ItemContainerTheme = controlTheme;
    Style style = new Style((Selector? selector) => selector.OfType<ListBoxItem>());
    style.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent));
    style.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, new SolidColorBrush(Color.Parse("#34393D"))));
    style.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1.0)));
    style.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(3.0)));
    style.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(10.0, 8.0)));
    style.Setters.Add(new Setter(Layoutable.MarginProperty, new Thickness(0.0, 0.0, 0.0, 6.0)));
    style.Setters.Add(new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
    timingList.Styles.Add(style);
    Style style2 = new Style((Selector? selector) => selector.OfType<ListBoxItem>().Class(":pointerover"));
    style2.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.Parse("#222629"))));
    timingList.Styles.Add(style2);
    Style style3 = new Style((Selector? selector) => selector.OfType<ListBoxItem>().Class(":selected"));
    style3.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.Parse("#293235"))));
    style3.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, new SolidColorBrush(Color.Parse("#698B8E"))));
    timingList.Styles.Add(style3);
}

private static Control BuildTimingListRow(TimingListItem item)
{
    Grid grid = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("92,*,82")
    };
    TextBlock textBlock = new TextBlock
    {
        Text = item.Beat,
        Foreground = new SolidColorBrush(Color.Parse("#E0E3E4")),
        FontWeight = FontWeight.DemiBold,
        VerticalAlignment = VerticalAlignment.Center
    };
    AutomationProperties.SetAutomationId(textBlock, $"Inspector.Timing.Row.{item.Id:D}");
    AutomationProperties.SetHelpText(textBlock, $"TimingId={item.Id:D}; Beat={item.Beat}");
    TextBlock textBlock2 = new TextBlock
    {
        Text = item.Main,
        Foreground = new SolidColorBrush(Color.Parse("#A3AAAE")),
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };
    TextBlock textBlock3 = new TextBlock
    {
        Text = item.Effect,
        Foreground = new SolidColorBrush(Color.Parse("#D09A56")),
        HorizontalAlignment = HorizontalAlignment.Right,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };
    Grid.SetColumn(textBlock2, 1);
    Grid.SetColumn(textBlock3, 2);
    grid.Children.Add(textBlock);
    grid.Children.Add(textBlock2);
    grid.Children.Add(textBlock3);
    AutomationProperties.SetName(grid, $"{item.Beat} · {item.Main} · {item.Effect}");
    return grid;
}

private static StackPanel TimingField(string label, Control control)
{
    return new StackPanel
    {
        Spacing = 3.0,
        Children =
        {
            (Control)new InspectorFlexibleField
            {
                Child = new TextBlock
                {
                    Text = label,
                    Foreground = MutedBrush,
                    FontSize = 11.0,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            },
            control
        }
    };
}

private async void Window_Opened(object? sender, EventArgs eventArgs)
{
    if (initialized)
    {
        return;
    }

    initialized = true;
    if (OperatingSystem.IsWindows() && editorViewSettings.FullscreenMode == WindowsFullscreenMode.Borderless && WindowState != WindowState.FullScreen)
    {
        windowStateBeforeFullscreen = WindowState;
        WindowState = WindowState.FullScreen;
        ConfigureFullscreenTitleBar();
    }

    bool flag = evidenceChartMode && (onlineAccountHost?.Endpoint.IsLoopback ?? false) && onlineAccountHost.Endpoint.Scheme == Uri.UriSchemeHttp;
    if ((!evidenceChartMode && !evidenceFocusLibraryVirtualization && !evidenceFocusLibraryLauncher) | flag)
    {
        RestoreStartupOnlineAccountAsync(!evidenceChartMode);
    }

    if (!evidenceFocusLibraryVirtualization && !evidenceFocusLibraryLauncher)
    {
        roomInvitationTimer.Start();
        followNotificationTimer.Start();
        ReconcileFollowNotificationSession();
    }

    WindowsDisplayScaleSettings windowsDisplayScaleSettings = displayScaleSettings;
    displayScaleSettings = displayScaleController.InitializeForScreen(OperatingSystem.IsWindows() ? ResolveDisplayScaleScreen() : null);
    if (!windowsDisplayScaleSettings.DisplayScaleInitialized || Math.Abs(windowsDisplayScaleSettings.UiScale - displayScaleSettings.UiScale) > 1E-12 || Math.Abs(windowsDisplayScaleSettings.FontScale - displayScaleSettings.FontScale) > 1E-12)
    {
        displayScaleSettingsDirty = true;
        ScheduleDisplayScaleSettingsSave();
    }

    RefreshDisplayScaleSettingsUi();
    QueueDisplayScaleMaterializedContentScan();
    localizationController.ScheduleApply();
    bool deferInitialLibraryScan = !evidenceChartMode && !evidenceFocusLibraryVirtualization && !evidenceFocusLibraryLauncher && string.IsNullOrWhiteSpace(evidenceOutputPath);
    Stopwatch libraryInitializationStopwatch = Stopwatch.StartNew();
    await viewModel.InitializeAsync(null, startupChartPaths.Count == 0, !deferInitialLibraryScan);
    ShowWorkspaceRecoveryWarningsIfNeeded();
    ShowWorkspaceRootFallbackNoticeIfNeeded();
    if (!deferInitialLibraryScan)
    {
        ShowLibraryScanErrorIfNeeded();
    }

    await RestoreRetainedOnlineRoomTabsAsync();
    libraryInitializationStopwatch.Stop();
    evidenceLibraryInitializationMilliseconds = libraryInitializationStopwatch.Elapsed.TotalMilliseconds;
    foreach (string startupChartPath in startupChartPaths)
    {
        if (!evidenceChartMode || evidenceShowOsuImportDialog || !System.IO.Path.GetExtension(startupChartPath).Equals(".osu", StringComparison.OrdinalIgnoreCase))
        {
            await OpenChartOrArchiveAsync(startupChartPath);
            continue;
        }

        try
        {
            await viewModel.OpenAsync(startupChartPath, OsuNoteTimingImportMode.CalibrateCommonSubdivisions);
            ShowEditorSurface();
        }
        catch (Exception ex)
        {
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("打开谱面失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
        }
    }

    startupInitializationCompleted = true;
    DrainSingleInstanceRequestsAsync();
    PrepareSyntheticEvidenceWorkload();
    PrepareChartValidationEvidenceWorkload();
    UpdateUi();
    FocusLibraryDeletionEvidence();
    FocusKeySoundEvidencePanel();
    FocusAudioTransportEvidencePanel();
    FocusDisplayScaleEvidencePanel();
    FocusSettingsCacheEvidencePanel();
    audioUiTimer.Start();
    ConfigureEditorFrameRateRuntime(resetCounters: true);
    ConfigureAutoBackupTimer();
    PlatformIncomingFiles.SetHandler(OpenChartOrArchiveAsync);
    if (deferInitialLibraryScan)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(StartDeferredLibraryScan, DispatcherPriority.Background);
    }

    if (!string.IsNullOrWhiteSpace(evidenceOutputPath))
    {
        if (evidenceFocusLibraryVirtualization)
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(StartLibraryVirtualizationEvidenceProbe, DispatcherPriority.Background);
        }
        else
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(StartEvidenceProbe, DispatcherPriority.Background);
        }
    }
}

private void ShowWorkspaceRecoveryWarningsIfNeeded()
{
    WindowsNativeMessageBoxRequest windowsNativeMessageBoxRequest = WindowsWorkspaceRecoveryNoticePolicy.Create(viewModel.RecoveryWarnings);
    if (windowsNativeMessageBoxRequest != null)
    {
        WindowsNativeMessageBox.Show(this, windowsNativeMessageBoxRequest);
    }
}

private void ShowWorkspaceRootFallbackNoticeIfNeeded()
{
    WorkspaceRootFallbackNotice workspaceRootFallback = viewModel.WorkspaceRootFallback;
    if ((object)workspaceRootFallback != null)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("原谱库目录暂时不可用，本次改用：{0}", workspaceRootFallback.FallbackRoot));
        WindowsNativeMessageBox.Show(this, new WindowsNativeMessageBoxRequest(AvaloniaLocalizationCatalog.Format("原谱库目录暂时无法访问：\n{0}\n\n原因：{1}\n\n本次改用：\n{2}\n\n设置没有被修改；原目录恢复可用后，下次启动会自动回到原目录。", workspaceRootFallback.RequestedRoot, workspaceRootFallback.Reason, workspaceRootFallback.FallbackRoot), "谱库目录暂时不可用", WindowsNativeMessageBoxIcon.Warning));
    }
}

private void StartDeferredLibraryScan()
{
    if (!windowClosePreflightComplete)
    {
        Task task = startupLibraryScanTask;
        if (task == null || task.IsCompleted)
        {
            startupLibraryScanTask = viewModel.EnsureLibrarySnapshotLoadedAsync();
        }
    }
}

private void FocusKeySoundEvidencePanel()
{
    if (!evidenceChartMode || !evidenceFocusKeySound)
    {
        return;
    }

    editorInspectorTabs.SelectedItem = settingsInspectorTab;
    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (!keySoundRuntimeDisposed && evidenceFocusKeySound)
        {
            editorInspectorTabs.SelectedItem = settingsInspectorTab;
            keySoundPanel.BringIntoView();
        }
    }, DispatcherPriority.Background);
}

private void FocusAudioTransportEvidencePanel()
{
    if (!evidenceChartMode || !evidenceFocusAudioTransport)
    {
        return;
    }

    editorInspectorTabs.SelectedItem = settingsInspectorTab;
    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (!keySoundRuntimeDisposed && evidenceFocusAudioTransport)
        {
            editorInspectorTabs.SelectedItem = settingsInspectorTab;
            audioPlayPauseButton.BringIntoView();
        }
    }, DispatcherPriority.Background);
}

private void FocusLibraryDeletionEvidence()
{
    if (evidenceChartMode && evidenceFocusLibraryDeletion)
    {
        ShowLibrary();
        librarySearchBox.Focus();
    }
}

private void FocusDisplayScaleEvidencePanel()
{
    if (!evidenceChartMode || !evidenceFocusDisplayScale)
    {
        return;
    }

    editorInspectorTabs.SelectedItem = settingsInspectorTab;
    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (!windowClosePreflightComplete && evidenceFocusDisplayScale)
        {
            editorInspectorTabs.SelectedItem = settingsInspectorTab;
            uiScaleSlider.BringIntoView();
            QueueDisplayScaleMaterializedContentScan();
        }
    }, DispatcherPriority.Background);
}

private void FocusSettingsCacheEvidencePanel()
{
    if (!evidenceChartMode || !evidenceFocusSettingsCache)
    {
        return;
    }

    editorInspectorTabs.SelectedItem = settingsInspectorTab;
    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (!windowClosePreflightComplete && evidenceFocusSettingsCache)
        {
            editorInspectorTabs.SelectedItem = settingsInspectorTab;
            mainEditorHorizontalOffsetSlider.BringIntoView();
            QueueDisplayScaleMaterializedContentScan();
        }
    }, DispatcherPriority.Background);
}

internal bool? TryEmergencyPersistWorkingCopies(TimeSpan timeout)
{
    try
    {
        if (emergencyPersistAttempted || windowCloseTeardownStarted)
        {
            return null;
        }

        emergencyPersistAttempted = true;
        if (!viewModel.Tabs.Any((DesktopChartTab tab) => tab.Session.IsDirty))
        {
            return null;
        }

        Task<bool> task = Task.Run(() => viewModel.PersistAsync());
        return task.Wait(timeout) && task.Result;
    }
    catch (Exception)
    {
        return false;
    }
}

private async void Window_Closing(object? sender, WindowClosingEventArgs eventArgs)
{
    PlatformIncomingFiles.ClearHandler();
    viewModel.CancelLibraryScan();
    CancelPendingWindowSnapApplication();
    onlineSurfaceCompletion?.TrySetResult(result: false);
    friendsPanelLoadCancellation?.Cancel();
    evidenceTimer?.Stop();
    editorToastTimer.Stop();
    audioUiTimer.Stop();
    StopEditorHighRateFramePump();
    autoBackupTimer.Stop();
    roomInvitationTimer.Stop();
    followNotificationTimer.Stop();
    chartingHeartbeatTimer.Stop();
    followNotificationToastTimer.Stop();
    settingsSnapCommitTimer.Stop();
    settingsFixedSnapCommitTimer.Stop();
    navigationMarkerSaveTimer.Stop();
    CancelPendingEditorSeek();
    PersistPendingProGridColumns();
    PersistPendingKeySoundSettings(force: true);
    PersistPendingDisplayScaleSettings(force: true);
    if (!windowClosePreflightComplete)
    {
        CommitSettingsSnapBoxText(restoreInvalidText: false);
        CommitSettingsFixedSnapBoxText(restoreInvalidText: false);
    }

    PersistPendingEditorViewSettings(force: true);
    PersistPendingShortcutSettings(force: true);
    PersistPendingAudioPanelSettings(force: true);
    if (windowClosePreflightComplete)
    {
        return;
    }

    DesktopChartTab activeTab = viewModel.ActiveTab;
    bool hasUnsavedDocuments = (activeTab != null && metadataEditorTabId == activeTab.Id && metadataEditorDirty) || trackWorkspaces.Values.Any((EditorTrackWorkspace w) => w.HasPendingChanges) || viewModel.Tabs.Any((DesktopChartTab desktopChartTab) => desktopChartTab.Session.IsDirty);
    bool hasBlockingWork = HasBlockingExternalApplicationShutdownWork();
    bool hasActiveOnlineRoom = roomChartSyncRoomId.HasValue || onlineAccountHost?.Session.CurrentRoom != null;
    bool flag = WindowsExternalApplicationShutdownPolicy.CanCloseImmediately(eventArgs.CloseReason, eventArgs.IsProgrammatic, hasUnsavedDocuments, hasBlockingWork, hasActiveOnlineRoom);
    WriteExternalApplicationShutdownEvidence(eventArgs, hasUnsavedDocuments, hasBlockingWork, hasActiveOnlineRoom, flag);
    if (flag)
    {
        windowClosePreflightComplete = true;
        return;
    }

    eventArgs.Cancel = true;
    if (windowClosePreflightInProgress)
    {
        return;
    }

    windowClosePreflightInProgress = true;
    bool fullscreenSuspendedForClosePreflight = SuspendFullscreenForClosePreflight();
    try
    {
        await SaveTrackWorkspacesAsync();
        if (!(await PersistPendingNavigationMarkersAsync(force: true)) && !(await ShowSettingsConfirmationAsync("无法保存跳转标记", AvaloniaLocalizationCatalog.Format("{0}\n\n跳转标记无法写入。可以取消退出，检查工作目录权限或磁盘空间后重试；也可以仍然退出，但尚未保存的跳转标记会丢失。", viewModel.Status), "仍然退出", dangerous: true)))
        {
            return;
        }

        DesktopChartTab activeTab2 = viewModel.ActiveTab;
        if (activeTab2 != null && !TryApplyPendingMetadataEditor(activeTab2, enforceCommunityRules: false))
        {
            return;
        }

        DesktopChartTab[] array = viewModel.Tabs.Where((DesktopChartTab desktopChartTab) => desktopChartTab.Session.IsDirty).ToArray();
        foreach (DesktopChartTab tab in array)
        {
            if (retainedOnlineRoomTabIds.Contains(tab.Id))
            {
                switch (await new PreservedRoomDraftCloseDialog(tab.DisplayTitle, displayScaleSettings.UiScale, displayScaleSettings.FontScale).ShowDialog<PreservedRoomDraftCloseChoice>(this))
                {
                    case PreservedRoomDraftCloseChoice.Cancel:
                        return;
                    case PreservedRoomDraftCloseChoice.SaveAs:
                    {
                        SaveRoundTripResult saveRoundTripResult = await PickAndSaveAsAsync(tab.Id);
                        if ((object)saveRoundTripResult == null || !saveRoundTripResult.StructurallyEqual || tab.Session.IsDirty)
                        {
                            return;
                        }

                        break;
                    }
                }

                continue;
            }

            switch (await new UnsavedChangesDialog(tab.DisplayTitle, applicationClosing: true, displayScaleSettings.UiScale, displayScaleSettings.FontScale).ShowDialog<UnsavedChangesChoice>(this))
            {
                case UnsavedChangesChoice.Cancel:
                    return;
                case UnsavedChangesChoice.Save:
                {
                    SaveRoundTripResult saveRoundTripResult2 = await SaveAndVerifyWithPreflightAsync(tab.Id);
                    if ((object)saveRoundTripResult2 == null || !saveRoundTripResult2.StructurallyEqual || tab.Session.IsDirty)
                    {
                        await ShowSettingsMessageAsync("保存失败", AvaloniaLocalizationCatalog.Format("“{0}”未能保存，本次退出已取消，编辑内容仍保留在编辑器中。\n\n{1}", tab.DisplayTitle, viewModel.Status));
                        return;
                    }

                    break;
                }
            }
        }

        if (!(await ConfirmOnlineRoomExitAsync()))
        {
            return;
        }

        await StopBackgroundWorkForCloseAsync();
        DesktopChartTab activeTab3 = viewModel.ActiveTab;
        if (activeTab3 != null && !TryApplyPendingMetadataEditor(activeTab3, enforceCommunityRules: false))
        {
            return;
        }

        await SaveTrackWorkspacesAsync();
        if (!(await viewModel.PersistAsync()))
        {
            WindowsNativeMessageBox.Show(this, new WindowsNativeMessageBoxRequest(AvaloniaLocalizationCatalog.Format("{0}\n\n为避免丢失可恢复的标签与编辑缓存，本次退出已取消。请检查工作目录权限或磁盘空间后重试。", viewModel.Status), "无法保存工作区", WindowsNativeMessageBoxIcon.Error));
            return;
        }

        windowCloseTeardownStarted = true;
        try
        {
            ReleaseChartBackgroundForShutdown();
            viewModel.StateChanged -= ViewModel_StateChanged;
            audioTransport.StateChanged -= AudioTransport_StateChanged;
            audioVisualization.StateChanged -= AudioVisualization_StateChanged;
            await StopRoomChartSyncAsync(WindowsOnlineRoomRetentionPolicy.ShouldPreserveDraftOnConfirmedExit(WindowsOnlineRoomExitContext.ApplicationClose));
            await LeaveCurrentRoomBeforeShutdownAsync();
            await StopOnlineRoomPresenceLoopAsync();
            await CancelPendingEditorSeek();
            keySoundPlanDebounceTimer.Stop();
            keySoundSettingsSaveTimer.Stop();
            displayScaleSettingsSaveTimer.Stop();
            editorViewSettingsSaveTimer.Stop();
            settingsSnapCommitTimer.Stop();
            settingsFixedSnapCommitTimer.Stop();
            navigationMarkerSaveTimer.Stop();
            shortcutSettingsSaveTimer.Stop();
            audioPanelSettingsSaveTimer.Stop();
            CancelScheduledEditorSeekCommit();
            editorHighRateFramePump.Dispose();
            DetachKeySoundActiveSession();
            keySoundRuntime.StateChanged -= KeySoundRuntime_StateChanged;
            await keySoundRuntime.DisposeAsync();
            keySoundRuntimeDisposed = true;
            await audioVisualization.DisposeAsync();
            await audioTransport.DisposeAsync();
            onlineCreatorAvatarLifetime.Cancel();
            if (onlineCreatorAvatarLoads.Count > 0)
            {
                try
                {
                    await Task.WhenAll(onlineCreatorAvatarLoads.Values);
                }
                catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is WebException || ex is OperationCanceledException || ex is JsonException) ? true : false)
                {
                }
            }

            timeline.SetOnlineCreators(new Dictionary<long, OnlineCreatorAppearance>());
            foreach (Bitmap value in onlineCreatorAvatarCache.Values)
            {
                value.Dispose();
            }

            onlineCreatorAvatarCache.Clear();
            onlineCreatorAvatarLoads.Clear();
            onlineCreatorAvatarLifetime.Dispose();
            onlineRestoreCancellation?.Cancel();
            onlineRestoreCancellation?.Dispose();
            onlineRestoreCancellation = null;
            followNotificationPollingCancellation.Cancel();
            chartingHeartbeatLifetime.Cancel();
            try
            {
                await activeChartingHeartbeatTask;
            }
            catch (OperationCanceledException)
            {
            }

            if (onlineAccountHost != null)
            {
                onlineAccountHost.Session.Changed -= OnlineAccountSession_Changed;
                onlineAccountHost.Session.NotificationsReceived -= OnlineAccountSession_NotificationsReceived;
                onlineAccountHost.Dispose();
            }

            followNotificationPollingCancellation.Dispose();
            chartingHeartbeatLifetime.Dispose();
        }
        catch (Exception)
        {
        }

        windowClosePreflightComplete = true;
        Close();
    }
    catch (Exception ex4)when (!windowCloseTeardownStarted)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("退出前检查失败，窗口保持打开：{0}", AvaloniaLocalizationCatalog.Text(ex4.Message)));
    }
    finally
    {
        windowClosePreflightInProgress = false;
        if (!windowClosePreflightComplete)
        {
            audioUiTimer.Start();
            ConfigureEditorFrameRateRuntime(resetCounters: true);
            if (keySoundSettingsDirty)
            {
                ScheduleKeySoundSettingsSave();
            }

            if (displayScaleSettingsDirty)
            {
                ScheduleDisplayScaleSettingsSave();
            }

            if (editorViewSettingsDirty)
            {
                ScheduleEditorViewSettingsSave();
            }

            if (shortcutSettingsDirty)
            {
                MarkShortcutSettingsDirty();
            }

            if (audioPanelSettingsDirty)
            {
                ScheduleAudioPanelSettingsSave();
            }

            if (navigationMarkerSettingsDirty)
            {
                ScheduleNavigationMarkerSave();
            }

            ConfigureAutoBackupTimer();
            roomInvitationTimer.Start();
            followNotificationTimer.Start();
            ReconcileChartingHeartbeatTimer();
            if (followNotificationToastVisible)
            {
                followNotificationToastTimer.Start();
            }

            if (fullscreenSuspendedForClosePreflight)
            {
                RestoreFullscreenAfterCanceledClosePreflight();
            }
        }
    }
}

private async Task StopBackgroundWorkForCloseAsync()
{
    difficultyAnalysisCancellation?.Cancel();
    await AwaitBackgroundWorkForCloseAsync(activeDifficultyAnalysisTask, "difficulty analysis");
    audioAnalysisCancellation?.Cancel();
    await AwaitBackgroundWorkForCloseAsync(activeAudioAnalysisTask, "tempo analysis");
    audioImportCancellation?.Cancel();
    await AwaitBackgroundWorkForCloseAsync(activeAudioImportTask, "audio import");
    imageImportCancellation?.Cancel();
    await AwaitBackgroundWorkForCloseAsync(activeImageImportTask, "image import");
    missingAudioRecoveryCancellation?.Cancel();
    await AwaitBackgroundWorkForCloseAsync(activeMissingAudioRecoveryTask, "missing audio recovery");
    titleRomanizationCancellation?.Cancel();
    artistRomanizationCancellation?.Cancel();
    await AwaitBackgroundWorkForCloseAsync(Task.WhenAll(activeTitleRomanizationTask, activeArtistRomanizationTask), "romanization");
    await AwaitBackgroundWorkForCloseAsync(activeBackupTask, "backup");
    if (exportInProgress)
    {
        viewModel.SetStatus("正在取消谱包导出…");
        activeExportCancellation?.Cancel();
        await AwaitBackgroundWorkForCloseAsync(activeExportTask, "export");
    }
}

private static async Task AwaitBackgroundWorkForCloseAsync(Task task, string operation)
{
    try
    {
        await task;
    }
    catch (Exception ex)
    {
        _ = ex is OperationCanceledException;
    }
}

private bool HasBlockingExternalApplicationShutdownWork()
{
    if (!exportInProgress && !audioAnalysisInProgress && !audioImportInProgress && !imageImportInProgress && !titleRomanizationInProgress && !artistRomanizationInProgress && !libraryDeletionInProgress && !autoBackupInProgress && !navigationMarkerSettingsDirty && activeDifficultyAnalysisTask.IsCompleted && activeExportTask.IsCompleted && activeAudioAnalysisTask.IsCompleted && activeAudioImportTask.IsCompleted && activeImageImportTask.IsCompleted && activeTitleRomanizationTask.IsCompleted && activeArtistRomanizationTask.IsCompleted)
    {
        return !activeBackupTask.IsCompleted;
    }

    return true;
}

private void ReleaseChartBackgroundForShutdown()
{
    if (WindowsClosePreflightResourcePolicy.CanReleaseEditorVisuals(windowCloseTeardownStarted))
    {
        backgroundImageLoadCancellation?.Cancel();
        timeline.SetChartBackgroundImage(null);
    }
}

private void WriteExternalApplicationShutdownEvidence(WindowClosingEventArgs eventArgs, bool hasUnsavedDocuments, bool hasBlockingWork, bool hasActiveOnlineRoom, bool canCloseImmediately)
{
    if (!evidenceChartMode || string.IsNullOrWhiteSpace(evidenceOutputPath))
    {
        return;
    }

    try
    {
        string path = evidenceOutputPath + ".shutdown.json";
        var value = new
        {
            schema = "lumachart.windows.avalonia.external-application-shutdown.v1",
            generatedUtc = DateTimeOffset.UtcNow,
            closeReason = eventArgs.CloseReason.ToString(),
            IsProgrammatic = eventArgs.IsProgrammatic,
            hasUnsavedDocuments = hasUnsavedDocuments,
            hasBlockingWork = hasBlockingWork,
            hasActiveOnlineRoom = hasActiveOnlineRoom,
            canCloseImmediately = canCloseImmediately,
            blockingWork = new
            {
                exportInProgress = exportInProgress,
                audioAnalysisInProgress = audioAnalysisInProgress,
                audioImportInProgress = audioImportInProgress,
                imageImportInProgress = imageImportInProgress,
                titleRomanizationInProgress = titleRomanizationInProgress,
                artistRomanizationInProgress = artistRomanizationInProgress,
                libraryDeletionInProgress = libraryDeletionInProgress,
                autoBackupInProgress = autoBackupInProgress,
                activeDifficultyAnalysis = !activeDifficultyAnalysisTask.IsCompleted,
                activeExport = !activeExportTask.IsCompleted,
                activeAudioAnalysis = !activeAudioAnalysisTask.IsCompleted,
                activeAudioImport = !activeAudioImportTask.IsCompleted,
                activeImageImport = !activeImageImportTask.IsCompleted,
                activeTitleRomanization = !activeTitleRomanizationTask.IsCompleted,
                activeArtistRomanization = !activeArtistRomanizationTask.IsCompleted,
                activeBackup = !activeBackupTask.IsCompleted
            }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
    catch
    {
    }
}

private async void StartEvidenceProbe()
{
    DesktopChartTab tab = viewModel.ActiveTab;
    if (tab == null || string.IsNullOrWhiteSpace(evidenceOutputPath))
    {
        viewModel.SetStatus("GUI evidence 未启动：没有打开真实谱面");
        return;
    }

    PersistPendingDisplayScaleSettings();
    if (evidenceFocusSettingsCache)
    {
        editorViewSettingsDirty = true;
        PersistPendingEditorViewSettings();
    }

    if (evidenceFocusAudioPanels)
    {
        audioPanelSettingsDirty = true;
        PersistPendingAudioPanelSettings();
    }

    TimingEvent[] array = (
        from timing in tab.Session.Document.Timing
        where timing.Effect.HasValue
        orderby timing.Beat, timing.Id
        select timing).ToArray();
    ChartNote[] array2 = (
        from note in tab.Session.Document.Notes
        where !note.IsDraft
        orderby note.Beat, note.Lane, note.Id
        select note).ToArray();
    ChartNote[] array3 = (
        from note in array2
        where note.EndBeat.HasValue
        orderby note.EndBeat, note.Lane
        select note).ToArray();
    ChartNote[] array4 = array2.Where((ChartNote note) => !note.EndBeat.HasValue).ToArray();
    BeatPosition? beatPosition = evidenceFocusGroupBeat;
    ChartNote? chartNote;
    if (beatPosition.HasValue)
    {
        BeatPosition authoredGroupFocusBeat = beatPosition.GetValueOrDefault();
        chartNote = array4.FirstOrDefault((ChartNote note) => note.Beat.Bar == authoredGroupFocusBeat.Bar && note.Beat.Numerator == authoredGroupFocusBeat.Numerator && note.Beat.Denominator == authoredGroupFocusBeat.Denominator);
    }
    else
    {
        chartNote = array4.ElementAtOrDefault(array4.Length / 2);
    }

    ChartNote chartNote2 = chartNote;
    if (evidenceFocusChartValidation)
    {
        DesktopChartValidationPresentation desktopChartValidationPresentation = (lastChartValidationPresentation = DesktopChartValidationPresenter.Create(tab.Session.Document, tab.HiddenGroups));
        tab.Session.Selection.Clear();
        timeline.SetSelectedTiming(null);
        evidenceBaseBeat = desktopChartValidationPresentation.FirstIssue?.Beat.Value ?? 0.0;
    }
    else if (evidenceFocusProEditor)
    {
        evidenceBaseBeat = 0.5;
    }
    else if (evidenceFocusGroup && chartNote2 != null)
    {
        ChartNote chartNote3 = chartNote2;
        tab.Session.Selection.Clear();
        tab.Session.Selection.Add(chartNote3.Id);
        timeline.SetSelectedTiming(null);
        evidenceBaseBeat = chartNote3.Beat.Value;
        editorInspectorTabs.SelectedItem = groupInspectorTab;
    }
    else if (!TryApplyEvidenceNotePropertyFocus(tab))
    {
        if (evidenceFocusHoldTail && array3.Length != 0)
        {
            ChartNote chartNote4 = array3[array3.Length / 2];
            tab.Session.Selection.Clear();
            tab.Session.Selection.Add(chartNote4.Id);
            timeline.SetSelectedTiming(null);
            evidenceBaseBeat = chartNote4.EndBeat.Value.Value;
        }
        else if (evidenceFocusTiming && array.Length != 0)
        {
            TimingEvent timingEvent = array[array.Length / 2];
            tab.Session.Selection.Clear();
            timeline.SetSelectedTiming(timingEvent.Id);
            evidenceBaseBeat = timingEvent.Beat.Value;
            editorInspectorTabs.SelectedItem = timingInspectorTab;
        }
        else if (array2.Length != 0)
        {
            ChartNote chartNote5 = array2[array2.Length / 2];
            tab.Session.Selection.Clear();
            tab.Session.Selection.Add(chartNote5.Id);
            evidenceBaseBeat = chartNote5.Beat.Value;
        }
        else
        {
            evidenceBaseBeat = 0.0;
        }
    }

    FocusKeySoundEvidencePanel();
    FocusDisplayScaleEvidencePanel();
    FocusSettingsCacheEvidencePanel();
    if (evidenceUsePlacementProbeViewport)
    {
        evidenceBaseBeat = 0.0;
    }

    timeline.SetCurrentBeat(evidenceBaseBeat);
    UpdateInspector();
    if (evidenceFocusAudioPanels || evidenceEnableBackgroundWaveform || evidenceEnableBackgroundSpectrogram)
    {
        viewModel.SetStatus("正在等待真实音频波形 / 频谱后台生成…");
        try
        {
            await audioVisualization.PendingLoad.WaitAsync(TimeSpan.FromSeconds(45.0));
        }
        catch (TimeoutException)
        {
            viewModel.SetStatus("音频可视化生成超过 45 秒；将写入未就绪状态供门禁拒绝");
        }
        catch (OperationCanceledException)
        {
            viewModel.SetStatus("音频可视化生成被新的标签或源 revision 取代");
        }

        UpdateAudioVisualizationUi();
        DateTimeOffset sceneDeadline = DateTimeOffset.UtcNow.AddSeconds(8.0);
        while (audioVisualization.LastError == null && !AreRequiredAudioVisualizationScenesReady() && DateTimeOffset.UtcNow < sceneDeadline)
        {
            timeline.InvalidateVisual();
            waveformSideSurface.InvalidateVisual();
            spectrogramSideSurface.InvalidateVisual();
            await Task.Delay(50);
        }
    }

    timeline.Performance.Reset();
    waveformSideSurface.Performance.Reset();
    spectrogramSideSurface.Performance.Reset();
    evidenceTicks = 0;
    evidenceStopwatch = new Stopwatch();
    int warmupTicks = 0;
    bool recording = false;
    evidenceTimer = new DispatcherTimer(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(4.0)
    };
    evidenceTimer.Tick += async (object? _, EventArgs _) =>
    {
        if (!recording)
        {
            warmupTicks++;
            double num = (double)(warmupTicks % 120 - 60) / 60.0;
            timeline.MarkSyntheticInput();
            timeline.SetCurrentBeat(Math.Max(0.0, evidenceBaseBeat + num * 2.0));
            if (warmupTicks >= 120)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                timeline.Performance.Reset();
                waveformSideSurface.Performance.Reset();
                spectrogramSideSurface.Performance.Reset();
                evidenceTicks = 0;
                evidenceStopwatch.Restart();
                recording = true;
                viewModel.SetStatus("预热完成，正在采集稳态窗口自绘性能…");
            }
        }
        else
        {
            evidenceTicks++;
            double num2 = (double)(evidenceTicks % 240 - 120) / 120.0;
            timeline.MarkSyntheticInput();
            timeline.SetCurrentBeat(Math.Max(0.0, evidenceBaseBeat + num2 * 2.0));
            if (timeline.Performance.Snapshot().RenderMilliseconds.Samples >= 240 || !(evidenceStopwatch.Elapsed < TimeSpan.FromSeconds(12.0)))
            {
                evidenceTimer.Stop();
                evidenceStopwatch.Stop();
                timeline.SetCurrentBeat(evidenceBaseBeat);
                GuiRuntimeEvidence evidence = GuiRuntimeEvidenceWriter.Capture(this, timeline, audioTransport, audioVisualization, tab, tab.SourcePath, (evidenceSyntheticNoteCount > 0) ? "generated-100000-note-render-workload" : "real-chart-document", (evidenceSyntheticNoteCount > 0) ? new int? (evidenceSyntheticNoteCount) : ((int? )null), evidenceStopwatch.Elapsed, warmupTicks, evidenceTicks, 240, keySoundRuntime, keySoundSettings, displayScaleController, displayScaleSettings, CaptureAudioPanelsRuntime(), CaptureLibraryDeletionRuntime(), CaptureChartValidationRuntime(), evidenceSuppressExportReveal, evidenceFollowActiveTab, evidenceFilePickerStartDirectory != null, evidenceFilePickerStartDirectory);
                try
                {
                    await GuiRuntimeEvidenceWriter.WriteAtomicAsync(evidenceOutputPath, evidence);
                    viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("GUI evidence 已写入 · Render {0} 帧", evidence.Timeline.Performance.RenderMilliseconds.Samples));
                    if (!string.IsNullOrWhiteSpace(evidenceInteractionOutputPath))
                    {
                        await Task.Delay(evidenceInteractionDelayMilliseconds);
                        DesktopChartTab desktopChartTab = (evidenceFollowActiveTab ? (viewModel.ActiveTab ?? tab) : tab);
                        if (evidenceFollowActiveTab && desktopChartTab != tab)
                        {
                            TryApplyEvidenceNotePropertyFocus(desktopChartTab);
                        }

                        GuiRuntimeEvidence interactionEvidence = GuiRuntimeEvidenceWriter.Capture(this, timeline, audioTransport, audioVisualization, desktopChartTab, desktopChartTab.SourcePath, (evidenceSyntheticNoteCount > 0) ? "generated-100000-note-render-workload" : "real-chart-document", (evidenceSyntheticNoteCount > 0) ? new int? (evidenceSyntheticNoteCount) : ((int? )null), evidenceStopwatch.Elapsed, warmupTicks, evidenceTicks, 240, keySoundRuntime, keySoundSettings, displayScaleController, displayScaleSettings, CaptureAudioPanelsRuntime(), CaptureLibraryDeletionRuntime(), CaptureChartValidationRuntime(), evidenceSuppressExportReveal, evidenceFollowActiveTab, evidenceFilePickerStartDirectory != null, evidenceFilePickerStartDirectory);
                        await GuiRuntimeEvidenceWriter.WriteAtomicAsync(evidenceInteractionOutputPath, interactionEvidence);
                        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("GUI 实际输入 evidence 已写入 · Beat {0:0.###}", interactionEvidence.Timeline.CurrentBeat));
                    }
                }
                catch (Exception ex3)
                {
                    viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("GUI evidence 写入失败：{0}", AvaloniaLocalizationCatalog.Text(ex3.Message)));
                }
            }
        }
    };
    evidenceTimer.Start();
    viewModel.SetStatus("正在预热 Windows 实际窗口自绘路径…");
}

private async void StartLibraryVirtualizationEvidenceProbe()
{
    if (!evidenceFocusLibraryVirtualization || string.IsNullOrWhiteSpace(evidenceOutputPath))
    {
        return;
    }

    if (viewModel.LibraryFolders.Count == 0)
    {
        viewModel.SetStatus("大曲库 evidence 未启动：隔离曲库为空");
        return;
    }

    try
    {
        ShowLibrary();
        UpdateUi();
        await YieldToLibraryRenderAsync();
        string panelType = libraryList.GetVisualDescendants().FirstOrDefault((Visual control) => control is VirtualizingStackPanel)?.GetType().FullName;
        LibraryRealizedContainerSnapshot initialContainers = CaptureRealizedLibraryContainers();
        List<int> realizedCounts = new List<int>(180);
        List<double> scrollSettleMilliseconds = new List<double>(180);
        List<double> inputQueueDelayMilliseconds = new List<double>(180);
        HashSet<ListBoxItem> observedItems = new HashSet<ListBoxItem>(ReferenceEqualityComparer.Instance);
        HashSet<LibraryFolderCard> observedCards = new HashSet<LibraryFolderCard>(ReferenceEqualityComparer.Instance);
        int staleBindingMismatchCount = 0;
        int maximumMaterializedCollapsedChartRows = 0;
        int[] array = new int[4]
        {
            0,
            viewModel.LibraryFolders.Count - 1,
            viewModel.LibraryFolders.Count / 2,
            0
        };
        foreach (int index in array)
        {
            libraryList.ScrollIntoView(index);
            await YieldToLibraryRenderAsync();
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long managedBytesBefore = GC.GetTotalMemory(forceFullCollection: false);
        long managedBytesPeak = managedBytesBefore;
        long privateBytesBefore = process.PrivateMemorySize64;
        long workingSetBytesBefore = process.WorkingSet64;
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("正在测量 {0:N0} 项曲库回收与滚动响应…", viewModel.LibraryFolders.Count));
        for (int num = 0; num < 180; num++)
        {
            int num2 = (int)((long)num * 7919L % viewModel.LibraryFolders.Count);
            if ((num & 1) != 0)
            {
                num2 = viewModel.LibraryFolders.Count - 1 - num2;
            }

            Stopwatch inputStopwatch = Stopwatch.StartNew();
            DispatcherOperation<double> dispatcherOperation = global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => inputStopwatch.Elapsed.TotalMilliseconds, DispatcherPriority.Input);
            Stopwatch scrollStopwatch = Stopwatch.StartNew();
            libraryList.ScrollIntoView(num2);
            double inputDelay = await dispatcherOperation;
            await YieldToLibraryRenderAsync();
            scrollStopwatch.Stop();
            inputQueueDelayMilliseconds.Add(inputDelay);
            scrollSettleMilliseconds.Add(scrollStopwatch.Elapsed.TotalMilliseconds);
            LibraryRealizedContainerSnapshot libraryRealizedContainerSnapshot = CaptureRealizedLibraryContainers();
            realizedCounts.Add(libraryRealizedContainerSnapshot.ListBoxItems.Count);
            foreach (ListBoxItem listBoxItem in libraryRealizedContainerSnapshot.ListBoxItems)
            {
                observedItems.Add(listBoxItem);
            }

            foreach (LibraryFolderCard card in libraryRealizedContainerSnapshot.Cards)
            {
                observedCards.Add(card);
                LibraryFolderCardDiagnostics libraryFolderCardDiagnostics = card.CaptureDiagnostics();
                if (!libraryFolderCardDiagnostics.IsExpanded)
                {
                    maximumMaterializedCollapsedChartRows = Math.Max(maximumMaterializedCollapsedChartRows, libraryFolderCardDiagnostics.MaterializedChartRowCount);
                }

                if (!(card.DataContext is DesktopLibraryFolderView desktopLibraryFolderView) || !string.Equals(libraryFolderCardDiagnostics.FolderPath, desktopLibraryFolderView.Folder.FolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    staleBindingMismatchCount++;
                }
            }

            managedBytesPeak = Math.Max(managedBytesPeak, GC.GetTotalMemory(forceFullCollection: false));
        }

        libraryList.ScrollIntoView(viewModel.LibraryFolders.Count / 2);
        await YieldToLibraryRenderAsync();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        process.Refresh();
        long managedBytesAfter = GC.GetTotalMemory(forceFullCollection: false);
        long privateBytesAfter = process.PrivateMemorySize64;
        long workingSetBytesAfter = process.WorkingSet64;
        LibraryRealizedContainerSnapshot libraryRealizedContainerSnapshot2 = CaptureRealizedLibraryContainers();
        LibraryFolderCard libraryFolderCard = libraryRealizedContainerSnapshot2.Cards.ElementAtOrDefault(Math.Min(2, libraryRealizedContainerSnapshot2.Cards.Count - 1)) ?? throw new InvalidOperationException("No realized recyclable library card was available for GUI expansion.");
        DesktopLibraryFolderView evidenceView = (libraryFolderCard.DataContext as DesktopLibraryFolderView) ?? throw new InvalidOperationException("The realized evidence card had no folder data context.");
        if (!libraryFolderCard.SetExpanded(expanded: true))
        {
            throw new InvalidOperationException("The realized evidence card rejected expansion.");
        }

        int evidenceFolderIndex = viewModel.LibraryFolders.IndexOf(evidenceView);
        if (evidenceFolderIndex < 0)
        {
            throw new InvalidOperationException("The expanded evidence folder was not present in the current library.");
        }

        await YieldToLibraryRenderAsync();
        libraryList.ScrollIntoView(evidenceFolderIndex);
        await YieldToLibraryRenderAsync();
        LibraryFolderCardDiagnostics libraryFolderCardDiagnostics2 = (CaptureRealizedLibraryContainers().Cards.FirstOrDefault((LibraryFolderCard card) => string.Equals(card.CaptureDiagnostics().FolderPath, evidenceView.Folder.FolderPath, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("The expanded evidence folder did not rematerialize after recycling.")).CaptureDiagnostics();
        DesktopLibraryChartEntry desktopLibraryChartEntry = evidenceView.VisibleCharts.FirstOrDefault() ?? throw new InvalidOperationException("The realized evidence folder had no visible chart.");
        bool evidenceExpandedBindingMatched = string.Equals(libraryFolderCardDiagnostics2.FolderPath, evidenceView.Folder.FolderPath, StringComparison.OrdinalIgnoreCase) && libraryFolderCardDiagnostics2.MaterializedChartRowCount == evidenceView.VisibleCharts.Count;
        WindowsLibraryVirtualizationRuntimeEvidence evidence = new WindowsLibraryVirtualizationRuntimeEvidence("lumachart.windows.avalonia.library-virtualization-runtime.v1", DateTimeOffset.UtcNow, "captured-migration-incomplete", "Windows Avalonia only", Environment.ProcessId, viewModel.ResolvedWorkspaceRoot, viewModel.LibraryEntries.Count, viewModel.IndexedLibraryFolderCount, viewModel.LibraryFolders.Count, evidenceLibraryInitializationMilliseconds, ContainerTemplateOwnsStableCard: true, FuncDataTemplateUsed: false, panelType, 180, initialContainers.ListBoxItems.Count, realizedCounts.Min(), realizedCounts.Max(), observedItems.Count, observedCards.Count, staleBindingMismatchCount, maximumMaterializedCollapsedChartRows, evidenceView.Folder.FolderPath, evidenceView.Folder.DisplayTitle, libraryFolderCardDiagnostics2.MaterializedChartRowCount, desktopLibraryChartEntry.ChartPath, evidenceExpandedBindingMatched, SummarizeLibraryMetric(scrollSettleMilliseconds), SummarizeLibraryMetric(inputQueueDelayMilliseconds), new WindowsLibraryVirtualizationMemoryEvidence(managedBytesBefore, managedBytesAfter, managedBytesPeak, privateBytesBefore, privateBytesAfter, workingSetBytesBefore, workingSetBytesAfter), RenderScaling, null);
        await WindowsLibraryVirtualizationRuntimeEvidenceWriter.WriteAtomicAsync(evidenceOutputPath, evidence);
        AutomationProperties.SetHelpText(libraryList, $"folders={evidence.VisibleFolderCount}; containers={evidence.MaximumRealizedContainerCount}; scrollP95={evidence.ScrollSettleMilliseconds.P95:0.00}ms; inputP95={evidence.InputQueueDelayMilliseconds.P95:0.00}ms; stale={evidence.StaleBindingMismatchCount}");
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("{0:N0} 项回收完成 · 容器≤{1} · 滚动 P95 {2:0.0}ms · 输入 P95 {3:0.0}ms", evidence.VisibleFolderCount, evidence.MaximumRealizedContainerCount, evidence.ScrollSettleMilliseconds.P95, evidence.InputQueueDelayMilliseconds.P95));
    }
    catch (Exception ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("大曲库 evidence 失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private async Task YieldToLibraryRenderAsync()
{
    await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
    {
    }, DispatcherPriority.Render);
    await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
    {
    }, DispatcherPriority.Background);
}

private LibraryRealizedContainerSnapshot CaptureRealizedLibraryContainers()
{
    Visual[] source = libraryList.GetVisualDescendants().ToArray();
    return new LibraryRealizedContainerSnapshot(source.OfType<ListBoxItem>().ToArray(), source.OfType<LibraryFolderCard>().ToArray());
}

private static WindowsLibraryVirtualizationMetricSummary SummarizeLibraryMetric(IReadOnlyCollection<double> values)
{
    if (values.Count == 0)
    {
        return new WindowsLibraryVirtualizationMetricSummary(0, 0.0, 0.0, 0.0, 0.0, 0.0);
    }

    double[] array = values.Order().ToArray();
    return new WindowsLibraryVirtualizationMetricSummary(array.Length, array[0], Percentile(array, 0.5), Percentile(array, 0.95), Percentile(array, 0.99), array[^1]);
    static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        int index = Math.Clamp((int)Math.Ceiling((double)sorted.Count * percentile) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }
}

private bool TryApplyEvidenceNotePropertyFocus(DesktopChartTab tab)
{
    if (!evidenceChartMode || !evidenceFocusNoteProperty)
    {
        return false;
    }

    ChartNote chartNote = EvidenceNoteFocusPolicy.Resolve(tab.Session.Document.Notes, evidenceFocusNotePropertyBeat);
    if (chartNote == null)
    {
        return false;
    }

    tab.Session.Selection.Clear();
    tab.Session.Selection.Add(chartNote.Id);
    timeline.SetSelectedTiming(null);
    evidenceBaseBeat = chartNote.Beat.Value;
    timeline.SetCurrentBeat(evidenceBaseBeat);
    editorInspectorTabs.SelectedItem = noteInspectorTab;
    UpdateInspector();
    return true;
}

private GuiAudioPanelsRuntimeCapture CaptureAudioPanelsRuntime()
{
    return new GuiAudioPanelsRuntimeCapture(audioPanelSettings, waveformPanel, waveformSideSurface, spectrogramPanel, spectrogramSideSurface);
}

private GuiLibraryDeletionRuntimeCapture CaptureLibraryDeletionRuntime()
{
    return new GuiLibraryDeletionRuntimeCapture(viewModel.ResolvedWorkspaceRoot, libraryList.SelectionMode.ToString(), viewModel.IndexedLibraryFolderCount, viewModel.LibraryFolders.Count, libraryList.SelectedItems?.OfType<DesktopLibraryFolderView>().Count() ?? 0, libraryDeleteSelectedButton.IsEnabled, libraryDeleteSelectedButton.Content?.ToString() ?? string.Empty, libraryDeletionInProgress, popupScaleController.LastDiagnostics, lastLibraryDeletionPlan, lastLibraryDeleteDialogScale, lastLibraryRecycleResults, lastLibraryRecycleFailures, lastLibraryDeletionConfirmed, lastLibraryDeletionCanceled);
}

private GuiChartValidationRuntimeCapture? CaptureChartValidationRuntime()
{
    if (!chartValidationEvidenceInjected && !chartValidationManualCheckShown && !chartValidationSaveBlockedShown && lastChartValidationPresentation == null)
    {
        return null;
    }

    return new GuiChartValidationRuntimeCapture(lastChartValidationPresentation, lastChartValidationNavigation, chartValidationManualCheckShown, chartValidationSaveBlockedShown, chartValidationManualDialogScale, chartValidationSaveDialogScale, lastChartValidationDialogMode);
}

private bool AreRequiredAudioVisualizationScenesReady()
{
    WindowsAudioPanelState waveform = audioPanelSettings.Waveform;
    WindowsAudioPanelState spectrogram = audioPanelSettings.Spectrogram;
    if (((!waveform.Collapsed && waveform.Mode == WindowsAudioPanelDisplayMode.Background) || (!spectrogram.Collapsed && spectrogram.Mode == WindowsAudioPanelDisplayMode.Background)) && !timeline.GetAudioBackgroundDiagnostics().SceneReady)
    {
        return false;
    }

    if (!waveform.Collapsed && waveform.Mode == WindowsAudioPanelDisplayMode.Side && !waveformSideSurface.GetDiagnostics().SceneReady)
    {
        return false;
    }

    if (!spectrogram.Collapsed && spectrogram.Mode == WindowsAudioPanelDisplayMode.Side && !spectrogramSideSurface.GetDiagnostics().SceneReady)
    {
        return false;
    }

    return true;
}

private void PrepareSyntheticEvidenceWorkload()
{
    if (evidenceSyntheticNoteCount <= 0)
    {
        return;
    }

    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab != null)
    {
        ChartDocument chartDocument = activeTab.Session.Document.Clone();
        chartDocument.Metadata.Title = $"{chartDocument.Metadata.Title} · {evidenceSyntheticNoteCount:N0} Note Probe";
        chartDocument.Notes = new List<ChartNote>(evidenceSyntheticNoteCount);
        for (int i = 0; i < evidenceSyntheticNoteCount; i++)
        {
            BeatPosition beat = BeatPosition.Preserve(i / 32, i % 32, 32);
            chartDocument.Notes.Add(new ChartNote { Lane = i % chartDocument.KeyCount, Beat = beat, EndBeat = ((i % 17 == 0) ? new BeatPosition? (BeatPosition.Preserve(i / 32, i % 32 + 16, 32)) : ((BeatPosition? )null)) });
        }

        activeTab.Session.ReplaceDocument(chartDocument);
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已生成 {0:N0} Note 的 Windows GUI 渲染负载", evidenceSyntheticNoteCount));
    }
}

private void PrepareChartValidationEvidenceWorkload()
{
    if (!evidenceChartMode || !evidenceFocusChartValidation)
    {
        return;
    }

    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null)
    {
        return;
    }

    if (evidenceSyntheticNoteCount > 0)
    {
        throw new InvalidOperationException("Chart-validation evidence requires an unchanged real-chart baseline; synthetic notes are not allowed.");
    }

    ChartDocument chartDocument = activeTab.Session.Document.Clone();
    DesktopChartValidationPresentation desktopChartValidationPresentation = DesktopChartValidationPresenter.Create(chartDocument, Array.Empty<int>());
    if (!desktopChartValidationPresentation.CanSave)
    {
        throw new InvalidOperationException($"Chart-validation evidence requires a valid baseline chart, but found {desktopChartValidationPresentation.TotalIssueCount} issue(s).");
    }

    if (chartDocument.Notes.Any((ChartNote note) =>
    {
        int num2 = note.Group;
        return (uint)(num2 - 7) <= 1u;
    }))
    {
        throw new InvalidOperationException("Chart-validation evidence reserves empty groups 7 and 8 for deterministic hidden-group navigation.");
    }

    int num = checked((int)Math.Ceiling(chartDocument.Notes.SelectMany((ChartNote note) =>
    {
        BeatPosition? endBeat = note.EndBeat;
        if (!endBeat.HasValue)
        {
            return new double[1]
            {
                note.Beat.Value
            };
        }

        BeatPosition valueOrDefault = endBeat.GetValueOrDefault();
        return new double[2]
        {
            note.Beat.Value,
            valueOrDefault.Value
        };
    }).Concat(chartDocument.Timing.Select((TimingEvent timing) => timing.Beat.Value)).DefaultIfEmpty(0.0).Max()) + 8);
    BeatPosition beat = BeatPosition.Preserve(num, 0, 1);
    chartDocument.Notes.Add(new ChartNote { Lane = 0, Beat = beat, Group = 7 });
    chartDocument.Notes.Add(new ChartNote { Lane = 0, Beat = beat, Group = 7 });
    int lane = Math.Min(1, Math.Max(0, chartDocument.KeyCount - 1));
    chartDocument.Notes.Add(new ChartNote { Lane = lane, Beat = BeatPosition.Preserve(num + 4, 0, 1), EndBeat = BeatPosition.Preserve(num + 8, 0, 1), Group = 8 });
    chartDocument.Notes.Add(new ChartNote { Lane = lane, Beat = BeatPosition.Preserve(num + 6, 0, 1), EndBeat = BeatPosition.Preserve(num + 10, 0, 1), Group = 8 });
    DesktopChartValidationPresentation desktopChartValidationPresentation2 = DesktopChartValidationPresenter.Create(chartDocument, new int[2] { 7, 8 });
    if (desktopChartValidationPresentation2.TotalIssueCount != 2 || desktopChartValidationPresentation2.OverlappingNoteCount != 1 || desktopChartValidationPresentation2.CrossingLongNoteCount != 1)
    {
        throw new InvalidOperationException("Chart-validation evidence injection did not produce exactly one overlap and one crossing-LN issue.");
    }

    activeTab.Session.ReplaceDocument(chartDocument, markDirty: true);
    activeTab.HiddenGroups.Add(7);
    activeTab.HiddenGroups.Add(8);
    chartValidationEvidenceInjected = true;
    viewModel.SetStatus("已注入 2 项内存谱面检查 evidence；源文件保持未写入");
}

private async void Window_KeyDown(object? sender, KeyEventArgs eventArgs)
{
    if (playtestInProgress)
    {
        eventArgs.Handled = true;
        return;
    }

    if (eventArgs.Key == Key.Return && eventArgs.KeyModifiers == KeyModifiers.None && !IsTextEditingEvent(eventArgs) && (pendingPasteSurface?.IsPatternPaste ?? false))
    {
        eventArgs.Handled = true;
        if (oneShotShortcutState.TryPress(Key.Return))
        {
            pendingPasteSurface.HandlePatternPasteKey(Key.Return);
        }

        return;
    }

    if (eventArgs.Key == Key.Escape && SymmetryTargetSurface.IsStretching)
    {
        SymmetryTargetSurface.CancelStretch();
        eventArgs.Handled = true;
        return;
    }

    if (eventArgs.Key == Key.Escape && pendingNotePaste != null)
    {
        CancelNotePaste();
        eventArgs.Handled = true;
        return;
    }

    if (noteSoundExistingPopup.IsOpen)
    {
        NoteSoundPicker_KeyDown(sender, eventArgs);
        if (eventArgs.Handled)
        {
            return;
        }
    }

    bool flag = IsTextEditingEvent(eventArgs);
    bool flag2 = eventArgs.Key == Key.Space && eventArgs.KeyModifiers == KeyModifiers.None && !(WindowsAvaloniaEditorInputPolicy.FindTextEditingControl(eventArgs.Source as Control) is TextBox) && !(WindowsAvaloniaEditorInputPolicy.FindTextEditingControl(FocusManager?.GetFocusedElement() as Control) is TextBox);
    imeDuplicateCommitGuard.ObserveKeyDown(eventArgs.Key, eventArgs.KeyModifiers, flag, Environment.TickCount64);
    if ((!flag2 & flag) && WindowsAvaloniaEditorInputPolicy.DeferToTextInputMethod(eventArgs.Key, eventArgs.KeyModifiers))
    {
        return;
    }

    Key key = eventArgs.Key;
    PhysicalKey physicalKey = eventArgs.PhysicalKey;
    string keySymbol = eventArgs.KeySymbol;
    Key key2 = eventArgs.Key;
    bool flag3 = (((uint)(key2 - 155) <= 1u || key2 == Key.DeadCharProcessed) ? true : false);
    Key key3 = WindowsEditorShortcutGesture.ResolveReportedKey(key, physicalKey, keySymbol, flag3 ? WindowsShortcutPhysicalKeyState.TryResolvePressedKey() : ((Key? )null));
    if (onlinePlayerPopupLayer.IsVisible && key3 == Key.Escape)
    {
        CloseOnlinePlayerPopup();
        eventArgs.Handled = true;
    }
    else
    {
        if (WindowsOnlineSurfaceParityPolicy.SuppressEditorShortcutRouting(onlineSurfaceLayer.IsVisible || onlinePlayerPopupLayer.IsVisible || aboutLayer.IsVisible || updateLayer.IsVisible))
        {
            return;
        }

        if (libraryView.IsVisible)
        {
            if (key3 == Key.F && eventArgs.KeyModifiers == KeyModifiers.Control)
            {
                librarySearchBox.Focus();
                librarySearchBox.SelectAll();
                eventArgs.Handled = true;
            }
        }
        else if (capturingShortcutAction != null)
        {
            eventArgs.Handled = true;
            if (((uint)(key3 - 155) <= 1u || key3 == Key.DeadCharProcessed) ? true : false)
            {
                shortcutStatusText.Text = AvaloniaLocalizationCatalog.Format("中文输入法按键尚未解析：Key={0} · Physical={1} · Symbol={2}", eventArgs.Key, eventArgs.PhysicalKey, eventArgs.KeySymbol ?? AvaloniaLocalizationCatalog.Text("(空)"));
            }
            else
            {
                await HandleShortcutCaptureAsync(key3, eventArgs.KeyModifiers);
            }
        }
        else if (key3 == Key.Escape && WindowState == WindowState.FullScreen)
        {
            ToggleFullscreen();
            eventArgs.Handled = true;
        }
        else if (flag2)
        {
            eventArgs.Handled = true;
            if (oneShotShortcutState.TryPress(Key.Space))
            {
                try
                {
                    await ToggleAudioPlaybackAsync();
                }
                catch (Exception ex)
                {
                    viewModel.SetStatus("播放切换失败：" + ex.Message);
                }
            }
        }
        else
        {
            if (flag && IsTextEditingShortcut(key3, eventArgs.KeyModifiers))
            {
                return;
            }

            WindowsEditorSpacePanKeyDownAction windowsEditorSpacePanKeyDownAction = spacePanGestureState.HandleKeyDown(key3, eventArgs.KeyModifiers, timeline.ProEditorEnabled, timeline.IsPointerOver);
            if (windowsEditorSpacePanKeyDownAction != WindowsEditorSpacePanKeyDownAction.None)
            {
                if (windowsEditorSpacePanKeyDownAction == WindowsEditorSpacePanKeyDownAction.Arm)
                {
                    timeline.SetSpacePanModifier(enabled: true);
                }

                eventArgs.Handled = true;
                return;
            }

            WindowsEditorShortcutDescriptor windowsEditorShortcutDescriptor = WindowsEditorShortcutGesture.Resolve(shortcutSettings, key3, eventArgs.KeyModifiers, flag);
            if (windowsEditorShortcutDescriptor == null)
            {
                return;
            }

            eventArgs.Handled = true;
            if (windowsEditorShortcutDescriptor.Id.Equals("PlayPause", StringComparison.OrdinalIgnoreCase) && !oneShotShortcutState.TryPress(key3))
            {
                return;
            }

            try
            {
                await ExecuteShortcutAsync(windowsEditorShortcutDescriptor.Id);
            }
            catch (Exception ex2)
            {
                if (!windowCloseTeardownStarted)
                {
                    viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("快捷键操作失败：{0}", AvaloniaLocalizationCatalog.Text(ex2.Message)));
                }
            }
        }
    }
}

private void Window_TextInput(object? sender, TextInputEventArgs eventArgs)
{
    if (!eventArgs.Handled)
    {
        Control control = WindowsAvaloniaEditorInputPolicy.FindTextEditingControl(eventArgs.Source as Control) ?? WindowsAvaloniaEditorInputPolicy.FindTextEditingControl(FocusManager?.GetFocusedElement() as Control);
        if (control == null)
        {
            imeDuplicateCommitGuard.Reset();
        }
        else if (imeDuplicateCommitGuard.ShouldSuppressReplay(control, eventArgs.Text, Environment.TickCount64))
        {
            eventArgs.Handled = true;
        }
    }
}

private async Task HandleShortcutCaptureAsync(Key key, KeyModifiers modifiers)
{
    string action = capturingShortcutAction;
    if (action == null)
    {
        return;
    }

    WindowsEditorShortcutCaptureIntent windowsEditorShortcutCaptureIntent = WindowsEditorShortcutGesture.InterpretCapture(key, modifiers);
    if (windowsEditorShortcutCaptureIntent.Kind == WindowsEditorShortcutCaptureKind.Cancel)
    {
        CancelShortcutCapture();
    }
    else
    {
        if (windowsEditorShortcutCaptureIntent.Kind == WindowsEditorShortcutCaptureKind.IgnoreModifier)
        {
            return;
        }

        if (windowsEditorShortcutCaptureIntent.Kind == WindowsEditorShortcutCaptureKind.Clear)
        {
            shortcutSettings.Assign(action, string.Empty, replaceConflict: true);
            capturingShortcutAction = null;
            shortcutStatusText.Text = AvaloniaLocalizationCatalog.Format("已清除“{0}”快捷键", ShortcutLabel(action));
            MarkShortcutSettingsDirty();
            RefreshShortcutButtons();
            RefreshShortcutLabels();
            return;
        }

        string captured = windowsEditorShortcutCaptureIntent.Gesture;
        WindowsEditorShortcutAssignmentResult windowsEditorShortcutAssignmentResult = shortcutSettings.Assign(action, captured, replaceConflict: false);
        if (!windowsEditorShortcutAssignmentResult.Applied && !string.IsNullOrWhiteSpace(windowsEditorShortcutAssignmentResult.ConflictAction))
        {
            string conflictAction = windowsEditorShortcutAssignmentResult.ConflictAction;
            if (!(await ShowSettingsConfirmationAsync("快捷键冲突", AvaloniaLocalizationCatalog.Format("{0} 已用于“{1}”。\n\n是否改为当前操作并清除原绑定？", captured, ShortcutLabel(conflictAction)), "改为当前操作", dangerous: false)))
            {
                shortcutStatusText.Text = AvaloniaLocalizationCatalog.Format("{0} 仍保留给“{1}”", captured, ShortcutLabel(conflictAction));
                RefreshShortcutButtons();
                return;
            }

            windowsEditorShortcutAssignmentResult = shortcutSettings.Assign(action, captured, replaceConflict: true);
        }

        if (windowsEditorShortcutAssignmentResult.Applied)
        {
            capturingShortcutAction = null;
            shortcutStatusText.Text = ((windowsEditorShortcutAssignmentResult.ConflictAction == null) ? AvaloniaLocalizationCatalog.Format("“{0}”已设为 {1}", ShortcutLabel(action), captured) : AvaloniaLocalizationCatalog.Format("“{0}”已设为 {1}；原冲突绑定已清除", ShortcutLabel(action), captured));
            MarkShortcutSettingsDirty();
            RefreshShortcutButtons();
            RefreshShortcutLabels();
        }
    }
}

private async Task ExecuteShortcutAsync(string action)
{
    if (!TryExecuteTrackCommand(action))
    {
        switch (WindowsEditorShortcutRouting.Resolve(action).Route)
        {
            case WindowsEditorShortcutRoute.New:
                await CreateChartAsync();
                break;
            case WindowsEditorShortcutRoute.Open:
                await PickAndOpenChartAsync();
                break;
            case WindowsEditorShortcutRoute.Save:
                await SaveAndVerifyWithPreflightAsync();
                break;
            case WindowsEditorShortcutRoute.SaveAs:
                await PickAndSaveAsAsync();
                break;
            case WindowsEditorShortcutRoute.Undo:
                ExecuteHistoryCommand(redo: false);
                break;
            case WindowsEditorShortcutRoute.Redo:
                ExecuteHistoryCommand(redo: true);
                break;
            case WindowsEditorShortcutRoute.Cut:
                CutMixedSelection();
                break;
            case WindowsEditorShortcutRoute.Copy:
                CopyMixedSelection();
                break;
            case WindowsEditorShortcutRoute.Paste:
                PasteMixedSelection();
                break;
            case WindowsEditorShortcutRoute.SelectAll:
                SelectAllVisibleObjects();
                break;
            case WindowsEditorShortcutRoute.ToolSelect:
                SetTimelineToolFromShortcut(TimelineTool.Select);
                break;
            case WindowsEditorShortcutRoute.ToolTap:
                SetTimelineToolFromShortcut(TimelineTool.Tap);
                break;
            case WindowsEditorShortcutRoute.ToolHold:
                SetTimelineToolFromShortcut(TimelineTool.Hold);
                break;
            case WindowsEditorShortcutRoute.ToolErase:
                SetTimelineToolFromShortcut(TimelineTool.Erase);
                break;
            case WindowsEditorShortcutRoute.Playtest:
                StartPlaytestAsync(timeline.CurrentBeat);
                break;
            case WindowsEditorShortcutRoute.PlayPause:
                await ToggleAudioPlaybackAsync();
                break;
            case WindowsEditorShortcutRoute.ToggleSymmetry:
                ToggleSymmetryTool();
                break;
            case WindowsEditorShortcutRoute.Delete:
                DeleteMixedSelection();
                break;
            case WindowsEditorShortcutRoute.StepForward:
                NudgeSelectionOrRedLine(1);
                break;
            case WindowsEditorShortcutRoute.StepBack:
                NudgeSelectionOrRedLine(-1);
                break;
            case WindowsEditorShortcutRoute.GoHome:
                timeline.SetCurrentBeat(0.0);
                break;
            case WindowsEditorShortcutRoute.TogglePro:
                timeline.SetProEditor(!timeline.ProEditorEnabled);
                RefreshProEditorControls();
                timeline.Focus();
                break;
            case WindowsEditorShortcutRoute.ToggleFullscreen:
                ToggleFullscreen();
                break;
            case WindowsEditorShortcutRoute.Mirror:
                MirrorVisibleSelection();
                break;
            case WindowsEditorShortcutRoute.Flip:
                FlipVisibleSelection();
                break;
            case WindowsEditorShortcutRoute.AddBookmark:
                AddNavigationMarker();
                break;
            case WindowsEditorShortcutRoute.SnapIncrease:
                AdjustQuickSnap(1);
                break;
            case WindowsEditorShortcutRoute.SnapDecrease:
                AdjustQuickSnap(-1);
                break;
            case WindowsEditorShortcutRoute.SnapSetFixed:
                SetQuickSnapDivision(editorViewSettings.FixedSnapDivision);
                break;
            case WindowsEditorShortcutRoute.Group:
                break;
        }
    }
}

private async void Window_KeyUp(object? sender, KeyEventArgs eventArgs)
{
    if (playtestInProgress)
    {
        eventArgs.Handled = true;
    }
    else
    {
        if (eventArgs.Key != Key.Space && IsTextEditingEvent(eventArgs) && WindowsAvaloniaEditorInputPolicy.DeferToTextInputMethod(eventArgs.Key, eventArgs.KeyModifiers))
        {
            return;
        }

        Key key = eventArgs.Key;
        PhysicalKey physicalKey = eventArgs.PhysicalKey;
        string keySymbol = eventArgs.KeySymbol;
        Key key2 = eventArgs.Key;
        bool flag = (((uint)(key2 - 155) <= 1u || key2 == Key.DeadCharProcessed) ? true : false);
        Key key3 = WindowsEditorShortcutGesture.ResolveReportedKey(key, physicalKey, keySymbol, flag ? WindowsShortcutPhysicalKeyState.TryResolvePressedKey() : ((Key? )null));
        if (oneShotShortcutState.Release(key3) && key3 == Key.Space)
        {
            eventArgs.Handled = true;
        }

        if (key3 == Key.Space && spacePanGestureState.IsArmed)
        {
            bool panned = timeline.EndSpacePanModifier();
            if (spacePanGestureState.HandleKeyUp(key3, panned) == WindowsEditorSpacePanKeyUpAction.TogglePlayback && !IsTextEditingFocused())
            {
                await ToggleAudioPlaybackAsync();
            }

            eventArgs.Handled = true;
        }
    }
}

private bool CanAcceptFileDrop()
{
    if (startupInitializationCompleted && !windowClosePreflightInProgress && !windowClosePreflightComplete)
    {
        return !windowCloseTeardownStarted;
    }

    return false;
}

private void Window_DragOver(object? sender, DragEventArgs eventArgs)
{
    bool flag = CanAcceptFileDrop() && (eventArgs.DataTransfer.TryGetFiles()?.Select((IStorageItem file) => file.TryGetLocalPath()).Any(WindowsFileDropPolicy.IsSupportedExistingFile) ?? false);
    eventArgs.DragEffects = (flag ? DragDropEffects.Copy : DragDropEffects.None);
    eventArgs.Handled = true;
}

private async void Window_Drop(object? sender, DragEventArgs eventArgs)
{
    eventArgs.Handled = true;
    if (!CanAcceptFileDrop())
    {
        return;
    }

    WindowsFileDropPlan windowsFileDropPlan = WindowsFileDropPolicy.CreatePlan(eventArgs.DataTransfer.TryGetFiles()?.Select((IStorageItem file) => file.TryGetLocalPath()) ?? Array.Empty<string>());
    if (windowsFileDropPlan.ChartAndArchivePaths.Count > 0)
    {
        try
        {
            foreach (string chartAndArchivePath in windowsFileDropPlan.ChartAndArchivePaths)
            {
                await OpenChartOrArchiveAsync(chartAndArchivePath);
            }

            return;
        }
        catch (Exception ex)
        {
            if (!windowCloseTeardownStarted)
            {
                viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("打开谱面失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
            }

            return;
        }
    }

    string audioPath = windowsFileDropPlan.AudioPath;
    if (audioPath == null)
    {
        return;
    }

    try
    {
        if (libraryVisible || viewModel.ActiveTab == null)
        {
            await CreateChartFromAudioPathAsync(audioPath);
        }
        else
        {
            await ImportChartAudioPathAsync(viewModel.ActiveTab, audioPath);
        }
    }
    catch (Exception ex2)
    {
        if (!windowCloseTeardownStarted)
        {
            SetMetadataAudioStatus(AvaloniaLocalizationCatalog.Format("无法导入音频：{0}", AvaloniaLocalizationCatalog.Text(ex2.Message)), error: true);
        }
    }
}

internal void AcceptSingleInstanceRequest(WindowsSingleInstanceOpenRequest request)
{
    ArgumentNullException.ThrowIfNull(request, "request");
    if (!global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            AcceptSingleInstanceRequest(request);
        }, DispatcherPriority.Input);
        return;
    }

    singleInstanceRequests.Enqueue(request);
    if (request.Activate)
    {
        ActivateFromSingleInstanceRequest();
    }

    DrainSingleInstanceRequestsAsync();
}

private void ActivateFromSingleInstanceRequest()
{
    if (WindowState == WindowState.Minimized)
    {
        WindowState = WindowState.Normal;
    }

    if (!IsVisible)
    {
        Show();
    }

    Activate();
}

private async Task DrainSingleInstanceRequestsAsync()
{
    if (!startupInitializationCompleted || singleInstanceDrainRunning || windowClosePreflightInProgress || windowCloseTeardownStarted)
    {
        return;
    }

    if (!IsEnabled)
    {
        if (!singleInstanceDrainRetryScheduled)
        {
            singleInstanceDrainRetryScheduled = true;
            DispatcherTimer.RunOnce(() =>
            {
                singleInstanceDrainRetryScheduled = false;
                DrainSingleInstanceRequestsAsync();
            }, TimeSpan.FromMilliseconds(250.0), DispatcherPriority.Background);
        }

        return;
    }

    singleInstanceDrainRunning = true;
    try
    {
        WindowsSingleInstanceOpenRequest request;
        while (singleInstanceRequests.TryDequeue(out request))
        {
            if (request.Activate)
            {
                ActivateFromSingleInstanceRequest();
            }

            foreach (string path in request.Paths)
            {
                await OpenChartOrArchiveAsync(path);
            }

            if (request.Paths.Count == 0)
            {
                viewModel.SetStatus("LumaChart Studio 已在此窗口运行");
                continue;
            }

            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已接收另一次启动请求 · {0:N0} 个文件", request.Paths.Count));
        }
    }
    finally
    {
        singleInstanceDrainRunning = false;
    }
}

private void ViewModel_StateChanged(object? sender, EventArgs eventArgs)
{
    if (global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
    {
        UpdateUi();
    }
    else
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(UpdateUi);
    }
}

private void AudioTransport_StateChanged(object? sender, EventArgs eventArgs)
{
    if (global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
    {
        HandleAudioTransportStateChanged();
    }
    else
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(HandleAudioTransportStateChanged);
    }
}

private void HandleAudioTransportStateChanged()
{
    UpdateAudioUi();
    UpdateQuickPlayPresentation();
    DesktopChartTab activeTab = viewModel.ActiveTab;
    WindowsAudioPlaybackSnapshot playbackSnapshot = audioTransport.PlaybackSnapshot;
    if (activeTab != null && IsCurrentKeySoundTab(activeTab) && (object)playbackSnapshot != null && playbackSnapshot.IsPlaying)
    {
        Guid? activeTabId = audioTransport.ActiveTabId;
        Guid id = activeTab.Id;
        if (activeTabId.HasValue && activeTabId.GetValueOrDefault() == id && !keySoundRuntimeDisposed)
        {
            keySoundRuntime.ResetPlaybackCursor(Math.Max(0.0, playbackSnapshot.Position.TotalMilliseconds), playbackSnapshot.PlaybackRate);
        }
    }
}

private void UpdateQuickPlayPresentation()
{
    WindowsAudioPlaybackSnapshot? playbackSnapshot = audioTransport.PlaybackSnapshot;
    if (((object)playbackSnapshot != null && playbackSnapshot.IsPlaying) || keySoundRuntime.Snapshot.IsSilentPlaying)
    {
        _ = 1;
    }
    else if (editorSeekPending)
    {
        _ = editorSeekResumeRequested;
    }
    else
        _ = 0;
    ConfigureEditorFrameRateRuntime();
    RefreshQuickValuesPaletteValues();
}

private void AudioVisualization_StateChanged(object? sender, EventArgs eventArgs)
{
    if (global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
    {
        UpdateAudioVisualizationUi();
    }
    else
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(UpdateAudioVisualizationUi);
    }
}

private void KeySoundRuntime_StateChanged(object? sender, EventArgs eventArgs)
{
    if (keySoundRuntimeDisposed)
    {
        return;
    }

    if (global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
    {
        UpdateKeySoundRuntimeUi();
        UpdateAudioUi();
        UpdateQuickPlayPresentation();
        return;
    }

    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (!keySoundRuntimeDisposed)
        {
            UpdateKeySoundRuntimeUi();
            UpdateAudioUi();
            UpdateQuickPlayPresentation();
        }
    });
}

private void AudioUiTimer_Tick(object? sender, EventArgs eventArgs)
{
    ConfigureEditorFrameRateRuntime();
    if (!IsEditorPlaybackActive() || (!UsesEditorAnimationFrames() && !UsesHighRateEditorVisuals()))
    {
        RunEditorFrame(updateVisuals: true);
    }
}

private bool UsesEditorAnimationFrames()
{
    if (IsEditorPlaybackActive())
    {
        return !UsesHighRateEditorVisuals();
    }

    return false;
}

private bool UsesHighRateEditorVisuals()
{
    if (OperatingSystem.IsWindows())
    {
        return EffectiveFrameRateLimit() > 165;
    }

    return false;
}

private void RequestEditorAnimationFrame()
{
    if (!editorAnimationFrameRequested && !windowClosePreflightInProgress && !windowClosePreflightComplete && !playtestInProgress && UsesEditorAnimationFrames())
    {
        editorAnimationFrameRequested = true;
        RequestAnimationFrame(EditorAnimationFrame);
    }
}

private void EditorAnimationFrame(TimeSpan compositionTimestamp)
{
    editorAnimationFrameRequested = false;
    if (!windowClosePreflightInProgress && !windowClosePreflightComplete && !playtestInProgress && UsesEditorAnimationFrames())
    {
        int num = EffectiveFrameRateLimit();
        if (editorCompositionFrameGate.Limit != num)
        {
            editorCompositionFrameGate.SetLimit(num);
        }

        RunEditorFrame(editorCompositionFrameGate.ShouldRender(), compositionTimestamp.TotalMilliseconds);
        RequestEditorAnimationFrame();
    }
}

private double GetDocumentEndBeat(DesktopChartTab tab)
{
    Guid? guid = editorRangeTab;
    Guid id = tab.Id;
    if (!guid.HasValue || guid.GetValueOrDefault() != id || editorRangeSerial != tab.Session.ChangeSerial || editorRangeDocument != tab.Session.Document)
    {
        editorRangeTab = tab.Id;
        editorRangeSerial = tab.Session.ChangeSerial;
        editorRangeDocument = tab.Session.Document;
        editorRangeEndBeat = tab.Session.Document.EndBeat;
    }

    return editorRangeEndBeat;
}

private void RunEditorFrame(bool updateVisuals, double? presentationTimeMilliseconds = null)
{
    if (playtestInProgress)
    {
        return;
    }

    bool flag = editorSeekNeedsFreshClock || Stopwatch.GetTimestamp() < editorSeekFreshClockUntil;
    editorSeekNeedsFreshClock = false;
    WindowsKeySoundPlaybackPumpResult windowsKeySoundPlaybackPumpResult;
    if (!flag && editorViewSettings.FrameRateLimit > 165 && TryReadEditorHighRateSample(out var sample))
    {
        windowsKeySoundPlaybackPumpResult = sample;
    }
    else
    {
        windowsKeySoundPlaybackPumpResult = (keySoundPlanRebuildPending ? CapturePlaybackClockWithoutScheduling() : keySoundRuntime.Pump());
    }

    if (!updateVisuals)
    {
        if (flag)
        {
            editorSeekNeedsFreshClock = true;
        }

        return;
    }

    editorFrameUpdates++;
    RefreshFrameRateStatus(IsEditorPlaybackActive());
    if (editorSeekPending)
    {
        UpdateAudioUi();
        return;
    }

    bool flag2 = audioPositionUiClock.ElapsedMilliseconds >= 50;
    if (flag2)
    {
        audioPositionUiClock.Restart();
        UpdateAudioUi();
    }

    if (windowsKeySoundPlaybackPumpResult.Output == WindowsKeySoundPlaybackOutput.None)
    {
        if (flag)
        {
            editorSeekNeedsFreshClock = true;
        }

        return;
    }

    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null || activeTab != keySoundActiveTab || !TryGetCachedAudioTimingMap(activeTab, out TimingMap timingMap))
    {
        return;
    }

    double num = Math.Max(0.0, timingMap.BeatToMilliseconds(BeatPosition.FromDouble(GetDocumentEndBeat(activeTab))));
    if (windowsKeySoundPlaybackPumpResult.Output == WindowsKeySoundPlaybackOutput.SilentEditorClock && windowsKeySoundPlaybackPumpResult.PositionMilliseconds >= num)
    {
        keySoundRuntime.StopSilent();
        SetProgrammaticPlaybackBeat(GetDocumentEndBeat(activeTab));
    }
    else if (!audioPositionPointerDown)
    {
        double num2 = editorVisualClock.Sample(windowsKeySoundPlaybackPumpResult.PositionMilliseconds, windowsKeySoundPlaybackPumpResult.PlaybackRate, presentationTimeMilliseconds ?? editorVisualTime.Elapsed.TotalMilliseconds);
        if (windowsKeySoundPlaybackPumpResult.Output == WindowsKeySoundPlaybackOutput.SharedMusicClock)
        {
            num2 = EditorAudioVisualCalibration.ChartTime(num2, windowsKeySoundPlaybackPumpResult.PlaybackRate, editorAudioVisualOffsetMilliseconds);
        }

        double value = timingMap.MillisecondsToBeat(num2, 1000000).Value;
        SetProgrammaticPlaybackBeat(value);
        lastCalibratedCursorMilliseconds = ((windowsKeySoundPlaybackPumpResult.Output == WindowsKeySoundPlaybackOutput.SharedMusicClock && editorAudioVisualOffsetMilliseconds != 0) ? new double? (num2) : ((double? )null));
        lastCalibratedCursorRate = windowsKeySoundPlaybackPumpResult.PlaybackRate;
        lastCalibratedCursorOffsetMilliseconds = editorAudioVisualOffsetMilliseconds;
        lastCalibratedCursorTabId = activeTab.Id;
        if (flag2)
        {
            UpdatePlaybackPositionUi(value);
        }
    }
}

private int EffectiveFrameRateLimit()
{
    return WindowsFrameRateOptions.ClampToPresentable(editorViewSettings.FrameRateLimit, PlatformDisplayRefreshRates.Current);
}

private static TimeSpan WindowsEditorFrameInterval(int frameRateLimit)
{
    int num = WindowsFrameRateOptions.Normalize(frameRateLimit);
    return TimeSpan.FromMilliseconds(1000.0 / (double)num);
}

private bool IsEditorPlaybackActive()
{
    if (keySoundRuntimeDisposed)
    {
        return false;
    }

    WindowsAudioPlaybackSnapshot? playbackSnapshot = audioTransport.PlaybackSnapshot;
    if ((object)playbackSnapshot == null || !playbackSnapshot.IsPlaying)
    {
        return keySoundRuntime.Snapshot.IsSilentPlaying;
    }

    return true;
}

private void ConfigureEditorFrameRateRuntime(bool resetCounters = false)
{
    if (!global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ConfigureEditorFrameRateRuntime(resetCounters);
        });
        return;
    }

    bool flag = IsEditorPlaybackActive();
    bool flag2 = flag && !editorPlaybackWasActive;
    editorPlaybackWasActive = flag;
    bool flag3 = timeline.IsPlaybackActive != flag;
    timeline.SetPlaybackActive(flag);
    if (flag3)
    {
        AdvanceTrackPlaybackViewports();
    }

    if (!frameRateRuntimeReady)
    {
        return;
    }

    int num = EffectiveFrameRateLimit();
    bool flag4 = UsesEditorAnimationFrames();
    TimeSpan timeSpan = ((flag4 || UsesHighRateEditorVisuals() || !flag) ? TimeSpan.FromMilliseconds(50.0) : WindowsEditorFrameInterval(num));
    if (Math.Abs((audioUiTimer.Interval - timeSpan).TotalMilliseconds) > 0.001)
    {
        audioUiTimer.Interval = timeSpan;
    }

    if (editorCompositionFrameGate.Limit != num)
    {
        editorCompositionFrameGate.SetLimit(num);
    }

    if (flag4)
    {
        RequestEditorAnimationFrame();
    }

    bool flag5 = (!windowClosePreflightInProgress && !windowClosePreflightComplete && !playtestInProgress && num > 165) & flag;
    if (flag5 && (!editorHighRateFramePumpRunning || editorHighRateFramePump.FramesPerSecond != num))
    {
        StopEditorHighRateFramePump();
        Interlocked.Exchange(ref editorHighRateSampleVersion, 0L);
        editorHighRateVisualGate.SetLimit(Math.Min(240, num));
        editorHighRateFramePump.Start(num, SampleEditorHighRateFrame);
        editorHighRateFramePumpRunning = true;
    }
    else if (!flag5 && editorHighRateFramePumpRunning)
    {
        StopEditorHighRateFramePump();
    }

    if (resetCounters || flag2)
    {
        editorFrameUpdates = 0L;
        editorHighRateFramePump.TakeTickCount();
        editorFrameClock.Restart();
        audioPositionUiClock.Restart();
        if (flag2)
        {
            timeline.Performance.Reset();
        }

        editorStatusRenderCount = timeline.Performance.TotalRenderedFrames;
        if (flag2 || !flag)
        {
            editorVisualClock.Reset();
            editorSeekNeedsFreshClock = true;
        }

        RefreshFrameRateStatus(flag, force: true);
    }
}

private void StopEditorHighRateFramePump()
{
    editorHighRateFramePump.Stop();
    editorHighRateFramePumpRunning = false;
    Interlocked.Exchange(ref editorHighRateSampleVersion, 0L);
    Volatile.Write(ref editorHighRateOutput, 0);
}

private void SampleEditorHighRateFrame()
{
    if (keySoundRuntimeDisposed || !IsEditorPlaybackActive())
    {
        return;
    }

    WindowsKeySoundPlaybackPumpResult windowsKeySoundPlaybackPumpResult = (Volatile.Read(in keySoundPlanRebuildPending) ? CapturePlaybackClockWithoutScheduling() : keySoundRuntime.Pump());
    if (windowsKeySoundPlaybackPumpResult.Output == WindowsKeySoundPlaybackOutput.None)
    {
        return;
    }

    Interlocked.Exchange(ref editorHighRatePositionBits, BitConverter.DoubleToInt64Bits(windowsKeySoundPlaybackPumpResult.PositionMilliseconds));
    Volatile.Write(ref editorHighRateOutput, (int)windowsKeySoundPlaybackPumpResult.Output);
    Interlocked.Increment(ref editorHighRateSampleVersion);
    if (!editorHighRateVisualGate.ShouldRender() || Interlocked.CompareExchange(ref editorVisualUpdatePending, 1, 0) != 0)
    {
        return;
    }

    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        Interlocked.Exchange(ref editorVisualUpdatePending, 0);
        if (editorHighRateFramePumpRunning && UsesHighRateEditorVisuals() && IsEditorPlaybackActive() && !playtestInProgress && !windowClosePreflightInProgress && !windowClosePreflightComplete)
        {
            RunEditorFrame(updateVisuals: true);
        }
    }, DispatcherPriority.Render);
}

private bool TryReadEditorHighRateSample(out WindowsKeySoundPlaybackPumpResult sample)
{
    for (int i = 0; i < 2; i++)
    {
        long num = Interlocked.Read(in editorHighRateSampleVersion);
        if (num <= 0)
        {
            break;
        }

        double positionMilliseconds = BitConverter.Int64BitsToDouble(Interlocked.Read(in editorHighRatePositionBits));
        WindowsKeySoundPlaybackOutput windowsKeySoundPlaybackOutput = (WindowsKeySoundPlaybackOutput)Volatile.Read(in editorHighRateOutput);
        long num2 = Interlocked.Read(in editorHighRateSampleVersion);
        if (num == num2 && windowsKeySoundPlaybackOutput != WindowsKeySoundPlaybackOutput.None)
        {
            sample = new WindowsKeySoundPlaybackPumpResult(windowsKeySoundPlaybackOutput, positionMilliseconds, audioTransport.PlaybackRate, 0, 0, 0);
            return true;
        }
    }

    sample = WindowsKeySoundPlaybackPumpResult.Idle;
    return false;
}

private static bool TryAdvanceLocalizedTickSource(ref string? lastSource, string? source)
{
    if (string.Equals(lastSource, source, StringComparison.Ordinal))
    {
        return false;
    }

    lastSource = source;
    return true;
}

private void RefreshFrameRateStatus(bool isPlaying, bool force = false)
{
    if (!isPlaying)
    {
        if (force || !frameRateStatusIdleShown)
        {
            frameRateStatusText.Text = "空闲 · 播放时测量帧率";
            frameRateStatusIdleShown = true;
            AutomationProperties.SetName(frameRateStatusText, frameRateStatusText.Text);
        }

        editorFrameUpdates = 0L;
        editorHighRateFramePump.TakeTickCount();
        editorFrameClock.Restart();
    }
    else if (force || editorFrameClock.ElapsedMilliseconds >= 500)
    {
        double num = Math.Max(0.001, editorFrameClock.Elapsed.TotalSeconds);
        int frameRateLimit = editorViewSettings.FrameRateLimit;
        TimelinePerformanceSnapshot timelinePerformanceSnapshot = timeline.Performance.Snapshot();
        long totalRenderedFrames = timeline.Performance.TotalRenderedFrames;
        double value = (double)Math.Max(0L, totalRenderedFrames - editorStatusRenderCount) / num;
        editorStatusRenderCount = totalRenderedFrames;
        double value2 = ((timelinePerformanceSnapshot.FrameIntervalMilliseconds.P99 > 0.0) ? (1000.0 / timelinePerformanceSnapshot.FrameIntervalMilliseconds.P99) : 0.0);
        frameRateStatusIdleShown = false;
        double value3 = ((frameRateLimit > 165) ? ((double)editorHighRateFramePump.TakeTickCount() / num) : ((double)editorFrameUpdates / num));
        frameRateStatusText.Text = $"绘制 {value:0} FPS · 1% low≈{value2:0} · 时钟 {value3:0}";
        AutomationProperties.SetName(frameRateStatusText, frameRateStatusText.Text);
        AutomationProperties.SetHelpText(frameRateStatusText, $"FrameRateLimit={frameRateLimit}; HighRateClock={frameRateLimit > 165}; FrameDriver={(UsesHighRateEditorVisuals() ? "CoalescedRenderDispatch" : "TopLevel.RequestAnimationFrame")}; RenderedFps={value:F3}; VisualUpdatesPerSecond={(double)editorFrameUpdates / num:F3}; AudioClockIndependent=true; TimerIntervalMs={audioUiTimer.Interval.TotalMilliseconds:F6}; RenderSamples={timelinePerformanceSnapshot.RenderMilliseconds.Samples}; RenderMsP50={timelinePerformanceSnapshot.RenderMilliseconds.P50:F6}; RenderMsP95={timelinePerformanceSnapshot.RenderMilliseconds.P95:F6}; RenderMsP99={timelinePerformanceSnapshot.RenderMilliseconds.P99:F6}; FrameMsP50={timelinePerformanceSnapshot.FrameIntervalMilliseconds.P50:F6}; FrameMsP95={timelinePerformanceSnapshot.FrameIntervalMilliseconds.P95:F6}; FrameMsP99={timelinePerformanceSnapshot.FrameIntervalMilliseconds.P99:F6}; InputMsP50={timelinePerformanceSnapshot.InputToRenderMilliseconds.P50:F6}; InputMsP95={timelinePerformanceSnapshot.InputToRenderMilliseconds.P95:F6}; InputMsP99={timelinePerformanceSnapshot.InputToRenderMilliseconds.P99:F6}; MaximumVisitedNotes={timelinePerformanceSnapshot.MaximumVisitedNotes}");
        editorFrameUpdates = 0L;
        editorFrameClock.Restart();
    }
}

private async Task ToggleAudioPlaybackAsync()
{
    if (playtestInProgress)
    {
        return;
    }

    await editorPlaybackCommandGate.WaitAsync();
    try
    {
        if (!playtestInProgress)
        {
            await ToggleAudioPlaybackCoreAsync();
        }
    }
    finally
    {
        editorPlaybackCommandGate.Release();
    }
}

private async Task ToggleAudioPlaybackCoreAsync()
{
    DesktopChartTab tab = viewModel.ActiveTab;
    if (tab == null || tab != keySoundActiveTab)
    {
        viewModel.SetStatus("没有活动谱面可播放");
        return;
    }

    bool pendingLogicalPlayback = editorSeekPending && editorSeekTab == tab && editorSeekResumeRequested;
    await CancelPendingEditorSeek();
    if (!IsCurrentKeySoundTab(tab))
    {
        return;
    }

    WindowsAudioPlaybackSnapshot playbackSnapshot = audioTransport.PlaybackSnapshot;
    if (pendingLogicalPlayback)
    {
        if ((object)playbackSnapshot != null && playbackSnapshot.IsPlaying && audioTransport.ActiveTabId == tab.Id)
        {
            keySoundRuntime.InvalidatePlayback();
            await audioTransport.TogglePlayPauseAsync();
        }
        else if (keySoundRuntime.Snapshot.IsSilentPlaying)
        {
            keySoundRuntime.PauseSilent();
        }
    }
    else if ((object)playbackSnapshot != null && playbackSnapshot.IsLoaded && audioTransport.ActiveTabId == tab.Id)
    {
        keySoundRuntime.InvalidatePlayback();
        if (!playbackSnapshot.IsPlaying)
        {
            GetAudioTimingMap(tab);
            TimeSpan timeSpan = TimeSpan.FromMilliseconds(ResolveEditorAudioResumeMilliseconds(tab));
            if (WindowsAudioResumePolicy.RequiresSeek(playbackSnapshot.Position, timeSpan))
            {
                await audioTransport.SeekAsync(timeSpan);
                if (audioTransport.LastError != null || !IsCurrentKeySoundTab(tab))
                {
                    return;
                }
            }
        }

        await audioTransport.TogglePlayPauseAsync();
        if (audioTransport.LastError == null && IsCurrentKeySoundTab(tab))
        {
            ResetKeySoundPlaybackAtCurrent(tab);
        }
    }
    else if (keySoundResolvedAudioPath != null)
    {
        viewModel.SetStatus("音频未就绪，客户端正在自动恢复播放链…");
        if (!(await audioTransport.EnsureAudioReadyAsync(tab)) || !IsCurrentKeySoundTab(tab))
        {
            viewModel.SetStatus(audioTransport.Status);
            return;
        }

        playbackSnapshot = audioTransport.PlaybackSnapshot;
        if ((object)playbackSnapshot == null || !playbackSnapshot.IsLoaded || audioTransport.ActiveTabId != tab.Id)
        {
            viewModel.SetStatus(audioTransport.Status);
            return;
        }

        keySoundRuntime.InvalidatePlayback();
        TimingMap audioTimingMap = GetAudioTimingMap(tab);
        double value = Math.Max(0.0, audioTimingMap.BeatToMilliseconds(BeatPosition.FromDouble(timeline.CurrentBeat)));
        await audioTransport.SeekAsync(TimeSpan.FromMilliseconds(value));
        if (audioTransport.LastError == null && IsCurrentKeySoundTab(tab))
        {
            await audioTransport.TogglePlayPauseAsync();
            if (audioTransport.LastError == null && IsCurrentKeySoundTab(tab))
            {
                ResetKeySoundPlaybackAtCurrent(tab);
            }
        }
    }
    else if (keySoundRuntime.Snapshot.IsSilentPlaying)
    {
        keySoundRuntime.PauseSilent();
    }
    else
    {
        double positionMilliseconds = Math.Max(0.0, GetAudioTimingMap(tab).BeatToMilliseconds(BeatPosition.FromDouble(timeline.CurrentBeat)));
        keySoundRuntime.SeekSilent(positionMilliseconds);
        keySoundRuntime.SetSilentRate(audioTransport.PlaybackRate);
        RequestKeySoundPlanNow();
        if (!(await keySoundRuntime.StartSilentAsync()) && IsCurrentKeySoundTab(tab))
        {
            viewModel.SetStatus(keySoundRuntime.Snapshot.Status);
        }
    }
}

private async Task StopAudioAsync()
{
    DesktopChartTab tab = viewModel.ActiveTab;
    if (tab == null || tab != keySoundActiveTab)
    {
        return;
    }

    await CancelPendingEditorSeek();
    if (!IsCurrentKeySoundTab(tab))
    {
        return;
    }

    if ((audioTransport.PlaybackSnapshot?.IsLoaded ?? false) && audioTransport.ActiveTabId == tab.Id)
    {
        keySoundRuntime.InvalidatePlayback();
        await audioTransport.StopAsync();
        if (audioTransport.LastError != null || !IsCurrentKeySoundTab(tab))
        {
            return;
        }

        keySoundRuntime.ResetPlaybackCursor(0.0, audioTransport.PlaybackRate);
    }
    else
    {
        keySoundRuntime.StopSilent();
    }

    SetProgrammaticPlaybackBeat(0.0);
}

private async Task SeekAudioFromSliderAsync()
{
    if (suppressAudioControls)
    {
        return;
    }

    double milliseconds = Math.Clamp(audioPositionSlider.Value, audioPositionSlider.Minimum, audioPositionSlider.Maximum);
    DesktopChartTab tab = viewModel.ActiveTab;
    if (tab == null || tab != keySoundActiveTab)
    {
        return;
    }

    await CancelPendingEditorSeek();
    if (!IsCurrentKeySoundTab(tab))
    {
        return;
    }

    if ((audioTransport.PlaybackSnapshot?.IsLoaded ?? false) && audioTransport.ActiveTabId == tab.Id)
    {
        keySoundRuntime.InvalidatePlayback();
        await audioTransport.SeekAsync(TimeSpan.FromMilliseconds(milliseconds));
        if (audioTransport.LastError != null || !IsCurrentKeySoundTab(tab))
        {
            return;
        }

        keySoundRuntime.ResetPlaybackCursor(milliseconds, audioTransport.PlaybackRate);
    }
    else
    {
        keySoundRuntime.SeekSilent(milliseconds);
    }

    TimingMap audioTimingMap = GetAudioTimingMap(tab);
    SetProgrammaticPlaybackBeat(Math.Max(0.0, audioTimingMap.MillisecondsToBeat(milliseconds).Value));
}

private async Task SetEditorPlaybackRateAsync(double rate)
{
    DesktopChartTab tab = viewModel.ActiveTab;
    if (tab != null && IsCurrentKeySoundTab(tab))
    {
        keySoundRuntime.InvalidatePlayback();
    }

    if (keySoundRuntime.Snapshot.IsSilentPlaying)
    {
        keySoundRuntime.SetSilentRate(rate);
    }

    await audioTransport.SetPlaybackRateAsync(rate);
    editorViewSettings = editorViewSettings.WithPlaybackRate(audioTransport.PlaybackRate);
    MarkEditorViewSettingsDirty();
    if (tab != null && audioTransport.LastError == null && IsCurrentKeySoundTab(tab) && (audioTransport.PlaybackSnapshot?.IsLoaded ?? false))
    {
        ResetKeySoundPlaybackAtCurrent(tab);
    }
}

private async Task SetEditorPreservePitchAsync(bool preservePitch)
{
    DesktopChartTab tab = viewModel.ActiveTab;
    if (tab != null && IsCurrentKeySoundTab(tab))
    {
        keySoundRuntime.InvalidatePlayback();
    }

    await audioTransport.SetPreservePitchAsync(preservePitch);
    editorViewSettings = editorViewSettings.WithPreservePitch(audioTransport.PreservePitch);
    MarkEditorViewSettingsDirty();
    if (tab != null && audioTransport.LastError == null && IsCurrentKeySoundTab(tab))
    {
        ResetKeySoundPlaybackAtCurrent(tab);
    }
}

private async Task RefreshEditorAudioDeviceAsync()
{
    DesktopChartTab tab = viewModel.ActiveTab;
    if (tab != null && IsCurrentKeySoundTab(tab))
    {
        keySoundRuntime.InvalidatePlayback();
    }

    await audioTransport.RefreshDefaultOutputDeviceAsync();
    if (tab != null && audioTransport.LastError == null && IsCurrentKeySoundTab(tab))
    {
        ResetKeySoundPlaybackAtCurrent(tab);
    }
}

private TimingMap GetAudioTimingMap(DesktopChartTab tab)
{
    return audioTimingMapCache.Get(tab.Id, tab.Session.Document, tab.Session.ChangeSerial);
}

private bool TryGetCachedAudioTimingMap(DesktopChartTab tab, out TimingMap timingMap)
{
    return audioTimingMapCache.TryGet(tab.Id, tab.Session.Document, tab.Session.ChangeSerial, out timingMap);
}

private void UpdateAudioUi()
{
    WindowsAudioPlaybackSnapshot playbackSnapshot = audioTransport.PlaybackSnapshot;
    bool flag = playbackSnapshot?.IsLoaded ?? false;
    bool flag2 = (object)playbackSnapshot != null && playbackSnapshot.State == WindowsAudioPlaybackState.Faulted && !string.IsNullOrWhiteSpace(playbackSnapshot.SourcePath);
    DesktopChartTab activeTab = viewModel.ActiveTab;
    WindowsKeySoundPlaybackRuntimeSnapshot snapshot = keySoundRuntime.Snapshot;
    bool flag3 = activeTab != null && editorSeekPending && editorSeekTab == activeTab;
    bool flag4 = activeTab != null && activeTab == keySoundActiveTab && keySoundResolvedAudioPath == null;
    bool flag5 = activeTab != null && activeTab == keySoundActiveTab && keySoundResolvedAudioPath != null;
    bool flag6 = flag | flag4 | flag5;
    bool flag7 = flag4 && snapshot.RequestedTabId == activeTab?.Id && snapshot.IsSilentPlaying;
    TimeSpan value;
    if (flag3)
    {
        value = TimeSpan.FromMilliseconds(pendingEditorSeekMilliseconds);
    }
    else
    {
        value = (flag ? playbackSnapshot.Position : TimeSpan.FromMilliseconds(Math.Max(0.0, snapshot.PositionMilliseconds)));
    }

    DateTimeOffset utcNow = DateTimeOffset.UtcNow;
    TimeSpan timeSpan;
    if (flag)
    {
        timeSpan = playbackSnapshot.Duration;
    }
    else
    {
        timeSpan = ((activeTab != null && TryGetCachedAudioTimingMap(activeTab, out TimingMap timingMap)) ? TimeSpan.FromMilliseconds(Math.Max(0.0, timingMap.BeatToMilliseconds(BeatPosition.FromDouble(GetDocumentEndBeat(activeTab))))) : TimeSpan.Zero);
    }

    double externalEndBeat = ((flag && activeTab != null) ? GetAudioTimingMap(activeTab).MillisecondsToBeat(timeSpan.TotalMilliseconds).Value : 0.0);
    RefreshEventTimeRulerAudioDuration();
    editorOverview.SetExternalEndBeat(externalEndBeat);
    difficultyOverview.SetEndBeat(editorOverview.DisplayEndBeat);
    suppressAudioControls = true;
    try
    {
        audioPlayPauseButton.IsEnabled = flag6;
        if (commandPlayButton != null)
        {
            commandPlayButton.IsEnabled = flag6;
            if (commandPlayButton.Content is global::Avalonia.Controls.Shapes.Path path)
            {
                bool flag8 = ((playbackSnapshot?.IsPlaying ?? false) | flag7) || (flag3 && editorSeekResumeRequested);
                path.Data = Geometry.Parse(flag8 ? "M3,2 H6 V14 H3 Z M9,2 H12 V14 H9 Z" : "M2,1 L13,7.5 L2,14 Z");
                string text = (flag8 ? "暂停" : "播放");
                if (TryAdvanceLocalizedTickSource(ref commandPlayButtonNameSource, text))
                {
                    AutomationProperties.SetName(commandPlayButton, text);
                }
            }
        }

        SetQuickShortcutEnabled("PlayPause", flag6);
        string text2 = ((((playbackSnapshot?.IsPlaying ?? false) | flag7) || (flag3 && editorSeekResumeRequested)) ? "Ⅱ 暂停" : "▶ 播放");
        if (TryAdvanceLocalizedTickSource(ref audioPlayPauseButtonContentSource, text2))
        {
            audioPlayPauseButton.Content = text2;
        }

        audioStopButton.IsEnabled = flag | flag4;
        if (commandStopButton != null)
        {
            commandStopButton.IsEnabled = flag | flag4;
        }

        audioRefreshDeviceButton.IsEnabled = flag | flag2;
        audioPositionSlider.IsEnabled = flag | flag4;
        audioPositionSlider.Maximum = Math.Max(1.0, timeSpan.TotalMilliseconds);
        if (!audioPositionPointerDown)
        {
            audioPositionSlider.Value = Math.Clamp(value.TotalMilliseconds, audioPositionSlider.Minimum, audioPositionSlider.Maximum);
        }

        audioVolumeSlider.Value = audioTransport.Volume * 100f;
        audioRateSlider.Value = audioTransport.PlaybackRate;
        SyncFixedPlaybackRate();
        SyncAdjustmentZoom();
        audioPreservePitchBox.IsChecked = audioTransport.PreservePitch;
        audioPreservePitchBox.IsEnabled = true;
        audioVolumeText.Text = $"{audioTransport.Volume:P0}";
        audioRateText.Text = $"{audioTransport.PlaybackRate:0.00}x";
        audioTimeText.Text = $"{FormatAudioTime(value)}  ({audioTransport.PlaybackRate:0.00}x)";
        string text3;
        if (flag7)
        {
            text3 = "当前谱面没有可用音乐 · 正在使用无音乐编辑时钟";
        }
        else
        {
            text3 = ((audioTransport.LastError == null) ? audioTransport.Status : (audioTransport.Status + "\n" + audioTransport.LastError.GetType().Name));
        }

        if (TryAdvanceLocalizedTickSource(ref audioStatusTextSource, text3))
        {
            audioStatusText.Text = text3;
        }

        TextBlock textBlock = audioStatusText;
        IBrush foreground;
        if (audioTransport.LastError != null)
        {
            IBrush indianRed = Brushes.IndianRed;
            foreground = indianRed;
        }
        else
        {
            foreground = MutedBrush;
        }

        textBlock.Foreground = foreground;
        AutomationProperties.SetName(audioTimeText, audioTimeText.Text);
        AutomationProperties.SetName(audioRateText, audioRateText.Text);
        AutomationProperties.SetName(audioVolumeText, audioVolumeText.Text);
        AutomationProperties.SetName(audioStatusText, audioStatusText.Text);
        WindowsAudioDeviceDiagnostics deviceDiagnostics = audioTransport.DeviceDiagnostics;
        AudioVisualizationSideRuntimeCounters runtimeCounters = waveformSideSurface.GetRuntimeCounters();
        AudioVisualizationSideRuntimeCounters runtimeCounters2 = spectrogramSideSurface.GetRuntimeCounters();
        string value2 = $"State={playbackSnapshot?.State.ToString() ?? "Empty"}; IsLoaded={flag}; IsPlaying={playbackSnapshot?.IsPlaying ?? false}; PositionObservedUtc={utcNow:O}; PositionMs={value.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}; DurationMs={timeSpan.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}; PlaybackRate={audioTransport.PlaybackRate.ToString("F3", CultureInfo.InvariantCulture)}; PreservePitch={audioTransport.PreservePitch}; Volume={audioTransport.Volume.ToString("F3", CultureInfo.InvariantCulture)}; ActiveTabId={audioTransport.ActiveTabId?.ToString("D") ?? string.Empty}; SourcePath={playbackSnapshot?.SourcePath ?? string.Empty}; SilentPlaying={flag7}; RecoveryAvailable={flag5}; PendingSeek={flag3}; UnexpectedOutputStops={deviceDiagnostics.UnexpectedPlaybackStopCount}; TransientMusicZeros={deviceDiagnostics.TransientMusicZeroCount}; WaveSceneReady={runtimeCounters.SceneReady}; WaveSceneBuilds={runtimeCounters.CompletedSceneBuilds}; SpecSceneReady={runtimeCounters2.SceneReady}; SpecSceneBuilds={runtimeCounters2.CompletedSceneBuilds}; SpecBitmapUiLastMs={runtimeCounters2.LastUiThreadCpuBitmapMaterializationMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}; SpecBitmapUiMaxMs={runtimeCounters2.MaximumUiThreadCpuBitmapMaterializationMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}; LastUnexpectedOutputStopError={deviceDiagnostics.LastUnexpectedPlaybackStopError ?? string.Empty}; LastError={audioTransport.LastError?.GetType().Name ?? string.Empty}";
        AutomationProperties.SetHelpText(audioStatusText, value2);
        if (commandPlayButton != null)
        {
            AutomationProperties.SetHelpText(commandPlayButton, value2);
        }

        if (commandStopButton != null)
        {
            AutomationProperties.SetHelpText(commandStopButton, value2);
        }

        ToolTip.SetTip(audioStatusText, playbackSnapshot?.SourcePath);
    }
    finally
    {
        suppressAudioControls = false;
    }
}

private void UpdateAudioVisualizationUi()
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    AudioVisualization audioVisualization = ((activeTab != null && this.audioVisualization.ActiveTabId == activeTab.Id) ? this.audioVisualization.Visualization : null);
    TimingMap timingMap = ((activeTab == null) ? null : GetAudioTimingMap(activeTab));
    bool flag = audioPanelSettings.Waveform.Mode == WindowsAudioPanelDisplayMode.Background && !audioPanelSettings.Waveform.Collapsed;
    bool flag2 = audioPanelSettings.Spectrogram.Mode == WindowsAudioPanelDisplayMode.Background && !audioPanelSettings.Spectrogram.Collapsed;
    timeline.SetAudioVisualization(audioVisualization, timingMap, flag, flag2);
    timeline.SetSpectrogramAppearance(audioPanelSettings.BackgroundSpectrogramStrength, audioPanelSettings.SpectrogramDetail);
    waveformSideSurface.SetVisualization(audioVisualization, timingMap);
    spectrogramSideSurface.SetSpectrogramDetail(audioPanelSettings.SpectrogramDetail);
    spectrogramSideSurface.SetVisualization(audioVisualization, timingMap);
    UpdateAudioPanelViewports();
    suppressAudioPanelControls = true;
    try
    {
        waveformBackgroundBox.IsChecked = flag;
        spectrogramBackgroundBox.IsChecked = flag2;
        waveformBackgroundBox.IsEnabled = activeTab != null;
        spectrogramBackgroundBox.IsEnabled = activeTab != null;
    }
    finally
    {
        suppressAudioPanelControls = false;
    }

    audioVisualizationStatusText.Text = AvaloniaLocalizationCatalog.Format("{0} · 背景波形{1} / 频谱{2}", this.audioVisualization.Status, flag ? AvaloniaLocalizationCatalog.Text("开") : AvaloniaLocalizationCatalog.Text("关"), flag2 ? AvaloniaLocalizationCatalog.Text("开") : AvaloniaLocalizationCatalog.Text("关"));
    TextBlock textBlock = audioVisualizationStatusText;
    IBrush foreground;
    if (this.audioVisualization.LastError != null)
    {
        IBrush indianRed = Brushes.IndianRed;
        foreground = indianRed;
    }
    else
    {
        foreground = MutedBrush;
    }

    textBlock.Foreground = foreground;
    QueueAudioPanelLayout();
}

private static string FormatAudioTime(TimeSpan value)
{
    if (value < TimeSpan.Zero || value == TimeSpan.MaxValue)
    {
        value = TimeSpan.Zero;
    }

    int value2 = (int)value.TotalMinutes;
    return $"{value2:00}:{value.Seconds:00}.{value.Milliseconds:000}";
}

private void TryApplyApplicationIcon()
{
    try
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://SimpleManiaEditor/Assets/app-icon.png"));
        Icon = new WindowIcon(stream);
    }
    catch (Exception ex)when ((ex is IOException || ex is InvalidOperationException || ex is UriFormatException) ? true : false)
    {
    }
}

private WindowsKeySoundPlaybackPumpResult CapturePlaybackClockWithoutScheduling()
{
    DesktopChartTab desktopChartTab = keySoundActiveTab;
    if (desktopChartTab == null)
    {
        return WindowsKeySoundPlaybackPumpResult.Idle;
    }

    WindowsAudioPlaybackSnapshot playbackSnapshot = audioTransport.PlaybackSnapshot;
    Guid? activeTabId = audioTransport.ActiveTabId;
    Guid id = desktopChartTab.Id;
    if (activeTabId.HasValue && activeTabId.GetValueOrDefault() == id && (object)playbackSnapshot != null && playbackSnapshot.IsPlaying)
    {
        return new WindowsKeySoundPlaybackPumpResult(WindowsKeySoundPlaybackOutput.SharedMusicClock, playbackSnapshot.Position.TotalMilliseconds, playbackSnapshot.PlaybackRate, 0, 0, 0);
    }

    WindowsKeySoundPlaybackRuntimeSnapshot snapshot = keySoundRuntime.Snapshot;
    activeTabId = snapshot.RequestedTabId;
    id = desktopChartTab.Id;
    if (!activeTabId.HasValue || !(activeTabId.GetValueOrDefault() == id) || !snapshot.IsSilentPlaying)
    {
        return WindowsKeySoundPlaybackPumpResult.Idle;
    }

    return new WindowsKeySoundPlaybackPumpResult(WindowsKeySoundPlaybackOutput.SilentEditorClock, snapshot.PositionMilliseconds, snapshot.PlaybackRate, 0, 0, 0);
}

private bool QueueEditorSeek(DesktopChartTab tab, double milliseconds, bool resumeAfterSeek = true)
{
    if (playtestInProgress)
    {
        return false;
    }

    editorVisualClock.Reset();
    if (!IsCurrentKeySoundTab(tab) || keySoundRuntimeDisposed)
    {
        return false;
    }

    if (editorSeekPending && editorSeekTab == tab)
    {
        if (!resumeAfterSeek)
        {
            editorSeekResumeRequested = false;
        }

        if (shortcutSettings.Profile != EditorInteractionProfile.OsuMania)
        {
            EnsurePendingEditorSeekTransportPaused();
        }

        UpdatePendingEditorSeekTarget(milliseconds);
        return true;
    }

    WindowsAudioPlaybackSnapshot playbackSnapshot = audioTransport.PlaybackSnapshot;
    bool flag = audioTransport.ActiveTabId == tab.Id && (playbackSnapshot?.IsLoaded ?? false);
    bool flag2 = flag && playbackSnapshot.IsPlaying;
    bool flag3 = !flag && keySoundRuntime.Snapshot.IsSilentPlaying;
    if (!flag && !flag3)
    {
        return false;
    }

    CancelPendingEditorSeek();
    long generation = ++editorSeekGeneration;
    editorSeekPending = true;
    editorSeekTab = tab;
    editorSeekUsesMusic = flag;
    editorSeekResumeRequested = resumeAfterSeek && (flag2 | flag3);
    keySoundRuntime.InvalidatePlayback();
    if (flag2 && shortcutSettings.Profile != EditorInteractionProfile.OsuMania)
    {
        editorSeekPauseTask = PauseMusicForEditorSeekAsync(generation, tab);
    }
    else
    {
        keySoundRuntime.PauseSilent();
        editorSeekPauseTask = Task.CompletedTask;
    }

    UpdatePendingEditorSeekTarget(milliseconds);
    return true;
}

private void UpdatePendingEditorSeekTarget(double milliseconds)
{
    pendingEditorSeekMilliseconds = Math.Max(0.0, milliseconds);
    editorSeekTargetVersion++;
    keySoundRuntime.ResetPlaybackCursor(pendingEditorSeekMilliseconds, audioTransport.PlaybackRate);
    ScheduleEditorSeekCommit();
}

private async Task PauseMusicForEditorSeekAsync(long generation, DesktopChartTab tab)
{
    if (IsPendingEditorSeek(generation, tab))
    {
        WindowsAudioPlaybackSnapshot playbackSnapshot = audioTransport.PlaybackSnapshot;
        if (!(audioTransport.ActiveTabId != tab.Id) && (object)playbackSnapshot != null && playbackSnapshot.IsPlaying)
        {
            await audioTransport.TogglePlayPauseAsync();
        }
    }
}

private void EnsurePendingEditorSeekTransportPaused()
{
    if (shortcutSettings.Profile == EditorInteractionProfile.OsuMania)
    {
        return;
    }

    DesktopChartTab desktopChartTab = editorSeekTab;
    if (!editorSeekPending || desktopChartTab == null || !IsCurrentKeySoundTab(desktopChartTab))
    {
        return;
    }

    if (editorSeekUsesMusic)
    {
        WindowsAudioPlaybackSnapshot playbackSnapshot = audioTransport.PlaybackSnapshot;
        if (editorSeekPauseTask.IsCompleted)
        {
            Guid? activeTabId = audioTransport.ActiveTabId;
            Guid id = desktopChartTab.Id;
            if (activeTabId.HasValue && activeTabId.GetValueOrDefault() == id && (object)playbackSnapshot != null && playbackSnapshot.IsPlaying)
            {
                editorSeekPauseTask = PauseMusicForEditorSeekAsync(editorSeekGeneration, desktopChartTab);
            }
        }
    }
    else if (keySoundRuntime.Snapshot.IsSilentPlaying)
    {
        keySoundRuntime.PauseSilent();
    }
}

private async Task CommitPendingEditorSeekAsync()
{
    if (editorSeekCommitRunning || !editorSeekPending)
    {
        return;
    }

    editorSeekCommitRunning = true;
    long generation = editorSeekGeneration;
    DesktopChartTab tab = editorSeekTab;
    try
    {
        if (tab == null)
        {
            return;
        }

        await editorSeekPauseTask;
        if (!IsPendingEditorSeek(generation, tab))
        {
            return;
        }

        long targetVersion = editorSeekTargetVersion;
        double milliseconds = pendingEditorSeekMilliseconds;
        bool usesMusic = editorSeekUsesMusic;
        bool resume = editorSeekResumeRequested;
        if (usesMusic)
        {
            WindowsAudioPlaybackSnapshot playbackSnapshot = audioTransport.PlaybackSnapshot;
            if (audioTransport.ActiveTabId != tab.Id || (object)playbackSnapshot == null || !playbackSnapshot.IsLoaded)
            {
                CompletePendingEditorSeek(generation, tab);
                return;
            }

            await audioTransport.SeekAsync(TimeSpan.FromMilliseconds(milliseconds));
            if (!IsPendingEditorSeek(generation, tab) || targetVersion != editorSeekTargetVersion)
            {
                return;
            }

            if (audioTransport.LastError != null)
            {
                CompletePendingEditorSeek(generation, tab);
                return;
            }
        }
        else
        {
            keySoundRuntime.SeekSilent(milliseconds);
        }

        if (!IsPendingEditorSeek(generation, tab) || targetVersion != editorSeekTargetVersion)
        {
            return;
        }

        keySoundRuntime.ResetPlaybackCursor(milliseconds, audioTransport.PlaybackRate);
        if (resume && !playtestInProgress)
        {
            if (usesMusic)
            {
                WindowsAudioPlaybackSnapshot playbackSnapshot2 = audioTransport.PlaybackSnapshot;
                if (audioTransport.ActiveTabId == tab.Id && (object)playbackSnapshot2 != null && playbackSnapshot2.IsLoaded && !playbackSnapshot2.IsPlaying)
                {
                    await audioTransport.TogglePlayPauseAsync();
                }

                if (!IsPendingEditorSeek(generation, tab) || targetVersion != editorSeekTargetVersion)
                {
                    return;
                }

                if (audioTransport.LastError != null)
                {
                    CompletePendingEditorSeek(generation, tab);
                    return;
                }
            }
            else if (!(await keySoundRuntime.StartSilentAsync()))
            {
                if (IsPendingEditorSeek(generation, tab))
                {
                    CompletePendingEditorSeek(generation, tab);
                }

                return;
            }
        }

        if (IsPendingEditorSeek(generation, tab) && targetVersion == editorSeekTargetVersion)
        {
            CompletePendingEditorSeek(generation, tab);
        }
    }
    catch (Exception ex)
    {
        if (tab != null && IsPendingEditorSeek(generation, tab))
        {
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("定位音频失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
            CompletePendingEditorSeek(generation, tab);
        }
    }
    finally
    {
        editorSeekCommitRunning = false;
        if (editorSeekPending && !editorSeekCommitScheduled)
        {
            EnsurePendingEditorSeekTransportPaused();
            ScheduleEditorSeekCommit();
        }
    }
}

private bool IsPendingEditorSeek(long generation, DesktopChartTab tab)
{
    if (!playtestInProgress && editorSeekPending && editorSeekGeneration == generation && editorSeekTab == tab)
    {
        return IsCurrentKeySoundTab(tab);
    }

    return false;
}

private void CompletePendingEditorSeek(long generation, DesktopChartTab tab)
{
    if (editorSeekGeneration == generation && editorSeekTab == tab)
    {
        editorVisualClock.Reset();
        editorSeekNeedsFreshClock = true;
        editorSeekFreshClockUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
        CancelScheduledEditorSeekCommit();
        editorSeekPending = false;
        editorSeekTab = null;
        editorSeekUsesMusic = false;
        editorSeekResumeRequested = false;
    }
}

private Task CancelPendingEditorSeek()
{
    CancelScheduledEditorSeekCommit();
    Task task = editorSeekPauseTask;
    if (editorSeekPending || editorSeekCommitRunning)
    {
        editorSeekGeneration++;
        editorSeekTargetVersion++;
        editorSeekPending = false;
        editorSeekTab = null;
        editorSeekUsesMusic = false;
        editorSeekResumeRequested = false;
        if (!keySoundRuntimeDisposed)
        {
            keySoundRuntime.InvalidatePlayback();
        }
    }

    if (!task.IsCompletedSuccessfully)
    {
        return task;
    }

    return Task.CompletedTask;
}

private void SetProgrammaticPlaybackBeat(double beat)
{
    bool flag = settingProgrammaticPlaybackBeat;
    settingProgrammaticPlaybackBeat = true;
    try
    {
        timeline.SetCurrentBeat(beat);
        viewModel.UpdateCurrentBeat(beat);
        RememberMainTrackPosition(beat);
    }
    finally
    {
        settingProgrammaticPlaybackBeat = flag;
    }
}

private bool IsCurrentKeySoundTab(DesktopChartTab tab)
{
    if (tab == keySoundActiveTab && tab.Session == keySoundActiveSession)
    {
        return tab == viewModel.ActiveTab;
    }

    return false;
}

private void ResetKeySoundPlaybackAtCurrent(DesktopChartTab tab)
{
    if (IsCurrentKeySoundTab(tab) && !keySoundRuntimeDisposed)
    {
        WindowsAudioPlaybackSnapshot playbackSnapshot = audioTransport.PlaybackSnapshot;
        double val = (((object)playbackSnapshot != null && playbackSnapshot.IsLoaded && audioTransport.ActiveTabId == tab.Id) ? playbackSnapshot.Position.TotalMilliseconds : keySoundRuntime.Snapshot.PositionMilliseconds);
        keySoundRuntime.ResetPlaybackCursor(Math.Max(0.0, val), audioTransport.PlaybackRate);
    }
}

private void EnsureKeySoundActiveTab(DesktopChartTab? tab, string? resolvedAudioPath)
{
    if (tab == null)
    {
        if (keySoundActiveTab != null)
        {
            CancelPendingEditorSeek();
            DetachKeySoundActiveSession();
            keySoundActiveTab = null;
            keySoundObservedDocument = null;
            keySoundObservedChangeSerial = -1L;
            keySoundObservedSourcePath = null;
            keySoundResolvedAudioPath = null;
            keySoundObservedHiddenGroups.Clear();
            keySoundPlanDebounceTimer.Stop();
            keySoundPlanRebuildPending = false;
            if (!keySoundRuntimeDisposed)
            {
                keySoundRuntime.Deactivate();
            }

            RefreshKeySoundSettingsUi();
        }
    }
    else if (tab == keySoundActiveTab && tab.Session == keySoundActiveSession)
    {
        bool num = keySoundObservedDocument != tab.Session.Document || keySoundObservedChangeSerial != tab.Session.ChangeSerial || !string.Equals(keySoundObservedSourcePath, tab.SourcePath, StringComparison.OrdinalIgnoreCase);
        bool flag = !string.Equals(keySoundResolvedAudioPath, resolvedAudioPath, StringComparison.OrdinalIgnoreCase);
        if (num || flag)
        {
            CancelPendingEditorSeek();
            keySoundResolvedAudioPath = resolvedAudioPath;
            if (flag && resolvedAudioPath != null && keySoundRuntime.Snapshot.IsSilentPlaying)
            {
                keySoundRuntime.PauseSilent();
            }

            ObserveKeySoundDocumentState(tab);
            RefreshKeySoundSettingsUi();
            ScheduleKeySoundPlanRebuild();
        }
    }
    else
    {
        CancelPendingEditorSeek();
        DetachKeySoundActiveSession();
        if (!keySoundRuntimeDisposed)
        {
            keySoundRuntime.Deactivate();
        }

        keySoundActiveTab = tab;
        keySoundActiveSession = tab.Session;
        keySoundActiveSession.Changed += KeySoundActiveSession_Changed;
        keySoundResolvedAudioPath = resolvedAudioPath;
        ObserveKeySoundDocumentState(tab);
        RefreshKeySoundSettingsUi();
        ScheduleKeySoundPlanRebuild();
    }
}

private void DetachKeySoundActiveSession()
{
    if (keySoundActiveSession != null)
    {
        keySoundActiveSession.Changed -= KeySoundActiveSession_Changed;
    }

    keySoundActiveSession = null;
}

private void KeySoundActiveSession_Changed(object? sender, EventArgs eventArgs)
{
    if (!global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            KeySoundActiveSession_Changed(sender, eventArgs);
        });
        return;
    }

    DesktopChartTab desktopChartTab = keySoundActiveTab;
    if (desktopChartTab != null && sender == keySoundActiveSession && IsCurrentKeySoundTab(desktopChartTab) && (keySoundObservedDocument != desktopChartTab.Session.Document || keySoundObservedChangeSerial != desktopChartTab.Session.ChangeSerial || !string.Equals(keySoundObservedSourcePath, desktopChartTab.SourcePath, StringComparison.OrdinalIgnoreCase)))
    {
        keySoundResolvedAudioPath = WindowsAudioTransportViewModel.ResolveDocumentAudioPath(desktopChartTab);
        if (keySoundResolvedAudioPath != null && keySoundRuntime.Snapshot.IsSilentPlaying)
        {
            keySoundRuntime.PauseSilent();
        }

        ObserveKeySoundDocumentState(desktopChartTab);
        GetAudioTimingMap(desktopChartTab);
        RefreshKeySoundSettingsUi();
        ScheduleKeySoundPlanRebuild();
    }
}

private void ObserveKeySoundDocumentState(DesktopChartTab tab)
{
    keySoundObservedDocument = tab.Session.Document;
    keySoundObservedChangeSerial = tab.Session.ChangeSerial;
    keySoundObservedSourcePath = tab.SourcePath;
    keySoundObservedHiddenGroups = tab.HiddenGroups.ToHashSet();
}

private void SyncKeySoundHiddenGroups(DesktopChartTab tab)
{
    if (IsCurrentKeySoundTab(tab) && !keySoundObservedHiddenGroups.SetEquals(tab.HiddenGroups))
    {
        keySoundObservedHiddenGroups = tab.HiddenGroups.ToHashSet();
        ScheduleKeySoundPlanRebuild();
    }
}

private void ScheduleKeySoundPlanRebuild()
{
    if (!keySoundRuntimeDisposed && keySoundActiveTab != null)
    {
        keySoundPlanRebuildPending = true;
        keySoundRuntime.InvalidatePlayback();
        keySoundPlanDebounceTimer.Stop();
        keySoundPlanDebounceTimer.Start();
    }
}

private Task RequestKeySoundPlanNow()
{
    keySoundPlanDebounceTimer.Stop();
    DesktopChartTab desktopChartTab = keySoundActiveTab;
    if (keySoundRuntimeDisposed || desktopChartTab == null || !IsCurrentKeySoundTab(desktopChartTab))
    {
        keySoundPlanRebuildPending = false;
        return Task.CompletedTask;
    }

    try
    {
        ChartDocument documentClone = desktopChartTab.Session.CaptureCommittedDocument();
        double currentPositionMilliseconds = CurrentEditorPlaybackPositionMilliseconds(desktopChartTab);
        WindowsKeySoundPlaybackOptions options = new WindowsKeySoundPlaybackOptions(keySoundSettings.EnableKeySounds, keySoundSettings.Preset, keySoundSettings.CustomFilePath, keySoundSettings.PlayLongNoteEndSound, keySoundSettings.GlobalOffsetMilliseconds, keySoundSettings.GetChartOffset(desktopChartTab.SourcePath));
        keySoundPlanRebuildPending = false;
        return keySoundRuntime.RequestPlanAsync(desktopChartTab.Id, documentClone, desktopChartTab.SourcePath, desktopChartTab.HiddenGroups.ToHashSet(), options, currentPositionMilliseconds, audioTransport.PlaybackRate);
    }
    catch (Exception ex)
    {
        keySoundPlanRebuildPending = true;
        keySoundRuntimeStatusText.Text = AvaloniaLocalizationCatalog.Format("Key 音计划快照失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message));
        keySoundRuntimeStatusText.Foreground = Brushes.IndianRed;
        return Task.CompletedTask;
    }
}

private double CurrentEditorPlaybackPositionMilliseconds(DesktopChartTab tab)
{
    WindowsAudioPlaybackSnapshot playbackSnapshot = audioTransport.PlaybackSnapshot;
    Guid? activeTabId = audioTransport.ActiveTabId;
    Guid id = tab.Id;
    if (activeTabId.HasValue && activeTabId.GetValueOrDefault() == id && (object)playbackSnapshot != null && playbackSnapshot.IsLoaded)
    {
        return Math.Max(0.0, playbackSnapshot.Position.TotalMilliseconds);
    }

    WindowsKeySoundPlaybackRuntimeSnapshot snapshot = keySoundRuntime.Snapshot;
    if (snapshot.RequestedTabId == tab.Id)
    {
        return Math.Max(0.0, snapshot.PositionMilliseconds);
    }

    return Math.Max(0.0, GetAudioTimingMap(tab).BeatToMilliseconds(BeatPosition.FromDouble(timeline.CurrentBeat)));
}

private void ApplyKeySoundSettings(WindowsKeySoundSettings next)
{
    keySoundSettings = next.Normalize();
    keySoundRuntime.Volume = (float)keySoundSettings.Volume;
    keySoundSettingsDirty = true;
    RefreshKeySoundSettingsUi();
    ScheduleKeySoundSettingsSave();
    ScheduleKeySoundPlanRebuild();
}

private void RefreshKeySoundSettingsUi()
{
    bool flag = suppressKeySoundControls;
    suppressKeySoundControls = true;
    try
    {
        keySoundEnabledBox.IsChecked = keySoundSettings.EnableKeySounds;
        keySoundPresetBox.SelectedItem = keySoundPresetBox.Items.OfType<KeySoundPresetChoice>().FirstOrDefault((KeySoundPresetChoice item) => item.Preset == keySoundSettings.Preset);
        keySoundCustomFileText.Text = (string.IsNullOrWhiteSpace(keySoundSettings.CustomFilePath) ? "未选择自定义音频" : keySoundSettings.CustomFilePath);
        ToolTip.SetTip(keySoundCustomFileText, keySoundSettings.CustomFilePath);
        keySoundVolumeSlider.Value = keySoundSettings.Volume * 100.0;
        keySoundVolumeText.Text = $"{keySoundSettings.Volume:P0}";
        keySoundLongNoteEndBox.IsChecked = keySoundSettings.PlayLongNoteEndSound;
        keySoundGlobalOffsetSlider.Value = keySoundSettings.GlobalOffsetMilliseconds;
        keySoundGlobalOffsetText.Text = AvaloniaLocalizationCatalog.Format("全局 {0}", FormatSignedMilliseconds(keySoundSettings.GlobalOffsetMilliseconds));
        int num = ((keySoundActiveTab != null) ? keySoundSettings.GetChartOffset(keySoundActiveTab.SourcePath) : 0);
        keySoundChartOffsetSlider.Value = num;
        keySoundChartOffsetSlider.IsEnabled = keySoundActiveTab != null;
        keySoundChartOffsetText.Text = ((keySoundActiveTab == null) ? "当前没有活动谱面 · 合计仅使用全局值" : AvaloniaLocalizationCatalog.Format("当前谱面 {0} · 合计 {1}", FormatSignedMilliseconds(num), FormatSignedMilliseconds(keySoundSettings.GlobalOffsetMilliseconds + num)));
        UpdateKeySoundRuntimeUi();
    }
    finally
    {
        suppressKeySoundControls = flag;
    }
}

private void UpdateKeySoundRuntimeUi()
{
    if (!keySoundRuntimeDisposed)
    {
        WindowsKeySoundPlaybackRuntimeSnapshot snapshot = keySoundRuntime.Snapshot;
        keySoundRuntimeStatusText.Text = ((snapshot.LastError == null) ? snapshot.Status : (snapshot.Status + "\n" + snapshot.LastError.GetType().Name));
        TextBlock textBlock = keySoundRuntimeStatusText;
        IBrush foreground;
        if (snapshot.LastError != null)
        {
            IBrush indianRed = Brushes.IndianRed;
            foreground = indianRed;
        }
        else
        {
            foreground = MutedBrush;
        }

        textBlock.Foreground = foreground;
        WindowsKeySoundPlaybackRuntimeCounters counters = snapshot.Counters;
        AutomationProperties.SetHelpText(keySoundRuntimeStatusText, $"Preparing={snapshot.IsPreparing}; HasPlan={snapshot.HasPlan}; Enabled={snapshot.IsEnabled}; PlanEvents={snapshot.PlannedEventCount}; PlanPreloads={snapshot.PlannedPreloadCount}; PlanFailures={counters.PlanFailures}; PreloadAccepted={counters.PreloadAccepted}; PreloadRejected={counters.PreloadRejected}; PreviewAccepted={counters.PreviewAccepted}; PreviewRejected={counters.PreviewRejected}; TriggersDue={counters.TriggersDue}; TriggersAccepted={counters.TriggersAccepted}; TriggersRejected={counters.TriggersRejected}; SharedAccepted={counters.SharedAccepted}; SharedRejected={counters.SharedRejected}; StandaloneAccepted={counters.StandaloneAccepted}; StandaloneRejected={counters.StandaloneRejected}; LastError={snapshot.LastError?.GetType().Name ?? "none"}");
    }
}

private void ScheduleKeySoundSettingsSave()
{
    keySoundSettingsSaveTimer.Stop();
    keySoundSettingsSaveTimer.Start();
}

private void PersistPendingKeySoundSettings(bool force = false)
{
    if (keySoundSettingsSaveTimer.IsEnabled)
    {
        keySoundSettingsSaveTimer.Stop();
    }

    if (!keySoundSettingsDirty)
    {
        return;
    }

    try
    {
        WindowsKeySoundSettingsStore.Save(keySoundSettings);
        keySoundSettingsDirty = false;
    }
    catch (Exception ex)when ((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException) ? true : false)
    {
        keySoundRuntimeStatusText.Text = AvaloniaLocalizationCatalog.Format("Key 音设置已应用，但保存失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message));
        keySoundRuntimeStatusText.Foreground = Brushes.IndianRed;
        if (!force)
        {
            ScheduleKeySoundSettingsSave();
        }
    }
}

private void DisplayScaleSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs)
{
    if (!suppressDisplayScaleControls)
    {
        displayScaleSettings = ((sender == fontScaleSlider) ? displayScaleController.UpdateFontScale(fontScaleSlider.Value / 100.0, ResolveDisplayScaleScreen()) : displayScaleController.UpdateUiScale(uiScaleSlider.Value / 100.0, ResolveDisplayScaleScreen()));
        displayScaleSettingsDirty = true;
        RefreshDisplayScaleSettingsUi();
        ScheduleDisplayScaleSettingsSave();
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("界面缩放 {0:P0} · 字体缩放 {1:P0}", displayScaleSettings.UiScale, displayScaleSettings.FontScale));
    }
}

private void ResetFloatingLayout()
{
    draggingAudioPanelKind = null;
    audioPanelSettings = WindowsAudioPanelLayoutPolicy.Reset().WithSpectrogramAppearance(audioPanelSettings.BackgroundSpectrogramStrength, audioPanelSettings.SpectrogramDetail);
    audioPanelSettingsDirty = true;
    editorViewSettings = editorViewSettings.ResetFloatingLayout();
    factoryFloatingLayoutPending = true;
    ApplyEditorViewSettings();
    MarkEditorViewSettingsDirty();
    ApplyAudioPanelLayout();
    UpdateAudioVisualizationUi();
    QueueFactoryFloatingLayout();
    ScheduleAudioPanelSettingsSave();
    viewModel.SetStatus("浮窗已重置并吸附到可编辑轨道外");
}

private void RefreshDisplayScaleSettingsUi()
{
    bool flag = suppressDisplayScaleControls;
    suppressDisplayScaleControls = true;
    try
    {
        if (automaticUiScaleBox != null)
        {
            automaticUiScaleBox.IsChecked = displayScaleSettings.AutomaticUiScale;
        }

        uiScaleSlider.Value = displayScaleSettings.UiScale * 100.0;
        fontScaleSlider.Value = displayScaleSettings.FontScale * 100.0;
        uiScaleValueText.Text = $"{displayScaleSettings.UiScale:P0}";
        fontScaleValueText.Text = $"{displayScaleSettings.FontScale:P0}";
        uiScaleDecreaseButton.IsEnabled = uiScaleSlider.Value > uiScaleSlider.Minimum;
        uiScaleIncreaseButton.IsEnabled = uiScaleSlider.Value < uiScaleSlider.Maximum;
        fontScaleDecreaseButton.IsEnabled = fontScaleSlider.Value > fontScaleSlider.Minimum;
        fontScaleIncreaseButton.IsEnabled = fontScaleSlider.Value < fontScaleSlider.Maximum;
        displayScaleStatusText.Text = (displayScaleSettings.DisplayScaleInitialized ? AvaloniaLocalizationCatalog.Format("当前已应用：界面 {0:P0} · 字体 {1:P0}", displayScaleSettings.UiScale, displayScaleSettings.FontScale) : "首次显示窗口时将按主屏有效分辨率初始化。");
        displayScaleStatusText.Foreground = MutedBrush;
    }
    finally
    {
        suppressDisplayScaleControls = flag;
    }
}

private Screen? ResolveDisplayScaleScreen()
{
    try
    {
        return Screens?.ScreenFromWindow(this) ?? Screens?.Primary;
    }
    catch (InvalidOperationException)
    {
        return null;
    }
}

private void ScheduleDisplayScaleSettingsSave()
{
    displayScaleSettingsSaveTimer.Stop();
    displayScaleSettingsSaveTimer.Start();
}

private void ScheduleAudioPanelSettingsSave()
{
    audioPanelSettingsSaveTimer.Stop();
    audioPanelSettingsSaveTimer.Start();
}

private void PersistPendingAudioPanelSettings(bool force = false)
{
    if (audioPanelSettingsSaveTimer.IsEnabled)
    {
        audioPanelSettingsSaveTimer.Stop();
    }

    if (!audioPanelSettingsDirty)
    {
        return;
    }

    try
    {
        WindowsAudioPanelSettingsStore.Save(audioPanelSettings);
        audioPanelSettingsDirty = false;
    }
    catch (Exception ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("音频面板布局已应用，但保存失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
        if (!force)
        {
            ScheduleAudioPanelSettingsSave();
        }
    }
}

private void PersistPendingDisplayScaleSettings(bool force = false)
{
    if (displayScaleSettingsSaveTimer.IsEnabled)
    {
        displayScaleSettingsSaveTimer.Stop();
    }

    if (!displayScaleSettingsDirty)
    {
        return;
    }

    try
    {
        WindowsDisplayScaleSettingsStore.Save(displayScaleSettings);
        displayScaleSettingsDirty = false;
    }
    catch (Exception ex)
    {
        displayScaleStatusText.Text = AvaloniaLocalizationCatalog.Format("缩放已应用，但保存失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message));
        displayScaleStatusText.Foreground = Brushes.IndianRed;
        if (!force)
        {
            ScheduleDisplayScaleSettingsSave();
        }
    }
}

private void QueueDisplayScaleMaterializedContentScan()
{
    if (displayScaleMaterializedScanQueued || windowClosePreflightComplete)
    {
        return;
    }

    displayScaleMaterializedScanQueued = true;
    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        displayScaleMaterializedScanQueued = false;
        if (!windowClosePreflightComplete)
        {
            displayScaleController.ApplyFontToMaterializedContent();
        }
    }, DispatcherPriority.Background);
}

private async Task SelectKeySoundFileAsync()
{
    string text = await PickLocalFileAsync(new FilePickerOpenOptions { Title = "选择自定义 Key 音", AllowMultiple = false, FileTypeFilter = new FilePickerFileType[]
{
    new FilePickerFileType("音频文件")
    {
        Patterns = new string[3] { "*.wav", "*.ogg", "*.mp3" },
        MimeTypes = new string[]
{
    "audio/*"
}
    }
} }, PickedFileRetention.Durable);
    if (string.IsNullOrWhiteSpace(text))
    {
        return;
    }

    try
    {
        text = System.IO.Path.GetFullPath(text);
        ApplyKeySoundSettings(new WindowsKeySoundSettings(keySoundSettings.EnableKeySounds, keySoundSettings.Volume, KeySoundPreset.Custom, text, keySoundSettings.PlayLongNoteEndSound, keySoundSettings.GlobalOffset, keySoundSettings.PerChartOffsets));
    }
    catch (Exception ex)
    {
        keySoundRuntimeStatusText.Text = AvaloniaLocalizationCatalog.Format("选择 Key 音失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message));
        keySoundRuntimeStatusText.Foreground = Brushes.IndianRed;
    }
}

private static string FormatSignedMilliseconds(double value)
{
    if (value > 0.5)
    {
        return $"+{value:0} ms";
    }

    return $"{value:0} ms";
}

private void Timeline_CurrentBeatChanged(object? sender, double beat)
{
    if (settingProgrammaticPlaybackBeat)
    {
        AdvanceTrackPlaybackViewports();
    }
    else
    {
        SyncTrackViewports();
    }

    eventTimeRuler.SetCurrentBeat(beat);
    if (settingProgrammaticPlaybackBeat)
    {
        return;
    }

    UpdatePlaybackPositionUi(beat);
    QueuePositionInspectorRefresh();
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null || !IsCurrentKeySoundTab(activeTab))
    {
        return;
    }

    double num = GetAudioTimingMap(activeTab).BeatToMilliseconds(BeatPosition.FromDouble(beat, 1000000));
    int num2;
    if (audioTransport.ActiveTabId == activeTab.Id)
    {
        WindowsAudioPlaybackSnapshot? playbackSnapshot = audioTransport.PlaybackSnapshot;
        if ((object)playbackSnapshot != null && playbackSnapshot.IsPlaying)
        {
            num2 = 1;
            goto IL_00d8;
        }
    }

    num2 = ((editorSeekPending && editorSeekTab == activeTab && editorSeekUsesMusic && editorSeekResumeRequested) ? 1 : 0);
    goto IL_00d8;
    IL_00d8:
        bool flag = (byte)num2 != 0;
    double milliseconds = Math.Max(0.0, flag ? EditorAudioVisualCalibration.AudioTime(num, audioTransport.PlaybackRate, editorAudioVisualOffsetMilliseconds) : num);
    if (flag && editorAudioVisualOffsetMilliseconds != 0)
    {
        lastCalibratedCursorMilliseconds = num;
        lastCalibratedCursorRate = audioTransport.PlaybackRate;
        lastCalibratedCursorOffsetMilliseconds = editorAudioVisualOffsetMilliseconds;
        lastCalibratedCursorTabId = activeTab.Id;
    }

    QueueEditorSeek(activeTab, milliseconds, DateTimeOffset.UtcNow > editorSeekResumeSuppressedUntilUtc);
}

private void Timeline_ManualNavigationRequested(object? sender, TimelineManualNavigationEventArgs eventArgs)
{
    if (shortcutSettings.Profile == EditorInteractionProfile.OsuMania)
    {
        return;
    }

    editorSeekResumeSuppressedUntilUtc = DateTimeOffset.UtcNow.AddMilliseconds(650.0);
    if (editorSeekPending)
    {
        editorSeekResumeRequested = false;
        EnsurePendingEditorSeekTransportPaused();
        UpdateQuickPlayPresentation();
    }

    long version = ++editorManualNavigationPauseVersion;
    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (version == editorManualNavigationPauseVersion && editorManualNavigationPauseTask.IsCompleted)
        {
            editorManualNavigationPauseTask = PauseEditorPlaybackForManualNavigationAsync();
        }
    }, DispatcherPriority.Input);
}

private async Task PauseEditorPlaybackForManualNavigationAsync()
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null || !IsCurrentKeySoundTab(activeTab))
    {
        return;
    }

    if (editorSeekPending && editorSeekTab == activeTab)
    {
        editorSeekResumeRequested = false;
        EnsurePendingEditorSeekTransportPaused();
        UpdateQuickPlayPresentation();
        return;
    }

    WindowsAudioPlaybackSnapshot playbackSnapshot = audioTransport.PlaybackSnapshot;
    if (audioTransport.ActiveTabId == activeTab.Id && (object)playbackSnapshot != null && playbackSnapshot.IsPlaying)
    {
        keySoundRuntime.InvalidatePlayback();
        await audioTransport.TogglePlayPauseAsync();
    }
    else if (keySoundRuntime.Snapshot.IsSilentPlaying)
    {
        keySoundRuntime.PauseSilent();
    }
}

private void UpdatePlaybackPositionUi(double beat)
{
    viewModel.UpdateCurrentBeat(beat);
    RememberMainTrackPosition(beat);
    editorOverview.SetCurrentBeat(beat);
    difficultyOverview.SetCurrentBeat(beat);
    UpdateAudioPanelViewports();
}

private void Timeline_TrackPanChanged(object? sender, ProEditorTrackPanState state)
{
    if (global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
    {
        RefreshProEditorControls(state);
        QueueAudioPanelLayout();
        return;
    }

    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        RefreshProEditorControls(state);
        QueueAudioPanelLayout();
    });
}

private void RefreshProEditorControls(ProEditorTrackPanState? panState = null)
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    bool flag = activeTab != null && timeline.ProEditorEnabled;
    suppressProEditorControls = true;
    try
    {
        proEditorButton.IsEnabled = activeTab != null;
        proModeLabel.Text = (timeline.ProEditorEnabled ? "Pro ON" : "Pro OFF");
        Button button = proEditorButton;
        IBrush background;
        if (!flag)
        {
            IBrush transparent = Brushes.Transparent;
            background = transparent;
        }
        else
        {
            IBrush transparent = new SolidColorBrush(Color.Parse("#22282A"));
            background = transparent;
        }

        button.Background = background;
        proEditorButton.Foreground = (flag ? new SolidColorBrush(Color.Parse("#F0F2F2")) : new SolidColorBrush(Color.Parse("#D0D4D6")));
        Button button2 = proEditorButton;
        IBrush borderBrush;
        if (!flag)
        {
            IBrush transparent = Brushes.Transparent;
            borderBrush = transparent;
        }
        else
        {
            IBrush transparent = new SolidColorBrush(Color.Parse("#5F7E82"));
            borderBrush = transparent;
        }

        button2.BorderBrush = borderBrush;
        int num = activeTab?.Session.Document.KeyCount ?? 1;
        proGridColumnSlider.Minimum = num;
        proGridColumnSlider.Maximum = 64.0;
        proGridColumnSlider.Value = Math.Clamp(timeline.ProGridColumnCount, num, 64);
        proGridColumnSlider.IsEnabled = activeTab != null;
        proGridColumnText.Text = AvaloniaLocalizationCatalog.Format("{0} 列", timeline.ProGridColumnCount);
        proGridColumnText.IsEnabled = activeTab != null;
        ProEditorTrackPanState proEditorTrackPanState = panState ?? timeline.GetTrackPanState();
        proTrackPanBar.IsVisible = flag;
        proTrackPanSlider.Minimum = proEditorTrackPanState.Minimum;
        proTrackPanSlider.Maximum = Math.Max(proEditorTrackPanState.Minimum, proEditorTrackPanState.Maximum);
        proTrackPanSlider.Value = Math.Clamp(proEditorTrackPanState.Value, proTrackPanSlider.Minimum, proTrackPanSlider.Maximum);
        proTrackPanSlider.IsEnabled = flag && proEditorTrackPanState.Enabled;
    }
    finally
    {
        suppressProEditorControls = false;
    }
}

private void ToggleSubdivisionCheck()
{
    bool valueOrDefault = subdivisionCheckButton.IsChecked == true;
    timeline.SetSubdivisionCheckEnabled(valueOrDefault);
    RefreshSubdivisionCheckControls();
    if (!valueOrDefault)
    {
        viewModel.SetStatus("分度检查已关闭");
        timeline.Focus();
        return;
    }

    List<ChartNote> source = viewModel.ActiveTab?.Session.Document.Notes ?? new List<ChartNote>();
    int num = source.Count((ChartNote note) =>
    {
        if (TimelineSubdivisionCheckPalette.IsSupportedDenominator(note.Beat.Denominator))
        {
            BeatPosition? endBeat = note.EndBeat;
            if (endBeat.HasValue)
            {
                return !TimelineSubdivisionCheckPalette.IsSupportedDenominator(endBeat.GetValueOrDefault().Denominator);
            }

            return false;
        }

        return true;
    });
    int num2 = source.SelectMany((ChartNote note) =>
    {
        BeatPosition? endBeat = note.EndBeat;
        if (!endBeat.HasValue)
        {
            return new int[1]
            {
                TimelineSubdivisionCheckPalette.NormalizeDenominator(note.Beat.Denominator)
            };
        }

        BeatPosition valueOrDefault2 = endBeat.GetValueOrDefault();
        return new int[2]
        {
            TimelineSubdivisionCheckPalette.NormalizeDenominator(note.Beat.Denominator),
            TimelineSubdivisionCheckPalette.NormalizeDenominator(valueOrDefault2.Denominator)
        };
    }).Distinct().Count();
    viewModel.SetStatus((num == 0) ? AvaloniaLocalizationCatalog.Format("分度检查已开启：{0} 种分度，未发现非常规分度 Note", num2) : AvaloniaLocalizationCatalog.Format("分度检查已开启：{0} 种分度；{1} 个非常规分度 Note 已标红", num2, num));
    timeline.Focus();
}

private void RefreshSubdivisionCheckControls()
{
    bool subdivisionCheckEnabled = timeline.SubdivisionCheckEnabled;
    subdivisionCheckButton.IsEnabled = viewModel.ActiveTab != null;
    subdivisionCheckButton.IsChecked = subdivisionCheckEnabled;
    ToggleButton toggleButton = subdivisionCheckButton;
    IBrush background;
    if (!subdivisionCheckEnabled)
    {
        IBrush transparent = Brushes.Transparent;
        background = transparent;
    }
    else
    {
        IBrush transparent = new SolidColorBrush(Color.Parse("#253336"));
        background = transparent;
    }

    toggleButton.Background = background;
    subdivisionCheckButton.Foreground = (subdivisionCheckEnabled ? new SolidColorBrush(Color.Parse("#F0F2F2")) : new SolidColorBrush(Color.Parse("#D0D4D6")));
    ToggleButton toggleButton2 = subdivisionCheckButton;
    IBrush borderBrush;
    if (!subdivisionCheckEnabled)
    {
        IBrush transparent = Brushes.Transparent;
        borderBrush = transparent;
    }
    else
    {
        IBrush transparent = new SolidColorBrush(Color.Parse("#628B90"));
        borderBrush = transparent;
    }

    toggleButton2.BorderBrush = borderBrush;
    AutomationProperties.SetHelpText(subdivisionCheckButton, subdivisionCheckEnabled ? "分度检查已开启；常规分度使用不同颜色，非常规分度使用红色" : "分度检查已关闭");
}

private void ScheduleProGridColumnSave()
{
    proGridColumnSaveTimer.Stop();
    proGridColumnSaveTimer.Start();
}

private void PersistPendingProGridColumns()
{
    if (!proGridColumnSaveTimer.IsEnabled)
    {
        return;
    }

    proGridColumnSaveTimer.Stop();
    try
    {
        AvaloniaAppPaths.SaveProGridColumns(persistedProGridColumns);
    }
    catch (Exception ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("ProEditor 列数已应用，但偏好保存失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private void Timeline_TimingSelectionChanged(object? sender, EventArgs eventArgs)
{
    if (!suppressTimingSelection)
    {
        HashSet<Guid> timingIds = timeline.SelectedTimingIds.ToHashSet();
        SyncTimingListSelection(timingIds, timeline.PrimarySelectedTimingId);
        if (timeline.PrimarySelectedTimingId.HasValue)
        {
            TimingListItem timingListItem = timingList.Items.OfType<TimingListItem>().FirstOrDefault((TimingListItem item) => item.Id == timeline.PrimarySelectedTimingId.Value);
            if ((object)timingListItem != null)
            {
                timingList.ScrollIntoView(timingListItem);
            }
        }
    }

    UpdateSelectionUi();
}

private void TimingList_PointerPressed(object? sender, PointerPressedEventArgs eventArgs)
{
    CaptureTimingListSelectionModifiers(eventArgs.KeyModifiers);
    if (eventArgs.GetCurrentPoint(timingList).Properties.IsRightButtonPressed)
    {
        eventArgs.Handled = true;
    }
}

private void TimingList_KeyDown(object? sender, KeyEventArgs eventArgs)
{
    CaptureTimingListSelectionModifiers(eventArgs.KeyModifiers);
    Key key = eventArgs.Key;
    PhysicalKey physicalKey = eventArgs.PhysicalKey;
    string keySymbol = eventArgs.KeySymbol;
    KeyModifiers keyModifiers = eventArgs.KeyModifiers;
    Key key2 = eventArgs.Key;
    bool flag = (((uint)(key2 - 155) <= 1u || key2 == Key.DeadCharProcessed) ? true : false);
    switch (WindowsInspectorParityPolicy.ResolveTimingListShortcut(key, physicalKey, keySymbol, keyModifiers, flag ? WindowsShortcutPhysicalKeyState.TryResolvePressedKey() : ((Key? )null)))
    {
        case WindowsTimingListShortcutCommand.Delete:
            DeleteSelectedTiming();
            eventArgs.Handled = true;
            break;
        case WindowsTimingListShortcutCommand.CopyEffect:
            CopyTimingSelection();
            eventArgs.Handled = true;
            break;
        case WindowsTimingListShortcutCommand.PasteEffect:
            PasteTimingSelection();
            eventArgs.Handled = true;
            break;
    }
}

private void CaptureTimingListSelectionModifiers(KeyModifiers modifiers)
{
    timingListSelectionModifiers = modifiers;
    long version = ++timingListInputVersion;
    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (version == timingListInputVersion)
        {
            timingListSelectionModifiers = KeyModifiers.None;
        }
    });
}

private void TimingList_SelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
{
    if (suppressTimingSelection)
    {
        return;
    }

    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab != null)
    {
        if (!WindowsAvaloniaEditorInputPolicy.PreserveNotesForTimingListSelection(timingListSelectionModifiers))
        {
            activeTab.Session.Selection.Clear();
        }

        HashSet<Guid> timingIds = SelectedTimingListIds();
        TimingListItem timingListItem = timingList.SelectedItem as TimingListItem;
        bool flag = suppressTimingSelection;
        suppressTimingSelection = true;
        try
        {
            timeline.SetSelectedTimings(timingIds, timingListItem?.Id);
        }
        finally
        {
            suppressTimingSelection = flag;
        }

        if (timingListItem != null)
        {
            PopulateTimingFields(timingListItem.Id);
        }

        UpdateSelectionUi();
    }
}

private void UpdateUi()
{
    if (!WindowsInspectorParityPolicy.AllowWindowUiMutation(windowCloseTeardownStarted, windowClosePreflightComplete))
    {
        return;
    }

    Guid? guid = viewModel.ActiveTab?.Id;
    if (audioAnalysisCancellation != null && audioAnalysisTabId != guid)
    {
        audioAnalysisCancellation.Cancel();
    }

    if (imageImportCancellation != null && imageImportTabId != guid)
    {
        imageImportCancellation.Cancel();
    }

    if (titleRomanizationCancellation != null && titleRomanizationTabId != guid)
    {
        titleRomanizationCancellation.Cancel();
    }

    if (artistRomanizationCancellation != null && artistRomanizationTabId != guid)
    {
        artistRomanizationCancellation.Cancel();
    }

    RefreshLibrarySourceChoices();
    workspaceText.Text = "创作目录  " + viewModel.CreationRoot + ((viewModel.LibraryWarnings.Count == 0) ? "" : ("  · 提示：" + string.Join("；", viewModel.LibraryWarnings)));
    ToolTip.SetTip(workspaceText, workspaceText.Text);
    AutomationProperties.SetName(workspaceText, workspaceText.Text);
    statusText.Text = viewModel.Status;
    libraryStatusText.Text = viewModel.Status;
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null)
    {
        libraryVisible = true;
    }

    if (evidenceFocusLibraryDeletion || evidenceFocusLibraryVirtualization)
    {
        libraryVisible = true;
    }

    WindowsLibrarySurfaceState windowsLibrarySurfaceState = WindowsLibrarySurfacePolicy.Resolve(libraryVisible, activeTab != null, activeTab?.Session.Document.Metadata.Title, activeTab?.Session.IsDirty ?? false);
    libraryVisible = windowsLibrarySurfaceState.LibraryVisible;
    titleSaveStateText.IsVisible = windowsLibrarySurfaceState.SaveStateVisible;
    titleSaveStateText.Text = ((activeTab != null && activeTab.Session.IsDirty) ? "· 未保存" : "· 已保存");
    titleSaveStateText.Foreground = ((activeTab != null && activeTab.Session.IsDirty) ? new SolidColorBrush(Color.Parse("#D6A35C")) : new SolidColorBrush(Color.Parse("#7FAE93")));
    Title = localizationController.Translate(windowsLibrarySurfaceState.WindowTitle).Replace("LumaChart Studio", "SimpleManiaEditor", StringComparison.Ordinal);
    RefreshMapperLibraryBrowser();
    WindowsLibraryContentState windowsLibraryContentState = WindowsLibraryContentPolicy.Resolve(viewModel.LibraryEntries.Count, viewModel.LibraryFolders.Count, (viewModel.LibrarySearchQuery.Length > 0) ? viewModel.LibrarySearchQuery : viewModel.LibrarySourceFilter, viewModel.IsScanning || viewModel.IsLibraryCacheLoading);
    libraryList.IsVisible = true;
    emptyLibraryPanel.IsVisible = windowsLibraryContentState.ShowEmptyPanel;
    emptyLibraryActions.IsVisible = windowsLibraryContentState.ShowLauncherActions;
    emptyLibraryTitleText.Text = windowsLibraryContentState.EmptyTitle;
    emptyLibrarySubtitleText.Text = windowsLibraryContentState.EmptySubtitle;
    libraryTitle.Text = "曲库";
    TextBlock textBlock = libraryCountText;
    string? text;
    if (viewModel.IsScanning)
    {
        text = "扫描中";
    }
    else
    {
        text = (viewModel.IsLibraryCacheLoading ? "载入本地索引…" : AvaloniaLocalizationCatalog.Format("{0:N0} 个文件夹  ·  {1:N0} 张谱面", viewModel.LibraryFolders.Count, viewModel.LibraryFolders.Sum((DesktopLibraryFolderView folder) => folder.VisibleCharts.Count)));
    }

    textBlock.Text = text;
    libraryCacheText.Text = viewModel.LibraryIndexSummary;
    libraryRefreshButton.Content = viewModel.LibraryRefreshButtonText;
    libraryRefreshButton.IsEnabled = !viewModel.IsScanning;
    UpdateLibrarySelectionUi();
    RebuildTabs();
    if (viewModel.ActiveTab != null)
    {
        editorView.IsVisible = windowsLibrarySurfaceState.EditorVisible;
        libraryView.IsVisible = windowsLibrarySurfaceState.LibraryVisible;
        audioTransport.Activate(viewModel.ActiveTab);
        string text2 = WindowsAudioTransportViewModel.ResolveDocumentAudioPath(viewModel.ActiveTab);
        RequestMissingAudioRecovery(viewModel.ActiveTab, text2);
        audioVisualization.Activate(viewModel.ActiveTab.Id, text2, viewModel.ActiveTab.Session.Document.Metadata.AudioFile);
        DesktopChartTab activeTab2 = viewModel.ActiveTab;
        WindowsGroupManagerCommand.RemoveHiddenSelection(activeTab2);
        timeline.Session = activeTab2.Session;
        editorOverview.Session = activeTab2.Session;
        editorOverview.SetIssues(timeline.ChartIssues);
        editorOverview.SetNavigationMarkers(activeTab2.NavigationMarkers);
        difficultyOverview.SetEndBeat(editorOverview.DisplayEndBeat);
        RefreshEventTimeRuler();
        editorOverview.SetCurrentBeat((IsEditorPlaybackActive() && activeTab2 == keySoundActiveTab) ? timeline.CurrentBeat : activeTab2.CurrentBeat);
        RefreshNavigationMarkerUi();
        miniNoteCountText.Text = activeTab2.Session.Document.Notes.Count.ToString("N0", CultureInfo.InvariantCulture);
        persistedProGridColumns = Math.Max(persistedProGridColumns, activeTab2.Session.Document.KeyCount);
        timeline.SetProGridColumnCount(persistedProGridColumns);
        activeTab2.HiddenGroups.Clear();
        activeTab2.ActivePlacementGroup = 0;
        timeline.PlacementGroup = 0;
        if (quickGroupLabel != null)
        {
            quickGroupLabel.Text = AvaloniaLocalizationCatalog.Format("分组 {0}", activeTab2.ActivePlacementGroup);
        }

        timeline.SetHiddenGroups(activeTab2.HiddenGroups);
        RefreshTimelineOnlineCreators();
        if (!IsEditorPlaybackActive() || activeTab2 != keySoundActiveTab)
        {
            SetProgrammaticPlaybackBeat(activeTab2.CurrentBeat);
        }

        activeTab2.Session.SnapDivision = editorViewSettings.SnapDivision;
        divisionPicker.SelectedItem = activeTab2.Session.SnapDivision;
        SyncSettingsSnapBox(activeTab2.Session.SnapDivision);
        RefreshQuickValuesPaletteValues();
        GetAudioTimingMap(activeTab2);
        EnsureKeySoundActiveTab(activeTab2, text2);
        RefreshTimingList();
    }
    else
    {
        editorView.IsVisible = false;
        libraryView.IsVisible = true;
        audioTransport.Activate(null);
        CancelMissingAudioRecovery();
        audioVisualization.Activate(null, null);
        timeline.Session = null;
        editorOverview.Session = null;
        editorOverview.SetIssues(Array.Empty<ChartIssue>());
        editorOverview.SetNavigationMarkers(Array.Empty<DesktopNavigationMarker>());
        eventTimeRuler.SetDocument(null, null, 0.0, Array.Empty<DesktopNavigationMarker>());
        editorOverview.SetExternalEndBeat(0.0);
        RefreshNavigationMarkerUi();
        miniNoteCountText.Text = "0";
        timeline.PlacementGroup = 0;
        if (quickGroupLabel != null)
        {
            quickGroupLabel.Text = "分组 0";
        }

        timeline.SetHiddenGroups(Array.Empty<int>());
        RefreshTimelineOnlineCreators();
        timingList.ItemsSource = Array.Empty<TimingListItem>();
        timingListTabId = null;
        timingListChangeSerial = -1L;
        SyncTimingListSelection(Array.Empty<Guid>());
        audioTimingMapCache.Clear();
        EnsureKeySoundActiveTab(null, null);
    }

    RefreshTrackWorkspace(viewModel.ActiveTab);
    statusBar.IsVisible = windowsLibrarySurfaceState.EditorStatusVisible;
    RefreshDifficultyUi();
    UpdateInspector();
    UpdateChartBackground(viewModel.ActiveTab);
    UpdateEditorAvailability();
    UpdateAudioUi();
    UpdateAudioVisualizationUi();
    RefreshProEditorControls();
    RefreshSubdivisionCheckControls();
    RefreshRoomCollaborationPanelVisibility();
    QueueDisplayScaleMaterializedContentScan();
    QueueFactoryFloatingLayout();
}

private void RequestMissingAudioRecovery(DesktopChartTab tab, string? resolvedAudioPath)
{
    string audioFile = tab.Session.Document.Metadata.AudioFile;
    if (!string.IsNullOrWhiteSpace(resolvedAudioPath) || string.IsNullOrWhiteSpace(audioFile))
    {
        CancelMissingAudioRecovery();
        return;
    }

    string text = tab.Id.ToString("N") + "\0" + tab.WorkingPath + "\0" + tab.SourcePath + "\0" + audioFile;
    if (!string.Equals(missingAudioRecoveryKey, text, StringComparison.Ordinal))
    {
        missingAudioRecoveryCancellation?.Cancel();
        CancellationTokenSource cancellation = (missingAudioRecoveryCancellation = new CancellationTokenSource());
        missingAudioRecoveryKey = text;
        activeMissingAudioRecoveryTask = RecoverMissingAudioAsync(tab, audioFile, text, cancellation);
    }
}

private async Task RecoverMissingAudioAsync(DesktopChartTab tab, string reference, string recoveryKey, CancellationTokenSource cancellation)
{
    try
    {
        IReadOnlyList<string> roots = AvaloniaAppPaths.EnumerateCompatibilityWorkspaceRoots();
        string candidate = await Task.Run(() => ChartAssetImportService.FindRelatedAsset(tab.WorkingPath, tab.SourcePath, reference, roots, cancellation.Token), cancellation.Token);
        if (candidate != null)
        {
            ChartAssetImportResult chartAssetImportResult = await Task.Run(() => ChartAssetImportService.ImportAudio(tab.WorkingPath, candidate, tab.SourcePath, reference), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!(viewModel.ActiveTab?.Id != tab.Id) && string.Equals(missingAudioRecoveryKey, recoveryKey, StringComparison.Ordinal))
            {
                viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已从曲库自动恢复音频：{0}", chartAssetImportResult.FileName));
                UpdateUi();
            }
        }
    }
    catch (OperationCanceledException)
    {
    }
    catch (Exception ex2)when ((ex2 is ArgumentException || ex2 is IOException || ex2 is NotSupportedException || ex2 is UnauthorizedAccessException) ? true : false)
    {
        if (viewModel.ActiveTab?.Id == tab.Id && string.Equals(missingAudioRecoveryKey, recoveryKey, StringComparison.Ordinal))
        {
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("自动恢复音频失败：{0}", AvaloniaLocalizationCatalog.Text(ex2.Message)));
        }
    }
    finally
    {
        if (missingAudioRecoveryCancellation == cancellation)
        {
            missingAudioRecoveryCancellation = null;
        }

        cancellation.Dispose();
    }
}

private void CancelMissingAudioRecovery()
{
    missingAudioRecoveryCancellation?.Cancel();
    missingAudioRecoveryKey = null;
}

private void UpdateInspector()
{
    DesktopChartTab tab = viewModel.ActiveTab;
    RefreshMetadataEditor(tab);
    UpdateNoteInspector(tab);
    RefreshGroupPanel();
    if (tab == null)
    {
        inspectorText.Text = "没有打开的谱面";
        return;
    }

    ChartDocument document = tab.Session.Document;
    ChartNote[] array = document.Notes.Where((ChartNote note) => tab.Session.Selection.Contains(note.Id) && !tab.HiddenGroups.Contains(note.Group)).ToArray();
    int num = timeline.SelectedTimingIds.Count((Guid id) => document.Timing.Any((TimingEvent timing) => timing.Id == id));
    string text = ((array.Length == 0) ? "未选择 Note" : string.Join("\n", from note in array.Take(4)select (note.IsDraft ? $"Draft D:{note.DraftColumn} · {note.Beat}" : $"Lane {note.Lane + 1} · {note.Beat}") + (note.EndBeat.HasValue ? $" → {note.EndBeat}" : string.Empty)));
    inspectorText.Text = AvaloniaLocalizationCatalog.Format("{0}\n{1} · {2}K\nNote {3:N0} · LN {4:N0}\nTiming {5:N0}\n红线 {6:F3}\n分度 1/{7}\n选择 {8:N0} Note · {9:N0} Timing\n权限：只读 {10} · Note {11} · Timing {12} · 元数据 {13}\n房主锁：{14:N0} Note · {15:N0} 连续区域\n未保存：{16}\n\n{17}", tab.DisplayTitle, tab.FormatBadge, document.KeyCount, document.Notes.Count, document.Notes.Count((ChartNote note) => note.IsHold), document.Timing.Count, timeline.CurrentBeat, tab.Session.SnapDivision, array.Length, num, tab.Session.IsReadOnly ? AvaloniaLocalizationCatalog.Text("是") : AvaloniaLocalizationCatalog.Text("否"), tab.Session.CanEditNotes ? AvaloniaLocalizationCatalog.Text("可编辑") : AvaloniaLocalizationCatalog.Text("禁用"), tab.Session.CanEditTiming ? AvaloniaLocalizationCatalog.Text("可编辑") : AvaloniaLocalizationCatalog.Text("禁用"), tab.Session.CanEditMetadata ? AvaloniaLocalizationCatalog.Text("可编辑") : AvaloniaLocalizationCatalog.Text("禁用"), tab.Session.OwnerProtectedNotes.Count, tab.Session.OwnerProtectedRegions.Count, tab.Session.IsDirty ? AvaloniaLocalizationCatalog.Text("是") : AvaloniaLocalizationCatalog.Text("否"), text);
}

private void MetadataEditorControlChanged(object? sender, EventArgs eventArgs)
{
    if (!suppressMetadataEditorControls)
    {
        DesktopChartTab? activeTab = viewModel.ActiveTab;
        if (activeTab == null || !activeTab.IsLibraryReadOnly)
        {
            metadataEditorDirty = true;
        }
    }
}

private async void MetadataKeyCountSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
{
    if (suppressMetadataEditorControls || metadataKeyCountChangeInProgress)
    {
        return;
    }

    object selectedItem = metadataKeyCountBox.SelectedItem;
    WindowsMetadataKeyCountOption option = selectedItem as WindowsMetadataKeyCountOption;
    if ((object)option == null)
    {
        return;
    }

    DesktopChartTab tab = viewModel.ActiveTab;
    if (tab == null || option.Value == tab.Session.Document.KeyCount)
    {
        return;
    }

    metadataEditorDirty = true;
    metadataKeyCountChangeInProgress = true;
    try
    {
        int num = 0;
        while (true)
        {
            int removed = tab.Session.Document.Notes.Count((ChartNote note) => !note.IsDraft && note.Lane >= option.Value);
            if (removed <= num)
            {
                break;
            }

            if (!(await ShowSettingsConfirmationAsync("更改轨道数", AvaloniaLocalizationCatalog.Format("切换为 {0}K 会删除最右侧的 {1} 个 Note，继续吗？", option.Value, removed), "继续", dangerous: true)) || viewModel.ActiveTab?.Id != tab.Id)
            {
                RestoreMetadataKeyCountSelection(tab);
                return;
            }

            num = removed;
        }

        if (!ApplyMetadataEditor())
        {
            RestoreMetadataKeyCountSelection(tab);
        }
    }
    catch (Exception ex)
    {
        if (!windowCloseTeardownStarted)
        {
            RestoreMetadataKeyCountSelection(tab);
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("谱面信息未应用：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
        }
    }
    finally
    {
        metadataKeyCountChangeInProgress = false;
    }
}

private void RestoreMetadataKeyCountSelection(DesktopChartTab tab)
{
    bool flag = suppressMetadataEditorControls;
    suppressMetadataEditorControls = true;
    try
    {
        metadataKeyCountBox.SelectedItem = WindowsInspectorParityPolicy.Wpf110MetadataKeyCountOptions.FirstOrDefault((WindowsMetadataKeyCountOption option) => option.Value == tab.Session.Document.KeyCount);
    }
    finally
    {
        suppressMetadataEditorControls = flag;
    }

    metadataEditorDirty = !ChartMetadataEditingService.DraftEquals(CaptureMetadataEditorDraft(tab), ChartMetadataEditingService.Capture(tab.Session.Document));
}

private Task RomanizeMetadataAsync(bool title)
{
    if (title ? titleRomanizationInProgress : artistRomanizationInProgress)
    {
        if (!title)
        {
            return activeArtistRomanizationTask;
        }

        return activeTitleRomanizationTask;
    }

    Task result = RomanizeMetadataCoreAsync(title);
    if (title)
    {
        activeTitleRomanizationTask = result;
    }
    else
    {
        activeArtistRomanizationTask = result;
    }

    return result;
}

private async Task RomanizeMetadataCoreAsync(bool title)
{
    DesktopChartTab tab = viewModel.ActiveTab;
    if (tab == null)
    {
        return;
    }

    if (tab.Session.IsReadOnly || !tab.Session.CanEditMetadata)
    {
        metadataStatusText.Text = (tab.Session.IsReadOnly ? "观察者不能修改谱面信息。" : "转罗马音需要谱面信息权限。");
        metadataStatusText.Foreground = Brushes.OrangeRed;
        return;
    }

    TextBox source = (title ? metadataTitleOriginalBox : metadataArtistOriginalBox);
    TextBox destination = (title ? metadataTitleBox : metadataArtistBox);
    Button button = (title ? metadataRomanizeTitleButton : metadataRomanizeArtistButton);
    string sourceSnapshot = source.Text ?? string.Empty;
    CancellationTokenSource cancellation = new CancellationTokenSource();
    if (title)
    {
        titleRomanizationCancellation?.Cancel();
        titleRomanizationCancellation?.Dispose();
        titleRomanizationCancellation = cancellation;
        titleRomanizationTabId = tab.Id;
        titleRomanizationInProgress = true;
    }
    else
    {
        artistRomanizationCancellation?.Cancel();
        artistRomanizationCancellation?.Dispose();
        artistRomanizationCancellation = cancellation;
        artistRomanizationTabId = tab.Id;
        artistRomanizationInProgress = true;
    }

    button.Content = "分析读音…";
    UpdateEditorAvailability();
    try
    {
        string text = await WindowsJapaneseRomanizer.RomanizeAsync(sourceSnapshot, cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        if (viewModel.ActiveTab?.Id != tab.Id || !string.Equals(source.Text ?? string.Empty, sourceSnapshot, StringComparison.Ordinal))
        {
            throw new OperationCanceledException("读音分析期间谱面或原文已变化。", cancellation.Token);
        }

        destination.Text = text;
        metadataEditorDirty = true;
        metadataStatusText.Text = (string.IsNullOrWhiteSpace(text) ? "原文为空，已清空对应的 ASCII 栏。" : AvaloniaLocalizationCatalog.Format("已生成罗马音：{0}", text));
        metadataStatusText.Foreground = MutedBrush;
        viewModel.SetStatus(metadataStatusText.Text);
    }
    catch (OperationCanceledException)
    {
        metadataStatusText.Text = "读音分析已取消，未覆盖当前输入。";
        metadataStatusText.Foreground = MutedBrush;
    }
    catch (Exception ex2)
    {
        Logger.TryGet(LogEventLevel.Warning, "Control")?.Log(this, "Japanese romanization failed: {Error}", ex2);
        metadataStatusText.Text = AvaloniaLocalizationCatalog.Format("无法分析这段日文的读音：{0}；可以直接在英文栏手动填写罗马音。", AvaloniaLocalizationCatalog.Text(ex2.Message));
        metadataStatusText.Foreground = Brushes.OrangeRed;
        viewModel.SetStatus(metadataStatusText.Text);
    }
    finally
    {
        if (title)
        {
            if (titleRomanizationCancellation == cancellation)
            {
                titleRomanizationCancellation = null;
                titleRomanizationTabId = null;
            }

            titleRomanizationInProgress = false;
            metadataRomanizeTitleButton.Content = "转罗马音";
        }
        else
        {
            if (artistRomanizationCancellation == cancellation)
            {
                artistRomanizationCancellation = null;
                artistRomanizationTabId = null;
            }

            artistRomanizationInProgress = false;
            metadataRomanizeArtistButton.Content = "转罗马音";
        }

        cancellation.Dispose();
        UpdateEditorAvailability();
    }
}

private void RefreshMetadataEditor(DesktopChartTab? tab, bool force = false)
{
    RefreshMetadataStats(tab);
    if (tab == null)
    {
        metadataEditorTabId = null;
        metadataEditorChangeSerial = -1L;
        metadataEditorDirty = false;
        metadataEditorBaseline = null;
        metadataStatusText.Text = "没有打开的谱面。";
        return;
    }

    bool flag = metadataEditorTabId != tab.Id;
    if (force || flag || metadataEditorChangeSerial != tab.Session.ChangeSerial)
    {
        ChartMetadataDraft chartMetadataDraft = ChartMetadataEditingService.Capture(tab.Session.Document);
        ChartMetadataDraftRebaseResult chartMetadataDraftRebaseResult = ((!force && !flag && metadataEditorDirty && metadataEditorBaseline != null) ? ChartMetadataEditingService.RebaseDraftWithDiagnostics(CaptureMetadataEditorDraft(tab), metadataEditorBaseline, chartMetadataDraft) : null);
        ChartMetadataDraft draft = chartMetadataDraftRebaseResult?.Draft ?? chartMetadataDraft;
        PopulateMetadataEditorDraft(tab, draft);
        metadataEditorTabId = tab.Id;
        metadataEditorChangeSerial = tab.Session.ChangeSerial;
        metadataEditorBaseline = chartMetadataDraft;
        metadataEditorDirty = chartMetadataDraftRebaseResult?.HasLocalChanges ?? false;
        metadataStatusText.Foreground = MutedBrush;
        if ((object)chartMetadataDraftRebaseResult != null && chartMetadataDraftRebaseResult.UpdatedAuthoritativeFieldCount > 0 && chartMetadataDraftRebaseResult.HasLocalChanges)
        {
            metadataStatusText.Text = "远端谱面信息已同步；未应用的本地输入已保留";
        }
        else
        {
            TextBlock textBlock = metadataStatusText;
            string text;
            if (tab.Session.IsReadOnly)
            {
                text = (tab.IsLibraryReadOnly ? "只读谱面：可查看和复制，克隆后编辑。" : "观察者只读：谱面信息不可修改。");
            }
            else
            {
                text = ((!tab.Session.CanEditMetadata) ? "没有谱面信息权限；请向房主索要。" : "Metadata、KeyCount 与 Offset 以一个撤销步骤原子应用。");
            }

            textBlock.Text = text;
        }
    }
}

private void RefreshMetadataStats(DesktopChartTab? tab)
{
    WindowsMetadataStats windowsMetadataStats = ((tab == null) ? new WindowsMetadataStats(0, 0, 0) : WindowsInspectorParityPolicy.MeasureMetadataStats(tab.Session.Document));
    metadataStatsNotesText.Text = windowsMetadataStats.Notes.ToString(CultureInfo.InvariantCulture);
    metadataStatsHoldsText.Text = windowsMetadataStats.Holds.ToString(CultureInfo.InvariantCulture);
    metadataStatsTimingText.Text = windowsMetadataStats.Timing.ToString(CultureInfo.InvariantCulture);
}

private ChartMetadataDraft CaptureMetadataEditorDraft(DesktopChartTab tab)
{
    return new ChartMetadataDraft(metadataTitleBox.Text ?? string.Empty, metadataTitleOriginalBox.Text ?? string.Empty, metadataArtistBox.Text ?? string.Empty, metadataArtistOriginalBox.Text ?? string.Empty, metadataCreatorBox.Text ?? string.Empty, metadataVersionBox.Text ?? string.Empty, metadataPreviewTimeBox.Text ?? string.Empty, metadataBpmBox.Text ?? string.Empty, ReadTimingOffsetDraft(tab), (metadataKeyCountBox.SelectedItem is WindowsMetadataKeyCountOption windowsMetadataKeyCountOption) ? windowsMetadataKeyCountOption.Value : tab.Session.Document.KeyCount, metadataOverallDifficultyBox.Text ?? string.Empty, metadataHpDrainRateBox.Text ?? string.Empty, metadataCoverFileBox.Text ?? string.Empty, metadataBackgroundFileBox.Text ?? string.Empty);
}

private void PopulateMetadataEditorDraft(DesktopChartTab tab, ChartMetadataDraft draft)
{
    bool flag = suppressMetadataEditorControls;
    suppressMetadataEditorControls = true;
    try
    {
        metadataTitleBox.Text = draft.Title;
        metadataTitleOriginalBox.Text = draft.TitleOriginal;
        metadataArtistBox.Text = draft.Artist;
        metadataArtistOriginalBox.Text = draft.ArtistOriginal;
        metadataCreatorBox.Text = draft.Creator;
        metadataVersionBox.Text = draft.Version;
        metadataPreviewTimeBox.Text = draft.PreviewTime;
        metadataBpmBox.Text = (double.TryParse(draft.Bpm, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result.ToString("0.######", CultureInfo.InvariantCulture) : draft.Bpm);
        metadataOffsetBox.Text = (double.TryParse(draft.Offset, NumberStyles.Float, CultureInfo.InvariantCulture, out var result2) ? FormatTimingMilliseconds(OsuSourceEnvelope.HasSource(tab.Session.Document) ? (0.0 - result2) : result2) : draft.Offset);
        metadataKeyCountBox.SelectedItem = WindowsInspectorParityPolicy.Wpf110MetadataKeyCountOptions.FirstOrDefault((WindowsMetadataKeyCountOption option) => option.Value == draft.KeyCount);
        metadataOverallDifficultyBox.Text = draft.OverallDifficulty;
        metadataHpDrainRateBox.Text = draft.HpDrainRate;
        metadataAudioFileBox.Text = tab.Session.Document.Metadata.AudioFile;
        metadataCoverFileBox.Text = draft.CoverFile;
        metadataBackgroundFileBox.Text = draft.BackgroundFile;
    }
    finally
    {
        suppressMetadataEditorControls = flag;
    }
}

private bool ApplyMetadataEditor(bool enforceCommunityRules = false, bool includeTiming = true)
{
    lastMetadataApplyError = null;
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null)
    {
        return false;
    }

    if (activeTab.IsLibraryReadOnly)
    {
        RefreshMetadataEditor(activeTab, force: true);
        metadataEditorDirty = false;
        return true;
    }

    ChartMetadataDraft chartMetadataDraft = CaptureMetadataEditorDraft(activeTab);
    bool flag = !double.TryParse(chartMetadataDraft.OverallDifficulty, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || !double.IsFinite(result);
    if (!flag)
    {
        bool flag2 = ((result < 0.0 || result > 10.0) ? true : false);
        flag = flag2;
    }

    double result2 = default;
    bool flag3 = flag || !double.TryParse(chartMetadataDraft.HpDrainRate, NumberStyles.Float, CultureInfo.InvariantCulture, out result2) || !double.IsFinite(result2);
    if (!flag3)
    {
        bool flag2 = ((result2 < 0.0 || result2 > 10.0) ? true : false);
        flag3 = flag2;
    }

    if (flag3)
    {
        lastMetadataApplyError = "OD 和 HP 必须是 0–10 范围内的数字。";
        metadataStatusText.Text = lastMetadataApplyError;
        metadataStatusText.Foreground = Brushes.OrangeRed;
        viewModel.SetStatus(lastMetadataApplyError);
        return false;
    }

    if (!includeTiming)
    {
        ChartMetadataDraft chartMetadataDraft2 = ChartMetadataEditingService.Capture(activeTab.Session.Document);
        chartMetadataDraft = chartMetadataDraft with
        {
            Bpm = chartMetadataDraft2.Bpm,
            Offset = chartMetadataDraft2.Offset
        };
    }

    if (!(enforceCommunityRules ? ChartMetadataEditingService.TryParseDraft(chartMetadataDraft, out ChartMetadataEdit edit, out string error) : ChartMetadataEditingService.TryParseDraftForLocalPreservation(chartMetadataDraft, activeTab.Session.Document, out edit, out error)) || edit == null)
    {
        lastMetadataApplyError = error;
        metadataStatusText.Text = error;
        metadataStatusText.Foreground = Brushes.OrangeRed;
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("谱面信息未应用：{0}", error));
        return false;
    }

    try
    {
        ChartMetadataApplyResult chartMetadataApplyResult = ChartMetadataEditingService.Apply(activeTab.Session, edit, enforceCommunityRules);
        metadataStatusText.Text = chartMetadataApplyResult.Message;
        TextBlock textBlock = metadataStatusText;
        IBrush foreground;
        if (chartMetadataApplyResult.Status != ChartMetadataApplyStatus.Denied)
        {
            foreground = MutedBrush;
        }
        else
        {
            IBrush orangeRed = Brushes.OrangeRed;
            foreground = orangeRed;
        }

        textBlock.Foreground = foreground;
        viewModel.SetStatus(chartMetadataApplyResult.Message);
        if (chartMetadataApplyResult.Status == ChartMetadataApplyStatus.Denied)
        {
            WindowsRoomPermissionNoticePresentation windowsRoomPermissionNoticePresentation = WindowsRoomPermissionNoticePolicy.MetadataApplyDenied(chartMetadataApplyResult.PermissionCategory);
            if ((object)windowsRoomPermissionNoticePresentation != null)
            {
                ShowRoomPermissionNotice(windowsRoomPermissionNoticePresentation);
            }
        }

        if (chartMetadataApplyResult.Status == ChartMetadataApplyStatus.Denied)
        {
            return false;
        }

        if (chartMetadataApplyResult.Status != ChartMetadataApplyStatus.Applied)
        {
            metadataEditorDirty = false;
            metadataEditorBaseline = ChartMetadataEditingService.Capture(activeTab.Session.Document);
            return true;
        }

        metadataEditorDirty = false;
        RefreshMetadataEditor(activeTab, force: true);
        RequestChartBackground(activeTab, activeTab.Session.Document.Metadata.BackgroundFile);
        metadataStatusText.Text = chartMetadataApplyResult.Message;
        return true;
    }
    catch (Exception ex)when ((ex is InvalidOperationException || ex is ArgumentException) ? true : false)
    {
        lastMetadataApplyError = ex.Message;
        metadataStatusText.Text = AvaloniaLocalizationCatalog.Format("谱面信息未应用：{0}", AvaloniaLocalizationCatalog.Text(ex.Message));
        metadataStatusText.Foreground = Brushes.OrangeRed;
        viewModel.SetStatus(metadataStatusText.Text);
        return false;
    }
}

private bool TryApplyPendingMetadataEditor(DesktopChartTab tab, bool enforceCommunityRules)
{
    if (tab.IsLibraryReadOnly)
    {
        if (metadataEditorTabId == tab.Id)
        {
            metadataEditorDirty = false;
            metadataEditorBaseline = ChartMetadataEditingService.Capture(tab.Session.Document);
        }

        return true;
    }

    Guid? guid = metadataEditorTabId;
    Guid id = tab.Id;
    if (!guid.HasValue || guid.GetValueOrDefault() != id || !metadataEditorDirty)
    {
        return true;
    }

    if (viewModel.ActiveTab != tab)
    {
        return false;
    }

    return ApplyMetadataEditor(enforceCommunityRules);
}

private void SetMetadataPreviewToCurrentPosition()
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null)
    {
        return;
    }

    try
    {
        WindowsAudioPlaybackSnapshot playbackSnapshot = audioTransport.PlaybackSnapshot;
        int previewTime = ChartMetadataEditingService.ResolvePreviewTime((object)playbackSnapshot != null && playbackSnapshot.IsPlaying && audioTransport.ActiveTabId == activeTab.Id, playbackSnapshot?.Position.TotalMilliseconds ?? 0.0, activeTab.Session.Document.Timing, BeatPosition.FromDouble(timeline.CurrentBeat));
        ChartMetadataApplyResult chartMetadataApplyResult = ChartMetadataEditingService.SetPreviewTime(activeTab.Session, previewTime);
        if (chartMetadataApplyResult.Status != ChartMetadataApplyStatus.Denied)
        {
            ChartMetadataDraft chartMetadataDraft = ChartMetadataEditingService.Capture(activeTab.Session.Document);
            ChartMetadataDraftRebaseResult chartMetadataDraftRebaseResult = ChartMetadataEditingService.RebaseDraftWithDiagnostics(CaptureMetadataEditorDraft(activeTab)with { PreviewTime = previewTime.ToString(CultureInfo.InvariantCulture) }, metadataEditorBaseline ?? chartMetadataDraft, chartMetadataDraft);
            PopulateMetadataEditorDraft(activeTab, chartMetadataDraftRebaseResult.Draft);
            metadataEditorBaseline = chartMetadataDraft;
            metadataEditorDirty = chartMetadataDraftRebaseResult.HasLocalChanges;
        }

        metadataEditorTabId = activeTab.Id;
        metadataEditorChangeSerial = activeTab.Session.ChangeSerial;
        metadataStatusText.Text = chartMetadataApplyResult.Message;
        TextBlock textBlock = metadataStatusText;
        IBrush foreground;
        if (chartMetadataApplyResult.Status != ChartMetadataApplyStatus.Denied)
        {
            foreground = MutedBrush;
        }
        else
        {
            IBrush orangeRed = Brushes.OrangeRed;
            foreground = orangeRed;
        }

        textBlock.Foreground = foreground;
        viewModel.SetStatus(chartMetadataApplyResult.Message);
        if (chartMetadataApplyResult.Status == ChartMetadataApplyStatus.Denied)
        {
            ShowRoomPermissionNotice(WindowsRoomPermissionNoticePolicy.PreviewTimeDenied(activeTab.Session.IsReadOnly));
        }
    }
    catch (Exception ex)
    {
        metadataStatusText.Text = AvaloniaLocalizationCatalog.Format("无法读取当前播放位置：{0}", AvaloniaLocalizationCatalog.Text(ex.Message));
        metadataStatusText.Foreground = Brushes.OrangeRed;
        viewModel.SetStatus(metadataStatusText.Text);
    }
}

private Task ImportCurrentChartAudioAsync()
{
    if (!activeAudioImportTask.IsCompleted)
    {
        return activeAudioImportTask;
    }

    return activeAudioImportTask = ImportCurrentChartAudioCoreAsync();
}

private Task ImportChartAudioPathAsync(DesktopChartTab tab, string sourcePath)
{
    if (!activeAudioImportTask.IsCompleted)
    {
        viewModel.SetStatus("音频导入进行中，请稍后再试。");
        return activeAudioImportTask;
    }

    return activeAudioImportTask = ImportChartAudioPathCoreAsync(tab, sourcePath);
}

private Task ImportCurrentChartImageAsync(bool background)
{
    if (imageImportInProgress)
    {
        return activeImageImportTask;
    }

    return activeImageImportTask = ImportCurrentChartImageCoreAsync(background);
}

private async Task ImportCurrentChartImageCoreAsync(bool background)
{
    DesktopChartTab tab = viewModel.ActiveTab;
    if (tab == null)
    {
        return;
    }

    if (tab.Session.IsReadOnly || !tab.Session.CanEditMetadata)
    {
        metadataStatusText.Text = (tab.Session.IsReadOnly ? "观察者不能修改谱面图片。" : "选择谱面图片需要谱面信息权限。");
        metadataStatusText.Foreground = Brushes.OrangeRed;
        return;
    }

    imageImportCancellation?.Cancel();
    imageImportCancellation?.Dispose();
    CancellationTokenSource cancellation = (imageImportCancellation = new CancellationTokenSource());
    imageImportTabId = tab.Id;
    imageImportInProgress = true;
    UpdateEditorAvailability();
    ChartAssetImportResult imported = null;
    try
    {
        string sourcePath = await PickLocalFileAsync(new FilePickerOpenOptions { Title = "选择图片", AllowMultiple = false, FileTypeFilter = new FilePickerFileType[]
{
    new FilePickerFileType("图片文件")
    {
        Patterns = new string[5] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" },
        MimeTypes = new string[]
{
    "image/*"
}
    }
} }, PickedFileRetention.Transient, cancellation.Token);
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            imported = await Task.Run(() => ChartAssetImportService.ImportImage(tab.WorkingPath, System.IO.Path.GetFullPath(sourcePath), tab.SourcePath), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (viewModel.ActiveTab?.Id != tab.Id)
            {
                throw new OperationCanceledException("导入期间已切换谱面。", cancellation.Token);
            }

            if (background)
            {
                metadataBackgroundFileBox.Text = imported.FileName;
                RequestChartBackground(tab, imported.FileName);
            }
            else
            {
                metadataCoverFileBox.Text = imported.FileName;
            }

            metadataEditorTabId = tab.Id;
            metadataEditorChangeSerial = tab.Session.ChangeSerial;
            metadataEditorDirty = true;
            metadataStatusText.Text = AvaloniaLocalizationCatalog.Format("{0}已导入：{1}；点击“应用谱面信息与 Offset”后写入谱面。", background ? AvaloniaLocalizationCatalog.Text("背景图片") : AvaloniaLocalizationCatalog.Text("歌曲封面"), imported.FileName);
            metadataStatusText.Foreground = MutedBrush;
            viewModel.SetStatus(metadataStatusText.Text);
        }
    }
    catch (OperationCanceledException)
    {
        if (imported?.Created ?? false)
        {
            TryDeleteImportedAsset(imported.DestinationPath);
        }

        metadataStatusText.Text = "图片导入已取消，未修改当前谱面。";
        metadataStatusText.Foreground = MutedBrush;
    }
    catch (Exception ex2)
    {
        if (imported?.Created ?? false)
        {
            TryDeleteImportedAsset(imported.DestinationPath);
        }

        metadataStatusText.Text = AvaloniaLocalizationCatalog.Format("无法导入图片：{0}", AvaloniaLocalizationCatalog.Text(ex2.Message));
        metadataStatusText.Foreground = Brushes.OrangeRed;
        viewModel.SetStatus(metadataStatusText.Text);
        WindowsNativeMessageBox.Show(this, WindowsLibraryLauncherParityPolicy.CreateOperationError("无法复制图片到谱面目录", ex2));
    }
    finally
    {
        if (imageImportCancellation == cancellation)
        {
            imageImportCancellation = null;
            imageImportTabId = null;
        }

        cancellation.Dispose();
        imageImportInProgress = false;
        UpdateEditorAvailability();
    }
}

private void UpdateChartBackground(DesktopChartTab? tab)
{
    string reference;
    if (tab == null)
    {
        reference = null;
    }
    else
    {
        reference = ((metadataEditorDirty && metadataEditorTabId == tab.Id) ? metadataBackgroundFileBox.Text : tab.Session.Document.Metadata.BackgroundFile);
    }

    RequestChartBackground(tab, reference);
}

private void InvalidateChartBackgroundCache()
{
    backgroundImageTabId = null;
    backgroundImagePath = null;
}

private void RequestChartBackground(DesktopChartTab? tab, string? reference)
{
    string text = ((tab == null) ? null : ChartAssetImportService.ResolveAssetPath(tab.WorkingPath, tab.SourcePath, reference));
    Guid? guid = backgroundImageTabId;
    Guid? guid2 = tab?.Id;
    if (guid.HasValue != guid2.HasValue || (guid.HasValue && !(guid.GetValueOrDefault() == guid2.GetValueOrDefault())) || !string.Equals(backgroundImagePath, text, StringComparison.OrdinalIgnoreCase))
    {
        backgroundImageLoadCancellation?.Cancel();
        backgroundImageLoadCancellation = null;
        long generation = ++backgroundImageLoadGeneration;
        backgroundImageTabId = tab?.Id;
        backgroundImagePath = text;
        timeline.SetChartBackgroundImage(null);
        if (tab != null && text != null)
        {
            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
            CancellationToken token = cancellationTokenSource.Token;
            backgroundImageLoadCancellation = cancellationTokenSource;
            LoadChartBackgroundAsync(text, tab.Id, generation, cancellationTokenSource, token);
        }
    }
}

private async Task LoadChartBackgroundAsync(string path, Guid tabId, long generation, CancellationTokenSource cancellation, CancellationToken cancellationToken)
{
    Bitmap bitmap = null;
    try
    {
        bitmap = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
            Bitmap result = Bitmap.DecodeToWidth(stream, 2560);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }, cancellationToken);
        if (generation == backgroundImageLoadGeneration && !cancellationToken.IsCancellationRequested)
        {
            DesktopChartTab? activeTab = viewModel.ActiveTab;
            if (activeTab != null && !(activeTab.Id != tabId) && string.Equals(backgroundImagePath, path, StringComparison.OrdinalIgnoreCase))
            {
                timeline.SetChartBackgroundImage(bitmap);
                bitmap = null;
                metadataStatusText.Text = AvaloniaLocalizationCatalog.Format("背景预览已加载：{0}。", System.IO.Path.GetFileName(path));
                metadataStatusText.Foreground = MutedBrush;
                viewModel.SetStatus(metadataStatusText.Text);
                return;
            }
        }

        bitmap.Dispose();
    }
    catch (OperationCanceledException)
    {
        bitmap?.Dispose();
    }
    catch
    {
        bitmap?.Dispose();
        if (generation == backgroundImageLoadGeneration)
        {
            DesktopChartTab? activeTab2 = viewModel.ActiveTab;
            if (activeTab2 != null && activeTab2.Id == tabId)
            {
                timeline.SetChartBackgroundImage(null);
                metadataStatusText.Text = AvaloniaLocalizationCatalog.Format("背景图片无法解码：{0}。", System.IO.Path.GetFileName(path));
                metadataStatusText.Foreground = Brushes.OrangeRed;
                viewModel.SetStatus(metadataStatusText.Text);
            }
        }
    }
    finally
    {
        if (backgroundImageLoadCancellation == cancellation)
        {
            backgroundImageLoadCancellation = null;
        }

        cancellation.Dispose();
    }
}

private async Task ImportCurrentChartAudioCoreAsync()
{
    DesktopChartTab tab = viewModel.ActiveTab;
    if (tab == null)
    {
        return;
    }

    if (tab.Session.IsReadOnly || !tab.Session.CanEditMetadata)
    {
        SetMetadataAudioStatus(tab.Session.IsReadOnly ? "观察者不能修改谱面音频。" : "选择谱面音频需要谱面信息权限。", error: true);
        return;
    }

    string text;
    try
    {
        text = await PickLocalFileAsync(new FilePickerOpenOptions { Title = "选择音乐", AllowMultiple = false, FileTypeFilter = new FilePickerFileType[]
{
    new FilePickerFileType("音频文件")
    {
        Patterns = new string[6] { "*.mp3", "*.ogg", "*.wav", "*.flac", "*.m4a", "*.aac" },
        MimeTypes = new string[]
{
    "audio/*"
}
    }
} }, PickedFileRetention.Transient);
    }
    catch (Exception ex)
    {
        SetMetadataAudioStatus(AvaloniaLocalizationCatalog.Format("无法导入音频：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)), error: true);
        return;
    }

    if (!string.IsNullOrWhiteSpace(text))
    {
        await ImportChartAudioPathCoreAsync(tab, System.IO.Path.GetFullPath(text));
    }
}

private async Task<WindowsChartAudioImportStatus> ImportChartAudioPathCoreAsync(DesktopChartTab tab, string sourcePath)
{
    if (tab.Session.IsReadOnly || !tab.Session.CanEditMetadata)
    {
        SetMetadataAudioStatus(tab.Session.IsReadOnly ? "观察者不能修改谱面音频。" : "选择谱面音频需要谱面信息权限。", error: true);
        return WindowsChartAudioImportStatus.Denied;
    }

    AudioImportMode mode = AudioImportMode.KeepOriginal;
    if (!System.IO.Path.GetExtension(sourcePath).Equals(".ogg", StringComparison.OrdinalIgnoreCase))
    {
        mode = await new AudioImportModeDialog(System.IO.Path.GetFileName(sourcePath), displayScaleSettings.UiScale, displayScaleSettings.FontScale).ShowDialog<AudioImportMode>(this);
        if (mode == AudioImportMode.Cancel)
        {
            return WindowsChartAudioImportStatus.Canceled;
        }
    }

    audioImportCancellation?.Cancel();
    audioImportCancellation?.Dispose();
    CancellationTokenSource cancellation = (audioImportCancellation = new CancellationTokenSource());
    audioImportInProgress = true;
    UpdateEditorAvailability();
    if (evidenceAudioPermissionRevokeDelayMilliseconds > 0)
    {
        DispatcherTimer.RunOnce(() =>
        {
            if (!(viewModel.ActiveTab?.Id != tab.Id))
            {
                tab.Session.CanEditMetadata = false;
                UpdateEditorAvailability();
            }
        }, TimeSpan.FromMilliseconds(evidenceAudioPermissionRevokeDelayMilliseconds));
    }

    try
    {
        if (audioAnalysisInProgress)
        {
            audioAnalysisCancellation?.Cancel();
            try
            {
                await activeAudioAnalysisTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        IProgress<double> progress = null;
        if (mode == AudioImportMode.ConvertToOgg)
        {
            progress = new Progress<double>((double value) =>
            {
                SetMetadataAudioStatus(AvaloniaLocalizationCatalog.Format("正在转换 OGG… {0:P0}", value));
            });
            SetMetadataAudioStatus("正在转换 OGG…");
        }
        else
        {
            SetMetadataAudioStatus("正在复制音频到谱面编辑目录…");
        }

        WindowsChartAudioImportResult windowsChartAudioImportResult = await chartAudioImportWorkflow.RunAsync(tab.Session, tab.WorkingPath, tab.SourcePath, sourcePath, mode == AudioImportMode.ConvertToOgg, () => viewModel.ActiveTab?.Id == tab.Id, progress, cancellation.Token, TimeSpan.FromMilliseconds(evidenceAudioPreCommitDelayMilliseconds));
        if (windowsChartAudioImportResult.Status == WindowsChartAudioImportStatus.Canceled)
        {
            SetMetadataAudioStatus("音频导入已取消，未修改当前谱面。", error: true);
            return WindowsChartAudioImportStatus.Canceled;
        }

        if (windowsChartAudioImportResult.Status == WindowsChartAudioImportStatus.Failed)
        {
            SetMetadataAudioStatus(AvaloniaLocalizationCatalog.Format("无法导入音频：{0}", windowsChartAudioImportResult.Error?.Message ?? AvaloniaLocalizationCatalog.Text("未知错误")), error: true);
            return WindowsChartAudioImportStatus.Failed;
        }

        if (windowsChartAudioImportResult.Status == WindowsChartAudioImportStatus.Denied)
        {
            SetMetadataAudioStatus(windowsChartAudioImportResult.MetadataApply?.Message ?? "当前权限不允许修改谱面音频。", error: true);
            return WindowsChartAudioImportStatus.Denied;
        }

        ChartAssetImportResult chartAssetImportResult = windowsChartAudioImportResult.Asset ?? throw new InvalidOperationException("音频导入没有返回资源。");
        ChartMetadataApplyResult chartMetadataApplyResult = windowsChartAudioImportResult.MetadataApply ?? throw new InvalidOperationException("音频导入没有返回谱面修改结果。");
        metadataAudioFileBox.Text = chartAssetImportResult.FileName;
        metadataEditorTabId = tab.Id;
        metadataEditorChangeSerial = tab.Session.ChangeSerial;
        SetMetadataAudioStatus((mode == AudioImportMode.ConvertToOgg) ? AvaloniaLocalizationCatalog.Format("音频已转换并导入：{0}", chartAssetImportResult.FileName) : AvaloniaLocalizationCatalog.Format("音频已导入：{0}", chartAssetImportResult.FileName));
        viewModel.SetStatus(chartMetadataApplyResult.Message);
        UpdateUi();
        return WindowsChartAudioImportStatus.Imported;
    }
    catch (Exception ex2)
    {
        SetMetadataAudioStatus(AvaloniaLocalizationCatalog.Format("无法导入音频：{0}", AvaloniaLocalizationCatalog.Text(ex2.Message)), error: true);
        return WindowsChartAudioImportStatus.Failed;
    }
    finally
    {
        if (audioImportCancellation == cancellation)
        {
            audioImportCancellation = null;
            audioImportInProgress = false;
            UpdateEditorAvailability();
        }

        cancellation.Dispose();
    }
}

private Task StartSingleTempoAnalysisAsync()
{
    if (audioAnalysisInProgress)
    {
        if (activeTempoAnalysisKind == TempoAnalysisKind.Single)
        {
            RequestTempoAnalysisCancellation();
        }

        return activeAudioAnalysisTask;
    }

    return activeAudioAnalysisTask = RunSingleTempoAnalysisAsync();
}

private async Task RunSingleTempoAnalysisAsync()
{
    DesktopChartTab tab = viewModel.ActiveTab;
    if (!TryBeginTempoAnalysis(tab, TempoAnalysisKind.Single, out string audioPath, out CancellationTokenSource cancellation))
    {
        return;
    }

    metadataAnalyzeSingleTempoButton.Content = "取消 BPM 分析";
    SetMetadataAudioStatus("正在读取波形并分析节拍，最长分析前 4 分钟；可点击按钮取消。");
    try
    {
        BeatAnalysisResult result = await WindowsBeatAnalyzer.AnalyzeAsync(audioPath, cancellation.Token);
        if (!IsCurrentAudioAnalysisContext(tab))
        {
            SetMetadataAudioStatus("分析期间已切换谱面，本次结果已取消且不会写入当前谱面。", error: true);
            return;
        }

        SetMetadataAudioStatus(AvaloniaLocalizationCatalog.Format("BPM {0:0.###} · Offset {1:0.###} ms · 稳定度 {2:P0} · 置信度 {3:P0}", result.Bpm, result.OffsetMilliseconds, 1.0 - result.Instability, result.Confidence));
        long serialAtPreview = tab.Session.ChangeSerial;
        if (!(await new SingleTempoResultDialog(result, tab.Session.CanEditMetadata, displayScaleSettings.UiScale, displayScaleSettings.FontScale).ShowDialog<bool>(this)))
        {
            return;
        }

        if (!IsCurrentAudioAnalysisContext(tab))
        {
            SetMetadataAudioStatus("确认前已切换谱面，检测结果未写入。", error: true);
            return;
        }

        TempoDetectionApplyResult tempoDetectionApplyResult = TempoDetectionEditingService.ApplySingle(tab.Session, result, serialAtPreview);
        string message = tempoDetectionApplyResult.Message;
        TempoDetectionApplyStatus status = tempoDetectionApplyResult.Status;
        bool error = (uint)(status - 2) <= 1u;
        SetMetadataAudioStatus(message, error);
        viewModel.SetStatus(tempoDetectionApplyResult.Message);
        if (tempoDetectionApplyResult.Status == TempoDetectionApplyStatus.Denied)
        {
            WindowsRoomPermissionNoticePresentation windowsRoomPermissionNoticePresentation = WindowsRoomPermissionNoticePolicy.TempoAnalysisApplyDenied(tempoDetectionApplyResult.PermissionCategory, variableTempo: false);
            if ((object)windowsRoomPermissionNoticePresentation != null)
            {
                ShowRoomPermissionNotice(windowsRoomPermissionNoticePresentation);
            }
        }

        if (tempoDetectionApplyResult.Status == TempoDetectionApplyStatus.Applied)
        {
            UpdateTempoEditorFields(tab, result.Bpm, result.OffsetMilliseconds, tempoDetectionApplyResult.MetadataBpmUpdated);
        }
    }
    catch (OperationCanceledException)
    {
        SetMetadataAudioStatus("BPM 分析已取消，未写入谱面。", error: true);
    }
    catch (Exception ex2)
    {
        SetMetadataAudioStatus(AvaloniaLocalizationCatalog.Format("BPM 检测失败：{0}", AvaloniaLocalizationCatalog.Text(ex2.Message)), error: true);
    }
    finally
    {
        metadataAnalyzeSingleTempoButton.Content = "检测 BPM 与 Offset";
        CompleteTempoAnalysis(cancellation);
    }
}

private Task StartVariableTempoAnalysisAsync()
{
    if (audioAnalysisInProgress)
    {
        if (activeTempoAnalysisKind == TempoAnalysisKind.Variable)
        {
            RequestTempoAnalysisCancellation();
        }

        return activeAudioAnalysisTask;
    }

    return activeAudioAnalysisTask = RunVariableTempoAnalysisAsync();
}

private Task StartManualTempoMeasurementAsync()
{
    if (audioAnalysisInProgress)
    {
        return activeAudioAnalysisTask;
    }

    return activeAudioAnalysisTask = RunManualTempoMeasurementAsync();
}

private async Task RunManualTempoMeasurementAsync()
{
    DesktopChartTab tab = viewModel.ActiveTab;
    if (!TryBeginTempoAnalysis(tab, TempoAnalysisKind.Manual, out string audioPath, out CancellationTokenSource cancellation))
    {
        return;
    }

    metadataAnalyzeManualTempoButton.Content = "正在手动测量…";
    SetMetadataAudioStatus("手动测量会播放当前歌曲；请在弹窗内设置按键并跟随整拍连续打拍。");
    double originalRate = audioTransport.PlaybackRate;
    bool wasPlaying = (audioTransport.PlaybackSnapshot?.IsPlaying ?? false) && audioTransport.ActiveTabId == tab.Id;
    try
    {
        _ = 2;
        try
        {
            if (!(await audioTransport.EnsureAudioReadyAsync(tab)))
            {
                throw new InvalidOperationException(audioTransport.Status);
            }

            if (!IsCurrentAudioAnalysisContext(tab))
            {
                throw new OperationCanceledException();
            }

            await audioTransport.SetPlaybackRateAsync(1.0);
            if (audioTransport.LastError != null)
            {
                throw new InvalidOperationException(audioTransport.LastError.Message, audioTransport.LastError);
            }

            long serialAtPreview = tab.Session.ChangeSerial;
            BeatAnalysisResult? beatAnalysisResult = await new ManualTempoMeasurementDialog(ToggleAudioPlaybackAsync, () => audioTransport.ActiveTabId == tab.Id && (audioTransport.PlaybackSnapshot?.IsPlaying ?? false), () => (!(audioTransport.ActiveTabId == tab.Id)) ? ((double? )null) : audioTransport.PlaybackSnapshot?.Position.TotalMilliseconds, displayScaleSettings.UiScale, displayScaleSettings.FontScale).ShowDialog<BeatAnalysisResult?>(this);
            if (!beatAnalysisResult.HasValue)
            {
                return;
            }

            BeatAnalysisResult valueOrDefault = beatAnalysisResult.GetValueOrDefault();
            if (!IsCurrentAudioAnalysisContext(tab))
            {
                SetMetadataAudioStatus("确认前已切换谱面，手动测量结果未写入。", error: true);
                goto end_IL_0128;
            }

            TempoDetectionApplyResult tempoDetectionApplyResult = TempoDetectionEditingService.ApplySingle(tab.Session, valueOrDefault, serialAtPreview);
            audioPath = tempoDetectionApplyResult.Message;
            TempoDetectionApplyStatus status = tempoDetectionApplyResult.Status;
            bool error = (uint)(status - 2) <= 1u;
            SetMetadataAudioStatus(audioPath, error);
            viewModel.SetStatus(tempoDetectionApplyResult.Message);
            if (tempoDetectionApplyResult.Status == TempoDetectionApplyStatus.Denied)
            {
                WindowsRoomPermissionNoticePresentation windowsRoomPermissionNoticePresentation = WindowsRoomPermissionNoticePolicy.TempoAnalysisApplyDenied(tempoDetectionApplyResult.PermissionCategory, variableTempo: false);
                if ((object)windowsRoomPermissionNoticePresentation != null)
                {
                    ShowRoomPermissionNotice(windowsRoomPermissionNoticePresentation);
                }
            }

            if (tempoDetectionApplyResult.Status == TempoDetectionApplyStatus.Applied)
            {
                UpdateTempoEditorFields(tab, valueOrDefault.Bpm, valueOrDefault.OffsetMilliseconds, tempoDetectionApplyResult.MetadataBpmUpdated);
            }

            return;
            end_IL_0128:
                ;
        }
        catch (OperationCanceledException)
        {
            SetMetadataAudioStatus("手动测量已取消，未写入谱面。", error: true);
            goto end_IL_0111;
        }
        catch (Exception ex2)
        {
            SetMetadataAudioStatus(AvaloniaLocalizationCatalog.Format("手动测量失败：{0}", AvaloniaLocalizationCatalog.Text(ex2.Message)), error: true);
            goto end_IL_0111;
        }

        end_IL_0111:
            ;
    }
    finally
    {
        if (audioTransport.ActiveTabId == tab.Id)
        {
            if ((audioTransport.PlaybackSnapshot?.IsPlaying ?? false) != wasPlaying)
            {
                await audioTransport.TogglePlayPauseAsync();
            }

            await audioTransport.SetPlaybackRateAsync(originalRate);
        }

        metadataAnalyzeManualTempoButton.Content = "手动测量 BPM 与 Offset";
        CompleteTempoAnalysis(cancellation);
    }
}

private async Task RunVariableTempoAnalysisAsync()
{
    DesktopChartTab tab = viewModel.ActiveTab;
    if (!TryBeginTempoAnalysis(tab, TempoAnalysisKind.Variable, out string audioPath, out CancellationTokenSource cancellation))
    {
        return;
    }

    metadataAnalyzeVariableTempoButton.Content = "取消多 BPM 分析";
    SetMetadataAudioStatus("正在分窗估计局部节拍并合并稳定变速点，最长分析前 4 分钟；可点击按钮取消。");
    try
    {
        TempoTimelineAnalysisResult result = await WindowsBeatAnalyzer.AnalyzeTimelineAsync(audioPath, cancellation.Token);
        if (!IsCurrentAudioAnalysisContext(tab))
        {
            SetMetadataAudioStatus("分析期间已切换谱面，本次结果已取消且不会写入当前谱面。", error: true);
            return;
        }

        TempoTimelinePlan plan = TempoDetectionEditingService.CreateTimelinePlan(tab.Session.Document, result.Timeline);
        double num = result.Timeline.Segments.Min((TempoSegmentEstimate segment) => segment.Confidence);
        SetMetadataAudioStatus(AvaloniaLocalizationCatalog.Format("预测到 {0} 个 BPM 段 · 首段 Offset {1:0.###} ms · 最低置信度 {2:P0}", plan.Candidates.Count, result.Timeline.InitialOffsetMilliseconds, num));
        long serialAtPreview = tab.Session.ChangeSerial;
        VariableTempoDialogChoice variableTempoDialogChoice = await new VariableTempoResultDialog(result, plan, displayScaleSettings.UiScale, displayScaleSettings.FontScale).ShowDialog<VariableTempoDialogChoice>(this);
        if (variableTempoDialogChoice == VariableTempoDialogChoice.Cancel)
        {
            return;
        }

        if (!IsCurrentAudioAnalysisContext(tab))
        {
            SetMetadataAudioStatus("确认前已切换谱面，预测结果未写入。", error: true);
            return;
        }

        bool flag = variableTempoDialogChoice == VariableTempoDialogChoice.ApplyWithScrollCompensation;
        if (flag && (!TryParseMetadataNumber(metadataBpmBox.Text, out var value) || !value.Equals(plan.ReferenceBpm)))
        {
            SetMetadataAudioStatus(AvaloniaLocalizationCatalog.Format("谱面 BPM 输入框中有尚未应用的内容。流速补正只使用已保存的基准 BPM {0:R}；请先应用谱面信息后重新预测。", plan.ReferenceBpm), error: true);
            return;
        }

        TempoDetectionApplyResult tempoDetectionApplyResult = TempoDetectionEditingService.ApplyTimeline(tab.Session, plan, result.Timeline.InitialOffsetMilliseconds, flag, serialAtPreview);
        string message = tempoDetectionApplyResult.Message;
        TempoDetectionApplyStatus status = tempoDetectionApplyResult.Status;
        bool error = (uint)(status - 2) <= 1u;
        SetMetadataAudioStatus(message, error);
        viewModel.SetStatus(tempoDetectionApplyResult.Message);
        if (tempoDetectionApplyResult.Status == TempoDetectionApplyStatus.Denied)
        {
            WindowsRoomPermissionNoticePresentation windowsRoomPermissionNoticePresentation = WindowsRoomPermissionNoticePolicy.TempoAnalysisApplyDenied(tempoDetectionApplyResult.PermissionCategory, variableTempo: true);
            if ((object)windowsRoomPermissionNoticePresentation != null)
            {
                ShowRoomPermissionNotice(windowsRoomPermissionNoticePresentation);
            }
        }

        if (tempoDetectionApplyResult.Status == TempoDetectionApplyStatus.Applied)
        {
            UpdateTempoEditorFields(tab, plan.ReferenceBpm, result.Timeline.InitialOffsetMilliseconds, updateBpm: false);
        }
    }
    catch (OperationCanceledException)
    {
        SetMetadataAudioStatus("多 BPM 分析已取消，未写入谱面。", error: true);
    }
    catch (Exception ex2)
    {
        SetMetadataAudioStatus(AvaloniaLocalizationCatalog.Format("多 BPM 预测失败：{0}", AvaloniaLocalizationCatalog.Text(ex2.Message)), error: true);
    }
    finally
    {
        metadataAnalyzeVariableTempoButton.Content = "预测多个 BPM";
        CompleteTempoAnalysis(cancellation);
    }
}

private bool TryBeginTempoAnalysis(DesktopChartTab? tab, TempoAnalysisKind kind, out string audioPath, out CancellationTokenSource cancellation)
{
    audioPath = string.Empty;
    cancellation = new CancellationTokenSource();
    if (tab == null)
    {
        cancellation.Dispose();
        return false;
    }

    if (tab.Session.IsReadOnly || !tab.Session.CanEditTiming)
    {
        cancellation.Dispose();
        SetMetadataAudioStatus(tab.Session.IsReadOnly ? "观察者不能写入 BPM 或 Delay。" : "BPM 与 Offset 属于 Timing，需要 Effect 权限。", error: true);
        ShowRoomPermissionNotice(WindowsRoomPermissionNoticePolicy.TempoAnalysisStartDenied());
        return false;
    }

    audioPath = WindowsAudioTransportViewModel.ResolveDocumentAudioPath(tab) ?? string.Empty;
    if (audioPath.Length == 0)
    {
        cancellation.Dispose();
        SetMetadataAudioStatus("请先选择一个存在的音频文件。", error: true);
        return false;
    }

    audioAnalysisCancellation?.Cancel();
    cancellation = new CancellationTokenSource();
    audioAnalysisCancellation = cancellation;
    audioAnalysisTabId = tab.Id;
    activeTempoAnalysisKind = kind;
    audioAnalysisInProgress = true;
    UpdateEditorAvailability();
    return true;
}

private void RequestTempoAnalysisCancellation()
{
    if (audioAnalysisInProgress && audioAnalysisCancellation != null)
    {
        SetMetadataAudioStatus("正在取消 BPM 分析…");
        audioAnalysisCancellation.Cancel();
    }
}

private void CompleteTempoAnalysis(CancellationTokenSource cancellation)
{
    if (audioAnalysisCancellation == cancellation)
    {
        audioAnalysisCancellation = null;
        audioAnalysisTabId = null;
    }

    cancellation.Dispose();
    audioAnalysisInProgress = false;
    activeTempoAnalysisKind = TempoAnalysisKind.None;
    UpdateEditorAvailability();
}

private bool IsCurrentAudioAnalysisContext(DesktopChartTab? tab)
{
    if (tab != null && viewModel.ActiveTab?.Id == tab.Id)
    {
        return audioAnalysisTabId == tab.Id;
    }

    return false;
}

private void UpdateTempoEditorFields(DesktopChartTab tab, double bpm, double offset, bool updateBpm)
{
    bool flag = suppressMetadataEditorControls;
    suppressMetadataEditorControls = true;
    try
    {
        metadataOffsetBox.Text = FormatTimingMilliseconds(OsuSourceEnvelope.HasSource(tab.Session.Document) ? (0.0 - offset) : offset);
        if (updateBpm)
        {
            metadataBpmBox.Text = bpm.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
    finally
    {
        suppressMetadataEditorControls = flag;
    }

    metadataEditorTabId = tab.Id;
    metadataEditorChangeSerial = tab.Session.ChangeSerial;
}

private void SetMetadataAudioStatus(string message, bool error = false)
{
    metadataAudioAnalysisText.Text = message;
    TextBlock textBlock = metadataAudioAnalysisText;
    IBrush foreground;
    if (!error)
    {
        foreground = MutedBrush;
    }
    else
    {
        IBrush indianRed = Brushes.IndianRed;
        foreground = indianRed;
    }

    textBlock.Foreground = foreground;
    viewModel.SetStatus(message);
}

private static bool TryParseMetadataNumber(string? text, out double value)
{
    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
    {
        return double.IsFinite(value);
    }

    return false;
}

private static void TryDeleteImportedAsset(string path)
{
    try
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
    catch
    {
    }
}

private void UpdateNoteInspector(DesktopChartTab? tab)
{
    if (tab == null)
    {
        noteSelectionHint.Text = "选择一个 Note 进行编辑";
        ClearNotePropertyFields();
        return;
    }

    WindowsNoteInspectorState windowsNoteInspectorState = WindowsNotePropertyCommand.ReadInspectorState(tab.Session, tab.HiddenGroups);
    noteSelectionHint.Text = windowsNoteInspectorState.Hint;
    if (windowsNoteInspectorState.SelectedCount == 0)
    {
        ClearNotePropertyFields();
        return;
    }

    WindowsNotePropertyFields fields = windowsNoteInspectorState.Fields;
    if ((object)fields == null)
    {
        noteInspectorNoteId = null;
        noteInspectorFields = null;
        return;
    }

    WindowsNotePropertyFields windowsNotePropertyFields = ((noteInspectorNoteId == windowsNoteInspectorState.NoteId) ? noteInspectorFields : null);
    RefillNoteField(noteBeatBox, fields.Beat, windowsNotePropertyFields?.Beat);
    RefillNoteField(noteEndBeatBox, fields.EndBeat, windowsNotePropertyFields?.EndBeat);
    RefillNoteField(noteLaneBox, fields.Lane, windowsNotePropertyFields?.Lane);
    RefillNoteField(noteVolumeBox, fields.Volume, windowsNotePropertyFields?.Volume);
    RefillNoteField(noteSoundBox, fields.Sound, windowsNotePropertyFields?.Sound);
    RefillNoteField(noteGroupBox, fields.Group, windowsNotePropertyFields?.Group);
    noteInspectorNoteId = windowsNoteInspectorState.NoteId;
    noteInspectorFields = fields;
}

private static void RefillNoteField(TextBox box, string value, string? previouslyFilledValue)
{
    if (previouslyFilledValue == null || !string.Equals(value, previouslyFilledValue, StringComparison.Ordinal))
    {
        box.Text = value;
    }
}

private void ResetNoteInspectorFill()
{
    noteInspectorNoteId = null;
    noteInspectorFields = null;
}

private void ClearNotePropertyFields()
{
    ResetNoteInspectorFill();
    noteBeatBox.Text = string.Empty;
    noteEndBeatBox.Text = string.Empty;
    noteLaneBox.Text = string.Empty;
    noteVolumeBox.Text = string.Empty;
    noteSoundBox.Text = string.Empty;
    noteGroupBox.Text = string.Empty;
}

private void RefreshGroupPanel()
{
    DesktopChartTab tab = viewModel.ActiveTab;
    groupListPanel.Children.Clear();
    if (tab == null)
    {
        currentPlacementGroupText.Text = "未分组（0）";
        groupManagerHint.Text = "选择 Note 后可批量设置分组";
        return;
    }

    WindowsGroupManagerSnapshot windowsGroupManagerSnapshot = WindowsGroupManagerCommand.ReadSnapshot(tab);
    SyncKeySoundHiddenGroups(tab);
    timeline.PlacementGroup = 0;
    tab.HiddenGroups.Clear();
    timeline.SetHiddenGroups(tab.HiddenGroups);
    currentPlacementGroupText.Text = ((windowsGroupManagerSnapshot.ActivePlacementGroup == 0) ? "未分组（0）" : $"Group {windowsGroupManagerSnapshot.ActivePlacementGroup}");
    currentPlacementGroupText.Foreground = ((windowsGroupManagerSnapshot.ActivePlacementGroup == 0) ? MutedBrush : TimelineSurface.GetGroupBrush(windowsGroupManagerSnapshot.ActivePlacementGroup));
    groupManagerHint.Text = ((windowsGroupManagerSnapshot.VisibleEditableSelectionCount == 0) ? "选择 Note 后可批量设置分组" : AvaloniaLocalizationCatalog.Format("已选择 {0} 个可见 Note，可批量分配", windowsGroupManagerSnapshot.VisibleEditableSelectionCount));
    int groupDynamicFocusStartIndex = WindowsInspectorParityPolicy.GroupDynamicFocusStartIndex;
    foreach (WindowsGroupManagerRow row in windowsGroupManagerSnapshot.Rows)
    {
        IBrush brush;
        if (row.Group != 0)
        {
            brush = TimelineSurface.GetGroupBrush(row.Group);
        }
        else
        {
            IBrush brush2 = new SolidColorBrush(Color.Parse("#FF70787C"));
            brush = brush2;
        }

        IBrush foreground = brush;
        CheckBox visible = new CheckBox
        {
            Content = "显示",
            IsChecked = row.IsVisible,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0.0, 0.0, 7.0, 0.0)
        };
        AutomationProperties.SetName(visible, AvaloniaLocalizationCatalog.Format("Group {0} 显示", row.Group));
        AutomationProperties.SetAutomationId(visible, $"Inspector.Group.Row.{row.Group}.Visible");
        KeyboardNavigation.SetTabIndex(visible, groupDynamicFocusStartIndex++);
        visible.IsCheckedChanged += (object? _, RoutedEventArgs _) =>
        {
            SetGroupVisibility(row.Group, visible.IsChecked == true);
        };
        StackPanel stackPanel = new StackPanel
        {
            Children =
            {
                (Control)new TextBlock
                {
                    Text = ((row.Group == 0) ? "未分组" : $"Group {row.Group}"),
                    Foreground = foreground,
                    FontWeight = FontWeight.DemiBold
                },
                (Control)new TextBlock
                {
                    Text = $"{row.NoteCount} Notes · {row.DominantSound ?? "未指定 Key 音"}",
                    Foreground = MutedBrush,
                    FontSize = 10.0,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 125.0
                }
            }
        };
        Button button = PrimaryButton(row.IsCurrent ? "当前" : "使用", (object? _, RoutedEventArgs _) =>
        {
            WindowsGroupManagerCommand.SetPlacementGroup(tab, row.Group);
            groupIdBox.Text = row.Group.ToString(CultureInfo.InvariantCulture);
            viewModel.SetStatus((row.Group == 0) ? "新放置的 Note 将不分组" : AvaloniaLocalizationCatalog.Format("新放置的 Note 将进入 Group {0}", row.Group));
            RefreshGroupPanel();
        });
        ApplyWpfToolButtonStyle(button);
        button.Padding = new Thickness(7.0, 5.0);
        button.Margin = new Thickness(2.0, 0.0);
        button.IsEnabled = !row.IsCurrent;
        AutomationProperties.SetName(button, AvaloniaLocalizationCatalog.Format("Group {0} 使用", row.Group));
        AutomationProperties.SetAutomationId(button, $"Inspector.Group.Row.{row.Group}.Use");
        KeyboardNavigation.SetTabIndex(button, groupDynamicFocusStartIndex++);
        Grid grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto")
        };
        grid.Children.Add(visible);
        Grid.SetColumn(stackPanel, 1);
        grid.Children.Add(stackPanel);
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);
        if (row.Group != 0)
        {
            Button button2 = PrimaryButton("解散", (object? _, RoutedEventArgs _) =>
            {
                DissolveGroup(row.Group);
            });
            ApplyWpfToolButtonStyle(button2);
            button2.Padding = new Thickness(7.0, 5.0);
            button2.Margin = new Thickness(3.0, 0.0, 0.0, 0.0);
            button2.IsEnabled = row.IsVisible && !tab.Session.IsReadOnly && tab.Session.CanEditNotes;
            AutomationProperties.SetName(button2, AvaloniaLocalizationCatalog.Format("Group {0} 解散", row.Group));
            AutomationProperties.SetAutomationId(button2, $"Inspector.Group.Row.{row.Group}.Dissolve");
            KeyboardNavigation.SetTabIndex(button2, groupDynamicFocusStartIndex++);
            Grid.SetColumn(button2, 3);
            grid.Children.Add(button2);
        }

        global::Avalonia.Controls.Controls children = groupListPanel.Children;
        Border border = new Border();
        IBrush background;
        if (!row.IsCurrent)
        {
            IBrush brush2 = Brushes.Transparent;
            background = brush2;
        }
        else
        {
            background = PanelBrush;
        }

        border.Background = background;
        border.BorderBrush = (row.IsCurrent ? AccentBrush : StrokeBrush);
        border.BorderThickness = new Thickness(1.0);
        border.CornerRadius = new CornerRadius(4.0);
        border.Padding = new Thickness(7.0, 6.0);
        border.Child = grid;
        children.Add(border);
    }
}

private void CreateAndUseGroup()
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab != null)
    {
        int num = WindowsGroupManagerCommand.CreateAndUse(activeTab);
        groupIdBox.Text = num.ToString(CultureInfo.InvariantCulture);
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已创建并使用 Group {0}；当前选择未改变", num));
        RefreshGroupPanel();
    }
}

private void SetCurrentPlacementGroup()
{
    if (viewModel.ActiveTab != null && TryReadGroupId(out var group))
    {
        SetPlacementGroup(group);
    }
}

private void SetPlacementGroup(int group)
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab != null)
    {
        WindowsGroupManagerCommand.SetPlacementGroup(activeTab, group);
        groupIdBox.Text = group.ToString(CultureInfo.InvariantCulture);
        if (quickGroupLabel != null)
        {
            quickGroupLabel.Text = AvaloniaLocalizationCatalog.Format("分组 {0}", group);
        }

        viewModel.SetStatus((group == 0) ? "新放置的 Note 将不分组" : AvaloniaLocalizationCatalog.Format("新放置的 Note 将进入 Group {0}", group));
        RefreshGroupPanel();
    }
}

private void AssignSelectionToGroup()
{
    if (TryReadGroupId(out var group))
    {
        AssignSelectionToGroup(group);
    }
}

private void AssignSelectionToGroup(int group)
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null)
    {
        return;
    }

    try
    {
        WindowsGroupMutationResult windowsGroupMutationResult = WindowsGroupManagerCommand.AssignSelection(activeTab, group);
        MainWindowViewModel mainWindowViewModel = viewModel;
        string status;
        if (windowsGroupMutationResult.CandidateCount != 0)
        {
            status = (windowsGroupMutationResult.Applied ? AvaloniaLocalizationCatalog.Format("已将 {0} 个 Note 分配到 Group {1}（单次撤销）", windowsGroupMutationResult.ChangedCount, group) : AvaloniaLocalizationCatalog.Format("选中 Note 已属于 Group {0}", group));
        }
        else
        {
            status = "请先选择一个或多个可见、未锁定的 Note";
        }

        mainWindowViewModel.SetStatus(status);
        groupIdBox.Text = group.ToString(CultureInfo.InvariantCulture);
        UpdateSelectionUi();
    }
    catch (InvalidOperationException ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("分配 Group 失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private void ClearSelectedGroups()
{
    AssignSelectionToGroup(0);
}

private void ShowAllGroups()
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab != null)
    {
        WindowsGroupManagerCommand.ShowAll(activeTab);
        viewModel.SetStatus("全部 Note 分组已显示");
        RefreshGroupPanel();
        UpdateSelectionUi();
    }
}

private void HideNonzeroGroups()
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab != null)
    {
        int num = WindowsGroupManagerCommand.HideNonzero(activeTab);
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已隐藏非零组；从选择中移除 {0} 个 Note", num));
        RefreshGroupPanel();
        UpdateSelectionUi();
    }
}

private void SetGroupVisibility(int group, bool visible)
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab != null)
    {
        int num = WindowsGroupManagerCommand.SetGroupVisible(activeTab, group, visible);
        viewModel.SetStatus(visible ? AvaloniaLocalizationCatalog.Format("Group {0} 已显示", group) : AvaloniaLocalizationCatalog.Format("Group {0} 已隐藏；从选择中移除 {1} 个 Note", group, num));
        RefreshGroupPanel();
        UpdateSelectionUi();
    }
}

private void DissolveGroup(int group)
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null)
    {
        return;
    }

    try
    {
        WindowsGroupMutationResult windowsGroupMutationResult = WindowsGroupManagerCommand.Dissolve(activeTab, group);
        viewModel.SetStatus(windowsGroupMutationResult.Applied ? AvaloniaLocalizationCatalog.Format("已解散 Group {0} 的 {1} 个未锁定 Note（单次撤销）", group, windowsGroupMutationResult.ChangedCount) : AvaloniaLocalizationCatalog.Format("Group {0} 没有可解散的未锁定 Note", group));
        UpdateSelectionUi();
    }
    catch (InvalidOperationException ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("解散 Group 失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private bool TryReadGroupId(out int group)
{
    try
    {
        group = WindowsGroupManagerCommand.ParseGroupId(groupIdBox.Text);
        return true;
    }
    catch (FormatException ex)
    {
        group = 0;
        viewModel.SetStatus(ex.Message);
        return false;
    }
}

private void UpdateSelectionUi()
{
    timeline.PruneNonInteractiveNoteSelection();
    UpdateInspector();
    UpdateEditorAvailability();
    timeline.InvalidateVisual();
}

private void OpenExistingNoteSounds()
{
    DesktopChartTab tab = viewModel.ActiveTab;
    if (!TryGetEditableSelectedNote(tab, out ChartNote _))
    {
        return;
    }

    string directoryName = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(tab.WorkingPath));
    if (string.IsNullOrWhiteSpace(directoryName))
    {
        return;
    }

    bool onlineRoomChart = IsBoundOnlineRoomChart(tab);
    string text = ((onlineRoomChart || string.IsNullOrWhiteSpace(tab.SourcePath)) ? null : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(tab.SourcePath)));
    ChartNoteSoundCandidate[] array = (
        from candidate in CoalesceNoteSoundCandidates(ChartAssetImportService.EnumerateNoteSoundCandidates(new string[2] { directoryName, text }), directoryName)
        where !onlineRoomChart || IsListedFrozenRoomNoteSound(tab, candidate.FullPath)select candidate).ToArray();
    noteSoundPickerItems.Children.Clear();
    noteSoundPickerFocusableButtons.Clear();
    noteSoundPickerFocusableButtons.Add(noteSoundPickerClearButton);
    noteSoundPickerCountText.Text = AvaloniaLocalizationCatalog.Format("{0} 个", array.Length);
    noteSoundPickerEmptyText.Text = (onlineRoomChart ? "房间资源中没有可用的 Key 音。" : "谱面目录中没有可用的音频文件。");
    noteSoundPickerEmptyText.IsVisible = array.Length == 0;
    noteSoundPickerScrollViewer.IsVisible = array.Length != 0;
    if (array.Length == 0)
    {
        noteSoundPickerLastNavigation = "opened-empty";
    }
    else
    {
        HashSet<string> hashSet = (
            from @group in array.GroupBy((ChartNoteSoundCandidate candidate) => candidate.FileName, StringComparer.OrdinalIgnoreCase)
            where @group.Count() > 1
            select @group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ChartNoteSoundCandidate[] array2 = array;
        foreach (ChartNoteSoundCandidate chartNoteSoundCandidate in array2)
        {
            string text2 = (hashSet.Contains(chartNoteSoundCandidate.FileName) ? (chartNoteSoundCandidate.FileName + "  —  " + System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(chartNoteSoundCandidate.FullPath))) : chartNoteSoundCandidate.FileName);
            Button button = new Button();
            ConfigureNoteSoundPickerItemButton(button, "♪", text2, "#83AEB1");
            ToolTip.SetTip(button, chartNoteSoundCandidate.FullPath);
            AutomationProperties.SetName(button, text2);
            AutomationProperties.SetAutomationId(button, $"Inspector.Note.SoundPicker.Item.{noteSoundPickerFocusableButtons.Count - 1}");
            KeyboardNavigation.SetTabIndex(button, noteSoundPickerFocusableButtons.Count);
            string sourcePath = chartNoteSoundCandidate.FullPath;
            button.Click += async (object? _, RoutedEventArgs _) =>
            {
                noteSoundExistingPopup.IsOpen = false;
                await AssignSelectedNoteSoundAsync(tab, sourcePath, import: false);
            };
            noteSoundPickerItems.Children.Add(button);
            noteSoundPickerFocusableButtons.Add(button);
        }
    }

    noteSoundPickerEscapeRestoredFocus = false;
    noteSoundPickerLastRawKey = "none";
    noteSoundPickerLastResolvedKey = "none";
    noteSoundPickerLastInputRoute = "none";
    noteSoundExistingPopup.IsOpen = true;
}

private static void ConfigureNoteSoundPickerItemButton(Button button, string glyph, string label, string glyphColor)
{
    button.Foreground = new SolidColorBrush(Color.Parse("#DDE3E4"));
    button.Background = Brushes.Transparent;
    button.BorderBrush = Brushes.Transparent;
    button.BorderThickness = new Thickness(1.0);
    button.Padding = new Thickness(11.0, 8.0);
    button.Margin = new Thickness(0.0, 1.0);
    button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
    button.CornerRadius = new CornerRadius(3.0);
    button.Cursor = new Cursor(StandardCursorType.Hand);
    TextBlock text = new TextBlock
    {
        Text = label,
        Foreground = button.Foreground,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };
    Grid content = new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("22,*"),
        Children =
        {
            (Control)new TextBlock
            {
                Text = glyph,
                Foreground = new SolidColorBrush(Color.Parse(glyphColor)),
                FontSize = ((glyph == "×") ? 15 : 12),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            (Control)text
        }
    };
    Grid.SetColumn(text, 1);
    button.Content = content;
    button.PointerEntered += (object? _, PointerEventArgs _) =>
    {
        button.Background = new SolidColorBrush(Color.Parse("#263034"));
        button.BorderBrush = new SolidColorBrush(Color.Parse("#4B6267"));
        button.Foreground = Brushes.White;
        text.Foreground = Brushes.White;
    };
    button.PointerExited += (object? _, PointerEventArgs _) =>
    {
        button.Background = Brushes.Transparent;
        Button button2 = button;
        IBrush borderBrush;
        if (!button.IsFocused)
        {
            IBrush transparent = Brushes.Transparent;
            borderBrush = transparent;
        }
        else
        {
            IBrush transparent = new SolidColorBrush(Color.Parse("#71999C"));
            borderBrush = transparent;
        }

        button2.BorderBrush = borderBrush;
        button.Foreground = new SolidColorBrush(Color.Parse("#DDE3E4"));
        text.Foreground = button.Foreground;
    };
    button.GotFocus += (object? _, FocusChangedEventArgs _) =>
    {
        button.BorderBrush = new SolidColorBrush(Color.Parse("#71999C"));
    };
    button.LostFocus += (object? _, FocusChangedEventArgs _) =>
    {
        if (!button.IsPointerOver)
        {
            button.BorderBrush = Brushes.Transparent;
        }
    };
}

private void NoteSoundPicker_Opened()
{
    WindowsDisplayScaleSettings settings = displayScaleController.CurrentSettings.Normalize();
    AvaloniaPopupScalePlan avaloniaPopupScalePlan = AvaloniaPopupScaleController.Resolve(12.0, settings);
    if (TopLevel.GetTopLevel(noteSoundPickerSurface)is PopupRoot popupRoot)
    {
        popupRoot.Transform = new ScaleTransform(avaloniaPopupScalePlan.UiScale, avaloniaPopupScalePlan.UiScale);
    }

    noteSoundPickerTitleText.FontSize = 13.0 * avaloniaPopupScalePlan.FontScale;
    noteSoundPickerSubtitleText.FontSize = 9.0 * avaloniaPopupScalePlan.FontScale;
    noteSoundPickerCountText.FontSize = 10.0 * avaloniaPopupScalePlan.FontScale;
    noteSoundPickerEmptyText.FontSize = 12.0 * avaloniaPopupScalePlan.FontScale;
    foreach (Button noteSoundPickerFocusableButton in noteSoundPickerFocusableButtons)
    {
        noteSoundPickerFocusableButton.FontSize = 12.0 * avaloniaPopupScalePlan.FontScale;
    }

    noteSoundPickerScrollViewer.ScrollToHome();
    noteSoundPickerLastNavigation = "opened-clear";
    UpdateNoteSoundPickerAutomation();
    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        noteSoundPickerClearButton.Focus();
        UpdateNoteSoundPickerAutomation();
    }, DispatcherPriority.Input);
}

private void NoteSoundPicker_KeyDown(object? sender, KeyEventArgs eventArgs)
{
    Key key = eventArgs.Key;
    PhysicalKey physicalKey = eventArgs.PhysicalKey;
    string keySymbol = eventArgs.KeySymbol;
    Key key2 = eventArgs.Key;
    bool flag = (((uint)(key2 - 155) <= 1u || key2 == Key.DeadCharProcessed) ? true : false);
    Key key3 = WindowsEditorShortcutGesture.ResolveReportedKey(key, physicalKey, keySymbol, flag ? WindowsShortcutPhysicalKeyState.TryResolvePressedKey() : ((Key? )null));
    noteSoundPickerLastRawKey = eventArgs.Key.ToString();
    noteSoundPickerLastResolvedKey = key3.ToString();
    noteSoundPickerLastInputRoute = sender?.GetType().Name ?? "null";
    int num = ((!(FocusManager?.GetFocusedElement()is Button item)) ? (-1) : noteSoundPickerFocusableButtons.IndexOf(item));
    WindowsNoteSoundPickerNavigationResult windowsNoteSoundPickerNavigationResult = WindowsNoteSoundPickerParityPolicy.ResolveNavigation(key3, eventArgs.KeyModifiers, num, noteSoundPickerFocusableButtons.Count);
    if (windowsNoteSoundPickerNavigationResult.Action == WindowsNoteSoundPickerNavigationAction.None)
    {
        UpdateNoteSoundPickerAutomation();
        return;
    }

    eventArgs.Handled = true;
    if (windowsNoteSoundPickerNavigationResult.Action == WindowsNoteSoundPickerNavigationAction.Focus)
    {
        Button button = noteSoundPickerFocusableButtons[windowsNoteSoundPickerNavigationResult.TargetIndex];
        button.Focus();
        button.BringIntoView();
        noteSoundPickerLastNavigation = $"{key3}:{num}->{windowsNoteSoundPickerNavigationResult.TargetIndex}";
        UpdateNoteSoundPickerAutomation();
    }
    else
    {
        noteSoundPickerLastNavigation = "Escape:close-restore";
        noteSoundExistingPopup.IsOpen = false;
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            noteSoundExistingButton.Focus();
            noteSoundPickerEscapeRestoredFocus = FocusManager?.GetFocusedElement() == noteSoundExistingButton;
            UpdateNoteSoundPickerAutomation();
        }, DispatcherPriority.Input);
    }
}

private void UpdateNoteSoundPickerAutomation()
{
    int value = ((!(FocusManager?.GetFocusedElement()is Button item)) ? (-1) : noteSoundPickerFocusableButtons.IndexOf(item));
    AutomationProperties.SetHelpText(noteSoundPickerSurface, $"Open={noteSoundExistingPopup.IsOpen}; FocusableCount={noteSoundPickerFocusableButtons.Count}; FocusedIndex={value}; LastNavigation={noteSoundPickerLastNavigation}; LastRawKey={noteSoundPickerLastRawKey}; LastResolvedKey={noteSoundPickerLastResolvedKey}; LastInputRoute={noteSoundPickerLastInputRoute}; EscapeRestoredFocus={noteSoundPickerEscapeRestoredFocus}; Width={330.0:R}; MaxHeight={370.0:R}; ScrollMaxHeight={250.0:R}");
    AutomationProperties.SetHelpText(noteSoundExistingButton, $"PickerOpen={noteSoundExistingPopup.IsOpen}; FocusableCount={noteSoundPickerFocusableButtons.Count}; CandidateCount={Math.Max(0, noteSoundPickerFocusableButtons.Count - 1)}; FocusedIndex={value}; EscapeRestoredFocus={noteSoundPickerEscapeRestoredFocus}; LastNavigation={noteSoundPickerLastNavigation}; LastRawKey={noteSoundPickerLastRawKey}; LastResolvedKey={noteSoundPickerLastResolvedKey}; LastInputRoute={noteSoundPickerLastInputRoute}; Width={330.0:R}; MaxHeight={370.0:R}; ScrollMaxHeight={250.0:R}; VerticalOffset={5.0:R}");
}

private async Task ImportSelectedNoteSoundAsync()
{
    DesktopChartTab tab = viewModel.ActiveTab;
    if (!TryGetEditableSelectedNote(tab, out ChartNote _))
    {
        return;
    }

    if (IsBoundOnlineRoomChart(tab))
    {
        await ShowSettingsMessageAsync("联机中不能新增本地 Key 音", "当前联机协议只同步 Note 的 Key 音文件名，不会把新选择的本地音频分发给其他成员。请在建房前加入 Key 音，或在房间结束后的“房间留存”标签中另存后再选择文件。");
        return;
    }

    string text;
    try
    {
        text = await PickLocalFileAsync(new FilePickerOpenOptions { Title = "选择 Note Key 音", AllowMultiple = false, FileTypeFilter = new FilePickerFileType[]
{
    new FilePickerFileType("音频文件")
    {
        Patterns = new string[6] { "*.wav", "*.ogg", "*.mp3", "*.flac", "*.m4a", "*.aac" },
        MimeTypes = new string[]
{
    "audio/*"
}
    }
} }, PickedFileRetention.Transient);
    }
    catch (Exception ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("设置 Note Key 音失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
        return;
    }

    if (!string.IsNullOrWhiteSpace(text))
    {
        await AssignSelectedNoteSoundAsync(tab, System.IO.Path.GetFullPath(text), import: true);
    }
}

private async Task AssignSelectedNoteSoundAsync(DesktopChartTab tab, string? sourcePath, bool import)
{
    ChartAssetImportResult imported = null;
    try
    {
        string sound = sourcePath;
        if (import && !string.IsNullOrWhiteSpace(sourcePath))
        {
            imported = await Task.Run(() => ChartAssetImportService.ImportNoteSound(tab.WorkingPath, sourcePath, tab.SourcePath));
            sound = imported.FileName;
        }
        else if (!string.IsNullOrWhiteSpace(sound))
        {
            sound = System.IO.Path.GetFileName(sound);
        }

        if (viewModel.ActiveTab?.Id != tab.Id)
        {
            throw new OperationCanceledException("选择 Key 音期间已切换谱面。");
        }

        WindowsNoteSoundAssignmentPolicy soundPolicy = await CreateNoteSoundAssignmentPolicyAsync(tab, sound);
        if (viewModel.ActiveTab?.Id != tab.Id)
        {
            throw new OperationCanceledException("校验 Key 音期间已切换谱面。");
        }

        WindowsNoteSoundAssignmentResult windowsNoteSoundAssignmentResult = WindowsNotePropertyCommand.AssignSound(tab.Session, sound, soundPolicy, tab.HiddenGroups);
        noteSoundBox.Text = windowsNoteSoundAssignmentResult.Sound ?? string.Empty;
        MainWindowViewModel mainWindowViewModel = viewModel;
        string status;
        if (windowsNoteSoundAssignmentResult.Sound == null)
        {
            status = "已清除当前 Note 的 Key 音引用；音频文件本身未删除";
        }
        else if (import)
        {
            status = AvaloniaLocalizationCatalog.Format("Key 音已复制为 {0}；保存谱面时写回曲库目录", windowsNoteSoundAssignmentResult.Sound);
        }
        else
        {
            status = (IsBoundOnlineRoomChart(tab) ? AvaloniaLocalizationCatalog.Format("已使用房间冻结资源 {0}；不会产生资源上传请求", windowsNoteSoundAssignmentResult.Sound) : AvaloniaLocalizationCatalog.Format("已使用谱面资源 {0}", windowsNoteSoundAssignmentResult.Sound));
        }

        mainWindowViewModel.SetStatus(status);
        UpdateSelectionUi();
    }
    catch (OperationCanceledException ex)
    {
        if (imported?.Created ?? false)
        {
            TryDeleteImportedAsset(imported.DestinationPath);
        }

        viewModel.SetStatus(ex.Message);
    }
    catch (Exception ex2)when ((ex2 is InvalidOperationException || ex2 is ArgumentException || ex2 is IOException || ex2 is UnauthorizedAccessException || ex2 is NotSupportedException) ? true : false)
    {
        if (imported?.Created ?? false)
        {
            TryDeleteImportedAsset(imported.DestinationPath);
        }

        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("设置 Note Key 音失败：{0}", AvaloniaLocalizationCatalog.Text(ex2.Message)));
    }
}

private bool TryGetEditableSelectedNote(DesktopChartTab? tab, out ChartNote? note)
{
    note = null;
    if (tab == null)
    {
        return false;
    }

    ChartNote[] array = tab.Session.Document.Notes.Where((ChartNote candidate) => tab.Session.Selection.Contains(candidate.Id) && !tab.HiddenGroups.Contains(candidate.Group)).ToArray();
    if (array.Length != 1)
    {
        viewModel.SetStatus("请先只选择一个 Note。");
        return false;
    }

    if (tab.Session.IsReadOnly || !tab.Session.CanEditNotes || (!tab.Session.AllowOwnerProtectedEdits && tab.Session.IsOwnerProtected(array[0])))
    {
        viewModel.SetStatus("当前 Note 没有可用的编辑权限。");
        return false;
    }

    note = array[0];
    return true;
}

private bool IsBoundOnlineRoomChart(DesktopChartTab tab)
{
    if (roomChartSyncRoomId.HasValue)
    {
        return roomChartSyncTab == tab;
    }

    return false;
}

private bool IsListedFrozenRoomNoteSound(DesktopChartTab tab, string fullPath)
{
    if (IsBoundOnlineRoomChart(tab) && ChartAssetImportService.IsSupportedNoteSoundFile(fullPath))
    {
        return roomChartFrozenResources?.ContainsListedFile(fullPath) ?? false;
    }

    return false;
}

private async Task<WindowsNoteSoundAssignmentPolicy> CreateNoteSoundAssignmentPolicyAsync(DesktopChartTab tab, string? sound)
{
    if (!IsBoundOnlineRoomChart(tab))
    {
        return new WindowsNoteSoundAssignmentPolicy(IsOnlineRoom: false, null);
    }

    string text = sound?.Trim();
    if (string.IsNullOrWhiteSpace(text))
    {
        return new WindowsNoteSoundAssignmentPolicy(IsOnlineRoom: true, (string _) => false);
    }

    string fileName = System.IO.Path.GetFileName(text);
    if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(text, fileName, StringComparison.Ordinal))
    {
        return new WindowsNoteSoundAssignmentPolicy(IsOnlineRoom: true, (string _) => false);
    }

    string fullPath;
    try
    {
        string directoryName = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(tab.WorkingPath));
        if (string.IsNullOrWhiteSpace(directoryName))
        {
            return new WindowsNoteSoundAssignmentPolicy(IsOnlineRoom: true, (string _) => false);
        }

        fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(directoryName, fileName));
    }
    catch (Exception ex)when ((ex is ArgumentException || ex is IOException || ex is NotSupportedException) ? true : false)
    {
        return new WindowsNoteSoundAssignmentPolicy(IsOnlineRoom: true, (string _) => false);
    }

    WindowsFrozenRoomResourceRegistry registry = roomChartFrozenResources;
    bool flag = registry != null;
    if (flag)
    {
        flag = await registry.VerifyAsync(fullPath).ConfigureAwait(continueOnCapturedContext: true);
    }

    bool verified = flag;
    return new WindowsNoteSoundAssignmentPolicy(IsOnlineRoom: true, (string candidate) => verified && roomChartFrozenResources == registry && IsBoundOnlineRoomChart(tab) && PathsEqual(candidate, fullPath));
}

private static IReadOnlyList<ChartNoteSoundCandidate> CoalesceNoteSoundCandidates(IReadOnlyList<ChartNoteSoundCandidate> candidates, string workingRoot)
{
    List<ChartNoteSoundCandidate> list = new List<ChartNoteSoundCandidate>();
    foreach (IGrouping<string, ChartNoteSoundCandidate> item in candidates.GroupBy((ChartNoteSoundCandidate candidate) => candidate.FileName, StringComparer.OrdinalIgnoreCase))
    {
        ChartNoteSoundCandidate working = item.FirstOrDefault((ChartNoteSoundCandidate candidate) => string.Equals(System.IO.Path.GetDirectoryName(candidate.FullPath)?.TrimEnd(System.IO.Path.DirectorySeparatorChar), workingRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        if (working == null)
        {
            list.AddRange(item);
            continue;
        }

        list.Add(working);
        list.AddRange(item.Where((ChartNoteSoundCandidate candidate) => (object)candidate != working && !NoteSoundCandidateFilesEqual(candidate.FullPath, working.FullPath)));
    }

    return list.OrderBy((ChartNoteSoundCandidate candidate) => candidate.FileName, StringComparer.OrdinalIgnoreCase).ThenBy((ChartNoteSoundCandidate candidate) => candidate.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
}

private static bool NoteSoundCandidateFilesEqual(string left, string right)
{
    try
    {
        FileInfo fileInfo = new FileInfo(left);
        FileInfo fileInfo2 = new FileInfo(right);
        if (fileInfo.Length != fileInfo2.Length)
        {
            return false;
        }

        using FileStream source = File.OpenRead(left);
        using FileStream source2 = File.OpenRead(right);
        return SHA256.HashData(source).AsSpan().SequenceEqual(SHA256.HashData(source2));
    }
    catch (Exception ex)when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
    {
        return false;
    }
}

private async Task ApplyNotePropertiesAsync()
{
    DesktopChartTab tab = viewModel.ActiveTab;
    if (tab == null)
    {
        return;
    }

    EditorSession session = tab.Session;
    try
    {
        string sound = noteSoundBox.Text ?? string.Empty;
        WindowsNoteSoundAssignmentPolicy soundPolicy = await CreateNoteSoundAssignmentPolicyAsync(tab, sound);
        if (viewModel.ActiveTab?.Id != tab.Id)
        {
            throw new InvalidOperationException("校验 Key 音期间已切换谱面。");
        }

        WindowsNotePropertyApplyResult windowsNotePropertyApplyResult = WindowsNotePropertyCommand.Apply(session, new WindowsNotePropertyFields(noteBeatBox.Text ?? string.Empty, noteEndBeatBox.Text ?? string.Empty, noteLaneBox.Text ?? string.Empty, noteVolumeBox.Text ?? string.Empty, sound, noteGroupBox.Text ?? string.Empty), soundPolicy, tab.HiddenGroups);
        string text = (windowsNotePropertyApplyResult.IsDraft ? $"D:{windowsNotePropertyApplyResult.Column}" : windowsNotePropertyApplyResult.Column.ToString(CultureInfo.InvariantCulture));
        ResetNoteInspectorFill();
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已应用 Note 属性 · Beat {0} · 轨道 {1}{2}", windowsNotePropertyApplyResult.Beat, text, windowsNotePropertyApplyResult.EndBeat.HasValue ? $" · End {windowsNotePropertyApplyResult.EndBeat.Value}" : " · Tap"));
        UpdateSelectionUi();
    }
    catch (Exception ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("应用 Note 属性失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private HashSet<Guid> SelectedTimingListIds()
{
    return timingList.SelectedItems?.OfType<TimingListItem>().Select((TimingListItem item) => item.Id).ToHashSet() ?? new HashSet<Guid>();
}

private void SyncTimingListSelection(IReadOnlyCollection<Guid> timingIds, Guid? primaryId = null)
{
    HashSet<Guid> ids = timingIds.ToHashSet();
    TimingListItem[] source = timingList.Items.OfType<TimingListItem>().ToArray();
    TimingListItem timingListItem = (primaryId.HasValue ? source.FirstOrDefault((TimingListItem item) => item.Id == primaryId.Value) : source.FirstOrDefault((TimingListItem item) => ids.Contains(item.Id)));
    bool flag = suppressTimingSelection;
    suppressTimingSelection = true;
    try
    {
        timingList.SelectedItems?.Clear();
        timingList.SelectedItem = timingListItem;
        if (timingList.SelectedItems != null)
        {
            foreach (TimingListItem item in source.Where((TimingListItem item) => ids.Contains(item.Id)))
            {
                if (!timingList.SelectedItems.Contains(item))
                {
                    timingList.SelectedItems.Add(item);
                }
            }
        }
    }
    finally
    {
        suppressTimingSelection = flag;
    }

    if (timingListItem != null)
    {
        PopulateTimingFields(timingListItem.Id);
    }
}

private void UpdateEditorAvailability()
{
    EditorSession editorSession = viewModel.ActiveTab?.Session;
    bool flag = editorSession != null && !editorSession.IsReadOnly && editorSession.CanEditNotes;
    bool flag2 = editorSession != null && !editorSession.IsReadOnly && editorSession.CanEditTiming;
    bool flag3 = editorSession != null && !editorSession.IsReadOnly && editorSession.CanEditMetadata;
    if (quickMirrorButton != null)
    {
        quickMirrorButton.IsEnabled = flag;
    }

    if (quickEffectButton != null)
    {
        quickEffectButton.IsEnabled = flag2;
    }

    if (!WindowsAvaloniaEditorInputPolicy.CanUseTimelineTool(editorSession, timeline.Tool))
    {
        timeline.Tool = TimelineTool.Select;
    }

    UpdateTimelineToolButtons();
    Control[] array = new Control[17]
    {
        metadataTitleBox,
        metadataTitleOriginalBox,
        metadataRomanizeTitleButton,
        metadataArtistBox,
        metadataArtistOriginalBox,
        metadataRomanizeArtistButton,
        metadataCreatorBox,
        metadataVersionBox,
        metadataBpmBox,
        metadataKeyCountBox,
        metadataPreviewTimeBox,
        metadataOverallDifficultyBox,
        metadataHpDrainRateBox,
        metadataPreviewHereButton,
        metadataAudioSelectButton,
        metadataCoverSelectButton,
        metadataBackgroundSelectButton
    };
    for (int i = 0; i < array.Length; i++)
    {
        array[i].IsEnabled = flag3;
    }

    metadataAudioFileBox.IsEnabled = editorSession != null;
    metadataCoverFileBox.IsEnabled = editorSession != null;
    metadataBackgroundFileBox.IsEnabled = editorSession != null;
    metadataAudioSelectButton.IsEnabled = flag3 && !audioImportInProgress && !imageImportInProgress;
    metadataCoverSelectButton.IsEnabled = flag3 && !audioImportInProgress && !imageImportInProgress;
    metadataBackgroundSelectButton.IsEnabled = flag3 && !audioImportInProgress && !imageImportInProgress;
    metadataRomanizeTitleButton.IsEnabled = flag3 && !titleRomanizationInProgress;
    metadataRomanizeArtistButton.IsEnabled = flag3 && !artistRomanizationInProgress;
    metadataOffsetBox.IsEnabled = flag2;
    metadataAnalyzeSingleTempoButton.IsEnabled = flag2 && !audioImportInProgress && (!audioAnalysisInProgress || activeTempoAnalysisKind == TempoAnalysisKind.Single);
    metadataAnalyzeVariableTempoButton.IsEnabled = flag2 && !audioImportInProgress && (!audioAnalysisInProgress || activeTempoAnalysisKind == TempoAnalysisKind.Variable);
    metadataAnalyzeManualTempoButton.IsEnabled = flag2 && !audioImportInProgress && (!audioAnalysisInProgress || activeTempoAnalysisKind == TempoAnalysisKind.Manual);
    metadataApplyButton.IsEnabled = (flag3 | flag2) && !audioAnalysisInProgress && !audioImportInProgress && !imageImportInProgress && !titleRomanizationInProgress && !artistRomanizationInProgress;
    array = new Control[6]
    {
        timingBeatBox,
        timingBpmBox,
        timingDelayBox,
        timingEffectBox,
        timingValueBox,
        timingCustomFieldBox
    };
    for (int i = 0; i < array.Length; i++)
    {
        array[i].IsEnabled = flag2;
    }

    TextBlock textBlock = timingPermissionText;
    string? text;
    if (editorSession == null)
    {
        text = string.Empty;
    }
    else if (editorSession.IsReadOnly)
    {
        text = "当前谱面为只读状态，不能编辑 Timing / Effect。";
    }
    else
    {
        text = ((!editorSession.CanEditTiming) ? "当前没有编辑 Timing / Effect 的权限。" : string.Empty);
    }

    textBlock.Text = text;
    timingPermissionText.IsVisible = !string.IsNullOrEmpty(timingPermissionText.Text);
    HashSet<Guid> selectedTimingIds = timeline.SelectedTimingIds.ToHashSet();
    int num = editorSession?.Selection.Count ?? 0;
    int num2 = editorSession?.Document.Timing.Count((TimingEvent timing) => selectedTimingIds.Contains(timing.Id) && timing.Effect.HasValue) ?? 0;
    bool flag4 = num > 0;
    bool flag5 = num2 > 0;
    bool flag6 = editorSession != null && !editorSession.IsReadOnly && (!flag4 || editorSession.CanEditNotes) && (!flag5 || editorSession.CanEditTiming);
    bool flag7 = editorSession != null && !editorSession.IsReadOnly && (editorSession.CanEditNotes || viewModel.HasMixedSelectionClipboard) && (viewModel.MixedClipboardNoteCount == 0 || editorSession.CanEditNotes) && (viewModel.MixedClipboardEffectCount == 0 || editorSession.CanEditTiming);
    SetQuickShortcutEnabled("Undo", editorSession?.CanUndo ?? false);
    if (toolbarUndoButton != null)
    {
        toolbarUndoButton.IsEnabled = editorSession?.CanUndo ?? false;
    }

    SetQuickShortcutEnabled("Redo", editorSession?.CanRedo ?? false);
    if (toolbarRedoButton != null)
    {
        toolbarRedoButton.IsEnabled = editorSession?.CanRedo ?? false;
    }

    SetQuickShortcutEnabled("Cut", flag6 && (num > 0 || num2 > 0));
    SetQuickShortcutEnabled("Copy", editorSession != null && (num > 0 || num2 > 0));
    SetQuickShortcutEnabled("Paste", flag7);
    SetQuickShortcutEnabled("SelectAll", editorSession != null);
    RefreshTrackCommandButtons();
    if (mixedCopyButton != null)
    {
        mixedCopyButton.IsEnabled = editorSession != null && (num > 0 || num2 > 0);
    }

    if (mixedPasteButton != null)
    {
        mixedPasteButton.IsEnabled = flag7;
    }

    if (mixedDeleteButton != null)
    {
        mixedDeleteButton.IsEnabled = flag6 && (num > 0 || num2 > 0);
    }

    if (extensionMenuButton != null)
    {
        extensionMenuButton.IsEnabled = editorSession != null;
    }

    if (extensionSelectLongNotesButton != null)
    {
        extensionSelectLongNotesButton.IsEnabled = editorSession != null && num > 0;
    }

    if (extensionSelectTapsButton != null)
    {
        extensionSelectTapsButton.IsEnabled = editorSession != null && num > 0;
    }

    if (longToTapButton != null)
    {
        longToTapButton.IsEnabled = flag;
    }

    if (tapToLongButton != null)
    {
        tapToLongButton.IsEnabled = flag;
    }

    if (randomPermutationButton != null)
    {
        randomPermutationButton.IsEnabled = flag && num > 0;
    }

    if (fullRandomButton != null)
    {
        fullRandomButton.IsEnabled = flag && num > 0;
    }

    array = new Control[10]
    {
        noteBeatBox,
        noteEndBeatBox,
        noteLaneBox,
        noteVolumeBox,
        noteSoundBox,
        noteSoundExistingButton,
        noteSoundSelectButton,
        noteGroupBox,
        noteApplyButton,
        noteDeleteButton
    };
    for (int i = 0; i < array.Length; i++)
    {
        array[i].IsEnabled = flag;
    }

    groupIdBox.IsEnabled = editorSession != null;
    groupCreateAndUseButton.IsEnabled = flag;
    groupSetCurrentButton.IsEnabled = editorSession != null;
    groupAssignButton.IsEnabled = flag;
    groupShowAllButton.IsEnabled = editorSession != null;
    groupHideNonzeroButton.IsEnabled = editorSession != null;
    groupClearSelectedButton.IsEnabled = flag;
    UpdateNativeTimingControls(editorSession);
    if (timingAddButton != null)
    {
        timingAddButton.IsEnabled = flag2;
    }

    if (timingApplyButton != null)
    {
        timingApplyButton.IsEnabled = flag2 && timingList.SelectedItem != null;
    }

    if (timingDeleteButton != null)
    {
        timingDeleteButton.IsEnabled = flag2 && selectedTimingIds.Count > 0;
    }

    if (timingCopyButton != null)
    {
        timingCopyButton.IsEnabled = editorSession != null;
    }

    if (timingPasteButton != null)
    {
        timingPasteButton.IsEnabled = flag2 && viewModel.MixedClipboardEffectCount > 0;
    }

    RefreshOwnerLockUi();
}

private void RefreshOwnerLockUi()
{
    if (ownerLockButton == null || quickOwnerLockButton == null)
    {
        return;
    }

    DesktopChartTab activeTab = viewModel.ActiveTab;
    EditorSession editorSession = activeTab?.Session;
    bool flag = activeTab != null && activeTab == roomChartSyncTab && roomChartSyncCoordinator != null && roomChartSyncRoomId.HasValue;
    ownerLockButton.IsVisible = flag;
    quickOwnerLockButton.IsVisible = flag;
    if (!flag || editorSession == null)
    {
        ownerLockButton.IsEnabled = false;
        ownerLockButton.Content = "锁定选区";
        quickOwnerLockButton.IsEnabled = false;
        if (quickOwnerLockLabel != null)
        {
            quickOwnerLockLabel.Text = "锁定选区";
        }

        AutomationProperties.SetName(quickOwnerLockButton, "锁定选区");
        return;
    }

    IReadOnlyList<OwnerLockSelectionRange> readOnlyList = SelectedOwnerLockRanges(activeTab ?? throw new InvalidOperationException("The online editor tab is unavailable."));
    bool flag2 = OwnerLockSelectionPlanner.IsFullyCovered(readOnlyList, editorSession.OwnerProtectedRegions.Select((OwnerProtectedRegion region) => new OwnerLockSelectionRange(region.StartBeat, region.EndBeat)));
    ownerLockButton.IsEnabled = editorSession.AllowOwnerProtectedEdits && readOnlyList.Count > 0;
    quickOwnerLockButton.IsEnabled = ownerLockButton.IsEnabled;
    string text;
    if (editorSession.AllowOwnerProtectedEdits)
    {
        text = (flag2 ? "解除选区" : "锁定选区");
    }
    else
    {
        text = "仅房主可锁定";
    }

    ownerLockButton.Content = text;
    if (quickOwnerLockLabel != null)
    {
        quickOwnerLockLabel.Text = text;
    }

    AutomationProperties.SetName(quickOwnerLockButton, text);
}

private async Task ToggleOwnerLockSelectionAsync()
{
    OnlineAccountSession onlineAccountSession = onlineAccountHost?.Session;
    DesktopChartTab tab = roomChartSyncTab;
    Guid? roomId = roomChartSyncRoomId;
    if (onlineAccountSession == null || tab == null || !roomId.HasValue || viewModel.ActiveTab != tab || !tab.Session.AllowOwnerProtectedEdits)
    {
        return;
    }

    IReadOnlyList<OwnerLockSelectionRange> readOnlyList = SelectedOwnerLockRanges(tab);
    if (readOnlyList.Count == 0)
    {
        return;
    }

    if (ownerLockButton != null)
    {
        ownerLockButton.IsEnabled = false;
    }

    if (quickOwnerLockButton != null)
    {
        quickOwnerLockButton.IsEnabled = false;
    }

    try
    {
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30.0));
        OnlineToggleChartLockSelectionResult onlineToggleChartLockSelectionResult = await onlineAccountSession.ToggleRoomChartLockSelectionAsync(roomId.Value, readOnlyList.Select((OwnerLockSelectionRange range) => new OnlineChartLockRange(new OnlineSyncBeat(range.StartBeat.Bar, range.StartBeat.Numerator, range.StartBeat.Denominator), new OnlineSyncBeat(range.EndBeat.Bar, range.EndBeat.Numerator, range.EndBeat.Denominator))).ToArray(), timeout.Token);
        if (roomChartSyncRoomId != roomId || roomChartSyncTab != tab)
        {
            return;
        }

        applyingRemoteChartSync = true;
        try
        {
            tab.Session.ReplaceOwnerProtectedRegions(OnlineChartSyncCodec.ToOwnerRegions(onlineToggleChartLockSelectionResult.Locks));
        }
        finally
        {
            applyingRemoteChartSync = false;
        }

        viewModel.SetStatus(onlineToggleChartLockSelectionResult.IsUnlock ? "房主已解除当前连续选区；选区外两侧的锁定保持不变" : "已锁定当前选区；单对象保持精确行 / LN 区间，多选保持一个连续区间");
        timeline.InvalidateVisual();
    }
    catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is WebException || ex is OperationCanceledException || ex is JsonException || ex is InvalidOperationException) ? true : false)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("选区锁同步失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
    finally
    {
        RefreshOwnerLockUi();
    }
}

private IReadOnlyList<OwnerLockSelectionRange> SelectedOwnerLockRanges(DesktopChartTab tab)
{
    EditorSession session = tab.Session;
    HashSet<Guid> selectedTimingIds = timeline.SelectedTimingIds.ToHashSet();
    ChartNote[] array = session.Document.Notes.Where((ChartNote note) => session.Selection.Contains(note.Id) && !tab.HiddenGroups.Contains(note.Group)).ToArray();
    TimingEvent[] array2 = session.Document.Timing.Where((TimingEvent item) => selectedTimingIds.Contains(item.Id)).ToArray();
    if (array.Length == 0 && array2.Length == 0)
    {
        return Array.Empty<OwnerLockSelectionRange>();
    }

    return OwnerLockSelectionPlanner.BuildSelectedObjectRanges(array.Select((ChartNote note) =>
    {
        BeatPosition beat = note.Beat;
        BeatPosition? endBeat = note.EndBeat;
        BeatPosition endBeat2;
        if (endBeat.HasValue)
        {
            BeatPosition valueOrDefault = endBeat.GetValueOrDefault();
            if (valueOrDefault.CompareTo(note.Beat) > 0)
            {
                endBeat2 = valueOrDefault;
                goto IL_004f;
            }
        }

        endBeat2 = OwnerLockSelectionPlanner.BuildPointRange(note.Beat, session.SnapDivision).EndBeat;
        goto IL_004f;
        IL_004f:
            return new OwnerLockSelectionRange(beat, endBeat2);
    }).Concat(array2.Select((TimingEvent item) => new OwnerLockSelectionRange(item.Beat, OwnerLockSelectionPlanner.BuildPointRange(item.Beat, session.SnapDivision).EndBeat))));
}

private void CopyMixedSelection()
{
    if (TryExecuteTrackCommand("Copy") || viewModel.ActiveTab == null)
    {
        return;
    }

    try
    {
        MixedSelectionClipboard mixedSelectionClipboard = viewModel.CopyMixedSelection(timeline.SelectedTimingIds);
        WriteNoteClipboard(viewModel.ActiveTab.Session.Document, mixedSelectionClipboard.SnapshotNotes());
        UpdateEditorAvailability();
    }
    catch (InvalidOperationException ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("复制失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private void CutMixedSelection()
{
    if (TryExecuteTrackCommand("Cut") || viewModel.ActiveTab == null)
    {
        return;
    }

    try
    {
        EditorSession session = viewModel.ActiveTab.Session;
        session.Selection.UnionWith(CenterSymmetryService.ExpandDeletion(session, session.Selection, timeline.SymmetrySettings));
        MixedSelectionClipboard mixedSelectionClipboard = viewModel.CopyMixedSelection(timeline.SelectedTimingIds);
        WriteNoteClipboard(viewModel.ActiveTab.Session.Document, mixedSelectionClipboard.SnapshotNotes());
        if (!mixedSelectionClipboard.IsEmpty && viewModel.DeleteMixedSelection(timeline.SelectedTimingIds, timeline.SymmetrySettings).Applied)
        {
            timeline.SetSelectedTiming(null);
            RefreshTimingList(Array.Empty<Guid>(), force: true);
            UpdateSelectionUi();
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已剪切 {0:N0} 个 Note、{1:N0} 个 Effect", mixedSelectionClipboard.NoteCount, mixedSelectionClipboard.EffectCount));
        }
    }
    catch (InvalidOperationException ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("剪切失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private void SelectAllVisibleObjects()
{
    if (TryExecuteTrackCommand("SelectAll"))
    {
        return;
    }

    DesktopChartTab tab = viewModel.ActiveTab;
    if (tab != null)
    {
        tab.Session.Selection.Clear();
        tab.Session.Selection.UnionWith(
            from note in tab.Session.Document.Notes
            where !tab.HiddenGroups.Contains(note.Group) && (timeline.ProEditorEnabled || !note.IsDraft)select note.Id);
        Guid[] array = (
            from timing in tab.Session.Document.Timing
            where timing.Effect.HasValue
            select timing.Id).ToArray();
        timeline.SetSelectedTimings(array);
        SyncTimingListSelection(array);
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已全选 {0:N0} 个可见 Note、{1:N0} 个 Effect", tab.Session.Selection.Count, array.Length));
        UpdateSelectionUi();
    }
}

private void ExecuteHistoryCommand(bool redo)
{
    if (!TryExecuteTrackCommand(redo ? "Redo" : "Undo"))
    {
        DesktopChartTab activeTab = viewModel.ActiveTab;
        if (activeTab == null)
        {
            viewModel.SetStatus(redo ? "没有可重做操作" : "没有可撤销操作");
            return;
        }

        WindowsEditorHistoryOutcome windowsEditorHistoryOutcome = WindowsEditorHistoryCommand.Execute(activeTab.Session, IsBoundOnlineRoomChart(activeTab), redo);
        viewModel.SetStatus(windowsEditorHistoryOutcome.Status);
    }
}

private void MirrorVisibleSelection()
{
    if (TryExecuteTrackCommand("Mirror"))
    {
        return;
    }

    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null)
    {
        return;
    }

    HashSet<Guid> hashSet = WindowsGroupManagerCommand.VisibleEditableSelectionIds(activeTab);
    if (hashSet.Count == 0)
    {
        viewModel.SetStatus("请先选择一个或多个可见 Note");
        return;
    }

    try
    {
        int num = NoteMirrorEditingService.MirrorSelection(activeTab.Session, hashSet);
        viewModel.SetStatus((num > 0) ? AvaloniaLocalizationCatalog.Format("已水平镜像 {0} 个可见 Note（单次撤销）", num) : "选中的 Note 位于镜像轴上，无需移动");
        UpdateSelectionUi();
    }
    catch (InvalidOperationException ex)
    {
        viewModel.SetStatus(ex.Message);
        timeline.InvalidateVisual();
    }
}

private void RandomizeVisibleSelection(bool perBeat)
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null)
    {
        return;
    }

    HashSet<Guid> hashSet = WindowsGroupManagerCommand.VisibleEditableSelectionIds(activeTab);
    if (hashSet.Count == 0)
    {
        viewModel.SetStatus("请先选择一个或多个可见 Note");
        return;
    }

    try
    {
        NoteLaneRandomizationMode mode = (perBeat ? NoteLaneRandomizationMode.PerBeatPermutation : NoteLaneRandomizationMode.FixedPermutation);
        NoteLaneRandomizationResult noteLaneRandomizationResult = NoteLaneRandomizationService.RandomizeSelection(activeTab.Session, hashSet, mode);
        MainWindowViewModel mainWindowViewModel = viewModel;
        string status;
        if (noteLaneRandomizationResult.ChangedCount <= 0)
        {
            if (noteLaneRandomizationResult.CollisionBlocked)
            {
                status = "没有找到不与未选中 Note / LN 重叠的安全排列，谱面未修改";
            }
            else
            {
                status = ((noteLaneRandomizationResult.SelectedCount == 0) ? "选区中没有可随机的正式轨道 Note" : "当前轨道数无法产生不同的随机排列");
            }
        }
        else
        {
            status = (perBeat ? AvaloniaLocalizationCatalog.Format("已完全随机 {0} 个 Note（{1} 排分别打乱 · 单次撤销）", noteLaneRandomizationResult.ChangedCount, noteLaneRandomizationResult.RowCount) : AvaloniaLocalizationCatalog.Format("已随机排列 {0} 个 Note（固定轨道映射 · 单次撤销）", noteLaneRandomizationResult.ChangedCount));
        }

        mainWindowViewModel.SetStatus(status);
        UpdateSelectionUi();
    }
    catch (InvalidOperationException ex)
    {
        viewModel.SetStatus(ex.Message);
        timeline.InvalidateVisual();
    }
}

private void BeginQuickEffectEdit()
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null)
    {
        return;
    }

    if (activeTab.Session.IsReadOnly || !activeTab.Session.CanEditTiming)
    {
        viewModel.SetStatus("当前没有编辑 Timing / Effect 的权限");
        return;
    }

    if (!inspectorPanel.IsVisible)
    {
        inspectorPanel.IsVisible = true;
        editorWorkbench.ColumnDefinitions[4].Width = new GridLength(350.0);
        inspectorToggleButton.Content = "−";
        ToolTip.SetTip(inspectorToggleButton, "收起右侧面板");
    }

    editorInspectorTabs.SelectedItem = timingInspectorTab;
    timeline.SetSelectedTiming(null);
    SyncTimingListSelection(Array.Empty<Guid>());
    WindowsTimingEffectMemorySnapshot current = timingEffectMemory.Current;
    timingBeatBox.Text = BeatPosition.FromDouble(timeline.CurrentBeat).Snap(activeTab.Session.SnapDivision).ToString();
    timingBpmBox.Text = string.Empty;
    timingDelayBox.Text = "0";
    string rememberedEffectValue = current.Effect.ToString();
    timingEffectBox.SelectedItem = WindowsInspectorParityPolicy.Wpf110TimingEffectOptions.FirstOrDefault((WindowsTimingEffectOption option) => option.Value.Equals(rememberedEffectValue, StringComparison.Ordinal)) ?? timingEffectBox.SelectedItem;
    timingValueBox.Text = current.ValueText;
    timingCustomFieldBox.Text = current.CustomFieldName;
    timingValueBox.Focus();
    timingValueBox.SelectAll();
    viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("添加 {0} Effect · Beat {1} · 继承值 {2}", EffectNumberText.CanonicalName(current.Effect, current.CustomFieldName), timingBeatBox.Text, current.ValueText));
}

private void SetTimelineToolFromShortcut(TimelineTool tool)
{
    if (SymmetryTargetSurface.IsStretching)
    {
        SymmetryTargetSurface.CancelInteraction();
    }

    if (!TryExecuteTrackCommand("Tool" + tool))
    {
        if (!WindowsAvaloniaEditorInputPolicy.CanUseTimelineTool(viewModel.ActiveTab?.Session, tool))
        {
            viewModel.SetStatus("当前权限不能使用该编辑工具");
            return;
        }

        timeline.Tool = tool;
        UpdateTimelineToolButtons();
        timeline.Focus();
    }
}

private void NudgeSelectionOrRedLine(int direction)
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null)
    {
        return;
    }

    HashSet<Guid> hashSet = WindowsGroupManagerCommand.VisibleEditableSelectionIds(activeTab.Session, activeTab.HiddenGroups);
    if (hashSet.Count == 0)
    {
        timeline.SetCurrentBeat(Math.Max(0.0, timeline.CurrentBeat + (double)direction / (double)Math.Clamp(activeTab.Session.SnapDivision, 1, 1920)));
        return;
    }

    try
    {
        int num = BasicChartEditingService.NudgeSelection(activeTab.Session, direction, hashSet);
        viewModel.SetStatus((num > 0) ? AvaloniaLocalizationCatalog.Format("已按当前 1/{0} 分度{1}移动 {2} 个 Note", activeTab.Session.SnapDivision, (direction > 0) ? AvaloniaLocalizationCatalog.Text("向前") : AvaloniaLocalizationCatalog.Text("向后"), num) : "Note 已到达谱面起点，不能继续向后移动");
        UpdateSelectionUi();
    }
    catch (InvalidOperationException ex)
    {
        viewModel.SetStatus(ex.Message);
        timeline.InvalidateVisual();
    }
}

private async Task ConvertLongNotesToTapsAsync()
{
    DesktopChartTab tab = viewModel.ActiveTab;
    if (tab == null)
    {
        return;
    }

    EditorSession session = tab.Session;
    if (session.IsReadOnly)
    {
        viewModel.SetStatus("当前谱面为只读状态，不能转换 Note 类型");
        return;
    }

    if (!session.CanEditNotes)
    {
        viewModel.SetStatus("当前没有编辑 Note 的权限");
        return;
    }

    int num = WindowsNoteTypeTransformCommand.CountSelectedLongNotes(session, tab.HiddenGroups);
    if (num == 0)
    {
        viewModel.SetStatus("当前选区中没有可转换的面");
        return;
    }

    LongNoteToTapMode? longNoteToTapMode = await new NoteTypeTransformDialog(num, session.SnapDivision, displayScaleSettings.UiScale, displayScaleSettings.FontScale).ShowDialog<LongNoteToTapMode?>(this);
    if (!longNoteToTapMode.HasValue)
    {
        return;
    }

    try
    {
        NoteTransformResult noteTransformResult = WindowsNoteTypeTransformCommand.ConvertLongNotesToTaps(session, longNoteToTapMode.Value, tab.HiddenGroups);
        ApplyNoteTransformSelection();
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已转换 {0} 个面，新增 {1} 个米{2}", noteTransformResult.ConvertedCount, noteTransformResult.CreatedCount, (noteTransformResult.SkippedCount > 0) ? AvaloniaLocalizationCatalog.Format(AvaloniaLocalizationCatalog.Text("，跳过 {0} 个重叠位置"), noteTransformResult.SkippedCount) : string.Empty));
    }
    catch (InvalidOperationException ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("面转米失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private void ConvertTapsToLongNotes()
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null)
    {
        return;
    }

    EditorSession session = activeTab.Session;
    if (session.IsReadOnly)
    {
        viewModel.SetStatus("当前谱面为只读状态，不能转换 Note 类型");
        return;
    }

    if (!session.CanEditNotes)
    {
        viewModel.SetStatus("当前没有编辑 Note 的权限");
        return;
    }

    if (WindowsNoteTypeTransformCommand.CountSelectedTaps(session, activeTab.HiddenGroups) == 0)
    {
        viewModel.SetStatus("当前选区中没有可转换的米");
        return;
    }

    try
    {
        NoteTransformResult noteTransformResult = WindowsNoteTypeTransformCommand.ConvertTapsToLongNotes(session, activeTab.HiddenGroups);
        ApplyNoteTransformSelection();
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已将 {0} 个米转换为面{1}", noteTransformResult.ConvertedCount, (noteTransformResult.SkippedCount > 0) ? AvaloniaLocalizationCatalog.Format(AvaloniaLocalizationCatalog.Text("；{0} 个没有足够的同列后继空间"), noteTransformResult.SkippedCount) : string.Empty));
    }
    catch (InvalidOperationException ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("米转面失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private void ApplyNoteTransformSelection()
{
    timeline.SetSelectedTiming(null);
    SyncTimingListSelection(Array.Empty<Guid>());
    UpdateSelectionUi();
}

private void CopyTimingSelection()
{
    if (viewModel.ActiveTab == null)
    {
        return;
    }

    try
    {
        HashSet<Guid> hashSet = SelectedTimingListIds();
        if (hashSet.Count == 0 && timingList.SelectedItem is TimingListItem timingListItem)
        {
            hashSet.Add(timingListItem.Id);
        }

        if (hashSet.Count == 0)
        {
            hashSet.UnionWith(timeline.SelectedTimingIds);
        }

        viewModel.CopyTimingSelection(hashSet);
        UpdateEditorAvailability();
    }
    catch (InvalidOperationException ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("复制 Effect 失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private void PasteMixedSelection()
{
    PasteNoteClipboardAsync();
}

private void PasteInternalSelection()
{
    if (TryExecuteTrackCommand("Paste"))
    {
        return;
    }

    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null)
    {
        return;
    }

    try
    {
        BeatPosition redLineBeat = BeatPosition.FromDouble(timeline.CurrentBeat).Snap(activeTab.Session.SnapDivision);
        MixedSelectionPasteResult mixedSelectionPasteResult = viewModel.PasteMixedSelection(redLineBeat);
        if (mixedSelectionPasteResult.Applied)
        {
            timeline.SetSelectedTimings(mixedSelectionPasteResult.TimingIds);
            RefreshTimingList(mixedSelectionPasteResult.TimingIds, force: true);
            UpdateSelectionUi();
        }
    }
    catch (InvalidOperationException ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("粘贴失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private void PasteTimingSelection()
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null)
    {
        return;
    }

    try
    {
        BeatPosition redLineBeat = BeatPosition.FromDouble(timeline.CurrentBeat).Snap(activeTab.Session.SnapDivision);
        MixedSelectionPasteResult mixedSelectionPasteResult = viewModel.PasteTimingSelection(redLineBeat);
        if (mixedSelectionPasteResult.Applied)
        {
            timeline.SetSelectedTimings(mixedSelectionPasteResult.TimingIds);
            RefreshTimingList(mixedSelectionPasteResult.TimingIds, force: true);
            UpdateSelectionUi();
        }
    }
    catch (InvalidOperationException ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("粘贴 Effect 失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private void DeleteMixedSelection()
{
    if (TryExecuteTrackCommand("Delete") || viewModel.ActiveTab == null)
    {
        return;
    }

    try
    {
        if (viewModel.DeleteMixedSelection(timeline.SelectedTimingIds, timeline.SymmetrySettings).Applied)
        {
            timeline.SetSelectedTiming(null);
            RefreshTimingList(Array.Empty<Guid>(), force: true);
            UpdateSelectionUi();
        }
    }
    catch (InvalidOperationException ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("删除选择失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private void DeleteTimingBatch(IEnumerable<Guid> timingIds)
{
    if (viewModel.ActiveTab == null)
    {
        return;
    }

    try
    {
        TimingBatchDeleteResult timingBatchDeleteResult = viewModel.DeleteTimings(timingIds);
        if (timingBatchDeleteResult.Applied)
        {
            Guid[] array = timeline.SelectedTimingIds.Except(timingBatchDeleteResult.DeletedTimingIds).ToArray();
            timeline.SetSelectedTimings(array);
            RefreshTimingList(array, force: true);
            UpdateSelectionUi();
        }
    }
    catch (InvalidOperationException ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("删除 Timing / Effect 失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private bool IsTextEditingFocused()
{
    return WindowsAvaloniaEditorInputPolicy.IsTextEditingControl(FocusManager?.GetFocusedElement() as Control);
}

private bool IsTextEditingEvent(KeyEventArgs eventArgs)
{
    if (!WindowsAvaloniaEditorInputPolicy.IsTextEditingControl(eventArgs.Source as Control))
    {
        return IsTextEditingFocused();
    }

    return true;
}

private static bool IsTextEditingShortcut(Key key, KeyModifiers modifiers)
{
    return WindowsAvaloniaEditorInputPolicy.IsTextEditingShortcut(key, modifiers);
}

private void RefreshTimingList(Guid? selectTimingId = null, bool force = false)
{
    object selectTimingIds;
    if (!selectTimingId.HasValue)
    {
        selectTimingIds = null;
    }
    else
    {
        IEnumerable<Guid> enumerable = new Guid[]
{
    selectTimingId.Value
};
        selectTimingIds = enumerable;
    }

    RefreshTimingList((IEnumerable<Guid>? )selectTimingIds, force);
}

private void RefreshTimingList(IEnumerable<Guid>? selectTimingIds, bool force = false)
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null)
    {
        return;
    }

    HashSet<Guid> selectedIds = selectTimingIds?.ToHashSet() ?? timeline.SelectedTimingIds.ToHashSet();
    if (!force)
    {
        Guid? guid = timingListTabId;
        Guid id = activeTab.Id;
        if (guid.HasValue && guid.GetValueOrDefault() == id && timingListChangeSerial == activeTab.Session.ChangeSerial)
        {
            return;
        }
    }

    TimingMap nativeTimingMap = (OsuSourceEnvelope.HasSource(activeTab.Session.Document) ? new TimingMap(activeTab.Session.Document.Timing) : null);
    TimingListItem[] array = (
        from entry in activeTab.Session.Document.Timing.Where((TimingEvent timing) => !osuMinimalMode || timing.Bpm.HasValue).Select((TimingEvent timing, int index) => new { Timing = timing, Index = index })orderby entry.Timing.Beat, (!entry.Timing.Bpm.HasValue) ? 1 : 0, entry.Index
        select (nativeTimingMap == null) ? CreateTimingListItem(entry.Timing) : CreateNativeTimingListItem(entry.Timing, nativeTimingMap)).ToArray();
    suppressTimingSelection = true;
    try
    {
        timingList.ItemsSource = array;
        AutomationProperties.SetName(timingList, AvaloniaLocalizationCatalog.Format("Timing 列表 · {0:N0} 项", array.Length));
        SyncTimingListSelection(selectedIds, array.FirstOrDefault((TimingListItem item) => selectedIds.Contains(item.Id))?.Id);
    }
    finally
    {
        suppressTimingSelection = false;
    }

    timingListTabId = activeTab.Id;
    timingListChangeSerial = activeTab.Session.ChangeSerial;
    timeline.SetSelectedTimings(
        from item in array
        where selectedIds.Contains(item.Id)select item.Id, (timingList.SelectedItem is TimingListItem timingListItem) ? new Guid? (timingListItem.Id) : ((Guid? )null));
    if (timingList.SelectedItem is TimingListItem timingListItem2)
    {
        PopulateTimingFields(timingListItem2.Id, force);
    }
}

private static TimingListItem CreateTimingListItem(TimingEvent timing)
{
    string text = timing.Delay.ToString("0.###", CultureInfo.InvariantCulture);
    string main = (timing.Bpm.HasValue ? (timing.Bpm.Value.ToString("0.###", CultureInfo.InvariantCulture) + " BPM  ·  " + text + " ms") : ("Delay " + text + " ms"));
    string effect = "—";
    if (timing.Effect.HasValue)
    {
        string text2 = EffectNumberText.CanonicalName(timing.Effect.Value, timing.EffectFieldName);
        string text3 = (string.IsNullOrWhiteSpace(timing.EffectValueText) ? timing.EffectValue.ToString("R", CultureInfo.InvariantCulture) : timing.EffectValueText);
        effect = text2 + "  " + text3;
    }

    return new TimingListItem(timing.Id, timing.Beat.ToString(), main, effect);
}

private void MarkTimingEditorDirty()
{
    if (!suppressTimingEditorControls)
    {
        timingEditorDirty = true;
    }
}

private void PopulateTimingFields(Guid timingId, bool force = false)
{
    if (!WindowsInspectorParityPolicy.ShouldPopulateTimingFields(force, timingEditorDirty, timingEditorTimingId, timingId))
    {
        return;
    }

    TimingEvent timingEvent = viewModel.ActiveTab?.Session.Document.Timing.FirstOrDefault((TimingEvent candidate) => candidate.Id == timingId);
    if (timingEvent == null)
    {
        return;
    }

    suppressTimingEditorControls = true;
    try
    {
        timingBeatBox.Text = timingEvent.Beat.ToString();
        timingBpmBox.Text = timingEvent.Bpm?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;
        timingDelayBox.Text = timingEvent.Delay.ToString("0.###", CultureInfo.InvariantCulture);
        string effectValue = timingEvent.Effect?.ToString() ?? "None";
        timingEffectBox.SelectedItem = WindowsInspectorParityPolicy.Wpf110TimingEffectOptions.First((WindowsTimingEffectOption option) => option.Value.Equals(effectValue, StringComparison.Ordinal));
        timingValueBox.Text = (string.IsNullOrWhiteSpace(timingEvent.EffectValueText) ? timingEvent.EffectValue.ToString("R", CultureInfo.InvariantCulture) : timingEvent.EffectValueText);
        timingCustomFieldBox.Text = timingEvent.EffectFieldName;
    }
    finally
    {
        suppressTimingEditorControls = false;
    }

    timingEditorTimingId = timingId;
    timingEditorDirty = false;
}

private CompleteTimingEdit ReadTimingFields()
{
    if (!BeatPosition.TryParse(timingBeatBox.Text, out var position) || position.CompareTo(BeatPosition.Zero) < 0)
    {
        throw new FormatException("Beat 格式应为 1:0/1 或十进制数，且不能为负数。");
    }

    if (!TryTimingNumber(timingDelayBox.Text, out var value) || !double.IsFinite(value))
    {
        throw new FormatException("Delay 必须是有限毫秒数。");
    }

    double? bpm = null;
    if (!string.IsNullOrWhiteSpace(timingBpmBox.Text))
    {
        if (!TryTimingNumber(timingBpmBox.Text, out var value2))
        {
            throw new FormatException("BPM 必须是有效数字。");
        }

        bpm = value2;
    }

    TimingEffectKind? effect = null;
    string text = (timingEffectBox.SelectedItem as WindowsTimingEffectOption)?.Value;
    if (!string.Equals(text, "None", StringComparison.Ordinal) && Enum.TryParse<TimingEffectKind>(text, out var result))
    {
        effect = result;
    }

    return new CompleteTimingEdit(position, bpm, value, effect, timingValueBox.Text ?? string.Empty, timingCustomFieldBox.Text ?? string.Empty);
}

private static bool TryTimingNumber(string? text, out double value)
{
    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    return true;
}

private void AddTimingFromFields()
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null)
    {
        return;
    }

    timingBeatBox.Text = BeatPosition.FromDouble(timeline.CurrentBeat).Snap(activeTab.Session.SnapDivision).ToString();
    try
    {
        CompleteTimingEdit edit = ReadTimingFields();
        Guid guid = viewModel.AddTiming(edit);
        if (guid != Guid.Empty)
        {
            timingEffectMemory.Remember(edit);
            RefreshTimingList(guid, force: true);
            UpdateSelectionUi();
        }
    }
    catch (Exception ex)when ((ex is FormatException || ex is ArgumentException || ex is InvalidOperationException) ? true : false)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("新增 Timing / Effect 失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private void ApplyTimingFromFields()
{
    if (!(timingList.SelectedItem is TimingListItem timingListItem))
    {
        viewModel.SetStatus("请先选择一个 Timing / Effect 条目");
        return;
    }

    try
    {
        CompleteTimingEdit edit = ReadTimingFields();
        if (viewModel.UpdateTiming(timingListItem.Id, edit))
        {
            timingEffectMemory.Remember(edit);
            RefreshTimingList(timingListItem.Id, force: true);
        }
    }
    catch (Exception ex)when ((ex is FormatException || ex is ArgumentException || ex is InvalidOperationException) ? true : false)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("应用 Timing / Effect 失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private void DeleteSelectedTiming()
{
    HashSet<Guid> hashSet = SelectedTimingListIds();
    if (hashSet.Count == 0 && timingList.SelectedItem is TimingListItem timingListItem)
    {
        hashSet.Add(timingListItem.Id);
    }

    DeleteTimingBatch(hashSet);
}

private void JumpToSelectedTiming()
{
    object selectedItem = timingList.SelectedItem;
    TimingListItem selected = selectedItem as TimingListItem;
    if ((object)selected != null)
    {
        TimingEvent timingEvent = viewModel.ActiveTab?.Session.Document.Timing.FirstOrDefault((TimingEvent candidate) => candidate.Id == selected.Id);
        if (timingEvent != null)
        {
            timeline.SetCurrentBeat(timingEvent.Beat.Value);
            viewModel.UpdateCurrentBeat(timingEvent.Beat.Value);
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已跳转到 {0}", timingEvent.Beat));
        }
    }
}

private void RebuildTabs()
{
    BeginDocumentTabRebuild();
    tabBar.Children.Clear();
    onlineLatencyVisuals.Clear();
    foreach (DesktopChartTab tab in viewModel.Tabs)
    {
        bool flag = tab == roomChartSyncTab;
        bool flag2 = retainedOnlineRoomTabIds.Contains(tab.Id);
        bool flag3 = flag && (onlineAccountHost?.Session.CurrentMemberRole.Equals("viewer", StringComparison.OrdinalIgnoreCase) ?? false);
        string text;
        if (flag)
        {
            text = (flag3 ? " · 观看" : " · 联机");
        }
        else
        {
            text = (flag2 ? " · 房间留存" : string.Empty);
        }

        WindowsDocumentTabPresentation windowsDocumentTabPresentation = WindowsTitleBarParityPolicy.ResolveDocumentTab(tab.Format, tab.DisplayTitle, tab.Session.IsDirty, tab == viewModel.ActiveTab, flag, flag2, flag3, (!flag) ? ((long? )null) : onlineAccountHost?.Session.CurrentRoom?.RoomNumber);
        AutomationBorder automationBorder = new AutomationBorder
        {
            Margin = WindowsTitleBarParityPolicy.FormatBadgeMargin,
            Padding = WindowsTitleBarParityPolicy.FormatBadgePadding,
            CornerRadius = new CornerRadius(2.0),
            Background = new SolidColorBrush(windowsDocumentTabPresentation.FormatBackground),
            BorderBrush = new SolidColorBrush(windowsDocumentTabPresentation.FormatBorder),
            BorderThickness = new Thickness(1.0),
            Child = new TextBlock
            {
                Text = (tab.IsLibraryReadOnly ? ("只读 · " + windowsDocumentTabPresentation.FormatBadge) : windowsDocumentTabPresentation.FormatBadge),
                FontSize = 8.0,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(windowsDocumentTabPresentation.FormatForeground),
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        AutomationProperties.SetAutomationId(automationBorder, "Editor.ChartTab.FormatBadge");
        AutomationProperties.SetName(automationBorder, windowsDocumentTabPresentation.FormatBadge);
        AutomationBorder automationBorder2 = null;
        if (windowsDocumentTabPresentation.LiveBadgeText != null)
        {
            WindowsOnlineLatencyPresentation windowsOnlineLatencyPresentation = WindowsTitleBarParityPolicy.ResolveOnlineLatency(onlineRoundTripLatencyMs);
            TextBlock textBlock = new TextBlock
            {
                Text = windowsOnlineLatencyPresentation.Text,
                FontSize = 8.0,
                FontFamily = new FontFamily("Cascadia Mono,Consolas"),
                Foreground = new SolidColorBrush(windowsOnlineLatencyPresentation.Foreground),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5.0, 0.0, 0.0, 0.0)
            };
            AutomationProperties.SetAutomationId(textBlock, "Editor.ChartTab.LiveLatency");
            AutomationProperties.SetName(textBlock, windowsOnlineLatencyPresentation.Text);
            automationBorder2 = new AutomationBorder
            {
                Margin = WindowsTitleBarParityPolicy.LiveBadgeMargin,
                Padding = WindowsTitleBarParityPolicy.LiveBadgePadding,
                CornerRadius = new CornerRadius(2.0),
                Background = new SolidColorBrush(Color.Parse("#314B4E")),
                BorderBrush = new SolidColorBrush(Color.Parse("#65979A")),
                BorderThickness = new Thickness(1.0),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        (Control)new TextBlock
                        {
                            Text = windowsDocumentTabPresentation.LiveBadgeText,
                            FontSize = 8.0,
                            FontWeight = FontWeight.Bold,
                            Foreground = new SolidColorBrush(Color.Parse("#DCEFEF")),
                            VerticalAlignment = VerticalAlignment.Center
                        },
                        (Control)new TextBlock
                        {
                            Text = windowsDocumentTabPresentation.LiveRoomNumberText,
                            FontSize = 8.0,
                            FontFamily = new FontFamily("Cascadia Mono,Consolas"),
                            FontWeight = FontWeight.DemiBold,
                            Foreground = new SolidColorBrush(Color.Parse("#A5CDCF")),
                            VerticalAlignment = VerticalAlignment.Center
                        },
                        (Control)textBlock
                    }
                }
            };
            AutomationProperties.SetAutomationId(automationBorder2, "Editor.ChartTab.LiveBadge");
            AutomationProperties.SetName(automationBorder2, windowsDocumentTabPresentation.LiveBadgeText + windowsDocumentTabPresentation.LiveRoomNumberText + " " + windowsOnlineLatencyPresentation.Text);
            AutomationProperties.SetHelpText(automationBorder2, windowsDocumentTabPresentation.LiveDescription + "；" + windowsOnlineLatencyPresentation.ToolTip);
            ToolTip.SetTip(automationBorder2, windowsOnlineLatencyPresentation.ToolTip);
            onlineLatencyVisuals.Add(new OnlineLatencyVisual(textBlock, automationBorder2, windowsDocumentTabPresentation.LiveBadgeText + windowsDocumentTabPresentation.LiveRoomNumberText));
        }

        AutomationBorder automationBorder3 = null;
        if (windowsDocumentTabPresentation.RetainedBadgeText != null)
        {
            automationBorder3 = new AutomationBorder
            {
                Margin = WindowsTitleBarParityPolicy.LiveBadgeMargin,
                Padding = WindowsTitleBarParityPolicy.LiveBadgePadding,
                CornerRadius = new CornerRadius(2.0),
                Background = new SolidColorBrush(Color.Parse("#4E372B")),
                BorderBrush = new SolidColorBrush(Color.Parse("#A6714F")),
                BorderThickness = new Thickness(1.0),
                Child = new TextBlock
                {
                    Text = windowsDocumentTabPresentation.RetainedBadgeText,
                    FontSize = 8.0,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#EFCDB1")),
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            AutomationProperties.SetAutomationId(automationBorder3, "Editor.ChartTab.RetainedBadge");
            AutomationProperties.SetName(automationBorder3, windowsDocumentTabPresentation.RetainedBadgeText);
            string value = ((tab.PreservedRoomNumber > 0) ? AvaloniaLocalizationCatalog.Format("房间 #{0} “{1}”的留存成果；保存时请另存为新的 MC / osu 单谱，重新加入不会覆盖此标签", tab.PreservedRoomNumber, tab.PreservedRoomName) : AvaloniaLocalizationCatalog.Format("房间关闭或被移出时保留的本地成果；保存时请另存为新的 MC / osu 单谱，重新加入不会覆盖此标签"));
            AutomationProperties.SetHelpText(automationBorder3, value);
            ToolTip.SetTip(automationBorder3, value);
        }

        Button button = new Button
        {
            Content = new TextBlock
            {
                Text = windowsDocumentTabPresentation.DisplayTitle,
                Foreground = new SolidColorBrush(windowsDocumentTabPresentation.TitleForeground),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            },
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(windowsDocumentTabPresentation.TitleForeground),
            BorderThickness = new Thickness(0.0),
            Padding = WindowsTitleBarParityPolicy.OpenButtonPadding,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            MinWidth = 88.0,
            MaxWidth = 190.0
        };
        AutomationProperties.SetAutomationId(button, "Editor.ChartTab");
        AutomationProperties.SetName(button, AvaloniaLocalizationCatalog.Format("谱面标签 {0} · {1}{2}", tab.DisplayTitle, System.IO.Path.GetFileName(tab.SourcePath), text));
        AutomationProperties.SetHelpText(button, AvaloniaLocalizationCatalog.Format("{0}\n未保存：{1}\n联机状态：{2}", System.IO.Path.GetFullPath(tab.SourcePath), tab.Session.IsDirty ? AvaloniaLocalizationCatalog.Text("是") : AvaloniaLocalizationCatalog.Text("否"), windowsDocumentTabPresentation.RoomStateDescription));
        button.Click += (object? _, RoutedEventArgs _) =>
        {
            DesktopChartTab activeTab = viewModel.ActiveTab;
            if (activeTab == null || activeTab == tab || TryApplyPendingMetadataEditor(activeTab, enforceCommunityRules: false))
            {
                viewModel.Activate(tab.Id);
                ShowEditorSurface();
            }
        };
        Button button2 = new Button
        {
            Content = new global::Avalonia.Controls.Shapes.Path
            {
                Width = 13.0,
                Height = 13.0,
                Data = Geometry.Parse("M2,2 L11,11 M11,2 L2,11"),
                Stroke = new SolidColorBrush(Color.Parse("#D8DEDF")),
                StrokeThickness = 1.6,
                StrokeLineCap = PenLineCap.Round,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Width = 25.0,
            Height = 24.0,
            Padding = new Thickness(5.0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0.0)
        };
        AutomationProperties.SetAutomationId(button2, "Editor.ChartTab.Close");
        AutomationProperties.SetName(button2, AvaloniaLocalizationCatalog.Format("关闭谱面标签 {0}", tab.DisplayTitle));
        ToolTip.SetTip(button2, AvaloniaLocalizationCatalog.Format("关闭 {0}", tab.DisplayTitle));
        button2.Click += async (object? _, RoutedEventArgs _) =>
        {
            await CloseTabAsync(tab);
        };
        StackPanel stackPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        stackPanel.Children.Add(automationBorder);
        if (automationBorder2 != null)
        {
            stackPanel.Children.Add(automationBorder2);
        }

        if (automationBorder3 != null)
        {
            stackPanel.Children.Add(automationBorder3);
        }

        stackPanel.Children.Add(button);
        stackPanel.Children.Add(button2);
        tabBar.Children.Add(new Border { Height = 28.0, Margin = WindowsTitleBarParityPolicy.DocumentTabMargin, Padding = WindowsTitleBarParityPolicy.DocumentTabPadding, Background = new SolidColorBrush(windowsDocumentTabPresentation.TabBackground), BorderBrush = new SolidColorBrush(windowsDocumentTabPresentation.TabBorder), BorderThickness = new Thickness(1.0), CornerRadius = new CornerRadius(3.0), Child = stackPanel });
    }

    UpdateOnlineLatencyLabels();
    CompleteDocumentTabRebuild();
}

private void UpdateOnlineLatencyLabels()
{
    WindowsOnlineLatencyPresentation windowsOnlineLatencyPresentation = WindowsTitleBarParityPolicy.ResolveOnlineLatency(onlineRoundTripLatencyMs);
    foreach (OnlineLatencyVisual onlineLatencyVisual in onlineLatencyVisuals)
    {
        onlineLatencyVisual.Label.Text = windowsOnlineLatencyPresentation.Text;
        onlineLatencyVisual.Label.Foreground = new SolidColorBrush(windowsOnlineLatencyPresentation.Foreground);
        AutomationProperties.SetName(onlineLatencyVisual.Label, windowsOnlineLatencyPresentation.Text);
        AutomationProperties.SetName(onlineLatencyVisual.Badge, onlineLatencyVisual.BadgePrefix + " " + windowsOnlineLatencyPresentation.Text);
    }
}

private async Task CheckActiveChartAsync()
{
    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab != null)
    {
        DesktopChartValidationPresentation desktopChartValidationPresentation = PresentChartValidation(activeTab);
        DesktopChartIssueNavigationPlan navigation = desktopChartValidationPresentation.Navigation;
        if ((object)navigation != null)
        {
            ApplyChartIssueNavigation(activeTab, navigation);
        }

        await ShowChartValidationDialogAsync(desktopChartValidationPresentation.ManualCheckMessage, (!desktopChartValidationPresentation.CanSave) ? ChartValidationDialogTone.Warning : ChartValidationDialogTone.Information, manual: true);
    }
}

private async Task<SaveRoundTripResult?> SaveAndVerifyWithPreflightAsync(Guid? tabId = null)
{
    try
    {
        await SaveTrackWorkspacesAsync();
    }
    catch (Exception ex)
    {
        viewModel.SetStatus("轨道保存失败：" + ex.Message);
        return null;
    }

    DesktopChartTab desktopChartTab = (tabId.HasValue ? viewModel.Tabs.FirstOrDefault((DesktopChartTab candidate) => candidate.Id == tabId.Value) : viewModel.ActiveTab);
    if (desktopChartTab == null)
    {
        return null;
    }

    if (saveInProgress)
    {
        if (!pendingFollowUpSaves.TryGetValue(desktopChartTab.Id, out TaskCompletionSource<SaveRoundTripResult> value))
        {
            value = new TaskCompletionSource<SaveRoundTripResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingFollowUpSaves[desktopChartTab.Id] = value;
        }

        return await value.Task;
    }

    Guid tabIdToSave = desktopChartTab.Id;
    return await RunExclusiveSaveAsync(() => SaveAndVerifyWithPreflightCoreAsync(tabIdToSave));
}

private async Task<SaveRoundTripResult?> RunExclusiveSaveAsync(Func<Task<SaveRoundTripResult?>> operation)
{
    while (saveInProgress)
    {
        await saveSlotReleased.Task;
    }

    saveInProgress = true;
    try
    {
        SaveRoundTripResult result;
        try
        {
            result = await operation();
        }
        finally
        {
            while (pendingFollowUpSaves.Count > 0)
            {
                var(guid2, followUp) = pendingFollowUpSaves.First();
                pendingFollowUpSaves.Remove(guid2);
                try
                {
                    TaskCompletionSource<SaveRoundTripResult> taskCompletionSource2 = followUp;
                    taskCompletionSource2.TrySetResult(await SaveAndVerifyWithPreflightCoreAsync(guid2));
                }
                catch (Exception exception)
                {
                    followUp.TrySetException(exception);
                }
            }
        }

        return result;
    }
    finally
    {
        saveInProgress = false;
        TaskCompletionSource taskCompletionSource3 = saveSlotReleased;
        saveSlotReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        taskCompletionSource3.TrySetResult();
    }
}

private async Task<SaveRoundTripResult?> SaveAndVerifyWithPreflightCoreAsync(Guid tabId)
{
    DesktopChartTab tab = viewModel.Tabs.FirstOrDefault((DesktopChartTab candidate) => candidate.Id == tabId);
    if (tab == null)
    {
        return null;
    }

    if (viewModel.ActiveTab != tab)
    {
        viewModel.Activate(tab.Id);
    }

    if (!ApplyMetadataEditor(enforceCommunityRules: true))
    {
        string text = lastMetadataApplyError;
        if (text != null && text.Length > 0)
        {
            await ShowSettingsMessageAsync("保存失败", AvaloniaLocalizationCatalog.Format("谱面信息未应用：{0}", AvaloniaLocalizationCatalog.Text(text)));
        }

        return null;
    }

    if (!(await EnsureChartCanSaveAsync(tab)))
    {
        return null;
    }

    if (retainedOnlineRoomTabIds.Contains(tab.Id))
    {
        return await PickAndSaveAsCoreAsync(tab.Id, preflightComplete: true);
    }

    try
    {
        return await viewModel.SaveAndVerifyAsync(tab.Id);
    }
    catch (Exception ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("保存谱面失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
        WindowsNativeMessageBox.Show(this, WindowsLibraryLauncherParityPolicy.CreateOperationError("无法保存原谱面；软件旁缓存已保留", ex));
        return null;
    }
}

private async Task<bool> EnsureChartCanSaveAsync(DesktopChartTab tab)
{
    DesktopChartValidationPresentation desktopChartValidationPresentation = PresentChartValidation(tab, tab.Session.CaptureCommittedDocument());
    if (desktopChartValidationPresentation.CanSave)
    {
        return true;
    }

    DesktopChartIssueNavigationPlan navigation = desktopChartValidationPresentation.Navigation;
    if ((object)navigation != null)
    {
        ApplyChartIssueNavigation(tab, navigation);
    }

    await ShowChartValidationDialogAsync(desktopChartValidationPresentation.SaveBlockingMessage ?? string.Empty, ChartValidationDialogTone.Warning, manual: false);
    return false;
}

private DesktopChartValidationPresentation PresentChartValidation(DesktopChartTab tab, ChartDocument? document = null)
{
    DesktopChartValidationPresentation result = (lastChartValidationPresentation = DesktopChartValidationPresenter.Create(document ?? tab.Session.Document, tab.HiddenGroups));
    timeline.InvalidateVisual();
    return result;
}

private void ApplyChartIssueNavigation(DesktopChartTab tab, DesktopChartIssueNavigationPlan navigation)
{
    if (viewModel.ActiveTab != tab)
    {
        viewModel.Activate(tab.Id);
    }

    foreach (int item in navigation.HiddenGroupsToReveal)
    {
        tab.HiddenGroups.Remove(item);
    }

    timeline.SetHiddenGroups(tab.HiddenGroups);
    tab.Session.Selection.Clear();
    timeline.SetSelectedTiming(null);
    HashSet<Guid> hashSet = tab.Session.Document.Notes.Select((ChartNote note) => note.Id).ToHashSet();
    tab.Session.Selection.UnionWith(navigation.NoteIds.Where(hashSet.Contains));
    timeline.SetCurrentBeat(navigation.ExactBeat.Value);
    viewModel.UpdateCurrentBeat(navigation.ExactBeat.Value);
    RefreshGroupPanel();
    UpdateNoteInspector(tab);
    editorInspectorTabs.SelectedItem = noteInspectorTab;
    UpdateSelectionUi();
    viewModel.SetStatus(navigation.HoverStatusText);
    timeline.InvalidateVisual();
    lastChartValidationNavigation = navigation;
}

private async Task ShowChartValidationDialogAsync(string message, ChartValidationDialogTone tone, bool manual)
{
    ChartValidationDialog chartValidationDialog = new ChartValidationDialog(message, tone, displayScaleSettings.UiScale, displayScaleSettings.FontScale);
    if (manual)
    {
        chartValidationManualCheckShown = true;
        chartValidationManualDialogScale = chartValidationDialog.ScaleDiagnostics;
        lastChartValidationDialogMode = ((tone == ChartValidationDialogTone.Information) ? "manual-information" : "manual-warning");
    }
    else
    {
        chartValidationSaveBlockedShown = true;
        chartValidationSaveDialogScale = chartValidationDialog.ScaleDiagnostics;
        lastChartValidationDialogMode = "save-warning";
    }

    await chartValidationDialog.ShowDialog<bool>(this);
}

private async Task CloseTabAsync(DesktopChartTab tab)
{
    if (!closingTabIds.Add(tab.Id))
    {
        return;
    }

    try
    {
        await SaveTrackWorkspacesAsync();
        await CloseTabWithPromptsAsync(tab);
    }
    catch (Exception ex)
    {
        if (!windowCloseTeardownStarted)
        {
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("关闭谱面标签失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
            WindowsNativeMessageBox.Show(this, WindowsLibraryLauncherParityPolicy.CreateOperationError("无法关闭谱面标签", ex));
        }
    }
    finally
    {
        closingTabIds.Remove(tab.Id);
    }
}

private async Task CloseTabWithPromptsAsync(DesktopChartTab tab)
{
    if (!TryApplyPendingMetadataEditor(tab, enforceCommunityRules: false))
    {
        return;
    }

    if (retainedOnlineRoomTabIds.Contains(tab.Id))
    {
        switch (await new PreservedRoomDraftCloseDialog(tab.DisplayTitle, displayScaleSettings.UiScale, displayScaleSettings.FontScale).ShowDialog<PreservedRoomDraftCloseChoice>(this))
        {
            case PreservedRoomDraftCloseChoice.SaveAs:
                if (((await PickAndSaveAsAsync(tab.Id))?.StructurallyEqual ?? false) && !tab.Session.IsDirty)
                {
                    await CloseTabCoreAsync(tab, discardUnsavedChanges: false);
                }

                break;
            case PreservedRoomDraftCloseChoice.Discard:
                await CloseTabCoreAsync(tab, discardUnsavedChanges: true);
                break;
        }

        return;
    }

    if (!tab.Session.IsDirty)
    {
        await CloseTabCoreAsync(tab, discardUnsavedChanges: false);
        return;
    }

    switch (await new UnsavedChangesDialog(tab.DisplayTitle, applicationClosing: false, displayScaleSettings.UiScale, displayScaleSettings.FontScale).ShowDialog<UnsavedChangesChoice>(this))
    {
        case UnsavedChangesChoice.Save:
            if (((await SaveAndVerifyWithPreflightAsync(tab.Id))?.StructurallyEqual ?? false) && !tab.Session.IsDirty)
            {
                await CloseTabCoreAsync(tab, discardUnsavedChanges: false);
            }

            break;
        case UnsavedChangesChoice.Discard:
            await CloseTabCoreAsync(tab, discardUnsavedChanges: true);
            break;
    }
}

private async Task<bool> CloseTabCoreAsync(DesktopChartTab tab, bool discardUnsavedChanges)
{
    if (!(await ConfirmOnlineRoomExitAsync(tab)))
    {
        return false;
    }

    DesktopChartCloseResult desktopChartCloseResult = await viewModel.CloseTabAsync(tab.Id, discardUnsavedChanges);
    if (desktopChartCloseResult.Status == DesktopChartCloseStatus.PersistFailed)
    {
        WindowsNativeMessageBox.Show(this, WindowsLibraryLauncherParityPolicy.CreateOperationError("无法关闭谱面标签", desktopChartCloseResult.Error ?? new IOException(viewModel.Status)));
        return false;
    }

    if (!desktopChartCloseResult.Closed)
    {
        return false;
    }

    await OnChartTabClosedAsync(tab);
    return true;
}

private async Task<bool> ConfirmOnlineRoomExitAsync(DesktopChartTab? closingTab = null)
{
    if (closingTab != null && roomChartSyncTab != closingTab)
    {
        return true;
    }

    OnlineAccountSession onlineAccountSession = onlineAccountHost?.Session;
    OnlineRoomSummaryDto onlineRoomSummaryDto = onlineAccountSession?.CurrentRoom;
    if (onlineRoomSummaryDto == null)
    {
        return true;
    }

    bool isOwner = onlineAccountSession?.CurrentUser != null && onlineRoomSummaryDto.OwnerUserId == onlineAccountSession.CurrentUser.Id;
    return await new OnlineRoomExitConfirmationDialog(onlineRoomSummaryDto.Name, isOwner, displayScaleSettings.UiScale, displayScaleSettings.FontScale).ShowDialog<OnlineRoomExitChoice>(this) == OnlineRoomExitChoice.Confirm;
}

private async Task OnChartTabClosedAsync(DesktopChartTab tab)
{
    difficultyStates.Remove(tab.Id);
    retainedOnlineRoomTabIds.Remove(tab.Id);
    difficultyAnalysisCancellation?.Cancel();
    if (roomChartSyncTab != tab)
    {
        return;
    }

    await StopRoomChartSyncAsync(WindowsOnlineRoomRetentionPolicy.ShouldPreserveDraftOnConfirmedExit(WindowsOnlineRoomExitContext.ExplicitTabClose));
    if (!(onlineAccountHost?.Session.CurrentRoom != null))
    {
        return;
    }

    try
    {
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15.0));
        await onlineAccountHost.Session.LeaveCurrentRoomAsync(timeout.Token);
    }
    catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is WebException || ex is OperationCanceledException || ex is JsonException) ? true : false)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("联机标签已关闭；退出房间请求未确认：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private async Task CreateChartAsync()
{
    string creator = ((!(onlineAccountHost?.Session.IsAuthenticated ?? false)) ? null : onlineAccountHost.Session.CurrentUser?.Username);
    try
    {
        await viewModel.CreateAsync(null, creator);
        DesktopChartTab activeTab = viewModel.ActiveTab;
        if (activeTab != null)
        {
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已在工作目录创建 {0}，可直接开始编辑", activeTab.DisplayTitle));
        }

        ShowEditorSurface();
    }
    catch (Exception ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("新建谱面失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
        WindowsNativeMessageBox.Show(this, WindowsLibraryLauncherParityPolicy.CreateOperationError("无法创建新项目", ex));
    }
}

private async Task CreateChartFromAudioAsync()
{
    string text;
    try
    {
        text = await PickLocalFileAsync(new FilePickerOpenOptions { Title = "选择要导入曲库的音频", AllowMultiple = false, FileTypeFilter = new FilePickerFileType[]
{
    new FilePickerFileType("音频文件")
    {
        Patterns = new string[6] { "*.mp3", "*.ogg", "*.wav", "*.flac", "*.m4a", "*.aac" },
        MimeTypes = new string[]
{
    "audio/*"
}
    }
} }, PickedFileRetention.Transient);
    }
    catch (Exception ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("无法从音频创建谱面：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
        return;
    }

    if (!string.IsNullOrWhiteSpace(text))
    {
        await CreateChartFromAudioPathAsync(System.IO.Path.GetFullPath(text));
    }
}

private async Task CreateChartFromAudioPathAsync(string selectedPath)
{
    if (audioChartCreationInProgress)
    {
        viewModel.SetStatus("音频导入进行中，请稍后再试。");
        return;
    }

    audioChartCreationInProgress = true;
    try
    {
        await CreateChartFromAudioPathCoreAsync(selectedPath);
    }
    finally
    {
        audioChartCreationInProgress = false;
    }
}

private async Task CreateChartFromAudioPathCoreAsync(string selectedPath)
{
    if (!File.Exists(selectedPath) || !WindowsFileDropPolicy.IsAudioPath(selectedPath))
    {
        WindowsNativeMessageBox.Show(this, new WindowsNativeMessageBoxRequest("请选择受支持的音频文件。", "导入音频", WindowsNativeMessageBoxIcon.Warning));
        return;
    }

    bool firstPrompt = true;
    DesktopNamedAudioChartCreation creation;
    while (true)
    {
        string text = await ShowAudioImportNamePromptAsync(selectedPath, firstPrompt);
        firstPrompt = false;
        if (text == null)
        {
            return;
        }

        if (!File.Exists(selectedPath))
        {
            audioImportNameStatusText.Text = "音频文件已不存在，请重新选择。";
            audioImportNameStatusText.Foreground = Brushes.OrangeRed;
            if (audioImportNameConfirmButton != null)
            {
                audioImportNameConfirmButton.IsEnabled = true;
            }

            continue;
        }

        try
        {
            string creator = ((!(onlineAccountHost?.Session.IsAuthenticated ?? false)) ? null : onlineAccountHost.Session.CurrentUser?.Username);
            creation = await viewModel.CreateNamedAudioChartAsync(text, creator);
            HideAudioImportNamePrompt();
            ShowEditorSurface();
        }
        catch (Exception ex)
        {
            audioImportNameStatusText.Text = ex.Message;
            audioImportNameStatusText.Foreground = Brushes.OrangeRed;
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("无法从音频创建谱面：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
            continue;
        }

        break;
    }

    if (!activeAudioImportTask.IsCompleted)
    {
        await activeAudioImportTask;
    }

    WindowsChartAudioImportStatus importStatus = await (Task<WindowsChartAudioImportStatus>)(activeAudioImportTask = ImportChartAudioPathCoreAsync(creation.Tab, selectedPath));
    viewModel.SetStatus(WindowsAudioLibraryCreationPolicy.CompletionStatus(creation.Name, importStatus));
}

private Task<string?> ShowAudioImportNamePromptAsync(string sourcePath, bool resetState)
{
    if (resetState)
    {
        audioImportSourceText.Text = System.IO.Path.GetFileName(sourcePath);
        audioImportNameBox.Text = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
        audioImportNameStatusText.Text = string.Empty;
        audioImportNameStatusText.Foreground = new SolidColorBrush(Color.Parse("#C58C79"));
    }

    audioImportNameConfirmButton?.SetCurrentValue(InputElement.IsEnabledProperty, value: true);
    audioImportNameCompletion?.TrySetResult(null);
    audioImportNameCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    audioImportNameLayer.IsVisible = true;
    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        audioImportNameBox.Focus();
        audioImportNameBox.SelectAll();
    }, DispatcherPriority.Input);
    return audioImportNameCompletion.Task;
}

private void CompleteAudioImportNamePrompt(bool confirm)
{
    TaskCompletionSource<string> taskCompletionSource = audioImportNameCompletion;
    if (taskCompletionSource == null)
    {
        return;
    }

    audioImportNameCompletion = null;
    if (!confirm)
    {
        audioImportNameLayer.IsVisible = false;
        taskCompletionSource.TrySetResult(null);
        return;
    }

    if (audioImportNameConfirmButton != null)
    {
        audioImportNameConfirmButton.IsEnabled = false;
    }

    audioImportNameStatusText.Text = "正在创建谱面与编辑缓存…";
    audioImportNameStatusText.Foreground = new SolidColorBrush(Color.Parse("#C58C79"));
    taskCompletionSource.TrySetResult(audioImportNameBox.Text);
}

private void HideAudioImportNamePrompt()
{
    audioImportNameLayer.IsVisible = false;
    audioImportNameCompletion = null;
}

private async Task<OnlineRoomUploadPreparation> PrepareCurrentOnlineRoomUploadAsync(CancellationToken cancellationToken)
{
    if (onlineAccountHost?.Session.CurrentRoom != null)
    {
        throw new InvalidOperationException("请先退出当前联机房间，再创建新房间。");
    }

    DesktopChartTab tab = viewModel.ActiveTab ?? throw new InvalidOperationException("请先打开要绑定到房间的谱面；一个房间只编辑一张谱。");
    if (roomChartSyncRoomId.HasValue)
    {
        throw new InvalidOperationException("请先退出当前联机谱面，再创建新房间。");
    }

    viewModel.SetStatus("正在刷新谱面工作缓存…");
    if (!(await viewModel.PersistAsync()))
    {
        throw new IOException(viewModel.Status);
    }

    cancellationToken.ThrowIfCancellationRequested();
    WindowsOnlineRoomUploadPackage windowsOnlineRoomUploadPackage = await onlineRoomResourceService.BuildUploadPackageAsync(tab, cancellationToken);
    preparedOnlineRoomUpload = new PreparedOnlineRoomUpload(tab.Id, tab.Session.ChangeSerial, windowsOnlineRoomUploadPackage);
    return new OnlineRoomUploadPreparation(windowsOnlineRoomUploadPackage.Files.Count, windowsOnlineRoomUploadPackage.TotalBytes, windowsOnlineRoomUploadPackage.Files.Select((WindowsOnlineRoomUploadFile file) => new OnlineRoomUploadPreviewFile(file.Path, file.Length)).ToArray());
}

private async Task<OnlineRoomResourcePreparation> PrepareCurrentOnlineRoomDownloadAsync(OnlineRoomSummaryDto room, CancellationToken cancellationToken)
{
    OnlineAccountSession obj = onlineAccountHost?.Session ?? throw new InvalidOperationException("联机账号会话不可用。");
    if (obj.CurrentRoom?.Id != room.Id)
    {
        throw new InvalidOperationException("当前房间已经变化，请重新进入。");
    }

    OnlineRoomResourceManifestDto manifest = await obj.GetRoomResourceManifestAsync(room.Id, cancellationToken);
    WindowsOnlineRoomResourceCacheStatus windowsOnlineRoomResourceCacheStatus = await onlineRoomResourceService.InspectAsync(manifest, cancellationToken);
    long totalBytes = windowsOnlineRoomResourceCacheStatus.TotalBytes;
    long missingBytes = windowsOnlineRoomResourceCacheStatus.MissingBytes;
    int reusedCount = windowsOnlineRoomResourceCacheStatus.ReusedCount;
    IReadOnlyList<OnlineRoomResourceEntryDto> entries = manifest.Entries;
    return new OnlineRoomResourcePreparation(totalBytes, missingBytes, reusedCount, entries != null && entries.Count > 0);
}

private async Task OpenCurrentOnlineRoomChartAsync(OnlineRoomSummaryDto room, bool lockExistingNotes, bool usePreparedUpload, CancellationToken cancellationToken)
{
    OnlineAccountSession account = onlineAccountHost?.Session ?? throw new InvalidOperationException("联机账号会话不可用。");
    if (account.CurrentRoom?.Id != room.Id)
    {
        throw new InvalidOperationException("当前房间已经变化，请重新进入。");
    }

    await StopRoomChartSyncAsync(resetRetainedTab: true);
    if (room.OwnerUserId == account.CurrentUser?.Id && !room.ResourcesReady)
    {
        await InitializeOwnedRoomChartAsync(account, room, lockExistingNotes, usePreparedUpload, cancellationToken);
        return;
    }

    viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("正在检查房间“{0}”的本地资源缓存…", room.Name));
    OnlineRoomResourceManifestDto manifest = await account.GetRoomResourceManifestAsync(room.Id, cancellationToken);
    WindowsOnlineRoomResourceCacheStatus windowsOnlineRoomResourceCacheStatus = await onlineRoomResourceService.InspectAsync(manifest, cancellationToken);
    ShowRoomDownloadProgress(room.Id, room.Name, windowsOnlineRoomResourceCacheStatus);
    bool enteredRoom = false;
    try
    {
        try
        {
            viewModel.SetStatus((windowsOnlineRoomResourceCacheStatus.MissingBytes == 0L) ? "房间资源 SHA-256 校验通过，正在复用本地缓存…" : AvaloniaLocalizationCatalog.Format("正在下载房间资源：{0}", FormatTransferBytes(windowsOnlineRoomResourceCacheStatus.MissingBytes)));
            Progress<WindowsOnlineRoomTransferProgress> progress = new Progress<WindowsOnlineRoomTransferProgress>((WindowsOnlineRoomTransferProgress value) =>
            {
                UpdateRoomDownloadProgress(room.Id, value);
                viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("房间资源 {0:P0} · {1} / {2} · {3}", value.Fraction, FormatTransferBytes(value.TransferredBytes), FormatTransferBytes(value.TotalBytes), value.CurrentPath));
            });
            WindowsOnlineRoomMaterialization materialized = await onlineRoomResourceService.DownloadAndMaterializeAsync(account, room.Id, manifest, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SetRoomDownloadProgressStage(room.Id, "资源下载与校验完成，正在打开真实谱面", "100%  ·  正在进入编辑器");
            await PreserveRetainedRoomTabBeforeRebindAsync(materialized.ChartPath, room.Id, cancellationToken);
            viewModel.SetStatus("房间资源已校验，正在打开真实 MC 编辑器标签…");
            await viewModel.OpenAsync(materialized.ChartPath);
            DesktopChartTab tab = viewModel.ActiveTab ?? throw new InvalidDataException("房间谱面无法打开。");
            OnlineChartSnapshot snapshot = await account.GetRoomChartAsync(room.Id, cancellationToken);
            ChartDocument canonical = OnlineChartSyncCodec.CreateCanonicalDocument(tab.Session.CaptureCommittedDocument(), snapshot);
            ApplyInitialRoomSnapshot(tab, canonical, snapshot.Locks);
            WindowsFrozenRoomResourceRegistry frozenResources = await WindowsFrozenRoomResourceRegistry.CreateAsync(tab.WorkingPath, manifest, cancellationToken);
            await BindRoomChartAsync(account, room.Id, room.RoomNumber, room.Name, tab, RoomChartSyncState.Create(snapshot.Revision, canonical, 0L), frozenResources);
            bool flag = account.CurrentMemberRole.Equals("viewer", StringComparison.OrdinalIgnoreCase);
            viewModel.SetStatus(flag ? AvaloniaLocalizationCatalog.Format("已进入房间“{0}” · 观察者只读 · revision {1:N0} · {2:N0} Note", room.Name, snapshot.Revision, tab.Session.Document.Notes.Count) : AvaloniaLocalizationCatalog.Format("已进入联机房间“{0}” · revision {1:N0} · {2:N0} Note", room.Name, snapshot.Revision, tab.Session.Document.Notes.Count));
            enteredRoom = true;
        }
        catch (Exception ex)
        {
            if (roomDownloadProgressRoomId == room.Id)
            {
                roomDownloadProgressBar.IsIndeterminate = false;
                roomDownloadProgressDetailText.Text = AvaloniaLocalizationCatalog.Format("下载或打开失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message));
                roomDownloadProgressValueText.Text = "未完成";
            }

            throw;
        }
    }
    finally
    {
        await HideRoomDownloadProgressAsync(room.Id, enteredRoom);
    }
}

private async Task InitializeOwnedRoomChartAsync(OnlineAccountSession account, OnlineRoomSummaryDto room, bool lockExistingNotes, bool usePreparedUpload, CancellationToken cancellationToken)
{
    DesktopChartTab tab = viewModel.ActiveTab ?? throw new InvalidOperationException("请先打开要绑定到房间的谱面。");
    Guid? guid = roomChartSyncRoomId;
    if (guid.HasValue)
    {
        Guid valueOrDefault = guid.GetValueOrDefault();
        if (valueOrDefault != room.Id)
        {
            throw new InvalidOperationException("请先退出当前联机谱面，再创建新房间。");
        }
    }

    await StopRoomChartSyncAsync(resetRetainedTab: true);
    viewModel.SetStatus("正在刷新谱面工作缓存…");
    WindowsOnlineRoomUploadPackage package;
    if (usePreparedUpload)
    {
        PreparedOnlineRoomUpload preparedOnlineRoomUpload = this.preparedOnlineRoomUpload;
        if ((object)preparedOnlineRoomUpload != null && preparedOnlineRoomUpload.TabId == tab.Id && preparedOnlineRoomUpload.ChangeSerial == tab.Session.ChangeSerial)
        {
            package = preparedOnlineRoomUpload.Package;
            goto IL_027d;
        }
    }

    if (!(await viewModel.PersistAsync()))
    {
        throw new IOException(viewModel.Status);
    }

    package = await onlineRoomResourceService.BuildUploadPackageAsync(tab, cancellationToken);
    goto IL_027d;
    IL_027d:
        this.preparedOnlineRoomUpload = null;
    viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("正在建立联机谱面状态并上传 {0:N0} 个资源…", package.Files.Count));
    ShowRoomUploadProgress(room.Id, room.Name, package);
    bool uploadSucceeded = false;
    ChartDocument committedDocument = tab.Session.CaptureCommittedDocument();
    OwnerProtectedRegion[] previousOwnerRegions = tab.Session.OwnerProtectedRegions.ToArray();
    bool previousAllowOwnerProtectedEdits = tab.Session.AllowOwnerProtectedEdits;
    bool appliedServerInitialization = false;
    OnlineChartLockRange[] initialLocks = (lockExistingNotes ? (
        from range in OwnerLockSelectionPlanner.BuildAutomaticNoteRanges(committedDocument.Notes, tab.Session.SnapDivision)select OnlineChartSyncCodec.CreateLockRange(range.StartBeat, range.EndBeat)).ToArray() : Array.Empty<OnlineChartLockRange>());
    try
    {
        OnlineChartSnapshot snapshot = await account.InitializeRoomChartAsync(room.Id, OnlineChartSyncCodec.CreateInitializeRequest(committedDocument, initialLocks), cancellationToken);
        ChartDocument canonical = OnlineChartSyncCodec.CreateCanonicalDocument(committedDocument, snapshot);
        ApplyInitialRoomSnapshot(tab, canonical, snapshot.Locks);
        appliedServerInitialization = true;
        Progress<WindowsOnlineRoomTransferProgress> progress = new Progress<WindowsOnlineRoomTransferProgress>((WindowsOnlineRoomTransferProgress value) =>
        {
            UpdateRoomUploadProgress(room.Id, value);
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("上传房间资源 {0:P0} · {1} / {2} · {3}", value.Fraction, FormatTransferBytes(value.TransferredBytes), FormatTransferBytes(value.TotalBytes), value.CurrentPath));
        });
        OnlineRoomResourceManifestDto manifest = await onlineRoomResourceService.UploadAsync(account, room.Id, package, progress, cancellationToken);
        WindowsFrozenRoomResourceRegistry frozenResources = await WindowsFrozenRoomResourceRegistry.CreateAsync(tab.WorkingPath, manifest, cancellationToken);
        await BindRoomChartAsync(account, room.Id, room.RoomNumber, room.Name, tab, RoomChartSyncState.Create(snapshot.Revision, canonical, 0L), frozenResources);
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("房间“{0}”已创建并绑定当前编辑器 · revision {1:N0}", room.Name, snapshot.Revision));
        uploadSucceeded = true;
    }
    catch (Exception ex)
    {
        if (roomUploadProgressRoomId == room.Id)
        {
            roomUploadProgressDetailText.Text = AvaloniaLocalizationCatalog.Format("上传失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message));
            roomUploadProgressValueText.Text = "未完成";
        }

        if (roomChartSyncRoomId == room.Id)
        {
            await StopRoomChartSyncAsync(resetRetainedTab: false);
        }

        if (appliedServerInitialization && viewModel.Tabs.Contains(tab))
        {
            applyingRemoteChartSync = true;
            try
            {
                tab.Session.ApplyRemoteMutation((ChartDocument document) =>
                {
                    ReplaceOnlineChartContent(document, committedDocument);
                });
                tab.Session.ReplaceOwnerProtectedRegions(previousOwnerRegions);
                tab.Session.AllowOwnerProtectedEdits = previousAllowOwnerProtectedEdits;
            }
            finally
            {
                applyingRemoteChartSync = false;
            }
        }

        throw;
    }
    finally
    {
        await HideRoomUploadProgressAsync(room.Id, uploadSucceeded);
    }
}

private void ApplyInitialRoomSnapshot(DesktopChartTab tab, ChartDocument canonical, IReadOnlyList<OnlineChartLock> locks)
{
    applyingRemoteChartSync = true;
    try
    {
        tab.Session.ApplyRemoteMutation((ChartDocument document) =>
        {
            ReplaceOnlineChartContent(document, canonical);
        });
        tab.Session.ReplaceOwnerProtectedRegions(OnlineChartSyncCodec.ToOwnerRegions(locks));
    }
    finally
    {
        applyingRemoteChartSync = false;
    }
}

private async Task BindRoomChartAsync(OnlineAccountSession account, Guid roomId, long roomNumber, string roomName, DesktopChartTab tab, RoomChartSyncState initialState, WindowsFrozenRoomResourceRegistry frozenResources)
{
    await StopRoomChartSyncAsync(resetRetainedTab: true);
    OnlineRoomSummaryDto? currentRoom = account.CurrentRoom;
    if ((object)currentRoom == null || currentRoom.Id != roomId || !viewModel.Tabs.Contains(tab))
    {
        throw new OperationCanceledException("联机谱面绑定已取消。");
    }

    roomChartSyncRoomId = roomId;
    roomChartSyncRoomNumber = Math.Max(0L, roomNumber);
    roomChartSyncRoomName = roomName?.Trim() ?? string.Empty;
    roomChartSyncTab = tab;
    onlineCreatorMembers = Array.Empty<OnlineRoomMemberDto>();
    ResetRoomChatUi(reveal: true);
    RefreshTimelineOnlineCreators();
    onlineRoundTripLatencyMs = null;
    retainedOnlineRoomTabIds.Remove(tab.Id);
    roomChartFrozenResources = frozenResources;
    roomChartLocalChangeSerial = 0L;
    roomChartObservedSessionSerial = tab.Session.ChangeSerial;
    tab.Session.Changed += RoomChartSession_Changed;
    roomAccessSnapshot = CaptureRoomAccess(account);
    ApplyOnlineRoomPermissions();
    RoomChartSyncCoordinator coordinator = null;
    AvaloniaRoomChartSyncDocumentHost host = new AvaloniaRoomChartSyncDocumentHost(() => new RoomChartLocalSnapshot(tab.Session.CaptureCommittedDocument(), roomChartLocalChangeSerial), (RoomChartApplyPlan plan) =>
    {
        if (roomChartSyncCoordinator == coordinator)
        {
            ApplyRoomChartPlan(tab, plan);
        }
    }, (RoomChartSyncViewState state) =>
    {
        if (roomChartSyncCoordinator == coordinator)
        {
            PublishRoomChartSyncViewState(tab, state);
        }
    }, () => roomChartLocalChangeSerial, (RoomChartRejectedLocalChanges rejected) =>
    {
        PreserveRejectedRoomChartChanges(tab, rejected);
    });
    coordinator = new RoomChartSyncCoordinator(initialState, new AvaloniaRoomChartSyncTransport(account, roomId), host);
    roomChartSyncCoordinator = coordinator;
    coordinator.Start();
    await EnsureOnlineRoomPresenceLoopAsync(roomId);
    viewModel.Activate(tab.Id);
    ShowEditorSurface();
}

private void PreserveRejectedRoomChartChanges(DesktopChartTab tab, RoomChartRejectedLocalChanges rejected)
{
    try
    {
        string path = ChartBackupArchiveService.CreateBackup(AvaloniaAppPaths.EditorBackupRoot, rejected.LocalDocument, tab.SourcePath);
        if (!windowCloseTeardownStarted)
        {
            string text = AvaloniaLocalizationCatalog.Format("部分协作修改被服务器拒绝，已保存为备份，可通过恢复备份找回：{0}", System.IO.Path.GetFileName(path));
            backupCacheStatusText.Text = text;
            ShowEditorToast(text);
            viewModel.SetStatus(text);
        }
    }
    catch (Exception ex)
    {
        try
        {
            if (!windowCloseTeardownStarted)
            {
                viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("部分协作修改被服务器拒绝，保存备份失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
            }
        }
        catch (Exception)
        {
        }
    }
}

private void RoomChartSession_Changed(object? sender, EventArgs eventArgs)
{
    DesktopChartTab desktopChartTab = roomChartSyncTab;
    if (desktopChartTab == null || sender != desktopChartTab.Session)
    {
        return;
    }

    long changeSerial = desktopChartTab.Session.ChangeSerial;
    checked
    {
        if (changeSerial != roomChartObservedSessionSerial)
        {
            roomChartObservedSessionSerial = changeSerial;
            if (!applyingRemoteChartSync)
            {
                roomChartLocalChangeSerial++;
                roomChartSyncCoordinator?.NotifyLocalChange();
            }
        }
    }
}

private void ApplyRoomChartPlan(DesktopChartTab tab, RoomChartApplyPlan plan)
{
    applyingRemoteChartSync = true;
    try
    {
        if (plan.ApplyDocument)
        {
            ChartDocument next = plan.Document;
            if (plan.ClearHistory)
            {
                tab.Session.ReplaceDocument(next, markDirty: true);
            }
            else if (!tab.Session.TryMergeRemoteAttributionDuringInteractiveMutation(next))
            {
                tab.Session.ApplyRemoteMutation((ChartDocument document) =>
                {
                    ReplaceOnlineChartContent(document, next);
                });
            }
        }

        tab.Session.ReplaceOwnerProtectedRegions(plan.OwnerProtectedRegions);
    }
    finally
    {
        applyingRemoteChartSync = false;
    }

    if (viewModel.ActiveTab == tab)
    {
        UpdateUi();
    }
}

private void PublishRoomChartSyncViewState(DesktopChartTab tab, RoomChartSyncViewState state)
{
    if (roomChartSyncTab != tab)
    {
        return;
    }

    timeline.SetRoomSyncDiagnostics(state.WorkStatus.ToString(), state.FeedStatus.ToString(), state.Revision, state.PendingOperationId, state.ConsecutiveWorkFailures, state.ConsecutiveFeedFailures);
    string text = FormatRoomChartRetryDelay(state.RetryAt);
    string text2;
    switch (state.WorkStatus)
    {
        case RoomChartSyncWorkStatus.Throttled:
            text2 = "正在汇集本地谱面修改…";
            break;
        case RoomChartSyncWorkStatus.Synchronizing:
            text2 = "正在同步房间谱面…";
            break;
        case RoomChartSyncWorkStatus.RollingBack:
            text2 = "正在原子回滚房间谱面…";
            break;
        case RoomChartSyncWorkStatus.BackingOff:
            text2 = AvaloniaLocalizationCatalog.Format("谱面同步暂时中断，{0}后重试；本地修改会保留。", text);
            break;
        case RoomChartSyncWorkStatus.WriteBudgetThrottled:
            text2 = AvaloniaLocalizationCatalog.Format("谱面写入额度暂满，{0}后用同一 OperationId 重试。", text);
            break;
        case RoomChartSyncWorkStatus.PermanentlyBlocked:
            text2 = "房间已达到联机操作上限；本地修改仍可保存。";
            break;
        case RoomChartSyncWorkStatus.Faulted:
            text2 = AvaloniaLocalizationCatalog.Format("谱面同步：{0}", state.WorkErrorMessage ?? AvaloniaLocalizationCatalog.Text("发生未知错误。"));
            break;
        case RoomChartSyncWorkStatus.Idle:
            if (state.WorkErrorMessage != null)
            {
                text2 = AvaloniaLocalizationCatalog.Format("谱面同步：{0}", AvaloniaLocalizationCatalog.Text(state.WorkErrorMessage));
                break;
            }

            if (state.StatusHoldUntil > DateTimeOffset.UtcNow)
            {
                text2 = AvaloniaLocalizationCatalog.Format("房间谱面已同步 · revision {0:N0}", state.Revision);
                break;
            }

            goto default;
        default:
            text2 = string.Empty;
            break;
    }

    string text3 = text2;
    if (text3.Length == 0)
    {
        text3 = state.FeedStatus switch
        {
            RoomChartSyncFeedStatus.Connecting => "正在连接实时谱面通知…",
            RoomChartSyncFeedStatus.Reconnecting => "实时谱面通知中断，正在重连…",
            RoomChartSyncFeedStatus.Faulted => AvaloniaLocalizationCatalog.Format("实时谱面通知：{0}", state.FeedErrorMessage ?? AvaloniaLocalizationCatalog.Text("连接已停止。")),
            _ => string.Empty,
        };
    }

    if (text3.Length == 0 && state.WorkStatus == RoomChartSyncWorkStatus.Idle && state.FeedStatus == RoomChartSyncFeedStatus.Connected)
    {
        text3 = AvaloniaLocalizationCatalog.Format("房间谱面已同步 · revision {0:N0}", state.Revision);
    }

    if (text3.Length > 0)
    {
        viewModel.SetStatus(text3);
    }

    string permissionDeniedCategory = CollaborationRevisionPolicy.GetPermissionDeniedCategory(state.WorkErrorCode);
    if (permissionDeniedCategory != null && !string.Equals(lastRoomChartPermissionErrorCode, state.WorkErrorCode, StringComparison.Ordinal))
    {
        lastRoomChartPermissionErrorCode = state.WorkErrorCode;
        WindowsRoomPermissionNoticePresentation windowsRoomPermissionNoticePresentation = WindowsRoomPermissionNoticePolicy.Denied(permissionDeniedCategory);
        viewModel.SetStatus(windowsRoomPermissionNoticePresentation.Message);
        ShowRoomPermissionNotice(windowsRoomPermissionNoticePresentation);
    }
    else if (permissionDeniedCategory == null)
    {
        lastRoomChartPermissionErrorCode = null;
    }
}

private void ApplyOnlineRoomPermissions()
{
    DesktopChartTab desktopChartTab = roomChartSyncTab;
    OnlineAccountSession onlineAccountSession = onlineAccountHost?.Session;
    OnlineRoomSummaryDto onlineRoomSummaryDto = onlineAccountSession?.CurrentRoom;
    if (desktopChartTab == null || onlineAccountSession == null || onlineRoomSummaryDto == null)
    {
        return;
    }

    Guid id = onlineRoomSummaryDto.Id;
    Guid? guid = roomChartSyncRoomId;
    if (!(id != guid))
    {
        bool flag = onlineRoomSummaryDto.OwnerUserId == onlineAccountSession.CurrentUser?.Id || onlineAccountSession.CurrentMemberRole.Equals("owner", StringComparison.OrdinalIgnoreCase);
        WindowsOnlineRoomEditorAccess windowsOnlineRoomEditorAccess = WindowsOnlineRoomPermissionPolicy.Resolve(flag, onlineAccountSession.CurrentMemberRole, onlineAccountSession.CanEditMetadata, onlineAccountSession.CanEditTiming, onlineAccountSession.CanEditNotes);
        desktopChartTab.Session.AllowOwnerProtectedEdits = flag;
        desktopChartTab.Session.IsReadOnly = windowsOnlineRoomEditorAccess.IsReadOnly || desktopChartTab.IsLibraryReadOnly;
        desktopChartTab.Session.CanEditMetadata = windowsOnlineRoomEditorAccess.CanEditMetadata && !desktopChartTab.IsLibraryReadOnly;
        desktopChartTab.Session.CanEditTiming = windowsOnlineRoomEditorAccess.CanEditTiming && !desktopChartTab.IsLibraryReadOnly;
        desktopChartTab.Session.CanEditNotes = windowsOnlineRoomEditorAccess.CanEditNotes && !desktopChartTab.IsLibraryReadOnly;
        if (viewModel.ActiveTab == desktopChartTab)
        {
            UpdateSelectionUi();
        }
    }
}

private void UpdateOnlineCreatorMembers(OnlineAccountSession account, IReadOnlyList<OnlineRoomMemberDto> members)
{
    ArgumentNullException.ThrowIfNull(account, "account");
    ArgumentNullException.ThrowIfNull(members, "members");
    onlineCreatorMembers = members.Where((OnlineRoomMemberDto member) => member.IsOnline).ToArray();
    roomMemberList.ItemsSource = onlineCreatorMembers;
    OnlineRoomSummaryDto currentRoom = account.CurrentRoom;
    roomCollaborationTitle.Text = currentRoom?.Name ?? roomChartSyncRoomName ?? "联机房间";
    roomMemberCountText.Text = $"{currentRoom?.MemberCount ?? 0:N0} / {currentRoom?.MaximumMembers ?? 8:N0}";
    RefreshTimelineOnlineCreators();
    foreach (string item in (
        from member in onlineCreatorMembers
        select member.AvatarKey into avatarKey
            where !string.IsNullOrWhiteSpace(avatarKey) && !avatarKey.Equals("default", StringComparison.OrdinalIgnoreCase)select avatarKey).Distinct(StringComparer.Ordinal))
    {
        if (!onlineCreatorAvatarCache.ContainsKey(item) && !onlineCreatorAvatarLoads.ContainsKey(item))
        {
            Task<Bitmap> task = DownloadOnlineCreatorAvatarAsync(account, item);
            onlineCreatorAvatarLoads.Add(item, task);
            InstallOnlineCreatorAvatarAsync(item, task);
        }
    }
}

private void RefreshTimelineOnlineCreators()
{
    if (!roomChartSyncRoomId.HasValue || roomChartSyncTab == null || viewModel.ActiveTab != roomChartSyncTab)
    {
        timeline.SetOnlineCreators(new Dictionary<long, OnlineCreatorAppearance>());
        return;
    }

    Dictionary<long, OnlineCreatorAppearance> dictionary = new Dictionary<long, OnlineCreatorAppearance>();
    foreach (OnlineRoomMemberDto onlineCreatorMember in onlineCreatorMembers)
    {
        Bitmap avatar = ((!string.IsNullOrWhiteSpace(onlineCreatorMember.AvatarKey) && onlineCreatorAvatarCache.TryGetValue(onlineCreatorMember.AvatarKey, out Bitmap value)) ? value : null);
        dictionary[onlineCreatorMember.UserId] = new OnlineCreatorAppearance(onlineCreatorMember.DisplayName, avatar);
    }

    timeline.SetOnlineCreators(dictionary);
}

private async Task<Bitmap?> DownloadOnlineCreatorAvatarAsync(OnlineAccountSession account, string avatarKey)
{
    using MemoryStream stream = new MemoryStream();
    await account.DownloadAvatarAsync(avatarKey, stream, null, onlineCreatorAvatarLifetime.Token);
    stream.Position = 0L;
    return new Bitmap(stream);
}

private void BindRemoteAvatar(WindowsDefaultUserAvatar avatar, string? avatarKey, string displayName)
{
    ArgumentNullException.ThrowIfNull(avatar, "avatar");
    string text = avatarKey?.Trim() ?? string.Empty;
    avatar.RemoteImage.Tag = text;
    Bitmap value;
    if (string.IsNullOrWhiteSpace(text) || text.Equals("default", StringComparison.OrdinalIgnoreCase))
    {
        avatar.RemoteImage.Source = null;
        AutomationProperties.SetName(avatar, AvaloniaLocalizationCatalog.Format("默认头像 · {0}", displayName));
    }
    else if (onlineCreatorAvatarCache.TryGetValue(text, out value))
    {
        avatar.RemoteImage.Source = value;
        AutomationProperties.SetName(avatar, AvaloniaLocalizationCatalog.Format("用户头像 · {0}", displayName));
    }
    else
    {
        avatar.RemoteImage.Source = null;
        AutomationProperties.SetName(avatar, AvaloniaLocalizationCatalog.Format("正在载入头像 · {0}", displayName));
        LoadRemoteAvatarIntoAsync(avatar, text, displayName);
    }
}

private async Task LoadRemoteAvatarIntoAsync(WindowsDefaultUserAvatar avatar, string avatarKey, string displayName)
{
    OnlineAccountSession onlineAccountSession = onlineAccountHost?.Session;
    if (onlineAccountSession == null || onlineCreatorAvatarLifetime.IsCancellationRequested)
    {
        return;
    }

    try
    {
        if (!onlineCreatorAvatarLoads.TryGetValue(avatarKey, out Task<Bitmap> value))
        {
            value = DownloadOnlineCreatorAvatarAsync(onlineAccountSession, avatarKey);
            onlineCreatorAvatarLoads.Add(avatarKey, value);
            InstallOnlineCreatorAvatarAsync(avatarKey, value);
        }

        Bitmap bitmap = await value;
        if (bitmap != null && !onlineCreatorAvatarLifetime.IsCancellationRequested && string.Equals(avatar.RemoteImage.Tag as string, avatarKey, StringComparison.Ordinal))
        {
            avatar.RemoteImage.Source = bitmap;
            AutomationProperties.SetName(avatar, AvaloniaLocalizationCatalog.Format("用户头像 · {0}", displayName));
        }
    }
    catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is OperationCanceledException || ex is JsonException || ex is IOException || ex is InvalidDataException || ex is ArgumentException) ? true : false)
    {
        if (string.Equals(avatar.RemoteImage.Tag as string, avatarKey, StringComparison.Ordinal))
        {
            avatar.RemoteImage.Source = null;
            AutomationProperties.SetName(avatar, AvaloniaLocalizationCatalog.Format("默认头像 · {0}", displayName));
        }
    }
}

private async Task InstallOnlineCreatorAvatarAsync(string avatarKey, Task<Bitmap?> load)
{
    Bitmap bitmap = null;
    try
    {
        bitmap = await load;
        if (bitmap == null || onlineCreatorAvatarLifetime.IsCancellationRequested)
        {
            bitmap?.Dispose();
            return;
        }

        onlineCreatorAvatarCache[avatarKey] = bitmap;
        RefreshTimelineOnlineCreators();
    }
    catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is OperationCanceledException || ex is JsonException || ex is IOException || ex is InvalidDataException || ex is ArgumentException) ? true : false)
    {
        bitmap?.Dispose();
        onlineCreatorAvatarLoads.Remove(avatarKey);
    }
}

private async Task EnsureOnlineRoomPresenceLoopAsync(Guid roomId)
{
    await roomPresenceLifecycleGate.WaitAsync();
    try
    {
        if (roomPresenceRoomId == roomId)
        {
            CancellationTokenSource cancellationTokenSource = roomPresenceLifetime;
            if (cancellationTokenSource != null && !cancellationTokenSource.IsCancellationRequested && !roomPresenceTask.IsCompleted)
            {
                return;
            }
        }

        await StopOnlineRoomPresenceLoopCoreAsync();
        CancellationTokenSource lifetime = new CancellationTokenSource();
        long generation = checked(roomPresenceGeneration + 1);
        roomPresenceGeneration = generation;
        roomPresenceRoomId = roomId;
        roomPresenceLifetime = lifetime;
        roomPresenceTask = Task.Run(() => RunOnlineRoomPresenceLoopAsync(roomId, generation, lifetime.Token), CancellationToken.None);
    }
    finally
    {
        roomPresenceLifecycleGate.Release();
    }
}

private async Task StopOnlineRoomPresenceLoopAsync()
{
    await roomPresenceLifecycleGate.WaitAsync();
    try
    {
        await StopOnlineRoomPresenceLoopCoreAsync();
    }
    finally
    {
        roomPresenceLifecycleGate.Release();
    }
}

private async Task StopOnlineRoomPresenceLoopCoreAsync()
{
    CancellationTokenSource lifetime = roomPresenceLifetime;
    Task task = roomPresenceTask;
    roomPresenceLifetime = null;
    roomPresenceTask = Task.CompletedTask;
    roomPresenceRoomId = null;
    checked
    {
        roomPresenceGeneration++;
        if (lifetime == null)
        {
            return;
        }

        try
        {
            lifetime.Cancel();
            await task.ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (OperationCanceledException)when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            lifetime.Dispose();
        }
    }
}

private async Task RunOnlineRoomPresenceLoopAsync(Guid roomId, long generation, CancellationToken cancellationToken)
{
    int failures = 0;
    while (!cancellationToken.IsCancellationRequested)
    {
        OnlineAccountSession account = onlineAccountHost?.Session;
        if (account == null || account.CurrentRoom?.Id != roomId)
        {
            break;
        }

        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15.0));
            Stopwatch requestClock = Stopwatch.StartNew();
            OnlineRoomLifecycleResult<OnlineRoomLiveStateDto> live = await account.GetRoomLiveStateAsync((roomChartSyncRoomId == roomId) ? latestRoomMessageId : 0, timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
            requestClock.Stop();
            if (live.Outcome == OnlineRoomLifecycleOutcome.Success && !(live.Value == null))
            {
                OnlineRoomSummaryDto? currentRoom = account.CurrentRoom;
                if ((object)currentRoom == null || currentRoom.Id != roomId)
                {
                    break;
                }

                failures = 0;
                await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ApplyOnlineRoomLiveState(roomId, generation, live.Value, requestClock.ElapsedMilliseconds);
                }, DispatcherPriority.Background, cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(3.0), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                continue;
            }

            break;
        }
        catch (OperationCanceledException)when (cancellationToken.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex2)
        {
            Exception exception = ex2;
            OnlineRoomSummaryDto? currentRoom2 = account.CurrentRoom;
            if ((object)currentRoom2 == null || currentRoom2.Id != roomId)
            {
                break;
            }

            failures = Math.Min(failures + 1, 8);
            TimeSpan retry = ComputeRoomPresenceRetryDelay(failures);
            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                PublishRoomPresenceRetry(roomId, generation, exception, retry);
            }, DispatcherPriority.Background, cancellationToken);
            await Task.Delay(retry, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
}

private void ApplyOnlineRoomLiveState(Guid roomId, long generation, OnlineRoomLiveStateDto liveState, long elapsedMilliseconds)
{
    Guid? guid = roomPresenceRoomId;
    Guid guid2 = roomId;
    if (!guid.HasValue || guid.GetValueOrDefault() != guid2 || roomPresenceGeneration != generation)
    {
        return;
    }

    WindowsOnlineAccountHost? windowsOnlineAccountHost = onlineAccountHost;
    if (windowsOnlineAccountHost == null)
    {
        return;
    }

    guid = windowsOnlineAccountHost.Session.CurrentRoom?.Id;
    guid2 = roomId;
    if (!guid.HasValue || guid.GetValueOrDefault() != guid2 || !(roomChartSyncRoomId == roomId))
    {
        return;
    }

    UpdateOnlineCreatorMembers(onlineAccountHost.Session, liveState.Members);
    AppendRoomChatMessages(liveState.Messages, liveState.LatestMessageId);
    roomCollaborationTitle.Text = liveState.Room.Name;
    roomMemberCountText.Text = $"{liveState.Room.MemberCount:N0} / {liveState.Room.MaximumMembers:N0}";
    roomChatStatusText.Text = string.Empty;
    onlineRoundTripLatencyMs = (int)Math.Clamp(elapsedMilliseconds, 0L, 99999L);
    UpdateOnlineLatencyLabels();
    WindowsRoomAccessSnapshot windowsRoomAccessSnapshot = roomAccessSnapshot;
    WindowsRoomAccessSnapshot current = CaptureRoomAccess(onlineAccountHost.Session);
    ApplyOnlineRoomPermissions();
    roomAccessSnapshot = current;
    if (windowsRoomAccessSnapshot != null)
    {
        WindowsRoomPermissionNoticePresentation windowsRoomPermissionNoticePresentation = WindowsRoomPermissionNoticePolicy.Updated(windowsRoomAccessSnapshot, current);
        if ((object)windowsRoomPermissionNoticePresentation != null)
        {
            ShowRoomPermissionNotice(windowsRoomPermissionNoticePresentation);
        }
    }
}

private void PublishRoomPresenceRetry(Guid roomId, long generation, Exception exception, TimeSpan retry)
{
    Guid? guid = roomPresenceRoomId;
    Guid guid2 = roomId;
    if (guid.HasValue && !(guid.GetValueOrDefault() != guid2) && roomPresenceGeneration == generation)
    {
        onlineRoundTripLatencyMs = null;
        UpdateOnlineLatencyLabels();
        if (roomChartSyncRoomId == roomId)
        {
            roomChatStatusText.Text = AvaloniaLocalizationCatalog.Format("连接暂时中断，{0} 秒后重试…", Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)));
        }
    }
}

private static TimeSpan ComputeRoomPresenceRetryDelay(int failureCount)
{
    int num = Math.Min(5, Math.Max(0, failureCount - 1));
    return TimeSpan.FromMilliseconds(Math.Min(8000, 400 * (1 << num)) + Random.Shared.Next(80, 280));
}

private async void ChartingHeartbeatTimer_Tick(object? sender, EventArgs eventArgs)
{
    if (chartingHeartbeatInProgress)
    {
        return;
    }

    WindowsOnlineAccountHost? windowsOnlineAccountHost = onlineAccountHost;
    if (windowsOnlineAccountHost == null || !windowsOnlineAccountHost.Session.IsAuthenticated || chartingHeartbeatLifetime.IsCancellationRequested)
    {
        return;
    }

    chartingHeartbeatInProgress = true;
    activeChartingHeartbeatTask = RunChartingHeartbeatAsync();
    try
    {
        await activeChartingHeartbeatTask;
    }
    catch (OperationCanceledException)when (chartingHeartbeatLifetime.IsCancellationRequested)
    {
    }
    finally
    {
        chartingHeartbeatInProgress = false;
    }
}

private async Task RunChartingHeartbeatAsync()
{
    OnlineAccountSession onlineAccountSession = onlineAccountHost?.Session;
    if (onlineAccountSession == null || !onlineAccountSession.IsAuthenticated)
    {
        return;
    }

    using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(chartingHeartbeatLifetime.Token);
    timeout.CancelAfter(TimeSpan.FromSeconds(15.0));
    try
    {
        DesktopChartTab activeTab = viewModel.ActiveTab;
        if (activeTab == null)
        {
            chartingHeartbeatStatus = "等待打开谱面";
            UpdateOnlineAccountUi();
            return;
        }

        HashSet<Guid> liveTabIds = viewModel.Tabs.Select((DesktopChartTab item) => item.Id).ToHashSet();
        Guid[] array = chartingHeartbeatSessionIds.Keys.Where((Guid id) => !liveTabIds.Contains(id)).ToArray();
        foreach (Guid key in array)
        {
            chartingHeartbeatSessionIds.Remove(key);
        }

        if (!chartingHeartbeatSessionIds.TryGetValue(activeTab.Id, out var value))
        {
            value = Guid.NewGuid();
            chartingHeartbeatSessionIds.Add(activeTab.Id, value);
        }

        bool isEditing = !libraryVisible && !onlineSurfaceLayer.IsVisible && !windowCloseTeardownStarted && !windowClosePreflightComplete;
        OnlineChartingHeartbeatResponseDto onlineChartingHeartbeatResponseDto = await onlineAccountSession.SendChartingHeartbeatAsync(value, activeTab.Session.ChangeSerial, isEditing, timeout.Token);
        if (onlineChartingHeartbeatResponseDto != null)
        {
            chartingHeartbeatStatus = (onlineChartingHeartbeatResponseDto.IsCounting ? "正在累计有效制谱时长" : "等待有效谱面操作");
        }
    }
    catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is WebException || ex is OperationCanceledException || ex is JsonException) ? true : false)
    {
        if (ex is OperationCanceledException && chartingHeartbeatLifetime.IsCancellationRequested)
        {
            throw;
        }

        chartingHeartbeatStatus = "计时同步暂时中断，将自动重试";
    }

    if (!windowCloseTeardownStarted && !windowClosePreflightComplete)
    {
        UpdateOnlineAccountUi();
    }
}

private void ReconcileChartingHeartbeatTimer()
{
    if (!windowCloseTeardownStarted && !windowClosePreflightComplete)
    {
        WindowsOnlineAccountHost? windowsOnlineAccountHost = onlineAccountHost;
        if (windowsOnlineAccountHost != null && windowsOnlineAccountHost.Session.IsAuthenticated)
        {
            if (chartingHeartbeatStatus.Equals("等待登录", StringComparison.Ordinal))
            {
                chartingHeartbeatStatus = "等待有效谱面操作";
            }

            if (!chartingHeartbeatTimer.IsEnabled)
            {
                chartingHeartbeatTimer.Start();
            }

            return;
        }
    }

    chartingHeartbeatTimer.Stop();
    chartingHeartbeatStatus = "等待登录";
}

private async Task<RoomChartRollbackOutcome> RollbackCurrentRoomChartAsync(Guid snapshotId, CancellationToken cancellationToken)
{
    RoomChartRollbackOutcome roomChartRollbackOutcome = await (roomChartSyncCoordinator ?? throw new InvalidOperationException("当前房间谱面尚未绑定编辑器。")).RollbackAsync(snapshotId, cancellationToken);
    if (!roomChartRollbackOutcome.IgnoredStaleResponse)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已回滚到快照“{0}”；安全快照 {1:D}", roomChartRollbackOutcome.RestoredSnapshot?.Name, roomChartRollbackOutcome.SafetySnapshotId));
    }

    return roomChartRollbackOutcome;
}

private async Task StopRoomChartSyncAsync(bool resetRetainedTab)
{
    await roomChartSyncStopGate.WaitAsync();
    try
    {
        RoomChartSyncCoordinator coordinator = roomChartSyncCoordinator;
        roomChartSyncCoordinator = null;
        DesktopChartTab tab = roomChartSyncTab;
        roomChartSyncTab = null;
        long retainedRoomNumber = roomChartSyncRoomNumber;
        string retainedRoomName = roomChartSyncRoomName;
        roomChartSyncRoomId = null;
        roomChartSyncRoomNumber = 0L;
        roomChartSyncRoomName = string.Empty;
        onlineCreatorMembers = Array.Empty<OnlineRoomMemberDto>();
        ResetRoomChatUi(reveal: false);
        timeline.SetOnlineCreators(new Dictionary<long, OnlineCreatorAppearance>());
        timeline.ResetRoomSyncDiagnostics();
        onlineRoundTripLatencyMs = null;
        roomChartFrozenResources = null;
        lastRoomChartPermissionErrorCode = null;
        roomAccessSnapshot = null;
        if (tab != null)
        {
            tab.Session.Changed -= RoomChartSession_Changed;
        }

        if (coordinator != null)
        {
            try
            {
                await coordinator.StopAsync();
            }
            finally
            {
                await coordinator.DisposeAsync();
            }
        }

        if (resetRetainedTab && tab != null && viewModel.Tabs.Contains(tab))
        {
            retainedOnlineRoomTabIds.Add(tab.Id);
            applyingRemoteChartSync = true;
            try
            {
                WindowsOnlineRoomRetentionPolicy.PrepareForLocalEditing(tab);
            }
            finally
            {
                applyingRemoteChartSync = false;
            }

            try
            {
                await viewModel.PreserveRoomDraftAsync(tab.Id, retainedRoomNumber, retainedRoomName, DateTimeOffset.UtcNow);
            }
            catch (Exception ex)when ((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException) ? true : false)
            {
                viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("房间留存仍在当前标签中，但持久缓存写入失败；请立即另存为：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
            }
        }

        UpdateUi();
    }
    finally
    {
        roomChartSyncStopGate.Release();
    }
}

private async Task RestoreRetainedOnlineRoomTabsAsync()
{
    foreach (DesktopChartTab item in viewModel.Tabs.Where((DesktopChartTab tab) => tab.IsPreservedRoomDraft || WindowsOnlineRoomRetentionPolicy.IsMaterializedRoomSource(tab.SourcePath, AvaloniaAppPaths.OnlineRoomDocumentRoot)))
    {
        retainedOnlineRoomTabIds.Add(item.Id);
        int num = WindowsOnlineRoomRetentionPolicy.PrepareForLocalEditing(item);
        if (item.IsPreservedRoomDraft && num == 0)
        {
            continue;
        }

        try
        {
            MainWindowViewModel mainWindowViewModel = viewModel;
            Guid id = item.Id;
            long roomNumber = (item.IsPreservedRoomDraft ? item.PreservedRoomNumber : 0);
            string roomName = (item.IsPreservedRoomDraft ? item.PreservedRoomName : string.Empty);
            DateTime? preservedRoomEndedUtc = item.PreservedRoomEndedUtc;
            DateTimeOffset endedUtc;
            if (preservedRoomEndedUtc.HasValue)
            {
                DateTime valueOrDefault = preservedRoomEndedUtc.GetValueOrDefault();
                endedUtc = new DateTimeOffset(DateTime.SpecifyKind(valueOrDefault, DateTimeKind.Utc));
            }
            else
            {
                endedUtc = DateTimeOffset.UtcNow;
            }

            await mainWindowViewModel.PreserveRoomDraftAsync(id, roomNumber, roomName, endedUtc);
        }
        catch (Exception ex)when ((ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException) ? true : false)
        {
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已恢复房间留存标签，但缓存清单升级失败；请另存为：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
        }
    }
}

private async Task PreserveRetainedRoomTabBeforeRebindAsync(string materializedChartPath, Guid roomId, CancellationToken cancellationToken)
{
    DesktopChartTab retainedTab = viewModel.Tabs.FirstOrDefault((DesktopChartTab tab) => retainedOnlineRoomTabIds.Contains(tab.Id) && PathsEqual(tab.SourcePath, materializedChartPath));
    if (retainedTab != null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string text = WindowsOnlineRoomRetentionPolicy.CreatePreservedSourcePath(AvaloniaAppPaths.OnlineRoomDocumentRoot, roomId, retainedTab.Id, DateTimeOffset.UtcNow, System.IO.Path.GetExtension(retainedTab.SourcePath));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(text));
        SaveRoundTripResult saveRoundTripResult = await viewModel.SaveAsAndVerifyAsync(retainedTab.Id, text);
        if ((object)saveRoundTripResult == null || !saveRoundTripResult.StructurallyEqual || retainedTab.Session.IsDirty)
        {
            throw new IOException("房间留存标签无法安全另存，已取消重新加入以避免覆盖本地成果。");
        }
    }
}

private async Task LeaveCurrentRoomBeforeShutdownAsync()
{
    OnlineAccountSession onlineAccountSession = onlineAccountHost?.Session;
    if (onlineAccountSession?.CurrentRoom == null)
    {
        return;
    }

    try
    {
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4.0));
        await onlineAccountSession.LeaveCurrentRoomAsync(timeout.Token);
    }
    catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is WebException || ex is OperationCanceledException || ex is JsonException) ? true : false)
    {
    }
}

private static void ReplaceOnlineChartContent(ChartDocument target, ChartDocument source)
{
    target.Metadata = source.Metadata.Clone();
    target.KeyCount = source.KeyCount;
    target.Notes.Clear();
    target.Notes.AddRange(source.Notes.Select((ChartNote note) => note.Clone()));
    target.Timing.Clear();
    target.Timing.AddRange(source.Timing.Select((TimingEvent timing) => timing.Clone()));
}

private static WindowsRoomAccessSnapshot CaptureRoomAccess(OnlineAccountSession account)
{
    return WindowsRoomPermissionNoticePolicy.Capture(account.CurrentMemberRole, account.CanEditMetadata, account.CanEditTiming, account.CanEditNotes);
}

private static string FormatRoomChartRetryDelay(DateTimeOffset? retryAt)
{
    TimeSpan timeSpan;
    if (retryAt.HasValue)
    {
        DateTimeOffset valueOrDefault = retryAt.GetValueOrDefault();
        timeSpan = valueOrDefault - DateTimeOffset.UtcNow;
    }
    else
    {
        timeSpan = TimeSpan.Zero;
    }

    TimeSpan timeSpan2 = timeSpan;
    if (timeSpan2 < TimeSpan.Zero)
    {
        timeSpan2 = TimeSpan.Zero;
    }

    if (!(timeSpan2.TotalMinutes >= 1.0))
    {
        return AvaloniaLocalizationCatalog.Format("{0:0} 秒", Math.Max(1.0, Math.Ceiling(timeSpan2.TotalSeconds)));
    }

    return AvaloniaLocalizationCatalog.Format("{0:0} 分钟", Math.Ceiling(timeSpan2.TotalMinutes));
}

private static string FormatTransferBytes(long bytes)
{
    if (bytes >= 1048576)
    {
        if (bytes >= 1073741824)
        {
            return $"{(double)bytes / 1073741824.0:0.##} GB";
        }

        return $"{(double)bytes / 1048576.0:0.##} MB";
    }

    if (bytes >= 1024)
    {
        return $"{(double)bytes / 1024.0:0.##} KB";
    }

    return $"{bytes} B";
}

private async Task PollRoomInvitationsAsync()
{
    OnlineAccountSession account = onlineAccountHost?.Session;
    if (roomInvitationPollInProgress || roomInvitationResponseInProgress || pendingRoomInvitation != null)
    {
        return;
    }

    if (account == null || !account.IsAuthenticated || windowClosePreflightInProgress || windowClosePreflightComplete)
    {
        return;
    }

    roomInvitationPollInProgress = true;
    try
    {
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12.0));
        OnlineRoomInvitationDto onlineRoomInvitationDto = (await account.GetRoomInvitationsAsync(timeout.Token)).OrderByDescending((OnlineRoomInvitationDto item) => item.ExpiresAt).FirstOrDefault();
        if (onlineRoomInvitationDto == null || account.CurrentRoom?.Id == onlineRoomInvitationDto.RoomId)
        {
            return;
        }

        pendingRoomInvitation = onlineRoomInvitationDto;
        roomInvitationTitleText.Text = AvaloniaLocalizationCatalog.Format("{0} 邀请你加入 {1}", onlineRoomInvitationDto.InviterDisplayName, onlineRoomInvitationDto.RoomName);
        roomInvitationDetailText.Text = "请选择同意或拒绝。";
        AutomationProperties.SetName(roomInvitationToast, AvaloniaLocalizationCatalog.Format("{0} 邀请你加入 {1}", onlineRoomInvitationDto.InviterDisplayName, onlineRoomInvitationDto.RoomName));
        AutomationProperties.SetHelpText(roomInvitationToast, AvaloniaLocalizationCatalog.Format("隐藏密码房可凭服务器邀请直接进入，不会显示或再次要求房间密码；contract={0}; width={1:0}; top={2:0}; right={3:0}; buttons={4:0}/{5:0}。", "windows-online-surface-r8", 360.0, 50.0, 14.0, 34.0, 8.0));
        SetRoomInvitationActionsEnabled(enabled: true);
        roomInvitationToast.IsVisible = true;
    }
    catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is WebException || ex is OperationCanceledException || ex is JsonException) ? true : false)
    {
    }
    finally
    {
        roomInvitationPollInProgress = false;
    }
}

private async Task RespondToPendingRoomInvitationAsync(string action)
{
    OnlineRoomInvitationDto invitation = pendingRoomInvitation;
    OnlineAccountSession account = onlineAccountHost?.Session;
    if (invitation == null)
    {
        return;
    }

    if (account == null || !account.IsAuthenticated || roomInvitationResponseInProgress)
    {
        return;
    }

    OnlineRoomSummaryDto currentRoom = account.CurrentRoom;
    if ((object)currentRoom != null && currentRoom.Id != invitation.RoomId)
    {
        roomInvitationDetailText.Text = "请先退出当前联机房间，再接受新邀请。";
        return;
    }

    bool flag = ((action == "join" || action == "watch") ? true : false);
    bool entersRoom = flag;
    if (!entersRoom && account.CurrentRoom?.Id == invitation.RoomId)
    {
        DismissPendingRoomInvitation();
        return;
    }

    roomInvitationResponseInProgress = true;
    SetRoomInvitationActionsEnabled(enabled: false);
    roomInvitationDetailText.Text = (entersRoom ? "正在验证邀请并准备房间资源…" : "正在取消邀请…");
    try
    {
        using CancellationTokenSource timeout = new CancellationTokenSource(entersRoom ? TimeSpan.FromMinutes(10.0) : TimeSpan.FromSeconds(20.0));
        if (entersRoom)
        {
            OnlineRoomSummaryDto currentRoom2 = account.CurrentRoom;
            if ((object)currentRoom2 != null)
            {
                DismissPendingRoomInvitation();
                await OpenConsumedRoomInvitationAsync(currentRoom2, timeout.Token);
                return;
            }
        }

        OnlineRoomInvitationResponseDto onlineRoomInvitationResponseDto = await account.RespondToRoomInvitationAsync(invitation.Id, action, timeout.Token);
        if (!entersRoom || onlineRoomInvitationResponseDto.Kind == OnlineRoomInvitationResponseKind.Canceled)
        {
            DismissPendingRoomInvitation();
            return;
        }

        OnlineRoomSummaryDto room = onlineRoomInvitationResponseDto.Room ?? account.CurrentRoom ?? throw new OnlineApiException("服务器没有返回受邀房间资料。");
        DismissPendingRoomInvitation();
        await OpenConsumedRoomInvitationAsync(room, timeout.Token);
    }
    catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is WebException || ex is OperationCanceledException || ex is JsonException) ? true : false)
    {
        if (entersRoom)
        {
            OnlineRoomSummaryDto currentRoom3 = account.CurrentRoom;
            if ((object)currentRoom3 != null && currentRoom3.Id == invitation.RoomId)
            {
                DismissPendingRoomInvitation();
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10.0));
                await OpenConsumedRoomInvitationAsync(currentRoom3, timeout.Token);
                return;
            }
        }

        roomInvitationDetailText.Text = ((ex is OperationCanceledException) ? "处理邀请超时，请重试。" : ex.Message);
    }
    finally
    {
        roomInvitationResponseInProgress = false;
        if (pendingRoomInvitation != null)
        {
            SetRoomInvitationActionsEnabled(enabled: true);
        }
    }
}

private async Task OpenConsumedRoomInvitationAsync(OnlineRoomSummaryDto room, CancellationToken cancellationToken)
{
    try
    {
        OnlineAccountSession onlineAccountSession = onlineAccountHost?.Session ?? throw new InvalidOperationException("联机账号会话不可用。");
        if (onlineAccountSession.CurrentRoom?.Id != room.Id)
        {
            throw new InvalidOperationException("当前房间已经变化，请重新进入。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已接受“{0}”邀请；无需房间密码，正在检查资源缓存。", room.Name));
        await ShowHostedOnlineCommunityAsync(onlineAccountSession, openAdminConfiguration: false, openCurrentRoom: true);
    }
    catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is WebException || ex is JsonException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is InvalidOperationException || ex is OperationCanceledException) ? true : false)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已经加入“{0}”，但资源准备暂时失败：{1}。邀请已安全消费，不会再次要求密码。", room.Name, AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private void DismissPendingRoomInvitation()
{
    pendingRoomInvitation = null;
    roomInvitationToast.IsVisible = false;
    roomInvitationTitleText.Text = string.Empty;
    roomInvitationDetailText.Text = string.Empty;
}

private void SetRoomInvitationActionsEnabled(bool enabled)
{
    if (roomInvitationCancelButton != null)
    {
        roomInvitationCancelButton.IsEnabled = enabled;
    }

    if (roomInvitationJoinButton != null)
    {
        roomInvitationJoinButton.IsEnabled = enabled;
    }
}

private async Task<WindowsAppUpdateSnapshot> RefreshAppUpdateSnapshotAsync(CancellationToken cancellationToken)
{
    if (onlineAccountHost == null)
    {
        throw new InvalidOperationException(onlineHostError ?? "联机配置无效，无法检查客户端版本。");
    }

    appUpdateSnapshot = WindowsAppUpdatePolicy.Evaluate(appConfiguration = await onlineAccountHost.Session.GetAppConfigurationAsync(cancellationToken), "1.3.0.0");
    ApplyAppConfiguration(appUpdateSnapshot);
    return appUpdateSnapshot;
}

private void ApplyAppConfiguration(WindowsAppUpdateSnapshot snapshot)
{
    aboutContentText.Text = (string.IsNullOrEmpty(snapshot.Configuration.AboutContent) ? "LumaChart Studio" : snapshot.Configuration.AboutContent);
    aboutVersionText.Text = AvaloniaLocalizationCatalog.Format("版本 {0}", "0.1.37");
    settingsVersionText.Text = WindowsAppUpdatePolicy.SettingsVersionText("1.3.0.0", snapshot);
}

private async Task RefreshUpdatePolicyAndShowAsync(bool blocksLogin, bool showWhenCurrent = true)
{
    if (!SimpleManiaProduct.OnlineEnabled)
    {
        if (githubUpdateChecking)
        {
            return;
        }

        githubUpdateChecking = true;
        viewModel.SetStatus("正在检查 GitHub Release…");
        try
        {
            int num = 0;
            try
            {
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10.0));
                using HttpClient client = new HttpClient();
                (WindowsAppUpdatePresentation, Uri) tuple = await GitHubReleaseUpdates.CheckAsync(client, timeout.Token);
                settingsVersionText.Text = tuple.Item1.VersionText;
                viewModel.SetStatus(tuple.Item1.Title);
                if (!windowCloseTeardownStarted)
                {
                    await ShowUpdatePromptAsync(tuple.Item1, tuple.Item2);
                }
            }
            catch (Exception ex)when ((ex is HttpRequestException || ex is OperationCanceledException || ex is JsonException) ? true : false)
            {
                num = 1;
            }

            if (num == 1)
            {
                viewModel.SetStatus("检查更新失败，可手动打开 GitHub 发布页。");
                if (!windowCloseTeardownStarted)
                {
                    await ShowUpdatePromptAsync(GitHubReleaseUpdates.Present("暂时无法检查更新", "无法连接 GitHub 或请求次数受限。可打开发布页查看，编辑器仍可正常离线使用。"), new Uri("https://github.com/Ist1na07/SimpleManiaEditor/releases"));
                }
            }

            return;
        }
        finally
        {
            githubUpdateChecking = false;
        }
    }

    try
    {
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
        WindowsAppUpdateSnapshot windowsAppUpdateSnapshot = await RefreshAppUpdateSnapshotAsync(timeout.Token);
        WindowsAppUpdatePresentation windowsAppUpdatePresentation = WindowsAppUpdatePolicy.Present(windowsAppUpdateSnapshot, blocksLogin, showWhenCurrent);
        if (windowsAppUpdatePresentation != null)
        {
            await ShowUpdatePromptAsync(windowsAppUpdatePresentation, windowsAppUpdateSnapshot.DownloadUri);
        }
    }
    catch (Exception ex2)when ((ex2 is OnlineApiException || ex2 is HttpRequestException || ex2 is OperationCanceledException || ex2 is JsonException || ex2 is InvalidOperationException) ? true : false)
    {
        await ShowUpdatePromptAsync(WindowsAppUpdatePolicy.Failure("1.3.0.0", blocksLogin), null);
    }
}

private Task ShowUpdatePromptAsync(WindowsAppUpdatePresentation presentation, Uri? downloadUri)
{
    ArgumentNullException.ThrowIfNull(presentation, "presentation");
    updateTitleText.Text = presentation.Title;
    updateVersionText.Text = presentation.VersionText;
    updateNoticeText.Text = presentation.Notice;
    updatePromptBlocksLogin = presentation.BlocksLogin;
    if (updateLaterButton != null)
    {
        updateLaterButton.Content = presentation.LaterButtonText;
    }

    if (updateDownloadButton != null)
    {
        updateDownloadButton.Content = presentation.DownloadButtonText;
        updateDownloadButton.IsEnabled = presentation.DownloadEnabled;
        updateDownloadButton.Tag = (presentation.DownloadEnabled ? downloadUri : null);
    }

    AutomationProperties.SetName(updateLayer, $"{presentation.Title}：{presentation.VersionText}：{presentation.Notice}");
    AutomationProperties.SetHelpText(updateLayer, $"BlocksLogin={presentation.BlocksLogin}; DownloadEnabled={presentation.DownloadEnabled}");
    if (updatePromptCompletion == null)
    {
        updatePromptCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    updateLayer.IsVisible = true;
    updateLaterButton?.Focus();
    return updatePromptCompletion.Task;
}

private void CloseUpdatePrompt()
{
    updateLayer.IsVisible = false;
    if (updateDownloadButton != null)
    {
        updateDownloadButton.Tag = null;
    }

    TaskCompletionSource<bool>? taskCompletionSource = updatePromptCompletion;
    updatePromptCompletion = null;
    taskCompletionSource?.TrySetResult(result: true);
    if (!updatePromptBlocksLogin)
    {
        timeline.Focus();
    }
}

private void OpenCurrentUpdateDownload()
{
    if (updateDownloadButton?.Tag is Uri uri)
    {
        OpenExternalUrl(uri.AbsoluteUri);
    }
}

private async Task ShowAboutAsync()
{
    aboutVersionText.Text = AvaloniaLocalizationCatalog.Format("版本 {0}", "0.1.37");
    aboutContentText.Text = ((!SimpleManiaProduct.OnlineEnabled) ? "SimpleManiaEditor\n面向 osu!mania / 7K 的本地编辑器\nCopyright © 2026 Ist1na\n基于 LumaChart Studio（MIT）改造；保留上游与第三方许可证。" : (appConfiguration?.AboutContent ?? "SimpleManiaEditor\n面向 osu!mania / 7K 的本地编辑器\nCopyright © 2026 Ist1na\n基于 LumaChart Studio（MIT）改造；保留上游与第三方许可证。"));
    if (aboutAdminButton != null)
    {
        aboutAdminButton.IsVisible = (onlineAccountHost?.Session.IsAuthenticated ?? false) && onlineAccountHost.Session.CanUseAdminApi;
    }

    aboutLayer.IsVisible = true;
    if (onlineAccountHost == null)
    {
        return;
    }

    try
    {
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6.0));
        await RefreshAppUpdateSnapshotAsync(timeout.Token);
        if (aboutAdminButton != null)
        {
            aboutAdminButton.IsVisible = onlineAccountHost.Session.IsAuthenticated && onlineAccountHost.Session.CanUseAdminApi;
        }
    }
    catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is OperationCanceledException || ex is JsonException) ? true : false)
    {
    }
}

private async Task ShowIssueReportAsync()
{
    if (!SimpleManiaProduct.OnlineEnabled)
    {
        OpenExternalUrl("https://github.com/Ist1na07/SimpleManiaEditor/issues/new");
        return;
    }

    WindowsDisplayScaleSettings currentSettings = displayScaleController.CurrentSettings;
    string environmentText = WindowsIssueReportPolicy.BuildEnvironment("1.3.0.0", RenderScaling, currentSettings.UiScale, currentSettings.FontScale, ClientSize.Width, ClientSize.Height, onlineAccountHost?.Session.IsAuthenticated ?? false, onlineAccountHost?.Session.CurrentRoom != null);
    await new WindowsIssueReportDialog(onlineAccountHost?.Session, environmentText, currentSettings.UiScale, currentSettings.FontScale).ShowDialog<OnlineIssueReportDto>(this);
    timeline.Focus();
}

private void CloseAbout()
{
    aboutLayer.IsVisible = false;
    timeline.Focus();
}

private async Task OpenOwnerAppConfigurationAsync()
{
    WindowsOnlineAccountHost? windowsOnlineAccountHost = onlineAccountHost;
    if (windowsOnlineAccountHost == null || !windowsOnlineAccountHost.Session.IsAuthenticated || !onlineAccountHost.Session.CanUseAdminApi)
    {
        return;
    }

    aboutLayer.IsVisible = false;
    try
    {
        await ShowHostedOnlineCommunityAsync(onlineAccountHost.Session, openAdminConfiguration: true);
    }
    catch (Exception ex)
    {
        if (!windowCloseTeardownStarted)
        {
            UpdateOnlineAccountUi();
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("账号功能不可用：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
        }
    }
}

private void OpenExternalUrl(string value)
{
    Uri uri = WindowsExternalUriPolicy.Resolve(value);
    if (!(uri == null))
    {
        OpenExternalUrlAsync(uri);
    }
}

private async Task OpenExternalUrlAsync(Uri uri)
{
    try
    {
        await PlatformExternalUriServices.OpenAsync(uri);
    }
    catch (Exception ex)when ((ex is InvalidOperationException || ex is Win32Exception) ? true : false)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("无法打开链接：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
    }
}

private async Task<bool> EnsureLoginVersionAllowedAsync()
{
    if (onlineAccountHost == null)
    {
        return false;
    }

    try
    {
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
        WindowsAppUpdateSnapshot windowsAppUpdateSnapshot = await RefreshAppUpdateSnapshotAsync(timeout.Token);
        if (!windowsAppUpdateSnapshot.BlocksLogin)
        {
            return true;
        }

        WindowsAppUpdatePresentation windowsAppUpdatePresentation = WindowsAppUpdatePolicy.Present(windowsAppUpdateSnapshot, blocksLogin: true, showWhenCurrent: true);
        if (windowsAppUpdatePresentation != null)
        {
            await ShowUpdatePromptAsync(windowsAppUpdatePresentation, windowsAppUpdateSnapshot.DownloadUri);
        }

        return false;
    }
    catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is OperationCanceledException || ex is JsonException) ? true : false)
    {
        return true;
    }
}

private async Task RestoreOnlineAccountAsync()
{
    if (onlineAccountHost == null)
    {
        UpdateOnlineAccountUi();
        return;
    }

    onlineRestoreCancellation?.Cancel();
    onlineRestoreCancellation?.Dispose();
    CancellationTokenSource restoreCancellation = (onlineRestoreCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8.0)));
    titleUserNameText.Text = "账号";
    titleUserStateText.Text = "恢复中";
    try
    {
        await onlineAccountHost.Session.TryRestoreAsync(restoreCancellation.Token);
    }
    catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is WebException || ex is OperationCanceledException || ex is JsonException) ? true : false)
    {
        if (!(ex is OperationCanceledException) || !restoreCancellation.IsCancellationRequested)
        {
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("账号会话暂时无法恢复：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
        }
    }
    finally
    {
        UpdateOnlineAccountUi();
    }
}

private async Task RestoreStartupOnlineAccountAsync(bool showStartupChoice)
{
    if (onlineAccountHost == null)
    {
        UpdateOnlineAccountUi();
        return;
    }

    WindowsAppUpdateSnapshot startupSnapshot = null;
    try
    {
        titleUserNameText.Text = "账号";
        titleUserStateText.Text = "检查版本";
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
        startupSnapshot = await RefreshAppUpdateSnapshotAsync(timeout.Token);
    }
    catch (Exception ex)when ((ex is OnlineApiException || ex is HttpRequestException || ex is OperationCanceledException || ex is JsonException) ? true : false)
    {
        viewModel.SetStatus("暂时无法检查客户端版本，可重试登录或离线使用。");
    }

    if (startupSnapshot?.BlocksLogin ?? false)
    {
        startupAccountChoiceHandled = true;
        titleUserNameText.Text = "Guest";
        titleUserStateText.Text = "需要更新";
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("需要更新到 {0} 才能登录，也可离线使用。", startupSnapshot.Configuration.MinimumLoginVersion));
        WindowsAppUpdatePresentation windowsAppUpdatePresentation = WindowsAppUpdatePolicy.Present(startupSnapshot, blocksLogin: true, showWhenCurrent: true);
        if (windowsAppUpdatePresentation != null)
        {
            await ShowUpdatePromptAsync(windowsAppUpdatePresentation, startupSnapshot.DownloadUri);
        }

        UpdateOnlineAccountUi();
        return;
    }

    await RestoreOnlineAccountAsync();
    if (startupSnapshot?.IsUpdateAvailable ?? false)
    {
        WindowsAppUpdatePresentation windowsAppUpdatePresentation2 = WindowsAppUpdatePolicy.Present(startupSnapshot, blocksLogin: false, showWhenCurrent: false);
        if (windowsAppUpdatePresentation2 != null)
        {
            await ShowUpdatePromptAsync(windowsAppUpdatePresentation2, startupSnapshot.DownloadUri);
        }
    }

    if (!startupAccountChoiceHandled && showStartupChoice && WindowsOnlineSurfaceParityPolicy.RequiresStartupAccountChoice(evidenceChartMode, onlineAccountHost.Session.IsAuthenticated))
    {
        startupAccountChoiceHandled = true;
        bool flag = await ShowHostedOnlineAccountAsync(onlineAccountHost.Session);
        UpdateOnlineAccountUi();
        if (flag && onlineAccountHost.Session.IsAuthenticated)
        {
            OnlineUserProfileDto currentUser = onlineAccountHost.Session.CurrentUser;
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已登录 {0}", currentUser?.DisplayName ?? currentUser?.Username ?? AvaloniaLocalizationCatalog.Text("账号")));
        }
        else
        {
            viewModel.SetStatus("已选择离线使用");
        }
    }
}

private async Task ShowOnlineAccountAsync(OnlineCommunityHostedSection requestedSection = OnlineCommunityHostedSection.Profile)
{
    HideFriendsPanel();
    startupAccountChoiceHandled = true;
    pendingOnlineCommunitySection = requestedSection;
    if ((activeHostedOnlineCommunitySurface?.NavigateHosted(requestedSection) ?? false) || onlineSurfaceCompletion != null || onlineAccountEntryInProgress)
    {
        return;
    }

    if (onlineAccountHost == null)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("账号功能不可用：{0}", onlineHostError ?? AvaloniaLocalizationCatalog.Text("联机配置无效")));
        return;
    }

    onlineAccountEntryInProgress = true;
    try
    {
        if (!onlineAccountHost.Session.IsAuthenticated)
        {
            bool flag = await ShowHostedOnlineAccountAsync(onlineAccountHost.Session);
            UpdateOnlineAccountUi();
            if (!flag || !onlineAccountHost.Session.IsAuthenticated)
            {
                return;
            }

            OnlineUserProfileDto currentUser = onlineAccountHost.Session.CurrentUser;
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已登录 {0}", currentUser?.DisplayName ?? currentUser?.Username ?? AvaloniaLocalizationCatalog.Text("账号")));
        }

        if (onlineAccountHost.Session.IsAuthenticated)
        {
            await ShowHostedOnlineCommunityAsync(onlineAccountHost.Session, openAdminConfiguration: false, openCurrentRoom: false, pendingOnlineCommunitySection);
        }

        UpdateOnlineAccountUi();
        if (onlineAccountHost.Session.IsAuthenticated)
        {
            OnlineUserProfileDto currentUser2 = onlineAccountHost.Session.CurrentUser;
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("账号资料已同步：{0}", currentUser2?.DisplayName ?? currentUser2?.Username ?? AvaloniaLocalizationCatalog.Text("账号")));
        }
        else if (!onlineAccountHost.Session.IsAuthenticated)
        {
            viewModel.SetStatus("已退出登录");
        }
    }
    catch (Exception ex)
    {
        if (!windowCloseTeardownStarted)
        {
            UpdateOnlineAccountUi();
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("账号功能不可用：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
        }
    }
    finally
    {
        onlineAccountEntryInProgress = false;
    }
}

private async Task<bool> ShowHostedOnlineAccountAsync(OnlineAccountSession session)
{
    TaskCompletionSource<bool> completion = BeginOnlineSurface();
    if (completion == null)
    {
        return false;
    }

    OnlineAccountDialog surface = null;
    try
    {
        if (!(await EnsureLoginVersionAllowedAsync()))
        {
            return false;
        }

        if (completion.Task.IsCompleted)
        {
            return false;
        }

        surface = new OnlineAccountDialog(session, (bool result) =>
        {
            completion.TrySetResult(result);
        }, async (OnlineApiException _) =>
        {
            await RefreshUpdatePolicyAndShowAsync(blocksLogin: true);
            completion.TrySetResult(result: false);
        });
        Control item = surface.DetachHostedContent();
        Grid grid = new Grid();
        grid.Children.Add(item);
        Button button = PrimaryButton("不登录，离线使用  →", (object? _, RoutedEventArgs _) =>
        {
            completion.TrySetResult(result: false);
        });
        ApplyWpfPrimaryButtonStyle(button);
        button.Width = 190.0;
        button.Height = 46.0;
        button.FontSize = 13.0;
        button.FontWeight = FontWeight.DemiBold;
        button.Background = new SolidColorBrush(Color.Parse("#38676C"));
        button.BorderBrush = new SolidColorBrush(Color.Parse("#79B8BD"));
        button.BorderThickness = new Thickness(1.5);
        button.HorizontalAlignment = HorizontalAlignment.Right;
        button.VerticalAlignment = VerticalAlignment.Bottom;
        button.Padding = new Thickness(14.0, 7.0);
        AutomationProperties.SetAutomationId(button, "OnlineAccount.ContinueOffline");
        Border item2 = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0.0, 0.0, 24.0, 22.0),
            Padding = new Thickness(5.0),
            Background = new SolidColorBrush(Color.Parse("#E6111315")),
            BorderBrush = new SolidColorBrush(Color.Parse("#475B60")),
            BorderThickness = new Thickness(1.0),
            CornerRadius = new CornerRadius(7.0),
            Child = button
        };
        grid.Children.Add(item2);
        ShowOnlineSurface(grid, "OnlineAccount.Layer");
        Task activation = surface.ActivateHostedAsync();
        if (await Task.WhenAny(activation, completion.Task) == completion.Task)
        {
            activation.ContinueWith((Task task) => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return await completion.Task;
        }

        await activation;
        return await completion.Task;
    }
    finally
    {
        surface?.DisposeHostedSurface();
        EndOnlineSurface(completion);
    }
}

private async Task<bool> ShowHostedOnlineCommunityAsync(OnlineAccountSession session, bool openAdminConfiguration = false, bool openCurrentRoom = false, OnlineCommunityHostedSection initialSection = OnlineCommunityHostedSection.Profile)
{
    TaskCompletionSource<bool> completion = BeginOnlineSurface();
    if (completion == null)
    {
        return false;
    }

    OnlineCommunityDialog surface = null;
    try
    {
        surface = (activeHostedOnlineCommunitySurface = new OnlineCommunityDialog(session, PrepareCurrentOnlineRoomUploadAsync, PrepareCurrentOnlineRoomDownloadAsync, OpenCurrentOnlineRoomChartAsync, RollbackCurrentRoomChartAsync, (bool result) =>
        {
            completion.TrySetResult(result);
        }, this, openAdminConfiguration));
        Control control = surface.DetachHostedContent();
        Grid grid = new Grid
        {
            RowDefinitions = new RowDefinitions($"{58.0},*"),
            Background = new SolidColorBrush(Color.Parse("#111315"))
        };
        Border border = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#171A1D")),
            BorderBrush = new SolidColorBrush(Color.Parse("#30363A")),
            BorderThickness = new Thickness(0.0, 0.0, 0.0, 1.0),
            Padding = new Thickness(20.0, 0.0)
        };
        Grid grid2 = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        StackPanel stackPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        stackPanel.Children.Add(new TextBlock { Text = "联机写谱", FontSize = 22.0, FontWeight = FontWeight.DemiBold, Foreground = new SolidColorBrush(Color.Parse("#E2E6E7")) });
        grid2.Children.Add(stackPanel);
        Button button = PrimaryButton("返回编辑器", (object? _, RoutedEventArgs _) =>
        {
            completion.TrySetResult(result: true);
        });
        button.Padding = new Thickness(14.0, 7.0);
        ApplyWpfToolButtonStyle(button);
        AutomationProperties.SetAutomationId(button, "OnlineCommunity.Header.Close");
        Grid.SetColumn(button, 1);
        grid2.Children.Add(button);
        border.Child = grid2;
        grid.Children.Add(border);
        Grid.SetRow(control, 1);
        grid.Children.Add(control);
        ShowOnlineSurface(grid, "OnlineCommunity.Layer");
        await surface.ActivateHostedAsync(openCurrentRoom, initialSection);
        return await completion.Task;
    }
    finally
    {
        surface?.DisposeHostedSurface();
        if (activeHostedOnlineCommunitySurface == surface)
        {
            activeHostedOnlineCommunitySurface = null;
        }

        EndOnlineSurface(completion);
    }
}

private TaskCompletionSource<bool>? BeginOnlineSurface()
{
    if (onlineSurfaceCompletion != null)
    {
        return null;
    }

    if (spacePanGestureState.Cancel())
    {
        timeline.EndSpacePanModifier();
    }

    oneShotShortcutState.Clear();
    return onlineSurfaceCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
}

private void ShowOnlineSurface(Control content, string automationId)
{
    onlineSurfaceContent.Children.Clear();
    AutomationBorder automationBorder = new AutomationBorder
    {
        Child = content
    };
    AutomationProperties.SetAutomationId(automationBorder, automationId);
    onlineSurfaceContent.Children.Add(automationBorder);
    onlineSurfaceLayer.IsVisible = true;
}

private void EndOnlineSurface(TaskCompletionSource<bool> completion)
{
    if (onlineSurfaceCompletion == completion)
    {
        onlineSurfaceLayer.IsVisible = false;
        onlineSurfaceContent.Children.Clear();
        onlineSurfaceCompletion = null;
        UpdateOnlineAccountUi();
    }
}

private void ReconcileFollowNotificationSession()
{
    WindowsOnlineAccountHost? windowsOnlineAccountHost = onlineAccountHost;
    long num = ((windowsOnlineAccountHost == null || !windowsOnlineAccountHost.Session.IsAuthenticated) ? 0 : (onlineAccountHost.Session.CurrentUser?.Id ?? 0));
    if (followNotificationUserId != num)
    {
        followNotificationUserId = num;
        followNotificationRetryNotBefore = DateTimeOffset.MinValue;
        ResetFollowNotificationToastUi();
        if (num > 0)
        {
            PollFollowNotificationsAsync();
        }
    }
}

private async Task PollFollowNotificationsAsync()
{
    OnlineAccountSession onlineAccountSession = onlineAccountHost?.Session;
    if (onlineAccountSession == null || !onlineAccountSession.IsAuthenticated || followNotificationPollInProgress || windowCloseTeardownStarted || DateTimeOffset.UtcNow < followNotificationRetryNotBefore)
    {
        return;
    }

    followNotificationPollInProgress = true;
    try
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(followNotificationPollingCancellation.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5.0));
        await onlineAccountSession.PollNotificationsAsync(timeout.Token);
    }
    catch (OnlineApiException ex)when (ex.StatusCode == HttpStatusCode.TooManyRequests)
    {
        TimeSpan valueOrDefault = ex.RetryAfter.GetValueOrDefault(TimeSpan.FromMilliseconds(5000.0));
        followNotificationRetryNotBefore = DateTimeOffset.UtcNow + ((valueOrDefault > TimeSpan.Zero) ? valueOrDefault : TimeSpan.FromMilliseconds(5000.0));
    }
    catch (Exception ex2)when ((ex2 is OnlineApiException || ex2 is HttpRequestException || ex2 is OperationCanceledException || ex2 is JsonException) ? true : false)
    {
    }
    finally
    {
        followNotificationPollInProgress = false;
    }
}

private void OnlineAccountSession_Changed(object? sender, EventArgs eventArgs)
{
    if (global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
    {
        ReconcileFollowNotificationSession();
        ReconcileChartingHeartbeatTimer();
        UpdateOnlineAccountUi();
        ReconcileOnlineRoomBindingAsync();
        PollRoomInvitationsAsync();
        return;
    }

    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        ReconcileFollowNotificationSession();
        ReconcileChartingHeartbeatTimer();
        UpdateOnlineAccountUi();
        ReconcileOnlineRoomBindingAsync();
        PollRoomInvitationsAsync();
    });
}

private async Task ReconcileOnlineRoomBindingAsync()
{
    long generation = ++onlineRoomBindingReconcileGeneration;
    Guid? guid = onlineAccountHost?.Session.CurrentRoom?.Id;
    if (!guid.HasValue)
    {
        await StopOnlineRoomPresenceLoopAsync();
    }
    else
    {
        Guid valueOrDefault = guid.GetValueOrDefault();
        await EnsureOnlineRoomPresenceLoopAsync(valueOrDefault);
    }

    if (generation != onlineRoomBindingReconcileGeneration)
    {
        return;
    }

    guid = onlineAccountHost?.Session.CurrentRoom?.Id;
    if (roomChartSyncRoomId.HasValue)
    {
        if (guid == roomChartSyncRoomId)
        {
            ApplyOnlineRoomPermissions();
            return;
        }

        await StopRoomChartSyncAsync(resetRetainedTab: true);
        viewModel.SetStatus("房间已关闭或成员资格已结束；保留标签仍可本地编辑和保存。");
    }
}

private void UpdateOnlineAccountUi()
{
    if (onlineAccountButton == null)
    {
        return;
    }

    if (!SimpleManiaProduct.OnlineEnabled)
    {
        titleUserNameText.Text = "本地创作";
        titleUserStateText.Text = "离线可用";
        onlineAccountButton.IsEnabled = false;
        ToolTip.SetTip(onlineAccountButton, "本地谱面编辑，无需账号");
        return;
    }

    ReconcileChartingHeartbeatTimer();
    if (onlineAccountHost == null)
    {
        titleUserNameText.Text = "配置错误";
        titleUserStateText.Text = "联机不可用";
        BindRemoteAvatar(titleUserAvatar, null, "Guest");
        ToolTip.SetTip(onlineAccountButton, onlineHostError);
        return;
    }

    OnlineUserProfileDto currentUser = onlineAccountHost.Session.CurrentUser;
    if (!onlineAccountHost.Session.IsAuthenticated)
    {
        DismissPendingRoomInvitation();
        HideFriendsPanel();
    }
    else if (friendsPanel.IsVisible && friendsList.ItemsSource is IEnumerable<OnlinePlayerProfileDto> source)
    {
        OnlinePlayerProfileDto[] itemsSource = source.ToArray();
        friendsList.ItemsSource = null;
        friendsList.ItemsSource = itemsSource;
    }

    titleUserNameText.Text = ((!onlineAccountHost.Session.IsAuthenticated) ? "Guest" : (currentUser?.DisplayName ?? currentUser?.Username ?? "账号"));
    titleUserStateText.Text = (onlineAccountHost.Session.IsAuthenticated ? "在线" : "离线");
    BindRemoteAvatar(titleUserAvatar, (!onlineAccountHost.Session.IsAuthenticated) ? null : currentUser?.AvatarKey, titleUserNameText.Text);
    AutomationProperties.SetName(onlineAccountButton, onlineAccountHost.Session.IsAuthenticated ? AvaloniaLocalizationCatalog.Format("账号 · {0}", titleUserNameText.Text) : "账号 · 登录");
    ToolTip.SetTip(onlineAccountButton, onlineAccountHost.Session.IsAuthenticated ? $"{currentUser?.Username} · {currentUser?.Role} · {chartingHeartbeatStatus}" : "登录 LumaChart 账号");
}

private async Task PickAndOpenChartAsync()
{
    try
    {
        await PickAndOpenChartCoreAsync();
    }
    catch (Exception ex)
    {
        if (!windowCloseTeardownStarted)
        {
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("打开谱面失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
        }
    }
}

private async Task PickAndOpenChartCoreAsync()
{
    IPlatformFileImportService current = PlatformFileImportServices.Current;
    if (current != null)
    {
        string text = await current.PickChartFileAsync();
        if (!string.IsNullOrWhiteSpace(text))
        {
            await OpenChartOrArchiveAsync(text);
        }

        return;
    }

    IStorageFolder suggestedStartLocation = await ResolveEvidenceFilePickerStartLocationAsync();
    foreach (IStorageFile item in await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "打开 Malody / osu! 谱面或谱包", AllowMultiple = true, SuggestedStartLocation = suggestedStartLocation, FileTypeFilter = new FilePickerFileType[4] { new FilePickerFileType("LumaChart 支持的谱面与谱包") { Patterns = new string[4] { "*.mc", "*.osu", "*.mcz", "*.osz" } }, new FilePickerFileType("Malody MC") { Patterns = new string[]
{
    "*.mc"
} }, new FilePickerFileType("osu!mania") { Patterns = new string[]
{
    "*.osu"
} }, new FilePickerFileType("Malody / osu! 谱包") { Patterns = new string[2] { "*.mcz", "*.osz" } } } }))
    {
        string text2 = item.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(text2))
        {
            await OpenChartOrArchiveAsync(text2);
        }
    }
}

private async Task<string?> PickLocalFileAsync(FilePickerOpenOptions options, PickedFileRetention retention, CancellationToken cancellationToken = default(CancellationToken))
{
    return await PlatformPickedFiles.ResolveLocalPathAsync((await StorageProvider.OpenFilePickerAsync(options)).FirstOrDefault(), retention, cancellationToken);
}

private async Task<IStorageFolder?> ResolveEvidenceFilePickerStartLocationAsync()
{
    if (!evidenceChartMode || evidenceFilePickerStartDirectory == null)
    {
        return null;
    }

    return (await StorageProvider.TryGetFolderFromPathAsync(evidenceFilePickerStartDirectory)) ?? throw new InvalidOperationException("Evidence file-picker start directory is unavailable: " + evidenceFilePickerStartDirectory);
}

private async Task<IStorageFolder?> ResolveFolderPickerStartLocationAsync(string evidenceOperation)
{
    ReportLibraryLauncherEvidencePhase(evidenceOperation + ":resolve-start-location");
    string path = ((evidenceFocusLibraryLauncher && evidenceFilePickerStartDirectory != null) ? evidenceFilePickerStartDirectory : viewModel.WorkspaceRoot);
    if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
    {
        return null;
    }

    IStorageFolder storageFolder = await StorageProvider.TryGetFolderFromPathAsync(path);
    ReportLibraryLauncherEvidencePhase($"{evidenceOperation}:start-location-resolved:{storageFolder != null}");
    if (storageFolder != null || !evidenceFocusLibraryLauncher)
    {
        return storageFolder;
    }

    throw new InvalidOperationException("Evidence folder-picker start directory is unavailable: " + path);
}

private void ReportLibraryLauncherEvidencePhase(string phase)
{
    if (evidenceFocusLibraryLauncher)
    {
        AutomationProperties.SetHelpText(libraryStatusText, "LibraryLauncherEvidence=" + phase);
    }
}

private async Task<string?> PickInAppSaveAsPathAsync(DesktopChartTab tab, string inAppSaveAsRoot, string defaultExtension)
{
    string directory = InAppSaveAsPolicy.ResolveDirectory(tab.SourcePath, inAppSaveAsRoot);
    Directory.CreateDirectory(directory);
    string folderLabel = System.IO.Path.GetFileName(directory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
    string path;
    while (true)
    {
        InAppSaveAsResult inAppSaveAsResult = await new InAppSaveAsDialog(System.IO.Path.GetFileNameWithoutExtension(tab.SourcePath), "." + defaultExtension, AvaloniaLocalizationCatalog.Format("保存到应用曲库：{0}", folderLabel), displayScaleSettings.UiScale, displayScaleSettings.FontScale).ShowDialog<InAppSaveAsResult>(this);
        if (inAppSaveAsResult == null)
        {
            return null;
        }

        string text = InAppSaveAsPolicy.SanitizeFileName(inAppSaveAsResult.FileName);
        if (text == null)
        {
            await ShowSettingsMessageAsync("文件名无效", "请输入不含路径分隔符的文件名。");
            continue;
        }

        path = System.IO.Path.Combine(directory, text + inAppSaveAsResult.Extension);
        if (PathsEqual(path, tab.SourcePath))
        {
            await ShowSettingsMessageAsync("无法另存", "所选名称与当前谱面相同。请换一个名称，或直接使用保存。");
            continue;
        }

        bool flag = File.Exists(path);
        if (flag)
        {
            flag = !(await ShowSettingsConfirmationAsync("覆盖已有文件", AvaloniaLocalizationCatalog.Format("{0}{1} 已存在，继续将覆盖它。", text, inAppSaveAsResult.Extension), "覆盖", dangerous: true));
        }

        if (!flag)
        {
            break;
        }
    }

    return path;
}

private Task<SaveRoundTripResult?> PickAndSaveAsAsync(Guid? tabId = null, bool preflightComplete = false)
{
    Guid? guid = tabId ?? viewModel.ActiveTab?.Id;
    if (guid.HasValue)
    {
        Guid id = guid.GetValueOrDefault();
        return RunExclusiveSaveAsync(() => PickAndSaveAsCoreAsync(id, preflightComplete));
    }

    return Task.FromResult<SaveRoundTripResult>(null);
}

private async Task<SaveRoundTripResult?> PickAndSaveAsCoreAsync(Guid? tabId = null, bool preflightComplete = false)
{
    DesktopChartTab tab = (tabId.HasValue ? viewModel.Tabs.FirstOrDefault((DesktopChartTab candidate) => candidate.Id == tabId.Value) : viewModel.ActiveTab);
    if (tab == null)
    {
        return null;
    }

    if (viewModel.ActiveTab != tab)
    {
        viewModel.Activate(tab.Id);
    }

    if (!ApplyMetadataEditor(enforceCommunityRules: true))
    {
        string text = lastMetadataApplyError;
        if (text != null && text.Length > 0)
        {
            await ShowSettingsMessageAsync("保存失败", AvaloniaLocalizationCatalog.Format("谱面信息未应用：{0}", AvaloniaLocalizationCatalog.Text(text)));
        }

        return null;
    }

    string extension = System.IO.Path.GetExtension(tab.SourcePath);
    string defaultExtension = (extension.Equals(".osu", StringComparison.OrdinalIgnoreCase) ? "osu" : "mc");
    string text2 = PlatformFileExportServices.Current?.InAppSaveAsRoot;
    string path;
    if (text2 != null)
    {
        path = await PickInAppSaveAsPathAsync(tab, text2, defaultExtension);
    }
    else
    {
        FilePickerFileType mcType = new FilePickerFileType("Malody MC")
        {
            Patterns = new string[]
{
    "*.mc"
}
        };
        FilePickerFileType osuType = new FilePickerFileType("osu!mania")
        {
            Patterns = new string[]
{
    "*.osu"
}
        };
        FilePickerFileType suggestedType = ((defaultExtension == "osu") ? osuType : mcType);
        IStorageFolder suggestedStartLocation = null;
        string directoryName = System.IO.Path.GetDirectoryName(tab.SourcePath);
        if (!string.IsNullOrWhiteSpace(directoryName) && Directory.Exists(directoryName))
        {
            suggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(directoryName);
        }

        path = (await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "另存谱面", SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(tab.SourcePath), SuggestedStartLocation = suggestedStartLocation, DefaultExtension = defaultExtension, FileTypeChoices = new FilePickerFileType[2] { mcType, osuType }, SuggestedFileType = suggestedType, ShowOverwritePrompt = true }))?.TryGetLocalPath();
    }

    if (string.IsNullOrWhiteSpace(path))
    {
        return null;
    }

    bool flag = !preflightComplete;
    if (flag)
    {
        flag = !(await EnsureChartCanSaveAsync(tab));
    }

    if (flag)
    {
        return null;
    }

    string previousSourcePath = tab.SourcePath;
    int previousChartOffset = keySoundSettings.GetChartOffset(previousSourcePath);
    try
    {
        SaveRoundTripResult saveRoundTripResult = await viewModel.SaveAsAndVerifyAsync(tab.Id, path);
        if (saveRoundTripResult?.StructurallyEqual ?? false)
        {
            bool flag2 = retainedOnlineRoomTabIds.Remove(tab.Id);
            if (!string.Equals(previousSourcePath, tab.SourcePath, StringComparison.OrdinalIgnoreCase) && previousChartOffset != 0)
            {
                keySoundSettings = keySoundSettings.SetChartOffset(tab.SourcePath, previousChartOffset).SetChartOffset(previousSourcePath, 0.0);
                keySoundSettingsDirty = true;
                ScheduleKeySoundSettingsSave();
            }

            if (IsCurrentKeySoundTab(tab))
            {
                RefreshKeySoundSettingsUi();
                ObserveKeySoundDocumentState(tab);
                ScheduleKeySoundPlanRebuild();
            }

            if (flag2)
            {
                RebuildTabs();
            }
        }

        return saveRoundTripResult;
    }
    catch (Exception ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("另存谱面失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
        WindowsNativeMessageBox.Show(this, WindowsLibraryLauncherParityPolicy.CreateOperationError("无法保存原谱面；软件旁缓存已保留", ex));
        return null;
    }
}

private async Task PickAndExportChartAsync(CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    DesktopChartTab tab = viewModel.ActiveTab;
    if (tab == null)
    {
        return;
    }

    ChartExportDialog optionsDialog = new ChartExportDialog(displayScaleSettings.UiScale, displayScaleSettings.FontScale, tab.Session.Document.Metadata.OverallDifficulty, tab.Session.Document.Metadata.HpDrainRate);
    if (!(await optionsDialog.ShowDialog<bool>(this)))
    {
        return;
    }

    ChartExportOptions options = optionsDialog.Options;
    if ((object)options == null)
    {
        return;
    }

    cancellationToken.ThrowIfCancellationRequested();
    ChartExportPlan chartExportPlan = ChartExportPlanner.Create(options);
    string extension = chartExportPlan.DestinationExtension;
    ChartExportFormat format = options.Format;
    ChartExportMode mode = options.Mode;
    string text;
    if (format != ChartExportFormat.Malody)
    {
        text = ((format != ChartExportFormat.OsuMania || mode != ChartExportMode.SingleChart) ? "osu! 谱包" : "osu!mania 谱面");
    }
    else
    {
        text = ((mode != ChartExportMode.SingleChart) ? "Malody 谱包" : "Malody 谱面");
    }

    string name = text;
    FilePickerFileType fileType = new FilePickerFileType(name)
    {
        Patterns = new string[]
{
    "*" + extension
}
    };
    string suggestedName = ChartExportFileNamePolicy.SuggestedFileName(tab.Session.Document, extension);
    IPlatformFileExportService current = PlatformFileExportServices.Current;
    if (current != null)
    {
        await ExportThroughPlatformDestinationAsync(current, tab, options, suggestedName, extension, cancellationToken);
        return;
    }

    IStorageFolder suggestedStartLocation = await ResolveChartExportStartLocationAsync(tab.SourcePath);
    string text2 = (await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "选择导出位置", SuggestedFileName = suggestedName, SuggestedStartLocation = suggestedStartLocation, DefaultExtension = extension.Substring(1), FileTypeChoices = new FilePickerFileType[]
{
    fileType
}, SuggestedFileType = fileType, ShowOverwritePrompt = true }))?.TryGetLocalPath();
    if (string.IsNullOrWhiteSpace(text2))
    {
        if (exportButton != null)
        {
            AutomationProperties.SetHelpText(exportButton, "导出文件选择器未返回可用的本地写入路径");
        }

        return;
    }

    WindowsChartExportDestinationResolution destinationResolution = WindowsChartExportDialogParityPolicy.ResolveDestinationPath(text2, extension);
    if (exportButton != null)
    {
        AutomationProperties.SetHelpText(exportButton, $"Selected={destinationResolution.SelectedPath}; Destination={destinationResolution.DestinationPath}; Corrected={destinationResolution.ExtensionCorrected}; Overwrite={destinationResolution.RequiresOverwriteConfirmation}");
    }

    if (destinationResolution.RequiresOverwriteConfirmation)
    {
        bool flag = await new ChartExportOverwriteDialog(destinationResolution.DestinationPath, displayScaleSettings.UiScale, displayScaleSettings.FontScale).ShowDialog<bool>(this);
        if (exportButton != null)
        {
            AutomationProperties.SetHelpText(exportButton, $"Selected={destinationResolution.SelectedPath}; Destination={destinationResolution.DestinationPath}; Corrected={destinationResolution.ExtensionCorrected}; Overwrite={destinationResolution.RequiresOverwriteConfirmation}; OverwriteResult={flag}");
        }

        if (!flag)
        {
            return;
        }
    }

    text2 = destinationResolution.DestinationPath;
    cancellationToken.ThrowIfCancellationRequested();
    string previousStatus = viewModel.Status;
    try
    {
        Progress<ChartExportProgress> progress = new Progress<ChartExportProgress>((ChartExportProgress value) =>
        {
            viewModel.SetStatus($"{value.Stage}  {value.Completed}/{value.Total}");
        });
        ChartExportResult chartExportResult = await viewModel.ExportChartAsync(tab.Id, text2, options, progress, cancellationToken);
        viewModel.SetStatus(previousStatus);
        if (!cancellationToken.IsCancellationRequested)
        {
            WindowsNativeMessageBox.Show(this, WindowsChartExportDialogParityPolicy.CreateSuccessResult(chartExportResult.DestinationPath));
            await RevealExportedFileAsync(chartExportResult.DestinationPath);
        }
    }
    catch
    {
        viewModel.SetStatus(previousStatus);
        throw;
    }
}

private async Task ExportThroughPlatformDestinationAsync(IPlatformFileExportService platform, DesktopChartTab tab, ChartExportOptions options, string suggestedName, string extension, CancellationToken cancellationToken)
{
    IPlatformExportDestination destination = await platform.PickExportDestinationAsync(suggestedName, extension, cancellationToken);
    if (destination == null)
    {
        return;
    }

    cancellationToken.ThrowIfCancellationRequested();
    string previousStatus = viewModel.Status;
    await using (destination)
    {
        try
        {
            Progress<ChartExportProgress> progress = new Progress<ChartExportProgress>((ChartExportProgress value) =>
            {
                viewModel.SetStatus($"{value.Stage}  {value.Completed}/{value.Total}");
            });
            await viewModel.ExportChartAsync(tab.Id, destination.StagingPath, options, progress, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                goto end_IL_00f4;
            }

            viewModel.SetStatus("正在写入导出位置…");
            await destination.CommitAsync(cancellationToken);
            viewModel.SetStatus(previousStatus);
            WindowsNativeMessageBox.Show(this, WindowsChartExportDialogParityPolicy.CreateSuccessResult(destination.DisplayName));
            goto end_IL_00e2;
            end_IL_00f4:
                ;
        }
        catch
        {
            viewModel.SetStatus(previousStatus);
            throw;
        }

        end_IL_00e2:
            ;
    }
}

private async Task BeginChartExportAsync()
{
    if (exportInProgress)
    {
        return;
    }

    DesktopChartTab activeTab = viewModel.ActiveTab;
    if (activeTab == null || !TryApplyPendingMetadataEditor(activeTab, enforceCommunityRules: false))
    {
        return;
    }

    exportInProgress = true;
    if (exportButton != null)
    {
        exportButton.IsEnabled = false;
    }

    CancellationTokenSource cancellation = (activeExportCancellation = new CancellationTokenSource());
    Task operation = (activeExportTask = ExecuteChartExportSafelyAsync(cancellation.Token));
    try
    {
        await operation;
    }
    finally
    {
        if (activeExportTask == operation)
        {
            activeExportTask = Task.CompletedTask;
        }

        if (activeExportCancellation == cancellation)
        {
            activeExportCancellation = null;
        }

        cancellation.Dispose();
        exportInProgress = false;
        if (exportButton != null)
        {
            exportButton.IsEnabled = viewModel.ActiveTab != null;
        }
    }
}

private async Task ExecuteChartExportSafelyAsync(CancellationToken cancellationToken)
{
    try
    {
        await PickAndExportChartAsync(cancellationToken);
    }
    catch (OperationCanceledException)
    {
        viewModel.SetStatus("导出已取消");
    }
    catch (Exception ex2)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("导出失败：{0}", AvaloniaLocalizationCatalog.Text(ex2.Message)));
        if (windowClosePreflightInProgress)
        {
            return;
        }

        try
        {
            WindowsNativeMessageBox.Show(this, WindowsChartExportDialogParityPolicy.CreateFailureResult("导出失败", ex2.Message));
        }
        catch
        {
        }
    }
}

private async Task<IStorageFolder?> ResolveChartExportStartLocationAsync(string sourcePath)
{
    IStorageFolder storageFolder = await ResolveEvidenceFilePickerStartLocationAsync();
    if (storageFolder != null)
    {
        return storageFolder;
    }

    IStorageFolder storageFolder2 = await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads);
    if (storageFolder2 != null)
    {
        return storageFolder2;
    }

    string text = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    if (Directory.Exists(text))
    {
        storageFolder2 = await StorageProvider.TryGetFolderFromPathAsync(text);
        if (storageFolder2 != null)
        {
            return storageFolder2;
        }
    }

    string directoryName = System.IO.Path.GetDirectoryName(sourcePath);
    return (string.IsNullOrWhiteSpace(directoryName) || !Directory.Exists(directoryName)) ? null : (await StorageProvider.TryGetFolderFromPathAsync(directoryName));
}

private Task RevealExportedFileAsync(string path)
{
    if (evidenceChartMode && evidenceSuppressExportReveal)
    {
        return Task.CompletedTask;
    }

    if (!OperatingSystem.IsWindows())
    {
        return Task.CompletedTask;
    }

    try
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select,\"" + fullPath + "\"", UseShellExecute = true });
    }
    catch (Exception ex)
    {
        viewModel.SetStatus("导出已完成，但无法打开导出目录：" + ex.Message);
        WindowsNativeMessageBox.Show(this, WindowsChartExportDialogParityPolicy.CreateFailureResult("导出已完成，但无法打开导出目录", ex.Message));
    }

    return Task.CompletedTask;
}

private async Task PickAndOpenProjectAsync()
{
    ReportLibraryLauncherEvidencePhase("open-project:invoked");
    IStorageFolder suggestedStartLocation = await ResolveFolderPickerStartLocationAsync("open-project");
    ReportLibraryLauncherEvidencePhase("open-project:show-picker");
    IReadOnlyList<IStorageFolder> readOnlyList = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择具体谱面目录", AllowMultiple = false, SuggestedStartLocation = suggestedStartLocation });
    ReportLibraryLauncherEvidencePhase($"open-project:picker-returned:{readOnlyList.Count}");
    string path = readOnlyList.FirstOrDefault()?.TryGetLocalPath();
    if (string.IsNullOrWhiteSpace(path))
    {
        return;
    }

    try
    {
        IReadOnlyList<string> chartPaths = await viewModel.InspectProjectDirectoryAsync(path);
        foreach (string item in chartPaths)
        {
            await OpenChartAsync(item);
        }

        viewModel.SetStatus((chartPaths.Count == 1) ? AvaloniaLocalizationCatalog.Format("已打开项目：{0}", System.IO.Path.GetFileName(path)) : AvaloniaLocalizationCatalog.Format("已打开项目：{0} · {1:N0} 张谱面标签", System.IO.Path.GetFileName(path), chartPaths.Count));
    }
    catch (DesktopProjectDirectoryValidationException ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("打开项目失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
        WindowsNativeMessageBox.Show(this, WindowsLibraryLauncherParityPolicy.CreateProjectDirectoryNotice(ex));
    }
    catch (Exception ex2)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("打开项目失败：{0}", AvaloniaLocalizationCatalog.Text(ex2.Message)));
        WindowsNativeMessageBox.Show(this, WindowsLibraryLauncherParityPolicy.CreateOperationError("无法打开项目目录", ex2));
    }
}

private async Task OpenChartOrArchiveAsync(string path)
{
    string extension = System.IO.Path.GetExtension(path);
    if (extension.Equals(".mcz", StringComparison.OrdinalIgnoreCase) || extension.Equals(".osz", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            AvaloniaArchiveImportPlan imported = await viewModel.ImportArchiveAsync(path);
            ShowLibraryScanErrorIfNeeded();
            if (await new ArchiveImportDialog(imported.Archive.DestinationRoot, imported.SupportedChartPaths.Count, imported.UnsupportedChartPaths.Count, imported.BrokenChartPaths.Count, displayScaleSettings.UiScale, displayScaleSettings.FontScale).ShowDialog<bool>(this))
            {
                foreach (string supportedChartPath in imported.SupportedChartPaths)
                {
                    await OpenChartAsync(supportedChartPath);
                }

                viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("已处理谱包中的 {0:N0} 张有效谱面", imported.SupportedChartPaths.Count));
            }

            return;
        }
        catch (Exception ex)
        {
            viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("导入谱包失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
            WindowsNativeMessageBox.Show(this, WindowsLibraryLauncherParityPolicy.CreateOperationError("无法导入谱包", ex));
            return;
        }
    }

    await OpenChartAsync(path);
}

private async Task<bool> OpenChartAsync(string path)
{
    try
    {
        path = System.IO.Path.GetFullPath(path);
        if (viewModel.LinkedLibraryRoots.Any((string root) => OsuFileSafetyPolicy.IsWithin(path, root)) && !OsuFileSafetyPolicy.IsWithin(path, viewModel.CreationRoot))
        {
            await OpenLibraryReadOnlyAsync(path);
            return true;
        }

        DesktopChartTab desktopChartTab = viewModel.Tabs.FirstOrDefault((DesktopChartTab tab) => string.Equals(tab.SourcePath, path, StringComparison.OrdinalIgnoreCase));
        if (desktopChartTab != null)
        {
            viewModel.Activate(desktopChartTab.Id);
            ShowEditorSurface();
            return true;
        }

        if (System.IO.Path.GetExtension(path).Equals(".osu", StringComparison.OrdinalIgnoreCase))
        {
            OsuNoteTimingImportMode? osuNoteTimingImportMode = await viewModel.TryGetSynchronizedSuspendedOsuTimingModeAsync(path);
            if (!osuNoteTimingImportMode.HasValue && shortcutSettings.Profile == EditorInteractionProfile.OsuMania && !evidenceShowOsuImportDialog)
            {
                osuNoteTimingImportMode = OsuNoteTimingImportMode.CalibrateCommonSubdivisions;
            }

            if (!osuNoteTimingImportMode.HasValue)
            {
                osuNoteTimingImportMode = await new OsuImportModeDialog(System.IO.Path.GetFileName(path), displayScaleSettings.UiScale, displayScaleSettings.FontScale).ShowDialog<OsuImportChoice>(this) switch
                {
                    OsuImportChoice.Calibrate => (OsuNoteTimingImportMode? )OsuNoteTimingImportMode.CalibrateCommonSubdivisions,
                    OsuImportChoice.Preserve => OsuNoteTimingImportMode.PreserveMilliseconds,
                    _ => null,
                };
            }

            if (!osuNoteTimingImportMode.HasValue)
            {
                return false;
            }

            await viewModel.OpenAsync(path, osuNoteTimingImportMode.Value);
            ShowEditorSurface();
            return true;
        }

        await viewModel.OpenAsync(path);
        ShowEditorSurface();
        return true;
    }
    catch (Exception ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("打开谱面失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
        WindowsNativeMessageBox.Show(this, WindowsLibraryLauncherParityPolicy.CreateOperationError("无法打开谱面", ex));
        return false;
    }
}

private async Task PickWorkspaceAsync()
{
    ReportLibraryLauncherEvidencePhase("choose-workspace:invoked");
    IStorageFolder suggestedStartLocation = await ResolveFolderPickerStartLocationAsync("choose-workspace");
    ReportLibraryLauncherEvidencePhase("choose-workspace:show-picker");
    IReadOnlyList<IStorageFolder> readOnlyList = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择谱面创作目录或 osu! Songs 目录", AllowMultiple = false, SuggestedStartLocation = suggestedStartLocation });
    ReportLibraryLauncherEvidencePhase($"choose-workspace:picker-returned:{readOnlyList.Count}");
    string text = readOnlyList.FirstOrDefault()?.TryGetLocalPath();
    if (string.IsNullOrWhiteSpace(text))
    {
        return;
    }

    try
    {
        librarySearchBox.Text = string.Empty;
        await viewModel.SwitchWorkspaceAsync(text);
        ShowLibraryScanErrorIfNeeded();
    }
    catch (Exception ex)
    {
        viewModel.SetStatus(AvaloniaLocalizationCatalog.Format("切换工作目录失败：{0}", AvaloniaLocalizationCatalog.Text(ex.Message)));
        WindowsNativeMessageBox.Show(this, WindowsLibraryLauncherParityPolicy.CreateOperationError("无法读取工作目录", ex));
    }
}

private async Task RefreshLibraryWithErrorUiAsync()
{
    await viewModel.RefreshLibraryAsync();
    ShowLibraryScanErrorIfNeeded();
}

private void ShowLibraryScanErrorIfNeeded()
{
    Exception lastLibraryScanError = viewModel.LastLibraryScanError;
    if (lastLibraryScanError != null)
    {
        WindowsNativeMessageBox.Show(this, WindowsLibraryLauncherParityPolicy.CreateOperationError("无法读取工作目录", lastLibraryScanError));
    }
}

private void ShowLibrary(bool focusSearch = false)
{
    audioAnalysisCancellation?.Cancel();
    libraryVisible = true;
    audioTransport.PauseAsync();
    UpdateUi();
    if (initialized && !viewModel.IsScanning)
    {
        StartDeferredLibraryScan();
    }

    if (!focusSearch)
    {
        return;
    }

    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (libraryVisible)
        {
            librarySearchBox.Focus();
        }
    }, DispatcherPriority.Input);
}

private void ShowEditorSurface(bool focusTimeline = true)
{
    if (viewModel.ActiveTab == null)
    {
        return;
    }

    libraryVisible = false;
    UpdateUi();
    if (!focusTimeline)
    {
        return;
    }

    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (!libraryVisible && viewModel.ActiveTab != null)
        {
            timeline.Focus();
        }
    }, DispatcherPriority.Input);
}

private void ToggleMaximize()
{
    CancelPendingWindowSnapApplication();
    activeWindowSnapTarget = null;
    windowSnapRestoreBounds = null;
    WindowState = WindowsTitleBarParityPolicy.ResolveMaximizeToggle(WindowState);
}

private void ToggleFullscreen()
{
    if (!SupportsDesktopWindowSizing)
    {
        SetTabletAppChromeHidden(!tabletAppChromeHidden);
    }
    else if (WindowState == WindowState.FullScreen)
    {
        RestoreWindowedTitleBar();
        WindowState = WindowsTitleBarParityPolicy.ResolveFullscreenRestoreState(windowStateBeforeFullscreen);
        editorViewSettings = editorViewSettings.WithFullscreenMode(WindowsFullscreenMode.Windowed);
        MarkEditorViewSettingsDirty();
    }
    else
    {
        windowStateBeforeFullscreen = WindowState;
        WindowState = WindowState.FullScreen;
        ConfigureFullscreenTitleBar();
        editorViewSettings = editorViewSettings.WithFullscreenMode(WindowsFullscreenMode.Borderless);
        MarkEditorViewSettingsDirty();
    }
}

private void SetTabletAppChromeHidden(bool hidden)
{
    tabletAppChromeHidden = hidden;
    titleBar.IsVisible = !hidden;
    shellRoot.RowDefinitions[0].Height = (hidden ? new GridLength(0.0) : new GridLength(38.0));
    titleBarTranslate.Y = 0.0;
    titleBar.IsHitTestVisible = !hidden;
    Grid.SetRowSpan(titleBar, 1);
    if (commandFullscreenButton != null)
    {
        AutomationProperties.SetName(commandFullscreenButton, hidden ? "显示顶部标签" : "隐藏顶部标签");
        ToolTip.SetTip(commandFullscreenButton, hidden ? "显示顶部标签" : "隐藏顶部标签");
    }

    viewModel.SetStatus(hidden ? "已隐藏顶部标签" : "已显示顶部标签");
    QueueAudioPanelLayout(clampFreePanels: true);
    QueueDifficultyCardLayout();
    QueueFloatingPaletteClamp();
}

private bool SuspendFullscreenForClosePreflight()
{
    if (!WindowsTitleBarParityPolicy.ShouldSuspendFullscreenForClosePreflight(WindowState))
    {
        return false;
    }

    RestoreWindowedTitleBar();
    WindowState = WindowsTitleBarParityPolicy.ResolveFullscreenRestoreState(windowStateBeforeFullscreen);
    return true;
}

private void RestoreFullscreenAfterCanceledClosePreflight()
{
    if (WindowState != WindowState.FullScreen)
    {
        windowStateBeforeFullscreen = WindowState;
        WindowState = WindowState.FullScreen;
        ConfigureFullscreenTitleBar();
    }
}

private void ConfigureFullscreenTitleBar()
{
    fullscreenTitleHideTimer.Stop();
    CancelFullscreenTitleBarAnimation();
    fullscreenTitleBarRevealed = false;
    shellRoot.RowDefinitions[0].Height = new GridLength(0.0);
    Grid.SetRowSpan(titleBar, 3);
    titleBar.Height = 38.0;
    titleBar.VerticalAlignment = VerticalAlignment.Top;
    titleBar.IsHitTestVisible = false;
    titleBarTranslate.Y = -38.0;
    fullscreenTitleRevealHotZone.IsVisible = true;
    if (evidenceFocusTitleBarAnimation)
    {
        UpdateTitleBarAutomationHelpText();
    }
}

private void RestoreWindowedTitleBar()
{
    fullscreenTitleHideTimer.Stop();
    CancelFullscreenTitleBarAnimation();
    fullscreenTitleBarRevealed = false;
    fullscreenTitleRevealHotZone.IsVisible = false;
    titleBarTranslate.Y = 0.0;
    titleBar.IsHitTestVisible = true;
    Grid.SetRowSpan(titleBar, 1);
    titleBar.Height = double.NaN;
    titleBar.VerticalAlignment = VerticalAlignment.Stretch;
    shellRoot.RowDefinitions[0].Height = new GridLength(38.0);
    if (evidenceFocusTitleBarAnimation)
    {
        UpdateTitleBarAutomationHelpText();
    }
}

private void SetFullscreenTitleBarRevealed(bool revealed)
{
    if (WindowState == WindowState.FullScreen && fullscreenTitleBarRevealed != revealed)
    {
        fullscreenTitleHideTimer.Stop();
        fullscreenTitleBarRevealed = revealed;
        AnimateFullscreenTitleBar(revealed);
        titleBar.IsHitTestVisible = revealed;
        fullscreenTitleRevealHotZone.IsVisible = !revealed;
    }
}

private void AnimateFullscreenTitleBar(bool revealed)
{
    double y = titleBarTranslate.Y;
    CancelFullscreenTitleBarAnimation();
    fullscreenTitleAnimationFrom = y;
    fullscreenTitleAnimationTarget = (revealed ? 0.0 : (-38.0));
    fullscreenTitleAnimationRevealed = revealed;
    fullscreenTitleAnimationRunning = true;
    titleBarTranslate.Y = y;
    fullscreenTitleAnimationStopwatch.Restart();
    if (evidenceFocusTitleBarAnimation)
    {
        UpdateTitleBarAutomationHelpText();
    }

    fullscreenTitleAnimationTimer.Start();
}

private void FullscreenTitleAnimationTimer_Tick(object? sender, EventArgs eventArgs)
{
    if (!fullscreenTitleAnimationRunning)
    {
        fullscreenTitleAnimationTimer.Stop();
        return;
    }

    int num = (fullscreenTitleAnimationRevealed ? 145 : 125);
    double num2 = Math.Clamp(fullscreenTitleAnimationStopwatch.Elapsed.TotalMilliseconds / (double)num, 0.0, 1.0);
    double num3 = WindowsTitleBarParityPolicy.EvaluateFullscreenTitleBarProgress(fullscreenTitleAnimationRevealed, num2);
    titleBarTranslate.Y = fullscreenTitleAnimationFrom + (fullscreenTitleAnimationTarget - fullscreenTitleAnimationFrom) * num3;
    if (num2 >= 1.0)
    {
        titleBarTranslate.Y = fullscreenTitleAnimationTarget;
        fullscreenTitleAnimationRunning = false;
        fullscreenTitleAnimationStopwatch.Stop();
        fullscreenTitleAnimationTimer.Stop();
    }

    if (evidenceFocusTitleBarAnimation)
    {
        UpdateTitleBarAutomationHelpText();
    }
}

private void CancelFullscreenTitleBarAnimation()
{
    fullscreenTitleAnimationTimer.Stop();
    fullscreenTitleAnimationStopwatch.Reset();
    fullscreenTitleAnimationRunning = false;
    if (evidenceFocusTitleBarAnimation)
    {
        UpdateTitleBarAutomationHelpText();
    }
}

private void FullscreenTitleHideTimer_Tick(object? sender, EventArgs eventArgs)
{
    fullscreenTitleHideTimer.Stop();
    if (WindowState == WindowState.FullScreen && !titleBar.IsPointerOver)
    {
        SetFullscreenTitleBarRevealed(revealed: false);
    }
}

private static Button CaptionButton(string content, EventHandler<RoutedEventArgs> handler)
{
    Button button = new Button
    {
        Content = content,
        Background = Brushes.Transparent,
        Foreground = Brushes.White,
        BorderThickness = new Thickness(0.0),
        FontFamily = new FontFamily("Segoe UI Symbol"),
        FontSize = 16.0,
        Focusable = false,
        IsTabStop = false,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center
    };
    button.PointerEntered += (object? _, PointerEventArgs _) =>
    {
        button.Background = new SolidColorBrush(Color.Parse("#262A2D"));
    };
    button.PointerExited += (object? _, PointerEventArgs _) =>
    {
        button.Background = Brushes.Transparent;
    };
    button.PointerPressed += (object? _, PointerPressedEventArgs eventArgs) =>
    {
        if (eventArgs.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
        {
            button.Background = new SolidColorBrush(Color.Parse("#303438"));
        }
    };
    button.PointerReleased += (object? _, PointerReleasedEventArgs _) =>
    {
        Button button2 = button;
        IBrush background;
        if (!button.IsPointerOver)
        {
            IBrush transparent = Brushes.Transparent;
            background = transparent;
        }
        else
        {
            IBrush transparent = new SolidColorBrush(Color.Parse("#262A2D"));
            background = transparent;
        }

        button2.Background = background;
    };
    button.Click += handler;
    return button;
}

private static Button TitleBarToolButton(string text, EventHandler<RoutedEventArgs> handler)
{
    Button button = new Button();
    button.Content = text;
    button.Padding = new Thickness(10.0, 3.0);
    button.Margin = new Thickness(0.0, 0.0, 6.0, 0.0);
    button.Background = new SolidColorBrush(Color.Parse("#22272A"));
    button.Foreground = new SolidColorBrush(Color.Parse("#B7C0C4"));
    button.BorderBrush = new SolidColorBrush(Color.Parse("#3A4246"));
    button.BorderThickness = new Thickness(1.0);
    button.CornerRadius = new CornerRadius(3.0);
    button.VerticalAlignment = VerticalAlignment.Center;
    button.Click += handler;
    return button;
}

private static Button NavigationButton(string text, EventHandler<RoutedEventArgs> handler)
{
    Button button = new Button();
    button.Content = text;
    button.HorizontalContentAlignment = HorizontalAlignment.Left;
    button.Padding = new Thickness(12.0, 9.0);
    button.Background = Brushes.Transparent;
    button.Foreground = Brushes.White;
    button.BorderBrush = Brushes.Transparent;
    button.BorderThickness = new Thickness(1.0);
    button.CornerRadius = new CornerRadius(4.0);
    button.Click += handler;
    return button;
}

private void AddTimelineToolButton(StackPanel toolbar, TimelineTool tool, string text)
{
    Button button = PrimaryButton(text, (object? _, RoutedEventArgs _) =>
    {
        SetTimelineTool(tool);
    });
    button.Padding = new Thickness(10.0, 6.0);
    AutomationProperties.SetName(button, $"Timeline Tool {tool}");
    AutomationProperties.SetAutomationId(button, $"Timeline.Tool.{tool}");
    timelineToolButtons.Add(tool, button);
    toolbar.Children.Add(button);
}

private void SetTimelineTool(TimelineTool tool)
{
    EditorSession editorSession = viewModel.ActiveTab?.Session;
    if (!WindowsAvaloniaEditorInputPolicy.CanUseTimelineTool(editorSession, tool))
    {
        viewModel.SetStatus((tool == TimelineTool.Erase) ? "当前谱面没有编辑 Note 或 Timing / Effect 的权限；选择工具仍可使用" : "当前谱面没有编辑 Note 的权限；选择工具仍可使用");
        return;
    }

    if (tool == TimelineTool.Erase && editorSession != null && (editorSession.Selection.Count > 0 || timeline.SelectedTimingIds.Count > 0))
    {
        DeleteMixedSelection();
    }

    timeline.Tool = tool;
    UpdateTimelineToolButtons();
    timeline.Focus();
}

private void UpdateTimelineToolButtons()
{
    RefreshFixedTools();
    EditorSession session = viewModel.ActiveTab?.Session;
    foreach (KeyValuePair<TimelineTool, Button> timelineToolButton in timelineToolButtons)
    {
        bool flag = timelineToolButton.Key == timeline.Tool;
        timelineToolButton.Value.IsEnabled = WindowsAvaloniaEditorInputPolicy.CanUseTimelineTool(session, timelineToolButton.Key);
        timelineToolButton.Value.Background = (flag ? AccentBrush : RaisedBrush);
        timelineToolButton.Value.Foreground = (flag ? Brushes.Black : Brushes.White);
        timelineToolButton.Value.BorderBrush = (flag ? AccentBrush : StrokeBrush);
        AutomationProperties.SetHelpText(timelineToolButton.Value, flag ? "当前编辑工具" : "可切换编辑工具");
    }

    foreach (KeyValuePair<TimelineTool, Button> quickTimelineToolButton in quickTimelineToolButtons)
    {
        bool flag2 = quickTimelineToolButton.Key == timeline.Tool;
        quickTimelineToolButton.Value.IsEnabled = WindowsAvaloniaEditorInputPolicy.CanUseTimelineTool(session, quickTimelineToolButton.Key);
        ApplyQuickPaletteModeState(quickTimelineToolButton.Value, flag2);
        AutomationProperties.SetHelpText(quickTimelineToolButton.Value, flag2 ? "当前编辑工具" : "可切换编辑工具");
    }
}

private static void ApplyQuickPaletteModeState(Button button, bool active)
{
    IBrush background;
    if (!active)
    {
        IBrush transparent = Brushes.Transparent;
        background = transparent;
    }
    else
    {
        background = QuickActiveBrush;
    }

    button.Background = background;
    IBrush foreground;
    if (!active)
    {
        IBrush transparent = Brushes.White;
        foreground = transparent;
    }
    else
    {
        foreground = QuickActiveForegroundBrush;
    }

    button.Foreground = foreground;
    IBrush borderBrush;
    if (!active)
    {
        IBrush transparent = Brushes.Transparent;
        borderBrush = transparent;
    }
    else
    {
        borderBrush = QuickActiveBorderBrush;
    }

    button.BorderBrush = borderBrush;
}

private static Button LibraryLauncherButton(string glyph, string title, string description, EventHandler<RoutedEventArgs> handler, bool primary, Thickness margin)
{
    Button button = new Button
    {
        Height = 52.0,
        Width = 468.0,
        Margin = margin,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Padding = new Thickness(12.0, 7.0)
    };
    StackPanel stackPanel = new StackPanel
    {
        Orientation = Orientation.Horizontal
    };
    global::Avalonia.Controls.Controls children = stackPanel.Children;
    TextBlock textBlock = new TextBlock
    {
        Text = glyph,
        FontSize = 20.0,
        Width = 38.0
    };
    IBrush foreground;
    if (!primary)
    {
        IBrush brush = new SolidColorBrush(Color.Parse("#AFC2C4"));
        foreground = brush;
    }
    else
    {
        IBrush brush = Brushes.White;
        foreground = brush;
    }

    textBlock.Foreground = foreground;
    textBlock.VerticalAlignment = VerticalAlignment.Center;
    children.Add(textBlock);
    stackPanel.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { (Control)new TextBlock { Text = title, FontSize = 14.0, FontWeight = FontWeight.DemiBold }, (Control)new TextBlock { Text = description, FontSize = 10.0, Foreground = (primary ? new SolidColorBrush(Color.Parse("#D0DEDF")) : new SolidColorBrush(Color.Parse("#87969A"))), Margin = new Thickness(0.0, 3.0, 0.0, 0.0) } } });
    button.Content = stackPanel;
    Button button2 = button;
    if (primary)
    {
        ApplyWpfPrimaryButtonStyle(button2);
    }
    else
    {
        ApplyWpfToolButtonStyle(button2);
    }

    button2.Click += handler;
    AutomationProperties.SetName(button2, title + "；" + description);
    return button2;
}

private static Button PrimaryButton(string text, EventHandler<RoutedEventArgs> handler)
{
    Button button = new Button();
    button.Content = text;
    button.Padding = new Thickness(12.0, 7.0);
    button.Background = RaisedBrush;
    button.Foreground = Brushes.White;
    button.BorderBrush = StrokeBrush;
    button.BorderThickness = new Thickness(1.0);
    button.CornerRadius = new CornerRadius(4.0);
    button.Click += handler;
    return button;
}

private static Button CommandIconTextButton(string text, string geometry, EventHandler<RoutedEventArgs> handler, bool primary = false, double iconWidth = 16.0)
{
    Button button = CommandButton(string.Empty, handler);
    IBrush brush2;
    if (!primary)
    {
        IBrush brush = new SolidColorBrush(Color.Parse("#ADB5B9"));
        brush2 = brush;
    }
    else
    {
        IBrush brush = Brushes.White;
        brush2 = brush;
    }

    IBrush stroke = brush2;
    global::Avalonia.Controls.Shapes.Path item = new global::Avalonia.Controls.Shapes.Path
    {
        Width = iconWidth,
        Height = 16.0,
        Data = Geometry.Parse(geometry),
        Stroke = stroke,
        StrokeThickness = 1.5,
        Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center
    };
    TextBlock textBlock = new TextBlock
    {
        Text = text
    };
    IBrush foreground;
    if (!primary)
    {
        IBrush brush = new SolidColorBrush(Color.Parse("#D0D4D6"));
        foreground = brush;
    }
    else
    {
        IBrush brush = Brushes.White;
        foreground = brush;
    }

    textBlock.Foreground = foreground;
    textBlock.Margin = new Thickness(7.0, 0.0, 0.0, 0.0);
    textBlock.VerticalAlignment = VerticalAlignment.Center;
    textBlock.FontWeight = (primary ? FontWeight.DemiBold : FontWeight.Normal);
    TextBlock item2 = textBlock;
    button.Content = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Children =
        {
            (Control)item,
            (Control)item2
        }
    };
    if (primary)
    {
        button.Background = new SolidColorBrush(Color.Parse("#38676C"));
        button.BorderBrush = new SolidColorBrush(Color.Parse("#4A777B"));
        button.Foreground = Brushes.White;
    }

    return button;
}

private static Button CommandIconButton(string geometry, EventHandler<RoutedEventArgs> handler, bool filled = false, double width = 36.0)
{
    Button button = CommandButton(string.Empty, handler);
    button.Width = width;
    button.Height = 34.0;
    button.Padding = new Thickness(7.0);
    SolidColorBrush solidColorBrush = new SolidColorBrush(Color.Parse("#ADB5B9"));
    button.Content = new global::Avalonia.Controls.Shapes.Path
    {
        Width = 18.0,
        Height = 16.0,
        Data = Geometry.Parse(geometry),
        Fill = (filled ? solidColorBrush : null),
        Stroke = (filled ? null : solidColorBrush),
        StrokeThickness = 1.6,
        Stretch = Stretch.Uniform
    };
    return button;
}

private static Button CommandGlyphButton(string glyph, EventHandler<RoutedEventArgs> handler)
{
    Button button = CommandButton(glyph, handler);
    button.Width = 38.0;
    button.Height = 34.0;
    button.Padding = new Thickness(7.0);
    return button;
}

private static void ConfigureCommandModeButton(Button button, TextBlock label)
{
    button.Padding = new Thickness(9.0, 7.0);
    button.Margin = new Thickness(1.0, 0.0, 10.0, 0.0);
    button.Background = Brushes.Transparent;
    button.Foreground = new SolidColorBrush(Color.Parse("#D0D4D6"));
    button.BorderBrush = Brushes.Transparent;
    button.BorderThickness = new Thickness(1.0);
    button.CornerRadius = new CornerRadius(3.0);
    label.Text = "Pro OFF";
    label.Margin = new Thickness(6.0, 0.0, 0.0, 0.0);
    label.VerticalAlignment = VerticalAlignment.Center;
    button.Content = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Children =
        {
            (Control)new global::Avalonia.Controls.Shapes.Path
            {
                Width = 16.0,
                Height = 16.0,
                Data = Geometry.Parse("M1,2 H15 V14 H1 Z M5,2 V14 M9,2 V14 M1,6 H15 M1,10 H15"),
                Stroke = new SolidColorBrush(Color.Parse("#91ABB8")),
                StrokeThickness = 1.2,
                Stretch = Stretch.Uniform
            },
            (Control)label
        }
    };
}

private static void ConfigureCommandToggleButton(ToggleButton button, string text)
{
    button.Content = text;
    button.Padding = new Thickness(9.0, 7.0);
    button.Margin = new Thickness(1.0, 0.0);
    button.Background = Brushes.Transparent;
    button.Foreground = new SolidColorBrush(Color.Parse("#D0D4D6"));
    button.BorderBrush = Brushes.Transparent;
    button.BorderThickness = new Thickness(1.0);
    button.CornerRadius = new CornerRadius(3.0);
}

private static Button CommandButton(string text, EventHandler<RoutedEventArgs> handler, bool horizontal = false)
{
    Button button = new Button();
    button.Content = text;
    button.Height = (horizontal ? 35.0 : double.NaN);
    button.Padding = new Thickness(9.0, 7.0);
    button.Margin = new Thickness(1.0, 0.0);
    button.HorizontalContentAlignment = (horizontal ? HorizontalAlignment.Left : HorizontalAlignment.Center);
    button.Background = Brushes.Transparent;
    button.Foreground = new SolidColorBrush(Color.Parse("#D0D4D6"));
    button.BorderBrush = Brushes.Transparent;
    button.BorderThickness = new Thickness(1.0);
    button.CornerRadius = new CornerRadius(3.0);
    button.Click += handler;
    return button;
}

private static void ApplyPrimaryButtonStyle(Button button)
{
    button.Padding = new Thickness(8.0, 6.0);
    button.Background = RaisedBrush;
    button.Foreground = Brushes.White;
    button.BorderBrush = StrokeBrush;
    button.BorderThickness = new Thickness(1.0);
    button.CornerRadius = new CornerRadius(4.0);
}

private static void ApplyWpfToolButtonStyle(Button button)
{
    button.Padding = new Thickness(9.0, 6.0);
    button.Margin = new Thickness(2.0, 0.0);
    button.Background = new SolidColorBrush(Color.Parse("#1A1D20"));
    button.Foreground = new SolidColorBrush(Color.Parse("#D0D4D6"));
    button.BorderBrush = new SolidColorBrush(Color.Parse("#34393D"));
    button.BorderThickness = new Thickness(1.0);
    button.CornerRadius = new CornerRadius(3.0);
    button.HorizontalAlignment = HorizontalAlignment.Stretch;
    button.HorizontalContentAlignment = HorizontalAlignment.Center;
    button.VerticalContentAlignment = VerticalAlignment.Center;
}

private static void ApplyWpfPrimaryButtonStyle(Button button)
{
    ApplyWpfToolButtonStyle(button);
    button.Classes.Add("accent");
    button.Background = new SolidColorBrush(Color.Parse("#38676C"));
    button.BorderBrush = new SolidColorBrush(Color.Parse("#4A777B"));
    button.Foreground = Brushes.White;
    button.FontWeight = FontWeight.DemiBold;
}

private static void ApplyWpfAssignButtonStyle(Button button)
{
    ApplyWpfToolButtonStyle(button);
    button.Background = new SolidColorBrush(Color.Parse("#76572F"));
    button.BorderBrush = new SolidColorBrush(Color.Parse("#9B7542"));
    button.Foreground = new SolidColorBrush(Color.Parse("#FFF4E3"));
    button.FontWeight = FontWeight.DemiBold;
}

private static void ApplyDangerButtonStyle(Button button)
{
    button.Padding = new Thickness(12.0, 7.0);
    button.Background = new SolidColorBrush(Color.Parse("#2B1E21"));
    button.Foreground = new SolidColorBrush(Color.Parse("#FF969B"));
    button.BorderBrush = new SolidColorBrush(Color.Parse("#6A3439"));
    button.BorderThickness = new Thickness(1.0);
    button.CornerRadius = new CornerRadius(4.0);
}

private static string? ParseArgument(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

private static int ParsePositiveIntArgument(string[] args, string name)
{
    if (!int.TryParse(ParseArgument(args, name), out var result) || result <= 0 || result > 1000000)
    {
        return 0;
    }

    return result;
}

private static bool IsSupportedOpenPath(string path)
{
    return WindowsSingleInstancePolicy.IsSupportedOpenPath(path);
}

}

public static class WindowsAvaloniaEditorInputPolicy
{
    public static bool CanUseTimelineTool(EditorSession? session, TimelineTool tool)
    {
        switch (tool)
        {
            case TimelineTool.Select:
                return true;
            case TimelineTool.Erase:
                return session != null && !session.IsReadOnly && (session.CanEditNotes || session.CanEditTiming);
            case TimelineTool.Tap:
            case TimelineTool.Hold:
                return session != null && !session.IsReadOnly && session.CanEditNotes;
            default:
                return false;
        }
    }

    public static bool PreserveNotesForTimingListSelection(KeyModifiers modifiers)
    {
        return modifiers.HasFlag(KeyModifiers.Shift);
    }

    public static bool IsTextEditingShortcut(Key key, KeyModifiers modifiers)
    {
        bool flag = ((key == Key.Back || key == Key.Delete) ? true : false);
        bool flag2 = flag;
        if (!flag2)
        {
            bool flag3 = modifiers.HasFlag(KeyModifiers.Control);
            if (flag3)
            {
                bool flag4;
                switch (key)
                {
                    case Key.A:
                    case Key.C:
                    case Key.V:
                    case Key.X:
                    case Key.Y:
                    case Key.Z:
                        flag4 = true;
                        break;
                    default:
                        flag4 = false;
                        break;
                }

                flag3 = flag4;
            }

            flag2 = flag3;
        }

        return flag2;
    }

    public static bool DeferToTextInputMethod(Key reportedKey, KeyModifiers modifiers)
    {
        if (((uint)(reportedKey - 155) <= 1u || reportedKey == Key.DeadCharProcessed) ? true : false)
        {
            return true;
        }

        if (!modifiers.HasFlag(KeyModifiers.Control) && !modifiers.HasFlag(KeyModifiers.Alt))
        {
            return !modifiers.HasFlag(KeyModifiers.Meta);
        }

        return false;
    }

    public static bool IsTextEditingControl(Control? control)
    {
        return FindTextEditingControl(control) != null;
    }

    public static Control? FindTextEditingControl(Control? control)
    {
        for (Control control2 = control; control2 != null; control2 = control2.Parent as Control)
        {
            if ((control2 is TextBox || control2 is ComboBox) ? true : false)
            {
                return control2;
            }
        }

        return null;
    }
}

public readonly record struct WindowsOnlineRoomEditorAccess(bool IsReadOnly, bool CanEditMetadata, bool CanEditTiming, bool CanEditNotes);

public static class WindowsOnlineRoomPermissionPolicy
{
    public static WindowsOnlineRoomEditorAccess Resolve(bool isOwner, string? memberRole, bool canEditMetadata, bool canEditTiming, bool canEditNotes)
    {
        if (isOwner)
        {
            return new WindowsOnlineRoomEditorAccess(IsReadOnly: false, CanEditMetadata: true, CanEditTiming: true, CanEditNotes: true);
        }

        if (memberRole != null && memberRole.Equals("viewer", StringComparison.OrdinalIgnoreCase))
        {
            return new WindowsOnlineRoomEditorAccess(IsReadOnly: true, CanEditMetadata: false, CanEditTiming: false, CanEditNotes: false);
        }

        return new WindowsOnlineRoomEditorAccess(IsReadOnly: false, canEditMetadata, canEditTiming, canEditNotes);
    }
}
}
