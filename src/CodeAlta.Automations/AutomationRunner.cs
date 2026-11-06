using CodeAlta.Agent;
using CodeAlta.Agent.Runtime.Tools;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.Scripts;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Automations;

/// <summary>How a run of an automation ended.</summary>
/// <param name="Status">One of the status constants of <see cref="AutomationRun"/>.</param>
/// <param name="Message">Why it did not complete.</param>
internal sealed record AutomationOutcome(string Status, string? Message = null)
{
    /// <summary>What the session of the run used, as its usage records say; null when it could not be read.</summary>
    public AutomationUsage? Usage { get; init; }

    /// <summary>The last answer of the session, which a trigger of another tool writes back to its source; null when it could not be read.</summary>
    public string? Answer { get; init; }

    /// <summary>What was done with <see cref="Answer"/>, set by the service.</summary>
    public string? Reply { get; init; }

    /// <summary>The answer a try kept, set by the service.</summary>
    public string? Output { get; init; }
}

/// <summary>What the session of a run used: its tokens, and its cost when the provider reported one, in one unit.</summary>
/// <param name="InputTokens">The input tokens of its requests, the prompt cache included; null when none was reported.</param>
/// <param name="OutputTokens">The output tokens of its requests; null when none was reported.</param>
/// <param name="Cost">The cost the provider reported; null when none was, or in more than one unit.</param>
/// <param name="CostUnit">The unit of <paramref name="Cost"/>: <c>usd</c> unless the provider named another, such as <c>AI credits</c>.</param>
internal sealed record AutomationUsage(long? InputTokens, long? OutputTokens, double? Cost, string? CostUnit)
{
    /// <summary>Sums the usage records of a session.</summary>
    /// <param name="updates">Its usage records.</param>
    /// <returns>The sum; null when the records report nothing, or something that does not add up.</returns>
    internal static AutomationUsage? Sum(IEnumerable<CodeAlta.Agent.Runtime.AgentSessionUsageUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        long? input = null, output = null;
        double? cost = null;
        string? unit = null;
        var units = 0;
        foreach (var update in updates)
        {
            var usage = update.Operation;
            if (usage.InputTokens is < 0 || usage.OutputTokens is < 0) return null;
            if (usage.InputTokens is { } read) input = (input ?? 0) + read;
            if (usage.OutputTokens is { } written) output = (output ?? 0) + written;
            if (usage.Cost is not { } amount) continue;
            if (!double.IsFinite(amount) || amount < 0) return null;
            var named = string.IsNullOrWhiteSpace(usage.CostUnit) ? "usd" : usage.CostUnit.Trim();
            if (unit is null) { unit = named; units = 1; }
            else if (!string.Equals(unit, named, StringComparison.OrdinalIgnoreCase)) units++;
            cost = (cost ?? 0) + amount;
        }

        // Costs in two units do not add up: the tokens are kept, the cost is not.
        if (units > 1) (cost, unit) = (null, null);
        return input is null && output is null && cost is null ? null : new AutomationUsage(input, output, cost is { } total ? Math.Round(total, 10) : null, unit);
    }
}

/// <summary>What starting a run gave.</summary>
/// <param name="SessionId">The session that was created; null when none was.</param>
/// <param name="Problem">Why the run did not start; null when it did.</param>
/// <param name="Completion">Completes when the session has answered, failed or been stopped; null when the run did not start.</param>
internal sealed record AutomationStart(string? SessionId, string? Problem, Task<AutomationOutcome>? Completion)
{
    /// <summary>The guard of the session of a run of an automation with autonomy; null for one without.</summary>
    public AutomationRunGuard? Guard { get; init; }
}

/// <summary>
/// What a host checks before a run starts, once the provider it would run with is known: a node checks its own local
/// policy and the credential kind of the run (<c>doc/fleet/node.md</c>, "Automations and statistics").
/// </summary>
internal interface IAutomationRunGate
{
    /// <summary>Whether a run may start here with a provider.</summary>
    /// <param name="entry">The automation.</param>
    /// <param name="provider">The provider the run would start with.</param>
    /// <returns>Why the run may not start; null when it may.</returns>
    string? Check(AutomationEntry entry, ModelProviderDescriptor provider);

