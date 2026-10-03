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
        return new(result.IsValid, Bound(result.Message), result.Line, result.Column);
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
        if (_store is null) return new("unavailable", null, null, [], ProviderTypes, ReasoningEfforts);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null, null, [], ProviderTypes, ReasoningEfforts);
        try
        {
            lock (_gate)
            {
                var revision = Revision(_store.LoadGlobalConfigContent());
                var document = _store.LoadGlobal();
                var raw = RawDefinitions(document).ToDictionary(static value => value.ProviderKey, StringComparer.OrdinalIgnoreCase);
                var providers = _store.LoadGlobalProviderDefinitions(includeDisabled: true).Take(MaximumProviders)
                    .Select(effective =>
                    {
                        raw.TryGetValue(effective.ProviderKey, out var definition);
                        return new GlobalConfigProvider(
                            Bound(effective.ProviderKey)!, Bound(effective.ProviderType) ?? string.Empty, effective.Enabled != false,
                            Bound(definition?.DisplayName), Bound(effective.DisplayName) ?? effective.ProviderKey,
                            Bound(definition?.Model), Bound(definition?.ReasoningEffort),
                            Bound(definition?.ApiUrl), Bound(effective.ApiUrl), Bound(definition?.ApiKeyEnv),
                            !string.IsNullOrEmpty(definition?.ApiKey));
                    }).ToArray();
                return new("ok", revision, Bound(document.Chat?.DefaultProvider?.Trim().ToLowerInvariant()), providers, ProviderTypes, ReasoningEfforts);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            return new("read_failed", null, null, [], ProviderTypes, ReasoningEfforts);
        }
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
            definition.Model = Optional(edit.Model);
            definition.ReasoningEffort = Optional(edit.ReasoningEffort);
            definition.ApiUrl = Optional(edit.ApiUrl);
            definition.ApiKeyEnv = Optional(edit.ApiKeyEnv);
            // A null key keeps the stored secret; the form never receives it back.
            if (edit.ClearApiKey) definition.ApiKey = null;
            else if (!string.IsNullOrEmpty(edit.ApiKey)) definition.ApiKey = edit.ApiKey;
            store.SaveGlobalProviderDefinitions(definitions);
            if (request.MakeDefault && edit.Enabled) store.SaveGlobalDefaultProvider(key);
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
        lock (_gate)
        {
            try
            {
                if (!string.Equals(Revision(_store.LoadGlobalConfigContent()), expectedRevision, StringComparison.Ordinal)) return Failure("conflict");
                var refusal = edit(_store, RawDefinitions(_store.LoadGlobal()).ToList());
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
            try { revision = Revision(_store.LoadGlobalConfigContent()); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException) { return Failure("write_failed"); }
            if (!applyProviders) return new("ok", revision, null, null, null, 0);
            try
            {
                return new("ok", revision, null, null, null, ApplyProviders(_store, _registry, _stateRoot!));
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

    private const int MaximumProviders = 64;
    private static readonly ImmutableArray<string> ProviderTypes =
        ["openai-chat", "openai-responses", "azure-openai", "anthropic", "google-genai", "vertex-ai", "mistral", "codex", "copilot", "xai"];
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
}

internal sealed record GlobalConfigReadRequest(string? ExpectedEpoch);
internal sealed record GlobalConfigReadResponse(string Status, string? Content, string? Revision);
internal sealed record GlobalConfigValidateRequest(string? Content);
internal sealed record GlobalConfigValidationResponse(bool Valid, string? Message, int? Line, int? Column);
internal sealed record GlobalConfigSaveRequest(string? ExpectedEpoch, string? Content, string? ExpectedRevision, bool ApplyProviders);
internal sealed record GlobalConfigSaveResponse(string Status, string? Revision, string? Message, int? Line, int? Column, int ProvidersApplied);
internal sealed record GlobalConfigProvidersRequest(string? ExpectedEpoch);
internal sealed record GlobalConfigProvidersResponse(string Status, string? Revision, string? DefaultProvider,
    IReadOnlyList<GlobalConfigProvider> Providers, IReadOnlyList<string> ProviderTypes, IReadOnlyList<string> ReasoningEfforts);

/// <summary>One configured provider: values as written (null when the file leaves them to defaults) plus effective display values.</summary>
internal sealed record GlobalConfigProvider(string Key, string Type, bool Enabled, string? DisplayName, string EffectiveName,
    string? Model, string? ReasoningEffort, string? ApiUrl, string? EffectiveApiUrl, string? ApiKeyEnv, bool HasApiKey);
internal sealed record GlobalConfigProviderEdit(string? Key, string? Type, bool Enabled, string? DisplayName, string? Model,
    string? ReasoningEffort, string? ApiUrl, string? ApiKeyEnv, string? ApiKey, bool ClearApiKey);
internal sealed record GlobalConfigSaveProviderRequest(string? ExpectedEpoch, string? ExpectedRevision, string? OriginalKey,
    GlobalConfigProviderEdit? Provider, bool MakeDefault, bool ApplyProviders);
internal sealed record GlobalConfigDeleteProviderRequest(string? ExpectedEpoch, string? ExpectedRevision, string? Key, bool ApplyProviders);
