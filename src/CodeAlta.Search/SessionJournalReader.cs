using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Search;

/// <summary>What the reader of a journal keeps between two readings, in the state of its source.</summary>
internal sealed class JournalReadState
{
    /// <summary>Gets or sets the space of the session, from its header.</summary>
    public string? SpaceId { get; set; }

    /// <summary>Gets or sets the project of the session, from its header.</summary>
    public string? ProjectId { get; set; }

    /// <summary>Gets or sets the time the records are the session's own from (a fork), in milliseconds since 1970, or 0.</summary>
    public long CountedFrom { get; set; }

    /// <summary>Gets or sets the run of the turn in progress.</summary>
    public string? OpenRun { get; set; }

    /// <summary>Gets or sets the last assistant message of the turn in progress, the final answer once the turn ends.</summary>
    public PendingAnswer? Answer { get; set; }

    /// <summary>Gets or sets the time of the last record, in milliseconds since 1970.</summary>
    public long LastRecordAt { get; set; }

    /// <summary>Gets or sets the first prompt, for the item of the session.</summary>
    public string? FirstPrompt { get; set; }

    /// <summary>Gets or sets when the session started, in milliseconds since 1970.</summary>
    public long CreatedAt { get; set; }
}

/// <summary>The last assistant message of a turn in progress.</summary>
internal sealed class PendingAnswer
{
    /// <summary>Gets or sets its content id.</summary>
    public string ContentId { get; set; } = "";

    /// <summary>Gets or sets its run.</summary>
    public string? RunId { get; set; }

    /// <summary>Gets or sets its text.</summary>
    public string Text { get; set; } = "";

    /// <summary>Gets or sets when it was written, in milliseconds since 1970.</summary>
    public long At { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JournalReadState))]
internal sealed partial class SearchJsonContext : JsonSerializerContext;

/// <summary>What a reading of a journal gave.</summary>
internal sealed class JournalReadResult
{
    /// <summary>Gets the documents: prompts, final answers whose turn ended, the session and its notes.</summary>
    public List<SearchDocument> Documents { get; } = [];

    /// <summary>Gets refs that are gone, such as notes cleared.</summary>
    public List<string> Removed { get; } = [];

    /// <summary>Gets or sets the offset to resume from.</summary>
    public long Offset { get; set; }

    /// <summary>Gets or sets the mark of the first line, or null while it is not complete.</summary>
    public byte[]? Fingerprint { get; set; }

    /// <summary>Gets or sets whether the file was not the one the cursor was made for and was read from its start.</summary>
    public bool Restarted { get; set; }

    /// <summary>Gets or sets the state to keep.</summary>
    public required JournalReadState State { get; set; }

    /// <summary>Gets or sets whether the reading reached the last complete line.</summary>
    public bool ReachedEnd { get; set; }
}

/// <summary>What the reader needs to know of a session that its journal does not say.</summary>
/// <param name="Title">The title the session has now.</param>
/// <param name="ProjectName">The name of its project, for the text given to the embedding model, or null.</param>
internal readonly record struct JournalContext(string Title, string? ProjectName);

/// <summary>
/// Reads a session journal from an offset to its last complete line and keeps only what search indexes: the prompt
/// and the final answer of each turn, the header for the space and the project, and the notes. Every other line
/// (tool outputs, snapshots, reasoning, which are most of a journal) is passed over by its first bytes, never parsed.
/// </summary>
internal sealed class SessionJournalReader
{
    /// <summary>A turn quiet for this long is taken as ended: CodeAlta was closed during its run.</summary>
    public static readonly TimeSpan OpenTurnTimeout = TimeSpan.FromHours(1);

    private const int FirstLineHashBytes = 4096;
    private const int MaximumParsedLine = 8 * 1024 * 1024;
    private const int MaximumMessageCharacters = 64 * 1024;

