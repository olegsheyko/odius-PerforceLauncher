using System.Windows;
using System.Windows.Controls;
using PerforceLauncher.App.ViewModels;

namespace PerforceLauncher.App.Views;

// Код окон инструментов: вся логика во view model, здесь только привязка DataContext.

public partial class FileHistoryWindow : Window
{
    public FileHistoryWindow(FileHistoryViewModel vm) { InitializeComponent(); DataContext = vm; }
}

public partial class ShelvesWindow : Window
{
    public ShelvesWindow(ShelvesViewModel vm) { InitializeComponent(); DataContext = vm; }
}

public partial class LabelsWindow : Window
{
    public LabelsWindow(LabelsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += () => { DialogResult = true; };
    }
}

public partial class CleanupWindow : Window
{
    public CleanupWindow(CleanupViewModel vm) { InitializeComponent(); DataContext = vm; }
}

public partial class BuildIssuesWindow : Window
{
    public BuildIssuesWindow(BuildIssuesViewModel vm) { InitializeComponent(); DataContext = vm; }

    private void Grid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (((DataGrid)sender).SelectedItem is BuildIssueViewModel { OpenCommand: var cmd } && cmd.CanExecute(null)) cmd.Execute(null);
    }
}
