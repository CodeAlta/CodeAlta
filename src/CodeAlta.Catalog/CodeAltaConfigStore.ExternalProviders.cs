using System.Globalization;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;
using Tomlyn.Text;

namespace CodeAlta.Catalog;

/// <summary>
/// A provider of the global configuration whose type is not a built-in one: a type a plugin runs, or one a newer
/// version wrote. The store keeps its section as written and gives its values as they are.
/// </summary>
/// <param name="ProviderKey">The normalized key of the provider.</param>
/// <param name="ProviderType">The type, as the file writes it, trimmed and in lowercase.</param>
/// <param name="Values">
/// The values of its section, by key: <see cref="string"/>, <see cref="long"/>, <see cref="double"/>, <see cref="bool"/>,
/// lists (<see cref="IReadOnlyList{T}"/> of these) and tables (<see cref="IReadOnlyDictionary{TKey, TValue}"/>).
/// </param>
public sealed record CodeAltaExternalProvider(string ProviderKey, string ProviderType, IReadOnlyDictionary<string, object?> Values);

public sealed partial class CodeAltaConfigStore
{
    private const string ProvidersSection = "providers";

    /// <summary>
    /// Loads the providers of the global configuration whose type is not a built-in one, with the values of their sections.
    /// </summary>
    /// <returns>The providers, in the order of the file.</returns>
    /// <exception cref="InvalidDataException">The configuration cannot be parsed.</exception>
    /// <exception cref="IOException">The configuration cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the configuration is denied.</exception>
    public IReadOnlyList<CodeAltaExternalProvider> LoadGlobalExternalProviders()
    {
        var document = LoadGlobal();
        if (document.UnsupportedProviders.Count == 0 || (!ChangesGlobalContent && !File.Exists(_options.ConfigPath)))
        {
            return [];
        }

        TomlTable root;
        try
        {
            var content = ChangesGlobalContent ? LoadGlobalConfigContent() : SharedTextFile.ReadAllText(_options.ConfigPath);
            root = string.IsNullOrWhiteSpace(content)
                ? new TomlTable()
                : TomlSerializer.Deserialize<TomlTable>(content, TomlSerializerOptions.Default with { SourceName = _options.ConfigPath }) ?? new TomlTable();
        }
        catch (Exception ex) when (IsConfigLoadException(ex))
        {
            throw new InvalidDataException($"Failed to parse CodeAlta config '{_options.ConfigPath}'.", ex);
        }

        if (!root.TryGetValue(ProvidersSection, out var value) || value is not TomlTable providers)
        {
            return [];
        }

        var external = new List<CodeAltaExternalProvider>();
        foreach (var (key, section) in providers)
        {
            if (section is not TomlTable table || NormalizeProviderKey(key) is not { } providerKey ||
                document.UnsupportedProviders.FirstOrDefault(provider => string.Equals(provider.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase)) is not { } unsupported)
            {
                continue;
            }

            external.Add(new CodeAltaExternalProvider(providerKey, unsupported.ProviderType.Trim().ToLowerInvariant(), ToValues(table)));
        }

        return external;
    }

