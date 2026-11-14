using PerforceLauncher.Core.Models;

namespace PerforceLauncher.Core.Launching;

/// <summary>Папка, которую можно безопасно очистить: размер, число файлов и чем это чревато.</summary>
public sealed record CleanupItem(string Id, string Title, string Description, string Path, long Bytes, int Files, bool Recommended)
{
    public bool Exists => Files > 0 || Bytes > 0;
}

public sealed record CleanupResult(long FreedBytes, IReadOnlyList<string> Failures);

/// <summary>
/// Что в проекте Unreal занимает место и восстанавливается сборкой: Intermediate, DerivedDataCache, Saved (логи, краши, автосохранения), Binaries.
/// Удаляется только из явного белого списка папок внутри папки проекта: ничего из Content, Source и Config не затрагивается.
/// </summary>
public static class CleanupScanner
{
    private sealed record Spec(string Id, string Title, string Description, string RelativePath, bool Recommended);

    private static readonly Spec[] Specs =
    {
        new("intermediate", "Intermediate", "Temporary build files. Unreal recreates them on the next build (the first build will be slow).", "Intermediate", true),
        new("ddc", "DerivedDataCache", "Local asset cache. It rebuilds itself, but the first editor start will be slow.", "DerivedDataCache", false),
        new("logs", "Saved\\Logs", "Editor logs.", @"Saved\Logs", true),
        new("crashes", "Saved\\Crashes", "Crash reports.", @"Saved\Crashes", true),
        new("autosaves", "Saved\\Autosaves", "Editor autosaves of maps and assets. Not needed once everything is saved.", @"Saved\Autosaves", true),
        new("backup", "Saved\\Backup", "Backup copies of assets made by the editor.", @"Saved\Backup", true),
        new("binaries", "Binaries", "Compiled project DLLs. The project must be built again after deleting them.", "Binaries", false),
    };

    /// <summary>Папки проекта и (отдельными пунктами) Intermediate/Binaries плагинов проекта с размерами.</summary>
    public static IReadOnlyList<CleanupItem> Scan(string uprojectPath)
    {
        var root = System.IO.Path.GetDirectoryName(uprojectPath)!;
        var items = new List<CleanupItem>();
        foreach (var spec in Specs)
        {
            var dir = System.IO.Path.Combine(root, spec.RelativePath);
            var (bytes, files) = Measure(dir);
            items.Add(new CleanupItem(spec.Id, spec.Title, spec.Description, dir, bytes, files, spec.Recommended));
        }

        // Плагины проекта: Intermediate и Binaries каждого плагина считаем одним пунктом (их десятки).
        foreach (var (id, title, sub, desc, rec) in new[]
                 {
                     ("plugin-intermediate", "Plugins\\*\\Intermediate", "Intermediate", "Temporary build files of the project plugins. A build recreates them.", true),
                     ("plugin-binaries", "Plugins\\*\\Binaries", "Binaries", "Compiled DLLs of the project plugins. A rebuild is needed after deleting them.", false),
                 })
        {
            long bytes = 0; var files = 0;
            var dirs = new List<string>();
            var pluginsDir = System.IO.Path.Combine(root, "Plugins");
            if (Directory.Exists(pluginsDir))
                foreach (var d in SafeDirs(pluginsDir, sub, maxDepth: 4))
                {
                    var (b, f) = Measure(d);
                    bytes += b; files += f; dirs.Add(d);
                }
            // Для пункта по плагинам Path — сама папка Plugins; список папок восстанавливается при удалении.
            items.Add(new CleanupItem(id, title, desc, pluginsDir, bytes, files, rec));
        }
        return items;
    }

    /// <summary>Удаляет выбранные пункты. Возвращает освобождённые байты и список того, что не удалось (файл занят и т.п.).</summary>
    public static CleanupResult Delete(string uprojectPath, IEnumerable<CleanupItem> items, Action<string>? progress = null)
    {
        var root = System.IO.Path.GetDirectoryName(uprojectPath)!;
        long freed = 0;
        var failures = new List<string>();
        foreach (var item in items)
        {
            progress?.Invoke(item.Title);
            var targets = item.Id switch
            {
                "plugin-intermediate" => SafeDirs(System.IO.Path.Combine(root, "Plugins"), "Intermediate", 4).ToList(),
                "plugin-binaries" => SafeDirs(System.IO.Path.Combine(root, "Plugins"), "Binaries", 4).ToList(),
                _ => Specs.Where(s => s.Id == item.Id).Select(s => System.IO.Path.Combine(root, s.RelativePath)).ToList(),
            };
            foreach (var dir in targets)
            {
                // Защита: только внутри папки проекта и только существующие папки из белого списка имён.
                if (!IsInside(root, dir) || !Directory.Exists(dir)) continue;
                var (bytes, _) = Measure(dir);
                try { Directory.Delete(dir, recursive: true); freed += bytes; }
                catch (Exception ex)
                {
                    // Часть файлов могла удалиться до ошибки: считаем, что освободилось то, чего больше нет.
                    var (left, _) = Measure(dir);
                    freed += Math.Max(0, bytes - left);
                    failures.Add($"{System.IO.Path.GetRelativePath(root, dir)}: {ex.Message}");
                }
            }
        }
        return new CleanupResult(freed, failures);
    }

    public static bool IsInside(string root, string path)
    {
        var r = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        var p = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase) && p.Length > r.Length;
    }

    private static IEnumerable<string> SafeDirs(string start, string name, int maxDepth)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((start, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { continue; }
            foreach (var s in subs)
            {
                var n = System.IO.Path.GetFileName(s);
                if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) { yield return s; continue; }   // внутрь найденной не заходим
                if (depth < maxDepth && n is not ("Source" or "Content" or "Resources")) stack.Push((s, depth + 1));
            }
        }
    }

    private static (long Bytes, int Files) Measure(string dir)
    {
        long bytes = 0; var files = 0;
        if (!Directory.Exists(dir)) return (0, 0);
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                try { bytes += f.Length; files++; } catch { /* файл исчез во время подсчёта */ }
            }
        }
        catch { /* папка недоступна: считаем пустой */ }
        return (bytes, files);
    }
}
