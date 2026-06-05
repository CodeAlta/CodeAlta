#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.OpenAI;

internal sealed class OpenAIProtocolTraceLogger
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly object _gate = new();
    private readonly int _maxBodyBytes;
    private bool _disabled;

    private OpenAIProtocolTraceLogger(string traceFilePath, int maxBodyBytes)
    {
        TraceFilePath = traceFilePath;
        _maxBodyBytes = Math.Max(1024, maxBodyBytes);
    }

    public string TraceFilePath { get; }

    public static OpenAIProtocolTraceLogger? Create(
        OpenAIProtocolTraceOptions? options,
        AgentTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (options?.Enabled != true || string.IsNullOrWhiteSpace(options.StateRootPath))
        {
            return null;
        }

        var layout = new AgentRuntimePathLayout(options.StateRootPath);
        var logger = new OpenAIProtocolTraceLogger(
            layout.GetSessionTraceFilePath(request.SessionId),
            options.MaxBodyBytes);
        logger.WriteLine(
            $"### turn start provider={request.Provider.ProviderKey} runtimeProviderId={request.ProviderId.Value} session={request.SessionId} run={request.RunId.Value} model={FormatValue(request.ModelId)}");
        return logger;
    }

    public static OpenAIProtocolTraceLogger? Create(
        OpenAIProtocolTraceOptions? options,
        OpenAIResponsesClientFactoryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (options?.Enabled != true || string.IsNullOrWhiteSpace(options.StateRootPath))
        {
            return null;
        }

        var layout = new AgentRuntimePathLayout(options.StateRootPath);
        var logger = new OpenAIProtocolTraceLogger(
            layout.GetSessionTraceFilePath(context.SessionId),
            options.MaxBodyBytes);
        logger.WriteLine(
            $"### turn start provider={context.Provider.ProviderKey} runtimeProviderId={context.Provider.ProviderKey} session={context.SessionId} run={context.RunId.Value} model={FormatValue(context.ModelId)}");
        return logger;
    }

    public PipelinePolicy CreateHttpPolicy()
        => new RawHttpLoggingPolicy(WriteLine, _maxBodyBytes);

    public void WriteLine(string message)
    {
        if (_disabled)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TraceFilePath)!);
                File.AppendAllText(
                    TraceFilePath,
                    $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}",
                    Utf8WithoutBom);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            _disabled = true;
        }
    }

    private static string FormatValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "<none>" : value.Trim();

    private sealed class RawHttpLoggingPolicy(Action<string> log, int maxBodyBytes) : PipelinePolicy
    {
        public override void Process(
            PipelineMessage message,
            IReadOnlyList<PipelinePolicy> pipeline,
            int currentIndex)
        {
            LogRequest(message);

            try
            {
                ProcessNext(message, pipeline, currentIndex);
            }
            finally
            {
                LogResponse(message);
            }
        }

        public override async ValueTask ProcessAsync(
            PipelineMessage message,
            IReadOnlyList<PipelinePolicy> pipeline,
            int currentIndex)
        {
            LogRequest(message);

            try
            {
                await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
            }
            finally
            {
                LogResponse(message);
            }
        }

        private void LogRequest(PipelineMessage message)
        {
            var request = message.Request;

            log($">>> {request.Method} {request.Uri}");
            foreach (var header in request.Headers)
            {
                var value = IsSensitiveHeader(header.Key) ? "<redacted>" : header.Value;
                log($">>> {header.Key}: {value}");
            }

            var body = CaptureRequestBody(request, maxBodyBytes);
            if (body is not null)
            {
                log(">>> body:");
                log(body);
            }
        }

        private void LogResponse(PipelineMessage message)
        {
            var response = message.Response;
            if (response is null)
            {
                log("<<< response: <none>");
                return;
            }

            log($"<<< HTTP {response.Status} {response.ReasonPhrase}");
            foreach (var header in response.Headers)
            {
                var value = IsSensitiveHeader(header.Key) ? "<redacted>" : header.Value;
                log($"<<< {header.Key}: {value}");
            }

            if (!message.BufferResponse)
            {
                if (response.ContentStream is { } contentStream && IsEventStreamResponse(response))
                {
                    log("<<< body: <streaming raw SSE lines>");
                    response.ContentStream = new RawSseTraceStream(contentStream, log, maxBodyBytes);
                    return;
                }

                log("<<< body: <not buffered; streaming body is not an SSE response or is unavailable>");
                return;
            }

            try
            {
                var content = response.Content.ToString();
                log("<<< body:");
                log(TruncateText(content, maxBodyBytes));
            }
            catch (InvalidOperationException)
            {
                log("<<< body: <not available as buffered content>");
            }
        }

        private static string? CaptureRequestBody(PipelineRequest request, int maxBodyBytes)
        {
            if (request.Content is null)
            {
                return null;
            }

            try
            {
                using var buffer = new MemoryStream();
                request.Content.WriteTo(buffer, CancellationToken.None);
                var bytes = buffer.ToArray();

                // Replace the content so stream-backed content is not consumed before transport sends it.
                request.Content = BinaryContent.Create(BinaryData.FromBytes(bytes));

                var byteCount = Math.Min(bytes.Length, Math.Max(1024, maxBodyBytes));
                var text = Encoding.UTF8.GetString(bytes, 0, byteCount);
                return bytes.Length <= byteCount
                    ? text
                    : text + $"\n<truncated {bytes.Length - byteCount} bytes>";
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
            {
                return $"<failed to capture request body: {ex.GetType().Name}: {ex.Message}>";
            }
        }

        private static string TruncateText(string text, int maxBytes)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            var maxChars = Math.Max(1024, maxBytes);
            return text.Length <= maxChars
                ? text
                : text[..maxChars] + $"\n<truncated {text.Length - maxChars} chars>";
        }

        private static bool IsEventStreamResponse(PipelineResponse response)
            => response.Headers.TryGetValue("Content-Type", out var contentType) &&
               contentType?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;

        private static bool IsSensitiveHeader(string name)
            => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
               || name.Equals("api-key", StringComparison.OrdinalIgnoreCase)
               || name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)
               || name.Equals("cookie", StringComparison.OrdinalIgnoreCase)
               || name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase)
               || name.Contains("token", StringComparison.OrdinalIgnoreCase)
               || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
               || name.Contains("credential", StringComparison.OrdinalIgnoreCase)
               || name.Contains("password", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("-key", StringComparison.OrdinalIgnoreCase);

        private sealed class RawSseTraceStream(Stream inner, Action<string> log, int maxBodyBytes) : Stream
        {
            private readonly StringBuilder _line = new();
            private int _remainingBytes = Math.Max(1024, maxBodyBytes);
            private bool _disposed;
            private bool _truncated;

            public override bool CanRead => inner.CanRead;

            public override bool CanSeek => inner.CanSeek;

            public override bool CanWrite => inner.CanWrite;

            public override long Length => inner.Length;

            public override long Position
            {
                get => inner.Position;
                set => inner.Position = value;
            }

            public override void Flush() => inner.Flush();

            public override int Read(byte[] buffer, int offset, int count)
            {
                var bytesRead = inner.Read(buffer, offset, count);
                if (bytesRead > 0)
                {
                    TraceBytes(buffer.AsSpan(offset, bytesRead));
                }

                return bytesRead;
            }

            public override int Read(Span<byte> buffer)
            {
                var bytesRead = inner.Read(buffer);
                if (bytesRead > 0)
                {
                    TraceBytes(buffer[..bytesRead]);
                }

                return bytesRead;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                var bytesRead = await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                if (bytesRead > 0)
                {
                    TraceBytes(buffer.AsSpan(offset, bytesRead));
                }

                return bytesRead;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var bytesRead = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead > 0)
                {
                    TraceBytes(buffer.Span[..bytesRead]);
                }

                return bytesRead;
            }

            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

            public override void SetLength(long value) => inner.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

            protected override void Dispose(bool disposing)
            {
                if (!_disposed)
                {
                    if (disposing)
                    {
                        FlushPartialLine();
                        inner.Dispose();
                    }

                    _disposed = true;
                }

                base.Dispose(disposing);
            }

            private void TraceBytes(ReadOnlySpan<byte> bytes)
            {
                if (_remainingBytes <= 0)
                {
                    TraceTruncation();
                    return;
                }

                var bytesToTrace = Math.Min(bytes.Length, _remainingBytes);
                var text = Encoding.UTF8.GetString(bytes[..bytesToTrace]);
                _remainingBytes -= bytesToTrace;
                TraceText(text);
                if (bytesToTrace < bytes.Length)
                {
                    TraceTruncation();
                }
            }

            private void TraceText(string text)
            {
                foreach (var ch in text)
                {
                    switch (ch)
                    {
                        case '\r':
                            break;
                        case '\n':
                            log($"<<< sse: {_line}");
                            _line.Clear();
                            break;
                        default:
                            _line.Append(ch);
                            break;
                    }
                }
            }

            private void FlushPartialLine()
            {
                if (_line.Length > 0)
                {
                    log($"<<< sse: {_line}");
                    _line.Clear();
                }
            }

            private void TraceTruncation()
            {
                if (_truncated)
                {
                    return;
                }

                FlushPartialLine();
                log($"<<< sse: <truncated after {Math.Max(1024, maxBodyBytes)} bytes>");
                _truncated = true;
            }
        }
    }
}
