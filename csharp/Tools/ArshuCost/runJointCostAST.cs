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
/// runJointCostAST: runJointCostAST.cs (tested and published, left unchanged) restructured as the shape a lean4 port proves:
/// the parsing of the command line, of the transcripts, the store and the ledger, the pricing, the merge, the grouping
/// and every rendering are functions of their input alone (a transcript arrives as its lines, a folder test as a
/// predicate), and the effects (reading the files, the store write, the ccusage process, the watcher, the console) sit
/// around them in Run, CollectAndMerge, ReadLive and Watch. The same command line, output and files as runJointCostAST.cs.
///
/// The cost reader of the joint tools. It lists every harness session (Claude Code, codex, pi) whose working folder is
/// the given folder, whoever started it, one row per (session, model), newest first, with a TOTAL row, as a markdown
/// table (or --json). The history lives in the folder itself, in &lt;folder&gt;/.joint-cost/: launches.jsonl (written by
/// the launcher and session tools) and usage.jsonl (written by this reader). Every run snapshots: it reads the live
/// transcripts, merges them into usage.jsonl and prints the table from the merged data, so a trimmed or lost transcript
/// never lowers a number already stored. --no-snapshot merges in memory and writes nothing.
///
/// Repository-independent single file; needs .NET 10+ only. Claude and codex costs come from the built-in price table; --updatecost
/// runs ccusage (pinned, needs Node) to price them with its numbers instead.
/// pi records its own cost. See runJointCostAST.md.
///
/// Usage: dotnet run csharp/ArshuCost/runJointCostAST.cs -- [--folder &lt;path&gt;] [--harness claude,codex,pi] [--json] [--no-snapshot]
///        [--updatecost] [--groups] | --watch --harness &lt;one&gt; [--interval-minutes 5] [--idle-minutes 10]
/// </summary>
class RunJointCostAST
{
    private const string CcusagePinned = "20.0.17";
    private const string StoreFolder = ".joint-cost";
    private const string UsageFile = "usage.jsonl";
    private const string LaunchesFile = "launches.jsonl";
    internal const string DefaultGroup = "Default";
    private const string NoModel = "-";
    private const string UsageLine = "--folder <path> --harness claude,codex,pi --json --no-snapshot --updatecost --groups | --watch --harness <one> --interval-minutes 5 --idle-minutes 10";
    internal static readonly string[] Harnesses = { "claude", "codex", "pi" };
    internal static readonly TimeSpan MatchWindow = TimeSpan.FromSeconds(120);

    /// <summary>The exception a refusal is thrown as: a bad option, a malformed record or ccusage answer, a process that fails, a lock.</summary>
    internal enum CostKind { Argument, Data, Operation, Io }

    /// <summary>Why the reader refuses (lean4 CostError). Message is the text the tool prints; ToException is the refusal as the
    /// exception of its kind, which is how the reader's shell (Launch) and its callers see it.</summary>
    internal sealed class CostError
    {
        private readonly CostKind kind;
        private readonly string text;

        internal CostError(CostKind kind, string text)
        {
            this.kind = kind;
            this.text = text;
        }

        internal string Message() => text;

        internal Exception ToException() => kind switch
        {
            CostKind.Argument => new ArgumentException(Message()),
            CostKind.Data => new InvalidDataException(Message()),
            CostKind.Operation => new InvalidOperationException(Message()),
            _ => new IOException(Message()),
        };

        internal static Exception Argument(string text) => new CostError(CostKind.Argument, text).ToException();

        internal static Exception Data(string text) => new CostError(CostKind.Data, text).ToException();

        internal static Exception Operation(string text) => new CostError(CostKind.Operation, text).ToException();

        internal static Exception Io(string text) => new CostError(CostKind.Io, text).ToException();
    }

    /// <summary>One (harness, session, model) with every token field, its cost and where the cost came from.</summary>
    internal sealed record Usage(string Harness, string SessionId, string Model, long Input, long Output, long CacheRead,
        long CacheWrite, long? Thinking, double Cost, string CostSource, DateTimeOffset First, DateTimeOffset Last,
        string Folder, DateTimeOffset Snapshot)
    {
        public long Tokens => RunJointCostAST.Tokens(this);
    }

    /// <summary>A launch ledger line: the launcher or session tool that started a harness in a folder, when, and the session
    /// group it was launched in (--group; DefaultGroup when none was given, also for a line written before groups existed).
    /// Folder is the normalized folder (ReadLaunches).</summary>
    internal sealed record LaunchEntry(DateTimeOffset StartUtc, string Kind, string Tool, string Harness, string Folder, string? Skill, string Group = DefaultGroup);

    /// <summary>A merged row: the usage and whether the live transcript supplied it.</summary>
    internal sealed record Row(Usage Usage, bool Live);

    /// <summary>The command line of the reader (ParseArgs): what to read, what to print and whether to watch.</summary>
    internal sealed record CostArgs(string Folder, List<string> Harnesses, bool Json, bool Snapshot, bool Watch, bool Groups,
        bool UpdateCost, double IntervalMinutes, double IdleMinutes);

    /// <summary>The totals of one model within a session: every token field and the cost recorded so far.</summary>
    internal sealed record Acc(long Input, long Output, long CacheRead, long CacheWrite, long? Thinking, double Cost)
    {
        public Acc Add(long input, long output, long cacheRead, long cacheWrite, long? thinking) =>
            RunJointCostAST.Add(this, input, output, cacheRead, cacheWrite, thinking);
    }

    /// <summary>The token total of a row: input, output, cache read and cache write (lean4 Usage.tokens).</summary>
    internal static long Tokens(Usage usage) => usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite;

    /// <summary>The totals with one message's counts added; thinking stays unset until a message reports it (lean4 Acc.add).</summary>
    internal static Acc Add(Acc acc, long input, long output, long cacheRead, long cacheWrite, long? thinking) =>
        new(acc.Input + input, acc.Output + output, acc.CacheRead + cacheRead, acc.CacheWrite + cacheWrite,
            thinking == null ? acc.Thinking : (acc.Thinking ?? 0) + thinking, acc.Cost);

    /// <summary>The totals of a model nothing was counted for yet.</summary>
    internal static readonly Acc NoAcc = new(0, 0, 0, 0, null, 0);

    /// <summary>What Utf8JsonWriter puts between lines when it writes indented (the platform's, its default).</summary>
    private static readonly string NewLine = Environment.NewLine;

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

    // ---- utilities: they call no other method of the tool ------------------------------------------------------------

