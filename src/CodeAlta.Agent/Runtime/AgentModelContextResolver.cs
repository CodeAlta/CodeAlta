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

        return model with { EffectiveContextWindowTokens = model.ContextSizes?.Resolve(sessionTokens, preferenceTokens) };
    }
}
