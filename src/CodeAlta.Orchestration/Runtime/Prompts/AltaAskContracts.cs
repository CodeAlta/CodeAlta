using System.Text;
using CodeAlta.Orchestration.Runtime.Prompts;

namespace CodeAlta.LiveTool;

/// <summary>
/// Describes a user-facing ask queued by an agent for the current CodeAlta session.
/// </summary>
public sealed record AltaAskRequest
{
    /// <summary>Gets the optional file to review while answering the ask.</summary>
    public AltaAskFile? File { get; init; }

    /// <summary>Gets the questions to present to the user.</summary>
    public IReadOnlyList<AltaAskQuestion> Questions { get; init; } = [];
}

/// <summary>
/// Describes one question in an <see cref="AltaAskRequest"/>.
/// </summary>
public sealed record AltaAskQuestion
{
    /// <summary>Gets the short tab/display title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the question text.</summary>
    public string? Question { get; init; }

    /// <summary>Gets optional explanatory text.</summary>
    public string? Description { get; init; }

    /// <summary>Gets optional fixed choices.</summary>
    public IReadOnlyList<AltaAskChoice> Choices { get; init; } = [];

    /// <summary>Gets optional freeform answer configuration.</summary>
    public AltaAskFreeform? Freeform { get; init; }
}

/// <summary>
/// Describes a selectable answer choice.
/// </summary>
public sealed record AltaAskChoice
{
    /// <summary>Gets the choice title shown to the user and returned in answer Markdown.</summary>
    public string? Title { get; init; }

    /// <summary>Gets optional choice explanatory text.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Describes a freeform answer field.
/// </summary>
public sealed record AltaAskFreeform
{
    /// <summary>Gets the optional freeform field title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets optional placeholder text.</summary>
    public string? Placeholder { get; init; }
}

/// <summary>
/// Describes a file to review while answering an ask.
/// </summary>
public sealed record AltaAskFile
{
    /// <summary>Gets the project- or workspace-relative file path.</summary>
    public string? Path { get; init; }
}

/// <summary>
/// Describes one answered ask question.
/// </summary>
public sealed record AltaAskAnswer
{
    /// <summary>Gets the zero-based question index.</summary>
    public int QuestionIndex { get; init; }

    /// <summary>Gets selected zero-based choice indexes.</summary>
    public IReadOnlyList<int> SelectedChoiceIndexes { get; init; } = [];

    /// <summary>Gets optional freeform answer text.</summary>
    public string? FreeformText { get; init; }
}

/// <summary>
/// Describes user edits and line comments captured while reviewing an attached ask file.
/// </summary>
public sealed record AltaAskFileReview
{
    /// <summary>Gets a value indicating whether the attached file was modified by the user and saved on disk.</summary>
    public bool FileModifiedAndSaved { get; init; }

    /// <summary>Gets submitted comments attached to file lines.</summary>
    public IReadOnlyList<AltaAskFileComment> Comments { get; init; } = [];
}

/// <summary>
/// Describes a submitted comment attached to one line of an ask file.
/// </summary>
public sealed record AltaAskFileComment
{
    /// <summary>Gets the one-based line number the comment was attached to.</summary>
    public int Line { get; init; }

    /// <summary>Gets the submitted comment text.</summary>
    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// Describes a queued ask instance.
/// </summary>
public sealed record AltaQueuedAsk
{
    /// <summary>Gets the immutable response attempt identity issued by the queue owner; null for unqueued presentation-only values.</summary>
    public AltaAskResponseHandle? ResponseHandle { get; init; }

    /// <summary>Gets the response ownership state at the time this snapshot was captured.</summary>
    public AltaAskResponseState ResponseState { get; init; }

    /// <summary>Gets the generated ask id.</summary>
    public required string AskId { get; init; }

    /// <summary>Gets the target source session id.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the queued ask request.</summary>
    public required AltaAskRequest Request { get; init; }

    /// <summary>Gets the caller identity that queued the ask.</summary>
    public required AltaCallerIdentity Caller { get; init; }

    /// <summary>Gets when the ask was queued.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Result returned after queueing an ask.
/// </summary>
public sealed record AltaAskQueueResult
{
    /// <summary>Gets the generated ask id.</summary>
    public required string AskId { get; init; }

    /// <summary>Gets the target source session id.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets immutable observer-failure summaries. Admission is committed even when this list is nonempty.</summary>
    public IReadOnlyList<string> NotificationErrors { get; init; } = [];
}

/// <summary>Reports an exact pending-head removal and any subsequent invalidation failures.</summary>
public sealed record AltaAskRemovalResult
{
    /// <summary>Gets whether the matching head was removed. This does not indicate prompt submission or runtime admission.</summary>
    public required bool Accepted { get; init; }

