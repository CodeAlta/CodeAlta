using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.Plugin.Mcp;

/// <summary>What the skill commands of a plugin keep between calls: the approvals of the user and the files read.</summary>
/// <param name="Approvals">The skills the user allowed from now on.</param>
/// <param name="Contents">The files of skills read and checked, in memory.</param>
internal sealed record McpSkillServices(McpSkillApprovalStore Approvals, McpSkillContentCache Contents);

/// <summary>
/// <c>alta mcp skill list|show|activate|read</c>: the skills of the MCP servers, each named by its server and its URI. A
/// skill is loaded only once the user approves it, its files are read on its server only, and each is checked against
/// the entry the session holds since it loaded the skill.
/// </summary>
internal static class McpSkillCommands
{
    // The most characters of a file given in a read; a longer file is cut, and says so.
    private const int MaximumReadChars = 256 * 1024;

    // The most files a refusal names.
    private const int MaximumNamedFiles = 20;

    public static Command Create(PluginAltaCommandContext context, McpCommandFactoryOptions options, McpSkillServices skills, McpActivationState activation)
    {
        var command = McpCommandFactory.Group("skill", "List, show, load and read the skills MCP servers publish.");
        command.Add(CreateListCommand(context, options));
        command.Add(CreateShowCommand(context, options, skills, activation));
        command.Add(CreateActivateCommand(context, options, skills, activation));
        command.Add(CreateReadCommand(context, options, skills, activation));
        McpCommandFactory.AddHelpText(
            command,
            "A skill of an MCP server is named by its server and the URI of its `SKILL.md`; `alta skill activate` loads local skills only.",
            "Its instructions come from that server: they never override the user, the project rules or the local skills.",
            "`activate` asks the user to approve the skill, then gives its `SKILL.md`; its files are read with `read`, never from disk.",
            "Examples: `alta mcp skill list`; `alta mcp skill activate --server docs skill://pdf-processing/SKILL.md`; `alta mcp skill read --server docs templates/invoice.md`.");
        return command;
    }

