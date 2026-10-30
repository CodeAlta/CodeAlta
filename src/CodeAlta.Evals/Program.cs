using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Evals;

// The evaluations, run by hand, never in CI: they spend real tokens. The report goes to tmp/evals/<date>/ of the
// repository: report.md, runs.json, and the JSONL of each run.
// - Code Mode (--models): every task, per model and per mode, several times, through altacli in an ephemeral state root.
// - The Adaptive mode (--scenarios): scenarios of several turns in one session, per configuration (an agent prompt and
//   the tier it starts on), with the tiers given on the command line and every request of every leg priced.
internal static class Program
{
    private const string Usage = """
        Usage: CodeAlta.Evals --models <ref,ref...> [options]
          --models REFS     model references, such as codex:gpt-6.1-sol@low,claude-code:haiku
          --modes MODES     off,on (default), only
          --tasks IDS       task ids or families (default: all)
          --repeat N        runs of each task, model and mode (default 2)
          --parallel N      runs at the same time (default 2)
          --timeout SECONDS time allowed for one run (default 600)
          --judge REF       a model that reads the failed and costly runs and names the friction
          --altacli PATH    the altacli to run (default: the build of this checkout)
          --out FOLDER      where the report goes (default: tmp/evals/<date>)
          --list            list the tasks and the scenarios and exit

        Usage: CodeAlta.Evals --scenarios <ids|all> --tiers complex=REF,standard=REF,quick=REF [options]
          --configs CONFIGS prompt@tier list (default: default@complex,default@standard,adaptive@standard,
                            adaptive-delegate@standard,adaptive-review@standard)
          --repeat N        runs of each scenario and configuration (default 3)
          --parallel N      runs at the same time (default 2)
          --timeout SECONDS time allowed for one turn (default 900)
          --retries N       times an invalid run (provider, harness) is run again (default 2)
          --judge REF       a model that reads the Adaptive runs and names each wrong choice
          --altacli PATH    the altacli to run (default: the build of this checkout)
          --out FOLDER      where the report goes (default: tmp/evals/<date>)
        """;

