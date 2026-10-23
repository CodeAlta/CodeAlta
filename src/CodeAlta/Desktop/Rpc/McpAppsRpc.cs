using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugin.Mcp;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Serves the views of MCP Apps to the window: opens the view of a tool call of a session from its record, and runs the
/// requests of the view (a tool of its server, a resource of its server) on the session's connection to that server.
/// </summary>
/// <remarks>
/// A view is named by the token <c>open</c> returns, which binds its session, project, space and server: a request of
/// the view cannot name another server. Every request is written to the log with its server, its tool and its time.
/// </remarks>
[NeoRpcService("mcpApps", Version = 1)]
internal sealed class McpAppsService
{
    /// <summary>Longest arguments of a tool call of a view, in UTF-16 units.</summary>
    internal const int MaximumArgumentsUnits = 1024 * 1024;

    /// <summary>Longest name of a tool or URI of a resource a view asks for, in UTF-16 units.</summary>
    internal const int MaximumNameUnits = 2048;

    private const string LogName = "CodeAlta.Desktop.McpApps";

    private readonly Func<string, long, CancellationToken, Task<AgentEvent?>>? _read;
    private readonly Func<McpPlugin?>? _plugin;
    private readonly DesktopMcpAppViews? _views;
    private readonly string? _epoch;

    /// <summary>Gets the folder a session works in, or null when it has none.</summary>
    internal Func<string, CancellationToken, ValueTask<string?>>? SessionFolder { get; init; }

    /// <summary>Gets the space of a session, or null.</summary>
    internal Func<string, CancellationToken, Task<string?>>? SessionSpace { get; init; }

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal McpAppsService()
    {
    }