    /// <summary>Gets immutable observer-failure summaries. An accepted removal remains committed even when this list is nonempty.</summary>
    public IReadOnlyList<string> NotificationErrors { get; init; } = [];
}

/// <summary>
/// Session-scoped ask queue service used by the live tool and frontend.
/// </summary>
public interface IAltaAskService
{
    /// <summary>Invalidates a session's pending snapshot after a committed change; observers must requery.</summary>
    /// <remarks>
    /// Observers run outside queue ownership, may reenter, and are all attempted even if one throws.
    /// Failures (including observer cancellation) are returned in the mutation result, not thrown as mutation failures.
    /// Notifications may interleave and are not an ordered replay stream or proof that an ask is still pending.
    /// </remarks>
    event EventHandler<AltaAskQueueChangedEventArgs>? QueueChanged;

    /// <summary>Copies a validated ask's nested collections and queues the immutable snapshot for the target session.</summary>
    /// <param name="request">The validated ask request.</param>
    /// <param name="sessionId">The target session id.</param>
    /// <param name="caller">The caller identity.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The queued ask id and target session id.</returns>
    /// <remarks>Session ids are trimmed on admission only. Cancellation is checked under ownership before admission, not after commit. Callers must not mutate input collections during this call.</remarks>
    /// <exception cref="ArgumentNullException">The request, session id, or caller is null.</exception>
    /// <exception cref="ArgumentException">The session id is empty or whitespace.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed before admission.</exception>
    Task<AltaAskQueueResult> QueueAsync(AltaAskRequest request, string sessionId, AltaCallerIdentity caller, CancellationToken cancellationToken = default);

    /// <summary>Peeks at the next pending ask for a session without removing it.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The next immutable queued ask snapshot, or <see langword="null"/> when none is pending.</returns>
    /// <exception cref="ArgumentNullException">The session id is null.</exception>
    /// <exception cref="ArgumentException">The session id is empty or whitespace.</exception>
    AltaQueuedAsk? Peek(string sessionId);

    /// <summary>Gets an immutable point-in-time FIFO snapshot for a session, independent of frontend tabs.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The pending asks, or an empty snapshot. Subsequent mutations do not change this snapshot.</returns>
    /// <exception cref="ArgumentNullException">The session id is null.</exception>
    /// <exception cref="ArgumentException">The session id is empty or whitespace.</exception>
    IReadOnlyList<AltaQueuedAsk> GetPending(string sessionId);

    /// <summary>Removes an unclaimed pending head only when both session and ask ids match exactly (ordinal, without trimming).</summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="askId">The expected head's ask id.</param>
    /// <returns>An explicit removal outcome. Stale, wrong-session, non-head, submitting and indeterminate requests leave pending state unchanged and do not notify.</returns>
    /// <remarks>Frontend response cancellation must use the generation-bound <see cref="TryCancelResponse"/> instead.</remarks>
    /// <exception cref="ArgumentNullException">Either id is null.</exception>
    /// <exception cref="ArgumentException">Either id is empty or whitespace.</exception>
    AltaAskRemovalResult TryRemoveHead(string sessionId, string askId);

    /// <summary>Atomically claims a pending response generation, then dispatches and settles it outside queue ownership.</summary>
    /// <remarks>Only positive admission removes the claimed head. Definite non-admission rotates the handle; uncertainty blocks replay. No execution lifetime, persistence or reconnect guarantee is provided.</remarks>
    /// <exception cref="ArgumentNullException">The handle or callback is null.</exception>
    Task<AltaAskResponseResult> RespondAsync(AltaAskResponseHandle handle, Func<Task<SessionPromptResponseResult>> dispatch);

    /// <summary>Cancels only the current unclaimed generation. Stale, foreign, submitting and indeterminate handles are rejected.</summary>
    /// <exception cref="ArgumentNullException">The handle is null.</exception>
    AltaAskRemovalResult TryCancelResponse(AltaAskResponseHandle handle);
}

/// <summary>
/// Event arguments describing an ask queue change.
/// </summary>
public sealed class AltaAskQueueChangedEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="AltaAskQueueChangedEventArgs"/> class.</summary>
    /// <param name="sessionId">The session whose ask queue changed.</param>
    /// <exception cref="ArgumentNullException">The session id is null.</exception>
    /// <exception cref="ArgumentException">The session id is empty or whitespace.</exception>
    public AltaAskQueueChangedEventArgs(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionId = sessionId;
    }

