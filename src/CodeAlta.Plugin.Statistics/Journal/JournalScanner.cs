using System.Buffers;
using System.Text.Json;

namespace CodeAlta.Plugin.Statistics.Journal;

/// <summary>A mark of the first line of a journal: its length and a hash of its start.</summary>
/// <param name="Length">The length of the line in bytes, without its line end.</param>
/// <param name="Hash">The hash of the first bytes of the line.</param>
internal readonly record struct JournalFingerprint(long Length, ulong Hash);

/// <summary>Receives the records of a scan.</summary>
internal interface IJournalRecordSink
{
    /// <summary>Receives a record, in the order of the file.</summary>
    /// <param name="record">The record; it is not reused after the call.</param>
    void OnRecord(JournalRecord record);

    /// <summary>Tells whether a prompt provenance entry was already taken, so that the reader passes over it.</summary>
    /// <param name="idHash">The hash of the identifier of the prompt.</param>
    /// <returns><see langword="true"/> when the entry was seen.</returns>
    bool IsPromptSeen(ulong idHash) => false;
}

/// <summary>The limits of the reader.</summary>
internal sealed class JournalScanOptions
{
    /// <summary>Gets the size of the buffer a file is read with.</summary>
    public int ReadBufferBytes { get; init; } = 256 * 1024;

    /// <summary>
    /// Gets the size above which a record that statistics read is not buffered whole: its start is parsed, its end gives its
    /// time and its run, and the bytes between are counted while they are passed over. At least 640 bytes.
    /// </summary>
    public int MaxParsedRecordBytes { get; init; } = 4 * 1024 * 1024;
}

/// <summary>What a scan of a journal did.</summary>
internal sealed class JournalScanResult
{
    /// <summary>Gets the offset the scan started at.</summary>
    public long StartOffset { get; init; }

    /// <summary>Gets or sets the offset to resume from: the byte after the last complete line that was handled.</summary>
    public long EndOffset { get; set; }

    /// <summary>Gets or sets the length of the file when the scan began.</summary>
    public long FileLength { get; set; }

    /// <summary>Gets or sets the mark of the first line of the file; null when the file has no complete first line yet.</summary>
    public JournalFingerprint? FirstLine { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the file is not the one the scan was told to resume: its first line differs
    /// from the expected one, or it is shorter than the offset. Nothing was read; the caller starts again from 0.
    /// </summary>
    public bool RewriteDetected { get; set; }

    /// <summary>Gets or sets a value indicating whether the scan reached the end of the data that was complete.</summary>
    public bool ReachedEnd { get; set; }

    /// <summary>Gets or sets a value indicating whether the scan stopped because the token was canceled.</summary>
    public bool Canceled { get; set; }

    /// <summary>Gets or sets the bytes of an incomplete last line, left for the next scan.</summary>
    public long PendingBytes { get; set; }

    /// <summary>Gets or sets the number of lines handled, blank ones apart.</summary>
    public long Lines { get; set; }

    /// <summary>Gets or sets the number of records whose payload was parsed.</summary>
    public long ParsedRecords { get; set; }

    /// <summary>Gets or sets the number of records passed over, of which only the time and the run were read.</summary>
    public long SkippedRecords { get; set; }

    /// <summary>Gets or sets the bytes of the lines that were passed over, without being parsed.</summary>
    public long SkippedBytes { get; set; }

    /// <summary>Gets or sets the number of records larger than the bound, of which only the two ends were read.</summary>
    public long OversizeRecords { get; set; }

    /// <summary>Gets or sets the number of lines that were not valid, or lacked a time.</summary>
    public long MalformedLines { get; set; }

    /// <summary>Gets the number of malformed lines by the kind their start announced; indexed by <see cref="JournalRecordKind"/>.</summary>
    public long[] MalformedByKind { get; } = new long[16];

    /// <summary>Gets or sets the number of lines whose start the reader did not recognize.</summary>
    public long UnknownLines { get; set; }

    /// <summary>Gets the number of bytes the scan consumed.</summary>
    public long BytesConsumed => EndOffset - StartOffset;
}

/// <summary>
/// A streaming reader of session journals. It reads a file in chunks from a byte offset to the last complete line, tells
/// the kind of each line from its first bytes, and passes over the bytes statistics do not need (the state snapshots, the
/// tool outputs, the copies of messages, which are most of a journal) without parsing them and without keeping them. A
/// line is never held whole unless statistics read it and it is below a bound.
/// </summary>
/// <remarks>The reader is not thread-safe; one scan at a time.</remarks>
internal sealed class JournalScanner : IDisposable
{
    private const int FirstLineHashBytes = 4096;
    private const long FirstLineScanLimit = 8 * 1024 * 1024;

