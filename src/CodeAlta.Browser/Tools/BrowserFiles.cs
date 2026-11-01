using System.Collections.Immutable;

namespace CodeAlta.Browser.Tools;

/// <summary>
/// Where the tools of a session may read and write files: a relative path starts from the folder of the session, and
/// a path has to be inside one of the folders of its kind.
/// </summary>
/// <remarks>
/// Paths are compared as the system resolves them: a link anywhere in a path, a folder or the file, counts for where it
/// leads, so that a link inside a folder of the session does not lead a tool out of it.
/// </remarks>
/// <param name="BaseDirectory">The folder a relative path starts from.</param>
/// <param name="ReadRoots">The folders a tool may read from: the files a page uploads, the scripts it runs.</param>
/// <param name="WriteRoots">The folders a tool may write to: screenshots, snapshots, traces.</param>
/// <param name="TemporaryDirectory">The folder of the files a tool writes by itself, such as a screenshot too large to attach.</param>
internal sealed record BrowserFiles(string BaseDirectory, IReadOnlyList<string> ReadRoots, IReadOnlyList<string> WriteRoots, string TemporaryDirectory)
{
    // Names of files that hold secrets: a page never gets them, whatever folder they are in.
    private static readonly ImmutableArray<string> SecretNames = [".env", ".npmrc", ".pypirc", ".netrc", ".git-credentials", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", "credentials", "known_hosts"];
    private static readonly ImmutableArray<string> SecretExtensions = [".pem", ".key", ".pfx", ".p12", ".kdbx", ".keystore", ".jks"];
    private static readonly ImmutableArray<string> SecretFolders = [".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker", ".alta"];

    // How many links a path may go through before it is refused: a loop of links never ends otherwise.
    private const int MaximumLinks = 40;

    /// <summary>Gets the folders and files never read or written, also inside the roots: those a sandbox denies.</summary>
    public IReadOnlyList<string> Denied { get; init; } = [];

    /// <summary>Gets the folders and files never written, also inside the write roots: those a sandbox protects.</summary>
    public IReadOnlyList<string> ReadOnly { get; init; } = [];

    /// <summary>Resolves a path a tool reads.</summary>
    /// <exception cref="BrowserToolException">The path is outside the folders, or names a file of secrets.</exception>
    public string ResolveRead(string path, string argument)
    {
        var (full, target) = Resolve(path, argument, ReadRoots, "read from");
        if (IsSecret(full) || IsSecret(target)) throw new BrowserToolException("not-allowed", $"The file of \"{argument}\" looks like it holds secrets ({Path.GetFileName(target)}): a page never gets such a file.");
        return full;
    }

    /// <summary>Resolves a path a tool writes; its folder is created.</summary>
    /// <exception cref="BrowserToolException">The path is outside the folders, or names a folder.</exception>
    public string ResolveWrite(string path, string argument)
    {
        var (full, target) = Resolve(path, argument, WriteRoots, "written to");
        if (ReadOnly.Any(entry => IsWithin(target, RealPath(entry)))) throw new BrowserToolException("not-allowed", $"The path of \"{argument}\" is read-only for this session.");
        if (Directory.Exists(target)) throw new BrowserToolException("invalid-arguments", $"\"{full}\" is a folder: \"{argument}\" names a file.");
        if (Path.GetDirectoryName(full) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
        return full;
    }

    /// <summary>Whether a path is inside the folders a page may read: a <c>file:</c> address is checked as a read.</summary>
    public bool CanRead(string fullPath)
    {
        string target;
        try
        {
            target = RealPath(fullPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }

        return ReadRoots.Any(root => IsWithin(target, RealPath(root))) && !Denied.Any(entry => IsWithin(target, RealPath(entry))) && !IsSecret(fullPath) && !IsSecret(target);
    }

    private (string Full, string Target) Resolve(string path, string argument, IReadOnlyList<string> roots, string verb)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new BrowserToolException("invalid-arguments", $"The argument \"{argument}\" must be a file path.");
        string full, target;
        try
        {
            full = Path.GetFullPath(path, BaseDirectory);
            target = RealPath(full);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new BrowserToolException("invalid-arguments", $"The argument \"{argument}\" is not a valid file path.", exception);
        }
        catch (IOException exception)
        {
            throw new BrowserToolException("not-allowed", $"The path of \"{argument}\" goes through too many links.", exception);
        }

        if (!roots.Any(root => IsWithin(target, RealPath(root))) || Denied.Any(entry => IsWithin(target, RealPath(entry))))
        {
            throw new BrowserToolException("not-allowed", $"The path of \"{argument}\" is outside the folders a file can be {verb}: {string.Join(", ", roots)}.");
        }

        return (full, target);
    }

    /// <summary>
    /// The path as the system resolves it: each folder and the file that is a link (a symbolic link or a junction) is
    /// replaced by where it leads, and what does not exist yet is kept as it is.
    /// </summary>
    /// <exception cref="IOException">The path goes through too many links.</exception>
    internal static string RealPath(string path)
    {
        var current = Path.GetFullPath(path);
        for (var links = 0; ; links++)
        {
            if (links > MaximumLinks) throw new IOException($"{path} goes through more than {MaximumLinks} links.");
            if (FirstLink(current) is not { } link) return current;
            current = link;
        }
    }

    // The path with its first link replaced by its target, or null when no part of it is a link.
    private static string? FirstLink(string full)
    {
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            try
            {
                if (!info.Exists) return null;
                if (info.LinkTarget is not { } linkTarget) continue;
                var target = Path.GetFullPath(linkTarget, Path.GetDirectoryName(current) ?? root);
                return Path.Combine([target, .. parts[(index + 1)..]]);
            }
            catch (UnauthorizedAccessException)
            {
                // A folder the user cannot list: nothing under it is a link CodeAlta could follow.
                return null;
            }
        }

        return null;
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
