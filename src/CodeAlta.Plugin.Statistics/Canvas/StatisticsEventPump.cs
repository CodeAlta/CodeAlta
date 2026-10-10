using System.Text;
using System.Text.Json;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugin.Statistics.Query;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Statistics.Canvas;

/// <summary>
/// Tells the script of one canvas what changed: the status of the history and the days whose numbers moved. What happens in a burst
/// (a session read every few milliseconds) is sent as one event of each kind after a short delay, and nothing is sent while the tab is
/// hidden: the latest is sent when it is shown again.
/// </summary>
internal sealed class StatisticsEventPump : IDisposable
{
    private const int MaximumSessionIds = 50;

    private readonly IPluginCanvasRpc _rpc;
    private readonly Func<bool> _isVisible;
    private readonly TimeSpan _delay;
    private readonly TimeProvider _time;
    private readonly Logger? _logger;
    private readonly CancellationTokenSource _stopped = new();
    private readonly object _gate = new();
    private StatisticsStatus? _status;
    private long _revision;
    private int _fromDay = int.MaxValue;
    private int _toDay = int.MinValue;
    private HashSet<string>? _sessions;
    private bool _scheduled;

    /// <summary>Initializes the pump.</summary>
    /// <param name="rpc">The registry of the canvas, which sends the events.</param>
    /// <param name="isVisible">Tells whether the tab is shown.</param>
    /// <param name="delay">How long a burst is gathered before it is sent.</param>
    /// <param name="time">The clock of the delay; null for the system clock.</param>
    /// <param name="logger">The logger of the plugin, or null.</param>
    public StatisticsEventPump(IPluginCanvasRpc rpc, Func<bool> isVisible, TimeSpan delay, TimeProvider? time = null, Logger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        ArgumentNullException.ThrowIfNull(isVisible);
        _rpc = rpc;
        _isVisible = isVisible;
        _delay = delay;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>Notes a new status; the latest one of a burst is sent.</summary>
    /// <param name="status">The status.</param>
    public void Status(StatisticsStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            _status = status;
        }

        Schedule();
    }

    /// <summary>Notes a change of the numbers; the days and the sessions of a burst are joined.</summary>
    /// <param name="change">The change.</param>
    public void Data(StatisticsDataChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            _revision = Math.Max(_revision, change.Revision);
            _fromDay = Math.Min(_fromDay, change.FromDay);
            _toDay = Math.Max(_toDay, change.ToDay);
            foreach (var session in change.SessionIds)
            {
                if ((_sessions ??= new HashSet<string>(StringComparer.Ordinal)).Count >= MaximumSessionIds)
                {
                    break;
                }

                _sessions.Add(session);
            }
        }

        Schedule();
    }

    /// <summary>Tells the pump that the tab was shown or hidden: what was held back is sent when it is shown.</summary>
    /// <param name="visible">Whether the tab is shown.</param>
    public void VisibilityChanged(bool visible)
    {
        if (visible)
        {
            Schedule();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopped.Cancel();
        _stopped.Dispose();
    }

    private void Schedule()
    {
        lock (_gate)
        {
            if (_scheduled || _stopped.IsCancellationRequested)
            {
                return;
            }

            _scheduled = true;
        }

        _ = FlushLaterAsync();
    }

    private async Task FlushLaterAsync()
    {
        try
        {
            await Task.Delay(_delay, _time, _stopped.Token).ConfigureAwait(false);
            StatisticsStatus? status;
            StatisticsDataChange? change = null;
            var visible = _isVisible();
            lock (_gate)
            {
                _scheduled = false;
                if (!visible)
                {
                    return;
                }

                status = _status;
                _status = null;
                if (_toDay >= _fromDay)
                {
                    change = new StatisticsDataChange(_revision, _fromDay, _toDay, _sessions is null ? [] : [.. _sessions]);
                    _fromDay = int.MaxValue;
                    _toDay = int.MinValue;
                    _sessions = null;
                }
            }

            // The data first: the page asks again for the days that moved, and the status that follows tells what the bar shows.
            if (change is not null)
            {
                await _rpc.PublishAsync(StatisticsCanvasRpc.EventsName, Parse($"{{\"kind\":\"data\",\"change\":{StatisticsJson.Serialize(change)}}}"), _stopped.Token).ConfigureAwait(false);
            }

            if (status is not null)
            {
                await _rpc.PublishAsync(StatisticsCanvasRpc.EventsName, Parse($"{{\"kind\":\"status\",\"status\":{StatisticsJson.Serialize(status)}}}"), _stopped.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The canvas closed.
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _scheduled = false;
            }

            _logger?.Warn($"A statistics event could not be sent: {exception.Message}");
        }
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(Encoding.UTF8.GetBytes(json));
        return document.RootElement.Clone();
    }
}
