using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.CodexAppServer;
using CodeAlta.CodexAppServer.Protocol;
using CodeAlta.Plugin.CodexCli;

namespace CodeAlta.Tests;

/// <summary>
/// A scripted Codex app-server on in-memory streams: it answers the protocol as <c>codex-app-server</c> does, keeps its
/// threads, and its turns run a script that streams text, runs commands, asks approvals and calls the MCP server of the
/// thread as Codex would.
/// </summary>
internal sealed class CodexAppServerFake : ICodexCliConnectionFactory
{
    private int _threads;
    private int _turns;

    public bool SignedIn { get; set; } = true;

    public List<string> Models { get; set; } = ["gpt-test", "gpt-mini"];

    /// <summary>Gets or sets the <c>models_cache.json</c> that <c>model/list</c> writes in the home, as Codex does; null for none.</summary>
    public string? ModelsCache { get; set; }

    /// <summary>Gets or sets the error <c>thread/start</c> answers with, or null to start threads.</summary>
    public string? ThreadStartError { get; set; }

    /// <summary>Gets or sets whether <c>turn/steer</c> is refused, as for a turn that takes no more input.</summary>
    public bool RefuseSteer { get; set; }

    /// <summary>Gets or sets what a turn does; by default it answers "ok".</summary>
    public Func<CodexFakeTurn, Task> OnTurn { get; set; } = static turn => turn.SendTextAsync("ok");

    /// <summary>Gets the threads by identifier, with the identifiers of their turns.</summary>
    public ConcurrentDictionary<string, List<string>> Threads { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the connections opened, one per process.</summary>
    public ConcurrentQueue<CodexFakeConnection> Connections { get; } = new();

    /// <summary>Gets every request a client sent, in order, with its parameters.</summary>
    public ConcurrentQueue<(string Method, JsonNode? Params)> Requests { get; } = new();

    public IEnumerable<JsonNode?> RequestsOf(string method) => Requests.Where(request => request.Method == method).Select(static request => request.Params);

    internal string NewThreadId() => $"thread-{Interlocked.Increment(ref _threads)}";

    internal string NewTurnId() => $"turn-{Interlocked.Increment(ref _turns)}";

    public async Task<CodexAppServerClient> StartAsync(CodexCliConnectionRequest request, Action<CodexServerMessage> messageHandler, CancellationToken cancellationToken)
    {
        var toClient = new Pipe();
        var toServer = new Pipe();
        var errors = new Pipe();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new CodexFakeConnection(this, request, toServer.Reader.AsStream(), toClient.Writer.AsStream(), exited, toClient.Writer, errors.Writer);
        Connections.Enqueue(connection);
        connection.Start();
        var process = new CodexAppServerProcess("fake-codex", toClient.Reader.AsStream(), toServer.Writer.AsStream(), errors.Reader.AsStream(), exited.Task,
            () =>
            {
                connection.Exit();
                return ValueTask.CompletedTask;
            });
        return await CodexAppServerClient.ConnectAsync(process, CodexCliClientInfo.CreateInitialize(), messageHandler, request.InitializeTimeout, cancellationToken: cancellationToken);
    }
}

/// <summary>One process of the fake app-server.</summary>
internal sealed class CodexFakeConnection
{
    private readonly CodexAppServerFake _server;
    private readonly StreamReader _input;
    private readonly Stream _output;
    private readonly TaskCompletionSource<int> _exited;
    private readonly PipeWriter _outputWriter;
    private readonly PipeWriter _errorWriter;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pendingRequests = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private int _nextRequest;
    private CodexFakeTurn? _activeTurn;

    public CodexFakeConnection(CodexAppServerFake server, CodexCliConnectionRequest request, Stream input, Stream output, TaskCompletionSource<int> exited,
        PipeWriter outputWriter, PipeWriter errorWriter)
    {
        _server = server;
        Request = request;
        _input = new StreamReader(input, Encoding.UTF8);
        _output = output;
        _exited = exited;
        _outputWriter = outputWriter;
        _errorWriter = errorWriter;
    }

    public CodexCliConnectionRequest Request { get; }

    /// <summary>Gets the MCP server the threads of this process were given: its address and its headers.</summary>
    public JsonObject? McpServer { get; private set; }

