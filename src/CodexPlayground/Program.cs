using System.Text.Json;
using CodeAlta.CodexSdk;
using PlaygroundShared;

var prompt = Environment.GetEnvironmentVariable("CODEX_PLAYGROUND_PROMPT")
             ?? "Call the MCP tool codealta.hello_world with the name \"CodeAlta\" and then tell me the exact tool result.";
var model = Environment.GetEnvironmentVariable("CODEX_PLAYGROUND_MODEL") ?? "gpt-5.1-codex-mini";
var workingDirectory = Environment.GetEnvironmentVariable("CODEX_PLAYGROUND_WORKDIR") ?? Environment.CurrentDirectory;
await using var mcpHost = await HelloWorldMcpHost.StartAsync().ConfigureAwait(false);

Console.WriteLine("Codex MCP playground");
Console.WriteLine($"MCP URL: {mcpHost.McpUrl}");
Console.WriteLine($"Working directory: {workingDirectory}");
Console.WriteLine($"Model: {model}");
Console.WriteLine("Prompt the model to use `codealta.hello_world` to verify MCP wiring.");
Console.WriteLine("Commands: /exit, /log, /clear");
Console.WriteLine();

await using var client = await CodexClient.StartAsync(
    new ClientInfo {
      Name = "codealta_codex_playground",
      Title = "CodeAlta Codex Playground",
      Version = "0.1.0"
    },
    experimentalApi: false,
    processOptions: null,
    cancellationToken: default)
  .ConfigureAwait(false);

var thread = await client.ThreadStartAsync(
    new ThreadStartParams {
      ApprovalPolicy = new AskForApproval.Never(),
      Cwd = workingDirectory,
      Model = model,
      Sandbox = SandboxMode.DangerFullAccess,
      Config = CreateMcpServerConfig(mcpHost.ServerName, mcpHost.McpUrl)
    })
  .ConfigureAwait(false);

var turnState = new ActiveTurnState();
using var pumpCancellation = new CancellationTokenSource();
var pumpTask = Task.Run(
  () => RunPumpAsync(client, turnState, pumpCancellation.Token),
  pumpCancellation.Token);

Console.WriteLine($"Thread: {thread.Thread.Id}");
Console.WriteLine("Enter a prompt.\n");

while (true) {
  Console.Write("You: ");
  var input = Console.ReadLine();
  if (string.IsNullOrWhiteSpace(input)) {
    input = prompt;
    Console.WriteLine(input);
  }

  var trimmed = input.Trim();
  if (string.Equals(trimmed, "/exit", StringComparison.OrdinalIgnoreCase)) {
    break;
  }

  if (string.Equals(trimmed, "/log", StringComparison.OrdinalIgnoreCase)) {
    DumpInvocationLog(mcpHost);
    Console.WriteLine();
    continue;
  }

  if (string.Equals(trimmed, "/clear", StringComparison.OrdinalIgnoreCase)) {
    mcpHost.ClearInvocations();
    Console.WriteLine("Invocation log cleared.\n");
    continue;
  }

  var completion = new TaskCompletionSource<TurnCompletedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
  var startResponse = await client.TurnStartAsync(
      new TurnStartParams {
        ThreadId = thread.Thread.Id,
        Input = [
          new UserInput.TextUserInput { Text = trimmed }
        ],
        Cwd = workingDirectory,
        ApprovalPolicy = new AskForApproval.Never(),
        SandboxPolicy = new SandboxPolicy.DangerFullAccessSandboxPolicy()
      })
    .ConfigureAwait(false);

  turnState.Set(startResponse.Turn.Id, completion);
  Console.WriteLine($"[turn] {startResponse.Turn.Id}");

  var finishedTurn = await completion.Task.ConfigureAwait(false);
  Console.WriteLine($"[turn completed] {finishedTurn.Turn.Id} status={finishedTurn.Turn.Status}");
  if (finishedTurn.Turn.Error is not null) {
    Console.WriteLine($"[turn error] {finishedTurn.Turn.Error.Message}");
  }

  var threadRead = await client.ThreadReadAsync(
      new ThreadReadParams {
        ThreadId = thread.Thread.Id,
        IncludeTurns = true
      })
    .ConfigureAwait(false);

  PrintLatestTurnSummary(threadRead.Thread);
  DumpInvocationLog(mcpHost);
  Console.WriteLine();
}

pumpCancellation.Cancel();
await pumpTask.ConfigureAwait(false);

static Dictionary<string, JsonElement> CreateMcpServerConfig(string serverName, string mcpUrl) {
  return new Dictionary<string, JsonElement>(StringComparer.Ordinal) {
    [$"mcp_servers.{serverName}.url"] = JsonSerializer.SerializeToElement(mcpUrl),
    [$"mcp_servers.{serverName}.enabled"] = JsonSerializer.SerializeToElement(true),
    [$"mcp_servers.{serverName}.required"] = JsonSerializer.SerializeToElement(false)
  };
}

static async Task RunPumpAsync(CodexClient client, ActiveTurnState turnState, CancellationToken cancellationToken) {
  try {
    await foreach (var message in client.StreamAsync(cancellationToken).ConfigureAwait(false)) {
      switch (message) {
        case CodexNotification notification:
          LogNotification(notification);
          if (notification is CodexNotification.TurnCompleted completed) {
            turnState.TryComplete(completed.Data.Turn.Id, completed.Data);
          }

          break;

        case ServerRequest request:
          Console.WriteLine($"[server request] {request.GetType().Name}");
          await RespondToServerRequestAsync(client, request, cancellationToken).ConfigureAwait(false);
          break;

        default:
          Console.WriteLine($"[stream] {message.GetType().Name}");
          break;
      }
    }
  } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
}

