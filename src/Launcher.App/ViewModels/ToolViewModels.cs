using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using PerforceLauncher.App.Infrastructure;
using PerforceLauncher.Core.Launching;
using PerforceLauncher.Core.Models;
using PerforceLauncher.Core.VersionControl;

namespace PerforceLauncher.App.ViewModels;

// ======================= История файла =======================

public sealed class FileRevisionViewModel
{
    public FileRevisionViewModel(FileRevision rev, bool hasPrevious, FileHistoryViewModel owner)
    {
        Revision = rev;
        DiffPreviousCommand = new AsyncRelayCommand(_ => owner.DiffAsync(this, withLocal: false), _ => hasPrevious && owner.CanDiff);
        DiffLocalCommand = new AsyncRelayCommand(_ => owner.DiffAsync(this, withLocal: true), _ => owner.HasLocal && owner.CanDiff && !rev.Action.Contains("delete", StringComparison.OrdinalIgnoreCase));
        CopyChangeCommand = new RelayCommand(_ => owner.Copy(rev.Change.ToString()));
        CopyDescriptionCommand = new RelayCommand(_ => owner.Copy(rev.Description.Trim()));
    }

    public FileRevision Revision { get; }
    public int Rev => Revision.Rev;
    public string Change => $"CL {Revision.Change}";
    public string Action => Revision.Action;
    public string User => Revision.User;
    public string Client => Revision.Client;
    public DateTime Time => Revision.Time;
    public string FirstLine => Revision.FirstLine;
    public string Description => Revision.Description.Trim();
    public string SizeText => Revision.Size <= 0 ? "" : Revision.Size < 1024 ? $"{Revision.Size} B" : Revision.Size < 1024 * 1024 ? $"{Revision.Size / 1024.0:0.#} KB" : $"{Revision.Size / 1024.0 / 1024.0:0.#} MB";

    public ICommand DiffPreviousCommand { get; }
    public ICommand DiffLocalCommand { get; }
    public ICommand CopyChangeCommand { get; }
    public ICommand CopyDescriptionCommand { get; }
}

/// <summary>Окно «File History»: кто, когда и зачем менял файл; сравнение ревизий.</summary>
public sealed class FileHistoryViewModel : ObservableObject
{
    private readonly IVersionControl _vcs;
    private readonly ProjectProfile _profile;
    private readonly DiffService _diff;
    private readonly IDialogService _dialogs;
    private readonly Action<string?> _copy;
    private FileRevisionViewModel? _selected;
    private bool _busy;
    private string _error = "";
    private string _type = "";

    public FileHistoryViewModel(IVersionControl vcs, ProjectProfile profile, string depotPath, string? localPath, string type,
        DiffService diff, IDialogService dialogs, Action<string?> copy)
    {
        _vcs = vcs; _profile = profile; _diff = diff; _dialogs = dialogs; _copy = copy;
        DepotPath = depotPath;
        LocalPath = localPath;
        _type = type;
        ReloadCommand = new AsyncRelayCommand(_ => LoadAsync(), _ => !IsBusy);
    }

    public string DepotPath { get; }
    public string? LocalPath { get; }
    public string FileName => DepotPath[(DepotPath.LastIndexOf('/') + 1)..];
    public ObservableCollection<FileRevisionViewModel> Items { get; } = new();
    public ICommand ReloadCommand { get; }
    public bool HasLocal => !string.IsNullOrEmpty(LocalPath) && System.IO.File.Exists(LocalPath);
    /// <summary>Сравнение возможно только для текстовых файлов.</summary>
    public bool CanDiff => _type.Length == 0 || DiffService.IsTextType(_type);
    public string BinaryHint => CanDiff ? "" : "Binary file: revisions can't be compared line by line.";
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public FileRevisionViewModel? SelectedItem { get => _selected; set => Set(ref _selected, value); }

    public void Copy(string? text) => _copy(text);

