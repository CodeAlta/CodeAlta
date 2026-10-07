using System.Buffers;
using System.Net;
using System.Text.Json;
using CodeAlta.Desktop.Ui;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Mcp;

/// <summary>A tool the MCP server offers.</summary>
/// <param name="Name">The name of the tool.</param>
/// <param name="Description">What the tool does.</param>
/// <param name="InputSchema">The JSON Schema of its arguments, an object schema.</param>
/// <param name="ReadOnly">Whether the tool only reads.</param>
/// <param name="CallAsync">Runs the tool with the arguments of a call.</param>
internal sealed record DesktopMcpTool(string Name, string Description, JsonElement InputSchema, bool ReadOnly,
    Func<JsonElement, CancellationToken, Task<DesktopUiToolResult>> CallAsync);

/// <summary>What the MCP server is doing.</summary>
/// <param name="State"><c>running</c>, <c>stopped</c> (turned off) or <c>failed</c> (turned on, and not listening).</param>
/// <param name="Enabled">Whether the server is turned on.</param>
/// <param name="Url">The address clients connect to while it runs.</param>
/// <param name="Token">The access token clients send, when the server asks for one.</param>
/// <param name="Error">Why it is not listening, when it failed.</param>
internal sealed record DesktopMcpStatus(string State, bool Enabled, string? Url, string? Token, string? Error)
{
    internal const string Running = "running";
    internal const string Stopped = "stopped";
    internal const string Failed = "failed";
}

/// <summary>
/// The MCP server of the application: other applications see and drive the window, and run <c>alta</c>
/// commands, through it. It speaks the Streamable HTTP transport of the Model Context Protocol.
/// </summary>
/// <remarks>
/// <para>
/// A client has the control over the application that its user has. The server therefore listens on the
/// loopback address unless a start names another one, answers no page of a browser (see
/// <see cref="DesktopMcpGuard"/>), and asks for an access token when other computers can reach it.
/// </para>
/// <para>
/// Requests share no state: nothing has to be set up again when the application restarts, which is what a
/// client that drives a build under development needs.
/// </para>
/// </remarks>
internal sealed class DesktopMcpServer : IAsyncDisposable
{
    private const string Instructions =
        "This server is CodeAlta Desktop, an application for agentic coding. " +
        "take_snapshot, take_screenshot, click, fill, press_key, evaluate_script and the other tools of that family see and drive its window, " +
        "with the names and the arguments of Chrome DevTools MCP: take_snapshot lists the elements of the page with the uid that the tools acting on an element take. " +
        "The alta tool runs the commands of CodeAlta (projects, sessions, prompts, skills, plugins): start with args [\"--help\"].";

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly DesktopMcpEndpoint _endpoint;
    private readonly Func<IReadOnlyList<DesktopMcpTool>> _tools;
    private readonly string _stateRoot;
    private readonly string _version;
    private readonly Action<bool>? _remember;
    private WebApplication? _application;
    private DesktopMcpStatus _status;
    private bool _disposed;

