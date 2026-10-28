using System.Buffers;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Tools;
using CodeAlta.CodexAppServer;
using CodeAlta.CodexAppServer.Protocol;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.CodexCli;

// The requests of Codex: its approvals, answered by the permission mode of the session or by the user, and what CodeAlta
// does not take (questions, elicitations, permissions of Codex's own sandbox).
internal sealed partial class CodexCliSession
{
    /// <summary>The permission modes of a session of a Codex CLI provider: what CodeAlta answers without asking.</summary>
    public static IReadOnlyList<string> PermissionModes { get; } = ["default", "acceptEdits", "bypassPermissions"];

    private const int PermissionReasonLimit = 1000;

    private enum Decision
    {
        Accept,
        AcceptForSession,
        Decline,
        Cancel,
    }

    private void OnServerRequest(Connection connection, JsonElement requestId, CodexServerMessage message)
    {
        object? parameters;
        try
        {
            parameters = CodexMethods.ParseServerRequest(message.Method, message.Params);
        }
        catch (JsonException ex)
        {
            _ = RespondErrorAsync(connection, requestId, CodexErrorCodes.InvalidParams, $"CodeAlta could not read '{message.Method}': {ex.Message}");
            return;
        }

        switch (parameters)
        {
            case CommandExecutionRequestApprovalParams command:
                _ = AnswerApprovalAsync(connection, requestId, command.ItemId, (cancellationToken) => DecideCommandAsync(connection, command, cancellationToken),
                    decision => connection.Client.RespondAsync(requestId, new CommandExecutionRequestApprovalResponse { Decision = CommandDecisionOf(decision) },
                        CodexProtocolJsonContext.Default.CommandExecutionRequestApprovalResponse));
                break;
            case FileChangeRequestApprovalParams change:
                _ = AnswerApprovalAsync(connection, requestId, change.ItemId, (cancellationToken) => DecideFileChangeAsync(connection, change, cancellationToken),
                    decision => connection.Client.RespondAsync(requestId, new FileChangeRequestApprovalResponse { Decision = FileDecisionOf(decision) },
                        CodexProtocolJsonContext.Default.FileChangeRequestApprovalResponse));
                break;
            case PermissionsRequestApprovalParams:
                // Codex's own sandbox is off: it is granted nothing more, CodeAlta's container is the boundary.
                _ = Respond(() => connection.Client.RespondAsync(requestId, new PermissionsRequestApprovalResponse { Permissions = new GrantedPermissionProfile(), Scope = PermissionGrantScope.Turn },
                    CodexProtocolJsonContext.Default.PermissionsRequestApprovalResponse));
                break;
            case McpServerElicitationRequestParams:
                _ = Respond(() => connection.Client.RespondAsync(requestId, new McpServerElicitationRequestResponse { Action = McpServerElicitationAction.Decline },
                    CodexProtocolJsonContext.Default.McpServerElicitationRequestResponse));
                break;
            default:
                // Questions to the user go through the request_user_input tool of CodeAlta; nothing else is answered.
                _ = RespondErrorAsync(connection, requestId, CodexErrorCodes.MethodNotFound, $"CodeAlta does not support '{message.Method}'.");
                break;
        }
    }

    private static async Task Respond(Func<Task> respond)
    {
        try
        {
            await respond().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or CodexConnectionClosedException)
        {
        }
    }

