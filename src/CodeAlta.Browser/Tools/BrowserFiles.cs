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

    /// <summary>
    /// Gets the folders of CodeAlta that hold its settings, credentials and sessions (its global and state roots, which
    /// a separate profile puts anywhere): a page never gets a file in them, whatever its name, as it never gets one of
    /// <c>~/.alta</c>. The worktrees CodeAlta keeps there are not part of them.
    /// </summary>
    public IReadOnlyList<string> SecretRoots { get; init; } = [];

    /// <summary>Resolves a path a tool reads.</summary>
    /// <exception cref="BrowserToolException">The path is outside the folders, or names a file of secrets.</exception>
    public string ResolveRead(string path, string argument)
    {
        var (full, target) = Resolve(path, argument, ReadRoots, "read from");
        if (IsSecretFile(full) || IsSecretFile(target)) throw new BrowserToolException("not-allowed", $"The file of \"{argument}\" looks like it holds secrets ({Path.GetFileName(target)}): a page never gets such a file.");
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

    /// <summary>
    /// Copies a file a page gets (an upload) into the temporary folder of the tools, which a sandboxed session cannot
    /// change, and gives the path of the copy. The file is checked as it was opened: a link put in its path after the
    /// check of <see cref="ResolveRead"/> is refused.
    /// </summary>
    /// <exception cref="BrowserToolException">The path is outside the folders, names a file of secrets, or cannot be read.</exception>
    public async Task<string> StageReadAsync(string path, string argument, CancellationToken cancellationToken)
    {
        var full = ResolveRead(path, argument);
        var staged = Path.Combine(TemporaryDirectory, "uploads", Guid.NewGuid().ToString("N")[..12], Path.GetFileName(full));
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        await using (var source = OpenChecked(full, argument, FileMode.Open, FileAccess.Read, FileShare.Read, ReadRoots, write: false))
        await using (var copy = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await source.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        }

        return staged;
    }

    /// <summary>Reads the text of a file a tool takes (a script), checked as it was opened.</summary>
    /// <exception cref="BrowserToolException">The path is outside the folders, names a file of secrets, or cannot be read.</exception>
    public async Task<string> ReadTextAsync(string path, string argument, CancellationToken cancellationToken)
    {
        var full = ResolveRead(path, argument);
        await using var source = OpenChecked(full, argument, FileMode.Open, FileAccess.Read, FileShare.Read, ReadRoots, write: false);
        using var reader = new StreamReader(source);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a file a tool saves at a path <see cref="ResolveWrite"/> gave, checked as it was opened: a link put in its
    /// path after the check is refused, and what was there is not truncated before.
    /// </summary>
    /// <exception cref="BrowserToolException">The file is not inside the folders a tool writes to any more, or cannot be written.</exception>
    public async Task WriteAsync(string fullPath, Func<Stream, Task> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = OpenChecked(fullPath, fullPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, WriteRoots, write: true);
        stream.SetLength(0);
        await write(stream).ConfigureAwait(false);
    }

    /// <summary>Writes bytes, as <see cref="WriteAsync(string, Func{Stream, Task}, CancellationToken)"/>.</summary>
    public Task WriteAsync(string fullPath, byte[] bytes, CancellationToken cancellationToken)
        => WriteAsync(fullPath, stream => stream.WriteAsync(bytes, cancellationToken).AsTask(), cancellationToken);

    /// <summary>Writes text in UTF-8, as <see cref="WriteAsync(string, Func{Stream, Task}, CancellationToken)"/>.</summary>
    public Task WriteAsync(string fullPath, string text, CancellationToken cancellationToken)
        => WriteAsync(fullPath, System.Text.Encoding.UTF8.GetBytes(text), cancellationToken);

    // Opens a file, then checks the path the system gives for what it opened: where the system cannot tell it, the path is
    // resolved again, which leaves a smaller window.
    private FileStream OpenChecked(string fullPath, string argument, FileMode mode, FileAccess access, FileShare share, IReadOnlyList<string> roots, bool write)
    {
        var existed = File.Exists(fullPath);
        FileStream stream;
        try
        {
            stream = new FileStream(fullPath, mode, access, share, 4096, FileOptions.Asynchronous);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new BrowserToolException("io", $"The file of \"{argument}\" could not be {(write ? "written" : "read")}: {exception.Message}", exception);
        }

        string opened;
        try
        {
            opened = BrowserFileHandles.PathOf(stream.SafeFileHandle) ?? RealPath(fullPath);
        }
        catch (IOException)
        {
            opened = string.Empty;
        }

        var allowed = opened.Length != 0 && roots.Any(root => IsWithin(opened, RealPath(root))) && !Denied.Any(entry => IsWithin(opened, RealPath(entry)))
            && (write ? !ReadOnly.Any(entry => IsWithin(opened, RealPath(entry))) : !IsSecretFile(opened));
        if (allowed) return stream;
        stream.Dispose();
        // A file this call created where it must not write is removed: it is empty.
        if (write && !existed && opened.Length != 0)
        {
            try { File.Delete(opened); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* left empty */ }
        }

        throw new BrowserToolException("not-allowed", $"The file of \"{argument}\" was moved outside the folders a file can be {(write ? "written to" : "read from")} while it was opened.");
    }

    /// <summary>Whether a path is inside the folders a page may read: a <c>file:</c> address is checked as a read.</summary>
    public bool CanRead(string fullPath)
    {
        string target;
        try
        {
            // Outside the folders, the path is never touched: a UNC path would make the system reach its server.
            if (!ReadRoots.Any(root => IsWithin(Path.GetFullPath(fullPath), root))) return false;
            target = RealPath(fullPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }

        return ReadRoots.Any(root => IsWithin(target, RealPath(root))) && !Denied.Any(entry => IsWithin(target, RealPath(entry))) && !IsSecretFile(fullPath) && !IsSecretFile(target);
    }

    private (string Full, string Target) Resolve(string path, string argument, IReadOnlyList<string> roots, string verb)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new BrowserToolException("invalid-arguments", $"The argument \"{argument}\" must be a file path.");
        string full, target;
        try
        {
            full = Path.GetFullPath(path, BaseDirectory);
            // Outside the folders, the path is never touched: a UNC path would make the system reach its server.
            if (!roots.Any(root => IsWithin(full, root)))
            {
                throw new BrowserToolException("not-allowed", $"The path of \"{argument}\" is outside the folders a file can be {verb}: {string.Join(", ", roots)}.");
            }

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

    // A file of secrets by its name, or inside a folder of CodeAlta.
    private bool IsSecretFile(string path) => IsSecret(path) || IsInSecretRoot(path, SecretRoots);

    /// <summary>
    /// Whether a full path is inside one of the folders of CodeAlta (<see cref="SecretRoots"/>), as written or once their
    /// links are followed, but in the worktrees CodeAlta keeps there (<c>worktrees/&lt;repository&gt;/&lt;name&gt;</c>).
    /// </summary>
    /// <param name="path">The full path, as written or as the system resolves it.</param>
    /// <param name="roots">The folders of CodeAlta.</param>
    public static bool IsInSecretRoot(string path, IReadOnlyList<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string full, real;
            try
            {
                full = Path.GetFullPath(root);
                real = RealPath(root);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException)
            {
                // A folder whose path cannot be resolved: everything is kept from a page rather than nothing.
                return true;
            }

            if (IsInResolvedSecretRoot(path, full == real ? [full] : [full, real])) return true;
        }

        return false;
    }

    /// <summary>
    /// The folders of CodeAlta as written and as the system resolves them, once, for <see cref="IsInResolvedSecretRoot"/>.
    /// A folder whose path cannot be resolved is null: everything is kept from a page then.
    /// </summary>
    internal static IReadOnlyList<string>? ResolveSecretRoots(IReadOnlyList<string> roots)
    {
        var folders = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                folders.Add(Path.GetFullPath(root));
                folders.Add(RealPath(root));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException)
            {
                return null;
            }
        }

        return folders;
    }

    /// <summary>Whether a full path is inside one of the folders <see cref="ResolveSecretRoots"/> gave, but in their worktrees.</summary>
    internal static bool IsInResolvedSecretRoot(string path, IReadOnlyList<string>? folders)
        => folders is null || folders.Any(folder => IsWithin(path, folder) && !InWorktree(path, folder));

    // Inside a worktree of the folder of CodeAlta, which a session works in as in the folder of its project.
    private static bool InWorktree(string path, string root)
    {
        var worktrees = Path.Combine(root, "worktrees");
        if (!IsWithin(path, worktrees)) return false;
        var parts = Path.GetRelativePath(worktrees, path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0] != "..";
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
