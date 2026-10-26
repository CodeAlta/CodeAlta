using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeAlta.Evals;

/// <summary>What a run left: the final answer, and the folder of the project it worked in.</summary>
internal sealed record EvalOutcome(string? FinalMessage, string ProjectFolder);

/// <summary>
/// A task of the evaluation: a prompt, the files of the project it starts in, and a check of what the run left. A task
/// that needs a tool (an MCP server) says so, and is told where it is.
/// </summary>
internal sealed record EvalTask(string Id, string Family, string Prompt, Func<EvalContext, IReadOnlyDictionary<string, string>> Files, Func<EvalOutcome, bool> Check)
{
    /// <summary>Gets whether the task should profit from Code Mode: a sequential control does not.</summary>
    public bool CodeModeHelps { get; init; } = true;
}

/// <summary>What the fixtures of a run may need to know.</summary>
internal sealed record EvalContext(string TinyMcpServer);

/// <summary>The tasks, by family (section 12 of the plan). Families that need the script library or workflows come later.</summary>
internal static class EvalTasks
{
    public static IReadOnlyList<EvalTask> All { get; } =
    [
        // Chaining: each call needs the result of the one before.
        new("chain-config", "chaining",
            "Read config/settings.json, follow the file its \"next\" names, then the file that one names, and so on until a file has no \"next\". Answer with the value of \"secret\" of the last file, alone on the last line.",
            _ => Chain(6),
            outcome => LastLine(outcome) == "orchid-7319"),

        // Fan-out over data: the same call over many files, then a computation.
        new("fanout-status", "fan-out over data",
            "The folder reports holds one JSON report per service. How many reports have \"status\": \"failed\", and what is the sum of their \"errors\"? Answer with the two numbers, as `failed=<n> errors=<m>`, on the last line.",
            _ => Reports(40),
            outcome => LastLine(outcome)?.Replace(" ", string.Empty, StringComparison.Ordinal) == ExpectedReports(40)),

        // Finding and using an MCP tool the session was not told about.
        new("mcp-echo", "finding and using an MCP tool",
            "This project configures an MCP server named tiny. Use its echo tool with the text `codemode-4711` and answer with exactly what the tool returned, on the last line.",
            context => new Dictionary<string, string>
            {
                [".alta/mcp.json"] = JsonSerializer.Serialize(new { mcpServers = new { tiny = new { command = "dotnet", args = new[] { context.TinyMcpServer } } } }),
                ["readme.md"] = "# Fixture\nA project with an MCP server.\n",
            },
            outcome => LastLine(outcome)?.Contains("echo:codemode-4711", StringComparison.Ordinal) == true),

        // Recovering from a failing call: some files are missing.
        new("recover-missing", "recovering from a failing call",
            "Each of the files data/n01.txt to data/n12.txt should hold one integer, but some of them are missing. Add the integers of the files that exist and answer with the sum alone on the last line.",
            _ => Numbers(),
            outcome => LastLine(outcome) == ExpectedNumbers().ToString(CultureInfo.InvariantCulture)),

        // A sequential control: one small edit, where a script should not help.
        new("edit-rename", "sequential control",
            "In src/app.js, rename the function `computeTotal` to `sumPrices`, everywhere it is used in that file. Answer `done` when it is done.",
            _ => new Dictionary<string, string>
            {
                ["src/app.js"] = "function computeTotal(prices) {\n  return prices.reduce((a, b) => a + b, 0);\n}\n\nconsole.log(computeTotal([1, 2, 3]));\nexport { computeTotal };\n",
            },
            outcome =>
            {
                var text = File.ReadAllText(Path.Combine(outcome.ProjectFolder, "src", "app.js"));
                return !text.Contains("computeTotal", StringComparison.Ordinal) && Regex.Count(text, "sumPrices") == 3;
            })
        { CodeModeHelps = false },
    ];

    private static string? LastLine(EvalOutcome outcome)
        => outcome.FinalMessage?.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault()?.Trim('`', '*', '.', ' ');

    private static Dictionary<string, string> Chain(int length)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = Enumerable.Range(0, length).Select(index => index == 0 ? "config/settings.json" : $"config/part-{(index * 37) % 101:D3}.json").ToArray();
        for (var index = 0; index < length; index++)
        {
            files[names[index]] = index + 1 < length
                ? JsonSerializer.Serialize(new { next = names[index + 1], secret = $"decoy-{index}" })
                : JsonSerializer.Serialize(new { secret = "orchid-7319" });
        }

        return files;
    }

    private static Dictionary<string, string> Reports(int count)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            files[$"reports/service-{index:D2}.json"] = JsonSerializer.Serialize(new { service = $"svc-{index}", status = Failed(index) ? "failed" : "ok", errors = Errors(index) });
        }

        return files;
    }

    private static bool Failed(int index) => index % 7 == 3 || index % 11 == 5;

    private static int Errors(int index) => (index * 13) % 9;

    private static string ExpectedReports(int count)
    {
        var failed = Enumerable.Range(0, count).Where(Failed).ToArray();
        return string.Create(CultureInfo.InvariantCulture, $"failed={failed.Length}errors={failed.Sum(Errors)}");
    }

    private static readonly int[] Missing = [3, 8, 11];

    private static Dictionary<string, string> Numbers()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index <= 12; index++)
        {
            if (!Missing.Contains(index)) files[$"data/n{index:D2}.txt"] = (index * 17 % 23 + 4).ToString(CultureInfo.InvariantCulture);
        }

        return files;
    }

    private static int ExpectedNumbers() => Enumerable.Range(1, 12).Where(index => !Missing.Contains(index)).Sum(index => index * 17 % 23 + 4);

    /// <summary>Writes the files of a task in a new folder.</summary>
    public static void Write(string folder, IReadOnlyDictionary<string, string> files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new UTF8Encoding(false));
        }
    }
}
