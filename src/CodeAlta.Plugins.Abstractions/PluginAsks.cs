namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Gives a plugin that serves the user beyond the window, such as the page a phone opens, the questions agents ask the user
/// and the reviews they request: the pending one of a session, and the user's answer, which the session receives as its next
/// prompt, as an answer from the window. The answer is the user's: a plugin answers only what the user chose, and the
/// <c>alta</c> commands, which agents run, never reach this service.
/// </summary>
public interface IPluginAskService
{
    /// <summary>Gets whether the host has asks a plugin can read and answer.</summary>
    bool HasAsks { get; }

    /// <summary>
    /// Lists the sessions that wait for the user to answer a question or a review request, with the ask each waits on: its
    /// revision and state change when an answer goes, or did not reach the session and the ask waits again.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One entry per session, its first ask; a reading that can be stale at once.</returns>
    ValueTask<IReadOnlyList<PluginAskSummary>> ListAskingAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the ask a session waits on: the first of its asks, the one the window shows.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ask, or <see langword="null"/> when the session waits for none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is null.</exception>
    ValueTask<PluginAsk?> GetPendingAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers an ask: the answers to its questions, and the verdict of a review request. The host sends them to the session,
    /// and the outcome says whether the session has them.
    /// </summary>
    /// <param name="ask">The ask, as <see cref="GetPendingAsync"/> read it, and the id of this answer.</param>
    /// <param name="answers">One answer per question at most.</param>
    /// <param name="review">The verdict of a review request; <see langword="null"/> for an ask without a review.</param>
    /// <param name="cancellationToken">Cancels the wait only: an answer the host took goes on.</param>
    /// <returns>What became of the answer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ask"/> or <paramref name="answers"/> is null.</exception>
    ValueTask<PluginAskOutcome> AnswerAsync(PluginAskReference ask, IReadOnlyList<PluginAskAnswer> answers, PluginAskReviewAnswer? review,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels an ask without answering it: the session gets no prompt, and returns to its prompt.</summary>
    /// <param name="ask">The ask, as <see cref="GetPendingAsync"/> read it, and the id of this cancel.</param>
    /// <param name="cancellationToken">Cancels the wait only.</param>
    /// <returns>What became of the cancel.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ask"/> is null.</exception>
    ValueTask<PluginAskOutcome> CancelAsync(PluginAskReference ask, CancellationToken cancellationToken = default);
}

/// <summary>An ask a session waits on: its questions, and what it asks the user to review.</summary>
public sealed record PluginAsk
{
    /// <summary>Gets the session that asks.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the id of the ask.</summary>
    public required string AskId { get; init; }

    /// <summary>
    /// Gets the revision of the ask an answer names. It changes when an answer did not reach the session and the ask waits
    /// again, so that an answer to an earlier state is refused.
    /// </summary>
    public required string Revision { get; init; }

    /// <summary>
    /// Gets the state of the ask: <c>pending</c> while it waits for the user, <c>submitting</c> while an answer goes to the
    /// session, <c>indeterminate</c> when an answer went and the host does not know whether the session has it.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Gets whether the ask is shown at once, while the turn of its session runs: a question of a script (<c>ask()</c> in
    /// Code Mode), which waits for the answer. Any other ask is shown once the turn that asked has ended.
    /// </summary>
    public bool Immediate { get; init; }

    /// <summary>Gets the questions, in order; a review request may have none.</summary>
    public IReadOnlyList<PluginAskQuestion> Questions { get; init; } = [];

