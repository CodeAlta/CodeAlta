// Checks a health report (report.json) and builds its readable forms beside it:
//   index.html  the report, one self-contained file (the template of the skill with the data inside)
//   report.md   the same content in short Markdown, for agents and terminals
//
//   dotnet run build_report.cs -- <folder of the report>            check and build
//   dotnet run build_report.cs -- <folder of the report> --check    check only
//
// Errors stop the build; warnings are printed and the build goes on.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Unicode;

const string Marker = "__REPORT_DATA__";
var enums = new (string Key, string[] Allowed)[]
{
    ("action", ["delete", "merge", "simplify", "move", "fix", "rewrite", "guard", "add"]),
    ("strength", ["strong", "worth-exploring", "speculative"]),
    ("impact", ["high", "medium", "low"]),
    ("effort", ["S", "M", "L"]),
    ("risk", ["low", "medium", "high"]),
    ("status", ["proposed", "accepted", "deferred", "rejected", "done"]),
};
string[] voteValues = ["found", "confirmed", "refined", "disputed", "unreviewed"];
string[] required = ["id", "title", "lens", "action", "strength", "impact", "effort", "claim", "evidence", "change", "balance"];
string[] diagrams = ["boxes", "layers", "flow", "edges", "bars", "mass", "text", "code"];

string? target = null, template = null;
var checkOnly = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--check": checkOnly = true; break;
        case "--template" when i + 1 < args.Length: template = args[++i]; break;
        case "-h" or "--help":
            Console.WriteLine("""
                Checks report.json and builds index.html and report.md beside it.

                  dotnet run build_report.cs -- <folder of the report, or its report.json> [--check] [--template <file>]

                  --check            check report.json and build nothing
                  --template <file>  another template than assets/report-template.html of the skill
                """);
            return 0;
        default: target ??= args[i]; break;
    }
}
if (target is null)
{
    Console.Error.WriteLine("the folder of the report is missing");
    return 1;
}

var jsonPath = target.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? target : Path.Combine(target, "report.json");
var folder = Path.GetDirectoryName(Path.GetFullPath(jsonPath))!;
JsonObject report;
try
{
    report = JsonNode.Parse(File.ReadAllText(jsonPath), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })!.AsObject();
}
catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"cannot read {jsonPath}: {e.Message}");
    return 1;
}

var root = RepoRoot(folder);
var errors = new List<string>();
var warnings = new List<string>();
Check();
foreach (var w in warnings)
{
    Console.WriteLine("warning: " + w);
}

foreach (var e in errors)
{
    Console.WriteLine("error: " + e);
}

var count = Objects(report["findings"]).Count();
if (errors.Count > 0)
{
    Console.Error.WriteLine($"{errors.Count} error(s): nothing was built");
    return 1;
}

if (checkOnly)
{
    Console.WriteLine($"{count} findings, {warnings.Count} warning(s): report.json is valid");
    return 0;
}

if (!Truthy(report["path"]) && root is not null)
{
    report["path"] = Path.GetRelativePath(root, folder).Replace('\\', '/');
}

template ??= Path.Combine(ScriptFolder(), "..", "assets", "report-template.html");
var html = File.ReadAllText(template);
var at = html.IndexOf(Marker, StringComparison.Ordinal);
if (at < 0)
{
    Console.Error.WriteLine($"{template} has no {Marker}");
    return 1;
}

// The encoder writes `<`, `>` and `&` as escapes, so that no text of the report can end the script element that holds the data.
var data = report.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) });
File.WriteAllText(Path.Combine(folder, "index.html"), string.Concat(html.AsSpan(0, at), data, html.AsSpan(at + Marker.Length)));
File.WriteAllText(Path.Combine(folder, "report.md"), Markdown());
Console.WriteLine($"{count} findings, {warnings.Count} warning(s) -> {Path.Combine(folder, "index.html")}, report.md");
return 0;

