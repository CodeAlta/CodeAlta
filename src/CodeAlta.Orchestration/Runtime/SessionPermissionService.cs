using System.Threading.Channels;
using System.Runtime.ExceptionServices;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime.Actors;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>Identifies one permission attempt in a trusted application callback association.</summary>
/// <param name="SessionId">Captured session identity, not a renderer authorization grant.</param>
/// <param name="RunId">Provider run identity, or null when the provider supplies none.</param>
/// <param name="InteractionId">Provider interaction identity.</param>
/// <param name="AttemptId">Fresh application identity; reused provider IDs never reuse this value.</param>
public sealed record SessionPermissionHandle(string SessionId, string? RunId, string InteractionId, Guid AttemptId);

/// <summary>
/// Immutable pending summary, independent of open tabs and lossy timeline streams.
/// Only scalar preview data is retained; raw payloads and mutable provider collections are not retained.
/// This trusted application contract is neither renderer authorization nor durable restart recovery.
/// </summary>
/// <param name="Handle">Attempt identity.</param>
/// <param name="ProviderId">Provider identity.</param>
/// <param name="Timestamp">Request timestamp.</param>
/// <param name="Kind">Request kind.</param>
/// <param name="Command">Optional command preview.</param>
/// <param name="WorkingDirectory">Optional command directory.</param>
/// <param name="Reason">Optional reason.</param>
/// <param name="GrantRoot">Optional file-change grant root.</param>
public sealed record SessionPermissionSnapshot(
    SessionPermissionHandle Handle, ModelProviderId ProviderId, DateTimeOffset Timestamp, string Kind,
    string? Command, string? WorkingDirectory, string? Reason, string? GrantRoot)
{
    /// <summary>
    /// Gets whether the command or the reason is longer than what the review shows: they are then cut, and what is
    /// allowed is longer than what is shown.
    /// </summary>
    public bool Shortened { get; init; }

    /// <summary>Gets the skill of an MCP server the request asks to load, for a request of kind <c>skill</c>; null otherwise.</summary>
    public SessionPermissionSkill? Skill { get; init; }

    /// <summary>Gets the user's browser, profile and site the request asks to drive, for a request of kind <c>browser</c>; null otherwise.</summary>
    public SessionPermissionBrowser? Browser { get; init; }

    /// <summary>
    /// Gets a value indicating whether a sandboxed session asks to run the command outside its sandbox
    /// (<see cref="AgentCommandPermissionRequest.OutsideSandbox"/>): no permission mode answers it for the user.
    /// </summary>
    public bool OutsideSandbox { get; init; }
}

/// <summary>The skill of an MCP server a permission request asks to load, as the review shows it.</summary>
/// <param name="Server">The key of the server that publishes it.</param>
/// <param name="Name">Its name.</param>
/// <param name="Uri">The URI of its <c>SKILL.md</c>.</param>
/// <param name="FileCount">The number of its files; 0 when its server generates them.</param>
/// <param name="TotalBytes">The number of bytes of its files; 0 when its server generates them.</param>
/// <param name="Dynamic">Whether its server generates its files: it can then be allowed once only.</param>
public sealed record SessionPermissionSkill(string Server, string Name, string Uri, int FileCount, long TotalBytes, bool Dynamic);

/// <summary>What a permission request asks to drive in the user's own browser, as the review shows it.</summary>
/// <param name="Browser">The name of the browser.</param>
/// <param name="Profile">The profile.</param>
/// <param name="Site">The site the session would act on; null for the use of the browser itself.</param>
public sealed record SessionPermissionBrowser(string Browser, string Profile, string? Site);

/// <summary>A permission registration with a read-only completion; no frontend owns its completion source.</summary>
public sealed class SessionPermissionRegistration
{
    private readonly Task<AgentPermissionDecision> _decision;
    private readonly CancellationToken _cancellationToken;

    internal SessionPermissionRegistration(SessionPermissionSnapshot snapshot, Task<AgentPermissionDecision> completion,
        Task<AgentPermissionDecision> decision, CancellationToken cancellationToken)
    {
        Snapshot = snapshot;
        Completion = completion;
        _decision = decision;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Gets the immutable request summary and attempt identity.</summary>
    public SessionPermissionSnapshot Snapshot { get; }

    /// <summary>Gets the exactly-once decision, including Cancel on caller cancellation or owner shutdown.</summary>
    public Task<AgentPermissionDecision> Completion { get; }

    /// <summary>Gets a presentation guard; resolution still validates the full handle under the owner.</summary>
    public bool IsPending => !_decision.IsCompleted && !_cancellationToken.IsCancellationRequested;
}

/// <summary>
/// Application-owned permission policy and pending completions. The mailbox serializes register/list/resolve/cancel,
/// never user waits. Completed attempts are removed, not retained as tombstones. Provider runtime choice enforcement
/// remains authoritative. Disposing presentation alone must not cancel this service or its requests.
/// </summary>
public sealed partial class SessionPermissionService : IAsyncDisposable
{
    private readonly Func<bool> _autoApproveOwnedPermissions;

    /// <summary>Creates a permission owner with deny-by-default owned callbacks.</summary>
    public SessionPermissionService() : this(false) { }

    /// <summary>Creates a permission owner with an explicit host-owned automatic approval policy.</summary>
    /// <remarks>Automatic approval grants AllowOnce, like the TUI, and is not a filesystem sandbox.
    /// Explicit per-operation command review still takes precedence.</remarks>
    public SessionPermissionService(bool autoApproveOwnedPermissions) : this(() => autoApproveOwnedPermissions) { }

    /// <summary>
    /// Creates a permission owner whose automatic approval policy is read again for every send, and for every
    /// request outside a send, so a host whose user turns review on or off applies it to what comes next
    /// without being restarted. A send keeps the policy it started with until it ends.
    /// </summary>
    /// <param name="autoApproveOwnedPermissions">Whether a request no explicit review takes is granted AllowOnce.</param>
    /// <exception cref="ArgumentNullException"><paramref name="autoApproveOwnedPermissions"/> is null.</exception>
    public SessionPermissionService(Func<bool> autoApproveOwnedPermissions)
    {
        ArgumentNullException.ThrowIfNull(autoApproveOwnedPermissions);
        _autoApproveOwnedPermissions = autoApproveOwnedPermissions;
        OwnedDefaultPermissionHandler = HandleOwnedDefaultAsync;
    }

    /// <summary>
    /// Gets or sets the policy of a session by its identifier, for a host whose sessions have a permission mode of
    /// their own. Null, the default, leaves every session the one policy of the host.
    /// </summary>
    internal Func<string, SessionPermissionPolicy>? SessionPolicy { get; set; }

    /// <summary>
    /// Gets or sets what a request outside a send of the owner is reviewed on: the live attachment of its session,
    /// or null when the session has none. Null, the default, leaves such a request without a review: it is denied
    /// when the policy of its session does not grant it.
    /// </summary>
    internal Func<string, OwnedReviewTarget?>? SessionReviewTarget { get; set; }

    /// <summary>The attachment a request outside a send of the owner is reviewed on.</summary>
    /// <param name="RuntimeId">The runtime that owns the attachment.</param>
    /// <param name="Attachment">The live attachment of the session.</param>
    /// <param name="ProviderId">The provider of the session.</param>
    internal sealed record OwnedReviewTarget(Guid RuntimeId, OwnedProviderEventForwarding.Attachment Attachment, ModelProviderId ProviderId);

