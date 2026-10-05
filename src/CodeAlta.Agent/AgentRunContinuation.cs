namespace CodeAlta.Agent;

/// <summary>A host-supplied, one-shot request to continue a successfully completed run with fresh tools.</summary>
/// <remarks>This is volatile authority for one actual run, not a durable queue or a request to interrupt it.
/// Hosts must await the complete send, check its outcome, then consume the text to prepare a new run.</remarks>
public sealed class AgentRunContinuation
{
    private readonly object _gate = new();
    private CancellationToken _runToken;
    private string? _text;
    private bool _bound;
    private bool _closed;
    private bool _cancelled;

    internal void Bind(CancellationToken runToken)
    {
        lock (_gate)
        {
            if (_bound || _closed) throw new InvalidOperationException("A continuation belongs to one run only.");
            _bound = true;
            _runToken = runToken;
        }
    }

    /// <summary>Requests one follow-up; identical retries coalesce and a different pending text is refused.</summary>
    /// <param name="text">Nonblank continuation text, at most 8192 characters.</param>
    /// <returns>Whether the request was admitted while its original run was open.</returns>
    /// <exception cref="ArgumentException">The text is blank or too long.</exception>
    public bool TryRequest(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > 8192) throw new ArgumentException("Continuation text exceeds 8192 characters.", nameof(text));
        lock (_gate)
        {
            if (!_bound || _closed || _runToken.IsCancellationRequested) return false;
            if (_text is not null) return _text == text;
            _text = text;
            return true;
        }
    }

    internal void Close()
    {
        lock (_gate) { _cancelled |= _runToken.IsCancellationRequested; _closed = true; }
    }

    /// <summary>Takes the text once after a successful, fully settled send; cancellation suppresses it.</summary>
    /// <returns>The requested text, or null when absent, still open, canceled, or already consumed.</returns>
    public string? Take()
    {
        lock (_gate)
        {
            if (!_bound || !_closed || _cancelled || _runToken.IsCancellationRequested) return null;
            var text = _text;
            _text = null;
            return text;
        }
    }
}
