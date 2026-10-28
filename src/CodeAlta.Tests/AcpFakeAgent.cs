using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Acp;
using CodeAlta.Plugin.Acp;

namespace CodeAlta.Tests;

/// <summary>
/// A scripted ACP agent on in-memory streams: it answers the protocol as an agent does, and its prompts run a script
/// that sends updates, asks permissions and calls the MCP server of the session as an agent would.
/// </summary>
internal sealed class AcpFakeAgent
{
    private int _sessions;

    /// <summary>Gets or sets how long the agent takes to start, as one that is slow on a computer, or downloaded first.</summary>
    public TimeSpan StartDelay { get; set; }

    public bool LoadSession { get; set; } = true;

    public bool ResumeSession { get; set; }

    public bool CloseSession { get; set; } = true;

    public bool SystemPrompt { get; set; }

    public bool McpHttp { get; set; } = true;

    public bool Images { get; set; } = true;

    /// <summary>Gets or sets the error code <c>session/new</c> answers with, or null to start sessions.</summary>
    public int? NewSessionError { get; set; }

    /// <summary>The text of the error of <see cref="NewSessionError"/>; the usual one of its code when null.</summary>
    public string? NewSessionErrorMessage { get; set; }

    public JsonArray AuthMethods { get; set; } = [];

    public List<string> Models { get; set; } = ["fast", "smart"];

    public List<string> Efforts { get; set; } = ["low", "medium", "high"];

    /// <summary>Gets the efforts of the models that do not have <see cref="Efforts"/>, by model; an empty list for a model without one.</summary>
    public Dictionary<string, List<string>> EffortsByModel { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the model a new session starts with, or null for the first one.</summary>
    public string? CurrentModel { get; set; }

    internal List<string> EffortsOf(string model) => EffortsByModel.TryGetValue(model, out var efforts) ? efforts : Efforts;

    /// <summary>
    /// Gets the other options of each model, as Cursor gives them: by option id, its values, the first the default. A
    /// <c>thinking</c> option is a <c>thought_level</c> listed before the effort, the others are <c>model_config</c>.
    /// The agent keeps the value of each option of each model, as Cursor does.
    /// </summary>
    public Dictionary<string, Dictionary<string, List<string>>> ParametersByModel { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the options set by the client, in order: <c>configId=value</c>.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> ConfigChanges { get; } = new();

    /// <summary>Gets the client capabilities of the last <c>initialize</c>.</summary>
    public JsonObject? ClientCapabilities { get; internal set; }

    internal Dictionary<string, Dictionary<string, string>> ParameterValues { get; } = new(StringComparer.Ordinal);

    internal string ParameterValue(string model, string id)
        => ParameterValues.TryGetValue(model, out var values) && values.TryGetValue(id, out var value) ? value : ParametersByModel[model][id][0];

    /// <summary>Gets or sets what a prompt does; by default it answers "ok".</summary>
    public Func<AcpFakePrompt, Task<string>> OnPrompt { get; set; } = static async prompt =>
    {
        await prompt.SendTextAsync("ok");
        return "end_turn";
    };

    /// <summary>Gets the connections opened, one per agent process.</summary>
    public ConcurrentQueue<AcpFakeConnection> Connections { get; } = new();

    /// <summary>Gets every request a client sent, in order, with its parameters.</summary>
    public ConcurrentQueue<(string Method, JsonNode? Params)> Requests { get; } = new();

    public IEnumerable<JsonNode?> RequestsOf(string method) => Requests.Where(request => request.Method == method).Select(static request => request.Params);

    internal string NextSessionId() => $"agent-session-{Interlocked.Increment(ref _sessions)}";

    /// <summary>A factory of connections the executor starts: each one is a new agent process.</summary>
    public IAcpConnectionFactory CreateFactory() => new Factory(this);

    private sealed class Factory(AcpFakeAgent agent) : IAcpConnectionFactory
    {
        public async Task<AcpClient> StartAsync(AcpConnectionRequest request, Action<AcpServerMessage> messageHandler, CancellationToken cancellationToken)
        {
            await Task.Delay(agent.StartDelay, cancellationToken);
            var toAgent = new Pipe();
            var fromAgent = new Pipe();
            var connection = new AcpFakeConnection(agent, toAgent.Reader.AsStream(), fromAgent.Writer.AsStream(), request);
            agent.Connections.Enqueue(connection);
            connection.Start();
            return await AcpClient.ConnectAsync(fromAgent.Reader.AsStream(), toAgent.Writer.AsStream(), AcpClientInfo.CreateInitializeRequest(), messageHandler, cancellationToken);
        }
    }
}

/// <summary>One process of the fake agent.</summary>
internal sealed class AcpFakeConnection
{
    private readonly AcpFakeAgent _agent;
    private readonly StreamReader _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly ConcurrentDictionary<string, JsonObject> _sessions = new(StringComparer.Ordinal);
    private CancellationTokenSource? _prompt;
    private long _nextId = 1000;

    public AcpFakeConnection(AcpFakeAgent agent, Stream input, Stream output, AcpConnectionRequest request)
    {
        _agent = agent;
        _input = new StreamReader(input, Encoding.UTF8);
        _output = output;
        Request = request;
    }

    public AcpConnectionRequest Request { get; }

    /// <summary>Gets the MCP servers the client gave each session, by session.</summary>
    public ConcurrentDictionary<string, JsonArray> McpServers { get; } = new(StringComparer.Ordinal);

    public bool Closed { get; private set; }

    public void Start() => _ = Task.Run(RunAsync);

    /// <summary>Ends the process, as an agent that exits.</summary>
    public void Exit()
    {
        Closed = true;
        _output.Dispose();
    }

    internal Task SendUpdateAsync(string sessionId, JsonObject update)
        => WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "session/update",
            ["params"] = new JsonObject { ["sessionId"] = sessionId, ["update"] = update },
        });

