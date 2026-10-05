using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>
/// Describes a built-in plugin shipped with CodeAlta.
/// </summary>
public sealed record BuiltInPluginDefinition
{
    /// <summary>Gets the stable built-in plugin id.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the plugin display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Gets the plugin description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets a value indicating whether the built-in plugin is enabled by default.</summary>
    public bool EnabledByDefault { get; init; } = true;

    /// <summary>Gets the factory invoked once per built-in activation attempt.</summary>
    /// <remarks>
    /// Return a new instance of the resolved concrete plugin type. Metadata resolution can also invoke this
    /// factory when <see cref="PluginType"/> is omitted; this is not an exactly-once lifetime guarantee.
    /// Activation invokes the factory directly, without reflection's constructor-exception wrapping.
    /// Factory cancellation follows the runtime's existing cancellation exclusion, not a reflection fallback.
    /// </remarks>
    public required Func<PluginBase> Factory { get; init; }

    /// <summary>Gets the concrete plugin type, when known without invoking the factory.</summary>
    public Type? PluginType { get; init; }

    /// <summary>Gets the CodeAlta applications the built-in plugin supports. The default is both.</summary>
    public PluginFrontends Frontends { get; init; } = PluginFrontends.All;

    /// <summary>Gets the built-in plugin descriptor.</summary>
    public PluginDescriptor CreateDescriptor()
    {
        var pluginType = ResolvePluginType();
        return new PluginDescriptor
        {
            RuntimeKey = "builtin:" + Id,
            TypeName = pluginType.FullName ?? pluginType.Name,
            AssemblyName = pluginType.Assembly.GetName().Name ?? "CodeAlta",
            DisplayName = DisplayName,
            Description = Description,
            Frontends = Frontends,
            Metadata = new Dictionary<string, string>
            {
                ["PluginKind"] = PluginLoadUnitKind.BuiltIn.ToString(),
                ["BuiltInId"] = Id,
            },
        };
    }

    /// <summary>Resolves the concrete plugin type.</summary>
    /// <returns>The concrete plugin type.</returns>
    /// <remarks>Without an explicit type, a factory declared to return <see cref="PluginBase"/> is invoked on each resolution. Specify <see cref="PluginType"/> to avoid metadata-time construction.</remarks>
    public Type ResolvePluginType()
        => PluginType ?? (Factory.Method.ReturnType == typeof(PluginBase) ? Factory().GetType() : Factory.Method.ReturnType);
}

/// <summary>
/// Stores built-in plugin definitions and exposes them in deterministic order.
/// </summary>
public sealed class BuiltInPluginRegistry
{
    private readonly List<BuiltInPluginDefinition> _definitions = [];

    /// <summary>
    /// Adds a built-in plugin definition.
    /// </summary>
    /// <param name="definition">The built-in plugin definition.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the id is duplicated.</exception>
    public void Add(BuiltInPluginDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Id);
        if (_definitions.Any(existing => string.Equals(existing.Id, definition.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A built-in plugin with id '{definition.Id}' is already registered.");
        }

        _definitions.Add(definition);
        _definitions.Sort(static (left, right) => string.Compare(left.Id, right.Id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets registered built-in plugin definitions.
    /// </summary>
    /// <returns>Definitions in deterministic id order.</returns>
    public IReadOnlyList<BuiltInPluginDefinition> GetDefinitions() => _definitions.ToArray();

    /// <summary>
    /// Gets the management message for a built-in reload action.
    /// </summary>
    /// <param name="definition">The built-in plugin definition.</param>
    /// <returns>The management message.</returns>
    public static string GetReloadRequiresRestartMessage(BuiltInPluginDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return $"Built-in plugin '{definition.Id}' cannot be code-reloaded while CodeAlta is running. Restart CodeAlta to load changed built-in code.";
    }
}
