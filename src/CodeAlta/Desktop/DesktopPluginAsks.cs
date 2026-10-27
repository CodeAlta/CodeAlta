using CodeAlta.Desktop.Rpc;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop;

/// <summary>
/// The asks the plugins of the window read and answer for the user, as the window does: through the same service, so that
/// the answer of a review request carries the round of the user's comments. Until the host is attached (while the plugins
/// start), no session waits.
/// </summary>
internal sealed class DesktopPluginAsks : IPluginAskService
{
    private Attached? _attached;

    /// <summary>Gives the asks of the host once it runs.</summary>
    /// <param name="owner">The owner of the asks.</param>
    /// <param name="window">The service the window answers through.</param>
    /// <param name="epoch">The epoch of the host, which <paramref name="window"/> checks.</param>
    internal void Attach(OwnedSessionAskService owner, SessionAsksService window, string epoch)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrEmpty(epoch);
        Volatile.Write(ref _attached, new(owner, window, epoch));
    }

    /// <inheritdoc />
    public bool HasAsks => Volatile.Read(ref _attached) is { Owner.Enabled: true };

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListAskingSessionsAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Volatile.Read(ref _attached) is { Owner.Enabled: true } attached ? attached.Owner.ListWaitingSessions() : (IReadOnlyList<string>)[]);

    /// <inheritdoc />
    public async ValueTask<PluginAsk?> GetPendingAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (Volatile.Read(ref _attached) is not { } attached || !OwnedSessionAskService.IsValidSessionId(sessionId)) return null;
        var page = await attached.Window.ListAsync(new(attached.Epoch, sessionId), cancellationToken).ConfigureAwait(false);
        if (page is not { Status: "ok", Head: { } head }) return null;
        var ask = ToPlugin(head);
        return ask.Review is { } review && head.Request.Review is { } target
            ? ask with { Review = review with { Comments = await attached.Window.CommentsAsync(target, cancellationToken).ConfigureAwait(false) } }
            : ask;
    }

    /// <inheritdoc />
    public ValueTask<PluginAskOutcome> AnswerAsync(PluginAskReference ask, IReadOnlyList<PluginAskAnswer> answers, PluginAskReviewAnswer? review,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(answers);
        var domain = answers.Select(static answer => answer is null ? null : new AltaAskAnswer
        {
            QuestionIndex = answer.QuestionIndex,
            SelectedChoiceIndexes = [.. answer.SelectedChoiceIndexes ?? []],
            FreeformText = answer.FreeformText,
        }).ToArray();
        if (domain.Any(static answer => answer is null)) return ValueTask.FromResult(new PluginAskOutcome("invalid"));
        return ActAsync(ask, domain!, review is null ? null : new SessionAskReview(review.Verdict, review.Overall), false, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PluginAskOutcome> CancelAsync(PluginAskReference ask, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ask);
        return ActAsync(ask, [], null, true, cancellationToken);
    }

    // The ask is read again: an answer goes only to the ask, in the revision, the user saw. One sent again after the
    // first reached the session finds the outcome of the first.
    private async ValueTask<PluginAskOutcome> ActAsync(PluginAskReference ask, IReadOnlyList<AltaAskAnswer> answers, SessionAskReview? review,
        bool cancel, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _attached) is not { } attached) return new("unavailable");
        if (ask.ActionId == Guid.Empty || !OwnedSessionAskService.IsValidSessionId(ask.SessionId) || string.IsNullOrEmpty(ask.AskId)) return new("invalid");
        var page = await attached.Window.ListAsync(new(attached.Epoch, ask.SessionId), cancellationToken).ConfigureAwait(false);
        if (page.Status != "ok") return new(Outcome(page.Status));
        if (page.Latest is { } latest && latest.ActionId == ask.ActionId && latest.Handle.AskId == ask.AskId) return new(latest.Status);
        if (page.Head is not { } head || head.Handle.AskId != ask.AskId || head.Handle.ResponseGeneration != ask.Revision) return new("rejected");
        var request = new SessionAskActionRequest(attached.Epoch, new SessionAskAction(ask.ActionId, head.Handle, answers, review));
        var result = cancel
            ? await attached.Window.CancelAsync(request, cancellationToken).ConfigureAwait(false)
            : await attached.Window.AnswerAsync(request, cancellationToken).ConfigureAwait(false);
        return new(result is { Status: "ok", Disposition: { } disposition } ? disposition.Status : Outcome(result.Status));
    }

    private static string Outcome(string status) => status switch
    {
        "invalid_request" => "invalid",
        "nothing_to_submit" => "nothing_to_submit",
        "review_unavailable" or "review_failed" => "review_failed",
        "uncertain" => "indeterminate",
        _ => "unavailable",
    };

    internal static PluginAsk ToPlugin(SessionAskHead head) => new()
    {
        SessionId = head.Handle.SessionId,
        AskId = head.Handle.AskId,
        Revision = head.Handle.ResponseGeneration,
        State = head.State,
        Questions = [.. head.Request.Questions.Select(static question => new PluginAskQuestion
        {
            Title = question.Title ?? "",
            Question = question.Question ?? "",
            Description = question.Description,
            Choices = [.. question.Choices.Select(static choice => new PluginAskChoice { Title = choice.Title ?? "", Description = choice.Description })],
            Freeform = question.Freeform is { } freeform ? new PluginAskFreeform { Title = freeform.Title, Placeholder = freeform.Placeholder } : null,
        })],
        Review = head.Request.Review is { Kind: { } kind } review
            ? new PluginAskReview { Kind = kind, Path = review.Path, Title = review.Title, Message = review.Message }
            : null,
    };

    private sealed record Attached(OwnedSessionAskService Owner, SessionAsksService Window, string Epoch);
}
