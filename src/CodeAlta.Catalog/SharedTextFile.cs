using System.Text;
using CodeAlta.Core.IO;

namespace CodeAlta.Catalog;

/// <summary>
/// Reads and writes the text of a file that others read and write meanwhile. A read is opened so that a writer is never
/// refused (Windows shares only the reads of a file by default, so a save that comes while another reader holds the file
/// would fail there with a sharing violation), and waits through a replacement, which on Windows can leave no file for a
/// moment. A write replaces the file (<see cref="StagedFileReplacement"/>): a reader finds the previous text or the new one,
/// never part of it, and the file keeps its access rules, its Unix mode and its Windows attributes, as a save of
/// <see cref="TextFileCodec"/> keeps them.
/// </summary>
internal static class SharedTextFile
{
    private const FileShare Sharing = FileShare.ReadWrite | FileShare.Delete;
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Reads the whole text of a file, decoded as UTF-8 unless a byte order mark says otherwise.</summary>
    /// <param name="path">The path of the file.</param>
    /// <returns>The text of the file.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder of the file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    internal static string ReadAllText(string path) => TryReadAllText(path) ?? throw Missing(path);

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
        => await TryReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) ?? throw Missing(path);

    /// <summary>
    /// Reads the whole text of a file as <see cref="ReadAllText"/> does, or returns <see langword="null"/> when there is no file:
    /// never in the moment a write replaces it.
    /// </summary>
    /// <param name="path">The path of the file.</param>
    /// <returns>The text of the file, or <see langword="null"/>.</returns>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    internal static string? TryReadAllText(string path)
    {
        using var stream = OpenRead(path);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Reads the whole text of a file as <see cref="TryReadAllText"/> does, asynchronously.</summary>
    /// <param name="path">The path of the file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The text of the file, or <see langword="null"/>.</returns>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    internal static async Task<string?> TryReadAllTextAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = await StagedFileReplacement.OpenReplacedFileAsync(path, Sharing, cancellationToken).ConfigureAwait(false);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a file to be read as <see cref="TryReadAllText"/> does, for a reader that reads part of it, or returns
    /// <see langword="null"/> when there is no file.
    /// </summary>
    /// <param name="path">The path of the file.</param>
    /// <returns>The file, or <see langword="null"/>.</returns>
    /// <exception cref="IOException">The file cannot be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    internal static FileStream? OpenRead(string path) => StagedFileReplacement.OpenReplacedFile(path, Sharing);

    /// <summary>Replaces a file with text in UTF-8 without a byte order mark, or creates it.</summary>
    /// <param name="path">The file; a link is followed, and its target replaced.</param>
    /// <param name="text">The whole text.</param>
    /// <exception cref="DirectoryNotFoundException">The folder of the file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be written or replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    internal static void WriteAllText(string path, string text) => WriteAllText(path, text, Utf8WithoutBom);

    /// <summary>Replaces a file with text in an encoding, its preamble first, or creates it.</summary>
    /// <param name="path">The file; a link is followed, and its target replaced.</param>
    /// <param name="text">The whole text.</param>
    /// <param name="encoding">The encoding of the text.</param>
    /// <exception cref="DirectoryNotFoundException">The folder of the file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be written or replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    internal static void WriteAllText(string path, string text, Encoding encoding)
    {
        var (target, staged) = Stage(path, Encode(text, encoding));
        try
        {
            StagedFileReplacement.MoveOver(staged, target);
        }
        finally
        {
            Delete(staged);
        }
    }

    /// <summary>Replaces a file with text in UTF-8 without a byte order mark, or creates it, as <see cref="WriteAllText(string, string)"/> does, without blocking the thread.</summary>
    /// <param name="path">The file; a link is followed, and its target replaced.</param>
    /// <param name="text">The whole text.</param>
    /// <param name="cancellationToken">Cancels the write until the file is replaced.</param>
    /// <returns>A task that completes once the file is replaced.</returns>
    /// <exception cref="DirectoryNotFoundException">The folder of the file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be written or replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the replacement.</exception>
    internal static async Task WriteAllTextAsync(string path, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (target, staged) = Stage(path, Encode(text, Utf8WithoutBom));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await StagedFileReplacement.MoveOverAsync(staged, target).ConfigureAwait(false);
        }
        finally
        {
            Delete(staged);
        }
    }

    // The text written beside the file it replaces, with what the file keeps of itself: its access rules from the start, so
    // that a restrictive file is never readable through its copy, then its Unix mode or its Windows attributes.
    private static (string Target, string Staged) Stage(string path, byte[] bytes)
    {
        var target = TargetOf(path);
        var exists = File.Exists(target);
        var staged = StagedFileReplacement.StagedPathOf(target);
        try
        {
            using (var stream = TextFileCodec.CreateStagingFile(staged, exists ? target : null))
            {
                stream.Write(bytes);
            }

            if (exists)
            {
                if (OperatingSystem.IsWindows()) TextFileCodec.KeepWindowsMetadata(target, staged);
                else File.SetUnixFileMode(staged, File.GetUnixFileMode(target));
            }
        }
        catch
        {
            Delete(staged);
            throw;
        }

        return (target, staged);
    }

    private static byte[] Encode(string text, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(encoding);
        var preamble = encoding.GetPreamble();
        var bytes = new byte[checked(preamble.Length + encoding.GetByteCount(text))];
        preamble.CopyTo(bytes, 0);
        encoding.GetBytes(text, bytes.AsSpan(preamble.Length));
        return bytes;
    }

    private static void Delete(string staged)
    {
        try
        {
            File.Delete(staged);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Moved onto the file, it is gone; left behind, it no longer counts as a write in progress once it is old.
        }
    }

    // A replacement puts a file in the place of a link: the file a link names (a configuration kept with the other files of
    // the user) is the one replaced, and the link stays.
    private static string TargetOf(string path)
    {
        var file = new FileInfo(path);
        return file.LinkTarget is not null && file.ResolveLinkTarget(returnFinalTarget: true) is { } target ? target.FullName : path;
    }

    private static IOException Missing(string path)
        => Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(path)))
            ? new FileNotFoundException($"Could not find file '{path}'.", path)
            : new DirectoryNotFoundException($"Could not find a part of the path '{path}'.");
}
