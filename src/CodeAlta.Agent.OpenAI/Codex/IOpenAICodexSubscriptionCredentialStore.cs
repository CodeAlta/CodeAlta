namespace CodeAlta.Agent.OpenAI.Codex;

internal interface IOpenAICodexSubscriptionCredentialStore
{
    ValueTask<IAsyncDisposable> AcquireLockAsync(string providerKey, CancellationToken cancellationToken = default);

    ValueTask<OpenAICodexSubscriptionCredential?> LoadAsync(
        string providerKey,
        CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        string providerKey,
        OpenAICodexSubscriptionCredential credential,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(
        string providerKey,
        CancellationToken cancellationToken = default);
}
