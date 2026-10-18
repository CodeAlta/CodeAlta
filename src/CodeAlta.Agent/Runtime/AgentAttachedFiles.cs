using System.Collections.Frozen;
using System.Text;
using CodeAlta.Agent.Runtime.Images;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// The files a user attaches to a prompt (<see cref="AgentInputItem.LocalFile"/>). Each one is a copy the session owns,
/// and each request gives it to the model in the form the model reads:
/// <list type="bullet">
/// <item>a PDF as the document itself to a model that declares PDF input, otherwise as the Markdown it was converted
/// to (<see cref="AgentPdfInputs"/>);</item>
/// <item>a text file as its content;</item>
/// <item>an office document (Word, Excel, PowerPoint, EPUB, RTF) as the Markdown it was converted to;</item>
/// <item>any other file as its path, for the model to open with its tools.</item>
/// </list>
/// The conversion to Markdown is done once, when the prompt is sent, and saved beside the copy
/// (<see cref="ConvertedPath"/>); nothing is converted while a request is built.
/// </summary>
internal static class AgentAttachedFiles
{
    /// <summary>Largest file attached, in bytes: the size of the largest PDF a model is sent.</summary>
    public const long MaximumBytes = AgentPdfInputs.MaximumBytes;

    /// <summary>Most characters of a text, or of a converted document, given to the model in the prompt.</summary>
    public const int MaximumInlineCharacters = 100_000;

    /// <summary>The media type of a file whose content is text.</summary>
    public const string TextMediaType = "text/plain";

    /// <summary>The media type of a file that is neither text nor a known document.</summary>
    public const string BinaryMediaType = "application/octet-stream";

