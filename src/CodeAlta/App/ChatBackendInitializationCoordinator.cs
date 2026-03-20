using System.ComponentModel;
using CodeAlta.Agent;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Chat;

namespace CodeAlta.App;

internal sealed class ChatBackendInitializationCoordinator
{
    private readonly AgentHub _agentHub;
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates;
    private readonly Action<Action> _dispatchToUi;
    private readonly Action _refreshHeaderAndThreadWorkspace;

    public ChatBackendInitializationCoordinator(
        AgentHub agentHub,
        Dictionary<string, ChatBackendState> chatBackendStates,
        Action<Action> dispatchToUi,
        Action refreshHeaderAndThreadWorkspace)
    {
        ArgumentNullException.ThrowIfNull(agentHub);
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(dispatchToUi);
        ArgumentNullException.ThrowIfNull(refreshHeaderAndThreadWorkspace);

        _agentHub = agentHub;
        _chatBackendStates = chatBackendStates;
        _dispatchToUi = dispatchToUi;
        _refreshHeaderAndThreadWorkspace = refreshHeaderAndThreadWorkspace;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
                RefreshAsync(AgentBackendIds.Codex, cancellationToken),
                RefreshAsync(AgentBackendIds.Copilot, cancellationToken))
            .ConfigureAwait(false);
    }

    internal static (ChatBackendAvailability Availability, string StatusMessage) ClassifyFailure(
        ChatBackendState state,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(exception);

        var root = exception.GetBaseException();
        if (root is FileNotFoundException or DirectoryNotFoundException)
        {
            return (ChatBackendAvailability.Unsupported, ChatBackendPresentation.BuildUnsupportedBackendMessage(state, root.Message));
        }

        if (root is Win32Exception win32Exception && win32Exception.NativeErrorCode == 2)
        {
            return (ChatBackendAvailability.Unsupported, ChatBackendPresentation.BuildUnsupportedBackendMessage(state, root.Message));
        }

        var message = root.Message.Trim();
        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("No such file", StringComparison.OrdinalIgnoreCase))
        {
            return (ChatBackendAvailability.Unsupported, ChatBackendPresentation.BuildUnsupportedBackendMessage(state, message));
        }

        return (ChatBackendAvailability.Failed, ChatBackendPresentation.BuildFailedBackendMessage(state, message));
    }

    private async Task RefreshAsync(AgentBackendId backendId, CancellationToken cancellationToken)
    {
        var state = _chatBackendStates[backendId.Value];
        _dispatchToUi(
            () =>
            {
                state.Availability = ChatBackendAvailability.Connecting;
                state.StatusMessage = "Detecting backend...";
                _refreshHeaderAndThreadWorkspace();
            });

        try
        {
            var models = await _agentHub.ListModelsAsync(backendId, cancellationToken).ConfigureAwait(false);
            _dispatchToUi(
                () =>
                {
                    state.Models.Clear();
                    state.Models.AddRange(models);
                    state.SelectedModelId = ChatBackendPresentation.ResolvePreferredModelId(models, state.SelectedModelId);
                    state.SelectedReasoningEffort = ChatBackendPresentation.ResolvePreferredReasoningEffort(
                        ChatBackendPreferenceCoordinator.FindModel(models, state.SelectedModelId),
                        state.SelectedReasoningEffort);
                    state.Availability = ChatBackendAvailability.Ready;
                    state.StatusMessage = ChatBackendPresentation.BuildReadyStatusMessage(state);
                    _refreshHeaderAndThreadWorkspace();
                });
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var (availability, statusMessage) = ClassifyFailure(state, ex);
            _dispatchToUi(
                () =>
                {
                    state.Models.Clear();
                    state.SelectedModelId = null;
                    state.SelectedReasoningEffort = null;
                    state.DraftScopeKey = null;
                    state.Availability = availability;
                    state.StatusMessage = statusMessage;
                    _refreshHeaderAndThreadWorkspace();
                });
        }
    }
}