    /// <summary>
    /// Whether a run may start in its space with a provider and a model, once both are known: what the work in that space must
    /// keep to (the signed policy of the organization it is linked to, on a machine of a fleet).
    /// </summary>
    /// <param name="entry">The automation.</param>
    /// <param name="provider">The provider the run would start with.</param>
    /// <param name="modelId">The model it would use.</param>
    /// <param name="spaceId">The space of the run.</param>
    /// <returns>Why the run may not start; null when it may.</returns>
    string? CheckModel(AutomationEntry entry, ModelProviderDescriptor provider, string modelId, string spaceId) => null;

    /// <summary>
    /// Admits a run to the environment its automation names (<c>doc/fleet/secrets.md</c>, environments): its rules hold
    /// on this machine and, when it requires one, an approver approved this run. It may wait for the approval.
    /// </summary>
    /// <param name="entry">The automation.</param>
    /// <param name="runId">The run, which the approval is bound to.</param>
    /// <param name="environment">The name of the environment.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What admitted the run, or why it may not start.</returns>
    Task<EnvironmentAdmissionResult> AdmitEnvironmentAsync(AutomationEntry entry, string runId, string environment, CancellationToken cancellationToken)
        => Task.FromResult(EnvironmentAdmissionResult.Refused($"This machine runs no automation in the environment '{environment}': environments are given by a server."));

    /// <summary>
    /// Admits a run to the budgets of the machine (<c>doc/fleet/observability.md</c>, budgets and alerts): an enforced budget
    /// while the machine holds enough of it, an advisory one until it is reached. A reached budget stops the run or holds it
    /// until its owner continues it anyway, which may wait.
    /// </summary>
    /// <param name="entry">The automation.</param>
    /// <param name="runId">The run, which a Continue anyway is bound to.</param>
    /// <param name="project">The project of the run; null for a chat.</param>
    /// <param name="spaceId">The space of the run.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>Why the run may not start; null when it may.</returns>
    Task<string?> AdmitBudgetAsync(AutomationEntry entry, string runId, ProjectDescriptor? project, string spaceId, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// Gets what the work in the space of a run must keep to besides its provider and model (the signed policy of the
    /// organization it is linked to, on a machine of a fleet): the permission mode, the sandbox and the tools off its session
    /// runs with, whatever its definition asks, or why it may not run at all.
    /// </summary>
    /// <param name="entry">The automation.</param>
    /// <param name="provider">The provider the run would start with.</param>
    /// <param name="spaceId">The space of the run.</param>
    /// <returns>The limits; <see cref="AutomationRunLimits.None"/> when nothing limits the run.</returns>
    AutomationRunLimits LimitsFor(AutomationEntry entry, ModelProviderDescriptor provider, string spaceId) => AutomationRunLimits.None;

    /// <summary>
    /// Gives the session of a run the folder it works in (<c>doc/fleet/node.md</c>, "Workspaces"): on a machine of a fleet, a
    /// worktree of its own in a git repository, so that the runs and the other sessions of the machine never share a checkout.
    /// </summary>
    /// <param name="project">The project of the run; null for a chat.</param>
    /// <param name="cancellationToken">Stops the creation.</param>
    /// <returns>The folder; <see cref="AutomationRunFolder.Project"/> for the folder of the project, the default.</returns>
    Task<AutomationRunFolder> PrepareFolderAsync(ProjectDescriptor? project, CancellationToken cancellationToken) => Task.FromResult(AutomationRunFolder.Project);

    /// <summary>Takes back a folder <see cref="PrepareFolderAsync"/> made when no session came of it.</summary>
    /// <param name="project">The project of the run; null for a chat.</param>
    /// <param name="folder">The folder.</param>
    /// <returns>A task that completes once it is taken back.</returns>
    Task ReleaseFolderAsync(ProjectDescriptor? project, AutomationRunFolder folder) => Task.CompletedTask;

    /// <summary>
    /// Records whose work the session of a run is, before it attaches (<c>doc/fleet/security-and-privacy.md</c>, shared machines):
    /// a run that is not positively the work of the owner of the machine (an automation of a team or of the organization, or one
    /// another person gave) resolves none of the owner's own values. Nothing to record on a host whose every session is its owner's.
    /// </summary>
    /// <param name="entry">The automation.</param>
    /// <param name="sessionId">The session of the run.</param>
    /// <exception cref="IOException">It could not be recorded: the run does not start.</exception>
    void Attribute(AutomationEntry entry, string sessionId)
    {
    }
}

/// <summary>What a run keeps to besides its provider and model: given to its session before its first prompt.</summary>
/// <param name="Problem">Why the run may not start; null when it may.</param>
/// <param name="PermissionMode">The permission mode its session runs in; null for the one its definition names.</param>
/// <param name="Sandbox">The sandbox its session runs in; null for the one its definition names.</param>
/// <param name="ToolsOff">The tools turned off in its session; null for none.</param>
internal sealed record AutomationRunLimits(string? Problem, string? PermissionMode, string? Sandbox, IReadOnlyList<string>? ToolsOff)
{
    /// <summary>Gets the limits of a run nothing limits.</summary>
    public static AutomationRunLimits None { get; } = new(null, null, null, null);

