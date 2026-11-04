using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using XenoAtom.Logging;

namespace CodeAlta.Node;

/// <summary>
/// The providers of the teams and of the organization a machine runs with (<c>doc/fleet/orchestration.md</c>, credentials of
/// unattended runs): read from the server of a link when access changes, each key opened only when its copy was given by
/// someone who answers for the owner of the provider, as the access document this machine verified says. The key is kept in
/// the protected store of the machine and the provider written to its configuration with <c>api_key_secret</c>, so that
/// it runs with the key of an account; a provider taken away, or one this machine no longer holds a copy of, is removed.
/// A provider of the user's own with the same key is never replaced.
/// </summary>
internal sealed class NodeScopeProviders
{
    private const string FileName = "scope-providers.json";
    private readonly Logger _logger = LogManager.GetLogger("CodeAlta.Node.Providers");
    private readonly CatalogOptions _catalog;
    private readonly LocalSecretStore _secrets;
    private readonly DeviceIdentity _identity;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the providers of a machine.</summary>
    /// <param name="catalog">The options of the catalog of the machine, whose configuration holds the providers.</param>
    /// <param name="secrets">The protected store of the machine.</param>
    /// <param name="identity">The key of this machine.</param>
    internal NodeScopeProviders(CatalogOptions catalog, LocalSecretStore secrets, DeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(identity);
        (_catalog, _secrets, _identity) = (catalog, secrets, identity);
    }

    /// <summary>The name of the secret that holds the key of a provider of a scope.</summary>
    /// <param name="providerId">The provider.</param>
    /// <returns>The name.</returns>
    internal static string SecretName(string providerId) => "CODEALTA_PROVIDER_" + providerId.ToUpperInvariant().Replace('-', '_');

    /// <summary>Reads the providers a server gives this machine again, and applies them.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="connector">The connector of the link.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The ids of the providers this machine runs with from that link.</returns>
    /// <exception cref="FleetServerException">The server refused the read.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    internal async Task<IReadOnlyList<string>> RefreshAsync(string linkId, FleetConnector connector, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(linkId);
        ArgumentNullException.ThrowIfNull(connector);
        if (connector.Access.Current is not { } access) return [];
        using var client = new FleetServerClient(connector.Devices.ServerAddress, _identity);
        var keys = await client.ListProviderKeysAsync(cancellationToken).ConfigureAwait(false);
        var list = connector.Devices.Current ?? await connector.Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var opened = new List<(ScopeProviderDto Provider, string Key)>();
        foreach (var key in keys.Items)
        {
            try
            {
                opened.Add((key.Provider, FleetProviderKeys.Open(key.Sealed, key.Wrapped, list.OrganizationId ?? string.Empty, key.Provider.Id, key.Provider.Version,
                    key.Provider.OwnerKind, key.Provider.OwnerId, _identity, list, access)));
            }
            catch (InvalidDataException exception)
            {
                if (LogManager.IsInitialized) _logger.Warn($"The key of the provider {key.Provider.Id} was refused: {exception.Message}");
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Apply(linkId, opened);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Writes the providers of a link: the keys in the protected store, the definitions in the configuration; those of the
    // link no longer given go away. A provider of the user's own with the same key stays as it is.
    internal IReadOnlyList<string> Apply(string linkId, IReadOnlyList<(ScopeProviderDto Provider, string Key)> opened)
    {
        var applied = ReadApplied();
        var before = applied.GetValueOrDefault(linkId) ?? [];
        var store = new CodeAltaConfigStore(_catalog);
        var definitions = store.LoadGlobalProviderDefinitions(includeDisabled: true).ToList();
        var ours = applied.Values.SelectMany(static ids => ids).ToHashSet(StringComparer.Ordinal);
        var kept = new List<string>();
        foreach (var (provider, key) in opened)
        {
            var existing = definitions.FirstOrDefault(definition => string.Equals(definition.ProviderKey, provider.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && !ours.Contains(provider.Id)) continue;
            if (existing is not null) definitions.Remove(existing);
            _secrets.SetSecret(SecretName(provider.Id), key, "fleet");
            definitions.Add(new CodeAltaProviderDocument
            {
                ProviderKey = provider.Id, DisplayName = provider.Name, ProviderType = provider.Type, ApiUrl = provider.Endpoint, Model = provider.Models.FirstOrDefault(),
                ApiKeySecret = SecretName(provider.Id), Enabled = true,
            });
            kept.Add(provider.Id);
        }

        foreach (var gone in before.Where(id => !kept.Contains(id, StringComparer.Ordinal)))
        {
            definitions.RemoveAll(definition => string.Equals(definition.ProviderKey, gone, StringComparison.OrdinalIgnoreCase));
            _secrets.DeleteSecret(SecretName(gone));
        }

        if (kept.Count > 0 || before.Count > 0) store.SaveGlobalProviderDefinitions(definitions);
        applied[linkId] = kept;
        WriteApplied(applied);
        if (LogManager.IsInitialized && (kept.Count > 0 || before.Count > 0)) _logger.Info($"The providers of {linkId} on this machine: {string.Join(", ", kept)}.");
        return kept;
    }

    private Dictionary<string, List<string>> ReadApplied()
    {
        var path = Path.Combine(_catalog.FleetRoot, FileName);
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), NodeScopeProvidersJson.Default.DictionaryStringListString) ?? new(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private void WriteApplied(Dictionary<string, List<string>> applied)
    {
        Directory.CreateDirectory(_catalog.FleetRoot);
        File.WriteAllText(Path.Combine(_catalog.FleetRoot, FileName), JsonSerializer.Serialize(applied, NodeScopeProvidersJson.Default.DictionaryStringListString));
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, List<string>>))]
internal sealed partial class NodeScopeProvidersJson : System.Text.Json.Serialization.JsonSerializerContext;
