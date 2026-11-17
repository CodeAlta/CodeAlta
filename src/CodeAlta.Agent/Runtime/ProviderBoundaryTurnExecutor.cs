using CodeAlta.Agent.Runtime.Images;

namespace CodeAlta.Agent.Runtime;

// Passes each request of a session through the boundary of a confidential session before the executor it wraps, and
// restores what comes back: the deltas as they stream, then the whole answer. A session that is not confidential (the
// source gives no boundary) sends its requests as they are. What cannot be made safe is never sent: the request fails
// before the wrapped executor is called.
internal sealed class ProviderBoundaryTurnExecutor(
    IModelProviderTurnExecutor inner,
    IAgentProviderBoundarySource source,
    bool providerRunsOwnTools) : IModelProviderTurnExecutor
{
    public Task<AgentTurnResponse> ExecuteTurnAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(request, onUpdate, onSessionUpdate: null, cancellationToken);

    public Task<AgentTurnResponse> ExecuteTurnAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onSessionUpdate);
        return ExecuteCoreAsync(request, onUpdate, onSessionUpdate, cancellationToken);
    }

    private async Task<AgentTurnResponse> ExecuteCoreAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask>? onSessionUpdate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onUpdate);
        AgentTurnRequest sent;
        IAgentProviderBoundary? boundary;
        try
        {
            boundary = await source.GetBoundaryAsync(request, cancellationToken).ConfigureAwait(false);
            if (boundary is null)
            {
                // A source that says the session is confidential and gives no boundary is at fault: nothing goes as it is.
                if (source.IsConfidential) throw new AgentProviderBoundaryException();
                return await ExecuteInnerAsync(request, onUpdate, onSessionUpdate, cancellationToken).ConfigureAwait(false);
            }

            // An agent that reads files and runs commands itself sends what it reads to its provider without CodeAlta.
            if (providerRunsOwnTools)
            {
                throw new AgentProviderBoundaryException(
                    "This session is confidential, and its provider runs an agent that reads files itself (such as Claude Code, Codex CLI or an ACP agent): CodeAlta cannot pseudonymize what it sends. Nothing was sent. Choose a provider whose requests CodeAlta sends itself.");
            }

            // An image becomes a note before the boundary, which pseudonymizes the note (the name of the image) as any text.
            sent = await boundary.PrepareRequestAsync(WithoutImages(request), cancellationToken).ConfigureAwait(false);
            EnsureNoBinaryContent(sent);
        }
        catch (AgentProviderBoundaryException exception)
        {
            throw Refused(exception);
        }

        var restore = new DeltaRestorer(boundary, onUpdate);
        var response = await ExecuteInnerAsync(sent, restore.WriteAsync, onSessionUpdate, cancellationToken).ConfigureAwait(false);
        // What a stream held back ends with the answer. A request that failed leaves its drafts, which the session discards.
        await restore.FlushAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return boundary.RestoreResponse(sent, response);
        }
        catch (AgentProviderBoundaryException exception)
        {
            throw Refused(exception);
        }
    }

    private Task<AgentTurnResponse> ExecuteInnerAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask>? onSessionUpdate,
        CancellationToken cancellationToken)
        => onSessionUpdate is null
            ? inner.ExecuteTurnAsync(request, onUpdate, cancellationToken)
            : inner.ExecuteTurnAsync(request, onUpdate, onSessionUpdate, cancellationToken);

    // An image cannot be pseudonymized: a confidential request carries a note in its place, the attached image as the
    // image a tool returned, so that the session goes on and the model knows it was there. The note is the same at each
    // request, so that what was sent is sent again as it was. A request without an image is given back as it is.
    internal static AgentTurnRequest WithoutImages(AgentTurnRequest request)
    {
        var changed = false;
        var conversation = request.Conversation.Select(message =>
        {
            var parts = message.Parts.Select(Replace).ToList();
            if (parts.SequenceEqual(message.Parts, ReferenceEqualityComparer.Instance)) return message;
            changed = true;
            return message with { Parts = parts };
        }).ToList();
        return changed ? request with { Conversation = conversation } : request;

        static AgentMessagePart Replace(AgentMessagePart part) => part switch
        {
            AgentMessagePart.Data data when !IsPdf(data.MediaType) => new AgentMessagePart.Text(ImageNote(data.Name)),
            AgentMessagePart.Uri uri when !IsPdf(uri.MediaType) => new AgentMessagePart.Text(ImageNote(uri.Name ?? uri.Value)),
            AgentMessagePart.ToolResult result when result.Result.Items.Any(static item => AgentToolResultImages.IsImage(item) || item is AgentToolResultItem.ImageUrl) => result with
            {
                Result = result.Result with
                {
                    Items = [.. result.Result.Items.Select(static item => item switch
                    {
                        AgentToolResultItem.ImageUrl image => new AgentToolResultItem.Text(ImageNote(image.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? null : image.Url)),
                        _ when AgentToolResultImages.IsImage(item) => new AgentToolResultItem.Text(ImageNote(AgentToolResultImages.Name(item))),
                        _ => item,
                    })],
                },
            },
            _ => part,
        };

        static bool IsPdf(string? mediaType) => string.Equals(mediaType, AgentPdfInputs.MediaType, StringComparison.OrdinalIgnoreCase);
    }

    private static string ImageNote(string? name)
        => $"[Image not sent{(string.IsNullOrWhiteSpace(name) ? "" : ": " + name)}. This session is confidential, and an image cannot be pseudonymized: ask the user for its text if you need it.]";

    // Documents and other binary content cannot be pseudonymized: a confidential request never carries them, whatever the
    // boundary left. Images were replaced by a note before it.
    private static void EnsureNoBinaryContent(AgentTurnRequest request)
    {
        foreach (var message in request.Conversation)
        {
            foreach (var part in message.Parts)
            {
                var binary = part switch
                {
                    AgentMessagePart.Data or AgentMessagePart.Uri => true,
                    AgentMessagePart.ToolResult toolResult => toolResult.Result.Items.Any(static item => item is not AgentToolResultItem.Text and not AgentToolResultItem.ToolReference),
                    _ => false,
                };
                if (binary)
                {
                    throw new AgentProviderBoundaryException(part is AgentMessagePart.Data { MediaType: AgentPdfInputs.MediaType } or AgentMessagePart.Uri { MediaType: AgentPdfInputs.MediaType }
                        ? "This session is confidential, and a PDF of its conversation was not converted to text, which is what is pseudonymized: attach it again. Nothing was sent."
                        : "This session is confidential, and its conversation holds an image, which cannot be pseudonymized. Nothing was sent.");
                }
            }
        }
    }

    private static AgentTurnExecutionException Refused(AgentProviderBoundaryException exception)
        => new(new AgentTurnFailure(exception.Message, IsContextOverflow: false, new AgentFailureInfo(AgentFailureKind.Other)), exception);

    // The streams of one request, one per content of each attempt: a retry of the provider starts its contents again.
    private sealed class DeltaRestorer(IAgentProviderBoundary boundary, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate)
    {
        private readonly Dictionary<(string? AttemptId, string ContentId), (IAgentBoundaryTextStream Stream, AgentTurnDelta Last)> _streams = [];
        // The attempt of the last part: the one the answer is made of.
        private string? _attemptId;

        public ValueTask WriteAsync(AgentTurnDelta delta, CancellationToken cancellationToken)
        {
            var key = (delta.AttemptId, delta.ContentId);
            _attemptId = delta.AttemptId;
            var stream = _streams.TryGetValue(key, out var held) ? held.Stream : boundary.CreateResponseStream(delta.Kind, delta.ContentId);
            _streams[key] = (stream, delta);
            var text = stream.Write(delta.Text);
            return text.Length == 0 && delta.Text.Length > 0 && delta.Details is null
                ? ValueTask.CompletedTask
                : onUpdate(delta with { Text = text }, cancellationToken);
        }

        public async ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            foreach (var (key, (stream, last)) in _streams)
            {
                // What an attempt the provider started again held back is no part of the answer.
                if (!string.Equals(key.AttemptId, _attemptId, StringComparison.Ordinal)) continue;
                var text = stream.Flush();
                if (text.Length > 0) await onUpdate(last with { Text = text, Details = null }, cancellationToken).ConfigureAwait(false);
            }

            _streams.Clear();
        }
    }
}