    /// <summary>Gets the configuration the last thread was opened with.</summary>
    public JsonObject? ThreadConfig { get; private set; }

    public bool Closed { get; private set; }

    public void Start() => _ = Task.Run(RunAsync);

    public void Exit()
    {
        if (Closed)
        {
            return;
        }

        Closed = true;
        _lifetime.Cancel();
        _exited.TrySetResult(0);
        _outputWriter.Complete();
        _errorWriter.Complete();
        foreach (var (_, pending) in _pendingRequests)
        {
            pending.TrySetCanceled();
        }
    }

    public Task NotifyAsync(string method, JsonNode parameters) => WriteAsync(new JsonObject { ["method"] = method, ["params"] = parameters });

    /// <summary>Sends a request to the client and waits for its result.</summary>
    public async Task<JsonNode?> RequestAsync(string method, JsonNode parameters)
    {
        var id = $"server-{Interlocked.Increment(ref _nextRequest)}";
        var pending = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = pending;
        await WriteAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters });
        return await pending.Task.WaitAsync(TimeSpan.FromSeconds(30), _lifetime.Token);
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested && await _input.ReadLineAsync(_lifetime.Token) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var message = JsonNode.Parse(line)!.AsObject();
                if (message["method"] is null && message["id"] is { } responseId)
                {
                    if (_pendingRequests.TryRemove(responseId.ToString(), out var pending))
                    {
                        pending.TrySetResult(message["result"] ?? message["error"]);
                    }

                    continue;
                }

                var method = message["method"]!.GetValue<string>();
                var parameters = message["params"];
                _server.Requests.Enqueue((method, parameters?.DeepClone()));
                if (message["id"] is { } id)
                {
                    _ = Task.Run(() => AnswerAsync(id.DeepClone(), method, parameters));
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private async Task AnswerAsync(JsonNode id, string method, JsonNode? parameters)
    {
        try
        {
            var result = await HandleAsync(method, parameters);
            await WriteAsync(new JsonObject { ["id"] = id, ["result"] = result });
        }
        catch (CodexFakeError error)
        {
            await WriteAsync(new JsonObject { ["id"] = id, ["error"] = new JsonObject { ["code"] = error.Code, ["message"] = error.Message } });
        }
    }

    private async Task<JsonNode?> HandleAsync(string method, JsonNode? parameters)
    {
        switch (method)
        {
            case "initialize":
                return new JsonObject { ["codexHome"] = Request.Home, ["platformFamily"] = "unix", ["platformOs"] = "macos", ["userAgent"] = $"codealta/{CodexPinnedRelease.Version} (fake)" };
            case "account/read":
                return new JsonObject
                {
                    ["requiresOpenaiAuth"] = true,
                    ["account"] = _server.SignedIn ? new JsonObject { ["type"] = "chatgpt", ["email"] = "user@example.com", ["planType"] = "business" } : null,
                };
            case "account/rateLimits/read":
                return new JsonObject { ["rateLimits"] = new JsonObject { ["primary"] = new JsonObject { ["usedPercent"] = 12 } } };
            case "model/list":
                if (_server.ModelsCache is { } cache)
                {
                    Directory.CreateDirectory(Request.Home);
                    await File.WriteAllTextAsync(Path.Combine(Request.Home, "models_cache.json"), cache);
                }

                return new JsonObject
                {
                    ["data"] = new JsonArray([.. _server.Models.Select((model, index) => (JsonNode)new JsonObject
                    {
                        ["id"] = model,
                        ["model"] = model,
                        ["displayName"] = model.ToUpperInvariant(),
                        ["description"] = $"The {model} model.",
                        ["hidden"] = false,
                        ["isDefault"] = index == 0,
                        ["defaultReasoningEffort"] = "medium",
                        ["supportedReasoningEfforts"] = new JsonArray(
                            new JsonObject { ["reasoningEffort"] = "low", ["description"] = "Fast" },
                            new JsonObject { ["reasoningEffort"] = "high", ["description"] = "Deep" },
                            new JsonObject { ["reasoningEffort"] = "ultra", ["description"] = "Deeper" }),
                    })]),
                };
            case "account/login/start":
                var device = parameters?["type"]?.GetValue<string>() == "chatgptDeviceCode";
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50);
                    _server.SignedIn = true;
                    await NotifyAsync("account/login/completed", new JsonObject { ["loginId"] = "login-1", ["success"] = true });
                });
                return device
                    ? new JsonObject { ["type"] = "chatgptDeviceCode", ["loginId"] = "login-1", ["verificationUrl"] = "https://auth.openai.com/codex/device", ["userCode"] = "ABCD-1234" }
                    : new JsonObject { ["type"] = "chatgpt", ["loginId"] = "login-1", ["authUrl"] = "https://auth.openai.com/oauth/authorize?client_id=app_EMoamEEZ73f0CkXaXp7hrann" };
            case "account/login/cancel":
                return new JsonObject { ["status"] = "canceled" };
            case "thread/start":
                if (_server.ThreadStartError is { } error)
                {
                    throw new CodexFakeError(-32600, error);
                }

