using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Catalog.Worktrees;

namespace CodeAlta.Fleet.Moves;

/// <summary>
/// The clones a machine makes of a repository it lacks: for a session moved to it (<see cref="FleetMoveReceiver"/>) and
/// for <c>altanode projects clone</c>. A clone goes to <c>&lt;root&gt;/&lt;host&gt;/&lt;owner&gt;/&lt;repo&gt;</c>, the repository key
/// under the clone root, and only from a URL the machine fetches from the network.
/// </summary>
internal static class FleetRepositoryClone
{
    /// <summary>The longest URL a clone takes.</summary>
    internal const int MaxUrlLength = 2048;

    /// <summary>
    /// Where a repository is cloned: <c>&lt;root&gt;/&lt;key&gt;</c>, when the key is <c>host/owner/repo</c>, is the key of the URL,
    /// and the URL is one a machine fetches from the network (never a path of the machine, which a caller must not make it read).
    /// </summary>
    /// <param name="cloneRoot">The clone root; null clones nothing.</param>
    /// <param name="url">The URL of the repository.</param>
    /// <param name="key">Its repository key (<see cref="RepositoryKey.Normalize"/>).</param>
    /// <returns>The folder, or null when the repository cannot be cloned there.</returns>
    public static string? PathFor(string? cloneRoot, string? url, string? key)
    {
        if (cloneRoot is null || key is null || url is not { Length: > 0 and <= MaxUrlLength } || !IsNetworkUrl(url)) return null;
        if (RepositoryKey.Normalize(url) != key) return null;
        var segments = key.Split('/');
        if (segments.Length < 2 || segments.Any(static segment => segment is "" or "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return null;
        var path = Path.GetFullPath(Path.Combine([cloneRoot, .. segments]));
        return GitWorktreeService.IsWithin(path, cloneRoot) ? path : null;
    }

    /// <summary>Whether a URL is one a machine fetches from the network: https, http, ssh, git, or scp-like <c>user@host:owner/repo</c>.</summary>
    /// <param name="url">The URL.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsNetworkUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Scheme is "https" or "http" or "ssh" or "git" && !string.IsNullOrEmpty(uri.Host);
        // scp-like: user@host:owner/repo
        var colon = url.IndexOf(':', StringComparison.Ordinal);
        var at = url.IndexOf('@', StringComparison.Ordinal);
        return colon > 0 && at > 0 && at < colon && !url.Contains('\\', StringComparison.Ordinal);
    }

    /// <summary>
    /// Clones a repository into a folder of <see cref="PathFor"/>, unless the folder holds files already; then checks that
    /// the folder is a checkout of that repository.
    /// </summary>
    /// <param name="url">The URL to clone.</param>
    /// <param name="path">The folder.</param>
    /// <param name="key">The repository key the checkout must have.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>Null when the folder is a checkout of the repository; otherwise why not, for people.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public static async Task<string?> CloneAsync(string url, string path, string key, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any())
        {
            var parent = Directory.CreateDirectory(Path.GetDirectoryName(path)!).FullName;
            var clone = await GitWorktreeService.RunGitAsync(parent, ["clone", "--", url, path], GitWorktreeService.WriteTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!clone.Succeeded) return (clone.Error.Length > 0 ? clone.Error : clone.Output).Trim();
        }

        return await RepositoryKey.ReadAsync(path, cancellationToken).ConfigureAwait(false) == key ? null : $"{path} holds another repository.";
    }
}
