using System.Reflection;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Declares optional metadata for a <see cref="PluginBase"/> implementation.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PluginAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginAttribute"/> class.
    /// </summary>
    public PluginAttribute()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginAttribute"/> class with an explicit runtime key hint.
    /// </summary>
    /// <param name="key">The optional author-provided key hint.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    public PluginAttribute(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = key;
    }

    /// <summary>
    /// Gets the optional author-provided key hint.
    /// </summary>
    public string? Key { get; }

    /// <summary>
    /// Gets or sets the human-readable plugin name.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Gets or sets the short plugin description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets or sets the plugin version string.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Gets or sets the plugin author.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Gets or sets the plugin license expression or text.
    /// </summary>
    public string? License { get; init; }

    /// <summary>
    /// Gets or sets the plugin project URL.
    /// </summary>
    public string? ProjectUrl { get; init; }

    /// <summary>
    /// Gets or sets an optional README anchor for package-level documentation.
    /// </summary>
    public string? ReadmeAnchor { get; init; }

    /// <summary>
    /// Gets or sets optional tags used for display and discovery.
    /// </summary>
    public string[] Tags { get; init; } = [];

    /// <summary>
    /// Gets or sets the minimum supported CodeAlta version hint.
    /// </summary>
    public string? MinCodeAltaVersion { get; init; }

    /// <summary>
    /// Gets or sets the maximum supported CodeAlta version hint.
    /// </summary>
    public string? MaxCodeAltaVersion { get; init; }
}

/// <summary>
/// Declares an optional dependency on another plugin.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class PluginDependencyAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginDependencyAttribute"/> class.
    /// </summary>
    /// <param name="pluginKey">The dependent plugin key.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="pluginKey"/> is empty.</exception>
    public PluginDependencyAttribute(string pluginKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginKey);
        PluginKey = pluginKey;
    }

    /// <summary>
    /// Gets the dependent plugin key.
    /// </summary>
    public string PluginKey { get; }

    /// <summary>
    /// Gets or sets an optional version range expression.
    /// </summary>
    public string? VersionRange { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the dependency is optional.
    /// </summary>
    public bool Optional { get; init; }
}

/// <summary>
/// Describes a plugin dependency declared by metadata or a future package manifest.
/// </summary>
public sealed record PluginDependency
{
    /// <summary>
    /// Gets the plugin key being depended on.
    /// </summary>
    public required string PluginKey { get; init; }

    /// <summary>
    /// Gets the optional version range.
    /// </summary>
    public string? VersionRange { get; init; }

    /// <summary>
    /// Gets a value indicating whether the dependency is optional.
    /// </summary>
    public bool Optional { get; init; }
}

/// <summary>
/// Describes a discovered plugin class and package metadata.
/// </summary>
public sealed record PluginDescriptor
{
    /// <summary>
    /// Gets the runtime-owned key used in diagnostics and contribution handles.
    /// </summary>
    public required string RuntimeKey { get; init; }

    /// <summary>
    /// Gets the plugin type full name.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Gets the plugin assembly simple name.
    /// </summary>
    public required string AssemblyName { get; init; }

    /// <summary>
    /// Gets the display name.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Gets the description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the version string.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Gets the author names.
    /// </summary>
    public IReadOnlyList<string> Authors { get; init; } = [];

    /// <summary>
    /// Gets the license expression or text.
    /// </summary>
    public string? License { get; init; }

    /// <summary>
    /// Gets the project URI.
    /// </summary>
    public Uri? ProjectUri { get; init; }

    /// <summary>
    /// Gets the optional README path discovered by the runtime.
    /// </summary>
    public string? ReadmePath { get; init; }

    /// <summary>
    /// Gets the optional README anchor.
    /// </summary>
    public string? ReadmeAnchor { get; init; }

    /// <summary>
    /// Gets the tags.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Gets plugin dependencies.
    /// </summary>
    public IReadOnlyList<PluginDependency> Dependencies { get; init; } = [];

    /// <summary>
    /// Gets extra descriptor metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets the minimum supported CodeAlta version hint.
    /// </summary>
    public string? MinCodeAltaVersion { get; init; }

    /// <summary>
    /// Gets the maximum supported CodeAlta version hint.
    /// </summary>
    public string? MaxCodeAltaVersion { get; init; }
}

