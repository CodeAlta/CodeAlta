using CodeAlta.Catalog.Worktrees;

namespace CodeAlta.Catalog.SessionMoves;

/// <summary>Where a folder is in a repository that other machines may have too.</summary>
/// <param name="Key">The repository key (<see cref="RepositoryKey"/>).</param>
/// <param name="Path">The path of the folder from the root of the checkout, with forward slashes; empty for the root.</param>
public sealed record RepositoryLocation(string Key, string Path);

/// <summary>
/// The key that recognizes one repository across machines: the normalized URL of its <c>origin</c> remote, or of its
/// first remote. <c>git@github.com:CodeAlta/CodeAlta.git</c>, <c>https://github.com/CodeAlta/CodeAlta</c> and
/// <c>ssh://git@github.com/codealta/codealta.git</c> are one key, <c>github.com/codealta/codealta</c>.
/// </summary>
/// <remarks>
/// The scheme, the user, a <c>.git</c> suffix and a trailing slash are dropped; the host is in lower case and its default
/// port dropped; the path keeps its case, except on the hosts that ignore it (GitHub, GitLab, Bitbucket, Azure DevOps).
/// The forms of Azure DevOps (<c>_git</c>, <c>ssh.dev.azure.com:v3</c>, <c>*.visualstudio.com</c>) give one key. A local
/// path is a key of its own, with forward slashes, in lower case on Windows.
/// </remarks>
public static class RepositoryKey
{
    private static readonly string[] CaseInsensitiveHosts = ["github.com", "gitlab.com", "bitbucket.org", "dev.azure.com"];

