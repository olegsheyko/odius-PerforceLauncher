using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using PerforceLauncher.App.ViewModels;
using PerforceLauncher.Core.Models;
using PerforceLauncher.Core.Services;

namespace PerforceLauncher.App.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm, AppSettings settings, SettingsStore store)
    {
        InitializeComponent();
        DataContext = vm;
        _vm = vm;
        _settings = settings;
        _store = store;

        // Pending-файлы группируются по changelist'ам (порядок групп = порядок элементов).
        CollectionViewSource.GetDefaultView(vm.PendingFiles).GroupDescriptions.Add(new PropertyGroupDescription(nameof(PendingFileViewModel.Header)));
        Activated += (_, _) => _vm.RefreshPendingIfStale();
        StateChanged += (_, _) => UpdateMaximizedLayout();
        SourceInitialized += (_, _) => ApplyDwmStyle();
        RestorePlacement();
    }

    // ---------- своя шапка окна ----------

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>С WindowChrome развёрнутое окно выходит за экран на ширину рамки — компенсируем отступом.</summary>
    private void UpdateMaximizedLayout()
    {
        var max = WindowState == WindowState.Maximized;
        RootGrid.Margin = max ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = max ? "" : "";
        MaxButton.ToolTip = max ? "Restore" : "Maximize";
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Тёмная рамка, скруглённые углы Windows 11 и тонкая граница в цвет карточек.</summary>
    private void ApplyDwmStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1, round = 2, border = 0x00282323;   // COLORREF: 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));      // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));   // DWMWA_WINDOW_CORNER_PREFERENCE = round
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));  // DWMWA_BORDER_COLOR (на Windows 10 игнорируется)
    }

    // ---------- положение окна ----------

    private void RestorePlacement()
    {
        var p = _settings.Window;
        if (p == null || p.Width < MinWidth || p.Height < MinHeight) return;
        // Монитор могли отключить: восстанавливаем, только если окно попадает на текущий рабочий стол.
        var visible = p.Left + 100 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
                      && p.Top + 50 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight
                      && p.Left + p.Width - 100 > SystemParameters.VirtualScreenLeft
                      && p.Top > SystemParameters.VirtualScreenTop - 20;
        if (!visible) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = p.Left; Top = p.Top; Width = p.Width; Height = p.Height;
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    public void SavePlacement()
    {
        if (!IsLoaded) return;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (b.IsEmpty || b.Width <= 0) return;
        _settings.Window = new WindowPlacement
        {
            Left = b.Left, Top = b.Top, Width = b.Width, Height = b.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
        try { _store.Save(_settings); } catch { /* не критично */ }
    }

    // ---------- меню и списки ----------

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = MoreButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();

    /// <summary>История грузится лениво: только когда вкладку History открыли. SelectionChanged внутренних таблиц сюда тоже всплывает.</summary>
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs)) return;
        _vm.SetHistoryTabActive(ReferenceEquals(MainTabs.SelectedItem, HistoryTab));
    }

    /// <summary>Клик по заголовку changelist'а: выбрать его целиком (выделение файлов снимается).</summary>
    private void ChangelistHeader_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not { } tag) return;
        PendingList.UnselectAll();
        _vm.SelectChangelist(System.Convert.ToInt64(tag));
    }

    /// <summary>Меню «⋯» в заголовке changelist'а.</summary>
    private void ChangelistMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ChangelistHeaderViewModel header } button) return;
        PendingList.UnselectAll();
        _vm.SelectChangelist(header.Id);

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        void Add(string text, System.Windows.Input.ICommand command, bool enabled = true)
        {
            var item = new MenuItem { Header = text, IsEnabled = enabled };
            item.Click += (_, _) => command.Execute(header.Id);
            menu.Items.Add(item);
        }
        if (header.CanSubmit) Add("Submit…", _vm.SubmitChangelistCommand);
        if (!header.IsDefault) Add("Edit Description…", _vm.EditChangelistCommand);
        if (header.FileCount > 0) Add("Revert All Files…", _vm.RevertChangelistCommand);
        if (!header.IsDefault)
        {
            Add("Delete Changelist", _vm.DeleteChangelistByIdCommand, enabled: header.FileCount == 0);
            menu.Items.Add(new Separator());
            var copy = new MenuItem { Header = "Copy CL Number" };
            copy.Click += (_, _) => _vm.CopyCommand.Execute(header.Id.ToString());
            menu.Items.Add(copy);
        }
        menu.IsOpen = true;
    }

    private void PendingList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.SelectedPending = PendingList.SelectedItems.Cast<PendingFileViewModel>().ToList();

    /// <summary>Меню «Move To»: default, существующие pending CL и «New changelist...».</summary>
    private void MoveButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoveButton, Placement = PlacementMode.Bottom };
        foreach (var cl in _vm.Changelists)
        {
            var first = cl.Description.Split('\n')[0].Trim();
            var header = cl.IsDefault ? "Default changelist" : $"CL {cl.Id}: {first}";
            var id = cl.Id;
            var item = new MenuItem { Header = header.Replace("_", "__") };
            item.Click += (_, _) => _vm.MoveToCommand.Execute(id);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var create = new MenuItem { Header = "New Changelist…" };
        create.Click += (_, _) => _vm.MoveToCommand.Execute(-1L);
        menu.Items.Add(create);
        menu.IsOpen = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SavePlacement();
        // Крестик прячет окно в трей, чтобы фоновая проверка коммитов продолжала работать.
        if (_settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
        Application.Current.Shutdown();
    }
}
