using CodeAlta.Agent;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Threading;
using CodeAlta.Views;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.App;

internal interface IThreadStateFrontendPort
{
    Rectangle? GetTimelineBounds();

    bool IsModelProviderReady(WorkThreadDescriptor thread);

    string? LoadPromptDraft(string threadId);

    void DeletePromptDraft(string threadId);

    void ApplyThreadPreference(OpenThreadState thread);

    void RememberThreadPreference(string threadId, string? modelId, AgentReasoningEffort? reasoningEffort, bool persistNow);

    Task EnsureThreadHistoryLoadedAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken);

    void ResetPendingThreadTabSelection();

    void RemoveThreadTabPage(string threadId);
}

internal sealed class CodeAltaThreadStateFrontendPort : IThreadStateFrontendPort
{
    private readonly CodeAltaApp _app;

    public CodeAltaThreadStateFrontendPort(CodeAltaApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _app = app;
    }

    public Rectangle? GetTimelineBounds() => _app.ThreadPaneLayout?.GetAbsoluteBounds();

    public bool IsModelProviderReady(WorkThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        return _app.IsModelProviderReady(new AgentBackendId(thread.BackendId));
    }

    public string? LoadPromptDraft(string threadId) => _app.LoadPromptDraft(threadId);

    public void DeletePromptDraft(string threadId) => _app.DeletePromptDraft(threadId);

    public void ApplyThreadPreference(OpenThreadState thread) => _app.ApplyThreadPreference(thread);

    public void RememberThreadPreference(string threadId, string? modelId, AgentReasoningEffort? reasoningEffort, bool persistNow)
        => _app.RememberThreadPreference(threadId, modelId, reasoningEffort, persistNow);

    public Task EnsureThreadHistoryLoadedAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken)
        => _app.EnsureThreadHistoryLoadedAsync(thread, cancellationToken);

    public void ResetPendingThreadTabSelection() => _app.ResetPendingThreadTabSelection();

    public void RemoveThreadTabPage(string threadId) => _app.RemoveThreadTabPage(threadId);
}