    /// <summary>
    /// Writes the section of a provider whose type is not a built-in one: <c>type</c>, then the values given, in the
    /// place of its section when it has one. The other sections of the file stay as they are.
    /// </summary>
    /// <param name="providerKey">The key of the provider.</param>
    /// <param name="providerType">Its type, which must not be a built-in one.</param>
    /// <param name="values">The values of its section, as <see cref="CodeAltaExternalProvider.Values"/> holds them; <c>type</c> is ignored.</param>
    /// <exception cref="ArgumentException">The key or the type is empty, or the type is a built-in one.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A built-in provider has the key, or the store is read-only.</exception>
    /// <exception cref="InvalidDataException">The configuration cannot be parsed, or a value cannot be written.</exception>
    /// <exception cref="IOException">The configuration cannot be read or written.</exception>
    public void SaveGlobalExternalProvider(string providerKey, string providerType, IReadOnlyDictionary<string, object?> values)
    {
        ThrowIfReadOnly();
        var key = NormalizeProviderKey(providerKey) ?? throw new ArgumentException("The provider key is empty.", nameof(providerKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(providerType);
        ArgumentNullException.ThrowIfNull(values);
        var type = providerType.Trim().ToLowerInvariant();
        if (NormalizeProviderType(string.Empty, type) is not null)
        {
            throw new ArgumentException($"'{type}' is the type of a built-in provider.", nameof(providerType));
        }

        if (NormalizeProviderType(key, type) is not null)
        {
            // A section under the key of a built-in provider is read as that provider, whatever its type says.
            throw new InvalidOperationException($"The key '{key}' is the one of a built-in provider.");
        }

        var section = new TomlTable { ["type"] = type };
        foreach (var (name, value) in values)
        {
            if (string.Equals(name, "type", StringComparison.OrdinalIgnoreCase) || value is null)
            {
                continue;
            }

            section[name] = ToToml(value) ?? throw new InvalidDataException($"The setting '{name}' of provider '{key}' has a value that cannot be written.");
        }

        try
        {
            EditConfigFile(_options.ConfigPath, content =>
            {
                var (root, options) = ParseTable(content);
                var providers = root.TryGetValue(ProvidersSection, out var existing) && existing is TomlTable table ? table : new TomlTable();
                var written = FindKey(providers, key);
                if (written is not null && providers[written] is TomlTable current &&
                    current.TryGetValue("type", out var currentType) && currentType is string currentText &&
                    NormalizeProviderType(key, currentText) is not null)
                {
                    throw new InvalidOperationException($"The provider '{key}' is a built-in provider.");
                }

                providers[written ?? key] = section;
                root[ProvidersSection] = providers;
                return TomlSerializer.Serialize(root, options);
            });
        }
        finally
        {
            Written();
        }
    }

    /// <summary>
    /// Removes the section of a provider whose type is not a built-in one.
    /// </summary>
    /// <param name="providerKey">The key of the provider.</param>
    /// <returns>Whether the section was removed; <see langword="false"/> when there is none, or it is the one of a built-in provider.</returns>
    /// <exception cref="InvalidDataException">The configuration cannot be parsed.</exception>
    /// <exception cref="IOException">The configuration cannot be read or written.</exception>
    /// <exception cref="InvalidOperationException">The store is read-only (<see cref="CodeAltaConfigOptions.ReadOnly"/>).</exception>
    public bool RemoveGlobalExternalProvider(string providerKey)
    {
        ThrowIfReadOnly();
        if (NormalizeProviderKey(providerKey) is not { } key)
        {
            return false;
        }

        var removed = false;
        try
        {
            EditConfigFile(_options.ConfigPath, content =>
            {
                var (root, options) = ParseTable(content);
                if (!root.TryGetValue(ProvidersSection, out var existing) || existing is not TomlTable providers ||
                    FindKey(providers, key) is not { } written || providers[written] is not TomlTable section ||
                    !section.TryGetValue("type", out var type) || type is not string text || NormalizeProviderType(key, text) is not null)
                {
                    return content;
                }

                providers.Remove(written);
                if (providers.Count == 0)
                {
                    root.Remove(ProvidersSection);
                }

                removed = true;
                return TomlSerializer.Serialize(root, options);
            });
        }
        finally
        {
            Written();
        }

        return removed;
    }

    private (TomlTable Root, TomlSerializerOptions Options) ParseTable(string content)
    {
        var options = TomlSerializerOptions.Default with
        {
            SourceName = _options.ConfigPath,
            MetadataStore = new TomlMetadataStore(),
            NewLine = content.Contains("\r\n", StringComparison.Ordinal) || (content.Length == 0 && OperatingSystem.IsWindows())
                ? TomlNewLineKind.CrLf
                : TomlNewLineKind.Lf,
        };
        try
        {
            return (string.IsNullOrWhiteSpace(content) ? new TomlTable() : TomlSerializer.Deserialize<TomlTable>(content, options) ?? new TomlTable(), options);
        }
        catch (Exception ex) when (IsConfigLoadException(ex))
        {
            throw new InvalidDataException($"Failed to parse CodeAlta config '{_options.ConfigPath}'.", ex);
        }
    }

    private static IReadOnlyDictionary<string, object?> ToValues(TomlTable table)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in table)
        {
            // Model ids are case-sensitive even though provider setting names are not.
            values[key] = key.Equals("context_preferences", StringComparison.OrdinalIgnoreCase) && value is TomlTable contexts
                ? contexts.ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal)
                : ToValue(value);
        }

        return values;
    }

    private static object? ToValue(object? value)
        => value switch
        {
            TomlTable table => ToValues(table),
            TomlTableArray tables => tables.Select(static table => (object?)ToValues(table)).ToArray(),
            TomlArray array => array.Select(ToValue).ToArray(),
            int number => (long)number,
            float number => (double)number,
            DateTime or DateTimeOffset or TomlDateTime => Convert.ToString(value, CultureInfo.InvariantCulture),
            _ => value,
        };

    private static object? ToToml(object value)
        => value switch
        {
            string or bool or long or double => value,
            int number => (long)number,
            float number => (double)number,
            IReadOnlyDictionary<string, object?> map => ToTomlTable(map),
            IReadOnlyDictionary<string, string> texts => ToTomlTable(texts.ToDictionary(static pair => pair.Key, static pair => (object?)pair.Value)),
            IEnumerable<object?> items => ToTomlArray(items),
            _ => null,
        };

    private static TomlTable? ToTomlTable(IReadOnlyDictionary<string, object?> map)
    {
        var table = new TomlTable(inline: true);
        foreach (var (name, item) in map)
        {
            if (item is null)
            {
                continue;
            }

            if (ToToml(item) is not { } converted)
            {
                return null;
            }

            table[name] = converted;
        }

        return table;
    }

    private static TomlArray? ToTomlArray(System.Collections.IEnumerable items)
    {
        var array = new TomlArray();
        foreach (var item in items)
        {
            if (item is null || ToToml(item) is not { } converted)
            {
                return null;
            }

            array.Add(converted);
        }

        return array;
    }
}
