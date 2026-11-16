using System.Globalization;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Fleet.Client;

/// <summary>
/// What the sessions of another machine ask a person, for what answers them from this computer for that person (the page of
/// the phones): their permission requests, answered through the node API as the window answers them. A machine that is not
/// reached has none to show, and an answer that could not be confirmed is said so, never taken for given.
/// </summary>
/// <param name="connect">Gives the open channel to the machine, or null while it is not reached.</param>
public sealed class FleetMachinePermissions(Func<CancellationToken, ValueTask<FleetNodeClient?>> connect) : IPluginPermissionService
{
    /// <summary>The most sessions that wait whose requests one reading asks for.</summary>
    public const int MaxWaiting = 24;

    /// <inheritdoc />
    public bool HasPermissions => true;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<PluginPermissionSummary>> ListWaitingAsync(CancellationToken cancellationToken = default)
    {
        if (await connect(cancellationToken).ConfigureAwait(false) is not { } client) return [];
        var waiting = new List<PluginPermissionSummary>();
        foreach (var session in await FleetMachineAsks.WaitingSessionsAsync(client, cancellationToken).ConfigureAwait(false))
        {
            var page = await client.ListPermissionsAsync(session, cancellationToken).ConfigureAwait(false);
            if (page.Requests.Count > 0) waiting.Add(new(session, page.Requests[0].Id.ToString("D"), page.Requests.Count));
        }

        return waiting;
    }

    /// <inheritdoc />
    public async ValueTask<PluginPermissionPage> ListAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (await connect(cancellationToken).ConfigureAwait(false) is not { } client) return new([], false);
        var page = await client.ListPermissionsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new([.. page.Requests.Select(static request => new PluginPermissionRequest
        {
            RequestId = request.Id.ToString("D"), Kind = request.Kind, Command = request.Command, WorkingDirectory = request.WorkingDirectory, GrantRoot = request.GrantRoot,
            Reason = request.Reason, Shortened = request.Shortened, OutsideSandbox = request.OutsideSandbox, SkillServer = request.SkillServer, SkillName = request.SkillName,
        })], page.HasMore);
    }

    /// <inheritdoc />
    public async ValueTask<PluginPermissionOutcome> ResolveAsync(string sessionId, string requestId, PluginPermissionDecision decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(requestId);
        if (!Guid.TryParse(requestId, out var id) || id == Guid.Empty) return new("invalid");
        var answer = decision switch { PluginPermissionDecision.AllowOnce => "allow_once", PluginPermissionDecision.Deny => "deny", _ => null };
        if (answer is null) return new("invalid");
        FleetNodeClient? client;
        try
        {
            client = await connect(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return new("unavailable");
        }

        if (client is null) return new("unavailable");
        try
        {
            var result = await client.AnswerPermissionAsync(sessionId, id, answer, cancellationToken).ConfigureAwait(false);
            return new(result.Status == "resolved" ? "resolved" : "rejected");
        }
        catch (FleetException exception) when (exception.Error.Code is FleetErrorCodes.Forbidden or FleetErrorCodes.InvalidParams or FleetNodeErrorCodes.SessionNotFound)
        {
            return new("rejected");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The answer may have reached the machine: it is not sent again, and the person checks the session.
            return new("uncertain");
        }
    }
}

/// <summary>
/// The questions the sessions of another machine ask a person, for what answers them from this computer for that person.
/// Only an ask the machine says is made of questions is shown: a review request is answered with a verdict the node API does
/// not carry, and an ask of an older machine, which does not say what it is, is not known to be a question.
/// </summary>
/// <param name="connect">Gives the open channel to the machine, or null while it is not reached.</param>
public sealed class FleetMachineAsks(Func<CancellationToken, ValueTask<FleetNodeClient?>> connect) : IPluginAskService
{
    /// <inheritdoc />
    public bool HasAsks => true;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<PluginAskSummary>> ListAskingAsync(CancellationToken cancellationToken = default)
    {
        if (await connect(cancellationToken).ConfigureAwait(false) is not { } client) return [];
        var asking = new List<PluginAskSummary>();
        foreach (var session in await WaitingSessionsAsync(client, cancellationToken).ConfigureAwait(false))
        {
            if ((await client.ListAsksAsync(session, cancellationToken).ConfigureAwait(false)).Ask is { } ask)
                asking.Add(new(session, ask.AskId, ask.Revision ?? "0", State(ask.State), ask.Immediate));
        }

        return asking;
    }