                return OpenThread(_server.NewThreadId(), parameters, []);
            case "thread/resume":
                var resumed = parameters!["threadId"]!.GetValue<string>();
                if (!_server.Threads.TryGetValue(resumed, out var turns))
                {
                    throw new CodexFakeError(-32600, $"no rollout found for thread id {resumed}");
                }

                return OpenThread(resumed, parameters, turns);
            case "thread/fork":
                var source = parameters!["threadId"]!.GetValue<string>();
                if (!_server.Threads.TryGetValue(source, out var sourceTurns))
                {
                    throw new CodexFakeError(-32600, $"no rollout found for thread id {source}");
                }

                var last = parameters["lastTurnId"]?.GetValue<string>();
                var kept = last is null ? [.. sourceTurns] : sourceTurns.TakeWhile(turn => turn != last).Append(last).ToList();
                return OpenThread(_server.NewThreadId(), parameters, kept);
            case "turn/start":
                var threadId = parameters!["threadId"]!.GetValue<string>();
                var turnId = _server.NewTurnId();
                _server.Threads[threadId].Add(turnId);
                var turn = new CodexFakeTurn(this, threadId, turnId, parameters.AsObject());
                _activeTurn = turn;
                _ = Task.Run(() => RunTurnAsync(turn));
                return new JsonObject { ["turn"] = Turn(turnId, "inProgress") };
            case "turn/steer":
                if (_server.RefuseSteer || _activeTurn is not { } active)
                {
                    throw new CodexFakeError(-32600, "activeTurnNotSteerable");
                }

