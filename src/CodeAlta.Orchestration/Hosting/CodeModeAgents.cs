using System.Collections.Concurrent;
using System.Diagnostics;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.Orchestration.Hosting;

/// <summary>
/// The sessions scripts start, through the headless driver of the host. A child is created by kind <c>workflow</c>, in the
/// project of the script's session, as its child: it is in its tree (aborts, retention), takes the sandbox and the tools
/// its creator gives the sessions it creates, and its answer goes to the script. A run may have
/// <see cref="MaxAtWork"/> sessions at work and <see cref="MaxPerRun"/> in all; a script of a session a script started
/// may start sessions too, <see cref="MaxDepth"/> levels deep.
/// </summary>
public sealed class CodeModeAgents : ICodeModeAgents, IAsyncDisposable
{
    /// <summary>The most sessions of one run at work at the same time; the others wait for one to end.</summary>
    public const int MaxAtWork = 8;

    /// <summary>The most sessions one run starts.</summary>
    public const int MaxPerRun = 50;

    /// <summary>The most levels of sessions started by scripts of sessions started by scripts.</summary>
    public const int MaxDepth = 3;

    private readonly CodeAltaHost _host;
    private readonly HeadlessSessionDriver _driver;
    private readonly ConcurrentDictionary<string, RunState> _runs = new(StringComparer.Ordinal);

    /// <summary>Creates the service of a host.</summary>
    /// <param name="host">The host, borrowed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
    public CodeModeAgents(CodeAltaHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _driver = new HeadlessSessionDriver(host);
    }

    /// <inheritdoc />
    public async Task<CodeModeAgentResult> RunAsync(string parentSessionId, CodeModeAgentRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentSessionId);
        ArgumentNullException.ThrowIfNull(request);
        var clock = Stopwatch.StartNew();
        var runtime = _host.RuntimeService;
        var parent = await runtime.TryGetResumableSessionAsync(parentSessionId, cancellationToken).ConfigureAwait(false);
        if (parent is null) return Refused($"The session '{parentSessionId}' that runs the script is not known.");
        if (await DepthAsync(parent, cancellationToken).ConfigureAwait(false) >= MaxDepth)
            return Refused($"A script of a session started by scripts {MaxDepth} levels deep starts no session.");

        var state = _runs.GetOrAdd(request.RunId, static _ => new RunState());
        if (Interlocked.Increment(ref state.Started) > MaxPerRun)
            return Refused($"This script already started {MaxPerRun} sessions, the most for one run.");

        ModelTier? tier = null;
        if (request.Tier is { } tierName && (tier = ModelTiers.Parse(tierName)) is null)
            return Refused($"'{tierName}' is not a tier: name complex, standard or quick.");
        if (request.ModelRef is not null && tier is not null) return Refused("Name either a model or a tier, not both.");

        await state.AtWork.WaitAsync(cancellationToken).ConfigureAwait(false);
        HeadlessSession? session = null;
        try
        {
            var opened = await _driver.OpenAsync(new HeadlessSessionRequest
            {
                ProjectFolder = parent.WorktreeDirectory ?? parent.WorkingDirectory,
                ModelRef = request.ModelRef ?? (tier is null ? ModelRefOf(parent) : null),
                Tier = tier,
                Reasoning = request.Effort ?? (request.ModelRef is null && tier is null ? parent.ReasoningEffort : null),
                AgentPromptId = request.AgentPromptId,
                Title = request.Title,
                Sandbox = request.Sandbox,
                DisabledToolPatterns = runtime.GetDisabledToolPatterns(parentSessionId),
                ParentSessionId = parentSessionId,
                CreatedBy = new AltaActorProvenance
                {
                    Kind = AltaActorProvenance.WorkflowKind,
                    SourceSessionId = parentSessionId,
                    SourceProjectId = parent.ProjectRef,
                    CorrelationId = request.StepId is null ? $"workflow:{request.RunId}" : $"workflow:{request.RunId}/{request.StepId}",
                    PromptProvenance = AgentPromptProvenance.Workflow,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
            }, cancellationToken).ConfigureAwait(false);
            if (opened.Session is not { } created) return Refused(opened.Message ?? $"The session could not be created ({opened.Status}).");
            session = created;
            _host.CodeModeRuns.Record(request.RunId, AgentWorkflowEventKind.SessionStarted, record => record with
            {
                ChildSessionId = created.SessionId,
                StepId = request.StepId,
                Title = request.Title ?? created.Descriptor.Title,
            });
            var run = await _driver.RunAsync(created, new HeadlessPrompt(request.Prompt) { Provenance = AgentPromptProvenance.Workflow }, request.Timeout, cancellationToken)
                .ConfigureAwait(false);
            var status = run.Status.ToString().ToLowerInvariant();
            _host.CodeModeRuns.Record(request.RunId, AgentWorkflowEventKind.SessionEnded, record => record with
            {
                ChildSessionId = created.SessionId,
                StepId = request.StepId,
                Status = status,
                Message = run.Status == HeadlessRunStatus.Completed ? null : run.FailureMessage,
                DurationMs = (long)run.Duration.TotalMilliseconds,
                InputTokens = run.Usage.InputTokens,
                OutputTokens = run.Usage.OutputTokens,
            });
            return new CodeModeAgentResult(created.SessionId, run.Status == HeadlessRunStatus.Completed, status, run.FinalMessage,
                run.Status == HeadlessRunStatus.Completed ? null : run.FailureMessage ?? $"The session ended: {status}.", run.Usage, clock.Elapsed);
        }
        finally
        {
            if (session is not null) await _driver.CloseAsync(session).ConfigureAwait(false);
            state.AtWork.Release();
        }

        CodeModeAgentResult Refused(string message) => new(null, false, "refused", null, message, SessionUsageTotals.None, clock.Elapsed);
    }

    /// <summary>Forgets the counts of a run that ended.</summary>
    /// <param name="runId">The run.</param>
    public void ForgetRun(string runId)
    {
        if (_runs.TryRemove(runId, out var state)) state.AtWork.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _driver.DisposeAsync();

    // How many sessions started by scripts are above this one.
    private async Task<int> DepthAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
    {
        var depth = 0;
        for (var current = session; current is { CreatedBy.Kind: AltaActorProvenance.WorkflowKind, ParentSessionId: { } parentId } && depth <= MaxDepth;)
        {
            depth++;
            current = await _host.RuntimeService.TryGetResumableSessionAsync(parentId, cancellationToken).ConfigureAwait(false);
        }

        return depth;
    }

    private static string? ModelRefOf(SessionViewDescriptor session)
        => string.IsNullOrWhiteSpace(session.ModelId) ? null : $"{session.ResolvedProviderKey}:{session.ModelId}";

    private sealed class RunState
    {
        public readonly SemaphoreSlim AtWork = new(MaxAtWork, MaxAtWork);
        public int Started;
    }
}
