using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Views;

namespace CodeAlta.App;

internal sealed class CodeAltaShellBridge : ICodeAltaShell
{
    private readonly CodeAltaApp _app;

    public CodeAltaShellBridge(CodeAltaApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _app = app;
    }

    public Task InitializeChatBackendsAsync(CancellationToken cancellationToken)
        => _app.InitializeChatBackendsAsync(cancellationToken);

    public void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info)
        => _app.SetStatus(message, showSpinner, tone);

    public void ApplyRecoveredCatalogState(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<WorkThreadDescriptor> threads)
        => _app.ApplyRecoveredCatalogState(projects, threads);

    public void SetReadyStatusForCurrentSelection()
        => _app.SetReadyStatusForCurrentSelection();

    public void HandleRuntimeEvent(WorkThreadRuntimeEvent runtimeEvent)
        => _app.HandleRuntimeEvent(runtimeEvent);

    public void RefreshCatalogAndThreadWorkspace()
        => _app.RefreshCatalogAndThreadWorkspace();

    public void TrySchedulePendingStartupThreadRestore(CancellationToken cancellationToken)
        => _app.TrySchedulePendingStartupThreadRestore(cancellationToken);

    public void SelectGlobalScope()
        => _app.SelectGlobalScope();

    public void SelectProjectScope(string projectId)
        => _app.SelectProjectScope(projectId);

    public void OpenThread(string threadId)
        => _app.OpenThread(threadId);

    public void SetInitialized(bool isInitialized)
        => _app.SetShellInitialized(isInitialized);
}