using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// Reads the transcript the CLI keeps of a conversation (<c>~/.claude/projects/&lt;folder&gt;/&lt;id&gt;.jsonl</c>) into
/// the turns a session of CodeAlta records. There is no official API for it: the rules are those of the SDK of Claude
/// Code (the main chain, its leaf, the compactions, the tool results of parallel calls), and whatever is not
/// understood (a bad or half-written line, an unknown entry, field or block) is skipped.
/// </summary>
internal static partial class ClaudeCodeTranscriptReader
{
    // A tool result the CLI saved beside the transcript is read back up to this size; a larger one keeps its preview.
    private const int MaxPersistedOutputBytes = 4 * 1024 * 1024;

    // How much of the file before a position is searched at once for the entries a new line names.
    private const int SearchChunkBytes = 1024 * 1024;

    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 256 };

    /// <summary>Reads the whole conversation of a transcript.</summary>
    /// <param name="transcriptPath">The path of the <c>&lt;id&gt;.jsonl</c> file.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>All the turns of the main chain, or <see cref="ClaudeCodeTranscriptReadStatus.Missing"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="transcriptPath"/> is empty.</exception>
    public static ClaudeCodeTranscriptReadResult Read(string transcriptPath, CancellationToken cancellationToken = default)
        => ReadAfter(transcriptPath, null, null, cancellationToken);

    /// <summary>
    /// Reads the turns written after an entry of the conversation, from where a previous read stopped when that is
    /// still valid. A read from the position gives the same result as a read of the whole file; when it cannot be
    /// sure to, it reads the whole file.
    /// </summary>
    /// <param name="transcriptPath">The path of the <c>&lt;id&gt;.jsonl</c> file.</param>
    /// <param name="afterUuid">
    /// The entry the turns follow (the leaf recorded last), or null for all of them. When the CLI never wrote that
    /// entry, the first line written below it is where the turns start.
    /// </param>
    /// <param name="position">
    /// Where a previous read stopped, or null. It is used only with <paramref name="afterUuid"/>, and only while the
    /// file did not shrink and has the same first line; otherwise the whole file is read.
    /// </param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>
    /// The turns after <paramref name="afterUuid"/>; <see cref="ClaudeCodeTranscriptReadStatus.CannotFollow"/> when it is
    /// not an ancestor of the current leaf; <see cref="ClaudeCodeTranscriptReadStatus.LeafNotFound"/> when it is not in
    /// the file and no line names it; <see cref="ClaudeCodeTranscriptReadStatus.Unchanged"/> when nothing was written
    /// since <paramref name="position"/>.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="transcriptPath"/> is empty.</exception>
    public static ClaudeCodeTranscriptReadResult ReadAfter(
        string transcriptPath,
        string? afterUuid,
        ClaudeCodeTranscriptPosition? position,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcriptPath);
        var sessionId = Path.GetFileNameWithoutExtension(transcriptPath);
        FileStream stream;
        DateTime lastWriteTime;
        try
        {
            stream = OpenShared(transcriptPath);
            lastWriteTime = File.GetLastWriteTimeUtc(transcriptPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Missing(sessionId);
        }

        try
        {
            using (stream)
            {
                var length = stream.Length;
                var firstLineHash = HashFirstLine(stream);
                if (afterUuid is not null &&
                    position is { Offset: > 0 } previous &&
                    string.Equals(previous.FirstLineHash, firstLineHash, StringComparison.Ordinal) &&
                    length >= previous.Offset &&
                    EndsALine(stream, previous.Offset))
                {
                    if (length == previous.Offset)
                    {
                        return new ClaudeCodeTranscriptReadResult(
                            ClaudeCodeTranscriptReadStatus.Unchanged,
                            sessionId,
                            [],
                            afterUuid,
                            null,
                            null,
                            null,
                            ClaudeCodeTranscriptOrigin.Unknown,
                            null,
                            previous with { Length = length, LastWriteTime = lastWriteTime },
                            FullRead: false);
                    }

                    if (TryReadIncrementally(transcriptPath, stream, sessionId, afterUuid, previous, length, lastWriteTime, firstLineHash, cancellationToken) is { } incremental)
                    {
                        return incremental;
                    }
                }

                return ReadWhole(transcriptPath, stream, sessionId, afterUuid, length, lastWriteTime, firstLineHash, cancellationToken);
            }
        }
        catch (IOException)
        {
            return Missing(sessionId);
        }
    }

    private static ClaudeCodeTranscriptReadResult Missing(string sessionId)
        => new(ClaudeCodeTranscriptReadStatus.Missing, sessionId, [], null, null, null, null, ClaudeCodeTranscriptOrigin.Unknown, null, null, FullRead: true);

    private static ClaudeCodeTranscriptReadResult ReadWhole(
        string transcriptPath,
        FileStream stream,
        string sessionId,
        string? afterUuid,
        long length,
        DateTime lastWriteTime,
        string firstLineHash,
        CancellationToken cancellationToken)
    {
        var buffer = ReadRange(stream, 0, length);
        var scan = Scan(buffer, 0, cancellationToken);
        var lastLines = BridgeUnwrittenParents(scan);
        ApplyCompactions(scan);
        var (leaf, terminal) = FindLeaf(scan);
        var chain = leaf is null ? [] : WithHistoryBeforeCompactions(scan, WalkChain(scan, leaf));
        var entries = ReattachSiblings(scan, chain);

        var status = ClaudeCodeTranscriptReadStatus.Read;
        if (afterUuid is not null)
        {
            var index = entries.FindIndex(node => string.Equals(node.Uuid, afterUuid, StringComparison.Ordinal));
            if (index >= 0)
            {
                entries = WrittenAfter(entries, index + 1, entries[index]);
            }
            else if (scan.Nodes.ContainsKey(afterUuid))
            {
                status = ClaudeCodeTranscriptReadStatus.CannotFollow;
                entries = [];
            }
            else if (scan.Nodes.Values
                         .Where(node => string.Equals(node.WrittenParent, afterUuid, StringComparison.Ordinal))
                         .MinBy(static node => node.Position) is { } anchor)
            {
                // The CLI never wrote the entry (a tool result CodeAlta was streamed, for one), but wrote the next line
                // below it: the turns start with that line.
                index = entries.IndexOf(anchor);
                status = index >= 0 ? ClaudeCodeTranscriptReadStatus.Read : ClaudeCodeTranscriptReadStatus.CannotFollow;
                entries = index >= 0 ? [anchor, .. WrittenAfter(entries, index + 1, anchor)] : [];
            }
            else
            {
                status = ClaudeCodeTranscriptReadStatus.LeafNotFound;
                entries = [];
            }
        }

        var folder = Path.GetDirectoryName(transcriptPath) ?? string.Empty;
        var turns = BuildTurns(buffer, entries, folder, sessionId, cancellationToken);
        var title = scan.CustomTitle ??
                    ReadCustomTitleFile(folder, sessionId) ??
                    scan.AiTitle ??
                    scan.LastPrompt ??
                    scan.Summary ??
                    scan.FirstPrompt;
        return new ClaudeCodeTranscriptReadResult(
            status,
            sessionId,
            turns,
            leaf?.Uuid ?? afterUuid,
            scan.Cost,
            title,
            scan.FirstEntrypoint,
            ToOrigin(scan.FirstEntrypoint),
            scan.FirstCwd,
            new ClaudeCodeTranscriptPosition(
                scan.End,
                length,
                lastWriteTime,
                firstLineHash,
                leaf?.Uuid,
                QuietTail(scan, leaf?.Uuid, terminal, null),
                leaf?.MessageId,
                lastLines),
            FullRead: true);
    }

    // Reads the lines written after the position. Returns null whenever the result could differ from the one of a read
    // of the whole file: a compaction, more lines of the model message the previous read ended in, a tool result or a
    // leaf that hangs from an entry before the part, a branch from an older entry, an entry asked for that the part
    // does not reach. The whole file is then read, which decides.
    private static ClaudeCodeTranscriptReadResult? TryReadIncrementally(
        string transcriptPath,
        FileStream stream,
        string sessionId,
        string afterUuid,
        ClaudeCodeTranscriptPosition previous,
        long length,
        DateTime lastWriteTime,
        string firstLineHash,
        CancellationToken cancellationToken)
    {
        var buffer = ReadRange(stream, previous.Offset, length);
        var scan = Scan(buffer, previous.Offset, cancellationToken);
        if (scan.HasCompaction)
        {
            return null;
        }

        // More lines of the model message the previous read ended in: the message, and the results of its calls, are
        // gathered from lines on both sides of the position.
        if (previous.LeafMessageId is { } leafMessage &&
            scan.Nodes.Values.Any(node => node.IsMainSide && node.Type == "assistant" && node.MessageId == leafMessage))
        {
            return null;
        }

        // The parents the part names but does not hold: entries before the position, or entries the CLI never wrote.
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var uuid in (ReadOnlySpan<string?>)[previous.Leaf, previous.Tail])
        {
            if (uuid is not null)
            {
                known.Add(uuid);
            }
        }

        var lastLinesBefore = previous.LastLines ?? [];
        known.UnionWith(lastLinesBefore.Select(static line => line.Uuid));

        var unknown = scan.Nodes.Values
            .Where(static node => node.IsMainSide)
            .Select(static node => node.WrittenParent)
            .Append(afterUuid)
            .OfType<string>()
            .Where(uuid => !scan.Nodes.ContainsKey(uuid) && !known.Contains(uuid))
            .ToHashSet(StringComparer.Ordinal);
        var writtenBefore = unknown.Count == 0 ? [] : FindEntriesBefore(stream, previous.Offset, unknown, cancellationToken);
        if (!scan.Nodes.ContainsKey(afterUuid) && !known.Contains(afterUuid) && !writtenBefore.Contains(afterUuid))
        {
            // The CLI never wrote the entry asked for: where the turns start is decided on the whole file.
            return null;
        }

        var lastLines = BridgeUnwrittenParents(
            scan,
            uuid => scan.Nodes.ContainsKey(uuid) || known.Contains(uuid) || writtenBefore.Contains(uuid),
            lastLinesBefore,
            mainSideOnly: true);
        var header = ReadHeader(transcriptPath);
        var search = FindLeaf(scan, partOfFile: true);
        if (search.Leaf is not { } leaf)
        {
            // No entry of the part ends the chain in a message of its own: the leaf is the one before the part when the
            // part only hangs below it (attachments, system lines), or when nothing in the part can end the chain.
            var stays = search.EscapedAt is { } escapedAt
                ? escapedAt == previous.Leaf || (previous.Tail is not null && escapedAt == previous.Tail)
                : !search.HadCandidates;
            if (!stays || previous.Leaf is null || afterUuid != previous.Leaf)
            {
                return null;
            }

            return new ClaudeCodeTranscriptReadResult(
                ClaudeCodeTranscriptReadStatus.Read,
                sessionId,
                [],
                afterUuid,
                scan.Cost,
                header?.Title,
                header?.Entrypoint,
                header?.Origin ?? ClaudeCodeTranscriptOrigin.Unknown,
                header?.Cwd,
                new ClaudeCodeTranscriptPosition(
                    scan.End,
                    length,
                    lastWriteTime,
                    firstLineHash,
                    previous.Leaf,
                    search.Terminal is { } terminal ? QuietTail(scan, previous.Leaf, terminal, previous) : previous.Tail,
                    previous.LeafMessageId,
                    lastLines),
                FullRead: false);
        }

        // The new entries from the leaf up to the entry asked for, or to the tail of the previous read when the entry
        // asked for is the leaf it found.
        var chain = new List<Node>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var node = leaf;
        var reached = false;
        while (node is not null && seen.Add(node.Uuid))
        {
            if (string.Equals(node.Uuid, afterUuid, StringComparison.Ordinal))
            {
                reached = true;
                break;
            }

            chain.Add(node);
            if (node.Parent is not { } parent)
            {
                break;
            }

            if (string.Equals(parent, afterUuid, StringComparison.Ordinal) ||
                (string.Equals(parent, previous.Tail, StringComparison.Ordinal) && string.Equals(previous.Leaf, afterUuid, StringComparison.Ordinal)))
            {
                reached = true;
                break;
            }

            node = scan.Nodes.GetValueOrDefault(parent);
        }

        if (!reached)
        {
            return null;
        }

        chain.Reverse();
        var entries = ReattachSiblings(scan, chain);

        // A tool result that hangs from an entry before the part and is not on the chain belongs to a call made before
        // the position: the whole file puts it back, this part cannot.
        var shown = entries.Select(static entry => entry.Uuid).ToHashSet(StringComparer.Ordinal);
        if (scan.Nodes.Values.Any(result => result.IsMainSide &&
                                            result.IsToolResultLine &&
                                            !scan.Nodes.ContainsKey(result.Parent!) &&
                                            !shown.Contains(result.Uuid)))
        {
            return null;
        }

        var folder = Path.GetDirectoryName(transcriptPath) ?? string.Empty;
        return new ClaudeCodeTranscriptReadResult(
            ClaudeCodeTranscriptReadStatus.Read,
            sessionId,
            BuildTurns(buffer, entries, folder, sessionId, cancellationToken),
            leaf.Uuid,
            scan.Cost,
            header?.Title,
            header?.Entrypoint,
            header?.Origin ?? ClaudeCodeTranscriptOrigin.Unknown,
            header?.Cwd,
            new ClaudeCodeTranscriptPosition(
                scan.End,
                length,
                lastWriteTime,
                firstLineHash,
                leaf.Uuid,
                QuietTail(scan, leaf.Uuid, search.Terminal, previous),
                leaf.MessageId,
                lastLines),
            FullRead: false);
    }

    // The entries after an index that were written after the entry the turns follow. One written before it is shown
    // after it only when it is put back with the model message of that entry (the result of a parallel call that
    // ends the chain): the read that found that entry had it, and showed it already.
    private static List<Node> WrittenAfter(List<Node> entries, int start, Node after)
        => entries.Skip(start).Where(node => node.Position > after.Position).ToList();

    /// <summary>Maps the <c>entrypoint</c> the CLI writes to where the turn was made.</summary>
    /// <param name="entrypoint">The entrypoint, or null.</param>
    /// <returns>The origin.</returns>
    public static ClaudeCodeTranscriptOrigin ToOrigin(string? entrypoint)
        => entrypoint switch
        {
            null or "" => ClaudeCodeTranscriptOrigin.Unknown,
            "claude-vscode" => ClaudeCodeTranscriptOrigin.VsCode,
            "cli" => ClaudeCodeTranscriptOrigin.Terminal,
            "sdk-cli" or "sdk-ts" or "sdk-py" => ClaudeCodeTranscriptOrigin.Programmatic,
            _ => ClaudeCodeTranscriptOrigin.Other,
        };

    // Every file the reader reads is opened so that the CLI can go on writing, renaming or deleting it.
    private static FileStream OpenShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    // Reads a whole file up to a size; null when it is larger.
    private static byte[]? ReadSmallFile(string path, long maxBytes)
    {
        using var stream = OpenShared(path);
        return stream.Length > maxBytes ? null : ReadRange(stream, 0, stream.Length);
    }

    /// <exception cref="IOException">The range is larger than an array holds (a file over 2 GB).</exception>
    private static byte[] ReadRange(FileStream stream, long from, long to)
    {
        var count = Math.Max(0, to - from);
        if (count > Array.MaxLength)
        {
            throw new IOException($"The transcript is too large to be read ({to} bytes).");
        }

        var buffer = new byte[count];
        stream.Position = from;
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static bool EndsALine(FileStream stream, long offset)
    {
        stream.Position = offset - 1;
        return stream.ReadByte() == '\n';
    }

    private static string HashFirstLine(FileStream stream)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunk = new byte[16 * 1024];
        stream.Position = 0;
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            var end = chunk.AsSpan(0, read).IndexOf((byte)'\n');
            hash.AppendData(chunk, 0, end < 0 ? read : end);
            if (end >= 0)
            {
                break;
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset(), 0, 16);
    }

    // Which of these uuids are entries written before the offset: the lines are searched as bytes for `"uuid":"<id>"`
    // (a key the CLI writes without spaces; in a string of the content its quotes would be escaped), and a line found
    // is parsed to check that it is an entry with that uuid. Much cheaper than reading the part again as entries.
    private static HashSet<string> FindEntriesBefore(FileStream stream, long end, HashSet<string> uuids, CancellationToken cancellationToken)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var patterns = uuids.Select(static uuid => (Uuid: uuid, Bytes: Encoding.UTF8.GetBytes($"\"uuid\":\"{uuid}\""))).ToList();
        var overlap = patterns.Max(static pattern => pattern.Bytes.Length);
        var chunk = new byte[SearchChunkBytes + overlap];
        for (long start = 0; start < end && patterns.Count > found.Count; start += SearchChunkBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(chunk.Length, end - start);
            stream.Position = start;
            stream.ReadExactly(chunk.AsSpan(0, count));
            foreach (var (uuid, bytes) in patterns)
            {
                if (found.Contains(uuid))
                {
                    continue;
                }

                var span = chunk.AsSpan(0, count);
                for (var index = span.IndexOf(bytes); index >= 0; index = NextIndex(span, bytes, index))
                {
                    if (IsEntryAt(stream, start + index, end, uuid))
                    {
                        found.Add(uuid);
                        break;
                    }
                }
            }
        }

        return found;

        static int NextIndex(ReadOnlySpan<byte> span, byte[] bytes, int index)
        {
            var next = span[(index + 1)..].IndexOf(bytes);
            return next < 0 ? -1 : index + 1 + next;
        }
    }

    private static bool IsEntryAt(FileStream stream, long offset, long end, string uuid)
    {
        // The line around the match, found by blocks.
        var block = new byte[64 * 1024];
        var lineStart = offset;
        while (lineStart > 0)
        {
            var from = Math.Max(0, lineStart - block.Length);
            var count = (int)(lineStart - from);
            stream.Position = from;
            stream.ReadExactly(block.AsSpan(0, count));
            var newline = block.AsSpan(0, count).LastIndexOf((byte)'\n');
            if (newline >= 0)
            {
                lineStart = from + newline + 1;
                break;
            }

            lineStart = from;
        }

        var lineEnd = offset;
        while (lineEnd < end)
        {
            var count = (int)Math.Min(block.Length, end - lineEnd);
            stream.Position = lineEnd;
            stream.ReadExactly(block.AsSpan(0, count));
            var newline = block.AsSpan(0, count).IndexOf((byte)'\n');
            if (newline >= 0)
            {
                lineEnd += newline;
                break;
            }

            lineEnd += count;
        }

        var line = ReadRange(stream, lineStart, lineEnd);
        using var document = TryParse(line, 0, line.Length);
        return document?.RootElement is { ValueKind: JsonValueKind.Object } root &&
               ClaudeCodeJson.GetString(root, "type") is "user" or "assistant" or "system" or "attachment" or "progress" &&
               string.Equals(ClaudeCodeJson.GetString(root, "uuid"), uuid, StringComparison.Ordinal);
    }

    // The newest entry below the leaf, when only entries that are not shown (system lines, attachments other than a
    // queued prompt) lie between them: the next prompt hangs from it, and a read from the position finds it there. In
    // a part of the file, the walk may reach the leaf or the tail of the previous read before the part.
    private static string? QuietTail(ScanResult scan, string? leaf, Node? terminal, ClaudeCodeTranscriptPosition? previous)
    {
        if (leaf is null || terminal is null || terminal.Uuid == leaf)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var node = terminal; node is not null && seen.Add(node.Uuid);)
        {
            if (node.Uuid == leaf)
            {
                return terminal.Uuid;
            }

            if (node.IsCompactBoundary || node is { Type: "attachment", AttachmentType: "queued_command" })
            {
                return null;
            }

            if (node.Parent is not { } parent)
            {
                return null;
            }

            if (parent == leaf ||
                (previous is not null && previous.Leaf == leaf && previous.Tail is not null && parent == previous.Tail))
            {
                return terminal.Uuid;
            }

            node = scan.Nodes.GetValueOrDefault(parent);
        }

        return null;
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
        => value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
            ? timestamp
            : null;
}
