using System.Globalization;
using System.Text.Json;
using CodeAlta.Automations;
using CodeAlta.Hosting;

namespace CodeAlta.Node;

/// <summary>The options of <c>altanode automations</c>.</summary>
internal sealed class NodeAutomationCommandOptions
{
    /// <summary>Gets the profile options.</summary>
    public NodeProfileOptions Profile { get; } = new();

    /// <summary>Gets the words: the subcommand and its arguments.</summary>
    public List<string> Words { get; } = [];
}

/// <summary>
/// The automations other devices gave the node of a profile, for its owner on that machine: <c>altanode automations list</c>
/// (what was given, by whom, through which way), <c>runs</c> (their latest runs) and <c>policy</c> (whether the node runs
/// automations, and with which credentials). They read and write the files of the profile: a node that runs reads the
/// policy again before each run, so a change applies at once.
/// </summary>
/// <param name="output">Standard output.</param>
/// <param name="error">Standard error.</param>
internal sealed class NodeAutomationCommands(TextWriter output, TextWriter error)
{
    private const int RunsShown = 20;

    public async ValueTask<int> RunAsync(NodeAutomationCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Profile.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        var catalog = new Catalog.CatalogOptions { GlobalRoot = profile.GlobalRoot, StateRoot = profile.StateRoot };
        var (command, arguments) = options.Words is [var first, .. var rest] ? (first, rest) : ("list", []);
        switch (command)
        {
            case "list" when arguments.Count == 0:
                return await ListAsync(catalog.FleetRoot).ConfigureAwait(false);
            case "runs" when arguments.Count <= 1:
                return await RunsAsync(catalog, arguments.Count == 1 ? arguments[0] : null).ConfigureAwait(false);
            case "policy":
                return await PolicyAsync(catalog.FleetRoot, arguments).ConfigureAwait(false);
            case "list" or "runs":
                return await UsageAsync(command == "list" ? "automations list takes no argument." : "automations runs takes at most the id of an automation.").ConfigureAwait(false);
            default:
                return await UsageAsync($"'{command}' is not a command of automations: list, runs or policy.").ConfigureAwait(false);
        }
    }

    private async Task<int> ListAsync(string fleetRoot)
    {
        var assignments = ReadAssignments(fleetRoot);
        var names = ReadNames(fleetRoot);
        if (assignments.Count == 0)
        {
            await output.WriteLineAsync("No device gave this machine an automation. CodeAlta Desktop gives one with Runs on.").ConfigureAwait(false);
            return NodeExitCodes.Success;
        }

        await output.WriteLineAsync($"{"ID",-36}  {"NAME",-28}  {"GIVEN BY",-26}  {"THROUGH",-8}  {"VERSION",-14}  STATE").ConfigureAwait(false);
        foreach (var (id, assignment) in assignments.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var state = assignment.Removed ? "taken away" : assignment.Signed is null ? "unsigned: does not run" : "assigned";
            await output.WriteLineAsync($"{Fit(id, 36)}  {Fit(names.GetValueOrDefault(id) ?? "-", 28)}  {Fit(assignment.DeviceName, 26)}  " +
                $"{(assignment.Via is null ? "paired" : "server"),-8}  {assignment.Number.ToString(CultureInfo.InvariantCulture),-14}  {state}").ConfigureAwait(false);
        }

        return NodeExitCodes.Success;
    }

