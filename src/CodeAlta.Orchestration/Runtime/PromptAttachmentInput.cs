using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Turns the attachments of a prompt into input the session owns, by the same rules as a send from the desktop
/// composer: an image is a PNG a model that reads images receives, any other file is a copy the model reads in the
/// form it can (a document as Markdown), and nothing is a path the caller chose.
/// </summary>
public static class PromptAttachmentInput
{
    /// <summary>Largest JSON file of attachments <see cref="MaterializeAsync"/> reads, in bytes: a prompt at the page's limit.</summary>
    public const long MaximumAttachmentsFileBytes = 96L * 1024 * 1024;

    /// <summary>
    /// Saves the attachments of <paramref name="path"/> beside <paramref name="session"/> and returns the input items
    /// that follow the text of the prompt.
    /// </summary>
    /// <param name="session">The session that owns the copies.</param>
    /// <param name="catalog">Where the copies are saved.</param>
    /// <param name="path">A JSON array of <c>title</c>, <c>mediaType</c> and canonical <c>base64</c>.</param>
    /// <param name="model">The model the prompt runs on, or null when it is not known.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The items, in the order of the file; empty when the file lists none.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The file is missing, too large or not the attachments of a prompt, or an attachment is invalid.</exception>
    /// <exception cref="NotSupportedException">An image is sent to a model that does not read images, or whose image support is not known.</exception>
    /// <exception cref="IOException">A copy could not be saved.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem denies the copies.</exception>
    /// <exception cref="OperationCanceledException">The save was canceled.</exception>
    public static Task<IReadOnlyList<AgentInputItem>> MaterializeAsync(
        SessionViewDescriptor session, CatalogOptions catalog, string path, AgentModelInfo? model, CancellationToken cancellationToken)
        => MaterializeAsync(session, catalog, path, model, documents: null, cancellationToken);

    /// <summary>The same as <see cref="MaterializeAsync(SessionViewDescriptor, CatalogOptions, string, AgentModelInfo?, CancellationToken)"/> with the converter of a test.</summary>
    internal static async Task<IReadOnlyList<AgentInputItem>> MaterializeAsync(
        SessionViewDescriptor session, CatalogOptions catalog, string path, AgentModelInfo? model, AttachedDocumentConverter? documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var frozen = OwnedPromptImages.Freeze(await ReadAsync(path, cancellationToken).ConfigureAwait(false));
        if (frozen.Count == 0) return [];
        if (frozen.Any(OwnedPromptImages.IsImage) && AgentImageInputCapability.Read(model) != true)
            throw new NotSupportedException("This model does not read images.");

        documents ??= new AttachedDocumentConverter();
        return await SaveAsync(session, catalog, frozen, Guid.NewGuid().ToString("N"), model, documents, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves attachments the caller already checked and returns the input items, in their order. A send of the window and
    /// <see cref="MaterializeAsync(SessionViewDescriptor, CatalogOptions, string, AgentModelInfo?, CancellationToken)"/> share this.
    /// </summary>
    internal static async Task<IReadOnlyList<AgentInputItem>> SaveAsync(
        SessionViewDescriptor session, CatalogOptions catalog, IReadOnlyList<OwnedPromptImage> attachments, string batchId,
        AgentModelInfo? model, AttachedDocumentConverter documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentNullException.ThrowIfNull(documents);
        cancellationToken.ThrowIfCancellationRequested();
        if (attachments.Count == 0) return [];
        var saved = await new PromptImageAttachmentStore(catalog).SaveAsync(session,
            attachments.Select((attachment, index) => CreateAttachment(batchId + index, attachment)).ToArray(), cancellationToken).ConfigureAwait(false);
        var items = new List<AgentInputItem>(saved.Count);
        for (var index = 0; index < saved.Count; index++)
        {
            var attachment = saved[index];
            if (OwnedPromptImages.IsImage(attachments[index]))
            {
                items.Add(new AgentInputItem.LocalImage(attachment.Path, attachment.Title, attachment.MediaType));
                continue;
            }

            if (NeedsConversion(attachment.MediaType, model))
                await documents.ConvertAsync(attachment.Path, cancellationToken).ConfigureAwait(false);
            items.Add(new AgentInputItem.LocalFile(attachment.Path, attachment.Title, attachment.MediaType));
        }

        return items;
    }

    /// <summary>
    /// Whether an attached file is converted to Markdown when it is sent: an office document for every model, a PDF for a
    /// model that does not declare PDF input.
    /// </summary>
    internal static bool NeedsConversion(string mediaType, AgentModelInfo? model)
        => AgentAttachedFiles.IsDocument(mediaType) || mediaType == AgentPdfInputs.MediaType && !AgentPdfInputs.AcceptsPdf(model);

    private static PromptImageAttachment CreateAttachment(string id, OwnedPromptImage attachment)
    {
        if (OwnedPromptImages.IsImage(attachment))
            return new PromptImageAttachment(id, attachment.Title, OwnedPromptImages.Decode(attachment), attachment.MediaType, ".png");
        var bytes = OwnedPromptImages.DecodeFile(attachment);
        var mediaType = AgentAttachedFiles.FindMediaType(bytes, attachment.Title);
        var extension = (AgentAttachedFiles.Extension(mediaType) ?? Path.GetExtension(attachment.Title)).TrimStart('.');
        if (extension.Length is 0 or > 16 || extension.Any(static c => !char.IsAsciiLetterOrDigit(c))) extension = "bin";
        return new PromptImageAttachment(id, attachment.Title, bytes, mediaType, "." + extension);
    }

    private static async Task<IReadOnlyList<OwnedPromptImage>> ReadAsync(string path, CancellationToken cancellationToken)
    {
        FileInfo info;
        try { info = new FileInfo(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ArgumentException("The attachments were not found.", exception);
        }

        if (!info.Exists || info.Length == 0 || info.Length > MaximumAttachmentsFileBytes)
            throw new ArgumentException("The attachments were not found.");
        JsonNode? node;
        try { node = JsonNode.Parse(await File.ReadAllBytesAsync(info.FullName, cancellationToken).ConfigureAwait(false)); }
        catch (JsonException exception) { throw new ArgumentException("The attachments could not be read.", exception); }
        if (node is not JsonArray rows) throw new ArgumentException("The attachments could not be read.");
        var images = new List<OwnedPromptImage>(rows.Count);
        foreach (var row in rows)
        {
            if (row is not JsonObject attachment
                || attachment["title"] is not JsonValue titleValue || !titleValue.TryGetValue<string>(out var title)
                || attachment["mediaType"] is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var mediaType)
                || attachment["base64"] is not JsonValue dataValue || !dataValue.TryGetValue<string>(out var data))
                throw new ArgumentException("The attachments could not be read.");
            images.Add(new OwnedPromptImage(title, mediaType, data));
        }

        return images;
    }
}
