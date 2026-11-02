using CodeAlta.Catalog;
using CodeAlta.Catalog.StartPrompts;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The texts CodeAlta starts a session with when it works on an item, such as the review of a pull request: the
/// one that ships with CodeAlta, the user's and a project's. Settings lists them on its page of prompts, where the
/// user's and a project's are written.
/// </summary>
[NeoRpcService("startPrompts", Version = 1)]
internal sealed class StartPromptsService
{
    private readonly StartPromptCatalog? _catalog;
    private readonly ProjectCatalog? _projects;
    private readonly string? _epoch;

    /// <summary>Creates the service of a window whose host keeps no texts: it answers <c>unavailable</c>.</summary>
    public StartPromptsService()
    {
    }

    /// <summary>Creates the service of an owned host.</summary>
    internal StartPromptsService(StartPromptCatalog catalog, ProjectCatalog projects, string epoch)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_catalog, _projects, _epoch) = (catalog, projects, epoch);
    }

    /// <summary>Lists every text of every start prompt, the ones a nearer file replaces included.</summary>
    [NeoRpcMethod("list")]
    public async Task<StartPromptsResponse> ListAsync(StartPromptsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, []);
        string? path = null;
        if (request.ProjectId is not null)
        {
            if (await ResolveAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is not { } project) return new("not_found", []);
            path = project.ProjectPath;
        }

        return new("ok", [.. _catalog!.List(path).Select(Item)]);
    }

    /// <summary>Writes the text of a start prompt of the user (no project) or of a project.</summary>
    [NeoRpcMethod("save")]
    public async Task<StartPromptSaveResponse> SaveAsync(StartPromptSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (StartPromptCatalog.NormalizeId(request.Id) is not { } id) return new("invalid", "There is no such start prompt.", null);
        string? path = null;
        if (request.ProjectId is not null)
        {
            if (await ResolveAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is not { } project) return new("not_found", null, null);
            path = project.ProjectPath;
        }

        try
        {
            return new("ok", null, Item(_catalog!.Save(path, id, request.Content ?? string.Empty)));
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

    /// <summary>Deletes the text of the user or of a project: the next one is used again. What ships is not a file to delete.</summary>
    [NeoRpcMethod("delete")]
    public async Task<StartPromptSaveResponse> DeleteAsync(StartPromptDeleteRequest request, CancellationToken cancellationToken)
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

    private static StartPromptItem Item(StartPrompt prompt)
        => new(prompt.Id, prompt.Source switch { StartPromptSource.Global => "global", StartPromptSource.Project => "project", _ => "builtin" }, prompt.Overridden, prompt.Content, prompt.Path);
}

/// <summary>Asks for the texts of the start prompts.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="ProjectId">The project whose texts are listed with the user's; null for the user's alone.</param>
internal sealed record StartPromptsRequest(string ExpectedEpoch, string? ProjectId);

/// <summary>The texts. Status is <c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c> or <c>not_found</c> (the project).</summary>
internal sealed record StartPromptsResponse(string Status, IReadOnlyList<StartPromptItem> Items);

/// <summary>One text of a start prompt.</summary>
/// <param name="Id">The start prompt, such as <c>pull-request-review</c>.</param>
/// <param name="Source"><c>builtin</c>, <c>global</c> or <c>project</c>.</param>
/// <param name="Overridden">Whether a nearer file is used in its place.</param>
/// <param name="Content">The text, with its placeholders.</param>
/// <param name="File">The path of its file; null for what ships with CodeAlta.</param>
internal sealed record StartPromptItem(string Id, string Source, bool Overridden, string Content, string? File);

/// <summary>Writes a text: of the user when <paramref name="ProjectId"/> is null, of that project otherwise.</summary>
internal sealed record StartPromptSaveRequest(string ExpectedEpoch, string? ProjectId, string Id, string? Content);

/// <summary>Status is <c>ok</c>, <c>invalid</c> (with a message), <c>not_found</c>, <c>write_failed</c>, <c>unavailable</c> or <c>stale_epoch</c>.</summary>
internal sealed record StartPromptSaveResponse(string Status, string? Message, StartPromptItem? Item);

/// <summary>Names a text of the user (no project) or of a project to delete.</summary>
internal sealed record StartPromptDeleteRequest(string ExpectedEpoch, string? ProjectId, string? Id);