    private readonly JournalScanOptions _options;
    private readonly int _maxRecordBytes;
    private readonly Utf8StringCache _strings = new();
    private readonly JournalPayloadParser _parser;
    private byte[]? _readBuffer;
    private byte[] _line = [];
    private int _lineFill;
    private long _lineLength;
    private LineMode _mode;
    private JournalRecordKind _kind;
    private RunEndKind _runEnd;
    private bool _oversize;
    private readonly byte[] _tail = new byte[JournalEnvelope.TailBytes];
    private int _tailLength;
    private Func<ulong, bool>? _isPromptSeen;

    /// <summary>Initializes a reader.</summary>
    /// <param name="options">The limits; null for the defaults.</param>
    public JournalScanner(JournalScanOptions? options = null)
    {
        _options = options ?? new JournalScanOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.ReadBufferBytes, 1);
        _maxRecordBytes = Math.Max(_options.MaxParsedRecordBytes, JournalPrefixClassifier.PrefixBytes * 4);
        _parser = new JournalPayloadParser(_strings);
    }

    /// <summary>Reads the mark of the first line of a journal.</summary>
    /// <param name="stream">A readable, seekable stream of the journal.</param>
    /// <returns>The mark; null when the first line is not complete yet.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot seek.</exception>
    public static JournalFingerprint? ReadFirstLine(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The journal stream must be seekable.", nameof(stream));
        }

