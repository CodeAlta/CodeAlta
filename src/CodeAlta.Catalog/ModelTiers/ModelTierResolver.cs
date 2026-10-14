using CodeAlta.Agent;

namespace CodeAlta.Catalog.ModelTiers;

/// <summary>One step of a tier: a target with one of its reasoning efforts, in the order the tier tries them.</summary>
/// <param name="Tier">The tier.</param>
/// <param name="Target">The target.</param>
/// <param name="Reasoning">The reasoning effort, or <see langword="null"/> for the provider's own choice.</param>
public sealed record ModelTierCandidate(ModelTier Tier, ModelTierTarget Target, AgentReasoningEffort? Reasoning)
{
    /// <summary>Gets whether the candidate is a provider, model and reasoning effort.</summary>
    /// <param name="providerKey">The provider key.</param>
    /// <param name="modelId">The model.</param>
    /// <param name="reasoning">The reasoning effort.</param>
    public bool Is(string? providerKey, string? modelId, AgentReasoningEffort? reasoning)
        => Target.IsModel(providerKey, modelId) && (Reasoning is null || reasoning is null || Reasoning == reasoning);

    /// <summary>Formats the candidate as a model reference: <c>provider:model@reasoning</c>.</summary>
    public string Format()
        => Reasoning is { } effort
            ? $"{Target.ProviderKey}:{Target.ModelId}@{CodeAltaConfigStore.FormatReasoningEffort(effort)}"
            : $"{Target.ProviderKey}:{Target.ModelId}";
}

/// <summary>Finds the model a tier gives: its first target whose provider is ready and knows the model.</summary>
public static class ModelTierResolver
{
    /// <summary>Lists the steps of a tier in order: each target, with each of its reasoning efforts in order.</summary>
    /// <param name="tiers">The tiers.</param>
    /// <param name="tier">The tier.</param>
    /// <returns>The steps; empty when the tier has no target.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tiers"/> is null.</exception>
    public static IReadOnlyList<ModelTierCandidate> Candidates(ModelTierSettings tiers, ModelTier tier)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        var candidates = new List<ModelTierCandidate>();
        foreach (var target in tiers.Get(tier))
        {
            if (target.Reasoning.Count == 0)
            {
                candidates.Add(new ModelTierCandidate(tier, target, null));
                continue;
            }

            candidates.AddRange(target.Reasoning.Select(reasoning => new ModelTierCandidate(tier, target, reasoning)));
        }

