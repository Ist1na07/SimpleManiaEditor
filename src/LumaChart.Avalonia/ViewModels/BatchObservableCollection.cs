using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace LumaChart.Avalonia.ViewModels;

/// <summary>
/// Replaces an indexed collection with one reset notification. Library scans
/// routinely return thousands of charts, so individual Add notifications would
/// monopolize the UI thread while the list re-evaluates every intermediate state.
/// </summary>
internal sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var replacement = items.ToArray();
        Items.Clear();
        foreach (var item in replacement) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(
            new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Reset));
    }
}
