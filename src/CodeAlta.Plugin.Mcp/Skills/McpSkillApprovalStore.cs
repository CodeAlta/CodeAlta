using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Plugin.Mcp;

/// <summary>An approval the user kept for a skill of an MCP server ("Allow this skill").</summary>
/// <param name="Server">The key of the server.</param>
/// <param name="Fingerprint">The fingerprint of the definition of the server when it was approved: another definition is another server.</param>
/// <param name="Uri">The URI of the <c>SKILL.md</c> of the skill.</param>
/// <param name="ManifestHash">The hash of every URI and digest of its files (<see cref="McpSkillEntry.ManifestHash"/>).</param>
/// <param name="ApprovedAt">When.</param>
internal sealed record McpSkillApproval(string Server, string Fingerprint, string Uri, string ManifestHash, DateTimeOffset ApprovedAt);

/// <summary>The file of the approvals.</summary>
internal sealed record McpSkillApprovalFile(int Version, IReadOnlyList<McpSkillApproval> Approvals);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(McpSkillApprovalFile))]
internal sealed partial class McpSkillApprovalJsonContext : JsonSerializerContext;

/// <summary>
/// The skills of MCP servers the user allowed from now on, in <c>~/.alta/plugin-data/mcp/skill-approvals.json</c>. An
/// approval is keyed by the server, the fingerprint of its definition and the URI of the skill, and bound to the hash of
/// the files the user approved: a file changed, added or removed revokes it. A <c>dynamic</c> skill is never kept.
/// </summary>
internal sealed class McpSkillApprovalStore
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private const int CurrentVersion = 1;
    private readonly Lock _gate = new();

    /// <summary>Creates the store of a file.</summary>
    public McpSkillApprovalStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    /// <summary>Gets the file of the approvals.</summary>
    public string Path { get; }

    /// <summary>Gets the file of the approvals of a profile.</summary>
    public static string PathOf(string? userHomeDirectory)
        => System.IO.Path.Combine(userHomeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alta", "plugin-data", "mcp", "skill-approvals.json");

    /// <summary>Whether the user allowed this skill of this server, with these files, from now on.</summary>
    public bool IsApproved(string server, string fingerprint, string uri, string? manifestHash)
        => manifestHash is not null && Find(server, fingerprint, uri) is { } approval && string.Equals(approval.ManifestHash, manifestHash, StringComparison.Ordinal);

    /// <summary>Gets the approval kept for a skill of a server with this definition, whatever its files, or null.</summary>
    public McpSkillApproval? Find(string server, string fingerprint, string uri)
    {
        lock (_gate)
        {
            return Read().FirstOrDefault(approval => Matches(approval, server, uri) && string.Equals(approval.Fingerprint, fingerprint, StringComparison.Ordinal));
        }
    }

    /// <summary>Keeps the approval of a skill with these files; it replaces the one kept before for the skill.</summary>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written.</exception>
    public void Approve(string server, string fingerprint, string uri, string manifestHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(manifestHash);
        lock (_gate)
        {
            var approvals = Read().Where(approval => !(Matches(approval, server, uri) && approval.Fingerprint == fingerprint)).ToList();
            approvals.Add(new McpSkillApproval(server, fingerprint, uri, manifestHash, DateTimeOffset.UtcNow));
            Write(approvals);
        }
    }

    /// <summary>Forgets every approval of a skill of a server.</summary>
    /// <returns>Whether one was kept.</returns>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written.</exception>
    public bool Forget(string server, string uri)
    {
        lock (_gate)
        {
            var approvals = Read();
            var kept = approvals.Where(approval => !Matches(approval, server, uri)).ToList();
            if (kept.Count == approvals.Count) return false;
            Write(kept);
            return true;
        }
    }

    private static bool Matches(McpSkillApproval approval, string server, string uri)
        => string.Equals(approval.Server, server, StringComparison.Ordinal) && string.Equals(approval.Uri, uri, StringComparison.Ordinal);

    // A file that cannot be read keeps no approval: the user is asked again.
    private IReadOnlyList<McpSkillApproval> Read()
    {
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists || info.Length > MaximumBytes) return [];
            var file = JsonSerializer.Deserialize(File.ReadAllBytes(Path), McpSkillApprovalJsonContext.Default.McpSkillApprovalFile);
            return file is { Version: CurrentVersion } ? file.Approvals ?? [] : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return [];
        }
    }

    private void Write(IReadOnlyList<McpSkillApproval> approvals)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporary = Path + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new McpSkillApprovalFile(CurrentVersion, approvals), McpSkillApprovalJsonContext.Default.McpSkillApprovalFile));
        File.Move(temporary, Path, overwrite: true);
    }
}
