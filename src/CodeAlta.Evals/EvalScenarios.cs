using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeAlta.Evals;

/// <summary>What a turn left: its final answer, the folder of the project, and where the fixtures of the scenario are.</summary>
internal sealed record EvalTurnOutcome(string? FinalMessage, string ProjectFolder, string ScenarioFolder, int Turn);

/// <summary>One prompt of a scenario and the check of what its turn left: null when it passes, else why it fails.</summary>
internal sealed record EvalTurn(string Prompt, Func<EvalTurnOutcome, string?> Check);

/// <summary>
/// A scenario of the evaluation of the Adaptive mode: a project (the shop fixture, the files of the scenario over it, and
/// files made by code) and several turns in one session, each turn with its check. A check may run tests the session never
/// sees (<c>Scenarios/&lt;id&gt;/hidden/turn&lt;n&gt;.test.mjs</c>), so that passing its own tests is not enough.
/// </summary>
internal sealed record EvalScenario(string Id, string Description, string GoodRun, IReadOnlyList<EvalTurn> Turns)
{
    /// <summary>Gets the files made by code, written over the fixture, such as long documents.</summary>
    public Func<IReadOnlyDictionary<string, string>>? Generated { get; init; }
}

/// <summary>The scenarios (section 3 of the plan of #273).</summary>
internal static class EvalScenarios
{
    /// <summary>The fixture every scenario starts from.</summary>
    public const string BaseFixture = "shop";

