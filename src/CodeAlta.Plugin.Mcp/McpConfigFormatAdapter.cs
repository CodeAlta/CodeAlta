using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAlta.Plugin.Mcp;

internal static class McpConfigFormatAdapter
{
    private const string McpServersRootKey = "mcpServers";
    private const string VscodeServersRootKey = "servers";

    public static McpConfigDocument CreateEmptyDocument()
    {
        var root = new JsonObject
        {
            [McpServersRootKey] = new JsonObject(),
        };
        return new McpConfigDocument
        {
            Root = root,
            Flavor = McpConfigFlavor.CodeAlta,
            RootKey = McpServersRootKey,
        };
    }

    public static McpConfigDocument ParseDocument(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
        if (node is not JsonObject root)
        {
            throw new InvalidDataException("MCP config root must be a JSON object.");
        }

        var hasMcpServers = root.TryGetPropertyValue(McpServersRootKey, out var mcpServersNode);
        var hasVscodeServers = root.TryGetPropertyValue(VscodeServersRootKey, out var vscodeServersNode);
        if (hasMcpServers && hasVscodeServers)
        {
            throw new InvalidDataException("MCP config is ambiguous because it contains both 'mcpServers' and 'servers' root keys.");
        }

        if (hasVscodeServers)
        {
            RequireServerMap(vscodeServersNode, VscodeServersRootKey);
            return new McpConfigDocument
            {
                Root = root,
                Flavor = McpConfigFlavor.Vscode,
                RootKey = VscodeServersRootKey,
            };
        }

        if (hasMcpServers)
        {
            var servers = RequireServerMap(mcpServersNode, McpServersRootKey);
            return new McpConfigDocument
            {
                Root = root,
                Flavor = DetectMcpServersFlavor(servers),
                RootKey = McpServersRootKey,
            };
        }

        throw new InvalidDataException("MCP config must contain a top-level 'mcpServers' or 'servers' object.");
    }

    public static IReadOnlyList<McpServerDefinition> ReadServers(McpConfigDocument document, McpConfigScope scope, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var servers = GetServerMap(document.Root, document.RootKey);
        var result = new List<McpServerDefinition>(servers.Count);
        foreach (var (key, value) in servers)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidDataException("MCP server keys must not be empty.");
            }

            if (value is not JsonObject server)
            {
                throw new InvalidDataException($"MCP server '{key}' must be a JSON object.");
            }

