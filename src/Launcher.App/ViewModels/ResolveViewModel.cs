using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using PerforceLauncher.App.Infrastructure;
using PerforceLauncher.Core.Models;
using PerforceLauncher.Core.VersionControl;

namespace PerforceLauncher.App.ViewModels;

public enum ResolveState { Pending, Working, Resolved, Failed }

/// <summary>Один файл в окне Resolve: что произошло с ним и что можно сделать.</summary>
public sealed class ResolveItemViewModel : ObservableObject
{
    private ResolveState _state = ResolveState.Pending;
    private string _message = "";

    public ResolveItemViewModel(UnresolvedFile file, string workspaceRoot, ResolveViewModel owner)
    {
        File = file;
        Folder = ProfilePaths_ToRelativeFolder(workspaceRoot, file.LocalPath);
        KeepMineCommand = new AsyncRelayCommand(_ => owner.ResolveSelectedAsync(new[] { this }, ResolveMode.AcceptYours), _ => CanAct(owner));
        TakeTheirsCommand = new AsyncRelayCommand(_ => owner.ResolveSelectedAsync(new[] { this }, ResolveMode.AcceptTheirs), _ => CanAct(owner));
        AutoMergeCommand = new AsyncRelayCommand(_ => owner.ResolveSelectedAsync(new[] { this }, ResolveMode.AutoMerge), _ => CanAct(owner) && file.IsText);
        MergeCommand = new AsyncRelayCommand(_ => owner.MergeInToolAsync(this), _ => CanAct(owner) && file.IsText && owner.HasMergeTool);
    }

    private bool CanAct(ResolveViewModel owner) => !owner.IsBusy && State is ResolveState.Pending or ResolveState.Failed;

    private static string ProfilePaths_ToRelativeFolder(string root, string localPath)
    {
        var rel = PerforceLauncher.Core.Launching.ProfilePaths.ToRelative(root, localPath);
        var dir = Path.GetDirectoryName(rel) ?? "";
        return dir.Replace('\\', '/');
    }

    public UnresolvedFile File { get; }
    public string Name => File.FileName;
    public string Folder { get; }
    public bool IsText => File.IsText;
    public string KindText => File.IsText ? "Text file: can be merged" : "Binary file: pick one version";

    /// <summary>Чья правка пришла: «CL 1234 by daniil: fix drinks».</summary>
    public string TheirsInfo
    {
        get
        {
            var who = File.HeadChange > 0 ? $"CL {File.HeadChange}" + (File.HeadUser.Length > 0 ? $" by {File.HeadUser}" : "") : "";
            var first = File.HeadDescription.Split('\n')[0].Trim();
            var head = $"Newer version #{File.HeadRev}" + (who.Length > 0 ? $" ({who})" : "");
            return first.Length > 0 ? $"{head}: {first}" : head;
        }
    }
    public string YoursInfo => $"Your changes are based on #{File.HaveRev}";

    public ResolveState State { get => _state; set { if (Set(ref _state, value)) { OnPropertyChanged(nameof(IsDone)); OnPropertyChanged(nameof(IsFailed)); OnPropertyChanged(nameof(IsWorking)); OnPropertyChanged(nameof(CanChoose)); } } }
    public string Message { get => _message; set => Set(ref _message, value); }
    public bool IsDone => State == ResolveState.Resolved;
    public bool IsFailed => State == ResolveState.Failed;
    public bool IsWorking => State == ResolveState.Working;
    public bool CanChoose => State is ResolveState.Pending or ResolveState.Failed;

    public ICommand KeepMineCommand { get; }
    public ICommand TakeTheirsCommand { get; }
    public ICommand AutoMergeCommand { get; }
    public ICommand MergeCommand { get; }
}

/// <summary>
/// Окно «Resolve»: файлы, которые изменили и вы, и кто-то ещё. Для каждого: оставить мой вариант, взять их,
/// слить автоматически (безопасно) или вручную в P4Merge. Перед любой перезаписью локальный файл копируется в Backups.
/// </summary>
public sealed class ResolveViewModel : ObservableObject
{
    private readonly IVersionControl _vcs;
    private readonly ProjectProfile _profile;
    private readonly string _projectName;
    private readonly IDialogService _dialogs;
    private readonly Action _onChanged;
    private readonly string? _mergeTool;
    private bool _isBusy;
    private string _status = "";
    private string _error = "";

    public ResolveViewModel(IVersionControl vcs, ProjectProfile profile, string projectName, string p4Path, IDialogService dialogs, Action onChanged)
    {
        _vcs = vcs; _profile = profile; _projectName = projectName; _dialogs = dialogs; _onChanged = onChanged;
        _mergeTool = MergeTool.Find(p4Path);
        KeepMineAllCommand = new AsyncRelayCommand(_ => ResolveSelectedAsync(Open(), ResolveMode.AcceptYours), _ => !IsBusy && Open().Count > 0);
        TakeTheirsAllCommand = new AsyncRelayCommand(_ => ResolveSelectedAsync(Open(), ResolveMode.AcceptTheirs), _ => !IsBusy && Open().Count > 0);
        AutoMergeAllCommand = new AsyncRelayCommand(_ => ResolveSelectedAsync(Open().Where(i => i.IsText).ToList(), ResolveMode.AutoMerge), _ => !IsBusy && Open().Any(i => i.IsText));
        ReloadCommand = new AsyncRelayCommand(_ => LoadAsync(), _ => !IsBusy);
    }

