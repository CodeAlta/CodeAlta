using CodeAlta.Tui.App.Events;
using CodeAlta.Tui.Threading;

namespace CodeAlta.Tests;

[TestClass]
public sealed class FrontendEventPublisherTests
{
    [TestMethod]
    public void Publish_PostsThroughUiDispatcherWhenCalledOffUiSession()
    {
        var uiDispatcher = new CapturingUiDispatcher(hasAccess: false);
        var publisher = new FrontendEventPublisher(uiDispatcher);
        ShellFrontendEvent? observed = null;
        publisher.Subscribe(frontendEvent => observed = frontendEvent);

        publisher.Publish(new CatalogChangedEvent());

        Assert.IsTrue(uiDispatcher.PostCalled);
        Assert.IsInstanceOfType<CatalogChangedEvent>(observed);
    }

    [TestMethod]
    public void Publish_NotifiesSubscribersInlineWhenCalledOnUiSession()
    {
        var uiDispatcher = new CapturingUiDispatcher(hasAccess: true);
        var publisher = new FrontendEventPublisher(uiDispatcher);
        ShellFrontendEvent? observed = null;
        publisher.Subscribe(frontendEvent => observed = frontendEvent);

        publisher.Publish(new PromptAvailabilityChangedEvent());

        Assert.IsFalse(uiDispatcher.PostCalled);
        Assert.IsInstanceOfType<PromptAvailabilityChangedEvent>(observed);
    }

    private sealed class CapturingUiDispatcher : IUiDispatcher
    {
        private readonly bool _hasAccess;

        public CapturingUiDispatcher(bool hasAccess)
            => _hasAccess = hasAccess;

        public bool PostCalled { get; private set; }

        public bool CheckAccess() => _hasAccess;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            PostCalled = true;
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
