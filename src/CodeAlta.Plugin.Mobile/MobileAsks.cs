using System.Text.Json.Nodes;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// The questions agents ask the user and the reviews they request, as the phone reads and answers them: the pending ask of a
/// session, as the window shows it, and the user's answer or cancel, which the session receives as from the window. They go
/// through the ask service of the host, never through <c>alta</c>: the commands of <c>alta</c> are those of agents, which
/// never answer what they ask. A session the phone does not see (<see cref="MobileSessions.FindAsync"/>) is refused as unknown.
/// </summary>
internal sealed class MobileAsks(IPluginAskService asks, MobileSessions sessions, TimeSpan? patience = null)
{
    // The limits of an ask (AltaAskValidator): 12 questions of 20 choices; those of the text are the host's.
    private const int MaximumQuestions = 12, MaximumChoices = 20;

    /// <summary>What the phone says of answers longer than the host takes.</summary>
    internal static readonly string AnswerTooLong = $"The answers are too long: {PluginAskLimits.AnswerText} characters in all at most.";

    /// <summary>What the phone says of an overall comment longer than the host takes.</summary>
    internal static readonly string OverallTooLong = $"The overall comment is too long: {PluginAskLimits.OverallComment} characters at most.";

    // How long a request waits for the session to take an answer: the live stream says the rest.
    private readonly TimeSpan _patience = patience ?? TimeSpan.FromSeconds(3);

    /// <summary>The ask a session waits on, or none.</summary>
    /// <returns>The ask, or null when the phone does not see the session.</returns>
    internal async Task<JsonObject?> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (await sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        var ask = asks.HasAsks ? await asks.GetPendingAsync(id, cancellationToken).ConfigureAwait(false) : null;
        return new JsonObject { ["ask"] = ask is null ? null : ToJson(ask), ["error"] = null };
    }

