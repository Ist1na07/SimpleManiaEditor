namespace LumaChart.Application.Libraries;

/// <summary>
/// Explorer/WPF-compatible context-menu selection rule: right-clicking an
/// already selected card preserves the batch; right-clicking another card
/// makes that card the sole target.
/// </summary>
public static class DesktopLibrarySelectionPolicy
{
    public static IReadOnlyList<T> ResolveRightClick<T>(
        IEnumerable<T> selectedItems,
        T clickedItem,
        IEqualityComparer<T>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(selectedItems);
        comparer ??= EqualityComparer<T>.Default;
        var selected = selectedItems.ToArray();
        return selected.Contains(clickedItem, comparer) ? selected : [clickedItem];
    }
}