    internal async Task<JsonNode?> RequestAsync(string method, JsonObject parameters)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private async Task RunAsync()
    {
        try
        {
            while (await _input.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var message = JsonNode.Parse(line)!.AsObject();
                if (message["method"] is null)
                {
                    if (message["id"]?.GetValue<long>() is { } responseId && _pending.TryRemove(responseId, out var pending))
                    {
                        pending.TrySetResult(message["result"]?.DeepClone() ?? message["error"]?.DeepClone());
                    }

                    continue;
                }

                var method = message["method"]!.GetValue<string>();
                var parameters = message["params"]?.DeepClone();
                _agent.Requests.Enqueue((method, parameters));
                var id = message["id"]?.DeepClone();
                if (id is null)
                {
                    if (method == "session/cancel")
                    {
                        _prompt?.Cancel();
                    }

                    continue;
                }

                _ = Task.Run(() => AnswerAsync(id, method, parameters as JsonObject ?? []));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private async Task AnswerAsync(JsonNode id, string method, JsonObject parameters)
    {
        try
        {
            JsonNode? result = method switch
            {
                "initialize" => Initialize(parameters),
                "session/new" => NewSession(parameters),
                "session/load" => await LoadSessionAsync(parameters),
                "session/resume" => ResumeSession(parameters),
                "session/close" => new JsonObject(),
                "session/set_config_option" => SetConfigOption(parameters),
                "session/prompt" => await PromptAsync(parameters),
                "authenticate" => new JsonObject(),
                _ => throw new FakeRpcException(-32601, $"Method '{method}' not found"),
            };
            await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });
        }
        catch (FakeRpcException ex)
        {
            await WriteAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.DeepClone(),
                ["error"] = new JsonObject { ["code"] = ex.Code, ["message"] = ex.Message },
            });
        }
    }

    private JsonObject Initialize(JsonObject parameters)
    {
        _agent.ClientCapabilities = parameters["clientCapabilities"]?.DeepClone().AsObject();
        var session = new JsonObject();
        if (_agent.CloseSession) session["close"] = new JsonObject();
        if (_agent.ResumeSession) session["resume"] = new JsonObject();
        if (_agent.SystemPrompt) session["systemPrompt"] = new JsonObject();
        return new JsonObject
        {
            ["protocolVersion"] = 1,
            ["agentCapabilities"] = new JsonObject
            {
                ["loadSession"] = _agent.LoadSession,
                ["mcpCapabilities"] = new JsonObject { ["http"] = _agent.McpHttp, ["sse"] = false },
                ["promptCapabilities"] = new JsonObject { ["image"] = _agent.Images, ["embeddedContext"] = true },
                ["sessionCapabilities"] = session,
            },
            ["agentInfo"] = new JsonObject { ["name"] = "fake-agent", ["title"] = "Fake Agent", ["version"] = "0.1.0" },
            ["authMethods"] = _agent.AuthMethods.DeepClone(),
        };
    }

    private JsonObject NewSession(JsonObject parameters)
    {
        if (_agent.NewSessionError is { } code)
        {
            throw new FakeRpcException(code, _agent.NewSessionErrorMessage ?? (code == -32000 ? "Authentication required" : "Failed"));
        }

        var sessionId = _agent.NextSessionId();
        McpServers[sessionId] = (parameters["mcpServers"] as JsonArray ?? []).DeepClone().AsArray();
        var model = _agent.CurrentModel ?? _agent.Models[0];
        var efforts = _agent.EffortsOf(model);
        var options = CreateOptions(model, efforts.Count > 0 ? efforts[^1] : null);
        _sessions[sessionId] = options;
        return new JsonObject { ["sessionId"] = sessionId, ["configOptions"] = options["list"]!.DeepClone() };
    }

    private async Task<JsonObject> LoadSessionAsync(JsonObject parameters)
    {
        var sessionId = parameters["sessionId"]!.GetValue<string>();
        McpServers[sessionId] = (parameters["mcpServers"] as JsonArray ?? []).DeepClone().AsArray();
        // A loaded session is replayed to the client before the answer.
        await SendUpdateAsync(sessionId, new JsonObject { ["sessionUpdate"] = "user_message_chunk", ["content"] = Text("an old prompt") });
        await SendUpdateAsync(sessionId, new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = Text("an old answer") });
        var options = _sessions.GetOrAdd(sessionId, _ => CreateOptions(_agent.Models[0], null));
        return new JsonObject { ["configOptions"] = options["list"]!.DeepClone() };
    }

    private JsonObject ResumeSession(JsonObject parameters)
    {
        var sessionId = parameters["sessionId"]!.GetValue<string>();
        McpServers[sessionId] = (parameters["mcpServers"] as JsonArray ?? []).DeepClone().AsArray();
        var options = _sessions.GetOrAdd(sessionId, _ => CreateOptions(_agent.Models[0], null));
        return new JsonObject { ["configOptions"] = options["list"]!.DeepClone() };
    }

    private JsonObject SetConfigOption(JsonObject parameters)
    {
        var sessionId = parameters["sessionId"]!.GetValue<string>();
        var configId = parameters["configId"]!.GetValue<string>();
        var value = parameters["value"]!.GetValue<string>();
        var current = _sessions[sessionId];
        _agent.ConfigChanges.Enqueue($"{configId}={value}");
        var model = configId == "model" ? value : current["model"]!.GetValue<string>();
        var effort = configId == "reasoning" ? value : current["effort"]?.GetValue<string>();
        if (!_agent.Models.Contains(model))
        {
            throw new FakeRpcException(-32602, $"Invalid model value: {model}");
        }

        if (configId is not "model" and not "reasoning")
        {
            if (!_agent.ParametersByModel.TryGetValue(model, out var modelParameters) || !modelParameters.TryGetValue(configId, out var values) || !values.Contains(value))
            {
                throw new FakeRpcException(-32602, $"Invalid value for {configId}: {value}");
            }

            lock (_agent.ParameterValues)
            {
                if (!_agent.ParameterValues.TryGetValue(model, out var kept))
                {
                    _agent.ParameterValues[model] = kept = new Dictionary<string, string>(StringComparer.Ordinal);
                }

                kept[configId] = value;
            }
        }

        // The efforts change with the model: one it does not have becomes its own default.
        var efforts = _agent.EffortsOf(model);
        effort = efforts.Count == 0 ? null : effort is not null && efforts.Contains(effort) ? effort : efforts[^1];
        var options = CreateOptions(model, effort);
        _sessions[sessionId] = options;
        return new JsonObject { ["configOptions"] = options["list"]!.DeepClone() };
    }

    private JsonObject CreateOptions(string model, string? effort)
    {
        var list = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "model", ["name"] = "Model", ["category"] = "model", ["type"] = "select", ["currentValue"] = model,
                ["options"] = new JsonArray([.. _agent.Models.Select(static name => (JsonNode)new JsonObject { ["value"] = name, ["name"] = name.ToUpperInvariant() })]),
            },
        };
        var parameters = _agent.ParametersByModel.TryGetValue(model, out var listed) ? listed : [];
        if (parameters.ContainsKey("thinking"))
        {
            list.Add(Parameter("thinking", "thought_level"));
        }

        if (effort is not null)
        {
            list.Add(new JsonObject
            {
                ["id"] = "reasoning", ["name"] = "Reasoning", ["category"] = "thought_level", ["type"] = "select", ["currentValue"] = effort,
                ["options"] = new JsonArray([.. _agent.EffortsOf(model).Select(static name => (JsonNode)new JsonObject { ["value"] = name, ["name"] = name })]),
            });
        }

        foreach (var id in parameters.Keys.Where(static id => id != "thinking"))
        {
            list.Add(Parameter(id, "model_config"));
        }

        return new JsonObject { ["list"] = list, ["model"] = model, ["effort"] = effort };

        JsonObject Parameter(string id, string category)
        {
            string current;
            lock (_agent.ParameterValues)
            {
                current = _agent.ParameterValue(model, id);
            }

            return new JsonObject
            {
                ["id"] = id, ["name"] = char.ToUpperInvariant(id[0]) + id[1..], ["category"] = category, ["type"] = "select", ["currentValue"] = current,
                ["options"] = new JsonArray([.. parameters[id].Select(static value => (JsonNode)new JsonObject { ["value"] = value, ["name"] = value.ToUpperInvariant() })]),
            };
        }
    }

    private async Task<JsonObject> PromptAsync(JsonObject parameters)
    {
        var sessionId = parameters["sessionId"]!.GetValue<string>();
        using var cancellation = new CancellationTokenSource();
        _prompt = cancellation;
        var prompt = new AcpFakePrompt(this, sessionId, parameters["prompt"]!.AsArray(), McpServers.GetValueOrDefault(sessionId), cancellation.Token);
        string stopReason;
        try
        {
            stopReason = await _agent.OnPrompt(prompt);
        }
        catch (OperationCanceledException)
        {
            stopReason = "cancelled";
        }

        return new JsonObject
        {
            ["stopReason"] = cancellation.IsCancellationRequested ? "cancelled" : stopReason,
            ["usage"] = new JsonObject { ["inputTokens"] = 120, ["outputTokens"] = 30, ["totalTokens"] = 150, ["cachedReadTokens"] = 100 },
        };
    }

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
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    private sealed class FakeRpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}

