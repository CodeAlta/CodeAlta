using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Telemetry;
using CodeAlta.Hosting;
using OpenTelemetry.Exporter;

namespace CodeAlta.Tests;

/// <summary>The spans of a run, in the OpenTelemetry conventions of generative AI, made from its events.</summary>
public sealed partial class AgentSessionTests
{
    [TestMethod]
    public async Task AgentSession_TracesItsRun_ItsRequestsAndItsToolCalls_WithoutTheirContent()
    {
        using var temp = TestTempDirectory.Create();
        var sessionId = "session-telemetry-" + Guid.NewGuid().ToString("N");
        using var spans = new SpanRecorder(sessionId);

        await RunTracedSessionAsync(temp.Path, sessionId);

        var run = spans.Single("invoke_agent");
        Assert.AreEqual("invoke_agent CodeAlta", run.DisplayName);
        Assert.AreEqual(ActivityKind.Internal, run.Kind);
        Assert.AreEqual("openai", run.GetTagItem("gen_ai.provider.name"));
        Assert.AreEqual("CodeAlta", run.GetTagItem("gen_ai.agent.name"));
        Assert.AreEqual("gpt-5.4", run.GetTagItem("gen_ai.request.model"));
        Assert.AreEqual(sessionId, run.GetTagItem("gen_ai.conversation.id"));
        Assert.AreEqual(150L, run.GetTagItem("gen_ai.usage.input_tokens"), "the usage of the run is the sum of its requests");
        Assert.AreEqual(30L, run.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.AreEqual(40L, run.GetTagItem("gen_ai.usage.cache_read.input_tokens"));
        Assert.AreEqual(ActivityStatusCode.Unset, run.Status);

        var requests = spans.All("chat");
        Assert.HasCount(2, requests, "one span for each request whose usage the provider reports");
        foreach (var request in requests)
        {
            Assert.AreEqual("chat gpt-5.4", request.DisplayName);
            Assert.AreEqual(ActivityKind.Client, request.Kind);
            Assert.AreEqual(run.SpanId, request.ParentSpanId);
            Assert.AreEqual("gpt-5.4-2026-10-01", request.GetTagItem("gen_ai.response.model"));
        }

        Assert.AreEqual(100L, requests[0].GetTagItem("gen_ai.usage.input_tokens"));
        Assert.AreEqual(40L, requests[0].GetTagItem("gen_ai.usage.cache_read.input_tokens"));
        Assert.AreEqual(5L, requests[0].GetTagItem("gen_ai.usage.reasoning.output_tokens"));
        Assert.IsNull(requests[1].GetTagItem("gen_ai.usage.cache_read.input_tokens"), "a count of zero is left out");

        var tools = spans.All("execute_tool");
        Assert.HasCount(2, tools);
        var inspect = tools.Single(static tool => (string?)tool.GetTagItem("gen_ai.tool.name") == "inspect");
        var broken = tools.Single(static tool => (string?)tool.GetTagItem("gen_ai.tool.name") == "broken");
        Assert.AreEqual("execute_tool inspect", inspect.DisplayName);
        Assert.AreEqual("call-inspect", inspect.GetTagItem("gen_ai.tool.call.id"));
        Assert.AreEqual(run.SpanId, inspect.ParentSpanId);
        Assert.AreEqual(ActivityStatusCode.Unset, inspect.Status);
        Assert.AreEqual(ActivityStatusCode.Error, broken.Status);
        Assert.AreEqual("tool_error", broken.GetTagItem("error.type"));
        Assert.IsNull(broken.StatusDescription, "the message of the failure is made of the content of the tool");
        Assert.IsTrue(requests[1].StartTimeUtc >= broken.StartTimeUtc + broken.Duration, "the second request starts once the tool calls ended");

        // Neither the prompt, the answer, the arguments nor the results.
        foreach (var span in spans.All(null))
        {
            foreach (var (name, value) in span.TagObjects)
            {
                Assert.IsFalse(value?.ToString()?.Contains("secret", StringComparison.Ordinal) == true, $"{span.DisplayName} records content in {name}");
            }
        }

        Assert.IsNull(Activity.Current, "the spans of a run never become the current activity of its caller");
    }

    [TestMethod]
    public async Task AgentSession_TracesTheFailureOfARun()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-telemetry-failure-" + Guid.NewGuid().ToString("N")) with { WorkingDirectory = temp.Path };
        var state = CreateState(summary.SessionId);
        await store.UpsertSessionAsync(summary);
        await store.UpsertStateAsync(state);
        using var spans = new SpanRecorder(summary.SessionId);
        await using var session = new AgentSession(ModelProviderIds.OpenAIResponses, provider, summary, state, [], store,
            new ScriptedTurnExecutor((_, _, _) => throw new TimeoutException("secret detail")), CreateOptions(provider, temp.Path));

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => session.SendAsync(new() { Input = AgentInput.Text("Work") }));