    /// <summary>Reads a journal.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="stream">A readable, seekable stream of its journal.</param>
    /// <param name="previous">Where the last reading stopped, or null.</param>
    /// <param name="context">What the journal does not say.</param>
    /// <param name="now">The time now, for turns left open.</param>
    /// <param name="maxBytes">The reading stops at the first line end after this many bytes.</param>
    /// <param name="cancellationToken">Stops the reading at a line end.</param>
    /// <returns>What it gave.</returns>
    public JournalReadResult Read(string sessionId, Stream stream, SearchSourceState? previous, JournalContext context, DateTimeOffset now, long maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(stream);
        var fingerprint = ReadFingerprint(stream);
        var offset = previous?.Offset ?? 0;
        var state = previous?.State is { Length: > 0 } json ? JsonSerializer.Deserialize(json, SearchJsonContext.Default.JournalReadState) : null;
        var restarted = false;
        if (previous is not null && (offset > stream.Length || fingerprint is null || previous.Fingerprint is null || !previous.Fingerprint.AsSpan().SequenceEqual(fingerprint)))
        {
            // Not the file the cursor was made for: a rewind, a move or a change of provider wrote it again.
            restarted = offset > 0 || previous.Fingerprint is not null;
            offset = 0;
            state = null;
        }

        var result = new JournalReadResult { State = state ?? new JournalReadState(), Restarted = restarted, Fingerprint = fingerprint };
        var scan = new Scan(sessionId, result, context);
        result.Offset = scan.Run(stream, offset, maxBytes, cancellationToken);
        result.ReachedEnd = result.Offset >= stream.Length || scan.OnlyIncompleteLineLeft;
        // A turn left open for an hour ended without its end being written: its last answer is its final one.
        if (result.ReachedEnd && result.State.OpenRun is not null && now.ToUnixTimeMilliseconds() - result.State.LastRecordAt > (long)OpenTurnTimeout.TotalMilliseconds)
        {
            scan.CloseTurn();
        }

        scan.WriteSession();
        return result;
    }

