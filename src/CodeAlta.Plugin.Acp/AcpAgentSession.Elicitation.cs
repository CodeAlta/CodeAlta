using System.Text.Json;
using CodeAlta.Acp;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Acp;

// The elicitation requests of the agent (`elicitation/create`), presented interactively to the user so the turn continues.
internal sealed partial class AcpAgentSession
{
    private const int MaxValidationAttempts = 3;

    private async Task AnswerElicitationAsync(Connection connection, RequestId requestId, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            await RespondErrorAsync(connection, requestId, AcpErrorCodes.InvalidParams, "The elicitation request has no parameters.").ConfigureAwait(false);
            return;
        }

        var mode = AcpJson.GetString(parameters, "mode");
        if (!string.Equals(mode, "form", StringComparison.Ordinal) || !connection.ElicitationFormEnabled)
        {
            // ACP Elicitation spec: requests using a mode the client has not advertised produce JSON-RPC -32602.
            await RespondErrorAsync(connection, requestId, AcpErrorCodes.InvalidParams,
                $"CodeAlta did not advertise support for '{mode ?? "(missing)"}' elicitation mode.").ConfigureAwait(false);
            return;
        }

        var sessionId = AcpJson.GetString(parameters, "sessionId");
        var hasSessionId = !string.IsNullOrEmpty(sessionId);
        var hasRequestId = parameters.TryGetProperty("requestId", out var scopeRequestId) &&
            scopeRequestId.ValueKind is JsonValueKind.String or JsonValueKind.Number;
        if (!hasSessionId && !hasRequestId)
        {
            await RespondErrorAsync(connection, requestId, AcpErrorCodes.InvalidParams,
                "The elicitation request must specify either sessionId or requestId.").ConfigureAwait(false);
            return;
        }

        if (hasSessionId && connection.AgentSessionId.Length > 0 &&
            !string.Equals(sessionId, connection.AgentSessionId, StringComparison.Ordinal))
        {
            await RespondErrorAsync(connection, requestId, AcpErrorCodes.InvalidParams,
                $"Unknown session '{sessionId}' for elicitation request.").ConfigureAwait(false);
            return;
        }

        var form = AcpElicitation.ParseForm(parameters);
        if (form is null)
        {
            await RespondErrorAsync(connection, requestId, AcpErrorCodes.InvalidParams,
                "The form elicitation request has an unsupported or empty schema.").ConfigureAwait(false);
            return;
        }

        // Request-scoped elicitations (no sessionId) and standalone turns are not tied to an interactive session prompt.
        if (Standalone || !hasSessionId)
        {
            await RespondElicitationAsync(connection, requestId, AcpElicitation.CreateActionResponse("cancel")).ConfigureAwait(false);
            return;
        }

        if (AcpJson.GetString(parameters, "toolCallId") is { Length: > 0 } toolCallId)
        {
            _bridge.MarkStarted(toolCallId);
        }

        AgentProviderRunContext? run;
        lock (_gate)
        {
            run = _run;
        }

        if (run is null || (run.OnInteractiveAsk is null && run.OnUserInputRequest is null))
        {
            await RespondElicitationAsync(connection, requestId, AcpElicitation.CreateActionResponse("cancel")).ConfigureAwait(false);
            return;
        }

        var key = RequestIdKey(requestId);
        using var pending = connection.CreatePendingRequest(_lifetime.Token);
        connection.PendingRequests[key] = pending;
        CreateElicitationResponse? response = null;
        var cancelledByAgent = false;
        try
        {
            pending.Token.ThrowIfCancellationRequested();
            if (run.OnInteractiveAsk is { } askInteractive)
            {
                string? validationNote = null;
                for (var attempt = 0; attempt < MaxValidationAttempts; attempt++)
                {
                    var message = string.IsNullOrEmpty(validationNote)
                        ? form.Message
                        : string.IsNullOrEmpty(form.Message) ? validationNote : $"{validationNote}\n\n{form.Message}";
                    var interactiveRequest = new AgentInteractiveAskRequest(
                        _providerId,
                        _sessionId,
                        run.RunId,
                        Guid.NewGuid().ToString("D"),
                        DateTimeOffset.UtcNow,
                        message,
                        form.Questions);
                    var result = await askInteractive(interactiveRequest, pending.Token).ConfigureAwait(false);
                    pending.Token.ThrowIfCancellationRequested();
                    if (AcpElicitation.TryBuildResponse(form, result, out var built, out validationNote))
                    {
                        response = built;
                        break;
                    }
                }

                response ??= AcpElicitation.CreateActionResponse("cancel");
            }
            else
            {
                var inputRequest = new AgentUserInputRequest(
                    _providerId,
                    _sessionId,
                    DateTimeOffset.UtcNow,
                    run.RunId,
                    Guid.NewGuid().ToString("D"),
                    AcpElicitation.ToUserInputForm(form));
                var result = await run.OnUserInputRequest!(inputRequest, pending.Token).ConfigureAwait(false);
                pending.Token.ThrowIfCancellationRequested();
                response = AcpElicitation.BuildResponse(form, result);
            }
        }
        catch (OperationCanceledException)
        {
            cancelledByAgent = pending.CancelledByAgent;
            response = cancelledByAgent ? null : AcpElicitation.CreateActionResponse("cancel");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.Warn($"The elicitation request of {_options.AgentName} could not be answered: {ex.Message}");
            response = AcpElicitation.CreateActionResponse("cancel");
        }
        finally
        {
            connection.PendingRequests.TryRemove(key, out _);
        }

        if (cancelledByAgent)
        {
            await RespondErrorAsync(connection, requestId, AcpErrorCodes.RequestCancelled, "The elicitation request was cancelled.").ConfigureAwait(false);
            return;
        }

        await RespondElicitationAsync(connection, requestId, response!.Value).ConfigureAwait(false);
    }

    private async Task RespondElicitationAsync(Connection connection, RequestId requestId, CreateElicitationResponse response)
    {
        try
        {
            await connection.Client.RespondToRequestAsync(requestId, response).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or AcpConnectionClosedException)
        {
        }
    }
}
