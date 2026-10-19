using Tomlyn.Model;

namespace CodeAlta.Catalog;

/// <summary>
/// Copies configuration documents exactly as they are, with every table, dictionary and list: the copy shares nothing
/// that can be changed with the original, keeps the comparers of the dictionaries and the order of their keys, and
/// nothing is normalized, dropped or filled in.
/// </summary>
/// <remarks>
/// Each type of the document is copied property by property: a property added to one of them is added here as well.
/// <c>CodeAltaConfigDocumentCopyTests</c> fills every property of every type the document reaches and fails on one that
/// is not copied.
/// </remarks>
internal static class CodeAltaConfigDocumentCopy
{
    /// <summary>Copies a whole document.</summary>
    /// <param name="document">The document.</param>
    /// <returns>A document equal to <paramref name="document"/> that shares nothing with it but immutable values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    internal static CodeAltaConfigDocument Copy(CodeAltaConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new CodeAltaConfigDocument
        {
            Chat = document.Chat is { } chat ? new CodeAltaChatSettingsDocument { DefaultProvider = chat.DefaultProvider } : null,
            Providers = Copy(document.Providers, Copy),
            // Records: immutable, only the list is new.
            UnsupportedProviders = [.. document.UnsupportedProviders],
            Skills = document.Skills is { } skills ? new CodeAltaSkillSettingsDocument { Disabled = Copy(skills.Disabled) } : null,
            Plugins = Copy(document.Plugins, static plugin => new CodeAltaPluginSettingsDocument { Enabled = plugin.Enabled }),
            Worktrees = document.Worktrees is { } worktrees
                ? new CodeAltaWorktreeSettingsDocument { Location = worktrees.Location, Folder = worktrees.Folder }
                : null,
            WorkItems = document.WorkItems is { } workItems
                ? new CodeAltaWorkItemSettingsDocument
                {
                    Propose = workItems.Propose,
                    Notify = workItems.Notify,
                    CompletedTasks = workItems.CompletedTasks,
                    DismissedTasks = workItems.DismissedTasks,
                    CompletedPlans = workItems.CompletedPlans,
                    Start = workItems.Start,
                }
                : null,
            Storage = document.Storage is { } storage
                ? new CodeAltaStorageSettingsDocument
                {
                    ClearScratchpadOnStartup = storage.ClearScratchpadOnStartup,
                    SessionRetention = storage.SessionRetention is { } retention
                        ? new CodeAltaSessionRetentionSettingsDocument { Enabled = retention.Enabled, Amount = retention.Amount, Unit = retention.Unit }
                        : null,
                }
                : null,
            Sessions = document.Sessions is { } sessions ? new CodeAltaSessionSettingsDocument { Title = sessions.Title } : null,
            Speech = document.Speech is { } speech
                ? new CodeAltaSpeechSettingsDocument { Enabled = speech.Enabled, Model = speech.Model, Send = speech.Send }
                : null,
            Search = document.Search is { } search
                ? new CodeAltaSearchSettingsDocument
                {
                    Enabled = search.Enabled,
                    Sessions = search.Sessions,
                    Semantic = search.Semantic,
                    Model = search.Model,
                    PromptCatalog = search.PromptCatalog,
                    ExcludedProjects = Copy(search.ExcludedProjects),
                    ExcludedSpaces = Copy(search.ExcludedSpaces),
                    MeaningOffer = search.MeaningOffer,
                    Parallelism = search.Parallelism,
                }
                : null,
            Models = document.Models is { } models
                ? new CodeAltaModelSettingsDocument
                {
                    Tiers = models.Tiers is { } tiers
                        ? new CodeAltaModelTiersDocument { Complex = Copy(tiers.Complex), Standard = Copy(tiers.Standard), Quick = Copy(tiers.Quick) }
                        : null,
                    Fallback = models.Fallback is { } fallback
                        ? new CodeAltaModelFallbackDocument { Enabled = fallback.Enabled, On = Copy(fallback.On) }
                        : null,
                }
                : null,
            Tools = document.Tools is { } tools ? new CodeAltaToolSettingsDocument { Disabled = Copy(tools.Disabled) } : null,
        };
    }

    /// <summary>Copies one provider definition as it is, unlike <see cref="CodeAltaConfigStore.CloneProviderDefinition"/>, which normalizes some tables.</summary>
    /// <param name="provider">The definition.</param>
    /// <returns>An equal definition that shares nothing that can be changed with <paramref name="provider"/>.</returns>
    internal static CodeAltaProviderDocument Copy(CodeAltaProviderDocument provider) => new()
    {
        ProviderKey = provider.ProviderKey,
        Enabled = provider.Enabled,
        DisplayName = provider.DisplayName,
        Icon = provider.Icon,
        Color = provider.Color,
        ProviderType = provider.ProviderType,
        Model = provider.Model,
        ReasoningEffort = provider.ReasoningEffort,
        ApiKey = provider.ApiKey,
        ApiKeyEnv = provider.ApiKeyEnv,
        ApiUrl = provider.ApiUrl,
        NetworkTimeoutSeconds = provider.NetworkTimeoutSeconds,
        Command = provider.Command,
        Arguments = Copy(provider.Arguments),
        PermissionMode = provider.PermissionMode,
        AnthropicApiKey = provider.AnthropicApiKey,
        PromptSuggestions = provider.PromptSuggestions,
        RefusalFallback = provider.RefusalFallback,
        PromptCacheTtl = provider.PromptCacheTtl,
        ConfigDir = provider.ConfigDir,
        CodexHome = provider.CodexHome,
        GitHubEnterpriseUrl = provider.GitHubEnterpriseUrl,
        GitHubTokenEnv = provider.GitHubTokenEnv,
        CopilotTokenEnv = provider.CopilotTokenEnv,
        EnableModelPolicies = provider.EnableModelPolicies,
        IncludePreviewModels = provider.IncludePreviewModels,
        ProtocolTrace = provider.ProtocolTrace,
        AuthSource = provider.AuthSource,
        AccountId = provider.AccountId,
        MaxConcurrentRequests = provider.MaxConcurrentRequests,
        TextVerbosity = provider.TextVerbosity,
        ServiceTier = provider.ServiceTier,
        WebSearch = provider.WebSearch,
        IncludeEncryptedReasoning = provider.IncludeEncryptedReasoning,
        ModelDiscovery = provider.ModelDiscovery,
        ResponseTransport = provider.ResponseTransport,
        SendResponsesBetaHeader = provider.SendResponsesBetaHeader,
        SendInstallationId = provider.SendInstallationId,
        InstallationIdSource = provider.InstallationIdSource,
        Experimental = provider.Experimental,
        OrganizationId = provider.OrganizationId,
        ProjectId = provider.ProjectId,
        Project = provider.Project,
        Location = provider.Location,
        ModelsDevProviderId = provider.ModelsDevProviderId,
        SingleModelId = provider.SingleModelId,
        ModelsIncludeRegex = provider.ModelsIncludeRegex,
        SortModels = provider.SortModels,
        ExtraBody = Copy(provider.ExtraBody),
        Request = provider.Request is { } request
            ? new CodeAltaProviderRequestDocument
            {
                Headers = Copy(request.Headers, static value => value),
                RemoveHeaders = Copy(request.RemoveHeaders),
                ExtraBody = Copy(request.ExtraBody),
                RemoveExtraBody = Copy(request.RemoveExtraBody),
            }
            : null,
        Profile = provider.Profile is { } profile
            ? new CodeAltaProviderProfileDocument
            {
                SupportsDeveloperRole = profile.SupportsDeveloperRole,
                SupportsStore = profile.SupportsStore,
                SupportsReasoningEffort = profile.SupportsReasoningEffort,
                SupportsParallelToolCalls = profile.SupportsParallelToolCalls,
                StreamsUsage = profile.StreamsUsage,
                SupportsThoughtSignatures = profile.SupportsThoughtSignatures,
                RequiresToolResultName = profile.RequiresToolResultName,
                RequiresAssistantAfterToolResult = profile.RequiresAssistantAfterToolResult,
                SupportsToolResultImages = profile.SupportsToolResultImages,
                SupportsCacheControl = profile.SupportsCacheControl,
                SupportsToolDeferral = profile.SupportsToolDeferral,
                SupportsStrictTools = profile.SupportsStrictTools,
                ThinkingFormat = profile.ThinkingFormat,
                MaxTokensFieldName = profile.MaxTokensFieldName,
                ReasoningFieldNames = Copy(profile.ReasoningFieldNames),
                ReasoningInputFieldName = profile.ReasoningInputFieldName,
            }
            : null,
        Compaction = provider.Compaction is { } compaction
            ? new CodeAltaProviderCompactionDocument
            {
                Enabled = compaction.Enabled,
                Ratio = compaction.Ratio,
                SummaryOutputRatio = compaction.SummaryOutputRatio,
                PostCompactionTargetRatio = compaction.PostCompactionTargetRatio,
                SummaryShareOfTarget = compaction.SummaryShareOfTarget,
                FileContextShareOfSummaryTarget = compaction.FileContextShareOfSummaryTarget,
                KeepLastUserMessage = compaction.KeepLastUserMessage,
                AllowSplitTurn = compaction.AllowSplitTurn,
            }
            : null,
        ModelOverrides = Copy(provider.ModelOverrides, static modelOverride => new CodeAltaProviderModelOverrideDocument
        {
            DisplayName = modelOverride.DisplayName,
            Description = modelOverride.Description,
            ContextWindow = modelOverride.ContextWindow,
            InputTokenLimit = modelOverride.InputTokenLimit,
            OutputTokenLimit = modelOverride.OutputTokenLimit,
            MaxTokens = modelOverride.MaxTokens,
            SupportsReasoning = modelOverride.SupportsReasoning,
            SupportsToolCall = modelOverride.SupportsToolCall,
            SupportsAttachments = modelOverride.SupportsAttachments,
            SupportsStructuredOutput = modelOverride.SupportsStructuredOutput,
        }),
        ModelRequest = Copy(provider.ModelRequest, static modelRequest => new CodeAltaProviderModelRequestDocument
        {
            Headers = Copy(modelRequest.Headers, static value => value),
            RemoveHeaders = Copy(modelRequest.RemoveHeaders),
            ExtraBody = Copy(modelRequest.ExtraBody),
            RemoveExtraBody = Copy(modelRequest.RemoveExtraBody),
        }),
    };

    private static List<string>? Copy(List<string>? list) => list is null ? null : [.. list];

    // The same comparer and the same order of the keys.
    private static Dictionary<string, TValue>? Copy<TValue>(Dictionary<string, TValue>? dictionary, Func<TValue, TValue> copy)
        where TValue : class
    {
        if (dictionary is null) return null;
        var result = new Dictionary<string, TValue>(dictionary.Count, dictionary.Comparer);
        foreach (var (key, value) in dictionary)
        {
            // A null value, which only a document built by hand holds, stays null.
            result.Add(key, value is null ? value! : copy(value));
        }

        return result;
    }

    private static TomlTable? Copy(TomlTable? table) => table is null ? null : CopyTable(table);

    // A table of TOML values: the tables and the arrays in it are copied, the other values (strings, numbers, booleans,
    // dates) are immutable. The kind of the table, its keys and their order, an empty table and a null value (which the
    // normalization of extra_body leaves for an empty table) stay as they are.
    private static TomlTable CopyTable(TomlTable table)
    {
        var result = new TomlTable(table.Kind == ObjectKind.InlineTable);
        foreach (var (key, value) in table)
        {
            result[key] = CopyValue(value)!;
        }

        return result;
    }

    private static object? CopyValue(object? value) => value switch
    {
        TomlTable table => CopyTable(table),
        TomlTableArray tables => CopyTables(tables),
        TomlArray array => CopyArray(array),
        _ => value,
    };

    private static TomlArray CopyArray(TomlArray array)
    {
        var result = new TomlArray(array.Count);
        foreach (var item in array)
        {
            result.Add(CopyValue(item));
        }

        return result;
    }

    private static TomlTableArray CopyTables(TomlTableArray tables)
    {
        var result = new TomlTableArray();
        foreach (var table in tables)
        {
            result.Add(CopyTable(table));
        }

        return result;
    }
}
