namespace CodeAlta.LiveTool;

/// <summary>
/// The audit log of a server, as the person of this computer may read it (<c>doc/fleet/security-and-privacy.md</c>, audit log),
/// for <c>alta fleet audit</c>: read only, for people whose role or grants give <c>audit.read</c>.
/// </summary>
public interface IAltaFleetAudit
{
    /// <summary>Reads a page of the audit log, newest first.</summary>
    /// <param name="server">The server, by its link id or its address; null for the one online.</param>
    /// <param name="filters">The filters; each one narrows.</param>
    /// <param name="before">Records before this sequence; null for the newest.</param>
    /// <param name="limit">The size of the page, 1 to 500.</param>
    /// <param name="cancellationToken">Stops waiting for the server.</param>
    /// <returns>The records, or why they could not be read (<c>no_server</c>, <c>forbidden</c>, <c>unreachable</c>...).</returns>
    Task<AltaFleetAuditPage> ReadAuditAsync(string? server, AltaFleetAuditFilters filters, long? before, int limit, CancellationToken cancellationToken);

    /// <summary>Checks the hash chain of the whole audit log at the server.</summary>
    /// <param name="server">The server; null for the one online.</param>
    /// <param name="cancellationToken">Stops waiting for the server.</param>
    /// <returns>The check, or why it could not be made.</returns>
    Task<AltaFleetAuditCheck> VerifyAuditAsync(string? server, CancellationToken cancellationToken);
}

/// <summary>The filters of the audit log; an absent one says nothing.</summary>
/// <param name="Actions">Actions, exact (<c>device.approve</c>) or by prefix (<c>team.</c>).</param>
/// <param name="Person">The person behind the records, by id.</param>
/// <param name="Device">The device that asked, by id.</param>
/// <param name="Result"><c>success</c>, <c>failure</c> or an error code.</param>
/// <param name="Target">What the records were done to.</param>
/// <param name="From">The first time, included.</param>
/// <param name="To">The time the records end before.</param>
/// <param name="Text">Words the records hold.</param>
public sealed record AltaFleetAuditFilters(IReadOnlyList<string>? Actions, string? Person, string? Device, string? Result, string? Target, DateTimeOffset? From,
    DateTimeOffset? To, string? Text);

/// <summary>A page of the audit log.</summary>
/// <param name="Status"><c>ok</c>, or why not.</param>
/// <param name="Message">What was said of a failure.</param>
public sealed record AltaFleetAuditPage(string Status, string? Message)
{
    /// <summary>Gets the address of the server.</summary>
    public string? Server { get; init; }

    /// <summary>Gets the records, newest first.</summary>
    public IReadOnlyList<AltaFleetAuditRecord> Records { get; init; } = [];

    /// <summary>Gets the cursor of the next, older page; null at the end.</summary>
    public long? Before { get; init; }
}

/// <summary>A record of the audit log, without content.</summary>
/// <param name="Sequence">Its position in the chain.</param>
/// <param name="Time">When it was recorded.</param>
/// <param name="Action">What was done.</param>
/// <param name="Result"><c>success</c> or the code of a refusal.</param>
/// <param name="Person">The person behind it, by name when the server knows it.</param>
/// <param name="Device">The device that asked, by name when the server knows it.</param>
/// <param name="Target">What it was done to.</param>
/// <param name="Address">The address of the request.</param>
/// <param name="Details">More facts, as JSON text.</param>
public sealed record AltaFleetAuditRecord(long Sequence, DateTimeOffset Time, string Action, string Result, string? Person, string? Device, string? Target,
    string? Address, string? Details);

/// <summary>The check of the chain of the audit log.</summary>
/// <param name="Status"><c>ok</c> when the server answered, or why not.</param>
/// <param name="Message">What was said of a failure.</param>
/// <param name="Valid">Whether the chain holds.</param>
/// <param name="Count">How many records were checked.</param>
/// <param name="BrokenAt">The first record that does not hold.</param>
/// <param name="HeadHash">The hash of the last record that holds.</param>
public sealed record AltaFleetAuditCheck(string Status, string? Message, bool Valid, long Count, long? BrokenAt, string? HeadHash);
