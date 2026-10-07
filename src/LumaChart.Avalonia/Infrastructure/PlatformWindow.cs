using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using LumaChart.Avalonia.Infrastructure;

#if LUMACHART_SHARED
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
#endif

namespace LumaChart.Avalonia;

#if LUMACHART_SHARED
/// <summary>
/// SingleView-compatible window surface. Android hosts the same controls in
/// one visual tree and presents secondary windows as modal overlays.
/// </summary>
public class PlatformWindow : UserControl
{
    public static readonly StyledProperty<WindowState> WindowStateProperty =
        AvaloniaProperty.Register<PlatformWindow, WindowState>(nameof(WindowState));

    /// <summary>Breathing room a centred modal keeps from the surface edges.</summary>
    private const double ModalMargin = 16;

    private TaskCompletionSource<object?>? modalCompletion;
    private PlatformWindow? modalOwner;
    private object? modalOwnerContent;
    private Grid? modalOverlay;
    private Action? applyOverlayLimits;
    private AvaloniaLocalizationController? localizationController;
#if LUMACHART_IOS
    private global::Avalonia.Controls.Platform.IInputPane? inputPane;
    private EventHandler<global::Avalonia.Controls.Platform.InputPaneStateEventArgs>? inputPaneStateChanged;
#endif

    public string? Title { get; set; }
    public WindowState WindowState
    {
        get => GetValue(WindowStateProperty);
        set => SetValue(WindowStateProperty, value);
    }
    public WindowDecorations WindowDecorations { get; set; } = WindowDecorations.Full;
    public bool CanResize { get; set; }
    public WindowStartupLocation WindowStartupLocation { get; set; }
    public SizeToContent SizeToContent { get; set; }
    public bool ShowInTaskbar { get; set; } = true;
    public PixelPoint Position { get; set; }
    public WindowIcon? Icon { get; set; }
    public Screens? Screens => TopLevel.GetTopLevel(this)?.Screens;
    public IStorageProvider StorageProvider =>
        TopLevel.GetTopLevel(this)?.StorageProvider
        ?? throw new PlatformNotSupportedException("Android storage provider is not attached yet.");
    public bool IsActive => IsVisible;
    public bool SupportsDesktopWindowSizing => false;

    /// <summary>
    /// Set by surfaces that take the whole SingleView area instead of appearing
    /// as a centred sheet. Playtest is the one that needs it: on a device the
    /// playfield should own the screen rather than sit in a boxed dialog.
    /// </summary>
    public bool PrefersFullSurface { get; set; }
    public double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
    public Size ClientSize => Bounds.Size;
    public IFocusManager? FocusManager => TopLevel.GetTopLevel(this)?.FocusManager;
    public IClipboard? Clipboard => TopLevel.GetTopLevel(this)?.Clipboard;

    public event EventHandler? Opened;
    public event EventHandler? Activated;
    public event EventHandler<WindowClosingEventArgs>? Closing;
    public event EventHandler? Closed;

    public PlatformWindow()
    {
        Opened += (_, _) =>
            localizationController = AvaloniaLocalizationRuntime.Attach(this);
    }

    public IPlatformHandle? TryGetPlatformHandle() =>
        TopLevel.GetTopLevel(this)?.TryGetPlatformHandle();

    public Screen? ScreenFromHost() => Screens?.Primary;

    public void ConfigureInitialWindowBounds(
        double width,
        double height,
        double minimumWidth,
        double minimumHeight)
    {
        _ = width;
        _ = height;
        _ = minimumWidth;
        _ = minimumHeight;
        Width = double.NaN;
        Height = double.NaN;
        MinWidth = 0;
        MinHeight = 0;
        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch;
        VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch;
    }

    public void BeginMoveDrag(PointerEventArgs _) { }
    public void BeginResizeDrag(WindowEdge _, PointerEventArgs __) { }

    public void Show() => IsVisible = true;

    public void Activate()
    {
        Focus();
        Activated?.Invoke(this, EventArgs.Empty);
    }

    public void RequestAnimationFrame(Action<TimeSpan> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            topLevel.RequestAnimationFrame(callback);
            return;
        }

        Dispatcher.UIThread.Post(
            () => callback(TimeSpan.Zero),
            DispatcherPriority.Render);
    }

    public Task<TResult> ShowDialog<TResult>(PlatformWindow owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (modalCompletion != null)
            throw new InvalidOperationException("The dialog is already visible.");

        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        modalCompletion = completion;
        modalOwner = owner;
        modalOwnerContent = owner.Content;

        owner.Content = null;
        var overlay = new Grid();
        if (modalOwnerContent is Control content)
            overlay.Children.Add(content);
        overlay.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#B0000000"))
        });

        if (PrefersFullSurface)
        {
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch;
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch;
            Margin = default;
        }
        else
        {
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center;
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center;
            Margin = new Thickness(ModalMargin);
        }

        ClampToOverlay(overlay);
#if LUMACHART_IOS
        if (!PrefersFullSurface) TrackInputPane();
