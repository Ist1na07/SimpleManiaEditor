using LumaChart.Application.Editing;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Rendering;

public static class TimelineEditPreflight
{
    public static string? GetNoteEditDenial(EditorSession session, string operation)
    {
        ArgumentNullException.ThrowIfNull(session);
        operation = string.IsNullOrWhiteSpace(operation) ? "编辑" : operation.Trim();
        if (session.IsReadOnly) return AvaloniaLocalizationCatalog.Format("当前谱面为只读状态，不能{0} Note", operation);
        if (!session.CanEditNotes) return AvaloniaLocalizationCatalog.Format("当前权限不能{0} Note", operation);
        return null;
    }
}
