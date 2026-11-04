using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime.Tools;

namespace CodeAlta.Automations;

/// <summary>
/// The circuit breaker of the allowed tools of the automations of a host: at most so many calls in an hour, the runs of
/// an automation together (<c>doc/fleet/orchestration.md</c>, "Autonomy and approvals"). Beyond, a call asks a person.
/// </summary>
internal sealed class AutomationCallBreaker
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _calls = new(StringComparer.Ordinal);

    /// <summary>Takes one call of an automation, when it has calls left in the hour.</summary>
    /// <returns>Whether the call may run without a person.</returns>
    internal bool TryTake(string automationId, int? maximum, DateTimeOffset now)
    {
        if (maximum is not { } most) return true;
        lock (_gate)
        {
            if (!_calls.TryGetValue(automationId, out var calls)) _calls[automationId] = calls = new();
            while (calls.Count > 0 && now - calls.Peek() >= Window) calls.Dequeue();
            if (calls.Count >= most) return false;
            calls.Enqueue(now);
            return true;
        }
    }
}

/// <summary>
/// The guard of the session of a run of an automation with <see cref="AutomationAutonomy"/>, on the machine that runs it,
/// at every tool call of the session (never only in its prompt):
/// <list type="bullet">
/// <item>the tools of the host keep their own guards: commands and edits follow the permission mode, which asks;</item>
/// <item>a tool the agent runs itself (an agent CLI) keeps the permission prompts of that agent;</item>
/// <item>any other tool (MCP servers, plugins) runs without a person when the automation allows it and its circuit breaker
/// lets it; otherwise the call waits for a person, as a permission request of the session bound to that call;</item>
/// <item>in a try, an allowed tool is a fake: the call is recorded on the run and not made.</item>
/// </list>
/// </summary>
internal sealed class AutomationRunGuard : AgentToolInterceptor
{
    private const int MaximumShownArguments = 2000;
    private readonly AutomationEntry _entry;
    private readonly AutomationAutonomy _autonomy;
    private readonly AutomationCallBreaker _breaker;
    private readonly Func<AgentPermissionRequest, CancellationToken, Task<AgentPermissionDecision>> _ask;
    private readonly TimeProvider _time;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<CancellationTokenSource, byte> _timeouts = new();
    private int _waiting;
    private int _takenOver;

    /// <summary>Creates the guard of a run.</summary>
    /// <param name="entry">The automation.</param>
    /// <param name="breaker">The circuit breaker of the host.</param>
    /// <param name="ask">Asks a person a permission request of the session of the run.</param>
    /// <param name="time">The clock of the timeouts.</param>
    /// <param name="dryRun">Whether the run is a try: the allowed tools are fakes.</param>
    internal AutomationRunGuard(AutomationEntry entry, AutomationCallBreaker breaker, Func<AgentPermissionRequest, CancellationToken, Task<AgentPermissionDecision>> ask,
        TimeProvider time, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entry = entry;
        _autonomy = entry.Definition.Autonomy ?? throw new ArgumentException("The automation has no autonomy.", nameof(entry));
        (_breaker, _ask, _time, DryRun) = (breaker, ask, time, dryRun);
    }

    /// <summary>Gets whether the run is a try, whose allowed tools are fakes.</summary>
    internal bool DryRun { get; }

    /// <summary>Gets whether a call waits for a person now.</summary>
    internal bool Waiting => Volatile.Read(ref _waiting) > 0;

    /// <summary>Gets whether a person took the run over: what waits for them is no longer timed out.</summary>
    internal bool TakenOver => Volatile.Read(ref _takenOver) != 0;

    /// <summary>Raised when a call starts or stops waiting for a person.</summary>
    internal event Action<bool>? WaitingChanged;

    /// <summary>Raised with what a try recorded in place of a call of an allowed tool.</summary>
    internal event Action<string>? Recorded;

    /// <summary>Raised when an approval waited too long and the automation stops its runs then, with why.</summary>
    internal event Action<string>? Stopped;

