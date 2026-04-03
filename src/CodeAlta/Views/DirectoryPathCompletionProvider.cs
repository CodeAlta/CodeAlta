using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Text;

namespace CodeAlta.Views;

internal sealed class DirectoryPathCompletionProvider
{
    private readonly string _currentDirectory;

    public DirectoryPathCompletionProvider(string? currentDirectory = null)
    {
        _currentDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(currentDirectory)
            ? Environment.CurrentDirectory
            : currentDirectory);
    }

    public PromptEditorCompletion Complete(in PromptEditorCompletionRequest request)
    {
        var input = SnapshotToString(request.Snapshot);
        var caret = Math.Clamp(request.CaretIndex, 0, input.Length);
        var currentText = input[..caret];

        if (!TryResolveCandidates(currentText, out var candidates))
        {
            return default;
        }

        var ghostText = caret == input.Length && candidates.Count > 0 &&
                        candidates[0].StartsWith(currentText, StringComparison.OrdinalIgnoreCase)
            ? candidates[0][currentText.Length..]
            : null;

        return new PromptEditorCompletion(
            Handled: true,
            Candidates: candidates,
            ReplaceStart: 0,
            ReplaceLength: request.Snapshot.Length,
            SelectedIndex: 0,
            GhostText: ghostText);
    }

    private bool TryResolveCandidates(string currentText, out IReadOnlyList<string> candidates)
    {
        if (string.IsNullOrEmpty(currentText))
        {
            candidates = GetRootCandidates();
            return candidates.Count > 0;
        }

        if (!TryResolveSearchContext(currentText, out var searchRoot, out var prefix))
        {
            candidates = [];
            return false;
        }

        try
        {
            if (!Directory.Exists(searchRoot))
            {
                candidates = [];
                return false;
            }

            candidates = Directory
                .EnumerateDirectories(searchRoot)
                .Where(directory => prefix.Length == 0 || Path.GetFileName(directory).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(AppendTrailingSeparator)
                .OrderBy(static candidate => candidate, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return candidates.Count > 0;
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

        candidates = [];
        return false;
    }

    private IReadOnlyList<string> GetRootCandidates()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return DriveInfo
                    .GetDrives()
                    .Where(static drive => drive.IsReady)
                    .Select(static drive => drive.RootDirectory.FullName)
                    .OrderBy(static drive => drive, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return [AppendTrailingSeparator(Path.GetPathRoot(_currentDirectory) ?? Path.DirectorySeparatorChar.ToString())];
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
        var effectivePath = OperatingSystem.IsWindows() &&
                            path.Length == 2 &&
                            char.IsLetter(path[0]) &&
                            path[1] == ':'
            ? AppendTrailingSeparator(path)
            : path;

        return Path.GetFullPath(Path.IsPathRooted(effectivePath)
            ? effectivePath
            : Path.Combine(_currentDirectory, effectivePath));
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

    private static string SnapshotToString(ITextSnapshot snapshot)
    {
        if (snapshot.Length == 0)
        {
            return string.Empty;
        }

        return string.Create(snapshot.Length, snapshot, static (span, source) => source.CopyTo(0, span));
    }
}