    /// <summary>
    /// Answers the ask of a session: <c>{ actionId, askId, revision, answers: [{ question, choices, text }], review: { verdict, overall } }</c>.
    /// </summary>
    /// <returns>What became of the answer, or null when the phone does not see the session.</returns>
    internal async Task<JsonObject?> AnswerAsync(string id, JsonObject body, CancellationToken cancellationToken)
    {
        if (Reference(id, body) is not { } reference) return Error("The answer is not valid.");
        var answers = Answers(body, out var tooLong);
        if (tooLong) return Error(AnswerTooLong);
        if (answers is null) return Error("The answer is not valid.");
        var review = Review(body, out tooLong);
        if (tooLong) return Error(OverallTooLong);
        if (review.Invalid) return Error("The answer is not valid.");
        if (await sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        return await PatientAsync(asks.AnswerAsync(reference, answers, review.Answer, CancellationToken.None)).ConfigureAwait(false);
    }

    /// <summary>Cancels the ask of a session without answering it: <c>{ actionId, askId, revision }</c>.</summary>
    /// <returns>What became of the cancel, or null when the phone does not see the session.</returns>
    internal async Task<JsonObject?> CancelAsync(string id, JsonObject body, CancellationToken cancellationToken)
    {
        if (Reference(id, body) is not { } reference) return Error("The request is not valid.");
        if (await sessions.FindAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        return await PatientAsync(asks.CancelAsync(reference, CancellationToken.None)).ConfigureAwait(false);
    }

    // Not tied to the request: a phone that gives up must not leave an answer half sent. The same action id sent again gets
    // the outcome of the first, or waits for it again while it goes.
    private async Task<JsonObject> PatientAsync(ValueTask<PluginAskOutcome> outcome)
    {
        var task = outcome.AsTask();
        if (await Task.WhenAny(task, Task.Delay(_patience, CancellationToken.None)).ConfigureAwait(false) != task) return Pending();
        var status = (await task.ConfigureAwait(false)).Status;
        return status == "submitting" ? Pending() : new JsonObject { ["ok"] = status is "admitted" or "cancelled", ["status"] = status, ["error"] = Message(status) };
    }

    private static JsonObject Pending() => new() { ["ok"] = true, ["status"] = "submitting", ["pending"] = true, ["error"] = null };

    /// <summary>What the phone says of an answer that did not reach the session.</summary>
    internal static string? Message(string status) => status switch
    {
        "admitted" or "cancelled" or "submitting" => null,
        "not_admitted" => "The session did not take the answer. Try again.",
        "indeterminate" => "The answer was sent, but CodeAlta cannot tell whether the session got it. Check the session on the computer.",
        "rejected" or "conflict" => "This question was answered or changed meanwhile.",
        "invalid" => "The answer does not fit the question.",
        "nothing_to_submit" => "Write a comment or an overall comment to send a comment-only review.",
        "review_failed" => "The review could not be saved.",
        _ => "CodeAlta cannot take answers now.",
    };

    internal static JsonObject ToJson(PluginAsk ask)
    {
        var questions = new JsonArray();
        foreach (var question in ask.Questions)
        {
            var choices = new JsonArray();
            foreach (var choice in question.Choices) choices.Add(new JsonObject { ["title"] = choice.Title, ["description"] = choice.Description });
            questions.Add(new JsonObject
            {
                ["title"] = question.Title, ["question"] = question.Question, ["description"] = question.Description, ["choices"] = choices,
                ["freeform"] = question.Freeform is { } freeform ? new JsonObject { ["title"] = freeform.Title, ["placeholder"] = freeform.Placeholder } : null,
            });
        }

        return new JsonObject
        {
            ["id"] = ask.AskId, ["revision"] = ask.Revision, ["state"] = ask.State, ["immediate"] = ask.Immediate, ["questions"] = questions,
            ["review"] = ask.Review is { } review
                ? new JsonObject { ["kind"] = review.Kind, ["path"] = review.Path, ["title"] = review.Title, ["message"] = review.Message, ["comments"] = review.Comments }
                : null,
        };
    }

    private static PluginAskReference? Reference(string id, JsonObject body)
    {
        if (!MobileApi.IsId(id) || !Guid.TryParse(MobileAlta.Text(body, "actionId"), out var action) || action == Guid.Empty) return null;
        var askId = MobileAlta.Text(body, "askId");
        var revision = MobileAlta.Text(body, "revision");
        return MobileApi.IsId(askId) && revision is { Length: > 0 and <= 16 } && revision.All(char.IsAsciiDigit) ? new(action, id, askId!, revision) : null;
    }

    /// <summary>The ask of a session as the live stream says it: what tells the phone to read the ask again.</summary>
    internal static JsonObject Mark(PluginAskSummary ask) => new()
    {
        ["id"] = ask.AskId, ["revision"] = ask.Revision, ["state"] = ask.State, ["immediate"] = ask.Immediate,
    };

    private static IReadOnlyList<PluginAskAnswer>? Answers(JsonObject body, out bool tooLong)
    {
        tooLong = false;
        if (body["answers"] is not JsonArray items || items.Count > MaximumQuestions) return null;
        var answers = new List<PluginAskAnswer>(items.Count);
        var text = 0;
        foreach (var node in items)
        {
            if (node is not JsonObject item || !Integer(item["question"], MaximumQuestions, out var question)) return null;
            var chosen = new List<int>();
            if (item["choices"] is JsonArray choices)
            {
                if (choices.Count > MaximumChoices) return null;
                foreach (var choice in choices)
                {
                    if (!Integer(choice, MaximumChoices, out var index)) return null;
                    chosen.Add(index);
                }
            }
            else if (item["choices"] is not null) return null;

            var freeform = MobileAlta.Text(item, "text");
            text += freeform?.Length ?? 0;
            if (text > PluginAskLimits.AnswerText)
            {
                tooLong = true;
                return null;
            }

            answers.Add(new PluginAskAnswer { QuestionIndex = question, SelectedChoiceIndexes = chosen, FreeformText = string.IsNullOrWhiteSpace(freeform) ? null : freeform });
        }

        return answers;
    }

    private static (PluginAskReviewAnswer? Answer, bool Invalid) Review(JsonObject body, out bool tooLong)
    {
        tooLong = false;
        if (body["review"] is null) return (null, false);
        if (body["review"] is not JsonObject value || MobileAlta.Text(value, "verdict") is not ("approved" or "changes_requested" or "commented")) return (null, true);
        var overall = MobileAlta.Text(value, "overall");
        if (overall is { Length: > PluginAskLimits.OverallComment })
        {
            tooLong = true;
            return (null, true);
        }

        return (new(MobileAlta.Text(value, "verdict")!, string.IsNullOrWhiteSpace(overall) ? null : overall), false);
    }

    private static bool Integer(JsonNode? node, int limit, out int value)
    {
        value = 0;
        return node is JsonValue number && number.TryGetValue(out value) && value >= 0 && value < limit;
    }

    private static JsonObject Error(string message) => new() { ["error"] = message };
}
