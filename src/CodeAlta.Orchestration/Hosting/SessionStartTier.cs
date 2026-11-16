using CodeAlta.Agent;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Hosting;

/// <summary>
/// A tier of models or Adaptive at a level, as a start of work names it: what a new session started for a work item, an
/// issue, a script or an automation runs on in place of a model chosen by hand.
/// </summary>
/// <param name="Tier">The tier; null on Adaptive.</param>
/// <param name="Adaptive">Whether CodeAlta chooses the model of each turn of the session.</param>
/// <param name="Level">The level of Adaptive; null for its default level, and off Adaptive.</param>
public sealed record SessionStartTier(ModelTier? Tier, bool Adaptive, AdaptiveLevel? Level)
{
    /// <summary>
    /// Reads a tier as the composer names it: <c>complex</c>, <c>standard</c> or <c>quick</c>, <c>adaptive</c> for
    /// Adaptive at its default level, or <c>adaptive:&lt;level&gt;</c>.
    /// </summary>
    /// <param name="value">The name.</param>
    /// <returns>The tier, or <see langword="null"/> when the name is none of them.</returns>
    public static SessionStartTier? Parse(string? value)
    {
        var text = value?.Trim().ToLowerInvariant();
        if (text is null) return null;
        if (text == "adaptive") return new(null, true, null);
        if (text.StartsWith("adaptive:", StringComparison.Ordinal))
            return AdaptiveLevels.Parse(text["adaptive:".Length..]) is { } level ? new(null, true, level) : null;
        return ModelTiers.Parse(text) is { } tier ? new(tier, false, null) : null;
    }

    /// <summary>Gets the name of the tier as <see cref="Parse"/> reads it.</summary>
    public string Name => Adaptive
        ? Level is { } level ? "adaptive:" + AdaptiveLevels.NameOf(level) : "adaptive"
        : ModelTiers.NameOf(Tier!.Value);

    /// <summary>
    /// Finds the model a new session on this tier starts with: the first ready one of the tier, or the one of the level
    /// of Adaptive, in the tiers of the space, as <c>alta session create --tier</c> and the composer take it.
    /// </summary>
    /// <param name="host">The host whose tiers and providers are read.</param>
    /// <param name="spaceId">The space of the session; null for the default one.</param>
    /// <param name="projectId">The project of the session; null for a chat.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>The model, or why the tier gives none now.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
    public async Task<(SessionStartTierModel? Model, string? Problem)> ResolveModelAsync(CodeAltaHost host, string? spaceId, string? projectId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        var scope = await host.RuntimeService.LoadModelTierScopeAsync(spaceId, projectId, cancellationToken).ConfigureAwait(false);
        if (Adaptive)
        {
            return scope.IsAdaptiveReady && scope.Adaptive!.Resolve(Level) is { } level
                ? (new(level.Model.ProviderKey, level.Model.ModelId, level.Model.PreferredReasoning), null)
                : (null, "Adaptive is not set up: give its levels a model in Settings > Models.");
        }

        var name = ModelTiers.NameOf(Tier!.Value);
        var targets = scope.Tiers.Get(Tier.Value);
        if (targets.Count == 0) return (null, $"The {name} tier has no model: set the tiers in Settings > Models.");
        return ModelTierSelection.SelectForNewSession(scope, Tier.Value, host.ModelProviderInitializationService.CurrentStates,
                key => host.ModelProviderRegistry.TryGetProvider(new ModelProviderId(key), out var descriptor) ? descriptor : null, requestedReasoning: null) is { } choice
            ? (new(choice.ProviderKey, choice.ModelId, choice.Reasoning), null)
            : (null, $"No model of the {name} tier is ready ({string.Join(", ", targets.Select(static target => target.Format()))}).");
    }

    /// <summary>Puts a new session on this tier, or on Adaptive, before its first prompt, which then runs on it.</summary>
    /// <param name="host">The host of the session.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns><see langword="null"/> when the session is on it, else what the host answered, such as <c>not_ready</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    public async Task<string?> PutSessionAsync(CodeAltaHost host, string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        var status = Adaptive
            ? (await host.Commands.SetSessionAdaptiveAsync(sessionId, Level, cancellationToken).ConfigureAwait(false)).Status
            : await host.Commands.SetSessionTierAsync(sessionId, Tier, cancellationToken).ConfigureAwait(false);
        return status == "ok" ? null : status;
    }
}

/// <summary>The model a tier gives a new session.</summary>
/// <param name="ProviderKey">Its provider.</param>
/// <param name="ModelId">The model.</param>
/// <param name="Effort">Its reasoning effort; null for the provider's own choice.</param>
public sealed record SessionStartTierModel(string ProviderKey, string ModelId, AgentReasoningEffort? Effort);
