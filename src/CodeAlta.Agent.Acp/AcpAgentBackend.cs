using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CodeAlta.Acp;

namespace CodeAlta.Agent.Acp;

/// <summary>
/// ACP implementation of <see cref="IAgentBackend"/>.
/// </summary>
public sealed partial class AcpAgentBackend : IAgentBackend
{
    private readonly ConcurrentDictionary<string, AcpAgentSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly AcpAgentBackendOptions _options;
    private readonly AcpTerminalBridge _terminalBridge = new();
    private Task? _pumpTask;
    private CancellationTokenSource? _pumpCancellationTokenSource;
    private AcpClient? _client;
    private bool _disposed;
    private bool _canLoadSession;
    private bool _canListSessions;
    private bool _canResumeSession;
    private bool _canSetModel;
    private IReadOnlyList<AgentModelInfo> _knownModels = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="AcpAgentBackend"/> class.
    /// </summary>
    /// <param name="options">ACP backend options.</param>
    public AcpAgentBackend(AcpAgentBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public AgentBackendId BackendId => AcpAgentBackendFactoryExtensions.CreateBackendId(_options.AgentId);

    /// <inheritdoc />
    public string DisplayName => _options.DisplayName;

    internal AcpClient Client => _client ?? throw new InvalidOperationException("The ACP backend has not been started.");

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                return;
            }

            var clientCapabilities = new ClientCapabilities
            {
                Auth = new AuthCapabilities(),
                Fs = new FileSystemCapabilities
                {
                    ReadTextFile = _options.EnableFilesystem,
                    WriteTextFile = _options.EnableFilesystem,
                },
                Terminal = _options.EnableTerminal,
                Elicitation = _options.UseUnstableFeatures && _options.UnstableFeatures.UseElicitation && _options.EnableElicitation
                    ? new ElicitationCapabilities { Form = new ElicitationFormCapabilities() }
                    : null,
            };

            _client = _options.ClientFactory is not null
                ? await _options.ClientFactory(cancellationToken).ConfigureAwait(false)
                : await AcpClient.StartAsync(
                        new AcpClientOptions
                        {
                            ProcessOptions = _options.ProcessOptions,
                            ClientInfo = new Implementation
                            {
                                Name = "CodeAlta",
                                Version = "dev",
                            },
                            ClientCapabilities = clientCapabilities,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            await AuthenticateAsync(_client, cancellationToken).ConfigureAwait(false);

            var capabilities = _client.InitializeResponse.AgentCapabilities;
            _canLoadSession = capabilities.LoadSession == true;
            _canListSessions = capabilities.SessionCapabilities.List is not null;
            _canResumeSession = _options.UseUnstableFeatures &&
                                _options.UnstableFeatures.UseSessionResume &&
                                capabilities.SessionCapabilities.Resume is not null;
            _canSetModel = _options.UseUnstableFeatures && _options.UnstableFeatures.UseSetModel;
            _pumpCancellationTokenSource = new CancellationTokenSource();
            _pumpTask = RunMessagePumpAsync(_pumpCancellationTokenSource.Token);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_knownModels);
    }