        stream.Position = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var hash = JournalHash.Start;
            long length = 0;
            var skipBom = true;
            while (length < FirstLineScanLimit)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    return null;
                }

                var span = buffer.AsSpan(0, read);
                if (skipBom)
                {
                    skipBom = false;
                    if (span.StartsWith("﻿"u8))
                    {
                        span = span[3..];
                    }
                }

                var newline = span.IndexOf((byte)'\n');
                var piece = newline >= 0 ? span[..newline] : span;
                if (length < FirstLineHashBytes)
                {
                    hash = JournalHash.Append(hash, piece[..(int)Math.Min(piece.Length, FirstLineHashBytes - length)]);
                }

                length += piece.Length;
                if (newline >= 0)
                {
                    return new JournalFingerprint(length, hash);
                }
            }

            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Reads a journal from an offset to the last complete line.</summary>
    /// <param name="stream">A readable, seekable stream of the journal, opened with sharing flags that let the session write.</param>
    /// <param name="startOffset">The offset of the first byte of a line, 0 for the start of the file.</param>
    /// <param name="expectedFirstLine">The mark of the first line the caller saw when it read before; null to accept any.</param>
    /// <param name="sink">Receives the records.</param>
    /// <param name="maxBytes">The scan stops at the first line end after this many bytes were consumed; <see cref="long.MaxValue"/> for no limit.</param>
    /// <param name="cancellationToken">A token that stops the scan at a line end; the result then says so.</param>
    /// <returns>What the scan did, with the offset to resume from.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="sink"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot seek.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startOffset"/> is negative.</exception>
    public JournalScanResult Scan(
        Stream stream,
        long startOffset,
        JournalFingerprint? expectedFirstLine,
        IJournalRecordSink sink,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The journal stream must be seekable.", nameof(stream));
        }

        var length = stream.Length;
        var firstLine = ReadFirstLine(stream);
        var result = new JournalScanResult { StartOffset = startOffset, EndOffset = startOffset, FileLength = length, FirstLine = firstLine };
        if (startOffset > length || (expectedFirstLine is { } expected && (firstLine is not { } actual || actual != expected)))
        {
            result.RewriteDetected = true;
            return result;
        }

        stream.Position = startOffset;
        _isPromptSeen = sink.IsPromptSeen;
        _readBuffer ??= ArrayPool<byte>.Shared.Rent(_options.ReadBufferBytes);
        ResetLine();
        var buffer = _readBuffer;
        var position = 0;
        var end = 0;
        var lineStart = startOffset;
        var checkBom = startOffset == 0;
        var lines = 0;
        while (true)
        {
            if (position == end)
            {
                end = stream.Read(buffer, 0, Math.Min(buffer.Length, _options.ReadBufferBytes));
                position = 0;
                if (end == 0)
                {
                    result.ReachedEnd = true;
                    result.PendingBytes = _lineLength;
                    return result;
                }

                if (checkBom)
                {
                    checkBom = false;
                    if (end >= 3 && buffer.AsSpan(0, 3).SequenceEqual("﻿"u8))
                    {
                        position = 3;
                        lineStart = 3;
                        result.EndOffset = 3;
                    }
                }
            }

            var span = buffer.AsSpan(position, end - position);
            var newline = span.IndexOf((byte)'\n');
            var piece = newline >= 0 ? span[..newline] : span;
            Append(piece);
            position += piece.Length;
            if (newline < 0)
            {
                continue;
            }

            position++;
            FinishLine(lineStart, sink, result);
            lineStart += _lineLength + 1;
            result.EndOffset = lineStart;
            ResetLine();
            if (result.EndOffset - startOffset >= maxBytes)
            {
                return result;
            }

            if ((++lines & 255) == 0 && cancellationToken.IsCancellationRequested)
            {
                result.Canceled = true;
                return result;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_readBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_readBuffer);
            _readBuffer = null;
        }

        if (_line.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_line);
            _line = [];
        }
    }

    private void ResetLine()
    {
        _lineFill = 0;
        _lineLength = 0;
        _mode = LineMode.Undecided;
        _kind = JournalRecordKind.Unknown;
        _oversize = false;
        _tailLength = 0;
    }

    private void Append(ReadOnlySpan<byte> piece)
    {
        _lineLength += piece.Length;
        while (!piece.IsEmpty)
        {
            switch (_mode)
            {
                case LineMode.Undecided:
                {
                    var take = Math.Min(JournalPrefixClassifier.PrefixBytes - _lineFill, piece.Length);
                    CopyToLine(piece[..take]);
                    piece = piece[take..];
                    if (_lineFill >= JournalPrefixClassifier.PrefixBytes)
                    {
                        Decide();
                    }

                    break;
                }

                case LineMode.Collect:
                {
                    var room = _maxRecordBytes - _lineFill;
                    if (piece.Length <= room)
                    {
                        CopyToLine(piece);
                        piece = default;
                        break;
                    }

                    // Larger than the bound: the start stays for parsing, the rest is passed over, and the end is kept for the envelope.
                    CopyToLine(piece[..room]);
                    piece = piece[room..];
                    _oversize = true;
                    _mode = LineMode.Skip;
                    _tailLength = 0;
                    FeedTail(_line.AsSpan(0, _lineFill));
                    break;
                }

                default:
                    FeedTail(piece);
                    piece = default;
                    break;
            }
        }
    }

    // The start of the line is known: tell what it is, and whether its body is read or passed over.
    private void Decide()
    {
        _kind = JournalPrefixClassifier.Classify(_line.AsSpan(0, _lineFill), out _runEnd);
        if (_kind is JournalRecordKind.Touch or JournalRecordKind.RunEnd or JournalRecordKind.Unknown)
        {
            _mode = LineMode.Skip;
            _tailLength = 0;
            FeedTail(_line.AsSpan(0, _lineFill));
        }
        else
        {
            _mode = LineMode.Collect;
        }
    }

    private void CopyToLine(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        var needed = _lineFill + bytes.Length;
        if (needed > _line.Length)
        {
            var size = Math.Max(Math.Max(needed, 4096), _line.Length * 2);
            size = Math.Min(size, Math.Max(_maxRecordBytes, JournalPrefixClassifier.PrefixBytes));
            size = Math.Max(size, needed);
            var bigger = ArrayPool<byte>.Shared.Rent(size);
            _line.AsSpan(0, _lineFill).CopyTo(bigger);
            if (_line.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_line);
            }

            _line = bigger;
        }

        bytes.CopyTo(_line.AsSpan(_lineFill));
        _lineFill += bytes.Length;
    }

    // Keeps the last bytes of what is passed over: the envelope of the record is at the end of its line.
    private void FeedTail(ReadOnlySpan<byte> piece)
    {
        if (piece.Length >= _tail.Length)
        {
            piece[^_tail.Length..].CopyTo(_tail);
            _tailLength = _tail.Length;
            return;
        }

        var overflow = _tailLength + piece.Length - _tail.Length;
        if (overflow > 0)
        {
            _tail.AsSpan(overflow, _tailLength - overflow).CopyTo(_tail);
            _tailLength -= overflow;
        }

        piece.CopyTo(_tail.AsSpan(_tailLength));
        _tailLength += piece.Length;
    }

    private void FinishLine(long lineStart, IJournalRecordSink sink, JournalScanResult result)
    {
        var lineLength = _lineLength;
        if (_lineLength > 0 && _line.Length > 0 && _lineFill > 0 && _mode != LineMode.Skip && _line[_lineFill - 1] == (byte)'\r')
        {
            _lineFill--;
            lineLength--;
        }

        if (lineLength == 0)
        {
            return;
        }

        if (_mode == LineMode.Undecided)
        {
            Decide();
        }

        result.Lines++;
        JournalRecord? record;
        ReadOnlySpan<byte> tail;
        if (_mode == LineMode.Skip)
        {
            tail = _tail.AsSpan(0, _tailLength);
            if (tail.Length > 0 && tail[^1] == (byte)'\r')
            {
                tail = tail[..^1];
            }

            if (_kind == JournalRecordKind.Unknown)
            {
                result.UnknownLines++;
                return;
            }

            if (!_oversize)
            {
                result.SkippedRecords++;
                result.SkippedBytes += lineLength;
            }

            if (_oversize)
            {
                result.OversizeRecords++;
                result.SkippedBytes += lineLength;
                try
                {
                    record = _parser.Parse(_kind, _line.AsSpan(0, _lineFill), isFinalBlock: false, _isPromptSeen);
                }
                catch (JsonException)
                {
                    record = null;
                }

                if (record is null)
                {
                    result.MalformedLines++;
                result.MalformedByKind[(int)_kind]++;
                    return;
                }

                result.ParsedRecords++;
                record.Oversize = true;
                EstimateOversize(record, lineLength);
            }
            else
            {
                record = _kind == JournalRecordKind.RunEnd ? new RunEndRecord { End = _runEnd } : new TouchRecord();
            }
        }
        else
        {
            var line = _line.AsSpan(0, _lineFill);
            tail = line.Length > JournalEnvelope.TailBytes ? line[^JournalEnvelope.TailBytes..] : line;
            try
            {
                record = _parser.Parse(_kind, line, isFinalBlock: true, _isPromptSeen);
            }
            catch (JsonException)
            {
                record = null;
            }

            if (record is null)
            {
                result.MalformedLines++;
                result.MalformedByKind[(int)_kind]++;
                return;
            }

            result.ParsedRecords++;
        }

        if (!JournalEnvelope.TryRead(tail, out var timestamp, out var runId, out var provider))
        {
            result.MalformedLines++;
                result.MalformedByKind[(int)_kind]++;
            return;
        }

        record.Timestamp = timestamp;
        record.RunId = runId.IsEmpty ? null : _strings.Get(runId);
        record.Provider = provider.IsEmpty ? null : _strings.Get(provider);
        record.Offset = lineStart;
        record.Length = lineLength;
        sink.OnRecord(record);
    }

    // A record that was only read at its two ends has no size of its text: the size of the line, in bytes, stands for it.
    private static void EstimateOversize(JournalRecord record, long lineLength)
    {
        switch (record)
        {
            case UserContentRecord user when user.Chars == 0:
                user.Chars = lineLength;
                break;
            case ContentRecord content when content.Chars == 0:
                content.Chars = lineLength;
                break;
            case ToolRecord tool when tool.ResultBytes == 0 && tool.Phase != ToolPhase.Started:
                tool.ResultBytes = lineLength;
                break;
        }
    }

    private enum LineMode : byte
    {
        Undecided,
        Collect,
        Skip,
    }
}
