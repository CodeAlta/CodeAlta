namespace CodeAlta.Agent.CopilotCli;

internal sealed class CopilotSessionCallbackBridge
{
    private Action<AgentEvent>? _publisher;

    public void AttachPublisher(Action<AgentEvent> publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        _publisher = publisher;
    }

    public void Publish(AgentEvent @event)
    {
        _publisher?.Invoke(@event);
    }
}
