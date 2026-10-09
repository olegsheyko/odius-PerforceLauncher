using System.Text.Json;
using System.Text.Json.Serialization;
using PerforceLauncher.Core.Models;
using PerforceLauncher.Core.VersionControl;

namespace PerforceLauncher.Core.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; }

    public SettingsStore(string? path = null) =>
        FilePath = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerforceLauncher", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch
        {
            // Битый файл: сохраняем копию, иначе следующий Save затёр бы все профили пустыми настройками.
            try { File.Copy(FilePath, $"{FilePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); } catch { }
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

public interface INotifier
{
    void Notify(string title, string message, Guid? profileId = null);
}

public sealed class NewChangesEventArgs : EventArgs
{
    public NewChangesEventArgs(ProjectProfile profile, VcsStatus status) { Profile = profile; Status = status; }
    public ProjectProfile Profile { get; }
    public VcsStatus Status { get; }
}

/// <summary>
/// Фоновый опрос всех профилей. Каждый профиль опрашивается со своим интервалом,
/// запросы идут последовательно (не нагружаем сервер). События приходят в SynchronizationContext,
/// в котором создан объект (UI-поток).
/// </summary>
public sealed class ChangeWatcher : IDisposable
{
    private readonly IVersionControl _vcs;
    private readonly Func<IReadOnlyList<ProjectProfile>> _profiles;
    private readonly SynchronizationContext? _ctx = SynchronizationContext.Current;
    private readonly Dictionary<Guid, DateTime> _lastCheck = new();
    private readonly Dictionary<Guid, long> _lastNotified = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    public event EventHandler<ProfileCheckResult>? Checked;
    public event EventHandler<NewChangesEventArgs>? NewChangesFound;

    private readonly SessionKeeper? _session;

    public ChangeWatcher(IVersionControl vcs, Func<IReadOnlyList<ProjectProfile>> profiles, SessionKeeper? session = null)
    {
        _vcs = vcs;
        _profiles = profiles;
        _session = session;
    }

    /// <summary>Проверка статуса с автоматическим продлением сессии (если пароль сохранён).</summary>
    private async Task<VcsStatus> GetStatusKeepingSessionAsync(ProjectProfile profile)
    {
        if (_session != null)
        {
            try { await _session.EnsureFreshAsync(profile, _cts.Token); }
            catch (P4Exception) { /* продление вторично: ошибку покажет сама проверка ниже */ }
        }
        try { return await _vcs.GetStatusAsync(profile, _cts.Token); }
        catch (P4Exception ex) when (ex.IsAuthError && _session != null)
        {
            // Тикет уже истёк (например, компьютер спал дольше срока): входим сохранённым паролем и повторяем один раз.
            if (!await _session.TryReloginAsync(profile, _cts.Token)) throw;
            return await _vcs.GetStatusAsync(profile, _cts.Token);
        }
    }

    public void Start() => _ = LoopAsync(_cts.Token);

    /// <summary>Сбросить «уже уведомляли» (например, после sync или смены воркспейса).</summary>
    public void ResetNotified(Guid profileId) => _lastNotified.Remove(profileId);

    /// <summary>Сбросить «уже уведомляли» у всех профилей (например, после выхода из сна: напомнить о непрочитанном).</summary>
    public void ResetAllNotified() => _lastNotified.Clear();

    // Профили, у которых последняя проверка упала не из-за логина (чаще всего сеть ещё не поднялась после
    // пробуждения/входа в систему): их перепроверяем быстро, а не ждём полный интервал.
    private readonly HashSet<Guid> _quickRetry = new();
    private static readonly TimeSpan QuickRetryInterval = TimeSpan.FromSeconds(30);

    public async Task<ProfileCheckResult> CheckNowAsync(ProjectProfile profile)
    {
        await _gate.WaitAsync(_cts.Token);
        try
        {
            _lastCheck[profile.Id] = DateTime.UtcNow;
            ProfileCheckResult result;
            try
            {
                var st = await GetStatusKeepingSessionAsync(profile);
                result = new ProfileCheckResult(profile, st, null, false);
            }
            catch (P4Exception ex) { result = new ProfileCheckResult(profile, null, ex.Message, ex.IsAuthError); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result = new ProfileCheckResult(profile, null, ex.Message, false); }

            if (result.Error != null && !result.AuthRequired) _quickRetry.Add(profile.Id); else _quickRetry.Remove(profile.Id);

            Post(() =>
            {
                Checked?.Invoke(this, result);
                var st = result.Status;
                if (st is { IsBehind: true } && profile.NotificationsEnabled &&
                    (!_lastNotified.TryGetValue(profile.Id, out var last) || st.LatestChange > last))
                {
                    _lastNotified[profile.Id] = st.LatestChange;
                    NewChangesFound?.Invoke(this, new NewChangesEventArgs(profile, st));
                }
            });
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            do
            {
                foreach (var p in _profiles().ToList())
                {
                    var interval = TimeSpan.FromSeconds(Math.Max(15, p.PollIntervalSeconds));
                    if (_quickRetry.Contains(p.Id) && interval > QuickRetryInterval) interval = QuickRetryInterval;
                    if (_lastCheck.TryGetValue(p.Id, out var t) && DateTime.UtcNow - t < interval) continue;
                    await CheckNowAsync(p);
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    private void Post(Action a)
    {
        if (_ctx != null) _ctx.Post(_ => a(), null); else a();
    }

    public void Dispose() => _cts.Cancel();
}
