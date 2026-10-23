using System.Collections;
using System.Globalization;
using System.Text.Json.Nodes;
using SharpYaml;

namespace CodeAlta.Catalog.Skills;

/// <summary>
/// Reads the YAML frontmatter of a <c>SKILL.md</c>: splits it from the body, and reads it as JSON, typed as YAML 1.2
/// types its scalars, for a reader that compares it with the JSON form a server gives (MCP skills).
/// </summary>
public static class SkillFrontmatterReader
{
    private const string Delimiter = "---";

    /// <summary>Splits a <c>SKILL.md</c> into its frontmatter, between the first two <c>---</c> lines, and its body.</summary>
    /// <param name="contents">The text of the file.</param>
    /// <param name="frontmatter">The YAML of the frontmatter, its lines joined with <c>\n</c>; empty when there is none.</param>
    /// <param name="body">The text after the frontmatter, or the whole text when there is none.</param>
    /// <returns>Whether the text starts with a frontmatter that is closed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contents"/> is <see langword="null"/>.</exception>
    public static bool TrySplit(string contents, out string frontmatter, out string body)
    {
        ArgumentNullException.ThrowIfNull(contents);
        frontmatter = string.Empty;
        body = contents;
        if (!contents.StartsWith(Delimiter, StringComparison.Ordinal))
        {
            return false;
        }

        using var reader = new StringReader(contents);
        _ = reader.ReadLine();
        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.Equals(line.Trim(), Delimiter, StringComparison.Ordinal))
            {
                frontmatter = string.Join('\n', lines);
                body = reader.ReadToEnd() ?? string.Empty;
                return true;
            }

            lines.Add(line);
        }

        return false;
    }

    /// <summary>
    /// Reads the YAML of a frontmatter as a JSON object: maps are objects, sequences are arrays, and a scalar is a
    /// boolean, a number, null or a string as YAML 1.2 types it.
    /// </summary>
    /// <param name="frontmatter">The YAML of the frontmatter.</param>
    /// <returns>The frontmatter as JSON; an empty object for an empty frontmatter.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frontmatter"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The YAML cannot be read, or its root is not a map.</exception>
    public static JsonObject ReadAsJson(string frontmatter)
    {
        ArgumentNullException.ThrowIfNull(frontmatter);
        object? root;
        try
        {
            root = YamlSerializer.Deserialize<object>(frontmatter);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException("The frontmatter is not valid YAML: " + exception.Message, exception);
        }

        return root switch
        {
            null => [],
            IDictionary map => ToObject(map),
            _ => throw new InvalidDataException("The frontmatter is not a map."),
        };
    }

    private static JsonObject ToObject(IDictionary map)
    {
        var result = new JsonObject();
        foreach (DictionaryEntry entry in map)
        {
            var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
            result[key] = ToNode(entry.Value);
        }

        return result;
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        sbyte or byte or short or ushort or int => JsonValue.Create(Convert.ToInt32(value, CultureInfo.InvariantCulture)),
        uint or long => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        ulong number => JsonValue.Create(number),
        float or double => JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        decimal number => JsonValue.Create(number),
        IDictionary map => ToObject(map),
        IEnumerable items => new JsonArray([.. items.Cast<object?>().Select(ToNode)]),
        // A scalar of another type (a date, a big number) keeps the text YAML gives it.
        IFormattable formattable => JsonValue.Create(formattable.ToString(null, CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(value.ToString()),
    };
}
