using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodeAlta.Plugin.Mcp;

// The skills of the servers (the MCP Skills extension), for a host that loads them: listed at each connection and kept
// on disk, and read one file at a time, on the server that publishes them only.
internal sealed partial class McpRuntimeService
{
    /// <summary>
    /// Lists the skills of a server that declares the extension, page after page, within the limits of a listing, and keeps
    /// the entries that pass the checks (<see cref="McpSkillListCache"/>). No file of a skill is read. A listing that fails
    /// leaves the server connected: its skills are then the ones of its last listing.
    /// </summary>
    private async Task<McpCachedSkillList?> ListServerSkillsAsync(
        RuntimeContext context, McpServerDefinition definition, McpClient client, string? variant, CancellationToken timeoutToken, CancellationToken cancellationToken)
    {
        if (McpSkillsClient.ReadCapability(client) is not { } directoryRead)
        {
            return null;
        }

        var skills = new List<McpSkillEntry>();
        var diagnostics = new List<string>();
        string? cursor = null;
        var complete = false;
        try
        {
            for (var page = 0; page < McpSkills.MaximumListPages; page++)
            {
                var listed = await McpSkillsClient.ListSkillsAsync(client, cursor, timeoutToken).ConfigureAwait(false);
                foreach (var node in listed.Entries)
                {
                    if (skills.Count >= McpSkills.MaximumListedSkills) break;
                    if (McpSkillValidator.TryParse(node, out var entry, out var error))
                    {
                        if (skills.Any(skill => string.Equals(skill.Uri, entry!.Uri, StringComparison.Ordinal))) diagnostics.Add($"The skill `{McpSkillValidator.Shorten(entry!.Uri)}` is listed twice.");
                        else skills.Add(entry!);
                    }
                    else
                    {
                        diagnostics.Add(error!);
                    }
                }

                cursor = listed.NextCursor;
                if (cursor is null || skills.Count >= McpSkills.MaximumListedSkills)
                {
                    complete = cursor is null;
                    break;
                }
            }

            if (!complete) diagnostics.Add($"The listing has more than {McpSkills.MaximumListPages} pages or {McpSkills.MaximumListedSkills} skills: the rest are found with `skills/get`.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            diagnostics.Add("The listing of the skills did not finish within the startup timeout of the server.");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && (IsRuntimeException(exception) || exception is InvalidDataException))
        {
            diagnostics.Add("The listing of the skills failed: " + McpRedactor.RedactValue(null, exception.GetBaseException().Message));
        }

        var list = new McpCachedSkillList(definition.Key, McpToolListCache.Fingerprint(definition), DateTimeOffset.UtcNow, directoryRead, skills, diagnostics) { Variant = variant };
        try
        {
            new McpSkillListCache(McpSkillListCache.FolderOf(context.Request.UserHomeDirectory)).Write(list);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* The cache is written at the next connection. */ }

        return list;
    }

    /// <summary>
    /// Lists the skills of the enabled servers of the space as CodeAlta last listed them with their definition of now,
    /// without starting any, in the order of the servers. A server never listed, or that publishes no skill, has none.
    /// </summary>
    public IReadOnlyList<McpCachedSkillList> ListCachedSkills(McpRuntimeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = LoadContext(request);
        var cache = new McpSkillListCache(McpSkillListCache.FolderOf(context.Request.UserHomeDirectory));
        var lists = new List<McpCachedSkillList>();
        if (!context.Policy.Enabled) return lists;
        foreach (var effective in context.Config.EffectiveServers.OrderBy(static server => server.Definition.Key, StringComparer.Ordinal))
        {
            var key = effective.Definition.Key;
            if (!context.Policy.IsServerInSpace(key, context.Request.SpaceId) || !IsServerEnabled(context.Policy, key, out _)) continue;
            if (cache.Read(key, McpToolListCache.Fingerprint(effective.Definition)) is { } list) lists.Add(list);
        }

        return lists;
    }

    /// <summary>
    /// Lists the skills of the servers, or of one: from what CodeAlta last listed, or, with <paramref name="refresh"/>, from
    /// the servers themselves, which are started for it.
    /// </summary>
    public async Task<(IReadOnlyList<McpCachedSkillList> Lists, IReadOnlyList<McpRuntimeDiagnostic> Diagnostics)> ListSkillsAsync(
        McpRuntimeRequest request, string? serverFilter, bool refresh, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = LoadContext(request);
        var diagnostics = new List<McpRuntimeDiagnostic>();
        var matched = context.Config.EffectiveServers.Where(server => MatchesServer(server.Definition.Key, serverFilter)).OrderBy(static server => server.Definition.Key, StringComparer.Ordinal).ToArray();
        if (matched.Length == 0 && !string.IsNullOrWhiteSpace(serverFilter))
        {
            diagnostics.Add(new McpRuntimeDiagnostic { Code = "server_not_found", Server = serverFilter.Trim(), Message = $"MCP server '{serverFilter.Trim()}' is not configured in the effective MCP configuration." });
            return ([], diagnostics);
        }

        if (!refresh)
        {
            var cached = ListCachedSkills(request).Where(list => MatchesServer(list.Server, serverFilter)).ToArray();
            // A server named that CodeAlta never listed is listed now: the user asked for its skills.
            if (cached.Length > 0 || string.IsNullOrWhiteSpace(serverFilter)) return (cached, diagnostics);
        }

        var lists = new List<McpCachedSkillList>();
        foreach (var effective in matched)
        {
            await ReviseKeptStateAsync(context, effective).ConfigureAwait(false);
            // A connection kept from before answers with the listing of its start: a refresh asks again.
            if (refresh) await ForgetServerAsync(context, effective).ConfigureAwait(false);
            var state = await GetServerStateAsync(context, effective, cancellationToken).ConfigureAwait(false);
            if (state.Diagnostic is not null)
            {
                diagnostics.Add(state.Diagnostic);
                continue;
            }

            if (state.Skills is { } list) lists.Add(list);
            else if (!string.IsNullOrWhiteSpace(serverFilter)) diagnostics.Add(NoSkills(effective.Definition.Key));
        }

        return (lists, diagnostics);
    }

    /// <summary>Reads the entry of a skill with <c>skills/get</c>, checked; null with a diagnostic when it cannot be had.</summary>
    public async Task<McpSkillEntry?> GetSkillEntryAsync(McpRuntimeRequest request, string serverKey, string uri, IList<McpRuntimeDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (await ResolveSkillServerAsync(request, serverKey, diagnostics, cancellationToken).ConfigureAwait(false) is not { } state) return null;
        try
        {
            var node = await McpSkillsClient.GetSkillAsync(state.Client!, uri, cancellationToken).ConfigureAwait(false);
            if (McpSkillValidator.TryParse(node, out var entry, out var error))
            {
                if (string.Equals(entry!.Uri, uri, StringComparison.Ordinal)) return entry;
                error = $"The server answered `skills/get` for `{McpSkillValidator.Shorten(uri)}` with another skill.";
            }

            diagnostics.Add(new McpRuntimeDiagnostic { Code = "skill_invalid", Server = serverKey, Message = error! });
            return null;
        }
        catch (McpProtocolException exception) when (exception.ErrorCode == McpErrorCode.InvalidParams)
        {
            diagnostics.Add(new McpRuntimeDiagnostic { Code = "skill_not_found", Server = serverKey, Message = $"The MCP server '{serverKey}' serves no skill at `{McpSkillValidator.Shorten(uri)}`." });
            return null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && (IsRuntimeException(exception) || exception is InvalidDataException))
        {
            diagnostics.Add(new McpRuntimeDiagnostic { Code = "skill_get_failed", Server = serverKey, Message = McpRedactor.RedactValue(null, exception.GetBaseException().Message) });
            return null;
        }
    }

    /// <summary>Reads a resource of a server that publishes skills: the answer as it sent it, which the caller checks.</summary>
    public async Task<ReadResourceResult?> ReadSkillResourceAsync(McpRuntimeRequest request, string serverKey, string uri, IList<McpRuntimeDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (await ResolveSkillServerAsync(request, serverKey, diagnostics, cancellationToken).ConfigureAwait(false) is null) return null;
        return (await ReadResourceAsync(request, serverKey, uri, diagnostics, cancellationToken).ConfigureAwait(false))?.Result;
    }

    /// <summary>Lists a directory of a server that declares <c>directoryRead</c>; null with a diagnostic otherwise.</summary>
    public async Task<IReadOnlyList<McpDirectoryChild>?> ReadSkillDirectoryAsync(McpRuntimeRequest request, string serverKey, string uri, IList<McpRuntimeDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (await ResolveSkillServerAsync(request, serverKey, diagnostics, cancellationToken).ConfigureAwait(false) is not { } state) return null;
        if (McpSkillsClient.ReadCapability(state.Client!) != true)
        {
            diagnostics.Add(new McpRuntimeDiagnostic { Code = "directory_read_unsupported", Server = serverKey, Message = $"The MCP server '{serverKey}' does not list its directories." });
            return null;
        }

        try
        {
            return await McpSkillsClient.ReadDirectoryAsync(state.Client!, uri, cancellationToken).ConfigureAwait(false);
        }
        catch (McpProtocolException exception) when (exception.ErrorCode == McpErrorCode.InvalidParams)
        {
            diagnostics.Add(new McpRuntimeDiagnostic { Code = "directory_not_found", Server = serverKey, Message = $"`{McpSkillValidator.Shorten(uri)}` is not a directory of the MCP server '{serverKey}'." });
            return null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && (IsRuntimeException(exception) || exception is InvalidDataException))
        {
            diagnostics.Add(new McpRuntimeDiagnostic { Code = "directory_read_failed", Server = serverKey, Message = McpRedactor.RedactValue(null, exception.GetBaseException().Message) });
            return null;
        }
    }

    // The connection of a configured server that publishes skills, with the checks of any request (space, policy).
    private async Task<ServerRuntimeState?> ResolveSkillServerAsync(McpRuntimeRequest request, string serverKey, IList<McpRuntimeDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var context = LoadContext(request);
        var effective = context.Config.EffectiveServers.SingleOrDefault(server => string.Equals(server.Definition.Key, serverKey, StringComparison.Ordinal));
        if (effective is null)
        {
            diagnostics.Add(new McpRuntimeDiagnostic { Code = "server_not_found", Server = serverKey, Message = $"MCP server '{serverKey}' is not configured in the effective MCP configuration." });
            return null;
        }

        var state = await GetServerStateAsync(context, effective, cancellationToken).ConfigureAwait(false);
        if (state.Diagnostic is not null)
        {
            diagnostics.Add(state.Diagnostic);
            return null;
        }

        if (McpSkillsClient.ReadCapability(state.Client!) is null)
        {
            diagnostics.Add(NoSkills(serverKey));
            return null;
        }

        return state;
    }

    // Closes the kept connection of a server, so that the next request starts it again and lists afresh.
    private async ValueTask ForgetServerAsync(RuntimeContext context, McpEffectiveServer effective)
    {
        var cacheKey = CacheKeyOf(context, effective, out _);
        if (!_servers.Remove(cacheKey, out var kept) || kept.Client is not { } client) return;
        await CloseClientAsync(client).ConfigureAwait(false);
    }

    private static McpRuntimeDiagnostic NoSkills(string serverKey) => new()
    {
        Code = "skills_not_supported",
        Server = serverKey,
        Message = $"The MCP server '{serverKey}' does not publish skills (the {McpSkills.ExtensionId} extension).",
    };
}
