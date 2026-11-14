using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using PerforceLauncher.App.Infrastructure;
using PerforceLauncher.Core.Launching;
using PerforceLauncher.Core.Models;
using PerforceLauncher.Core.VersionControl;

namespace PerforceLauncher.App.ViewModels;

/// <summary>
/// Инструменты поверх Perforce: история файла и сравнение, shelve, блокировки, предупреждение о пересечении с коллегами,
/// метки, очистка диска. Каждый открывается своим окном; здесь только запуск и связка с остальным интерфейсом.
/// </summary>
public sealed partial class MainViewModel
{
    private DiffService _diff = null!;
    private readonly HashSet<string> _notifiedShared = new(StringComparer.OrdinalIgnoreCase);
    private string _sharedText = "";

    public DescriptionBook Book { get; private set; } = null!;
    public ICommand FileHistoryCommand { get; private set; } = null!;
    public ICommand DiffPendingCommand { get; private set; } = null!;
    public ICommand DiffChangeFileCommand { get; private set; } = null!;
    public ICommand LockFilesCommand { get; private set; } = null!;
    public ICommand UnlockFilesCommand { get; private set; } = null!;
    public ICommand ShelveChangelistCommand { get; private set; } = null!;
    public ICommand ShowShelvesCommand { get; private set; } = null!;
    public ICommand ShowLabelsCommand { get; private set; } = null!;
    public ICommand ShowCleanupCommand { get; private set; } = null!;

    /// <summary>Сколько ваших открытых файлов сейчас открыто и у кого-то ещё.</summary>
    public int SharedCount => PendingFiles.Count(f => f.SharedWith.Length > 0);
    public bool HasShared => SharedCount > 0;
    public string SharedText { get => _sharedText; private set { if (Set(ref _sharedText, value)) { OnPropertyChanged(nameof(SharedCount)); OnPropertyChanged(nameof(HasShared)); } } }

    private void InitTools()
    {
        _diff = new DiffService(_vcs, () => P4Locator.Resolve(_settings.P4Path));
        Book = new DescriptionBook(_settings, _store);
        _dialogs.DescriptionBook = Book;

        FileHistoryCommand = new AsyncRelayCommand(p => OpenFileHistoryAsync(p), _ => Selected != null);
        DiffPendingCommand = new AsyncRelayCommand(p => DiffPendingAsync(p as PendingFileViewModel), p => p is PendingFileViewModel { File: not null });
        DiffChangeFileCommand = new AsyncRelayCommand(p => DiffChangeFileAsync(p as ChangeFile), p => p is ChangeFile);
        LockFilesCommand = new AsyncRelayCommand(_ => LockSelectedAsync(@lock: true), _ => SelectedFilesOnly().Count > 0);
        UnlockFilesCommand = new AsyncRelayCommand(_ => LockSelectedAsync(@lock: false), _ => SelectedFilesOnly().Count > 0);
        ShelveChangelistCommand = new AsyncRelayCommand(p => ShelveChangelistAsync(Convert.ToInt64(p)), p => p != null && !IsBusy);
        ShowShelvesCommand = new AsyncRelayCommand(_ => ShowShelvesAsync(), _ => Selected != null);
        ShowLabelsCommand = new AsyncRelayCommand(_ => ShowLabelsAsync(), _ => Selected != null && !IsBusy);
        ShowCleanupCommand = new AsyncRelayCommand(_ => ShowCleanupAsync(), _ => Selected != null && !IsBusy);

        // Раз в 5 минут: не заняли ли коллеги ваши файлы и не освободился ли файл, за которым вы следите.
        var shared = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        shared.Tick += (_, _) =>
        {
            if (Selected != null && (PendingCount > 0 || Selected.Model.WatchedFiles.Count > 0)) _ = LoadCheckoutsAsync();
        };
        shared.Start();
    }

    // ---------- история файла и сравнение ----------

    private async Task OpenFileHistoryAsync(object? source)
    {
        var profile = Selected?.Model;
        if (profile == null) return;
        string? depot = null, local = null, type = "";
        switch (source)
        {
            case PendingFileViewModel { File: { } f }: depot = f.DepotPath; local = f.LocalPath; type = f.Type; break;
            case CheckoutFileViewModel c: depot = c.DepotPath; type = c.File.Type; break;
            case ChangeFile cf: depot = cf.DepotPath; type = cf.Type; break;
            case string s when s.StartsWith("//"): depot = s; break;
        }
        if (depot == null) return;
        var vm = new FileHistoryViewModel(_vcs, profile, depot, local, type, _diff, _dialogs, CopyToClipboard);
        await vm.LoadAsync();
        _dialogs.ShowDialog(vm);
    }

