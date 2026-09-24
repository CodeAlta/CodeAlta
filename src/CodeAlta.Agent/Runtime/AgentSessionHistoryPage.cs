namespace CodeAlta.Agent.Runtime;

/// <summary>A continuation for bounded UTF-8 journal reads; not a transactional snapshot or authenticated token.</summary>
/// <param name="SessionId">Selected durable session identity, independent of persisted event identities.</param>
/// <param name="Length">Observed journal byte length.</param>
/// <param name="LastWriteUtcTicks">Observed UTC last-write ticks; same-stamp rewrites can go undetected.</param>
/// <param name="Offset">Record-boundary byte offset immediately after LF: the next record for forward reads, or the exclusive end of an older reverse page.</param>
public sealed record AgentSessionHistoryCursor(string SessionId, long Length, long LastWriteUtcTicks, long Offset);

/// <summary>A canonical persisted event with its journal position; no conversation reconstruction is applied.</summary>
/// <param name="Offset">Physical record's starting byte offset.</param>
/// <param name="Event">Canonical event, retaining its actual stored provider/runtime identity.</param>
public sealed record AgentSessionHistoryEntry(long Offset, AgentEvent Event);

/// <summary>One bounded history page. Blank records and four metadata snapshot types consume work but are not returned.</summary>
/// <param name="Entries">Events from at most 100 physical records, 256 KiB input and 128 KiB per record.</param>
/// <param name="Next">Next unconsumed position (forward) or exclusive older-page boundary (reverse); null at the respective observed end.</param>
/// <param name="TailOmitted">A malformed final JSON record was omitted under the complete-reader tail policy.</param>
public sealed record AgentSessionHistoryPage(IReadOnlyList<AgentSessionHistoryEntry> Entries, AgentSessionHistoryCursor? Next, bool TailOmitted);

/// <summary>A structured bounded-history limitation or validation failure, containing no filesystem paths or payloads.</summary>
public sealed class AgentSessionHistoryException : IOException
{
    /// <summary>Initializes a history failure.</summary>
    /// <param name="code">Stable failure code; consumers must map recognized codes rather than display arbitrary values.</param>
    /// <exception cref="ArgumentException">The code is empty.</exception>
    public AgentSessionHistoryException(string code) : base("The persisted history page could not be read.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>Gets the failure code, such as invalid_cursor, history_changed, unsupported_format or record_too_large.</summary>
    public string Code { get; }
}