// ---- JSON as the report uses it: a missing field, an empty text and an empty list all count as absent
static string ScriptFolder([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

static bool Truthy(JsonNode? node) => node switch
{
    null => false,
    JsonArray a => a.Count > 0,
    JsonObject o => o.Count > 0,
    JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
    JsonValue v when v.TryGetValue<bool>(out var b) => b,
    _ => true,
};

static string Text(JsonNode? node) => node switch
{
    null => "",
    JsonValue v when v.TryGetValue<string>(out var s) => s,
    _ => node.ToJsonString(),
};

// A text, or the items of a list joined in one line.
static string Flat(JsonNode? node) => node is JsonArray a ? string.Join("; ", a.Select(Text)) : Text(node).Trim();

static IEnumerable<JsonNode?> Items(JsonNode? node) => node as JsonArray ?? [];

static IEnumerable<JsonObject> Objects(JsonNode? node) => Items(node).OfType<JsonObject>();

// How many paths of the working folder differ from the commit, the folder of reports left out; null without git.
static int? Uncommitted(string root)
{
    try
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-C", root, "status", "--porcelain", "--", ".", ":(exclude).alta" })
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info)!;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length : null;
    }
    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        return null;
    }
}

static string? RepoRoot(string folder)
{
    try
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-C", folder, "rev-parse", "--show-toplevel" })
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info)!;
        var text = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 && text.Length > 0 ? Path.GetFullPath(text) : null;
    }
    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        return null; // no git: the cited files are not checked
    }
}

// What is wrong with a cited file and line range, or null.
static string? Cited(string path, string line)
{
    if (!File.Exists(path))
    {
        return "which is not in the repository";
    }

    line = line.Trim();
    if (line.Length == 0)
    {
        return null;
    }

    var m = Regex.Match(line, @"^(\d+)(?:-(\d+))?$");
    var first = m.Success ? int.Parse(m.Groups[1].Value) : 0;
    var last = m.Success && m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : first;
    if (!m.Success || first < 1 || last < first)
    {
        return "which is not a line or a range such as 41 or 12-60";
    }

    var data = File.ReadAllBytes(path);
    var lines = data.AsSpan().Count((byte)'\n') + (data.Length > 0 && data[^1] != (byte)'\n' ? 1 : 0);
    return last > lines ? $"and the file has {lines} lines" : null;
}

