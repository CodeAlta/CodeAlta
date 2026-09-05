using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace CodeAlta.Catalog;

/// <summary>Contains a complete decoded file and the identity of the bytes that were read.</summary>
/// <param name="Text">Literal text, with original newline sequences and final-newline presence.</param>
/// <param name="Encoding">The detected Unicode encoding.</param>
/// <param name="HasByteOrderMark">Whether the bytes begin with an encoding BOM.</param>
/// <param name="LastWriteTimeUtc">Advisory filesystem timestamp; not used for conditional saves.</param>
/// <param name="Revision">Identity of the raw file bytes.</param>
public sealed record TextFileSnapshot(
    string Text,
    Encoding Encoding,
    bool HasByteOrderMark,
    DateTimeOffset LastWriteTimeUtc,
    TextFileRevision Revision);

/// <summary>Requests a conditional save of a complete text document at a trusted local path.</summary>
/// <param name="FullPath">Target path, supplied by trusted application code, not an access grant.</param>
/// <param name="Text">Complete literal text; no newline normalization is performed.</param>
/// <param name="Encoding">UTF-8, or BOM-bearing UTF-16/UTF-32, in either byte order.</param>
/// <param name="HasByteOrderMark">Whether to emit the encoding BOM.</param>
/// <param name="ExpectedRevision">Previously observed bytes, or missing for create-only saves.</param>
public sealed record TextFileSaveRequest(
    string FullPath,
    string Text,
    Encoding Encoding,
    bool HasByteOrderMark,
    TextFileRevision ExpectedRevision);

/// <summary>Reports either an acknowledged save or a conflicting current content identity.</summary>
/// <param name="Snapshot">The saved snapshot, or null when no write was committed due to conflict.</param>
/// <param name="CurrentRevision">Saved revision on success, or observed conflicting revision.</param>
public sealed record TextFileSaveResult(TextFileSnapshot? Snapshot, TextFileRevision CurrentRevision)
{
    /// <summary>Gets whether the expected revision differed from the target's content identity.</summary>
    [MemberNotNullWhen(false, nameof(Snapshot))]
    public bool IsConflict => Snapshot is null;
}
