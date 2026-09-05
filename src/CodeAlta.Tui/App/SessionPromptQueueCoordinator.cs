using CodeAlta.Tui.App.Context;
using CodeAlta.Tui.App.Events;
using CodeAlta.Tui.App.State;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Prompting;
using CodeAlta.Tui.ViewModels;

namespace CodeAlta.Tui.App;

internal sealed class SessionPromptQueueCoordinator : IQueuedPromptProjectionController
{
    private readonly SessionWorkspaceViewModel _workspaceViewModel;
    private readonly SessionSelectionContext _sessionSelection;
    private readonly Action<Action> _dispatchToUi;
    private readonly Action _verifyBindableAccess;
    private readonly Func<OpenSessionState, PromptSubmission, CancellationToken, Task> _dispatchQueuedPromptAsync;
    private readonly Func<OpenSessionState, PromptSubmission, CancellationToken, Task> _dispatchSteeringPromptAsync;

    public SessionPromptQueueCoordinator(
        SessionWorkspaceViewModel workspaceViewModel,
        SessionSelectionContext sessionSelection,
        Action<Action> dispatchToUi,
        Action verifyBindableAccess,
        Func<OpenSessionState, PromptSubmission, CancellationToken, Task> dispatchQueuedPromptAsync,
        Func<OpenSessionState, PromptSubmission, CancellationToken, Task> dispatchSteeringPromptAsync)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(sessionSelection);
        ArgumentNullException.ThrowIfNull(dispatchToUi);
        ArgumentNullException.ThrowIfNull(verifyBindableAccess);
        ArgumentNullException.ThrowIfNull(dispatchQueuedPromptAsync);
        ArgumentNullException.ThrowIfNull(dispatchSteeringPromptAsync);

