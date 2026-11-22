// Collects objective health signals of a git repository: where the code changes, where it is fixed, which files
// change together, where the tests wait or are skipped, how large the documents are.
//
//   dotnet run signals.cs -- --out <folder> [--repo <folder>] [--since "12 months ago"] [--scope <folder>] [--exclude <glob>]...
//
// Reads the repository and writes two files in --out:
//   signals.json  everything, for analysts and for the report
//   signals.md    the top of each list, for a first read
//
// The numbers are leads, not findings: a file is not unhealthy because it changes often.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

const char Sep = (char)0x1E; // starts a commit record in the git log output
const RegexOptions Insensitive = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
// A fix is recognized by the subject of its commit: a proxy that depends on the conventions of the project (--fix-pattern).
var fixPattern = @"\b(fix(es|ed)?|bug(fix)?|regress\w*|revert\w*|hotfix|crash\w*|broken)\b";
var flaky = new Regex(@"flak|intermittent|unstable|deflak|quarantin|race condition|time[- ]?out|sporadic", Insensitive);
var testPath = new Regex(@"(^|[/\\._-])(tests?|specs?|e2e|__tests__|testing)([/\\._-]|$)", Insensitive);
string[] docExtensions = [".md", ".mdx", ".rst", ".adoc", ".txt"];
string[] defaultExclude =
[
    ".alta/*", "*.lock", "*-lock.json", "*.lock.json", "pnpm-lock.yaml", "*.min.*", "*.map", "*.snap", "*.svg", "*.designer.cs",
    "*.g.cs", "*.generated.*", "*.pb.go", "*_pb2.py", "node_modules/*", "dist/*", "vendor/*",
    "third_party/*", "*/node_modules/*", "*/dist/*", "*/vendor/*", "*/third_party/*",
];
// Leads only: each match is read in its context before it becomes a finding.
var markers = new (string Name, bool TestOnly, Regex Pattern)[]
{
    ("sleep_in_test", true, new Regex(
        @"Thread\.Sleep|Task\.Delay|time\.sleep|waitForTimeout|\bsleep\s*\(|\busleep\s*\(|setTimeout\s*\(|"
        + @"\.pause\s*\(|cy\.wait\s*\(\s*\d|Timer\.sleep|tokio::time::sleep|std::thread::sleep")),
    ("skipped_test", true, new Regex(
        @"\[Ignore|\bSkip\s*=|\.skip\s*\(|\bxit\s*\(|\bxdescribe\s*\(|@Disabled|@Ignore|pytest\.mark\.skip|"
        + @"\bt\.Skip\s*\(|#\[ignore\]|\.todo\s*\(")),
    // Retries of the test runner, not of the product under test.
    ("retry_in_test", true, new Regex(@"\bretries\s*[:=(]|\[Retry|RetryFact|RetryTheory|@Retry|--retr(y|ies)\b|retryTimes|\bflaky\b|\brerun", Insensitive)),
    ("todo", false, new Regex(@"\b(TODO|FIXME|HACK|XXX)\b")),
    ("workaround", false, new Regex(@"\bwork[- ]?around\b|\btemporar(y|ily)\b|\bfor now\b|\bkludge\b", Insensitive)),
};

