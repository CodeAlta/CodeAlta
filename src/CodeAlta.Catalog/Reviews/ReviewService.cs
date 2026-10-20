using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Catalog.Worktrees;

namespace CodeAlta.Catalog.Reviews;

/// <summary>
/// The reviews of the user: comments on the lines of plans, files and changes, sent to an agent in rounds that
/// carry a verdict, and answered by the agent thread by thread.
/// </summary>
/// <remarks>
/// <para>
/// Each checkout of a project (the project folder, each worktree) keeps its reviews in one file of the state
/// folder of the application, <c>reviews/&lt;project&gt;/&lt;checkout&gt;.json</c>: never inside the checkout,
/// where the file would show in the changes under review and be committed with them.
/// </para>
/// <para>
/// Writes replace the file at once. The file is read again when it changed on disk, so the window and the
/// <c>alta</c> commands of several sessions see the same state. A file of a newer version or that cannot be read
/// is never overwritten: its reviews are read-only until it is fixed or deleted.
/// </para>
/// </remarks>
public sealed class ReviewService
{
    /// <summary>The version of the file this service writes.</summary>
    public const int FileVersion = 1;

    /// <summary>The most threads a checkout keeps; the oldest resolved ones make room first.</summary>
    public const int MaximumThreads = 500;

    /// <summary>The longest comment, suggestion or reply, in UTF-16 units.</summary>
    public const int MaximumText = 16 * 1024;

    /// <summary>The most replies of a thread.</summary>
    public const int MaximumReplies = 50;

    /// <summary>The most rounds a checkout keeps; the oldest are forgotten.</summary>
    public const int MaximumRounds = 200;

    /// <summary>The longest path of a file, in UTF-16 units.</summary>
    public const int MaximumPathLength = 1024;

    private const string ProjectCheckoutKey = "project";
    private readonly string _root;
    private readonly IReviewTextSource _text;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Cached> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates the service.</summary>
    /// <param name="stateRoot">The state folder of this instance of the application; the reviews are in its <c>reviews</c> folder.</param>
    /// <param name="text">Reads the files the reviews are about; null reads them with git.</param>
    /// <param name="time">The clock; the system's when null.</param>
    /// <exception cref="ArgumentException"><paramref name="stateRoot"/> is blank.</exception>
    public ReviewService(string stateRoot, IReviewTextSource? text = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        _root = Path.Combine(stateRoot, "reviews");
        _text = text ?? new GitReviewTextSource();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after a change made through this service, with the project it changed.</summary>
    public event Action<string>? Changed;

    /// <summary>Gets the folder the reviews of every project are kept in.</summary>
    public string Folder => _root;

    /// <summary>Gets what reads the files the reviews are about.</summary>
    public IReviewTextSource TextSource => _text;

    /// <summary>Answers whether a path can name a file of a review: relative, of plain segments, inside its root.</summary>
    /// <param name="path">The path, with <c>/</c>.</param>
    /// <returns>True when the path is safe to read below a root.</returns>
    public static bool IsValidPath(string? path)
        => path is { Length: > 0 and <= MaximumPathLength } && !path.Any(char.IsControl) && !path.Contains('\\') && !path.Contains(':')
            && !Path.IsPathRooted(path) && path.Split('/').All(static part => part.Length > 0 && part is not "." and not "..");

    /// <summary>Reads the threads of a target, placed on the lines of the files as they are now.</summary>
    /// <param name="scope">The checkout.</param>
    /// <param name="target">One of <see cref="ReviewTargets"/>; null reads every target.</param>
    /// <param name="path">For a file target, the file; null reads every file.</param>
    /// <param name="includeResolved">Whether resolved threads are returned.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>The review.</returns>
    /// <exception cref="ReviewException">The checkout is not one of the project.</exception>
    public async Task<ReviewState> ListAsync(ReviewScope scope, string? target, string? path, bool includeResolved, CancellationToken cancellationToken)
    {
        var file = Store(scope);
        var (data, readOnly) = Load(file);
        var wanted = data.Threads.Where(thread => Matches(thread, target, path) && (includeResolved || thread.State != ReviewThreadStates.Resolved)).ToArray();
        var placed = await PlaceAsync(scope, wanted, cancellationToken).ConfigureAwait(false);
        if (readOnly is null && placed.Any(static pair => !ReferenceEquals(pair.Before, pair.After)))
        {
            // The new places are kept, so that the next reading starts from them; a thread changed meanwhile wins.
            Update(scope, file, current =>
            {
                var changed = false;
                foreach (var (before, after) in placed)
                {
                    var index = current.Threads.FindIndex(thread => thread.Id == after.Id);
                    if (index >= 0 && current.Threads[index] == before && before != after)
                    {
                        current.Threads[index] = after;
                        changed = true;
                    }
                }

                return changed;
            }, notify: false);
        }

        var threads = placed.Select(static pair => pair.After).ToArray();
        var rounds = data.Rounds.Where(round => Matches(round, target, path)).ToArray();
        var request = data.Requests.LastOrDefault(request => Matches(request, target, path));
        var viewed = data.Viewed.Where(entry => target is null || entry.Key.StartsWith(target + ":", StringComparison.Ordinal))
            .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);
        return new(threads, rounds, viewed, request, readOnly);
    }

