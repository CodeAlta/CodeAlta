using CodeAlta.Catalog;
using CodeAlta.Catalog.PullRequests;
using CodeAlta.Catalog.Skills;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The files and the folders behind the pages of Settings: where each one is, and the two things the page does
/// with it, which are to open it in the code editor and to show it in the file manager of the system.
/// </summary>
/// <remarks>
/// <para>
/// The page never names a path. It names what it wants by kind (<c>config</c>, <c>mcp</c>, <c>prompts</c>,
/// <c>prompt</c>, <c>skills</c>, <c>plugins</c>, <c>colorSchemes</c>, <c>pullRequests</c>, <c>pullRequest</c>), scope
/// and id, and the host finds the path as the service of that page does. The path a listing returns is shown,
/// never read back.
/// </para>
/// <para>
/// A file inside the folder of a project opens in the code editor of that project. A folder opens in a tab of
/// its own (<see cref="DiskFolders"/>), and a file of such a folder in that tab. A file whose folder holds more
/// than settings (the <c>config.toml</c> and <c>mcp.json</c> of <c>~/.alta</c>, beside the stored credentials)
/// opens in a tab that has that file alone. What ships with the application is only read.
/// </para>
/// </remarks>
[NeoRpcService("settingsFiles", Version = 1)]
internal sealed class SettingsFilesService
{
    private const string Global = "Global";
    private const string Project = "Project";
    private const string BuiltIn = "BuiltIn";
    private const int MaximumNameLength = 128;

    private readonly ProjectCatalog? _projects;
    private readonly string? _epoch;
    private readonly DiskFolders? _folders;
    private readonly DesktopEditorView? _view;
    private readonly AgentPromptsService? _prompts;
    private readonly SkillsService? _skills;
    private readonly McpServersService? _mcp;
    private readonly PullRequestPromptCatalog? _pullRequests;
    private readonly Func<string, bool> _reveal;

    // One entry of a page: how the page names it, and what it is on disk.
    private readonly record struct Entry(string Kind, string Scope, string? Id, string Path, bool Folder, bool Owned = false, bool ReadOnly = false, bool Alone = false, string? Root = null);

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal SettingsFilesService()
    {
        _reveal = DesktopFileReveal.Show;
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="projects">The project catalog of the host; its global root holds the files of the user.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="folders">Gives an id to a folder, or to a file alone, that is no project.</param>
    /// <param name="view">Passes what to show to the window.</param>
    /// <param name="prompts">Finds the prompt files and their folders, or null when the page has none.</param>
    /// <param name="skills">Finds the folders of the skills, or null when the page has none.</param>
    /// <param name="mcp">Finds the files of the MCP servers, or null when the page has none.</param>
    /// <param name="pullRequests">Finds the instructions for pull requests, or null when the page has none.</param>
    /// <param name="reveal">Shows an entry in the file manager; the one of the system by default.</param>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/>, <paramref name="folders"/> or <paramref name="view"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal SettingsFilesService(ProjectCatalog projects, string epoch, DiskFolders folders, DesktopEditorView view, AgentPromptsService? prompts = null,
        SkillsService? skills = null, McpServersService? mcp = null, PullRequestPromptCatalog? pullRequests = null, Func<string, bool>? reveal = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(view);
        (_projects, _epoch, _folders, _view, _prompts, _skills, _mcp, _pullRequests) = (projects, epoch, folders, view, prompts, skills, mcp, pullRequests);
        _reveal = reveal ?? DesktopFileReveal.Show;
    }

