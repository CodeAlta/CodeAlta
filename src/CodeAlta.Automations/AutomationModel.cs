using System.Security.Cryptography;
using System.Text;
using CodeAlta.Agent;

namespace CodeAlta.Automations;

/// <summary>What starts an automation besides the user.</summary>
internal enum AutomationTriggerKind
{
    /// <summary>Every hour, or every few hours, at one minute.</summary>
    Hourly,

    /// <summary>Every day at one or more times.</summary>
    Daily,

    /// <summary>On chosen days of the week at one or more times.</summary>
    Weekly,

    /// <summary>When a five-field cron expression matches the local time.</summary>
    Cron,

    /// <summary>When an issue of the project's repository is opened.</summary>
    Issue,

    /// <summary>When a pull request of the project's repository is opened or receives commits.</summary>
    PullRequest,

    /// <summary>When an issue of the project's Jira is created or updated.</summary>
    Jira,

    /// <summary>When a command the automation keeps running ends with the exit code 0.</summary>
    Command,

    /// <summary>When a version of a Jira project is released, as the REST API of the site says.</summary>
    JiraVersion,

    /// <summary>When an issue starts to match a JQL query, or changes while it matches.</summary>
    JiraQuery,

    /// <summary>When a message is posted in a Slack channel, or mentions the app, as Socket Mode delivers it.</summary>
    Slack,

    /// <summary>When incident.io pushes an incident event to a hook of the server.</summary>
    IncidentIo,

    /// <summary>When any tool pushes a signed event to a hook of the server.</summary>
    Webhook,
}

/// <summary>How the sender of a webhook signs what it pushes.</summary>
internal static class AutomationSignatures
{
    /// <summary>Standard Webhooks (<c>webhook-id</c>, <c>webhook-timestamp</c>, <c>webhook-signature</c>), as incident.io and Svix send it.</summary>
    internal const string Standard = "standard";

    /// <summary>GitHub's <c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c>.</summary>
    internal const string GitHub = "github";

    /// <summary>An HMAC-SHA256 of the body, in hexadecimal or base64, in a header the trigger names.</summary>
    internal const string HmacSha256 = "hmac_sha256";

    /// <summary>The schemes, in the order the editor offers them.</summary>
    internal static readonly string[] All = [Standard, GitHub, HmacSha256];
}

/// <summary>Whose issues and pull requests start an automation.</summary>
internal enum AutomationAuthors
{
    /// <summary>
    /// The people of the repository: on GitHub its owner, the members of its organization and its collaborators,
    /// on GitLab its developers and above, on Azure DevOps anyone of the organization. What they write is not a
    /// stranger's instructions.
    /// </summary>
    Trusted,

    /// <summary>Anyone who can open an issue or a pull request.</summary>
    Anyone,
}

/// <summary>A time of day, to the minute, on the clock of the machine.</summary>
internal readonly record struct AutomationTime(int Hour, int Minute) : IComparable<AutomationTime>
{
    public int CompareTo(AutomationTime other) => (Hour * 60 + Minute).CompareTo(other.Hour * 60 + other.Minute);

    public override string ToString() => $"{Hour:00}:{Minute:00}";

    /// <summary>Reads <c>H:mm</c> or <c>HH:mm</c>.</summary>
    internal static bool TryParse(string? text, out AutomationTime time)
    {
        time = default;
        var parts = text?.Trim().Split(':');
        if (parts is not { Length: 2 } || parts[0].Length is < 1 or > 2 || parts[1].Length != 2
            || !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minute)
            || hour > 23 || minute > 59) return false;
        time = new(hour, minute);
        return true;
    }
}

/// <summary>One trigger of an automation. Which members count depends on <see cref="Kind"/>.</summary>
internal sealed record AutomationTrigger(AutomationTriggerKind Kind)
{
    /// <summary>The longest cron expression kept.</summary>
    internal const int MaximumExpressionLength = 128;

    /// <summary>The longest command kept.</summary>
    internal const int MaximumCommandLength = 2048;

    /// <summary>The longest folder of a command kept.</summary>
    internal const int MaximumFolderLength = 1024;

