using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Catalog.Skills;
using ModelContextProtocol.Protocol;

namespace CodeAlta.Plugin.Mcp;

/// <summary>
/// Checks what a server answered for a file of a skill against the entry CodeAlta holds: the bytes of the read (the UTF-8
/// of a text, or the decoded blob) must have the size and the SHA-256 of the entry, and the frontmatter of a
/// <c>SKILL.md</c> must equal the one of the entry, field by field. Any difference is a verification failure.
/// </summary>
internal static class McpSkillVerifier
{
    /// <summary>Gets the bytes of the content of a read for a URI: the content with that URI, or the only one.</summary>
    /// <returns>The bytes, or null with the reason when the answer has no content for the URI.</returns>
    public static byte[]? GetBytes(ReadResourceResult result, string uri, out string? error)
    {
        ArgumentNullException.ThrowIfNull(result);
        var contents = result.Contents.Where(content => string.Equals(content.Uri, uri, StringComparison.Ordinal)).ToArray();
        var content = contents.Length == 1 ? contents[0] : result.Contents.Count == 1 ? result.Contents[0] : null;
        switch (content)
        {
            case TextResourceContents text:
                error = null;
                return Encoding.UTF8.GetBytes(text.Text);
            case BlobResourceContents blob:
                error = null;
                return blob.DecodedData.ToArray();
            default:
                error = $"The server answered no single content for `{McpSkillValidator.Shorten(uri)}`.";
                return null;
        }
    }

    /// <summary>Checks the bytes of a file against its entry: first their number, then their SHA-256.</summary>
    /// <returns>Null when they match, else what differs.</returns>
    public static string? VerifyFile(McpSkillFile file, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (bytes.Length != file.Size)
        {
            return $"`{McpSkillValidator.Shorten(file.Uri)}` has {bytes.Length} bytes, not the {file.Size} of its entry.";
        }

        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        return string.Equals(digest, file.Digest, StringComparison.Ordinal)
            ? null
            : $"The SHA-256 of `{McpSkillValidator.Shorten(file.Uri)}` is not the digest of its entry.";
    }

    /// <summary>Checks that the frontmatter of a <c>SKILL.md</c> equals the one of its entry, field by field.</summary>
    /// <returns>Null when it does, else what differs.</returns>
    public static string? VerifyFrontmatter(McpSkillEntry entry, string skillFile)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(skillFile);
        if (!SkillFrontmatterReader.TrySplit(skillFile, out var yaml, out _))
        {
            return "The `SKILL.md` has no frontmatter.";
        }

        JsonObject frontmatter;
        try
        {
            frontmatter = SkillFrontmatterReader.ReadAsJson(yaml);
        }
        catch (InvalidDataException exception)
        {
            return exception.Message;
        }

        foreach (var (key, value) in frontmatter)
        {
            if (!entry.Frontmatter.TryGetPropertyValue(key, out var expected)) return $"The `SKILL.md` has the field `{key}`, which its entry does not.";
            if (!JsonEquals(value, expected)) return $"The field `{key}` of the `SKILL.md` is not the one of its entry.";
        }

        foreach (var (key, _) in entry.Frontmatter)
        {
            if (!frontmatter.ContainsKey(key)) return $"The entry has the field `{key}`, which the `SKILL.md` does not.";
        }

        return null;
    }

    /// <summary>Compares two JSON values: the same keys and values, numbers by value whatever their form.</summary>
    public static bool JsonEquals(JsonNode? left, JsonNode? right)
    {
        switch (left, right)
        {
            case (null, null):
                return true;
            case (JsonObject a, JsonObject b):
                return a.Count == b.Count && a.All(pair => b.TryGetPropertyValue(pair.Key, out var other) && JsonEquals(pair.Value, other));
            case (JsonArray a, JsonArray b):
                return a.Count == b.Count && a.Zip(b).All(static pair => JsonEquals(pair.First, pair.Second));
            case (JsonValue a, JsonValue b):
                var kind = a.GetValueKind();
                if (kind != b.GetValueKind()) return false;
                return kind switch
                {
                    JsonValueKind.String => string.Equals(a.GetValue<string>(), b.GetValue<string>(), StringComparison.Ordinal),
                    JsonValueKind.Number => NumberEquals(a, b),
                    _ => true,
                };
            default:
                return false;
        }
    }

    // A value made from a number of .NET and one read from JSON give no common type: their JSON text does.
    private static bool NumberEquals(JsonValue left, JsonValue right)
    {
        const System.Globalization.NumberStyles style = System.Globalization.NumberStyles.Float;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        string a = left.ToJsonString(), b = right.ToJsonString();
        if (decimal.TryParse(a, style, culture, out var x) && decimal.TryParse(b, style, culture, out var y)) return x == y;
        return double.TryParse(a, style, culture, out var p) && double.TryParse(b, style, culture, out var q) && p.Equals(q);
    }
}
