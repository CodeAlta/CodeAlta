using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Hosting;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Reads, validates and saves the global <c>config.toml</c> for the desktop Settings editor, and can
/// re-register the configured model providers after a save (the TUI's "Save and Apply").
/// </summary>
[NeoRpcService("globalConfig", Version = 1)]
internal sealed class GlobalConfigService
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
                // Another editor (the TUI, a text editor) may have changed the file since it was read.
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
                return new("ok", revision, null, null, null, ApplyProviders(_store, _registry, _stateRoot!));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
            {
                // The file is saved; only the in-process provider registration failed.
                return new("apply_failed", revision, null, null, null, 0);
            }
        }
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
                        { Icon = Bound(definition?.Icon), Color = Bound(definition?.Color), AnthropicApiKey = Bound(definition?.AnthropicApiKey) };
                    }).ToArray();
                // The providers CodeAlta knows how to configure that this configuration does not have yet.
                var builtIn = defaults.Template.Values.Where(entry => !providers.Any(provider => string.Equals(provider.Key, entry.ProviderKey, StringComparison.OrdinalIgnoreCase)))
                    .Select(entry => new GlobalConfigBuiltInProvider(Bound(entry.ProviderKey)!, Bound(entry.ProviderType) ?? string.Empty, defaults.For(entry).DisplayName ?? entry.ProviderKey))
                    .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                // The providers of a newer version: not listed, not saved over, and said to be there.
                var unsupported = document.UnsupportedProviders.Take(MaximumProviders)
                    .Select(static provider => new GlobalConfigUnsupportedProvider(Bound(provider.ProviderKey)!, Bound(provider.ProviderType)!)).ToArray();
                var configured = Bound(document.Chat?.DefaultProvider?.Trim().ToLowerInvariant());
                // The provider a new session starts with: the configured one when it is enabled, else the first
                // enabled one in the order the providers are listed everywhere (by name).
                var enabled = providers.Where(static provider => provider.Enabled)
                    .OrderBy(static provider => provider.EffectiveName, StringComparer.OrdinalIgnoreCase).ThenBy(static provider => provider.Key, StringComparer.OrdinalIgnoreCase).ToArray();
                var starting = enabled.FirstOrDefault(provider => string.Equals(provider.Key, configured, StringComparison.OrdinalIgnoreCase)) ?? enabled.FirstOrDefault();
                return new("ok", revision, configured, providers, ProviderTypes, ReasoningEfforts, defaults.Types, builtIn)
                {
                    Unsupported = unsupported,
                    StartingProvider = starting?.Key,
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
    /// optionally makes it the default provider, and optionally re-registers the providers.
    /// </summary>
    [NeoRpcMethod("saveProvider")]
    public GlobalConfigSaveResponse SaveProvider(GlobalConfigSaveProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var edit = request.Provider;
        var key = edit?.Key?.Trim().ToLowerInvariant();
        if (edit is null || string.IsNullOrEmpty(key) || key.Length > 64 || !key.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            return new("invalid", null, "A provider key uses letters, digits, '-' or '_' (at most 64).", null, null, 0);
        if (new[] { edit.DisplayName, edit.Model, edit.ReasoningEffort, edit.ApiUrl, edit.ApiKeyEnv, edit.ApiKey, edit.Type }.Any(static value => value?.Length > 2048))
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
            // A null key keeps the stored secret; the form never receives it back.
            if (edit.ClearApiKey) definition.ApiKey = null;
            else if (!string.IsNullOrEmpty(edit.ApiKey)) definition.ApiKey = edit.ApiKey;
            store.SaveGlobalProviderDefinitions(definitions);
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

    /// <summary>Removes one provider definition and optionally re-registers the remaining providers.</summary>
    [NeoRpcMethod("deleteProvider")]
    public GlobalConfigSaveResponse DeleteProvider(GlobalConfigDeleteProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Mutate(request.ExpectedEpoch, request.ExpectedRevision, request.ApplyProviders, (store, definitions) =>
        {
            if (definitions.RemoveAll(value => string.Equals(value.ProviderKey, request.Key?.Trim(), StringComparison.OrdinalIgnoreCase)) == 0)
                return "The provider no longer exists in the configuration.";
            store.SaveGlobalProviderDefinitions(definitions);
            return null;
        });
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
                return new("ok", revision, null, null, null, ApplyProviders(store, registry, _stateRoot!));
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
    /// Re-registers the enabled provider definitions and unregisters providers that are no longer
    /// configured or enabled, like the TUI's provider refresh.
    /// </summary>
    /// <returns>The number of providers registered.</returns>
    internal static int ApplyProviders(CodeAltaConfigStore store, ModelProviderRegistry registry, string stateRoot)
    {
        var definitions = store.LoadGlobalProviderDefinitions(includeDisabled: true);
        var previous = registry.ListProviders(includeDisabled: true);
        var applied = ConfiguredModelProviderRegistryBuilder.RegisterOrReplaceConfiguredProviders(
            registry, definitions.Where(static definition => definition.Enabled != false), stateRoot);
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
internal sealed record GlobalConfigSaveResponse(string Status, string? Revision, string? Message, int? Line, int? Column, int ProvidersApplied);
internal sealed record GlobalConfigProvidersRequest(string? ExpectedEpoch);
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
    /// The provider a new session starts with: <see cref="DefaultProvider"/> when it is enabled, otherwise the
    /// first enabled provider. Null when no provider is enabled.
    /// </summary>
    public string? StartingProvider { get; init; }
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
}
internal sealed record GlobalConfigSaveProviderRequest(string? ExpectedEpoch, string? ExpectedRevision, string? OriginalKey,
    GlobalConfigProviderEdit? Provider, bool MakeDefault, bool ApplyProviders);
internal sealed record GlobalConfigDeleteProviderRequest(string? ExpectedEpoch, string? ExpectedRevision, string? Key, bool ApplyProviders);
