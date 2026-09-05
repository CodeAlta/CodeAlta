using CodeAlta.Tui.App;
using CodeAlta.Catalog;

namespace CodeAlta.Tui.Views;

internal enum OpenProjectSuggestionKind
{
    Project,
    Directory,
}

internal sealed record OpenProjectSuggestion(
    OpenProjectSuggestionKind Kind,
    string ReplaceText,
    string PrimaryText,
    string? SecondaryText = null);

internal sealed class DirectoryPathCompletionProvider
{
    private readonly string _currentDirectory;
    private readonly Func<bool> _includeHidden;
    private readonly Func<IEnumerable<ProjectDescriptor>>? _getProjects;

    public DirectoryPathCompletionProvider(
        string? currentDirectory = null,
        Func<bool>? includeHidden = null,
        Func<IEnumerable<ProjectDescriptor>>? projects = null)
    {
        _currentDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(currentDirectory)
            ? Environment.CurrentDirectory
            : currentDirectory);
        _includeHidden = includeHidden ?? (() => false);
        _getProjects = projects;
    }

    public IReadOnlyList<OpenProjectSuggestion> GetSuggestions(string? currentText)
    {
        var text = currentText ?? string.Empty;
        if (text.Length == 0)
        {
            return GetDefaultSuggestions();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        if (!OpenProjectRequestResolver.LooksLikePath(text))
        {
            return GetProjectMatches(text);
        }

        if (!TryResolveSearchContext(text, out var searchRoot, out var prefix))
        {
            return [];
        }

        try
        {
            if (!Directory.Exists(searchRoot))
            {
                return [];
            }

            var suggestions = Directory
                .EnumerateDirectories(searchRoot)
                .Where(directory => prefix.Length == 0 || Path.GetFileName(directory).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(static directory => BuildDirectorySuggestion(directory))
                .OrderBy(static candidate => candidate.ReplaceText, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (prefix.Length == 0)
            {
                suggestions.Insert(0, BuildDirectorySuggestion(searchRoot));
            }

            return suggestions;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (ArgumentException)
        {
        }

        return [];
    }

    private IReadOnlyList<OpenProjectSuggestion> GetRootSuggestions()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return DriveInfo
                    .GetDrives()
                    .Where(static drive => drive.IsReady)
                    .Select(static drive => BuildDirectorySuggestion(drive.RootDirectory.FullName))
                    .OrderBy(static drive => drive.ReplaceText, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return [BuildDirectorySuggestion(Path.GetPathRoot(_currentDirectory) ?? Path.DirectorySeparatorChar.ToString())];
    }

    private IReadOnlyList<OpenProjectSuggestion> GetDefaultSuggestions()
    {
        var projectSuggestions = BuildProjectSuggestions(_getProjects, _includeHidden());
        var rootSuggestions = GetRootSuggestions();
        if (projectSuggestions.Count == 0)
        {
            return rootSuggestions;
        }

        var suggestions = new List<OpenProjectSuggestion>(projectSuggestions.Count + rootSuggestions.Count);
        suggestions.AddRange(projectSuggestions);
        suggestions.AddRange(rootSuggestions);
        return suggestions;
    }

    private IReadOnlyList<OpenProjectSuggestion> GetProjectMatches(string currentText)
    {
        var trimmed = currentText.Trim();
        if (trimmed.Length == 0)
        {
            return BuildProjectSuggestions(_getProjects, _includeHidden());
        }

        return BuildProjectSuggestions(_getProjects, _includeHidden())
            .Where(candidate => candidate.PrimaryText.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private bool TryResolveSearchContext(string currentText, out string searchRoot, out string prefix)
    {
        var pathText = currentText.Trim();
        if (pathText.Length == 0)
        {
            searchRoot = _currentDirectory;
            prefix = string.Empty;
            return true;
        }

        if (OperatingSystem.IsWindows() &&
            pathText.Length == 2 &&
            char.IsLetter(pathText[0]) &&
            pathText[1] == ':')
        {
            searchRoot = AppendTrailingSeparator(pathText);
            prefix = string.Empty;
            return true;
        }

        if (string.Equals(pathText, "~", StringComparison.Ordinal))
        {
            searchRoot = NormalizeDirectoryPath(pathText);
            prefix = string.Empty;
            return true;
        }

        if (EndsWithDirectorySeparator(pathText))
        {
            searchRoot = NormalizeDirectoryPath(pathText);
            prefix = string.Empty;
            return true;
        }

        var parentPath = Path.GetDirectoryName(pathText);
        prefix = Path.GetFileName(pathText);
        searchRoot = string.IsNullOrWhiteSpace(parentPath)
            ? _currentDirectory
            : NormalizeDirectoryPath(parentPath);
        return true;
    }

    private string NormalizeDirectoryPath(string path)
    {
        var effectiveInput = path.Trim();
        if (effectiveInput.StartsWith("~", StringComparison.Ordinal))
        {
            return OpenProjectRequestResolver.NormalizePath(effectiveInput);
        }

        var effectivePath = OperatingSystem.IsWindows() &&
                            effectiveInput.Length == 2 &&
                            char.IsLetter(effectiveInput[0]) &&
                            effectiveInput[1] == ':'
            ? AppendTrailingSeparator(effectiveInput)
            : effectiveInput;

        return Path.GetFullPath(Path.IsPathRooted(effectivePath)
            ? effectivePath
            : Path.Combine(_currentDirectory, effectivePath));
    }

    private static IReadOnlyList<OpenProjectSuggestion> BuildProjectSuggestions(
        Func<IEnumerable<ProjectDescriptor>>? getProjects,
        bool includeHidden)
    {
        if (getProjects is null)
        {
            return [];
        }

        return getProjects()
            .Where(project => includeHidden || !project.Archived)
            .OrderBy(static project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static project => project.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static project => project.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static project => new OpenProjectSuggestion(
                OpenProjectSuggestionKind.Project,
                project.DisplayName,
                project.DisplayName,
                project.ProjectPath))
            .ToArray();
    }

    private static OpenProjectSuggestion BuildDirectorySuggestion(string directory)
    {
        var normalized = AppendTrailingSeparator(directory);
        return new OpenProjectSuggestion(
            OpenProjectSuggestionKind.Directory,
            normalized,
            normalized);
    }

    private static bool EndsWithDirectorySeparator(string path)
        => path.Length > 0 &&
           (path[^1] == Path.DirectorySeparatorChar || path[^1] == Path.AltDirectorySeparatorChar);

    private static string AppendTrailingSeparator(string path)
    {
        if (string.IsNullOrEmpty(path) || EndsWithDirectorySeparator(path))
        {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
    }
}
