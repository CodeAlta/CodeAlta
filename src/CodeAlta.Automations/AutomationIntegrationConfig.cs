using System.Text;
using CodeAlta.Catalog.Secrets;
using Tomlyn.Model;

namespace CodeAlta.Automations;

/// <summary>
/// The triggers of tools other than Git (<c>doc/fleet/orchestration.md</c>, "Triggers"): Jira versions and queries,
/// Slack, incident.io and signed webhooks, as one inline table of a configuration file. A credential is always a
/// <c>${secret:NAME}</c> reference to the store of secrets, read by the host when the trigger is used: a configuration
/// file never holds a token.
/// </summary>
internal static class AutomationIntegrationConfig
{
    private const int MaximumTextLength = 512;
    private const int MaximumMatchLength = 200;
    private const int MaximumFieldLength = 200;
    private const int MaximumHookLength = 64;

    /// <summary>Reads an integration trigger from its table.</summary>
    internal static bool TryRead(AutomationTriggerKind kind, TomlTable table, out AutomationTrigger? trigger, out string? problem)
    {
        trigger = null;
        string? site = null, email = null, token = null, project = null, query = null, appToken = null, botToken = null, channel = null, match = null,
            apiKey = null, hook = null, secret = null, signature = null, header = null, @event = null;
        IReadOnlyList<string> fields = [];
        var reply = false;
        if (!AutomationConfig.TryText(table, "event", out @event, out problem) || !AutomationConfig.TryText(table, "site", out site, out problem)
            || !AutomationConfig.TryText(table, "email", out email, out problem) || !AutomationConfig.TryText(table, "token", out token, out problem)
            || !AutomationConfig.TryText(table, "jira_project", out project, out problem) || !AutomationConfig.TryText(table, "jql", out query, out problem)
            || !AutomationConfig.TryText(table, "app_token", out appToken, out problem) || !AutomationConfig.TryText(table, "bot_token", out botToken, out problem)
            || !AutomationConfig.TryText(table, "channel", out channel, out problem) || !AutomationConfig.TryText(table, "match", out match, out problem)
            || !AutomationConfig.TryText(table, "api_key", out apiKey, out problem) || !AutomationConfig.TryText(table, "hook", out hook, out problem)
            || !AutomationConfig.TryText(table, "secret", out secret, out problem) || !AutomationConfig.TryText(table, "signature", out signature, out problem)
            || !AutomationConfig.TryText(table, "signature_header", out header, out problem) || !AutomationConfig.TryTexts(table, "fields", out fields, out problem)
            || !AutomationConfig.TryFlag(table, "reply", false, out reply, out problem)) return false;
        var events = AutomationTrigger.Events(kind);
        trigger = new AutomationTrigger(kind)
        {
            Event = AutomationConfig.Blank(@event)?.ToLowerInvariant() ?? events[0],
            Site = AutomationConfig.Blank(site)?.TrimEnd('/'),
            Email = AutomationConfig.Blank(email),
            Token = AutomationConfig.Blank(token),
            JiraProject = AutomationConfig.Blank(project),
            Query = AutomationConfig.Blank(query),
            AppToken = AutomationConfig.Blank(appToken),
            BotToken = AutomationConfig.Blank(botToken),
            Channel = AutomationConfig.Blank(channel),
            Match = AutomationConfig.Blank(match),
            ApiKey = AutomationConfig.Blank(apiKey),
            Hook = AutomationConfig.Blank(hook),
            Secret = AutomationConfig.Blank(secret),
            Signature = kind == AutomationTriggerKind.Webhook ? AutomationConfig.Blank(signature)?.ToLowerInvariant() ?? AutomationSignatures.Standard
                : kind == AutomationTriggerKind.IncidentIo ? AutomationSignatures.Standard : null,
            SignatureHeader = AutomationConfig.Blank(header),
            Fields = [.. fields.Select(static field => field.Trim()).Where(static field => field.Length > 0).Distinct(StringComparer.Ordinal)],
            Reply = reply,
        };
        return true;
    }

