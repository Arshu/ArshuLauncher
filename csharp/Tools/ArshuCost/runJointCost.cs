#!/usr/bin/dotnet run

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The cost reader of the joint tools. It lists every harness session (Claude Code, codex, pi) whose working folder is
/// the given folder, whoever started it, one row per (session, model), newest first, with a TOTAL row, as a markdown
/// table (or --json). The history lives in the folder itself, in &lt;folder&gt;/.joint-cost/: launches.jsonl (written by
/// the launcher and session tools) and usage.jsonl (written by this reader). Every run snapshots: it reads the live
/// transcripts, merges them into usage.jsonl and prints the table from the merged data, so a trimmed or lost transcript
/// never lowers a number already stored. --no-snapshot merges in memory and writes nothing.
///
/// Repository-independent single file; needs .NET 10+ and, for Claude and codex costs, Node (npx runs ccusage, pinned).
/// pi records its own cost. See runJointCost.md.
///
/// Usage: dotnet run csharp/Tools/ArshuCost/runJointCost.cs -- [--folder &lt;path&gt;] [--harness claude,codex,pi] [--json] [--no-snapshot]
/// </summary>
class RunJointCost
{
    private const string CcusagePinned = "20.0.17";
    private const string StoreFolder = ".joint-cost";
    private const string UsageFile = "usage.jsonl";
    private const string LaunchesFile = "launches.jsonl";
    internal const string DefaultGroup = "Default";
    private const string NoModel = "-";
    internal static readonly string[] Harnesses = { "claude", "codex", "pi" };
    internal static readonly TimeSpan MatchWindow = TimeSpan.FromSeconds(120);

    /// <summary>One (harness, session, model) with every token field, its cost and where the cost came from.</summary>
    internal sealed record Usage(string Harness, string SessionId, string Model, long Input, long Output, long CacheRead,
        long CacheWrite, long? Thinking, double Cost, string CostSource, DateTimeOffset First, DateTimeOffset Last,
        string Folder, DateTimeOffset Snapshot)
    {
        public long Tokens => Input + Output + CacheRead + CacheWrite;
    }

    /// <summary>A launch ledger line: the launcher or session tool that started a harness in a folder, when, and the session
    /// group it was launched in (--group; DefaultGroup when none was given, also for a line written before groups existed).</summary>
    internal sealed record Launch(DateTimeOffset StartUtc, string Kind, string Tool, string Harness, string Folder, string Skill, string Group = DefaultGroup);

    /// <summary>A merged row: the usage and whether the live transcript supplied it.</summary>
    internal sealed record Row(Usage Usage, bool Live);

    private sealed class Acc
    {
        public long Input, Output, CacheRead, CacheWrite;
        public long? Thinking;
        public double Cost;

        public void Add(long input, long output, long cacheRead, long cacheWrite, long? thinking)
        {
            Input += input; Output += output; CacheRead += cacheRead; CacheWrite += cacheWrite;
            if (thinking != null) Thinking = (Thinking ?? 0) + thinking;
        }
    }

#if !EMBEDDED_COST
    static int Main(string[] args) => Cli(args);
#endif

    /// <summary>The command line of the reader: the script's Main, and the --cost mode of a tool that carries the reader (its
    /// NativeAOT binary, built by runJointBuild.cs with EMBEDDED_COST, where Main is the tool's own).</summary>
    internal static int Cli(string[] args)
    {
        try
        {
            Run(args, Console.Out);
            return exitCode;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine("runJointCost: " + e.Message);
            return 1;
        }
    }

