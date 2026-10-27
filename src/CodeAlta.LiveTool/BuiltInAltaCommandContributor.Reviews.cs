using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Reviews;
using CodeAlta.Catalog.Worktrees;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The `alta review` commands: an agent asks the user to review a file or the changes, reads the comments of the
// user, and answers them.
internal sealed partial class BuiltInAltaCommandContributor
{
    private const string ReviewNextStep = "Do not call another tool or poll. End your turn now with one short sentence saying that the review is with the user; the next user prompt contains the review.";

    private static readonly AltaCommandPolicy[] ReviewPolicies =
    [
        Mutating("review request"),
        Read("review status"),
        Read("review list"),
        Mutating("review reply"),
    ];

    private static Command CreateReviewCommand(AltaCommandContext context)
    {
        var group = Group("review", "Have the user review a file or the changes, and answer their comments.");
        group.Add(CreateReviewRequestCommand(context));
        group.Add(CreateReviewStatusCommand(context));
        group.Add(CreateReviewListCommand(context));
        group.Add(CreateReviewReplyCommand(context));
        AddHelpText(
            group,
            "The user annotates what you produced: a plan or any file (`file <path>`), the uncommitted changes of your checkout (`changes`), or what your branch changed since its base (`branch`). Their comments are on lines or ranges of lines, may propose replacement text, and come to you as one prompt with a verdict: approved, changes requested, or comments.",
            "Each comment has an id (`c12`). Address every comment of a review, then answer them all with one `alta review reply --stdin`. Only the user resolves a comment.",
            "When the user reviews changes before commit, never commit by yourself: run `alta review request changes` when the work is done and verified, end your turn, and commit only what a review approved. `alta review status` says whether that applies to this session.",
            "`alta ask` only asks questions; a review request can carry questions too, so one request collects the review and the answers.",
            "Examples: `alta review request file .alta/plans/2026-10-20-x.md --stdin`; `alta review request changes`; `alta review status`; `alta review list`; `alta review reply --stdin`.");
        return group;
    }

    private static Command CreateReviewRequestCommand(AltaCommandContext context)
    {
        string? target = null;
        string? path = null;
        string? title = null;
        string? project = null;
        var useStdin = false;
        var command = Leaf("request", "Open a review for the user and return at once; the review comes back as your next prompt.");
        command.Add("<target>", "`file`, `changes` or `branch`.", value => target = value);
        command.Add("<path>?", "For `file`: the file to review, relative to your checkout.", value => path = value);
        command.Add("title=", "A short title for the review.", value => title = value);
        command.Add("stdin", "Read a JSON payload from stdin: {\"message\": \"...\", \"questions\": [...]} with the questions in the schema of `alta ask`.", value => useStdin = value is not null);
        command.Add("project=", "Project id, slug or path. Defaults to the project of the calling session.", value => project = value);
        command.Add(async (_, _) => await HandleReviewRequestAsync(context, target, path, title, useStdin, project).ConfigureAwait(false));
        AddHelpText(
            command,
            "`file <path>` shows the file to the user: a Markdown file (a plan) rendered, with comments on its blocks; any other text file with comments on its lines. `changes` opens the uncommitted changes of your checkout; `branch` what your branch changed since it left its base branch.",
            "Questions are optional: give them for the decisions the user must take, such as the open points of a plan. The answers come in the same prompt as the review.",
            "One request per turn, whatever started the turn: a prompt of the user, the result of a child session or of a background job. After `alta.review.queued`, end your turn; do not poll.",
            "Examples: `alta review request file .alta/plans/2026-10-20-x.md --stdin` with {\"questions\":[{\"title\":\"Plan review\",\"question\":\"Does this plan match your intent?\",\"choices\":[{\"title\":\"Approve\"},{\"title\":\"Needs changes\"}]}]}; `alta review request changes --title \"Price cache\"`.");
        return command;
    }

    private static Command CreateReviewStatusCommand(AltaCommandContext context)
    {
        string? project = null;
        var command = Leaf("status", "Say whether the user reviews the changes of this session before commit, and what waits.");
        command.Add("project=", "Project id, slug or path. Defaults to the project of the calling session.", value => project = value);
        command.Add(async (_, _) => await HandleReviewStatusAsync(context, project).ConfigureAwait(false));
        AddHelpText(
            command,
            "`beforeCommit` is true when the user reviews the changes before they are committed: then do not commit before a review approved them. `approvedSnapshotMatches` says whether the work tree is still the one the last review approved: only then may it be committed; otherwise `changedSinceApproval` lists the files that changed since.",
            "Example: `alta review status`.");
        return command;
    }

