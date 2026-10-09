using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Media;
using PerforceLauncher.App.Infrastructure;
using PerforceLauncher.Core.Launching;
using PerforceLauncher.Core.Models;

namespace PerforceLauncher.App.ViewModels;

public sealed class ProfileViewModel : ObservableObject
{
    private static readonly EngineLocator Engines = new();

    public ProjectProfile Model { get; }
    public ObservableCollection<ChangeInfo> NewChanges { get; } = new();

    private VcsStatus? _status;
    private string? _error;
    private bool _authRequired;
    private bool _checking;
    private string? _engineLabel;

    public ProfileViewModel(ProjectProfile model)
    {
        Model = model;
        AvatarBrush = Palette.Avatar(model.Name);
    }

    // ---------- идентичность ----------

    public string Name => Model.Name;
    public Brush AvatarBrush { get; }

    public string Initials
    {
        get
        {
            var words = Name.Split(new[] { ' ', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2) return $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}";
            return words.Length == 1 ? words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant() : "?";
        }
    }

    public string Subtitle => string.IsNullOrWhiteSpace(Model.P4Client) ? "no workspace" : Model.P4Client;
    public string RootText => string.IsNullOrWhiteSpace(Model.WorkspaceRoot) ? "root unknown" : Model.WorkspaceRoot;

    /// <summary>«UE 5.4», «Source build» и т.п.; читается из .uproject и кэшируется.</summary>
    public string EngineLabel => _engineLabel ??= Engines.DescribeEngine(Model);

    public string ProjectFileName
    {
        get
        {
            var up = ProfilePaths.UProject(Model);
            return up == null ? "" : Path.GetFileNameWithoutExtension(up);
        }
    }

    // ---------- состояние ----------

    public bool AuthRequired => _authRequired;
    /// <summary>Ошибка проверки не из-за логина (обычно сеть ещё не поднялась).</summary>
    public bool HasTransientError => _error != null && !_authRequired;
    public bool IsBehind => _status?.IsBehind == true;
    public long HaveChange => _status?.HaveChange ?? 0;
    public int Behind => _status?.NewChanges.Count ?? 0;
    public string BehindText => Behind == 0 ? "" : (_status!.HasMoreChanges ? $"{Behind}+" : Behind.ToString());
    public string HaveText => _status == null ? "—" : $"CL {_status.HaveChange}";
    public string LatestText => _status == null ? "—" : $"CL {_status.LatestChange}";

    private enum Kind { Unknown, UpToDate, Behind, Auth, Error }

    private Kind State =>
        _error != null ? (_authRequired ? Kind.Auth : Kind.Error) :
        _status == null ? Kind.Unknown :
        _status.IsBehind ? Kind.Behind : Kind.UpToDate;

    public Brush StateBrush => State switch
    {
        Kind.UpToDate => Palette.Ok,
        Kind.Behind => Palette.Warn,
        Kind.Auth or Kind.Error => Palette.Bad,
        _ => Palette.Neutral,
    };

    public Brush StateTint => State switch
    {
        Kind.UpToDate => Palette.OkTint,
        Kind.Behind => Palette.WarnTint,
        Kind.Auth or Kind.Error => Palette.BadTint,
        _ => Palette.NeutralTint,
    };

    /// <summary>Глиф Segoe Fluent Icons для статуса.</summary>
    public string StateGlyph => State switch
    {
        Kind.UpToDate => "",   // CheckMark
        Kind.Behind => "",     // Download
        Kind.Auth => "",       // Lock
        Kind.Error => "",      // Error
        _ => "",               // Sync
    };

    public string StatusTitle => State switch
    {
        Kind.UpToDate => "Up to date",
        Kind.Behind => $"{BehindText} new commit{(Behind == 1 ? "" : "s")} on the server",
        Kind.Auth => "Sign in required",
        Kind.Error => "Can't reach Perforce",
        _ => _checking ? "Checking for updates…" : "Not checked yet",
    };

    public string StatusSubtitle
    {
        get
        {
            if (_error != null)
            {
                var first = _error.Split('\n')[0].Trim();
                return first.Length > 160 ? first[..160] + "…" : first;
            }
            if (_status == null) return $"Workspace {Subtitle}";
            var checkedText = _checking ? "checking now…" : $"checked {TimeText.Ago(_status.CheckedAt)}";
            if (!_status.IsBehind) return $"Synced to CL {_status.HaveChange} · {checkedText}";
            var top = _status.NewChanges.FirstOrDefault();
            var by = top != null ? $" by {top.User}" : "";
            return $"You're on CL {_status.HaveChange} · latest CL {_status.LatestChange}{by} · {checkedText}";
        }
    }

    public string PathWarning
    {
        get
        {
            var up = ProfilePaths.UProject(Model);
            if (up == null) return "No .uproject found in this workspace. Set it in Project Settings.";
            return File.Exists(up) ? "" : $"Project file not found: {up}";
        }
    }

    public bool IsChecking
    {
        get => _checking;
        set { if (Set(ref _checking, value)) Refresh(); }
    }

    public void Apply(ProfileCheckResult r)
    {
        _status = r.Status ?? _status;
        _error = r.Error;
        _authRequired = r.AuthRequired;
        if (r.Status != null)
        {
            // Кэшируем root воркспейса: от него считаются относительные пути.
            if (!string.IsNullOrWhiteSpace(r.Status.Workspace.Root))
                Model.WorkspaceRoot = r.Status.Workspace.Root;
            NewChanges.Clear();
            foreach (var c in r.Status.NewChanges) NewChanges.Add(c);
        }
        _checking = false;
        _engineLabel = null;   // после sync .uproject мог поменяться
        Refresh();
    }

    public void ResetStatus()
    {
        _status = null; _error = null; _authRequired = false; _engineLabel = null;
        NewChanges.Clear();
        Refresh();
    }

    /// <summary>Обновить «checked N min ago» без перерисовки остального.</summary>
    public void RefreshTime() => OnPropertyChanged(nameof(StatusSubtitle));

    /// <summary>Перерисовать все биндинги (после редактирования профиля и т.п.).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
