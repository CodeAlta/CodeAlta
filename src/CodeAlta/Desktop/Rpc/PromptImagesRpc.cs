using System.Globalization;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Serves the images of a persisted user message to the desktop window's timeline, one image per request.
/// </summary>
/// <remarks>
/// A request names a session, the journal offset of a user message and the index of one of its images
/// (<see cref="HistoryImage.Index"/>). The path is taken from the journal record, never from the request, and
/// the file is served only from the session's prompt-image folder, as a PNG, JPEG, GIF, WebP or BMP image of
/// at most <see cref="PromptImageHistory.MaximumImageBytes"/>. Reading is not tied to a project: the images
/// of an archived project's sessions are served like any other.
/// </remarks>
[NeoRpcService("promptImages", Version = 1)]
internal sealed class PromptImagesService
{
    private readonly Func<string, long, int, CancellationToken, Task<PromptImageReadResult>>? _read;
    private readonly string? _epoch;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal PromptImagesService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="reads">The host's admitted workspace reads.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reads"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal PromptImagesService(OwnedSessionWorkspace reads, string epoch)
        : this((reads ?? throw new ArgumentNullException(nameof(reads))).ReadPromptImageAsync, epoch)
    {
    }

    /// <summary>Creates the service over a literal image read.</summary>
    /// <param name="read">Reads the image at (session, record offset, image index).</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal PromptImagesService(Func<string, long, int, CancellationToken, Task<PromptImageReadResult>> read, string epoch)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _read = read;
        _epoch = epoch;
    }

    /// <summary>Returns one image of a persisted user message as base64.</summary>
    [NeoRpcMethod("read")]
    public async Task<PromptImageResponse> ReadAsync(PromptImageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_read is null) return Refused("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Refused("stale_epoch");
        if (!Identity(request.SessionId) || request.Index < 0 || request.Offset is not { Length: >= 1 and <= 19 } text
            || text.Any(static character => character is < '0' or > '9')
            || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)) return Refused("invalid");
        cancellationToken.ThrowIfCancellationRequested();
        Task<PromptImageReadResult> reading;
        // Only the synchronous shared gate refusal is capacity; an admitted read reports its failure when awaited.
        try { reading = _read(request.SessionId, offset, request.Index, cancellationToken); }
        catch (ObjectDisposedException) { return Refused("closed"); }
        catch (InvalidOperationException) { return Refused("capacity"); }
        PromptImageReadResult result;
        try { result = await reading.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ObjectDisposedException) { return Refused("closed"); }
        catch (Exception) { return Refused("read_failed"); } // Never serialize exception details: they name absolute paths.
        return result.Status switch
        {
            PromptImageReadStatus.Ok when result is { MediaType: { } mediaType, Bytes: { } bytes } => new("ok", mediaType, Convert.ToBase64String(bytes)),
            PromptImageReadStatus.MissingSession => Refused("missing_session"),
            PromptImageReadStatus.MissingRecord => Refused("missing_record"),
            PromptImageReadStatus.MissingImage => Refused("missing_image"),
            PromptImageReadStatus.MissingFile => Refused("missing_file"),
            PromptImageReadStatus.OutsideStore => Refused("outside_store"),
            PromptImageReadStatus.UnsupportedType => Refused("unsupported_type"),
            PromptImageReadStatus.TooLarge => Refused("too_large"),
            _ => Refused("read_failed"),
        };
    }

    private static PromptImageResponse Refused(string status) => new(status, null, null);

    private static bool Identity(string? value)
        => value is { Length: >= 1 and <= 256 } && !string.IsNullOrWhiteSpace(value) && value == value.Trim() && !value.Any(char.IsControl);
}

/// <summary>Asks for one image of a persisted user message.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="SessionId">The selected session.</param>
/// <param name="Offset">The journal offset of the user message, as its history row reported it.</param>
/// <param name="Index">The index of the image among those of the message.</param>
internal sealed record PromptImageRequest(string ExpectedEpoch, string SessionId, string Offset, int Index);

/// <summary>One image, or the reason it is not served.</summary>
/// <param name="Status">
/// <c>ok</c>, or <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid</c>, <c>capacity</c>, <c>closed</c>,
/// <c>missing_session</c>, <c>missing_record</c>, <c>missing_image</c>, <c>missing_file</c>, <c>outside_store</c>,
/// <c>unsupported_type</c>, <c>too_large</c> or <c>read_failed</c>.
/// </param>
/// <param name="MediaType">The media type found in the file's header; null unless <c>ok</c>.</param>
/// <param name="Base64">The file content; null unless <c>ok</c>.</param>
internal sealed record PromptImageResponse(string Status, string? MediaType, string? Base64);
