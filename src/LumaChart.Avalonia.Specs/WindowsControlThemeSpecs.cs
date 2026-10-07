using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsControlThemeSpecs
{
    public static IReadOnlyList<WindowsControlThemeSpecResult> Run()
    {
        var results = new List<WindowsControlThemeSpecResult>();
        Check(results, "control-theme-wpf-button-states-and-metrics-are-exact",
            VerifyButtons);
        Check(results, "control-theme-wpf-text-input-focus-and-density-are-exact",
            VerifyTextInputs);
        Check(results, "control-theme-wpf-combo-popup-and-items-are-exact",
            VerifyComboBoxes);
        Check(results, "control-theme-wpf-scrollbar-is-compact-and-never-auto-hidden",
            VerifyScrollBars);
        Check(results, "control-theme-wpf-slider-rail-thumb-and-hit-target-are-exact",
            VerifySliders);
        Check(results, "control-theme-wpf-flat-checkbox-states-and-density-are-exact",
            VerifyCheckBoxes);
        Check(results, "control-theme-wpf-tooltip-and-context-menu-surfaces-are-exact",
            VerifyPopupSurfaces);
        Check(results, "control-theme-wpf-tabitem-states-and-metrics-are-global",
            VerifyTabItems);
        Check(results, "control-theme-wpf-danger-menu-hover-is-exact",
            VerifyDangerMenuItem);
        Check(results, "control-theme-wpf-content-alignment-is-explicit",
            VerifyContentAlignment);
        Check(results, "control-theme-wpf-typography-is-global-and-stable",
            VerifyTypography);
        Check(results, "control-theme-install-is-idempotent-and-complete",
            VerifyInstall);
        Check(results, "control-theme-retains-avalonia-input-templates",
            VerifyTemplateBoundary);
        return results;
    }

    private static string VerifyButtons()
    {
        var resources = WindowsWpf110ControlTheme.CreateResourceOverrides();
        RequireBrush(resources, "ButtonBackground", "#1A1D20");
        RequireBrush(resources, "ButtonBorderBrush", "#34393D");
        RequireBrush(resources, "ButtonBackgroundPointerOver", "#22262A");
        RequireBrush(resources, "ButtonBorderBrushPointerOver", "#4A5055");
        RequireBrush(resources, "ButtonBackgroundPressed", "#282D30");
        RequireBrush(resources, "AccentButtonBackground", "#38676C");
        RequireBrush(resources, "AccentButtonBackgroundPointerOver", "#416F74");
        RequireBrush(resources, "AccentButtonBorderBrushPointerOver", "#628C90");
        Require(resources["ButtonPadding"] is Thickness padding &&
                padding == new Thickness(9, 6),
            "Button padding differs from WPF ToolButton 9,6.");
        Require(resources["ControlCornerRadius"] is CornerRadius radius &&
                radius == new CornerRadius(3),
            "Control corner radius differs from WPF ToolButton radius 3.");
        return "tool=#1A1D20/#34393D; hover=#22262A/#4A5055; " +
               "primary=#38676C; padding=9,6; radius=3";
    }

    private static string VerifyTextInputs()
    {
        var resources = WindowsWpf110ControlTheme.CreateResourceOverrides();
        RequireBrush(resources, "TextControlBackground", "#15181A");
        RequireBrush(resources, "TextControlForeground", "#E3E6E8");
        RequireBrush(resources, "TextControlBorderBrush", "#383E43");
        RequireBrush(resources, "TextControlBorderBrushFocused", "#718F92");
        RequireBrush(resources, "TextControlSelectionHighlightColor", "#526E71");
        Require(resources["TextControlBorderThemeThickness"] is Thickness border &&
                border == new Thickness(1),
            "Text input border is not 1 DIP.");
        Require(resources["TextControlBorderThemeThicknessFocused"] is Thickness focused &&
                focused == new Thickness(1),
            "Fluent 2-DIP focus border was not removed.");
        Require(resources["TextControlThemePadding"] is Thickness padding &&
                padding == new Thickness(8, 6),
            "Text input padding differs from WPF 8,6.");
        Require(resources["TextControlThemeMinHeight"] is double height && height == 30,
            "Text input minimum height differs from the WPF density contract.");
        return "background=#15181A; border=#383E43; focus=#718F92@1dip; " +
               "selection=#526E71; minHeight=30; padding=8,6";
    }

    private static string VerifyComboBoxes()
    {
        var resources = WindowsWpf110ControlTheme.CreateResourceOverrides();
        RequireBrush(resources, "ComboBoxBackground", "#15181A");
        RequireBrush(resources, "ComboBoxBorderBrush", "#383E43");
        RequireBrush(resources, "ComboBoxBorderBrushPointerOver", "#596166");
        RequireBrush(resources, "ComboBoxDropDownGlyphForeground", "#9DA5AA");
        RequireBrush(resources, "ComboBoxDropDownBackground", "#1B1E21");
        RequireBrush(resources, "ComboBoxDropDownBorderBrush", "#3B4146");
        RequireBrush(resources, "ComboBoxItemBackground", "#1B1E21");
        RequireBrush(resources, "ComboBoxItemBackgroundPointerOver", "#262B2E");
        RequireBrush(resources, "ComboBoxItemBackgroundSelected", "#314044");
        Require(resources["ComboBoxPadding"] is Thickness padding &&
                padding == new Thickness(8, 6),
            "ComboBox padding differs from WPF 8,6.");
        Require(resources["ComboBoxItemThemePadding"] is Thickness itemPadding &&
                itemPadding == new Thickness(9, 6),
            "ComboBoxItem padding differs from WPF 9,6.");
        Require(resources["ComboBoxDropdownBorderPadding"] is Thickness popupPadding &&
                popupPadding == new Thickness(3),
            "ComboBox popup padding differs from WPF 3.");
        return "field=#15181A/#383E43; glyph=#9DA5AA; popup=#1B1E21; " +
               "hover=#262B2E; selected=#314044";
    }

    private static string VerifyScrollBars()
    {
        var resources = WindowsWpf110ControlTheme.CreateResourceOverrides();
        Require(resources["ScrollBarSize"] is double size && size == 9,
            "ScrollBar size differs from WPF 9 DIP.");
        RequireBrush(resources, "ScrollBarTrackFill", "#272E32");
        RequireBrush(resources, "ScrollBarTrackFillPointerOver", "#30393D");
        RequireColor(resources, "ScrollBarThumbBackgroundColor", "#566267");
        RequireBrush(resources, "ScrollBarThumbFillPointerOver", "#718185");
        RequireBrush(resources, "ScrollBarThumbFillPressed", "#8AA9AC");
        Require(resources["ScrollBarButtonArrowIconFontSize"] is double arrowSize &&
                arrowSize == 0,
            "Fluent line-button arrows remain visible in the WPF scrollbar.");
        return "size=9; rail=3/#272E32/#30393D; " +
               "thumb=5/#566267/#718185/#8AA9AC; arrows=0; AllowAutoHide=false";
    }

    private static string VerifySliders()
    {
        var resources = WindowsWpf110ControlTheme.CreateResourceOverrides();
        Require(resources["SliderHorizontalHeight"] is double height && height == 28,
            "Slider height differs from WPF 28 DIP.");
        Require(resources["SliderTrackThemeHeight"] is double rail && rail == 3,
            "Slider rail differs from WPF 3 DIP.");
        RequireBrush(resources, "SliderTrackFill", "#30363A");
        RequireBrush(resources, "SliderTrackValueFill", "#718F92");
        RequireBrush(resources, "SliderThumbBackground", "#B7BFC2");
        RequireBrush(resources, "SliderThumbBackgroundPointerOver", "#D0D7D9");
        RequireBrush(resources, "SliderThumbBackgroundPressed", "#E0E7E8");
        RequireBrush(resources, "LumaChartSliderThumbBorder", "#687176");
        RequireBrush(resources, "LumaChartSliderThumbBorderPointerOver", "#7E999C");
        RequireBrush(resources, "LumaChartSliderThumbBorderPressed", "#8FB0B3");
        Require(resources["SliderHorizontalThumbWidth"] is double targetWidth &&
                targetWidth == 26 &&
                resources["SliderHorizontalThumbHeight"] is double targetHeight &&
                targetHeight == 28,
            "Slider thumb pointer target differs from WPF 26x28 DIP.");
        Require(resources["LumaChartSliderThumbBodyWidth"] is double bodyWidth &&
                bodyWidth == 11 &&
                resources["LumaChartSliderThumbBodyHeight"] is double bodyHeight &&
                bodyHeight == 16,
            "Slider visible thumb body differs from WPF 11x16 DIP.");
        return "height=28; rail=3/#30363A/#718F92; " +
               "target=26x28; body=11x16; normal/hover/pressed exact";
    }

    private static string VerifyCheckBoxes()
    {
        var resources = WindowsWpf110ControlTheme.CreateResourceOverrides();
        RequireBrush(resources, "CheckBoxForegroundUnchecked", "#C8D0D2");
        RequireBrush(resources, "CheckBoxCheckBackgroundFillUnchecked", "#15191B");
        RequireBrush(resources, "CheckBoxCheckBackgroundStrokeUnchecked", "#465156");
        RequireBrush(
            resources,
            "CheckBoxCheckBackgroundStrokeUncheckedPointerOver",
            "#718D90");
        RequireBrush(resources, "CheckBoxCheckBackgroundFillChecked", "#52777B");
        RequireBrush(
            resources,
            "CheckBoxCheckBackgroundStrokeCheckedPointerOver",
            "#70979A");
        RequireBrush(resources, "CheckBoxCheckGlyphForegroundChecked", "#F1F5F5");
        Require(resources["LumaChartCheckBoxSize"] is double size && size == 16,
            "CheckBox box differs from WPF 16 DIP.");
        Require(resources["LumaChartCheckBoxContentGap"] is double gap && gap == 7,
            "CheckBox content gap differs from WPF 7 DIP.");
        return "box=16/#15191B/#465156; hover=#718D90; " +
               "checked=#52777B/#70979A; stroked glyph=#F1F5F5; gap=7";
    }

    private static string VerifyPopupSurfaces()
    {
        var resources = WindowsWpf110ControlTheme.CreateResourceOverrides();
        RequireBrush(resources, "ToolTipBackground", "#22262A");
        RequireBrush(resources, "ToolTipForeground", "#E3E6E8");
        RequireBrush(resources, "ToolTipBorderBrush", "#3A4045");
        Require(resources["ToolTipBorderThemePadding"] is Thickness tipPadding &&
                tipPadding == new Thickness(8, 5),
            "ToolTip padding differs from WPF 8,5.");
        RequireBrush(resources, "MenuFlyoutPresenterBackground", "#1A1E21");
        RequireBrush(resources, "MenuFlyoutPresenterBorderBrush", "#3A4246");
        Require(resources["MenuFlyoutPresenterThemePadding"] is Thickness menuPadding &&
                menuPadding == new Thickness(4),
            "ContextMenu padding differs from WPF 4.");
        Require(resources["MenuFlyoutItemThemePaddingNarrow"] is Thickness itemPadding &&
                itemPadding == new Thickness(13, 7),
            "MenuItem density differs from WPF context menus.");
        return "tooltip=#22262A/#3A4045@8,5; " +
               "menu=#1A1E21/#3A4246@4; item=13,7; danger hover=#4A292B";
    }

    private static string VerifyTabItems()
    {
        var resources = WindowsWpf110ControlTheme.CreateResourceOverrides();
        RequireBrush(resources, "LumaChartTabItemForeground", "#929A9F");
        RequireBrush(
            resources,
            "LumaChartTabItemForegroundPointerOver",
            "#D6DADC");
        RequireBrush(
            resources,
            "LumaChartTabItemForegroundSelected",
            "#FFFFFF");
        RequireBrush(
            resources,
            "LumaChartTabItemBackgroundPointerOver",
            "#202427");
        RequireBrush(
            resources,
            "LumaChartTabItemBackgroundSelected",
            "#22272A");
        RequireBrush(resources, "LumaChartTabItemBorderBrush", "#7FA4A7");
        Require(resources["LumaChartTabItemPadding"] is Thickness padding &&
                padding == new Thickness(7, 10),
            "TabItem padding differs from WPF RightTab 7,10.");
        Require(
            resources["LumaChartTabItemBorderThicknessSelected"] is
                Thickness selectedBorder &&
            selectedBorder == new Thickness(0, 0, 0, 2),
            "Selected TabItem marker differs from WPF 2-DIP bottom border.");
        return "global=true; normal=#929A9F; hover=#202427/#D6DADC; " +
               "selected=#22272A/#FFFFFF/#7FA4A7@2dip; padding=7,10";
    }

    private static string VerifyDangerMenuItem()
    {
        var resources = WindowsWpf110ControlTheme.CreateResourceOverrides();
        RequireBrush(
            resources,
            "LumaChartDangerMenuItemForeground",
            "#FFD4D4");
        RequireBrush(
            resources,
            "LumaChartDangerMenuItemForegroundPointerOver",
            "#FFEEEE");
        RequireBrush(
            resources,
            "LumaChartDangerMenuItemBackgroundPointerOver",
            "#4A292B");
        Require(
            resources["LumaChartDangerMenuItemPadding"] is Thickness padding &&
            padding == new Thickness(13, 8),
            "Danger MenuItem padding differs from WPF 13,8.");
        Require(
            resources["LumaChartDangerMenuItemMinWidth"] is double width &&
            width == 190,
            "Danger MenuItem minimum width differs from WPF 190 DIP.");
        return "normal=transparent/#FFD4D4; hover=#4A292B/#FFEEEE; " +
               "padding=13,8; minWidth=190";
    }

    private static string VerifyContentAlignment()
    {
        var application = new global::Avalonia.Application();
        WindowsWpf110ControlTheme.Apply(application);
        var styleSetters = application.Styles
            .OfType<Style>()
            .Select(style => style.Setters.OfType<Setter>().ToArray())
            .ToArray();

        static bool HasSetter(
            IEnumerable<Setter> setters,
            AvaloniaProperty property,
            object expected) =>
            setters.Any(setter =>
                setter.Property == property &&
                Equals(setter.Value, expected));

        var centredBothAxes = styleSetters.Count(setters =>
            HasSetter(
                setters,
                ContentControl.HorizontalContentAlignmentProperty,
                HorizontalAlignment.Center) &&
            HasSetter(
                setters,
                ContentControl.VerticalContentAlignmentProperty,
                VerticalAlignment.Center));
        var centredVertically = styleSetters.Count(setters =>
            HasSetter(
                setters,
                ContentControl.VerticalContentAlignmentProperty,
                VerticalAlignment.Center));
        var stretchedAndCentred = styleSetters.Count(setters =>
            HasSetter(
                setters,
                ContentControl.HorizontalContentAlignmentProperty,
                HorizontalAlignment.Stretch) &&
            HasSetter(
                setters,
                ContentControl.VerticalContentAlignmentProperty,
                VerticalAlignment.Center));

        Require(centredBothAxes >= 1,
            "No WPF-equivalent two-axis centred Button style was installed.");
        Require(centredVertically >= 4,
            "Button, ComboBox, ComboBoxItem and MenuItem vertical alignment " +
            "styles were not all installed.");
        Require(stretchedAndCentred >= 1,
            "No WPF-equivalent stretched and vertically centred item style was installed.");
        return "button=center/center; combo=center-y; " +
               "combo-item=stretch/center; menu-item=center-y";
    }

    private static string VerifyTypography()
    {
        var application = new global::Avalonia.Application();
        WindowsWpf110ControlTheme.Apply(application);
        var styleSetters = application.Styles
            .OfType<Style>()
            .Select(style => style.Setters.OfType<Setter>().ToArray())
            .ToArray();
        var expectedFamily = new FontFamily(
            WindowsWpf110ControlTheme.TextFontFamilyName);

        static bool HasSetter(
            IEnumerable<Setter> setters,
            AvaloniaProperty property,
            object expected) =>
            setters.Any(setter =>
                setter.Property == property &&
                Equals(setter.Value, expected));

        Require(styleSetters.Any(setters =>
                HasSetter(
                    setters,
                    TemplatedControl.FontFamilyProperty,
                    expectedFamily) &&
                HasSetter(
                    setters,
                    TemplatedControl.FontSizeProperty,
                    WindowsWpf110ControlTheme.BaseFontSize)),
            "Independent windows and controls do not share WPF's 12-DIP " +
            "Segoe UI / Microsoft YaHei metric baseline.");
        Require(styleSetters.Any(setters =>
                HasSetter(
                    setters,
                    TextBlock.FontFamilyProperty,
                    expectedFamily) &&
                HasSetter(
                    setters,
                    TextBlock.FontSizeProperty,
                    WindowsWpf110ControlTheme.BaseFontSize)),
            "TextBlock does not carry WPF App.xaml's explicit font family.");
        return $"controls/text={WindowsWpf110ControlTheme.TextFontFamilyName}; " +
               $"base={WindowsWpf110ControlTheme.BaseFontSize:0}dip; " +
               "independent-windows=true";
    }

    private static string VerifyInstall()
    {
        var application = new global::Avalonia.Application();
        var resources = WindowsWpf110ControlTheme.CreateResourceOverrides();
        WindowsWpf110ControlTheme.Apply(application);
        var stylesAfterFirstApply = application.Styles.Count;
        WindowsWpf110ControlTheme.Apply(application);
        Require(application.Styles.Count == stylesAfterFirstApply,
            "Applying the WPF control theme twice duplicated interaction styles.");
        Require(
            stylesAfterFirstApply ==
            WindowsWpf110ControlTheme.PlatformIndependentInteractionStyleCount,
            $"Expected {WindowsWpf110ControlTheme.PlatformIndependentInteractionStyleCount} " +
            $"headless styles, " +
            $"got {stylesAfterFirstApply}.");
        foreach (var resource in resources)
        {
            Require(application.Resources.TryGetResource(
                    resource.Key,
                    application.ActualThemeVariant,
                    out var installed) &&
                    ResourceValuesEqual(installed, resource.Value),
                $"Installed resource differs: {resource.Key}.");
        }
        return $"version={WindowsWpf110ControlTheme.ContractVersion}; " +
               $"resources={resources.Count}; styles={stylesAfterFirstApply}; idempotent=true";
    }

    private static string VerifyTemplateBoundary()
    {
        var resources = WindowsWpf110ControlTheme.CreateResourceOverrides();
        Require(resources.Keys.All(key =>
                !key.Contains("Template", StringComparison.Ordinal)),
            "The resource contract unexpectedly replaces an Avalonia input template.");
        Require(!resources.ContainsKey("TextBoxTemplate") &&
                !resources.ContainsKey("ComboBoxTemplate"),
            "TextBox or ComboBox template replacement would regress IME or accessibility.");
        return "TextBox/ComboBox templates retained; only WPF resource states overridden";
    }

    private static void RequireBrush(
        IReadOnlyDictionary<string, object> resources,
        string key,
        string expected)
    {
        Require(resources.TryGetValue(key, out var value) &&
                value is ISolidColorBrush brush &&
                brush.Color == Color.Parse(expected),
            $"{key} differs from {expected}.");
    }

    private static void RequireColor(
        IReadOnlyDictionary<string, object> resources,
        string key,
        string expected)
    {
        Require(resources.TryGetValue(key, out var value) &&
                value is Color color &&
                color == Color.Parse(expected),
            $"{key} differs from {expected}.");
    }

    private static bool ResourceValuesEqual(object? left, object? right) =>
        (left, right) switch
        {
            (ISolidColorBrush leftBrush, ISolidColorBrush rightBrush) =>
                leftBrush.Color == rightBrush.Color &&
                leftBrush.Opacity.Equals(rightBrush.Opacity),
            (Color leftColor, Color rightColor) => leftColor == rightColor,
            (Thickness leftThickness, Thickness rightThickness) =>
                leftThickness == rightThickness,
            (CornerRadius leftRadius, CornerRadius rightRadius) =>
                leftRadius == rightRadius,
            (GridLength leftLength, GridLength rightLength) =>
                leftLength == rightLength,
            (double leftDouble, double rightDouble) =>
                leftDouble.Equals(rightDouble),
            _ => Equals(left, right)
        };

    private static void Check(
        ICollection<WindowsControlThemeSpecResult> results,
        string name,
        Func<string> verify)
    {
        try
        {
            results.Add(new WindowsControlThemeSpecResult(name, true, verify()));
        }
        catch (Exception exception)
        {
            results.Add(new WindowsControlThemeSpecResult(name, false, exception.Message));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed record WindowsControlThemeSpecResult(
    string Name,
    bool Passed,
    string Detail);
