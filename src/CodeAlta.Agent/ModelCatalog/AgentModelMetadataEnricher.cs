using System.Diagnostics.CodeAnalysis;
using CodeAlta.Agent.Runtime.Compaction;

namespace CodeAlta.Agent.ModelCatalog;

internal static class AgentModelMetadataEnricher
{
    /// <summary>The price of a million input tokens, in US dollars.</summary>
    public const string InputCostKey = "inputCostPerMillionTokens";

    /// <summary>The price of a million output tokens, in US dollars.</summary>
    public const string OutputCostKey = "outputCostPerMillionTokens";

    /// <summary>The price of a million input tokens read from the prompt cache, in US dollars.</summary>
    public const string CacheReadCostKey = "cacheReadCostPerMillionTokens";

    /// <summary>The price of a million input tokens written to the prompt cache, in US dollars.</summary>
    public const string CacheWriteCostKey = "cacheWriteCostPerMillionTokens";

    /// <summary>The models.dev provider whose price the model has.</summary>
    public const string CostProviderKey = "costModelsDevProviderId";

    public static IReadOnlyList<AgentModelInfo> EnrichModels(
        IReadOnlyList<AgentModelInfo> models,
        ModelsDevCatalogService? catalog,
        string? modelsDevProviderId,
        IReadOnlyDictionary<string, AgentModelOverride>? overrides,
        string? modelsIncludeRegex = null,
        IReadOnlyList<AgentModelKnownLimit>? knownLimits = null)
    {
        var filteredModels = AgentModelCatalogFilter.ApplyIncludeRegex(models, modelsIncludeRegex);
        if (filteredModels.Count == 0)
        {
            return filteredModels;
        }

        return filteredModels
            .Select(model => EnrichModel(model, catalog, modelsDevProviderId, overrides, knownLimits))
            .ToArray();
    }

    public static AgentModelInfo EnrichModel(
        AgentModelInfo model,
        ModelsDevCatalogService? catalog,
        string? modelsDevProviderId,
        IReadOnlyDictionary<string, AgentModelOverride>? overrides,
        IReadOnlyList<AgentModelKnownLimit>? knownLimits = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        var displayName = model.DisplayName;
        var description = model.Description;
        var efforts = model.SupportedReasoningEfforts;
        var capabilities = model.Capabilities is null
            ? null
            : new Dictionary<string, object?>(model.Capabilities, StringComparer.Ordinal);

        if (catalog is not null && TryFindModelsDevModel(catalog, modelsDevProviderId, model.Id, out var foundProviderId, out var modelsDevModel))
        {
            capabilities ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            ApplyModelsDevMetadata(capabilities, foundProviderId, modelsDevModel);
            ApplyModelsDevPrice(capabilities, catalog, foundProviderId, modelsDevModel, model.Id);

            // A provider that lists no effort of the model (the Models API of OpenAI, a compatible endpoint) is offered
            // the efforts models.dev gives it. The list is not said to be complete: it comes from a catalog. A provider
            // that says its list is complete has none for the model (Haiku of Claude Code).
            if (efforts is null && !AgentReasoningEffortCapability.IsListComplete(model) && ReadModelsDevEfforts(modelsDevModel) is { } listed)
            {
                efforts = listed.Efforts;
                SetIfMissing(capabilities, AgentReasoningEffortCapability.HighestEffortKey, listed.HighestEffort);
            }

            if (string.IsNullOrWhiteSpace(displayName) ||
                string.Equals(displayName, model.Id, StringComparison.Ordinal))
            {
                displayName = modelsDevModel.Name ?? displayName;
            }
        }

        // What the provider knows better than models.dev, under what the user set.
        if (AgentModelKnownLimit.Find(knownLimits, model.Id) is { } knownLimit)
        {
            capabilities ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            ApplyKnownLimit(capabilities, knownLimit);
        }

        if (TryGetOverride(overrides, model, out var modelOverride) && modelOverride is not null)
        {
            capabilities ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            ApplyOverride(capabilities, modelOverride);

            if (!string.IsNullOrWhiteSpace(modelOverride.DisplayName))
            {
                displayName = modelOverride.DisplayName;
            }

            if (!string.IsNullOrWhiteSpace(modelOverride.Description))
            {
                description = modelOverride.Description;
            }
        }

        if (capabilities is null &&
            string.Equals(displayName, model.DisplayName, StringComparison.Ordinal) &&
            string.Equals(description, model.Description, StringComparison.Ordinal))
        {
            return model;
        }

        return new AgentModelInfo(
            model.Id,
            DisplayName: displayName,
            Description: description,
            Provider: model.Provider,
            DefaultReasoningEffort: model.DefaultReasoningEffort,
            SupportedReasoningEfforts: efforts,
            Capabilities: capabilities ?? model.Capabilities)
        {
            Options = model.Options,
        };
    }

