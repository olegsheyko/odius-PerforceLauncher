using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PerforceLauncher.App.ViewModels;
using PerforceLauncher.Core.Models;
using PerforceLauncher.Core.Services;
using PerforceLauncher.Core.VersionControl;

namespace PerforceLauncher.App.Views;

public sealed class DialogService : IDialogService
{
    private readonly IVersionControl _vcs;
    private readonly SessionKeeper? _session;
    public Window? Owner { get; set; }
    /// <summary>Автообновление (задаётся приложением); null — раздел Updates показывает только версию.</summary>
    public PerforceLauncher.App.Infrastructure.AppUpdater? Updater { get; set; }
    /// <summary>Сохранить состояние, установить скачанное обновление и перезапуститься.</summary>
    public Action? RestartToUpdate { get; set; }

    public DialogService(IVersionControl vcs, SessionKeeper? session = null)
    {
        _vcs = vcs;
        _session = session;
    }

    public ProjectProfile? EditProfile(ProjectProfile? existing)
    {
        var copy = existing?.Clone() ?? new ProjectProfile { Name = "New project" };
        var win = new ProfileEditWindow(copy, _vcs) { Owner = Owner };
        return win.ShowDialog() == true ? win.Profile : null;
    }

    public async Task<WorkspaceInfo?> PickWorkspaceAsync(ProjectProfile profile)
    {
        var win = new WorkspacePickerWindow(profile, _vcs) { Owner = Owner };
        await Task.Yield();
        return win.ShowDialog() == true ? win.Selected : null;
    }

