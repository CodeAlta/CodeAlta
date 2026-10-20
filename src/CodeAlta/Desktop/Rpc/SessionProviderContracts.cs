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
