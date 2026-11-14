using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PerforceLauncher.Core.Launching;

namespace PerforceLauncher.App.Infrastructure;

/// <summary>
/// Копии локальных файлов перед операциями, которые их перезаписывают (Get Server Version, Take Theirs, результат слияния):
/// %LocalAppData%\PerforceLauncher\Backups\Проект\дата-время\относительный путь. Вне воркспейса, чтобы не попасть в reconcile.
/// </summary>
public static class LocalBackup
{
    public static string Save(string projectName, string workspaceRoot, IEnumerable<string> files)
    {
        var safeName = string.Concat(projectName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PerforceLauncher", "Backups", safeName, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        foreach (var f in files)
        {
            if (!File.Exists(f)) continue;
            var rel = ProfilePaths.ToRelative(workspaceRoot, f);
            if (Path.IsPathRooted(rel)) rel = rel.Replace(":", "");   // файл вне корня воркспейса
            var target = Path.Combine(dir, rel.TrimStart('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: true);
        }
        return dir;
    }
}
