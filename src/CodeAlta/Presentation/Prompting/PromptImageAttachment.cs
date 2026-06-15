using System.Security.Cryptography;
using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Presentation.Prompting;

internal sealed record PromptImageAttachment(
    string Id,
    string Title,
    byte[] Bytes,
    string MediaType,
    string FileExtension)
{
    public static PromptImageAttachment Create(
        string title,
        ReadOnlySpan<byte> bytes,
        string mediaType,
        string fileExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileExtension);
        if (bytes.IsEmpty)
        {
            throw new ArgumentException("Image bytes must not be empty.", nameof(bytes));
        }

        return new PromptImageAttachment(
            Guid.CreateVersion7().ToString("N"),
            NormalizeTitle(title),
            bytes.ToArray(),
            mediaType.Trim(),
            NormalizeFileExtension(fileExtension));
    }

    public PromptImageAttachment Copy()
        => this with { Bytes = [.. Bytes] };

    public PromptImageAttachment WithTitle(string title)
        => this with { Title = NormalizeTitle(title) };

    public static string NormalizeTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var normalized = string.Join(" ", title.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length == 0 ? SR.T("Image") : normalized;
    }

    public static string NormalizeFileExtension(string fileExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileExtension);
        var trimmed = fileExtension.Trim();
        return trimmed[0] == '.' ? trimmed : "." + trimmed;
    }
}

internal sealed record PromptImageAttachmentReference(
    string Title,
    string Path,
    string MediaType)
{
    public AgentInputItem.LocalImage ToAgentInputItem()
        => new(Path, DisplayName: Title, MediaType: MediaType);
}

internal sealed record PromptSubmission(string Text, IReadOnlyList<PromptImageAttachment> Images, string? AskId = null)
{
    public static PromptSubmission Create(string? text, IReadOnlyList<PromptImageAttachment>? images = null, string? askId = null)
    {
        var normalizedText = text?.Trim() ?? string.Empty;
        var clonedImages = images is { Count: > 0 }
            ? images.Select(static image => image.Copy()).ToArray()
            : [];
        return new PromptSubmission(normalizedText, clonedImages, string.IsNullOrWhiteSpace(askId) ? null : askId.Trim());
    }

    public static PromptSubmission TextOnly(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return Create(text, []);
    }

    public PromptSubmission WithAskId(string askId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(askId);
        return this with { AskId = askId.Trim() };
    }

    public bool HasContent => !string.IsNullOrWhiteSpace(Text) || Images.Count > 0;

    public PromptSubmission Copy()
        => Create(Text, Images, AskId);

    public IReadOnlyList<PromptImageAttachmentReference> ToUnsavedReferences()
        => Images.Select(static image => new PromptImageAttachmentReference(image.Title, $"memory:{image.Id}", image.MediaType)).ToArray();

    public string CreateFallbackTitle()
    {
        if (!string.IsNullOrWhiteSpace(Text))
        {
            return Text;
        }

        return Images.Count switch
        {
            0 => SR.T("Prompt"),
            1 => Images[0].Title,
            _ => SR.T("{0} images", Images.Count),
        };
    }

    public AgentInput AppendImageItems(AgentInput input, IReadOnlyList<PromptImageAttachmentReference> references)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(references);
        if (references.Count == 0)
        {
            return input;
        }

        var items = new List<AgentInputItem>(input.Items.Count + references.Count);
        items.AddRange(input.Items);
        foreach (var reference in references)
        {
            items.Add(reference.ToAgentInputItem());
        }

        return new AgentInput(items);
    }

    public string GetStableContentHash()
    {
        using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8(incrementalHash, Text);
        foreach (var image in Images)
        {
            AppendUtf8(incrementalHash, image.Title);
            AppendUtf8(incrementalHash, image.MediaType);
            incrementalHash.AppendData(image.Bytes);
        }

        return Convert.ToHexString(incrementalHash.GetHashAndReset());
    }

    private static void AppendUtf8(IncrementalHash hash, string value)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(value);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, byteCount);
        hash.AppendData(lengthBytes);
        if (byteCount == 0)
        {
            return;
        }

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = System.Text.Encoding.UTF8.GetBytes(value, buffer);
            hash.AppendData(buffer.AsSpan(0, written));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
