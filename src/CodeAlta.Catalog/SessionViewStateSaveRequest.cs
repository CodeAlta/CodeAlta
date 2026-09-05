namespace CodeAlta.Catalog;

/// <summary>A frozen UI-state YAML document and the last acknowledged raw-byte revision.</summary>
/// <param name="Yaml">Complete YAML, normally frozen with SessionViewCatalog.CreateViewStateSaveRequest.</param>
/// <param name="ExpectedRevision">Loaded or successfully saved revision, never a just-fetched write baseline.</param>
public sealed record SessionViewStateSaveRequest(string Yaml, TextFileRevision ExpectedRevision);

/// <summary>Separates a committed revision from a conflict. I/O/validation/cancellation failures throw.</summary>
/// <param name="AcknowledgedRevision">Committed revision, null on conflict.</param>
/// <param name="CurrentRevision">Committed revision or observed conflicting revision (not an acknowledgment).</param>
public sealed record SessionViewStateSaveResult(TextFileRevision? AcknowledgedRevision, TextFileRevision CurrentRevision)
{
    /// <summary>Gets whether nothing was committed because the expected revision differed.</summary>
    public bool IsConflict => AcknowledgedRevision is null;
}