    /// <summary>
    /// Lists the files and the folders a page reads: <c>config</c>, <c>mcp</c>, <c>prompts</c>, <c>skills</c>,
    /// <c>plugins</c>, <c>colorSchemes</c> or <c>pullRequests</c>. Those of a project are listed with that project.
    /// </summary>
    [NeoRpcMethod("list")]
    public async Task<SettingsFilesListResponse> ListAsync(SettingsFilesListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SettingsFilesListResponse Failed(string status) => new(status, [], null);
        if (_projects is null) return Failed("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Failed("stale_epoch");
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return Failed(project.Status);
        try
        {
            var platform = !DesktopFileReveal.Available ? null : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
            return new("ok", [.. Page(request.Page, project.Root).Select(Location)], platform);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Failed("read_failed"); // A configuration file of another tool that cannot be read, a path that is none.
        }
    }

    /// <summary>
    /// Opens a file or a folder in the code editor and answers <c>ok</c>, <c>not_found</c> when it is not there
    /// (a folder CodeAlta owns is created), <c>invalid</c> for what no page lists, or <c>failed</c> when no window
    /// could show it.
    /// </summary>
    [NeoRpcMethod("open")]
    public async Task<SettingsFileResponse> OpenAsync(SettingsFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (status, found) = await FindAsync(request, cancellationToken).ConfigureAwait(false);
        if (found is not { } entry) return new(status);
        try
        {
            if (entry.Folder)
            {
                if (!Directory.Exists(entry.Path))
                {
                    if (!entry.Owned) return new("not_found");
                    Directory.CreateDirectory(entry.Path);
                }

                return Shown(_view!.OpenFolder(_folders!.Give(entry.Path, entry.ReadOnly), null, null, null));
            }

            if (!File.Exists(entry.Path)) return new("not_found");
            var full = Path.GetFullPath(entry.Path);
            // What ships with the application is not edited through a project that happens to hold it.
            if (!entry.ReadOnly && await DesktopFileLinks.ProjectOfAsync(_projects!, full, cancellationToken).ConfigureAwait(false) is { } project
                && Inside(project.Root, full) is { } file)
            {
                return Shown(_view!.Open(project.Id, file, null, null));
            }

            if (!entry.Alone && entry.Root is { } root && Inside(root, full) is { } below) return Shown(_view!.OpenFolder(_folders!.Give(root, entry.ReadOnly), below, null, null));
            // The folder of the file holds more than settings: the tab has that file and nothing else.
            return Shown(_view!.OpenFolder(_folders!.GiveFile(full), Path.GetFileName(full), null, null));
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return new("failed");
        }
    }

    /// <summary>Shows a file or a folder in the file manager of the system; <c>not_found</c> when it is not there.</summary>
    [NeoRpcMethod("reveal")]
    public async Task<SettingsFileResponse> RevealAsync(SettingsFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (status, found) = await FindAsync(request, cancellationToken).ConfigureAwait(false);
        if (found is not { } entry) return new(status);
        if (!(entry.Folder ? Directory.Exists(entry.Path) : File.Exists(entry.Path))) return new("not_found");
        return Shown(_reveal(Path.GetFullPath(entry.Path)));
    }

    private static SettingsFileResponse Shown(bool shown) => new(shown ? "ok" : "failed");

    // What a request names, as the pages list it; or why it names nothing.
    private async Task<(string Status, Entry? Entry)> FindAsync(SettingsFileRequest request, CancellationToken cancellationToken)
    {
        if (_projects is null || _folders is null || _view is null) return ("unavailable", null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return ("stale_epoch", null);
        if (request.Kind is not { Length: > 0 and <= 32 } kind || request.Scope is not { Length: > 0 and <= 32 } scope
            || request.Id is { Length: > MaximumNameLength } || request.Part is { Length: > 32 }) return ("invalid", null);
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return (project.Status, null);
        try
        {
            Entry? entry = kind switch
            {
                "prompt" => Prompt(request.Part, scope, request.Id, project.Root),
                "pullRequest" => PullRequest(scope, request.Id, project.Root),
                // "config" is a page and the kind of its files; the kind of the others is the name of their page.
                _ => Page(kind, project.Root).Where(entry => entry.Kind == kind && entry.Scope == scope && string.Equals(entry.Id, request.Id, StringComparison.Ordinal))
                    .Select(static entry => (Entry?)entry).FirstOrDefault(),
            };
            return entry is null ? ("invalid", null) : ("ok", entry);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return ("failed", null);
        }
    }

    // The files and the folders of a page, in the order the page shows them.
    private IEnumerable<Entry> Page(string? page, string? projectRoot)
    {
        var global = _projects!.Options.GlobalRoot;
        switch (page)
        {
            case "config":
                yield return new("config", Global, null, _projects.Options.ConfigPath, Folder: false, Alone: true);
                if (projectRoot is not null) yield return new("config", Project, null, Path.Combine(projectRoot, ".alta", "config.toml"), Folder: false, Alone: true);
                break;
            case "mcp":
                // A file of the user is beside other things (credentials in ~/.alta, the settings of another tool).
                foreach (var file in _mcp?.Files(projectRoot) ?? []) yield return new("mcp", file.Scope, file.Origin, file.Path, Folder: false, Alone: true);
                break;
            case "prompts":
                if (_prompts is null) break;
                var roots = _prompts.Roots(projectRoot);
                yield return new("prompts", Global, "CodeAlta", roots.GlobalPromptRoot, Folder: true, Owned: true);
                if (roots.ProjectPromptRoot is { } projectPrompts) yield return new("prompts", Project, "CodeAlta", projectPrompts, Folder: true, Owned: true);
                if (roots.CopilotUserAgentsRoot is { } userAgents && Directory.Exists(userAgents)) yield return new("prompts", Global, "Copilot", userAgents, Folder: true);
                if (roots.CopilotProjectAgentsRoot is { } projectAgents && Directory.Exists(projectAgents)) yield return new("prompts", Project, "Copilot", projectAgents, Folder: true);
                yield return new("prompts", BuiltIn, "CodeAlta", roots.ShippedPromptRoot, Folder: true, ReadOnly: true);
                break;
            case "skills":
                foreach (var root in _skills?.Roots(projectRoot) ?? [])
                {
                    // The folders of CodeAlta are listed before they exist: opening one creates it.
                    var owned = root.Source is SkillSourceKind.UserAlta or SkillSourceKind.ProjectAlta;
                    if (!owned && !Directory.Exists(root.RootPath)) continue;
                    yield return new("skills", root.Source is SkillSourceKind.UserAlta or SkillSourceKind.UserCommon or SkillSourceKind.UserCopilot ? Global : Project,
                        root.Source.ToString(), root.RootPath, Folder: true, Owned: owned);
                }

                break;
            case "plugins":
                yield return new("plugins", Global, null, Path.Combine(global, "plugins"), Folder: true, Owned: true);
                if (projectRoot is not null) yield return new("plugins", Project, null, Path.Combine(projectRoot, ".alta", "plugins"), Folder: true, Owned: true);
                break;
            case "colorSchemes":
                yield return new("colorSchemes", Global, null, Path.Combine(global, ColorSchemesService.FolderName), Folder: true, Owned: true);
                break;
            case "pullRequests":
                if (_pullRequests is null) break;
                yield return new("pullRequests", Global, null, _pullRequests.GlobalFolder, Folder: true, Owned: true);
                if (projectRoot is not null) yield return new("pullRequests", Project, null, PullRequestPromptCatalog.ProjectFolder(projectRoot), Folder: true, Owned: true);
                break;
        }
    }

    // The file of a listed prompt, in the folder of prompts it is read from.
    private Entry? Prompt(string? part, string scope, string? id, string? projectRoot)
        => _prompts?.Locate(part, scope, id, projectRoot) is { } prompt
            ? new("prompt", scope, id, prompt.File, Folder: false, ReadOnly: prompt.ReadOnly, Root: prompt.Root) : null;

    // The file of a kind of pull request of the user or of a project, in the folder of those files.
    private Entry? PullRequest(string scope, string? id, string? projectRoot)
    {
        if (_pullRequests is null || PullRequestPromptCatalog.NormalizeId(id) is null) return null;
        var project = string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase);
        if (!project && !string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase) || project && projectRoot is null) return null;
        return _pullRequests.List(projectRoot).FirstOrDefault(prompt => prompt.Path is not null && string.Equals(prompt.Id, id, StringComparison.OrdinalIgnoreCase)
                && prompt.Source == (project ? PullRequestPromptSource.Project : PullRequestPromptSource.Global)) is { Path: { } path }
            ? new("pullRequest", scope, id, path, Folder: false, Root: project ? PullRequestPromptCatalog.ProjectFolder(projectRoot!) : _pullRequests.GlobalFolder) : null;
    }

