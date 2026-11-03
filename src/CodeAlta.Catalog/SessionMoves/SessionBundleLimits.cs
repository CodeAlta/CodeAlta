namespace CodeAlta.Catalog.SessionMoves;

/// <summary>
/// The most an archive of a bundle may unpack to (<see cref="SessionBundle.UnpackAsync(Stream, string, SessionBundleLimits, CancellationToken)"/>):
/// counted on the bytes actually written, never on what the archive says of its entries.
/// </summary>
/// <param name="MaximumEntries">The most entries of the archive.</param>
/// <param name="MaximumEntryBytes">The most bytes one entry unpacks to.</param>
/// <param name="MaximumTotalBytes">The most bytes all the entries unpack to.</param>
public sealed record SessionBundleLimits(int MaximumEntries, long MaximumEntryBytes, long MaximumTotalBytes)
{
    /// <summary>Gets the limits of an archive whose sender is not known: 50,000 entries and 4 GiB.</summary>
    public static SessionBundleLimits Default { get; } = new(50_000, 4L * 1024 * 1024 * 1024, 4L * 1024 * 1024 * 1024);
}
