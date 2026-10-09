using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PerforceLauncher.Core.Models;

namespace PerforceLauncher.Core.Launching;

public static class ProfilePaths
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Intermediate", "Binaries", "Saved", "DerivedDataCache", "Content", "Plugins", "Source", "Build",
        ".git", ".vs", ".idea", "node_modules",
    };

    /// <summary>Явный путь из профиля, иначе автопоиск *.uproject в воркспейсе
    /// (корень, затем до 2 уровней вглубь).</summary>
    public static string? UProject(ProjectProfile p) =>
        !string.IsNullOrWhiteSpace(p.UProjectRelativePath)
            ? WithExtension(Combine(p, p.UProjectRelativePath), ".uproject")
            : FindInWorkspace(p.WorkspaceRoot, "*.uproject");

    public static string? Solution(ProjectProfile p)
    {
        if (!string.IsNullOrWhiteSpace(p.SolutionRelativePath))
            return WithExtension(Combine(p, p.SolutionRelativePath), ".sln");

        // Автопоиск: <папка uproject>\<имя>.sln -> любой .sln в папке -> любой .sln в воркспейсе.
        var up = UProject(p);
        if (up != null)
        {
            var dir = Path.GetDirectoryName(up)!;
            var byName = Path.Combine(dir, Path.GetFileNameWithoutExtension(up) + ".sln");
            if (File.Exists(byName)) return byName;
            var any = FindFirst(dir, "*.sln");
            if (any != null) return any;
        }
        return FindInWorkspace(p.WorkspaceRoot, "*.sln");
    }

    /// <summary>Эти пути открываются через оболочку Windows, поэтому принимаем только ожидаемый тип файла
    /// (иначе путь к .exe/.bat в профиле был бы запущен кнопкой «Launch Editor»).</summary>
    private static string? WithExtension(string? path, string ext) =>
        path != null && string.Equals(Path.GetExtension(path), ext, StringComparison.OrdinalIgnoreCase) ? path : null;

    private static string? FindInWorkspace(string root, string pattern)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        var level = new List<string> { root };
        for (var depth = 0; depth <= 2 && level.Count > 0; depth++)
        {
            foreach (var dir in level)
            {
                var hit = FindFirst(dir, pattern);
                if (hit != null) return hit;
            }
            level = level.SelectMany(SafeSubdirs).ToList();
        }
        return null;
    }

    private static string? FindFirst(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault() : null; }
        catch { return null; }
    }

    private static IEnumerable<string> SafeSubdirs(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir)
                .Where(d => !SkipDirs.Contains(Path.GetFileName(d))).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    private static string? Combine(ProjectProfile p, string rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return null;
        if (Path.IsPathRooted(rel)) return Path.GetFullPath(rel);
        if (string.IsNullOrWhiteSpace(p.WorkspaceRoot)) return null;
        return Path.GetFullPath(Path.Combine(p.WorkspaceRoot, rel));
    }

    /// <summary>Делает путь относительным к корню воркспейса, если он внутри него.</summary>
    public static string ToRelative(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root)) return path;
        var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var f = Path.GetFullPath(path);
        return f.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? f[r.Length..] : f;
    }
}

/// <summary>Определяет, каким движком открывать .uproject (EngineAssociation, реестр Epic, source build).</summary>
public sealed class EngineLocator
{
    public string? FindEngineRoot(ProjectProfile p)
    {
        if (!string.IsNullOrWhiteSpace(p.EngineRootOverride))
            return NormalizeEngineRoot(p.EngineRootOverride);

        var uproject = ProfilePaths.UProject(p);
        if (uproject == null || !File.Exists(uproject)) return null;

        var dir = Path.GetDirectoryName(uproject)!;
        var assoc = ReadAssociation(uproject);

        if (assoc == "") return WalkUp(dir);

        if (Regex.IsMatch(assoc, @"^\{?[0-9A-Fa-f\-]{32,38}\}?$"))
        {
            // Source/custom build: GUID в HKCU\Software\Epic Games\Unreal Engine\Builds
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\Unreal Engine\Builds");
            var v = k?.GetValue(assoc) as string;
            return v != null ? NormalizeEngineRoot(v) : null;
        }

        if (Regex.IsMatch(assoc, @"^\d+\.\d+"))
        {
            // Launcher build: HKLM\SOFTWARE\EpicGames\Unreal Engine\<ver>
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var k = hklm.OpenSubKey($@"SOFTWARE\EpicGames\Unreal Engine\{assoc}");
                if (k?.GetValue("InstalledDirectory") is string s) return NormalizeEngineRoot(s);
            }
            return null;
        }

        // Относительный/абсолютный путь к движку
        return NormalizeEngineRoot(Path.GetFullPath(Path.Combine(dir, assoc)));
    }

    /// <summary>Короткая подпись движка для кнопки запуска: «UE 5.4», «Source build», «Custom engine» или пусто.</summary>
    public string DescribeEngine(ProjectProfile p)
    {
        if (!string.IsNullOrWhiteSpace(p.EngineRootOverride)) return "Custom engine";
        var uproject = ProfilePaths.UProject(p);
        if (uproject == null || !File.Exists(uproject)) return "";
        var assoc = ReadAssociation(uproject);
        if (assoc == "") return "Source build";                              // движок рядом с проектом
        if (Regex.IsMatch(assoc, @"^\d+\.\d+")) return "UE " + assoc;       // сборка из Epic Launcher
        if (Regex.IsMatch(assoc, @"^\{?[0-9A-Fa-f\-]{32,38}\}?$")) return "Source build";
        return "Custom engine";
    }

    public string? FindEditorExe(string engineRoot)
    {
        var bin = Path.Combine(engineRoot, "Engine", "Binaries", "Win64");
        foreach (var name in new[] { "UnrealEditor.exe", "UE4Editor.exe" })
        {
            var path = Path.Combine(bin, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>UnrealVersionSelector.exe: из реестра (shell-команда .uproject) либо из самого движка.</summary>
    public string? FindVersionSelector(string? engineRoot)
    {
        using (var k = Registry.ClassesRoot.OpenSubKey(@"Unreal.ProjectFile\shell\rungenproj\command"))
        {
            if (k?.GetValue(null) is string cmd)
            {
                var m = Regex.Match(cmd, "^\"([^\"]+)\"");
                if (m.Success && File.Exists(m.Groups[1].Value)) return m.Groups[1].Value;
            }
        }
        if (engineRoot != null)
        {
            var p = Path.Combine(engineRoot, "Engine", "Binaries", "Win64", "UnrealVersionSelector-Win64-Shipping.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string ReadAssociation(string uproject)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(uproject),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.TryGetProperty("EngineAssociation", out var e) ? e.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    private static string? NormalizeEngineRoot(string path)
    {
        var t = path.TrimEnd('\\', '/');
        if (Path.GetFileName(t).Equals("Engine", StringComparison.OrdinalIgnoreCase))
            t = Path.GetDirectoryName(t) ?? t;
        return Directory.Exists(Path.Combine(t, "Engine")) ? t : null;
    }

    private static string? WalkUp(string dir)
    {
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "Engine", "Binaries"))) return d.FullName;
        return null;
    }
}
