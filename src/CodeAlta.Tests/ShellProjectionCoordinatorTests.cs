using CodeAlta.App.Events;
using CodeAlta.Threading;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ShellProjectionCoordinatorTests
{
    [TestMethod]
    public void Publish_CatalogChanged_RefreshesCatalogWorkspace()
    {
        var invalidator = new CapturingProjectionInvalidator();
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        using var coordinator = new ShellProjectionCoordinator(publisher, invalidator);

        publisher.Publish(new CatalogChangedEvent());

        CollectionAssert.AreEqual(new[] { "catalog" }, invalidator.Calls);
    }

    [TestMethod]
    public void Publish_SelectionChanged_RefreshesSelectionWorkspace()
    {
        var invalidator = new CapturingProjectionInvalidator();
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        using var coordinator = new ShellProjectionCoordinator(publisher, invalidator);

        publisher.Publish(new SelectionChangedEvent());

        CollectionAssert.AreEqual(new[] { "selection" }, invalidator.Calls);
    }

    [TestMethod]
    public void Publish_ThreadStatusChanged_RefreshesChromeAndPromptAvailability()
    {
        var invalidator = new CapturingProjectionInvalidator();
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        using var coordinator = new ShellProjectionCoordinator(publisher, invalidator);

        publisher.Publish(new ThreadStatusChangedEvent("thread-1"));

        CollectionAssert.AreEqual(new[] { "chrome", "prompt" }, invalidator.Calls);
    }

    [TestMethod]
    public void Publish_ModelProviderChanged_RefreshesPromptAvailability()
    {
        var invalidator = new CapturingProjectionInvalidator();
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        using var coordinator = new ShellProjectionCoordinator(publisher, invalidator);

        publisher.Publish(new ModelProviderStateChangedEvent("provider"));

        CollectionAssert.AreEqual(new[] { "prompt" }, invalidator.Calls);
    }

    [TestMethod]
    public void Publish_ModelProviderCatalogChanged_RefreshesSelectionWorkspace()
    {
        var invalidator = new CapturingProjectionInvalidator();
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        using var coordinator = new ShellProjectionCoordinator(publisher, invalidator);

        publisher.Publish(new ModelProviderCatalogChangedEvent());

        CollectionAssert.AreEqual(new[] { "selection" }, invalidator.Calls);
    }

    [TestMethod]
    public void Publish_QueuedPromptListChanged_RefreshesQueueAndPromptAvailability()
    {
        var invalidator = new CapturingProjectionInvalidator();
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        using var coordinator = new ShellProjectionCoordinator(publisher, invalidator);

        publisher.Publish(new QueuedPromptListChangedEvent("thread-1"));

        CollectionAssert.AreEqual(new[] { "queue", "prompt" }, invalidator.Calls);
    }

    [TestMethod]
    public void Publish_SessionUsageChanged_InvalidatesSelectedSessionUsage()
    {
        var invalidator = new CapturingProjectionInvalidator();
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        using var coordinator = new ShellProjectionCoordinator(publisher, invalidator);

        publisher.Publish(new SessionUsageChangedEvent("thread-1"));

        CollectionAssert.AreEqual(new[] { "usage" }, invalidator.Calls);
    }

    private sealed class CapturingProjectionInvalidator : IProjectionInvalidator
    {
        public List<string> Calls { get; } = [];

        public void RefreshCatalogAndThreadWorkspace() => Calls.Add("catalog");

        public void RefreshSelectionAndThreadWorkspace() => Calls.Add("selection");

        public void RefreshHeaderAndThreadWorkspace() => Calls.Add("header");

        public void RefreshShellChrome() => Calls.Add("chrome");

        public void UpdatePromptAvailabilityUi() => Calls.Add("prompt");

        public void RefreshQueuedPromptList() => Calls.Add("queue");

        public void InvalidateSelectedSessionUsage() => Calls.Add("usage");
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