    /// <summary>Gets the session whose ask queue changed.</summary>
    public string SessionId { get; }
}

/// <summary>
/// In-memory FIFO implementation of <see cref="IAltaAskService"/>.
/// </summary>
public sealed partial class AltaAskService : IAltaAskService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<PendingAsk>> _queues = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public event EventHandler<AltaAskQueueChangedEventArgs>? QueueChanged;

    /// <inheritdoc />
    public Task<AltaAskQueueResult> QueueAsync(AltaAskRequest request, string sessionId, AltaCallerIdentity caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(caller);
        cancellationToken.ThrowIfCancellationRequested();

        var queued = new AltaQueuedAsk
        {
            AskId = Guid.CreateVersion7().ToString(),
            SessionId = sessionId.Trim(),
            Request = request with
            {
                Questions = Array.AsReadOnly(request.Questions.Select(static question => question with
                {
                    Choices = Array.AsReadOnly(question.Choices.ToArray()),
                }).ToArray()),
            },
            Caller = caller,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        queued = queued with { ResponseHandle = new AltaAskResponseHandle(queued.SessionId, queued.AskId, 0) };

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_queues.TryGetValue(queued.SessionId, out var queue))
            {
                queue = new Queue<PendingAsk>();
                _queues.Add(queued.SessionId, queue);
            }

            queue.Enqueue(new PendingAsk(queued));
        }

        var errors = NotifyQueueChanged(queued.SessionId);
        return Task.FromResult(new AltaAskQueueResult { AskId = queued.AskId, SessionId = queued.SessionId, NotificationErrors = errors });
    }

    /// <inheritdoc />
    public AltaQueuedAsk? Peek(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            return _queues.TryGetValue(sessionId, out var queue) && queue.Count > 0 ? queue.Peek().Snapshot : null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AltaQueuedAsk> GetPending(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            return Array.AsReadOnly(_queues.TryGetValue(sessionId, out var queue) ? queue.Select(static entry => entry.Snapshot).ToArray() : []);
        }
    }

    /// <inheritdoc />
    public AltaAskRemovalResult TryRemoveHead(string sessionId, string askId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(askId);
        lock (_gate)
        {
            if (!_queues.TryGetValue(sessionId, out var queue) || queue.Count == 0
                || !string.Equals(queue.Peek().Snapshot.AskId, askId, StringComparison.Ordinal)
                || queue.Peek().Snapshot.ResponseState != AltaAskResponseState.Pending)
            {
                return new AltaAskRemovalResult { Accepted = false };
            }

            RemoveHeadCore(sessionId);
        }

        return new AltaAskRemovalResult { Accepted = true, NotificationErrors = NotifyQueueChanged(sessionId) };
    }

    private IReadOnlyList<string> NotifyQueueChanged(string sessionId)
    {
        var observers = QueueChanged;
        if (observers is null)
        {
            return [];
        }

        List<string>? errors = null;
        var args = new AltaAskQueueChangedEventArgs(sessionId);
        foreach (EventHandler<AltaAskQueueChangedEventArgs> observer in observers.GetInvocationList())
        {
            try
            {
                observer(this, args);
            }
            catch (Exception ex)
            {
                // Return diagnostics with the committed outcome; no logger, sink, or frontend belongs to queue ownership.
                (errors ??= []).Add(ex.Message);
            }
        }

        return errors is null ? [] : Array.AsReadOnly(errors.ToArray());
    }
}

/// <summary>
/// Validates and normalizes ask requests.
/// </summary>
public static class AltaAskValidator
{
    /// <summary>Gets the maximum number of questions accepted in one ask.</summary>
    public const int MaxQuestions = 12;

    /// <summary>Gets the maximum number of choices accepted for one question.</summary>
    public const int MaxChoicesPerQuestion = 20;

    /// <summary>Gets the maximum length for short title fields.</summary>
    public const int MaxTitleLength = 120;

    /// <summary>Gets the maximum length for question and description text fields.</summary>
    public const int MaxTextLength = 4000;

    /// <summary>Gets the maximum length for placeholder text fields.</summary>
    public const int MaxPlaceholderLength = 500;

    /// <summary>Validates and normalizes an ask request.</summary>
    /// <param name="request">The request to validate.</param>
    /// <param name="allowedRoots">Optional file roots allowed for request file paths.</param>
    /// <param name="baseDirectory">The base directory used for relative file path resolution.</param>
    /// <returns>The normalized request.</returns>
    /// <exception cref="ArgumentException">Thrown when the request is invalid.</exception>
    public static AltaAskRequest ValidateAndNormalize(AltaAskRequest request, IReadOnlyList<string>? allowedRoots = null, string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Questions is null || request.Questions.Count == 0)
        {
            throw new ArgumentException("Ask payload requires at least one question.", nameof(request));
        }

