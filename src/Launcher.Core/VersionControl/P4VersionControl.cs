using System.Text.Json.Nodes;
using PerforceLauncher.Core.Models;

namespace PerforceLauncher.Core.VersionControl;

/// <summary>Реализация поверх p4 CLI (-ztag -Mj). Каждый вызов явно получает -p/-u/-c из профиля,
/// поэтому профили не влияют друг на друга и не трогают глобальные настройки.</summary>
public sealed class P4VersionControl : IVersionControl
{
    private const int MaxNewChanges = 100;
    private readonly Func<string> _p4Path;

    public P4VersionControl(Func<string> p4Path) => _p4Path = p4Path;

    /// <summary>Только абсолютный путь: голое имя Windows искало бы в папке приложения и текущей папке (подмена p4.exe).</summary>
    private string P4Exe()
    {
        var path = _p4Path();
        if (!Path.IsPathRooted(path))
            throw new P4Exception("p4.exe was not found in PATH. Install the Perforce command-line client or set its full path in Settings.");
        return path;
    }

    // ---------- public API ----------

    public async Task<VcsStatus> GetStatusAsync(ProjectProfile profile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.P4Client))
            throw new P4Exception("Workspace (P4CLIENT) is not set for this profile.");

        var ws = await GetWorkspaceAsync(profile, profile.P4Client, ct);
        var scope = Scope(profile);

        var have = await QueryChangesAsync(profile, ct, "-m1", "-s", "submitted", scope + "#have");
        var latest = await QueryChangesAsync(profile, ct, "-m1", "-s", "submitted", scope);

        long haveCl = have.FirstOrDefault()?.Id ?? 0;
        long latestCl = latest.FirstOrDefault()?.Id ?? haveCl;

        IReadOnlyList<ChangeInfo> news = Array.Empty<ChangeInfo>();
        bool more = false;
        if (latestCl > haveCl)
        {
            // «#have» врёт для changelist'ов, состоящих только из удалений: удалённого файла нет в have-списке,
            // поэтому после sync «have» навсегда застревает на предыдущем CL (а свои коммиты выглядят «новыми»).
            // cstat знает статус каждого CL: have / partial / need.
            HashSet<long>? needed = null;
            try
            {
                var rows = await RunTaggedAsync(profile, ct, "cstat", $"{scope}@>{haveCl}");
                var statuses = rows
                    .Select(r => (Ok: long.TryParse(Str(r, "change"), out var id), Id: id, Status: Str(r, "status")))
                    .Where(x => x.Ok).ToList();
                needed = statuses.Where(x => x.Status != "have").Select(x => x.Id).ToHashSet();
                if (needed.Count == 0)
                    haveCl = latestCl;   // всё синхронизировано
                else
                {
                    var firstNeeded = needed.Min();
                    haveCl = Math.Max(haveCl, statuses.Where(x => x.Status == "have" && x.Id < firstNeeded)
                        .Select(x => x.Id).DefaultIfEmpty(haveCl).Max());
                }
            }
            catch (P4Exception) { /* старый сервер без cstat: остаёмся на «#have» */ }

            if (haveCl < latestCl)
            {
                var candidates = await QueryChangesAsync(profile, ct, "-l", "-m", MaxNewChanges.ToString(),
                    "-s", "submitted", $"{scope}@>{haveCl}");
                news = needed == null ? candidates : candidates.Where(c => needed.Contains(c.Id)).ToList();
                more = candidates.Count >= MaxNewChanges;
            }
        }

        return new VcsStatus(haveCl, latestCl, news, more, ws, DateTime.Now);
    }

    public async Task<(string User, string Server)> GetConnectionInfoAsync(ProjectProfile profile, CancellationToken ct = default)
    {
        var info = (await RunTaggedAsync(profile, ct, "info")).FirstOrDefault();
        var server = Str(info, "serverAddress");
        if (server == "") server = Str(info, "port");
        return (Str(info, "userName"), server);
    }

    public async Task<WorkspaceInfo> GetWorkspaceAsync(ProjectProfile profile, string clientName, CancellationToken ct = default)
    {
        CheckToken("Workspace", clientName);
        var rows = await RunTaggedAsync(profile, ct, "client", "-o", clientName);
        var o = rows.FirstOrDefault() ?? throw new P4Exception($"Workspace '{clientName}' not found.");
        // Для несуществующего клиента p4 возвращает шаблон без Update/Access.
        if (Str(o, "Update") == "" && Str(o, "Access") == "")
            throw new P4Exception($"Workspace '{clientName}' does not exist on the server.");
        return ToWorkspace(o, clientName);
    }

    public async Task<IReadOnlyList<WorkspaceInfo>> ListWorkspacesAsync(ProjectProfile profile, CancellationToken ct = default)
    {
        var user = profile.P4User;
        if (string.IsNullOrWhiteSpace(user))
        {
            var info = await RunTaggedAsync(profile, ct, "info");
            user = Str(info.FirstOrDefault(), "userName");
        }
        var rows = await RunTaggedAsync(profile, ct, "clients", "-u", user);
        return rows
            .Select(r => ToWorkspace(r, Str(r, "client")))
            .OrderByDescending(w => w.IsLocalHost)
            .ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task SyncAsync(ProjectProfile profile, IProgress<string>? progress, CancellationToken ct = default) =>
        SyncTargetAsync(profile, Scope(profile), progress, ct);

    public Task SyncToChangeAsync(ProjectProfile profile, long changeId, IProgress<string>? progress, CancellationToken ct = default) =>
        SyncTargetAsync(profile, $"{Scope(profile)}@{changeId}", progress, ct);

    private async Task SyncTargetAsync(ProjectProfile profile, string target, IProgress<string>? progress, CancellationToken ct)
    {
        var args = Prefix(profile, tagged: false).Concat(new[] { "sync", target });
        var res = await ProcessRunner.RunAsync(P4Exe(), args, onLine: l => progress?.Report(l), ct: ct);
        if (res.ExitCode != 0)
            throw Wrap(string.Join(Environment.NewLine, res.StdErr.Concat(res.StdOut)));
    }

    public async Task LoginAsync(ProjectProfile profile, string password, CancellationToken ct = default)
    {
        var args = Prefix(profile, tagged: false).Concat(new[] { "login" });
        var res = await ProcessRunner.RunAsync(P4Exe(), args, stdin: password, ct: ct, stdinEncoding: await ServerEncodingAsync(profile, ct));
        if (res.ExitCode != 0)
            throw Wrap(string.Join(Environment.NewLine, res.StdErr.Concat(res.StdOut)));
    }

    public async Task<TimeSpan?> GetTicketRemainingAsync(ProjectProfile profile, CancellationToken ct = default)
    {
        try
        {
            var row = (await RunTaggedAsync(profile, ct, "login", "-s")).FirstOrDefault();
            var seconds = Str(row, "TicketExpiration");
            // Нет поля — тикет без срока (группа с Timeout: unlimited).
            return long.TryParse(seconds, out var s) ? TimeSpan.FromSeconds(s) : TimeSpan.MaxValue;
        }
        catch (P4Exception ex) when (ex.IsAuthError) { return null; }
    }

    public async Task SetActiveWorkspaceAsync(ProjectProfile profile, string clientName, CancellationToken ct = default)
    {
        CheckToken("Workspace", clientName);
        var res = await ProcessRunner.RunAsync(P4Exe(), new[] { "set", $"P4CLIENT={clientName}" }, ct: ct);
        if (res.ExitCode != 0)
            throw Wrap(string.Join(Environment.NewLine, res.StdErr));
    }

    // ---------- history ----------

    public Task<IReadOnlyList<ChangeInfo>> GetHistoryAsync(ProjectProfile profile, int limit, long? beforeChange, CancellationToken ct = default)
    {
        var target = beforeChange is > 1 ? $"{Scope(profile)}@<={beforeChange.Value - 1}" : Scope(profile);
        return QueryChangesAsync(profile, ct, "-l", "-m", Math.Clamp(limit, 1, 500).ToString(), "-s", "submitted", target);
    }

    public async Task<IReadOnlyList<ChangeFile>> GetChangeFilesAsync(ProjectProfile profile, long changeId, CancellationToken ct = default)
    {
        var rows = await RunTaggedAsync(profile, ct, "describe", "-s", changeId.ToString());
        var o = rows.FirstOrDefault();
        var files = new List<ChangeFile>();
        // describe отдаёт плоский объект: depotFile0/action0/rev0/type0, depotFile1/...
        for (var i = 0; o != null; i++)
        {
            var depot = Str(o, "depotFile" + i);
            if (depot == "") break;
            int.TryParse(Str(o, "rev" + i), out var rev);
            files.Add(new ChangeFile(depot, Str(o, "action" + i), rev, Str(o, "type" + i)));
        }
        return files;
    }

    // ---------- P4IGNORE ----------

    public async Task<IReadOnlyDictionary<string, string>> FindIgnoredAsync(ProjectProfile profile, IReadOnlyList<string> localPaths, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in localPaths.Chunk(40))
        {
            // p4 ignores -i -v: «<путь> ignored by <файл>:<строка>:<правило>» либо «<путь> not ignored».
            var lines = await RunUntaggedAsync(profile, null, ct, new[] { "ignores", "-i", "-v" }.Concat(chunk).ToArray());
            foreach (var line in lines)
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"^(?<path>.+?) ignored by (?<rule>.+)$");
                if (m.Success) result[m.Groups["path"].Value] = m.Groups["rule"].Value;
            }
        }
        return result;
    }

    // ---------- pending changes / submit ----------

    public async Task<IReadOnlyList<PendingChangelist>> GetPendingAsync(ProjectProfile profile, CancellationToken ct = default)
    {
        var opened = (await RunTaggedAsync(profile, ct, "opened"))
            .Select(o =>
            {
                var chText = Str(o, "change");
                long.TryParse(chText, out var ch);   // "default" -> 0
                int.TryParse(Str(o, "rev"), out var rev);
                var depot = Str(o, "depotFile");
                return new OpenedFile(depot, ToLocalPath(profile, Str(o, "clientFile"), depot),
                    Str(o, "action"), ch, Str(o, "type"), rev);
            })
            .ToList();

        // Помечаем файлы, которые подпадают под P4IGNORE, но уже лежат в депо (reconcile/edit их всё равно откроет).
        // Удаление такого файла (delete) — это как раз «лечение», его не помечаем. Сбой проверки не должен ломать список.
        try
        {
            var candidates = opened.Where(f => !f.Action.Contains("delete", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.LocalPath).ToList();
            var ignored = await FindIgnoredAsync(profile, candidates, ct);
            if (ignored.Count > 0)
                opened = opened.Select(f => ignored.TryGetValue(f.LocalPath, out var rule) ? f with { IgnoredBy = rule } : f).ToList();
        }
        catch (P4Exception) { }

        var pending = await QueryChangesAsync(profile, ct, "-l", "-s", "pending", "-c", profile.P4Client);

        var result = new List<PendingChangelist>
        {
            new(0, "", opened.Where(f => f.Change == 0).OrderBy(f => f.LocalPath, StringComparer.OrdinalIgnoreCase).ToList()),
        };
        foreach (var c in pending.OrderBy(c => c.Id))
            result.Add(new PendingChangelist(c.Id, c.Description.Trim(),
                opened.Where(f => f.Change == c.Id).OrderBy(f => f.LocalPath, StringComparer.OrdinalIgnoreCase).ToList()));
        return result;
    }

    public async Task ReconcileAsync(ProjectProfile profile, IProgress<string>? progress, CancellationToken ct = default)
    {
        var args = Prefix(profile, tagged: false).Concat(new[] { "reconcile", Scope(profile) });
        var res = await ProcessRunner.RunAsync(P4Exe(), args, onLine: l => progress?.Report(l), ct: ct);
        if (res.ExitCode != 0)
            throw Wrap(string.Join(Environment.NewLine, res.StdErr.Concat(res.StdOut)));
    }

    public async Task<long> CreateChangelistAsync(ProjectProfile profile, string description, CancellationToken ct = default)
    {
        var user = await UserAsync(profile, ct);
        var spec = $"Change: new\nClient: {profile.P4Client}\nUser: {user}\nStatus: new\nDescription:\n{IndentDescription(description)}\n";
        var res = await RunUntaggedAsync(profile, spec, ct, "change", "-i");
        var m = System.Text.RegularExpressions.Regex.Match(string.Join("\n", res), @"Change (\d+) created");
        if (!m.Success) throw new P4Exception("Unexpected p4 output: " + string.Join(" ", res));
        return long.Parse(m.Groups[1].Value);
    }

    public async Task UpdateChangelistDescriptionAsync(ProjectProfile profile, long changeId, string description, CancellationToken ct = default)
    {
        var spec = await RunUntaggedAsync(profile, null, ct, "change", "-o", changeId.ToString());
        var updated = ReplaceDescription(spec, description);
        await RunUntaggedAsync(profile, updated, ct, "change", "-i");
    }

    public Task DeleteChangelistAsync(ProjectProfile profile, long changeId, CancellationToken ct = default) =>
        RunUntaggedAsync(profile, null, ct, "change", "-d", changeId.ToString());

    public async Task MoveFilesAsync(ProjectProfile profile, long targetChange, IReadOnlyList<string> depotPaths, CancellationToken ct = default)
    {
        var target = targetChange == 0 ? "default" : targetChange.ToString();
        foreach (var chunk in depotPaths.Chunk(50))
            await RunUntaggedAsync(profile, null, ct, new[] { "reopen", "-c", target }.Concat(chunk).ToArray());
    }

    public async Task RevertAsync(ProjectProfile profile, IReadOnlyList<string> depotPaths, IProgress<string>? progress, CancellationToken ct = default)
    {
        foreach (var chunk in depotPaths.Chunk(50))
        {
            var lines = await RunUntaggedAsync(profile, null, ct, new[] { "revert" }.Concat(chunk).ToArray());
            foreach (var l in lines) progress?.Report(l);
        }
    }

    public async Task SubmitAsync(ProjectProfile profile, long changeId, IProgress<string>? progress, CancellationToken ct = default)
    {
        var args = Prefix(profile, tagged: false).Concat(new[] { "submit", "-c", changeId.ToString() });
        var res = await ProcessRunner.RunAsync(P4Exe(), args, onLine: l => progress?.Report(l), ct: ct);
        if (res.ExitCode != 0)
            throw Wrap(string.Join(Environment.NewLine, res.StdErr.Concat(res.StdOut)));
    }

    /// <summary>Обычный (не tagged) вызов p4; возвращает строки stdout, бросает при ненулевом коде.</summary>
    private async Task<List<string>> RunUntaggedAsync(ProjectProfile p, string? stdin, CancellationToken ct, params string[] args)
    {
        var encoding = stdin != null ? await ServerEncodingAsync(p, ct) : null;
        var res = await ProcessRunner.RunAsync(P4Exe(), Prefix(p, tagged: false).Concat(args), stdin, ct: ct, stdinEncoding: encoding);
        if (res.ExitCode != 0)
            throw Wrap(string.Join(Environment.NewLine, res.StdErr.Concat(res.StdOut)));
        return res.StdOut.ToList();
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.Encoding> _serverEncodings = new();

    /// <summary>
    /// В какой кодировке отправлять текст (описания CL, пароль). Unicode-сервер ждёт UTF-8; обычный хранит байты
    /// как есть, и P4V коллег пишет/читает их в системной ANSI-кодировке — используем её же, иначе у команды кракозябры.
    /// </summary>
    private async Task<System.Text.Encoding> ServerEncodingAsync(ProjectProfile p, CancellationToken ct)
    {
        var key = p.P4Port ?? "";
        if (_serverEncodings.TryGetValue(key, out var cached)) return cached;
        System.Text.Encoding enc;
        try
        {
            var info = (await RunTaggedAsync(p, ct, "info")).FirstOrDefault();
            enc = Str(info, "unicode") == "enabled" ? new System.Text.UTF8Encoding(false) : TextDecoding.Legacy;
        }
        catch (P4Exception) { return new System.Text.UTF8Encoding(false); }   // не кэшируем: сервер мог быть недоступен
        return _serverEncodings[key] = enc;
    }

    private async Task<string> UserAsync(ProjectProfile p, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(p.P4User)) return p.P4User;
        var info = await RunTaggedAsync(p, ct, "info");
        return Str(info.FirstOrDefault(), "userName");
    }

    /// <summary>Каждая строка описания получает отступ-табуляцию, поэтому текст не может «выйти» из поля Description
    /// и подменить другие поля спецификации (Client, User, Files...). Одиночный \r тоже считается переводом строки.</summary>
    private static string IndentDescription(string d) =>
        string.Join("\n", d.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => "\t" + l));

    /// <summary>Подменяет блок Description в тексте спецификации changelist'а.</summary>
    internal static string ReplaceDescription(IEnumerable<string> specLines, string description)
    {
        var lines = specLines.ToList();
        var start = lines.FindIndex(l => l.StartsWith("Description:", StringComparison.Ordinal));
        if (start < 0) throw new P4Exception("Changelist spec has no Description field.");
        var end = start + 1;
        while (end < lines.Count && (lines[end].Length == 0 || char.IsWhiteSpace(lines[end][0]))) end++;
        var result = lines.Take(start + 1).ToList();
        result.AddRange(IndentDescription(description).Split('\n'));
        result.Add("");
        result.AddRange(lines.Skip(end));
        return string.Join("\n", result) + "\n";
    }

    /// <summary>clientFile вида //client/path -> локальный путь; %40 %23 %2A %25 разэкранируются.</summary>
    private static string ToLocalPath(ProjectProfile p, string clientFile, string depotFile)
    {
        static string Unescape(string s) => s.Replace("%40", "@").Replace("%23", "#").Replace("%2A", "*").Replace("%25", "%");
        var m = System.Text.RegularExpressions.Regex.Match(clientFile, @"^//[^/]+/(.*)$");
        if (!m.Success || string.IsNullOrWhiteSpace(p.WorkspaceRoot)) return Unescape(depotFile);
        return Path.GetFullPath(Path.Combine(p.WorkspaceRoot, Unescape(m.Groups[1].Value)));
    }

    // ---------- helpers ----------

    private static string Scope(ProjectProfile p) =>
        !string.IsNullOrWhiteSpace(p.DepotScope) ? p.DepotScope.Trim() : $"//{p.P4Client}/...";

    private static IEnumerable<string> Prefix(ProjectProfile p, bool tagged)
    {
        Validate(p);   // выполняется сразу при вызове, а не лениво
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.P4Port)) { args.Add("-p"); args.Add(p.P4Port); }
        if (!string.IsNullOrWhiteSpace(p.P4User)) { args.Add("-u"); args.Add(p.P4User); }
        if (!string.IsNullOrWhiteSpace(p.P4Client)) { args.Add("-c"); args.Add(p.P4Client); }
        if (tagged) args.Add("-G");   // структурированный вывод с сырыми байтами (см. RunTaggedAsync)
        return args;
    }

    /// <summary>Значения из профиля попадают в аргументы p4. ArgumentList исключает shell-инъекцию, но значение,
    /// начинающееся с «-», p4 принял бы за опцию (например, client «-d»), поэтому такие значения отклоняются.</summary>
    private static void Validate(ProjectProfile p)
    {
        CheckToken("P4 server", p.P4Port);
        CheckToken("P4 user", p.P4User);
        CheckToken("Workspace", p.P4Client);
        if (!string.IsNullOrWhiteSpace(p.DepotScope))
        {
            var s = p.DepotScope.Trim();
            if (!s.StartsWith("//") || s.Any(char.IsControl))
                throw new P4Exception("Depot scope must be a depot path starting with '//' (e.g. //depot/Game/Main/...).");
        }
    }

    private static void CheckToken(string label, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (value.StartsWith('-') || value.Any(char.IsControl))
            throw new P4Exception($"{label} contains invalid characters (leading '-' or control characters).");
    }

    private async Task<List<JsonObject>> RunTaggedAsync(ProjectProfile p, CancellationToken ct, params string[] args)
    {
        // -G (marshal) вместо -Mj (JSON): JSON-режим p4 заменяет не-UTF-8 байты на «�», и кириллица
        // с не-Unicode сервера терялась ещё до приложения. Здесь байты сырые и декодируются TextDecoding.
        var res = await ProcessRunner.RunBytesAsync(P4Exe(), Prefix(p, tagged: true).Concat(args), ct);

        List<Dictionary<string, string>> rows;
        try { rows = P4Marshal.Parse(res.StdOut); }
        catch (FormatException ex)
        {
            if (res.StdErr.Count > 0) throw Wrap(string.Join(Environment.NewLine, res.StdErr));
            throw new P4Exception("Unexpected p4 output: " + ex.Message);
        }

        var objects = new List<JsonObject>();
        var errors = res.StdErr.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        foreach (var row in rows)
        {
            if (row.TryGetValue("code", out var code) && code == "error")
            {
                // severity: 1 empty, 2 info/warning (напр. "no such file(s)"), 3+ реальные ошибки
                int.TryParse(row.GetValueOrDefault("severity"), out var sev);
                if (sev >= 3) errors.Add(row.GetValueOrDefault("data", "").Trim());
                continue;
            }
            objects.Add(new JsonObject(row.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))));
        }

        if (errors.Count > 0 && (res.ExitCode != 0 || objects.Count == 0))
            throw Wrap(string.Join(Environment.NewLine, errors));
        if (res.ExitCode != 0 && objects.Count == 0)
            throw Wrap("p4 exited with code " + res.ExitCode);
        return objects;
    }

    private async Task<IReadOnlyList<ChangeInfo>> QueryChangesAsync(ProjectProfile p, CancellationToken ct, params string[] args)
    {
        var rows = await RunTaggedAsync(p, ct, new[] { "changes" }.Concat(args).ToArray());
        return rows.Select(r =>
        {
            long.TryParse(Str(r, "change"), out var id);
            long.TryParse(Str(r, "time"), out var t);
            return new ChangeInfo(id, Str(r, "user"), Str(r, "client"),
                DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime, Str(r, "desc"));
        }).ToList();
    }

    private static WorkspaceInfo ToWorkspace(JsonObject o, string fallbackName)
    {
        var name = Str(o, "client");
        if (name == "") name = Str(o, "Client");
        if (name == "") name = fallbackName;
        var host = Str(o, "Host");
        var local = host == "" || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        return new WorkspaceInfo(name, Str(o, "Root"), host, Str(o, "Stream"), Str(o, "Description").Trim(), local);
    }

    private static string Str(JsonObject? o, string key) =>
        o != null && o.TryGetPropertyValue(key, out var v) && v != null ? v.ToString() : "";

    private static P4Exception Wrap(string message)
    {
        var m = message.ToLowerInvariant();
        bool auth = m.Contains("p4passwd") || m.Contains("session has expired") || m.Contains("please login");
        return new P4Exception(message.Trim(), auth);
    }
}
