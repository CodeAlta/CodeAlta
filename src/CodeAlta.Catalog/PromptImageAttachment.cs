namespace CodeAlta.Catalog;

/// <summary>
/// An in-memory image or file payload attached to a prompt. The store validates directly constructed records before
/// persistence. This record does not decode images or authorize renderer access to files.
/// </summary>
/// <param name="Id">A nonempty ASCII letter/digit/hyphen/underscore image identifier.</param>
/// <param name="Title">The nonempty display title, sanitized separately for the filename.</param>
/// <param name="Bytes">The nonempty bytes, owned by the caller.</param>
/// <param name="MediaType">The MIME type, such as <c>image/png</c> or <c>application/pdf</c>.</param>
/// <param name="FileExtension">An ASCII alphanumeric extension of 1–16 characters, optionally prefixed by a dot.</param>
public sealed record PromptImageAttachment(string Id, string Title, byte[] Bytes, string MediaType, string FileExtension)
{
    /// <summary>Copies the payload, including its byte array, for independent submission/queue ownership.</summary>
    /// <exception cref="ArgumentNullException">The byte array is null.</exception>
    public PromptImageAttachment Copy()
    {
        ArgumentNullException.ThrowIfNull(Bytes);
        return this with { Bytes = [.. Bytes] };
    }
}

/// <summary>A saved image or file reference, or a frontend-owned transient reference before saving.</summary>
/// <param name="Title">The display title.</param>
/// <param name="Path">The trusted backend path (or frontend transient identifier), not renderer authorization.</param>
/// <param name="MediaType">The MIME type.</param>
public sealed record PromptImageAttachmentReference(string Title, string Path, string MediaType);