    private static void Run(string[] args, TextWriter output)
    {
        string folder = Directory.GetCurrentDirectory();
        var harnesses = Harnesses.ToList();
        bool json = false, snapshot = true, watch = false, listGroups = false;
        double intervalMinutes = 5, idleMinutes = 10;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--folder": folder = Value(args, ref i); break;
                case "--harness": harnesses = Value(args, ref i).Split(',').Select(h => h.Trim()).ToList(); break;
                case "--json": json = true; break;
                case "--no-snapshot": snapshot = false; break;
                case "--watch": watch = true; break;
                case "--groups": listGroups = true; break;
                case "--interval-minutes": intervalMinutes = Minutes(Value(args, ref i), "--interval-minutes"); break;
                case "--idle-minutes": idleMinutes = Minutes(Value(args, ref i), "--idle-minutes"); break;
                default: throw new ArgumentException($"unknown argument {args[i]}; usage: --folder <path> --harness claude,codex,pi --json --no-snapshot --groups | --watch --harness <one> --interval-minutes 5 --idle-minutes 10");
            }
        }
        foreach (string h in harnesses)
            if (!Harnesses.Contains(h)) throw new ArgumentException($"unknown harness {h}; use claude, codex or pi");
        folder = NormalizeFolder(folder);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (watch)
        {
            if (harnesses.Count != 1) throw new ArgumentException("--watch needs exactly one --harness");
            if (!snapshot || json) throw new ArgumentException("--watch snapshots and prints no table: it takes neither --no-snapshot nor --json");
            using var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
            using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cancel.Cancel(); });
            exitCode = Watch(folder, harnesses[0], TimeSpan.FromMinutes(intervalMinutes), TimeSpan.FromMinutes(idleMinutes),
                () => CollectAndMerge(folder, harnesses, home, true, DateTimeOffset.UtcNow), rows => LatestChange(home, folder, harnesses[0], rows), cancel.Token);
            return;
        }
        var merged = CollectAndMerge(folder, harnesses, home, snapshot, DateTimeOffset.UtcNow).Where(r => harnesses.Contains(r.Usage.Harness)).ToList();
        var launches = ReadLaunches(Path.Combine(folder, StoreFolder, LaunchesFile));
        output.Write(listGroups ? (json ? RenderGroupsJson(merged, launches) : RenderGroups(merged, launches))
            : json ? RenderJson(merged, launches) : RenderTable(merged, launches));
    }

    private static int exitCode;

    private static double Minutes(string text, string name) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m > 0 ? m : throw new ArgumentException($"{name} needs a positive number of minutes, not {text}");

    /// <summary>Reads the live transcripts of the harnesses, merges them into the folder's usage.jsonl and (unless read-only)
    /// writes it back. Returns every merged row, of all harnesses.</summary>
    internal static List<Row> CollectAndMerge(string folder, List<string> harnesses, string home, bool snapshot, DateTimeOffset now)
    {
        var live = new List<Usage>();
        if (harnesses.Contains("claude"))
        {
            var rows = ReadClaude(Path.Combine(home, ".claude", "projects"), folder, now);
            if (rows.Count > 0) live.AddRange(ApplyClaudeCost(rows, Ccusage("claude")));
        }
        if (harnesses.Contains("codex"))
        {
            var rows = ReadCodex(Path.Combine(home, ".codex", "sessions"), folder, now);
            if (rows.Count > 0) live.AddRange(ApplyCodexCost(rows, Ccusage("codex")));
        }
        if (harnesses.Contains("pi")) live.AddRange(ReadPi(Path.Combine(home, ".pi", "agent", "sessions"), folder, now));

        string usage = Path.Combine(folder, StoreFolder, UsageFile);
        List<Row> merged = Merge(live, ReadStore(usage, folder));
        if (snapshot && merged.Count > 0) WriteStore(usage, merged.Select(r => r.Usage).ToList());
        return merged;
    }

    // ---- watch ---------------------------------------------------------------------------------------------------

    private static string LogLine(string text) => $"{Iso(DateTimeOffset.UtcNow)} {text}\n";

    private static bool PidAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Takes the lock file (the pid inside). A lock whose pid is not alive is stale and replaced; a live one is
    /// left alone and its pid returned.</summary>
    internal static (bool Acquired, int OwnerPid) AcquireLock(string path, int pid)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                stream.Write(Encoding.ASCII.GetBytes(pid.ToString(CultureInfo.InvariantCulture)));
                return (true, pid);
            }
            catch (IOException) when (File.Exists(path))
            {
                string text = File.ReadAllText(path).Trim();
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int owner))
                    throw new InvalidDataException($"{path}: the lock does not hold a pid (\"{text}\")");
                if (PidAlive(owner)) return (false, owner);
                File.Delete(path);
            }
        }
        throw new IOException($"{path}: the stale lock could not be replaced");
    }

    internal static void ReleaseLock(string path, int pid)
    {
        if (File.Exists(path) && File.ReadAllText(path).Trim() == pid.ToString(CultureInfo.InvariantCulture)) File.Delete(path);
    }

    /// <summary>The newest write time of the transcripts of the rows' sessions (and of the folder's Claude and pi files).</summary>
    internal static DateTimeOffset? LatestChange(string home, string folder, string harness, List<Row> rows)
    {
        var ids = rows.Where(r => r.Usage.Harness == harness).Select(r => r.Usage.SessionId).ToHashSet();
        IEnumerable<string> files = harness switch
        {
            "claude" => Jsonl(ClaudeDir(Path.Combine(home, ".claude", "projects"), folder)),
            "pi" => Jsonl(PiDir(Path.Combine(home, ".pi", "agent", "sessions"), folder)),
            _ => Directory.Exists(Path.Combine(home, ".codex", "sessions"))
                ? Directory.EnumerateFiles(Path.Combine(home, ".codex", "sessions"), "rollout-*.jsonl", SearchOption.AllDirectories).Where(f => ids.Any(id => f.EndsWith(id + ".jsonl", StringComparison.Ordinal)))
                : Array.Empty<string>()
        };
        DateTimeOffset? newest = null;
        foreach (string f in files)
        {
            DateTimeOffset t = File.GetLastWriteTimeUtc(f);
            if (newest == null || t > newest) newest = t;
        }
        return newest;
    }

    private static string[] Jsonl(string dir) => Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jsonl") : Array.Empty<string>();

    /// <summary>The watcher: one per folder and harness (lock file), snapshots every interval, ends (exit 0) once nothing
    /// changed for the idle time and a snapshot was taken after the last change, or on cancellation after a final snapshot.
    /// A failing snapshot is logged with its cause and ends the watcher with exit 1.</summary>
    internal static int Watch(string folder, string harness, TimeSpan interval, TimeSpan idle, Func<List<Row>> snapshot,
        Func<List<Row>, DateTimeOffset?> latestChange, CancellationToken cancel)
    {
        string dir = Path.Combine(folder, StoreFolder);
        string lockPath = Path.Combine(dir, $"watch-{harness}.lock");
        string log = Path.Combine(dir, "watch.log");
        int pid = Environment.ProcessId;
        var (acquired, owner) = AcquireLock(lockPath, pid);
        if (!acquired)
        {
            Console.WriteLine($"already watching, pid {owner}");
            return 0;
        }
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            File.AppendAllText(log, LogLine($"watch {harness} started pid={pid} interval={interval} idle={idle}"));
            while (true)
            {
                bool cancelled = cancel.IsCancellationRequested;
                DateTimeOffset snapshotAt = DateTimeOffset.UtcNow;
                var rows = snapshot().Where(r => r.Usage.Harness == harness).ToList();
                File.AppendAllText(log, LogLine($"snapshot {harness}{(cancelled ? " final" : "")} sessions={rows.Select(r => r.Usage.SessionId).Distinct().Count()} totalCostUsd={rows.Sum(r => r.Usage.Cost).ToString("0.0000", CultureInfo.InvariantCulture)}"));
                if (cancelled) break;
                DateTimeOffset change = latestChange(rows) ?? started;
                if (change < started) change = started;
                if (change <= snapshotAt && DateTimeOffset.UtcNow - change >= idle)
                {
                    File.AppendAllText(log, LogLine($"watch {harness} ended: idle"));
                    break;
                }
                cancel.WaitHandle.WaitOne(interval);
            }
            return 0;
        }
        catch (Exception e)
        {
            File.AppendAllText(log, LogLine($"ERROR {harness}: {e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}"));
            Console.Error.WriteLine("runJointCost: " + e.Message);
            return 1;
        }
        finally
        {
            ReleaseLock(lockPath, pid);
        }
    }

    private static string Value(string[] args, ref int i) =>
        i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");

    internal static string NormalizeFolder(string folder)
    {
        string full = Path.GetFullPath(folder);
        string root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    private static bool FolderEquals(string a, string b) =>
        string.Equals(NormalizeFolder(a), NormalizeFolder(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ---- JSON lines ----------------------------------------------------------------------------------------------

    private static IEnumerable<(int Line, JsonElement Record)> ReadJsonLines(string path)
    {
        int n = 0;
        foreach (string text in File.ReadLines(path))
        {
            n++;
            // A line of NUL bytes is the padding a crashed write leaves in a transcript: it holds no record, so it is
            // skipped like a blank line. NULs next to anything else are still a malformed record (an error below).
            // A line of NUL bytes is the padding a crashed write leaves in a transcript: it holds no record, so it is
            // skipped like a blank line. NULs next to anything else are still a malformed record (an error below).
            if (text.Trim().Trim('\0').Length == 0) continue;
            JsonElement record;
            try
            {
                using var doc = JsonDocument.Parse(text);
                record = doc.RootElement.Clone();
            }
            catch (JsonException e)
            {
                throw new InvalidDataException($"{path}:{n}: malformed JSON record ({e.Message})");
            }
            if (record.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{path}:{n}: record is not a JSON object");
            yield return (n, record);
        }
    }

    private static JsonElement? Get(JsonElement e, params string[] path)
    {
        JsonElement cur = e;
        foreach (string name in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(name, out cur) || cur.ValueKind == JsonValueKind.Null) return null;
        }
        return cur;
    }

    private static string? Text(JsonElement e, params string[] path) =>
        Get(e, path) is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    private static string RequiredText(JsonElement e, string file, int line, params string[] path) =>
        Text(e, path) ?? throw new InvalidDataException($"{file}:{line}: missing string \"{string.Join('.', path)}\"");

    private static long? Number(JsonElement e, string file, int line, params string[] path)
    {
        if (Get(e, path) is not { } v) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out long n))
            throw new InvalidDataException($"{file}:{line}: \"{string.Join('.', path)}\" is not an integer");
        return n;
    }

    private static long RequiredNumber(JsonElement e, string file, int line, params string[] path) =>
        Number(e, file, line, path) ?? throw new InvalidDataException($"{file}:{line}: missing number \"{string.Join('.', path)}\"");

    private static DateTimeOffset Time(string text, string file, int line)
    {
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var t))
            throw new InvalidDataException($"{file}:{line}: \"{text}\" is not a timestamp");
        return t.ToUniversalTime();
    }

    /// <summary>Widens the session span by the record's own "timestamp" (absent on bookkeeping records).</summary>
    private static void Span(JsonElement record, string file, int line, ref DateTimeOffset? first, ref DateTimeOffset? last)
    {
        if (Text(record, "timestamp") is not { } text) return;
        DateTimeOffset t = Time(text, file, line);
        if (first == null || t < first) first = t;
        if (last == null || t > last) last = t;
    }

    private static List<Usage> Rows(string harness, string id, string folder, Dictionary<string, Acc> models, DateTimeOffset? first,
        DateTimeOffset? last, string costSource, string file, DateTimeOffset now)
    {
        if (first == null || last == null) throw new InvalidDataException($"{file}: session {id} has no timestamps");
        if (models.Count == 0) models[NoModel] = new Acc();
        return models.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => new Usage(harness, id, m.Key, m.Value.Input,
            m.Value.Output, m.Value.CacheRead, m.Value.CacheWrite, m.Value.Thinking, m.Value.Cost, costSource, first.Value,
            last.Value, folder, now)).ToList();
    }

    private static Acc For(Dictionary<string, Acc> models, string model)
    {
        if (!models.TryGetValue(model, out Acc? acc)) models[model] = acc = new Acc();
        return acc;
    }

    // ---- Claude Code ---------------------------------------------------------------------------------------------

    private static string ClaudeDir(string projectsRoot, string folder) => Path.Combine(projectsRoot, Regex.Replace(folder, "[^A-Za-z0-9]", "-"));

    private static string PiDir(string sessionsRoot, string folder) =>
        Path.Combine(sessionsRoot, "--" + Regex.Replace(folder.TrimStart('/', '\\'), @"[/\\:]", "-") + "--");

    /// <summary>Claude Code's transcripts of the folder; costs are filled in by ApplyClaudeCost.</summary>
    internal static List<Usage> ReadClaude(string projectsRoot, string folder, DateTimeOffset now)
    {
        var result = new List<Usage>();
        string dir = ClaudeDir(projectsRoot, folder);
        if (!Directory.Exists(dir)) return result;
        foreach (string file in Directory.GetFiles(dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            var messages = new Dictionary<string, (string Model, long In, long Out, long CR, long CW, long? Think)>();
            DateTimeOffset? first = null, last = null;
            bool otherFolder = false;
            foreach (var (line, record) in ReadJsonLines(file))
            {
                if (Text(record, "cwd") is { } cwd && !FolderEquals(cwd, folder)) { otherFolder = true; break; }
                Span(record, file, line, ref first, ref last);
                if (Text(record, "type") != "assistant" || Get(record, "message", "usage") is null) continue;
                string model = RequiredText(record, file, line, "message", "model");
                if (model == "<synthetic>") continue;
                // One API response is streamed as several records sharing message id and request id; the last one is final.
                string key = (Text(record, "message", "id") ?? RequiredText(record, file, line, "uuid")) + "|" + Text(record, "requestId");
                messages[key] = (model, RequiredNumber(record, file, line, "message", "usage", "input_tokens"),
                    RequiredNumber(record, file, line, "message", "usage", "output_tokens"),
                    Number(record, file, line, "message", "usage", "cache_read_input_tokens") ?? 0,
                    Number(record, file, line, "message", "usage", "cache_creation_input_tokens") ?? 0,
                    Number(record, file, line, "message", "usage", "output_tokens_details", "thinking_tokens"));
            }
            if (otherFolder) continue;
            var models = new Dictionary<string, Acc>();
            foreach (var m in messages.Values) For(models, m.Model).Add(m.In, m.Out, m.CR, m.CW, m.Think);
            result.AddRange(Rows("claude", id, folder, models, first, last, "ccusage", file, now));
        }
        return result;
    }

    /// <summary>Fills each Claude row's cost from ccusage's per-model breakdown of the same session.</summary>
    internal static List<Usage> ApplyClaudeCost(List<Usage> rows, string ccusageJson)
    {
        var costs = new Dictionary<(string, string), double>();
        foreach (JsonElement s in CcusageSessions(ccusageJson))
        {
            string id = Text(s, "sessionId") ?? throw new InvalidDataException("ccusage claude session without sessionId");
            if (Get(s, "modelBreakdowns") is not { ValueKind: JsonValueKind.Array } breakdowns)
                throw new InvalidDataException($"ccusage claude session {id} has no modelBreakdowns");
            foreach (JsonElement b in breakdowns.EnumerateArray())
                costs[(id, Text(b, "modelName") ?? throw new InvalidDataException($"ccusage session {id}: breakdown without modelName"))] =
                    Get(b, "cost") is { ValueKind: JsonValueKind.Number } c ? c.GetDouble() : throw new InvalidDataException($"ccusage session {id}: breakdown without cost");
        }
        return rows.Select(r => r.Model == NoModel ? r : costs.TryGetValue((r.SessionId, r.Model), out double c)
            ? r with { Cost = c }
            : throw new InvalidDataException($"ccusage reports no cost for claude session {r.SessionId}, model {r.Model}")).Select(r => AddListPrice(r)).ToList();
    }

    /// <summary>US dollars per million tokens (input, output, cache read, cache write) of the models that have no price in
    /// ccusage <see cref="CcusagePinned"/> (it reports 0 for them) or in pi, from LiteLLM's model_prices_and_context_window.json.
    /// Thinking tokens are part of output. Drop an entry when the pinned ccusage prices that model, or its cost is counted twice.</summary>
    private static readonly Dictionary<string, (double In, double Out, double CacheRead, double CacheWrite)> ListPrices = new()
    {
        ["claude-haiku-5-5"] = (0.1, 0.5, 0.01, 0.125),
        ["claude-sonnet-5-5"] = (2, 10, 0.10, 2.50),
        ["claude-opus-5-5"] = (4, 20, 0.20, 5),
        ["claude-fable-5-1"] = (10, 50, 0.25, 12.50),
        ["gpt-6-luna"] = (0.1, 0.5, 0.01, 0.125),
        ["gpt-6.1-terra"] = (1, 5, 0.05, 1.25),
        ["gpt-6-sol"] = (2, 10, 0.20, 2.50),
        ["gpt-6.1-sol"] = (2, 10, 0.10, 2.50),
        ["deepseek-v4-pro"] = (1.32, 3.96, 0.044, 0),
        ["deepseek-v4-flash"] = (0.3, 1.2, 0.006, 0),
        ["deepseek-flash"] = (0.3, 1.2, 0.006, 0),
        ["glm-5.3"] = (1.4, 4.4, 0.26, 0),
        ["glm-5.3-flash"] = (0.15, 0.5, 0.03, 0),
        ["glm-5.2"] = (1.4, 4.4, 0.26, 0),
        ["glm-5.1"] = (1.4, 4.4, 0.26, 0),
        ["glm-5"] = (1.0, 3.2, 0.2, 0),
        ["glm-4.7"] = (0.6, 2.2, 0.11, 0),
    };

    /// <summary>The model whose list price stands in for a model with no price anywhere, per harness.</summary>
    private static readonly Dictionary<string, string> DefaultPriceModel = new()
    {
        ["claude"] = "claude-haiku-5-5",
        ["codex"] = "gpt-6-luna",
        ["pi"] = "deepseek-flash",
    };

    private static double ListCost(Usage u, string model)
    {
        var p = ListPrices[model];
        return (u.Input * p.In + u.Output * p.Out + u.CacheRead * p.CacheRead + u.CacheWrite * p.CacheWrite) / 1e6;
    }

    /// <summary>Prices a row nothing else priced. A model in ListPrices is added to a ccusage row (ccusage reports 0 for it)
    /// or set on a pi row that recorded no cost. A model with no price at all is charged the harness's default model
    /// (<see cref="DefaultPriceModel"/>) when <paramref name="defaultPrice"/> is set, and its CostSource records "+default:<model>": the cost is an estimate, the model has no price in this config.
    /// Any other row is unchanged.</summary>
    internal static Usage AddListPrice(Usage u, bool defaultPrice = true)
    {
        bool listed = ListPrices.ContainsKey(u.Model);
        if (u.CostSource == "ccusage" && listed) return u with { Cost = u.Cost + ListCost(u, u.Model) };
        if (u.CostSource == "pi-recorded" && listed && u.Cost == 0 && u.Tokens > 0) return u with { Cost = ListCost(u, u.Model) };
        if (defaultPrice && !listed && u.Model != NoModel && u.Cost == 0 && u.Tokens > 0 && DefaultPriceModel.TryGetValue(u.Harness, out string? fallback))
            return u with { Cost = ListCost(u, fallback), CostSource = $"{u.CostSource}+default:{fallback}" };
        return u;
    }

    private static IEnumerable<JsonElement> CcusageSessions(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (Get(doc.RootElement, "sessions") is not { ValueKind: JsonValueKind.Array } sessions)
                throw new InvalidDataException("ccusage output has no \"sessions\" array");
            return sessions.EnumerateArray().Select(s => s.Clone()).ToList();
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("ccusage output is not JSON: " + e.Message);
        }
    }

    // ---- codex ---------------------------------------------------------------------------------------------------

    /// <summary>codex rollouts whose session_meta cwd is the folder; tokens are the growth of the cumulative counter
    /// between token_count events, charged to the model of the turn they fall in. Costs are filled in by ApplyCodexCost.</summary>
    internal static List<Usage> ReadCodex(string sessionsRoot, string folder, DateTimeOffset now)
    {
        var result = new List<Usage>();
        if (!Directory.Exists(sessionsRoot)) return result;
        foreach (string file in Directory.EnumerateFiles(sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string id = name.Length >= 36 ? name[^36..] : "";
            if (!Guid.TryParse(id, out _)) throw new InvalidDataException($"{file}: the file name does not end in the rollout uuid");
            var models = new Dictionary<string, Acc>();
            DateTimeOffset? first = null, last = null;
            string? model = null;
            long prevIn = 0, prevCached = 0, prevWrite = 0, prevOut = 0, prevReason = 0;
            bool seenMeta = false, otherFolder = false;
            foreach (var (line, record) in ReadJsonLines(file))
            {
                string type = Text(record, "type") ?? "";
                if (!seenMeta)
                {
                    if (type != "session_meta") throw new InvalidDataException($"{file}:{line}: the first record is not session_meta");
                    seenMeta = true;
                    if (!FolderEquals(RequiredText(record, file, line, "payload", "cwd"), folder)) { otherFolder = true; break; }
                }
                Span(record, file, line, ref first, ref last);
                if (type == "turn_context") model = RequiredText(record, file, line, "payload", "model");
                if (type != "event_msg" || Text(record, "payload", "type") != "token_count" || Get(record, "payload", "info") is null) continue;
                if (model == null) throw new InvalidDataException($"{file}:{line}: token_count before any turn_context model");
                long tIn = RequiredNumber(record, file, line, "payload", "info", "total_token_usage", "input_tokens");
                long tCached = Number(record, file, line, "payload", "info", "total_token_usage", "cached_input_tokens") ?? 0;
                long tWrite = Number(record, file, line, "payload", "info", "total_token_usage", "cache_write_input_tokens") ?? 0;
                long tOut = RequiredNumber(record, file, line, "payload", "info", "total_token_usage", "output_tokens");
                long tReason = Number(record, file, line, "payload", "info", "total_token_usage", "reasoning_output_tokens") ?? 0;
                if (tIn < prevIn || tCached < prevCached || tWrite < prevWrite || tOut < prevOut || tReason < prevReason)
                    throw new InvalidDataException($"{file}:{line}: the cumulative token counters went down");
                // codex counts cached input inside input; ccusage and the table show the uncached part as Input.
                For(models, model).Add((tIn - prevIn) - (tCached - prevCached), tOut - prevOut, tCached - prevCached, tWrite - prevWrite, tReason - prevReason);
                (prevIn, prevCached, prevWrite, prevOut, prevReason) = (tIn, tCached, tWrite, tOut, tReason);
            }
            if (otherFolder) continue;
            result.AddRange(Rows("codex", id, folder, models, first, last, "ccusage", file, now));
        }
        return result;
    }

    /// <summary>ccusage prices a codex session as a whole; the cost goes on the session's largest model row (the others
    /// stay 0), so the session total is right without inventing a per-model split.</summary>
    internal static List<Usage> ApplyCodexCost(List<Usage> rows, string ccusageJson)
    {
        var costs = new Dictionary<string, double>();
        foreach (JsonElement s in CcusageSessions(ccusageJson))
        {
            string file = Text(s, "sessionFile") ?? throw new InvalidDataException("ccusage codex session without sessionFile");
            costs[file.Length >= 36 ? file[^36..] : file] =
                Get(s, "costUSD") is { ValueKind: JsonValueKind.Number } c ? c.GetDouble() : throw new InvalidDataException($"ccusage codex session {file} has no costUSD");
        }
        var result = new List<Usage>();
        foreach (var session in rows.GroupBy(r => r.SessionId))
        {
            var sessionRows = session.ToList();
            if (sessionRows.All(r => r.Tokens == 0)) { result.AddRange(sessionRows); continue; }  // no usage recorded: ccusage lists no such session
            if (!costs.TryGetValue(session.Key, out double cost))
                throw new InvalidDataException($"ccusage reports no cost for codex session {session.Key}");
            Usage largest = sessionRows.OrderByDescending(r => r.Tokens).First();
            result.AddRange(sessionRows.Select(r => AddListPrice(r == largest ? r with { Cost = cost } : r, defaultPrice: cost == 0)));
        }
        return result;
    }

    // ---- pi ------------------------------------------------------------------------------------------------------

    /// <summary>pi's sessions of the folder; pi records the cost of every message itself.</summary>
    internal static List<Usage> ReadPi(string sessionsRoot, string folder, DateTimeOffset now)
    {
        var result = new List<Usage>();
        string dir = PiDir(sessionsRoot, folder);
        if (!Directory.Exists(dir)) return result;
        foreach (string file in Directory.GetFiles(dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string id = name[(name.LastIndexOf('_') + 1)..];
            var models = new Dictionary<string, Acc>();
            DateTimeOffset? first = null, last = null;
            bool seenSession = false, otherFolder = false;
            foreach (var (line, record) in ReadJsonLines(file))
            {
                if (!seenSession)
                {
                    if (Text(record, "type") != "session") throw new InvalidDataException($"{file}:{line}: the first record is not the session header");
                    seenSession = true;
                    if (!FolderEquals(RequiredText(record, file, line, "cwd"), folder)) { otherFolder = true; break; }
                }
                Span(record, file, line, ref first, ref last);
                if (Text(record, "type") != "message" || Text(record, "message", "role") != "assistant") continue;
                if (Get(record, "message", "usage") is null) throw new InvalidDataException($"{file}:{line}: assistant message without usage");
                Acc acc = For(models, RequiredText(record, file, line, "message", "model"));
                acc.Add(RequiredNumber(record, file, line, "message", "usage", "input"), RequiredNumber(record, file, line, "message", "usage", "output"),
                    Number(record, file, line, "message", "usage", "cacheRead") ?? 0, Number(record, file, line, "message", "usage", "cacheWrite") ?? 0,
                    Number(record, file, line, "message", "usage", "reasoning"));
                acc.Cost += Get(record, "message", "usage", "cost", "total") is { ValueKind: JsonValueKind.Number } c ? c.GetDouble()
                    : throw new InvalidDataException($"{file}:{line}: usage without cost.total");
            }
            if (otherFolder) continue;
            result.AddRange(Rows("pi", id, folder, models, first, last, "pi-recorded", file, now).Select(r => AddListPrice(r)));
        }
        return result;
    }

    // ---- ccusage -------------------------------------------------------------------------------------------------

    private static string Ccusage(string tool)
    {
        var (command, prefix) = Runner();
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : command,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows()) { start.ArgumentList.Add("/c"); start.ArgumentList.Add(command); }
        foreach (string a in prefix) start.ArgumentList.Add(a);
        foreach (string a in new[] { $"ccusage@{CcusagePinned}", tool, "session", "--json", "--breakdown", "--offline" }) start.ArgumentList.Add(a);
        string stdout = RunProcess(start, $"ccusage {tool} session");
        int brace = stdout.IndexOf('{');
        return brace >= 0 ? stdout[brace..] : throw new InvalidDataException($"ccusage {tool} session printed no JSON");
    }

    // bunx where installed, otherwise npx, which every machine with Node has (as CI/runUsageReport.cs).
    private static (string Command, string[] Prefix) Runner()
    {
        var probe = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "bunx",
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows()) { probe.ArgumentList.Add("/c"); probe.ArgumentList.Add("bunx"); }
        probe.ArgumentList.Add("--version");
        try
        {
            using var p = Process.Start(probe)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode == 0) return ("bunx", Array.Empty<string>());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // bunx is not installed: this is the choice between two runners, not a failure.
        }
        return ("npx", new[] { "-y" });
    }

    private static string RunProcess(ProcessStartInfo start, string what)
    {
        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new InvalidOperationException($"cannot run {what}: {start.FileName} is not available (Node.js is required for Claude and codex costs): {e.Message}");
        }
        using (process)
        {
            Task<string> err = process.StandardError.ReadToEndAsync();
            string stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"{what} failed with exit code {process.ExitCode}: {err.Result.Trim()}");
            return stdout;
        }
    }

    // ---- the store and the ledger --------------------------------------------------------------------------------

    internal static List<Usage> ReadStore(string path, string folder)
    {
        var result = new List<Usage>();
        if (!File.Exists(path)) return result;
        foreach (var (line, r) in ReadJsonLines(path))
        {
            string recordFolder = RequiredText(r, path, line, "folder");
            if (!FolderEquals(recordFolder, folder)) throw new InvalidDataException($"{path}:{line}: record is for folder {recordFolder}, not {folder}");
            string harness = RequiredText(r, path, line, "harness");
            if (!Harnesses.Contains(harness)) throw new InvalidDataException($"{path}:{line}: unknown harness {harness}");
            result.Add(new Usage(harness, RequiredText(r, path, line, "sessionId"), RequiredText(r, path, line, "model"),
                RequiredNumber(r, path, line, "input"), RequiredNumber(r, path, line, "output"), RequiredNumber(r, path, line, "cacheRead"),
                RequiredNumber(r, path, line, "cacheWrite"), Number(r, path, line, "thinking"),
                Get(r, "costUsd") is { ValueKind: JsonValueKind.Number } c ? c.GetDouble() : throw new InvalidDataException($"{path}:{line}: missing number \"costUsd\""),
                RequiredText(r, path, line, "costSource"), Time(RequiredText(r, path, line, "first"), path, line),
                Time(RequiredText(r, path, line, "last"), path, line), recordFolder, Time(RequiredText(r, path, line, "snapshotUtc"), path, line)));
        }
        return result;
    }

    /// <summary>A live record replaces the stored one when its token totals are greater or equal; the stored one stays when
    /// its transcript is gone or has less. Stored costs are never re-priced.</summary>
    internal static List<Row> Merge(List<Usage> live, List<Usage> stored)
    {
        var merged = new Dictionary<(string, string, string), Row>();
        foreach (Usage s in stored) merged[(s.Harness, s.SessionId, s.Model)] = new Row(s, false);
        foreach (Usage l in live)
        {
            var key = (l.Harness, l.SessionId, l.Model);
            if (!merged.TryGetValue(key, out Row? old) || l.Tokens >= old.Usage.Tokens) merged[key] = new Row(l, true);
        }
        return merged.Values.OrderByDescending(r => r.Usage.First).ThenBy(r => r.Usage.SessionId, StringComparer.Ordinal)
            .ThenBy(r => r.Usage.Model, StringComparer.Ordinal).ToList();
    }

    /// <summary>Writes a temp file beside the store, then renames it over it: the store is never half written.</summary>
    internal static void WriteStore(string path, List<Usage> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var text = new StringBuilder();
            foreach (Usage u in rows.OrderBy(u => u.Harness, StringComparer.Ordinal).ThenBy(u => u.SessionId, StringComparer.Ordinal).ThenBy(u => u.Model, StringComparer.Ordinal))
                text.Append(StoreLine(u)).Append('\n');
            File.WriteAllText(temp, text.ToString(), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string StoreLine(Usage u)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("harness", u.Harness);
            w.WriteString("sessionId", u.SessionId);
            w.WriteString("model", u.Model);
            w.WriteNumber("input", u.Input);
            w.WriteNumber("output", u.Output);
            w.WriteNumber("cacheRead", u.CacheRead);
            w.WriteNumber("cacheWrite", u.CacheWrite);
            if (u.Thinking is { } t) w.WriteNumber("thinking", t); else w.WriteNull("thinking");
            w.WriteNumber("costUsd", u.Cost);
            w.WriteString("costSource", u.CostSource);
            w.WriteString("first", Iso(u.First));
            w.WriteString("last", Iso(u.Last));
            w.WriteString("folder", u.Folder);
            w.WriteString("snapshotUtc", Iso(u.Snapshot));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static List<Launch> ReadLaunches(string path)
    {
        var result = new List<Launch>();
        if (!File.Exists(path)) return result;
        foreach (var (line, r) in ReadJsonLines(path))
        {
            string kind = RequiredText(r, path, line, "kind");
            if (kind != "Launcher" && kind != "Session") throw new InvalidDataException($"{path}:{line}: kind must be Launcher or Session, not {kind}");
            result.Add(new Launch(Time(RequiredText(r, path, line, "startUtc"), path, line), kind, RequiredText(r, path, line, "tool"),
                RequiredText(r, path, line, "harness"), RequiredText(r, path, line, "folder"), Text(r, "skill") ?? "", Text(r, "group") ?? DefaultGroup));
        }
        return result;
    }

    /// <summary>Session key (harness, id) to its session group: the group of the ledger entry the session matches, else
    /// DefaultGroup (a session started by hand, or by a launch that named no group).</summary>
    internal static Dictionary<(string, string), string> SessionGroups(List<Row> rows, List<Launch> launches)
    {
        var matches = MatchLaunches(rows, launches);
        return rows.Select(r => (r.Usage.Harness, r.Usage.SessionId)).Distinct()
            .ToDictionary(k => k, k => matches.TryGetValue(k, out Launch? l) ? l.Group : DefaultGroup);
    }

    /// <summary>The newest session (by first message) with its group and cost, or null when there is none.</summary>
    private static (Usage Newest, string Group, double Cost, DateTimeOffset First)? NewestSession(List<Row> rows, Dictionary<(string, string), string> of)
    {
        if (rows.Count == 0) return null;
        Usage newest = rows.OrderByDescending(r => r.Usage.First).First().Usage;
        var mine = rows.Where(r => r.Usage.SessionId == newest.SessionId && r.Usage.Harness == newest.Harness).ToList();
        return (newest, of[(newest.Harness, newest.SessionId)], mine.Sum(r => r.Usage.Cost), mine.Min(r => r.Usage.First));
    }

    internal static string RenderGroups(List<Row> rows, List<Launch> launches)
    {
        var of = SessionGroups(rows, launches);
        var sb = new StringBuilder();
        sb.Append("| Group | Sessions | Approx cost (USD) |\n|---|---:|---:|\n");
        foreach (var g in GroupTotals(rows, of))
            sb.Append($"| {g.Name} | {g.Sessions} | {g.Cost.ToString("0.0000", CultureInfo.InvariantCulture)} |\n");
        return sb.ToString();
    }

    internal static string RenderGroupsJson(List<Row> rows, List<Launch> launches)
    {
        var of = SessionGroups(rows, launches);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartArray("groups");
            foreach (var g in GroupTotals(rows, of))
            {
                w.WriteStartObject();
                w.WriteString("name", g.Name);
                w.WriteNumber("sessions", g.Sessions);
                w.WriteNumber("costUsd", g.Cost);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>Every group with a session, in name order.</summary>
    private static List<(string Name, int Sessions, double Cost)> GroupTotals(List<Row> rows, Dictionary<(string, string), string> of) =>
        of.Values.Distinct().Order(StringComparer.Ordinal)
            .Select(n => (n, of.Count(kv => kv.Value == n), rows.Where(r => of[(r.Usage.Harness, r.Usage.SessionId)] == n).Sum(r => r.Usage.Cost))).ToList();

    /// <summary>Session key (harness, id) to its ledger entry: same harness and folder, the session's first message within
    /// MatchWindow after startUtc, the closest pair first, one entry per session and one session per entry.</summary>
    internal static Dictionary<(string, string), Launch> MatchLaunches(List<Row> rows, List<Launch> launches)
    {
        var sessions = rows.GroupBy(r => (r.Usage.Harness, r.Usage.SessionId))
            .Select(g => (Key: g.Key, First: g.Min(r => r.Usage.First), Folder: g.First().Usage.Folder)).ToList();
        var pairs = new List<(TimeSpan Gap, (string, string) Key, Launch Launch)>();
        foreach (var s in sessions)
            foreach (Launch l in launches)
            {
                TimeSpan gap = s.First - l.StartUtc;
                if (l.Harness == s.Key.Harness && FolderEquals(l.Folder, s.Folder) && gap >= TimeSpan.Zero && gap <= MatchWindow)
                    pairs.Add((gap, s.Key, l));
            }
        var matched = new Dictionary<(string, string), Launch>();
        var used = new HashSet<Launch>();
        foreach (var p in pairs.OrderBy(p => p.Gap))
        {
            if (matched.ContainsKey(p.Key) || used.Contains(p.Launch)) continue;
            matched[p.Key] = p.Launch;
            used.Add(p.Launch);
        }
        return matched;
    }

    // ---- output --------------------------------------------------------------------------------------------------

    private static string StartedBy(Usage u, Dictionary<(string, string), Launch> matches) =>
        matches.TryGetValue((u.Harness, u.SessionId), out Launch? l) ? $"{l.Kind} ({l.Tool})" : "manual";

    private static bool Unpriced(Usage u) => u.CostSource == "ccusage" && u.Cost == 0 && u.Tokens > 0;

    internal static string RenderTable(List<Row> rows, List<Launch> launches)
    {
        var matches = MatchLaunches(rows, launches);
        var groups = SessionGroups(rows, launches);
        var sb = new StringBuilder();
        sb.Append("| Session id | Harness | Model | Input | Output | Cache read | Cache write | Thinking | Approx cost (USD) | Start | End | Started by | Group | Source |\n");
        sb.Append("|---|---|---|---:|---:|---:|---:|---:|---:|---|---|---|---|---|\n");
        foreach (Row r in rows)
        {
            Usage u = r.Usage;
            sb.Append($"| {u.SessionId} | {u.Harness} | {u.Model} | {u.Input} | {u.Output} | {u.CacheRead} | {u.CacheWrite} | {u.Thinking} | ")
              .Append(Unpriced(u) ? "unpriced" : u.Cost.ToString("0.0000", CultureInfo.InvariantCulture))
              .Append($" | {Iso(u.First)} | {Iso(u.Last)} | {StartedBy(u, matches)} | {groups[(u.Harness, u.SessionId)]} | {(r.Live ? "live" : "snapshot only")} |\n");
        }
        var all = rows.Select(r => r.Usage).ToList();
        long? thinking = all.Any(u => u.Thinking != null) ? all.Sum(u => u.Thinking ?? 0) : null;
        sb.Append($"| **TOTAL** | | | {all.Sum(u => u.Input)} | {all.Sum(u => u.Output)} | {all.Sum(u => u.CacheRead)} | {all.Sum(u => u.CacheWrite)} | {thinking} | {all.Sum(u => u.Cost).ToString("0.0000", CultureInfo.InvariantCulture)} | | | | | |\n");
        var unpriced = all.Where(Unpriced).Select(u => $"{u.Harness} {u.Model}").Distinct().Order().ToList();
        if (unpriced.Count > 0)
            sb.Append($"\n{all.Count(Unpriced)} rows are unpriced: ccusage {CcusagePinned} has no price for {string.Join(", ", unpriced)}; their cost is not in TOTAL.\n");
        var defaulted = all.Where(u => u.CostSource.Contains("+default:")).Select(u => $"{u.Harness} {u.Model} (priced as {u.CostSource[(u.CostSource.IndexOf("+default:", StringComparison.Ordinal) + 9)..]})").Distinct().Order().ToList();
        if (defaulted.Count > 0)
            sb.Append($"\n{defaulted.Count} kinds of rows use a default price, so their cost may not be accurate: {string.Join(", ", defaulted)}.\n");
        return sb.ToString();
    }

    internal static string RenderJson(List<Row> rows, List<Launch> launches)
    {
        var matches = MatchLaunches(rows, launches);
        var groups = SessionGroups(rows, launches);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartArray("rows");
            foreach (Row r in rows)
            {
                w.WriteStartObject();
                foreach (var part in StoreLine(r.Usage) is var line ? JsonDocument.Parse(line).RootElement.EnumerateObject() : default)
                    part.WriteTo(w);
                w.WriteString("startedBy", StartedBy(r.Usage, matches));
                w.WriteString("group", groups[(r.Usage.Harness, r.Usage.SessionId)]);
                w.WriteString("source", r.Live ? "live" : "snapshot only");
                w.WriteBoolean("unpriced", Unpriced(r.Usage));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            var all = rows.Select(r => r.Usage).ToList();
            w.WriteStartObject("total");
            w.WriteNumber("input", all.Sum(u => u.Input));
            w.WriteNumber("output", all.Sum(u => u.Output));
            w.WriteNumber("cacheRead", all.Sum(u => u.CacheRead));
            w.WriteNumber("cacheWrite", all.Sum(u => u.CacheWrite));
            if (all.Any(u => u.Thinking != null)) w.WriteNumber("thinking", all.Sum(u => u.Thinking ?? 0)); else w.WriteNull("thinking");
            w.WriteNumber("costUsd", all.Sum(u => u.Cost));
            w.WriteEndObject();
            // the newest session (the one running now), and the session group it belongs to with that group's total: what the
            // joint bar shows. No session yet: the default group, empty.
            var current = NewestSession(rows, groups);
            string group = current?.Group ?? DefaultGroup;
            w.WriteStartObject("group");
            w.WriteString("name", group);
            w.WriteNumber("sessions", groups.Count(kv => kv.Value == group));
            w.WriteNumber("costUsd", rows.Where(r => groups[(r.Usage.Harness, r.Usage.SessionId)] == group).Sum(r => r.Usage.Cost));
            w.WriteEndObject();
            if (current is not { } c) w.WriteNull("session");
            else
            {
                w.WriteStartObject("session");
                w.WriteString("harness", c.Newest.Harness);
                w.WriteString("sessionId", c.Newest.SessionId);
                w.WriteString("group", c.Group);
                w.WriteString("first", Iso(c.First));
                w.WriteNumber("costUsd", c.Cost);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
