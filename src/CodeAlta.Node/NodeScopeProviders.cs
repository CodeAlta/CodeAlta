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

    /// <summary>
    /// Gets what is told the providers an accepted change added, redefined, gave a new key or took away, so that the node
    /// runs them as they are now (<see cref="NodeProviders.Reconcile"/>); null tells nothing.
    /// </summary>
    internal Action<IReadOnlyCollection<string>>? ProvidersChanged { get; init; }

    /// <summary>
    /// The name of the secret that holds the key of a provider of a scope: one per id, never shared by two. An id of lowercase
    /// letters and digits in words joined by single dashes reads in it (<c>my-gateway</c>: <c>CODEALTA_PROVIDER_MY_GATEWAY</c>),
    /// which never holds two underscores in a row; any other id (<c>my_gateway</c>, <c>My-Gateway</c>) is named by a digest
    /// after two underscores, so that it never takes the name of another.
    /// </summary>
    /// <param name="providerId">The provider.</param>
    /// <returns>The name.</returns>
    internal static string SecretName(string providerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        const string Prefix = "CODEALTA_PROVIDER_";
        var readable = providerId.Length <= 64 && providerId.Split('-').All(static word => word.Length > 0 && word.All(static c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9')));
        return readable
            ? Prefix + providerId.ToUpperInvariant().Replace('-', '_')
            : Prefix + "_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(providerId)))[..32];
    }

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
        using var client = connector.Devices.CreateClient();
        var keys = await client.ListProviderKeysAsync(cancellationToken).ConfigureAwait(false);
        var list = connector.Devices.Current ?? await connector.Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var opened = new List<(ScopeProviderDto Provider, string Key)>();
        foreach (var key in keys.Items)
        {
            try
            {
                // The copy is signed over the definition: what is installed below is what the maintainer sealed, not what the server says.
                opened.Add((key.Provider, FleetProviderKeys.Open(key.Sealed, key.Wrapped, list.OrganizationId ?? string.Empty, key.Provider.Id, key.Provider.Version,
                    FleetProviderDefinition.Of(key.Provider), _identity, list, access)));
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
    // link no longer given go away. A provider of the user's own with the same key stays as it is. Versions only move forward:
    // a copy older than the version installed is refused and the installed one kept, and a provider taken away comes back
    // only at a newer version than the one removed, also after a restart.
    internal IReadOnlyList<string> Apply(string linkId, IReadOnlyList<(ScopeProviderDto Provider, string Key)> opened)
    {
        var applied = ReadApplied();
        var link = applied.GetValueOrDefault(linkId) ?? new ScopeProvidersOfLink();
        var before = new Dictionary<string, long>(link.Applied, StringComparer.Ordinal);
        var store = new CodeAltaConfigStore(_catalog);
        var definitions = store.LoadGlobalProviderDefinitions(includeDisabled: true).ToList();
        var ours = applied.Values.SelectMany(static other => other.Applied.Keys).ToHashSet(StringComparer.Ordinal);
        var kept = new Dictionary<string, long>(StringComparer.Ordinal);
        var changed = new List<string>();
        foreach (var (provider, key) in opened)
        {
            if (kept.ContainsKey(provider.Id)) continue;
            if (before.TryGetValue(provider.Id, out var held) && provider.Version < held)
            {
                if (LogManager.IsInitialized) _logger.Warn($"The provider {provider.Id} of {linkId} was given at version {provider.Version}, older than {held}: the installed one stays.");
                kept[provider.Id] = held;
                continue;
            }

            if (link.Removed.TryGetValue(provider.Id, out var removed) && provider.Version <= removed)
            {
                if (LogManager.IsInitialized) _logger.Warn($"The provider {provider.Id} of {linkId} was removed at version {removed}: version {provider.Version} does not bring it back.");
                continue;
            }

            var existing = definitions.FirstOrDefault(definition => string.Equals(definition.ProviderKey, provider.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && !ours.Contains(provider.Id)) continue;
            if (Differs(existing, provider, key)) changed.Add(provider.Id);
            if (existing is not null) definitions.Remove(existing);
            _secrets.SetSecret(SecretName(provider.Id), key, "fleet");
            definitions.Add(new CodeAltaProviderDocument
            {
                ProviderKey = provider.Id, DisplayName = provider.Name, ProviderType = provider.Type, ApiUrl = provider.Endpoint, Model = provider.Models.FirstOrDefault(),
                ApiKeySecret = SecretName(provider.Id), Enabled = true,
            });
            kept[provider.Id] = provider.Version;
            link.Removed.Remove(provider.Id);
        }

        foreach (var (gone, version) in before.Where(pair => !kept.ContainsKey(pair.Key)))
        {
            definitions.RemoveAll(definition => string.Equals(definition.ProviderKey, gone, StringComparison.OrdinalIgnoreCase));
            _secrets.DeleteSecret(SecretName(gone));
            link.Removed[gone] = Math.Max(version, link.Removed.GetValueOrDefault(gone));
            changed.Add(gone);
        }

        if (kept.Count > 0 || before.Count > 0) store.SaveGlobalProviderDefinitions(definitions);
        link.Applied = kept;
        applied[linkId] = link;
        WriteApplied(applied);
        if (LogManager.IsInitialized && (kept.Count > 0 || before.Count > 0)) _logger.Info($"The providers of {linkId} on this machine: {string.Join(", ", kept.Keys)}.");
        if (changed.Count > 0) ProvidersChanged?.Invoke(changed);
        return [.. kept.Keys];
    }

    // Whether a provider given differs from the one this machine runs: new, another definition, or another key (a rotation).
    private bool Differs(CodeAltaProviderDocument? existing, ScopeProviderDto provider, string key)
        => existing is null || existing.DisplayName != provider.Name || existing.ProviderType != provider.Type || existing.ApiUrl != provider.Endpoint
            || existing.Model != provider.Models.FirstOrDefault() || existing.Enabled == false
            || !_secrets.TryGetSecret(SecretName(provider.Id), out var held) || !string.Equals(held, key, StringComparison.Ordinal);

    private Dictionary<string, ScopeProvidersOfLink> ReadApplied()
    {
        var path = Path.Combine(_catalog.FleetRoot, FileName);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(StringComparer.Ordinal);
        }

        try
        {
            return new(JsonSerializer.Deserialize(text, NodeScopeProvidersJson.Default.DictionaryStringScopeProvidersOfLink) ?? [], StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // The first form of the file named the providers of each link, without versions: each is held at version 0.
            var ids = JsonSerializer.Deserialize(text, NodeScopeProvidersJson.Default.DictionaryStringListString) ?? [];
            return ids.ToDictionary(static pair => pair.Key,
                static pair => new ScopeProvidersOfLink { Applied = pair.Value.ToDictionary(static id => id, static _ => 0L, StringComparer.Ordinal) }, StringComparer.Ordinal);
        }
    }

    private void WriteApplied(Dictionary<string, ScopeProvidersOfLink> applied)
    {
        Directory.CreateDirectory(_catalog.FleetRoot);
        // Written whole, then moved over the file: a machine stopped halfway keeps the checkpoints it had.
        var path = Path.Combine(_catalog.FleetRoot, FileName);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(applied, NodeScopeProvidersJson.Default.DictionaryStringScopeProvidersOfLink));
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>The providers of one link on a machine: the version installed of each, and the last version of each one taken away.</summary>
internal sealed class ScopeProvidersOfLink
{
    /// <summary>Gets or sets the version installed of each provider.</summary>
    public Dictionary<string, long> Applied { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the version each provider taken away had: it comes back only above it.</summary>
    public Dictionary<string, long> Removed { get; set; } = new(StringComparer.Ordinal);
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, List<string>>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, ScopeProvidersOfLink>))]
internal sealed partial class NodeScopeProvidersJson : System.Text.Json.Serialization.JsonSerializerContext;