using System.Diagnostics.CodeAnalysis;

namespace CodeAlta.Plugin.Git;

/// <summary>Identifies the service that hosts a git remote.</summary>
public enum GitRemoteProvider
{
    /// <summary>github.com.</summary>
    GitHub,

    /// <summary>gitlab.com or a self-managed GitLab instance.</summary>
    GitLab,

    /// <summary>Azure DevOps Services (dev.azure.com).</summary>
    AzureDevOps,
}

/// <summary>Display helpers for <see cref="GitRemoteProvider"/>.</summary>
public static class GitRemoteProviderExtensions
{
    /// <summary>Gets the name of the provider as its users write it.</summary>
    /// <param name="provider">The provider.</param>
    /// <returns><c>GitHub</c>, <c>GitLab</c> or <c>Azure DevOps</c>.</returns>
    public static string GetDisplayName(this GitRemoteProvider provider)
        => provider switch
        {
            GitRemoteProvider.GitLab => "GitLab",
            GitRemoteProvider.AzureDevOps => "Azure DevOps",
            _ => "GitHub",
        };

    /// <summary>Gets what the provider calls the items of its tracker, in the plural.</summary>
    /// <param name="provider">The provider.</param>
    /// <returns><c>work items</c> for Azure DevOps, <c>issues</c> otherwise.</returns>
    public static string GetIssueNoun(this GitRemoteProvider provider)
        => provider == GitRemoteProvider.AzureDevOps ? "work items" : "issues";
}

/// <summary>Identifies a repository hosted by a supported provider.</summary>
/// <param name="Provider">The hosting provider.</param>
/// <param name="Host">The web host of the provider instance, such as <c>github.com</c>, <c>gitlab.example.com</c> or <c>dev.azure.com</c>.</param>
/// <param name="Owner">The GitHub owner, the GitLab namespace (groups separated by <c>/</c>) or the Azure DevOps organization.</param>
/// <param name="Name">The repository name.</param>
public sealed record GitRepositoryReference(GitRemoteProvider Provider, string Host, string Owner, string Name)
{
    /// <summary>Gets the Azure DevOps project that owns the repository and its work items; null for the other providers.</summary>
    public string? Project { get; init; }

    /// <summary>Gets the <c>owner/name</c> form (<c>organization/project/name</c> for Azure DevOps).</summary>
    public string FullName => Project is null ? Owner + "/" + Name : Owner + "/" + Project + "/" + Name;

    /// <summary>Creates a reference to a github.com repository.</summary>
    /// <param name="owner">The owning user or organization.</param>
    /// <param name="name">The repository name.</param>
    /// <returns>The reference.</returns>
    public static GitRepositoryReference GitHub(string owner, string name)
        => new(GitRemoteProvider.GitHub, GitRemoteUrl.GitHubHost, owner, name);

    /// <summary>Creates a reference to a GitLab project.</summary>
    /// <param name="host">The GitLab host, such as <c>gitlab.com</c>.</param>
    /// <param name="namespacePath">The group path, with subgroups separated by <c>/</c>.</param>
    /// <param name="name">The project name.</param>
    /// <returns>The reference.</returns>
    public static GitRepositoryReference GitLab(string host, string namespacePath, string name)
        => new(GitRemoteProvider.GitLab, host, namespacePath, name);

    /// <summary>Creates a reference to an Azure DevOps Services repository.</summary>
    /// <param name="organization">The organization.</param>
    /// <param name="project">The project that owns the repository.</param>
    /// <param name="name">The repository name.</param>
    /// <returns>The reference.</returns>
    public static GitRepositoryReference AzureDevOps(string organization, string project, string name)
        => new(GitRemoteProvider.AzureDevOps, GitRemoteUrl.AzureDevOpsHost, organization, name) { Project = project };
}

/// <summary>Recognizes the remote URLs of the supported providers.</summary>
internal static class GitRemoteUrl
{
    public const string GitHubHost = "github.com";
    public const string GitLabHost = "gitlab.com";
    public const string AzureDevOpsHost = "dev.azure.com";