#endif
        overlay.Children.Add(this);
        modalOverlay = overlay;
        owner.Content = overlay;
        IsVisible = true;
        Opened?.Invoke(this, EventArgs.Empty);
        Activated?.Invoke(this, EventArgs.Empty);

        return AwaitResultAsync<TResult>(completion.Task);
    }

    public async Task ShowDialog(PlatformWindow owner) =>
        _ = await ShowDialog<object?>(owner);

    /// <summary>
    /// Keeps a centred modal inside the SingleView surface. A dialog laid out
    /// for a desktop window can be taller than the whole device viewport, and
    /// centring something larger than its parent pushes the top of it off the
    /// screen where no scroll can reach it.
    /// </summary>
    private void ClampToOverlay(Control overlay)
    {
        void ApplyLimits()
        {
            var bounds = overlay.Bounds;
            var margin = Margin.Left + Margin.Right;
            if (bounds.Width > margin) MaxWidth = bounds.Width - margin;
            margin = Margin.Top + Margin.Bottom;
            if (bounds.Height > margin) MaxHeight = bounds.Height - margin;
        }

        applyOverlayLimits = ApplyLimits;
        ApplyLimits();
        overlay.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.Property == BoundsProperty) ApplyLimits();
        };
    }

#if LUMACHART_IOS
    /// <summary>
    /// Keeps a centred modal above the on-screen keyboard.
    ///
    /// A centred dialog laid out for a desktop window puts its confirm button
    /// near the bottom, which on an iPad is exactly where the keyboard appears —
    /// and any dialog that asks for text raises it. Observed on the login card,
    /// where the keyboard covered the captcha image the user is being asked to
    /// read, and on the audio-import dialog, where it covered the button.
    ///
    /// Reserving the occluded height as bottom margin is enough: the modal is
    /// vertically centred, so shrinking the band it is centred in lifts it, and
    /// ClampToOverlay derives MaxHeight from the same margin, so a dialog taller
    /// than the remaining space shrinks instead of sliding off the top.
    ///
    /// iOS only. Android compiles this same file, and its window manager already
    /// resizes the surface for the soft keyboard, so doing it here as well would
    /// inset twice.
    /// </summary>
    private void TrackInputPane()
    {
        var pane = TopLevel.GetTopLevel(this)?.InputPane;
        if (pane == null) return;

        inputPane = pane;
        inputPaneStateChanged = (_, eventArgs) => ApplyInputPaneInset(
            eventArgs.NewState == global::Avalonia.Controls.Platform.InputPaneState.Open
                ? eventArgs.EndRect.Height
                : 0);
        pane.StateChanged += inputPaneStateChanged;
        ApplyInputPaneInset(
            pane.State == global::Avalonia.Controls.Platform.InputPaneState.Open
                ? pane.OccludedRect.Height
                : 0);
    }

    private void ApplyInputPaneInset(double occludedHeight)
    {
        var bottom = ModalMargin + Math.Max(0, occludedHeight);
        Margin = new Thickness(ModalMargin, ModalMargin, ModalMargin, bottom);
        applyOverlayLimits?.Invoke();
    }

    private void ReleaseInputPane()
    {
        if (inputPane != null && inputPaneStateChanged != null)
            inputPane.StateChanged -= inputPaneStateChanged;
        inputPane = null;
        inputPaneStateChanged = null;
    }
#endif

    public void Close() => Close(null);

    public void Close(object? result)
    {
        _ = Closing;

#if LUMACHART_IOS
        ReleaseInputPane();
#endif
        applyOverlayLimits = null;
        var completion = modalCompletion;
        var owner = modalOwner;
        if (completion == null || owner == null)
        {
            IsVisible = false;
            Closed?.Invoke(this, EventArgs.Empty);
            return;
        }

        owner.Content = null;
        if (modalOwnerContent is Control content)
            modalOverlay?.Children.Remove(content);
        modalOverlay?.Children.Remove(this);
        owner.Content = modalOwnerContent;
        modalCompletion = null;
        modalOwner = null;
        modalOwnerContent = null;
        modalOverlay = null;
        IsVisible = false;
        completion.TrySetResult(result);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private static async Task<TResult> AwaitResultAsync<TResult>(Task<object?> task)
    {
        var result = await task;
        if (result is TResult typed) return typed;
        return result == null ? default! : (TResult)result;
    }
}
#else
public class PlatformWindow : Window
{
    private AvaloniaLocalizationController? localizationController;

    public PlatformWindow()
    {
        Opened += (_, _) =>
            localizationController = AvaloniaLocalizationRuntime.Attach(this);
    }

    public bool SupportsDesktopWindowSizing => true;

    /// <summary>
    /// SingleView-only presentation hint. A desktop window already has its own
    /// bounds and chrome, so this has no effect here.
    /// </summary>
    public bool PrefersFullSurface { get; set; }

    public Screen? ScreenFromHost() => Screens.ScreenFromWindow(this) ?? Screens.Primary;

    public void ConfigureInitialWindowBounds(
        double width,
        double height,
        double minimumWidth,
        double minimumHeight)
    {
        Width = width;
        Height = height;
        MinWidth = minimumWidth;
        MinHeight = minimumHeight;
    }
}
#endif
