using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using PerforceLauncher.Core.Services;

namespace PerforceLauncher.App.Infrastructure;

public sealed class TrayNotifier : INotifier, IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private Guid? _pendingProfile;
    private List<(Guid Id, string Name)> _projects = new();

    public event Action<Guid?>? ActivateRequested;
    public event Action? CheckAllRequested;
    public event Action<Guid>? LaunchRequested;
    public event Action? ExitRequested;

    public TrayNotifier()
    {
        BuildMenu();
        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "Perforce Launcher",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        // Один левый клик открывает окно (правый — меню). Двойной клик не нужен.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ActivateRequested?.Invoke(null);
        };
        _icon.BalloonTipClicked += (_, _) => ActivateRequested?.Invoke(_pendingProfile);
    }

    /// <summary>Проекты для пункта «Launch Unreal» — запуск редактора прямо из трея.</summary>
    public void SetProjects(IEnumerable<(Guid Id, string Name)> projects)
    {
        _projects = projects.ToList();
        BuildMenu();
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var open = new ToolStripMenuItem("Open Perforce Launcher", null, (_, _) => ActivateRequested?.Invoke(null))
        {
            Font = new Font(_menu.Font, FontStyle.Bold),
        };
        _menu.Items.Add(open);
        if (_projects.Count > 0)
        {
            var launch = new ToolStripMenuItem("Launch Unreal");
            foreach (var (id, name) in _projects)
                launch.DropDownItems.Add(name.Replace("&", "&&"), null, (_, _) => LaunchRequested?.Invoke(id));
            _menu.Items.Add(launch);
        }
        _menu.Items.Add("Check for Updates", null, (_, _) => CheckAllRequested?.Invoke());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
    }

    /// <summary>Иконка самого exe (ApplicationIcon); при сбое — стандартная.</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (path != null && Icon.ExtractAssociatedIcon(path) is { } icon) return icon;
        }
        catch { /* фолбэк ниже */ }
        return SystemIcons.Application;
    }

    public void Notify(string title, string message, Guid? profileId = null)
    {
        _pendingProfile = profileId;
        _icon.ShowBalloonTip(8000, title, message, ToolTipIcon.Info);
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
