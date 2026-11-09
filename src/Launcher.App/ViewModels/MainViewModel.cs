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
    /// <summary>Выбор из нескольких кнопок (первая — основная, последняя — отмена). Индекс кнопки или -1.</summary>
    int Choose(string title, string message, params string[] buttons);
    void Error(string message);
    /// <summary>Многострочный ввод текста (описание changelist'а). null = отмена.
    /// requireEnglish: русский текст переводится на английский перед закрытием (описание коммита).</summary>
    string? PromptText(string title, string caption, string initial, string okText, bool requireEnglish = false);
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
    private string _hintActionText = "";
    private Action? _hintAction;
    private readonly HashSet<string> _dismissedHints = new();
    public string HintText { get => _hintText; private set { if (Set(ref _hintText, value)) OnPropertyChanged(nameof(HasHint)); } }
    public bool HasHint => _hintText.Length > 0;
    /// <summary>Подпись кнопки в подсказке («Build Project», «Generate Project Files»).</summary>
    public string HintActionText { get => _hintActionText; private set => Set(ref _hintActionText, value); }
    /// <summary>Подсказка «нужно собрать» — отдельный акцент в баннере.</summary>
    public bool HintIsBuild => _hintKey.StartsWith("build:");

    // Подсказка после Get Latest «изменились build-файлы» живёт, пока её не закрыли или не выполнили (по профилю).
    private (Guid ProfileId, string Key, string Text)? _syncHint;

    // ---------- состояние сборки C++ (нужно ли пересобрать после синка) ----------
    private readonly ProjectBuilder _builder = new(new EngineLocator());
    // Когда лаунчер синкнул код в профиль: страховка для воркспейсов с опцией modtime, где время файлов — из депо.
    private readonly Dictionary<Guid, DateTime> _codeSyncedAt = new();
    private BuildState _buildState = BuildState.NoCode;
    private int _buildStateVersion;
    private DateTime _buildStateCheckedAt = DateTime.MinValue;

    public BuildState BuildState
    {
        get => _buildState;
        private set
        {
            if (!Set(ref _buildState, value)) return;
            OnPropertyChanged(nameof(NeedsBuild));
            OnPropertyChanged(nameof(CanBuild));
            OnPropertyChanged(nameof(BuildToolTip));
        }
    }
    public bool NeedsBuild => _buildState.NeedsBuild;
    /// <summary>Кнопка «Build Project» есть только у проектов с C++ кодом.</summary>
    public bool CanBuild => _buildState.HasCode;
    public string BuildToolTip => _buildState.NeedsBuild
        ? $"Needs a build: {_buildState.Summary}. Until you build, Unreal runs the old code."
        : "Compile the project's C++ code (incremental, like Build in your IDE). Code is up to date.";

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
    public ICommand BuildProjectCommand { get; }
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
            if (_rebuildingPending) return;   // список пересобирается: выделение восстановит RefreshPendingAsync
            _selectedPending = value ?? Array.Empty<PendingFileViewModel>();
            // Выделили файлы одного changelist'а — он и становится выбранным (и остаётся им после снятия выделения).
            var ids = _selectedPending.Select(f => f.ChangelistId).Distinct().ToList();
            if (ids.Count == 1) _selectedChangelistId = ids[0];
            UpdateChangelistSelection();
        }
    }
    private IReadOnlyList<PendingFileViewModel> _selectedPending = Array.Empty<PendingFileViewModel>();
    private bool _rebuildingPending;
    /// <summary>Список Pending пересобран: окно заново выделяет эти строки (те же файлы, но новые объекты).</summary>
    public event Action<IReadOnlyList<PendingFileViewModel>>? PendingSelectionRestored;

    /// <summary>Что переносит Move To: выделенные файлы, а если их нет, все файлы выбранного changelist'а.</summary>
    public IReadOnlyList<OpenedFile> MoveSource()
    {
        var files = SelectedFilesOnly();
        return files.Count > 0 ? files : ResolveTargetChangelist()?.Files ?? Array.Empty<OpenedFile>();
    }
    public bool CanMoveFiles => MoveSource().Count > 0;

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
        OnPropertyChanged(nameof(CanMoveFiles));
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
        RefreshCheckoutsCommand = new AsyncRelayCommand(_ => LoadCheckoutsAsync(), _ => Selected != null && !CheckoutBusy);
        ExpandAllCheckoutsCommand = new RelayCommand(_ => SetAllCheckoutsExpanded(true));
        CollapseAllCheckoutsCommand = new RelayCommand(_ => SetAllCheckoutsExpanded(false));
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
                _ = UpdateBuildStateAsync();   // код мог прийти синком из P4V/Unreal
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
        MoveToCommand = new AsyncRelayCommand(MoveToAsync, _ => CanMoveFiles);
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
        HintCommand = new RelayCommand(_ => RunHintAction(), _ => !IsBusy);
        DismissHintCommand = new RelayCommand(_ => { _dismissedHints.Add(_hintKey); UpdateHint(); });
        BuildProjectCommand = new AsyncRelayCommand(_ => BuildProjectAsync(Selected!.Model), _ => Selected != null && CanBuild && !IsBusy);
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
            ClearCheckouts();
            _checkoutExpanded.Clear();
            if (_historyTabActive) EnsureHistoryLoaded();
            if (_checkoutTabActive) EnsureCheckoutsFresh();
            OnPropertyChanged(nameof(ConnectionText));
            if (value != null) _ = LoadConnectionAsync(value);
            BuildState = BuildState.NoCode;
            UpdateHint();
            _ = UpdateBuildStateAsync(force: true);
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

    // Синк принёс C++ код (в том числе удаление файлов — его по времени изменения не увидеть).
    private static readonly System.Text.RegularExpressions.Regex CodeFileRx =
        new(@"\.(cpp|h|hpp|inl|c|cc|cs|ispc)#\d+ - (added|updating|deleted|replacing)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private async Task SyncAsync()
    {
        var vm = Selected!;
        if (EditorRunning() && !_dialogs.Confirm("Unreal Editor is running. Getting latest may fail on files the editor has locked.\n\nContinue?")) return;

        var changedBuildFiles = new List<string>();
        IReadOnlyList<string> writable = Array.Empty<string>();
        await RunBusyAsync("Getting latest…", cancellable: true, async ct =>
        {
            Output = "";
            AppendOutput($"> p4 sync ({vm.Subtitle})");
            var progress = new Progress<string>(line =>
            {
                AppendOutput(line);
                if (BuildFileRx.Match(line) is { Success: true } m) changedBuildFiles.Add(m.Groups["name"].Value);
                if (CodeFileRx.IsMatch(line)) _codeSyncedAt[vm.Model.Id] = DateTime.Now;
            });
            var result = await _vcs.SyncAsync(vm.Model, progress, ct);
            writable = result.WritableNotUpdated;
            AppendOutput(writable.Count == 0 ? "Get latest finished." : $"Get latest finished, {writable.Count} writable file(s) not updated.");
        });
        if (writable.Count > 0) await ResolveWritableFilesAsync(vm, writable, changeId: null);
        _watcher.ResetNotified(vm.Model.Id);

        if (changedBuildFiles.Count > 0)
        {
            var names = string.Join(", ", changedBuildFiles.Distinct().Take(3)) + (changedBuildFiles.Distinct().Count() > 3 ? "…" : "");
            _syncHint = (vm.Model.Id, $"sync:{DateTime.Now.Ticks}", $"Build files changed ({names}). Regenerate project files so your IDE picks them up.");
        }
        await CheckAsync(vm);
        await UpdateBuildStateAsync(force: true);
        if (vm == Selected && NeedsBuild) AppendOutput($"C++ code changed: {BuildState.Summary}. Press Build Project so Unreal runs the new code.");
    }

    /// <summary>
    /// p4 не перезаписал файлы, доступные на запись без checkout (обычно Unreal сохранил их через «Make Writable»).
    /// Спрашиваем: взять версию с сервера (локальные копии сначала уходят в резервную папку) или оставить свои.
    /// </summary>
    private async Task ResolveWritableFilesAsync(ProfileViewModel vm, IReadOnlyList<string> files, long? changeId)
    {
        var root = vm.Model.WorkspaceRoot;
        var list = string.Join(Environment.NewLine, files.Take(8).Select(f => "  " + ProfilePaths.ToRelative(root, f)));
        var more = files.Count > 8 ? $"{Environment.NewLine}  …and {files.Count - 8} more" : "";
        var choice = _dialogs.Choose(
            files.Count == 1 ? "1 file wasn't updated" : $"{files.Count} files weren't updated",
            "Perforce didn't overwrite these files: they are writable on your disk but not checked out " +
            "(usually Unreal saved them with \"Make Writable\"). Everything else was synced." +
            $"{Environment.NewLine}{Environment.NewLine}{list}{more}{Environment.NewLine}{Environment.NewLine}" +
            "Get Server Version: replace them with the depot version. Your copies are backed up first." +
            $"{Environment.NewLine}Keep Mine: leave them as they are (they stay out of date; check them out if you meant to change them).",
            "Get Server Version", "Keep Mine");
        if (choice != 0)
        {
            AppendOutput($"Kept {files.Count} local writable file(s); they are not updated.");
            return;
        }

        await RunBusyAsync("Replacing writable files…", cancellable: false, async ct =>
        {
            var backup = BackupFiles(vm, files);
            AppendOutput($"Local copies backed up to {backup}");
            await _vcs.ForceSyncFilesAsync(vm.Model, files, changeId, new Progress<string>(AppendOutput), ct);
            AppendOutput($"{files.Count} file(s) replaced with the depot version.");
        });
    }

    /// <summary>Копии файлов в %LocalAppData%\PerforceLauncher\Backups\Проект\время\относительный_путь (вне воркспейса, чтобы не попасть в reconcile).</summary>
    private static string BackupFiles(ProfileViewModel vm, IReadOnlyList<string> files)
    {
        var safeName = string.Concat(vm.Name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PerforceLauncher", "Backups", safeName, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        foreach (var f in files)
        {
            if (!System.IO.File.Exists(f)) continue;
            var rel = ProfilePaths.ToRelative(vm.Model.WorkspaceRoot, f);
            if (System.IO.Path.IsPathRooted(rel)) rel = rel.Replace(":", "");   // файл вне корня воркспейса
            var target = System.IO.Path.Combine(dir, rel.TrimStart('\\', '/'));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            System.IO.File.Copy(f, target, overwrite: true);
        }
        return dir;
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

        IReadOnlyList<string> writable = Array.Empty<string>();
        await RunBusyAsync($"Syncing to CL {entry.Id}…", cancellable: true, async ct =>
        {
            Output = "";
            AppendOutput($"> p4 sync @{entry.Id} ({vm.Subtitle})");
            var result = await _vcs.SyncToChangeAsync(vm.Model, entry.Id, new Progress<string>(line =>
            {
                AppendOutput(line);
                if (CodeFileRx.IsMatch(line)) _codeSyncedAt[vm.Model.Id] = DateTime.Now;
            }), ct);
            writable = result.WritableNotUpdated;
            AppendOutput($"Workspace is now at CL {entry.Id}" + (writable.Count > 0 ? $", except {writable.Count} writable file(s)." : "."));
        });
        if (writable.Count > 0) await ResolveWritableFilesAsync(vm, writable, entry.Id);
        _watcher.ResetNotified(vm.Model.Id);
        await CheckAsync(vm);
        await UpdateBuildStateAsync(force: true);
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

    /// <summary>
    /// Одна подсказка под статусом, по приоритету: «нужно собрать C++» → «изменились build-файлы» → «нет .sln».
    /// Закрытая подсказка не возвращается, пока не изменится её содержание (ключ).
    /// </summary>
    private void UpdateHint()
    {
        var vm = Selected;
        var candidates = new List<(string Key, string Text, string ActionText, Action Run)>();
        if (vm != null)
        {
            var id = vm.Model.Id;
            if (_buildState.NeedsBuild)
                candidates.Add(($"build:{id}:{_buildState.Summary}",
                    $"C++ code changed since your last build: {_buildState.Summary}. Unreal will run the old code until you build.",
                    "Build Project", () => BuildProjectCommand.Execute(null)));
            if (_syncHint is { } sh && sh.ProfileId == id)
                candidates.Add((sh.Key, sh.Text, "Generate Project Files", RunGenerateProjectFiles));
            var up = ProfilePaths.UProject(vm.Model);
            var sln = ProfilePaths.Solution(vm.Model);
            if (up != null && System.IO.File.Exists(up) && (sln == null || !System.IO.File.Exists(sln)))
                candidates.Add(($"nosln:{id}", "No solution file yet. Generate project files to open the code in your IDE.",
                    "Generate Project Files", RunGenerateProjectFiles));
        }

        var pick = candidates.FirstOrDefault(c => !_dismissedHints.Contains(c.Key));
        _hintKey = pick.Key ?? "";
        _hintAction = pick.Run;
        HintActionText = pick.ActionText ?? "";
        HintText = pick.Text ?? "";
        OnPropertyChanged(nameof(HintIsBuild));
    }

    private void RunHintAction()
    {
        var run = _hintAction;
        // «build» не скрываем: если сборка упала, подсказка должна остаться; исчезнет сама, когда код соберётся.
        if (_hintKey.StartsWith("sync:")) _syncHint = null;
        else if (_hintKey.StartsWith("nosln:")) _dismissedHints.Add(_hintKey);   // .sln появится через несколько секунд
        UpdateHint();
        run?.Invoke();
    }

    private void RunGenerateProjectFiles()
    {
        var gen = Actions.FirstOrDefault(a => a.Action.Id == "genproj");
        if (gen?.Command is { } c && c.CanExecute(null)) c.Execute(null);
    }

    // ---------- сборка C++ ----------

    /// <summary>Пересчитать «нужна ли сборка» для выбранного профиля (обход Source — в фоне).</summary>
    public async Task UpdateBuildStateAsync(bool force = false)
    {
        var vm = Selected;
        if (vm == null) return;
        if (!force && DateTime.UtcNow - _buildStateCheckedAt < TimeSpan.FromSeconds(5)) return;
        _buildStateCheckedAt = DateTime.UtcNow;
        var version = ++_buildStateVersion;
        var state = await CheckBuildStateAsync(vm.Model);
        if (version != _buildStateVersion || vm != Selected) return;   // за это время выбрали другой проект
        BuildState = state;
        UpdateHint();
        CommandManager.InvalidateRequerySuggested();
    }

    private Task<BuildState> CheckBuildStateAsync(ProjectProfile p)
    {
        var up = ProfilePaths.UProject(p);
        DateTime? synced = _codeSyncedAt.TryGetValue(p.Id, out var t) ? t : null;
        if (up == null) return Task.FromResult(BuildState.NoCode);
        return Task.Run(() =>
        {
            try { return BuildStateChecker.Check(up, synced); }
            catch (Exception) { return BuildState.NoCode; }   // проверка — подсказка, а не блокер
        });
    }

    /// <summary>Окно стало активным (пользователь мог собрать проект в IDE) — обновить подсказку.</summary>
    public void OnWindowActivated()
    {
        RefreshPendingIfStale();
        if (_checkoutTabActive) EnsureCheckoutsFresh();
        if (!IsBusy) _ = UpdateBuildStateAsync();
    }

    /// <summary>Инкрементальная сборка editor-цели через Build.bat движка. true — собрано.</summary>
    private async Task<bool> BuildProjectAsync(ProjectProfile profile)
    {
        var name = Profiles.FirstOrDefault(p => p.Model.Id == profile.Id)?.Name ?? profile.Name;
        if (_builder.GetPlan(profile) is not { } plan)
        {
            _dialogs.Error($"{name}: can't build — the engine's Build.bat or the .uproject was not found. Check the engine in Project Settings.");
            return false;
        }
        if (EditorRunning() && !_dialogs.Confirm(
                "Unreal Editor is running. Close it before building: the build can't replace modules the editor has loaded, " +
                "and Live Coding blocks it.\n\nBuild anyway?"))
            return false;

        var ok = false;
        string? failure = null;
        await RunBusyAsync($"Building {name}…", cancellable: true, async ct =>
        {
            Output = "";
            AppendOutput($"> Build {plan.Target} Win64 Development ({System.IO.Path.GetFileName(plan.UProject)})");
            var lines = new Progress<string>(AppendOutput);
            var steps = new Progress<(int Done, int Total)>(s => BusyText = $"Building {name}… {s.Done}/{s.Total}");
            var sw = Stopwatch.StartNew();
            try
            {
                await _builder.BuildAsync(profile, ((IProgress<string>)lines).Report,
                    (n, m) => ((IProgress<(int, int)>)steps).Report((n, m)), ct);
                ok = true;
            }
            catch (InvalidOperationException ex) { failure = ex.Message; }
            catch (P4Exception ex) { failure = ex.Message; }   // cmd.exe не запустился
            await Task.Yield();   // дать Progress дописать последние строки до итоговой
            AppendOutput(ok ? $"Build succeeded in {sw.Elapsed:m\\:ss}." : failure ?? "Build failed.");
        });

        _buildStateCheckedAt = DateTime.MinValue;
        if (Selected?.Model.Id == profile.Id) await UpdateBuildStateAsync(force: true);
        if (ok)
        {
            if (!IsWindowShown()) _notifier.Notify($"{name}: build succeeded", "The project is compiled and ready to launch.", profile.Id);
        }
        else if (failure != null)
        {
            if (!IsWindowShown()) _notifier.Notify($"{name}: build failed", "Open the launcher to see the compiler output.", profile.Id);
            _dialogs.Error($"{name}: build failed.\n\nThe compiler output is in Activity (errors are near the end).");
        }
        return ok;
    }

    /// <summary>Перед запуском редактора: код новее сборки — предложить собрать. false — запуск отменён.</summary>
    private async Task<bool> OfferBuildBeforeLaunchAsync(ProjectProfile profile)
    {
        if (IsBusy || EditorRunning()) return true;   // уже что-то идёт / редактор открыт: не мешаем
        var state = await CheckBuildStateAsync(profile);
        if (Selected?.Model.Id == profile.Id) { BuildState = state; UpdateHint(); }
        if (!state.NeedsBuild) return true;

        var name = Profiles.FirstOrDefault(p => p.Model.Id == profile.Id)?.Name ?? profile.Name;
        var choice = _dialogs.Choose($"Build {name} first?",
            $"C++ code changed since your last build: {state.Summary}.\n\n" +
            "If you launch now, Unreal runs the old code — recent changes won't work and you may see bugs that are already fixed.",
            "Build and Launch", "Launch Anyway", "Cancel");
        return choice switch
        {
            0 => await BuildProjectAsync(profile),
            1 => true,
            _ => false,
        };
    }

    /// <summary>Задаётся окном: видно ли оно (иначе о результате сборки сообщаем уведомлением).</summary>
    public Func<bool> IsWindowShown { get; set; } = () => true;

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
            // Список обновляется и сам (например, при возврате в окно), поэтому выделенные файлы должны это пережить.
            var keep = _selectedPending.Where(f => f.File != null).Select(f => f.File!.DepotPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _rebuildingPending = true;
            try
            {
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
            }
            finally { _rebuildingPending = false; }
            _selectedPending = PendingFiles.Where(f => f.File != null && keep.Contains(f.File.DepotPath)).ToList();
            if (_selectedPending.Count > 0) PendingSelectionRestored?.Invoke(_selectedPending);
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
        var files = MoveSource();
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
        var desc = _dialogs.PromptText("Submit", caption, cl.Description, "Submit", requireEnglish: true);
        if (string.IsNullOrWhiteSpace(desc)) return;
        desc = desc.Trim();
        // Страховка: в депо коммиты только с английским описанием, даже если русский текст как-то прошёл мимо окна.
        if (Translator.HasCyrillic(desc))
        {
            try { desc = (await Translator.ToEnglishAsync(desc)).Trim(); }
            catch (InvalidOperationException ex)
            {
                _dialogs.Error($"The description is in Russian and couldn't be translated to English: {ex.Message}\n\nNothing was submitted.");
                return;
            }
            if (Translator.HasCyrillic(desc))
            {
                _dialogs.Error("Some words in the description couldn't be translated to English. Edit them and submit again.\n\nNothing was submitted.");
                return;
            }
        }

        Output = "";
        AppendOutput("Description: " + desc.Replace(Environment.NewLine, " / "));
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

    // ---------- checked out: кто что держит (p4 opened -a) ----------

    private IReadOnlyList<CheckedOutFile> _checkoutAll = Array.Empty<CheckedOutFile>();
    private readonly Dictionary<string, bool> _checkoutExpanded = new(StringComparer.OrdinalIgnoreCase);
    private string _checkoutMe = "";
    private string _checkoutFilter = "";
    private bool _checkoutOthersOnly;
    private bool _checkoutLockedOnly;
    private bool _checkoutBusy;
    private bool _checkoutLoaded;
    private bool _checkoutTabActive;
    private string _checkoutError = "";
    private string _checkoutSummary = "";
    private string _checkoutBadge = "";
    private DateTime _checkoutLoadedAt = DateTime.MinValue;

    public ObservableCollection<UserCheckoutViewModel> CheckoutGroups { get; } = new();
    public ICommand RefreshCheckoutsCommand { get; private set; } = null!;
    public ICommand ExpandAllCheckoutsCommand { get; private set; } = null!;
    public ICommand CollapseAllCheckoutsCommand { get; private set; } = null!;

    public string CheckoutFilter { get => _checkoutFilter; set { if (Set(ref _checkoutFilter, value)) ApplyCheckoutFilter(); } }
    public bool CheckoutOthersOnly { get => _checkoutOthersOnly; set { if (Set(ref _checkoutOthersOnly, value)) ApplyCheckoutFilter(); } }
    public bool CheckoutLockedOnly { get => _checkoutLockedOnly; set { if (Set(ref _checkoutLockedOnly, value)) ApplyCheckoutFilter(); } }
    public bool CheckoutBusy { get => _checkoutBusy; private set => Set(ref _checkoutBusy, value); }
    public string CheckoutError { get => _checkoutError; private set { if (Set(ref _checkoutError, value)) OnPropertyChanged(nameof(CheckoutEmpty)); } }
    /// <summary>«12 files · 4 people · 2 locked»: по всем открытым файлам, независимо от фильтра.</summary>
    public string CheckoutSummary { get => _checkoutSummary; private set => Set(ref _checkoutSummary, value); }
    /// <summary>Число на вкладке: сколько файлов открыто у других (чужие правки — то, что важно знать).</summary>
    public string CheckoutBadge { get => _checkoutBadge; private set => Set(ref _checkoutBadge, value); }
    /// <summary>Список загружен, но показывать нечего: никто ничего не держит или всё скрыто фильтром.</summary>
    public bool CheckoutEmpty => _checkoutLoaded && CheckoutGroups.Count == 0 && CheckoutError.Length == 0;
    public string CheckoutEmptyTitle => _checkoutAll.Count == 0 ? "Nothing is checked out" : "No files match the filters";
    public string CheckoutEmptyHint => _checkoutAll.Count == 0
        ? "Files that you or your teammates open for edit will show up here, grouped by person."
        : "Clear the search or switch off the filters above.";

    /// <summary>Вызывается из code-behind при переключении вкладок.</summary>
    public void SetCheckoutTabActive(bool active)
    {
        _checkoutTabActive = active;
        if (active) EnsureCheckoutsFresh();
    }

    private void EnsureCheckoutsFresh()
    {
        if (Selected == null || _checkoutBusy) return;
        if (!_checkoutLoaded || DateTime.UtcNow - _checkoutLoadedAt > TimeSpan.FromSeconds(30)) _ = LoadCheckoutsAsync();
    }

    private void ClearCheckouts()
    {
        _checkoutAll = Array.Empty<CheckedOutFile>();
        CheckoutGroups.Clear();
        _checkoutLoaded = false;
        _checkoutLoadedAt = DateTime.MinValue;
        CheckoutError = "";
        CheckoutSummary = "";
        CheckoutBadge = "";
        OnPropertyChanged(nameof(CheckoutEmpty));
    }

    private async Task LoadCheckoutsAsync()
    {
        var vm = Selected;
        if (vm == null || _checkoutBusy) return;
        CheckoutBusy = true;
        try
        {
            var me = !string.IsNullOrWhiteSpace(vm.Model.P4User) ? vm.Model.P4User : (await _vcs.GetConnectionInfoAsync(vm.Model)).User;
            var files = await _vcs.GetCheckedOutAsync(vm.Model);
            if (vm != Selected) return;   // за время запроса сменили профиль
            _checkoutMe = me;
            _checkoutAll = files;
            _checkoutLoaded = true;
            _checkoutLoadedAt = DateTime.UtcNow;
            CheckoutError = "";
            var others = files.Count(f => !IsMe(f.User));
            CheckoutBadge = others > 0 ? others.ToString() : "";
            var people = files.Select(f => f.User).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var locked = files.Count(f => f.IsLocked);
            CheckoutSummary = files.Count == 0 ? "" :
                $"{files.Count} file{(files.Count == 1 ? "" : "s")} · {people} {(people == 1 ? "person" : "people")}" + (locked > 0 ? $" · {locked} locked" : "");
            ApplyCheckoutFilter();
        }
        catch (Exception ex)
        {
            if (vm != Selected) return;
            _checkoutLoaded = true;
            CheckoutError = ex.Message;
        }
        finally { CheckoutBusy = false; }
    }

    private bool IsMe(string user) => string.Equals(user, _checkoutMe, StringComparison.OrdinalIgnoreCase);

    private bool MatchesCheckoutFilter(CheckedOutFile f, string[] words)
    {
        if (_checkoutOthersOnly && IsMe(f.User)) return false;
        if (_checkoutLockedOnly && !f.IsLocked) return false;
        // Каждое слово поиска должно найтись в пути, пользователе, workspace, действии или номере CL.
        return words.All(w => f.DepotPath.Contains(w, StringComparison.OrdinalIgnoreCase) || f.User.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                              f.Client.Contains(w, StringComparison.OrdinalIgnoreCase) || f.Action.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                              (f.Change != 0 && f.Change.ToString().Contains(w)));
    }

    /// <summary>Пересобирает группы: я первым, дальше по числу файлов. Раскрытость групп запоминается по пользователю.</summary>
    private void ApplyCheckoutFilter()
    {
        var words = _checkoutFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var filtering = words.Length > 0;
        var strip = CommonDepotPrefixLength(_checkoutAll);
        var visible = _checkoutAll.Where(f => MatchesCheckoutFilter(f, words)).ToList();

        // По умолчанию раскрыто всё, пока файлов немного; при поиске раскрываем найденное, чтобы результат был виден.
        var expandByDefault = visible.Count <= 60 || filtering;
        CheckoutGroups.Clear();
        foreach (var g in visible.GroupBy(f => f.User, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => IsMe(g.Key)).ThenByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var rows = g.OrderBy(f => CheckoutRank(f.DepotPath)).ThenBy(f => f.DepotPath, StringComparer.OrdinalIgnoreCase)
                .Select(f => new CheckoutFileViewModel(f, FolderOf(f.DepotPath, strip), CopyToClipboard)).ToList();
            var expanded = filtering || (_checkoutExpanded.TryGetValue(g.Key, out var remembered) ? remembered : expandByDefault);
            var group = new UserCheckoutViewModel(g.Key, IsMe(g.Key), rows, expanded, CopyToClipboard);
            var user = g.Key;
            group.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UserCheckoutViewModel.IsExpanded) && _checkoutFilter.Trim().Length == 0)
                    _checkoutExpanded[user] = group.IsExpanded;
            };
            CheckoutGroups.Add(group);
        }
        OnPropertyChanged(nameof(CheckoutEmpty));
        OnPropertyChanged(nameof(CheckoutEmptyTitle));
        OnPropertyChanged(nameof(CheckoutEmptyHint));
    }

    /// <summary>Порядок файлов в группе: Content, потом Source, потом всё остальное, а сгенерированное (Intermediate, Binaries…) в самом конце.</summary>
    private static int CheckoutRank(string depotPath)
    {
        var segments = depotPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var dirs = segments.Take(segments.Length - 1).ToList();
        if (dirs.Any(d => d is "Intermediate" or "Binaries" or "Saved" or "DerivedDataCache")) return 3;
        if (dirs.Any(d => d.Equals("Content", StringComparison.OrdinalIgnoreCase))) return 0;
        if (dirs.Any(d => d.Equals("Source", StringComparison.OrdinalIgnoreCase))) return 1;
        return 2;
    }

    private void SetAllCheckoutsExpanded(bool expanded)
    {
        foreach (var g in CheckoutGroups) g.IsExpanded = expanded;
    }

    /// <summary>Сколько начальных символов пути (//депо/ветка/) общие у всех файлов: их прячем, оставляя значимую часть.</summary>
    private static int CommonDepotPrefixLength(IReadOnlyList<CheckedOutFile> files)
    {
        if (files.Count == 0) return 0;
        var dirs = files.Select(f => f.DepotPath[..(f.DepotPath.LastIndexOf('/') + 1)]).Distinct().ToList();
        var first = dirs[0];
        var common = first.Length;
        foreach (var d in dirs.Skip(1))
        {
            var i = 0;
            while (i < common && i < d.Length && char.ToLowerInvariant(first[i]) == char.ToLowerInvariant(d[i])) i++;
            common = i;
        }
        // Прячем общее начало, но не больше двух уровней после «//»: глубже начинается осмысленная часть пути.
        var slashes = 0;
        var lastCommonSlash = 0;
        for (var i = 0; i < common; i++)
        {
            if (first[i] != '/') continue;
            lastCommonSlash = i + 1;
            if (++slashes == 4) break;   // «//depot/branch/» — четыре слэша
        }
        return lastCommonSlash;
    }

    private static string FolderOf(string depotPath, int strip)
    {
        var dir = depotPath[..(depotPath.LastIndexOf('/') + 1)];
        dir = strip > 0 && strip <= dir.Length ? dir[strip..] : dir.TrimStart('/');
        return dir.TrimEnd('/');
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
    private async Task RunActionAsync(ILaunchAction action, ProjectProfile profile)
    {
        if (action is CustomCommandAction custom && !_approvedCustom.Contains(custom.CommandLine))
        {
            var ok = _dialogs.Confirm(
                $"Run custom command from this profile?{Environment.NewLine}{Environment.NewLine}" +
                $"{custom.DisplayName}{Environment.NewLine}{custom.CommandLine}{Environment.NewLine}{Environment.NewLine}" +
                "It runs with your user rights. Continue only if you configured this command yourself.");
            if (!ok) return;
            _approvedCustom.Add(custom.CommandLine);
        }
        // Движок сам не предлагает пересобрать, если код новее DLL, — и запускается со старым кодом.
        if (action.Id == "editor" && !await OfferBuildBeforeLaunchAsync(profile)) return;
        await action.ExecuteAsync(profile);
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