                active.Steers.Enqueue(parameters!["input"]!.DeepClone());
                return new JsonObject { ["turnId"] = active.TurnId };
            case "turn/interrupt":
                _activeTurn?.Interrupt();
                return new JsonObject();
            case "thread/compact/start":
                var compactedThread = parameters!["threadId"]!.GetValue<string>();
                var compaction = _server.NewTurnId();
                _ = Task.Run(async () =>
                {
                    await NotifyAsync("turn/started", new JsonObject { ["threadId"] = compactedThread, ["turn"] = Turn(compaction, "inProgress") });
                    await NotifyAsync("thread/compacted", new JsonObject { ["threadId"] = compactedThread, ["turnId"] = compaction });
                    await NotifyAsync("turn/completed", new JsonObject { ["threadId"] = compactedThread, ["turn"] = Turn(compaction, "completed") });
                });
                return new JsonObject();
            default:
                throw new CodexFakeError(-32601, $"method not found: {method}");
        }
    }

    private JsonObject OpenThread(string threadId, JsonNode? parameters, List<string> turns)
    {
        _server.Threads[threadId] = turns;
        ThreadConfig = parameters?["config"]?.DeepClone().AsObject();
        McpServer = ThreadConfig?["mcp_servers"]?["codealta"]?.DeepClone().AsObject();
        return new JsonObject
        {
            ["thread"] = new JsonObject { ["id"] = threadId, ["cliVersion"] = CodexPinnedRelease.Version, ["preview"] = string.Empty, ["turns"] = new JsonArray() },
            ["model"] = parameters?["model"]?.GetValue<string>() ?? "gpt-test",
            ["modelProvider"] = "openai",
            ["cwd"] = parameters?["cwd"]?.GetValue<string>(),
            ["approvalPolicy"] = parameters?["approvalPolicy"]?.DeepClone() ?? "untrusted",
            ["approvalsReviewer"] = "user",
            ["sandbox"] = new JsonObject { ["type"] = "dangerFullAccess" },
        };
    }

    private async Task RunTurnAsync(CodexFakeTurn turn)
    {
        await NotifyAsync("turn/started", new JsonObject { ["threadId"] = turn.ThreadId, ["turn"] = Turn(turn.TurnId, "inProgress") });
        var status = "completed";
        JsonObject? error = null;
        try
        {
            await _server.OnTurn(turn);
            if (turn.Interrupted.IsCancellationRequested)
            {
                status = "interrupted";
            }
        }
        catch (OperationCanceledException) when (turn.Interrupted.IsCancellationRequested)
        {
            status = "interrupted";
        }
        catch (CodexFakeTurnFailure failure)
        {
            status = "failed";
            error = new JsonObject { ["message"] = failure.Message, ["codexErrorInfo"] = failure.Kind };
        }

        if (!Closed)
        {
            await NotifyAsync("thread/tokenUsage/updated", new JsonObject
            {
                ["threadId"] = turn.ThreadId,
                ["turnId"] = turn.TurnId,
                ["tokenUsage"] = new JsonObject
                {
                    ["last"] = Usage(120, 30),
                    ["total"] = Usage(500, 90),
                    ["modelContextWindow"] = 400000,
                },
            });
            await NotifyAsync("turn/completed", new JsonObject { ["threadId"] = turn.ThreadId, ["turn"] = Turn(turn.TurnId, status, error) });
        }
    }

    private static JsonObject Usage(long input, long output)
        => new() { ["inputTokens"] = input, ["cachedInputTokens"] = input / 2, ["outputTokens"] = output, ["reasoningOutputTokens"] = output / 3, ["totalTokens"] = input + output };

    internal static JsonObject Turn(string id, string status, JsonObject? error = null)
        => new() { ["id"] = id, ["items"] = new JsonArray(), ["status"] = status, ["error"] = error, ["itemsView"] = "notLoaded" };

    private async Task WriteAsync(JsonObject message)
    {
        if (Closed)
        {
            return;
        }

        await _writeLock.WaitAsync();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
            await _output.WriteAsync(bytes);
            await _output.FlushAsync();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Calls a tool of the MCP server the thread was given, over HTTP, as Codex does.</summary>
    public async Task<JsonNode?> CallMcpToolAsync(string name, JsonObject arguments)
    {
        var server = McpServer ?? throw new InvalidOperationException("The thread was given no MCP server.");
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, server["url"]!.GetValue<string>());
        foreach (var (header, value) in server["http_headers"]!.AsObject())
        {
            request.Headers.TryAddWithoutValidation(header, value!.GetValue<string>());
        }

        request.Content = new StringContent(
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments } }.ToJsonString(),
            Encoding.UTF8,
            "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
        {
            return JsonNode.Parse(body)?["result"];
        }

        return body.Split('\n').Where(static line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(static line => JsonNode.Parse(line["data: ".Length..])!)
            .LastOrDefault(static message => message["id"] is not null)?["result"];
    }
}

/// <summary>A turn of the fake app-server: what its script sends.</summary>
internal sealed class CodexFakeTurn(CodexFakeConnection connection, string threadId, string turnId, JsonObject parameters)
{
    private readonly CancellationTokenSource _interrupted = new();
    private int _items;

    public CodexFakeConnection Connection { get; } = connection;

    public string ThreadId { get; } = threadId;

    public string TurnId { get; } = turnId;

    /// <summary>Gets the parameters of <c>turn/start</c>.</summary>
    public JsonObject Parameters { get; } = parameters;

    /// <summary>Gets the text of the input of the turn.</summary>
    public string Text => string.Join("\n", Parameters["input"]!.AsArray().Select(static input => input?["text"]?.GetValue<string>()).OfType<string>());

    /// <summary>Gets the inputs <c>turn/steer</c> added to the turn.</summary>
    public ConcurrentQueue<JsonNode> Steers { get; } = new();

    public CancellationToken Interrupted => _interrupted.Token;

    public void Interrupt() => _interrupted.Cancel();

    private string NextItem() => $"item-{TurnId}-{Interlocked.Increment(ref _items)}";

