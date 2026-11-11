using Tomlyn;
using Tomlyn.Model;

namespace CodeAlta.Catalog;

public sealed partial class CodeAltaConfigStore
{
    /// <summary>
    /// Reads the section of each provider of a configuration file as text (<c>[providers.&lt;key&gt;]</c>), to tell which providers
    /// changed between two reads of the file: a section written again with the same values reads the same.
    /// </summary>
    /// <param name="path">The configuration file.</param>
    /// <returns>The sections by key; empty when the file does not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="IOException">The file could not be read (still being written).</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    /// <exception cref="InvalidDataException">The file is not valid TOML; the message says where.</exception>
    public static IReadOnlyDictionary<string, string> ReadProviderSections(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return sections;
        var text = File.ReadAllText(path);
        TomlTable? root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(text, TomlSerializerOptions.Default with { SourceName = path });
        }
        catch (TomlException exception)
        {
            throw new InvalidDataException(exception.Message, exception);
        }

        if (root is not null && root.TryGetValue("providers", out var value) && value is TomlTable providers)
        {
            foreach (var (key, section) in providers) sections[key] = section is TomlTable table ? TomlSerializer.Serialize(table) : section?.ToString() ?? "";
        }

        return sections;
    }
}
