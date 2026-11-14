using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using PerforceLauncher.Core.Models;
using PerforceLauncher.Core.VersionControl;

namespace PerforceLauncher.App.Infrastructure;

/// <summary>Сравнение ревизий файла в P4Merge: ревизия с ревизией или ревизия с локальным файлом. Бинарные файлы построчно не сравниваются.</summary>
public sealed class DiffService
{
    private readonly IVersionControl _vcs;
    private readonly Func<string> _p4Path;

    public DiffService(IVersionControl vcs, Func<string> p4Path)
    {
        _vcs = vcs;
        _p4Path = p4Path;
    }

    public bool HasTool => MergeTool.Find(_p4Path()) != null;

    public static bool IsTextType(string type) =>
        type.StartsWith("text", StringComparison.OrdinalIgnoreCase) || type.StartsWith("unicode", StringComparison.OrdinalIgnoreCase) ||
        type.StartsWith("utf8", StringComparison.OrdinalIgnoreCase) || type.StartsWith("utf16", StringComparison.OrdinalIgnoreCase);

    private string RequireTool(string type)
    {
        if (type.Length > 0 && !IsTextType(type))
            throw new InvalidOperationException("This is a binary file (for example .uasset or .umap): it can't be compared line by line.");
        return MergeTool.Find(_p4Path()) ?? throw new InvalidOperationException("P4Merge was not found (it comes with P4V). Install P4V or put p4merge.exe next to p4.exe.");
    }

    /// <summary>Левая и правая ревизии из депо.</summary>
    public async Task DiffRevisionsAsync(ProjectProfile profile, string depotPath, int leftRev, int rightRev, string type)
    {
        var tool = RequireTool(type);
        var dir = NewTempDir();
        var name = depotPath[(depotPath.LastIndexOf('/') + 1)..];
        var left = Path.Combine(dir, $"{name}#{leftRev}");
        var right = Path.Combine(dir, $"{name}#{rightRev}");
        await _vcs.PrintRevisionAsync(profile, depotPath, leftRev, left);
        await _vcs.PrintRevisionAsync(profile, depotPath, rightRev, right);
        Launch(tool, dir, $"{name}#{leftRev}", $"{name}#{rightRev}", left, right);
    }

    /// <summary>Ревизия из депо слева, локальный файл справа (в P4Merge его можно править).</summary>
    public async Task DiffWithLocalAsync(ProjectProfile profile, string depotPath, int rev, string localPath, string type)
    {
        var tool = RequireTool(type);
        if (!File.Exists(localPath)) throw new FileNotFoundException("The local file doesn't exist.", localPath);
        var dir = NewTempDir();
        var name = depotPath[(depotPath.LastIndexOf('/') + 1)..];
        var left = Path.Combine(dir, $"{name}#{rev}");
        await _vcs.PrintRevisionAsync(profile, depotPath, rev, left);
        Launch(tool, dir, $"{name}#{rev} (depot)", "Your workspace file", left, localPath);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PerforceLauncher", "diff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Запускает P4Merge и не ждёт его: окно сравнения живёт само, временные файлы чистятся, когда его закроют.</summary>
    private static void Launch(string tool, string tempDir, string leftLabel, string rightLabel, string left, string right)
    {
        var psi = new ProcessStartInfo(tool) { UseShellExecute = false };
        foreach (var a in new[] { "-nl", leftLabel, "-nr", rightLabel, left, right }) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start P4Merge.");
        _ = Task.Run(async () =>
        {
            try { await proc.WaitForExitAsync(); } catch { }
            try { Directory.Delete(tempDir, recursive: true); } catch { /* файл ещё занят — останется во временной папке */ }
            proc.Dispose();
        });
    }
}
