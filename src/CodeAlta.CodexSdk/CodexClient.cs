using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using XenoAtom.Logging;

namespace CodeAlta.CodexSdk;

/// <summary>
/// High-level async client for the codex app-server JSON-RPC API over stdio.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CodexClient"/> manages the lifecycle of a codex app-server child process,
/// including initialization handshake, typed request/response methods for all supported
/// commands, and streaming server notifications via <see cref="IAsyncEnumerable{T}"/>.
/// </para>
/// <para>
/// Typical usage:
/// <code>
/// await using var client = await CodexClient.StartAsync(new ClientInfo
/// {
///     Name = "my_app",
///     Title = "My Application",
///     Version = "1.0.0"
/// });
///
/// var response = await client.ThreadStartAsync(new ThreadStartParams
/// {
///     Cwd = "/path/to/project"
/// });
///
/// await foreach (var notification in client.Notifications())
/// {
///     // Handle turn events, agent messages, etc.
/// }
/// </code>
/// </para>
/// </remarks>
public sealed partial class CodexClient : IAsyncDisposable
{
    private readonly CodexProcess _process;
    private readonly JsonRpcTransport _transport;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    /// <summary>
    /// Represents the name of the logger used for logging events in the application.
    /// </summary>
    public const string LoggerName = "codex.app-server";

    private CodexClient(CodexProcess process, JsonRpcTransport transport, JsonSerializerOptions jsonOptions)
    {
        _process = process;
        _transport = transport;
        _jsonOptions = jsonOptions;
    }

    /// <summary>
    /// Starts a new codex app-server process in stdio mode, performs the initialization handshake,
    /// and returns a ready-to-use <see cref="CodexClient"/>.
    /// </summary>
    /// <param name="clientInfo">Client identification sent during the <c>initialize</c> handshake.</param>
    /// <param name="experimentalApi">
    /// When <see langword="true"/>, opts into the experimental API surface during initialization.
    /// </param>
    /// <param name="optOutNotificationMethods">
    /// Optional list of exact notification method names to suppress for this connection.
    /// </param>
    /// <param name="processOptions">
    /// Options controlling how the codex executable is located and started.
    /// When <see langword="null"/>, defaults are used (PATH lookup with fnm fallback).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An initialized <see cref="CodexClient"/> instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="clientInfo"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the codex executable cannot be found.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server rejects the initialization request.</exception>
    public static async Task<CodexClient> StartAsync(
        ClientInfo clientInfo,
        bool experimentalApi = false,
        IReadOnlyList<string>? optOutNotificationMethods = null,
        CodexProcessOptions? processOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientInfo);

        var logger = LogManager.GetLogger(LoggerName);
        var process = CodexProcess.Start(processOptions, cancellationToken, logger);
        var jsonOptions = CreateJsonSerializerOptions();
        var transport = new JsonRpcTransport(process.StandardOutput, process.StandardInput, jsonOptions, logger);