// ---- options
string repoArg = ".", since = "12 months ago";
string? outDir = null, scope = null;
var exclude = new List<string>();
bool useDefaultExclude = true;
int top = 40, areaDepth = 2, maxCommitFiles = 30, minCoupling = 5;
for (var i = 0; i < args.Length; i++)
{
    string Value() => i + 1 < args.Length ? args[++i] : Fail($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--repo": repoArg = Value(); break;
        case "--out": outDir = Value(); break;
        case "--since": since = Value(); break;
        case "--scope": scope = Value(); break;
        case "--exclude": exclude.Add(Value()); break;
        case "--fix-pattern": fixPattern = Value(); break;
        case "--no-default-exclude": useDefaultExclude = false; break;
        case "--top": top = int.Parse(Value(), CultureInfo.InvariantCulture); break;
        case "--area-depth": areaDepth = int.Parse(Value(), CultureInfo.InvariantCulture); break;
        case "--max-commit-files": maxCommitFiles = int.Parse(Value(), CultureInfo.InvariantCulture); break;
        case "--min-coupling": minCoupling = int.Parse(Value(), CultureInfo.InvariantCulture); break;
        case "-h" or "--help":
            Console.WriteLine("""
                Collects objective health signals of a git repository into signals.json and signals.md.

                  dotnet run signals.cs -- --out <folder> [options]

                  --out <folder>          folder that receives signals.json and signals.md (required)
                  --repo <folder>         repository folder (default: current folder)
                  --since <when>          history window (default: "12 months ago")
                  --scope <folder>        keep to this folder of the repository
                  --exclude <glob>        glob to leave out (repeatable), added to the defaults
                  --fix-pattern <regex>   what the subject of a fix commit matches, without case; "off" counts no fix
                  --no-default-exclude    do not leave out lock, generated, vendored and agent work files
                  --top <n>               rows per list (default: 40)
                  --area-depth <n>        folder depth of an area (default: 2)
                  --max-commit-files <n>  commits that touch more files are left out of the change coupling (default: 30)
                  --min-coupling <n>      least number of shared commits of a pair (default: 5)
                """);
            return 0;
        default: Fail($"unknown option {args[i]}"); break;
    }
}
if (outDir is null)
{
    Fail("--out is required");
}

var fix = new Regex(fixPattern == "off" ? "(?!)" : fixPattern, Insensitive);
var repo = Path.GetFullPath(repoArg);
var patterns = exclude.Concat(useDefaultExclude ? defaultExclude : []).ToArray();
var globs = patterns.Select(GlobToRegex).ToArray();
var listed = Git("ls-files", "--", scope ?? ".").Split('\n', StringSplitOptions.RemoveEmptyEntries);
var tracked = listed.Where(p => !Excluded(p)).ToArray();
var trackedSet = tracked.ToHashSet(StringComparer.Ordinal);
// Sizes, words and markers are read from the working folder: uncommitted changes are in them, and are counted here.
var uncommitted = Git("status", "--porcelain", "--", scope ?? ".").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

// ---- the files as they are today
var loc = new Dictionary<string, int>(StringComparer.Ordinal);
var markerHits = markers.ToDictionary(m => m.Name, _ => new Dictionary<string, int>(StringComparer.Ordinal));
var docs = new List<Doc>();
foreach (var path in tracked)
{
    if (ReadText(Path.Combine(repo, path)) is not var (text, lines))
    {
        continue;
    }

    loc[path] = lines;
    if (IsDoc(path))
    {
        docs.Add(new Doc(path, CountWords(text), lines));
        continue;
    }

    var isTest = testPath.IsMatch(path);
    foreach (var (name, testOnly, pattern) in markers)
    {
        if (testOnly && !isTest)
        {
            continue;
        }

        var hits = pattern.Count(text);
        if (hits > 0)
        {
            markerHits[name][path] = hits;
        }
    }
}

