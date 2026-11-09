using System.Text;

namespace CodeAlta.Core.IO;

/// <summary>
/// Writes a whole file so that a reader finds the previous content or the new one, never half of it nor, beyond a moment
/// on Windows, no file; and reads a whole file that such a write may be replacing. The content is staged beside the file
/// (<see cref="StagedFileReplacement.StagedPathOf"/>) and moved onto it with <see cref="StagedFileReplacement"/>.
/// </summary>
/// <remarks>
/// The folder of the file must exist. Nothing here orders two writers: a read-modify-write store takes a lock first, a
/// <see cref="FilePathLocks"/> within a process and a <see cref="LockFile"/> across processes. On Windows, a file that
/// stays held beyond the retries without its deletion shared, but with its writes shared (a protected file of a running
/// sandbox), is written in place: a reader may then find it half written, for a moment.
/// </remarks>
public static class SafeFile
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Replaces a file with bytes, or creates it.</summary>
    /// <param name="path">The file.</param>
    /// <param name="bytes">The whole content.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder of the file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be written or replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes) => Write(path, bytes, withoutGap: false);

    /// <summary>
    /// Replaces a file with bytes, or creates it, as <see cref="WriteAllBytes"/> does, but never moves the file out of the
    /// way (<see cref="StagedFileReplacement.MoveOverWithoutGap(string, string)"/>): a file a filter holds beyond the retries
    /// is left as it is, and the write fails.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="bytes">The whole content.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder of the file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be written or replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    /// <remarks>For a file whose readers take a missing file for the default content: a policy, the state of an update.</remarks>
    public static void WriteAllBytesWithoutGap(string path, ReadOnlySpan<byte> bytes) => Write(path, bytes, withoutGap: true);

    private static void Write(string path, ReadOnlySpan<byte> bytes, bool withoutGap)
    {
        var staged = StagedFileReplacement.StagedPathOf(path);
        try
        {
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
            {
                stream.Write(bytes);
            }

            try
            {
                if (withoutGap) StagedFileReplacement.MoveOverWithoutGap(staged, path);
                else StagedFileReplacement.MoveOver(staged, path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (!TryWriteInPlace(path, bytes)) throw;
            }
        }
        finally
        {
            TryDelete(staged);
        }
    }

    // A file held open without its deletion shared, but its writes shared, is never replaced while it is held: a protected
    // file of a running sandbox, which keeps the sandbox from replacing it, is one. It is written in place instead, which
    // its holder lets through: a reader may then find it half written, for a moment.
    private static bool TryWriteInPlace(string path, ReadOnlySpan<byte> bytes)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1);
            stream.Write(bytes);
            stream.SetLength(bytes.Length);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Replaces a file with text in UTF-8 without a byte order mark, or creates it.</summary>
    /// <param name="path">The file.</param>
    /// <param name="text">The whole text.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder of the file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be written or replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public static void WriteAllText(string path, string text) => WriteAllText(path, text, Utf8WithoutBom);

    /// <summary>Replaces a file with text in an encoding, its preamble first, or creates it.</summary>
    /// <param name="path">The file.</param>
    /// <param name="text">The whole text.</param>
    /// <param name="encoding">The encoding of the text.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="encoding"/> is null.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder of the file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be written or replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public static void WriteAllText(string path, string text, Encoding encoding) => WriteAllBytes(path, Encode(text, encoding));

    /// <summary>Replaces a file with bytes, or creates it, as <see cref="WriteAllBytes"/> does, without blocking the thread.</summary>
    /// <param name="path">The file.</param>
    /// <param name="bytes">The whole content.</param>
    /// <param name="cancellationToken">Cancels the write until the file is replaced; the replacement itself is not canceled.</param>
    /// <returns>A task that completes once the file is replaced.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder of the file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be written or replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the replacement.</exception>
    public static async ValueTask WriteAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var staged = StagedFileReplacement.StagedPathOf(path);
        try
        {
            await using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, useAsync: true))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await StagedFileReplacement.MoveOverAsync(staged, path).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (!TryWriteInPlace(path, bytes.Span)) throw;
            }
        }
        finally
        {
            TryDelete(staged);
        }
    }

    /// <summary>Replaces a file with text in UTF-8 without a byte order mark, or creates it, without blocking the thread.</summary>
    /// <param name="path">The file.</param>
    /// <param name="text">The whole text.</param>
    /// <param name="cancellationToken">Cancels the write until the file is replaced; the replacement itself is not canceled.</param>
    /// <returns>A task that completes once the file is replaced.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder of the file does not exist.</exception>
    /// <exception cref="IOException">The file cannot be written or replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the replacement.</exception>
    public static ValueTask WriteAllTextAsync(string path, string text, CancellationToken cancellationToken)
        => WriteAllBytesAsync(path, Encode(text, Utf8WithoutBom), cancellationToken);

    /// <summary>Reads a whole file that a write of this class may be replacing.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Its bytes, or <see langword="null"/> when there is no file (never during a replacement).</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public static byte[]? ReadAllBytes(string path)
    {
        using var stream = StagedFileReplacement.OpenReplacedFile(path);
        if (stream is null) return null;
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>Reads a whole file that a write of this class may be replacing, without blocking the thread.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Its bytes, or <see langword="null"/> when there is no file (never during a replacement).</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static async ValueTask<byte[]?> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = await StagedFileReplacement.OpenReplacedFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (stream is null) return null;
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    /// <summary>Reads the whole text of a file that a write of this class may be replacing: UTF-8 unless a byte order mark says otherwise.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Its text, or <see langword="null"/> when there is no file (never during a replacement).</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public static string? ReadAllText(string path)
    {
        using var stream = StagedFileReplacement.OpenReplacedFile(path);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Reads the whole text of a file as <see cref="ReadAllText"/> does, without blocking the thread.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Its text, or <see langword="null"/> when there is no file (never during a replacement).</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static async ValueTask<string?> ReadAllTextAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = await StagedFileReplacement.OpenReplacedFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
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

    // A staged file left by a write that failed; none once it was moved.
    private static void TryDelete(string staged)
    {
        try
        {
            File.Delete(staged);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