    public static IReadOnlyList<EvalScenario> All { get; } =
    [
        new("small-steps", "explain a function, rename, fix a typo, add a test case, commit", "stays on quick or standard",
        [
            new("What does `cartTotal` in src/cart.js return for an empty cart? Answer in one sentence.",
                outcome => Regex.IsMatch(outcome.FinalMessage ?? string.Empty, @"(?<![\w.])0(?![\w.])") ? null : "the answer does not say 0"),
            new("Rename the function `formatPrice` to `formatAmount` everywhere in the project.",
                outcome => FirstFailure(
                    Absent(outcome, "formatPrice", "src", "test", "README.md"),
                    NodeTests(outcome),
                    Hidden(outcome))),
            new("Fix the spelling mistake in the first paragraph of README.md.",
                outcome => FirstFailure(
                    Read(outcome, "README.md").Contains("recieve", StringComparison.Ordinal) ? "README.md still says recieve" : null,
                    Read(outcome, "README.md").Contains("receive", StringComparison.Ordinal) ? null : "README.md does not say receive")),
            new("Add a test case to test/pricing.test.js: a 100% discount gives 0.",
                outcome => FirstFailure(
                    Regex.Count(Read(outcome, "test/pricing.test.js"), @"\btest\(") > 3 ? null : "no new test in test/pricing.test.js",
                    Read(outcome, "test/pricing.test.js").Contains("100", StringComparison.Ordinal) ? null : "the new test does not use 100",
                    NodeTests(outcome))),
            new("Commit all the changes, with a short message.", Committed),
        ]),

        new("easy-then-hard", "two small requests, a failing test whose cause is hidden, commit", "goes up once, at the third turn",
        [
            new("Which functions does src/inventory.js export? Answer with their names, one per line.",
                outcome => new[] { "available", "reserve", "release" }.All(name => (outcome.FinalMessage ?? string.Empty).Contains(name, StringComparison.Ordinal))
                    ? null : "the answer misses a function"),
            new("In README.md, under the Usage heading, add the line: Run the tests with `npm test`.",
                outcome =>
                {
                    var text = Read(outcome, "README.md");
                    var line = text.IndexOf("Run the tests with `npm test`", StringComparison.Ordinal);
                    return line > text.IndexOf("## Usage", StringComparison.Ordinal) && line < text.IndexOf("## Modules", StringComparison.Ordinal)
                        ? null : "the line is not under Usage";
                }),
            new("`npm test` fails in test/report.test.js. Find the cause and fix it in the source, not in the tests.",
                outcome => FirstFailure(
                    Unchanged(outcome, "test/report.test.js"),
                    NodeTests(outcome),
                    Hidden(outcome))),
            new("Commit all the changes, with a short message.", Committed),
        ]),

        new("hard-then-easy", "a feature across three files with tests, then three small follow-ups", "starts strong, delegates or comes down once",
        [
            new("""
                Add coupon codes to the shop:
                - a new module src/coupons.js exporting `parseCoupon(code)`, which accepts `SAVE<n>` (n percent off, 1 to 90) and `FLAT<n>` (n cents off, at least 1), in any case, and throws a RangeError for any other code;
                - `cartTotal(cart, options)` in src/cart.js takes an optional `{ coupon }` and applies it to the total after the discounts of the lines, never below 0; a percent coupon rounds half up to the cent;
                - src/cli.js accepts `--coupon <code>` and prints the total with the coupon applied.
                Add tests for each part, and keep `npm test` passing.
                """,
                outcome => FirstFailure(
                    NodeTests(outcome),
                    Directory.EnumerateFiles(Path.Combine(outcome.ProjectFolder, "test")).Any(file => File.ReadAllText(file).Contains("oupon", StringComparison.Ordinal))
                        ? null : "no test of the coupons",
                    Hidden(outcome))),
            new("Add a JSDoc comment above `parseCoupon` that says which codes it accepts.",
                outcome => FirstFailure(
                    Regex.IsMatch(Read(outcome, "src/coupons.js"), @"\*/\s*export\s+(async\s+)?(function\s+parseCoupon|const\s+parseCoupon)") ? null : "no JSDoc right above parseCoupon",
                    NodeTests(outcome))),
            new("Add src/coupons.js to the list of modules in README.md.",
                outcome => Read(outcome, "README.md").Contains("coupons.js", StringComparison.Ordinal) ? null : "README.md does not list coupons.js"),
            new("Commit everything with a short message.", Committed),
        ]),

        new("race-condition", "a question, a concurrency bug of an async cache with hidden cases, commit", "goes up once, at the second turn",
        [
            new("What does `createCache` in src/cache.js return? Answer in one sentence.",
                outcome => new[] { "get", "invalidate" }.All(name => (outcome.FinalMessage ?? string.Empty).Contains(name, StringComparison.Ordinal)) ? null : "the answer misses get or invalidate"),
            new("""
                Users report two problems with the cache of src/cache.js: after `invalidate(key)`, it can serve an old value again (test/cache.test.js fails on it), and a load that failed keeps failing until it expires instead of being tried again.
                Find the causes and fix them in src/cache.js, with regression tests, and keep `npm test` passing.
                """,
                outcome => FirstFailure(NodeTests(outcome), Hidden(outcome))),
            new("Commit all the changes, with a short message.", Committed),
        ]),

        new("long-context","a wide reading turn (about 100k tokens), then a mix", "switches at most once",
        [
            new("""
                Read every design note under docs/, and write docs/index.md with one line per note: its file name and a summary of a few words.
                Then answer: which note defines the current retry policy of the HTTP client, and what is its maximum number of retries?
                """,
                outcome =>
                {
                    var index = Path.Combine(outcome.ProjectFolder, "docs", "index.md");
                    if (!File.Exists(index)) return "no docs/index.md";
                    var text = File.ReadAllText(index);
                    var missing = DesignNotes.Names.Where(name => !text.Contains(Path.GetFileNameWithoutExtension(name), StringComparison.Ordinal)).ToArray();
                    if (missing.Length > 0) return $"docs/index.md misses {missing.Length} notes";
                    var answer = outcome.FinalMessage ?? string.Empty;
                    return answer.Contains(Path.GetFileNameWithoutExtension(DesignNotes.Policy), StringComparison.Ordinal) && Regex.IsMatch(answer, @"(?<![\w.])4(?![\w.])")
                        ? null : "the answer does not name the policy note and 4";
                }),
            new("Set MAX_RETRIES in src/http.js to the maximum of that policy.",
                outcome => Regex.IsMatch(Read(outcome, "src/http.js"), @"MAX_RETRIES\s*=\s*4\s*;") ? null : "MAX_RETRIES is not 4"),
            new("Fix the spelling mistake in the comments of src/http.js.",
                outcome => Read(outcome, "src/http.js").Contains("occured", StringComparison.Ordinal) ? "src/http.js still says occured" : null),
            new("Commit everything with a short message.", Committed),
        ])
        { Generated = DesignNotes.Files },

        new("one-big-task", "one substantial, well-scoped request: a small feature with its tests", "does it on standard and passes, or goes up once when its checks keep failing",
        [
            new("""
                Add invoices to the shop: a module src/invoice.js exporting `buildInvoice(cart, region)` and `renderInvoice(invoice)`.
                - `buildInvoice` gives `{ lines, subtotal, tax, total }`: one line per line of the cart, with `sku`, `quantity`, `unitPrice`, `discount` (percent, 0 when none) and `amount` (the line after its discount, rounded half up to the cent as `applyDiscount` does); `subtotal` is the sum of the amounts; `tax` is the tax of the region (`taxFor` in src/pricing.js) on the subtotal, rounded half up to the cent; `total` is the subtotal plus the tax. Every amount is an integer number of cents. An empty cart gives an invoice of zeros, and an unknown region throws.
                - `renderInvoice` returns a text with one row per line (`<quantity> x <sku>` and its amount), then the rows `Subtotal`, `Tax (<rate>%)` and `Total`, each amount formatted with `formatPrice`.
                Add tests in test/invoice.test.js, and keep `npm test` passing.
                """,
                outcome => FirstFailure(
                    File.Exists(Path.Combine(outcome.ProjectFolder, "test", "invoice.test.js")) ? null : "no test/invoice.test.js",
                    NodeTests(outcome),
                    Hidden(outcome))),
        ]),
    ];

