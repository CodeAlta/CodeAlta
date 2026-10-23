using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeAlta.Plugin.Mcp;

/// <summary>
/// The MCP Skills extension (<c>io.modelcontextprotocol/skills</c>, SEP-2640 v1): a server publishes skills, each the
/// directory of a <c>SKILL.md</c> whose files are resources, listed with <c>skills/list</c> and confirmed with
/// <c>skills/get</c>; <c>resources/directory/read</c> lists a directory of a server that declares <c>directoryRead</c>.
/// </summary>
internal static class McpSkills
{
    /// <summary>The identifier of the extension, in the <c>extensions</c> capability of a server and of a client.</summary>
    public const string ExtensionId = "io.modelcontextprotocol/skills";

    /// <summary>The name of the file of a skill that its URI names.</summary>
    public const string SkillFileName = "SKILL.md";

    /// <summary>The most files a skill may have, <c>SKILL.md</c> included.</summary>
    public const int MaximumFiles = 512;

    /// <summary>The most bytes the files of a skill may have in all (16 MiB).</summary>
    public const long MaximumTotalBytes = 16L * 1024 * 1024;

    /// <summary>The most pages of <c>skills/list</c> read at a connection.</summary>
    public const int MaximumListPages = 20;

    /// <summary>The most skills of a server kept from its listing.</summary>
    public const int MaximumListedSkills = 2000;

    /// <summary>The most entries of a directory read with <c>resources/directory/read</c>.</summary>
    public const int MaximumDirectoryEntries = 1000;

    /// <summary>The capability a client that loads skills declares: the extension, with no setting.</summary>
    public static JsonObject CreateClientCapability() => [];
}

/// <summary>A file of a skill as its server describes it: its URI, the SHA-256 of its bytes and their number.</summary>
/// <param name="Uri">The URI of the resource.</param>
/// <param name="Digest">The digest, <c>sha256:</c> followed by 64 lowercase hexadecimal characters.</param>
/// <param name="Size">The number of bytes.</param>
internal sealed record McpSkillFile(string Uri, string Digest, long Size);

/// <summary>
/// A skill as its server describes it, checked by <see cref="McpSkillValidator"/>: the URI of its <c>SKILL.md</c>, its
/// frontmatter as JSON, and its files, or none for a <c>dynamic</c> skill whose content cannot be checked.
/// </summary>
/// <param name="Uri">The URI of its <c>SKILL.md</c>.</param>
/// <param name="Frontmatter">Its frontmatter, as the server renders it.</param>
/// <param name="Files">Every file of the skill, <c>SKILL.md</c> included; null for a <c>dynamic</c> skill.</param>
/// <param name="Meta">The <c>_meta</c> of the entry, or null.</param>
internal sealed record McpSkillEntry(string Uri, JsonObject Frontmatter, IReadOnlyList<McpSkillFile>? Files, JsonObject? Meta = null)
{
    /// <summary>Gets the name of the skill, the last segment of its path.</summary>
    public string Name => Frontmatter["name"]?.GetValue<string>() ?? string.Empty;

    /// <summary>Gets what the skill is for.</summary>
    public string Description => Frontmatter["description"]?.GetValue<string>() ?? string.Empty;

    /// <summary>Gets whether the server publishes no digest for the skill: its content is generated.</summary>
    public bool IsDynamic => Files is null;

    /// <summary>Gets the URI of the directory of the skill, without a trailing slash.</summary>
    public string RootUri => Uri[..^(McpSkills.SkillFileName.Length + 1)];

    /// <summary>Gets the number of bytes of the files of the skill; 0 for a <c>dynamic</c> skill.</summary>
    public long TotalSize => Files?.Sum(static file => file.Size) ?? 0;

    /// <summary>Gets the file of the skill at a URI, or null when the skill does not list it.</summary>
    public McpSkillFile? FindFile(string uri) => Files?.FirstOrDefault(file => string.Equals(file.Uri, uri, StringComparison.Ordinal));

    /// <summary>Gets the path of a file under the directory of the skill (<c>templates/invoice.md</c>).</summary>
    public string RelativePathOf(string uri) => uri.StartsWith(RootUri + "/", StringComparison.Ordinal) ? uri[(RootUri.Length + 1)..] : uri;

    /// <summary>
    /// Gets the hash of the files of the skill, every URI and digest, which an approval is bound to: any file changed,
    /// added or removed changes it. Null for a <c>dynamic</c> skill, which nothing can be bound to.
    /// </summary>
    public string? ManifestHash
    {
        get
        {
            if (Files is null) return null;
            var text = new StringBuilder();
            foreach (var file in Files.OrderBy(static file => file.Uri, StringComparer.Ordinal))
            {
                text.Append(file.Uri).Append('\0').Append(file.Digest).Append('\n');
            }

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }
    }
}

/// <summary>A direct child of a directory resource, from <c>resources/directory/read</c>.</summary>
/// <param name="Uri">Its URI.</param>
/// <param name="Name">Its name.</param>
/// <param name="IsDirectory">Whether it is a directory (<c>inode/directory</c>).</param>
internal sealed record McpDirectoryChild(string Uri, string Name, bool IsDirectory);

/// <summary>A page of <c>skills/list</c>: its entries as the server sent them, and the cursor of the next page.</summary>
internal sealed record McpSkillListPage(IReadOnlyList<System.Text.Json.Nodes.JsonNode?> Entries, string? NextCursor);
