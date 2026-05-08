using CodeAlta.App.Context;
using CodeAlta.App.State;
using CodeAlta.Models;
using CodeAlta.Presentation.Prompting;
using CodeAlta.ViewModels;

namespace CodeAlta.App;

internal sealed class ThreadPromptQueueCoordinator
{
    private readonly ThreadWorkspaceViewModel _workspaceViewModel;
    private readonly ThreadSelectionContext _threadSelection;
    private readonly Action<Action> _dispatchToUi;
    private readonly Action _verifyBindableAccess;
    private readonly Func<OpenThreadState, PromptSubmission, CancellationToken, Task> _dispatchQueuedPromptAsync;
    private readonly Func<OpenThreadState, PromptSubmission, CancellationToken, Task> _dispatchSteeringPromptAsync;

    public ThreadPromptQueueCoordinator(
        ThreadWorkspaceViewModel workspaceViewModel,
        ThreadSelectionContext threadSelection,
        Action<Action> dispatchToUi,
        Action verifyBindableAccess,
        Func<OpenThreadState, PromptSubmission, CancellationToken, Task> dispatchQueuedPromptAsync,
        Func<OpenThreadState, PromptSubmission, CancellationToken, Task> dispatchSteeringPromptAsync)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(threadSelection);
        ArgumentNullException.ThrowIfNull(dispatchToUi);
        ArgumentNullException.ThrowIfNull(verifyBindableAccess);
        ArgumentNullException.ThrowIfNull(dispatchQueuedPromptAsync);
        ArgumentNullException.ThrowIfNull(dispatchSteeringPromptAsync);

