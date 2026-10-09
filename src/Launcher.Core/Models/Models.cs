namespace PerforceLauncher.Core.Models;

/// <summary>Пользовательская команда, которая отображается кнопкой в профиле.</summary>
/// <remarks>Токены в Command/Arguments/WorkingDirectory: {Root} {UProject} {Solution} {Client} {Engine}.</remarks>
public sealed class CustomActionDef
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
}

/// <summary>Один профиль = проект + воркспейс. Можно держать сколько угодно профилей.</summary>
public sealed class ProjectProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New project";

    // Perforce. Пустые значения -> берутся из окружения/p4 set.
    public string P4Port { get; set; } = "";
    public string P4User { get; set; } = "";
    public string P4Client { get; set; } = "";

    /// <summary>Корень воркспейса (кэшируется из p4 client -o).</summary>
    public string WorkspaceRoot { get; set; } = "";

    /// <summary>Путь в депо для проверки коммитов. Пусто -> //{client}/... (весь view воркспейса).</summary>
    public string DepotScope { get; set; } = "";

    // Пути относительно WorkspaceRoot, поэтому переживают смену воркспейса с тем же layout.
    public string UProjectRelativePath { get; set; } = "";
    public string SolutionRelativePath { get; set; } = "";

    public string EngineRootOverride { get; set; } = "";
    /// <summary>Исполняемый файл IDE (Rider/VS). Пусто -> ассоциация Windows для .sln.</summary>
    public string IdePath { get; set; } = "";

    public int PollIntervalSeconds { get; set; } = 120;
    public bool NotificationsEnabled { get; set; } = true;

    public List<CustomActionDef> CustomActions { get; set; } = new();

    public ProjectProfile Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        return System.Text.Json.JsonSerializer.Deserialize<ProjectProfile>(json)!;
    }
}

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

public sealed class AppSettings
{
    public string P4Path { get; set; } = "p4";
    public bool MinimizeToTray { get; set; } = true;
    public bool ApplyP4SetOnSwitch { get; set; } = true;
    /// <summary>Запускать лаунчер при входе в Windows (свёрнутым в трей), чтобы сразу напомнить о новых коммитах.</summary>
    public bool StartWithWindows { get; set; }
    /// <summary>При автозапуске не показывать окно, а сразу свернуться в трей.</summary>
    public bool StartMinimized { get; set; } = true;
    /// <summary>Общее уведомление о новых коммитах при входе в систему и после выхода из сна.</summary>
    public bool RemindOnWake { get; set; } = true;
    /// <summary>Положение и размер главного окна между запусками.</summary>
    public WindowPlacement? Window { get; set; }
    public Guid? SelectedProfileId { get; set; }
    public List<ProjectProfile> Profiles { get; set; } = new();
}

public sealed record ChangeInfo(long Id, string User, string Client, DateTime Time, string Description)
{
    public string FirstLine
    {
        get
        {
            var d = Description.Trim();
            var i = d.IndexOfAny(new[] { '\r', '\n' });
            return i < 0 ? d : d[..i];
        }
    }
}

public sealed record WorkspaceInfo(string Name, string Root, string Host, string Stream, string Description, bool IsLocalHost);

public sealed record VcsStatus(
    long HaveChange,
    long LatestChange,
    IReadOnlyList<ChangeInfo> NewChanges,
    bool HasMoreChanges,
    WorkspaceInfo Workspace,
    DateTime CheckedAt)
{
    public bool IsBehind => LatestChange > HaveChange;
}

/// <summary>Файл, открытый в воркспейсе (p4 opened). Change == 0 — default changelist.</summary>
/// <remarks>IgnoredBy != null: файл подпадает под P4IGNORE, но уже находится в депо (формат «файл:строка:правило»).</remarks>
public sealed record OpenedFile(string DepotPath, string LocalPath, string Action, long Change, string Type, int Rev, string? IgnoredBy = null)
{
    public bool IsIgnored => IgnoredBy != null;
}

/// <summary>Файл внутри submitted-changelist'а (p4 describe).</summary>
public sealed record ChangeFile(string DepotPath, string Action, int Rev, string Type);

/// <summary>Pending changelist воркспейса. Id == 0 — default changelist (всегда присутствует).</summary>
public sealed record PendingChangelist(long Id, string Description, IReadOnlyList<OpenedFile> Files)
{
    public bool IsDefault => Id == 0;
    public string Title => IsDefault ? "Default changelist" : $"CL {Id}";
}

public sealed record ProfileCheckResult(ProjectProfile Profile, VcsStatus? Status, string? Error, bool AuthRequired);
