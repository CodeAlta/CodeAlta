namespace CodeAlta.Browser.Tools;

/// <summary>
/// Where the tools of a session may read and write files: a relative path starts from the folder of the session, and
/// a path has to be inside one of the folders of its kind.
/// </summary>
/// <param name="BaseDirectory">The folder a relative path starts from.</param>
/// <param name="ReadRoots">The folders a tool may read from: the files a page uploads, the scripts it runs.</param>
/// <param name="WriteRoots">The folders a tool may write to: screenshots, snapshots, traces.</param>
/// <param name="TemporaryDirectory">The folder of the files a tool writes by itself, such as a screenshot too large to attach.</param>
internal sealed record BrowserFiles(string BaseDirectory, IReadOnlyList<string> ReadRoots, IReadOnlyList<string> WriteRoots, string TemporaryDirectory)
{
    // Names of files that hold secrets: a page never gets them, whatever folder they are in.
    private static readonly string[] SecretNames = [".env", ".npmrc", ".pypirc", ".netrc", ".git-credentials", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", "credentials", "known_hosts"];
    private static readonly string[] SecretExtensions = [".pem", ".key", ".pfx", ".p12", ".kdbx", ".keystore", ".jks"];
    private static readonly string[] SecretFolders = [".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker", ".alta"];

    /// <summary>Resolves a path a tool reads.</summary>
    /// <exception cref="BrowserToolException">The path is outside the folders, or names a file of secrets.</exception>
    public string ResolveRead(string path, string argument)
    {
        var full = Resolve(path, argument, ReadRoots, "read from");
        if (IsSecret(full)) throw new BrowserToolException("not-allowed", $"The file of \"{argument}\" looks like it holds secrets ({Path.GetFileName(full)}): a page never gets such a file.");
        return full;
    }

    /// <summary>Resolves a path a tool writes; its folder is created.</summary>
    /// <exception cref="BrowserToolException">The path is outside the folders, or names a folder.</exception>
    public string ResolveWrite(string path, string argument)
    {
        var full = Resolve(path, argument, WriteRoots, "written to");
        if (Directory.Exists(full)) throw new BrowserToolException("invalid-arguments", $"\"{full}\" is a folder: \"{argument}\" names a file.");
        if (Path.GetDirectoryName(full) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
        return full;
    }

    /// <summary>Whether a path is inside the folders a page may read: a <c>file:</c> address is checked as a read.</summary>
    public bool CanRead(string fullPath) => ReadRoots.Any(root => IsWithin(Path.GetFullPath(fullPath), root)) && !IsSecret(fullPath);

    private string Resolve(string path, string argument, IReadOnlyList<string> roots, string verb)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new BrowserToolException("invalid-arguments", $"The argument \"{argument}\" must be a file path.");
        string full;
        try
        {
            full = Path.GetFullPath(path, BaseDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new BrowserToolException("invalid-arguments", $"The argument \"{argument}\" is not a valid file path.", exception);
        }

        // A link inside a folder may point anywhere: its target is what is read or written.
        var target = ResolveLinks(full);
        if (!roots.Any(root => IsWithin(target, root)))
        {
            throw new BrowserToolException("not-allowed", $"The path of \"{argument}\" is outside the folders a file can be {verb}: {string.Join(", ", roots)}.");
        }

        return full;
    }

    private static string ResolveLinks(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.LinkTarget is not null ? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path : path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    /// <summary>Whether a path is a folder or inside it.</summary>
    public static bool IsWithin(string path, string root)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.TrimEndingDirectorySeparator(path);
        return string.Equals(full, folder, comparison) || full.StartsWith(folder + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>Whether a path names a file that holds secrets, or a file inside a folder of secrets.</summary>
    public static bool IsSecret(string path)
    {
        var name = Path.GetFileName(path);
        if (SecretNames.Any(secret => string.Equals(name, secret, StringComparison.OrdinalIgnoreCase))) return true;
        if (name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)) return true;
        if (SecretExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))) return true;
        var parts = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => SecretFolders.Any(folder => string.Equals(part, folder, StringComparison.OrdinalIgnoreCase)));
    }
}