    /// <summary>The name of <see cref="AutomationTriggerKind.Command"/> in a configuration file, and of the runs it starts.</summary>
    internal const string CommandKindName = "command";

    /// <summary>The names of the days in a configuration file, Sunday first.</summary>
    internal static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    /// <summary><see cref="AutomationTriggerKind.Hourly"/>: the minute of the hour.</summary>
    public int Minute { get; init; }

    /// <summary><see cref="AutomationTriggerKind.Hourly"/>: every how many hours, counted from midnight (1 to 12).</summary>
    public int Every { get; init; } = 1;

    /// <summary><see cref="AutomationTriggerKind.Daily"/> and <see cref="AutomationTriggerKind.Weekly"/>: the times of day, in order.</summary>
    public IReadOnlyList<AutomationTime> At { get; init; } = [];

    /// <summary><see cref="AutomationTriggerKind.Weekly"/>: the days, in order from Sunday.</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];

    /// <summary><see cref="AutomationTriggerKind.Cron"/>: the expression as written.</summary>
    public string? Expression { get; init; }

    /// <summary>
    /// An event trigger: <c>opened</c>, or <c>updated</c> for a pull request that receives commits; <c>created</c> or
    /// <c>updated</c> for an issue of Jira.
    /// </summary>
    public string Event { get; init; } = "opened";

    /// <summary>An event trigger: whose items start the automation.</summary>
    public AutomationAuthors Authors { get; init; }

    /// <summary>
    /// <see cref="AutomationTriggerKind.Command"/>: the command line the automation keeps running, in the shell the
    /// <c>shell_command</c> tool uses. It is one line.
    /// </summary>
    public string? Command { get; init; }

    /// <summary>
    /// <see cref="AutomationTriggerKind.Command"/>: the folder the command runs in, <c>cwd</c> in a configuration
    /// file: a path relative to the folder of the project, or a full path. Null for the folder of the project, or
    /// the home folder of the user for an automation that runs in no project.
    /// </summary>
    public string? Folder { get; init; }

    /// <summary>The integration triggers: the site of Jira (<c>https://acme.atlassian.net</c>), or a <c>${var:NAME}</c> reference.</summary>
    public string? Site { get; init; }

    /// <summary>Jira: the email of the account of the API token, or a <c>${var:NAME}</c> reference.</summary>
    public string? Email { get; init; }

    /// <summary>Jira: the API token, a <c>${secret:NAME}</c> reference and never a value.</summary>
    public string? Token { get; init; }

    /// <summary><see cref="AutomationTriggerKind.JiraVersion"/>: the key of the Jira project whose versions are watched.</summary>
    public string? JiraProject { get; init; }

    /// <summary><see cref="AutomationTriggerKind.JiraQuery"/>: the JQL query.</summary>
    public string? Query { get; init; }

    /// <summary>Slack: the app-level token of Socket Mode (<c>xapp-</c>), a <c>${secret:NAME}</c> reference.</summary>
    public string? AppToken { get; init; }

    /// <summary>Slack: the bot token (<c>xoxb-</c>) that replies and reads, a <c>${secret:NAME}</c> reference.</summary>
    public string? BotToken { get; init; }

    /// <summary>Slack: the id of the channel watched (<c>C0123ABCD</c>), or a <c>${var:NAME}</c> reference.</summary>
    public string? Channel { get; init; }

    /// <summary>Slack: a text the message must contain, case ignored; null for any message.</summary>
    public string? Match { get; init; }

    /// <summary>incident.io: the API key that re-reads the incident and writes on its timeline, a <c>${secret:NAME}</c> reference.</summary>
    public string? ApiKey { get; init; }

    /// <summary>incident.io and webhooks: the id of the hook of the server the events are pushed to.</summary>
    public string? Hook { get; init; }

    /// <summary>incident.io and webhooks: the signing secret of the sender, a <c>${secret:NAME}</c> reference the server never holds.</summary>
    public string? Secret { get; init; }

    /// <summary>Webhooks: how the sender signs (<see cref="AutomationSignatures"/>).</summary>
    public string? Signature { get; init; }

    /// <summary>Webhooks with <see cref="AutomationSignatures.HmacSha256"/>: the header that holds the signature.</summary>
    public string? SignatureHeader { get; init; }

