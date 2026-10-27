using System.Globalization;
using System.Security;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile;

/// <summary>An image the phone shows: its bytes and the type read from its content.</summary>
/// <param name="Bytes">The content of the image.</param>
/// <param name="MediaType">PNG, JPEG, GIF, WebP or BMP.</param>
internal sealed record MobileImage(byte[] Bytes, string MediaType);

/// <summary>
/// The images of the timeline of a session, which the phone asks for by an id and never by a path. A file of a prompt or
/// an image a tool gave the model is a saved copy of the session, read with <c>alta session image</c>, which looks for it
/// among the files of that session only. A picture of the disk that an answer shows (<c>![alt](path)</c>) is found again in
/// that answer, by its sequence number and its position, and its path is resolved as the desktop resolves it: from the
/// folder the session works in for a relative one.
/// </summary>
internal sealed class MobileImages(IPluginAltaService alta, MobileSessions sessions)
{
    /// <summary>The largest image served, as the desktop serves them.</summary>
    internal const int MaximumBytes = 8 * 1024 * 1024;

    // The base64 of the largest image, and its record.
    private static readonly PluginAltaInvocationOptions ImageRead = new() { MaxOutputBytes = MaximumBytes / 3 * 4 + 64 * 1024 };

    /// <summary>A saved copy of a session (a file of a prompt, an image of a tool), or null when the session has none of that id.</summary>
    internal async Task<MobileImage?> SavedAsync(string sessionId, string imageId, CancellationToken cancellationToken)
    {
        if (!MobileTimeline.IsSavedCopy(imageId) || await sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false) is null) return null;
        var result = await alta.InvokeAsync(["session", "image", sessionId, imageId], options: ImageRead, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.Truncated) return null;
        var record = MobileAlta.Parse(result.TranscriptJsonl).FirstOrDefault(static item => (string?)item["type"] == "alta.session.image");
        if (MobileAlta.Text(record, "base64") is not { } base64) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }
        // The content decides, whatever alta says of it.
        return bytes.Length <= MaximumBytes && MediaType(bytes) is { } mediaType ? new(bytes, mediaType) : null;
    }

    /// <summary>A picture of the disk that an answer of a session shows, by the sequence number of the answer and its position in it.</summary>
    internal async Task<MobileImage?> LinkedAsync(string sessionId, long sequence, int index, CancellationToken cancellationToken)
    {
        if (sequence < 1 || index is < 0 or >= MobileTimeline.MaximumLinkedImages) return null;
        if (await sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false) is not { } status) return null;
        var read = await MobileAlta.RunAsync(alta, ["session", "events", sessionId, "--since", (sequence - 1).ToString(CultureInfo.InvariantCulture), "--limit", "1",
            "--kind", "assistant.message", "--fields", "sequenceNumber,content"], null, cancellationToken).ConfigureAwait(false);
        var answer = read.OfType("alta.session.event").FirstOrDefault(record => record["sequenceNumber"] is { } number && (long)number == sequence);
        if (answer is null) return null;
        var text = string.Concat((answer["content"] as System.Text.Json.Nodes.JsonArray ?? []).Select(static part => (string?)part?["text"] ?? ""));
        var images = MobileTimeline.LinkedImages(text);
        if (index >= images.Count) return null;
        var folder = MobileAlta.Text(status, "worktreeDirectory") ?? MobileAlta.Text(status, "workingDirectory");
        return Locate(images[index].Target, folder) is { } path ? await ReadFileAsync(path, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>The full path a link names: a <c>file:</c> address, an absolute path, or a path relative to the folder of the session.</summary>
    internal static string? Locate(string target, string? folder)
    {
        try
        {
            if (target.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                return Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;
            if (target.StartsWith('~')) return null;
            if (Path.IsPathFullyQualified(target)) return Path.GetFullPath(target);
            return folder is not null && Path.IsPathFullyQualified(folder) ? Path.GetFullPath(Path.Combine(folder, target)) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Reads a picture of the disk: named as a picture, at most <see cref="MaximumBytes"/>, and a picture by its content.</summary>
    internal static async Task<MobileImage?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp")) return null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            if (stream.Length > MaximumBytes) return null;
            // One byte more than the length shows a file that grew past the maximum while it was read.
            var buffer = new byte[(int)stream.Length + 1];
            var count = 0;
            int read;
            while (count < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false)) > 0) count += read;
            if (count > MaximumBytes) return null;
            var bytes = buffer.AsSpan(0, count).ToArray();
            return MediaType(bytes) is { } mediaType ? new(bytes, mediaType) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The type of an image by its first bytes: PNG, JPEG, GIF, WebP or BMP; null for anything else.</summary>
    internal static string? MediaType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])) return "image/png";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xff, 0xd8, 0xff])) return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        if (bytes.Length >= 26 && bytes.StartsWith("BM"u8)) return "image/bmp";
        return null;
    }
}