    private const int MaximumPartLength = 100;
    private const string LegacyAzureDevOpsSuffix = ".visualstudio.com";

    /// <summary>
    /// Reads the hosts glab is configured for through <c>GITLAB_HOST</c>, <c>GITLAB_URI</c> and <c>GL_HOST</c>:
    /// the self-managed instances whose name does not say they are GitLab.
    /// </summary>
    public static IReadOnlyList<string> ReadGitLabHostsFromEnvironment()
    {
        var hosts = new List<string>();
        foreach (var variable in (ReadOnlySpan<string>)["GITLAB_HOST", "GITLAB_URI", "GL_HOST"])
        {
            if (TryNormalizeHost(Environment.GetEnvironmentVariable(variable), out var host))
            {
                hosts.Add(host);
            }
        }

        return hosts;
    }

    /// <summary>Reduces a host name or URL (<c>https://gitlab.example.com/</c>) to its host name.</summary>
    public static bool TryNormalizeHost(string? value, [NotNullWhen(true)] out string? host)
    {
        host = null;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.Contains("://", StringComparison.Ordinal))
        {
            text = Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
        }
        else
        {
            var end = text.IndexOfAny(['/', ':']);
            text = end >= 0 ? text[..end] : text;
        }

        if (!IsHostName(text))
        {
            return false;
        }

