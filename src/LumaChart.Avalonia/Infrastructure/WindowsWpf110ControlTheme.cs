using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;
using AvaloniaRectangle = Avalonia.Controls.Shapes.Rectangle;

namespace LumaChart.Avalonia.Infrastructure;

public static class WindowsWpf110ControlTheme
{
    public const string ContractVersion = "wpf-1.1.0-controls-r5";
    public const int PlatformIndependentInteractionStyleCount = 30;
    public const int WindowsRuntimeInteractionStyleCount = 33;
    public const string TextFontFamilyName =
        "Segoe UI Variable Text, Microsoft YaHei UI";
    public const double BaseFontSize = 12d;

    private const string InstalledResourceKey =
        "LumaChart.Windows.Wpf110ControlTheme.ContractVersion";

    public static IReadOnlyDictionary<string, object> CreateResourceOverrides() =>
        new Dictionary<string, object>(StringComparer.Ordinal)
        {
            // WPF App.xaml ToolButton / PrimaryButton.
            ["ButtonBackground"] = Brush("#1A1D20"),
            ["ButtonForeground"] = Brush("#D0D4D6"),
            ["ButtonBorderBrush"] = Brush("#34393D"),
            ["ButtonBackgroundPointerOver"] = Brush("#22262A"),
            ["ButtonForegroundPointerOver"] = Brush("#F1F2F2"),
            ["ButtonBorderBrushPointerOver"] = Brush("#4A5055"),
            ["ButtonBackgroundPressed"] = Brush("#282D30"),
            ["ButtonForegroundPressed"] = Brush("#F1F2F2"),
            ["ButtonBorderBrushPressed"] = Brush("#4A5055"),
            ["ButtonBackgroundDisabled"] = Brush("#1A1D20"),
            ["ButtonForegroundDisabled"] = Brush("#D0D4D6"),
            ["ButtonBorderBrushDisabled"] = Brush("#34393D"),
            ["AccentButtonBackground"] = Brush("#38676C"),
            ["AccentButtonForeground"] = Brush("#FFFFFF"),
            ["AccentButtonBorderBrush"] = Brush("#4A777B"),
            ["AccentButtonBackgroundPointerOver"] = Brush("#416F74"),
            ["AccentButtonForegroundPointerOver"] = Brush("#FFFFFF"),
            ["AccentButtonBorderBrushPointerOver"] = Brush("#628C90"),
            ["AccentButtonBackgroundPressed"] = Brush("#38676C"),
            ["AccentButtonForegroundPressed"] = Brush("#FFFFFF"),
            ["AccentButtonBorderBrushPressed"] = Brush("#4A777B"),
            ["AccentButtonBackgroundDisabled"] = Brush("#38676C"),
            ["AccentButtonForegroundDisabled"] = Brush("#FFFFFF"),
            ["AccentButtonBorderBrushDisabled"] = Brush("#4A777B"),
            ["ButtonBorderThemeThickness"] = new Thickness(1),
            ["ButtonPadding"] = new Thickness(9, 6),
            ["ControlCornerRadius"] = new CornerRadius(3),

            // WPF App.xaml default TextBox.
            ["TextControlBackground"] = Brush("#15181A"),
            ["TextControlBackgroundPointerOver"] = Brush("#15181A"),
            ["TextControlBackgroundFocused"] = Brush("#15181A"),
            ["TextControlBackgroundDisabled"] = Brush("#15181A"),
            ["TextControlForeground"] = Brush("#E3E6E8"),
            ["TextControlForegroundPointerOver"] = Brush("#E3E6E8"),
            ["TextControlForegroundFocused"] = Brush("#E3E6E8"),
            ["TextControlForegroundDisabled"] = Brush("#E3E6E8"),
            ["TextControlBorderBrush"] = Brush("#383E43"),
            ["TextControlBorderBrushPointerOver"] = Brush("#383E43"),
            ["TextControlBorderBrushFocused"] = Brush("#718F92"),
            ["TextControlBorderBrushDisabled"] = Brush("#383E43"),
            ["TextControlSelectionHighlightColor"] = Brush("#526E71"),
            ["TextControlPlaceholderForeground"] = Brush("#929A9F"),
            ["TextControlPlaceholderForegroundPointerOver"] = Brush("#929A9F"),
            ["TextControlPlaceholderForegroundFocused"] = Brush("#929A9F"),
            ["TextControlPlaceholderForegroundDisabled"] = Brush("#929A9F"),
            ["TextControlBorderThemeThickness"] = new Thickness(1),
            ["TextControlBorderThemeThicknessFocused"] = new Thickness(1),
            ["TextControlThemeMinHeight"] = 30d,
            ["TextControlThemePadding"] = new Thickness(8, 6),

            // WPF App.xaml default ComboBox and ComboBoxItem.
            ["ComboBoxBackground"] = Brush("#15181A"),
            ["ComboBoxBackgroundPointerOver"] = Brush("#1B1F22"),
            ["ComboBoxBackgroundPressed"] = Brush("#15181A"),
            ["ComboBoxBackgroundDisabled"] = Brush("#15181A"),
            ["ComboBoxBackgroundUnfocused"] = Brushes.Transparent,
            ["ComboBoxBackgroundBorderBrushFocused"] = Brush("#718F92"),
            ["ComboBoxBackgroundBorderBrushUnfocused"] = Brushes.Transparent,
            ["ComboBoxForeground"] = Brush("#E3E6E8"),
            ["ComboBoxForegroundDisabled"] = Brush("#E3E6E8"),
            ["ComboBoxForegroundFocused"] = Brush("#E3E6E8"),
            ["ComboBoxForegroundFocusedPressed"] = Brush("#E3E6E8"),
            ["ComboBoxPlaceHolderForeground"] = Brush("#929A9F"),
            ["ComboBoxPlaceHolderForegroundFocusedPressed"] = Brush("#929A9F"),
            ["ComboBoxBorderBrush"] = Brush("#383E43"),
            ["ComboBoxBorderBrushPointerOver"] = Brush("#596166"),
            ["ComboBoxBorderBrushPressed"] = Brush("#596166"),
            ["ComboBoxBorderBrushDisabled"] = Brush("#383E43"),
            ["ComboBoxDropDownGlyphForeground"] = Brush("#9DA5AA"),
            ["ComboBoxDropDownGlyphForegroundDisabled"] = Brush("#9DA5AA"),
            ["ComboBoxDropDownGlyphForegroundFocused"] = Brush("#9DA5AA"),
            ["ComboBoxDropDownGlyphForegroundFocusedPressed"] = Brush("#9DA5AA"),
            ["ComboBoxDropDownBackground"] = Brush("#1B1E21"),
            ["ComboBoxDropDownBorderBrush"] = Brush("#3B4146"),
            ["ComboBoxDropdownBorderThickness"] = new Thickness(1),
            ["ComboBoxDropdownBorderPadding"] = new Thickness(3),
            ["ComboBoxDropdownContentMargin"] = new Thickness(0),
            ["ComboBoxBorderThemeThickness"] = new Thickness(1),
            ["ComboBoxPadding"] = new Thickness(8, 6),
            ["ComboBoxEditableTextPadding"] = new Thickness(8, 6, 30, 6),
            ["ComboBoxMinHeight"] = 30d,
            ["ComboBoxItemForeground"] = Brush("#E3E6E8"),
            ["ComboBoxItemForegroundPointerOver"] = Brush("#E3E6E8"),
            ["ComboBoxItemForegroundPressed"] = Brush("#FFFFFF"),
            ["ComboBoxItemForegroundSelected"] = Brush("#FFFFFF"),
            ["ComboBoxItemForegroundSelectedPointerOver"] = Brush("#FFFFFF"),
            ["ComboBoxItemForegroundSelectedPressed"] = Brush("#FFFFFF"),
            ["ComboBoxItemForegroundDisabled"] = Brush("#929A9F"),
            ["ComboBoxItemForegroundSelectedDisabled"] = Brush("#929A9F"),
            ["ComboBoxItemBackground"] = Brush("#1B1E21"),
            ["ComboBoxItemBackgroundPointerOver"] = Brush("#262B2E"),
            ["ComboBoxItemBackgroundPressed"] = Brush("#262B2E"),
            ["ComboBoxItemBackgroundSelected"] = Brush("#314044"),
            ["ComboBoxItemBackgroundSelectedPointerOver"] = Brush("#314044"),
            ["ComboBoxItemBackgroundSelectedPressed"] = Brush("#314044"),
            ["ComboBoxItemBackgroundDisabled"] = Brush("#1B1E21"),
            ["ComboBoxItemBackgroundSelectedDisabled"] = Brush("#1B1E21"),
            ["ComboBoxItemBorderBrushPointerOver"] = Brushes.Transparent,
            ["ComboBoxItemBorderBrushPressed"] = Brushes.Transparent,
            ["ComboBoxItemBorderBrushSelected"] = Brushes.Transparent,
            ["ComboBoxItemBorderBrushSelectedPointerOver"] = Brushes.Transparent,
            ["ComboBoxItemBorderBrushSelectedPressed"] = Brushes.Transparent,
            ["ComboBoxItemBorderBrushDisabled"] = Brushes.Transparent,
            ["ComboBoxItemBorderBrushSelectedDisabled"] = Brushes.Transparent,
            ["ComboBoxItemThemePadding"] = new Thickness(9, 6),
            ["OverlayCornerRadius"] = new CornerRadius(3),

            // WPF App.xaml compact ScrollBar. Avalonia's proven Track remains in
            // place; only its dimensions and state resources are replaced.
            ["ScrollBarSize"] = 9d,
            ["ScrollBarBackground"] = Brushes.Transparent,
            ["ScrollBarBackgroundPointerOver"] = Brushes.Transparent,
            ["ScrollBarTrackFill"] = Brush("#272E32"),
            ["ScrollBarTrackFillPointerOver"] = Brush("#30393D"),
            ["ScrollBarTrackStroke"] = Brushes.Transparent,
            ["ScrollBarTrackStrokePointerOver"] = Brushes.Transparent,
            ["ScrollBarThumbBackgroundColor"] = Color.Parse("#566267"),
            ["ScrollBarPanningThumbBackgroundColor"] = Color.Parse("#566267"),
            ["ScrollBarPanningThumbBackground"] = Brush("#566267"),
            ["ScrollBarThumbFillPointerOver"] = Brush("#718185"),
            ["ScrollBarThumbFillPressed"] = Brush("#8AA9AC"),
            ["ScrollBarThumbFillDisabled"] = Brush("#566267"),
            ["ScrollBarTrackBorderThemeThickness"] = 0d,
            ["ScrollBarButtonArrowIconFontSize"] = 0d,

            // WPF App.xaml default Slider. The Fluent Track and Slider input
            // behavior stay in place; the thumb receives an exact visual body
            // inside the WPF 26x28 pointer target in Apply().
            ["SliderHorizontalHeight"] = 28d,
            ["SliderVerticalWidth"] = 28d,
            ["SliderPreContentMargin"] = new GridLength(0),
            ["SliderPostContentMargin"] = new GridLength(0),
            ["SliderTrackThemeHeight"] = 3d,
            ["SliderBorderThemeThickness"] = new Thickness(0),
            ["SliderContainerBackground"] = Brushes.Transparent,
            ["SliderContainerBackgroundPointerOver"] = Brushes.Transparent,
            ["SliderContainerBackgroundPressed"] = Brushes.Transparent,
            ["SliderContainerBackgroundDisabled"] = Brushes.Transparent,
            ["SliderTrackFill"] = Brush("#30363A"),
            ["SliderTrackFillPointerOver"] = Brush("#30363A"),
            ["SliderTrackFillPressed"] = Brush("#30363A"),
            ["SliderTrackFillDisabled"] = Brush("#30363A"),
            ["SliderTrackValueFill"] = Brush("#718F92"),
            ["SliderTrackValueFillPointerOver"] = Brush("#718F92"),
            ["SliderTrackValueFillPressed"] = Brush("#718F92"),
            ["SliderTrackValueFillDisabled"] = Brush("#718F92"),
            ["SliderThumbBackground"] = Brush("#B7BFC2"),
            ["SliderThumbBackgroundPointerOver"] = Brush("#D0D7D9"),
            ["SliderThumbBackgroundPressed"] = Brush("#E0E7E8"),
            ["SliderThumbBackgroundDisabled"] = Brush("#B7BFC2"),
            ["SliderHorizontalThumbWidth"] = 26d,
            ["SliderHorizontalThumbHeight"] = 28d,
            ["SliderVerticalThumbWidth"] = 28d,
            ["SliderVerticalThumbHeight"] = 26d,
            ["LumaChartSliderThumbBodyWidth"] = 11d,
            ["LumaChartSliderThumbBodyHeight"] = 16d,
            ["LumaChartSliderThumbBodyCornerRadius"] = new CornerRadius(2),
            ["LumaChartSliderThumbBorder"] = Brush("#687176"),
            ["LumaChartSliderThumbBorderPointerOver"] = Brush("#7E999C"),
            ["LumaChartSliderThumbBorderPressed"] = Brush("#8FB0B3"),

            // WPF App.xaml FlatCheckBox. A compact template is installed in
            // Apply() so the 16-DIP box and stroked check do not inherit the
            // Fluent 20-DIP filled-glyph geometry.
            ["CheckBoxForegroundUnchecked"] = Brush("#C8D0D2"),
            ["CheckBoxForegroundUncheckedPointerOver"] = Brush("#C8D0D2"),
            ["CheckBoxForegroundUncheckedPressed"] = Brush("#C8D0D2"),
            ["CheckBoxForegroundUncheckedDisabled"] = Brush("#C8D0D2"),
            ["CheckBoxForegroundChecked"] = Brush("#C8D0D2"),
            ["CheckBoxForegroundCheckedPointerOver"] = Brush("#C8D0D2"),
            ["CheckBoxForegroundCheckedPressed"] = Brush("#C8D0D2"),
            ["CheckBoxForegroundCheckedDisabled"] = Brush("#C8D0D2"),
            ["CheckBoxCheckBackgroundFillUnchecked"] = Brush("#15191B"),
            ["CheckBoxCheckBackgroundStrokeUnchecked"] = Brush("#465156"),
            ["CheckBoxCheckBackgroundStrokeUncheckedPointerOver"] = Brush("#718D90"),
            ["CheckBoxCheckBackgroundFillChecked"] = Brush("#52777B"),
            ["CheckBoxCheckBackgroundStrokeCheckedPointerOver"] = Brush("#70979A"),
            ["CheckBoxCheckGlyphForegroundChecked"] = Brush("#F1F5F5"),
            ["LumaChartCheckBoxSize"] = 16d,
            ["LumaChartCheckBoxContentGap"] = 7d,
            ["LumaChartCheckBoxCornerRadius"] = new CornerRadius(3),

            // WPF App.xaml ToolTip.
            ["ToolTipBackground"] = Brush("#22262A"),
            ["ToolTipForeground"] = Brush("#E3E6E8"),
            ["ToolTipBorderBrush"] = Brush("#3A4045"),
            ["ToolTipBorderThemeThickness"] = new Thickness(1),
            ["ToolTipBorderThemePadding"] = new Thickness(8, 5),
            ["ToolTipContentThemeFontSize"] = 12d,

            // WPF MainWindow quick/library ContextMenu family.
            ["MenuFlyoutPresenterBackground"] = Brush("#1A1E21"),
            ["MenuFlyoutPresenterBorderBrush"] = Brush("#3A4246"),
            ["MenuFlyoutPresenterBorderThemeThickness"] = new Thickness(1),
            ["MenuFlyoutPresenterThemePadding"] = new Thickness(4),
            ["MenuFlyoutScrollerMargin"] = new Thickness(0),
            ["MenuFlyoutThemeMinHeight"] = 0d,
            ["MenuFlyoutItemBackground"] = Brushes.Transparent,
            ["MenuFlyoutItemBackgroundPointerOver"] = Brush("#262B2E"),
            ["MenuFlyoutItemBackgroundPressed"] = Brush("#30363A"),
            ["MenuFlyoutItemBackgroundDisabled"] = Brushes.Transparent,
            ["MenuFlyoutItemForeground"] = Brush("#E7EAEB"),
            ["MenuFlyoutItemForegroundPointerOver"] = Brush("#FFFFFF"),
            ["MenuFlyoutItemForegroundPressed"] = Brush("#FFFFFF"),
            ["MenuFlyoutItemForegroundDisabled"] = Brush("#929A9F"),
            ["MenuFlyoutItemThemePaddingNarrow"] = new Thickness(13, 7),

            // WPF MainWindow RightTab.
            ["LumaChartTabItemForeground"] = Brush("#929A9F"),
            ["LumaChartTabItemForegroundPointerOver"] = Brush("#D6DADC"),
            ["LumaChartTabItemForegroundSelected"] = Brush("#FFFFFF"),
            ["LumaChartTabItemBackground"] = Brushes.Transparent,
            ["LumaChartTabItemBackgroundPointerOver"] = Brush("#202427"),
            ["LumaChartTabItemBackgroundSelected"] = Brush("#22272A"),
            ["LumaChartTabItemBorderBrush"] = Brush("#7FA4A7"),
            ["LumaChartTabItemPadding"] = new Thickness(7, 10),
            ["LumaChartTabItemBorderThicknessSelected"] =
                new Thickness(0, 0, 0, 2),

            // WPF MainWindow LibraryFolderDeleteMenuItem.
            ["LumaChartDangerMenuItemForeground"] = Brush("#FFD4D4"),
            ["LumaChartDangerMenuItemForegroundPointerOver"] = Brush("#FFEEEE"),
            ["LumaChartDangerMenuItemBackgroundPointerOver"] = Brush("#4A292B"),
            ["LumaChartDangerMenuItemPadding"] = new Thickness(13, 8),
            ["LumaChartDangerMenuItemMinWidth"] = 190d
        };

