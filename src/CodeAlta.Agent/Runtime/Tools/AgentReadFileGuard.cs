using System.Text.Json;

namespace CodeAlta.Agent.Runtime.Tools;

/// <summary>
/// Answers a <c>read_file</c> of the model that asks again for lines it already has: when the lines asked for are within
/// an earlier read of the same file, the file has kept its size and its time of last write since, and the result of that
/// read is still in the conversation the model is sent, the call gets a short note that points to that result instead of
/// the content, as Claude Code does.
/// </summary>
/// <remarks>
/// <para>
/// A session owns one, for the calls of its model only: a call of a script of Code Mode (<c>tools.read_file</c>) returns
/// its value to the script, not to the model, so it is neither answered nor recorded here. A compaction forgets every
/// read (<see cref="Clear"/>), and a read whose result the session no longer sends is not taken as known.
/// </para>
/// <para>The calls of a batch of the model may run at the same time: the state is behind a lock.</para>
/// </remarks>
internal sealed class AgentReadFileGuard
{
    // The reads kept for a file: a session that reads a file in many parts keeps the latest of them.
    private const int MaximumReadsPerFile = 16;

    private readonly int _defaultLineLimit;
    private readonly int _maxLineLimit;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, FileReads> _files = new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Initializes a guard for the limits of lines of the session's <c>read_file</c>.</summary>
    /// <param name="defaultLineLimit">The lines a read gives without a limit.</param>
    /// <param name="maxLineLimit">The most lines a read gives.</param>
    public AgentReadFileGuard(int defaultLineLimit, int maxLineLimit)
    {
        _defaultLineLimit = Math.Max(1, defaultLineLimit);
        _maxLineLimit = Math.Max(_defaultLineLimit, maxLineLimit);
    }

    /// <summary>The state of a file before a read, which the read is recorded with.</summary>
    /// <param name="Path">The full path of the file.</param>
    /// <param name="Length">Its size, in bytes.</param>
    /// <param name="LastWriteUtc">Its time of last write.</param>
    public readonly record struct FileStamp(string Path, long Length, DateTime LastWriteUtc);

    /// <summary>Gives the note that answers a read of lines the model already has, or null when the read must run.</summary>
    /// <param name="arguments">The arguments of the call.</param>
    /// <param name="workingDirectory">The folder relative paths are resolved from.</param>
    /// <param name="isInConversation">Whether a recorded result is still in the conversation the model is sent.</param>
    /// <param name="stamp">The state of the file now, to record the read with when it runs; null when it cannot be recorded.</param>
    /// <returns>The note, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="isInConversation"/> is null.</exception>
    public string? TryAnswer(JsonElement arguments, string? workingDirectory, Func<AgentMessagePart.ToolResult, bool> isInConversation, out FileStamp? stamp)
    {
        ArgumentNullException.ThrowIfNull(isInConversation);
        stamp = StampOf(arguments, workingDirectory);
        if (stamp is not { } current || Requested(arguments) is not { } request)
        {
            return null;
        }

        lock (_gate)
        {
            if (!_files.TryGetValue(current.Path, out var reads))
            {
                return null;
            }

            if (reads.Stamp != current)
            {
                _files.Remove(current.Path);
                return null;
            }

            reads.Ranges.RemoveAll(range => !isInConversation(range.Result));
            var total = reads.Ranges.FirstOrDefault(static range => range.ReachedEnd)?.End;
            foreach (var range in reads.Ranges)
            {
                if (Covers(range, request, total))
                {
                    return FormattableString.Invariant(
                        $"[read_file: {current.Path} has not changed since your read of its lines {range.Start}-{range.End} (call {range.CallId}), whose result is still in this conversation: use that result instead of reading the file again.]");
                }
            }

            return null;
        }
    }

    /// <summary>Records a read whose whole result the model was given.</summary>
    /// <param name="stamp">The state of the file before the read (<see cref="TryAnswer"/>).</param>
    /// <param name="callId">The id of the call.</param>
    /// <param name="value">The value of the read for scripts: its path, its first and last lines and whether more follow.</param>
    /// <param name="text">The text the model was given.</param>
    /// <param name="result">The result the conversation keeps for the call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="callId"/>, <paramref name="text"/> or <paramref name="result"/> is null.</exception>
    public void Record(FileStamp stamp, string callId, JsonElement value, string text, AgentMessagePart.ToolResult result)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(result);
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("startLine", out var startElement) || !startElement.TryGetInt32(out var start)
            || !value.TryGetProperty("endLine", out var endElement) || !endElement.TryGetInt32(out var end)
            || !value.TryGetProperty("more", out var moreElement) || moreElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return;
        }

        // A read that stopped at its limit of characters gave the lines before the one to read on from.
        var stoppedAt = AgentBuiltInToolFactory.ReadFileStoppedAt(text);
        var shownEnd = stoppedAt is { } next ? next - 1 : end;
        if (shownEnd < start || StampOf(stamp.Path) != stamp)
        {
            return;
        }

        var range = new ReadRange(start, shownEnd, stoppedAt is null && !moreElement.GetBoolean(), callId, result);
        lock (_gate)
        {
            if (!_files.TryGetValue(stamp.Path, out var reads) || reads.Stamp != stamp)
            {
                _files[stamp.Path] = reads = new FileReads(stamp);
            }

            reads.Ranges.Add(range);
            if (reads.Ranges.Count > MaximumReadsPerFile)
            {
                reads.Ranges.RemoveAt(0);
            }
        }
    }

    /// <summary>Forgets every read: the conversation was compacted.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _files.Clear();
        }
    }

    private static bool Covers(ReadRange range, (int Offset, int Limit) request, int? total)
    {
        int start;
        if (request.Offset > 0)
        {
            start = request.Offset;
        }
        else if (total is { } lines)
        {
            // From the end: the file is known to its last line.
            start = Math.Max(1, lines + request.Offset + 1);
        }
        else
        {
            return false;
        }

        var end = start + (long)request.Limit - 1;
        return range.Start <= start && start <= range.End && (end <= range.End || range.ReachedEnd);
    }

    private (int Offset, int Limit)? Requested(JsonElement arguments)
    {
        var offset = 1;
        if (arguments.TryGetProperty("offset", out var offsetElement))
        {
            if (offsetElement.ValueKind != JsonValueKind.Number || !offsetElement.TryGetInt32(out offset) || offset == 0)
            {
                return null;
            }
        }

        var limit = _defaultLineLimit;
        if (arguments.TryGetProperty("limit", out var limitElement) && limitElement.ValueKind == JsonValueKind.Number && limitElement.TryGetInt32(out var asked))
        {
            limit = Math.Clamp(asked, 1, _maxLineLimit);
        }

        return (offset, limit);
    }

    private static FileStamp? StampOf(JsonElement arguments, string? workingDirectory)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("path", out var pathElement)
            || pathElement.ValueKind != JsonValueKind.String || pathElement.GetString() is not { Length: > 0 } path)
        {
            return null;
        }

        try
        {
            return StampOf(Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory ?? Environment.CurrentDirectory, path)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FileStamp? StampOf(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? new FileStamp(file.FullName, file.Length, file.LastWriteTimeUtc) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class FileReads(FileStamp stamp)
    {
        public FileStamp Stamp { get; } = stamp;

        public List<ReadRange> Ranges { get; } = [];
    }

    // The lines a read gave the model, whether they reach the end of the file, and the result that holds them.
    private sealed record ReadRange(int Start, int End, bool ReachedEnd, string CallId, AgentMessagePart.ToolResult Result);
}
