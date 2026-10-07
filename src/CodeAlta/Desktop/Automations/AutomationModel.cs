using System.Security.Cryptography;
using System.Text;
using CodeAlta.Agent;

namespace CodeAlta.Desktop.Automations;

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

    /// <summary>Whether the trigger is a schedule, as opposed to an event of the repository.</summary>
    public bool IsSchedule => Kind is not (AutomationTriggerKind.Issue or AutomationTriggerKind.PullRequest or AutomationTriggerKind.Jira);

    /// <summary>Whether the trigger watches a tracker a plugin gives, as opposed to the hosted repository of the project.</summary>
    public bool IsTracker => Kind == AutomationTriggerKind.Jira;

    /// <summary>The name of the kind in a configuration file.</summary>
    public string KindName => KindNames[(int)Kind];

    private static readonly string[] KindNames = ["hourly", "daily", "weekly", "cron", "issue", "pull_request", "jira"];

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
        _ => $"{KindName}:{Event}:{(Authors == AutomationAuthors.Anyone ? "anyone" : "trusted")}",
    };
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

    /// <summary>What is sent to the session the automation starts.</summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>In the configuration of the user only: the folder of the project it runs in. Without one it runs as a chat.</summary>
    public string? Project { get; init; }

    /// <summary>The provider, model and effort asked for.</summary>
    public AutomationModelRef Model { get; init; }

    /// <summary>The agent prompt asked for; null for the default one.</summary>
    public string? Agent { get; init; }

    /// <summary>
    /// Whether what was missed while the application was not running is run when it starts: once for the times
    /// of a schedule, and for each issue or pull request an event trigger would have seen.
    /// </summary>
    public bool CatchUp { get; init; }

    /// <summary>The triggers. None: the automation is run by hand.</summary>
    public IReadOnlyList<AutomationTrigger> Triggers { get; init; } = [];

    /// <summary>
    /// A value that changes with what the automation runs and with when it runs: its name, prompt, model, agent
    /// prompt, project and triggers. It does not change with <see cref="Enabled"/>.
    /// </summary>
    internal string Fingerprint()
    {
        var text = string.Join('\n', Name, Model.Format() ?? string.Empty, Agent ?? string.Empty, CatchUp ? "1" : "0", Project ?? string.Empty,
            string.Join('|', Triggers.Select(static trigger => trigger.Key)), Prompt);
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
    /// <summary>Whether this is the configuration of the user.</summary>
    public bool IsGlobal => ProjectId is null;
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
