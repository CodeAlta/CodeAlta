using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile;

internal sealed record MobilePushNotice(string SessionId, string Kind, string? MachineId = null, int Generation = 0);

/// <summary>Transitions that need the person, queued separately so push services never hold up agent events or live updates.</summary>
internal sealed class MobilePushEvents
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _turns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private Dictionary<string, PluginAskSummary> _asks = new(StringComparer.Ordinal);
    private Dictionary<string, PluginPermissionSummary> _permissions = new(StringComparer.Ordinal);
    private int _generation;
    private readonly Channel<MobilePushNotice> _notices = Channel.CreateBounded<MobilePushNotice>(new BoundedChannelOptions(256)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest,
    });

    internal ChannelReader<MobilePushNotice> Reader => _notices.Reader;
    internal int Generation => Volatile.Read(ref _generation);

    internal void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _turns.Clear();
            _running.Clear();
            _asks.Clear();
            _permissions.Clear();
        }
    }

    internal void Remote(string machineId, string sessionId, string kind)
        => _notices.Writer.TryWrite(new(sessionId, kind, machineId, Generation));

    internal void Observe(AgentEvent agentEvent)
    {
        if (agentEvent is not AgentActivityEvent { Kind: AgentActivityKind.Turn } turn) return;
        lock (_gate)
        {
            if (turn.Phase is AgentActivityPhase.Started or AgentActivityPhase.Progressed) { _running.Add(turn.SessionId); return; }
            if (turn.Phase is not (AgentActivityPhase.Completed or AgentActivityPhase.Failed or AgentActivityPhase.Canceled)) return;
            _running.Remove(turn.SessionId);
            if (turn.Phase == AgentActivityPhase.Canceled) return;
            if (_turns.GetValueOrDefault(turn.SessionId) == turn.ActivityId) return;
            _turns[turn.SessionId] = turn.ActivityId;
            _notices.Writer.TryWrite(new(turn.SessionId, turn.Phase == AgentActivityPhase.Failed ? "failed" : "completed", Generation: _generation));
        }
    }

    internal void Pending(IReadOnlyCollection<PluginAskSummary>? asks, IReadOnlyCollection<PluginPermissionSummary>? permissions)
    {
        lock (_gate)
        {
            if (asks is not null)
            {
                var ready = asks.Where(ask => ask.State == "pending" && (ask.Immediate || !_running.Contains(ask.SessionId)))
                    .DistinctBy(static ask => ask.SessionId).ToDictionary(static ask => ask.SessionId, StringComparer.Ordinal);
                foreach (var ask in ready.Values)
                    if (_asks.GetValueOrDefault(ask.SessionId) != ask)
                        _notices.Writer.TryWrite(new(ask.SessionId, "answer", Generation: _generation));
                _asks = ready;
            }

            if (permissions is not null)
            {
                foreach (var permission in permissions)
                    if (permission.Count > 0 && _permissions.GetValueOrDefault(permission.SessionId)?.RequestId != permission.RequestId)
                        _notices.Writer.TryWrite(new(permission.SessionId, "approval", Generation: _generation));
                _permissions = permissions.DistinctBy(static permission => permission.SessionId).ToDictionary(static permission => permission.SessionId, StringComparer.Ordinal);
            }
        }
    }
}