    /// <summary>Writes a new comment of the user, as a draft.</summary>
    /// <param name="scope">The checkout.</param>
    /// <param name="draft">The comment.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>The thread.</returns>
    /// <exception cref="ReviewException">The comment is invalid, the lines are not in the file, or the review cannot be written.</exception>
    public async Task<ReviewThread> AddThreadAsync(ReviewScope scope, ReviewThreadDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var file = Store(scope);
        if (!ReviewTargets.IsValid(draft.Target)) throw new ReviewException("invalid", "Unknown review target.");
        if (!IsValidPath(draft.Path)) throw new ReviewException("invalidPath", "The path must be relative to the checkout, without `..`.");
        if (draft.Side is not (ReviewSides.Old or ReviewSides.New) || (draft.Side == ReviewSides.Old && !ReviewTargets.IsDiff(draft.Target)))
            throw new ReviewException("invalid", "A file has only its new side.");
        var body = Text(draft.Body, "comment", allowEmpty: draft.Suggestion is not null);
        var suggestion = draft.Suggestion is null ? null : Text(draft.Suggestion, "suggestion", allowEmpty: true);
        var anchor = string.Empty;
        if (draft.StartLine != 0 || draft.EndLine != 0)
        {
            if (draft.StartLine < 1 || draft.EndLine < draft.StartLine) throw new ReviewException("invalidLines", "The lines must be a range that starts at 1 or later.");
            var text = await _text.ReadAsync(scope, draft.Target, draft.Side, draft.Path, cancellationToken).ConfigureAwait(false)
                ?? throw new ReviewException("invalidLines", "The file cannot be read as text.");
            var lines = ReviewAnchoring.SplitLines(text);
            if (draft.EndLine > lines.Length) throw new ReviewException("invalidLines", "The lines are past the end of the file.");
            anchor = ReviewAnchoring.Excerpt(lines, draft.StartLine, draft.EndLine);
        }

        var now = _time.GetUtcNow();
        ReviewThread? added = null;
        Update(scope, file, data =>
        {
            if (data.Threads.Count >= MaximumThreads)
            {
                var oldest = data.Threads.Where(static thread => thread.State == ReviewThreadStates.Resolved).OrderBy(static thread => thread.ResolvedAt).FirstOrDefault()
                    ?? throw new ReviewException("limit", $"A checkout keeps at most {MaximumThreads} comments that are not resolved.");
                data.Threads.Remove(oldest);
            }

            added = new ReviewThread
            {
                Id = "c" + data.NextThread.ToString(CultureInfo.InvariantCulture),
                Target = draft.Target,
                Path = draft.Path,
                Side = draft.Side,
                StartLine = draft.StartLine,
                EndLine = draft.EndLine,
                Anchor = anchor,
                Body = body,
                Suggestion = suggestion,
                State = ReviewThreadStates.Draft,
                CreatedAt = now,
                UpdatedAt = now,
            };
            data.NextThread++;
            data.Threads.Add(added);
            return true;
        });
        return added!;
    }

    /// <summary>Changes the text of a comment of the user.</summary>
    /// <param name="scope">The checkout.</param>
    /// <param name="id">The thread.</param>
    /// <param name="body">The new comment; null keeps it.</param>
    /// <param name="suggestion">The new suggestion; null keeps it.</param>
    /// <param name="clearSuggestion">Whether the suggestion is removed.</param>
    /// <returns>The thread.</returns>
    /// <exception cref="ReviewException">There is no such thread, a text is invalid, or the review cannot be written.</exception>
    public ReviewThread UpdateThread(ReviewScope scope, string id, string? body, string? suggestion, bool clearSuggestion)
    {
        var file = Store(scope);
        var nextBody = body is null ? null : Text(body, "comment", allowEmpty: true);
        var nextSuggestion = suggestion is null ? null : Text(suggestion, "suggestion", allowEmpty: true);
        return Change(scope, file, id, thread =>
        {
            var changed = thread with
            {
                Body = nextBody ?? thread.Body,
                Suggestion = clearSuggestion ? null : nextSuggestion ?? thread.Suggestion,
                UpdatedAt = _time.GetUtcNow(),
            };
            if (string.IsNullOrWhiteSpace(changed.Body) && changed.Suggestion is null) throw new ReviewException("invalid", "A comment needs a text or a suggestion.");
            // A comment the agent answered and that the user changes asks again.
            return changed.State == ReviewThreadStates.Addressed ? changed with { State = ReviewThreadStates.Open } : changed;
        });
    }

