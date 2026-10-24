namespace CodeAlta.Acp;

/// <summary>
/// The exception of a request that the agent can no longer answer: its output ended (it exited) or the client was disposed.
/// </summary>
public sealed class AcpConnectionClosedException : IOException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AcpConnectionClosedException" /> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The error that ended the connection, if any.</param>
    public AcpConnectionClosedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
