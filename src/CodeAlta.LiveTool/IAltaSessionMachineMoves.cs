namespace CodeAlta.LiveTool;

/// <summary>
/// The moves of sessions between this computer and the machines it is paired with (CodeAlta Desktop), for
/// <c>alta session move</c>: a session goes to another machine with its conversation and its work in progress, and runs
/// there from then on. A host without paired machines does not give this service, and the command is unavailable.
/// </summary>
public interface IAltaSessionMachineMoves
{
    /// <summary>Lists the machines a session can move to: this computer first, then the paired machines.</summary>
    /// <returns>The machines.</returns>
    IReadOnlyList<AltaMoveMachine> ListMachines();

    /// <summary>Moves a session now, from where it runs to a machine.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="machine">The machine: its name or its id, as <see cref="ListMachines"/> gives them.</param>
    /// <param name="cloneRepository">Lets the machine clone the repository of the session when it has no project of it.</param>
    /// <param name="cancellationToken">Stops waiting; the move stays recoverable.</param>
    /// <returns>The outcome.</returns>
    Task<AltaSessionMoveResult> MoveAsync(string sessionId, string machine, bool cloneRepository, CancellationToken cancellationToken);

    /// <summary>Moves a session of this computer once its turn ends: an agent that moves its own session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="machine">The machine: its name or its id.</param>
    /// <param name="cloneRepository">Lets the machine clone the repository of the session when it has no project of it.</param>
    /// <returns><c>scheduled</c>, or why the move cannot be scheduled.</returns>
    AltaSessionMoveResult Schedule(string sessionId, string machine, bool cloneRepository);
}

/// <summary>A machine a session can move to.</summary>
/// <param name="Id">Its device id; empty for this computer.</param>
/// <param name="Name">Its name.</param>
/// <param name="IsThisComputer">Whether it is this computer.</param>
/// <param name="State"><c>online</c>, <c>connecting</c>, <c>offline</c> or <c>unauthorized</c>.</param>
public sealed record AltaMoveMachine(string Id, string Name, bool IsThisComputer, string State);

/// <summary>The outcome of a move of a session.</summary>
/// <param name="Status">
/// <c>finished</c> (the session runs on the machine), <c>scheduled</c> (it moves when its turn ends), <c>offered</c> (the
/// connection was lost after the session was frozen: the move goes on when the machine is back), <c>refused</c> or
/// <c>aborted</c> (it stays where it was).
/// </param>
/// <param name="Code">Why it was refused or undone (<c>machine_not_found</c>, <c>working</c>, <c>project_missing</c>, <c>forbidden</c>...).</param>
/// <param name="Message">The refusal in words.</param>
/// <param name="Machine">The name of the machine the session goes to.</param>
/// <param name="WorkFolder">The folder the session works in there.</param>
/// <param name="Warnings">What went wrong without stopping the move.</param>
public sealed record AltaSessionMoveResult(string Status, string? Code, string? Message, string? Machine, string? WorkFolder, IReadOnlyList<string> Warnings);
