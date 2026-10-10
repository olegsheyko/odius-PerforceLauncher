using System.Linq;
using PerforceLauncher.App.Infrastructure;
using PerforceLauncher.Core.Models;

namespace PerforceLauncher.App.ViewModels;

/// <summary>
/// Заголовок changelist'а во вкладке Pending: один объект на changelist (по нему же группируются файлы),
/// поэтому его можно выделить кликом и повесить на него действия (Submit, Edit, Revert, Delete).
/// </summary>
public sealed class ChangelistHeaderViewModel : ObservableObject
{
    private bool _isSelected;

    public ChangelistHeaderViewModel(PendingChangelist changelist) => Changelist = changelist;

    public PendingChangelist Changelist { get; }
    public long Id => Changelist.Id;
    public bool IsDefault => Changelist.IsDefault;
    public string Title => IsDefault ? "Default changelist" : $"CL {Id}";

    public string Description
    {
        get
        {
            var d = Changelist.Description.Trim();
            var nl = d.IndexOfAny(new[] { '\r', '\n' });
            return nl < 0 ? d : d[..nl];
        }
    }

    public int FileCount => Changelist.Files.Count;
    public string CountText => FileCount == 1 ? "1 file" : $"{FileCount} files";
    public int IgnoredCount => Changelist.Files.Count(f => f.IsIgnored);
    public bool CanSubmit => FileCount > 0;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    // Для отображения имени группы, если шаблон не задан.
    public override string ToString() => Title;
}

/// <summary>Строка вкладки Pending changes: открытый файл либо заглушка «(empty)» пустого changelist'а.</summary>
public sealed class PendingFileViewModel
{
    public PendingFileViewModel(ChangelistHeaderViewModel header, OpenedFile? file, string displayPath)
    {
        Header = header;
        File = file;
        DisplayPath = displayPath;
    }

    /// <summary>Группа (changelist) этой строки; общий объект для всех файлов changelist'а.</summary>
    public ChangelistHeaderViewModel Header { get; }
    public long ChangelistId => Header.Id;
    public OpenedFile? File { get; }
    public bool IsPlaceholder => File == null;
    public string Action => File?.Action ?? "";
    public string Type => File?.Type ?? "";
    public string DisplayPath { get; }

    /// <summary>Файл под правилом P4IGNORE, но уже в депо: reconcile/edit его открывает, а коммитить обычно не нужно.</summary>
    public bool IsIgnored => File?.IsIgnored == true;
    public string Marker => IsIgnored ? "⚠" : "";
    public string MarkerTip => IsIgnored
        ? $"Matches .p4ignore ({File!.IgnoredBy}) but is already tracked in the depot.\nUsually a generated/local file: revert it or move it to a separate changelist instead of submitting."
        : "";
}