    private async Task DiffPendingAsync(PendingFileViewModel? row)
    {
        if (row?.File is not { } f || Selected == null) return;
        if (f.Action.Equals("add", StringComparison.OrdinalIgnoreCase) || f.Action.Equals("branch", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This is a new file: there is no depot version to compare with.");
        if (f.Action.Contains("delete", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This file is marked for delete: nothing to compare.");
        await _diff.DiffWithLocalAsync(Selected.Model, f.DepotPath, f.Rev, f.LocalPath, f.Type);
    }

    private async Task DiffChangeFileAsync(ChangeFile? cf)
    {
        if (cf == null || Selected == null) return;
        if (cf.Rev <= 1 || cf.Action.Equals("add", StringComparison.OrdinalIgnoreCase) || cf.Action.Equals("branch", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This commit added the file: there is no previous revision to compare with.");
        await _diff.DiffRevisionsAsync(Selected.Model, cf.DepotPath, cf.Rev - 1, cf.Rev, cf.Type);
    }

    // ---------- блокировки ----------

    private async Task LockSelectedAsync(bool @lock)
    {
        var profile = Selected!.Model;
        var files = SelectedFilesOnly();
        if (files.Count == 0) return;
        var paths = files.Select(f => f.DepotPath).ToList();
        if (@lock) await _vcs.LockAsync(profile, paths);
        else await _vcs.UnlockAsync(profile, paths);
        AppendOutput($"{(@lock ? "Locked" : "Unlocked")} {paths.Count} file(s).");
        await RefreshPendingAsync();
    }

    // ---------- shelve ----------

    private async Task ShelveChangelistAsync(long id)
    {
        var vm = Selected!;
        if (IsBusy) throw new InvalidOperationException($"Please wait: {BusyText.TrimEnd('…')} is still running.");
        var cl = _changelists.FirstOrDefault(c => c.Id == id);
        if (cl == null || cl.Files.Count == 0) { _dialogs.Error("There is nothing to shelve in this changelist."); return; }

        var target = id;
        if (cl.IsDefault)
        {
            // Shelve работает только с нумерованным changelist'ом: создаём его из default.
            var desc = _dialogs.PromptText("Shelve", $"Shelve {cl.Files.Count} file(s) from the default changelist. Description:", "", "Shelve", requireEnglish: true);
            if (string.IsNullOrWhiteSpace(desc)) return;
            target = await _vcs.CreateChangelistAsync(vm.Model, desc.Trim());
            await _vcs.MoveFilesAsync(vm.Model, target, cl.Files.Select(f => f.DepotPath).ToList());
            AppendOutput($"Created CL {target} from the default changelist.");
        }
        await RunBusyAsync($"Shelving CL {target}…", cancellable: false, async _ =>
        {
            Output = "";
            AppendOutput($"> p4 shelve -f -c {target}");
            await _vcs.ShelveAsync(vm.Model, target, new Progress<string>(AppendOutput));
            AppendOutput($"CL {target} is shelved. Your local files are unchanged; teammates can unshelve it from the Shelves window.");
        });
        await RefreshPendingAsync();
    }

    private async Task ShowShelvesAsync()
    {
        var vm = Selected;
        if (vm == null) return;
        var me = !string.IsNullOrWhiteSpace(vm.Model.P4User) ? vm.Model.P4User : (await _vcs.GetConnectionInfoAsync(vm.Model)).User;
        var shelves = new ShelvesViewModel(_vcs, vm.Model, me, _dialogs, () => _ = RefreshPendingAsync());
        await shelves.LoadAsync();
        _dialogs.ShowDialog(shelves);
        await RefreshPendingAsync();
    }

    // ---------- метки ----------

    private async Task ShowLabelsAsync()
    {
        var vm = Selected;
        if (vm == null) return;
        var labels = new LabelsViewModel(_vcs, vm.Model);
        await labels.LoadAsync();
        _dialogs.ShowDialog(labels);
        if (labels.SyncTarget is { } label) await SyncToLabelAsync(vm, label);
    }

    private async Task SyncToLabelAsync(ProfileViewModel vm, string label)
    {
        var ok = _dialogs.Confirm(
            $"Sync the workspace to label '{label}'?\n\nFiles will match the depot as of this label. Newer changes leave your workspace until you press Get Latest. " +
            "Files you have opened are not touched.");
        if (!ok) return;
        if (EditorRunning() && !_dialogs.Confirm("Unreal Editor is running and may lock files. Continue?")) return;

        IReadOnlyList<string> writable = Array.Empty<string>();
        await RunBusyAsync($"Syncing to {label}…", cancellable: true, async ct =>
        {
            Output = "";
            AppendOutput($"> p4 sync @{label} ({vm.Subtitle})");
            var result = await _vcs.SyncToLabelAsync(vm.Model, label, new Progress<string>(line =>
            {
                AppendOutput(line);
                if (CodeFileRx.IsMatch(line)) _codeSyncedAt[vm.Model.Id] = DateTime.Now;
            }), ct);
            writable = result.WritableNotUpdated;
            AppendOutput($"Workspace is now at label {label}" + (writable.Count > 0 ? $", except {writable.Count} writable file(s): they were not overwritten." : "."));
        });
        _watcher.ResetNotified(vm.Model.Id);
        await CheckAsync(vm);
        await RefreshPendingAsync();
        await UpdateBuildStateAsync(force: true);
    }

    // ---------- очистка диска ----------

    private async Task ShowCleanupAsync()
    {
        var vm = Selected;
        var up = vm == null ? null : ProfilePaths.UProject(vm.Model);
        if (vm == null || up == null || !System.IO.File.Exists(up))
        {
            _dialogs.Error("No .uproject found in this workspace: nothing to clean.");
            return;
        }
        var cleanup = new CleanupViewModel(up, _dialogs, EditorRunning);
        _dialogs.ShowDialog(cleanup);   // окно показывается сразу, размеры считаются в фоне
        await Task.CompletedTask;
    }

    // ---------- пересечения с коллегами и слежение за файлами ----------

    private string SharedWithOf(string depotPath) =>
        string.Join(", ", _checkoutAll.Where(c => string.Equals(c.DepotPath, depotPath, StringComparison.OrdinalIgnoreCase) && !IsMe(c.User))
            .Select(c => c.User).Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>Проставляет в Pending, кто ещё держит ваши файлы; о новых пересечениях сообщает уведомлением.</summary>
    private void ApplySharedToPending()
    {
        if (!_checkoutLoaded) return;
        foreach (var row in PendingFiles.Where(f => f.File != null)) row.SharedWith = SharedWithOf(row.File!.DepotPath);
        var shared = PendingFiles.Where(f => f.SharedWith.Length > 0).ToList();
        var people = shared.SelectMany(f => f.SharedWith.Split(", ")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SharedText = shared.Count == 0 ? "" :
            $"{(shared.Count == 1 ? "1 of your files is" : shared.Count + " of your files are")} also open by {string.Join(", ", people)}. " +
            "Check the ALSO OPEN BY column and agree who goes first, otherwise the second submit needs a resolve.";

        var profileId = Selected?.Model.Id;
        var fresh = shared.Where(f => _notifiedShared.Add($"{profileId}|{f.File!.DepotPath}|{f.SharedWith}")).ToList();
        if (fresh.Count > 0 && profileId != null)
        {
            var names = string.Join(", ", fresh.Take(3).Select(f => System.IO.Path.GetFileName(f.File!.LocalPath))) + (fresh.Count > 3 ? "…" : "");
            _notifier.Notify($"{Selected!.Name}: a teammate opened your file{(fresh.Count == 1 ? "" : "s")}",
                $"{string.Join(", ", fresh.SelectMany(f => f.SharedWith.Split(", ")).Distinct())} also edit{(fresh.Count == 1 && people.Count == 1 ? "s" : "")}: {names}", profileId);
        }
    }

    /// <summary>Файлы из списка слежения, которые больше никто не держит: сообщаем и убираем из списка.</summary>
    private void CheckWatches()
    {
        var profile = Selected?.Model;
        if (profile == null || !_checkoutLoaded || profile.WatchedFiles.Count == 0) return;
        var freed = profile.WatchedFiles.Where(d => !_checkoutAll.Any(c => string.Equals(c.DepotPath, d, StringComparison.OrdinalIgnoreCase) && !IsMe(c.User))).ToList();
        if (freed.Count == 0) return;
        foreach (var d in freed) profile.WatchedFiles.Remove(d);
        try { _store.Save(_settings); } catch { /* список слежения не критичен */ }
        var names = string.Join(", ", freed.Take(3).Select(d => d[(d.LastIndexOf('/') + 1)..])) + (freed.Count > 3 ? "…" : "");
        _notifier.Notify($"{Selected!.Name}: file{(freed.Count == 1 ? " is" : "s are")} free now", $"Nobody has it checked out any more: {names}", profile.Id);
        ApplyCheckoutFilter();   // обновить пометки «слежу» в списке
    }

    private bool IsWatched(string depotPath) => Selected?.Model.WatchedFiles.Contains(depotPath, StringComparer.OrdinalIgnoreCase) == true;

    private void ToggleWatch(string depotPath)
    {
        var profile = Selected?.Model;
        if (profile == null) return;
        var existing = profile.WatchedFiles.FirstOrDefault(d => string.Equals(d, depotPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null) profile.WatchedFiles.Remove(existing); else profile.WatchedFiles.Add(depotPath);
        try { _store.Save(_settings); } catch { /* не критично */ }
        ApplyCheckoutFilter();
        AppendOutput(existing != null ? $"Stopped watching {depotPath[(depotPath.LastIndexOf('/') + 1)..]}." : $"You will be notified when {depotPath[(depotPath.LastIndexOf('/') + 1)..]} is free.");
    }

    /// <summary>После загрузки списка checkout'ов: пересечения и слежение.</summary>
    private void AfterCheckoutsLoaded()
    {
        ApplySharedToPending();
        CheckWatches();
    }

    /// <summary>После обновления Pending: подставить уже известные пересечения или запросить свежие.</summary>
    private void SharedAfterPendingRefresh()
    {
        if (PendingFiles.Count == 0) { SharedText = ""; return; }
        if (_checkoutLoaded && DateTime.UtcNow - _checkoutLoadedAt < TimeSpan.FromMinutes(1)) ApplySharedToPending();
        else if (!_checkoutBusy) _ = LoadCheckoutsAsync();
    }
}