    public async Task LoadAsync()
    {
        IsBusy = true; Error = "";
        try
        {
            var revs = await _vcs.GetFileHistoryAsync(_profile, DepotPath, 200);
            if (_type.Length == 0 && revs.Count > 0) { _type = revs[0].Type; OnPropertyChanged(nameof(CanDiff)); OnPropertyChanged(nameof(BinaryHint)); }
            Items.Clear();
            foreach (var r in revs) Items.Add(new FileRevisionViewModel(r, r.Rev > 1, this));
            SelectedItem = Items.FirstOrDefault();
            if (revs.Count == 0) Error = "No history found for this file.";
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsBusy = false; }
    }

    public async Task DiffAsync(FileRevisionViewModel item, bool withLocal)
    {
        try
        {
            if (withLocal) await _diff.DiffWithLocalAsync(_profile, DepotPath, item.Rev, LocalPath!, _type);
            else await _diff.DiffRevisionsAsync(_profile, DepotPath, item.Rev - 1, item.Rev, _type);
        }
        catch (Exception ex) { _dialogs.Error(ex.Message); }
    }
}

// ======================= Shelves =======================

public sealed class ShelvedItemViewModel : ObservableObject
{
    public ShelvedItemViewModel(ShelvedChange change, bool isMine) { Change = change; IsMine = isMine; }
    public ShelvedChange Change { get; }
    public long Id => Change.Id;
    public string Title => $"CL {Change.Id}";
    public string User => Change.User;
    public bool IsMine { get; }
    public string Workspace => Change.Client;
    public DateTime Time => Change.Time;
    public string FirstLine => Change.FirstLine;
    public string Description => Change.Description.Trim();
}

/// <summary>Окно «Shelves»: отложенные изменения команды; забрать к себе (unshelve) или удалить свой shelve.</summary>
public sealed class ShelvesViewModel : ObservableObject
{
    private readonly IVersionControl _vcs;
    private readonly ProjectProfile _profile;
    private readonly string _me;
    private readonly IDialogService _dialogs;
    private readonly Action _onChanged;
    private ShelvedItemViewModel? _selected;
    private bool _busy;
    private string _error = "";
    private string _status = "";
    private string _filesText = "";

    public ShelvesViewModel(IVersionControl vcs, ProjectProfile profile, string me, IDialogService dialogs, Action onChanged)
    {
        _vcs = vcs; _profile = profile; _me = me; _dialogs = dialogs; _onChanged = onChanged;
        ReloadCommand = new AsyncRelayCommand(_ => LoadAsync(), _ => !IsBusy);
        UnshelveCommand = new AsyncRelayCommand(_ => UnshelveAsync(), _ => !IsBusy && SelectedItem != null);
        DeleteCommand = new AsyncRelayCommand(_ => DeleteAsync(), _ => !IsBusy && SelectedItem is { IsMine: true });
    }

    public ObservableCollection<ShelvedItemViewModel> Items { get; } = new();
    public ICommand ReloadCommand { get; }
    public ICommand UnshelveCommand { get; }
    public ICommand DeleteCommand { get; }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    /// <summary>Файлы выбранного shelve: «edit  //depot/…».</summary>
    public string FilesText { get => _filesText; private set => Set(ref _filesText, value); }
    public bool IsEmpty => Items.Count == 0 && !IsBusy && Error.Length == 0;