    /// <summary>Checks an integration trigger as reading it back would.</summary>
    /// <returns>What is wrong with it, or null.</returns>
    internal static string? Validate(AutomationTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        var name = Name(trigger.Kind);
        if (!AutomationTrigger.Events(trigger.Kind).Contains(trigger.Event))
            return $"A {name} trigger starts on {string.Join(" or ", AutomationTrigger.Events(trigger.Kind))}.";
        if (trigger.Reply && !trigger.CanReply) return $"A {name} trigger has nowhere to reply.";
        foreach (var text in new[] { trigger.Site, trigger.Email, trigger.JiraProject, trigger.Query, trigger.Channel, trigger.Match, trigger.Hook, trigger.SignatureHeader })
        {
            if (text is not null && (text.Any(char.IsControl) || !AutomationConfig.IsWellFormed(text) || text.Length > AutomationTrigger.MaximumQueryLength))
                return $"The settings of a {name} trigger are single lines of text.";
        }

        switch (trigger.Kind)
        {
            case AutomationTriggerKind.JiraVersion or AutomationTriggerKind.JiraQuery:
                if (SiteProblem(trigger.Site) is { } site) return site;
                if (trigger.Email is null) return "A Jira trigger names the email of the account of its API token.";
                if (Value(trigger.Email, MaximumTextLength) is { } email) return "The email of a Jira trigger " + email;
                if (CredentialProblem(trigger.Token, "API token of a Jira trigger") is { } token) return token;
                if (trigger.Kind == AutomationTriggerKind.JiraVersion)
                {
                    if (trigger.JiraProject is null || !IsReference(trigger.JiraProject, SecretReferenceKind.Variable) && !IsProjectKey(trigger.JiraProject))
                        return "A Jira version trigger names the key of its Jira project, such as PLAT.";
                }
                else if (trigger.Query is null)
                {
                    return "A Jira query trigger has a JQL query, such as project = PLAT AND status = \"To Do\".";
                }

                break;
            case AutomationTriggerKind.Slack:
                if (CredentialProblem(trigger.AppToken, "app-level token (xapp-) of a Slack trigger") is { } app) return app;
                if (CredentialProblem(trigger.BotToken, "bot token (xoxb-) of a Slack trigger") is { } bot) return bot;
                if (trigger.Channel is null || !IsReference(trigger.Channel, SecretReferenceKind.Variable) && !IsSlackId(trigger.Channel))
                    return "A Slack trigger names the id of its channel, such as C0123ABCDEF (in Slack: the channel's details, at the bottom).";
                if (trigger.Match is { Length: > MaximumMatchLength }) return $"The text a Slack message must contain has at most {MaximumMatchLength} characters.";
                break;
            case AutomationTriggerKind.IncidentIo or AutomationTriggerKind.Webhook:
                if (trigger.Hook is null || !IsHookId(trigger.Hook)) return $"A {name} trigger names the hook of the server its events are pushed to.";
                if (CredentialProblem(trigger.Secret, $"signing secret of a {name} trigger") is { } secret) return secret;
                if (trigger.Kind == AutomationTriggerKind.IncidentIo)
                {
                    if (CredentialProblem(trigger.ApiKey, "API key of an incident.io trigger") is { } key) return key;
                    break;
                }

                if (!AutomationSignatures.All.Contains(trigger.Signature)) return $"A webhook is signed {string.Join(", ", AutomationSignatures.All)}.";
                if (trigger.Signature == AutomationSignatures.HmacSha256 && (trigger.SignatureHeader is null || !IsHeaderName(trigger.SignatureHeader)))
                    return "A webhook signed with hmac_sha256 names the header of its signature, such as X-Signature.";
                if (trigger.Fields.Count > AutomationTrigger.MaximumFields
                    || trigger.Fields.Any(static field => field.Length > MaximumFieldLength || !field.StartsWith('/') || field.Any(char.IsControl)))
                    return $"The fields of a webhook are at most {AutomationTrigger.MaximumFields} JSON pointers, such as /action or /issue/title.";
                break;
        }

        // What is not part of this kind of trigger is not written beside it: a token named for nothing would look used.
        return Stray(trigger) is { } stray ? $"A {name} trigger has no {stray}." : null;
    }

    /// <summary>Writes an integration trigger as an inline table.</summary>
    internal static string Format(AutomationTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        var builder = new StringBuilder("{ type = ").Append(AutomationConfig.Quote(trigger.KindName));
        Add("event", trigger.Event);
        Add("site", trigger.Site);
        Add("email", trigger.Email);
        Add("token", trigger.Token);
        Add("jira_project", trigger.JiraProject);
        Add("jql", trigger.Query);
        Add("channel", trigger.Channel);
        Add("match", trigger.Match);
        Add("app_token", trigger.AppToken);
        Add("bot_token", trigger.BotToken);
        Add("hook", trigger.Hook);
        Add("api_key", trigger.ApiKey);
        Add("secret", trigger.Secret);
        if (trigger.Kind == AutomationTriggerKind.Webhook) Add("signature", trigger.Signature);
        Add("signature_header", trigger.SignatureHeader);
        if (trigger.Fields.Count > 0) builder.Append(", fields = [").Append(string.Join(", ", trigger.Fields.Select(AutomationConfig.Quote))).Append(']');
        if (trigger.Reply) builder.Append(", reply = true");
        return builder.Append(" }").ToString();

        void Add(string key, string? value)
        {
            if (value is not null) builder.Append(", ").Append(key).Append(" = ").Append(AutomationConfig.Quote(value));
        }
    }

