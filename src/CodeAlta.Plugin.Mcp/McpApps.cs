using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAlta.Plugin.Mcp;

/// <summary>
/// The MCP Apps extension (<c>io.modelcontextprotocol/ui</c>, spec 2026-01-26): a tool names a <c>ui://</c> resource,
/// an HTML view the host shows for its calls, and says whether the model, the view or both may call it.
/// </summary>
internal static class McpApps
{
    /// <summary>The identifier of the extension, in the <c>extensions</c> capability of a client.</summary>
    public const string ExtensionId = "io.modelcontextprotocol/ui";

    /// <summary>The MIME type of an HTML view.</summary>
    public const string HtmlMimeType = "text/html;profile=mcp-app";

    /// <summary>The scheme of the resource of a view.</summary>
    public const string ResourceScheme = "ui://";

    /// <summary>The audience of a tool the model may call.</summary>
    public const string ModelVisibility = "model";

    /// <summary>The audience of a tool the view of its server may call.</summary>
    public const string AppVisibility = "app";

    /// <summary>Gets the capability a host that shows views advertises.</summary>
    public static JsonObject CreateCapability() => new() { ["mimeTypes"] = new JsonArray(HtmlMimeType) };

    /// <summary>
    /// Reads the view and the audience of a tool from its <c>_meta</c>: <c>ui.resourceUri</c>, or the deprecated flat
    /// <c>ui/resourceUri</c>, and <c>ui.visibility</c>. A resource that is not a <c>ui://</c> one is not a view.
    /// </summary>
    public static (string? ResourceUri, IReadOnlyList<string>? Visibility) ReadToolMeta(JsonObject? meta)
    {
        if (meta is null) return (null, null);
        var ui = meta["ui"] as JsonObject;
        var uri = ReadString(ui?["resourceUri"]) ?? ReadString(meta["ui/resourceUri"]);
        if (uri is not null && !IsResourceUri(uri)) uri = null;
        IReadOnlyList<string>? visibility = null;
        if (ui?["visibility"] is JsonArray array)
        {
            visibility = [.. array.Select(ReadString).Where(static value => value is not null).Select(static value => value!)];
        }

        return (uri, visibility);
    }

    /// <summary>Whether a tool of this audience is offered to the model: when no audience is declared, or it names the model.</summary>
    public static bool IsVisibleToModel(IReadOnlyList<string>? visibility)
        => visibility is null || visibility.Contains(ModelVisibility, StringComparer.Ordinal);

    /// <summary>Whether a view of the same server may call a tool of this audience: when no audience is declared, or it names the app.</summary>
    public static bool IsVisibleToApp(IReadOnlyList<string>? visibility)
        => visibility is null || visibility.Contains(AppVisibility, StringComparer.Ordinal);

    /// <summary>Whether a URI names the resource of a view.</summary>
    public static bool IsResourceUri(string? uri)
        => uri is { Length: > 5 } && uri.StartsWith(ResourceScheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a MIME type is the one of an HTML view, whatever its case and spaces.</summary>
    public static bool IsHtmlMimeType(string? mimeType)
        => mimeType is not null && string.Equals(mimeType.Replace(" ", string.Empty, StringComparison.Ordinal), HtmlMimeType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the security and display metadata of a view (<c>_meta.ui</c> of a resource).</summary>
    public static McpAppResourceMeta ReadResourceMeta(JsonObject? ui)
    {
        if (ui is null) return McpAppResourceMeta.Empty;
        var csp = ui["csp"] as JsonObject;
        return new McpAppResourceMeta
        {
            ConnectDomains = ReadStrings(csp?["connectDomains"]),
            ResourceDomains = ReadStrings(csp?["resourceDomains"]),
            FrameDomains = ReadStrings(csp?["frameDomains"]),
            BaseUriDomains = ReadStrings(csp?["baseUriDomains"]),
            PrefersBorder = ui["prefersBorder"] is JsonValue border && border.TryGetValue<bool>(out var value) ? value : null,
        };
    }

    /// <summary>Writes a JSON node as an element, or null for none.</summary>
    public static JsonElement? AsElement(JsonNode? node)
    {
        if (node is null) return null;
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>Parses a JSON element as an object node, or null when it is not an object.</summary>
    public static JsonObject? AsObject(JsonElement? element)
        => element is { ValueKind: JsonValueKind.Object } value ? JsonNode.Parse(value.GetRawText()) as JsonObject : null;

    private static string? ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static IReadOnlyList<string> ReadStrings(JsonNode? node)
        => node is JsonArray array ? [.. array.Select(ReadString).Where(static value => value is not null).Select(static value => value!)] : [];
}

/// <summary>What a view declares of its needs: the origins it may reach and whether it wants the host's border.</summary>
public sealed record McpAppResourceMeta
{
    /// <summary>Gets a view that declares nothing: it reaches no origin, and the host decides its border.</summary>
    public static McpAppResourceMeta Empty { get; } = new();

    /// <summary>Gets the origins of its network requests (<c>connect-src</c>).</summary>
    public IReadOnlyList<string> ConnectDomains { get; init; } = [];

    /// <summary>Gets the origins of its scripts, styles, images, fonts and media.</summary>
    public IReadOnlyList<string> ResourceDomains { get; init; } = [];

    /// <summary>Gets the origins of the frames it holds (<c>frame-src</c>).</summary>
    public IReadOnlyList<string> FrameDomains { get; init; } = [];

    /// <summary>Gets the origins its document may take as base (<c>base-uri</c>).</summary>
    public IReadOnlyList<string> BaseUriDomains { get; init; } = [];

    /// <summary>Gets whether it wants a border and a background from the host; null leaves it to the host.</summary>
    public bool? PrefersBorder { get; init; }
}
