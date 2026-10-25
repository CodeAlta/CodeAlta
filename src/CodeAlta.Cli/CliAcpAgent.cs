using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CodeAlta.Acp;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugin.Mcp;
using static CodeAlta.Cli.CodeAltaAcpExtensions;

namespace CodeAlta.Cli;

/// <summary>What a new session of <c>altacli acp</c> starts with, from its command line.</summary>
internal sealed record CliAcpDefaults
{
    public string? ModelRef { get; init; }

    public CodeAlta.Catalog.ModelTiers.ModelTier? Tier { get; init; }

    public AgentReasoningEffort? Reasoning { get; init; }

    public string? AgentPromptId { get; init; }

    public IReadOnlyList<string> DisabledToolPatterns { get; init; } = [];

    // The sandbox every session it serves runs in, from `--sandbox`; null for what the settings decide.
    public string? Sandbox { get; init; }
}

/// <summary>
/// CodeAlta as an agent of the Agent Client Protocol: each method of ACP runs on the sessions of a host of
/// <see cref="CliHost"/> through the one headless driver, and the runtime events of a session become its
/// <c>session/update</c> notifications through <see cref="AcpEventProjector"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>session/new</c> opens a session in the folder <c>cwd</c>; <c>session/load</c> opens one that exists and replays
/// its history, <c>session/resume</c> opens it without replay; <c>session/fork</c> forks one; <c>session/close</c> closes one.
/// The sessions of a client stay open (and leased) until it closes them or the connection ends.</item>
/// <item><c>session/prompt</c> runs a turn to its settlement (the sessions it started and the jobs included), sends every
/// update of the run, then answers with the stop reason and the usage of the run.</item>
/// <item>The <c>mcpServers</c> of a session (stdio, HTTP, SSE) are given to the MCP plugin in memory and activated for that
/// session (<see cref="McpPlugin.AddSessionServers"/>); no configuration file is written.</item>
/// <item>The agent prompts of a session (<c>default</c>, <c>plan</c>...) are its modes; the model and the reasoning effort are
/// configuration options, which <c>session/set_config_option</c> changes for the next prompts.</item>
/// <item>Only the updates of the sessions of the client are sent as <c>session/update</c>: a session a run started is announced
/// by <see cref="CodeAltaAcpExtensions.SessionStartedMethod"/>, and its compactions, retries and errors are sent as the other
/// extension notifications, which name it.</item>
/// </list>
/// </remarks>
internal sealed class CliAcpAgent : AcpAgentBase, IAcpEventSink, IAsyncDisposable
{
    /// <summary>The version of the protocol the agent speaks.</summary>
    public const ushort ProtocolVersionNumber = 1;

    /// <summary>The id of the configuration option of the agent prompt.</summary>
    public const string ModeOption = "mode";

    /// <summary>The id of the configuration option of the model.</summary>
    public const string ModelOption = "model";

    /// <summary>The id of the configuration option of the reasoning effort.</summary>
    public const string ThoughtLevelOption = "thought_level";

    /// <summary>The most sessions a page of <c>session/list</c> has.</summary>
    public const int ListPageSize = 50;

    // The most models of a provider the model option lists.
    private const int MaximumModelsPerProvider = 200;

    // How long the events of a run that settled are waited for, so that its response comes after them.
    private static readonly TimeSpan FlushLimit = TimeSpan.FromSeconds(5);

    private readonly IAcpClientConnection _client;
    private readonly CliHost _host;
    private readonly CliAcpDefaults _defaults;
    private readonly TimeSpan _abortGrace;
    private readonly HeadlessSessionDriver _driver;
    private readonly AcpEventProjector _projector;
    // The sessions of the client, by id.
    private readonly ConcurrentDictionary<string, AcpSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    // The folders whose required plugins and MCP servers were found running: the folder of the command, checked as the host
    // started, and those of the sessions opened since.
    private readonly ConcurrentDictionary<string, byte> _checkedFolders = new(FolderComparer);
    private int _disposed;