        _workspaceViewModel = workspaceViewModel;
        _threadSelection = threadSelection;
        _dispatchToUi = dispatchToUi;
        _verifyBindableAccess = verifyBindableAccess;
        _dispatchQueuedPromptAsync = dispatchQueuedPromptAsync;
        _dispatchSteeringPromptAsync = dispatchSteeringPromptAsync;
    }

    public bool HasQueuedPrompts(OpenThreadState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        lock (tab.PromptStripSyncRoot)
        {
            return tab.QueuedPrompts.Count > 0;
        }
    }

    public void EnqueuePrompt(OpenThreadState tab, string prompt)
        => EnqueuePrompt(tab, PromptSubmission.TextOnly(prompt));

    public void EnqueuePrompt(OpenThreadState tab, PromptSubmission prompt)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(prompt);
        if (!prompt.HasContent)
        {
            throw new ArgumentException("Queued prompt text or image attachments are required.", nameof(prompt));
        }

        lock (tab.PromptStripSyncRoot)
        {
            tab.QueuedPrompts.Add(new QueuedThreadPrompt(prompt));
        }

        RefreshSelectedThreadQueueUi();
    }

    public void ClearSelectedThreadQueue()
    {
        if (TryGetSelectedTabWithQueue(out var tab) && tab is not null)
        {
            ClearQueue(tab);
        }
    }

    public void DeleteSelectedThreadQueuedPrompt(string queuedPromptId)
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

        RefreshSelectedThreadQueueUi();
    }

    public void DeleteSelectedThreadPendingSteer(string pendingSteerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingSteerId);

        if (!TryGetSelectedTabWithQueue(out var tab) || tab is null)
        {
            return;
        }

        RemovePendingSteer(tab, pendingSteerId);
    }

    public void UpdateSelectedThreadQueuedPromptCount(string queuedPromptId, int remainingCount)
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

        RefreshSelectedThreadQueueUi();
    }

    public void UpdateSelectedThreadQueuedPromptText(string queuedPromptId, string text)
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

        RefreshSelectedThreadQueueUi();
    }

    public async Task ConvertSelectedThreadQueuedPromptToSteerAsync(string queuedPromptId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queuedPromptId);

        if (!TryGetSelectedTabWithQueue(out var tab) ||
            tab is null ||
            !TryDequeueQueuedPrompt(tab, queuedPromptId, out var queuedPrompt))
        {
            return;
        }

        RefreshSelectedThreadQueueUi();

        try
        {
            await DispatchQueuedPromptForCurrentThreadStateAsync(tab, queuedPrompt.Submission, cancellationToken);
        }
        catch
        {
            RestoreDequeuedPrompt(tab, queuedPrompt);
        }
    }

    public async Task DrainNextQueuedPromptAsync(OpenThreadState tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!TryDequeueNextQueuedPrompt(tab, out var queuedPrompt))
        {
            RefreshSelectedThreadQueueUi();
            return;
        }

        RefreshSelectedThreadQueueUi();

        try
        {
            await _dispatchQueuedPromptAsync(tab, queuedPrompt.Submission, cancellationToken);
        }
        catch
        {
            RestoreDequeuedPrompt(tab, queuedPrompt);
        }
    }

    public async Task ConvertNextQueuedPromptToSteerAsync(OpenThreadState tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!TryDequeueNextQueuedPrompt(tab, out var queuedPrompt))
        {
            RefreshSelectedThreadQueueUi();
            return;
        }

        RefreshSelectedThreadQueueUi();

        try
        {
            await DispatchQueuedPromptForCurrentThreadStateAsync(tab, queuedPrompt.Submission, cancellationToken);
        }
        catch
        {
            RestoreDequeuedPrompt(tab, queuedPrompt);
        }
    }

    public void RefreshSelectedThreadQueueUi()
        => _dispatchToUi(RefreshSelectedThreadQueueUiCore);

    public string AddPendingSteer(OpenThreadState tab, string prompt)
        => AddPendingSteer(tab, PromptSubmission.TextOnly(prompt));

    public string AddPendingSteer(OpenThreadState tab, PromptSubmission prompt)
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

        RefreshSelectedThreadQueueUi();
        return pendingSteer.Id;
    }

    public void RemovePendingSteer(OpenThreadState tab, string pendingSteerId)
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

        RefreshSelectedThreadQueueUi();
    }

    public bool ConsumePendingSteerForLiveUserContent(OpenThreadState tab, string contentId)
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
            RefreshSelectedThreadQueueUi();
        }

        return removed;
    }

    public void ClearPendingSteers(OpenThreadState tab)
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
            RefreshSelectedThreadQueueUi();
        }
    }

    private void RefreshSelectedThreadQueueUiCore()
    {
        _verifyBindableAccess();

        var selectedThread = _threadSelection.GetSelectedThread();
        var tab = selectedThread is null ? null : _threadSelection.FindOpenThread(selectedThread.ThreadId);
        var projection = QueuedPromptListProjectionBuilder.Build(tab);
        _workspaceViewModel.SetPromptStripItems(projection.Items, projection.HasQueuedPrompts);
    }

    private void ClearQueue(OpenThreadState tab)
    {
        lock (tab.PromptStripSyncRoot)
        {
            tab.QueuedPrompts.Clear();
        }

        RefreshSelectedThreadQueueUi();
    }

    private Task DispatchQueuedPromptForCurrentThreadStateAsync(OpenThreadState tab, PromptSubmission prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(prompt);
        if (!prompt.HasContent)
        {
            throw new ArgumentException("Prompt text or image attachments are required.", nameof(prompt));
        }

        return _dispatchSteeringPromptAsync(tab, prompt, cancellationToken);
    }

    private bool TryGetSelectedTabWithQueue(out OpenThreadState? tab)
    {
        tab = null;

        var selectedThread = _threadSelection.GetSelectedThread();
        if (selectedThread is null)
        {
            return false;
        }

        tab = _threadSelection.FindOpenThread(selectedThread.ThreadId);
        return tab is not null;
    }

    private static int FindQueuedPromptIndex(OpenThreadState tab, string queuedPromptId)
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

    private static QueuedThreadPrompt? FindQueuedPrompt(OpenThreadState tab, string queuedPromptId)
    {
        return tab.QueuedPrompts.FirstOrDefault(prompt => string.Equals(prompt.Id, queuedPromptId, StringComparison.Ordinal));
    }

    private static bool TryDequeueQueuedPrompt(OpenThreadState tab, string queuedPromptId, out QueuedPromptSnapshot queuedPrompt)
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

    private static bool TryDequeueNextQueuedPrompt(OpenThreadState tab, out QueuedPromptSnapshot queuedPrompt)
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

    private static QueuedPromptSnapshot DequeueQueuedPromptAt(OpenThreadState tab, int queueIndex)
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

    private void RestoreDequeuedPrompt(OpenThreadState tab, QueuedPromptSnapshot queuedPrompt)
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
                tab.QueuedPrompts.Insert(queueIndex, new QueuedThreadPrompt(queuedPrompt.Submission));
            }
        }

        RefreshSelectedThreadQueueUi();
    }

    private static PendingSteerPrompt? FindPendingSteer(OpenThreadState tab, string pendingSteerId)
        => tab.PendingSteers.FirstOrDefault(prompt => string.Equals(prompt.Id, pendingSteerId, StringComparison.Ordinal));

    private static void ResetPendingSteerTrackingIfEmpty(OpenThreadState tab)
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
