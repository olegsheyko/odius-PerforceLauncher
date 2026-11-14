using System.Text.RegularExpressions;

namespace PerforceLauncher.Core.Launching;

public enum BuildSeverity { Error, Warning }

/// <summary>Ошибка или предупреждение компилятора/линкера из вывода сборки.</summary>
public sealed record BuildIssue(BuildSeverity Severity, string File, int Line, int Column, string Code, string Message)
{
    public bool HasFile => File.Length > 0;
    public string Location => !HasFile ? "" : Line > 0 ? $"{System.IO.Path.GetFileName(File)}:{Line}" : System.IO.Path.GetFileName(File);
}

/// <summary>Разбор строк вывода Build.bat: MSVC («file(12,5): error C2065: …»), clang («file:12:5: error: …») и «ERROR: …» от UBT.</summary>
public static class BuildIssueParser
{
    private static readonly Regex Msvc = new(
        @"^\s*(?:\d+>)?(?<file>(?:[A-Za-z]:)?[^:()]+?)(?:\((?<line>\d+)(?:,(?<col>\d+))?\))?\s*:\s*(?<sev>fatal error|error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.+?)\s*$",
        RegexOptions.Compiled);
    private static readonly Regex Clang = new(
        @"^\s*(?<file>(?:[A-Za-z]:)?[^:]+?):(?<line>\d+):(?<col>\d+):\s*(?<sev>fatal error|error|warning):\s*(?<msg>.+?)\s*$",
        RegexOptions.Compiled);
    private static readonly Regex Ubt = new(@"^\s*(?<sev>ERROR|WARNING)\s*:\s*(?<msg>.+?)\s*$", RegexOptions.Compiled);

    public static BuildIssue? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var m = Msvc.Match(line);
        if (m.Success && LooksLikePath(m.Groups["file"].Value))
            return Make(m.Groups["sev"].Value, m.Groups["file"].Value, m.Groups["line"].Value, m.Groups["col"].Value, m.Groups["code"].Value, m.Groups["msg"].Value);
        m = Clang.Match(line);
        if (m.Success && LooksLikePath(m.Groups["file"].Value))
            return Make(m.Groups["sev"].Value, m.Groups["file"].Value, m.Groups["line"].Value, m.Groups["col"].Value, "", m.Groups["msg"].Value);
        m = Ubt.Match(line);
        if (m.Success) return Make(m.Groups["sev"].Value, "", "", "", "", m.Groups["msg"].Value);
        return null;
    }

    private static bool LooksLikePath(string s) => s.Contains('\\') || s.Contains('/') || System.IO.Path.HasExtension(s);

    private static BuildIssue Make(string sev, string file, string line, string col, string code, string msg)
    {
        int.TryParse(line, out var l);
        int.TryParse(col, out var c);
        var severity = sev.Equals("warning", StringComparison.OrdinalIgnoreCase) ? BuildSeverity.Warning : BuildSeverity.Error;
        return new BuildIssue(severity, file.Trim(), l, c, code, msg);
    }
}
