using System.Security;
using System.Text;
using System.Text.Json;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>Outcome of reading one image of a persisted user message.</summary>
public enum PromptImageReadStatus
{
    /// <summary>The image was read.</summary>
    Ok,

    /// <summary>No session has the asked identity.</summary>
    MissingSession,

    /// <summary>The offset is not the start of a persisted user message.</summary>
    MissingRecord,

    /// <summary>The message has no image at the asked index.</summary>
    MissingImage,

    /// <summary>The image file no longer exists.</summary>
    MissingFile,

    /// <summary>The recorded path is not a file of the session's prompt-image folder.</summary>
    OutsideStore,

    /// <summary>The file is not a PNG, JPEG, GIF, WebP or BMP image.</summary>
    UnsupportedType,

    /// <summary>The file is larger than the served maximum.</summary>
    TooLarge,

    /// <summary>The journal or the file could not be read.</summary>
    ReadFailed,
}

/// <summary>One image of a persisted user message, or the reason it is not served.</summary>
/// <param name="Status">The outcome.</param>
/// <param name="MediaType">The media type found in the file's own header; null unless read.</param>
/// <param name="Bytes">The file content; null unless read.</param>
public sealed record PromptImageReadResult(PromptImageReadStatus Status, string? MediaType = null, byte[]? Bytes = null);

/// <summary>
/// Reads the images a persisted user message refers to. The message records the path of each image file; a
/// frontend gets the images by index and never the paths.
/// </summary>
public static class PromptImageHistory
{
    /// <summary>Largest image file served, in bytes.</summary>
    public const int MaximumImageBytes = 8 * 1024 * 1024;

    /// <summary>Lists the images recorded in the details of a user message, in the order their indexes name them.</summary>
    /// <param name="details">The details of a completed user content event.</param>
    /// <returns>
    /// The <c>localImage</c> input items, then the entries of a legacy <c>attachments</c> list. A missing title
    /// is the file name and a missing media type is <c>image/*</c>.
    /// </returns>
    public static IReadOnlyList<PromptImageAttachmentReference> ReadImages(JsonElement? details)
    {
        if (details is not { ValueKind: JsonValueKind.Object } root) return [];
        var images = new List<PromptImageAttachmentReference>();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
                if (IsLocalImage(item)) Add(images, Text(item, "path"), Text(item, "displayName"), Text(item, "mediaType"));
        }

        if (root.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
        {
            foreach (var attachment in attachments.EnumerateArray())
                if (attachment.ValueKind == JsonValueKind.Object)
                    Add(images, Text(attachment, "path"), Text(attachment, "title"), Text(attachment, "mediaType"));
        }

        return images;
    }

    /// <summary>Tells whether an input item of a user message's details is a local image.</summary>
    /// <param name="item">One element of the details' <c>items</c> list.</param>
    /// <returns>True for an object whose <c>$type</c> is <c>localImage</c>.</returns>
    public static bool IsLocalImage(JsonElement item)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("$type", out var type)
            && type.ValueKind == JsonValueKind.String && type.ValueEquals("localImage");

    /// <summary>Removes from the text of a user message the lines that name its image files.</summary>
    /// <param name="content">The recorded text of the message.</param>
    /// <param name="images">The images of the message, from <see cref="ReadImages"/>.</param>
    /// <returns>
    /// The text without one <c>Local image (title): path</c> or <c>Local image: path</c> line per image, taken
    /// from the end; everything else, including what the user typed, is unchanged.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static string RemoveImageLines(string content, IReadOnlyList<PromptImageAttachmentReference> images)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0 || content.Length == 0) return content;
        var lines = content.Split('\n');
        var removed = new bool[lines.Length];
        var any = false;
        for (var image = images.Count - 1; image >= 0; image--)
        {
            var titled = $"Local image ({images[image].Title}): {images[image].Path}";
            var untitled = $"Local image: {images[image].Path}";
            for (var line = lines.Length - 1; line >= 0; line--)
            {
                if (removed[line]) continue;
                var text = lines[line].AsSpan().TrimEnd('\r');
                if (!text.SequenceEqual(titled) && !text.SequenceEqual(untitled)) continue;
                removed[line] = any = true;
                break;
            }
        }

