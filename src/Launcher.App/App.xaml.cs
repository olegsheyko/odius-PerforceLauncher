using System;
using System.Linq;
using System.Threading;
using Microsoft.Win32;
using System.Windows;
using PerforceLauncher.App.Infrastructure;
using PerforceLauncher.App.ViewModels;
using PerforceLauncher.App.Views;
using PerforceLauncher.Core.Launching;
using PerforceLauncher.Core.Services;
using PerforceLauncher.Core.VersionControl;

namespace PerforceLauncher.App;

public partial class App : Application
{
    private const string MutexName = "PerforceLauncher.SingleInstance";
    private const string ActivateEventName = "PerforceLauncher.Activate";

    private Mutex? _singleInstance;
    private EventWaitHandle? _activateEvent;
    private volatile bool _exiting;
    private TrayNotifier? _tray;
    private ChangeWatcher? _watcher;
    private MainWindow? _window;
    private PowerModeChangedEventHandler? _powerHandler;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var fromAutostart = e.Args.Contains(AutostartManager.ArgTray);

        _singleInstance = new Mutex(true, MutexName, out var isNew);
        if (!isNew)
        {
            // Уже запущен: повторный запуск exe просто показывает существующее окно (автозапуск — молча выходит).
            if (!fromAutostart)
            {
                try { using var ev = EventWaitHandle.OpenExisting(ActivateEventName); ev.Set(); }
                catch { MessageBox.Show("Perforce Launcher is already running (see the tray).", "Perforce Launcher"); }
            }
            Shutdown();
            return;
        }
        ListenForActivation();

        var store = new SettingsStore();
        var settings = store.Load();

        var vcs = new P4VersionControl(() => P4Locator.Resolve(settings.P4Path));
        // Автопродление Perforce-сессии сохранённым паролем (Диспетчер учётных данных Windows).
        var session = new SessionKeeper(vcs, new WindowsCredentialStore());
        _watcher = new ChangeWatcher(vcs, () => settings.Profiles, session);
        _tray = new TrayNotifier();

        var dialogs = new DialogService(vcs, session);
        var vm = new MainViewModel(settings, store, vcs, _watcher, LaunchActionRegistry.CreateDefault(), dialogs, _tray, session);

        _window = new MainWindow(vm, settings, store);
        dialogs.Owner = _window;

        _tray.ActivateRequested += id =>
        {
            if (id.HasValue) vm.SelectProfile(id.Value);
            ShowWindow();
        };
        _tray.CheckAllRequested += () => _ = vm.CheckAllAsync();
        _tray.LaunchRequested += id => _ = vm.LaunchEditorAsync(id);
        _tray.ExitRequested += () =>
        {
            _window.SavePlacement();
            store.Save(settings);
            Shutdown();
        };
        void SyncTrayProjects() => _tray.SetProjects(vm.Profiles.Select(p => (p.Model.Id, p.Name)));
        SyncTrayProjects();
        vm.Profiles.CollectionChanged += (_, _) => SyncTrayProjects();

        // Выход из сна: напомнить о коммитах. Событие приходит из фонового потока, поэтому переходим в UI-поток.
        _powerHandler = (_, args) =>
        {
            if (args.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(new Action(vm.OnSystemResume));
        };
        SystemEvents.PowerModeChanged += _powerHandler;

        // Обновить путь в автозапуске, если exe переехал. Выключатель Диспетчера задач не трогаем.
        if (settings.StartWithWindows)
            try { AutostartManager.RefreshCommand(settings.StartMinimized); } catch { /* не критично */ }

        _watcher.Start();
        _ = vm.StartupReminderAsync();

        // Автозапуск с Windows передаёт --tray: окно не показываем, лаунчер сидит в трее.
        if (!fromAutostart) _window.Show();
    }

    /// <summary>Второй экземпляр сигналит именованным событием (в пределах сессии пользователя) — показываем окно.</summary>
    private void ListenForActivation()
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var thread = new Thread(() =>
        {
            while (_activateEvent.WaitOne())
            {
                if (_exiting) return;
                Dispatcher.BeginInvoke(new Action(ShowWindow));
            }
        }) { IsBackground = true, Name = "ActivationListener" };
        thread.Start();
    }

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _activateEvent?.Set();   // разбудить поток ожидания, чтобы он завершился
        if (_powerHandler != null) SystemEvents.PowerModeChanged -= _powerHandler;   // статическое событие: иначе утечка
        _watcher?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
