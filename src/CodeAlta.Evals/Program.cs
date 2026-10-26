using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Evals;

// The evaluation of Code Mode (section 12 of the plan): every task, per model and per mode, several times, through
// altacli in an ephemeral state root. Run by hand, never in CI: it spends real tokens. The report goes to
// tmp/evals/<date>/ of the repository: report.md, runs.json, and the JSONL of each run.
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
          --list            list the tasks and exit
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
            return 0;
        }

        if (!options.TryGetValue("models", out var modelList)) { Console.Error.WriteLine(Usage); return 2; }
        var repository = FindRepository();
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Debug" : "Release";
        var altacli = options.GetValueOrDefault("altacli") ?? Path.Combine(repository, "src", "CodeAlta.Cli", "bin", configuration, "net10.0", OperatingSystem.IsWindows() ? "altacli.exe" : "altacli");
        if (!File.Exists(altacli)) { Console.Error.WriteLine($"{altacli} does not exist: build the solution in {configuration} first, or name one with --altacli."); return 2; }
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
internal sealed partial class EvalJson : System.Text.Json.Serialization.JsonSerializerContext;
