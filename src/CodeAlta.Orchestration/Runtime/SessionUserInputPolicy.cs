using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Selects immediate user-input answers from a trusted request and captured auto-approval setting.
/// </summary>
/// <remarks>
/// This policy does not present questions, own pending interactions, or authorize renderer requests.
/// </remarks>
public static class SessionUserInputPolicy
{
    /// <summary>
    /// Creates an ordinal answer map using the existing immediate-response heuristics.
    /// </summary>
    /// <param name="request">The structured user-input request.</param>
    /// <param name="autoApprove">Whether to select preferred options or nonsecret freeform defaults;
    /// otherwise every answer is empty.</param>
    /// <returns>Answers keyed by the literal prompt identifiers. Selected option labels are returned literally.</returns>
    /// <remarks>
    /// Options take precedence over secret/freeform flags, and the first highest-scoring option wins.
    /// Without options, secret or nonfreeform prompts receive empty answers. Input records are not mutated.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/>, its prompt collection, or a prompt identifier is null;
    /// or an option label is null when auto-approval is enabled.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Prompt identifiers are duplicated under ordinal comparison, or an option label is empty or whitespace
    /// when auto-approval is enabled.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// The request form or a prompt is null, or an option is null when auto-approval is enabled.
    /// </exception>
    public static AgentUserInputResponse CreateResponse(AgentUserInputRequest request, bool autoApprove)
    {
        ArgumentNullException.ThrowIfNull(request);

        var answers = request.Form.Prompts.ToDictionary(
            static prompt => prompt.Id,
            prompt => ResolvePromptAnswer(prompt, autoApprove),
            StringComparer.Ordinal);

        return new AgentUserInputResponse(answers);
    }

    private static string ResolvePromptAnswer(AgentUserInputPrompt prompt, bool autoApprove)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        if (!autoApprove)
        {
            return string.Empty;
        }

        if (prompt.Options is { Count: > 0 } options)
        {
            return SelectPreferredPromptOption(options, prompt.Question);
        }

        if (prompt.IsSecret)
        {
            return string.Empty;
        }

        return prompt.AllowFreeform
            ? "No preference. Use your best judgment and continue."
            : string.Empty;
    }

    private static string SelectPreferredPromptOption(IReadOnlyList<AgentUserInputOption> options, string? question)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Count == 0)
        {
            return string.Empty;
        }

        var bestIndex = 0;
        var bestScore = int.MinValue;
        for (var index = 0; index < options.Count; index++)
        {
            var score = ScorePromptOption(options[index].Label, question);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = index;
            }
        }

        return options[bestIndex].Label;
    }

    private static int ScorePromptOption(string label, string? question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var normalizedLabel = label.Trim().ToLowerInvariant();
        var normalizedQuestion = question?.Trim().ToLowerInvariant() ?? string.Empty;
        var score = 0;

        score += ScoreOptionKeywords(
            normalizedLabel,
            "yes",
            "allow",
            "approve",
            "continue",
            "proceed",
            "go ahead",
            "run",
            "use",
            "look",
            "inspect",
            "search",
            "list",
            "read",
            "open",
            "explore",
            "summarize");

        score -= ScoreOptionKeywords(
            normalizedLabel,
            "no",
            "deny",
            "reject",
            "cancel",
            "abort",
            "stop",
            "don't",
            "do not",
            "never",
            "skip",
            "later",
            "different path",
            "specify a different path",
            "provide instructions",
            "inspect locally");

        if (normalizedQuestion.Contains("which option", StringComparison.Ordinal) ||
            normalizedQuestion.Contains("how should i proceed", StringComparison.Ordinal) ||
            normalizedQuestion.Contains("do you want me to", StringComparison.Ordinal))
        {
            score += ScoreOptionKeywords(normalizedLabel, "continue", "proceed", "look", "inspect", "search", "list", "use", "run");
            score -= ScoreOptionKeywords(normalizedLabel, "provide instructions", "different path", "stop", "cancel");
        }

        return score;
    }

    private static int ScoreOptionKeywords(string value, params string[] keywords)
    {
        var score = 0;
        foreach (var keyword in keywords)
        {
            if (value.Contains(keyword, StringComparison.Ordinal))
            {
                score += 10;
            }
        }

        return score;
    }
}
