using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace iSay.Desktop;

// 后台词库刷新只发送一次重置通知，避免数千个逐项通知占用录音界面线程。
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> values)
    {
        var snapshot = values.ToArray();
        if (this.SequenceEqual(snapshot)) return;
        CheckReentrancy();
        Items.Clear();
        foreach (var item in snapshot) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
