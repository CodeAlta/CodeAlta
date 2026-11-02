namespace CodeAlta.Fleet.Identity;

/// <summary>
/// Writes the files of the fleet folder: staged beside their destination and renamed over it, so a reader never sees half
/// a file, and readable by their owner only on macOS and Linux.
/// </summary>
/// <remarks>
/// One instance alone writes its state root (its lock says so), so these writes need no cross-process gate: the
/// rename protects readers and crashes, not a second writer.
/// </remarks>
internal static class FleetFiles
{
    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>The mode of a file only its owner reads and writes (0600).</summary>
    internal const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Creates a folder, closed to other users on macOS and Linux.</summary>
    public static void EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, OwnerOnlyDirectory);
        if (File.GetUnixFileMode(path) != OwnerOnlyDirectory)
        {
            File.SetUnixFileMode(path, OwnerOnlyDirectory);
        }
    }

    /// <summary>Replaces a file with new bytes, or creates it.</summary>
    public static void WriteAtomically(string path, ReadOnlySpan<byte> bytes) => Publish(path, bytes, overwrite: true);

    /// <summary>Creates a file with its bytes, unless it exists.</summary>
    /// <returns>Whether the file was created; false when it already existed.</returns>
    public static bool TryWriteNew(string path, ReadOnlySpan<byte> bytes) => Publish(path, bytes, overwrite: false);

    /// <summary>Makes an existing file readable by its owner only on macOS and Linux.</summary>
    public static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != OwnerOnlyFile)
        {
            File.SetUnixFileMode(path, OwnerOnlyFile);
        }
    }

    private static bool Publish(string path, ReadOnlySpan<byte> bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new ArgumentException("A file path with a parent folder is required.", nameof(path));
        EnsurePrivateDirectory(directory);
        var stagingPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerOnlyFile;
        }

        try
        {
            using (var stream = new FileStream(stagingPath, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (overwrite)
            {
                File.Move(stagingPath, path, overwrite: true);
                return true;
            }

            try
            {
                File.Move(stagingPath, path, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                return false;
            }
        }
        finally
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
    }
}
