using CodeAlta.Agent;
using CodeAlta.Orchestration;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Persistence;

internal sealed class ChatAgentConnection : IAsyncDisposable
{
    private readonly AgentHub _agentHub;
    private readonly Action<AgentEvent> _eventHandler;

    private AgentId? _connectedAgentId;
    private AgentBackendId? _connectedBackendId;
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

    public bool IsConnected =>
        _connectedAgentId is not null &&
        _eventSubscription is not null &&
        _connectedBackendId is not null;

    public async Task<AgentId> EnsureConnectedAsync(
        AgentBackendId backendId,
        string workingDirectory,
        IReadOnlyList<AgentToolDefinition>? tools,
        AgentPermissionRequestHandler permissionRequestHandler,
        AgentUserInputRequestHandler? userInputRequestHandler,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(permissionRequestHandler);

        if (IsConnected &&
            _connectedAgentId is { } connectedAgentId &&
            _connectedBackendId is { } connectedBackendId &&
            string.Equals(connectedBackendId.Value, backendId.Value, StringComparison.OrdinalIgnoreCase))
        {
            return connectedAgentId;
        }

        var identity = await _agentHub.RegisterAgentAsync(
                "chat.global",
                new AgentScope { Kind = AgentScopeKind.Global },
                backendId,
                cancellationToken)
            .ConfigureAwait(false);

        IDisposable? newSubscription = null;
        try
        {
            await _agentHub.StartSessionAsync(
                    identity.AgentId,
                    new AgentSessionCreateOptions
                    {
                        Streaming = true,
                        WorkingDirectory = workingDirectory,
                        Tools = tools,
                        OnPermissionRequest = permissionRequestHandler,
                        OnUserInputRequest = userInputRequestHandler,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            newSubscription = await _agentHub.SubscribeSessionEventsAsync(
                    identity.AgentId,
                    _eventHandler,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            newSubscription?.Dispose();
            throw;
        }

        _eventSubscription?.Dispose();
        _eventSubscription = newSubscription;
        _connectedAgentId = identity.AgentId;
        _connectedBackendId = backendId;
        return identity.AgentId;
    }

    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        if (_connectedAgentId is not { } agentId)
        {
            return;
        }

        await _agentHub.AbortAsync(agentId, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _eventSubscription?.Dispose();
        _eventSubscription = null;
        _connectedAgentId = null;
        _connectedBackendId = null;
        return ValueTask.CompletedTask;
    }
}
