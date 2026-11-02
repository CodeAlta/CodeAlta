using System.Globalization;
using System.Text;

namespace CodeAlta.Node;

/// <summary>The options of <c>altanode logs</c>.</summary>
internal sealed class NodeLogOptions
{
    /// <summary>Gets the profile options.</summary>
    public NodeProfileOptions Profile { get; } = new();

    /// <summary>Gets or sets whether the log is followed as it grows.</summary>
    public bool Follow { get; set; }

    /// <summary>Gets or sets how many of the last lines are printed, as typed; null for 50.</summary>
    public string? Lines { get; set; }
}

/// <summary>
/// <c>altanode logs</c>: the last lines of the log of the node (<c>logs/altanode.log</c> of its state root), and with
/// <c>--follow</c> what it writes next, across the daily rolls of the file, until Ctrl+C.
/// </summary>
internal sealed class NodeLogCommand(TextWriter output, TextWriter error)
{
    private const int DefaultLines = 50;
    private const int ChunkSize = 64 * 1024;

    /// <summary>Gets or sets what stops <c>--follow</c>, for tests; none by default.</summary>
    public CancellationToken Stop { get; init; }

    /// <summary>Gets or sets how often <c>--follow</c> looks at the file.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public async ValueTask<int> RunAsync(NodeLogOptions options)
    {
        if (options.Profile.Resolve(out var invalid) is not { } profile)
        {
            await error.WriteLineAsync("altanode: " + invalid).ConfigureAwait(false);
            return NodeExitCodes.InvalidUsage;
        }

        var lines = DefaultLines;
        if (options.Lines is { } text && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out lines) || lines > 100_000))
        {
            await error.WriteLineAsync("altanode: --lines takes 0 to 100000.").ConfigureAwait(false);
            return NodeExitCodes.InvalidUsage;
        }

        var path = NodeLogging.GetLogFilePath(profile.StateRoot);
        if (!File.Exists(path) && !options.Follow)
        {
            await error.WriteLineAsync($"altanode logs: no log yet at {path}: the node writes it once it runs (altanode run, or its service).").ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        long position = 0;
        if (File.Exists(path))
        {
            using var stream = Open(path);
            if (stream is not null)
            {
                await output.WriteAsync(Tail(stream, lines)).ConfigureAwait(false);
                position = stream.Length;
            }
        }

        if (!options.Follow) return NodeExitCodes.Success;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Stop);
        using var signals = new NodeSignals(stop.Cancel, stop.Cancel, static () => Environment.Exit(NodeExitCodes.Failure));
        await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await FollowAsync(path, position, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }

        return NodeExitCodes.Success;
    }

    /// <summary>Reads the last lines of a log, from its end, without reading the whole file.</summary>
    /// <param name="stream">The log, seekable.</param>
    /// <param name="lines">How many lines.</param>
    /// <returns>The lines, each with its end of line.</returns>
    internal static string Tail(Stream stream, int lines)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var length = stream.Length;
        if (lines <= 0 || length == 0) return "";
        var start = length;
        var found = 0;
        var buffer = new byte[ChunkSize];
        // The end of line of the last line does not start a line of its own.
        var skipLast = true;
        while (start > 0)
        {
            var count = (int)Math.Min(ChunkSize, start);
            start -= count;
            stream.Position = start;
            stream.ReadExactly(buffer, 0, count);
            for (var index = count - 1; index >= 0; index--)
            {
                if (buffer[index] != (byte)'\n') continue;
                if (skipLast && start + index == length - 1)
                {
                    skipLast = false;
                    continue;
                }

                if (++found == lines)
                {
                    stream.Position = start + index + 1;
                    return ReadToEnd(stream);
                }
            }
        }

        stream.Position = 0;
        return ReadToEnd(stream);
    }

    private async Task FollowAsync(string path, long position, CancellationToken cancellationToken)
    {
        var stream = Open(path);
        if (stream is not null) stream.Position = Math.Min(position, stream.Length);
        var mismatches = 0;
        try
        {
            while (true)
            {
                if (stream is null)
                {
                    stream = Open(path);
                }
                else
                {
                    if (stream.Length < stream.Position) stream.Position = 0;
                    var text = ReadToEnd(stream);
                    if (text.Length > 0)
                    {
                        await output.WriteAsync(text).ConfigureAwait(false);
                        await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    // The log rolls by moving the file aside: the path then names a new file, read from its start. A
                    // length that differs twice in a row is a new file, not a write between the two reads.
                    var current = new FileInfo(path);
                    mismatches = current.Exists && current.Length != stream.Length ? mismatches + 1 : 0;
                    if (mismatches >= 2)
                    {
                        await stream.DisposeAsync().ConfigureAwait(false);
                        stream = Open(path);
                        mismatches = 0;
                        continue;
                    }
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    // The log is shared: the node writes it and rolls it (moves it aside) while it is read.
    private static FileStream? Open(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadToEnd(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var text = reader.ReadToEnd();
        return text;
    }
}
