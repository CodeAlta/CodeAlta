using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.Plugin.Jira;

/// <summary>The <c>alta jira</c> commands: the Jira of the project, for agents and for the user.</summary>
internal static class JiraCommands
{
    private const int Usage = 2;
    private const int Failure = 1;
    private const int MaximumBody = 32 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Command Create(JiraPlugin plugin, PluginAltaCommandContext context)
    {
        var command = Group("jira", "Set up the Jira of the project and change its issues: sign in, create, comment, move, assign.");
        command.Add(Status(plugin, context));
        command.Add(Login(plugin, context));
        command.Add(CreateIssue(plugin, context));
        command.Add(Comment(plugin, context));
        command.Add(Transition(plugin, context));
        command.Add(Assign(plugin, context));
        Help(command,
            "The project names its Jira in `.alta/config.toml`: `[plugins.jira]` with `site` (example.atlassian.net) and `project` (the key, such as ALTA).",
            "Jira is reached through the Atlassian CLI, which CodeAlta downloads and signs in when it is first needed.",
            "To list and read the issues, use `alta issue list` and `alta issue show`: they answer for Jira as for every other tracker.",
            "An issue is named by its key: `ALTA-12`. The number alone names an issue of the project.",
            "Examples: `alta jira status`; `alta jira create --type Bug --summary \"The list loses its place\" --stdin`; `alta jira comment ALTA-12 --stdin`; "
            + "`alta jira transition ALTA-12 \"In Progress\"`; `alta jira assign ALTA-12 @me`.");
        return command;
    }

    private static Command Status(JiraPlugin plugin, PluginAltaCommandContext context)
    {
        var command = Leaf("status", "Say whether the Jira of the project is ready: the CLI, the account, the site and the project.");
        command.Add(async (_, _) =>
        {
            if (Settings(context) is not { } settings) return NotConfigured(context);
            plugin.Invalidate();
            var (problem, needsSignIn) = await plugin.EnsureReadyAsync(settings, context.CancellationToken).ConfigureAwait(false);
            var account = problem is null ? await plugin.Client.GetAccountAsync(context.CancellationToken).ConfigureAwait(false) : null;
            Write(context, new { type = "alta.jira.status", version = 1, correlationId = context.CorrelationId, ready = problem is null, site = settings.Site, project = settings.Project,
                account = account?.Email, signIn = account?.Kind, problem, needsSignIn = needsSignIn ? true : (bool?)null });
            return 0;
        });
        return command;
    }

    private static Command Login(JiraPlugin plugin, PluginAltaCommandContext context)
    {
        var command = Leaf("login", "Sign in to the Jira of the project in the browser of the user. It waits until the user finished, five minutes at most.");
        Help(command, "With an API token in the environment (JIRA_API_TOKEN and JIRA_EMAIL, or the variable `token_env` names) no browser is needed: `alta jira status` signs in by itself.");
        command.Add(async (_, _) =>
        {
            if (Settings(context) is not { } settings) return NotConfigured(context);
            if (await plugin.SignInWithBrowserAsync(settings, context.CancellationToken).ConfigureAwait(false) is { } problem) return Fail(context, "jira.signInFailed", problem);
            Write(context, new { type = "alta.jira.signedIn", version = 1, correlationId = context.CorrelationId, site = settings.Site });
            return 0;
        });
        return command;
    }

