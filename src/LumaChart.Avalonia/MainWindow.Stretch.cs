using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Layout;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private Button? stretchButton;
    private Control BuildStretchToolButton()
    {
        stretchButton = new Button { Content = "拉伸", Height = 34, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(stretchButton, "Editor.QuickStretch");
        ToolTip.SetTip(stretchButton, "先选中按键；拖动框内平移，拖动上下边拉伸。松手应用，可撤销；Esc 取消当前拖动 / 退出。");
        stretchButton.Click += (_, _) =>
        {
            if (SymmetryTargetSurface.BeginStretch()) UpdateTimelineToolButtons();
            SymmetryTargetSurface.Focus();
        };
        RefreshStretchButton(); return stretchButton;
    }
    private void RefreshStretchButton()
    {
        if (stretchButton != null) stretchButton.IsEnabled = SymmetryTargetSession is { IsReadOnly: false, CanEditNotes: true, Selection.Count: > 0 } && !playtestInProgress;
    }
}
