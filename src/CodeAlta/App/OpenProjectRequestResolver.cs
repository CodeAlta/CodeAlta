using CodeAlta.Catalog;

namespace CodeAlta.App;

internal static class OpenProjectRequestResolver
{
    public static bool LooksLikePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var trimmed = value.Trim();
        return trimmed.StartsWith("~", StringComparison.Ordinal) || Path.IsPathRooted(trimmed);
    }

    public static string NormalizePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var trimmed = value.Trim();
        if (trimmed.StartsWith("~", StringComparison.Ordinal))
        {
            trimmed = ExpandHomePath(trimmed);
        }

        if (OperatingSystem.IsWindows() &&
            trimmed.Length == 2 &&
            char.IsLetter(trimmed[0]) &&
            trimmed[1] == ':')
        {
            trimmed += Path.DirectorySeparatorChar;
        }

        var fullPath = Path.GetFullPath(trimmed);
        var rootPath = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, rootPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static ProjectDescriptor ResolveProjectReference(
        IReadOnlyList<ProjectDescriptor> projects,
        string projectReference)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectReference);

        var trimmedReference = projectReference.Trim();

        if (TryResolveSingle(projects, trimmedReference, static (project, value) => string.Equals(project.DisplayName, value, StringComparison.OrdinalIgnoreCase), out var exactDisplayNameMatch))
        {
            return exactDisplayNameMatch;
        }

        throw new InvalidOperationException(
            SR.T("Project '{0}' was not found. Enter a rooted path or use an existing project name from the sidebar.", trimmedReference));
    }

    private static bool TryResolveSingle(
        IReadOnlyList<ProjectDescriptor> projects,
        string projectReference,
        Func<ProjectDescriptor, string, bool> predicate,
        out ProjectDescriptor project)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectReference);
        ArgumentNullException.ThrowIfNull(predicate);

        var matches = projects
            .Where(candidate => predicate(candidate, projectReference))
            .ToArray();

        if (matches.Length == 0)
        {
            project = null!;
            return false;
        }

        if (matches.Length > 1)
        {
            var matchList = string.Join(
                ", ",
                matches.Select(static candidate => $"{candidate.DisplayName} ({candidate.ProjectPath})"));
            throw new InvalidOperationException(
                SR.T("Project '{0}' matched multiple entries: {1}. Enter the rooted folder path instead.", projectReference, matchList));
        }

        project = matches[0];
        return true;
    }

    private static string ExpandHomePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!string.Equals(path, "~", StringComparison.Ordinal) &&
            !path.StartsWith("~/", StringComparison.Ordinal) &&
            !path.StartsWith("~\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(SR.T("Only '~', '~/' and '~\\' are supported for home-relative paths."));
        }

        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(homePath))
        {
            throw new InvalidOperationException(SR.T("The current user's home directory could not be resolved."));
        }

        if (path.Length == 1)
        {
            return homePath;
        }

        return Path.Combine(homePath, path[2..]);
    }
}