/// <summary>
/// Builds plugin descriptors from type metadata.
/// </summary>
public static class PluginDescriptorFactory
{
    /// <summary>
    /// Creates a descriptor for a plugin type.
    /// </summary>
    /// <param name="pluginType">The plugin type.</param>
    /// <param name="packageDirectory">The optional package directory.</param>
    /// <param name="readmePath">The optional README path discovered by the runtime.</param>
    /// <returns>The descriptor for <paramref name="pluginType"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pluginType"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="pluginType"/> does not derive from <see cref="PluginBase"/>.</exception>
    public static PluginDescriptor FromType(Type pluginType, string? packageDirectory = null, string? readmePath = null)
    {
        ArgumentNullException.ThrowIfNull(pluginType);
        if (!typeof(PluginBase).IsAssignableFrom(pluginType))
        {
            throw new ArgumentException("The type must derive from PluginBase.", nameof(pluginType));
        }

        var attribute = pluginType.GetCustomAttribute<PluginAttribute>(inherit: false);
        var assemblyName = pluginType.Assembly.GetName().Name ?? pluginType.Assembly.FullName ?? "PluginAssembly";
        var typeName = pluginType.FullName ?? pluginType.Name;
        var runtimeKey = DeriveRuntimeKey(assemblyName, typeName, attribute?.Key);
        var projectUri = Uri.TryCreate(attribute?.ProjectUrl, UriKind.Absolute, out var parsedUri) ? parsedUri : null;
        var authors = string.IsNullOrWhiteSpace(attribute?.Author) ? [] : new[] { attribute.Author! };
        var dependencies = pluginType.GetCustomAttributes<PluginDependencyAttribute>(inherit: false)
            .Select(static dependency => new PluginDependency
            {
                PluginKey = dependency.PluginKey,
                VersionRange = dependency.VersionRange,
                Optional = dependency.Optional,
            })
            .ToArray();

        return new PluginDescriptor
        {
            RuntimeKey = runtimeKey,
            TypeName = typeName,
            AssemblyName = assemblyName,
            DisplayName = attribute?.DisplayName ?? pluginType.Name,
            Description = attribute?.Description,
            Version = attribute?.Version,
            Authors = authors,
            License = attribute?.License,
            ProjectUri = projectUri,
            ReadmePath = readmePath,
            ReadmeAnchor = attribute?.ReadmeAnchor,
            Tags = NormalizeTags(attribute?.Tags),
            Dependencies = dependencies,
            MinCodeAltaVersion = attribute?.MinCodeAltaVersion,
            MaxCodeAltaVersion = attribute?.MaxCodeAltaVersion,
            Metadata = string.IsNullOrWhiteSpace(packageDirectory)
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["PackageDirectory"] = packageDirectory! },
        };
    }

    /// <summary>
    /// Derives a stable runtime key from assembly, type, and optional author key data.
    /// </summary>
    /// <param name="assemblyName">The assembly simple name.</param>
    /// <param name="typeName">The plugin type name.</param>
    /// <param name="explicitKey">The optional explicit key.</param>
    /// <returns>A derived runtime key.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="assemblyName"/> or <paramref name="typeName"/> is empty.</exception>
    public static string DeriveRuntimeKey(string assemblyName, string typeName, string? explicitKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        return string.IsNullOrWhiteSpace(explicitKey)
            ? $"{assemblyName}:{typeName}"
            : $"{assemblyName}:{explicitKey}";
    }

    /// <summary>
    /// Validates a descriptor and returns non-fatal diagnostics suitable for management UI.
    /// </summary>
    /// <param name="descriptor">The descriptor to validate.</param>
    /// <returns>Validation diagnostics.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<PluginDescriptorDiagnostic> Validate(PluginDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var diagnostics = new List<PluginDescriptorDiagnostic>();
        if (string.IsNullOrWhiteSpace(descriptor.RuntimeKey))
        {
            diagnostics.Add(PluginDescriptorDiagnostic.Warning("RuntimeKey", "The runtime key is empty."));
        }

        if (string.IsNullOrWhiteSpace(descriptor.DisplayName))
        {
            diagnostics.Add(PluginDescriptorDiagnostic.Warning("DisplayName", "The display name is empty."));
        }

        var duplicateTags = descriptor.Tags
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .GroupBy(static tag => tag, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key);
        foreach (var tag in duplicateTags)
        {
            diagnostics.Add(PluginDescriptorDiagnostic.Warning("Tags", $"Duplicate tag '{tag}'."));
        }

        var duplicateDependencies = descriptor.Dependencies
            .GroupBy(static dependency => dependency.PluginKey, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key);
        foreach (var dependency in duplicateDependencies)
        {
            diagnostics.Add(PluginDescriptorDiagnostic.Warning("Dependencies", $"Duplicate dependency '{dependency}'."));
        }

        return diagnostics;
    }

    private static IReadOnlyList<string> NormalizeTags(string[]? tags)
    {
        if (tags is null || tags.Length == 0)
        {
            return [];
        }

        return tags.Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .Select(static tag => tag.Trim())
            .ToArray();
    }
}

/// <summary>
/// Describes a non-fatal plugin descriptor validation issue.
/// </summary>
public sealed record PluginDescriptorDiagnostic
{
    /// <summary>
    /// Gets the descriptor field associated with the diagnostic.
    /// </summary>
    public required string Field { get; init; }

    /// <summary>
    /// Gets the diagnostic message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Gets the severity.
    /// </summary>
    public PluginDiagnosticSeverity Severity { get; init; } = PluginDiagnosticSeverity.Warning;

    /// <summary>
    /// Creates a warning diagnostic.
    /// </summary>
    /// <param name="field">The descriptor field.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <returns>The diagnostic.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="field"/> or <paramref name="message"/> is empty.</exception>
    public static PluginDescriptorDiagnostic Warning(string field, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new PluginDescriptorDiagnostic
        {
            Field = field,
            Message = message,
            Severity = PluginDiagnosticSeverity.Warning,
        };
    }
}
