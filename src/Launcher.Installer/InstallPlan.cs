using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace PerforceLauncher.Installer;

/// <summary>Куда ставить и можно ли туда ставить: проверки до запуска Velopack Setup.</summary>
internal static class InstallPlan
{
    public const long RequiredBytes = 400L * 1024 * 1024;   // ~170 МБ программа + место под скачанные обновления
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PerforceLauncher";

    public static string DefaultLocation =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PerforceLauncher");

    /// <summary>Папка уже установленного лаунчера (Velopack пишет её в ключ удаления программы).</summary>
    public static string? ExistingLocation()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            var path = key?.GetValue("InstallLocation") as string;
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Проверяет папку. Возвращает текст ошибки или null, если ставить можно.</summary>
    public static string? Validate(string raw, out string normalized)
    {
        normalized = "";
        var text = (raw ?? "").Trim().Trim('"');
        if (text.Length == 0) return "Choose a folder to install to.";
        try
        {
            if (!Path.IsPathRooted(text) || text.StartsWith(@"\\", StringComparison.Ordinal))
                return "Use a full local path, for example D:\\Tools\\PerforceLauncher.";
            normalized = Path.GetFullPath(text).TrimEnd('\\');
        }
        catch { return "This is not a valid folder path."; }
        if (normalized.Length <= 3) return "Choose a folder, not the root of a drive (for example D:\\Tools\\PerforceLauncher).";

        // Program Files и Windows требуют прав администратора на каждое обновление — автообновление там не работает.
        foreach (var protectedRoot in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                 }.Where(p => !string.IsNullOrEmpty(p)))
        {
            if (normalized.StartsWith(protectedRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || SamePath(normalized, protectedRoot))
                return "Program Files and Windows need administrator rights for every update, so automatic updates would stop working. Choose a folder in your user profile or on another drive.";
        }

        var existing = ExistingLocation();
        if (existing != null && !SamePath(existing, normalized))
            return $"Perforce Launcher is already installed in {existing}. To move it, uninstall it first (Settings → Apps; your projects and settings are kept), then run this installer again.";

        var isUpdateOfExisting = existing != null;
        if (!isUpdateOfExisting && Directory.Exists(normalized) && Directory.EnumerateFileSystemEntries(normalized).Any())
            return "This folder isn't empty. Choose an empty or new folder, for example " + Path.Combine(normalized, "PerforceLauncher") + ".";

        // Хватит ли места
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(normalized)!);
            if (!drive.IsReady) return $"Drive {drive.Name} is not available.";
            if (drive.AvailableFreeSpace < RequiredBytes)
                return $"Not enough free space on {drive.Name}: about 400 MB is needed.";
        }
        catch { return "Can't access this drive."; }

        // Можно ли писать: обновления ставятся без прав администратора, поэтому папка должна быть доступна пользователю.
        return CanWrite(normalized) ? null : "You don't have permission to write to this folder. Choose a folder in your user profile or on another drive.";
    }

    private static bool CanWrite(string dir)
    {
        var created = !Directory.Exists(dir);
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
        finally
        {
            // Пустую папку, созданную только для проверки, убираем (её создаст сам установщик).
            if (created) try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }
    }

    public static string FreeSpaceText(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path.Trim().Trim('"')));
            if (string.IsNullOrEmpty(root)) return "";
            var drive = new DriveInfo(root);
            return drive.IsReady ? $"Needs about 170 MB · {drive.AvailableFreeSpace / 1024d / 1024 / 1024:0.#} GB free on {drive.Name.TrimEnd('\\')}" : "";
        }
        catch { return ""; }
    }
}
