using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PerforceLauncher.Core.Models;
using PerforceLauncher.Core.VersionControl;

namespace PerforceLauncher.App.Infrastructure;

/// <summary>P4Merge (ставится вместе с P4V): трёхстороннее слияние текстовых файлов.</summary>
public static class MergeTool
{
    /// <summary>Рядом с p4.exe, в Program Files\Perforce, затем в PATH (только абсолютные записи).</summary>
    public static string? Find(string p4Path)
    {
        var candidates = new System.Collections.Generic.List<string>();
        try { if (Path.GetDirectoryName(p4Path) is { Length: > 0 } dir) candidates.Add(Path.Combine(dir, "p4merge.exe")); } catch { }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            if (root.Length > 0) candidates.Add(Path.Combine(root, "Perforce", "p4merge.exe"));
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (Path.IsPathRooted(entry)) candidates.Add(Path.Combine(entry.Trim('"'), "p4merge.exe")); } catch { }
        }
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>
    /// Выгружает базу и «их» версию, запускает P4Merge и ждёт закрытия. Возвращает путь к сохранённому результату
    /// или null, если пользователь закрыл P4Merge, не сохранив результат.
    /// </summary>
    public static async Task<string?> MergeAsync(string tool, IVersionControl vcs, ProjectProfile profile, UnresolvedFile file, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PerforceLauncher", "merge", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var name = file.FileName;
        var baseFile = Path.Combine(dir, "base_" + name);
        var theirs = Path.Combine(dir, "theirs_" + name);
        var yours = Path.Combine(dir, "yours_" + name);
        var merged = Path.Combine(dir, "merged_" + name);

        await vcs.PrintRevisionAsync(profile, file.BaseFile, file.BaseRev, baseFile, ct);
        await vcs.PrintRevisionAsync(profile, file.FromFile, file.FromRev, theirs, ct);
        File.Copy(file.LocalPath, yours, overwrite: true);

        var started = DateTime.UtcNow;
        var psi = new ProcessStartInfo(tool) { UseShellExecute = false };
        foreach (var a in new[] { "-nb", $"Base (#{file.BaseRev})", "-nl", $"Theirs (#{file.FromRev})", "-nr", "Yours (your workspace)", "-nm", "Result",
                     baseFile, theirs, yours, merged })
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start P4Merge.");
        await proc.WaitForExitAsync(ct);

        // Результат считается сохранённым, если файл появился (или изменился) за время работы P4Merge.
        return File.Exists(merged) && File.GetLastWriteTimeUtc(merged) >= started.AddSeconds(-2) ? merged : null;
    }
}