    private static Command CreateIssue(JiraPlugin plugin, PluginAltaCommandContext context)
    {
        string? summary = null, type = null, description = null;
        var labels = new List<string>();
        var stdin = false;
        var command = Leaf("create", "Create an issue in the Jira project. Only when the user asks for it.");
        command.Add("summary=", "The title of the issue, in one line. Required.", value => summary = value);
        command.Add("type=", "The type of the issue: Task (default), Bug, Story, Epic, or another type of the project.", value => type = value);
        command.Add("description=", "The description, as plain text. Prefer --stdin for several lines.", value => description = value);
        command.Add("stdin", "Read the description from stdin.", value => stdin = value is not null);
        command.Add("label=", "A label; repeat the option for several.", value => { if (!string.IsNullOrWhiteSpace(value)) labels.Add(value.Trim()); });
        command.Add(async (_, _) =>
        {
            if (Settings(context) is not { } settings) return NotConfigured(context);
            if (string.IsNullOrWhiteSpace(summary) || summary.Length > 255 || summary.Contains('\n', StringComparison.Ordinal)) return Fail(context, "usage.invalidSummary", "--summary is required: one line of at most 255 characters.", Usage);
            var body = stdin ? await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false) : description;
            if (body is { Length: > MaximumBody }) return Fail(context, "usage.descriptionTooLong", "The description is too long.", Usage);
            if (await Ready(plugin, context, settings).ConfigureAwait(false) is { } refused) return refused;
            var created = await plugin.Client.CreateAsync(settings.Project, string.IsNullOrWhiteSpace(type) ? "Task" : type.Trim(), summary.Trim(), body?.Trim(), labels, context.CancellationToken).ConfigureAwait(false);
            if (created.Value is not { } key) return Fail(context, "jira.failed", created.Problem ?? "Jira did not create the issue.");
            Write(context, new { type = "alta.jira.created", version = 1, correlationId = context.CorrelationId, key, url = settings.BrowseUrl(key), project = settings.Project });
            return 0;
        });
        return command;
    }

    private static Command Comment(JiraPlugin plugin, PluginAltaCommandContext context)
    {
        string? key = null, body = null;
        var stdin = false;
        var command = Leaf("comment", "Add a comment to an issue. Only when the user asks for it.");
        command.Add("<key>", "The key of the issue.", value => key = value);
        command.Add("body=", "The comment, as plain text. Prefer --stdin for several lines.", value => body = value);
        command.Add("stdin", "Read the comment from stdin.", value => stdin = value is not null);
        command.Add((_, _) => Change(plugin, context, key, "alta.jira.commented", async (issue, token) =>
        {
            var text = (stdin ? await context.Stdin.ReadToEndAsync(token).ConfigureAwait(false) : body)?.Trim();
            return string.IsNullOrEmpty(text) || text.Length > MaximumBody ? "A comment of at most 32,768 characters is required: --body or --stdin." : await plugin.Client.CommentAsync(issue, text, token).ConfigureAwait(false);
        }));
        return command;
    }

    private static Command Transition(JiraPlugin plugin, PluginAltaCommandContext context)
    {
        string? key = null, status = null;
        var command = Leaf("transition", "Move an issue to a status of its workflow, such as \"In Progress\" or \"Done\". Only when the user asks for it.");
        command.Add("<key>", "The key of the issue.", value => key = value);
        command.Add("<status>", "The name of the status.", value => status = value);
        command.Add((_, _) => Change(plugin, context, key, "alta.jira.transitioned", (issue, token) =>
            string.IsNullOrWhiteSpace(status) ? Task.FromResult<string?>("A status is required.") : plugin.Client.TransitionAsync(issue, status.Trim(), token), status?.Trim()));
        return command;
    }

    private static Command Assign(JiraPlugin plugin, PluginAltaCommandContext context)
    {
        string? key = null, assignee = null;
        var command = Leaf("assign", "Assign an issue: an email, or `@me` for the account that is signed in. Only when the user asks for it.");
        command.Add("<key>", "The key of the issue.", value => key = value);
        command.Add("<assignee>", "An email, an account id, or @me.", value => assignee = value);
        command.Add((_, _) => Change(plugin, context, key, "alta.jira.assigned", (issue, token) =>
            string.IsNullOrWhiteSpace(assignee) ? Task.FromResult<string?>("An assignee is required.") : plugin.Client.AssignAsync(issue, assignee.Trim(), token), assignee?.Trim()));
        return command;
    }

    private static async ValueTask<int> Change(JiraPlugin plugin, PluginAltaCommandContext context, string? key, string type, Func<string, CancellationToken, Task<string?>> change, string? value = null)
    {
        if (Settings(context) is not { } settings) return NotConfigured(context);
        if (Key(settings, key) is not { } issue) return Fail(context, "usage.missingKey", "An issue key is required, such as " + settings.Project + "-12.", Usage);
        if (await Ready(plugin, context, settings).ConfigureAwait(false) is { } refused) return refused;
        if (await change(issue, context.CancellationToken).ConfigureAwait(false) is { } problem) return Fail(context, "jira.failed", problem);
        Write(context, new { type, version = 1, correlationId = context.CorrelationId, key = issue, url = settings.BrowseUrl(issue), value });
        return 0;
    }

    private static async ValueTask<int?> Ready(JiraPlugin plugin, PluginAltaCommandContext context, JiraSettings settings)
    {
        var (problem, needsSignIn) = await plugin.EnsureReadyAsync(settings, context.CancellationToken).ConfigureAwait(false);
        return problem is null ? null : Fail(context, needsSignIn ? "jira.signInRequired" : "jira.unavailable", problem);
    }

    private static JiraSettings? Settings(PluginAltaCommandContext context) => JiraSettings.Read(JiraPlugin.ProjectPath(context));

    private static string? Key(JiraSettings settings, string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.All(char.IsAsciiDigit)) text = settings.Project + "-" + text;
        return JiraClient.IsKey(text) ? text.ToUpperInvariant() : null;
    }

    private static int NotConfigured(PluginAltaCommandContext context)
        => Fail(context, "jira.notConfigured", "This project does not use Jira. Its .alta/config.toml needs [plugins.jira] with `site` (example.atlassian.net) and `project` (the key of the Jira project).");

    private static int Fail(PluginAltaCommandContext context, string code, string message, int exitCode = Failure)
    {
        context.Stderr.WriteLine(JsonSerializer.Serialize(new { type = "alta.error", version = 1, correlationId = context.CorrelationId, code, exitCode, message }, Json));
        return exitCode;
    }

    private static void Write(PluginAltaCommandContext context, object record) => context.Stdout.WriteLine(JsonSerializer.Serialize(record, Json));

    private static Command Group(string name, string description) => new(name, description) { new CommandUsage(), new HelpOption() };

    private static Command Leaf(string name, string description) => new(name, description) { new CommandUsage(), new HelpOption() };

    private static void Help(Command command, params string[] lines)
    {
        command.Add("");
        foreach (var line in lines) command.Add(line);
    }
}
