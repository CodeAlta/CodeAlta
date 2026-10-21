using System.Text.Json.Serialization;

namespace CodeAlta.Catalog.Reviews;

/// <summary>What a review is of: the names of its targets.</summary>
public static class ReviewTargets
{
    /// <summary>The uncommitted changes of a checkout: its work tree against its last commit.</summary>
    public const string Changes = "changes";

    /// <summary>What the branch of a checkout changed since it left its base branch, the uncommitted changes included.</summary>
    public const string Branch = "branch";

    /// <summary>One file of a checkout, such as a plan.</summary>
    public const string File = "file";

    /// <summary>Answers whether a target compares two sides of a file (a diff) rather than showing one file.</summary>
    /// <param name="target">The target.</param>
    /// <returns>True for <see cref="Changes"/> and <see cref="Branch"/>.</returns>
    public static bool IsDiff(string? target) => target is Changes or Branch;

    /// <summary>Answers whether a text names a target.</summary>
    /// <param name="target">The text.</param>
    /// <returns>True for one of the targets.</returns>
    public static bool IsValid(string? target) => target is Changes or Branch or File;
}

/// <summary>The sides of a file a comment can be on.</summary>
public static class ReviewSides
{
    /// <summary>The side before the changes: the last commit, or the base of the branch.</summary>
    public const string Old = "old";

    /// <summary>The side after the changes: the work tree. The only side of a file target.</summary>
    public const string New = "new";
}

/// <summary>Where a thread is in its life.</summary>
public static class ReviewThreadStates
{
    /// <summary>Written and not sent yet.</summary>
    public const string Draft = "draft";

    /// <summary>Sent to an agent, not answered yet; also a thread the user replied to or reopened.</summary>
    public const string Open = "open";

    /// <summary>The agent says it did what the thread asks; the user still resolves it.</summary>
    public const string Addressed = "addressed";

    /// <summary>Closed by the user.</summary>
    public const string Resolved = "resolved";
}

/// <summary>The verdicts a round can carry.</summary>
public static class ReviewVerdicts
{
    /// <summary>The user approves: the agent goes on, commits the changes or carries the plan out.</summary>
    public const string Approved = "approved";

    /// <summary>The user asks for changes: the agent revises.</summary>
    public const string ChangesRequested = "changes_requested";

    /// <summary>The user comments without a verdict: the agent answers.</summary>
    public const string Commented = "commented";

    /// <summary>Answers whether a text names a verdict.</summary>
    /// <param name="verdict">The text.</param>
    /// <returns>True for one of the verdicts.</returns>
    public static bool IsValid(string? verdict) => verdict is Approved or ChangesRequested or Commented;
}

/// <summary>Who wrote a reply.</summary>
public static class ReviewAuthors
{
    /// <summary>The person who reviews.</summary>
    public const string User = "user";

    /// <summary>An agent, through <c>alta review reply</c>.</summary>
    public const string Agent = "agent";
}

/// <summary>A comment on lines of a file, with its replies.</summary>
public sealed record ReviewThread
{
    /// <summary>Gets the id, <c>c</c> and a number, unique in its checkout and never reused.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the target it belongs to: one of <see cref="ReviewTargets"/>.</summary>
    public string Target { get; init; } = ReviewTargets.Changes;

    /// <summary>
    /// Gets the file: relative to the work tree of the repository for a diff target, as the changes view lists it;
    /// relative to the checkout folder for a file target. Always with <c>/</c>.
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets the side the lines are on: one of <see cref="ReviewSides"/>.</summary>
    public string Side { get; init; } = ReviewSides.New;

    /// <summary>Gets the first line, from 1; 0 for a comment on the whole file.</summary>
    public int StartLine { get; init; }

    /// <summary>Gets the last line, inclusive; 0 for a comment on the whole file.</summary>
    public int EndLine { get; init; }

    /// <summary>Gets the text of the lines when the comment was written or last found: what anchors it.</summary>
    public string Anchor { get; init; } = string.Empty;

    /// <summary>
    /// Gets the nearest line that is not blank above the lines when they were last found, or null: with
    /// <see cref="After"/>, it finds the place of the thread again once the agent changed its lines.
    /// </summary>
    public string? Before { get; init; }

    /// <summary>Gets the nearest line that is not blank below the lines when they were last found, or null.</summary>
    public string? After { get; init; }

    /// <summary>Gets the comment, in Markdown.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Gets the text the user proposes in place of the lines; empty deletes them; null proposes nothing.</summary>
    public string? Suggestion { get; init; }

    /// <summary>Gets the state: one of <see cref="ReviewThreadStates"/>.</summary>
    public string State { get; init; } = ReviewThreadStates.Draft;

