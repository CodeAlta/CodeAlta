using System.Text.Json;

namespace CodeAlta.Plugin.Mcp;

/// <summary>Who asks for the skills of the MCP servers: a project, a space, and the session whose connections are used.</summary>
/// <param name="ProjectDirectory">The project whose configuration applies, or null for the global one.</param>
/// <param name="SpaceId">The space whose servers are listed, or null for every server (the user).</param>
/// <param name="SessionId">The session whose connections read a skill, or null for a connection of its own.</param>
public sealed record McpSkillsRequest(string? ProjectDirectory, string? SpaceId, string? SessionId = null);

/// <summary>The skills of one MCP server, as CodeAlta last listed them.</summary>
/// <param name="Server">The key of the server.</param>
/// <param name="ListedAt">When CodeAlta listed them.</param>
/// <param name="Skills">The skills.</param>
/// <param name="Problems">Why entries of the listing were left out, or why it stopped.</param>
public sealed record McpServerSkills(string Server, DateTimeOffset ListedAt, IReadOnlyList<McpSkillInfo> Skills, IReadOnlyList<string> Problems);

/// <summary>A skill an MCP server publishes.</summary>
/// <param name="Server">The key of the server.</param>
/// <param name="Name">Its name.</param>
/// <param name="Description">What it is for, as the server writes it.</param>
/// <param name="Uri">The URI of its <c>SKILL.md</c>.</param>
/// <param name="Frontmatter">Its frontmatter, as the server renders it (a JSON object).</param>
/// <param name="Files">Its files, or null when its server generates them.</param>
/// <param name="Enabled">Whether the user offers it to the sessions: the MCP policy of its server does not turn it off.</param>
/// <param name="Approval">Whether the user allowed it from now on.</param>
public sealed record McpSkillInfo(string Server, string Name, string Description, string Uri, JsonElement Frontmatter, IReadOnlyList<McpSkillFileInfo>? Files,
    bool Enabled, McpSkillApprovalState Approval);

/// <summary>A file of a skill of an MCP server.</summary>
/// <param name="Path">Its path under the folder of the skill.</param>
/// <param name="Size">Its number of bytes.</param>
public sealed record McpSkillFileInfo(string Path, long Size);

/// <summary>Whether the user allowed a skill of an MCP server from now on.</summary>
public enum McpSkillApprovalState
{
    /// <summary>Not allowed: a session asks the user before it loads it.</summary>
    NotApproved,

    /// <summary>Allowed from now on, with the files it has now.</summary>
    Approved,

    /// <summary>Allowed with other files than the ones it has now: a session asks the user again.</summary>
    Changed,

    /// <summary>Its server generates its files: it can only be allowed once, at each load.</summary>
    OnceOnly,
}

/// <summary>The <c>SKILL.md</c> of a skill read for the user, or why it could not be.</summary>
/// <param name="Text">The text, or null when it could not be read.</param>
/// <param name="Verified">Whether it was checked against the digest and the frontmatter of the entry of the skill.</param>
/// <param name="Error">What went wrong, or null.</param>
public sealed record McpSkillContent(string? Text, bool Verified, string? Error);

public sealed partial class McpPlugin
{
    /// <summary>
    /// Lists the skills of the enabled MCP servers as CodeAlta last listed them, with the ones the user turned off, without
    /// starting any server: a server never listed has none yet.
    /// </summary>
    /// <param name="request">The project and the space.</param>
    /// <param name="cancellationToken">A token to cancel the listing.</param>
    /// <returns>The skills by server, in the order of the servers; empty for a host that does not load skills.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The listing was canceled.</exception>
    public async Task<IReadOnlyList<McpServerSkills>> ListSkillsAsync(McpSkillsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_skills is null) return [];
        var projectDirectory = string.IsNullOrWhiteSpace(request.ProjectDirectory) ? null : request.ProjectDirectory;
        var runtimeRequest = new McpRuntimeRequest { ProjectDirectory = projectDirectory, UserHomeDirectory = _userHomeDirectory, SpaceId = request.SpaceId };
        IReadOnlyList<McpCachedSkillList> lists;
        await using (var runtime = _connections.CreateRuntime(_userHomeDirectory))
        {
            lists = runtime.ListCachedSkills(runtimeRequest);
        }

