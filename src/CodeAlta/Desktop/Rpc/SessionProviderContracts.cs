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
