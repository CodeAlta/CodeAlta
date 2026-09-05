using System.Text;
using System.Text.Json;

namespace CodeAlta.Catalog;

/// <summary>Editable file-local prompt values, not an assembled runtime prompt.</summary>
/// <param name="Name">Agent display name; absent append metadata inherits at composition time.</param>
/// <param name="Description">Optional agent description.</param>
/// <param name="SystemPromptName">Optional system id; absence inherits or defaults to default.</param>
/// <param name="Body">Markdown body.</param>
/// <param name="Append">Whether the resource appends to lower-precedence resources.</param>
public sealed record PromptFileContent(string? Name, string? Description, string? SystemPromptName, string Body, bool Append);

/// <summary>Shared flat prompt frontmatter parsing and management serialization.</summary>
public static class PromptFileFormat
{
    /// <summary>Splits the existing flat frontmatter dialect, tolerating legacy unclosed headers.</summary>
    /// <exception cref="ArgumentNullException">Text is null.</exception>
    public static (Dictionary<string, string> Frontmatter, string Body) SplitFrontmatter(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return (values, text);
        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) return (values, text);
        foreach (var rawLine in normalized[4..end].Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line == "---") continue;
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            values[line[..colon].Trim()] = DecodeScalar(line[(colon + 1)..].Trim());
        }
        return (values, normalized[(end + 5)..]);
    }

    /// <summary>Reads mode/append aliases; null denotes invalid or contradictory metadata.</summary>
    /// <exception cref="ArgumentNullException">Frontmatter is null.</exception>
    public static bool? ParseAppend(IReadOnlyDictionary<string, string> frontmatter)
    {
        ArgumentNullException.ThrowIfNull(frontmatter);
        bool? appendFlag = null;
        if (frontmatter.TryGetValue("append", out var appendValue))
        {
            if (!bool.TryParse(appendValue, out var append)) return null;
            appendFlag = append;
        }
        bool? mode = null;
        if (frontmatter.TryGetValue("mode", out var modeValue))
        {
            mode = Normalize(modeValue)?.ToLowerInvariant() switch { null or "replace" => false, "append" => true, _ => null };
            if (mode is null) return null;
        }
        if (appendFlag is not null && mode is not null && appendFlag != mode) return null;
        return mode ?? appendFlag ?? false;
    }

    /// <summary>Parses one file's values without applying inheritance or defaults.</summary>
    /// <exception cref="ArgumentException">Kind, mode or required fields are invalid.</exception>
    /// <exception cref="ArgumentNullException">Text is null.</exception>
    public static PromptFileContent Parse(PromptResourceKind kind, string text)
    {
        var (metadata, body) = SplitFrontmatter(text);
        var append = ParseAppend(metadata) ?? throw new ArgumentException("Invalid prompt composition mode.", nameof(text));
        var content = new PromptFileContent(Normalize(metadata.GetValueOrDefault("name")), Normalize(metadata.GetValueOrDefault("description")), Normalize(metadata.GetValueOrDefault("system")), body.Trim(), append);
        Validate(kind, content);
        return content;
    }

    /// <summary>Validates editable values while preserving absent append metadata.</summary>
    /// <exception cref="ArgumentException">Kind, system id or required fields are invalid.</exception>
    /// <exception cref="ArgumentNullException">Content is null.</exception>
    public static void Validate(PromptResourceKind kind, PromptFileContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!Enum.IsDefined(kind)) throw new ArgumentException("Invalid prompt kind.", nameof(kind));
        if (string.IsNullOrWhiteSpace(content.Body)) throw new ArgumentException("Prompt body is required.", nameof(content));
        if (kind == PromptResourceKind.Agent)
        {
            if (!content.Append && string.IsNullOrWhiteSpace(content.Name)) throw new ArgumentException("Prompt name is required.", nameof(content));
            if (Normalize(content.SystemPromptName) is { } system) PromptResourceStore.ValidateId(system);
        }
    }

    /// <summary>Serializes the management format (replace system files remain plain Markdown).</summary>
    /// <exception cref="ArgumentException">Kind, system id or required fields are invalid.</exception>
    /// <exception cref="ArgumentNullException">Content is null.</exception>
    public static string Serialize(PromptResourceKind kind, PromptFileContent content)
    {
        Validate(kind, content);
        if (kind == PromptResourceKind.System && !content.Append)
        {
            // Replace-mode system files are plain Markdown. A body containing a leading
            // closed header would be reinterpreted as metadata on the next load.
            var body = content.Body.Trim();
            if (!string.Equals(SplitFrontmatter(body + Environment.NewLine).Body.Trim(), body, StringComparison.Ordinal))
                throw new ArgumentException("A replace-mode system prompt body cannot begin with prompt frontmatter.", nameof(content));
        }
        var builder = new StringBuilder();
        if (kind == PromptResourceKind.Agent || content.Append)
        {
            builder.AppendLine("---");
            if (kind == PromptResourceKind.Agent)
            {
                AddScalar(builder, "name", content.Name);
                AddScalar(builder, "description", content.Description);
                // Explicit default on append must not accidentally inherit a different lower system id.
                if (content.Append || !string.Equals(Normalize(content.SystemPromptName), "default", StringComparison.OrdinalIgnoreCase))
                    AddScalar(builder, "system", content.SystemPromptName);
            }
            if (content.Append) builder.AppendLine("mode: append");
            builder.AppendLine("---");
        }
        builder.AppendLine(content.Body.Trim());
        return builder.ToString();
    }

    private static void AddScalar(StringBuilder builder, string key, string? value)
    {
        if (Normalize(value) is { } normalized)
            builder.Append(key).Append(": \"").Append(JsonEncodedText.Encode(normalized).ToString()).AppendLine("\"");
    }

    /// <summary>Decodes quoted prompt scalars, including escapes emitted by management; tolerates legacy flat values.</summary>
    /// <exception cref="ArgumentNullException">Value is null.</exception>
    public static string DecodeScalar(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.StartsWith('"') && value.EndsWith('"'))
        {
            try { using var json = JsonDocument.Parse(value); return json.RootElement.GetString()!; }
            catch (JsonException) { /* Preserve the legacy permissive flat scalar dialect. */ }
        }
        return value.Trim('"', '\'');
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
