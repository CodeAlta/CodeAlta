namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Where a request that waits below its provider says so to its session: a free slot of the model server that serves its
/// model waits in the transport of the provider, which does not know the session. The session gives its updates for the
/// time of each request (<see cref="Enter"/>); the transports of the providers take them from the HTTP request
/// (<see cref="RequestOption"/>), which <see cref="Wrap"/> sets. Owned by the <see cref="ModelProviderRegistry"/>.
/// </summary>
public sealed class AgentWaitScope
{
    /// <summary>The option of an HTTP request of a provider that gives the updates of the session that sends it.</summary>
    public static readonly HttpRequestOptionsKey<Func<AgentTurnSessionUpdate, CancellationToken, ValueTask>> RequestOption = new("CodeAlta.AgentWait");

    // The updates of the session of the request under way in this flow of execution: per flow, never shared between them.
    private readonly AsyncLocal<Func<AgentTurnSessionUpdate, CancellationToken, ValueTask>?> _current = new();

    /// <summary>Gets the updates of the session whose request is under way in this flow; null for none.</summary>
    public Func<AgentTurnSessionUpdate, CancellationToken, ValueTask>? Current => _current.Value;

    /// <summary>Gives the updates of a session to what its request waits for, until the returned scope is disposed.</summary>
    /// <param name="onSessionUpdate">The updates of the session.</param>
    /// <returns>The scope.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="onSessionUpdate"/> is null.</exception>
    public IDisposable Enter(Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate)
    {
        ArgumentNullException.ThrowIfNull(onSessionUpdate);
        var previous = _current.Value;
        _current.Value = onSessionUpdate;
        return new Exit(this, previous);
    }

    /// <summary>Wraps the transport of a provider so that its requests carry the updates of the session that sends them.</summary>
    /// <param name="inner">The transport.</param>
    /// <returns>The wrapped transport.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    public HttpMessageHandler Wrap(HttpMessageHandler inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new Carrier(this, inner);
    }

    private sealed class Exit(AgentWaitScope owner, Func<AgentTurnSessionUpdate, CancellationToken, ValueTask>? previous) : IDisposable
    {
        public void Dispose() => owner._current.Value = previous;
    }

    private sealed class Carrier(AgentWaitScope owner, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (owner.Current is { } updates) request.Options.Set(RequestOption, updates);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
