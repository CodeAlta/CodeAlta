using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAlta.Plugin.Mcp;

/// <summary>
/// Checks an entry of <c>skills/list</c> or <c>skills/get</c> before CodeAlta keeps it: an entry that fails is never
/// shown to the model, and the reason is kept for Settings. Unknown fields are ignored.
/// </summary>
internal static class McpSkillValidator
{
    private const string Sha256Prefix = "sha256:";
    private const int MaximumNameLength = 64;
    private const int MaximumDescriptionLength = 1024;
    private const int MaximumUriLength = 2048;

    /// <summary>Reads and checks an entry.</summary>
    /// <param name="node">The entry as the server sent it.</param>
    /// <param name="entry">The entry when it is valid.</param>
    /// <param name="error">Why it is not, naming its URI when it has one.</param>
    /// <returns>Whether the entry is valid.</returns>
    public static bool TryParse(JsonNode? node, out McpSkillEntry? entry, out string? error)
    {
        entry = null;
        if (node is not JsonObject value)
        {
            error = "A skill entry is not an object.";
            return false;
        }

        var uri = ReadString(value["uri"]);
        if (uri is null)
        {
            error = "A skill entry has no `uri`.";
            return false;
        }

        error = Check(value, uri, out entry);
        if (error is not null)
        {
            error = $"The skill `{Shorten(uri)}` is invalid: {error}";
            entry = null;
            return false;
        }

        return true;
    }

    private static string? Check(JsonObject value, string uri, out McpSkillEntry? entry)
    {
        entry = null;
        if (uri.Length > MaximumUriLength) return "its URI is too long.";
        if (!TrySplitUri(uri, out var root, out var name)) return "its URI is not the URI of a `SKILL.md` in a directory.";
        if (value["frontmatter"] is not JsonObject frontmatter) return "it has no `frontmatter` object.";
        var declaredName = ReadString(frontmatter["name"]);
        if (declaredName is null) return "its frontmatter has no `name`.";
        if (!string.Equals(declaredName, name, StringComparison.Ordinal)) return $"its name `{Shorten(declaredName)}` is not the last segment of its path, `{Shorten(name)}`.";
        if (!IsValidName(name)) return $"its name `{Shorten(name)}` does not follow the naming rules of Agent Skills.";
        var description = ReadString(frontmatter["description"]);
        if (string.IsNullOrWhiteSpace(description)) return "its frontmatter has no `description`.";
        if (description.Length > MaximumDescriptionLength) return "its description is longer than 1024 characters.";

        IReadOnlyList<McpSkillFile>? files;
        switch (value["resources"])
        {
            case JsonValue dynamic when dynamic.TryGetValue<string>(out var text) && text == "dynamic":
                files = null;
                break;
            case JsonArray array:
                var list = new List<McpSkillFile>(array.Count);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (array.Count > McpSkills.MaximumFiles) return $"it has more than {McpSkills.MaximumFiles} files.";
                long total = 0;
                foreach (var item in array)
                {
                    if (item is not JsonObject file) return "a file of `resources` is not an object.";
                    var fileUri = ReadString(file["uri"]);
                    if (fileUri is null || fileUri.Length > MaximumUriLength) return "a file of `resources` has no valid `uri`.";
                    if (!IsUnder(root, fileUri)) return $"the file `{Shorten(fileUri)}` is not in the directory of the skill.";
                    if (!seen.Add(fileUri)) return $"the file `{Shorten(fileUri)}` is listed twice.";
                    var digest = ReadString(file["digest"]);
                    if (digest is null || !IsValidDigest(digest)) return $"the digest of `{Shorten(fileUri)}` is not `sha256:` and 64 lowercase hexadecimal characters.";
                    if (file["size"] is not JsonValue sizeValue || !TryReadSize(sizeValue, out var size)) return $"the size of `{Shorten(fileUri)}` is not a number of bytes.";
                    total += size;
                    if (total > McpSkills.MaximumTotalBytes) return "its files are larger than 16 MiB in all.";
                    list.Add(new McpSkillFile(fileUri, digest, size));
                }

                if (!seen.Contains(uri)) return "its `resources` do not list its `SKILL.md`.";
                files = list;
                break;
            default:
                return "its `resources` are neither a list of files nor `dynamic`.";
        }

        entry = new McpSkillEntry(uri, (JsonObject)frontmatter.DeepClone(), files, value["_meta"] is JsonObject meta ? (JsonObject)meta.DeepClone() : null);
        return null;
    }

    /// <summary>Splits the URI of a <c>SKILL.md</c> into the URI of its directory and the name of the skill.</summary>
    public static bool TrySplitUri(string uri, out string root, out string name)
    {
        root = string.Empty;
        name = string.Empty;
        var suffix = "/" + McpSkills.SkillFileName;
        var scheme = uri.IndexOf("://", StringComparison.Ordinal);
        if (scheme <= 0 || !uri.EndsWith(suffix, StringComparison.Ordinal) || uri.IndexOfAny(['?', '#', '\\']) >= 0) return false;
        var path = uri[(scheme + 3)..^suffix.Length];
        if (path.Length == 0) return false;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..") return false;
        }

        root = uri[..^suffix.Length];
        name = path[(path.LastIndexOf('/') + 1)..];
        return true;
    }

    /// <summary>Whether a URI is a file under the directory of a skill, without a step out of it.</summary>
    public static bool IsUnder(string root, string uri)
    {
        if (!uri.StartsWith(root + "/", StringComparison.Ordinal) || uri.IndexOfAny(['?', '#', '\\']) >= 0) return false;
        foreach (var segment in uri[(root.Length + 1)..].Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..") return false;
        }

        return true;
    }

    /// <summary>Whether a name follows the naming rules of Agent Skills: 1 to 64 lowercase letters, digits and single inner hyphens.</summary>
    public static bool IsValidName(string name)
    {
        if (name.Length is 0 or > MaximumNameLength || name[0] == '-' || name[^1] == '-' || name.Contains("--", StringComparison.Ordinal)) return false;
        foreach (var character in name)
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-') return false;
        }

        return true;
    }

    /// <summary>Whether a digest is <c>sha256:</c> followed by 64 lowercase hexadecimal characters.</summary>
    public static bool IsValidDigest(string digest)
    {
        if (digest.Length != Sha256Prefix.Length + 64 || !digest.StartsWith(Sha256Prefix, StringComparison.Ordinal)) return false;
        foreach (var character in digest.AsSpan(Sha256Prefix.Length))
        {
            if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f')) return false;
        }

        return true;
    }

    private static bool TryReadSize(JsonValue value, out long size)
    {
        size = 0;
        if (value.GetValueKind() != JsonValueKind.Number) return false;
        // A number written with a fraction of zero (`175.0`) is still a whole number of bytes.
        if (!decimal.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
            || number < 0 || number > long.MaxValue || decimal.Truncate(number) != number)
        {
            return false;
        }

        size = (long)number;
        return true;
    }

    private static string? ReadString(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    internal static string Shorten(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