    /// <summary>Writes the project of a scenario: the shop, the files of the scenario, the files made by code.</summary>
    public static void WriteProject(EvalScenario scenario, string scenariosFolder, string project)
    {
        Copy(Path.Combine(scenariosFolder, BaseFixture), project);
        Copy(Path.Combine(scenariosFolder, scenario.Id, "project"), project);
        if (scenario.Generated is { } generated) EvalTasks.Write(project, generated());
    }

    private static void Copy(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // The fixtures are written with line feeds whatever the checkout did, as the hidden checks compare them.
            File.WriteAllText(target, File.ReadAllText(file).ReplaceLineEndings("\n"), new UTF8Encoding(false));
        }
    }

    private static string? FirstFailure(params string?[] failures) => failures.FirstOrDefault(static failure => failure is not null);

    private static string Read(EvalTurnOutcome outcome, string path)
    {
        var full = Path.Combine(outcome.ProjectFolder, path.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? File.ReadAllText(full) : string.Empty;
    }

    private static string? Absent(EvalTurnOutcome outcome, string text, params string[] paths)
    {
        foreach (var path in paths)
        {
            var full = Path.Combine(outcome.ProjectFolder, path);
            var files = Directory.Exists(full) ? Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories) : File.Exists(full) ? [full] : [];
            if (files.FirstOrDefault(file => File.ReadAllText(file).Contains(text, StringComparison.Ordinal)) is { } found)
            {
                return $"{Path.GetRelativePath(outcome.ProjectFolder, found)} still has {text}";
            }
        }

        return null;
    }

    private static string? Unchanged(EvalTurnOutcome outcome, string path)
    {
        var original = Path.Combine(outcome.ScenarioFolder, "project", path.Replace('/', Path.DirectorySeparatorChar));
        return Read(outcome, path).ReplaceLineEndings("\n") == File.ReadAllText(original).ReplaceLineEndings("\n") ? null : $"{path} was changed";
    }

    private static string? NodeTests(EvalTurnOutcome outcome)
    {
        var (code, output) = Run(outcome.ProjectFolder, "node", ["--test"]);
        return code == 0 ? null : "npm test fails: " + Tail(output);
    }

    private static string? Hidden(EvalTurnOutcome outcome)
    {
        var file = Path.Combine(outcome.ScenarioFolder, "hidden", $"turn{outcome.Turn}.test.mjs");
        var (code, output) = Run(outcome.ProjectFolder, "node", ["--test", file], ("EVAL_PROJECT_URL", new Uri(Path.TrimEndingDirectorySeparator(outcome.ProjectFolder) + Path.DirectorySeparatorChar).AbsoluteUri));
        return code == 0 ? null : "hidden tests fail: " + Tail(output);
    }

    private static string? Committed(EvalTurnOutcome outcome)
    {
        var (_, status) = Run(outcome.ProjectFolder, "git", ["status", "--porcelain"]);
        if (status.Trim().Length > 0) return "uncommitted changes: " + Tail(status);
        var (_, count) = Run(outcome.ProjectFolder, "git", ["rev-list", "--count", "HEAD"]);
        return int.TryParse(count.Trim(), CultureInfo.InvariantCulture, out var commits) && commits >= 2 ? null : "no new commit";
    }

    private static string Tail(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var failing = lines.Where(static line => line.Contains("not ok", StringComparison.Ordinal) || line.Contains("✖", StringComparison.Ordinal)).Take(3).ToArray();
        return string.Join(" | ", failing.Length > 0 ? failing : lines.TakeLast(3));
    }

    /// <summary>Runs a program in a folder and gives its exit code and its output, standard error included.</summary>
    internal static (int ExitCode, string Output) Run(string folder, string program, IEnumerable<string> arguments, params (string Name, string Value)[] environment)
    {
        var start = new ProcessStartInfo(program) { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment) start.Environment[name] = value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "timed out");
        }

        return (process.ExitCode, output.Result + error.Result);
    }
}