    private static Command CreateListCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options)
    {
        string? server = null;
        string? query = null;
        var refresh = false;
        var command = McpCommandFactory.Leaf("list", "List the skills of the MCP servers, as CodeAlta last listed them.");
        command.Add("server=", "Only the skills of one MCP server.", value => server = value);
        command.Add("query=", "Case-insensitive text to match the name, description or URI of a skill.", value => query = value);
        command.Add("refresh", "Ask the servers for their skills: they are started.", value => refresh = value is not null);
        command.Add(async (_, _) =>
        {
            // A sandboxed session lists the skills of the servers of its profile alone: the others would start outside its sandbox.
            var sandboxServers = context.ResolveSourceSandboxMcpServersAsync is { } sandboxOf ? await sandboxOf(context.CancellationToken).ConfigureAwait(false) : null;
            if (server is not null && await McpCommandFactory.RefuseOutsideSandboxAsync(context, [server]).ConfigureAwait(false) is { } unlisted)
            {
                return unlisted;
            }

            var request = await McpCommandFactory.CreateRuntimeRequestAsync(context, options).ConfigureAwait(false);
            var diagnostics = new List<McpRuntimeDiagnostic>();
            var listed = await UseAsync(context, options, request, async (runtime, found) =>
            {
                var all = new List<McpCachedSkillList>();
                foreach (var searched in server is null && sandboxServers is not null ? sandboxServers.Select(static key => (string?)key) : [server])
                {
                    var (lists, listDiagnostics) = await runtime.ListSkillsAsync(request, searched, refresh, context.CancellationToken).ConfigureAwait(false);
                    all.AddRange(lists);
                    foreach (var diagnostic in listDiagnostics) found.Add(diagnostic);
                }

                return new SkillLists(all);
            }, diagnostics).ConfigureAwait(false);
            foreach (var diagnostic in diagnostics) McpCommandFactory.WriteRecord(context.Stdout, McpCommandFactory.CreateDiagnosticRecord(context, diagnostic));
            var policy = McpCommandFactory.LoadPolicy(context, options);
            var count = 0;
            foreach (var list in listed?.Lists ?? [])
            {
                foreach (var message in list.Diagnostics)
                {
                    McpCommandFactory.WriteRecord(context.Stdout, new { type = "alta.mcp.skill.diagnostic", version = 1, correlationId = context.CorrelationId, server = list.Server, message });
                }

                foreach (var skill in list.Skills.OrderBy(static skill => skill.Uri, StringComparer.Ordinal))
                {
                    // A skill the user turned off is not offered.
                    if (policy.IsSkillDisabled(list.Server, skill.Uri, skill.Name) || !Matches(skill, query)) continue;
                    count++;
                    McpCommandFactory.WriteRecord(context.Stdout, new
                    {
                        type = "alta.mcp.skill",
                        version = 1,
                        correlationId = context.CorrelationId,
                        server = list.Server,
                        name = skill.Name,
                        description = McpPlugin.OneLine(skill.Description, 1024),
                        uri = skill.Uri,
                        dynamic = skill.IsDynamic,
                        fileCount = skill.Files?.Count,
                        size = skill.Files is null ? (long?)null : skill.TotalSize,
                        // Two skills of a server may have one name: they are told apart by their URIs.
                        sharedName = list.Skills.Count(other => other.Name == skill.Name) > 1 ? true : (bool?)null,
                    });
                }
            }

            McpCommandFactory.WriteRecord(context.Stdout, new
            {
                type = "alta.mcp.skill.list",
                version = 1,
                correlationId = context.CorrelationId,
                server,
                query,
                skillCount = count,
                listedAt = listed?.Lists.Count == 1 ? listed.Lists[0].ListedAt : (DateTimeOffset?)null,
                note = count == 0 && !refresh ? "A server CodeAlta never started has no listing yet: `--refresh` asks the servers." : null,
            });
            return 0;
        });
        McpCommandFactory.AddHelpText(command, "A server lists its skills when CodeAlta connects to it; nothing is started for a listing unless `--refresh` is given, or the one server named was never listed.",
            "Examples: `alta mcp skill list`; `alta mcp skill list --server hf --query pdf`; `alta mcp skill list --server docs --refresh`.");
        return command;
    }

    private static Command CreateShowCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options, McpSkillServices skills, McpActivationState activation)
    {
        string? server = null;
        string? skill = null;
        var command = McpCommandFactory.Leaf("show", "Show a skill of an MCP server: its frontmatter, its files and their sizes, and whether the user approved it. No file is read.");
        command.Add("server=", "MCP server key.", value => server = value);
        command.Add("<skill>", "The URI of the `SKILL.md` of the skill, or its name.", value => skill = value);
        command.Add(async (_, _) =>
        {
            var serverKey = McpCommandFactory.RequireServerKey(server);
            if (await PrepareAsync(context, options, serverKey).ConfigureAwait(false) is not { } prepared) return 1;
            var uri = ResolveUri(context, prepared, serverKey, skill);
            if (uri is null) return 1;
            var entry = prepared.Cached.FirstOrDefault(cached => cached.Uri == uri)
                ?? await GetEntryAsync(context, options, prepared.Request, serverKey, uri).ConfigureAwait(false);
            if (entry is null) return 1;
            var approval = skills.Approvals.Find(serverKey, prepared.Fingerprint, uri);
            McpCommandFactory.WriteRecord(context.Stdout, new
            {
                type = "alta.mcp.skill.show",
                version = 1,
                correlationId = context.CorrelationId,
                server = serverKey,
                name = entry.Name,
                description = entry.Description,
                uri = entry.Uri,
                frontmatter = entry.Frontmatter,
                // CodeAlta never acts on the tools a skill of a server asks for.
                allowedToolsIgnored = entry.Frontmatter.ContainsKey("allowed-tools") ? true : (bool?)null,
                dynamic = entry.IsDynamic,
                files = entry.Files?.Select(file => new { path = entry.RelativePathOf(file.Uri), size = file.Size, digest = file.Digest }).ToArray(),
                size = entry.Files is null ? (long?)null : entry.TotalSize,
                approval = entry.IsDynamic ? "once_only" : approval is null ? "not_approved" : approval.ManifestHash == entry.ManifestHash ? "approved" : "changed",
                disabled = prepared.Policy.IsSkillDisabled(serverKey, entry.Uri, entry.Name) ? true : (bool?)null,
                loaded = activation.GetHeldSkills(prepared.ScopeKey, serverKey).Any(held => held.Entry.Uri == entry.Uri) ? true : (bool?)null,
            });
            return 0;
        });
        McpCommandFactory.AddHelpText(command, "Example: `alta mcp skill show --server docs skill://pdf-processing/SKILL.md`.");
        return command;
    }

    private static Command CreateActivateCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options, McpSkillServices skills, McpActivationState activation)
    {
        string? server = null;
        string? skill = null;
        var command = McpCommandFactory.Leaf("activate", "Load a skill of an MCP server, once the user approves it: its `SKILL.md`, checked, tagged with its server.");
        command.Add("server=", "MCP server key.", value => server = value);
        command.Add("<skill>", "The URI of the `SKILL.md` of the skill, or its name when no other skill of the server has it.", value => skill = value);
        command.Add(async (_, _) =>
        {
            var serverKey = McpCommandFactory.RequireServerKey(server);
            if (RefuseWithoutSession(context) is { } refused) return refused;
            if (await PrepareAsync(context, options, serverKey).ConfigureAwait(false) is not { } prepared) return 1;
            var uri = ResolveUri(context, prepared, serverKey, skill);
            if (uri is null) return 1;
            McpSkillValidator.TrySplitUri(uri, out _, out var name);
            if (prepared.Policy.IsSkillDisabled(serverKey, uri, name))
            {
                return McpCommandFactory.WriteError(context, "skill_disabled", $"The user turned off the skill `{uri}` of MCP server '{serverKey}'.");
            }

            // The entry of now, which the approval is bound to and the files are checked against: not the one of the listing.
            var entry = await GetEntryAsync(context, options, prepared.Request, serverKey, uri).ConfigureAwait(false);
            if (entry is null) return 1;
            string approval;
            if (!entry.IsDynamic && skills.Approvals.IsApproved(serverKey, prepared.Fingerprint, uri, entry.ManifestHash))
            {
                approval = "kept";
            }
            else if (context.RequestPermissionAsync is not { } ask)
            {
                return McpCommandFactory.WriteError(context, "approval_unavailable",
                    $"The skill `{uri}` of MCP server '{serverKey}' needs the approval of the user, which this caller cannot ask: an agent of a session of CodeAlta Desktop loads it.");
            }
            else
            {
                var decision = await ask(new AgentSkillPermissionRequest(new ModelProviderId("codealta"), context.SourceSessionId ?? string.Empty, DateTimeOffset.UtcNow, null,
                    "mcp-skill-" + Guid.NewGuid().ToString("N"), serverKey, entry.Name, uri, entry.Files?.Count ?? 0, entry.TotalSize, entry.IsDynamic), context.CancellationToken).ConfigureAwait(false);
                switch (decision.Kind)
                {
                    case AgentPermissionDecisionKind.AllowAlways when !entry.IsDynamic:
                        skills.Approvals.Approve(serverKey, prepared.Fingerprint, uri, entry.ManifestHash!);
                        approval = "always";
                        break;
                    case AgentPermissionDecisionKind.AllowAlways or AgentPermissionDecisionKind.AllowOnce or AgentPermissionDecisionKind.AllowForSession:
                        approval = "once";
                        break;
                    default:
                        return McpCommandFactory.WriteError(context, "skill_not_approved",
                            $"The user did not allow the skill `{uri}` of MCP server '{serverKey}': it is not loaded. Do without it, or ask the user.");
                }
            }

            var diagnostics = new List<McpRuntimeDiagnostic>();
            var held = new McpHeldSkill(serverKey, entry);
            var read = await UseAsync(context, options, prepared.Request, async (runtime, _) => await ReadAsync(runtime, prepared.Request, held, uri, skills, context.CancellationToken).ConfigureAwait(false), diagnostics).ConfigureAwait(false);
            if (read?.Bytes is not { } bytes)
            {
                return ReportReadFailure(context, options, prepared, activation, held, uri, read, diagnostics);
            }

            if (McpSkillReader.ReadSkillFile(entry, bytes, out var text) is { } mismatch)
            {
                return McpCommandFactory.WriteError(context, "skill_changed",
                    $"The `SKILL.md` of the skill `{uri}` is not the one its entry describes ({mismatch}): it is not loaded. The server may have changed it; activate it again.");
            }

            activation.HoldSkill(prepared.ScopeKey, held);
            McpCommandFactory.WriteRecord(context.Stdout, new
            {
                type = "alta.skill.activated",
                version = 1,
                correlationId = context.CorrelationId,
                skillName = entry.Name,
                sessionId = context.SourceSessionId,
                sourceKind = "mcp",
                server = serverKey,
                uri,
                approval,
                dynamic = entry.IsDynamic,
                payload = McpSkillReader.BuildPayload(serverKey, entry, text, prepared.DirectoryRead),
            });
            return 0;
        });
        McpCommandFactory.AddHelpText(command,
            "The user is asked to approve the skill, in every permission mode, unless they allowed it from now on and its files did not change since.",
            "Examples: `alta mcp skill activate --server docs skill://pdf-processing/SKILL.md`; `alta mcp skill activate --server docs pdf-processing`.");
        return command;
    }

    private static Command CreateReadCommand(PluginAltaCommandContext context, McpCommandFactoryOptions options, McpSkillServices skills, McpActivationState activation)
    {
        string? server = null;
        string? skill = null;
        string? path = null;
        var command = McpCommandFactory.Leaf("read", "Read a file, or list a folder, of a skill of an MCP server this session loaded, checked against the skill as it was loaded.");
        command.Add("server=", "MCP server key.", value => server = value);
        command.Add("skill=", "The skill, by URI or name, when the session loaded several of the server.", value => skill = value);
        command.Add("<path>", "The path of the file under the folder of the skill (`templates/invoice.md`), or its URI; a folder ends with `/`.", value => path = value);
        command.Add(async (_, _) =>
        {
            var serverKey = McpCommandFactory.RequireServerKey(server);
            if (RefuseWithoutSession(context) is { } refused) return refused;
            if (string.IsNullOrWhiteSpace(path)) return McpCommandFactory.WriteError(context, "missing_path", "Give the path or the URI of a file of the skill.");
            if (await PrepareAsync(context, options, serverKey).ConfigureAwait(false) is not { } prepared) return 1;
            var loaded = activation.GetHeldSkills(prepared.ScopeKey, serverKey);
            if (loaded.Count == 0)
            {
                return McpCommandFactory.WriteError(context, "skill_not_loaded",
                    $"This session loaded no skill of MCP server '{serverKey}': load one with `alta mcp skill activate --server {serverKey} <uri>`.");
            }

            if (!TryResolveTarget(loaded, skill, path.Trim(), out var held, out var target, out var folder, out var error))
            {
                return McpCommandFactory.WriteError(context, error!.Value.Code, error.Value.Message);
            }

            var entry = held!.Entry;
            if (entry.Files is { } files)
            {
                // A folder of a skill whose files are listed is answered from its entry.
                if (folder || (entry.FindFile(target!) is null && files.Any(file => file.Uri.StartsWith(target + "/", StringComparison.Ordinal))))
                {
                    WriteFolder(context, serverKey, entry, target!, files.Where(file => file.Uri.StartsWith(target + "/", StringComparison.Ordinal))
                        .Select(file => file.Uri[(target!.Length + 1)..]).ToArray(), "manifest");
                    return 0;
                }

                if (entry.FindFile(target!) is null)
                {
                    return await ReportUnlistedAsync(context, options, prepared, activation, held, target!).ConfigureAwait(false);
                }
            }
            else if (folder)
            {
                var children = new List<McpRuntimeDiagnostic>();
                var listed = await UseAsync(context, options, prepared.Request,
                    async (runtime, found) => await runtime.ReadSkillDirectoryAsync(prepared.Request, serverKey, target!, found, context.CancellationToken).ConfigureAwait(false), children).ConfigureAwait(false);
                if (listed is null) return WriteDiagnostics(context, children, "directory_read_failed", $"The folder `{target}` of the skill could not be listed.");
                WriteFolder(context, serverKey, entry, target!, listed.Select(child => child.Uri.StartsWith(target + "/", StringComparison.Ordinal)
                    ? child.Uri[(target!.Length + 1)..] + (child.IsDirectory ? "/" : string.Empty) : child.Name).ToArray(), "server");
                return 0;
            }

            var diagnostics = new List<McpRuntimeDiagnostic>();
            var read = await UseAsync(context, options, prepared.Request, async (runtime, _) => await ReadAsync(runtime, prepared.Request, held, target!, skills, context.CancellationToken).ConfigureAwait(false), diagnostics).ConfigureAwait(false);
            if (read?.Bytes is not { } bytes) return ReportReadFailure(context, options, prepared, activation, held, target!, read, diagnostics);
            var text = McpSkillReader.TryDecode(bytes, out var decoded) ? decoded : null;
            var truncated = text is not null && text.Length > MaximumReadChars;
            McpCommandFactory.WriteRecord(context.Stdout, new
            {
                type = "alta.mcp.skill.read",
                version = 1,
                correlationId = context.CorrelationId,
                server = serverKey,
                skill = entry.Name,
                uri = target,
                path = entry.RelativePathOf(target!),
                size = bytes.Length,
                // A file of a skill its server generates cannot be checked.
                verified = !entry.IsDynamic,
                content = text is null ? null : truncated ? text[..MaximumReadChars] : text,
                contentBase64 = text is null ? Convert.ToBase64String(bytes) : null,
                truncated = truncated ? true : (bool?)null,
            });
            return 0;
        });
        McpCommandFactory.AddHelpText(command,
            "Only the files of the skill as it was loaded are read; a file it did not list then is refused, and the skill is to be loaded again.",
            "Examples: `alta mcp skill read --server docs templates/invoice.md`; `alta mcp skill read --server docs templates/`.");
        return command;
    }

    // Reads a file of a held skill: checked when the skill lists its files, counted against the limit of a skill otherwise.
    private static async Task<McpSkillRead> ReadAsync(McpRuntimeService runtime, McpRuntimeRequest request, McpHeldSkill held, string uri, McpSkillServices skills, CancellationToken cancellationToken)
    {
        if (held.Entry.FindFile(uri) is { } file)
        {
            return await McpSkillReader.ReadFileAsync(runtime, request, held.Server, file, skills.Contents, cancellationToken).ConfigureAwait(false);
        }

        var read = await McpSkillReader.ReadUncheckedAsync(runtime, request, held.Server, uri, cancellationToken).ConfigureAwait(false);
        if (read.Bytes is { } bytes && !held.TryCount(bytes.Length))
        {
            return new McpSkillRead(null, $"The files read of the skill would be larger than {McpSkillReader.FormatSize(McpSkills.MaximumTotalBytes)}.", []);
        }

        return read;
    }

    // A read that failed: the server could not be read, or it answered other bytes than the skill's. Then the entry is
    // asked again: a skill that changed is let go, to be loaded again, which asks the user again when its files changed.
    private static int ReportReadFailure(PluginAltaCommandContext context, McpCommandFactoryOptions options, Prepared prepared, McpActivationState activation,
        McpHeldSkill held, string uri, McpSkillRead? read, IReadOnlyList<McpRuntimeDiagnostic> diagnostics)
    {
        if (read?.Mismatch is not { } mismatch)
        {
            return WriteDiagnostics(context, [.. diagnostics, .. read?.Diagnostics ?? []], "skill_read_failed", $"The file `{uri}` of the skill could not be read from MCP server '{held.Server}'.");
        }

        activation.ReleaseSkill(prepared.ScopeKey, held.Server, held.Entry.Uri);
        return McpCommandFactory.WriteError(context, "skill_changed",
            $"The file `{held.Entry.RelativePathOf(uri)}` is not the one of the skill `{held.Entry.Uri}` as it was loaded ({mismatch}): the skill changed on its server, or the server serves other content. It is not used. " +
            $"Load the skill again with `alta mcp skill activate --server {held.Server} {held.Entry.Uri}`; the user is asked again when its files changed.");
    }

    // A file the held entry does not list is a change of the skill, or a file it does not have: the entry is asked again.
    private static async Task<int> ReportUnlistedAsync(PluginAltaCommandContext context, McpCommandFactoryOptions options, Prepared prepared, McpActivationState activation, McpHeldSkill held, string target)
    {
        var current = await GetEntryAsync(context, options, prepared.Request, held.Server, held.Entry.Uri, quiet: true).ConfigureAwait(false);
        if (current is null || current.ManifestHash != held.Entry.ManifestHash)
        {
            activation.ReleaseSkill(prepared.ScopeKey, held.Server, held.Entry.Uri);
            return McpCommandFactory.WriteError(context, "skill_changed",
                $"The skill `{held.Entry.Uri}` changed on MCP server '{held.Server}' since it was loaded: `{held.Entry.RelativePathOf(target)}` is not among the files it was loaded with. " +
                $"Load it again with `alta mcp skill activate --server {held.Server} {held.Entry.Uri}`, which asks the user again.");
        }

        var files = held.Entry.Files!.Select(file => held.Entry.RelativePathOf(file.Uri)).Take(MaximumNamedFiles).ToArray();
        return McpCommandFactory.WriteError(context, "file_not_in_skill",
            $"`{held.Entry.RelativePathOf(target)}` is not a file of the skill `{held.Entry.Uri}`. Its files: {string.Join(", ", files)}{(held.Entry.Files!.Count > files.Length ? ", …" : string.Empty)}.");
    }

    // Resolves the skill and the URI a read names among the skills of a server the session loaded.
    private static bool TryResolveTarget(IReadOnlyList<McpHeldSkill> loaded, string? skill, string path, out McpHeldSkill? held, out string? target, out bool folder,
        out (string Code, string Message)? error)
    {
        held = null;
        target = null;
        folder = false;
        error = null;
        IReadOnlyList<McpHeldSkill> candidates = loaded;
        if (!string.IsNullOrWhiteSpace(skill))
        {
            var wanted = skill.Trim();
            candidates = [.. loaded.Where(item => item.Entry.Uri == wanted || item.Entry.Name == wanted)];
            if (candidates.Count == 0)
            {
                error = ("skill_not_loaded", $"This session loaded no skill `{wanted}` of this server. It loaded: {string.Join(", ", loaded.Select(static item => item.Entry.Uri))}.");
                return false;
            }
        }

        if (path.Contains("://", StringComparison.Ordinal))
        {
            // The skill whose folder holds the URI; of a skill nested in another, the innermost.
            var trimmed = path.TrimEnd('/');
            held = candidates.Where(item => trimmed == item.Entry.RootUri || McpSkillValidator.IsUnder(item.Entry.RootUri, trimmed))
                .OrderByDescending(static item => item.Entry.RootUri.Length).FirstOrDefault();
            if (held is null)
            {
                error = ("file_not_in_skill", $"`{path}` is not in the folder of a skill this session loaded of this server.");
                return false;
            }

            target = trimmed;
            folder = path.EndsWith('/') || trimmed == held.Entry.RootUri;
            return true;
        }

        if (candidates.Count > 1)
        {
            error = ("skill_ambiguous", $"This session loaded several skills of this server: name one with `--skill`: {string.Join(", ", candidates.Select(static item => item.Entry.Uri))}.");
            return false;
        }

        held = candidates[0];
        var relative = path.Replace('\\', '/');
        while (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
        folder = relative.Length == 0 || relative == "." || relative.EndsWith('/');
        relative = relative.TrimEnd('/');
        if (relative is "" or ".")
        {
            target = held.Entry.RootUri;
            return true;
        }

        target = held.Entry.RootUri + "/" + relative;
        if (relative.StartsWith('/') || !McpSkillValidator.IsUnder(held.Entry.RootUri, target))
        {
            error = ("file_not_in_skill", $"`{path}` is not a path under the folder of the skill `{held.Entry.Uri}`.");
            return false;
        }

        return true;
    }

    private static void WriteFolder(PluginAltaCommandContext context, string server, McpSkillEntry entry, string uri, IReadOnlyList<string> descendants, string source)
    {
        // The direct children: a file, or the first folder of a deeper path.
        var children = descendants.Select(static path => path.Contains('/') ? path[..(path.IndexOf('/') + 1)] : path)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        McpCommandFactory.WriteRecord(context.Stdout, new
        {
            type = "alta.mcp.skill.folder",
            version = 1,
            correlationId = context.CorrelationId,
            server,
            skill = entry.Name,
            uri,
            path = uri == entry.RootUri ? "" : entry.RelativePathOf(uri) + "/",
            entries = children.Select(child =>
            {
                var file = child.EndsWith('/') ? null : entry.FindFile(uri + "/" + child);
                return new { name = child, size = file?.Size };
            }).ToArray(),
            // The entry the skill was loaded with, or what its server lists now (a skill whose server generates its files).
            source,
        });
    }

    // A skill is loaded and read for a session, which holds its entry: a caller without one (a terminal, a client of the
    // MCP server of the window) has none to hold it.
    private static int? RefuseWithoutSession(PluginAltaCommandContext context)
        => string.IsNullOrWhiteSpace(context.SourceSessionId)
            ? McpCommandFactory.WriteError(context, "session_required", "The skills of MCP servers are loaded and read by an agent of a session of CodeAlta Desktop.")
            : null;

    // The configured server of the call, refused out of the space of the session, with what the commands read of it.
    private static async Task<Prepared?> PrepareAsync(PluginAltaCommandContext context, McpCommandFactoryOptions options, string serverKey)
    {
        var definition = McpCommandFactory.Discover(context, options).EffectiveServers.FirstOrDefault(server => server.Definition.Key == serverKey)?.Definition;
        if (definition is null)
        {
            McpCommandFactory.WriteError(context, "server_not_found", $"MCP server '{serverKey}' is not configured.");
            return null;
        }

        if (await McpCommandFactory.RefuseOutsideSpaceAsync(context, options, [serverKey]).ConfigureAwait(false) is not null) return null;
        var request = await McpCommandFactory.CreateRuntimeRequestAsync(context, options).ConfigureAwait(false);
        IReadOnlyList<McpCachedSkillList> lists;
        await using (var runtime = McpCommandFactory.CreateRuntime(options))
        {
            lists = runtime.ListCachedSkills(request);
        }

        var list = lists.FirstOrDefault(item => item.Server == serverKey);
        return new Prepared(request, McpToolListCache.Fingerprint(definition), list?.Skills ?? [], list?.DirectoryRead ?? false,
            McpCommandFactory.LoadPolicy(context, options),
            McpCommandFactory.ResolveActivationScopeKey(context, McpCommandFactory.ResolveProjectDirectory(context, options)));
    }

    // The URI a command names: given as such, or the one skill of the server's listing with that name.
    private static string? ResolveUri(PluginAltaCommandContext context, Prepared prepared, string serverKey, string? skill)
    {
        if (string.IsNullOrWhiteSpace(skill))
        {
            McpCommandFactory.WriteError(context, "missing_skill", "Give the URI of the `SKILL.md` of the skill, or its name.");
            return null;
        }

        var value = skill.Trim();
        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (McpSkillValidator.TrySplitUri(value, out _, out _)) return value;
            McpCommandFactory.WriteError(context, "invalid_skill_uri", $"`{McpSkillValidator.Shorten(value)}` is not the URI of the `SKILL.md` of a skill.");
            return null;
        }

        var matches = prepared.Cached.Where(entry => entry.Name == value).Select(static entry => entry.Uri).Order(StringComparer.Ordinal).ToArray();
        if (matches.Length == 1) return matches[0];
        McpCommandFactory.WriteError(context, matches.Length == 0 ? "skill_not_found" : "skill_ambiguous", matches.Length == 0
            ? $"MCP server '{serverKey}' lists no skill named '{value}': give its URI, or list its skills with `alta mcp skill list --server {serverKey} --refresh`."
            : $"Several skills of MCP server '{serverKey}' are named '{value}': give the URI of one: {string.Join(", ", matches)}.");
        return null;
    }

    // The entry of a skill of now, with `skills/get`; null, once reported, when the server does not give it.
    private static async Task<McpSkillEntry?> GetEntryAsync(PluginAltaCommandContext context, McpCommandFactoryOptions options, McpRuntimeRequest request, string serverKey, string uri, bool quiet = false)
    {
        var diagnostics = new List<McpRuntimeDiagnostic>();
        var entry = await UseAsync(context, options, request,
            async (runtime, found) => await runtime.GetSkillEntryAsync(request, serverKey, uri, found, context.CancellationToken).ConfigureAwait(false), diagnostics).ConfigureAwait(false);
        if (entry is null && !quiet) WriteDiagnostics(context, diagnostics, "skill_not_found", $"MCP server '{serverKey}' gives no skill at `{uri}`.");
        return entry;
    }

    private static int WriteDiagnostics(PluginAltaCommandContext context, IReadOnlyList<McpRuntimeDiagnostic> diagnostics, string code, string message)
    {
        foreach (var diagnostic in diagnostics) McpCommandFactory.WriteRecord(context.Stdout, McpCommandFactory.CreateDiagnosticRecord(context, diagnostic));
        return McpCommandFactory.WriteError(context, diagnostics.LastOrDefault()?.Code ?? code, diagnostics.LastOrDefault()?.Message ?? message);
    }

    // A use of a runtime: on the connections of the session that calls, which its tool calls use, else on one of its own.
    private static async Task<T?> UseAsync<T>(PluginAltaCommandContext context, McpCommandFactoryOptions options, McpRuntimeRequest request,
        Func<McpRuntimeService, IList<McpRuntimeDiagnostic>, Task<T?>> use, IList<McpRuntimeDiagnostic> diagnostics)
        where T : class
    {
        if (options.Connections is { } connections && !string.IsNullOrWhiteSpace(context.SourceSessionId))
        {
            try
            {
                return await connections.UseForViewAsync(context.SourceSessionId, request, use, diagnostics, context.CancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                diagnostics.Add(new McpRuntimeDiagnostic { Code = "unavailable", Message = "The MCP plugin was stopped." });
                return null;
            }
        }

        await using var runtime = McpCommandFactory.CreateRuntime(options);
        return await use(runtime, diagnostics).ConfigureAwait(false);
    }

    private static bool Matches(McpSkillEntry skill, string? query)
        => string.IsNullOrWhiteSpace(query)
            || skill.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
            || skill.Description.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
            || skill.Uri.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    private sealed record SkillLists(IReadOnlyList<McpCachedSkillList> Lists);

    private sealed record Prepared(McpRuntimeRequest Request, string Fingerprint, IReadOnlyList<McpSkillEntry> Cached, bool DirectoryRead, McpPolicyOptions Policy, string ScopeKey);
}