    /// <summary>Creates the service of an owned host.</summary>
    /// <param name="read">Reads the record of a session at a journal offset.</param>
    /// <param name="plugin">Gets the running MCP plugin, or null when it does not run.</param>
    /// <param name="views">The views the window shows.</param>
    /// <param name="epoch">The host epoch requests must name.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal McpAppsService(Func<string, long, CancellationToken, Task<AgentEvent?>> read, Func<McpPlugin?> plugin, DesktopMcpAppViews views, string epoch)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(views);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _read = read;
        _plugin = plugin;
        _views = views;
        _epoch = epoch;
    }

    /// <summary>Opens the view of a tool call: reads its record and the view from its server.</summary>
    [NeoRpcMethod("open")]
    public async Task<McpAppOpenResponse> OpenAsync(McpAppOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_read is null || _views is null) return new("unavailable", null, null, null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null, null, null);
        if (!Identity(request.SessionId) || !Offset(request.Offset, out var offset)) return new("invalid", null, null, null);
        if (_plugin!() is not { ShowsApps: true } plugin) return new("unavailable", null, null, null);
        AgentEvent? record;
        try { record = await _read(request.SessionId, offset, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new("missing_record", null, null, null); } // Never serialize exception details: they name paths.
        if (record is not AgentActivityEvent activity || ReadCall(activity) is not { } call) return new("not_a_view", null, null, null);
        var started = Stopwatch.GetTimestamp();
        string? folder;
        McpAppResult<McpAppView> opened;
        try
        {
            folder = SessionFolder is null ? null : await SessionFolder(request.SessionId, cancellationToken).ConfigureAwait(false);
            opened = await plugin.OpenAppViewAsync(new McpAppRequest(request.SessionId, folder, await SpaceOfAsync(request.SessionId, cancellationToken).ConfigureAwait(false)),
                call.Server, call.Tool, call.ResourceUri, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            if (LogManager.IsInitialized) LogManager.GetLogger(LogName).Warn(exception, $"The view of '{call.Server}' could not be opened.");
            return new("failed", null, "open_failed", "The view could not be read from its server.");
        }

        Log(call.Server, "open " + call.ResourceUri, opened.ErrorCode, started);
        if (opened.Value is not { } view) return new("failed", null, opened.ErrorCode, opened.Error);
        var shown = _views.Open(request.SessionId, call.Server, call.Tool, folder, view);
        return new("ok", new McpAppOpened(shown.Token, shown.FrameUrl, call.Server, call.Tool, view.Meta.PrefersBorder,
            call.Input, call.Result, view.Tool?.GetRawText()), null, null);
    }

    /// <summary>Calls a tool of the server of a view, for the view: only a tool the server lets a view call.</summary>
    [NeoRpcMethod("callTool")]
    public async Task<McpAppAnswer> CallToolAsync(McpAppCallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Admit(request.ExpectedEpoch, request.Token, out var view, out var plugin) is { } refused) return refused;
        if (request.Name is not { Length: > 0 and <= MaximumNameUnits } name || (request.ArgumentsJson?.Length ?? 0) > MaximumArgumentsUnits) return new("invalid", null, null, null);
        JsonElement arguments;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson);
            arguments = document.RootElement.Clone();
        }
        catch (JsonException) { return new("invalid", null, null, null); }

        return await AnswerAsync(view!, "tools/call " + name,
            async request => await plugin!.CallAppToolAsync(request, view!.Server, name, arguments, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a resource of the server of a view, for the view.</summary>
    [NeoRpcMethod("readResource")]
    public async Task<McpAppAnswer> ReadResourceAsync(McpAppReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Admit(request.ExpectedEpoch, request.Token, out var view, out var plugin) is { } refused) return refused;
        if (request.Uri is not { Length: > 0 and <= MaximumNameUnits } uri || uri.Any(char.IsControl)) return new("invalid", null, null, null);
        return await AnswerAsync(view!, "resources/read " + uri,
            async request => await plugin!.ReadAppResourceAsync(request, view!.Server, uri, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    // Runs a request of a view in the space its session is in now, and logs it.
    private async Task<McpAppAnswer> AnswerAsync(DesktopMcpAppView view, string what, Func<McpAppRequest, Task<McpAppResult<string>>> run, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        McpAppResult<string> answer;
        try
        {
            answer = await run(new McpAppRequest(view.SessionId, view.ProjectDirectory, await SpaceOfAsync(view.SessionId, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            if (LogManager.IsInitialized) LogManager.GetLogger(LogName).Warn(exception, $"A request of the view of '{view.Server}' failed: {what}.");
            return new("failed", null, "request_failed", "The request of the view failed.");
        }

        Log(view.Server, what, answer.ErrorCode, started);
        return answer.Value is { } json ? new("ok", json, null, null) : new("failed", null, answer.ErrorCode, answer.Error);
    }

    private async Task<string?> SpaceOfAsync(string sessionId, CancellationToken cancellationToken)
        => SessionSpace is null ? null : await SessionSpace(sessionId, cancellationToken).ConfigureAwait(false);

    /// <summary>Closes a view: its document is no longer served and its requests are refused.</summary>
    [NeoRpcMethod("close")]
    public McpAppCloseResponse Close(McpAppCloseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_views is null) return new("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch");
        return new(_views.Close(request.Token) ? "ok" : "closed");
    }

    /// <summary>
    /// Reads the view of a tool call from its completed activity record: its server, tool and resource, the arguments of
    /// the call, and its result as the <c>CallToolResult</c> the view expects; null when the call has no view.
    /// </summary>
    internal static McpAppCall? ReadCall(AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (HistoryToolProjection.Value(activity.Details, "result") is not { ValueKind: JsonValueKind.Object } result
            || !result.TryGetProperty("app", out var app) || app.ValueKind != JsonValueKind.Object
            || Text(app, "server") is not { } server || Text(app, "tool") is not { } tool
            || Text(app, "resourceUri") is not { } uri || !uri.StartsWith("ui://", StringComparison.OrdinalIgnoreCase)) return null;
        // A tool called through another one (`alta mcp tool call`) has its own arguments with its view.
        var arguments = app.TryGetProperty("arguments", out var own) && own.ValueKind == JsonValueKind.Object
            ? own.GetRawText()
            : ToolInput(HistoryToolProjection.Value(activity.Details, "arguments"));
        return new McpAppCall(server, tool, uri, arguments, ToolResult(result, app));
    }

    // The arguments of the call, as the server got them: a call through the compatibility wrapper is unwrapped.
    private static string ToolInput(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } value) return "{}";
        if (value.TryGetProperty("arguments_json", out var wrapped) && wrapped.ValueKind == JsonValueKind.String && value.EnumerateObject().Count() == 1)
        {
            try
            {
                using var document = JsonDocument.Parse(wrapped.GetString()!);
                if (document.RootElement.ValueKind == JsonValueKind.Object) return document.RootElement.GetRawText();
            }
            catch (JsonException) { return "{}"; }
        }

        return value.GetRawText();
    }

    // The result of the call as MCP has it: its content, its structured content and its `_meta` as the server sent them.
    // A record without the content blocks of the server (over the budget of a view) gives the text the model got.
    private static string ToolResult(JsonElement result, JsonElement app)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (app.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                writer.WritePropertyName("content");
                content.WriteTo(writer);
            }
            else
            {
                WriteTextContent(writer, result);
            }

            if (app.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName("structuredContent");
                structured.WriteTo(writer);
            }

            if (app.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName("_meta");
                meta.WriteTo(writer);
            }

            writer.WriteBoolean("isError", result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteTextContent(Utf8JsonWriter writer, JsonElement result)
    {
        writer.WriteStartArray("content");
        if (result.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("value", out var text) || text.ValueKind != JsonValueKind.String) continue;
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", text.GetString());
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
    }

    private McpAppAnswer? Admit(string expectedEpoch, string? token, out DesktopMcpAppView? view, out McpPlugin? plugin)
    {
        view = null;
        plugin = null;
        if (_views is null || _plugin is null) return new("unavailable", null, null, null);
        if (!string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null, null, null);
        view = _views.Find(token);
        if (view is null) return new("closed", null, null, null);
        plugin = _plugin();
        return plugin is { ShowsApps: true } ? null : new("unavailable", null, null, null);
    }

    // A host without logging (the tests) logs nothing.
    private static void Log(string server, string request, string? errorCode, long started)
    {
        if (LogManager.IsInitialized) LogManager.GetLogger(LogName).Info($"MCP App of '{server}': {request} {(errorCode ?? "ok")} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms");
    }

    private static string? Text(JsonElement value, string property)
        => value.TryGetProperty(property, out var text) && text.ValueKind == JsonValueKind.String && text.GetString() is { Length: > 0 and <= MaximumNameUnits } found ? found : null;

    private static bool Offset(string? value, out long offset)
    {
        offset = -1;
        return value is { Length: >= 1 and <= 19 } && !value.Any(static character => character is < '0' or > '9')
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out offset);
    }

    private static bool Identity(string? value)
        => value is { Length: >= 1 and <= 256 } && !string.IsNullOrWhiteSpace(value) && value == value.Trim() && !value.Any(char.IsControl);
}

/// <summary>The view of a tool call, read from its record.</summary>
/// <param name="Server">The MCP server.</param>
/// <param name="Tool">The tool on the server.</param>
/// <param name="ResourceUri">The <c>ui://</c> resource of the view.</param>
/// <param name="Input">The arguments of the call, as JSON.</param>
/// <param name="Result">The result of the call, as the JSON of a <c>CallToolResult</c>.</param>
internal sealed record McpAppCall(string Server, string Tool, string ResourceUri, string Input, string Result);

/// <summary>Asks for the view of a tool call.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="SessionId">The session of the call.</param>
/// <param name="Offset">The journal offset of the call's activity record, as its history row reported it.</param>
internal sealed record McpAppOpenRequest(string ExpectedEpoch, string SessionId, string Offset);

/// <summary>The view of a tool call, or why it is not shown.</summary>
/// <param name="Status"><c>ok</c>, or <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid</c>, <c>missing_record</c>, <c>not_a_view</c> or <c>failed</c>.</param>
/// <param name="View">The open view, for <c>ok</c>.</param>
/// <param name="Code">The code of the failure, for <c>failed</c>.</param>
/// <param name="Error">What failed, for <c>failed</c>.</param>
internal sealed record McpAppOpenResponse(string Status, McpAppOpened? View, string? Code, string? Error);

/// <summary>An open view.</summary>
/// <param name="Token">The token of the view, which its requests name.</param>
/// <param name="FrameUrl">The address of its document, for its frame.</param>
/// <param name="Server">Its MCP server.</param>
/// <param name="Tool">The tool of its call.</param>
/// <param name="PrefersBorder">Whether it wants a border and a background; null leaves it to the window.</param>
/// <param name="ToolInput">The arguments of the call, as JSON.</param>
/// <param name="ToolResult">The result of the call, as the JSON of a <c>CallToolResult</c>.</param>
/// <param name="ToolDefinition">The tool as its server lists it, as JSON, or null.</param>
internal sealed record McpAppOpened(string Token, string FrameUrl, string Server, string Tool, bool? PrefersBorder, string ToolInput, string ToolResult, string? ToolDefinition);

/// <summary>Asks to call a tool of the server of a view.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="Token">The token of the view.</param>
/// <param name="Name">The name of the tool.</param>
/// <param name="ArgumentsJson">The arguments, a JSON object, or null for none.</param>
internal sealed record McpAppCallRequest(string ExpectedEpoch, string Token, string Name, string? ArgumentsJson);

/// <summary>Asks to read a resource of the server of a view.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="Token">The token of the view.</param>
/// <param name="Uri">The URI of the resource.</param>
internal sealed record McpAppReadRequest(string ExpectedEpoch, string Token, string Uri);

/// <summary>The answer of the server to a request of a view, or why there is none.</summary>
/// <param name="Status"><c>ok</c>, or <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid</c>, <c>closed</c> or <c>failed</c>.</param>
/// <param name="Json">The JSON of the server's answer, for <c>ok</c>.</param>
/// <param name="Code">The code of the failure, such as <c>tool_not_for_app</c>, for <c>failed</c>.</param>
/// <param name="Error">What failed, for <c>failed</c>.</param>
internal sealed record McpAppAnswer(string Status, string? Json, string? Code, string? Error);

/// <summary>Asks to close a view.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="Token">The token of the view.</param>
internal sealed record McpAppCloseRequest(string ExpectedEpoch, string Token);

/// <summary>Whether the view was closed: <c>ok</c>, <c>closed</c> when it was not open, <c>unavailable</c> or <c>stale_epoch</c>.</summary>
/// <param name="Status">The status.</param>
internal sealed record McpAppCloseResponse(string Status);