    public (string Password, bool Remember)? PromptPassword(string message, bool rememberDefault)
    {
        var app = Application.Current;
        var box = new PasswordBox { Margin = new Thickness(0, 10, 0, 12), MinWidth = 380 };
        var remember = new CheckBox
        {
            Content = "Remember password and sign in again automatically",
            IsChecked = rememberDefault, Margin = new Thickness(0, 0, 0, 2),
        };
        var note = new TextBlock
        {
            Text = "Stored encrypted in Windows Credential Manager for your Windows account only. " +
                   "The launcher renews the Perforce session before it expires, so P4V and Unreal stay signed in too.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 380, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = (Brush)app.FindResource("TextMuted"), Margin = new Thickness(28, 0, 0, 16),
        };
        var ok = new Button { Content = "Sign In", IsDefault = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 6, 14, 6) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 6, 14, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var panel = new StackPanel
        {
            Margin = new Thickness(20, 16, 20, 18),
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 }, box, remember, note, buttons },
        };
        var win = new Window
        {
            Title = "Sign in to Perforce", Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Owner, ResizeMode = ResizeMode.NoResize,
        };
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => box.Focus();
        (string, bool)? result = Themed(win).ShowDialog() == true && box.Password.Length > 0
            ? (box.Password, remember.IsChecked == true)
            : null;
        box.Clear();   // не держим пароль в контроле дольше необходимого
        return result;
    }

    public bool EditSettings(AppSettings settings)
    {
        var app = Application.Current;
        TextBlock Section(string title) => new()
        {
            Text = title, Style = (Style)app.FindResource("SectionLabel"), Margin = new Thickness(0, 18, 0, 8),
        };
        TextBlock Note(string text, Brush? brush = null) => new()
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, MaxWidth = 470, HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = brush ?? (Brush)app.FindResource("TextMuted"), Margin = new Thickness(28, -2, 0, 8),
        };
        CheckBox Check(string text, bool value) => new() { Content = text, IsChecked = value, Margin = new Thickness(0, 0, 0, 4) };

        // Реальное состояние автозапуска: запись могли удалить или выключить в Диспетчере задач.
        var state = AutostartState.Off;
        try { state = AutostartManager.GetState(); } catch { /* реестр недоступен — считаем выключенным */ }

        var p4 = new TextBox { Text = settings.P4Path, MinWidth = 470 };
        var p4Note = Note("Leave \"p4\" to find it in PATH.");
        p4Note.Margin = new Thickness(0, 4, 0, 10);   // подпись под полем, без отступа чекбокса
        var launch = Check("Launch at Windows sign-in", state == AutostartState.On);
        var minimized = Check("Start minimized to tray", settings.StartMinimized);
        var remind = Check("Remind about new commits at sign-in and after sleep", settings.RemindOnWake);
        var tray = Check("Close button hides the window to the tray", settings.MinimizeToTray);
        var set = Check("Run 'p4 set P4CLIENT' when switching workspace", settings.ApplyP4SetOnSwitch);

        var launchNote = state == AutostartState.DisabledInTaskManager
            ? Note("Currently turned off in Task Manager → Startup apps. Tick the box to turn it back on.", (Brush)app.FindResource("Warn"))
            : Note("Registered only for your Windows account, no admin rights needed.");
        void SyncMinimized() => minimized.IsEnabled = launch.IsChecked == true;
        launch.Checked += (_, _) => SyncMinimized();
        launch.Unchecked += (_, _) => SyncMinimized();
        SyncMinimized();

        // Сохранённые пароли (Диспетчер учётных данных Windows) и кнопка «забыть».
        var saved = _session?.SavedAccounts() ?? Array.Empty<string>();
        var savedText = new TextBlock
        {
            Text = saved.Count == 0
                ? "No saved passwords. Tick \"Remember password\" when signing in to enable automatic sign-in."
                : "Automatic sign-in: " + string.Join(", ", saved),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 470, HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = (Brush)app.FindResource(saved.Count == 0 ? "TextMuted" : "TextSecondary"), Margin = new Thickness(0, 0, 0, 8),
        };
        var forget = new Button
        {
            Content = "Forget Saved Passwords", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 5, 12, 5),
            IsEnabled = saved.Count > 0,
        };
        forget.Click += (_, _) =>
        {
            if (!Confirm("Delete saved Perforce passwords from Windows Credential Manager?\n\nAutomatic sign-in stops; your current session stays until it expires.")) return;
            _session?.ForgetAll();
            savedText.Text = "No saved passwords.";
            forget.IsEnabled = false;
        };

        // Версия и обновления.
        var installed = Updater?.IsInstalled == true;
        var version = Updater?.CurrentVersion ?? "dev";
        var updateText = new TextBlock
        {
            Text = installed
                ? $"Version {version}. Updates download from GitHub and install automatically."
                : $"Version {version}, development build. Automatic updates work in the installed version.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 470, HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = (Brush)app.FindResource("TextSecondary"), Margin = new Thickness(0, 0, 0, 8),
        };
        var checkUpdates = new Button { Content = "Check for Updates", Padding = new Thickness(12, 5, 12, 5), IsEnabled = installed, Margin = new Thickness(0, 0, 8, 0) };
        var restart = new Button
        {
            Content = "Restart to Update", Padding = new Thickness(12, 5, 12, 5),
            Style = (Style)app.FindResource("PrimaryButton"),
            Visibility = Updater?.ReadyVersion != null ? Visibility.Visible : Visibility.Collapsed,
        };
        if (Updater?.ReadyVersion is { } readyNow) updateText.Text = $"Version {readyNow} is downloaded and ready to install (current: {version}).";
        checkUpdates.Click += async (_, _) =>
        {
            if (Updater == null) return;
            checkUpdates.IsEnabled = false;
            updateText.Text = "Checking for updates…";
            try
            {
                var ready = await Updater.CheckAndDownloadAsync();
                updateText.Text = ready == null
                    ? $"You're up to date (version {version})."
                    : $"Version {ready} is downloaded and ready to install (current: {version}).";
                if (ready != null) restart.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { updateText.Text = "Couldn't check for updates: " + ex.Message.Split('\n')[0]; }
            finally { checkUpdates.IsEnabled = true; }
        };
        restart.Click += (_, _) => RestartToUpdate?.Invoke();
        var updateButtons = new StackPanel { Orientation = Orientation.Horizontal, Children = { checkUpdates, restart } };

        var ok = new Button { Content = "Save", IsDefault = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 6, 14, 6) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 6, 14, 6) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0), Children = { ok, cancel },
        };
        var panel = new StackPanel
        {
            Margin = new Thickness(20, 4, 20, 18),
            Children =
            {
                Section("STARTUP"), launch, launchNote, minimized, Note("The launcher waits in the tray and keeps checking for commits."),
                Section("NOTIFICATIONS"), remind, Note("One summary notification for all projects; works even if the launcher was already running."),
                Section("WINDOW"), tray, Note("Otherwise closing the window exits the launcher and stops checking."),
                Section("PERFORCE"), new TextBlock { Text = "Path to p4.exe", Margin = new Thickness(0, 0, 0, 6) }, p4,
                p4Note, set, Note("So P4V and Unreal Editor use the same workspace."),
                Section("SAVED SIGN-IN"), savedText, forget,
                Section("UPDATES"), updateText, updateButtons,
                buttons,
            },
        };
        var win = new Window
        {
            // На невысоких экранах (ноутбуки) настройки прокручиваются, а не обрезаются.
            Title = "Settings", SizeToContent = SizeToContent.WidthAndHeight,
            MaxHeight = SystemParameters.WorkArea.Height - 40,
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Owner, ResizeMode = ResizeMode.NoResize,
        };
        ok.Click += (_, _) => win.DialogResult = true;
        if (Themed(win).ShowDialog() != true) return false;
        settings.P4Path = string.IsNullOrWhiteSpace(p4.Text) ? "p4" : p4.Text.Trim();
        settings.StartWithWindows = launch.IsChecked == true;
        settings.StartMinimized = minimized.IsChecked == true;
        settings.RemindOnWake = remind.IsChecked == true;
        settings.MinimizeToTray = tray.IsChecked == true;
        settings.ApplyP4SetOnSwitch = set.IsChecked == true;
        return true;
    }

    public string? PromptText(string title, string caption, string initial, string okText)
    {
        var box = new TextBox
        {
            Text = initial, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Width = 480, Height = 160, Margin = new Thickness(0, 8, 0, 12),
        };
        var ok = new Button { Content = okText, IsDefault = false, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 6, 14, 6) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 6, 14, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var panel = new StackPanel { Margin = new Thickness(16), Children = { new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 }, box, buttons } };
        var win = new Window
        {
            Title = title, Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Owner, ResizeMode = ResizeMode.NoResize,
        };
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => { box.Focus(); box.CaretIndex = box.Text.Length; };
        return Themed(win).ShowDialog() == true ? box.Text : null;
    }

    /// <summary>Окна, собранные в коде, получают общий стиль (тёмный фон/текст) и иконку приложения.</summary>
    private static Window Themed(Window w)
    {
        w.Style = (Style)Application.Current.FindResource("AppWindow");
        // Первая кнопка нижней панели (Login/Save/Submit) — основная, с акцентом.
        var content = w.Content is ScrollViewer sv ? sv.Content : w.Content;
        if (content is StackPanel root && root.Children.OfType<StackPanel>().LastOrDefault() is { } bar
            && bar.Children.OfType<Button>().FirstOrDefault() is { } ok)
            ok.Style = (Style)Application.Current.FindResource("PrimaryButton");
        w.Icon = new System.Windows.Media.Imaging.BitmapImage(new System.Uri("pack://application:,,,/PerforceLauncher;component/Assets/app.ico"));
        return w;
    }

    public bool Confirm(string message) =>
        // Кнопка по умолчанию — «No»: случайный Enter не должен подтверждать revert/submit.
        MessageBox.Show(Owner!, message, "Perforce Launcher", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void Error(string message) =>
        MessageBox.Show(Owner!, message, "Perforce Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
}
