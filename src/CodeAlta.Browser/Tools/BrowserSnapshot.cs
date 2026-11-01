using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.Browser.Drivers;

namespace CodeAlta.Browser.Tools;

/// <summary>A node of a page snapshot: an element or a text of the page with its accessibility role and name.</summary>
internal sealed class BrowserSnapshotNode
{
    public BrowserSnapshotNode(string uid, string role, string name, IReadOnlyDictionary<string, object> properties, List<BrowserSnapshotNode> children)
    {
        Uid = uid;
        Role = role;
        Name = name;
        Properties = properties;
        Children = children;
    }

    /// <summary>Gets the id that input tools take. It stays with the element across snapshots of a document.</summary>
    public string Uid { get; }

    /// <summary>Gets the role, with the names Chrome DevTools uses.</summary>
    public string Role { get; }

    /// <summary>Gets the accessible name, or an empty string.</summary>
    public string Name { get; }

    /// <summary>Gets the other properties: a value is a <see cref="bool"/>, a <see cref="double"/> or a <see cref="string"/>.</summary>
    public IReadOnlyDictionary<string, object> Properties { get; }

    /// <summary>Gets the child nodes.</summary>
    public List<BrowserSnapshotNode> Children { get; }
}

/// <summary>
/// A text snapshot of a page from the accessibility tree its in-page agent computes, the frames of other origins read
/// by their own agent and joined under their frame element, formatted as Chrome DevTools MCP formats its snapshots.
/// </summary>
internal sealed class BrowserSnapshot
{
    private static readonly FrozenDictionary<string, string> BooleanStateNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["disabled"] = "disableable",
        ["expanded"] = "expandable",
        ["focused"] = "focusable",
        ["selected"] = "selectable",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly Dictionary<string, BrowserFrame> _frames;

    public BrowserSnapshot(BrowserSnapshotNode root, bool verbose, bool truncated, Dictionary<string, BrowserFrame> frames)
    {
        Root = root;
        IsVerbose = verbose;
        IsTruncated = truncated;
        _frames = frames;
    }

    /// <summary>Gets the root node, which stands for the document.</summary>
    public BrowserSnapshotNode Root { get; }

    /// <summary>Gets whether the snapshot lists every node.</summary>
    public bool IsVerbose { get; }

    /// <summary>Gets whether the page had more nodes than a snapshot holds.</summary>
    public bool IsTruncated { get; }

    /// <summary>The frame whose agent knows a uid, or null when the uid is not in this snapshot.</summary>
    public BrowserFrame? FrameOf(string uid) => _frames.GetValueOrDefault(uid);

    /// <summary>Reads what the agent of one frame gives, and records which frame each of its uids belongs to.</summary>
    /// <exception cref="BrowserToolException">The tree is nested too deeply to be a page.</exception>
    public static (BrowserSnapshotNode Root, bool Truncated) Parse(JsonElement value, BrowserFrame frame, Dictionary<string, BrowserFrame> frames)
    {
        var truncated = value.TryGetProperty("truncated", out var flag) && flag.ValueKind == JsonValueKind.True;
        return (ParseNode(value.GetProperty("root"), 0, frame, frames), truncated);
    }

    private static BrowserSnapshotNode ParseNode(JsonElement value, int depth, BrowserFrame frame, Dictionary<string, BrowserFrame> frames)
    {
        // The agent bounds the number of nodes; this bounds the depth of a hostile or degenerate tree.
        if (depth > 512) throw new BrowserToolException("snapshot-too-deep", "The page is nested too deeply to be captured in a snapshot.");
        var uid = value.GetProperty("i").GetString() ?? string.Empty;
        frames[uid] = frame;
        var role = value.TryGetProperty("r", out var roleValue) ? roleValue.GetString() ?? string.Empty : string.Empty;
        var name = value.TryGetProperty("n", out var nameValue) ? nameValue.GetString() ?? string.Empty : string.Empty;
        IReadOnlyDictionary<string, object> properties = EmptyProperties;
        if (value.TryGetProperty("p", out var propertyValues) && propertyValues.ValueKind == JsonValueKind.Object)
        {
            var parsed = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var property in propertyValues.EnumerateObject())
            {
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.True: parsed[property.Name] = true; break;
                    case JsonValueKind.False: parsed[property.Name] = false; break;
                    case JsonValueKind.Number: parsed[property.Name] = property.Value.GetDouble(); break;
                    case JsonValueKind.String: parsed[property.Name] = property.Value.GetString() ?? string.Empty; break;
                }
            }

            properties = parsed;
        }

        var children = new List<BrowserSnapshotNode>();
        if (value.TryGetProperty("c", out var childValues) && childValues.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in childValues.EnumerateArray()) children.Add(ParseNode(child, depth + 1, frame, frames));
        }

        return new BrowserSnapshotNode(uid, role, name, properties, children);
    }

    private static IReadOnlyDictionary<string, object> EmptyProperties { get; } = FrozenDictionary<string, object>.Empty;

    /// <summary>Finds a node of a tree by its uid.</summary>
    public static BrowserSnapshotNode? Find(BrowserSnapshotNode node, string uid)
    {
        if (node.Uid == uid) return node;
        foreach (var child in node.Children)
        {
            if (Find(child, uid) is { } found) return found;
        }

        return null;
    }

    /// <summary>Formats the snapshot as indented text, one node per line, in the layout of Chrome DevTools MCP.</summary>
    public override string ToString()
    {
        var builder = new StringBuilder();
        Append(builder, Root, 0);
        return builder.ToString();
    }

    private void Append(StringBuilder builder, BrowserSnapshotNode node, int depth)
    {
        builder.Append(' ', depth * 2).Append("uid=").Append(node.Uid);
        if (node.Role.Length != 0 && node.Role != "StaticText") builder.Append(' ').Append(node.Role == "none" ? "ignored" : node.Role);
        if (node.Name.Length != 0) builder.Append(" \"").Append(node.Name).Append('"');

        var isOption = node.Role == "option";
        foreach (var name in node.Properties.Keys.Order(StringComparer.Ordinal))
        {
            var value = node.Properties[name];
            // A state that can change is announced by its "-able" name, then by the state itself when it is set.
            if (value is bool && BooleanStateNames.TryGetValue(name, out var stateName) && !(isOption && name == "selected")) builder.Append(' ').Append(stateName);
            switch (value)
            {
                case true:
                    builder.Append(' ').Append(name);
                    break;
                case string text when !(isOption && name == "value" && text == node.Name):
                    builder.Append(' ').Append(name).Append("=\"").Append(text).Append('"');
                    break;
                case double number:
                    builder.Append(' ').Append(name).Append("=\"").Append(number.ToString(CultureInfo.InvariantCulture)).Append('"');
                    break;
            }
        }

        builder.Append('\n');
        if (HasRedundantTextChildren(node)) return;
        foreach (var child in node.Children) Append(builder, child, depth + 1);
    }

    // Text children that only repeat the name of their parent add nothing to a compact snapshot.
    private bool HasRedundantTextChildren(BrowserSnapshotNode node)
    {
        if (IsVerbose || node.Name.Length == 0 || node.Children.Count == 0) return false;
        var text = new StringBuilder();
        foreach (var child in node.Children)
        {
            if (child.Role != "StaticText" || child.Children.Count != 0) return false;
            text.Append(child.Name);
        }

        return StripWhitespace(text.ToString()) == StripWhitespace(node.Name);
    }

    private static string StripWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character)) builder.Append(character);
        }

        return builder.ToString();
    }
}
