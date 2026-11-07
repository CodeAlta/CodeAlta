using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// Runs a turn that belongs to no session (<see cref="IAgentProviderStandaloneTurns"/>) in a CLI process of its own,
/// started for it and ended with it: without tools, without a transcript, without hooks, and with every permission
/// request refused.
/// </summary>
internal static class ClaudeCodeStandaloneTurn
{
    private const string NoTools = "This turn has no tools: answer from the conversation.";

    /// <summary>Gets the variables that keep the CLI from reading or writing its memory and the instructions of the folder.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> Variables { get; } =
    [
        // The auto memory of the CLI is read into the context and written by a background agent of its own.
        new("CLAUDE_CODE_DISABLE_AUTO_MEMORY", "1"),
        // The conversation of the request is the whole context: not the CLAUDE.md files of the folder.
        new("CLAUDE_CODE_DISABLE_CLAUDE_MDS", "1"),
    ];

    /// <summary>
    /// Starts the CLI for <paramref name="request"/>, sends it its conversation as one prompt, streams the text of the
    /// answer, and stops the process whatever happens.
    /// </summary>
    /// <exception cref="ArgumentException">The conversation of the request does not end with a user message.</exception>
    /// <exception cref="ClaudeCodeNotFoundException">The CLI is not installed.</exception>
    /// <exception cref="NotSupportedException">The CLI refused an option this turn needs (an older version).</exception>
    /// <exception cref="InvalidOperationException">The CLI could not be started, or ended before it answered.</exception>
    /// <exception cref="AgentProviderFailureException">The CLI ended the turn with an error.</exception>
    /// <exception cref="OperationCanceledException">The turn was stopped.</exception>
    public static async Task<AgentTurnResponse> RunAsync(
        ClaudeCodeModelProviderRuntimeOptions options,
        ClaudeCodeOwnProcesses ownProcesses,
        AgentTurnRequest request,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken)
    {
        if (request.Conversation is not [.., { Role: AgentConversationRole.User } prompt])
        {
            throw new ArgumentException("A standalone turn ends with a user message.", nameof(request));
        }

        var resolution = options.ResolveExecutable();
        if (resolution.Path is null)
        {
            throw new ClaudeCodeNotFoundException(resolution.Error ?? "Claude Code was not found.");
        }

        // As for a turn of a session: the CLI would bill an API key of the environment without asking.
        var apiKey = ClaudeCodeApiKey.Decide(options);
        if (apiKey == ClaudeCodeApiKeyDecision.Undecided)
        {
            throw new InvalidOperationException(ClaudeCodeApiKey.UndecidedMessage);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var launch = CreateLaunch(resolution.Path, options, request, withoutApiKey: apiKey == ClaudeCodeApiKeyDecision.Ignore);
        var events = Channel.CreateUnbounded<(string? Type, JsonElement Message)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var handler = new Handler(events.Writer);
        var transport = (options.TransportFactory ?? ClaudeCodeProcessTransportFactory.Instance).Start(launch);
        // Its entry in the registry of live sessions is CodeAlta's own, not another process that holds a conversation.
        var ownProcess = ownProcesses.Track(transport.ProcessId);
        var connection = new ClaudeCodeConnection(transport, handler);
        handler.Connection = connection;
        var running = false;
        var answered = false;
        try
        {
            connection.Start();
            try
            {
                await connection.RequestAsync("initialize", null, options.StartupTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or ClaudeCodeControlException && !cancellationToken.IsCancellationRequested)
            {
                var detail = connection.DescribeExit();
                throw detail.Contains("unknown option", StringComparison.OrdinalIgnoreCase)
                    ? new NotSupportedException($"This version of Claude Code cannot answer without tools and without keeping the conversation. {detail}", exception)
                    : new InvalidOperationException($"Claude Code could not be started. {detail}", exception);
            }

            running = true;
            await SendPromptAsync(connection, request.Conversation, prompt, cancellationToken).ConfigureAwait(false);
            var response = await ReadAnswerAsync(options, connection, events.Reader, onUpdate, cancellationToken).ConfigureAwait(false);
            answered = true;
            return response;
        }
        finally
        {
            if (running && !answered && !connection.IsClosed)
            {
                // The request to the model stops at once; closing the input then ends the process.
                await InterruptAsync(connection, options.InterruptTimeout).ConfigureAwait(false);
            }

            // Closes the input: the CLI exits, or is killed when it does not within a few seconds.
            await connection.DisposeAsync().ConfigureAwait(false);
            ownProcess?.Dispose();
        }
    }

    /// <summary>
    /// Creates the command line of the process: the one of a session, with the model and the effort of the request,
    /// without the tools of CodeAlta, then the options that leave out every tool of the CLI, every MCP server, the hooks,
    /// the skills and the transcript.
    /// </summary>
    internal static ClaudeCodeLaunch CreateLaunch(string executable, ClaudeCodeModelProviderRuntimeOptions options, AgentTurnRequest request, bool withoutApiKey)
    {
        var key = new ClaudeCodeLaunchKey(
            string.IsNullOrWhiteSpace(request.WorkingDirectory) ? null : request.WorkingDirectory,
            ClaudeCodeLauncher.ToModelOption(request.ModelId),
            ClaudeCodeLauncher.ToEffort(request),
            request.Environment is { IsEmpty: false } environment ? environment : null,
            ContextSize: request.ContextSize);
        // In the default mode the CLI asks before any tool runs, and is refused.
        var launch = ClaudeCodeLauncher.Create(executable, options, key, newSessionId: null, resumeSessionId: null, withTools: false,
            withoutApiKey: withoutApiKey, permissionMode: "default");
        List<string> arguments =
        [
            .. launch.Arguments,
            // Nothing is written of the conversation: no transcript to list or resume.
            "--no-session-persistence",
            // No built-in tool, no MCP server of the settings or of claude.ai, no skill.
            "--tools", "",
            "--strict-mcp-config",
            "--disable-slash-commands",
            // The hooks of the settings run commands: none runs.
            "--settings", "{\"disableAllHooks\":true}",
        ];
        if (!string.IsNullOrWhiteSpace(request.SystemMessage))
        {
            // The prompt of Claude Code describes its tools and its ways of working: this turn has its own.
            arguments.Add("--system-prompt");
            arguments.Add(request.SystemMessage.Trim());
        }

        var variables = new Dictionary<string, string?>(launch.Environment, StringComparer.Ordinal);
        foreach (var (name, value) in Variables)
        {
            variables[name] = value;
        }

        return launch with { Arguments = arguments, Environment = variables };
    }

    private static async Task SendPromptAsync(ClaudeCodeConnection connection, IReadOnlyList<AgentConversationMessage> conversation, AgentConversationMessage prompt,
        CancellationToken cancellationToken)
    {
        // What comes before the prompt, if anything, is given with it as context.
        var preamble = conversation.Count > 1 ? ClaudeCodePrompts.CreateHistoryPreamble([.. conversation.Take(conversation.Count - 1)]) : null;
        try
        {
            await connection.SendAsync(writer =>
            {
                writer.WriteString("type", "user");
                writer.WriteString("uuid", Guid.NewGuid().ToString());
                writer.WriteString("session_id", string.Empty);
                writer.WriteNull("parent_tool_use_id");
                writer.WriteStartObject("message");
                writer.WriteString("role", "user");
                ClaudeCodePrompts.WriteUserContent(writer, prompt, preamble);
                writer.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(connection.DescribeExit(), exception);
        }
    }

    private static async Task<AgentTurnResponse> ReadAnswerAsync(
        ClaudeCodeModelProviderRuntimeOptions options,
        ClaudeCodeConnection connection,
        ChannelReader<(string? Type, JsonElement Message)> reader,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken)
    {
        var parts = new List<AgentMessagePart>();
        (string Kind, string Text)? apiError = null;
        string? messageId = null;
        while (true)
        {
            (string? Type, JsonElement Message) @event;
            try
            {
                @event = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                throw new InvalidOperationException(connection.DescribeExit());
            }

            var message = @event.Message;
            switch (@event.Type)
            {
                case null:
                    // The process ended before the result.
                    throw new InvalidOperationException(connection.DescribeExit());
                case "stream_event" when ClaudeCodeJson.TryGetObject(message, "event", out var stream):
                    if (ClaudeCodeJson.GetString(stream, "type") == "message_start")
                    {
                        messageId = ClaudeCodeJson.TryGetObject(stream, "message", out var started) ? ClaudeCodeJson.GetString(started, "id") : null;
                    }
                    else if (ClaudeCodeJson.GetString(stream, "type") == "content_block_delta" &&
                             ClaudeCodeJson.GetInt64(stream, "index") is { } index &&
                             ClaudeCodeJson.TryGetObject(stream, "delta", out var delta) &&
                             ClaudeCodeJson.GetString(delta, "type") == "text_delta" &&
                             ClaudeCodeJson.GetString(delta, "text") is { Length: > 0 } text)
                    {
                        // As a session streams it: the identifier names the block of the message.
                        await onUpdate(new AgentTurnDelta
                        {
                            Kind = AgentContentKind.Assistant,
                            ContentId = $"claude:{messageId ?? "message"}:{index.ToString(CultureInfo.InvariantCulture)}",
                            Text = text,
                        }, cancellationToken).ConfigureAwait(false);
                    }

                    break;
                case "assistant" when ClaudeCodeJson.TryGetObject(message, "message", out var assistant) &&
                                      ClaudeCodeJson.TryGetArray(assistant, "content", out var content):
                    var error = ClaudeCodeJson.GetString(message, "error");
                    foreach (var block in content.EnumerateArray())
                    {
                        if (ClaudeCodeJson.GetString(block, "type") == "text" && ClaudeCodeJson.GetString(block, "text") is { } blockText &&
                            !string.IsNullOrWhiteSpace(blockText))
                        {
                            // The CLI writes a failed request as an assistant text: it is an error, not an answer.
                            if (error is not null) apiError = (error, blockText);
                            else parts.Add(new AgentMessagePart.Text(blockText));
                        }
                    }

                    if (error is not null) apiError ??= (error, string.Empty);
                    break;
                case "result":
                    var subtype = ClaudeCodeJson.GetString(message, "subtype");
                    if (ClaudeCodeJson.GetBoolean(message, "is_error") || (subtype is not null && subtype.StartsWith("error", StringComparison.Ordinal)))
                    {
                        throw ClaudeCodeSession.CreateFailure(options, message, subtype, apiError, rejected: null, sandboxedOnMacOS: false);
                    }

                    if (parts.Count == 0 && ClaudeCodeJson.GetString(message, "result") is { } result && !string.IsNullOrWhiteSpace(result))
                    {
                        parts.Add(new AgentMessagePart.Text(result));
                    }

                    return new AgentTurnResponse { AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, parts) };
            }
        }
    }

    private static async Task InterruptAsync(ClaudeCodeConnection connection, TimeSpan timeout)
    {
        try
        {
            await connection.RequestAsync("interrupt", null, timeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or ClaudeCodeControlException or OperationCanceledException)
        {
            // The process is ended all the same.
        }
    }

    // Hands the messages to the turn, and refuses at once what the CLI asks: a permission, a hook, an MCP message.
    private sealed class Handler(ChannelWriter<(string? Type, JsonElement Message)> events) : IClaudeCodeConnectionHandler
    {
        public ClaudeCodeConnection? Connection { get; set; }

        public void OnMessage(string type, JsonElement message) => events.TryWrite((type, message));

        public void OnControlRequest(string requestId, string subtype, JsonElement request)
        {
            if (Connection is not { } connection)
            {
                return;
            }

            _ = AnswerAsync(subtype == "can_use_tool"
                ? connection.RespondAsync(requestId, static writer =>
                {
                    writer.WriteString("behavior", "deny");
                    writer.WriteString("message", NoTools);
                })
                : connection.RespondErrorAsync(requestId, NoTools));
        }

        public void OnControlCancel(string requestId)
        {
        }

        public void OnClosed(Exception? failure)
        {
            events.TryWrite((null, default));
            events.TryComplete();
        }

        private static async Task AnswerAsync(ValueTask answer)
        {
            try
            {
                await answer.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The process ended meanwhile.
            }
        }
    }
}
