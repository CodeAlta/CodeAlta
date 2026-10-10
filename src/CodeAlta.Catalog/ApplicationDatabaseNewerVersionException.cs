namespace CodeAlta.Catalog;

/// <summary>
/// The exception thrown when the tables of an owner of the application database were written by a newer build:
/// the version recorded for them is higher than the one the owner asks for. Nothing was changed.
/// </summary>
/// <remarks>
/// It is an <see cref="InvalidOperationException"/>, so an owner whose data cannot be made again treats it as any
/// other refused migration. An owner that can make its data again (the list of sessions) catches it, drops its
/// tables and migrates from nothing.
/// </remarks>
public sealed class ApplicationDatabaseNewerVersionException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ApplicationDatabaseNewerVersionException"/> class.
    /// </summary>
    /// <param name="owner">The owner of the tables.</param>
    /// <param name="recordedVersion">The version recorded for the owner.</param>
    /// <param name="requestedVersion">The version the owner asked for.</param>
    /// <exception cref="ArgumentException"><paramref name="owner"/> is empty.</exception>
    public ApplicationDatabaseNewerVersionException(string owner, int recordedVersion, int requestedVersion)
        : base(CreateMessage(owner, recordedVersion, requestedVersion))
    {
        Owner = owner;
        RecordedVersion = recordedVersion;
        RequestedVersion = requestedVersion;
    }

    /// <summary>Gets the owner of the tables.</summary>
    public string Owner { get; }

    /// <summary>Gets the version recorded for the owner, written by the newer build.</summary>
    public int RecordedVersion { get; }

    /// <summary>Gets the version the owner asked for, the one this build knows.</summary>
    public int RequestedVersion { get; }

    private static string CreateMessage(string owner, int recordedVersion, int requestedVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return $"The tables of '{owner}' are at version {recordedVersion}, newer than the version {requestedVersion} this build knows. They were written by a newer build; nothing was changed.";
    }
}
