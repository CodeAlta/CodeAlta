using System.Globalization;
using System.Text.Json;
using CodeAlta.Catalog.Skills;
using CodeAlta.Plugin.Mcp;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// The skills MCP servers publish, beside the local ones: read-only, each with a switch in the MCP policy of its server,
// its approval, and its `SKILL.md` read on demand, checked against its entry.
internal sealed partial class SkillsService
{
    private const int MaximumMcpServers = 256;
    private const int MaximumMcpUriLength = 1024;
    private const int MaximumMcpFiles = 512;
    private const int MaximumMcpFields = 64;
    private const int MaximumMcpProblems = 32;

    /// <summary>Gets what finds the running MCP plugin, or null where the host has none.</summary>
    internal Func<McpPlugin?>? Mcp { get; init; }

    /// <summary>Gets the home whose MCP policy the switches write; null for the user's.</summary>
    internal string? McpHome { get; init; }

    /// <summary>
    /// Lists the skills of the MCP servers as CodeAlta last listed them, with the ones turned off, without starting any
    /// server. A skill named like a local skill says so: it never takes its place.
    /// </summary>
    [NeoRpcMethod("listMcp")]
    public async Task<SkillsMcpListResponse> ListMcpAsync(SkillsListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SkillsMcpListResponse Failed(string status) => new(status, request.ProjectId, []);
        if (_projects is null || _management is null) return Failed("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Failed("stale_epoch");
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return Failed(project.Status);
        // A host whose MCP plugin does not run, or does not load skills, has none to show.
        if (Mcp?.Invoke() is not { LoadsSkills: true } plugin) return new("ok", request.ProjectId, []);
        try
        {
            var servers = await plugin.ListSkillsAsync(new McpSkillsRequest(project.Root, null), cancellationToken).ConfigureAwait(false);
            var local = (await _management.LoadListingAsync(SkillListingScope.Combined, project.Root, cancellationToken).ConfigureAwait(false)).Skills
                .Select(static skill => skill.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var remaining = MaximumSkills;
            var entries = new List<SkillsMcpServer>();
            foreach (var server in servers.Take(MaximumMcpServers))
            {
                var skills = server.Skills.Take(remaining).Select(skill => new SkillsMcpEntry(
                    Bound(skill.Server, MaximumNameLength), Bound(skill.Name, MaximumNameLength), Bound(skill.Description, MaximumDescriptionLength),
                    Bound(skill.Uri, MaximumMcpUriLength), skill.Enabled, local.Contains(skill.Name), ApprovalOf(skill.Approval), skill.Files is null,
                    skill.Files?.Count ?? 0, (int)Math.Min(int.MaxValue, skill.Files?.Sum(static file => file.Size) ?? 0),
                    [.. (skill.Files ?? []).Take(MaximumMcpFiles).Select(static file => new SkillsMcpFile(Bound(file.Path, MaximumPathLength), (int)Math.Min(int.MaxValue, file.Size)))],
                    FieldsOf(skill.Frontmatter))).ToArray();
                remaining -= skills.Length;
                entries.Add(new SkillsMcpServer(Bound(server.Server, MaximumNameLength), server.ListedAt.ToString("O", CultureInfo.InvariantCulture), skills,
                    [.. server.Problems.Take(MaximumMcpProblems).Select(Clean)]));
            }

            return new("ok", request.ProjectId, entries);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log(exception, "The skills of the MCP servers could not be listed.");
            return Failed("read_failed");
        }
    }

    /// <summary>Turns a skill of an MCP server on or off in the MCP policy of its server, in the global or project configuration.</summary>
    [NeoRpcMethod("setMcpEnabled")]
    public async Task<SkillsMutationResponse> SetMcpEnabledAsync(SkillsMcpSetEnabledRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_projects is null || _management is null) return new("unavailable", 0, null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", 0, null);
        if (!TryScope(request.Scope, out var projectScope)) return new("invalid", 0, ScopeRule);
        if (!ValidMcpSkill(request.Server, request.Uri)) return new("invalid", 0, "A server and the URI of a skill are required.");
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return new(project.Status, 0, null);
        if (projectScope && project.Root is null) return new("invalid", 0, ProjectRequired);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await new McpManagementService().SetSkillEnabledAsync(request.Server!, request.Uri!, request.Enabled,
                projectScope ? McpManagementScope.Project : McpManagementScope.Global,
                new McpManagementRequest { ProjectDirectory = project.Root, UserHomeDirectory = McpHome, ProbeWritability = false }, CancellationToken.None).ConfigureAwait(false);
            return new("ok", result.Changed ? 1 : 0, null);
        }
        catch (InvalidDataException)
        {
            return new("config_invalid", 0, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log(exception, "The switch of a skill of an MCP server could not be saved.");
            return new("write_failed", 0, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads the <c>SKILL.md</c> of a skill of an MCP server for the user to read before a session loads it: its entry of
    /// now and the file, checked against it. The server is started when it does not run. Nothing is loaded or approved.
    /// </summary>
    [NeoRpcMethod("mcpContent", TimeoutMilliseconds = 120_000)]
    public async Task<SkillsMcpContentResponse> McpContentAsync(SkillsMcpSkillRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SkillsMcpContentResponse Failed(string status, string? message = null) => new(status, null, false, false, message);
        if (_projects is null || _management is null) return Failed("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Failed("stale_epoch");
        if (!ValidMcpSkill(request.Server, request.Uri)) return Failed("invalid");
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return Failed(project.Status);
        if (Mcp?.Invoke() is not { LoadsSkills: true } plugin) return Failed("unavailable");
        try
        {
            var content = await plugin.ReadSkillContentAsync(new McpSkillsRequest(project.Root, null), request.Server!, request.Uri!, cancellationToken).ConfigureAwait(false);
            if (content.Text is not { } text) return Failed("read_failed", Clean(content.Error ?? "The skill could not be read."));
            var truncated = text.Length > MaximumContentLength;
            return new("ok", truncated ? text[..(char.IsHighSurrogate(text[MaximumContentLength - 1]) ? MaximumContentLength - 1 : MaximumContentLength)] : text,
                content.Verified, truncated, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log(exception, "The skill of an MCP server could not be read.");
            return Failed("read_failed");
        }
    }

    /// <summary>Forgets the approval the user kept for a skill of an MCP server: a session asks again before it loads it.</summary>
    [NeoRpcMethod("forgetMcpApproval")]
    public async Task<SkillsMutationResponse> ForgetMcpApprovalAsync(SkillsMcpSkillRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_projects is null || _management is null) return new("unavailable", 0, null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", 0, null);
        if (!ValidMcpSkill(request.Server, request.Uri)) return new("invalid", 0, null);
        if (Mcp?.Invoke() is not { LoadsSkills: true } plugin) return new("unavailable", 0, null);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new("ok", plugin.ForgetSkillApproval(request.Server!, request.Uri!) ? 1 : 0, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log(exception, "The approval of a skill of an MCP server could not be forgotten.");
            return new("write_failed", 0, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool ValidMcpSkill(string? server, string? uri)
        => server is { Length: > 0 and <= MaximumNameLength } && server.Trim() == server && !server.Any(char.IsControl)
            && uri is { Length: > 0 and <= MaximumMcpUriLength } && uri.Contains("://", StringComparison.Ordinal)
            && uri.EndsWith("/SKILL.md", StringComparison.Ordinal) && !uri.Any(char.IsControl);

    private static string ApprovalOf(McpSkillApprovalState state) => state switch
    {
        McpSkillApprovalState.Approved => "approved",
        McpSkillApprovalState.Changed => "changed",
        McpSkillApprovalState.OnceOnly => "once_only",
        _ => "not_approved",
    };

    // The fields of a frontmatter as text: a string as it is, any other value as its JSON. `allowed-tools` is shown, and
    // said to be ignored: CodeAlta never acts on the tools a skill of a server asks for.
    private static IReadOnlyList<SkillsMcpField> FieldsOf(JsonElement frontmatter)
        => frontmatter.ValueKind != JsonValueKind.Object ? []
            : [.. frontmatter.EnumerateObject().Take(MaximumMcpFields).Select(static field => new SkillsMcpField(Bound(field.Name, MaximumNameLength),
                Bound(field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() ?? string.Empty : field.Value.GetRawText(), MaximumDescriptionLength),
                string.Equals(field.Name, "allowed-tools", StringComparison.Ordinal)))];
}

/// <summary>The skills of the MCP servers, by server, as CodeAlta last listed them.</summary>
internal sealed record SkillsMcpListResponse(string Status, string? ProjectId, IReadOnlyList<SkillsMcpServer> Servers);

/// <summary>The skills of one MCP server: when they were listed, and why entries of its listing were left out.</summary>
internal sealed record SkillsMcpServer(string Server, string ListedAt, IReadOnlyList<SkillsMcpEntry> Skills, IReadOnlyList<string> Problems);

/// <summary>
/// A skill of an MCP server. <c>Approval</c> is <c>approved</c>, <c>not_approved</c>, <c>changed</c> (approved with other
/// files) or <c>once_only</c> (its server generates its files); <c>NamedLikeLocal</c> says a local skill has its name.
/// </summary>
internal sealed record SkillsMcpEntry(string Server, string Name, string Description, string Uri, bool Enabled, bool NamedLikeLocal, string Approval,
    bool Dynamic, int FileCount, int TotalBytes, IReadOnlyList<SkillsMcpFile> Files, IReadOnlyList<SkillsMcpField> Frontmatter);

/// <summary>A file of a skill of an MCP server.</summary>
internal sealed record SkillsMcpFile(string Path, int Size);

/// <summary>A field of the frontmatter of a skill of an MCP server; <c>Ignored</c> for one CodeAlta never acts on.</summary>
internal sealed record SkillsMcpField(string Name, string Value, bool Ignored);

/// <summary>Names a skill of an MCP server by its server and URI.</summary>
internal sealed record SkillsMcpSkillRequest(string? ExpectedEpoch, string? ProjectId, string? Server, string? Uri);

/// <summary>Turns a skill of an MCP server on or off in the Global or Project policy.</summary>
internal sealed record SkillsMcpSetEnabledRequest(string? ExpectedEpoch, string? ProjectId, string? Scope, string? Server, string? Uri, bool Enabled);

/// <summary>The <c>SKILL.md</c> of a skill of an MCP server, cut at 64 Ki characters; <c>Verified</c> when it was checked against its entry.</summary>
internal sealed record SkillsMcpContentResponse(string Status, string? Content, bool Verified, bool Truncated, string? Message);
