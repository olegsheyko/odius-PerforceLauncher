using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using PerforceLauncher.Core.Models;
using PerforceLauncher.Core.VersionControl;

namespace PerforceLauncher.App.Views;

public partial class WorkspacePickerWindow : Window
{
    private readonly ProjectProfile _profile;
    private readonly IVersionControl _vcs;

    public WorkspaceInfo? Selected { get; private set; }

    public WorkspacePickerWindow(ProjectProfile profile, IVersionControl vcs)
    {
        InitializeComponent();
        _profile = profile;
        _vcs = vcs;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async System.Threading.Tasks.Task LoadAsync()
    {
        try
        {
            var all = await _vcs.ListWorkspacesAsync(_profile);
            List.ItemsSource = all;
            List.SelectedItem = all.FirstOrDefault(w => w.Name == _profile.P4Client);
            StatusText.Text = $"{all.Count} workspace(s). Current: {_profile.P4Client}. Workspaces on this machine are listed first.";
        }
        catch (Exception ex) { StatusText.Text = "Error: " + ex.Message; }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Selected = List.SelectedItem as WorkspaceInfo;
        if (Selected != null) DialogResult = true;
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e) => Ok_Click(sender, e);
}