        if (!any) return content;
        var result = new StringBuilder(content.Length);
        var first = true;
        for (var line = 0; line < lines.Length; line++)
        {
            if (removed[line]) continue;
            if (!first) result.Append('\n');
            result.Append(lines[line]);
            first = false;
        }

        // The separator before a removed last line goes with it.
        if (removed[^1] && result.Length > 0 && result[^1] == '\r') result.Length--;
        return result.ToString();
    }

    /// <summary>Reads an image file when it is a file of a session's prompt-image folder.</summary>
    /// <param name="directory">The session's prompt-image folder.</param>
    /// <param name="path">The path a user message recorded.</param>
    /// <param name="maximumBytes">Largest file read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes and the media type found in the file's header, or the reason for the refusal.</returns>
    /// <remarks>
    /// The folder and every folder below it down to the file must be ordinary directories: a symbolic link or a
    /// junction is refused, like a path that leaves the folder. The check and the read are not one atomic step.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumBytes"/> is not positive.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public static async Task<PromptImageReadResult> ReadFileAsync(string directory, string path, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumBytes, 0);
        static PromptImageReadResult Refused(PromptImageReadStatus status) => new(status);
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathFullyQualified(path)) return Refused(PromptImageReadStatus.OutsideStore);
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var full = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (full.Length <= root.Length + 1 || !full.StartsWith(root, comparison) || full[root.Length] != Path.DirectorySeparatorChar)
                return Refused(PromptImageReadStatus.OutsideStore);
            var segments = full[(root.Length + 1)..].Split(Path.DirectorySeparatorChar);
            var current = root;
            for (var index = 0; index <= segments.Length; index++)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    return Refused(PromptImageReadStatus.MissingFile);
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0) return Refused(PromptImageReadStatus.OutsideStore);
                // Every segment but the last is a folder, and the last is a file.
                if (((attributes & FileAttributes.Directory) != 0) == (index == segments.Length)) return Refused(PromptImageReadStatus.MissingFile);
                if (index < segments.Length) current = Path.Combine(current, segments[index]);
            }

            await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, useAsync: true);
            if (stream.Length > maximumBytes) return Refused(PromptImageReadStatus.TooLarge);
            // One byte more than the length shows a file that grew past the maximum while it was read.
            var buffer = new byte[(int)stream.Length + 1];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }

            if (count > maximumBytes) return Refused(PromptImageReadStatus.TooLarge);
            var bytes = buffer.AsSpan(0, count).ToArray();
            return FindMediaType(bytes) is { } mediaType ? new(PromptImageReadStatus.Ok, mediaType, bytes) : Refused(PromptImageReadStatus.UnsupportedType);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Refused(PromptImageReadStatus.MissingFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Refused(PromptImageReadStatus.ReadFailed);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Refused(PromptImageReadStatus.OutsideStore); // Not a path this platform can name.
        }
    }

    /// <summary>Names the image format a file starts with.</summary>
    /// <param name="bytes">The start of the file, or all of it.</param>
    /// <returns>The media type of a PNG, JPEG, GIF, WebP or BMP image; otherwise null.</returns>
    public static string? FindMediaType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])) return "image/png";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xff, 0xd8, 0xff])) return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        // A bitmap has a 14-byte file header and at least a 12-byte information header.
        if (bytes.Length >= 26 && bytes.StartsWith("BM"u8)) return "image/bmp";
        return null;
    }

    private static void Add(List<PromptImageAttachmentReference> images, string? path, string? title, string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        images.Add(new(string.IsNullOrWhiteSpace(title) ? FileName(path) : title, path, string.IsNullOrWhiteSpace(mediaType) ? "image/*" : mediaType));
    }

    // A recorded path may come from the other platform: both separators end the folder part.
    private static string FileName(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    private static string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        try { return value.GetString(); }
        catch (InvalidOperationException) { return null; } // An escaped unpaired surrogate is not text.
    }
}
