using CodeAlta.Agent;
using CodeAlta.App.Context;
using CodeAlta.App.State;
using CodeAlta.Models;
using CodeAlta.Presentation.Formatting;
using CodeAlta.Threading;
using CodeAlta.Views;

namespace CodeAlta.App;

internal sealed class SessionPermissionRequestCoordinator
{
    private readonly SessionSelectionContext _sessionSelection;
    private readonly ShellSessionCommandContext _commandContext;
    private readonly IUiDispatcher _uiDispatcher;

    public SessionPermissionRequestCoordinator(
        SessionSelectionContext sessionSelection,
        ShellSessionCommandContext commandContext,
        IUiDispatcher uiDispatcher)
    {
        ArgumentNullException.ThrowIfNull(sessionSelection);
        ArgumentNullException.ThrowIfNull(commandContext);
        ArgumentNullException.ThrowIfNull(uiDispatcher);

        _sessionSelection = sessionSelection;
        _commandContext = commandContext;
        _uiDispatcher = uiDispatcher;
    }

    public async Task<AgentPermissionDecision> HandleAsync(
        string sessionId,
        AgentPermissionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var autoApproveEnabled = _commandContext.GetAutoApproveEnabled();

        // Fast path: auto-approve
        if (autoApproveEnabled)
        {
            var decision = new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce);
            RenderTimelineCard(sessionId, request, decision, autoApproveEnabled);
            return decision;
        }

        // Interactive path: show modal dialog and wait for user decision
        var tcs = new TaskCompletionSource<AgentPermissionDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var _ = cancellationToken.Register(() =>
            tcs.TrySetResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Cancel)));

        // Marshal to UI thread and show dialog
        await _uiDispatcher.InvokeAsync(() =>
        {
            new PermissionApprovalDialog(
                request,
                tcs,
                getBounds: () => null,
                getFocusTarget: () => null).Show();
        });

        var userDecision = await tcs.Task;

        // Render timeline card for record
        RenderTimelineCard(sessionId, request, userDecision, autoApproveEnabled);
        return userDecision;
    }

    private void RenderTimelineCard(
        string sessionId,
        AgentPermissionRequest request,
        AgentPermissionDecision decision,
        bool autoApproveEnabled)
    {
        if (!ChatMarkdownFormatter.ShouldDisplayPermissionRequest(autoApproveEnabled))
        {
            return;
        }

        var tab = _sessionSelection.FindOpenSession(sessionId);
        if (tab is null)
        {
            return;
        }

        _commandContext.TryRenderInteraction(
            tab,
            () =>
            {
                tab.Timeline.UpsertInteraction(
                    request.InteractionId,
                    request.Timestamp,
                    ChatMarkdownFormatter.FormatChatPermissionRequestMarkdown(request),
                    ChatMarkdownFormatter.FormatChatImmediatePermissionDecisionMarkdown(decision, autoApproveEnabled),
                    ChatTimelineTone.Interaction,
                    decision.Kind switch
                    {
                        AgentPermissionDecisionKind.AllowOnce => "Approved (Once)",
                        AgentPermissionDecisionKind.AllowForSession => "Approved (Session)",
                        AgentPermissionDecisionKind.Deny => "Denied",
                        _ => "Cancelled",
                    },
                    "Permission Request");
            },
            "permission request");
    }
}
