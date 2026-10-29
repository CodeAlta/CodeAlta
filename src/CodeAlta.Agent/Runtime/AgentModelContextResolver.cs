namespace CodeAlta.Agent.Runtime;

internal static class AgentModelContextResolver
{
    public static AgentModelInfo? Resolve(
        AgentModelInfo? model, string? modelId, string providerKey, long? sessionTokens, long? preferenceTokens)
    {
        if (model is null) return null;

        // Catalog aliases are useful for prices/reasoning, not authority to enlarge a different model's context.
        if (!string.Equals(model.Id, modelId, StringComparison.Ordinal) ||
            model.Provider is not null && !string.Equals(model.Provider, providerKey, StringComparison.Ordinal))
        {
            return model with { ContextSizes = null, EffectiveContextWindowTokens = null };
        }

        var sizes = model.ContextSizes;
        // A provider-owned default means leave its launch alone, not explicitly request its advertised capacity.
        var selected = sizes?.IsProviderManaged == true
            ? sessionTokens is not null && sizes.Supports(sessionTokens) ? sessionTokens
                : preferenceTokens is not null && sizes.Supports(preferenceTokens) ? preferenceTokens : null
            : sizes?.Resolve(sessionTokens, preferenceTokens);
        return model with { EffectiveContextWindowTokens = selected };
    }
}
