using System.Diagnostics;
using Microsoft.Win32;
using PerforceLauncher.Core.Models;

namespace PerforceLauncher.Core.Launching;

/// <summary>Какой IDE откроет .sln: по пути из профиля или по ассоциации .sln в Windows (только чтение реестра).</summary>
public static class IdeDetector
{
    public static string? Describe(ProjectProfile p)
    {
        if (!string.IsNullOrWhiteSpace(p.IdePath))
            return FromName(Path.GetFileNameWithoutExtension(p.IdePath)) ?? "IDE";
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.sln\UserChoice");
            if (FromName(choice?.GetValue("ProgId") as string) is { } fromChoice) return fromChoice;
            using var cls = Registry.ClassesRoot.OpenSubKey(".sln");
            return FromName(cls?.GetValue(null) as string);
        }
        catch { return null; }
    }

    private static string? FromName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (name.Contains("rider", StringComparison.OrdinalIgnoreCase)) return "Rider";
        if (name.Contains("devenv", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("VisualStudio", StringComparison.OrdinalIgnoreCase)) return "Visual Studio";
        return null;
    }
}

public static class ShellHelper
{
    private static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>Открывает Проводник с выделенным файлом (или папку, если файла уже нет).</summary>
    public static void Reveal(string path)
    {
        if (File.Exists(path))
        {
            // В путях Windows кавычек не бывает, поэтому обрамление безопасно.
            Process.Start(new ProcessStartInfo(Explorer, $"/select,\"{path}\"") { UseShellExecute = false });
            return;
        }
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (dir != null && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
        if (dir != null)
            Process.Start(new ProcessStartInfo(Explorer) { ArgumentList = { dir }, UseShellExecute = false });
    }
}
