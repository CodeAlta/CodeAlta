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
