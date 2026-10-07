using CodeAlta.Catalog;
using CodeAlta.Catalog.PullRequests;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The instructions a session is sent when the user asks it to create a pull request: the kind that ships with
/// CodeAlta, those of the user and those of a project. The page lists them for the button under the prompt and
/// for Settings, where the user's and a project's are written.
/// </summary>
[NeoRpcService("pullRequestPrompts", Version = 1)]
internal sealed class PullRequestPromptsService
{
    private readonly PullRequestPromptCatalog? _catalog;
    private readonly ProjectCatalog? _projects;
    private readonly string? _epoch;

    /// <summary>Creates the service of a window whose host keeps no instructions: it answers <c>unavailable</c>.</summary>
    public PullRequestPromptsService()
    {
    }

    /// <summary>Creates the service of an owned host.</summary>
    internal PullRequestPromptsService(PullRequestPromptCatalog catalog, ProjectCatalog projects, string epoch)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_catalog, _projects, _epoch) = (catalog, projects, epoch);
    }

    /// <summary>
    /// Lists the kinds of pull request: for a project, the ones its sessions can be sent; with <c>All</c>, every
    /// file, the ones a nearer file replaces included.
    /// </summary>
    [NeoRpcMethod("list")]
    public async Task<PullRequestPromptsResponse> ListAsync(PullRequestPromptsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, []);
        string? path = null;
        if (request.ProjectId is not null)
        {
            if (await ResolveAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is not { } project) return new("not_found", []);
            path = project.ProjectPath;
        }

        var prompts = request.All ? _catalog!.List(path) : _catalog!.Effective(path);
        return new("ok", [.. prompts.Select(Item)]);
    }

    /// <summary>Writes a kind of the user (no project) or of a project.</summary>
    [NeoRpcMethod("save")]
    public async Task<PullRequestPromptSaveResponse> SaveAsync(PullRequestPromptSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (PullRequestPromptCatalog.NormalizeId(request.Id) is not { } id) return new("invalid", "A name uses letters, digits, '-' or '_' (at most 64).", null);
        string? path = null;
        if (request.ProjectId is not null)
        {
            if (await ResolveAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is not { } project) return new("not_found", null, null);
            path = project.ProjectPath;
        }

        try
        {
            return new("ok", null, Item(_catalog!.Save(path, id, request.Name, request.Description, request.Content ?? string.Empty)));
        }
        catch (ArgumentException exception)
        {
            return new("invalid", exception.Message.Split(" (Parameter", 2)[0], null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("write_failed", null, null);
        }
    }

    /// <summary>Deletes a kind of the user or of a project. What ships with CodeAlta is not a file to delete.</summary>
    [NeoRpcMethod("delete")]
    public async Task<PullRequestPromptSaveResponse> DeleteAsync(PullRequestPromptDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null);
        string? path = null;
        if (request.ProjectId is not null)
        {
            if (await ResolveAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is not { } project) return new("not_found", null, null);
            path = project.ProjectPath;
        }

        try
        {
            return new(_catalog!.Delete(path, request.Id ?? string.Empty) ? "ok" : "not_found", null, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("write_failed", null, null);
        }
    }

    private string? Refuse(string? expectedEpoch)
        => _catalog is null ? "unavailable" : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";

    private async ValueTask<ProjectDescriptor?> ResolveAsync(string projectId, CancellationToken cancellationToken)
    {
        if (!GitIssuesService.Identity(projectId)) return null;
        var projects = await _projects!.LoadAsync(cancellationToken).ConfigureAwait(false);
        return projects.FirstOrDefault(project => string.Equals(project.Id, projectId, StringComparison.Ordinal)) is { Archived: false } found && Directory.Exists(found.ProjectPath) ? found : null;
    }

    private static PullRequestPromptItem Item(PullRequestPrompt prompt)
        => new(prompt.Id, prompt.Name, prompt.Description, prompt.Source switch { PullRequestPromptSource.Global => "global", PullRequestPromptSource.Project => "project", _ => "builtin" },
            prompt.Overridden, prompt.Content, prompt.Path);
}

/// <summary>Asks for the kinds of pull request.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="ProjectId">The project whose kinds are added to the user's; null for the user's alone.</param>
/// <param name="All">Whether the files a nearer one replaces are listed too, as Settings shows them.</param>
internal sealed record PullRequestPromptsRequest(string ExpectedEpoch, string? ProjectId, bool All = false);

/// <summary>The kinds. Status is <c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c> or <c>not_found</c> (the project).</summary>
internal sealed record PullRequestPromptsResponse(string Status, IReadOnlyList<PullRequestPromptItem> Items);

/// <summary>One kind of pull request.</summary>
/// <param name="Id">The name of its file without <c>.pr.md</c>.</param>
/// <param name="Name">The name shown to the user.</param>
/// <param name="Description">One line that says what it does.</param>
/// <param name="Source"><c>builtin</c>, <c>global</c> or <c>project</c>.</param>
/// <param name="Overridden">Whether a nearer file of the same id is used in its place.</param>
/// <param name="Content">The instructions sent to the session.</param>
/// <param name="File">The path of its file; null for what ships with CodeAlta.</param>
internal sealed record PullRequestPromptItem(string Id, string Name, string? Description, string Source, bool Overridden, string Content, string? File);

/// <summary>Writes a kind: of the user when <paramref name="ProjectId"/> is null, of that project otherwise.</summary>
internal sealed record PullRequestPromptSaveRequest(string ExpectedEpoch, string? ProjectId, string Id, string? Name, string? Description, string? Content);

/// <summary>Status is <c>ok</c>, <c>invalid</c> (with a message), <c>not_found</c>, <c>write_failed</c>, <c>unavailable</c> or <c>stale_epoch</c>.</summary>
internal sealed record PullRequestPromptSaveResponse(string Status, string? Message, PullRequestPromptItem? Item);

/// <summary>Names a kind of the user (no project) or of a project to delete.</summary>
internal sealed record PullRequestPromptDeleteRequest(string ExpectedEpoch, string? ProjectId, string? Id);