    private async Task<int> RunsAsync(Catalog.CatalogOptions catalog, string? id)
    {
        // The runs of what devices gave: those of the automations of the profile's own configuration are listed by CodeAlta.
        var given = ReadAssignments(catalog.FleetRoot).Keys.ToHashSet(StringComparer.Ordinal);
        var runs = AutomationStateStore.ReadRuns(Path.Combine(catalog.StateRoot, "automations.json"), id, AutomationStateStore.MaximumRuns)
            .Where(run => given.Contains(run.AutomationId)).Take(RunsShown).ToArray();
        if (runs.Length == 0)
        {
            await output.WriteLineAsync(id is null ? "No automation given to this machine has run yet." : $"The automation {id} has not run on this machine.").ConfigureAwait(false);
            return NodeExitCodes.Success;
        }

        await output.WriteLineAsync($"{"STARTED",-20}  {"AUTOMATION",-28}  {"TRIGGER",-8}  {"STATUS",-11}  {"TOKENS",-13}  SESSION").ConfigureAwait(false);
        foreach (var run in runs)
        {
            var tokens = run.InputTokens is null && run.OutputTokens is null ? "-" : $"{run.InputTokens ?? 0}/{run.OutputTokens ?? 0}";
            await output.WriteLineAsync($"{run.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),-20}  {Fit(run.Name ?? run.AutomationId, 28)}  " +
                $"{Fit(run.Trigger, 8)}  {run.Status,-11}  {tokens,-13}  {run.SessionId ?? "-"}{(run.Message is { } message ? "  " + message : "")}").ConfigureAwait(false);
        }

        return NodeExitCodes.Success;
    }

    private async Task<int> PolicyAsync(string fleetRoot, IReadOnlyList<string> arguments)
    {
        var policy = NodeAutomationPolicy.Load(fleetRoot);
        switch (arguments)
        {
            case []:
                break;
            case ["on"]:
                policy = policy with { Enabled = true };
                break;
            case ["off"]:
                policy = policy with { Enabled = false };
                break;
            case ["credentials", var kind] when kind is NodeAutomationPolicy.AnyCredentials or NodeAutomationPolicy.AccountCredentials:
                policy = policy with { Credentials = kind };
                break;
            default:
                return await UsageAsync("automations policy takes nothing, on, off, or credentials any|account.").ConfigureAwait(false);
        }

        if (arguments.Count > 0)
        {
            try
            {
                // What the owner sets replaces a file that could not be read.
                policy = policy with { Problem = null };
                NodeAutomationPolicy.Save(fleetRoot, policy);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await error.WriteLineAsync("altanode automations: the policy could not be written: " + exception.Message).ConfigureAwait(false);
                return NodeExitCodes.Failure;
            }
        }

        await output.WriteLineAsync(policy.Problem is { } problem
            ? $"This machine runs no automations: its policy cannot be read ({problem}). Set it again with: altanode automations policy on|off."
            : policy.Enabled ? "This machine runs the automations given to it." : "This machine runs no automations: they are off.").ConfigureAwait(false);
        await output.WriteLineAsync(policy.Credentials == NodeAutomationPolicy.AccountCredentials
            ? "Credentials: accounts only (API keys and local models, never a personal plan)."
            : "Credentials: any the machine has, its personal plans included.").ConfigureAwait(false);
        return NodeExitCodes.Success;
    }

    private static Dictionary<string, NodeAutomationAssignment> ReadAssignments(string fleetRoot)
    {
        try
        {
            var path = Path.Combine(fleetRoot, NodeAutomations.AssignmentsFileName);
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new(JsonSerializer.Deserialize(stream, NodeAutomationJsonContext.Default.DictionaryStringNodeAutomationAssignment) ?? [], StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    // The names of the automations given to this machine, from the file the node keeps them in.
    private static Dictionary<string, string> ReadNames(string fleetRoot)
    {
        try
        {
            var path = Path.Combine(fleetRoot, NodeAutomations.AssignedFileName);
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            var (definitions, _) = AutomationConfig.Read(File.ReadAllText(path), new AutomationSource(path, null, null) { IsAssigned = true });
            return definitions.ToDictionary(static definition => definition.Id, static definition => definition.Name, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private static string Fit(string text, int width) => text.Length <= width ? text.PadRight(width) : text[..(width - 1)] + "…";

    private async Task<int> UsageAsync(string message)
    {
        await error.WriteLineAsync("altanode automations: " + message).ConfigureAwait(false);
        await error.WriteLineAsync("Run altanode automations --help for its commands.").ConfigureAwait(false);
        return NodeExitCodes.InvalidUsage;
    }
}