    // The answer of the host to a request outside a send of the owner: a prompt the host queued, a session another
    // session drives, a turn its provider started by itself. It is granted when the policy of its session grants it.
    // Otherwise it waits for the user as a request of a send does, in a review of its own that lasts as long as the
    // request, on the card of its session; a session that cannot be reviewed has it denied.
    private async Task<AgentPermissionDecision> HandleOwnedDefaultAsync(AgentPermissionRequest request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposeStarted) != 0) return new(AgentPermissionDecisionKind.Cancel);
        if (FullTrust) return new(OnlyTheUserAllows(request) ? AgentPermissionDecisionKind.Deny : AgentPermissionDecisionKind.AllowOnce);
        if (SessionPolicy is not { } policyOf || string.IsNullOrWhiteSpace(request.SessionId))
            return new(_autoApproveOwnedPermissions() && !AlwaysAsks(request) ? AgentPermissionDecisionKind.AllowOnce : AgentPermissionDecisionKind.Deny);
        var policy = policyOf(request.SessionId);
        if (!AlwaysAsks(request) && (policy == SessionPermissionPolicy.Approve || (policy == SessionPermissionPolicy.AcceptEdits && request is AgentFileChangePermissionRequest)))
            return new(AgentPermissionDecisionKind.AllowOnce);
        if (SessionReviewTarget?.Invoke(request.SessionId) is not { } target) return new(AgentPermissionDecisionKind.Deny);
        var execution = await CreateOwnedExecutionAsync(Guid.NewGuid(), request.SessionId, cancellationToken, policy, enableUserInput: false).ConfigureAwait(false);
        if (execution is null) return new(AgentPermissionDecisionKind.Deny);
        try
        {
            return await BindOwnedExecutionAsync(execution, target.RuntimeId, target.Attachment, target.ProviderId).ConfigureAwait(false)
                ? await HandleOwnedCommandAsync(execution, request, cancellationToken).ConfigureAwait(false)
                : new(AgentPermissionDecisionKind.Deny);
        }
        finally
        {
            await CloseOwnedExecutionAsync(execution).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets or sets what a request that waits for the user goes through first, during a send or after it: the script of Code
    /// Mode it comes from takes its turn among the questions of its run, its card says it waits, and the <c>exec</c> that waits
    /// for it does not count that time. It returns what ends that wait, or null for a request of no script. Null for none.
    /// </summary>
    internal Func<AgentPermissionRequest, CancellationToken, ValueTask<IDisposable?>>? WaitingForUser { get; set; }

    // Whether a request of a send waits for the user: neither its grants nor the policy of its session answer it.
    private bool WaitsForUser(OwnedPermissionExecution execution, AgentPermissionRequest request)
    {
        var (reviewCommands, autoApprove, approveFileChanges) = Grants(execution);
        return !((autoApprove || (approveFileChanges && request is AgentFileChangePermissionRequest)) && !AlwaysAsks(request))
            && (reviewCommands || AlwaysAsks(request));
    }

    /// <summary>
    /// Asks the user, on the card of a session, a request that a command of that session makes (the load of a skill of
    /// an MCP server): in every permission mode, as no mode answers such a request for the user. The request is given
    /// the session and its provider. It is denied when the session has no live attachment to be reviewed on, and
    /// canceled with the caller.
    /// </summary>
    /// <param name="sessionId">The session whose command asks.</param>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token that cancels the request: its answer is then <see cref="AgentPermissionDecisionKind.Cancel"/>.</param>
    /// <returns>The answer of the user.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async Task<AgentPermissionDecision> RequestForSessionAsync(string sessionId, AgentPermissionRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposeStarted) != 0) return new(AgentPermissionDecisionKind.Cancel);
        if (FullTrust) return new(OnlyTheUserAllows(request) ? AgentPermissionDecisionKind.Deny : AgentPermissionDecisionKind.AllowOnce);
        if (SessionReviewTarget?.Invoke(sessionId) is not { } target) return new(AgentPermissionDecisionKind.Deny);
        request = request with { SessionId = sessionId, ProviderId = target.ProviderId };
        // A review of its own that asks whatever the mode of the session: it follows no policy.
        var execution = await CreateOwnedExecutionAsync(Guid.NewGuid(), sessionId, cancellationToken).ConfigureAwait(false);
        if (execution is null) return new(AgentPermissionDecisionKind.Deny);
        try
        {
            return await BindOwnedExecutionAsync(execution, target.RuntimeId, target.Attachment, target.ProviderId).ConfigureAwait(false)
                ? await HandleOwnedCommandAsync(execution, request, cancellationToken).ConfigureAwait(false)
                : new(AgentPermissionDecisionKind.Deny);
        }
        finally
        {
            await CloseOwnedExecutionAsync(execution).ConfigureAwait(false);
        }
    }

    // A request no permission mode answers for the user: the load of a skill of an MCP server, whose instructions may ask
    // for commands, and a command a sandboxed session asks to run outside its sandbox. It waits for the user, or is
    // denied where nobody can be asked.
    internal static bool AlwaysAsks(AgentPermissionRequest request) => request is AgentSkillPermissionRequest or AgentBrowserPermissionRequest || IsOutsideSandbox(request);

    // A host that trusts its sessions fully still has nobody to let a command out of a sandbox: the user chose the sandbox.
    private static bool IsOutsideSandbox(AgentPermissionRequest request) => request is AgentCommandPermissionRequest { OutsideSandbox: true };

    // Nor has it anybody to let a session into the user's own browser, with the user's sign-ins.
    private static bool OnlyTheUserAllows(AgentPermissionRequest request) => IsOutsideSandbox(request) || request is AgentBrowserPermissionRequest;

    /// <summary>
    /// Gets or sets whether every request the host answers by default is granted, the loads of skills that otherwise
    /// always ask included: a host that runs without a user and trusts its sessions fully (a headless run). Default false.
    /// </summary>
    internal bool FullTrust { get; set; }

    // Whether the user may allow a request from now on: only the load of a skill whose files can be checked.
    private static bool OffersAllowAlways(SessionPermissionSnapshot snapshot) => snapshot.Skill is { Dynamic: false } || snapshot.Browser is { Site: not null };

    private readonly OrchestrationMailboxActor _actor = new(128);
    private readonly Dictionary<SessionPermissionHandle, PendingPermission> _pending = new();
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopped;
    private int _disposeStarted;
    internal const int OwnedExecutionLimit = 64;
    internal const int OwnedPendingLimit = 128;
    internal const int OwnedPendingPerExecutionLimit = 4;
    internal const int OwnedIdentityLimit = 128;
    internal const int OwnedCommandLimit = 4096;
    internal const int OwnedDirectoryLimit = 1024;
    internal const int OwnedReasonLimit = 1024;
    private readonly Dictionary<Guid, OwnedPermissionExecution> _ownedExecutions = [];
    private readonly HashSet<PendingPermission> _ownedDeliveries = [];
    private bool _ownedAdmissionClosed;

    // Immutable host-policy capabilities, not per-send or mutable latest-callback associations.
    // Owned sends reject a reused coordinator whose session defaults came from another caller.

