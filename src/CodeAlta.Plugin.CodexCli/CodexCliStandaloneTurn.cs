using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using CodeAlta.Agent.Runtime;
using CodeAlta.CodexAppServer;
using CodeAlta.CodexAppServer.Protocol;

namespace CodeAlta.Plugin.CodexCli;

/// <summary>
/// A turn of Codex that belongs to no session (<see cref="IAgentProviderStandaloneTurns"/>): an app-server of its own,
/// started for the turn and stopped when it ends, runs one turn in an ephemeral thread, which Codex does not write to
/// disk, so that nothing of it can be listed or resumed afterwards.
/// </summary>
/// <remarks>
/// Codex is given no tool of CodeAlta (no MCP server), and its own tools are turned off where its configuration allows
/// it: its shell, <c>view_image</c>, its wait, image generation, its plan and web search. Its <c>apply_patch</c>, which
/// the model decides, has no switch: the thread runs in Codex's read-only sandbox with the <c>untrusted</c> policy, so
/// that a change asks for an approval, and every approval is refused.
/// </remarks>
internal static class CodexCliStandaloneTurn
{
    private const string TextContentId = "codex-standalone:text";
    private const string ReasoningContentId = "codex-standalone:reasoning";

    private abstract record Event;

    private sealed record DeltaEvent(AgentContentKind Kind, string ItemId, string Text) : Event;

    private sealed record MessageCompletedEvent(string ItemId, string Text) : Event;

    private sealed record TurnEndedEvent(Turn Turn) : Event;

    private sealed record ClosedEvent(string Message) : Event;

    /// <summary>Runs <paramref name="request"/> on its own, and stops the app-server it started once it ends.</summary>
    /// <exception cref="NotSupportedException">Codex did not open an ephemeral thread (its version).</exception>
    /// <exception cref="AgentProviderFailureException">Codex could not be started, is not signed in, or failed the turn.</exception>
    /// <exception cref="OperationCanceledException">The turn was stopped.</exception>
    public static async Task<AgentTurnResponse> ExecuteAsync(
        CodexCliRuntimeOptions options,
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken)
    {
        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory) ? options.ProbeDirectory : request.WorkingDirectory;
        var command = await options.ResolveCommandAsync(progress: null, cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(workingDirectory);
            CodexCliHomeConfig.Write(options.Home);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentProviderFailureException($"{options.ProviderName} could not prepare its home {options.Home}: {exception.Message}",
                new AgentFailureInfo(AgentFailureKind.Unavailable), exception);
        }

