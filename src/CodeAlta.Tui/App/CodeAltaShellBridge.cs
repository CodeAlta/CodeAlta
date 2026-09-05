using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Tui.Views;

namespace CodeAlta.Tui.App;

internal sealed class CodeAltaShellBridge : ICodeAltaShell
{
    private readonly CodeAltaApp _app;

    public CodeAltaShellBridge(CodeAltaApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _app = app;
    }

    public Task InitializeModelProvidersAsync(CancellationToken cancellationToken)
        => _app.InitializeModelProvidersAsync(cancellationToken);

    public Task InitializeModelProviderAsync(ModelProviderId providerId, CancellationToken cancellationToken)
        => _app.InitializeModelProviderAsync(providerId, cancellationToken);

    public void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info)
        => _app.SetStatus(message, showSpinner, tone);

    public void SetProviderSessionLoadStatus(string? message)
        => _app.SetProviderSessionLoadStatus(message);

    public void ApplyRecoveredCatalogState(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<SessionViewDescriptor> sessions,
        bool pruneMissingSessions = true)
        => _app.ApplyRecoveredCatalogState(projects, sessions, pruneMissingSessions);

    public void UpsertProject(ProjectDescriptor project)
        => _app.UpsertProject(project);

    public void SetReadyStatusForCurrentSelection()
        => _app.SetReadyStatusForCurrentSelection();

    public void HandleRuntimeEvent(SessionRuntimeEvent runtimeEvent)
        => _app.HandleRuntimeEvent(runtimeEvent);

    public void PublishStartupCatalogProjectionReady()
        => _app.PublishStartupCatalogProjectionReady();

    public void TrySchedulePendingStartupSessionRestore(CancellationToken cancellationToken)
        => _app.TrySchedulePendingStartupSessionRestore(cancellationToken);

    public void SelectGlobalScope()
        => _app.SelectGlobalScope();

    public void SelectProjectScope(string projectId)
        => _app.SelectProjectScope(projectId);

    public void OpenSession(string sessionId)
        => _app.OpenSession(sessionId);

    public void FocusPromptEditor()
        => _app.FocusPromptEditor();

    public void SetInitialized(bool isInitialized)
        => _app.SetShellInitialized(isInitialized);
}
