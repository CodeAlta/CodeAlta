using CodeAlta.Agent;
using CodeAlta.Orchestration;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Persistence;
using XenoAtom.Logging;

namespace CodeAlta.Services;

internal sealed class ChatAgentConnection : IAsyncDisposable
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.ChatAgentConnection");
    private readonly AgentHub _agentHub;
    private readonly Action<AgentEvent> _eventHandler;

    private AgentId? _connectedAgentId;
    private AgentBackendId? _connectedBackendId;
    private string? _connectedModel;
    private AgentReasoningEffort? _connectedReasoningEffort;
    private IDisposable? _eventSubscription;

    public ChatAgentConnection(AgentHub agentHub, Action<AgentEvent> eventHandler)
    {
        ArgumentNullException.ThrowIfNull(agentHub);
        ArgumentNullException.ThrowIfNull(eventHandler);

        _agentHub = agentHub;
        _eventHandler = eventHandler;
    }

    public AgentId? CurrentAgentId => _connectedAgentId;

    public AgentBackendId? ConnectedBackendId => _connectedBackendId;

    public string? ConnectedModel => _connectedModel;

    public AgentReasoningEffort? ConnectedReasoningEffort => _connectedReasoningEffort;

    public bool IsConnected =>
        _connectedAgentId is not null &&
        _eventSubscription is not null &&
        _connectedBackendId is not null;

    public async Task<AgentId> EnsureConnectedAsync(
        AgentBackendId backendId,
        string workingDirectory,
        string? model,
        AgentReasoningEffort? reasoningEffort,
        IReadOnlyList<AgentToolDefinition>? tools,
        AgentPermissionRequestHandler permissionRequestHandler,
        AgentUserInputRequestHandler? userInputRequestHandler,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(permissionRequestHandler);

        LogDebug(
            $"EnsureConnected backend={backendId.Value} workdir={workingDirectory} model={model ?? "<default>"} reasoning={reasoningEffort?.ToString() ?? "<default>"} tools={tools?.Count ?? 0}");

        if (IsConnected &&
            _connectedAgentId is { } connectedAgentId &&
            _connectedBackendId is { } connectedBackendId &&
            string.Equals(connectedBackendId.Value, backendId.Value, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_connectedModel, model, StringComparison.Ordinal) &&
            _connectedReasoningEffort == reasoningEffort)
        {
            LogDebug($"Reusing existing chat connection agentId={connectedAgentId.Value} backend={backendId.Value}");
            return connectedAgentId;
        }

        AgentId agentId;
        if (_connectedAgentId is { } existingAgentId &&
            _connectedBackendId is { } existingBackendId &&
            string.Equals(existingBackendId.Value, backendId.Value, StringComparison.OrdinalIgnoreCase))
        {
            LogDebug($"Restarting existing chat session agentId={existingAgentId.Value} backend={backendId.Value}");
            _eventSubscription?.Dispose();
            _eventSubscription = null;
            await _agentHub.StopSessionAsync(existingAgentId, cancellationToken).ConfigureAwait(false);
            agentId = existingAgentId;
        }
        else
        {
            if (_connectedAgentId is { } previousAgentId)
            {
                LogDebug($"Stopping previous chat session agentId={previousAgentId.Value}");
                _eventSubscription?.Dispose();
                _eventSubscription = null;
                await _agentHub.StopSessionAsync(previousAgentId, cancellationToken).ConfigureAwait(false);
            }

            var identity = await _agentHub.RegisterAgentAsync(
                    "chat.global",
                    new AgentScope { Kind = AgentScopeKind.Global },
                    backendId,
                    cancellationToken)
                .ConfigureAwait(false);
            agentId = identity.AgentId;
            LogDebug($"Registered chat agent agentId={agentId.Value} backend={backendId.Value}");
        }

        IDisposable? newSubscription = null;
        try
        {
            await _agentHub.StartSessionAsync(
                    agentId,
                    new AgentSessionCreateOptions
                    {
                        Model = model,
                        ReasoningEffort = reasoningEffort,
                        Streaming = true,
                        WorkingDirectory = workingDirectory,
                        Tools = tools,
                        //SystemMessage = "You are a global coding agent assistant that helps users on coding tasks and distributing work to other coding agents",
                        //DeveloperInstructions = "You should not perform coding tasks yourself. You must delegate to other coding agents.",
                        OnPermissionRequest = permissionRequestHandler,
                        OnUserInputRequest = userInputRequestHandler,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            LogDebug($"Started chat session agentId={agentId.Value} backend={backendId.Value}");

            newSubscription = await _agentHub.SubscribeSessionEventsAsync(
                    agentId,
                    _eventHandler,
                    cancellationToken)
                .ConfigureAwait(false);
            LogDebug($"Subscribed to chat session events agentId={agentId.Value} backend={backendId.Value}");
        }
        catch
        {
            newSubscription?.Dispose();
            throw;
        }

        _eventSubscription?.Dispose();
        _eventSubscription = newSubscription;
        _connectedAgentId = agentId;
        _connectedBackendId = backendId;
        _connectedModel = model;
        _connectedReasoningEffort = reasoningEffort;
        LogDebug($"Chat connection ready agentId={agentId.Value} backend={backendId.Value}");
        return agentId;
    }

    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        if (_connectedAgentId is not { } agentId)
        {
            return;
        }

        LogDebug($"Aborting chat session agentId={agentId.Value}");
        await _agentHub.AbortAsync(agentId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _eventSubscription?.Dispose();
        _eventSubscription = null;
        if (_connectedAgentId is { } agentId)
        {
            await _agentHub.StopSessionAsync(agentId).ConfigureAwait(false);
        }

        _connectedAgentId = null;
        _connectedBackendId = null;
        _connectedModel = null;
        _connectedReasoningEffort = null;
    }

    private static void LogDebug(string message)
    {
        if (LogManager.IsInitialized && Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.Debug(message);
        }
    }
}