    public CliAcpAgent(IAcpClientConnection client, CliHost host, CliAcpDefaults defaults, TimeSpan? abortGrace)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(defaults);
        _client = client;
        _host = host;
        _defaults = defaults;
        _checkedFolders.TryAdd(FolderKey(host.ProjectFolder), 0);
        _abortGrace = abortGrace ?? new HeadlessSessionDriverOptions().AbortGrace;
        _projector = new AcpEventProjector(this, (sessionId, cancellationToken) =>
            new ValueTask<SessionViewDescriptor?>(host.Host.RuntimeService.TryGetResumableSessionAsync(sessionId, cancellationToken)));
        _driver = new HeadlessSessionDriver(host.Host, new HeadlessSessionDriverOptions
        {
            Observer = ObserveAsync,
            AbortGrace = _abortGrace,
        });
    }

    /// <summary>Gets the sessions the client has open.</summary>
    public int OpenSessionCount => _sessions.Count;

    /// <summary>Gets what a prompt awaits before its content is read, for a test (<see cref="CliEnvironment.AcpPromptReading"/>); null for nothing.</summary>
    public Func<Task>? PromptReading { get; init; }

    /// <inheritdoc />
    public override Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The version of the client when the agent speaks it, else the latest the agent speaks.
        var asked = request.ProtocolVersion.Value.ValueKind == JsonValueKind.Number && request.ProtocolVersion.Value.TryGetInt32(out var number) ? number : 0;
        var version = asked == ProtocolVersionNumber ? (ushort)asked : ProtocolVersionNumber;
        return Task.FromResult(new InitializeResponse
        {
            ProtocolVersion = ProtocolVersion.From(version),
            AgentInfo = new Implementation { Name = "altacli", Title = "CodeAlta", Version = AgentVersion() },
            // CodeAlta signs in to its providers itself (CodeAlta Desktop, TUI, or a key in the environment): nothing to authenticate here.
            AuthMethods = [],
            AgentCapabilities = new AgentCapabilities
            {
                LoadSession = true,
                Auth = new AgentAuthCapabilities(),
                PromptCapabilities = new PromptCapabilities { Image = true, EmbeddedContext = true, Audio = false },
                // Servers over stdio are taken by every agent; the MCP plugin also connects to HTTP ones, falling back to SSE.
                McpCapabilities = new McpCapabilities { Http = _host.Mcp is not null, Sse = _host.Mcp is not null },
                SessionCapabilities = new SessionCapabilities
                {
                    List = new SessionListCapabilities(),
                    Fork = new SessionForkCapabilities(),
                    Close = new SessionCloseCapabilities(),
                    Resume = new SessionResumeCapabilities(),
                },
                Meta = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    [MetaKey] = AcpJson.Build(writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("schemaVersion", SchemaVersion);
                        writer.WriteStartArray("notifications");
                        foreach (var method in AcpMethods) writer.WriteStringValue(method);
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }),
                },
            },
        });
    }

    /// <inheritdoc />
    public override async Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cwd = RequireFolder(request.Cwd);
        var servers = ReadMcpServers(request.McpServers);
        await RequireResourcesAsync(cwd, null, cancellationToken).ConfigureAwait(false);
        var opened = await _driver.OpenAsync(new HeadlessSessionRequest
        {
            ProjectFolder = cwd,
            ModelRef = _defaults.ModelRef,
            Tier = _defaults.Tier,
            Reasoning = _defaults.Reasoning,
            AgentPromptId = _defaults.AgentPromptId,
            DisabledToolPatterns = _defaults.DisabledToolPatterns,
            Sandbox = _defaults.Sandbox,
        }, cancellationToken).ConfigureAwait(false);
        var state = await RegisterAsync(opened, cancellationToken).ConfigureAwait(false);
        await AttachMcpServersAsync(state, servers, cwd).ConfigureAwait(false);
        var (modes, options) = await DescribeAsync(state.Session, cancellationToken).ConfigureAwait(false);
        return new NewSessionResponse { SessionId = state.Session.SessionId, Modes = modes, ConfigOptions = options };
    }

    /// <inheritdoc />
    public override async Task<LoadSessionResponse> LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = await OpenExistingAsync(request.SessionId.Value, request.McpServers, cancellationToken).ConfigureAwait(false);
        var runtime = _host.Host.RuntimeService;
        var history = await runtime.TryReadStoredHistoryAsync(state.Session.Descriptor, cancellationToken).ConfigureAwait(false) ?? [];
        // A projection of its own: the one of the live events is the driver's reader's alone.
        await new AcpEventProjector(this).ReplayAsync(state.Session.SessionId, history, cancellationToken).ConfigureAwait(false);
        var (modes, options) = await DescribeAsync(state.Session, cancellationToken).ConfigureAwait(false);
        return new LoadSessionResponse { Modes = modes, ConfigOptions = options };
    }

    /// <inheritdoc />
    public override async Task<ResumeSessionResponse> ResumeSessionAsync(ResumeSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = await OpenExistingAsync(request.SessionId.Value, request.McpServers, cancellationToken).ConfigureAwait(false);
        var (modes, options) = await DescribeAsync(state.Session, cancellationToken).ConfigureAwait(false);
        return new ResumeSessionResponse { Modes = modes, ConfigOptions = options };
    }

    /// <inheritdoc />
    public override async Task<ForkSessionResponse> ForkSessionAsync(ForkSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceId = NormalizeSessionId(request.SessionId.Value);
        var servers = ReadMcpServers(request.McpServers);
        var fork = await _host.Host.RuntimeService.ForkSessionAsync(new SessionForkRequest { SessionId = sourceId }, cancellationToken).ConfigureAwait(false);
        if (fork is not { Status: "ok", Session: { } forked })
        {
            throw fork.Status == "session_missing"
                ? Error(AcpErrorCodes.ResourceNotFound, $"Session '{sourceId}' was not found.", "session_not_found", sourceId)
                : Error(AcpErrorCodes.InvalidRequest, $"Session '{sourceId}' could not be forked ({fork.Status}).", fork.Status, sourceId);
        }

        var opened = await _driver.OpenAsync(new HeadlessSessionRequest { ResumeSessionId = forked.SessionId, DisabledToolPatterns = _defaults.DisabledToolPatterns, Sandbox = _defaults.Sandbox },
            cancellationToken).ConfigureAwait(false);
        // CodeAlta Desktop does not know the fork: it is marked as a new session is.
        var state = await RegisterAsync(opened, cancellationToken).ConfigureAwait(false);
        await RequireResourcesAsync(state, cancellationToken).ConfigureAwait(false);
        await AttachMcpServersAsync(state, servers, state.Session.Descriptor.WorkingDirectory).ConfigureAwait(false);
        var (modes, options) = await DescribeAsync(state.Session, cancellationToken).ConfigureAwait(false);
        return new ForkSessionResponse { SessionId = state.Session.SessionId, Modes = modes, ConfigOptions = options };
    }

    /// <inheritdoc />
    public override async Task<CloseSessionResponse> CloseSessionAsync(CloseSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_sessions.TryRemove(request.SessionId.Value, out var state))
            throw Error(AcpErrorCodes.ResourceNotFound, $"Session '{request.SessionId.Value}' is not open.", "session_not_found", request.SessionId.Value);
        await CloseAsync(state).ConfigureAwait(false);
        return new CloseSessionResponse();
    }

    /// <inheritdoc />
    public override async Task<ListSessionsResponse> ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? cwd = null;
        if (request.Cwd is { Length: > 0 } folder)
        {
            if (!Path.IsPathFullyQualified(folder)) throw Error(AcpErrorCodes.InvalidParams, $"The folder '{folder}' is not an absolute path.", "invalid_request", null);
            cwd = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }

        var offset = 0;
        if (request.Cursor is { } cursor && !TryReadCursor(cursor, out offset))
            throw Error(AcpErrorCodes.InvalidParams, "The cursor is not one of session/list.", "invalid_request", null);
        var runtime = _host.Host.RuntimeService;
        // The journals written by other processes since the host started are listed too.
        await runtime.ReconcileRecoverableSessionCacheAsync(cancellationToken).ConfigureAwait(false);
        var sessions = new List<SessionViewDescriptor>();
        await foreach (var session in runtime.ListRecoverableSessionsAsync(cancellationToken).ConfigureAwait(false))
        {
            // The sessions of a person: not those agents started, not those archived.
            if (session.ParentSessionId is not null || session.Status == SessionViewStatus.Archived) continue;
            if (cwd is not null && !SameFolder(session.WorkingDirectory, cwd)) continue;
            sessions.Add(session);
        }

        var page = sessions.OrderByDescending(static session => session.LastActiveAt).ThenBy(static session => session.SessionId, StringComparer.Ordinal)
            .Skip(offset).Take(ListPageSize + 1).ToList();
        return new ListSessionsResponse
        {
            Sessions = [.. page.Take(ListPageSize).Select(session => new SessionInfo
            {
                SessionId = session.SessionId,
                Cwd = string.IsNullOrWhiteSpace(session.WorkingDirectory) ? _host.Host.CatalogOptions.GlobalRoot : session.WorkingDirectory,
                Title = string.IsNullOrWhiteSpace(session.Title) ? null : session.Title,
                UpdatedAt = session.LastActiveAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            })],
            NextCursor = page.Count > ListPageSize ? WriteCursor(offset + ListPageSize) : null,
        };
    }

    /// <inheritdoc />
    public override async Task<SetSessionModeResponse> SetSessionModeAsync(SetSessionModeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = Find(request.SessionId.Value);
        var session = await ChangeAsync(state, new HeadlessSelectionChange { AgentPromptId = request.ModeId.Value }, cancellationToken).ConfigureAwait(false);
        // A client that reads the configuration options learns the mode there.
        var (_, options) = await DescribeAsync(session, cancellationToken).ConfigureAwait(false);
        await SendAsync(session.SessionId, AcpSessionUpdates.ConfigOptions(options), cancellationToken).ConfigureAwait(false);
        return new SetSessionModeResponse();
    }

    /// <inheritdoc />
    public override async Task<SetSessionConfigOptionResponse> SetSessionConfigOptionAsync(SetSessionConfigOptionRequest request, CancellationToken cancellationToken)
    {
        var state = Find(request.SessionId);
        var value = request.StringValue;
        if (string.IsNullOrWhiteSpace(value)) throw Error(AcpErrorCodes.InvalidParams, $"The option '{request.ConfigId}' takes a string value.", "invalid_request", request.SessionId);
        HeadlessSelectionChange change;
        switch (request.ConfigId)
        {
            case ModeOption:
                change = new HeadlessSelectionChange { AgentPromptId = value };
                break;
            case ModelOption:
                if (value.Contains('@', StringComparison.Ordinal))
                    throw Error(AcpErrorCodes.InvalidParams, $"'{value}' is not a value of the option '{ModelOption}': the reasoning effort is the option '{ThoughtLevelOption}'.", "invalid_request", request.SessionId);
                change = new HeadlessSelectionChange { ModelRef = value };
                break;
            case ThoughtLevelOption:
                if (!AltaModelRef.TryParseReasoning(value, out var effort) || effort is null)
                    throw Error(AcpErrorCodes.InvalidParams, $"'{value}' is not a reasoning effort.", "invalid_request", request.SessionId);
                change = new HeadlessSelectionChange { Reasoning = effort };
                break;
            default:
                throw Error(AcpErrorCodes.InvalidParams, $"The session has no option '{request.ConfigId}': it has {ModeOption}, {ModelOption} and {ThoughtLevelOption}.", "invalid_request", request.SessionId);
        }

        var session = await ChangeAsync(state, change, cancellationToken).ConfigureAwait(false);
        var (_, options) = await DescribeAsync(session, cancellationToken).ConfigureAwait(false);
        // A client that reads the modes learns the mode there.
        if (request.ConfigId == ModeOption) await SendAsync(session.SessionId, AcpSessionUpdates.CurrentMode(session.AgentPromptId), cancellationToken).ConfigureAwait(false);
        return new SetSessionConfigOptionResponse { ConfigOptions = options };
    }

    /// <inheritdoc />
    public override async Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = Find(request.SessionId.Value);
        // Taken before the first await: the server starts a session/cancel read after this prompt only once it is (AcpAgentServer).
        using var turn = new PromptTurn(cancellationToken);
        if (Interlocked.CompareExchange(ref state.Turn, turn, null) is not null)
        {
            throw Error(AcpErrorCodes.InvalidRequest, $"A prompt already runs in session '{state.Id}': wait for its response, or cancel it with session/cancel.",
                "session_busy", state.Id);
        }

        try
        {
            // The rest runs off the loop that reads the messages.
            await Task.Yield();
            var session = state.Session;
            HeadlessPrompt? prompt;
            string? error;
            try
            {
                if (PromptReading is { } reading) await reading().ConfigureAwait(false);
                (prompt, error) = await CliAcpPromptContent.ReadAsync(request.Prompt, session.Descriptor, _host.Host.CatalogOptions, turn.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (turn.CancelRequested)
            {
                return Respond(Cancelled(session));
            }

            if (prompt is null) throw Error(AcpErrorCodes.InvalidParams, error!, "invalid_request", state.Id);
            // A prompt cancelled while its content was read never reaches the provider.
            if (turn.CancelRequested) return Respond(Cancelled(session));
            HeadlessRunResult result;
            try
            {
                result = await _driver.RunAsync(session, prompt, timeout: null, turn.Token).ConfigureAwait(false);
            }
            catch (ArgumentException) when (!_sessions.ContainsKey(state.Id))
            {
                throw Error(AcpErrorCodes.ResourceNotFound, $"Session '{state.Id}' was closed.", "session_not_found", state.Id);
            }

            if (turn.CancelRequested && result.Status is HeadlessRunStatus.Completed or HeadlessRunStatus.Failed)
            {
                // The cancel may land as the run ends: a turn the client cancelled is reported as cancelled all the same.
                result = result with { Status = HeadlessRunStatus.Aborted, AbortReason = "cancelled" };
            }

            if (state.Markers is { } markers) await markers.TryWriteTreeAsync(CancellationToken.None).ConfigureAwait(false);
            await FlushAsync().ConfigureAwait(false);
            return Respond(result);
        }
        finally
        {
            // The session takes the next prompt; the turn ends (Ended) as it is disposed, right after.
            Interlocked.CompareExchange(ref state.Turn, null, turn);
        }
    }

    /// <inheritdoc />
    public override Task CancelAsync(CancelNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        // The prompt in flight, from the moment it was taken: its content stops being read, it does not reach the provider,
        // or its run is aborted. A cancel with no prompt in flight cancels nothing, not even the next prompt.
        if (_sessions.TryGetValue(notification.SessionId.Value, out var state) && Volatile.Read(ref state.Turn) is { } turn) turn.Cancel();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask SessionUpdateAsync(string sessionId, SessionUpdate update, CancellationToken cancellationToken)
    {
        // A session a run started is not one of the client's: it is announced by an extension notification instead.
        if (!_sessions.ContainsKey(sessionId)) return;
        await SendAsync(sessionId, update, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ExtensionAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        try
        {
            await _client.SendNotificationAsync(method, ExtNotification.FromJsonElement(parameters), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is AcpConnectionClosedException or IOException or ObjectDisposedException)
        {
            // The client is gone.
        }
    }

    /// <summary>
    /// Closes the sessions of the client: the runs still going are aborted (reason <c>cancelled</c>) and waited for a bounded
    /// time, the leases are released, and the driver stops. The host is left to its owner.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var sessionId in _sessions.Keys.ToArray())
        {
            if (_sessions.TryRemove(sessionId, out var state)) await CloseAsync(state).ConfigureAwait(false);
        }

        await _driver.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask ObserveAsync(HeadlessSessionEvent item, CancellationToken cancellationToken)
    {
        // The session and the sessions it starts, once their journal exists.
        if (_sessions.TryGetValue(item.RootSessionId, out var state) && state.Markers is { } markers && !markers.IsWritten(item.Event.SessionId))
        {
            await markers.TryWriteAsync(item.Event.SessionId, cancellationToken).ConfigureAwait(false);
        }

        await _projector.ProjectAsync(item, cancellationToken).ConfigureAwait(false);
    }

    // Opens a session that exists, for session/load and session/resume, with the MCP servers the client gives it.
    private async Task<AcpSession> OpenExistingAsync(string sessionId, IReadOnlyList<McpServer>? mcpServers, CancellationToken cancellationToken)
    {
        var id = NormalizeSessionId(sessionId);
        if (_sessions.ContainsKey(id)) throw Error(AcpErrorCodes.InternalError, $"Session '{id}' is already open in this connection.", "session_in_use", id);
        var servers = ReadMcpServers(mcpServers);
        var opened = await _driver.OpenAsync(new HeadlessSessionRequest { ResumeSessionId = id, DisabledToolPatterns = _defaults.DisabledToolPatterns, Sandbox = _defaults.Sandbox },
            cancellationToken).ConfigureAwait(false);
        var state = await RegisterAsync(opened, cancellationToken).ConfigureAwait(false);
        await RequireResourcesAsync(state, cancellationToken).ConfigureAwait(false);
        await AttachMcpServersAsync(state, servers, state.Session.Descriptor.WorkingDirectory).ConfigureAwait(false);
        return state;
    }

    // Refuses a session whose folder requires a plugin or an MCP server that does not run, as the command refuses to start
    // for its own folder; a folder found right is not checked again.
    private async Task RequireResourcesAsync(string? folder, string? sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        var key = FolderKey(folder);
        if (_checkedFolders.ContainsKey(key)) return;
        if (await _host.CheckRequiredAsync(key, cancellationToken).ConfigureAwait(false) is { } missing)
            throw Error(AcpErrorCodes.InternalError, missing, "required_unavailable", sessionId);
        _checkedFolders.TryAdd(key, 0);
    }

    // The same for a session that is open already (load, resume, fork): it is closed when it is refused.
    private async Task RequireResourcesAsync(AcpSession state, CancellationToken cancellationToken)
    {
        try
        {
            await RequireResourcesAsync(state.Session.Descriptor.WorkingDirectory, state.Id, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (_sessions.TryRemove(state.Id, out _)) await CloseAsync(state).ConfigureAwait(false);
            throw;
        }
    }

    // The MCP servers of a request, as the MCP plugin takes them; refused when this run starts no MCP server.
    private List<McpSessionServer> ReadMcpServers(IReadOnlyList<McpServer>? servers)
    {
        if (servers is not { Count: > 0 }) return [];
        if (_host.Mcp is null)
            throw Error(AcpErrorCodes.InvalidParams, "This run starts no MCP server (--no-mcp or --no-plugins): the session cannot connect to the servers it is given.", "mcp_off", null);
        var read = new List<McpSessionServer>(servers.Count);
        foreach (var server in servers)
        {
            read.Add(server switch
            {
                McpServerStdio stdio => new McpSessionServer { Name = stdio.Name, Command = stdio.Command, Args = [.. stdio.Args], Env = Pairs(stdio.Env.Select(static variable => (variable.Name, variable.Value))) },
                McpServerHttp http => new McpSessionServer { Name = http.Name, Url = http.Url, Headers = Pairs(http.Headers.Select(static header => (header.Name, header.Value))) },
                McpServerSse sse => new McpSessionServer { Name = sse.Name, Url = sse.Url, Headers = Pairs(sse.Headers.Select(static header => (header.Name, header.Value))) },
                _ => throw Error(AcpErrorCodes.InvalidParams, "An MCP server of the request has a transport CodeAlta does not know.", "invalid_request", null),
            });
        }

        return read;

        // The last of the values of a name wins.
        static Dictionary<string, string> Pairs(IEnumerable<(string Name, string Value)> pairs)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in pairs) values[name] = value;
            return values;
        }
    }

    // Gives a session its MCP servers: they are activated for it, and their tools are offered from its next prompt on.
    private async Task AttachMcpServersAsync(AcpSession state, List<McpSessionServer> servers, string? folder)
    {
        if (servers.Count == 0 || _host.Mcp is not { } mcp) return;
        try
        {
            mcp.AddSessionServers(state.Id, string.IsNullOrWhiteSpace(folder) ? null : folder, servers);
        }
        catch (ArgumentException exception)
        {
            if (_sessions.TryRemove(state.Id, out _)) await CloseAsync(state).ConfigureAwait(false);
            throw Error(AcpErrorCodes.InvalidParams, exception.Message, "invalid_request", state.Id);
        }
    }

    // The session of an opening, as the client knows it from now on; the error of the opening otherwise.
    private async Task<AcpSession> RegisterAsync(HeadlessSessionOpenResult opened, CancellationToken cancellationToken)
    {
        if (opened.Session is not { } session)
        {
            throw opened.Status switch
            {
                HeadlessOpenStatus.NotFound => Error(AcpErrorCodes.ResourceNotFound, opened.Message ?? "The session was not found.", "session_not_found", null),
                // A provider that cannot run the session: a sign-in, a key, a model that is not offered.
                HeadlessOpenStatus.ProviderNotReady => Error(AcpErrorCodes.AuthRequired,
                    (opened.Message ?? "The provider is not ready.") + " Sign in with CodeAlta Desktop or TUI, or give the key of the provider in the environment; `altacli auth check --provider <id>` tells when it is ready.",
                    "provider_not_ready", null),
                HeadlessOpenStatus.SessionInUse => Error(AcpErrorCodes.InternalError,
                    (opened.Message ?? "The session is in use.") + " Close it where it is open (CodeAlta Desktop, TUI, another altacli) first.", "session_in_use", null),
                _ => Error(AcpErrorCodes.InvalidParams, opened.Message ?? "The session cannot be opened.", "invalid_request", null),
            };
        }

        // A session that exists gets its marker again: Desktop refreshes what the run writes to it.
        CliSessionMarkers? markers = null;
        if (_host.Storage.WritesMarkers)
        {
            markers = new CliSessionMarkers(_host.Host.CatalogOptions, _host.Host.RuntimeService, session.SessionId);
            await markers.TryWriteAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        }

        var state = new AcpSession(session, markers);
        if (!_sessions.TryAdd(session.SessionId, state))
        {
            await _driver.CloseAsync(session).ConfigureAwait(false);
            throw Error(AcpErrorCodes.InternalError, $"Session '{session.SessionId}' is already open in this connection.", "session_in_use", session.SessionId);
        }

        return state;
    }

    // One change of the selection of a session at a time; the next prompts run with it.
    private async Task<HeadlessSession> ChangeAsync(AcpSession state, HeadlessSelectionChange change, CancellationToken cancellationToken)
    {
        await state.Selection.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var changed = await _driver.ChangeSelectionAsync(state.Session, change, cancellationToken).ConfigureAwait(false);
            if (changed.Session is not { } session)
                throw Error(AcpErrorCodes.InvalidParams, changed.Message ?? "The session cannot run with this selection.", changed.Status == HeadlessOpenStatus.ProviderNotReady ? "provider_not_ready" : "invalid_request", state.Id);
            state.Session = session;
            return session;
        }
        finally
        {
            state.Selection.Release();
        }
    }

    // The modes of a session (its agent prompts) and its configuration options: the mode, the model, the reasoning effort.
    private async Task<(SessionModeState Modes, List<SessionConfigOption> Options)> DescribeAsync(HeadlessSession session, CancellationToken cancellationToken)
    {
        var choices = await _host.Host.Commands.GetSelectionChoicesAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        var prompts = (choices?.Prompts ?? []).ToList();
        if (!prompts.Any(prompt => prompt.Id == session.AgentPromptId)) prompts.Insert(0, new OwnedPromptChoice(session.AgentPromptId, session.AgentPromptId));
        var modes = new SessionModeState
        {
            CurrentModeId = session.AgentPromptId,
            AvailableModes = [.. prompts.Select(static prompt => new SessionMode { Id = prompt.Id, Name = prompt.Name })],
        };
        var options = new List<SessionConfigOption>
        {
            SessionConfigOption.Select(ModeOption, "Mode", "The agent prompt of the session", "mode", session.AgentPromptId,
                prompts.Select(static prompt => new SessionConfigSelectOption { Value = prompt.Id, Name = prompt.Name })),
            SessionConfigOption.Select(ModelOption, "Model", "The model of the next prompts", "model", ModelValue(session.ProviderKey, session.ModelId),
                ModelValues(session, choices)),
        };
        var model = choices?.Models.FirstOrDefault(candidate => string.Equals(candidate.Id, session.ModelId, StringComparison.OrdinalIgnoreCase));
        if (model is { Efforts.Count: > 0 } && (session.ReasoningEffort ?? model.StartEffort ?? model.Efforts[0]) is var effort)
        {
            var efforts = model.Efforts.Contains(effort) ? model.Efforts : [effort, .. model.Efforts];
            options.Add(SessionConfigOption.Select(ThoughtLevelOption, "Reasoning", "The reasoning effort of the next prompts", "thought_level", EffortValue(effort),
                efforts.Select(static candidate => new SessionConfigSelectOption { Value = EffortValue(candidate), Name = candidate.ToString() })));
        }

        return (modes, options);
    }

    // The models of the providers that are ready, as provider:model: those of the provider of the session first, then those
    // the other providers reported, without asking them again.
    private List<SessionConfigSelectOption> ModelValues(HeadlessSession session, OwnedSelectionChoices? choices)
    {
        var values = new List<SessionConfigSelectOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var registry = _host.Host.ModelProviderRegistry;
        string ProviderName(string key) => registry.TryGetProvider(new ModelProviderId(key), out var descriptor) ? descriptor.DisplayName : key;
        void Add(string providerKey, string modelId, string? name)
        {
            var value = ModelValue(providerKey, modelId);
            if (seen.Add(value)) values.Add(new SessionConfigSelectOption { Value = value, Name = string.IsNullOrWhiteSpace(name) ? modelId : name, Description = ProviderName(providerKey) });
        }

        Add(session.ProviderKey, session.ModelId, choices?.Models.FirstOrDefault(model => model.Id == session.ModelId)?.Name);
        foreach (var model in (choices?.Models ?? []).Take(MaximumModelsPerProvider)) Add(session.ProviderKey, model.Id, model.Name);
        foreach (var state in _host.Host.ModelProviderInitializationService.CurrentStates)
        {
            var key = state.ProviderId.Value;
            if (state.Availability != ModelProviderAvailability.Ready || string.Equals(key, session.ProviderKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (!registry.TryGetProvider(state.ProviderId, out var provider) || !provider.IsEnabled) continue;
            foreach (var model in (state.Models ?? []).Take(MaximumModelsPerProvider)) Add(key, model.Id, model.DisplayName);
        }

        return values;
    }

    private static string ModelValue(string providerKey, string modelId) => providerKey + ":" + modelId;

    private static string EffortValue(AgentReasoningEffort effort) => effort.ToString().ToLowerInvariant();

    private PromptResponse Respond(HeadlessRunResult result)
    {
        switch (result.Status)
        {
            case HeadlessRunStatus.Completed or HeadlessRunStatus.Aborted or HeadlessRunStatus.Timeout:
                return new PromptResponse
                {
                    StopReason = result.Status == HeadlessRunStatus.Completed ? StopReason.EndTurn : StopReason.Cancelled,
                    Usage = CliJsonlWriter.ToUsage(result.Usage),
                    Meta = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        [MetaKey] = AcpJson.Build(writer =>
                        {
                            writer.WriteStartObject();
                            AcpEventProjector.WriteStringOrNull(writer, "runId", result.RunId?.Value);
                            AcpEventProjector.WriteStringOrNull(writer, "abortReason", result.AbortReason);
                            writer.WritePropertyName("cost");
                            // A cost the provider did not report is unknown, never zero.
                            if (result.Usage.Cost is { } amount)
                            {
                                writer.WriteStartObject();
                                writer.WriteNumber("amount", amount);
                                writer.WriteString("currency", result.Usage.CostUnit ?? string.Empty);
                                writer.WriteEndObject();
                            }
                            else
                            {
                                writer.WriteNullValue();
                            }

                            writer.WriteNumber("durationMs", (long)Math.Round(result.Duration.TotalMilliseconds));
                            writer.WriteEndObject();
                        }),
                    },
                };
            case HeadlessRunStatus.ProviderNotReady when result.FailureKind is not AgentFailureKind.ModelUnavailable:
                throw Error(AcpErrorCodes.AuthRequired, (result.FailureMessage ?? "The provider is not ready.")
                    + " Sign in with CodeAlta Desktop or TUI, or give the key of the provider in the environment.", "auth", result.SessionId);
            case HeadlessRunStatus.SessionInUse:
                throw Error(AcpErrorCodes.InternalError, result.FailureMessage ?? $"Session '{result.SessionId}' is in use by another process.", "session_in_use", result.SessionId);
            default:
                throw Error(AcpErrorCodes.InternalError, result.FailureMessage ?? "The run failed.",
                    result.FailureKind is { } kind ? AcpEventProjector.FailureKindName(kind) : "other", result.SessionId);
        }
    }

    // The result of a prompt cancelled before it ran.
    private static HeadlessRunResult Cancelled(HeadlessSession session)
        => new(HeadlessRunStatus.Aborted, session.SessionId, null, null, "cancelled", null, "The prompt was cancelled before it ran.", SessionUsageTotals.None, TimeSpan.Zero);

    private AcpSession Find(string sessionId)
        => _sessions.TryGetValue(sessionId, out var state) ? state
            : throw Error(AcpErrorCodes.ResourceNotFound, $"Session '{sessionId}' is not open: create it with session/new, or open it with session/load.", "session_not_found", sessionId);

    private async Task CloseAsync(AcpSession state)
    {
        if (Volatile.Read(ref state.Turn) is { } turn)
        {
            turn.Cancel();
            await AbortAsync(state).ConfigureAwait(false);
            try { await turn.Ended.WaitAsync(_abortGrace + TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }

        await _driver.CloseAsync(state.Session).ConfigureAwait(false);
        if (state.Markers is { } markers) await markers.TryWriteTreeAsync(CancellationToken.None).ConfigureAwait(false);
        state.Selection.Dispose();
    }

    private async Task AbortAsync(AcpSession state)
    {
        try
        {
            await _driver.CancelAsync(state.Session, "cancelled").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            // The run or the host is ending already.
        }
    }

    private async Task FlushAsync()
    {
        using var limit = new CancellationTokenSource(FlushLimit);
        try
        {
            await _driver.FlushEventsAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            // The observer did not reach the marker in time (a stalled client): the response is sent without waiting more.
        }
    }

    private async ValueTask SendAsync(string sessionId, SessionUpdate update, CancellationToken cancellationToken)
    {
        try
        {
            await _client.SessionUpdateAsync(sessionId, update, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is AcpConnectionClosedException or IOException or ObjectDisposedException)
        {
            // The client is gone.
        }
    }

    private static string RequireFolder(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd))
            throw Error(AcpErrorCodes.InvalidParams, $"The folder '{cwd}' of the session is not an absolute path.", "invalid_request", null);
        var folder = Path.GetFullPath(cwd);
        return Directory.Exists(folder) ? folder : throw Error(AcpErrorCodes.InvalidParams, $"The folder '{folder}' does not exist.", "invalid_request", null);
    }

    private static string NormalizeSessionId(string sessionId)
        => SessionRuntimeService.TryNormalizeSessionId(sessionId, out var normalized) ? normalized
            : throw Error(AcpErrorCodes.ResourceNotFound, $"'{sessionId}' is not a session of CodeAlta: a session is named by a GUID.", "session_not_found", null);

    private static bool SameFolder(string? path, string folder)
        => !string.IsNullOrWhiteSpace(path) && FolderComparer.Equals(FolderKey(path), folder);

    private static StringComparer FolderComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string FolderKey(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private static string WriteCursor(int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes("offset:" + offset.ToString(CultureInfo.InvariantCulture)));

    private static bool TryReadCursor(string cursor, out int offset)
    {
        offset = 0;
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return text.StartsWith("offset:", StringComparison.Ordinal)
                && int.TryParse(text.AsSpan("offset:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out offset);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static AcpJsonRpcException Error(int code, string message, string kind, string? sessionId)
        => AcpJsonRpcException.WithData(code, message, AcpJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind);
            if (sessionId is not null) writer.WriteString("sessionId", sessionId);
            writer.WriteEndObject();
        }));

    private static string AgentVersion()
    {
        var version = typeof(CliAcpAgent).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(CliAcpAgent).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }

    // A session of the client: what its next prompts run with, the markers of its tree, and its prompt in progress.
    private sealed class AcpSession(HeadlessSession session, CliSessionMarkers? markers)
    {
        private HeadlessSession _session = session;

        // The prompt in progress, from the moment it is taken to its response; null between the prompts.
        public PromptTurn? Turn;

        public string Id { get; } = session.SessionId;

        public HeadlessSession Session
        {
            get => Volatile.Read(ref _session);
            set => Volatile.Write(ref _session, value);
        }

        public CliSessionMarkers? Markers { get; } = markers;

        public SemaphoreSlim Selection { get; } = new(1, 1);
    }

    // A prompt of a session, and the cancel of its client: the reading of its content and its run stop at the token.
    private sealed class PromptTurn(CancellationToken requestToken) : IDisposable
    {
        private readonly CancellationTokenSource _cancel = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cancelRequested;

        public CancellationToken Token => _cancel.Token;

        // Whether the client cancelled it with session/cancel (or closed its session), not with $/cancel_request.
        public bool CancelRequested => Volatile.Read(ref _cancelRequested) != 0;

        // Ends when the prompt has answered.
        public Task Ended => _ended.Task;

        public void Cancel()
        {
            Volatile.Write(ref _cancelRequested, 1);
            try
            {
                // The callbacks of the token run off the caller (the loop that reads the messages).
                _ = _cancel.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // The prompt has just answered.
            }
        }

        public void Dispose()
        {
            _ended.TrySetResult();
            _cancel.Dispose();
        }
    }
}
