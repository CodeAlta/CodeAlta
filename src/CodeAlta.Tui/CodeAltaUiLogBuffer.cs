namespace CodeAlta.Tui;

internal sealed class CodeAltaUiLogBuffer
{
    private readonly object _gate = new();
    private readonly List<CodeAltaUiLogEntry> _entries;
    private readonly Dictionary<int, Action<CodeAltaUiLogBufferEvent>> _subscribers;
    private readonly int _capacity;
    private int _nextSubscriptionId;

    public CodeAltaUiLogBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _entries = new List<CodeAltaUiLogEntry>(capacity);
        _subscribers = new Dictionary<int, Action<CodeAltaUiLogBufferEvent>>();
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public void Append(string text, bool isMarkup)
    {
        ArgumentNullException.ThrowIfNull(text);

        Action<CodeAltaUiLogBufferEvent>[] subscribers;
        CodeAltaUiLogBufferEvent @event;
        lock (_gate)
        {
            if (_entries.Count == _capacity)
            {
                _entries.RemoveAt(0);
            }

            var entry = new CodeAltaUiLogEntry(text, isMarkup);
            _entries.Add(entry);
            @event = CodeAltaUiLogBufferEvent.Appended(entry);
            subscribers = _subscribers.Values.ToArray();
        }

        NotifySubscribers(subscribers, @event);
    }

    public void Clear()
    {
        Action<CodeAltaUiLogBufferEvent>[] subscribers;
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return;
            }

            _entries.Clear();
            subscribers = _subscribers.Values.ToArray();
        }

        NotifySubscribers(subscribers, CodeAltaUiLogBufferEvent.Cleared());
    }

    public IDisposable Subscribe(Action<CodeAltaUiLogBufferEvent> handler, out CodeAltaUiLogEntry[] snapshot)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_gate)
        {
            snapshot = _entries.ToArray();
            var subscriptionId = _nextSubscriptionId++;
            _subscribers.Add(subscriptionId, handler);
            return new Subscription(this, subscriptionId);
        }
    }

    private static void NotifySubscribers(
        IReadOnlyList<Action<CodeAltaUiLogBufferEvent>> subscribers,
        CodeAltaUiLogBufferEvent @event)
    {
        foreach (var subscriber in subscribers)
        {
            subscriber(@event);
        }
    }

    private void Unsubscribe(int subscriptionId)
    {
        lock (_gate)
        {
            _subscribers.Remove(subscriptionId);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly CodeAltaUiLogBuffer _owner;
        private readonly int _subscriptionId;
        private bool _disposed;

        public Subscription(CodeAltaUiLogBuffer owner, int subscriptionId)
        {
            _owner = owner;
            _subscriptionId = subscriptionId;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.Unsubscribe(_subscriptionId);
        }
    }
}

internal readonly record struct CodeAltaUiLogEntry(string Text, bool IsMarkup);

internal readonly record struct CodeAltaUiLogBufferEvent(CodeAltaUiLogBufferEventKind Kind, CodeAltaUiLogEntry? Entry)
{
    public static CodeAltaUiLogBufferEvent Appended(CodeAltaUiLogEntry entry)
        => new(CodeAltaUiLogBufferEventKind.Appended, entry);

    public static CodeAltaUiLogBufferEvent Cleared()
        => new(CodeAltaUiLogBufferEventKind.Cleared, Entry: null);
}

internal enum CodeAltaUiLogBufferEventKind
{
    Appended,
    Cleared,
}