/// <summary>A prompt of the fake agent, which its script answers.</summary>
internal sealed class AcpFakePrompt(AcpFakeConnection connection, string sessionId, JsonArray content, JsonArray? mcpServers, CancellationToken cancellationToken)
{
    public string SessionId { get; } = sessionId;

    public AcpFakeConnection Connection { get; } = connection;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    public JsonArray Content { get; } = content;

    /// <summary>Gets the text of the prompt, its blocks joined.</summary>
    public string Text => string.Join("\n", Content.Select(static block => block?["text"]?.GetValue<string>()).Where(static text => text is not null));

    public Task SendTextAsync(string text)
        => Connection.SendUpdateAsync(SessionId, new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = AcpFakeConnection.Text(text) });

    public Task SendThoughtAsync(string text)
        => Connection.SendUpdateAsync(SessionId, new JsonObject { ["sessionUpdate"] = "agent_thought_chunk", ["content"] = AcpFakeConnection.Text(text) });

    public Task SendUpdateAsync(JsonObject update) => Connection.SendUpdateAsync(SessionId, update);

    public Task StartToolAsync(string id, string title, string kind, JsonObject? rawInput = null, JsonArray? locations = null)
        => SendUpdateAsync(new JsonObject
        {
            ["sessionUpdate"] = "tool_call",
            ["toolCallId"] = id,
            ["title"] = title,
            ["kind"] = kind,
            ["status"] = "pending",
            ["rawInput"] = rawInput,
            ["locations"] = locations,
        });

    public Task EndToolAsync(string id, string output, bool success = true)
        => SendUpdateAsync(new JsonObject
        {
            ["sessionUpdate"] = "tool_call_update",
            ["toolCallId"] = id,
            ["status"] = success ? "completed" : "failed",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "content", ["content"] = AcpFakeConnection.Text(output) }),
        });

    /// <summary>Asks the client the permission of a tool call; returns the outcome object.</summary>
    public async Task<JsonNode?> RequestPermissionAsync(string toolCallId, string title, string kind, JsonObject? rawInput = null)
        => await Connection.RequestAsync("session/request_permission", new JsonObject
        {
            ["sessionId"] = SessionId,
            ["toolCall"] = new JsonObject { ["toolCallId"] = toolCallId, ["title"] = title, ["kind"] = kind, ["rawInput"] = rawInput },
            ["options"] = new JsonArray(
                new JsonObject { ["optionId"] = "yes", ["name"] = "Allow", ["kind"] = "allow_once" },
                new JsonObject { ["optionId"] = "always", ["name"] = "Always allow", ["kind"] = "allow_always" },
                new JsonObject { ["optionId"] = "no", ["name"] = "Reject", ["kind"] = "reject_once" }),
        });

    /// <summary>Calls a tool of the MCP server the client gave the session, over HTTP.</summary>
    public Task<JsonNode?> CallMcpToolAsync(string name, JsonObject arguments)
        => PostMcpAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments });

    /// <summary>Lists the tools of the MCP server the client gave the session, by their names.</summary>
    public async Task<string[]> ListMcpToolNamesAsync()
        => [.. (await PostMcpAsync("tools/list", new JsonObject()))!["tools"]!.AsArray().Select(static tool => tool!["name"]!.GetValue<string>())];

    /// <summary>Opens the stream of the messages of the MCP server, as a client of Streamable HTTP does.</summary>
    public async Task<AcpFakeMcpEvents> OpenMcpEventsAsync()
    {
        var client = new HttpClient();
        using var request = CreateMcpRequest(HttpMethod.Get);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken);
        response.EnsureSuccessStatusCode();
        return new AcpFakeMcpEvents(client, response, new StreamReader(await response.Content.ReadAsStreamAsync(CancellationToken), Encoding.UTF8));
    }

    /// <summary>Gets the messages of the server the responses of posts carried before them, as Copilot takes them.</summary>
    public List<JsonNode> McpNotifications { get; } = [];

    // Posts as Copilot does: a response is one JSON body, or a stream of events that ends with it.
    private async Task<JsonNode?> PostMcpAsync(string method, JsonObject parameters)
    {
        using var client = new HttpClient();
        using var request = CreateMcpRequest(HttpMethod.Post);
        request.Content = new StringContent(
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters }.ToJsonString(),
            Encoding.UTF8,
            "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request, CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
        {
            return JsonNode.Parse(body)?["result"];
        }

        JsonNode? result = null;
        foreach (var line in body.Split('\n').Where(static line => line.StartsWith("data: ", StringComparison.Ordinal)))
        {
            var message = JsonNode.Parse(line["data: ".Length..])!;
            if (message["id"] is null)
            {
                lock (McpNotifications)
                {
                    McpNotifications.Add(message);
                }
            }
            else
            {
                result = message["result"];
            }
        }

        return result;
    }

    private HttpRequestMessage CreateMcpRequest(HttpMethod method)
    {
        var server = mcpServers?.OfType<JsonObject>().FirstOrDefault() ?? throw new InvalidOperationException("The session was given no MCP server.");
        var request = new HttpRequestMessage(method, server["url"]!.GetValue<string>());
        foreach (var header in server["headers"]!.AsArray().OfType<JsonObject>())
        {
            request.Headers.TryAddWithoutValidation(header["name"]!.GetValue<string>(), header["value"]!.GetValue<string>());
        }

        return request;
    }
}

/// <summary>The stream of the messages of the MCP server of a session, as the fake agent reads it.</summary>
internal sealed class AcpFakeMcpEvents(HttpClient client, HttpResponseMessage response, StreamReader reader) : IAsyncDisposable
{
    /// <summary>Reads the next message of the server, or null when the stream ends first.</summary>
    public async Task<JsonNode?> ReadMessageAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (await reader.ReadLineAsync(cancellation.Token) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                return JsonNode.Parse(line["data: ".Length..]);
            }
        }

        return null;
    }

    public ValueTask DisposeAsync()
    {
        reader.Dispose();
        response.Dispose();
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}
