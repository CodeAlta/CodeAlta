using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Reviews;
using CodeAlta.Desktop.SessionHosts;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The questions and the reviews agents ask the user: the pending one of a session, and the user's answer. The
/// answer of a review request carries the verdict; the round the agent receives is composed here, from the
/// comments of the review store, and recorded once the session has it.
/// </summary>
[NeoRpcService("sessionAsks", Version = 1)]
internal sealed class SessionAsksService
{
    internal const int MaximumResponseBytes = 192 * 1024;
    private readonly string _epoch;
    // What the owner of the asks of a session says and does: the ask service of this process, or the host of the session.
    private readonly AskOwner _owner;
    private readonly ReviewService? reviews;
    private readonly ProjectCatalog? projects;
    private int _closed;

    internal SessionAsksService(OwnedSessionAskService owner, string epoch, ReviewService? reviews = null, ProjectCatalog? projects = null)
        : this(OwnerOf(owner), epoch, reviews, projects) { }

    /// <summary>Creates the service over the hosts of the sessions: each request goes to the host of its session.</summary>
    internal SessionAsksService(SessionHostRegistry hosts, string epoch, ReviewService? reviews = null, ProjectCatalog? projects = null)
        : this(OwnerOf(hosts), epoch, reviews, projects) { }

    private SessionAsksService(AskOwner owner, string epoch, ReviewService? reviews, ProjectCatalog? projects)
    {
        _epoch = CanonicalEpoch(epoch) ? epoch : throw new ArgumentException("Canonical host epoch required.", nameof(epoch));
        _owner = owner;
        this.reviews = reviews;
        this.projects = projects;
    }

    private sealed record AskOwner(Func<string?, bool> Enabled, Func<string, OwnedAskPage> List, Func<Guid, OwnedAskHandle, OwnedAskDisposition?> Observe,
        Func<OwnedAskAction, CancellationToken, Task<OwnedAskDisposition>> AnswerAsync, Func<OwnedAskAction, CancellationToken, Task<OwnedAskDisposition>> CancelAsync);

    private static AskOwner OwnerOf(OwnedSessionAskService owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new(_ => owner.Enabled, owner.List, owner.Observe, owner.AnswerAsync, owner.CancelAsync);
    }

    private static AskOwner OwnerOf(SessionHostRegistry hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        // A request without a valid session is refused before the owner acts; the local host says whether asks are on.
        return new(sessionId => (sessionId is null ? hosts.Local : hosts.Resolve(sessionId)).AsksEnabled,
            sessionId => hosts.Resolve(sessionId).ListAsks(sessionId),
            (actionId, handle) => hosts.Resolve(handle.SessionId).ObserveAsk(actionId, handle),
            (action, token) => hosts.Resolve(action.Handle.SessionId).AnswerAskAsync(action, token),
            (action, token) => hosts.Resolve(action.Handle.SessionId).CancelAskAsync(action, token));
    }

    internal void CloseAdmission() => Interlocked.Exchange(ref _closed, 1);

    [NeoRpcMethod("list")]
    public Task<SessionAsksPage> ListAsync(SessionAsksRequest request, CancellationToken cancellationToken)
    {
        var error = Check(request?.ExpectedHostEpoch, request?.SessionId);
        var validSession = request is not null && OwnedSessionAskService.IsValidSessionId(request.SessionId);
        if (!validSession) error = "invalid_request";
        if (error is not null) return Task.FromResult(new SessionAsksPage(error, _epoch, validSession ? request!.SessionId : null, null, null, false));
        cancellationToken.ThrowIfCancellationRequested();
        var page = _owner.List(request!.SessionId);
        // One <=8192-unit request (plus a file path of at most 1000 units), at most 12 questions/240
        // choices, two scalar envelopes and one scalar disposition. Generated-serializer
        // escaping/framing is independently tested.
        return Task.FromResult(new SessionAsksPage("ok", _epoch, request.SessionId, ToWire(page.Head), ToWire(page.Latest), page.HasMore));
    }

    [NeoRpcMethod("answer")]
    public Task<SessionAskResult> AnswerAsync(SessionAskActionRequest request, CancellationToken cancellationToken)
        => ActAsync(request, false, cancellationToken);

    [NeoRpcMethod("cancel")]
    public Task<SessionAskResult> CancelAsync(SessionAskActionRequest request, CancellationToken cancellationToken)
        => ActAsync(request, true, cancellationToken);