// ---- the history
var commits = ReadHistory();
var stats = new Dictionary<string, FileStat>(StringComparer.Ordinal);
var pairs = new Dictionary<(string A, string B), int>();
var areaCommits = new Dictionary<string, int>(StringComparer.Ordinal);
var areaFixes = new Dictionary<string, int>(StringComparer.Ordinal);
var areaFixesWithoutTest = new Dictionary<string, int>(StringComparer.Ordinal);
var flakyCommits = new List<JsonObject>();
int counted = 0, skippedLarge = 0, fixCommits = 0, fixCommitsWithoutTest = 0;
foreach (var commit in commits)
{
    var live = commit.Files.Select(f => f.Path).Where(trackedSet.Contains).Distinct().Order(StringComparer.Ordinal).ToArray();
    if (live.Length == 0)
    {
        continue; // nothing of the files reviewed: another folder, or files that are gone
    }

    counted++;
    var isFix = fix.IsMatch(commit.Subject);
    fixCommits += isFix ? 1 : 0;
    foreach (var area in live.Select(AreaOf).Distinct()) // a commit counts once per area
    {
        areaCommits[area] = areaCommits.GetValueOrDefault(area) + 1;
        areaFixes[area] = areaFixes.GetValueOrDefault(area) + (isFix ? 1 : 0);
    }

    // A fix of code that touched no test: the defect can come back unseen. A lead: the test may be in another commit.
    var tests = live.Where(p => testPath.IsMatch(p)).ToArray();
    var code = live.Where(p => !testPath.IsMatch(p) && !IsDoc(p)).ToArray();
    if (isFix && code.Length > 0 && !commit.Files.Any(f => testPath.IsMatch(f.Path)))
    {
        fixCommitsWithoutTest++;
        foreach (var area in code.Select(AreaOf).Distinct())
        {
            areaFixesWithoutTest[area] = areaFixesWithoutTest.GetValueOrDefault(area) + 1;
        }
    }

    foreach (var (path, added, deleted) in commit.Files)
    {
        if (!trackedSet.Contains(path))
        {
            continue;
        }

        if (!stats.TryGetValue(path, out var s))
        {
            stats[path] = s = new FileStat();
        }

        s.Commits++;
        s.Added += added;
        s.Deleted += deleted;
        s.Fixes += isFix ? 1 : 0;
        s.Last = string.CompareOrdinal(commit.Date, s.Last) > 0 ? commit.Date : s.Last;
    }

    if (tests.Length > 0 && flaky.IsMatch(commit.Subject))
    {
        flakyCommits.Add(new JsonObject
        {
            ["sha"] = commit.Sha, ["date"] = commit.Date, ["subject"] = commit.Subject,
            ["tests"] = new JsonArray(tests.Take(8).Select(p => (JsonNode)p).ToArray()),
        });
    }

    if (commit.Files.Count > maxCommitFiles)
    {
        skippedLarge++; // a sweep: its files do not depend on each other for having changed together
    }
    else
    {
        foreach (var path in live)
        {
            stats[path].Eligible++;
        }

        for (var x = 0; x < live.Length; x++)
        {
            for (var y = x + 1; y < live.Length; y++)
            {
                var key = (live[x], live[y]);
                pairs[key] = pairs.GetValueOrDefault(key) + 1;
            }
        }
    }
}

// Hotspot: changed often and large (size stands for complexity). The logarithm keeps one huge file from hiding the rest.
var changed = stats.Where(kv => loc.ContainsKey(kv.Key) && !IsDoc(kv.Key))
    .Select(kv => new Changed(kv.Key, kv.Value, loc[kv.Key], Math.Round(kv.Value.Commits * Math.Log2(1 + loc[kv.Key]), 1)))
    .ToArray();
var hotspots = changed.OrderByDescending(r => r.Score).ThenBy(r => r.Path, StringComparer.Ordinal).Take(top);
var fixDense = changed.Where(r => r.Stat.Fixes >= 3).OrderByDescending(r => r.Stat.Fixes).ThenByDescending(r => r.Stat.Commits)
    .ThenBy(r => r.Path, StringComparer.Ordinal).Take(top);
var largest = loc.Where(kv => !IsDoc(kv.Key)).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(top);

var coupling = pairs.Where(kv => kv.Value >= minCoupling).Select(kv =>
    {
        var (a, b) = kv.Key;
        int ca = stats[a].Eligible, cb = stats[b].Eligible; // the same commits as the ones the pair was counted in
        return new Coupling(a, b, kv.Value, Math.Round(kv.Value / ((ca + cb) / 2.0), 2), Math.Round(kv.Value / (double)Math.Min(ca, cb), 2),
            AreaOf(a) != AreaOf(b), testPath.IsMatch(a) != testPath.IsMatch(b));
    })
    .OrderByDescending(c => c.Degree).ThenByDescending(c => c.Shared).ThenBy(c => c.A, StringComparer.Ordinal).ThenBy(c => c.B, StringComparer.Ordinal)
    .ToArray();