    /// <summary>The name of a kind for the user.</summary>
    internal static string Name(AutomationTriggerKind kind) => kind switch
    {
        AutomationTriggerKind.JiraVersion => "Jira version",
        AutomationTriggerKind.JiraQuery => "Jira query",
        AutomationTriggerKind.Slack => "Slack",
        AutomationTriggerKind.IncidentIo => "incident.io",
        _ => "webhook",
    };

    /// <summary>Whether a text is a hook id of the server: 16 to 64 lowercase letters and digits.</summary>
    internal static bool IsHookId(string? text)
        => text is { Length: >= 16 and <= MaximumHookLength } && text.All(static ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9');

    private static string? CredentialProblem(string? reference, string what)
    {
        if (reference is null) return $"The {what} is missing: name a secret of this machine, such as ${{secret:NAME}}.";
        // A value written in the file would travel with it, be read by whoever reads the file, and end in backups.
        return IsReference(reference, SecretReferenceKind.Secret) ? null
            : $"The {what} is a reference to a secret, ${{secret:NAME}}, never the token itself: set its value in Settings > Secrets.";
    }

    private static string? SiteProblem(string? site)
    {
        if (site is null) return "A Jira trigger names its site, such as https://acme.atlassian.net.";
        if (IsReference(site, SecretReferenceKind.Variable)) return null;
        return Uri.TryCreate(site, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? null : "The site of a Jira trigger is its https address, such as https://acme.atlassian.net.";
    }

    private static string? Value(string text, int maximum)
        => IsReference(text, SecretReferenceKind.Variable) || text.Length <= maximum && !text.Contains("${", StringComparison.Ordinal) ? null : "is a text, or a ${var:NAME} reference.";

    private static bool IsReference(string text, SecretReferenceKind kind) => SecretReferences.TryParseLone(text, out var reference) && reference.Kind == kind;

    private static bool IsProjectKey(string text)
        => text.Length is >= 1 and <= 32 && char.IsAsciiLetterUpper(text[0]) && text.All(static ch => char.IsAsciiLetterUpper(ch) || char.IsAsciiDigit(ch) || ch == '_');

    private static bool IsSlackId(string text)
        => text.Length is >= 3 and <= 32 && text[0] is 'C' or 'G' && text.All(static ch => char.IsAsciiLetterUpper(ch) || char.IsAsciiDigit(ch));

    private static bool IsHeaderName(string text)
        => text.Length is >= 1 and <= 64 && text.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    private static string? Stray(AutomationTrigger trigger)
    {
        var jira = trigger.Kind is AutomationTriggerKind.JiraVersion or AutomationTriggerKind.JiraQuery;
        var hooked = trigger.IsHooked;
        if (!jira && (trigger.Site ?? trigger.Email ?? trigger.Token) is not null) return "site, email or token";
        if (trigger.Kind != AutomationTriggerKind.JiraVersion && trigger.JiraProject is not null) return "jira_project";
        if (trigger.Kind != AutomationTriggerKind.JiraQuery && trigger.Query is not null) return "jql";
        if (trigger.Kind != AutomationTriggerKind.Slack && (trigger.AppToken ?? trigger.BotToken ?? trigger.Channel ?? trigger.Match) is not null) return "app_token, bot_token, channel or match";
        if (!hooked && (trigger.Hook ?? trigger.Secret) is not null) return "hook or secret";
        if (trigger.Kind != AutomationTriggerKind.IncidentIo && trigger.ApiKey is not null) return "api_key";
        if (trigger.Kind != AutomationTriggerKind.Webhook && (trigger.SignatureHeader is not null || trigger.Fields.Count > 0)) return "signature_header or fields";
        if (trigger.Kind == AutomationTriggerKind.Webhook && trigger.Signature != AutomationSignatures.HmacSha256 && trigger.SignatureHeader is not null) return "signature_header";
        return null;
    }
}
