using CodeAlta.Agent;
using CodeAlta.Catalog.ModelTiers;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Chooses the model a new session starts on when it is asked for a tier rather than a model: the rule of
/// <c>alta session create --tier</c>, shared with the headless driver.
/// </summary>
public static class ModelTierSelection
{
    /// <summary>
    /// Finds the first target of a tier that a new session can start on now: its provider is available in the space of
    /// the scope, configured and enabled, not known to be unable to answer (a provider not probed yet may still answer),
    /// and lists the model when it is ready and lists models.
    /// </summary>
    /// <param name="scope">The tiers of the space of the session and the providers it has.</param>
    /// <param name="tier">The tier asked for.</param>
    /// <param name="states">The states of the configured providers.</param>
    /// <param name="findProvider">Finds the configured provider of a key; null when there is none.</param>
    /// <param name="requestedReasoning">
    /// The reasoning effort asked for, which wins; null for the first effort of the target that its model supports, or its
    /// first effort when the provider does not say which it supports.
    /// </param>
    /// <returns>The target with its reasoning effort, or <see langword="null"/> when no target of the tier can be used now.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/>, <paramref name="states"/> or <paramref name="findProvider"/> is null.</exception>
    public static ModelTierChoice? SelectForNewSession(
        ModelTierScope scope,
        ModelTier tier,
        IReadOnlyList<ModelProviderStateSnapshot> states,
        Func<string, ModelProviderDescriptor?> findProvider,
        AgentReasoningEffort? requestedReasoning)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(findProvider);
        foreach (var target in scope.Tiers.Get(tier))
        {
            if (!scope.IsProviderAvailable(target.ProviderKey)) continue;
            if (findProvider(target.ProviderKey) is not { IsEnabled: true } descriptor) continue;
            var state = states.FirstOrDefault(state => string.Equals(state.ProviderId.Value, target.ProviderKey, StringComparison.OrdinalIgnoreCase));
            // A provider that was not probed yet may still answer; one that is known not to is skipped.
            if (state is not null && state.Availability is not (ModelProviderAvailability.Ready or ModelProviderAvailability.Unknown or ModelProviderAvailability.Probing)) continue;
            var model = state?.Models.FirstOrDefault(model => string.Equals(model.Id, target.ModelId, StringComparison.OrdinalIgnoreCase));
            if (state is { Availability: ModelProviderAvailability.Ready, Models.Count: > 0 } && model is null) continue;
            var reasoning = requestedReasoning
                ?? (model?.SupportedReasoningEfforts is { Count: > 0 } supported && target.Reasoning.Count > 0
                    ? target.Reasoning.FirstOrDefault(supported.Contains, target.Reasoning[0])
                    : target.PreferredReasoning);
            return new ModelTierChoice(tier, target, reasoning, state?.Descriptor.DisplayName ?? descriptor.DisplayName, model?.DisplayName ?? target.ModelId);
        }

        return null;
    }
}