        if (request.Questions.Count > MaxQuestions)
        {
            throw new ArgumentException($"Ask payload contains too many questions; maximum is {MaxQuestions}.", nameof(request));
        }

        var questions = new AltaAskQuestion[request.Questions.Count];
        for (var index = 0; index < request.Questions.Count; index++)
        {
            questions[index] = ValidateQuestion(request.Questions[index], index);
        }

        var file = request.File is null ? null : ValidateFile(request.File, allowedRoots, baseDirectory);
        return new AltaAskRequest { File = file, Questions = questions };
    }

    private static AltaAskQuestion ValidateQuestion(AltaAskQuestion question, int index)
    {
        if (question is null)
        {
            throw new ArgumentException($"Question {index + 1} is required.");
        }

        var title = RequireText(question.Title, $"Question {index + 1} title", MaxTitleLength);
        var text = RequireText(question.Question, $"Question {index + 1} text", MaxTextLength);
        var description = OptionalText(question.Description, $"Question {index + 1} description", MaxTextLength);
        var questionChoices = question.Choices ?? [];
        if (questionChoices.Count == 0 && question.Freeform is null)
        {
            throw new ArgumentException($"Question {index + 1} requires choices or freeform.");
        }

        if (questionChoices.Count > MaxChoicesPerQuestion)
        {
            throw new ArgumentException($"Question {index + 1} contains too many choices; maximum is {MaxChoicesPerQuestion}.");
        }

        var choices = new AltaAskChoice[questionChoices.Count];
        for (var choiceIndex = 0; choiceIndex < questionChoices.Count; choiceIndex++)
        {
            var choice = questionChoices[choiceIndex] ?? throw new ArgumentException($"Question {index + 1} choice {choiceIndex + 1} is required.");
            choices[choiceIndex] = new AltaAskChoice
            {
                Title = RequireText(choice.Title, $"Question {index + 1} choice {choiceIndex + 1} title", MaxTitleLength),
                Description = OptionalText(choice.Description, $"Question {index + 1} choice {choiceIndex + 1} description", MaxTextLength),
            };
        }

        var freeform = question.Freeform is null
            ? null
            : new AltaAskFreeform
            {
                Title = OptionalText(question.Freeform.Title, $"Question {index + 1} freeform title", MaxTitleLength),
                Placeholder = OptionalText(question.Freeform.Placeholder, $"Question {index + 1} freeform placeholder", MaxPlaceholderLength),
            };

        return new AltaAskQuestion
        {
            Title = title,
            Question = text,
            Description = description,
            Choices = choices,
            Freeform = freeform,
        };
    }

    private static AltaAskFile ValidateFile(AltaAskFile file, IReadOnlyList<string>? allowedRoots, string? baseDirectory)
    {
        var path = RequireText(file.Path, "File path", 1000);
        if (System.IO.Path.IsPathFullyQualified(path) && allowedRoots is null or { Count: 0 })
        {
            throw new ArgumentException("File path must be relative when no allowed roots are available.");
        }

        var rootCandidates = allowedRoots?
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(static root => System.IO.Path.GetFullPath(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        var baseRoot = !string.IsNullOrWhiteSpace(baseDirectory)
            ? System.IO.Path.GetFullPath(baseDirectory)
            : rootCandidates.FirstOrDefault() ?? Environment.CurrentDirectory;
        var fullPath = System.IO.Path.GetFullPath(System.IO.Path.IsPathFullyQualified(path) ? path : System.IO.Path.Combine(baseRoot, path));
        if (rootCandidates.Length > 0 && !rootCandidates.Any(root => IsUnderRoot(fullPath, root)))
        {
            throw new ArgumentException("File path must stay inside the session workspace or project roots.");
        }

        var displayPath = rootCandidates.Length > 0
            ? ToRelativePath(rootCandidates.First(root => IsUnderRoot(fullPath, root)), fullPath)
            : path.Replace('\\', '/');
        return new AltaAskFile { Path = displayPath };
    }

    private static string RequireText(string? value, string field, int maxLength)
    {
        var normalized = OptionalText(value, field, maxLength);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException($"{field} is required.");
        }

        return normalized;
    }

    private static string? OptionalText(string? value, string field, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"{field} is too long; maximum is {maxLength} characters.");
        }

        return normalized.Length == 0 ? null : normalized;
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        return string.Equals(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar), root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToRelativePath(string root, string path)
        => System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
}

