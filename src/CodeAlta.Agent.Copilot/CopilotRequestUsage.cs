using System.Buffers;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Copilot;

/// <summary>
/// What GitHub bills for one request, as the Copilot endpoint reports it in the stream of its answer: a
/// <c>copilot_usage</c> object beside the events of the protocol, with the cost in nano AI units and the tokens
/// it counted as input, cache read, cache write and output.
/// </summary>
internal sealed class CopilotRequestUsage
{
    /// <summary>The unit of the cost: one AI credit is one AI unit, a billion nano AI units.</summary>
    public const string CostUnit = "AI credits";

    private const double NanoPerCredit = 1_000_000_000d;

    private readonly Lock _gate = new();
    private long? _totalNanoAiu;
    private long? _cacheReadTokens;
    private long? _cacheWriteTokens;

    /// <summary>Reads one line of the stream; a line without the usage of Copilot is skipped.</summary>
    public void ObserveLine(ReadOnlySpan<byte> line)
    {
        if (line.IndexOf("\"copilot_usage\""u8) < 0)
        {
            return;
        }

        var start = line.IndexOf((byte)'{');
        if (start < 0)
        {
            return;
        }

        try
        {
            var reader = new Utf8JsonReader(line[start..]);
            using var document = JsonDocument.ParseValue(ref reader);
            if (!TryFindUsage(document.RootElement, out var usage))
            {
                return;
            }

            long? cacheRead = null;
            long? cacheWrite = null;
            if (usage.TryGetProperty("token_details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var detail in details.EnumerateArray())
                {
                    if (detail.ValueKind != JsonValueKind.Object ||
                        !detail.TryGetProperty("token_type", out var type) || type.ValueKind != JsonValueKind.String ||
                        !detail.TryGetProperty("token_count", out var count) || !TryGetCount(count, out var tokens))
                    {
                        continue;
                    }

                    if (type.ValueEquals("cache_read"u8))
                    {
                        cacheRead = (cacheRead ?? 0) + tokens;
                    }
                    else if (type.ValueEquals("cache_write"u8))
                    {
                        cacheWrite = (cacheWrite ?? 0) + tokens;
                    }
                }
            }

            long? total = usage.TryGetProperty("total_nano_aiu", out var nano) && TryGetCount(nano, out var value) ? value : null;
            lock (_gate)
            {
                // A request that is sent again reports again: the last answer is the one that is kept.
                _totalNanoAiu = total;
                _cacheReadTokens = cacheRead;
                _cacheWriteTokens = cacheWrite;
            }
        }
        catch (JsonException)
        {
            // Not the usage: an event whose text holds the name.
        }
    }

    // What is not a count is left out: reading the usage never fails the answer it comes with.
    private static bool TryGetCount(JsonElement element, out long count)
    {
        count = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out count) && count >= 0;
    }

    /// <summary>Reads one line of the stream; a line without the usage of Copilot is skipped.</summary>
    public void ObserveLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Contains("\"copilot_usage\"", StringComparison.Ordinal))
        {
            ObserveLine(Encoding.UTF8.GetBytes(line));
        }
    }

    /// <summary>Adds what Copilot billed to the usage of the turn: the cost in AI credits and the cache tokens.</summary>
    public AgentTurnResponse Apply(AgentTurnResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        long? total;
        long? cacheRead;
        long? cacheWrite;
        lock (_gate)
        {
            total = _totalNanoAiu;
            cacheRead = _cacheReadTokens;
            cacheWrite = _cacheWriteTokens;
        }

        if (response.Usage is not { LastOperation: { } operation } usage || (total is null && cacheRead is null && cacheWrite is null))
        {
            return response;
        }

        return response with
        {
            Usage = usage with
            {
                LastOperation = operation with
                {
                    Cost = total is { } nano ? nano / NanoPerCredit : operation.Cost,
                    CostUnit = total is null ? operation.CostUnit : CostUnit,
                    CachedInputTokens = operation.CachedInputTokens ?? cacheRead,
                    CacheWriteTokens = operation.CacheWriteTokens ?? cacheWrite,
                },
            },
        };
    }

    // The usage is a property of the event, or of the answer the event carries.
    private static bool TryFindUsage(JsonElement root, out JsonElement usage)
    {
        usage = default;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (root.TryGetProperty("copilot_usage", out usage) && usage.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        return root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object &&
               response.TryGetProperty("copilot_usage", out usage) && usage.ValueKind == JsonValueKind.Object;
    }
}

/// <summary>Passes the stream of an answer through, and reads its lines for the usage Copilot reports.</summary>
internal sealed class CopilotUsageSseStream(Stream inner, CopilotRequestUsage usage) : Stream
{
    private readonly ArrayBufferWriter<byte> _line = new();

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        Scan(buffer[..read], read == 0 && !buffer.IsEmpty);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Scan(buffer.Span[..read], read == 0 && !buffer.IsEmpty);
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void Scan(ReadOnlySpan<byte> bytes, bool end)
    {
        while (!bytes.IsEmpty)
        {
            var newline = bytes.IndexOf((byte)'\n');
            if (newline < 0)
            {
                _line.Write(bytes);
                return;
            }

            if (_line.WrittenCount == 0)
            {
                usage.ObserveLine(bytes[..newline]);
            }
            else
            {
                _line.Write(bytes[..newline]);
                usage.ObserveLine(_line.WrittenSpan);
                _line.ResetWrittenCount();
            }

            bytes = bytes[(newline + 1)..];
        }

        if (end && _line.WrittenCount > 0)
        {
            usage.ObserveLine(_line.WrittenSpan);
            _line.ResetWrittenCount();
        }
    }
}
