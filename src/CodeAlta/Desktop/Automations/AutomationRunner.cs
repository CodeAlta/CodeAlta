using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Desktop.Automations;

/// <summary>How a run of an automation ended.</summary>
/// <param name="Status">One of the status constants of <see cref="AutomationRun"/>.</param>
/// <param name="Message">Why it did not complete.</param>
internal sealed record AutomationOutcome(string Status, string? Message = null);

/// <summary>What starting a run gave.</summary>
/// <param name="SessionId">The session that was created; null when none was.</param>
/// <param name="Problem">Why the run did not start; null when it did.</param>
/// <param name="Completion">Completes when the session has answered, failed or been stopped; null when the run did not start.</param>
internal sealed record AutomationStart(string? SessionId, string? Problem, Task<AutomationOutcome>? Completion);

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

    internal AutomationRunner(CodeAltaHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <inheritdoc />
    public async Task<AutomationStart> StartAsync(AutomationEntry entry, string runId, string prompt, string? detail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
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
            }, null, spaceId, definition.PermissionMode).ConfigureAwait(false);
            sessionId = session.SessionId;
            // The review and the sandbox the automation chose, before anything is sent to the session.
            if (definition.ReviewBeforeCommit is { } review
                && (await _host.Commands.SetReviewBeforeCommitAsync(sessionId, review, cancellationToken).ConfigureAwait(false)).Status is var reviewed and not "ok")
                return new(sessionId, $"The review before commit could not be chosen ({reviewed}).", null);
            if (definition.Sandbox is { } sandbox
                && (await _host.Commands.SetSandboxAsync(sessionId, sandbox, cancellationToken).ConfigureAwait(false)).Status is var sandboxed and not "ok")
                return new(sessionId, $"The sandbox '{sandbox}' could not be chosen ({sandboxed}).", null);

            // The effort of a model that reports its efforts is completed by the host when the automation names none.
            var selection = new OwnedSessionSelection(provider.ProviderId.Value, agent, model.Id, definition.Model.Effort);
            var admission = _host.Commands.AdmitSend(new OwnedTextSendRequest("automation:" + runId, sessionId, prompt)
                { Selection = selection, Provenance = AgentPromptProvenance.Automation }, CancellationToken.None);
            if (admission.Kind is not (OwnedSessionCommandAdmissionKind.Accepted or OwnedSessionCommandAdmissionKind.Replay) || admission.Receipt is null)
            {
                return new(sessionId, admission.Kind == OwnedSessionCommandAdmissionKind.Capacity ? NoCapacity
                    : $"The prompt was not accepted ({admission.Kind.ToString().ToLowerInvariant()}).", null);
            }

            return new(sessionId, null, ObserveAsync(admission.Receipt));
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

    private static async Task<AutomationOutcome> ObserveAsync(OwnedSessionCommandReceipt receipt)
    {
        var result = await receipt.Completion.ConfigureAwait(false);
        return result.Outcome switch
        {
            OwnedSessionCommandOutcome.Completed => new(AutomationRun.Completed),
            OwnedSessionCommandOutcome.Cancelled => new(AutomationRun.Cancelled),
            _ => new(AutomationRun.Failed, result.Code is { } code ? $"The session did not complete ({code})." : "The session did not complete."),
        };
    }

    private static string FirstLine(string message)
    {
        var line = message.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return line.Length <= 300 ? line : line[..300];
    }
}