static async Task RespondToServerRequestAsync(CodexClient client, ServerRequest request, CancellationToken cancellationToken) {
  switch (request) {
    case ServerRequest.ItemCommandExecutionRequestApprovalRequest commandApproval:
      await client.RespondToRequestAsync(
          commandApproval.Id,
          new CommandExecutionRequestApprovalResponse {
            Decision = new CommandExecutionApprovalDecision.Accept()
          },
          cancellationToken)
        .ConfigureAwait(false);
      break;

    case ServerRequest.ItemFileChangeRequestApprovalRequest fileApproval:
      await client.RespondToRequestAsync(
          fileApproval.Id,
          new FileChangeRequestApprovalResponse {
            Decision = FileChangeApprovalDecision.Accept
          },
          cancellationToken)
        .ConfigureAwait(false);
      break;

    case ServerRequest.ItemToolRequestUserInputRequest requestUserInput:
      await client.RespondToRequestAsync(
          requestUserInput.Id,
          CreateEmptyUserInputResponse(requestUserInput.Params),
          cancellationToken)
        .ConfigureAwait(false);
      break;

    default:
      Console.WriteLine($"[server request] no handler for {request.GetType().Name}");
      break;
  }
}

static ToolRequestUserInputResponse CreateEmptyUserInputResponse(ToolRequestUserInputParams parameters) {
  var answers = parameters.Questions.ToDictionary(
    static question => question.Id,
    static _ => new ToolRequestUserInputAnswer {
      Answers = [string.Empty]
    },
    StringComparer.Ordinal);

  return new ToolRequestUserInputResponse {
    Answers = answers
  };
}

static void PrintLatestTurnSummary(CodeAlta.CodexSdk.Thread thread) {
  var turn = thread.Turns.LastOrDefault();
  if (turn is null) {
    Console.WriteLine("[thread] no turns loaded");
    return;
  }

  foreach (var item in turn.Items) {
    switch (item) {
      case ThreadItem.AgentMessageThreadItem message:
        Console.WriteLine($"[assistant] {message.Text}");
        break;

      case ThreadItem.ReasoningThreadItem reasoning:
        Console.WriteLine($"[reasoning] {string.Join(" ", reasoning.Summary ?? reasoning.Content ?? [])}");
        break;

      case ThreadItem.McpToolCallThreadItem mcpToolCall:
        Console.WriteLine($"[mcp item] server={mcpToolCall.Server} tool={mcpToolCall.Tool} status={mcpToolCall.Status}");
        Console.WriteLine($"  args={mcpToolCall.Arguments.GetRawText()}");
        if (mcpToolCall.Result is not null) {
          Console.WriteLine($"  result={JsonSerializer.Serialize(mcpToolCall.Result)}");
        }

        if (mcpToolCall.Error is not null) {
          Console.WriteLine($"  error={mcpToolCall.Error.Message}");
        }

        break;
    }
  }
}

static void LogNotification(CodexNotification notification) {
  switch (notification) {
    case CodexNotification.TurnStarted started:
      Console.WriteLine($"[notification] turn started {started.Data.Turn.Id}");
      break;

    case CodexNotification.McpToolCallProgress progress:
      Console.WriteLine($"[notification] mcp progress call={progress.Data.ItemId} turn={progress.Data.TurnId}");
      break;

    case CodexNotification.ItemCompleted completed when completed.Data.Item is ThreadItem.McpToolCallThreadItem mcpToolCall:
      Console.WriteLine($"[notification] mcp completed server={mcpToolCall.Server} tool={mcpToolCall.Tool} status={mcpToolCall.Status}");
      break;

    case CodexNotification.AgentMessageDelta delta:
      Console.Write(delta.Data.Delta);
      break;

    case CodexNotification.ReasoningSummaryTextDelta delta:
      Console.WriteLine($"[reasoning delta] {delta.Data.Delta}");
      break;

    case CodexNotification.Error error:
      Console.WriteLine($"[notification/error] {error.Data.Error.Message}");
      break;
  }
}

static void DumpInvocationLog(HelloWorldMcpHost mcpHost) {
  if (!mcpHost.TryGetInvocations(out var entries)) {
    Console.WriteLine("[mcp log] no invocations");
    return;
  }

  Console.WriteLine("[mcp log]");
  foreach (var entry in entries) {
    Console.WriteLine($"  {entry}");
  }
}

file sealed class ActiveTurnState {
  private readonly object _gate = new();
  private string? _turnId;
  private TaskCompletionSource<TurnCompletedNotification>? _completion;

  public void Set(string turnId, TaskCompletionSource<TurnCompletedNotification> completion) {
    lock (_gate) {
      _turnId = turnId;
      _completion = completion;
    }
  }

  public void TryComplete(string turnId, TurnCompletedNotification notification) {
    TaskCompletionSource<TurnCompletedNotification>? completion = null;
    lock (_gate) {
      if (!string.Equals(_turnId, turnId, StringComparison.Ordinal)) {
        return;
      }

      completion = _completion;
      _turnId = null;
      _completion = null;
    }

    completion?.TrySetResult(notification);
  }
}