using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// The PDF files attached to a prompt. A user message names an attached PDF by an <see cref="AgentMessagePart.Uri"/>
/// part (its full path, <c>application/pdf</c>); each request gives it to a model that declares PDF input as the
/// document itself, and to any other as the line every other attached file is.
/// </summary>
internal static class AgentPdfInputs
{
    /// <summary>The capability of a model that says whether it reads PDF documents: true, false, or absent when unknown.</summary>
    public const string CapabilityKey = "supportsPdfInput";

    /// <summary>The media type of a PDF document.</summary>
    public const string MediaType = "application/pdf";

    /// <summary>Largest PDF sent, in bytes: the API of Anthropic takes requests of 32 MB, base64 included.</summary>
    public const long MaximumBytes = 20L * 1024 * 1024;

    /// <summary>Gets whether a model declares that it reads PDF documents.</summary>
    /// <param name="model">The model, or null when it is not known.</param>
    /// <returns>True only when the model says so.</returns>
    public static bool AcceptsPdf(AgentModelInfo? model)
        => model?.Capabilities?.TryGetValue(CapabilityKey, out var value) == true &&
            value switch { bool flag => flag, JsonElement { ValueKind: JsonValueKind.True } => true, _ => false };

    /// <summary>Gets whether a file attached to a prompt is sent as a PDF document reference.</summary>
    /// <param name="file">The attached file.</param>
    /// <returns>True for a whole file whose name ends with <c>.pdf</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
    public static bool IsPdf(AgentInputItem.File file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.LineRange is null && string.Equals(Path.GetExtension(file.Path), ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gives the PDF references of a conversation the form the model of a request takes.</summary>
    /// <param name="messages">The conversation of the request.</param>
    /// <param name="model">The model of the request.</param>
    /// <returns>The conversation to send; <paramref name="messages"/> itself when it names no PDF.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is null.</exception>
    public static IReadOnlyList<AgentConversationMessage> Resolve(IReadOnlyList<AgentConversationMessage> messages, AgentModelInfo? model)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (!messages.Any(static message => message.Parts.Any(IsReference)))
        {
            return messages;
        }

        var acceptsPdf = AcceptsPdf(model);
        return messages
            .Select(message => message.Parts.Any(IsReference)
                ? message with { Parts = message.Parts.Select(part => IsReference(part) ? Resolve((AgentMessagePart.Uri)part, acceptsPdf) : part).ToArray() }
                : message)
            .ToArray();
    }

    private static bool IsReference(AgentMessagePart part)
        => part is AgentMessagePart.Uri { MediaType: MediaType } uri && Path.IsPathFullyQualified(uri.Value);

    private static AgentMessagePart Resolve(AgentMessagePart.Uri reference, bool acceptsPdf)
    {
        var line = $"File: {reference.Value}";
        if (!acceptsPdf)
        {
            return new AgentMessagePart.Text(line);
        }

        try
        {
            var info = new FileInfo(reference.Value);
            if (!info.Exists)
            {
                return new AgentMessagePart.Text($"{line} (the file no longer exists)");
            }

            if (info.Length > MaximumBytes)
            {
                return new AgentMessagePart.Text($"{line} (a PDF of {Images.AgentImagePreparation.FormatBytes(info.Length)}, too large to attach: the limit is {Images.AgentImagePreparation.FormatBytes(MaximumBytes)})");
            }

            var bytes = File.ReadAllBytes(reference.Value);
            return bytes.AsSpan().StartsWith("%PDF-"u8)
                ? new AgentMessagePart.Data(Convert.ToBase64String(bytes), MediaType, Path.GetFileName(reference.Value))
                : new AgentMessagePart.Text($"{line} (not a PDF document)");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new AgentMessagePart.Text($"{line} (the file could not be read)");
        }
    }
}