    [NeoRpcMethod("observe")]
    public Task<SessionAskResult> ObserveAsync(SessionAskObservationRequest request, CancellationToken cancellationToken)
    {
        var error = Check(request?.ExpectedHostEpoch, request?.Handle?.SessionId);
        if (request is null || request.ActionId == Guid.Empty || !TryHandle(request.Handle, out var handle))
            return Task.FromResult(new SessionAskResult("invalid_request", _epoch, null));
        if (error is not null) return Task.FromResult(new SessionAskResult(error, _epoch, null));
        cancellationToken.ThrowIfCancellationRequested();
        var value = _owner.Observe(request.ActionId, handle);
        return Task.FromResult(new SessionAskResult(value is null ? "not_found" : "ok", _epoch, ToWire(value)));
    }

    private async Task<SessionAskResult> ActAsync(SessionAskActionRequest request, bool cancel, CancellationToken token)
    {
        var error = Check(request?.ExpectedHostEpoch, request?.Action?.Handle?.SessionId);
        if (request?.Action is not { } action || !TryHandle(action.Handle, out var handle))
            return new("invalid_request", _epoch, null);
        if (error is not null) return new(error, _epoch, null);
        token.ThrowIfCancellationRequested();
        try
        {
            // The review a request asks for: its round is composed from the user's comments, and recorded once the
            // session has it. An answer without its verdict, or a verdict for a plain question, is refused by the owner.
            var target = _owner.List(handle.SessionId).Head is { } head && head.Handle.AskId == handle.AskId ? head.Request.Review : null;
            ReviewScope? scope = null;
            ReviewRoundDraft? round = null;
            if (target is not null)
            {
                scope = await ScopeAsync(target, token).ConfigureAwait(false);
                if (!cancel && action.Review is { } submitted)
                {
                    if (scope is null) return new("review_unavailable", _epoch, null);
                    round = await reviews!.PrepareRoundAsync(scope, new ReviewSubmission(target.Kind!, target.Path, submitted.Verdict, submitted.Overall, handle.SessionId, handle.AskId), token)
                        .ConfigureAwait(false);
                }
            }
            else if (action.Review is not null)
            {
                return new("invalid_request", _epoch, null);
            }

            // The round is recorded before the session gets it, so that an agent that answers at once finds its
            // comments open; it is undone when the session does not take the answer.
            var record = round is null ? null : reviews!.RecordRound(scope!, round);

            // Owner retains the original before return. Only this transport wait is cancellable.
            // Decimal parsing supplies only scalar context. The owner still recovers its exact queue handle.
            var domain = new OwnedAskAction(action.ActionId, handle, action.Answers, round?.Text);
            OwnedAskDisposition result;
            try
            {
                var original = cancel ? _owner.CancelAsync(domain, token) : _owner.AnswerAsync(domain, token);
                result = await original.WaitAsync(token).ConfigureAwait(false);
            }
            catch (Exception exception) when (record is not null && exception is not OperationCanceledException)
            {
                Revert(scope!, record);
                throw;
            }

            if (scope is not null)
            {
                Settle(scope, handle.AskId, record, result.Status);
            }

            return new("ok", _epoch, ToWire(result));
        }
        catch (ReviewException exception) { return new(exception.Code == "nothingToSubmit" ? "nothing_to_submit" : "review_failed", _epoch, null); }
        catch (ArgumentException) { return new("invalid_request", _epoch, null); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return new("uncertain", _epoch, null); }
    }

    /// <summary>The comments the answer of a review request sends now: the drafts and the open comments of its target.</summary>
    /// <returns>Their number; null when the host keeps no reviews, the project is gone or the store cannot be read.</returns>
    internal async Task<int?> CommentsAsync(AltaReviewTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (await ScopeAsync(target, cancellationToken).ConfigureAwait(false) is not { } scope) return null;
        try
        {
            var state = await reviews!.ListAsync(scope, target.Kind, target.Kind == ReviewTargets.File ? target.Path : null, false, cancellationToken).ConfigureAwait(false);
            return state.Threads.Count(static thread => thread.State is ReviewThreadStates.Draft or ReviewThreadStates.Open);
        }
        catch (ReviewException)
        {
            return null;
        }
    }

    // The checkout of the review a request asks for; null when the host keeps no reviews or the project is gone.
    private async Task<ReviewScope?> ScopeAsync(AltaReviewTarget target, CancellationToken token)
    {
        if (reviews is null || projects is null || target.ProjectId is not { } projectId) return null;
        var project = await projects.GetByIdAsync(projectId, token).ConfigureAwait(false);
        return project is null ? null : ReviewService.ScopeOf(project, target.Checkout);
    }

