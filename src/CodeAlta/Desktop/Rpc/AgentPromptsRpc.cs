using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.SystemPrompts;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Lists, reads, saves and deletes agent prompts and system prompts for the desktop Settings page: the
/// same prompt files, precedence and read-only built-ins as the terminal's prompt manager.
/// </summary>
/// <remarks>
/// A prompt is addressed by kind, scope and file id, never by a path. Saving an existing prompt and
/// deleting one require the revision that was read, so a file changed elsewhere is reported as a conflict.
/// </remarks>
[NeoRpcService("agentPrompts", Version = 1)]
internal sealed class AgentPromptsService
{
    /// <summary>Largest prompt body accepted or returned, in UTF-16 units.</summary>
    internal const int MaximumBodyLength = 256 * 1024;

    /// <summary>Largest number of prompts returned by one listing.</summary>
    internal const int MaximumPrompts = 256;

    private const int MaximumIdLength = 128;
    private const int MaximumNameLength = 256;
    private const int MaximumDescriptionLength = 1024;
    private const int MaximumMessageLength = 512;
    private readonly ProjectCatalog? _projects;
    private readonly string? _epoch;
    private readonly string? _applicationRoot;
    private readonly AgentPromptCatalog _catalog = new();
    private readonly TextFileCodec _textFiles = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal AgentPromptsService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="projects">The host's project catalog; its global root holds the global prompts.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="applicationRoot">The directory holding the shipped <c>content/prompts</c>, or null for the application's.</param>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal AgentPromptsService(ProjectCatalog projects, string epoch, string? applicationRoot = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _projects = projects;
        _epoch = epoch;
        _applicationRoot = applicationRoot;
    }

    /// <summary>Lists every valid agent and system prompt file, including the ones a later source shadows.</summary>
    [NeoRpcMethod("list")]
    public async Task<AgentPromptsListResponse> ListAsync(AgentPromptsListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        AgentPromptsListResponse Failed(string status) => new(status, request.ProjectId, [], 0);
        if (_projects is null) return Failed("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Failed("stale_epoch");
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return Failed(project.Status);
        try
        {
            var query = Query(project.Root);
            var agents = _catalog.ListPrompts(query);
            var systems = _catalog.ListSystemPrompts(query);
            var sources = agents.Select(static prompt => (prompt.SourcePath, prompt.SourceKind))
                .Concat(systems.Select(static prompt => (prompt.SourcePath, prompt.SourceKind)))
                .GroupBy(static source => source.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.First().SourceKind, StringComparer.OrdinalIgnoreCase);
            string? ShadowedBy(string? path) => path is not null && sources.TryGetValue(path, out var kind) ? Scope(kind).ToString() : null;
            var rows = agents.Select(prompt => new AgentPromptEntry(prompt.PromptName, Bound(prompt.DisplayName, MaximumNameLength)!,
                    Bound(prompt.Description, MaximumDescriptionLength), nameof(PromptResourceKind.Agent), Scope(prompt.SourceKind).ToString(),
                    prompt.IsBuiltIn, prompt.IsShadowed, ShadowedBy(prompt.ShadowedByPath), Bound(prompt.SystemPromptName, MaximumIdLength),
                    prompt.Mode == PromptCompositionMode.Append))
                .Concat(systems.Select(prompt => new AgentPromptEntry(prompt.PromptName, Bound(prompt.PromptName, MaximumNameLength)!, null,
                    nameof(PromptResourceKind.System), Scope(prompt.SourceKind).ToString(), prompt.IsBuiltIn, prompt.IsShadowed,
                    ShadowedBy(prompt.ShadowedByPath), null, prompt.Mode == PromptCompositionMode.Append)))
                // An id the store cannot address is counted, never shortened into another prompt's id.
                .Where(static row => ValidId(row.Id)).ToArray();
            var listed = rows.Take(MaximumPrompts).ToArray();
            return new("ok", request.ProjectId, listed, agents.Count + systems.Count - listed.Length);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed("read_failed");
        }
    }

    /// <summary>Reads one prompt's editable values and the revision of the file they came from.</summary>
    [NeoRpcMethod("read")]
    public async Task<AgentPromptReadResponse> ReadAsync(AgentPromptReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_projects is null) return new("unavailable", null, null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null, null);
        if (Identify(request.Kind, request.Scope, request.Id, out var identity) is { } refusal) return new("invalid", null, refusal);
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return new(project.Status, null, null);
        if (project.Root is null && identity.Scope == PromptResourceScope.Project) return new("invalid", null, ProjectRequired);
        try
        {
            var snapshot = Store(project.Root).Load(identity);
            var content = snapshot.Content;
            // An editable value is returned whole or not at all: a shortened one would be saved back shortened.
            if (content.Body.Length > MaximumBodyLength || content.Name?.Length > MaximumNameLength
                || content.Description?.Length > MaximumDescriptionLength || content.SystemPromptName?.Length > MaximumIdLength)
                return new("too_large", null, null);
            return new("ok", new(identity.Id, identity.Kind.ToString(), identity.Scope.ToString(), identity.Scope == PromptResourceScope.BuiltIn,
                content.Name, content.Description, identity.Kind == PromptResourceKind.Agent ? content.SystemPromptName : null,
                content.Body, content.Append, snapshot.File.Revision.ContentHash!), null);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new("not_found", null, null);
        }
        catch (ArgumentException exception)
        {
            // The file exists but is not a valid prompt (or is not valid Unicode).
            return new("invalid", null, Reason(exception));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("read_failed", null, null);
        }
    }

