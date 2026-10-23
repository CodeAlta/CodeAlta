using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodeAlta.Agent;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace CodeAlta.Plugin.Mcp;

/// <summary>What a host does with the MCP servers beyond giving their tools to the model.</summary>
public sealed record McpPluginOptions
{
    /// <summary>
    /// Gets whether the host shows the views of MCP Apps (the <c>io.modelcontextprotocol/ui</c> extension). The servers
    /// are told, and the calls of a tool with a view carry it (<see cref="AgentToolResult.App"/>). A host that cannot
    /// keep a view away from itself leaves it off: the servers then answer with text.
    /// </summary>
    public bool ShowsApps { get; init; }
}

/// <summary>Who asks for the view of a tool call: the session it belongs to, which gives the server its connection.</summary>
/// <param name="SessionId">The session of the call.</param>
/// <param name="ProjectDirectory">The project of the session, or null.</param>
/// <param name="SpaceId">The space of the session, or null.</param>
public sealed record McpAppRequest(string SessionId, string? ProjectDirectory, string? SpaceId);

/// <summary>The view of a tool, read from its server.</summary>
/// <param name="Html">The HTML document of the view.</param>
/// <param name="Meta">What it declares of its needs.</param>
/// <param name="Tool">The tool as its server lists it (name, title, description, input schema), for the view; null when the server no longer lists it.</param>
public sealed record McpAppView(string Html, McpAppResourceMeta Meta, JsonElement? Tool);

/// <summary>The outcome of a request of a view: a JSON value, or the reason it was refused or failed.</summary>
/// <param name="Value">The value: a view, or the JSON of the server's answer; null when <paramref name="ErrorCode"/> is set.</param>
/// <param name="ErrorCode">A short code of the refusal or the failure (<c>tool_not_for_app</c>, <c>server_not_in_space</c>, …), or null.</param>
/// <param name="Error">What happened, for the user, or null.</param>
/// <typeparam name="T">The type of the value.</typeparam>
public sealed record McpAppResult<T>(T? Value, string? ErrorCode = null, string? Error = null)
    where T : class
{
    /// <summary>Gets whether the request succeeded.</summary>
    public bool Succeeded => ErrorCode is null;
}

public sealed partial class McpPlugin
{
    // Largest answer of a server sent to a view.
    private const int MaximumAppAnswerChars = 4 * 1024 * 1024;