    // A round the session did not get is undone; a cancelled request is forgotten. A dispatch whose outcome is not
    // known keeps the round: the session most likely has it, and the comments are then not sent twice.
    private void Settle(ReviewScope scope, string askId, ReviewRoundRecord? record, string status)
    {
        if (record is not null)
        {
            if (status is not ("admitted" or "indeterminate")) Revert(scope, record);
            return;
        }

        try
        {
            if (status == "cancelled") reviews!.RemoveRequest(scope, askId);
        }
        catch (ReviewException)
        {
            // The answer reached the session; the store says what it can, and the page reads it again.
        }
    }

    private void Revert(ReviewScope scope, ReviewRoundRecord record)
    {
        try
        {
            reviews!.RevertRound(scope, record);
        }
        catch (ReviewException)
        {
            // The store cannot be written: its comments stay as sent, and the user sends them again by a reply.
        }
    }

    private string? Check(string? expected, string? sessionId) => expected != _epoch ? "stale_epoch" : Volatile.Read(ref _closed) != 0 ? "closed"
        : !_owner.Enabled(sessionId) ? "disabled" : null;
    private static bool CanonicalEpoch(string value) => Guid.TryParseExact(value, "D", out var parsed) && parsed != Guid.Empty && parsed.ToString("D") == value;

    internal static SessionAskHandle ToWire(OwnedAskHandle handle) => new(handle.OperationId, handle.RuntimeInstanceId,
        handle.AttachmentGeneration.ToString(CultureInfo.InvariantCulture), handle.ProviderId, handle.SessionId, handle.RunId,
        handle.AskId, handle.ResponseGeneration.ToString(CultureInfo.InvariantCulture));

    internal static SessionAskHead? ToWire(OwnedAskHead? head)
        => head is null ? null : new(ToWire(head.Handle), head.Request, head.State, head.Immediate);

    internal static SessionAskDisposition? ToWire(OwnedAskDisposition? disposition)
        => disposition is null ? null : new(disposition.ActionId, ToWire(disposition.Handle), disposition.Status, disposition.RunId);

    internal static bool TryHandle(SessionAskHandle? value, [NotNullWhen(true)] out OwnedAskHandle? handle)
    {
        handle = null;
        if (value is null || !Generation(value.AttachmentGeneration, 1, 9007199254740991, out var attachment)
            || !Generation(value.ResponseGeneration, 0, 256, out var response)) return false;
        var parsed = new OwnedAskHandle(value.OperationId, value.RuntimeInstanceId, attachment, value.ProviderId,
            value.SessionId, value.RunId, value.AskId, response);
        if (!OwnedSessionAskService.IsValidHandle(parsed)) return false;
        handle = parsed;
        return true;
    }

    private static bool Generation(string? value, long minimum, long maximum, out long generation)
    {
        generation = 0;
        return value is { Length: > 0 and <= 16 }
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out generation)
            && generation >= minimum && generation <= maximum
            && generation.ToString(CultureInfo.InvariantCulture) == value;
    }
}

// Desktop wire DTOs deliberately do not expose domain Int64 properties to the RPC generator.
internal sealed record SessionAskHandle(Guid OperationId, Guid RuntimeInstanceId, string AttachmentGeneration,
    string ProviderId, string SessionId, string RunId, string AskId, string ResponseGeneration);
/// <param name="Immediate">Whether the ask is shown at once, while the turn of its session runs: a question of a script (<c>ask()</c>).</param>
internal sealed record SessionAskHead(SessionAskHandle Handle, AltaAskRequest Request, string State, bool Immediate);
internal sealed record SessionAskDisposition(Guid ActionId, SessionAskHandle Handle, string Status, string? RunId);
/// <summary>An answer or a cancel of the pending ask. <paramref name="Review"/> is the verdict of a review request.</summary>
internal sealed record SessionAskAction(Guid ActionId, SessionAskHandle Handle, IReadOnlyList<AltaAskAnswer> Answers, SessionAskReview? Review = null);
/// <summary>What the user decides of the review a request asks for; the comments are those of the review store.</summary>
/// <param name="Verdict"><c>approved</c>, <c>changes_requested</c> or <c>commented</c>.</param>
/// <param name="Overall">The overall comment, or null.</param>
internal sealed record SessionAskReview(string Verdict, string? Overall);
internal sealed record SessionAsksRequest(string ExpectedHostEpoch, string SessionId);
internal sealed record SessionAsksPage(string Status, string HostEpoch, string? SessionId, SessionAskHead? Head, SessionAskDisposition? Latest, bool HasMore);
internal sealed record SessionAskActionRequest(string ExpectedHostEpoch, SessionAskAction Action);
internal sealed record SessionAskObservationRequest(string ExpectedHostEpoch, Guid ActionId, SessionAskHandle Handle);
internal sealed record SessionAskResult(string Status, string HostEpoch, SessionAskDisposition? Disposition);