    private static Command CreateReviewListCommand(AltaCommandContext context)
    {
        string? project = null;
        string? file = null;
        string? target = null;
        var all = false;
        var command = Leaf("list", "List the comments the user sent on your checkout, placed on the lines as they are now.");
        command.Add("target=", "`file`, `changes` or `branch`. Defaults to every target.", value => target = value);
        command.Add("file=", "Only the comments on this file.", value => file = value);
        command.Add("all", "Also the resolved comments.", value => all = value is not null);
        command.Add("project=", "Project id, slug or path. Defaults to the project of the calling session.", value => project = value);
        command.Add(async (_, _) => await HandleReviewListAsync(context, project, target, file, all).ConfigureAwait(false));
        AddHelpText(
            command,
            "The comments the user has not submitted yet are not listed: they are drafts.",
            "Example: `alta review list --target changes`.");
        return command;
    }

    private static Command CreateReviewReplyCommand(AltaCommandContext context)
    {
        string? project = null;
        var useStdin = false;
        var command = Leaf("reply", "Answer comments of the user, all in one call.");
        command.Add("stdin", "Read the answers from stdin: [{\"id\": \"c12\", \"reply\": \"What you changed.\"}]. Required.", value => useStdin = value is not null);
        command.Add("project=", "Project id, slug or path. Defaults to the project of the calling session.", value => project = value);
        command.Add(async (_, _) => await HandleReviewReplyAsync(context, project, useStdin).ConfigureAwait(false));
        AddHelpText(
            command,
            "Each answer says what you changed for the comment, or why you did not. An answer marks the comment addressed; add \"addressed\": false to answer a question without claiming a change. Only the user resolves a comment.",
            "Example: `alta review reply --stdin` with [{\"id\":\"c12\",\"reply\":\"Logs the error and rethrows.\"},{\"id\":\"c13\",\"reply\":\"It runs once per request.\",\"addressed\":false}].");
        return command;
    }