    private static SettingsFileLocation Location(Entry entry)
    {
        var exists = entry.Folder ? Directory.Exists(entry.Path) : File.Exists(entry.Path);
        return new(entry.Kind, entry.Scope, entry.Id, entry.Path, entry.Folder, exists, exists || (entry.Folder && entry.Owned), entry.ReadOnly);
    }

    // The path of a file below a folder as the code editor names it, or null when it is not below it.
    private static string? Inside(string root, string full)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), full);
        if (relative == "." || Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal)) return null;
        return ProjectFilesService.Normalize(relative.Replace(Path.DirectorySeparatorChar, '/'), out var path) == "ok" ? path : null;
    }

    private static bool IsStorageFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidDataException or System.Text.Json.JsonException;
}

/// <summary>Asks for the files and the folders of a page of Settings.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The selected project, whose files are listed too; null for those of the user alone.</param>
/// <param name="Page"><c>config</c>, <c>mcp</c>, <c>prompts</c>, <c>skills</c>, <c>plugins</c>, <c>colorSchemes</c> or <c>pullRequests</c>.</param>
internal sealed record SettingsFilesListRequest(string? ExpectedEpoch, string? ProjectId, string? Page);

/// <summary>The files and the folders of a page.</summary>
/// <param name="Status"><c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c>, <c>read_failed</c>, or why the project is not available.</param>
/// <param name="Locations">The files and the folders, in the order they are shown.</param>
/// <param name="Platform"><c>windows</c>, <c>macos</c> or <c>linux</c>, which names the file manager; null when none can be opened.</param>
internal sealed record SettingsFilesListResponse(string Status, IReadOnlyList<SettingsFileLocation> Locations, string? Platform);

