namespace PerforceLauncher.Core.Services;

/// <summary>
/// Небольшой журнал диагностики в %AppData%\PerforceLauncher\launcher.log (переживает переустановку).
/// Только события, никаких паролей и тикетов. Файл обрезается до последних ~200 КБ.
/// </summary>
public static class DiagLog
{
    private const long MaxBytes = 200 * 1024;
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerforceLauncher", "launcher.log");

    /// <summary>Тесты отключают запись, чтобы не засорять настоящий журнал пользователя.</summary>
    public static bool Enabled { get; set; } = true;

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
                var info = new FileInfo(FilePath);
                if (info.Length > MaxBytes)
                {
                    var tail = File.ReadAllLines(FilePath).Reverse().Take(1500).Reverse().ToArray();
                    File.WriteAllLines(FilePath, tail);
                }
            }
        }
        catch { /* журнал не должен ломать приложение */ }
    }
}
