using System.ComponentModel;
using System.Diagnostics;
using CodeAlta.Browser.Links;
using CodeAlta.Catalog;

namespace CodeAlta.Desktop;

/// <summary>Where a link was followed: the space, and the folder whose project configuration may set where its links open.</summary>
/// <param name="SpaceId">The space; null for the default space.</param>
/// <param name="ProjectFolder">The folder of the project or of the session; null for none.</param>
internal sealed record DesktopLinkOrigin(string? SpaceId, string? ProjectFolder);

/// <summary>
/// Opens an address of the web where the space of the session or the project it was followed from says (<see cref="SpaceLinks"/>),
/// with the setting of the project over the one of the space: a browser, and a profile of it. Without a setting, or when it
/// cannot be followed, the system opens the address as it opens any link (<see cref="DesktopLinks.Open"/>).
/// </summary>
internal sealed class DesktopLinkOpener
{
    private readonly SpaceCatalog _spaces;
    private readonly Func<string?, CancellationToken, Task<SpaceLinks?>> _projectLinks;
    private readonly Func<string?, string?, string?, CancellationToken, Task<DesktopLinkOrigin?>> _origin;
    private readonly Func<string, string?, string?, LinkLaunch?> _plan;
    private readonly Func<LinkLaunch, bool> _start;
    private readonly Func<string, bool> _open;

    /// <summary>Creates the opener of a host.</summary>
    /// <param name="spaces">The spaces, whose settings say where their links open.</param>
    /// <param name="projectLinks">Reads the setting of the configuration of a project, from its folder; null when it has none. It never throws.</param>
    /// <param name="origin">Tells where a link was followed, from the session, the project of its document, or the window that showed it; null when none is known.</param>
    /// <param name="plan">Says what to start to open an address in a browser and a profile (<see cref="LinkBrowsers.Plan"/>); null for the system.</param>
    /// <param name="start">Starts what <paramref name="plan"/> said; null for <see cref="Start"/>.</param>
    /// <param name="open">Opens an address as the system does; null for <see cref="DesktopLinks.Open"/>.</param>
    internal DesktopLinkOpener(SpaceCatalog spaces, Func<string?, CancellationToken, Task<SpaceLinks?>> projectLinks,
        Func<string?, string?, string?, CancellationToken, Task<DesktopLinkOrigin?>> origin, Func<string, string?, string?, LinkLaunch?> plan,
        Func<LinkLaunch, bool>? start = null, Func<string, bool>? open = null)
    {
        ArgumentNullException.ThrowIfNull(spaces);
        ArgumentNullException.ThrowIfNull(projectLinks);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(plan);
        (_spaces, _projectLinks, _origin, _plan, _start, _open) = (spaces, projectLinks, origin, plan, start ?? Start, open ?? DesktopLinks.Open);
    }

    /// <summary>
    /// Opens an address of the web (<see cref="DesktopLinks.WebAddress"/>) followed from a session or the document of a
    /// project, in the browser and the profile their space and project set.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <param name="sessionId">The session whose message has the link; null for none.</param>
    /// <param name="projectId">Without a session, the project whose document has the link; null for none.</param>
    /// <param name="originLabel">The label of the view of the window where the link was followed; null for none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>False when nothing could open it.</returns>
    /// <exception cref="OperationCanceledException">The request was canceled before anything was opened.</exception>
    internal async Task<bool> OpenAsync(string address, string? sessionId, string? projectId, string? originLabel, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        var links = await LinksAsync(sessionId, projectId, originLabel, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (links is { IsEmpty: false } && _plan(address, links.Browser, links.Profile) is { } launch && _start(launch)) return true;
        return _open(address);
    }

    /// <summary>The setting the links of a session or a project open with: the one of their project over the one of their space.</summary>
    /// <exception cref="OperationCanceledException">The request was canceled.</exception>
    internal async Task<SpaceLinks?> LinksAsync(string? sessionId, string? projectId, string? originLabel, CancellationToken cancellationToken)
    {
        try
        {
            var origin = await _origin(Blank(sessionId), Blank(projectId), Blank(originLabel), cancellationToken).ConfigureAwait(false);
            var space = await _spaces.GetAsync(origin?.SpaceId ?? SpaceDescriptor.DefaultId, cancellationToken).ConfigureAwait(false);
            var project = origin?.ProjectFolder is { } folder ? await _projectLinks(folder, cancellationToken).ConfigureAwait(false) : null;
            var links = (space?.Settings.Links ?? new SpaceLinks()).WithProject(project);
            return links.IsEmpty ? null : links;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A space or a project that cannot be read opens its links as the system does.
            return null;
        }
    }

    /// <summary>Starts a browser as <see cref="LinkBrowsers.Plan"/> said.</summary>
    /// <returns>False when it could not be started.</returns>
    internal static bool Start(LinkLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var start = new ProcessStartInfo(launch.FileName) { UseShellExecute = false };
        foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or PlatformNotSupportedException or IOException)
        {
            return false;
        }
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
