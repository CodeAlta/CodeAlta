using System.Text.Json.Serialization;

namespace CodeAlta.Desktop.Rpc;

internal sealed record SessionProviderChoice(string Id, string Name);
internal sealed record SessionProviderChoices(string Status, string? Epoch, string SessionId, string? RuntimeInstanceId,
    string? AttachmentGeneration, string? ProviderKey, string? Revision, SessionProviderChoice[] Providers);
internal sealed record SessionProviderRequest(string ExpectedEpoch, string SessionId, string RuntimeInstanceId,
    string? AttachmentGeneration, string ExpectedProviderKey, string Revision, string ProviderKey);
internal sealed record SessionProviderResult(string Status, string? Epoch, string SessionId);

/// <summary>Asks to stop one background task of a session, by the identity its runtime state lists.</summary>
internal sealed record SessionStopBackgroundTaskRequest(string ExpectedEpoch, string SessionId, string TaskId);

/// <summary>Whether the provider took the request: <c>ok</c>, <c>unavailable</c> (no such task runs), or why it was refused.</summary>
internal sealed record SessionStopBackgroundTaskResult(string Status, string? Epoch, string SessionId);

/// <summary>Asks for the resume after a limit a session waits for.</summary>
internal sealed record SessionResumeRequest(string ExpectedEpoch, string SessionId);

/// <summary>Cancels, or starts at once, the resume after a limit a session waits for, by the identity its card names.</summary>
internal sealed record SessionResumeActionRequest(string ExpectedEpoch, string SessionId, string ResumeId);

/// <summary>A resume after a limit, as its card offers Cancel and Resume now.</summary>
/// <param name="ResumeId">The identity of the resume.</param>
/// <param name="ResumeAt">When it starts, ISO 8601 in UTC.</param>
/// <param name="ProviderKey">The provider whose limit resets first.</param>
/// <param name="ProviderName">The name of that provider.</param>
/// <param name="Attempt">How many times in a row the session resumes by itself, this one included.</param>
internal sealed record SessionResumeItem(string ResumeId, string ResumeAt, string ProviderKey, string ProviderName, int Attempt);

/// <summary><c>ok</c> with the resume the session waits for, or none; or why it was not read.</summary>
internal sealed record SessionResumeState(string Status, string? Epoch, string SessionId, SessionResumeItem? Resume);

/// <summary><c>ok</c> when the resume was cancelled or started; <c>unknown</c> when it no longer waits; or why it was refused.</summary>
internal sealed record SessionResumeResult(string Status, string? Epoch, string SessionId);

/// <summary>Asks to give a session a permission mode at once.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="SessionId">The session.</param>
/// <param name="PermissionMode">One of the modes its choices offer, or <c>provider</c> for the one its provider is configured with.</param>
internal sealed record SessionPermissionModeRequest(string ExpectedEpoch, string SessionId, string PermissionMode);
internal sealed record SessionReviewBeforeCommitRequest(string ExpectedEpoch, string SessionId);
/// <param name="ReviewBeforeCommit">The choice of the session; null follows the setting.</param>
internal sealed record SessionSetReviewBeforeCommitRequest(string ExpectedEpoch, string SessionId, bool? ReviewBeforeCommit);
/// <param name="Own">The session's own choice; null when it follows the setting.</param>
/// <param name="Effective">What applies.</param>
/// <param name="Setting">The setting, <c>[review] before_commit</c>.</param>
internal sealed record SessionReviewBeforeCommitResult(string Status, string? Epoch, string SessionId, bool? Own, bool Effective, bool Setting);

/// <summary>
/// The options of a session as the page shows and chooses them (<see cref="CodeAlta.Agent.AgentSessionOptions"/>): each
/// option by its value, the defaults being every option on.
/// </summary>
/// <param name="SubSessions">Whether the session can start sessions of its own (sub-agents).</param>
/// <param name="ContextSize">The size of context of the session, in input tokens; null for the default.</param>
internal sealed record SessionOptionsChoice(bool SubSessions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ContextSize = null)
{
    /// <summary>The options of a session, or of what records none.</summary>
    public static SessionOptionsChoice Of(CodeAlta.Agent.AgentSessionOptions? options)
    {
        var of = CodeAlta.Agent.AgentSessionOptions.Of(options);
        return new(of.SubSessions, ContextSizeContract.Of(of.ContextSize));
    }