void Check()
{
    foreach (var key in new[] { "title", "repo", "date", "verdict" })
    {
        if (!Truthy(report[key]))
        {
            errors.Add($"report: `{key}` is missing");
        }
    }

    // A review may keep no finding: the list is there, and can be empty.
    if (report["findings"] is not JsonArray listed)
    {
        errors.Add("report: `findings` is missing");
    }
    else if (listed.Any(entry => entry is not JsonObject))
    {
        errors.Add("report: every entry of `findings` is a finding, not its id or a text");
    }

    foreach (var extra in Objects(report["sections"]).Where(extra => extra["html"] is not null))
    {
        errors.Add($"section `{Text(extra["title"])}`: a section takes `text`; the report shows no free HTML");
    }

    if (root is not null && Uncommitted(root) is > 0 and var dirty)
    {
        warnings.Add($"{dirty} paths of the working folder have uncommitted changes: the evidence was read from it, not from the commit the report names. Say so in `method`");
    }

    var analysts = Objects(report["analysts"]).Select(a => Text(a["id"])).ToHashSet();
    var drivers = Objects(report["analysts"]).Where(a => Text(a["role"]) == "driver").Select(a => Text(a["id"])).ToHashSet();
    foreach (var a in Objects(report["analysts"]))
    {
        foreach (var key in new[] { "analysis", "review" })
        {
            if (Truthy(a[key]) && !File.Exists(Path.Combine(folder, Text(a[key]))))
            {
                warnings.Add($"analyst {Text(a["id"])}: `{Text(a[key])}` does not exist in the report folder");
            }
        }
    }

    var ids = new HashSet<string>();
    var grade = new Dictionary<string, string>();
    var findings = Objects(report["findings"]).ToList();
    foreach (var f in findings)
    {
        var id = Truthy(f["id"]) ? Text(f["id"]) : "?";
        if (!ids.Add(id))
        {
            errors.Add($"{id}: the id is used twice");
        }

        grade[id] = Text(f["strength"]);
        if (!Regex.IsMatch(id, @"^[A-Za-z][\w-]*$"))
        {
            errors.Add($"{id}: an id starts with a letter and holds letters, digits, `-` and `_` only");
        }

        foreach (var key in required.Where(key => !Truthy(f[key])))
        {
            errors.Add($"{id}: `{key}` is missing");
        }

        foreach (var (key, allowed) in enums)
        {
            if (f[key] is not null && !allowed.Contains(Text(f[key])))
            {
                errors.Add($"{id}: `{key}` is `{Text(f[key])}`, not one of {string.Join(", ", allowed)}");
            }
        }

        var located = false;
        foreach (var e in Items(f["evidence"]))
        {
            if (e is not JsonObject entry || !(Truthy(entry["text"]) || Truthy(entry["file"]) || Truthy(entry["command"])))
            {
                errors.Add($"{id}: an evidence entry needs `text`, `file` or `command`");
                continue;
            }

            located |= Truthy(entry["file"]) || Truthy(entry["command"]);
            if (Truthy(entry["file"]) && root is not null && Cited(Path.Combine(root, Text(entry["file"])), Text(entry["line"])) is { } problem)
            {
                warnings.Add($"{id}: the evidence cites `{Text(entry["file"])}`" + (Truthy(entry["line"]) ? $" line {Text(entry["line"])}" : "") + $", {problem}");
            }
        }

        var votes = (f["votes"] as JsonObject ?? []).ToList();
        foreach (var (who, vote) in votes)
        {
            if (!voteValues.Contains(Text(vote)))
            {
                errors.Add($"{id}: the vote of `{who}` is `{Text(vote)}`, not one of {string.Join(", ", voteValues)}");
            }

            if (analysts.Count > 0 && !analysts.Contains(who))
            {
                errors.Add($"{id}: a vote names `{who}`, who is not in `analysts`");
            }
        }

        foreach (var side in new[] { "before", "after" })
        {
            foreach (var spec in (f[side] is JsonArray list ? list : [f[side]?.DeepClone()]).OfType<JsonObject>())
            {
                if (!diagrams.Contains(Text(spec["type"])))
                {
                    errors.Add($"{id}: `{side}` has the diagram type `{Text(spec["type"])}`, not one of {string.Join(", ", diagrams)}");
                }
            }
        }

        var balance = f["balance"] as JsonObject;
        if (Text(f["strength"]) == "strong")
        {
            if (!located)
            {
                errors.Add($"{id}: a strong finding cites a file or a command in its evidence");
            }

            foreach (var key in new[] { "firstStep", "verify" }.Where(key => !Truthy(f[key])))
            {
                errors.Add($"{id}: a strong finding has `{key}`");
            }

            if (votes.Any(v => Text(v.Value) == "disputed") && !Truthy(f["dissent"]))
            {
                errors.Add($"{id}: a strong finding that an analyst disputed says in `dissent` how it was settled");
            }

            var supporters = votes.Where(v => Text(v.Value) is "found" or "confirmed" or "refined").Select(v => v.Key).ToList();
            if (supporters.Count < 2)
            {
                errors.Add($"{id}: a strong finding is found or confirmed by two analysts; this one is worth-exploring");
            }

            if (!supporters.Any(drivers.Contains))
            {
                errors.Add($"{id}: a strong finding is one the driver checked in the code: its vote is `found`, `confirmed` or `refined`");
            }
        }

        if (Text(f["action"]) is "add" or "guard" && !Truthy(balance?["removes"]))
        {
            warnings.Add($"{id}: it adds something and `balance.removes` is empty: say what it removes or prevents, or drop it");
        }

        if (Truthy(balance) && !Truthy(balance!["removes"]) && !Truthy(balance["adds"]))
        {
            errors.Add($"{id}: `balance` names neither what is removed nor what is added");
        }

        if (Truthy(f["before"]) != Truthy(f["after"]))
        {
            warnings.Add($"{id}: it has only one of `before` and `after`");
        }
    }

    var placed = new List<string>();
    foreach (var t in Objects(report["themes"]))
    {
        foreach (var id in Items(t["findings"]).Select(Text))
        {
            if (!ids.Contains(id))
            {
                errors.Add($"theme `{Text(t["title"])}`: `{id}` is not a finding");
            }

            placed.Add(id);
        }
    }

    foreach (var id in placed.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).Order(StringComparer.Ordinal))
    {
        errors.Add($"{id}: it is in two themes");
    }

    if (Truthy(report["themes"]))
    {
        foreach (var id in ids.Except(placed).Order(StringComparer.Ordinal))
        {
            warnings.Add($"{id}: it is in no theme");
        }
    }

    var wave = new Dictionary<string, int>();
    var waves = Objects(report["roadmap"]).ToList();
    for (var n = 0; n < waves.Count; n++)
    {
        foreach (var id in Objects(waves[n]["items"]).Select(it => Text(it["finding"])))
        {
            wave.TryAdd(id, n);
        }
    }

    foreach (var w in waves)
    {
        foreach (var id in Objects(w["items"]).Select(it => Text(it["finding"])))
        {
            if (!ids.Contains(id))
            {
                errors.Add($"roadmap `{Text(w["title"])}`: `{id}` is not a finding");
            }
            else if (grade[id] == "speculative")
            {
                errors.Add($"roadmap `{Text(w["title"])}`: {id} is speculative, and a speculative finding is not in the order of work");
            }
            else if (findings.First(f => Text(f["id"]) == id) is var scheduled && Text(scheduled["status"]) == "rejected")
            {
                errors.Add($"roadmap `{Text(w["title"])}`: {id} was rejected, and a rejected finding is not in the order of work");
            }
            else
            {
                if (!Truthy(scheduled["firstStep"]))
                {
                    errors.Add($"roadmap `{Text(w["title"])}`: {id} has no `firstStep`, and work that is scheduled says how it starts");
                }

                foreach (var dep in Items(scheduled["dependsOn"]).Select(Text).Where(dep => wave.TryGetValue(dep, out var later) && later > wave[id]))
                {
                    errors.Add($"roadmap `{Text(w["title"])}`: {id} waits for {dep}, which is scheduled after it");
                }
            }
        }
    }

    foreach (var f in findings)
    {
        foreach (var dep in Items(f["dependsOn"]).Select(Text).Where(dep => !ids.Contains(dep)))
        {
            errors.Add($"{Text(f["id"])}: `dependsOn` names `{dep}`, which is not a finding");
        }
    }

    // What depends on what has an order: a finding cannot wait for itself.
    var needs = findings.ToDictionary(f => Text(f["id"]), f => Items(f["dependsOn"]).Select(Text).Where(ids.Contains).ToArray());
    var state = new Dictionary<string, int>(); // 1: being walked, 2: done
    bool Cycles(string id)
    {
        if (state.TryGetValue(id, out var seen))
        {
            return seen == 1;
        }

        state[id] = 1;
        var cycles = needs[id].Any(Cycles);
        state[id] = 2;
        return cycles;
    }

    foreach (var id in needs.Keys.Where(Cycles))
    {
        errors.Add($"{id}: its `dependsOn` is part of a cycle");
    }

    if (findings.Count > 15)
    {
        warnings.Add($"{findings.Count} findings: a reader acts on a dozen; move the weakest to `dropped` or to a later review");
    }

    if (findings.Count > 0 && !findings.Any(f => Text(f["strength"]) == "strong"))
    {
        warnings.Add("no strong finding: say in the verdict that the review found nothing proven");
    }

    var words = Text(report["verdict"]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    if (words > 70)
    {
        warnings.Add($"the verdict has {words} words: the first screen holds about 60");
    }
}

