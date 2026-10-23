using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodeAlta.Plugin.Mcp;

/// <summary>
/// The requests of the MCP Skills extension, sent as raw JSON-RPC requests on a client of the SDK, which stamps the
/// request <c>_meta</c> the negotiated revision requires on them as on its own methods.
/// </summary>
internal static class McpSkillsClient
{
    /// <summary>Gets whether a server declares the extension, and whether it reads directories; null when it does not declare it.</summary>
    /// <param name="client">The client of the server.</param>
    /// <returns>Whether the server implements <c>resources/directory/read</c>, or null when it publishes no skill.</returns>
    public static bool? ReadCapability(McpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.ServerCapabilities.Extensions is not { } extensions || !extensions.TryGetValue(McpSkills.ExtensionId, out var setting))
        {
            return null;
        }

        var element = setting is JsonElement json ? json : JsonSerializer.SerializeToElement(setting, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object)));
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty("directoryRead", out var directoryRead) && directoryRead.ValueKind == JsonValueKind.True;
    }

    /// <summary>Reads a page of <c>skills/list</c>.</summary>
    /// <exception cref="McpException">The server answered with an error.</exception>
    /// <exception cref="InvalidDataException">The answer is not a listing.</exception>
    public static async Task<McpSkillListPage> ListSkillsAsync(McpClient client, string? cursor, CancellationToken cancellationToken)
    {
        var parameters = new JsonObject();
        if (cursor is not null) parameters["cursor"] = cursor;
        var result = await SendAsync(client, "skills/list", parameters, cancellationToken).ConfigureAwait(false);
        if (result["skills"] is not JsonArray skills) throw new InvalidDataException("The answer of `skills/list` has no `skills` list.");
        var next = result["nextCursor"] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
        return new McpSkillListPage([.. skills.Select(static skill => skill?.DeepClone())], string.IsNullOrEmpty(next) ? null : next);
    }

    /// <summary>Reads the entry of one skill with <c>skills/get</c>.</summary>
    /// <returns>The entry as the server sent it.</returns>
    /// <exception cref="McpException">The server answered with an error (<c>-32602</c> for a skill it does not serve).</exception>
    /// <exception cref="InvalidDataException">The answer has no entry.</exception>
    public static async Task<JsonNode> GetSkillAsync(McpClient client, string uri, CancellationToken cancellationToken)
    {
        var result = await SendAsync(client, "skills/get", new JsonObject { ["uri"] = uri }, cancellationToken).ConfigureAwait(false);
        return result["skill"]?.DeepClone() ?? throw new InvalidDataException("The answer of `skills/get` has no `skill`.");
    }

    /// <summary>Reads the direct children of a directory with <c>resources/directory/read</c>, page after page.</summary>
    /// <exception cref="McpException">The server answered with an error (<c>-32602</c> for what is not a directory).</exception>
    /// <exception cref="InvalidDataException">The answer is not a listing.</exception>
    public static async Task<IReadOnlyList<McpDirectoryChild>> ReadDirectoryAsync(McpClient client, string uri, CancellationToken cancellationToken)
    {
        var children = new List<McpDirectoryChild>();
        string? cursor = null;
        for (var page = 0; page < McpSkills.MaximumListPages && children.Count < McpSkills.MaximumDirectoryEntries; page++)
        {
            var parameters = new JsonObject { ["uri"] = uri };
            if (cursor is not null) parameters["cursor"] = cursor;
            var result = await SendAsync(client, "resources/directory/read", parameters, cancellationToken).ConfigureAwait(false);
            if (result["resources"] is not JsonArray resources) throw new InvalidDataException("The answer of `resources/directory/read` has no `resources` list.");
            foreach (var resource in resources.OfType<JsonObject>())
            {
                if (resource["uri"] is not JsonValue childUri || childUri.GetValueKind() != JsonValueKind.String) continue;
                var name = resource["name"] is JsonValue childName && childName.GetValueKind() == JsonValueKind.String ? childName.GetValue<string>() : childUri.GetValue<string>();
                var mimeType = resource["mimeType"] is JsonValue mime && mime.GetValueKind() == JsonValueKind.String ? mime.GetValue<string>() : null;
                children.Add(new McpDirectoryChild(childUri.GetValue<string>(), name, string.Equals(mimeType, "inode/directory", StringComparison.OrdinalIgnoreCase)));
                if (children.Count >= McpSkills.MaximumDirectoryEntries) break;
            }

            cursor = result["nextCursor"] is JsonValue next && next.GetValueKind() == JsonValueKind.String ? next.GetValue<string>() : null;
            if (string.IsNullOrEmpty(cursor)) break;
        }

        return children;
    }

    private static async Task<JsonObject> SendAsync(McpClient client, string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var response = await client.SendRequestAsync(new JsonRpcRequest { Method = method, Params = parameters }, cancellationToken).ConfigureAwait(false);
        return response.Result as JsonObject ?? throw new InvalidDataException($"The answer of `{method}` is not an object.");
    }
}