    /// <summary>What a choice records: null for the defaults.</summary>
    public static CodeAlta.Agent.AgentSessionOptions? Record(SessionOptionsChoice? choice)
        => choice is null ? null : CodeAlta.Agent.AgentSessionOptions.Stored(new CodeAlta.Agent.AgentSessionOptions
            { SubSessions = choice.SubSessions, ContextSize = choice.ContextSize is > 0 ? choice.ContextSize : null });
}

/// <summary>A size of context as the page reads it: a number of tokens, null for none or for one it cannot hold.</summary>
internal static class ContextSizeContract
{
    /// <summary>A size of context for the page.</summary>
    public static int? Of(long? size) => size is > 0 and <= int.MaxValue ? (int)size : null;

    /// <summary>The sizes of context of a model for the page; null for none.</summary>
    public static IReadOnlyList<int>? Of(IReadOnlyList<long>? sizes)
        => sizes is { Count: > 0 } ? [.. sizes.Where(static size => size is > 0 and <= int.MaxValue).Select(static size => (int)size)] : null;
}

/// <summary>Asks for the options of a session.</summary>
internal sealed record SessionOptionsRequest(string ExpectedEpoch, string SessionId);

/// <summary>Changes the options of a session: its tools and its instructions follow them from its next prompt.</summary>
internal sealed record SessionSetOptionsRequest(string ExpectedEpoch, string SessionId, SessionOptionsChoice Options);

/// <summary>The options of a session after the request, when <paramref name="Status"/> is <c>ok</c>.</summary>
internal sealed record SessionOptionsResult(string Status, string? Epoch, string SessionId, SessionOptionsChoice? Options);

/// <summary>
/// <c>ok</c> with the mode of the session after the request (null for the one of its provider); <c>invalid_mode</c>,
/// <c>unavailable</c> (an unknown session, or one this window does not own), <c>busy</c> (its provider is being
/// changed, or it is being deleted), <c>closed</c>, or why it was refused.
/// </summary>
internal sealed record SessionPermissionModeResult(string Status, string? Epoch, string SessionId, string? PermissionMode);

/// <summary>Asks to turn the Remote Control of a session on or off.</summary>
internal sealed record SessionRemoteControlRequest(string ExpectedEpoch, string SessionId, bool Enabled);

/// <summary>
/// <c>ok</c> with the Remote Control after the request; <c>unavailable</c> (an unknown session, a provider without
/// Remote Control), <c>busy</c> (its provider is being changed, or it is being deleted), or why it was refused.
/// </summary>
internal sealed record SessionRemoteControlResult(string Status, string? Epoch, string SessionId, SessionRemoteControlResponse? RemoteControl);

/// <summary>The Remote Control of a session.</summary>
/// <param name="Status"><c>off</c>, <c>connecting</c>, <c>connected</c> or <c>failed</c>.</param>
/// <param name="SessionUrl">The link that opens the session on claude.ai; null before Claude Code gives it.</param>
/// <param name="Error">Why it could not be connected, as Claude Code says it; null unless it failed.</param>
internal sealed record SessionRemoteControlResponse(string Status, string? SessionUrl, string? Error)
{
    internal static SessionRemoteControlResponse From(CodeAlta.Agent.AgentRemoteControl remote)
        => new(remote.Status switch
        {
            CodeAlta.Agent.AgentRemoteControlStatus.Connecting => "connecting",
            CodeAlta.Agent.AgentRemoteControlStatus.Connected => "connected",
            CodeAlta.Agent.AgentRemoteControlStatus.Failed => "failed",
            _ => "off",
        },
        // A link of claude.ai only: the page opens it in the browser.
        remote.SessionUrl is { Length: <= 512 } url && url.StartsWith("https://claude.ai/", StringComparison.Ordinal) ? url : null,
        remote.Error is { } error ? error.Length > 512 ? error[..512] : error : null);
}

/// <summary>Asks to add to an idle session the turns made outside CodeAlta on the conversation its provider keeps.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="SessionId">The session.</param>
internal sealed record SessionCatchUpRequest(string ExpectedEpoch, string SessionId);

/// <summary>
/// <c>appended</c> (turns were added), <c>cannot_follow</c> (a notice was added instead), <c>unchanged</c>, <c>none</c>,
/// <c>busy</c>, <c>held</c> (another program is working on the conversation now), <c>stale</c>, or why it was refused.
/// </summary>
internal sealed record SessionCatchUpResult(string Status, string? Epoch, string SessionId);