    /// <summary>Creates the server. It does not listen until <see cref="StartAsync"/>.</summary>
    /// <param name="endpoint">Where it listens.</param>
    /// <param name="tools">The tools it offers, read at each request.</param>
    /// <param name="stateRoot">The folder of this instance, where the address and the access token are written.</param>
    /// <param name="version">The version of the application, which clients are told.</param>
    /// <param name="enabled">Whether the server is turned on.</param>
    /// <param name="remember">Keeps whether the server is turned on, for the next start.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    internal DesktopMcpServer(DesktopMcpEndpoint endpoint, Func<IReadOnlyList<DesktopMcpTool>> tools, string stateRoot, string version,
        bool enabled = true, Action<bool>? remember = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        ArgumentNullException.ThrowIfNull(version);
        _endpoint = endpoint;
        _tools = tools;
        _stateRoot = stateRoot;
        _version = version;
        _remember = remember;
        _status = new DesktopMcpStatus(DesktopMcpStatus.Stopped, enabled, null, null, null);
    }

    /// <summary>The file that holds the address of the running server: one line, removed when it stops.</summary>
    internal string UrlPath => Path.Combine(_stateRoot, "mcp_url.txt");

    /// <summary>The file that holds the access token of a server other computers can reach.</summary>
    internal string TokenPath => Path.Combine(_stateRoot, "mcp_token.txt");

    /// <summary>Gets what the server is doing.</summary>
    internal DesktopMcpStatus Status { get { lock (_gate) return _status; } }

    /// <summary>Raised when <see cref="Status"/> changed.</summary>
    internal event Action? Changed;

    /// <summary>Starts listening when the server is turned on. A failure is kept in <see cref="Status"/>.</summary>
    internal async Task StartAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            if (Status.Enabled) await StartCoreAsync().ConfigureAwait(false);
            else RemoveUrl(); // An application that was ended without notice left the address of a server that is gone.
        }
        finally { _transition.Release(); }
    }

    /// <summary>Turns the server on or off, now and for the next starts.</summary>
    internal async Task SetEnabledAsync(bool enabled)
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _remember?.Invoke(enabled);
            if (enabled)
            {
                Set(Status with { Enabled = true });
                if (_application is null) await StartCoreAsync().ConfigureAwait(false);
            }
            else
            {
                await StopCoreAsync().ConfigureAwait(false);
                Set(new DesktopMcpStatus(DesktopMcpStatus.Stopped, false, null, null, null));
            }
        }
        finally { _transition.Release(); }
    }

    /// <summary>Stops listening for good.</summary>
    public async ValueTask DisposeAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally { _transition.Release(); }
    }

    private async Task StartCoreAsync()
    {
        string? token = null;
        try
        {
            if (!_endpoint.IsLoopback) token = DesktopMcpGuard.ReadOrCreateToken(TokenPath);
            WebApplication application;
            try
            {
                application = await ListenAsync(_endpoint.Port, token).ConfigureAwait(false);
            }
            catch (IOException) when (_endpoint.Port != 0 && !_endpoint.Chosen)
            {
                // The port of the application's own choice is taken, by another program or another user's CodeAlta.
                application = await ListenAsync(0, token).ConfigureAwait(false);
            }

            _application = application;
            var url = _endpoint.On(BoundPort(application)).Url;
            WriteUrl(url);
            Info($"The MCP server listens at {url}");
            Set(new DesktopMcpStatus(DesktopMcpStatus.Running, true, url, token, null));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException
            or System.Net.Sockets.SocketException or FormatException)
        {
            Warn($"The MCP server could not listen on {_endpoint.Host}:{_endpoint.Port}: {exception.Message}");
            Set(new DesktopMcpStatus(DesktopMcpStatus.Failed, true, null, null, exception.Message));
        }
    }

    private async Task StopCoreAsync()
    {
        var application = _application;
        _application = null;
        if (application is null) return;
        RemoveUrl();
        try
        {
            // Requests in progress get a moment; a client that holds one open does not keep the application.
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await application.StopAsync(patience.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidOperationException)
        {
            Warn($"The MCP server did not stop cleanly: {exception.Message}");
        }

        await application.DisposeAsync().ConfigureAwait(false);
    }

    // A server of its own, with nothing read from the folder the application was started in: no settings file,
    // no environment variable and no console decides where it listens or when it stops.
    private async Task<WebApplication> ListenAsync(int port, string? token)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ApplicationName = "CodeAlta", ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            if (string.Equals(_endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                // Both loopback addresses, except for a free port, which the two would not share.
                if (port == 0) kestrel.Listen(IPAddress.Loopback, 0);
                else kestrel.ListenLocalhost(port);
            }
            else kestrel.Listen(IPAddress.Parse(_endpoint.Host), port);
        });
        builder.Services.AddRoutingCore();
        builder.Services.AddSingleton<IHostLifetime, QuietLifetime>();
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "CodeAlta", Version = _version };
                options.ServerInstructions = Instructions;
            })
            .WithHttpTransport(options => options.Stateless = true)
            .WithListToolsHandler(ListToolsAsync)
            .WithCallToolHandler(CallToolAsync);
        var application = builder.Build();
        application.Use(async (context, next) =>
        {
            var status = DesktopMcpGuard.Check(context.Request.Host.Host, context.Request.Headers.Origin, context.Request.Headers.Authorization, token);
            if (status != DesktopMcpGuard.Allowed)
            {
                context.Response.StatusCode = status;
                if (status == StatusCodes.Status401Unauthorized) context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        application.MapMcp(DesktopMcpEndpoint.Path);
        try
        {
            await application.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            await application.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return application;
    }

    private ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> request, CancellationToken cancellationToken)
        => ValueTask.FromResult(new ListToolsResult
        {
            Tools = [.. _tools().Select(static tool => new Tool
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = tool.InputSchema,
                Annotations = new ToolAnnotations { ReadOnlyHint = tool.ReadOnly },
            })],
        });

    private async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var name = request.Params?.Name;
        var tool = _tools().FirstOrDefault(candidate => candidate.Name == name);
        DesktopUiToolResult result;
        if (tool is null) result = DesktopUiToolResult.Error($"Unknown tool \"{name}\". Call a tool of the list of tools.");
        else
        {
            try
            {
                result = await tool.CallAsync(ToObject(request.Params!.Arguments), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Warn($"The MCP tool {tool.Name} failed: {exception.Message}");
                result = DesktopUiToolResult.Error($"The tool \"{name}\" failed: {exception.Message}");
            }
        }

        return new CallToolResult
        {
            IsError = result.IsError,
            Content =
            [
                new TextContentBlock { Text = result.Text },
                .. result.Images.Select(static image => (ContentBlock)ImageContentBlock.FromBytes(image.Data, image.MediaType)),
            ],
        };
    }

    // The arguments of a call as the object the tools read.
    private static JsonElement ToObject(IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in arguments ?? [])
            {
                writer.WritePropertyName(key);
                if (value.ValueKind == JsonValueKind.Undefined) writer.WriteNullValue();
                else value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static int BoundPort(WebApplication application)
    {
        foreach (var address in application.Urls)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0) return uri.Port;
        }

        throw new InvalidOperationException("The server did not report the port it listens on.");
    }

    private void WriteUrl(string url)
    {
        try
        {
            Directory.CreateDirectory(_stateRoot);
            File.WriteAllText(UrlPath, url + Environment.NewLine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Warn($"The address of the MCP server could not be written to {UrlPath}: {exception.Message}");
        }
    }

    private void RemoveUrl()
    {
        try { File.Delete(UrlPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stale address names a port nothing listens on.
        }
    }

    // The application has its log; a test has none.
    private static void Info(string message)
    {
        if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Mcp").Info(message);
    }

    private static void Warn(string message)
    {
        if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Mcp").Warn(message);
    }

    private void Set(DesktopMcpStatus status)
    {
        lock (_gate)
        {
            if (_status == status) return;
            _status = status;
        }

        Changed?.Invoke();
    }

    // The application has its own lifetime: the server neither listens to the console nor to the signals of
    // the process, and writes nothing when it starts.
    private sealed class QuietLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
