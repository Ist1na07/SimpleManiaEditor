using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Applies the selected display language to the already-created Avalonia tree.
/// Each value retains its original source text, so switching back and forth or
/// materializing a later TabItem cannot lose a Chinese label that is not in the
/// first English catalog rollout.
/// </summary>
public sealed class AvaloniaLocalizationController
{
    private const string TextBlockText = "TextBlock.Text";
    private const string ContentControlContent = "ContentControl.Content";
    private const string HeaderedControlHeader = "HeaderedContentControl.Header";
    private const string TextBoxPlaceholder = "TextBox.PlaceholderText";
    private const string ToolTipText = "ToolTip";
    private const string WindowTitle = "Window.Title";
    private const string AutomationName = "AutomationProperties.Name";

    /// <summary>
    /// The source text behind every translated property, keyed by control.
    /// Shared by all controllers rather than kept per controller: on a device a
    /// dialog is an overlay inside the editor's own tree, so the editor's
    /// controller and the dialog's both translate its labels. With a table
    /// each, the second one took the first one's English output for the source
    /// and the label then stopped following language switches — or, through
    /// the reverse lookup, came back meaning something else.
    /// </summary>
    private static readonly ConditionalWeakTable<AvaloniaObject, TrackedTextSet> TrackedTexts = new();

    private static readonly ConditionalWeakTable<Control, AvaloniaLocalizationController> ControllersByRoot = new();
    private static readonly List<WeakReference<AvaloniaLocalizationController>> LiveControllers = [];
    private static readonly object ObservedMarker = new();
    private static bool attachHooksInstalled;

    private readonly Control root;
    private readonly List<WeakReference<Control>> additionalRoots = [];

    // Weak on purpose. A HashSet<Control> here kept every control the editor
    // had ever scanned alive for the life of the window, and on a device every
    // dialog is an overlay inside that window, so each dialog shown leaked.
    private readonly ConditionalWeakTable<Control, object> observedControls = new();

    // Work waiting for the next Background pass. Strong only until that pass.
    private readonly HashSet<Control> pendingControls = [];
    private readonly HashSet<Control> pendingSubtrees = [];
    private AvaloniaLanguagePreference preference;
    private bool applyQueued;
    private bool pendingQueued;

    public AvaloniaLocalizationController(
        Control root,
        AvaloniaLanguagePreference preference)
    {
        this.root = root ?? throw new ArgumentNullException(nameof(root));
        this.preference = NormalizePreference(preference);
        EffectiveLanguage = AvaloniaLanguageSettings.ResolveEffectiveLanguage(this.preference);
        Register(this);
    }

    public AvaloniaLanguagePreference Preference => preference;

    public AvaloniaDisplayLanguage EffectiveLanguage { get; private set; }

    public void SetPreference(AvaloniaLanguagePreference value)
    {
        preference = NormalizePreference(value);
        Apply();
    }

    /// <summary>
    /// Re-evaluates a Follow system setting after the app regains activation.
    /// Returns whether the effective language changed.
    /// </summary>
    public bool RefreshForSystemLanguage()
    {
        // Runs on every window activation. Only a change in the resolved
        // language needs a pass over the tree; labels changed in the meantime
        // are already kept current one by one.
        if (AvaloniaLanguageSettings.ResolveEffectiveLanguage(preference) == EffectiveLanguage)
            return false;
        Apply();
        return true;
    }

    public void Apply()
    {
        EffectiveLanguage = AvaloniaLanguageSettings.ResolveEffectiveLanguage(preference);
        LumaChart.Core.Localization.LocalizationCatalog.CurrentLanguage = EffectiveLanguage;
        // One visited set for the whole pass: the extra roots are popups and
        // combo boxes inside the main root, which were otherwise done twice.
        var visited = new HashSet<Control>();
        ApplyTree(root, visited);
        for (var index = additionalRoots.Count - 1; index >= 0; index--)
        {
            if (additionalRoots[index].TryGetTarget(out var target))
                ApplyTree(target, visited);
            else
                additionalRoots.RemoveAt(index);
        }
    }

