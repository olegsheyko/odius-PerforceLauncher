using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace PerforceLauncher.Core.VersionControl;

public sealed record ProcessResult(int ExitCode, IReadOnlyList<string> StdOut, IReadOnlyList<string> StdErr);

public sealed record RawProcessResult(int ExitCode, byte[] StdOut, IReadOnlyList<string> StdErr);

/// <summary>
/// Запуск p4. Вывод читается байтами и декодируется построчно через <see cref="TextDecoding"/>:
/// так кириллица из не-Unicode сервера (CP1251) не превращается в «�».
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string exe,
        IEnumerable<string> args,
        string? stdin = null,
        Action<string>? onLine = null,
        CancellationToken ct = default,
        Encoding? stdinEncoding = null)
    {
        var stdout = new List<string>();
        var (exit, _, stderr) = await RunCoreAsync(exe, args, stdin, stdinEncoding, ct, line =>
        {
            lock (stdout) stdout.Add(line);
            onLine?.Invoke(line);
        }, onLine);
        return new ProcessResult(exit, stdout, stderr);
    }

    /// <summary>Сырые байты stdout (для «p4 -G»), stderr построчно.</summary>
    public static async Task<RawProcessResult> RunBytesAsync(string exe, IEnumerable<string> args, CancellationToken ct = default)
    {
        var (exit, bytes, stderr) = await RunCoreAsync(exe, args, null, null, ct, stdoutLine: null, onErrLine: null);
        return new RawProcessResult(exit, bytes, stderr);
    }

    private static async Task<(int Exit, byte[] StdOut, List<string> StdErr)> RunCoreAsync(
        string exe, IEnumerable<string> args, string? stdin, Encoding? stdinEncoding, CancellationToken ct,
        Action<string>? stdoutLine, Action<string>? onErrLine)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = stdinEncoding ?? new UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Win32Exception ex) { throw new P4Exception($"Cannot start '{exe}': {ex.Message}"); }

        var stderr = new List<string>();
        var rawOut = new MemoryStream();
        var outTask = stdoutLine != null
            ? ReadLinesAsync(p.StandardOutput.BaseStream, stdoutLine)
            : p.StandardOutput.BaseStream.CopyToAsync(rawOut);
        var errTask = ReadLinesAsync(p.StandardError.BaseStream, line =>
        {
            lock (stderr) stderr.Add(line);
            onErrLine?.Invoke(line);
        });

        if (stdin != null) await p.StandardInput.WriteLineAsync(stdin);
        p.StandardInput.Close();

        using var reg = ct.Register(() => { try { p.Kill(true); } catch { /* already exited */ } });
        await p.WaitForExitAsync(ct);
        await Task.WhenAll(outTask, errTask);
        return (p.ExitCode, rawOut.ToArray(), stderr);
    }

    /// <summary>Читает поток байтами, режет по \n и декодирует каждую строку отдельно.</summary>
    private static async Task ReadLinesAsync(Stream stream, Action<string> onLine)
    {
        var buffer = new byte[16 * 1024];
        var line = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                line.Write(buffer, start, i - start);
                Emit(line, onLine);
                start = i + 1;
            }
            line.Write(buffer, start, read - start);
        }
        if (line.Length > 0) Emit(line, onLine);
    }

    private static void Emit(MemoryStream line, Action<string> onLine)
    {
        var bytes = line.GetBuffer().AsSpan(0, (int)line.Length);
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r') bytes = bytes[..^1];
        onLine(TextDecoding.Decode(bytes));
        line.SetLength(0);
    }
}
