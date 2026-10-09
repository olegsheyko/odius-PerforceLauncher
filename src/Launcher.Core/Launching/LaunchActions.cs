using System.Diagnostics;
using PerforceLauncher.Core.Models;

namespace PerforceLauncher.Core.Launching;

/// <summary>Одна кнопка запуска. Чтобы добавить новое действие — реализуйте интерфейс и
/// зарегистрируйте в <see cref="LaunchActionRegistry"/>.</summary>
public interface ILaunchAction
{
    string Id { get; }
    string DisplayName { get; }
    bool CanExecute(ProjectProfile profile);
    Task ExecuteAsync(ProjectProfile profile, CancellationToken ct = default);
}

public sealed class LaunchActionRegistry
{
    private readonly List<ILaunchAction> _builtIn;

    public LaunchActionRegistry(IEnumerable<ILaunchAction> builtIn) => _builtIn = builtIn.ToList();

    public IReadOnlyList<ILaunchAction> GetActions(ProjectProfile profile) =>
        _builtIn.Concat(profile.CustomActions
                .Where(c => !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Command))
                .Select(c => (ILaunchAction)new CustomCommandAction(c, new EngineLocator())))
            .ToList();

    public static LaunchActionRegistry CreateDefault()
    {
        var engines = new EngineLocator();
        return new LaunchActionRegistry(new ILaunchAction[]
        {
            new OpenEditorAction(engines),
            new OpenSolutionAction(),
            new GenerateProjectFilesAction(engines),
            new OpenFolderAction(),
        });
    }
}

public sealed class OpenEditorAction : ILaunchAction
{
    private readonly EngineLocator _engines;
    public OpenEditorAction(EngineLocator engines) => _engines = engines;

    public string Id => "editor";
    public string DisplayName => "Launch Editor";

    public bool CanExecute(ProjectProfile p)
    {
        var up = ProfilePaths.UProject(p);
        return up != null && File.Exists(up);
    }

    public Task ExecuteAsync(ProjectProfile p, CancellationToken ct = default)
    {
        var up = ProfilePaths.UProject(p) ?? throw new InvalidOperationException(".uproject is not configured.");
        var root = _engines.FindEngineRoot(p);
        var exe = root != null ? _engines.FindEditorExe(root) : null;

        if (exe != null)
            Process.Start(new ProcessStartInfo(exe) { ArgumentList = { up }, WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false });
        else
            // Фолбэк: ассоциация .uproject (UnrealVersionSelector сам подберёт движок).
            Process.Start(new ProcessStartInfo(up) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}

public sealed class OpenSolutionAction : ILaunchAction
{
    public string Id => "solution";
    public string DisplayName => "Open Solution";

    public bool CanExecute(ProjectProfile p)
    {
        var sln = ProfilePaths.Solution(p);
        return sln != null && File.Exists(sln);
    }

    public Task ExecuteAsync(ProjectProfile p, CancellationToken ct = default)
    {
        var sln = ProfilePaths.Solution(p) ?? throw new InvalidOperationException(".sln not found.");
        if (!string.IsNullOrWhiteSpace(p.IdePath))
            Process.Start(new ProcessStartInfo(p.IdePath) { ArgumentList = { sln }, UseShellExecute = false });
        else
            Process.Start(new ProcessStartInfo(sln) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}

public sealed class GenerateProjectFilesAction : ILaunchAction
{
    private readonly EngineLocator _engines;
    public GenerateProjectFilesAction(EngineLocator engines) => _engines = engines;

    public string Id => "genproj";
    public string DisplayName => "Generate Project Files";

    public bool CanExecute(ProjectProfile p)
    {
        var up = ProfilePaths.UProject(p);
        return up != null && File.Exists(up);
    }

    public async Task ExecuteAsync(ProjectProfile p, CancellationToken ct = default)
    {
        var up = ProfilePaths.UProject(p) ?? throw new InvalidOperationException(".uproject is not configured.");
        var uvs = _engines.FindVersionSelector(_engines.FindEngineRoot(p))
                  ?? throw new InvalidOperationException("UnrealVersionSelector.exe not found.");
        using var proc = Process.Start(new ProcessStartInfo(uvs) { ArgumentList = { "/projectfiles", up }, UseShellExecute = false })
                         ?? throw new InvalidOperationException("Failed to start UnrealVersionSelector.");
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"UnrealVersionSelector exited with code {proc.ExitCode}.");
    }
}

public sealed class OpenFolderAction : ILaunchAction
{
    public string Id => "folder";
    public string DisplayName => "Open Folder";
    public bool CanExecute(ProjectProfile p) => Directory.Exists(p.WorkspaceRoot);

    public Task ExecuteAsync(ProjectProfile p, CancellationToken ct = default)
    {
        // Полный путь: по голому имени Windows искала бы explorer.exe сначала в папке приложения.
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(new ProcessStartInfo(explorer) { ArgumentList = { p.WorkspaceRoot }, UseShellExecute = false });
        return Task.CompletedTask;
    }
}

/// <summary>Пользовательская команда из профиля с подстановкой токенов.</summary>
public sealed class CustomCommandAction : ILaunchAction
{
    private readonly CustomActionDef _def;
    private readonly EngineLocator _engines;

    public CustomCommandAction(CustomActionDef def, EngineLocator engines) { _def = def; _engines = engines; }

    /// <summary>Команда как записана в профиле (для подтверждения перед первым запуском).</summary>
    public string CommandLine => $"{_def.Command} {_def.Arguments}".Trim();

    public string Id => "custom:" + _def.Name;
    public string DisplayName => _def.Name;
    public bool CanExecute(ProjectProfile p) => true;

    public Task ExecuteAsync(ProjectProfile p, CancellationToken ct = default)
    {
        string Expand(string s) => s
            .Replace("{Root}", p.WorkspaceRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("{UProject}", ProfilePaths.UProject(p) ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{Solution}", ProfilePaths.Solution(p) ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{Client}", p.P4Client, StringComparison.OrdinalIgnoreCase)
            .Replace("{Engine}", _engines.FindEngineRoot(p) ?? "", StringComparison.OrdinalIgnoreCase);

        var wd = Expand(_def.WorkingDirectory);
        Process.Start(new ProcessStartInfo(Expand(_def.Command), Expand(_def.Arguments))
        {
            WorkingDirectory = Directory.Exists(wd) ? wd : p.WorkspaceRoot,
            UseShellExecute = true,
        });
        return Task.CompletedTask;
    }
}