/// <summary>
/// Formats ask answers as compact Markdown prompt text.
/// </summary>
public static class AltaAskAnswerMarkdownFormatter
{
    /// <summary>Formats ask answers as Markdown without exposing the ask id.</summary>
    /// <param name="request">The original ask request.</param>
    /// <param name="answers">The answers supplied by the user.</param>
    /// <returns>Markdown suitable for a normal user prompt.</returns>
    public static string Format(AltaAskRequest request, IReadOnlyList<AltaAskAnswer> answers)
        => Format(request, answers, fileReview: null);

    /// <summary>Formats ask answers and attached-file review comments as Markdown without exposing the ask id.</summary>
    /// <param name="request">The original ask request.</param>
    /// <param name="answers">The answers supplied by the user.</param>
    /// <param name="fileReview">The optional attached-file review details captured in the UI.</param>
    /// <returns>Markdown suitable for a normal user prompt.</returns>
    public static string Format(AltaAskRequest request, IReadOnlyList<AltaAskAnswer> answers, AltaAskFileReview? fileReview)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(answers);
        var answersByQuestion = answers
            .Where(static answer => answer.QuestionIndex >= 0)
            .GroupBy(static answer => answer.QuestionIndex)
            .ToDictionary(static group => group.Key, static group => group.Last());
        var builder = new StringBuilder();
        builder.AppendLine("# Ask response");
        builder.AppendLine();
        if (!string.IsNullOrWhiteSpace(request.File?.Path))
        {
            builder.Append("File: `").Append(EscapeCodeSpan(request.File.Path!)).AppendLine("`");
            builder.AppendLine();
            AppendFileReview(builder, fileReview);
        }

        builder.AppendLine("## Answers");
        builder.AppendLine();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            builder.Append(index + 1).Append(". ").AppendLine(EscapeInline(question.Question ?? question.Title ?? $"Question {index + 1}"));
            builder.AppendLine();
            if (answersByQuestion.TryGetValue(index, out var answer))
            {
                AppendAnswer(builder, question, answer);
            }
            else
            {
                builder.AppendLine("No answer provided.");
            }

            if (index + 1 < request.Questions.Count)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendFileReview(StringBuilder builder, AltaAskFileReview? fileReview)
    {
        var comments = fileReview?.Comments ?? [];
        if (fileReview is null || (!fileReview.FileModifiedAndSaved && comments.Count == 0))
        {
            return;
        }

        builder.AppendLine("## File User Comments");
        builder.AppendLine();
        if (fileReview.FileModifiedAndSaved)
        {
            builder.AppendLine("The file has been modified by the user and saved on disk.");
            if (comments.Count > 0)
            {
                builder.AppendLine();
            }
        }

        for (var index = 0; index < comments.Count; index++)
        {
            var comment = comments[index];
            builder.Append("Line ").Append(Math.Max(1, comment.Line)).AppendLine(":");
            AppendFreeform(builder, comment.Text);
            if (index + 1 < comments.Count)
            {
                builder.AppendLine();
            }
        }

        builder.AppendLine();
    }

    private static void AppendAnswer(StringBuilder builder, AltaAskQuestion question, AltaAskAnswer answer)
    {
        var wrote = false;
        foreach (var choiceIndex in answer.SelectedChoiceIndexes.Distinct().Order())
        {
            if ((uint)choiceIndex < (uint)question.Choices.Count)
            {
                builder.AppendLine(EscapeInline(question.Choices[choiceIndex].Title ?? $"Choice {choiceIndex + 1}"));
                wrote = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(answer.FreeformText))
        {
            if (wrote)
            {
                builder.AppendLine();
            }

            AppendFreeform(builder, answer.FreeformText!);
            wrote = true;
        }

        if (!wrote)
        {
            builder.AppendLine("No answer provided.");
        }
    }

    private static void AppendFreeform(StringBuilder builder, string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (RequiresFence(normalized))
        {
            builder.AppendLine("````text");
            builder.AppendLine(normalized.Replace("````", "``` `", StringComparison.Ordinal));
            builder.AppendLine("````");
            return;
        }

        builder.AppendLine(EscapeInline(normalized));
    }

    private static bool RequiresFence(string text)
        => text.Contains('\n') || text.StartsWith('#') || text.StartsWith('>') || text.StartsWith('-') || text.StartsWith('*') || text.StartsWith("```", StringComparison.Ordinal);

    private static string EscapeInline(string text)
        => text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);

    private static string EscapeCodeSpan(string text)
        => text.Replace("`", "\u02cb", StringComparison.Ordinal);
}

