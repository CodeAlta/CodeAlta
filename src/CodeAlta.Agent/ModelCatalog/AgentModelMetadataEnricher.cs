using System.Diagnostics.CodeAnalysis;

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
        string? modelsIncludeRegex = null)
    {
        var filteredModels = AgentModelCatalogFilter.ApplyIncludeRegex(models, modelsIncludeRegex);
        if (filteredModels.Count == 0)
        {
            return filteredModels;
        }

        return filteredModels
            .Select(model => EnrichModel(model, catalog, modelsDevProviderId, overrides))
            .ToArray();
    }

    public static AgentModelInfo EnrichModel(
        AgentModelInfo model,
        ModelsDevCatalogService? catalog,
        string? modelsDevProviderId,
        IReadOnlyDictionary<string, AgentModelOverride>? overrides)
    {
        ArgumentNullException.ThrowIfNull(model);

        var displayName = model.DisplayName;
        var description = model.Description;
        var capabilities = model.Capabilities is null
            ? null
            : new Dictionary<string, object?>(model.Capabilities, StringComparer.Ordinal);

        if (catalog is not null && TryFindModelsDevModel(catalog, modelsDevProviderId, model.Id, out var foundProviderId, out var modelsDevModel))
        {
            capabilities ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            ApplyModelsDevMetadata(capabilities, foundProviderId, modelsDevModel);
            ApplyModelsDevPrice(capabilities, catalog, foundProviderId, modelsDevModel, model.Id);

            if (string.IsNullOrWhiteSpace(displayName) ||
                string.Equals(displayName, model.Id, StringComparison.Ordinal))
            {
                displayName = modelsDevModel.Name ?? displayName;
            }
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
            SupportedReasoningEfforts: model.SupportedReasoningEfforts,
            Capabilities: capabilities ?? model.Capabilities);
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
        SetWhenValue(capabilities, "supportsStructuredOutput", modelOverride.SupportsStructuredOutput);
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
