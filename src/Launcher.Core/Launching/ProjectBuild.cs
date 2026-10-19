using System.Text.Json;
using System.Text.RegularExpressions;
using PerforceLauncher.Core.Models;
using PerforceLauncher.Core.VersionControl;

namespace PerforceLauncher.Core.Launching;

/// <summary>Модуль, чей код новее собранной DLL (или DLL ещё нет).</summary>
public sealed record StaleModule(string Name, int ChangedFiles, bool NeverBuilt, string? ExampleFile);

/// <summary>Состояние сборки C++ проекта: нужно ли пересобрать, чтобы редактор запустил актуальный код.</summary>
/// <param name="SyncedSinceBuild">Лаунчер синкнул код после последней сборки (страховка для воркспейсов с опцией modtime).</param>
public sealed record BuildState(bool HasCode, IReadOnlyList<StaleModule> StaleModules, bool TargetsChanged, bool SyncedSinceBuild = false)
{
    public static readonly BuildState NoCode = new(false, Array.Empty<StaleModule>(), false);

    public bool NeedsBuild => StaleModules.Count > 0 || TargetsChanged || SyncedSinceBuild;
    public int ChangedFiles => StaleModules.Sum(m => m.ChangedFiles);

    /// <summary>«14 files in MonsterPotion, MPFunctionalTests» / «MonsterPotion was never built».</summary>
    public string Summary
    {
        get
        {
            if (!NeedsBuild) return "C++ code is up to date";
            if (StaleModules.Count == 0 && !TargetsChanged) return "code synced after the last build";
            var names = StaleModules.Select(m => m.Name).ToList();
            var list = names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + $" +{names.Count - 3}";
            if (StaleModules.Count > 0 && StaleModules.All(m => m.NeverBuilt)) return $"{list} not built yet";
            var files = ChangedFiles;
            var head = files > 0 ? $"{files} changed file{(files == 1 ? "" : "s")}" : "build targets changed";
            return names.Count > 0 ? $"{head} in {list}" : head;
        }
    }
}

/// <summary>
/// Определяет, устарела ли сборка: для каждого C++ модуля проекта и его плагинов сравнивает время самых свежих
/// исходников (.cpp/.h/.Build.cs…) с временем DLL модуля в Binaries\Win64. Работает независимо от того, чем был
/// сделан sync (лаунчер, P4V, Unreal): при опции воркспейса nomodtime (по умолчанию) синкнутые файлы получают текущее время.
/// </summary>
public static class BuildStateChecker
{
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cpp", ".h", ".hpp", ".inl", ".c", ".cc", ".cs", ".ispc" };
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    /// <param name="codeSyncedAt">Когда лаунчер последний раз синкнул код (страховка для воркспейсов с опцией modtime).</param>
    public static BuildState Check(string uprojectPath, DateTime? codeSyncedAt = null)
    {
        if (!File.Exists(uprojectPath)) return BuildState.NoCode;
        var projectDir = Path.GetDirectoryName(uprojectPath)!;
        var modules = CollectModules(uprojectPath, projectDir);
        if (modules.Count == 0) return BuildState.NoCode;

        var stale = new List<StaleModule>();
        var manifests = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        DateTime? newestDll = null;
        foreach (var (name, ownerDir) in modules)
        {
            var sourceDir = FindModuleSourceDir(ownerDir, name);
            if (sourceDir == null) continue;   // нет исходников (бинарный плагин) — собирать нечего

            var dll = FindModuleDll(ownerDir, name, manifests);
            if (dll == null) { stale.Add(new StaleModule(name, 0, true, null)); continue; }

            var builtAt = File.GetLastWriteTime(dll);
            if (newestDll == null || builtAt > newestDll) newestDll = builtAt;
            var changed = SafeFiles(sourceDir)
                .Where(f => CodeExtensions.Contains(Path.GetExtension(f)))
                .Select(f => (Path: f, Time: File.GetLastWriteTime(f)))
                .Where(x => x.Time > builtAt + Tolerance)
                .OrderByDescending(x => x.Time)
                .ToList();
            if (changed.Count > 0)
                stale.Add(new StaleModule(name, changed.Count, false, Path.GetFileName(changed[0].Path)));
        }

        // modtime: у синкнутых файлов время из депо, поэтому сравниваем момент синка с самой свежей DLL.
        // Именно с самой свежей: сборка перелинковывает только изменённые модули, остальные DLL остаются старыми.
        var syncedSinceBuild = stale.Count == 0 && codeSyncedAt is { } synced && newestDll is { } newest && synced > newest + Tolerance;

        // Изменился *.Target.cs проекта — нужна пересборка целевой сборки
        var targetsChanged = newestDll is { } nd && SafeFiles(Path.Combine(projectDir, "Source"), recursive: false)
            .Where(f => f.EndsWith(".Target.cs", StringComparison.OrdinalIgnoreCase))
            .Any(f => File.GetLastWriteTime(f) > nd + Tolerance);

        return new BuildState(true, stale, targetsChanged, syncedSinceBuild);
    }

