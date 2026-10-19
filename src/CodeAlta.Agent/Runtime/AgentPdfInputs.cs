using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// The PDF files attached to a prompt. A user message names an attached PDF by an <see cref="AgentMessagePart.Uri"/>
/// part (its full path, <c>application/pdf</c>); each request gives it to a model that declares PDF input as the
/// document itself, and to any other as the Markdown the PDF was converted to when it was sent
/// (<see cref="AgentAttachedFiles.ConvertedPath"/>), or else as the line every other referenced file is.
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
        => Resolve(messages, model, cache: null);

    /// <summary>Gives the PDF references of a conversation the form the model of a request takes, reusing what was read.</summary>
    /// <param name="messages">The conversation of the request.</param>
    /// <param name="model">The model of the request.</param>
    /// <param name="cache">The reads of the run, or null to read every file again.</param>
    /// <returns>The conversation to send; <paramref name="messages"/> itself when it names no PDF.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is null.</exception>
    public static IReadOnlyList<AgentConversationMessage> Resolve(IReadOnlyList<AgentConversationMessage> messages, AgentModelInfo? model, AgentPdfInputCache? cache)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (!messages.Any(static message => message.Parts.Any(IsReference)))
        {
            return messages;
        }

        var acceptsPdf = AcceptsPdf(model);
        return messages
            .Select(message => message.Parts.Any(IsReference)
                ? message with { Parts = message.Parts.Select(part => IsReference(part) ? Resolve((AgentMessagePart.Uri)part, acceptsPdf, cache) : part).ToArray() }
                : message)
            .ToArray();
    }

    private static bool IsReference(AgentMessagePart part)
        => part is AgentMessagePart.Uri { MediaType: MediaType } uri && Path.IsPathFullyQualified(uri.Value);

    private static AgentMessagePart Resolve(AgentMessagePart.Uri reference, bool acceptsPdf, AgentPdfInputCache? cache)
    {
        if (cache is null)
        {
            return Resolve(reference, acceptsPdf);
        }

        // The files are those of the reference and of its Markdown: while neither changed, the part is the one read before.
        var stamp = (Stamp(reference.Value), acceptsPdf ? default : Stamp(AgentAttachedFiles.ConvertedPath(reference.Value)));
        var key = (reference.Value, reference.Name, acceptsPdf);
        if (cache.TryGet(key, stamp, out var known))
        {
            return known;
        }

        var resolved = Resolve(reference, acceptsPdf);
        cache.Set(key, stamp, resolved);
        return resolved;
    }

    private static (long Length, long LastWriteUtcTicks) Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return (-2, 0);
        }
    }

    private static AgentMessagePart Resolve(AgentMessagePart.Uri reference, bool acceptsPdf)
    {
        var line = $"File: {reference.Value}";
        try
        {
            if (!acceptsPdf)
            {
                // A PDF attached to a prompt was converted to Markdown when it was sent, for the models that read no PDF; a
                // PDF of the project named with @ is not, whatever is beside it.
                var converted = AgentAttachedFiles.ConvertedPath(reference.Value);
                return AgentAttachedFiles.IsAttachedCopy(reference.Value) && File.Exists(converted)
                    ? new AgentMessagePart.Text(AgentAttachedFiles.Inline(reference.Name ?? Path.GetFileName(reference.Value), reference.Value, converted, converted: true))
                    : new AgentMessagePart.Text(line);
            }

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
                ? new AgentMessagePart.Data(Convert.ToBase64String(bytes), MediaType, reference.Name ?? Path.GetFileName(reference.Value))
                : new AgentMessagePart.Text($"{line} (not a PDF document)");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new AgentMessagePart.Text($"{line} (the file could not be read)");
        }
    }
}

/// <summary>
/// The PDF references a run resolved, with the stamps of the files they were read from: a PDF is read and encoded once for
/// the requests of a run, not again for each of them. Thread-safe.
/// </summary>
internal sealed class AgentPdfInputCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Path, string? Name, bool AcceptsPdf), (((long, long) Reference, (long, long) Converted) Stamp, AgentMessagePart Part)> _parts = [];

    public bool TryGet((string Path, string? Name, bool AcceptsPdf) key, ((long, long), (long, long)) stamp, out AgentMessagePart part)
    {
        lock (_gate)
        {
            if (_parts.TryGetValue(key, out var known) && known.Stamp == stamp)
            {
                part = known.Part;
                return true;
            }
        }

        part = null!;
        return false;
    }

    public void Set((string Path, string? Name, bool AcceptsPdf) key, ((long, long), (long, long)) stamp, AgentMessagePart part)
    {
        lock (_gate)
        {
            _parts[key] = (stamp, part);
        }
    }

    /// <summary>Forgets what the run that ended read.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _parts.Clear();
        }
    }
}
