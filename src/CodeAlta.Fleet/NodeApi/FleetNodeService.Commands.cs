using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Fleet.NodeApi;

public sealed partial class FleetNodeService
{
    private const int MaxPromptLength = 32 * 1024;
    private const int MaxTitleLength = 256;

    // A creation is not deduplicated by the runtime: the key of the request makes a retry return the first session.
    private Task<FleetCreateSessionResult> CreateSessionAsync(FleetNodeCall call, FleetCreateSessionParams parameters, CancellationToken cancellationToken)
    {
        RequireWork();
        RequireNotHalted();
        if (parameters.Prompt is { } prompt && (string.IsNullOrWhiteSpace(prompt) || prompt.Length > MaxPromptLength))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, $"A prompt has 1 to {MaxPromptLength} characters.");
        if (parameters.Prompt is not null && (call.Device!.Permissions & Identity.DevicePermissions.Drive) == 0)
            throw new FleetException(FleetErrorCodes.Forbidden, null, "This device may start sessions but not send them prompts.");
        if (parameters.Title is { } title && (string.IsNullOrWhiteSpace(title) || title.Length > MaxTitleLength || title.Any(char.IsControl)))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, $"A title has 1 to {MaxTitleLength} characters.");
        if (parameters.Agent is not null) return CreateAgentSessionAsync(call, parameters);
        var retryKey = RetryKey(call);
        return CreateRecordedAsync(call, Once(call, FleetNodeJsonContext.Default.FleetCreateSessionResult, () => CreateSessionCoreAsync(call.Device, retryKey, parameters)));
    }

    // A session started through a team membership is its person's: recorded before its result returns, so that their next call finds it.
    private async Task<FleetCreateSessionResult> CreateRecordedAsync(FleetNodeCall call, Task<FleetCreateSessionResult> created)
    {
        var result = await created.ConfigureAwait(false);
        RecordPrincipal(call.Device, result.SessionId);
        return result;
    }

    private async Task<FleetCreateSessionResult> CreateSessionCoreAsync(Identity.TrustedDevice? device, string retryKey, FleetCreateSessionParams parameters)
    {
        ProjectDescriptor? project = null;
        if (parameters.ProjectId is { } projectId)
        {
            project = await _host.ProjectCatalog.GetByIdAsync(projectId).ConfigureAwait(false);
            if (project is null || project.Archived || !Directory.Exists(project.ProjectPath))
                throw new FleetException(FleetNodeErrorCodes.Unavailable, null, $"The node has no project '{projectId}'.");
        }

        if (parameters.SpaceId is { } spaceId && (!SpaceDescriptor.IsValidId(spaceId) || await _host.SpaceCatalog.GetAsync(spaceId).ConfigureAwait(false) is null))
            throw new FleetException(FleetNodeErrorCodes.Unavailable, null, $"The node has no space '{spaceId}'.");
        if (parameters.Environment is { } named && (_options.AdmitEnvironment is null || named.Length is 0 or > 64 || named.Any(char.IsControl)))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, _options.AdmitEnvironment is null ? "This node runs no session in an environment." : "The name of an environment has 1 to 64 characters.");

        var provider = ResolveProvider(parameters.Selection?.ProviderKey, project?.ProjectPath);
        // The policy of the organization of the space, before the session exists: who starts, the provider, the model, the mode.
        var rules = RequirePolicyStart(parameters.SpaceId, provider, parameters.Selection?.ModelId, agent: false);
        var permissionMode = PolicyPermissionMode(provider.ProviderId.Value, rules?.PermissionMode);
        var sessionId = Guid.NewGuid().ToString();
        if (rules?.ToolsOff is { Count: > 0 } off)
        {
            if (off.Select(AgentToolPatterns.Validate).FirstOrDefault(static error => error is not null) is { } invalid)
                throw Refused($"The tools the policy of the organization turns off do not read: {invalid}");
            _host.RuntimeService.SetDisabledToolPatterns(sessionId, off);
        }

        // Whose work it is is known before the session exists: its identity, read when it attaches, resolves only what that person may use.
        RecordPrincipal(device, sessionId);
        SessionViewDescriptor session;
        try
        {
            session = await _host.Commands.CreateDraftSessionAsync(project, provider, parameters.Title, null, null, parameters.SpaceId, permissionMode, sessionId, null, null)
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            _host.RuntimeService.SetDisabledToolPatterns(sessionId, null);
            throw new FleetException(FleetNodeErrorCodes.NodeStopping, FleetErrorHints.Retry, "The node is stopping.");
        }

        // Work of the fleet: a stop of the kill switch and the longest time of a policy reach it.
        Work.Record(session.SessionId, parameters.SpaceId, "remote", _options.TimeProvider.GetUtcNow());
        if (rules is not null) await ApplyPolicySandboxAsync(session.SessionId, rules).ConfigureAwait(false);

        // The session is admitted to its environment before its first prompt: an approval may wait, the creation does not.
        if (parameters.Environment is { } environment)
        {
            _ = AdmitThenSendAsync(environment, session, provider, parameters, retryKey);
            return new FleetCreateSessionResult(session.SessionId, provider.ProviderId.Value, null);
        }

        FleetCommandResult? send = null;
        if (parameters.Prompt is { } prompt)
        {
            var selection = Selection(parameters.Selection, provider.ProviderId.Value, session.AgentPromptId);
            // The budgets of the machine admit the first run: it may wait for its owner, the creation does not.
            send = await WithinBudgetAsync(session.SessionId, session, parameters.Title, HeldSendOf(retryKey, session.SessionId, prompt, parameters.Title, selection))
                .ConfigureAwait(false);
        }

        return new FleetCreateSessionResult(session.SessionId, provider.ProviderId.Value, send);
    }

    private async Task AdmitThenSendAsync(string environment, SessionViewDescriptor session, ModelProviderDescriptor provider, FleetCreateSessionParams parameters, string retryKey)
    {
        try
        {
            var result = await _options.AdmitEnvironment!(environment, session.SessionId, parameters.Title ?? "A session", CancellationToken.None).ConfigureAwait(false);
            if (result.Admission is not { } admission)
            {
                LogInfo($"The session {session.SessionId} was not admitted to the environment '{environment}': {result.Problem}");
                return;
            }

            // A stop that came while the approval was awaited reaches the session: nothing is sent.
            if (Policy?.Halt is { } halt)
            {
                LogInfo($"The session {session.SessionId} was not sent its prompt once admitted to the environment '{environment}': {halt.Describe(_options.Name)}");
                return;
            }

            _host.Identities.Environments.Admit(session.SessionId, admission);
            if (parameters.Prompt is { } prompt)
            {
                var selection = Selection(parameters.Selection, provider.ProviderId.Value, session.AgentPromptId);
                // Admitted to its environment, the first run is admitted to the budgets as any other: one may stop or hold it.
                var sent = await WithinBudgetAsync(session.SessionId, session, parameters.Title, HeldSendOf(retryKey, session.SessionId, prompt, parameters.Title, selection))
                    .ConfigureAwait(false);
                if (sent.Code == FleetBudgetCommandStatuses.Reached) LogInfo($"The first prompt of the session {session.SessionId} was stopped by a budget.");
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException or UnauthorizedAccessException)
        {
            LogInfo($"The session {session.SessionId} was not admitted to the environment '{environment}': {exception.Message}");
        }
    }

    // The provider a request names, or the default of the node for the project, as the desktop chooses it: the one the
    // configuration names, else the registry's default, else the first enabled one.
    private ModelProviderDescriptor ResolveProvider(string? providerKey, string? projectPath)
    {
        var providers = _host.ModelProviderRegistry.ListProviders();
        if (providerKey is not null)
        {
            return providers.FirstOrDefault(value => value.IsEnabled && string.Equals(value.ProviderId.Value, providerKey, StringComparison.Ordinal))
                ?? throw new FleetException(FleetNodeErrorCodes.Unavailable, null, $"The node has no enabled provider '{providerKey}'.");
        }

        string? configured = null;
        try
        {
            configured = new CodeAltaConfigStore(_host.CatalogOptions).GetEffectiveDefaultProvider(projectPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // An unreadable configuration names no default: the registry's is taken.
        }

        return providers.FirstOrDefault(value => value.IsEnabled && configured is not null && string.Equals(value.ProviderId.Value, configured, StringComparison.Ordinal))
            ?? providers.FirstOrDefault(static value => value.IsEnabled && value.IsDefault)
            ?? providers.FirstOrDefault(static value => value.IsEnabled)
            ?? throw new FleetException(FleetNodeErrorCodes.Unavailable, null, "The node has no enabled provider.");
    }

    private static OwnedSessionSelection? Selection(FleetModelSelection? selection, string sessionProvider, string? sessionPrompt)
    {
        if (selection is null || selection.ModelId is null && selection.ReasoningEffort is null && selection.AgentPromptId is null) return null;
        AgentReasoningEffort? effort = null;
        if (selection.ReasoningEffort is { } text)
        {
            if (!Enum.TryParse<AgentReasoningEffort>(text, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                throw new FleetException(FleetErrorCodes.InvalidParams, null, $"'{text}' is not a reasoning effort.");
            effort = parsed;
        }

        return new OwnedSessionSelection(selection.ProviderKey ?? sessionProvider, selection.AgentPromptId ?? sessionPrompt ?? "default", selection.ModelId, effort);
    }

    // A send is the owned command of the desktop: the key of the request is its client retry key, so a retry after a lost
    // reply returns the first receipt and never sends twice.
    private async Task<FleetCommandResult> SendAsync(FleetNodeCall call, FleetSendParams parameters, CancellationToken cancellationToken)
    {
        RequireWork();
        RequireNotHalted();
        var retryKey = RetryKey(call);
        RequirePrompt(parameters.Text);
        var session = await RequireSessionAsync(parameters.SessionId, cancellationToken).ConfigureAwait(false);
        RequirePolicySelection(session, parameters.Selection);
        var text = parameters.Text;
        // An agent's prompt is checked and attributed once: a retry of the same key gets the first receipt.
        if (parameters.Agent is not null)
        {
            return await Once(call, FleetNodeJsonContext.Default.FleetCommandResult, async () =>
            {
                var attributed = await AgentPromptAsync(call, parameters.Agent, Protocol.FleetAgentRequestKinds.Send, session.SessionId, text, parameters.Selection,
                    CancellationToken.None).ConfigureAwait(false);
                return Admit(_host.Commands.AdmitSend(new OwnedTextSendRequest(retryKey, session.SessionId, attributed), CancellationToken.None));
            }).ConfigureAwait(false);
        }

        var selection = Selection(parameters.Selection, session.ResolvedProviderKey, session.AgentPromptId);
        // A send the runtime knows already (a retry) is answered as it was; a new one starts a run the budgets admit first.
        return await WithinBudgetAsync(retryKey, session, null, HeldSendOf(retryKey, session.SessionId, text, null, selection)).ConfigureAwait(false);
    }

    private async Task<FleetQueueResult> QueueAsync(FleetNodeCall call, FleetQueueParams parameters, CancellationToken cancellationToken)
    {
        RequireWork();
        RequireNotHalted();
        RequirePrompt(parameters.Text);
        _ = await RequireRemoteSessionAsync(parameters.SessionId, cancellationToken).ConfigureAwait(false);
        return await Once(call, FleetNodeJsonContext.Default.FleetQueueResult, async () =>
        {
            var session = await RequireSessionAsync(parameters.SessionId, CancellationToken.None).ConfigureAwait(false);
            var text = parameters.Agent is null ? parameters.Text
                : await AgentPromptAsync(call, parameters.Agent, Protocol.FleetAgentRequestKinds.Queue, session.SessionId, parameters.Text, null, CancellationToken.None)
                    .ConfigureAwait(false);
            var item = await _host.RuntimeService.QueuePromptAsync(session, text, "queue", null, CancellationToken.None).ConfigureAwait(false);
            return new FleetQueueResult("queued", item.QueueItemId);
        }).ConfigureAwait(false);
    }

    private async Task<FleetCommandResult> SteerAsync(FleetNodeCall call, FleetSteerParams parameters, CancellationToken cancellationToken)
    {
        RequireWork();
        RequireNotHalted();
        var retryKey = RetryKey(call);
        RequirePrompt(parameters.Text);
        var sessionId = await RequireRemoteSessionAsync(parameters.SessionId, cancellationToken).ConfigureAwait(false);
        if (parameters.RuntimeInstanceId == Guid.Empty || parameters.AttachmentGeneration <= 0 || string.IsNullOrWhiteSpace(parameters.RunId))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "Steering is bound to the runtime, attachment and run of sessions.state.");
        if (parameters.Agent is not null)
        {
            return await Once(call, FleetNodeJsonContext.Default.FleetCommandResult, async () =>
            {
                var attributed = await AgentPromptAsync(call, parameters.Agent, Protocol.FleetAgentRequestKinds.Steer, sessionId, parameters.Text, null,
                    CancellationToken.None).ConfigureAwait(false);
                return Admit(_host.Commands.AdmitSteer(new OwnedTextSteerRequest(retryKey, sessionId, parameters.RuntimeInstanceId,
                    parameters.AttachmentGeneration, parameters.RunId, attributed), CancellationToken.None));
            }).ConfigureAwait(false);
        }

        return Admit(_host.Commands.AdmitSteer(new OwnedTextSteerRequest(retryKey, sessionId, parameters.RuntimeInstanceId,
            parameters.AttachmentGeneration, parameters.RunId, parameters.Text), cancellationToken));
    }

    private async Task<FleetCommandResult> AbortAsync(FleetNodeCall call, FleetAbortParams parameters, CancellationToken cancellationToken)
    {
        var retryKey = RetryKey(call);
        var sessionId = await RequireRemoteSessionAsync(parameters.SessionId, cancellationToken).ConfigureAwait(false);
        if (parameters.OperationId is { } operation)
        {
            if (operation == Guid.Empty) throw new FleetException(FleetErrorCodes.InvalidParams, null, "The operation of a send is required.");
            return Admit(_host.Commands.AdmitAbort(new OwnedAbortRequest(retryKey, operation), cancellationToken));
        }

        if (parameters.RuntimeInstanceId is not { } runtime || runtime == Guid.Empty || parameters.AttachmentGeneration is not > 0 || string.IsNullOrWhiteSpace(parameters.RunId))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "Stopping names the operation of a send, or the runtime, attachment and run of sessions.state.");
        return Admit(_host.Commands.AdmitAbortRun(new OwnedAbortRunRequest(retryKey, sessionId, runtime,
            parameters.AttachmentGeneration.Value, parameters.RunId), cancellationToken));
    }

    private static void RequirePrompt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxPromptLength)
            throw new FleetException(FleetErrorCodes.InvalidParams, null, $"A prompt has 1 to {MaxPromptLength} characters.");
    }

    private static FleetCommandResult Admit(OwnedSessionCommandAdmission admission)
    {
        var status = admission.Kind switch
        {
            OwnedSessionCommandAdmissionKind.Accepted => "accepted",
            OwnedSessionCommandAdmissionKind.Replay => "replay",
            OwnedSessionCommandAdmissionKind.Conflict => "conflict",
            OwnedSessionCommandAdmissionKind.Busy => "busy",
            OwnedSessionCommandAdmissionKind.Capacity => "capacity",
            OwnedSessionCommandAdmissionKind.Closed => "closed",
            OwnedSessionCommandAdmissionKind.UnknownTarget => "unknown_target",
            OwnedSessionCommandAdmissionKind.Expired => "expired",
            _ => admission.Kind.ToString().ToLowerInvariant(),
        };
        if (admission.Receipt is not { } receipt) return new FleetCommandResult(status);
        var settled = receipt.Completion.IsCompletedSuccessfully ? receipt.Completion.Result : null;
        return new FleetCommandResult(status, receipt.OperationId, settled?.Outcome.ToString(), settled?.RunId?.Value, settled?.Code);
    }
}