    /// <summary>Webhooks: the JSON pointers of the fields of the body that reach the prompt; nothing else of it does.</summary>
    public IReadOnlyList<string> Fields { get; init; } = [];

    /// <summary>Whether the answer of the run is written back where the event came from (a comment, a thread reply, a timeline item).</summary>
    public bool Reply { get; init; }

    /// <summary>
    /// Slack, on every message: whether a person's replies in a thread this automation answered start runs too. Off, they do
    /// not: the automation answered there, and the conversation is the people's.
    /// </summary>
    public bool Threads { get; init; }

    /// <summary>The longest JQL query kept.</summary>
    internal const int MaximumQueryLength = 2000;

    /// <summary>The most fields of a webhook that reach the prompt.</summary>
    internal const int MaximumFields = 16;

    /// <summary>Whether the trigger watches a tool other than Git through its own API or a hook of the server.</summary>
    public bool IsIntegration => Kind is AutomationTriggerKind.JiraVersion or AutomationTriggerKind.JiraQuery or AutomationTriggerKind.Slack
        or AutomationTriggerKind.IncidentIo or AutomationTriggerKind.Webhook;

    /// <summary>Whether the events of the trigger are pushed to a hook of the server.</summary>
    public bool IsHooked => Kind is AutomationTriggerKind.IncidentIo or AutomationTriggerKind.Webhook;

    /// <summary>Whether the trigger can write the answer of a run back to its source.</summary>
    public bool CanReply => Kind is AutomationTriggerKind.JiraQuery or AutomationTriggerKind.Slack or AutomationTriggerKind.IncidentIo;

    /// <summary>The events each integration trigger starts on, the default first.</summary>
    internal static IReadOnlyList<string> Events(AutomationTriggerKind kind) => kind switch
    {
        AutomationTriggerKind.JiraVersion => ["released"],
        AutomationTriggerKind.JiraQuery => ["matches", "updated"],
        AutomationTriggerKind.Slack => ["message", "mention"],
        AutomationTriggerKind.IncidentIo => ["declared", "updated"],
        AutomationTriggerKind.Webhook => ["received"],
        AutomationTriggerKind.Jira => ["created", "updated"],
        _ => ["opened", "updated"],
    };

    /// <summary>The secret references the trigger uses: its credentials.</summary>
    public IEnumerable<string> Credentials => new[] { Token, AppToken, BotToken, ApiKey, Secret }.OfType<string>();

    /// <summary>Whether the trigger is a schedule, as opposed to an event of the repository, of another tool, or a command.</summary>
    public bool IsSchedule => !IsEvent && !IsCommand && !IsIntegration;

    /// <summary>Whether the trigger is an event of the repository of the project, or of its tracker.</summary>
    public bool IsEvent => Kind is AutomationTriggerKind.Issue or AutomationTriggerKind.PullRequest or AutomationTriggerKind.Jira;

    /// <summary>Whether the trigger is a command the automation keeps running.</summary>
    public bool IsCommand => Kind == AutomationTriggerKind.Command;

    /// <summary>Whether the trigger watches a tracker a plugin gives, as opposed to the hosted repository of the project.</summary>
    public bool IsTracker => Kind == AutomationTriggerKind.Jira;

    /// <summary>The name of the kind in a configuration file.</summary>
    public string KindName => KindNames[(int)Kind];

    private static readonly string[] KindNames = ["hourly", "daily", "weekly", "cron", "issue", "pull_request", "jira", CommandKindName,
        "jira_version", "jira_query", "slack", "incident_io", "webhook"];

    /// <summary>Reads the name of a kind from a configuration file.</summary>
    internal static bool TryParseKind(string? name, out AutomationTriggerKind kind)
    {
        var index = Array.IndexOf(KindNames, name?.Trim().ToLowerInvariant());
        kind = index < 0 ? default : (AutomationTriggerKind)index;
        return index >= 0;
    }

