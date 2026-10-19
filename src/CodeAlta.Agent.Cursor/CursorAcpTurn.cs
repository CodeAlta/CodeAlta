using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent.Cursor;

/// <summary>
/// One Cursor agent ACP turn: initialize, authenticate with <c>cursor_login</c>, open a session, prompt, and answer permission requests.
/// </summary>
internal static class CursorAcpTurn
{
    /// <summary>
    /// Runs one prompt against <c>agent acp</c> and returns the assistant text streamed in <c>session/update</c>.
    /// </summary>
    /// <param name="launch">The CLI launch. The working directory is sent as the session cwd.</param>
    /// <param name="prompt">The user text.</param>
    /// <param name="sessionId">An existing CLI session to load, or null to create one.</param>
    /// <param name="onText">Called with each assistant chunk.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    /// <returns>The session id and the assistant text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="launch"/> or <paramref name="onText"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The CLI closes the stream, returns an error, or exits before the prompt completes.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public static async Task<(string SessionId, string AssistantText)> RunAsync(
        CursorCliLaunch launch,
        string prompt,
        string? sessionId,
        Func<string, CancellationToken, ValueTask> onText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(onText);
        using var process = CursorProcess.Start(launch, "acp", deviceFlow: false);
        var stderr = new StringBuilder();
        var errors = PumpErrorsAsync(process, stderr, cancellationToken);
        var assistant = new StringBuilder();
        var nextId = 0;
        try
        {
            await CallAsync(process, ++nextId, "initialize", writer =>
            {
                writer.WriteNumber("protocolVersion", 1);
                writer.WritePropertyName("clientCapabilities");
                writer.WriteStartObject();
                writer.WritePropertyName("fs");
                writer.WriteStartObject();
                writer.WriteBoolean("readTextFile", false);
                writer.WriteBoolean("writeTextFile", false);
                writer.WriteEndObject();
                writer.WriteBoolean("terminal", false);
                writer.WriteEndObject();
                writer.WritePropertyName("clientInfo");
                writer.WriteStartObject();
                writer.WriteString("name", "CodeAlta");
                writer.WriteString("version", "0");
                writer.WriteEndObject();
            }, assistant, onText, cancellationToken).ConfigureAwait(false);

            await CallAsync(process, ++nextId, "authenticate", writer => writer.WriteString("methodId", "cursor_login"), assistant, onText, cancellationToken).ConfigureAwait(false);

            var opened = await CallAsync(process, ++nextId, string.IsNullOrWhiteSpace(sessionId) ? "session/new" : "session/load", writer =>
            {
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    writer.WriteString("sessionId", sessionId);
                }

                writer.WriteString("cwd", string.IsNullOrWhiteSpace(launch.WorkingDirectory) ? Environment.CurrentDirectory : launch.WorkingDirectory);
                writer.WritePropertyName("mcpServers");
                writer.WriteStartArray();
                writer.WriteEndArray();
            }, assistant, onText, cancellationToken).ConfigureAwait(false);
            var created = opened.RootElement.GetProperty("result").GetProperty("sessionId").GetString();
            if (string.IsNullOrWhiteSpace(created))
            {
                throw new InvalidOperationException("Cursor ACP did not return a session id. " + stderr);
            }

            await CallAsync(process, ++nextId, "session/prompt", writer =>
            {
                writer.WriteString("sessionId", created);
                writer.WritePropertyName("prompt");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", prompt);
                writer.WriteEndObject();
                writer.WriteEndArray();
            }, assistant, onText, cancellationToken).ConfigureAwait(false);

            return (created, assistant.ToString());
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            throw;
        }
        finally
        {
            Stop(process);
            try
            {
                await errors.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task<JsonDocument> CallAsync(
        Process process,
        int id,
        string method,
        Action<Utf8JsonWriter> writeParameters,
        StringBuilder assistant,
        Func<string, CancellationToken, ValueTask> onText,
        CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(Request(id, method, writeParameters)).ConfigureAwait(false);
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new InvalidOperationException($"Cursor ACP closed the stream during {method}.");
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            if (message.TryGetProperty("method", out var inbound))
            {
                var name = inbound.GetString();
                if (name == "session/update")
                {
                    var chunk = ChunkText(message);
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        assistant.Append(chunk);
                        await onText(chunk, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (name == "session/request_permission")
                {
                    await process.StandardInput.WriteLineAsync(PermissionResult(message)).ConfigureAwait(false);
                }

                document.Dispose();
                continue;
            }

            if (message.TryGetProperty("id", out var responseId) && responseId.TryGetInt32(out var response) && response == id)
            {
                if (message.TryGetProperty("error", out var error))
                {
                    var description = error.ToString();
                    document.Dispose();
                    throw new InvalidOperationException($"Cursor ACP {method} failed: {description}");
                }

                return document;
            }

            document.Dispose();
        }
    }

    private static string Request(int id, string method, Action<Utf8JsonWriter> writeParameters)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            writer.WriteStartObject();
            writeParameters(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string PermissionResult(JsonElement message)
    {
        var optionId = "allow-once";
        if (message.TryGetProperty("params", out var parameters) && parameters.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            string? first = null;
            foreach (var option in options.EnumerateArray())
            {
                var id = option.TryGetProperty("optionId", out var idProperty) ? idProperty.GetString() : null;
                var kind = option.TryGetProperty("kind", out var kindProperty) ? kindProperty.GetString() : null;
                first ??= id;
                if (kind is "allow_once" or "allow-once" || id is "allow-once")
                {
                    optionId = id ?? optionId;
                    first = optionId;
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(first))
            {
                optionId = first;
            }
        }

        var idText = message.TryGetProperty("id", out var idValue) ? idValue.GetRawText() : "0";
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            using var idDocument = JsonDocument.Parse(idText);
            idDocument.RootElement.WriteTo(writer);
            writer.WritePropertyName("result");
            writer.WriteStartObject();
            writer.WritePropertyName("outcome");
            writer.WriteStartObject();
            writer.WriteString("outcome", "selected");
            writer.WriteString("optionId", optionId);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? ChunkText(JsonElement message)
    {
        if (!message.TryGetProperty("params", out var parameters))
        {
            return null;
        }

        var update = parameters.TryGetProperty("update", out var nested) ? nested : parameters;
        var kind = update.TryGetProperty("sessionUpdate", out var kindProperty) ? kindProperty.GetString() : null;
        if (kind is "agent_message_chunk" or "agent_message"
            && update.TryGetProperty("content", out var content)
            && content.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString();
        }

        if (parameters.TryGetProperty("content", out var direct)
            && direct.TryGetProperty("text", out var directText)
            && directText.ValueKind == JsonValueKind.String)
        {
            return directText.GetString();
        }

        return null;
    }

    private static async Task PumpErrorsAsync(Process process, StringBuilder stderr, CancellationToken cancellationToken)
    {
        while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            stderr.AppendLine(line);
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