    /// <summary>Deletes a thread; its id is not used again.</summary>
    /// <param name="scope">The checkout.</param>
    /// <param name="id">The thread.</param>
    /// <returns>False when there is no such thread.</returns>
    /// <exception cref="ReviewException">The review cannot be written.</exception>
    public bool DeleteThread(ReviewScope scope, string id)
    {
        var file = Store(scope);
        var removed = false;
        Update(scope, file, data =>
        {
            removed = data.Threads.RemoveAll(thread => thread.Id == id) > 0;
            return removed;
        });
        return removed;
    }

    /// <summary>Resolves a thread, or opens a resolved one again. Only the user does.</summary>
    /// <param name="scope">The checkout.</param>
    /// <param name="id">The thread.</param>
    /// <param name="resolved">True resolves; false opens it again.</param>
    /// <returns>The thread.</returns>
    /// <exception cref="ReviewException">There is no such thread, or the review cannot be written.</exception>
    public ReviewThread Resolve(ReviewScope scope, string id, bool resolved)
    {
        var file = Store(scope);
        var now = _time.GetUtcNow();
        return Change(scope, file, id, thread => resolved
            ? thread with { State = ReviewThreadStates.Resolved, ResolvedAt = now }
            : thread with { State = thread.Round is null ? ReviewThreadStates.Draft : ReviewThreadStates.Open, ResolvedAt = null });
    }

    /// <summary>Resolves every thread of a target that the agent addressed.</summary>
    /// <param name="scope">The checkout.</param>
    /// <param name="target">One of <see cref="ReviewTargets"/>.</param>
    /// <param name="path">For a file target, the file.</param>
    /// <returns>How many threads were resolved.</returns>
    /// <exception cref="ReviewException">The review cannot be written.</exception>
    public int ResolveAddressed(ReviewScope scope, string target, string? path)
    {
        var file = Store(scope);
        var now = _time.GetUtcNow();
        var count = 0;
        Update(scope, file, data =>
        {
            for (var index = 0; index < data.Threads.Count; index++)
            {
                if (data.Threads[index] is { State: ReviewThreadStates.Addressed } thread && Matches(thread, target, path))
                {
                    data.Threads[index] = thread with { State = ReviewThreadStates.Resolved, ResolvedAt = now };
                    count++;
                }
            }

            return count > 0;
        });
        return count;
    }

    /// <summary>Adds a reply of the user under a thread; a thread the agent addressed is open again.</summary>
    /// <param name="scope">The checkout.</param>
    /// <param name="id">The thread.</param>
    /// <param name="body">The reply.</param>
    /// <returns>The thread.</returns>
    /// <exception cref="ReviewException">There is no such thread, the reply is invalid, or the review cannot be written.</exception>
    public ReviewThread ReplyAsUser(ReviewScope scope, string id, string body)
    {
        var file = Store(scope);
        var text = Text(body, "reply", allowEmpty: false);
        var now = _time.GetUtcNow();
        return Change(scope, file, id, thread =>
        {
            if (thread.Replies.Count >= MaximumReplies) throw new ReviewException("limit", $"A comment has at most {MaximumReplies} replies.");
            return thread with
            {
                Replies = [.. thread.Replies, new ReviewReply { Author = ReviewAuthors.User, Body = text, At = now }],
                State = thread.State == ReviewThreadStates.Addressed ? ReviewThreadStates.Open : thread.State,
            };
        });
    }

