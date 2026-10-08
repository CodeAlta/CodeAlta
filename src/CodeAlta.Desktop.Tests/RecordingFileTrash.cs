namespace CodeAlta.Desktop.Tests;

/// <summary>
/// A trash for tests: what is moved there is kept in a folder of the test, never in the trash of the system, so
/// that a test sees that nothing was removed for good.
/// </summary>
internal sealed class RecordingFileTrash(string folder) : IDesktopFileTrash
{
    /// <summary>Whether the system has a trash.</summary>
    public bool Available { get; set; } = true;

    /// <summary>Whether a move fails, as when the system refuses it.</summary>
    public bool Fails { get; set; }

    /// <summary>The paths that were moved, in order.</summary>
    public List<string> Moved { get; } = [];

    /// <summary>Where the last entry that was moved is now.</summary>
    public string? Kept { get; private set; }

    public ValueTask<bool> MoveAsync(string fullPath, CancellationToken cancellationToken)
    {
        if (!Available || Fails) return ValueTask.FromResult(false);
        Directory.CreateDirectory(folder);
        Kept = Path.Combine(folder, Moved.Count + "-" + Path.GetFileName(fullPath));
        if (Directory.Exists(fullPath)) Directory.Move(fullPath, Kept);
        else File.Move(fullPath, Kept);
        Moved.Add(fullPath);
        return ValueTask.FromResult(true);
    }
}
