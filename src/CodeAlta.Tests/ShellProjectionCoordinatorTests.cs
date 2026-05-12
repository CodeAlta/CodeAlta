using CodeAlta.App.Events;
using CodeAlta.Threading;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ShellProjectionCoordinatorTests
{
    [TestMethod]
    public void Publish_CatalogChanged_RefreshesCatalogWorkspace()
    {
        var projections = new CapturingProjectionControllers();
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        using var coordinator = new ShellProjectionCoordinator(publisher, projections, projections, projections);

        publisher.Publish(new CatalogChangedEvent());

        CollectionAssert.AreEqual(new[] { "catalog" }, projections.Calls);
    }

    [TestMethod]
    public void Publish_StartupCatalogProjectionReady_AppliesCatalogProjection()
    {
        var projections = new CapturingProjectionControllers();
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        using var coordinator = new ShellProjectionCoordinator(publisher, projections, projections, projections);

        publisher.Publish(new StartupCatalogProjectionReadyEvent());

        CollectionAssert.AreEqual(new[] { "catalog" }, projections.Calls);
    }

    [TestMethod]
    public void Publish_SelectionChanged_RefreshesSelectionWorkspace()
    {
        var projections = new CapturingProjectionControllers();
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        using var coordinator = new ShellProjectionCoordinator(publisher, projections, projections, projections);

        publisher.Publish(new SelectionChangedEvent());

        CollectionAssert.AreEqual(new[] { "selection" }, projections.Calls);
    }

    [TestMethod]
    public void Publish_TabChangedEvents_RefreshSelectionWorkspace()
    {
        var projections = new CapturingProjectionControllers();
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        using var coordinator = new ShellProjectionCoordinator(publisher, projections, projections, projections);

        publisher.Publish(new OpenTabsChangedEvent([]));
        publisher.Publish(new SelectedTabChangedEvent(null));

        CollectionAssert.AreEqual(new[] { "tabs", "tabs" }, projections.Calls);
    }

    [TestMethod]
    public void Publish_ThreadStatusChanged_RefreshesChromeAndPromptAvailability()
    {
        var projections = new CapturingProjectionControllers();
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        using var coordinator = new ShellProjectionCoordinator(publisher, projections, projections, projections);

        publisher.Publish(new ThreadStatusChangedEvent("thread-1"));

        CollectionAssert.AreEqual(new[] { "thread-status", "prompt" }, projections.Calls);
    }

    [TestMethod]
    public void Publish_ModelProviderChanged_RefreshesPromptAvailability()
    {
        var projections = new CapturingProjectionControllers();
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        using var coordinator = new ShellProjectionCoordinator(publisher, projections, projections, projections);

        publisher.Publish(new ModelProviderStateChangedEvent("provider"));

        CollectionAssert.AreEqual(new[] { "prompt" }, projections.Calls);
    }

    [TestMethod]
    public void Publish_PromptDraftChanged_RefreshesChromeWithoutWorkspaceRefresh()
    {
        var projections = new CapturingProjectionControllers();
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        using var coordinator = new ShellProjectionCoordinator(publisher, projections, projections, projections);

        publisher.Publish(new PromptDraftChangedEvent("thread-1"));

        CollectionAssert.AreEqual(new[] { "prompt-draft", "prompt" }, projections.Calls);
    }

    [TestMethod]
    public void Publish_ModelProviderCatalogChanged_RefreshesSelectionWorkspace()
    {
        var projections = new CapturingProjectionControllers();
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        using var coordinator = new ShellProjectionCoordinator(publisher, projections, projections, projections);

        publisher.Publish(new ModelProviderCatalogChangedEvent());

        CollectionAssert.AreEqual(new[] { "selection" }, projections.Calls);
    }

    [TestMethod]
    public void Publish_QueuedPromptListChanged_RefreshesQueueAndPromptAvailability()
    {
        var projections = new CapturingProjectionControllers();
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        using var coordinator = new ShellProjectionCoordinator(publisher, projections, projections, projections);

        publisher.Publish(new QueuedPromptListChangedEvent("thread-1"));

        CollectionAssert.AreEqual(new[] { "queue", "prompt" }, projections.Calls);
    }

    [TestMethod]
    public void Publish_PromptFocusRequested_FocusesPrompt()
    {
        var projections = new CapturingProjectionControllers();
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        using var coordinator = new ShellProjectionCoordinator(publisher, projections, projections, projections);

        publisher.Publish(new PromptFocusRequestedEvent());

        CollectionAssert.AreEqual(new[] { "focus" }, projections.Calls);
    }

    [TestMethod]
    public void Publish_SessionUsageChanged_InvalidatesSelectedSessionUsage()
    {
        var projections = new CapturingProjectionControllers();
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        using var coordinator = new ShellProjectionCoordinator(publisher, projections, projections, projections);

        publisher.Publish(new SessionUsageChangedEvent("thread-1"));

        CollectionAssert.AreEqual(new[] { "usage" }, projections.Calls);
    }

    [TestMethod]
    public void Publish_RuntimeTimelineChanged_InvalidatesTimelineWithoutRefreshingShellChrome()
    {
        var projections = new CapturingProjectionControllers();
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        using var coordinator = new ShellProjectionCoordinator(publisher, projections, projections, projections);

        publisher.Publish(new RuntimeTimelineChangedEvent("thread-1"));

        CollectionAssert.AreEqual(new[] { "timeline" }, projections.Calls);
    }

    private sealed class CapturingProjectionControllers :
        IWorkspaceProjectionController,
        IPromptAvailabilityProjectionController,
        IQueuedPromptProjectionController
    {
        public List<string> Calls { get; } = [];

        public void ApplyCatalogProjection() => Calls.Add("catalog");

        public void ApplySelectionProjection() => Calls.Add("selection");

        public void ApplyHeaderProjection() => Calls.Add("header");

        public void ApplyShellChromeProjection() => Calls.Add("chrome");

        public void ApplyRuntimeTimelineProjection() => Calls.Add("timeline");

        public void ApplyTabProjection() => Calls.Add("tabs");

        public void ApplyThreadStatusProjection() => Calls.Add("thread-status");

        public void ApplyPromptDraftProjection() => Calls.Add("prompt-draft");

        public void ApplySessionUsageProjection() => Calls.Add("usage");

        public void RequestPromptFocus() => Calls.Add("focus");

        public void ApplyPromptAvailabilityProjection() => Calls.Add("prompt");

        public void ApplyQueuedPromptProjection() => Calls.Add("queue");
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
        }

        public Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            return Task.FromResult(action());
        }
    }
}
