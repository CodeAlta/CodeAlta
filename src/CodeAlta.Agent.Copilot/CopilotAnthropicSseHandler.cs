using System.Net;
using System.Text;

namespace CodeAlta.Agent.Copilot;

internal sealed class CopilotAnthropicSseHandler(CopilotRequestUsage? usage = null) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var content = response.Content;
        if (content is not null && string.Equals(
                content.Headers.ContentType?.MediaType,
                "text/event-stream",
                StringComparison.OrdinalIgnoreCase))
        {
            // Copilot can append the OpenAI-style `data: [DONE]` sentinel to an otherwise
            // Anthropic-shaped stream. The Anthropic SDK treats it as JSON and fails parsing.
            response.Content = new CopilotAnthropicSseContent(content, usage);
        }

        return response;
    }

    private sealed class CopilotAnthropicSseContent : HttpContent
    {
        private readonly HttpContent _innerContent;
        private readonly CopilotRequestUsage? _usage;

        public CopilotAnthropicSseContent(HttpContent innerContent, CopilotRequestUsage? usage)
        {
            _innerContent = innerContent;
            _usage = usage;
            foreach (var header in innerContent.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            Headers.ContentLength = null;
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            await using var filteredStream = await CreateContentReadStreamAsync().ConfigureAwait(false);
            await filteredStream.CopyToAsync(stream).ConfigureAwait(false);
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            await using var filteredStream = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);
            await filteredStream.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        protected override async Task<Stream> CreateContentReadStreamAsync()
            => new CopilotAnthropicSseStream(
                await _innerContent.ReadAsStreamAsync().ConfigureAwait(false), _usage);

        protected override async Task<Stream> CreateContentReadStreamAsync(
            CancellationToken cancellationToken)
            => new CopilotAnthropicSseStream(
                await _innerContent.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _usage);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _innerContent.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CopilotAnthropicSseStream : Stream
    {
        private readonly StreamReader _reader;
        private readonly CopilotRequestUsage? _usage;
        private byte[] _pendingBytes = [];
        private int _pendingOffset;
        private bool _endOfStream;

        public CopilotAnthropicSseStream(Stream stream, CopilotRequestUsage? usage)
        {
            _usage = usage;
            _reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            while (_pendingOffset >= _pendingBytes.Length)
            {
                if (_endOfStream || !await ReadNextEventAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }
            }

            var count = Math.Min(buffer.Length, _pendingBytes.Length - _pendingOffset);
            _pendingBytes.AsMemory(_pendingOffset, count).CopyTo(buffer);
            _pendingOffset += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _reader.Dispose();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }

        private async Task<bool> ReadNextEventAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var lines = new List<string>();
                while (true)
                {
                    var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        _endOfStream = true;
                        break;
                    }

                    if (line.Length == 0)
                    {
                        break;
                    }

                    // Copilot reports what it bills beside the usage of the protocol, in the same event.
                    _usage?.ObserveLine(line);
                    lines.Add(line);
                }

                if (lines.Count == 0)
                {
                    if (_endOfStream)
                    {
                        return false;
                    }

                    continue;
                }

                if (IsDoneEvent(lines))
                {
                    if (_endOfStream)
                    {
                        return false;
                    }

                    continue;
                }

                _pendingBytes = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n\n");
                _pendingOffset = 0;
                return true;
            }
        }

        private static bool IsDoneEvent(IReadOnlyList<string> lines)
        {
            string? data = null;
            foreach (var line in lines)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                if (data is not null)
                {
                    return false;
                }

                data = GetFieldValue(line, 5);
            }

            return data == "[DONE]";
        }

        private static string GetFieldValue(string line, int valueOffset)
        {
            var value = line.AsSpan(valueOffset);
            if (!value.IsEmpty && value[0] == ' ')
            {
                value = value[1..];
            }

            return value.ToString();
        }
    }
}
