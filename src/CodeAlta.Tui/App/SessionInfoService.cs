using CodeAlta.Agent;
using CodeAlta.Tui.App.Context;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Tui.Presentation.Sessions;

namespace CodeAlta.Tui.App;

internal sealed class SessionInfoService
{
    private readonly record struct SessionInfoSnapshot(
        SessionViewDescriptor Session,
        string ProviderDisplayName,
        string? ModelId,
        AgentReasoningEffort? ReasoningEffort,
        IReadOnlyList<AgentEvent>? History);

    private readonly IAgentSessionCatalog _sessionCatalog;
    private readonly SessionSelectionContext _sessionSelection;
    private readonly IReadOnlyDictionary<string, ModelProviderState> _modelProviderStates;

    public SessionInfoService(
        IAgentSessionCatalog sessionCatalog,
        SessionSelectionContext sessionSelection,
        IReadOnlyDictionary<string, ModelProviderState> modelProviderStates)
    {
        ArgumentNullException.ThrowIfNull(sessionCatalog);
        ArgumentNullException.ThrowIfNull(sessionSelection);
        ArgumentNullException.ThrowIfNull(modelProviderStates);

        _sessionCatalog = sessionCatalog;
        _sessionSelection = sessionSelection;
        _modelProviderStates = modelProviderStates;
    }

    public async Task<SessionInfoReport?> LoadSelectedSessionReportAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await CaptureSelectedSessionSnapshotAsync(cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        AgentSessionMetadata? metadata = null;
        try
        {
            await foreach (var session in _sessionCatalog
                .ListSessionsAsync(filter: null, cancellationToken: cancellationToken))
            {
                if (string.Equals(session.SessionId, snapshot.Value.Session.SessionId, StringComparison.Ordinal))
                {
                    metadata = session;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            metadata = null;
        }

        return SessionInfoReportBuilder.Build(
            snapshot.Value.Session,
            snapshot.Value.ProviderDisplayName,
            snapshot.Value.ModelId,
            snapshot.Value.ReasoningEffort,
            metadata,
            snapshot.Value.History,
            DateTimeOffset.Now);
    }

    private async Task<SessionInfoSnapshot?> CaptureSelectedSessionSnapshotAsync(CancellationToken cancellationToken)
    {
        var session = _sessionSelection.GetSelectedSession();
        if (session is null)
        {
            return null;
        }

        var tab = _sessionSelection.EnsureSessionTab(session);
        var providerState = _modelProviderStates.TryGetValue(session.ProviderId, out var resolvedProviderState)
            ? resolvedProviderState
            : new ModelProviderState(new ModelProviderId(session.ProviderId), session.ProviderId);

        if (!tab.HistoryLoaded || tab.HistoryEvents is null)
        {
            try
            {
                // Session/tab history lives in UI-owned state, so capture it before switching to
                // background provider I/O for the session lookup below.
                await _sessionSelection.EnsureSessionHistoryLoadedAsync(session, cancellationToken);
            }
            catch (InvalidOperationException)
            {
            }
        }

        return new SessionInfoSnapshot(
            session,
            providerState.DisplayName,
            tab.ModelId ?? providerState.SelectedModelId,
            tab.ReasoningEffort,
            tab.HistoryEvents);
    }
}
