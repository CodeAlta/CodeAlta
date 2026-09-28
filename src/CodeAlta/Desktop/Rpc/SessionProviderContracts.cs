namespace CodeAlta.Desktop.Rpc;

internal sealed record SessionProviderChoice(string Id, string Name);
internal sealed record SessionProviderChoices(string Status, string? Epoch, string SessionId, string? RuntimeInstanceId,
    string? AttachmentGeneration, string? ProviderKey, string? Revision, SessionProviderChoice[] Providers);
internal sealed record SessionProviderRequest(string ExpectedEpoch, string SessionId, string RuntimeInstanceId,
    string? AttachmentGeneration, string ExpectedProviderKey, string Revision, string ProviderKey);
internal sealed record SessionProviderResult(string Status, string? Epoch, string SessionId);
