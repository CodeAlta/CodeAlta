using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Threading;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Tests;

internal sealed class TestThreadStateFrontendPort : IThreadStateFrontendPort
{
    private readonly Func<Rectangle?> _getTimelineBounds;
    private readonly Func<WorkThreadDescriptor, bool> _isModelProviderReady;
    private readonly Func<string, string?> _loadPromptDraft;
    private readonly Action<string> _deletePromptDraft;
    private readonly Action<OpenThreadState> _applyThreadPreference;
    private readonly Action<string, string?, AgentReasoningEffort?, bool> _rememberThreadPreference;
    private readonly Func<WorkThreadDescriptor, CancellationToken, Task> _ensureThreadHistoryLoadedAsync;
    private readonly Action _resetPendingThreadTabSelection;
    private readonly Action<string, ShellTabCloseReason> _removeThreadTabPage;

    public TestThreadStateFrontendPort(
        Func<Rectangle?>? getTimelineBounds = null,
        Func<WorkThreadDescriptor, bool>? isModelProviderReady = null,
        Func<string, string?>? loadPromptDraft = null,
        Action<string>? deletePromptDraft = null,
        Action<OpenThreadState>? applyThreadPreference = null,
        Action<string, string?, AgentReasoningEffort?, bool>? rememberThreadPreference = null,
        Func<WorkThreadDescriptor, CancellationToken, Task>? ensureThreadHistoryLoadedAsync = null,
        Action? resetPendingThreadTabSelection = null,
        Action<string, ShellTabCloseReason>? removeThreadTabPage = null)
    {
        _getTimelineBounds = getTimelineBounds ?? (static () => null);
        _isModelProviderReady = isModelProviderReady ?? (static _ => true);
        _loadPromptDraft = loadPromptDraft ?? (static _ => null);
        _deletePromptDraft = deletePromptDraft ?? (static _ => { });
        _applyThreadPreference = applyThreadPreference ?? (static _ => { });
        _rememberThreadPreference = rememberThreadPreference ?? (static (_, _, _, _) => { });
        _ensureThreadHistoryLoadedAsync = ensureThreadHistoryLoadedAsync ?? (static (_, _) => Task.CompletedTask);
        _resetPendingThreadTabSelection = resetPendingThreadTabSelection ?? (static () => { });
        _removeThreadTabPage = removeThreadTabPage ?? (static (_, _) => { });
    }

    public Rectangle? GetTimelineBounds() => _getTimelineBounds();

    public bool IsModelProviderReady(WorkThreadDescriptor thread) => _isModelProviderReady(thread);

    public string? LoadPromptDraft(string threadId) => _loadPromptDraft(threadId);

    public void DeletePromptDraft(string threadId) => _deletePromptDraft(threadId);

    public void ApplyThreadPreference(OpenThreadState thread) => _applyThreadPreference(thread);

    public void RememberThreadPreference(string threadId, string? modelId, AgentReasoningEffort? reasoningEffort, bool persistNow)
        => _rememberThreadPreference(threadId, modelId, reasoningEffort, persistNow);

    public Task EnsureThreadHistoryLoadedAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken)
        => _ensureThreadHistoryLoadedAsync(thread, cancellationToken);

    public void ResetPendingThreadTabSelection() => _resetPendingThreadTabSelection();

    public void RemoveThreadTabPage(string threadId, ShellTabCloseReason reason) => _removeThreadTabPage(threadId, reason);
}
