using System.Text.Json;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Claude;

// The requests of the CLI that the host of CodeAlta or one of its tools answers.
internal sealed partial class ClaudeCodeSession
{
    private const int PermissionReasonLimit = 1000;

    // Answers a permission prompt of the CLI with the permission policy of CodeAlta. The CLI only prompts for
    // what the settings of the user neither allow nor deny.
    private async Task<Action<Utf8JsonWriter>?> HandlePermissionAsync(string requestId, JsonElement request)
    {
        var toolName = ClaudeCodeJson.GetString(request, "tool_name") ?? string.Empty;
        var input = request.TryGetProperty("input", out var inputElement) && inputElement.ValueKind == JsonValueKind.Object
            ? inputElement
            : ClaudeCodeJson.EmptyObject;
        if (toolName.StartsWith(ClaudeCodeLauncher.McpToolPrefix, StringComparison.Ordinal))
        {
            // A tool of CodeAlta asks its own permissions when the session runs it.
            return Allow(input, default);
        }

        AgentProviderRunContext? run;
        var prompt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        lock (_gate)
        {
            run = _run;
            _permissionPrompts[requestId] = prompt;
        }

        try
        {
            var interactionId = ClaudeCodeJson.GetString(request, "tool_use_id") ?? requestId;
            if (string.Equals(toolName, "AskUserQuestion", StringComparison.Ordinal))
            {
                return await AskUserAsync(run, interactionId, input, prompt.Token).ConfigureAwait(false);
            }

            if (string.Equals(toolName, "ExitPlanMode", StringComparison.Ordinal) &&
                string.Equals(_options.PermissionMode?.Trim(), "plan", StringComparison.OrdinalIgnoreCase))
            {
                // Allowing it tells the model that the user approved its plan. Nobody did, and the session was
                // put in that mode on purpose. A session that was not is let out of it, below.
                return Deny(
                    "This session is in the plan mode of Claude Code by the configuration of its CodeAlta provider (`permission_mode`): it is not left from within the session, and the user approved nothing. Give the plan as your answer.",
                    interrupt: false);
            }

            var permission = CreatePermissionRequest(toolName, input, request, interactionId);
            if (permission is null)
            {
                // CodeAlta gates commands and file changes, as it does for its own tools.
                return Allow(input, default);
            }

            if (run is null)
            {
                return Deny("No run of CodeAlta is active for this session.", interrupt: false);
            }

            var decision = await run.OnPermissionRequest(permission, prompt.Token).ConfigureAwait(false);
            return decision.Kind switch
            {
                AgentPermissionDecisionKind.AllowOnce => Allow(input, default),
                AgentPermissionDecisionKind.AllowForSession => Allow(input, request.TryGetProperty("permission_suggestions", out var suggestions) ? suggestions : default),
                AgentPermissionDecisionKind.Cancel => Deny("The user cancelled this action in CodeAlta.", interrupt: true),
                _ => Deny("This action was denied in CodeAlta.", interrupt: false),
            };
        }
        catch (OperationCanceledException)
        {
            // The CLI withdrew the prompt, or the turn was interrupted: nothing waits for an answer.
            return null;
        }
        finally
        {
            lock (_gate)
            {
                _permissionPrompts.Remove(requestId);
            }

            prompt.Dispose();
        }
    }

    private AgentPermissionRequest? CreatePermissionRequest(string toolName, JsonElement input, JsonElement request, string interactionId)
    {
        var workingDirectory = _launchKey?.WorkingDirectory ?? Environment.CurrentDirectory;
        switch (toolName)
        {
            case "Bash":
            case "PowerShell":
                var reason = ClaudeCodeJson.GetString(input, "description")
                             ?? ClaudeCodeJson.GetString(request, "description")
                             ?? ClaudeCodeJson.GetString(request, "title")
                             ?? "Claude Code requested local shell execution.";
                return new AgentCommandPermissionRequest(
                    _providerId,
                    _sessionId,
                    DateTimeOffset.UtcNow,
                    RunId: null,
                    InteractionId: interactionId,
                    ApprovalId: null,
                    Command: ClaudeCodeJson.GetString(input, "command") ?? string.Empty,
                    WorkingDirectory: workingDirectory,
                    Actions: null,
                    Reason: reason.Length > PermissionReasonLimit ? reason[..PermissionReasonLimit] : reason,
                    Network: null,
                    ProposedExecPolicyAmendment: null,
                    ProposedNetworkPolicyAmendments: null);
            case "Edit":
            case "MultiEdit":
            case "Write":
            case "NotebookEdit":
                var path = ClaudeCodeJson.GetString(input, "file_path") ?? ClaudeCodeJson.GetString(input, "notebook_path");
                return new AgentFileChangePermissionRequest(
                    _providerId,
                    _sessionId,
                    DateTimeOffset.UtcNow,
                    RunId: null,
                    InteractionId: interactionId,
                    GrantRoot: workingDirectory,
                    Reason: path is null
                        ? $"Claude Code requested filesystem edits via {toolName}."
                        : $"Claude Code requested filesystem edits via {toolName} affecting '{path}'.");
            default:
                return null;
        }
    }

