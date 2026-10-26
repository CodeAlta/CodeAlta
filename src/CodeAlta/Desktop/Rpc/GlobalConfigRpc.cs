using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CodeAlta.Agent;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.Hosting;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Reads, validates and saves the global <c>config.toml</c> for the desktop Settings editor, and can
/// re-register the configured model providers after a save (the TUI's "Save and Apply").
/// </summary>
[NeoRpcService("globalConfig", Version = 1)]
internal sealed partial class GlobalConfigService
{
    /// <summary>Largest configuration text accepted or returned, in UTF-16 units.</summary>
    internal const int MaximumContentLength = 256 * 1024;

    private const int MaximumMessageLength = 512;
    private readonly CodeAltaConfigStore? _store;
    private readonly ModelProviderRegistry? _registry;
    private readonly string? _stateRoot;
    private readonly string? _epoch;
    private readonly Lock _gate = new();
    private ProviderDefaults? _defaults; // Parsed on the first listing, under the gate.

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal GlobalConfigService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="store">The configuration store of the owned global root.</param>
    /// <param name="registry">The host's provider registry, updated when a save applies providers.</param>
    /// <param name="stateRoot">The state root forwarded to provider registrations.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stateRoot"/> or <paramref name="epoch"/> is blank.</exception>
    internal GlobalConfigService(CodeAltaConfigStore store, ModelProviderRegistry registry, string stateRoot, string epoch)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _store = store;
        _registry = registry;
        _stateRoot = stateRoot;
        _epoch = epoch;
    }

    /// <summary>
    /// Gets the models.dev catalog the providers registered again describe their models with, or <see langword="null"/>
    /// for none. The application owns it.
    /// </summary>
    internal ModelsDevCatalogService? ModelCatalog { get; init; }

    /// <summary>Reads the current configuration text and its revision.</summary>
    [NeoRpcMethod("read")]
    public GlobalConfigReadResponse Read(GlobalConfigReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store is null) return new("unavailable", null, null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null, null);
        try
        {
            string content;
            lock (_gate) content = _store.LoadGlobalConfigContent();
            return content.Length > MaximumContentLength ? new("too_large", null, null) : new("ok", content, Revision(content));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new("read_failed", null, null);
        }
    }

    /// <summary>Validates configuration text without writing it or starting providers.</summary>
    [NeoRpcMethod("validate")]
    public GlobalConfigValidationResponse Validate(GlobalConfigValidateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Content is null || request.Content.Length > MaximumContentLength)
            return new(false, "The configuration exceeds the editor limit.", null, null);
        var result = CodeAltaConfigStore.ValidateGlobalConfigContent(request.Content);
        return new(result.IsValid, Bound(result.Message), result.Line, result.Column) { Warning = Bound(result.Warning) };
    }

    /// <summary>
    /// Saves configuration text when it is valid and the file still has the revision the editor read,
    /// then optionally re-registers the configured providers.
    /// </summary>
    [NeoRpcMethod("save")]
    public GlobalConfigSaveResponse Save(GlobalConfigSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store is null || _registry is null) return Failure("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Failure("stale_epoch");
        if (request.Content is null || request.Content.Length > MaximumContentLength) return Failure("too_large");
        var validation = CodeAltaConfigStore.ValidateGlobalConfigContent(request.Content);
        if (!validation.IsValid) return new("invalid", null, Bound(validation.Message), validation.Line, validation.Column, 0);
        lock (_gate)
        {
            try
            {
                // Another editor (the TUI, a text editor) may have changed the file since it was read; the other
                // writers of the application wait until the check and the write are done.
                using var writing = _store.LockGlobalConfig();
                if (!string.Equals(Revision(_store.LoadGlobalConfigContent()), request.ExpectedRevision, StringComparison.Ordinal))
                    return Failure("conflict");
                _store.SaveGlobalConfigContent(request.Content);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                return Failure("write_failed");
            }

            var revision = Revision(request.Content);
            if (!request.ApplyProviders) return new("ok", revision, null, null, null, 0);
            try
            {
                return new("ok", revision, null, null, null, ApplyProviders(_store, _registry, _stateRoot!, PluginProviderTypes, ModelCatalog));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
            {
                // The file is saved; only the in-process provider registration failed.
                return new("apply_failed", revision, null, null, null, 0);
            }
        }
    }

    /// <summary>Reads how a session nobody named is titled: the <c>[sessions] title</c> setting.</summary>
    [NeoRpcMethod("sessionTitles")]
    public GlobalConfigSessionTitlesResponse SessionTitles(GlobalConfigSessionTitlesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store is null) return new("unavailable", null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null);
        lock (_gate) return new("ok", CodeAlta.Agent.AgentSessionTitles.NameOf(_store.LoadGlobalSessionTitleMode()));
    }

    /// <summary>
    /// Saves how a session nobody named is titled. A session that follows the setting takes the mode with its next
    /// prompt; a title somebody gave stays.
    /// </summary>
    [NeoRpcMethod("saveSessionTitles")]
    public GlobalConfigSessionTitlesResponse SaveSessionTitles(GlobalConfigSaveSessionTitlesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store is null) return new("unavailable", null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null);
        if (CodeAlta.Agent.AgentSessionTitles.ParseMode(request.Mode) is not { } mode) return new("invalid", null);
        lock (_gate)
        {
            try
            {
                _store.SaveGlobalSessionTitleMode(mode);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or FormatException)
            {
                return new("write_failed", null);
            }
        }

        return new("ok", CodeAlta.Agent.AgentSessionTitles.NameOf(mode));
    }

    /// <summary>Reads whether the user reviews the changes of every session before an agent commits them (<c>[review] before_commit</c>).</summary>
    [NeoRpcMethod("reviewSettings")]
    public GlobalConfigReviewSettingsResponse ReviewSettings(GlobalConfigSessionTitlesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store is null) return new("unavailable", false);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", false);
        lock (_gate) return new("ok", _store.LoadGlobalReviewBeforeCommit());
    }

    /// <summary>
    /// Saves whether the user reviews the changes of every session before an agent commits them. A session with a
    /// choice of its own keeps it; the others follow at their next command, and their agent is told at the next prompt.
    /// </summary>
    [NeoRpcMethod("saveReviewSettings")]
    public GlobalConfigReviewSettingsResponse SaveReviewSettings(GlobalConfigSaveReviewSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store is null) return new("unavailable", false);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", false);
        lock (_gate)
        {
            try
            {
                _store.SaveGlobalReviewBeforeCommit(request.BeforeCommit);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or FormatException)
            {
                return new("write_failed", _store.LoadGlobalReviewBeforeCommit());
            }

            return new("ok", _store.LoadGlobalReviewBeforeCommit());
        }
    }

    /// <summary>Reads whether agents name CodeAlta as a co-author of their commits (<c>[commits] co_author</c>).</summary>
    [NeoRpcMethod("commitSettings")]
    public GlobalConfigCommitSettingsResponse CommitSettings(GlobalConfigSessionTitlesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store is null) return new("unavailable", true);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", true);
        lock (_gate) return new("ok", _store.LoadGlobalCommitCoAuthor());
    }

    /// <summary>Saves whether agents name CodeAlta as a co-author of their commits; a session is told at its next prompt.</summary>
    [NeoRpcMethod("saveCommitSettings")]
    public GlobalConfigCommitSettingsResponse SaveCommitSettings(GlobalConfigSaveCommitSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store is null) return new("unavailable", true);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", true);
        lock (_gate)
        {
            try
            {
                _store.SaveGlobalCommitCoAuthor(request.CoAuthor);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or FormatException)
            {
                return new("write_failed", _store.LoadGlobalCommitCoAuthor());
            }

            return new("ok", _store.LoadGlobalCommitCoAuthor());
        }
    }

    /// <summary>Reads whether the model suggests the next prompt at the end of a turn (<c>[sessions] prompt_suggestions</c>).</summary>
    [NeoRpcMethod("promptSuggestionSettings")]
    public GlobalConfigPromptSuggestionSettingsResponse PromptSuggestionSettings(GlobalConfigSessionTitlesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store is null) return new("unavailable", true);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", true);
        lock (_gate) return new("ok", _store.LoadGlobalPromptSuggestions());
    }

    /// <summary>Saves whether the model suggests the next prompt at the end of a turn; a session takes it with its next prompt.</summary>
    [NeoRpcMethod("savePromptSuggestionSettings")]
    public GlobalConfigPromptSuggestionSettingsResponse SavePromptSuggestionSettings(GlobalConfigSavePromptSuggestionSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store is null) return new("unavailable", true);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", true);
        lock (_gate)
        {
            try
            {
                _store.SaveGlobalPromptSuggestions(request.Enabled);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or FormatException)
            {
                return new("write_failed", _store.LoadGlobalPromptSuggestions());
            }

            return new("ok", _store.LoadGlobalPromptSuggestions());
        }
    }

    /// <summary>Reads whether sessions get the tools of Code Mode (<c>[code_mode] mode</c>), and the modes.</summary>
    [NeoRpcMethod("codeModeSettings")]
    public GlobalConfigCodeModeSettingsResponse CodeModeSettings(GlobalConfigSessionTitlesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var modes = CodeAltaConfigStore.CodeModeModes;
        if (_store is null) return new("unavailable", null, modes);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null, modes);
        try
        {
            lock (_gate) return new("ok", _store.LoadCodeModeMode(), modes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new("config_invalid", null, modes);
        }
    }

    /// <summary>Saves whether sessions get the tools of Code Mode; a session takes it with its next prompt.</summary>
    [NeoRpcMethod("saveCodeModeSettings")]
    public GlobalConfigCodeModeSettingsResponse SaveCodeModeSettings(GlobalConfigSaveCodeModeSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var modes = CodeAltaConfigStore.CodeModeModes;
        if (_store is null) return new("unavailable", null, modes);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null, modes);
        if (request.Mode is not { } mode || !modes.Contains(mode, StringComparer.Ordinal)) return new("invalid", null, modes);
        lock (_gate)
        {
            try
            {
                _store.SaveCodeModeMode(mode);
                return new("ok", _store.LoadCodeModeMode(), modes);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
            {
                return new("write_failed", null, modes);
            }
        }
    }

    /// <summary>Reads the tools every session does without (<c>[tools] disabled</c>), and the names of the built-in tools.</summary>
    [NeoRpcMethod("disabledTools")]
    public GlobalConfigDisabledToolsResponse DisabledTools(GlobalConfigSessionTitlesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<string> known = CodeAlta.Agent.Runtime.Tools.AgentBuiltInToolFactory.BuiltInToolNames;
        if (_store is null) return new("unavailable", [], known);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", [], known);
        try
        {
            lock (_gate) return new("ok", [.. _store.LoadGlobalDisabledTools().Order(StringComparer.Ordinal)], known);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new("config_invalid", [], known);
        }
    }

    /// <summary>Saves the tools every session does without; a session runs without them from its next turn.</summary>
    [NeoRpcMethod("saveDisabledTools")]
    public GlobalConfigDisabledToolsResponse SaveDisabledTools(GlobalConfigSaveDisabledToolsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<string> known = CodeAlta.Agent.Runtime.Tools.AgentBuiltInToolFactory.BuiltInToolNames;
        if (_store is null) return new("unavailable", [], known);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", [], known);
        var names = (request.Tools ?? []).Select(static name => name?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length > SpaceTools.MaximumTools || names.Any(static name => !SpaceTools.IsToolName(name))) return new("invalid", [], known);
        lock (_gate)
        {
            try
            {
                _store.SaveGlobalDisabledTools(names);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                return new("write_failed", [], known);
            }
        }

        return new("ok", [.. names.Order(StringComparer.Ordinal)], known);
    }

    /// <summary>Lists the configured provider definitions, including disabled ones, without their secrets.</summary>
    [NeoRpcMethod("providers")]
    public GlobalConfigProvidersResponse Providers(GlobalConfigProvidersRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store is null) return new("unavailable", null, null, [], ProviderTypes, ReasoningEfforts, [], []);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null, null, [], ProviderTypes, ReasoningEfforts, [], []);
        try
        {
            lock (_gate)
            {
                var revision = Revision(_store.LoadGlobalConfigContent());
                var document = _store.LoadGlobal();
                var raw = RawDefinitions(document).ToDictionary(static value => value.ProviderKey, StringComparer.OrdinalIgnoreCase);
                var defaults = _defaults ??= ProviderDefaults.Load();
                var providers = _store.LoadGlobalProviderDefinitions(includeDisabled: true).Take(MaximumProviders)
                    .Select(effective =>
                    {
                        raw.TryGetValue(effective.ProviderKey, out var definition);
                        return new GlobalConfigProvider(
                            Bound(effective.ProviderKey)!, Bound(effective.ProviderType) ?? string.Empty, effective.Enabled != false,
                            Bound(definition?.DisplayName), Bound(effective.DisplayName) ?? effective.ProviderKey,
                            Bound(definition?.Model), Bound(definition?.ReasoningEffort),
                            Bound(definition?.ApiUrl), Bound(effective.ApiUrl), Bound(definition?.ApiKeyEnv),
                            !string.IsNullOrEmpty(definition?.ApiKey), defaults.For(effective))
                        { Icon = Bound(definition?.Icon), Color = Bound(definition?.Color), AnthropicApiKey = Bound(definition?.AnthropicApiKey), PermissionMode = Bound(definition?.PermissionMode),
                          ConfigDir = Bound(definition?.ConfigDir), Command = Bound(definition?.Command), ModelsOrder = ConfiguredModelsOrder(definition?.ModelsOrder, definition?.SortModels),
                          Environment = definition?.Env is { Count: > 0 } variables ? [.. variables.Take(MaximumEnvironmentVariables).Select(static pair => new GlobalConfigPluginMapEntry(Bound(pair.Key)!, Bound(pair.Value)!))] : null };
                    }).Concat(ListPluginProviders(document)).ToArray();
                var pluginTypes = DescribePluginTypes();
                // The providers CodeAlta knows how to configure that this configuration does not have yet.
                var builtIn = defaults.Template.Values.Where(entry => !providers.Any(provider => string.Equals(provider.Key, entry.ProviderKey, StringComparison.OrdinalIgnoreCase)))
                    .Select(entry => new GlobalConfigBuiltInProvider(Bound(entry.ProviderKey)!, Bound(entry.ProviderType) ?? string.Empty, defaults.For(entry).DisplayName ?? entry.ProviderKey))
                    .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                // The providers of a newer version: not listed, not saved over, and said to be there.
                var unsupported = document.UnsupportedProviders
                    .Where(provider => PluginProviderTypes?.Find(provider.ProviderType) is null)
                    .Take(MaximumProviders)
                    .Select(static provider => new GlobalConfigUnsupportedProvider(Bound(provider.ProviderKey)!, Bound(provider.ProviderType)!)).ToArray();
                var configured = Bound(document.Chat?.DefaultProvider?.Trim().ToLowerInvariant());
                // The provider a new session starts with: the configured one when it is enabled, else the first
                // enabled one in the order the providers are listed everywhere (by name).
                var enabled = providers.Where(static provider => provider.Enabled)
                    .OrderBy(static provider => provider.EffectiveName, StringComparer.OrdinalIgnoreCase).ThenBy(static provider => provider.Key, StringComparer.OrdinalIgnoreCase).ToArray();
                var starting = enabled.FirstOrDefault(provider => string.Equals(provider.Key, configured, StringComparison.OrdinalIgnoreCase)) ?? enabled.FirstOrDefault();
                return new("ok", revision, configured, providers, [.. ProviderTypes, .. pluginTypes.Select(static type => type.Type)], ReasoningEfforts, defaults.Types, builtIn)
                {
                    Unsupported = unsupported,
                    StartingProvider = starting?.Key,
                    PermissionModes = PermissionModes,
                    PluginTypes = pluginTypes.Count == 0 ? null : pluginTypes,
                };
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            return new("read_failed", null, null, [], ProviderTypes, ReasoningEfforts, [], []);
        }
    }

    /// <summary>
    /// Enables one configured provider and re-registers the providers, as a structured save with "apply" does.
    /// An already enabled provider is not rewritten. Used after an account sign-in, which has no editor revision.
    /// </summary>
    /// <param name="key">The provider key.</param>
    /// <returns>The save result: <c>ok</c>, <c>invalid</c> (unknown provider), <c>write_failed</c>, <c>apply_failed</c> or <c>unavailable</c>.</returns>
    internal GlobalConfigSaveResponse EnableProvider(string? key)
    {
        if (_store is null || _registry is null) return Failure("unavailable");
        return Mutate(_store, _registry, null, checkRevision: false, applyProviders: true, (store, definitions) =>
        {
            var definition = definitions.FirstOrDefault(value => string.Equals(value.ProviderKey, key?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (definition is null) return "The provider no longer exists in the configuration.";
            if (definition.Enabled != false) return null;
            definition.Enabled = true;
            store.SaveGlobalProviderDefinitions(definitions);
            return null;
        });
    }

    /// <summary>
    /// Adds or updates one provider definition, leaving every setting the form does not show untouched,
    /// optionally makes it the default provider, and optionally re-registers the providers. A provider given another
    /// key takes its targets in the tiers of models along, the global ones and those of each space.
    /// </summary>
    [NeoRpcMethod("saveProvider")]
    public async Task<GlobalConfigSaveResponse> SaveProviderAsync(GlobalConfigSaveProviderRequest request, CancellationToken cancellationToken)
    {
        var response = SaveProvider(request);
        var original = request.OriginalKey?.Trim();
        var key = request.Provider?.Key?.Trim().ToLowerInvariant();
        // The provider is saved: the spaces follow it even when the page stops waiting.
        if (response.Status is "ok" or "apply_failed" && !string.IsNullOrEmpty(original) && !string.IsNullOrEmpty(key)
            && !string.Equals(original, key, StringComparison.OrdinalIgnoreCase))
            await ReplaceProviderInSpaceTiersAsync(original, key).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Adds or updates one provider definition as <see cref="SaveProviderAsync"/> does, leaving the tiers of the
    /// spaces as they are.
    /// </summary>
    internal GlobalConfigSaveResponse SaveProvider(GlobalConfigSaveProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var edit = request.Provider;
        var key = edit?.Key?.Trim().ToLowerInvariant();
        if (edit is null || string.IsNullOrEmpty(key) || key.Length > 64 || !key.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            return new("invalid", null, "A provider key uses letters, digits, '-' or '_' (at most 64).", null, null, 0);
        if (new[] { edit.DisplayName, edit.Model, edit.ReasoningEffort, edit.ApiUrl, edit.ApiKeyEnv, edit.ApiKey, edit.Type, edit.ConfigDir }.Any(static value => value?.Length > 2048))
            return new("invalid", null, "A provider field is too long.", null, null, 0);
        // The file is written by hand too, and is read whatever these two say: only the form is held to their shape.
        var icon = Optional(edit.Icon)?.ToLowerInvariant();
        if (icon is not null && (icon.Length > 64 || !icon.All(static character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-')))
            return new("invalid", null, "An icon is named by lowercase letters, digits or '-' (at most 64).", null, null, 0);
        var color = Optional(edit.Color);
        if (color is not null && !IsHexColor(color))
            return new("invalid", null, "A color is written as #rgb or #rrggbb.", null, null, 0);
        var anthropicApiKey = Optional(edit.AnthropicApiKey);
        if (anthropicApiKey is not (null or "use" or "ignore"))
            return new("invalid", null, "ANTHROPIC_API_KEY is used, ignored, or left to the answer of Claude Code.", null, null, 0);
        var permissionMode = Optional(edit.PermissionMode);
        if (permissionMode is not null && !PermissionModes.Contains(permissionMode, StringComparer.Ordinal))
            return new("invalid", null, $"A permission mode is one of: {string.Join(", ", PermissionModes)}.", null, null, 0);
        if (!TryReadModelsOrder(edit.ModelsOrder, out var modelsOrder))
            return new("invalid", null, $"An order of the models is one of: {string.Join(", ", ModelOrdering.Values)}.", null, null, 0);
        if (!TryReadEnvironment(edit.Environment, out var environment, out var environmentProblem))
            return new("invalid", null, environmentProblem, null, null, 0);
        return Mutate(request.ExpectedEpoch, request.ExpectedRevision, request.ApplyProviders, (store, definitions) =>
        {
            var original = request.OriginalKey?.Trim();
            var definition = string.IsNullOrEmpty(original) ? null
                : definitions.FirstOrDefault(value => string.Equals(value.ProviderKey, original, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(original) && definition is null) return "The provider no longer exists in the configuration.";
            if (definitions.Any(value => value != definition && string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase)))
                return "Another provider already uses this key.";
            if (definition is null) definitions.Add(definition = new CodeAltaProviderDocument());
            definition.ProviderKey = key;
            definition.ProviderType = Optional(edit.Type) ?? definition.ProviderType;
            definition.Enabled = edit.Enabled;
            definition.DisplayName = Optional(edit.DisplayName);
            definition.Icon = icon;
            definition.Color = color;
            definition.Model = Optional(edit.Model);
            definition.ReasoningEffort = Optional(edit.ReasoningEffort);
            definition.ApiUrl = Optional(edit.ApiUrl);
            definition.ApiKeyEnv = Optional(edit.ApiKeyEnv);
            // Only the CLI of Claude Code reads that variable by itself.
            definition.AnthropicApiKey = definition.ProviderType == "claude-code" ? anthropicApiKey : null;
            // So is its configuration folder: the account it runs with.
            definition.ConfigDir = definition.ProviderType == "claude-code" ? Optional(edit.ConfigDir) : null;
            // And the variables it starts with: a token of the account, which a sandbox on macOS needs.
            definition.Env = definition.ProviderType == "claude-code" ? environment : null;
            // The settings of the TOML that only the anthropic type reads are kept for it, and left behind otherwise.
            definition.RefusalFallback = definition.ProviderType == "anthropic" ? definition.RefusalFallback : null;
            definition.PromptCacheTtl = definition.ProviderType == "anthropic" ? definition.PromptCacheTtl : null;
            // Only a provider that runs the CLI takes a permission mode; the store refuses the field for any
            // other type, so a type changed away from it leaves the mode behind.
            definition.PermissionMode = TakesPermissionMode(definition.ProviderType) ? permissionMode : null;
            // The older sort_models gives way to the order the form names.
            definition.ModelsOrder = modelsOrder;
            definition.SortModels = null;
            // A null key keeps the stored secret; the form never receives it back.
            if (edit.ClearApiKey) definition.ApiKey = null;
            else if (!string.IsNullOrEmpty(edit.ApiKey)) definition.ApiKey = edit.ApiKey;
            // The spaces of a provider are written beside it: a provider given another key keeps them.
            var renamedSpaces = !string.IsNullOrEmpty(original) && !string.Equals(original, key, StringComparison.OrdinalIgnoreCase)
                ? store.LoadGlobalSpaceAssignments().Of(SpaceResourceKind.Provider, original) : [];
            store.SaveGlobalProviderDefinitions(definitions);
            if (renamedSpaces.Count > 0) store.SaveGlobalSpaceAssignment(SpaceResourceKind.Provider, key, renamedSpaces);
            if (!string.IsNullOrEmpty(original) && !string.Equals(original, key, StringComparison.OrdinalIgnoreCase)) ReplaceProviderInTiers(store, original, key);
            if (request.MakeDefault && edit.Enabled) store.SaveGlobalDefaultProvider(key);
            // A provider that is no longer the default, or no longer enabled, is not left named as the default.
            else if (store.GetEffectiveDefaultProvider() is { } current
                && (string.Equals(current, key, StringComparison.OrdinalIgnoreCase) || string.Equals(current, original, StringComparison.OrdinalIgnoreCase)))
                store.SaveGlobalDefaultProvider(null);
            return null;
        });
    }

    /// <summary>
    /// Adds one of the providers CodeAlta ships a configuration for, as that configuration has it: its key, its
    /// adapter type, its endpoint, the variable of its key and what its service needs. It is added disabled: the user
    /// gives it a credential, or signs in, and enables it.
    /// </summary>
    [NeoRpcMethod("addBuiltInProvider")]
    public GlobalConfigSaveResponse AddBuiltInProvider(GlobalConfigAddBuiltInProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = request.Key?.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > 64) return new("invalid", null, "A provider key is required.", null, null, 0);
        return Mutate(request.ExpectedEpoch, request.ExpectedRevision, applyProviders: true, (store, definitions) =>
        {
            if (definitions.Any(value => string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase))) return "This provider is already in the configuration.";
            // Read again: the definition that is added is not one another caller could have changed.
            if (CodeAltaConfigStore.LoadDefaultProviderDefinitions().FirstOrDefault(value => string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase)) is not { } template)
                return "CodeAlta has no built-in provider with this key.";
            template.Enabled = false;
            definitions.Add(template);
            store.SaveGlobalProviderDefinitions(definitions);
            return null;
        });
    }

    /// <summary>
    /// Adds a copy of a provider under the first free key <c>&lt;key&gt;-2</c>, <c>&lt;key&gt;-3</c>…: the same type and
    /// settings, a numbered name, and no folder of its own (<c>config_dir</c>, <c>codex_home</c>), which the user sets
    /// for the other account. It is not the default provider. The providers are re-registered.
    /// </summary>
    [NeoRpcMethod("duplicateProvider")]
    public GlobalConfigSaveResponse DuplicateProvider(GlobalConfigDuplicateProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? created = null;
        var response = Mutate(request.ExpectedEpoch, request.ExpectedRevision, applyProviders: true, (store, definitions) =>
        {
            var source = definitions.FirstOrDefault(value => string.Equals(value.ProviderKey, request.Key?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (source is null) return "The provider no longer exists in the configuration.";
            var (key, number) = NextKey(source.ProviderKey, definitions);
            var copy = CodeAltaConfigStore.CloneProviderDefinition(source);
            copy.ProviderKey = key;
            var name = source.DisplayName ?? (source.ProviderType is { } type ? CodeAltaConfigStore.CreateProviderTypeDefaults(type).DisplayName : null) ?? source.ProviderKey;
            // The copy of "Claude Code 2" is "Claude Code 3".
            var space = name.LastIndexOf(' ');
            if (space > 0 && name.AsSpan(space + 1).IndexOfAnyExceptInRange('0', '9') < 0) name = name[..space];
            copy.DisplayName = $"{name} {number}";
            copy.ConfigDir = null;
            // A token of the variables belongs to the account of the source, as its folder does.
            copy.Env = null;
            copy.CodexHome = null;
            definitions.Add(copy);
            store.SaveGlobalProviderDefinitions(definitions);
            created = key;
            return null;
        });
        return response with { Key = response.Status is "ok" or "apply_failed" ? created : null };
    }

    // The first key of the form <key>-<n> from 2 that no provider has, within the 64 characters of a key.
    private static (string Key, int Number) NextKey(string key, List<CodeAltaProviderDocument> definitions)
    {
        var stem = key;
        var dash = key.LastIndexOf('-');
        var start = 2;
        if (dash > 0 && int.TryParse(key.AsSpan(dash + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number >= 1)
        {
            // The copy of claude-code-2 is claude-code-3, not claude-code-2-2.
            stem = key[..dash];
            start = number + 1;
        }

        for (var next = start; ; next++)
        {
            var suffix = "-" + next.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var candidate = (stem.Length + suffix.Length > 64 ? stem[..(64 - suffix.Length)] : stem) + suffix;
            if (!definitions.Any(value => string.Equals(value.ProviderKey, candidate, StringComparison.OrdinalIgnoreCase))) return (candidate, next);
        }
    }

    /// <summary>
    /// Removes one provider definition, and its targets from the tiers of models, the global ones and those of each
    /// space; optionally re-registers the remaining providers.
    /// </summary>
    [NeoRpcMethod("deleteProvider")]
    public async Task<GlobalConfigSaveResponse> DeleteProviderAsync(GlobalConfigDeleteProviderRequest request, CancellationToken cancellationToken)
    {
        var response = DeleteProvider(request);
        // The provider is removed: the spaces leave it even when the page stops waiting.
        if (response.Status is "ok" or "apply_failed") await ReplaceProviderInSpaceTiersAsync(request.Key!.Trim(), null).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Removes one provider definition, and its targets from the global tiers of models, and optionally re-registers
    /// the remaining providers. The tiers of the spaces are left to <see cref="DeleteProviderAsync"/>.
    /// </summary>
    internal GlobalConfigSaveResponse DeleteProvider(GlobalConfigDeleteProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Mutate(request.ExpectedEpoch, request.ExpectedRevision, request.ApplyProviders, (store, definitions) =>
        {
            var key = request.Key?.Trim();
            if (string.IsNullOrEmpty(key)) return "The provider no longer exists in the configuration.";
            if (definitions.RemoveAll(value => string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase)) > 0)
                store.SaveGlobalProviderDefinitions(definitions);
            // A provider of a plugin is a section of its own type.
            else if (!store.RemoveGlobalExternalProvider(key))
                return "The provider no longer exists in the configuration.";
            if (string.Equals(store.GetEffectiveDefaultProvider(), key, StringComparison.OrdinalIgnoreCase)) store.SaveGlobalDefaultProvider(null);
            ReplaceProviderInTiers(store, key, null);
            return null;
        });
    }

    // The tiers of models name providers by their key: a provider removed leaves them, a provider renamed takes its
    // targets to its new key. Tiers the file writes wrong are left as they are, for the user to correct in Settings.
    private static void ReplaceProviderInTiers(CodeAltaConfigStore store, string providerKey, string? renamedTo)
    {
        ModelSettings settings;
        try { settings = store.LoadGlobalModelSettings(); }
        catch (InvalidDataException) { return; }
        var tiers = settings.Tiers.ReplaceProvider(providerKey, renamedTo);
        if (!ReferenceEquals(tiers, settings.Tiers)) store.SaveGlobalModelSettings(settings with { Tiers = tiers });
    }

    // The same for the tiers of each space, in their files. A space left without tiers of its own takes the global
    // ones, and their fallback. A space file that cannot be read or written keeps what it has: a tier skips a target
    // whose provider is not configured.
    private async Task ReplaceProviderInSpaceTiersAsync(string providerKey, string? renamedTo)
    {
        if (Spaces is null) return;
        try
        {
            foreach (var space in await Spaces.LoadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (space.Settings.NewSessions?.Tiers is not { } own || ReplaceProvider(own) is null) continue;
                await Spaces.UpdateSettingsAsync(space.Id, settings =>
                {
                    if (settings.NewSessions is not { Tiers: { } tiers } sessions || ReplaceProvider(tiers) is not { } replaced) return settings;
                    var left = SpaceModelTiers.From(replaced);
                    var kept = sessions with { Tiers = left, Fallback = left is null ? null : sessions.Fallback };
                    return settings with { NewSessions = kept.IsEmpty ? null : kept };
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }

        // The tiers once the provider is gone, or null when none of their targets names it.
        ModelTierSettings? ReplaceProvider(SpaceModelTiers tiers)
        {
            var current = tiers.ToTierSettings();
            var replaced = current.ReplaceProvider(providerKey, renamedTo);
            return ReferenceEquals(replaced, current) ? null : replaced;
        }
    }

    // Runs one structured provider edit under the same epoch, revision and apply rules as a text save.
    private GlobalConfigSaveResponse Mutate(string? expectedEpoch, string? expectedRevision, bool applyProviders,
        Func<CodeAltaConfigStore, List<CodeAltaProviderDocument>, string?> edit)
    {
        if (_store is null || _registry is null) return Failure("unavailable");
        if (!string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal)) return Failure("stale_epoch");
        return Mutate(_store, _registry, expectedRevision, checkRevision: true, applyProviders, edit);
    }

    private GlobalConfigSaveResponse Mutate(CodeAltaConfigStore store, ModelProviderRegistry registry, string? expectedRevision,
        bool checkRevision, bool applyProviders, Func<CodeAltaConfigStore, List<CodeAltaProviderDocument>, string?> edit)
    {
        lock (_gate)
        {
            try
            {
                if (checkRevision && !string.Equals(Revision(store.LoadGlobalConfigContent()), expectedRevision, StringComparison.Ordinal)) return Failure("conflict");
                var refusal = edit(store, RawDefinitions(store.LoadGlobal()).ToList());
                if (refusal is not null) return new("invalid", null, refusal, null, null, 0);
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentException)
            {
                // The store validates the complete definition set before writing.
                return new("invalid", null, Bound(exception.Message), null, null, 0);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Failure("write_failed");
            }

            string revision;
            try { revision = Revision(store.LoadGlobalConfigContent()); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException) { return Failure("write_failed"); }
            if (!applyProviders) return new("ok", revision, null, null, null, 0);
            try
            {
                return new("ok", revision, null, null, null, ApplyProviders(store, registry, _stateRoot!, PluginProviderTypes, ModelCatalog));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
            {
                return new("apply_failed", revision, null, null, null, 0);
            }
        }
    }

    // The definitions exactly as written in the file: no defaults are filled in, so saving them back adds none.
    private static IEnumerable<CodeAltaProviderDocument> RawDefinitions(CodeAltaConfigDocument document)
    {
        foreach (var (key, definition) in document.Providers ?? [])
        {
            definition.ProviderKey = key;
            yield return definition;
        }
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsHexColor(string value)
        => value.Length is 4 or 7 && value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    private const int MaximumProviders = 64;
    private static readonly ImmutableArray<string> ProviderTypes =
        ["openai-chat", "openai-responses", "azure-openai", "anthropic", "google-genai", "vertex-ai", "mistral", "codex", "copilot", "xai", "claude-code"];
    private static readonly ImmutableArray<string> ReasoningEfforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// The permission modes a provider that runs the Claude Code CLI can start its sessions in, as the provider reports
    /// them. The configuration store holds <c>permission_mode</c> to the same list.
    /// </summary>
    private static IReadOnlyList<string> PermissionModes => ConfiguredModelProviderRegistryBuilder.GetPermissionModes("claude-code");

    /// <summary>Provider types that take a permission mode: the ones that run the Claude Code CLI.</summary>
    // The order the file names, as the form shows it; a value of a newer version is shown as it is.
    private static string? ConfiguredModelsOrder(string? modelsOrder, bool? sortModels)
        => ModelOrdering.TryParse(modelsOrder, out var order) ? ModelOrdering.Format(order)
            : !string.IsNullOrWhiteSpace(modelsOrder) ? Bound(modelsOrder.Trim())
            : sortModels is { } sort ? ModelOrdering.Format(sort ? ModelOrder.Name : ModelOrder.Provider)
            : null;

    // An order of the form: blank for the order of the type, else one this version knows.
    private static bool TryReadModelsOrder(string? value, out string? modelsOrder)
    {
        modelsOrder = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!ModelOrdering.TryParse(value, out var order)) return false;
        modelsOrder = ModelOrdering.Format(order);
        return true;
    }

    private static bool TakesPermissionMode(string? type) => ConfiguredModelProviderRegistryBuilder.GetPermissionModes(type).Count > 0;

    private const int MaximumEnvironmentVariables = 64;

    // The variables of the form: names of variables, each once, with values of a bounded length; null for none.
    private static bool TryReadEnvironment(IReadOnlyList<GlobalConfigPluginMapEntry>? entries, out Dictionary<string, string>? environment, out string problem)
    {
        environment = null;
        problem = string.Empty;
        if (entries is null) return true;
        var read = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var name = entry.Name?.Trim() ?? string.Empty;
            if (name.Length == 0) continue;
            if (name.Length > 256 || char.IsAsciiDigit(name[0]) || !name.All(static character => char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                problem = $"'{(name.Length > 64 ? name[..64] + "…" : name)}' is not the name of a variable: letters, digits and '_', not starting with a digit.";
                return false;
            }

            if (!read.TryAdd(name, entry.Value ?? string.Empty))
            {
                problem = $"The variable {name} is given twice.";
                return false;
            }

            if (read[name].Length > 8192 || read.Count > MaximumEnvironmentVariables)
            {
                problem = "The environment of a provider has at most 64 variables, of at most 8192 characters each.";
                return false;
            }
        }

        environment = read.Count == 0 ? null : read;
        return true;
    }

    /// <summary>
    /// Re-registers the enabled provider definitions and unregisters providers that are no longer
    /// configured or enabled, like the TUI's provider refresh.
    /// </summary>
    /// <returns>The number of providers registered.</returns>
    internal static int ApplyProviders(CodeAltaConfigStore store, ModelProviderRegistry registry, string stateRoot)
        => ApplyProviders(store, registry, stateRoot, pluginTypes: null, modelCatalog: null);

    /// <summary>
    /// Re-registers the enabled provider definitions and the enabled providers whose type a running plugin
    /// contributes, and unregisters the providers that are no longer configured, enabled or contributed.
    /// </summary>
    /// <returns>The number of providers registered.</returns>
    internal static int ApplyProviders(CodeAltaConfigStore store, ModelProviderRegistry registry, string stateRoot, PluginModelProviderTypes? pluginTypes,
        ModelsDevCatalogService? modelCatalog)
    {
        var definitions = store.LoadGlobalProviderDefinitions(includeDisabled: true);
        var previous = registry.ListProviders(includeDisabled: true);
        var applied = ConfiguredModelProviderRegistryBuilder.RegisterOrReplaceConfiguredProviders(
            registry, definitions.Where(static definition => definition.Enabled != false), stateRoot, modelCatalog).ToList();
        if (pluginTypes is not null)
        {
            applied.AddRange(pluginTypes.RegisterOrReplace(registry, store, stateRoot, includeDisabled: false));
        }

        var expected = applied.Select(static descriptor => descriptor.ProviderId.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in previous)
        {
            if (!expected.Contains(descriptor.ProviderId.Value)) registry.Unregister(descriptor.ProviderId);
        }

        return applied.Count;
    }

    /// <summary>Returns a stable revision token for configuration text.</summary>
    internal static string Revision(string content)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)).AsSpan(0, 16));

    private static GlobalConfigSaveResponse Failure(string status) => new(status, null, null, null, null, 0);

    private static string? Bound(string? value)
        => value is null || value.Length <= MaximumMessageLength ? value : value[..MaximumMessageLength];

    // What blank provider fields fall back to. Built once from the bundled template and the type completion, then only read.
    private sealed class ProviderDefaults
    {
        private readonly Dictionary<string, CodeAltaProviderDocument> _template;
        private readonly Dictionary<string, GlobalConfigProviderDefaults> _types;

        private ProviderDefaults(Dictionary<string, CodeAltaProviderDocument> template, Dictionary<string, GlobalConfigProviderDefaults> types)
        {
            _template = template;
            _types = types;
            Types = [.. ProviderTypes.Select(type => new GlobalConfigProviderTypeDefaults(type, types[type]))];
        }

        /// <summary>The defaults of a new provider of each offered type, in the order of the offered types.</summary>
        public IReadOnlyList<GlobalConfigProviderTypeDefaults> Types { get; }

        /// <summary>The providers of the configuration CodeAlta ships, by their key.</summary>
        public IReadOnlyDictionary<string, CodeAltaProviderDocument> Template => _template;

        /// <exception cref="InvalidOperationException">The bundled template or an offered type cannot be completed.</exception>
        public static ProviderDefaults Load()
        {
            var template = new Dictionary<string, CodeAltaProviderDocument>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in CodeAltaConfigStore.LoadDefaultProviderDefinitions().Take(MaximumProviders))
                template[definition.ProviderKey] = definition;
            return new(template, ProviderTypes.ToDictionary(static type => type,
                static type => Project(CodeAltaConfigStore.CreateProviderTypeDefaults(type), null), StringComparer.Ordinal));
        }

        /// <summary>
        /// The defaults of one configured provider: the built-in template entry with its key (when it has the same
        /// adapter type), then the type's own defaults; a blank display name finally shows the provider key.
        /// </summary>
        /// <remarks>
        /// The model and the reasoning effort of the template are what a new configuration starts with, not what a
        /// blank field falls back to: a provider without a model starts its sessions with the first model it lists.
        /// </remarks>
        public GlobalConfigProviderDefaults For(CodeAltaProviderDocument effective)
        {
            var type = _types.GetValueOrDefault(effective.ProviderType ?? string.Empty);
            var template = _template.TryGetValue(effective.ProviderKey, out var entry) &&
                string.Equals(entry.ProviderType, effective.ProviderType, StringComparison.OrdinalIgnoreCase) ? entry : null;
            var defaults = template is null ? type ?? new(null, null, null, null, null) : Project(template, type);
            return defaults with
            {
                DisplayName = defaults.DisplayName ?? Bound(effective.ProviderKey),
                Model = type?.Model,
                ReasoningEffort = type?.ReasoningEffort,
            };
        }

        private static GlobalConfigProviderDefaults Project(CodeAltaProviderDocument definition, GlobalConfigProviderDefaults? fallback)
            => new(Bound(definition.DisplayName) ?? fallback?.DisplayName, Bound(definition.Model) ?? fallback?.Model,
                Bound(definition.ReasoningEffort) ?? fallback?.ReasoningEffort, Bound(definition.ApiUrl) ?? fallback?.ApiUrl,
                Bound(definition.ApiKeyEnv) ?? fallback?.ApiKeyEnv);
    }
}

internal sealed record GlobalConfigReadRequest(string? ExpectedEpoch);
internal sealed record GlobalConfigReadResponse(string Status, string? Content, string? Revision);
internal sealed record GlobalConfigValidateRequest(string? Content);
internal sealed record GlobalConfigValidationResponse(bool Valid, string? Message, int? Line, int? Column)
{
    /// <summary>What a valid configuration leaves out: the providers whose type this version does not know.</summary>
    public string? Warning { get; init; }
}
internal sealed record GlobalConfigSaveRequest(string? ExpectedEpoch, string? Content, string? ExpectedRevision, bool ApplyProviders);
internal sealed record GlobalConfigSaveResponse(string Status, string? Revision, string? Message, int? Line, int? Column, int ProvidersApplied)
{
    /// <summary>The key of the provider a duplication added; null for any other save.</summary>
    public string? Key { get; init; }
}

/// <summary>Asks to copy a provider; the revision is the one of the listing it was chosen from.</summary>
internal sealed record GlobalConfigDuplicateProviderRequest(string? ExpectedEpoch, string? ExpectedRevision, string? Key);
internal sealed record GlobalConfigProvidersRequest(string? ExpectedEpoch);
internal sealed record GlobalConfigSessionTitlesRequest(string? ExpectedEpoch);
/// <param name="ExpectedEpoch">The host epoch the page was loaded with.</param>
/// <param name="Mode"><c>first_prompt</c>, <c>last_message</c> or <c>model</c>.</param>
internal sealed record GlobalConfigSaveSessionTitlesRequest(string? ExpectedEpoch, string? Mode);
/// <param name="Status"><c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid</c> or <c>write_failed</c>.</param>
/// <param name="Mode">The mode in the settings; null unless <c>ok</c>.</param>
internal sealed record GlobalConfigSessionTitlesResponse(string Status, string? Mode);

internal sealed record GlobalConfigSaveReviewSettingsRequest(string? ExpectedEpoch, bool BeforeCommit);

/// <param name="BeforeCommit">Whether the user reviews the changes of every session before an agent commits them.</param>
internal sealed record GlobalConfigReviewSettingsResponse(string Status, bool BeforeCommit);

/// <param name="ExpectedEpoch">The host epoch the page was loaded with.</param>
/// <param name="CoAuthor">Whether agents end the message of their commits with the <c>Co-authored-by</c> line of CodeAlta.</param>
internal sealed record GlobalConfigSaveCommitSettingsRequest(string? ExpectedEpoch, bool CoAuthor);

/// <param name="Status"><c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c> or <c>write_failed</c>.</param>
/// <param name="CoAuthor">Whether agents end the message of their commits with the <c>Co-authored-by</c> line of CodeAlta.</param>
internal sealed record GlobalConfigCommitSettingsResponse(string Status, bool CoAuthor);

/// <param name="ExpectedEpoch">The host epoch the page was loaded with.</param>
/// <param name="Enabled">Whether the model suggests the next prompt at the end of a turn.</param>
internal sealed record GlobalConfigSavePromptSuggestionSettingsRequest(string? ExpectedEpoch, bool Enabled);

/// <param name="Status"><c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c> or <c>write_failed</c>.</param>
/// <param name="Enabled">Whether the model suggests the next prompt at the end of a turn.</param>
internal sealed record GlobalConfigPromptSuggestionSettingsResponse(string Status, bool Enabled);

/// <param name="ExpectedEpoch">The host epoch the page was loaded with.</param>
/// <param name="Mode">One of the modes of Code Mode: <c>off</c> or <c>on</c>.</param>
internal sealed record GlobalConfigSaveCodeModeSettingsRequest(string? ExpectedEpoch, string? Mode);

/// <param name="Status"><c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c>, <c>config_invalid</c>, <c>invalid</c> or <c>write_failed</c>.</param>
/// <param name="Mode">The mode in the settings; null unless <c>ok</c>.</param>
/// <param name="Modes">The modes this version knows.</param>
internal sealed record GlobalConfigCodeModeSettingsResponse(string Status, string? Mode, IReadOnlyList<string> Modes);

/// <summary>The tools every session does without.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="Tools">The names of the tools.</param>
internal sealed record GlobalConfigSaveDisabledToolsRequest(string? ExpectedEpoch, IReadOnlyList<string>? Tools);

/// <summary>
/// The tools every session does without. Status is <c>ok</c>, <c>invalid</c>, <c>config_invalid</c>, <c>write_failed</c>,
/// <c>unavailable</c> or <c>stale_epoch</c>.
/// </summary>
/// <param name="Status">How the call ended.</param>
/// <param name="Tools">The names of the tools turned off.</param>
/// <param name="Known">The names of the built-in tools, which the page offers.</param>
internal sealed record GlobalConfigDisabledToolsResponse(string Status, IReadOnlyList<string> Tools, IReadOnlyList<string> Known);
internal sealed record GlobalConfigProvidersResponse(string Status, string? Revision, string? DefaultProvider,
    IReadOnlyList<GlobalConfigProvider> Providers, IReadOnlyList<string> ProviderTypes, IReadOnlyList<string> ReasoningEfforts,
    IReadOnlyList<GlobalConfigProviderTypeDefaults> TypeDefaults, IReadOnlyList<GlobalConfigBuiltInProvider> BuiltIn)
{
    /// <summary>
    /// The providers of the file whose type this version does not know (a newer version wrote them): they are
    /// not among <see cref="Providers"/>, and saving keeps their sections as they are.
    /// </summary>
    public IReadOnlyList<GlobalConfigUnsupportedProvider> Unsupported { get; init; } = [];

    /// <summary>
    /// The permission modes a provider that runs its own CLI can be given, for the form's selector. Empty for a
    /// response that carries no listing.
    /// </summary>
    public IReadOnlyList<string> PermissionModes { get; init; } = [];

    /// <summary>
    /// The provider a new session starts with: <see cref="DefaultProvider"/> when it is enabled, otherwise the
    /// first enabled provider. Null when no provider is enabled.
    /// </summary>
    public string? StartingProvider { get; init; }

    /// <summary>The provider types the running plugins contribute: their providers are among <see cref="Providers"/>; absent for none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GlobalConfigPluginProviderType>? PluginTypes { get; init; }
}

/// <param name="Key">The provider key.</param>
/// <param name="Type">The type as the file writes it.</param>
internal sealed record GlobalConfigUnsupportedProvider(string Key, string Type);

/// <summary>A provider CodeAlta ships a configuration for and that the user does not have yet.</summary>
/// <param name="Key">Its key, which the added provider takes.</param>
/// <param name="Type">Its adapter type.</param>
/// <param name="Name">Its name.</param>
internal sealed record GlobalConfigBuiltInProvider(string Key, string Type, string Name);

/// <summary>Asks to add a built-in provider by its key; the revision is the one of the listing it was chosen from.</summary>
internal sealed record GlobalConfigAddBuiltInProviderRequest(string? ExpectedEpoch, string? ExpectedRevision, string? Key);

/// <summary>One configured provider: values as written (null when the file leaves them to defaults) plus effective display values.</summary>
internal sealed record GlobalConfigProvider(string Key, string Type, bool Enabled, string? DisplayName, string EffectiveName,
    string? Model, string? ReasoningEffort, string? ApiUrl, string? EffectiveApiUrl, string? ApiKeyEnv, bool HasApiKey,
    GlobalConfigProviderDefaults Defaults)
{
    /// <summary>The id of the brand icon the file gives the provider; null when the icon follows its key and type.</summary>
    public string? Icon { get; init; }

    /// <summary>The color the file gives that icon, as <c>#rgb</c> or <c>#rrggbb</c>; null for the colors of the icon.</summary>
    public string? Color { get; init; }

    /// <summary>
    /// Whether a <c>claude-code</c> provider gives the CLI <c>ANTHROPIC_API_KEY</c>: <c>use</c> or <c>ignore</c>;
    /// null to follow the answer Claude Code saved for the key.
    /// </summary>
    public string? AnthropicApiKey { get; init; }

    /// <summary>
    /// The permission mode the file gives a provider that runs the Claude Code CLI; null when it leaves the mode
    /// to the settings of the CLI, and always null for the other provider types.
    /// </summary>
    public string? PermissionMode { get; init; }

    /// <summary>
    /// The configuration folder a <c>claude-code</c> provider gives its CLI (<c>config_dir</c>), as the file writes it;
    /// null for the folder of Claude Code itself.
    /// </summary>
    public string? ConfigDir { get; init; }

    /// <summary>The executable a provider that runs a CLI names (<c>command</c>), as the file writes it; null when CodeAlta finds it.</summary>
    public string? Command { get; init; }

    /// <summary>
    /// The order of its models the file names: <c>models_order</c>, else the older <c>sort_models</c> read as <c>name</c> or
    /// <c>provider</c>; null for the order of its type.
    /// </summary>
    public string? ModelsOrder { get; init; }

    /// <summary>
    /// The variables a <c>claude-code</c> provider starts its CLI with (<c>env</c>), as the file writes them; null for none.
    /// </summary>
    public IReadOnlyList<GlobalConfigPluginMapEntry>? Environment { get; init; }

    /// <summary>The settings of a provider whose type a plugin contributes; absent for a built-in type.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GlobalConfigPluginProviderValues? Plugin { get; init; }
}

/// <summary>What each blank field of a provider falls back to; null when nothing is known for the field.</summary>
internal sealed record GlobalConfigProviderDefaults(string? DisplayName, string? Model, string? ReasoningEffort, string? ApiUrl, string? ApiKeyEnv);

/// <summary>The defaults of a provider of one adapter type that has no definition yet.</summary>
internal sealed record GlobalConfigProviderTypeDefaults(string Type, GlobalConfigProviderDefaults Defaults);
internal sealed record GlobalConfigProviderEdit(string? Key, string? Type, bool Enabled, string? DisplayName, string? Model,
    string? ReasoningEffort, string? ApiUrl, string? ApiKeyEnv, string? ApiKey, bool ClearApiKey)
{
    /// <summary>The id of the brand icon of the provider; blank for the icon that goes with its key and type.</summary>
    public string? Icon { get; init; }

    /// <summary>The color of that icon, as <c>#rgb</c> or <c>#rrggbb</c>; blank for the colors of the icon.</summary>
    public string? Color { get; init; }

    /// <summary><c>use</c> or <c>ignore</c> for <c>ANTHROPIC_API_KEY</c>; blank to follow Claude Code. Kept for <c>claude-code</c> only.</summary>
    public string? AnthropicApiKey { get; init; }
    /// <summary>
    /// The permission mode of a provider that runs the Claude Code CLI; blank to leave it to the settings of the
    /// CLI. A provider of another type sends null: the configuration file takes the field for no other type.
    /// </summary>
    public string? PermissionMode { get; init; }

    /// <summary>
    /// The configuration folder of the CLI (<c>config_dir</c>), a full path or one that starts with <c>~</c>; blank for
    /// the folder of Claude Code itself. Kept for <c>claude-code</c> only.
    /// </summary>
    public string? ConfigDir { get; init; }

    /// <summary>The order of the models (<c>provider</c>, <c>name</c>, <c>name_version</c>); blank for the order of its type.</summary>
    public string? ModelsOrder { get; init; }

    /// <summary>
    /// The variables a <c>claude-code</c> provider starts its CLI with (<c>env</c>); empty or null for none. Kept for
    /// <c>claude-code</c> only.
    /// </summary>
    public IReadOnlyList<GlobalConfigPluginMapEntry>? Environment { get; init; }
}
internal sealed record GlobalConfigSaveProviderRequest(string? ExpectedEpoch, string? ExpectedRevision, string? OriginalKey,
    GlobalConfigProviderEdit? Provider, bool MakeDefault, bool ApplyProviders);
internal sealed record GlobalConfigDeleteProviderRequest(string? ExpectedEpoch, string? ExpectedRevision, string? Key, bool ApplyProviders);