    /// <summary>A value that identifies this trigger among those of its automation, stable while it is not edited.</summary>
    public string Key => Kind switch
    {
        AutomationTriggerKind.Hourly => $"hourly:{Minute}/{Every}",
        AutomationTriggerKind.Daily => "daily:" + string.Join(',', At),
        AutomationTriggerKind.Weekly => "weekly:" + string.Join(',', Days.Select(static day => DayNames[(int)day])) + "@" + string.Join(',', At),
        AutomationTriggerKind.Cron => "cron:" + Expression,
        // Neither part holds a tabulation: two commands that differ, or that run in two folders, are two triggers.
        AutomationTriggerKind.Command => "command:" + Folder + "\t" + Command,
        // What it watches, not how it signs in: a new token keeps what the trigger has seen.
        AutomationTriggerKind.JiraVersion => $"jira_version:{Site}\t{JiraProject}",
        AutomationTriggerKind.JiraQuery => $"jira_query:{Event}:{Site}\t{Query}",
        AutomationTriggerKind.Slack => $"slack:{Event}:{Channel}\t{Match}",
        AutomationTriggerKind.IncidentIo => $"incident_io:{Event}:{Hook}",
        AutomationTriggerKind.Webhook => $"webhook:{Hook}",
        _ => $"{KindName}:{Event}:{(Authors == AutomationAuthors.Anyone ? "anyone" : "trusted")}",
    };

    /// <summary>
    /// What the fingerprint of an automation takes of the trigger: its <see cref="Key"/>, and for an integration trigger
    /// also how it signs in, what it binds and whether it replies, so that a change of any of them asks again.
    /// </summary>
    public string FingerprintKey => !IsIntegration ? Key
        : string.Join('\t', Key, Email ?? string.Empty, string.Join(',', Credentials), Signature ?? string.Empty, SignatureHeader ?? string.Empty,
            string.Join(',', Fields), Reply ? "reply" : string.Empty) + (Threads ? "\tthreads" : string.Empty);
}

/// <summary>
/// What the runs of an automation may do without a person (<c>doc/fleet/orchestration.md</c>, "Autonomy and approvals"):
/// the tools of other sources (MCP servers, plugins) they call by themselves, how often, and what happens to the rest,
/// which waits for an approval. The machine enforces it on every tool call; the host's own tools keep their guards.
/// </summary>
internal sealed record AutomationAutonomy
{
    /// <summary>Approvals go to the owner of the automation: its person, or the people of the team that owns it.</summary>
    internal const string OwnerApprovers = "owner";

    /// <summary>Approvals go to the person on call of the team that owns the automation, by its rotation on its server.</summary>
    internal const string OnCallApprovers = "oncall";

    /// <summary>A run whose approval waits too long stops.</summary>
    internal const string StopOnTimeout = "stop";

    /// <summary>A run whose approval waits too long keeps waiting.</summary>
    internal const string WaitOnTimeout = "wait";

    /// <summary>The most allowed tools.</summary>
    internal const int MaximumTools = 32;

    /// <summary>The longest approval timeout, in minutes: a week.</summary>
    internal const int MaximumTimeout = 7 * 24 * 60;

    /// <summary>The most calls of the allowed tools in an hour, the circuit breaker.</summary>
    internal const int MaximumCallsPerHour = 1000;

    /// <summary>
    /// The tools of the host: their calls keep the guards of the host (commands and edits follow the permission mode,
    /// reads run), so an automation neither lists nor holds them.
    /// </summary>
    internal static readonly string[] HostTools =
    [
        "read_file", "view_image", "list_dir", "grep", "webget", "shell_command", "write_file", "replace_in_file", "delete_file_or_dir", "rename_file_or_dir",
        "apply_patch", "request_user_input", "alta", "exec", "wait", "web_search", "web_fetch", "tool_search",
    ];

    /// <summary>Gets the tools of other sources the runs call without a person, by their exact names.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>Gets the circuit breaker: the most calls of the allowed tools in an hour, all runs together; null for no limit.</summary>
    public int? MaxCallsPerHour { get; init; }

    /// <summary>Gets how many minutes an approval waits before <see cref="OnTimeout"/>; null to wait as long as it takes.</summary>
    public int? ApprovalTimeout { get; init; }

    /// <summary>Gets what a run does when an approval waits too long: <see cref="StopOnTimeout"/> or <see cref="WaitOnTimeout"/>.</summary>
    public string OnTimeout { get; init; } = StopOnTimeout;

