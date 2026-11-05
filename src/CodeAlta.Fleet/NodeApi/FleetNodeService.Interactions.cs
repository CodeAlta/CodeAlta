using System.Security.Cryptography;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.LiveTool;

namespace CodeAlta.Fleet.NodeApi;

public sealed partial class FleetNodeService
{
    private const int MaxAnswerText = 8192;

    // The requests of the owned sends that wait for a person, as the desktop's permission cards list them. A request is
    // named by its attempt: the answer finds it again among those that still wait, so it never applies to another one.
    private async Task<FleetPermissionList> ListPermissionsAsync(FleetNodeCall call, FleetSessionRef parameters, CancellationToken cancellationToken)
    {
        var sessionId = await RequireRemoteSessionAsync(parameters.SessionId, cancellationToken).ConfigureAwait(false);
        var page = await _host.RuntimeService.Permissions.ListOwnedCommandsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new FleetPermissionList([.. page.Entries.Select(static entry => new FleetPermissionRequest(entry.Handle.Attempt.AttemptId, entry.Handle.Attempt.RunId,
            entry.Request.Kind, entry.Request.Command, entry.Request.WorkingDirectory, entry.Request.GrantRoot, entry.Request.Reason, entry.Request.Timestamp))], page.HasMore);
    }

    private async Task<FleetAnswerResult> AnswerPermissionAsync(FleetNodeCall call, FleetPermissionAnswerParams parameters, CancellationToken cancellationToken)
    {
        var sessionId = await RequireRemoteSessionAsync(parameters.SessionId, cancellationToken).ConfigureAwait(false);
        var decision = parameters.Decision switch
        {
            "allow_once" => AgentPermissionDecisionKind.AllowOnce,
            "allow_always" => AgentPermissionDecisionKind.AllowAlways,
            "deny" => AgentPermissionDecisionKind.Deny,
            "cancel" => AgentPermissionDecisionKind.Cancel,
            _ => throw new FleetException(FleetErrorCodes.InvalidParams, null, "A decision is allow_once, allow_always, deny or cancel."),
        };
        var page = await _host.RuntimeService.Permissions.ListOwnedCommandsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var entry = page.Entries.FirstOrDefault(entry => entry.Handle.Attempt.AttemptId == parameters.Id);
        if (entry is null) return new FleetAnswerResult("not_found");
        var resolved = await _host.RuntimeService.Permissions.ResolveOwnedCommandAsync(entry.Handle, decision, cancellationToken).ConfigureAwait(false);
        return new FleetAnswerResult(resolved ? "resolved" : "rejected", entry.Handle.Attempt.RunId);
    }

    private async Task<FleetAskList> ListAsksAsync(FleetNodeCall call, FleetSessionRef parameters, CancellationToken cancellationToken)
    {
        var sessionId = await RequireRemoteSessionAsync(parameters.SessionId, cancellationToken).ConfigureAwait(false);
        var page = _host.Commands.Asks.List(sessionId);
        var ask = page.Head is { } head
            ? new FleetAsk(head.Handle.AskId, head.Handle.RunId, head.State, [.. head.Request.Questions.Select(static question => new FleetAskQuestion(question.Title,
                question.Question, question.Description, [.. question.Choices.Select(static choice => new FleetAskChoice(choice.Title, choice.Description))],
                question.Freeform is not null))])
            : null;
        return new FleetAskList(ask, page.HasMore);
    }

    // An answer is bound to the question that waits (its exact handle), and its action id comes from the request's key,
    // so a retry is the same action.
    private async Task<FleetAnswerResult> AnswerAskAsync(FleetNodeCall call, FleetAskAnswerParams parameters, CancellationToken cancellationToken)
    {
        var retryKey = RetryKey(call);
        var sessionId = await RequireRemoteSessionAsync(parameters.SessionId, cancellationToken).ConfigureAwait(false);
        var answers = (parameters.Answers ?? []).Select(static answer =>
        {
            if (answer.QuestionIndex < 0 || answer.Text is { Length: > MaxAnswerText })
                throw new FleetException(FleetErrorCodes.InvalidParams, null, "An answer names a question and has at most 8192 characters.");
            return new AltaAskAnswer { QuestionIndex = answer.QuestionIndex, SelectedChoiceIndexes = [.. answer.Choices ?? []], FreeformText = answer.Text };
        }).ToArray();
        return await Once(call, FleetNodeJsonContext.Default.FleetAnswerResult, async () =>
        {
            var head = _host.Commands.Asks.List(sessionId).Head;
            if (head is null || !string.Equals(head.Handle.AskId, parameters.AskId, StringComparison.Ordinal)) return new FleetAnswerResult("not_found");
            var action = new OwnedAskAction(ActionId(retryKey), head.Handle, answers);
            var disposition = parameters.Cancel
                ? await _host.Commands.Asks.CancelAsync(action, CancellationToken.None).ConfigureAwait(false)
                : await _host.Commands.Asks.AnswerAsync(action, CancellationToken.None).ConfigureAwait(false);
            return new FleetAnswerResult(disposition.Status, disposition.RunId);
        }).ConfigureAwait(false);
    }

    private static Guid ActionId(string retryKey)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(retryKey), hash);
        return new Guid(hash[..16]);
    }
}
