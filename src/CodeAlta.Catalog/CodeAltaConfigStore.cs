using CodeAlta.Agent;
using Tomlyn;
using Tomlyn.Model;

namespace CodeAlta.Catalog;

/// <summary>
/// Loads and persists CodeAlta TOML configuration files.
/// </summary>
public sealed class CodeAltaConfigStore
{
    private static readonly CodeAltaRawApiCompactionDocument DefaultCompaction = new()
    {
        Enabled = true,
        TriggerThreshold = 0.85,
        TargetThreshold = 0.50,
        ReservedOutputTokens = 4096,
        ReservedOverheadTokens = 2048,
        KeepLastUserMessage = true,
        AllowSplitTurn = true,
        TargetContextRatioIdeal = 0.03,
        TargetContextRatioMax = 0.10,
        RecentSuffixTargetTokens = 20_000,
        SummaryOutputTokens = 1_024,
        SummaryInputTokens = 24_000,
        ToolResultCharsPerItem = 1_200,
        ToolResultCharsTotal = 6_000,
        ReasoningCharsPerItem = 600,
        ReasoningCharsTotal = 3_000,
        ReasoningMode = "adaptive",
        MaxChunkPasses = 4,
        AllowOversizedAnchorReduction = true,
        PreferRecentMessages = true,
        PreferRecentToolOutputs = true,
        DropMessagesOnlyWhenSummaryInputExceedsBudget = true,
    };

