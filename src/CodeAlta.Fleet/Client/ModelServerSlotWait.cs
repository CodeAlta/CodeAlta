using System.Net;
using System.Text.Json;
using CodeAlta.Agent.Runtime;
using CodeAlta.ModelServers;

namespace CodeAlta.Fleet.Client;

/// <summary>
/// A request of inference that takes a slot of its model on the machine of its server (<see cref="ModelSlotGates"/>): the model
/// it names, the wait in turn, and the release of the slot once its answer is read to its end or let go.
/// </summary>
internal static class ModelServerSlotWait
{
    /// <summary>The paths whose requests a slot serves; the others (the list of models, the health) take none.</summary>
    private static readonly string[] InferencePaths =
    [
        "/v1/chat/completions", "/v1/completions", "/v1/responses", "/v1/embeddings", "/v1/rerank", "/v1/messages",
        "/infill", "/completion", "/embedding", "/rerank",
    ];

    /// <summary>Whether a request takes a slot: a <c>POST</c> of inference.</summary>
    /// <param name="method">Its method.</param>
    /// <param name="path">Its path, with or without a query.</param>
    /// <returns>Whether it does.</returns>
    public static bool TakesSlot(string method, string path)
        => method == "POST" && InferencePaths.Contains(path.Split('?', 2)[0], StringComparer.Ordinal);

    /// <summary>The model the body of a request names (its <c>model</c>); null when it names none or is not JSON.</summary>
    /// <param name="body">The body.</param>
    /// <returns>The model, or null.</returns>
    public static string? ModelOf(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty) return null;
        try
        {
            var reader = new Utf8JsonReader(body);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var named = reader.ValueTextEquals("model"u8);
                if (!reader.Read()) return null;
                if (named) return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                reader.Skip();
            }
        }
        catch (JsonException)
        {
            // Not JSON: no model is named.
        }

        return null;
    }

    /// <summary>Takes a slot, and tells the session of the request its place while it waits.</summary>
    /// <param name="gate">The slots of the model.</param>
    /// <param name="name">What the session reads it waits for: the server as its provider names it (<c>pc-ai/coder</c>).</param>
    /// <param name="updates">The updates of the session that sends the request; null tells nobody.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The slot.</returns>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public static async Task<IDisposable> AcquireAsync(ModelSlotGate gate, string name, Func<AgentTurnSessionUpdate, CancellationToken, ValueTask>? updates,
        CancellationToken cancellationToken)
    {
        if (updates is null) return await gate.AcquireAsync(null, cancellationToken).ConfigureAwait(false);
        var notice = new AgentWaitNotice(AgentWaitKind.ModelServerSlot, name, updates);
        try
        {
            return await gate.AcquireAsync(notice.ReportAsync, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await notice.EndAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The updates of the session that sends a request, which the transport of its provider carries.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The updates, or null.</returns>
    public static Func<AgentTurnSessionUpdate, CancellationToken, ValueTask>? UpdatesOf(HttpRequestMessage request)
        => request.Options.TryGetValue(AgentWaitScope.RequestOption, out var updates) ? updates : null;

    /// <summary>The answer of a request whose slot is given back once its body is read to its end, or let go.</summary>
    /// <param name="response">The answer.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="cancellationToken">Cancels the read of the start of its body.</param>
    /// <returns>The answer, its body releasing the slot.</returns>
    public static async Task<HttpResponseMessage> HoldUntilReadAsync(HttpResponseMessage response, IDisposable slot, CancellationToken cancellationToken)
    {
        var inner = response.Content;
        Stream stream;
        try
        {
            stream = await inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            slot.Dispose();
            throw;
        }

        var held = new StreamContent(new ReleasingStream(stream, slot, inner));
        foreach (var header in inner.Headers) held.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = held;
        return response;
    }

    /// <summary>The body of an answer that gives its slot back at its end, or once disposed, whichever comes first.</summary>
    private sealed class ReleasingStream(Stream inner, IDisposable slot, HttpContent content) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read;
            try
            {
                read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                slot.Dispose();
                throw;
            }

            if (read == 0 && buffer.Length > 0) slot.Dispose();
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read == 0 && count > 0) slot.Dispose();
            return read;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                slot.Dispose();
                inner.Dispose();
                content.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>A 503 answer that says why, as a server would.</summary>
    /// <param name="request">The request.</param>
    /// <param name="message">Why.</param>
    /// <returns>The answer.</returns>
    public static HttpResponseMessage Unavailable(HttpRequestMessage request, string message)
        => new(HttpStatusCode.ServiceUnavailable)
        {
            RequestMessage = request,
            Content = new StringContent("{\"error\":{\"code\":503,\"message\":\"" + message.Replace("\"", "'", StringComparison.Ordinal) + "\"}}", System.Text.Encoding.UTF8, "application/json"),
        };
}
