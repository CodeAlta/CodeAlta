using System.Net;
using CodeAlta.Browser.Tools;

namespace CodeAlta.Plugin.Browser;

/// <summary>A page of the project of a session: where it comes from, and the folder its files are in.</summary>
/// <param name="Root">The folder the server of the page serves or runs in.</param>
/// <param name="Description">Where the page comes from, for the agent: the file server of CodeAlta, or the program of its port.</param>
internal sealed record ProjectPage(string Root, string Description);

/// <summary>
/// Tells whether a page belongs to the project of a session, which is when the CodeAlta toolbar is offered: a file
/// CodeAlta serves from the folder of the project, or an address of the loopback whose port a program listens on that
/// runs inside that folder. Any other page (a site of the web, a server of another project) gets no toolbar.
/// </summary>
internal sealed class ProjectPages
{
    private readonly ProjectFileServer _files;
    private readonly IPortOwners _owners;

    /// <summary>Creates the guard.</summary>
    /// <param name="files">The file server of CodeAlta.</param>
    /// <param name="owners">Finds the program of a port.</param>
    public ProjectPages(ProjectFileServer files, IPortOwners owners)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(owners);
        _files = files;
        _owners = owners;
    }

    /// <summary>Gets the file server of CodeAlta.</summary>
    public ProjectFileServer Files => _files;

    /// <summary>What a page of the project is, or null when the page is not one of the project.</summary>
    /// <param name="url">The address of the page.</param>
    /// <param name="projectFolder">The folder the session works in.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    public async Task<ProjectPage?> FindAsync(Uri url, string projectFolder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFolder);
        var project = Real(projectFolder);
        if (_files.Map(url) is { } served)
        {
            return BrowserFiles.IsWithin(served.File, project) ? new ProjectPage(served.Root, "a file of the project served by CodeAlta") : null;
        }

        if (url.Scheme is not ("http" or "https") || !IsLoopback(url)) return null;
        // Asked at each page, never remembered: the port of a server of the project can be taken by another program as soon
        // as that server stops.
        if (await _owners.FindAsync(url.Host, url.Port, cancellationToken).ConfigureAwait(false) is not { } owner || owner.FolderIn(project) is not { } folder) return null;
        var relative = Path.GetRelativePath(project, folder).Replace('\\', '/');
        var command = owner.Command.Length > 120 ? owner.Command[..120] + "…" : owner.Command;
        var where = owner.WorkingDirectory is null ? "which names a file of the project" : "in " + (relative == "." ? "the project folder" : relative + "/");
        return new ProjectPage(folder, $"`{command}` (process {owner.ProcessId}) {where}");
    }

    private static string Real(string path)
    {
        try { return BrowserFiles.RealPath(path); }
        catch (IOException) { return Path.GetFullPath(path); }
    }

    private static bool IsLoopback(Uri url)
        => url.IsLoopback || (IPAddress.TryParse(url.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
}