    /// <summary>Gets the limits of a run that may not start.</summary>
    /// <param name="problem">Why.</param>
    /// <returns>The limits.</returns>
    public static AutomationRunLimits Refused(string problem) => new(problem, null, null, null);
}

/// <summary>The folder the session of a run works in.</summary>
/// <param name="Worktree">The worktree folder it works in; null for the folder of its project.</param>
/// <param name="Root">The root of a worktree made for it, taken back when no session comes of it; null for none.</param>
/// <param name="Problem">Why no folder could be given and the run does not start; null when one was.</param>
internal sealed record AutomationRunFolder(string? Worktree, string? Root, string? Problem)
{
    /// <summary>Gets the folder of a run that works in the folder of its project.</summary>
    public static AutomationRunFolder Project { get; } = new(null, null, null);
}

/// <summary>Starts the session of a run and sends it the prompt.</summary>
internal interface IAutomationRunner
{
    /// <summary>Creates a session where the automation runs and sends it the prompt.</summary>
    /// <param name="entry">The automation.</param>
    /// <param name="runId">The identifier of the run, which makes the send one that is never sent twice.</param>
    /// <param name="prompt">The text to send.</param>
    /// <param name="detail">What the trigger was about, such as the issue that was opened: it names the session with the automation.</param>
    /// <param name="cancellationToken">Cancels the preparation of the run; a session that was already sent its prompt keeps going.</param>
    /// <returns>The session and its completion, or why the run did not start. Failures are results, not exceptions.</returns>
    Task<AutomationStart> StartAsync(AutomationEntry entry, string runId, string prompt, string? detail, CancellationToken cancellationToken);

    /// <summary>Creates a session where the automation runs, as <see cref="StartAsync(AutomationEntry, string, string, string?, CancellationToken)"/>, for a try or not.</summary>
    /// <param name="entry">The automation.</param>
    /// <param name="runId">The identifier of the run.</param>
    /// <param name="prompt">The text to send.</param>
    /// <param name="detail">What the trigger was about.</param>
    /// <param name="dryRun">Whether the run is a try: the tools its autonomy allows are fakes.</param>
    /// <param name="cancellationToken">Cancels the preparation of the run.</param>
    /// <returns>The session and its completion, or why the run did not start.</returns>
    Task<AutomationStart> StartAsync(AutomationEntry entry, string runId, string prompt, string? detail, bool dryRun, CancellationToken cancellationToken)
        => StartAsync(entry, runId, prompt, detail, cancellationToken);
}

/// <summary>
/// Runs an automation in the host of the application: a new session of the project, or a chat, named after the
/// automation (and after what started it, for an event) and recorded as created by it, then one send of the prompt
/// with the model the automation asks for.
/// Everything the automation names is checked before the session is created, so that a wrong name leaves no
/// empty session behind at every run.
/// </summary>
internal sealed class AutomationRunner : IAutomationRunner
{
    private const int MaximumDetailInTitle = 80;
    private const string NoCapacity = "Too many commands are pending in CodeAlta. The automation runs again at its next trigger.";

    private readonly CodeAltaHost _host;
    private readonly IAutomationRunGate? _gate;
    /// <summary>Gets or sets the circuit breaker of the allowed tools of the runs; the service gives one that keeps its calls with its state.</summary>
    internal AutomationCallBreaker Breaker { get; set; } = new();

    /// <summary>How often the approvals of the commands and edits of a run with autonomy are looked at for their timeout.</summary>
    internal static readonly TimeSpan ApprovalWatch = TimeSpan.FromSeconds(15);