    private static bool TryGetReviews(AltaCommandContext context, out ReviewService reviews)
    {
        if (context.Services.Get<ReviewService>() is { } found)
        {
            reviews = found;
            return true;
        }

        reviews = null!;
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
            "Required in-process service 'ReviewService' is unavailable.");
        return false;
    }

    // The checkout a command works on: the worktree of the calling session, else the project folder.
    private static async Task<(ReviewScope? Scope, int ExitCode)> ResolveReviewScopeAsync(AltaCommandContext context, string? projectRef)
    {
        var (project, exitCode) = await ResolveWorkItemProjectAsync(context, projectRef).ConfigureAwait(false);
        if (project is null)
        {
            return (null, exitCode);
        }

        // The root of the worktree the caller works in, also from a folder below it: the commit guard and the window
        // name the same checkout.
        var checkout = NormalizeOptionalText(projectRef) is null ? CallerWorktree(context, project) : null;
        return (ReviewService.ScopeOf(project, checkout), AltaExitCodes.Success);
    }

    private static int ReviewError(AltaCommandContext context, ReviewException exception, string command)
        => exception.Code switch
        {
            "notFound" => NotFound(context, "review.notFound", exception.Message),
            "readOnly" or "writeFailed" => ReviewFailure(context, "review." + exception.Code, exception.Message),
            _ => UsageError(context, "review." + exception.Code, exception.Message, command),
        };

    private static int ReviewFailure(AltaCommandContext context, string code, string message)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, AltaExitCodes.Failure, message);
        return AltaExitCodes.Failure;
    }

    private static async ValueTask<int> HandleReviewRequestAsync(AltaCommandContext context, string? target, string? path, string? title, bool useStdin, string? projectRef)
    {
        const string Command = "alta review request";
        var sessionId = NormalizeOptionalText(context.Caller.SourceSessionId);
        if (sessionId is null || !string.Equals(context.Caller.Kind, "agent", StringComparison.OrdinalIgnoreCase))
        {
            return UsageError(context, "usage.missingSession", "Only an agent session requests a review: the review comes back to it.", Command);
        }

        context.ClaimSessionWork(sessionId);
        if (!TryGetReviews(context, out var reviews) || !TryGetAskService(context, out var asks))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var kind = NormalizeOptionalText(target)?.ToLowerInvariant();
        if (kind is not (ReviewTargets.File or ReviewTargets.Changes or ReviewTargets.Branch))
        {
            return UsageError(context, "usage.invalidTarget", "The target is `file <path>`, `changes` or `branch`.", Command);
        }

        var (scope, exitCode) = await ResolveReviewScopeAsync(context, projectRef).ConfigureAwait(false);
        if (scope is null)
        {
            return exitCode;
        }

        string? file = null;
        if (kind == ReviewTargets.File)
        {
            if (NormalizeOptionalText(path) is not { } given)
            {
                return UsageError(context, "usage.missingFile", "A file review names the file: `alta review request file <path>`.", Command);
            }

            file = ReviewFilePath(scope.Folder, given);
            if (file is null)
            {
                return UsageError(context, "usage.invalidFile", "The file must be inside your checkout.", Command);
            }

            if (!File.Exists(Path.Combine(scope.Folder, file.Replace('/', Path.DirectorySeparatorChar))))
            {
                return NotFound(context, "file.notFound", $"File '{file}' was not found in your checkout.");
            }
        }
        else if (NormalizeOptionalText(path) is not null)
        {
            return UsageError(context, "usage.unexpectedPath", $"A `{kind}` review names no file.", Command);
        }
        else if (context.Services.Get<IAltaChangesView>() is null)
        {
            // Only a host with a view of the changes (the desktop window) shows a diff to review.
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "review.unavailable", AltaExitCodes.ServiceUnavailable,
                "This CodeAlta has no view of the changes to review: leave the changes uncommitted and report them instead.");
            return AltaExitCodes.ServiceUnavailable;
        }
        else if (kind == ReviewTargets.Changes && !await HasUncommittedChangesAsync(scope, context.CancellationToken).ConfigureAwait(false))
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "review.nothingToReview", AltaExitCodes.Failure,
                "Your checkout has no uncommitted change to review.");
            return AltaExitCodes.Failure;
        }

        ReviewRequestPayload? payload = null;
        if (useStdin)
        {
            var stdin = await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stdin))
            {
                try
                {
                    payload = JsonSerializer.Deserialize(stdin, ReviewJsonSerializerContext.Default.ReviewRequestPayload);
                }
                catch (JsonException exception)
                {
                    return UsageError(context, "usage.invalidJson", $"The payload is not valid JSON: {exception.Message}", Command);
                }
            }
        }

        AltaAskRequest request;
        try
        {
            request = AltaAskValidator.ValidateAndNormalize(new AltaAskRequest
            {
                Review = new AltaReviewTarget
                {
                    Kind = kind,
                    Path = file,
                    Title = title,
                    Message = payload?.Message,
                    ProjectId = scope.Project.Id,
                    Checkout = scope.Checkout,
                },
                Questions = payload?.Questions ?? [],
            });
        }
        catch (ArgumentException exception)
        {
            return UsageError(context, "usage.invalidPayload", exception.Message.Split(" (Parameter", 2)[0], Command);
        }

        AltaAskQueueResult queued;
        try
        {
            queued = await context.ObserveOriginalAsync(asks.QueueAsync(request, sessionId, context.Caller, context.CancellationToken)).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "review.cannotRequest", AltaExitCodes.Failure, exception.Message);
            return AltaExitCodes.Failure;
        }

        try
        {
            reviews.AddRequest(scope, new ReviewRequest
            {
                Id = queued.AskId,
                SessionId = sessionId,
                Target = kind,
                Path = file,
                Title = request.Review!.Title,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        catch (ReviewException exception)
        {
            // The review is with the user all the same: the request is only a mark in the store.
            AltaJsonlWriter.WriteWarning(context.Stderr, context.CorrelationId, "review.notRecorded", exception.Message);
        }

        var counts = reviews.Counts(scope);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.review.queued",
            version = 1,
            correlationId = context.CorrelationId,
            requestId = queued.AskId,
            sessionId = queued.SessionId,
            target = kind,
            file,
            checkout = scope.Checkout,
            openThreads = counts.Open + counts.Addressed,
            queued = true,
            shouldYield = true,
            recommendedAction = "stop",
            activeWaitAllowed = false,
            shouldPoll = false,
            nextStep = ReviewNextStep,
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleReviewStatusAsync(AltaCommandContext context, string? projectRef)
    {
        if (!TryGetReviews(context, out var reviews))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var (scope, exitCode) = await ResolveReviewScopeAsync(context, projectRef).ConfigureAwait(false);
        if (scope is null)
        {
            return exitCode;
        }

        var sessionId = NormalizeOptionalText(context.Caller.SourceSessionId);
        var policy = sessionId is not null ? context.Services.Get<IAltaReviewPolicy>()?.GetBeforeCommit(sessionId) : null;
        ReviewState state;
        try
        {
            state = await reviews.ListAsync(scope, null, null, includeResolved: false, context.CancellationToken).ConfigureAwait(false);
        }
        catch (ReviewException exception)
        {
            return ReviewError(context, exception, "alta review status");
        }

        var last = reviews.LastRound(scope, null);
        var lastDiff = state.Rounds.LastOrDefault(static round => ReviewTargets.IsDiff(round.Target));
        var approval = lastDiff is { Verdict: ReviewVerdicts.Approved } && (policy?.On ?? false)
            ? await reviews.GetApprovalAsync(scope, context.CancellationToken).ConfigureAwait(false)
            : null;
        bool? matches = lastDiff is { Verdict: ReviewVerdicts.Approved } && (policy?.On ?? false)
            ? approval is { Current: true }
            : lastDiff is { Verdict: ReviewVerdicts.Approved } ? null : false;
        var changedSince = approval is { Current: false }
            ? await reviews.ChangedSinceApprovalAsync(scope, approval, context.CancellationToken).ConfigureAwait(false)
            : null;
        var request = state.Request;
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.review.status",
            version = 1,
            correlationId = context.CorrelationId,
            projectId = scope.Project.Id,
            checkout = scope.Checkout,
            beforeCommit = policy?.On,
            source = policy?.Source,
            pendingRequest = request is null ? null : new { id = request.Id, target = request.Target, file = request.Path, sessionId = request.SessionId },
            drafts = state.Threads.Count(static thread => thread.State == ReviewThreadStates.Draft),
            openThreads = state.Threads.Count(static thread => thread.State == ReviewThreadStates.Open),
            addressedThreads = state.Threads.Count(static thread => thread.State == ReviewThreadStates.Addressed),
            lastVerdict = last?.Verdict,
            lastTarget = last?.Target,
            lastFile = last?.Path,
            approvedSnapshotMatches = matches,
            changedSinceApproval = changedSince?.Take(50).ToArray(),
            nextStep = policy?.On == true
                ? matches == true
                    ? "The work tree is the one the user approved: commit it, and only it."
                    : "Do not commit: when the work is done and verified, run `alta review request changes` and end your turn."
                : null,
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleReviewListAsync(AltaCommandContext context, string? projectRef, string? target, string? file, bool all)
    {
        const string Command = "alta review list";
        if (!TryGetReviews(context, out var reviews))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var kind = NormalizeOptionalText(target)?.ToLowerInvariant();
        if (kind is not null && !ReviewTargets.IsValid(kind))
        {
            return UsageError(context, "usage.invalidTarget", "--target is `file`, `changes` or `branch`.", Command);
        }

        var (scope, exitCode) = await ResolveReviewScopeAsync(context, projectRef).ConfigureAwait(false);
        if (scope is null)
        {
            return exitCode;
        }

        var path = NormalizeOptionalText(file) is { } given ? ReviewFilePath(scope.Folder, given) ?? given.Replace('\\', '/') : null;
        ReviewState state;
        try
        {
            state = await reviews.ListAsync(scope, kind, path, all, context.CancellationToken).ConfigureAwait(false);
        }
        catch (ReviewException exception)
        {
            return ReviewError(context, exception, Command);
        }

        var count = 0;
        foreach (var thread in state.Threads.Where(static thread => thread.State != ReviewThreadStates.Draft)
            .OrderBy(static thread => thread.Path, StringComparer.Ordinal).ThenBy(static thread => thread.StartLine))
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.review.thread",
                version = 1,
                correlationId = context.CorrelationId,
                id = thread.Id,
                target = thread.Target,
                file = thread.Path,
                side = thread.Side,
                startLine = thread.WholeFile ? (int?)null : thread.StartLine,
                endLine = thread.WholeFile ? (int?)null : thread.EndLine,
                wholeFile = thread.WholeFile,
                state = thread.State,
                outdated = thread.Outdated,
                lines = thread.WholeFile ? null : thread.Anchor,
                body = thread.Body,
                suggestion = thread.Suggestion,
                replies = thread.Replies.Select(static reply => new { author = reply.Author, body = reply.Body, at = reply.At }).ToArray(),
                round = thread.Round,
            });
            count++;
        }

        var last = state.Rounds.LastOrDefault();
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.review.summary",
            version = 1,
            correlationId = context.CorrelationId,
            count,
            open = state.Threads.Count(static thread => thread.State == ReviewThreadStates.Open),
            addressed = state.Threads.Count(static thread => thread.State == ReviewThreadStates.Addressed),
            lastVerdict = last?.Verdict,
            readOnly = state.ReadOnly,
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleReviewReplyAsync(AltaCommandContext context, string? projectRef, bool useStdin)
    {
        const string Command = "alta review reply";
        if (!useStdin)
        {
            return UsageError(context, "usage.missingStdin", "Give the answers on stdin: [{\"id\": \"c12\", \"reply\": \"...\"}].", Command);
        }

        var sessionId = NormalizeOptionalText(context.Caller.SourceSessionId);
        if (sessionId is null)
        {
            return UsageError(context, "usage.missingSession", "Only an agent session answers the comments of a review.", Command);
        }

        if (!TryGetReviews(context, out var reviews))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var stdin = await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
        List<ReviewReplyPayload>? payload;
        try
        {
            payload = JsonSerializer.Deserialize(stdin, ReviewJsonSerializerContext.Default.ListReviewReplyPayload);
        }
        catch (JsonException exception)
        {
            return UsageError(context, "usage.invalidJson", $"The answers are not valid JSON: {exception.Message}", Command);
        }

        if (payload is not { Count: > 0 } || payload.Any(static reply => reply is null || string.IsNullOrWhiteSpace(reply.Id) || string.IsNullOrWhiteSpace(reply.Reply)))
        {
            return UsageError(context, "usage.invalidPayload", "Give at least one answer, each with an `id` and a `reply`.", Command);
        }

        if (payload.Count > 200)
        {
            return UsageError(context, "usage.invalidPayload", "At most 200 answers in one call.", Command);
        }

        var (scope, exitCode) = await ResolveReviewScopeAsync(context, projectRef).ConfigureAwait(false);
        if (scope is null)
        {
            return exitCode;
        }

        IReadOnlyList<ReviewThread> answered;
        try
        {
            answered = reviews.ReplyAsAgent(scope, sessionId, [.. payload.Select(static reply => new ReviewReplyDraft(reply.Id!.Trim(), reply.Reply!, reply.Addressed ?? true))]);
        }
        catch (ReviewException exception)
        {
            return ReviewError(context, exception, Command);
        }

        foreach (var thread in answered)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.review.replied",
                version = 1,
                correlationId = context.CorrelationId,
                id = thread.Id,
                state = thread.State,
            });
        }

        WriteSummary(context, "alta.review.summary", answered.Count, truncated: false);
        return AltaExitCodes.Success;
    }

    // A file of a checkout, as a review names it: relative to the checkout with `/`. A full path inside the
    // checkout is made relative; anything outside it is refused.
    private static string? ReviewFilePath(string folder, string given)
    {
        try
        {
            var path = given.Trim();
            if (Path.IsPathFullyQualified(path))
            {
                var relative = Path.GetRelativePath(Path.GetFullPath(folder), Path.GetFullPath(path));
                if (relative == "." || Path.IsPathRooted(relative))
                {
                    return null;
                }

                path = relative;
            }

            path = path.Replace('\\', '/');
            while (path.StartsWith("./", StringComparison.Ordinal))
            {
                path = path[2..];
            }

            return ReviewService.IsValidPath(path) ? path : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static async Task<bool> HasUncommittedChangesAsync(ReviewScope scope, CancellationToken cancellationToken)
    {
        var run = await GitWorktreeService.RunGitAsync(scope.Folder, ["status", "--porcelain=v1", "--untracked-files=all"], TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        // When git cannot answer, the user is asked anyway: the window says what it finds.
        return !run.Succeeded || run.Output.Length > 0;
    }
}

/// <summary>What `alta review request` reads on stdin.</summary>
internal sealed record ReviewRequestPayload
{
    public string? Message { get; init; }

    public IReadOnlyList<AltaAskQuestion>? Questions { get; init; }
}

/// <summary>One answer of `alta review reply`.</summary>
internal sealed record ReviewReplyPayload
{
    public string? Id { get; init; }

    public string? Reply { get; init; }

    public bool? Addressed { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ReviewRequestPayload))]
[JsonSerializable(typeof(List<ReviewReplyPayload>))]
internal sealed partial class ReviewJsonSerializerContext : JsonSerializerContext;
