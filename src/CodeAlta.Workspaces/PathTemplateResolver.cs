namespace CodeAlta.Workspaces;

/// <summary>
/// Resolves checkout path templates.
/// </summary>
public static class PathTemplateResolver
{
    /// <summary>
    /// Resolves a template into a normalized path.
    /// </summary>
    /// <param name="template">The template to resolve.</param>
    /// <param name="context">The expansion context.</param>
    /// <returns>The normalized resolved path.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="template"/> is empty or unsafe.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is <see langword="null"/>.</exception>
    public static string Resolve(string template, PathTemplateContext context)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new ArgumentException("Template is required.", nameof(template));
        }

        ArgumentNullException.ThrowIfNull(context);

        var resolved = template
            .Replace("{workspaceSlug}", context.WorkspaceSlug, StringComparison.Ordinal)
            .Replace("{projectSlug}", context.ProjectSlug, StringComparison.Ordinal)
            .Replace("{workspaceKey}", context.WorkspaceSlug, StringComparison.Ordinal)
            .Replace("{projectKey}", context.ProjectSlug, StringComparison.Ordinal)
            .Replace("{repoName}", context.RepoName, StringComparison.Ordinal)
            .Replace("{machineId}", context.MachineId, StringComparison.Ordinal)
            .Replace("{workspaceId}", context.WorkspaceId.ToString(), StringComparison.Ordinal)
            .Replace("{projectId}", context.ProjectId.ToString(), StringComparison.Ordinal);

        if (resolved.IndexOf('{', StringComparison.Ordinal) >= 0)
        {
            throw new ArgumentException($"Template '{template}' contains unsupported macros.", nameof(template));
        }

        return NormalizePath(context.BaseRoot, resolved);
    }

    private static string NormalizePath(string? baseRoot, string resolved)
    {
        if (string.IsNullOrWhiteSpace(baseRoot))
        {
            return Path.GetFullPath(resolved);
        }

        var normalizedRoot = Path.GetFullPath(baseRoot);
        var combined = Path.IsPathRooted(resolved)
            ? resolved
            : Path.Combine(normalizedRoot, resolved);

        var normalizedPath = Path.GetFullPath(combined);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!normalizedPath.StartsWith(normalizedRoot, comparison))
        {
            throw new ArgumentException(
                $"Resolved path '{normalizedPath}' escapes root '{normalizedRoot}'.",
                nameof(resolved));
        }

        return normalizedPath;
    }
}
