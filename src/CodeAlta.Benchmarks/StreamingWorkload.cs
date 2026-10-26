using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Benchmarks;

/// <summary>What a benchmark run asks of the fake provider: how much it streams, how fast, and which tools it calls.</summary>
internal sealed class StreamingWorkload
{
    private static readonly ImmutableArray<string> Words =
    [
        "the", "session", "streams", "text", "while", "a", "tool", "runs", "and", "its", "output", "is", "shown", "to",
        "every", "reader", "of", "runtime", "events", "with", "bounded", "memory", "per", "message", "so", "that", "many",
        "agents", "can", "work", "at", "once", "without", "slowing", "down", "user", "interface",
    ];

    private readonly ConcurrentDictionary<string, TaskCompletionSource> _finalSteps = new(StringComparer.OrdinalIgnoreCase);

    public int StepsPerTurn { get; init; } = 3;
    public int DeltasPerStep { get; init; } = 200;
    public int CharactersPerDelta { get; init; } = 6;
    public int ToolOutputLines { get; init; } = 50;
    // Deltas a session streams each second; 0 streams as fast as the runtime takes them.
    public int DeltasPerSecond { get; init; }
    // Code Mode: the steps that call a tool run a script instead, which calls the tool this many times at once, and goes
    // on in the background so that the scripts of a session run at the same time; 0 calls the tool directly.
    public int ScriptCalls { get; init; }

    public long DeltasStreamed;
    public long ToolCalls;
    public long ToolOutputLinesStreamed;

    public const string ToolName = "bench_tool";