    private JsonObject Scope(JsonObject parameters)
    {
        parameters["threadId"] = ThreadId;
        parameters["turnId"] = TurnId;
        return parameters;
    }

    public async Task SendTextAsync(string text)
    {
        var id = NextItem();
        await Connection.NotifyAsync("item/started", Scope(new JsonObject { ["startedAtMs"] = 1, ["item"] = new JsonObject { ["type"] = "agentMessage", ["id"] = id, ["text"] = string.Empty } }));
        foreach (var part in new[] { text[..(text.Length / 2)], text[(text.Length / 2)..] })
        {
            await Connection.NotifyAsync("item/agentMessage/delta", Scope(new JsonObject { ["itemId"] = id, ["delta"] = part }));
        }

        await Connection.NotifyAsync("item/completed", Scope(new JsonObject { ["completedAtMs"] = 2, ["item"] = new JsonObject { ["type"] = "agentMessage", ["id"] = id, ["text"] = text } }));
    }

    public async Task SendReasoningAsync(string summary)
    {
        var id = NextItem();
        await Connection.NotifyAsync("item/started", Scope(new JsonObject { ["startedAtMs"] = 1, ["item"] = new JsonObject { ["type"] = "reasoning", ["id"] = id } }));
        await Connection.NotifyAsync("item/reasoning/summaryTextDelta", Scope(new JsonObject { ["itemId"] = id, ["delta"] = summary, ["summaryIndex"] = 0 }));
        await Connection.NotifyAsync("item/completed", Scope(new JsonObject { ["completedAtMs"] = 2, ["item"] = new JsonObject { ["type"] = "reasoning", ["id"] = id, ["summary"] = new JsonArray(summary) } }));
    }

    /// <summary>
    /// Generates an image as Codex does: started, then completed with the path Codex saved it to, or, without a path,
    /// failed as Codex ends a generation the image API refused (no failure detail unless a limit was reached).
    /// </summary>
    public async Task GenerateImageAsync(string revisedPrompt, string? savedPath)
    {
        var id = NextItem();
        await Connection.NotifyAsync("item/started", Scope(new JsonObject { ["startedAtMs"] = 1, ["item"] = new JsonObject { ["type"] = "imageGeneration", ["id"] = id, ["status"] = "in_progress" } }));
        var item = new JsonObject { ["type"] = "imageGeneration", ["id"] = id, ["status"] = savedPath is null ? "failed" : "completed", ["revisedPrompt"] = revisedPrompt };
        if (savedPath is not null)
        {
            item["savedPath"] = savedPath;
        }

        await Connection.NotifyAsync("item/completed", Scope(new JsonObject { ["completedAtMs"] = 2, ["item"] = item }));
    }

    /// <summary>Runs a command: started, its approval asked when <paramref name="askApproval"/>, its output, completed or declined.</summary>
    public async Task<string?> RunCommandAsync(string command, string output, bool askApproval = false)
    {
        var id = NextItem();
        JsonObject Item(string status, string? aggregated, int? exitCode)
            => new()
            {
                ["type"] = "commandExecution", ["id"] = id, ["command"] = command, ["cwd"] = Connection.Request.WorkingDirectory, ["commandActions"] = new JsonArray(),
                ["source"] = "agent", ["status"] = status, ["aggregatedOutput"] = aggregated, ["exitCode"] = exitCode,
            };
        await Connection.NotifyAsync("item/started", Scope(new JsonObject { ["startedAtMs"] = 1, ["item"] = Item("inProgress", null, null) }));
        string? decision = null;
        if (askApproval)
        {
            var answer = await Connection.RequestAsync("item/commandExecution/requestApproval",
                Scope(new JsonObject { ["itemId"] = id, ["startedAtMs"] = 1, ["command"] = command, ["cwd"] = Connection.Request.WorkingDirectory, ["kind"] = "command", ["reason"] = "needs approval" }));
            decision = answer?["decision"] is JsonValue value ? value.GetValue<string>() : answer?["decision"]?.ToJsonString();
            if (decision is not "accept" and not "acceptForSession")
            {
                await Connection.NotifyAsync("item/completed", Scope(new JsonObject { ["completedAtMs"] = 2, ["item"] = Item("declined", null, null) }));
                return decision;
            }
        }

        await Connection.NotifyAsync("item/commandExecution/outputDelta", Scope(new JsonObject { ["itemId"] = id, ["delta"] = output }));
        await Connection.NotifyAsync("item/completed", Scope(new JsonObject { ["completedAtMs"] = 2, ["item"] = Item("completed", output, 0) }));
        return decision;
    }

