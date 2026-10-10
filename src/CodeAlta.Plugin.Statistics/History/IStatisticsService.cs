using CodeAlta.Plugin.Statistics.Query;

namespace CodeAlta.Plugin.Statistics.History;

/// <summary>
/// What the Statistics plugin offers to the application while it runs: the state of the reading of the history, the controls of
/// the user, and the signals of the flow. The pages of the canvas and <c>alta statistics</c> use it.
/// </summary>
/// <remarks>All the members are thread-safe. The events are raised on the thread of the engine: a handler returns at once.</remarks>
public interface IStatisticsService
{
    /// <summary>Gets the current state of the reading of the history.</summary>
    StatisticsStatus Status { get; }

    /// <summary>Gets the questions the pages and <c>alta statistics</c> ask of the numbers.</summary>
    StatisticsQueries Queries { get; }

    /// <summary>Occurs when <see cref="Status"/> changes.</summary>
    event Action<StatisticsStatus>? StatusChanged;

    /// <summary>Occurs when a catch-up was saved: the numbers of some days changed.</summary>
    event Action<StatisticsDataChange>? DataChanged;

    /// <summary>
    /// Tells the engine that a session wrote. It returns at once: the session is caught up a moment later, from its journal.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    void Signal(string sessionId);

    /// <summary>
    /// Chooses how much history to read: the first time, or to go further back later ("read more history"). A choice that does not
    /// go further back than what was chosen is ignored.
    /// </summary>
    /// <param name="choice">The choice.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The status after the choice.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="choice"/> is null.</exception>
    ValueTask<StatisticsStatus> ChooseHistoryAsync(HistoryChoice choice, CancellationToken cancellationToken = default);

    /// <summary>Pauses the reading of the history at the end of a line; the flow goes on. The pause is kept across restarts.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The status after the pause.</returns>
    ValueTask<StatisticsStatus> PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes a paused reading. On a history that is done or stopped with sessions that could not be read, tries them again;
    /// on an engine that could not start, tries the start again.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The status after the resume.</returns>
    ValueTask<StatisticsStatus> ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the reading where it is: the statistics start at the date reached, and "read more history" goes further back.
    /// A catch-up of what changed, or the new reading of a new version of the facts, only ends where it is: the floor and the
    /// choice stay, and the next look at the journals reads what is left.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The status after the stop.</returns>
    ValueTask<StatisticsStatus> StopHereAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the facts of the sessions whose journal is gone.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of sessions forgotten.</returns>
    ValueTask<int> ForgetDeletedAsync(CancellationToken cancellationToken = default);

    /// <summary>Empties the statistics and comes back to the first-time choice of how much history to read.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The status after the reset.</returns>
    ValueTask<StatisticsStatus> ResetAsync(CancellationToken cancellationToken = default);
}
