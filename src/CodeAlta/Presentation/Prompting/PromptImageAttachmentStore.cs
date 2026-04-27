using System.Globalization;
using System.Text;
using CodeAlta.Catalog;

namespace CodeAlta.Presentation.Prompting;

internal sealed class PromptImageAttachmentStore
{
    private readonly CatalogOptions _catalogOptions;

    public PromptImageAttachmentStore(CatalogOptions catalogOptions)
    {
        ArgumentNullException.ThrowIfNull(catalogOptions);
        _catalogOptions = catalogOptions;
    }

    public async Task<IReadOnlyList<PromptImageAttachmentReference>> SaveAsync(
        WorkThreadDescriptor thread,
        IReadOnlyList<PromptImageAttachment> images,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0)
        {
            return [];
        }

        var directory = GetAttachmentDirectory(thread);
        Directory.CreateDirectory(directory);

        var references = new List<PromptImageAttachmentReference>(images.Count);
        for (var index = 0; index < images.Count; index++)
        {
            var image = images[index];
            var fileName = CreateAttachmentFileName(image, index + 1);
            var path = Path.Combine(directory, fileName);
            var uniquePath = GetAvailablePath(path);
            await File.WriteAllBytesAsync(uniquePath, image.Bytes, cancellationToken);
            references.Add(new PromptImageAttachmentReference(image.Title, uniquePath, image.MediaType));
        }

        return references;
    }

    internal string GetAttachmentDirectory(WorkThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        var createdAt = thread.CreatedAt == default ? DateTimeOffset.UtcNow : thread.CreatedAt;
        var sessionSegment = !string.IsNullOrWhiteSpace(thread.BackendSessionId)
            ? thread.BackendSessionId
            : !string.IsNullOrWhiteSpace(thread.ThreadId)
                ? thread.ThreadId
                : Guid.CreateVersion7().ToString("N");

        return Path.Combine(
            _catalogOptions.SessionsRoot,
            createdAt.UtcDateTime.ToString("yyyy", CultureInfo.InvariantCulture),
            createdAt.UtcDateTime.ToString("MM", CultureInfo.InvariantCulture),
            createdAt.UtcDateTime.ToString("dd", CultureInfo.InvariantCulture),
            SanitizeFileName(sessionSegment) + ".attachments");
    }

    private static string CreateAttachmentFileName(PromptImageAttachment image, int index)
    {
        var title = SanitizeFileName(image.Title);
        if (title.Length == 0)
        {
            title = "image";
        }

        if (title.Length > 48)
        {
            title = title[..48].TrimEnd('.', ' ', '_', '-');
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{index:00}-{title}-{image.Id[..Math.Min(8, image.Id.Length)]}{PromptImageAttachment.NormalizeFileExtension(image.FileExtension)}");
    }

    private static string GetAvailablePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(directory, $"{fileName}-{index}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (Path.GetInvalidFileNameChars().Contains(ch) ||
                ch == Path.DirectorySeparatorChar ||
                ch == Path.AltDirectorySeparatorChar ||
                char.IsControl(ch))
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString().Trim('.', ' ', '_');
    }
}