/// <summary>
/// The design notes of the long-context scenario: a few thousand tokens each, about 100k in all. One note sets the retry
/// policy of the HTTP client, a later one supersedes it, and another speaks of the retries of something else.
/// </summary>
internal static class DesignNotes
{
    private static readonly string[] Topics =
    [
        "catalog", "pricing-engine", "checkout", "payments", "http-client", "search", "inventory-sync", "importer", "notifications", "auth",
        "sessions", "caching", "metrics", "logging", "feature-flags", "localization", "tax-rules", "resilience-review", "shipping", "returns",
        "reports", "admin-console", "data-retention", "deployment",
    ];

    /// <summary>Gets the file names of the notes, under docs/.</summary>
    public static IReadOnlyList<string> Names { get; } = Topics.Select(static (topic, index) => $"{index + 1:D2}-{topic}.md").ToArray();

    /// <summary>Gets the note that defines the current retry policy.</summary>
    public static string Policy => Names[17];

    public static IReadOnlyDictionary<string, string> Files()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < Names.Count; index++)
        {
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"# {Title(Topics[index])}\n\n");
            var random = new Random(1000 + index);
            for (var section = 1; section <= 4; section++)
            {
                text.Append(CultureInfo.InvariantCulture, $"## {Title(Topics[index])}: part {section}\n\n");
                for (var paragraph = 0; paragraph < 4; paragraph++)
                {
                    text.Append(Paragraph(random, Topics[index])).Append("\n\n");
                }

                if (section == 3) text.Append(Special(index)).Append("\n\n");
            }

            files[$"docs/{Names[index]}"] = text.ToString();
        }

        return files;
    }

    private static string Special(int index) => index switch
    {
        4 => "### Retries\n\nThe HTTP client retries a request that timed out or got a 5xx answer at most 5 times, with a delay that doubles from 200 ms.",
        7 => "### Retries\n\nThe importer retries a file that failed to parse 3 times before it moves it to the rejected folder.",
        17 => $"### Retry policy\n\nThis note supersedes the retry rules of {Path.GetFileNameWithoutExtension(Names[4])}: the HTTP client now retries a request at most 4 times, "
            + "with a delay that doubles from 200 ms, because five retries kept the checkout waiting for too long during the incident of March.",
        _ => string.Empty,
    };

    private static string Title(string topic) => string.Join(' ', topic.Split('-').Select(static word => char.ToUpperInvariant(word[0]) + word[1..]));

    private static readonly string[] Subjects = ["The service", "The worker", "Each request", "The scheduler", "The gateway", "A batch", "The queue", "The store", "Every node", "The client"];
    private static readonly string[] Verbs = ["records", "validates", "forwards", "compacts", "publishes", "reconciles", "indexes", "throttles", "replays", "partitions"];
    private static readonly string[] Objects = ["the events of the day", "the pending orders", "a snapshot of the state", "the changes of the catalog", "the counters of each shard",
        "the expired entries", "the audit trail", "the outgoing messages", "the configuration of the region", "the summaries of the week"];
    private static readonly string[] Conditions = ["when the load is low", "before the nightly window", "after a deployment", "once the lease is renewed", "while the primary is healthy",
        "whenever a threshold is crossed", "at the end of each hour", "if the checksum matches", "unless a maintenance is planned", "as soon as the lock is free"];

    private static string Paragraph(Random random, string topic)
    {
        var text = new StringBuilder();
        for (var sentence = 0; sentence < 7; sentence++)
        {
            text.Append(Subjects[random.Next(Subjects.Length)]).Append(" of the ").Append(topic.Replace('-', ' ')).Append(' ')
                .Append(Verbs[random.Next(Verbs.Length)]).Append(' ').Append(Objects[random.Next(Objects.Length)]).Append(' ')
                .Append(Conditions[random.Next(Conditions.Length)]).Append(", and keeps ").Append(random.Next(2, 90)).Append(" of them for review. ");
        }

        return text.ToString().TrimEnd();
    }
}
