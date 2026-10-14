using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Desktop;

/// <summary>
/// Says which provider a new session starts with: among the enabled providers of its space, the default provider of
/// the configuration (<c>[chat].default_provider</c>, of the project then of the user) when it is one of them,
/// otherwise the first of them. The page, a new session, a work item and an automation all take it from here, so that
/// they agree.
/// </summary>
internal sealed class DesktopDefaultProvider
{
    private readonly CatalogOptions _options;
    private readonly Lock _gate = new();
    private (DateTime Written, long Length, string? Key)? _global;
    private (DateTime Written, long Length, SpaceAssignments Assignments)? _assignments;

    internal DesktopDefaultProvider(CatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Gets the default provider the configuration names, whether or not it is enabled; null when it names none or cannot be read.</summary>
    /// <param name="projectPath">The folder of a project, whose configuration comes first; null for the user's configuration alone.</param>
    internal string? Configured(string? projectPath = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(projectPath)) return new CodeAltaConfigStore(_options).GetEffectiveDefaultProvider(projectPath);

            // The page asks often: the user's file is read again only when it changed.
            var file = new FileInfo(_options.ConfigPath);
            var stamp = file.Exists ? (file.LastWriteTimeUtc, file.Length) : (default, -1L);
            lock (_gate)
            {
                if (_global is { } known && (known.Written, known.Length) == stamp) return known.Key;
            }

            var key = new CodeAltaConfigStore(_options).GetEffectiveDefaultProvider();
            lock (_gate)
            {
                _global = (stamp.Item1, stamp.Item2, key);
            }

            return key;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A configuration that cannot be read names no default: the first enabled provider is the one.
            return null;
        }
    }

    /// <summary>Gets the provider a new session starts with among the enabled ones; null when none is enabled.</summary>
    /// <param name="enabled">The enabled providers, in the order they are listed.</param>
    /// <param name="projectPath">The folder of the project of the session; null for a session of no project.</param>
    internal ModelProviderDescriptor? Of(IReadOnlyList<ModelProviderDescriptor> enabled, string? projectPath = null)
        => Pick(enabled, Configured(projectPath));

    /// <summary>
    /// Gets the provider a new session of a space starts with among the enabled providers of that space; null when
    /// none of them is.
    /// </summary>
    /// <param name="enabled">The enabled providers, in the order they are listed.</param>
    /// <param name="projectPath">The folder of the project of the session; null for a session of no project.</param>
    /// <param name="spaceId">The space of the session; null for the default space.</param>
    internal ModelProviderDescriptor? Of(IReadOnlyList<ModelProviderDescriptor> enabled, string? projectPath, string? spaceId)
        => Pick(InSpace(enabled, spaceId), Configured(projectPath));

    /// <summary>Keeps the providers available in a space, in their order.</summary>
    /// <param name="providers">The providers.</param>
    /// <param name="spaceId">The space; null for the default space.</param>
    internal IReadOnlyList<ModelProviderDescriptor> InSpace(IReadOnlyList<ModelProviderDescriptor> providers, string? spaceId)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var assignments = Assignments();
        return [.. providers.Where(provider => assignments.IsAvailable(SpaceResourceKind.Provider, provider.ProviderId.Value, spaceId))];
    }

    /// <summary>Tells whether a provider is available in a space.</summary>
    /// <param name="providerKey">The key of the provider.</param>
    /// <param name="spaceId">The space; null for the default space.</param>
    internal bool IsInSpace(string providerKey, string? spaceId)
        => string.IsNullOrWhiteSpace(providerKey) || Assignments().IsAvailable(SpaceResourceKind.Provider, providerKey, spaceId);

    /// <summary>
    /// Gets the spaces each provider is assigned to in the user's configuration; a configuration that cannot be read
    /// assigns none, so that every provider stays in every space.
    /// </summary>
    internal SpaceAssignments Assignments()
    {
        try
        {
            var file = new FileInfo(_options.ConfigPath);
            var stamp = file.Exists ? (file.LastWriteTimeUtc, file.Length) : (default, -1L);
            lock (_gate)
            {
                if (_assignments is { } known && (known.Written, known.Length) == stamp) return known.Assignments;
            }

            var assignments = new CodeAltaConfigStore(_options).LoadGlobalSpaceAssignments();
            lock (_gate)
            {
                _assignments = (stamp.Item1, stamp.Item2, assignments);
            }

            return assignments;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return SpaceAssignments.Empty;
        }
    }

    /// <summary>
    /// Gets the configured provider when it is one of the enabled ones, otherwise the first of them that its own
    /// definition marks as a default (every configured provider is), otherwise the first of them.
    /// </summary>
    internal static ModelProviderDescriptor? Pick(IReadOnlyList<ModelProviderDescriptor> enabled, string? configured)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        return (string.IsNullOrWhiteSpace(configured) ? null
            : enabled.FirstOrDefault(provider => string.Equals(provider.ProviderId.Value, configured.Trim(), StringComparison.OrdinalIgnoreCase)))
            ?? enabled.FirstOrDefault(static provider => provider.IsDefault)
            ?? enabled.FirstOrDefault();
    }
}
