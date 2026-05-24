using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime.Compaction;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Text;

namespace CodeAlta.Catalog;

/// <summary>
/// Describes whether CodeAlta TOML configuration content can be loaded.
/// </summary>
/// <param name="IsValid"><see langword="true"/> when the configuration can be loaded.</param>
/// <param name="Message">The diagnostic message, or <see langword="null"/> when valid.</param>
/// <param name="Line">The 1-based diagnostic line when available.</param>
/// <param name="Column">The 1-based diagnostic column when available.</param>
public sealed record CodeAltaConfigValidationResult(bool IsValid, string? Message, int? Line, int? Column)
{
    /// <summary>
    /// Gets a successful validation result.
    /// </summary>
    public static CodeAltaConfigValidationResult Valid { get; } = new(true, null, null, null);
}

/// <summary>
/// Loads and persists CodeAlta TOML configuration files.
/// </summary>
public sealed class CodeAltaConfigStore
{
    private const string DefaultGlobalConfigResourceName = "CodeAlta.Catalog.DefaultConfig.config.toml";
    private const string CodexProviderKey = "codex";
    private const string CopilotProviderKey = "copilot";
    private const string CodexSubscriptionProviderType = "codex";
    private const string CopilotDirectProviderType = "copilot";
    private const string XaiDirectProviderType = "xai";
    private const string CopilotDirectDefaultDisplayName = "Copilot";
    private const string CopilotDirectDefaultAuthSource = "github_device_flow";
    private const string CopilotDirectDefaultModelDiscovery = "copilot_endpoint_with_static_fallback";
    private const string XaiDirectDefaultDisplayName = "xAI Grok";
    private const string XaiDirectDefaultAuthSource = "xai_browser_oauth";
    private const string XaiDirectDefaultModelDiscovery = "xai_endpoint_with_static_fallback";
    private const string CodexSubscriptionDefaultDisplayName = "Codex";
    private const string CodexSubscriptionDefaultApiUrl = "https://chatgpt.com/backend-api/codex";
    private const string CodexSubscriptionDefaultAuthSource = "codealta_oauth";
    private const string CodexSubscriptionDefaultTextVerbosity = "medium";
    private const string CodexSubscriptionDefaultModelDiscovery = "static";
    private const string CodexSubscriptionDefaultResponseTransport = "websocket_with_http_fallback";
    private const string CodexSubscriptionDefaultInstallationIdSource = "codealta_state";
    private const int CodexSubscriptionDefaultMaxConcurrentRequests = 16;

