using System.Text;
using CodeAlta.Agent;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.Runtime.Compaction;
using CodeAlta.Catalog.Skills;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;
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

    /// <summary>
    /// Gets what a configuration that can be loaded leaves out, or <see langword="null"/>: the providers whose
    /// type this version does not know.
    /// </summary>
    public string? Warning { get; init; }
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
    private const string ClaudeCodeProviderType = "claude-code";
    private const string AnthropicProviderType = "anthropic";
    private const string TypeSafeProviderType = "typesafe";
    private const string ClaudeCodeDefaultDisplayName = "Claude Code";
    private static readonly string[] ClaudeCodePermissionModes = ["default", "acceptEdits", "plan", "auto", "dontAsk", "bypassPermissions"];
    private static readonly string[] ClaudeCodeApiKeyChoices = ["use", "ignore"];
    private const string CopilotDirectDefaultDisplayName = "Copilot";
    private const string CopilotDirectDefaultAuthSource = "github_device_flow";
    private const string CopilotDirectDefaultModelDiscovery = "copilot_endpoint_with_static_fallback";
    private const string XaiDirectDefaultDisplayName = "xAI Grok";
    private const string XaiDirectDefaultAuthSource = "xai_browser_oauth";
    private const string XaiDirectDefaultModelDiscovery = "xai_endpoint_with_static_fallback";
    private const string CodexSubscriptionDefaultDisplayName = "Codex";
    private const string CodexSubscriptionDefaultApiUrl = "https://api.openai.com/v1";
    private const string CodexSubscriptionDefaultAuthSource = "codealta_oauth";
    private const string CodexSubscriptionDefaultTextVerbosity = "medium";
    private const string CodexSubscriptionDefaultModelDiscovery = "codex_endpoint_with_static_fallback";
    private const string CodexSubscriptionDefaultResponseTransport = "http";
    private const string CodexSubscriptionDefaultInstallationIdSource = "codealta_state";
    private const int CodexSubscriptionDefaultMaxConcurrentRequests = 16;

    private static readonly CodeAltaProviderCompactionDocument DefaultCompaction = new()
    {
        Enabled = AgentCompactionSettings.DefaultEnabled,
        Ratio = AgentCompactionSettings.DefaultRatio,
        SummaryOutputRatio = AgentCompactionSettings.DefaultSummaryOutputRatio,
        PostCompactionTargetRatio = AgentCompactionSettings.DefaultPostCompactionTargetRatio,
        SummaryShareOfTarget = AgentCompactionSettings.DefaultSummaryShareOfTarget,
        FileContextShareOfSummaryTarget = AgentCompactionSettings.DefaultFileContextShareOfSummaryTarget,
        KeepLastUserMessage = AgentCompactionSettings.DefaultKeepLastUserMessage,
        AllowSplitTurn = AgentCompactionSettings.DefaultAllowSplitTurn,
    };

    private readonly CatalogOptions _options;
    private readonly TextFileCodec _textFiles;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodeAltaConfigStore"/> class.
    /// </summary>
    /// <param name="options">Catalog layout options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <see cref="CatalogOptions.GlobalRoot"/> is empty.</exception>
    public CodeAltaConfigStore(CatalogOptions options)
        : this(options, new TextFileCodec())
    {
    }

    /// <summary>Initializes the config owner with a shared codec for nonoverwriting first-run creation.</summary>
    /// <exception cref="ArgumentNullException">Options or codec is null.</exception>
    /// <exception cref="ArgumentException">The global root is empty.</exception>
    public CodeAltaConfigStore(CatalogOptions options, TextFileCodec textFiles)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(textFiles);
        if (string.IsNullOrWhiteSpace(options.GlobalRoot))
        {
            throw new ArgumentException("Global catalog root is required.", nameof(options));
        }

        _options = options;
        _textFiles = textFiles;
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

    /// <summary>Reads the global choices for speech to prompt. A missing file gives the defaults.</summary>
    /// <exception cref="InvalidDataException">The configuration or the model is invalid.</exception>
    /// <exception cref="IOException">The configuration cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the configuration is denied.</exception>
    public Speech.SpeechSettings LoadGlobalSpeechSettings()
        => Speech.SpeechSettings.Read(LoadGlobal().Speech);

    /// <summary>Reads the global choices for search. A missing file gives the defaults.</summary>
    /// <returns>The choices.</returns>
    /// <exception cref="InvalidDataException">The configuration is invalid.</exception>
    public Search.SearchSettings LoadGlobalSearchSettings()
        => Search.SearchSettings.Read(LoadGlobal().Search);

    /// <summary>Reads whether a project's own configuration keeps it out of search (<c>[search] enabled = false</c>).</summary>
    /// <param name="projectRoot">The folder of the project.</param>
    /// <returns>True when the project file turns search off for the project.</returns>
    public bool IsProjectExcludedFromSearch(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return false;
        try
        {
            return LoadProject(projectRoot).Search?.Enabled == false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Reads the global model tiers and provider fallback. A missing file gives no tier, fallback off.</summary>
    /// <exception cref="InvalidDataException">The configuration, a target or a fallback trigger is invalid.</exception>
    /// <exception cref="IOException">The configuration cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the configuration is denied.</exception>
    public ModelTiers.ModelSettings LoadGlobalModelSettings()
        => ModelTiers.ModelSettings.Read(LoadGlobal().Models);

    /// <summary>Saves the global model tiers and provider fallback. Defaults are not written.</summary>
    /// <exception cref="ArgumentNullException">The settings are null.</exception>
    /// <exception cref="InvalidDataException">The existing configuration is invalid.</exception>
    /// <exception cref="IOException">The configuration cannot be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the configuration is denied.</exception>
    public void SaveGlobalModelSettings(ModelTiers.ModelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var writing = ConfigFileLock.Acquire(_options.ConfigPath);
        var document = LoadGlobal();
        document.Models = settings.ToDocument();
        SaveDocument(_options.ConfigPath, document);
    }

    /// <summary>Reads global cleanup policy. Only an actually missing file uses the enabled defaults.</summary>
    /// <remarks>Unlike a File.Exists probe, an inaccessible file must not look like permission to delete.</remarks>
    /// <exception cref="InvalidDataException">The configuration or duration is invalid.</exception>
    /// <exception cref="IOException">The configuration cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the configuration is denied.</exception>
    public Storage.StorageSettings LoadGlobalStorageSettings()
    {
        string content;
        try { content = File.ReadAllText(_options.ConfigPath); }
        catch (FileNotFoundException) { return Storage.StorageSettings.Default; }
        catch (DirectoryNotFoundException) { return Storage.StorageSettings.Default; }
        try { return Storage.StorageSettings.Read(ParseDocument(content, _options.ConfigPath).Storage); }
        catch (Exception ex) when (IsConfigLoadException(ex))
        {
            throw new InvalidDataException($"Failed to read storage settings from '{_options.ConfigPath}'.", ex);
        }
    }

    /// <summary>
    /// Creates the global user configuration from the bundled first-run template when it is missing.
    /// </summary>
    /// <returns><see langword="true"/> when a new config file was written.</returns>
    /// <exception cref="IOException">The config cannot be read or created.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="InvalidOperationException">The bundled template is unavailable.</exception>
    public bool EnsureGlobalConfigExists()
        => EnsureGlobalConfigExistsAsync().GetAwaiter().GetResult();

    /// <summary>Creates missing defaults through the shared conditional-save gate; never replaces a competing creator.</summary>
    /// <inheritdoc cref="EnsureGlobalConfigExists"/>
    public async Task<bool> EnsureGlobalConfigExistsAsync()
    {
        try
        {
            var result = await _textFiles.SaveAsync(new TextFileSaveRequest(
                _options.ConfigPath, GetDefaultGlobalConfigContent(), new UTF8Encoding(false), false,
                TextFileRevision.Missing)).ConfigureAwait(false);
            return !result.IsConflict;
        }
        catch (IOException)
        {
            // A creator can win even after the codec's last revision check (File.Move is nonoverwriting).
            // The caller must load/validate that file; an unreadable competitor still fails closed.
            if ((await _textFiles.GetRevisionAsync(_options.ConfigPath).ConfigureAwait(false)).Exists) return false;
            throw;
        }
    }

    /// <summary>
    /// Creates the global user configuration from the bundled first-run template when it is missing.
    /// </summary>
    /// <param name="backupPath">Always receives <see langword="null"/>; existing configs are not modified.</param>
    /// <returns><see langword="true"/> when a new config file was written.</returns>
    /// <remarks>
    /// Existing user configuration files are left untouched so users can remove or rename bundled default entries.
    /// </remarks>
    /// <exception cref="IOException">Thrown when the config file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when access to the config file is denied.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the bundled first-run template is unavailable.</exception>
    public bool UpgradeGlobalConfigFromDefaults(out string? backupPath)
    {
        backupPath = null;
        return EnsureGlobalConfigExists();
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
        using var writing = ConfigFileLock.Acquire(_options.ConfigPath);
        File.WriteAllText(_options.ConfigPath, content ?? string.Empty);
    }

    /// <summary>
    /// Holds the lock of the global configuration file until the result is disposed: the store's own writes wait
    /// for it, in this process and in the others. A caller reads, checks and saves the file under it, on one thread.
    /// </summary>
    /// <returns>The held lock.</returns>
    /// <exception cref="IOException">Another writer has held the lock for too long.</exception>
    public IDisposable LockGlobalConfig() => ConfigFileLock.Acquire(_options.ConfigPath);

    /// <summary>
    /// Holds the lock of a configuration file of CodeAlta, the user's or a project's, until the result is disposed: the
    /// saves of the stores wait for it, in this process and in the others. A caller that writes the file its own way
    /// reads, changes and writes it under it, on one thread, without waiting for anything else.
    /// </summary>
    /// <param name="path">The path of the file, as the stores name it.</param>
    /// <returns>The held lock; disposing it releases it, on the same thread.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or not a valid path.</exception>
    /// <exception cref="IOException">Another writer has held the lock for too long.</exception>
    public static IDisposable LockConfigFile(string path) => ConfigFileLock.Acquire(path);

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
    /// Loads the provider definitions of the bundled first-run template, normalized like a loaded configuration.
    /// </summary>
    /// <returns>
    /// New definitions identified by <see cref="CodeAltaProviderDocument.ProviderKey"/>. Type defaults are not filled
    /// in, and a value the template writes that equals its type default is left unset.
    /// </returns>
    /// <exception cref="InvalidOperationException">The bundled template is unavailable or cannot be parsed.</exception>
    public static IReadOnlyList<CodeAltaProviderDocument> LoadDefaultProviderDefinitions()
    {
        try
        {
            return [.. (ParseDocument(GetDefaultGlobalConfigContent(), null).Providers ?? []).Values.Select(CloneProviderDefinition)];
        }
        catch (Exception ex) when (ex is FormatException or TomlException)
        {
            throw new InvalidOperationException("The bundled first-run configuration template is invalid.", ex);
        }
    }

    /// <summary>
    /// Tells whether a provider type is a decision provider: a model that answers typed questions (yes/no, a choice,
    /// a score) for the agents of sessions to consult, never a model that runs a session.
    /// </summary>
    /// <param name="providerType">A provider type, normalized or as a form or a file writes it (<c>jev</c>, <c>TypeSafe</c>).</param>
    /// <returns><see langword="true"/> for <c>typesafe</c> and its aliases; otherwise <see langword="false"/>.</returns>
    public static bool IsDecisionProviderType(string? providerType)
        => string.Equals(NormalizeProviderType(string.Empty, providerType), TypeSafeProviderType, StringComparison.Ordinal);

    /// <summary>
    /// Creates a blank, disabled definition of one provider type completed with the defaults that type fills in.
    /// </summary>
    /// <param name="providerType">A provider type or one of its aliases.</param>
    /// <returns>A definition holding only the type's defaults; a type without defaults leaves the optional settings unset.</returns>
    /// <exception cref="ArgumentException"><paramref name="providerType"/> is blank.</exception>
    /// <exception cref="InvalidOperationException">The provider type is not supported.</exception>
    public static CodeAltaProviderDocument CreateProviderTypeDefaults(string providerType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerType);

        // Disabled: completion does not then require the settings an enabled provider of the type needs.
        var definition = new CodeAltaProviderDocument { ProviderKey = "type-defaults", ProviderType = providerType, Enabled = false };
        CompleteAndValidateProviderDefinition(definition);
        return definition;
    }

    /// <summary>
    /// Validates global CodeAlta TOML configuration content without starting providers or sessions.
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
            return document.UnsupportedProviders.Count == 0
                ? CodeAltaConfigValidationResult.Valid
                : CodeAltaConfigValidationResult.Valid with { Warning = DescribeUnsupportedProviders(document.UnsupportedProviders) };
        }
        catch (Exception ex) when (IsConfigLoadException(ex))
        {
            return CreateValidationFailure(ex);
        }
    }

    /// <summary>
    /// Says which providers a configuration leaves out because this version does not know their type.
    /// </summary>
    /// <param name="providers">The providers that were set aside.</param>
    /// <returns>One sentence naming each provider and its type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="providers"/> is <see langword="null"/>.</exception>
    public static string DescribeUnsupportedProviders(IReadOnlyList<CodeAltaUnsupportedProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var names = string.Join(", ", providers.Select(static provider => $"providers.{provider.ProviderKey} (type \"{provider.ProviderType}\")"));
        return $"This version of CodeAlta does not know the provider type of {names}: left out, and kept in the file as written.";
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
    /// Loads globally disabled skill names.
    /// </summary>
    /// <returns>The normalized disabled skill names.</returns>
    public IReadOnlySet<string> LoadGlobalDisabledSkillNames()
    {
        var document = LoadGlobal();
        return LoadDisabledSkillNames(document);
    }

    /// <summary>
    /// Loads project-local disabled skill names.
    /// </summary>
    /// <param name="projectRoot">The project root directory.</param>
    /// <returns>The normalized disabled skill names.</returns>
    public IReadOnlySet<string> LoadProjectDisabledSkillNames(string? projectRoot)
    {
        var document = LoadProject(projectRoot);
        return LoadDisabledSkillNames(document);
    }

    /// <summary>Reads globally disabled skill names with a hard byte limit; does not create or update files.</summary>
    /// <remarks>Missing configuration means no disabled names. For every other non-complete outcome,
    /// enablement is unknown; callers must not substitute an empty set.</remarks>
    /// <param name="maxBytes">Byte budget, between 1 and 262144 inclusive.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Read outcome and names, without private diagnostics.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The budget is outside the supported range.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public Task<SkillDisabledNamesReadResult> LoadGlobalDisabledSkillNamesBoundedAsync(
        int maxBytes, CancellationToken cancellationToken = default)
        => LoadDisabledSkillNamesBoundedAsync(_options.ConfigPath, maxBytes, cancellationToken);

    /// <summary>Reads project-disabled skill names with a hard byte limit; does not create or update files.</summary>
    /// <remarks>A null project root omits project disablement. Other non-complete outcomes mean unknown enablement.</remarks>
    /// <param name="projectRoot">Optional project root, resolved to its standard configuration path.</param>
    /// <param name="maxBytes">Byte budget, between 1 and 262144 inclusive.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Read outcome and names, without private diagnostics.</returns>
    /// <exception cref="ArgumentException">A non-null project root is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The budget is outside the supported range.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public Task<SkillDisabledNamesReadResult> LoadProjectDisabledSkillNamesBoundedAsync(
        string? projectRoot, int maxBytes, CancellationToken cancellationToken = default)
        => LoadDisabledSkillNamesBoundedAsync(
            projectRoot is null ? null : GetProjectConfigPath(projectRoot), maxBytes, cancellationToken);

    private static async Task<SkillDisabledNamesReadResult> LoadDisabledSkillNamesBoundedAsync(
        string? path, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, SkillBoundedTextReader.MaximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (path is null)
        {
            return new SkillDisabledNamesReadResult(SkillBoundedReadStatus.Missing, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        var read = await SkillBoundedTextReader.ReadAsync(path, maxBytes, cancellationToken).ConfigureAwait(false);
        if (read.Status != SkillBoundedReadStatus.Complete)
        {
            return new SkillDisabledNamesReadResult(read.Status, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        try
        {
            return new SkillDisabledNamesReadResult(SkillBoundedReadStatus.Complete,
                LoadDisabledSkillNames(ParseDocument(read.Content!, path)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or IOException or TomlException)
        {
            return new SkillDisabledNamesReadResult(SkillBoundedReadStatus.Invalid, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Loads the effective disabled skill names for a scope.
    /// </summary>
    /// <param name="projectRoot">Optional project root for project-local disablement.</param>
    /// <returns>The normalized global/project disabled skill names.</returns>
    public IReadOnlySet<string> LoadEffectiveDisabledSkillNames(string? projectRoot = null)
    {
        var disabled = new HashSet<string>(LoadGlobalDisabledSkillNames(), StringComparer.OrdinalIgnoreCase);
        disabled.UnionWith(LoadProjectDisabledSkillNames(projectRoot));
        return disabled;
    }

    /// <summary>
    /// Reads the tools every session does without (<c>[tools] disabled</c>), from the user's file only. A name that
    /// is not the name of a tool is left out.
    /// </summary>
    /// <returns>The names; empty when the file sets none.</returns>
    /// <exception cref="InvalidDataException">The configuration file cannot be parsed.</exception>
    /// <exception cref="IOException">The configuration file cannot be read.</exception>
    public IReadOnlySet<string> LoadGlobalDisabledTools()
    {
        var document = LoadGlobal();
        return new HashSet<string>((document.Tools?.Disabled ?? []).Where(SpaceTools.IsToolName).Select(static name => name.Trim()), StringComparer.Ordinal);
    }

    /// <summary>Saves the tools every session does without; an empty list removes the setting.</summary>
    /// <param name="tools">The names of the tools.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tools"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A name is not the name of a tool, or there are too many.</exception>
    /// <exception cref="InvalidDataException">The configuration file cannot be parsed.</exception>
    /// <exception cref="IOException">The configuration file cannot be read or written.</exception>
    public void SaveGlobalDisabledTools(IEnumerable<string> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var names = tools.Select(static name => name?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal).ToList();
        if (names.FirstOrDefault(static name => !SpaceTools.IsToolName(name)) is { } wrong) throw new ArgumentException($"'{wrong}' is not the name of a tool.", nameof(tools));
        if (names.Count > SpaceTools.MaximumTools) throw new ArgumentException($"At most {SpaceTools.MaximumTools} tools are turned off.", nameof(tools));
        using var writing = ConfigFileLock.Acquire(_options.ConfigPath);
        var document = LoadGlobal();
        NormalizeDocument(document);
        document.Tools = names.Count == 0 ? null : new CodeAltaToolSettingsDocument { Disabled = names };
        SaveDocument(_options.ConfigPath, document);
    }

    /// <summary>
    /// Persists the complete global disabled skill set.
    /// </summary>
    /// <remarks>Edits only skill enablement tokens, preserving TOML comments and unknown settings. This is not an external-writer compare-and-swap.</remarks>
    /// <param name="skillNames">The skill names to disable globally.</param>
    public void SaveGlobalDisabledSkillNames(IEnumerable<string> skillNames)
    {
        ArgumentNullException.ThrowIfNull(skillNames);
        SaveDisabledSkillNames(_options.ConfigPath, skillNames);
    }

    /// <summary>
    /// Persists the complete project-local disabled skill set.
    /// </summary>
    /// <remarks>Edits only skill enablement tokens, preserving TOML comments and unknown settings. This is not a transaction with global configuration.</remarks>
    /// <param name="projectRoot">The project root directory.</param>
    /// <param name="skillNames">The skill names to disable for the project.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="projectRoot"/> is empty.</exception>
    public void SaveProjectDisabledSkillNames(string projectRoot, IEnumerable<string> skillNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(skillNames);
        var path = GetProjectConfigPath(projectRoot);
        SaveDisabledSkillNames(path, skillNames);
    }

    /// <summary>
    /// Persists a global skill enablement setting.
    /// </summary>
    /// <param name="skillName">The skill name.</param>
    /// <param name="enabled">A value indicating whether the skill should be enabled globally.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="skillName"/> is empty or invalid.</exception>
    public void SaveGlobalSkillEnabled(string skillName, bool enabled)
    {
        var normalizedName = NormalizeSkillConfigNameForSave(skillName);
        using var writing = ConfigFileLock.Acquire(_options.ConfigPath);
        var disabled = new HashSet<string>(LoadGlobalDisabledSkillNames(), StringComparer.OrdinalIgnoreCase);
        SetSkillEnabled(disabled, normalizedName, enabled);
        SaveGlobalDisabledSkillNames(disabled);
    }

    /// <summary>
    /// Persists a project-local skill enablement setting.
    /// </summary>
    /// <param name="projectRoot">The project root directory.</param>
    /// <param name="skillName">The skill name.</param>
    /// <param name="enabled">A value indicating whether the skill should be enabled for the project.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="projectRoot"/> or <paramref name="skillName"/> is empty, or when the skill name is invalid.</exception>
    public void SaveProjectSkillEnabled(string projectRoot, string skillName, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var normalizedName = NormalizeSkillConfigNameForSave(skillName);
        using var writing = ConfigFileLock.Acquire(GetProjectConfigPath(projectRoot));
        var disabled = new HashSet<string>(LoadProjectDisabledSkillNames(projectRoot), StringComparer.OrdinalIgnoreCase);
        SetSkillEnabled(disabled, normalizedName, enabled);
        SaveProjectDisabledSkillNames(projectRoot, disabled);
    }

    /// <summary>
    /// Reads the spaces each resource is assigned to in the global configuration: providers, MCP servers, plugins,
    /// skills and agent prompts. A resource it does not list is in every space.
    /// </summary>
    /// <returns>The assignments; empty when the file does not exist.</returns>
    /// <exception cref="InvalidDataException">The configuration file cannot be parsed.</exception>
    /// <exception cref="IOException">The configuration file cannot be read.</exception>
    public SpaceAssignments LoadGlobalSpaceAssignments()
    {
        var path = _options.ConfigPath;
        return File.Exists(path) ? SpaceAssignments.Parse(File.ReadAllText(path), path) : SpaceAssignments.Empty;
    }

    /// <summary>
    /// Writes the spaces a resource is assigned to in the global configuration, beside its definition: only that
    /// list changes, and the comments and the other settings of the file stay as they are.
    /// </summary>
    /// <remarks>
    /// An empty list removes the line: the resource is then in every space. A provider is assigned only when its
    /// table is in the file, so that a table with nothing but its spaces never stands for an incomplete provider;
    /// the table of an MCP server, a plugin, the skills or the prompts is added when it is missing. This is ordinary
    /// configuration persistence, not a compare-and-swap with another writer.
    /// </remarks>
    /// <param name="kind">The kind of the resource.</param>
    /// <param name="key">Its key, name or id.</param>
    /// <param name="spaces">The identifiers of its spaces; <see langword="null"/> or empty for every space.</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty, or a space is not the identifier of a space.</exception>
    /// <exception cref="InvalidDataException">The configuration cannot be parsed, or writes the resource in a form that is not edited here.</exception>
    /// <exception cref="InvalidOperationException">The provider is not defined in the configuration file.</exception>
    /// <exception cref="IOException">The configuration file cannot be read or written.</exception>
    public void SaveGlobalSpaceAssignment(SpaceResourceKind kind, string key, IEnumerable<string>? spaces)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var list = SpaceScope.Normalize(spaces ?? []);
        if (list.FirstOrDefault(static id => !SpaceDescriptor.IsValidId(id)) is { } invalid)
            throw new ArgumentException($"'{invalid}' is not the identifier of a space.", nameof(spaces));
        var (table, entry) = SpaceAssignments.Location(kind, key.Trim());
        EditConfigFile(_options.ConfigPath, content => ConfigTableSyntax.Set(content, table, entry, list, createTable: kind != SpaceResourceKind.Provider));
    }

    /// <summary>
    /// Edits a configuration file of CodeAlta, the user's or a project's, under the lock every save of the stores takes on
    /// it, in this process and in the others: the text is read, changed by <paramref name="edit"/> and written back
    /// before another writer reads it. The file is written only when the text changes, and only when the new text is
    /// still a configuration this version reads. A missing file is read as empty and created.
    /// </summary>
    /// <remarks>
    /// <paramref name="edit"/> runs on the calling thread while the lock is held: it changes the text and returns, without
    /// waiting for anything. <see cref="ConfigTableSyntax"/> changes one setting and leaves the rest of the text as it is.
    /// </remarks>
    /// <param name="path">The path of the file.</param>
    /// <param name="edit">Returns the new text of the file from its current text.</param>
    /// <returns><see langword="true"/> when the file was written; <see langword="false"/> when the text did not change.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or not a valid path.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="edit"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="edit"/> returned <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The new text is not a configuration this version reads.</exception>
    /// <exception cref="IOException">The file cannot be read or written, or another writer has held its lock for too long.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public static bool EditConfigFile(string path, Func<string, string> edit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(edit);
        path = Path.GetFullPath(path);
        using var writing = ConfigFileLock.Acquire(path);
        var content = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var updated = edit(content) ?? throw new InvalidOperationException("The edit of the configuration returned no text.");
        if (string.Equals(updated, content, StringComparison.Ordinal)) return false;
        // The edited text must still be a configuration this version reads before it replaces the file.
        try
        {
            _ = ParseDocument(updated, path);
        }
        catch (Exception ex) when (IsConfigLoadException(ex))
        {
            throw new InvalidDataException($"Failed to parse CodeAlta config '{path}'.", ex);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, updated);
        return true;
    }

    /// <summary>
    /// Persists a global plugin enablement override.
    /// </summary>
    /// <param name="pluginId">The built-in plugin id or source plugin package id.</param>
    /// <param name="enabled">A value indicating whether the plugin should be enabled.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="pluginId"/> is empty.</exception>
    public void SaveGlobalPluginEnabled(string pluginId, bool enabled)
    {
        using var writing = ConfigFileLock.Acquire(_options.ConfigPath);
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
        using var writing = ConfigFileLock.Acquire(path);
        var document = LoadDocument(path);
        SavePluginEnabled(path, document, pluginId, enabled);
    }

    /// <summary>
    /// Removes the global enablement override of a plugin. The other settings of its table are kept, and the
    /// table goes when nothing is left in it.
    /// </summary>
    /// <param name="pluginId">The built-in plugin id or source plugin package id.</param>
    /// <returns><see langword="true"/> when an override was removed; <see langword="false"/> when the file has none.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="pluginId"/> is empty.</exception>
    /// <exception cref="InvalidDataException">Thrown when the configuration file cannot be parsed.</exception>
    public bool RemoveGlobalPluginEnabled(string pluginId)
    {
        using var writing = ConfigFileLock.Acquire(_options.ConfigPath);
        return RemovePluginEnabled(_options.ConfigPath, LoadGlobal(), pluginId);
    }

    /// <summary>
    /// Removes the project-local enablement override of a plugin. The other settings of its table are kept, and
    /// the table goes when nothing is left in it.
    /// </summary>
    /// <param name="projectRoot">The project root directory.</param>
    /// <param name="pluginId">The source plugin package id.</param>
    /// <returns><see langword="true"/> when an override was removed; <see langword="false"/> when the file has none.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="projectRoot"/> or <paramref name="pluginId"/> is empty.</exception>
    /// <exception cref="InvalidDataException">Thrown when the configuration file cannot be parsed.</exception>
    public bool RemoveProjectPluginEnabled(string projectRoot, string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var path = GetProjectConfigPath(projectRoot);
        using var writing = ConfigFileLock.Acquire(path);
        return RemovePluginEnabled(path, LoadDocument(path), pluginId);
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
        using var writing = ConfigFileLock.Acquire(_options.ConfigPath);
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
        using var writing = ConfigFileLock.Acquire(_options.ConfigPath);
        var document = LoadGlobal();
        NormalizeDocument(document);
        document.Chat ??= new CodeAltaChatSettingsDocument();
        document.Chat.DefaultProvider = NormalizeProviderKey(providerKey);
        SaveDocument(_options.ConfigPath, document);
    }

    /// <summary>
    /// Persists where the git worktrees CodeAlta creates are placed, in the user's configuration file.
    /// </summary>
    /// <param name="location"><c>global</c>, <c>project</c> or <c>custom</c>; <c>global</c>, the default, is not written.</param>
    /// <param name="folder">The folder of the <c>custom</c> location; it is kept when another location is chosen.</param>
    public void SaveGlobalWorktreeSettings(string? location, string? folder)
    {
        using var writing = ConfigFileLock.Acquire(_options.ConfigPath);
        var document = LoadGlobal();
        NormalizeDocument(document);
        document.Worktrees = new CodeAltaWorktreeSettingsDocument { Location = location, Folder = folder };
        SaveDocument(_options.ConfigPath, document);
    }

    /// <summary>
    /// Persists the choices for tasks and plans, in the user's configuration file. A choice that is the default
    /// is not written.
    /// </summary>
    /// <param name="settings">The choices.</param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    public void SaveGlobalWorkItemSettings(WorkItems.WorkItemSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var defaults = WorkItems.WorkItemSettings.Default;
        using var writing = ConfigFileLock.Acquire(_options.ConfigPath);
        var document = LoadGlobal();
        NormalizeDocument(document);
        var tasks = new CodeAltaWorkItemSettingsDocument
        {
            Propose = settings.Propose == defaults.Propose ? null : settings.Propose,
            Notify = settings.Notify == defaults.Notify ? null : settings.Notify,
            CompletedTasks = settings.CompletedTasks == defaults.CompletedTasks ? null : WorkItems.WorkItemSettings.NameOf(settings.CompletedTasks),
            DismissedTasks = settings.DismissedTasks == defaults.DismissedTasks ? null : WorkItems.WorkItemSettings.NameOf(settings.DismissedTasks),
            CompletedPlans = settings.CompletedPlans == defaults.CompletedPlans ? null : WorkItems.WorkItemSettings.NameOf(settings.CompletedPlans),
            Start = settings.Start == defaults.Start ? null : WorkItems.WorkItemSettings.NameOf(settings.Start),
        };
        document.WorkItems = tasks is { Propose: null, Notify: null, CompletedTasks: null, DismissedTasks: null, CompletedPlans: null, Start: null } ? null : tasks;
        SaveDocument(_options.ConfigPath, document);
    }

    /// <summary>
    /// Reads how a session nobody named is titled, from the user's configuration file (<c>[sessions] title</c>).
    /// </summary>
    /// <returns>The mode; <see cref="CodeAlta.Agent.AgentSessionTitles.DefaultMode"/> when the file names none, or cannot be read.</returns>
    public CodeAlta.Agent.AgentSessionTitleMode LoadGlobalSessionTitleMode()
    {
        try
        {
            return CodeAlta.Agent.AgentSessionTitles.ParseMode(LoadGlobal().Sessions?.Title) ?? CodeAlta.Agent.AgentSessionTitles.DefaultMode;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException || IsConfigLoadException(exception))
        {
            return CodeAlta.Agent.AgentSessionTitles.DefaultMode;
        }
    }

    /// <summary>
    /// Persists how a session nobody named is titled, in the user's configuration file. The default is not written.
    /// </summary>
    /// <param name="mode">The mode.</param>
    public void SaveGlobalSessionTitleMode(CodeAlta.Agent.AgentSessionTitleMode mode)
    {
        using var writing = ConfigFileLock.Acquire(_options.ConfigPath);
        var document = LoadGlobal();
        NormalizeDocument(document);
        document.Sessions = mode == CodeAlta.Agent.AgentSessionTitles.DefaultMode
            ? null
            : new CodeAltaSessionSettingsDocument { Title = CodeAlta.Agent.AgentSessionTitles.NameOf(mode) };
        SaveDocument(_options.ConfigPath, document);
    }

    /// <summary>Saves global storage choices, omitting default fields and retaining unknown TOML settings.</summary>
    /// <exception cref="ArgumentNullException">The settings or retention is null.</exception>
    /// <exception cref="InvalidDataException">The duration or existing configuration is invalid.</exception>
    /// <exception cref="IOException">The configuration cannot be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the configuration is denied.</exception>
    public void SaveGlobalStorageSettings(Storage.StorageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.SessionRetention);
        if (settings.SessionRetention.GetValidationError() is { } error) throw new InvalidDataException(error);
        using var writing = ConfigFileLock.Acquire(_options.ConfigPath);
        var document = LoadGlobal();
        document.Storage = new CodeAltaStorageSettingsDocument
        {
            ClearScratchpadOnStartup = settings.ClearScratchpadOnStartup,
            SessionRetention = new CodeAltaSessionRetentionSettingsDocument
            {
                Enabled = settings.SessionRetention.Enabled,
                Amount = settings.SessionRetention.Amount,
                Unit = settings.SessionRetention.Unit,
            },
        };
        SaveDocument(_options.ConfigPath, document);
    }

    /// <summary>Saves the global choices for search. Defaults are not written.</summary>
    /// <param name="settings">The choices.</param>
    /// <exception cref="ArgumentNullException">The settings are null.</exception>
    /// <exception cref="InvalidDataException">The model, the mode or the existing configuration is invalid.</exception>
    /// <exception cref="IOException">The configuration cannot be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the configuration is denied.</exception>
    public void SaveGlobalSearchSettings(Search.SearchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if ((Search.SearchSettings.GetModelError(settings.Model) ?? Search.SearchSettings.GetPromptCatalogError(settings.PromptCatalog)
            ?? Search.SearchSettings.GetParallelismError(settings.Parallelism)) is { } error) throw new InvalidDataException(error);
        using var writing = ConfigFileLock.Acquire(_options.ConfigPath);
        var document = LoadGlobal();
        document.Search = new CodeAltaSearchSettingsDocument
        {
            Enabled = settings.Enabled, Sessions = settings.Sessions, Semantic = settings.Semantic, Model = settings.Model, PromptCatalog = settings.PromptCatalog,
            ExcludedProjects = [.. Search.SearchSettings.Normalize(settings.ExcludedProjects)],
            ExcludedSpaces = [.. Search.SearchSettings.Normalize(settings.ExcludedSpaces)],
            MeaningOffer = settings.MeaningOffer,
            Parallelism = settings.Parallelism,
        };
        SaveDocument(_options.ConfigPath, document);
    }

    /// <summary>Saves the global choices for speech to prompt. Defaults are not written.</summary>
    /// <exception cref="ArgumentNullException">The settings are null.</exception>
    /// <exception cref="InvalidDataException">The model or the existing configuration is invalid.</exception>
    /// <exception cref="IOException">The configuration cannot be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the configuration is denied.</exception>
    public void SaveGlobalSpeechSettings(Speech.SpeechSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if ((Speech.SpeechSettings.GetModelError(settings.Model) ?? Speech.SpeechSettings.GetSendError(settings.Send)) is { } error) throw new InvalidDataException(error);
        using var writing = ConfigFileLock.Acquire(_options.ConfigPath);
        var document = LoadGlobal();
        document.Speech = new CodeAltaSpeechSettingsDocument { Enabled = settings.Enabled, Model = settings.Model, Send = settings.Send };
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
    /// Saves the complete set of global provider definitions. The providers of the file whose type this version
    /// does not know are not part of that set: their sections stay as they are.
    /// </summary>
    /// <param name="definitions">The provider definitions to persist.</param>
    /// <exception cref="InvalidOperationException">
    /// A definition is not valid, or has the key of a provider whose type this version does not know.
    /// </exception>
    public void SaveGlobalProviderDefinitions(IEnumerable<CodeAltaProviderDocument> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        using var writing = ConfigFileLock.Acquire(_options.ConfigPath);
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

        // Saving under the key of a provider of a newer version would write into that provider's section.
        if (normalizedDefinitions.Keys.FirstOrDefault(key => IsUnsupported(document.UnsupportedProviders, key)) is { } taken)
        {
            throw new InvalidOperationException(
                $"providers.{taken} is in the configuration with a type this version of CodeAlta does not know: use another key.");
        }

        foreach (var definition in normalizedDefinitions.Values)
        {
            NormalizeProviderEntry(definition.ProviderKey, definition);
            ValidateProviderCredentialsForSave(definition);
        }

        document.Providers = normalizedDefinitions.Count == 0
            ? null
            : normalizedDefinitions;

        // A default that names a provider of a newer version is that version's choice: it stays in the file.
        var defaultProvider = NormalizeProviderKey(document.Chat?.DefaultProvider);
        if (!string.IsNullOrWhiteSpace(defaultProvider) &&
            !IsUnsupported(document.UnsupportedProviders, defaultProvider) &&
            !LoadEnabledProviderKeys(normalizedDefinitions.Values).Contains(defaultProvider))
        {
            document.Chat ??= new CodeAltaChatSettingsDocument();
            document.Chat.DefaultProvider = LoadEnabledProviderKeys(normalizedDefinitions.Values).FirstOrDefault();
        }

        SaveDocument(_options.ConfigPath, document);
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
            "max" => AgentReasoningEffort.Max,
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
            AgentReasoningEffort.Max => "max",
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
        _ = Storage.StorageSettings.Read(document.Storage);
        _ = Speech.SpeechSettings.Read(document.Speech);
        _ = Search.SearchSettings.Read(document.Search);
        _ = ModelTiers.ModelSettings.Read(document.Models);
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
        => ex is InvalidOperationException or FormatException or InvalidDataException or IOException or TomlException;

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
        var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var content = string.IsNullOrWhiteSpace(existing)
            ? TomlSerializer.Serialize(document)
            : MergeIntoExistingContent(existing, document, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // The typed document models only part of the file. Everything else (settings a plugin reads itself, such as
    // the MCP policy under [plugins.mcp], ignored legacy tables, settings of another version) must survive a save,
    // so the typed changes are applied to the file's own table model instead of replacing the file.
    private static string MergeIntoExistingContent(string existing, CodeAltaConfigDocument document, string path)
    {
        var options = TomlSerializerOptions.Default with
        {
            SourceName = path,
            // Carries the end-of-line comments of the settings that stay in place; full-line comments are not kept.
            MetadataStore = new TomlMetadataStore(),
            NewLine = existing.Contains("\r\n", StringComparison.Ordinal) ? TomlNewLineKind.CrLf : TomlNewLineKind.Lf,
        };
        TomlTable root;
        TomlTable modeled;
        IReadOnlyList<CodeAltaUnsupportedProvider> unsupported;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(existing, options) ?? new TomlTable();
            // The file as the typed document reads it, before any normalization: its keys are the modeled ones.
            var read = TomlSerializer.Deserialize<CodeAltaConfigDocument>(
                existing, TomlSerializerOptions.Default with { SourceName = path }) ?? new CodeAltaConfigDocument();
            modeled = ToTable(read);
            unsupported = [.. FindUnsupportedProviders(read).Select(static entry => entry.Provider)];
        }
        catch (Exception ex) when (IsConfigLoadException(ex))
        {
            throw new InvalidDataException($"Failed to parse CodeAlta config '{path}'.", ex);
        }

        var updated = ToTable(document);
        // The providers this version does not know are not in the typed document: neither their modeled
        // settings nor their tables are taken as dropped.
        ForgetProviders(modeled, unsupported);
        ApplyModeledSettings(root, modeled, updated);
        RemoveDroppedProviders(root, updated, unsupported);
        return TomlSerializer.Serialize(root, options);
    }

    private static TomlTable ToTable(CodeAltaConfigDocument document)
    {
        var content = TomlSerializer.Serialize(document);
        return string.IsNullOrWhiteSpace(content)
            ? new TomlTable()
            : TomlSerializer.Deserialize<TomlTable>(content) ?? new TomlTable();
    }

    // Gives every modeled key its updated value, or removes it when the typed document dropped it,
    // and leaves every key the typed document does not model as it is.
    private static void ApplyModeledSettings(TomlTable target, TomlTable modeled, TomlTable updated)
    {
        foreach (var (key, value) in modeled)
        {
            if (FindKey(updated, key) is not null || !target.TryGetValue(key, out var existing))
            {
                continue;
            }

            if (value is TomlTable modeledTable && existing is TomlTable existingTable)
            {
                ApplyModeledSettings(existingTable, modeledTable, new TomlTable());
                if (existingTable.Count > 0)
                {
                    continue; // Unmodeled settings remain in the table.
                }
            }

            target.Remove(key);
        }

        foreach (var (key, value) in updated)
        {
            // Normalization may change the case of a provider or plugin key; the file keeps its spelling.
            var targetKey = FindKey(modeled, key) ?? key;
            if (value is TomlTable updatedTable &&
                target.TryGetValue(targetKey, out var existing) && existing is TomlTable existingTable)
            {
                var modeledTable = modeled.TryGetValue(targetKey, out var modeledValue) ? modeledValue as TomlTable : null;
                ApplyModeledSettings(existingTable, modeledTable ?? new TomlTable(), updatedTable);
            }
            else
            {
                target[targetKey] = value;
            }
        }
    }

    // The settings this version models of a provider it does not know are not compared: they are not its to drop.
    private static void ForgetProviders(TomlTable modeled, IReadOnlyList<CodeAltaUnsupportedProvider> unsupported)
    {
        if (unsupported.Count == 0 || !modeled.TryGetValue("providers", out var value) || value is not TomlTable providers)
        {
            return;
        }

        foreach (var providerKey in providers.Keys.Where(key => IsUnsupported(unsupported, key)).ToArray())
        {
            providers.Remove(providerKey);
        }
    }

    private static bool IsUnsupported(IReadOnlyList<CodeAltaUnsupportedProvider> unsupported, string? providerKey)
        => NormalizeProviderKey(providerKey) is { } key
           && unsupported.Any(provider => string.Equals(provider.ProviderKey, key, StringComparison.OrdinalIgnoreCase));

    // A provider the typed document no longer has goes with its whole table: settings left behind
    // would bring it back as an incomplete definition on the next load. Not one this version does not know.
    private static void RemoveDroppedProviders(TomlTable root, TomlTable updated, IReadOnlyList<CodeAltaUnsupportedProvider> unsupported)
    {
        const string ProvidersKey = "providers";
        if (!root.TryGetValue(ProvidersKey, out var value) || value is not TomlTable providers)
        {
            return;
        }

        var kept = updated.TryGetValue(ProvidersKey, out var updatedValue) ? updatedValue as TomlTable : null;
        foreach (var providerKey in providers.Keys)
        {
            if ((kept is null || FindKey(kept, providerKey) is null) && !IsUnsupported(unsupported, providerKey))
            {
                providers.Remove(providerKey);
            }
        }

        if (kept is null && providers.Count == 0)
        {
            root.Remove(ProvidersKey);
        }
    }

    private static string? FindKey(TomlTable table, string key)
    {
        if (table.ContainsKey(key))
        {
            return key;
        }

        foreach (var pair in table)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Key;
            }
        }

        return null;
    }

    // Nothing is written when the file says nothing of the plugin: a file that does not exist is not created.
    private static bool RemovePluginEnabled(string path, CodeAltaConfigDocument document, string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        NormalizeDocument(document);
        var key = document.Plugins?.Keys.FirstOrDefault(key => string.Equals(key, pluginId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (key is null || document.Plugins![key].Enabled is null)
        {
            return false;
        }

        document.Plugins.Remove(key);
        if (document.Plugins.Count == 0)
        {
            document.Plugins = null;
        }

        SaveDocument(path, document);
        return true;
    }

    private static void SavePluginEnabled(string path, CodeAltaConfigDocument document, string pluginId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        NormalizeDocument(document);
        document.Plugins ??= new Dictionary<string, CodeAltaPluginSettingsDocument>(StringComparer.OrdinalIgnoreCase);
        document.Plugins[pluginId.Trim()] = new CodeAltaPluginSettingsDocument { Enabled = enabled };
        SaveDocument(path, document);
    }

    private static void SaveDisabledSkillNames(string path, IEnumerable<string> skillNames)
    {
        ArgumentNullException.ThrowIfNull(skillNames);
        var disabled = NormalizeSkillConfigNameList(skillNames);
        using var writing = ConfigFileLock.Acquire(path);
        var content = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        try
        {
            _ = ParseDocument(content, path);
        }
        catch (Exception ex) when (IsConfigLoadException(ex))
        {
            throw new InvalidDataException($"Failed to parse CodeAlta config '{path}'.", ex);
        }
        var updated = Skills.SkillConfigSyntax.UpdateDisabledNames(content, disabled ?? []);
        // Validate the edited document before writing; this remains ordinary config-owner persistence,
        // not a multi-file transaction or external-writer compare-and-swap.
        _ = ParseDocument(updated, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, updated);
    }

    private static IReadOnlySet<string> LoadDisabledSkillNames(CodeAltaConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        NormalizeDocument(document);
        return document.Skills?.Disabled is { Count: > 0 } disabled
            ? new HashSet<string>(disabled, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SetSkillEnabled(HashSet<string> disabled, string skillName, bool enabled)
    {
        if (enabled)
        {
            disabled.Remove(skillName);
        }
        else
        {
            disabled.Add(skillName);
        }
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


        if (document.Providers is not null)
        {
            // A type this version does not know is one a newer version added: the provider is set aside, not
            // offered, instead of the whole file being refused. Its section stays in the file as it is.
            var unsupported = FindUnsupportedProviders(document);
            if (unsupported.Length > 0)
            {
                foreach (var (key, _) in unsupported)
                {
                    document.Providers.Remove(key);
                }

                document.UnsupportedProviders = [.. document.UnsupportedProviders, .. unsupported.Select(static entry => entry.Provider)];
            }

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

        if (document.Worktrees is { } worktrees)
        {
            // The default location is the absence of one; a folder is kept for when `custom` is chosen again.
            var location = worktrees.Location?.Trim().ToLowerInvariant();
            worktrees.Location = location is "project" or "custom" ? location : null;
            worktrees.Folder = string.IsNullOrWhiteSpace(worktrees.Folder) ? null : worktrees.Folder.Trim();
            if (worktrees.Location is null && worktrees.Folder is null)
            {
                document.Worktrees = null;
            }
        }

        if (document.Sessions is { } sessions)
        {
            // The default is the absence of a mode.
            sessions.Title = CodeAlta.Agent.AgentSessionTitles.ParseMode(sessions.Title) is { } titleMode && titleMode != CodeAlta.Agent.AgentSessionTitles.DefaultMode
                ? CodeAlta.Agent.AgentSessionTitles.NameOf(titleMode)
                : null;
            if (sessions.Title is null)
            {
                document.Sessions = null;
            }
        }

        if (document.Storage is { } storage)
        {
            // Only known defaults are omitted. Unknown/invalid values must survive normalization so they
            // produce a visible validation failure, never silently enable a shorter destructive policy.
            if (storage.ClearScratchpadOnStartup == true) storage.ClearScratchpadOnStartup = null;
            if (storage.SessionRetention is { } retention)
            {
                var defaults = Storage.SessionRetentionSettings.Default;
                if (retention.Enabled == defaults.Enabled) retention.Enabled = null;
                if (retention.Amount == defaults.Amount) retention.Amount = null;
                if (retention.Unit == defaults.Unit) retention.Unit = null;
                if (retention is { Enabled: null, Amount: null, Unit: null }) storage.SessionRetention = null;
            }
            if (storage is { ClearScratchpadOnStartup: null, SessionRetention: null }) document.Storage = null;
        }

        if (document.Speech is { } speech)
        {
            // Only the defaults are omitted: an invalid model survives, so that validation reports it.
            if (speech.Enabled == true) speech.Enabled = null;
            if (speech.Model == Speech.SpeechSettings.DefaultModel) speech.Model = null;
            if (speech.Send == Speech.SpeechSettings.ManualSend) speech.Send = null;
            if (speech is { Enabled: null, Model: null, Send: null }) document.Speech = null;
        }

        if (document.Search is { } search)
        {
            // Only the defaults are omitted: an invalid value survives, so that validation reports it.
            if (search.Enabled == true) search.Enabled = null;
            if (search.Sessions == true) search.Sessions = null;
            if (search.Semantic == false) search.Semantic = null;
            if (search.Model == Search.SearchSettings.DefaultModel) search.Model = null;
            if (search.PromptCatalog == Search.SearchSettings.PromptCatalogAuto) search.PromptCatalog = null;
            if (search.ExcludedProjects is { Count: 0 }) search.ExcludedProjects = null;
            if (search.ExcludedSpaces is { Count: 0 }) search.ExcludedSpaces = null;
            if (string.IsNullOrWhiteSpace(search.MeaningOffer)) search.MeaningOffer = null;
            if (search.Parallelism == Search.SearchSettings.DefaultParallelism) search.Parallelism = null;
            if (search is { Enabled: null, Sessions: null, Semantic: null, Model: null, PromptCatalog: null, ExcludedProjects: null, ExcludedSpaces: null, MeaningOffer: null, Parallelism: null }) document.Search = null;
        }

        if (document.Models is { } models)
        {
            // Only empty tables are omitted: an invalid target survives, so that validation reports it.
            if (models.Tiers is { } tiers)
            {
                if (tiers.Complex is { Count: 0 }) tiers.Complex = null;
                if (tiers.Standard is { Count: 0 }) tiers.Standard = null;
                if (tiers.Quick is { Count: 0 }) tiers.Quick = null;
                if (tiers is { Complex: null, Standard: null, Quick: null }) models.Tiers = null;
            }

            if (models.Fallback is { Enabled: null or false, On: null }) models.Fallback = null;
            if (models is { Tiers: null, Fallback: null }) document.Models = null;
        }

        if (document.Skills is not null)
        {
            document.Skills.Disabled = NormalizeSkillConfigNameList(document.Skills.Disabled ?? []);
            if (document.Skills.Disabled is null)
            {
                document.Skills = null;
            }
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

    private static CodeAltaProviderDocument NormalizeProviderEntry(string key, CodeAltaProviderDocument? value)
    {
        var definition = value ?? new CodeAltaProviderDocument();
        definition.ProviderKey = NormalizeProviderEntryKey(key, definition) ?? string.Empty;
        definition.DisplayName = NormalizeText(definition.DisplayName);
        definition.Icon = NormalizeText(definition.Icon)?.ToLowerInvariant();
        definition.Color = NormalizeText(definition.Color);
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
        definition.ServiceTier = NormalizeText(definition.ServiceTier)?.ToLowerInvariant() switch
        {
            "fast" => "priority",
            var tier => tier,
        };
        definition.ModelDiscovery = NormalizeCodexSubscriptionModelDiscovery(definition.ModelDiscovery);
        definition.ResponseTransport = NormalizeCodexSubscriptionResponseTransport(definition.ResponseTransport);
        definition.InstallationIdSource = NormalizeCodexSubscriptionInstallationIdSource(definition.InstallationIdSource);
        definition.OrganizationId = NormalizeText(definition.OrganizationId);
        definition.ProjectId = NormalizeText(definition.ProjectId);
        definition.Project = NormalizeText(definition.Project);
        definition.Location = NormalizeText(definition.Location);
        definition.ModelsDevProviderId = NormalizeProviderKey(definition.ModelsDevProviderId);
        definition.SingleModelId = NormalizeModel(definition.SingleModelId);
        definition.ModelsIncludeRegex = NormalizeText(definition.ModelsIncludeRegex);
        definition.ExtraBody = NormalizeExtraBody(definition.ExtraBody);
        definition.Request = NormalizeRequest(definition.Request);
        definition.Profile = NormalizeProfile(definition.Profile);
        definition.ModelOverrides = NormalizeModelOverrides(definition.ModelOverrides);
        definition.ModelRequest = NormalizeModelRequests(definition.ModelRequest);
        definition.Compaction = NormalizeCompaction(definition.Compaction);
        CompleteAndValidateProviderDefinition(CloneProviderDefinition(definition));
        PruneProviderDefaults(definition);
        return definition;
    }

    private static string? NormalizeReasoningEffortText(string? value)
        => FormatReasoningEffort(ParseReasoningEffort(value));

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string>? NormalizeSkillConfigNameList(IEnumerable<string> values)
    {
        var normalized = values
            .Select(NormalizeSkillConfigName)
            .Where(static value => value is not null)
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return normalized.Count == 0 ? null : normalized;
    }

    private static string NormalizeSkillConfigNameForSave(string? value)
    {
        try
        {
            return NormalizeSkillConfigName(value)
                ?? throw new ArgumentException("Skill name is required.", nameof(value));
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException(ex.Message, nameof(value), ex);
        }
    }

    private static string? NormalizeSkillConfigName(string? value)
    {
        var normalized = NormalizeText(value)?.ToLowerInvariant();
        if (normalized is null)
        {
            return null;
        }

        if (normalized.Length > 64 ||
            normalized.StartsWith("-", StringComparison.Ordinal) ||
            normalized.EndsWith("-", StringComparison.Ordinal) ||
            normalized.Contains("--", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"skills.disabled contains invalid skill name '{value}'.");
        }

        foreach (var rune in normalized.EnumerateRunes())
        {
            if (rune.Value == '-')
            {
                continue;
            }

            if (!Rune.IsLetterOrDigit(rune) ||
                (Rune.IsLetter(rune) && Rune.ToLowerInvariant(rune) != rune))
            {
                throw new InvalidOperationException($"skills.disabled contains invalid skill name '{value}'.");
            }
        }

        return normalized;
    }

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

    private static void ValidateModelsIncludeRegex(CodeAltaProviderDocument definition)
    {
        if (string.IsNullOrWhiteSpace(definition.ModelsIncludeRegex))
        {
            return;
        }

        try
        {
            AgentModelCatalogFilter.ValidateIncludeRegex(definition.ModelsIncludeRegex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"providers.{definition.ProviderKey} models_include_regex must be a valid regular expression: {ex.Message}",
                ex);
        }
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

    // The providers of a document as it was read, by the key the file writes, whose type is written and that
    // this version cannot name. A missing type stays an error of the file.
    private static (string Key, CodeAltaUnsupportedProvider Provider)[] FindUnsupportedProviders(CodeAltaConfigDocument document)
        => [.. (document.Providers ?? [])
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value?.ProviderType))
            .Select(static entry => (entry.Key, Normalized: NormalizeProviderKey(entry.Key) ?? entry.Key.Trim(), Type: entry.Value.ProviderType!.Trim()))
            .Where(static entry => NormalizeProviderType(entry.Normalized, entry.Type) is null)
            .Select(static entry => (entry.Key, new CodeAltaUnsupportedProvider(entry.Normalized, entry.Type)))];

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
            "mistral" or "mistral-chat" or "mistral_chat" => "mistral",
            "typesafe" or "typesafe-ai" or "typesafe_ai" or "jev" => TypeSafeProviderType,
            "codex" => CodexSubscriptionProviderType,
            "copilot" => CopilotDirectProviderType,
            "xai" or "xai-grok" or "grok" or "x-ai" => XaiDirectProviderType,
            "claude-code" or "claude_code" or "claudecode" or "claude-cli" or "claude-code-cli" => ClaudeCodeProviderType,
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
                $"providers.{definition.ProviderKey} type must be one of: codex, copilot, openai-chat, openai-responses, azure-openai, anthropic, google-genai, vertex-ai, mistral, xai, claude-code, typesafe.");
        definition.Compaction = NormalizeAndCompleteCompactionSettings(definition.Compaction, DefaultCompaction);
        ApplyCodexSubscriptionDefaults(definition);
        ApplyCopilotDirectDefaults(definition);
        ApplyXaiDirectDefaults(definition);
        ApplyClaudeCodeDefaults(definition);
        if (string.Equals(definition.ProviderType, AnthropicProviderType, StringComparison.Ordinal))
        {
            // On is the absence of the setting.
            definition.RefusalFallback = definition.RefusalFallback == false ? false : null;
            // So are five minutes, the TTL the API keeps without one.
            definition.PromptCacheTtl = NormalizeText(definition.PromptCacheTtl)?.ToLowerInvariant() switch
            {
                null or "5m" => null,
                "1h" => "1h",
                var other => throw new InvalidOperationException($"providers.{definition.ProviderKey} prompt_cache_ttl must be \"5m\" or \"1h\", not \"{other}\"."),
            };
        }

        ValidateProviderFields(definition);
    }

    private static void ApplyCodexSubscriptionDefaults(CodeAltaProviderDocument definition)
    {
        if (!string.Equals(definition.ProviderType, CodexSubscriptionProviderType, StringComparison.Ordinal))
        {
            return;
        }

        definition.DisplayName ??= CodexSubscriptionDefaultDisplayName;
        if (definition.ApiUrl is "https://chatgpt.com/backend-api/codex" or "https://chatgpt.com/backend-api/codex/")
        {
            // Move the former built-in endpoint along with the replaced OAuth flow.
            definition.ApiUrl = CodexSubscriptionDefaultApiUrl;
        }

        definition.ApiUrl ??= CodexSubscriptionDefaultApiUrl;
        definition.AuthSource ??= CodexSubscriptionDefaultAuthSource;
        definition.MaxConcurrentRequests ??= CodexSubscriptionDefaultMaxConcurrentRequests;
        definition.TextVerbosity ??= CodexSubscriptionDefaultTextVerbosity;
        definition.IncludeEncryptedReasoning ??= true;
        definition.ModelDiscovery ??= CodexSubscriptionDefaultModelDiscovery;
        definition.ResponseTransport ??= CodexSubscriptionDefaultResponseTransport;
        definition.SendResponsesBetaHeader ??= false;
        definition.SendInstallationId ??= false;
        definition.InstallationIdSource ??= CodexSubscriptionDefaultInstallationIdSource;
        definition.Experimental ??= false;
        definition.CodexHome = NormalizeText(definition.CodexHome);
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

    private static void ApplyClaudeCodeDefaults(CodeAltaProviderDocument definition)
    {
        if (!string.Equals(definition.ProviderType, ClaudeCodeProviderType, StringComparison.Ordinal))
        {
            return;
        }

        definition.DisplayName ??= ClaudeCodeDefaultDisplayName;
        definition.Command = NormalizeText(definition.Command);
        definition.PermissionMode = NormalizeText(definition.PermissionMode);
        definition.AnthropicApiKey = NormalizeText(definition.AnthropicApiKey);
        definition.ConfigDir = NormalizeText(definition.ConfigDir);
        // Off is the absence of the setting.
        definition.PromptSuggestions = definition.PromptSuggestions == true ? true : null;
        if (definition.Arguments is { Count: 0 })
        {
            definition.Arguments = null;
        }
    }

    private static void ValidateProviderFields(CodeAltaProviderDocument definition)
    {
        if (!string.Equals(definition.ProviderType, ClaudeCodeProviderType, StringComparison.Ordinal))
        {
            RejectUnsupportedField(definition, "command", definition.Command);
            RejectUnsupportedField(definition, "args", definition.Arguments is { Count: > 0 } ? definition.Arguments : null);
            RejectUnsupportedField(definition, "permission_mode", definition.PermissionMode);
            RejectUnsupportedField(definition, "anthropic_api_key", definition.AnthropicApiKey);
            RejectUnsupportedField(definition, "prompt_suggestions", definition.PromptSuggestions);
            RejectUnsupportedField(definition, "config_dir", definition.ConfigDir);
        }

        if (!string.Equals(definition.ProviderType, AnthropicProviderType, StringComparison.Ordinal))
        {
            RejectUnsupportedField(definition, "refusal_fallback", definition.RefusalFallback);
            RejectUnsupportedField(definition, "prompt_cache_ttl", definition.PromptCacheTtl);
        }

        if (!string.Equals(definition.ProviderType, CodexSubscriptionProviderType, StringComparison.Ordinal))
        {
            RejectUnsupportedField(definition, "service_tier", definition.ServiceTier);
            RejectUnsupportedField(definition, "codex_home", definition.CodexHome);
        }

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

        if (!IsOpenAISdkProviderType(definition.ProviderType))
        {
            RejectUnsupportedField(definition, "network_timeout_seconds", definition.NetworkTimeoutSeconds);
        }

        if (definition.NetworkTimeoutSeconds is <= 0)
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} network_timeout_seconds must be greater than zero.");
        }

        ValidateModelsIncludeRegex(definition);
        ValidateFolderField(definition, "config_dir", definition.ConfigDir);
        ValidateFolderField(definition, "codex_home", definition.CodexHome);

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
                RejectUnsupportedField(definition, "request", definition.Request);
                RejectUnsupportedField(definition, "model_request", definition.ModelRequest);
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
                RejectUnsupportedField(definition, "extra_body", definition.ExtraBody);
                RejectUnsupportedField(definition, "request", definition.Request);
                RejectUnsupportedField(definition, "model_request", definition.ModelRequest);
                ValidateCopilotDirectFields(definition);
                break;

            case "anthropic":
                RejectUnsupportedField(definition, "organization_id", definition.OrganizationId);
                RejectUnsupportedField(definition, "project_id", definition.ProjectId);
                RejectUnsupportedField(definition, "project", definition.Project);
                RejectUnsupportedField(definition, "location", definition.Location);
                RejectUnsupportedField(definition, "extra_body", definition.ExtraBody);
                RejectRequestExtraBodyFields(definition);
                RejectUnsupportedField(definition, "model_request", definition.ModelRequest);
                break;

            case "google-genai":
                RejectUnsupportedField(definition, "organization_id", definition.OrganizationId);
                RejectUnsupportedField(definition, "project_id", definition.ProjectId);
                RejectUnsupportedField(definition, "project", definition.Project);
                RejectUnsupportedField(definition, "location", definition.Location);
                RejectUnsupportedField(definition, "extra_body", definition.ExtraBody);
                RejectRequestExtraBodyFields(definition);
                RejectUnsupportedField(definition, "model_request", definition.ModelRequest);
                break;

            case "mistral":
                RejectUnsupportedField(definition, "organization_id", definition.OrganizationId);
                RejectUnsupportedField(definition, "project_id", definition.ProjectId);
                RejectUnsupportedField(definition, "project", definition.Project);
                RejectUnsupportedField(definition, "location", definition.Location);
                RejectUnsupportedField(definition, "extra_body", definition.ExtraBody);
                RejectRequestExtraBodyFields(definition);
                RejectUnsupportedField(definition, "model_request", definition.ModelRequest);
                break;

            case TypeSafeProviderType:
                // A decision model: no chat request is ever sent to it, so nothing that shapes one applies.
                RejectUnsupportedField(definition, "reasoning_effort", definition.ReasoningEffort);
                RejectUnsupportedField(definition, "organization_id", definition.OrganizationId);
                RejectUnsupportedField(definition, "project_id", definition.ProjectId);
                RejectUnsupportedField(definition, "project", definition.Project);
                RejectUnsupportedField(definition, "location", definition.Location);
                RejectUnsupportedField(definition, "single_model_id", definition.SingleModelId);
                RejectUnsupportedField(definition, "models_dev_provider_id", definition.ModelsDevProviderId);
                RejectUnsupportedField(definition, "extra_body", definition.ExtraBody);
                RejectUnsupportedField(definition, "request", definition.Request);
                RejectUnsupportedField(definition, "model_request", definition.ModelRequest);
                RejectUnsupportedField(definition, "profile", definition.Profile);
                break;

            case "vertex-ai":
                RejectUnsupportedField(definition, "api_key", definition.ApiKey);
                RejectUnsupportedField(definition, "api_key_env", definition.ApiKeyEnv);
                RejectUnsupportedField(definition, "organization_id", definition.OrganizationId);
                RejectUnsupportedField(definition, "project_id", definition.ProjectId);
                RejectUnsupportedField(definition, "extra_body", definition.ExtraBody);
                RejectRequestExtraBodyFields(definition);
                RejectUnsupportedField(definition, "model_request", definition.ModelRequest);
                if (definition.Enabled != false && string.IsNullOrWhiteSpace(definition.Project))
                {
                    throw new InvalidOperationException($"providers.{definition.ProviderKey} project is required for type 'vertex-ai'.");
                }

                if (definition.Enabled != false && string.IsNullOrWhiteSpace(definition.Location))
                {
                    throw new InvalidOperationException($"providers.{definition.ProviderKey} location is required for type 'vertex-ai'.");
                }

                break;

            case ClaudeCodeProviderType:
                // The CLI authenticates by itself, with what the user set up for it: CodeAlta holds no credential
                // and names no endpoint for it.
                RejectUnsupportedField(definition, "api_key", definition.ApiKey);
                RejectUnsupportedField(definition, "api_key_env", definition.ApiKeyEnv);
                RejectUnsupportedField(definition, "api_url", definition.ApiUrl);
                RejectUnsupportedField(definition, "organization_id", definition.OrganizationId);
                RejectUnsupportedField(definition, "project_id", definition.ProjectId);
                RejectUnsupportedField(definition, "project", definition.Project);
                RejectUnsupportedField(definition, "location", definition.Location);
                RejectUnsupportedField(definition, "models_dev_provider_id", definition.ModelsDevProviderId);
                RejectUnsupportedField(definition, "extra_body", definition.ExtraBody);
                RejectUnsupportedField(definition, "request", definition.Request);
                RejectUnsupportedField(definition, "model_request", definition.ModelRequest);
                if (definition.PermissionMode is { } permissionMode &&
                    !ClaudeCodePermissionModes.Contains(permissionMode, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"providers.{definition.ProviderKey} permission_mode must be one of: {string.Join(", ", ClaudeCodePermissionModes)}.");
                }

                if (definition.AnthropicApiKey is { } apiKeyChoice &&
                    !ClaudeCodeApiKeyChoices.Contains(apiKeyChoice, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"providers.{definition.ProviderKey} anthropic_api_key must be one of: {string.Join(", ", ClaudeCodeApiKeyChoices)}.");
                }

                if (definition.Arguments is { } arguments && arguments.Exists(static argument => string.IsNullOrWhiteSpace(argument)))
                {
                    throw new InvalidOperationException($"providers.{definition.ProviderKey} args must not contain empty values.");
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
             string.Equals(definition.ProviderType, XaiDirectProviderType, StringComparison.Ordinal) ||
             string.Equals(definition.ProviderType, TypeSafeProviderType, StringComparison.Ordinal)) &&
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

    private static void RejectRequestExtraBodyFields(CodeAltaProviderDocument definition)
    {
        RejectUnsupportedField(definition, "request.extra_body", definition.Request?.ExtraBody);
        RejectUnsupportedField(definition, "request.remove_extra_body", definition.Request?.RemoveExtraBody);
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
        if (definition.ServiceTier is not (null or "default" or "priority"))
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} service_tier must be one of: default, priority, fast (alias for priority).");
        }

        if (definition.MaxConcurrentRequests is <= 0)
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} max_concurrent_requests must be greater than zero.");
        }

        if (definition.AuthSource != "codealta_oauth")
        {
            throw new InvalidOperationException($"providers.{definition.ProviderKey} auth_source must be codealta_oauth. Use Continue with ChatGPT in CodeAlta; legacy Codex credential imports are not supported.");
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

    private static bool IsOpenAISdkProviderType(string? providerType)
        => providerType is "openai-chat" or "openai-responses" or "azure-openai" or CodexSubscriptionProviderType;

    // A folder the CLI of a provider is given: a relative one would depend on the folder CodeAlta was started in.
    private static void ValidateFolderField(CodeAltaProviderDocument definition, string fieldName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value == "~" || value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal) ||
            Path.IsPathRooted(value))
        {
            return;
        }

        throw new InvalidOperationException($"providers.{definition.ProviderKey} {fieldName} must be a full path or start with ~.");
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
        profile.ThinkingFormat = NormalizeText(profile.ThinkingFormat);
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

    private static CodeAltaProviderRequestDocument? NormalizeRequest(CodeAltaProviderRequestDocument? request)
    {
        if (request is null)
        {
            return null;
        }

        request.Headers = NormalizeDictionary(request.Headers);
        request.RemoveHeaders = NormalizeList(request.RemoveHeaders);
        request.ExtraBody = NormalizeExtraBody(request.ExtraBody);
        request.RemoveExtraBody = NormalizeList(request.RemoveExtraBody);
        return request.Headers is { Count: > 0 } ||
               request.RemoveHeaders is { Count: > 0 } ||
               request.ExtraBody is { Count: > 0 } ||
               request.RemoveExtraBody is { Count: > 0 }
            ? request
            : null;
    }

    private static Dictionary<string, CodeAltaProviderModelRequestDocument>? NormalizeModelRequests(
        Dictionary<string, CodeAltaProviderModelRequestDocument>? requests)
    {
        if (requests is null)
        {
            return null;
        }

        var normalized = requests
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Key))
            .Select(static entry => KeyValuePair.Create(entry.Key.Trim(), NormalizeModelRequest(entry.Value)))
            .Where(static entry => entry.Value is not null)
            .ToDictionary(
                static entry => entry.Key,
                static entry => entry.Value!,
                StringComparer.OrdinalIgnoreCase);
        return normalized.Count == 0 ? null : normalized;
    }

    private static CodeAltaProviderModelRequestDocument? NormalizeModelRequest(CodeAltaProviderModelRequestDocument? request)
    {
        if (request is null)
        {
            return null;
        }

        request.Headers = NormalizeDictionary(request.Headers);
        request.RemoveHeaders = NormalizeList(request.RemoveHeaders);
        request.ExtraBody = NormalizeExtraBody(request.ExtraBody);
        request.RemoveExtraBody = NormalizeList(request.RemoveExtraBody);
        return request.Headers is { Count: > 0 } ||
               request.RemoveHeaders is { Count: > 0 } ||
               request.ExtraBody is { Count: > 0 } ||
               request.RemoveExtraBody is { Count: > 0 }
            ? request
            : null;
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

            if (definition.ServiceTier == "default")
            {
                definition.ServiceTier = null;
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

            if (definition.SendResponsesBetaHeader == false)
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

        if (string.Equals(definition.ProviderType, ClaudeCodeProviderType, StringComparison.Ordinal) &&
            string.Equals(definition.DisplayName, ClaudeCodeDefaultDisplayName, StringComparison.Ordinal))
        {
            definition.DisplayName = null;
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
               !string.IsNullOrWhiteSpace(definition.Icon) ||
               !string.IsNullOrWhiteSpace(definition.Color) ||
               !string.IsNullOrWhiteSpace(definition.ProviderType) ||
               !string.IsNullOrWhiteSpace(definition.Model) ||
               !string.IsNullOrWhiteSpace(definition.ReasoningEffort) ||
               !string.IsNullOrWhiteSpace(definition.ApiKey) ||
               !string.IsNullOrWhiteSpace(definition.ApiKeyEnv) ||
               !string.IsNullOrWhiteSpace(definition.ApiUrl) ||
               definition.NetworkTimeoutSeconds is not null ||
               !string.IsNullOrWhiteSpace(definition.Command) ||
               definition.Arguments is { Count: > 0 } ||
               !string.IsNullOrWhiteSpace(definition.PermissionMode) ||
               !string.IsNullOrWhiteSpace(definition.AnthropicApiKey) ||
               definition.PromptSuggestions is not null ||
               definition.RefusalFallback is not null ||
               !string.IsNullOrWhiteSpace(definition.PromptCacheTtl) ||
               !string.IsNullOrWhiteSpace(definition.ConfigDir) ||
               !string.IsNullOrWhiteSpace(definition.CodexHome) ||
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
               !string.IsNullOrWhiteSpace(definition.ServiceTier) ||
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
               !string.IsNullOrWhiteSpace(definition.ModelsIncludeRegex) ||
               definition.SortModels is not null ||
               definition.ExtraBody is { Count: > 0 } ||
               definition.Request is not null ||
               definition.Profile is not null ||
               definition.Compaction is not null ||
               definition.ModelOverrides is { Count: > 0 } ||
               definition.ModelRequest is { Count: > 0 };
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
            // A decision provider runs no session, so it is never the default provider of new sessions.
            .Where(static definition => definition.Enabled != false && !IsDecisionProviderType(definition.ProviderType))
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
            case TypeSafeProviderType:
                if (string.IsNullOrWhiteSpace(definition.ApiKey) &&
                    string.IsNullOrWhiteSpace(definition.ApiKeyEnv))
                {
                    throw new InvalidOperationException($"providers.{definition.ProviderKey} requires api_key or api_key_env when enabled.");
                }

                break;
        }
    }

    /// <summary>
    /// Copies a provider definition, with its tables and lists, as it is: no default is filled in.
    /// </summary>
    /// <param name="definition">The definition to copy.</param>
    /// <returns>A copy that shares nothing the caller can change with <paramref name="definition"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    public static CodeAltaProviderDocument CloneProviderDefinition(CodeAltaProviderDocument definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new CodeAltaProviderDocument
        {
            ProviderKey = definition.ProviderKey,
            Enabled = definition.Enabled,
            DisplayName = definition.DisplayName,
            Icon = definition.Icon,
            Color = definition.Color,
            ProviderType = definition.ProviderType,
            Model = definition.Model,
            ReasoningEffort = definition.ReasoningEffort,
            ApiKey = definition.ApiKey,
            ApiKeyEnv = definition.ApiKeyEnv,
            ApiUrl = definition.ApiUrl,
            NetworkTimeoutSeconds = definition.NetworkTimeoutSeconds,
            Command = definition.Command,
            Arguments = definition.Arguments is null ? null : [.. definition.Arguments],
            PermissionMode = definition.PermissionMode,
            AnthropicApiKey = definition.AnthropicApiKey,
            PromptSuggestions = definition.PromptSuggestions,
            RefusalFallback = definition.RefusalFallback,
            PromptCacheTtl = definition.PromptCacheTtl,
            ConfigDir = definition.ConfigDir,
            CodexHome = definition.CodexHome,
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
            ServiceTier = definition.ServiceTier,
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
            ModelsIncludeRegex = definition.ModelsIncludeRegex,
            SortModels = definition.SortModels,
            ExtraBody = CloneExtraBody(definition.ExtraBody),
            Request = CloneRequest(definition.Request),
            Profile = CloneProfile(definition.Profile),
            Compaction = CloneCompaction(definition.Compaction),
            ModelOverrides = CloneModelOverrides(definition.ModelOverrides),
            ModelRequest = CloneModelRequests(definition.ModelRequest),
        };
    }

    private static CodeAltaProviderRequestDocument? CloneRequest(CodeAltaProviderRequestDocument? request)
    {
        if (request is null)
        {
            return null;
        }

        return new CodeAltaProviderRequestDocument
        {
            Headers = request.Headers is null ? null : new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase),
            RemoveHeaders = request.RemoveHeaders is null ? null : [.. request.RemoveHeaders],
            ExtraBody = CloneExtraBody(request.ExtraBody),
            RemoveExtraBody = request.RemoveExtraBody is null ? null : [.. request.RemoveExtraBody],
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

        if (pruned.Enabled == AgentCompactionSettings.DefaultEnabled)
        {
            pruned.Enabled = null;
        }

        if (pruned.Ratio == AgentCompactionSettings.DefaultRatio)
        {
            pruned.Ratio = null;
        }

        if (pruned.SummaryOutputRatio == AgentCompactionSettings.DefaultSummaryOutputRatio)
        {
            pruned.SummaryOutputRatio = null;
        }

        if (pruned.PostCompactionTargetRatio == AgentCompactionSettings.DefaultPostCompactionTargetRatio)
        {
            pruned.PostCompactionTargetRatio = null;
        }

        if (pruned.SummaryShareOfTarget == AgentCompactionSettings.DefaultSummaryShareOfTarget)
        {
            pruned.SummaryShareOfTarget = null;
        }

        if (pruned.FileContextShareOfSummaryTarget == AgentCompactionSettings.DefaultFileContextShareOfSummaryTarget)
        {
            pruned.FileContextShareOfSummaryTarget = null;
        }

        if (pruned.KeepLastUserMessage == AgentCompactionSettings.DefaultKeepLastUserMessage)
        {
            pruned.KeepLastUserMessage = null;
        }

        if (pruned.AllowSplitTurn == AgentCompactionSettings.DefaultAllowSplitTurn)
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

        merged.Enabled ??= AgentCompactionSettings.DefaultEnabled;
        merged.Ratio ??= AgentCompactionSettings.DefaultRatio;
        merged.SummaryOutputRatio ??= AgentCompactionSettings.DefaultSummaryOutputRatio;
        merged.PostCompactionTargetRatio ??= AgentCompactionSettings.DefaultPostCompactionTargetRatio;
        merged.SummaryShareOfTarget ??= AgentCompactionSettings.DefaultSummaryShareOfTarget;
        merged.FileContextShareOfSummaryTarget ??= AgentCompactionSettings.DefaultFileContextShareOfSummaryTarget;
        merged.KeepLastUserMessage ??= AgentCompactionSettings.DefaultKeepLastUserMessage;
        merged.AllowSplitTurn ??= AgentCompactionSettings.DefaultAllowSplitTurn;

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
            compaction.SummaryOutputRatio > AgentCompactionSettings.MaxSummaryOutputRatio)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"provider compaction summary_output_ratio must be > 0 and <= {AgentCompactionSettings.MaxSummaryOutputRatio:0.##}."));
        }

        if (compaction.PostCompactionTargetRatio is not > 0 ||
            compaction.PostCompactionTargetRatio > AgentCompactionSettings.MaxPostCompactionTargetRatio)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"provider compaction post_compaction_target_ratio must be > 0 and <= {AgentCompactionSettings.MaxPostCompactionTargetRatio:0.##}."));
        }

        if (compaction.SummaryShareOfTarget is not > 0 ||
            compaction.SummaryShareOfTarget > AgentCompactionSettings.MaxSummaryShareOfTarget)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"provider compaction summary_share_of_target must be > 0 and <= {AgentCompactionSettings.MaxSummaryShareOfTarget:0.##}."));
        }

        if (compaction.FileContextShareOfSummaryTarget is not >= 0 ||
            compaction.FileContextShareOfSummaryTarget > AgentCompactionSettings.MaxFileContextShareOfSummaryTarget)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"provider compaction file_context_share_of_summary_target must be >= 0 and <= {AgentCompactionSettings.MaxFileContextShareOfSummaryTarget:0.##}."));
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

    private static Dictionary<string, CodeAltaProviderModelRequestDocument>? CloneModelRequests(
        Dictionary<string, CodeAltaProviderModelRequestDocument>? requests)
    {
        if (requests is null || requests.Count == 0)
        {
            return null;
        }

        var clone = new Dictionary<string, CodeAltaProviderModelRequestDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in requests)
        {
            var request = CloneModelRequest(entry.Value);
            if (!string.IsNullOrWhiteSpace(entry.Key) && request is not null)
            {
                clone[entry.Key.Trim()] = request;
            }
        }

        return clone.Count == 0 ? null : clone;
    }

    private static CodeAltaProviderModelRequestDocument? CloneModelRequest(CodeAltaProviderModelRequestDocument? request)
    {
        if (request is null)
        {
            return null;
        }

        return new CodeAltaProviderModelRequestDocument
        {
            Headers = request.Headers is null ? null : new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase),
            RemoveHeaders = request.RemoveHeaders is null ? null : [.. request.RemoveHeaders],
            ExtraBody = CloneExtraBody(request.ExtraBody),
            RemoveExtraBody = request.RemoveExtraBody is null ? null : [.. request.RemoveExtraBody],
        };
    }

    private static void ThrowIfLegacyConfigShapeDetected(string content, string? sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        // A legacy key is a key: the text of a string value (the prompt of an automation, a description) is not one.
        var normalized = MaskTomlStrings(content).ToLowerInvariant();
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

    /// <summary>
    /// Returns the content with the text of every TOML string replaced by spaces, offsets and line breaks unchanged.
    /// </summary>
    internal static string MaskTomlStrings(string content)
    {
        var masked = content.ToCharArray();
        var index = 0;
        while (index < masked.Length)
        {
            var current = masked[index];
            if (current == '#')
            {
                // A comment runs to the end of its line; a quote in it opens nothing.
                while (index < masked.Length && masked[index] != '\n') index++;
            }
            else if (current is '"' or '\'')
            {
                var multiline = index + 2 < masked.Length && masked[index + 1] == current && masked[index + 2] == current;
                index += multiline ? 3 : 1;
                while (index < masked.Length)
                {
                    if (masked[index] == current && (!multiline
                        || index + 2 < masked.Length && masked[index + 1] == current && masked[index + 2] == current))
                    {
                        index += multiline ? 3 : 1;
                        break;
                    }

                    // An unterminated single-line string ends with its line, as it does for the parser.
                    if (!multiline && masked[index] == '\n') break;
                    if (current == '"' && masked[index] == '\\' && index + 1 < masked.Length && masked[index + 1] is not ('\r' or '\n'))
                    {
                        masked[index++] = ' ';
                    }

                    if (masked[index] is not ('\r' or '\n')) masked[index] = ' ';
                    index++;
                }
            }
            else
            {
                index++;
            }
        }

        return new string(masked);
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