    private readonly CatalogOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodeAltaConfigStore"/> class.
    /// </summary>
    /// <param name="options">Catalog layout options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <see cref="CatalogOptions.GlobalRoot"/> is empty.</exception>
    public CodeAltaConfigStore(CatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.GlobalRoot))
        {
            throw new ArgumentException("Global catalog root is required.", nameof(options));
        }

        _options = options;
    }

    /// <summary>
    /// Loads the global user configuration.
    /// </summary>
    /// <returns>The parsed configuration document.</returns>
    public CodeAltaConfigDocument LoadGlobal()
        => LoadDocument(_options.ConfigPath);

    /// <summary>
    /// Loads the project-local configuration when present.
    /// </summary>
    /// <param name="projectRoot">The project root directory.</param>
    /// <returns>The parsed configuration document, or an empty document when the file is missing.</returns>
    public CodeAltaConfigDocument LoadProject(string? projectRoot)
        => string.IsNullOrWhiteSpace(projectRoot)
            ? new CodeAltaConfigDocument()
            : LoadDocument(GetProjectConfigPath(projectRoot));

    /// <summary>
    /// Resolves the effective backend preference for a scope.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="projectRoot">Optional project root for project-local overrides.</param>
    /// <returns>The merged backend preference.</returns>
    public CodeAltaBackendPreference GetEffectiveBackendPreference(AgentBackendId backendId, string? projectRoot = null)
    {
        var global = LoadGlobal();
        var project = LoadProject(projectRoot);
        return ResolveBackendPreference(global, project, backendId);
    }

    /// <summary>
    /// Persists the global backend preference.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="model">The preferred model identifier.</param>
    /// <param name="reasoningEffort">The preferred reasoning effort.</param>
    public void SaveGlobalBackendPreference(
        AgentBackendId backendId,
        string? model,
        AgentReasoningEffort? reasoningEffort)
    {
        var document = LoadGlobal();
        var normalizedModel = NormalizeModel(model);
        var normalizedReasoning = FormatReasoningEffort(reasoningEffort);

        if (normalizedModel is null && normalizedReasoning is null)
        {
            document.Backends.Remove(backendId.Value);
        }
        else
        {
            document.Backends[backendId.Value] = new CodeAltaBackendSettingsDocument
            {
                Model = normalizedModel,
                ReasoningEffort = normalizedReasoning,
            };
        }

        SaveDocument(_options.ConfigPath, document);
    }

    /// <summary>
    /// Loads globally configured ACP backend definitions.
    /// </summary>
    /// <returns>The configured ACP agent definitions.</returns>
    public IReadOnlyList<AcpBackendDefinition> LoadGlobalAcpBackendDefinitions()
        => LoadGlobalAcpBackendDefinitions(includeDisabled: false);

    /// <summary>
    /// Loads globally configured ACP backend definitions.
    /// </summary>
    /// <param name="includeDisabled">
    /// <see langword="true"/> to include disabled definitions; otherwise only enabled definitions are returned.
    /// </param>
    /// <returns>The configured ACP agent definitions.</returns>
    public IReadOnlyList<AcpBackendDefinition> LoadGlobalAcpBackendDefinitions(bool includeDisabled)
    {
        var document = LoadGlobal();
        NormalizeDocument(document);
        return document.Acp.Agents.Values
            .Where(definition => includeDisabled || definition.Enabled)
            .Select(CloneAcpBackendDefinition)
            .OrderBy(static definition => definition.DisplayName ?? definition.AgentId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Loads a globally configured ACP backend definition when present.
    /// </summary>
    /// <param name="agentId">The ACP agent identifier.</param>
    /// <returns>The configured definition, or <see langword="null"/> when missing.</returns>
    public AcpBackendDefinition? LoadGlobalAcpBackendDefinition(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var document = LoadGlobal();
        NormalizeDocument(document);
        return document.Acp.Agents.TryGetValue(agentId.Trim(), out var definition)
            ? CloneAcpBackendDefinition(definition)
            : null;
    }

    /// <summary>
    /// Saves a global ACP backend definition override.
    /// </summary>
    /// <param name="definition">The definition to persist.</param>
    public void SaveGlobalAcpBackendDefinition(AcpBackendDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.AgentId))
        {
            throw new ArgumentException("ACP agent id is required.", nameof(definition));
        }

        var document = LoadGlobal();
        NormalizeDocument(document);
        var normalized = CloneAcpBackendDefinition(definition);
        normalized.AgentId = NormalizeAcpAgentId(normalized.AgentId)
            ?? throw new ArgumentException("ACP agent id is required.", nameof(definition));
        normalized.RegistryId = NormalizeAcpAgentId(normalized.RegistryId);
        normalized.DisplayName = NormalizeText(normalized.DisplayName);
        normalized.Command = NormalizeText(normalized.Command);
        normalized.WorkingDirectory = NormalizeText(normalized.WorkingDirectory);
        normalized.Arguments = NormalizeList(normalized.Arguments);
        normalized.EnvironmentVariables = NormalizeDictionary(normalized.EnvironmentVariables);

        document.Acp.Agents[normalized.AgentId] = normalized;
        SaveDocument(_options.ConfigPath, document);
    }

    /// <summary>
    /// Deletes a global ACP backend definition override.
    /// </summary>
    /// <param name="agentId">The ACP agent identifier.</param>
    /// <returns><see langword="true"/> when the definition existed and was removed.</returns>
    public bool DeleteGlobalAcpBackendDefinition(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var document = LoadGlobal();
        NormalizeDocument(document);
        var removed = document.Acp.Agents.Remove(agentId.Trim());
        if (removed)
        {
            SaveDocument(_options.ConfigPath, document);
        }

        return removed;
    }

    /// <summary>
    /// Deletes every global ACP backend definition override.
    /// </summary>
    public void DeleteAllGlobalAcpBackendDefinitions()
    {
        var document = LoadGlobal();
        NormalizeDocument(document);
        if (document.Acp.Agents.Count == 0)
        {
            return;
        }

        document.Acp.Agents.Clear();
        SaveDocument(_options.ConfigPath, document);
    }

    /// <summary>
    /// Determines whether a global ACP backend definition override exists.
    /// </summary>
    /// <param name="agentId">The ACP agent identifier.</param>
    /// <returns><see langword="true"/> when an override exists.</returns>
    public bool HasGlobalAcpBackendDefinition(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var document = LoadGlobal();
        NormalizeDocument(document);
        return document.Acp.Agents.ContainsKey(agentId.Trim());
    }

    /// <summary>
    /// Loads effective ACP backend definitions using installed manifests as defaults and global config as overrides.
    /// </summary>
    /// <param name="installedDefinitions">Installed ACP backend definitions.</param>
    /// <returns>The effective ACP backend definitions.</returns>
    public IReadOnlyList<AcpBackendDefinition> LoadEffectiveAcpBackendDefinitions(
        IReadOnlyList<AcpBackendDefinition>? installedDefinitions = null)
    {
        var effective = new Dictionary<string, AcpBackendDefinition>(StringComparer.OrdinalIgnoreCase);
        if (installedDefinitions is not null)
        {
            foreach (var installedDefinition in installedDefinitions.Where(static definition => definition.Enabled))
            {
                effective[installedDefinition.AgentId] = CloneAcpBackendDefinition(installedDefinition);
            }
        }

        foreach (var configuredDefinition in LoadGlobalAcpBackendDefinitions(includeDisabled: false))
        {
            effective[configuredDefinition.AgentId] = CloneAcpBackendDefinition(configuredDefinition);
        }

        return effective.Values
            .Where(static definition => definition.Enabled)
            .OrderBy(static definition => definition.DisplayName ?? definition.AgentId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Loads globally configured raw-API provider endpoint definitions.
    /// </summary>
    /// <param name="includeDisabled">
    /// <see langword="true"/> to include disabled definitions; otherwise only enabled definitions are returned.
    /// </param>
    /// <returns>The configured provider endpoint definitions.</returns>
    public IReadOnlyList<CodeAltaRawApiProviderDocument> LoadGlobalRawApiProviderDefinitions(bool includeDisabled = false)
    {
        var document = LoadGlobal();
        NormalizeDocument(document);
        var definitions = document.RawApi.Providers.Values
            .Select(CloneRawApiProviderDefinition)
            .ToDictionary(
                static definition => definition.ProviderKey,
                static definition => definition,
                StringComparer.OrdinalIgnoreCase);

        foreach (var definition in document.Providers.Values)
        {
            definitions[definition.ProviderKey] = CloneRawApiProviderDefinition(definition);
        }

        return definitions.Values
            .Where(definition => includeDisabled || definition.Enabled)
            .OrderBy(static definition => definition.DisplayName ?? definition.ProviderKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Loads globally configured OpenAI-compatible provider definitions.
    /// </summary>
    /// <param name="includeDisabled">
    /// <see langword="true"/> to include disabled definitions; otherwise only enabled definitions are returned.
    /// </param>
    /// <returns>The configured provider definitions.</returns>
    public IReadOnlyList<CodeAltaOpenAIProviderDocument> LoadGlobalOpenAIProviderDefinitions(bool includeDisabled = false)
    {
        var document = LoadGlobal();
        NormalizeDocument(document);
        var definitions = document.RawApi.OpenAI.Providers.Values
            .Select(CloneOpenAIProviderDefinition)
            .ToDictionary(
                static definition => definition.ProviderKey,
                static definition => definition,
                StringComparer.OrdinalIgnoreCase);

        foreach (var definition in document.RawApi.Providers.Values)
        {
            if (TryConvertUnifiedProviderToOpenAI(definition, out var converted) && converted is not null)
            {
                definitions[converted.ProviderKey] = converted;
            }
        }

        foreach (var definition in document.Providers.Values)
        {
            if (TryConvertUnifiedProviderToOpenAI(definition, out var converted) && converted is not null)
            {
                definitions[converted.ProviderKey] = converted;
            }
        }

        return definitions.Values
            .Where(definition => includeDisabled || definition.Enabled)
            .OrderBy(static definition => definition.DisplayName ?? definition.ProviderKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Loads globally configured Anthropic provider definitions.
    /// </summary>
    /// <param name="includeDisabled">
    /// <see langword="true"/> to include disabled definitions; otherwise only enabled definitions are returned.
    /// </param>
    /// <returns>The configured provider definitions.</returns>
    public IReadOnlyList<CodeAltaAnthropicProviderDocument> LoadGlobalAnthropicProviderDefinitions(bool includeDisabled = false)
    {
        var document = LoadGlobal();
        NormalizeDocument(document);
        var definitions = document.RawApi.Anthropic.Providers.Values
            .Select(CloneAnthropicProviderDefinition)
            .ToDictionary(
                static definition => definition.ProviderKey,
                static definition => definition,
                StringComparer.OrdinalIgnoreCase);

        foreach (var definition in document.RawApi.Providers.Values)
        {
            if (TryConvertUnifiedProviderToAnthropic(definition, out var converted) && converted is not null)
            {
                definitions[converted.ProviderKey] = converted;
            }
        }

        foreach (var definition in document.Providers.Values)
        {
            if (TryConvertUnifiedProviderToAnthropic(definition, out var converted) && converted is not null)
            {
                definitions[converted.ProviderKey] = converted;
            }
        }

        return definitions.Values
            .Where(definition => includeDisabled || definition.Enabled)
            .OrderBy(static definition => definition.DisplayName ?? definition.ProviderKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Loads globally configured Google GenAI provider definitions.
    /// </summary>
    /// <param name="includeDisabled">
    /// <see langword="true"/> to include disabled definitions; otherwise only enabled definitions are returned.
    /// </param>
    /// <returns>The configured provider definitions.</returns>
    public IReadOnlyList<CodeAltaGoogleGenAIProviderDocument> LoadGlobalGoogleGenAIProviderDefinitions(bool includeDisabled = false)
    {
        var document = LoadGlobal();
        NormalizeDocument(document);
        var definitions = document.RawApi.GoogleGenAI.Providers.Values
            .Select(CloneGoogleGenAIProviderDefinition)
            .ToDictionary(
                static definition => definition.ProviderKey,
                static definition => definition,
                StringComparer.OrdinalIgnoreCase);

        foreach (var definition in document.RawApi.Providers.Values)
        {
            if (TryConvertUnifiedProviderToGoogleGenAI(definition, out var converted) && converted is not null)
            {
                definitions[converted.ProviderKey] = converted;
            }
        }

        foreach (var definition in document.Providers.Values)
        {
            if (TryConvertUnifiedProviderToGoogleGenAI(definition, out var converted) && converted is not null)
            {
                definitions[converted.ProviderKey] = converted;
            }
        }

        return definitions.Values
            .Where(definition => includeDisabled || definition.Enabled)
            .OrderBy(static definition => definition.DisplayName ?? definition.ProviderKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static AgentReasoningEffort? ParseReasoningEffort(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "none" => AgentReasoningEffort.None,
            "minimal" => AgentReasoningEffort.Minimal,
            "low" => AgentReasoningEffort.Low,
            "medium" => AgentReasoningEffort.Medium,
            "high" => AgentReasoningEffort.High,
            "xhigh" => AgentReasoningEffort.XHigh,
            _ => null,
        };
    }

    internal static string? FormatReasoningEffort(AgentReasoningEffort? effort)
    {
        return effort switch
        {
            null => null,
            AgentReasoningEffort.None => "none",
            AgentReasoningEffort.Minimal => "minimal",
            AgentReasoningEffort.Low => "low",
            AgentReasoningEffort.Medium => "medium",
            AgentReasoningEffort.High => "high",
            AgentReasoningEffort.XHigh => "xhigh",
            _ => null,
        };
    }

    internal static CodeAltaBackendPreference ResolveBackendPreference(
        CodeAltaConfigDocument global,
        CodeAltaConfigDocument? project,
        AgentBackendId backendId)
    {
        ArgumentNullException.ThrowIfNull(global);

        return MergeBackendPreference(
            GetBackendSettings(global, backendId),
            project is null ? null : GetBackendSettings(project, backendId));
    }

    internal static CodeAltaBackendSettingsDocument? GetBackendSettings(
        CodeAltaConfigDocument document,
        AgentBackendId backendId)
    {
        return document.Backends.TryGetValue(backendId.Value, out var settings)
            ? settings
            : null;
    }

    private static CodeAltaBackendPreference MergeBackendPreference(
        CodeAltaBackendSettingsDocument? global,
        CodeAltaBackendSettingsDocument? project)
    {
        var model = NormalizeModel(project?.Model) ?? NormalizeModel(global?.Model);
        var reasoning = ParseReasoningEffort(project?.ReasoningEffort) ?? ParseReasoningEffort(global?.ReasoningEffort);
        return new CodeAltaBackendPreference(model, reasoning);
    }

    private static string? NormalizeModel(string? model)
        => string.IsNullOrWhiteSpace(model) ? null : model.Trim();

    private static string GetProjectConfigPath(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return Path.Combine(projectRoot, ".codealta", "config.toml");
    }

    private static CodeAltaConfigDocument LoadDocument(string path)
    {
        if (!File.Exists(path))
        {
            return new CodeAltaConfigDocument();
        }

        var content = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(content))
        {
            return new CodeAltaConfigDocument();
        }

        try
        {
            var document = TomlSerializer.Deserialize(content, CodeAltaTomlSerializerContext.Default.CodeAltaConfigDocument)
                ?? new CodeAltaConfigDocument();
            NormalizeDocument(document);
            return document;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or IOException or TomlException)
        {
            throw new InvalidDataException($"Failed to parse CodeAlta config '{path}'.", ex);
        }
    }

    private static void SaveDocument(string path, CodeAltaConfigDocument document)
    {
        NormalizeDocument(document);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = TomlSerializer.Serialize(document, CodeAltaTomlSerializerContext.Default.CodeAltaConfigDocument);
        File.WriteAllText(path, content);
    }

    private static void NormalizeDocument(CodeAltaConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Backends = document.Backends
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Key))
            .ToDictionary(
                static entry => entry.Key.Trim(),
                static entry => new CodeAltaBackendSettingsDocument
                {
                    Model = NormalizeModel(entry.Value?.Model),
                    ReasoningEffort = NormalizeReasoningEffortText(entry.Value?.ReasoningEffort),
                },
                StringComparer.OrdinalIgnoreCase);

        document.Acp ??= new CodeAltaAcpSettingsDocument();
        document.Acp.Agents = document.Acp.Agents
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Key))
            .Select(
                static entry =>
                {
                    var definition = entry.Value ?? new AcpBackendDefinition();
                    definition.AgentId = NormalizeAcpAgentId(definition.AgentId) ?? entry.Key.Trim();
                    definition.DisplayName = NormalizeText(definition.DisplayName);
                    definition.RegistryId = NormalizeAcpAgentId(definition.RegistryId);
                    definition.Command = NormalizeText(definition.Command);
                    definition.WorkingDirectory = NormalizeText(definition.WorkingDirectory);
                    definition.Arguments = NormalizeList(definition.Arguments);
                    definition.EnvironmentVariables = NormalizeDictionary(definition.EnvironmentVariables);
                    return KeyValuePair.Create(entry.Key.Trim(), definition);
                })
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Value.AgentId))
            .ToDictionary(
                static entry => entry.Value.AgentId,
                static entry => entry.Value,
                StringComparer.OrdinalIgnoreCase);

        document.RawApi ??= new CodeAltaRawApiSettingsDocument();
        document.RawApi.Compaction = NormalizeAndCompleteCompactionSettings(
            document.RawApi.Compaction,
            DefaultCompaction);
        document.Providers = NormalizeUnifiedProviderDefinitions(
            document.Providers,
            document.RawApi.Compaction,
            document.RawApi.Compaction);
        document.RawApi.Providers = NormalizeUnifiedProviderDefinitions(
            document.RawApi.Providers,
            document.RawApi.Compaction,
            document.RawApi.Compaction);
        document.RawApi.OpenAI ??= new CodeAltaOpenAISettingsDocument();
        document.RawApi.OpenAI.Providers = document.RawApi.OpenAI.Providers
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Key))
            .Select(entry =>
            {
                var definition = entry.Value ?? new CodeAltaOpenAIProviderDocument();
                definition.ProviderKey = NormalizeRawProviderKey(entry.Key) ?? string.Empty;
                definition.DisplayName = NormalizeText(definition.DisplayName);
                definition.ApiKey = NormalizeText(definition.ApiKey);
                definition.ApiKeyEnv = NormalizeText(definition.ApiKeyEnv);
                definition.BaseUri = NormalizeText(definition.BaseUri);
                definition.OrganizationId = NormalizeText(definition.OrganizationId);
                definition.ProjectId = NormalizeText(definition.ProjectId);
                definition.ModelsDevProviderId = NormalizeRawProviderKey(definition.ModelsDevProviderId);
                definition.SingleModelId = NormalizeModel(definition.SingleModelId);
                definition.ExtraBody = NormalizeExtraBody(definition.ExtraBody);
                definition.Profile = NormalizeProfile(definition.Profile);
                definition.ModelOverrides = NormalizeModelOverrides(definition.ModelOverrides);
                definition.Compaction = NormalizeAndCompleteCompactionSettings(definition.Compaction, document.RawApi.Compaction);
                return definition;
            })
            .Where(static definition => !string.IsNullOrWhiteSpace(definition.ProviderKey))
            .ToDictionary(
                static definition => definition.ProviderKey,
                static definition => definition,
                StringComparer.OrdinalIgnoreCase);

        document.RawApi.Anthropic ??= new CodeAltaAnthropicSettingsDocument();
        document.RawApi.Anthropic.Providers = document.RawApi.Anthropic.Providers
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Key))
            .Select(entry =>
            {
                var definition = entry.Value ?? new CodeAltaAnthropicProviderDocument();
                definition.ProviderKey = NormalizeRawProviderKey(entry.Key) ?? string.Empty;
                definition.DisplayName = NormalizeText(definition.DisplayName);
                definition.ApiKey = NormalizeText(definition.ApiKey);
                definition.ApiKeyEnv = NormalizeText(definition.ApiKeyEnv);
                definition.BaseUri = NormalizeText(definition.BaseUri);
                definition.ModelsDevProviderId = NormalizeRawProviderKey(definition.ModelsDevProviderId);
                definition.SingleModelId = NormalizeModel(definition.SingleModelId);
                definition.Profile = NormalizeProfile(definition.Profile);
                definition.ModelOverrides = NormalizeModelOverrides(definition.ModelOverrides);
                definition.Compaction = NormalizeAndCompleteCompactionSettings(definition.Compaction, document.RawApi.Compaction);
                return definition;
            })
            .Where(static definition => !string.IsNullOrWhiteSpace(definition.ProviderKey))
            .ToDictionary(
                static definition => definition.ProviderKey,
                static definition => definition,
                StringComparer.OrdinalIgnoreCase);

        document.RawApi.GoogleGenAI ??= new CodeAltaGoogleGenAISettingsDocument();
        document.RawApi.GoogleGenAI.Providers = document.RawApi.GoogleGenAI.Providers
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Key))
            .Select(entry =>
            {
                var definition = entry.Value ?? new CodeAltaGoogleGenAIProviderDocument();
                definition.ProviderKey = NormalizeRawProviderKey(entry.Key) ?? string.Empty;
                definition.DisplayName = NormalizeText(definition.DisplayName);
                definition.ApiKey = NormalizeText(definition.ApiKey);
                definition.ApiKeyEnv = NormalizeText(definition.ApiKeyEnv);
                definition.Project = NormalizeText(definition.Project);
                definition.Location = NormalizeText(definition.Location);
                definition.BaseUri = NormalizeText(definition.BaseUri);
                definition.ModelsDevProviderId = NormalizeRawProviderKey(definition.ModelsDevProviderId);
                definition.SingleModelId = NormalizeModel(definition.SingleModelId);
                definition.Profile = NormalizeProfile(definition.Profile);
                definition.ModelOverrides = NormalizeModelOverrides(definition.ModelOverrides);
                definition.Compaction = NormalizeAndCompleteCompactionSettings(definition.Compaction, document.RawApi.Compaction);
                return definition;
            })
            .Where(static definition => !string.IsNullOrWhiteSpace(definition.ProviderKey))
            .ToDictionary(
                static definition => definition.ProviderKey,
                static definition => definition,
                StringComparer.OrdinalIgnoreCase);
    }

    private static string? NormalizeReasoningEffortText(string? value)
        => FormatReasoningEffort(ParseReasoningEffort(value));

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string>? NormalizeList(List<string>? values)
    {
        if (values is null)
        {
            return null;
        }

        var normalized = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .ToList();
        return normalized.Count == 0 ? null : normalized;
    }

    private static Dictionary<string, string>? NormalizeDictionary(Dictionary<string, string>? values)
    {
        if (values is null)
        {
            return null;
        }

        var normalized = values
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Key))
            .ToDictionary(
                static entry => entry.Key.Trim(),
                static entry => entry.Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        return normalized.Count == 0 ? null : normalized;
    }

    private static string? NormalizeAcpAgentId(string? value)
    {
        var normalized = NormalizeText(value);
        return normalized?.ToLowerInvariant();
    }

    private static string? NormalizeRawProviderKey(string? value)
    {
        var normalized = NormalizeText(value);
        return normalized?.ToLowerInvariant();
    }

    private static string? NormalizeRawProviderKind(string? value)
    {
        var normalized = NormalizeText(value)?.ToLowerInvariant();
        return normalized switch
        {
            "openai" or "openai_compatible" or "openai-compatible" => "openai",
            "anthropic" => "anthropic",
            "google" or "google_genai" or "google-genai" or "genai" or "gemini" => "google_genai",
            _ => null,
        };
    }

    private static string? NormalizeWireApi(string providerKind, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKind);

        var normalized = NormalizeText(value)?.ToLowerInvariant();
        return providerKind switch
        {
            "openai" => normalized switch
            {
                null or "" or "chat" or "chat_completions" or "chat-completions" or "chatcompletions" or "completions" => "chat",
                "responses" or "response" => "responses",
                _ => null,
            },
            "anthropic" => normalized switch
            {
                null or "" or "messages" or "message" => "messages",
                _ => null,
            },
            "google_genai" => normalized switch
            {
                null or "" or "google_genai" or "google-genai" or "genai" or "gemini" or "generate_content" or "generate-content" => "google_genai",
                _ => null,
            },
            _ => null,
        };
    }

    private static Dictionary<string, CodeAltaRawApiProviderDocument> NormalizeUnifiedProviderDefinitions(
        Dictionary<string, CodeAltaRawApiProviderDocument>? definitions,
        CodeAltaRawApiCompactionDocument? inheritedCompaction,
        CodeAltaRawApiCompactionDocument? fallbackCompaction)
    {
        var inherited = NormalizeAndCompleteCompactionSettings(inheritedCompaction, fallbackCompaction);
        return (definitions ?? new Dictionary<string, CodeAltaRawApiProviderDocument>(StringComparer.OrdinalIgnoreCase))
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Key))
            .Select(entry =>
            {
                var definition = entry.Value ?? new CodeAltaRawApiProviderDocument();
                definition.ProviderKey = NormalizeRawProviderKey(entry.Key) ?? string.Empty;
                definition.DisplayName = NormalizeText(definition.DisplayName);
                definition.Provider = NormalizeRawProviderKind(definition.Provider)
                    ?? throw new InvalidOperationException(
                        $"providers.{entry.Key.Trim()} provider must be one of: openai, anthropic, google_genai.");
                definition.WireApi = NormalizeWireApi(definition.Provider, definition.WireApi)
                    ?? throw new InvalidOperationException(
                        $"providers.{entry.Key.Trim()} wire_api is invalid for provider '{definition.Provider}'.");
                definition.ApiKey = NormalizeText(definition.ApiKey);
                definition.ApiKeyEnv = NormalizeText(definition.ApiKeyEnv);
                definition.BaseUri = NormalizeText(definition.BaseUri);
                definition.OrganizationId = NormalizeText(definition.OrganizationId);
                definition.ProjectId = NormalizeText(definition.ProjectId);
                definition.Project = NormalizeText(definition.Project);
                definition.Location = NormalizeText(definition.Location);
                definition.ModelsDevProviderId = NormalizeRawProviderKey(definition.ModelsDevProviderId);
                definition.SingleModelId = NormalizeModel(definition.SingleModelId);
                definition.ExtraBody = NormalizeExtraBody(definition.ExtraBody);
                definition.Profile = NormalizeProfile(definition.Profile);
                definition.ModelOverrides = NormalizeModelOverrides(definition.ModelOverrides);
                definition.Compaction = NormalizeAndCompleteCompactionSettings(definition.Compaction, inherited);
                return definition;
            })
            .Where(static definition => !string.IsNullOrWhiteSpace(definition.ProviderKey))
            .ToDictionary(
                static definition => definition.ProviderKey,
                static definition => definition,
                StringComparer.OrdinalIgnoreCase);
    }

    private static CodeAltaRawApiProviderProfileDocument? NormalizeProfile(CodeAltaRawApiProviderProfileDocument? profile)
    {
        if (profile is null)
        {
            return null;
        }

        profile.MaxTokensFieldName = NormalizeText(profile.MaxTokensFieldName);
        profile.ReasoningFieldNames = NormalizeList(profile.ReasoningFieldNames)?
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return profile;
    }

    private static Dictionary<string, CodeAltaRawApiModelOverrideDocument>? NormalizeModelOverrides(
        Dictionary<string, CodeAltaRawApiModelOverrideDocument>? overrides)
    {
        if (overrides is null)
        {
            return null;
        }

        var normalized = overrides
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Key))
            .Select(static entry =>
            {
                var modelOverride = entry.Value ?? new CodeAltaRawApiModelOverrideDocument();
                modelOverride.DisplayName = NormalizeText(modelOverride.DisplayName);
                modelOverride.Description = NormalizeText(modelOverride.Description);
                return KeyValuePair.Create(entry.Key.Trim(), modelOverride);
            })
            .ToDictionary(
                static entry => entry.Key,
                static entry => entry.Value,
                StringComparer.OrdinalIgnoreCase);
        return normalized.Count == 0 ? null : normalized;
    }

    private static bool TryConvertUnifiedProviderToOpenAI(
        CodeAltaRawApiProviderDocument definition,
        out CodeAltaOpenAIProviderDocument? converted)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!string.Equals(definition.Provider, "openai", StringComparison.Ordinal))
        {
            converted = null;
            return false;
        }

        converted = new CodeAltaOpenAIProviderDocument
        {
            ProviderKey = definition.ProviderKey,
            Enabled = definition.Enabled,
            DisplayName = definition.DisplayName,
            ApiKey = definition.ApiKey,
            ApiKeyEnv = definition.ApiKeyEnv,
            BaseUri = definition.BaseUri,
            OrganizationId = definition.OrganizationId,
            ProjectId = definition.ProjectId,
            ModelsDevProviderId = definition.ModelsDevProviderId,
            SingleModelId = definition.SingleModelId,
            ExtraBody = CloneExtraBody(definition.ExtraBody),
            EnableResponses = string.Equals(definition.WireApi, "responses", StringComparison.Ordinal),
            EnableChat = string.Equals(definition.WireApi, "chat", StringComparison.Ordinal),
            DefaultResponses = definition.IsDefault && string.Equals(definition.WireApi, "responses", StringComparison.Ordinal),
            DefaultChat = definition.IsDefault && string.Equals(definition.WireApi, "chat", StringComparison.Ordinal),
            Profile = CloneProfile(definition.Profile),
            Compaction = CloneCompaction(definition.Compaction),
            ModelOverrides = CloneModelOverrides(definition.ModelOverrides),
        };
        return true;
    }

    private static bool TryConvertUnifiedProviderToAnthropic(
        CodeAltaRawApiProviderDocument definition,
        out CodeAltaAnthropicProviderDocument? converted)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!string.Equals(definition.Provider, "anthropic", StringComparison.Ordinal))
        {
            converted = null;
            return false;
        }

        converted = new CodeAltaAnthropicProviderDocument
        {
            ProviderKey = definition.ProviderKey,
            Enabled = definition.Enabled,
            DisplayName = definition.DisplayName,
            ApiKey = definition.ApiKey,
            ApiKeyEnv = definition.ApiKeyEnv,
            BaseUri = definition.BaseUri,
            ModelsDevProviderId = definition.ModelsDevProviderId,
            SingleModelId = definition.SingleModelId,
            IsDefault = definition.IsDefault,
            Profile = CloneProfile(definition.Profile),
            Compaction = CloneCompaction(definition.Compaction),
            ModelOverrides = CloneModelOverrides(definition.ModelOverrides),
        };
        return true;
    }

    private static bool TryConvertUnifiedProviderToGoogleGenAI(
        CodeAltaRawApiProviderDocument definition,
        out CodeAltaGoogleGenAIProviderDocument? converted)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!string.Equals(definition.Provider, "google_genai", StringComparison.Ordinal))
        {
            converted = null;
            return false;
        }

        converted = new CodeAltaGoogleGenAIProviderDocument
        {
            ProviderKey = definition.ProviderKey,
            Enabled = definition.Enabled,
            DisplayName = definition.DisplayName,
            ApiKey = definition.ApiKey,
            ApiKeyEnv = definition.ApiKeyEnv,
            UseVertexAI = definition.UseVertexAI,
            Project = definition.Project,
            Location = definition.Location,
            BaseUri = definition.BaseUri,
            ModelsDevProviderId = definition.ModelsDevProviderId,
            SingleModelId = definition.SingleModelId,
            IsDefault = definition.IsDefault,
            Profile = CloneProfile(definition.Profile),
            Compaction = CloneCompaction(definition.Compaction),
            ModelOverrides = CloneModelOverrides(definition.ModelOverrides),
        };
        return true;
    }

    private static AcpBackendDefinition CloneAcpBackendDefinition(AcpBackendDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new AcpBackendDefinition
        {
            AgentId = definition.AgentId,
            DisplayName = definition.DisplayName,
            Enabled = definition.Enabled,
            RegistryId = definition.RegistryId,
            Command = definition.Command,
            Arguments = definition.Arguments is null ? null : [.. definition.Arguments],
            WorkingDirectory = definition.WorkingDirectory,
            EnvironmentVariables = definition.EnvironmentVariables is null
                ? null
                : new Dictionary<string, string>(definition.EnvironmentVariables, StringComparer.OrdinalIgnoreCase),
            UseUnstable = definition.UseUnstable,
            EnableTerminal = definition.EnableTerminal,
            EnableFilesystem = definition.EnableFilesystem,
            EnableElicitation = definition.EnableElicitation,
        };
    }

    private static CodeAltaRawApiProviderDocument CloneRawApiProviderDefinition(CodeAltaRawApiProviderDocument definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new CodeAltaRawApiProviderDocument
        {
            ProviderKey = definition.ProviderKey,
            Enabled = definition.Enabled,
            DisplayName = definition.DisplayName,
            Provider = definition.Provider,
            WireApi = definition.WireApi,
            ApiKey = definition.ApiKey,
            ApiKeyEnv = definition.ApiKeyEnv,
            BaseUri = definition.BaseUri,
            IsDefault = definition.IsDefault,
            OrganizationId = definition.OrganizationId,
            ProjectId = definition.ProjectId,
            UseVertexAI = definition.UseVertexAI,
            Project = definition.Project,
            Location = definition.Location,
            ModelsDevProviderId = definition.ModelsDevProviderId,
            SingleModelId = definition.SingleModelId,
            ExtraBody = CloneExtraBody(definition.ExtraBody),
            Profile = CloneProfile(definition.Profile),
            Compaction = CloneCompaction(definition.Compaction),
            ModelOverrides = CloneModelOverrides(definition.ModelOverrides),
        };
    }

    private static CodeAltaOpenAIProviderDocument CloneOpenAIProviderDefinition(CodeAltaOpenAIProviderDocument definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new CodeAltaOpenAIProviderDocument
        {
            ProviderKey = definition.ProviderKey,
            Enabled = definition.Enabled,
            DisplayName = definition.DisplayName,
            ApiKey = definition.ApiKey,
            ApiKeyEnv = definition.ApiKeyEnv,
            BaseUri = definition.BaseUri,
            OrganizationId = definition.OrganizationId,
            ProjectId = definition.ProjectId,
            ModelsDevProviderId = definition.ModelsDevProviderId,
            SingleModelId = definition.SingleModelId,
            ExtraBody = CloneExtraBody(definition.ExtraBody),
            EnableResponses = definition.EnableResponses,
            EnableChat = definition.EnableChat,
            DefaultResponses = definition.DefaultResponses,
            DefaultChat = definition.DefaultChat,
            Profile = CloneProfile(definition.Profile),
            Compaction = CloneCompaction(definition.Compaction),
            ModelOverrides = CloneModelOverrides(definition.ModelOverrides),
        };
    }

    private static CodeAltaAnthropicProviderDocument CloneAnthropicProviderDefinition(CodeAltaAnthropicProviderDocument definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new CodeAltaAnthropicProviderDocument
        {
            ProviderKey = definition.ProviderKey,
            Enabled = definition.Enabled,
            DisplayName = definition.DisplayName,
            ApiKey = definition.ApiKey,
            ApiKeyEnv = definition.ApiKeyEnv,
            BaseUri = definition.BaseUri,
            ModelsDevProviderId = definition.ModelsDevProviderId,
            SingleModelId = definition.SingleModelId,
            IsDefault = definition.IsDefault,
            Profile = CloneProfile(definition.Profile),
            Compaction = CloneCompaction(definition.Compaction),
            ModelOverrides = CloneModelOverrides(definition.ModelOverrides),
        };
    }

    private static CodeAltaGoogleGenAIProviderDocument CloneGoogleGenAIProviderDefinition(CodeAltaGoogleGenAIProviderDocument definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new CodeAltaGoogleGenAIProviderDocument
        {
            ProviderKey = definition.ProviderKey,
            Enabled = definition.Enabled,
            DisplayName = definition.DisplayName,
            ApiKey = definition.ApiKey,
            ApiKeyEnv = definition.ApiKeyEnv,
            UseVertexAI = definition.UseVertexAI,
            Project = definition.Project,
            Location = definition.Location,
            BaseUri = definition.BaseUri,
            ModelsDevProviderId = definition.ModelsDevProviderId,
            SingleModelId = definition.SingleModelId,
            IsDefault = definition.IsDefault,
            Profile = CloneProfile(definition.Profile),
            Compaction = CloneCompaction(definition.Compaction),
            ModelOverrides = CloneModelOverrides(definition.ModelOverrides),
        };
    }

    private static CodeAltaRawApiProviderProfileDocument? CloneProfile(CodeAltaRawApiProviderProfileDocument? profile)
    {
        if (profile is null)
        {
            return null;
        }

        return new CodeAltaRawApiProviderProfileDocument
        {
            SupportsDeveloperRole = profile.SupportsDeveloperRole,
            SupportsStore = profile.SupportsStore,
            SupportsReasoningEffort = profile.SupportsReasoningEffort,
            StreamsUsage = profile.StreamsUsage,
            SupportsThoughtSignatures = profile.SupportsThoughtSignatures,
            MaxTokensFieldName = profile.MaxTokensFieldName,
            ReasoningFieldNames = profile.ReasoningFieldNames is null ? null : [.. profile.ReasoningFieldNames],
        };
    }

    private static TomlTable? NormalizeExtraBody(TomlTable? extraBody)
        => CloneExtraBody(extraBody);

    private static TomlTable? CloneExtraBody(TomlTable? extraBody)
    {
        if (extraBody is null || extraBody.Count == 0)
        {
            return null;
        }

        var clone = new TomlTable(extraBody.Kind == ObjectKind.InlineTable);
        foreach (var entry in extraBody)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            clone[entry.Key.Trim()] = CloneExtraBodyValue(entry.Value)!;
        }

        return clone.Count == 0 ? null : clone;
    }

    private static object? CloneExtraBodyValue(object? value)
    {
        return value switch
        {
            null => null,
            TomlTable table => CloneExtraBody(table),
            TomlArray array => CloneExtraBodyArray(array),
            _ => value,
        };
    }

    private static TomlArray CloneExtraBodyArray(TomlArray array)
    {
        ArgumentNullException.ThrowIfNull(array);

        var clone = new TomlArray(array.Count);
        foreach (var item in array)
        {
            clone.Add(CloneExtraBodyValue(item));
        }

        return clone;
    }

    private static CodeAltaRawApiCompactionDocument CloneCompaction(CodeAltaRawApiCompactionDocument? compaction)
    {
        return compaction is null
            ? new CodeAltaRawApiCompactionDocument()
            : new CodeAltaRawApiCompactionDocument
            {
                Enabled = compaction.Enabled,
                TriggerThreshold = compaction.TriggerThreshold,
                TargetThreshold = compaction.TargetThreshold,
                ReservedOutputTokens = compaction.ReservedOutputTokens,
                ReservedOverheadTokens = compaction.ReservedOverheadTokens,
                KeepLastUserMessage = compaction.KeepLastUserMessage,
                AllowSplitTurn = compaction.AllowSplitTurn,
                TargetContextRatioIdeal = compaction.TargetContextRatioIdeal,
                TargetContextRatioMax = compaction.TargetContextRatioMax,
                RecentSuffixTargetTokens = compaction.RecentSuffixTargetTokens,
                SummaryOutputTokens = compaction.SummaryOutputTokens,
                SummaryInputTokens = compaction.SummaryInputTokens,
                ToolResultCharsPerItem = compaction.ToolResultCharsPerItem,
                ToolResultCharsTotal = compaction.ToolResultCharsTotal,
                ReasoningCharsPerItem = compaction.ReasoningCharsPerItem,
                ReasoningCharsTotal = compaction.ReasoningCharsTotal,
                ReasoningMode = compaction.ReasoningMode,
                MaxChunkPasses = compaction.MaxChunkPasses,
                AllowOversizedAnchorReduction = compaction.AllowOversizedAnchorReduction,
                PreferRecentMessages = compaction.PreferRecentMessages,
                PreferRecentToolOutputs = compaction.PreferRecentToolOutputs,
                DropMessagesOnlyWhenSummaryInputExceedsBudget = compaction.DropMessagesOnlyWhenSummaryInputExceedsBudget,
            };
    }

    private static Dictionary<string, CodeAltaRawApiModelOverrideDocument>? CloneModelOverrides(
        Dictionary<string, CodeAltaRawApiModelOverrideDocument>? overrides)
    {
        if (overrides is null)
        {
            return null;
        }

        return overrides.ToDictionary(
            static entry => entry.Key,
            static entry => new CodeAltaRawApiModelOverrideDocument
            {
                DisplayName = entry.Value.DisplayName,
                Description = entry.Value.Description,
                ContextWindow = entry.Value.ContextWindow,
                InputTokenLimit = entry.Value.InputTokenLimit,
                OutputTokenLimit = entry.Value.OutputTokenLimit,
                MaxTokens = entry.Value.MaxTokens,
                SupportsReasoning = entry.Value.SupportsReasoning,
                SupportsToolCall = entry.Value.SupportsToolCall,
                SupportsAttachments = entry.Value.SupportsAttachments,
                SupportsStructuredOutput = entry.Value.SupportsStructuredOutput,
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private static CodeAltaRawApiCompactionDocument NormalizeAndCompleteCompactionSettings(
        CodeAltaRawApiCompactionDocument? compaction,
        CodeAltaRawApiCompactionDocument? inherited)
    {
        var merged = CloneCompaction(inherited);
        var normalized = compaction is null ? null : CloneCompaction(compaction);

        if (normalized is not null)
        {
            merged.Enabled = normalized.Enabled ?? merged.Enabled;
            merged.TriggerThreshold = normalized.TriggerThreshold ?? merged.TriggerThreshold;
            merged.TargetThreshold = normalized.TargetThreshold ?? merged.TargetThreshold;
            merged.ReservedOutputTokens = normalized.ReservedOutputTokens ?? merged.ReservedOutputTokens;
            merged.ReservedOverheadTokens = normalized.ReservedOverheadTokens ?? merged.ReservedOverheadTokens;
            merged.KeepLastUserMessage = normalized.KeepLastUserMessage ?? merged.KeepLastUserMessage;
            merged.AllowSplitTurn = normalized.AllowSplitTurn ?? merged.AllowSplitTurn;
            merged.TargetContextRatioIdeal = normalized.TargetContextRatioIdeal ?? merged.TargetContextRatioIdeal;
            merged.TargetContextRatioMax = normalized.TargetContextRatioMax ?? merged.TargetContextRatioMax;
            merged.RecentSuffixTargetTokens = normalized.RecentSuffixTargetTokens ?? merged.RecentSuffixTargetTokens;
            merged.SummaryOutputTokens = normalized.SummaryOutputTokens ?? merged.SummaryOutputTokens;
            merged.SummaryInputTokens = normalized.SummaryInputTokens ?? merged.SummaryInputTokens;
            merged.ToolResultCharsPerItem = normalized.ToolResultCharsPerItem ?? merged.ToolResultCharsPerItem;
            merged.ToolResultCharsTotal = normalized.ToolResultCharsTotal ?? merged.ToolResultCharsTotal;
            merged.ReasoningCharsPerItem = normalized.ReasoningCharsPerItem ?? merged.ReasoningCharsPerItem;
            merged.ReasoningCharsTotal = normalized.ReasoningCharsTotal ?? merged.ReasoningCharsTotal;
            merged.ReasoningMode = NormalizeCompactionReasoningMode(normalized.ReasoningMode) ?? merged.ReasoningMode;
            merged.MaxChunkPasses = normalized.MaxChunkPasses ?? merged.MaxChunkPasses;
            merged.AllowOversizedAnchorReduction = normalized.AllowOversizedAnchorReduction ?? merged.AllowOversizedAnchorReduction;
            merged.PreferRecentMessages = normalized.PreferRecentMessages ?? merged.PreferRecentMessages;
            merged.PreferRecentToolOutputs = normalized.PreferRecentToolOutputs ?? merged.PreferRecentToolOutputs;
            merged.DropMessagesOnlyWhenSummaryInputExceedsBudget = normalized.DropMessagesOnlyWhenSummaryInputExceedsBudget ?? merged.DropMessagesOnlyWhenSummaryInputExceedsBudget;
        }

        merged.Enabled ??= true;
        merged.TriggerThreshold ??= 0.85;
        merged.TargetThreshold ??= 0.50;
        merged.ReservedOutputTokens ??= 4096;
        merged.ReservedOverheadTokens ??= 2048;
        merged.KeepLastUserMessage ??= true;
        merged.AllowSplitTurn ??= true;
        merged.TargetContextRatioIdeal ??= 0.03;
        merged.TargetContextRatioMax ??= 0.10;
        merged.RecentSuffixTargetTokens ??= 20_000;
        merged.SummaryOutputTokens ??= 1_024;
        merged.SummaryInputTokens ??= 24_000;
        merged.ToolResultCharsPerItem ??= 1_200;
        merged.ToolResultCharsTotal ??= 6_000;
        merged.ReasoningCharsPerItem ??= 600;
        merged.ReasoningCharsTotal ??= 3_000;
        merged.ReasoningMode = NormalizeCompactionReasoningMode(merged.ReasoningMode) ?? "adaptive";
        merged.MaxChunkPasses ??= 4;
        merged.AllowOversizedAnchorReduction ??= true;
        merged.PreferRecentMessages ??= true;
        merged.PreferRecentToolOutputs ??= true;
        merged.DropMessagesOnlyWhenSummaryInputExceedsBudget ??= true;

        ValidateCompaction(merged);
        return merged;
    }

    private static void ValidateCompaction(CodeAltaRawApiCompactionDocument compaction)
    {
        ArgumentNullException.ThrowIfNull(compaction);

        if (compaction.TriggerThreshold is not > 0 or > 1)
        {
            throw new InvalidOperationException("raw_api compaction trigger_threshold must be > 0 and <= 1.");
        }

        if (compaction.TargetThreshold is not > 0)
        {
            throw new InvalidOperationException("raw_api compaction target_threshold must be > 0.");
        }

        if (compaction.TargetThreshold >= compaction.TriggerThreshold)
        {
            throw new InvalidOperationException("raw_api compaction target_threshold must be less than trigger_threshold.");
        }

        if (compaction.ReservedOutputTokens < 0)
        {
            throw new InvalidOperationException("raw_api compaction reserved_output_tokens must be >= 0.");
        }

        if (compaction.ReservedOverheadTokens < 0)
        {
            throw new InvalidOperationException("raw_api compaction reserved_overhead_tokens must be >= 0.");
        }

        if (compaction.TargetContextRatioIdeal is not > 0 or > 1)
        {
            throw new InvalidOperationException("raw_api compaction target_context_ratio_ideal must be > 0 and <= 1.");
        }

        if (compaction.TargetContextRatioMax is not > 0 or > 1)
        {
            throw new InvalidOperationException("raw_api compaction target_context_ratio_max must be > 0 and <= 1.");
        }

        if (compaction.TargetContextRatioIdeal > compaction.TargetContextRatioMax)
        {
            throw new InvalidOperationException("raw_api compaction target_context_ratio_ideal must be <= target_context_ratio_max.");
        }

        if (compaction.RecentSuffixTargetTokens is not > 0)
        {
            throw new InvalidOperationException("raw_api compaction recent_suffix_target_tokens must be > 0.");
        }

        if (compaction.SummaryOutputTokens is not > 0)
        {
            throw new InvalidOperationException("raw_api compaction summary_output_tokens must be > 0.");
        }

        if (compaction.SummaryInputTokens is not > 0)
        {
            throw new InvalidOperationException("raw_api compaction summary_input_tokens must be > 0.");
        }

        if (compaction.ToolResultCharsPerItem is < 0)
        {
            throw new InvalidOperationException("raw_api compaction tool_result_chars_per_item must be >= 0.");
        }

        if (compaction.ToolResultCharsTotal is < 0)
        {
            throw new InvalidOperationException("raw_api compaction tool_result_chars_total must be >= 0.");
        }

        if (compaction.ReasoningCharsPerItem is < 0)
        {
            throw new InvalidOperationException("raw_api compaction reasoning_chars_per_item must be >= 0.");
        }

        if (compaction.ReasoningCharsTotal is < 0)
        {
            throw new InvalidOperationException("raw_api compaction reasoning_chars_total must be >= 0.");
        }

        if (compaction.MaxChunkPasses is not > 0)
        {
            throw new InvalidOperationException("raw_api compaction max_chunk_passes must be > 0.");
        }

        if (NormalizeCompactionReasoningMode(compaction.ReasoningMode) is null)
        {
            throw new InvalidOperationException("raw_api compaction reasoning_mode must be one of: none, adaptive, summary_only.");
        }
    }

    private static string? NormalizeCompactionReasoningMode(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "none" => "none",
            "adaptive" => "adaptive",
            "summary_only" => "summary_only",
            _ => null,
        };
}
