using System.IO;
using System.Linq;
using System.Windows.Input;
using PerforceLauncher.App.Infrastructure;
using PerforceLauncher.Core.Launching;
using PerforceLauncher.Core.Models;

namespace PerforceLauncher.App.ViewModels;

/// <summary>Кнопка действия. Подписи зависят от профиля: «Open in Rider», «Launch Unreal · UE 5.4».</summary>
public sealed class ActionViewModel : ObservableObject
{
    private readonly ProjectProfile _profile;
    private readonly string? _ide;

    public ActionViewModel(ILaunchAction action, ICommand command, ProjectProfile profile)
    {
        Action = action;
        Command = command;
        _profile = profile;
        if (action.Id == "solution") _ide = IdeDetector.Describe(profile);
    }

    public ILaunchAction Action { get; }
    public ICommand Command { get; }

    /// <summary>Запуск редактора — главная (hero) кнопка.</summary>
    public bool IsPrimary => Action.Id == "editor";

    public string Name => Action.Id switch
    {
        "editor" => "Launch Unreal",
        "solution" => _ide != null ? $"Open in {_ide}" : "Open Solution",
        "genproj" => "Generate Project Files",
        "folder" => "Show in Explorer",
        _ => Action.DisplayName,
    };

    /// <summary>Вторая строка главной кнопки: движок и имя проекта.</summary>
    public string Detail
    {
        get
        {
            if (!IsPrimary) return "";
            var engine = new EngineLocator().DescribeEngine(_profile);
            var project = ProfilePaths.UProject(_profile) is { } up ? Path.GetFileNameWithoutExtension(up) : "";
            return string.Join(" · ", new[] { engine, project }.Where(s => s.Length > 0));
        }
    }

    public string Glyph => Action.Id switch
    {
        "editor" => "",    // Play
        "solution" => "",  // Code
        "genproj" => "",   // Repair
        "folder" => "",    // FolderOpen
        _ => "",           // CommandPrompt
    };

    /// <summary>Подсказка; для недоступной кнопки объясняет, почему она недоступна.</summary>
    public string ToolTipText
    {
        get
        {
            var ok = Action.CanExecute(_profile);
            return Action.Id switch
            {
                "editor" => ok ? "Open the project in Unreal Editor (Ctrl+Enter)" : "No .uproject found in this workspace. Set it in Project Settings.",
                "solution" => ok ? $"Open {Path.GetFileName(ProfilePaths.Solution(_profile))}" : "No .sln yet. Run Generate Project Files to create it.",
                "genproj" => ok ? "Regenerate the .sln and IDE project files (UnrealVersionSelector /projectfiles)" : "No .uproject found in this workspace.",
                "folder" => "Open the workspace folder",
                _ => Action is CustomCommandAction c ? c.CommandLine : Action.DisplayName,
            };
        }
    }

    /// <summary>Пересчитать подписи (после проверки/синка файлы могли появиться или пропасть).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