    /// <summary>Adds the answers of an agent under threads, in one change.</summary>
    /// <param name="scope">The checkout.</param>
    /// <param name="sessionId">The session of the agent.</param>
    /// <param name="replies">The answers.</param>
    /// <returns>The threads answered.</returns>
    /// <exception cref="ReviewException">A thread does not exist, a reply is invalid, or the review cannot be written; nothing is changed then.</exception>
    public IReadOnlyList<ReviewThread> ReplyAsAgent(ReviewScope scope, string? sessionId, IReadOnlyList<ReviewReplyDraft> replies)
    {
        ArgumentNullException.ThrowIfNull(replies);
        if (replies.Count == 0) throw new ReviewException("invalid", "Give at least one reply.");
        var file = Store(scope);
        var texts = replies.Select(reply => (reply.Id, Text: Text(reply.Reply, "reply", allowEmpty: false), reply.Addressed)).ToArray();
        var now = _time.GetUtcNow();
        var answered = new List<ReviewThread>();
        Update(scope, file, data =>
        {
            foreach (var (id, text, addressed) in texts)
            {
                var index = data.Threads.FindIndex(thread => thread.Id == id);
                if (index < 0) throw new ReviewException("notFound", $"There is no comment `{id}` in this checkout.");
                var thread = data.Threads[index];
                if (thread.Replies.Count >= MaximumReplies) throw new ReviewException("limit", $"Comment `{id}` has {MaximumReplies} replies already.");
                thread = thread with
                {
                    Replies = [.. thread.Replies, new ReviewReply { Author = ReviewAuthors.Agent, SessionId = sessionId, Body = text, At = now }],
                    // Only the user resolves; a draft the agent read through `alta review list` stays a draft.
                    State = addressed && thread.State is ReviewThreadStates.Open ? ReviewThreadStates.Addressed : thread.State,
                };
                data.Threads[index] = thread;
                answered.Add(thread);
            }

            return true;
        });
        return answered;
    }

    /// <summary>Records that the user viewed a file of a diff at a revision, or clears it.</summary>
    /// <param name="scope">The checkout.</param>
    /// <param name="target">One of <see cref="ReviewTargets"/>.</param>
    /// <param name="path">The file.</param>
    /// <param name="revision">The revision of the file the user saw; null clears the mark.</param>
    /// <exception cref="ReviewException">The path is invalid, or the review cannot be written.</exception>
    public void SetViewed(ReviewScope scope, string target, string path, string? revision)
    {
        if (!ReviewTargets.IsValid(target) || !IsValidPath(path)) throw new ReviewException("invalidPath", "Invalid file.");
        if (revision is { Length: > 128 }) throw new ReviewException("invalid", "Invalid revision.");
        var file = Store(scope);
        Update(scope, file, data =>
        {
            var key = target + ":" + path;
            if (revision is null) return data.Viewed.Remove(key);
            if (data.Viewed.TryGetValue(key, out var known) && known == revision) return false;
            if (!data.Viewed.ContainsKey(key) && data.Viewed.Count >= 4 * MaximumThreads) data.Viewed.Remove(data.Viewed.Keys.First());
            data.Viewed[key] = revision;
            return true;
        });
    }

    /// <summary>Records a review an agent asked for, until the user submits it.</summary>
    /// <param name="scope">The checkout.</param>
    /// <param name="request">The request.</param>
    /// <exception cref="ReviewException">The review cannot be written.</exception>
    public void AddRequest(ReviewScope scope, ReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var file = Store(scope);
        Update(scope, file, data =>
        {
            data.Requests.RemoveAll(existing => existing.SessionId == request.SessionId || existing.Id == request.Id);
            data.Requests.Add(request);
            if (data.Requests.Count > 50) data.Requests.RemoveAt(0);
            return true;
        });
    }

    /// <summary>Forgets a request: answered, cancelled, or no longer waited for.</summary>
    /// <param name="scope">The checkout.</param>
    /// <param name="id">The request.</param>
    /// <returns>False when there was no such request.</returns>
    public bool RemoveRequest(ReviewScope scope, string id)
    {
        var file = Store(scope);
        var removed = false;
        try
        {
            Update(scope, file, data => removed = data.Requests.RemoveAll(request => request.Id == id) > 0);
        }
        catch (ReviewException)
        {
            return false;
        }

        return removed;
    }

