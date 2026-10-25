using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using CodeAlta.Acp;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AcpAgentServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Initialize_AnswersWithTheAgentAndGivesItTheClientCapabilities()
    {
        await using var harness = await Harness.StartAsync();

        var initialize = harness.Client.InitializeResponse;
        Assert.AreEqual("test-agent", initialize.AgentInfo?.Name);
        Assert.AreEqual(1, initialize.ProtocolVersion.Value.GetInt32());
        Assert.IsTrue(initialize.AgentCapabilities.LoadSession);
        Assert.IsTrue(initialize.AgentCapabilities.PromptCapabilities.Image);
        Assert.AreEqual("test-client", harness.Agent.Connection.ClientInfo?.Name);
        Assert.IsTrue(harness.Agent.Connection.ClientCapabilities?.Terminal);
    }

    [TestMethod]
    public async Task NewSession_AnswersWithTheSessionAndItsConfigOptions()
    {
        await using var harness = await Harness.StartAsync();

        var session = await harness.Client.SessionNewAsync(new NewSessionRequest { Cwd = "/work" }).WaitAsync(Timeout);

        Assert.AreEqual("session-1", session.SessionId.Value);
        Assert.AreEqual("/work", harness.Agent.LastCwd);
        var option = session.ConfigOptions!.Single();
        Assert.AreEqual("model", option.Id);
        Assert.AreEqual("model", option.Category);
        Assert.AreEqual("fast", option.CurrentValue);
    }

    [TestMethod]
    public async Task Prompt_SendsEveryUpdateBeforeItsResponse()
    {
        await using var harness = await Harness.StartAsync();
        harness.Agent.OnPrompt = async (request, connection, cancellationToken) =>
        {
            await connection.SessionUpdateAsync(request.SessionId, AcpSessionUpdates.AgentThoughtChunk("thinking"), cancellationToken);
            await connection.SessionUpdateAsync(request.SessionId, AcpSessionUpdates.ToolCall(new ToolCall
            {
                ToolCallId = "call-1",
                Title = "Read file",
                Kind = ToolKind.Read,
                Status = ToolCallStatus.InProgress,
            }), cancellationToken);
            await connection.SessionUpdateAsync(request.SessionId, AcpSessionUpdates.ToolCallUpdate(new ToolCallUpdate
            {
                ToolCallId = "call-1",
                Status = ToolCallStatus.Completed,
                Content = [ToolCallContent.FromText("contents")],
            }), cancellationToken);
            for (var index = 0; index < 20; index++)
            {
                await connection.SessionUpdateAsync(request.SessionId, AcpSessionUpdates.AgentMessageChunk($"part {index} "), cancellationToken);
            }

            return new PromptResponse
            {
                StopReason = StopReason.EndTurn,
                Usage = new Usage { InputTokens = 10, OutputTokens = 5, TotalTokens = 15 },
            };
        };

        var response = await harness.Client.SessionPromptAsync(new PromptRequest
        {
            SessionId = "session-1",
            Prompt = [ContentBlock.FromText("Hello")],
        }).WaitAsync(Timeout);

        Assert.AreEqual("end_turn", response.StopReason.Value.GetString());
        Assert.AreEqual(15UL, response.Usage?.TotalTokens);
        var updates = harness.Messages.ToArray();
        Assert.HasCount(23, updates);
        Assert.IsTrue(updates.All(static message => message.Method == "session/update" && message.RequestId is null));
        Assert.AreEqual("session-1", AcpJson.GetString(updates[0].Params, "sessionId"));
        string?[] kinds = [.. updates.Select(static message => message.Params.GetProperty("update").GetProperty("sessionUpdate").GetString())];
        Assert.AreEqual("agent_thought_chunk", kinds[0]);
        Assert.AreEqual("tool_call", kinds[1]);
        Assert.AreEqual("tool_call_update", kinds[2]);
        Assert.AreEqual("part 19 ", updates[^1].Params.GetProperty("update").GetProperty("content").GetProperty("text").GetString());
        Assert.AreEqual("Hello", ((AcpContentPart.Text)harness.Agent.LastPrompt!.Single()).Value);
    }

    [TestMethod]
    public async Task SessionCancel_EndsThePromptWithCancelled()
    {
        await using var harness = await Harness.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Agent.OnPrompt = async (request, connection, cancellationToken) =>
        {
            using var turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            harness.Agent.Turns[request.SessionId.Value] = turn;
            started.SetResult();
            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, turn.Token);
                return new PromptResponse { StopReason = StopReason.EndTurn };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new PromptResponse { StopReason = StopReason.Cancelled };
            }
        };

        var prompt = harness.Client.SessionPromptAsync(new PromptRequest { SessionId = "session-1", Prompt = [ContentBlock.FromText("Wait")] });
        await started.Task.WaitAsync(Timeout);
        await harness.Client.CancelSessionAsync("session-1");

        var response = await prompt.WaitAsync(Timeout);
        Assert.AreEqual("cancelled", response.StopReason.Value.GetString());
    }

    [TestMethod]
    public async Task CancelRequest_CancelsThePendingRequestOfTheAgent()
    {
        await using var harness = await Harness.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Agent.OnList = async cancellationToken =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }

            return new ListSessionsResponse();
        };

        using var cancellation = new CancellationTokenSource();
        var list = harness.Client.SessionListAsync(new ListSessionsRequest(), cancellation.Token);
        await started.Task.WaitAsync(Timeout);
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => list);
        await cancelled.Task.WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task CancelRequest_AnswersTheCancelledRequestWithRequestCancelled()
    {
        await using var raw = new RawHarness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        raw.Agent.OnList = async cancellationToken =>
        {
            started.SetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            return new ListSessionsResponse();
        };

        await raw.WriteAsync("""{"jsonrpc":"2.0","id":"list-1","method":"session/list","params":{}}""");
        await started.Task.WaitAsync(Timeout);
        await raw.WriteAsync("""{"jsonrpc":"2.0","method":"$/cancel_request","params":{"requestId":"list-1"}}""");

        var response = await raw.ReadAsync();
        Assert.AreEqual("list-1", response.GetProperty("id").GetString());
        Assert.AreEqual(AcpErrorCodes.RequestCancelled, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [TestMethod]
    public async Task UnknownMethod_AnswersMethodNotFound_AndUnknownNotificationIsIgnored()
    {
        await using var harness = await Harness.StartAsync();

        await harness.Client.SendNotificationAsync("session/unknown_notification", new ListSessionsRequest());
        var unknown = await Assert.ThrowsAsync<AcpJsonRpcException>(
            () => harness.Client.SendRequestAsync<ListSessionsRequest, ListSessionsResponse>("session/unknown", new ListSessionsRequest()));
        var unsupported = await Assert.ThrowsAsync<AcpJsonRpcException>(
            () => harness.Client.SessionForkAsync(new ForkSessionRequest { SessionId = "session-1", Cwd = "/work" }));

        Assert.AreEqual(AcpErrorCodes.MethodNotFound, unknown.Code);
        Assert.AreEqual(AcpErrorCodes.MethodNotFound, unsupported.Code);
        StringAssert.Contains(unsupported.Message, "session/fork");
    }

    [TestMethod]
    public async Task ThrowingAgent_AnswersInternalError()
    {
        await using var harness = await Harness.StartAsync();
        harness.Agent.NewSessionError = new InvalidOperationException("The session store is broken.");

        var error = await Assert.ThrowsAsync<AcpJsonRpcException>(() => harness.Client.SessionNewAsync(new NewSessionRequest { Cwd = "/work" }));

        Assert.AreEqual(AcpErrorCodes.InternalError, error.Code);
        Assert.AreEqual("The session store is broken.", error.Message);

        // The server goes on serving.
        harness.Agent.NewSessionError = null;
        var session = await harness.Client.SessionNewAsync(new NewSessionRequest { Cwd = "/work" }).WaitAsync(Timeout);
        Assert.AreEqual("session-2", session.SessionId.Value);
    }

    [TestMethod]
    public async Task AuthRequired_SurfacesOnTheClientWithItsData()
    {
        await using var harness = await Harness.StartAsync();
        harness.Agent.NewSessionError = AcpJsonRpcException.WithData(
            AcpErrorCodes.AuthRequired,
            "Authentication required",
            AcpJson.Parse("""{"provider":"anthropic"}"""));

        var error = await Assert.ThrowsAsync<AcpJsonRpcException>(() => harness.Client.SessionNewAsync(new NewSessionRequest { Cwd = "/work" }));

        Assert.AreEqual(AcpErrorCodes.AuthRequired, error.Code);
        Assert.AreEqual("Authentication required", error.Message);
        Assert.AreEqual("anthropic", AcpJson.GetString(error.ErrorData!.Value, "provider"));
    }

    [TestMethod]
    public async Task InvalidParams_AnswersInvalidParams()
    {
        await using var raw = new RawHarness();

        await raw.WriteAsync("""{"jsonrpc":"2.0","id":3,"method":"session/new","params":{"cwd":42}}""");

        var response = await raw.ReadAsync();
        Assert.AreEqual(3, response.GetProperty("id").GetInt32());
        Assert.AreEqual(AcpErrorCodes.InvalidParams, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [TestMethod]
    public async Task MessagesWrittenBeforeTheServerStarts_AreAnswered()
    {
        var toServer = new Pipe();
        var fromServer = new Pipe();
        await toServer.Writer.WriteAsync(Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{}}}""" + "\n"));

        await using var raw = new RawHarness(toServer, fromServer);

        var response = await raw.ReadAsync();
        Assert.AreEqual(1, response.GetProperty("id").GetInt32());
        Assert.AreEqual("test-agent", response.GetProperty("result").GetProperty("agentInfo").GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task EndOfInput_CancelsTheRunningRequestsAndCompletes()
    {
        await using var raw = new RawHarness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        raw.Agent.OnList = async cancellationToken =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }

            return new ListSessionsResponse();
        };

        await raw.WriteAsync("""{"jsonrpc":"2.0","id":1,"method":"session/list"}""");
        await started.Task.WaitAsync(Timeout);
        await raw.CompleteInputAsync();

        await raw.Server.Completion.WaitAsync(Timeout);
        await cancelled.Task.WaitAsync(Timeout);
    }

    private static InitializeRequest CreateInitializeRequest()
        => new()
        {
            ClientInfo = new Implementation { Name = "test-client", Version = "1.0.0" },
            ClientCapabilities = new ClientCapabilities { Terminal = true },
            ProtocolVersion = ProtocolVersion.From(1),
        };

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Pipe _toServer;

        private Harness(Pipe toServer, AcpAgentServer server, TestAgent agent, AcpClient client, ConcurrentQueue<AcpServerMessage> messages)
        {
            _toServer = toServer;
            Server = server;
            Agent = agent;
            Client = client;
            Messages = messages;
        }

        public AcpAgentServer Server { get; }

        public TestAgent Agent { get; }

        public AcpClient Client { get; }

        public ConcurrentQueue<AcpServerMessage> Messages { get; }

        public static async Task<Harness> StartAsync()
        {
            var toServer = new Pipe();
            var fromServer = new Pipe();
            TestAgent? agent = null;
            var server = new AcpAgentServer(toServer.Reader.AsStream(), fromServer.Writer.AsStream(), connection => agent = new TestAgent(connection));
            var messages = new ConcurrentQueue<AcpServerMessage>();
            var client = await AcpClient.ConnectAsync(fromServer.Reader.AsStream(), toServer.Writer.AsStream(), CreateInitializeRequest(), messages.Enqueue)
                .WaitAsync(Timeout);
            return new Harness(toServer, server, agent!, client, messages);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _toServer.Writer.CompleteAsync();
            await Server.Completion.WaitAsync(Timeout);
            await Server.DisposeAsync();
        }
    }

    // Writes and reads the lines of the protocol, for what the client does not show.
    private sealed class RawHarness : IAsyncDisposable
    {
        private readonly Pipe _toServer;
        private readonly StreamReader _reader;

        public RawHarness()
            : this(new Pipe(), new Pipe())
        {
        }

        public RawHarness(Pipe toServer, Pipe fromServer)
        {
            _toServer = toServer;
            _reader = new StreamReader(fromServer.Reader.AsStream(), Encoding.UTF8);
            TestAgent? agent = null;
            Server = new AcpAgentServer(toServer.Reader.AsStream(), fromServer.Writer.AsStream(), connection => agent = new TestAgent(connection));
            Agent = agent!;
        }

        public AcpAgentServer Server { get; }

        public TestAgent Agent { get; }

        public async Task WriteAsync(string line)
            => await _toServer.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));

        public async Task<JsonElement> ReadAsync()
        {
            var line = await _reader.ReadLineAsync().WaitAsync(Timeout);
            Assert.IsNotNull(line);
            return AcpJson.Parse(line);
        }

        public async Task CompleteInputAsync() => await _toServer.Writer.CompleteAsync();

        public async ValueTask DisposeAsync()
        {
            await _toServer.Writer.CompleteAsync();
            await Server.DisposeAsync();
        }
    }

    private sealed class TestAgent(IAcpClientConnection connection) : AcpAgentBase
    {
        private int _sessions;

        public IAcpClientConnection Connection { get; } = connection;

        public ConcurrentDictionary<string, CancellationTokenSource> Turns { get; } = new(StringComparer.Ordinal);

        public Func<PromptRequest, IAcpClientConnection, CancellationToken, Task<PromptResponse>> OnPrompt { get; set; } =
            static (_, _, _) => Task.FromResult(new PromptResponse { StopReason = StopReason.EndTurn });

        public Func<CancellationToken, Task<ListSessionsResponse>> OnList { get; set; } = static _ => Task.FromResult(new ListSessionsResponse());

        public Exception? NewSessionError { get; set; }

        public string? LastCwd { get; private set; }

        public IReadOnlyList<AcpContentPart>? LastPrompt { get; private set; }

        public override Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = ProtocolVersion.From(1),
                AgentInfo = new Implementation { Name = "test-agent", Version = "1.0.0" },
                AgentCapabilities = new AgentCapabilities
                {
                    LoadSession = true,
                    PromptCapabilities = new PromptCapabilities { Image = true, EmbeddedContext = true },
                    SessionCapabilities = new SessionCapabilities { List = new SessionListCapabilities() },
                },
            });

        public override Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
        {
            var id = $"session-{Interlocked.Increment(ref _sessions)}";
            if (NewSessionError is { } error)
            {
                return Task.FromException<NewSessionResponse>(error);
            }

            LastCwd = request.Cwd;
            return Task.FromResult(new NewSessionResponse
            {
                SessionId = id,
                ConfigOptions =
                [
                    SessionConfigOption.Select("model", "Model", "model", "fast",
                    [
                        new SessionConfigSelectOption { Value = "fast", Name = "Fast" },
                        new SessionConfigSelectOption { Value = "smart", Name = "Smart" },
                    ]),
                ],
            });
        }

        public override Task<ListSessionsResponse> ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken)
            => OnList(cancellationToken);

        public override Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken)
        {
            LastPrompt = AcpContentPart.ReadAll(request.Prompt);
            return OnPrompt(request, Connection, cancellationToken);
        }

        public override Task CancelAsync(CancelNotification notification, CancellationToken cancellationToken)
        {
            if (Turns.TryGetValue(notification.SessionId.Value, out var turn))
            {
                turn.Cancel();
            }

            return Task.CompletedTask;
        }
    }
}