        _workspaceViewModel = workspaceViewModel;
        _sessionSelection = sessionSelection;
        _dispatchToUi = dispatchToUi;
        _verifyBindableAccess = verifyBindableAccess;
        _dispatchQueuedPromptAsync = dispatchQueuedPromptAsync;
        _dispatchSteeringPromptAsync = dispatchSteeringPromptAsync;
    }

    public bool HasQueuedPrompts(OpenSessionState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        lock (tab.PromptStripSyncRoot)
        {
            return tab.QueuedPrompts.Count > 0;
        }
    }

    public void EnqueuePrompt(OpenSessionState tab, string prompt)
        => EnqueuePrompt(tab, PromptSubmission.TextOnly(prompt));

    public void EnqueuePrompt(OpenSessionState tab, PromptSubmission prompt)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(prompt);
        if (!prompt.HasContent)
        {
            throw new ArgumentException("Queued prompt text or image attachments are required.", nameof(prompt));
        }

        lock (tab.PromptStripSyncRoot)
        {
            tab.QueuedPrompts.Add(new QueuedSessionPrompt(prompt));
        }

        RefreshSelectedSessionQueueUi();
    }

    public void ClearSelectedSessionQueue()
    {
        if (TryGetSelectedTabWithQueue(out var tab) && tab is not null)
        {
            ClearQueue(tab);
        }
    }

    public void DeleteSelectedSessionQueuedPrompt(string queuedPromptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queuedPromptId);

        if (!TryGetSelectedTabWithQueue(out var tab) || tab is null)
        {
            return;
        }

        lock (tab.PromptStripSyncRoot)
        {
            var index = FindQueuedPromptIndex(tab, queuedPromptId);
            if (index >= 0)
            {
                tab.QueuedPrompts.RemoveAt(index);
            }
        }

        RefreshSelectedSessionQueueUi();
    }

    public void DeleteSelectedSessionPendingSteer(string pendingSteerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingSteerId);

        if (!TryGetSelectedTabWithQueue(out var tab) || tab is null)
        {
            return;
        }

        RemovePendingSteer(tab, pendingSteerId);
    }

    public void UpdateSelectedSessionQueuedPromptCount(string queuedPromptId, int remainingCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queuedPromptId);

        if (remainingCount <= 0 || !TryGetSelectedTabWithQueue(out var tab) || tab is null)
        {
            return;
        }

        lock (tab.PromptStripSyncRoot)
        {
            var queuedPrompt = FindQueuedPrompt(tab, queuedPromptId);
            queuedPrompt?.UpdateRemainingCount(remainingCount);
        }

        RefreshSelectedSessionQueueUi();
    }

    public void UpdateSelectedSessionQueuedPromptText(string queuedPromptId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queuedPromptId);
        ArgumentNullException.ThrowIfNull(text);

        if (!TryGetSelectedTabWithQueue(out var tab) || tab is null)
        {
            return;
        }

        lock (tab.PromptStripSyncRoot)
        {
            var queuedPrompt = FindQueuedPrompt(tab, queuedPromptId);
            queuedPrompt?.UpdateText(text);
        }

        RefreshSelectedSessionQueueUi();
    }

    public async Task ConvertSelectedSessionQueuedPromptToSteerAsync(string queuedPromptId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queuedPromptId);

        if (!TryGetSelectedTabWithQueue(out var tab) ||
            tab is null ||
            !TryDequeueQueuedPrompt(tab, queuedPromptId, out var queuedPrompt))
        {
            return;
        }

        RefreshSelectedSessionQueueUi();

        try
        {
            await DispatchQueuedPromptForCurrentSessionStateAsync(tab, queuedPrompt.Submission, cancellationToken);
        }
        catch
        {
            RestoreDequeuedPrompt(tab, queuedPrompt);
        }
    }

    public async Task DrainNextQueuedPromptAsync(OpenSessionState tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!TryDequeueNextQueuedPrompt(tab, out var queuedPrompt))
        {
            RefreshSelectedSessionQueueUi();
            return;
        }

        RefreshSelectedSessionQueueUi();

        try
        {
            await _dispatchQueuedPromptAsync(tab, queuedPrompt.Submission, cancellationToken);
        }
        catch
        {
            RestoreDequeuedPrompt(tab, queuedPrompt);
        }
    }

    public async Task ConvertNextQueuedPromptToSteerAsync(OpenSessionState tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!TryDequeueNextQueuedPrompt(tab, out var queuedPrompt))
        {
            RefreshSelectedSessionQueueUi();
            return;
        }

        RefreshSelectedSessionQueueUi();

        try
        {
            await DispatchQueuedPromptForCurrentSessionStateAsync(tab, queuedPrompt.Submission, cancellationToken);
        }
        catch
        {
            RestoreDequeuedPrompt(tab, queuedPrompt);
        }
    }

    public void RefreshSelectedSessionQueueUi()
        => ApplyQueuedPromptProjection();

    public void ApplyQueuedPromptProjection()
        => _dispatchToUi(ApplyQueuedPromptProjectionCore);

    public string AddPendingSteer(OpenSessionState tab, string prompt)
        => AddPendingSteer(tab, PromptSubmission.TextOnly(prompt));

    public string AddPendingSteer(OpenSessionState tab, PromptSubmission prompt)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(prompt);
        if (!prompt.HasContent)
        {
            throw new ArgumentException("Pending steer prompt text or image attachments are required.", nameof(prompt));
        }

        PendingSteerPrompt pendingSteer;
        lock (tab.PromptStripSyncRoot)
        {
            pendingSteer = new PendingSteerPrompt(prompt);
            tab.PendingSteers.Add(pendingSteer);
            tab.LastObservedPendingSteerUserContentId = null;
        }

        RefreshSelectedSessionQueueUi();
        return pendingSteer.Id;
    }

    public void RemovePendingSteer(OpenSessionState tab, string pendingSteerId)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingSteerId);

        lock (tab.PromptStripSyncRoot)
        {
            var pendingSteer = FindPendingSteer(tab, pendingSteerId);
            if (pendingSteer is null)
            {
                return;
            }

            _ = tab.PendingSteers.Remove(pendingSteer);
            ResetPendingSteerTrackingIfEmpty(tab);
        }

        RefreshSelectedSessionQueueUi();
    }

    public bool ConsumePendingSteerForLiveUserContent(OpenSessionState tab, string contentId)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);

        var removed = false;
        lock (tab.PromptStripSyncRoot)
        {
            if (string.Equals(tab.LastObservedPendingSteerUserContentId, contentId, StringComparison.Ordinal))
            {
                return false;
            }

            tab.LastObservedPendingSteerUserContentId = contentId;
            if (tab.PendingSteers.Count == 0)
            {
                return false;
            }

            tab.PendingSteers.RemoveAt(0);
            ResetPendingSteerTrackingIfEmpty(tab);
            removed = true;
        }

        if (removed)
        {
            RefreshSelectedSessionQueueUi();
        }

        return removed;
    }

    public void ClearPendingSteers(OpenSessionState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        var cleared = false;
        lock (tab.PromptStripSyncRoot)
        {
            if (tab.PendingSteers.Count == 0)
            {
                tab.LastObservedPendingSteerUserContentId = null;
                return;
            }

            tab.PendingSteers.Clear();
            tab.LastObservedPendingSteerUserContentId = null;
            cleared = true;
        }

        if (cleared)
        {
            RefreshSelectedSessionQueueUi();
        }
    }

    private void ApplyQueuedPromptProjectionCore()
    {
        _verifyBindableAccess();

        var selectedSession = _sessionSelection.GetSelectedSession();
        var tab = selectedSession is null ? null : _sessionSelection.FindOpenSession(selectedSession.SessionId);
        var projection = QueuedPromptListProjectionBuilder.Build(tab);
        _workspaceViewModel.SetPromptStripItems(projection.Items, projection.HasQueuedPrompts);
    }

    private void ClearQueue(OpenSessionState tab)
    {
        lock (tab.PromptStripSyncRoot)
        {
            tab.QueuedPrompts.Clear();
        }

        RefreshSelectedSessionQueueUi();
    }

    private Task DispatchQueuedPromptForCurrentSessionStateAsync(OpenSessionState tab, PromptSubmission prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(prompt);
        if (!prompt.HasContent)
        {
            throw new ArgumentException("Prompt text or image attachments are required.", nameof(prompt));
        }

        return _dispatchSteeringPromptAsync(tab, prompt, cancellationToken);
    }

    private bool TryGetSelectedTabWithQueue(out OpenSessionState? tab)
    {
        tab = null;

        var selectedSession = _sessionSelection.GetSelectedSession();
        if (selectedSession is null)
        {
            return false;
        }

        tab = _sessionSelection.FindOpenSession(selectedSession.SessionId);
        return tab is not null;
    }

    private static int FindQueuedPromptIndex(OpenSessionState tab, string queuedPromptId)
    {
        for (var index = 0; index < tab.QueuedPrompts.Count; index++)
        {
            if (string.Equals(tab.QueuedPrompts[index].Id, queuedPromptId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static QueuedSessionPrompt? FindQueuedPrompt(OpenSessionState tab, string queuedPromptId)
    {
        return tab.QueuedPrompts.FirstOrDefault(prompt => string.Equals(prompt.Id, queuedPromptId, StringComparison.Ordinal));
    }

    private static bool TryDequeueQueuedPrompt(OpenSessionState tab, string queuedPromptId, out QueuedPromptSnapshot queuedPrompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queuedPromptId);

        lock (tab.PromptStripSyncRoot)
        {
            var queueIndex = FindQueuedPromptIndex(tab, queuedPromptId);
            if (queueIndex < 0)
            {
                queuedPrompt = default;
                return false;
            }

            queuedPrompt = DequeueQueuedPromptAt(tab, queueIndex);
            return true;
        }
    }

    private static bool TryDequeueNextQueuedPrompt(OpenSessionState tab, out QueuedPromptSnapshot queuedPrompt)
    {
        lock (tab.PromptStripSyncRoot)
        {
            if (tab.QueuedPrompts.Count == 0)
            {
                queuedPrompt = default;
                return false;
            }

            queuedPrompt = DequeueQueuedPromptAt(tab, queueIndex: 0);
            return true;
        }
    }

    private static QueuedPromptSnapshot DequeueQueuedPromptAt(OpenSessionState tab, int queueIndex)
    {
        var existing = tab.QueuedPrompts[queueIndex];
        var queuedPrompt = new QueuedPromptSnapshot(
            existing.Id,
            existing.Submission.Copy(),
            queueIndex);
        if (existing.RemainingCount > 1)
        {
            existing.UpdateRemainingCount(existing.RemainingCount - 1);
        }
        else
        {
            tab.QueuedPrompts.RemoveAt(queueIndex);
        }

        return queuedPrompt;
    }

    private void RestoreDequeuedPrompt(OpenSessionState tab, QueuedPromptSnapshot queuedPrompt)
    {
        lock (tab.PromptStripSyncRoot)
        {
            var existing = FindQueuedPrompt(tab, queuedPrompt.Id);
            if (existing is not null)
            {
                existing.UpdateRemainingCount(existing.RemainingCount + 1);
            }
            else
            {
                var queueIndex = Math.Clamp(queuedPrompt.QueueIndex, 0, tab.QueuedPrompts.Count);
                tab.QueuedPrompts.Insert(queueIndex, new QueuedSessionPrompt(queuedPrompt.Submission));
            }
        }

        RefreshSelectedSessionQueueUi();
    }

    private static PendingSteerPrompt? FindPendingSteer(OpenSessionState tab, string pendingSteerId)
        => tab.PendingSteers.FirstOrDefault(prompt => string.Equals(prompt.Id, pendingSteerId, StringComparison.Ordinal));

    private static void ResetPendingSteerTrackingIfEmpty(OpenSessionState tab)
    {
        if (tab.PendingSteers.Count == 0)
        {
            tab.LastObservedPendingSteerUserContentId = null;
        }
    }

    private readonly record struct QueuedPromptSnapshot(
        string Id,
        PromptSubmission Submission,
        int QueueIndex);
}
