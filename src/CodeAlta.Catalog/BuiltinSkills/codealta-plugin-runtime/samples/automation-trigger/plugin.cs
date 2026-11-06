using System.Text.Json;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;
// XenoAtom.CommandLine has a Command too: these two lines keep Command.Shell and name the other one.
using Command = CodeAlta.Plugins.Abstractions.Command;
using AltaCommand = XenoAtom.CommandLine.Command;

// A trigger of automations, the script it comes with, and the alta command that script calls: when a branch of a project
// moves, an automation runs `branch-watch/log` without a model, and the script writes a line with `alta branch-log add`.
[Plugin("automation-trigger", DisplayName = "Branch watch", Description = "Starts automations when a git branch of a project moves, with a script that logs it.")]
public sealed class BranchWatchPlugin : PluginBase, IAutomationTriggerSource
{
    private const string LogScript = """
        export const meta = { name: "branch-watch/log",description: "Writes the branch that moved in the log of Branch watch.", params: { event: { type: "object" } } };
        const line = `${args.event?.branch} moved to ${args.event?.head}`;
        await alta.branchLog.add({ text: line });
        return line;
        """;

    // CodeAlta asks for what happened since the cursor it keeps for the trigger: every few minutes.
    public PluginTriggerDelivery Delivery => PluginTriggerDelivery.Polled;

    public override IEnumerable<PluginAutomationTriggerContribution> GetAutomationTriggers()
    {
        yield return new PluginAutomationTriggerContribution
        {
            // The prefix is the plugin's own: a configuration file writes { type = "branch-watch/branch", branch = "main" }.
            Type = "branch-watch/branch",
            DisplayName = "Branch moved",
            Description = "When a branch of the project gets new commits: a commit, a pull, a merge.",
            Events = [new("moved", "Moved")],
            Fields = [new("branch", "Branch", PluginTriggerFieldKind.Text) { Placeholder = "main", Description = "The branch to watch; main when empty." }],
            Project = PluginTriggerProject.Required,
            Source = this,
            // The editor offers the trigger with this script, run without a model: nothing is sent to an agent.
            Script = new PluginTriggerScript("branch-watch/log"),
        };
    }

    public override IEnumerable<PluginScriptContribution> GetScripts()
    {
        yield return new PluginScriptContribution { Name = "branch-watch/log", Content = LogScript };
    }

    public override IEnumerable<PluginAltaCommandContribution> GetAltaCommands()
    {
        yield return new PluginAltaCommandContribution { Path = "branch-log", Description = "The log of Branch watch: add a line, or list them.", CreateCommandNode = CreateLog };
    }

    public ValueTask<PluginTriggerPage> ReadAsync(PluginTriggerContext context, string? cursor, CancellationToken cancellationToken)
    {
        var branch = context.Settings.Text("branch") is { Length: > 0 } named ? named : "main";
        if (context.ProjectPath is not { } project || ReadHead(project, branch) is not { } head)
            return ValueTask.FromResult(new PluginTriggerPage([], cursor) { Problem = $"The project has no branch {branch}." });
        // The first reading, and a branch that did not move, are no event: the cursor is the commit seen last.
        if (cursor is null || cursor == head) return ValueTask.FromResult(new PluginTriggerPage([], head));
        var moved = new PluginTriggerEvent($"{branch}@{head}", "moved", $"{branch} moved to {head[..Math.Min(7, head.Length)]}")
        {
            Fields = [new("Branch", branch), new("Commit", head)],
            // What the script receives as args.event, beside `on` and `id`.
            DataJson = JsonSerializer.Serialize(new { branch, head, previous = cursor }),
        };
        return ValueTask.FromResult(new PluginTriggerPage([moved], head));
    }

    // The commit of a branch, read from the files of the repository: a loose ref, else packed-refs; a worktree keeps its
    // branches in the folder of its repository.
    private static string? ReadHead(string project, string branch)
    {
        var dotGit = Path.Combine(project, ".git");
        var gitDir = Directory.Exists(dotGit) ? dotGit
            : File.Exists(dotGit) && File.ReadAllText(dotGit).Trim() is var link && link.StartsWith("gitdir:", StringComparison.Ordinal)
                ? Path.GetFullPath(Path.Combine(project, link["gitdir:".Length..].Trim())) : null;
        if (gitDir is null) return null;
        var common = Path.Combine(gitDir, "commondir") is var commonFile && File.Exists(commonFile)
            ? Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonFile).Trim())) : gitDir;
        var loose = Path.Combine(common, "refs", "heads", branch);
        if (File.Exists(loose)) return File.ReadAllText(loose).Trim();
        var packed = Path.Combine(common, "packed-refs");
        if (!File.Exists(packed)) return null;
        foreach (var line in File.ReadLines(packed))
        {
            var parts = line.Split(' ');
            if (parts.Length == 2 && parts[1] == "refs/heads/" + branch) return parts[0];
        }

        return null;
    }

    // `alta branch-log add --text <line>` and `alta branch-log list`, over a file of the plugin's data.
    private AltaCommand CreateLog(PluginAltaCommandContext context)
    {
        string? text = null;
        var add = new AltaCommand("add", "Adds a line to the log.") { new CommandUsage(), new HelpOption() };
        add.Add("text=", "The line.", value => text = value);
        add.Add(async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                context.Stderr.WriteLine(JsonSerializer.Serialize(new { type = "alta.branchlog.error", message = "--text is the line to add." }));
                return 2;
            }

            var lines = await Services.State.ReadJsonAsync<List<string>>(PluginStateScope.User, "log") ?? [];
            lines.Add($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm} {text.Trim()}");
            await Services.State.WriteJsonAsync(PluginStateScope.User, "log", lines.TakeLast(200).ToList());
            context.Stdout.WriteLine(JsonSerializer.Serialize(new { type = "alta.branchlog.added", count = lines.Count }));
            return 0;
        });

        var list = new AltaCommand("list", "Lists the lines of the log, the newest last.") { new CommandUsage(), new HelpOption() };
        list.Add(async (_, _) =>
        {
            foreach (var line in await Services.State.ReadJsonAsync<List<string>>(PluginStateScope.User, "log") ?? [])
                context.Stdout.WriteLine(JsonSerializer.Serialize(new { type = "alta.branchlog.line", text = line }));
            return 0;
        });

        var root = new AltaCommand("branch-log", "The log of Branch watch.") { new CommandUsage(), new HelpOption() };
        root.Add(add);
        root.Add(list);
        return root;
    }
}