var areas = loc.GroupBy(kv => AreaOf(kv.Key), StringComparer.Ordinal).Select(g => new
    {
        Area = g.Key,
        Files = g.Count(),
        Loc = g.Sum(kv => kv.Value),
        Commits = areaCommits.GetValueOrDefault(g.Key),
        Churn = g.Sum(kv => stats.TryGetValue(kv.Key, out var s) ? s.Added + s.Deleted : 0),
        Fixes = areaFixes.GetValueOrDefault(g.Key),
        FixesWithoutTest = areaFixesWithoutTest.GetValueOrDefault(g.Key),
    })
    .OrderByDescending(a => a.Commits).ThenBy(a => a.Area, StringComparer.Ordinal).Take(top);

var docRows = docs.Select(d => new { d.Path, d.Words, d.Lines, Commits = stats.TryGetValue(d.Path, out var s) ? s.Commits : 0, Last = stats.TryGetValue(d.Path, out var t) ? t.Last : "" })
    .OrderByDescending(d => d.Words).ThenBy(d => d.Path, StringComparer.Ordinal).ToArray();

var totals = new JsonObject
{
    ["files"] = loc.Count, ["filesExcluded"] = listed.Length - tracked.Length, ["uncommitted"] = uncommitted, ["loc"] = loc.Values.Sum(), ["commits"] = counted,
    ["fixCommits"] = fixCommits, ["fixCommitsWithoutTest"] = fixCommitsWithoutTest, ["filesChanged"] = changed.Length, ["commitsLeftOutOfCoupling"] = skippedLarge,
    ["docWords"] = docs.Sum(d => d.Words),
};
var output = new JsonObject
{
    ["generated"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture),
    ["repo"] = Path.GetFileName(repo), ["head"] = Git("rev-parse", "--short", "HEAD").Trim(),
    ["since"] = since, ["scope"] = scope, ["excluded"] = new JsonArray(patterns.Select(p => (JsonNode)p).ToArray()),
    ["settings"] = new JsonObject { ["fixPattern"] = fixPattern, ["areaDepth"] = areaDepth, ["maxCommitFiles"] = maxCommitFiles, ["minCoupling"] = minCoupling },
    ["totals"] = totals,
    ["areas"] = Array(areas, a => new JsonObject { ["area"] = a.Area, ["files"] = a.Files, ["loc"] = a.Loc, ["commits"] = a.Commits, ["churn"] = a.Churn, ["fixes"] = a.Fixes, ["fixesWithoutTest"] = a.FixesWithoutTest }),
    ["hotspots"] = Array(hotspots, ChangedRow),
    ["fixDense"] = Array(fixDense, ChangedRow),
    ["largest"] = Array(largest, kv => new JsonObject { ["path"] = kv.Key, ["loc"] = kv.Value, ["commits"] = stats.TryGetValue(kv.Key, out var s) ? s.Commits : 0 }),
    ["coupling"] = Array(coupling.Take(top), CouplingRow),
    ["crossAreaCoupling"] = Array(coupling.Where(c => c.CrossArea && !c.CodeAndTest).Take(top), CouplingRow),
    ["flakyCommits"] = new JsonArray(flakyCommits.Take(top * 2).ToArray<JsonNode>()),
    ["markers"] = new JsonObject(markerHits.Select(m => KeyValuePair.Create<string, JsonNode?>(m.Key,
        Array(m.Value.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(top), kv => new JsonObject { ["path"] = kv.Key, ["count"] = kv.Value })))),
    ["markerTotals"] = new JsonObject(markerHits.Select(m => KeyValuePair.Create<string, JsonNode?>(m.Key, m.Value.Values.Sum()))),
    ["docs"] = Array(docRows.Take(top), d => new JsonObject { ["path"] = d.Path, ["words"] = d.Words, ["lines"] = d.Lines, ["commits"] = d.Commits, ["last"] = d.Last }),
    ["docsChanged"] = Array(docRows.Where(d => d.Commits >= 3).OrderByDescending(d => d.Commits).Take(top),
        d => new JsonObject { ["path"] = d.Path, ["words"] = d.Words, ["lines"] = d.Lines, ["commits"] = d.Commits, ["last"] = d.Last }),
};