    /// <summary>
    /// Gets whether the anchor is no longer found in the file: its lines changed. The thread is then on the lines
    /// most like it or that took its place, else on its last lines; for a whole file, the file cannot be read.
    /// </summary>
    public bool Outdated { get; init; }

    /// <summary>Gets the replies, the oldest first.</summary>
    public IReadOnlyList<ReviewReply> Replies { get; init; } = [];

    /// <summary>Gets the last round that sent the thread; null for a draft.</summary>
    public string? Round { get; init; }

    /// <summary>Gets when the comment was written.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets when the comment was last changed by its author.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Gets when the user resolved it; null while it is not resolved.</summary>
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>Gets whether the comment is on the whole file rather than on lines.</summary>
    [JsonIgnore]
    public bool WholeFile => StartLine == 0;
}

/// <summary>A reply under a thread.</summary>
public sealed record ReviewReply
{
    /// <summary>Gets who wrote it: one of <see cref="ReviewAuthors"/>.</summary>
    public string Author { get; init; } = ReviewAuthors.User;

    /// <summary>Gets the session of the agent that wrote it; null for the user.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets the reply, in Markdown.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Gets when it was written.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary>One submission of a review: its verdict and the threads it sent.</summary>
public sealed record ReviewRound
{
    /// <summary>Gets the id, <c>r</c> and a number, unique in its checkout.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the target that was reviewed.</summary>
    public string Target { get; init; } = ReviewTargets.Changes;

    /// <summary>Gets the file, for a file target; null for a diff.</summary>
    public string? Path { get; init; }

    /// <summary>Gets the verdict: one of <see cref="ReviewVerdicts"/>.</summary>
    public string Verdict { get; init; } = ReviewVerdicts.Commented;

    /// <summary>Gets the overall comment; null when there is none.</summary>
    public string? Overall { get; init; }

    /// <summary>Gets the session the round was sent to.</summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets the git tree of the work tree when the round was submitted, for a diff target: what an approval
    /// approves, and where the next round's "since last review" starts. Null when git could not write it.
    /// </summary>
    public string? Snapshot { get; init; }

    /// <summary>Gets the ids of the threads it sent.</summary>
    public IReadOnlyList<string> Threads { get; init; } = [];

    /// <summary>Gets the agent's request it answered; null when the user submitted on their own.</summary>
    public string? RequestId { get; init; }

    /// <summary>Gets when it was submitted.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary>A review an agent asked for, until the user submits it.</summary>
public sealed record ReviewRequest
{
    /// <summary>Gets the id: the id of the ask that carries it.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the session that asked.</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>Gets the target to review.</summary>
    public string Target { get; init; } = ReviewTargets.Changes;

    /// <summary>Gets the file, for a file target.</summary>
    public string? Path { get; init; }

    /// <summary>Gets a short title the agent gave; null when it gave none.</summary>
    public string? Title { get; init; }

