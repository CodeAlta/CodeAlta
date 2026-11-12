namespace CodeAlta.Catalog;

internal static class ProjectPathNameFormatter
{
    public static string InferName(string projectPath, string fallbackDisplayName = "")
    {
        var displayName = InferDisplayName(projectPath);
        if (IsValidProjectName(displayName))
        {
            return displayName;
        }

        var safeRootName = CreateSafeRootName(displayName);
        if (IsValidProjectName(safeRootName))
        {
            return safeRootName;
        }

        return fallbackDisplayName;
    }

    public static string InferDisplayName(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return string.Empty;
        }

        var trimmed = projectPath.Trim();
        if (TryGetPathLeaf(trimmed, out var leaf))
        {
            return leaf;
        }

        if (TryGetRootDisplayName(trimmed, out var rootDisplayName))
        {
            return rootDisplayName;
        }

        return trimmed;
    }

    public static bool IsValidProjectName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
               !name.Contains(Path.DirectorySeparatorChar) &&
               !name.Contains(Path.AltDirectorySeparatorChar);
    }

    private static bool TryGetPathLeaf(string projectPath, out string leaf)
    {
        // A local path is an absolute URI too (file:///Users/x/X.Y, file:///C:/x/X.Y): it is named after its folder below, whole,
        // dots included. Only the address of a remote repository drops the .git of a clone.
        if (Uri.TryCreate(projectPath, UriKind.Absolute, out var uri) && !uri.IsFile && !uri.IsUnc)
        {
            var remoteName = Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/')[^1]);
            if (remoteName.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) remoteName = remoteName[..^4];
            if (!string.IsNullOrWhiteSpace(remoteName))
            {
                leaf = remoteName;
                return true;
            }
        }

        var localPath = GetFullPathOrOriginal(ExpandBareDriveRoot(projectPath));
        var localName = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(localName))
        {
            leaf = localName;
            return true;
        }

        leaf = string.Empty;
        return false;
    }

    private static bool TryGetRootDisplayName(string projectPath, out string rootDisplayName)
    {
        var localPath = GetFullPathOrOriginal(ExpandBareDriveRoot(projectPath));
        var root = Path.GetPathRoot(localPath);
        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(localPath, root, StringComparison.OrdinalIgnoreCase))
        {
            rootDisplayName = root;
            return true;
        }

        rootDisplayName = string.Empty;
        return false;
    }

    private static string CreateSafeRootName(string displayName)
    {
        var builder = new System.Text.StringBuilder(displayName.Length + 5);
        var previousHyphen = false;
        foreach (var ch in displayName)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousHyphen = false;
                continue;
            }

            if (previousHyphen)
            {
                continue;
            }

            builder.Append('-');
            previousHyphen = true;
        }

        var safeName = builder.ToString().Trim('-');
        if (safeName.Length == 0)
        {
            return "filesystem-root";
        }

        return safeName.EndsWith("-root", StringComparison.OrdinalIgnoreCase)
            ? safeName
            : safeName + "-root";
    }

    private static string GetFullPathOrOriginal(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
        catch (NotSupportedException)
        {
            return path;
        }
        catch (PathTooLongException)
        {
            return path;
        }
    }

    private static string ExpandBareDriveRoot(string path)
    {
        if (path.Length == 2 &&
            char.IsLetter(path[0]) &&
            path[1] == Path.VolumeSeparatorChar)
        {
            return path + Path.DirectorySeparatorChar;
        }

        return path;
    }
}
