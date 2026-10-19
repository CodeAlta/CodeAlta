using System.Text;

namespace CodeAlta.Catalog;

/// <summary>
/// Reads the text of a file that others write while it is read: as <see cref="File.ReadAllText(string)"/> does, but
/// opened so that a writer is never refused meanwhile. Windows shares only the reads of a file by default, so a save
/// that comes while another reader holds the file would fail there with a sharing violation.
/// </summary>
internal static class SharedTextFile
{
    /// <summary>Reads the whole text of a file, decoded as UTF-8 unless a byte order mark says otherwise.</summary>
    /// <param name="path">The path of the file.</param>
    /// <returns>The text of the file.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder of the file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    internal static string ReadAllText(string path)
    {
        using var stream = Open(path, useAsync: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Reads the whole text of a file as <see cref="ReadAllText"/> does, asynchronously.</summary>
    /// <param name="path">The path of the file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The text of the file.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder of the file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    internal static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = Open(path, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static FileStream Open(string path, bool useAsync)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync);
}