    /// <summary>
    /// Lists sessions from an ACP agent that advertises ACP session-list support.
    /// </summary>
    /// <remarks>
    /// This compatibility method is intentionally concrete to keep external ACP support working while persisted
    /// CodeAlta-owned sessions are discovered through <see cref="IAgentSessionCatalog" /> and <see cref="IAgentSessionStore" />.
    /// </remarks>
    /// <param name="filter">Optional ACP session filter.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>ACP session metadata streamed from the agent.</returns>
    public async IAsyncEnumerable<AgentSessionMetadata> ListSessionsAsync(
        AgentSessionListFilter? filter = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (!_canListSessions)
        {
            yield break;
        }

        string? cursor = null;
        do
        {
            var response = await client.SessionListAsync(
                    new ListSessionsRequest
                    {
                        Cursor = cursor,
                        Cwd = filter?.Cwd
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var session in response.Sessions)
            {
                yield return AcpAgentMapper.ToSessionMetadata(session);
            }

            cursor = response.NextCursor;
        } while (cursor is not null);
    }

    /// <inheritdoc />
    public async Task<IAgentSession> CreateSessionAsync(
        AgentSessionCreateOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateTools(options.Tools);

        var client = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var response = await client.SessionNewAsync(
                AcpAgentMapper.ToNewSessionRequest(options, client.InitializeResponse.AgentCapabilities.McpCapabilities),
                cancellationToken)
            .ConfigureAwait(false);
        var session = await RegisterSessionAsync(
                response.SessionId.Value,
                options,
                options.WorkingDirectory,
                loadExistingJournal: false,
                cancellationToken)
            .ConfigureAwait(false);
        UpdateKnownModels(response.Models);
        await session.PublishAsync(
                new AgentSessionUpdateEvent(
                    BackendId,
                    session.SessionId,
                    DateTimeOffset.UtcNow,
                    RunId: null,
                    AgentSessionUpdateKind.Started,
                    "Session started."),
                cancellationToken)
            .ConfigureAwait(false);
        return session;
    }

    /// <inheritdoc />
    public async Task<IAgentSession> ResumeSessionAsync(
        string sessionId,
        AgentSessionResumeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(options);
        ValidateTools(options.Tools);

        var client = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var normalizedSessionId = sessionId.Trim();
        if (_sessions.TryGetValue(normalizedSessionId, out var existingSession))
        {
            existingSession.UpdateOptions(
                options.SystemMessage,
                options.DeveloperInstructions,
                options.Model,
                options.ReasoningEffort,
                options.OnPermissionRequest,
                options.OnUserInputRequest);
            return existingSession;
        }

        if (_canLoadSession)
        {
            var session = await RegisterSessionAsync(
                    normalizedSessionId,
                    options,
                    options.WorkingDirectory,
                    loadExistingJournal: false,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var response = await client.SessionLoadAsync(
                        AcpAgentMapper.ToLoadSessionRequest(sessionId, options, client.InitializeResponse.AgentCapabilities.McpCapabilities),
                        cancellationToken)
                    .ConfigureAwait(false);
                UpdateKnownModels(response.Models);
                await session.PublishAsync(
                        new AgentSessionUpdateEvent(
                            BackendId,
                            session.SessionId,
                            DateTimeOffset.UtcNow,
                            RunId: null,
                            AgentSessionUpdateKind.Resumed,
                            "Session resumed."),
                        cancellationToken)
                    .ConfigureAwait(false);
                return session;
            }
            catch
            {
                _sessions.TryRemove(normalizedSessionId, out _);
                session.CompleteEventStream();
                throw;
            }
        }

        if (_canResumeSession)
        {
            var session = await RegisterSessionAsync(
                    normalizedSessionId,
                    options,
                    options.WorkingDirectory,
                    loadExistingJournal: true,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var response = await client.SessionResumeAsync(
                        AcpAgentMapper.ToResumeSessionRequest(sessionId, options, client.InitializeResponse.AgentCapabilities.McpCapabilities),
                        cancellationToken)
                    .ConfigureAwait(false);
                UpdateKnownModels(response.Models);
                await session.PublishAsync(
                        new AgentSessionUpdateEvent(
                            BackendId,
                            session.SessionId,
                            DateTimeOffset.UtcNow,
                            RunId: null,
                            AgentSessionUpdateKind.Resumed,
                            "Session resumed."),
                        cancellationToken)
                    .ConfigureAwait(false);
                return session;
            }
            catch
            {
                _sessions.TryRemove(normalizedSessionId, out _);
                session.CompleteEventStream();
                throw;
            }
        }

        throw new NotSupportedException($"{DisplayName} does not support session loading or resuming.");
    }

    /// <inheritdoc />
    public Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        await _terminalBridge.DisposeAsync().ConfigureAwait(false);
        _lifecycleLock.Dispose();
    }

    internal async Task ApplySessionPreferencesAsync(
        AcpAgentSession session,
        string? model,
        AgentReasoningEffort? reasoningEffort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!string.IsNullOrWhiteSpace(model) && _canSetModel)
        {
            try
            {
                await Client.SessionSetModelAsync(
                        new SetSessionModelRequest
                        {
                            SessionId = session.SessionId,
                            ModelId = model.Trim()
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AcpJsonRpcException ex) when (IsMethodUnavailable(ex))
            {
                _canSetModel = false;
            }
        }

        if (reasoningEffort is not null)
        {
            var reasoningEffortValue = reasoningEffort.Value.ToString().ToLowerInvariant();
            var configUpdate = new SetSessionConfigOptionRequest
            {
                Value = AcpJsonHelpers.CreateElement(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("sessionId", session.SessionId);
                    writer.WriteString("configId", "model_reasoning_effort");
                    writer.WriteString("value", reasoningEffortValue);
                    writer.WriteEndObject();
                })
            };

            try
            {
                await Client.SessionSetConfigOptionAsync(configUpdate, cancellationToken).ConfigureAwait(false);
            }
            catch (AcpJsonRpcException ex) when (IsMethodUnavailable(ex) || ex.Code == -32602)
            {
            }
        }
    }

