using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Plugin.Mcp;

/// <summary>An argument of a cached tool: its name and what it is for, never its schema.</summary>
/// <param name="Name">The name.</param>
/// <param name="Description">What it is for, or null.</param>
public sealed record McpCachedArgument(string Name, string? Description);

/// <summary>A tool of an MCP server as CodeAlta last listed it.</summary>
/// <param name="Name">Its name on the server.</param>
/// <param name="Alias">The name a session calls it by (<c>mcp__server__tool</c>).</param>
/// <param name="Title">Its title, or null.</param>
/// <param name="Description">What it does, or null.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="SchemaHash">The SHA-256 of its input schema, to tell when it changed.</param>
/// <param name="Enabled">Whether the policy lets a session call it.</param>
public sealed record McpCachedTool(string Name, string Alias, string? Title, string? Description, IReadOnlyList<McpCachedArgument> Arguments, string SchemaHash, bool Enabled);

/// <summary>The tools of one MCP server, as CodeAlta last listed them.</summary>
/// <param name="Server">The key of the server.</param>
/// <param name="Fingerprint">The fingerprint of its definition when it was listed (<see cref="McpToolListCache.Fingerprint(string?, IReadOnlyList{string}, string?, string?, IEnumerable{string})"/>).</param>
/// <param name="ListedAt">When.</param>
/// <param name="Tools">Its tools.</param>
public sealed record McpCachedToolList(string Server, string Fingerprint, DateTimeOffset ListedAt, IReadOnlyList<McpCachedTool> Tools);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(McpCachedToolList))]
internal sealed partial class McpToolListCacheJsonContext : JsonSerializerContext;

/// <summary>
/// The tool lists of the MCP servers, kept on disk each time CodeAlta lists the tools of a server (an activation, a
/// test, a search of tools, a run), so that search finds the tools of a server without starting it. A list is kept with
/// the fingerprint of the definition it was listed with, which leaves out the values of the variables and the headers:
/// a list whose fingerprint is not the one of the definition now is stale and not used. Nothing here starts a server.
/// </summary>
public sealed class McpToolListCache
{
    private const int MaximumBytes = 4 * 1024 * 1024;

    /// <summary>Creates the cache of a folder.</summary>
    /// <param name="folder">The folder of the lists.</param>
    public McpToolListCache(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        Folder = Path.GetFullPath(folder);
    }

    /// <summary>Gets the folder of the lists.</summary>
    public string Folder { get; }

    /// <summary>Gets the folder of the lists of a profile: <c>~/.alta/cache/mcp/tools</c>.</summary>
    /// <param name="userHomeDirectory">The folder of the user, or null for the profile of the process.</param>
    public static string FolderOf(string? userHomeDirectory)
        => Path.Combine(userHomeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alta", "cache", "mcp", "tools");

    /// <summary>
    /// Gets the fingerprint of the definition of a server: its command, its arguments, its folder, its address and the
    /// names of its variables. The values of the variables and the headers, which may be secrets, are left out.
    /// </summary>
    public static string Fingerprint(string? command, IReadOnlyList<string> args, string? cwd, string? url, IEnumerable<string> variableNames)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(variableNames);
        var text = string.Join('\0', command ?? string.Empty, string.Join('\u0001', args), cwd ?? string.Empty, url ?? string.Empty,
            string.Join('\u0001', variableNames.Order(StringComparer.Ordinal)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    internal static string Fingerprint(McpServerDefinition definition)
        => Fingerprint(definition.Command, definition.Args, definition.Cwd, definition.Url, definition.Env.Keys);

    /// <summary>Reads the list of a server when it was listed with this fingerprint.</summary>
    /// <returns>The list, or null when there is none or it is stale.</returns>
    public McpCachedToolList? Read(string server, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);
        var path = PathOf(server);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumBytes) return null;
            var list = JsonSerializer.Deserialize(File.ReadAllBytes(path), McpToolListCacheJsonContext.Default.McpCachedToolList);
            return list is not null && string.Equals(list.Server, server, StringComparison.Ordinal) && string.Equals(list.Fingerprint, fingerprint, StringComparison.Ordinal) ? list : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Gets the stamp of the folder: the names, sizes and times of its files, so that a reader knows when a list changed.</summary>
    public string Stamp()
    {
        try
        {
            if (!Directory.Exists(Folder)) return string.Empty;
            var stamp = new StringBuilder();
            foreach (var file in Directory.EnumerateFiles(Folder, "*.json").Order(StringComparer.Ordinal))
            {
                var info = new FileInfo(file);
                stamp.Append(info.Name).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append(';');
            }

            return stamp.ToString();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    internal void Write(McpServerDefinition definition, IReadOnlyList<McpRuntimeTool> tools)
    {
        var list = new McpCachedToolList(definition.Key, Fingerprint(definition), DateTimeOffset.UtcNow,
            [.. tools.Select(static tool => new McpCachedTool(tool.Name, tool.Alias, tool.Title, tool.Description, ArgumentsOf(tool.InputSchema),
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tool.InputSchema.GetRawText())))[..32], tool.Enabled))]);
        var path = PathOf(definition.Key);
        // A list with the same tools is not written again: its time would only wake the readers for nothing.
        if (Read(definition.Key, list.Fingerprint) is { } previous && previous.Tools.SequenceEqual(list.Tools, ToolComparer.Instance)) return;
        Directory.CreateDirectory(Folder);
        var temporary = path + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(list, McpToolListCacheJsonContext.Default.McpCachedToolList));
        File.Move(temporary, path, overwrite: true);
    }

    private string PathOf(string server)
    {
        var name = new StringBuilder(server.Length);
        foreach (var character in server) name.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_');
        // Two keys that differ only by characters left out still have files of their own.
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(server)))[..8];
        return Path.Combine(Folder, $"{name}-{hash}.json");
    }

    private static IReadOnlyList<McpCachedArgument> ArgumentsOf(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) return [];
        return [.. properties.EnumerateObject().Select(static property => new McpCachedArgument(property.Name,
            property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String
                ? description.GetString() : null))];
    }

    private sealed class ToolComparer : IEqualityComparer<McpCachedTool>
    {
        public static ToolComparer Instance { get; } = new();

        public bool Equals(McpCachedTool? left, McpCachedTool? right)
            => left is not null && right is not null && left.Name == right.Name && left.Alias == right.Alias && left.Title == right.Title
                && left.Description == right.Description && left.SchemaHash == right.SchemaHash && left.Enabled == right.Enabled
                && left.Arguments.SequenceEqual(right.Arguments);

        public int GetHashCode(McpCachedTool tool) => HashCode.Combine(tool.Name, tool.SchemaHash);
    }
}