    public static void Apply(global::Avalonia.Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (application.Resources.TryGetResource(
                InstalledResourceKey,
                application.ActualThemeVariant,
                out _))
            return;

        foreach (var resource in CreateResourceOverrides())
            application.Resources[resource.Key] = resource.Value;
        application.Resources[InstalledResourceKey] = ContractVersion;

        // WPF applies Segoe UI Variable Text to TextBlock globally and its
        // Windows control font is 12 DIP. MainWindow already carried that
        // baseline explicitly, but independent Avalonia windows otherwise
        // fell back to the Fluent theme font. Besides changing glyphs, that
        // fallback changes ascent/descent and therefore makes otherwise exact
        // padding appear vertically off-centre. Install both inheritable
        // control typography and WPF's explicit TextBlock family so dialogs,
        // popups and the main surface share one stable metric baseline.
        var textFontFamily = new FontFamily(TextFontFamilyName);
        var controlTypography = new Style(
            selector => selector.OfType<TemplatedControl>());
        controlTypography.Setters.Add(new Setter(
            TemplatedControl.FontFamilyProperty,
            textFontFamily));
        controlTypography.Setters.Add(new Setter(
            TemplatedControl.FontSizeProperty,
            BaseFontSize));
        application.Styles.Add(controlTypography);

        var textTypography = new Style(selector => selector.OfType<TextBlock>());
        textTypography.Setters.Add(new Setter(
            TextBlock.FontFamilyProperty,
            textFontFamily));
        textTypography.Setters.Add(new Setter(
            TextBlock.FontSizeProperty,
            BaseFontSize));
        application.Styles.Add(textTypography);

        // The headless Specs host intentionally has no ICursorFactory. The real
        // Win32 application does, so install the WPF hand cursor there without
        // making the pure resource contract depend on a desktop platform.
        try
        {
            var buttonCursor = new Style(selector => selector.OfType<Button>());
            buttonCursor.Setters.Add(new Setter(
                InputElement.CursorProperty,
                new Cursor(StandardCursorType.Hand)));
            application.Styles.Add(buttonCursor);

            var checkBoxCursor = new Style(selector => selector.OfType<CheckBox>());
            checkBoxCursor.Setters.Add(new Setter(
                InputElement.CursorProperty,
                new Cursor(StandardCursorType.Hand)));
            application.Styles.Add(checkBoxCursor);

            var sliderThumbCursor = new Style(selector => selector
                .OfType<Slider>()
                .Template()
                .OfType<Thumb>());
            sliderThumbCursor.Setters.Add(new Setter(
                InputElement.CursorProperty,
                new Cursor(StandardCursorType.Hand)));
            application.Styles.Add(sliderThumbCursor);
        }
        catch (InvalidOperationException exception)
            when (exception.Message.Contains(
                "Avalonia.Platform.ICursorFactory",
                StringComparison.Ordinal))
        {
        }

        // WPF ToolButton renders every content presenter in the visual centre.
        // Keep this global so icon-only, text-only and mixed-content buttons
        // share the same baseline. Deliberately left-aligned button rows retain
        // their local HorizontalContentAlignment value.
        var buttonContentAlignment =
            new Style(selector => selector.OfType<Button>());
        buttonContentAlignment.Setters.Add(new Setter(
            ContentControl.HorizontalContentAlignmentProperty,
            HorizontalAlignment.Center));
        buttonContentAlignment.Setters.Add(new Setter(
            ContentControl.VerticalContentAlignmentProperty,
            VerticalAlignment.Center));
        application.Styles.Add(buttonContentAlignment);

        // WPF's ComboBox selection presenter is vertically centred while its
        // popup items stretch across the available row width.
        var comboBoxContentAlignment =
            new Style(selector => selector.OfType<ComboBox>());
        comboBoxContentAlignment.Setters.Add(new Setter(
            ContentControl.VerticalContentAlignmentProperty,
            VerticalAlignment.Center));
        application.Styles.Add(comboBoxContentAlignment);

        var comboBoxItemContentAlignment =
            new Style(selector => selector.OfType<ComboBoxItem>());
        comboBoxItemContentAlignment.Setters.Add(new Setter(
            ContentControl.HorizontalContentAlignmentProperty,
            HorizontalAlignment.Stretch));
        comboBoxItemContentAlignment.Setters.Add(new Setter(
            ContentControl.VerticalContentAlignmentProperty,
            VerticalAlignment.Center));
        application.Styles.Add(comboBoxItemContentAlignment);

        var buttonPressed = new Style(
            selector => selector.OfType<Button>().Class(":pressed"));
        buttonPressed.Setters.Add(new Setter(
            Visual.RenderTransformProperty,
            TransformOperations.Identity));
        application.Styles.Add(buttonPressed);

        var buttonDisabled = new Style(
            selector => selector.OfType<Button>().Class(":disabled"));
        buttonDisabled.Setters.Add(new Setter(Visual.OpacityProperty, .38d));
        application.Styles.Add(buttonDisabled);

        var scrollViewer = new Style(selector => selector.OfType<ScrollViewer>());
        scrollViewer.Setters.Add(new Setter(
            ScrollViewer.AllowAutoHideProperty,
            false));
        application.Styles.Add(scrollViewer);

        var verticalRail = new Style(selector => selector
            .OfType<ScrollBar>()
            .Class(":vertical")
            .Template()
            .OfType<AvaloniaRectangle>()
            .Name("TrackRect"));
        verticalRail.Setters.Add(new Setter(AvaloniaRectangle.WidthProperty, 3d));
        verticalRail.Setters.Add(new Setter(
            AvaloniaRectangle.HorizontalAlignmentProperty,
            global::Avalonia.Layout.HorizontalAlignment.Center));
        verticalRail.Setters.Add(new Setter(Visual.OpacityProperty, 1d));
        application.Styles.Add(verticalRail);

        var horizontalRail = new Style(selector => selector
            .OfType<ScrollBar>()
            .Class(":horizontal")
            .Template()
            .OfType<AvaloniaRectangle>()
            .Name("TrackRect"));
        horizontalRail.Setters.Add(new Setter(AvaloniaRectangle.HeightProperty, 3d));
        horizontalRail.Setters.Add(new Setter(
            AvaloniaRectangle.VerticalAlignmentProperty,
            global::Avalonia.Layout.VerticalAlignment.Center));
        horizontalRail.Setters.Add(new Setter(Visual.OpacityProperty, 1d));
        application.Styles.Add(horizontalRail);

        var verticalThumb = new Style(selector => selector
            .OfType<ScrollBar>()
            .Class(":vertical")
            .Template()
            .OfType<Thumb>());
        verticalThumb.Setters.Add(new Setter(Thumb.WidthProperty, 5d));
        verticalThumb.Setters.Add(new Setter(Thumb.MinHeightProperty, 34d));
        verticalThumb.Setters.Add(new Setter(
            Thumb.CornerRadiusProperty,
            new CornerRadius(2.5)));
        verticalThumb.Setters.Add(new Setter(
            Visual.RenderTransformProperty,
            TransformOperations.Identity));
        application.Styles.Add(verticalThumb);

        var horizontalThumb = new Style(selector => selector
            .OfType<ScrollBar>()
            .Class(":horizontal")
            .Template()
            .OfType<Thumb>());
        horizontalThumb.Setters.Add(new Setter(Thumb.HeightProperty, 5d));
        horizontalThumb.Setters.Add(new Setter(Thumb.MinWidthProperty, 34d));
        horizontalThumb.Setters.Add(new Setter(
            Thumb.CornerRadiusProperty,
            new CornerRadius(2.5)));
        horizontalThumb.Setters.Add(new Setter(
            Visual.RenderTransformProperty,
            TransformOperations.Identity));
        application.Styles.Add(horizontalThumb);

        var slider = new Style(selector => selector.OfType<Slider>());
        slider.Setters.Add(new Setter(Layoutable.HeightProperty, 28d));
        slider.Setters.Add(new Setter(
            TemplatedControl.PaddingProperty,
            new Thickness(2, 0)));
        application.Styles.Add(slider);

        var sliderThumbBody = new Style(selector => selector
            .OfType<Slider>()
            .Template()
            .OfType<Thumb>()
            .Template()
            .OfType<Border>());
        sliderThumbBody.Setters.Add(new Setter(Layoutable.WidthProperty, 11d));
        sliderThumbBody.Setters.Add(new Setter(Layoutable.HeightProperty, 16d));
        sliderThumbBody.Setters.Add(new Setter(
            Border.BackgroundProperty,
            Brush("#B7BFC2")));
        sliderThumbBody.Setters.Add(new Setter(
            Border.BorderBrushProperty,
            Brush("#687176")));
        sliderThumbBody.Setters.Add(new Setter(
            Border.BorderThicknessProperty,
            new Thickness(1)));
        sliderThumbBody.Setters.Add(new Setter(
            Border.CornerRadiusProperty,
            new CornerRadius(2)));
        sliderThumbBody.Setters.Add(new Setter(
            Layoutable.HorizontalAlignmentProperty,
            HorizontalAlignment.Center));
        sliderThumbBody.Setters.Add(new Setter(
            Layoutable.VerticalAlignmentProperty,
            VerticalAlignment.Center));
        application.Styles.Add(sliderThumbBody);

        var sliderThumbBodyHover = new Style(selector => selector
            .OfType<Slider>()
            .Class(":pointerover")
            .Template()
            .OfType<Thumb>()
            .Template()
            .OfType<Border>());
        sliderThumbBodyHover.Setters.Add(new Setter(
            Border.BackgroundProperty,
            Brush("#D0D7D9")));
        sliderThumbBodyHover.Setters.Add(new Setter(
            Border.BorderBrushProperty,
            Brush("#7E999C")));
        application.Styles.Add(sliderThumbBodyHover);

        var sliderThumbBodyPressed = new Style(selector => selector
            .OfType<Slider>()
            .Class(":pressed")
            .Template()
            .OfType<Thumb>()
            .Template()
            .OfType<Border>());
        sliderThumbBodyPressed.Setters.Add(new Setter(
            Border.BackgroundProperty,
            Brush("#E0E7E8")));
        sliderThumbBodyPressed.Setters.Add(new Setter(
            Border.BorderBrushProperty,
            Brush("#8FB0B3")));
        application.Styles.Add(sliderThumbBodyPressed);

        var checkBoxTheme = CreateCheckBoxTheme();
        var checkBox = new Style(selector => selector.OfType<CheckBox>());
        checkBox.Setters.Add(new Setter(StyledElement.ThemeProperty, checkBoxTheme));
        application.Styles.Add(checkBox);

        var checkBoxHover = new Style(selector => selector
            .OfType<CheckBox>()
            .Class(":pointerover"));
        checkBoxHover.Setters.Add(new Setter(
            TemplatedControl.BorderBrushProperty,
            Brush("#718D90")));
        application.Styles.Add(checkBoxHover);

        var checkBoxPath = new Style(selector => selector
            .OfType<CheckBox>()
            .Template()
            .OfType<AvaloniaPath>()
            .Name("CheckGlyph"));
        checkBoxPath.Setters.Add(new Setter(Visual.OpacityProperty, 0d));
        application.Styles.Add(checkBoxPath);

        var checkBoxChecked = new Style(selector => selector
            .OfType<CheckBox>()
            .Class(":checked"));
        checkBoxChecked.Setters.Add(new Setter(
            TemplatedControl.BackgroundProperty,
            Brush("#52777B")));
        checkBoxChecked.Setters.Add(new Setter(
            TemplatedControl.BorderBrushProperty,
            Brush("#70979A")));
        application.Styles.Add(checkBoxChecked);

        var checkBoxCheckedPath = new Style(selector => selector
            .OfType<CheckBox>()
            .Class(":checked")
            .Template()
            .OfType<AvaloniaPath>()
            .Name("CheckGlyph"));
        checkBoxCheckedPath.Setters.Add(new Setter(Visual.OpacityProperty, 1d));
        application.Styles.Add(checkBoxCheckedPath);

        var checkBoxDisabled = new Style(selector => selector
            .OfType<CheckBox>()
            .Class(":disabled"));
        checkBoxDisabled.Setters.Add(new Setter(Visual.OpacityProperty, .4d));
        application.Styles.Add(checkBoxDisabled);

        var contextMenu = new Style(selector => selector.OfType<ContextMenu>());
        contextMenu.Setters.Add(new Setter(
            TemplatedControl.CornerRadiusProperty,
            new CornerRadius(4)));
        contextMenu.Setters.Add(new Setter(
            TemplatedControl.FontFamilyProperty,
            textFontFamily));
        contextMenu.Setters.Add(new Setter(
            TemplatedControl.FontSizeProperty,
            BaseFontSize));
        application.Styles.Add(contextMenu);

        var menuItem = new Style(selector => selector.OfType<MenuItem>());
        menuItem.Setters.Add(new Setter(
            TemplatedControl.CornerRadiusProperty,
            new CornerRadius(3)));
        menuItem.Setters.Add(new Setter(
            ContentControl.VerticalContentAlignmentProperty,
            VerticalAlignment.Center));
        application.Styles.Add(menuItem);

        var dangerMenuItem = new Style(selector => selector
            .OfType<MenuItem>()
            .Class("danger"));
        dangerMenuItem.Setters.Add(new Setter(
            TemplatedControl.ForegroundProperty,
            Brush("#FFD4D4")));
        application.Styles.Add(dangerMenuItem);

        var dangerMenuItemSelectedRoot = new Style(selector => selector
            .OfType<MenuItem>()
            .Class("danger")
            .Class(":selected")
            .Template()
            .OfType<Border>()
            .Name("PART_LayoutRoot"));
        dangerMenuItemSelectedRoot.Setters.Add(new Setter(
            Border.BackgroundProperty,
            Brush("#4A292B")));
        application.Styles.Add(dangerMenuItemSelectedRoot);

        var dangerMenuItemSelectedHeader = new Style(selector => selector
            .OfType<MenuItem>()
            .Class("danger")
            .Class(":selected")
            .Template()
            .OfType<ContentPresenter>()
            .Name("PART_HeaderPresenter"));
        dangerMenuItemSelectedHeader.Setters.Add(new Setter(
            TemplatedControl.ForegroundProperty,
            Brush("#FFEEEE")));
        application.Styles.Add(dangerMenuItemSelectedHeader);

        var dangerMenuItemPointerOverRoot = new Style(selector => selector
            .OfType<MenuItem>()
            .Class("danger")
            .Class(":pointerover")
            .Template()
            .OfType<Border>()
            .Name("PART_LayoutRoot"));
        dangerMenuItemPointerOverRoot.Setters.Add(new Setter(
            Border.BackgroundProperty,
            Brush("#4A292B")));
        application.Styles.Add(dangerMenuItemPointerOverRoot);

        var dangerMenuItemPointerOverHeader = new Style(selector => selector
            .OfType<MenuItem>()
            .Class("danger")
            .Class(":pointerover")
            .Template()
            .OfType<ContentPresenter>()
            .Name("PART_HeaderPresenter"));
        dangerMenuItemPointerOverHeader.Setters.Add(new Setter(
            TemplatedControl.ForegroundProperty,
            Brush("#FFEEEE")));
        application.Styles.Add(dangerMenuItemPointerOverHeader);

        var tabItemTheme = CreateTabItemTheme();
        var tabItem = new Style(selector => selector.OfType<TabItem>());
        tabItem.Setters.Add(new Setter(StyledElement.ThemeProperty, tabItemTheme));
        application.Styles.Add(tabItem);
    }