    internal async Task ReleaseSessionAsync(AcpAgentSession session)
    {
        _sessions.TryRemove(session.SessionId, out _);
        if (_options.UseUnstableFeatures &&
            _options.UnstableFeatures.UseSessionClose)
        {
            try
            {
                await Client.SessionCloseAsync(
                        new CloseSessionRequest
                        {
                            SessionId = session.SessionId
                        })
                    .ConfigureAwait(false);
            }
            catch (AcpJsonRpcException ex) when (IsMethodUnavailable(ex))
            {
                _options.UnstableFeatures.UseSessionClose = false;
            }
            catch
            {
            }
        }
    }

    private static void ValidateTools(IReadOnlyList<AgentToolDefinition>? tools)
    {
        if (tools is not { Count: > 0 })
        {
            return;
        }

        throw new NotSupportedException("Generic ACP backends do not support CodeAlta dynamic tool definitions. Use MCP server configuration instead.");
    }

    private static bool IsMethodUnavailable(AcpJsonRpcException exception)
        => exception.Code == -32601 || exception.Code == -32602;

    private async Task<AcpAgentSession> RegisterSessionAsync(
        string sessionId,
        AgentSessionCreateOptions options,
        string? workspacePath,
        bool loadExistingJournal,
        CancellationToken cancellationToken)
    {
        var historyJournal = new AcpHistoryJournal(_options.StateRootPath, BackendId, sessionId);
        if (loadExistingJournal)
        {
            await historyJournal.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        var session = _sessions.GetOrAdd(
            sessionId,
            static (key, tuple) => new AcpAgentSession(
                tuple.Backend,
                key,
                tuple.WorkspacePath,
                tuple.Options.SystemMessage,
                tuple.Options.DeveloperInstructions,
                tuple.Options.Model,
                tuple.Options.ReasoningEffort,
                tuple.Options.OnPermissionRequest,
                tuple.Options.OnUserInputRequest,
                tuple.HistoryJournal),
            (Backend: this, WorkspacePath: workspacePath, Options: options, HistoryJournal: historyJournal));

        session.UpdateOptions(
            options.SystemMessage,
            options.DeveloperInstructions,
            options.Model,
            options.ReasoningEffort,
            options.OnPermissionRequest,
            options.OnUserInputRequest);
        return session;
    }

    private void UpdateKnownModels(SessionModelState? models)
    {
        var known = AcpAgentMapper.ToModelInfos(models);
        if (known.Count > 0)
        {
            _knownModels = known;
        }
    }

    private async Task<AcpClient> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        return Client;
    }