    public ShelvedItemViewModel? SelectedItem
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            CommandManager.InvalidateRequerySuggested();
            _ = LoadFilesAsync(value);
        }
    }

    public async Task LoadAsync()
    {
        IsBusy = true; Error = "";
        try
        {
            var list = await _vcs.GetShelvesAsync(_profile);
            Items.Clear();
            foreach (var s in list) Items.Add(new ShelvedItemViewModel(s, string.Equals(s.User, _me, StringComparison.OrdinalIgnoreCase)));
            Status = list.Count == 0 ? "" : $"{list.Count} shelved changelist{(list.Count == 1 ? "" : "s")}";
            SelectedItem = Items.FirstOrDefault();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsBusy = false; OnPropertyChanged(nameof(IsEmpty)); }
    }

    private async Task LoadFilesAsync(ShelvedItemViewModel? item)
    {
        FilesText = "";
        if (item == null) return;
        try
        {
            var files = await _vcs.GetShelvedFilesAsync(_profile, item.Id);
            if (_selected != item) return;   // за время запроса выбрали другой
            FilesText = files.Count == 0 ? "No files." : string.Join(Environment.NewLine, files.Select(f => $"{f.Action,-9} {f.DepotPath}"));
        }
        catch (Exception ex) { if (_selected == item) FilesText = ex.Message; }
    }

    private async Task UnshelveAsync()
    {
        var item = SelectedItem!;
        if (!_dialogs.Confirm($"Unshelve CL {item.Id} by {item.User}?\n\nThe files are opened in your default changelist with the shelved content.")) return;
        IsBusy = true; Error = "";
        try
        {
            try { await _vcs.UnshelveAsync(_profile, item.Id, 0, force: false, null); }
            catch (P4Exception ex) when (ex.Message.Contains("opened", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("clobber", StringComparison.OrdinalIgnoreCase))
            {
                // Файлы уже открыты или доступны на запись: перезапись только по явному согласию.
                if (!_dialogs.Confirm("Some of these files are already open (or writable) in your workspace.\n\nOverwrite them with the shelved versions? Local changes in those files are lost.")) { Status = "Unshelve cancelled."; return; }
                await _vcs.UnshelveAsync(_profile, item.Id, 0, force: true, null);
            }
            Status = $"CL {item.Id} unshelved into your default changelist.";
            _onChanged();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task DeleteAsync()
    {
        var item = SelectedItem!;
        if (!_dialogs.Confirm($"Delete the shelf of CL {item.Id}?\n\nThe shelved copy on the server is removed. Your local files are not touched.")) return;
        IsBusy = true; Error = "";
        try
        {
            await _vcs.DeleteShelfAsync(_profile, item.Id);
            Items.Remove(item);
            Status = $"Shelf of CL {item.Id} deleted.";
            SelectedItem = Items.FirstOrDefault();
            _onChanged();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsBusy = false; OnPropertyChanged(nameof(IsEmpty)); }
    }
}

// ======================= Labels =======================

public sealed class LabelItemViewModel
{
    public LabelItemViewModel(LabelInfo info) => Info = info;
    public LabelInfo Info { get; }
    public string Name => Info.Name;
    public string Owner => Info.Owner;
    public DateTime Updated => Info.Updated;
    public string Description => Info.Description.Split('\n')[0].Trim();
    public string FullDescription => Info.Description;
}

/// <summary>Окно «Labels»: метки (релизы, стабильные сборки) и sync воркспейса на выбранную.</summary>
public sealed class LabelsViewModel : ObservableObject
{
    private readonly IVersionControl _vcs;
    private readonly ProjectProfile _profile;
    private IReadOnlyList<LabelItemViewModel> _all = Array.Empty<LabelItemViewModel>();
    private string _filter = "";
    private bool _busy;
    private string _error = "";
    private LabelItemViewModel? _selected;

    public LabelsViewModel(IVersionControl vcs, ProjectProfile profile)
    {
        _vcs = vcs; _profile = profile;
        SyncCommand = new RelayCommand(_ => { SyncTarget = SelectedItem?.Name; CloseRequested?.Invoke(); }, _ => SelectedItem != null && !IsBusy);
        ReloadCommand = new AsyncRelayCommand(_ => LoadAsync(), _ => !IsBusy);
    }

    public ObservableCollection<LabelItemViewModel> Items { get; } = new();
    public ICommand SyncCommand { get; }
    public ICommand ReloadCommand { get; }
    public event Action? CloseRequested;
    /// <summary>Выбранная метка, на которую нужно синкнуть воркспейс (выставляется кнопкой Sync).</summary>
    public string? SyncTarget { get; private set; }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public LabelItemViewModel? SelectedItem { get => _selected; set { if (Set(ref _selected, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) ApplyFilter(); } }
    public bool IsEmpty => !IsBusy && Items.Count == 0 && Error.Length == 0;

    public async Task LoadAsync()
    {
        IsBusy = true; Error = "";
        try
        {
            _all = (await _vcs.GetLabelsAsync(_profile, 500)).Select(l => new LabelItemViewModel(l)).ToList();
            ApplyFilter();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsBusy = false; OnPropertyChanged(nameof(IsEmpty)); }
    }

    private void ApplyFilter()
    {
        var words = _filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Items.Clear();
        foreach (var l in _all.Where(l => words.All(w => l.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || l.Owner.Contains(w, StringComparison.OrdinalIgnoreCase) || l.FullDescription.Contains(w, StringComparison.OrdinalIgnoreCase))))
            Items.Add(l);
        SelectedItem = Items.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }
}

// ======================= Очистка диска =======================

public sealed class CleanupItemViewModel : ObservableObject
{
    private bool _checked;

    public CleanupItemViewModel(CleanupItem item) { Item = item; _checked = item.Recommended && item.Exists; }
    public CleanupItem Item { get; }
    public string Title => Item.Title;
    public string Description => Item.Description;
    public bool Exists => Item.Exists;
    public string SizeText => Item.Exists ? FormatBytes(Item.Bytes) + (Item.Files > 0 ? $" in {Item.Files:N0} files" : "") : "nothing to clean";
    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }

    public static string FormatBytes(long b) => b < 1024 ? $"{b} B" : b < 1024L * 1024 ? $"{b / 1024.0:0.#} KB" : b < 1024L * 1024 * 1024 ? $"{b / 1024.0 / 1024.0:0.#} MB" : $"{b / 1024.0 / 1024.0 / 1024.0:0.##} GB";
}

/// <summary>Окно «Clean Up Disk»: размеры Intermediate, DDC, Saved и др. и их удаление. Content, Source и Config не затрагиваются.</summary>
public sealed class CleanupViewModel : ObservableObject
{
    private readonly string _uproject;
    private readonly IDialogService _dialogs;
    private readonly Func<bool> _editorRunning;
    private bool _busy;
    private string _status = "";
    private string _error = "";

    public CleanupViewModel(string uprojectPath, IDialogService dialogs, Func<bool> editorRunning)
    {
        _uproject = uprojectPath; _dialogs = dialogs; _editorRunning = editorRunning;
        DeleteCommand = new AsyncRelayCommand(_ => DeleteAsync(), _ => !IsBusy && Items.Any(i => i.IsChecked && i.Exists));
        RescanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsBusy);
    }

    public ObservableCollection<CleanupItemViewModel> Items { get; } = new();
    public ICommand DeleteCommand { get; }
    public ICommand RescanCommand { get; }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public long TotalBytes => Items.Sum(i => i.Item.Bytes);
    public long SelectedBytes => Items.Where(i => i.IsChecked).Sum(i => i.Item.Bytes);
    public string SelectedText => $"Selected: {CleanupItemViewModel.FormatBytes(SelectedBytes)} of {CleanupItemViewModel.FormatBytes(TotalBytes)}";

    /// <param name="keepStatus">После удаления сообщение «Freed …» должно остаться, а не затираться повторным замером.</param>
    public async Task ScanAsync(bool keepStatus = false)
    {
        IsBusy = true;
        if (!keepStatus) { Error = ""; Status = "Measuring…"; }
        try
        {
            var scanned = await Task.Run(() => CleanupScanner.Scan(_uproject));
            foreach (var i in Items) i.PropertyChanged -= OnItemChanged;
            Items.Clear();
            foreach (var s in scanned) { var vm = new CleanupItemViewModel(s); vm.PropertyChanged += OnItemChanged; Items.Add(vm); }
            if (!keepStatus) Status = "";
        }
        catch (Exception ex) { Error = ex.Message; if (!keepStatus) Status = ""; }
        finally { IsBusy = false; RaiseTotals(); }
    }

    private void OnItemChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupItemViewModel.IsChecked)) { RaiseTotals(); CommandManager.InvalidateRequerySuggested(); }
    }

    private void RaiseTotals()
    {
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedText));
    }

    private async Task DeleteAsync()
    {
        if (_editorRunning()) { _dialogs.Error("Unreal Editor is running. Close it first: it keeps these files open."); return; }
        var chosen = Items.Where(i => i.IsChecked && i.Exists).Select(i => i.Item).ToList();
        var warn = chosen.Where(i => !i.Recommended).Select(i => i.Title).ToList();
        var msg = $"Delete {string.Join(", ", chosen.Select(i => i.Title))}?\n\nThis frees {CleanupItemViewModel.FormatBytes(chosen.Sum(i => i.Bytes))}. Content, Source and Config are not touched." +
                  (warn.Count > 0 ? $"\n\nNote: {string.Join(" and ", warn)} take{(warn.Count == 1 ? "s" : "")} a long time to rebuild." : "");
        if (!_dialogs.Confirm(msg)) return;
        IsBusy = true; Error = "";
        try
        {
            Status = "Deleting…";
            var result = await Task.Run(() => CleanupScanner.Delete(_uproject, chosen));
            Status = $"Freed {CleanupItemViewModel.FormatBytes(result.FreedBytes)}.";
            if (result.Failures.Count > 0) Error = "Some files could not be deleted (in use?):\n" + string.Join("\n", result.Failures.Take(5));
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsBusy = false; }
        await ScanAsync(keepStatus: true);
    }
}

