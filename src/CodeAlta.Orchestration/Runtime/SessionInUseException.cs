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
        : this(sessionId, holderKind, holderProcessId, holderKind is null
            ? $"Session '{sessionId}' is in use by another process."
            : $"Session '{sessionId}' is in use by {GetHolderName(holderKind)}{(holderProcessId is { } pid ? $" (process {pid})" : string.Empty)}.")
    {
    }

    private SessionInUseException(string sessionId, string? holderKind, int? holderProcessId, string message)
        : base(message)
    {
        SessionId = sessionId;
        HolderKind = holderKind;
        HolderProcessId = holderProcessId;
    }

    /// <summary>The holder kind of a session frozen for a move to another machine, or given to it (<see cref="Moved"/>).</summary>
    public const string MovedKind = "moved";

    /// <summary>
    /// Creates the refusal of a write to a session that another machine owns now, or that is frozen while it moves there:
    /// this machine keeps it read-only.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="record">What this machine recorded of the move.</param>
    /// <returns>The exception, whose <see cref="HolderKind"/> is <see cref="MovedKind"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is null.</exception>
    public static SessionInUseException Moved(string sessionId, Catalog.SessionMoves.SessionRelinquishment record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var machine = string.IsNullOrWhiteSpace(record.ToMachine) ? "another machine" : record.ToMachine;
        return new SessionInUseException(sessionId, MovedKind, null, record.IsRelinquished
            ? $"Session '{sessionId}' moved to {machine}: it is read-only here."
            : $"Session '{sessionId}' is moving to {machine}: it is read-only until the move ends.");
    }

    /// <summary>Gets the name of a holder as users know it: <c>CodeAlta Desktop</c>, <c>CodeAlta TUI</c>, <c>CodeAlta Node</c>, <c>altacli</c>.</summary>
    /// <param name="holderKind">The kind of the holder; null when it is not known.</param>
    /// <returns>The name; <c>another process</c> for an unknown holder, the kind itself for another kind.</returns>
    public static string GetHolderName(string? holderKind) => holderKind switch
    {
        null or "" => "another process",
        Catalog.SessionLeaseOwner.DesktopKind => "CodeAlta Desktop",
        Catalog.SessionLeaseOwner.TuiKind => "CodeAlta TUI",
        Catalog.SessionLeaseOwner.NodeKind => "CodeAlta Node",
        MovedKind => "another machine",
        _ => holderKind,
    };

    /// <summary>Gets the session.</summary>
    public string SessionId { get; }

    /// <summary>Gets the kind of the holder (<c>altacli</c>, <c>desktop</c>, <c>tui</c>); null when it is not known.</summary>
    public string? HolderKind { get; }

    /// <summary>Gets the process of the holder; null when it is not known.</summary>
    public int? HolderProcessId { get; }
}
