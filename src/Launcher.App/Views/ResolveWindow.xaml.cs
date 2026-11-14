using System.Windows;
using PerforceLauncher.App.ViewModels;

namespace PerforceLauncher.App.Views;

public partial class ResolveWindow : Window
{
    public ResolveWindow(ResolveViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
