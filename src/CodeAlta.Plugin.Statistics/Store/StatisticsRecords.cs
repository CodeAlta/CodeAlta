using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Journal;

namespace CodeAlta.Plugin.Statistics.Store;

/// <summary>Where the reading of one journal stopped, as the <c>journal</c> table keeps it.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Offset">The byte after the last complete line that was read.</param>
/// <param name="FileLength">The length of the file when the reading began; zero when the reading did not reach the end.</param>
/// <param name="FileStampTicks">The UTC ticks of the last write of the file when the reading began; zero when the reading did not reach the end.</param>
/// <param name="FirstLine">The mark of the first line of the file.</param>
/// <param name="State">The state of the facts, as JSON.</param>
/// <param name="FactsVersion">The version of the facts the rows of the session were computed with.</param>
/// <param name="FloorQuarter">The first quarter hour that was kept when the session was read; <see cref="int.MinValue"/> when none was left out.</param>
/// <param name="Deleted">Whether the file is gone.</param>
/// <param name="OpenRuns">The runs that had not ended when the reading stopped.</param>
internal sealed record JournalRow(
    string SessionId,
    long Offset,
    long FileLength,
    long FileStampTicks,
    JournalFingerprint? FirstLine,
    byte[] State,
    int FactsVersion,
    int FloorQuarter,
    bool Deleted,
    int OpenRuns = 0)
{
    /// <summary>Gets the cursor to resume the session from.</summary>
    /// <returns>The cursor.</returns>
    public JournalCursor ToCursor() => new(Offset, FirstLine, SessionFactsState.FromUtf8Json(State));
}

/// <summary>What the store writes when one catch-up of a session is saved.</summary>
internal sealed class ApplyRequest
{
    /// <summary>Gets the session.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the facts the reading added.</summary>
    public required FactBatch Batch { get; init; }

    /// <summary>Gets the cursor to save with the facts.</summary>
    public required JournalCursor Cursor { get; init; }

    /// <summary>Gets a value indicating whether the batch replaces every fact of the session, which are removed first.</summary>
    public bool Replace { get; init; }

    /// <summary>Gets the length of the file when the reading began; zero when the reading did not reach the end.</summary>
    public long FileLength { get; init; }

    /// <summary>Gets the UTC ticks of the last write of the file when the reading began; zero when the reading did not reach the end.</summary>
    public long FileStampTicks { get; init; }

    /// <summary>Gets the first quarter hour to keep; facts of an earlier quarter are left out. <see cref="int.MinValue"/> keeps everything.</summary>
    public int FloorQuarter { get; init; } = int.MinValue;

    /// <summary>Gets the version of the facts.</summary>
    public int FactsVersion { get; init; } = SessionFactsState.CurrentVersion;

    /// <summary>Gets entries of the <c>meta</c> table to write in the same transaction.</summary>
    public IReadOnlyDictionary<string, string>? Meta { get; init; }
}

/// <summary>What a saved catch-up changed.</summary>
/// <param name="ChangedDays">The local days whose numbers changed, ascending, as <c>yyyymmdd</c>.</param>
/// <param name="Rows">The rows of facts written.</param>
internal sealed record ApplyResult(IReadOnlyList<int> ChangedDays, int Rows);

/// <summary>A row of the <c>session</c> table.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="ProjectRef">The project the session was created for.</param>
/// <param name="ProjectName">The name the project had when it was last resolved.</param>
/// <param name="Kind">The kind of session.</param>
/// <param name="ParentSessionId">The parent of a sub-agent session.</param>
/// <param name="CreatedByKind">The kind of actor that created the session.</param>
/// <param name="Title">The title.</param>
/// <param name="Provider">The provider the session was created with.</param>
/// <param name="FirstRecord">The earliest record.</param>
/// <param name="LastRecord">The latest record.</param>
/// <param name="Deleted">Whether the journal file is gone.</param>
internal sealed record StoredSession(
    string SessionId,
    string? ProjectRef,
    string? ProjectName,
    string? Kind,
    string? ParentSessionId,
    string? CreatedByKind,
    string? Title,
    string? Provider,
    DateTimeOffset? FirstRecord,
    DateTimeOffset? LastRecord,
    bool Deleted);
