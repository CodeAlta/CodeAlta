using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
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
/// may start sessions too, <see cref="MaxDepth"/> levels deep. A session a script keeps (<c>session()</c>) is closed when
/// the script closes it, or when its run ends.
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
        var state = _runs.GetOrAdd(request.RunId, static _ => new RunState());
        await state.AtWork.WaitAsync(cancellationToken).ConfigureAwait(false);
        HeadlessSession? session = null;
        try
        {
            var (opened, refusal, warning) = await OpenCoreAsync(parentSessionId, request, state, cancellationToken).ConfigureAwait(false);
            if (opened is null) return refusal! with { Warning = warning };
            session = opened;
            return await AskCoreAsync(opened, request.RunId, request.StepId, request.Prompt, request.Schema, request.Timeout, clock, cancellationToken).ConfigureAwait(false)
                with { Warning = warning };
        }
        finally
        {
            if (session is not null) await _driver.CloseAsync(session).ConfigureAwait(false);
            state.AtWork.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CodeModeAgentResult> OpenAsync(string parentSessionId, CodeModeAgentRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentSessionId);
        ArgumentNullException.ThrowIfNull(request);
        var state = _runs.GetOrAdd(request.RunId, static _ => new RunState());
        var (opened, refusal, warning) = await OpenCoreAsync(parentSessionId, request, state, cancellationToken).ConfigureAwait(false);
        if (opened is null) return refusal! with { Warning = warning };
        state.Kept[opened.SessionId] = (opened, request.StepId);
        return new CodeModeAgentResult(opened.SessionId, true, "opened", null, null, SessionUsageTotals.None, TimeSpan.Zero) { Warning = warning };
    }

    /// <inheritdoc />
    public async Task<CodeModeAgentResult> AskAsync(string runId, string sessionId, string prompt, JsonElement? schema, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var clock = Stopwatch.StartNew();
        if (!_runs.TryGetValue(runId, out var state) || !state.Kept.TryGetValue(sessionId, out var kept))
            return new(sessionId, false, "refused", null, $"The session '{sessionId}' is not a session this script keeps open.", SessionUsageTotals.None, clock.Elapsed);
        if (schema is { } asked && StructuredAnswer.Inspect(asked) is { Count: > 0 } problems)
            return new(sessionId, false, "refused", null, SchemaRefusal(problems), SessionUsageTotals.None, clock.Elapsed);
        // One prompt at a time per session; the session takes a slot of the run while it works.
        await kept.Session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await state.AtWork.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await AskCoreAsync(kept.Session.Session, runId, kept.StepId, prompt, schema, timeout, clock, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                state.AtWork.Release();
            }
        }
        finally
        {
            kept.Session.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(string runId, string sessionId)
    {
        if (_runs.TryGetValue(runId, out var state) && state.Kept.TryRemove(sessionId, out var kept))
        {
            await _driver.CloseAsync(kept.Session.Session).ConfigureAwait(false);
        }
    }

    /// <summary>Closes the sessions a run kept open, and forgets its counts.</summary>
    /// <param name="runId">The run.</param>
    /// <returns>A task that completes when they are closed.</returns>
    public async Task ForgetRunAsync(string runId)
    {
        if (!_runs.TryRemove(runId, out var state)) return;
        foreach (var (_, kept) in state.Kept)
        {
            await _driver.CloseAsync(kept.Session.Session).ConfigureAwait(false);
        }

        state.Kept.Clear();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _driver.DisposeAsync();

    private async Task<(KeptSession? Session, CodeModeAgentResult? Refusal, string? Warning)> OpenCoreAsync(string parentSessionId, CodeModeAgentRequest request, RunState state,
        CancellationToken cancellationToken)
    {
        var runtime = _host.RuntimeService;
        var parent = await runtime.TryGetResumableSessionAsync(parentSessionId, cancellationToken).ConfigureAwait(false);
        if (parent is null) return Refused($"The session '{parentSessionId}' that runs the script is not known.");
        if (await DepthAsync(parent, cancellationToken).ConfigureAwait(false) >= MaxDepth)
            return Refused($"A script of a session started by scripts {MaxDepth} levels deep starts no session.");
        if (request.BudgetTokens is { } budget && Interlocked.Read(ref state.SpentTokens) is var spent && spent >= budget)
            return Refused($"The budget of this script is spent: its sessions used {spent:N0} tokens of {budget:N0}.");
        if (Interlocked.Increment(ref state.Started) > MaxPerRun)
            return Refused($"This script already started {MaxPerRun} sessions, the most for one run.");
        if (request.Schema is { } asked && StructuredAnswer.Inspect(asked) is { Count: > 0 } problems) return Refused(SchemaRefusal(problems));
        if (request.Sandbox is { } sandbox && await RefuseSandboxAsync(parentSessionId, sandbox, cancellationToken).ConfigureAwait(false) is { } sandboxRefusal)
            return Refused(sandboxRefusal);

        ModelTier? tier = null;
        if (request.Tier is { } tierName && (tier = ModelTiers.Parse(tierName)) is null)
            return Refused($"'{tierName}' is not a tier: name complex, standard or quick.");
        if (request.ModelRef is not null && tier is not null) return Refused("Name either a model or a tier, not both.");

        // Scripts are examples people copy into any profile: a tier that is not set up there (no model for it, or no tiers
        // at all) runs on the model of the session that runs the script, and the run is told once per tier. A tier that
        // has models keeps its fallback, and its error when none of them can run.
        string? warning = null;
        if (tier is { } askedTier && (await runtime.LoadModelTierScopeAsync(parent.SpaceId, parent.ProjectRef, cancellationToken).ConfigureAwait(false)).Tiers.Get(askedTier).Count == 0)
        {
            tier = null;
            if (state.WarnedTiers.TryAdd(askedTier, 0))
                warning = $"tier `{ModelTiers.NameOf(askedTier)}` is not set up: its sessions use this session's model ({ModelRefOf(parent) ?? parent.ResolvedProviderKey}).";
        }

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
        if (opened.Session is not { } created)
            return (null, new(null, false, "refused", null, opened.Message ?? $"The session could not be created ({opened.Status}).", SessionUsageTotals.None, TimeSpan.Zero), warning);
        _host.CodeModeRuns.Record(request.RunId, AgentWorkflowEventKind.SessionStarted, record => record with
        {
            ChildSessionId = created.SessionId,
            StepId = request.StepId,
            Title = request.Title ?? created.Descriptor.Title,
        });
        return (new KeptSession(created), null, warning);

        static (KeptSession?, CodeModeAgentResult?, string?) Refused(string message) => (null, new(null, false, "refused", null, message, SessionUsageTotals.None, TimeSpan.Zero), null);
    }

    private async Task<CodeModeAgentResult> AskCoreAsync(HeadlessSession session, string runId, string? stepId, string prompt, JsonElement? schema, TimeSpan? timeout,
        Stopwatch clock, CancellationToken cancellationToken)
    {
        var text = schema is { } asked ? prompt + StructuredAnswer.Instruction(asked) : prompt;
        var run = await _driver.RunAsync(session, new HeadlessPrompt(text) { Provenance = AgentPromptProvenance.Workflow }, timeout, cancellationToken).ConfigureAwait(false);
        string? value = null;
        if (schema is { } expected && run.Status == HeadlessRunStatus.Completed)
        {
            // An answer that does not match is asked once more, with what is wrong; a second one fails the session.
            var (json, errors) = StructuredAnswer.Check(run.FinalMessage, expected);
            if (json is null)
            {
                var usage = run.Usage;
                run = await _driver.RunAsync(session, new HeadlessPrompt(
                    $"Your final answer is not the JSON value asked for: {string.Join(" ", errors)} Answer again with only one JSON value that matches the schema.")
                    { Provenance = AgentPromptProvenance.Workflow }, timeout, cancellationToken).ConfigureAwait(false);
                run = run with { Usage = Add(usage, run.Usage) };
                (json, errors) = run.Status == HeadlessRunStatus.Completed ? StructuredAnswer.Check(run.FinalMessage, expected) : (null, []);
                if (json is null && run.Status == HeadlessRunStatus.Completed)
                    run = run with { Status = HeadlessRunStatus.Failed, FailureMessage = $"The answer does not match the schema: {string.Join(" ", errors)}" };
            }

            value = json;
        }

        var status = run.Status.ToString().ToLowerInvariant();
        if (_runs.TryGetValue(runId, out var state)) Interlocked.Add(ref state.SpentTokens, run.Usage.InputTokens + run.Usage.OutputTokens);
        _host.CodeModeRuns.Record(runId, AgentWorkflowEventKind.SessionEnded, record => record with
        {
            ChildSessionId = session.SessionId,
            StepId = stepId,
            Status = status,
            Message = run.Status == HeadlessRunStatus.Completed ? null : run.FailureMessage,
            DurationMs = (long)run.Duration.TotalMilliseconds,
            InputTokens = run.Usage.InputTokens,
            OutputTokens = run.Usage.OutputTokens,
        });
        return new CodeModeAgentResult(session.SessionId, run.Status == HeadlessRunStatus.Completed, status, run.FinalMessage,
            run.Status == HeadlessRunStatus.Completed ? null : run.FailureMessage ?? $"The session ended: {status}.", run.Usage, clock.Elapsed) { Value = value };
    }

    // The sandbox a script asks for its session, under the rules of the model's own `alta session create --sandbox`: a
    // session started by the script of a sandboxed session has that sandbox and no other, and a script turns a sandbox on,
    // never off. Null when the choice stands.
    private async Task<string?> RefuseSandboxAsync(string parentSessionId, string asked, CancellationToken cancellationToken)
    {
        var own = await _host.RuntimeService.GetSessionSandboxAsync(parentSessionId, cancellationToken).ConfigureAwait(false);
        if (own.IsSandboxed)
            return $"The session that runs the script is sandboxed (profile '{own.Choice.Profile}'): the sessions it starts have its sandbox, and the script chooses no other. Leave `sandbox` out.";
        var wanted = asked.Trim();
        return wanted.Equals(CodeAlta.Sandbox.SandboxProfiles.Off, StringComparison.OrdinalIgnoreCase) || wanted.Equals(OwnedSessionCommandService.ScopeSandbox, StringComparison.OrdinalIgnoreCase)
            ? "A script turns a sandbox on, never off: the user chooses where sessions run without one. Leave `sandbox` out, or name a profile."
            : null;
    }

    private static string SchemaRefusal(IReadOnlyList<string> problems)
        => $"The schema uses what the check does not cover: {string.Join("; ", problems)}. It checks {string.Join(", ", StructuredAnswer.CheckedKeywords)}.";

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

    private static SessionUsageTotals Add(SessionUsageTotals a, SessionUsageTotals b)
        => new(a.OperationCount + b.OperationCount, a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens, a.CachedInputTokens + b.CachedInputTokens,
            a.ReasoningTokens + b.ReasoningTokens, a.CacheWriteTokens + b.CacheWriteTokens,
            a.Cost is null && b.Cost is null ? null : (a.Cost ?? 0) + (b.Cost ?? 0), a.CostUnit ?? b.CostUnit);

    private static string? ModelRefOf(SessionViewDescriptor session)
        => string.IsNullOrWhiteSpace(session.ModelId) ? null : $"{session.ResolvedProviderKey}:{session.ModelId}";

    // A session of the driver, and the gate of the prompts sent to it.
    private sealed class KeptSession(HeadlessSession session)
    {
        public HeadlessSession Session { get; } = session;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public string SessionId => Session.SessionId;

        public static implicit operator HeadlessSession(KeptSession kept) => kept.Session;
    }

    private sealed class RunState
    {
        public readonly SemaphoreSlim AtWork = new(MaxAtWork, MaxAtWork);
        public readonly ConcurrentDictionary<string, (KeptSession Session, string? StepId)> Kept = new(StringComparer.OrdinalIgnoreCase);
        public readonly ConcurrentDictionary<ModelTier, byte> WarnedTiers = new();
        public int Started;
        public long SpentTokens;
    }
}