    /// <summary>Gets who answers the approvals: <see cref="OwnerApprovers"/> or <see cref="OnCallApprovers"/>.</summary>
    public string Approvers { get; init; } = OwnerApprovers;

    /// <summary>
    /// Gets what the allowed tools that say more than their name must be and stay within: their pins, bounds and
    /// preconditions. A tool of <see cref="AllowedTools"/> missing here runs as allowed, under the circuit breaker only.
    /// </summary>
    public IReadOnlyList<AutomationAllowedTool> Tools { get; init; } = [];

    /// <summary>A text that changes with every part, for the fingerprint of the automation.</summary>
    internal string Key => string.Join('\t', string.Join(',', AllowedTools), MaxCallsPerHour?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        ApprovalTimeout?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, OnTimeout, Approvers)
        + (Tools.Count == 0 ? string.Empty : "\t" + string.Join('\u001b', Tools.Select(static tool => tool.Key)));

    /// <summary>What an allowed tool must be and stay within; null for a tool allowed by its name alone.</summary>
    internal AutomationAllowedTool? Tool(string name) => Tools.FirstOrDefault(tool => tool.Name == name);

    /// <summary>Whether two read the same: the same tools in the same order and the same limits.</summary>
    public bool Equals(AutomationAutonomy? other) => other is not null && Key == other.Key;

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key);

    /// <summary>Whether a tool is one the runs call without a person.</summary>
    internal bool Allows(string tool) => AllowedTools.Contains(tool, StringComparer.Ordinal);

    /// <summary>What is wrong with it, or null.</summary>
    internal string? Validate()
    {
        if (AllowedTools.Count > MaximumTools) return $"An automation allows at most {MaximumTools} tools.";
        foreach (var tool in AllowedTools)
        {
            if (tool.Length is 0 or > 128 || !tool.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':' or '/'))
                return $"'{AutomationText.Line(tool, 60)}' is not the name of a tool: letters, digits, _ - . : / only, without wildcards.";
            if (HostTools.Contains(tool, StringComparer.Ordinal))
                return $"{tool} is a tool of CodeAlta: its commands and edits follow the permission mode of the run, and it is not allowed by name.";
        }

        if (AllowedTools.Distinct(StringComparer.Ordinal).Count() != AllowedTools.Count) return "A tool is allowed twice.";
        foreach (var tool in Tools)
        {
            if (!Allows(tool.Name)) return $"{tool.Name} has a pin, bounds or preconditions but is not allowed.";
            if (tool.Validate(AllowedTools) is { } problem) return problem;
            // A precondition reads the target: it has none of its own, so that checks never call each other in a loop.
            if (tool.Preconditions.FirstOrDefault(precondition => Tool(precondition.Tool) is { Preconditions.Count: > 0 }) is { } loop)
                return $"{loop.Tool}, a precondition of {tool.Name}, has preconditions of its own.";
        }

        if (Tools.Select(static tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != Tools.Count) return "A tool is described twice.";
        if (MaxCallsPerHour is < 1 or > MaximumCallsPerHour) return $"max_calls_per_hour is 1 to {MaximumCallsPerHour}.";
        if (ApprovalTimeout is < 1 or > MaximumTimeout) return $"approval_timeout is 1 to {MaximumTimeout} minutes.";
        if (OnTimeout is not (StopOnTimeout or WaitOnTimeout)) return "on_timeout is stop or wait.";
        if (Approvers is not (OwnerApprovers or OnCallApprovers))
            return "approvers is owner (its person, or the team that owns it) or oncall (the person on call of the team that owns it, by its rotation on its server).";
        return null;
    }
}

