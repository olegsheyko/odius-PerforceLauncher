using Microsoft.Win32;

namespace PerforceLauncher.Core.Services;

public enum AutostartState
{
    Off,
    On,
    /// <summary>Запись есть, но выключена пользователем в «Диспетчер задач → Автозагрузка».</summary>
    DisabledInTaskManager,
}

/// <summary>
/// Автозапуск при входе в Windows через HKCU\...\Run (права администратора не нужны, действует только для
/// текущего пользователя). Единственное место, где приложение пишет в реестр; включается только в Settings.
/// Учитывает выключатель Диспетчера задач (Explorer\StartupApproved\Run).
/// </summary>
public static class AutostartManager
{
    public const string ArgTray = "--tray";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "PerforceLauncher";

    public static AutostartState GetState()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(ValueName) is not string) return AutostartState.Off;
        return IsDisabledInTaskManager() ? AutostartState.DisabledInTaskManager : AutostartState.On;
    }

    public static bool IsEnabled() => GetState() == AutostartState.On;

    /// <summary>
    /// Включает/выключает автозапуск. Явное включение из Settings снимает выключатель Диспетчера задач —
    /// пользователь только что сам поставил галочку.
    /// </summary>
    public static void Apply(bool enable, bool startMinimized = true, string? exePath = null)
    {
        if (enable)
        {
            WriteCommand(startMinimized, exePath);
            ClearTaskManagerFlag();
        }
        else
        {
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                run?.DeleteValue(ValueName, throwOnMissingValue: false);
            using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true))
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// При старте: обновить путь/флаг --tray (exe могли переместить), не трогая выключатель Диспетчера задач —
    /// если пользователь отключил автозапуск там, мы его молча обратно не включаем.
    /// </summary>
    public static void RefreshCommand(bool startMinimized, string? exePath = null)
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(ValueName) is not string) return;
        WriteCommand(startMinimized, exePath);
    }

    private static void WriteCommand(bool startMinimized, string? exePath)
    {
        exePath ??= Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath)) return;
        var command = startMinimized ? $"\"{exePath}\" {ArgTray}" : $"\"{exePath}\"";
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (!string.Equals(run.GetValue(ValueName) as string, command, StringComparison.Ordinal))
            run.SetValue(ValueName, command);
    }

    // StartupApproved: REG_BINARY из 12 байт; нечётный первый байт (0x03, 0x07) = выключено в Диспетчере задач.
    private static bool IsDisabledInTaskManager()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return approved?.GetValue(ValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1;
    }

    private static void ClearTaskManagerFlag()
    {
        if (!IsDisabledInTaskManager()) return;
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.SetValue(ValueName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
    }
}
