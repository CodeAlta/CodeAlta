using CodeAlta.Catalog;
using CodeAlta.Tui.Models;

namespace CodeAlta.Tui.App;

internal interface IFrontendPersistencePort
{
    string? LoadPromptDraft(PromptSessionId promptSessionId);

    void DeletePromptDraft(PromptSessionId promptSessionId);

    Task PersistViewStateAsync(CancellationToken cancellationToken = default);

    Task RegisterCreatedSessionAsync(SessionViewDescriptor session, CancellationToken cancellationToken = default);
}

internal sealed class FrontendPersistencePort : IFrontendPersistencePort
{
    private readonly Func<string, string?> _loadPromptDraft;
    private readonly Action<string> _deletePromptDraft;
    private readonly Func<CancellationToken, Task> _persistViewStateAsync;
    private readonly Func<SessionViewDescriptor, CancellationToken, Task> _registerCreatedSessionAsync;

    public FrontendPersistencePort(
        Func<string, string?> loadPromptDraft,
        Action<string> deletePromptDraft,
        Func<CancellationToken, Task> persistViewStateAsync,
        Func<SessionViewDescriptor, CancellationToken, Task> registerCreatedSessionAsync)
    {
        ArgumentNullException.ThrowIfNull(loadPromptDraft);
        ArgumentNullException.ThrowIfNull(deletePromptDraft);
        ArgumentNullException.ThrowIfNull(persistViewStateAsync);
        ArgumentNullException.ThrowIfNull(registerCreatedSessionAsync);

        _loadPromptDraft = loadPromptDraft;
        _deletePromptDraft = deletePromptDraft;
        _persistViewStateAsync = persistViewStateAsync;
        _registerCreatedSessionAsync = registerCreatedSessionAsync;
    }

    public string? LoadPromptDraft(PromptSessionId promptSessionId)
        => _loadPromptDraft(GetPromptSessionKey(promptSessionId));

    public void DeletePromptDraft(PromptSessionId promptSessionId)
        => _deletePromptDraft(GetPromptSessionKey(promptSessionId));

    public async Task PersistViewStateAsync(CancellationToken cancellationToken = default)
        => await _persistViewStateAsync(cancellationToken);

    public async Task RegisterCreatedSessionAsync(SessionViewDescriptor session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await _registerCreatedSessionAsync(session, cancellationToken);
    }

    private static string GetPromptSessionKey(PromptSessionId promptSessionId)
    {
        if (promptSessionId.IsEmpty)
        {
            throw new ArgumentException("Prompt session id cannot be empty.", nameof(promptSessionId));
        }

        return promptSessionId.Value;
    }
}
