using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using PerforceLauncher.App.Infrastructure;
using PerforceLauncher.Core.Launching;
using PerforceLauncher.Core.Models;
using PerforceLauncher.Core.Services;
using PerforceLauncher.Core.VersionControl;

namespace PerforceLauncher.App.ViewModels;

public interface IDialogService
{
    ProjectProfile? EditProfile(ProjectProfile? existing);
    Task<WorkspaceInfo?> PickWorkspaceAsync(ProjectProfile profile);
    /// <summary>Пароль и галочка «запомнить»; null — отмена.</summary>
    (string Password, bool Remember)? PromptPassword(string message, bool rememberDefault);
    bool EditSettings(AppSettings settings);
    bool Confirm(string message);
    void Error(string message);
    /// <summary>Многострочный ввод текста (описание changelist'а). null = отмена.</summary>
    string? PromptText(string title, string caption, string initial, string okText);
}

public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly IVersionControl _vcs;
    private readonly ChangeWatcher _watcher;
    private readonly LaunchActionRegistry _actions;
    private readonly IDialogService _dialogs;
    private readonly INotifier _notifier;

    private ProfileViewModel? _selected;
    private string _output = "";

    public ObservableCollection<ProfileViewModel> Profiles { get; } = new();
    public ObservableCollection<ActionViewModel> Actions { get; } = new();
    /// <summary>Все действия, кроме главной кнопки «Launch Unreal».</summary>
    public ObservableCollection<ActionViewModel> SecondaryActions { get; } = new();
    private ActionViewModel? _primaryAction;
    public ActionViewModel? PrimaryAction { get => _primaryAction; private set => Set(ref _primaryAction, value); }

    // ---------- длительные операции и журнал ----------
    private CancellationTokenSource? _opCts;
    private bool _isBusy;
    private bool _canCancel;
    private bool _isLogOpen;
    private string _busyText = "";
    private string _lastOutputLine = "";

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public bool CanCancel { get => _canCancel; private set => Set(ref _canCancel, value); }
    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }
    public bool IsLogOpen { get => _isLogOpen; set => Set(ref _isLogOpen, value); }
    public string LastOutputLine { get => _lastOutputLine; private set => Set(ref _lastOutputLine, value); }

    // ---------- подсказка под статусом («сгенерируйте project files» и т.п.) ----------
    private string _hintText = "";
    private string _hintKey = "";
    private readonly HashSet<string> _dismissedHints = new();
    public string HintText { get => _hintText; private set { if (Set(ref _hintText, value)) OnPropertyChanged(nameof(HasHint)); } }
    public bool HasHint => _hintText.Length > 0;

    public int PendingCount => _changelists.Sum(c => c.Files.Count);
    public string PendingCountText => PendingCount == 1 ? "1 file" : $"{PendingCount} files";

    // Пользователь/сервер из p4 info (учитывает p4 set и P4CONFIG), кэш по профилю.
    private readonly Dictionary<Guid, string> _connections = new();

    /// <summary>Подключение выбранного профиля для строки состояния: «user @ perforce-server:1666».</summary>
    public string ConnectionText
    {
        get
        {
            if (Selected == null) return "";
            var m = Selected.Model;
            if (!string.IsNullOrWhiteSpace(m.P4User) && !string.IsNullOrWhiteSpace(m.P4Port)) return $"{m.P4User} @ {m.P4Port}";
            return _connections.TryGetValue(m.Id, out var text) ? text : "Perforce";
        }
    }

    private async Task LoadConnectionAsync(ProfileViewModel vm)
    {
        if (_connections.ContainsKey(vm.Model.Id)) return;
        try
        {
            var (user, server) = await _vcs.GetConnectionInfoAsync(vm.Model);
            var m = vm.Model;
            _connections[m.Id] = $"{(string.IsNullOrWhiteSpace(m.P4User) ? user : m.P4User)} @ {(string.IsNullOrWhiteSpace(m.P4Port) ? server : m.P4Port)}";
            if (vm == Selected) OnPropertyChanged(nameof(ConnectionText));
        }
        catch (Exception) { /* оффлайн или нужен логин — оставляем «Perforce» */ }
    }

    public ICommand CancelOperationCommand { get; }
    public ICommand ToggleLogCommand { get; }
    public ICommand LaunchPrimaryCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand SyncToChangeCommand { get; }
    public ICommand HintCommand { get; }
    public ICommand DismissHintCommand { get; }
    public ICommand ShowInExplorerCommand { get; }

    public ICommand AddProfileCommand { get; }
    public ICommand EditProfileCommand { get; }
    public ICommand RemoveProfileCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RefreshAllCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand SwitchWorkspaceCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand SettingsCommand { get; }

    // ---------- pending changes ----------
    private IReadOnlyList<PendingChangelist> _changelists = Array.Empty<PendingChangelist>();
    private string _pendingError = "";
    private DateTime _pendingLoadedAt = DateTime.MinValue;

    public ObservableCollection<PendingFileViewModel> PendingFiles { get; } = new();
    public IReadOnlyList<PendingChangelist> Changelists => _changelists;
    /// <summary>Выделенные строки списка; выставляется из code-behind.</summary>
    public IReadOnlyList<PendingFileViewModel> SelectedPending
    {
        get => _selectedPending;
        set
        {
            _selectedPending = value ?? Array.Empty<PendingFileViewModel>();
            // Выделили файлы одного changelist'а — он и становится выбранным (и остаётся им после снятия выделения).
            var ids = _selectedPending.Select(f => f.ChangelistId).Distinct().ToList();
            if (ids.Count == 1) _selectedChangelistId = ids[0];
            UpdateChangelistSelection();
        }
    }
    private IReadOnlyList<PendingFileViewModel> _selectedPending = Array.Empty<PendingFileViewModel>();

    // Выбранный changelist: клик по заголовку или по его файлу.
    private long? _selectedChangelistId;
    private readonly List<ChangelistHeaderViewModel> _headers = new();

    /// <summary>Подпись кнопки Submit в панели: сразу видно, что именно отправится.</summary>
    public string SubmitButtonText => ResolveTargetChangelist() switch
    {
        null => "Submit…",
        { IsDefault: true } => "Submit Default…",
        var cl => $"Submit CL {cl.Id}…",
    };

    public ICommand SubmitChangelistCommand { get; private set; } = null!;
    public ICommand RevertChangelistCommand { get; private set; } = null!;
    public ICommand EditChangelistCommand { get; private set; } = null!;
    public ICommand DeleteChangelistByIdCommand { get; private set; } = null!;

    /// <summary>Клик по заголовку changelist'а.</summary>
    public void SelectChangelist(long id)
    {
        _selectedChangelistId = id;
        UpdateChangelistSelection();
    }

    private void UpdateChangelistSelection()
    {
        var target = ResolveTargetChangelist()?.Id;
        foreach (var h in _headers) h.IsSelected = h.Id == target;
        OnPropertyChanged(nameof(SubmitButtonText));
        CommandManager.InvalidateRequerySuggested();
    }
    public string PendingHeader => "Pending";
    public string PendingError { get => _pendingError; private set => Set(ref _pendingError, value); }

    public ICommand RefreshPendingCommand { get; }
    public ICommand ReconcileCommand { get; }
    public ICommand NewChangelistCommand { get; }
    public ICommand MoveToCommand { get; }
    public ICommand RevertCommand { get; }
    public ICommand SubmitCommand { get; }
    public ICommand EditDescriptionCommand { get; }
    public ICommand DeleteChangelistCommand { get; }

    // ---------- history ----------
    private const int HistoryPageSize = 100;
    private const int MaxHistoryFiles = 3000;
    private readonly ObservableCollection<HistoryEntry> _history = new();
    private HistoryEntry? _selectedHistory;
    private string _historyFilter = "";
    private string _historyError = "";
    private string _historyDescription = "";
    private string _historyFilesInfo = "";
    private bool _historyHasMore;
    private bool _historyLoaded;
    private bool _historyBusy;
    private bool _historyTabActive;
    private readonly HashSet<string> _approvedCustom = new();

    public ICollectionView HistoryView { get; }
    public ObservableCollection<ChangeFile> HistoryFiles { get; } = new();
    public ICommand RefreshHistoryCommand { get; }
    public ICommand LoadMoreHistoryCommand { get; }

    public string HistoryFilter
    {
        get => _historyFilter;
        set { if (Set(ref _historyFilter, value ?? "")) HistoryView.Refresh(); }
    }
    public string HistoryError { get => _historyError; private set => Set(ref _historyError, value); }
    public string HistoryDescription { get => _historyDescription; private set => Set(ref _historyDescription, value); }
    public string HistoryFilesInfo { get => _historyFilesInfo; private set => Set(ref _historyFilesInfo, value); }
    public bool HistoryHasMore { get => _historyHasMore; private set => Set(ref _historyHasMore, value); }

    public HistoryEntry? SelectedHistory
    {
        get => _selectedHistory;
        set { if (Set(ref _selectedHistory, value)) _ = LoadHistoryDetailsAsync(value); }
    }

    private readonly SessionKeeper? _session;

    public MainViewModel(AppSettings settings, SettingsStore store, IVersionControl vcs, ChangeWatcher watcher,
        LaunchActionRegistry actions, IDialogService dialogs, INotifier notifier, SessionKeeper? session = null)
    {
        _settings = settings; _store = store; _vcs = vcs; _watcher = watcher;
        _actions = actions; _dialogs = dialogs; _notifier = notifier; _session = session;

        if (_session != null)
        {
            // События приходят из фоновой проверки — переходим в UI-поток.
            void Ui(Action a) { if (System.Windows.Application.Current?.Dispatcher is { } d) d.BeginInvoke(a); else a(); }
            _session.Renewed += account => Ui(() => AppendOutput($"Perforce session for {account} renewed automatically."));
            _session.PasswordRejected += account => Ui(() =>
            {
                AppendOutput($"Saved password for {account} was rejected by the server and removed. Sign in again.");
                _notifier.Notify("Perforce sign-in needed",
                    $"The saved password for {account} no longer works (changed?). It was removed; open the launcher and sign in.");
            });
        }

        AsyncRelayCommand.ErrorHandler = ex => _dialogs.Error(ex.Message);

        HistoryView = CollectionViewSource.GetDefaultView(_history);
        HistoryView.Filter = o => o is HistoryEntry h && h.Matches(_historyFilter.Trim());
        RefreshHistoryCommand = new AsyncRelayCommand(_ => LoadHistoryAsync(reset: true), _ => Selected != null);
        LoadMoreHistoryCommand = new AsyncRelayCommand(_ => LoadHistoryAsync(reset: false), _ => Selected != null && HistoryHasMore);

        foreach (var p in settings.Profiles) Profiles.Add(new ProfileViewModel(p));
        Selected = Profiles.FirstOrDefault(p => p.Model.Id == settings.SelectedProfileId) ?? Profiles.FirstOrDefault();

        _watcher.Checked += (_, r) =>
        {
            var pvm = Find(r.Profile);
            pvm?.Apply(r);
            // Новая синхронизация/коммиты сдвигают границу «уже у вас» в истории.
            if (pvm != null && pvm == Selected && r.Status != null)
            {
                foreach (var h in _history) h.UpdateHave(pvm.HaveChange);
                foreach (var a in Actions) a.Refresh();   // .sln/.uproject могли появиться после sync
                UpdateHint();
            }
        };
        _watcher.NewChangesFound += OnNewChanges;

        AddProfileCommand = new RelayCommand(_ => AddProfile());
        EditProfileCommand = new RelayCommand(_ => EditProfile(), _ => Selected != null);
        RemoveProfileCommand = new RelayCommand(_ => RemoveProfile(), _ => Selected != null);
        RefreshCommand = new AsyncRelayCommand(_ => CheckAsync(Selected), _ => Selected != null);
        RefreshAllCommand = new AsyncRelayCommand(_ => CheckAllAsync());
        SyncCommand = new AsyncRelayCommand(_ => SyncAsync(), _ => Selected != null);
        SwitchWorkspaceCommand = new AsyncRelayCommand(_ => SwitchWorkspaceAsync(), _ => Selected != null);
        LoginCommand = new AsyncRelayCommand(_ => LoginAsync(), _ => Selected != null);
        SettingsCommand = new RelayCommand(_ =>
        {
            if (!_dialogs.EditSettings(_settings)) return;
            _store.Save(_settings);
            ApplyAutostart();
        });

        RefreshPendingCommand = new AsyncRelayCommand(_ => RefreshPendingAsync(), _ => Selected != null);
        ReconcileCommand = new AsyncRelayCommand(_ => ReconcileAsync(), _ => Selected != null);
        NewChangelistCommand = new AsyncRelayCommand(_ => NewChangelistAsync(), _ => Selected != null);
        MoveToCommand = new AsyncRelayCommand(MoveToAsync, _ => SelectedFilesOnly().Count > 0);
        RevertCommand = new AsyncRelayCommand(_ => RevertAsync(), _ => SelectedFilesOnly().Count > 0);
        SubmitCommand = new AsyncRelayCommand(_ => SubmitAsync(), _ => Selected != null && ResolveTargetChangelist() != null);
        EditDescriptionCommand = new AsyncRelayCommand(_ => EditDescriptionAsync(), _ => ResolveTargetChangelist() is { IsDefault: false });
        DeleteChangelistCommand = new AsyncRelayCommand(_ => DeleteChangelistAsync(), _ => ResolveTargetChangelist() is { IsDefault: false, Files.Count: 0 });
        // Действия из заголовка changelist'а: параметр — его Id.
        SubmitChangelistCommand = new AsyncRelayCommand(p => SubmitAsync(Convert.ToInt64(p)), p => p != null && !IsBusy);
        RevertChangelistCommand = new AsyncRelayCommand(p => RevertChangelistAsync(Convert.ToInt64(p)), p => p != null);
        EditChangelistCommand = new AsyncRelayCommand(p => EditDescriptionAsync(Convert.ToInt64(p)), p => p != null);
        DeleteChangelistByIdCommand = new AsyncRelayCommand(p => DeleteChangelistAsync(Convert.ToInt64(p)), p => p != null);

        CancelOperationCommand = new RelayCommand(_ => _opCts?.Cancel(), _ => CanCancel);
        ToggleLogCommand = new RelayCommand(_ => IsLogOpen = !IsLogOpen);
        LaunchPrimaryCommand = new RelayCommand(_ =>
        {
            if (PrimaryAction?.Command is { } c && c.CanExecute(null)) c.Execute(null);
        });
        RevealCommand = new RelayCommand(p => { if (p is string path && path.Length > 0) ShellHelper.Reveal(path); });
        CopyCommand = new RelayCommand(p => CopyToClipboard(p?.ToString()));
        SyncToChangeCommand = new AsyncRelayCommand(p => SyncToChangeAsync(p as HistoryEntry), p => p is HistoryEntry && !IsBusy);
        HintCommand = new RelayCommand(_ => RunHintAction());
        DismissHintCommand = new RelayCommand(_ => { _dismissedHints.Add(_hintKey); HintText = ""; });
        ShowInExplorerCommand = new RelayCommand(_ => { if (Selected != null) ShellHelper.Reveal(Selected.Model.WorkspaceRoot); },
            _ => Selected != null && System.IO.Directory.Exists(Selected.Model.WorkspaceRoot));

        // «checked 2 min ago» должно стареть само.
        var clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        clock.Tick += (_, _) => { foreach (var p in Profiles) p.RefreshTime(); };
        clock.Start();
    }

    public ProfileViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            _settings.SelectedProfileId = value?.Model.Id;
            RebuildActions();
            OnPropertyChanged(nameof(HasSelection));
            ClearPending();
            _ = RefreshPendingAsync();
            ClearHistory();
            if (_historyTabActive) EnsureHistoryLoaded();
            OnPropertyChanged(nameof(ConnectionText));
            if (value != null) _ = LoadConnectionAsync(value);
            HintText = "";
            UpdateHint();
        }
    }

    public bool HasSelection => Selected != null;

    public string Output
    {
        get => _output;
        private set => Set(ref _output, value);
    }

    public void SelectProfile(Guid id) => Selected = Profiles.FirstOrDefault(p => p.Model.Id == id) ?? Selected;

    public Task CheckAllAsync() => Task.WhenAll(Profiles.Select(p => CheckAsync(p)).ToArray());

    // ---------- profile management ----------

    private void AddProfile()
    {
        var created = _dialogs.EditProfile(null);
        if (created == null) return;
        _settings.Profiles.Add(created);
        var vm = new ProfileViewModel(created);
        Profiles.Add(vm);
        Selected = vm;
        _store.Save(_settings);
        _ = CheckAsync(vm);
    }

    private void EditProfile()
    {
        if (Selected == null) return;
        var edited = _dialogs.EditProfile(Selected.Model);
        if (edited == null) return;

        var idx = _settings.Profiles.IndexOf(Selected.Model);
        var vm = new ProfileViewModel(edited);
        _settings.Profiles[idx] = edited;
        Profiles[idx] = vm;
        Selected = vm;
        _store.Save(_settings);
        _ = CheckAsync(vm);
    }

    private void RemoveProfile()
    {
        if (Selected == null || !_dialogs.Confirm($"Remove '{Selected.Name}' from the launcher?\n\nOnly the launcher entry is removed. The Perforce workspace and your files are not touched.")) return;
        var vm = Selected;
        _settings.Profiles.Remove(vm.Model);
        Profiles.Remove(vm);
        Selected = Profiles.FirstOrDefault();
        _store.Save(_settings);
    }

    // ---------- actions ----------

    private void RebuildActions()
    {
        Actions.Clear();
        SecondaryActions.Clear();
        PrimaryAction = null;
        if (Selected == null) return;
        var profile = Selected.Model;
        foreach (var a in _actions.GetActions(profile))
        {
            var action = a;
            var vm = new ActionViewModel(action,
                new AsyncRelayCommand(_ => RunActionAsync(action, profile), _ => action.CanExecute(profile)), profile);
            Actions.Add(vm);
            if (vm.IsPrimary) PrimaryAction = vm;
            else if (action.Id != "folder") SecondaryActions.Add(vm);   // «Show in Explorer» живёт в меню проекта
        }
    }

    private async Task CheckAsync(ProfileViewModel? vm)
    {
        if (vm == null) return;
        vm.IsChecking = true;
        try
        {
            var r = await _watcher.CheckNowAsync(vm.Model);
            // Событие Checked уже применило результат; для AuthRequired показываем подсказку один раз.
            if (r.AuthRequired && vm == Selected) AppendOutput($"{vm.Name}: Perforce session expired. Press Sign In.");
        }
        finally { vm.IsChecking = false; }
    }

    private static bool EditorRunning() =>
        Process.GetProcessesByName("UnrealEditor").Length > 0 || Process.GetProcessesByName("UE4Editor").Length > 0;

    // Изменения этих файлов требуют перегенерации .sln / project files.
    private static readonly System.Text.RegularExpressions.Regex BuildFileRx =
        new(@"[\\/](?<name>[^\\/#]+\.(uproject|uplugin|Build\.cs|Target\.cs))#\d+ - (added|updating|deleted|replacing)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private async Task SyncAsync()
    {
        var vm = Selected!;
        if (EditorRunning() && !_dialogs.Confirm("Unreal Editor is running. Getting latest may fail on files the editor has locked.\n\nContinue?")) return;

        var changedBuildFiles = new List<string>();
        await RunBusyAsync("Getting latest…", cancellable: true, async ct =>
        {
            Output = "";
            AppendOutput($"> p4 sync ({vm.Subtitle})");
            var progress = new Progress<string>(line =>
            {
                AppendOutput(line);
                if (BuildFileRx.Match(line) is { Success: true } m) changedBuildFiles.Add(m.Groups["name"].Value);
            });
            await _vcs.SyncAsync(vm.Model, progress, ct);
            AppendOutput("Get latest finished.");
        });
        _watcher.ResetNotified(vm.Model.Id);
        await CheckAsync(vm);

        if (changedBuildFiles.Count > 0 && vm == Selected)
        {
            var names = string.Join(", ", changedBuildFiles.Distinct().Take(3)) + (changedBuildFiles.Distinct().Count() > 3 ? "…" : "");
            ShowHint($"sync:{DateTime.Now.Ticks}", $"Build files changed ({names}). Regenerate project files so your IDE picks them up.");
        }
    }

    private async Task SyncToChangeAsync(HistoryEntry? entry)
    {
        var vm = Selected;
        if (entry == null || vm == null) return;
        var ok = _dialogs.Confirm(
            $"Sync the workspace to CL {entry.Id}?{Environment.NewLine}{Environment.NewLine}" +
            $"\"{entry.FirstLine}\" by {entry.User}{Environment.NewLine}{Environment.NewLine}" +
            "Files will match the depot as of this changelist. Newer changes leave your workspace until you press Get Latest. " +
            "Files you have opened are not touched.");
        if (!ok) return;
        if (EditorRunning() && !_dialogs.Confirm("Unreal Editor is running and may lock files. Continue?")) return;

        await RunBusyAsync($"Syncing to CL {entry.Id}…", cancellable: true, async ct =>
        {
            Output = "";
            AppendOutput($"> p4 sync @{entry.Id} ({vm.Subtitle})");
            await _vcs.SyncToChangeAsync(vm.Model, entry.Id, new Progress<string>(AppendOutput), ct);
            AppendOutput($"Workspace is now at CL {entry.Id}.");
        });
        _watcher.ResetNotified(vm.Model.Id);
        await CheckAsync(vm);
    }

    /// <summary>Единая обёртка для долгих операций: индикатор, журнал, отмена (убивает процесс p4).</summary>
    private async Task RunBusyAsync(string text, bool cancellable, Func<CancellationToken, Task> body)
    {
        if (IsBusy) throw new InvalidOperationException($"Please wait: {BusyText.TrimEnd('…')} is still running.");
        _opCts = cancellable ? new CancellationTokenSource() : null;
        BusyText = text;
        CanCancel = cancellable;
        IsBusy = true;
        IsLogOpen = true;
        try { await body(_opCts?.Token ?? CancellationToken.None); }
        catch (OperationCanceledException) { AppendOutput("Cancelled."); }
        finally
        {
            IsBusy = false;
            CanCancel = false;
            BusyText = "";
            _opCts?.Dispose();
            _opCts = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ShowHint(string key, string text)
    {
        if (_dismissedHints.Contains(key)) return;
        _hintKey = key;
        HintText = text;
    }

    /// <summary>Постоянная подсказка: проект есть, а .sln нет (например, .sln не хранится в депо).</summary>
    private void UpdateHint()
    {
        var vm = Selected;
        if (vm == null || (HasHint && _hintKey.StartsWith("sync:"))) return;
        var up = ProfilePaths.UProject(vm.Model);
        var sln = ProfilePaths.Solution(vm.Model);
        if (up != null && System.IO.File.Exists(up) && (sln == null || !System.IO.File.Exists(sln)))
            ShowHint($"nosln:{vm.Model.Id}", "No solution file yet. Generate project files to open the code in your IDE.");
        else if (_hintKey.StartsWith("nosln:")) HintText = "";
    }

    private void RunHintAction()
    {
        var gen = Actions.FirstOrDefault(a => a.Action.Id == "genproj");
        HintText = "";
        if (gen?.Command is { } c && c.CanExecute(null)) c.Execute(null);
    }

    private void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { System.Windows.Clipboard.SetText(text); AppendOutput($"Copied: {text}"); }
        catch (Exception) { /* буфер занят другим приложением — не критично */ }
    }

    /// <summary>Запуск редактора проекта из меню трея.</summary>
    public Task LaunchEditorAsync(Guid profileId)
    {
        var vm = Profiles.FirstOrDefault(p => p.Model.Id == profileId);
        var editor = vm == null ? null : _actions.GetActions(vm.Model).FirstOrDefault(a => a.Id == "editor");
        if (vm == null || editor == null) return Task.CompletedTask;
        if (!editor.CanExecute(vm.Model)) { _dialogs.Error($"{vm.Name}: no .uproject found in the workspace."); return Task.CompletedTask; }
        return RunActionAsync(editor, vm.Model);
    }

    private async Task SwitchWorkspaceAsync()
    {
        var vm = Selected!;
        var ws = await _dialogs.PickWorkspaceAsync(vm.Model);
        if (ws == null || ws.Name == vm.Model.P4Client) return;

        if (Process.GetProcessesByName("UnrealEditor").Length > 0 &&
            !_dialogs.Confirm("Unreal Editor is running. Switch workspace anyway?")) return;

        vm.Model.P4Client = ws.Name;
        vm.Model.WorkspaceRoot = ws.Root;
        if (_settings.ApplyP4SetOnSwitch) await _vcs.SetActiveWorkspaceAsync(vm.Model, ws.Name);

        _store.Save(_settings);
        _watcher.ResetNotified(vm.Model.Id);
        vm.ResetStatus();
        RebuildActions();
        await CheckAsync(vm);
    }

    private async Task LoginAsync()
    {
        var vm = Selected!;
        // «Запомнить» включено по умолчанию: ради этого обычно и открывают окно входа заранее.
        var input = _dialogs.PromptPassword($"Perforce password for {ConnectionText}", rememberDefault: _session != null);
        if (input == null) return;
        var (password, remember) = input.Value;

        await _vcs.LoginAsync(vm.Model, password);   // неверный пароль -> исключение, ничего не сохраняем
        if (_session != null)
        {
            if (remember)
            {
                await _session.SavePasswordAsync(vm.Model, password);
                AppendOutput("Signed in. Password saved in Windows Credential Manager: the session will be renewed automatically.");
            }
            else
            {
                await _session.ForgetAsync(vm.Model);
                AppendOutput("Signed in. Password not saved.");
            }
        }
        await CheckAsync(vm);
    }

    // ---------- pending changes: logic ----------

    private void ClearPending()
    {
        _changelists = Array.Empty<PendingChangelist>();
        PendingFiles.Clear();
        _headers.Clear();
        _selectedChangelistId = null;   // другой профиль — свои changelist'ы
        PendingError = "";
        _pendingLoadedAt = DateTime.MinValue;
        OnPropertyChanged(nameof(SubmitButtonText));
        OnPropertyChanged(nameof(PendingHeader));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(PendingCountText));
    }

    /// <summary>Вызывается при активации окна: обновляет pending не чаще раза в 10 секунд.</summary>
    public void RefreshPendingIfStale()
    {
        if (Selected != null && DateTime.UtcNow - _pendingLoadedAt > TimeSpan.FromSeconds(10))
            _ = RefreshPendingAsync();
    }

    public async Task RefreshPendingAsync()
    {
        var vm = Selected;
        if (vm == null) return;
        try
        {
            var list = await _vcs.GetPendingAsync(vm.Model);
            if (vm != Selected) return;   // за время запроса сменили профиль
            _changelists = list;
            PendingFiles.Clear();
            _headers.Clear();
            var root = vm.Model.WorkspaceRoot;
            foreach (var cl in list)
            {
                if (cl.Files.Count == 0 && cl.IsDefault) continue;   // пустой default не показываем
                var header = new ChangelistHeaderViewModel(cl);
                _headers.Add(header);
                if (cl.Files.Count == 0) PendingFiles.Add(new PendingFileViewModel(header, null, "(empty changelist)"));
                foreach (var f in cl.Files)
                    PendingFiles.Add(new PendingFileViewModel(header, f, ProfilePaths.ToRelative(root, f.LocalPath)));
            }
            // Выбранный changelist переживает обновление списка, если он ещё существует.
            if (_selectedChangelistId is { } sel && list.All(c => c.Id != sel)) _selectedChangelistId = null;
            PendingError = "";
            _pendingLoadedAt = DateTime.UtcNow;
            UpdateChangelistSelection();
        }
        catch (Exception ex)
        {
            if (vm != Selected) return;
            PendingError = ex.Message;
        }
        OnPropertyChanged(nameof(PendingHeader));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(PendingCountText));
        OnPropertyChanged(nameof(Changelists));
    }

    private List<OpenedFile> SelectedFilesOnly() =>
        SelectedPending.Where(f => f.File != null).Select(f => f.File!).ToList();

    /// <summary>
    /// Changelist, на котором работают Submit/Edit/Delete: выделенные файлы (одного CL) → выбранный заголовок →
    /// единственный непустой changelist.
    /// </summary>
    private PendingChangelist? ResolveTargetChangelist()
    {
        if (SelectedPending.Count > 0)
        {
            var ids = SelectedPending.Select(f => f.ChangelistId).Distinct().ToList();
            return ids.Count == 1 ? _changelists.FirstOrDefault(c => c.Id == ids[0]) : null;
        }
        if (_selectedChangelistId is { } id && _changelists.FirstOrDefault(c => c.Id == id) is { } chosen) return chosen;
        var nonEmpty = _changelists.Where(c => c.Files.Count > 0).ToList();
        return nonEmpty.Count == 1 ? nonEmpty[0] : null;
    }

    private PendingChangelist? ChangelistOrTarget(long? id) =>
        id is { } explicitId ? _changelists.FirstOrDefault(c => c.Id == explicitId) : ResolveTargetChangelist();

    /// <summary>Revert всех файлов changelist'а (из меню заголовка).</summary>
    private async Task RevertChangelistAsync(long id)
    {
        var cl = _changelists.FirstOrDefault(c => c.Id == id);
        if (cl == null || cl.Files.Count == 0) return;
        await RevertFilesAsync(cl.Files.ToList(), $" in {(cl.IsDefault ? "the default changelist" : cl.Title)}");
    }

    private async Task ReconcileAsync()
    {
        var vm = Selected!;
        await RunBusyAsync("Reconciling offline work…", cancellable: true, async ct =>
        {
            Output = "";
            AppendOutput($"> p4 reconcile ({vm.Subtitle})");
            await _vcs.ReconcileAsync(vm.Model, new Progress<string>(AppendOutput), ct);
            AppendOutput("Reconcile finished.");
        });
        await RefreshPendingAsync();

        var ignored = _changelists.SelectMany(c => c.Files).Where(f => f.IsIgnored).ToList();
        if (ignored.Count > 0)
            AppendOutput($"WARNING: {ignored.Count} opened file(s) match .p4ignore but are tracked in the depot " +
                         $"(marked ⚠): {string.Join(", ", ignored.Take(5).Select(f => System.IO.Path.GetFileName(f.LocalPath)))}" +
                         (ignored.Count > 5 ? ", ..." : ""));
    }

    private async Task NewChangelistAsync()
    {
        var desc = _dialogs.PromptText("New changelist", "Description:", "", "Create");
        if (string.IsNullOrWhiteSpace(desc)) return;
        var id = await _vcs.CreateChangelistAsync(Selected!.Model, desc.Trim());
        AppendOutput($"Created CL {id}.");
        await RefreshPendingAsync();
    }

    /// <param name="parameter">Id целевого changelist'а (0 = default) или -1 для «новый».</param>
    private async Task MoveToAsync(object? parameter)
    {
        var files = SelectedFilesOnly();
        if (files.Count == 0) return;
        var target = Convert.ToInt64(parameter);
        var profile = Selected!.Model;
        if (target < 0)
        {
            var desc = _dialogs.PromptText("New changelist", $"Description (for {files.Count} moved file(s)):", "", "Create and move");
            if (string.IsNullOrWhiteSpace(desc)) return;
            target = await _vcs.CreateChangelistAsync(profile, desc.Trim());
        }
        await _vcs.MoveFilesAsync(profile, target, files.Select(f => f.DepotPath).ToList());
        AppendOutput($"Moved {files.Count} file(s) to {(target == 0 ? "default changelist" : "CL " + target)}.");
        await RefreshPendingAsync();
    }

    private Task RevertAsync() => RevertFilesAsync(SelectedFilesOnly(), "");

    private async Task RevertFilesAsync(List<OpenedFile> files, string where)
    {
        if (files.Count == 0) return;
        var sample = string.Join(Environment.NewLine, files.Take(8).Select(f => "  " + System.IO.Path.GetFileName(f.LocalPath)));
        var more = files.Count > 8 ? $"{Environment.NewLine}  ...and {files.Count - 8} more" : "";
        if (!_dialogs.Confirm($"Revert {files.Count} file(s){where}? Local modifications will be LOST.{Environment.NewLine}{Environment.NewLine}{sample}{more}")) return;

        await _vcs.RevertAsync(Selected!.Model, files.Select(f => f.DepotPath).ToList(), new Progress<string>(AppendOutput));
        await RefreshPendingAsync();
    }

    private async Task EditDescriptionAsync(long? id = null)
    {
        var cl = ChangelistOrTarget(id);
        if (cl == null || cl.IsDefault) return;
        var desc = _dialogs.PromptText($"Edit {cl.Title}", "Description:", cl.Description, "Save");
        if (string.IsNullOrWhiteSpace(desc)) return;
        await _vcs.UpdateChangelistDescriptionAsync(Selected!.Model, cl.Id, desc.Trim());
        await RefreshPendingAsync();
    }

    private async Task DeleteChangelistAsync(long? id = null)
    {
        var cl = ChangelistOrTarget(id);
        if (cl == null || cl.IsDefault || cl.Files.Count > 0) return;
        if (!_dialogs.Confirm($"Delete empty {cl.Title}?")) return;
        await _vcs.DeleteChangelistAsync(Selected!.Model, cl.Id);
        await RefreshPendingAsync();
    }

    private async Task SubmitAsync(long? changelistId = null)
    {
        var vm = Selected!;
        if (IsBusy) throw new InvalidOperationException($"Please wait: {BusyText.TrimEnd('…')} is still running.");
        var targetId = changelistId ?? ResolveTargetChangelist()?.Id;
        if (targetId != null) SelectChangelist(targetId.Value);   // видно, какой changelist отправляется
        if (targetId == null) return;

        // Состав changelist'а и пометки P4IGNORE берём свежими: список мог устареть с прошлого обновления.
        await RefreshPendingAsync();
        var cl = _changelists.FirstOrDefault(c => c.Id == targetId);
        if (cl == null) { _dialogs.Error("The changelist no longer exists."); return; }
        if (cl.Files.Count == 0) { _dialogs.Error($"{cl.Title} has no files to submit."); return; }

        // Файлы под P4IGNORE, но уже в депо: коммит изменит их для всей команды. Спрашиваем явно (по умолчанию «No»).
        var ignored = cl.Files.Where(f => f.IsIgnored).ToList();
        if (ignored.Count > 0)
        {
            var list = string.Join(Environment.NewLine, ignored.Take(8).Select(f =>
                $"  {ProfilePaths.ToRelative(vm.Model.WorkspaceRoot, f.LocalPath)}   [{f.Action}, rule {f.IgnoredBy}]"));
            var more = ignored.Count > 8 ? $"{Environment.NewLine}  ...and {ignored.Count - 8} more" : "";
            var proceed = _dialogs.Confirm(
                $"{ignored.Count} of {cl.Files.Count} file(s) match .p4ignore but are already tracked in the depot:{Environment.NewLine}{Environment.NewLine}" +
                $"{list}{more}{Environment.NewLine}{Environment.NewLine}" +
                "These are usually generated or local files (e.g. .sln). Submitting changes them for the whole team." +
                $"{Environment.NewLine}Tip: cancel, then revert them or move them to another changelist ('Move to').{Environment.NewLine}{Environment.NewLine}" +
                "Submit anyway?");
            if (!proceed) return;
        }

        var caption = $"Submit {cl.Files.Count} file(s) from {cl.Title} to {vm.Subtitle}. Description:";
        var desc = _dialogs.PromptText("Submit", caption, cl.Description, "Submit");
        if (string.IsNullOrWhiteSpace(desc)) return;
        desc = desc.Trim();

        Output = "";
        var profile = vm.Model;
        var id = cl.Id;
        if (cl.IsDefault)
        {
            // p4 submit работает только с нумерованным changelist'ом: создаём его и переносим файлы из default.
            id = await _vcs.CreateChangelistAsync(profile, desc);
            await _vcs.MoveFilesAsync(profile, id, cl.Files.Select(f => f.DepotPath).ToList());
            AppendOutput($"Created CL {id} from default changelist.");
        }
        else if (desc != cl.Description)
        {
            await _vcs.UpdateChangelistDescriptionAsync(profile, id, desc);
        }

        AppendOutput($"> p4 submit -c {id}");
        try
        {
            // Submit не отменяем: обрыв посреди отправки оставил бы changelist в неясном состоянии.
            await RunBusyAsync($"Submitting CL {id}…", cancellable: false,
                _ => _vcs.SubmitAsync(profile, id, new Progress<string>(AppendOutput)));
            AppendOutput("Submit finished.");
            _notifier.Notify("Submitted", $"{cl.Files.Count} file(s) submitted in CL {id}.", profile.Id);
        }
        finally
        {
            // Даже при ошибке файлы остались в нумерованном CL — обновляем список и статус.
            _watcher.ResetNotified(profile.Id);
            await RefreshPendingAsync();
            await CheckAsync(vm);
        }
    }

    // ---------- history: logic ----------

    /// <summary>Вызывается из code-behind при переключении вкладок; история грузится лениво при первом открытии.</summary>
    public void SetHistoryTabActive(bool active)
    {
        _historyTabActive = active;
        if (active) EnsureHistoryLoaded();
    }

    private void EnsureHistoryLoaded()
    {
        if (Selected != null && !_historyLoaded && !_historyBusy) _ = LoadHistoryAsync(reset: true);
    }

    private void ClearHistory()
    {
        _history.Clear();
        HistoryFiles.Clear();
        _selectedHistory = null;
        OnPropertyChanged(nameof(SelectedHistory));
        HistoryDescription = "";
        HistoryFilesInfo = "";
        HistoryError = "";
        HistoryHasMore = false;
        _historyLoaded = false;
    }

    private async Task LoadHistoryAsync(bool reset)
    {
        var vm = Selected;
        if (vm == null || _historyBusy) return;
        _historyBusy = true;
        try
        {
            long? before = reset || _history.Count == 0 ? null : _history[^1].Id;   // список идёт от новых к старым
            var page = await _vcs.GetHistoryAsync(vm.Model, HistoryPageSize, before);
            if (vm != Selected) return;   // за время запроса сменили профиль
            if (reset) ClearHistory();
            foreach (var c in page) _history.Add(new HistoryEntry(c, vm.HaveChange));
            HistoryHasMore = page.Count >= HistoryPageSize;
            HistoryError = "";
            _historyLoaded = true;
        }
        catch (Exception ex)
        {
            if (vm == Selected) HistoryError = ex.Message;
        }
        finally { _historyBusy = false; }
    }

    private async Task LoadHistoryDetailsAsync(HistoryEntry? entry)
    {
        HistoryFiles.Clear();
        var vm = Selected;
        if (entry == null || vm == null)
        {
            HistoryDescription = "";
            HistoryFilesInfo = "";
            return;
        }
        HistoryDescription = $"CL {entry.Id} by {entry.User}, {entry.Time:dd.MM.yyyy HH:mm}{Environment.NewLine}{Environment.NewLine}{entry.Change.Description.Trim()}";
        HistoryFilesInfo = "Loading files...";
        try
        {
            var files = await _vcs.GetChangeFilesAsync(vm.Model, entry.Id);
            if (SelectedHistory != entry) return;   // успели выбрать другой коммит
            foreach (var f in files.Take(MaxHistoryFiles)) HistoryFiles.Add(f);
            HistoryFilesInfo = files.Count > MaxHistoryFiles
                ? $"{files.Count} files (showing first {MaxHistoryFiles})"
                : $"{files.Count} file(s)";
        }
        catch (Exception ex)
        {
            if (SelectedHistory == entry) HistoryFilesInfo = "Error: " + ex.Message;
        }
    }

    // ---------- custom actions ----------

    /// <summary>Пользовательские команды из профиля запускаются с правами пользователя, поэтому перед первым
    /// запуском за сессию показываем, что именно будет выполнено (защита от подменённого settings.json).</summary>
    private Task RunActionAsync(ILaunchAction action, ProjectProfile profile)
    {
        if (action is CustomCommandAction custom && !_approvedCustom.Contains(custom.CommandLine))
        {
            var ok = _dialogs.Confirm(
                $"Run custom command from this profile?{Environment.NewLine}{Environment.NewLine}" +
                $"{custom.DisplayName}{Environment.NewLine}{custom.CommandLine}{Environment.NewLine}{Environment.NewLine}" +
                "It runs with your user rights. Continue only if you configured this command yourself.");
            if (!ok) return Task.CompletedTask;
            _approvedCustom.Add(custom.CommandLine);
        }
        return action.ExecuteAsync(profile);
    }

    // ---------- events ----------

    private ProfileViewModel? Find(ProjectProfile p) => Profiles.FirstOrDefault(x => x.Model.Id == p.Id);

    // ---------- reminders: startup / wake from sleep ----------

    private bool _batchNotify = true;   // на старте отдельные уведомления копятся в одно общее
    private bool _reminding;
    private DateTime _lastResume = DateTime.MinValue;

    /// <summary>Включает/выключает запись автозапуска в HKCU\...\Run согласно настройке.</summary>
    public void ApplyAutostart()
    {
        try { AutostartManager.Apply(_settings.StartWithWindows, _settings.StartMinimized); }
        catch (Exception ex) { _dialogs.Error("Could not update the Windows startup entry: " + ex.Message); }
    }

    /// <summary>Вызывается при старте приложения (в том числе при автозапуске вместе с Windows).</summary>
    public Task StartupReminderAsync() => RemindAboutUpdatesAsync(resetNotified: false);

    /// <summary>Выход компьютера из сна: напомнить о коммитах, даже если об этих уже уведомляли.</summary>
    public void OnSystemResume()
    {
        if (DateTime.UtcNow - _lastResume < TimeSpan.FromSeconds(60)) return;   // Resume приходит по нескольку раз
        _lastResume = DateTime.UtcNow;
        _ = RemindAboutUpdatesAsync(resetNotified: true);
    }

    private async Task RemindAboutUpdatesAsync(bool resetNotified)
    {
        if (!_settings.RemindOnWake)
        {
            _batchNotify = false;   // напоминания выключены: работают обычные уведомления по каждому проекту
            return;
        }
        if (_reminding) return;
        _reminding = true;
        _batchNotify = true;
        try
        {
            if (resetNotified) _watcher.ResetAllNotified();
            // После пробуждения или входа сеть/VPN поднимаются не сразу: повторяем, пока есть сетевые ошибки.
            for (var attempt = 0; attempt < 6; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(15));
                await CheckAllAsync();
                if (!Profiles.Any(p => p.HasTransientError)) break;
            }
        }
        catch (Exception) { /* напоминание вторично: ошибки проверки уже видны в статусе профилей */ }
        finally { _batchNotify = false; _reminding = false; }

        var behind = Profiles.Where(p => p.IsBehind && p.Model.NotificationsEnabled).ToList();
        if (behind.Count > 0)
        {
            string Line(ProfileViewModel p)
            {
                var top = p.NewChanges.FirstOrDefault();
                return $"{p.Name}: {p.BehindText} new" + (top != null ? $" (latest CL {top.Id} by {top.User}: {top.FirstLine})" : "");
            }
            var title = behind.Count == 1 ? $"{behind[0].Name}: updates available" : $"Perforce: updates in {behind.Count} projects";
            var message = string.Join("\n", behind.Take(3).Select(Line)) + (behind.Count > 3 ? "\n..." : "");
            _notifier.Notify(title, message, behind[0].Model.Id);
            return;
        }

        // Тикет мог истечь за время сна: без этого напоминание молча не сработало бы.
        var needLogin = Profiles.Where(p => p.AuthRequired).ToList();
        if (needLogin.Count > 0)
            _notifier.Notify("Perforce login required",
                $"Cannot check for commits: {string.Join(", ", needLogin.Take(3).Select(p => p.Name))}. Open the launcher and press Login.",
                needLogin[0].Model.Id);
    }

    private void OnNewChanges(object? sender, NewChangesEventArgs e)
    {
        if (_batchNotify) return;   // покроет общее уведомление после проверки
        var top = e.Status.NewChanges.FirstOrDefault();
        var msg = $"{e.Status.NewChanges.Count}{(e.Status.HasMoreChanges ? "+" : "")} new commit(s)" +
                  (top != null ? $"\nLatest: CL {top.Id} by {top.User}: {top.FirstLine}" : "");
        _notifier.Notify($"{e.Profile.Name}: updates available", msg, e.Profile.Id);
    }

    private void AppendOutput(string line)
    {
        var text = _output + line + Environment.NewLine;
        if (text.Length > 20000) text = text[^15000..];
        Output = text;
        if (!string.IsNullOrWhiteSpace(line)) LastOutputLine = line.Trim();
    }
}