        var policy = LoadPolicy(projectDirectory);
        var definitions = _configCache.Discover(projectDirectory, _userHomeDirectory).EffectiveServers.ToDictionary(static server => server.Definition.Key, static server => server.Definition, StringComparer.Ordinal);
        return [.. lists.Select(list =>
        {
            var fingerprint = definitions.TryGetValue(list.Server, out var definition) ? McpToolListCache.Fingerprint(definition) : list.Fingerprint;
            return new McpServerSkills(list.Server, list.ListedAt, [.. list.Skills.OrderBy(static skill => skill.Uri, StringComparer.Ordinal).Select(skill => new McpSkillInfo(
                list.Server, skill.Name, skill.Description, skill.Uri, McpApps.AsElement(skill.Frontmatter)!.Value,
                skill.Files?.Select(file => new McpSkillFileInfo(skill.RelativePathOf(file.Uri), file.Size)).ToArray(),
                !policy.IsSkillDisabled(list.Server, skill.Uri, skill.Name),
                ApprovalOf(list.Server, fingerprint, skill)))], list.Diagnostics);
        })];
    }

    /// <summary>
    /// Reads the <c>SKILL.md</c> of a skill of an MCP server for the user to read before it is loaded: the entry of now,
    /// then the file, checked against it. Nothing is loaded and nothing is approved.
    /// </summary>
    /// <param name="request">The project, the space, and the session whose connections are used, if any.</param>
    /// <param name="server">The key of the server.</param>
    /// <param name="uri">The URI of the <c>SKILL.md</c>.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The text, or why it could not be read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="server"/> or <paramref name="uri"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async Task<McpSkillContent> ReadSkillContentAsync(McpSkillsRequest request, string server, string uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        if (_skills is not { } skills) return new McpSkillContent(null, false, "This host does not load the skills of MCP servers.");
        if (!McpSkillValidator.TrySplitUri(uri, out _, out _)) return new McpSkillContent(null, false, "This is not the URI of the `SKILL.md` of a skill.");
        var runtimeRequest = new McpRuntimeRequest
        {
            // The project the session's runs gave: the connection its activation used.
            ProjectDirectory = (request.SessionId is { Length: > 0 } session ? _activationState.GetProjectDirectory(McpActivationState.ResolveScopeKey(session, null)) : null)
                ?? request.ProjectDirectory,
            UserHomeDirectory = _userHomeDirectory,
            SpaceId = request.SpaceId,
            Environment = await SessionEnvironmentAsync(request.SessionId, cancellationToken).ConfigureAwait(false),
        };
        var diagnostics = new List<McpRuntimeDiagnostic>();
        async Task<McpSkillContent?> ReadAsync(McpRuntimeService runtime, IList<McpRuntimeDiagnostic> found)
        {
            var entry = await runtime.GetSkillEntryAsync(runtimeRequest, server.Trim(), uri, found, cancellationToken).ConfigureAwait(false);
            if (entry is null) return null;
            var read = entry.FindFile(uri) is { } file
                ? await McpSkillReader.ReadFileAsync(runtime, runtimeRequest, server.Trim(), file, skills.Contents, cancellationToken).ConfigureAwait(false)
                : await McpSkillReader.ReadUncheckedAsync(runtime, runtimeRequest, server.Trim(), uri, cancellationToken).ConfigureAwait(false);
            if (read.Bytes is not { } bytes)
            {
                foreach (var diagnostic in read.Diagnostics) found.Add(diagnostic);
                return read.Mismatch is { } mismatch ? new McpSkillContent(null, false, mismatch) : null;
            }

            return McpSkillReader.ReadSkillFile(entry, bytes, out var text) is { } error
                ? new McpSkillContent(null, false, error)
                : new McpSkillContent(text, !entry.IsDynamic, null);
        }

        McpSkillContent? content;
        try
        {
            if (request.SessionId is { Length: > 0 } sessionId)
            {
                content = await _connections.UseForViewAsync(sessionId, runtimeRequest, ReadAsync, diagnostics, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var runtime = _connections.CreateRuntime(_userHomeDirectory);
                content = await ReadAsync(runtime, diagnostics).ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
            return new McpSkillContent(null, false, "The MCP plugin was stopped.");
        }

        return content ?? new McpSkillContent(null, false, diagnostics.LastOrDefault()?.Message ?? "The MCP server did not answer.");
    }

    /// <summary>Forgets the approval the user kept for a skill of an MCP server: a session asks again before it loads it.</summary>
    /// <param name="server">The key of the server.</param>
    /// <param name="uri">The URI of the <c>SKILL.md</c>.</param>
    /// <returns>Whether an approval was kept.</returns>
    /// <exception cref="ArgumentException"><paramref name="server"/> or <paramref name="uri"/> is empty.</exception>
    /// <exception cref="IOException">The file of the approvals cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file of the approvals cannot be written.</exception>
    public bool ForgetSkillApproval(string server, string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        return _skills?.Approvals.Forget(server.Trim(), uri.Trim()) ?? false;
    }

    private McpSkillApprovalState ApprovalOf(string server, string fingerprint, McpSkillEntry skill)
    {
        if (skill.IsDynamic) return McpSkillApprovalState.OnceOnly;
        var approval = _skills!.Approvals.Find(server, fingerprint, skill.Uri);
        return approval is null ? McpSkillApprovalState.NotApproved
            : approval.ManifestHash == skill.ManifestHash ? McpSkillApprovalState.Approved : McpSkillApprovalState.Changed;
    }
}
