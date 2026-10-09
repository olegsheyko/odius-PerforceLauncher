using PerforceLauncher.App.Infrastructure;
using PerforceLauncher.Core.Models;

namespace PerforceLauncher.App.ViewModels;

/// <summary>Строка вкладки History. Маркер показывает коммиты новее синхронизированного (ещё не у вас).</summary>
public sealed class HistoryEntry : ObservableObject
{
    private bool _isNew;

    public HistoryEntry(ChangeInfo change, long have)
    {
        Change = change;
        _isNew = change.Id > have;
    }

    public ChangeInfo Change { get; }
    public long Id => Change.Id;
    public string User => Change.User;
    public System.DateTime Time => Change.Time;
    public string FirstLine => Change.FirstLine;
    public string Marker => _isNew ? "↓" : "";
    public string MarkerTip => _isNew ? "Newer than your synced changelist (not in your workspace yet)" : "";

    public void UpdateHave(long have)
    {
        if (Set(ref _isNew, Change.Id > have, nameof(Marker))) OnPropertyChanged(nameof(MarkerTip));
    }

    public bool Matches(string filter) =>
        filter.Length == 0
        || Id.ToString().Contains(filter, System.StringComparison.OrdinalIgnoreCase)
        || User.Contains(filter, System.StringComparison.OrdinalIgnoreCase)
        || Change.Description.Contains(filter, System.StringComparison.OrdinalIgnoreCase);
}
