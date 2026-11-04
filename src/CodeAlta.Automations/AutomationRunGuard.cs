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
    /// <summary>The period of the circuit breaker.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _calls = new(StringComparer.Ordinal);
    private readonly AutomationStateStore? _store;

    /// <summary>Creates a circuit breaker whose calls are forgotten with the host.</summary>
    internal AutomationCallBreaker()
    {
    }

    /// <summary>Creates a circuit breaker that keeps the calls of the hour with the state of the automations: a restart keeps them.</summary>
    /// <param name="store">The state of the automations of the host.</param>
    internal AutomationCallBreaker(AutomationStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>Takes one call of an automation, when it has calls left in the hour.</summary>
    /// <returns>Whether the call may run without a person.</returns>
    internal bool TryTake(string automationId, int? maximum, DateTimeOffset now)
    {
        if (maximum is not { } most) return true;
        lock (_gate)
        {
            if (!_calls.TryGetValue(automationId, out var calls)) _calls[automationId] = calls = new(_store?.Calls(automationId) ?? []);
            while (calls.Count > 0 && now - calls.Peek() >= Window) calls.Dequeue();
            if (calls.Count >= most) return false;
            calls.Enqueue(now);
            _store?.SetCalls(automationId, [.. calls]);
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
    private readonly Func<string, AgentPermissionRequest, CancellationToken, Task<AgentPermissionDecision>> _ask;
    private readonly TimeProvider _time;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<CancellationTokenSource, byte> _timeouts = new();
    private readonly Lock _gate = new();
    private int _waiting;
    private bool _external;
    private bool _shown;
    private int _takenOver;
    private int _stopped;

    /// <summary>Creates the guard of a run.</summary>
    /// <param name="entry">The automation.</param>
    /// <param name="breaker">The circuit breaker of the host.</param>
    /// <param name="ask">Asks a person a permission request of the session of the run.</param>
    /// <param name="time">The clock of the timeouts.</param>
    /// <param name="dryRun">Whether the run is a try: the allowed tools are fakes.</param>
    internal AutomationRunGuard(AutomationEntry entry, AutomationCallBreaker breaker, Func<AgentPermissionRequest, CancellationToken, Task<AgentPermissionDecision>> ask,
        TimeProvider time, bool dryRun)
        : this(entry, breaker, (_, request, token) => ask(request, token), time, dryRun)
    {
    }

    /// <summary>Creates the guard of a run, whose sessions (its own, and those it starts) are asked on their own cards.</summary>
    /// <param name="entry">The automation.</param>
    /// <param name="breaker">The circuit breaker of the host.</param>
    /// <param name="ask">Asks a person a permission request of a session: the one that makes the call.</param>
    /// <param name="time">The clock of the timeouts.</param>
    /// <param name="dryRun">Whether the run is a try: the allowed tools are fakes.</param>
    internal AutomationRunGuard(AutomationEntry entry, AutomationCallBreaker breaker, Func<string, AgentPermissionRequest, CancellationToken, Task<AgentPermissionDecision>> ask,
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
    internal bool Waiting
    {
        get { lock (_gate) return _waiting > 0 || _external; }
    }

    /// <summary>Gets whether a person took the run over: what waits for them is no longer timed out.</summary>
    internal bool TakenOver => Volatile.Read(ref _takenOver) != 0;

    /// <summary>Raised when a call starts or stops waiting for a person.</summary>
    internal event Action<bool>? WaitingChanged;

    /// <summary>Raised with what a try recorded in place of a call of an allowed tool.</summary>
    internal event Action<string>? Recorded;

    /// <summary>Raised when an approval waited too long and the automation stops its runs then, with why.</summary>
    internal event Action<string>? Stopped;

    /// <summary>Says whether an approval of a command or an edit of the session waits now (the host's own guards ask it).</summary>
    internal void NoteWaiting(bool waiting)
    {
        lock (_gate) _external = waiting;
        Raise();
    }

    /// <summary>Stops the run, once, with why: an approval waited too long.</summary>
    internal void Stop(string why)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0) Stopped?.Invoke(why);
    }

    // Tells whether the run waits, when that changed.
    private void Raise()
    {
        bool now;
        lock (_gate)
        {
            now = _waiting > 0 || _external;
            if (now == _shown) return;
            _shown = now;
        }

        WaitingChanged?.Invoke(now);
    }

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
        if (!_autonomy.Allows(tool))
        {
            return await AskAsync(call.Invocation.SessionId, tool, shown, DigestOf(tool, call.Invocation.Arguments), $"The automation '{_entry.Definition.Name}' may not call {tool} by itself: allow this call once, or deny it.",
                call.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        }

        var rules = _autonomy.Tool(tool);
        if (DryRun)
        {
            // A try makes no call, preconditions included; it says what a pin would name, for the person to write it.
            Recorded?.Invoke(shown);
            var pin = call.Spec is { } spec ? $" Its pin: hash = \"{AutomationAllowedTool.HashOf(spec)}\"{(spec.Version is { } version ? $", version = \"{version}\"" : string.Empty)}." : string.Empty;
            return AgentToolCallDecision.Block($"This is a try of the automation: the call of {tool} was recorded and not made.{pin} Go on as if it had succeeded, and say what you would do next.");
        }

        // What its pin and bounds do not hold, and what the circuit breaker stops, waits for a person; a precondition that
        // fails stops the call, and is read again after an approval that waited.
        var held = rules?.CheckPin(call.Spec) ?? rules?.CheckBounds(call.Invocation.Arguments);
        if (held is null)
        {
            if (_breaker.TryTake(_entry.Id, _autonomy.MaxCallsPerHour, _time.GetUtcNow()))
                return await PreconditionsAsync(rules, call, cancellationToken).ConfigureAwait(false) is { } failed ? NotMade(tool, failed) : AgentToolCallDecision.Allow;
            held = $"it called its allowed tools {_autonomy.MaxCallsPerHour} times in the last hour, and its circuit breaker asks a person";
        }

        var decision = await AskAsync(call.Invocation.SessionId, tool, shown, DigestOf(tool, call.Invocation.Arguments), $"The automation '{_entry.Definition.Name}' may not make this call of {tool} by itself: {held}. Allow it once, or deny it.",
            call.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        if (decision.BlockMessage is not null) return decision;
        return await PreconditionsAsync(rules, call, cancellationToken).ConfigureAwait(false) is { } stale ? NotMade(tool, stale) : decision;
    }

    // Calls the preconditions of an allowed tool, each a tool of the session that reads the target: why one does not hold, or null.
    private static async Task<string?> PreconditionsAsync(AutomationAllowedTool? rules, AgentToolHookCall call, CancellationToken cancellationToken)
    {
        if (rules is null) return null;
        foreach (var precondition in rules.Preconditions)
        {
            if (call.FindTool?.Invoke(precondition.Tool) is not { } tool) return $"{precondition.Tool}, which checks it first, is not a tool of this session";
            AgentToolResult result;
            try
            {
                result = await tool.Handler(call.Invocation with
                {
                    ToolName = precondition.Tool, ToolCallId = call.Invocation.ToolCallId + "-precondition", Arguments = precondition.ArgumentsOf(call.Invocation.Arguments),
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return $"{precondition.Tool}, which checks it first, failed: {AutomationText.Line(exception.Message, 200)}";
            }

            var said = string.Join('\n', result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value));
            if (!result.Success || !said.Contains(precondition.Expect.Trim(), StringComparison.OrdinalIgnoreCase))
                return $"{precondition.Tool} did not say “{precondition.Expect.Trim()}”{(result.Success ? string.Empty : ": " + AutomationText.Line(result.Error ?? said, 200))}";
        }

        return null;
    }

    private static AgentToolCallDecision NotMade(string tool, string why)
        => AgentToolCallDecision.Block($"The call of {tool} was not made: {why}. Its target is not as it must be to act; say what you found instead.");

    // Asks a person a call, bound to the digest of the complete call (its approval id): a call shortened to be shown says where
    // the complete arguments are read.
    private async Task<AgentToolCallDecision> AskAsync(string sessionId, string tool, string shown, string digest, string reason, string? folder, CancellationToken cancellationToken)
    {
        if (shown.Contains(digest, StringComparison.Ordinal)) reason += " The call is shortened here: its complete arguments are in the tool call of the session.";
        var request = new AgentCommandPermissionRequest(new ModelProviderId("automation"), string.Empty, _time.GetUtcNow(), null, "automation-" + Guid.NewGuid().ToString("N"),
            digest, shown, string.IsNullOrWhiteSpace(folder) ? Environment.CurrentDirectory : folder, null, reason, null, null, null);
        using var timeout = _autonomy.ApprovalTimeout is { } minutes && _autonomy.OnTimeout == AutomationAutonomy.StopOnTimeout && !TakenOver
            ? new CancellationTokenSource(TimeSpan.FromMinutes(minutes), _time) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        _timeouts[timeout] = 0;
        if (TakenOver) timeout.CancelAfter(Timeout.InfiniteTimeSpan);
        lock (_gate) _waiting++;
        Raise();
        AgentPermissionDecision decision;
        try
        {
            decision = await _ask(sessionId, request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            decision = new(AgentPermissionDecisionKind.Cancel);
        }
        finally
        {
            _timeouts.TryRemove(timeout, out _);
            lock (_gate) _waiting--;
            Raise();
        }

        if (decision.Kind is AgentPermissionDecisionKind.AllowOnce or AgentPermissionDecisionKind.AllowAlways or AgentPermissionDecisionKind.AllowForSession)
            return AgentToolCallDecision.Allow;
        if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !TakenOver)
        {
            var why = $"No one answered the call of {tool} within {_autonomy.ApprovalTimeout} minutes: the run stopped.";
            Stop(why);
            return AgentToolCallDecision.Block(why);
        }

        return AgentToolCallDecision.Block($"A person did not allow the call of {tool}: do not call it again, and say what you would have done.");
    }

    // The tool and its arguments as the person reads them, and as a try records them: a call too long to be shown whole says so,
    // with the digest of the complete call its approval is bound to.
    private static string Shown(string tool, JsonElement arguments)
    {
        var text = arguments.ValueKind is JsonValueKind.Undefined ? "{}" : arguments.GetRawText();
        return tool + " " + (text.Length <= MaximumShownArguments ? text
            : text[..MaximumShownArguments] + $"… (shortened: the complete arguments are {text.Length} characters, {DigestOf(tool, arguments)})");
    }

    /// <summary>
    /// The digest of a call an approval is bound to: SHA-256 of the tool, a line feed and its complete arguments as compact JSON
    /// (without their spacing), as <c>sha256:</c> and lowercase hexadecimal.
    /// </summary>
    internal static string DigestOf(string tool, JsonElement arguments)
    {
        using var buffer = new MemoryStream();
        buffer.Write(System.Text.Encoding.UTF8.GetBytes(tool + "\n"));
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            if (arguments.ValueKind is not JsonValueKind.Undefined) arguments.WriteTo(writer);
            else
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
        }

        return AutomationAllowedTool.HashPrefix + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer.ToArray()));
    }
}
