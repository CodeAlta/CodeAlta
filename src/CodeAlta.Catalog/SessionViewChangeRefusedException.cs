namespace CodeAlta.Catalog;

/// <summary>
/// Thrown when an idle session cannot be switched to another provider, moved to another project or given another
/// space because of what it still holds, with a code a caller can show.
/// </summary>
public sealed class SessionViewChangeRefusedException : InvalidOperationException
{
    /// <summary>The code of a session whose queue holds a prompt that waits or is being submitted.</summary>
    public const string QueuePending = "queue_pending";

    /// <summary>The code of a session whose history holds a turn that did not end.</summary>
    public const string TurnUnfinished = "turn_unfinished";

    /// <summary>Initializes a new instance of the <see cref="SessionViewChangeRefusedException"/> class.</summary>
    /// <param name="code">Why the change is refused: <see cref="QueuePending"/> or <see cref="TurnUnfinished"/>.</param>
    /// <param name="message">The message that describes the refusal.</param>
    /// <exception cref="ArgumentException">The code is empty.</exception>
    public SessionViewChangeRefusedException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>Gets why the change is refused: <see cref="QueuePending"/> or <see cref="TurnUnfinished"/>.</summary>
    public string Code { get; }
}
