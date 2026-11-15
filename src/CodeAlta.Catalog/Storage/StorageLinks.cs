namespace CodeAlta.Catalog.Storage;

/// <summary>The check of links the measures of the profile share.</summary>
internal static class StorageLinks
{
    /// <summary>
    /// Gets whether a folder, or any folder above it, is a link (a symbolic link, a junction or another reparse point):
    /// what is behind it is not measured. Checked again for each folder taken from the stack of a walk, because a folder
    /// above it may have become a link since it was listed.
    /// </summary>
    /// <param name="directory">The folder.</param>
    /// <exception cref="FileNotFoundException">The folder or a folder above it is not there.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder or a folder above it is not there.</exception>
    /// <exception cref="IOException">A folder could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">A folder could not be read.</exception>
    internal static bool HasLinkedAncestor(DirectoryInfo directory)
    {
        for (DirectoryInfo? current = directory; current is not null; current = current.Parent)
        {
            var attributes = File.GetAttributes(current.FullName);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || current.LinkTarget is not null) return true;
        }

        return false;
    }
}