    public void Apply(Control target)
    {
        ArgumentNullException.ThrowIfNull(target);
        EffectiveLanguage = AvaloniaLanguageSettings.ResolveEffectiveLanguage(preference);
        LumaChart.Core.Localization.LocalizationCatalog.CurrentLanguage = EffectiveLanguage;
        RememberAdditionalRoot(target);
        ApplyTree(target, new HashSet<Control>());
    }

    private void ApplyTree(Control target, ISet<Control> visited)
    {
        foreach (var control in target.GetSelfAndVisualDescendants().OfType<Control>())
            ApplyToControl(control, visited);
        foreach (var control in target.GetSelfAndLogicalDescendants().OfType<Control>())
            ApplyToControl(control, visited);
    }

    /// <summary>
    /// Translates only the controls in <paramref name="target"/> that this
    /// controller has not seen yet. A control it has seen is kept current by its
    /// own change handler and by <see cref="Apply()"/> on a language switch, so
    /// a re-attached subtree — a dialog shown again, the editor moved into a
    /// modal overlay — costs a walk, not a retranslation.
    /// </summary>
    private void ApplyNewControls(Control target, ISet<Control> visited)
    {
        foreach (var control in target.GetSelfAndVisualDescendants().OfType<Control>())
        {
            if (!observedControls.TryGetValue(control, out _))
                ApplyToControl(control, visited);
        }
        foreach (var control in target.GetSelfAndLogicalDescendants().OfType<Control>())
        {
            if (!observedControls.TryGetValue(control, out _))
                ApplyToControl(control, visited);
        }
    }

    private void RememberAdditionalRoot(Control target)
    {
        if (ReferenceEquals(target, root)) return;
        for (var index = additionalRoots.Count - 1; index >= 0; index--)
        {
            if (!additionalRoots[index].TryGetTarget(out var existing))
            {
                additionalRoots.RemoveAt(index);
                continue;
            }
            if (ReferenceEquals(existing, target)) return;
        }
        additionalRoots.Add(new WeakReference<Control>(target));
    }

