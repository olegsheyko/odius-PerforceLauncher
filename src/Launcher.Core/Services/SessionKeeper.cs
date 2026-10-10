using PerforceLauncher.Core.Models;
using PerforceLauncher.Core.VersionControl;

namespace PerforceLauncher.Core.Services;

/// <summary>
/// Держит Perforce-сессию живой: если для аккаунта сохранён пароль, перелогинивается заранее (когда тикету
/// осталось меньше <see cref="RenewBefore"/>) и сразу после ошибки авторизации. Тикет p4 общий для пользователя
/// и сервера, поэтому P4V и Unreal Editor тоже перестают разлогиниваться.
/// Неверный пароль пробуется ровно один раз: затем удаляется, чтобы не заблокировать учётную запись повторами.
/// </summary>
public sealed class SessionKeeper
{
    public const string TargetPrefix = "PerforceLauncher/";
    public static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TicketCheckEvery = TimeSpan.FromMinutes(5);

    private readonly IVersionControl _vcs;
    private readonly ICredentialStore _store;
    private readonly Dictionary<Guid, string> _targets = new();
    private readonly Dictionary<string, DateTime> _lastTicketCheck = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Сохранённый пароль не подошёл и удалён (аргумент — «user@server»).</summary>
    public event Action<string>? PasswordRejected;
    /// <summary>Сессия продлена автоматически (аргумент — «user@server»).</summary>
    public event Action<string>? Renewed;

    public SessionKeeper(IVersionControl vcs, ICredentialStore store)
    {
        _vcs = vcs;
        _store = store;
    }

    public static string AccountOf(string target) => target.StartsWith(TargetPrefix) ? target[TargetPrefix.Length..] : target;

    /// <summary>Ключ в Диспетчере учётных данных: «PerforceLauncher/user@server» (фактические значения из p4 info).</summary>
    public async Task<string> TargetForAsync(ProjectProfile p, CancellationToken ct = default)
    {
        if (_targets.TryGetValue(p.Id, out var cached)) return cached;
        var (user, server) = await _vcs.GetConnectionInfoAsync(p, ct);
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(server))
            throw new P4Exception("Cannot determine Perforce user/server for this project.");
        return _targets[p.Id] = $"{TargetPrefix}{user}@{server}";
    }

    public async Task<bool> HasSavedPasswordAsync(ProjectProfile p, CancellationToken ct = default) =>
        _store.Read(await TargetForAsync(p, ct)) != null;

    public async Task SavePasswordAsync(ProjectProfile p, string password, CancellationToken ct = default)
    {
        var target = await TargetForAsync(p, ct);
        _store.Save(target, AccountOf(target).Split('@')[0], password);
        _lastTicketCheck.Remove(target);
        _reportedMissing.Remove(target);
        DiagLog.Write($"Sign-in: password saved for {AccountOf(target)}; automatic sign-in enabled.");
    }

    public async Task ForgetAsync(ProjectProfile p, CancellationToken ct = default)
    {
        var target = await TargetForAsync(p, ct);
        if (_store.Read(target) == null) return;
        _store.Delete(target);
        DiagLog.Write($"Sign-in: saved password for {AccountOf(target)} removed (signed in without 'Remember').");
    }

    public IReadOnlyList<string> SavedAccounts() => _store.List(TargetPrefix).Select(AccountOf).ToList();

    public void ForgetAll()
    {
        foreach (var t in _store.List(TargetPrefix)) _store.Delete(t);
        _lastTicketCheck.Clear();
        DiagLog.Write("Sign-in: all saved passwords removed (Settings → Forget Saved Passwords).");
    }

    // Чтобы не писать в журнал «пароля нет» каждые 2 минуты, пока сессия истекла.
    private readonly HashSet<string> _reportedMissing = new();

    /// <summary>Перед проверкой: продлить сессию заранее, если тикет скоро истечёт. Без сохранённых паролей — ничего не делает.</summary>
    public async Task EnsureFreshAsync(ProjectProfile p, CancellationToken ct = default)
    {
        if (_store.List(TargetPrefix).Count == 0) return;   // никаких лишних вызовов p4, если функция не используется
        var target = await TargetForAsync(p, ct);
        var password = _store.Read(target);
        if (password == null) return;
        if (_lastTicketCheck.TryGetValue(target, out var last) && DateTime.UtcNow - last < TicketCheckEvery) return;
        _lastTicketCheck[target] = DateTime.UtcNow;

        var remaining = await _vcs.GetTicketRemainingAsync(p, ct);
        if (remaining is { } r && r > RenewBefore) return;
        await LoginWithSavedAsync(p, target, password, ct);
    }

    /// <summary>После ошибки авторизации: войти сохранённым паролем. true — сессия восстановлена.</summary>
    public async Task<bool> TryReloginAsync(ProjectProfile p, CancellationToken ct = default)
    {
        string target;
        try { target = await TargetForAsync(p, ct); }
        catch (P4Exception) { return false; }
        var password = _store.Read(target);
        if (password == null)
        {
            if (_reportedMissing.Add(target))
                DiagLog.Write($"Sign-in: session for {AccountOf(target)} expired, no saved password — automatic sign-in not possible.");
            return false;
        }
        return await LoginWithSavedAsync(p, target, password, ct);
    }

    private async Task<bool> LoginWithSavedAsync(ProjectProfile p, string target, string password, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Другой проект с тем же аккаунтом мог уже продлить тикет, пока мы ждали.
            if (await _vcs.GetTicketRemainingAsync(p, ct) is { } r && r > RenewBefore) return true;
            try
            {
                await _vcs.LoginAsync(p, password, ct);
                _lastTicketCheck[target] = DateTime.UtcNow;
                DiagLog.Write($"Sign-in: session for {AccountOf(target)} renewed automatically.");
                Renewed?.Invoke(AccountOf(target));
                return true;
            }
            catch (P4Exception ex) when (!IsTransient(ex.Message))
            {
                // Пароль отвергнут (сменили, учётку заблокировали...). Больше не пробуем — удаляем.
                _store.Delete(target);
                DiagLog.Write($"Sign-in: saved password for {AccountOf(target)} rejected by server and removed: {ex.Message.Split('\n')[0]}");
                PasswordRejected?.Invoke(AccountOf(target));
                return false;
            }
        }
        catch (P4Exception ex)
        {
            // сеть недоступна: пароль сохраняем, попробуем позже
            DiagLog.Write($"Sign-in: renewal for {AccountOf(target)} postponed (server unreachable): {ex.Message.Split('\n')[0]}");
            return false;
        }
        finally { _gate.Release(); }
    }

    private static bool IsTransient(string message)
    {
        var m = message.ToLowerInvariant();
        return m.Contains("connect to server failed") || m.Contains("tcp") || m.Contains("timed out") ||
               m.Contains("network") || m.Contains("unreachable") || m.Contains("cannot start");
    }
}