    public ObservableCollection<ResolveItemViewModel> Items { get; } = new();
    public bool HasMergeTool => _mergeTool != null;
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public bool AllDone => Items.Count > 0 && Items.All(i => i.IsDone);
    public bool HasBinary => Items.Any(i => !i.IsText);
    public string MergeToolHint => HasMergeTool ? "" : "P4Merge was not found (it comes with P4V). Text files can still be merged automatically.";

    public ICommand KeepMineAllCommand { get; }
    public ICommand TakeTheirsAllCommand { get; }
    public ICommand AutoMergeAllCommand { get; }
    public ICommand ReloadCommand { get; }

    private List<ResolveItemViewModel> Open() => Items.Where(i => i.State is ResolveState.Pending or ResolveState.Failed).ToList();

    public async Task LoadAsync()
    {
        IsBusy = true;
        Error = "";
        try
        {
            var files = await _vcs.GetUnresolvedAsync(_profile);
            Items.Clear();
            foreach (var f in files) Items.Add(new ResolveItemViewModel(f, _profile.WorkspaceRoot, this));
            RefreshStatus();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsBusy = false; }
    }

    private void RefreshStatus()
    {
        var left = Items.Count(i => !i.IsDone);
        Status = Items.Count == 0 ? "Nothing to resolve."
            : left == 0 ? "All files are resolved. You can submit now."
            : $"{left} of {Items.Count} file{(Items.Count == 1 ? "" : "s")} left to resolve.";
        OnPropertyChanged(nameof(AllDone));
        OnPropertyChanged(nameof(HasBinary));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Выбранным способом решает файлы; спрашивает подтверждение, если решение что-то отбрасывает.</summary>
    public async Task ResolveSelectedAsync(IReadOnlyList<ResolveItemViewModel> items, ResolveMode mode)
    {
        if (items.Count == 0 || IsBusy) return;
        var what = items.Count == 1 ? $"'{items[0].Name}'" : $"{items.Count} files";
        if (mode == ResolveMode.AcceptTheirs &&
            !_dialogs.Confirm($"Take the server version of {what}?\n\nYour changes in {(items.Count == 1 ? "it" : "them")} are replaced. A copy of your file is saved to the Backups folder first."))
            return;
        if (mode == ResolveMode.AcceptYours &&
            !_dialogs.Confirm($"Keep your version of {what}?\n\nWhen you submit, your version replaces what your teammates submitted in {(items.Count == 1 ? "this file" : "these files")}. Their changes will not be merged in."))
            return;

        IsBusy = true;
        Error = "";
        foreach (var i in items) { i.State = ResolveState.Working; i.Message = ""; }
        try
        {
            if (mode == ResolveMode.AcceptTheirs)
                LocalBackup.Save(_projectName, _profile.WorkspaceRoot, items.Select(i => i.File.LocalPath));
            var outcomes = await _vcs.ResolveAsync(_profile, items.Select(i => i.File).ToList(), mode, null);
            foreach (var item in items)
            {
                var o = outcomes.FirstOrDefault(x => string.Equals(x.DepotPath, item.File.DepotPath, StringComparison.OrdinalIgnoreCase));
                if (o is { Resolved: true })
                {
                    item.State = ResolveState.Resolved;
                    item.Message = mode switch
                    {
                        ResolveMode.AcceptTheirs => "Took the server version",
                        ResolveMode.AcceptYours => "Kept your version",
                        _ => "Merged automatically",
                    };
                }
                else
                {
                    item.State = ResolveState.Failed;
                    item.Message = mode == ResolveMode.AutoMerge
                        ? "Both of you changed the same lines: merge it in P4Merge or choose a side."
                        : o?.Message ?? "Not resolved.";
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var i in items.Where(i => i.State == ResolveState.Working)) i.State = ResolveState.Pending;
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshStatus();
            _onChanged();
        }
    }

    /// <summary>Ручное слияние: P4Merge с базой, их версией и вашей; результат записывается в файл и принимается как «мой».</summary>
    public async Task MergeInToolAsync(ResolveItemViewModel item)
    {
        if (_mergeTool == null || IsBusy) return;
        IsBusy = true;
        Error = "";
        item.State = ResolveState.Working;
        item.Message = "P4Merge is open: save the result and close it.";
        try
        {
            var merged = await MergeTool.MergeAsync(_mergeTool, _vcs, _profile, item.File);
            if (merged == null)
            {
                item.State = ResolveState.Pending;
                item.Message = "P4Merge was closed without saving a result.";
                return;
            }
            LocalBackup.Save(_projectName, _profile.WorkspaceRoot, new[] { item.File.LocalPath });
            var local = item.File.LocalPath;
            if (File.GetAttributes(local).HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(local, FileAttributes.Normal);
            File.Copy(merged, local, overwrite: true);
            var outcomes = await _vcs.ResolveAsync(_profile, new[] { item.File }, ResolveMode.AcceptYours, null);
            if (outcomes.Any(o => o.Resolved))
            {
                item.State = ResolveState.Resolved;
                item.Message = "Merged in P4Merge";
            }
            else
            {
                item.State = ResolveState.Failed;
                item.Message = outcomes.FirstOrDefault()?.Message ?? "Not resolved.";
            }
        }
        catch (Exception ex)
        {
            item.State = ResolveState.Pending;
            item.Message = "";
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshStatus();
            _onChanged();
        }
    }
}