    /// <summary>Returns the task that ends when the last model step of the current turn of a session returned.</summary>
    public Task ExpectFinalStep(string sessionId)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _finalSteps[sessionId] = completion;
        return completion.Task;
    }

    private void SignalFinalStep(string sessionId)
    {
        if (_finalSteps.TryRemove(sessionId, out var completion)) completion.TrySetResult();
    }

    public AgentToolDefinition CreateTool()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"step":{"type":"integer"}},"additionalProperties":false}""");
        return new AgentToolDefinition(
            new AgentToolSpec(ToolName, "Benchmark tool that streams lines of output.", schema.RootElement.Clone()) { RunsInParallel = true },
            async (invocation, cancellationToken) =>
            {
                Interlocked.Increment(ref ToolCalls);
                var output = new StringBuilder();
                for (var line = 0; line < ToolOutputLines; line++)
                {
                    var text = $"output line {line} of tool call {invocation.ToolCallId}\n";
                    output.Append(text);
                    if (invocation.Progress is { } progress) await progress(new AgentToolProgressUpdate(text), cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref ToolOutputLinesStreamed);
                }

                return new AgentToolResult(true, [new AgentToolResultItem.Text(output.ToString())]);
            });
    }

    public async Task<AgentTurnResponse> ExecuteTurnAsync(
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken)
    {
        var step = CountToolResultsOfLastTurn(request.Conversation);
        var contentId = "bench-" + Guid.CreateVersion7().ToString("N");
        var text = new StringBuilder(DeltasPerStep * (CharactersPerDelta + 1));
        var random = new Random(HashCode.Combine(request.SessionId, step));
        var perTick = DeltasPerSecond <= 0 ? int.MaxValue : Math.Max(1, DeltasPerSecond / 20);
        var sinceTick = 0;
        for (var index = 0; index < DeltasPerStep; index++)
        {
            if (DeltasPerSecond > 0 && sinceTick++ == perTick)
            {
                sinceTick = 1;
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            var delta = NextDelta(random, index, text.Length);
            text.Append(delta);
            await onUpdate(new AgentTurnDelta { Kind = AgentContentKind.Assistant, ContentId = contentId, Text = delta }, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref DeltasStreamed);
        }

        var parts = new List<AgentMessagePart> { new AgentMessagePart.Text(text.ToString()) };
        var ids = new List<string?> { contentId };
        var final = step >= StepsPerTurn - 1;
        if (!final)
        {
            using var arguments = ScriptCalls > 0
                ? JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["code"] = Script(step) }))
                : JsonDocument.Parse($$"""{"step":{{step}}}""");
            parts.Add(new AgentMessagePart.ToolCall("call-" + Guid.CreateVersion7().ToString("N"), ScriptCalls > 0 ? "exec" : ToolName, arguments.RootElement.Clone()));
            ids.Add(null);
        }

        var response = new AgentTurnResponse
        {
            AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, parts),
            AssistantPartContentIds = ids,
        };
        if (final) SignalFinalStep(request.SessionId);
        return response;
    }

    // A script that leaves its turn at once and calls the tool in parallel, with a little work of its own on the results.
    private string Script(int step)
        => $$"""
            // @exec: {"title": "bench {{step}}", "yield_ms": 1}
            const results = await Promise.all(Array.from({ length: {{ScriptCalls}} }, (_, i) => tools.{{ToolName}}({ step: i })));
            let lines = 0;
            for (const text of results) lines += text.split("\n").filter(line => line.length > 0).length;
            return { calls: results.length, lines };
            """;

    private string NextDelta(Random random, int index, int length)
    {
        // Mostly words, a code fence now and then, and line ends: what a model streams.
        if (index % 97 == 50) return "\n```csharp\n";
        if (index % 97 == 80) return "\n```\n";
        var builder = new StringBuilder(CharactersPerDelta + 8);
        while (builder.Length < CharactersPerDelta)
        {
            builder.Append(Words[random.Next(Words.Length)]);
            builder.Append(length % 80 < 6 ? '\n' : ' ');
        }

        return builder.ToString();
    }

    private static int CountToolResultsOfLastTurn(IReadOnlyList<AgentConversationMessage> conversation)
    {
        var count = 0;
        for (var index = conversation.Count - 1; index >= 0; index--)
        {
            var message = conversation[index];
            if (message.Role is AgentConversationRole.User && !message.Parts.Any(static part => part is AgentMessagePart.ToolResult)) break;
            count += message.Parts.Count(static part => part is AgentMessagePart.ToolResult);
        }

        return count;
    }
}

/// <summary>The provider of the benchmark: every session gets an executor that streams what the workload says.</summary>
internal sealed class BenchProviderRuntime(ModelProviderDescriptor descriptor, StreamingWorkload workload) : IAgentModelProviderRuntime
{
    public ModelProviderDescriptor Descriptor { get; } = descriptor;

    public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
    {
        ProtocolFamily = "bench",
        ProviderKey = descriptor.ProviderId.Value,
        DisplayName = descriptor.DisplayName,
        TransportKind = AgentTransportKind.OpenAIResponses,
    };

    public IModelProviderModelCatalog? ModelCatalog => null;

    public AgentRuntimeProviderRegistration CreateProviderRegistration() => new()
    {
        Provider = RuntimeDescriptor,
        TurnExecutor = new BenchTurnExecutor(workload),
    };

    public IModelProviderTurnExecutor CreateTurnExecutor() => new BenchTurnExecutor(workload);

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ModelProviderProbeResult
        {
            ProviderId = Descriptor.ProviderId,
            Availability = ModelProviderAvailability.Ready,
            Models = [new AgentModelInfo("bench-model", DisplayName: "Bench Model")],
            SelectedModelId = "bench-model",
        });

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class BenchTurnExecutor(StreamingWorkload workload) : IModelProviderTurnExecutor
    {
        public Task<AgentTurnResponse> ExecuteTurnAsync(
            AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
            => workload.ExecuteTurnAsync(request, onUpdate, cancellationToken);
    }
}

/// <summary>What the observing plugins of a run saw.</summary>
internal sealed class PluginCounters
{
    public long Events;
    public long Deltas;
    public long Idle;
}

/// <summary>A plugin that looks at events, as the statistics and search plugins of the desktop application do.</summary>
internal sealed class ObservingPlugin(PluginCounters counters) : PluginBase
{
    public override ValueTask OnAgentEventAsync(PluginAgentEventContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Interlocked.Increment(ref counters.Events);
        if (context.Event is AgentContentDeltaEvent) Interlocked.Increment(ref counters.Deltas);
        if (context.Event is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle }) Interlocked.Increment(ref counters.Idle);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A plugin that does not look at events, as most plugins of the desktop application.</summary>
internal sealed class QuietPlugin : PluginBase;

internal static class BenchPlugins
{
    public static IReadOnlyList<BuiltInPluginDefinition> Create(int quiet, int observing, PluginCounters counters)
    {
        var plugins = new List<BuiltInPluginDefinition>();
        for (var index = 0; index < observing; index++)
        {
            plugins.Add(new BuiltInPluginDefinition
            {
                Id = $"bench-observing-{index}",
                DisplayName = $"Bench observing {index}",
                Factory = () => new ObservingPlugin(counters),
                PluginType = typeof(ObservingPlugin),
            });
        }

        for (var index = 0; index < quiet; index++)
        {
            plugins.Add(new BuiltInPluginDefinition
            {
                Id = $"bench-quiet-{index}",
                DisplayName = $"Bench quiet {index}",
                Factory = static () => new QuietPlugin(),
                PluginType = typeof(QuietPlugin),
            });
        }

        return plugins;
    }
}
