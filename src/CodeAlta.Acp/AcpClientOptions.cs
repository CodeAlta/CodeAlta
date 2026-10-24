namespace CodeAlta.Acp;

/// <summary>
/// Options used to create and initialize an ACP client connection.
/// </summary>
public sealed class AcpClientOptions
{
    /// <summary>
    /// Gets or sets the process options used to launch the agent.
    /// </summary>
    public required AcpProcessOptions ProcessOptions { get; init; }

    /// <summary>
    /// Gets or sets the client implementation info sent during initialization.
    /// </summary>
    public required Implementation ClientInfo { get; init; }

    /// <summary>
    /// Gets or sets the advertised client capabilities.
    /// </summary>
    public required ClientCapabilities ClientCapabilities { get; init; }

    /// <summary>
    /// Gets or sets the protocol version to negotiate.
    /// </summary>
    public ushort ProtocolVersion { get; init; } = 1;

    /// <summary>
    /// Gets how long the agent has to answer <c>initialize</c>; <see cref="Timeout.InfiniteTimeSpan" /> for no limit.
    /// An agent started through <c>npx</c> or <c>uvx</c> may download itself first.
    /// </summary>
    public TimeSpan InitializeTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets what is called on the reading loop for each notification and request of the agent, in the order of its
    /// output, before the response that follows it completes its request: it must not wait. <see langword="null" />
    /// to read the messages with <see cref="AcpClient.StreamAsync" />.
    /// </summary>
    public Action<AcpServerMessage>? MessageHandler { get; init; }
}
