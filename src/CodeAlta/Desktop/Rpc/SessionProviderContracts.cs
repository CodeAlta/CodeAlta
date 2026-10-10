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
