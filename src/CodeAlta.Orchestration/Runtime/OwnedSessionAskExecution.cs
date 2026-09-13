using System.Text.Json;
using CodeAlta.Agent;

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
        AdditionalTools = System.Array.AsReadOnly<AgentToolDefinition>([.. (options.AdditionalTools ?? []), Tool]),
        RunLifecycle = new Lifecycle(this, options.RunLifecycle),
    };

    internal static AgentRunLifecycle Combine(AgentRunLifecycle? first, AgentRunLifecycle second)
        => first is null ? second : new Combined(first, second);

    private sealed class Combined(AgentRunLifecycle first, AgentRunLifecycle second) : AgentRunLifecycle
    {
        public override async Task StartedAsync(AgentRunId runId, CancellationToken executionToken)
        {
            await first.StartedAsync(runId, executionToken).ConfigureAwait(false);
            await second.StartedAsync(runId, executionToken).ConfigureAwait(false);
        }
        public override async Task ClosingAsync(AgentRunId runId)
        {
            // Start both original closing obligations before joining either, including partial start.
            Task a, b;
            try { a = first.ClosingAsync(runId); } catch (Exception ex) { a = Task.FromException(ex); }
            try { b = second.ClosingAsync(runId); } catch (Exception ex) { b = Task.FromException(ex); }
            var both = Task.WhenAll(a, b);
            try { await both.ConfigureAwait(false); }
            catch { if (a.Exception is not null && b.Exception is not null) throw new AggregateException(a.Exception.InnerExceptions.Concat(b.Exception.InnerExceptions)); throw; }
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

    private sealed class Lifecycle(OwnedSessionAskExecution ask, AgentRunLifecycle? previous) : AgentRunLifecycle
    {
        private readonly object _gate = new();
        private Task? _closing;
        public override async Task StartedAsync(AgentRunId runId, CancellationToken executionToken)
        {
            // AgentSession retains this original start and always invokes Closing, including start failure.
            if (previous is not null) await previous.StartedAsync(runId, executionToken).ConfigureAwait(false);
            await ask.StartedAsync(runId, executionToken).ConfigureAwait(false);
        }
        public override Task ClosingAsync(AgentRunId runId)
        {
            TaskCompletionSource launch;
            Task closing;
            lock (_gate)
            {
                if (_closing is not null) return _closing;
                launch = new(TaskCreationOptions.RunContinuationsAsynchronously);
                closing = _closing = CloseOriginalAsync(runId, launch.Task);
            }
            // Independent ask closure precedes a potentially noncooperative permission join.
            try { ask.Close(); }
            finally { launch.TrySetResult(); }
            return closing;
        }
        private async Task CloseOriginalAsync(AgentRunId runId, Task launch)
        {
            await launch.ConfigureAwait(false);
            if (previous is not null) await previous.ClosingAsync(runId).ConfigureAwait(false);
        }
    }
}