    /// <summary>
    /// Creates a global or project prompt when no revision is expected, or replaces the prompt whose file
    /// still has the expected revision. Built-in prompts are refused.
    /// </summary>
    [NeoRpcMethod("save")]
    public async Task<AgentPromptMutationResponse> SaveAsync(AgentPromptSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_projects is null) return new("unavailable", null, null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null, null);
        if (Identify(request.Kind, request.Scope, request.Id, out var identity) is { } refusal) return new("invalid", null, refusal);
        if (identity.Scope == PromptResourceScope.BuiltIn) return new("read_only", null, null);
        if (request.Body is null) return new("invalid", null, "A prompt body is required.");
        if (request.Body.Length > MaximumBodyLength) return new("too_large", null, null);
        if (!Line(request.Name, MaximumNameLength) || !Line(request.Description, MaximumDescriptionLength) || !Line(request.SystemPromptId, MaximumIdLength))
            return new("invalid", null, "The name, description or system prompt id is too long or contains control characters.");
        var content = new PromptFileContent(Optional(request.Name), Optional(request.Description),
            identity.Kind == PromptResourceKind.Agent ? Optional(request.SystemPromptId) : null, request.Body.Trim(), request.Append);
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return new(project.Status, null, null);
        if (project.Root is null && identity.Scope == PromptResourceScope.Project) return new("invalid", null, ProjectRequired);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Reports an empty body, a missing name or an unusable system id before anything is written.
            PromptFileFormat.Validate(identity.Kind, content);
            var store = Store(project.Root);
            TextFileSaveResult result;
            if (request.ExpectedRevision is null)
            {
                result = await store.CreateAsync(identity, content, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                var snapshot = store.Load(identity);
                // Another editor (the TUI, a text editor) may have changed the file since it was read.
                if (!string.Equals(snapshot.File.Revision.ContentHash, request.ExpectedRevision, StringComparison.Ordinal)) return new("conflict", null, null);
                result = await store.SaveAsync(snapshot, content, snapshot.File.Revision, CancellationToken.None).ConfigureAwait(false);
            }

            return result.IsConflict ? new("conflict", null, null) : new("ok", result.CurrentRevision.ContentHash, null);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new("not_found", null, null);
        }
        catch (ArgumentException exception)
        {
            return new("invalid", null, Reason(exception));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("write_failed", null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes a global or project prompt whose file still has the expected revision.</summary>
    [NeoRpcMethod("delete")]
    public async Task<AgentPromptMutationResponse> DeleteAsync(AgentPromptDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_projects is null) return new("unavailable", null, null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null, null);
        if (Identify(request.Kind, request.Scope, request.Id, out var identity) is { } refusal) return new("invalid", null, refusal);
        if (identity.Scope == PromptResourceScope.BuiltIn) return new("read_only", null, null);
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return new(project.Status, null, null);
        if (project.Root is null && identity.Scope == PromptResourceScope.Project) return new("invalid", null, ProjectRequired);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var store = Store(project.Root);
            var snapshot = store.Load(identity);
            if (!string.Equals(snapshot.File.Revision.ContentHash, request.ExpectedRevision, StringComparison.Ordinal)) return new("conflict", null, null);
            return store.Delete(snapshot).IsConflict ? new("conflict", null, null) : new("ok", null, null);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new("not_found", null, null);
        }
        catch (ArgumentException exception)
        {
            return new("invalid", null, Reason(exception));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("write_failed", null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private AgentPromptCatalogQuery Query(string? projectRoot) => new()
    {
        AppBaseDirectory = _applicationRoot,
        UserCodeAltaRoot = _projects!.Options.GlobalRoot,
        UserProfileRoot = _projects.Options.GlobalRoot, // Explicit unused fallback: do not query the user's profile.
        ProjectRoot = projectRoot,
        ProjectPromptResourcesTrusted = projectRoot is not null,
    };

    private PromptResourceStore Store(string? projectRoot)
    {
        var roots = _catalog.ResolveRoots(Query(projectRoot));
        return new PromptResourceStore(roots.ShippedPromptRoot, roots.GlobalPromptRoot, roots.ProjectPromptRoot, _textFiles);
    }

    private const string ProjectRequired = "The project scope requires a project.";

    // Returns the refusal for an identity the store cannot address, or null.
    private static string? Identify(string? kind, string? scope, string? id, out PromptResourceIdentity identity)
    {
        identity = new(PromptResourceScope.BuiltIn, PromptResourceKind.Agent, string.Empty);
        PromptResourceKind? resourceKind = kind?.ToLowerInvariant() switch
        {
            "agent" => PromptResourceKind.Agent, "system" => PromptResourceKind.System, _ => null,
        };
        PromptResourceScope? resourceScope = scope?.ToLowerInvariant() switch
        {
            "builtin" => PromptResourceScope.BuiltIn, "global" => PromptResourceScope.Global, "project" => PromptResourceScope.Project, _ => null,
        };
        if (resourceKind is null) return "The kind must be Agent or System.";
        if (resourceScope is null) return "The scope must be BuiltIn, Global or Project.";
        if (!ValidId(id)) return "A prompt id uses letters, digits, '.', '_' or '-' (at most 128).";
        identity = new(resourceScope.Value, resourceKind.Value, id!);
        return null;
    }

    private static bool ValidId(string? id)
    {
        if (id is not { Length: > 0 and <= MaximumIdLength }) return false;
        try
        {
            PromptResourceStore.ValidateId(id);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static PromptResourceScope Scope(AgentPromptSourceKind kind) => kind switch
    {
        AgentPromptSourceKind.UserGlobal => PromptResourceScope.Global,
        AgentPromptSourceKind.Project => PromptResourceScope.Project,
        _ => PromptResourceScope.BuiltIn,
    };

    private static bool Line(string? value, int maximum) => value is null || (value.Length <= maximum && !value.Any(char.IsControl));

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Bound(string? value, int maximum) => value is null || value.Length <= maximum ? value : value[..maximum];

    // The validation sentence without the runtime's parameter-name suffix.
    private static string Reason(ArgumentException exception)
    {
        var message = exception.Message;
        var suffix = message.IndexOf(" (Parameter '", StringComparison.Ordinal);
        return Bound(suffix > 0 ? message[..suffix] : message, MaximumMessageLength)!;
    }
}

internal sealed record AgentPromptsListRequest(string? ExpectedEpoch, string? ProjectId);
internal sealed record AgentPromptsListResponse(string Status, string? ProjectId, IReadOnlyList<AgentPromptEntry> Prompts, int Omitted);

/// <summary>One prompt file: kind is <c>Agent</c> or <c>System</c>, scope is <c>BuiltIn</c>, <c>Global</c> or <c>Project</c>.</summary>
internal sealed record AgentPromptEntry(string Id, string Name, string? Description, string Kind, string Scope, bool ReadOnly,
    bool Shadowed, string? ShadowedByScope, string? SystemPromptId, bool Append);
internal sealed record AgentPromptReadRequest(string? ExpectedEpoch, string? ProjectId, string? Kind, string? Scope, string? Id);
internal sealed record AgentPromptReadResponse(string Status, AgentPromptDocument? Prompt, string? Message);

/// <summary>The values written in one prompt file (null where the file leaves them unset) and its revision.</summary>
internal sealed record AgentPromptDocument(string Id, string Kind, string Scope, bool ReadOnly, string? Name, string? Description,
    string? SystemPromptId, string Body, bool Append, string Revision);

/// <summary>A prompt to write; a null expected revision creates the file and refuses an existing one.</summary>
internal sealed record AgentPromptSaveRequest(string? ExpectedEpoch, string? ProjectId, string? Kind, string? Scope, string? Id,
    string? ExpectedRevision, string? Name, string? Description, string? SystemPromptId, string? Body, bool Append);
internal sealed record AgentPromptDeleteRequest(string? ExpectedEpoch, string? ProjectId, string? Kind, string? Scope, string? Id, string? ExpectedRevision);
internal sealed record AgentPromptMutationResponse(string Status, string? Revision, string? Message);