/// <summary>One file or folder of a page, with what names it in a request to open it.</summary>
/// <param name="Kind">The kind of the entry: the name of its page.</param>
/// <param name="Scope"><c>Global</c>, <c>Project</c> or <c>BuiltIn</c>.</param>
/// <param name="Id">What tells it from the others of its kind and scope: whose file it is, or the source of the skills; null when nothing does.</param>
/// <param name="Path">The full path, to be shown.</param>
/// <param name="Folder">Whether it is a folder.</param>
/// <param name="Exists">Whether it is on disk.</param>
/// <param name="CanOpen">Whether it can be opened: it is there, or it is a folder CodeAlta creates.</param>
/// <param name="ReadOnly">Whether it is only read: it ships with the application.</param>
internal sealed record SettingsFileLocation(string Kind, string Scope, string? Id, string Path, bool Folder, bool Exists, bool CanOpen, bool ReadOnly = false);

/// <summary>Names a file or a folder of a page; a prompt (<c>prompt</c>) and a kind of pull request (<c>pullRequest</c>) by their id.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The selected project; required for what a project has.</param>
/// <param name="Kind">The kind, as a listing gives it, or <c>prompt</c> or <c>pullRequest</c>.</param>
/// <param name="Scope">The scope, as a listing gives it; the scope of the prompt or the source of the kind of pull request.</param>
/// <param name="Id">The id, as a listing gives it; the id of the prompt or of the kind of pull request.</param>
/// <param name="Part">For a prompt, <c>Agent</c> or <c>System</c>.</param>
internal sealed record SettingsFileRequest(string? ExpectedEpoch, string? ProjectId, string? Kind, string? Scope, string? Id = null, string? Part = null);

/// <summary>The answer to a request to open or to show: <c>ok</c>, <c>not_found</c>, <c>invalid</c>, <c>failed</c>, or why the host or the project is not available.</summary>
internal sealed record SettingsFileResponse(string Status);
