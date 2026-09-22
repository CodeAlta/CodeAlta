using System.Text.Json;
using System.Runtime.ExceptionServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Orchestration.Runtime;

// A per-send capability. It is never stored in session options or rebound to a replacement operation.
internal sealed class OwnedSessionAskExecution : AgentRunLifecycle
{
    private readonly object _gate = new();
    private readonly OwnedSessionAskService _owner;
    private readonly Guid _operation;
    private readonly string _session;
    private readonly CancellationToken _operationToken;
    private CancellationToken _runToken;
    private Guid _runtime;
    private long _attachment;
    private ModelProviderId _provider;
    private string? _run;
    private bool _bound;
    private bool _closed;
    private bool _committed;
    // Producer work is entirely synchronous and bounded. Retain its original completed task before return.
    private Task<AgentToolResult>? _committedInvocation;

    internal OwnedSessionAskExecution(OwnedSessionAskService owner, Guid operation, string session, CancellationToken token)
    {
        _owner = owner; _operation = operation; _session = session; _operationToken = token;
        Tool = OwnedSessionAskTool.Create(Invoke);
    }

    internal AgentToolDefinition Tool { get; }

    internal void Bind(Guid runtime, long attachment, ModelProviderId provider)
    {
        lock (_gate)
        {
            if (_closed || _bound || runtime == Guid.Empty || attachment is <= 0 or > 9007199254740991
                || !OwnedSessionAskTool.Identity(provider.Value)) throw new InvalidOperationException("Ask execution cannot bind.");
            _runtime = runtime; _attachment = attachment; _provider = provider; _bound = true;
        }
    }

    public override Task StartedAsync(AgentRunId runId, CancellationToken executionToken)
    {
        lock (_gate)
        {
            if (_closed || !_bound || _run is not null || !OwnedSessionAskTool.Identity(runId.Value))
                throw new InvalidOperationException("Ask execution requires its original actual run binding.");
            _operationToken.ThrowIfCancellationRequested();
            executionToken.ThrowIfCancellationRequested();
            _runToken = executionToken;
            _run = runId.Value;
        }
        return Task.CompletedTask;
    }

    public override Task ClosingAsync(AgentRunId runId) { Close(); return Task.CompletedTask; }

    // Acquiring the gate joins any synchronous producer body. No answer dispatch is joined here.
    internal void Close() { lock (_gate) _closed = true; }

    internal AgentSendOptions Compose(AgentSendOptions options) => new()
    {
        Input = options.Input, AskId = options.AskId, OnPermissionRequest = options.OnPermissionRequest,
        OnUserInputRequest = options.OnUserInputRequest,
        EnableUserInputTool = options.EnableUserInputTool,
        AdditionalTools = System.Array.AsReadOnly<AgentToolDefinition>([.. (options.AdditionalTools ?? []), Tool]),
        RunLifecycle = new Lifecycle(this, options.RunLifecycle),
    };

    internal static AgentRunLifecycle Combine(AgentRunLifecycle? first, AgentRunLifecycle second)
        => first is null ? second : new Combined(first, second);

    private sealed class Combined(AgentRunLifecycle first, AgentRunLifecycle second) : AgentRunLifecycle, AgentSession.IRunStartEvidence
    {
        private readonly object _gate = new();
        private ClosingPair? _closing;
        private readonly AgentSession.RunStartInvocation _firstStart = new(OwnedProviderEventForwarding.HasRetention);
        private readonly AgentSession.RunStartInvocation _secondStart = new(OwnedProviderEventForwarding.HasRetention);
        AgentSession.RunStartInvocation? AgentSession.IRunStartEvidence.FailedStart
            => _firstStart.Failure is not null ? _firstStart : _secondStart.Failure is not null ? _secondStart : null;
        public override async Task StartedAsync(AgentRunId runId, CancellationToken executionToken)
        {
            await _firstStart.RunAsync(first, runId, executionToken).ConfigureAwait(false);
            await _secondStart.RunAsync(second, runId, executionToken).ConfigureAwait(false);
        }
        public override Task ClosingAsync(AgentRunId runId)
        {
            ClosingPair closing;
            lock (_gate)
            {
                if (_closing is not null) return _closing.Completion;
                closing = _closing = new ClosingPair(() => first.ClosingAsync(runId), () => second.ClosingAsync(runId));
            }
            closing.Launch();
            return closing.Completion;
        }
    }