    // The documents MarkItDotNet converts that no model reads as they are, by extension and media type.
    private static readonly FrozenDictionary<string, string> DocumentMediaTypes = new Dictionary<string, string>
    {
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".epub"] = "application/epub+zip",
        [".rtf"] = "application/rtf",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>The path of the Markdown a document attached at <paramref name="path"/> was converted to.</summary>
    /// <param name="path">The path of the attached copy.</param>
    /// <returns>The path followed by <c>.md</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.</exception>
    public static string ConvertedPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path + ".md";
    }

    /// <summary>Gets whether a file is a copy attached to a prompt: a file of the attachment folder of a session.</summary>
    /// <param name="path">The path of a file.</param>
    /// <returns>True when its folder is a <c>&lt;session&gt;.attachments</c> folder.</returns>
    public static bool IsAttachedCopy(string path)
        => Path.GetFileName(Path.GetDirectoryName(path)) is { } folder && folder.EndsWith(".attachments", StringComparison.OrdinalIgnoreCase);

    /// <summary>The extension a copy of a file of this type is saved with, whatever its name says: the converter reads a document by its extension.</summary>
    /// <param name="mediaType">The media type from <see cref="FindMediaType"/>.</param>
    /// <returns><c>.pdf</c> or the extension of a known document; null for any other type.</returns>
    public static string? Extension(string mediaType)
    {
        if (string.Equals(mediaType, AgentPdfInputs.MediaType, StringComparison.OrdinalIgnoreCase)) return ".pdf";
        foreach (var (extension, type) in DocumentMediaTypes)
            if (string.Equals(type, mediaType, StringComparison.OrdinalIgnoreCase)) return extension;
        return null;
    }

    /// <summary>Gets whether an attached file is converted to Markdown for every model.</summary>
    /// <param name="mediaType">The media type from <see cref="FindMediaType"/>.</param>
    /// <returns>True for a Word, Excel, PowerPoint, EPUB or RTF document.</returns>
    public static bool IsDocument(string? mediaType)
        => mediaType is not null && DocumentMediaTypes.Values.Contains(mediaType);

    /// <summary>Names what a file attached to a prompt is, from its content and, for a document, its extension.</summary>
    /// <param name="content">The content of the file.</param>
    /// <param name="fileName">The name of the file.</param>
    /// <returns>
    /// <c>application/pdf</c>, the media type of an image, of a known document, <see cref="TextMediaType"/> for
    /// UTF-8 text, or <see cref="BinaryMediaType"/>.
    /// </returns>
    public static string FindMediaType(ReadOnlySpan<byte> content, string? fileName)
    {
        if (content.StartsWith("%PDF-"u8)) return AgentPdfInputs.MediaType;
        if (AgentImagePreparation.FindMediaType(content) is { } image) return image;
        var extension = Path.GetExtension(fileName ?? string.Empty);
        if (DocumentMediaTypes.TryGetValue(extension, out var document))
        {
            // An office document and an EPUB are ZIP archives; an RTF document is text that starts with its header.
            var matches = string.Equals(extension, ".rtf", StringComparison.OrdinalIgnoreCase)
                ? content.StartsWith("{\\rtf"u8)
                : content.StartsWith("PK\u0003\u0004"u8);
            if (matches) return document;
        }

        return IsText(content) ? TextMediaType : BinaryMediaType;
    }

    /// <summary>Gets whether content is text: UTF-8 without a NUL character and with few other control characters.</summary>
    /// <param name="content">The content of a file.</param>
    /// <returns>True for text, including an empty file.</returns>
    public static bool IsText(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) content = content[3..];
        if (!System.Text.Unicode.Utf8.IsValid(content)) return false;
        var control = 0;
        foreach (var value in content)
        {
            if (value == 0) return false;
            if (value < 0x20 && value is not ((byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C)) control++;
        }

        return control * 10 <= content.Length;
    }

    /// <summary>Maps an attached file to the part of a user message that names it.</summary>
    /// <param name="file">The attached file.</param>
    /// <returns>
    /// A PDF reference (<see cref="AgentPdfInputs"/> decides, for each request, what it becomes); for any other
    /// file, the text the model is given.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
    public static AgentMessagePart Map(AgentInputItem.LocalFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var name = Name(file);
        if (string.Equals(file.MediaType, AgentPdfInputs.MediaType, StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(file.Path))
        {
            return new AgentMessagePart.Uri(file.Path, AgentPdfInputs.MediaType, name);
        }

        try
        {
            if (string.Equals(file.MediaType, TextMediaType, StringComparison.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file.Path);
                if (!info.Exists) return new AgentMessagePart.Text(Line(file, "the file no longer exists"));
                if (info.Length > MaximumBytes) return new AgentMessagePart.Text(Line(file, $"{AgentImagePreparation.FormatBytes(info.Length)}, too large to include"));
                return new AgentMessagePart.Text(Inline(name, file.Path, file.Path, converted: false));
            }

            var converted = ConvertedPath(file.Path);
            if (File.Exists(converted))
            {
                return new AgentMessagePart.Text(Inline(name, file.Path, converted, converted: true));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new AgentMessagePart.Text(Line(file, "the file could not be read"));
        }

        return new AgentMessagePart.Text(IsDocument(file.MediaType)
            ? Line(file, "a document that could not be converted to text: convert it with the document-conversion skill, or open it with your tools")
            : Line(file, "not text: open it with your tools"));
    }

    /// <summary>The text that gives a model the content of an attached file, or the Markdown it was converted to.</summary>
    /// <param name="name">The name of the attached file.</param>
    /// <param name="path">The path of the attached copy.</param>
    /// <param name="source">The file whose text is given: the copy itself, or the Markdown it was converted to.</param>
    /// <param name="converted">Whether <paramref name="source"/> is the Markdown the file was converted to.</param>
    /// <returns>The content between <c>attached_file</c> tags, cut at <see cref="MaximumInlineCharacters"/>.</returns>
    /// <exception cref="IOException">The file could not be read.</exception>
    public static string Inline(string name, string path, string source, bool converted)
    {
        // At most one character more than is given: that one says that the file goes on.
        var buffer = new char[MaximumInlineCharacters + 1];
        int count;
        using (var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            count = reader.ReadBlock(buffer, 0, buffer.Length);
        var text = new string(buffer, 0, count);
        var builder = new StringBuilder(Math.Min(text.Length, MaximumInlineCharacters) + 256);
        builder.Append("<attached_file name=\"").Append(Attribute(name)).Append("\" path=\"").Append(Attribute(path)).Append('"');
        if (converted) builder.Append(" converted_to_markdown=\"").Append(Attribute(source)).Append('"');
        builder.Append(">\n");
        if (text.Length > MaximumInlineCharacters)
        {
            var end = MaximumInlineCharacters;
            if (char.IsHighSurrogate(text[end - 1])) end--;
            builder.Append(text, 0, end).Append('\n')
                .Append("[The file goes on: read the rest from ").Append(source).Append(".]\n");
        }
        else
        {
            builder.Append(text);
            if (text.Length > 0 && text[^1] != '\n') builder.Append('\n');
        }

        return builder.Append("</attached_file>").ToString();
    }

    /// <summary>The line of the text of a user message that names an attached file.</summary>
    /// <param name="file">The attached file.</param>
    /// <returns><c>Attached file (name): path</c>.</returns>
    public static string RenderUserInput(AgentInputItem.LocalFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return $"Attached file ({Name(file)}): {file.Path}";
    }

    private static string Line(AgentInputItem.LocalFile file, string note)
        => $"Attached file: {Name(file)} ({file.MediaType}), saved at {file.Path} ({note}).";

    private static string Name(AgentInputItem.LocalFile file)
        => string.IsNullOrWhiteSpace(file.DisplayName) ? Path.GetFileName(file.Path) : file.DisplayName;

    private static string Attribute(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal);
}