    private static async Task<int> Main(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] is "-h" or "--help") { Console.WriteLine(Usage); return 0; }
            if (args[index] == "--list") { options["list"] = "true"; continue; }
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length) { Console.Error.WriteLine($"Unknown argument '{args[index]}'.\n{Usage}"); return 2; }
            options[args[index][2..]] = args[++index];
        }

        if (options.ContainsKey("list"))
        {
            foreach (var task in EvalTasks.All) Console.WriteLine($"{task.Id,-18} {task.Family}");
            foreach (var scenario in EvalScenarios.All) Console.WriteLine($"{scenario.Id,-18} scenario, {scenario.Turns.Count} turns: {scenario.Description}");
            return 0;
        }

        if (!options.ContainsKey("models") && !options.ContainsKey("scenarios")) { Console.Error.WriteLine(Usage); return 2; }
        var repository = FindRepository();
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Debug" : "Release";
        // Absolute: each run starts altacli in the folder of its fixture.
        var altacli = options.TryGetValue("altacli", out var given) ? Path.GetFullPath(given) : Path.Combine(repository, "src", "CodeAlta.Cli", "bin", configuration, "net10.0", OperatingSystem.IsWindows() ? "altacli.exe" : "altacli");
        if (!File.Exists(altacli)) { Console.Error.WriteLine($"{altacli} does not exist: build the solution in {configuration} first, or name one with --altacli."); return 2; }
        if (options.ContainsKey("scenarios")) return await RunScenariosAsync(options, repository, altacli).ConfigureAwait(false);
        var modelList = options["models"];
        var tiny = Path.Combine(repository, "src", "CodeAlta.Tests.TinyMcpServer", "bin", configuration, "net10.0", "CodeAlta.Tests.TinyMcpServer.dll");
        var models = Split(modelList);
        var modes = Split(options.GetValueOrDefault("modes") ?? "off,on");
        var wanted = options.TryGetValue("tasks", out var taskList) ? Split(taskList) : null;
        var tasks = EvalTasks.All.Where(task => wanted is null || wanted.Contains(task.Id) || wanted.Contains(task.Family)).ToArray();
        var repeat = int.Parse(options.GetValueOrDefault("repeat") ?? "2", CultureInfo.InvariantCulture);
        var parallel = int.Parse(options.GetValueOrDefault("parallel") ?? "2", CultureInfo.InvariantCulture);
        var timeout = TimeSpan.FromSeconds(int.Parse(options.GetValueOrDefault("timeout") ?? "600", CultureInfo.InvariantCulture));
        var output = options.GetValueOrDefault("out") ?? Path.Combine(repository, "tmp", "evals", DateTime.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(output);

        var runner = new EvalRunner(altacli, new EvalContext(tiny), timeout);
        var grid = (from task in tasks from model in models from mode in modes from index in Enumerable.Range(1, repeat) select (task, model, mode, index)).ToArray();
        Console.WriteLine($"{grid.Length} runs: {tasks.Length} tasks × {models.Length} models × {modes.Length} modes × {repeat}. Report: {output}");
        var runs = new List<EvalRun>();
        await Parallel.ForEachAsync(grid, new ParallelOptions { MaxDegreeOfParallelism = parallel }, async (cell, token) =>
        {
            var run = await runner.RunAsync(cell.task, cell.model, cell.mode, cell.index, output, token).ConfigureAwait(false);
            lock (runs) runs.Add(run);
            Console.WriteLine($"{(run.Succeeded ? "pass" : "FAIL")} {run.Task} {run.Model} {run.Mode} #{run.Repeat}: {run.Status}, {run.DurationMs / 1000.0:0.0} s, {run.InputTokens ?? 0}+{run.OutputTokens ?? 0} tokens, scripts {run.Scripts}");
        }).ConfigureAwait(false);

        var ordered = runs.OrderBy(static run => run.Task, StringComparer.Ordinal).ThenBy(static run => run.Model, StringComparer.Ordinal)
            .ThenBy(static run => run.Mode, StringComparer.Ordinal).ThenBy(static run => run.Repeat).ToList();
        await File.WriteAllTextAsync(Path.Combine(output, "runs.json"), JsonSerializer.Serialize(ordered, EvalJson.Default.ListEvalRun)).ConfigureAwait(false);
        var report = EvalReport.Markdown(ordered, tasks);
        if (options.TryGetValue("judge", out var judge))
        {
            report += await EvalJudge.RunAsync(altacli, judge, ordered, output, timeout).ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), report, new UTF8Encoding(false)).ConfigureAwait(false);
        Console.WriteLine(report);
        return 0;
    }

    // The grid of the Adaptive mode: scenarios × configurations × repeats, an invalid run run again.
    private static async Task<int> RunScenariosAsync(Dictionary<string, string> options, string repository, string altacli)
    {
        if (!options.TryGetValue("tiers", out var tierList)) { Console.Error.WriteLine("--tiers is required: complex=REF,standard=REF,quick=REF.\n" + Usage); return 2; }
        var tiers = Split(tierList);
        if (tiers.Any(static tier => !tier.Contains('=', StringComparison.Ordinal) || !tier.Contains(':', StringComparison.Ordinal))) { Console.Error.WriteLine($"A tier is <tier>=<provider:model[@effort]>: '{tierList}'."); return 2; }
        // The tiers of the runs, over the settings of the user; no fallback to another model: a failure of the provider is an invalid run.
        var settings = tiers.Select(static tier => tier.Split('=', 2)).Select(static pair => $"models.tiers.{pair[0]}=[\"{pair[1]}\"]").Append("models.fallback.enabled=false").ToArray();
        var wanted = Split(options.GetValueOrDefault("scenarios") ?? "all");
        var scenarios = EvalScenarios.All.Where(scenario => wanted.Contains("all") || wanted.Contains(scenario.Id)).ToArray();
        if (scenarios.Length == 0) { Console.Error.WriteLine($"No scenario '{string.Join(',', wanted)}': see --list."); return 2; }
        var configurations = Split(options.GetValueOrDefault("configs") ?? "default@complex,default@standard,adaptive@standard,adaptive-delegate@standard,adaptive-review@standard")
            .Select(EvalConfiguration.Parse).ToArray();
        var repeat = int.Parse(options.GetValueOrDefault("repeat") ?? "3", CultureInfo.InvariantCulture);
        var parallel = int.Parse(options.GetValueOrDefault("parallel") ?? "2", CultureInfo.InvariantCulture);
        var retries = int.Parse(options.GetValueOrDefault("retries") ?? "2", CultureInfo.InvariantCulture);
        var timeout = TimeSpan.FromSeconds(int.Parse(options.GetValueOrDefault("timeout") ?? "900", CultureInfo.InvariantCulture));
        var output = options.GetValueOrDefault("out") ?? Path.Combine(repository, "tmp", "evals", DateTime.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture) + "-adaptive");
        Directory.CreateDirectory(output);
        var scenariosFolder = Path.Combine(AppContext.BaseDirectory, "Scenarios");
        var promptsFolder = Path.Combine(repository, ".alta", "prompts", "agents");
        var runner = new EvalScenarioRunner(altacli, scenariosFolder, promptsFolder, settings, timeout);
        var prices = new EvalPrices(altacli);
        foreach (var target in tiers.Select(static tier => tier.Split('=', 2)[1]))
        {
            var provider = target.Split(':', 2);
            prices.Get(provider[0], provider[1].Split('@')[0]);
        }

        var grid = (from scenario in scenarios from config in configurations from index in Enumerable.Range(1, repeat) select (scenario, config, index)).ToArray();
        Console.WriteLine($"{grid.Length} runs: {scenarios.Length} scenarios × {configurations.Length} configurations × {repeat}. Report: {output}");
        var runs = new List<EvalScenarioRun>();
        await Parallel.ForEachAsync(grid, new ParallelOptions { MaxDegreeOfParallelism = parallel }, async (cell, token) =>
        {
            for (var attempt = 1; ; attempt++)
            {
                var run = await runner.RunAsync(cell.scenario, cell.config, cell.index, attempt, output, token).ConfigureAwait(false);
                foreach (var request in run.Requests) request.Price = EvalPrices.PriceOf(request, prices.Get(request.Provider, request.Model));
                lock (runs) runs.Add(run);
                var cost = EvalScenarioReport.Cost(run);
                var status = run.Invalid is not null ? "INVALID" : run.Succeeded ? "pass" : "FAIL";
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{status} {run.Scenario} {run.Configuration} #{run.Repeat}: {run.Turns.Count(static turn => turn.Passed)}/{run.Turns.Count} turns, ")
                    + string.Create(CultureInfo.InvariantCulture, $"{run.Requests.Count} requests, {run.Switches} switches, cost {cost:0.0000}") + (run.Invalid is { } why ? " — " + why : string.Empty));
                if (run.Invalid is null || attempt > retries) break;
            }
        }).ConfigureAwait(false);

        var ordered = runs.OrderBy(static run => run.Scenario, StringComparer.Ordinal).ThenBy(static run => run.Configuration, StringComparer.Ordinal).ThenBy(static run => run.Repeat).ThenBy(static run => run.Attempt).ToList();
        await File.WriteAllTextAsync(Path.Combine(output, "runs.json"), JsonSerializer.Serialize(ordered, EvalJson.Default.ListEvalScenarioRun)).ConfigureAwait(false);
        // A run kept is its last attempt; the attempts before it were invalid.
        var kept = ordered.GroupBy(static run => (run.Scenario, run.Configuration, run.Repeat)).Select(static group => group.Last()).ToList();
        var report = EvalScenarioReport.Markdown(kept, tiers, prices, ordered.Count(static run => run.Invalid is not null));
        if (options.TryGetValue("judge", out var judge))
        {
            report += await EvalScenarioJudge.RunAsync(altacli, judge, kept, tiers, output, timeout).ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), report, new UTF8Encoding(false)).ConfigureAwait(false);
        Console.WriteLine(report);
        return 0;
    }

    private static string[] Split(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FindRepository()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "src", "CodeAlta.slnx"))) return folder.FullName;
        }

        return Directory.GetCurrentDirectory();
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<EvalRun>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<EvalScenarioRun>))]
internal sealed partial class EvalJson : System.Text.Json.Serialization.JsonSerializerContext;