    /// <summary>Normalizes the URL of a remote into a repository key.</summary>
    /// <param name="remoteUrl">The URL as git has it (<c>git remote get-url</c>).</param>
    /// <returns>The key; <see langword="null"/> when the URL is blank or names no repository.</returns>
    public static string? Normalize(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return null;
        var url = remoteUrl.Trim();
        string? host;
        string path;
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
        {
            var name = url[..scheme].ToLowerInvariant();
            var rest = url[(scheme + 3)..];
            if (name == "file") return LocalKey(rest.StartsWith('/') && rest.Length > 2 && rest[2] == ':' ? rest[1..] : rest);
            var slash = rest.IndexOf('/');
            var authority = slash < 0 ? rest : rest[..slash];
            path = slash < 0 ? string.Empty : rest[(slash + 1)..];
            var at = authority.LastIndexOf('@');
            if (at >= 0) authority = authority[(at + 1)..];
            host = HostWithoutDefaultPort(authority, name);
        }
        else if (IsScpLike(url, out var colon))
        {
            var authority = url[..colon];
            var at = authority.LastIndexOf('@');
            host = (at >= 0 ? authority[(at + 1)..] : authority).ToLowerInvariant();
            path = url[(colon + 1)..];
        }
        else
        {
            return LocalKey(url);
        }

        if (string.IsNullOrEmpty(host)) return null;
        var segments = TrimPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count == 0) return null;
        (host, segments) = UnifyAzureDevOps(host, segments);
        var key = host + "/" + string.Join('/', segments);
        return IsCaseInsensitive(host) ? key.ToLowerInvariant() : key;
    }

    /// <summary>Reads the repository key of the checkout a folder is in, from its <c>origin</c> remote or its first remote.</summary>
    /// <param name="folder">A folder of the checkout.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The key; <see langword="null"/> when the folder is in no repository or the repository has no remote.</returns>
    /// <exception cref="ArgumentException"><paramref name="folder"/> is blank.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public static async Task<string?> ReadAsync(string folder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Directory.Exists(folder)) return null;
        var origin = await GitWorktreeService.RunGitAsync(folder, ["remote", "get-url", "origin"], GitWorktreeService.ReadTimeout, cancellationToken).ConfigureAwait(false);
        if (origin.Succeeded) return Normalize(FirstLine(origin.Output));
        var remotes = await GitWorktreeService.RunGitAsync(folder, ["remote"], GitWorktreeService.ReadTimeout, cancellationToken).ConfigureAwait(false);
        if (!remotes.Succeeded || FirstLine(remotes.Output) is not { } first) return null;
        var url = await GitWorktreeService.RunGitAsync(folder, ["remote", "get-url", first], GitWorktreeService.ReadTimeout, cancellationToken).ConfigureAwait(false);
        return url.Succeeded ? Normalize(FirstLine(url.Output)) : null;
    }

    /// <summary>
    /// Reads where a folder is in its repository: the repository key and the path of the folder from the root of the
    /// checkout, which tells a project of a subfolder of a monorepo from the repository itself.
    /// </summary>
    /// <param name="folder">The folder of a project.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The location; <see langword="null"/> when the folder is in no repository or the repository has no remote.</returns>
    /// <exception cref="ArgumentException"><paramref name="folder"/> is blank.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public static async Task<RepositoryLocation?> ReadLocationAsync(string folder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Directory.Exists(folder)) return null;
        var prefix = await GitWorktreeService.RunGitAsync(folder, ["rev-parse", "--show-prefix"], GitWorktreeService.ReadTimeout, cancellationToken).ConfigureAwait(false);
        if (!prefix.Succeeded) return null;
        if (await ReadAsync(folder, cancellationToken).ConfigureAwait(false) is not { } key) return null;
        return new RepositoryLocation(key, (FirstLine(prefix.Output) ?? string.Empty).Replace('\\', '/').Trim('/'));
    }

    /// <summary>Whether two locations are the same folder of the same repository, wherever each machine keeps its checkout.</summary>
    /// <param name="left">A location.</param>
    /// <param name="right">Another location.</param>
    /// <returns>Whether both name a location and they are the same one.</returns>
    public static bool SameLocation(RepositoryLocation? left, RepositoryLocation? right)
        => left is not null && right is not null && string.Equals(left.Key, right.Key, StringComparison.Ordinal)
            // A folder of a checkout is named in the case of the file system that has it, which may ignore case.
            && string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);

    private static string? FirstLine(string output)
        => output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    // user@host:path, but not a Windows drive (C:\x, C:/x) and not a path whose first slash comes before the colon.
    private static bool IsScpLike(string url, out int colon)
    {
        colon = url.IndexOf(':');
        if (colon <= 1) return false;
        var slash = url.IndexOfAny(['/', '\\']);
        return slash < 0 || slash > colon;
    }

    private static string HostWithoutDefaultPort(string authority, string scheme)
    {
        var host = authority.ToLowerInvariant();
        var colon = host.LastIndexOf(':');
        if (colon < 0 || host.EndsWith(']')) return host;
        var port = host[(colon + 1)..];
        var isDefault = port.Length == 0 || (scheme, port) is ("ssh", "22") or ("git+ssh", "22") or ("https", "443") or ("http", "80") or ("git", "9418");
        return isDefault ? host[..colon] : host;
    }

    private static string TrimPath(string path)
    {
        var trimmed = path.Replace('\\', '/').Trim('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^4];
        return trimmed.TrimEnd('/');
    }

    private static string? LocalKey(string path)
    {
        var trimmed = path.Replace('\\', '/').TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^4].TrimEnd('/');
        if (trimmed.Length == 0) return null;
        return OperatingSystem.IsWindows() ? trimmed.ToLowerInvariant() : trimmed;
    }

    private static (string Host, List<string> Segments) UnifyAzureDevOps(string host, List<string> segments)
    {
        // ssh.dev.azure.com:v3/org/project/repo, vs-ssh.visualstudio.com:v3/org/project/repo
        if (host is "ssh.dev.azure.com" or "vs-ssh.visualstudio.com" && segments.Count >= 4 && segments[0] == "v3")
            return ("dev.azure.com", segments[1..]);
        if (host == "dev.azure.com")
            return (host, [.. segments.Where(static segment => segment != "_git")]);
        // org.visualstudio.com/[DefaultCollection/]project/_git/repo
        if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
        {
            var organization = host[..^".visualstudio.com".Length];
            var rest = segments.Where(static segment => segment != "_git").ToList();
            if (rest.Count > 0 && string.Equals(rest[0], "DefaultCollection", StringComparison.OrdinalIgnoreCase)) rest.RemoveAt(0);
            return ("dev.azure.com", [organization, .. rest]);
        }

        return (host, segments);
    }

    private static bool IsCaseInsensitive(string host)
        => CaseInsensitiveHosts.Contains(host.Split(':')[0], StringComparer.Ordinal);
}