    /// <summary>
    /// Defers a root scan until the current input event completes. This is used
    /// for tab and popup content that Avalonia creates after the initial tree.
    /// It is an explicit, occasional request; a label change no longer calls it.
    /// </summary>
    public void ScheduleApply()
    {
        if (applyQueued) return;
        applyQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                applyQueued = false;
                Apply();
            },
            DispatcherPriority.Background);
    }

    public void ScheduleApply(Control target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Dispatcher.UIThread.Post(
            () => Apply(target),
            DispatcherPriority.Background);
    }

    public string Translate(string sourceText) =>
        AvaloniaLocalizationCatalog.Translate(sourceText, EffectiveLanguage);

    private void ApplyToControl(Control control, ISet<Control> visited)
    {
        if (!visited.Add(control)) return;
        ObserveControl(control);

        if (control is TextBlock textBlock && textBlock.Text is { } text)
            textBlock.Text = TranslateTracked(control, TextBlockText, text);

        if (control is ContentControl contentControl && contentControl.Content is string content)
            contentControl.Content = TranslateTracked(control, ContentControlContent, content);

        if (control is HeaderedContentControl headeredControl && headeredControl.Header is string header)
            headeredControl.Header = TranslateTracked(control, HeaderedControlHeader, header);

        if (control is TextBox textBox && textBox.PlaceholderText is { } placeholderText)
            textBox.PlaceholderText = TranslateTracked(
                control,
                TextBoxPlaceholder,
                placeholderText);

        if (ToolTip.GetTip(control) is string toolTip)
            ToolTip.SetTip(control, TranslateTracked(control, ToolTipText, toolTip));

        // The window title is the one label a user sees before anything else,
        // in the title bar and the taskbar, and it was the last one still
        // showing Chinese with every other label already translated.
        if (control is Window window && window.Title is { } title)
            window.Title = TranslateTracked(control, WindowTitle, title);

        if (AutomationProperties.GetName(control) is { } automationName &&
            automationName.Length > 0)
            AutomationProperties.SetName(
                control,
                TranslateTracked(control, AutomationName, automationName));
    }

    private void ObserveControl(Control control)
    {
        if (observedControls.TryGetValue(control, out _)) return;
        observedControls.Add(control, ObservedMarker);
        if (control is ComboBox comboBox)
        {
            // Avalonia creates the drop-down item presenters only when the
            // popup opens. Re-scan after materialization so ItemsSource text
            // does not remain in the source language while the selection is
            // already localized.
            comboBox.DropDownOpened += (_, _) => ScheduleComboBoxPopupApply(comboBox);
        }
        control.PropertyChanged += (_, args) => OnObservedPropertyChanged(control, args);
    }

    /// <summary>
    /// Retranslates the one control whose label changed.
    ///
    /// This used to schedule a scan of the whole visual and logical tree for
    /// every label change anywhere. A status line rewritten each frame therefore
    /// rescanned the entire editor each frame, twice over while a second
    /// controller shared the root. Controls that appear later are found by the
    /// attach hook instead (see <see cref="OnAttachedToParent"/>), so nothing
    /// here needs the tree.
    /// </summary>
    private void OnObservedPropertyChanged(Control control, AvaloniaPropertyChangedEventArgs args)
    {
        var propertyName = TrackedPropertyName(control, args.Property);
        if (propertyName == null || args.NewValue is not string value) return;

        // The controller's own write comes straight back through here. So does
        // code assigning the label it already shows; neither needs any work,
        // which is what keeps a value rewritten every frame from costing a pass.
        if (IsLastApplied(control, propertyName, value)) return;
        if (!Dispatcher.UIThread.CheckAccess()) return;
        pendingControls.Add(control);
        QueuePendingPass();
    }

    private static string? TrackedPropertyName(Control control, AvaloniaProperty property)
    {
        // TextBox shares TextBlock's Text property, so the owner type is checked
        // as well: typing into a TextBox is not a label change.
        if (property == TextBlock.TextProperty) return control is TextBlock ? TextBlockText : null;
        if (property == ContentControl.ContentProperty)
            return control is ContentControl ? ContentControlContent : null;
        if (property == HeaderedContentControl.HeaderProperty)
            return control is HeaderedContentControl ? HeaderedControlHeader : null;
        if (property == TextBox.PlaceholderTextProperty)
            return control is TextBox ? TextBoxPlaceholder : null;
        if (property == ToolTip.TipProperty) return ToolTipText;
        if (property == Window.TitleProperty) return control is Window ? WindowTitle : null;
        if (property == AutomationProperties.NameProperty) return AutomationName;
        return null;
    }

    private static bool IsLastApplied(AvaloniaObject owner, string propertyName, string value) =>
        TrackedTexts.TryGetValue(owner, out var values) &&
        values.Values.TryGetValue(propertyName, out var tracked) &&
        string.Equals(tracked.LastApplied, value, StringComparison.Ordinal);

    private void QueuePendingPass()
    {
        if (pendingQueued) return;
        pendingQueued = true;
        Dispatcher.UIThread.Post(RunPendingPass, DispatcherPriority.Background);
    }

    private void RunPendingPass()
    {
        pendingQueued = false;
        if (pendingControls.Count == 0 && pendingSubtrees.Count == 0) return;
        var controls = pendingControls.ToArray();
        var subtrees = pendingSubtrees.ToArray();
        pendingControls.Clear();
        pendingSubtrees.Clear();

        var visited = new HashSet<Control>();
        var queued = new HashSet<Control>(subtrees);
        foreach (var subtree in subtrees)
        {
            // Attaching a panel raises the hook for it and then again for every
            // templated part inside it; walking only the outermost one keeps the
            // pass linear in what was added.
            if (HasQueuedAncestor(subtree, queued) || !Covers(subtree)) continue;
            ApplyNewControls(subtree, visited);
        }
        foreach (var control in controls)
            ApplyToControl(control, visited);
    }

    private static bool HasQueuedAncestor(Control control, ISet<Control> queued)
    {
        for (var element = control.Parent; element != null; element = element.Parent)
        {
            if (element is Control ancestor && queued.Contains(ancestor)) return true;
        }
        for (var visual = control.GetVisualParent(); visual != null; visual = visual.GetVisualParent())
        {
            if (visual is Control ancestor && queued.Contains(ancestor)) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a control sits under this controller's root or one of the extra
    /// roots it was handed. The logical chain is what reaches a popup's content
    /// (its visual root is the popup window), the visual chain what reaches a
    /// template's parts.
    /// </summary>
    private bool Covers(Control control)
    {
        if (IsWithin(control, root)) return true;
        foreach (var reference in additionalRoots)
        {
            if (reference.TryGetTarget(out var extra) && IsWithin(control, extra)) return true;
        }
        return false;
    }

    private static bool IsWithin(Control control, Control ancestor)
    {
        for (StyledElement? element = control; element != null; element = element.Parent)
        {
            if (ReferenceEquals(element, ancestor)) return true;
        }
        for (Visual? visual = control; visual != null; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, ancestor)) return true;
        }
        return false;
    }

    internal static bool TryGetForRoot(
        Control root,
        [NotNullWhen(true)] out AvaloniaLocalizationController? controller) =>
        ControllersByRoot.TryGetValue(root, out controller);

    private static void Register(AvaloniaLocalizationController controller)
    {
        ControllersByRoot.AddOrUpdate(controller.root, controller);
        lock (LiveControllers)
        {
            LiveControllers.RemoveAll(reference => !reference.TryGetTarget(out _));
            LiveControllers.Add(new WeakReference<AvaloniaLocalizationController>(controller));
            if (attachHooksInstalled) return;
            attachHooksInstalled = true;
        }

        // Whatever is added to a tree after its first scan — a rebuilt list, a
        // new tab body, a dialog's content — gets a new parent, and only the top
        // of the added subtree does. Both chains are watched because a template
        // part has a visual parent and no logical one.
        StyledElement.ParentProperty.Changed.AddClassHandler<Control>(OnParentChanged);
        Visual.VisualParentProperty.Changed.AddClassHandler<Control>(OnParentChanged);
    }

    private static void OnParentChanged(Control control, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.NewValue is null) return;
        OnAttachedToParent(control);
    }

    /// <summary>
    /// Queues a newly attached subtree for every controller whose tree it
    /// joined. This is what replaced the per-label full rescan as the way late
    /// content gets translated.
    /// </summary>
    private static void OnAttachedToParent(Control control)
    {
        // Runs for every parent change in the process, template parts included,
        // so it allocates nothing and only walks up from the control.
        if (!Dispatcher.UIThread.CheckAccess()) return;
        lock (LiveControllers)
        {
            for (var index = LiveControllers.Count - 1; index >= 0; index--)
            {
                if (!LiveControllers[index].TryGetTarget(out var controller))
                {
                    LiveControllers.RemoveAt(index);
                    continue;
                }
                if (!controller.Covers(control)) continue;
                controller.pendingSubtrees.Add(control);
                controller.QueuePendingPass();
            }
        }
    }

    private void ScheduleComboBoxPopupApply(ComboBox comboBox)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                Apply(comboBox);
                var visited = new HashSet<Popup>();
                foreach (var popup in comboBox
                             .GetSelfAndVisualDescendants()
                             .OfType<Popup>())
                {
                    if (visited.Add(popup) && popup.Child is Control child)
                        Apply(child);
                }
                foreach (var popup in comboBox
                             .GetSelfAndLogicalDescendants()
                             .OfType<Popup>())
                {
                    if (visited.Add(popup) && popup.Child is Control child)
                        Apply(child);
                }
            },
            DispatcherPriority.Background);
    }

    private string TranslateTracked(AvaloniaObject owner, string propertyName, string currentValue)
    {
        var values = TrackedTexts.GetValue(owner, static _ => new TrackedTextSet());
        if (!values.Values.TryGetValue(propertyName, out var value))
        {
            value = new TrackedText(currentValue);
            values.Values[propertyName] = value;
        }
        else if (!string.Equals(currentValue, value.LastApplied, StringComparison.Ordinal))
        {
            // Application code wrote a new value after the last pass; anything
            // else keeps the remembered source, which is what a language switch
            // translates from. The new value is nearly always Chinese source
            // text and is taken as it is. Only a label code wrote already
            // translated into the current language is mapped back, and only
            // when exactly one source gives that text — reading the source back
            // out of an ambiguous translation is how "待处理" shown as "Open"
            // became "打开" and then "開く" after a switch to Japanese.
            value.Source = AvaloniaLocalizationCatalog.TryResolveSourceText(
                currentValue,
                EffectiveLanguage,
                out var sourceText)
                ? sourceText
                : currentValue;
            value.Composed = false;
        }

        // A message built by AvaloniaLocalizationCatalog.Format arrives already
        // in a display language, so it has no Chinese source to look up. It is
        // rebuilt from its own template instead, which is what lets a status
        // line follow a language switch instead of freezing in whichever
        // language it was first written in.
        if (AvaloniaLocalizationCatalog.TryReformat(
                value.Composed ? currentValue : value.Source,
                EffectiveLanguage,
                out var recomposed))
        {
            value.Composed = true;
            value.LastApplied = recomposed;
            return recomposed;
        }

        var translated = Translate(value.Source);
        value.LastApplied = translated;
        return translated;
    }

    private static AvaloniaLanguagePreference NormalizePreference(
        AvaloniaLanguagePreference value) =>
        Enum.IsDefined(value)
            ? value
            : AvaloniaLanguagePreference.FollowSystem;

    private sealed class TrackedTextSet
    {
        public Dictionary<string, TrackedText> Values { get; } = new(StringComparer.Ordinal);
    }

    private sealed class TrackedText
    {
        public TrackedText(string source)
        {
            Source = source;
        }

        public string Source { get; set; }
        public string? LastApplied { get; set; }

        /// <summary>
        /// Whether the last value came from a formatted template rather than a
        /// catalog lookup, so the next pass re-renders it from that template.
        /// </summary>
        public bool Composed { get; set; }
    }
}

