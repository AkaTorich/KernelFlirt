using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace KernelFlirt.UI.Models;

/// <summary>
/// ObservableCollection that supports batch updates with a single UI notification.
/// Use ReplaceAll() instead of Clear() + Add() loops to avoid per-item re-renders.
/// </summary>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppress;

    public void ReplaceAll(IEnumerable<T> items)
    {
        _suppress = true;
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        _suppress = false;
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
    }

    public void AddRange(IEnumerable<T> items)
    {
        _suppress = true;
        foreach (var item in items)
            Items.Add(item);
        _suppress = false;
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppress)
            base.OnCollectionChanged(e);
    }
}
