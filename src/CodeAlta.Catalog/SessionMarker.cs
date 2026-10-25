using System.Text.Json.Serialization;

namespace CodeAlta.Catalog;

/// <summary>
/// Names a session that a process without the application database wrote into the sessions folder, for the instance
/// that owns the folder to list it (<see cref="SessionMarkerStore"/>).
/// </summary>
/// <param name="SessionId">The session.</param>
/// <param name="JournalPath">The path of its journal relative to the sessions folder, with <c>/</c> separators, such as <c>2026/10/25/&lt;id&gt;.jsonl</c>.</param>
/// <param name="CreatedUtc">When the marker was written.</param>
/// <param name="Writer">The kind of process that wrote it, such as <see cref="SessionLeaseOwner.CliKind"/>.</param>
/// <param name="ProcessId">The id of that process.</param>
public sealed record SessionMarker(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("journalPath")] string JournalPath,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc,
    [property: JsonPropertyName("writer")] string Writer,
    [property: JsonPropertyName("pid")] int ProcessId);
