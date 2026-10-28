namespace CodeAlta.Agent.ModelCatalog;

/// <summary>
/// A limit a provider knows better than models.dev for a family of its models: the size of the context a session
/// should use, which can be smaller than the one the model accepts (a price that rises past it), and the share of it
/// at which a session compacts. A provider gives its table to <see cref="AgentModelMetadataEnricher"/>, which applies
/// it over models.dev and under the overrides of the user.
/// </summary>
/// <param name="ModelIdPrefix">
/// The start of the identifiers of the models, matched without case up to a <c>-</c> or a <c>.</c>:
/// <c>gpt-6</c> is <c>gpt-6-astra</c> and <c>gpt-6.1-sol</c>, not <c>gpt-60</c>.
/// </param>
/// <param name="InputTokenLimit">The most input tokens a session sends; a larger limit of the model is lowered to it.</param>
/// <param name="ContextWindowTokens">The input-plus-output envelope that goes with it; a larger one is lowered to it.</param>
/// <param name="CompactionRatio">The share of the input limit at which a session compacts, at most.</param>
internal sealed record AgentModelKnownLimit(
    string ModelIdPrefix,
    long InputTokenLimit,
    long ContextWindowTokens,
    double CompactionRatio)
{
    /// <summary>Whether the limit is of the model with this identifier.</summary>
    public bool Matches(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) ||
            !modelId.StartsWith(ModelIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return modelId.Length == ModelIdPrefix.Length || modelId[ModelIdPrefix.Length] is '-' or '.';
    }

    /// <summary>The first limit of the table that is of the model, or null.</summary>
    public static AgentModelKnownLimit? Find(IReadOnlyList<AgentModelKnownLimit>? limits, string? modelId)
    {
        if (limits is null)
        {
            return null;
        }

        foreach (var limit in limits)
        {
            if (limit.Matches(modelId))
            {
                return limit;
            }
        }

        return null;
    }
}
