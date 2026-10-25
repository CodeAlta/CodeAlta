namespace CodeAlta.Acp;

/// <summary>
/// A part of a prompt or of a message, read from its <see cref="ContentBlock"/>: a text, an image, an audio clip, an
/// embedded resource or the link to one.
/// </summary>
public abstract record AcpContentPart
{
    private AcpContentPart()
    {
    }

    /// <summary>Reads a content block.</summary>
    /// <param name="block">The block.</param>
    /// <returns>The part, or <see langword="null"/> for a block of a type it does not know or that lacks what its type requires.</returns>
    public static AcpContentPart? Read(ContentBlock block)
    {
        var value = block.Value;
        switch (block.Type)
        {
            case "text" when AcpJson.GetString(value, "text") is { } text:
                return new Text(text);
            case "image" when AcpJson.GetString(value, "data") is { } data && AcpJson.GetString(value, "mimeType") is { } mimeType:
                return new Image(data, mimeType, AcpJson.GetString(value, "uri"));
            case "audio" when AcpJson.GetString(value, "data") is { } data && AcpJson.GetString(value, "mimeType") is { } mimeType:
                return new Audio(data, mimeType);
            case "resource_link" when AcpJson.GetString(value, "uri") is { } uri:
                return new ResourceLink(uri, AcpJson.GetString(value, "name") ?? uri, AcpJson.GetString(value, "mimeType"), AcpJson.GetString(value, "title"));
            case "resource" when AcpJson.TryGetObject(value, "resource", out var resource) && AcpJson.GetString(resource, "uri") is { } uri:
                return new Resource(uri, AcpJson.GetString(resource, "mimeType"), AcpJson.GetString(resource, "text"), AcpJson.GetString(resource, "blob"));
            default:
                return null;
        }
    }

    /// <summary>Reads the content blocks of a prompt, in order, leaving out those <see cref="Read"/> does not know.</summary>
    /// <param name="blocks">The blocks.</param>
    /// <returns>The parts.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="blocks"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<AcpContentPart> ReadAll(IEnumerable<ContentBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var parts = new List<AcpContentPart>();
        foreach (var block in blocks)
        {
            if (Read(block) is { } part)
            {
                parts.Add(part);
            }
        }

        return parts;
    }

    /// <summary>A text.</summary>
    /// <param name="Value">The text.</param>
    public sealed record Text(string Value) : AcpContentPart;

    /// <summary>An image.</summary>
    /// <param name="Base64Data">The image, in base64.</param>
    /// <param name="MimeType">Its media type, such as <c>image/png</c>.</param>
    /// <param name="Uri">Where it comes from, or <see langword="null"/>.</param>
    public sealed record Image(string Base64Data, string MimeType, string? Uri) : AcpContentPart;

    /// <summary>An audio clip.</summary>
    /// <param name="Base64Data">The audio, in base64.</param>
    /// <param name="MimeType">Its media type, such as <c>audio/wav</c>.</param>
    public sealed record Audio(string Base64Data, string MimeType) : AcpContentPart;

    /// <summary>The link to a resource, such as a file, without its content.</summary>
    /// <param name="Uri">The URI of the resource.</param>
    /// <param name="Name">Its name.</param>
    /// <param name="MimeType">Its media type, or <see langword="null"/>.</param>
    /// <param name="Title">Its title, or <see langword="null"/>.</param>
    public sealed record ResourceLink(string Uri, string Name, string? MimeType, string? Title) : AcpContentPart;

    /// <summary>A resource with its content, such as a file the user attached: a text or a blob in base64.</summary>
    /// <param name="Uri">The URI of the resource.</param>
    /// <param name="MimeType">Its media type, or <see langword="null"/>.</param>
    /// <param name="TextContents">Its text, or <see langword="null"/> for a binary resource.</param>
    /// <param name="Base64Blob">Its content in base64, or <see langword="null"/> for a text resource.</param>
    public sealed record Resource(string Uri, string? MimeType, string? TextContents, string? Base64Blob) : AcpContentPart;
}
