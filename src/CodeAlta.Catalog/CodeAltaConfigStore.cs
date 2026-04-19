using CodeAlta.Agent;
using Tomlyn;
using Tomlyn.Model;

namespace CodeAlta.Catalog;

/// <summary>
/// Loads and persists CodeAlta TOML configuration files.
/// </summary>
public sealed class CodeAltaConfigStore
{
    private const string CodexProviderKey = "codex";
    private const string CopilotProviderKey = "copilot";

    private static readonly CodeAltaProviderCompactionDocument DefaultCompaction = new()
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
    /// Resolves the effective provider preference for a scope.
    /// </summary>
    /// <param name="providerKey">The provider key.</param>
    /// <param name="projectRoot">Optional project root for project-local overrides.</param>
    /// <returns>The merged provider preference.</returns>
    public CodeAltaProviderPreference GetEffectiveProviderPreference(string providerKey, string? projectRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        var global = LoadGlobal();
        var project = LoadProject(projectRoot);
        return ResolveProviderPreference(global, project, providerKey);
    }

    /// <summary>
    /// Persists the global provider preference.
    /// </summary>
    /// <param name="providerKey">The provider key.</param>
    /// <param name="model">The preferred model identifier.</param>
    /// <param name="reasoningEffort">The preferred reasoning effort.</param>
    public void SaveGlobalProviderPreference(
        string providerKey,
        string? model,
        AgentReasoningEffort? reasoningEffort)
    {
        var normalizedProviderKey = NormalizeProviderKey(providerKey)
            ?? throw new ArgumentException("Provider key is required.", nameof(providerKey));
        var document = LoadGlobal();
        NormalizeDocument(document);
        var normalizedModel = NormalizeModel(model);
        var normalizedReasoning = FormatReasoningEffort(reasoningEffort);

        if (normalizedModel is null && normalizedReasoning is null)
        {
            if (document.Providers.TryGetValue(normalizedProviderKey, out var existing))
            {
                existing.Model = null;
                existing.ReasoningEffort = null;
                if (CanDropProviderEntry(normalizedProviderKey, existing))
                {
                    document.Providers.Remove(normalizedProviderKey);
                }
            }
        }
        else
        {
            var definition = GetOrCreateProviderPreferenceEntry(document, normalizedProviderKey);
            definition.Model = normalizedModel;
            definition.ReasoningEffort = normalizedReasoning;
        }

        SaveDocument(_options.ConfigPath, document);
    }

    /// <summary>
    /// Resolves the effective default provider key.
    /// </summary>
    /// <param name="projectRoot">Optional project root for project-local overrides.</param>
    /// <returns>The configured default provider key, or <see langword="null"/> when none is configured.</returns>
    public string? GetEffectiveDefaultProvider(string? projectRoot = null)
    {
        var global = LoadGlobal();
        var project = LoadProject(projectRoot);
        return NormalizeProviderKey(project.Chat.DefaultProvider)
            ?? NormalizeProviderKey(global.Chat.DefaultProvider);
    }

    /// <summary>
    /// Persists the global default provider key.
    /// </summary>
    /// <param name="providerKey">The provider key, or <see langword="null"/> to clear the setting.</param>
    public void SaveGlobalDefaultProvider(string? providerKey)
    {
        var document = LoadGlobal();
        NormalizeDocument(document);
        document.Chat.DefaultProvider = NormalizeProviderKey(providerKey);
        SaveDocument(_options.ConfigPath, document);
    }

