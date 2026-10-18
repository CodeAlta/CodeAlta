using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAlta.Agent.TypeSafe;

/// <summary>
/// Checks the questions of a System One request before it is sent, with the rules of the API: a question that
/// breaks one would be refused by the service, and charged for nothing.
/// </summary>
public static class TypeSafeQuestions
{
    /// <summary>The most options of one <c>choice</c> question.</summary>
    public const int MaximumChoiceOptions = 255;

    /// <summary>The fewest levels of one <c>score</c> question.</summary>
    public const int MinimumScoreLevels = 2;

    /// <summary>The most levels of one <c>score</c> question.</summary>
    public const int MaximumScoreLevels = 10;

    /// <summary>
    /// Checks a state and its questions.
    /// </summary>
    /// <param name="state">What the questions are asked about: a text, an object or an array.</param>
    /// <param name="questions">The questions by the id their answers come back under.</param>
    /// <param name="error">What is wrong, naming the question and the field; <see langword="null"/> when nothing is.</param>
    /// <returns><see langword="true"/> when the request can be sent.</returns>
    public static bool TryValidate(JsonNode? state, JsonNode? questions, out string? error)
    {
        error = ValidateState(state) ?? ValidateQuestions(questions);
        return error is null;
    }

    private static string? ValidateState(JsonNode? state) => state switch
    {
        null => "`state` is required: the text, object or array the questions are asked about.",
        JsonValue value when value.GetValueKind() == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetValue<string>()) => "`state` is empty.",
        JsonValue value when value.GetValueKind() is not JsonValueKind.String => "`state` is a text, an object or an array.",
        _ => null,
    };

    private static string? ValidateQuestions(JsonNode? questions)
    {
        if (questions is not JsonObject byId || byId.Count == 0)
        {
            return "`questions` is required: an object of at least one question, by the id its answer comes back under.";
        }

        foreach (var (id, node) in byId)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return "A question id is empty.";
            }

            if (ValidateQuestion(id, node) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    private static string? ValidateQuestion(string id, JsonNode? node)
    {
        if (node is not JsonObject question)
        {
            return $"Question `{id}` is an object with `type`, `instructions` and, for some types, `criteria`.";
        }

        foreach (var (name, _) in question)
        {
            if (name is not ("type" or "instructions" or "criteria"))
            {
                return $"Question `{id}` has an unknown field `{name}`: a question has `type`, `instructions` and `criteria`.";
            }
        }

        if (IsBlank(question["instructions"]))
        {
            return $"Question `{id}` needs `instructions`: what to judge, as a text, an object or an array.";
        }

        var type = question["type"] is JsonValue typeValue && typeValue.GetValueKind() == JsonValueKind.String ? typeValue.GetValue<string>() : null;
        var criteria = question["criteria"];
        return type switch
        {
            "noul" => criteria switch
            {
                null => null,
                JsonObject meanings when meanings.All(static entry => entry.Key is "true" or "false") => null,
                _ => $"Question `{id}`: the `criteria` of a noul is an object with `true` and `false`, what each answer means.",
            },
            "choice" => criteria is JsonObject options && options.Count is >= 1 and <= MaximumChoiceOptions && options.All(static entry => !string.IsNullOrWhiteSpace(entry.Key))
                ? null
                : $"Question `{id}`: the `criteria` of a choice is an object of 1 to {MaximumChoiceOptions} options, each with its description or null.",
            "score" => criteria is JsonArray levels && levels.Count is >= MinimumScoreLevels and <= MaximumScoreLevels && !levels.Any(IsBlank)
                ? null
                : $"Question `{id}`: the `criteria` of a score is an array of {MinimumScoreLevels} to {MaximumScoreLevels} levels, the lowest first.",
            _ => $"Question `{id}` has the type `{type ?? "(none)"}`: use `noul` (yes or no), `choice` (one of options) or `score` (a level of a scale).",
        };
    }

    private static bool IsBlank(JsonNode? node) => node switch
    {
        null => true,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetValue<string>()),
        JsonValue value => value.GetValueKind() == JsonValueKind.Null,
        JsonObject value => value.Count == 0,
        JsonArray value => value.Count == 0,
        _ => false,
    };
}
