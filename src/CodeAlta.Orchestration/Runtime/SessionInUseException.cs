namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// The exception thrown when a session cannot be run or changed because another process holds its lease: the command
/// line, or another CodeAlta Desktop or TUI on the same sessions folder. Once that process releases it, either side can
/// continue the session.
/// </summary>
public sealed class SessionInUseException : InvalidOperationException
{
    /// <summary>Initializes the exception for a session and what its lease says of its holder.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="holderKind">The kind of the holder (<c>altacli</c>, <c>desktop</c>, <c>tui</c>); null when it is not known.</param>
    /// <param name="holderProcessId">The process of the holder; null when it is not known.</param>
    public SessionInUseException(string sessionId, string? holderKind, int? holderProcessId)
        : base(holderKind is null
            ? $"Session '{sessionId}' is in use by another process."
            : $"Session '{sessionId}' is in use by {GetHolderName(holderKind)}{(holderProcessId is { } pid ? $" (process {pid})" : string.Empty)}.")
    {
        SessionId = sessionId;
        HolderKind = holderKind;
        HolderProcessId = holderProcessId;
    }

    /// <summary>Gets the name of a holder as users know it: <c>CodeAlta Desktop</c>, <c>CodeAlta TUI</c>, <c>altacli</c>.</summary>
    /// <param name="holderKind">The kind of the holder; null when it is not known.</param>
    /// <returns>The name; <c>another process</c> for an unknown holder, the kind itself for another kind.</returns>
    public static string GetHolderName(string? holderKind) => holderKind switch
    {
        null or "" => "another process",
        Catalog.SessionLeaseOwner.DesktopKind => "CodeAlta Desktop",
        Catalog.SessionLeaseOwner.TuiKind => "CodeAlta TUI",
        _ => holderKind,
    };

    /// <summary>Gets the session.</summary>
    public string SessionId { get; }

    /// <summary>Gets the kind of the holder (<c>altacli</c>, <c>desktop</c>, <c>tui</c>); null when it is not known.</summary>
    public string? HolderKind { get; }

    /// <summary>Gets the process of the holder; null when it is not known.</summary>
    public int? HolderProcessId { get; }
}
