using CodeAlta.Agent;
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
internal sealed record AutomationStart(string? SessionId, string? Problem, Task<AutomationOutcome>? Completion);

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

    /// <summary>Creates the runner of a host.</summary>
    /// <param name="host">The host the sessions of the runs start in.</param>
    /// <param name="gate">What the host checks before a run starts; null for nothing more than what the automation names.</param>
    internal AutomationRunner(CodeAltaHost host, IAutomationRunGate? gate = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        (_host, _gate) = (host, gate);
    }

    /// <inheritdoc />
    public async Task<AutomationStart> StartAsync(AutomationEntry entry, string runId, string prompt, string? detail, CancellationToken cancellationToken)
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

            if (definition.Model.Effort is { } effort && model.SupportedReasoningEfforts is { } supported && !supported.Contains(effort))
                return Refuse($"The model '{model.Id}' has no reasoning effort '{effort.ToString().ToLowerInvariant()}'.");

            // The agent the automation names, else the one its space starts its new sessions with; one the space does
            // not have is refused, as for any session of that space.
            var drafts = await _host.Commands.GetDraftPromptsAsync(project is null ? null : new(project.Id, project.ProjectPath), spaceId, cancellationToken).ConfigureAwait(false);
            if (drafts is null) return Refuse("The application is closing.");
            var agent = definition.Agent ?? drafts.DefaultPromptId;
            if (!drafts.Prompts.Any(candidate => candidate.Id == agent)) return Refuse($"There is no agent prompt '{agent}' in the space '{spaceId}'.");
            // The permission mode is one the provider offers a new session, checked before the session exists.
            if (definition.PermissionMode is { } mode && !_host.Commands.GetNewSessionPermissionModes(provider.ProviderId.Value).Modes.Contains(mode, StringComparer.Ordinal))
                return Refuse($"The provider '{provider.ProviderId.Value}' does not offer the permission mode '{mode}'.");

            cancellationToken.ThrowIfCancellationRequested();
            // The host keeps a bounded number of pending commands: a prompt that would be refused starts no session.
            if (!_host.Commands.HasCapacity) return Refuse(NoCapacity);
            var session = await _host.Commands.CreateDraftSessionAsync(project, provider, SessionTitle(definition.Name, detail), new AltaActorProvenance
            {
                Kind = AltaActorProvenance.AutomationKind,
                AutomationId = definition.Id,
                SourceProjectId = project?.Id,
                CorrelationId = runId,
                CreatedAt = DateTimeOffset.UtcNow,
            }, null, spaceId, definition.PermissionMode, null, null, definition.SessionOptions).ConfigureAwait(false);
            sessionId = session.SessionId;
            // The processes of the session get the values of the environment it was admitted to, from its first attachment.
            if (admitted is not null) _host.Identities.Environments.Admit(sessionId, admitted);
            // The review and the sandbox the automation chose, before anything is sent to the session.
            if (definition.ReviewBeforeCommit is { } review
                && (await _host.Commands.SetReviewBeforeCommitAsync(sessionId, review, cancellationToken).ConfigureAwait(false)).Status is var reviewed and not "ok")
                return new(sessionId, $"The review before commit could not be chosen ({reviewed}).", null);
            if (definition.Sandbox is { } sandbox
                && (await _host.Commands.SetSandboxAsync(sessionId, sandbox, cancellationToken).ConfigureAwait(false)).Status is var sandboxed and not "ok")
                return new(sessionId, $"The sandbox '{sandbox}' could not be chosen ({sandboxed}).", null);

            // A script runs through exec, which its session has whatever the setting of Code Mode says.
            if (definition.Script is not null) _host.CodeModeRuns.RequireCodeMode(sessionId);
            // The effort of a model that reports its efforts is completed by the host when the automation names none.
            var selection = new OwnedSessionSelection(provider.ProviderId.Value, agent, model.Id, definition.Model.Effort);
            var admission = _host.Commands.AdmitSend(new OwnedTextSendRequest("automation:" + runId, sessionId, prompt)
                { Selection = selection, Provenance = AgentPromptProvenance.Automation }, CancellationToken.None);
            if (admission.Kind is not (OwnedSessionCommandAdmissionKind.Accepted or OwnedSessionCommandAdmissionKind.Replay) || admission.Receipt is null)
            {
                return new(sessionId, admission.Kind == OwnedSessionCommandAdmissionKind.Capacity ? NoCapacity
                    : $"The prompt was not accepted ({admission.Kind.ToString().ToLowerInvariant()}).", null);
            }

            // Its answer is read only when a trigger of it writes the answer back to the source of its event.
            return new(sessionId, null, ObserveAsync(admission.Receipt, sessionId, definition.Triggers.Any(static trigger => trigger.Reply)));
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
