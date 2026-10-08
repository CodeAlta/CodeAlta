using CodeAlta.Agent;
using CodeAlta.Catalog.WorkItems;

namespace CodeAlta.Desktop.WorkItems;

/// <summary>What a piece of work says of the provider, the model and the effort of the session started for it.</summary>
/// <param name="Asked">What the user chose when starting the work: it is used as it is, or the work does not start.</param>
/// <param name="Recorded">What the session that proposed the work ran with: it is used when it is still there.</param>
internal sealed record SessionStartModel(WorkItemSelection? Asked = null, WorkItemSelection? Recorded = null);

/// <summary>The provider, the model and the effort a new session starts with, or why none could be chosen.</summary>
/// <param name="Provider">The provider; null when <paramref name="Problem"/> says why.</param>
/// <param name="ModelId">The model; null leaves it to the provider, which the host completes at the first send.</param>
/// <param name="Effort">The reasoning effort; null leaves it to the model.</param>
/// <param name="Problem">Why nothing was chosen; null when something was.</param>
internal sealed record SessionStartChoice(ModelProviderDescriptor? Provider, string? ModelId, AgentReasoningEffort? Effort, string? Problem = null)
{
    /// <summary>
    /// Chooses what a new session starts with, in this order: what the user asked for, what the session that
    /// shows the work runs with, what the session that proposed the work ran with, then the default provider of
    /// the configuration, and the first enabled provider when there is none. What the user asked for is never
    /// replaced: when it is not available, the answer says why. The others are skipped when their provider is no
    /// longer enabled, and keep their provider with its own model when their model is no longer offered.
    /// </summary>
    /// <param name="providers">The enabled providers, in the order they are listed.</param>
    /// <param name="defaultProviderKey">The default provider of the configuration; null when it names none.</param>
    /// <param name="asked">What the user chose; null when nothing.</param>
    /// <param name="like">What the session that shows the work runs with; null when no session shows it.</param>
    /// <param name="recorded">What the session that proposed the work ran with; null when it is not known.</param>
    /// <param name="models">Lists the models of a provider; an empty list when they are not known.</param>
    internal static async Task<SessionStartChoice> ChooseAsync(
        IReadOnlyList<ModelProviderDescriptor> providers,
        string? defaultProviderKey,
        WorkItemSelection? asked,
        WorkItemSelection? like,
        WorkItemSelection? recorded,
        Func<ModelProviderId, Task<IReadOnlyList<AgentModelInfo>>> models)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(models);

        if (asked is not null)
        {
            if (Find(providers, asked.ProviderKey) is not { } provider) return Refuse($"The provider '{asked.ProviderKey}' is not enabled.");
            if (asked.ModelId is null) return new(provider, null, null);
            var effort = ParseEffort(asked.ReasoningEffort);
            if (asked.ReasoningEffort is not null && effort is null) return Refuse($"'{asked.ReasoningEffort}' is not a reasoning effort.");
            var listed = await ListAsync(models, provider).ConfigureAwait(false);
            if (listed.Count == 0) return new(provider, asked.ModelId, effort);
            if (listed.FirstOrDefault(candidate => string.Equals(candidate.Id, asked.ModelId, StringComparison.Ordinal)) is not { } model)
                return Refuse($"The provider '{provider.ProviderId.Value}' does not offer the model '{asked.ModelId}'.");
            if (effort is { } wanted && model.SupportedReasoningEfforts is { Count: > 0 } supported && !supported.Contains(wanted))
                return Refuse($"The model '{model.Id}' has no reasoning effort '{asked.ReasoningEffort}'.");
            return new(provider, model.Id, effort);
        }

        foreach (var candidate in (ReadOnlySpan<WorkItemSelection?>)[like, recorded])
        {
            if (candidate is null || Find(providers, candidate.ProviderKey) is not { } provider) continue;
            if (candidate.ModelId is null) return new(provider, null, null);
            var effort = ParseEffort(candidate.ReasoningEffort);
            var listed = await ListAsync(models, provider).ConfigureAwait(false);
            if (listed.Count == 0) return new(provider, candidate.ModelId, effort);
            if (listed.FirstOrDefault(known => string.Equals(known.Id, candidate.ModelId, StringComparison.Ordinal)) is not { } model) return new(provider, null, null);
            return new(provider, model.Id, effort is { } wanted && model.SupportedReasoningEfforts is { Count: > 0 } supported && !supported.Contains(wanted) ? null : effort);
        }

        return (Find(providers, defaultProviderKey) ?? providers.FirstOrDefault()) is { } fallback ? new(fallback, null, null) : Refuse("No model provider is enabled.");

        static SessionStartChoice Refuse(string problem) => new(null, null, null, problem);
    }

    private static ModelProviderDescriptor? Find(IReadOnlyList<ModelProviderDescriptor> providers, string? key)
        => string.IsNullOrWhiteSpace(key) ? null : providers.FirstOrDefault(provider => string.Equals(provider.ProviderId.Value, key.Trim(), StringComparison.OrdinalIgnoreCase));

    private static AgentReasoningEffort? ParseEffort(string? effort)
        => !string.IsNullOrWhiteSpace(effort) && Enum.TryParse<AgentReasoningEffort>(effort, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !char.IsAsciiDigit(effort.Trim()[0]) ? parsed : null;

    // A provider that cannot list its models now says nothing of them: what was chosen is kept.
    private static async Task<IReadOnlyList<AgentModelInfo>> ListAsync(Func<ModelProviderId, Task<IReadOnlyList<AgentModelInfo>>> models, ModelProviderDescriptor provider)
    {
        try
        {
            return await models(provider.ProviderId).ConfigureAwait(false) ?? [];
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or HttpRequestException or NotSupportedException)
        {
            return [];
        }
    }
}