    /// <summary>
    /// Prepares the round of a submission: its text, with every draft of the target and every thread still open,
    /// and for a diff a snapshot of the work tree. Nothing is recorded until <see cref="CommitRound"/>.
    /// </summary>
    /// <param name="scope">The checkout.</param>
    /// <param name="submission">What the user submits.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>The round and its text.</returns>
    /// <exception cref="ReviewException">The submission is invalid, or the review is read-only.</exception>
    public async Task<ReviewRoundDraft> PrepareRoundAsync(ReviewScope scope, ReviewSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (!ReviewTargets.IsValid(submission.Target)) throw new ReviewException("invalid", "Unknown review target.");
        if (submission.Target == ReviewTargets.File && !IsValidPath(submission.Path)) throw new ReviewException("invalidPath", "Invalid file.");
        if (!ReviewVerdicts.IsValid(submission.Verdict)) throw new ReviewException("invalid", "Unknown verdict.");
        ArgumentException.ThrowIfNullOrWhiteSpace(submission.SessionId);
        var overall = submission.Overall is null ? null : Text(submission.Overall, "overall comment", allowEmpty: true);
        var path = submission.Target == ReviewTargets.File ? submission.Path : null;
        var file = Store(scope);
        var (data, readOnly) = Load(file);
        if (readOnly is not null) throw new ReviewException("readOnly", readOnly);
        var threads = data.Threads.Where(thread => Matches(thread, submission.Target, path)
            && thread.State is ReviewThreadStates.Draft or ReviewThreadStates.Open).ToArray();
        if (threads.Length == 0 && string.IsNullOrWhiteSpace(overall) && submission.Verdict == ReviewVerdicts.Commented)
            throw new ReviewException("nothingToSubmit", "There is no comment to send.");

        var placed = await PlaceAsync(scope, threads, cancellationToken).ConfigureAwait(false);
        var snapshot = ReviewTargets.IsDiff(submission.Target) ? await _text.SnapshotAsync(scope, cancellationToken).ConfigureAwait(false) : null;
        var items = new List<ReviewPromptComposer.Item>(placed.Count);
        var texts = new Dictionary<(string Path, string Side), string[]?>();
        foreach (var (_, thread) in placed)
        {
            var lines = await LinesAsync(scope, thread.Target, thread.Side, thread.Path, texts, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> snippet = thread.WholeFile ? []
                : thread.Outdated || lines is null ? ReviewAnchoring.SplitLines(thread.Anchor)
                : lines[(thread.StartLine - 1)..thread.EndLine];
            IReadOnlyList<bool>? added = null;
            if (ReviewTargets.IsDiff(thread.Target) && thread.Side == ReviewSides.New && snippet.Count > 0)
            {
                var old = await LinesAsync(scope, thread.Target, ReviewSides.Old, thread.Path, texts, cancellationToken).ConfigureAwait(false);
                var known = old is null ? [] : old.Select(static line => line.TrimEnd()).ToHashSet(StringComparer.Ordinal);
                added = [.. snippet.Select(line => !known.Contains(line.TrimEnd()))];
            }

            items.Add(new(thread, snippet, added, thread.State != ReviewThreadStates.Draft));
        }

        var number = data.Rounds.Count(round => Matches(round, submission.Target, path)) + 1;
        var round = new ReviewRound
        {
            Id = "r" + data.NextRound.ToString(CultureInfo.InvariantCulture),
            Target = submission.Target,
            Path = path,
            Verdict = submission.Verdict,
            Overall = string.IsNullOrWhiteSpace(overall) ? null : overall,
            SessionId = submission.SessionId,
            Snapshot = snapshot,
            Threads = [.. threads.Select(static thread => thread.Id)],
            RequestId = submission.RequestId,
            At = _time.GetUtcNow(),
        };
        return new(round, ReviewPromptComposer.Compose(round, number, items));
    }

    /// <summary>
    /// Records a round once the session has it: its drafts become open, an approval resolves what the agent
    /// addressed, and the request it answers is forgotten.
    /// </summary>
    /// <param name="scope">The checkout.</param>
    /// <param name="draft">The prepared round.</param>
    /// <returns>The round as recorded.</returns>
    /// <exception cref="ReviewException">The review cannot be written.</exception>
    public ReviewRound CommitRound(ReviewScope scope, ReviewRoundDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var file = Store(scope);
        ReviewRound? recorded = null;
        var now = _time.GetUtcNow();
        Update(scope, file, data =>
        {
            recorded = draft.Round with { Id = "r" + data.NextRound.ToString(CultureInfo.InvariantCulture) };
            data.NextRound++;
            var sent = draft.Round.Threads.ToHashSet(StringComparer.Ordinal);
            for (var index = 0; index < data.Threads.Count; index++)
            {
                var thread = data.Threads[index];
                if (sent.Contains(thread.Id))
                {
                    thread = thread with { State = thread.State == ReviewThreadStates.Draft ? ReviewThreadStates.Open : thread.State, Round = recorded.Id };
                }

                if (recorded.Verdict == ReviewVerdicts.Approved && thread.State == ReviewThreadStates.Addressed && Matches(thread, recorded.Target, recorded.Path))
                {
                    thread = thread with { State = ReviewThreadStates.Resolved, ResolvedAt = now };
                }

                data.Threads[index] = thread;
            }

            data.Rounds.Add(recorded);
            if (data.Rounds.Count > MaximumRounds) data.Rounds.RemoveRange(0, data.Rounds.Count - MaximumRounds);
            if (recorded.RequestId is { } request) data.Requests.RemoveAll(value => value.Id == request);
            data.Requests.RemoveAll(value => value.SessionId == recorded.SessionId && value.Target == recorded.Target && value.Path == recorded.Path);
            return true;
        });
        return recorded!;
    }

    /// <summary>Counts what waits in a checkout.</summary>
    /// <param name="scope">The checkout.</param>
    /// <returns>The counts.</returns>
    public ReviewCounts Counts(ReviewScope scope)
    {
        var (data, _) = Load(Store(scope));
        return new(data.Threads.Count(static thread => thread.State == ReviewThreadStates.Draft),
            data.Threads.Count(static thread => thread.State == ReviewThreadStates.Open),
            data.Threads.Count(static thread => thread.State == ReviewThreadStates.Addressed));
    }

    /// <summary>Gets the last round of a target of a checkout.</summary>
    /// <param name="scope">The checkout.</param>
    /// <param name="target">One of <see cref="ReviewTargets"/>; null for any.</param>
    /// <returns>The round, or null when there was none.</returns>
    public ReviewRound? LastRound(ReviewScope scope, string? target)
    {
        var (data, _) = Load(Store(scope));
        return data.Rounds.LastOrDefault(round => target is null || round.Target == target);
    }

    /// <summary>
    /// Answers whether the work tree of a checkout is the one the last round of its diff approved: what may be
    /// committed while the user reviews changes before commit.
    /// </summary>
    /// <param name="scope">The checkout.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>True when the last diff round approved and its snapshot is the work tree now.</returns>
    public async Task<bool> IsApprovedAsync(ReviewScope scope, CancellationToken cancellationToken)
    {
        var (data, _) = Load(Store(scope));
        var last = data.Rounds.LastOrDefault(static round => ReviewTargets.IsDiff(round.Target));
        if (last is not { Verdict: ReviewVerdicts.Approved, Snapshot: { } approved })
        {
            return false;
        }

        var now = await _text.SnapshotAsync(scope, cancellationToken).ConfigureAwait(false);
        return string.Equals(now, approved, StringComparison.Ordinal);
    }

    /// <summary>Lists the checkouts of a project that keep reviews, and whether their folder still exists.</summary>
    /// <param name="projectId">The project.</param>
    /// <returns>The folders and whether each one is missing.</returns>
    public IReadOnlyList<(string Checkout, bool Missing)> ListCheckouts(string projectId)
    {
        var folder = ProjectFolder(projectId);
        if (folder is null || !Directory.Exists(folder)) return [];
        var checkouts = new List<(string, bool)>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(path);
                if (JsonSerializer.Deserialize(stream, ReviewJsonContext.Default.ReviewFile)?.Checkout is { Length: > 0 } checkout)
                {
                    checkouts.Add((checkout, !Directory.Exists(checkout)));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // A file that cannot be read is listed by the storage page as a file.
            }
        }

        return checkouts;
    }