    private static double Minutes(string setting, string name) =>
        double.TryParse(setting, NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m > 0 ? m : throw CostError.Argument($"{name} needs a positive number of minutes, not {setting}");

    /// <summary>The value of the option at <paramref name="index"/>: the argument after it.</summary>
    private static string ArgValue(string[] args, int index) =>
        index + 1 < args.Length ? args[index + 1] : throw CostError.Argument($"{args[index]} needs a value");

    /// <summary>The watcher's options are legal together: one harness, and none of the options that print a table.</summary>
    internal static void CheckWatch(CostArgs a)
    {
        if (!a.Watch) return;
        if (a.Harnesses.Count != 1) throw CostError.Argument("--watch needs exactly one --harness");
        if (!a.Snapshot || a.Json || a.UpdateCost) throw CostError.Argument("--watch snapshots and prints no table: it takes none of --no-snapshot, --json and --updatecost");
    }

    /// <summary>The quotient of a division that rounds toward negative infinity (Lean's Int.ediv for a positive divisor).</summary>
    private static long FloorDiv(long a, long b)
    {
        long q = a / b;
        return a % b != 0 && (a < 0) != (b < 0) ? q - 1 : q;
    }

    /// <summary>The remainder that goes with FloorDiv: never negative for a positive divisor.</summary>
    private static long FloorMod(long a, long b) => (a % b + b) % b;

    /// <summary>The day count since 1970-01-01 of a civil date (Hinnant's days_from_civil); the year is at least 1.</summary>
    internal static long DaysFromCivil(long year, int month, int day)
    {
        long y = month <= 2 ? year - 1 : year;
        long era = y / 400;
        long yoe = y - era * 400;
        long doy = (153 * ((month + 9) % 12) + 2) / 5 + day - 1;
        long doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097 + doe - 719468;
    }

    /// <summary>Whether the year is a leap year of the Gregorian calendar.</summary>
    internal static bool IsLeapYear(long year) => (year % 4 == 0 && year % 100 != 0) || year % 400 == 0;

    /// <summary>The four-decimal text of a whole number of ten-thousandths.</summary>
    internal static string Fixed4Text(bool negative, System.Numerics.BigInteger scaled)
    {
        string shown = string.Create(CultureInfo.InvariantCulture, $"{scaled / 10000}.{(int)(scaled % 10000):D4}");
        return negative ? "-" + shown : shown;
    }

    /// <summary>Whether 10^k is at most the positive number num / den.</summary>
    internal static bool PowerOfTenWithin(int k, System.Numerics.BigInteger num, System.Numerics.BigInteger den) =>
        k >= 0 ? System.Numerics.BigInteger.Pow(10, k) * den <= num : den <= num * System.Numerics.BigInteger.Pow(10, -k);

    /// <summary>The k with 10^k &lt;= num / den &lt; 10^(k+1), for positive num and den.</summary>
    internal static int DecimalExponent(System.Numerics.BigInteger num, System.Numerics.BigInteger den)
    {
        int k = (int)(((long)num.GetBitLength() - (long)den.GetBitLength()) * 30103 / 100000);
        while (!PowerOfTenWithin(k, num, den)) k--;
        while (PowerOfTenWithin(k + 1, num, den)) k++;
        return k;
    }

    /// <summary>The value mantissa * 2^exponent in ten-thousandths as the runtime's "0.0000" prints it: the runtime first rounds a double
    /// to its 15 significant digits (exactly, a tie to the even digit), then rounds that decimal to four places, half up. This differs
    /// from the exact value rounded half up on a value such as 0.00015 (the double is a little below the tie, its 15 digits are exactly it).</summary>
    internal static System.Numerics.BigInteger Fixed4Rounded(System.Numerics.BigInteger mantissa, int exponent)
    {
        if (mantissa.IsZero) return 0;
        System.Numerics.BigInteger num = exponent >= 0 ? mantissa * System.Numerics.BigInteger.Pow(2, exponent) : mantissa;
        System.Numerics.BigInteger den = exponent >= 0 ? 1 : System.Numerics.BigInteger.Pow(2, -exponent);
        int k = DecimalExponent(num, den);
        int shift = 14 - k;
        System.Numerics.BigInteger n = shift >= 0 ? num * System.Numerics.BigInteger.Pow(10, shift) : num;
        System.Numerics.BigInteger d = shift >= 0 ? den : den * System.Numerics.BigInteger.Pow(10, -shift);
        System.Numerics.BigInteger q = System.Numerics.BigInteger.DivRem(n, d, out System.Numerics.BigInteger r);
        System.Numerics.BigInteger twice = 2 * r;
        if (twice > d || (twice == d && !q.IsEven)) q += 1;
        // q * 10^(k-14) in ten-thousandths is q * 10^(k-10)
        if (k >= 10) return q * System.Numerics.BigInteger.Pow(10, k - 10);
        System.Numerics.BigInteger unit = System.Numerics.BigInteger.Pow(10, 10 - k);
        return (q + unit / 2) / unit;
    }

    private static JsonElement? Member(JsonElement e, params string[] path)
    {
        JsonElement cur = e;
        foreach (string name in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(name, out cur) || cur.ValueKind == JsonValueKind.Null) return null;
        }
        return cur;
    }

    /// <summary>The records of a file's lines, numbered from 1: a blank line, and a line of NUL bytes (the padding a crashed
    /// write leaves in a transcript, which holds no record), are skipped; NULs next to anything else are a malformed
    /// record. Lazy: a reader that stops early never parses the lines after it.</summary>
    internal static IEnumerable<(int Line, JsonElement Record)> JsonLines(string path, IEnumerable<string> lines)
    {
        int n = 0;
        foreach (string text in lines)
        {
            n++;
            if (text.Trim().Trim('\0').Length == 0) continue;
            JsonElement record;
            try
            {
                using var doc = JsonDocument.Parse(text);
                record = doc.RootElement.Clone();
            }
            catch (JsonException e)
            {
                throw CostError.Data($"{path}:{n}: malformed JSON record ({e.Message})");
            }
            if (record.ValueKind != JsonValueKind.Object) throw CostError.Data($"{path}:{n}: record is not a JSON object");
            yield return (n, record);
        }
    }

    private static Acc ModelAcc(Dictionary<string, Acc> models, string model) =>
        models.TryGetValue(model, out Acc? acc) ? acc : NoAcc;

    private static string ClaudeDir(string projectsRoot, string folder) => Path.Combine(projectsRoot, Regex.Replace(folder, "[^A-Za-z0-9]", "-"));

    private static string PiDir(string sessionsRoot, string folder) =>
        Path.Combine(sessionsRoot, "--" + Regex.Replace(folder.TrimStart('/', '\\'), @"[/\\:]", "-") + "--");

    private static double ListCost(Usage u, string model)
    {
        var p = ListPrices[model];
        return (u.Input * p.In + u.Output * p.Out + u.CacheRead * p.CacheRead + u.CacheWrite * p.CacheWrite) / 1e6;
    }

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

