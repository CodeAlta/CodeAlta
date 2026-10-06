using System.Globalization;
using System.Text;

namespace CodeAlta.Agent.Runtime.Images;

/// <summary>A session store that has a folder for the files a session keeps beside its journal.</summary>
public interface IAgentSessionAttachmentStore
{
    /// <summary>Gets the folder of a session's attachments; the folder may not exist yet.</summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The full path of the folder, or null when the session has no journal.</returns>
    ValueTask<string?> GetAttachmentDirectoryAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>
/// The images of a session's tool results: an image a tool returns is checked, made to fit what a model accepts
/// and saved in the session's attachment folder; a request reads it back for the run that asked for it.
/// </summary>
internal sealed class AgentSessionToolImages
{
    /// <summary>Most images kept from one tool result.</summary>
    public const int MaximumImagesPerResult = 8;

    private readonly IAgentSessionAttachmentStore? _store;
    private readonly string _sessionId;
    private readonly AgentImageLimits _limits;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    // The images of the active run, in base64 by path: a run sends them with each of its requests.
    private readonly Dictionary<string, string> _loaded = new(StringComparer.Ordinal);

    public AgentSessionToolImages(IAgentSessionAttachmentStore? store, string sessionId, AgentImageLimits? limits = null, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _store = store;
        _sessionId = sessionId;
        _limits = limits ?? AgentImageLimits.Default;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Prepares the images of a tool result. Each image becomes a saved image, or a line that says why the model
    /// does not get it.
    /// </summary>
    /// <param name="result">What the tool returned.</param>
    /// <param name="modelInfo">The model of the run; one that declares no image input gets no image.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The result to record; <paramref name="result"/> itself when it has no image.</returns>
    public async Task<AgentToolResult> PrepareAsync(AgentToolResult result, AgentModelInfo? modelInfo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Items.Any(static item => item is AgentToolResultItem.Image)) return result;

        var acceptsImages = AgentImageInputCapability.Read(modelInfo) != false;
        string? directory = null;
        var directoryResolved = false;
        var items = new List<AgentToolResultItem>(result.Items.Count);
        var count = 0;
        foreach (var item in result.Items)
        {
            if (item is not AgentToolResultItem.Image image)
            {
                items.Add(item);
                continue;
            }

            var name = string.IsNullOrWhiteSpace(image.DisplayName) ? "image" : image.DisplayName.Trim();
            if (!acceptsImages)
            {
                items.Add(new AgentToolResultItem.Text($"[Image not attached: {name}. The current model does not accept image input.]"));
                continue;
            }

            if (++count > MaximumImagesPerResult)
            {
                items.Add(new AgentToolResultItem.Text(string.Create(CultureInfo.InvariantCulture,
                    $"[Image omitted: {name}. A tool result keeps at most {MaximumImagesPerResult} images.]")));
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(image.Base64Data);
            }
            catch (FormatException)
            {
                items.Add(new AgentToolResultItem.Text($"[Image omitted: {name}. Its data is not valid base64.]"));
                continue;
            }

            var (prepared, error) = await Task.Run(
                    () => AgentImagePreparation.TryPrepare(bytes, _limits, out var ready, out var reason) ? (ready, (string?)null) : (null, reason),
                    cancellationToken)
                .ConfigureAwait(false);
            if (prepared is null)
            {
                items.Add(new AgentToolResultItem.Text($"[Image omitted: {name}. {error}]"));
                continue;
            }

            var base64 = ReferenceEquals(prepared.Bytes, bytes) ? image.Base64Data : Convert.ToBase64String(prepared.Bytes);
            if (!directoryResolved)
            {
                directoryResolved = true;
                directory = _store is null ? null : await _store.GetAttachmentDirectoryAsync(_sessionId, cancellationToken).ConfigureAwait(false);
            }

            var saved = directory is null ? null : await TrySaveAsync(directory, name, prepared, cancellationToken).ConfigureAwait(false);
            if (saved is null)
            {
                // No folder to save to: the result keeps the bytes.
                items.Add(new AgentToolResultItem.Image(base64, prepared.MediaType, image.DisplayName));
                continue;
            }

            lock (_gate)
            {
                _loaded[saved] = base64;
            }

            items.Add(new AgentToolResultItem.LocalImage(saved, prepared.MediaType, image.DisplayName, prepared.Width, prepared.Height));
        }

        return result with { Items = items };
    }

