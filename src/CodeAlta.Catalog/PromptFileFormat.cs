using System.Text;
using System.Text.Json;

namespace CodeAlta.Catalog;

/// <summary>Editable file-local prompt values, not an assembled runtime prompt.</summary>
/// <param name="Name">Agent display name; absent append metadata inherits at composition time.</param>
/// <param name="Description">Optional agent description.</param>
/// <param name="SystemPromptName">Optional system id; absence inherits or defaults to default.</param>
/// <param name="Body">Markdown body.</param>
/// <param name="Append">Whether the resource appends to lower-precedence resources.</param>
public sealed record PromptFileContent(string? Name, string? Description, string? SystemPromptName, string Body, bool Append)
{
    /// <summary>Gets the built-in tools a session of the agent prompt does without (<c>disabled_tools</c>); empty for none.</summary>
    public IReadOnlyList<string> DisabledTools { get; init; } = [];

    /// <summary>
    /// Gets the ids of the agent prompts this one is made of (<c>inherit</c>), in their order: their text comes first,
    /// then the text of this file. Empty for none.
    /// </summary>
    public IReadOnlyList<string> Inherits { get; init; } = [];
}

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
        var list = new BlockList();
        foreach (var rawLine in normalized[4..end].Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line == "---") continue;
            if (list.TryAdd(line)) continue;
            list.Close(values);
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            values[key] = DecodeScalar(value);
            if (value.Length == 0) list.Open(key);
        }
        list.Close(values);
        return (values, normalized[(end + 5)..]);
    }

    /// <summary>
    /// The items of a YAML block list (<c>- item</c> lines below a key without a value), which a flat front matter
    /// keeps as the flow list <c>[a, b]</c>: <c>inherit</c> and <c>disabled_tools</c> are written either way.
    /// </summary>
    internal sealed class BlockList
    {
        private string? _key;
        private readonly List<string> _items = [];

        /// <summary>Starts a list below a key written without a value.</summary>
        public void Open(string key)
        {
            _key = key;
            _items.Clear();
        }

        /// <summary>Adds a <c>- item</c> line to the open list; false for another line or without an open list.</summary>
        public bool TryAdd(string line)
        {
            if (_key is null || line.Length < 2 || line[0] != '-' || !char.IsWhiteSpace(line[1])) return false;
            _items.Add(DecodeScalar(line[2..].Trim()));
            return true;
        }

        /// <summary>Writes the open list, when it has items, as a flow list of its key.</summary>
        public void Close(Dictionary<string, string> values)
        {
            if (_key is not null && _items.Count > 0) values[_key] = "[" + string.Join(", ", _items) + "]";
            _key = null;
            _items.Clear();
        }
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
        var content = new PromptFileContent(Normalize(metadata.GetValueOrDefault("name")), Normalize(metadata.GetValueOrDefault("description")), Normalize(metadata.GetValueOrDefault("system")), body.Trim(), append)
        {
            DisabledTools = kind == PromptResourceKind.Agent ? ParseDisabledTools(metadata) : [],
            Inherits = kind == PromptResourceKind.Agent ? ParseInherits(metadata) : [],
        };
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
            if (content.DisabledTools.FirstOrDefault(static name => !SpaceTools.IsToolName(name)) is { } wrong)
                throw new ArgumentException($"'{wrong}' is not the name of a tool.", nameof(content));
            if (content.Inherits.Count > MaximumInherits)
                throw new ArgumentException($"An agent prompt inherits at most {MaximumInherits} prompts.", nameof(content));
            if (content.Inherits.FirstOrDefault(static id => !IsPromptId(id)) is { } invalid)
                throw new ArgumentException($"'{invalid}' is not the id of an agent prompt.", nameof(content));
        }
    }

    /// <summary>Largest number of agent prompts one agent prompt inherits (<c>inherit</c>).</summary>
    public const int MaximumInherits = 16;

    /// <summary>
    /// Reads the agent prompts an agent prompt is made of, <c>inherit</c> of its front matter: a flow list
    /// (<c>[default, maui]</c>), a block list (<c>- default</c> lines below <c>inherit:</c>) or ids separated by commas.
    /// An id that cannot name a prompt file is left out, and each id is kept once.
    /// </summary>
    /// <param name="frontmatter">The front matter.</param>
    /// <returns>The ids in their order; empty when none is set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frontmatter"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> ParseInherits(IReadOnlyDictionary<string, string> frontmatter)
    {
        ArgumentNullException.ThrowIfNull(frontmatter);
        if (!frontmatter.TryGetValue("inherit", out var value) || string.IsNullOrWhiteSpace(value)) return [];
        var list = value.Trim();
        if (list.StartsWith('[') && list.EndsWith(']')) list = list[1..^1];
        var ids = new List<string>();
        foreach (var part in list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var id = DecodeScalar(part).Trim();
            if (IsPromptId(id) && !ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id);
        }

        return ids;
    }

    private static bool IsPromptId(string id)
    {
        if (id is not { Length: > 0 and <= 128 }) return false;
        try
        {
            PromptResourceStore.ValidateId(id);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Serializes the management format (replace system files without metadata remain plain Markdown).</summary>
    /// <exception cref="ArgumentException">Kind, system id or required fields are invalid.</exception>
    /// <exception cref="ArgumentNullException">Content is null.</exception>
    public static string Serialize(PromptResourceKind kind, PromptFileContent content)
    {
        Validate(kind, content);
        var hasSystemMetadata = kind == PromptResourceKind.System && (Normalize(content.Name) is not null || Normalize(content.Description) is not null);
        if (kind == PromptResourceKind.System && !content.Append && !hasSystemMetadata)
        {
            // Replace-mode system files are plain Markdown. A body containing a leading
            // closed header would be reinterpreted as metadata on the next load.
            var body = content.Body.Trim();
            if (!string.Equals(SplitFrontmatter(body + Environment.NewLine).Body.Trim(), body, StringComparison.Ordinal))
                throw new ArgumentException("A replace-mode system prompt body cannot begin with prompt frontmatter.", nameof(content));
        }
        var builder = new StringBuilder();
        if (kind == PromptResourceKind.Agent || content.Append || hasSystemMetadata)
        {
            builder.AppendLine("---");
            AddScalar(builder, "name", content.Name);
            AddScalar(builder, "description", content.Description);
            if (kind == PromptResourceKind.Agent)
            {
                // Explicit default on append must not accidentally inherit a different lower system id.
                if (content.Append || !string.Equals(Normalize(content.SystemPromptName), "default", StringComparison.OrdinalIgnoreCase))
                    AddScalar(builder, "system", content.SystemPromptName);
            }
            if (kind == PromptResourceKind.Agent && content.Inherits.Count > 0)
                builder.Append("inherit: [").Append(string.Join(", ", content.Inherits.Distinct(StringComparer.OrdinalIgnoreCase))).AppendLine("]");
            if (kind == PromptResourceKind.Agent && content.DisabledTools.Count > 0)
                builder.Append("disabled_tools: [").Append(string.Join(", ", content.DisabledTools.Distinct(StringComparer.Ordinal))).AppendLine("]");
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

    /// <summary>
    /// Reads the tools an agent prompt turns off, <c>disabled_tools</c> of its front matter: a flow list
    /// (<c>[shell_command, webget]</c>) or names separated by commas. A name that is not the name of a tool is left out.
    /// </summary>
    /// <param name="frontmatter">The front matter.</param>
    /// <returns>The names, each once, in their order; empty when none is set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frontmatter"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> ParseDisabledTools(IReadOnlyDictionary<string, string> frontmatter)
    {
        ArgumentNullException.ThrowIfNull(frontmatter);
        if (!frontmatter.TryGetValue("disabled_tools", out var value) || string.IsNullOrWhiteSpace(value)) return [];
        var list = value.Trim();
        if (list.StartsWith('[') && list.EndsWith(']')) list = list[1..^1];
        var names = new List<string>();
        foreach (var part in list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var name = DecodeScalar(part).Trim();
            if (SpaceTools.IsToolName(name) && !names.Contains(name, StringComparer.Ordinal)) names.Add(name);
        }

        return names;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
