namespace CodeAlta.Agent.Runtime;

/// <summary>
/// The events of the journal of a session read again that come before the ones it read: they are read when an
/// action needs them (the whole history, the title of the first prompt, the conversation a provider asks for), once.
/// </summary>
internal sealed class AgentSessionEarlierHistory
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<AgentEvent>>> _load;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<AgentEvent>? _events;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentSessionEarlierHistory"/> class.
    /// </summary>
    /// <param name="load">Reads the events; called again after a failure or a cancellation only.</param>
    /// <param name="conversationIncluded">
    /// Whether the events read hold the conversation the session sends (they start at its last compaction
    /// checkpoint); <see langword="false"/> when the provider keeps the conversation and was given none of it.
    /// </param>
    /// <param name="durableEventCount">How many events come before the ones read, when it is known.</param>
    /// <exception cref="ArgumentNullException"><paramref name="load"/> is <see langword="null"/>.</exception>
    public AgentSessionEarlierHistory(Func<CancellationToken, Task<IReadOnlyList<AgentEvent>>> load, bool conversationIncluded, long? durableEventCount)
    {
        ArgumentNullException.ThrowIfNull(load);
        _load = load;
        ConversationIncluded = conversationIncluded;
        DurableEventCount = durableEventCount;
    }

    /// <summary>Gets whether the events read hold the conversation the session sends.</summary>
    public bool ConversationIncluded { get; }

    /// <summary>Gets how many events come before the ones read, when it is known; null otherwise.</summary>
    public long? DurableEventCount { get; }

    /// <summary>
    /// Loads the earlier events, or returns them when they were loaded.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The events, in journal order.</returns>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public async Task<IReadOnlyList<AgentEvent>> LoadAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _events) is { } loaded)
        {
            return loaded;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_events is { } read)
            {
                return read;
            }

            var events = await _load(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _events, events);
            return events;
        }
        finally
        {
            _gate.Release();
        }
    }
}