    /// <summary>
    /// Gives a request the images of its tool results as bytes. A saved image whose file is gone, and every
    /// image when the model declares no image input, becomes a line of text.
    /// </summary>
    /// <param name="messages">The conversation of the request, after the images of earlier runs were pruned.</param>
    /// <param name="modelInfo">The model of the request.</param>
    /// <returns>The conversation to send; <paramref name="messages"/> itself when nothing changed.</returns>
    public IReadOnlyList<AgentConversationMessage> Resolve(IReadOnlyList<AgentConversationMessage> messages, AgentModelInfo? modelInfo)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (!AgentToolResultImages.HasImages(messages)) return messages;
        var acceptsImages = AgentImageInputCapability.Read(modelInfo) != false;
        return AgentToolResultImages.ReplaceImages(messages, static _ => true, item =>
        {
            var name = AgentToolResultImages.Name(item);
            if (!acceptsImages)
            {
                return new AgentToolResultItem.Text($"[Image not attached: {name}. The current model does not accept image input.]");
            }

            if (item is not AgentToolResultItem.LocalImage local) return item;
            return TryLoad(local.Path) is { } base64
                ? new AgentToolResultItem.Image(base64, local.MediaType, local.DisplayName)
                : new AgentToolResultItem.Text($"[Image no longer available: {name}.]");
        });
    }

    /// <summary>Forgets the images read for the run that ended.</summary>
    public void ReleaseRun()
    {
        lock (_gate)
        {
            _loaded.Clear();
        }
    }

    private string? TryLoad(string path)
    {
        lock (_gate)
        {
            if (_loaded.TryGetValue(path, out var known)) return known;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > _limits.MaximumBytes) return null;
            var base64 = Convert.ToBase64String(File.ReadAllBytes(path));
            lock (_gate)
            {
                _loaded[path] = base64;
            }

            return base64;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<string?> TrySaveAsync(string directory, string name, AgentPreparedImage image, CancellationToken cancellationToken)
    {
        var extension = image.MediaType switch
        {
            "image/jpeg" => "jpg",
            "image/webp" => "webp",
            _ => "png",
        };
        var stem = string.Create(CultureInfo.InvariantCulture, $"{_clock.GetUtcNow():yyyyMMddHHmmssfff}-tool-{SanitizeName(name)}");
        try
        {
            Directory.CreateDirectory(directory);
            for (var attempt = 1; attempt <= 100; attempt++)
            {
                var path = Path.Combine(directory, attempt == 1
                    ? $"{stem}.{extension}"
                    : string.Create(CultureInfo.InvariantCulture, $"{stem}-{attempt}.{extension}"));
                FileStream stream;
                try
                {
                    stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true);
                }
                catch (IOException) when (File.Exists(path))
                {
                    continue;
                }

                try
                {
                    await using (stream.ConfigureAwait(false))
                    {
                        await stream.WriteAsync(image.Bytes, cancellationToken).ConfigureAwait(false);
                    }

                    return path;
                }
                catch
                {
                    TryDelete(path);
                    throw;
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    // The name a tool gave, as part of a file name on any platform.
    private static string SanitizeName(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name.Replace('\\', '/').Split('/')[^1]);
        var builder = new StringBuilder(Math.Min(stem.Length, 48));
        foreach (var character in stem)
        {
            if (builder.Length == 48) break;
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        }

        var text = builder.ToString().Trim('-');
        return text.Length == 0 ? "image" : text;
    }
}
