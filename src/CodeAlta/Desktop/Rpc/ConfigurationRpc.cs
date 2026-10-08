using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Plugins;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

[NeoRpcService("configuration", Version = 1)]
internal sealed class ConfigurationService(
    ModelProviderRegistry? providerRegistry = null,
    PluginRuntimeManager? pluginRuntime = null,
    DesktopDefaultProvider? defaultProvider = null,
    CatalogOptions? catalog = null)
{
    private readonly string? _catalogRoot;

    internal ConfigurationService(string? catalogRoot) : this()
    {
        if (catalogRoot is null) return; // Browser-only mode has no admitted catalog.
        if (string.IsNullOrWhiteSpace(catalogRoot) || !Path.IsPathFullyQualified(catalogRoot))
            throw new ArgumentException("An absolute catalog root is required.", nameof(catalogRoot));
        _catalogRoot = Path.GetFullPath(catalogRoot);
    }

    [NeoRpcMethod("snapshot")]
    public ConfigurationSnapshot Snapshot(ConfigurationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var providerDescriptors = providerRegistry?.ListProviders(includeDisabled: true) ?? [];
        var pluginInstances = pluginRuntime?.ActivePlugins ?? [];
        // One provider is the default: the one a new session starts with.
        var startsWith = defaultProvider?.Of([.. providerDescriptors.Where(static value => value.IsEnabled)])?.ProviderId.Value;
        var providers = providerDescriptors
            .Take(32)
            // Identity must never be truncated into another selectable provider key.
            .Where(static value => value.ProviderId.Value.Length <= 256)
            .Select(value => new ConfigurationProvider(
                value.ProviderId.Value,
                Bound(value.DisplayName),
                Bound(value.ProviderType),
                value.IsEnabled,
                defaultProvider is null ? value.IsDefault : string.Equals(value.ProviderId.Value, startsWith, StringComparison.Ordinal),
                BoundOptional(value.DefaultModelId),
                BoundOptional(value.DefaultReasoningEffort?.ToString())))
            .ToArray();
        var plugins = pluginInstances
            .Take(32)
            .Select(static value => new ConfigurationPlugin(
                Bound(value.Descriptor.RuntimeKey),
                Bound(value.Descriptor.DisplayName ?? value.Descriptor.TypeName),
                BoundOptional(value.Descriptor.Version),
                Bound(value.State.ToString()),
                value.Contributions.Count))
            .ToArray();
        if (providerRegistry is null && _catalogRoot is not null)
        {
            try
            {
                var document = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = _catalogRoot }).LoadGlobal();
                var configuredProviders = document.Providers ?? [];
                providers = configuredProviders.Take(32).Select(value => new ConfigurationProvider(
                    Bound(value.Key), Bound(value.Value.DisplayName ?? value.Key), Bound(value.Value.ProviderType ?? value.Key),
                    value.Value.Enabled ?? CodeAltaProviderDocument.DefaultEnabled,
                    string.Equals(document.Chat?.DefaultProvider, value.Key, StringComparison.OrdinalIgnoreCase),
                    BoundOptional(value.Value.Model), BoundOptional(value.Value.ReasoningEffort))).ToArray();
                var configuredPlugins = document.Plugins ?? [];
                plugins = configuredPlugins.Take(32).Select(value => new ConfigurationPlugin(
                    Bound(value.Key), Bound(value.Key), null, value.Value.Enabled == false ? "Disabled" : "Configured", 0)).ToArray();
                return new ConfigurationSnapshot(providers, plugins, false, false,
                    configuredProviders.Count > providers.Length, configuredPlugins.Count > plugins.Length) { ProviderBrands = Brands() };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new ConfigurationSnapshot([], [], false, false, false, false);
            }
        }
        return new ConfigurationSnapshot(providers, plugins, providerRegistry is not null, pluginRuntime is not null,
            providerDescriptors.Count > providers.Length, pluginInstances.Count > plugins.Length) { ProviderBrands = Brands() };
    }

    // Every provider the configuration defines, enabled or not, with credentials or not: a session names its
    // provider by key, and is shown with the icon of that provider whether or not the provider can run now.
    private IReadOnlyList<ConfigurationProviderBrand> Brands()
    {
        var options = catalog ?? (_catalogRoot is null ? null : new CatalogOptions { GlobalRoot = _catalogRoot });
        if (options is null) return [];
        try
        {
            return [.. new CodeAltaConfigStore(options).LoadGlobalProviderDefinitions(includeDisabled: true)
                .Where(static value => value.ProviderKey.Length is > 0 and <= 256)
                .Take(64)
                .Select(static value => new ConfigurationProviderBrand(
                    value.ProviderKey, Bound(value.ProviderType ?? string.Empty), Bound(value.DisplayName ?? value.ProviderKey),
                    BoundOptional(value.Icon), BoundOptional(value.Color)))];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A configuration that cannot be read gives no icon: the page falls back to the key of each provider.
            return [];
        }
    }

    private static string Bound(string value) => value.Length <= 256 ? value : value[..256];
    private static string? BoundOptional(string? value) => value is null ? null : Bound(value);
}

internal sealed record ConfigurationRequest;
internal sealed record ConfigurationSnapshot(
    IReadOnlyList<ConfigurationProvider> Providers,
    IReadOnlyList<ConfigurationPlugin> Plugins,
    bool ProviderRuntimeAvailable,
    bool PluginRuntimeAvailable,
    bool ProvidersTruncated,
    bool PluginsTruncated)
{
    /// <summary>What every provider of the configuration is shown with, whether or not it is registered.</summary>
    public IReadOnlyList<ConfigurationProviderBrand> ProviderBrands { get; init; } = [];
}

/// <summary>What a provider is shown with: its name, and the icon and color its definition gives it.</summary>
/// <param name="Key">The key of the provider.</param>
/// <param name="Type">Its adapter type.</param>
/// <param name="Name">Its name.</param>
/// <param name="Icon">The id of its brand icon; null when the icon follows its key and type.</param>
/// <param name="Color">The color of that icon; null for the colors of the icon.</param>
internal sealed record ConfigurationProviderBrand(string Key, string Type, string Name, string? Icon, string? Color);
internal sealed record ConfigurationProvider(
    string Id,
    string Name,
    string Type,
    bool Enabled,
    bool IsDefault,
    string? DefaultModel,
    string? DefaultReasoning);
internal sealed record ConfigurationPlugin(
    string Id,
    string Name,
    string? Version,
    string State,
    int ContributionCount);
