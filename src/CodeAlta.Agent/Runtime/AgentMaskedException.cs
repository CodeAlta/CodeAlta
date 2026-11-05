namespace CodeAlta.Agent.Runtime;

/// <summary>
/// An exception told with the secrets of the machine masked (<see cref="AgentSecretMasker.MaskException"/>): its message
/// and its text (type, message, stack and inner exceptions, as <see cref="Exception.ToString"/> gives them) are those of
/// the exception it stands for, masked. It is never thrown: it is what an export of telemetry is given in its place.
/// </summary>
public sealed class AgentMaskedException : Exception
{
    private readonly string _text;

    /// <summary>Initializes a new instance of the <see cref="AgentMaskedException"/> class.</summary>
    /// <param name="originalType">The full name of the type of the exception it stands for.</param>
    /// <param name="message">Its message, masked.</param>
    /// <param name="text">Its text, masked.</param>
    /// <param name="innerException">Its inner exception, masked, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="originalType"/> or <paramref name="text"/> is <see langword="null"/>.</exception>
    public AgentMaskedException(string originalType, string? message, string text, Exception? innerException)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(originalType);
        ArgumentNullException.ThrowIfNull(text);
        OriginalType = originalType;
        _text = text;
    }

    /// <summary>Gets the full name of the type of the exception it stands for.</summary>
    public string OriginalType { get; }

    /// <summary>Gets the text of the exception it stands for, masked.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => _text;
}
