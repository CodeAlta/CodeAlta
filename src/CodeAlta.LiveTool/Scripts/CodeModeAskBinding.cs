using System.Text;
using System.Text.Json;
using CodeAlta.CodeMode;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.LiveTool.Scripts;

/// <summary>
/// <c>ask(question, { choices, default })</c> in scripts: a question to the user, where the session that runs the script
/// asks its questions; it resolves with the answer, and rejects with an <c>AskError</c> whose code is <c>cancelled</c>
/// (the user dismissed it, or the run stopped) or <c>unavailable</c> (nobody can be asked). The prelude gives scripts the
/// function; it calls <c>codemode.ask</c>, which this binding runs.
/// </summary>
public sealed class CodeModeAskBinding : ICodeModeCallHandler
{
    /// <summary>The path of the function in scripts.</summary>
    public const string Path = "codemode.ask";

    private const int MaxChoices = 20;

    private readonly ICodeModeQuestions? _questions;
    private readonly string _sessionId;
    private readonly Func<string> _runId;

    /// <summary>Creates the binding of one run.</summary>
    /// <param name="questions">Where the questions reach the user, or null for a host where nobody can be asked.</param>
    /// <param name="sessionId">The session that runs the script.</param>
    /// <param name="runId">Gives the id of the run.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> or <paramref name="runId"/> is null.</exception>
    public CodeModeAskBinding(ICodeModeQuestions? questions, string sessionId, Func<string> runId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(runId);
        _questions = questions;
        _sessionId = sessionId;
        _runId = runId;
    }

    /// <summary>Gets the declaration of <c>codemode.ask</c>.</summary>
    public static CodeModeFunction Function { get; } = new(Path)
    {
        Description = "Asks the user a question where this session asks its questions, and resolves with the answer: the choice taken, or the text written (the default when left empty); scripts call it as ask(question, options). The questions of a run come one at a time, and the time the user takes does not count toward the time limit. Throws an AskError with code cancelled when the user dismisses it, unavailable when nobody can be asked (an automation). In a step, a replay gives the recorded answer without asking.",
        InputSchemaJson = """
            {"type":"object","properties":{
              "question":{"type":"string","description":"The question, in a few lines."},
              "choices":{"type":"array","items":{"type":"string"},"maxItems":20,"description":"The answers the user chooses from; without them the user writes the answer."},
              "default":{"type":"string","description":"The answer given when the user leaves the question empty."}
            },"required":["question"],"additionalProperties":false}
            """,
        ResultType = "{ answer: string }",
        Parallel = true,
        WaitsForUser = true,
    };

    /// <inheritdoc />
    public async ValueTask<CodeModeCallResult> InvokeAsync(CodeModeCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        var arguments = document.RootElement;
        if (!arguments.TryGetProperty("question", out var questionElement) || questionElement.ValueKind != JsonValueKind.String
            || questionElement.GetString() is not { Length: > 0 and <= 4000 } question)
        {
            return CodeModeCallResult.Failure(new CodeModeCallError("TypeError", "ask(question, options): the question is a non-empty string of at most 4000 characters."));
        }

        var choices = new List<string>();
        if (arguments.TryGetProperty("choices", out var choicesElement))
        {
            if (choicesElement.ValueKind != JsonValueKind.Array || choicesElement.GetArrayLength() > MaxChoices
                || choicesElement.EnumerateArray().Any(static choice => choice.ValueKind != JsonValueKind.String || choice.GetString() is not { Length: > 0 and <= 120 }))
            {
                return CodeModeCallResult.Failure(new CodeModeCallError("TypeError", $"ask(question, {{ choices }}): choices is an array of at most {MaxChoices} non-empty strings of at most 120 characters."));
            }

            choices.AddRange(choicesElement.EnumerateArray().Select(static choice => choice.GetString()!));
        }

        string? fallback = null;
        if (arguments.TryGetProperty("default", out var defaultElement))
        {
            if (defaultElement.ValueKind != JsonValueKind.String)
            {
                return CodeModeCallResult.Failure(new CodeModeCallError("TypeError", "ask(question, { default }): default is a string."));
            }

            fallback = defaultElement.GetString();
        }

        if (_questions is null)
        {
            return Failure("unavailable", "Nobody can be asked here: ask() waits for an answer in a session of CodeAlta Desktop. Give the value as an argument of the script instead.");
        }

        var answer = await _questions.AskAsync(new CodeModeQuestionRequest(_sessionId, _runId(), question)
        {
            Choices = choices,
            Default = fallback,
            StepId = call.StepId,
        }, cancellationToken).ConfigureAwait(false);
        return answer.Outcome switch
        {
            CodeModeQuestionOutcome.Answered => CodeModeCallResult.Success(Reply(answer.Answer ?? string.Empty)),
            CodeModeQuestionOutcome.Cancelled => Failure("cancelled", answer.Message ?? "The user dismissed the question."),
            _ => Failure("unavailable", answer.Message ?? "Nobody can be asked."),
        };
    }

    private static CodeModeCallResult Failure(string code, string message) => CodeModeCallResult.Failure(new CodeModeCallError("AskError", message) { Code = code });

    private static string Reply(string answer)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("answer", answer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
