using System.Collections.Frozen;
using System.Collections.Immutable;

namespace CodeAlta.Catalog.Storage;

/// <summary>
/// What an ignored entry of a checkout is called build output for: what a build, a package manager or the system brings
/// back, so that losing it loses nothing. The lists are fixed.
/// </summary>
public static class BuildOutput
{
    /// <summary>Gets the names of the folders that builds and package managers fill, compared ordinally without case.</summary>
    public static ImmutableArray<string> Folders { get; } =
    [
        "bin", "obj", "node_modules", "dist", "build", "target", ".gradle", ".next", ".nuxt", ".turbo", "DerivedData",
        "__pycache__", ".pytest_cache", ".venv",
    ];

    /// <summary>
    /// Gets the names of the files the file browsers of the systems leave in folders, compared ordinally without case: a
    /// global ignore of them does not make every checkout hold ignored files of its own.
    /// </summary>
    public static ImmutableArray<string> SystemFiles { get; } = [".DS_Store", "Thumbs.db", "desktop.ini"];

    private static readonly FrozenSet<string> FolderSet = Folders.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> SystemFileSet = SystemFiles.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets whether the name of an ignored entry makes it a candidate for build output: a folder whose own name, the last
    /// segment of its path, is one of <see cref="Folders"/>, or a file named as one of <see cref="SystemFiles"/> wherever it
    /// is. A folder below a build folder (<c>bin/Debug</c>) is not one by its name, nor is any other file. The name alone
    /// does not decide: a measure also asks which rule ignores a folder and whether it holds a repository.
    /// </summary>
    /// <param name="relativePath">The path of the entry relative to its checkout, with <c>/</c> or the separator of the system.</param>
    /// <param name="folder">Whether git names the entry as a folder.</param>
    /// <returns>Whether its name is one of build output.</returns>
    /// <exception cref="ArgumentNullException">The path is null.</exception>
    public static bool Includes(string relativePath, bool folder)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var name = Path.GetFileName(relativePath.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar));
        return name.Length > 0 && (folder ? FolderSet.Contains(name) : SystemFileSet.Contains(name));
    }
}