    // The effort levels of models.dev ({"type":"effort","values":["none","low","medium","high","xhigh","max"]}), as a
    // session names them: XHigh is the highest of xhigh and max, sent as the one the model has; Max is an effort of its
    // own only where the model has both. "none" is no reasoning, which a session asks with no effort. Null when
    // models.dev gives no level (a model without reasoning, or one controlled by a toggle or a token budget).
    internal static (IReadOnlyList<AgentReasoningEffort> Efforts, string? HighestEffort)? ReadModelsDevEfforts(ModelsDevModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var levels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in model.ReasoningOptions ?? [])
        {
            if (option is not null && string.Equals(option.Type, ModelsDevReasoningOptionDefinition.EffortType, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var value in option.Values ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        levels.Add(value.Trim().ToLowerInvariant());
                    }
                }
            }
        }

        var efforts = new List<AgentReasoningEffort>();
        if (levels.Contains("minimal")) efforts.Add(AgentReasoningEffort.Minimal);
        if (levels.Contains("low")) efforts.Add(AgentReasoningEffort.Low);
        if (levels.Contains("medium")) efforts.Add(AgentReasoningEffort.Medium);
        if (levels.Contains("high")) efforts.Add(AgentReasoningEffort.High);
        if (levels.Contains("xhigh") || levels.Contains("max")) efforts.Add(AgentReasoningEffort.XHigh);
        if (levels.Contains("xhigh") && levels.Contains("max")) efforts.Add(AgentReasoningEffort.Max);
        if (efforts.Count == 0)
        {
            return null;
        }

        return (efforts, levels.Contains("xhigh") ? "xhigh" : levels.Contains("max") ? "max" : null);
    }

    private static bool TryGetOverride(
        IReadOnlyDictionary<string, AgentModelOverride>? overrides,
        AgentModelInfo model,
        out AgentModelOverride? modelOverride)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (overrides is null || overrides.Count == 0)
        {
            modelOverride = null;
            return false;
        }

        if (overrides.TryGetValue(model.Id, out modelOverride))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(model.DisplayName) &&
            overrides.TryGetValue(model.DisplayName, out modelOverride))
        {
            return true;
        }

        var lookupKeys = AgentModelIdentity.GetLookupKeySet(model.Id, model.DisplayName);
        foreach (var entry in overrides)
        {
            if (AgentModelIdentity.Matches(entry.Key, lookupKeys))
            {
                modelOverride = entry.Value;
                return true;
            }
        }

        modelOverride = null;
        return false;
    }

    // The model as its provider lists it on models.dev, else as its vendor does (a model GitHub Copilot or a coding
    // plan resells, or that a provider not on models.dev serves).
    private static bool TryFindModelsDevModel(
        ModelsDevCatalogService catalog,
        string? modelsDevProviderId,
        string modelId,
        [NotNullWhen(true)] out string? foundProviderId,
        [NotNullWhen(true)] out ModelsDevModelDefinition? model)
    {
        if (!string.IsNullOrWhiteSpace(modelsDevProviderId) && catalog.TryGetModel(modelsDevProviderId, modelId, out model))
        {
            foundProviderId = modelsDevProviderId;
            return true;
        }

        var vendorProviderId = ModelsDevVendors.Find(modelId);
        if (vendorProviderId is not null &&
            !string.Equals(vendorProviderId, modelsDevProviderId, StringComparison.OrdinalIgnoreCase) &&
            catalog.TryGetModel(vendorProviderId, modelId, out model))
        {
            foundProviderId = vendorProviderId;
            return true;
        }

        foundProviderId = null;
        model = null;
        return false;
    }

    // The price of the model per million tokens: the one of its provider, else the one of its vendor when its
    // provider gives none (a subscription, a coding plan, GitHub Copilot), so that what it would cost is known.
    private static void ApplyModelsDevPrice(
        IDictionary<string, object?> capabilities,
        ModelsDevCatalogService catalog,
        string foundProviderId,
        ModelsDevModelDefinition model,
        string modelId)
    {
        var cost = model.Cost;
        var costProviderId = foundProviderId;
        // A plan may name a model its own way (`k3` for Kimi K3): its family and its name tell the vendor's.
        if (!HasPrice(cost) &&
            (ModelsDevVendors.Find(modelId) ?? ModelsDevVendors.Find(model.Family) ?? ModelsDevVendors.Find(model.Name)) is { } vendorProviderId &&
            !string.Equals(vendorProviderId, foundProviderId, StringComparison.OrdinalIgnoreCase) &&
            (catalog.TryGetModel(vendorProviderId, modelId, out var vendorModel) ||
             (!string.IsNullOrWhiteSpace(model.Name) && catalog.TryGetModel(vendorProviderId, model.Name, out vendorModel))) &&
            HasPrice(vendorModel.Cost))
        {
            cost = vendorModel.Cost;
            costProviderId = vendorProviderId;
        }

        if (!HasPrice(cost))
        {
            return;
        }

        SetIfMissing(capabilities, InputCostKey, cost!.Input);
        SetIfMissing(capabilities, OutputCostKey, cost.Output);
        SetIfMissing(capabilities, CacheReadCostKey, cost.CacheRead);
        SetIfMissing(capabilities, CacheWriteCostKey, cost.CacheWrite);
        SetIfMissing(capabilities, CostProviderKey, costProviderId);
    }

    private static bool HasPrice([NotNullWhen(true)] ModelsDevCostDefinition? cost)
        => cost is not null && (cost.Input > 0 || cost.Output > 0);

    private static void ApplyModelsDevMetadata(
        IDictionary<string, object?> capabilities,
        string modelsDevProviderId,
        ModelsDevModelDefinition model)
    {
        capabilities["modelsDevProviderId"] = modelsDevProviderId;
        capabilities["modelsDevModelId"] = model.Id;
        SetIfMissing(capabilities, "family", model.Family);
        SetIfMissing(capabilities, "knowledge", model.Knowledge);
        SetIfMissing(capabilities, "releaseDate", model.ReleaseDate);
        SetIfMissing(capabilities, "lastUpdated", model.LastUpdated);
        SetIfMissing(capabilities, "status", model.Status);
        SetIfMissing(capabilities, "supportsAttachments", model.Attachment);
        SetIfMissing(capabilities, "supportsReasoning", model.Reasoning);
        SetIfMissing(capabilities, "supportsToolCall", model.ToolCall);
        SetIfMissing(capabilities, "supportsStructuredOutput", model.StructuredOutput);
        SetIfMissing(capabilities, "supportsTemperature", model.Temperature);
        SetIfMissing(capabilities, "openWeights", model.OpenWeights);
        SetIfMissing(capabilities, "inputModalities", model.Modalities?.Input);
        SetIfMissing(capabilities, "outputModalities", model.Modalities?.Output);
        SetIfMissing(capabilities, "contextWindow", model.Limit?.Context);
        SetIfMissing(capabilities, "contextWindowTokens", model.Limit?.Context);
        SetIfMissing(capabilities, "inputTokenLimit", model.Limit?.Input);
        SetIfMissing(capabilities, "maxInputTokens", model.Limit?.Input);
        SetIfMissing(capabilities, "outputTokenLimit", model.Limit?.Output);
        SetIfMissing(capabilities, "maxTokens", model.Limit?.Output);
    }

    private static void ApplyOverride(
        IDictionary<string, object?> capabilities,
        AgentModelOverride modelOverride)
    {
        capabilities["overrideApplied"] = true;
        SetWhenValue(capabilities, "contextWindow", modelOverride.ContextWindowTokens);
        SetWhenValue(capabilities, "contextWindowTokens", modelOverride.ContextWindowTokens);
        SetWhenValue(capabilities, "inputTokenLimit", modelOverride.InputTokenLimit);
        SetWhenValue(capabilities, "maxInputTokens", modelOverride.InputTokenLimit);
        SetWhenValue(capabilities, "outputTokenLimit", modelOverride.OutputTokenLimit);
        SetWhenValue(capabilities, "maxTokens", modelOverride.MaxTokens ?? modelOverride.OutputTokenLimit);
        SetWhenValue(capabilities, "supportsReasoning", modelOverride.SupportsReasoning);
        SetWhenValue(capabilities, "supportsToolCall", modelOverride.SupportsToolCall);
        SetWhenValue(capabilities, "supportsAttachments", modelOverride.SupportsAttachments);
        // The model reads images and attachments: images are what the composer and the tools check (#57).
        SetWhenValue(capabilities, "supportsImageInput", modelOverride.SupportsAttachments);
        SetWhenValue(capabilities, "supportsStructuredOutput", modelOverride.SupportsStructuredOutput);
    }

    private static void ApplyKnownLimit(
        IDictionary<string, object?> capabilities,
        AgentModelKnownLimit knownLimit)
    {
        SetAtMost(capabilities, "contextWindow", knownLimit.ContextWindowTokens);
        SetAtMost(capabilities, "contextWindowTokens", knownLimit.ContextWindowTokens);
        SetAtMost(capabilities, "inputTokenLimit", knownLimit.InputTokenLimit);
        SetAtMost(capabilities, "maxInputTokens", knownLimit.InputTokenLimit);
        capabilities[AgentTokenBudgetResolver.CompactionRatioCapabilityKey] = knownLimit.CompactionRatio;
    }

    // A limit the model has, lowered to the known one; the known one where it has none.
    private static void SetAtMost(IDictionary<string, object?> capabilities, string key, long limit)
    {
        if (capabilities.TryGetValue(key, out var value) &&
            AgentTokenBudgetResolver.TryConvertToInt64(value, out var current) &&
            current > 0 &&
            current <= limit)
        {
            return;
        }

        capabilities[key] = limit;
    }

    private static void SetIfMissing(IDictionary<string, object?> capabilities, string key, object? value)
    {
        if (value is null || capabilities.ContainsKey(key))
        {
            return;
        }

        capabilities[key] = value;
    }

    private static void SetWhenValue(IDictionary<string, object?> capabilities, string key, object? value)
    {
        if (value is not null)
        {
            capabilities[key] = value;
        }
    }
}