    /// <summary>A person takes the run over: what waits for them waits as long as they take.</summary>
    internal void TakeOver()
    {
        Volatile.Write(ref _takenOver, 1);
        // What waits already is no longer timed out either.
        foreach (var timeout in _timeouts.Keys)
        {
            try { timeout.CancelAfter(Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <inheritdoc />
    public override async ValueTask<AgentToolCallDecision> BeforeToolCallAsync(AgentToolHookCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var tool = call.Invocation.ToolName;
        // What the host runs keeps the guards of the host; what an agent CLI runs itself keeps its own prompts.
        if (call.RunByAgent || AutomationAutonomy.HostTools.Contains(tool, StringComparer.Ordinal)) return AgentToolCallDecision.Allow;
        var shown = Shown(tool, call.Invocation.Arguments);
        string reason;
        if (_autonomy.Allows(tool))
        {
            if (DryRun)
            {
                Recorded?.Invoke(shown);
                return AgentToolCallDecision.Block($"This is a try of the automation: the call of {tool} was recorded and not made. Go on as if it had succeeded, and say what you would do next.");
            }

            if (_breaker.TryTake(_entry.Id, _autonomy.MaxCallsPerHour, _time.GetUtcNow())) return AgentToolCallDecision.Allow;
            reason = $"The automation '{_entry.Definition.Name}' called its allowed tools {_autonomy.MaxCallsPerHour} times in the last hour: its circuit breaker asks a person for this call.";
        }
        else
        {
            reason = $"The automation '{_entry.Definition.Name}' may not call {tool} by itself: allow this call once, or deny it.";
        }

        return await AskAsync(tool, shown, reason, call.WorkingDirectory, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentToolCallDecision> AskAsync(string tool, string shown, string reason, string? folder, CancellationToken cancellationToken)
    {
        var request = new AgentCommandPermissionRequest(new ModelProviderId("automation"), string.Empty, _time.GetUtcNow(), null, "automation-" + Guid.NewGuid().ToString("N"),
            null, shown, string.IsNullOrWhiteSpace(folder) ? Environment.CurrentDirectory : folder, null, reason, null, null, null);
        using var timeout = _autonomy.ApprovalTimeout is { } minutes && _autonomy.OnTimeout == AutomationAutonomy.StopOnTimeout && !TakenOver
            ? new CancellationTokenSource(TimeSpan.FromMinutes(minutes), _time) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        _timeouts[timeout] = 0;
        if (TakenOver) timeout.CancelAfter(Timeout.InfiniteTimeSpan);
        Interlocked.Increment(ref _waiting);
        WaitingChanged?.Invoke(true);
        AgentPermissionDecision decision;
        try
        {
            decision = await _ask(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            decision = new(AgentPermissionDecisionKind.Cancel);
        }
        finally
        {
            _timeouts.TryRemove(timeout, out _);
            Interlocked.Decrement(ref _waiting);
            WaitingChanged?.Invoke(false);
        }

        if (decision.Kind is AgentPermissionDecisionKind.AllowOnce or AgentPermissionDecisionKind.AllowAlways or AgentPermissionDecisionKind.AllowForSession)
            return AgentToolCallDecision.Allow;
        if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !TakenOver)
        {
            var why = $"No one answered the call of {tool} within {_autonomy.ApprovalTimeout} minutes: the run stopped.";
            Stopped?.Invoke(why);
            return AgentToolCallDecision.Block(why);
        }

        return AgentToolCallDecision.Block($"A person did not allow the call of {tool}: do not call it again, and say what you would have done.");
    }

    // The tool and its arguments as the person reads them, and as a try records them.
    private static string Shown(string tool, JsonElement arguments)
    {
        var text = arguments.ValueKind is JsonValueKind.Undefined ? "{}" : arguments.GetRawText();
        return tool + " " + (text.Length <= MaximumShownArguments ? text : text[..MaximumShownArguments] + "…");
    }
}
