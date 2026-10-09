using System;
using System.Reflection;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace PerforceLauncher.App.Infrastructure;

/// <summary>
/// Автообновление из GitHub Releases (Velopack): проверка, фоновая загрузка (только изменения),
/// установка с перезапуском. Работает только в установленной версии — сборка из dist\ не обновляется.
/// </summary>
public sealed class AppUpdater
{
    public const string RepoUrl = "https://github.com/olegsheyko/odius-PerforceLauncher";

    private readonly UpdateManager? _manager;
    private UpdateInfo? _ready;

    /// <summary>Обновление скачано и готово к установке (аргумент — версия).</summary>
    public event Action<string>? UpdateReady;

    public AppUpdater()
    {
        try { _manager = new UpdateManager(new GithubSource(RepoUrl, null, false)); }
        catch { _manager = null; }   // нестандартный запуск: обновления просто выключены
    }

    public bool IsInstalled => _manager?.IsInstalled == true;

    public string CurrentVersion =>
        _manager?.CurrentVersion?.ToString()
        ?? Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "dev";

    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();

    /// <summary>Проверить и скачать. Возвращает версию готового обновления или null, если обновлений нет.</summary>
    public async Task<string?> CheckAndDownloadAsync()
    {
        if (_manager == null || !IsInstalled) return null;
        if (_ready != null) return ReadyVersion;
        var info = await _manager.CheckForUpdatesAsync();
        if (info == null) return null;
        await _manager.DownloadUpdatesAsync(info, null);
        _ready = info;
        var version = info.TargetFullRelease.Version.ToString();
        UpdateReady?.Invoke(version);
        return version;
    }

    /// <summary>Установить скачанное обновление и перезапуститься (в трей или с окном).</summary>
    public void ApplyAndRestart(bool startInTray)
    {
        if (_manager == null || _ready == null) return;
        _manager.ApplyUpdatesAndRestart(_ready.TargetFullRelease,
            startInTray ? new[] { PerforceLauncher.Core.Services.AutostartManager.ArgTray } : Array.Empty<string>());
    }
}
