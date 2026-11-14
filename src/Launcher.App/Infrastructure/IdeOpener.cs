using System;
using System.Diagnostics;
using System.IO;
using PerforceLauncher.Core.Models;

namespace PerforceLauncher.App.Infrastructure;

/// <summary>Открывает файл в IDE проекта на нужной строке (Rider и VS Code умеют строку; остальные открывают файл как есть).</summary>
public static class IdeOpener
{
    public static void Open(ProjectProfile profile, string file, int line)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("The file doesn't exist in your workspace (yet).", file);
        var ide = profile.IdePath?.Trim() ?? "";
        var name = Path.GetFileNameWithoutExtension(ide).ToLowerInvariant();
        if (ide.Length > 0 && File.Exists(ide))
        {
            var psi = new ProcessStartInfo(ide) { UseShellExecute = false };
            if (name.Contains("rider") && line > 0) { psi.ArgumentList.Add("--line"); psi.ArgumentList.Add(line.ToString()); psi.ArgumentList.Add(file); }
            else if (name == "code" && line > 0) { psi.ArgumentList.Add("--goto"); psi.ArgumentList.Add($"{file}:{line}"); }
            else if (name.Contains("devenv")) { psi.ArgumentList.Add("/Edit"); psi.ArgumentList.Add(file); }
            else psi.ArgumentList.Add(file);
            Process.Start(psi);
            return;
        }
        // IDE не задана: пусть Windows откроет файл программой по умолчанию.
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }
}
