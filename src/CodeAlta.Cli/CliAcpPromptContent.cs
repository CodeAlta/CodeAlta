using System.Text;
using CodeAlta.Acp;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Cli;

/// <summary>
/// The prompt of a <c>session/prompt</c> of <c>altacli acp</c>, read from its content blocks, in order:
/// <list type="bullet">
/// <item>a text is the text of the prompt, the texts joined as they come;</item>
/// <item>an image (base64) is saved beside the journal of the session, as the images pasted in CodeAlta Desktop are, and
/// attached by its path;</item>
/// <item>an embedded resource with a text is written in the prompt in a <c>&lt;resource uri="..."&gt;</c> block; one with a
/// blob is saved as a file and attached;</item>
/// <item>a link to a resource is written in the prompt as a Markdown link, and attached by its path when it names a local
/// file or folder (<c>file://</c>) that exists.</item>
/// </list>
/// Audio is not taken: <c>initialize</c> does not advertise it.
/// </summary>
internal static class CliAcpPromptContent
{
    /// <summary>The text of a prompt that has attachments and no text.</summary>
    public const string AttachmentsOnlyText = "See the attachments.";

    /// <summary>Reads the prompt of a session.</summary>
    /// <param name="blocks">The content blocks of the request.</param>
    /// <param name="session">The session, beside whose journal images and files are saved.</param>
    /// <param name="catalog">The roots of the host.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The prompt, or null with what is wrong with the content.</returns>
    /// <exception cref="IOException">An image or a file could not be saved.</exception>
    /// <exception cref="UnauthorizedAccessException">An image or a file could not be saved.</exception>
    public static async Task<(HeadlessPrompt? Prompt, string? Error)> ReadAsync(IReadOnlyList<ContentBlock> blocks, SessionViewDescriptor session,
        CatalogOptions catalog, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(catalog);
        var text = new StringBuilder();
        var attachments = new List<SessionPromptAttachment>();
        // The images and the files to save, and the place of each among the attachments.
        var saved = new List<(int Index, PromptImageAttachment Payload, bool Image)>();
        foreach (var part in AcpContentPart.ReadAll(blocks))
        {
            switch (part)
            {
                case AcpContentPart.Text { Value: var value }:
                    text.Append(value);
                    break;
                case AcpContentPart.Image image:
                    if (!TryDecode(image.Base64Data, out var pixels)) return (null, "An image of the prompt is not valid base64.");
                    saved.Add((attachments.Count, new PromptImageAttachment(NewId(), TitleOf(image.Uri, "image"), pixels, image.MimeType,
                        ExtensionOf(image.MimeType, image.Uri)), true));
                    attachments.Add(null!);
                    break;
                case AcpContentPart.Resource { TextContents: { } contents } resource:
                    AppendBlock(text, resource.Uri, contents);
                    break;
                case AcpContentPart.Resource { Base64Blob: { } blob } resource:
                    if (!TryDecode(blob, out var bytes)) return (null, $"The resource '{resource.Uri}' of the prompt is not valid base64.");
                    var mediaType = resource.MimeType ?? "application/octet-stream";
                    saved.Add((attachments.Count, new PromptImageAttachment(NewId(), TitleOf(resource.Uri, "file"), bytes, mediaType,
                        ExtensionOf(mediaType, resource.Uri)), mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)));
                    attachments.Add(null!);
                    break;
                case AcpContentPart.ResourceLink link:
                    text.Append('[').Append(link.Title ?? link.Name).Append("](").Append(link.Uri).Append(')');
                    if (LocalPath(link.Uri) is { } path)
                    {
                        attachments.Add(new SessionPromptAttachment
                        {
                            AttachmentId = "link-" + attachments.Count,
                            Path = path,
                            DisplayName = link.Name,
                            ContentType = link.MimeType,
                        });
                    }

                    break;
                case AcpContentPart.Audio:
                    return (null, "Audio is not supported: the agent does not advertise it.");
            }
        }

        if (saved.Count > 0)
        {
            var references = await new PromptImageAttachmentStore(catalog).SaveAsync(session, [.. saved.Select(static item => item.Payload)], cancellationToken)
                .ConfigureAwait(false);
            for (var index = 0; index < saved.Count; index++)
            {
                attachments[saved[index].Index] = new SessionPromptAttachment
                {
                    AttachmentId = "content-" + saved[index].Index,
                    Kind = saved[index].Image ? SessionPromptAttachmentKind.Image : SessionPromptAttachmentKind.File,
                    Path = references[index].Path,
                    DisplayName = references[index].Title,
                    ContentType = references[index].MediaType,
                };
            }
        }

        var prompt = text.ToString();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            if (attachments.Count == 0) return (null, "The prompt is empty.");
            prompt = AttachmentsOnlyText;
        }

        return (new HeadlessPrompt(prompt) { Attachments = attachments }, null);
    }

    /// <summary>The local path of a <c>file://</c> URI that names a file or a folder that exists; null otherwise.</summary>
    internal static string? LocalPath(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return null;
        var path = parsed.LocalPath;
        return Path.IsPathFullyQualified(path) && (File.Exists(path) || Directory.Exists(path)) ? path : null;
    }

    private static void AppendBlock(StringBuilder text, string uri, string contents)
    {
        if (text.Length > 0 && text[^1] != '\n') text.Append('\n');
        text.Append("\n<resource uri=\"").Append(uri.Replace("\"", "%22", StringComparison.Ordinal)).Append("\">\n").Append(contents);
        if (!contents.EndsWith('\n')) text.Append('\n');
        text.Append("</resource>\n");
    }

    private static bool TryDecode(string base64, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(base64);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    // The name of the file of a URI, else a name of its kind.
    private static string TitleOf(string? uri, string fallback)
    {
        if (uri is null) return fallback;
        var name = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? Path.GetFileName(parsed.IsFile ? parsed.LocalPath : parsed.AbsolutePath) : Path.GetFileName(uri);
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    // The extension a saved copy is given: the one of its media type, else of its name, else bin.
    private static string ExtensionOf(string mediaType, string? uri)
    {
        var extension = mediaType.ToLowerInvariant() switch
        {
            "image/png" => "png",
            "image/jpeg" or "image/jpg" => "jpg",
            "image/gif" => "gif",
            "image/webp" => "webp",
            "image/bmp" => "bmp",
            "image/svg+xml" => "svg",
            "application/pdf" => "pdf",
            "text/plain" => "txt",
            "text/markdown" => "md",
            "application/json" => "json",
            _ => Path.GetExtension(TitleOf(uri, string.Empty)).TrimStart('.'),
        };
        return extension.Length is > 0 and <= 16 && extension.All(char.IsAsciiLetterOrDigit) ? extension : "bin";
    }
}
