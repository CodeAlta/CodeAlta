using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.NativeEngines;

namespace CodeAlta.Sandbox.Worker;

/// <summary>
/// One message between CodeAlta and the sandbox worker of a session. <see cref="Type"/> says what it is; the other
/// fields are those of that type. Tool calls, results, permission requests and decisions are JSON of the agent
/// contracts, as the journal writes them.
/// </summary>
internal sealed record SandboxWorkerMessage
{
    public required string Type { get; init; }

    public long Id { get; init; }

    // init
    public string? SessionId { get; init; }

    public string? ProviderId { get; init; }

    public string? WorkingDirectory { get; init; }

    public List<string>? WritableRoots { get; init; }

    public string? Scratchpad { get; init; }

    public string? Home { get; init; }

    public bool CommandGuard { get; init; }

    // invoke
    public string? Tool { get; init; }

    public string? ToolCallId { get; init; }

    public JsonElement? Arguments { get; init; }

    // result, permission, decision
    public JsonElement? Payload { get; init; }

    // progress, process output
    public string? Text { get; init; }

    public JsonElement? Details { get; init; }

    // guard
    public string? Command { get; init; }

    public string? Folder { get; init; }

    public string? Refusal { get; init; }

    // process
    public int? ProcessId { get; init; }

    public int? ExitCode { get; init; }

    public string? Error { get; init; }
}

/// <summary>The types of <see cref="SandboxWorkerMessage"/>.</summary>
internal static class SandboxWorkerMessageTypes
{
    // CodeAlta to the worker.
    public const string Init = "init";
    public const string Invoke = "invoke";
    public const string Cancel = "cancel";
    public const string Decision = "decision";
    public const string GuardAnswer = "guard_answer";
    public const string Start = "start";
    public const string Kill = "kill";

    // The worker to CodeAlta.
    public const string Ready = "ready";
    public const string Result = "result";
    public const string Progress = "progress";
    public const string Permission = "permission";
    public const string Guard = "guard";
    public const string Started = "started";
    public const string Output = "output";
    public const string Exited = "exited";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SandboxWorkerMessage))]
internal sealed partial class SandboxWorkerJsonContext : JsonSerializerContext;

/// <summary>
/// The messages on the standard streams of the worker, one JSON object per message, in the frames of the native
/// workers. A message longer than a frame is sent in parts: every part but the last has the kind <see cref="PartKind"/>.
/// </summary>
internal sealed class SandboxWorkerChannel
{
    private const byte MessageKind = 1;
    private const byte PartKind = 2;
    private const int MaximumMessage = 256 * 1024 * 1024;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writing = new(1, 1);

    public SandboxWorkerChannel(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    public async Task WriteAsync(SandboxWorkerMessage message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, SandboxWorkerJsonContext.Default.SandboxWorkerMessage);
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The frames of one message are written together: another writer waits for its end.
            await Task.Run(() =>
            {
                var offset = 0;
                while (bytes.Length - offset > WorkerFrames.MaximumContent)
                {
                    WorkerFrames.Write(_output, PartKind, 0, bytes.AsSpan(offset, WorkerFrames.MaximumContent));
                    offset += WorkerFrames.MaximumContent;
                }

                WorkerFrames.Write(_output, MessageKind, 0, bytes.AsSpan(offset));
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }

    // Read by one reader only. Null at the end of the stream.
    public async Task<SandboxWorkerMessage?> ReadAsync()
    {
        using var buffer = new MemoryStream();
        while (true)
        {
            var frame = await Task.Run(() => WorkerFrames.Read(_input)).ConfigureAwait(false);
            if (frame is not { } value) return null;
            if (buffer.Length + value.Content.Length > MaximumMessage) throw new InvalidDataException("A message of the sandbox worker is too long.");
            buffer.Write(value.Content);
            if (value.Kind == PartKind) continue;
            if (value.Kind != MessageKind) throw new InvalidDataException($"The sandbox worker sent a frame of kind {value.Kind}.");
            return JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), SandboxWorkerJsonContext.Default.SandboxWorkerMessage)
                ?? throw new InvalidDataException("The sandbox worker sent an empty message.");
        }
    }
}