    // The question tool of Claude Code is answered through the question form of CodeAlta.
    private async Task<Action<Utf8JsonWriter>?> AskUserAsync(AgentProviderRunContext? run, string interactionId, JsonElement input, CancellationToken cancellationToken)
    {
        if (run?.OnUserInputRequest is not { } ask)
        {
            // The run does not take live questions (the desktop application asks with `alta ask`): the refusal
            // names the way that works, so that the model does not conclude that nothing can be asked.
            bool hasGateway;
            lock (_gate)
            {
                hasGateway = _exposedTools.ContainsKey(ClaudeCodePrompts.GatewayTool);
            }

            return Deny(
                hasGateway
                    ? $"CodeAlta does not take questions through this tool in this run. To ask the user, call `{ClaudeCodeLauncher.McpToolPrefix}{ClaudeCodePrompts.GatewayTool}` with the args `ask --stdin` (`ask --help` gives the form), within the rules its instructions give for asking. Otherwise continue with your best judgment and state your assumptions."
                    : "Questions cannot be asked in this session. Continue with your best judgment and state your assumptions.",
                interrupt: false);
        }

        if (!ClaudeCodeJson.TryGetArray(input, "questions", out var questions) || questions.GetArrayLength() == 0)
        {
            return Deny("The question tool was called without a question.", interrupt: false);
        }

        var prompts = new List<AgentUserInputPrompt>();
        var texts = new List<string>();
        foreach (var question in questions.EnumerateArray())
        {
            var text = ClaudeCodeJson.GetString(question, "question");
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            List<AgentUserInputOption>? options = null;
            if (ClaudeCodeJson.TryGetArray(question, "options", out var optionElements))
            {
                options = [];
                foreach (var option in optionElements.EnumerateArray())
                {
                    if (ClaudeCodeJson.GetString(option, "label") is { Length: > 0 } label)
                    {
                        options.Add(new AgentUserInputOption(label, ClaudeCodeJson.GetString(option, "description")));
                    }
                }
            }

            prompts.Add(new AgentUserInputPrompt(
                $"question_{prompts.Count + 1}",
                text,
                ClaudeCodeJson.GetString(question, "header"),
                options is { Count: > 0 } ? options : null));
            texts.Add(text);
        }

        if (prompts.Count == 0)
        {
            return Deny("The question could not be read.", interrupt: false);
        }

        var response = await ask(
                new AgentUserInputRequest(_providerId, _sessionId, DateTimeOffset.UtcNow, run.RunId, interactionId, new AgentUserInputForm(prompts)),
                cancellationToken)
            .ConfigureAwait(false);

        var answers = new List<KeyValuePair<string, string>>(prompts.Count);
        for (var index = 0; index < prompts.Count; index++)
        {
            if (response.Answers.TryGetValue(prompts[index].Id, out var answer) && !string.IsNullOrWhiteSpace(answer))
            {
                answers.Add(new(texts[index], answer));
            }
        }

        if (answers.Count == 0)
        {
            return Deny("The user did not answer. Continue with your best judgment and state your assumptions.", interrupt: false);
        }

        // The tool returns the answers it is given back with its input.
        return writer =>
        {
            writer.WriteString("behavior", "allow");
            writer.WriteStartObject("updatedInput");
            foreach (var property in input.EnumerateObject())
            {
                if (!property.NameEquals("answers"))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteStartObject("answers");
            foreach (var (question, answer) in answers)
            {
                writer.WriteString(question, answer);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        };
    }

    private static Action<Utf8JsonWriter> Allow(JsonElement input, JsonElement permissionSuggestions)
        => writer =>
        {
            writer.WriteString("behavior", "allow");
            writer.WritePropertyName("updatedInput");
            input.WriteTo(writer);
            if (permissionSuggestions.ValueKind == JsonValueKind.Array)
            {
                // "Allow for the session": the rules the CLI proposed for this prompt are applied.
                writer.WritePropertyName("updatedPermissions");
                permissionSuggestions.WriteTo(writer);
            }
        };

    private static Action<Utf8JsonWriter> Deny(string message, bool interrupt)
        => writer =>
        {
            writer.WriteString("behavior", "deny");
            writer.WriteString("message", message);
            if (interrupt)
            {
                writer.WriteBoolean("interrupt", true);
            }
        };

    private void CancelPermissionPrompt(string requestId)
    {
        lock (_gate)
        {
            if (_permissionPrompts.TryGetValue(requestId, out var prompt))
            {
                try
                {
                    prompt.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    private void CancelPermissionPrompts()
    {
        CancellationTokenSource[] prompts;
        lock (_gate)
        {
            prompts = [.. _permissionPrompts.Values];
        }

        foreach (var prompt in prompts)
        {
            try
            {
                prompt.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    // The MCP server of CodeAlta, served over the control protocol. Returns whether a call was given to the
    // session of CodeAlta to run.
    private bool HandleMcpMessage(string requestId, JsonElement request)
    {
        if (!ClaudeCodeJson.TryGetObject(request, "message", out var message))
        {
            _ = SendSafelyAsync(connection => connection.RespondErrorAsync(requestId, "The MCP message is missing."));
            return false;
        }

        JsonElement? id = message.TryGetProperty("id", out var idElement) && idElement.ValueKind is JsonValueKind.Number or JsonValueKind.String
            ? idElement
            : null;
        var method = ClaudeCodeJson.GetString(message, "method");
        if (id is null)
        {
            // A notification has no answer of its own: the request that carried it is acknowledged.
            if (string.Equals(method, "notifications/initialized", StringComparison.Ordinal))
            {
                lock (_gate)
                {
                    _mcpInitialized = true;
                }
            }

            _ = SendSafelyAsync(connection => connection.RespondAsync(requestId, static writer =>
            {
                writer.WriteStartObject("mcp_response");
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteStartObject("result");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }));
            return false;
        }

        ClaudeCodeJson.TryGetObject(message, "params", out var parameters);
        switch (method)
        {
            case "initialize":
                var protocolVersion = ClaudeCodeJson.GetString(parameters, "protocolVersion") ?? "2025-06-18";
                RespondMcp(requestId, id, writer =>
                {
                    writer.WriteString("protocolVersion", protocolVersion);
                    writer.WriteStartObject("capabilities");
                    writer.WriteStartObject("tools");
                    writer.WriteBoolean("listChanged", true);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteStartObject("serverInfo");
                    writer.WriteString("name", ClaudeCodeLauncher.McpServerName);
                    writer.WriteString("title", "CodeAlta");
                    writer.WriteString("version", "1");
                    writer.WriteEndObject();
                });
                return false;
            case "ping":
                RespondMcp(requestId, id, static _ => { });
                return false;
            case "tools/list":
                IReadOnlyDictionary<string, AgentToolDefinition> tools;
                lock (_gate)
                {
                    tools = _exposedTools;
                    _mcpInitialized = true;
                }

                RespondMcp(requestId, id, writer =>
                {
                    writer.WriteStartArray("tools");
                    foreach (var (name, definition) in tools)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", name);
                        writer.WriteString("description", definition.Spec.Description);
                        writer.WritePropertyName("inputSchema");
                        if (definition.Spec.InputSchema.ValueKind == JsonValueKind.Object)
                        {
                            definition.Spec.InputSchema.WriteTo(writer);
                        }
                        else
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "object");
                            writer.WriteEndObject();
                        }

                        // Claude Code defers the tools of an MCP server until the model searches for them. CodeAlta
                        // already decides which tools a session carries (the UI tools and the tools of its MCP
                        // servers join when the session asks for them), and its instructions name them as tools
                        // that are there: none is deferred a second time.
                        writer.WriteStartObject("_meta");
                        writer.WriteBoolean("anthropic/alwaysLoad", true);
                        writer.WriteEndObject();

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                });
                return false;
            case "tools/call":
                return HandleMcpToolCall(requestId, id, parameters);
            default:
                _ = SendSafelyAsync(connection => connection.RespondAsync(requestId, writer =>
                {
                    writer.WriteStartObject("mcp_response");
                    writer.WriteString("jsonrpc", "2.0");
                    writer.WritePropertyName("id");
                    id.Value.WriteTo(writer);
                    writer.WriteStartObject("error");
                    writer.WriteNumber("code", -32601);
                    writer.WriteString("message", $"Method '{method}' is not supported.");
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }));
                return false;
        }
    }

    private bool HandleMcpToolCall(string requestId, JsonElement? id, JsonElement parameters)
    {
        var name = ClaudeCodeJson.GetString(parameters, "name") ?? string.Empty;
        var arguments = parameters.ValueKind == JsonValueKind.Object &&
                        parameters.TryGetProperty("arguments", out var argumentsElement) &&
                        argumentsElement.ValueKind == JsonValueKind.Object
            ? argumentsElement
            : ClaudeCodeJson.EmptyObject;
        var toolUseId = ClaudeCodeJson.TryGetObject(parameters, "_meta", out var meta)
            ? ClaudeCodeJson.GetString(meta, "claudecode/toolUseId")
            : null;
        var call = new McpToolCall(requestId, id, name, arguments);

        // The call belongs to a tool call of the main conversation, which the session of CodeAlta runs and
        // records, or to a subagent of the CLI, for which it is run here.
        ToolCallState? owner = null;
        AgentToolDefinition? definition;
        lock (_gate)
        {
            if (toolUseId is not null)
            {
                if (_toolCalls.TryGetValue(toolUseId, out var named) && named is { IsNative: true, McpMatched: false })
                {
                    owner = named;
                }
            }
            else
            {
                ToolCallState? byName = null;
                foreach (var candidate in _toolCallOrder)
                {
                    if (!candidate.IsNative ||
                        candidate.McpMatched ||
                        candidate.Result.Task.IsCompleted ||
                        !string.Equals(candidate.Name, name, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (ClaudeCodeJson.DeepEquals(candidate.Arguments, arguments))
                    {
                        owner = candidate;
                        break;
                    }

                    byName ??= candidate;
                }

                owner ??= byName;
            }

            if (owner is not null)
            {
                owner.McpMatched = true;
            }

            _exposedTools.TryGetValue(name, out definition);
        }

        if (owner is not null)
        {
            owner.McpCall.TrySetResult(call);
            return true;
        }

        _ = RunDetachedToolAsync(call, definition);
        return false;
    }

    private async Task RunDetachedToolAsync(McpToolCall call, AgentToolDefinition? definition)
    {
        AgentToolResult result;
        if (definition is null)
        {
            result = Failure($"Tool '{call.Name}' is not available in this session.");
        }
        else
        {
            try
            {
                result = await definition.Handler(
                        new AgentToolInvocation(_providerId, _sessionId, $"claude-mcp-{Guid.NewGuid():N}", definition.Spec.Name, call.Arguments),
                        _lifetime.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = Failure($"Tool '{call.Name}' failed: {ex.Message}");
            }
        }

        await RespondToolResultAsync(call, result).ConfigureAwait(false);
    }

    private Task RespondToolResultAsync(McpToolCall call, AgentToolResult result)
        => SendSafelyAsync(connection => connection.RespondAsync(call.ControlRequestId, writer =>
        {
            writer.WriteStartObject("mcp_response");
            writer.WriteString("jsonrpc", "2.0");
            if (call.JsonRpcId is { } id)
            {
                writer.WritePropertyName("id");
                id.WriteTo(writer);
            }

            writer.WriteStartObject("result");
            writer.WriteStartArray("content");
            var written = 0;
            foreach (var item in result.Items)
            {
                switch (item)
                {
                    case AgentToolResultItem.Text text:
                        WriteMcpText(writer, text.Value);
                        written++;
                        break;
                    case AgentToolResultItem.ImageUrl imageUrl:
                        WriteMcpText(writer, imageUrl.Url);
                        written++;
                        break;
                    case AgentToolResultItem.Image image:
                        WriteMcpImage(writer, image.Base64Data, image.MediaType);
                        written++;
                        break;
                    case AgentToolResultItem.LocalImage localImage:
                        try
                        {
                            WriteMcpImage(writer, Convert.ToBase64String(File.ReadAllBytes(localImage.Path)), localImage.MediaType);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            WriteMcpText(writer, $"[image {localImage.Path} could not be read: {ex.Message}]");
                        }

                        written++;
                        break;
                }
            }

            if (written == 0)
            {
                WriteMcpText(writer, result.Error ?? (result.Success ? "(no output)" : "The tool call failed."));
            }

            writer.WriteEndArray();
            writer.WriteBoolean("isError", !result.Success);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }));

    private void RespondMcp(string requestId, JsonElement? id, Action<Utf8JsonWriter> writeResult)
        => _ = SendSafelyAsync(connection => connection.RespondAsync(requestId, writer =>
        {
            writer.WriteStartObject("mcp_response");
            writer.WriteString("jsonrpc", "2.0");
            if (id is { } value)
            {
                writer.WritePropertyName("id");
                value.WriteTo(writer);
            }

            writer.WriteStartObject("result");
            writeResult(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }));

    private static void WriteMcpText(Utf8JsonWriter writer, string text)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", text);
        writer.WriteEndObject();
    }

    private static void WriteMcpImage(Utf8JsonWriter writer, string base64Data, string mediaType)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "image");
        writer.WriteString("data", base64Data);
        writer.WriteString("mimeType", mediaType);
        writer.WriteEndObject();
    }
}
