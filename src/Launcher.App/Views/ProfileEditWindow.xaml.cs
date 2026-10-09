using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PerforceLauncher.Core.Launching;
using PerforceLauncher.Core.Models;
using PerforceLauncher.Core.VersionControl;

namespace PerforceLauncher.App.Views;

public partial class ProfileEditWindow : Window
{
    private readonly IVersionControl _vcs;
    private readonly ObservableCollection<CustomActionDef> _actions;

    public ProjectProfile Profile { get; }

    public ProfileEditWindow(ProjectProfile profile, IVersionControl vcs)
    {
        InitializeComponent();
        _vcs = vcs;
        Profile = profile;
        DataContext = profile;
        _actions = new ObservableCollection<CustomActionDef>(profile.CustomActions);
        ActionsGrid.ItemsSource = _actions;
        UpdateHints();
    }

    /// <summary>Серые подсказки в пустых полях: что лаунчер подставит сам, если поле не заполнять.</summary>
    private void UpdateHints()
    {
        var auto = Profile.Clone();
        auto.UProjectRelativePath = "";
        auto.SolutionRelativePath = "";
        auto.EngineRootOverride = "";
        string Rel(string? path) => path == null ? "" : ProfilePaths.ToRelative(Profile.WorkspaceRoot, path);

        var up = ProfilePaths.UProject(auto);
        var sln = ProfilePaths.Solution(auto);
        var engine = new EngineLocator().DescribeEngine(auto);
        var ide = IdeDetector.Describe(auto);

        PerforceLauncher.App.Infrastructure.Hint.SetText(PortBox, "From p4 set / P4CONFIG");
        PerforceLauncher.App.Infrastructure.Hint.SetText(UserBox, "From p4 set / P4CONFIG");
        PerforceLauncher.App.Infrastructure.Hint.SetText(UProjectBox, up != null ? $"Auto-detected: {Rel(up)}" : "Not found. Pick the .uproject file");
        PerforceLauncher.App.Infrastructure.Hint.SetText(SlnBox, sln != null ? $"Auto-detected: {Rel(sln)}" : "None yet. Run Generate Project Files");
        PerforceLauncher.App.Infrastructure.Hint.SetText(EngineBox, engine.Length > 0 ? $"From .uproject: {engine}" : "From EngineAssociation in the .uproject");
        PerforceLauncher.App.Infrastructure.Hint.SetText(IdeBox, ide != null ? $"Default app for .sln ({ide})" : "Default app for .sln");
        PerforceLauncher.App.Infrastructure.Hint.SetText(ScopeBox,
            string.IsNullOrWhiteSpace(Profile.P4Client) ? "Whole workspace view" : $"Whole workspace: //{Profile.P4Client}/...");
    }

    private async void LoadClients_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ClientBox.Text = Profile.P4Client;
            // Подтягиваем текущие значения полей (Port/User) перед запросом.
            var list = await _vcs.ListWorkspacesAsync(Profile);
            ClientBox.ItemsSource = list;
            ClientBox.IsDropDownOpen = true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cannot load workspaces", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ClientBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClientBox.SelectedItem is WorkspaceInfo ws)
        {
            Profile.P4Client = ws.Name;
            Profile.WorkspaceRoot = ws.Root;
            DataContext = null; DataContext = Profile;   // обновить поля
            ClientBox.Text = ws.Name;
            UpdateHints();
        }
    }

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Workspace root", InitialDirectory = Profile.WorkspaceRoot };
        if (dlg.ShowDialog(this) == true) { Profile.WorkspaceRoot = dlg.FolderName; Rebind(); }
    }

    private void BrowseUProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Unreal project (*.uproject)|*.uproject", InitialDirectory = Profile.WorkspaceRoot };
        if (dlg.ShowDialog(this) != true) return;
        // Если root ещё не задан, считаем корнем папку проекта.
        if (string.IsNullOrWhiteSpace(Profile.WorkspaceRoot)) Profile.WorkspaceRoot = System.IO.Path.GetDirectoryName(dlg.FileName)!;
        Profile.UProjectRelativePath = ProfilePaths.ToRelative(Profile.WorkspaceRoot, dlg.FileName);
        Rebind();
    }

    private void BrowseSln_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Solution (*.sln)|*.sln", InitialDirectory = Profile.WorkspaceRoot };
        if (dlg.ShowDialog(this) != true) return;
        Profile.SolutionRelativePath = ProfilePaths.ToRelative(Profile.WorkspaceRoot, dlg.FileName);
        Rebind();
    }

    private void BrowseEngine_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Engine root (folder containing 'Engine')" };
        if (dlg.ShowDialog(this) == true) { Profile.EngineRootOverride = dlg.FolderName; Rebind(); }
    }

    private void BrowseIde_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Executable (*.exe)|*.exe" };
        if (dlg.ShowDialog(this) == true) { Profile.IdePath = dlg.FileName; Rebind(); }
    }

    private void Rebind() { DataContext = null; DataContext = Profile; UpdateHints(); }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ActionsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (string.IsNullOrWhiteSpace(Profile.Name))
        {
            MessageBox.Show(this, "Name is required.", Title);
            return;
        }
        Profile.PollIntervalSeconds = Math.Max(15, Profile.PollIntervalSeconds);
        Profile.CustomActions = _actions
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.Command)).ToList();
        DialogResult = true;
    }
}