    /// <summary>Gets what the user is asked to review, or <see langword="null"/> for questions only.</summary>
    public PluginAskReview? Review { get; init; }
}

/// <summary>The ask a session waits on, as <see cref="IPluginAskService.ListAskingAsync"/> lists it.</summary>
/// <param name="SessionId">The session that asks.</param>
/// <param name="AskId">The ask, <see cref="PluginAsk.AskId"/>.</param>
/// <param name="Revision">Its revision, <see cref="PluginAsk.Revision"/>.</param>
/// <param name="State">Its state, <see cref="PluginAsk.State"/>.</param>
/// <param name="Immediate">Whether it is shown while the turn runs, <see cref="PluginAsk.Immediate"/>.</param>
public sealed record PluginAskSummary(string SessionId, string AskId, string Revision, string State, bool Immediate);

/// <summary>The limits of an answer, those the host checks: an answer beyond them is <c>invalid</c>, or <c>review_failed</c> for the overall comment.</summary>
public static class PluginAskLimits
{
    /// <summary>The most characters (UTF-16 units) of the text answers of an ask, all its questions together.</summary>
    public const int AnswerText = 8192;

    /// <summary>The most characters (UTF-16 units) of the overall comment of a review.</summary>
    public const int OverallComment = 16384;
}

/// <summary>A question of an ask, answered with one of its choices, with text, or both.</summary>
public sealed record PluginAskQuestion
{
    /// <summary>Gets the short title of the question, the name of its step.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the question.</summary>
    public required string Question { get; init; }

    /// <summary>Gets what explains the question, or <see langword="null"/>.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the choices; empty for a question answered with text only.</summary>
    public IReadOnlyList<PluginAskChoice> Choices { get; init; } = [];

    /// <summary>Gets the text field of the question, or <see langword="null"/> for a question answered with a choice only.</summary>
    public PluginAskFreeform? Freeform { get; init; }
}

/// <summary>A choice of a question.</summary>
public sealed record PluginAskChoice
{
    /// <summary>Gets the title of the choice, which the answer gives the agent.</summary>
    public required string Title { get; init; }

    /// <summary>Gets what explains the choice, or <see langword="null"/>.</summary>
    public string? Description { get; init; }
}

/// <summary>The text field of a question.</summary>
public sealed record PluginAskFreeform
{
    /// <summary>Gets the label of the field, or <see langword="null"/> for the usual one.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the text shown in the empty field, or <see langword="null"/>.</summary>
    public string? Placeholder { get; init; }
}

/// <summary>What a review request asks the user to review.</summary>
public sealed record PluginAskReview
{
    /// <summary>Gets what is reviewed: <c>file</c>, <c>changes</c> (the uncommitted changes) or <c>branch</c> (the changes of the branch).</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the file of a <c>file</c> review, relative to the folder of the session, with <c>/</c>; <see langword="null"/> otherwise.</summary>
    public string? Path { get; init; }

    /// <summary>Gets the short title the agent gave the review, or <see langword="null"/>.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the message of the agent for the user, in Markdown, or <see langword="null"/>.</summary>
    public string? Message { get; init; }

    /// <summary>
    /// Gets how many comments the answer sends: those the user wrote on the lines in the window and has not sent yet;
    /// <see langword="null"/> when the host cannot say.
    /// </summary>
    public int? Comments { get; init; }
}

/// <summary>The ask an answer or a cancel is for, as it was read, and the id of that answer or cancel.</summary>
/// <param name="ActionId">The id of this answer or cancel: one sent again with the same id gets the outcome of the first, and is never sent twice.</param>
/// <param name="SessionId">The session that asks.</param>
/// <param name="AskId">The ask, <see cref="PluginAsk.AskId"/>.</param>
/// <param name="Revision">The revision of the ask that was read, <see cref="PluginAsk.Revision"/>.</param>
public sealed record PluginAskReference(Guid ActionId, string SessionId, string AskId, string Revision);

/// <summary>The answer to one question of an ask.</summary>
public sealed record PluginAskAnswer
{
    /// <summary>Gets the question, by its zero-based index.</summary>
    public required int QuestionIndex { get; init; }

    /// <summary>Gets the chosen choices, by their zero-based indexes.</summary>
    public IReadOnlyList<int> SelectedChoiceIndexes { get; init; } = [];

