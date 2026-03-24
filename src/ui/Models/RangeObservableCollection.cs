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

    /// <summary>Add item without firing CollectionChanged (for incremental disasm loading).</summary>
    public void AddSilent(T item) { _suppress = true; Items.Add(item); _suppress = false; }

    /// <summary>Insert items at index without firing CollectionChanged.</summary>
    public void InsertRangeSilent(int index, IList<T> items)
    {
        _suppress = true;
        for (int i = 0; i < items.Count; i++)
            Items.Insert(index + i, items[i]);
        _suppress = false;
    }

    /// <summary>Remove a range without firing CollectionChanged.</summary>
    public void RemoveRangeSilent(int index, int count)
    {
        _suppress = true;
        for (int i = 0; i < count; i++)
            Items.RemoveAt(index);
        _suppress = false;
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppress)
            base.OnCollectionChanged(e);
    }
}
