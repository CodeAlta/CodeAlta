#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using CodeAlta.Agent.LocalRuntime;

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
        LocalAgentTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (options?.Enabled != true || string.IsNullOrWhiteSpace(options.StateRootPath))
        {
            return null;
        }

        var layout = new LocalAgentRuntimePathLayout(options.StateRootPath);
        var logger = new OpenAIProtocolTraceLogger(
            layout.GetSessionTraceFilePath(request.SessionId),
            options.MaxBodyBytes);
        logger.WriteLine(
            $"### turn start provider={request.Provider.ProviderKey} backend={request.BackendId.Value} session={request.SessionId} run={request.RunId.Value} model={FormatValue(request.ModelId)}");
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

        var layout = new LocalAgentRuntimePathLayout(options.StateRootPath);
        var logger = new OpenAIProtocolTraceLogger(
            layout.GetSessionTraceFilePath(context.SessionId),
            options.MaxBodyBytes);
        logger.WriteLine(
            $"### turn start provider={context.Provider.ProviderKey} backend={context.Provider.BackendId.Value} session={context.SessionId} run={context.RunId.Value} model={FormatValue(context.ModelId)}");
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
                log("<<< body: <not buffered; streaming body is traced as SDK stream updates when available>");
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
    }
}
