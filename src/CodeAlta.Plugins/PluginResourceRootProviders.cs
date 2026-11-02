using CodeAlta.Catalog.Skills;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>
/// Exposes plugin-contributed skill roots to the CodeAlta skill catalog.
/// </summary>
public sealed class PluginSkillRootProvider : ISkillRootProvider
{
    private readonly Func<IReadOnlyList<PluginResolvedResourceContribution>> _getResources;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginSkillRootProvider"/> class.
    /// </summary>
    /// <param name="getResources">A callback that returns current plugin resource roots.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="getResources"/> is <see langword="null"/>.</exception>
    public PluginSkillRootProvider(Func<IReadOnlyList<PluginResolvedResourceContribution>> getResources)
    {
        ArgumentNullException.ThrowIfNull(getResources);
        _getResources = getResources;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(
        SkillDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var roots = _getResources()
            .Where(static resource => resource.Kind == PluginResourceKind.SkillRoot)
            .Where(static resource => !string.IsNullOrWhiteSpace(resource.Path))
            .Select(static (resource, index) => new SkillRootRegistration
            {
                RootPath = resource.Path,
                SourceKind = SkillSourceKind.Plugin,
                SourceId = $"plugin:{resource.Handle.PluginRuntimeKey}:{resource.Handle.RuntimeContributionKey}",
                Scope = resource.Handle.PluginRuntimeKey.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase)
                    ? SkillScopeKind.Builtin
                    : SkillScopeKind.User,
                Precedence = SkillRootPrecedence.Builtin + resource.Precedence + index,
                IsTrusted = true,
            })
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<SkillRootRegistration>>(roots);
    }
}