        var events = Channel.CreateUnbounded<Event>(new UnboundedChannelOptions { SingleReader = true });
        CodexAppServerClient? client = null;
        try
        {
            try
            {
                client = await options.ConnectionFactory.StartAsync(
                        new CodexCliConnectionRequest(command, options.Home, options.Settings.Environment, workingDirectory, options.InitializeTimeout),
                        message => OnMessage(client, message, events.Writer),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CodexConnectionClosedException or TimeoutException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException
                or CodexJsonRpcException && !cancellationToken.IsCancellationRequested)
            {
                throw new AgentProviderFailureException($"{options.ProviderName} could not be started. {ex.Message}", new AgentFailureInfo(AgentFailureKind.Unavailable), ex);
            }

            var started = client;
            _ = started.Completion.ContinueWith(
                _ => events.Writer.TryWrite(new ClosedEvent(started.DescribeExit())),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            // The requests that start the turn have the time of one request; the turn itself, until it ends or is stopped.
            using (var requests = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                requests.CancelAfter(options.RequestTimeout);
                await CodexCliAccount.EnsureSignedInAsync(client, options, requests.Token).ConfigureAwait(false);
                var threadId = await StartThreadAsync(client, options, request, workingDirectory, requests.Token).ConfigureAwait(false);
                await StartTurnAsync(client, request, threadId, workingDirectory, requests.Token).ConfigureAwait(false);
            }

            return await ReadTurnAsync(events.Reader, options, onUpdate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is CodexConnectionClosedException or TimeoutException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // A request Codex did not answer in time is a failure, not a stop.
            throw new AgentProviderFailureException($"{options.ProviderName} stopped: {exception.Message}", new AgentFailureInfo(AgentFailureKind.Unavailable), exception);
        }
        catch (CodexJsonRpcException exception)
        {
            throw CodexCliFailures.FromRequest(exception, "answer");
        }
        finally
        {
            // The process ends with the turn, whatever its end: its thread, held in memory only, ends with it.
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // Opens the ephemeral thread, read-only and without tools, with the model of the request.
    private static async Task<string> StartThreadAsync(CodexAppServerClient client, CodexCliRuntimeOptions options, AgentTurnRequest request, string workingDirectory,
        CancellationToken cancellationToken)
    {
        var started = await client.ThreadStartAsync(
                new ThreadStartParams
                {
                    Cwd = workingDirectory,
                    ApprovalPolicy = new AskForApproval.Untrusted(),
                    Sandbox = SandboxMode.ReadOnly,
                    DeveloperInstructions = AgentInstructionText.Compose(request),
                    Config = CreateThreadConfig(workingDirectory),
                    Model = string.IsNullOrWhiteSpace(request.ModelId) ? null : request.ModelId,
                    Ephemeral = true,
                    ServiceName = "codealta",
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!started.Thread.Ephemeral)
        {
            // A Codex that would keep the thread runs no turn in it.
            throw new NotSupportedException($"This version of {options.ProviderName} keeps every thread it opens: it cannot answer a question it does not keep.");
        }

        return started.Thread.Id;
    }

    private static async Task StartTurnAsync(CodexAppServerClient client, AgentTurnRequest request, string threadId, string workingDirectory, CancellationToken cancellationToken)
    {
        var conversation = request.Conversation;
        var earlier = HarnessHistory.WithoutTrailingUserMessages(conversation);
        var preamble = HarnessHistory.CreatePreviousConversation(earlier);
        await client.TurnStartAsync(
                new TurnStartParams
                {
                    ThreadId = threadId,
                    Input = CodexCliPrompts.CreateInput(preamble is null ? [] : [preamble], [.. conversation.Skip(earlier.Count)]),
                    ApprovalPolicy = new AskForApproval.Untrusted(),
                    SandboxPolicy = new SandboxPolicy.ReadOnlySandboxPolicy { NetworkAccess = false },
                    Cwd = workingDirectory,
                    Model = string.IsNullOrWhiteSpace(request.ModelId) ? null : request.ModelId,
                    Effort = CodexCliModels.FromEffort(request.ReasoningEffort) is { } effort ? (ReasoningEffort?)new ReasoningEffort { Value = effort } : null,
                    Summary = request.OmitReasoningSummaries ? new ReasoningSummary.None() : null,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    // Streams the text and the reasoning of the turn until it ends.
    private static async Task<AgentTurnResponse> ReadTurnAsync(ChannelReader<Event> events, CodexCliRuntimeOptions options,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var streamed = new HashSet<string>(StringComparer.Ordinal);
        string? textItem = null;
        while (true)
        {
            var next = await events.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (next is MessageCompletedEvent completed)
            {
                if (streamed.Contains(completed.ItemId) || completed.Text.Length == 0)
                {
                    continue;
                }

                // Codex gave the message whole, without deltas.
                next = new DeltaEvent(AgentContentKind.Assistant, completed.ItemId, completed.Text);
            }

            switch (next)
            {
                case DeltaEvent delta:
                    streamed.Add(delta.ItemId);
                    var value = delta.Text;
                    if (delta.Kind == AgentContentKind.Assistant && textItem != delta.ItemId)
                    {
                        // Another message of the turn is another paragraph of the answer.
                        value = text.Length > 0 ? "\n\n" + value : value;
                        textItem = delta.ItemId;
                    }

                    (delta.Kind == AgentContentKind.Assistant ? text : reasoning).Append(value);
                    var contentId = delta.Kind == AgentContentKind.Assistant ? TextContentId : ReasoningContentId;
                    await onUpdate(new AgentTurnDelta { Kind = delta.Kind, ContentId = contentId, Text = value }, cancellationToken).ConfigureAwait(false);
                    break;
                case TurnEndedEvent { Turn.Status: TurnStatus.Failed } ended:
                    throw CodexCliFailures.FromTurnError(ended.Turn.Error ?? new TurnError { Message = "The turn failed." });
                case TurnEndedEvent:
                    var parts = new List<AgentMessagePart>();
                    var ids = new List<string?>();
                    if (reasoning.Length > 0)
                    {
                        parts.Add(new AgentMessagePart.Reasoning(reasoning.ToString()));
                        ids.Add(ReasoningContentId);
                    }

                    parts.Add(new AgentMessagePart.Text(text.ToString()));
                    ids.Add(TextContentId);
                    return new AgentTurnResponse
                    {
                        AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [.. parts]),
                        AssistantPartContentIds = ids,
                    };
                case ClosedEvent closed:
                    throw new AgentProviderFailureException($"{options.ProviderName} stopped: {closed.Message}", new AgentFailureInfo(AgentFailureKind.Unavailable));
            }
        }
    }

    // Called on the reading loop of the app-server for each of its messages: it must not wait.
    private static void OnMessage(CodexAppServerClient? client, CodexServerMessage message, ChannelWriter<Event> events)
    {
        if (message.RequestId is { } requestId)
        {
            if (client is not null)
            {
                _ = RefuseAsync(client, requestId, message);
            }

            return;
        }

        object? notification;
        try
        {
            notification = CodexMethods.ParseNotification(message.Method, message.Params);
        }
        catch (JsonException)
        {
            return;
        }

        switch (notification)
        {
            case AgentMessageDeltaNotification delta:
                events.TryWrite(new DeltaEvent(AgentContentKind.Assistant, delta.ItemId, delta.Delta));
                break;
            case ReasoningSummaryTextDeltaNotification delta:
                events.TryWrite(new DeltaEvent(AgentContentKind.Reasoning, delta.ItemId, delta.Delta));
                break;
            case ItemCompletedNotification { Item: ThreadItem.AgentMessageThreadItem item }:
                events.TryWrite(new MessageCompletedEvent(item.Id, item.Text));
                break;
            case TurnCompletedNotification completed:
                events.TryWrite(new TurnEndedEvent(completed.Turn));
                break;
        }
    }

    // Refuses what Codex asks: no command, no change, no permission, no question.
    private static async Task RefuseAsync(CodexAppServerClient client, JsonElement requestId, CodexServerMessage message)
    {
        try
        {
            switch (CodexMethods.ParseServerRequest(message.Method, message.Params))
            {
                case CommandExecutionRequestApprovalParams:
                    await client.RespondAsync(requestId, new CommandExecutionRequestApprovalResponse { Decision = new CommandExecutionApprovalDecision.Decline() },
                        CodexProtocolJsonContext.Default.CommandExecutionRequestApprovalResponse).ConfigureAwait(false);
                    break;
                case FileChangeRequestApprovalParams:
                    await client.RespondAsync(requestId, new FileChangeRequestApprovalResponse { Decision = FileChangeApprovalDecision.Decline },
                        CodexProtocolJsonContext.Default.FileChangeRequestApprovalResponse).ConfigureAwait(false);
                    break;
                case PermissionsRequestApprovalParams:
                    await client.RespondAsync(requestId, new PermissionsRequestApprovalResponse { Permissions = new GrantedPermissionProfile(), Scope = PermissionGrantScope.Turn },
                        CodexProtocolJsonContext.Default.PermissionsRequestApprovalResponse).ConfigureAwait(false);
                    break;
                case McpServerElicitationRequestParams:
                    await client.RespondAsync(requestId, new McpServerElicitationRequestResponse { Action = McpServerElicitationAction.Decline },
                        CodexProtocolJsonContext.Default.McpServerElicitationRequestResponse).ConfigureAwait(false);
                    break;
                default:
                    await client.RespondErrorAsync(requestId, CodexErrorCodes.MethodNotFound, $"CodeAlta does not support '{message.Method}' in a question without tools.").ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or ObjectDisposedException or CodexConnectionClosedException)
        {
        }
    }

    // The configuration of the thread, over the config.toml of the home: no web search, the tools of Codex that can be
    // turned off turned off, and the folder untrusted, so that Codex loads nothing of the project.
    private static Dictionary<string, JsonElement> CreateThreadConfig(string workingDirectory)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("web_search", "disabled");
            foreach (var feature in (string[])["shell_tool", "view_image", "sleep_tool", "image_generation"])
            {
                writer.WriteBoolean("features." + feature, false);
            }

            writer.WriteBoolean("tools.update_plan.enabled", false);
            writer.WriteStartObject("projects");
            writer.WriteStartObject(workingDirectory);
            writer.WriteString("trust_level", "untrusted");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.EnumerateObject().ToDictionary(static property => property.Name, static property => property.Value.Clone(), StringComparer.Ordinal);
    }
}