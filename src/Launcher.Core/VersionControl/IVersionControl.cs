using PerforceLauncher.Core.Models;

namespace PerforceLauncher.Core.VersionControl;

public sealed class P4Exception : Exception
{
    public bool IsAuthError { get; }
    public P4Exception(string message, bool isAuthError = false) : base(message) => IsAuthError = isAuthError;
}

/// <summary>
/// Абстракция над системой контроля версий. Сейчас есть одна реализация (Perforce),
/// но UI и ChangeWatcher зависят только от этого интерфейса.
/// </summary>
public interface IVersionControl
{
    Task<VcsStatus> GetStatusAsync(ProjectProfile profile, CancellationToken ct = default);
    Task<IReadOnlyList<WorkspaceInfo>> ListWorkspacesAsync(ProjectProfile profile, CancellationToken ct = default);
    /// <summary>Фактические пользователь и сервер (с учётом p4 set / P4CONFIG), для строки состояния.</summary>
    Task<(string User, string Server)> GetConnectionInfoAsync(ProjectProfile profile, CancellationToken ct = default);
    Task<WorkspaceInfo> GetWorkspaceAsync(ProjectProfile profile, string clientName, CancellationToken ct = default);
    Task SyncAsync(ProjectProfile profile, IProgress<string>? progress, CancellationToken ct = default);
    /// <summary>Синхронизировать воркспейс на состояние конкретного changelist'а (откат или промежуточная версия).</summary>
    Task SyncToChangeAsync(ProjectProfile profile, long changeId, IProgress<string>? progress, CancellationToken ct = default);
    Task LoginAsync(ProjectProfile profile, string password, CancellationToken ct = default);
    /// <summary>Сколько осталось жить тикету; null — не залогинен (тикета нет или истёк).</summary>
    Task<TimeSpan?> GetTicketRemainingAsync(ProjectProfile profile, CancellationToken ct = default);
    /// <summary>Делает воркспейс активным глобально (p4 set P4CLIENT), чтобы P4V/Unreal тоже его использовали.</summary>
    Task SetActiveWorkspaceAsync(ProjectProfile profile, string clientName, CancellationToken ct = default);

    // ---------- history ----------

    /// <summary>Submitted-коммиты воркспейса, новые первыми. beforeChange — для подгрузки следующей страницы.</summary>
    Task<IReadOnlyList<ChangeInfo>> GetHistoryAsync(ProjectProfile profile, int limit, long? beforeChange, CancellationToken ct = default);
    Task<IReadOnlyList<ChangeFile>> GetChangeFilesAsync(ProjectProfile profile, long changeId, CancellationToken ct = default);

    // ---------- P4IGNORE ----------

    /// <summary>Какие из локальных путей попадают под P4IGNORE: путь -> «файл:строка:правило».</summary>
    Task<IReadOnlyDictionary<string, string>> FindIgnoredAsync(ProjectProfile profile, IReadOnlyList<string> localPaths, CancellationToken ct = default);

    // ---------- pending changes / submit (как в P4V) ----------

    /// <summary>Открытые файлы, сгруппированные по changelist'ам (default первым).</summary>
    Task<IReadOnlyList<PendingChangelist>> GetPendingAsync(ProjectProfile profile, CancellationToken ct = default);
    /// <summary>Находит изменения вне Perforce (правки без checkout, новые/удалённые файлы) и открывает их в default.</summary>
    Task ReconcileAsync(ProjectProfile profile, IProgress<string>? progress, CancellationToken ct = default);
    Task<long> CreateChangelistAsync(ProjectProfile profile, string description, CancellationToken ct = default);
    Task UpdateChangelistDescriptionAsync(ProjectProfile profile, long changeId, string description, CancellationToken ct = default);
    Task DeleteChangelistAsync(ProjectProfile profile, long changeId, CancellationToken ct = default);
    /// <summary>Переносит файлы в changelist (0 = default).</summary>
    Task MoveFilesAsync(ProjectProfile profile, long targetChange, IReadOnlyList<string> depotPaths, CancellationToken ct = default);
    Task RevertAsync(ProjectProfile profile, IReadOnlyList<string> depotPaths, IProgress<string>? progress, CancellationToken ct = default);
    /// <summary>Сабмит нумерованного changelist'а.</summary>
    Task SubmitAsync(ProjectProfile profile, long changeId, IProgress<string>? progress, CancellationToken ct = default);
}