string Markdown()
{
    var byId = Objects(report["findings"]).ToDictionary(f => Text(f["id"]));
    var text = new StringBuilder();
    void Line(string line = "") => text.Append(line).Append('\n');

    Line($"# {(Truthy(report["title"]) ? Text(report["title"]) : "Codebase health report")}");
    Line();
    Line(string.Join(" · ", new[] { Text(report["repo"]), Truthy(report["commit"]) ? "at " + Text(report["commit"]) : "", Text(report["date"]), Text(report["scope"]) }.Where(x => x.Length > 0)));
    Line();
    Line(Text(report["verdict"]).Trim());
    Line();
    foreach (var m in Objects(report["metrics"]))
    {
        Line($"- **{Text(m["value"])}** {Text(m["label"])}" + (Truthy(m["note"]) ? $" ({Text(m["note"])})" : ""));
    }

    if (Truthy(report["roadmap"]))
    {
        Line();
        Line("## Order of work");
        foreach (var w in Objects(report["roadmap"]))
        {
            Line();
            Line($"**{Text(w["title"])}**" + (Truthy(w["note"]) ? $": {Text(w["note"])}" : ""));
            var n = 0;
            foreach (var it in Objects(w["items"]))
            {
                var id = Text(it["finding"]);
                Line($"{++n}. {id} {(byId.TryGetValue(id, out var f) ? Text(f["title"]) : "")}" + (Truthy(it["note"]) ? $" ({Text(it["note"])})" : ""));
            }
        }
    }

    Line();
    Line("## Findings");
    var themes = Objects(report["themes"]).Select(t => (Title: Text(t["title"]), Thesis: Text(t["thesis"]), Ids: Items(t["findings"]).Select(Text).ToList())).ToList();
    var rest = byId.Keys.Except(themes.SelectMany(t => t.Ids)).ToList();
    if (rest.Count > 0)
    {
        themes.Add((themes.Count > 0 ? "Other findings" : "", "", rest));
    }

    foreach (var (title, thesis, ids) in themes)
    {
        if (title.Length > 0)
        {
            Line();
            Line($"### {title}");
        }

        if (thesis.Length > 0)
        {
            Line();
            Line(thesis);
        }

        foreach (var f in ids.Where(byId.ContainsKey).Select(id => byId[id]))
        {
            var votes = string.Join(", ", (f["votes"] as JsonObject ?? []).Select(v => $"{v.Key} {Text(v.Value)}"));
            Line();
            Line($"#### {Text(f["id"])} {Text(f["title"])}");
            Line();
            Line($"`{Text(f["action"])}` · {Text(f["strength"])} · impact {Text(f["impact"])} · effort {Text(f["effort"])}"
                + (Truthy(f["risk"]) ? $" · risk {Text(f["risk"])}" : "") + (Truthy(f["area"]) ? $" · `{Text(f["area"])}`" : "")
                + (votes.Length > 0 ? $" · {votes}" : ""));
            Line();
            Line(Text(f["claim"]));
            var evidence = Objects(f["evidence"]).ToList();
            if (evidence.Count > 0)
            {
                Line();
                Line("Evidence:");
                foreach (var e in evidence)
                {
                    var where = Truthy(e["file"]) ? $"`{Text(e["file"])}" + (Truthy(e["line"]) ? $":{Text(e["line"])}" : "") + "` " : "";
                    Line($"- {where}{Text(e["text"])}" + (Truthy(e["command"]) ? $" (`{Text(e["command"])}`)" : ""));
                }
            }

            Line();
            Line($"Change: {Flat(f["change"])}");
            Line();
            Line($"Removes: {(Truthy(f["balance"]?["removes"]) ? Flat(f["balance"]!["removes"]) : "nothing")}  ");
            Line($"Adds: {(Truthy(f["balance"]?["adds"]) ? Flat(f["balance"]!["adds"]) : "nothing")}");
            foreach (var (label, key) in new[] { ("First step", "firstStep"), ("Then", "steps"), ("Verify", "verify"), ("Risks", "risks"),
                         ("If we leave it", "ifNot"), ("Not part of this", "outOfScope"), ("Disagreement", "dissent") })
            {
                if (Truthy(f[key]))
                {
                    Line();
                    Line($"{label}: {Flat(f[key])}");
                }
            }
        }
    }

    if (Truthy(report["dropped"]))
    {
        Line();
        Line("## Considered and dropped");
        Line();
        foreach (var d in Objects(report["dropped"]))
        {
            Line($"- {Text(d["title"])}: {Text(d["reason"])}");
        }
    }

    if (Truthy(report["analysts"]))
    {
        Line();
        Line("## Analyses");
        Line();
        foreach (var a in Objects(report["analysts"]))
        {
            Line($"- {(Truthy(a["name"]) ? Text(a["name"]) : Text(a["id"]))} (`{Text(a["model"])}`, {(Truthy(a["role"]) ? Text(a["role"]) : "analyst")}): "
                + string.Join(", ", new[] { "analysis", "review" }.Where(k => Truthy(a[k])).Select(k => $"`{Text(a[k])}`")));
        }
    }

    return text.ToString();
}