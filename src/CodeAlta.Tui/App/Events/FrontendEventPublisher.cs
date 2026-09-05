using CodeAlta.Tui.Threading;

namespace CodeAlta.Tui.App.Events;

internal sealed class FrontendEventPublisher
{
    private readonly IUiDispatcher _uiDispatcher;
    private readonly List<Action<ShellFrontendEvent>> _subscribers = [];

    public FrontendEventPublisher(IUiDispatcher uiDispatcher)
    {
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        _uiDispatcher = uiDispatcher;
    }

    public IDisposable Subscribe(Action<ShellFrontendEvent> subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        _subscribers.Add(subscriber);
        return new Subscription(_subscribers, subscriber);
    }

    public void Publish(ShellFrontendEvent frontendEvent)
    {
        ArgumentNullException.ThrowIfNull(frontendEvent);

        if (_uiDispatcher.CheckAccess())
        {
            PublishCore(frontendEvent);
            return;
        }

        _uiDispatcher.Post(() => PublishCore(frontendEvent));
    }

    private void PublishCore(ShellFrontendEvent frontendEvent)
    {
        foreach (var subscriber in _subscribers.ToArray())
        {
            subscriber(frontendEvent);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly List<Action<ShellFrontendEvent>> _subscribers;
        private Action<ShellFrontendEvent>? _subscriber;

        public Subscription(List<Action<ShellFrontendEvent>> subscribers, Action<ShellFrontendEvent> subscriber)
        {
            _subscribers = subscribers;
            _subscriber = subscriber;
        }

        public void Dispose()
        {
            if (_subscriber is not { } subscriber)
            {
                return;
            }

            _subscribers.Remove(subscriber);
            _subscriber = null;
        }
    }
}