Directory.CreateDirectory(outDir!);
File.WriteAllText(Path.Combine(outDir!, "signals.json"), output.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
File.WriteAllText(Path.Combine(outDir!, "signals.md"), Render(output, Math.Min(top, 20)));
var hint = counted is >= 300 and <= 3000 ? "" : " (a window of about 500 to 2,000 commits reads best: adjust --since)";
Console.WriteLine($"{loc.Count} files, {counted} commits since {since}{hint} -> {outDir}");
return 0;

// ---- helpers
string Fail(string message)
{
    Console.Error.WriteLine(message);
    Environment.Exit(1);
    return "";
}

string Git(params string[] gitArgs)
{
    // quotepath off: paths with non-ASCII characters are written as they are, not escaped.
    var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
    foreach (var a in new[] { "-C", repo, "-c", "core.quotepath=false" }.Concat(gitArgs))
    {
        info.ArgumentList.Add(a);
    }

    using var process = Process.Start(info) ?? throw new InvalidOperationException("git did not start");
    var error = process.StandardError.ReadToEndAsync();
    var text = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return process.ExitCode == 0 ? text : Fail($"git {string.Join(' ', gitArgs.Take(2))} failed: {error.Result.Trim()}");
}

static Regex GlobToRegex(string glob) =>
    new("^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

bool Excluded(string path)
{
    var name = path[(path.LastIndexOf('/') + 1)..];
    return globs.Any(g => g.IsMatch(path) || g.IsMatch(name));
}

bool IsDoc(string path) => docExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

string AreaOf(string path)
{
    var parts = path.Split('/');
    return parts.Length > areaDepth ? string.Join('/', parts.Take(areaDepth)) : parts.Length > 1 ? string.Join('/', parts.Take(parts.Length - 1)) : ".";
}

// The text and the number of lines of a text file; null for a binary, a very large or an unreadable file.
static (string Text, int Lines)? ReadText(string full)
{
    byte[] data;
    try
    {
        if (new FileInfo(full) is not { Exists: true, Length: <= 4_000_000 })
        {
            return null;
        }

        data = File.ReadAllBytes(full);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        return null;
    }

    if (data.AsSpan(0, Math.Min(data.Length, 8192)).Contains((byte)0))
    {
        return null;
    }

    var lines = data.AsSpan().Count((byte)'\n') + (data.Length > 0 && data[^1] != (byte)'\n' ? 1 : 0);
    return (Encoding.UTF8.GetString(data), lines);
}

static int CountWords(string text)
{
    var words = 0;
    var inWord = false;
    foreach (var c in text)
    {
        var space = char.IsWhiteSpace(c);
        words += !space && !inWord ? 1 : 0;
        inWord = !space;
    }

    return words;
}

// `a/{old => new}/b` or `old => new` of numstat: the old and the new path, or null.
static (string Old, string New)? RenameTarget(string path)
{
    if (!path.Contains(" => ", StringComparison.Ordinal))
    {
        return null;
    }

    var m = Regex.Match(path, @"^(.*)\{(.*) => (.*)\}(.*)$");
    if (m.Success)
    {
        return ((m.Groups[1].Value + m.Groups[2].Value + m.Groups[4].Value).Replace("//", "/"),
            (m.Groups[1].Value + m.Groups[3].Value + m.Groups[4].Value).Replace("//", "/"));
    }

    var parts = path.Split(" => ", 2);
    return (parts[0], parts[1]);
}

// The commits of the window, newest first, with the paths as the files are named today.
List<Commit> ReadHistory()
{
    var gitArgs = new List<string> { "log", $"--since={since}", "--no-merges", "-M", "--numstat", "--date=short", $"--format={Sep}%h\t%ad\t%s" };
    var alias = new Dictionary<string, string>(StringComparer.Ordinal);
    var result = new List<Commit>();
    foreach (var record in Git([.. gitArgs]).Split(Sep).Skip(1))
    {
        var lines = record.Split('\n');
        var head = lines[0].Split('\t', 3);
        if (head.Length < 3)
        {
            continue;
        }

        var files = new List<(string Path, int Added, int Deleted)>();
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split('\t');
            if (parts.Length != 3)
            {
                continue;
            }

            var path = parts[2];
            if (RenameTarget(path) is var (old, renamed))
            {
                path = alias.GetValueOrDefault(renamed, renamed);
                alias[old] = path; // older commits name the file by its old path
            }
            else
            {
                path = alias.GetValueOrDefault(path, path);
            }

            files.Add((path, parts[0] == "-" ? 0 : int.Parse(parts[0], CultureInfo.InvariantCulture), parts[1] == "-" ? 0 : int.Parse(parts[1], CultureInfo.InvariantCulture)));
        }

        result.Add(new Commit(head[0], head[1], head[2], files));
    }

    return result;
}

static JsonArray Array<T>(IEnumerable<T> items, Func<T, JsonObject> row) => new(items.Select(i => (JsonNode)row(i)).ToArray());

JsonObject ChangedRow(Changed r) => new()
{
    ["path"] = r.Path, ["commits"] = r.Stat.Commits, ["churn"] = r.Stat.Added + r.Stat.Deleted, ["fixes"] = r.Stat.Fixes,
    ["loc"] = r.Loc, ["last"] = r.Stat.Last, ["test"] = testPath.IsMatch(r.Path), ["score"] = r.Score,
};

static JsonObject CouplingRow(Coupling c) => new()
{
    ["a"] = c.A, ["b"] = c.B, ["shared"] = c.Shared, ["degree"] = c.Degree, ["ofSmaller"] = c.OfSmaller,
    ["crossArea"] = c.CrossArea, ["codeAndTest"] = c.CodeAndTest,
};

static string Table(string[] headers, IEnumerable<string[]> rows)
{
    var list = rows.ToList();
    if (list.Count == 0)
    {
        return "_none_\n";
    }

    var text = new StringBuilder();
    text.Append("| ").AppendJoin(" | ", headers).Append(" |\n| ").AppendJoin(" | ", headers.Select(_ => "---")).Append(" |\n");
    foreach (var row in list)
    {
        text.Append("| ").AppendJoin(" | ", row).Append(" |\n");
    }

    return text.ToString();
}

static string Render(JsonObject o, int n)
{
    static string S(JsonNode? node) => node?.ToString() ?? "";
    static string Code(JsonNode? node) => $"`{node}`";
    static string Percent(JsonNode? node) => Math.Round(node!.GetValue<double>() * 100).ToString(CultureInfo.InvariantCulture) + "%";
    IEnumerable<JsonObject> Rows(string key) => o[key]!.AsArray().Take(n).Select(x => x!.AsObject());

    var t = o["totals"]!;
    var m = o["markerTotals"]!;
    var text = new StringBuilder();
    text.Append($"# Signals of {o["repo"]} at {o["head"]}\n\n");
    text.Append($"History since {o["since"]}" + (o["scope"] is null ? "" : $", scope `{o["scope"]}`") + ". Leads to read in the code, not findings. Full lists: `signals.json`.\n\n");
    text.Append($"- {t["files"]} text files, {t["loc"]} lines ({t["filesExcluded"]} files left out as generated, vendored or agent work files); "
        + $"{t["commits"]} commits, {t["fixCommits"]} of them fixes (by their subject), {t["fixCommitsWithoutTest"]} of those without a change to a test; {t["filesChanged"]} files changed.\n");
    if (t["uncommitted"]!.GetValue<int>() > 0)
    {
        text.Append($"- Uncommitted changes in {t["uncommitted"]} paths: sizes, words and markers are those of the working folder, not of {o["head"]}.\n");
    }

    text.Append($"- Tests: {m["sleep_in_test"]} sleep-like calls, {m["skipped_test"]} skips, {m["retry_in_test"]} mentions of retry or flaky.\n");
    text.Append($"- Code: {m["todo"]} TODO/FIXME/HACK, {m["workaround"]} mentions of a workaround. Docs: {t["docWords"]} words.\n\n");

    text.Append("## Areas by activity\n\n");
    text.Append(Table(["area", "files", "lines", "commits", "churn", "fixes", "fixes without a test"],
        Rows("areas").Select(r => new[] { Code(r["area"]), S(r["files"]), S(r["loc"]), S(r["commits"]), S(r["churn"]), S(r["fixes"]), S(r["fixesWithoutTest"]) })));
    text.Append("\n## Hotspots: changed often and large\n\n");
    text.Append(Table(["file", "commits", "lines", "fixes", "last change"],
        Rows("hotspots").Select(r => new[] { Code(r["path"]), S(r["commits"]), S(r["loc"]), S(r["fixes"]), S(r["last"]) })));
    text.Append("\n## Fixed most often\n\n");
    text.Append(Table(["file", "fixes", "commits", "lines"],
        Rows("fixDense").Select(r => new[] { Code(r["path"]), S(r["fixes"]), S(r["commits"]), S(r["loc"]) })));
    text.Append("\n## Files of different areas that change together\n\n");
    text.Append("Shared commits, their share of the commits of the two files, and of the file that changes less; "
        + $"commits of more than {o["settings"]!["maxCommitFiles"]} files are left out of both. "
        + "A high share across areas is a dependency the structure does not show.\n\n");
    text.Append(Table(["file", "file", "shared", "of both", "of the smaller"],
        Rows("crossAreaCoupling").Select(c => new[] { Code(c["a"]), Code(c["b"]), S(c["shared"]), Percent(c["degree"]), Percent(c["ofSmaller"]) })));
    text.Append("\n## Largest files\n\n");
    text.Append(Table(["file", "lines", "commits"], Rows("largest").Select(r => new[] { Code(r["path"]), S(r["loc"]), S(r["commits"]) })));

    // One row per test file, the three kinds of marker side by side.
    var byPath = new Dictionary<string, int[]>(StringComparer.Ordinal);
    string[] kinds = ["sleep_in_test", "skipped_test", "retry_in_test"];
    for (var k = 0; k < kinds.Length; k++)
    {
        foreach (var r in o["markers"]![kinds[k]]!.AsArray())
        {
            var path = S(r!["path"]);
            if (!byPath.TryGetValue(path, out var counts))
            {
                byPath[path] = counts = new int[3];
            }

            counts[k] = r["count"]!.GetValue<int>();
        }
    }

    text.Append("\n## Tests that wait, skip or retry\n\n");
    text.Append(Table(["file", "sleep-like", "skips", "retry/flaky"],
        byPath.OrderByDescending(kv => kv.Value[0] * 3 + kv.Value[1] * 2 + kv.Value[2]).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(n)
            .Select(kv => new[] { $"`{kv.Key}`", kv.Value[0].ToString(CultureInfo.InvariantCulture), kv.Value[1].ToString(CultureInfo.InvariantCulture), kv.Value[2].ToString(CultureInfo.InvariantCulture) })));
    text.Append("\n## Commits about unstable tests\n\n");
    text.Append(Table(["commit", "date", "subject", "tests"],
        Rows("flakyCommits").Select(c => new[] { S(c["sha"]), S(c["date"]), S(c["subject"]).Replace("|", "/"), string.Join(", ", c["tests"]!.AsArray().Take(3).Select(Code)) })));
    text.Append("\n## Largest documents\n\n");
    text.Append(Table(["file", "words", "commits", "last change"],
        Rows("docs").Select(d => new[] { Code(d["path"]), S(d["words"]), S(d["commits"]), S(d["last"]) })));
    text.Append("\n## Documents changed most\n\n");
    text.Append(Table(["file", "commits", "words", "last change"],
        Rows("docsChanged").Select(d => new[] { Code(d["path"]), S(d["commits"]), S(d["words"]), S(d["last"]) })));
    return text.ToString();
}

sealed class FileStat
{
    public int Commits, Eligible, Added, Deleted, Fixes;
    public string Last = "";
}

sealed record Commit(string Sha, string Date, string Subject, List<(string Path, int Added, int Deleted)> Files);

sealed record Doc(string Path, int Words, int Lines);

sealed record Changed(string Path, FileStat Stat, int Loc, double Score);

sealed record Coupling(string A, string B, int Shared, double Degree, double OfSmaller, bool CrossArea, bool CodeAndTest);
