namespace PerforceLauncher.Core.VersionControl;

/// <summary>
/// Находит p4.exe по абсолютному пути. Папка самого лаунчера и относительные записи PATH
/// (в т.ч. «.») не используются: иначе подброшенный рядом p4.exe выполнялся бы с правами пользователя
/// и получал бы его Perforce-тикет.
/// </summary>
public static class P4Locator
{
    private static readonly object Gate = new();
    private static string? _cachedFor;
    private static string? _cachedPath;

    public static string Resolve(string? configured)
    {
        var name = string.IsNullOrWhiteSpace(configured) ? "p4" : configured.Trim().Trim('"');

        // Явный путь из настроек: используем как есть.
        if (name.Contains('\\') || name.Contains('/')) return Path.GetFullPath(name);

        lock (Gate)
        {
            if (_cachedFor == name && _cachedPath != null && File.Exists(_cachedPath)) return _cachedPath;

            var exe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
            var appDir = Normalize(AppContext.BaseDirectory);
            foreach (var raw in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var dir = raw.Trim().Trim('"');
                if (!Path.IsPathRooted(dir)) continue;                       // «.», «bin» и т.п.
                if (string.Equals(Normalize(dir), appDir, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var candidate = Path.Combine(dir, exe);
                    if (File.Exists(candidate)) { _cachedFor = name; return _cachedPath = candidate; }
                }
                catch { /* некорректная запись PATH */ }
            }
            return name;   // не найден: ошибка запуска будет показана пользователю
        }
    }

    private static string Normalize(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
}