    /// <summary>Gets the text of the answer, or <see langword="null"/>.</summary>
    public string? FreeformText { get; init; }
}

/// <summary>The verdict of the user on a review request. The comments on lines are those the user made in the window.</summary>
/// <param name="Verdict"><c>approved</c>, <c>changes_requested</c> or <c>commented</c>.</param>
/// <param name="Overall">The overall comment, or <see langword="null"/>.</param>
public sealed record PluginAskReviewAnswer(string Verdict, string? Overall);

/// <summary>What became of an answer or a cancel.</summary>
/// <param name="Status">
/// <list type="bullet">
/// <item><c>admitted</c>: the session has the answer.</item>
/// <item><c>submitting</c>: the answer goes to the session; the same answer sent again, with the same action id, waits for its outcome.</item>
/// <item><c>cancelled</c>: the ask is cancelled.</item>
/// <item><c>not_admitted</c>: the session did not take the answer; the ask waits again, with a new revision.</item>
/// <item><c>indeterminate</c>: the answer went, and the host does not know whether the session has it.</item>
/// <item><c>rejected</c>: the ask no longer waits, or not in the revision named.</item>
/// <item><c>conflict</c>: another answer was sent with the same action id.</item>
/// <item><c>invalid</c>: the answer does not fit the ask, or is too long.</item>
/// <item><c>nothing_to_submit</c>: a review with the <c>commented</c> verdict and nothing to say.</item>
/// <item><c>review_failed</c>: the review could not be written.</item>
/// <item><c>unavailable</c>: the host has no asks now (it starts, or it closes).</item>
/// </list>
/// </param>
public sealed record PluginAskOutcome(string Status)
{
    /// <summary>Gets whether the answer reached the session, or the ask was cancelled.</summary>
    public bool Succeeded => Status is "admitted" or "cancelled";
}

/// <summary>
/// The ask service of a host that knows which plugin asks. The runtime asks the host for the service of each plugin it
/// starts; a host that does not implement this interface gives no plugin asks. Asks answer for the user, so a host gives a
/// working service only to the plugins of its own build that serve the user (the page of the phones), never to a plugin of
/// a folder, which an agent can write.
/// </summary>
public interface IPluginAskRuntimeService : IPluginAskService
{
    /// <summary>Gets the service of one plugin.</summary>
    /// <param name="pluginRuntimeKey">The runtime key of the plugin.</param>
    /// <param name="builtIn">Whether the plugin is one of the application's own build, not of a folder.</param>
    /// <returns>The service of that plugin: this one, or <see cref="NoopPluginAskService.Instance"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="pluginRuntimeKey"/> is null, empty or whitespace.</exception>
    IPluginAskService ForPlugin(string pluginRuntimeKey, bool builtIn);
}

/// <summary>The ask service of a host without asks: no session waits, and nothing can be answered.</summary>
public sealed class NoopPluginAskService : IPluginAskService
{
    private NoopPluginAskService()
    {
    }

    /// <summary>Gets the shared instance.</summary>
    public static NoopPluginAskService Instance { get; } = new();

    /// <inheritdoc />
    public bool HasAsks => false;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PluginAskSummary>> ListAskingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<PluginAskSummary>>([]);

    /// <inheritdoc />
    public ValueTask<PluginAsk?> GetPendingAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        return ValueTask.FromResult<PluginAsk?>(null);
    }

    /// <inheritdoc />
    public ValueTask<PluginAskOutcome> AnswerAsync(PluginAskReference ask, IReadOnlyList<PluginAskAnswer> answers, PluginAskReviewAnswer? review,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(answers);
        return ValueTask.FromResult(new PluginAskOutcome("unavailable"));
    }

    /// <inheritdoc />
    public ValueTask<PluginAskOutcome> CancelAsync(PluginAskReference ask, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ask);
        return ValueTask.FromResult(new PluginAskOutcome("unavailable"));
    }
}