    /// <summary>
    /// Loads globally configured provider definitions.
    /// </summary>
    /// <param name="includeDisabled"><see langword="true"/> to include disabled definitions.</param>
    /// <returns>The configured provider definitions.</returns>
    public IReadOnlyList<CodeAltaProviderDocument> LoadGlobalProviderDefinitions(bool includeDisabled = false)
    {
        var document = LoadGlobal();
        NormalizeDocument(document);
        var definitions = document.Providers.Values
            .Select(CloneProviderDefinition)
            .ToDictionary(
                static definition => definition.ProviderKey,
                static definition => definition,
                StringComparer.OrdinalIgnoreCase);

        AddImplicitReservedProvider(definitions, CodexProviderKey);
        AddImplicitReservedProvider(definitions, CopilotProviderKey);

        foreach (var definition in definitions.Values)
        {
            CompleteAndValidateProviderDefinition(definition);
        }

        return definitions.Values
            .Where(definition => includeDisabled || definition.Enabled)
            .OrderBy(static definition => definition.DisplayName ?? definition.ProviderKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    internal static CodeAltaProviderPreference ResolveProviderPreference(
        CodeAltaConfigDocument global,
        CodeAltaConfigDocument? project,
        string providerKey)
    {
        ArgumentNullException.ThrowIfNull(global);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        return MergeProviderPreference(
            GetProviderSettings(global, providerKey),
            project is null ? null : GetProviderSettings(project, providerKey));
    }

    internal static CodeAltaProviderDocument? GetProviderSettings(CodeAltaConfigDocument document, string providerKey)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        var normalizedProviderKey = NormalizeProviderKey(providerKey);
        if (normalizedProviderKey is null)
        {
            return null;
        }

        if (document.Providers.TryGetValue(normalizedProviderKey, out var settings))
        {
            return settings;
        }

        return IsReservedProviderKey(normalizedProviderKey)
            ? CreateImplicitReservedProviderDefinition(normalizedProviderKey)
            : null;
    }

    private static CodeAltaProviderPreference MergeProviderPreference(
        CodeAltaProviderDocument? global,
        CodeAltaProviderDocument? project)
    {
        var model = NormalizeModel(project?.Model) ?? NormalizeModel(global?.Model);
        var reasoning = ParseReasoningEffort(project?.ReasoningEffort) ?? ParseReasoningEffort(global?.ReasoningEffort);
        return new CodeAltaProviderPreference(model, reasoning);
    }

    private static string? NormalizeModel(string? model)
        => string.IsNullOrWhiteSpace(model) ? null : model.Trim();

    private static string GetProjectConfigPath(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return Path.Combine(projectRoot, ".alta", "config.toml");
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
            ThrowIfLegacyConfigShapeDetected(content);
            var document = TomlSerializer.Deserialize<CodeAltaConfigDocument>(content)
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
        var content = TomlSerializer.Serialize(document);
        File.WriteAllText(path, content);
    }

    private static void NormalizeDocument(CodeAltaConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Chat ??= new CodeAltaChatSettingsDocument();
        document.Chat.DefaultProvider = NormalizeProviderKey(document.Chat.DefaultProvider);

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

        document.Providers = (document.Providers ?? new Dictionary<string, CodeAltaProviderDocument>(StringComparer.OrdinalIgnoreCase))
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Key))
            .Select(static entry => NormalizeProviderEntry(entry.Key, entry.Value))
            .Where(static definition => !string.IsNullOrWhiteSpace(definition.ProviderKey))
            .ToDictionary(
                static definition => definition.ProviderKey,
                static definition => definition,
                StringComparer.OrdinalIgnoreCase);
    }

    private static CodeAltaProviderDocument NormalizeProviderEntry(string key, CodeAltaProviderDocument? value)
    {
        var definition = value ?? new CodeAltaProviderDocument();
        definition.ProviderKey = NormalizeProviderKey(key) ?? string.Empty;
        definition.DisplayName = NormalizeText(definition.DisplayName);
        definition.ProviderType = NormalizeProviderType(definition.ProviderKey, definition.ProviderType);
        definition.Model = NormalizeModel(definition.Model);
        definition.ReasoningEffort = NormalizeReasoningEffortText(definition.ReasoningEffort);
        definition.ApiKey = NormalizeText(definition.ApiKey);
        definition.ApiKeyEnv = NormalizeText(definition.ApiKeyEnv);
        definition.ApiUrl = NormalizeText(definition.ApiUrl);
        definition.OrganizationId = NormalizeText(definition.OrganizationId);
        definition.ProjectId = NormalizeText(definition.ProjectId);
        definition.Project = NormalizeText(definition.Project);
        definition.Location = NormalizeText(definition.Location);
        definition.ModelsDevProviderId = NormalizeProviderKey(definition.ModelsDevProviderId);
        definition.SingleModelId = NormalizeModel(definition.SingleModelId);
        definition.ExtraBody = NormalizeExtraBody(definition.ExtraBody);
        definition.Profile = NormalizeProfile(definition.Profile);
        definition.ModelOverrides = NormalizeModelOverrides(definition.ModelOverrides);
        definition.Compaction = NormalizeAndCompleteCompactionSettings(definition.Compaction, DefaultCompaction);

        ApplyReservedProviderDefaults(definition);
        ValidateReservedProviderKey(definition);
        return definition;
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

    private static string? NormalizeProviderKey(string? value)
    {
        var normalized = NormalizeText(value);
        return normalized?.ToLowerInvariant();
    }

    private static string? NormalizeProviderType(string providerKey, string? value)
    {
        var normalized = NormalizeText(value)?.ToLowerInvariant();
        normalized = normalized switch
        {
            null => null,
            "openai" or "openai-chat" or "openai-chat-completions" or "chat" or "chat-completions" or "chat_completions" => "openai-chat",
            "openai-responses" or "responses" or "response" => "openai-responses",
            "anthropic" or "anthropic-messages" or "messages" or "message" => "anthropic",
            "google" or "google-genai" or "google_genai" or "gemini" or "genai" => "google-genai",
            "vertex" or "vertex-ai" or "google-vertex" or "google_vertex" => "vertex-ai",
            "copilot" => "copilot",
            "codex" => "codex",
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return IsReservedProviderKey(providerKey) ? providerKey : null;
        }

        return normalized;
    }

    private static void ValidateReservedProviderKey(CodeAltaProviderDocument definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (string.Equals(definition.ProviderKey, CodexProviderKey, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(definition.ProviderType, CodexProviderKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("providers.codex type must be 'codex'.");
        }

        if (string.Equals(definition.ProviderKey, CopilotProviderKey, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(definition.ProviderType, CopilotProviderKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("providers.copilot type must be 'copilot'.");
        }
    }

    private static void CompleteAndValidateProviderDefinition(CodeAltaProviderDocument definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        definition.ProviderType = NormalizeProviderType(definition.ProviderKey, definition.ProviderType)
            ?? throw new InvalidOperationException(
                $"providers.{definition.ProviderKey} type must be one of: codex, copilot, openai-chat, openai-responses, anthropic, google-genai, vertex-ai.");
        ApplyReservedProviderDefaults(definition);
        ValidateReservedProviderKey(definition);
        ValidateProviderFields(definition);
    }

    private static void ValidateProviderFields(CodeAltaProviderDocument definition)
    {
        switch (definition.ProviderType)
        {
            case "codex":
            case "copilot":
                RejectUnsupportedField(definition, "api_key", definition.ApiKey);
                RejectUnsupportedField(definition, "api_key_env", definition.ApiKeyEnv);
                RejectUnsupportedField(definition, "api_url", definition.ApiUrl);
                RejectUnsupportedField(definition, "organization_id", definition.OrganizationId);
                RejectUnsupportedField(definition, "project_id", definition.ProjectId);
                RejectUnsupportedField(definition, "project", definition.Project);
                RejectUnsupportedField(definition, "location", definition.Location);
                RejectUnsupportedField(definition, "extra_body", definition.ExtraBody);
                break;

            case "openai-chat":
            case "openai-responses":
                RejectUnsupportedField(definition, "project", definition.Project);
                RejectUnsupportedField(definition, "location", definition.Location);
                break;

            case "anthropic":
                RejectUnsupportedField(definition, "organization_id", definition.OrganizationId);
                RejectUnsupportedField(definition, "project_id", definition.ProjectId);
                RejectUnsupportedField(definition, "project", definition.Project);
                RejectUnsupportedField(definition, "location", definition.Location);
                RejectUnsupportedField(definition, "extra_body", definition.ExtraBody);
                break;

            case "google-genai":
                RejectUnsupportedField(definition, "organization_id", definition.OrganizationId);
                RejectUnsupportedField(definition, "project_id", definition.ProjectId);
                RejectUnsupportedField(definition, "project", definition.Project);
                RejectUnsupportedField(definition, "location", definition.Location);
                RejectUnsupportedField(definition, "extra_body", definition.ExtraBody);
                break;

            case "vertex-ai":
                RejectUnsupportedField(definition, "api_key", definition.ApiKey);
                RejectUnsupportedField(definition, "api_key_env", definition.ApiKeyEnv);
                RejectUnsupportedField(definition, "organization_id", definition.OrganizationId);
                RejectUnsupportedField(definition, "project_id", definition.ProjectId);
                RejectUnsupportedField(definition, "extra_body", definition.ExtraBody);
                if (definition.Enabled && string.IsNullOrWhiteSpace(definition.Project))
                {
                    throw new InvalidOperationException($"providers.{definition.ProviderKey} project is required for type 'vertex-ai'.");
                }

                if (definition.Enabled && string.IsNullOrWhiteSpace(definition.Location))
                {
                    throw new InvalidOperationException($"providers.{definition.ProviderKey} location is required for type 'vertex-ai'.");
                }

                break;
        }
    }

    private static void RejectUnsupportedField(CodeAltaProviderDocument definition, string fieldName, object? value)
    {
        var hasValue = value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            TomlTable table => table.Count > 0,
            _ => true,
        };

        if (hasValue)
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} field '{fieldName}' is not supported for type '{definition.ProviderType}'.");
        }
    }

    private static bool CanDropProviderEntry(string providerKey, CodeAltaProviderDocument definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentNullException.ThrowIfNull(definition);

        if (IsReservedProviderKey(providerKey))
        {
            return !definition.Enabled &&
                string.IsNullOrWhiteSpace(definition.DisplayName) &&
                string.IsNullOrWhiteSpace(definition.Model) &&
                string.IsNullOrWhiteSpace(definition.ReasoningEffort);
        }

        return false;
    }

    private static CodeAltaProviderDocument GetOrCreateProviderPreferenceEntry(CodeAltaConfigDocument document, string providerKey)
    {
        if (document.Providers.TryGetValue(providerKey, out var existing))
        {
            return existing;
        }

        var created = CreateImplicitReservedProviderDefinition(providerKey);
        if (created is null)
        {
            throw new InvalidOperationException($"Provider '{providerKey}' is not configurable through the provider config store.");
        }

        document.Providers[providerKey] = created;
        return created;
    }

    private static bool IsReservedProviderKey(string providerKey)
        => string.Equals(providerKey, CodexProviderKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerKey, CopilotProviderKey, StringComparison.OrdinalIgnoreCase);

    private static void ApplyReservedProviderDefaults(CodeAltaProviderDocument definition)
    {
        if (string.Equals(definition.ProviderKey, CodexProviderKey, StringComparison.OrdinalIgnoreCase))
        {
            definition.ProviderType ??= CodexProviderKey;
            definition.DisplayName ??= "Codex";
            return;
        }

        if (string.Equals(definition.ProviderKey, CopilotProviderKey, StringComparison.OrdinalIgnoreCase))
        {
            definition.ProviderType ??= CopilotProviderKey;
            definition.DisplayName ??= "GitHub Copilot";
        }
    }

    private static void AddImplicitReservedProvider(Dictionary<string, CodeAltaProviderDocument> definitions, string providerKey)
    {
        if (!definitions.ContainsKey(providerKey))
        {
            definitions[providerKey] = CreateImplicitReservedProviderDefinition(providerKey)!;
        }
    }

    private static CodeAltaProviderDocument? CreateImplicitReservedProviderDefinition(string providerKey)
    {
        var normalizedProviderKey = NormalizeProviderKey(providerKey);
        if (string.IsNullOrWhiteSpace(normalizedProviderKey) || !IsReservedProviderKey(normalizedProviderKey))
        {
            return null;
        }

        return new CodeAltaProviderDocument
        {
            ProviderKey = normalizedProviderKey,
            Enabled = true,
            ProviderType = normalizedProviderKey,
            DisplayName = string.Equals(normalizedProviderKey, CodexProviderKey, StringComparison.OrdinalIgnoreCase)
                ? "Codex"
                : "GitHub Copilot",
            Compaction = NormalizeAndCompleteCompactionSettings(null, DefaultCompaction),
        };
    }

    private static CodeAltaProviderProfileDocument? NormalizeProfile(CodeAltaProviderProfileDocument? profile)
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

    private static Dictionary<string, CodeAltaProviderModelOverrideDocument>? NormalizeModelOverrides(
        Dictionary<string, CodeAltaProviderModelOverrideDocument>? overrides)
    {
        if (overrides is null)
        {
            return null;
        }

        var normalized = overrides
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Key))
            .Select(static entry =>
            {
                var modelOverride = entry.Value ?? new CodeAltaProviderModelOverrideDocument();
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

    private static CodeAltaProviderDocument CloneProviderDefinition(CodeAltaProviderDocument definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new CodeAltaProviderDocument
        {
            ProviderKey = definition.ProviderKey,
            Enabled = definition.Enabled,
            DisplayName = definition.DisplayName,
            ProviderType = definition.ProviderType,
            Model = definition.Model,
            ReasoningEffort = definition.ReasoningEffort,
            ApiKey = definition.ApiKey,
            ApiKeyEnv = definition.ApiKeyEnv,
            ApiUrl = definition.ApiUrl,
            OrganizationId = definition.OrganizationId,
            ProjectId = definition.ProjectId,
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

    private static CodeAltaProviderProfileDocument? CloneProfile(CodeAltaProviderProfileDocument? profile)
    {
        if (profile is null)
        {
            return null;
        }

        return new CodeAltaProviderProfileDocument
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

    private static CodeAltaProviderCompactionDocument CloneCompaction(CodeAltaProviderCompactionDocument? compaction)
    {
        return compaction is null
            ? new CodeAltaProviderCompactionDocument()
            : new CodeAltaProviderCompactionDocument
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

    private static Dictionary<string, CodeAltaProviderModelOverrideDocument>? CloneModelOverrides(
        Dictionary<string, CodeAltaProviderModelOverrideDocument>? overrides)
    {
        if (overrides is null)
        {
            return null;
        }

        return overrides.ToDictionary(
            static entry => entry.Key,
            static entry => new CodeAltaProviderModelOverrideDocument
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

    private static CodeAltaProviderCompactionDocument NormalizeAndCompleteCompactionSettings(
        CodeAltaProviderCompactionDocument? compaction,
        CodeAltaProviderCompactionDocument? inherited)
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

    private static void ValidateCompaction(CodeAltaProviderCompactionDocument compaction)
    {
        ArgumentNullException.ThrowIfNull(compaction);

        if (compaction.TriggerThreshold is not > 0 or > 1)
        {
            throw new InvalidOperationException("provider compaction trigger_threshold must be > 0 and <= 1.");
        }

        if (compaction.TargetThreshold is not > 0)
        {
            throw new InvalidOperationException("provider compaction target_threshold must be > 0.");
        }

        if (compaction.TargetThreshold >= compaction.TriggerThreshold)
        {
            throw new InvalidOperationException("provider compaction target_threshold must be less than trigger_threshold.");
        }

        if (compaction.ReservedOutputTokens < 0)
        {
            throw new InvalidOperationException("provider compaction reserved_output_tokens must be >= 0.");
        }

        if (compaction.ReservedOverheadTokens < 0)
        {
            throw new InvalidOperationException("provider compaction reserved_overhead_tokens must be >= 0.");
        }

        if (compaction.TargetContextRatioIdeal is not > 0 or > 1)
        {
            throw new InvalidOperationException("provider compaction target_context_ratio_ideal must be > 0 and <= 1.");
        }

        if (compaction.TargetContextRatioMax is not > 0 or > 1)
        {
            throw new InvalidOperationException("provider compaction target_context_ratio_max must be > 0 and <= 1.");
        }

        if (compaction.TargetContextRatioIdeal > compaction.TargetContextRatioMax)
        {
            throw new InvalidOperationException("provider compaction target_context_ratio_ideal must be <= target_context_ratio_max.");
        }

        if (compaction.RecentSuffixTargetTokens is not > 0)
        {
            throw new InvalidOperationException("provider compaction recent_suffix_target_tokens must be > 0.");
        }

        if (compaction.SummaryOutputTokens is not > 0)
        {
            throw new InvalidOperationException("provider compaction summary_output_tokens must be > 0.");
        }

        if (compaction.SummaryInputTokens is not > 0)
        {
            throw new InvalidOperationException("provider compaction summary_input_tokens must be > 0.");
        }

        if (compaction.ToolResultCharsPerItem is < 0)
        {
            throw new InvalidOperationException("provider compaction tool_result_chars_per_item must be >= 0.");
        }

        if (compaction.ToolResultCharsTotal is < 0)
        {
            throw new InvalidOperationException("provider compaction tool_result_chars_total must be >= 0.");
        }

        if (compaction.ReasoningCharsPerItem is < 0)
        {
            throw new InvalidOperationException("provider compaction reasoning_chars_per_item must be >= 0.");
        }

        if (compaction.ReasoningCharsTotal is < 0)
        {
            throw new InvalidOperationException("provider compaction reasoning_chars_total must be >= 0.");
        }

        if (compaction.MaxChunkPasses is not > 0)
        {
            throw new InvalidOperationException("provider compaction max_chunk_passes must be > 0.");
        }

        if (NormalizeCompactionReasoningMode(compaction.ReasoningMode) is null)
        {
            throw new InvalidOperationException("provider compaction reasoning_mode must be one of: none, adaptive, summary_only.");
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

    private static void ThrowIfLegacyConfigShapeDetected(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var normalized = content.Replace("\r", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        string[] legacyMarkers =
        [
            "[backends",
            "[raw_api",
            "wire_api =",
            "base_uri =",
            "use_vertex_ai =",
            "default_responses =",
            "default_chat =",
            "is_default =",
            "\nprovider =",
        ];

        if (legacyMarkers.Any(normalized.Contains) ||
            normalized.StartsWith("provider =", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Legacy CodeAlta config keys are no longer supported. Migrate to [chat].default_provider, providers.<key>.type, and providers.<key>.api_url.");
        }
    }
}
