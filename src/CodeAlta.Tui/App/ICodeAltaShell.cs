using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tui.App;

internal interface ICodeAltaShell
{
    Task InitializeModelProvidersAsync(CancellationToken cancellationToken);

    Task InitializeModelProviderAsync(ModelProviderId providerId, CancellationToken cancellationToken);

    void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info);

    void SetProviderSessionLoadStatus(string? message);

    void ApplyRecoveredCatalogState(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<SessionViewDescriptor> sessions,
        bool pruneMissingSessions = true);

    void UpsertProject(ProjectDescriptor project);

    void SetReadyStatusForCurrentSelection();

    void HandleRuntimeEvent(SessionRuntimeEvent runtimeEvent);

    void PublishStartupCatalogProjectionReady();

    void TrySchedulePendingStartupSessionRestore(CancellationToken cancellationToken);

    void SelectGlobalScope();

    void SelectProjectScope(string projectId);

    void OpenSession(string sessionId);

    void FocusPromptEditor();

    void SetInitialized(bool isInitialized);
}
