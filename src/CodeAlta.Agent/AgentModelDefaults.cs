namespace CodeAlta.Agent;

/// <summary>
/// Picks the model and the reasoning effort a session starts with among what its provider reports.
/// </summary>
public static class AgentModelDefaults
{
    /// <summary>
    /// Resolves the model a session starts with: the preferred model when the provider lists it, otherwise the
    /// first model the provider lists.
    /// </summary>
    /// <param name="models">The models the provider reports.</param>
    /// <param name="preferredModelId">The preferred model identifier, such as the provider's configured default.</param>
    /// <returns>A listed model identifier, or <see langword="null"/> when the provider lists no model.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="models"/> is null.</exception>
    public static string? ResolveModelId(IReadOnlyList<AgentModelInfo> models, string? preferredModelId)
    {
        ArgumentNullException.ThrowIfNull(models);

        if (models.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredModelId) &&
            models.Any(model => string.Equals(model.Id, preferredModelId, StringComparison.Ordinal)))
        {
            return preferredModelId;
        }

        return models[0].Id;
    }

    /// <summary>
    /// Resolves the reasoning effort a model starts with: the preferred effort when the model supports it or does
    /// not report what it supports, then <see cref="AgentReasoningEffort.High"/>, then the model's own default,
    /// then the first effort the model supports.
    /// </summary>
    /// <param name="model">The selected model, or <see langword="null"/> when it is not known.</param>
    /// <param name="preferredReasoningEffort">The preferred reasoning effort.</param>
    /// <returns>
    /// The reasoning effort to use, or <see langword="null"/> when nothing is preferred and the model reports none.
    /// </returns>
    public static AgentReasoningEffort? ResolveReasoningEffort(AgentModelInfo? model, AgentReasoningEffort? preferredReasoningEffort)
    {
        var supportedReasoningEfforts = model?.SupportedReasoningEfforts?
            .Distinct()
            .ToArray();
        if (preferredReasoningEffort is { } requestedEffort &&
            (supportedReasoningEfforts is null || supportedReasoningEfforts.Contains(requestedEffort)))
        {
            return requestedEffort;
        }

        if (supportedReasoningEfforts is { Length: > 0 })
        {
            if (supportedReasoningEfforts.Contains(AgentReasoningEffort.High))
            {
                return AgentReasoningEffort.High;
            }

            if (model?.DefaultReasoningEffort is { } defaultEffort && supportedReasoningEfforts.Contains(defaultEffort))
            {
                return defaultEffort;
            }

            return supportedReasoningEfforts[0];
        }

        return model?.DefaultReasoningEffort;
    }
}