    /// <summary>
    /// DLL, которую редактор реально загрузит: из манифеста Binaries\Win64\UnrealEditor.modules (при сборке с открытым
    /// редактором UBT пишет UnrealEditor-Mod-0001.dll и обновляет манифест), иначе UnrealEditor-Mod.dll.
    /// </summary>
    private static string? FindModuleDll(string ownerDir, string module, Dictionary<string, Dictionary<string, string>> cache)
    {
        var bin = Path.Combine(ownerDir, "Binaries", "Win64");
        if (!cache.TryGetValue(bin, out var map))
        {
            map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var manifest in new[] { "UnrealEditor.modules", "UE4Editor.modules" })
            {
                using var doc = ParseJson(Path.Combine(bin, manifest));
                if (doc != null && doc.RootElement.TryGetProperty("Modules", out var mods) && mods.ValueKind == JsonValueKind.Object)
                    foreach (var m in mods.EnumerateObject())
                        if (m.Value.ValueKind == JsonValueKind.String && m.Value.GetString() is { Length: > 0 } file) map.TryAdd(m.Name, file);
            }
            cache[bin] = map;
        }
        if (map.TryGetValue(module, out var listed) && File.Exists(Path.Combine(bin, listed))) return Path.Combine(bin, listed);
        return new[] { $"UnrealEditor-{module}.dll", $"UE4Editor-{module}.dll" }.Select(f => Path.Combine(bin, f)).FirstOrDefault(File.Exists);
    }

    /// <summary>Модули проекта (.uproject) и включённых плагинов из папки Plugins: (имя, папка-владелец).</summary>
    private static List<(string Name, string OwnerDir)> CollectModules(string uprojectPath, string projectDir)
    {
        var result = new List<(string, string)>();
        var disabledPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabledPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var doc = ParseJson(uprojectPath))
        {
            if (doc == null) return result;
            AddModules(doc.RootElement, projectDir, result);
            if (doc.RootElement.TryGetProperty("Plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Array)
                foreach (var pl in plugins.EnumerateArray())
                {
                    var plName = pl.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    var on = !pl.TryGetProperty("Enabled", out var e) || e.ValueKind != JsonValueKind.False;
                    (on ? enabledPlugins : disabledPlugins).Add(plName);
                }
        }

        foreach (var uplugin in SafeFiles(Path.Combine(projectDir, "Plugins"), recursive: true, maxDepth: 4)
                     .Where(f => f.EndsWith(".uplugin", StringComparison.OrdinalIgnoreCase)))
        {
            var pluginName = Path.GetFileNameWithoutExtension(uplugin);
            if (disabledPlugins.Contains(pluginName)) continue;
            using var doc = ParseJson(uplugin);
            if (doc == null) continue;
            var enabledByDefault = !doc.RootElement.TryGetProperty("EnabledByDefault", out var ebd) || ebd.ValueKind != JsonValueKind.False;
            if (!enabledByDefault && !enabledPlugins.Contains(pluginName)) continue;
            AddModules(doc.RootElement, Path.GetDirectoryName(uplugin)!, result);
        }
        return result;
    }

    private static void AddModules(JsonElement root, string ownerDir, List<(string, string)> result)
    {
        if (!root.TryGetProperty("Modules", out var modules) || modules.ValueKind != JsonValueKind.Array) return;
        foreach (var m in modules.EnumerateArray())
        {
            var name = m.TryGetProperty("Name", out var n) ? n.GetString() : null;
            var type = m.TryGetProperty("Type", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(name) || type is "Program" or "External") continue;
            // Модуль не для Windows (PlatformAllowList без Win64) — в редакторе на Windows не собирается
            if (m.TryGetProperty("PlatformAllowList", out var allow) && allow.ValueKind == JsonValueKind.Array &&
                !allow.EnumerateArray().Any(p => string.Equals(p.GetString(), "Win64", StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add((name!, ownerDir));
        }
    }

    private static string? FindModuleSourceDir(string ownerDir, string module)
    {
        var buildCs = SafeFiles(Path.Combine(ownerDir, "Source"), recursive: true, maxDepth: 4)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), module + ".Build.cs", StringComparison.OrdinalIgnoreCase));
        return buildCs == null ? null : Path.GetDirectoryName(buildCs);
    }

    private static JsonDocument? ParseJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch { return null; }
    }

    /// <summary>Файлы папки без падений на недоступных подпапках; служебные папки UE пропускаются.</summary>
    private static IEnumerable<string> SafeFiles(string dir, bool recursive = true, int maxDepth = 64)
    {
        if (!Directory.Exists(dir)) yield break;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((dir, 0));
        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();
            string[] files, dirs;
            try { files = Directory.GetFiles(current); dirs = recursive && depth < maxDepth ? Directory.GetDirectories(current) : Array.Empty<string>(); }
            catch { continue; }
            foreach (var f in files) yield return f;
            foreach (var d in dirs)
            {
                var n = Path.GetFileName(d);
                if (n is "Intermediate" or "Binaries" or "Saved" or "DerivedDataCache" or ".git" or ".vs") continue;
                stack.Push((d, depth + 1));
            }
        }
    }
}