    private async Task AnswerApprovalAsync(Connection connection, JsonElement requestId, string itemId, Func<CancellationToken, Task<Decision>> decide, Func<Decision, Task> respond)
    {
        var key = requestId.GetRawText();
        using var prompt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        connection.Approvals[key] = prompt;
        Decision decision;
        try
        {
            decision = await decide(prompt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The turn was stopped: the request is cancelled.
            decision = Decision.Cancel;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.Warn($"An approval request of {_options.ProviderName} could not be answered: {ex.Message}");
            decision = Decision.Decline;
        }
        finally
        {
            connection.Approvals.TryRemove(key, out _);
        }

        _bridge.MarkStarted(itemId);
        await Respond(() => respond(decision)).ConfigureAwait(false);
    }

    private async Task<Decision> DecideCommandAsync(Connection connection, CommandExecutionRequestApprovalParams parameters, CancellationToken cancellationToken)
    {
        var (permissionMode, run, interceptor) = Snapshot();
        var command = parameters.Command ?? string.Empty;
        var cwd = parameters.Cwd?.Value ?? connection.WorkingDirectory;
        if (interceptor is not null && await BlockedByToolHooksAsync(connection, interceptor, parameters.ItemId, "shell",
                Build(writer => { writer.WriteString("command", command); writer.WriteString("cwd", cwd); }), cwd, cancellationToken).ConfigureAwait(false) is { } blocked)
        {
            _logger?.Info($"A command of {_options.ProviderName} was refused by a tool hook: {Limit(blocked)}");
            return Decision.Decline;
        }

        if (permissionMode == "bypassPermissions")
        {
            return Decision.Accept;
        }

        if (run is null)
        {
            return Decision.Decline;
        }

        var request = new AgentCommandPermissionRequest(
            _providerId,
            _sessionId,
            DateTimeOffset.UtcNow,
            RunId: null,
            InteractionId: parameters.ItemId,
            ApprovalId: parameters.ApprovalId,
            Command: command,
            WorkingDirectory: cwd,
            Actions: null,
            Reason: Limit(parameters.Reason ?? $"{_options.ProviderName} asks to run a command."),
            Network: null,
            ProposedExecPolicyAmendment: null,
            ProposedNetworkPolicyAmendments: null);
        return DecisionOf(await run.OnPermissionRequest(request, cancellationToken).ConfigureAwait(false));
    }

    private async Task<Decision> DecideFileChangeAsync(Connection connection, FileChangeRequestApprovalParams parameters, CancellationToken cancellationToken)
    {
        var (permissionMode, run, interceptor) = Snapshot();
        var call = _bridge.Find(parameters.ItemId);
        if (interceptor is not null && await BlockedByToolHooksAsync(connection, interceptor, parameters.ItemId, call?.Name ?? "edit",
                call?.Arguments ?? Build(static _ => { }), connection.WorkingDirectory, cancellationToken).ConfigureAwait(false) is { } blocked)
        {
            _logger?.Info($"A change of {_options.ProviderName} was refused by a tool hook: {Limit(blocked)}");
            return Decision.Decline;
        }

        if (permissionMode is "bypassPermissions" or "acceptEdits")
        {
            return Decision.Accept;
        }

        if (run is null)
        {
            return Decision.Decline;
        }

        var path = call?.Arguments.TryGetProperty("file_path", out var file) == true && file.ValueKind == JsonValueKind.String ? file.GetString() : null;
        var reason = parameters.Reason ?? $"{_options.ProviderName} edits {(path is null ? "files" : "a file")}";
        var request = new AgentFileChangePermissionRequest(
            _providerId,
            _sessionId,
            DateTimeOffset.UtcNow,
            RunId: null,
            InteractionId: parameters.ItemId,
            GrantRoot: parameters.GrantRoot ?? connection.WorkingDirectory,
            Reason: Limit(path is null ? reason : $"{reason} ({path})"));
        return DecisionOf(await run.OnPermissionRequest(request, cancellationToken).ConfigureAwait(false));
    }

    private (string? PermissionMode, AgentProviderRunContext? Run, AgentToolInterceptor? Interceptor) Snapshot()
    {
        lock (_gate)
        {
            return (_permissionMode, _run, _toolInterceptor);
        }
    }

    // The tool hooks of the session decide before the permission mode does: a call they block is refused, in every mode.
    private async Task<string?> BlockedByToolHooksAsync(Connection connection, AgentToolInterceptor interceptor, string callId, string name, JsonElement arguments,
        string commandDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var call = AgentToolHookCall.Create(
                new AgentToolInvocation(_providerId, _sessionId, callId, name, arguments),
                spec: null,
                connection.WorkingDirectory,
                AgentToolHookCapabilities.Block,
                runByAgent: true,
                commandDirectory: commandDirectory);
            return (await interceptor.BeforeToolCallAsync(call, cancellationToken).ConfigureAwait(false)).BlockMessage;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            // Hooks that fail leave the decision to the permission mode, as without hooks.
            _logger?.Warn($"The tool hooks of a call of {_options.ProviderName} failed: {ex.Message}");
            return null;
        }
    }

    private static Decision DecisionOf(AgentPermissionDecision decision) => decision.Kind switch
    {
        AgentPermissionDecisionKind.AllowOnce => Decision.Accept,
        AgentPermissionDecisionKind.AllowForSession or AgentPermissionDecisionKind.AllowAlways => Decision.AcceptForSession,
        AgentPermissionDecisionKind.Cancel => Decision.Cancel,
        _ => Decision.Decline,
    };

    private static CommandExecutionApprovalDecision CommandDecisionOf(Decision decision) => decision switch
    {
        Decision.Accept => new CommandExecutionApprovalDecision.Accept(),
        Decision.AcceptForSession => new CommandExecutionApprovalDecision.AcceptForSession(),
        Decision.Cancel => new CommandExecutionApprovalDecision.Cancel(),
        _ => new CommandExecutionApprovalDecision.Decline(),
    };

    private static FileChangeApprovalDecision FileDecisionOf(Decision decision) => decision switch
    {
        Decision.Accept => FileChangeApprovalDecision.Accept,
        Decision.AcceptForSession => FileChangeApprovalDecision.AcceptForSession,
        Decision.Cancel => FileChangeApprovalDecision.Cancel,
        _ => FileChangeApprovalDecision.Decline,
    };

    private static string Limit(string text) => text.Length > PermissionReasonLimit ? text[..PermissionReasonLimit] : text;

    private static JsonElement Build(Action<Utf8JsonWriter> writeProperties)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