    private static ControlTheme CreateTabItemTheme()
    {
        var theme = new ControlTheme(typeof(TabItem));
        theme.Setters.Add(new Setter(
            TemplatedControl.BackgroundProperty,
            Brushes.Transparent));
        theme.Setters.Add(new Setter(
            TemplatedControl.ForegroundProperty,
            Brush("#929A9F")));
        theme.Setters.Add(new Setter(
            TemplatedControl.BorderBrushProperty,
            Brush("#7FA4A7")));
        theme.Setters.Add(new Setter(
            TemplatedControl.BorderThicknessProperty,
            new Thickness(0)));
        theme.Setters.Add(new Setter(
            ContentControl.PaddingProperty,
            new Thickness(7, 10)));
        theme.Setters.Add(new Setter(
            TemplatedControl.TemplateProperty,
            new FuncControlTemplate<TabItem>((_, _) =>
            {
                var presenter = new ContentPresenter
                {
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                presenter.Bind(
                    ContentPresenter.ContentProperty,
                    new TemplateBinding(HeaderedContentControl.HeaderProperty));
                presenter.Bind(
                    ContentPresenter.ContentTemplateProperty,
                    new TemplateBinding(
                        HeaderedContentControl.HeaderTemplateProperty));
                presenter.Bind(
                    ContentPresenter.PaddingProperty,
                    new TemplateBinding(ContentControl.PaddingProperty));

                var border = new Border { Child = presenter };
                border.Bind(
                    Border.BackgroundProperty,
                    new TemplateBinding(TemplatedControl.BackgroundProperty));
                border.Bind(
                    Border.BorderBrushProperty,
                    new TemplateBinding(TemplatedControl.BorderBrushProperty));
                border.Bind(
                    Border.BorderThicknessProperty,
                    new TemplateBinding(TemplatedControl.BorderThicknessProperty));
                return border;
            })));

        var hover = new Style(selector => selector
            .Nesting()
            .Class(":pointerover"));
        hover.Setters.Add(new Setter(
            TemplatedControl.BackgroundProperty,
            Brush("#202427")));
        hover.Setters.Add(new Setter(
            TemplatedControl.ForegroundProperty,
            Brush("#D6DADC")));
        theme.Children.Add(hover);

        var selected = new Style(selector => selector
            .Nesting()
            .Class(":selected"));
        selected.Setters.Add(new Setter(
            TemplatedControl.BackgroundProperty,
            Brush("#22272A")));
        selected.Setters.Add(new Setter(
            TemplatedControl.ForegroundProperty,
            Brushes.White));
        selected.Setters.Add(new Setter(
            TemplatedControl.BorderThicknessProperty,
            new Thickness(0, 0, 0, 2)));
        theme.Children.Add(selected);
        return theme;
    }

    private static ControlTheme CreateCheckBoxTheme()
    {
        var theme = new ControlTheme(typeof(CheckBox));
        theme.Setters.Add(new Setter(
            TemplatedControl.ForegroundProperty,
            Brush("#C8D0D2")));
        theme.Setters.Add(new Setter(
            TemplatedControl.BackgroundProperty,
            Brush("#15191B")));
        theme.Setters.Add(new Setter(
            TemplatedControl.BorderBrushProperty,
            Brush("#465156")));
        theme.Setters.Add(new Setter(
            TemplatedControl.BorderThicknessProperty,
            new Thickness(1)));
        theme.Setters.Add(new Setter(
            Layoutable.HorizontalAlignmentProperty,
            HorizontalAlignment.Left));
        theme.Setters.Add(new Setter(
            ContentControl.VerticalContentAlignmentProperty,
            VerticalAlignment.Center));
        theme.Setters.Add(new Setter(
            TemplatedControl.TemplateProperty,
            new FuncControlTemplate<CheckBox>((_, _) =>
            {
                var box = new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(3),
                    VerticalAlignment = VerticalAlignment.Center
                };
                box.Bind(
                    Border.BackgroundProperty,
                    new TemplateBinding(TemplatedControl.BackgroundProperty));
                box.Bind(
                    Border.BorderBrushProperty,
                    new TemplateBinding(TemplatedControl.BorderBrushProperty));
                box.Bind(
                    Border.BorderThicknessProperty,
                    new TemplateBinding(TemplatedControl.BorderThicknessProperty));

                var check = new AvaloniaPath
                {
                    Name = "CheckGlyph",
                    Width = 9,
                    Height = 7,
                    Data = Geometry.Parse("M1,3.5 L3.5,6 L8,1"),
                    Fill = Brushes.Transparent,
                    Stroke = Brush("#F1F5F5"),
                    StrokeThickness = 1.7,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                    Stretch = Stretch.None,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var boxHost = new Grid
                {
                    Width = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { box, check }
                };
                var presenter = new ContentPresenter
                {
                    Margin = new Thickness(7, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    RecognizesAccessKey = true
                };
                presenter.Bind(
                    ContentPresenter.ContentProperty,
                    new TemplateBinding(ContentControl.ContentProperty));
                presenter.Bind(
                    ContentPresenter.ContentTemplateProperty,
                    new TemplateBinding(ContentControl.ContentTemplateProperty));
                Grid.SetColumn(presenter, 1);
                return new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("18,*"),
                    Children = { boxHost, presenter }
                };
            })));
        return theme;
    }

    private static SolidColorBrush Brush(string color) =>
        new(Color.Parse(color));
}