    /// <summary>Deletes the reviews of a project, every checkout included.</summary>
    /// <param name="projectId">The project.</param>
    /// <returns>False when the folder could not be deleted.</returns>
    public bool DeleteProject(string projectId)
    {
        var folder = ProjectFolder(projectId);
        if (folder is null) return false;
        lock (_gate)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                foreach (var key in _cache.Keys.Where(key => key.StartsWith(folder, StringComparison.Ordinal)).ToArray()) _cache.Remove(key);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        Changed?.Invoke(projectId);
        return true;
    }

    /// <summary>Deletes the reviews of one checkout of a project, such as a worktree that is gone.</summary>
    /// <param name="projectId">The project.</param>
    /// <param name="checkout">The folder of the checkout, as <see cref="ListCheckouts"/> gives it.</param>
    /// <returns>False when there was nothing to delete or it could not be deleted.</returns>
    public bool DeleteCheckout(string projectId, string checkout)
    {
        var folder = ProjectFolder(projectId);
        if (folder is null || string.IsNullOrWhiteSpace(checkout)) return false;
        var path = Path.Combine(folder, CheckoutKey(checkout) + ".json");
        lock (_gate)
        {
            try
            {
                if (!File.Exists(path)) return false;
                File.Delete(path);
                _cache.Remove(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        Changed?.Invoke(projectId);
        return true;
    }

    // ---- placing threads ----

    private async Task<List<(ReviewThread Before, ReviewThread After)>> PlaceAsync(ReviewScope scope, IReadOnlyList<ReviewThread> threads, CancellationToken cancellationToken)
    {
        var texts = new Dictionary<(string Path, string Side), string[]?>();
        var placed = new List<(ReviewThread, ReviewThread)>(threads.Count);
        foreach (var thread in threads)
        {
            if (thread.State == ReviewThreadStates.Resolved)
            {
                // A resolved thread is not followed any more: it is history.
                placed.Add((thread, thread));
                continue;
            }

            var lines = await LinesAsync(scope, thread.Target, thread.Side, thread.Path, texts, cancellationToken).ConfigureAwait(false);
            placed.Add((thread, ReviewAnchoring.Relocate(thread, lines)));
        }

        return placed;
    }

    private async Task<string[]?> LinesAsync(ReviewScope scope, string target, string side, string path, Dictionary<(string Path, string Side), string[]?> texts, CancellationToken cancellationToken)
    {
        var key = (target + ":" + path, side);
        if (!texts.TryGetValue(key, out var lines))
        {
            var text = await _text.ReadAsync(scope, target, side, path, cancellationToken).ConfigureAwait(false);
            texts[key] = lines = text is null ? null : ReviewAnchoring.SplitLines(text);
        }

        return lines;
    }

    private static bool Matches(ReviewThread thread, string? target, string? path)
        => (target is null || thread.Target == target) && (path is null || thread.Path == path);

    private static bool Matches(ReviewRound round, string? target, string? path)
        => (target is null || round.Target == target) && (path is null || round.Path == path);

    private static bool Matches(ReviewRequest request, string? target, string? path)
        => (target is null || request.Target == target) && (path is null || request.Path == path);

    private static string Text(string? value, string what, bool allowEmpty)
    {
        var text = (value ?? string.Empty).ReplaceLineEndings("\n");
        if (text.Length > MaximumText) throw new ReviewException("limit", $"The {what} is longer than {MaximumText} characters.");
        if (text.Contains('\0')) throw new ReviewException("invalid", $"The {what} holds a character that is not text.");
        if (!allowEmpty && string.IsNullOrWhiteSpace(text)) throw new ReviewException("invalid", $"The {what} is empty.");
        return text;
    }

    // ---- the file of a checkout ----

    private string Store(ReviewScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var folder = ProjectFolder(scope.Project.Id) ?? throw new ReviewException("invalidCheckout", "The project has no valid id.");
        if (scope.Checkout is { } checkout)
        {
            if (!Path.IsPathFullyQualified(checkout) || checkout.Length > 4096 || checkout.Any(char.IsControl))
                throw new ReviewException("invalidCheckout", "A checkout is the full path of a worktree of the project.");
            if (!SameFolder(checkout, scope.Project.ProjectPath) && !GitWorktreeService.SameRepository(checkout, scope.Project.ProjectPath))
                throw new ReviewException("invalidCheckout", "The folder is not a checkout of the project's repository.");
            if (SameFolder(checkout, scope.Project.ProjectPath)) return Path.Combine(folder, ProjectCheckoutKey + ".json");
        }

        return Path.Combine(folder, CheckoutKey(scope.Checkout) + ".json");
    }

    private string? ProjectFolder(string? projectId)
        => projectId is { Length: > 0 and <= 128 } && projectId.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') && projectId[0] != '.'
            ? Path.Combine(_root, projectId)
            : null;

    private static string CheckoutKey(string? checkout)
    {
        if (checkout is null) return ProjectCheckoutKey;
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkout));
        if (!OperatingSystem.IsLinux()) normalized = normalized.ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "worktree-" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    private static bool SameFolder(string first, string second)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
                OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private (ReviewFile Data, string? ReadOnly) Load(string path)
    {
        lock (_gate)
        {
            var cached = Read(path);
            return (cached.Data.Clone(), cached.ReadOnly);
        }
    }

    private ReviewThread Change(ReviewScope scope, string path, string id, Func<ReviewThread, ReviewThread> change)
    {
        ReviewThread? changed = null;
        Update(scope, path, data =>
        {
            var index = data.Threads.FindIndex(thread => thread.Id == id);
            if (index < 0) throw new ReviewException("notFound", $"There is no comment `{id}` in this checkout.");
            changed = change(data.Threads[index]);
            if (changed == data.Threads[index]) return false;
            data.Threads[index] = changed;
            return true;
        });
        return changed!;
    }

    // Changes the file of a checkout under the gate: the change works on a copy, which is written only when the
    // change says it changed something and the write succeeded.
    private void Update(ReviewScope scope, string path, Func<ReviewFile, bool> change, bool notify = true)
    {
        lock (_gate)
        {
            var cached = Read(path);
            if (cached.ReadOnly is { } reason) throw new ReviewException("readOnly", reason);
            var copy = cached.Data.Clone();
            copy.ProjectId = scope.Project.Id;
            copy.Checkout = scope.Checkout is null ? scope.Project.ProjectPath : Path.GetFullPath(scope.Checkout);
            if (!change(copy)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = File.Create(staged))
                {
                    JsonSerializer.Serialize(stream, copy, ReviewJsonContext.Default.ReviewFile);
                }

                File.Move(staged, path, overwrite: true);
                _cache[path] = new(Stamp(path), copy, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ReviewException("writeFailed", "The review could not be saved: " + exception.Message);
            }
        }

        if (notify) Changed?.Invoke(scope.Project.Id);
    }

    private Cached Read(string path)
    {
        var stamp = Stamp(path);
        if (_cache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
        {
            return cached;
        }

        Cached read;
        if (stamp is null)
        {
            read = new(null, new ReviewFile(), null);
        }
        else
        {
            try
            {
                using var stream = File.OpenRead(path);
                var data = JsonSerializer.Deserialize(stream, ReviewJsonContext.Default.ReviewFile) ?? new ReviewFile();
                read = data.Version > FileVersion
                    ? new(stamp, data, "These reviews were saved by a newer version of CodeAlta; they are read-only here.")
                    : new(stamp, data.Normalize(), null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                read = new(stamp, new ReviewFile(), "The file of these reviews cannot be read; it is left as it is: " + path);
            }
        }

        if (_cache.Count >= 64) _cache.Clear();
        _cache[path] = read;
        return read;
    }

    private static (long Length, long Ticks)? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record Cached((long Length, long Ticks)? Stamp, ReviewFile Data, string? ReadOnly);
}

/// <summary>The file of the reviews of one checkout.</summary>
internal sealed class ReviewFile
{
    public int Version { get; set; } = ReviewService.FileVersion;

    public string? ProjectId { get; set; }

    public string? Checkout { get; set; }

    public int NextThread { get; set; } = 1;

    public int NextRound { get; set; } = 1;

    public List<ReviewThread> Threads { get; set; } = [];

    public List<ReviewRound> Rounds { get; set; } = [];

    public Dictionary<string, string> Viewed { get; set; } = new(StringComparer.Ordinal);

    public List<ReviewRequest> Requests { get; set; } = [];

    internal ReviewFile Clone() => new()
    {
        Version = Version,
        ProjectId = ProjectId,
        Checkout = Checkout,
        NextThread = NextThread,
        NextRound = NextRound,
        Threads = [.. Threads],
        Rounds = [.. Rounds],
        Viewed = new(Viewed, StringComparer.Ordinal),
        Requests = [.. Requests],
    };

    // What a hand edit may have left: missing lists, ids below the ones in use, invalid entries.
    internal ReviewFile Normalize()
    {
        Threads = [.. (Threads ?? []).Where(static thread => thread is not null && thread.Id.Length > 1 && ReviewTargets.IsValid(thread.Target) && ReviewService.IsValidPath(thread.Path))
            .Select(static thread => thread with { Replies = thread.Replies ?? [] })];
        Rounds = [.. (Rounds ?? []).Where(static round => round is not null && ReviewTargets.IsValid(round.Target)).Select(static round => round with { Threads = round.Threads ?? [] })];
        Requests = [.. (Requests ?? []).Where(static request => request is not null && request.Id.Length > 0)];
        Viewed = new(Viewed ?? [], StringComparer.Ordinal);
        NextThread = Math.Max(NextThread, Threads.Select(static thread => Number(thread.Id)).DefaultIfEmpty(0).Max() + 1);
        NextRound = Math.Max(NextRound, Rounds.Select(static round => Number(round.Id)).DefaultIfEmpty(0).Max() + 1);
        Version = ReviewService.FileVersion;
        return this;
    }

    private static int Number(string id) => id.Length > 1 && int.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true)]
[JsonSerializable(typeof(ReviewFile))]
internal sealed partial class ReviewJsonContext : JsonSerializerContext;