    /// <summary>Creates the runner of a host.</summary>
    /// <param name="host">The host the sessions of the runs start in.</param>
    /// <param name="gate">What the host checks before a run starts; null for nothing more than what the automation names.</param>
    internal AutomationRunner(CodeAltaHost host, IAutomationRunGate? gate = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        (_host, _gate) = (host, gate);
        host.ToolGuards.AddOrigin(GuardOf);
    }

    /// <summary>
    /// Gets or sets what tells, for the session of a run once its first turn ended (or after a restart), the automation as it
    /// is now and whether the run was a try (null when the run is no longer known); null when nothing tells: such a session is
    /// then guarded as one of an automation that allows nothing by itself.
    /// </summary>
    internal Func<string, string, (AutomationEntry? Entry, bool? DryRun)>? Origin { get; set; }

    // The guards of the runs that work now, by run: their sessions and the sessions these start are checked by the same one.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AutomationRunGuard> _live = new(StringComparer.Ordinal);

    // The sessions whose approvals a guard taken from their automation watches now (GuardOf), while they work.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _watched = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the clock the approvals of the runs time out by.</summary>
    internal TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>Gets or sets how often the approvals of the commands and edits of a guarded session are looked at.</summary>
    internal TimeSpan WatchInterval { get; set; } = ApprovalWatch;

    /// <summary>
    /// Gets or sets what stops the turn of a session whose approval waited too long, once its run's first turn ended (or
    /// after a restart); null aborts the active work of the session.
    /// </summary>
    internal Func<string, Task>? AbortSession { get; set; }

    // The guard a session takes from the automation that created it (its lineage gives it to the sessions it starts): the one
    // of its run while the run works, else one of the automation as it is now; a try stays a try. An automation no longer
    // known, or a run no longer known, gives a guard that allows nothing by itself: what its sessions call waits for a person.
    private AgentToolInterceptor? GuardOf(string sessionId, AltaActorProvenance createdBy)
    {
        if (createdBy is not { Kind: AltaActorProvenance.AutomationKind, AutomationId: { Length: > 0 } automationId }) return null;
        if (createdBy.CorrelationId is { Length: > 0 } runId && _live.TryGetValue(runId, out var live)) return live;
        var (entry, dryRun) = Origin?.Invoke(automationId, createdBy.CorrelationId ?? string.Empty) ?? (null, null);
        if (entry is not null && entry.Definition.Autonomy is null) return null;
        entry ??= new AutomationEntry(new AutomationDefinition(automationId, "an automation that is no longer here"), new AutomationSource(string.Empty, null, null), null, null, null);
        var autonomy = dryRun is null || entry.Definition.Autonomy is null ? new AutomationAutonomy() : entry.Definition.Autonomy;
        var guard = new AutomationRunGuard(entry with { Definition = entry.Definition with { Autonomy = autonomy } }, Breaker,
            (session, request, token) => _host.RuntimeService.Permissions.RequestForSessionAsync(session, request, token), Time, dryRun == true);
        // Its later turns are supervised as its first one was: an approval that waits too long stops the turn that waits, and
        // the approvals of its commands and edits are watched while the session that calls works.
        if (autonomy.ApprovalTimeout is not null && autonomy.OnTimeout == AutomationAutonomy.StopOnTimeout)
        {
            guard.StoppedIn += (session, why) => _ = AbortQuietlyAsync(session);
            guard.Calling = session => Supervise(session, guard, autonomy);
        }

        return guard;
    }