/// <summary>
/// Shares the current display-language preference with secondary windows.
/// Avalonia dialogs are separate control trees, so registering them here keeps
/// their initial and later-created content in the same language as the editor.
/// </summary>
public static class AvaloniaLocalizationRuntime
{
    private static readonly List<WeakReference<AvaloniaLocalizationController>> Controllers = [];
    private static AvaloniaLanguagePreference preference = AvaloniaLanguagePreference.FollowSystem;

    public static AvaloniaLanguagePreference Preference => preference;

    /// <summary>
    /// The language runtime messages should be built in right now. Composed
    /// messages are formatted at their call site, before any controller sees
    /// them, so they need the current language without going through a control.
    /// </summary>
    public static AvaloniaDisplayLanguage EffectiveLanguage =>
        AvaloniaLanguageSettings.ResolveEffectiveLanguage(preference);

    public static void SetPreference(AvaloniaLanguagePreference value)
    {
        preference = Enum.IsDefined(value)
            ? value
            : AvaloniaLanguagePreference.FollowSystem;
        // The layers below the UI compose their own messages and cannot read a
        // window's preference, so the resolved language is pushed down to them.
        LumaChart.Core.Localization.LocalizationCatalog.CurrentLanguage = EffectiveLanguage;
        for (var index = Controllers.Count - 1; index >= 0; index--)
        {
            if (Controllers[index].TryGetTarget(out var controller))
                controller.SetPreference(preference);
            else
                Controllers.RemoveAt(index);
        }
    }

    public static AvaloniaLocalizationController Attach(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);

        // One controller per root. MainWindow builds its own before it opens and
        // PlatformWindow attaches again on Opened, so the editor used to carry
        // two controllers over the same tree and every pass ran twice; a mobile
        // dialog raises Opened each time it is shown and gained one per showing.
        if (AvaloniaLocalizationController.TryGetForRoot(root, out var existing))
        {
            if (existing.Preference != preference)
                existing.SetPreference(preference);
            else
                existing.ScheduleApply();
            return existing;
        }

        var controller = new AvaloniaLocalizationController(root, preference);
        Controllers.Add(new WeakReference<AvaloniaLocalizationController>(controller));
        controller.Apply();
        return controller;
    }
}