    /// <inheritdoc />
    public async ValueTask<PluginAsk?> GetPendingAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (await connect(cancellationToken).ConfigureAwait(false) is not { } client) return null;
        if ((await client.ListAsksAsync(sessionId, cancellationToken).ConfigureAwait(false)).Ask is not { } ask) return null;
        return new PluginAsk
        {
            SessionId = sessionId, AskId = ask.AskId, Revision = ask.Revision ?? "0", State = State(ask.State), Immediate = ask.Immediate,
            Questions = ask.Kind == FleetAsk.KindQuestions
                ? [.. ask.Questions.Select(static question => new PluginAskQuestion
                {
                    Title = question.Title ?? "", Question = question.Question ?? "", Description = question.Description,
                    Choices = [.. question.Choices.Select(static choice => new PluginAskChoice { Title = choice.Title ?? "", Description = choice.Description })],
                    Freeform = question.AllowsText ? new PluginAskFreeform { Title = question.FreeformTitle, Placeholder = question.FreeformPlaceholder } : null,
                })]
                : [],
            // What is not known to be questions is shown as something to answer on a computer, never as a question.
            Review = ask.Kind == FleetAsk.KindQuestions ? null : new PluginAskReview { Kind = Elsewhere },
        };
    }

    /// <summary>The kind of the review of an ask that is answered on a computer: a review request of another machine, or an ask an older machine does not describe.</summary>
    public const string Elsewhere = "elsewhere";

    /// <inheritdoc />
    public ValueTask<PluginAskOutcome> AnswerAsync(PluginAskReference ask, IReadOnlyList<PluginAskAnswer> answers, PluginAskReviewAnswer? review,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(answers);
        // A verdict is not something the node API carries.
        if (review is not null) return ValueTask.FromResult(new PluginAskOutcome("rejected"));
        return SendAsync(ask, [.. answers.Select(static answer => new FleetAskAnswer(answer.QuestionIndex, answer.SelectedChoiceIndexes, answer.FreeformText))], cancel: false, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PluginAskOutcome> CancelAsync(PluginAskReference ask, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ask);
        return SendAsync(ask, [], cancel: true, cancellationToken);
    }

    private async ValueTask<PluginAskOutcome> SendAsync(PluginAskReference ask, IReadOnlyList<FleetAskAnswer> answers, bool cancel, CancellationToken cancellationToken)
    {
        FleetNodeClient? client;
        try
        {
            client = await connect(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return new("unavailable");
        }

        if (client is null) return new("unavailable");
        try
        {
            // The ask is read again: an answer goes only to the ask the person read, of the kind this API answers.
            var current = (await client.ListAsksAsync(ask.SessionId, cancellationToken).ConfigureAwait(false)).Ask;
            if (current is null || current.AskId != ask.AskId || (current.Revision ?? "0") != ask.Revision) return new("rejected");
            if (!cancel && current.Kind != FleetAsk.KindQuestions) return new("rejected");
            // The action of the phone is the key of the request: a retry of it is the same answer.
            var result = await client.AnswerAskAsync(new FleetAskAnswerParams(ask.SessionId, ask.AskId, answers, cancel) { Revision = ask.Revision }, ask.ActionId.ToString("N"), cancellationToken).ConfigureAwait(false);
            return new(result.Status == "not_found" ? "rejected" : result.Status);
        }
        catch (FleetException exception) when (exception.Error.Code is FleetErrorCodes.Forbidden or FleetErrorCodes.InvalidParams or FleetNodeErrorCodes.SessionNotFound)
        {
            return new("rejected");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new("indeterminate");
        }
    }

    private static string State(string state) => state is "pending" or "submitting" ? state : "indeterminate";

    // The sessions of the machine that wait for a person, the latest first, as its list says them.
    internal static async Task<IReadOnlyList<string>> WaitingSessionsAsync(FleetNodeClient client, CancellationToken cancellationToken)
    {
        var listed = await client.ListSessionsAsync(new FleetSessionListParams(Limit: 200), cancellationToken).ConfigureAwait(false);
        return [.. listed.Sessions.Where(static session => session.State == "waiting").Take(FleetMachinePermissions.MaxWaiting).Select(static session => session.Id)];
    }
}
