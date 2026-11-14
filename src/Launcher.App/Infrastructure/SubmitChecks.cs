using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PerforceLauncher.Core.Launching;
using PerforceLauncher.Core.Models;

namespace PerforceLauncher.App.Infrastructure;

/// <summary>Предупреждения перед Submit: то, что обычно коммитят по ошибке (сгенерированные файлы, огромные файлы).</summary>
public static class SubmitChecks
{
    public const long LargeFileBytes = 100L * 1024 * 1024;
    private static readonly string[] GeneratedFolders = { "Intermediate", "Saved", "Binaries", "DerivedDataCache", ".vs" };
    private static readonly string[] JunkExtensions = { ".log", ".tmp", ".bak", ".orig", ".rej" };

    /// <summary>Строки-предупреждения; пусто, если всё в порядке. Файлы, помеченные .p4ignore, уже проверяются отдельно.</summary>
    public static IReadOnlyList<string> Evaluate(string workspaceRoot, IEnumerable<OpenedFile> files)
    {
        var issues = new List<string>();
        foreach (var f in files)
        {
            if (f.IsIgnored || f.Action.Contains("delete", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = ProfilePaths.ToRelative(workspaceRoot, f.LocalPath).Replace('\\', '/');
            var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var folder = parts.Take(Math.Max(0, parts.Length - 1)).FirstOrDefault(p => GeneratedFolders.Contains(p, StringComparer.OrdinalIgnoreCase));
            if (folder != null)
                issues.Add($"{rel}: generated or local file (inside {folder})");
            else if (JunkExtensions.Contains(Path.GetExtension(rel), StringComparer.OrdinalIgnoreCase))
                issues.Add($"{rel}: looks like a log or temporary file");

            try
            {
                if (File.Exists(f.LocalPath) && new FileInfo(f.LocalPath).Length is var size and >= LargeFileBytes)
                    issues.Add($"{rel}: very large file ({size / 1024 / 1024} MB)");
            }
            catch (IOException) { /* файл недоступен — размер не критичен для предупреждения */ }
        }
        return issues;
    }
}