// ======================= Ошибки сборки =======================

public sealed class BuildIssueViewModel
{
    public BuildIssueViewModel(BuildIssue issue, ProjectProfile profile, IDialogService dialogs)
    {
        Issue = issue;
        OpenCommand = new RelayCommand(_ =>
        {
            try { IdeOpener.Open(profile, issue.File, issue.Line); }
            catch (Exception ex) { dialogs.Error(ex.Message); }
        }, _ => issue.HasFile);
    }

    public BuildIssue Issue { get; }
    public bool IsError => Issue.Severity == BuildSeverity.Error;
    public string SeverityText => IsError ? "error" : "warning";
    public string Location => Issue.Location;
    public string Code => Issue.Code;
    public string Message => Issue.Message;
    public string FullPath => Issue.File;
    public ICommand OpenCommand { get; }
}

/// <summary>Окно «Build Errors»: ошибки и предупреждения компилятора; двойной клик открывает файл в IDE на нужной строке.</summary>
public sealed class BuildIssuesViewModel : ObservableObject
{
    private readonly IReadOnlyList<BuildIssueViewModel> _all;
    private readonly Action<string?> _copy;
    private bool _showWarnings;

    public BuildIssuesViewModel(string title, IReadOnlyList<BuildIssue> issues, ProjectProfile profile, IDialogService dialogs, Action<string?> copy)
    {
        Title = title;
        _copy = copy;
        // Одна и та же ошибка часто повторяется для каждого включения заголовка: схлопываем одинаковые.
        _all = issues.GroupBy(i => (i.Severity, i.File, i.Line, i.Code, i.Message)).Select(g => new BuildIssueViewModel(g.First(), profile, dialogs)).ToList();
        _showWarnings = !_all.Any(i => i.IsError);   // есть ошибки: сначала только они; нет ошибок: показываем предупреждения
        Rebuild();
        CopyAllCommand = new RelayCommand(_ => _copy(string.Join(Environment.NewLine, Items.Select(i => $"{i.SeverityText}: {(i.Location.Length > 0 ? i.Location + " " : "")}{i.Code} {i.Message}".Replace("  ", " ")))));
    }

    public string Title { get; }
    public ObservableCollection<BuildIssueViewModel> Items { get; } = new();
    public ICommand CopyAllCommand { get; }
    public int ErrorCount => _all.Count(i => i.IsError);
    public int WarningCount => _all.Count(i => !i.IsError);
    public string Summary => $"{ErrorCount} error{(ErrorCount == 1 ? "" : "s")}, {WarningCount} warning{(WarningCount == 1 ? "" : "s")}";
    public bool ShowWarnings { get => _showWarnings; set { if (Set(ref _showWarnings, value)) Rebuild(); } }

    private void Rebuild()
    {
        Items.Clear();
        foreach (var i in _all.Where(i => i.IsError || _showWarnings).OrderByDescending(i => i.IsError)) Items.Add(i);
    }
}
