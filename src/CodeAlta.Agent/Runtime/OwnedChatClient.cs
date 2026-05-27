using Microsoft.Extensions.AI;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Wraps an <see cref="IChatClient"/> and disposes an owned resource when the wrapper is disposed.
/// </summary>
internal sealed class OwnedChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly IDisposable _owner;

    /// <summary>
    /// Initializes a new instance of the <see cref="OwnedChatClient"/> class.
    /// </summary>
    /// <param name="inner">The wrapped chat client.</param>
    /// <param name="owner">The owned disposable resource.</param>
    public OwnedChatClient(IChatClient inner, IDisposable owner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(owner);

        _inner = inner;
        _owner = owner;
    }

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => _inner.GetResponseAsync(messages, options, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => _inner.GetStreamingResponseAsync(messages, options, cancellationToken);

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
        => _inner.GetService(serviceType, serviceKey);

    /// <inheritdoc />
    public void Dispose()
    {
        _inner.Dispose();
        _owner.Dispose();
    }
}
