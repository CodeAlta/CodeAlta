using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Plugin.Mcp;

/// <summary>The skills of one MCP server, as CodeAlta last listed them at a connection.</summary>
/// <param name="Server">The key of the server.</param>
/// <param name="Fingerprint">The fingerprint of its definition when it was listed (<see cref="McpToolListCache.Fingerprint(McpServerDefinition)"/>).</param>
/// <param name="ListedAt">When.</param>
/// <param name="DirectoryRead">Whether the server declared <c>resources/directory/read</c>.</param>
/// <param name="Skills">The entries that passed the checks.</param>
/// <param name="Diagnostics">Why entries were left out, or why the listing stopped.</param>
internal sealed record McpCachedSkillList(string Server, string Fingerprint, DateTimeOffset ListedAt, bool DirectoryRead, IReadOnlyList<McpSkillEntry> Skills, IReadOnlyList<string> Diagnostics)
{
    /// <summary>Gets the hash of what the server was started with, as for its tools; only a hash is kept.</summary>
    public string? Variant { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false, IgnoreReadOnlyProperties = true)]
[JsonSerializable(typeof(McpCachedSkillList))]
internal sealed partial class McpSkillListCacheJsonContext : JsonSerializerContext;

/// <summary>
/// The skill lists of the MCP servers, kept on disk each time CodeAlta connects to a server that publishes skills, so that
/// the prompt and Settings show them without starting the server. Only the entries are kept, never a file of a skill: the
/// folder (<c>~/.alta/cache/mcp/skills</c>) is outside every folder CodeAlta reads local skills from. A list whose
/// fingerprint is not the one of the definition now is stale and not used.
/// </summary>
internal sealed class McpSkillListCache
{
    private const int MaximumBytes = 16 * 1024 * 1024;

    /// <summary>Creates the cache of a folder.</summary>
    public McpSkillListCache(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        Folder = Path.GetFullPath(folder);
    }

    /// <summary>Gets the folder of the lists.</summary>
    public string Folder { get; }

    /// <summary>Gets the folder of the lists of a profile: <c>~/.alta/cache/mcp/skills</c>.</summary>
    public static string FolderOf(string? userHomeDirectory)
        => Path.Combine(userHomeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alta", "cache", "mcp", "skills");

    /// <summary>Reads the list of a server when it was listed with this fingerprint; null when there is none or it is stale.</summary>
    public McpCachedSkillList? Read(string server, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);
        var path = PathOf(server);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumBytes) return null;
            var list = JsonSerializer.Deserialize(File.ReadAllBytes(path), McpSkillListCacheJsonContext.Default.McpCachedSkillList);
            return list is not null && string.Equals(list.Server, server, StringComparison.Ordinal) && string.Equals(list.Fingerprint, fingerprint, StringComparison.Ordinal) ? list : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Writes the list of a server; a list with the same entries is not written again.</summary>
    public void Write(McpCachedSkillList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        var path = PathOf(list.Server);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(list, McpSkillListCacheJsonContext.Default.McpCachedSkillList);
        // A list with the same entries is not written again: its time would only change the prompt and wake the readers for nothing.
        if (Read(list.Server, list.Fingerprint) is { } previous && Same(previous, list)) return;
        Directory.CreateDirectory(Folder);
        var temporary = path + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Deletes the list of a server, when the user removes the server.</summary>
    public void Delete(string server)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);
        try
        {
            File.Delete(PathOf(server));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A list left behind is stale for any other definition of the server, and never read for a removed one.
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

    private static bool Same(McpCachedSkillList left, McpCachedSkillList right)
        => left.Variant == right.Variant && left.DirectoryRead == right.DirectoryRead && left.Diagnostics.SequenceEqual(right.Diagnostics, StringComparer.Ordinal)
            && JsonSerializer.Serialize(left.Skills, McpSkillListCacheJsonContext.Default.IReadOnlyListMcpSkillEntry)
                == JsonSerializer.Serialize(right.Skills, McpSkillListCacheJsonContext.Default.IReadOnlyListMcpSkillEntry);

    private string PathOf(string server)
    {
        var name = new StringBuilder(server.Length);
        foreach (var character in server) name.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_');
        // Two keys that differ only by characters left out still have files of their own.
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(server)))[..8];
        return Path.Combine(Folder, $"{name}-{hash}.json");
    }
}
