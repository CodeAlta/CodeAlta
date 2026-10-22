using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Reviews;
using CodeAlta.Desktop.WorkItems;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The page's side of the reviews: the comments of the user on files and changes of a checkout, their replies,
/// the files viewed, and the submission of a round to a session, and a notice when anything of this changes.
/// </summary>
/// <remarks>
/// A round goes to the session the user picks. When that session waits for this very review (it asked for it with
/// <c>alta review request</c>), the round answers its request; otherwise it is sent as a prompt, queued when the
/// session is busy. A round can also start a new session in the checkout, in Plan mode for a plan.
/// </remarks>
[NeoRpcService("reviews", Version = 1)]
internal sealed class ReviewsService
{
    /// <summary>The most threads of one answer.</summary>
    internal const int MaximumThreads = ReviewService.MaximumThreads;

    /// <summary>The longest folder of a worktree a request may name, in UTF-16 units.</summary>
    internal const int MaximumCheckoutLength = 4096;

    private readonly ReviewService? _reviews;
    private readonly ProjectCatalog? _projects;
    private readonly OwnedSessionAskService? _asks;
    private readonly Func<string, string, CancellationToken, Task<string?>>? _send;
    private readonly ISessionStarter? _starter;
    private readonly string? _epoch;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal ReviewsService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="reviews">The reviews.</param>
    /// <param name="projects">The host's project catalog.</param>
    /// <param name="asks">The pending requests of the sessions, which a round may answer.</param>
    /// <param name="send">Sends a prompt to a session, queued when it is busy; returns why it failed, or null.</param>
    /// <param name="starter">Starts a new session for a round.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    internal ReviewsService(ReviewService reviews, ProjectCatalog projects, OwnedSessionAskService asks, Func<string, string, CancellationToken, Task<string?>> send,
        ISessionStarter starter, string epoch)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(asks);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(starter);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_reviews, _projects, _asks, _send, _starter, _epoch) = (reviews, projects, asks, send, starter, epoch);
    }

    /// <summary>The threads of a target of a checkout, placed on the lines of the files as they are now.</summary>
    [NeoRpcMethod("list", TimeoutMilliseconds = 60_000)]
    public async Task<ReviewsListResponse> ListAsync(ReviewsListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (scope, refused) = await ScopeAsync(request.ExpectedEpoch, request.ProjectId, request.Checkout, cancellationToken).ConfigureAwait(false);
        if (scope is null) return ReviewsListResponse.Refused(refused!);
        if (request.Target is not null && !ReviewTargets.IsValid(request.Target) || request.Path is not null && !ReviewService.IsValidPath(request.Path))
            return ReviewsListResponse.Refused("invalid_request");
        try
        {
            ForgetStaleRequests(scope);
            var state = await _reviews!.ListAsync(scope, request.Target, request.Path, request.IncludeResolved, cancellationToken).ConfigureAwait(false);
            return new("ok",
                [.. state.Threads.Take(MaximumThreads).Select(Thread)],
                [.. state.Rounds.TakeLast(50).Select(Round)],
                [.. state.Viewed.Select(static entry => Viewed(entry.Key, entry.Value)).OfType<ReviewViewedDto>()],
                state.Request is { } pending ? new(pending.Id, pending.SessionId, pending.Target, pending.Path, pending.Title, Time(pending.CreatedAt)) : null,
                state.ReadOnly, null);
        }
        catch (ReviewException exception)
        {
            return ReviewsListResponse.Refused(Status(exception)) with { Message = exception.Message };
        }
    }

    /// <summary>Writes a new comment of the user, as a draft.</summary>
    [NeoRpcMethod("add")]
    public async Task<ReviewsThreadResponse> AddAsync(ReviewsAddRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (scope, refused) = await ScopeAsync(request.ExpectedEpoch, request.ProjectId, request.Checkout, cancellationToken).ConfigureAwait(false);
        if (scope is null) return new(refused!, null, null);
        return await ThreadAsync(async () => await _reviews!.AddThreadAsync(scope,
            new(request.Target, request.Path, request.Side, request.StartLine, request.EndLine, request.Body ?? string.Empty, request.Suggestion), cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <summary>Changes the text or the suggestion of a comment.</summary>
    [NeoRpcMethod("update")]
    public async Task<ReviewsThreadResponse> UpdateAsync(ReviewsUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (scope, refused) = await ScopeAsync(request.ExpectedEpoch, request.ProjectId, request.Checkout, cancellationToken).ConfigureAwait(false);
        if (scope is null) return new(refused!, null, null);
        return await ThreadAsync(() => Task.FromResult(_reviews!.UpdateThread(scope, request.Id, request.Body, request.Suggestion, request.ClearSuggestion))).ConfigureAwait(false);
    }

    /// <summary>Deletes a comment and its replies.</summary>
    [NeoRpcMethod("delete")]
    public async Task<ReviewsStatusResponse> DeleteAsync(ReviewsIdRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (scope, refused) = await ScopeAsync(request.ExpectedEpoch, request.ProjectId, request.Checkout, cancellationToken).ConfigureAwait(false);
        if (scope is null) return new(refused!, null);
        return Act(() => _reviews!.DeleteThread(scope, request.Id) ? "ok" : "not_found");
    }

    /// <summary>Resolves a comment, or opens it again.</summary>
    [NeoRpcMethod("resolve")]
    public async Task<ReviewsThreadResponse> ResolveAsync(ReviewsResolveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (scope, refused) = await ScopeAsync(request.ExpectedEpoch, request.ProjectId, request.Checkout, cancellationToken).ConfigureAwait(false);
        if (scope is null) return new(refused!, null, null);
        return await ThreadAsync(() => Task.FromResult(_reviews!.Resolve(scope, request.Id, request.Resolved))).ConfigureAwait(false);
    }

    /// <summary>Resolves every comment of a target the agent addressed.</summary>
    [NeoRpcMethod("resolveAddressed")]
    public async Task<ReviewsStatusResponse> ResolveAddressedAsync(ReviewsTargetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (scope, refused) = await ScopeAsync(request.ExpectedEpoch, request.ProjectId, request.Checkout, cancellationToken).ConfigureAwait(false);
        if (scope is null) return new(refused!, null);
        if (!ReviewTargets.IsValid(request.Target)) return new("invalid_request", null);
        return Act(() =>
        {
            _reviews!.ResolveAddressed(scope, request.Target, request.Target == ReviewTargets.File ? request.Path : null);
            return "ok";
        });
    }

    /// <summary>
    /// Deletes a review: every comment of a target (of one file for a file target), its rounds and what was
    /// viewed. An agent that waits for it is told the review was cancelled.
    /// </summary>
    [NeoRpcMethod("deleteReview")]
    public async Task<ReviewsStatusResponse> DeleteReviewAsync(ReviewsTargetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (scope, refused) = await ScopeAsync(request.ExpectedEpoch, request.ProjectId, request.Checkout, cancellationToken).ConfigureAwait(false);
        if (scope is null) return new(refused!, null);
        if (!ReviewTargets.IsValid(request.Target) || request.Target == ReviewTargets.File && !ReviewService.IsValidPath(request.Path)) return new("invalid_request", null);
        try
        {
            var deleted = _reviews!.DeleteReview(scope, request.Target, request.Target == ReviewTargets.File ? request.Path : null);
            await CancelAsync(_asks!, deleted.Requests).ConfigureAwait(false);
            return new(deleted.Deleted ? "ok" : "not_found", null);
        }
        catch (ReviewException exception)
        {
            return new(Status(exception), exception.Message);
        }
    }

    /// <summary>Deletes the review of a plan the work items deleted, and cancels the asks that waited for it.</summary>
    /// <param name="reviews">The reviews.</param>
    /// <param name="asks">The pending requests of the sessions.</param>
    /// <param name="project">The project.</param>
    /// <param name="folder">The checkout the plan was in.</param>
    /// <param name="path">The plan, relative to the checkout.</param>
    internal static void ForgetPlan(ReviewService reviews, OwnedSessionAskService asks, ProjectDescriptor project, string folder, string path)
    {
        var deleted = reviews.ForgetFile(project, folder, path);
        if (deleted.Requests.Count > 0) _ = CancelAsync(asks, deleted.Requests);
    }

    // The agents that wait for a review that is gone are not left waiting: their asks are cancelled.
    private static async Task CancelAsync(OwnedSessionAskService asks, IReadOnlyList<ReviewRequest> requests)
    {
        foreach (var request in requests)
        {
            try
            {
                if (asks.List(request.SessionId).Head is { } head && head.Handle.AskId == request.Id)
                    await asks.CancelAsync(new OwnedAskAction(Guid.NewGuid(), head.Handle, []), CancellationToken.None).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                // An identity the asks do not know: nothing waits.
            }
        }
    }

    // The requests whose ask no longer waits (answered in the session, cancelled, ended with a restart of the host)
    // are forgotten: the review does not say it is requested any more.
    private void ForgetStaleRequests(ReviewScope scope)
    {
        try
        {
            _reviews!.RemoveRequests(scope, request => !_asks!.IsPending(request.SessionId, request.Id));
        }
        catch (ReviewException)
        {
            // A checkout that is not one of the project any more keeps what it has.
        }
    }

    /// <summary>Adds a reply of the user under a comment.</summary>
    [NeoRpcMethod("reply")]
    public async Task<ReviewsThreadResponse> ReplyAsync(ReviewsReplyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (scope, refused) = await ScopeAsync(request.ExpectedEpoch, request.ProjectId, request.Checkout, cancellationToken).ConfigureAwait(false);
        if (scope is null) return new(refused!, null, null);
        return await ThreadAsync(() => Task.FromResult(_reviews!.ReplyAsUser(scope, request.Id, request.Body ?? string.Empty))).ConfigureAwait(false);
    }

    /// <summary>Marks a file viewed at a revision, or clears the mark.</summary>
    [NeoRpcMethod("viewed")]
    public async Task<ReviewsStatusResponse> ViewedAsync(ReviewsViewedRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (scope, refused) = await ScopeAsync(request.ExpectedEpoch, request.ProjectId, request.Checkout, cancellationToken).ConfigureAwait(false);
        if (scope is null) return new(refused!, null);
        return Act(() =>
        {
            _reviews!.SetViewed(scope, request.Target, request.Path, request.Revision);
            return "ok";
        });
    }

    /// <summary>
    /// Submits a round: every draft of the target and every comment still open, with a verdict, to a session. A
    /// session that waits for this review gets it as the answer of its request; another one as a prompt, queued
    /// when it is busy. <see cref="ReviewsSubmitRequest.NewSession"/> starts a session for it instead.
    /// </summary>
    [NeoRpcMethod("submit", TimeoutMilliseconds = 600_000)]
    public async Task<ReviewsSubmitResponse> SubmitAsync(ReviewsSubmitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (scope, refused) = await ScopeAsync(request.ExpectedEpoch, request.ProjectId, request.Checkout, cancellationToken).ConfigureAwait(false);
        if (scope is null) return new(refused!, null, null, null);
        if (!ReviewTargets.IsValid(request.Target) || !ReviewVerdicts.IsValid(request.Verdict)
            || request.Target == ReviewTargets.File && !ReviewService.IsValidPath(request.Path)
            || request.NewSession == (request.SessionId is not null) || request.SessionId is not null && !Identifier(request.SessionId))
            return new("invalid_request", null, null, null);
        var path = request.Target == ReviewTargets.File ? request.Path : null;
        try
        {
            if (request.SessionId is { } sessionId)
            {
                // The session waits for this review: the round is the answer of its request.
                if (_asks!.List(sessionId).Head is { State: "pending", Request.Review: { } asked } head
                    && asked.Kind == request.Target && asked.Path == path && SameCheckout(asked.Checkout, scope.Checkout) && asked.ProjectId == scope.Project.Id)
                {
                    if (head.Request.Questions.Count > 0) return new("answer_in_session", sessionId, null, "The agent also asked questions: answer them in the session.");
                    var answered = await _reviews!.PrepareRoundAsync(scope, new(request.Target, path, request.Verdict, request.Overall, sessionId, head.Handle.AskId), cancellationToken).ConfigureAwait(false);
                    return await SendRoundAsync(scope, answered, async _ =>
                    {
                        var disposition = await _asks.AnswerAsync(new OwnedAskAction(Guid.NewGuid(), head.Handle, [], answered.Text), cancellationToken).ConfigureAwait(false);
                        return (sessionId, disposition.Status is "admitted" or "indeterminate" ? null : $"The session did not take the review ({disposition.Status}).");
                    }).ConfigureAwait(false);
                }

                var round = await _reviews!.PrepareRoundAsync(scope, new(request.Target, path, request.Verdict, request.Overall, sessionId), cancellationToken).ConfigureAwait(false);
                return await SendRoundAsync(scope, round,
                    async _ => (sessionId, await _send!(sessionId, round.Text, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
            }

            // A new session in the checkout of the review: in Plan mode for a plan.
            var fresh = await _reviews!.PrepareRoundAsync(scope, new(request.Target, path, request.Verdict, request.Overall, "new"), cancellationToken).ConfigureAwait(false);
            var plan = path is not null && path.StartsWith(".alta/plans/", StringComparison.Ordinal);
            var title = path is not null ? "Review of " + Path.GetFileName(path) : request.Target == ReviewTargets.Branch ? "Review of the branch" : "Review of the changes";
            return await SendRoundAsync(scope, fresh, async record =>
            {
                var started = await _starter!.StartAsync(scope.Project, title, _ => fresh.Text, worktree: false, likeSessionId: request.LikeSessionId, "review",
                    new SessionStartModel { AgentPromptId = plan ? "plan" : null, Worktree = scope.Checkout, Provenance = AgentPromptProvenance.UserReview })
                    .ConfigureAwait(false);
                if (started.SessionId is not { } created || started.Problem is not null)
                    return (started.SessionId, started.Problem ?? "The session could not be started.");
                _reviews.SetRoundSession(scope, record.Round.Id, created);
                return (created, null);
            }).ConfigureAwait(false);
        }
        catch (ReviewException exception)
        {
            return new(Status(exception), null, null, exception.Message);
        }
    }

    // The round is recorded before the session gets it: an agent that answers at once finds its comments open, not
    // drafts. A send that fails undoes it. A send whose outcome is not known keeps it, so the comments are not sent twice.
    private async Task<ReviewsSubmitResponse> SendRoundAsync(ReviewScope scope, ReviewRoundDraft draft,
        Func<ReviewRoundRecord, Task<(string? SessionId, string? Problem)>> send)
    {
        var record = _reviews!.RecordRound(scope, draft);
        (string? SessionId, string? Problem) sent;
        try
        {
            sent = await send(record).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Revert(scope, record);
            throw;
        }

        if (sent.Problem is null) return new("ok", sent.SessionId, record.Round.Id, null);
        Revert(scope, record);
        return new("send_failed", sent.SessionId, null, sent.Problem);
    }

    private void Revert(ReviewScope scope, ReviewRoundRecord record)
    {
        try
        {
            _reviews!.RevertRound(scope, record);
        }
        catch (ReviewException)
        {
            // The store cannot be written: its comments stay as sent, and the user sends them again by a reply.
        }
    }

    /// <summary>What waits in each checkout of a project that has reviews.</summary>
    [NeoRpcMethod("counts")]
    public async Task<ReviewsCountsResponse> CountsAsync(ReviewsCountsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, []);
        if (!Identifier(request.ProjectId)) return new("invalid_request", []);
        if (await ProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is not { } project) return new("not_found", []);
        var rows = new List<ReviewCheckoutCounts>();
        foreach (var (checkout, missing) in _reviews!.ListCheckouts(project.Id))
        {
            var mine = SameCheckout(checkout, project.ProjectPath) ? null : checkout;
            if (missing)
            {
                rows.Add(new(mine, 0, 0, 0, Missing: true));
                continue;
            }

            try
            {
                var counts = _reviews.Counts(new ReviewScope(project, mine));
                rows.Add(new(mine, counts.Drafts, counts.Open, counts.Addressed, Missing: false));
            }
            catch (ReviewException)
            {
                // A checkout that is no longer one of the project is listed by the storage page.
            }
        }

        return new("ok", rows);
    }

    /// <summary>
    /// The reviews of the projects of a space, for the Reviews page: per project, one entry per target of each
    /// checkout, the ones in flight first. Finished ones are included on request.
    /// </summary>
    [NeoRpcMethod("overview", TimeoutMilliseconds = 60_000)]
    public async Task<ReviewsOverviewResponse> OverviewAsync(ReviewsOverviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, []);
        if (request.SpaceId is not null && !Identifier(request.SpaceId)) return new("invalid_request", []);
        IReadOnlyList<ProjectDescriptor> projects;
        try
        {
            projects = await _projects!.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return new("read_failed", []);
        }

        var rows = new List<ReviewsOverviewProject>();
        foreach (var project in projects)
        {
            // The default space holds every project.
            if (project.Archived || request.SpaceId is { } space && space != SpaceDescriptor.DefaultId && !project.Spaces.Contains(space, StringComparer.Ordinal)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (checkout, missing) in _reviews!.ListCheckouts(project.Id))
            {
                if (!missing) ForgetStaleRequests(new ReviewScope(project, SameCheckout(checkout, project.ProjectPath) ? null : checkout));
            }

            var entries = _reviews.Overview(project).Where(entry => request.IncludeFinished || entry.InFlight || entry.Missing || entry.FileMissing).Take(100).ToArray();
            if (entries.Length == 0) continue;
            rows.Add(new(project.Id, string.IsNullOrWhiteSpace(project.DisplayName) ? project.Name : project.DisplayName,
                [.. entries.Select(static entry => new ReviewOverviewDto(entry.Checkout, entry.Missing, entry.Target, entry.Path, entry.Drafts, entry.Open, entry.Addressed,
                    entry.Resolved, entry.Request is { } pending ? new(pending.Id, pending.SessionId, pending.Target, pending.Path, pending.Title, Time(pending.CreatedAt)) : null,
                    entry.LastRound is { } round ? Round(round) : null, entry.UpdatedAt is { } at ? Time(at) : null, entry.FileMissing))]));
        }

        return new("ok", rows);
    }

    /// <summary>Deletes the reviews of a checkout that is gone.</summary>
    [NeoRpcMethod("forgetCheckout")]
    public async Task<ReviewsStatusResponse> ForgetCheckoutAsync(ReviewsScopeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null);
        if (!Identifier(request.ProjectId) || request.Checkout is not { Length: > 0 and <= MaximumCheckoutLength } checkout) return new("invalid_request", null);
        if (await ProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is not { } project) return new("not_found", null);
        return new(_reviews!.DeleteCheckout(project.Id, checkout) ? "ok" : "not_found", null);
    }

    /// <summary>Tells when reviews changed, through the window or an agent: the page then reads again what it shows.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<ReviewsEvent> Watch(ReviewsWatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchAsync(request, cancellationToken), DesktopJsonContext.Default.ReviewsEvent);
    }

    internal async IAsyncEnumerable<ReviewsEvent> WatchAsync(ReviewsWatchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refuse(request.ExpectedEpoch) is not null) yield break;
        // A page that does not read misses nothing: one pending notice says everything that changed.
        var notices = Channel.CreateBounded<int>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        void Notify(string _) => notices.Writer.TryWrite(0);
        _reviews!.Changed += Notify;
        try
        {
            var revision = 0;
            yield return new ReviewsEvent(revision);
            await foreach (var _ in notices.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return new ReviewsEvent(++revision);
        }
        finally
        {
            _reviews.Changed -= Notify;
        }
    }

    private async Task<(ReviewScope? Scope, string? Refused)> ScopeAsync(string? expectedEpoch, string? projectId, string? checkout, CancellationToken cancellationToken)
    {
        if (Refuse(expectedEpoch) is { } refused) return (null, refused);
        if (!Identifier(projectId) || checkout is not null && (checkout.Length is 0 or > MaximumCheckoutLength || checkout.Any(char.IsControl) || !Path.IsPathFullyQualified(checkout)))
            return (null, "invalid_request");
        if (await ProjectAsync(projectId!, cancellationToken).ConfigureAwait(false) is not { } project) return (null, "not_found");
        // The project folder named as a checkout is the project folder.
        return (new ReviewScope(project, checkout is not null && SameCheckout(checkout, project.ProjectPath) ? null : checkout), null);
    }

    private async Task<ProjectDescriptor?> ProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        try
        {
            var project = await _projects!.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false);
            return project is { Archived: false } ? project : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
    }

    private static async Task<ReviewsThreadResponse> ThreadAsync(Func<Task<ReviewThread>> change)
    {
        try
        {
            return new("ok", Thread(await change().ConfigureAwait(false)), null);
        }
        catch (ReviewException exception)
        {
            return new(Status(exception), null, exception.Message);
        }
    }

    private static ReviewsStatusResponse Act(Func<string> change)
    {
        try
        {
            return new(change(), null);
        }
        catch (ReviewException exception)
        {
            return new(Status(exception), exception.Message);
        }
    }

    private static string Status(ReviewException exception) => exception.Code switch
    {
        "notFound" => "not_found",
        "readOnly" => "read_only",
        "writeFailed" => "write_failed",
        "limit" => "limit",
        "nothingToSubmit" => "nothing_to_submit",
        "invalidCheckout" => "invalid_checkout",
        _ => "invalid_request",
    };

    private string? Refuse(string? expectedEpoch)
        => _reviews is null ? "unavailable" : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";

    private static bool Identifier(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl);

    private static bool SameCheckout(string? first, string? second)
    {
        if (first is null || second is null) return first == second;
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

    private static string Time(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static ReviewThreadDto Thread(ReviewThread thread)
        => new(thread.Id, thread.Target, thread.Path, thread.Side, thread.StartLine, thread.EndLine, thread.Anchor, thread.Body, thread.Suggestion, thread.State,
            thread.Outdated, [.. thread.Replies.Select(static reply => new ReviewReplyDto(reply.Author, reply.SessionId, reply.Body, Time(reply.At)))],
            thread.Round, Time(thread.CreatedAt), Time(thread.UpdatedAt));

    private static ReviewRoundDto Round(ReviewRound round)
        => new(round.Id, round.Target, round.Path, round.Verdict, round.Overall, round.SessionId, round.Snapshot, round.Threads, round.RequestId, Time(round.At));

    private static ReviewViewedDto? Viewed(string key, string revision)
    {
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? new(key[..colon], key[(colon + 1)..], revision) : null;
    }
}

/// <param name="Checkout">The folder of a worktree of the project; null for the project folder.</param>
internal sealed record ReviewsScopeRequest(string? ExpectedEpoch, string ProjectId, string? Checkout);

/// <param name="Target"><c>file</c>, <c>changes</c> or <c>branch</c>; null for every target.</param>
/// <param name="Path">For a file target, the file; null for every file.</param>
internal sealed record ReviewsListRequest(string? ExpectedEpoch, string ProjectId, string? Checkout, string? Target, string? Path, bool IncludeResolved);

/// <param name="Status"><c>ok</c>, <c>not_found</c>, <c>invalid_request</c>, <c>invalid_checkout</c>, <c>read_only</c>, <c>unavailable</c> or <c>stale_epoch</c>.</param>
/// <param name="Request">The review an agent asked for and that waits, or null.</param>
/// <param name="ReadOnly">Why the reviews cannot be changed (a newer or broken file), or null.</param>
internal sealed record ReviewsListResponse(string Status, IReadOnlyList<ReviewThreadDto> Threads, IReadOnlyList<ReviewRoundDto> Rounds, IReadOnlyList<ReviewViewedDto> Viewed,
    ReviewRequestDto? Request, string? ReadOnly, string? Message)
{
    internal static ReviewsListResponse Refused(string status) => new(status, [], [], [], null, null, null);
}

/// <param name="Side"><c>old</c> or <c>new</c>.</param>
/// <param name="StartLine">The first line, from 1; 0 for the whole file.</param>
/// <param name="EndLine">The last line, inclusive; 0 for the whole file.</param>
internal sealed record ReviewThreadDto(string Id, string Target, string Path, string Side, int StartLine, int EndLine, string Anchor, string Body, string? Suggestion,
    string State, bool Outdated, IReadOnlyList<ReviewReplyDto> Replies, string? Round, string CreatedAt, string UpdatedAt);

internal sealed record ReviewReplyDto(string Author, string? SessionId, string Body, string At);

internal sealed record ReviewRoundDto(string Id, string Target, string? Path, string Verdict, string? Overall, string? SessionId, string? Snapshot,
    IReadOnlyList<string> Threads, string? RequestId, string At);

internal sealed record ReviewViewedDto(string Target, string Path, string Revision);

internal sealed record ReviewRequestDto(string Id, string SessionId, string Target, string? Path, string? Title, string CreatedAt);

internal sealed record ReviewsAddRequest(string? ExpectedEpoch, string ProjectId, string? Checkout, string Target, string Path, string Side, int StartLine, int EndLine,
    string? Body, string? Suggestion);

internal sealed record ReviewsThreadResponse(string Status, ReviewThreadDto? Thread, string? Message);

internal sealed record ReviewsUpdateRequest(string? ExpectedEpoch, string ProjectId, string? Checkout, string Id, string? Body, string? Suggestion, bool ClearSuggestion);

internal sealed record ReviewsIdRequest(string? ExpectedEpoch, string ProjectId, string? Checkout, string Id);

internal sealed record ReviewsResolveRequest(string? ExpectedEpoch, string ProjectId, string? Checkout, string Id, bool Resolved);

internal sealed record ReviewsReplyRequest(string? ExpectedEpoch, string ProjectId, string? Checkout, string Id, string? Body);

internal sealed record ReviewsTargetRequest(string? ExpectedEpoch, string ProjectId, string? Checkout, string Target, string? Path);

/// <param name="Revision">The revision of the file the user saw; null clears the mark.</param>
internal sealed record ReviewsViewedRequest(string? ExpectedEpoch, string ProjectId, string? Checkout, string Target, string Path, string? Revision);

/// <param name="SessionId">The session the round goes to; null with <paramref name="NewSession"/>.</param>
/// <param name="NewSession">Whether a new session is started for the round.</param>
/// <param name="LikeSessionId">For a new session, the session whose provider and model it takes; null for the default.</param>
internal sealed record ReviewsSubmitRequest(string? ExpectedEpoch, string ProjectId, string? Checkout, string Target, string? Path, string Verdict, string? Overall,
    string? SessionId, bool NewSession, string? LikeSessionId = null);

/// <param name="Status"><c>ok</c>, <c>answer_in_session</c> (the request has questions), <c>send_failed</c>, <c>nothing_to_submit</c>, <c>read_only</c>, <c>invalid_request</c>, <c>not_found</c>, <c>unavailable</c> or <c>stale_epoch</c>.</param>
internal sealed record ReviewsSubmitResponse(string Status, string? SessionId, string? RoundId, string? Message);

internal sealed record ReviewsStatusResponse(string Status, string? Message);

internal sealed record ReviewsCountsRequest(string? ExpectedEpoch, string ProjectId);

/// <param name="Checkout">The worktree; null for the project folder.</param>
/// <param name="Missing">Whether the folder of the checkout is gone: its reviews wait for the user to delete them.</param>
internal sealed record ReviewCheckoutCounts(string? Checkout, int Drafts, int Open, int Addressed, bool Missing);

internal sealed record ReviewsCountsResponse(string Status, IReadOnlyList<ReviewCheckoutCounts> Checkouts);

internal sealed record ReviewsWatchRequest(string? ExpectedEpoch);

/// <param name="SpaceId">The space whose projects are listed; null for every project.</param>
/// <param name="IncludeFinished">Whether reviews with nothing waiting are listed too.</param>
internal sealed record ReviewsOverviewRequest(string? ExpectedEpoch, string? SpaceId, bool IncludeFinished);

internal sealed record ReviewsOverviewResponse(string Status, IReadOnlyList<ReviewsOverviewProject> Projects);

internal sealed record ReviewsOverviewProject(string ProjectId, string Name, IReadOnlyList<ReviewOverviewDto> Entries);

/// <param name="Checkout">The worktree; null for the project folder.</param>
/// <param name="Missing">Whether the folder of the checkout is gone.</param>
/// <param name="FileMissing">Whether the file of a file review is gone from its checkout.</param>
internal sealed record ReviewOverviewDto(string? Checkout, bool Missing, string Target, string? Path, int Drafts, int Open, int Addressed, int Resolved,
    ReviewRequestDto? Request, ReviewRoundDto? LastRound, string? UpdatedAt, bool FileMissing = false);

internal sealed record ReviewsEvent(int Revision);