    private Task<AgentToolResult> Invoke(AgentToolInvocation invocation, CancellationToken token)
    {
        lock (_gate)
        {
            if (_closed || !_bound || _run is null || _committed || _operationToken.IsCancellationRequested
                || _runToken.IsCancellationRequested || token.IsCancellationRequested
                || invocation.ProviderId != _provider || invocation.SessionId != _session)
                return Task.FromResult(new AgentToolResult(false, [], "Ask producer is unavailable."));
            try
            {
                var request = OwnedSessionAskTool.ParseInvocation(invocation);
                var queued = _owner.Queue(new(_operation, _runtime, _attachment, _provider.Value, _session, _run, "", 0), request);
                if (queued is null) return Task.FromResult(new AgentToolResult(false, [], "Ask capacity or admission is closed."));
                _committed = true;
                var reply = new OwnedAskToolReply("alta.ask.queued", queued.AskId, _session, false, true);
                _committedInvocation = Task.FromResult(new AgentToolResult(true,
                    [new AgentToolResultItem.Text(JsonSerializer.Serialize(reply, OwnedAskJsonContext.Default.OwnedAskToolReply))]));
                return _committedInvocation;
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException)
            { return Task.FromResult(new AgentToolResult(false, [], "Invalid restricted ask request.")); }
        }
    }

    internal sealed class Lifecycle(AgentRunLifecycle ask, AgentRunLifecycle? previous) : AgentRunLifecycle, AgentSession.IRunStartEvidence
    {
        private readonly object _gate = new();
        private ClosingPair? _closing;
        private readonly AgentSession.RunStartInvocation _previousStart = new(OwnedProviderEventForwarding.HasRetention);
        private readonly AgentSession.RunStartInvocation _askStart = new(OwnedProviderEventForwarding.HasRetention);
        AgentSession.RunStartInvocation? AgentSession.IRunStartEvidence.FailedStart
            => _previousStart.Failure is not null ? _previousStart : _askStart.Failure is not null ? _askStart : null;
        public override async Task StartedAsync(AgentRunId runId, CancellationToken executionToken)
        {
            // AgentSession retains this original start and always invokes Closing, including start failure.
            if (previous is not null) await _previousStart.RunAsync(previous, runId, executionToken).ConfigureAwait(false);
            await _askStart.RunAsync(ask, runId, executionToken).ConfigureAwait(false);
        }
        public override Task ClosingAsync(AgentRunId runId)
        {
            ClosingPair closing;
            lock (_gate)
            {
                if (_closing is not null) return _closing.Completion;
                closing = _closing = new ClosingPair(
                    () => ask.ClosingAsync(runId),
                    () => previous is null ? Task.CompletedTask : previous.ClosingAsync(runId), firstRequired: true);
            }
            // Independent ask closure precedes a potentially noncooperative permission join.
            closing.Launch();
            return closing.Completion;
        }
    }

    internal sealed class ClosingPair
    {
        private readonly bool _firstRequired;
        internal ClosingPair(Func<Task> first, Func<Task> second, bool firstRequired = false)
        {
            _firstRequired = firstRequired;
            First = new SessionPermissionService.DeliveryStage(first);
            Second = new SessionPermissionService.DeliveryStage(second);
            Completion = CompleteAsync();
        }
        internal SessionPermissionService.DeliveryStage First { get; }
        internal SessionPermissionService.DeliveryStage Second { get; }
        internal Task Completion { get; }
        internal void Launch() { First.Launch(); Second.Launch(); }
        private async Task CompleteAsync()
        {
            var first = await First.Outcome.ConfigureAwait(false);
            var second = await Second.Outcome.ConfigureAwait(false);
            var failures = new List<Exception>();
            if (first is not null) failures.Add(First.Failure!);
            if (second is not null) failures.Add(Second.Failure!);
            if (First.Original is null || Second.Original is null
                || (_firstRequired && first is not null) || failures.Any(OwnedProviderEventForwarding.HasRetention))
                throw new AgentDependencyRetentionException("owned lifecycle", "closing", failures, this);
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException(failures);
        }
    }
}
