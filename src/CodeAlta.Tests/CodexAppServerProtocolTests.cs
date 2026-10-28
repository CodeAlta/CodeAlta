using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.CodexAppServer;
using CodeAlta.CodexAppServer.Protocol;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodexAppServerProtocolTests
{
    [TestMethod]
    public void Notification_WithAnItemKindThisVersionDoesNotKnow_ReadsAsTheBaseType()
    {
        var parameters = Parse("""{"item":{"type":"holographicCall","id":"i1","glow":true},"threadId":"t","turnId":"u","completedAtMs":5}""");

        var notification = (ItemCompletedNotification)CodexMethods.ParseNotification(CodexMethods.ItemCompleted, parameters)!;

        Assert.AreEqual(typeof(ThreadItem), notification.Item.GetType());
        Assert.AreEqual("u", notification.TurnId);
    }

    [TestMethod]
    public void Notification_WithAnEnumValueThisVersionDoesNotKnow_ReadsAsUnrecognized()
    {
        var parameters = Parse("""
            {"threadId":"t","turnId":"u","startedAtMs":1,"item":{"id":"c1","command":"ls","commandActions":[],"cwd":"/w","source":"agent","status":"paused","type":"commandExecution"}}
            """);

        var notification = (ItemStartedNotification)CodexMethods.ParseNotification(CodexMethods.ItemStarted, parameters)!;

        // The kind comes after the other properties: the server may write them in any order.
        var command = (ThreadItem.CommandExecutionThreadItem)notification.Item;
        Assert.AreEqual(CommandExecutionStatus.UnrecognizedValue, command.Status);
        Assert.AreEqual(CommandExecutionSource.Agent, command.Source);
        Assert.AreEqual("ls", command.Command);
    }

    [TestMethod]
    public void Notification_OfAMethodCodeAltaDoesNotUse_IsNull()
        => Assert.IsNull(CodexMethods.ParseNotification("thread/realtime/started", Parse("{}")));

    [TestMethod]
    public void ThreadStartParams_WritesTheConfigAndLeavesOutWhatIsNotSet()
    {
        var parameters = new ThreadStartParams
        {
            Cwd = "/work",
            ApprovalPolicy = new AskForApproval.Never(),
            Sandbox = SandboxMode.DangerFullAccess,
            DeveloperInstructions = "# CodeAlta",
            Config = new Dictionary<string, JsonElement> { ["web_search"] = JsonSerializer.SerializeToElement("live", CodexProtocolJsonContext.Default.String) },
        };

        var json = JsonNode.Parse(JsonSerializer.Serialize(parameters, CodexProtocolJsonContext.Default.ThreadStartParams))!.AsObject();

        Assert.AreEqual("never", json["approvalPolicy"]!.GetValue<string>());
        Assert.AreEqual("danger-full-access", json["sandbox"]!.GetValue<string>());
        Assert.AreEqual("live", json["config"]!["web_search"]!.GetValue<string>());
        Assert.IsFalse(json.ContainsKey("baseInstructions"), "CodeAlta adds to the prompt of Codex and never replaces it.");
        Assert.IsFalse(json.ContainsKey("model"));
    }

    [TestMethod]
    public async Task Client_InitializesThenAnswersRequestsByIdentifier()
    {
        await using var peer = new RawPeer();
        var connecting = CodexAppServerClient.ConnectAsync(peer.Process, new InitializeParams { ClientInfo = new ClientInfo { Name = "codealta", Version = "1" } },
            messageHandler: null, TimeSpan.FromSeconds(10));
        var initialize = await peer.ReadAsync();
        Assert.AreEqual("initialize", initialize["method"]!.GetValue<string>());
        Assert.IsNull(initialize["jsonrpc"], "The protocol omits the jsonrpc member.");
        Assert.AreEqual("codealta", initialize["params"]!["clientInfo"]!["name"]!.GetValue<string>());
        await peer.WriteAsync($$$"""{"id":{{{initialize["id"]}}},"result":{"codexHome":"/h","platformFamily":"unix","platformOs":"macos","userAgent":"codealta/0.165.0 (Mac OS)"}}""");
        await using var client = await connecting;
        Assert.AreEqual("initialized", (await peer.ReadAsync())["method"]!.GetValue<string>());
        Assert.AreEqual("0.165.0", CodexAppServerCommand.VersionFromUserAgent(client.InitializeResponse.UserAgent));

        var reading = client.AccountReadAsync(new GetAccountParams { RefreshToken = false });
        var request = await peer.ReadAsync();
        await peer.WriteAsync($$$$"""{"id":{{{{request["id"]}}}},"result":{"requiresOpenaiAuth":true,"account":{"type":"chatgpt","email":"a@b.c","planType":"business"}}}""");
        var account = await reading;

        Assert.AreEqual(PlanType.Business, ((Account.ChatgptAccount)account.Account!).PlanType);
    }

    [TestMethod]
    public async Task Client_GivesAnErrorAnswerAsAnException()
    {
        await using var peer = new RawPeer();
        await using var client = await peer.ConnectAsync();

        var starting = client.ThreadStartAsync(new ThreadStartParams());
        var request = await peer.ReadAsync();
        await peer.WriteAsync($$$"""{"id":{{{request["id"]}}},"error":{"code":-32600,"message":"failed to load configuration"}}""");

        var exception = await Assert.ThrowsExactlyAsync<CodexJsonRpcException>(() => starting);
        Assert.AreEqual("thread/start", exception.Method);
        Assert.AreEqual(-32600, exception.Code);
        Assert.AreEqual("failed to load configuration", exception.Message);
    }

    [TestMethod]
    public async Task Client_GivesRequestsOfTheServerToItsHandlerAndAnswersThem()
    {
        await using var peer = new RawPeer();
        var received = new TaskCompletionSource<CodexServerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = await peer.ConnectAsync(message => received.TrySetResult(message));

        await peer.WriteAsync("""{"id":"r-1","method":"item/fileChange/requestApproval","params":{"itemId":"p","threadId":"t","turnId":"u","startedAtMs":1,"reason":"edit"}}""");
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var approval = (FileChangeRequestApprovalParams)CodexMethods.ParseServerRequest(message.Method, message.Params)!;
        Assert.AreEqual("edit", approval.Reason);
        await client.RespondAsync(message.RequestId!.Value, new FileChangeRequestApprovalResponse { Decision = FileChangeApprovalDecision.Decline },
            CodexProtocolJsonContext.Default.FileChangeRequestApprovalResponse);

        var answer = await peer.ReadAsync();
        Assert.AreEqual("r-1", answer["id"]!.GetValue<string>());
        Assert.AreEqual("decline", answer["result"]!["decision"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Client_FailsWhatWaitsWhenTheServerStops()
    {
        await using var peer = new RawPeer();
        await using var client = await peer.ConnectAsync();
        var waiting = client.ModelListAsync(new ModelListParams());
        _ = await peer.ReadAsync();

        await peer.ExitAsync(3, "fatal: config is invalid");

        var exception = await Assert.ThrowsExactlyAsync<CodexConnectionClosedException>(() => waiting);
        StringAssert.Contains(exception.Message, "exited with code 3");
        StringAssert.Contains(exception.Message, "fatal: config is invalid");
        Assert.IsTrue(client.IsClosed);
    }

    [TestMethod]
    public void Command_TellsTheStandaloneServerFromTheCli()
    {
        using var temp = TestTempDirectory.Create();
        var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var standalone = Path.Combine(temp.Path, "codex-app-server" + suffix);
        var cli = Path.Combine(temp.Path, "codex" + suffix);
        File.WriteAllText(standalone, string.Empty);
        File.WriteAllText(cli, string.Empty);

        CollectionAssert.AreEqual(new[] { "--session-source", "codealta" }, CodexAppServerCommand.FromUserCommand(standalone)!.StartArguments.ToArray());
        CollectionAssert.AreEqual(new[] { "app-server", "--session-source", "codealta" }, CodexAppServerCommand.FromUserCommand(cli)!.StartArguments.ToArray());
        Assert.IsNull(CodexAppServerCommand.FromUserCommand(Path.Combine(temp.Path, "missing" + suffix)));
    }

    [TestMethod]
    public void Home_IsOnePerProviderUnderThePluginData()
    {
        var root = Path.Combine(Path.GetTempPath(), "alta");

        Assert.AreEqual(Path.GetFullPath(Path.Combine(root, "plugin-data", "codex-cli", "codex-work")), CodexAppServerHome.GetDefault(root, "codex-work"));
        Assert.AreEqual(Path.GetFullPath(Path.Combine(root, "plugin-data", "codex-cli", "a_b")), CodexAppServerHome.GetDefault(root, "a/b"));
        Assert.AreEqual(Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-work")),
            CodexAppServerHome.Resolve("~/.codex-work", root, "x"));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>The other end of a client: reads what it sends and writes what a server would, line by line.</summary>
    private sealed class RawPeer : IAsyncDisposable
    {
        private readonly Pipe _toClient = new();
        private readonly Pipe _toServer = new();
        private readonly Pipe _errors = new();
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly StreamReader _reader;

        public RawPeer()
        {
            Process = new CodexAppServerProcess("fake", _toClient.Reader.AsStream(), _toServer.Writer.AsStream(), _errors.Reader.AsStream(), _exited.Task,
                () =>
                {
                    _exited.TrySetResult(0);
                    _toClient.Writer.Complete();
                    _errors.Writer.Complete();
                    return ValueTask.CompletedTask;
                });
            _reader = new StreamReader(_toServer.Reader.AsStream(), Encoding.UTF8);
        }

        public CodexAppServerProcess Process { get; }

        public async Task<CodexAppServerClient> ConnectAsync(Action<CodexServerMessage>? handler = null)
        {
            var connecting = CodexAppServerClient.ConnectAsync(Process, new InitializeParams { ClientInfo = new ClientInfo { Name = "codealta", Version = "1" } }, handler, TimeSpan.FromSeconds(10));
            var initialize = await ReadAsync();
            await WriteAsync($$$"""{"id":{{{initialize["id"]}}},"result":{"codexHome":"/h","platformFamily":"unix","platformOs":"macos","userAgent":"codealta/0.165.0"}}""");
            var client = await connecting;
            _ = await ReadAsync();
            return client;
        }

        public async Task<JsonNode> ReadAsync()
        {
            var line = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return JsonNode.Parse(line!)!;
        }

        public async Task WriteAsync(string line)
        {
            await _toClient.Writer.WriteAsync(Encoding.UTF8.GetBytes(line.ReplaceLineEndings(string.Empty) + "\n"));
        }

        public async Task ExitAsync(int code, string error)
        {
            await _errors.Writer.WriteAsync(Encoding.UTF8.GetBytes(error + "\n"));
            await _errors.Writer.CompleteAsync();
            _exited.TrySetResult(code);
            await _toClient.Writer.CompleteAsync();
        }

        public ValueTask DisposeAsync()
        {
            _exited.TrySetResult(0);
            _toClient.Writer.Complete();
            return ValueTask.CompletedTask;
        }
    }
}