    // Watches the approvals of a session a guard taken from its automation checks, once at a time, until it no longer works.
    private void Supervise(string sessionId, AutomationRunGuard guard, AutomationAutonomy autonomy)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !_watched.TryAdd(sessionId, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await WatchApprovalsAsync(sessionId, guard, autonomy, () => IdleAsync(sessionId), why => guard.Stop(sessionId, why), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A watch that fails ends; the next call of the session starts another.
            }
            finally
            {
                // What it saw waiting is no longer watched: a stopped turn waits for nothing.
                guard.NoteWaiting(false);
                _watched.TryRemove(sessionId, out _);
            }
        });
    }

    private async Task<bool> IdleAsync(string sessionId)
    {
        var session = await _host.RuntimeService.TryGetRecoverableSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        return session is null || !await _host.RuntimeService.HasActiveRunAsync(session, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task AbortQuietlyAsync(string sessionId)
    {
        try
        {
            await (AbortSession ?? (id => _host.RuntimeService.AbortAsync(id, CancellationToken.None)))(sessionId).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            // Nothing works in it any more.
        }
    }

    /// <inheritdoc />
    public Task<AutomationStart> StartAsync(AutomationEntry entry, string runId, string prompt, string? detail, CancellationToken cancellationToken)
        => StartAsync(entry, runId, prompt, detail, false, cancellationToken);

    /// <inheritdoc />
    public async Task<AutomationStart> StartAsync(AutomationEntry entry, string runId, string prompt, string? detail, bool dryRun, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(prompt);
        // An automation that runs a script has no prompt of its own: what started it may still say something.
        if (entry.Definition.Script is null) ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        string? sessionId = null;
        try
        {
            ProjectDescriptor? project = null;
            if (entry.ProjectId is { } projectId)
            {
                project = await _host.ProjectCatalog.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false);
                if (project is null || project.Archived) return Refuse("Its project is no longer one of the projects of CodeAlta.");
                if (!Directory.Exists(project.ProjectPath)) return Refuse($"The folder of its project is missing: {project.ProjectPath}");
            }

            var definition = entry.Definition;
            // Nothing of the window decides where a run goes: the space of its project when the project is in
            // exactly one, the default space otherwise.
            var spaceId = SpaceCatalog.ResolveSessionSpace(null, project, await _host.SpaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false));
            // A run starts with a provider of its space: the one the automation names, which must be there, else the
            // default provider of the configuration when it is, else the first enabled provider of the space.
            var inSpace = new SpaceFilter(new CachedSpaceAssignments(new CodeAltaConfigStore(_host.CatalogOptions)).Current(), spaceId);
            if (definition.Script is { } scriptName)
            {
                // What started the automation (an issue, the output of a command) is the `event` argument of a script that
                // declares it. A script of yours or a sample runs only in a space that has it.
                if (CodeModeScriptRunPrompt.Build(_host.CodeModeScripts, scriptName, definition.ScriptArguments, prompt, project?.ProjectPath, inSpace, out var problem) is not { } scriptPrompt)
                    return Refuse(problem!);
                prompt = scriptPrompt;
            }

            if (definition.Model.Provider is { Length: > 0 } named && !inSpace.Allows(SpaceResourceKind.Provider, named))
                return Refuse($"The provider '{named}' is not available in the space '{spaceId}'.");
            var providers = _host.ModelProviderRegistry.ListProviders().Where(candidate => inSpace.Allows(SpaceResourceKind.Provider, candidate.ProviderId.Value)).ToArray();
            var providerKey = definition.Model.Provider
                ?? new CodeAltaConfigStore(_host.CatalogOptions).GetEffectiveDefaultProvider(project?.ProjectPath)
                ?? providers.FirstOrDefault()?.ProviderId.Value;
            if (providerKey is null) return Refuse("No model provider is enabled.");
            if (!_host.ModelProviderRegistry.TryGetProvider(new ModelProviderId(providerKey), out var provider) || !provider.IsEnabled
                || !inSpace.Allows(SpaceResourceKind.Provider, provider.ProviderId.Value))
            {
                // The default provider of the configuration may be one that is not enabled, or not in the space: the
                // first enabled provider of the space then.
                provider = definition.Model.Provider is null ? providers.FirstOrDefault(static candidate => candidate.IsEnabled) : null;
                if (provider is null) return Refuse(definition.Model.Provider is null ? $"No model provider is enabled in the space '{spaceId}'." : $"The provider '{providerKey}' is not enabled.");
            }

            // What the host itself requires of a run (a node: its policy, the credential kind), before anything is created.
            if (_gate?.Check(entry, provider) is { } refused) return Refuse(refused);
            // An environment admits the run before anything is created: its rules, then its approval, which may wait.
            EnvironmentAdmission? admitted = null;
            if (definition.Environment is { } environment)
            {
                if (_gate is null) return Refuse($"This machine runs no automation in the environment '{environment}': environments are given by a server.");
                var gated = await _gate.AdmitEnvironmentAsync(entry, runId, environment, cancellationToken).ConfigureAwait(false);
                if (gated.Problem is not null || gated.Admission is null) return Refuse(gated.Problem ?? "The environment did not admit the run.");
                admitted = gated.Admission;
            }

            // The budgets of the machine admit the run before anything is created; a reached one may hold it for its owner.
            if (_gate is not null && await _gate.AdmitBudgetAsync(entry, runId, project, spaceId, cancellationToken).ConfigureAwait(false) is { } overBudget)
                return Refuse(overBudget);

            var models = await _host.ModelProviderInitializationService.GetModelsAsync(provider.ProviderId, cancellationToken).ConfigureAwait(false);
            var modelId = definition.Model.Model ?? AgentModelDefaults.ResolveModelId(models, provider.DefaultModelId);
            var model = models.FirstOrDefault(candidate => string.Equals(candidate.Id, modelId, StringComparison.Ordinal));
            if (model is null)
            {
                return Refuse(definition.Model.Model is { } asked
                    ? $"The provider '{provider.ProviderId.Value}' does not offer the model '{asked}'."
                    : $"The provider '{provider.ProviderId.Value}' offers no model at the moment.");
            }

            if (_gate?.CheckModel(entry, provider, model.Id, spaceId) is { } ruled) return Refuse(ruled);
            // The permission mode, the sandbox and the tools the policy of the space allows, whatever the definition asks.
            var limits = _gate?.LimitsFor(entry, provider, spaceId) ?? AutomationRunLimits.None;
            if (limits.Problem is { } limited) return Refuse(limited);
            var permissionMode = limits.PermissionMode ?? definition.PermissionMode;
            var sandboxName = limits.Sandbox ?? definition.Sandbox;
            if (definition.Model.Effort is { } effort && model.SupportedReasoningEfforts is { } supported && !supported.Contains(effort))
                return Refuse($"The model '{model.Id}' has no reasoning effort '{effort.ToString().ToLowerInvariant()}'.");

            // The agent the automation names, else the one its space starts its new sessions with; one the space does
            // not have is refused, as for any session of that space.
            var drafts = await _host.Commands.GetDraftPromptsAsync(project is null ? null : new(project.Id, project.ProjectPath), spaceId, cancellationToken).ConfigureAwait(false);
            if (drafts is null) return Refuse("The application is closing.");
            var agent = definition.Agent ?? drafts.DefaultPromptId;
            if (!drafts.Prompts.Any(candidate => candidate.Id == agent)) return Refuse($"There is no agent prompt '{agent}' in the space '{spaceId}'.");
            // The permission mode is one the provider offers a new session, checked before the session exists.
            if (permissionMode is { } mode && !_host.Commands.GetNewSessionPermissionModes(provider.ProviderId.Value).Modes.Contains(mode, StringComparer.Ordinal))
                return Refuse($"The provider '{provider.ProviderId.Value}' does not offer the permission mode '{mode}'.");

            cancellationToken.ThrowIfCancellationRequested();
            // What the host requires is checked again after the admissions that may wait (an approval, a budget): a stop of the
            // kill switch that came meanwhile reaches the run before anything is created.
            if (_gate?.Check(entry, provider) is { } stopped) return Refuse(stopped);
            // The host keeps a bounded number of pending commands: a prompt that would be refused starts no session.
            if (!_host.Commands.HasCapacity) return Refuse(NoCapacity);
            // The tools the policy turns off are off from the creation of the session, as for a session started from another device.
            string? limitedId = null;
            if (limits.ToolsOff is { Count: > 0 } off)
            {
                limitedId = Guid.NewGuid().ToString();
                _host.RuntimeService.SetDisabledToolPatterns(limitedId, off);
            }

            // The folder of the run: on a machine of a fleet, a worktree of its own; none could be made, the run does not start.
            var folder = _gate is null ? AutomationRunFolder.Project : await _gate.PrepareFolderAsync(project, cancellationToken).ConfigureAwait(false);
            if (folder.Problem is { } noFolder)
            {
                if (limitedId is not null) _host.RuntimeService.SetDisabledToolPatterns(limitedId, null);
                return Refuse(noFolder);
            }

            SessionViewDescriptor session;
            try
            {
                session = await _host.Commands.CreateDraftSessionAsync(project, provider, SessionTitle(definition.Name, detail), new AltaActorProvenance
                {
                    Kind = AltaActorProvenance.AutomationKind,
                    AutomationId = definition.Id,
                    SourceProjectId = project?.Id,
                    CorrelationId = runId,
                    CreatedAt = DateTimeOffset.UtcNow,
                }, folder.Worktree, spaceId, permissionMode, limitedId, null, definition.SessionOptions).ConfigureAwait(false);
            }
            catch
            {
                if (limitedId is not null) _host.RuntimeService.SetDisabledToolPatterns(limitedId, null);
                if (_gate is not null) await _gate.ReleaseFolderAsync(project, folder).ConfigureAwait(false);
                throw;
            }

            sessionId = session.SessionId;
            // Whose work it is is known before the session attaches: a run that is not its owner's resolves none of the owner's values.
            try
            {
                _gate?.Attribute(entry, sessionId);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new(sessionId, $"Whose work the run is could not be recorded: {exception.Message}", null);
            }

            // The processes of the session get the values of the environment it was admitted to, from its first attachment.
            if (admitted is not null) _host.Identities.Environments.Admit(sessionId, admitted);
            // The review and the sandbox the automation chose, before anything is sent to the session.
            if (definition.ReviewBeforeCommit is { } review
                && (await _host.Commands.SetReviewBeforeCommitAsync(sessionId, review, cancellationToken).ConfigureAwait(false)).Status is var reviewed and not "ok")
                return new(sessionId, $"The review before commit could not be chosen ({reviewed}).", null);
            if (sandboxName is { } sandbox
                && (await _host.Commands.SetSandboxAsync(sessionId, sandbox, cancellationToken).ConfigureAwait(false)).Status is var sandboxed and not "ok")
                return new(sessionId, $"The sandbox '{sandbox}' could not be chosen ({sandboxed}).", null);

            // A script runs through exec, which its session has whatever the setting of Code Mode says.
            if (definition.Script is not null) _host.CodeModeRuns.RequireCodeMode(sessionId);
            // What a run with autonomy does without a person is enforced on this machine, at every tool call, from its first one.
            AutomationRunGuard? guard = null;
            if (definition.Autonomy is not null)
            {
                if (_host.RuntimeService.GetPermissionPolicy(sessionId) == SessionPermissionPolicy.Approve)
                    return new(sessionId, "This automation decides what its runs do without a person: it runs in a permission mode that asks for commands and edits, not one that allows everything.", null);
                // A call waits for a person on the card of the session that makes it: the run's own, or a session it started.
                guard = new AutomationRunGuard(entry, Breaker, (session, request, token) => _host.RuntimeService.Permissions.RequestForSessionAsync(session, request, token),
                    Time, dryRun);
                _live[runId] = guard;
                _host.ToolGuards.Set(sessionId, guard);
            }

            // The effort of a model that reports its efforts is completed by the host when the automation names none.
            var selection = new OwnedSessionSelection(provider.ProviderId.Value, agent, model.Id, definition.Model.Effort);
            var admission = _host.Commands.AdmitSend(new OwnedTextSendRequest("automation:" + runId, sessionId, prompt)
                { Selection = selection, Provenance = AgentPromptProvenance.Automation }, CancellationToken.None);
            if (admission.Kind is not (OwnedSessionCommandAdmissionKind.Accepted or OwnedSessionCommandAdmissionKind.Replay) || admission.Receipt is null)
            {
                if (guard is not null)
                {
                    _host.ToolGuards.Remove(sessionId, guard);
                    _live.TryRemove(new KeyValuePair<string, AutomationRunGuard>(runId, guard));
                }

                return new(sessionId, admission.Kind == OwnedSessionCommandAdmissionKind.Capacity ? NoCapacity
                    : $"The prompt was not accepted ({admission.Kind.ToString().ToLowerInvariant()}).", null);
            }

            // Its answer is read only when a trigger of it writes the answer back to the source of its event.
            var observed = ObserveAsync(admission.Receipt, sessionId, definition.Triggers.Any(static trigger => trigger.Reply));
            if (guard is not null) observed = GuardAsync(observed, admission.Receipt, sessionId, runId, guard, definition.Autonomy!);
            return new(sessionId, null, observed) { Guard = guard };
        }
        catch (OperationCanceledException)
        {
            return new(sessionId, "The run was cancelled before it started.", null);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException
            or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException)
        {
            return new(sessionId, FirstLine(exception.Message), null);
        }

        static AutomationStart Refuse(string problem) => new(null, problem, null);
    }

    /// <summary>
    /// The name of the session of a run: the name of the automation, followed by what started it when the trigger
    /// was about something, so that the sessions of an event trigger are told apart in a list.
    /// </summary>
    internal static string SessionTitle(string name, string? detail)
        => AutomationText.Line(detail, MaximumDetailInTitle) is { Length: > 0 } about ? name + " · " + about : name;

    // While a run with autonomy works: the approvals of its commands and edits count as waiting too, an approval that waits
    // longer than the automation says stops the run (unless a person took it over), and the guard leaves with the run.
    private async Task<AutomationOutcome> GuardAsync(Task<AutomationOutcome> observed, OwnedSessionCommandReceipt receipt, string sessionId, string runId,
        AutomationRunGuard guard, AutomationAutonomy autonomy)
    {
        guard.Stopped += _ => _host.Commands.AdmitAbort(new OwnedAbortRequest("automation-stop:" + runId, receipt.OperationId), CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var watch = Task.Run(() => WatchApprovalsAsync(sessionId, guard, autonomy, null, guard.Stop, stop.Token));
        try
        {
            return await observed.ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try { await watch.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            guard.NoteWaiting(false);
            // The session keeps a guard of its automation from its origin (GuardOf): its later turns are checked too.
            _host.ToolGuards.Remove(sessionId, guard);
            _live.TryRemove(new KeyValuePair<string, AutomationRunGuard>(runId, guard));
        }
    }

    // Looks at the approvals of the commands and edits of a guarded session: they count as waiting too, and one that waits longer
    // than the automation says is denied and stops the run (unless a person took it over). Ends once it stopped the run, when
    // cancelled, or when the session waits for nothing and `idle` says it no longer works.
    private async Task WatchApprovalsAsync(string sessionId, AutomationRunGuard guard, AutomationAutonomy autonomy, Func<Task<bool>>? idle, Action<string> stopRun,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? since = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(WatchInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            SessionOwnedPermissionPage page;
            try { page = await _host.RuntimeService.Permissions.ListOwnedCommandsAsync(sessionId, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or ArgumentException) { return; }
            var waiting = page.Entries.Count > 0;
            guard.NoteWaiting(waiting);
            if (!waiting && idle is not null && await idle().ConfigureAwait(false)) return;
            since = waiting ? since ?? Time.GetUtcNow() : null;
            if (since is not { } start || guard.TakenOver || autonomy.ApprovalTimeout is not { } minutes || autonomy.OnTimeout != AutomationAutonomy.StopOnTimeout
                || Time.GetUtcNow() - start < TimeSpan.FromMinutes(minutes)) continue;
            foreach (var entry in page.Entries)
            {
                try { await _host.RuntimeService.Permissions.ResolveOwnedCommandAsync(entry.Handle, AgentPermissionDecisionKind.Deny, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { }
            }

            stopRun($"No one answered an approval of the run within {minutes} minutes: the run stopped.");
            return;
        }
    }

    private async Task<AutomationOutcome> ObserveAsync(OwnedSessionCommandReceipt receipt, string sessionId, bool answer)
    {
        var result = await receipt.Completion.ConfigureAwait(false);
        AutomationOutcome outcome = result.Outcome switch
        {
            OwnedSessionCommandOutcome.Completed => new(AutomationRun.Completed),
            OwnedSessionCommandOutcome.Cancelled => new(AutomationRun.Cancelled),
            _ => new(AutomationRun.Failed, result.Code is { } code ? $"The session did not complete ({code})." : "The session did not complete."),
        };
        return outcome with { Usage = await ReadUsageAsync(sessionId).ConfigureAwait(false), Answer = answer ? await ReadAnswerAsync(sessionId).ConfigureAwait(false) : null };
    }

    // The last answer of the session, from the newest page of its timeline: what a trigger of another tool writes back.
    private async Task<string?> ReadAnswerAsync(string sessionId)
    {
        try
        {
            var tail = await _host.WorkspaceReads.ReadTimelineTailAsync(sessionId, null, CancellationToken.None).ConfigureAwait(false);
            return tail.Page?.Entries.Select(static entry => entry.Event).OfType<CodeAlta.Agent.AgentContentCompletedEvent>()
                .LastOrDefault(static content => content.Kind == CodeAlta.Agent.AgentContentKind.Assistant && !string.IsNullOrWhiteSpace(content.Content))?.Content;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException
            or InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    // What the session used, from its usage records; a journal that cannot be read now leaves the run without it.
    private async Task<AutomationUsage?> ReadUsageAsync(string sessionId)
    {
        try
        {
            return AutomationUsage.Sum(await _host.WorkspaceReads.ReadUsageUpdatesAsync(sessionId, CancellationToken.None).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException
            or InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static string FirstLine(string message)
    {
        var line = message.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return line.Length <= 300 ? line : line[..300];
    }
}
