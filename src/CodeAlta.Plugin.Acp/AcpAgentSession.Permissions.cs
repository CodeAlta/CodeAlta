using System.Text.Json;
using CodeAlta.Acp;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Acp;

// The permission requests of the agent, answered by the permission mode of the session or by the user.
internal sealed partial class AcpAgentSession
{
    /// <summary>The permission modes of a session of an ACP provider: what CodeAlta answers without asking.</summary>
    public static IReadOnlyList<string> PermissionModes { get; } = ["default", "acceptEdits", "bypassPermissions"];

    private const int PermissionReasonLimit = 1000;

    private async Task AnswerPermissionAsync(Connection connection, RequestId requestId, JsonElement parameters)
    {
        var key = requestId.Value.GetRawText();
        using var prompt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        connection.PermissionRequests[key] = prompt;
        string? optionId;
        try
        {
            optionId = await DecideAsync(connection, parameters, prompt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The turn was stopped: the request is cancelled, as the protocol asks.
            optionId = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.Warn($"The permission request of {_options.AgentName} could not be answered: {ex.Message}");
            optionId = AcpPermissionOptions.Select(parameters, "reject_once", "reject_always");
        }
        finally
        {
            connection.PermissionRequests.TryRemove(key, out _);
        }

        var outcome = AcpJson.Build(writer =>
        {
            writer.WriteStartObject();
            if (optionId is null)
            {
                writer.WriteString("outcome", "cancelled");
            }
            else
            {
                writer.WriteString("outcome", "selected");
                writer.WriteString("optionId", optionId);
            }

            writer.WriteEndObject();
        });
        try
        {
            await connection.Client.RespondToRequestAsync(requestId, new RequestPermissionResponse { Outcome = new RequestPermissionOutcome { Value = outcome } }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or AcpConnectionClosedException)
        {
        }
    }

    // Returns the option the request is answered with, or null to cancel it.
    private async Task<string?> DecideAsync(Connection connection, JsonElement parameters, CancellationToken cancellationToken)
    {
        AcpJson.TryGetObject(parameters, "toolCall", out var toolCall);
        var callId = AcpJson.GetString(toolCall, "toolCallId");
        var kind = AcpJson.GetString(toolCall, "kind");
        if (callId is not null && _bridge.Find(callId) is { IsCodeAltaTool: true })
        {
            // A tool of CodeAlta asks its own permissions when the session runs it.
            return AcpPermissionOptions.Select(parameters, "allow_once", "allow_always");
        }

        string? permissionMode;
        AgentProviderRunContext? run;
        CodeAlta.Agent.Runtime.Tools.AgentToolInterceptor? interceptor;
        lock (_gate)
        {
            permissionMode = _permissionMode;
            run = _run;
            interceptor = _toolInterceptor;
        }

        // The tool hooks of the session decide before the permission mode does: a call they block is refused, in every mode.
        if (interceptor is not null && await BlockedByToolHooksAsync(connection, interceptor, toolCall, callId, cancellationToken).ConfigureAwait(false) is { } blocked)
        {
            _logger?.Info($"A tool call of {_options.AgentName} was refused by a tool hook: {Limit(blocked)}");
            return AcpPermissionOptions.Select(parameters, "reject_once", "reject_always");
        }

        if (AcpPermissionOptions.AllowsWithoutAsking(permissionMode, kind))
        {
            return AcpPermissionOptions.Select(parameters, "allow_once", "allow_always");
        }

        if (run is null)
        {
            return AcpPermissionOptions.Select(parameters, "reject_once", "reject_always");
        }

        if (callId is not null)
        {
            _bridge.MarkStarted(callId);
        }

        var request = CreatePermissionRequest(connection, toolCall, kind, callId ?? Guid.NewGuid().ToString("N"), parameters);
        var decision = await run.OnPermissionRequest(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return decision.Kind switch
        {
            AgentPermissionDecisionKind.AllowOnce => AcpPermissionOptions.Select(parameters, "allow_once", "allow_always"),
            AgentPermissionDecisionKind.AllowForSession or AgentPermissionDecisionKind.AllowAlways => AcpPermissionOptions.Select(parameters, "allow_always", "allow_once"),
            AgentPermissionDecisionKind.Cancel => null,
            _ => AcpPermissionOptions.Select(parameters, "reject_once", "reject_always"),
        };
    }

    // The protocol only says that the call is refused: the agent does not read the reason of the hook.
    private async Task<string?> BlockedByToolHooksAsync(Connection connection, CodeAlta.Agent.Runtime.Tools.AgentToolInterceptor interceptor,
        JsonElement toolCall, string? callId, CancellationToken cancellationToken)
    {
        try
        {
            var (name, arguments, _) = AcpToolCalls.Map(toolCall, ExposedTools, connection.WorkingDirectory);
            AcpJson.TryGetObject(toolCall, "rawInput", out var input);
            var call = CodeAlta.Agent.Runtime.Tools.AgentToolHookCall.Create(
                new AgentToolInvocation(_providerId, _sessionId, callId ?? Guid.NewGuid().ToString("N"), name, arguments),
                spec: null,
                connection.WorkingDirectory,
                CodeAlta.Agent.Runtime.Tools.AgentToolHookCapabilities.Block,
                runByAgent: true,
                commandDirectory: AcpJson.GetString(input, "cwd"));
            return (await interceptor.BeforeToolCallAsync(call, cancellationToken).ConfigureAwait(false)).BlockMessage;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            // Hooks that fail leave the decision to the permission mode, as without hooks.
            _logger?.Warn($"The tool hooks of a call of {_options.AgentName} failed: {ex.Message}");
            return null;
        }
    }

    private AgentPermissionRequest CreatePermissionRequest(Connection connection, JsonElement toolCall, string? kind, string interactionId, JsonElement parameters)
    {
        var title = AcpJson.GetString(toolCall, "title");
        AcpJson.TryGetObject(toolCall, "rawInput", out var input);
        var reason = Limit(title ?? $"{_options.AgentName} asks to use a tool.");
        switch (kind)
        {
            case "execute":
                return new AgentCommandPermissionRequest(
                    _providerId,
                    _sessionId,
                    DateTimeOffset.UtcNow,
                    RunId: null,
                    InteractionId: interactionId,
                    ApprovalId: null,
                    Command: AcpToolCalls.FindCommand(input) ?? title ?? string.Empty,
                    WorkingDirectory: AcpJson.GetString(input, "cwd") ?? connection.WorkingDirectory,
                    Actions: null,
                    Reason: reason,
                    Network: null,
                    ProposedExecPolicyAmendment: null,
                    ProposedNetworkPolicyAmendments: null);
            case "edit" or "delete" or "move":
                var path = AcpToolCalls.FirstPath(toolCall, input);
                return new AgentFileChangePermissionRequest(
                    _providerId,
                    _sessionId,
                    DateTimeOffset.UtcNow,
                    RunId: null,
                    InteractionId: interactionId,
                    GrantRoot: connection.WorkingDirectory,
                    Reason: path is null ? reason : Limit($"{title ?? _options.AgentName + " edits a file"} ({path})"));
            default:
                // Another kind of action (fetching a page, a tool of an MCP server of the agent): the user sees what
                // the agent says it is about to do, with the input it gives.
                return new AgentCommandPermissionRequest(
                    _providerId,
                    _sessionId,
                    DateTimeOffset.UtcNow,
                    RunId: null,
                    InteractionId: interactionId,
                    ApprovalId: null,
                    Command: title ?? kind ?? "tool",
                    WorkingDirectory: connection.WorkingDirectory,
                    Actions: null,
                    Reason: Limit(input.ValueKind == JsonValueKind.Object ? $"{reason}\n{input.GetRawText()}" : reason),
                    Network: null,
                    ProposedExecPolicyAmendment: null,
                    ProposedNetworkPolicyAmendments: null);
        }
    }

    private static string Limit(string text) => text.Length > PermissionReasonLimit ? text[..PermissionReasonLimit] : text;
}

/// <summary>Chooses among the options of a permission request of an agent.</summary>
internal static class AcpPermissionOptions
{
    /// <summary>Returns the option of the first kind the request offers, else its first option, or null when it offers none.</summary>
    public static string? Select(JsonElement parameters, string kind, string fallbackKind)
    {
        if (!AcpJson.TryGetArray(parameters, "options", out var options))
        {
            return null;
        }

        string? fallback = null;
        string? first = null;
        foreach (var option in options.EnumerateArray())
        {
            var id = AcpJson.GetString(option, "optionId");
            if (id is null)
            {
                continue;
            }

            first ??= id;
            var optionKind = AcpJson.GetString(option, "kind");
            if (string.Equals(optionKind, kind, StringComparison.Ordinal))
            {
                return id;
            }

            if (string.Equals(optionKind, fallbackKind, StringComparison.Ordinal))
            {
                fallback ??= id;
            }
        }

        // An agent whose options name no kind of this side: allowing takes its first option, refusing none.
        return fallback ?? (kind.StartsWith("allow", StringComparison.Ordinal) ? first : null);
    }

    /// <summary>
    /// Whether a permission mode answers a request of a kind of action without asking: reads always, edits in
    /// <c>acceptEdits</c>, everything in <c>bypassPermissions</c>.
    /// </summary>
    public static bool AllowsWithoutAsking(string? permissionMode, string? kind)
        => permissionMode switch
        {
            "bypassPermissions" => true,
            "acceptEdits" => kind is "read" or "search" or "think" or "edit" or "delete" or "move",
            _ => kind is "read" or "search" or "think",
        };
}
