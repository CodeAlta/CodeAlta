using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Hosting;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Rpc;

// The providers whose key is a secret kept in a vault: one that was left out for want of its secret is registered once the secret
// can be given (its vault unlocked, the secret bound, a use confirmed). A provider that runs is left as it is: its sessions keep it.
internal sealed partial class GlobalConfigService
{
    /// <summary>Registers the providers that name one of these secrets and are not registered, and prepares them.</summary>
    /// <param name="names">The secrets that can be given differently now.</param>
    /// <returns>A task that ends once they were prepared.</returns>
    internal async Task ApplySecretsChangedAsync(IReadOnlyCollection<string> names)
    {
        if (_store is null || _registry is null || names.Count == 0) return;
        List<string> registered = [];
        lock (_gate)
        {
            try
            {
                var running = _registry.ListProviders(includeDisabled: true).Select(static descriptor => descriptor.ProviderId.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var definitions = _store.LoadGlobalProviderDefinitions(includeDisabled: false)
                    .Where(definition => definition.Enabled != false && !running.Contains(definition.ProviderKey) && Names(definition, names))
                    .ToList();
                if (definitions.Count > 0)
                {
                    registered.AddRange(ConfiguredModelProviderRegistryBuilder.RegisterOrReplaceConfiguredProviders(_registry, definitions, _stateRoot!, ModelCatalog)
                        .Select(static descriptor => descriptor.ProviderId.Value));
                }

                // The providers of the types of plugins, whose references are in their settings (the environment of an agent...).
                if (PluginProviderTypes is { } types)
                {
                    registered.AddRange(types.RegisterMissing(_registry, _store, _stateRoot!, settings => settings.Values.Values.Any(value => Refers(value, names)))
                        .Select(static descriptor => descriptor.ProviderId.Value));
                }

                if (registered.Count == 0) return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
            {
                LogManager.GetLogger("CodeAlta.Desktop").Warn($"The providers of the secrets kept in vaults could not be registered: {exception.Message}");
                return;
            }
        }

        foreach (var key in registered)
        {
            if (RefreshProviderAfterSignIn is not { } refresh) break;
            try
            {
                await refresh(key, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogManager.GetLogger("CodeAlta.Desktop").Warn($"The provider {key} could not be prepared: {exception.Message}");
            }
        }

        if (registered.Count > 0) ProvidersChanged?.Invoke();
    }

    // Whether a provider takes its key, or a variable of its program, from one of these secrets.
    private static bool Names(CodeAltaProviderDocument definition, IReadOnlyCollection<string> names)
        => definition.ApiKeySecret is { } secret && names.Contains(secret, StringComparer.OrdinalIgnoreCase)
            || Refers(definition.ApiKey, names)
            || (definition.Env?.Values.Any(value => Refers(value, names)) ?? false);

    // Whether a setting, a text or the texts of a list or a table, refers to one of these secrets.
    private static bool Refers(object? value, IReadOnlyCollection<string> names) => value switch
    {
        string text => SecretReferences.Find(text).Any(reference => reference.Kind == SecretReferenceKind.Secret && names.Contains(reference.Name, StringComparer.OrdinalIgnoreCase)),
        IReadOnlyDictionary<string, object?> table => table.Values.Any(item => Refers(item, names)),
        IEnumerable<object?> list => list.Any(item => Refers(item, names)),
        _ => false,
    };
}
