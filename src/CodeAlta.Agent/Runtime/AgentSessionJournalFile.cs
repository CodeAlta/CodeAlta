using System.Collections.Concurrent;
using System.Text;

namespace CodeAlta.Agent.Runtime;

internal sealed class AgentSessionJournalFile
{
    private const int SharingViolation = 32;
    private const int LockViolation = 33;
    private static readonly TimeSpan FileRetryDelay = TimeSpan.FromMilliseconds(10);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pathLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NotesScanState> _notesScans = new(StringComparer.Ordinal);

    public async Task AppendLinesAsync(
        string path,
        IReadOnlyList<string> lines,
        Encoding encoding,
        CancellationToken cancellationToken)
        => await AppendLinesIfAsync(
                path,
                lines,
                encoding,
                static (_, _) => Task.FromResult(true),
                cancellationToken)
            .ConfigureAwait(false);

    public async Task AppendLinesIfAsync(
        string path,
        IReadOnlyList<string> lines,
        Encoding encoding,
        Func<string, CancellationToken, Task<bool>> shouldAppendAsync,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentNullException.ThrowIfNull(shouldAppendAsync);
        if (lines.Count == 0)
        {
            return;
        }

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Path '{path}' did not resolve to a parent directory.");
        Directory.CreateDirectory(directory);

        await WithPathLockAsync(
                path,
                async () =>
                {
                    if (!await shouldAppendAsync(path, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    await AppendLinesCoreAsync(path, lines, encoding, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task EnsureFirstLineAsync(
        string path,
        string firstLine,
        Encoding encoding,
        Func<string?, bool> isExpectedFirstLine,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(firstLine);
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentNullException.ThrowIfNull(isExpectedFirstLine);

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Path '{path}' did not resolve to a parent directory.");
        Directory.CreateDirectory(directory);

        await WithPathLockAsync(
                path,
                () => EnsureFirstLineCoreAsync(path, firstLine, encoding, isExpectedFirstLine, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task AppendLinesWithRequiredFirstLineAsync(
        string path,
        string firstLine,
        IReadOnlyList<string> lines,
        Encoding encoding,
        Func<string?, bool> isExpectedFirstLine,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(firstLine);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentNullException.ThrowIfNull(isExpectedFirstLine);

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Path '{path}' did not resolve to a parent directory.");
        Directory.CreateDirectory(directory);

        await WithPathLockAsync(
                path,
                async () =>
                {
                    await EnsureFirstLineCoreAsync(path, firstLine, encoding, isExpectedFirstLine, cancellationToken).ConfigureAwait(false);
                    if (lines.Count == 0)
                    {
                        return;
                    }

                    await AppendLinesCoreAsync(path, lines, encoding, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task AppendLineAsync(
        string path,
        string line,
        Encoding encoding,
        CancellationToken cancellationToken)
        => AppendLinesAsync(path, [line], encoding, cancellationToken);

    // Notes use the existing journal gate, but never create a missing journal. Once the
    // stream is open and cancellation is checked, finish the single record and feedback
    // without caller cancellation: cancellation must not masquerade as a rollback.
    //
    // Every existing record must be valid before the append: a malformed tail must not become an
    // interior record that hides an acknowledged notes write. Records in the remembered notes
    // prefix were already deserialized and are not read again. The rest is read through `open`
    // without the journal gate, as a notes read does, which leaves only what is written in the
    // meantime to validate strictly on the append handle.
    public Task AppendNotesLineAsync(
        string path, string line, Encoding encoding, Func<string, CancellationToken, Task<Stream>> open,
        Func<Task> committed, CancellationToken cancellationToken)
        => AppendNotesLineAsync(
            path, line, encoding, open,
            static file => new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, useAsync: true),
            committed, cancellationToken);

    // Opening the append handle is a parameter only for tests that record what is read through it.
    internal async Task AppendNotesLineAsync(
        string path, string line, Encoding encoding, Func<string, CancellationToken, Task<Stream>> open,
        Func<string, Stream> openAppend, Func<Task> committed, CancellationToken cancellationToken)
    {
        // Only for the prefix it remembers; the journal can still change before the append is admitted.
        await ReadLatestNotesAsync(path, open, cancellationToken).ConfigureAwait(false);
        var fullPath = Path.GetFullPath(path);
        var state = _notesScans.GetOrAdd(fullPath, static _ => new NotesScanState());
        // The scan state is taken before the journal gate, in the order a notes read takes them.
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WithPathLockAsync(path, async () =>
            {
                // Retry only opening, never a partially written record.
                await using (var stream = await RetryFileOperationAsync(
                    () => Task.FromResult(openAppend(path)), cancellationToken).ConfigureAwait(false))
                {
                    // Validate through the canonical parser on this same handle, before
                    // changing any bytes, with read-only file sharing requested.
                    var validated = await AgentJournalNotesReader.ScanAsync(
                        stream, state.Prefix, stream.Length, new FileInfo(fullPath).LastWriteTimeUtc.Ticks,
                        tolerateIncompleteTail: false, cancellationToken).ConfigureAwait(false);
                    if (!validated.Supported)
                    {
                        // Canonical writes are UTF-8. Tolerant reads may detect legacy BOMs, but
                        // appending UTF-8 to a different encoding would acknowledge unreadable notes.
                        throw new InvalidDataException("Notes cannot append to a non-UTF-8 journal. No journal bytes were changed.");
                    }

                    // The appended record is left to the next scan, which reads it back from the journal.
                    if (validated.Prefix is not null)
                    {
                        state.Prefix = validated.Prefix;
                    }

                    var needsSeparator = false;
                    if (stream.Length != 0)
                    {
                        stream.Seek(-1, SeekOrigin.End);
                        var lastByte = new byte[1];
                        await stream.ReadExactlyAsync(lastByte, cancellationToken).ConfigureAwait(false);
                        needsSeparator = lastByte[0] is not ((byte)'\n' or (byte)'\r');
                    }

                    stream.Seek(0, SeekOrigin.End);
                    cancellationToken.ThrowIfCancellationRequested();
                    await using var writer = new StreamWriter(stream, encoding);
                    if (needsSeparator)
                    {
                        await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, CancellationToken.None).ConfigureAwait(false);
                    }
                    await writer.WriteLineAsync(line.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                    await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                await committed().ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    // Reads the latest notes event without rescanning what an earlier read of this journal already covered.
    // Every store sharing this instance shares the result: an unchanged journal (length and last-write time)
    // is answered without opening it, and a longer one is scanned only past the remembered prefix.
    public async Task<AgentJournalNotesReader.Result> ReadLatestNotesAsync(
        string path, Func<string, CancellationToken, Task<Stream>> open, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(open);
        var fullPath = Path.GetFullPath(path);
        var state = _notesScans.GetOrAdd(fullPath, static _ => new NotesScanState());
        // One scan per journal at a time, so a reader that waited reuses the finished scan instead of repeating it.
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The journal gate only orders this read after admitted writes. The scan runs outside it and stops at
            // the length captured here, so a long first scan delays neither history pages nor appends.
            var (length, lastWriteUtcTicks) = await WithPathLockAsync(path, () =>
            {
                var info = new FileInfo(fullPath);
                return info.Exists
                    ? Task.FromResult((info.Length, info.LastWriteTimeUtc.Ticks))
                    : throw new FileNotFoundException("The session journal does not exist.", fullPath);
            }, cancellationToken).ConfigureAwait(false);
            var previous = state.Prefix;
            if (previous is not null && previous.Offset == length && previous.LastWriteUtcTicks == lastWriteUtcTicks)
            {
                return new(true, previous.Latest, previous);
            }

            await using var stream = await open(path, cancellationToken).ConfigureAwait(false);
            var result = await AgentJournalNotesReader.ScanAsync(stream, previous, length, lastWriteUtcTicks, tolerateIncompleteTail: true, cancellationToken).ConfigureAwait(false);
            if (result.Prefix is not null)
            {
                state.Prefix = result.Prefix;
            }

            return result;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    // Drops the remembered notes prefix of a journal that was deleted or rewritten in place.
    public void ForgetLatestNotes(string path)
        => _notesScans.TryRemove(Path.GetFullPath(path), out _);

    // A same-directory rename is the commit point. No canonical bytes are changed before it.
    // Used for idle provider selection only, not normal streaming event writes.
    internal Task ReplaceWithSnapshotsAsync(string path, AgentHistoryRevision expected, string? header,
        IReadOnlyList<string> snapshots, CancellationToken token)
        => WithPathLockAsync(path, async () =>
        {
            var temporary = path + ".selection-" + Guid.NewGuid().ToString("N");
            var notesKey = Path.GetFullPath(path);
            try
            {
                await using var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                var info = new FileInfo(path);
                if (original.Length != expected.Length || info.LastWriteTimeUtc.Ticks != expected.LastWriteUtcTicks)
                    throw new InvalidOperationException("The session changed before provider selection could commit.");
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
                    if (header is not null)
                    {
                        var count = 0;
                        while (original.ReadByte() is var value && value >= 0 && value != '\n')
                            if (++count > 128 * 1024) throw new InvalidDataException("Session header is oversized.");
                        await writer.WriteLineAsync(header.AsMemory(), token).ConfigureAwait(false);
                        await writer.FlushAsync(token).ConfigureAwait(false);
                    }
                    await original.CopyToAsync(output, token).ConfigureAwait(false);
                    // An extra empty line is harmless and prevents joining a final unterminated record.
                    await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, token).ConfigureAwait(false);
                    foreach (var line in snapshots) await writer.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
                    await writer.FlushAsync(token).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                token.ThrowIfCancellationRequested();
                // Windows replacement needs the read handle closed. The shared journal gate
                // remains held; reject an externally changed path immediately before rename.
                await original.DisposeAsync().ConfigureAwait(false);
                info.Refresh();
                if (info.Length != expected.Length || info.LastWriteTimeUtc.Ticks != expected.LastWriteUtcTicks)
                    throw new InvalidOperationException("The session changed before journal replacement.");
                File.Move(temporary, path, overwrite: true);
                // Nothing fallible follows the durable decision; caches reproject from the changed stamp.
                // A replaced header moves every later record, so the notes prefix is dropped rather than left to its guard.
                _notesScans.TryRemove(notesKey, out _);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, token);

    private static async Task AppendLinesCoreAsync(
        string path,
        IReadOnlyList<string> lines,
        Encoding encoding,
        CancellationToken cancellationToken)
    {
        await RetryFileOperationAsync(
                async () =>
                {
                    await using var stream = new FileStream(
                        path,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read,
                        bufferSize: 4096,
                        useAsync: true);
                    await using var writer = new StreamWriter(stream, encoding);
                    foreach (var line in lines)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task EnsureFirstLineCoreAsync(
        string path,
        string firstLine,
        Encoding encoding,
        Func<string?, bool> isExpectedFirstLine,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Path '{path}' did not resolve to a parent directory.");

        await RetryFileOperationAsync(
                async () =>
                {
                    await using var stream = new FileStream(
                        path,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 81920,
                        useAsync: true);
                    string? existingFirstLine = null;
                    if (stream.Length > 0)
                    {
                        using (var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
                        {
                            existingFirstLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                        }
                    }

                    if (isExpectedFirstLine(existingFirstLine))
                    {
                        return;
                    }

                    stream.Position = 0;
                    if (stream.Length == 0)
                    {
                        await WriteFirstLineAsync(stream, firstLine, encoding, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await PrependFirstLineAsync(stream, directory, path, firstLine, encoding, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteFirstLineAsync(
        FileStream stream,
        string firstLine,
        Encoding encoding,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream, encoding, bufferSize: 4096, leaveOpen: true);
        await writer.WriteLineAsync(firstLine.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task PrependFirstLineAsync(
        FileStream stream,
        string directory,
        string path,
        string firstLine,
        Encoding encoding,
        CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var tempStream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await using (var writer = new StreamWriter(tempStream, encoding, bufferSize: 4096, leaveOpen: true))
                {
                    await writer.WriteLineAsync(firstLine.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                stream.Position = 0;
                await stream.CopyToAsync(tempStream, cancellationToken).ConfigureAwait(false);
                await tempStream.FlushAsync(cancellationToken).ConfigureAwait(false);

                tempStream.Position = 0;
                stream.Position = 0;
                stream.SetLength(0);
                await tempStream.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public async Task WithPathLockAsync(
        string path,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        await WithPathLockAsync(
                path,
                async () =>
                {
                    await action().ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<T> WithPathLockAsync<T>(
        string path,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(action);

        var pathLock = _pathLocks.GetOrAdd(Path.GetFullPath(path), static _ => new SemaphoreSlim(1, 1));
        await pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            pathLock.Release();
        }
    }

    internal static async Task RetryFileOperationAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await RetryFileOperationAsync(
                async () =>
                {
                    await action().ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<T> RetryFileOperationAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
        => await RetryFileOperationAsync(action, maxRetryTime: null, cancellationToken).ConfigureAwait(false);

    internal static async Task<T> RetryFileOperationAsync<T>(
        Func<Task<T>> action,
        TimeSpan? maxRetryTime,
        CancellationToken cancellationToken)
    {
        var startedAt = maxRetryTime is null ? default : TimeProvider.System.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (IOException ex) when (IsRetryableFileAccessException(ex))
            {
                if (HasRetryWindowElapsed(startedAt, maxRetryTime))
                {
                    throw;
                }

                await Task.Delay(FileRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                if (HasRetryWindowElapsed(startedAt, maxRetryTime))
                {
                    throw;
                }

                await Task.Delay(FileRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool HasRetryWindowElapsed(long startedAt, TimeSpan? maxRetryTime)
        => maxRetryTime is not null && TimeProvider.System.GetElapsedTime(startedAt) >= maxRetryTime.Value;

    private static bool IsRetryableFileAccessException(IOException ex)
    {
        var errorCode = ex.HResult & 0xFFFF;
        return errorCode is SharingViolation or LockViolation ||
            ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("locked", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NotesScanState
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);

        // Read and replaced only while Gate is held.
        internal AgentJournalNotesReader.Prefix? Prefix;
    }
}