    /// <summary>
    /// Gets the host's default permission decision for sessions it owns: allow once when the host, or the policy of
    /// the session, approves automatically; otherwise the request waits for the user where the host reviews its
    /// sessions by their permission mode, and is denied elsewhere. A session created with it can later be driven by
    /// the owner's commands.
    /// </summary>
    public AgentPermissionRequestHandler OwnedDefaultPermissionHandler { get; }

    // The default decision of one send that no owned execution reviews. A session with a mode of its own follows the
    // mode it has at each request, as a request outside a send does: a change of the mode applies to the running send
    // at once, and a request a mode that asks no longer grants waits on the card of the session. Without one, the
    // automatic approval of the host, read now, decides until the send ends.
    internal AgentPermissionRequestHandler CreateSendDefaultPermissionHandler(SessionPermissionPolicy? policy)
    {
        if (FullTrust || policy is not null && SessionPolicy is not null) return OwnedDefaultPermissionHandler;
        var autoApprove = policy is { } own ? own == SessionPermissionPolicy.Approve : _autoApproveOwnedPermissions();
        return (request, token) => Task.FromResult(new AgentPermissionDecision(
            token.IsCancellationRequested || Volatile.Read(ref _disposeStarted) != 0 ? AgentPermissionDecisionKind.Cancel
            : autoApprove && !AlwaysAsks(request) ? AgentPermissionDecisionKind.AllowOnce : AgentPermissionDecisionKind.Deny));
    }

    /// <summary>Gets the host's default user-input answer for sessions it owns: the request is canceled.</summary>
    public AgentUserInputRequestHandler OwnedDefaultUserInputHandler { get; }
        = static (_, _) => Task.FromCanceled<AgentUserInputResponse>(new CancellationToken(true));

    // Only the command owner creates one of these per immutable receipt. Closed records are removed
    // from the service; retained delegates hold their closed record, never a lookup of the latest send.
    internal sealed class OwnedPermissionExecution(SessionPermissionService owner, Guid operationId, string sessionId, CancellationToken token)
    {
        internal SessionPermissionService Owner { get; } = owner;
        internal Guid OperationId { get; } = operationId;
        internal string SessionId { get; } = sessionId;
        internal CancellationToken ExecutionToken { get; set; } = token;
        internal CancellationToken AttachmentToken { get; set; }
        internal AgentRunId? RunId { get; set; }
        internal CancellationToken RunToken { get; set; }
        internal bool RunBound { get; set; }
        internal OwnedProviderEventForwarding.Attachment? Attachment { get; set; }
        internal Guid RuntimeId { get; set; }
        internal long AttachmentOrdinal { get; set; }
        internal string? ProviderId { get; set; }
        internal bool Bound { get; set; }
        internal bool Closed { get; set; }
        internal bool ReviewCommands { get; init; }
        // Whether a request this send does not review is granted: read once when the send starts, like its review.
        internal bool AutoApprove { get; init; }
        // Whether the file changes of this send are granted while its commands are reviewed.
        internal bool ApproveFileChanges { get; init; }
        // Whether this send follows the mode its session has at each request rather than the three flags above, read
        // when it started: a session with a mode of its own, whose change applies to the running send at once.
        internal bool FollowsSessionPolicy { get; init; }
        internal bool EnableUserInput { get; init; }
        internal HashSet<PendingUserInput> InputDeliveries { get; } = [];
        internal HashSet<PendingPermission> Deliveries { get; } = [];
        internal Task? Closure { get; set; }
    }

    internal ValueTask<OwnedPermissionExecution?> CreateOwnedExecutionAsync(Guid operationId, string sessionId, CancellationToken token)
        => CreateOwnedExecutionAsync(operationId, sessionId, token, true, false);

    internal ValueTask<OwnedPermissionExecution?> CreateOwnedExecutionAsync(Guid operationId, string sessionId,
        CancellationToken token, bool reviewCommands, bool enableUserInput)
        => ExecuteAsync<OwnedPermissionExecution?>(() =>
        {
            if (_stopped || _ownedAdmissionClosed || token.IsCancellationRequested || operationId == Guid.Empty
                || !ValidOwnedText(sessionId, OwnedIdentityLimit, required: true, identity: true)
                || _ownedExecutions.Count >= OwnedExecutionLimit || _ownedExecutions.ContainsKey(operationId)) return null;
            // A change of the policy applies to the next send: one that runs keeps asking, or approving, as it started.
            var execution = new OwnedPermissionExecution(this, operationId, sessionId, token)
            { ReviewCommands = reviewCommands, AutoApprove = !reviewCommands && _autoApproveOwnedPermissions(), EnableUserInput = enableUserInput };
            _ownedExecutions.Add(operationId, execution);
            return execution;
        }, null);

    // The same for a send of a session that has a policy of its own: the flags are the policy the send starts with,
    // and each request then follows the policy the session has when it comes.
    internal ValueTask<OwnedPermissionExecution?> CreateOwnedExecutionAsync(Guid operationId, string sessionId,
        CancellationToken token, SessionPermissionPolicy policy, bool enableUserInput)
        => ExecuteAsync<OwnedPermissionExecution?>(() =>
        {
            if (_stopped || _ownedAdmissionClosed || token.IsCancellationRequested || operationId == Guid.Empty
                || !ValidOwnedText(sessionId, OwnedIdentityLimit, required: true, identity: true)
                || _ownedExecutions.Count >= OwnedExecutionLimit || _ownedExecutions.ContainsKey(operationId)) return null;
            var execution = new OwnedPermissionExecution(this, operationId, sessionId, token)
            {
                ReviewCommands = policy != SessionPermissionPolicy.Approve,
                AutoApprove = policy == SessionPermissionPolicy.Approve,
                ApproveFileChanges = policy == SessionPermissionPolicy.AcceptEdits,
                EnableUserInput = enableUserInput,
                FollowsSessionPolicy = SessionPolicy is not null,
            };
            _ownedExecutions.Add(operationId, execution);
            return execution;
        }, null);

    internal ValueTask<bool> BindOwnedExecutionAsync(OwnedPermissionExecution execution, Guid runtimeId,
        OwnedProviderEventForwarding.Attachment attachment, ModelProviderId providerId)
        => ExecuteAsync(() =>
        {
            if (!Owns(execution) || execution.Bound || execution.Closed || _ownedAdmissionClosed
                || execution.ExecutionToken.IsCancellationRequested || runtimeId == Guid.Empty || attachment.IsRetiring
                || !string.Equals(attachment.Identity.SessionId, execution.SessionId, StringComparison.Ordinal)
                || !ValidOwnedText(providerId.Value, OwnedIdentityLimit, required: true, identity: true)) return false;
            execution.Bound = true;
            execution.RuntimeId = runtimeId;
            execution.AttachmentOrdinal = attachment.Ordinal;
            execution.ProviderId = providerId.Value;
            execution.Attachment = attachment;
            execution.AttachmentToken = attachment.Cancellation.Token;
            return true;
        }, false);