    private async Task AuthenticateAsync(AcpClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        var authMethod = ResolveAuthenticationMethod(client.InitializeResponse.AuthMethods);
        if (authMethod is null)
        {
            return;
        }

        await client.AuthenticateAsync(
                new AuthenticateRequest
                {
                    MethodId = GetAuthenticationMethodId(authMethod)
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private AuthMethod? ResolveAuthenticationMethod(IReadOnlyList<AuthMethod>? authMethods)
    {
        if (authMethods is not { Count: > 0 })
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_options.AuthenticationMethodId))
        {
            var requested = authMethods.FirstOrDefault(method =>
                string.Equals(GetAuthenticationMethodId(method), _options.AuthenticationMethodId, StringComparison.Ordinal));
            if (requested is null)
            {
                throw new InvalidOperationException(
                    $"ACP backend '{DisplayName}' does not advertise authentication method '{_options.AuthenticationMethodId}'.");
            }

            EnsureSupportedAuthenticationMethod(requested);
            return requested;
        }

        var agentManaged = authMethods.OfType<AuthMethodAgent>().FirstOrDefault();
        if (agentManaged is not null)
        {
            return agentManaged;
        }

        if (_options.UseUnstableFeatures)
        {
            var envVarMethod = authMethods.OfType<AuthMethodEnvVar>().FirstOrDefault(AreEnvironmentVariablesSatisfied);
            if (envVarMethod is not null)
            {
                return envVarMethod;
            }
        }

        throw new InvalidOperationException(
            $"ACP backend '{DisplayName}' requires unsupported authentication. Supported methods: agent, env_var with preconfigured variables.");
    }

    private void EnsureSupportedAuthenticationMethod(AuthMethod method)
    {
        switch (method)
        {
            case AuthMethodAgent:
                return;
            case AuthMethodEnvVar envVar when _options.UseUnstableFeatures && AreEnvironmentVariablesSatisfied(envVar):
                return;
            default:
                throw new InvalidOperationException(
                    $"ACP backend '{DisplayName}' authentication method '{GetAuthenticationMethodId(method)}' is not supported by this client configuration.");
        }
    }

    private bool AreEnvironmentVariablesSatisfied(AuthMethodEnvVar method)
    {
        foreach (var variable in method.Vars)
        {
            if (variable.Optional == true)
            {
                continue;
            }

            var configuredValue = _options.ProcessOptions.EnvironmentVariables is { Count: > 0 } configured &&
                                  configured.TryGetValue(variable.Name, out var value)
                ? value
                : Environment.GetEnvironmentVariable(variable.Name);
            if (string.IsNullOrWhiteSpace(configuredValue))
            {
                return false;
            }
        }

        return true;
    }

    private static string GetAuthenticationMethodId(AuthMethod method)
    {
        return method switch
        {
            AuthMethodAgent agent => agent.Id,
            AuthMethodEnvVar envVar => envVar.Id,
            AuthMethodTerminal terminal => terminal.Id,
            _ => throw new InvalidOperationException($"Unsupported ACP authentication method '{method.GetType().FullName}'."),
        };
    }

    private async Task RunMessagePumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in Client.StreamAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (message.Method)
                {
                    case "session/update":
                        await HandleSessionUpdateAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    case "session/request_permission":
                        await HandlePermissionRequestAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    case "session/elicitation":
                        await HandleElicitationAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    case "session/elicitation/complete":
                        await HandleElicitationCompleteAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    case "fs/read_text_file":
                        await HandleReadTextFileAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    case "fs/write_text_file":
                        await HandleWriteTextFileAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    case "terminal/create":
                        await HandleCreateTerminalAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    case "terminal/output":
                        await HandleTerminalOutputAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    case "terminal/wait_for_exit":
                        await HandleWaitForTerminalExitAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    case "terminal/kill":
                        await HandleKillTerminalAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                    case "terminal/release":
                        await HandleReleaseTerminalAsync(message, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var timestamp = DateTimeOffset.UtcNow;
            foreach (var session in _sessions.Values)
            {
                await session.PublishAsync(
                        new AgentErrorEvent(
                            BackendId,
                            session.SessionId,
                            timestamp,
                            ex.Message,
                            ex),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var session in _sessions.Values)
            {
                session.CompleteEventStream();
            }
        }
    }

    private async Task HandleSessionUpdateAsync(AcpServerMessage message, CancellationToken cancellationToken)
    {
        var notification = AcpJsonHelpers.DeserializeAcp<SessionNotification>(message.Params)
            ?? throw new InvalidOperationException("Failed to deserialize ACP session update.");
        if (!_sessions.TryGetValue(notification.SessionId.Value, out var session))
        {
            return;
        }

        var update = notification.Update.Value;
        var timestamp = DateTimeOffset.UtcNow;
        var runId = session.GetActiveRunId();
        switch (AcpJsonHelpers.GetDiscriminator(update, "sessionUpdate"))
        {
            case "user_message_chunk":
                if (TryCreateContentEvent(update, timestamp, runId, session, AgentContentKind.User, out var userDelta))
                {
                    await session.PublishAsync(userDelta!, cancellationToken).ConfigureAwait(false);
                }

                break;

            case "agent_message_chunk":
                if (TryCreateContentEvent(update, timestamp, runId, session, AgentContentKind.Assistant, out var assistantDelta))
                {
                    await session.PublishAsync(assistantDelta!, cancellationToken).ConfigureAwait(false);
                }

                break;

            case "agent_thought_chunk":
                if (TryCreateContentEvent(update, timestamp, runId, session, AgentContentKind.Reasoning, out var reasoningDelta))
                {
                    await session.PublishAsync(reasoningDelta!, cancellationToken).ConfigureAwait(false);
                }

                break;

            case "tool_call":
            {
                var toolCall = AcpJsonHelpers.DeserializeAcp<ToolCall>(update)
                    ?? throw new InvalidOperationException("Failed to deserialize ACP tool call.");
                var phase = AcpAgentMapper.ToActivityPhase(toolCall.Status, AgentActivityPhase.Requested);
                await session.PublishAsync(
                        new AgentActivityEvent(
                            BackendId,
                            session.SessionId,
                            timestamp,
                            runId,
                            AcpAgentMapper.ToActivityKind(toolCall.Kind),
                            phase,
                            toolCall.ToolCallId.Value,
                            ParentActivityId: null,
                            Name: toolCall.Title,
                            Message: toolCall.Title,
                            Details: AcpAgentMapper.BuildToolDetails(toolCall)),
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            case "tool_call_update":
            {
                var toolCall = AcpJsonHelpers.DeserializeAcp<ToolCallUpdate>(update)
                    ?? throw new InvalidOperationException("Failed to deserialize ACP tool call update.");
                var details = AcpAgentMapper.BuildToolDetails(toolCall);
                var activityKind = toolCall.Kind is null
                    ? AgentActivityKind.ToolCall
                    : AcpAgentMapper.ToActivityKind(toolCall.Kind.Value);
                await session.PublishAsync(
                        new AgentActivityEvent(
                            BackendId,
                            session.SessionId,
                            timestamp,
                            runId,
                            activityKind,
                            AcpAgentMapper.ToActivityPhase(toolCall.Status, AgentActivityPhase.Progressed),
                            toolCall.ToolCallId.Value,
                            ParentActivityId: null,
                            Name: toolCall.Title,
                            Message: AcpAgentMapper.ExtractToolOutput(toolCall.Content) ?? toolCall.Title,
                            Details: details),
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            case "plan":
            {
                var plan = AcpJsonHelpers.DeserializeAcp<Plan>(update)
                    ?? throw new InvalidOperationException("Failed to deserialize ACP plan.");
                await session.PublishAsync(
                        new AgentPlanSnapshotEvent(
                            BackendId,
                            session.SessionId,
                            timestamp,
                            runId,
                            AcpAgentMapper.ToPlanSnapshot(plan)),
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            case "current_mode_update":
            {
                var mode = AcpJsonHelpers.DeserializeAcp<CurrentModeUpdate>(update)
                    ?? throw new InvalidOperationException("Failed to deserialize ACP mode update.");
                await session.PublishAsync(
                        new AgentSessionUpdateEvent(
                            BackendId,
                            session.SessionId,
                            timestamp,
                            runId,
                            AgentSessionUpdateKind.ModeChanged,
                            $"Mode changed to {mode.CurrentModeId.Value}.",
                            Details: AcpJsonHelpers.SerializeAcpToElement(mode)),
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            case "config_option_update":
            {
                var config = AcpJsonHelpers.DeserializeAcp<ConfigOptionUpdate>(update)
                    ?? throw new InvalidOperationException("Failed to deserialize ACP config update.");
                await session.PublishAsync(
                        new AgentSessionUpdateEvent(
                            BackendId,
                            session.SessionId,
                            timestamp,
                            runId,
                            AgentSessionUpdateKind.Info,
                            "Session configuration updated.",
                            Details: AcpJsonHelpers.SerializeAcpToElement(config)),
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            case "session_info_update":
            {
                var info = AcpJsonHelpers.DeserializeAcp<SessionInfoUpdate>(update)
                    ?? throw new InvalidOperationException("Failed to deserialize ACP session info update.");
                await session.PublishAsync(
                        new AgentSessionUpdateEvent(
                            BackendId,
                            session.SessionId,
                            timestamp,
                            runId,
                            info.Title is null ? AgentSessionUpdateKind.ContextChanged : AgentSessionUpdateKind.TitleChanged,
                            info.Title ?? "Session metadata updated.",
                            Details: AcpJsonHelpers.SerializeAcpToElement(info)),
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            case "available_commands_update":
                await session.PublishAsync(
                        new AgentRawEvent(
                            BackendId,
                            session.SessionId,
                            timestamp,
                            "available_commands_update",
                            update.Clone(),
                            runId),
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "usage_update":
            {
                var usage = AcpJsonHelpers.DeserializeAcp<UsageUpdate>(update)
                    ?? throw new InvalidOperationException("Failed to deserialize ACP usage update.");
                await session.PublishAsync(
                        new AgentSessionUpdateEvent(
                            BackendId,
                            session.SessionId,
                            timestamp,
                            runId,
                            AgentSessionUpdateKind.UsageUpdated,
                            "Session usage updated.",
                            Details: AcpJsonHelpers.SerializeAcpToElement(usage),
                            Usage: new AgentSessionUsage(
                                Window: new AgentWindowUsageSnapshot(
                                    CurrentTokens: usage.Used > long.MaxValue ? long.MaxValue : (long)usage.Used,
                                    TokenLimit: usage.Size > long.MaxValue ? long.MaxValue : (long)usage.Size,
                                    MessageCount: null,
                                    Label: "Active context window"),
                                Scope: AgentUsageScope.CurrentWindow,
                                Source: AgentUsageSource.Unknown,
                                UpdatedAt: timestamp)),
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
        }
    }

    private async Task HandlePermissionRequestAsync(AcpServerMessage message, CancellationToken cancellationToken)
    {
        if (message.RequestId is null)
        {
            return;
        }

        var request = AcpJsonHelpers.DeserializeAcp<RequestPermissionRequest>(message.Params)
            ?? throw new InvalidOperationException("Failed to deserialize ACP permission request.");
        if (!_sessions.TryGetValue(request.SessionId.Value, out var session))
        {
            await Client.RespondToRequestAsync(
                    message.RequestId.Value,
                    new RequestPermissionResponse
                    {
                        Outcome = new RequestPermissionOutcome
                        {
                            Value = AcpJsonHelpers.CreateElement(writer =>
                            {
                                writer.WriteStartObject();
                                writer.WriteString("outcome", "cancelled");
                                writer.WriteEndObject();
                            })
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var timestamp = DateTimeOffset.UtcNow;
        var mappedRequest = AcpAgentMapper.ToPermissionRequest(BackendId, request, session.GetActiveRunId(), timestamp);
        await session.PublishAsync(mappedRequest, cancellationToken).ConfigureAwait(false);

        AgentPermissionDecision decision;
        try
        {
            decision = await session.GetPermissionHandler().Invoke(mappedRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await session.PublishAsync(
                    new AgentErrorEvent(
                        BackendId,
                        session.SessionId,
                        DateTimeOffset.UtcNow,
                        $"Failed while handling ACP permission request: {ex.Message}",
                        ex,
                        session.GetActiveRunId()),
                    cancellationToken)
                .ConfigureAwait(false);
            decision = new AgentPermissionDecision(AgentPermissionDecisionKind.Deny);
        }

        var response = AcpAgentMapper.ToPermissionResponse(request, decision);
        await Client.RespondToRequestAsync(message.RequestId.Value, response, cancellationToken).ConfigureAwait(false);
        await session.PublishAsync(
                new AgentInteractionEvent(
                    BackendId,
                    session.SessionId,
                    DateTimeOffset.UtcNow,
                    session.GetActiveRunId(),
                    AgentInteractionKind.PermissionResolved,
                    request.ToolCall.ToolCallId.Value,
                    $"Permission resolved: {decision.Kind}."),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleElicitationAsync(AcpServerMessage message, CancellationToken cancellationToken)
    {
        if (message.RequestId is null)
        {
            return;
        }

        if (!_options.UseUnstableFeatures || !_options.UnstableFeatures.UseElicitation)
        {
            await Client.RespondToRequestAsync(
                    message.RequestId.Value,
                    AcpAgentMapper.CreateDeclinedElicitationResponse(),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var request = message.Params;
        if (!request.TryGetProperty("sessionId", out var sessionIdProperty) ||
            sessionIdProperty.ValueKind != JsonValueKind.String ||
            !_sessions.TryGetValue(sessionIdProperty.GetString()!, out var session))
        {
            await Client.RespondToRequestAsync(
                    message.RequestId.Value,
                    AcpAgentMapper.CreateDeclinedElicitationResponse(),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var mode = request.TryGetProperty("mode", out var modeProperty) && modeProperty.ValueKind == JsonValueKind.String
            ? modeProperty.GetString()
            : null;
        if (!string.Equals(mode, "form", StringComparison.Ordinal))
        {
            await session.PublishAsync(
                    new AgentRawEvent(
                        BackendId,
                        session.SessionId,
                        DateTimeOffset.UtcNow,
                        "session/elicitation",
                        request.Clone(),
                        session.GetActiveRunId()),
                    cancellationToken)
                .ConfigureAwait(false);
            await Client.RespondToRequestAsync(
                    message.RequestId.Value,
                    AcpAgentMapper.CreateDeclinedElicitationResponse(),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var userInputHandler = session.GetUserInputHandler();
        if (userInputHandler is null)
        {
            await Client.RespondToRequestAsync(
                    message.RequestId.Value,
                    AcpAgentMapper.CreateDeclinedElicitationResponse(),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!request.TryGetProperty("requestedSchema", out var schemaProperty) ||
            schemaProperty.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Failed to deserialize ACP elicitation schema.");
        }

        var messageText = request.TryGetProperty("message", out var messageProperty) && messageProperty.ValueKind == JsonValueKind.String
            ? messageProperty.GetString() ?? "Provide the requested input."
            : "Provide the requested input.";
        var schema = schemaProperty.Clone();
        var interactionId = $"acp-elicitation:{message.RequestId.Value}";
        var inputRequest = AcpAgentMapper.ToUserInputRequest(
            BackendId,
            session.SessionId,
            interactionId,
            messageText,
            schema,
            session.GetActiveRunId(),
            DateTimeOffset.UtcNow);

        await session.PublishAsync(inputRequest, cancellationToken).ConfigureAwait(false);

        AgentUserInputResponse response;
        try
        {
            response = await userInputHandler(inputRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await Client.RespondToRequestAsync(
                    message.RequestId.Value,
                    AcpAgentMapper.CreateCanceledElicitationResponse(),
                    cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await session.PublishAsync(
                    new AgentErrorEvent(
                        BackendId,
                        session.SessionId,
                        DateTimeOffset.UtcNow,
                        $"Failed while handling ACP elicitation request: {ex.Message}",
                        ex,
                        session.GetActiveRunId()),
                    cancellationToken)
                .ConfigureAwait(false);
            await Client.RespondToRequestAsync(
                    message.RequestId.Value,
                    AcpAgentMapper.CreateDeclinedElicitationResponse(),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        ElicitationResponse elicitationResponse;
        try
        {
            elicitationResponse = AcpAgentMapper.ToAcceptedElicitationResponse(schema, response);
        }
        catch (Exception ex)
        {
            await session.PublishAsync(
                    new AgentErrorEvent(
                        BackendId,
                        session.SessionId,
                        DateTimeOffset.UtcNow,
                        $"Failed to translate ACP elicitation response: {ex.Message}",
                        ex,
                        session.GetActiveRunId()),
                    cancellationToken)
                .ConfigureAwait(false);
            await Client.RespondToRequestAsync(
                    message.RequestId.Value,
                    AcpAgentMapper.CreateDeclinedElicitationResponse(),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await Client.RespondToRequestAsync(message.RequestId.Value, elicitationResponse, cancellationToken).ConfigureAwait(false);
        await session.PublishAsync(
                new AgentInteractionEvent(
                    BackendId,
                    session.SessionId,
                    DateTimeOffset.UtcNow,
                    session.GetActiveRunId(),
                    AgentInteractionKind.UserInputResolved,
                    inputRequest.InteractionId,
                    $"User input resolved ({response.Answers.Count} answer(s)).",
                    AcpAgentMapper.CreateUserInputResolutionDetails(response)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleElicitationCompleteAsync(AcpServerMessage message, CancellationToken cancellationToken)
    {
        var notification = AcpJsonHelpers.DeserializeAcp<ElicitationCompleteNotification>(message.Params);
        if (notification is null)
        {
            return;
        }

        foreach (var session in _sessions.Values)
        {
            await session.PublishAsync(
                    new AgentRawEvent(
                        BackendId,
                        session.SessionId,
                        DateTimeOffset.UtcNow,
                        "session/elicitation/complete",
                        AcpJsonHelpers.SerializeAcpToElement(notification),
                        session.GetActiveRunId()),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleReadTextFileAsync(AcpServerMessage message, CancellationToken cancellationToken)
    {
        if (message.RequestId is null)
        {
            return;
        }

        var request = AcpJsonHelpers.DeserializeAcp<ReadTextFileRequest>(message.Params)
            ?? throw new InvalidOperationException("Failed to deserialize ACP read_text_file request.");
        ValidateAbsolutePath(request.Path);
        var content = await ReadFileSliceAsync(request, cancellationToken).ConfigureAwait(false);
        await Client.RespondToRequestAsync(
                message.RequestId.Value,
                new ReadTextFileResponse { Content = content },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleWriteTextFileAsync(AcpServerMessage message, CancellationToken cancellationToken)
    {
        if (message.RequestId is null)
        {
            return;
        }

        var request = AcpJsonHelpers.DeserializeAcp<WriteTextFileRequest>(message.Params)
            ?? throw new InvalidOperationException("Failed to deserialize ACP write_text_file request.");
        ValidateAbsolutePath(request.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(request.Path)!);
        await File.WriteAllTextAsync(request.Path, request.Content, cancellationToken).ConfigureAwait(false);
        await Client.RespondToRequestAsync(message.RequestId.Value, new WriteTextFileResponse(), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleCreateTerminalAsync(AcpServerMessage message, CancellationToken cancellationToken)
    {
        if (message.RequestId is null)
        {
            return;
        }

        var request = AcpJsonHelpers.DeserializeAcp<CreateTerminalRequest>(message.Params)
            ?? throw new InvalidOperationException("Failed to deserialize ACP terminal/create request.");
        var response = await _terminalBridge.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        await Client.RespondToRequestAsync(message.RequestId.Value, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleTerminalOutputAsync(AcpServerMessage message, CancellationToken cancellationToken)
    {
        if (message.RequestId is null)
        {
            return;
        }

        var request = AcpJsonHelpers.DeserializeAcp<TerminalOutputRequest>(message.Params)
            ?? throw new InvalidOperationException("Failed to deserialize ACP terminal/output request.");
        var response = await _terminalBridge.GetOutputAsync(request, cancellationToken).ConfigureAwait(false);
        await Client.RespondToRequestAsync(message.RequestId.Value, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleWaitForTerminalExitAsync(AcpServerMessage message, CancellationToken cancellationToken)
    {
        if (message.RequestId is null)
        {
            return;
        }

        var request = AcpJsonHelpers.DeserializeAcp<WaitForTerminalExitRequest>(message.Params)
            ?? throw new InvalidOperationException("Failed to deserialize ACP terminal/wait_for_exit request.");
        var response = await _terminalBridge.WaitForExitAsync(request, cancellationToken).ConfigureAwait(false);
        await Client.RespondToRequestAsync(message.RequestId.Value, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleKillTerminalAsync(AcpServerMessage message, CancellationToken cancellationToken)
    {
        if (message.RequestId is null)
        {
            return;
        }

        var request = AcpJsonHelpers.DeserializeAcp<KillTerminalRequest>(message.Params)
            ?? throw new InvalidOperationException("Failed to deserialize ACP terminal/kill request.");
        var response = await _terminalBridge.KillAsync(request, cancellationToken).ConfigureAwait(false);
        await Client.RespondToRequestAsync(message.RequestId.Value, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleReleaseTerminalAsync(AcpServerMessage message, CancellationToken cancellationToken)
    {
        if (message.RequestId is null)
        {
            return;
        }

        var request = AcpJsonHelpers.DeserializeAcp<ReleaseTerminalRequest>(message.Params)
            ?? throw new InvalidOperationException("Failed to deserialize ACP terminal/release request.");
        var response = await _terminalBridge.ReleaseAsync(request, cancellationToken).ConfigureAwait(false);
        await Client.RespondToRequestAsync(message.RequestId.Value, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task StopCoreAsync()
    {
        _pumpCancellationTokenSource?.Cancel();

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var session in _sessions.Values.ToArray())
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();

        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        _pumpTask = null;
        _pumpCancellationTokenSource?.Dispose();
        _pumpCancellationTokenSource = null;
    }

    private static bool TryCreateContentEvent(
        JsonElement update,
        DateTimeOffset timestamp,
        AgentRunId? runId,
        AcpAgentSession session,
        AgentContentKind kind,
        out AgentContentDeltaEvent? eventData)
    {
        eventData = null;
        if (!update.TryGetProperty("content", out var content) ||
            AcpJsonHelpers.GetDiscriminator(content, "type") != "text" ||
            !content.TryGetProperty("text", out var text) ||
            text.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var contentId = runId is null
            ? $"acp:{kind}"
            : $"{runId.Value.Value}:{kind}";
        eventData = new AgentContentDeltaEvent(
            session.BackendId,
            session.SessionId,
            timestamp,
            runId,
            kind,
            contentId,
            ParentActivityId: null,
            Delta: text.GetString() ?? string.Empty);
        return true;
    }

    private static void ValidateAbsolutePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"ACP file paths must be absolute. Received '{path}'.", nameof(path));
        }
    }

    private static async Task<string> ReadFileSliceAsync(ReadTextFileRequest request, CancellationToken cancellationToken)
    {
        if (request.Line is null && request.Limit is null)
        {
            return await File.ReadAllTextAsync(request.Path, cancellationToken).ConfigureAwait(false);
        }

        using var reader = File.OpenText(request.Path);
        var builder = new StringBuilder();
        var currentLine = 1u;
        var startLine = request.Line ?? 1u;
        var remaining = request.Limit ?? uint.MaxValue;
        while (remaining > 0)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (currentLine >= startLine)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(line);
                remaining--;
            }

            currentLine++;
        }

        return builder.ToString();
    }
}
