namespace CodeAlta.App.Events;

internal interface IWorkspaceProjectionController
{
    void ApplyCatalogProjection();

    void ApplySelectionProjection();

    void ApplyHeaderProjection();

    void ApplyShellChromeProjection();

    void ApplyRuntimeTimelineProjection();

    void ApplyTabProjection();

    void ApplyThreadStatusProjection();

    void ApplyPromptDraftProjection();

    void ApplySessionUsageProjection();

    void RequestPromptFocus();
}

internal interface IPromptAvailabilityProjectionController
{
    void ApplyPromptAvailabilityProjection();
}

internal interface IQueuedPromptProjectionController
{
    void ApplyQueuedPromptProjection();
}

internal sealed class ShellProjectionCoordinator : IDisposable
{
    private readonly IDisposable _subscription;
    private readonly IWorkspaceProjectionController _workspaceProjections;
    private readonly IPromptAvailabilityProjectionController _promptAvailabilityProjection;
    private readonly IQueuedPromptProjectionController _queuedPromptProjection;

    public ShellProjectionCoordinator(
        FrontendEventPublisher publisher,
        IWorkspaceProjectionController workspaceProjections,
        IPromptAvailabilityProjectionController promptAvailabilityProjection,
        IQueuedPromptProjectionController queuedPromptProjection)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(workspaceProjections);
        ArgumentNullException.ThrowIfNull(promptAvailabilityProjection);
        ArgumentNullException.ThrowIfNull(queuedPromptProjection);

        _workspaceProjections = workspaceProjections;
        _promptAvailabilityProjection = promptAvailabilityProjection;
        _queuedPromptProjection = queuedPromptProjection;
        _subscription = publisher.Subscribe(Handle);
    }

    public void Dispose()
        => _subscription.Dispose();

    private void Handle(ShellFrontendEvent frontendEvent)
    {
        switch (frontendEvent)
        {
            case CatalogChangedEvent:
            case StartupCatalogProjectionReadyEvent:
                _workspaceProjections.ApplyCatalogProjection();
                break;
            case SelectionChangedEvent:
                _workspaceProjections.ApplySelectionProjection();
                break;
            case OpenTabsChangedEvent:
            case SelectedTabChangedEvent:
                _workspaceProjections.ApplyTabProjection();
                break;
            case HeaderChangedEvent:
                _workspaceProjections.ApplyHeaderProjection();
                break;
            case ShellChromeChangedEvent:
                _workspaceProjections.ApplyShellChromeProjection();
                break;
            case RuntimeTimelineChangedEvent:
                _workspaceProjections.ApplyRuntimeTimelineProjection();
                break;
            case ThreadStatusChangedEvent:
                _workspaceProjections.ApplyThreadStatusProjection();
                _promptAvailabilityProjection.ApplyPromptAvailabilityProjection();
                break;
            case PromptDraftChangedEvent:
            case PromptImagesChangedEvent:
                _workspaceProjections.ApplyPromptDraftProjection();
                _promptAvailabilityProjection.ApplyPromptAvailabilityProjection();
                break;
            case PromptAvailabilityChangedEvent:
            case ModelProviderStateChangedEvent:
                _promptAvailabilityProjection.ApplyPromptAvailabilityProjection();
                break;
            case PromptFocusRequestedEvent:
                _workspaceProjections.RequestPromptFocus();
                break;
            case ModelProviderCatalogChangedEvent:
                _workspaceProjections.ApplySelectionProjection();
                break;
            case QueuedPromptListChangedEvent:
                _queuedPromptProjection.ApplyQueuedPromptProjection();
                _promptAvailabilityProjection.ApplyPromptAvailabilityProjection();
                break;
            case SessionUsageChangedEvent:
                _workspaceProjections.ApplySessionUsageProjection();
                break;
            default:
                throw new InvalidOperationException($"Unsupported shell frontend event: {frontendEvent.GetType().Name}");
        }
    }
}