        var run = spans.Single("invoke_agent");
        Assert.AreEqual(ActivityStatusCode.Error, run.Status);
        Assert.AreEqual("Unavailable", run.GetTagItem("error.type"), "the kind of the failure of the provider, not its message");
        Assert.IsEmpty(spans.All("chat"), "no usage, no request span");
    }

    [TestMethod]
    public async Task AgentSession_SendsItsSpansToTheOtlpEndpoint_ThroughTheTelemetryOfTheHost()
    {
        using var temp = TestTempDirectory.Create();
        var sessionId = "session-otlp-" + Guid.NewGuid().ToString("N");
        using var collector = new OtlpHttpCollector();
        var environment = new Dictionary<string, string>
        {
            [OpenTelemetryEnvironment.EndpointVariable] = collector.Endpoint,
        };

        using (var telemetry = CodeAltaTelemetry.StartFromEnvironment("altacli", name => environment.GetValueOrDefault(name), options =>
               {
                   options.Endpoint = new Uri(collector.Endpoint + "v1/traces");
                   options.Protocol = OtlpExportProtocol.HttpProtobuf;
               }))
        {
            Assert.IsNotNull(telemetry);
            await RunTracedSessionAsync(temp.Path, sessionId);
        }

        // Disposed at the end of the application, it sent what it held: nothing is lost by a short run.
        var received = collector.Received;
        Assert.AreEqual("/v1/traces", collector.Path);
        foreach (var expected in new[] { "invoke_agent CodeAlta", "chat gpt-5.4", "execute_tool inspect", "execute_tool broken", sessionId, "altacli" })
        {
            StringAssert.Contains(received, expected);
        }

        Assert.IsFalse(received.Contains("secret", StringComparison.Ordinal));
    }

    // A run of two requests: the first calls a tool that works and one that fails, the second answers.
    private static async Task RunTracedSessionAsync(string root, string sessionId)
    {
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(root, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary(sessionId) with { WorkingDirectory = root };
        var state = CreateState(sessionId);
        await store.UpsertSessionAsync(summary);
        await store.UpsertStateAsync(state);
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var arguments = JsonDocument.Parse("""{"path":"secret.txt"}""").RootElement.Clone();
        static AgentSessionUsage Usage(long input, long output, long cached, long reasoning) => new(
            LastOperation: new AgentOperationUsageSnapshot(Model: "gpt-5.4-2026-10-01", InputTokens: input, OutputTokens: output,
                CachedInputTokens: cached, ReasoningTokens: reasoning),
            Scope: AgentUsageScope.LastOperation,
            Source: AgentUsageSource.ProviderUsage,
            UpdatedAt: DateTimeOffset.UtcNow);
        var options = new AgentSessionCreateOptions
        {
            ProviderKey = provider.ProviderKey,
            Model = "gpt-5.4",
            WorkingDirectory = root,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            Tools =
            [
                new AgentToolDefinition(new AgentToolSpec("inspect", "Inspect", schema.RootElement.Clone()),
                    (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("secret content")]))),
                new AgentToolDefinition(new AgentToolSpec("broken", "Fail", schema.RootElement.Clone()),
                    (_, _) => Task.FromResult(new AgentToolResult(false, [new AgentToolResultItem.Text("secret failure")], "secret failure"))),
            ],
        };
        await using var session = new AgentSession(ModelProviderIds.OpenAIResponses, provider, summary, state, [], store,
            new ScriptedTurnExecutor(
                (_, _, _) => Task.FromResult(new AgentTurnResponse
                {
                    AssistantMessage = new(AgentConversationRole.Assistant,
                        [new AgentMessagePart.ToolCall("call-inspect", "inspect", arguments), new AgentMessagePart.ToolCall("call-broken", "broken", arguments)]),
                    Usage = Usage(100, 20, 40, 5),
                }),
                (_, _, _) => Task.FromResult(new AgentTurnResponse
                {
                    AssistantMessage = new(AgentConversationRole.Assistant, [new AgentMessagePart.Text("The secret answer.")]),
                    Usage = Usage(50, 10, 0, 0),
                })),
            options);

        await session.SendAsync(new() { Input = AgentInput.Text("Read the secret file.") });
    }

    // Records the spans of CodeAlta for one session, as a tracer provider would receive them.
    private sealed class SpanRecorder : IDisposable
    {
        private readonly ConcurrentQueue<Activity> _spans = new();
        private readonly ActivityListener _listener;

        public SpanRecorder(string sessionId)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == AgentTelemetry.SourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if ((string?)activity.GetTagItem("gen_ai.conversation.id") == sessionId) _spans.Enqueue(activity);
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public List<Activity> All(string? operation)
            => [.. _spans.Where(span => operation is null || (string?)span.GetTagItem("gen_ai.operation.name") == operation).OrderBy(static span => span.StartTimeUtc)];

        public Activity Single(string operation) => All(operation).Single();

        public void Dispose() => _listener.Dispose();
    }

    // An OTLP/HTTP collector of one request at a time, which keeps the text of what it received.
    private sealed class OtlpHttpCollector : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly StringBuilder _received = new();
        private readonly Task _serving;

        public OtlpHttpCollector()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Endpoint = $"http://localhost:{port}/";
            _listener.Prefixes.Add(Endpoint);
            _listener.Start();
            _serving = ServeAsync();
        }

        public string Endpoint { get; }

        public string? Path { get; private set; }

        public string Received
        {
            get
            {
                lock (_received) return _received.ToString();
            }
        }

        public void Dispose()
        {
            _listener.Close();
            try
            {
                _serving.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                using var body = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(body);
                lock (_received)
                {
                    Path = context.Request.Url?.AbsolutePath;
                    // The strings of the protobuf message are UTF-8 in its bytes.
                    _received.Append(Encoding.UTF8.GetString(body.ToArray()));
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/x-protobuf";
                context.Response.Close();
            }
        }
    }
}
