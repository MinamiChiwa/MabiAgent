using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MabiAgent.Desktop;

// Filtered DataGrid views must never observe a half-applied queue snapshot.
public sealed class QueueRows : ObservableCollection<JobRow>
{
    public void ApplySnapshot(IReadOnlyList<JobRow> rows)
    {
        CheckReentrancy();
        var existing=this.ToDictionary(row=>row.Id);
        var next=rows.Select(row=>
        {
            if(!existing.TryGetValue(row.Id,out var prior))return row;
            if(!ReferenceEquals(prior,row))prior.UpdateFrom(row);
            return prior;
        }).ToArray();
        if(this.SequenceEqual(next))return;
        Items.Clear();foreach(var row in next)Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