            result.Add(ReadServer(key, server, document.Flavor, scope, path));
        }

        return result;
    }

    public static JsonObject AddOrUpdateServer(McpConfigDocument document, McpServerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(definition);
        ValidateDefinition(definition);
        var servers = GetServerMap(document.Root, document.RootKey);
        var existing = servers.TryGetPropertyValue(definition.Key, out var existingNode) && existingNode is JsonObject existingObject
            ? (JsonObject)existingObject.DeepClone()
            : new JsonObject();
        servers[definition.Key] = RenderServer(existing, document.Flavor, definition);
        return document.Root;
    }

    public static bool RemoveServer(McpConfigDocument document, string key)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var servers = GetServerMap(document.Root, document.RootKey);
        return servers.Remove(key);
    }

    public static string Serialize(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    private static JsonObject RequireServerMap(JsonNode? node, string rootKey)
        => node as JsonObject ?? throw new InvalidDataException($"MCP config root '{rootKey}' must be a JSON object.");

    private static JsonObject GetServerMap(JsonObject root, string rootKey)
    {
        if (!root.TryGetPropertyValue(rootKey, out var node) || node is not JsonObject servers)
        {
            servers = new JsonObject();
            root[rootKey] = servers;
        }

        return servers;
    }

    private static McpConfigFlavor DetectMcpServersFlavor(JsonObject servers)
    {
        var hasTools = false;
        var hasExplicitStdioType = false;
        var hasUntypedUrl = false;
        foreach (var (_, value) in servers)
        {
            if (value is not JsonObject server)
            {
                continue;
            }

            hasTools |= server.ContainsKey("tools");
            var type = GetString(server, "type");
            var hasUrl = HasNonEmptyString(server, "url");
            hasExplicitStdioType |= string.Equals(type, "stdio", StringComparison.OrdinalIgnoreCase);
            hasUntypedUrl |= hasUrl && string.IsNullOrWhiteSpace(type);
        }

        if (hasTools)
        {
            return McpConfigFlavor.Copilot;
        }

        if (hasExplicitStdioType)
        {
            return McpConfigFlavor.Claude;
        }

        if (hasUntypedUrl)
        {
            return McpConfigFlavor.Intellij;
        }

        return McpConfigFlavor.CodeAlta;
    }

    private static McpServerDefinition ReadServer(string key, JsonObject server, McpConfigFlavor flavor, McpConfigScope scope, string path)
    {
        var command = GetString(server, "command");
        var url = GetString(server, "url");
        var hasCommand = !string.IsNullOrWhiteSpace(command);
        var hasUrl = !string.IsNullOrWhiteSpace(url);
        if (hasCommand && hasUrl)
        {
            throw new InvalidDataException($"MCP server '{key}' is invalid because it contains both 'command' and 'url'.");
        }

        if (!hasCommand && !hasUrl)
        {
            throw new InvalidDataException($"MCP server '{key}' is invalid because it contains neither 'command' nor 'url'.");
        }

        var type = GetString(server, "type");
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!IsSupportedType(type))
            {
                throw new InvalidDataException($"MCP server '{key}' uses unsupported transport type '{type}'.");
            }

            if (hasCommand && !string.Equals(type, "stdio", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"MCP server '{key}' mixes command transport with type '{type}'.");
            }

            if (hasUrl && string.Equals(type, "stdio", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"MCP server '{key}' mixes URL transport with type 'stdio'.");
            }
        }

        return new McpServerDefinition
        {
            Key = key,
            Transport = hasUrl ? McpTransportKind.Http : McpTransportKind.Stdio,
            SourceScope = scope,
            SourcePath = path,
            SourceFlavor = flavor,
            Command = command,
            Args = ReadStringArray(server, "args"),
            Cwd = GetString(server, "cwd"),
            Env = ReadStringDictionary(server, "env"),
            Url = url,
            Headers = ReadStringDictionary(server, "headers"),
        };
    }

    private static void ValidateDefinition(McpServerDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Key);
        var hasCommand = !string.IsNullOrWhiteSpace(definition.Command);
        var hasUrl = !string.IsNullOrWhiteSpace(definition.Url);
        if (hasCommand == hasUrl)
        {
            throw new ArgumentException("An MCP server definition must set exactly one of Command or Url.", nameof(definition));
        }

        if (definition.Transport == McpTransportKind.Stdio && !hasCommand)
        {
            throw new ArgumentException("A stdio MCP server definition requires Command.", nameof(definition));
        }

        if (definition.Transport == McpTransportKind.Http && !hasUrl)
        {
            throw new ArgumentException("An HTTP MCP server definition requires Url.", nameof(definition));
        }
    }

    private static JsonObject RenderServer(JsonObject node, McpConfigFlavor flavor, McpServerDefinition definition)
    {
        var originalType = GetString(node, "type");
        RemoveKnownTransportFields(node);
        if (definition.Transport == McpTransportKind.Stdio)
        {
            node["command"] = definition.Command;
            if (definition.Args.Count > 0)
            {
                node["args"] = ToJsonArray(definition.Args);
            }

            if (!string.IsNullOrWhiteSpace(definition.Cwd))
            {
                node["cwd"] = definition.Cwd;
            }

            if (definition.Env.Count > 0)
            {
                node["env"] = ToJsonObject(definition.Env);
            }

            if (flavor is McpConfigFlavor.Vscode or McpConfigFlavor.Claude)
            {
                node["type"] = "stdio";
            }
        }
        else
        {
            node["url"] = definition.Url;
            if (definition.Headers.Count > 0)
            {
                node["headers"] = ToJsonObject(definition.Headers);
            }

            if (flavor == McpConfigFlavor.Vscode)
            {
                node["type"] = string.Equals(originalType, "http", StringComparison.OrdinalIgnoreCase) ? "http" : "sse";
            }
            else if (flavor != McpConfigFlavor.Intellij)
            {
                node["type"] = "http";
            }
        }

        if (flavor == McpConfigFlavor.Copilot && !node.ContainsKey("tools"))
        {
            node["tools"] = new JsonArray("*");
        }

        return node;
    }

    private static void RemoveKnownTransportFields(JsonObject node)
    {
        node.Remove("command");
        node.Remove("args");
        node.Remove("cwd");
        node.Remove("env");
        node.Remove("url");
        node.Remove("headers");
        node.Remove("type");
    }

    private static bool IsSupportedType(string type)
        => string.Equals(type, "stdio", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(type, "http", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(type, "sse", StringComparison.OrdinalIgnoreCase);

    private static bool HasNonEmptyString(JsonObject obj, string propertyName)
        => !string.IsNullOrWhiteSpace(GetString(obj, propertyName));

    private static string? GetString(JsonObject obj, string propertyName)
        => obj.TryGetPropertyValue(propertyName, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return [];
        }

        if (node is not JsonArray array)
        {
            throw new InvalidDataException($"MCP field '{propertyName}' must be an array of strings.");
        }

        var values = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var text))
            {
                throw new InvalidDataException($"MCP field '{propertyName}' must be an array of strings.");
            }

            values.Add(text);
        }

        return values;
    }

    private static IReadOnlyDictionary<string, string> ReadStringDictionary(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (node is not JsonObject map)
        {
            throw new InvalidDataException($"MCP field '{propertyName}' must be an object with string values.");
        }

        var values = new Dictionary<string, string>(map.Count, StringComparer.Ordinal);
        foreach (var (key, valueNode) in map)
        {
            if (valueNode is not JsonValue value || !value.TryGetValue<string>(out var text))
            {
                throw new InvalidDataException($"MCP field '{propertyName}.{key}' must be a string.");
            }

            values[key] = text;
        }

        return values;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, string> values)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in values)
        {
            obj[key] = value;
        }

        return obj;
    }
}