/// <summary>The provider, model and effort an automation asks for: each part is optional.</summary>
internal readonly record struct AutomationModelRef(string? Provider, string? Model, AgentReasoningEffort? Effort)
{
    /// <summary>Whether nothing is asked for.</summary>
    public bool IsEmpty => Provider is null && Model is null && Effort is null;

    /// <summary>Writes <c>provider[:model][@effort]</c>; null when nothing is asked for.</summary>
    public string? Format()
    {
        if (Provider is null) return null;
        var text = Model is null ? Provider : Provider + ":" + Model;
        return Effort is { } effort ? text + "@" + effort.ToString().ToLowerInvariant() : text;
    }

    /// <summary>Reads <c>provider[:model][@effort]</c>. A blank text asks for nothing.</summary>
    internal static bool TryParse(string? text, out AutomationModelRef reference, out string? error)
    {
        (reference, error) = (default, null);
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value)) return true;
        AgentReasoningEffort? effort = null;
        var at = value.LastIndexOf('@');
        if (at >= 0)
        {
            var suffix = value[(at + 1)..].Trim();
            if (Enum.TryParse<AgentReasoningEffort>(suffix, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(suffix, out _))
            {
                (effort, value) = (parsed, value[..at].Trim());
            }
            else if (!value[..at].Contains(':', StringComparison.Ordinal))
            {
                error = $"'{suffix}' is not a reasoning effort.";
                return false;
            }

            // Otherwise the mark belongs to the name of the model, as in claude-sonnet-4@20250514.
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        var provider = (colon < 0 ? value : value[..colon]).Trim();
        var model = colon < 0 ? null : value[(colon + 1)..].Trim();
        if (provider.Length == 0 || model is { Length: 0 } || provider.Length > 256 || model is { Length: > 256 })
        {
            error = "A model is written provider[:model][@effort].";
            return false;
        }

        reference = new(provider, model, effort);
        return true;
    }
}

/// <summary>An automation as its configuration file defines it.</summary>
/// <param name="Id">The identifier: a GUID, which is the key of its table.</param>
/// <param name="Name">The name shown to the user, which also names the sessions it starts.</param>
internal sealed record AutomationDefinition(string Id, string Name)
{
    /// <summary>The longest name kept.</summary>
    internal const int MaximumNameLength = 120;

    /// <summary>The longest prompt kept, the limit of a prompt typed in a session.</summary>
    internal const int MaximumPromptLength = 32768;

    /// <summary>The most triggers one automation has.</summary>
    internal const int MaximumTriggers = 8;

    /// <summary>Whether its triggers start it. A disabled automation can still be run by hand.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The longest arguments of a script kept, as JSON.</summary>
    internal const int MaximumScriptArgumentsLength = 32768;

    /// <summary>What is sent to the session the automation starts; empty for an automation that runs a script.</summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>The script of the library the automation runs in place of a prompt; null when it sends its prompt.</summary>
    public string? Script { get; init; }

    /// <summary>The arguments of its script, a JSON object; null for none.</summary>
    public string? ScriptArguments { get; init; }

    /// <summary>In the configuration of the user only: the folder of the project it runs in. Without one it runs as a chat.</summary>
    public string? Project { get; init; }

    /// <summary>The longest name of a machine kept.</summary>
    internal const int MaximumMachineLength = 128;

    /// <summary>
    /// In the configuration of the user only: the paired machine (its device id) that runs the automation in place of
    /// this one, which then keeps its definition and copies it there. Null runs it here.
    /// </summary>
    public string? RunsOn { get; init; }

    /// <summary>The provider, model and effort asked for.</summary>
    public AutomationModelRef Model { get; init; }

    /// <summary>The agent prompt asked for; null for the default one.</summary>
    public string? Agent { get; init; }

    /// <summary>The permission mode its sessions start in, one its provider offers; null for the one of the provider.</summary>
    public string? PermissionMode { get; init; }

    /// <summary>Whether the user reviews the changes of its sessions before an agent commits them; null follows the setting.</summary>
    public bool? ReviewBeforeCommit { get; init; }

    /// <summary>The sandbox its sessions run in: a profile, or <c>off</c>; null for what the settings give.</summary>
    public string? Sandbox { get; init; }

    /// <summary>
    /// The environment of the fleet its runs use (<c>doc/fleet/secrets.md</c>, environments): a run starts only once the
    /// machine admitted it (its rules held, its approval given), and its sessions get the values of that environment; null for none.
    /// </summary>
    public string? Environment { get; init; }

    /// <summary>The options of its sessions, such as whether they can start sessions of their own; null for the defaults.</summary>
    public AgentSessionOptions? SessionOptions { get; init; }

    /// <summary>What its runs may do without a person, enforced on the machine; null for an automation that runs as before.</summary>
    public AutomationAutonomy? Autonomy { get; init; }