        host = text.ToLowerInvariant();
        return true;
    }

    /// <summary>Parses a remote URL into the repository it names, when a supported provider hosts it.</summary>
    /// <param name="remoteUrl">An <c>https://</c>, <c>ssh://</c> or <c>user@host:path</c> remote.</param>
    /// <param name="gitLabHosts">Self-managed GitLab hosts, beside gitlab.com and hosts named <c>gitlab.*</c>.</param>
    /// <param name="repository">The repository on success.</param>
    public static bool TryParse(string? remoteUrl, IReadOnlyCollection<string> gitLabHosts, [NotNullWhen(true)] out GitRepositoryReference? repository)
    {
        repository = null;
        if (!TrySplit(remoteUrl, out var host, out var segments))
        {
            return false;
        }

        if (host is GitHubHost or "ssh.github.com")
        {
            return TryParseGitHub(segments, out repository);
        }

        if (host is AzureDevOpsHost)
        {
            // https://dev.azure.com/{organization}/{project}/_git/{repository}
            return segments.Length > 1 && TryParseAzureDevOpsWeb(segments[0], segments.AsSpan(1), out repository);
        }

        if (host is "ssh.dev.azure.com" or "vs-ssh.visualstudio.com")
        {
            // git@ssh.dev.azure.com:v3/{organization}/{project}/{repository}
            return segments.Length == 4 && segments[0] == "v3" && TryCreateAzureDevOps(segments[1], segments[2], segments[3], out repository);
        }

        if (host.EndsWith(LegacyAzureDevOpsSuffix, StringComparison.Ordinal))
        {
            // https://{organization}.visualstudio.com/[DefaultCollection/]{project}/_git/{repository}
            var path = segments.AsSpan();
            if (path.Length > 0 && string.Equals(path[0], "DefaultCollection", StringComparison.OrdinalIgnoreCase))
            {
                path = path[1..];
            }

            return TryParseAzureDevOpsWeb(host[..^LegacyAzureDevOpsSuffix.Length], path, out repository);
        }

        var gitLabHost = host == "altssh.gitlab.com" ? GitLabHost : host;
        if (gitLabHost == GitLabHost || gitLabHost.StartsWith("gitlab.", StringComparison.Ordinal) ||
            gitLabHosts.Contains(gitLabHost, StringComparer.OrdinalIgnoreCase))
        {
            return TryParseGitLab(gitLabHost, segments, out repository);
        }

        return false;
    }

    // Splits a remote into its lower-case host and its unescaped path segments.
    private static bool TrySplit(string? remoteUrl, out string host, out string[] segments)
    {
        host = string.Empty;
        segments = [];
        var text = remoteUrl?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        string path;
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "ssh" or "git"))
            {
                return false;
            }

            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else
        {
            // The scp-like form: [user@]host:path
            var colon = text.IndexOf(':');
            if (colon <= 0)
            {
                return false;
            }

            var authority = text[..colon];
            host = authority[(authority.LastIndexOf('@') + 1)..];
            path = text[(colon + 1)..];
        }

        if (!IsHostName(host))
        {
            return false;
        }

        host = host.ToLowerInvariant();
        segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            segments[index] = Uri.UnescapeDataString(segments[index]);
        }

        return segments.Length > 0;
    }

    private static bool TryParseGitHub(string[] segments, [NotNullWhen(true)] out GitRepositoryReference? repository)
    {
        repository = null;
        if (segments.Length < 2)
        {
            return false;
        }

        var name = TrimGitSuffix(segments[1]);
        if (!IsSlug(segments[0]) || !IsSlug(name))
        {
            return false;
        }

        repository = GitRepositoryReference.GitHub(segments[0], name);
        return true;
    }

    private static bool TryParseGitLab(string host, string[] segments, [NotNullWhen(true)] out GitRepositoryReference? repository)
    {
        repository = null;
        if (segments.Length < 2)
        {
            return false;
        }

        // A project lives under a group and any number of subgroups: group/subgroup/project.
        segments[^1] = TrimGitSuffix(segments[^1]);
        if (!segments.All(IsSlug))
        {
            return false;
        }

        repository = GitRepositoryReference.GitLab(host, string.Join('/', segments[..^1]), segments[^1]);
        return true;
    }

    // {project}/_git/{repository}, or _git/{repository} for the repository named after its project.
    private static bool TryParseAzureDevOpsWeb(string organization, ReadOnlySpan<string> path, [NotNullWhen(true)] out GitRepositoryReference? repository)
    {
        repository = null;
        string? project = null;
        if (path.Length > 0 && path[0] != "_git")
        {
            project = path[0];
            path = path[1..];
        }

        if (path.Length < 2 || path[0] != "_git")
        {
            return false;
        }

        // Clone URLs can name a limited-refs view of the repository: _git/_optimized/{repository}.
        path = path[1..];
        if (path.Length > 1 && path[0] is "_optimized" or "_full")
        {
            path = path[1..];
        }

        return path.Length == 1 && TryCreateAzureDevOps(organization, project ?? path[0], path[0], out repository);
    }

    private static bool TryCreateAzureDevOps(string organization, string project, string name, [NotNullWhen(true)] out GitRepositoryReference? repository)
    {
        repository = null;
        // An organization name holds letters, digits and hyphens only.
        if (!IsSlug(organization) || organization.AsSpan().ContainsAny('.', '_') || !IsAzureDevOpsName(project) || !IsAzureDevOpsName(name))
        {
            return false;
        }

        repository = GitRepositoryReference.AzureDevOps(organization, project, name);
        return true;
    }

    private static string TrimGitSuffix(string name)
        => name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static bool IsHostName(string value)
        => value.Length is > 0 and <= 253 && char.IsAsciiLetterOrDigit(value[0]) &&
           value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.');

    // The names GitHub and GitLab accept in a path: no remote can smuggle a separator, a query or a space.
    private static bool IsSlug(string value)
        => value.Length is > 0 and <= MaximumPartLength && value is not ("." or "..") &&
           value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    // Azure DevOps project and repository names may hold spaces and non-ASCII letters, but no path syntax.
    private static bool IsAzureDevOpsName(string value)
        => value.Length is > 0 and <= MaximumPartLength && value is not ("." or "..") && value == value.Trim() &&
           !value.Any(static character => char.IsControl(character) || character is '/' or '\\' or '?' or '#' or ':' or '*' or '"' or '<' or '>' or '|');
}
