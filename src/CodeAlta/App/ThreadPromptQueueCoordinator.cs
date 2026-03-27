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
    private readonly Action _updatePromptAvailabilityUi;
    private readonly Action<Action> _dispatchToUi;
    private readonly Action _verifyBindableAccess;
    private readonly Func<OpenThreadState, string, CancellationToken, Task> _dispatchQueuedPromptAsync;
    private readonly Func<OpenThreadState, string, CancellationToken, Task> _dispatchSteeringPromptAsync;

    public ThreadPromptQueueCoordinator(
        ThreadWorkspaceViewModel workspaceViewModel,
        ThreadSelectionContext threadSelection,
        Action updatePromptAvailabilityUi,
        Action<Action> dispatchToUi,
        Action verifyBindableAccess,
        Func<OpenThreadState, string, CancellationToken, Task> dispatchQueuedPromptAsync,
        Func<OpenThreadState, string, CancellationToken, Task> dispatchSteeringPromptAsync)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(threadSelection);
        ArgumentNullException.ThrowIfNull(updatePromptAvailabilityUi);
        ArgumentNullException.ThrowIfNull(dispatchToUi);
        ArgumentNullException.ThrowIfNull(verifyBindableAccess);
        ArgumentNullException.ThrowIfNull(dispatchQueuedPromptAsync);
        ArgumentNullException.ThrowIfNull(dispatchSteeringPromptAsync);

        _workspaceViewModel = workspaceViewModel;
        _threadSelection = threadSelection;
        _updatePromptAvailabilityUi = updatePromptAvailabilityUi;
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
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

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
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

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
            !TrySnapshotQueuedPrompt(tab, queuedPromptId, out var queuedPrompt))
        {
            return;
        }

        try
        {
            await DispatchQueuedPromptForCurrentThreadStateAsync(tab, queuedPrompt.Text, cancellationToken).ConfigureAwait(false);
            ConsumeQueuedPrompt(tab, queuedPrompt.Id);
        }
        catch
        {
        }
    }

    public async Task DrainNextQueuedPromptAsync(OpenThreadState tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!TrySnapshotNextQueuedPrompt(tab, out var queuedPrompt))
        {
            RefreshSelectedThreadQueueUi();
            return;
        }

        try
        {
            await _dispatchQueuedPromptAsync(tab, queuedPrompt.Text, cancellationToken).ConfigureAwait(false);
            ConsumeQueuedPrompt(tab, queuedPrompt.Id);
        }
        catch
        {
        }
    }

    public async Task ConvertNextQueuedPromptToSteerAsync(OpenThreadState tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!TrySnapshotNextQueuedPrompt(tab, out var queuedPrompt))
        {
            RefreshSelectedThreadQueueUi();
            return;
        }

        try
        {
            await DispatchQueuedPromptForCurrentThreadStateAsync(tab, queuedPrompt.Text, cancellationToken).ConfigureAwait(false);
            ConsumeQueuedPrompt(tab, queuedPrompt.Id);
        }
        catch
        {
        }
    }

    public void RefreshSelectedThreadQueueUi()
        => _dispatchToUi(RefreshSelectedThreadQueueUiCore);

    public string AddPendingSteer(OpenThreadState tab, string prompt)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

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
        _updatePromptAvailabilityUi();
    }

    private void ClearQueue(OpenThreadState tab)
    {
        lock (tab.PromptStripSyncRoot)
        {
            tab.QueuedPrompts.Clear();
        }

        RefreshSelectedThreadQueueUi();
    }

    private Task DispatchQueuedPromptForCurrentThreadStateAsync(OpenThreadState tab, string prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        return tab.ActiveRunId is not null
            ? _dispatchSteeringPromptAsync(tab, prompt, cancellationToken)
            : _dispatchQueuedPromptAsync(tab, prompt, cancellationToken);
    }

    private void ConsumeQueuedPrompt(OpenThreadState tab, string queuedPromptId)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(queuedPromptId);

        lock (tab.PromptStripSyncRoot)
        {
            var queuedPrompt = FindQueuedPrompt(tab, queuedPromptId);
            if (queuedPrompt is null)
            {
                return;
            }

            if (queuedPrompt.RemainingCount > 1)
            {
                queuedPrompt.UpdateRemainingCount(queuedPrompt.RemainingCount - 1);
            }
            else
            {
                _ = tab.QueuedPrompts.Remove(queuedPrompt);
            }
        }

        RefreshSelectedThreadQueueUi();
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

    private static bool TrySnapshotQueuedPrompt(OpenThreadState tab, string queuedPromptId, out QueuedPromptSnapshot queuedPrompt)
    {
        lock (tab.PromptStripSyncRoot)
        {
            var existing = FindQueuedPrompt(tab, queuedPromptId);
            if (existing is null)
            {
                queuedPrompt = default;
                return false;
            }

            queuedPrompt = new QueuedPromptSnapshot(existing.Id, existing.Text, existing.RemainingCount);
            return true;
        }
    }

    private static bool TrySnapshotNextQueuedPrompt(OpenThreadState tab, out QueuedPromptSnapshot queuedPrompt)
    {
        lock (tab.PromptStripSyncRoot)
        {
            if (tab.QueuedPrompts.Count == 0)
            {
                queuedPrompt = default;
                return false;
            }

            var existing = tab.QueuedPrompts[0];
            queuedPrompt = new QueuedPromptSnapshot(existing.Id, existing.Text, existing.RemainingCount);
            return true;
        }
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
        string Text,
        int RemainingCount);
}
