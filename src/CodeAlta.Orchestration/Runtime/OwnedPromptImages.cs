using System.Buffers.Binary;
using System.IO.Compression;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// An attachment of a prompt: an encoded PNG image (<c>image/png</c>) or a file of any other type; never a renderer
/// filesystem path or remote URL.
/// </summary>
/// <param name="Title">Bounded plain display title; the name of the file for a file.</param>
/// <param name="MediaType">Exactly image/png for an image; the media type the page saw for a file.</param>
/// <param name="Base64">Canonical base64 of the exact original bytes.</param>
public sealed record OwnedPromptImage(string Title, string MediaType, string Base64);

internal static class OwnedPromptImages
{
    /// <summary>The media type of an image attachment; any other is a file.</summary>
    internal const string ImageMediaType = "image/png";

    /// <summary>Most attachments of one prompt.</summary>
    internal const int MaximumAttachments = 64;

    /// <summary>Largest total of the files of one prompt, in bytes, as the page bounds it.</summary>
    internal const long MaximumTotalFileBytes = 64L * 1024 * 1024;

    internal static bool IsImage(OwnedPromptImage attachment) => attachment.MediaType == ImageMediaType;

    /// <exception cref="ArgumentException">An attachment is invalid, there are more than <see cref="MaximumAttachments"/>,
    /// or the files are larger than <see cref="MaximumTotalFileBytes"/> in all.</exception>
    internal static IReadOnlyList<OwnedPromptImage> Freeze(IReadOnlyList<OwnedPromptImage>? images)
    {
        if (images is null || images.Count == 0) return [];
        if (images.Count > MaximumAttachments) throw new ArgumentException($"A prompt has at most {MaximumAttachments} attachments.", nameof(images));
        // Checked from the length of the base64, before any file is decoded.
        if (images.Where(static image => image is not null && !IsImage(image)).Sum(static image => (long)(image.Base64?.Length ?? 0) / 4 * 3) > MaximumTotalFileBytes + 2 * MaximumAttachments)
            throw new ArgumentException($"The files of a prompt are at most {MaximumTotalFileBytes / (1024 * 1024)} MB in all.", nameof(images));
        var copy = images.ToArray();
        foreach (var image in copy)
        {
            if (image is not null && !IsImage(image)) DecodeFile(image);
            else Decode(image!);
        }
        return Array.AsReadOnly(copy);
    }

    /// <summary>Checks a file attachment and returns its bytes.</summary>
    /// <exception cref="ArgumentException">The title is not a plain file name, the media type is malformed, the base64 is not
    /// canonical, or the file is empty or larger than <see cref="AgentAttachedFiles.MaximumBytes"/>.</exception>
    internal static byte[] DecodeFile(OwnedPromptImage file)
    {
        static ArgumentException Invalid() => new($"Invalid file attachment: a named file of 1 byte to {AgentAttachedFiles.MaximumBytes / (1024 * 1024)} MB is required.");
        if (file is null || string.IsNullOrWhiteSpace(file.Title) || file.Title.Length > 80 || file.Title.Any(static c => char.IsControl(c) || c is '/' or '\\')
            || string.IsNullOrWhiteSpace(file.MediaType) || file.MediaType.Length > 128 || file.MediaType.Count(static c => c == '/') != 1
            || file.MediaType.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c) || c == ';') || file.Base64 is null
            || file.Base64.Length == 0 || file.Base64.Length > (AgentAttachedFiles.MaximumBytes + 2) / 3 * 4) throw Invalid();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(file.Base64); }
        catch (FormatException) { throw Invalid(); }
        if (bytes.Length == 0 || bytes.Length > AgentAttachedFiles.MaximumBytes || Convert.ToBase64String(bytes) != file.Base64) throw Invalid();
        return bytes;
    }

    internal static byte[] Decode(OwnedPromptImage image)
    {
        static ArgumentException Invalid() => new("Invalid PNG attachment: RGB/RGBA 8-bit non-interlaced PNG required.");
        if (image is null || string.IsNullOrWhiteSpace(image.Title) || image.Title.Length > 80 || image.Title.Any(char.IsControl)
            || image.MediaType != "image/png" || image.Base64 is null) throw Invalid();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(image.Base64); }
        catch (FormatException) { throw Invalid(); }
        if (bytes.Length < 57 || Convert.ToBase64String(bytes) != image.Base64
            || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw Invalid();
        using var compressed = new MemoryStream();
        var offset = 8; var width = 0; var height = 0; var channels = 0; var ended = false; var dataEnded = false;
        while (offset <= bytes.Length - 12)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
            if (size > bytes.Length - offset - 12) throw Invalid();
            var length = (int)size;
            var type = bytes.AsSpan(offset + 4, 4);
            var data = bytes.AsSpan(offset + 8, length);
            var crc = uint.MaxValue;
            foreach (var b in bytes.AsSpan(offset + 4, length + 4))
            {
                crc ^= b;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + length))) throw Invalid();
            if (type.SequenceEqual("IHDR"u8))
            {
                if (offset != 8 || length != 13) throw Invalid();
                width = BinaryPrimitives.ReadInt32BigEndian(data); height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                if (width < 1 || height < 1
                    || data[8] != 8 || data[9] is not (2 or 6) || data[10] != 0 || data[11] != 0 || data[12] != 0) throw Invalid();
                channels = data[9] == 2 ? 3 : 4;
            }
            else if (width == 0) throw Invalid();
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (dataEnded) throw Invalid();
                compressed.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0 || compressed.Length == 0 || offset + 12 != bytes.Length) throw Invalid();
                ended = true; break;
            }
            else
            {
                // No compressed metadata/profile bombs, animation, or unknown chunks.
                if (!((type.SequenceEqual("sRGB"u8) && length == 1) || (type.SequenceEqual("gAMA"u8) && length == 4)
                    || (type.SequenceEqual("cHRM"u8) && length == 32) || (type.SequenceEqual("pHYs"u8) && length == 9))) throw Invalid();
                if (compressed.Length != 0) dataEnded = true;
            }
            offset += length + 12;
        }
        if (!ended) throw Invalid();
        compressed.Position = 0;
        try
        {
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            // Validate scanlines with fixed scratch space, not a width-sized allocation.
            var buffer = new byte[64 * 1024];
            for (var y = 0; y < height; y++)
            {
                var filter = zlib.ReadByte();
                if (filter is < 0 or > 4) throw Invalid();
                for (long remaining = (long)width * channels; remaining > 0;)
                {
                    var count = (int)Math.Min(remaining, buffer.Length);
                    zlib.ReadExactly(buffer.AsSpan(0, count));
                    remaining -= count;
                }
            }
            if (zlib.ReadByte() != -1) throw Invalid();
        }
        catch (IOException) { throw Invalid(); }
        return bytes;
    }
}
