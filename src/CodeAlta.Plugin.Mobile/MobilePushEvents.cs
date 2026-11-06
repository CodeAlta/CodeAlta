using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile;

internal sealed record MobilePushNotice(string SessionId, string Kind);

/// <summary>Transitions that need the person, queued separately so push services never hold up agent events or live updates.</summary>
internal sealed class MobilePushEvents
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _turns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private Dictionary<string, PluginAskSummary> _asks = new(StringComparer.Ordinal);
    private Dictionary<string, PluginPermissionSummary> _permissions = new(StringComparer.Ordinal);
    private readonly Channel<MobilePushNotice> _notices = Channel.CreateBounded<MobilePushNotice>(new BoundedChannelOptions(256)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest,
    });

    internal ChannelReader<MobilePushNotice> Reader => _notices.Reader;

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
            _notices.Writer.TryWrite(new(turn.SessionId, turn.Phase == AgentActivityPhase.Failed ? "failed" : "completed"));
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
                        _notices.Writer.TryWrite(new(ask.SessionId, "answer"));
                _asks = ready;
            }

            if (permissions is not null)
            {
                foreach (var permission in permissions)
                    if (permission.Count > 0 && _permissions.GetValueOrDefault(permission.SessionId)?.RequestId != permission.RequestId)
                        _notices.Writer.TryWrite(new(permission.SessionId, "approval"));
                _permissions = permissions.DistinctBy(static permission => permission.SessionId).ToDictionary(static permission => permission.SessionId, StringComparer.Ordinal);
            }
        }
    }
}