    /// <summary>Reads the mark of the first line of a journal: a hash of its first bytes, or null while the line is not complete.</summary>
    /// <param name="stream">The stream.</param>
    /// <returns>The mark.</returns>
    public static byte[]? ReadFingerprint(Stream stream)
    {
        stream.Position = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            while (length < MaximumParsedLine)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) return null;
                var span = buffer.AsSpan(0, read);
                var newline = span.IndexOf((byte)'\n');
                var piece = newline >= 0 ? span[..newline] : span;
                if (length < FirstLineHashBytes) hash.AppendData(piece[..(int)Math.Min(piece.Length, FirstLineHashBytes - length)]);
                length += piece.Length;
                if (newline >= 0)
                {
                    hash.AppendData(BitConverter.GetBytes(length));
                    return hash.GetHashAndReset()[..16];
                }
            }

            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private enum LineKind : byte
    {
        Skip,
        User,
        Assistant,
        Idle,
        Header,
        Notes,
        Parse,
    }

    private sealed class Scan(string sessionId, JournalReadResult result, JournalContext context)
    {
        private readonly JournalReadState _state = result.State;
        private byte[] _line = new byte[4096];
        private int _lineLength;
        private long _lineBytes;
        private LineKind _kind;
        private bool _classified;

        public bool OnlyIncompleteLineLeft { get; private set; }

        public long Run(Stream stream, long offset, long maxBytes, CancellationToken cancellationToken)
        {
            stream.Position = offset;
            var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
            var position = offset;
            var lineStart = offset;
            var consumed = offset;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    var span = buffer.AsSpan(0, read);
                    while (span.Length > 0)
                    {
                        var newline = span.IndexOf((byte)'\n');
                        var piece = newline >= 0 ? span[..newline] : span;
                        Append(piece);
                        position += piece.Length;
                        if (newline < 0) break;
                        position++;
                        EndLine();
                        consumed = position;
                        lineStart = position;
                        span = span[(newline + 1)..];
                        if (consumed - offset >= maxBytes || cancellationToken.IsCancellationRequested) return consumed;
                    }
                }

                OnlyIncompleteLineLeft = position > lineStart;
                return consumed;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private void Append(ReadOnlySpan<byte> piece)
        {
            if (piece.IsEmpty) return;
            if (!_classified)
            {
                // The first bytes tell what the line is; only the lines search reads are kept whole.
                Keep(piece[..Math.Min(piece.Length, 160 - (int)Math.Min(_lineBytes, 160))]);
                _lineBytes += piece.Length;
                if (_lineLength >= 160 || _lineBytes > _lineLength)
                {
                    _kind = Classify(_line.AsSpan(0, _lineLength));
                    _classified = true;
                    if (_kind != LineKind.Skip && _lineBytes > _lineLength) Keep(piece[(piece.Length - (int)(_lineBytes - _lineLength))..]);
                }

                return;
            }

            _lineBytes += piece.Length;
            if (_kind != LineKind.Skip) Keep(piece);
        }

        private void Keep(ReadOnlySpan<byte> bytes)
        {
            if (_lineLength + bytes.Length > MaximumParsedLine)
            {
                _kind = LineKind.Skip;
                return;
            }

            if (_lineLength + bytes.Length > _line.Length) Array.Resize(ref _line, Math.Max(_line.Length * 2, _lineLength + bytes.Length));
            bytes.CopyTo(_line.AsSpan(_lineLength));
            _lineLength += bytes.Length;
        }

        private void EndLine()
        {
            try
            {
                var kind = _classified ? _kind : Classify(_line.AsSpan(0, _lineLength));
                if (kind != LineKind.Skip && _lineLength > 0) Handle(kind, _line.AsSpan(0, _lineLength));
            }
            catch (JsonException)
            {
                // A line that is not valid JSON is passed over.
            }
            finally
            {
                _lineLength = 0;
                _lineBytes = 0;
                _classified = false;
                _kind = LineKind.Skip;
            }
        }

        private static LineKind Classify(ReadOnlySpan<byte> start)
        {
            start = start.TrimStart((byte)' ');
            if (start.StartsWith("{\"$type\":\"contentCompleted\","u8))
            {
                var rest = start[28..];
                if (rest.StartsWith("\"kind\":\"User\""u8)) return LineKind.User;
                if (rest.StartsWith("\"kind\":\"Assistant\""u8)) return LineKind.Assistant;
                return rest.StartsWith("\"kind\":"u8) ? LineKind.Skip : LineKind.Parse;
            }

            if (start.StartsWith("{\"$type\":\"sessionUpdate\","u8))
            {
                var rest = start[25..];
                if (rest.StartsWith("\"kind\":\"Idle\""u8)) return LineKind.Idle;
                return rest.StartsWith("\"kind\":"u8) ? LineKind.Skip : LineKind.Parse;
            }

            if (start.StartsWith("{\"$type\":\"raw\",\"backendEventType\":\"codealta.sessionHeader\""u8)) return LineKind.Header;
            if (start.StartsWith("{\"$type\":\"notes\""u8)) return LineKind.Notes;
            if (start.StartsWith("{\"$type\":\""u8)) return LineKind.Skip;
            // Another writer that does not put the type first: read it to know.
            return start.Length > 0 && start[0] == (byte)'{' ? LineKind.Parse : LineKind.Skip;
        }

        private void Handle(LineKind kind, ReadOnlySpan<byte> line)
        {
            using var document = JsonDocument.Parse(line.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (kind == LineKind.Parse)
            {
                var type = String(root, "$type");
                var recordKind = String(root, "kind");
                kind = (type, recordKind) switch
                {
                    ("contentCompleted", "User") => LineKind.User,
                    ("contentCompleted", "Assistant") => LineKind.Assistant,
                    ("sessionUpdate", "Idle") => LineKind.Idle,
                    ("notes", _) => LineKind.Notes,
                    ("raw", _) when String(root, "backendEventType") == "codealta.sessionHeader" => LineKind.Header,
                    _ => LineKind.Skip,
                };
                if (kind == LineKind.Skip) return;
            }

            if (kind == LineKind.Header)
            {
                if (root.TryGetProperty("raw", out var header) && header.ValueKind == JsonValueKind.Object)
                {
                    _state.SpaceId = String(header, "space_id") ?? _state.SpaceId;
                    _state.ProjectId = String(header, "project_ref") ?? _state.ProjectId;
                    if (String(header, "created_at") is { } created && DateTimeOffset.TryParse(created, System.Globalization.CultureInfo.InvariantCulture, out var at)) _state.CreatedAt = at.ToUnixTimeMilliseconds();
                    if (header.TryGetProperty("forked_from", out var fork) && fork.ValueKind == JsonValueKind.Object
                        && String(fork, "counted_from") is { } counted && DateTimeOffset.TryParse(counted, System.Globalization.CultureInfo.InvariantCulture, out var from))
                    {
                        _state.CountedFrom = from.ToUnixTimeMilliseconds();
                    }
                }

                return;
            }

            var timestamp = String(root, "timestamp") is { } time && DateTimeOffset.TryParse(time, System.Globalization.CultureInfo.InvariantCulture, out var stamp)
                ? stamp.ToUnixTimeMilliseconds() : _state.LastRecordAt;
            if (timestamp > _state.LastRecordAt) _state.LastRecordAt = timestamp;
            // A fork starts with the records of its source: those are indexed in the source.
            if (_state.CountedFrom > 0 && timestamp < _state.CountedFrom) return;
            var runId = String(root, "runId");
            switch (kind)
            {
                case LineKind.User:
                {
                    var content = String(root, "content");
                    var contentId = String(root, "contentId");
                    if (content is null || contentId is null) return;
                    // A user message in the run in progress steers it: only the prompt that starts a run is indexed.
                    if (runId is not null && runId == _state.OpenRun) return;
                    CloseTurn();
                    _state.OpenRun = runId;
                    var role = root.TryGetProperty("source_session_id", out var source) && source.ValueKind == JsonValueKind.String ? SearchRole.Agent : SearchRole.User;
                    _state.FirstPrompt ??= Truncate(content, 2000);
                    AddMessage(contentId, runId, content, role, timestamp);
                    break;
                }

                case LineKind.Assistant:
                {
                    var content = String(root, "content");
                    var contentId = String(root, "contentId");
                    if (content is null || contentId is null || content.Trim().Length == 0) return;
                    if (_state.OpenRun is null) _state.OpenRun = runId;
                    else if (runId is not null && runId != _state.OpenRun)
                    {
                        CloseTurn();
                        _state.OpenRun = runId;
                    }

                    _state.Answer = new PendingAnswer { ContentId = contentId, RunId = runId, Text = Truncate(content, MaximumMessageCharacters), At = timestamp };
                    break;
                }

                case LineKind.Idle:
                    if (runId is null || runId == _state.OpenRun || _state.OpenRun is null) CloseTurn();
                    break;

                case LineKind.Notes:
                {
                    var markdown = String(root, "markdown");
                    var reference = "note:" + sessionId;
                    if (string.IsNullOrWhiteSpace(markdown))
                    {
                        result.Documents.RemoveAll(document => document.Ref == reference);
                        if (!result.Removed.Contains(reference)) result.Removed.Add(reference);
                    }
                    else
                    {
                        result.Removed.Remove(reference);
                        result.Documents.RemoveAll(document => document.Ref == reference);
                        result.Documents.Add(Document(reference, SearchKind.Note, markdown, timestamp, null, SearchRole.None));
                    }

                    break;
                }
            }
        }

        /// <summary>Ends the turn in progress: its last answer is its final one.</summary>
        public void CloseTurn()
        {
            if (_state.Answer is { } answer)
            {
                AddMessage(answer.ContentId, answer.RunId, answer.Text, SearchRole.Assistant, answer.At);
            }

            _state.Answer = null;
            _state.OpenRun = null;
        }

        private void AddMessage(string contentId, string? runId, string text, SearchRole role, long at)
        {
            text = Truncate(text, MaximumMessageCharacters);
            if (text.Trim().Length == 0) return;
            result.Documents.Add(Document($"msg:{sessionId}/{contentId}", SearchKind.Message, text, at, runId, role));
        }

        public void WriteSession()
        {
            if (_state.FirstPrompt is null) return;
            var at = _state.CreatedAt > 0 ? _state.CreatedAt : _state.LastRecordAt;
            result.Documents.Add(Document("session:" + sessionId, SearchKind.Session, _state.FirstPrompt, at, null, SearchRole.None));
        }

        private SearchDocument Document(string reference, SearchKind kind, string text, long at, string? runId, SearchRole role)
            => new(reference, kind, context.Title, text, DateTimeOffset.FromUnixTimeMilliseconds(at))
            {
                SpaceId = _state.SpaceId ?? "default",
                ProjectId = string.IsNullOrEmpty(_state.ProjectId) ? null : _state.ProjectId,
                SessionId = sessionId,
                RunId = runId,
                Role = role,
                EmbeddingTitle = context.ProjectName is { Length: > 0 } project ? $"{context.Title} · {project}" : context.Title,
            };

        private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

        private static string? String(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