    private static readonly CodeAltaProviderCompactionDocument DefaultCompaction = new()
    {
        Enabled = LocalAgentCompactionSettings.DefaultEnabled,
        Ratio = LocalAgentCompactionSettings.DefaultRatio,
        SummaryOutputRatio = LocalAgentCompactionSettings.DefaultSummaryOutputRatio,
        PostCompactionTargetRatio = LocalAgentCompactionSettings.DefaultPostCompactionTargetRatio,
        SummaryShareOfTarget = LocalAgentCompactionSettings.DefaultSummaryShareOfTarget,
        FileContextShareOfSummaryTarget = LocalAgentCompactionSettings.DefaultFileContextShareOfSummaryTarget,
        KeepLastUserMessage = LocalAgentCompactionSettings.DefaultKeepLastUserMessage,
        AllowSplitTurn = LocalAgentCompactionSettings.DefaultAllowSplitTurn,
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
    /// Gets the global user configuration file path.
    /// </summary>
    public string ConfigPath => _options.ConfigPath;

    /// <summary>
    /// Loads the global user configuration.
    /// </summary>
    /// <returns>The parsed configuration document.</returns>
    public CodeAltaConfigDocument LoadGlobal()
        => LoadDocument(_options.ConfigPath);

    /// <summary>
    /// Creates the global user configuration from the bundled first-run template when it is missing.
    /// </summary>
    /// <returns><see langword="true"/> when a new config file was written.</returns>
    public bool EnsureGlobalConfigExists()
    {
        if (File.Exists(_options.ConfigPath))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_options.ConfigPath)!);
        File.WriteAllText(_options.ConfigPath, GetDefaultGlobalConfigContent());
        return true;
    }

    /// <summary>
    /// Loads the global user configuration TOML content.
    /// </summary>
    /// <returns>The existing config content, or the bundled first-run template when the config file is missing.</returns>
    /// <exception cref="IOException">Thrown when the config file or default template cannot be read.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the bundled first-run template is unavailable.</exception>
    public string LoadGlobalConfigContent()
        => File.Exists(_options.ConfigPath)
            ? File.ReadAllText(_options.ConfigPath)
            : GetDefaultGlobalConfigContent();

    /// <summary>
    /// Validates and saves the global user configuration TOML content without reformatting it.
    /// </summary>
    /// <param name="content">The complete TOML content to save.</param>
    /// <exception cref="InvalidDataException">Thrown when the supplied configuration cannot be loaded.</exception>
    /// <exception cref="IOException">Thrown when the config file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the config file cannot be written because access is denied.</exception>
    public void SaveGlobalConfigContent(string? content)
    {
        var validation = ValidateGlobalConfigContent(content, _options.ConfigPath);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(validation.Message ?? "CodeAlta configuration is invalid.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_options.ConfigPath)!);
        File.WriteAllText(_options.ConfigPath, content ?? string.Empty);
    }

    /// <summary>
    /// Gets the bundled first-run global configuration template.
    /// </summary>
    /// <returns>The default global config TOML content.</returns>
    public static string GetDefaultGlobalConfigContent()
    {
        using var stream = typeof(CodeAltaConfigStore).Assembly.GetManifestResourceStream(DefaultGlobalConfigResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{DefaultGlobalConfigResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Validates global CodeAlta TOML configuration content without starting backends or sessions.
    /// </summary>
    /// <param name="content">The TOML content to validate.</param>
    /// <param name="sourcePath">Optional source path used in diagnostics.</param>
    /// <returns>The validation result, including the first diagnostic location when available.</returns>
    public static CodeAltaConfigValidationResult ValidateGlobalConfigContent(string? content, string? sourcePath = null)
    {
        try
        {
            var document = ParseDocument(content ?? string.Empty, sourcePath);
            ValidateGlobalDocument(document);
            return CodeAltaConfigValidationResult.Valid;
        }
        catch (Exception ex) when (IsConfigLoadException(ex))
        {
            return CreateValidationFailure(ex);
        }
    }

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
    /// Persists a global plugin enablement override.
    /// </summary>
    /// <param name="pluginId">The built-in plugin id or source plugin package id.</param>
    /// <param name="enabled">A value indicating whether the plugin should be enabled.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="pluginId"/> is empty.</exception>
    public void SaveGlobalPluginEnabled(string pluginId, bool enabled)
    {
        var document = LoadGlobal();
        SavePluginEnabled(_options.ConfigPath, document, pluginId, enabled);
    }

    /// <summary>
    /// Persists a project-local plugin enablement override.
    /// </summary>
    /// <param name="projectRoot">The project root directory.</param>
    /// <param name="pluginId">The source plugin package id.</param>
    /// <param name="enabled">A value indicating whether the plugin should be enabled.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="projectRoot"/> or <paramref name="pluginId"/> is empty.</exception>
    public void SaveProjectPluginEnabled(string projectRoot, string pluginId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var path = GetProjectConfigPath(projectRoot);
        var document = LoadDocument(path);
        SavePluginEnabled(path, document, pluginId, enabled);
    }

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
            if (document.Providers?.TryGetValue(normalizedProviderKey, out var existing) == true)
            {
                existing.Model = null;
                existing.ReasoningEffort = null;
                PruneProviderPreferenceFields(normalizedProviderKey, existing);
                if (CanDropProviderEntry(normalizedProviderKey, existing))
                {
                    document.Providers!.Remove(normalizedProviderKey);
                }
            }
        }
        else
        {
            var definition = GetOrCreateProviderPreferenceEntry(document, normalizedProviderKey);
            definition.Model = normalizedModel;
            definition.ReasoningEffort = normalizedReasoning;
            PruneProviderPreferenceFields(normalizedProviderKey, definition);
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
        return NormalizeProviderKey(project.Chat?.DefaultProvider)
            ?? NormalizeProviderKey(global.Chat?.DefaultProvider);
    }

    /// <summary>
    /// Persists the global default provider key.
    /// </summary>
    /// <param name="providerKey">The provider key, or <see langword="null"/> to clear the setting.</param>
    public void SaveGlobalDefaultProvider(string? providerKey)
    {
        var document = LoadGlobal();
        NormalizeDocument(document);
        document.Chat ??= new CodeAltaChatSettingsDocument();
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
        var definitions = (document.Providers ?? new Dictionary<string, CodeAltaProviderDocument>(StringComparer.OrdinalIgnoreCase))
            .Values
            .Select(CloneProviderDefinition)
            .ToDictionary(
                static definition => definition.ProviderKey,
                static definition => definition,
                StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions.Values)
        {
            CompleteAndValidateProviderDefinition(definition);
        }

        return definitions.Values
            .Where(definition => includeDisabled || definition.Enabled != false)
            .OrderBy(static definition => definition.DisplayName ?? definition.ProviderKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Saves the complete set of global provider definitions.
    /// </summary>
    /// <param name="definitions">The provider definitions to persist.</param>
    public void SaveGlobalProviderDefinitions(IEnumerable<CodeAltaProviderDocument> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var document = LoadGlobal();
        NormalizeDocument(document);

        var normalizedDefinitions = definitions
            .Select(CloneProviderDefinition)
            .Select(static definition =>
            {
                definition.ProviderKey = NormalizeProviderKey(definition.ProviderKey) ?? string.Empty;
                return definition;
            })
            .Where(static definition => !string.IsNullOrWhiteSpace(definition.ProviderKey))
            .ToDictionary(
                static definition => definition.ProviderKey,
                static definition => definition,
                StringComparer.OrdinalIgnoreCase);

        foreach (var definition in normalizedDefinitions.Values)
        {
            NormalizeProviderEntry(definition.ProviderKey, definition);
            ValidateProviderCredentialsForSave(definition);
        }

        document.Providers = normalizedDefinitions.Count == 0
            ? null
            : normalizedDefinitions;

        var defaultProvider = NormalizeProviderKey(document.Chat?.DefaultProvider);
        if (!string.IsNullOrWhiteSpace(defaultProvider) &&
            !LoadEnabledProviderKeys(normalizedDefinitions.Values).Contains(defaultProvider))
        {
            document.Chat ??= new CodeAltaChatSettingsDocument();
            document.Chat.DefaultProvider = LoadEnabledProviderKeys(normalizedDefinitions.Values).FirstOrDefault();
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
        return (document.Acp?.Agents ?? new Dictionary<string, AcpBackendDefinition>(StringComparer.OrdinalIgnoreCase))
            .Values
            .Where(definition => includeDisabled || definition.Enabled != false)
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
        return document.Acp?.Agents?.TryGetValue(agentId.Trim(), out var definition) == true
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
        document.Acp ??= new CodeAltaAcpSettingsDocument();
        document.Acp.Agents ??= new Dictionary<string, AcpBackendDefinition>(StringComparer.OrdinalIgnoreCase);
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
        var removed = document.Acp?.Agents?.Remove(agentId.Trim()) == true;
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
        if (document.Acp?.Agents?.Count is not > 0)
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
        return document.Acp?.Agents?.ContainsKey(agentId.Trim()) == true;
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
            foreach (var installedDefinition in installedDefinitions.Where(static definition => definition.Enabled != false))
            {
                effective[installedDefinition.AgentId] = CloneAcpBackendDefinition(installedDefinition);
            }
        }

        foreach (var configuredDefinition in LoadGlobalAcpBackendDefinitions(includeDisabled: false))
        {
            effective[configuredDefinition.AgentId] = CloneAcpBackendDefinition(configuredDefinition);
        }

        return effective.Values
            .Where(static definition => definition.Enabled != false)
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

        if (document.Providers?.TryGetValue(normalizedProviderKey, out var settings) == true)
        {
            return settings;
        }

        return null;
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
            return ParseDocument(content, path);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or IOException or TomlException)
        {
            throw new InvalidDataException($"Failed to parse CodeAlta config '{path}'.", ex);
        }
    }

    private static CodeAltaConfigDocument ParseDocument(string content, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new CodeAltaConfigDocument();
        }

        ThrowIfLegacyConfigShapeDetected(content, sourcePath);
        var options = string.IsNullOrWhiteSpace(sourcePath)
            ? TomlSerializerOptions.Default
            : TomlSerializerOptions.Default with { SourceName = sourcePath };
        var document = TomlSerializer.Deserialize<CodeAltaConfigDocument>(content, options)
            ?? new CodeAltaConfigDocument();
        NormalizeDocument(document);
        return document;
    }

    private static void ValidateGlobalDocument(CodeAltaConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        NormalizeDocument(document);
        var definitions = (document.Providers ?? new Dictionary<string, CodeAltaProviderDocument>(StringComparer.OrdinalIgnoreCase))
            .Values
            .Select(CloneProviderDefinition)
            .ToDictionary(
                static definition => definition.ProviderKey,
                static definition => definition,
                StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions.Values)
        {
            CompleteAndValidateProviderDefinition(definition);
        }
    }

    private static bool IsConfigLoadException(Exception ex)
        => ex is InvalidOperationException or FormatException or IOException or TomlException;

    private static CodeAltaConfigValidationResult CreateValidationFailure(Exception exception)
    {
        var tomlException = FindTomlException(exception);
        var message = exception.GetBaseException().Message;
        if (tomlException is not null)
        {
            message = tomlException.Message;
        }

        return new CodeAltaConfigValidationResult(
            IsValid: false,
            Message: message,
            Line: tomlException?.Line,
            Column: tomlException?.Column);
    }

    private static TomlException? FindTomlException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is TomlException tomlException)
            {
                return tomlException;
            }
        }

        return null;
    }

    private static void SaveDocument(string path, CodeAltaConfigDocument document)
    {
        NormalizeDocument(document);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = TomlSerializer.Serialize(document);
        File.WriteAllText(path, content);
    }

    private static void SavePluginEnabled(string path, CodeAltaConfigDocument document, string pluginId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        NormalizeDocument(document);
        document.Plugins ??= new Dictionary<string, CodeAltaPluginSettingsDocument>(StringComparer.OrdinalIgnoreCase);
        document.Plugins[pluginId.Trim()] = new CodeAltaPluginSettingsDocument { Enabled = enabled };
        SaveDocument(path, document);
    }

    private static void NormalizeDocument(CodeAltaConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Chat is not null)
        {
            document.Chat.DefaultProvider = NormalizeProviderKey(document.Chat.DefaultProvider);
            if (document.Chat.DefaultProvider is null)
            {
                document.Chat = null;
            }
        }

        if (document.Acp?.Agents is not null)
        {
            var agents = document.Acp.Agents
                .Where(static entry => !string.IsNullOrWhiteSpace(entry.Key))
                .Select(static entry => NormalizeAcpEntry(entry.Key, entry.Value))
                .Where(static definition => !string.IsNullOrWhiteSpace(definition.AgentId) && CanPersistAcpEntry(definition))
                .ToDictionary(
                    static definition => definition.AgentId,
                    static definition => definition,
                    StringComparer.OrdinalIgnoreCase);

            document.Acp.Agents = agents.Count == 0 ? null : agents;
        }

        if (document.Acp?.Agents is null)
        {
            document.Acp = null;
        }

        if (document.Providers is not null)
        {
            var providers = document.Providers
                .Where(static entry => !string.IsNullOrWhiteSpace(entry.Key))
                .Select(static entry => NormalizeProviderEntry(entry.Key, entry.Value))
                .Where(static definition => !string.IsNullOrWhiteSpace(definition.ProviderKey) && CanPersistProviderEntry(definition))
                .ToDictionary(
                    static definition => definition.ProviderKey,
                    static definition => definition,
                    StringComparer.OrdinalIgnoreCase);

            document.Providers = providers.Count == 0 ? null : providers;
        }

        if (document.Plugins is not null)
        {
            var plugins = document.Plugins
                .Where(static entry => !string.IsNullOrWhiteSpace(entry.Key))
                .ToDictionary(
                    static entry => entry.Key.Trim(),
                    static entry => entry.Value ?? new CodeAltaPluginSettingsDocument(),
                    StringComparer.OrdinalIgnoreCase);

            document.Plugins = plugins.Count == 0 ? null : plugins;
        }
    }

    private static AcpBackendDefinition NormalizeAcpEntry(string key, AcpBackendDefinition? value)
    {
        var definition = value ?? new AcpBackendDefinition();
        definition.AgentId = NormalizeAcpAgentId(definition.AgentId) ?? key.Trim();
        definition.DisplayName = NormalizeText(definition.DisplayName);
        definition.RegistryId = NormalizeAcpAgentId(definition.RegistryId);
        definition.Command = NormalizeText(definition.Command);
        definition.WorkingDirectory = NormalizeText(definition.WorkingDirectory);
        definition.Arguments = NormalizeList(definition.Arguments);
        definition.EnvironmentVariables = NormalizeDictionary(definition.EnvironmentVariables);
        PruneAcpDefaults(definition);
        return definition;
    }

    private static CodeAltaProviderDocument NormalizeProviderEntry(string key, CodeAltaProviderDocument? value)
    {
        var definition = value ?? new CodeAltaProviderDocument();
        definition.ProviderKey = NormalizeProviderEntryKey(key, definition) ?? string.Empty;
        definition.DisplayName = NormalizeText(definition.DisplayName);
        definition.ProviderType = NormalizeProviderType(definition.ProviderKey, definition.ProviderType);
        definition.Model = NormalizeModel(definition.Model);
        definition.ReasoningEffort = NormalizeReasoningEffortText(definition.ReasoningEffort);
        definition.ApiKey = NormalizeText(definition.ApiKey);
        definition.ApiKeyEnv = NormalizeText(definition.ApiKeyEnv);
        definition.ApiUrl = NormalizeText(definition.ApiUrl);
        definition.GitHubEnterpriseUrl = NormalizeText(definition.GitHubEnterpriseUrl);
        definition.GitHubTokenEnv = NormalizeText(definition.GitHubTokenEnv);
        definition.CopilotTokenEnv = NormalizeText(definition.CopilotTokenEnv);
        if (definition.ProtocolTrace == false)
        {
            definition.ProtocolTrace = null;
        }

        definition.AuthSource = NormalizeCodexSubscriptionAuthSource(definition.AuthSource);
        definition.AccountId = NormalizeText(definition.AccountId);
        definition.TextVerbosity = NormalizeCodexSubscriptionTextVerbosity(definition.TextVerbosity);
        definition.ModelDiscovery = NormalizeCodexSubscriptionModelDiscovery(definition.ModelDiscovery);
        definition.ResponseTransport = NormalizeCodexSubscriptionResponseTransport(definition.ResponseTransport);
        definition.InstallationIdSource = NormalizeCodexSubscriptionInstallationIdSource(definition.InstallationIdSource);
        definition.OrganizationId = NormalizeText(definition.OrganizationId);
        definition.ProjectId = NormalizeText(definition.ProjectId);
        definition.Project = NormalizeText(definition.Project);
        definition.Location = NormalizeText(definition.Location);
        definition.ModelsDevProviderId = NormalizeProviderKey(definition.ModelsDevProviderId);
        definition.SingleModelId = NormalizeModel(definition.SingleModelId);
        definition.ExtraBody = NormalizeExtraBody(definition.ExtraBody);
        definition.Profile = NormalizeProfile(definition.Profile);
        definition.ModelOverrides = NormalizeModelOverrides(definition.ModelOverrides);
        definition.Compaction = NormalizeCompaction(definition.Compaction);
        CompleteAndValidateProviderDefinition(CloneProviderDefinition(definition));
        PruneProviderDefaults(definition);
        return definition;
    }

    private static string? NormalizeReasoningEffortText(string? value)
        => FormatReasoningEffort(ParseReasoningEffort(value));

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeDomain(string? value)
    {
        var normalized = NormalizeText(value);
        if (normalized is null)
        {
            return null;
        }

        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = "https://" + normalized;
        }

        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : null;
    }

    private static string? NormalizeCodexSubscriptionAuthSource(string? value)
        => NormalizeText(value)?.ToLowerInvariant() switch
        {
            null => null,
            "codealta_oauth" => "codealta_oauth",
            "codex_auth_import" => "codex_auth_import",
            "codex_auth_file_readonly" => "codex_auth_file_readonly",
            "external_token_command" => "external_token_command",
            var normalized => normalized,
        };

    private static string? NormalizeCodexSubscriptionTextVerbosity(string? value)
        => NormalizeText(value)?.ToLowerInvariant() switch
        {
            null => null,
            "low" => "low",
            "medium" => "medium",
            "high" => "high",
            var normalized => normalized,
        };

    private static string? NormalizeCodexSubscriptionModelDiscovery(string? value)
        => NormalizeText(value)?.ToLowerInvariant() switch
        {
            null => null,
            "codex_endpoint_with_static_fallback" => "codex_endpoint_with_static_fallback",
            "codex_endpoint" => "codex_endpoint",
            "static" => "static",
            var normalized => normalized,
        };

    private static string? NormalizeCodexSubscriptionResponseTransport(string? value)
        => NormalizeText(value)?.ToLowerInvariant() switch
        {
            null => null,
            "auto" or "websocket" or "websocket_with_fallback" or "websocket_with_http_fallback" => "websocket_with_http_fallback",
            "http" or "sse" => "http",
            var normalized => normalized,
        };

    private static string? NormalizeCodexSubscriptionInstallationIdSource(string? value)
        => NormalizeText(value)?.ToLowerInvariant() switch
        {
            null => null,
            "codealta_state" => "codealta_state",
            "codex_home_import" => "codex_home_import",
            "codex_home_readonly" => "codex_home_readonly",
            var normalized => normalized,
        };

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

    private static void PruneAcpDefaults(AcpBackendDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.Enabled == AcpBackendDefinition.DefaultEnabled)
        {
            definition.Enabled = null;
        }

        if (definition.UseUnstable == AcpBackendDefinition.DefaultUseUnstable)
        {
            definition.UseUnstable = null;
        }

        if (definition.EnableTerminal == AcpBackendDefinition.DefaultEnableTerminal)
        {
            definition.EnableTerminal = null;
        }

        if (definition.EnableFilesystem == AcpBackendDefinition.DefaultEnableFilesystem)
        {
            definition.EnableFilesystem = null;
        }

        if (definition.EnableElicitation == AcpBackendDefinition.DefaultEnableElicitation)
        {
            definition.EnableElicitation = null;
        }
    }

    private static bool CanPersistAcpEntry(AcpBackendDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return !string.IsNullOrWhiteSpace(definition.DisplayName) ||
               definition.Enabled is not null ||
               !string.IsNullOrWhiteSpace(definition.RegistryId) ||
               !string.IsNullOrWhiteSpace(definition.Command) ||
               definition.Arguments is { Count: > 0 } ||
               !string.IsNullOrWhiteSpace(definition.WorkingDirectory) ||
               definition.EnvironmentVariables is { Count: > 0 } ||
               definition.UseUnstable is not null ||
               definition.EnableTerminal is not null ||
               definition.EnableFilesystem is not null ||
               definition.EnableElicitation is not null;
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

    private static string? NormalizeProviderEntryKey(string key, CodeAltaProviderDocument definition)
    {
        var normalizedKey = NormalizeProviderKey(key);
        if (normalizedKey is null)
        {
            return null;
        }

        var normalizedType = NormalizeText(definition.ProviderType)?.ToLowerInvariant();
        if (normalizedKey is "openai-codex-subscription" or "codex-subscription" or "codex_subscription")
        {
            return "codex";
        }

        if (normalizedKey is "github-copilot-direct" or "github_copilot_direct" or "copilot-direct")
        {
            return "copilot";
        }

        return normalizedKey;
    }

    private static string? NormalizeProviderType(string providerKey, string? value)
    {
        var normalized = NormalizeText(value)?.ToLowerInvariant();
        normalized = normalized switch
        {
            null => null,
            "openai" or "openai-chat" or "openai-chat-completions" or "chat" or "chat-completions" or "chat_completions" => "openai-chat",
            "openai-responses" or "responses" or "response" => "openai-responses",
            "azure-openai" or "azure-openai-chat" or "azure_openai" or "azure_openai_chat" or "aoai" => "azure-openai",
            "openai-codex-subscription" or "codex-subscription" or "codex_subscription" => CodexSubscriptionProviderType,
            "github-copilot-direct" or "copilot-direct" or "github-copilot" or "github-copilot-local" => CopilotDirectProviderType,
            "anthropic" or "anthropic-messages" or "messages" or "message" => "anthropic",
            "google" or "google-genai" or "google_genai" or "gemini" or "genai" => "google-genai",
            "vertex" or "vertex-ai" or "google-vertex" or "google_vertex" => "vertex-ai",
            "codex" => CodexSubscriptionProviderType,
            "copilot" => CopilotDirectProviderType,
            "xai" or "xai-grok" or "grok" or "x-ai" => XaiDirectProviderType,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Equals(providerKey, CodexProviderKey, StringComparison.OrdinalIgnoreCase)
                ? CodexSubscriptionProviderType
                : string.Equals(providerKey, CopilotProviderKey, StringComparison.OrdinalIgnoreCase)
                    ? CopilotDirectProviderType
                    : null;
        }

        return normalized;
    }

    private static void CompleteAndValidateProviderDefinition(CodeAltaProviderDocument definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        definition.Enabled ??= GetDefaultProviderEnabled(definition.ProviderKey);
        definition.ProviderType = NormalizeProviderType(definition.ProviderKey, definition.ProviderType)
            ?? throw new InvalidOperationException(
                $"providers.{definition.ProviderKey} type must be one of: codex, copilot, openai-chat, openai-responses, azure-openai, anthropic, google-genai, vertex-ai, xai.");
        definition.Compaction = NormalizeAndCompleteCompactionSettings(definition.Compaction, DefaultCompaction);
        ApplyCodexSubscriptionDefaults(definition);
        ApplyCopilotDirectDefaults(definition);
        ApplyXaiDirectDefaults(definition);
        ValidateProviderFields(definition);
    }

    private static void ApplyCodexSubscriptionDefaults(CodeAltaProviderDocument definition)
    {
        if (!string.Equals(definition.ProviderType, CodexSubscriptionProviderType, StringComparison.Ordinal))
        {
            return;
        }

        definition.DisplayName ??= CodexSubscriptionDefaultDisplayName;
        definition.ApiUrl ??= CodexSubscriptionDefaultApiUrl;
        definition.AuthSource ??= CodexSubscriptionDefaultAuthSource;
        definition.MaxConcurrentRequests ??= CodexSubscriptionDefaultMaxConcurrentRequests;
        definition.TextVerbosity ??= CodexSubscriptionDefaultTextVerbosity;
        definition.IncludeEncryptedReasoning ??= true;
        definition.ModelDiscovery ??= CodexSubscriptionDefaultModelDiscovery;
        definition.ResponseTransport ??= CodexSubscriptionDefaultResponseTransport;
        definition.SendResponsesBetaHeader ??= true;
        definition.SendInstallationId ??= false;
        definition.InstallationIdSource ??= CodexSubscriptionDefaultInstallationIdSource;
        definition.Experimental ??= false;
    }

    private static void ApplyCopilotDirectDefaults(CodeAltaProviderDocument definition)
    {
        if (!string.Equals(definition.ProviderType, CopilotDirectProviderType, StringComparison.Ordinal))
        {
            return;
        }

        definition.DisplayName ??= CopilotDirectDefaultDisplayName;
        definition.AuthSource ??= CopilotDirectDefaultAuthSource;
        definition.ModelDiscovery ??= CopilotDirectDefaultModelDiscovery;
        definition.EnableModelPolicies ??= false;
        definition.IncludePreviewModels ??= false;
        definition.Experimental ??= false;
    }

    private static void ApplyXaiDirectDefaults(CodeAltaProviderDocument definition)
    {
        if (!string.Equals(definition.ProviderType, XaiDirectProviderType, StringComparison.Ordinal))
        {
            return;
        }

        definition.DisplayName ??= XaiDirectDefaultDisplayName;
        definition.AuthSource ??= XaiDirectDefaultAuthSource;
        definition.ModelDiscovery ??= XaiDirectDefaultModelDiscovery;
    }

    private static void ValidateProviderFields(CodeAltaProviderDocument definition)
    {
        if (!string.Equals(definition.ProviderType, CodexSubscriptionProviderType, StringComparison.Ordinal) &&
            !string.Equals(definition.ProviderType, CopilotDirectProviderType, StringComparison.Ordinal) &&
            !string.Equals(definition.ProviderType, XaiDirectProviderType, StringComparison.Ordinal))
        {
            RejectCodexSubscriptionOnlyFields(definition);
        }

        if (!string.Equals(definition.ProviderType, CopilotDirectProviderType, StringComparison.Ordinal))
        {
            RejectCopilotDirectOnlyFields(definition);
        }

        switch (definition.ProviderType)
        {
            case "openai-chat":
            case "openai-responses":
                RejectUnsupportedField(definition, "project", definition.Project);
                RejectUnsupportedField(definition, "location", definition.Location);
                break;

            case "azure-openai":
                RejectUnsupportedField(definition, "organization_id", definition.OrganizationId);
                RejectUnsupportedField(definition, "project_id", definition.ProjectId);
                RejectUnsupportedField(definition, "project", definition.Project);
                RejectUnsupportedField(definition, "location", definition.Location);
                RejectUnsupportedField(definition, "extra_body", definition.ExtraBody);
                if (definition.Enabled != false && string.IsNullOrWhiteSpace(definition.ApiUrl))
                {
                    throw new InvalidOperationException($"providers.{definition.ProviderKey} api_url is required for type 'azure-openai'.");
                }

                if (definition.Enabled != false &&
                    string.IsNullOrWhiteSpace(definition.Model) &&
                    string.IsNullOrWhiteSpace(definition.SingleModelId))
                {
                    throw new InvalidOperationException($"providers.{definition.ProviderKey} model or single_model_id is required for type 'azure-openai'.");
                }

                break;

            case CodexSubscriptionProviderType:
                RejectCodexSubscriptionApiKeyFields(definition);
                RejectUnsupportedField(definition, "organization_id", definition.OrganizationId);
                RejectUnsupportedField(definition, "project_id", definition.ProjectId);
                RejectUnsupportedField(definition, "project", definition.Project);
                RejectUnsupportedField(definition, "location", definition.Location);
                RejectUnsupportedField(definition, "models_dev_provider_id", definition.ModelsDevProviderId);
                RejectUnsupportedField(definition, "single_model_id", definition.SingleModelId);
                RejectUnsupportedField(definition, "extra_body", definition.ExtraBody);
                ValidateCodexSubscriptionFields(definition);
                break;

            case CopilotDirectProviderType:
                RejectUnsupportedField(definition, "account_id", definition.AccountId);
                RejectUnsupportedField(definition, "max_concurrent_requests", definition.MaxConcurrentRequests);
                RejectUnsupportedField(definition, "text_verbosity", definition.TextVerbosity);
                RejectUnsupportedField(definition, "include_encrypted_reasoning", definition.IncludeEncryptedReasoning);
                RejectUnsupportedField(definition, "response_transport", definition.ResponseTransport);
                RejectUnsupportedField(definition, "send_responses_beta_header", definition.SendResponsesBetaHeader);
                RejectUnsupportedField(definition, "send_installation_id", definition.SendInstallationId);
                RejectUnsupportedField(definition, "installation_id_source", definition.InstallationIdSource);
                RejectUnsupportedField(definition, "organization_id", definition.OrganizationId);
                RejectUnsupportedField(definition, "project_id", definition.ProjectId);
                RejectUnsupportedField(definition, "project", definition.Project);
                RejectUnsupportedField(definition, "location", definition.Location);
                RejectUnsupportedField(definition, "models_dev_provider_id", definition.ModelsDevProviderId);
                RejectUnsupportedField(definition, "extra_body", definition.ExtraBody);
                ValidateCopilotDirectFields(definition);
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
                if (definition.Enabled != false && string.IsNullOrWhiteSpace(definition.Project))
                {
                    throw new InvalidOperationException($"providers.{definition.ProviderKey} project is required for type 'vertex-ai'.");
                }

                if (definition.Enabled != false && string.IsNullOrWhiteSpace(definition.Location))
                {
                    throw new InvalidOperationException($"providers.{definition.ProviderKey} location is required for type 'vertex-ai'.");
                }

                break;

            case XaiDirectProviderType:
                RejectUnsupportedField(definition, "account_id", definition.AccountId);
                RejectUnsupportedField(definition, "max_concurrent_requests", definition.MaxConcurrentRequests);
                RejectUnsupportedField(definition, "text_verbosity", definition.TextVerbosity);
                RejectUnsupportedField(definition, "include_encrypted_reasoning", definition.IncludeEncryptedReasoning);
                RejectUnsupportedField(definition, "response_transport", definition.ResponseTransport);
                RejectUnsupportedField(definition, "send_responses_beta_header", definition.SendResponsesBetaHeader);
                RejectUnsupportedField(definition, "send_installation_id", definition.SendInstallationId);
                RejectUnsupportedField(definition, "installation_id_source", definition.InstallationIdSource);
                RejectUnsupportedField(definition, "experimental", definition.Experimental);
                RejectUnsupportedField(definition, "organization_id", definition.OrganizationId);
                RejectUnsupportedField(definition, "project_id", definition.ProjectId);
                RejectUnsupportedField(definition, "project", definition.Project);
                RejectUnsupportedField(definition, "location", definition.Location);
                RejectUnsupportedField(definition, "extra_body", definition.ExtraBody);
                RejectUnsupportedField(definition, "api_key", definition.ApiKey);
                RejectUnsupportedField(definition, "api_key_env", definition.ApiKeyEnv);
                ValidateXaiDirectFields(definition);
                break;
        }

        if (!string.IsNullOrWhiteSpace(definition.ApiUrl) &&
            !Uri.TryCreate(definition.ApiUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} api_url must be an absolute URI.");
        }

        if ((string.Equals(definition.ProviderType, CodexSubscriptionProviderType, StringComparison.Ordinal) ||
             string.Equals(definition.ProviderType, "azure-openai", StringComparison.Ordinal) ||
             string.Equals(definition.ProviderType, CopilotDirectProviderType, StringComparison.Ordinal) ||
             string.Equals(definition.ProviderType, XaiDirectProviderType, StringComparison.Ordinal)) &&
            !string.IsNullOrWhiteSpace(definition.ApiUrl) &&
            Uri.TryCreate(definition.ApiUrl, UriKind.Absolute, out var directUri) &&
            !IsHttpsOrLocalhost(directUri))
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} api_url must use HTTPS except for localhost test transports.");
        }
    }

    private static void RejectCodexSubscriptionOnlyFields(CodeAltaProviderDocument definition)
    {
        RejectUnsupportedField(definition, "auth_source", definition.AuthSource);
        RejectUnsupportedField(definition, "account_id", definition.AccountId);
        RejectUnsupportedField(definition, "max_concurrent_requests", definition.MaxConcurrentRequests);
        RejectUnsupportedField(definition, "text_verbosity", definition.TextVerbosity);
        RejectUnsupportedField(definition, "include_encrypted_reasoning", definition.IncludeEncryptedReasoning);
        RejectUnsupportedField(definition, "model_discovery", definition.ModelDiscovery);
        RejectUnsupportedField(definition, "response_transport", definition.ResponseTransport);
        RejectUnsupportedField(definition, "send_responses_beta_header", definition.SendResponsesBetaHeader);
        RejectUnsupportedField(definition, "send_installation_id", definition.SendInstallationId);
        RejectUnsupportedField(definition, "installation_id_source", definition.InstallationIdSource);
        RejectUnsupportedField(definition, "experimental", definition.Experimental);
    }

    private static void RejectCopilotDirectOnlyFields(CodeAltaProviderDocument definition)
    {
        RejectUnsupportedField(definition, "github_enterprise_url", definition.GitHubEnterpriseUrl);
        RejectUnsupportedField(definition, "github_token_env", definition.GitHubTokenEnv);
        RejectUnsupportedField(definition, "copilot_token_env", definition.CopilotTokenEnv);
        RejectUnsupportedField(definition, "enable_model_policies", definition.EnableModelPolicies);
        RejectUnsupportedField(definition, "include_preview_models", definition.IncludePreviewModels);
    }

    private static void RejectCodexSubscriptionApiKeyFields(CodeAltaProviderDocument definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.ApiKey) || !string.IsNullOrWhiteSpace(definition.ApiKeyEnv))
        {
            throw new InvalidOperationException(
                $"providers.{definition.ProviderKey} uses ChatGPT OAuth; api_key and api_key_env are not supported for type '{CodexSubscriptionProviderType}'.");
        }
    }

    private static void ValidateCodexSubscriptionFields(CodeAltaProviderDocument definition)
    {
        if (definition.MaxConcurrentRequests is <= 0)
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} max_concurrent_requests must be greater than zero.");
        }

        if (definition.AuthSource is not ("codealta_oauth" or "codex_auth_import" or "codex_auth_file_readonly" or "external_token_command"))
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} auth_source must be one of: codealta_oauth, codex_auth_import, codex_auth_file_readonly, external_token_command.");
        }

        if (definition.TextVerbosity is not ("low" or "medium" or "high"))
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} text_verbosity must be one of: low, medium, high.");
        }

        if (definition.ModelDiscovery is not ("codex_endpoint_with_static_fallback" or "codex_endpoint" or "static"))
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} model_discovery must be one of: codex_endpoint_with_static_fallback, codex_endpoint, static.");
        }

        if (definition.ResponseTransport is not ("websocket_with_http_fallback" or "http"))
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} response_transport must be one of: websocket_with_http_fallback, http.");
        }

        if (definition.InstallationIdSource is not ("codealta_state" or "codex_home_import" or "codex_home_readonly"))
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} installation_id_source must be one of: codealta_state, codex_home_import, codex_home_readonly.");
        }
    }

    private static void ValidateXaiDirectFields(CodeAltaProviderDocument definition)
    {
        if (definition.AuthSource is not ("xai_browser_oauth" or "xai_device_flow"))
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} auth_source must be one of: xai_browser_oauth, xai_device_flow.");
        }

        if (definition.ModelDiscovery is not ("xai_endpoint_with_static_fallback" or "xai_endpoint" or "static"))
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} model_discovery must be one of: xai_endpoint_with_static_fallback, xai_endpoint, static.");
        }
    }

    private static void ValidateCopilotDirectFields(CodeAltaProviderDocument definition)
    {
        if (definition.AuthSource is not ("github_device_flow" or "github_token_env" or "copilot_token_env"))
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} auth_source must be one of: github_device_flow, github_token_env, copilot_token_env.");
        }

        if (definition.ModelDiscovery is not ("copilot_endpoint_with_static_fallback" or "copilot_endpoint" or "static"))
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} model_discovery must be one of: copilot_endpoint_with_static_fallback, copilot_endpoint, static.");
        }

        if (definition.Enabled != false && definition.AuthSource == "github_token_env" && string.IsNullOrWhiteSpace(definition.GitHubTokenEnv))
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} github_token_env is required when auth_source is 'github_token_env'.");
        }

        if (definition.Enabled != false && definition.AuthSource == "copilot_token_env" && string.IsNullOrWhiteSpace(definition.CopilotTokenEnv))
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} copilot_token_env is required when auth_source is 'copilot_token_env'.");
        }

        if (!string.IsNullOrWhiteSpace(definition.GitHubEnterpriseUrl) && NormalizeDomain(definition.GitHubEnterpriseUrl) is null)
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} github_enterprise_url must be a valid URL or domain.");
        }
    }

    private static bool IsHttpsOrLocalhost(Uri uri)
        => string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
           uri.IsLoopback ||
           string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

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

        return !CanPersistProviderEntry(definition);
    }

    private static void PruneProviderPreferenceFields(string providerKey, CodeAltaProviderDocument definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentNullException.ThrowIfNull(definition);

        _ = providerKey;
    }

    private static CodeAltaProviderDocument GetOrCreateProviderPreferenceEntry(CodeAltaConfigDocument document, string providerKey)
    {
        document.Providers ??= new Dictionary<string, CodeAltaProviderDocument>(StringComparer.OrdinalIgnoreCase);
        if (document.Providers.TryGetValue(providerKey, out var existing))
        {
            return existing;
        }

        throw new InvalidOperationException($"Provider '{providerKey}' is not configured.");
    }

    private static bool GetDefaultProviderEnabled(string providerKey)
        => CodeAltaProviderDocument.DefaultEnabled;

    private static CodeAltaProviderProfileDocument? NormalizeProfile(CodeAltaProviderProfileDocument? profile)
    {
        if (profile is null)
        {
            return null;
        }

        profile.MaxTokensFieldName = NormalizeText(profile.MaxTokensFieldName);
        profile.ReasoningInputFieldName = NormalizeText(profile.ReasoningInputFieldName);
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

    private static void PruneProviderDefaults(CodeAltaProviderDocument definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.Enabled == GetDefaultProviderEnabled(definition.ProviderKey))
        {
            definition.Enabled = null;
        }

        if (string.Equals(definition.ProviderType, CodexSubscriptionProviderType, StringComparison.Ordinal))
        {
            if (string.Equals(definition.DisplayName, CodexSubscriptionDefaultDisplayName, StringComparison.Ordinal))
            {
                definition.DisplayName = null;
            }

            if (string.Equals(definition.ApiUrl, CodexSubscriptionDefaultApiUrl, StringComparison.Ordinal))
            {
                definition.ApiUrl = null;
            }

            if (string.Equals(definition.AuthSource, CodexSubscriptionDefaultAuthSource, StringComparison.Ordinal))
            {
                definition.AuthSource = null;
            }

            if (definition.MaxConcurrentRequests == CodexSubscriptionDefaultMaxConcurrentRequests)
            {
                definition.MaxConcurrentRequests = null;
            }

            if (string.Equals(definition.TextVerbosity, CodexSubscriptionDefaultTextVerbosity, StringComparison.Ordinal))
            {
                definition.TextVerbosity = null;
            }

            if (definition.IncludeEncryptedReasoning == true)
            {
                definition.IncludeEncryptedReasoning = null;
            }

            if (string.Equals(definition.ModelDiscovery, CodexSubscriptionDefaultModelDiscovery, StringComparison.Ordinal))
            {
                definition.ModelDiscovery = null;
            }

            if (string.Equals(definition.ResponseTransport, CodexSubscriptionDefaultResponseTransport, StringComparison.Ordinal))
            {
                definition.ResponseTransport = null;
            }

            if (definition.SendResponsesBetaHeader == true)
            {
                definition.SendResponsesBetaHeader = null;
            }

            if (definition.SendInstallationId == false)
            {
                definition.SendInstallationId = null;
            }

            if (string.Equals(definition.InstallationIdSource, CodexSubscriptionDefaultInstallationIdSource, StringComparison.Ordinal))
            {
                definition.InstallationIdSource = null;
            }
        }

        if (string.Equals(definition.ProviderType, CopilotDirectProviderType, StringComparison.Ordinal))
        {
            if (string.Equals(definition.DisplayName, CopilotDirectDefaultDisplayName, StringComparison.Ordinal))
            {
                definition.DisplayName = null;
            }

            if (string.Equals(definition.AuthSource, CopilotDirectDefaultAuthSource, StringComparison.Ordinal))
            {
                definition.AuthSource = null;
            }

            if (string.Equals(definition.ModelDiscovery, CopilotDirectDefaultModelDiscovery, StringComparison.Ordinal))
            {
                definition.ModelDiscovery = null;
            }

            if (definition.EnableModelPolicies == false)
            {
                definition.EnableModelPolicies = null;
            }

            if (definition.IncludePreviewModels == false)
            {
                definition.IncludePreviewModels = null;
            }
        }

        if (string.Equals(definition.ProviderType, XaiDirectProviderType, StringComparison.Ordinal))
        {
            if (string.Equals(definition.DisplayName, XaiDirectDefaultDisplayName, StringComparison.Ordinal))
            {
                definition.DisplayName = null;
            }

            if (string.Equals(definition.AuthSource, XaiDirectDefaultAuthSource, StringComparison.Ordinal))
            {
                definition.AuthSource = null;
            }

            if (string.Equals(definition.ModelDiscovery, XaiDirectDefaultModelDiscovery, StringComparison.Ordinal))
            {
                definition.ModelDiscovery = null;
            }
        }

        if ((string.Equals(definition.ProviderType, CodexSubscriptionProviderType, StringComparison.Ordinal) ||
             string.Equals(definition.ProviderType, CopilotDirectProviderType, StringComparison.Ordinal)) &&
            definition.Experimental == false)
        {
            definition.Experimental = null;
        }

        definition.Compaction = PruneCompactionDefaults(definition.Compaction);
    }

    private static bool CanPersistProviderEntry(CodeAltaProviderDocument definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return definition.Enabled is not null ||
               !string.IsNullOrWhiteSpace(definition.DisplayName) ||
               !string.IsNullOrWhiteSpace(definition.ProviderType) ||
               !string.IsNullOrWhiteSpace(definition.Model) ||
               !string.IsNullOrWhiteSpace(definition.ReasoningEffort) ||
               !string.IsNullOrWhiteSpace(definition.ApiKey) ||
               !string.IsNullOrWhiteSpace(definition.ApiKeyEnv) ||
               !string.IsNullOrWhiteSpace(definition.ApiUrl) ||
               !string.IsNullOrWhiteSpace(definition.GitHubEnterpriseUrl) ||
               !string.IsNullOrWhiteSpace(definition.GitHubTokenEnv) ||
               !string.IsNullOrWhiteSpace(definition.CopilotTokenEnv) ||
               definition.EnableModelPolicies is not null ||
               definition.IncludePreviewModels is not null ||
               definition.ProtocolTrace == true ||
               !string.IsNullOrWhiteSpace(definition.AuthSource) ||
               !string.IsNullOrWhiteSpace(definition.AccountId) ||
               definition.MaxConcurrentRequests is not null ||
               !string.IsNullOrWhiteSpace(definition.TextVerbosity) ||
               definition.IncludeEncryptedReasoning is not null ||
               !string.IsNullOrWhiteSpace(definition.ModelDiscovery) ||
               !string.IsNullOrWhiteSpace(definition.ResponseTransport) ||
               definition.SendResponsesBetaHeader is not null ||
               definition.SendInstallationId is not null ||
               !string.IsNullOrWhiteSpace(definition.InstallationIdSource) ||
               definition.Experimental is not null ||
               !string.IsNullOrWhiteSpace(definition.OrganizationId) ||
               !string.IsNullOrWhiteSpace(definition.ProjectId) ||
               !string.IsNullOrWhiteSpace(definition.Project) ||
               !string.IsNullOrWhiteSpace(definition.Location) ||
               !string.IsNullOrWhiteSpace(definition.ModelsDevProviderId) ||
               !string.IsNullOrWhiteSpace(definition.SingleModelId) ||
               definition.ExtraBody is { Count: > 0 } ||
               definition.Profile is not null ||
               definition.Compaction is not null ||
               definition.ModelOverrides is { Count: > 0 };
    }

    private static HashSet<string> LoadEnabledProviderKeys(IEnumerable<CodeAltaProviderDocument> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        return definitions
            .Select(CloneProviderDefinition)
            .Select(static definition =>
            {
                CompleteAndValidateProviderDefinition(definition);
                return definition;
            })
            .Where(static definition => definition.Enabled != false)
            .Select(static definition => definition.ProviderKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateProviderCredentialsForSave(CodeAltaProviderDocument definition)
    {
        if (definition.Enabled == false)
        {
            return;
        }

        switch (definition.ProviderType)
        {
            case "openai-chat":
            case "openai-responses":
            case "azure-openai":
            case "anthropic":
            case "google-genai":
                if (string.IsNullOrWhiteSpace(definition.ApiKey) &&
                    string.IsNullOrWhiteSpace(definition.ApiKeyEnv))
                {
                    throw new InvalidOperationException($"providers.{definition.ProviderKey} requires api_key or api_key_env when enabled.");
                }

                break;
        }
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
            GitHubEnterpriseUrl = definition.GitHubEnterpriseUrl,
            GitHubTokenEnv = definition.GitHubTokenEnv,
            CopilotTokenEnv = definition.CopilotTokenEnv,
            EnableModelPolicies = definition.EnableModelPolicies,
            IncludePreviewModels = definition.IncludePreviewModels,
            ProtocolTrace = definition.ProtocolTrace,
            AuthSource = definition.AuthSource,
            AccountId = definition.AccountId,
            MaxConcurrentRequests = definition.MaxConcurrentRequests,
            TextVerbosity = definition.TextVerbosity,
            IncludeEncryptedReasoning = definition.IncludeEncryptedReasoning,
            ModelDiscovery = definition.ModelDiscovery,
            ResponseTransport = definition.ResponseTransport,
            SendResponsesBetaHeader = definition.SendResponsesBetaHeader,
            SendInstallationId = definition.SendInstallationId,
            InstallationIdSource = definition.InstallationIdSource,
            Experimental = definition.Experimental,
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
            ReasoningInputFieldName = profile.ReasoningInputFieldName,
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
                Ratio = compaction.Ratio,
                SummaryOutputRatio = compaction.SummaryOutputRatio,
                PostCompactionTargetRatio = compaction.PostCompactionTargetRatio,
                SummaryShareOfTarget = compaction.SummaryShareOfTarget,
                FileContextShareOfSummaryTarget = compaction.FileContextShareOfSummaryTarget,
                KeepLastUserMessage = compaction.KeepLastUserMessage,
                AllowSplitTurn = compaction.AllowSplitTurn,
            };
    }

    private static CodeAltaProviderCompactionDocument? NormalizeCompaction(CodeAltaProviderCompactionDocument? compaction)
    {
        if (compaction is null)
        {
            return null;
        }

        var normalized = CloneCompaction(compaction);
        return IsEmptyCompaction(normalized) ? null : normalized;
    }

    private static CodeAltaProviderCompactionDocument? PruneCompactionDefaults(CodeAltaProviderCompactionDocument? compaction)
    {
        if (compaction is null)
        {
            return null;
        }

        var pruned = CloneCompaction(compaction);

        if (pruned.Enabled == LocalAgentCompactionSettings.DefaultEnabled)
        {
            pruned.Enabled = null;
        }

        if (pruned.Ratio == LocalAgentCompactionSettings.DefaultRatio)
        {
            pruned.Ratio = null;
        }

        if (pruned.SummaryOutputRatio == LocalAgentCompactionSettings.DefaultSummaryOutputRatio)
        {
            pruned.SummaryOutputRatio = null;
        }

        if (pruned.PostCompactionTargetRatio == LocalAgentCompactionSettings.DefaultPostCompactionTargetRatio)
        {
            pruned.PostCompactionTargetRatio = null;
        }

        if (pruned.SummaryShareOfTarget == LocalAgentCompactionSettings.DefaultSummaryShareOfTarget)
        {
            pruned.SummaryShareOfTarget = null;
        }

        if (pruned.FileContextShareOfSummaryTarget == LocalAgentCompactionSettings.DefaultFileContextShareOfSummaryTarget)
        {
            pruned.FileContextShareOfSummaryTarget = null;
        }

        if (pruned.KeepLastUserMessage == LocalAgentCompactionSettings.DefaultKeepLastUserMessage)
        {
            pruned.KeepLastUserMessage = null;
        }

        if (pruned.AllowSplitTurn == LocalAgentCompactionSettings.DefaultAllowSplitTurn)
        {
            pruned.AllowSplitTurn = null;
        }

        return IsEmptyCompaction(pruned) ? null : pruned;
    }

    private static bool IsEmptyCompaction(CodeAltaProviderCompactionDocument compaction)
    {
        ArgumentNullException.ThrowIfNull(compaction);

        return compaction.Enabled is null &&
               compaction.Ratio is null &&
               compaction.SummaryOutputRatio is null &&
               compaction.PostCompactionTargetRatio is null &&
               compaction.SummaryShareOfTarget is null &&
               compaction.FileContextShareOfSummaryTarget is null &&
               compaction.KeepLastUserMessage is null &&
               compaction.AllowSplitTurn is null;
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
            merged.Ratio = normalized.Ratio ?? merged.Ratio;
            merged.SummaryOutputRatio = normalized.SummaryOutputRatio ?? merged.SummaryOutputRatio;
            merged.PostCompactionTargetRatio = normalized.PostCompactionTargetRatio ?? merged.PostCompactionTargetRatio;
            merged.SummaryShareOfTarget = normalized.SummaryShareOfTarget ?? merged.SummaryShareOfTarget;
            merged.FileContextShareOfSummaryTarget = normalized.FileContextShareOfSummaryTarget ?? merged.FileContextShareOfSummaryTarget;
            merged.KeepLastUserMessage = normalized.KeepLastUserMessage ?? merged.KeepLastUserMessage;
            merged.AllowSplitTurn = normalized.AllowSplitTurn ?? merged.AllowSplitTurn;
        }

        merged.Enabled ??= LocalAgentCompactionSettings.DefaultEnabled;
        merged.Ratio ??= LocalAgentCompactionSettings.DefaultRatio;
        merged.SummaryOutputRatio ??= LocalAgentCompactionSettings.DefaultSummaryOutputRatio;
        merged.PostCompactionTargetRatio ??= LocalAgentCompactionSettings.DefaultPostCompactionTargetRatio;
        merged.SummaryShareOfTarget ??= LocalAgentCompactionSettings.DefaultSummaryShareOfTarget;
        merged.FileContextShareOfSummaryTarget ??= LocalAgentCompactionSettings.DefaultFileContextShareOfSummaryTarget;
        merged.KeepLastUserMessage ??= LocalAgentCompactionSettings.DefaultKeepLastUserMessage;
        merged.AllowSplitTurn ??= LocalAgentCompactionSettings.DefaultAllowSplitTurn;

        ValidateCompaction(merged);
        return merged;
    }

    private static void ValidateCompaction(CodeAltaProviderCompactionDocument compaction)
    {
        ArgumentNullException.ThrowIfNull(compaction);

        if (compaction.Ratio is not > 0 or > 1)
        {
            throw new InvalidOperationException("provider compaction ratio must be > 0 and <= 1.");
        }

        if (compaction.SummaryOutputRatio is not > 0 ||
            compaction.SummaryOutputRatio > LocalAgentCompactionSettings.MaxSummaryOutputRatio)
        {
            throw new InvalidOperationException(
                $"provider compaction summary_output_ratio must be > 0 and <= {LocalAgentCompactionSettings.MaxSummaryOutputRatio:0.##}.");
        }

        if (compaction.PostCompactionTargetRatio is not > 0 ||
            compaction.PostCompactionTargetRatio > LocalAgentCompactionSettings.MaxPostCompactionTargetRatio)
        {
            throw new InvalidOperationException(
                $"provider compaction post_compaction_target_ratio must be > 0 and <= {LocalAgentCompactionSettings.MaxPostCompactionTargetRatio:0.##}.");
        }

        if (compaction.SummaryShareOfTarget is not > 0 ||
            compaction.SummaryShareOfTarget > LocalAgentCompactionSettings.MaxSummaryShareOfTarget)
        {
            throw new InvalidOperationException(
                $"provider compaction summary_share_of_target must be > 0 and <= {LocalAgentCompactionSettings.MaxSummaryShareOfTarget:0.##}.");
        }

        if (compaction.FileContextShareOfSummaryTarget is not >= 0 ||
            compaction.FileContextShareOfSummaryTarget > LocalAgentCompactionSettings.MaxFileContextShareOfSummaryTarget)
        {
            throw new InvalidOperationException(
                $"provider compaction file_context_share_of_summary_target must be >= 0 and <= {LocalAgentCompactionSettings.MaxFileContextShareOfSummaryTarget:0.##}.");
        }
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

    private static void ThrowIfLegacyConfigShapeDetected(string content, string? sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var normalized = content.ToLowerInvariant();
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

        var markerOffset = normalized.StartsWith("provider =", StringComparison.Ordinal)
            ? 0
            : legacyMarkers
                .Select(marker => normalized.IndexOf(marker, StringComparison.Ordinal))
                .Where(static index => index >= 0)
                .DefaultIfEmpty(-1)
                .Min();

        if (markerOffset >= 0)
        {
            if (normalized[markerOffset] == '\n')
            {
                markerOffset++;
            }

            var position = GetTomlTextPosition(content, markerOffset);
            var span = new TomlSourceSpan(sourcePath ?? string.Empty, position, position);
            throw new TomlException(
                span,
                "Legacy CodeAlta config keys are no longer supported. Migrate to [chat].default_provider, providers.<key>.type, and providers.<key>.api_url.");
        }
    }

    private static TomlTextPosition GetTomlTextPosition(string content, int offset)
    {
        var clampedOffset = Math.Clamp(offset, 0, content.Length);
        var line = 0;
        var column = 0;
        for (var i = 0; i < clampedOffset; i++)
        {
            if (content[i] == '\n')
            {
                line++;
                column = 0;
            }
            else if (content[i] != '\r')
            {
                column++;
            }
        }

        return new TomlTextPosition(clampedOffset, line, column);
    }
}