    /// <summary>Changes a file: started, its approval asked, completed or declined.</summary>
    public async Task<string?> ChangeFileAsync(string path, bool askApproval = true)
    {
        var id = NextItem();
        JsonObject Item(string status)
            => new()
            {
                ["type"] = "fileChange", ["id"] = id, ["status"] = status,
                ["changes"] = new JsonArray(new JsonObject { ["path"] = path, ["kind"] = new JsonObject { ["type"] = "update" }, ["diff"] = "@@ -1 +1 @@\n-a\n+b" }),
            };
        await Connection.NotifyAsync("item/started", Scope(new JsonObject { ["startedAtMs"] = 1, ["item"] = Item("inProgress") }));
        string? decision = null;
        if (askApproval)
        {
            var answer = await Connection.RequestAsync("item/fileChange/requestApproval", Scope(new JsonObject { ["itemId"] = id, ["startedAtMs"] = 1, ["reason"] = "edit" }));
            decision = answer?["decision"]?.GetValue<string>();
        }

        await Connection.NotifyAsync("item/completed", Scope(new JsonObject { ["completedAtMs"] = 2, ["item"] = Item(decision is null or "accept" or "acceptForSession" ? "completed" : "declined") }));
        return decision;
    }

    /// <summary>Calls a tool of the MCP server of the thread, announced as an <c>mcpToolCall</c> item, as Codex does.</summary>
    public async Task<JsonNode?> CallCodeAltaToolAsync(string tool, JsonObject arguments)
    {
        var id = NextItem();
        JsonObject Item(string status, JsonNode? result)
            => new() { ["type"] = "mcpToolCall", ["id"] = id, ["server"] = "codealta", ["tool"] = tool, ["arguments"] = arguments.DeepClone(), ["status"] = status, ["result"] = result };
        await Connection.NotifyAsync("item/started", Scope(new JsonObject { ["startedAtMs"] = 1, ["item"] = Item("inProgress", null) }));
        var result = await Connection.CallMcpToolAsync(tool, arguments);
        await Connection.NotifyAsync("item/completed", Scope(new JsonObject
        {
            ["completedAtMs"] = 2,
            ["item"] = Item("completed", new JsonObject { ["content"] = result?["content"]?.DeepClone() ?? new JsonArray() }),
        }));
        return result;
    }

    /// <summary>Waits with the sleep tool of Codex: a <c>sleep</c> item started, then completed when the wait ends.</summary>
    public async Task SleepAsync(ulong durationMs)
    {
        var id = NextItem();
        var item = new JsonObject { ["type"] = "sleep", ["id"] = id, ["durationMs"] = durationMs };
        await Connection.NotifyAsync("item/started", Scope(new JsonObject { ["startedAtMs"] = 1, ["item"] = item.DeepClone() }));
        await Connection.NotifyAsync("item/completed", Scope(new JsonObject { ["completedAtMs"] = 2, ["item"] = item }));
    }

    public Task UpdatePlanAsync(params (string Step, string Status)[] steps)
        => Connection.NotifyAsync("turn/plan/updated", Scope(new JsonObject
        {
            ["plan"] = new JsonArray([.. steps.Select(static step => (JsonNode)new JsonObject { ["step"] = step.Step, ["status"] = step.Status })]),
        }));

    public Task NotifyErrorAsync(string message, bool willRetry)
        => Connection.NotifyAsync("error", Scope(new JsonObject { ["error"] = new JsonObject { ["message"] = message }, ["willRetry"] = willRetry }));
}

/// <summary>An error a request of the fake app-server answers with.</summary>
internal sealed class CodexFakeError(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>Ends a turn of the fake app-server as failed, with a kind of <c>codexErrorInfo</c>.</summary>
internal sealed class CodexFakeTurnFailure(string message, string kind) : Exception(message)
{
    public string Kind { get; } = kind;
}
