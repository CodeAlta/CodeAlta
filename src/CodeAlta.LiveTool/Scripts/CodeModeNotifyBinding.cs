using System.Text.Json;
using CodeAlta.CodeMode;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.LiveTool.Scripts;

/// <summary>
/// <c>notify({ title, body, tone, session })</c> in scripts: a notification to the user, on the channel of the sessions that are
/// done or wait (a notification of the system while CodeAlta is in the background, a notice in the window otherwise). A click
/// opens its session: the one named, else the session that runs the script. It resolves with <c>{ shown }</c>, false on a host
/// that has nowhere to show it. The prelude gives scripts the function; it calls <c>codemode.notify</c>, which this binding runs.
/// </summary>
public sealed class CodeModeNotifyBinding : ICodeModeCallHandler
{
    /// <summary>The path of the function in scripts.</summary>
    public const string Path = "codemode.notify";

    // The longest texts taken: a longer title or body is cut where it is shown.
    private const int MaximumTitle = 200;
    private const int MaximumBody = 4000;
    private const int MaximumSessionId = 200;

    private readonly IUserNotifications? _notifications;
    private readonly string? _sessionId;

    /// <summary>Creates the binding of one run.</summary>
    /// <param name="notifications">Where the notifications reach the user, or null for a host that has nowhere to show them.</param>
    /// <param name="sessionId">The session that runs the script, which a click opens when the script names none; null for a run without a session.</param>
    public CodeModeNotifyBinding(IUserNotifications? notifications, string? sessionId)
    {
        _notifications = notifications;
        _sessionId = sessionId;
    }

    /// <summary>Gets the declaration of <c>codemode.notify</c>.</summary>
    public static CodeModeFunction Function { get; } = new(Path)
    {
        Description = "Tells the user something with a notification of CodeAlta: a notification of the system while CodeAlta is in the background, a notice in the window otherwise. A click opens the session named, else the session that runs the script. Scripts call it as notify({ title, body, tone, session }), or notify(title). Resolves with shown false when nothing can show it here.",
        InputSchemaJson = """
            {"type":"object","properties":{
              "title":{"type":"string","description":"What it is about, in a few words."},
              "body":{"type":"string","description":"What happened, in a line or two."},
              "tone":{"type":"string","enum":["info","success","warning","danger"],"description":"Its color: info by default."},
              "session":{"type":"string","description":"The id of the session a click opens; the session that runs the script by default."}
            },"required":["title"],"additionalProperties":false}
            """,
        ResultType = "{ shown: boolean }",
        Parallel = true,
    };

    /// <inheritdoc />
    public async ValueTask<CodeModeCallResult> InvokeAsync(CodeModeCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        var arguments = document.RootElement;
        if (!TryText(arguments, "title", MaximumTitle, out var title, out var problem) || title is null || string.IsNullOrWhiteSpace(title))
            return Failure(problem ?? $"notify({{ title }}): the title is a non-empty string of at most {MaximumTitle} characters.");
        if (!TryText(arguments, "body", MaximumBody, out var body, out problem)) return Failure(problem!);
        if (!TryText(arguments, "session", MaximumSessionId, out var session, out problem)) return Failure(problem!);
        if (!TryText(arguments, "tone", 16, out var toneName, out problem)) return Failure(problem!);
        if (!TryTone(toneName, out var tone)) return Failure("notify({ tone }): tone is info, success, warning or danger.");

        var shown = _notifications is not null && await _notifications.NotifyAsync(new PluginNotification
        {
            Title = title,
            Body = string.IsNullOrWhiteSpace(body) ? null : body,
            Tone = tone,
            SessionId = string.IsNullOrWhiteSpace(session) ? _sessionId : session.Trim(),
        }, cancellationToken).ConfigureAwait(false);
        return CodeModeCallResult.Success(shown ? """{"shown":true}""" : """{"shown":false}""");
    }

    private static bool TryText(JsonElement arguments, string name, int maximum, out string? text, out string? problem)
    {
        (text, problem) = (null, null);
        if (!arguments.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } value || value.Length > maximum)
        {
            problem = $"notify({{ {name} }}): {name} is a string of at most {maximum} characters.";
            return false;
        }

        text = value;
        return true;
    }

    private static bool TryTone(string? name, out PluginNotificationTone tone)
    {
        tone = name?.Trim().ToLowerInvariant() switch
        {
            null or "" or "info" => PluginNotificationTone.Info,
            "success" => PluginNotificationTone.Success,
            "warning" => PluginNotificationTone.Warning,
            "danger" => PluginNotificationTone.Danger,
            _ => (PluginNotificationTone)(-1),
        };
        return Enum.IsDefined(tone);
    }

    private static CodeModeCallResult Failure(string message) => CodeModeCallResult.Failure(new CodeModeCallError("TypeError", message));
}
