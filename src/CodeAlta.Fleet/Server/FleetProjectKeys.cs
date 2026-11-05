using CodeAlta.Catalog;
using CodeAlta.Catalog.SessionMoves;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// The key of a project in the statistics a machine publishes (<c>doc/fleet/statistics.md</c>, dimensions): a blind index
/// of what recognizes the project on every machine of its person, its repository and its folder in it, so that the
/// server sums a project across machines without learning its name, and the person's devices name it again.
/// </summary>
public static class FleetProjectKeys
{
    /// <summary>The purpose of the blind index of a project (<see cref="FleetMetadataKey.BlindIndex"/>).</summary>
    public const string Purpose = "project";

    /// <summary>
    /// Gives what recognizes a project on the person's machines: <c>repo:</c> and its repository location (the normalized
    /// remote and the folder in the checkout) when its folder is in a repository with a remote, else <c>project:</c> and its id.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="repositories">The repository locations of folders, read with git and kept.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The identity; null for a project without an id.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public static async Task<string?> IdentityAsync(ProjectDescriptor project, RepositoryLocationCache repositories, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(repositories);
        if (!string.IsNullOrWhiteSpace(project.ProjectPath))
        {
            try
            {
                if (await repositories.GetAsync(project.ProjectPath, cancellationToken).ConfigureAwait(false) is { } location)
                    return "repo:" + location.Key + (location.Path.Length == 0 ? string.Empty : "/" + location.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                // Git could not be asked: the project is known by its id on this machine.
            }
        }

        return string.IsNullOrWhiteSpace(project.Id) ? null : "project:" + project.Id;
    }

    /// <summary>Gives the key of a project: the blind index of its identity with the person's metadata key.</summary>
    /// <param name="key">The metadata key of the person.</param>
    /// <param name="identity">The identity of the project (<see cref="IdentityAsync"/>).</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="identity"/> is empty.</exception>
    public static string Of(FleetMetadataKey key, string identity)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.BlindIndex(Purpose, identity);
    }
}