        var client = new CodexClient(process, transport, jsonOptions);
        try
        {
            await client.InitializeAsync(clientInfo, experimentalApi, optOutNotificationMethods, cancellationToken)
                .ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Creates a <see cref="CodexClient"/> from existing streams (useful for testing or
    /// external process management).
    /// </summary>
    /// <param name="clientInfo">Client identification sent during the <c>initialize</c> handshake.</param>
    /// <param name="serverOutput">The stream to read server messages from.</param>
    /// <param name="clientInput">The stream to write client messages to.</param>
    /// <param name="experimentalApi">
    /// When <see langword="true"/>, opts into the experimental API surface during initialization.
    /// </param>
    /// <param name="optOutNotificationMethods">
    /// Optional list of exact notification method names to suppress for this connection.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An initialized <see cref="CodexClient"/> instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="clientInfo"/>, <paramref name="serverOutput"/>, or
    /// <paramref name="clientInput"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="JsonRpcException">Thrown when the server rejects the initialization request.</exception>
    public static async Task<CodexClient> ConnectAsync(
        ClientInfo clientInfo,
        Stream serverOutput,
        Stream clientInput,
        bool experimentalApi = false,
        IReadOnlyList<string>? optOutNotificationMethods = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientInfo);
        ArgumentNullException.ThrowIfNull(serverOutput);
        ArgumentNullException.ThrowIfNull(clientInput);

        var logger = LogManager.GetLogger(LoggerName);
        var jsonOptions = CreateJsonSerializerOptions();
        var transport = new JsonRpcTransport(serverOutput, clientInput, jsonOptions, logger);

        var client = new CodexClient(process: null!, transport, jsonOptions);
        try
        {
            await client.InitializeAsync(clientInfo, experimentalApi, optOutNotificationMethods, cancellationToken)
                .ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ── Notification streaming ────────────────────────────────────

    /// <summary>
    /// Streams all server-initiated notifications and requests as an <see cref="IAsyncEnumerable{T}"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method yields <see cref="CodexNotification"/> for server notifications and
    /// <see cref="CodexServerRequest"/> for server-initiated requests (such as approval prompts).
    /// Both are delivered as the base type <see cref="object"/>; use pattern matching to distinguish.
    /// </para>
    /// <para>
    /// For server-initiated requests, the caller is responsible for responding via
    /// <see cref="RespondToApprovalAsync{TResult}"/> to unblock the turn.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">A token to cancel the enumeration.</param>
    /// <returns>An async enumerable of <see cref="CodexNotification"/> and <see cref="CodexServerRequest"/> instances.</returns>
    public async IAsyncEnumerable<object> StreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in _transport.Messages.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var parsed = ParseServerMessage(message);
            if (parsed is not null)
                yield return parsed;
        }
    }

    /// <summary>
    /// Streams only server-initiated notifications (ignoring server requests) as <see cref="CodexNotification"/>.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the enumeration.</param>
    /// <returns>An async enumerable of <see cref="CodexNotification"/> instances.</returns>
    public async IAsyncEnumerable<CodexNotification> NotificationsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in StreamAsync(cancellationToken).ConfigureAwait(false))
        {
            if (item is CodexNotification notification)
                yield return notification;
        }
    }

    // ── Approval responses ────────────────────────────────────────

    /// <summary>
    /// Responds to a server-initiated approval request (command execution or file change).
    /// </summary>
    /// <typeparam name="TResult">The response type (e.g., <see cref="CommandExecutionRequestApprovalResponse"/>).</typeparam>
    /// <param name="requestId">The JSON-RPC request id from the <see cref="CodexServerRequest"/>.</param>
    /// <param name="result">The response payload.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed.</exception>
    public async Task RespondToApprovalAsync<TResult>(
        long requestId,
        TResult result,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _transport.SendResponseAsync(requestId, result, cancellationToken).ConfigureAwait(false);
    }

    // ── Thread APIs ───────────────────────────────────────────────

    /// <summary>
    /// Creates a new thread and auto-subscribes to its events.
    /// </summary>
    /// <param name="parameters">Thread start parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The thread start response containing the new thread and effective config.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<ThreadStartResponse> ThreadStartAsync(
        ThreadStartParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<ThreadStartParams, ThreadStartResponse>(
            "thread/start", parameters, cancellationToken);
    }

    /// <summary>
    /// Resumes an existing thread by id.
    /// </summary>
    /// <param name="parameters">Thread resume parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The thread resume response containing the restored thread.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<ThreadResumeResponse> ThreadResumeAsync(
        ThreadResumeParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<ThreadResumeParams, ThreadResumeResponse>(
            "thread/resume", parameters, cancellationToken);
    }

    /// <summary>
    /// Forks an existing thread into a new thread with copied history.
    /// </summary>
    /// <param name="parameters">Thread fork parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The thread fork response containing the new thread.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<ThreadForkResponse> ThreadForkAsync(
        ThreadForkParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<ThreadForkParams, ThreadForkResponse>(
            "thread/fork", parameters, cancellationToken);
    }

    /// <summary>
    /// Lists stored threads with optional pagination and filters.
    /// </summary>
    /// <param name="parameters">Thread list parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A paginated list of threads.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<ThreadListResponse> ThreadListAsync(
        ThreadListParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<ThreadListParams, ThreadListResponse>(
            "thread/list", parameters, cancellationToken);
    }

    /// <summary>
    /// Lists all thread ids currently loaded in memory.
    /// </summary>
    /// <param name="parameters">Optional pagination parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of loaded thread ids.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<ThreadLoadedListResponse> ThreadLoadedListAsync(
        ThreadLoadedListParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<ThreadLoadedListParams, ThreadLoadedListResponse>(
            "thread/loaded/list", parameters, cancellationToken);
    }

    /// <summary>
    /// Reads a stored thread by id without resuming it.
    /// </summary>
    /// <param name="parameters">Thread read parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The thread read response.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<ThreadReadResponse> ThreadReadAsync(
        ThreadReadParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<ThreadReadParams, ThreadReadResponse>(
            "thread/read", parameters, cancellationToken);
    }

    /// <summary>
    /// Archives a thread by moving its rollout to the archived directory.
    /// </summary>
    /// <param name="parameters">Thread archive parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An empty response on success.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<ThreadArchiveResponse> ThreadArchiveAsync(
        ThreadArchiveParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<ThreadArchiveParams, ThreadArchiveResponse>(
            "thread/archive", parameters, cancellationToken);
    }

    /// <summary>
    /// Unarchives a thread by moving it back to the sessions directory.
    /// </summary>
    /// <param name="threadId">The id of the thread to unarchive.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The unarchive response containing the restored thread.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="threadId"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<ThreadUnarchiveResponse> ThreadUnarchiveAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        return _transport.SendRequestAsync<ThreadUnarchiveParams, ThreadUnarchiveResponse>(
            "thread/unarchive", new ThreadUnarchiveParams { ThreadId = threadId }, cancellationToken);
    }

    /// <summary>
    /// Sets or updates a thread's user-facing name.
    /// </summary>
    /// <param name="threadId">The id of the thread.</param>
    /// <param name="name">The new name for the thread.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An empty response on success.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="threadId"/> or <paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<ThreadSetNameResponse> ThreadNameSetAsync(
        string threadId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(name);
        return _transport.SendRequestAsync<ThreadSetNameParams, ThreadSetNameResponse>(
            "thread/name/set",
            new ThreadSetNameParams { ThreadId = threadId, Name = name },
            cancellationToken);
    }

    /// <summary>
    /// Triggers conversation history compaction for a thread.
    /// </summary>
    /// <param name="threadId">The id of the thread to compact.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An empty response on success.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="threadId"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<ThreadCompactStartResponse> ThreadCompactStartAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        return _transport.SendRequestAsync<ThreadCompactStartParams, ThreadCompactStartResponse>(
            "thread/compact/start", new ThreadCompactStartParams { ThreadId = threadId }, cancellationToken);
    }

    /// <summary>
    /// Drops the last N turns from a thread's in-memory context and persists a rollback marker.
    /// </summary>
    /// <param name="parameters">Thread rollback parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The updated thread after rollback.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<ThreadRollbackResponse> ThreadRollbackAsync(
        ThreadRollbackParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<ThreadRollbackParams, ThreadRollbackResponse>(
            "thread/rollback", parameters, cancellationToken);
    }

    // ── Turn APIs ─────────────────────────────────────────────────

    /// <summary>
    /// Starts a new turn on a thread with user input.
    /// </summary>
    /// <param name="parameters">Turn start parameters including input and optional config overrides.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The initial turn object. Stream <see cref="NotificationsAsync"/> for progress events.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<TurnStartResponse> TurnStartAsync(
        TurnStartParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<TurnStartParams, TurnStartResponse>(
            "turn/start", parameters, cancellationToken);
    }

    /// <summary>
    /// Appends additional user input to an in-flight turn without starting a new one.
    /// </summary>
    /// <param name="parameters">Turn steer parameters with thread id, input, and expected turn id.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The turn steer response.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<TurnSteerResponse> TurnSteerAsync(
        TurnSteerParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<TurnSteerParams, TurnSteerResponse>(
            "turn/steer", parameters, cancellationToken);
    }

    /// <summary>
    /// Interrupts (cancels) an in-flight turn.
    /// </summary>
    /// <param name="parameters">Turn interrupt parameters with thread and turn ids.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An empty response on success. Wait for <c>TurnCompleted</c> notification for cleanup.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<TurnInterruptResponse> TurnInterruptAsync(
        TurnInterruptParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<TurnInterruptParams, TurnInterruptResponse>(
            "turn/interrupt", parameters, cancellationToken);
    }

    // ── Review API ────────────────────────────────────────────────

    /// <summary>
    /// Starts an automated code review on a thread.
    /// </summary>
    /// <param name="parameters">Review start parameters with target and delivery mode.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The review start response with the turn and review thread id.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<ReviewStartResponse> ReviewStartAsync(
        ReviewStartParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<ReviewStartParams, ReviewStartResponse>(
            "review/start", parameters, cancellationToken);
    }

    // ── Command API ───────────────────────────────────────────────

    /// <summary>
    /// Runs a single command under the server sandbox without starting a thread/turn.
    /// </summary>
    /// <param name="parameters">Command execution parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The command execution response with exit code and output.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<CommandExecResponse> CommandExecAsync(
        CommandExecParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<CommandExecParams, CommandExecResponse>(
            "command/exec", parameters, cancellationToken);
    }

    // ── Model API ─────────────────────────────────────────────────

    /// <summary>
    /// Lists available models.
    /// </summary>
    /// <param name="parameters">Model list parameters with optional pagination.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A paginated list of models.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<ModelListResponse> ModelListAsync(
        ModelListParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<ModelListParams, ModelListResponse>(
            "model/list", parameters, cancellationToken);
    }

    // ── Config APIs ───────────────────────────────────────────────

    /// <summary>
    /// Reads the effective configuration after resolving layering.
    /// </summary>
    /// <param name="parameters">Config read parameters with optional cwd scope.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The effective config and its origins.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<ConfigReadResponse> ConfigReadAsync(
        ConfigReadParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<ConfigReadParams, ConfigReadResponse>(
            "config/read", parameters, cancellationToken);
    }

    /// <summary>
    /// Writes a single config key/value to the user's config file.
    /// </summary>
    /// <param name="parameters">Config value write parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The config write response with file path and status.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<ConfigWriteResponse> ConfigValueWriteAsync(
        ConfigValueWriteParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<ConfigValueWriteParams, ConfigWriteResponse>(
            "config/value/write", parameters, cancellationToken);
    }

    /// <summary>
    /// Applies multiple config edits atomically.
    /// </summary>
    /// <param name="parameters">Batch config write parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The config write response with file path and status.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<ConfigWriteResponse> ConfigBatchWriteAsync(
        ConfigBatchWriteParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<ConfigBatchWriteParams, ConfigWriteResponse>(
            "config/batchWrite", parameters, cancellationToken);
    }

    /// <summary>
    /// Reloads MCP server config from disk and queues a refresh for loaded threads.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An empty JSON response on success.</returns>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<JsonElement> ConfigMcpServerReloadAsync(
        CancellationToken cancellationToken = default)
    {
        return _transport.SendRequestAsync<JsonElement>(
            "config/mcpServer/reload", cancellationToken);
    }

    /// <summary>
    /// Reads config requirements constraints.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The raw JSON response containing requirements or null.</returns>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<JsonElement> ConfigRequirementsReadAsync(
        CancellationToken cancellationToken = default)
    {
        return _transport.SendRequestAsync<JsonElement>(
            "configRequirements/read", cancellationToken);
    }

    // ── Skills APIs ───────────────────────────────────────────────

    /// <summary>
    /// Lists available skills, optionally scoped by working directories.
    /// </summary>
    /// <param name="parameters">Skills list parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of skills grouped by working directory.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<SkillsListResponse> SkillsListAsync(
        SkillsListParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<SkillsListParams, SkillsListResponse>(
            "skills/list", parameters, cancellationToken);
    }

    /// <summary>
    /// Writes user-level skill config by path.
    /// </summary>
    /// <param name="parameters">Skills config write parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The skills config write response.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<SkillsConfigWriteResponse> SkillsConfigWriteAsync(
        SkillsConfigWriteParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<SkillsConfigWriteParams, SkillsConfigWriteResponse>(
            "skills/config/write", parameters, cancellationToken);
    }

    /// <summary>
    /// Lists remote skills from the skills marketplace.
    /// </summary>
    /// <param name="parameters">Remote skills list parameters with scope and filters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of remote skill summaries.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<SkillsRemoteReadResponse> SkillsRemoteListAsync(
        SkillsRemoteReadParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<SkillsRemoteReadParams, SkillsRemoteReadResponse>(
            "skills/remote/list", parameters, cancellationToken);
    }

    /// <summary>
    /// Exports a remote skill by hazelnut id to the local skills directory.
    /// </summary>
    /// <param name="parameters">Remote skill export parameters with the hazelnut id.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The exported skill with its id and local path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<SkillsRemoteWriteResponse> SkillsRemoteExportAsync(
        SkillsRemoteWriteParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<SkillsRemoteWriteParams, SkillsRemoteWriteResponse>(
            "skills/remote/export", parameters, cancellationToken);
    }

    // ── App APIs ──────────────────────────────────────────────────

    /// <summary>
    /// Lists available apps/connectors (experimental).
    /// </summary>
    /// <param name="parameters">App list parameters with optional pagination.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A paginated list of app info.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<AppsListResponse> AppListAsync(
        AppsListParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<AppsListParams, AppsListResponse>(
            "app/list", parameters, cancellationToken);
    }

    // ── Account APIs ──────────────────────────────────────────────

    /// <summary>
    /// Reads the current account/authentication state.
    /// </summary>
    /// <param name="parameters">Account read parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The account state including auth mode and whether OpenAI auth is required.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<GetAccountResponse> AccountReadAsync(
        GetAccountParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<GetAccountParams, GetAccountResponse>(
            "account/read", parameters, cancellationToken);
    }

    /// <summary>
    /// Starts a login flow (API key or ChatGPT).
    /// </summary>
    /// <param name="parameters">Login parameters (API key or ChatGPT).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The login response.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<LoginAccountResponse> AccountLoginStartAsync(
        LoginAccountParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<LoginAccountParams, LoginAccountResponse>(
            "account/login/start", parameters, cancellationToken);
    }

    /// <summary>
    /// Cancels a pending ChatGPT login flow.
    /// </summary>
    /// <param name="parameters">Cancel login parameters with the login id.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The cancel login response with status.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<CancelLoginAccountResponse> AccountLoginCancelAsync(
        CancelLoginAccountParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<CancelLoginAccountParams, CancelLoginAccountResponse>(
            "account/login/cancel", parameters, cancellationToken);
    }

    /// <summary>
    /// Signs out from the current account.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An empty response on success.</returns>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<LogoutAccountResponse> AccountLogoutAsync(
        CancellationToken cancellationToken = default)
    {
        return _transport.SendRequestAsync<LogoutAccountResponse>(
            "account/logout", cancellationToken);
    }

    /// <summary>
    /// Reads ChatGPT rate limits.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The rate limits response.</returns>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<GetAccountRateLimitsResponse> AccountRateLimitsReadAsync(
        CancellationToken cancellationToken = default)
    {
        return _transport.SendRequestAsync<GetAccountRateLimitsResponse>(
            "account/rateLimits/read", cancellationToken);
    }

    // ── Feedback API ──────────────────────────────────────────────

    /// <summary>
    /// Submits a feedback report.
    /// </summary>
    /// <param name="parameters">Feedback upload parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The feedback upload response with tracking thread id.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<FeedbackUploadResponse> FeedbackUploadAsync(
        FeedbackUploadParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<FeedbackUploadParams, FeedbackUploadResponse>(
            "feedback/upload", parameters, cancellationToken);
    }

    // ── MCP Server APIs ───────────────────────────────────────────

    /// <summary>
    /// Starts an OAuth login flow for a configured MCP server.
    /// </summary>
    /// <param name="parameters">MCP server OAuth login parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The OAuth login response with authorization URL.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<McpServerOauthLoginResponse> McpServerOauthLoginAsync(
        McpServerOauthLoginParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<McpServerOauthLoginParams, McpServerOauthLoginResponse>(
            "mcpServer/oauth/login", parameters, cancellationToken);
    }

    /// <summary>
    /// Lists configured MCP servers with their tools, resources, and auth status.
    /// </summary>
    /// <param name="parameters">MCP server status list parameters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A paginated list of MCP server statuses.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<ListMcpServerStatusResponse> McpServerStatusListAsync(
        ListMcpServerStatusParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<ListMcpServerStatusParams, ListMcpServerStatusResponse>(
            "mcpServerStatus/list", parameters, cancellationToken);
    }

    // ── Experimental Feature APIs ─────────────────────────────────

    /// <summary>
    /// Lists available experimental features with their current status.
    /// </summary>
    /// <param name="parameters">Experimental feature list parameters with optional pagination.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A paginated list of experimental features.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonRpcException">Thrown when the server returns an error.</exception>
    public Task<ExperimentalFeatureListResponse> ExperimentalFeatureListAsync(
        ExperimentalFeatureListParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _transport.SendRequestAsync<ExperimentalFeatureListParams, ExperimentalFeatureListResponse>(
            "experimentalFeature/list", parameters, cancellationToken);
    }

    // ── Dispose ───────────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _transport.DisposeAsync().ConfigureAwait(false);

        // _process may be null when constructed via ConnectAsync with external streams.
        if (_process is not null)
            await _process.DisposeAsync().ConfigureAwait(false);
    }

    // ── Private helpers ───────────────────────────────────────────

    private async Task InitializeAsync(
        ClientInfo clientInfo,
        bool experimentalApi,
        IReadOnlyList<string>? optOutNotificationMethods,
        CancellationToken cancellationToken)
    {
        var initParams = new InitializeParams
        {
            ClientInfo = clientInfo,
            Capabilities = (experimentalApi || optOutNotificationMethods is { Count: > 0 })
                ? new InitializeCapabilities
                {
                    ExperimentalApi = experimentalApi ? true : null,
                    OptOutNotificationMethods = optOutNotificationMethods?.ToList()
                }
                : null
        };

        _ = await _transport.SendRequestAsync<InitializeParams, InitializeResponse>(
            "initialize", initParams, cancellationToken).ConfigureAwait(false);

        // Send the required 'initialized' notification to complete the handshake.
        await _transport.SendNotificationAsync("initialized", cancellationToken).ConfigureAwait(false);
    }

    private object? ParseServerMessage(ServerMessage message)
    {
        return CodexMessageParser.ParseServerMessage(
            message.Method,
            message.Params,
            message.RequestId,
            _jsonOptions);
    }

    /// <summary>
    /// Creates a JSON serializer options.
    /// </summary>
    public static JsonSerializerOptions CreateJsonSerializerOptions()
    {
        return new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = CodexJsonSerializerContext.Default
        };
    }
}