    /// <summary>
    /// Whether what was missed while the application was not running is run when it starts: once for the times
    /// of a schedule, and for each issue or pull request an event trigger would have seen.
    /// </summary>
    public bool CatchUp { get; init; }

    /// <summary>The triggers. None: the automation is run by hand.</summary>
    public IReadOnlyList<AutomationTrigger> Triggers { get; init; } = [];

    /// <summary>
    /// A value that changes with what the automation runs and with when it runs: its name, prompt or script, model, agent
    /// prompt, permission mode, review, sandbox, project and triggers, the command and the folder of a command
    /// trigger included. It does not change with <see cref="Enabled"/>.
    /// </summary>
    internal string Fingerprint()
    {
        var text = string.Join('\n', Name, Model.Format() ?? string.Empty, Agent ?? string.Empty, CatchUp ? "1" : "0", Project ?? string.Empty,
            string.Join('|', Triggers.Select(static trigger => trigger.FingerprintKey)), Prompt);
        // An automation without them keeps the fingerprint it was allowed with.
        if (PermissionMode is not null || ReviewBeforeCommit is not null || Sandbox is not null)
            text += "\n" + string.Join('\n', PermissionMode ?? string.Empty, ReviewBeforeCommit switch { true => "1", false => "0", null => string.Empty }, Sandbox ?? string.Empty);
        // Nor without options of their own.
        if (AgentSessionOptions.Stored(SessionOptions) is { } options) text += "\noptions\n" + (options.SubSessions ? "1" : "0");
        if (Script is not null) text += "\nscript\n" + Script + "\n" + (ScriptArguments ?? string.Empty);
        // Nor without a machine of their own.
        if (RunsOn is not null) text += "\nruns_on\n" + RunsOn;
        // Nor without an environment.
        if (Environment is not null) text += "\nenvironment\n" + Environment;
        // Nor without autonomy.
        if (Autonomy is not null) text += "\nautonomy\n" + Autonomy.Key;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Whether the text is a GUID in the form a table key takes: 32 digits in five groups, lower case.</summary>
    internal static bool IsId(string? text)
        => text is { Length: 36 } && Guid.TryParseExact(text, "D", out _) && text == text.ToLowerInvariant();

    /// <summary>Creates an identifier for a new automation.</summary>
    internal static string NewId() => Guid.CreateVersion7().ToString("D");
}

/// <summary>The configuration file an automation is defined in.</summary>
/// <param name="FilePath">The full path of the file.</param>
/// <param name="ProjectId">The project whose configuration it is; null for the configuration of the user.</param>
/// <param name="ProjectPath">The folder of that project.</param>
internal sealed record AutomationSource(string FilePath, string? ProjectId, string? ProjectPath)
{
    /// <summary>Whether this is the configuration of the user, or the file of the automations other devices assigned to this machine.</summary>
    public bool IsGlobal => ProjectId is null;

    /// <summary>
    /// Whether this is the file of the automations other devices of the user assigned to this machine (a node), which
    /// only the host writes, through what checks those devices; false for the configuration of the user or of a project.
    /// </summary>
    public bool IsAssigned { get; init; }
}

/// <summary>An automation with where it is defined and where it runs.</summary>
/// <param name="Definition">The definition read from the file.</param>
/// <param name="Source">The file it was read from.</param>
/// <param name="ProjectId">The project it runs in; null when it runs as a chat, or when <see cref="Problem"/> says its project is unknown.</param>
/// <param name="ProjectPath">The folder of that project.</param>
/// <param name="Problem">Why it cannot run as defined; null when it can.</param>
internal sealed record AutomationEntry(AutomationDefinition Definition, AutomationSource Source, string? ProjectId, string? ProjectPath, string? Problem)
{
    /// <summary>The identifier of the automation.</summary>
    public string Id => Definition.Id;
}

/// <summary>A table under <c>[automations]</c> that is not an automation, with the reason.</summary>
/// <param name="Source">The file it is in.</param>
/// <param name="Key">The key of the table.</param>
/// <param name="Message">What is wrong with it.</param>
internal sealed record AutomationFault(AutomationSource Source, string Key, string Message);