/// <summary>Сборка редакторной цели проекта штатным Build.bat движка (инкрементально, как Build в IDE).</summary>
public sealed class ProjectBuilder
{
    private static readonly Regex ProgressRx = new(@"^\s*\[(\d+)/(\d+)\]");
    private readonly EngineLocator _engines;

    public ProjectBuilder(EngineLocator engines) => _engines = engines;

    public sealed record Plan(string BuildBat, string Target, string UProject);

    /// <summary>Что и чем собирать; null — нечем (движок или .uproject не найдены).</summary>
    public Plan? GetPlan(ProjectProfile p)
    {
        var uproject = ProfilePaths.UProject(p);
        var engine = _engines.FindEngineRoot(p);
        if (uproject == null || !File.Exists(uproject) || engine == null) return null;
        var bat = Path.Combine(engine, "Engine", "Build", "BatchFiles", "Build.bat");
        if (!File.Exists(bat)) return null;
        return new Plan(bat, FindEditorTarget(uproject), uproject);
    }

    /// <summary>Имя editor-цели: Source\*.Target.cs с TargetType.Editor, иначе «ИмяПроектаEditor».</summary>
    private static string FindEditorTarget(string uproject)
    {
        var source = Path.Combine(Path.GetDirectoryName(uproject)!, "Source");
        try
        {
            foreach (var f in Directory.GetFiles(source, "*.Target.cs"))
                if (File.ReadAllText(f).Contains("TargetType.Editor"))
                    return Path.GetFileName(f)[..^".Target.cs".Length];
        }
        catch { /* нет папки Source — берём имя по умолчанию */ }
        return Path.GetFileNameWithoutExtension(uproject) + "Editor";
    }

    /// <param name="onProgress">(собрано, всего) по строкам вида «[12/57] Compile …».</param>
    public async Task BuildAsync(ProjectProfile p, Action<string> onLine, Action<int, int>? onProgress, CancellationToken ct)
    {
        var plan = GetPlan(p) ?? throw new InvalidOperationException("Can't find the engine or the .uproject for this project.");
        // cmd /c ""…\Build.bat" Target Win64 Development -Project="…" …": внешние кавычки снимает cmd.
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var args = $"/c \"\"{plan.BuildBat}\" {plan.Target} Win64 Development -Project=\"{plan.UProject}\" -WaitMutex -FromMsBuild\"";
        var result = await ProcessRunner.RunCommandLineAsync(cmd, args, line =>
        {
            onLine(line);
            var m = ProgressRx.Match(line);
            if (m.Success) onProgress?.Invoke(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        }, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Build failed (exit code {result.ExitCode}). See Activity for the compiler output.");
    }
}
