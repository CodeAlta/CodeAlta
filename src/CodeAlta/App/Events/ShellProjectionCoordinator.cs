namespace CodeAlta.App.Events;

internal interface IProjectionInvalidator
{
    void RefreshCatalogAndThreadWorkspace();

    void RefreshSelectionAndThreadWorkspace();

    void RefreshHeaderAndThreadWorkspace();

    void RefreshShellChrome();

    void UpdatePromptAvailabilityUi();

    void RefreshQueuedPromptList();

    void InvalidateSelectedSessionUsage();
}

internal sealed class ShellProjectionCoordinator : IDisposable
{
    private readonly IDisposable _subscription;
    private readonly IProjectionInvalidator _invalidator;

    public ShellProjectionCoordinator(FrontendEventPublisher publisher, IProjectionInvalidator invalidator)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(invalidator);

        _invalidator = invalidator;
        _subscription = publisher.Subscribe(Handle);
    }

    public void Dispose()
        => _subscription.Dispose();

    private void Handle(ShellFrontendEvent frontendEvent)
    {
        switch (frontendEvent)
        {
            case CatalogChangedEvent:
                _invalidator.RefreshCatalogAndThreadWorkspace();
                break;
            case SelectionChangedEvent:
                _invalidator.RefreshSelectionAndThreadWorkspace();
                break;
            case HeaderChangedEvent:
                _invalidator.RefreshHeaderAndThreadWorkspace();
                break;
            case ShellChromeChangedEvent:
            case RuntimeTimelineChangedEvent:
                _invalidator.RefreshShellChrome();
                break;
            case ThreadStatusChangedEvent:
                _invalidator.RefreshShellChrome();
                _invalidator.UpdatePromptAvailabilityUi();
                break;
            case PromptDraftChangedEvent:
            case PromptImagesChangedEvent:
            case PromptAvailabilityChangedEvent:
            case ModelProviderStateChangedEvent:
                _invalidator.UpdatePromptAvailabilityUi();
                break;
            case ModelProviderCatalogChangedEvent:
                _invalidator.RefreshSelectionAndThreadWorkspace();
                break;
            case QueuedPromptListChangedEvent:
                _invalidator.RefreshQueuedPromptList();
                _invalidator.UpdatePromptAvailabilityUi();
                break;
            case SessionUsageChangedEvent:
                _invalidator.InvalidateSelectedSessionUsage();
                break;
            default:
                throw new InvalidOperationException($"Unsupported shell frontend event: {frontendEvent.GetType().Name}");
        }
    }
}