    /// <summary>Gets when it was asked.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Where a review is: a project and one of its checkouts.</summary>
/// <param name="Project">The project.</param>
/// <param name="Checkout">
/// The folder of a worktree of the project's repository, fully qualified; null for the project folder.
/// </param>
public sealed record ReviewScope(ProjectDescriptor Project, string? Checkout)
{
    /// <summary>Gets the folder of the checkout: the worktree, or the project folder.</summary>
    public string Folder => Checkout ?? Project.ProjectPath;
}

/// <summary>A new comment, as the user writes it.</summary>
/// <param name="Target">One of <see cref="ReviewTargets"/>.</param>
/// <param name="Path">The file, as for <see cref="ReviewThread.Path"/>.</param>
/// <param name="Side">One of <see cref="ReviewSides"/>.</param>
/// <param name="StartLine">The first line, from 1; 0 for the whole file.</param>
/// <param name="EndLine">The last line, inclusive; 0 for the whole file.</param>
/// <param name="Body">The comment, in Markdown; may be empty when a suggestion is given.</param>
/// <param name="Suggestion">The proposed replacement of the lines, or null.</param>
public sealed record ReviewThreadDraft(string Target, string Path, string Side, int StartLine, int EndLine, string Body, string? Suggestion = null);

/// <summary>An agent's answer to one thread.</summary>
/// <param name="Id">The thread.</param>
/// <param name="Reply">The answer, in Markdown.</param>
/// <param name="Addressed">Whether the agent did what the thread asks; false answers a question without claiming a change.</param>
public sealed record ReviewReplyDraft(string Id, string Reply, bool Addressed = true);

/// <summary>What the user submits.</summary>
/// <param name="Target">One of <see cref="ReviewTargets"/>.</param>
/// <param name="Path">The file, for a file target; null for a diff.</param>
/// <param name="Verdict">One of <see cref="ReviewVerdicts"/>.</param>
/// <param name="Overall">The overall comment, or null.</param>
/// <param name="SessionId">The session the round goes to.</param>
/// <param name="RequestId">The agent's request it answers, or null.</param>
public sealed record ReviewSubmission(string Target, string? Path, string Verdict, string? Overall, string SessionId, string? RequestId = null);

/// <summary>A round ready to be sent: its text, and what is recorded once it is sent.</summary>
/// <param name="Round">The round that is recorded once the session has it.</param>
/// <param name="Text">The prompt the session receives.</param>
public sealed record ReviewRoundDraft(ReviewRound Round, string Text);

/// <summary>A recorded round, and what recording it changed, to undo it when the session does not get it.</summary>
/// <param name="Round">The round as recorded.</param>
/// <param name="Threads">The threads it changed, as they were before.</param>
/// <param name="Requests">The requests it answered, which no longer wait.</param>
public sealed record ReviewRoundRecord(ReviewRound Round, IReadOnlyList<ReviewThread> Threads, IReadOnlyList<ReviewRequest> Requests);

/// <summary>The threads of a target as the user and the agent see them now.</summary>
/// <param name="Threads">The threads, re-anchored.</param>
/// <param name="Rounds">The rounds of the target, the oldest first.</param>
/// <param name="Viewed">The files the user marked viewed, with the revision they were at.</param>
/// <param name="Request">The agent's request still waiting, or null.</param>
/// <param name="ReadOnly">Why the review cannot be changed (a newer or broken file), or null.</param>
public sealed record ReviewState(IReadOnlyList<ReviewThread> Threads, IReadOnlyList<ReviewRound> Rounds, IReadOnlyDictionary<string, string> Viewed,
    ReviewRequest? Request, string? ReadOnly);

/// <summary>How many threads of a checkout wait for something.</summary>
/// <param name="Drafts">Comments not sent yet.</param>
/// <param name="Open">Threads sent and not answered.</param>
/// <param name="Addressed">Threads the agent answered, waiting for the user.</param>
public sealed record ReviewCounts(int Drafts, int Open, int Addressed);

/// <summary>One review of a checkout as the Reviews page lists it: a target, what waits, and how it went.</summary>
/// <param name="Checkout">The worktree; null for the project folder.</param>
/// <param name="Missing">Whether the folder of the checkout is gone: its reviews wait for the user to delete them.</param>
/// <param name="Target">One of <see cref="ReviewTargets"/>.</param>
/// <param name="Path">The file of a file target; null for a diff.</param>
/// <param name="Drafts">Comments not sent yet.</param>
/// <param name="Open">Threads sent and not answered.</param>
/// <param name="Addressed">Threads the agent answered, waiting for the user.</param>
/// <param name="Resolved">Threads the user resolved.</param>
/// <param name="Request">The agent's request still waiting, or null.</param>
/// <param name="LastRound">The last round, or null.</param>
/// <param name="UpdatedAt">When something of it last changed.</param>
public sealed record ReviewOverviewEntry(string? Checkout, bool Missing, string Target, string? Path, int Drafts, int Open, int Addressed, int Resolved,
    ReviewRequest? Request, ReviewRound? LastRound, DateTimeOffset? UpdatedAt)
{
    /// <summary>Gets whether the file of a file review is gone from its checkout: the review waits for the user to delete it.</summary>
    public bool FileMissing { get; init; }

    /// <summary>Gets whether the review waits for the user or for an agent: a request, drafts, or threads not resolved.</summary>
    public bool InFlight => Request is not null || Drafts + Open + Addressed > 0;
}

/// <summary>What deleting a review took away.</summary>
/// <param name="Threads">The comments deleted, with their replies.</param>
/// <param name="Rounds">The rounds forgotten.</param>
/// <param name="Requests">The requests of agents that waited for the review: their asks are for the caller to cancel.</param>
public sealed record ReviewDeletion(int Threads, int Rounds, IReadOnlyList<ReviewRequest> Requests)
{
    /// <summary>Gets whether there was anything to delete.</summary>
    public bool Deleted => Threads + Rounds + Requests.Count > 0;
}

/// <summary>A review that cannot be done: the reason, for the caller to report.</summary>
public sealed class ReviewException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">A short code: <c>notFound</c>, <c>invalidPath</c>, <c>invalidLines</c>, <c>invalidCheckout</c>, <c>limit</c>, <c>readOnly</c>, <c>writeFailed</c>, <c>invalid</c>, <c>nothingToSubmit</c>.</param>
    /// <param name="message">What happened, for a person.</param>
    public ReviewException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Gets the short code.</summary>
    public string Code { get; }
}