    // Only the provider's awaited Started hook supplies this identity/token. Legacy providers that
    // ignore the hook retain their prior unbound behavior; observations never manufacture a binding.
    internal ValueTask<bool> BindOwnedRunAsync(OwnedPermissionExecution execution, AgentRunId runId, CancellationToken runToken)
        => ExecuteAsync(() =>
        {
            if (!CanUse(execution) || execution.RunBound || execution.Deliveries.Count != 0 || execution.InputDeliveries.Count != 0
                || !ValidOwnedText(runId.Value, OwnedIdentityLimit, required: true, identity: true)
                || !runToken.CanBeCanceled || runToken.IsCancellationRequested) return false;
            execution.RunId = runId;
            execution.RunToken = runToken;
            execution.RunBound = true;
            return true;
        }, false);

    internal AgentRunLifecycle CreateOwnedRunLifecycle(OwnedPermissionExecution execution) => new OwnedRunLifecycle(this, execution);

    private sealed class OwnedRunLifecycle(SessionPermissionService owner, OwnedPermissionExecution execution) : AgentRunLifecycle
    {
        public override async Task StartedAsync(AgentRunId runId, CancellationToken executionToken)
        {
            if (await owner.BindOwnedRunAsync(execution, runId, executionToken).ConfigureAwait(false)) return;
            executionToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The owned execution cannot bind this provider run.");
        }

        // Captured execution object, never lookup by a supplied run/session or attachment-wide closure.
        public override Task ClosingAsync(AgentRunId runId) => owner.CloseOwnedExecutionAsync(execution);
    }