        return candidates;
    }

    /// <summary>
    /// Gets whether a target can be used now: its provider is configured and ready, and lists the model when it
    /// lists models.
    /// </summary>
    /// <param name="target">The target.</param>
    /// <param name="states">The states of the configured providers.</param>
    /// <returns>Whether the target can be used.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static bool IsReady(ModelTierTarget target, IReadOnlyList<ModelProviderStateSnapshot> states)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(states);
        return FindState(target.ProviderKey, states) is { Availability: ModelProviderAvailability.Ready } state
               && (state.Models.Count == 0 || FindModel(state, target.ModelId) is not null);
    }

    /// <summary>Finds the model a tier gives now.</summary>
    /// <param name="tiers">The tiers.</param>
    /// <param name="tier">The tier.</param>
    /// <param name="states">The states of the configured providers.</param>
    /// <param name="isProviderAllowed">
    /// Says whether a provider may be used, for instance in the space of the session; every provider when null.
    /// </param>
    /// <returns>The first ready target of the tier, or <see langword="null"/> when none is ready.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tiers"/> or <paramref name="states"/> is null.</exception>
    public static ModelTierChoice? Resolve(
        ModelTierSettings tiers,
        ModelTier tier,
        IReadOnlyList<ModelProviderStateSnapshot> states,
        Func<string, bool>? isProviderAllowed = null)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        ArgumentNullException.ThrowIfNull(states);
        foreach (var target in tiers.Get(tier))
        {
            if (isProviderAllowed?.Invoke(target.ProviderKey) == false || !IsReady(target, states))
            {
                continue;
            }

            return CreateChoice(tier, target, PreferredReasoning(target, states), states);
        }

        return null;
    }

    /// <summary>Describes a candidate with the names of its provider and model, for what the user reads.</summary>
    /// <param name="candidate">The candidate.</param>
    /// <param name="states">The states of the configured providers.</param>
    /// <returns>The choice.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ModelTierChoice Describe(ModelTierCandidate candidate, IReadOnlyList<ModelProviderStateSnapshot> states)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(states);
        return CreateChoice(candidate.Tier, candidate.Target, candidate.Reasoning, states);
    }

    /// <summary>
    /// Describes the tiers in one line for the agents: the first target of each tier, in the order of the tiers.
    /// </summary>
    /// <param name="tiers">The tiers.</param>
    /// <returns>The line, or <see langword="null"/> when no tier has a target.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tiers"/> is null.</exception>
    public static string? DescribeForAgents(ModelTierSettings tiers)
        => DescribeForAgents(tiers, isProviderAllowed: null);

    /// <summary>
    /// Describes the tiers in one line for the agents, with only the targets whose provider may be used: the first
    /// target of each tier, in the order of the tiers.
    /// </summary>
    /// <param name="tiers">The tiers.</param>
    /// <param name="isProviderAllowed">Says whether a provider may be used, for instance in the space of the session; every provider when null.</param>
    /// <returns>The line, or <see langword="null"/> when no tier has a target that may be used.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tiers"/> is null.</exception>
    public static string? DescribeForAgents(ModelTierSettings tiers, Func<string, bool>? isProviderAllowed)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        var described = ModelTiers.All
            .Select(tier => (Tier: tier, Targets: tiers.Get(tier).Where(target => isProviderAllowed?.Invoke(target.ProviderKey) != false).ToArray()))
            .Where(static entry => entry.Targets.Length > 0)
            .Select(static entry =>
            {
                var first = entry.Targets[0];
                var text = first.PreferredReasoning is { } effort
                    ? $"{first.ProviderKey}:{first.ModelId}@{CodeAltaConfigStore.FormatReasoningEffort(effort)}"
                    : $"{first.ProviderKey}:{first.ModelId}";
                return entry.Targets.Length > 1
                    ? $"`{ModelTiers.NameOf(entry.Tier)}` ({text}, then {entry.Targets.Length - 1} more)"
                    : $"`{ModelTiers.NameOf(entry.Tier)}` ({text})";
            })
            .ToArray();
        return described.Length == 0 ? null : string.Join(", ", described);
    }

    /// <summary>Finds the model a tier gives now in the space of a scope: its tiers, and its providers only.</summary>
    /// <param name="scope">The tiers of the space.</param>
    /// <param name="tier">The tier.</param>
    /// <param name="states">The states of the configured providers.</param>
    /// <returns>The first ready target of the tier that the space has, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> or <paramref name="states"/> is null.</exception>
    public static ModelTierChoice? Resolve(ModelTierScope scope, ModelTier tier, IReadOnlyList<ModelProviderStateSnapshot> states)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Resolve(scope.Tiers, tier, states, scope.IsProviderAvailable);
    }

    private static ModelTierChoice CreateChoice(
        ModelTier tier,
        ModelTierTarget target,
        AgentReasoningEffort? reasoning,
        IReadOnlyList<ModelProviderStateSnapshot> states)
    {
        var state = FindState(target.ProviderKey, states);
        var model = state is null ? null : FindModel(state, target.ModelId);
        return new ModelTierChoice(
            tier,
            target,
            reasoning,
            state?.Descriptor.DisplayName ?? target.ProviderKey,
            model?.DisplayName ?? target.ModelId);
    }

    // The first reasoning effort of the target that its model supports, when the provider says which it supports.
    private static AgentReasoningEffort? PreferredReasoning(ModelTierTarget target, IReadOnlyList<ModelProviderStateSnapshot> states)
    {
        if (target.Reasoning.Count == 0) return null;
        var supported = FindState(target.ProviderKey, states) is { } state ? FindModel(state, target.ModelId)?.SupportedReasoningEfforts : null;
        return supported is { Count: > 0 }
            ? target.Reasoning.FirstOrDefault(supported.Contains, target.Reasoning[0])
            : target.Reasoning[0];
    }

    private static ModelProviderStateSnapshot? FindState(string providerKey, IReadOnlyList<ModelProviderStateSnapshot> states)
        => states.FirstOrDefault(state => string.Equals(state.ProviderId.Value, providerKey, StringComparison.OrdinalIgnoreCase));

    private static AgentModelInfo? FindModel(ModelProviderStateSnapshot state, string modelId)
        => state.Models.FirstOrDefault(model => string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The model a tier gives: a target with the reasoning effort to use, and the names the user reads.</summary>
/// <param name="Tier">The tier.</param>
/// <param name="Target">The target.</param>
/// <param name="Reasoning">The reasoning effort, or <see langword="null"/> for the provider's own choice.</param>
/// <param name="ProviderDisplayName">The name of the provider.</param>
/// <param name="ModelDisplayName">The name of the model.</param>
public sealed record ModelTierChoice(
    ModelTier Tier,
    ModelTierTarget Target,
    AgentReasoningEffort? Reasoning,
    string ProviderDisplayName,
    string ModelDisplayName)
{
    /// <summary>Gets the provider key.</summary>
    public string ProviderKey => Target.ProviderKey;

    /// <summary>Gets the model.</summary>
    public string ModelId => Target.ModelId;
}
