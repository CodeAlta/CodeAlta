using System.Globalization;
using System.Security;
using CodeAlta.CodeMode;

namespace CodeAlta.Orchestration.Runtime.Scripts;

/// <summary>A picture a script attached to its result, read from its file.</summary>
/// <param name="Path">The full path of the file.</param>
/// <param name="Title">Its title: the one the script gave, or the name of the file.</param>
/// <param name="MediaType">The media type found in the header of the file: PNG, JPEG, GIF or WebP.</param>
/// <param name="Bytes">The content of the file.</param>
public sealed record CodeModeResultImage(string Path, string Title, string MediaType, byte[] Bytes);

/// <summary>
/// The pictures a script attached to its result (<c>image(path, { title })</c>), read when the run ended: those that could
/// be read, and the lines the result says about them (how many were attached, and why each other one was skipped).
/// </summary>
/// <param name="Images">The pictures that could be read, in their order.</param>
/// <param name="Lines">The lines for the text of the result.</param>
public sealed record CodeModeResultImages(IReadOnlyList<CodeModeResultImage> Images, IReadOnlyList<string> Lines)
{
    /// <summary>Gets a result without pictures.</summary>
    public static CodeModeResultImages None { get; } = new([], []);

    /// <summary>Reads the pictures of a run from their files. A file that is missing, too large or not a picture is skipped with a line.</summary>
    /// <param name="images">The pictures the script attached.</param>
    /// <returns>The pictures and the lines.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="images"/> is null.</exception>
    public static CodeModeResultImages Read(IReadOnlyList<CodeModeImage> images)
        => ReadAsync(images, null).GetAwaiter().GetResult();

    /// <summary>
    /// Reads the pictures of a run. A picture that is missing, too large or not a picture is skipped with a line, and so
    /// is one its session may not read.
    /// </summary>
    /// <param name="images">The pictures the script attached.</param>
    /// <param name="open">
    /// Gives the content of a picture as the session of the run reads it (its sandbox, the tools it has), or why it does
    /// not; null reads the file itself, for a run that has no session.
    /// </param>
    /// <returns>The pictures and the lines.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="images"/> is null.</exception>
    public static async Task<CodeModeResultImages> ReadAsync(IReadOnlyList<CodeModeImage> images, Func<CodeModeImage, Task<(byte[]? Bytes, string? Why)>>? open)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0)
        {
            return None;
        }

        var read = new List<CodeModeResultImage>(images.Count);
        var skipped = new List<string>();
        foreach (var image in images)
        {
            var (bytes, refusal) = open is null ? ReadFile(image) : await open(image).ConfigureAwait(false);
            if (bytes is null)
            {
                skipped.Add($"image skipped: {image.Path}: {refusal}");
            }
            else if (TryAccept(image, bytes, out var picture, out var why))
            {
                read.Add(picture);
            }
            else
            {
                skipped.Add($"image skipped: {image.Path}: {why}");
            }
        }

        var lines = new List<string>(skipped.Count + 1);
        if (read.Count > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Images: {read.Count} attached ({string.Join(", ", read.Select(static picture => picture.Title))})"));
        }

        lines.AddRange(skipped);
        return new(read, lines);
    }

    // The content of the file of a picture, or why it is not read.
    private static (byte[]? Bytes, string? Why) ReadFile(CodeModeImage image)
    {
        if (!Path.IsPathFullyQualified(image.Path))
        {
            return (null, "not a full path.");
        }

        try
        {
            var file = new FileInfo(image.Path);
            if (!file.Exists)
            {
                return (null, "no such file.");
            }

            return file.Length > PromptImageHistory.MaximumImageBytes
                ? (null, $"larger than {PromptImageHistory.MaximumImageBytes / (1024 * 1024)} MB.")
                : (File.ReadAllBytes(file.FullName), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            return (null, $"cannot be read: {ex.Message}");
        }
    }

    private static bool TryAccept(CodeModeImage image, byte[] bytes, out CodeModeResultImage picture, out string why)
    {
        picture = null!;
        if (bytes.Length > PromptImageHistory.MaximumImageBytes)
        {
            why = $"larger than {PromptImageHistory.MaximumImageBytes / (1024 * 1024)} MB.";
            return false;
        }

        // What every provider reads: a bitmap is not sent.
        if (PromptImageHistory.FindMediaType(bytes) is not { } mediaType || mediaType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp"))
        {
            why = "not a PNG, JPEG, GIF or WebP picture.";
            return false;
        }

        picture = new(image.Path, image.Title ?? Path.GetFileName(image.Path), mediaType, bytes);
        why = string.Empty;
        return true;
    }
}
