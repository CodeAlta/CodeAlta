using CodeAlta.Agent;

namespace CodeAlta.Presentation.Chat;

internal static class ChatPromptResponseBuilder
{
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