    /// <summary>
    /// Reads the view of a tool call of a session (MCP Apps): the <c>ui://</c> resource on the session's connection to its
    /// server, started again when it closed. The server must be configured, enabled and in the space of the session.
    /// </summary>
    /// <param name="request">The session of the call.</param>
    /// <param name="server">The key of the server.</param>
    /// <param name="tool">The name of the tool on the server.</param>
    /// <param name="resourceUri">The <c>ui://</c> resource of the view.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The view, or why it could not be read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A name is empty.</exception>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async Task<McpAppResult<McpAppView>> OpenAppViewAsync(McpAppRequest request, string server, string tool, string resourceUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceUri);
        return await UseForViewAsync(request, async (runtime, runtimeRequest, diagnostics) =>
        {
            var resource = await runtime.ReadAppResourceAsync(runtimeRequest, server, resourceUri, diagnostics, cancellationToken).ConfigureAwait(false);
            if (resource is null) return null;
            // The tool is described for the view when the server still lists it; a view does without.
            var described = await runtime.DescribeToolAsync(runtimeRequest, server, tool, new List<McpRuntimeDiagnostic>(), cancellationToken).ConfigureAwait(false);
            return new McpAppView(resource.Html, resource.Meta, described is null ? null : DescribeToolForView(described));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Calls a tool for the view of a tool call (MCP Apps), on the session's connection to the server of the view: only a
    /// tool whose audience has <c>app</c>, with the checks of a call of the model. The answer is the server's, not redacted.
    /// </summary>
    /// <param name="request">The session of the view.</param>
    /// <param name="server">The key of the server of the view.</param>
    /// <param name="tool">The name of the tool on that server.</param>
    /// <param name="arguments">The arguments, a JSON object (or null or undefined for none).</param>
    /// <param name="cancellationToken">A token to cancel the call.</param>
    /// <returns>The JSON of the <c>CallToolResult</c> of the server, or why the call was refused or failed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A name is empty.</exception>
    /// <exception cref="OperationCanceledException">The call was canceled.</exception>
    public async Task<McpAppResult<string>> CallAppToolAsync(McpAppRequest request, string server, string tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        if (!TryReadObjectArguments(arguments, out var values, out var error))
        {
            return new McpAppResult<string>(null, "invalid_arguments", error);
        }

        return await UseForViewAsync(request, async (runtime, runtimeRequest, diagnostics) =>
        {
            var result = await runtime.CallToolForViewAsync(runtimeRequest, server, tool, values, diagnostics, cancellationToken).ConfigureAwait(false);
            return result is null ? null : SerializeAnswer(result, diagnostics);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a resource of the server of a view for that view (MCP Apps), on the session's connection to it.</summary>
    /// <param name="request">The session of the view.</param>
    /// <param name="server">The key of the server of the view.</param>
    /// <param name="uri">The URI of the resource.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The JSON of the <c>ReadResourceResult</c> of the server, or why the read failed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A name is empty.</exception>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async Task<McpAppResult<string>> ReadAppResourceAsync(McpAppRequest request, string server, string uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        return await UseForViewAsync(request, async (runtime, runtimeRequest, diagnostics) =>
        {
            var result = await runtime.ReadResourceForViewAsync(runtimeRequest, server, uri, diagnostics, cancellationToken).ConfigureAwait(false);
            return result is null ? null : SerializeAnswer(result, diagnostics);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<McpAppResult<T>> UseForViewAsync<T>(
        McpAppRequest request,
        Func<McpRuntimeService, McpRuntimeRequest, IList<McpRuntimeDiagnostic>, Task<T?>> use,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        if (!ShowsApps)
        {
            return new McpAppResult<T>(null, "apps_not_shown", "This host does not show the views of MCP servers.");
        }

        var runtimeRequest = new McpRuntimeRequest
        {
            // The project the session's runs gave: the connection the model's calls use is the one the view talks to.
            ProjectDirectory = _activationState.GetProjectDirectory(McpActivationState.ResolveScopeKey(request.SessionId, null)) ?? request.ProjectDirectory,
            UserHomeDirectory = _userHomeDirectory,
            SpaceId = request.SpaceId,
            Environment = await SessionEnvironmentAsync(request.SessionId, cancellationToken).ConfigureAwait(false),
        };
        var diagnostics = new List<McpRuntimeDiagnostic>();
        T? value;
        try
        {
            value = await _connections.UseForViewAsync(request.SessionId, runtimeRequest, (runtime, found) => use(runtime, runtimeRequest, found), diagnostics, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return new McpAppResult<T>(null, "unavailable", "The MCP plugin was stopped.");
        }

        if (value is not null) return new McpAppResult<T>(value);
        var diagnostic = diagnostics.LastOrDefault();
        return new McpAppResult<T>(null, diagnostic?.Code ?? "unavailable", diagnostic?.Message ?? "The MCP server did not answer.");
    }

    // The answer of a server for a view, as JSON; null, with the reason, when it is over the budget of a view.
    private static string? SerializeAnswer<T>(T answer, IList<McpRuntimeDiagnostic> diagnostics)
        where T : Result
    {
        var json = JsonSerializer.Serialize(answer, (JsonTypeInfo<T>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(T)));
        if (json.Length <= MaximumAppAnswerChars) return json;
        diagnostics.Add(new McpRuntimeDiagnostic { Code = "answer_too_large", Message = $"The answer of the MCP server is larger than {MaximumAppAnswerChars} characters." });
        return null;
    }

    // The tool as the view is told of it (hostContext.toolInfo.tool): what the server listed, not CodeAlta's alias.
    private static JsonElement DescribeToolForView(McpRuntimeTool tool)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            if (tool.Title is { } title) writer.WriteString("title", title);
            if (tool.Description is { } description) writer.WriteString("description", description);
            writer.WritePropertyName("inputSchema");
            tool.InputSchema.WriteTo(writer);
            if (tool.OutputSchema is { } outputSchema)
            {
                writer.WritePropertyName("outputSchema");
                outputSchema.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
