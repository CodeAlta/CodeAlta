using System.Collections.Concurrent;
using System.Text;

namespace CodeAlta.Agent.Runtime;

internal sealed class AgentSessionJournalFile
{
    private const int SharingViolation = 32;
    private const int LockViolation = 33;
    private static readonly TimeSpan FileRetryDelay = TimeSpan.FromMilliseconds(10);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pathLocks = new(StringComparer.Ordinal);

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
}
