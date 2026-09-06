using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Tui.App.Context;
using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Formatting;
using CodeAlta.Tui.Threading;
using CodeAlta.Tui.Views;

namespace CodeAlta.Tui.App;

internal sealed class SessionPermissionRequestCoordinator
{
    private readonly SessionSelectionContext _sessionSelection;
    private readonly ShellSessionCommandContext _commandContext;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly SessionPermissionService _permissions;

    public SessionPermissionRequestCoordinator(
        SessionSelectionContext sessionSelection,
        ShellSessionCommandContext commandContext,
        IUiDispatcher uiDispatcher,
        SessionPermissionService permissions)
    {
        ArgumentNullException.ThrowIfNull(sessionSelection);
        ArgumentNullException.ThrowIfNull(commandContext);
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentNullException.ThrowIfNull(permissions);

        _sessionSelection = sessionSelection;
        _commandContext = commandContext;
        _uiDispatcher = uiDispatcher;
        _permissions = permissions;
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

        var registration = await _permissions.RegisterAsync(sessionId, request, autoApproveEnabled, cancellationToken);
        PermissionApprovalDialog? dialog = null;
        AgentPermissionDecision decision;
        try
        {
            if (registration.IsPending)
            {
                // Join queued presentation even on cancellation. The guard is evaluated on the UI thread,
                // not only before dispatch, so canceled queued requests cannot appear late.
                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (!registration.IsPending)
                    {
                        return;
                    }

                    dialog = new PermissionApprovalDialog(request, _permissions, registration.Snapshot.Handle,
                        getBounds: () => null, getFocusTarget: () => null);
                    dialog.Show();
                });
            }

            decision = await registration.Completion;
        }
        catch
        {
            // A failed presentation must never strand the provider or silently allow its tool.
            await _permissions.CancelAsync(registration.Snapshot.Handle);
            await registration.Completion;
            throw;
        }
        finally
        {
            if (dialog is not null)
            {
                try
                {
                    await _uiDispatcher.InvokeAsync(dialog.Dispose);
                }
                finally
                {
                    await dialog.Resolution;
                }
            }
        }

        // Open-tab lookup and timeline mutation are presentation only, after authoritative completion.
        if (ChatMarkdownFormatter.ShouldDisplayPermissionRequest(autoApproveEnabled))
        {
            if (_uiDispatcher.CheckAccess())
            {
                RenderTimelineCard(sessionId, request, decision, autoApproveEnabled);
            }
            else
            {
                await _uiDispatcher.InvokeAsync(() => RenderTimelineCard(sessionId, request, decision, autoApproveEnabled));
            }
        }
        return decision;
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
                        AgentPermissionDecisionKind.AllowOnce => SR.T("Approved (Once)"),
                        AgentPermissionDecisionKind.AllowForSession => SR.T("Approved (Session)"),
                        AgentPermissionDecisionKind.Deny => SR.T("Denied"),
                        _ => SR.T("Cancelled"),
                    },
                    SR.T("Permission Request"));
            },
            SR.T("permission request"));
    }
}
