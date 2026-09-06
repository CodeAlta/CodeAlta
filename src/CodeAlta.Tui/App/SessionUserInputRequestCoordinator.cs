using CodeAlta.Agent;
using CodeAlta.Tui.App.Context;
using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Chat;
using CodeAlta.Tui.Presentation.Formatting;

namespace CodeAlta.Tui.App;

internal sealed class SessionUserInputRequestCoordinator
{
    private readonly SessionSelectionContext _sessionSelection;
    private readonly ShellSessionCommandContext _commandContext;

    public SessionUserInputRequestCoordinator(
        SessionSelectionContext sessionSelection,
        ShellSessionCommandContext commandContext)
    {
        ArgumentNullException.ThrowIfNull(sessionSelection);
        ArgumentNullException.ThrowIfNull(commandContext);

        _sessionSelection = sessionSelection;
        _commandContext = commandContext;
    }

    public Task<AgentUserInputResponse> HandleAsync(
        string sessionId,
        AgentUserInputRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var autoApproveEnabled = _commandContext.GetAutoApproveEnabled();
        var response = SessionUserInputPolicy.CreateResponse(request, autoApproveEnabled);
        if (_sessionSelection.FindOpenSession(sessionId) is { } tab)
        {
            _commandContext.TryRenderInteraction(
                tab,
                () =>
                {
                    tab.Timeline.UpsertInteraction(
                        request.InteractionId,
                        request.Timestamp,
                        ChatMarkdownFormatter.FormatChatUserInputRequestMarkdown(request, autoApproveEnabled),
                        ChatMarkdownFormatter.FormatChatImmediateUserInputResponseMarkdown(response, autoApproveEnabled),
                        ChatTimelineTone.Interaction,
                        SR.T("Action Required"),
                        SR.T("User Input Request"));
                },
                SR.T("user input request"));
        }

        return Task.FromResult(response);
    }
}