    /// <summary>The rows of one session, one per model in model order (a session with no usage is one row of NoModel).</summary>
    private static List<Usage> SessionRows(string harness, string id, string folder, Dictionary<string, Acc> models, DateTimeOffset? first,
        DateTimeOffset? last, string costSource, string file, DateTimeOffset now)
    {
        if (first == null || last == null) throw CostError.Data($"{file}: session {id} has no timestamps");
        var byModel = models.Count == 0 ? new Dictionary<string, Acc> { [NoModel] = NoAcc } : models;
        return byModel.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => new Usage(harness, id, m.Key, m.Value.Input,
            m.Value.Output, m.Value.CacheRead, m.Value.CacheWrite, m.Value.Thinking, m.Value.Cost, costSource, first.Value,
            last.Value, folder, now)).ToList();
    }

    /// <summary>The arguments of the pinned ccusage run that lists a tool's sessions with their model breakdown.</summary>
    private static string[] CcusageArgs(string tool) => new[] { $"ccusage@{CcusagePinned}", tool, "session", "--json", "--breakdown", "--offline" };

    /// <summary>ccusage's JSON: its standard output from the first brace (a runner may print a banner before it).</summary>
    private static string CcusageJson(string tool, string stdout)
    {
        int brace = stdout.IndexOf('{');
        return brace >= 0 ? stdout[brace..] : throw CostError.Data($"ccusage {tool} session printed no JSON");
    }

    private static string StartedBy(Usage u, Dictionary<(string, string), LaunchEntry> launched) =>
        launched.TryGetValue((u.Harness, u.SessionId), out LaunchEntry? l) ? $"{l.Kind} ({l.Tool})" : "manual";

    /// <summary>The folder without its trailing separators, a root left as it is.</summary>
    internal static string TrimFolder(string full)
    {
        int rootLength = Path.GetPathRoot(full)!.Length;
        return full.Length > rootLength ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    /// <summary>The newest session (by first message) with its group (<paramref name="of"/>, from SessionGroups) and cost, or null when there is none.</summary>
    private static (Usage Newest, string Group, double Cost, DateTimeOffset First)? NewestSession(List<Row> rows, Dictionary<(string, string), string> of)
    {
        if (rows.Count == 0) return null;
        Usage newest = rows.OrderByDescending(r => r.Usage.First).First().Usage;
        var mine = rows.Where(r => r.Usage.SessionId == newest.SessionId && r.Usage.Harness == newest.Harness).ToList();
        return (newest, of[(newest.Harness, newest.SessionId)], mine.Sum(r => r.Usage.Cost), mine.Min(r => r.Usage.First));
    }

    /// <summary>Every group with a session, in name order.</summary>
    private static List<(string Name, int Sessions, double Cost)> GroupTotals(List<Row> rows, Dictionary<(string, string), string> of) =>
        of.Values.Distinct().Order(StringComparer.Ordinal)
            .Select(n => (n, of.Count(kv => kv.Value == n), rows.Where(r => of[(r.Usage.Harness, r.Usage.SessionId)] == n).Sum(r => r.Usage.Cost))).ToList();

    /// <summary>A compact JSON object (Utf8JsonWriter's default) whose members <paramref name="write"/> writes.</summary>
    private static string JsonText(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // ---- the effects at the edge: files, processes ---------------------------------------------------------------------

    /// <summary>The lines of a file. Opened shareable for reading and writing: a harness that is running holds its transcript open for writing, and the default read-only share would refuse
    /// the read on Windows ("the process cannot access the file") while the session is live, which is when the cost is wanted.</summary>
    internal static IEnumerable<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        string? text;
        while ((text = reader.ReadLine()) != null) yield return text;
    }

    /// <summary>The lines of a file's bytes, split at LF, CR LF or CR as StreamReader.ReadLine does, a leading byte order mark dropped.</summary>
    internal static List<string> LinesOfBytes(string path, byte[] bytes)
    {
        var lines = new List<string>();
        using var reader = new StreamReader(new MemoryStream(bytes));
        string? text;
        while ((text = reader.ReadLine()) != null) lines.Add(text);
        return lines;
    }

    /// <summary>What the head of a file has told so far: its bytes, whether a byte with content has come, and whether the first
    /// record has ended (the bytes after that are not wanted).</summary>
    internal sealed class HeadScan
    {
        internal readonly List<byte> Bytes = new();
        internal bool SeenContent;
        internal bool Done;

        /// <summary>One byte of the head: kept until the first record's terminator; a byte that is not blank (or part of a byte
        /// order mark) is content. The scan is threaded through the bytes of one head, so it is updated in place and returned.</summary>
        internal HeadScan Push(byte b)
        {
            if (Done) return this;
            Bytes.Add(b);
            if (b == 10 || b == 13) Done = SeenContent;
            else if (!(b == 0 || b == 9 || b == 11 || b == 12 || b == 32 || b == 0xEF || b == 0xBB || b == 0xBF)) SeenContent = true;
            return this;
        }
    }

    /// <summary>Reads chunks until the first record has ended or the file does (the fuel is the number of chunks the file can have).</summary>
    internal static HeadScan ReadHeadChunks(Stream handle, HeadScan scan, int fuel)
    {
        var chunk = new byte[65536];
        while (fuel > 0 && !scan.Done)
        {
            fuel--;
            int read = handle.Read(chunk, 0, chunk.Length);
            if (read == 0) break;
            for (int i = 0; i < read; i++) scan = scan.Push(chunk[i]);
        }
        return scan;
    }

    /// <summary>The lines of a file up to and including its first record (the first line with content), read in chunks so that a
    /// large transcript is not read whole. Opened shareable like ReadLines.</summary>
    internal static List<string> ReadHeadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        HeadScan scan = ReadHeadChunks(stream, new HeadScan(), (int)(stream.Length / 65536) + 2);
        return LinesOfBytes(path, scan.Bytes.ToArray());
    }

    private static string[] Jsonl(string dir) => Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jsonl") : Array.Empty<string>();

    private static bool PidAlive(int pid)
    {
        foreach (Process p in Process.GetProcesses())
            using (p)
                if (p.Id == pid) return !p.HasExited;
        return false;
    }

    internal static void ReleaseLock(string path, int pid)
    {
        if (File.Exists(path) && File.ReadAllText(path).Trim() == pid.ToString(CultureInfo.InvariantCulture)) File.Delete(path);
    }

    private static bool OnPath(string program) =>
        Environment.GetEnvironmentVariable("PATH") is { } path
        && path.Split(Path.PathSeparator).Any(dir => dir.Length > 0 && File.Exists(Path.Combine(dir, program)));

    private static string RunProcess(ProcessStartInfo start, string what)
    {
        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw CostError.Operation($"cannot run {what}: {start.FileName} is not available (Node.js is required for Claude and codex costs): {e.Message}");
        }
        using (process)
        {
            Task<string> err = process.StandardError.ReadToEndAsync();
            string stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw CostError.Operation($"{what} failed with exit code {process.ExitCode}: {err.Result.Trim()}");
            return stdout;
        }
    }

    /// <summary>The session id a rollout's file name ends in (a uuid).</summary>
    internal static string CodexRolloutId(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        string id = name.Length >= 36 ? name[^36..] : "";
        if (!Guid.TryParse(id, out _)) throw CostError.Data($"{file}: the file name does not end in the rollout uuid");
        return id;
    }

    // ---- helpers: they call the utilities ------------------------------------------------------------------------------

    /// <summary>The civil date (year, month, day) of a day count since 1970-01-01, proleptic Gregorian (Hinnant's civil_from_days).</summary>
    internal static (long Year, int Month, int Day) CivilFromDays(long days)
    {
        long z = days + 719468;
        long era = FloorDiv(z, 146097);
        long doe = z - era * 146097;
        long yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        long doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        long mp = (5 * doy + 2) / 153;
        int day = (int)(doy - (153 * mp + 2) / 5 + 1);
        int month = (int)(mp < 10 ? mp + 3 : mp - 9);
        long year = yoe + era * 400 + (month <= 2 ? 1 : 0);
        return (year, month, day);
    }

    /// <summary>The days of a month of a year.</summary>
    internal static int MonthLength(long year, int month) =>
        month == 2 ? (IsLeapYear(year) ? 29 : 28) : (month == 4 || month == 6 || month == 9 || month == 11) ? 30 : 31;

    /// <summary>t in UTC as yyyy-MM-dd'T'HH:mm:ss.fff'Z': the time cut to milliseconds, the date from CivilFromDays.</summary>
    private static string Iso(DateTimeOffset t)
    {
        const long unixEpochTicks = 621355968000000000L;
        long milliseconds = FloorDiv(t.UtcTicks - unixEpochTicks, 10000);
        long seconds = FloorDiv(milliseconds, 1000);
        long days = FloorDiv(seconds, 86400);
        int secondOfDay = (int)FloorMod(seconds, 86400);
        var (year, month, day) = CivilFromDays(days);
        return string.Create(CultureInfo.InvariantCulture,
            $"{year:D4}-{month:D2}-{day:D2}T{secondOfDay / 3600:D2}:{secondOfDay % 3600 / 60:D2}:{secondOfDay % 60:D2}.{FloorMod(milliseconds, 1000):D3}Z");
    }

    /// <summary>The model of a four-decimal text for a finite double given as its sign and mantissa * 2^exponent: the runtime's 15
    /// significant digits of the exact value, rounded to four decimals half up (Fixed4Rounded). It is the Lean port's text and the
    /// subject of its laws; the tool prints through Fixed4, which uses the runtime's own "0.0000", and a test holds the two equal.</summary>
    internal static string Fixed4Core(bool negative, System.Numerics.BigInteger mantissa, int exponent) =>
        Fixed4Text(negative, Fixed4Rounded(mantissa, exponent));

    /// <summary>x.ToString("0.0000", InvariantCulture). A value that is a whole number of sixteenths below 1e9 has at most four decimals
    /// and at most 14 digits, so the runtime's 15-digit rounding changes nothing and Fixed4Core prints the same text.</summary>
    private static string Fixed4(double x)
    {
        double sixteenths = x * 16;
        if (Math.Abs(x) < 1e9 && sixteenths == Math.Floor(sixteenths))
        {
            long bits = BitConverter.DoubleToInt64Bits(x);
            int exponentBits = (int)((bits >> 52) & 0x7FF);
            long fraction = bits & 0xFFFFFFFFFFFFFL;
            return exponentBits == 0 ? Fixed4Core(bits < 0, fraction, -1074) : Fixed4Core(bits < 0, fraction + (1L << 52), exponentBits - 1075);
        }
        return x.ToString("0.0000", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset Time(string stamp, string file, int line)
    {
        if (!DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var t))
            throw CostError.Data($"{file}:{line}: \"{stamp}\" is not a timestamp");
        DateTime utc = t.UtcDateTime;
        if (utc.Day > MonthLength(utc.Year, utc.Month)) throw CostError.Data($"{file}:{line}: \"{stamp}\" is not a timestamp");
        const long unixEpochTicks = 621355968000000000L;
        return new DateTimeOffset(unixEpochTicks + DaysFromCivil(utc.Year, utc.Month, utc.Day) * TimeSpan.TicksPerDay + utc.TimeOfDay.Ticks, TimeSpan.Zero);
    }

    /// <summary>A row whose tokens are those the store already has keeps the store's cost (ccusage's, after --updatecost, or an
    /// earlier price): nothing changed, so there is nothing to price again.</summary>
    internal static Usage KeepStoredCost(Usage live, List<Usage> stored)
    {
        Usage? old = stored.FirstOrDefault(s => s.Harness == live.Harness && s.SessionId == live.SessionId && s.Model == live.Model);
        return old != null && old.Tokens == live.Tokens && old.Cost > 0 ? live with { Cost = old.Cost, CostSource = old.CostSource } : live;
    }


    private static bool Unpriced(Usage u) => u.CostSource == "ccusage" && u.Cost == 0 && u.Tokens > 0;

    /// <summary>Reads the options from <paramref name="index"/> on, one step per argument (the fuel is the number of arguments): each
    /// option sets its field, a value option takes the argument after it, anything else is refused.</summary>
    internal static CostArgs ReadOptions(string[] args, CostArgs a, int fuel, int index)
    {
        while (fuel > 0 && index < args.Length)
        {
            fuel--;
            switch (args[index])
            {
                case "--folder": a = a with { Folder = ArgValue(args, index) }; index += 2; break;
                case "--harness": a = a with { Harnesses = ArgValue(args, index).Split(',').Select(h => h.Trim()).ToList() }; index += 2; break;
                case "--json": a = a with { Json = true }; index++; break;
                case "--no-snapshot": a = a with { Snapshot = false }; index++; break;
                case "--updatecost": a = a with { UpdateCost = true }; index++; break;
                case "--watch": a = a with { Watch = true }; index++; break;
                case "--groups": a = a with { Groups = true }; index++; break;
                case "--interval-minutes": a = a with { IntervalMinutes = Minutes(ArgValue(args, index), "--interval-minutes") }; index += 2; break;
                case "--idle-minutes": a = a with { IdleMinutes = Minutes(ArgValue(args, index), "--idle-minutes") }; index += 2; break;
                default: throw CostError.Argument($"unknown argument {args[index]}; usage: {UsageLine}");
            }
        }
        return a;
    }

    private static CostArgs ParseArgs(string[] args, string cwd)
    {
        CostArgs a = ReadOptions(args, new CostArgs(cwd, Harnesses.ToList(), false, true, false, false, false, 5, 10), args.Length, 0);
        foreach (string h in a.Harnesses)
            if (!Harnesses.Contains(h)) throw CostError.Argument($"unknown harness {h}; use claude, codex or pi");
        return a;
    }

    private static string LogLine(DateTimeOffset now, string text) => $"{Iso(now)} {text}\n";


    private static string? Text(JsonElement e, params string[] path) =>
        Member(e, path) is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    private static long? Number(JsonElement e, string file, int line, params string[] path)
    {
        if (Member(e, path) is not { } v) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out long n))
            throw CostError.Data($"{file}:{line}: \"{string.Join('.', path)}\" is not an integer");
        return n;
    }

    /// <summary>Two folders are the same folder: equal without their trailing separators (case-blind on Windows). Both are
    /// expected normalized (NormalizeFolder).</summary>
    internal static bool SameFolder(string a, string b) =>
        string.Equals(TrimFolder(a), TrimFolder(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Whether folder <paramref name="a"/> is folder <paramref name="b"/> or lies under it, component-wise: /a/b/c is under /a/b,
    /// /a/bc is not. Trailing separators and (on Windows) case do not matter, as in SameFolder. A transcript whose cwd is a subfolder of
    /// the folder (a harness that did cd folder/skills) belongs to the folder.</summary>
    internal static bool UnderFolder(string a, string b)
    {
        string x = TrimFolder(a), y = TrimFolder(b);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(x, y, comparison)) return true;
        if (y.Length == 0 || x.Length <= y.Length || !x.StartsWith(y, comparison)) return false;
        return IsSeparator(y[^1]) || IsSeparator(x[y.Length]);
    }

    private static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;

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
            if (Member(doc.RootElement, "sessions") is not { ValueKind: JsonValueKind.Array } sessions)
                throw CostError.Data("ccusage output has no \"sessions\" array");
            return sessions.EnumerateArray().Select(s => s.Clone()).ToList();
        }
        catch (JsonException e)
        {
            throw CostError.Data("ccusage output is not JSON: " + e.Message);
        }
    }

    /// <summary>The members of a usage record, as the store keeps them (the store line and the --json rows).</summary>
    private static Action<Utf8JsonWriter> UsageFields(Usage u) => w =>
    {
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
    };

    private static string NormalizeFolder(string folder) => TrimFolder(Path.GetFullPath(folder));

    private static string RequiredText(JsonElement e, string file, int line, params string[] path) =>
        Text(e, path) ?? throw CostError.Data($"{file}:{line}: missing string \"{string.Join('.', path)}\"");

    private static long RequiredNumber(JsonElement e, string file, int line, params string[] path) =>
        Number(e, file, line, path) ?? throw CostError.Data($"{file}:{line}: missing number \"{string.Join('.', path)}\"");

    /// <summary>Widens the session span by the record's own "timestamp" (absent on bookkeeping records).</summary>
    private static (DateTimeOffset? First, DateTimeOffset? Last) WidenSpan(JsonElement record, string file, int line, DateTimeOffset? first, DateTimeOffset? last)
    {
        if (Text(record, "timestamp") is not { } text) return (first, last);
        DateTimeOffset t = Time(text, file, line);
        return (first == null || t < first ? t : first, last == null || t > last ? t : last);
    }

    /// <summary>Prices a Claude or codex row from its tokens with the built-in table: a model in it at its price, any other
    /// model at the harness's default model (CostSource "list-price+default:&lt;model&gt;", an estimate); no tokens, no cost.
    /// <c>--updatecost</c> replaces these numbers with ccusage's.</summary>
    internal static Usage PlainPrice(Usage u)
    {
        var priced = u with { Cost = 0, CostSource = "list-price" };
        return ListPrices.ContainsKey(u.Model) ? priced with { Cost = ListCost(u, u.Model) } : AddListPrice(priced);
    }

    /// <summary>Fills each Claude row's cost from ccusage's per-model breakdown of the same session.</summary>
    internal static List<Usage> ApplyClaudeCost(List<Usage> rows, string ccusageJson)
    {
        var costs = new Dictionary<(string, string), double>();
        foreach (JsonElement s in CcusageSessions(ccusageJson))
        {
            string id = Text(s, "sessionId") ?? throw CostError.Data("ccusage claude session without sessionId");
            if (Member(s, "modelBreakdowns") is not { ValueKind: JsonValueKind.Array } breakdowns)
                throw CostError.Data($"ccusage claude session {id} has no modelBreakdowns");
            foreach (JsonElement b in breakdowns.EnumerateArray())
                costs[(id, Text(b, "modelName") ?? throw CostError.Data($"ccusage session {id}: breakdown without modelName"))] =
                    Member(b, "cost") is { ValueKind: JsonValueKind.Number } c ? c.GetDouble() : throw CostError.Data($"ccusage session {id}: breakdown without cost");
        }
        return rows.Select(r => r.Model == NoModel ? r : costs.TryGetValue((r.SessionId, r.Model), out double c)
            ? r with { Cost = c }
            : throw CostError.Data($"ccusage reports no cost for claude session {r.SessionId}, model {r.Model}")).Select(r => AddListPrice(r)).ToList();
    }

    /// <summary>ccusage prices a codex session as a whole; the cost goes on the session's largest model row (the others
    /// stay 0), so the session total is right without inventing a per-model split.</summary>
    internal static List<Usage> ApplyCodexCost(List<Usage> rows, string ccusageJson)
    {
        var costs = new Dictionary<string, double>();
        foreach (JsonElement s in CcusageSessions(ccusageJson))
        {
            string file = Text(s, "sessionFile") ?? throw CostError.Data("ccusage codex session without sessionFile");
            costs[file.Length >= 36 ? file[^36..] : file] =
                Member(s, "costUSD") is { ValueKind: JsonValueKind.Number } c ? c.GetDouble() : throw CostError.Data($"ccusage codex session {file} has no costUSD");
        }
        var result = new List<Usage>();
        foreach (var session in rows.GroupBy(r => r.SessionId))
        {
            var sessionRows = session.ToList();
            if (sessionRows.All(r => r.Tokens == 0)) { result.AddRange(sessionRows); continue; }  // no usage recorded: ccusage lists no such session
            if (!costs.TryGetValue(session.Key, out double cost))
                throw CostError.Data($"ccusage reports no cost for codex session {session.Key}");
            Usage largest = sessionRows.OrderByDescending(r => r.Tokens).First();
            result.AddRange(sessionRows.Select(r => AddListPrice(r == largest ? r with { Cost = cost } : r, defaultPrice: cost == 0)));
        }
        return result;
    }

    /// <summary>What a Claude transcript has told so far: its messages by message and request id (the last record of an API
    /// response is final), and the span of its timestamps. The message table is threaded through the records of one transcript
    /// and updated in place.</summary>
    internal sealed record ClaudeScan(Dictionary<string, ClaudeMessage> Messages, DateTimeOffset? First, DateTimeOffset? Last);

    /// <summary>One API response of a Claude transcript: its model and token counts.</summary>
    internal sealed record ClaudeMessage(string Model, long In, long Out, long CR, long CW, long? Think);

    /// <summary>One record of a Claude transcript: null when it is of another folder, else the scan with the record's message and span.</summary>
    internal static ClaudeScan? ClaudeRecord(string file, Func<string, bool> inFolder, ClaudeScan scan, int line, JsonElement record)
    {
        if (Text(record, "cwd") is { } cwd && !inFolder(cwd)) return null;
        var (first, last) = WidenSpan(record, file, line, scan.First, scan.Last);
        scan = scan with { First = first, Last = last };
        if (Text(record, "type") != "assistant" || Member(record, "message", "usage") is null) return scan;
        string model = RequiredText(record, file, line, "message", "model");
        if (model == "<synthetic>") return scan;
        string key = (Text(record, "message", "id") ?? RequiredText(record, file, line, "uuid")) + "|" + Text(record, "requestId");
        scan.Messages[key] = new ClaudeMessage(model, RequiredNumber(record, file, line, "message", "usage", "input_tokens"),
            RequiredNumber(record, file, line, "message", "usage", "output_tokens"),
            Number(record, file, line, "message", "usage", "cache_read_input_tokens") ?? 0,
            Number(record, file, line, "message", "usage", "cache_creation_input_tokens") ?? 0,
            Number(record, file, line, "message", "usage", "output_tokens_details", "thinking_tokens"));
        return scan;
    }

    /// <summary>The totals per model of a list of messages.</summary>
    internal static Dictionary<string, Acc> MessageModels(IEnumerable<ClaudeMessage> messages)
    {
        var models = new Dictionary<string, Acc>();
        foreach (var m in messages) models[m.Model] = ModelAcc(models, m.Model).Add(m.In, m.Out, m.CR, m.CW, m.Think);
        return models;
    }

    /// <summary>The Claude Code session of one transcript (its lines): the usage per model, or no rows when the transcript is
    /// of another folder. One API response is streamed as several records sharing message id and request id; the last one
    /// is final. Costs are filled in by PlainPrice or ApplyClaudeCost.</summary>
    internal static List<Usage> ClaudeSession(string file, IEnumerable<string> lines, string folder, Func<string, bool> inFolder, DateTimeOffset now)
    {
        string id = Path.GetFileNameWithoutExtension(file);
        var scan = new ClaudeScan(new Dictionary<string, ClaudeMessage>(), null, null);
        foreach (var (line, record) in JsonLines(file, lines))
        {
            if (ClaudeRecord(file, inFolder, scan, line, record) is not { } next) return new List<Usage>();
            scan = next;
        }
        return SessionRows("claude", id, folder, MessageModels(scan.Messages.Values), scan.First, scan.Last, "ccusage", file, now);
    }

    /// <summary>Whether a rollout is of the folder, from the first lines of its file (at least up to the first record): its file name,
    /// its first record being session_meta and that record's cwd. The same errors as CodexSession, which reads the rest only for a
    /// rollout of the folder.</summary>
    internal static bool CodexInFolder(string file, IEnumerable<string> head, Func<string, bool> inFolder)
    {
        CodexRolloutId(file);
        foreach (var (line, record) in JsonLines(file, head))
        {
            if (Text(record, "type") != "session_meta") throw CostError.Data($"{file}:{line}: the first record is not session_meta");
            return inFolder(RequiredText(record, file, line, "payload", "cwd"));
        }
        return false;
    }

    /// <summary>The cumulative token counters of a codex rollout: input, cached input, cache write, output and reasoning output.</summary>
    internal sealed record Counters(long Input, long Cached, long Write, long Output, long Reason);

    /// <summary>What a codex rollout has told so far: the models' totals (updated in place), the span, the model of the turn, the
    /// counters of the last token_count, whether the session_meta record has been seen and the records skipped so far (file:line and
    /// why: a token_count whose cumulative counters went down is skipped, it is not counted and the run goes on).</summary>
    internal sealed record CodexScan(Dictionary<string, Acc> Models, DateTimeOffset? First, DateTimeOffset? Last, string? Model, Counters Prev, bool SeenMeta, string[] Skips);

    /// <summary>Whether a token_count's cumulative counters are below those of the last one (a record to skip).</summary>
    internal static bool CountersDown(Counters prev, long tIn, long tCached, long tWrite, long tOut, long tReason) =>
        tIn < prev.Input || tCached < prev.Cached || tWrite < prev.Write || tOut < prev.Output || tReason < prev.Reason;

    /// <summary>One record of a codex rollout: null when the rollout is of another folder, else the scan with the record's growth.</summary>
    internal static CodexScan? CodexRecord(string file, Func<string, bool> inFolder, CodexScan scan, int line, JsonElement record)
    {
        string? type = Text(record, "type");
        if (!scan.SeenMeta)
        {
            if (type != "session_meta") throw CostError.Data($"{file}:{line}: the first record is not session_meta");
            if (!inFolder(RequiredText(record, file, line, "payload", "cwd"))) return null;
        }
        var (first, last) = WidenSpan(record, file, line, scan.First, scan.Last);
        string? model = type == "turn_context" ? RequiredText(record, file, line, "payload", "model") : scan.Model;
        scan = scan with { SeenMeta = true, First = first, Last = last, Model = model };
        if (type != "event_msg" || Text(record, "payload", "type") != "token_count" || Member(record, "payload", "info") is null) return scan;
        if (model == null) throw CostError.Data($"{file}:{line}: token_count before any turn_context model");
        long tIn = RequiredNumber(record, file, line, "payload", "info", "total_token_usage", "input_tokens");
        long tCached = Number(record, file, line, "payload", "info", "total_token_usage", "cached_input_tokens") ?? 0;
        long tWrite = Number(record, file, line, "payload", "info", "total_token_usage", "cache_write_input_tokens") ?? 0;
        long tOut = RequiredNumber(record, file, line, "payload", "info", "total_token_usage", "output_tokens");
        long tReason = Number(record, file, line, "payload", "info", "total_token_usage", "reasoning_output_tokens") ?? 0;
        Counters prev = scan.Prev;
        if (CountersDown(prev, tIn, tCached, tWrite, tOut, tReason))
            return scan with { Skips = [.. scan.Skips, $"{file}:{line}: the cumulative token counters went down"] };
        // codex counts cached input inside input; ccusage and the table show the uncached part as Input.
        scan.Models[model] = ModelAcc(scan.Models, model).Add((tIn - prev.Input) - (tCached - prev.Cached), tOut - prev.Output, tCached - prev.Cached, tWrite - prev.Write, tReason - prev.Reason);
        return scan with { Prev = new Counters(tIn, tCached, tWrite, tOut, tReason) };
    }

    /// <summary>The codex session of one rollout (its lines), or no rows when it is of another folder or empty; tokens are the
    /// growth of the cumulative counter between token_count events, charged to the model of the turn they fall in. Costs
    /// are filled in by PlainPrice or ApplyCodexCost. Also the records skipped (a token_count whose counters went down), as file:line and why.</summary>
    internal static (List<Usage> Rows, List<string> Skipped) CodexSession(string file, IEnumerable<string> lines, string folder, Func<string, bool> inFolder, DateTimeOffset now)
    {
        string id = CodexRolloutId(file);
        var scan = new CodexScan(new Dictionary<string, Acc>(), null, null, null, new Counters(0, 0, 0, 0, 0), false, []);
        foreach (var (line, record) in JsonLines(file, lines))
        {
            if (CodexRecord(file, inFolder, scan, line, record) is not { } next) return (new List<Usage>(), new List<string>());
            scan = next;
        }
        // A rollout with no record at all (codex made the file and never wrote to it) holds nothing to count or to misread; one with records still needs its meta and timestamps.
        return (scan.SeenMeta ? SessionRows("codex", id, folder, scan.Models, scan.First, scan.Last, "ccusage", file, now) : new List<Usage>(), scan.Skips.ToList());
    }

    /// <summary>What a pi transcript has told so far: the models' totals (updated in place), the span and whether the session
    /// header has been seen.</summary>
    internal sealed record PiScan(Dictionary<string, Acc> Models, DateTimeOffset? First, DateTimeOffset? Last, bool SeenSession);

    /// <summary>One record of a pi transcript: null when the transcript is of another folder, else the scan with the record's message.</summary>
    internal static PiScan? PiRecord(string file, Func<string, bool> inFolder, PiScan scan, int line, JsonElement record)
    {
        if (!scan.SeenSession)
        {
            if (Text(record, "type") != "session") throw CostError.Data($"{file}:{line}: the first record is not the session header");
            if (!inFolder(RequiredText(record, file, line, "cwd"))) return null;
        }
        var (first, last) = WidenSpan(record, file, line, scan.First, scan.Last);
        scan = scan with { SeenSession = true, First = first, Last = last };
        if (Text(record, "type") != "message" || Text(record, "message", "role") != "assistant") return scan;
        if (Member(record, "message", "usage") is null) throw CostError.Data($"{file}:{line}: assistant message without usage");
        string model = RequiredText(record, file, line, "message", "model");
        Acc acc = ModelAcc(scan.Models, model).Add(RequiredNumber(record, file, line, "message", "usage", "input"), RequiredNumber(record, file, line, "message", "usage", "output"),
            Number(record, file, line, "message", "usage", "cacheRead") ?? 0, Number(record, file, line, "message", "usage", "cacheWrite") ?? 0,
            Number(record, file, line, "message", "usage", "reasoning"));
        double cost = Member(record, "message", "usage", "cost", "total") is { ValueKind: JsonValueKind.Number } c ? c.GetDouble()
            : throw CostError.Data($"{file}:{line}: usage without cost.total");
        scan.Models[model] = acc with { Cost = acc.Cost + cost };
        return scan;
    }

    /// <summary>The pi session of one transcript (its lines), or no rows when it is of another folder; pi records the cost of
    /// every message itself.</summary>
    internal static List<Usage> PiSession(string file, IEnumerable<string> lines, string folder, Func<string, bool> inFolder, DateTimeOffset now)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        string id = name[(name.LastIndexOf('_') + 1)..];
        var scan = new PiScan(new Dictionary<string, Acc>(), null, null, false);
        foreach (var (line, record) in JsonLines(file, lines))
        {
            if (PiRecord(file, inFolder, scan, line, record) is not { } next) return new List<Usage>();
            scan = next;
        }
        return SessionRows("pi", id, folder, scan.Models, scan.First, scan.Last, "pi-recorded", file, now).Select(r => AddListPrice(r)).ToList();
    }

    /// <summary>The store's usage records (its lines); every record is of the folder (<paramref name="inFolder"/>).</summary>
    internal static List<Usage> ParseStore(string path, IEnumerable<string> lines, Func<string, bool> inFolder, string folder)
    {
        var result = new List<Usage>();
        foreach (var (line, r) in JsonLines(path, lines))
        {
            string recordFolder = RequiredText(r, path, line, "folder");
            if (!inFolder(recordFolder)) throw CostError.Data($"{path}:{line}: record is for folder {recordFolder}, not {folder}");
            string harness = RequiredText(r, path, line, "harness");
            if (!Harnesses.Contains(harness)) throw CostError.Data($"{path}:{line}: unknown harness {harness}");
            result.Add(new Usage(harness, RequiredText(r, path, line, "sessionId"), RequiredText(r, path, line, "model"),
                RequiredNumber(r, path, line, "input"), RequiredNumber(r, path, line, "output"), RequiredNumber(r, path, line, "cacheRead"),
                RequiredNumber(r, path, line, "cacheWrite"), Number(r, path, line, "thinking"),
                Member(r, "costUsd") is { ValueKind: JsonValueKind.Number } c ? c.GetDouble() : throw CostError.Data($"{path}:{line}: missing number \"costUsd\""),
                RequiredText(r, path, line, "costSource"), Time(RequiredText(r, path, line, "first"), path, line),
                Time(RequiredText(r, path, line, "last"), path, line), recordFolder, Time(RequiredText(r, path, line, "snapshotUtc"), path, line)));
        }
        return result;
    }

    /// <summary>The launch ledger's entries (its lines).</summary>
    internal static List<LaunchEntry> ParseLaunches(string path, IEnumerable<string> lines)
    {
        var result = new List<LaunchEntry>();
        foreach (var (line, r) in JsonLines(path, lines))
        {
            string kind = RequiredText(r, path, line, "kind");
            if (kind != "Launcher" && kind != "Session" && kind != "Editor") throw CostError.Data($"{path}:{line}: kind must be Launcher, Session or Editor, not {kind}");
            result.Add(new LaunchEntry(Time(RequiredText(r, path, line, "startUtc"), path, line), kind, RequiredText(r, path, line, "tool"),
                RequiredText(r, path, line, "harness"), RequiredText(r, path, line, "folder"), Text(r, "skill"), Text(r, "group") ?? DefaultGroup));
        }
        return result;
    }

    private static string StoreLine(Usage u) => JsonText(UsageFields(u));

    /// <summary>The store file's text: one line per usage record, in harness, session, model order.</summary>
    internal static string StoreText(List<Usage> rows)
    {
        var text = new StringBuilder();
        foreach (Usage u in rows.OrderBy(u => u.Harness, StringComparer.Ordinal).ThenBy(u => u.SessionId, StringComparer.Ordinal).ThenBy(u => u.Model, StringComparer.Ordinal))
            text.Append(StoreLine(u)).Append('\n');
        return text.ToString();
    }

    /// <summary>Session key (harness, id) to its ledger entry: same harness and folder, the session's first message within
    /// MatchWindow after startUtc, the closest pair first, one entry per session and one session per entry.</summary>
    internal static Dictionary<(string, string), LaunchEntry> MatchLaunches(List<Row> rows, List<LaunchEntry> launches)
    {
        var sessions = rows.GroupBy(r => (r.Usage.Harness, r.Usage.SessionId))
            .Select(g => (Key: g.Key, First: g.Min(r => r.Usage.First), Folder: g.First().Usage.Folder)).ToList();
        var pairs = new List<(TimeSpan Gap, (string, string) Key, LaunchEntry LaunchEntry)>();
        foreach (var s in sessions)
            foreach (LaunchEntry l in launches)
            {
                TimeSpan gap = s.First - l.StartUtc;
                if (l.Harness == s.Key.Harness && SameFolder(l.Folder, s.Folder) && gap >= TimeSpan.Zero && gap <= MatchWindow)
                    pairs.Add((gap, s.Key, l));
            }
        var matched = new Dictionary<(string, string), LaunchEntry>();
        var used = new HashSet<LaunchEntry>();
        foreach (var p in pairs.OrderBy(p => p.Gap))
        {
            if (matched.ContainsKey(p.Key) || used.Contains(p.LaunchEntry)) continue;
            matched[p.Key] = p.LaunchEntry;
            used.Add(p.LaunchEntry);
        }
        return matched;
    }

    /// <summary>Session key (harness, id) to its session group: the group of the ledger entry the session matches, else
    /// DefaultGroup (a session started by hand, or by a launch that named no group).</summary>
    internal static Dictionary<(string, string), string> SessionGroups(List<Row> rows, List<LaunchEntry> launches)
    {
        var launched = MatchLaunches(rows, launches);
        return rows.Select(r => (r.Usage.Harness, r.Usage.SessionId)).Distinct()
            .ToDictionary(k => k, k => launched.TryGetValue(k, out LaunchEntry? l) ? l.Group : DefaultGroup);
    }

    /// <summary>The note a report ends with when transcript records were skipped; nothing when none were.</summary>
    internal static string SkippedNote(int skipped) =>
        skipped == 0 ? "" : $"\n{skipped} transcript records were skipped (a codex token_count whose cumulative counters went down); their tokens are not counted. The records are listed on stderr.\n";

    internal static string RenderGroups(List<Row> rows, List<LaunchEntry> launches, int skipped = 0)
    {
        var of = SessionGroups(rows, launches);
        var sb = new StringBuilder();
        sb.Append("| Group | Sessions | Approx cost (USD) |\n|---|---:|---:|\n");
        foreach (var g in GroupTotals(rows, of))
            sb.Append($"| {g.Name} | {g.Sessions} | {Fixed4(g.Cost)} |\n");
        return sb.Append(SkippedNote(skipped)).ToString();
    }

    internal static string RenderGroupsJson(List<Row> rows, List<LaunchEntry> launches, int skipped = 0)
    {
        var of = SessionGroups(rows, launches);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = NewLine }))
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
            if (skipped > 0) w.WriteNumber("skippedRecords", skipped);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    internal static string RenderTable(List<Row> rows, List<LaunchEntry> launches, int skipped = 0)
    {
        var launched = MatchLaunches(rows, launches);
        var groups = SessionGroups(rows, launches);
        var sb = new StringBuilder();
        sb.Append("| Session id | Harness | Model | Input | Output | Cache read | Cache write | Thinking | Approx cost (USD) | Start | End | Started by | Group | Source |\n");
        sb.Append("|---|---|---|---:|---:|---:|---:|---:|---:|---|---|---|---|---|\n");
        foreach (Row r in rows)
        {
            Usage u = r.Usage;
            sb.Append($"| {u.SessionId} | {u.Harness} | {u.Model} | {u.Input} | {u.Output} | {u.CacheRead} | {u.CacheWrite} | {u.Thinking} | ")
              .Append(Unpriced(u) ? "unpriced" : Fixed4(u.Cost))
              .Append($" | {Iso(u.First)} | {Iso(u.Last)} | {StartedBy(u, launched)} | {groups[(u.Harness, u.SessionId)]} | {(r.Live ? "live" : "snapshot only")} |\n");
        }
        var all = rows.Select(r => r.Usage).ToList();
        long? thinking = all.Any(u => u.Thinking != null) ? all.Sum(u => u.Thinking ?? 0) : null;
        sb.Append($"| **TOTAL** | | | {all.Sum(u => u.Input)} | {all.Sum(u => u.Output)} | {all.Sum(u => u.CacheRead)} | {all.Sum(u => u.CacheWrite)} | {thinking} | {Fixed4(all.Sum(u => u.Cost))} | | | | | |\n");
        var unpriced = all.Where(Unpriced).Select(u => $"{u.Harness} {u.Model}").Distinct().Order(StringComparer.Ordinal).ToList();
        if (unpriced.Count > 0)
            sb.Append($"\n{all.Count(Unpriced)} rows are unpriced: ccusage {CcusagePinned} has no price for {string.Join(", ", unpriced)}; their cost is not in TOTAL.\n");
        var defaulted = all.Where(u => u.CostSource.Contains("+default:")).Select(u => $"{u.Harness} {u.Model} (priced as {u.CostSource[(u.CostSource.IndexOf("+default:", StringComparison.Ordinal) + 9)..]})").Distinct().Order(StringComparer.Ordinal).ToList();
        if (defaulted.Count > 0)
            sb.Append($"\n{defaulted.Count} kinds of rows use a default price, so their cost may not be accurate: {string.Join(", ", defaulted)}.\n");
        return sb.Append(SkippedNote(skipped)).ToString();
    }

    internal static string RenderJson(List<Row> rows, List<LaunchEntry> launches, int skipped = 0)
    {
        var launched = MatchLaunches(rows, launches);
        var groups = SessionGroups(rows, launches);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = NewLine }))
        {
            w.WriteStartObject();
            w.WriteStartArray("rows");
            foreach (Row r in rows)
            {
                w.WriteStartObject();
                UsageFields(r.Usage)(w);
                w.WriteString("startedBy", StartedBy(r.Usage, launched));
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
            if (skipped > 0) w.WriteNumber("skippedRecords", skipped);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>What a run prints: the groups (--groups) or the rows, as JSON (--json) or a table.</summary>
    internal static string RenderReport(bool groups, bool json, List<Row> rows, List<LaunchEntry> launches, int skipped = 0) =>
        groups ? (json ? RenderGroupsJson(rows, launches, skipped) : RenderGroups(rows, launches, skipped))
            : json ? RenderJson(rows, launches, skipped) : RenderTable(rows, launches, skipped);

    /// <summary>Merges the live rows into the stored ones. Unless ccusage priced them (<paramref name="updateCost"/>), a live row
    /// whose tokens did not change since the store keeps the store's cost.</summary>
    internal static List<Row> MergeLive(List<Usage> live, List<Usage> stored, bool updateCost) =>
        Merge(updateCost ? live : live.Select(l => KeepStoredCost(l, stored)).ToList(), stored);

    // ---- the effects: files, ccusage, the lock, the console -----------------------------------------------------------

    /// <summary>The test "this record's folder is <paramref name="folder"/> or under it": both sides normalized (NormalizeFolder), so a
    /// transcript's path spelled with the other separator, or with a trailing one, still launched (UnderFolder).</summary>
    private static Func<string, bool> InFolder(string folder)
    {
        string wanted = NormalizeFolder(folder);
        return cwd => UnderFolder(NormalizeFolder(cwd), wanted);
    }

    /// <summary>One attempt per unit of fuel to take the lock file.</summary>
    internal static (bool Acquired, int OwnerPid) AcquireLockWith(string path, int pid, int fuel)
    {
        for (; fuel > 0; fuel--)
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
                    throw CostError.Data($"{path}: the lock does not hold a pid (\"{text}\")");
                if (PidAlive(owner)) return (false, owner);
                File.Delete(path);
            }
        }
        throw CostError.Io($"{path}: the stale lock could not be replaced");
    }

    /// <summary>Takes the lock file (the pid inside). A lock whose pid is not alive is stale and replaced; a live one is
    /// left alone and its pid returned.</summary>
    internal static (bool Acquired, int OwnerPid) AcquireLock(string path, int pid)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return AcquireLockWith(path, pid, 2);
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

    /// <summary>Sleeps a quarter of a second at a time, as many times as the fuel, and not once more after a cancellation.</summary>
    internal static void WaitFor(Func<bool> isCancelled, int fuel)
    {
        for (; fuel > 0; fuel--)
        {
            if (isCancelled()) return;
            Thread.Sleep(250);
        }
    }

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
            File.AppendAllText(log, LogLine(DateTimeOffset.UtcNow, $"watch {harness} started pid={pid} interval={interval} idle={idle}"));
            while (true)
            {
                bool cancelled = cancel.IsCancellationRequested;
                DateTimeOffset snapshotAt = DateTimeOffset.UtcNow;
                var rows = snapshot().Where(r => r.Usage.Harness == harness).ToList();
                File.AppendAllText(log, LogLine(DateTimeOffset.UtcNow, $"snapshot {harness}{(cancelled ? " final" : "")} sessions={rows.Select(r => r.Usage.SessionId).Distinct().Count()} totalCostUsd={Fixed4(rows.Sum(r => r.Usage.Cost))}"));
                if (cancelled) break;
                DateTimeOffset change = latestChange(rows) ?? started;
                if (change < started) change = started;
                if (change <= snapshotAt && DateTimeOffset.UtcNow - change >= idle)
                {
                    File.AppendAllText(log, LogLine(DateTimeOffset.UtcNow, $"watch {harness} ended: idle"));
                    break;
                }
                WaitFor(() => cancel.IsCancellationRequested, (int)((interval.Ticks / 10000 + 249) / 250));
            }
            return 0;
        }
        catch (Exception e)
        {
            File.AppendAllText(log, LogLine(DateTimeOffset.UtcNow, $"ERROR {harness}: {e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}"));
            Console.Error.WriteLine("runJointCostAST: " + e.Message);
            return 1;
        }
        finally
        {
            ReleaseLock(lockPath, pid);
        }
    }

    /// <summary>bunx where it is installed and runs, otherwise npx, which every machine with Node has.</summary>
    private static bool BunxAvailable()
    {
        if (!OperatingSystem.IsWindows() && !OnPath("bunx")) return false;
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
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw CostError.Operation($"cannot run bunx, which is on the PATH: {e.Message}");
        }
    }

    private static (string Command, string[] Prefix) Runner() =>
        BunxAvailable() ? ("bunx", Array.Empty<string>()) : ("npx", new[] { "-y" });

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
        foreach (string a in CcusageArgs(tool)) start.ArgumentList.Add(a);
        return CcusageJson(tool, RunProcess(start, $"ccusage {tool} session"));
    }

    /// <summary>The rows of the transcripts, in file-name order: each file is read whole and turned into its session's rows.</summary>
    internal static List<Usage> ReadSessions(IEnumerable<string> files, Func<string, IEnumerable<string>, List<Usage>> session) =>
        files.SelectMany(file => session(file, ReadLines(file))).ToList();

    internal static List<Usage> ReadClaude(string projectsRoot, string folder, DateTimeOffset now)
    {
        string dir = ClaudeDir(projectsRoot, folder);
        if (!Directory.Exists(dir)) return new List<Usage>();
        Func<string, bool> inFolder = InFolder(folder);
        return ReadSessions(Directory.GetFiles(dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal), (file, lines) => ClaudeSession(file, lines, folder, inFolder, now));
    }

    internal static (List<Usage> Rows, List<string> Skipped) ReadCodex(string sessionsRoot, string folder, DateTimeOffset now)
    {
        var result = new List<Usage>();
        var skipped = new List<string>();
        if (!Directory.Exists(sessionsRoot)) return (result, skipped);
        Func<string, bool> inFolder = InFolder(folder);
        foreach (string file in Directory.EnumerateFiles(sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (!CodexInFolder(file, ReadHeadLines(file), inFolder)) continue;
            var (rows, skips) = CodexSession(file, ReadLines(file), folder, inFolder, now);
            result.AddRange(rows);
            skipped.AddRange(skips);
        }
        return (result, skipped);
    }

    internal static List<Usage> ReadPi(string sessionsRoot, string folder, DateTimeOffset now)
    {
        string dir = PiDir(sessionsRoot, folder);
        if (!Directory.Exists(dir)) return new List<Usage>();
        Func<string, bool> inFolder = InFolder(folder);
        return ReadSessions(Directory.GetFiles(dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal), (file, lines) => PiSession(file, lines, folder, inFolder, now));
    }

    /// <summary>The rows of one harness, priced: from their tokens with the built-in table, or with the pinned ccusage's numbers
    /// (<paramref name="updateCost"/>); none when there are none.</summary>
    internal static List<Usage> PriceLive(List<Usage> rows, bool updateCost, string tool, Func<List<Usage>, string, List<Usage>> applyCost)
    {
        if (rows.Count == 0) return new List<Usage>();
        return updateCost ? applyCost(rows, Ccusage(tool)) : rows.Select(r => PlainPrice(r)).ToList();
    }

    /// <summary>The live rows of the harnesses' transcripts, priced: Claude and codex from their tokens with the built-in table
    /// (<see cref="ListPrices"/>), or with the pinned ccusage's numbers (<paramref name="updateCost"/>); pi's own cost.</summary>
    private static (List<Usage> Rows, List<string> Skipped) ReadLive(string folder, List<string> harnesses, string home, DateTimeOffset now, bool updateCost)
    {
        var live = new List<Usage>();
        var skipped = new List<string>();
        if (harnesses.Contains("claude"))
            live.AddRange(PriceLive(ReadClaude(Path.Combine(home, ".claude", "projects"), folder, now), updateCost, "claude", ApplyClaudeCost));
        if (harnesses.Contains("codex"))
        {
            var (codex, skips) = ReadCodex(Path.Combine(home, ".codex", "sessions"), folder, now);
            live.AddRange(PriceLive(codex, updateCost, "codex", ApplyCodexCost));
            skipped.AddRange(skips);
        }
        if (harnesses.Contains("pi")) live.AddRange(ReadPi(Path.Combine(home, ".pi", "agent", "sessions"), folder, now));
        return (live, skipped);
    }

    internal static List<Usage> ReadStore(string path, string folder) =>
        File.Exists(path) ? ParseStore(path, ReadLines(path), InFolder(folder), folder) : new List<Usage>();

    internal static List<LaunchEntry> ReadLaunches(string path) =>
        File.Exists(path) ? ParseLaunches(path, ReadLines(path)).Select(l => l with { Folder = NormalizeFolder(l.Folder) }).ToList() : new List<LaunchEntry>();

    /// <summary>Writes a temp file beside the store, then renames it over it: the store is never half written.</summary>
    internal static void WriteStore(string path, List<Usage> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, StoreText(rows), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }

    /// <summary>Reads the live transcripts of the harnesses, merges them into the folder's usage.jsonl and (unless read-only)
    /// writes it back. Returns every merged row, of all harnesses, and the transcript records skipped (file:line and why).</summary>
    internal static (List<Row> Rows, List<string> Skipped) CollectAndMerge(string folder, List<string> harnesses, string home, bool snapshot, DateTimeOffset now, bool updateCost = false)
    {
        var (live, skipped) = ReadLive(folder, harnesses, home, now, updateCost);
        string usage = Path.Combine(folder, StoreFolder, UsageFile);
        List<Row> merged = MergeLive(live, ReadStore(usage, folder), updateCost);
        if (snapshot && merged.Count > 0) WriteStore(usage, merged.Select(r => r.Usage).ToList());
        return (merged, skipped);
    }

    /// <summary>The skipped records on stderr, one line each (file:line and why).</summary>
    private static void ReportSkipped(List<string> skipped)
    {
        foreach (string s in skipped) Console.Error.WriteLine("runJointCostAST: skipped " + s);
    }

    /// <summary>What a run does once its command line is read: watch one harness, or print the report of the folder's usage.</summary>
    internal abstract record Command;

    internal sealed record WatchCommand(string Harness, TimeSpan Interval, TimeSpan Idle) : Command;

    internal sealed record ReportCommand(CostArgs A) : Command;

    /// <summary>Minutes as ticks of 100 ns.</summary>
    internal static long Ticks(double minutes) => (long)Math.Round(minutes * 600000000.0, MidpointRounding.AwayFromZero);

    /// <summary>The command a command line asks for; the refusal when its options are not legal together.</summary>
    internal static Command Decide(CostArgs a)
    {
        CheckWatch(a);
        return a.Watch && a.Harnesses.Count == 1
            ? new WatchCommand(a.Harnesses[0], new TimeSpan(Ticks(a.IntervalMinutes)), new TimeSpan(Ticks(a.IdleMinutes)))
            : new ReportCommand(a);
    }

    private static int Run(string[] args, TextWriter output)
    {
        CostArgs a = ParseArgs(args, Directory.GetCurrentDirectory());
        string folder = NormalizeFolder(a.Folder);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Decide(a) is WatchCommand w)
        {
            using var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
            using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cancel.Cancel(); });
            return Watch(folder, w.Harness, w.Interval, w.Idle,
                () => { var (rows, skipped) = CollectAndMerge(folder, a.Harnesses, home, true, DateTimeOffset.UtcNow); ReportSkipped(skipped); return rows; }, rows => LatestChange(home, folder, w.Harness, rows), cancel.Token);
        }
        var (collected, skipped) = CollectAndMerge(folder, a.Harnesses, home, a.Snapshot, DateTimeOffset.UtcNow, a.UpdateCost);
        var merged = collected.Where(r => a.Harnesses.Contains(r.Usage.Harness)).ToList();
        var launches = ReadLaunches(Path.Combine(folder, StoreFolder, LaunchesFile));
        ReportSkipped(skipped);
        output.Write(RenderReport(a.Groups, a.Json, merged, launches, skipped.Count));
        return 0;
    }

    /// <summary>The command line of the reader: the script's Main.</summary>
    internal static int Launch(string[] args)
    {
        try
        {
            return Run(args, Console.Out);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine("runJointCostAST: " + e.Message);
            return 1;
        }
    }

    static int Main(string[] args) => Launch(args);
}