    internal async Task<AgentPermissionDecision> HandleOwnedCommandAsync(OwnedPermissionExecution execution,
        AgentPermissionRequest request, CancellationToken cancellationToken)
    {
        // The request of a script waits its turn among the questions of its run before it is shown (an execution that has
        // closed hands the request over below, to one that takes the turn).
        IDisposable? wait = null;
        if (!execution.Closed && WaitingForUser is { } waiting && WaitsForUser(execution, request))
        {
            try
            {
                wait = await waiting(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new(AgentPermissionDecisionKind.Cancel);
            }
        }

        using (wait)
        {
            return await HandleOwnedCommandCoreAsync(execution, request, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AgentPermissionDecision> HandleOwnedCommandCoreAsync(OwnedPermissionExecution execution,
        AgentPermissionRequest request, CancellationToken cancellationToken)
    {
        var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterSend = false;
        var delivery = await ExecuteAsync<Task<AgentPermissionDecision>?>(() =>
        {
            // The send has ended, its run with it: the request comes from the script of one of its calls that went on in
            // the background. It is answered as a request outside a send, never denied for the send being over.
            if (execution.Closed && ReferenceEquals(execution.Owner, this) && !_stopped && !_ownedAdmissionClosed && !cancellationToken.IsCancellationRequested
                && Volatile.Read(ref _disposeStarted) == 0)
            {
                afterSend = true;
                return null;
            }

            var (reviewCommands, autoApprove, approveFileChanges) = Grants(execution);
            if ((autoApprove || (approveFileChanges && request is AgentFileChangePermissionRequest)) && !AlwaysAsks(request)
                && CanUse(execution) && !cancellationToken.IsCancellationRequested
                && request.SessionId == execution.SessionId && request.ProviderId.Value == execution.ProviderId
                && (!execution.RunBound || request.RunId is null || request.RunId == execution.RunId))
                return Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce));
            if ((!reviewCommands && !AlwaysAsks(request)) || !CanUse(execution) || cancellationToken.IsCancellationRequested || !Eligible(execution, request)
                || !HasOwnedDeliveryCapacity(execution)) return null;
            var handle = new SessionPermissionHandle(execution.SessionId, request.RunId?.Value, request.InteractionId, Guid.NewGuid());
            var snapshot = OwnedSnapshot(handle, request);
            var pending = new PendingPermission(snapshot,
                new(TaskCreationOptions.RunContinuationsAsynchronously), cancellationToken, execution);
            _pending.Add(handle, pending);
            execution.Deliveries.Add(pending);
            _ownedDeliveries.Add(pending);
            // Starts an asynchronous wait, never awaits a decision in the mailbox. The bounded delivery
            // remains owned until its linked source is disposed and its cleanup message is observed.
            pending.Delivery = DeliverOwnedAsync(pending, execution.ExecutionToken, execution.AttachmentToken, execution.RunToken, launch.Task);
            return pending.Delivery;
        }, null).ConfigureAwait(false);
        launch.TrySetResult();
        if (afterSend) return await HandleOwnedDefaultAsync(request, cancellationToken).ConfigureAwait(false);
        return delivery is null ? new(AgentPermissionDecisionKind.Deny) : await delivery.ConfigureAwait(false);
    }

    // What a send grants and reviews for one request: the policy its session has now when it follows it, the flags it
    // started with otherwise.
    private (bool ReviewCommands, bool AutoApprove, bool ApproveFileChanges) Grants(OwnedPermissionExecution execution)
    {
        if (!execution.FollowsSessionPolicy || SessionPolicy is not { } policyOf)
            return (execution.ReviewCommands, execution.AutoApprove, execution.ApproveFileChanges);
        var policy = policyOf(execution.SessionId);
        return (policy != SessionPermissionPolicy.Approve, policy == SessionPermissionPolicy.Approve, policy == SessionPermissionPolicy.AcceptEdits);
    }

    /// <summary>
    /// Answers the permission requests of a session that wait for the user and that a new policy of the session
    /// grants: all of them for <see cref="SessionPermissionPolicy.Approve"/>, the file changes for
    /// <see cref="SessionPermissionPolicy.AcceptEdits"/>, none for <see cref="SessionPermissionPolicy.Review"/>. Only
    /// the requests of sends that follow the policy of their session are answered; a question to the user is not a
    /// permission request and is never answered.
    /// </summary>
    /// <param name="sessionId">The session whose policy changed.</param>
    /// <param name="policy">The policy it has now.</param>
    /// <returns>How many requests were allowed.</returns>
    internal ValueTask<int> GrantWaitingOwnedAsync(string sessionId, SessionPermissionPolicy policy)
        => ExecuteAsync(() =>
        {
            if (policy == SessionPermissionPolicy.Review || string.IsNullOrWhiteSpace(sessionId)) return 0;
            var allowed = 0;
            // A copy: an answer leaves the set when its delivery ends, which is a later message of this mailbox.
            foreach (var pending in _ownedDeliveries.ToArray())
            {
                if (pending.OwnedExecution is not { FollowsSessionPolicy: true } execution || execution.SessionId != sessionId) continue;
                if (policy == SessionPermissionPolicy.AcceptEdits && pending.Snapshot.Kind != FileChangeKind) continue;
                // The load of a skill of an MCP server, and a command out of a sandbox, wait for the user whatever the mode.
                if (pending.Snapshot.Kind is AgentSkillPermissionRequest.SkillKind or AgentBrowserPermissionRequest.BrowserKind || pending.Snapshot.OutsideSandbox) continue;
                if (Complete(pending.Snapshot.Handle, AgentPermissionDecisionKind.AllowOnce)) allowed++;
            }
            return allowed;
        }, 0);

    // The kind of a file change request, as its snapshot keeps it.
    private const string FileChangeKind = "fileChange";

    // Keep retained provider delegates independent of a runtime send's compiler-generated closure
    // (which also owns captured entries, handle uses and linked cancellation sources).
    internal AgentPermissionRequestHandler CreateOwnedCommandHandler(OwnedPermissionExecution execution)
        => (request, token) => HandleOwnedCommandAsync(execution, request, token);

    private async Task<AgentPermissionDecision> DeliverOwnedAsync(PendingPermission pending,
        CancellationToken executionToken, CancellationToken attachmentToken, CancellationToken runToken, Task launch)
    {
        await launch.ConfigureAwait(false);
        pending.Lifetime = new OwnedDeliveryLifetime(pending);
        return await pending.Lifetime.RunAsync(
            token => AwaitDecisionAsync(pending.Snapshot.Handle, pending.Completion.Task, token),
            async () => { await _actor.AskAsync(_ => ValueTask.FromResult(Complete(pending.Snapshot.Handle, AgentPermissionDecisionKind.Cancel))).ConfigureAwait(false); },
            async () =>
            {
                await _actor.AskAsync(_ =>
                {
                    pending.OwnedExecution!.Deliveries.Remove(pending);
                    _ownedDeliveries.Remove(pending);
                    return ValueTask.FromResult(true);
                }).ConfigureAwait(false);
            }, [executionToken, attachmentToken, runToken, pending.CancellationToken]).ConfigureAwait(false);
    }

    internal Task CloseOwnedExecutionAsync(OwnedPermissionExecution execution)
        => JoinOwnedClosureAsync(() => ReferenceEquals(execution.Owner, this) ? CloseOwned(execution) : Task.CompletedTask);

    internal Task InvalidateOwnedOperationAsync(Guid operationId)
        => JoinOwnedClosureAsync(() => _ownedExecutions.TryGetValue(operationId, out var execution) ? CloseOwned(execution) : Task.CompletedTask);

    internal Task InvalidateOwnedAttachmentAsync(OwnedProviderEventForwarding.Attachment attachment)
        => JoinOwnedClosureAsync(() => JoinDeliveryOriginalsAsync(_ownedExecutions.Values
            .Where(execution => ReferenceEquals(execution.Attachment, attachment)).ToArray().Select(CloseOwned)));

    internal Task CloseOwnedAdmissionAsync()
        => JoinOwnedClosureAsync(() =>
        {
            _ownedAdmissionClosed = true;
            return JoinDeliveryOriginalsAsync(_ownedExecutions.Values.ToArray().Select(CloseOwned));
        });

    private async Task JoinOwnedClosureAsync(Func<Task> close)
    {
        var admission = new OwnedSessionCommandService.OriginalInvocation();
        Task? completion;
        try { completion = await admission.RunAsync(() => ExecuteAsync<Task?>(close, null).AsTask()).ConfigureAwait(false); }
        catch (Exception failure) { throw new AgentDependencyRetentionException("permission", "closure admission", [failure], new { Owner = this, Admission = admission }); }
        if (completion is null)
        {
            await JoinDeliveryOriginalsAsync([_shutdown.Task]).ConfigureAwait(false);
            return;
        }
        await JoinDeliveryOriginalsAsync([completion]).ConfigureAwait(false);
        // A concurrently disposed service owns all remaining completions. Do not release a caller's
        // source/handle on ExecuteAsync's stopped fallback before that shutdown has actually joined.
        if (Volatile.Read(ref _disposeStarted) != 0) await JoinDeliveryOriginalsAsync([_shutdown.Task]).ConfigureAwait(false);
    }

    private Task CloseOwned(OwnedPermissionExecution execution)
    {
        if (execution.Closure is not null) return execution.Closure;
        if (!Owns(execution)) return Task.CompletedTask;
        var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controls = new List<Exception>();
        var deliveries = execution.Deliveries.ToArray();
        var inputs = execution.InputDeliveries.ToArray();
        execution.Closure = CloseCoreAsync();
        execution.Closed = true;
        foreach (var pending in deliveries)
        {
            try { Complete(pending.Snapshot.Handle, AgentPermissionDecisionKind.Cancel); }
            catch (Exception failure) { controls.Add(failure); }
            finally { pending.Completion.TrySetResult(new(AgentPermissionDecisionKind.Cancel)); }
        }
        foreach (var pending in inputs)
        {
            try { CompleteInput(pending.Snapshot.Handle, null); }
            catch (Exception failure) { controls.Add(failure); }
            finally { pending.Completion.TrySetResult(null); }
        }
        launch.TrySetResult();
        return execution.Closure;

        async Task CloseCoreAsync()
        {
            await launch.Task.ConfigureAwait(false);
            var failures = new List<Exception>(controls);
            try
            {
                await JoinDeliveryOriginalsAsync(deliveries.Select(pending => (Task)pending.Delivery!)
                    .Concat(inputs.Select(pending => (Task)pending.Delivery!))).ConfigureAwait(false);
            }
            catch (Exception failure) { failures.Add(failure); }
            if (controls.Count != 0 || failures.Any(OwnedProviderEventForwarding.HasRetention))
                throw new AgentDependencyRetentionException(execution.SessionId, "permission closure", failures, new { Owner = this, Execution = execution, Deliveries = deliveries, Inputs = inputs });
            try
            {
                await _actor.AskAsync(_ =>
                {
                    _ownedExecutions.Remove(execution.OperationId);
                    execution.Attachment = null;
                    execution.ExecutionToken = default;
                    execution.AttachmentToken = default;
                    execution.RunToken = default;
                    return ValueTask.FromResult(true);
                }).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
                throw new AgentDependencyRetentionException(execution.SessionId, "permission closure index", failures, new { Owner = this, Execution = execution });
            }
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException(failures);
        }
    }

    private bool Owns(OwnedPermissionExecution execution)
        => ReferenceEquals(execution.Owner, this) && _ownedExecutions.TryGetValue(execution.OperationId, out var current)
            && ReferenceEquals(current, execution);

    private bool CanUse(OwnedPermissionExecution execution)
        => !_stopped && !_ownedAdmissionClosed && Owns(execution) && execution.Bound && !execution.Closed
            && !execution.ExecutionToken.IsCancellationRequested && !execution.AttachmentToken.IsCancellationRequested
            && (!execution.RunBound || !execution.RunToken.IsCancellationRequested)
            && execution.Attachment is { IsRetiring: false };

    private bool IsCanceled(PendingPermission pending)
        => pending.CancellationToken.IsCancellationRequested || (pending.OwnedExecution is { } execution && !CanUse(execution));

    // A request the owned review can present: it is addressed to this execution, and it is one of the two kinds
    // the review shows. A richer command prompt (parsed actions, network access, a policy amendment) is still
    // refused rather than shown as less than it is. A command or a reason longer than the review shows is shown
    // cut, and says so (`Shortened`): refused, it would be denied without the user seeing it at all.
    private static bool Eligible(OwnedPermissionExecution execution, AgentPermissionRequest request)
        => Addressed(execution, request) && request switch
        {
            AgentCommandPermissionRequest command => request.Kind == "commandExecution"
                && ValidOwnedText(command.Command, int.MaxValue, required: true)
                && ValidOwnedText(command.WorkingDirectory, OwnedDirectoryLimit, required: true)
                && ValidOwnedText(command.Reason, int.MaxValue, required: false)
                && command.ApprovalId is null && command.Actions is null && command.Network is null
                && command.ProposedExecPolicyAmendment is null && command.ProposedNetworkPolicyAmendments is null,
            AgentFileChangePermissionRequest change => request.Kind == "fileChange"
                && ValidOwnedText(change.GrantRoot, OwnedDirectoryLimit, required: true)
                && ValidOwnedText(change.Reason, int.MaxValue, required: false),
            AgentSkillPermissionRequest skill => request.Kind == AgentSkillPermissionRequest.SkillKind
                && ValidOwnedText(skill.Server, OwnedIdentityLimit, required: true)
                && ValidOwnedText(skill.SkillName, OwnedIdentityLimit, required: true)
                && ValidOwnedText(skill.Uri, OwnedDirectoryLimit, required: true)
                && skill.FileCount >= 0 && skill.TotalBytes >= 0,
            AgentBrowserPermissionRequest browser => request.Kind == AgentBrowserPermissionRequest.BrowserKind
                && ValidOwnedText(browser.Browser, OwnedIdentityLimit, required: true)
                && ValidOwnedText(browser.Profile, OwnedIdentityLimit, required: true)
                && ValidOwnedText(browser.Site, OwnedIdentityLimit, required: false, identity: browser.Site is not null),
            _ => false,
        };

    private static bool Addressed(OwnedPermissionExecution execution, AgentPermissionRequest request)
        => request.SessionId == execution.SessionId && request.ProviderId.Value == execution.ProviderId
            && ValidOwnedText(request.InteractionId, OwnedIdentityLimit, required: true, identity: true)
            && (request.RunId is null || ValidOwnedText(request.RunId.Value.Value, OwnedIdentityLimit, required: true, identity: true))
            && (!execution.RunBound || request.RunId is null || request.RunId == execution.RunId);

    // The presented form of an eligible request: a command carries its command line and folder, a file change
    // carries the root it asks to write under. Only a kind <see cref="Eligible"/> accepted reaches this.
    private static SessionPermissionSnapshot OwnedSnapshot(SessionPermissionHandle handle, AgentPermissionRequest request)
        => request switch
        {
            AgentCommandPermissionRequest command => new(handle, request.ProviderId, request.Timestamp, request.Kind,
                Shown(command.Command, OwnedCommandLimit), command.WorkingDirectory, Shown(command.Reason, OwnedReasonLimit), null)
            {
                Shortened = command.Command!.Length > OwnedCommandLimit || command.Reason?.Length > OwnedReasonLimit,
                OutsideSandbox = command.OutsideSandbox,
            },
            AgentFileChangePermissionRequest change => new(handle, request.ProviderId, request.Timestamp, request.Kind,
                null, null, Shown(change.Reason, OwnedReasonLimit), change.GrantRoot)
            {
                Shortened = change.Reason?.Length > OwnedReasonLimit,
            },
            AgentSkillPermissionRequest skill => new(handle, request.ProviderId, request.Timestamp, request.Kind, null, null, null, null)
            {
                Skill = new(skill.Server, skill.SkillName, skill.Uri, skill.FileCount, skill.TotalBytes, skill.Dynamic),
            },
            AgentBrowserPermissionRequest browser => new(handle, request.ProviderId, request.Timestamp, request.Kind, null, null, null, null)
            {
                Browser = new(browser.Browser, browser.Profile, browser.Site),
            },
            _ => throw new InvalidOperationException($"A permission request of kind '{request.Kind}' is not presented by the owned review."),
        };

    // The start of a text the review shows, cut without splitting a character.
    private static string? Shown(string? value, int limit)
    {
        if (value is null || value.Length <= limit) return value;
        var end = char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit;
        return value[..end];
    }

    private static bool ValidOwnedText(string? value, int limit, bool required, bool identity = false)
    {
        if (value is null) return !required;
        if (value.Length > limit || ((required || identity) && string.IsNullOrWhiteSpace(value))) return false;
        if (identity && !value.AsSpan().Trim().SequenceEqual(value.AsSpan())) return false;
        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            if (character == '\0' || (identity && char.IsControl(character))) return false;
            if (!char.IsSurrogate(character)) continue;
            if (!char.IsHighSurrogate(character) || ++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// Registers a trusted callback association. AutoApprove retains the existing AllowOnce policy.
    /// Cancellation (including before registration) and a disposed owner return Cancel, never approval.
    /// An explicit provider session ID is selected by the application adapter before calling this method.
    /// </summary>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="ArgumentException">The session or interaction identity is blank.</exception>
    public async Task<SessionPermissionRegistration> RegisterAsync(
        string sessionId, AgentPermissionRequest request, bool autoApprove, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InteractionId);
        var handle = new SessionPermissionHandle(sessionId, request.RunId?.Value, request.InteractionId, Guid.NewGuid());
        var command = request as AgentCommandPermissionRequest;
        var file = request as AgentFileChangePermissionRequest;
        var snapshot = new SessionPermissionSnapshot(handle, request.ProviderId, request.Timestamp, request.Kind,
            command?.Command, command?.WorkingDirectory, command?.Reason ?? file?.Reason, file?.GrantRoot)
        {
            Skill = request is AgentSkillPermissionRequest skill ? new(skill.Server, skill.SkillName, skill.Uri, skill.FileCount, skill.TotalBytes, skill.Dynamic) : null,
            Browser = request is AgentBrowserPermissionRequest browser ? new(browser.Browser, browser.Profile, browser.Site) : null,
            OutsideSandbox = command?.OutsideSandbox == true,
        };
        var completion = new TaskCompletionSource<AgentPermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registered = await ExecuteAsync(() =>
        {
            if (_stopped || cancellationToken.IsCancellationRequested)
            {
                completion.SetResult(new(AgentPermissionDecisionKind.Cancel));
            }
            else if (autoApprove && !AlwaysAsks(request))
            {
                completion.SetResult(new(AgentPermissionDecisionKind.AllowOnce));
            }
            else
            {
                _pending.Add(handle, new(snapshot, completion, cancellationToken));
            }

            return true;
        }, false).ConfigureAwait(false);
        if (!registered)
        {
            completion.TrySetResult(new(AgentPermissionDecisionKind.Cancel));
        }

        // This wait is outside the mailbox. Cancellation cleanup is part of the returned, observed task.
        return new(snapshot, AwaitDecisionAsync(handle, completion.Task, cancellationToken), completion.Task, cancellationToken);
    }

    /// <summary>
    /// Lists independent, immutable summaries; excludes caller-canceled attempts even before cleanup runs.
    /// Each entry's cancellation check occurs in the mailbox; later cancellation can invalidate an already returned summary.
    /// Returns an empty list after disposal.
    /// </summary>
    public ValueTask<IReadOnlyList<SessionPermissionSnapshot>> ListAsync()
        => ExecuteAsync<IReadOnlyList<SessionPermissionSnapshot>>(
            () => Array.AsReadOnly(_pending.Values
                .Where(entry => !IsCanceled(entry))
                .Select(static entry => entry.Snapshot).ToArray()),
            Array.Empty<SessionPermissionSnapshot>());

    /// <summary>
    /// Lists the sessions that wait for the user: a command to allow or refuse, or a form to fill. Each session
    /// is listed once. Returns an empty list after disposal; a reading that can be stale at once.
    /// </summary>
    public ValueTask<IReadOnlyList<string>> ListWaitingSessionsAsync()
        => ExecuteAsync<IReadOnlyList<string>>(
            () => Array.AsReadOnly(_ownedDeliveries
                .Where(pending => _pending.ContainsKey(pending.Snapshot.Handle) && !IsCanceled(pending))
                .Select(static pending => pending.Snapshot.Handle.SessionId)
                .Concat(_inputs.Values.Where(LiveInput).Select(static pending => pending.Snapshot.Handle.SessionId))
                .Distinct(StringComparer.Ordinal).ToArray()),
            Array.Empty<string>());

    /// <summary>
    /// Lists at most four complete owned plain-command attempts for an exact session under mailbox authority.
    /// Legacy trusted registrations are never included. Cancellation or closure invalidates observations, not accepted decisions.
    /// Returns an empty window after disposal. This does not create runtime state or wait for user decisions.
    /// </summary>
    /// <exception cref="ArgumentException">The session is not a bounded, canonical UTF-16 identity.</exception>
    /// <exception cref="OperationCanceledException">The caller token is canceled at the mailbox query.</exception>
    public ValueTask<SessionOwnedPermissionPage> ListOwnedCommandsAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!ValidOwnedText(sessionId, OwnedIdentityLimit, required: true, identity: true))
            throw new ArgumentException("A bounded canonical session identity is required.", nameof(sessionId));
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = _ownedDeliveries.Where(pending => pending.Snapshot.Handle.SessionId == sessionId
                && _pending.ContainsKey(pending.Snapshot.Handle) && !IsCanceled(pending)).Take(5)
                .Select(pending => new SessionOwnedPermissionSnapshot(
                    new(pending.OwnedExecution!.OperationId, pending.OwnedExecution.RuntimeId,
                        pending.OwnedExecution.AttachmentOrdinal, pending.Snapshot.Handle), pending.Snapshot)).ToArray();
            return new SessionOwnedPermissionPage(Array.AsReadOnly(entries.Take(4).ToArray()), entries.Length > 4);
        }, new SessionOwnedPermissionPage(Array.Empty<SessionOwnedPermissionSnapshot>(), false));
    }

    /// <summary>
    /// Resolves only the exact still-live owned execution/attachment/attempt in the permission mailbox.
    /// Wrong identities, replay, closure and unsupported decisions return false, including legacy TUI handles.
    /// Caller cancellation observed before the mailbox decision throws without resolving; cancellation after acceptance
    /// cannot revoke the decision. An uncertain transport response must not be treated as an uncommitted decision.
    /// </summary>
    /// <exception cref="ArgumentNullException">The handle or its attempt is null.</exception>
    /// <exception cref="OperationCanceledException">Caller cancellation is observed before resolution.</exception>
    public ValueTask<bool> ResolveOwnedCommandAsync(SessionOwnedPermissionHandle handle,
        AgentPermissionDecisionKind decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(handle.Attempt);
        cancellationToken.ThrowIfCancellationRequested();
        if (decision is not (AgentPermissionDecisionKind.AllowOnce or AgentPermissionDecisionKind.Deny or AgentPermissionDecisionKind.Cancel or AgentPermissionDecisionKind.AllowAlways))
            return ValueTask.FromResult(false);
        return ExecuteAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_pending.TryGetValue(handle.Attempt, out var pending) || pending.OwnedExecution is not { } execution
                || execution.OperationId != handle.OperationId || execution.RuntimeId != handle.RuntimeInstanceId
                || execution.AttachmentOrdinal != handle.AttachmentGeneration || !CanUse(execution) || IsCanceled(pending)) return false;
            return Complete(handle.Attempt, decision);
        }, false);
    }

    /// <summary>
    /// Checks the entire scoped handle and caller cancellation in the mailbox; canceled, stale or wrong identities
    /// return false even before cancellation cleanup. This observation does not reserve a later resolution.
    /// </summary>
    /// <exception cref="ArgumentNullException">The handle is null.</exception>
    public ValueTask<bool> IsPendingAsync(SessionPermissionHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return ExecuteAsync(() => _pending.TryGetValue(handle, out var entry) && !IsCanceled(entry), false);
    }

    /// <summary>
    /// Resolves the exact attempt. Invalid decision values, wrong identities and stale/replayed responses return false.
    /// Resolution checks the caller token in the mailbox: cancellation already signaled at that check completes Cancel
    /// and rejects a non-Cancel response regardless of cleanup-message ordering. Cancellation after that check does not
    /// revoke an accepted decision. The mailbox serializes competing responses and entry removal.
    /// Preserves the four existing TUI choices. Opt-in owned command attempts allow only AllowOnce, Deny and Cancel;
    /// this trusted route cannot grant AllowForSession on them. Provider policy amendments remain provider-owned.
    /// </summary>
    /// <exception cref="ArgumentNullException">The handle is null.</exception>
    public ValueTask<bool> ResolveAsync(SessionPermissionHandle handle, AgentPermissionDecisionKind decision)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!Enum.IsDefined(decision))
        {
            return ValueTask.FromResult(false);
        }

        return ExecuteAsync(() => Complete(handle, decision), false);
    }

    /// <summary>Cancels exactly one pending attempt; returns false for stale or mismatched handles.</summary>
    /// <exception cref="ArgumentNullException">The handle is null.</exception>
    public ValueTask<bool> CancelAsync(SessionPermissionHandle handle)
        => ResolveAsync(handle, AgentPermissionDecisionKind.Cancel);

    /// <summary>Cancels current pending attempts for exactly the given session/run, including an explicit null run.</summary>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    public ValueTask<int> CancelRunAsync(string sessionId, string? runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return ExecuteAsync(() =>
        {
            var handles = _pending.Keys.Where(handle => handle.SessionId == sessionId && handle.RunId == runId).ToArray();
            foreach (var handle in handles)
            {
                Complete(handle, AgentPermissionDecisionKind.Cancel);
            }

            return handles.Length;
        }, 0);
    }

    /// <summary>Cancels all pending waits before stopping the mailbox. Concurrent disposals join the same shutdown.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            await _shutdown.Task.ConfigureAwait(false);
            return;
        }

        var requiredCleanupConfirmed = false;
        try
        {
            var ownedCompletion = await _actor.AskAsync(actorCancellationToken =>
            {
                _stopped = true;
                _ownedAdmissionClosed = true;
                var owned = JoinDeliveryOriginalsAsync(_ownedExecutions.Values.ToArray().Select(CloseOwned));
                foreach (var entry in _pending.Values)
                {
                    entry.Completion.TrySetResult(new(AgentPermissionDecisionKind.Cancel));
                }

                _pending.Clear();
                return ValueTask.FromResult(owned);
            }).ConfigureAwait(false);
            Exception? ordinaryFailure = null;
            try { await ownedCompletion.ConfigureAwait(false); }
            catch (Exception failure)
            {
                if (OwnedProviderEventForwarding.HasRetention(failure)) throw;
                ordinaryFailure = failure;
            }
            try { await _actor.StopAsync().ConfigureAwait(false); }
            catch (Exception failure)
            {
                throw new AgentDependencyRetentionException("permission", "mailbox stop",
                    ordinaryFailure is null ? [failure] : [ordinaryFailure, failure], this);
            }
            requiredCleanupConfirmed = true;
            if (ordinaryFailure is not null) ExceptionDispatchInfo.Capture(ordinaryFailure).Throw();
            _shutdown.TrySetResult();
        }
        catch (Exception failure)
        {
            var reported = !requiredCleanupConfirmed && !OwnedProviderEventForwarding.HasRetention(failure)
                ? new AgentDependencyRetentionException("permission", "shutdown prerequisite", [failure], this)
                : failure;
            _shutdown.TrySetException(reported);
            ExceptionDispatchInfo.Capture(reported).Throw();
        }
    }

    private async Task<AgentPermissionDecision> AwaitDecisionAsync(
        SessionPermissionHandle handle, Task<AgentPermissionDecision> completion, CancellationToken cancellationToken)
    {
        try
        {
            return await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelAsync(handle).ConfigureAwait(false);
            return await completion.ConfigureAwait(false);
        }
    }

    private bool Complete(SessionPermissionHandle handle, AgentPermissionDecisionKind decision)
    {
        if (!_pending.TryGetValue(handle, out var entry))
        {
            return false;
        }

        var canceled = IsCanceled(entry);
        // Allowed from now on: only a request that offers it, the load of a skill whose files can be checked.
        if (!canceled && decision == AgentPermissionDecisionKind.AllowAlways && !OffersAllowAlways(entry.Snapshot)) return false;
        if (!canceled && entry.OwnedExecution is not null
            && decision is not (AgentPermissionDecisionKind.AllowOnce or AgentPermissionDecisionKind.Deny or AgentPermissionDecisionKind.Cancel or AgentPermissionDecisionKind.AllowAlways)) return false;
        _pending.Remove(handle);
        entry.Completion.SetResult(new(canceled ? AgentPermissionDecisionKind.Cancel : decision));
        return !canceled || decision == AgentPermissionDecisionKind.Cancel;
    }

    private async ValueTask<T> ExecuteAsync<T>(Func<T> command, T stoppedResult)
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
        {
            return stoppedResult;
        }

        try
        {
            return await _actor.AskAsync(_ => ValueTask.FromResult(command())).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposeStarted) != 0)
        {
            return stoppedResult;
        }
        catch (ChannelClosedException) when (Volatile.Read(ref _disposeStarted) != 0)
        {
            return stoppedResult;
        }
    }

    internal sealed class PendingPermission(
        SessionPermissionSnapshot snapshot, TaskCompletionSource<AgentPermissionDecision> completion, CancellationToken cancellationToken,
        OwnedPermissionExecution? ownedExecution = null)
    {
        internal SessionPermissionSnapshot Snapshot { get; } = snapshot;
        internal TaskCompletionSource<AgentPermissionDecision> Completion { get; } = completion;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal OwnedPermissionExecution? OwnedExecution { get; } = ownedExecution;
        internal Task<AgentPermissionDecision>? Delivery { get; set; }
        internal OwnedDeliveryLifetime? Lifetime { get; set; }
    }

    internal static async Task JoinDeliveryOriginalsAsync(IEnumerable<Task> originals)
    {
        var stages = originals.Select(original => new DeliveryStage(() => original)).ToArray();
        foreach (var stage in stages) stage.Launch();
        var failures = new List<Exception>();
        foreach (var stage in stages)
            if (await stage.Outcome.ConfigureAwait(false) is not null) failures.Add(stage.Failure!);
        if (stages.Any(static stage => stage.Original is null) || failures.Any(OwnedProviderEventForwarding.HasRetention))
            throw new AgentDependencyRetentionException("permission", "closure originals", failures, stages);
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    // Owns actual delivery registrations and their destination, shared only by permission/input delivery.
    internal sealed class OwnedDeliveryLifetime(object dependencies)
    {
        internal CancellationTokenSource? Source { get; private set; }
        internal DeliveryStage? Body { get; private set; }
        internal DeliveryStage? CancelPending { get; private set; }
        internal DeliveryStage? IndexCleanup { get; private set; }
        internal CancellationTokenRegistration[] Registrations { get; private set; } = [];
        internal DeliveryStage[] RegistrationCleanup { get; private set; } = [];
        internal bool SourceReleased { get; private set; }
        internal bool IndexReleased { get; private set; }
        internal Exception? SourceFailure { get; private set; }
        internal async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> body,
            Func<Task> cancelPending, Func<Task> removeIndex, CancellationToken[] tokens)
        {
            Source = new CancellationTokenSource();
            Registrations = new CancellationTokenRegistration[tokens.Length];
            var registered = 0;
            var failures = new List<Exception>();
            var retained = false;
            try
            {
                for (; registered < tokens.Length; registered++)
                    Registrations[registered] = tokens[registered].UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), Source);
                Body = new DeliveryStage(() => body(Source.Token));
                Body.Launch();
                if (await Body.Outcome.ConfigureAwait(false) is not null) failures.Add(Body.Failure!);
            }
            catch (Exception failure) { failures.Add(failure); }
            if (failures.Count != 0) CancelPending = new DeliveryStage(cancelPending);
            RegistrationCleanup = Registrations.Take(registered)
                .Select(registration => new DeliveryStage(() => registration.DisposeAsync().AsTask())).ToArray();
            // Independent cancellation/index controls and every unregister start before a dependent join.
            CancelPending?.Launch();
            foreach (var cleanup in RegistrationCleanup) cleanup.Launch();
            if (CancelPending is not null && await CancelPending.Outcome.ConfigureAwait(false) is not null)
            {
                failures.Add(CancelPending.Failure!);
                retained = true;
            }
            foreach (var cleanup in RegistrationCleanup)
                if (await cleanup.Outcome.ConfigureAwait(false) is not null) { failures.Add(cleanup.Failure!); retained = true; }
            retained |= failures.Any(OwnedProviderEventForwarding.HasRetention);
            if (!retained)
            {
                try { Source.Dispose(); SourceReleased = true; }
                catch (Exception failure) { SourceFailure = failure; failures.Add(failure); retained = true; }
            }
            if (!retained)
            {
                IndexCleanup = new DeliveryStage(removeIndex);
                IndexCleanup.Launch();
                if (await IndexCleanup.Outcome.ConfigureAwait(false) is not null) { failures.Add(IndexCleanup.Failure!); retained = true; }
                else IndexReleased = true;
            }
            if (retained) throw new AgentDependencyRetentionException("permission delivery", "required cleanup", failures, new { Owner = dependencies, Lifetime = this });
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException(failures);
            return ((Task<T>)Body!.Original!).GetAwaiter().GetResult();
        }
    }

    internal sealed class DeliveryStage
    {
        private readonly Func<Task> _invoke;
        private readonly TaskCompletionSource<Task> _launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DeliveryStage(Func<Task> invoke) { _invoke = invoke; Outcome = ObserveAsync(); }
        internal Task? Original { get; private set; }
        internal AggregateException? OriginalFaults { get; private set; }
        internal Exception? Failure { get; private set; }
        internal Task<Exception?> Outcome { get; }
        internal void Launch()
        {
            try { Original = _invoke(); _launched.TrySetResult(Original); }
            catch (Exception failure) { _launched.TrySetException(failure); }
        }
        private async Task<Exception?> ObserveAsync()
        {
            try { await (await _launched.Task.ConfigureAwait(false)).ConfigureAwait(false); return null; }
            catch (Exception failure)
            {
                OriginalFaults = Original?.Exception;
                Failure = OriginalFaults is { InnerExceptions.Count: > 1 } ? OriginalFaults : failure;
                return failure;
            }
        }
    }
}
