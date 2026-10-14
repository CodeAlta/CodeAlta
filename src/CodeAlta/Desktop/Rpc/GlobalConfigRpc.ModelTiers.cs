using CodeAlta.Agent;
using CodeAlta.Catalog.ModelTiers;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class GlobalConfigService
{
    private const int MaximumTierTargets = 16;

    /// <summary>Gets the states of the configured providers, which say what each tier gives now; none when null.</summary>
    internal Func<IReadOnlyList<ModelProviderStateSnapshot>>? ProviderStates { get; init; }

    /// <summary>Reads the tiers of models, the model each gives now, and the fallback: the <c>[models]</c> table.</summary>
    [NeoRpcMethod("modelTiers")]
    public GlobalConfigModelTiersResponse ModelTiers(GlobalConfigModelTiersRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store is null) return new("unavailable", [], null, null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", [], null, null);
        ModelSettings settings;
        try
        {
            lock (_gate) settings = _store.LoadGlobalModelSettings();
        }
        catch (InvalidDataException exception)
        {
            // Tiers the file writes wrong are shown as none, with what is wrong: saving here writes them again.
            return new("invalid", ToItems(ModelTierSettings.Empty), ToItem(ModelFallbackSettings.Default), Bound(exception.Message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("read_failed", [], null, null);
        }

        return new("ok", ToItems(settings.Tiers), ToItem(settings.Fallback), null);
    }

    /// <summary>Saves the tiers of models and the fallback. A target that is not one is refused with what is wrong.</summary>
    [NeoRpcMethod("saveModelTiers")]
    public GlobalConfigModelTiersResponse SaveModelTiers(GlobalConfigSaveModelTiersRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store is null) return new("unavailable", [], null, null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", [], null, null);
        if (request.Tiers is null || request.Fallback is null || request.Tiers.Count > CodeAlta.Catalog.ModelTiers.ModelTiers.All.Count)
            return new("invalid", [], null, "The tiers are not complete.");
        var document = new CodeAlta.Catalog.CodeAltaModelTiersDocument();
        foreach (var list in request.Tiers)
        {
            if (list?.Targets is null || CodeAlta.Catalog.ModelTiers.ModelTiers.Parse(list.Tier) is not { } tier || list.Targets.Count > MaximumTierTargets)
                return new("invalid", [], null, "The tiers are not complete.");
            var written = new List<string>();
            foreach (var target in list.Targets)
            {
                if (target is null || string.IsNullOrWhiteSpace(target.ProviderKey) || target.ProviderKey.Length > 256
                    || string.IsNullOrWhiteSpace(target.ModelId) || target.ModelId.Length > 256 || target.Reasoning is null || target.Reasoning.Count > 8)
                    return new("invalid", [], null, "A target of a tier names no provider or no model.");
                var efforts = new List<string>();
                foreach (var effort in target.Reasoning)
                {
                    if (!Enum.TryParse<AgentReasoningEffort>(effort, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                        return new("invalid", [], null, $"'{effort}' is not a reasoning effort.");
                    efforts.Add(parsed.ToString().ToLowerInvariant());
                }

                written.Add(efforts.Count == 0 ? $"{target.ProviderKey.Trim()}:{target.ModelId.Trim()}" : $"{target.ProviderKey.Trim()}:{target.ModelId.Trim()}@{string.Join(',', efforts)}");
            }

            switch (tier)
            {
                case ModelTier.Complex: document.Complex = written; break;
                case ModelTier.Standard: document.Standard = written; break;
                default: document.Quick = written; break;
            }
        }

        ModelSettings settings;
        try
        {
            settings = ModelSettings.Read(new CodeAlta.Catalog.CodeAltaModelSettingsDocument
            {
                Tiers = document,
                Fallback = new CodeAlta.Catalog.CodeAltaModelFallbackDocument
                {
                    Enabled = request.Fallback.Enabled,
                    On = [.. Triggers(request.Fallback)],
                },
            });
        }
        catch (InvalidDataException exception)
        {
            return new("invalid", [], null, Bound(exception.Message));
        }

        lock (_gate)
        {
            try
            {
                _store.SaveGlobalModelSettings(settings);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or FormatException)
            {
                return new("write_failed", [], null, null);
            }
        }

        return new("ok", ToItems(settings.Tiers), ToItem(settings.Fallback), null);

        static IEnumerable<string> Triggers(ModelFallbackSetting fallback)
        {
            if (fallback.OnQuota) yield return ModelFallbackSettings.QuotaTrigger;
            if (fallback.OnUnavailable) yield return ModelFallbackSettings.UnavailableTrigger;
        }
    }

    private IReadOnlyList<ModelTierList> ToItems(ModelTierSettings tiers)
    {
        var states = ProviderStates?.Invoke() ?? [];
        return [.. CodeAlta.Catalog.ModelTiers.ModelTiers.All.Select(tier => new ModelTierList(
            CodeAlta.Catalog.ModelTiers.ModelTiers.NameOf(tier),
            [.. tiers.Get(tier).Select(static target => new ModelTierTargetItem(target.ProviderKey, target.ModelId, [.. target.Reasoning.Select(static effort => effort.ToString())]))])
        {
            Choice = ModelTierResolver.Resolve(tiers, tier, states) is { } choice
                ? new ModelTierChoiceItem(choice.ProviderKey, choice.ModelId, choice.Reasoning?.ToString(), choice.ProviderDisplayName, choice.ModelDisplayName)
                : null,
        })];
    }

    private static ModelFallbackSetting ToItem(ModelFallbackSettings fallback)
        => new(fallback.Enabled, fallback.OnQuota, fallback.OnUnavailable);
}

/// <summary>Asks for the tiers of models.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
internal sealed record GlobalConfigModelTiersRequest(string ExpectedEpoch);

/// <summary>Saves the tiers of models and the fallback.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="Tiers">Each tier with its targets in order.</param>
/// <param name="Fallback">When a session goes on with the next target of its tier.</param>
internal sealed record GlobalConfigSaveModelTiersRequest(string ExpectedEpoch, IReadOnlyList<ModelTierList> Tiers, ModelFallbackSetting Fallback);

/// <summary>
/// <c>ok</c> with the tiers; <c>invalid</c> with what is wrong (a save that was refused, or tiers the file writes
/// wrong, then shown as none); <c>unavailable</c>, <c>stale_epoch</c>, <c>read_failed</c> or <c>write_failed</c>.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Tiers">Complex, standard and quick, in this order.</param>
/// <param name="Fallback">The fallback, when the tiers were read.</param>
/// <param name="Message">What is wrong, for <c>invalid</c>.</param>
internal sealed record GlobalConfigModelTiersResponse(string Status, IReadOnlyList<ModelTierList> Tiers, ModelFallbackSetting? Fallback, string? Message);

/// <summary>A tier of models: its targets in order of preference, and the model it gives now.</summary>
/// <param name="Tier"><c>complex</c>, <c>standard</c> or <c>quick</c>.</param>
/// <param name="Targets">The targets in order.</param>
internal sealed record ModelTierList(string Tier, IReadOnlyList<ModelTierTargetItem> Targets)
{
    /// <summary>Gets the first target that is ready now, or null when none is (or for a request).</summary>
    public ModelTierChoiceItem? Choice { get; init; }
}

/// <summary>A target of a tier: a model of a configured provider with its reasoning efforts in order.</summary>
/// <param name="ProviderKey">The key of the provider.</param>
/// <param name="ModelId">The model.</param>
/// <param name="Reasoning">The reasoning efforts in order (Low, Medium, High...); empty for the provider's own.</param>
internal sealed record ModelTierTargetItem(string ProviderKey, string ModelId, IReadOnlyList<string> Reasoning);

/// <summary>The model a tier gives now.</summary>
/// <param name="ProviderKey">The key of the provider.</param>
/// <param name="ModelId">The model.</param>
/// <param name="Reasoning">The reasoning effort, or null for the provider's own.</param>
/// <param name="ProviderName">The name of the provider.</param>
/// <param name="ModelName">The name of the model.</param>
internal sealed record ModelTierChoiceItem(string ProviderKey, string ModelId, string? Reasoning, string ProviderName, string ModelName);

/// <summary>When a session goes on with the next target of its tier.</summary>
/// <param name="Enabled">Whether the fallback is on.</param>
/// <param name="OnQuota">On an exhausted quota or a long rate limit.</param>
/// <param name="OnUnavailable">On a provider that stays unavailable.</param>
internal sealed record ModelFallbackSetting(bool Enabled, bool OnQuota, bool OnUnavailable);
