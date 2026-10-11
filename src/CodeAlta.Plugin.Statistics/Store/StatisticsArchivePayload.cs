using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Agent;

namespace CodeAlta.Plugin.Statistics.Store;

// An explicit allowlist of projection cells, never a reducer checkpoint, SQL script, prompt or tool payload.
internal sealed record ArchiveCell(long? Number = null, string? Text = null)
{
    internal object? Value => Text is not null ? Text : Number is { } number ? number : null;
}

internal sealed record ArchiveTable(string Name, string[] Columns, ArchiveCell[][] Rows);

internal sealed record StatisticsArchivePayload
{
    public int ArchiveVersion { get; init; } = 1;
    public int FactsVersion { get; init; }
    public required string SessionId { get; init; }
    public SessionStatisticsSource? Source { get; init; }
    public long ReadOffset { get; init; }
    public long SourceLength { get; init; }
    public int FloorQuarter { get; init; }
    public bool FullSourceVerified { get; init; }
    public required string[] RecordedMetrics { get; init; }
    public required ArchiveTable[] Tables { get; init; }

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, StatisticsArchiveJsonContext.Default.StatisticsArchivePayload);

    public static StatisticsArchivePayload Deserialize(byte[] bytes)
        => JsonSerializer.Deserialize(bytes, StatisticsArchiveJsonContext.Default.StatisticsArchivePayload)
            ?? throw new InvalidDataException("The saved Statistics payload is empty.");
}

[JsonSerializable(typeof(StatisticsArchivePayload))]
internal sealed partial class StatisticsArchiveJsonContext : JsonSerializerContext;
