using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Jobs;
using CodeAlta.Orchestration.Runtime.Actors;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Owns per-session coordinator sessions, recovers project/global sessions, and projects sanitized runtime events.
/// </summary>
public sealed partial class SessionRuntimeService : IAsyncDisposable
{
    private static readonly Regex ParentNotificationBlockRegex = new(
        @"<notify-parent(?:\s+kind=""(?<kind>[^""]+)"")?\s*>(?<body>.*?)</notify-parent>",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly AgentHub _agentHub;
    private readonly IAgentSessionCatalog _agentSessionCatalog;
    private readonly ProjectCatalog _projectCatalog;
    private readonly SessionViewCatalog _sessionViewCatalog;
    private readonly AgentInstructionTemplateProvider _instructionTemplateProvider;
    private readonly SessionDiscoveryScope? _discoveryScope;
    private readonly CatalogOptions _catalogOptions;
    private readonly CodeAltaConfigStore _configStore;
    private readonly SkillCatalog _skillCatalog;
    private readonly SpaceCatalog _spaceCatalog;
    private readonly SessionRuntimeEventPublisher _events = new();
    private readonly Guid _runtimeInstanceId = Guid.NewGuid();
    private readonly SessionActorRegistry _sessionActors = new(mailboxCapacity: 128);
    private readonly ConcurrentDictionary<string, RuntimeSessionEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly OwnedProviderEventForwarding _forwarding = new();
    private readonly ConcurrentDictionary<string, Task> _transitions = new(StringComparer.OrdinalIgnoreCase);
    // The sessions whose remote control is wanted, with the name it was last turned on with; written under the
    // actor of the session. It outlives the attachment it was turned on in: the one that replaces it turns it on
    // again, unless it was turned off meanwhile.
    private readonly ConcurrentDictionary<string, RemoteControlRequest> _remoteControlWanted = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _identityGate = new();
    private readonly HashSet<string> _newSessionIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>Gets the optional borrowed catalog used for agent-prompt metadata lookup.</summary>
    /// <remarks>Null preserves fresh default-catalog construction at each existing lookup site.
    /// The supplying owner retains the catalog and its locator through runtime cleanup.</remarks>
    internal AgentPromptCatalog? PromptCatalog { get; init; }

    /// <summary>
    /// Gets the home folder whose resources of other tools the sessions read (the skills of <c>~/.agents/skills</c> and
    /// <c>~/.copilot/skills</c>, the agents of <c>~/.copilot/agents</c>); <see langword="null"/> for a host that reads none
    /// of them (<see cref="AgentInstructionTemplateProvider.ReadsUserHome"/>).
    /// </summary>
    public string? UserHomeRoot => _instructionTemplateProvider.UserHomeRoot;

    private Task<T> AdmitAsync<T>(Func<Task<T>> body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _forwarding.RunAsync(body);
    }

    private Task AdmitAsync(Func<Task> body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _forwarding.RunAsync(body);
    }

    private SessionActor GetActorForWork(string sessionId)
    {
        lock (_identityGate)
        {
            if (_sessionActors.TryGet(sessionId, out var existing)) return existing;
            ObjectDisposedException.ThrowIf(_disposed, this);
            var actor = _sessionActors.GetOrCreate(sessionId);
            return actor;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionRuntimeService"/> class.
    /// </summary>
    public SessionRuntimeService(
        AgentHub agentHub,
        IAgentSessionCatalog agentSessionCatalog,
        ProjectCatalog projectCatalog,
        SessionViewCatalog sessionViewCatalog,
        AgentInstructionTemplateProvider instructionTemplateProvider,
        CatalogOptions catalogOptions,
        SkillCatalog? skillCatalog = null)
        : this(agentHub, agentSessionCatalog, projectCatalog, sessionViewCatalog, instructionTemplateProvider, catalogOptions, skillCatalog, false)
    {
    }

    /// <summary>Initializes a runtime with an explicit owned automatic-permission policy.</summary>
    /// <remarks>Automatic approval grants tools the host's privileges; roots are not a sandbox.</remarks>
    /// <exception cref="ArgumentNullException">A required runtime dependency is null.</exception>
    public SessionRuntimeService(
        AgentHub agentHub, IAgentSessionCatalog agentSessionCatalog, ProjectCatalog projectCatalog,
        SessionViewCatalog sessionViewCatalog, AgentInstructionTemplateProvider instructionTemplateProvider,
        CatalogOptions catalogOptions, SkillCatalog? skillCatalog, bool autoApproveOwnedPermissions)
        : this(agentHub, agentSessionCatalog, projectCatalog, sessionViewCatalog, instructionTemplateProvider,
            catalogOptions, skillCatalog, () => autoApproveOwnedPermissions)
    {
    }

    /// <summary>
    /// Initializes a runtime whose owned automatic-permission policy is read again for every request, so a host
    /// whose user turns review on or off applies it to what comes next without being restarted.
    /// </summary>
    /// <remarks>Automatic approval grants tools the host's privileges; roots are not a sandbox.</remarks>
    /// <exception cref="ArgumentNullException">A required runtime dependency is null.</exception>
    public SessionRuntimeService(
        AgentHub agentHub, IAgentSessionCatalog agentSessionCatalog, ProjectCatalog projectCatalog,
        SessionViewCatalog sessionViewCatalog, AgentInstructionTemplateProvider instructionTemplateProvider,
        CatalogOptions catalogOptions, SkillCatalog? skillCatalog, Func<bool> autoApproveOwnedPermissions)
    {
        ArgumentNullException.ThrowIfNull(autoApproveOwnedPermissions);
        ArgumentNullException.ThrowIfNull(agentHub);
        ArgumentNullException.ThrowIfNull(agentSessionCatalog);
        ArgumentNullException.ThrowIfNull(projectCatalog);
        ArgumentNullException.ThrowIfNull(sessionViewCatalog);
        ArgumentNullException.ThrowIfNull(instructionTemplateProvider);
        ArgumentNullException.ThrowIfNull(catalogOptions);

        _agentHub = agentHub;
        _agentSessionCatalog = agentSessionCatalog;
        _projectCatalog = projectCatalog;
        _sessionViewCatalog = sessionViewCatalog;
        _instructionTemplateProvider = instructionTemplateProvider;
        _discoveryScope = instructionTemplateProvider.DiscoveryScope;
        _catalogOptions = catalogOptions;
        _configStore = new CodeAltaConfigStore(catalogOptions);
        _skillCatalog = skillCatalog ?? new SkillCatalog();
        _spaceCatalog = new SpaceCatalog(projectCatalog);
        _autoApproveOwnedPermissions = autoApproveOwnedPermissions;
        Permissions = new SessionPermissionService(autoApproveOwnedPermissions)
        {
            // A request outside a send asks on the attachment of its session, which is live while it runs: a turn the
            // owner did not send (where sessions have a mode of their own), and a command of a session that asks the user.
            SessionReviewTarget = sessionId => _entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated && !entry.Attachment.IsRetiring
                ? new(_runtimeInstanceId, entry.Attachment, entry.ProviderId)
                : null,
        };
        Jobs = new SessionJobService(DeliverJobResultAsync);
        _sessionViewCatalog.JournalStore.StateAppended += OnSessionStateAppended;
        instructionTemplateProvider.SessionToolPatterns = GetDisabledToolPatterns;
        _events.Published = PulseSettlement;
    }

    // A state written to a journal (a prompt queued or sent, a mode, an archive) is in the row of its session: the listing
    // reads that session again, and lists what its journal says.
    private void OnSessionStateAppended(string sessionId)
        => _ = _agentSessionCatalog.InvalidateAsync(sessionId, CancellationToken.None);

    private readonly Func<bool> _autoApproveOwnedPermissions;
    private readonly bool _sessionPermissionModes;
    private readonly bool _fullTrust;

    /// <summary>
    /// Gets or initializes whether the sessions of this runtime are trusted fully, for a host without a user (a headless
    /// run): every permission request is granted, whatever the permission mode of the session, the loads of skills that
    /// otherwise always ask the user included (<see cref="GetPermissionPolicy"/> answers
    /// <see cref="SessionPermissionPolicy.Approve"/>). Default false.
    /// </summary>
    /// <remarks>This grants commands and file changes the privileges of the process, not a sandbox.</remarks>
    public bool FullTrust
    {
        get => _fullTrust;
        init
        {
            _fullTrust = value;
            Permissions.FullTrust = value;
        }
    }

    /// <summary>
    /// Gets or initializes whether the sessions of this runtime run without a user (a headless run). Their instructions
    /// then end with <see cref="HeadlessGuidance"/>, the same text for every session, and none of them reviews its
    /// changes before commit, whatever the setting or its own choice. Default false.
    /// </summary>
    public bool Headless { get; init; }

    /// <summary>
    /// The paragraph that ends the system prompt of a session of a headless runtime (<see cref="Headless"/>). It never
    /// changes, so that the prompt cache of the provider is kept.
    /// </summary>
    public const string HeadlessGuidance =
        "## Headless run\n\n"
        + "No user is available while this run goes on: nobody reads a question, approves a plan or reviews a change before it ends. "
        + "Decide by yourself instead of asking: choose the narrowest safe interpretation of the request, go on, and say in your final answer what you assumed. "
        + "The commands `alta ask`, `alta review`, `alta reminder` and the commands of the windows and of the user interface (`alta ui`, `alta window`) are not available here. "
        + "Your final answer is the result of the run, read by a script or a program: make it complete and self-contained.";

    /// <summary>
    /// Gets or initializes whether the host shows the prompts the model suggests. False, the default: no session
    /// asks its model for suggestions. Where it is true, a session the user writes in asks for them while the
    /// setting <c>[sessions] prompt_suggestions</c> says so.
    /// </summary>
    public bool ShowsPromptSuggestions { get; init; }

    /// <summary>
    /// Gets or initializes the folder of the scratchpad files of the sessions, where the file tools of a session without
    /// the shell may write; null, the default, for the <c>scratchpad</c> folder of the state root. It is the folder the
    /// instructions name (<see cref="AgentInstructionTemplateProvider.ScratchpadRoot"/>).
    /// </summary>
    public string? ScratchpadRoot { get; init; }

    /// <summary>
    /// Gets or initializes what gives a session the identity of its space (its Git author, its GitHub profile), read
    /// when the session attaches and kept while it stays attached. <see cref="SessionIdentityResolver.None"/>, the
    /// default, keeps the usual settings for every session.
    /// </summary>
    public SessionIdentityResolver Identities { get; init; } = SessionIdentityResolver.None;

    /// <summary>
    /// Gets or initializes whether the permission mode of a session decides what the host does with its requests
    /// (<see cref="GetPermissionPolicy"/>). False, the default, leaves every session the one policy of the host.
    /// </summary>
    public bool SessionPermissionModes
    {
        get => _sessionPermissionModes;
        init
        {
            _sessionPermissionModes = value;
            // A turn the owner did not send (a queued prompt of the host, a send of another session, a turn of the
            // provider) then asks on the attachment of its session (Permissions.SessionReviewTarget) when its mode asks.
            Permissions.SessionPolicy = value ? GetPermissionPolicy : null;
        }
    }

    /// <summary>
    /// Gets what the host does with the permission requests of a session: the policy of the mode chosen for the
    /// session, else of the mode its provider is configured with, else the one of the host.
    /// </summary>
    /// <param name="sessionId">The session. One that is not attached has the policy of the host.</param>
    /// <returns>The policy, read now: where sessions have a mode of their own, a send reads it again at each request.</returns>
    public SessionPermissionPolicy GetPermissionPolicy(string sessionId)
    {
        if (_fullTrust) return SessionPermissionPolicy.Approve;
        var reviewByDefault = !_autoApproveOwnedPermissions();
        return _sessionPermissionModes && !string.IsNullOrWhiteSpace(sessionId) && _entries.TryGetValue(sessionId, out var entry)
            ? Runtime.SessionPermissionModes.Policy(entry.PermissionMode ?? ConfiguredPermissionMode(entry.ProviderKey), reviewByDefault)
            : Runtime.SessionPermissionModes.Policy(null, reviewByDefault);
    }

    /// <summary>
    /// Gets the permission mode a session of a provider runs in when none is chosen for it: the mode the provider is
    /// configured with, else the one that names the policy of the host.
    /// </summary>
    /// <param name="providerKey">The key of the provider.</param>
    /// <returns>A permission mode.</returns>
    public string GetDefaultPermissionMode(string? providerKey)
        => ConfiguredPermissionMode(providerKey) ?? Runtime.SessionPermissionModes.Default(!_autoApproveOwnedPermissions());

    /// <summary>
    /// Gets or initializes whether a session that another session creates takes the permission mode of its creator,
    /// read at every creation. Null or false, the default: such a session does not ask.
    /// </summary>
    /// <remarks>Only read where <see cref="SessionPermissionModes"/> is on.</remarks>
    public Func<bool>? InheritPermissionMode { get; init; }

    /// <summary>
    /// Gets the permission mode a session is given when another session creates it. By default it does not ask:
    /// it bypasses permissions, whatever the policy of the host, unless its provider is configured with a mode.
    /// With <see cref="InheritPermissionMode"/> it asks what its creator asks: it takes the very mode of its creator
    /// when its provider offers it (a creator in Claude Code's <c>auto</c> creates a session in <c>auto</c>), else
    /// the mode of the host that has the policy of its creator.
    /// </summary>
    /// <param name="creatorSessionId">The session that creates it, or null when no session does.</param>
    /// <param name="providerKey">The key of the provider of the new session.</param>
    /// <returns>
    /// One of <see cref="Runtime.SessionPermissionModes.HostModes"/> or of the modes its provider offers a session,
    /// or null to give the session no mode of its own: no session creates it, the host has no mode per session, or
    /// it does not ask anyway.
    /// </returns>
    public string? GetCreatedSessionPermissionMode(string? creatorSessionId, string? providerKey)
    {
        if (!_sessionPermissionModes || string.IsNullOrWhiteSpace(creatorSessionId) || !_entries.ContainsKey(creatorSessionId)) return null;
        var reviewByDefault = !_autoApproveOwnedPermissions();
        var configured = ConfiguredPermissionMode(providerKey);
        if (InheritPermissionMode?.Invoke() != true)
            return configured is null && reviewByDefault ? Runtime.SessionPermissionModes.Bypass : null;
        // A mode of the provider that reviews (Claude Code's auto, dontAsk) is kept as it is where the new session's
        // provider has it too: the host reviews it the same way, and the provider decides the rest as for its creator.
        if (_entries.TryGetValue(creatorSessionId, out var creator)
            && NormalizeOptionalText(creator.PermissionMode ?? ConfiguredPermissionMode(creator.ProviderKey)) is { } own
            && !Runtime.SessionPermissionModes.HostModes.Contains(own, StringComparer.Ordinal)
            && !OwnedSessionCommandService.ProviderOnlyPermissionModes.Contains(own)
            && OffersPermissionMode(providerKey, own))
            return own;
        // A mode that asks is saved with the session: it keeps asking when the policy of the host changes.
        return GetPermissionPolicy(creatorSessionId) switch
        {
            SessionPermissionPolicy.Review => Runtime.SessionPermissionModes.Ask,
            SessionPermissionPolicy.AcceptEdits => Runtime.SessionPermissionModes.AcceptEdits,
            _ => Runtime.SessionPermissionModes.Policy(configured, reviewByDefault) == SessionPermissionPolicy.Approve ? null : Runtime.SessionPermissionModes.Bypass,
        };
    }

    /// <summary>
    /// Gets whether a session may hand a prompt to another one. With <see cref="InheritPermissionMode"/>, a
    /// session does not hand work to a session that asks less than it does: what it would have been asked for
    /// would run there without a review.
    /// </summary>
    /// <param name="senderSessionId">The session that sends, or null when no session does.</param>
    /// <param name="target">The session that receives the prompt.</param>
    /// <returns>False when the prompt is refused.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.</exception>
    public bool AcceptsPromptFrom(string? senderSessionId, SessionViewDescriptor target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_sessionPermissionModes || InheritPermissionMode?.Invoke() != true || string.IsNullOrWhiteSpace(senderSessionId)
            || !_entries.ContainsKey(senderSessionId) || string.Equals(senderSessionId, target.SessionId, StringComparison.Ordinal)) return true;
        // A session that is not attached has the mode saved with it.
        var policy = _entries.ContainsKey(target.SessionId)
            ? GetPermissionPolicy(target.SessionId)
            : Runtime.SessionPermissionModes.Policy(NormalizeOptionalText(target.PermissionMode) ?? ConfiguredPermissionMode(target.ResolvedProviderKey), !_autoApproveOwnedPermissions());
        return policy <= GetPermissionPolicy(senderSessionId);
    }

    /// <summary>
    /// Gets whether a session may arrange a prompt whose future session does not retain its permission mode,
    /// such as an automation. With <see cref="InheritPermissionMode"/>, only a known, live session that bypasses
    /// permissions may do so: the future provider and host defaults can change before the prompt runs.
    /// </summary>
    /// <param name="senderSessionId">The session arranging the prompt, or null when no session does.</param>
    /// <returns>
    /// False for a reviewed or unknown session when per-session inheritance is enabled. Hosts without that
    /// policy and callers that belong to no session retain their existing behavior.
    /// </returns>
    /// <remarks>This is prompt admission at the time of the call, not command approval or a durable execution grant.</remarks>
    public bool AcceptsDeferredPromptFrom(string? senderSessionId)
    {
        if (!_sessionPermissionModes || InheritPermissionMode?.Invoke() != true || string.IsNullOrWhiteSpace(senderSessionId)) return true;
        return _entries.TryGetValue(senderSessionId, out var entry) && !entry.IsTerminated && !entry.Attachment.IsRetiring
            && Runtime.SessionPermissionModes.Policy(entry.PermissionMode ?? ConfiguredPermissionMode(entry.ProviderKey), !_autoApproveOwnedPermissions()) == SessionPermissionPolicy.Approve;
    }

    /// <summary>
    /// Gets the permission modes a session of a provider can be given, as the composer of a session offers them: the
    /// modes of the provider but its plan mode, else, where sessions have a mode of their own, the modes of the host
    /// (<see cref="Runtime.SessionPermissionModes.HostModes"/>).
    /// </summary>
    /// <param name="providerKey">The key of the provider.</param>
    /// <returns>The modes; empty when the provider offers none.</returns>
    public IReadOnlyList<string> GetSessionPermissionModes(string? providerKey)
    {
        if (!string.IsNullOrWhiteSpace(providerKey))
        {
            foreach (var provider in _agentHub.SelectionProviders)
            {
                if (!string.Equals(provider.ProviderId.Value, providerKey, StringComparison.OrdinalIgnoreCase)) continue;
                var own = provider.PermissionModes.Where(static mode => !OwnedSessionCommandService.ProviderOnlyPermissionModes.Contains(mode)).ToArray();
                if (own.Length > 0) return own;
                break;
            }
        }

        return _sessionPermissionModes ? Runtime.SessionPermissionModes.HostModes : [];
    }

    /// <summary>
    /// Gets whether a session may create a session in a permission mode it names. With <see cref="InheritPermissionMode"/>,
    /// a session does not create a session that asks less than it does, as it does not hand a prompt to one
    /// (<see cref="AcceptsPromptFrom"/>).
    /// </summary>
    /// <param name="creatorSessionId">The session that creates it, or null when no session does.</param>
    /// <param name="permissionMode">The mode named for the new session.</param>
    /// <returns>False when the mode is refused.</returns>
    /// <exception cref="ArgumentException"><paramref name="permissionMode"/> is empty.</exception>
    public bool MayCreateInPermissionMode(string? creatorSessionId, string permissionMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionMode);
        return MayRunInPermissionMode(creatorSessionId, permissionMode);
    }

    /// <summary>
    /// Gets whether a session may give an existing session a permission mode, as one chosen with a prompt it sends: by
    /// the rule of <see cref="MayCreateInPermissionMode"/>, a session does not give a session a mode that asks less
    /// than it does, itself included.
    /// </summary>
    /// <param name="senderSessionId">The session that gives the mode, or null when no session does.</param>
    /// <param name="providerKey">The key of the provider of the session that is given the mode.</param>
    /// <param name="permissionMode">
    /// The mode, or <see cref="OwnedSessionSelection.ProviderPermissionMode"/> for the one the provider is configured with.
    /// </param>
    /// <returns>False when the mode is refused.</returns>
    /// <exception cref="ArgumentException"><paramref name="permissionMode"/> is empty.</exception>
    public bool MayGivePermissionMode(string? senderSessionId, string? providerKey, string permissionMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionMode);
        return MayRunInPermissionMode(senderSessionId,
            permissionMode == OwnedSessionSelection.ProviderPermissionMode ? ConfiguredPermissionMode(providerKey) : permissionMode);
    }

    // A mode, or null for none, asks at least as much as the session that names it.
    private bool MayRunInPermissionMode(string? sessionId, string? permissionMode)
    {
        if (!_sessionPermissionModes || InheritPermissionMode?.Invoke() != true || string.IsNullOrWhiteSpace(sessionId)) return true;
        // A session that is not attached has no policy to compare with: it names no mode.
        return _entries.ContainsKey(sessionId)
            && Runtime.SessionPermissionModes.Policy(permissionMode, !_autoApproveOwnedPermissions()) <= GetPermissionPolicy(sessionId);
    }

    // Whether a session of a provider can be given a mode of that provider's own.
    private bool OffersPermissionMode(string? providerKey, string permissionMode)
    {
        if (string.IsNullOrWhiteSpace(providerKey)) return false;
        foreach (var provider in _agentHub.SelectionProviders)
        {
            if (string.Equals(provider.ProviderId.Value, providerKey, StringComparison.OrdinalIgnoreCase))
                return provider.PermissionModes.Contains(permissionMode, StringComparer.Ordinal);
        }

        return false;
    }

    private string? ConfiguredPermissionMode(string? providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey)) return null;
        foreach (var provider in _agentHub.SelectionProviders)
        {
            if (string.Equals(provider.ProviderId.Value, providerKey, StringComparison.OrdinalIgnoreCase))
                return NormalizeOptionalText(provider.DefaultPermissionMode);
        }

        return null;
    }

    /// <summary>Gets application-owned pending permissions, independent of attached frontend presentations.</summary>
    public SessionPermissionService Permissions { get; }

    /// <summary>Gets committed bounded live display state, independent of the original lossy event/effects stream.</summary>
    public RuntimeDisplayProjection Display => _events.Display;

    /// <summary>Gets the output the running tool calls have written so far, which no journal keeps.</summary>
    public RuntimeToolOutputProjection ToolOutput => _events.ToolOutput;

    /// <summary>
    /// Gets the skill catalog used when building instructions and activating skills.
    /// </summary>
    public SkillCatalog SkillCatalog => _skillCatalog;

    /// <summary>
    /// Streams sanitized runtime events across all active sessions.
    /// </summary>
    /// <param name="cancellationToken">Cancels admission or channel reads; buffered events may still be yielded after admission.</param>
    /// <returns>The exclusive original-event sequence, not a broadcast or recoverable display snapshot.</returns>
    /// <remarks>
    /// Admission occurs on the first enumeration move, not when obtaining the sequence or enumerator.
    /// A pre-canceled token neither claims admission nor consumes buffered events. Only one enumerator may
    /// be active; use <see cref="Display"/> for independent bounded display observations instead of a competing reader.
    /// Dispose the enumerator on detach. Admission is released only after actual enumeration termination or
    /// disposal; cancellation or runtime completion alone does not release an enumerator suspended at a yield.
    /// After admission, underlying channel cancellation semantics apply: available buffered events may be
    /// yielded despite cancellation. Completion drains accepted events; a successor consumes remaining events,
    /// without replay. Original object references and accepted-event order are preserved by this stream,
    /// but newest publications can still be dropped under pressure. Enumeration is not acknowledgement of
    /// frontend/plugin effects, a history recovery guarantee, or ownership of a run. Detach does not stop the runtime.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Another original-event enumerator owns admission.</exception>
    /// <exception cref="OperationCanceledException">Admission or a channel read is canceled.</exception>
    public IAsyncEnumerable<SessionRuntimeEvent> StreamEventsAsync(CancellationToken cancellationToken = default)
        => _events.ReadAllAsync(cancellationToken);

    // Gives the reader of StreamEventsAsync a marker after every event published before it, and nothing else:
    // false when the stream is closed or full.
    internal bool TryPublishToReader(SessionRuntimeEvent marker) => _events.TryPublishToReader(marker);

    // The same, waiting for room in a full stream so that the marker is never dropped: false when the stream is closed.
    internal ValueTask<bool> PublishToReaderAsync(SessionRuntimeEvent marker, CancellationToken cancellationToken)
        => _events.PublishToReaderAsync(marker, cancellationToken);

    // StreamEventsAsync, each event with the number of events the stream had dropped when it was accepted.
    internal IAsyncEnumerable<BoundedRuntimeEventStream<SessionRuntimeEvent>.StampedEvent> StreamStampedEventsAsync(CancellationToken cancellationToken = default)
        => _events.ReadStampedAsync(cancellationToken);

    /// <summary>
    /// Appends a CodeAlta-authored session event to the session journal and publishes it to runtime subscribers.
    /// </summary>
    /// <param name="session">The session descriptor.</param>
    /// <param name="event">The event to append.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes after the event is appended.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session"/> or <paramref name="event"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the event session id does not match <paramref name="session"/>.</exception>
    public async Task AppendSessionEventAsync(
        SessionViewDescriptor session,
        AgentEvent @event,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(@event);
        var effectWorkingDirectory = session.WorkingDirectory;
        var sessionId = session.SessionId;
        var projectId = session.ProjectRef;
        await AdmitAsync(() => AppendSessionEventOwnedBodyAsync(session, @event, sessionId, projectId, effectWorkingDirectory, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendSessionEventOwnedBodyAsync(SessionViewDescriptor session, AgentEvent @event, string sessionId, string? projectId, string effectWorkingDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(@event);
        using var retentionWork = ClaimRetentionWork(sessionId, session.ParentSessionId, session.CreatedBy?.SourceSessionId);
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = HoldForWrite(sessionId);
        if (!string.Equals(sessionId, @event.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The event session id must match the target session.", nameof(@event));
        }

        await _sessionViewCatalog.JournalStore.EnsureHeaderAsync(session, cancellationToken).ConfigureAwait(false);
        var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
        var publication = new LiveEventPublication(this);
        await publication.CompleteAsync(async mark =>
        {
        await store.AppendEventsAsync(
                session.ProviderId,
                session.ResolvedProviderKey,
                sessionId,
                [@event],
                cancellationToken)
            .ConfigureAwait(false);
        await _agentSessionCatalog.InvalidateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        _events.TryPublish(new SessionAgentEvent(sessionId, @event));
        mark(CapturePluginEvent(@event, sessionId, projectId, effectWorkingDirectory));
        }, static () => { }, ObserveLivePluginEventAsync,
            () => publication.Published is null ? Task.CompletedTask : InvalidateFileSearchCacheAsync(@event, effectWorkingDirectory).AsTask(), _forwarding.RetainDependencies).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the approximate number of runtime events dropped because event consumers fell behind.
    /// </summary>
    public long DroppedRuntimeEventCount => _events.DroppedCount;

    /// <summary>Reads durable notes for a known active or recoverable session, without requiring a frontend view or starting providers.</summary>
    /// <param name="sessionId">An identifier resolved only within the configured backend root.</param>
    /// <param name="cancellationToken">Cancels lookup or reading.</param>
    /// <returns>The exact latest Markdown, or empty when no notes event exists.</returns>
    /// <exception cref="ArgumentException">The identifier is empty.</exception>
    /// <exception cref="SessionNotesSessionNotFoundException">No known session matches the identifier and scope.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <exception cref="IOException">The journal cannot be read.</exception>
    /// <exception cref="System.Text.Json.JsonException">A canonical journal record cannot be decoded.</exception>
    /// <exception cref="ObjectDisposedException">The runtime has been disposed.</exception>
    public async Task<string> GetNotesMarkdownAsync(string sessionId, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => GetNotesMarkdownOwnedBodyAsync(sessionId, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<string> GetNotesMarkdownOwnedBodyAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await ResolveNotesSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var notes = await _sessionViewCatalog.JournalStore.CreateSessionStore()
            .ReadLatestNotesAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        return notes is null ? string.Empty : notes.Markdown;
    }

    /// <summary>Reads known-session notes with lexical containment of the notes-content open.</summary>
    /// <param name="sessionId">A backend-resolved active or recoverable session, not a renderer path or descriptor.</param>
    /// <param name="cancellationToken">Cancels lookup/read or the caller wait; runtime retains admitted work.</param>
    /// <returns>Exact latest Markdown, or empty for no notes, an empty Set or Clear.</returns>
    /// <remarks>No provider activation, writes or events. Scan costs, prior cache probes and reparse/external races
    /// are not bounded or isolated by the content-open containment check.</remarks>
    /// <exception cref="ArgumentException">The identifier is blank.</exception>
    /// <exception cref="SessionNotesSessionNotFoundException">No known session matches the configured scope.</exception>
    /// <exception cref="InvalidOperationException">The resolved journal no longer exists.</exception>
    /// <exception cref="AgentSessionHistoryException">The notes-content path is outside the sessions root.</exception>
    /// <exception cref="IOException">The journal cannot be read or its notes are invalid.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="System.Text.Json.JsonException">A canonical journal record is malformed.</exception>
    /// <exception cref="ObjectDisposedException">Runtime admission has closed.</exception>
    /// <exception cref="OperationCanceledException">The operation or caller wait is canceled.</exception>
    public async Task<string> GetOwnedNotesMarkdownAsync(string sessionId, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => GetOwnedNotesMarkdownBodyAsync(sessionId, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<string> GetOwnedNotesMarkdownBodyAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await ResolveNotesSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var notes = await _sessionViewCatalog.JournalStore.CreateSessionStore()
            .ReadLatestNotesContainedAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        return notes is null ? string.Empty : notes.Markdown;
    }

    /// <summary>Appends session notes and delivers feedback in the existing per-journal write order.</summary>
    /// <param name="sessionId">A known backend session identifier; never a renderer grant or arbitrary path.</param>
    /// <param name="markdown">Exact replacement Markdown.</param>
    /// <param name="kind">Set or cleared. Cleared requires empty Markdown.</param>
    /// <param name="committed">Synchronous, non-reentrant presentation notification after acknowledgment. Must not block on UI work.</param>
    /// <param name="cancellationToken">Cancels before write admission, not an acknowledged commit.</param>
    /// <returns>A task completing after persistence and feedback.</returns>
    /// <exception cref="ArgumentException">The identifier or update is invalid.</exception>
    /// <exception cref="ArgumentNullException">Markdown or feedback is null.</exception>
    /// <exception cref="SessionNotesSessionNotFoundException">No known session matches.</exception>
    /// <exception cref="OperationCanceledException">Canceled before write admission.</exception>
    /// <exception cref="IOException">The write failed; partial I/O is not claimed to be rolled back.</exception>
    /// <exception cref="AgentNotesCommittedException">Persistence succeeded but subsequent feedback failed.</exception>
    /// <exception cref="ObjectDisposedException">The runtime has been disposed.</exception>
    public async Task UpdateNotesAsync(string sessionId, string markdown, AgentNotesUpdateKind kind,
        Action<AgentNotesEvent> committed, CancellationToken cancellationToken = default)
        // The journal owns the cancellation cutoff: after commit, feedback must settle rather
        // than presenting a cancelled waiter as though the write had not happened.
        => await AdmitAsync(() => UpdateNotesOwnedBodyAsync(sessionId, markdown, kind, committed, cancellationToken), cancellationToken).ConfigureAwait(false);

    private async Task UpdateNotesOwnedBodyAsync(string sessionId, string markdown, AgentNotesUpdateKind kind,
        Action<AgentNotesEvent> committed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(committed);
        using var retentionWork = ClaimRetentionWork(sessionId);
        if (kind is not (AgentNotesUpdateKind.Set or AgentNotesUpdateKind.Cleared) ||
            (kind == AgentNotesUpdateKind.Cleared && markdown.Length != 0))
        {
            throw new ArgumentException("Invalid notes update.", nameof(kind));
        }

        var session = await ResolveNotesSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        using var lease = HoldForWrite(session.SessionId);
        var notes = new AgentNotesEvent(session.ProviderId, session.SessionId, DateTimeOffset.UtcNow, null, kind, markdown);
        var publication = new LiveEventPublication(this);
        await publication.CompleteAsync(mark => _sessionViewCatalog.JournalStore.CreateSessionStore().AppendNotesAsync(notes, async () =>
        {
            await _agentSessionCatalog.InvalidateAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);
            _events.TryPublish(new SessionAgentEvent(session.SessionId, notes));
            mark(CapturePluginEvent(notes, session.SessionId, session.ProjectId, session.WorkingDirectory));
            committed(notes);
        }, cancellationToken), static () => { }, ObserveLivePluginEventAsync,
            static () => Task.CompletedTask, _forwarding.RetainDependencies).ConfigureAwait(false);
    }

    private async Task<(string SessionId, ModelProviderId ProviderId, string? ProjectId, string WorkingDirectory)> ResolveNotesSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated)
        {
            return (entry.SessionId, !string.IsNullOrWhiteSpace(entry.ProviderId.Value)
                ? entry.ProviderId
                : new ModelProviderId(entry.ProviderKey), entry.ProjectId, entry.WorkingDirectory);
        }

        var metadata = await _sessionViewCatalog.JournalStore.CreateSessionStore()
            .GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            throw new SessionNotesSessionNotFoundException(sessionId);
        }

        return await ResolveRecoveredPluginEventContextAsync(metadata.SessionId, metadata.ProviderKey,
            metadata.Context?.Cwd, metadata.WorkspacePath, _catalogOptions.GlobalRoot, NormalizePath,
            async () => (await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false))
                .Select(static project => (project.Id, project.ProjectPath))).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets an active session descriptor from the runtime's in-memory coordinator session table.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The active in-memory session descriptor when present; otherwise <see langword="null" />.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sessionId" /> is empty.</exception>
    public async Task<SessionViewDescriptor?> TryGetActiveSessionDescriptorAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
        => await AdmitAsync(() => TryGetActiveSessionDescriptorOwnedBodyAsync(sessionId, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<SessionViewDescriptor?> TryGetActiveSessionDescriptorOwnedBodyAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        cancellationToken.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(sessionId, out var entry) || entry.IsTerminated)
        {
            return null;
        }

        var descriptor = entry.ToDescriptor();
        // The session that runs writes its title: the title it has now, and who chose it, are the ones it saved.
        // A journal that cannot be read leaves the title the session was attached with.
        try
        {
            if ((await _sessionViewCatalog.JournalStore.CreateSessionStore().GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false))?.Details
                is RawApiSessionMetadataDetails { TitleSource: { } titleSource } details && NormalizeOptionalText(details.Title) is { } savedTitle)
            {
                descriptor.Title = savedTitle;
                descriptor.TitleSource = titleSource;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
        }

        var localState = await ReadLatestLocalStateAsync(sessionId, entry.CreatedAt, cancellationToken).ConfigureAwait(false);
        if (localState is not null)
        {
            var activeProviderId = descriptor.ProviderId;
            var activeProviderKey = descriptor.ProviderKey;
            var activeModelId = descriptor.ModelId;
            var activeReasoningEffort = descriptor.ReasoningEffort;
            var activeAgentPromptId = descriptor.AgentPromptId;

            ApplyPersistedSessionLocalState(descriptor, localState);

            descriptor.ProviderId = activeProviderId;
            descriptor.ProviderKey = activeProviderKey;
            if (!string.IsNullOrWhiteSpace(activeModelId))
            {
                descriptor.ModelId = activeModelId;
            }

            if (activeReasoningEffort is not null)
            {
                descriptor.ReasoningEffort = activeReasoningEffort;
            }

            if (!string.IsNullOrWhiteSpace(activeAgentPromptId))
            {
                descriptor.AgentPromptId = activeAgentPromptId;
            }
        }

        return descriptor;
    }

    /// <summary>
    /// Gets the git worktree a session works in, while its folder exists: null for a session that works in the
    /// folder of its project, and for one that is not known.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>The folder of the worktree, or null.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    public async Task<string?> GetSessionWorktreeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        // A session that is attached works where its attachment runs.
        if (_entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated) return ExistingWorktree(entry.WorktreeDirectory);
        var session = await ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return ExistingWorktree(session?.WorktreeDirectory);
    }

    /// <summary>
    /// Reads the references of the tools a session loaded (<see cref="AgentRunTools.KeepLoaded"/>), from the state its
    /// journal keeps: the plugins register those tools again at each run of the session.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The references, in the order the session loaded them; empty for a session that loaded none or is not known.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    public async ValueTask<IReadOnlyList<string>> GetLoadedToolsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var state = await _sessionViewCatalog.JournalStore.CreateSessionStore().GetStateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return state?.LoadedTools ?? [];
    }

    /// <summary>
    /// Reads what a session records of where it belongs: the space its journal names, its project and the session
    /// that created it. The space a session is in follows from the first two (<see cref="SpaceCatalog.ResolveSessionSpace"/>).
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the session records, or <see langword="null"/> when no such session is known.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public async Task<SessionSpaceRecord?> GetSessionSpaceAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        // An attached session has what it was created with: it never changes while it runs.
        if (_entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated)
            return new(entry.SpaceId, entry.ProjectId) { ParentSessionId = NormalizeOptionalText(entry.ParentSessionId) };
        var session = await ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return session is null ? null : new(session.SpaceId, session.ProjectRef) { ParentSessionId = NormalizeOptionalText(session.ParentSessionId) };
    }

    /// <summary>
    /// Gets the identity a session works under: the one its attachment was given, for a session that is attached;
    /// otherwise the one of its space as it is now, which it will be given when it attaches.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identity; the usual one for a session that is not known.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public async Task<SessionIdentity> GetSessionIdentityAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!Identities.IsEnabled) return SessionIdentity.Usual;
        if (_entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated) return entry.Identity;
        return await GetSessionSpaceAsync(sessionId, cancellationToken).ConfigureAwait(false) is { } record
            ? await Identities.ResolveAsync(record.SpaceId, record.ProjectId, cancellationToken).ConfigureAwait(false)
            : SessionIdentity.Usual;
    }

    /// <summary>
    /// Gets the environment the programs a session starts get (a background job, a command): the one its attachment was
    /// given, for a session that is attached; otherwise the environment of its identity with the variables that name the
    /// session and its saved model (<see cref="SessionProcessEnvironment"/>).
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The environment; the one of the usual identity, without variables of a session, for a session that is not known.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public async Task<AgentProcessEnvironment> GetSessionProcessEnvironmentAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated) return entry.ProcessEnvironment;
        var identity = await GetSessionIdentityAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var session = await ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return session is null
            ? identity.Environment
            : SessionProcessEnvironment.WithMarkers(identity.Environment, session.SessionId, session.ResolvedProviderKey, session.ModelId, session.ReasoningEffort);
    }

    internal async Task<SessionViewDescriptor?> ResolveOwnedSessionAsync(string sessionId, CancellationToken cancellationToken)
        => await AdmitAsync(() => ResolveOwnedSessionBodyAsync(sessionId, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    internal Task<bool> RenameOwnedSessionAsync(string sessionId, string? projectId, string workspacePath, string title)
        => AdmitAsync(() => GetActorForWork(sessionId).QueryAsync(async token =>
        {
            var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
            var metadata = await store.GetSessionAsync(sessionId, token).ConfigureAwait(false);
            if (metadata is null) return false;
            var projects = await _projectCatalog.LoadAsync(token).ConfigureAwait(false);
            var session = TryCreateRecoverableSession(metadata, projects);
            if (session is null || session.Kind != (projectId is null ? SessionViewKind.GlobalSession : SessionViewKind.ProjectSession)
                || session.ProjectRef != projectId || session.WorkingDirectory != workspacePath) return false;
            using var lease = HoldForWrite(sessionId);
            if (!await RenameInActorAsync(store, sessionId, title, token).ConfigureAwait(false)) return false;
            await _agentSessionCatalog.NotifySessionUpdatedAsync(sessionId, token).ConfigureAwait(false);
            return true;
        }).AsTask(), CancellationToken.None);

    // Runs on the actor of the session. The title is one somebody chose from now on: no turn replaces it.
    private async Task<bool> RenameInActorAsync(IAgentSessionJournalStore store, string sessionId, string title, CancellationToken token)
    {
        var summary = await store.GetSessionSummaryAsync(sessionId, token).ConfigureAwait(false);
        if (summary is null) return false;
        var attached = _entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated;
        // A live session owns its summary snapshots: update it too, or its next write would undo the rename.
        if (attached && !await _agentHub.RenameSessionAsync(entry!.SessionHandleId, title, token).ConfigureAwait(false)) return false;
        if (!attached)
            await store.UpsertSessionAsync(summary with { Title = title, TitleSource = AgentSessionTitleSource.User }, token).ConfigureAwait(false);
        if (attached) entry!.Title = title;
        return true;
    }

    /// <summary>
    /// Says why a title cannot be given to a session: it is at most 256 characters, without spaces around it,
    /// without control characters, and made of valid Unicode.
    /// </summary>
    /// <param name="title">The title.</param>
    /// <returns>What is wrong with the title, or <see langword="null" /> for one that can be given.</returns>
    public static string? SessionTitleError(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "A title is not empty.";
        if (title.Length > 256 || title != title.Trim() || title.Any(char.IsControl))
            return "A title must be unpadded, free of control characters, and at most 256 characters.";
        for (var index = 0; index < title.Length; index++)
        {
            if (!char.IsSurrogate(title[index])) continue;
            if (!char.IsHighSurrogate(title[index]) || ++index == title.Length || !char.IsLowSurrogate(title[index]))
                return "A title must contain only valid Unicode scalars.";
        }

        return null;
    }

    /// <summary>Reads the title of a stored session, who chose it, and how the session is titled.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The title of the session, or <see langword="null" /> for a session that is not known.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId" /> is blank.</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closing.</exception>
    public async Task<SessionTitleState?> GetSessionTitleAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return await AdmitAsync(() => GetActorForWork(sessionId).QueryAsync(token => ReadTitleStateAsync(sessionId, token), CancellationToken.None).AsTask(), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Names a session, or changes how it is titled, also while it runs. It changes that session only, never the
    /// setting.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="title">The title somebody chose for the session; <see langword="null" /> to leave its title.</param>
    /// <param name="changeMode">Whether <paramref name="mode" /> is set.</param>
    /// <param name="mode">How the session titles itself; <see langword="null" /> to follow the setting again.</param>
    /// <param name="cancellationToken">Stops waiting; what was started is completed.</param>
    /// <returns>The title of the session after the change, or <see langword="null" /> for a session that is not known.</returns>
    /// <remarks>
    /// A mode that is set without a title hands the title back to CodeAlta: it follows the mode from then on. With a
    /// title, the title is the one somebody chose, and only the mode is recorded.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="sessionId" /> is blank, or <paramref name="title" /> cannot be given (<see cref="SessionTitleError" />).</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closing.</exception>
    public async Task<SessionTitleState?> SetSessionTitleAsync(string sessionId, string? title, bool changeMode, AgentSessionTitleMode? mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (title is not null && SessionTitleError(title) is { } error) throw new ArgumentException(error, nameof(title));
        return await AdmitAsync(async () =>
        {
            while (true)
            {
                ObjectDisposedException.ThrowIf(_forwarding.IsClosed, this);
                var (transition, state) = await GetActorForWork(sessionId).QueryAsync(async token =>
                {
                    // An attachment that is being made reads what is saved: it is waited for, then told.
                    if (_transitions.TryGetValue(sessionId, out var pending)) return ((Task?)pending, (SessionTitleState?)null);
                    var session = await ResolveOwnedSessionBodyAsync(sessionId, token).ConfigureAwait(false);
                    if (session is null) return (null, null);
                    using var lease = HoldForWrite(sessionId);
                    var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
                    if (_entries.TryGetValue(sessionId, out var entry) && (entry.IsTerminated || entry.Attachment.IsRetiring)) entry = null;
                    if (changeMode)
                    {
                        var name = mode is { } own ? AgentSessionTitles.NameOf(own) : null;
                        var localState = await ReadLatestLocalStateAsync(sessionId, session.CreatedAt, token).ConfigureAwait(false);
                        if (AgentSessionTitles.ParseMode(localState?.TitleMode) != mode)
                        {
                            if (localState is null)
                            {
                                localState = new SessionViewLocalState();
                                CopySessionMetadata(session, localState);
                            }

                            localState.TitleMode = name;
                            await _sessionViewCatalog.JournalStore.AppendStateAsync(session, localState, token).ConfigureAwait(false);
                        }

                        var effective = mode ?? _configStore.LoadGlobalSessionTitleMode();
                        var release = title is null;
                        if (entry is not null)
                        {
                            // The session that runs is the one that writes its title: it is told, and goes on.
                            await _agentHub.SetTitleModeAsync(entry.SessionHandleId, effective, release, token).ConfigureAwait(false);
                            entry.OwnTitleMode = mode;
                            entry.TitleMode = effective;
                        }
                        else if (release && await store.GetSessionSummaryAsync(sessionId, token).ConfigureAwait(false) is { } summary)
                        {
                            await store.UpsertSessionAsync(summary with
                            {
                                Title = (effective is AgentSessionTitleMode.LastMessage ? SummaryTitle(summary.Summary) : null) ?? summary.Title,
                                TitleSource = AgentSessionTitleSource.Auto,
                            }, token).ConfigureAwait(false);
                        }
                    }

                    if (title is not null && !await RenameInActorAsync(store, sessionId, title, token).ConfigureAwait(false)) return (null, null);
                    await _agentSessionCatalog.NotifySessionUpdatedAsync(sessionId, token).ConfigureAwait(false);
                    var changed = await ReadTitleStateAsync(sessionId, token).ConfigureAwait(false);
                    if (entry is not null && changed is not null) entry.Title = changed.Title;
                    return (null, changed);
                }, CancellationToken.None).ConfigureAwait(false);
                if (transition is null) return state;
                // Its failure belongs to the one that attaches.
                await transition.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).ConfigureAwait(false);
            }
        }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Runs on the actor of the session.
    private async ValueTask<SessionTitleState?> ReadTitleStateAsync(string sessionId, CancellationToken token)
    {
        var metadata = await _sessionViewCatalog.JournalStore.CreateSessionStore().GetSessionAsync(sessionId, token).ConfigureAwait(false);
        if (metadata is null) return null;
        var projects = await _projectCatalog.LoadAsync(token).ConfigureAwait(false);
        var session = TryCreateRecoverableSession(metadata, projects);
        if (session is null) return null;
        var own = AgentSessionTitles.ParseMode((await ReadLatestLocalStateAsync(sessionId, session.CreatedAt, token).ConfigureAwait(false))?.TitleMode);
        return new SessionTitleState(sessionId, ListedTitle(metadata, session.Kind, ProjectOf(session, projects)),
            (metadata.Details as RawApiSessionMetadataDetails)?.TitleSource, own ?? _configStore.LoadGlobalSessionTitleMode(), own is not null);
    }

    /// <summary>Reads how a session nobody named is titled, as the settings say (<c>[sessions] title</c>).</summary>
    /// <returns>The mode of the sessions that have none of their own.</returns>
    public AgentSessionTitleMode GetSessionTitleMode() => _configStore.LoadGlobalSessionTitleMode();

    /// <summary>
    /// Saves how a session nobody named is titled, in the settings. A session that follows the setting takes the mode
    /// with its next prompt.
    /// </summary>
    /// <param name="mode">The mode.</param>
    public void SaveSessionTitleMode(AgentSessionTitleMode mode) => _configStore.SaveGlobalSessionTitleMode(mode);

    private async Task<SessionViewDescriptor?> ResolveOwnedSessionBodyAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var metadata = await _sessionViewCatalog.JournalStore.CreateSessionStore()
            .GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return null;
        }

        var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var session = TryCreateRecoverableSession(metadata, projects);
        if (session is not null)
        {
            if (metadata.ViewState is not null)
            {
                ApplyCachedSessionLocalState(session, metadata.ViewState);
            }
            else
            {
                await ApplyPersistedSessionLocalStateAsync(session, cancellationToken).ConfigureAwait(false);
            }
        }
        return session;
    }

    /// <summary>
    /// Lists recoverable user-facing sessions from the session catalog.
    /// </summary>
    public IAsyncEnumerable<SessionViewDescriptor> ListRecoverableSessionsAsync(CancellationToken cancellationToken = default)
        => ListRecoverableSessionsAsync(shouldListProviderSessions: null, cancellationToken);

    /// <summary>
    /// Reconciles the recoverable-session cache with external journal additions, changes, and deletions.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true" /> when cached visible session metadata changed.</returns>
    public async Task<bool> ReconcileRecoverableSessionCacheAsync(CancellationToken cancellationToken = default)
        => await AdmitAsync(() => ReconcileRecoverableSessionCacheOwnedBodyAsync(cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<bool> ReconcileRecoverableSessionCacheOwnedBodyAsync(CancellationToken cancellationToken)
    {
        var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
        var result = await store.ReconcileCacheAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Changed)
        {
            return false;
        }

        await _agentSessionCatalog.InvalidateAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Lists recoverable user-facing sessions from the session catalog.
    /// </summary>
    /// <param name="shouldListProviderSessions">Optional predicate that returns whether a provider's sessions should be listed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recoverable user-facing sessions.</returns>
    public async IAsyncEnumerable<SessionViewDescriptor> ListRecoverableSessionsAsync(
        Func<ModelProviderId, bool>? shouldListProviderSessions,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        // The prompts of a folder are read once for the listing, not once for each of its sessions.
        var knownPrompts = new KnownPromptNames(this);
        await foreach (var metadata in _agentSessionCatalog.ListSessionsAsync(filter: null, cancellationToken).ConfigureAwait(false))
        {
            var session = TryCreateRecoverableSession(metadata, projects, knownPrompts);
            if (session is null)
            {
                continue;
            }

            if (shouldListProviderSessions is not null &&
                !shouldListProviderSessions(new ModelProviderId(session.ResolvedProviderKey)))
            {
                continue;
            }

            if (metadata.ViewState is not null)
            {
                ApplyCachedSessionLocalState(session, metadata.ViewState, knownPrompts);
            }
            else
            {
                await ApplyPersistedSessionLocalStateAsync(session, cancellationToken, knownPrompts).ConfigureAwait(false);
            }

            yield return session;
        }
    }

    private async Task ApplyPersistedSessionLocalStateAsync(
        IReadOnlyList<SessionViewDescriptor> sessions,
        CancellationToken cancellationToken)
    {
        foreach (var session in sessions)
        {
            await ApplyPersistedSessionLocalStateAsync(session, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyPersistedSessionLocalStateAsync(
        SessionViewDescriptor session,
        CancellationToken cancellationToken,
        KnownPromptNames? knownPrompts = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionViewLocalState? localState;
        try
        {
            localState = await _sessionViewCatalog.JournalStore
                .ReadLatestStateAsync(session.SessionId, session.CreatedAt, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return;
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        if (localState is null)
        {
            return;
        }

        ApplyPersistedSessionLocalState(session, localState, knownPrompts);
    }

    private void ApplyPersistedSessionLocalState(SessionViewDescriptor session, SessionViewLocalState localState, KnownPromptNames? knownPrompts = null)
    {
        if (localState.Archived)
        {
            session.Status = SessionViewStatus.Archived;
        }

        if (!string.IsNullOrWhiteSpace(localState.ProviderKey))
        {
            var providerKey = localState.ProviderKey.Trim();
            session.ProviderKey = providerKey;
            session.ProviderId = providerKey;
        }

        if (!string.IsNullOrWhiteSpace(localState.ModelId))
        {
            session.ModelId = localState.ModelId;
        }

        if (localState.ReasoningEffort is { } reasoningEffort)
        {
            session.ReasoningEffort = reasoningEffort;
        }

        // Only the local state says it, and none is a choice too: the mode of the provider.
        session.PermissionMode = NormalizeOptionalText(localState.PermissionMode);
        session.Sandbox = NormalizeOptionalText(localState.Sandbox);
        session.TitleMode = AgentSessionTitles.ParseMode(localState.TitleMode) is { } titleMode ? AgentSessionTitles.NameOf(titleMode) : null;

        if (!string.IsNullOrWhiteSpace(localState.AgentPromptId))
        {
            session.AgentPromptId = ResolveKnownAgentPromptId(localState.AgentPromptId, session.WorkingDirectory, knownPrompts);
        }

        if (localState.MessageCount is { } messageCount)
        {
            session.MessageCount = messageCount;
        }

        var parentSessionId = ResolveParentSessionId(localState.ParentSessionId, localState.CreatedBy?.SourceSessionId, session.ForkedFrom is not null);
        if (!string.IsNullOrWhiteSpace(parentSessionId))
        {
            session.ParentSessionId = parentSessionId;
        }

        if (localState.CreatedBy is not null)
        {
            session.CreatedBy = localState.CreatedBy;
        }
    }

    private void ApplyCachedSessionLocalState(SessionViewDescriptor session, AgentSessionViewStateMetadata localState, KnownPromptNames? knownPrompts = null)
    {
        var viewState = new SessionViewLocalState
        {
            ProviderKey = localState.ProviderKey,
            ModelId = localState.ModelId,
            ReasoningEffort = localState.ReasoningEffort,
            PermissionMode = localState.PermissionMode,
            Sandbox = localState.Sandbox,
            TitleMode = localState.TitleMode is { } titleMode ? AgentSessionTitles.NameOf(titleMode) : null,
            AgentPromptId = localState.AgentPromptId,
            Archived = localState.Archived,
            MessageCount = localState.MessageCount,
            ParentSessionId = localState.ParentSessionId,
            CreatedBy = DeserializeCachedCreatedBy(localState.CreatedByJson),
        };
        ApplyPersistedSessionLocalState(session, viewState, knownPrompts);
        session.CachedViewState = localState;
    }

    private static AltaActorProvenance? DeserializeCachedCreatedBy(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(
                json,
                SessionViewJournalJsonSerializerContext.Default.AltaActorProvenance);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes a session from the session catalog when present and persists local hidden-session metadata otherwise.
    /// </summary>
    /// <param name="session">The session view to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the session existed and was deleted; otherwise <see langword="false"/>.</returns>
    public async Task<bool> DeleteSessionAsync(SessionViewDescriptor session, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => DeleteSessionOwnedBodyAsync(session, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<bool> DeleteSessionOwnedBodyAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var deleted = false;
        using var lease = HoldForWrite(session.SessionId);
        if (!string.IsNullOrWhiteSpace(session.SessionId))
        {
            deleted = await _agentSessionCatalog.DeleteSessionAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
            if (deleted) NotifySessionDeleted(session.SessionId);
        }

        session.Status = SessionViewStatus.Archived;
        if (!deleted)
        {
            await HideSessionLocalStateAsync(session, cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }

    /// <summary>
    /// Persists machine-local session metadata for a recoverable session.
    /// </summary>
    /// <param name="session">The session whose local state should be updated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PersistSessionLocalStateAsync(SessionViewDescriptor session, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => PersistSessionLocalStateOwnedBodyAsync(session, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task PersistSessionLocalStateOwnedBodyAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var retentionWork = ClaimRetentionWork(session.SessionId, session.ParentSessionId, session.CreatedBy?.SourceSessionId);
        using var lease = HoldForWrite(session.SessionId);
        if (!string.IsNullOrWhiteSpace(session.SessionId))
        {
            await _agentSessionCatalog.NotifySessionUpdatedAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        }

        await UpdateSessionLocalStateAsync(session, cancellationToken).ConfigureAwait(false);
        PublishSessionAgentConfigurationEvent(session);
    }

    /// <summary>
    /// Records a pending agent prompt selection for an active coordinator session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="agentPromptId">The selected agent prompt identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sessionId" /> or <paramref name="agentPromptId" /> is empty.</exception>
    public async Task SetActiveSessionAgentPromptIdAsync(
        string sessionId,
        string agentPromptId,
        CancellationToken cancellationToken = default)
        => await AdmitAsync(() => SetActiveSessionAgentPromptIdOwnedBodyAsync(sessionId, agentPromptId, CancellationToken.None), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task SetActiveSessionAgentPromptIdOwnedBodyAsync(string sessionId, string agentPromptId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentPromptId);
        if (!_sessionActors.TryGet(sessionId, out var actor))
        {
            return;
        }

        await actor.QueryAsync(
                actorCancellationToken =>
                {
                    actorCancellationToken.ThrowIfCancellationRequested();
                    if (_entries.TryGetValue(sessionId, out var entry) && (!entry.IsTerminated || _transitions.ContainsKey(sessionId)))
                    {
                        entry.PendingAgentPromptId = agentPromptId.Trim();
                    }

                    return ValueTask.FromResult(true);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Records the model and the reasoning effort an attached session takes at its next turn, as
    /// <see cref="SetActiveSessionAgentPromptIdAsync"/> records an agent prompt: the drain of its queue attaches it
    /// again with them, and a send that runs on its attachment drops them, having chosen its own. A session that is not
    /// attached takes the model saved with it at its next attachment: nothing is recorded for it.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="modelId">The model, one its provider offers.</param>
    /// <param name="reasoningEffort">The effort, or null for none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sessionId" /> or <paramref name="modelId" /> is empty.</exception>
    public async Task SetActiveSessionModelAsync(
        string sessionId,
        string modelId,
        AgentReasoningEffort? reasoningEffort,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        await AdmitAsync(() => SetActiveSessionModelOwnedBodyAsync(sessionId, modelId.Trim(), reasoningEffort, CancellationToken.None), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SetActiveSessionModelOwnedBodyAsync(string sessionId, string modelId, AgentReasoningEffort? reasoningEffort, CancellationToken cancellationToken)
    {
        if (!_sessionActors.TryGet(sessionId, out var actor))
        {
            return;
        }

        await actor.QueryAsync(
                actorCancellationToken =>
                {
                    actorCancellationToken.ThrowIfCancellationRequested();
                    if (_entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated)
                    {
                        // The model the attachment already runs with is nothing to wait for.
                        entry.PendingModel = string.Equals(entry.Model, modelId, StringComparison.Ordinal) && entry.ReasoningEffort == reasoningEffort
                            ? null
                            : new PendingModelChoice(modelId, reasoningEffort);
                    }

                    return ValueTask.FromResult(true);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads persisted CodeAlta-owned agent-runtime history for a recoverable session without resuming the session.
    /// </summary>
    /// <param name="session">The session descriptor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored events when available; otherwise <see langword="null" />.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session" /> is <see langword="null" />.</exception>
    public async Task<IReadOnlyList<AgentEvent>?> TryReadStoredHistoryAsync(
        SessionViewDescriptor session,
        CancellationToken cancellationToken = default)
        => await TryReadStoredHistoryAsync(session, onUnavailable: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reads persisted CodeAlta-owned agent-runtime history for a recoverable session without resuming the session.
    /// </summary>
    /// <param name="session">The session descriptor.</param>
    /// <param name="onUnavailable">Optional callback invoked when a local history file exists but cannot be read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored events when available; otherwise <see langword="null" />.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session" /> is <see langword="null" />.</exception>
    public async Task<IReadOnlyList<AgentEvent>?> TryReadStoredHistoryAsync(
        SessionViewDescriptor session,
        Action<Exception>? onUnavailable,
        CancellationToken cancellationToken = default)
        => await AdmitAsync(() => TryReadStoredHistoryOwnedBodyAsync(session, onUnavailable, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<AgentEvent>?> TryReadStoredHistoryOwnedBodyAsync(SessionViewDescriptor session,
        Action<Exception>? onUnavailable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.SessionId))
        {
            return null;
        }

        try
        {
            var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
            return await store.ReadEventsAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            onUnavailable?.Invoke(ex);
            return null;
        }
    }

    /// <summary>
    /// Creates a new global session and returns its descriptor.
    /// </summary>
    /// <exception cref="ArgumentException">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    public async Task<SessionViewDescriptor> CreateGlobalSessionAsync(
        SessionExecutionOptions options,
        string? title,
        CancellationToken cancellationToken = default)
        => await CreateGlobalSessionAsync(options, title, parentSessionId: null, createdBy: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a new global session with optional durable lineage and returns its descriptor.
    /// </summary>
    /// <exception cref="ArgumentException">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    public async Task<SessionViewDescriptor> CreateGlobalSessionAsync(
        SessionExecutionOptions options,
        string? title,
        string? parentSessionId,
        AltaActorProvenance? createdBy,
        CancellationToken cancellationToken = default)
        => await CreateGlobalSessionAsync(options, title, parentSessionId, createdBy, spaceId: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a new global session (a chat) in a space, with optional durable lineage, and returns its descriptor.
    /// </summary>
    /// <param name="options">How the session runs.</param>
    /// <param name="title">The title, or null for the usual one.</param>
    /// <param name="parentSessionId">The session that this one continues the work of, if any.</param>
    /// <param name="createdBy">Who creates the session when it is not the user.</param>
    /// <param name="spaceId">
    /// The space the session belongs to for its whole life, recorded in its journal; null records none, and the
    /// session is then read as in the default space.
    /// </param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The descriptor of the session.</returns>
    /// <exception cref="ArgumentException">A supplied scoped working or project path is invalid or outside the instruction boundary, or the space is not a valid identifier.</exception>
    public async Task<SessionViewDescriptor> CreateGlobalSessionAsync(
        SessionExecutionOptions options,
        string? title,
        string? parentSessionId,
        AltaActorProvenance? createdBy,
        string? spaceId,
        CancellationToken cancellationToken = default)
        => await CreateGlobalSessionAsync(options, title, parentSessionId, createdBy, spaceId, sessionId: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a new global session (a chat) as <see cref="CreateGlobalSessionAsync(SessionExecutionOptions, string?, string?, AltaActorProvenance?, string?, CancellationToken)"/>
    /// does, with an identifier the caller chooses.
    /// </summary>
    /// <param name="options">How the session runs.</param>
    /// <param name="title">The title, or null for the usual one.</param>
    /// <param name="parentSessionId">The session that this one continues the work of, if any.</param>
    /// <param name="createdBy">Who creates the session when it is not the user.</param>
    /// <param name="spaceId">The space the session belongs to for its whole life; null records none.</param>
    /// <param name="sessionId">
    /// The identifier of the new session, a GUID written with hyphens (<see cref="TryNormalizeSessionId"/>); null for one
    /// CodeAlta generates.
    /// </param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The descriptor of the session.</returns>
    /// <exception cref="ArgumentException">
    /// A supplied scoped working or project path is invalid or outside the instruction boundary, the space is not a valid
    /// identifier, or the session identifier is not a GUID written with hyphens.
    /// </exception>
    /// <exception cref="SessionIdConflictException">A session with that identifier exists or is being created.</exception>
    public async Task<SessionViewDescriptor> CreateGlobalSessionAsync(
        SessionExecutionOptions options,
        string? title,
        string? parentSessionId,
        AltaActorProvenance? createdBy,
        string? spaceId,
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        var space = NormalizeSpaceId(spaceId);
        var id = NormalizeRequestedSessionId(sessionId);
        return await AdmitAsync(() => CreateGlobalSessionOwnedBodyAsync(options, title, parentSessionId, createdBy, space, id, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Normalizes an identifier a caller chooses for a new session: a GUID written with hyphens
    /// (<c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>), as CodeAlta writes the ones it generates, in lower case.
    /// </summary>
    /// <param name="value">The identifier.</param>
    /// <param name="sessionId">The identifier in lower case, when it is valid.</param>
    /// <returns>Whether the identifier is valid.</returns>
    public static bool TryNormalizeSessionId(string? value, out string sessionId)
    {
        sessionId = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length != 36 || !Guid.TryParseExact(value, "D", out var guid) || guid == Guid.Empty)
        {
            return false;
        }

        sessionId = guid.ToString("D");
        return true;
    }

    private static string? NormalizeRequestedSessionId(string? sessionId)
    {
        if (sessionId is null) return null;
        return TryNormalizeSessionId(sessionId, out var normalized)
            ? normalized
            : throw new ArgumentException($"'{sessionId}' is not a session identifier: a session is named by a GUID written with hyphens.", nameof(sessionId));
    }

    // Takes an identifier a caller chose for a new session, before anything is attached: one the store or this runtime
    // knows is refused. A creation that fails before the session is written gives it back (ReleaseRequestedSessionId).
    private async Task ReserveRequestedSessionIdAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (await _sessionViewCatalog.JournalStore.CreateSessionStore().GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false) is not null)
            throw new SessionIdConflictException(sessionId);
        lock (_identityGate)
        {
            if (_entries.ContainsKey(sessionId) || _transitions.ContainsKey(sessionId) || !_newSessionIds.Add(sessionId))
                throw new SessionIdConflictException(sessionId);
        }
    }

    private void ReleaseRequestedSessionId(string sessionId)
    {
        lock (_identityGate) _newSessionIds.Remove(sessionId);
    }

    // A space given to a new session is an identifier, or none at all.
    private static string? NormalizeSpaceId(string? spaceId)
    {
        if (string.IsNullOrWhiteSpace(spaceId)) return null;
        var id = spaceId.Trim();
        return SpaceDescriptor.IsValidId(id) ? id : throw new ArgumentException($"'{id}' is not the identifier of a space.", nameof(spaceId));
    }

    private async Task<SessionViewDescriptor> CreateGlobalSessionOwnedBodyAsync(SessionExecutionOptions options, string? title,
        string? parentSessionId, AltaActorProvenance? createdBy, string? spaceId, string? requestedSessionId, CancellationToken cancellationToken)
    {
        using var lineage = ClaimRetentionWork(parentSessionId, createdBy?.SourceSessionId);
        ArgumentNullException.ThrowIfNull(options);
        ValidateDiscoveryPaths(null, options);
        if (requestedSessionId is not null) await ReserveRequestedSessionIdAsync(requestedSessionId, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var session = new SessionViewDescriptor
        {
            SessionId = requestedSessionId ?? string.Empty,
            Kind = SessionViewKind.GlobalSession,
            ProviderId = options.ProviderId.Value,
            ProviderKey = options.ProviderKey ?? options.ProviderId.Value,
            SpaceId = spaceId,
            WorkingDirectory = options.WorkingDirectory,
            Title = string.IsNullOrWhiteSpace(title) ? UnnamedGlobalSessionTitle : title.Trim(),
            // A title that is given was chosen; without one, CodeAlta titles the session.
            TitleSource = string.IsNullOrWhiteSpace(title) ? AgentSessionTitleSource.Auto : AgentSessionTitleSource.User,
            Status = SessionViewStatus.Draft,
            ParentSessionId = NormalizeOptionalText(parentSessionId),
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
            LastActiveAt = now,
            LatestSummary = UnnamedGlobalSessionSummary,
            ModelId = options.Model,
            ReasoningEffort = options.ReasoningEffort,
            PermissionMode = NormalizeOptionalText(options.PermissionMode),
            Sandbox = await CreatedSandboxAsync(options.Sandbox, createdBy, cancellationToken).ConfigureAwait(false),
            AgentPromptId = NormalizeOptionalText(options.AgentPromptId),
        };
        ReserveSessionIdentity(session);
        InheritToolPatterns(session.SessionId, session.ParentSessionId, createdBy?.SourceSessionId);

        var failureCapture = new RuntimeFailureCapture();
        try
        {
            await EnsureCoordinatorSessionWithFailureCaptureAsync(session, options, cancellationToken, failureCapture).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (requestedSessionId is not null) ReleaseRequestedSessionId(requestedSessionId);
            await ObserveRuntimeFailureAsync(session, failureCapture.Original ?? ex, ex).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (requestedSessionId is not null)
        {
            ReleaseRequestedSessionId(requestedSessionId);
            throw;
        }

        return session;
    }

    /// <summary>
    /// Creates a new project session and returns its descriptor.
    /// </summary>
    /// <exception cref="ArgumentException">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    public async Task<SessionViewDescriptor> CreateProjectSessionAsync(
        ProjectDescriptor project,
        SessionExecutionOptions options,
        string? title,
        CancellationToken cancellationToken = default)
        => await CreateProjectSessionAsync(project, options, title, parentSessionId: null, createdBy: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a new project session with optional durable lineage and returns its descriptor.
    /// </summary>
    /// <exception cref="ArgumentException">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    public async Task<SessionViewDescriptor> CreateProjectSessionAsync(
        ProjectDescriptor project,
        SessionExecutionOptions options,
        string? title,
        string? parentSessionId,
        AltaActorProvenance? createdBy,
        CancellationToken cancellationToken = default)
        => await CreateProjectSessionAsync(project, options, title, parentSessionId, createdBy, worktreeDirectory: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a new project session that works in a git worktree of its project, or in the folder of its
    /// project, and returns its descriptor.
    /// </summary>
    /// <remarks>
    /// The session belongs to its project whatever folder it works in: its working directory is the folder of
    /// the project, and the worktree is recorded beside it. The worktree is an argument of the creation, never a
    /// property of the options, so that nothing that copies options can lose it.
    /// </remarks>
    /// <param name="project">The project of the session.</param>
    /// <param name="options">How the session runs; its working directory is the folder of the project.</param>
    /// <param name="title">The title, or null for the name of the project.</param>
    /// <param name="parentSessionId">The session that this one continues the work of, if any.</param>
    /// <param name="createdBy">Who creates the session when it is not the user.</param>
    /// <param name="worktreeDirectory">The folder of the project in a git worktree, which exists; null for the folder of the project.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The descriptor of the session.</returns>
    /// <exception cref="ArgumentException">
    /// A supplied scoped working or project path is invalid or outside the instruction boundary, or the worktree is not an absolute folder.
    /// </exception>
    public async Task<SessionViewDescriptor> CreateProjectSessionAsync(
        ProjectDescriptor project,
        SessionExecutionOptions options,
        string? title,
        string? parentSessionId,
        AltaActorProvenance? createdBy,
        string? worktreeDirectory,
        CancellationToken cancellationToken = default)
        => await CreateProjectSessionAsync(project, options, title, parentSessionId, createdBy, worktreeDirectory, spaceId: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a new project session in a space, that works in a git worktree of its project or in the folder of
    /// its project, and returns its descriptor.
    /// </summary>
    /// <param name="project">The project of the session.</param>
    /// <param name="options">How the session runs; its working directory is the folder of the project.</param>
    /// <param name="title">The title, or null for the name of the project.</param>
    /// <param name="parentSessionId">The session that this one continues the work of, if any.</param>
    /// <param name="createdBy">Who creates the session when it is not the user.</param>
    /// <param name="worktreeDirectory">The folder of the project in a git worktree, which exists; null for the folder of the project.</param>
    /// <param name="spaceId">
    /// The space the session belongs to for its whole life, recorded in its journal; null records none, and the
    /// session is then read as in the space of its project when it is in exactly one, else in the default space.
    /// </param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The descriptor of the session.</returns>
    /// <exception cref="ArgumentException">
    /// A supplied scoped working or project path is invalid or outside the instruction boundary, the worktree is not
    /// an absolute folder, or the space is not a valid identifier.
    /// </exception>
    public async Task<SessionViewDescriptor> CreateProjectSessionAsync(
        ProjectDescriptor project,
        SessionExecutionOptions options,
        string? title,
        string? parentSessionId,
        AltaActorProvenance? createdBy,
        string? worktreeDirectory,
        string? spaceId,
        CancellationToken cancellationToken = default)
        => await CreateProjectSessionAsync(project, options, title, parentSessionId, createdBy, worktreeDirectory, spaceId, sessionId: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a new project session as
    /// <see cref="CreateProjectSessionAsync(ProjectDescriptor, SessionExecutionOptions, string?, string?, AltaActorProvenance?, string?, string?, CancellationToken)"/>
    /// does, with an identifier the caller chooses.
    /// </summary>
    /// <param name="project">The project of the session.</param>
    /// <param name="options">How the session runs; its working directory is the folder of the project.</param>
    /// <param name="title">The title, or null for the name of the project.</param>
    /// <param name="parentSessionId">The session that this one continues the work of, if any.</param>
    /// <param name="createdBy">Who creates the session when it is not the user.</param>
    /// <param name="worktreeDirectory">The folder of the project in a git worktree, which exists; null for the folder of the project.</param>
    /// <param name="spaceId">The space the session belongs to for its whole life; null records none.</param>
    /// <param name="sessionId">
    /// The identifier of the new session, a GUID written with hyphens (<see cref="TryNormalizeSessionId"/>); null for one
    /// CodeAlta generates.
    /// </param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The descriptor of the session.</returns>
    /// <exception cref="ArgumentException">
    /// A supplied scoped working or project path is invalid or outside the instruction boundary, the worktree is not
    /// an absolute folder, the space is not a valid identifier, or the session identifier is not a GUID written with hyphens.
    /// </exception>
    /// <exception cref="SessionIdConflictException">A session with that identifier exists or is being created.</exception>
    public async Task<SessionViewDescriptor> CreateProjectSessionAsync(
        ProjectDescriptor project,
        SessionExecutionOptions options,
        string? title,
        string? parentSessionId,
        AltaActorProvenance? createdBy,
        string? worktreeDirectory,
        string? spaceId,
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        if (worktreeDirectory is not null && !Path.IsPathFullyQualified(worktreeDirectory))
            throw new ArgumentException("A worktree is an absolute folder.", nameof(worktreeDirectory));
        var space = NormalizeSpaceId(spaceId);
        var id = NormalizeRequestedSessionId(sessionId);
        return await AdmitAsync(() => CreateProjectSessionOwnedBodyAsync(project, options, title, parentSessionId, createdBy, worktreeDirectory, space, id, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SessionViewDescriptor> CreateProjectSessionOwnedBodyAsync(ProjectDescriptor project, SessionExecutionOptions options,
        string? title, string? parentSessionId, AltaActorProvenance? createdBy, string? worktreeDirectory, string? spaceId, string? requestedSessionId,
        CancellationToken cancellationToken)
    {
        using var lineage = ClaimRetentionWork(parentSessionId, createdBy?.SourceSessionId);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(options);

        using var projectCreation = ClaimProjectCreation(project);

        ValidateDiscoveryPaths(null, options, project);

        var requestedProjectId = project.Id;
        var previousProject = await _projectCatalog.GetByPathAsync(project.ProjectPath, cancellationToken).ConfigureAwait(false);
        var restoredArchivedProject = previousProject?.Archived == true;
        project = await _projectCatalog.EnsurePersistedAsync(project, cancellationToken).ConfigureAwait(false);
        var persistedNewProject = previousProject is null &&
            string.Equals(project.Id, requestedProjectId, StringComparison.OrdinalIgnoreCase);
        if (requestedSessionId is not null)
        {
            try
            {
                await ReserveRequestedSessionIdAsync(requestedSessionId, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await RollBackProjectPersistenceAsync(project, previousProject, persistedNewProject, restoredArchivedProject).ConfigureAwait(false);
                throw;
            }
        }

        var now = DateTimeOffset.UtcNow;
        var session = new SessionViewDescriptor
        {
            SessionId = requestedSessionId ?? string.Empty,
            Kind = SessionViewKind.ProjectSession,
            ProviderId = options.ProviderId.Value,
            ProviderKey = options.ProviderKey ?? options.ProviderId.Value,
            ProjectRef = project.Id,
            SpaceId = spaceId,
            WorkingDirectory = options.WorkingDirectory,
            WorktreeDirectory = NormalizeOptionalText(worktreeDirectory),
            Title = string.IsNullOrWhiteSpace(title) ? project.DisplayName : title.Trim(),
            // A title that is given was chosen; without one, CodeAlta titles the session.
            TitleSource = string.IsNullOrWhiteSpace(title) ? AgentSessionTitleSource.Auto : AgentSessionTitleSource.User,
            Status = SessionViewStatus.Draft,
            ParentSessionId = NormalizeOptionalText(parentSessionId),
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
            LastActiveAt = now,
            LatestSummary = UnnamedProjectSessionSummary(project),
            ModelId = options.Model,
            ReasoningEffort = options.ReasoningEffort,
            PermissionMode = NormalizeOptionalText(options.PermissionMode),
            Sandbox = await CreatedSandboxAsync(options.Sandbox, createdBy, cancellationToken).ConfigureAwait(false),
            AgentPromptId = NormalizeOptionalText(options.AgentPromptId),
            // A child reviews before commit when its parent does: its changes reach the user through it.
            ReviewBeforeCommit = NormalizeOptionalText(parentSessionId) is { } parent && GetEffectiveReviewBeforeCommit(parent) ? true : null,
        };
        ReserveSessionIdentity(session);
        InheritToolPatterns(session.SessionId, session.ParentSessionId, createdBy?.SourceSessionId);

        var failureCapture = new RuntimeFailureCapture();
        try
        {
            await EnsureCoordinatorSessionWithFailureCaptureAsync(session, options, cancellationToken, failureCapture).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Exception escaping = ex;
            if (requestedSessionId is not null) ReleaseRequestedSessionId(requestedSessionId);
            try
            {
                await RollBackProjectPersistenceAsync(project, previousProject, persistedNewProject, restoredArchivedProject).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                escaping = new AggregateException(ex, rollbackException);
            }

            if (ex is not OperationCanceledException)
            {
                await ObserveRuntimeFailureAsync(session, failureCapture.Original ?? ex, escaping).ConfigureAwait(false);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(escaping);
        }

        return session;
    }

    private async Task RollBackProjectPersistenceAsync(
        ProjectDescriptor project,
        ProjectDescriptor? previousProject,
        bool persistedNewProject,
        bool restoredArchivedProject)
    {
        if (persistedNewProject)
        {
            await _projectCatalog.DeleteAsync(project, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        // A read-only catalog restored nothing: it leaves an archived project as it is.
        if (restoredArchivedProject && previousProject is not null && !_projectCatalog.IsReadOnly)
        {
            await _projectCatalog.SaveAsync(previousProject, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ensures that the session has an active coordinator session.
    /// </summary>
    /// <exception cref="ArgumentException">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    public async Task<AgentSessionHandleId> EnsureCoordinatorSessionAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        CancellationToken cancellationToken = default)
        => await EnsureCoordinatorSessionWithFailureCaptureAsync(session, options, cancellationToken, null).ConfigureAwait(false);

    private async Task<AgentSessionHandleId> EnsureCoordinatorSessionWithFailureCaptureAsync(
        SessionViewDescriptor session, SessionExecutionOptions options, CancellationToken cancellationToken, RuntimeFailureCapture? failureCapture)
    {
        return await AdmitAsync(async () =>
        {
            var entry = await ResolveCoordinatorEntryAsync(session, options, failureCapture: failureCapture).ConfigureAwait(false);
            return entry.SessionHandleId;
        }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal Task<AgentSessionHandleId> EnsureOwnedCoordinatorSessionAsync(SessionViewDescriptor session, SessionExecutionOptions options, bool useExplicitPrompt = false)
        => AdmitAsync(async () =>
        {
            var entry = await ResolveCoordinatorEntryAsync(session, options, ownedCommand: true, useExplicitPrompt: useExplicitPrompt).ConfigureAwait(false);
            return entry.SessionHandleId;
        }, CancellationToken.None);

    private void ReserveSessionIdentity(SessionViewDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_identityGate)
        {
            if (!string.IsNullOrWhiteSpace(session.SessionId)) return;
            session.SessionId = Guid.CreateVersion7().ToString();
            _newSessionIds.Add(session.SessionId);
        }
    }

    private async Task<RuntimeSessionEntry> ResolveCoordinatorEntryAsync(SessionViewDescriptor session, SessionExecutionOptions options, bool history = false, bool ownedCommand = false, RuntimeFailureCapture? failureCapture = null, bool useExplicitPrompt = false)
    {
        ReserveSessionIdentity(session);
        using var retentionWork = ClaimRetentionWork(session.SessionId, session.ParentSessionId, session.CreatedBy?.SourceSessionId);
        ArgumentNullException.ThrowIfNull(options);
        string? preparedPrompt = null;
        Task? joined = null;
        while (true)
        {
            ObjectDisposedException.ThrowIf(_forwarding.IsClosed, this);
            var actor = GetActorForWork(session.SessionId);
            var prepared = await actor.QueryAsync(
                actorCancellationToken => history && _entries.TryGetValue(session.SessionId, out var active) && !active.IsTerminated && !active.Attachment.IsRetiring
                    ? ValueTask.FromResult(new CoordinatorPreparation(active, null))
                    : EnsureCoordinatorSessionCoreAsync(session, options, actorCancellationToken, ownedCommand, failureCapture, useExplicitPrompt, preparedPrompt, joined),
                CancellationToken.None).ConfigureAwait(false);
            if (prepared.Entry is not null) return prepared.Entry;
            // Keep the pending prompt consumed by this request across its transition join.
            // Otherwise stale caller options immediately recreate the just-prepared attachment.
            preparedPrompt = prepared.SelectedPrompt ?? preparedPrompt;
            await prepared.Transition!.ConfigureAwait(false);
            joined = prepared.Own ? prepared.Transition : null;
        }
    }

    // Actor prepare only: callers join the returned ticket outside the mailbox.
    private async ValueTask<CoordinatorPreparation> EnsureCoordinatorSessionCoreAsync(
        SessionViewDescriptor session, SessionExecutionOptions options, CancellationToken actorCancellationToken, bool ownedCommand = false, RuntimeFailureCapture? failureCapture = null, bool useExplicitPrompt = false, string? preparedPrompt = null,
        Task? joinedTransition = null)
    {
        actorCancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_forwarding.IsClosed, this);
        if (_transitions.TryGetValue(session.SessionId, out var transition))
            return new CoordinatorPreparation(null, transition);
        _entries.TryGetValue(session.SessionId, out var existing);
        var prompt = (useExplicitPrompt ? null : NormalizeOptionalText(existing?.PendingAgentPromptId))
            ?? preparedPrompt ?? NormalizeOptionalText(options.AgentPromptId) ?? NormalizeOptionalText(session.AgentPromptId);
        // An attachment runs in one folder. A session whose worktree is gone, or that was given one, is attached
        // again where it works now; an attachment that is busy, or that this caller does not own, stays where it is.
        var settled = existing is not null && (existing.HasActiveRun || existing.QueueDrainInProgress || (ownedCommand && !HasOwnedCommandDefaults(existing)));
        var worktree = settled ? existing!.WorktreeDirectory : ExistingWorktree(session.WorktreeDirectory);
        bool Reusable(RuntimeSessionEntry entry) => entry.Matches(options, prompt) && string.Equals(entry.WorktreeDirectory, worktree, StringComparison.Ordinal)
            && UsesCurrentProvider(entry) && AgentToolPatterns.SameAs(entry.DisabledToolPatterns, GetDisabledToolPatterns(session.SessionId));
        // A provider whose settings were saved is registered again: an idle attachment is attached again, to run with
        // them. One that runs, or that another caller configured, keeps the provider it started with, and so does one
        // whose provider is no longer registered.
        bool UsesCurrentProvider(RuntimeSessionEntry entry)
        {
            if (entry.HasActiveRun || entry.QueueDrainInProgress || (ownedCommand && !HasOwnedCommandDefaults(entry)))
                return true;
            var current = _agentHub.GetProviderRegistrationVersion(entry.ProviderKey);
            return current == 0 || current == entry.ProviderRegistrationVersion;
        }
        if (ownedCommand && existing is not null && !Reusable(existing)
            && (existing.HasActiveRun || existing.QueueDrainInProgress || !HasOwnedCommandDefaults(existing)))
            throw new InvalidOperationException("Cannot change configuration of an active or externally owned attachment.");
        // Carry a matching but incompatible entry to send admission without consuming its pending
        // prompt or updating the descriptor. Admission rechecks under the actor and owns rejection.
        if (ownedCommand && existing is not null && !existing.Attachment.IsRetiring
            && !HasOwnedCommandDefaults(existing) && Reusable(existing))
            return new CoordinatorPreparation(existing, null);
        // The attachment that the transition of this request has just published was attached with the instructions
        // built for this request: it is taken as it is, without building them again to compare its tools.
        if (joinedTransition is not null && existing is not null && ReferenceEquals(existing.PublishedBy, joinedTransition)
            && !existing.Attachment.IsRetiring && Reusable(existing))
        {
            session.AgentPromptId = existing.ComposedAgentPromptId;
            existing.PendingAgentPromptId = null;
            await SyncTitleModeAsync(existing, actorCancellationToken).ConfigureAwait(false);
            return new CoordinatorPreparation(existing, null);
        }
        // Preserve validation/instruction-build-before-retirement behavior. The body is retained.
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ValidateDiscoveryPaths(session, options);
        var project = await ResolveProjectAsync(session, actorCancellationToken).ConfigureAwait(false);
        ValidateDiscoveryPaths(session, options, project);
        session.AgentPromptId = prompt;
        var built = await _instructionTemplateProvider.BuildCoordinatorInstructionsAsync(session, project, options.Model, prompt, actorCancellationToken).ConfigureAwait(false);
        // A session with no attachment is attached with these very instructions, unless what they read changes meanwhile.
        var prepared = existing is null ? PreparedInstructions.Of(built, session, project, options.Model, prompt) : null;
        // The tools the session does without follow the settings of the user, of its space and of its agent prompt:
        // an idle attachment that runs with others is attached again.
        if (existing is not null && !existing.Attachment.IsRetiring && Reusable(existing)
            && (settled || existing.DisabledTools.SetEquals(built.PromptBundle?.DisabledTools ?? new HashSet<string>())))
        {
            existing.PendingAgentPromptId = null;
            await SyncTitleModeAsync(existing, actorCancellationToken).ConfigureAwait(false);
            return new CoordinatorPreparation(existing, null);
        }
        var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Remote Control ends with the attachment it runs in (its bridge with the process of the provider): the
        // attachment that replaces it turns it on again, under the same name, when it is still wanted then.
        var carriesRemoteControl = existing is not null;
        var retirement = existing is null ? Task.CompletedTask : _forwarding.RetireAsync(existing.Attachment);
        Task? ticket = null;
        transition = _forwarding.RunAsync(async () =>
        {
            await launch.Task.ConfigureAwait(false);
            Exception? bodyFailure = null;
            try
            {
                await retirement.ConfigureAwait(false);
                ObjectDisposedException.ThrowIf(_forwarding.IsClosed, this);
                await CreateCoordinatorSessionAsync(session, options, ticket!, existing, prompt, CancellationToken.None, failureCapture, prepared).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                bodyFailure = failure;
                failureCapture?.Capture(failure);
                throw;
            }
            finally
            {
                try
                {
                await GetActorForWork(session.SessionId).QueryAsync(_ =>
                {
                    if (_transitions.TryGetValue(session.SessionId, out var current) && ReferenceEquals(current, ticket))
                    {
                        _transitions.TryRemove(session.SessionId, out var completedTransition);
                        // Callbacks that arrive from now on count as activity of the published attachment.
                        if (_entries.TryGetValue(session.SessionId, out var published)) published.OpenActivity();
                    }
                    return ValueTask.FromResult(true);
                }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupFailure) when (bodyFailure is not null)
                {
                    throw new AggregateException(bodyFailure, cleanupFailure);
                }
            }

            // Once the new attachment is published, and without holding back the send that waits for it: the
            // provider connects a process that a turn starts meanwhile.
            if (carriesRemoteControl && _remoteControlWanted.ContainsKey(session.SessionId))
                _ = _forwarding.RunAsync(() => CarryRemoteControlAsync(session.SessionId), external: false);
        }, external: false);
        _transitions[session.SessionId] = transition;
        ticket = transition;
        launch.TrySetResult();
        return new CoordinatorPreparation(null, transition, prompt, Own: true);
    }

    /// <param name="Own">Whether the transition is the one this request started, rather than one it waits for.</param>
    private sealed record CoordinatorPreparation(RuntimeSessionEntry? Entry, Task? Transition, string? SelectedPrompt = null, bool Own = false);

    // Coordinator instructions, with what they were built from: the session, its project, the model and the prompt.
    private sealed record PreparedInstructions(AgentInstructionBundle Instructions, string? ComposedAgentPromptId, string? WorktreeDirectory,
        string WorkingDirectory, string? SpaceId, string? ProviderKey, string? ProjectId, string? ProjectPath, string? Model, string? Prompt)
    {
        // Read after the build, which sets the agent prompt it composed on the session.
        public static PreparedInstructions Of(AgentInstructionBundle instructions, SessionViewDescriptor session, ProjectDescriptor? project, string? model, string? prompt)
            => new(instructions, session.AgentPromptId, session.WorktreeDirectory, session.WorkingDirectory, session.SpaceId, session.ResolvedProviderKey,
                project?.Id, project?.ProjectPath, model, prompt);

        public bool Matches(SessionViewDescriptor session, ProjectDescriptor? project, string? model, string? prompt)
            => WorktreeDirectory == session.WorktreeDirectory && WorkingDirectory == session.WorkingDirectory && SpaceId == session.SpaceId
                && ProviderKey == session.ResolvedProviderKey && ProjectId == project?.Id && ProjectPath == project?.ProjectPath
                && Model == model && Prompt == prompt;
    }

    // The remote control an attachment was asked for, with the name the session is shown under remotely.
    private sealed record RemoteControlRequest(string? Name);

    // Turns the remote control of a replaced attachment on again in the one that replaced it. Nobody waits for it:
    // a session that is gone again, or a runtime that closes, has nothing to turn on, and the state of the
    // session says whether it connected.
    private async Task CarryRemoteControlAsync(string sessionId)
    {
        try
        {
            await AdmitAsync(() => SetRemoteControlBodyAsync(sessionId, true, null, carried: true, CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is ObjectDisposedException or OperationCanceledException or InvalidOperationException or NotSupportedException)
        {
        }
    }

    private async ValueTask<AgentSessionHandleId> CreateCoordinatorSessionAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        Task ticket,
        RuntimeSessionEntry? previousEntry,
        string? selectedPrompt,
        CancellationToken cancellationToken,
        RuntimeFailureCapture? failureCapture,
        PreparedInstructions? prepared = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ValidateDiscoveryPaths(session, options);

        var project = await ResolveProjectAsync(session, cancellationToken).ConfigureAwait(false);
        ValidateDiscoveryPaths(session, options, project);

        // The session works in its worktree while that folder exists. Once it is gone the session works in the
        // folder of its project again, and no longer records the worktree. The instructions name the folder.
        var recordedWorktree = NormalizeOptionalText(session.WorktreeDirectory);
        var worktree = ExistingWorktree(recordedWorktree);
        session.WorktreeDirectory = worktree;
        // What the session said and did so far names the folder that is gone: it is told where it works now.
        var worktreeGone = recordedWorktree is not null && worktree is null
            ? $"The git worktree this session worked in, `{recordedWorktree}`, is no longer there. The session now works in the folder of the project, `{options.WorkingDirectory}`: use that folder, and not the one that is gone."
            : null;
        var effectiveAgentPromptId = selectedPrompt;
        session.AgentPromptId = effectiveAgentPromptId;
        // The preparation built them a moment ago, for a session that had no attachment: they are built again only
        // when what they were built from changed since (a worktree that is gone, another project or model).
        AgentInstructionBundle instructions;
        if (prepared is not null && prepared.Matches(session, project, options.Model, effectiveAgentPromptId))
        {
            instructions = prepared.Instructions;
            session.AgentPromptId = prepared.ComposedAgentPromptId;
        }
        else instructions = await _instructionTemplateProvider.BuildCoordinatorInstructionsAsync(session, project, options.Model, session.AgentPromptId, cancellationToken).ConfigureAwait(false);
        var composedAgentPromptId = session.AgentPromptId;
        foreach (var diagnostic in instructions.PromptBundle?.Diagnostics ?? [])
        {
            if (diagnostic.Code == "unreadable_project_context_file")
            {
                _events.TryPublish(new SessionHostEvent(session.SessionId, DateTimeOffset.UtcNow, AgentSessionUpdateKind.Warning, diagnostic.Message));
            }
        }
        var agentPromptUsage = ResolveAgentPromptUsage(instructions.PromptBundle, project?.ProjectPath);
        // Read with the instructions, which expand the same patterns into the built-in tools the session does without.
        var toolPatterns = GetDisabledToolPatterns(session.SessionId);
        var providerProviderId = new ModelProviderId(options.ProviderId.Value);
        var developerInstructions = instructions.DeveloperInstructions;
        // A session moved to another project is told so once, by the first attachment after the move.
        var movedNotice = TakeMovedNotice(session.SessionId);
        var additionalDeveloperInstructions = AppendPromptPart(AppendPromptPart(AppendPromptPart(BuildParentNotificationGuidance(session), worktreeGone), movedNotice),
            options.AdditionalDeveloperInstructions);
        var tools = options.Tools;
        // The identity of the space of the session as it is now: the session keeps it until it attaches again,
        // whatever the window shows and whatever is changed in the space meanwhile.
        var identity = await Identities.ResolveAsync(session.SpaceId, session.ProjectRef, cancellationToken).ConfigureAwait(false);
        // What the programs of the session start with: the identity of its space, and the variables that name the session.
        var processEnvironment = SessionProcessEnvironment.WithMarkers(identity.Environment, session.SessionId,
            options.ProviderKey ?? session.ResolvedProviderKey, options.Model, options.ReasoningEffort);

        AgentSessionHandleId sessionHandleId;
        bool startNewSession;
        lock (_identityGate) startNewSession = _newSessionIds.Remove(session.SessionId);

        // An attachment retains the saved title and who chose it. A session that starts says it itself; one that
        // says nothing (a frontend that built its descriptor) was named unless its title is one a session is created with.
        var stored = startNewSession ? null : await StoredTitleAsync(session, project, cancellationToken).ConfigureAwait(false);
        var title = stored is { } kept ? kept.Saved : NormalizeOptionalText(session.Title);
        var titleSource = stored?.Source ?? session.TitleSource
            ?? (title is not null && !IsCreationTitle(title, session.Kind, project) ? AgentSessionTitleSource.User : AgentSessionTitleSource.Auto);
        var shownTitle = title ?? session.Title;
        var requestedSessionId = NormalizeOptionalText(session.SessionId);
        var systemMessage = AppendPromptPart(AppendPromptPart(instructions.SystemMessage, options.AdditionalSystemMessage), Headless ? HeadlessGuidance : null);
        var finalDeveloperInstructions = AppendPromptPart(developerInstructions, additionalDeveloperInstructions);
        IReadOnlyList<AgentInstructionTransformationInfo> instructionTransformations = [];
        var instructionsAlreadyComposed = false;
        if (options.InstructionProcessor is not null)
        {
            var processing = await options.InstructionProcessor(
                    new SessionInstructionProcessingRequest
                    {
                        SessionId = requestedSessionId,
                        ProjectId = project?.Id,
                        ProjectPath = project?.ProjectPath,
                        ProviderId = options.ProviderId.Value,
                        Model = options.Model,
                        SystemMessage = systemMessage,
                        DeveloperInstructions = finalDeveloperInstructions,
                        ActiveToolNames = (tools ?? []).Select(static tool => tool.Spec.Name).ToArray(),
                        Manifest = new Dictionary<string, string>
                        {
                            ["agentPromptId"] = NormalizeOptionalText(session.AgentPromptId) ?? AgentPromptCatalog.DefaultPromptName,
                        },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(processing.CancelReason))
            {
                throw new InvalidOperationException(processing.CancelReason);
            }

            systemMessage = processing.SystemMessage;
            finalDeveloperInstructions = processing.DeveloperInstructions;
            instructionTransformations = processing.Transformations;
            instructionsAlreadyComposed = instructionTransformations.Count > 0;
        }

        // The permission mode is the one saved with the session: what attaches it does not have to know it. So is
        // how the session titles itself, which is the setting unless the session has a mode of its own.
        var savedLocalState = startNewSession ? null : await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false);
        var permissionMode = NormalizeOptionalText(startNewSession ? session.PermissionMode : savedLocalState?.PermissionMode);
        // So is the sandbox chosen for the session; its scopes are read again at each command.
        var ownSandbox = NormalizeOptionalText(startNewSession ? session.Sandbox : savedLocalState?.Sandbox);
        var ownTitleMode = AgentSessionTitles.ParseMode(startNewSession ? session.TitleMode : savedLocalState?.TitleMode);
        var titleMode = ownTitleMode ?? _configStore.LoadGlobalSessionTitleMode();
        var sessionOptions = new AgentSessionResumeOptions
        {
            SessionId = requestedSessionId,
            ParentSessionId = NormalizeOptionalText(session.ParentSessionId),
            CreatedBySessionId = NormalizeOptionalText(session.CreatedBy?.SourceSessionId),
            // The journal header records this instant; the provider's own record must not name a later one.
            CreatedAt = session.CreatedAt == default ? null : session.CreatedAt,
            Title = title,
            TitleSource = titleSource,
            TitleMode = titleMode,
            TitleInstructions = SessionTitleGuidance,
            // A suggestion is for somebody who reads it and writes the next prompt: not in a host that shows none,
            // and not for a session an agent drives. The setting is read at each prompt.
            PromptSuggestions = ShowsPromptSuggestions && string.IsNullOrWhiteSpace(session.ParentSessionId),
            PromptSuggestionInstructions = PromptSuggestionGuidance,
            PromptSuggestionsEnabled = _configStore.LoadGlobalPromptSuggestions,
            ProviderKey = options.ProviderKey ?? session.ResolvedProviderKey,
            Model = options.Model,
            ReasoningEffort = options.ReasoningEffort,
            PermissionMode = permissionMode,
            Streaming = true,
            WorkingDirectory = options.WorkingDirectory,
            WorktreeDirectory = worktree,
            LeaveWorktree = worktree is null,
            ProjectRoots = options.ProjectRoots,
            Environment = processEnvironment,
            SystemMessage = systemMessage,
            DeveloperInstructions = finalDeveloperInstructions,
            InstructionsAlreadyComposed = instructionsAlreadyComposed,
            InstructionTransformations = instructionTransformations,
            AgentPromptId = NormalizeOptionalText(session.AgentPromptId) ?? AgentPromptCatalog.DefaultPromptName,
            AgentPromptUsage = agentPromptUsage,
            Tools = tools,
            DisabledBuiltInTools = instructions.PromptBundle?.DisabledTools,
            DisabledToolPatterns = toolPatterns,
            WriteGuard = CreateWriteGuard(instructions.PromptBundle?.DisabledTools, options.WorkingDirectory, worktree, options.ProjectRoots),
            CommandGuard = CreateCommandGuard(session.SessionId),
            OnPermissionRequest = options.OnPermissionRequest,
            OnUserInputRequest = options.OnUserInputRequest,
            OnConversationHeld = AskWhenConversationHeld ? ConversationHolds.Handler : null,
        };

        // Read before the runtime is created: a provider registered again in between is only attached again once more.
        var providerVersion = _agentHub.GetProviderRegistrationVersion(sessionOptions.ProviderKey);
        // The lease before the journal: a session another process runs is not attached, and nothing is written for it.
        ClaimEntryLease(session.SessionId);
        try
        {
            if (startNewSession)
            {
                var handle = await _agentHub.StartSessionAsync(sessionOptions, cancellationToken).ConfigureAwait(false);
                sessionHandleId = handle.HandleId;
            }
            else
            {
                AgentSessionHandle handle;
                try
                {
                    handle = await _agentHub.ResumeSessionAsync(session.SessionId, sessionOptions, cancellationToken).ConfigureAwait(false);
                }
                catch (KeyNotFoundException)
                {
                    handle = await _agentHub.StartSessionAsync(sessionOptions, cancellationToken).ConfigureAwait(false);
                }

                sessionHandleId = handle.HandleId;
            }
        }
        catch
        {
            ReleaseEntryLeaseWithoutEntry(session.SessionId);
            throw;
        }

        var attachment = _forwarding.RegisterAttachment(
            session.SessionId, sessionHandleId.ToString(),
            () => _agentHub.AbortAsync(sessionHandleId, CancellationToken.None),
            () => _agentHub.StopSessionAsync(sessionHandleId, CancellationToken.None));
        // A retirement before this assignment cannot have a bound owned send: publication is later.
        attachment.CloseOwnedPermissions = () => CloseOwnedQueueAttachmentAsync(attachment);
        try
        {
        session.ProviderId = options.ProviderId.Value;
        session.ProviderKey = options.ProviderKey ?? options.ProviderId.Value;
        session.WorkingDirectory = options.WorkingDirectory;
        session.WorktreeDirectory = worktree;
        session.ModelId = options.Model;
        session.ReasoningEffort = options.ReasoningEffort;
        session.PermissionMode = permissionMode;
        session.Sandbox = ownSandbox;
        session.TitleMode = ownTitleMode is { } ownMode ? AgentSessionTitles.NameOf(ownMode) : null;
        session.TitleSource = titleSource;
        session.AgentPromptId = effectiveAgentPromptId ?? session.AgentPromptId;
        await UpsertSessionMetadataAsync(session, options, title, titleSource, cancellationToken).ConfigureAwait(false);
        var actor = GetActorForWork(session.SessionId);
        // Queue mutations share this mailbox. Keep the durable read/modify/append indivisible
        // with respect to them, without joining setup or retirement from inside the actor.
        await actor.QueryAsync(async actorCancellationToken =>
        {
            // The modes a session is created with are saved with it: the next attachment reads them from there.
            await UpdateSessionLocalStateAsync(session, actorCancellationToken, startNewSession ? permissionMode : null,
                startNewSession ? session.TitleMode : null, createdReviewBeforeCommit: startNewSession ? session.ReviewBeforeCommit : null,
                createdSandbox: startNewSession ? ownSandbox : null).ConfigureAwait(false);
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
        if (startNewSession)
        {
            await _agentSessionCatalog.NotifySessionCreatedAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _agentSessionCatalog.NotifySessionResumedAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        }

        PublishSessionCatalogEvent(session);
        PublishSessionLifecycleEvent(session.SessionId);

        RuntimeSessionEntry? entry = null;
        var projector = new EventProjector(
            session.SessionId,
            runtimeEvent => _events.TryPublish(runtimeEvent),
            @event => entry?.ObserveEvent(@event));
        entry = new RuntimeSessionEntry(
            session.SessionId,
            sessionHandleId,
            session.Kind,
            session.Status,
            providerProviderId,
            options.ProviderKey ?? session.ResolvedProviderKey,
            session.ProjectRef,
            session.ParentSessionId,
            session.CreatedBy,
            session.CreatedAt,
            shownTitle,
            options.WorkingDirectory,
            options.Model,
            options.ReasoningEffort,
            session.AgentPromptId,
            options.AdditionalSystemMessage,
            options.AdditionalDeveloperInstructions,
            options.ProjectRoots,
            tools,
            CreateToolSignatures(options.Tools),
            options.OnPermissionRequest,
            options.OnUserInputRequest,
            options.InstructionProcessor,
            projector,
            attachment)
        {
            WorktreeDirectory = worktree,
            ForkedFrom = session.ForkedFrom,
            SpaceId = session.SpaceId,
            Identity = identity,
            ProcessEnvironment = processEnvironment,
            ProviderRegistrationVersion = providerVersion,
            PermissionMode = permissionMode,
            OwnSandbox = ownSandbox,
            OwnTitleMode = ownTitleMode,
            TitleMode = titleMode,
            OwnReviewBeforeCommit = startNewSession ? session.ReviewBeforeCommit : savedLocalState?.ReviewBeforeCommit,
            ReviewNoted = startNewSession ? null : savedLocalState?.ReviewNoted,
            DisabledTools = instructions.PromptBundle?.DisabledTools ?? System.Collections.Frozen.FrozenSet<string>.Empty,
            DisabledToolPatterns = toolPatterns,
            PublishedBy = ticket,
            ComposedAgentPromptId = composedAgentPromptId,
        };

        projector.Entry = entry;
        var subscription = await _agentHub.SubscribeSessionEventsAsync(
                sessionHandleId,
                // Whether a callback counts as activity is decided when it arrives, not when the actor reaches it:
                // one delivered while the attachment is still being set up (a provider may call back from inside
                // the subscription itself) must not count or be skipped depending on how fast it is forwarded.
                @event => _ = PostAgentEventToActorAsync(actor, session.SessionId, projector, @event, entry.ActivityOpen),
                cancellationToken)
            .ConfigureAwait(false);
        attachment.InstallSubscription(subscription);
        attachment.CompleteSetup();
        await actor.QueryAsync(_ =>
        {
            ObjectDisposedException.ThrowIf(_forwarding.IsClosed || attachment.IsRetiring, this);
            if (!_transitions.TryGetValue(session.SessionId, out var currentTicket) || !ReferenceEquals(currentTicket, ticket))
                throw new InvalidOperationException("The coordinator transition no longer owns publication.");
            if (previousEntry is not null && !string.Equals(previousEntry.PendingAgentPromptId, selectedPrompt, StringComparison.Ordinal))
                entry.PendingAgentPromptId = previousEntry.PendingAgentPromptId;
            // An attachment is published with its lease, even when a removal released it meanwhile.
            ClaimEntryLease(session.SessionId);
            _entries[session.SessionId] = entry;
            return ValueTask.FromResult(true);
        }, CancellationToken.None).ConfigureAwait(false);

        return sessionHandleId;
        }
        catch (Exception failure)
        {
            failureCapture?.Capture(failure);
            // Signal setup before joining retirement: retirement may already be awaiting this record.
            attachment.CompleteSetup();
            try { await _forwarding.RetireAsync(attachment).ConfigureAwait(false); }
            catch (Exception cleanupFailure) { throw new AggregateException(failure, cleanupFailure); }
            finally { ReleaseEntryLeaseWithoutEntry(session.SessionId); }
            throw;
        }
    }

    private async Task PublishRunSubmittedIfStillInFlightAsync(
        SessionViewDescriptor session,
        AgentRunId runId,
        DateTimeOffset runStartedAt,
        CancellationToken cancellationToken,
        RuntimeSessionEntry capturedEntry)
    {
        if (await MarkActiveRunIfStillInFlightAsync(session.SessionId, runId, runStartedAt, cancellationToken).ConfigureAwait(false))
        {
            PublishRunSubmittedEvent(session.SessionId, runId, runStartedAt);
        }

        async Task<bool> MarkActiveRunIfStillInFlightAsync(string sessionId, AgentRunId id, DateTimeOffset started, CancellationToken token)
            => await GetActorForWork(sessionId).QueryAsync(_ =>
                ValueTask.FromResult(capturedEntry.MarkActiveRunIfStillInFlight(id, started)), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<AgentRunId?> ClearCapturedRunAsync(RuntimeSessionEntry? entry)
        => entry is null ? null : await GetActorForWork(entry.SessionId).QueryAsync(_ =>
            ValueTask.FromResult(entry.ClearActiveRun()), CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Sends input to the coordinator session for a session.
    /// </summary>
    /// <exception cref="ArgumentException">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    public async Task<AgentRunId> SendAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        AgentSendOptions sendOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sendOptions);

        return await SendAsync(session, options, sendOptions, cancellationToken, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<AgentRunId> SendAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        AgentSendOptions sendOptions,
        CancellationToken cancellationToken,
        CancellationToken coordinationCancellationToken)
    {
        // Taken at the admission: an abort of the tree after it wins over this send, even one detached from its caller.
        var admittedAbortSequence = CaptureAbortSequence();
        return await AdmitAsync(() => SendOwnedBodyAsync(session, options, sendOptions, admittedAbortSequence, cancellationToken, CancellationToken.None),
                coordinationCancellationToken)
            .WaitAsync(coordinationCancellationToken).ConfigureAwait(false);
    }

    // Awaited by a send once it is admitted, before it does anything, with the token that cancels it: what a test orders
    // itself by. Null in the application.
    internal Func<string, CancellationToken, Task>? SendStarting { get; set; }

    // Awaited by a send once its run is claimed, just before the run starts: what a test orders itself by. Null in the application.
    internal Func<string, Task>? RunClaimed { get; set; }

    private async Task<AgentRunId> SendOwnedBodyAsync(
        SessionViewDescriptor session, SessionExecutionOptions options, AgentSendOptions sendOptions, long admittedAbortSequence,
        CancellationToken cancellationToken, CancellationToken coordinationCancellationToken,
        SessionPermissionService.OwnedPermissionExecution? permissionExecution = null, bool ownedCommand = false,
        OwnedSessionAskExecution? askExecution = null, OwnedAskSubmission? askSubmission = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sendOptions);
        if (SendStarting is { } starting) await starting(session.SessionId, cancellationToken).ConfigureAwait(false);
        // A send cancelled before it got here (an abort of its tree after its admission) starts nothing, and lifts no hold.
        cancellationToken.ThrowIfCancellationRequested();

        using var retentionWork = ClaimRetentionWork(session.SessionId, session.ParentSessionId, session.CreatedBy?.SourceSessionId);
        await RestoreArchivedForRunAsync(session.SessionId, session.CreatedAt).ConfigureAwait(false);
        RuntimeSessionEntry? capturedEntry = null;
        OwnedProviderEventForwarding.Use? handleUse = null;
        CancellationTokenSource? execution = null;
        var failureCapture = new RuntimeFailureCapture();
        var executionReleased = false;
        var runInvocation = new OwnedSessionCommandService.OriginalInvocation();
        var permissionClose = new OwnedSessionCommandService.OriginalInvocation();
        var failures = new List<Exception>();
        AgentRunId result = default;
        var ownedDefaultsRejected = false;
        var abortedBeforeRun = false;
        // The turns made outside CodeAlta on the conversation of an idle session are written before its prompt: the
        // prompt follows them, and the provider is not sent what its conversation already holds.
        await CatchUpBeforeSendAsync(session).ConfigureAwait(false);
        // A send of an alta command can ask too; the finally below closes the execution.
        askExecution ??= CreateRunAskExecution(session.SessionId, cancellationToken);
        try
        {
            while (true)
            {
            var candidate = await ResolveCoordinatorEntryAsync(session, options, ownedCommand: ownedCommand, failureCapture: failureCapture).ConfigureAwait(false);
            GetActorForWork(session.SessionId);
            var sessionStateUpdated = false;
            var sessionHandleId = await _sessionActors.GetOrCreate(session.SessionId).QueryAsync(
                    async actorCancellationToken =>
                    {
                        await Task.CompletedTask.ConfigureAwait(false);
                        if (!_entries.TryGetValue(session.SessionId, out var current) || !ReferenceEquals(current, candidate)
                            || !candidate.Matches(options, NormalizeOptionalText(candidate.PendingAgentPromptId)
                                ?? NormalizeOptionalText(options.AgentPromptId) ?? NormalizeOptionalText(session.AgentPromptId)))
                            return default(AgentSessionHandleId);
                        handleUse = candidate.Attachment.TryAcquireWorkUse();
                        if (handleUse is null) return default(AgentSessionHandleId);
                        // Matches deliberately ignores callbacks. Reject before taking ownership of
                        // any run/start/prompt state; a different caller may already have an active run.
                        if (ownedCommand && !HasOwnedCommandDefaults(candidate))
                        {
                            ownedDefaultsRejected = true;
                            return default(AgentSessionHandleId);
                        }
                        // An abort of the tree after the admission wins: the run is claimed on the actor, as the abort
                        // finds it. A prompt sent to a session an abort held lets its queue drain again after this run.
                        if (!ClaimRunAfterAborts(session, admittedAbortSequence))
                        {
                            abortedBeforeRun = true;
                            return default(AgentSessionHandleId);
                        }
                        candidate.PendingAgentPromptId = null;
                        // This send runs with the model of the attachment, which it chose.
                        candidate.PendingModel = null;
                        capturedEntry = candidate;
                        session.MarkStarted(DateTimeOffset.UtcNow);
                        sessionStateUpdated = true;

                        return candidate.SessionHandleId;
                    },
                    coordinationCancellationToken)
                .ConfigureAwait(false);
            if (ownedDefaultsRejected)
                throw new InvalidOperationException("Owned command requires matching host-owned session defaults.");
            if (abortedBeforeRun)
                throw new OperationCanceledException("The session was aborted after this prompt was sent, before its run started.");
            if (handleUse is null) continue;

            if (sessionStateUpdated)
            {
                PublishSessionCatalogEvent(session);
            }

            if (RunClaimed is { } claimed) await claimed(session.SessionId).ConfigureAwait(false);
            var runStartedAt = DateTimeOffset.UtcNow;
            execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, candidate.Attachment.Cancellation.Token);
            AgentRunId runId;
            try
            {
                if (permissionExecution is not null)
                {
                    if (!await Permissions.BindOwnedExecutionAsync(permissionExecution, _runtimeInstanceId,
                        candidate.Attachment, candidate.ProviderId).ConfigureAwait(false))
                        throw new OperationCanceledException("Owned permission execution cannot bind to this attachment.");
                    sendOptions = new AgentSendOptions
                    {
                        Input = sendOptions.Input,
                        AskId = sendOptions.AskId,
                        SourceSessionId = sendOptions.SourceSessionId,
                        Provenance = sendOptions.Provenance,
                        AdditionalTools = sendOptions.AdditionalTools,
                        OnPermissionRequest = Permissions.CreateOwnedCommandHandler(permissionExecution),
                        OnUserInputRequest = Permissions.CreateOwnedUserInputHandler(permissionExecution),
                        EnableUserInputTool = permissionExecution.EnableUserInput,
                        RunLifecycle = OwnedSessionAskExecution.Combine(sendOptions.RunLifecycle, Permissions.CreateOwnedRunLifecycle(permissionExecution)),
                    };
                }
                if (askExecution is not null) sendOptions = ComposeRunAsk(askExecution, candidate, sendOptions);
                // What applies of the review before commit, told only when it differs from what the agent knows: a
                // note of the message, never the instructions, whose text stays the same for the cache of the provider.
                var reviewNote = ReviewNoteFor(candidate);
                if (reviewNote is { } told)
                {
                    sendOptions = sendOptions.WithInput(new AgentInput([.. sendOptions.Input.Items, new AgentInputItem.HostContext("review", ReviewNoteText(told))]));
                }

                Task<AgentRunId>? runOriginal = null;
                runInvocation.Launch(() => runOriginal = RunCapturedAsync(sessionHandleId, sendOptions, execution.Token));
                if (await runInvocation.Outcome.ConfigureAwait(false) is { } runFailure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(runFailure);
                runId = await runOriginal!.ConfigureAwait(false);
                askSubmission?.RecordRunReturned(runId);
                if (reviewNote is { } noted)
                {
                    await RecordReviewNotedAsync(session, candidate, noted).ConfigureAwait(false);
                }
            }
            catch (Exception failure)
            {
                failureCapture.Capture(failure);
                throw;
            }
            await PublishRunSubmittedIfStillInFlightAsync(session, runId, runStartedAt, coordinationCancellationToken, candidate).ConfigureAwait(false);

            result = runId;
            break;
            }
        }
        catch (Exception failure)
        {
            failureCapture.Capture(failure);
            failures.Add(failure);
        }
        finally
        {
            // Close independent interaction controls before joining either, retaining the actual
            // permission original and its outcome. A failed close never releases the linked source.
            if (permissionExecution is not null) permissionClose.Launch(() => Permissions.CloseOwnedExecutionAsync(permissionExecution));
            try { askExecution?.Close(); }
            catch (Exception failure)
            {
                failures.Add(new AgentDependencyRetentionException("runtime send", "ask close", [failure], askExecution!));
            }
            if (permissionExecution is not null && await permissionClose.Outcome.ConfigureAwait(false) is { } closeFailure)
                failures.Add(permissionClose.Original is null
                    ? new AgentDependencyRetentionException("runtime send", "permission close launch", [closeFailure], permissionClose)
                    : closeFailure);
            if (!failures.Any(OwnedProviderEventForwarding.HasRetention))
            {
                try { execution?.Dispose(); executionReleased = true; }
                catch (Exception failure) { failures.Add(new AgentDependencyRetentionException("runtime send", "source release", [failure], execution!)); }
                if (!failures.Any(OwnedProviderEventForwarding.HasRetention))
                {
                    try { handleUse?.Dispose(); }
                    catch (Exception failure) { failures.Add(new AgentDependencyRetentionException("runtime send", "handle release", [failure], handleUse!)); }
                }
            }
        }
        if (failures.Count == 0) return result;
        Exception escaping = failures.Count == 1 ? failures[0] : new AggregateException(failures);
        if (OwnedProviderEventForwarding.HasRetention(escaping))
        {
            escaping = new AgentDependencyRetentionException("runtime send", "retained terminal prerequisites", failures,
                new { Runtime = this, Source = execution, Use = handleUse, Run = runInvocation, Close = permissionClose, Ask = askExecution, Permission = permissionExecution });
            handleUse?.Retain(escaping);
            _forwarding.RetainDependencies(escaping, this);
        }
        // A policy refusal still affects only the owned command receipt, never another caller's run.
        if (!ownedDefaultsRejected)
        {
            var original = failureCapture.Original ?? escaping;
            try
            {
                var activeRunId = await ClearCapturedRunAsync(capturedEntry).ConfigureAwait(false);
                if (original is OperationCanceledException)
                    PublishRunFinishedEvent(session.SessionId, activeRunId, SessionLifecycleEventKind.RunAborted, "Runtime run cancelled.", DateTimeOffset.UtcNow);
            }
            catch (Exception cleanupFailure) { escaping = new AggregateException(escaping, cleanupFailure); }
            var prerequisite = new LiveEventObservationPrerequisite(
                new { Runtime = this, Source = execution, Use = handleUse, Run = runInvocation, Close = permissionClose, Ask = askExecution, Permission = permissionExecution },
                executionReleased && (handleUse is null || handleUse.IsReleased), escaping);
            await ObserveRuntimeFailureAsync(session, original, escaping, prerequisite).ConfigureAwait(false);
        }
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(escaping);
        return default;
    }

    internal Task<AgentRunId> SendOwnedCommandAsync(SessionViewDescriptor session, SessionExecutionOptions options,
        AgentSendOptions sendOptions, SessionPermissionService.OwnedPermissionExecution? permissionExecution, CancellationToken cancellationToken,
        OwnedSessionAskExecution? askExecution = null, OwnedAskSubmission? askSubmission = null)
    {
        var admittedAbortSequence = CaptureAbortSequence();
        return AdmitAsync(() => SendOwnedBodyAsync(session, options, sendOptions, admittedAbortSequence, cancellationToken, CancellationToken.None, permissionExecution,
            ownedCommand: true, askExecution: askExecution, askSubmission: askSubmission), CancellationToken.None);
    }

    /// <summary>
    /// Gets or sets the ask owner of the host, when its sends can ask the user: a run the runtime starts by itself
    /// then asks as a send of the window does, once while it is open. Such runs are a prompt sent by an
    /// <c>alta</c> command, and a prompt drained from the queue of the session (the result a child session or a
    /// background job sends back, a prompt queued in the window), so that a session that waited for its children
    /// can still ask, or request the review of its plan, at the end.
    /// </summary>
    internal OwnedSessionAskService? RunAsks { get; set; }

    // The ask execution of a run that no send of the owner gave one; null while asks are off or closed.
    private OwnedSessionAskExecution? CreateRunAskExecution(string sessionId, CancellationToken cancellationToken)
    {
        if (RunAsks is not { Enabled: true } asks) return null;
        var execution = asks.CreateExecution(Guid.NewGuid(), sessionId, cancellationToken);
        return execution.IsClosed ? null : execution;
    }

    // Binds an ask execution to the attachment that runs the send and gives the run its ask. A session whose own
    // tools include `alta` reaches the execution through that tool.
    private AgentSendOptions ComposeRunAsk(OwnedSessionAskExecution askExecution, RuntimeSessionEntry entry, AgentSendOptions sendOptions)
    {
        askExecution.Bind(_runtimeInstanceId, entry.Attachment.Ordinal, entry.ProviderId);
        return askExecution.Compose(sendOptions,
            includeTool: !entry.Tools.Any(static tool => string.Equals(tool.Spec.Name, "alta", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Saves the permission mode of an owned session and gives it to its attachment, which is not attached again. It
    /// applies at once, also to a run in progress: the requests of the session follow it from now on, those that wait
    /// for the user and that it grants are allowed, and its provider is told where it can be, between runs as during one.
    /// </summary>
    /// <param name="session">The session, resolved by the owner.</param>
    /// <param name="permissionMode">One of the permission modes of its provider, or null for the one the provider is configured with.</param>
    /// <exception cref="InvalidOperationException">The attachment is not owned.</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closing.</exception>
    internal Task SetOwnedPermissionModeAsync(SessionViewDescriptor session, string? permissionMode)
    {
        ArgumentNullException.ThrowIfNull(session);
        permissionMode = NormalizeOptionalText(permissionMode);
        return AdmitAsync(async () =>
        {
            using var lease = HoldForWrite(session.SessionId);
            while (true)
            {
                ObjectDisposedException.ThrowIf(_forwarding.IsClosed, this);
                var (transition, attached) = await GetActorForWork(session.SessionId).QueryAsync(async actorCancellationToken =>
                {
                    // An attachment that is being made reads the saved mode: it is waited for, then given the new one.
                    if (_transitions.TryGetValue(session.SessionId, out var pending)) return ((Task?)pending, (AgentSessionHandleId?)null);
                    if (_entries.TryGetValue(session.SessionId, out var entry) && (entry.IsTerminated || entry.Attachment.IsRetiring)) entry = null;
                    // A run in progress, or a queue that drains, takes the new mode at once: its requests follow the mode
                    // the session has when they come.
                    if (entry is not null && !HasOwnedCommandDefaults(entry))
                        throw new InvalidOperationException("Cannot change configuration of an externally owned attachment.");
                    if (entry is not null)
                    {
                        await _agentHub.SetPermissionModeAsync(entry.SessionHandleId, permissionMode, actorCancellationToken).ConfigureAwait(false);
                        entry.PermissionMode = permissionMode;
                    }
                    var localState = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, actorCancellationToken).ConfigureAwait(false);
                    if (!string.Equals(NormalizeOptionalText(localState?.PermissionMode), permissionMode, StringComparison.Ordinal))
                    {
                        if (localState is null)
                        {
                            localState = new SessionViewLocalState();
                            CopySessionMetadata(session, localState);
                        }

                        localState.PermissionMode = permissionMode;
                        await _sessionViewCatalog.JournalStore.AppendStateAsync(session, localState, actorCancellationToken).ConfigureAwait(false);
                    }

                    session.PermissionMode = permissionMode;
                    return ((Task?)null, entry is not null ? entry.SessionHandleId : (AgentSessionHandleId?)null);
                }, CancellationToken.None).ConfigureAwait(false);
                if (transition is null)
                {
                    // Outside the actor of the session: the provider may take a moment to answer.
                    await ApplyPermissionModeNowAsync(session.SessionId, attached).ConfigureAwait(false);
                    return;
                }
                // Its failure belongs to the one that attaches.
                await transition.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).ConfigureAwait(false);
            }
        }, CancellationToken.None);
    }

    // What a new mode changes for the requests of a session at once: those that wait for the user and that it grants
    // are allowed, and its attachment is told, so that what its provider decides by itself follows it too, during a
    // run and between runs. A provider that cannot be told takes the mode at its next turn; meanwhile the host answers
    // its requests by it.
    private async Task ApplyPermissionModeNowAsync(string sessionId, AgentSessionHandleId? attached)
    {
        if (_sessionPermissionModes)
            await Permissions.GrantWaitingOwnedAsync(sessionId, GetPermissionPolicy(sessionId)).ConfigureAwait(false);
        if (attached is not { } handle) return;
        try
        {
            await _agentHub.ApplyPermissionModeNowAsync(handle, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException or TimeoutException)
        {
            // The attachment went away meanwhile: the next one reads the saved mode.
        }
    }

    // Fixed default-policy check only; per-operation association remains in the permission mailbox.
    private bool HasOwnedCommandDefaults(RuntimeSessionEntry candidate)
        => ReferenceEquals(candidate.OnPermissionRequest, Permissions.OwnedDefaultPermissionHandler)
            && ReferenceEquals(candidate.OnUserInputRequest, Permissions.OwnedDefaultUserInputHandler);

    private Task<AgentRunId> RunCapturedAsync(AgentSessionHandleId sessionHandleId, AgentSendOptions sendOptions, CancellationToken cancellationToken)
        => _agentHub.RunAsync(sessionHandleId, sendOptions, cancellationToken);

    /// <summary>
    /// Persists a headless prompt queue item for later submission by the owning runtime/front-end queue drain path.
    /// </summary>
    /// <param name="session">Target session.</param>
    /// <param name="prompt">Prompt text to submit later.</param>
    /// <param name="kind">Prompt dispatch kind, such as <c>send</c>, <c>message</c>, or <c>request</c>.</param>
    /// <param name="submittedBy">Durable caller attribution for the queueing actor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted queue item.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="prompt"/> or <paramref name="kind"/> is empty.</exception>
    public async Task<SessionViewQueuedPrompt> QueuePromptAsync(
        SessionViewDescriptor session,
        string prompt,
        string kind,
        AltaActorProvenance? submittedBy,
        CancellationToken cancellationToken = default)
        => await AdmitAsync(() => QueuePromptOwnedBodyAsync(session, prompt, kind, submittedBy, CancellationToken.None), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<SessionViewQueuedPrompt> QueuePromptOwnedBodyAsync(SessionViewDescriptor session, string prompt, string kind,
        AltaActorProvenance? submittedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        using var retentionWork = ClaimRetentionWork(session.SessionId, session.ParentSessionId, session.CreatedBy?.SourceSessionId);
        using var lease = HoldForWrite(session.SessionId);
        var actor = GetActorForWork(session.SessionId);
        return await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    var current = await _sessionViewCatalog.JournalStore.CreateSessionStore().GetSessionAsync(session.SessionId, actorCancellationToken).ConfigureAwait(false);
                    if (current is null || current.CreatedAt != session.CreatedAt)
                        throw new InvalidOperationException("The queued prompt's session generation no longer exists.");
                    var timestamp = DateTimeOffset.UtcNow;
                    var item = new SessionViewQueuedPrompt
                    {
                        QueueItemId = "queue-" + Guid.NewGuid().ToString("N"),
                        Kind = kind,
                        Prompt = prompt,
                        PromptPreview = CreatePromptPreview(prompt),
                        State = "queued",
                        SubmittedBy = submittedBy,
                        CreatedAt = timestamp,
                    };

                    var localState = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, actorCancellationToken).ConfigureAwait(false) ?? new SessionViewLocalState();
                    CopySessionMetadata(session, localState);
                    localState.QueuedPrompts ??= [];
                    localState.QueuedPrompts.Add(item);
                    localState.PromptProvenance ??= [];
                    localState.PromptProvenance.Add(new SessionViewPromptProvenance
                    {
                        PromptId = item.QueueItemId,
                        Kind = kind,
                        Queued = true,
                        PromptPreview = item.PromptPreview,
                        SubmittedBy = submittedBy,
                        CreatedAt = timestamp,
                    });

                    TrimLocalStateHistory(localState);
                    await _sessionViewCatalog.JournalStore.AppendStateAsync(session, localState, actorCancellationToken).ConfigureAwait(false);
                    _events.TryPublish(new SessionQueueRuntimeEvent(
                        session.SessionId,
                        timestamp,
                        QueuedPromptCount(localState),
                        item.QueueItemId,
                        item.PromptPreview,
                        IsEnqueued: true)
                    { QueueKind = item.Kind });
                    return item;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Activates a CodeAlta-managed skill for a session through the host-owned agent runtime path.
    /// </summary>
    /// <param name="session">Target session.</param>
    /// <param name="options">Execution options used to resolve the backing session.</param>
    /// <param name="skillName">Skill name to activate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The run identifier that received the activated skill content.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="skillName"/> is empty or a supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the requested skill cannot be resolved.</exception>
    /// <exception cref="InvalidOperationException">The target or its lineage is being retired or has been retired.</exception>
    public async Task<AgentRunId> ActivateSkillAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        string skillName,
        CancellationToken cancellationToken = default)
    {
        var admittedAbortSequence = CaptureAbortSequence();
        return await AdmitAsync(() => ActivateSkillOwnedBodyAsync(session, options, skillName, admittedAbortSequence, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentRunId> ActivateSkillOwnedBodyAsync(SessionViewDescriptor session, SessionExecutionOptions options,
        string skillName, long admittedAbortSequence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);

        return await WithRetentionWorkAsync(async () =>
        {
            // Join the preparation and send originals, never their public cancelable observers.
            // The activation admission covers both phases, including their cancellation unwind.
            var activation = await AwaitRetentionAwareAsync(
                CreateSkillActivationOwnedBodyAsync(session, options, skillName, cancellationToken)).ConfigureAwait(false);

            var input = new AgentInput(
            [
                new AgentInputItem.Skill(activation.Descriptor.Name, activation.Descriptor.SkillFilePath),
                new AgentInputItem.Text(
                    $"""
                    The user activated the CodeAlta skill '{activation.Descriptor.Name}' for this session.
                    Treat the following host-provided skill content as active session context.

                    {activation.Payload}
                    """),
            ]);

            // Preserve SendAsync's pre-admission cancellation check without using its waiter.
            cancellationToken.ThrowIfCancellationRequested();
            return await AwaitRetentionAwareAsync(SendOwnedBodyAsync(
                    session,
                    options,
                    new AgentSendOptions { Input = input },
                    admittedAbortSequence,
                    cancellationToken,
                    CancellationToken.None))
                .ConfigureAwait(false);
        }, session.SessionId, session.ParentSessionId, session.CreatedBy?.SourceSessionId).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a CodeAlta-managed skill activation payload for a session without starting an agent run.
    /// </summary>
    /// <param name="session">Target session.</param>
    /// <param name="options">Execution options used to resolve project-local skill roots.</param>
    /// <param name="skillName">Skill name to activate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved activation payload.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="skillName"/> is empty or a supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the requested skill cannot be resolved.</exception>
    /// <exception cref="InvalidOperationException">The target or its lineage is being retired or has been retired.</exception>
    public async Task<SkillActivation> CreateSkillActivationAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        string skillName,
        CancellationToken cancellationToken = default)
        => await AdmitAsync(() => CreateSkillActivationOwnedBodyAsync(session, options, skillName, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<SkillActivation> CreateSkillActivationOwnedBodyAsync(SessionViewDescriptor session, SessionExecutionOptions options,
        string skillName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);

        return await WithRetentionWorkAsync(async () =>
        {
            ValidateDiscoveryPaths(session, options);
            var project = await AwaitRetentionAwareAsync(ResolveProjectAsync(session, cancellationToken)).ConfigureAwait(false);
            var space = await AwaitRetentionAwareAsync(GetSpaceFilterAsync(session.SpaceId, project, cancellationToken)).ConfigureAwait(false);
            var query = BuildSkillCatalogQuery(project, options.ProjectRoots, space);
            if (await AwaitRetentionAwareAsync(_skillCatalog.ActivateAsync(query, skillName, cancellationToken)).ConfigureAwait(false) is { } activation)
            {
                return activation;
            }

            // A skill of another space is refused for what it is, so that the agent can tell the user.
            var document = await AwaitRetentionAwareAsync(_skillCatalog.GetAsync(query with { IncludeDisabled = true }, skillName, cancellationToken)).ConfigureAwait(false);
            throw document?.Descriptor.IsOutsideSpace == true
                ? new ResourceNotInSpaceException(SpaceResourceKind.Skill, document.Descriptor.Name, space.SpaceId)
                : new KeyNotFoundException($"Skill '{skillName}' was not found or is not activatable for this session.");
        }, session.SessionId, session.ParentSessionId, session.CreatedBy?.SourceSessionId).ConfigureAwait(false);
    }

    /// <summary>
    /// Steers the current coordinator run for a session.
    /// </summary>
    /// <exception cref="InvalidOperationException">The target or its lineage is being retired, has been retired, or has no matching active run.</exception>
    public async Task<AgentRunId> SteerAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        AgentSteerOptions steerOptions,
        CancellationToken cancellationToken = default)
        => await AdmitAsync(() => SteerOwnedBodyAsync(session, options, steerOptions, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<AgentRunId> SteerOwnedBodyAsync(SessionViewDescriptor session, SessionExecutionOptions options,
        AgentSteerOptions steerOptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(steerOptions);

        return await WithRetentionWorkAsync(async () =>
        {
        OwnedProviderEventForwarding.Use? handleUse = null;
        try
        {
        GetActorForWork(session.SessionId);
        var sessionHandleId = await _sessionActors.GetOrCreate(session.SessionId).QueryAsync(
                async actorCancellationToken =>
                {
                    var entry = await GetActiveRuntimeSessionForSteeringAsync(session, options, actorCancellationToken).ConfigureAwait(false);
                    handleUse = entry.Attachment.TryAcquireHandleUse()
                        ?? throw new InvalidOperationException("The coordinator attachment is retiring.");
                    return entry.SessionHandleId;
                },
                CancellationToken.None)
            .ConfigureAwait(false);

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, handleUse!.Attachment.Cancellation.Token);
        return await SteerCapturedAsync(sessionHandleId, steerOptions, execution.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (OwnedProviderEventForwarding.HasRetention(failure))
        {
            handleUse?.Retain(failure);
            throw;
        }
        finally { handleUse?.Dispose(); }
        }, session.SessionId, session.ParentSessionId, session.CreatedBy?.SourceSessionId).ConfigureAwait(false);
    }

    // Own the original body, including options/provider preparation, not a caller's cancelable wait.
    // Unconfirmed dependency failure keeps admission with the same owner as the orphaned operation.
    private async Task<T> WithRetentionWorkAsync<T>(Func<Task<T>> body, params string?[] sessionIds)
    {
        var claim = ClaimRetentionWork(sessionIds);
        var retained = false;
        Task<T>? original = null;
        try
        {
            original = body();
            return await original.ConfigureAwait(false);
        }
        catch (Exception failure) when (OwnedProviderEventForwarding.HasRetention(original?.Exception ?? failure))
        {
            retained = true;
            var failureGraph = original?.Exception ?? failure;
            _forwarding.RetainDependencies(failureGraph, new { Runtime = this, Claim = claim, Original = original });
            if (!ReferenceEquals(failureGraph, failure)) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failureGraph);
            throw;
        }
        finally
        {
            if (!retained) claim.Dispose();
        }
    }

    // Await alone selects one sibling from a faulted original. Preserve retention evidence before
    // callers release a handle/history use or interpret the failure as permission to queue instead.
    private static async Task<T> AwaitRetentionAwareAsync<T>(Task<T> original)
    {
        try { return await original.ConfigureAwait(false); }
        catch (Exception failure) when (OwnedProviderEventForwarding.HasRetention(original.Exception ?? failure))
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(original.Exception ?? failure);
            throw;
        }
    }

    private Task<AgentRunId> SteerCapturedAsync(AgentSessionHandleId sessionHandleId, AgentSteerOptions steerOptions, CancellationToken cancellationToken)
    {
        return AwaitRetentionAwareAsync(_agentHub.SteerAsync(sessionHandleId, steerOptions, cancellationToken));
    }

    // Only the command owner supplies execution cancellation; transport cancellation never reaches
    // accepted work. This path cannot acquire a coordinator or substitute a newly observed target.
    internal Task<AgentRunId> SteerOwnedCommandAsync(OwnedTextSteerRequest request, CancellationToken executionCancellationToken)
        => AdmitAsync(() => SteerOwnedCommandBodyAsync(request, executionCancellationToken), CancellationToken.None);

    private async Task<AgentRunId> SteerOwnedCommandBodyAsync(OwnedTextSteerRequest request, CancellationToken executionCancellationToken)
    {
        executionCancellationToken.ThrowIfCancellationRequested();
        if (request.ExpectedRuntimeInstanceId != _runtimeInstanceId || !_sessionActors.TryGet(request.SessionId, out var actor))
            throw new InvalidOperationException("The observed steering target is unavailable.");
        OwnedProviderEventForwarding.Use? handleUse = null;
        try
        {
            var handle = await actor.QueryAsync(_ =>
            {
                if (_transitions.ContainsKey(request.SessionId) || !_entries.TryGetValue(request.SessionId, out var entry)
                    || entry.IsTerminated || entry.Attachment.Ordinal != request.ExpectedAttachmentGeneration
                    || entry.ActiveRunId is not { } activeRun || !string.Equals(activeRun.Value, request.ExpectedRunId, StringComparison.Ordinal)
                    || !HasOwnedCommandDefaults(entry))
                    throw new InvalidOperationException("The observed steering target is stale or is not owned.");
                handleUse = entry.Attachment.TryAcquireHandleUse()
                    ?? throw new InvalidOperationException("The observed steering attachment is retiring.");
                return ValueTask.FromResult(entry.SessionHandleId);
            }, CancellationToken.None).ConfigureAwait(false);
            var lifetime = new RuntimeCommandLifetime(new { Runtime = this, Use = handleUse });
            var expectedRun = new AgentRunId(request.ExpectedRunId);
            var returnedRun = await lifetime.RunAsync(token => SteerCapturedAsync(handle, new AgentSteerOptions
            {
                Input = AgentInput.Text(request.Text),
                ExpectedRunId = expectedRun,
            }, token), executionCancellationToken, handleUse!.Attachment.Cancellation.Token).ConfigureAwait(false);
            if (returnedRun != expectedRun)
                throw new InvalidOperationException("The provider returned a different steering target.");
            return returnedRun;
        }
        catch (Exception failure) when (OwnedProviderEventForwarding.HasRetention(failure))
        {
            handleUse?.Retain(failure);
            _forwarding.RetainDependencies(failure, this);
            throw;
        }
        finally { handleUse?.Dispose(); }
    }

    // Recorded idleness only permits a provider attempt. The provider gate proves idle against
    // intervening sends; neither this route nor its mailbox query publishes a started event.
    internal Task<AgentCompactionOutcome?> CompactOwnedCommandAsync(OwnedCompactRequest request, CancellationToken executionCancellationToken)
        => AdmitAsync(() => CompactOwnedCommandBodyAsync(request, executionCancellationToken), CancellationToken.None);

    private async Task<AgentCompactionOutcome?> CompactOwnedCommandBodyAsync(OwnedCompactRequest request, CancellationToken executionCancellationToken)
    {
        executionCancellationToken.ThrowIfCancellationRequested();
        if (request.ExpectedRuntimeInstanceId != _runtimeInstanceId || !_sessionActors.TryGet(request.SessionId, out var actor))
            throw new InvalidOperationException("The observed compaction target is unavailable.");
        OwnedProviderEventForwarding.Use? handleUse = null;
        try
        {
            var handle = await actor.QueryAsync(_ =>
            {
                if (_transitions.ContainsKey(request.SessionId) || !_entries.TryGetValue(request.SessionId, out var entry)
                    || entry.IsTerminated || entry.Attachment.Ordinal != request.ExpectedAttachmentGeneration || !HasOwnedCommandDefaults(entry))
                    throw new InvalidOperationException("The observed compaction target is stale or is not owned.");
                if (entry.ActiveRunId is not null || entry.QueueDrainInProgress)
                    return ValueTask.FromResult<AgentSessionHandleId?>(null);
                handleUse = entry.Attachment.TryAcquireWorkUse()
                    ?? throw new InvalidOperationException("The observed compaction attachment is retiring.");
                return ValueTask.FromResult<AgentSessionHandleId?>(entry.SessionHandleId);
            }, CancellationToken.None).ConfigureAwait(false);
            if (handle is null) return null;
            var lifetime = new RuntimeCommandLifetime(new { Runtime = this, Use = handleUse });
            return await lifetime.RunAsync(token => _agentHub.TryCompactWhenIdleAsync(handle.Value, token),
                executionCancellationToken, handleUse!.Attachment.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (OwnedProviderEventForwarding.HasRetention(failure))
        {
            handleUse?.Retain(failure);
            _forwarding.RetainDependencies(failure, this);
            throw;
        }
        finally { handleUse?.Dispose(); }
    }

    /// <summary>
    /// Asks the provider of a session to stop one of its background tasks. The end of the task is told by the
    /// events of the session; a task that already ended is not an error.
    /// </summary>
    /// <param name="sessionId">The session, which must be attached in this runtime.</param>
    /// <param name="taskId">The identity of the task, as the state of the session lists it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the provider took the request; false when the session is not attached, or its provider has no such tasks.</returns>
    /// <exception cref="ArgumentException">An identity is blank.</exception>
    /// <exception cref="OperationCanceledException">The request was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closing.</exception>
    public Task<bool> StopBackgroundTaskAsync(string sessionId, string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        // A background job is a task of the host, not of the provider: the user stops it whatever the session does.
        if (taskId.StartsWith(SessionJobService.IdPrefix, StringComparison.OrdinalIgnoreCase)) return StopJobAsync(sessionId, taskId, cancellationToken);
        return AdmitAsync(() => StopBackgroundTaskBodyAsync(sessionId, taskId, cancellationToken), cancellationToken);
    }

    private async Task<bool> StopBackgroundTaskBodyAsync(string sessionId, string taskId, CancellationToken cancellationToken)
    {
        if (!_sessionActors.TryGet(sessionId, out var actor)) return false;
        OwnedProviderEventForwarding.Use? handleUse = null;
        try
        {
            // A task goes on outside the runs: neither a run nor a queue that drains holds this back.
            var handle = await actor.QueryAsync(_ =>
            {
                if (_transitions.ContainsKey(sessionId) || !_entries.TryGetValue(sessionId, out var entry) || entry.IsTerminated
                    || !entry.BackgroundTasks.Any(task => task.Outcome is null && string.Equals(task.TaskId, taskId, StringComparison.Ordinal)))
                    return ValueTask.FromResult<AgentSessionHandleId?>(null);
                handleUse = entry.Attachment.TryAcquireHandleUse();
                return ValueTask.FromResult(handleUse is null ? (AgentSessionHandleId?)null : entry.SessionHandleId);
            }, CancellationToken.None).ConfigureAwait(false);
            if (handle is null) return false;
            var lifetime = new RuntimeCommandLifetime(new { Runtime = this, Use = handleUse });
            return await lifetime.RunAsync(token => _agentHub.StopBackgroundTaskAsync(handle.Value, taskId, token),
                cancellationToken, handleUse!.Attachment.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (OwnedProviderEventForwarding.HasRetention(failure))
        {
            handleUse?.Retain(failure);
            _forwarding.RetainDependencies(failure, this);
            throw;
        }
        finally { handleUse?.Dispose(); }
    }

    /// <summary>
    /// Turns the remote control of an attached session on or off (Claude Code's Remote Control). Neither a run nor
    /// a queue that drains holds it back: it acts on the provider at once.
    /// </summary>
    /// <param name="sessionId">The session, which must be attached in this runtime.</param>
    /// <param name="enabled">Whether the session is to be controlled remotely.</param>
    /// <param name="name">The name the session is shown under remotely, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The remote control after the request; null when the session is not attached or its provider has none.</returns>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    /// <exception cref="OperationCanceledException">The request was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closing.</exception>
    public Task<AgentRemoteControl?> SetRemoteControlAsync(string sessionId, bool enabled, string? name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return AdmitAsync(() => SetRemoteControlBodyAsync(sessionId, enabled, name, carried: false, cancellationToken), cancellationToken);
    }

    private void ForgetRemoteControl(string sessionId) => _remoteControlWanted.TryRemove(sessionId, out _);

    // Carried, it is turned on again only while it is still wanted, under the name it was wanted with.
    private async Task<AgentRemoteControl?> SetRemoteControlBodyAsync(string sessionId, bool enabled, string? name, bool carried, CancellationToken cancellationToken)
    {
        if (!_sessionActors.TryGet(sessionId, out var actor))
        {
            if (!enabled) ForgetRemoteControl(sessionId);
            return null;
        }

        OwnedProviderEventForwarding.Use? handleUse = null;
        try
        {
            var handle = await actor.QueryAsync(_ =>
            {
                // Turned off, it is no longer wanted, whatever the attachment: one being replaced does not turn it on again.
                if (!enabled) ForgetRemoteControl(sessionId);
                if (carried)
                {
                    if (!_remoteControlWanted.TryGetValue(sessionId, out var wanted)) return ValueTask.FromResult<AgentSessionHandleId?>(null);
                    name = wanted.Name;
                }

                if (_transitions.ContainsKey(sessionId) || !_entries.TryGetValue(sessionId, out var entry) || entry.IsTerminated)
                    return ValueTask.FromResult<AgentSessionHandleId?>(null);
                handleUse = entry.Attachment.TryAcquireHandleUse();
                if (handleUse is null) return ValueTask.FromResult<AgentSessionHandleId?>(null);
                if (enabled) _remoteControlWanted[sessionId] = new RemoteControlRequest(name);
                return ValueTask.FromResult<AgentSessionHandleId?>(entry.SessionHandleId);
            }, CancellationToken.None).ConfigureAwait(false);
            if (handle is null) return null;
            var lifetime = new RuntimeCommandLifetime(new { Runtime = this, Use = handleUse });
            return await lifetime.RunAsync(token => _agentHub.SetRemoteControlAsync(handle.Value, enabled, name, token),
                cancellationToken, handleUse!.Attachment.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (OwnedProviderEventForwarding.HasRetention(failure))
        {
            handleUse?.Retain(failure);
            _forwarding.RetainDependencies(failure, this);
            throw;
        }
        finally { handleUse?.Dispose(); }
    }

    internal Task<AgentTargetedAbortOutcome?> AbortRunOwnedCommandAsync(OwnedAbortRunRequest request, CancellationToken executionCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return AdmitAsync(() => AbortRunOwnedCommandBodyAsync(request, executionCancellationToken), CancellationToken.None);
    }

    private async Task<AgentTargetedAbortOutcome?> AbortRunOwnedCommandBodyAsync(OwnedAbortRunRequest request, CancellationToken executionCancellationToken)
    {
        executionCancellationToken.ThrowIfCancellationRequested();
        if (request.ExpectedRuntimeInstanceId != _runtimeInstanceId || !_sessionActors.TryGet(request.SessionId, out var actor)) return null;
        OwnedProviderEventForwarding.Use? handleUse = null;
        try
        {
            var handle = await actor.QueryAsync(_ =>
            {
                // A queue drain is no refusal: a provider returns from its send when the run ends, so the run a
                // drain started (the answer of a child session, a queued prompt) is draining for as long as it runs.
                if (_transitions.ContainsKey(request.SessionId) || !_entries.TryGetValue(request.SessionId, out var entry)
                    || entry.IsTerminated || entry.Attachment.Ordinal != request.ExpectedAttachmentGeneration
                    || !HasOwnedCommandDefaults(entry)) return ValueTask.FromResult<AgentSessionHandleId?>(null);
                handleUse = entry.Attachment.TryAcquireHandleUse();
                return ValueTask.FromResult(handleUse is null ? (AgentSessionHandleId?)null : entry.SessionHandleId);
            }, CancellationToken.None).ConfigureAwait(false);
            if (handle is null) return null;
            // Event-derived ActiveRunId is not admission authority. Only the captured provider can
            // atomically validate the unchanged expected run; no permission invalidation occurs here.
            var lifetime = new RuntimeCommandLifetime(new { Runtime = this, Use = handleUse });
            return await lifetime.RunAsync(token => _agentHub.AbortRunAsync(handle.Value, new AgentRunId(request.ExpectedRunId), token),
                executionCancellationToken, handleUse!.Attachment.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (OwnedProviderEventForwarding.HasRetention(failure))
        {
            handleUse?.Retain(failure);
            _forwarding.RetainDependencies(failure, this);
            throw;
        }
        finally { handleUse?.Dispose(); }
    }

    /// <summary>Captures immutable current-runtime facts for one session without discovery or acquisition.</summary>
    /// <param name="sessionId">The nonblank durable session identifier.</param>
    /// <param name="cancellationToken">Cancels admission or the caller's wait, not already admitted runtime-owned work.</param>
    /// <returns>A point-in-time observation; a missing entry is explicitly absent, not idle or completed.</returns>
    /// <remarks>
    /// Uses the existing session actor when present; never creates an actor, coordinator or provider, and
    /// never reads catalogs, journals or Display. Entry and transition facts are copied synchronously on
    /// that actor; attachment retirement is read through its existing owner gate. Missing-actor absence
    /// is observed at registry lookup. The result can become stale immediately. Runtime instance and
    /// attachment generation identify ownership, not state revisions or effect watermarks. Consumers
    /// must fence obsolete selection/request results themselves. Queue depth, provider quiescence,
    /// history recovery and an atomic history/original-stream handshake are not provided.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The session identifier is null.</exception>
    /// <exception cref="ArgumentException">The session identifier is empty or whitespace.</exception>
    /// <exception cref="OperationCanceledException">Admission or the caller's wait was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The runtime or session actor no longer admits queries.</exception>
    public async Task<SessionRuntimeCurrentState> GetCurrentStateAsync(string sessionId, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => GetCurrentStateOwnedBodyAsync(sessionId), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<SessionRuntimeCurrentState> GetCurrentStateOwnedBodyAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!_sessionActors.TryGet(sessionId, out var actor))
            return new(_runtimeInstanceId, sessionId, false, null);

        return await actor.QueryAsync(_ =>
        {
            return ValueTask.FromResult(CaptureCurrentState(sessionId));
        }, CancellationToken.None).ConfigureAwait(false);
    }

    // Called only on the existing session actor.
    private SessionRuntimeCurrentState CaptureCurrentState(string sessionId)
    {
        SessionRuntimeCurrentEntry? snapshot = null;
        if (_entries.TryGetValue(sessionId, out var entry))
            snapshot = new(entry.Attachment.Ordinal, entry.IsTerminated, entry.Attachment.IsRetiring,
                entry.ActiveRunId?.Value, entry.QueueDrainInProgress, entry.ProviderId.Value, entry.ProviderKey,
                entry.Model, entry.ReasoningEffort, entry.AgentPromptId, entry.PendingAgentPromptId)
            { Activity = new(entry.ActivityTimestamp, entry.ActivityEvents, entry.OmittedActivityEvents), BackgroundTasks = WithJobTasks(entry.BackgroundTasks, sessionId),
              RemoteControl = entry.RemoteControl, PromptSuggestion = entry.PromptSuggestion };
        return new(_runtimeInstanceId, sessionId, _transitions.ContainsKey(sessionId), snapshot);
    }

    /// <summary>
    /// Reads the latest bounded, typed usage event admitted by the existing attachment's actor.
    /// Does not activate a session, read history, probe a provider or establish completeness/freshness.
    /// </summary>
    /// <param name="sessionId">Exact session identifier to inspect.</param>
    /// <param name="cancellationToken">Cancels the caller's wait, not work already admitted to the actor.</param>
    /// <returns>Attachment-scoped last-observed usage or explicit absence/transition state.</returns>
    /// <exception cref="ArgumentNullException">The session identifier is null.</exception>
    /// <exception cref="ArgumentException">The session identifier is blank.</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closed.</exception>
    /// <exception cref="OperationCanceledException">The caller's wait is canceled.</exception>
    public async Task<SessionRuntimeUsageState> GetUsageStateAsync(string sessionId, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => GetUsageStateOwnedBodyAsync(sessionId), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<SessionRuntimeUsageState> GetUsageStateOwnedBodyAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!_sessionActors.TryGet(sessionId, out var actor))
            return new(_runtimeInstanceId, sessionId, false, null, false, false, null, 0);

        return await actor.QueryAsync(_ =>
        {
            var transitioning = _transitions.ContainsKey(sessionId);
            if (!_entries.TryGetValue(sessionId, out var entry))
                return ValueTask.FromResult(new SessionRuntimeUsageState(_runtimeInstanceId, sessionId,
                    transitioning, null, false, false, null, 0));
            var retiring = entry.Attachment.IsRetiring;
            return ValueTask.FromResult(new SessionRuntimeUsageState(_runtimeInstanceId, sessionId,
                transitioning, entry.Attachment.Ordinal, retiring, entry.IsTerminated,
                transitioning || retiring || entry.IsTerminated ? null : entry.LastObservedUsage,
                entry.OmittedUsageEvents));
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Counts the sessions that have a run in flight right now. It is a reading for the user (a question
    /// before the application exits), taken without waiting for any session: a run that starts or ends at
    /// this instant may or may not be counted.
    /// </summary>
    public int CountActiveRuns() => _entries.Values.Count(static entry => !entry.IsTerminated && entry.HasActiveRun);

    /// <summary>
    /// Lists what the sessions this runtime holds are doing right now: whether each runs, whether its provider
    /// works in the background, and whether its last run failed. Like <see cref="CountActiveRuns"/> it is a
    /// reading for the user, taken without waiting for any session. A session the runtime does not hold (one
    /// that was not opened or sent to since the application started) is not listed.
    /// </summary>
    /// <returns>One entry for each session the runtime holds, in no particular order.</returns>
    public IReadOnlyList<SessionRuntimeOverview> ListOverview()
        => [.. _entries.Values
            .Where(static entry => !entry.IsTerminated)
            .Select(entry => new SessionRuntimeOverview(entry.SessionId, entry.ProjectId, entry.Title,
                entry.HasActiveRun || entry.QueueDrainInProgress, entry.BackgroundTasks.Count(static task => task.Outcome is null) + Jobs.CountRunning(entry.SessionId), entry.LastRunFailed)
            { ParentSessionId = entry.ParentSessionId, SpaceId = entry.SpaceId, RestoredForRunAt = GetRestoredForRunAt(entry.SessionId), ProviderKey = entry.ProviderKey })];

    /// <summary>
    /// Lists the sessions that are at work right now, each with the folder it works in: its worktree when it has
    /// one, the folder of its project otherwise. Like <see cref="CountActiveRuns"/> it is a reading taken without
    /// waiting for any session; it answers whether a checkout can be removed or moved to another branch.
    /// </summary>
    public IReadOnlyList<SessionWorkFolder> ListBusySessionFolders()
        => [.. _entries.Values
            .Where(static entry => !entry.IsTerminated && (entry.HasActiveRun || entry.QueueDrainInProgress))
            .Select(static entry => new SessionWorkFolder(entry.SessionId, entry.WorktreeDirectory ?? entry.WorkingDirectory, entry.WorktreeDirectory is not null))];

    /// <summary>
    /// Returns whether the session's active coordinator session has an in-flight run.
    /// </summary>
    public async Task<bool> HasActiveRunAsync(SessionViewDescriptor session, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => HasActiveRunOwnedBodyAsync(session, CancellationToken.None), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<bool> HasActiveRunOwnedBodyAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.SessionId))
        {
            return false;
        }

        if (!_sessionActors.TryGet(session.SessionId, out var actor))
        {
            return false;
        }

        return await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    await Task.CompletedTask.ConfigureAwait(false);
                    return _entries.TryGetValue(session.SessionId, out var entry) && entry.HasActiveRun;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns whether the session has an active coordinator session in this runtime process.
    /// </summary>
    /// <param name="sessionId">The durable session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when this runtime owns a non-terminated coordinator session.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sessionId"/> is empty.</exception>
    public async Task<bool> HasActiveCoordinatorSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => HasActiveCoordinatorSessionOwnedBodyAsync(sessionId, CancellationToken.None), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<bool> HasActiveCoordinatorSessionOwnedBodyAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!_sessionActors.TryGet(sessionId, out var actor))
        {
            return false;
        }

        return await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    await Task.CompletedTask.ConfigureAwait(false);
                    return _entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the runs of the sessions that run now with one of some providers, as the user stops them: the providers
    /// are about to be replaced or taken away (the plugin that gives their type stops or is built again), which would
    /// end the turns with their runtime. A session that does not run is left as it is.
    /// </summary>
    /// <param name="providerKeys">The keys of the providers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sessions whose run was stopped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="providerKeys"/> is <see langword="null"/>.</exception>
    public async Task<IReadOnlyList<string>> AbortRunsOfProvidersAsync(IReadOnlyCollection<string> providerKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerKeys);
        if (providerKeys.Count == 0) return [];
        var running = ListOverview()
            .Where(overview => overview.Running && overview.ProviderKey is { } key && providerKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            .Select(static overview => overview.SessionId)
            .ToArray();
        foreach (var sessionId in running)
        {
            await AbortAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }

        return running;
    }

    /// <summary>
    /// Aborts active work in the session coordinator session.
    /// </summary>
    public async Task AbortAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_forwarding.IsClosed) return;
        await AdmitAsync(() => AbortOwnedBodyAsync(sessionId, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AbortOwnedBodyAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_disposed)
        {
            return;
        }

        SessionActorCommandResult result;
        RuntimeSessionEntry? capturedEntry = null;
        OwnedProviderEventForwarding.Use? handleUse = null;
        var admission = new OwnedSessionCommandService.OriginalInvocation();
        RuntimeAbortLifetime? lifetime = null;
        try
        {
            var actor = GetActorForWork(sessionId);
            result = await admission.RunAsync(() => actor.ExecuteReservedAsync(
                    async actorCancellationToken =>
                    {
                        var entry = await GetEntryAsync(sessionId, actorCancellationToken).ConfigureAwait(false);
                        handleUse = entry.Attachment.TryAcquireHandleUse()
                            ?? throw new InvalidOperationException("The coordinator attachment is retiring.");
                        capturedEntry = entry;
                    },
                    CancellationToken.None).AsTask())
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    result.Message ?? $"Failed to abort session '{sessionId}'.", result.Exception);
            }
            lifetime = new RuntimeAbortLifetime(new { Runtime = this, Entry = capturedEntry, Use = handleUse, Admission = admission });
            await lifetime.RunAsync(() => Permissions.InvalidateOwnedAttachmentAsync(capturedEntry!.Attachment),
                token => _agentHub.AbortAsync(capturedEntry!.SessionHandleId, token),
                cancellationToken, capturedEntry!.Attachment.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (OwnedProviderEventForwarding.HasRetention(failure))
        {
            var dependencies = new { Runtime = this, Entry = capturedEntry, Use = handleUse, Admission = admission, Lifetime = lifetime };
            var retained = new AgentDependencyRetentionException("runtime abort", "retained use", [failure], dependencies);
            handleUse?.Retain(retained);
            _forwarding.RetainDependencies(retained, dependencies);
            throw retained;
        }
        catch (Exception ex) when (_disposed && ex is ObjectDisposedException or ChannelClosedException)
        {
            return;
        }
        finally { handleUse?.Dispose(); }
    }

    private static string? BuildParentNotificationGuidance(SessionViewDescriptor session)
        => string.IsNullOrWhiteSpace(session.ParentSessionId)
            ? null
            : $"Parent session: `{session.ParentSessionId}`. CodeAlta auto-forwards your final assistant reply. For progress/intermediate parent updates, include `<notify-parent>update text</notify-parent>` in an assistant reply.";

    // What a session titled by its model is asked. The session adds it to the developer instructions of a run
    // while that is its mode and nobody named it, so a change of mode reaches the next prompt without attaching the
    // session again. It starts with words that name it: Claude Code is told which paragraph no longer applies by them.
    internal const string SessionTitleGuidance =
        "Session title: you choose the short title CodeAlta lists this session under. Begin the first message you write in this session with a line that holds only `"
        + AgentSessionTitleTag.Opening + "Title in a few words" + AgentSessionTitleTag.Closing
        + "` (at most 60 characters, plain text, in the language of the user), before any other text and before any tool call. Write such a line again later only when the main subject of the session has changed. CodeAlta removes these lines from what is shown: never mention or explain them.";

    // What a session the user writes in is asked while suggestions are on ([sessions] prompt_suggestions). The session
    // adds it after the title paragraph, so a change of the setting reaches the next prompt. It starts with words that
    // name it, as the title paragraph does.
    internal const string PromptSuggestionGuidance =
        "Next prompt suggestion: end your final answer of each turn with a line that holds only `"
        + AgentNextPromptTag.Opening + "Next prompt" + AgentNextPromptTag.Closing
        + "`: the prompt the user is most likely to send next, written as they would write it, in their language, in a few words (such as `go ahead`, `run the tests` or `commit it`). Leave the line out when no next step is obvious, and in a message that is not your final answer. CodeAlta removes this line from what is shown and offers it to the user: never mention or explain it.";

    // An attachment that follows the setting takes the mode the setting has now: a change of the setting reaches a
    // session with its next prompt, without attaching it again. Runs on the actor of the session.
    private async ValueTask SyncTitleModeAsync(RuntimeSessionEntry entry, CancellationToken cancellationToken)
    {
        var mode = entry.OwnTitleMode ?? _configStore.LoadGlobalSessionTitleMode();
        if (mode == entry.TitleMode) return;
        try
        {
            if (await _agentHub.SetTitleModeAsync(entry.SessionHandleId, mode, releaseTitle: false, cancellationToken).ConfigureAwait(false))
                entry.TitleMode = mode;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // The attachment is going away: the one that replaces it reads the setting.
        }
    }

    private static string? AppendPromptPart(string? baseText, string? additionalText)
    {
        if (string.IsNullOrWhiteSpace(additionalText))
        {
            return baseText;
        }

        if (string.IsNullOrWhiteSpace(baseText))
        {
            return additionalText.Trim();
        }

        return string.Concat(baseText.TrimEnd(), Environment.NewLine, Environment.NewLine, additionalText.Trim());
    }

    private async Task<SessionViewLocalState?> ReadLatestLocalStateAsync(
        string sessionId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _sessionViewCatalog.JournalStore.ReadLatestStateAsync(sessionId, createdAt, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Answers whether the user reviews the changes of a session before an agent commits them: its own choice when
    /// it has one, else the setting. A session that is not attached follows the setting here; read its own choice
    /// with <see cref="GetReviewBeforeCommitAsync"/>.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>True when the changes wait for an approved review.</returns>
    public bool GetEffectiveReviewBeforeCommit(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return EffectiveReviewBeforeCommit(_entries.TryGetValue(sessionId, out var entry) ? entry.OwnReviewBeforeCommit : null);
    }

    /// <summary>
    /// Reads the setting <c>[review] before_commit</c> as it applies to the sessions of this runtime: off in a headless
    /// runtime (<see cref="Headless"/>), where nobody reviews.
    /// </summary>
    /// <returns>Whether sessions that follow the setting review their changes before commit.</returns>
    internal bool LoadReviewBeforeCommitSetting() => !Headless && _configStore.LoadGlobalReviewBeforeCommit();

    // What applies of a session's own choice: the choice, else the setting; never a review in a headless runtime.
    private bool EffectiveReviewBeforeCommit(bool? own) => !Headless && (own ?? _configStore.LoadGlobalReviewBeforeCommit());

    /// <summary>
    /// Gets the own choice of an attached session about the review before commit; null when it follows the setting or
    /// is not attached.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The choice, or null.</returns>
    public bool? GetOwnReviewBeforeCommit(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _entries.TryGetValue(sessionId, out var entry) ? entry.OwnReviewBeforeCommit : null;
    }

    /// <summary>Reads the choice of a session about the review before commit, and what applies.</summary>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>Its own choice (null: it follows the setting), and what applies.</returns>
    public async Task<(bool? Own, bool Effective)> GetReviewBeforeCommitAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        bool? own;
        if (_entries.TryGetValue(session.SessionId, out var entry))
        {
            own = entry.OwnReviewBeforeCommit;
        }
        else
        {
            own = (await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false))?.ReviewBeforeCommit
                ?? session.ReviewBeforeCommit;
        }

        return (own, EffectiveReviewBeforeCommit(own));
    }

    /// <summary>
    /// Saves whether the user reviews the changes of a session before an agent commits them, or that it follows the
    /// setting. It applies at once: the commit guard reads it at each command, and the agent is told with its next
    /// prompt. Nothing is attached again and the instructions of the session do not change.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="reviewBeforeCommit">The choice; null follows the setting.</param>
    /// <exception cref="ObjectDisposedException">The runtime is closing.</exception>
    public Task SetReviewBeforeCommitAsync(SessionViewDescriptor session, bool? reviewBeforeCommit)
    {
        ArgumentNullException.ThrowIfNull(session);
        return AdmitAsync(async () =>
        {
            ObjectDisposedException.ThrowIf(_forwarding.IsClosed, this);
            using var lease = HoldForWrite(session.SessionId);
            await GetActorForWork(session.SessionId).QueryAsync(async actorCancellationToken =>
            {
                if (_entries.TryGetValue(session.SessionId, out var entry)) entry.OwnReviewBeforeCommit = reviewBeforeCommit;
                var localState = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, actorCancellationToken).ConfigureAwait(false);
                if (localState?.ReviewBeforeCommit != reviewBeforeCommit)
                {
                    if (localState is null)
                    {
                        localState = new SessionViewLocalState();
                        CopySessionMetadata(session, localState);
                    }

                    localState.ReviewBeforeCommit = reviewBeforeCommit;
                    await _sessionViewCatalog.JournalStore.AppendStateAsync(session, localState, actorCancellationToken).ConfigureAwait(false);
                }

                session.ReviewBeforeCommit = reviewBeforeCommit;
                return true;
            }, CancellationToken.None).ConfigureAwait(false);
        }, CancellationToken.None);
    }

    /// <summary>Gets the configuration this runtime reads its settings from.</summary>
    internal CodeAltaConfigStore ConfigStore => _configStore;

    /// <summary>
    /// Gets or sets the reviews of the host: a session that reviews before commit may commit the work tree its last
    /// review approved. Without them, such a session commits nothing.
    /// </summary>
    public CodeAlta.Catalog.Reviews.ReviewService? Reviews { get; set; }

    internal const string CommitRefusal = "The user reviews the changes of this session before they are committed, and no review approved the work tree as it is now. "
        + "Do not commit: when the work is done and verified, run `alta review request changes` and end your turn; commit after the review approves the changes, and only them.";

    // Refuses a commit while the session reviews before commit, unless the work tree is the one its last review approved.
    private Agent.Runtime.Tools.AgentCommandGuard CreateCommandGuard(string sessionId)
        => async (command, workingDirectory, cancellationToken) =>
        {
            if (!Agent.Runtime.Tools.AgentGitCommands.IsCommit(command) || !GetEffectiveReviewBeforeCommit(sessionId))
            {
                return null;
            }

            if (Reviews is { } reviews && _entries.TryGetValue(sessionId, out var entry) && entry.ProjectId is { } projectId
                && await _projectCatalog.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false) is { } project)
            {
                try
                {
                    if (await reviews.IsApprovedAsync(new CodeAlta.Catalog.Reviews.ReviewScope(project, entry.WorktreeDirectory), cancellationToken).ConfigureAwait(false))
                    {
                        return null;
                    }
                }
                catch (CodeAlta.Catalog.Reviews.ReviewException)
                {
                    // A checkout whose reviews cannot be read has no approval.
                }
            }

            return CommitRefusal;
        };

    // The note a run carries about the review before commit: what applies, when the agent was told something else,
    // or nothing yet while the review applies. Null when there is nothing to tell.
    private bool? ReviewNoteFor(RuntimeSessionEntry entry)
    {
        var effective = EffectiveReviewBeforeCommit(entry.OwnReviewBeforeCommit);
        return entry.ReviewNoted is { } told ? told == effective ? null : effective : effective ? true : null;
    }

    internal static string ReviewNoteText(bool on)
        => on
            ? "<codealta-context>Review before commit: on. The user reviews the changes of this session before they are committed. Do not commit by yourself: when the work is done and verified, run `alta review request changes` and end your turn; commit only what a review approves.</codealta-context>"
            : "<codealta-context>Review before commit: off. Commit as the instructions of the user and of the repository say.</codealta-context>";

    // What the agent was told is kept with the session, so that a session that comes back is not told twice.
    private async Task RecordReviewNotedAsync(SessionViewDescriptor session, RuntimeSessionEntry entry, bool noted)
    {
        entry.ReviewNoted = noted;
        try
        {
            await GetActorForWork(session.SessionId).QueryAsync(async actorCancellationToken =>
            {
                var localState = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, actorCancellationToken).ConfigureAwait(false);
                if (localState is not null && localState.ReviewNoted != noted)
                {
                    localState.ReviewNoted = noted;
                    await _sessionViewCatalog.JournalStore.AppendStateAsync(session, localState, actorCancellationToken).ConfigureAwait(false);
                }

                return true;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException)
        {
            // The next run of a session that comes back tells it again, which is harmless.
        }
    }

    private static void CopySessionMetadata(SessionViewDescriptor session, SessionViewLocalState localState)
    {
        localState.ProviderKey = session.ResolvedProviderKey;
        localState.ModelId = session.ModelId;
        localState.ReasoningEffort = session.ReasoningEffort;
        localState.AgentPromptId = NormalizeOptionalText(session.AgentPromptId);
        // Whether the session is archived is kept as it was read: only an archive or a restore changes it. The status
        // of a descriptor says it too of an attachment that ended, which is no archive.
        localState.MessageCount = session.MessageCount;
        localState.ParentSessionId = session.ParentSessionId;
        localState.CreatedBy = session.CreatedBy;
    }

    private static int QueuedPromptCount(SessionViewLocalState localState)
        => localState.QueuedPrompts.Count(static prompt => SessionViewQueuedPrompt.IsPendingState(prompt.State));

    internal const string InterruptedSendRetried = "CodeAlta closed while this prompt was being sent: it is sent again.";
    internal const string InterruptedSendFailed = "CodeAlta closed twice while this prompt was being sent: it is not sent again.";

    /// <summary>
    /// Ends the sends that the end of another process cut: a prompt this runtime finds <c>submitting</c>, marked by
    /// another runtime or by none, is no longer sent by anybody. It is queued again, so that it is sent once more; a
    /// prompt whose send was already cut once fails instead, so that a prompt that ends CodeAlta is not sent at every
    /// start. Runs under the actor of the session, on the state it is about to write.
    /// </summary>
    /// <returns>The prompts it changed, in the order of the queue.</returns>
    private List<SessionViewQueuedPrompt> RecoverInterruptedSends(SessionViewLocalState localState, DateTimeOffset timestamp)
    {
        var self = _runtimeInstanceId.ToString("N");
        var recovered = new List<SessionViewQueuedPrompt>();
        foreach (var prompt in localState.QueuedPrompts)
        {
            if (!string.Equals(prompt.State, "submitting", StringComparison.OrdinalIgnoreCase)
                || string.Equals(prompt.SubmittingRuntime, self, StringComparison.Ordinal))
            {
                continue;
            }

            var interrupted = (prompt.InterruptedSends ?? 0) + 1;
            prompt.InterruptedSends = interrupted;
            prompt.SubmittingRuntime = null;
            prompt.DrainedAt = timestamp;
            (prompt.State, prompt.LastError) = interrupted > 1 ? ("failed", InterruptedSendFailed) : ("queued", InterruptedSendRetried);
            recovered.Add(prompt);
        }

        return recovered;
    }

    private static string CreatePromptPreview(string prompt)
        => prompt.Length <= 160 ? prompt : prompt[..160];

    private static void TrimLocalStateHistory(SessionViewLocalState localState)
    {
        const int MaxPromptProvenanceRecords = 200;
        if (localState.PromptProvenance.Count > MaxPromptProvenanceRecords)
        {
            localState.PromptProvenance.RemoveRange(0, localState.PromptProvenance.Count - MaxPromptProvenanceRecords);
        }

        const int MaxQueuedPromptRecords = 200;
        if (localState.QueuedPrompts.Count > MaxQueuedPromptRecords)
        {
            localState.QueuedPrompts.RemoveRange(0, localState.QueuedPrompts.Count - MaxQueuedPromptRecords);
        }
    }

    private void ValidateDiscoveryPaths(SessionViewDescriptor? session, SessionExecutionOptions? options, ProjectDescriptor? project = null)
    {
        if (_discoveryScope is null)
        {
            return;
        }

        if (session?.WorkingDirectory is not null)
        {
            _discoveryScope.ValidateProjectPath(session.WorkingDirectory, nameof(session.WorkingDirectory));
        }

        if (options is not null)
        {
            _discoveryScope.ValidateProjectPath(options.WorkingDirectory, nameof(options.WorkingDirectory));
            foreach (var root in options.ProjectRoots)
            {
                _discoveryScope.ValidateProjectPath(root, nameof(options.ProjectRoots));
            }
        }

        ValidateDiscoveryProjectRoot(project?.ProjectPath);
    }

    private void ValidateDiscoveryProjectRoot(string? projectRoot)
    {
        if (_discoveryScope is not null && projectRoot is not null)
        {
            _discoveryScope.ValidateProjectPath(projectRoot, nameof(projectRoot));
        }
    }

    private SkillCatalogQuery BuildSkillCatalogQuery(ProjectDescriptor? project, IReadOnlyList<string> projectRoots, SpaceFilter space)
    {
        if (_discoveryScope is not null)
        {
            ValidateDiscoveryProjectRoot(project?.ProjectPath);
            foreach (var root in projectRoots)
            {
                _discoveryScope.ValidateProjectPath(root, nameof(projectRoots));
            }
        }

        return SkillCatalogQuery.ForSession(
            _configStore,
            _catalogOptions.GlobalRoot,
            UserHomeRoot,
            project?.ProjectPath,
            projectRoots,
            space);
    }

    /// <summary>
    /// Gets what the space of a session has: the space it records, else the only space of its project, else the
    /// default space (see <see cref="SpaceCatalog.ResolveSessionSpace"/>), with the assignments of the global
    /// configuration. Every resource a session is offered goes through it before its enablement.
    /// </summary>
    /// <param name="recordedSpaceId">The space the session records, or <see langword="null"/>.</param>
    /// <param name="project">The project of the session, or <see langword="null"/> for a chat.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the space of the session has.</returns>
    /// <exception cref="InvalidDataException">The global configuration cannot be parsed.</exception>
    /// <exception cref="IOException">The global configuration cannot be read.</exception>
    public async Task<SpaceFilter> GetSpaceFilterAsync(string? recordedSpaceId, ProjectDescriptor? project, CancellationToken cancellationToken = default)
    {
        var spaces = await _spaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        return SpaceFilter.Load(_configStore, SpaceCatalog.ResolveSessionSpace(recordedSpaceId, project, spaces));
    }

    /// <summary>
    /// Gets what the space of a session has, by its id: see
    /// <see cref="GetSpaceFilterAsync(string?, ProjectDescriptor?, CancellationToken)"/>. A session that is not known is
    /// read as in the default space.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the space of the session has.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    /// <exception cref="InvalidDataException">The global configuration cannot be parsed.</exception>
    /// <exception cref="IOException">The global configuration cannot be read.</exception>
    public async Task<SpaceFilter> GetSessionSpaceFilterAsync(string sessionId, CancellationToken cancellationToken = default)
        => SpaceFilter.Load(_configStore, await GetSessionSpaceIdAsync(sessionId, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Gets the built-in tools a session does without, as the settings of the user, of its space and of its agent
    /// prompt say them now. A session that is not known has the settings of the user.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The names of the tools.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    /// <exception cref="InvalidDataException">The configuration cannot be parsed.</exception>
    public async Task<IReadOnlySet<string>> GetDisabledToolsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        // What the settings say now: an attachment that runs with less is attached again at its next turn.
        var session = await ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return _configStore.LoadGlobalDisabledTools();
        var project = await ResolveProjectAsync(session, cancellationToken).ConfigureAwait(false);
        if (!_entries.TryGetValue(sessionId, out var entry) || entry.IsTerminated)
        {
            return _instructionTemplateProvider.ResolveDisabledTools(session, project, session.AgentPromptId);
        }

        // An attached session does without what its attachment runs without until its next turn, and from then on
        // without what the agent prompt it was given meanwhile says: all of it, so that nothing it does now gives
        // it back what one of them takes away.
        var disabled = new HashSet<string>(_instructionTemplateProvider.ResolveDisabledTools(session, project, entry.AgentPromptId), StringComparer.Ordinal);
        disabled.UnionWith(entry.DisabledTools);
        if (NormalizeOptionalText(entry.PendingAgentPromptId) is { } pending)
        {
            disabled.UnionWith(_instructionTemplateProvider.ResolveDisabledTools(session, project, pending));
        }

        return disabled;
    }

    /// <summary>
    /// Gets the built-in tools a session would do without under an agent prompt, as the settings of the user, of its
    /// space and of that prompt say them now: a session given another prompt, or one about to be created, whose
    /// descriptor names its project and its space.
    /// </summary>
    /// <param name="session">The session, or what a session about to be created records.</param>
    /// <param name="agentPromptId">The agent prompt; <see langword="null"/> for the one the session records.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The names of the tools.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The configuration cannot be parsed.</exception>
    public async Task<IReadOnlySet<string>> GetDisabledToolsAsync(SessionViewDescriptor session, string? agentPromptId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var project = await ResolveProjectAsync(session, cancellationToken).ConfigureAwait(false);
        return _instructionTemplateProvider.ResolveDisabledTools(session, project, NormalizeOptionalText(agentPromptId) ?? session.AgentPromptId);
    }

    /// <summary>
    /// Gets the fewest built-in tools a session may be doing without while it is handed a prompt: only those that
    /// the turn it runs now does without, and that its settings say under its current agent prompt, the one it was
    /// given for its next turn and the one the prompt asks for. A session that is not attached does without what its
    /// settings say under that prompt.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="agentPromptId">The agent prompt the prompt it is handed asks for; <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The names of the tools.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The configuration cannot be parsed.</exception>
    public async Task<IReadOnlySet<string>> GetFewestDisabledToolsAsync(SessionViewDescriptor session, string? agentPromptId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var project = await ResolveProjectAsync(session, cancellationToken).ConfigureAwait(false);
        var requested = NormalizeOptionalText(agentPromptId);
        if (!_entries.TryGetValue(session.SessionId, out var entry) || entry.IsTerminated)
        {
            return _instructionTemplateProvider.ResolveDisabledTools(session, project, requested ?? session.AgentPromptId);
        }

        var fewest = new HashSet<string>(entry.DisabledTools, StringComparer.Ordinal);
        // The current prompt (null for the default one), then the pending and the requested ones when there are.
        fewest.IntersectWith(_instructionTemplateProvider.ResolveDisabledTools(session, project, entry.AgentPromptId));
        foreach (var prompt in new[] { NormalizeOptionalText(entry.PendingAgentPromptId), requested })
        {
            if (prompt is not null)
            {
                fewest.IntersectWith(_instructionTemplateProvider.ResolveDisabledTools(session, project, prompt));
            }
        }

        return fewest;
    }

    /// <summary>
    /// Gets the space a session is in: the space it records, else the only space of its project, else the default
    /// space. A session that is not known is read as in the default space. Reads no configuration.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identifier of an existing space, or <see cref="SpaceDescriptor.DefaultId"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public async Task<string> GetSessionSpaceIdAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var record = await GetSessionSpaceAsync(sessionId, cancellationToken).ConfigureAwait(false);
        ProjectDescriptor? project = null;
        if (string.IsNullOrWhiteSpace(record?.SpaceId) && !string.IsNullOrWhiteSpace(record?.ProjectId))
        {
            project = (await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(candidate => string.Equals(candidate.Id, record.ProjectId, StringComparison.OrdinalIgnoreCase));
        }

        return SpaceCatalog.ResolveSessionSpace(record?.SpaceId, project, await _spaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Detaches and disposes the active coordinator session for a session when present.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when an active coordinator session was detached; otherwise <see langword="false"/>.</returns>
    public async Task<bool> DetachRuntimeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => DetachOwnedBodyAsync(sessionId), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    // onlyIfIdle: a session nobody shows is detached only while it still has nothing to do, without waiting for anything.
    private async Task<bool> DetachOwnedBodyAsync(string sessionId, bool onlyIfIdle = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var actor = GetActorForWork(sessionId);
        while (true)
        {
            var preparation = await actor.QueryAsync(async token =>
            {
                if (_transitions.TryGetValue(sessionId, out var current))
                    return (Claimed: false, Work: onlyIfIdle ? null : (Task?)current);
                if (!_entries.TryGetValue(sessionId, out var entry))
                    return (Claimed: false, Work: (Task?)null);
                if (onlyIfIdle && !await IsStillIdleOnActorAsync(entry, token).ConfigureAwait(false))
                    return (Claimed: false, Work: (Task?)null);
                var retirement = _forwarding.RetireAsync(entry.Attachment);
                var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task? ticket = null;
                var work = _forwarding.RunAsync(async () =>
                {
                    await launch.Task.ConfigureAwait(false);
                    try { await retirement.ConfigureAwait(false); }
                    finally
                    {
                        await actor.QueryAsync(token =>
                        {
                            if (_entries.TryGetValue(sessionId, out var active) && ReferenceEquals(active, entry)
                                && _entries.TryRemove(sessionId, out var removedEntry))
                                ReleaseEntryLease(sessionId);
                            if (_transitions.TryGetValue(sessionId, out var currentTicket) && ReferenceEquals(currentTicket, ticket))
                                _transitions.TryRemove(sessionId, out var removedTransition);
                            return ValueTask.FromResult(true);
                        }, CancellationToken.None).ConfigureAwait(false);
                    }
                }, external: false);
                _transitions[sessionId] = work;
                ticket = work;
                launch.TrySetResult();
                return (Claimed: true, Work: (Task?)work);
            }, CancellationToken.None).ConfigureAwait(false);
            if (preparation.Work is null) return false;
            await preparation.Work.ConfigureAwait(false);
            if (preparation.Claimed) return true;
        }
    }

    /// <summary>
    /// Triggers a manual compaction for a session coordinator session.
    /// </summary>
    /// <exception cref="InvalidOperationException">The target or its lineage is being retired or has been retired, or compaction admission failed.</exception>
    public async Task CompactAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        CancellationToken cancellationToken = default)
        => await AdmitAsync(() => CompactOwnedBodyAsync(session, options, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task CompactOwnedBodyAsync(SessionViewDescriptor session, SessionExecutionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);

        await WithRetentionWorkAsync(async () =>
        {
        ReserveSessionIdentity(session);
        GetActorForWork(session.SessionId);
        var result = await _sessionActors.GetOrCreate(session.SessionId).ExecuteAsync(
                async actorCancellationToken =>
                {
                    _events.TryPublish(new SessionHostEvent(
                        session.SessionId,
                        DateTimeOffset.UtcNow,
                        AgentSessionUpdateKind.CompactionStarted,
                        $"Manual compaction requested for '{session.Title}'."));

                    await Task.CompletedTask.ConfigureAwait(false);
                },
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                result.Message ?? $"Failed to compact session '{session.SessionId}'.",
                result.Exception);
        }

        OwnedProviderEventForwarding.Use? handleUse = null;
        try
        {
            while (handleUse is null)
            {
                var entry = await ResolveCoordinatorEntryAsync(session, options).ConfigureAwait(false);
                handleUse = await GetActorForWork(session.SessionId).QueryAsync(_ =>
                {
                    var use = _entries.TryGetValue(session.SessionId, out var current) && ReferenceEquals(current, entry)
                        && entry.Matches(options, NormalizeOptionalText(entry.PendingAgentPromptId)
                            ?? NormalizeOptionalText(options.AgentPromptId) ?? NormalizeOptionalText(session.AgentPromptId))
                        ? entry.Attachment.TryAcquireHandleUse() : null;
                    if (use is not null) entry.PendingAgentPromptId = null;
                    return ValueTask.FromResult(use);
                }, CancellationToken.None).ConfigureAwait(false);
                if (handleUse is null) continue;
                using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.Attachment.Cancellation.Token);
                var outcome = await AwaitRetentionAwareAsync(_agentHub.CompactAsync(entry.SessionHandleId, execution.Token)).ConfigureAwait(false);
                if (outcome is not null && ShouldPublishHostCompactionOutcome(outcome))
                {
                    _events.TryPublish(new SessionHostEvent(
                        session.SessionId,
                        DateTimeOffset.UtcNow,
                        AgentSessionUpdateKind.CompactionCompleted,
                        outcome.Message ?? (outcome.Success ? "Manual compaction completed." : "Manual compaction failed.")));
                }
            }
        }
        catch (Exception failure) when (OwnedProviderEventForwarding.HasRetention(failure))
        {
            handleUse?.Retain(failure);
            throw;
        }
        finally { handleUse?.Dispose(); }
        return true;
        }, session.SessionId, session.ParentSessionId, session.CreatedBy?.SourceSessionId).ConfigureAwait(false);
    }

    private static bool ShouldPublishHostCompactionOutcome(AgentCompactionOutcome outcome)
        => outcome is
        {
            MessagesRemoved: null,
            TokensRemoved: null,
            PreCompactionTokens: null,
            PostCompactionTokens: null,
        };

    /// <summary>
    /// Gets sanitized history for a session, reusing an active coordinator session when present.
    /// </summary>
    /// <param name="session">The session descriptor.</param>
    /// <param name="options">Execution options used to resume the coordinator session when it is not already active.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sanitized session event history.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session" /> or <paramref name="options" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">The target or its lineage is being retired or has been retired.</exception>
    /// <remarks>
    /// History loading is a read operation. When a coordinator session is already attached, this method intentionally
    /// does not compare or apply <paramref name="options" /> because replacing a live attachment can cancel in-flight work.
    /// </remarks>
    public async Task<IReadOnlyList<AgentEvent>> GetOrResumeHistoryAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        CancellationToken cancellationToken = default)
        => await AdmitAsync(() => GetOrResumeHistoryOwnedBodyAsync(session, options, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<AgentEvent>> GetOrResumeHistoryOwnedBodyAsync(
        SessionViewDescriptor session, SessionExecutionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        return await WithRetentionWorkAsync(async () =>
        {
        while (true)
        {
            var entry = await ResolveCoordinatorEntryAsync(session, options, history: true).ConfigureAwait(false);
            var use = await GetActorForWork(session.SessionId).QueryAsync(_ =>
                ValueTask.FromResult(_entries.TryGetValue(session.SessionId, out var current) && ReferenceEquals(current, entry)
                    ? entry.Attachment.TryAcquireHandleUse() : null), CancellationToken.None).ConfigureAwait(false);
            if (use is null) continue;
            using (use)
            using (var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.Attachment.Cancellation.Token))
            {
                try
                {
                    return await GetProjectedHistoryAsync(entry, execution.Token).ConfigureAwait(false);
                }
                catch (Exception failure) when (OwnedProviderEventForwarding.HasRetention(failure))
                {
                    use.Retain(failure);
                    throw;
                }
            }
        }
        }, session.SessionId, session.ParentSessionId, session.CreatedBy?.SourceSessionId).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets sanitized history for an active session.
    /// </summary>
    public async Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(string sessionId, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => GetHistoryOwnedBodyAsync(sessionId, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<AgentEvent>> GetHistoryOwnedBodyAsync(string sessionId, CancellationToken cancellationToken)
    {
        var actor = GetActorForWork(sessionId);
        var selected = await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    var entry = await GetEntryAsync(sessionId, actorCancellationToken).ConfigureAwait(false);
                    var use = entry.Attachment.TryAcquireHandleUse()
                        ?? throw new InvalidOperationException("The coordinator attachment is retiring.");
                    return (Entry: entry, Use: use);
                },
                CancellationToken.None)
            .ConfigureAwait(false);
        using (selected.Use)
        using (var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, selected.Entry.Attachment.Cancellation.Token))
            return await GetProjectedHistoryAsync(selected.Entry, execution.Token).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<AgentEvent>> GetProjectedHistoryAsync(
        RuntimeSessionEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var history = await AwaitRetentionAwareAsync(_agentHub.GetSessionHistoryAsync(entry.SessionHandleId, cancellationToken)).ConfigureAwait(false);
        return await GetActorForWork(entry.SessionId).QueryAsync(async _ =>
        {
            await Task.CompletedTask.ConfigureAwait(false);
            return entry.Projector.ProjectHistory(history);
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_identityGate)
        {
        _disposed = true;
        _sessionViewCatalog.JournalStore.StateAppended -= OnSessionStateAppended;
        // The commands of the jobs are ended first: none tells its end to a session that is closing.
        var jobs = Jobs.DisposeAsync().AsTask();
        return new ValueTask(Task.WhenAll(jobs, _forwarding.CloseAsync(
            () => Task.WhenAll(Permissions.DisposeAsync().AsTask(), ConversationHolds.DisposeAsync().AsTask()),
            async () =>
            {
                await _sessionActors.DisposeAsync().ConfigureAwait(false);
                _entries.Clear();
                // Nothing writes for the sessions any more: another process may take them.
                DisposeLeases();
            }, _events.Complete)));
        }
    }

    // Where the file tools of a session without the shell write: the folders it works in and its scratchpad, not the
    // folders of CodeAlta (its configuration, its plugins, its prompts) nor the settings of git and of coding agents.
    // Null for a session that has the shell, which could change all of it with a command.
    private Agent.Runtime.Tools.AgentWriteGuard? CreateWriteGuard(IReadOnlySet<string>? disabledTools, string? workingDirectory, string? worktree, IReadOnlyList<string>? projectRoots)
    {
        if (disabledTools?.Contains(PromptToolConditions.ShellCommand) != true)
        {
            return null;
        }

        string?[] roots = [workingDirectory, worktree, .. projectRoots ?? []];
        string?[] codeAlta = [_catalogOptions.GlobalRoot, _catalogOptions.StateRoot];
        return new Agent.Runtime.Tools.AgentWriteGuard(
            roots.Where(static root => !string.IsNullOrWhiteSpace(root))!,
            codeAlta.Where(static root => !string.IsNullOrWhiteSpace(root))!,
            !string.IsNullOrWhiteSpace(ScratchpadRoot) ? [ScratchpadRoot]
                : string.IsNullOrWhiteSpace(_catalogOptions.StateRoot) ? [] : [Path.Combine(_catalogOptions.StateRoot, "scratchpad")],
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    private async Task<ProjectDescriptor?> ResolveProjectAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.ProjectRef))
        {
            return null;
        }

        return await _projectCatalog.GetByIdAsync(session.ProjectRef, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RuntimeSessionEntry> GetEntryAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        }

        await Task.CompletedTask.ConfigureAwait(false);
        if (!_entries.TryGetValue(sessionId, out var entry))
        {
            throw new InvalidOperationException($"Session '{sessionId}' does not have an active coordinator session.");
        }

        return entry;
    }

    private async Task<RuntimeSessionEntry> GetActiveRuntimeSessionForSteeringAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId))
        {
            throw new InvalidOperationException("Cannot steer a session without an active coordinator session.");
        }

        await Task.CompletedTask.ConfigureAwait(false);
        if (!_entries.TryGetValue(session.SessionId, out var entry) || entry.IsTerminated)
        {
            throw new InvalidOperationException(
                $"Session '{session.SessionId}' does not have an active coordinator session to steer.");
        }

        if (!string.Equals(entry.ProviderId.Value, options.ProviderId.Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Session '{session.SessionId}' active coordinator session does not match the requested provider.");
        }

        if (!entry.HasActiveRun)
        {
            throw new InvalidOperationException(
                $"Session '{session.SessionId}' does not have an active coordinator run to steer.");
        }

        return entry;
    }

    private async Task<bool> MarkActiveRunIfStillInFlightAsync(
        string sessionId,
        AgentRunId runId,
        DateTimeOffset runStartedAt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        if (!_sessionActors.TryGet(sessionId, out var actor))
        {
            return false;
        }

        try
        {
            return await actor.QueryAsync(
                    _ =>
                    {
                        if (_entries.TryGetValue(sessionId, out var entry))
                        {
                            return ValueTask.FromResult(entry.MarkActiveRunIfStillInFlight(runId, runStartedAt));
                        }

                        return ValueTask.FromResult(false);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException) when (_disposed)
        {
            return false;
        }
    }

    private async Task<AgentRunId?> ClearActiveRunAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !_sessionActors.TryGet(sessionId, out var actor))
        {
            return null;
        }

        try
        {
            return await actor.QueryAsync(
                    _ =>
                    {
                        if (_entries.TryGetValue(sessionId, out var entry))
                        {
                            return ValueTask.FromResult(entry.ClearActiveRun());
                        }

                        return ValueTask.FromResult<AgentRunId?>(null);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (InvalidOperationException) when (_disposed)
        {
            return null;
        }
    }

    private Task PostAgentEventToActorAsync(
        SessionActor actor,
        string sessionId,
        EventProjector projector,
        AgentEvent @event,
        bool arrivedOnOpenActivity)
        => _forwarding.Forward(projector.Entry!.Attachment,
            use => PostAgentEventToActorCoreAsync(actor, sessionId, projector, @event, arrivedOnOpenActivity, use));

    private async Task PostAgentEventToActorCoreAsync(
        SessionActor actor, string sessionId, EventProjector projector, AgentEvent @event, bool arrivedOnOpenActivity,
        OwnedProviderEventForwarding.Use projectionUse)
    {
        var projectId = projector.Entry!.ProjectId;
        var workingDirectory = projector.Entry.WorkingDirectory;
        AgentEvent? published = null;
        IReadOnlyList<ParentNotificationWork> notifications = [];
        IDisposable? parentNotificationClaim = null;
        var retainParentNotificationClaim = false;
        var actorChoresCompleted = false;
        Action<HeldRunFailure>? fallback = null;
        HeldRunFailure? heldFailure = null;
        var publication = new LiveEventPublication(new { Runtime = this, Projector = projector, Use = projectionUse });
        try
        {
            await publication.CompleteAsync(mark =>
            {
                // Capture admission before the mailbox handoff, Project publication, or final-answer
                // candidate update. Entry lineage is immutable. The original forwarding operation owns
                // this claim, not an observer's wait or the use released before independent delivery.
                if (!string.IsNullOrWhiteSpace(projector.Entry!.ParentSessionId)
                    && @event is AgentContentCompletedEvent { Kind: AgentContentKind.Assistant }
                        or AgentErrorEvent or AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle })
                    parentNotificationClaim = ClaimRetentionWork(sessionId, projector.Entry.ParentSessionId,
                        projector.Entry.CreatedBy?.SourceSessionId);
                return actor.QueryAsync(async _ =>
                {
                    if (arrivedOnOpenActivity) ObserveAdmittedActivity(sessionId, projector.Entry!, @event);
                    var sanitized = projector.Project(@event);
                    ObserveAdmittedUsage(sessionId, projector.Entry!, sanitized);
                    published = sanitized;
                    if (sanitized is not null)
                        mark(CapturePluginEvent(sanitized, sessionId, projectId, workingDirectory));
                    RefuseUnavailableOwnedQueue(sessionId);
                    notifications = projector.Entry!.TakeParentNotifications(sanitized);
                    // A failure the session goes on from with the next model of its tier is not told to the parent,
                    // and its queue waits for the run that continues: both are held until the fallback settles.
                    if (sanitized is AgentErrorEvent failed
                        && await TryClaimRunFailureAsync(sessionId, projector.Entry!, failed).ConfigureAwait(false) is { } claimed)
                    {
                        fallback = claimed;
                        heldFailure = new HeldRunFailure(this, sessionId, notifications);
                        notifications = [];
                    }
                    actorChoresCompleted = true;
                    // The session that runs writes its title: the sessions that are listed after a prompt or after
                    // the end of a run are read again, and show it.
                    if (@event is AgentContentCompletedEvent { Kind: AgentContentKind.User } or AgentErrorEvent
                        or AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle })
                        await _agentSessionCatalog.InvalidateAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                    return true;
                }).AsTask();
            }, projectionUse.Dispose, ObserveLivePluginEventAsync, () =>
            {
                var effects = new List<Func<Task>>();
                if (published is not null)
                    effects.Add(() => InvalidateFileSearchCacheAsync(published, workingDirectory).AsTask());
                foreach (var notification in notifications)
                    effects.Add(() => DeliverParentNotificationAsync(notification));
                if (fallback is not null && heldFailure is not null)
                {
                    // The fallback runs on its own: it waits for the send of the failed run to be released.
                    var held = heldFailure;
                    var start = fallback;
                    effects.Add(() =>
                    {
                        start(held);
                        return Task.CompletedTask;
                    });
                }
                else if (actorChoresCompleted && IsQueueDrainTrigger(@event))
                    effects.Add(() => TryDrainNextQueuedPromptAsync(sessionId));
                publication.IndependentWork = new LiveEventIndependentWork(effects);
                return publication.IndependentWork.RunAsync();
            }, (failure, dependencies) =>
            {
                projectionUse.Retain(failure);
                _forwarding.RetainDependencies(failure, dependencies);
            }).ConfigureAwait(false);
        }
        catch (Exception failure) when (OwnedProviderEventForwarding.HasRetention(failure))
        {
            // An orphaned dependency is not a conclusively finished notification. Keep the original
            // lineage admission with the forwarding owner's retained work, including at shutdown.
            retainParentNotificationClaim = true;
            _forwarding.RetainDependencies(failure, new { Runtime = this, Claim = parentNotificationClaim, Publication = publication });
            throw;
        }
        catch (ObjectDisposedException) when (publication.CanToleratePrepublicationFailure)
        {
        }
        catch (InvalidOperationException) when (publication.CanToleratePrepublicationFailure)
        {
        }
        catch (OperationCanceledException) when (_disposed && publication.CanToleratePrepublicationFailure)
        {
        }
        catch (IOException) when (publication.CanToleratePrepublicationFailure)
        {
        }
        catch (UnauthorizedAccessException) when (publication.CanToleratePrepublicationFailure)
        {
        }
        finally
        {
            if (!retainParentNotificationClaim) parentNotificationClaim?.Dispose();
        }
    }

    // Actor-only, using the existing admitted callback path before projection (including Shutdown).
    // Like the TUI event reducer, arrival order can move the timestamp backwards. No wall-clock fallback.
    private void ObserveAdmittedActivity(string sessionId, RuntimeSessionEntry entry, AgentEvent @event)
    {
        if (!_entries.TryGetValue(sessionId, out var active) || !ReferenceEquals(active, entry)
            || _transitions.ContainsKey(sessionId) || entry.Attachment.IsRetiring || entry.IsTerminated) return;
        if (@event.SessionId != sessionId || @event.ProviderId != entry.ProviderId || @event.Timestamp.Year <= 1)
        {
            entry.OmittedActivityEvents = entry.OmittedActivityEvents == long.MaxValue ? long.MaxValue : entry.OmittedActivityEvents + 1;
            return;
        }
        entry.ActivityTimestamp = @event.Timestamp;
        entry.ActivityEvents = entry.ActivityEvents == long.MaxValue ? long.MaxValue : entry.ActivityEvents + 1;
    }

    // Actor-only; do not alter the existing event stream/journal path for nonmatching provider callbacks.
    private void ObserveAdmittedUsage(string sessionId, RuntimeSessionEntry entry, AgentEvent? @event)
    {
        if (@event is not AgentSessionUpdateEvent { Usage: not null } update
            || !_entries.TryGetValue(sessionId, out var active) || !ReferenceEquals(active, entry)
            || _transitions.ContainsKey(sessionId) || entry.Attachment.IsRetiring || entry.IsTerminated)
            return;
        if (!string.Equals(update.SessionId, sessionId, StringComparison.Ordinal)
            || !string.Equals(update.ProviderId.Value, entry.ProviderId.Value, StringComparison.Ordinal))
        {
            entry.OmittedUsageEvents = entry.OmittedUsageEvents == long.MaxValue ? long.MaxValue : entry.OmittedUsageEvents + 1;
            return;
        }
        entry.UsageSequence = entry.UsageSequence == long.MaxValue ? long.MaxValue : entry.UsageSequence + 1;
        entry.LastObservedUsage = SessionRuntimeUsageObservation.FromEvent(entry.UsageSequence, update);
    }

    private static bool IsQueueDrainTrigger(AgentEvent @event)
        => @event is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle }
            or AgentErrorEvent;

    private async Task TryDrainNextQueuedPromptAsync(string sessionId, SessionActor? existingActor = null, OwnedQueuedExecution? ownedTrigger = null)
    {
        if (_disposed || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var work = await TryMarkNextQueuedPromptSubmittingAsync(sessionId, existingActor, ownedTrigger).ConfigureAwait(false);
        if (work is null)
        {
            return;
        }

        if (work.Owned is { } owned)
        {
            // The runtime-admitted queue body owns execution and cleanup, not this event tail.
            // The claim was published atomically with the slot in the mailbox.
            await owned.Drained.Task.ConfigureAwait(false);
            await TryDrainNextQueuedPromptAsync(sessionId, existingActor).ConfigureAwait(false);
            return;
        }

        // The run of a queued prompt (the result of a child session or of a background job) can ask, like a send.
        var askExecution = CreateRunAskExecution(sessionId, work.Entry.Attachment.Cancellation.Token);
        try
        {
            // The legacy arm always holds a cloned durable prompt; only the owned arm above has null.
            await RestoreArchivedForRunAsync(sessionId, work.Entry.CreatedAt).ConfigureAwait(false);
            var runStartedAt = DateTimeOffset.UtcNow;
            var sendOptions = new AgentSendOptions
            {
                Input = AgentInput.Text(work.Prompt!.Prompt), SourceSessionId = AgentSource(work.Prompt.SubmittedBy),
                Provenance = SessionPromptProvenance.Of(work.Prompt.Kind, work.Prompt.SubmittedBy),
            };
            if (askExecution is not null) sendOptions = ComposeRunAsk(askExecution, work.Entry, sendOptions);
            var runId = await _agentHub.RunAsync(work.SessionHandleId, sendOptions, work.Entry.Attachment.Cancellation.Token)
                .ConfigureAwait(false);
            await MarkQueuedPromptSubmittedAsync(work.Entry, work.Prompt!.QueueItemId, runId, runStartedAt, DateTimeOffset.UtcNow).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (OwnedProviderEventForwarding.HasRetention(ex))
            {
                work.Use.Retain(ex);
                _forwarding.RetainDependencies(ex, work);
            }
            try { await MarkQueuedPromptFailedAsync(work.Entry, work.Prompt!.QueueItemId, ex.Message, DateTimeOffset.UtcNow).ConfigureAwait(false); }
            catch (Exception cleanupFailure) { throw new AggregateException(ex, cleanupFailure); }
            if (OwnedProviderEventForwarding.HasRetention(ex)) throw;
        }
        finally
        {
            askExecution?.Close();
            work.Use.Dispose();
        }

        // A fast run can publish Idle before RunAsync returns and before the submitting item is
        // marked submitted. Probe the queue again after clearing QueueDrainInProgress so later
        // queued prompts are not left waiting for a terminal event that already happened.
        await TryDrainNextQueuedPromptAsync(sessionId, existingActor).ConfigureAwait(false);
    }

    private async Task<QueuedPromptDrainWork?> TryMarkNextQueuedPromptSubmittingAsync(string sessionId,
        SessionActor? existingActor = null, OwnedQueuedExecution? ownedTrigger = null)
    {
        var actor = existingActor ?? GetActorForWork(sessionId);
        while (!_disposed)
        {
        Task? transition = null;
        var work = await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    RefuseUnavailableOwnedQueue(sessionId);
                    // A rejected/stale immediate owned trigger is not authority to begin legacy
                    // replacement. Ordinary event/completion drain opportunities remain unchanged.
                    if (ownedTrigger is not null && (!_entries.TryGetValue(sessionId, out var target)
                        || !ReferenceEquals(target.OwnedQueue, ownedTrigger) || !CanUseOwnedQueue(target, ownedTrigger))) return null;
                    if (_transitions.TryGetValue(sessionId, out transition)) return null;
                    if (!_entries.TryGetValue(sessionId, out var entry) || entry.IsTerminated || entry.HasActiveRun || entry.QueueDrainInProgress)
                    {
                        return null;
                    }

                    // An aborted tree starts nothing by itself: its queue waits for a prompt sent to the session again.
                    if (_abortHolds.ContainsKey(sessionId)) return null;

                    var localState = await ReadLatestLocalStateAsync(sessionId, entry.CreatedAt, actorCancellationToken).ConfigureAwait(false);
                    // Null also represents a failed read; it is not proof that legacy work is absent.
                    if (localState is null) return null;
                    var recovered = RecoverInterruptedSends(localState, DateTimeOffset.UtcNow);
                    var item = localState.QueuedPrompts.FirstOrDefault(static prompt => string.Equals(prompt.State, "queued", StringComparison.OrdinalIgnoreCase));
                    if (item is null)
                    {
                        if (recovered.Count > 0)
                        {
                            // Sends cut twice end here: the queue says so, and holds nothing pending any more.
                            CopySessionMetadata(entry.ToDescriptor(), localState);
                            await _sessionViewCatalog.JournalStore.AppendStateAsync(entry.ToDescriptor(), localState, actorCancellationToken).ConfigureAwait(false);
                            PublishQueueChanged(sessionId, localState, recovered[^1], DateTimeOffset.UtcNow, isEnqueued: false);
                        }

                        // This successful read and the slot claim share the original arbitration.
                        // Never start a second owned drainer after an ambiguous null/busy/fault result.
                        return TryClaimOwnedQueue(entry);
                    }

                    var sessionHandleId = entry.SessionHandleId;
                    // What waits in the queue is run by the attachment of the session, without a send that prepares
                    // it. A session whose worktree was removed meanwhile is attached again first, in the folder of
                    // its project: the answer a child forwards to it is then read, and not lost to a run that
                    // cannot start in a folder that is gone.
                    if (!string.IsNullOrWhiteSpace(entry.PendingAgentPromptId) || entry.PendingModel is not null
                        || (entry.WorktreeDirectory is not null && ExistingWorktree(entry.WorktreeDirectory) is null))
                    {
                        var session = entry.ToDescriptor();
                        var prepared = await EnsureCoordinatorSessionCoreAsync(session, entry.ToExecutionOptions(), actorCancellationToken).ConfigureAwait(false);
                        if (prepared.Transition is not null)
                        {
                            transition = prepared.Transition;
                            return null;
                        }
                        entry = prepared.Entry!;
                        // The attachment runs with the pending model now.
                        entry.PendingModel = null;
                        sessionHandleId = entry.SessionHandleId;
                    }

                    var use = entry.Attachment.TryAcquireHandleUse();
                    if (use is null) return null;
                    try
                    {
                    var timestamp = DateTimeOffset.UtcNow;
                    item.State = "submitting";
                    item.SubmittingRuntime = _runtimeInstanceId.ToString("N");
                    item.DrainedAt = timestamp;
                    item.LastError = null;
                    CopySessionMetadata(entry.ToDescriptor(), localState);
                    await _sessionViewCatalog.JournalStore.AppendStateAsync(entry.ToDescriptor(), localState, actorCancellationToken).ConfigureAwait(false);
                    entry.BeginQueueDrain();
                    PublishQueueChanged(sessionId, localState, item, timestamp, isEnqueued: false);
                    return new QueuedPromptDrainWork(sessionHandleId, CloneQueuedPrompt(item), entry, use);
                    }
                    catch { use.Dispose(); throw; }
                },
                CancellationToken.None)
            .ConfigureAwait(false);
        if (transition is null) return work;
        try { await transition.ConfigureAwait(false); }
        catch (ObjectDisposedException failure) when (_forwarding.IsClosed
            && failure.ObjectName == typeof(SessionRuntimeService).FullName)
        {
            // A late attachment can be retired by shutdown before publication. The original
            // Ensure caller observes that refusal; an opportunistic queue tail has nothing to drain.
            return null;
        }
        // Re-read durable queue state after transition; never reuse the earlier state/item.
        }
        return null;
    }

    private async Task MarkQueuedPromptSubmittedAsync(
        RuntimeSessionEntry capturedEntry,
        string queueItemId,
        AgentRunId runId,
        DateTimeOffset runStartedAt,
        DateTimeOffset timestamp)
        => await UpdateQueuedPromptStateAsync(
                capturedEntry,
                queueItemId,
                timestamp,
                item =>
                {
                    item.State = "submitted";
                    item.RunId = runId.Value;
                    item.DrainedAt = timestamp;
                    item.LastError = null;
                },
                provenance => provenance.RunId = runId.Value,
                entry =>
                {
                    entry.CompleteQueueDrain();
                    entry.MarkActiveRunIfStillInFlight(runId, runStartedAt);
                })
            .ConfigureAwait(false);

    private async Task MarkQueuedPromptFailedAsync(RuntimeSessionEntry capturedEntry, string queueItemId, string error, DateTimeOffset timestamp)
        => await UpdateQueuedPromptStateAsync(
                capturedEntry,
                queueItemId,
                timestamp,
                item =>
                {
                    item.State = "failed";
                    item.DrainedAt = timestamp;
                    item.LastError = error;
                },
                updateProvenance: null,
                entry => entry.CompleteQueueDrain())
            .ConfigureAwait(false);

    private async Task UpdateQueuedPromptStateAsync(
        RuntimeSessionEntry currentEntry,
        string queueItemId,
        DateTimeOffset timestamp,
        Action<SessionViewQueuedPrompt> updateItem,
        Action<SessionViewPromptProvenance>? updateProvenance,
        Action<RuntimeSessionEntry>? updateEntry)
    {
        var sessionId = currentEntry.SessionId;
        var actor = GetActorForWork(sessionId);
        await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    try
                    {
                        var localState = await ReadLatestLocalStateAsync(sessionId, currentEntry.CreatedAt, actorCancellationToken).ConfigureAwait(false);
                        if (localState is null)
                        {
                            return false;
                        }

                        var item = localState.QueuedPrompts.FirstOrDefault(prompt => string.Equals(prompt.QueueItemId, queueItemId, StringComparison.Ordinal));
                        if (item is null)
                        {
                            return false;
                        }

                        updateItem(item);
                        if (updateProvenance is not null)
                        {
                            var provenance = localState.PromptProvenance.FirstOrDefault(prompt => string.Equals(prompt.PromptId, queueItemId, StringComparison.Ordinal));
                            if (provenance is not null)
                            {
                                updateProvenance(provenance);
                            }
                        }

                        await _sessionViewCatalog.JournalStore.AppendStateAsync(currentEntry.ToDescriptor(), localState, actorCancellationToken).ConfigureAwait(false);
                        PublishQueueChanged(sessionId, localState, item, timestamp, isEnqueued: false);
                        return true;
                    }
                    finally
                    {
                        updateEntry?.Invoke(currentEntry);
                    }
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private void PublishQueueChanged(string sessionId, SessionViewLocalState localState, SessionViewQueuedPrompt item, DateTimeOffset timestamp, bool isEnqueued)
    {
        _events.TryPublish(new SessionQueueRuntimeEvent(
            sessionId,
            timestamp,
            QueuedPromptCount(localState),
            item.QueueItemId,
            item.PromptPreview,
            isEnqueued)
        { QueueKind = item.Kind });
    }

    private static SessionViewQueuedPrompt CloneQueuedPrompt(SessionViewQueuedPrompt item)
        => new()
        {
            QueueItemId = item.QueueItemId,
            Kind = item.Kind,
            Prompt = item.Prompt,
            PromptPreview = item.PromptPreview,
            State = item.State,
            RunId = item.RunId,
            SubmittedBy = item.SubmittedBy,
            CreatedAt = item.CreatedAt,
            DrainedAt = item.DrainedAt,
            LastError = item.LastError,
            SubmittingRuntime = item.SubmittingRuntime,
            InterruptedSends = item.InterruptedSends,
        };

    private static IReadOnlyList<ParentNotificationPayload> ExtractParentNotificationBlocks(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var matches = ParentNotificationBlockRegex.Matches(content);
        if (matches.Count == 0)
        {
            return [];
        }

        var results = new List<ParentNotificationPayload>(matches.Count);
        foreach (Match match in matches)
        {
            var body = match.Groups["body"].Value.Trim();
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            results.Add(new ParentNotificationPayload(NormalizeParentNotificationKind(match.Groups["kind"].Value), body));
        }

        return results;
    }

    private static string StripParentNotificationBlocks(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        return ParentNotificationBlockRegex.Replace(content, string.Empty).Trim();
    }

    private static string NormalizeParentNotificationKind(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "note" or "progress" or "result" or "handoff" => normalized,
            _ => "progress",
        };
    }

    private async Task DeliverParentNotificationAsync(ParentNotificationWork notification)
    {
        try
        {
            var parent = await TryResolveSessionForParentDeliveryAsync(notification.ParentSessionId, CancellationToken.None).ConfigureAwait(false);
            if (parent is null)
            {
                PublishParentNotificationWarning(notification.SourceSessionId, $"Parent session '{notification.ParentSessionId}' was not found for automatic child-session notification.");
                return;
            }

            var prompt = BuildParentPeerAgentMessage(parent, notification);
            var submittedBy = new AltaActorProvenance
            {
                Kind = "agent",
                SourceSessionId = notification.SourceSessionId,
                SourceProjectId = notification.SourceProjectId,
                SourceAgentId = notification.SourceAgentId,
                CorrelationId = notification.CorrelationId,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            if (await HasActiveRunOwnedBodyAsync(parent, CancellationToken.None).ConfigureAwait(false))
            {
                try
                {
                    var runId = await SteerOwnedBodyAsync(
                            parent,
                            CreateParentDeliveryExecutionOptions(parent),
                            new AgentSteerOptions
                            {
                                Input = AgentInput.Text(prompt), SourceSessionId = AgentSource(submittedBy),
                                Provenance = SessionPromptProvenance.Of(SessionPromptProvenance.ParentNotifyKind, submittedBy),
                            },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await PersistPromptProvenanceAsync(parent, runId.Value, queued: false, SessionPromptProvenance.ParentNotifyKind, prompt, submittedBy, CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (!OwnedProviderEventForwarding.HasRetention(ex))
                {
                    PublishParentNotificationWarning(notification.SourceSessionId, $"Parent session '{notification.ParentSessionId}' could not be steered; queued the child-session notification instead. {ex.Message}");
                }
            }

            await QueuePromptOwnedBodyAsync(parent, prompt, SessionPromptProvenance.ParentNotifyKind, submittedBy, CancellationToken.None).ConfigureAwait(false);
            await TryDrainNextQueuedPromptAsync(parent.SessionId).ConfigureAwait(false);
        }
        catch (Exception ex) when (!OwnedProviderEventForwarding.HasRetention(ex)
            && (ex is not OperationCanceledException || _disposed))
        {
            PublishParentNotificationWarning(notification.SourceSessionId, $"Automatic parent notification failed without affecting the child session: {ex.Message}");
        }
    }

    private async Task<SessionViewDescriptor?> TryResolveSessionForParentDeliveryAsync(string sessionId, CancellationToken cancellationToken)
    {
        SessionViewDescriptor? session = null;
        try
        {
            session = await ResolveParentFromCachedStoreAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!OwnedProviderEventForwarding.HasRetention(ex)
            && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
        }

        if (session is null && _entries.TryGetValue(sessionId, out var entry))
        {
            var now = DateTimeOffset.UtcNow;
            session = new SessionViewDescriptor
            {
                SessionId = sessionId,
                Kind = SessionViewKind.ProjectSession,
                ProviderId = entry.ProviderId.Value,
                ProviderKey = entry.ProviderKey,
                ProjectRef = entry.ProjectId,
                ParentSessionId = entry.ParentSessionId,
                CreatedBy = entry.CreatedBy,
                ForkedFrom = entry.ForkedFrom,
                SpaceId = entry.SpaceId,
                WorkingDirectory = entry.WorkingDirectory,
                WorktreeDirectory = entry.WorktreeDirectory,
                Title = sessionId,
                Status = SessionViewStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
                LastActiveAt = now,
                StartedAt = now,
            };
        }

        if (session is not null)
        {
            await ApplyLocalSessionStateAsync(session, cancellationToken).ConfigureAwait(false);
        }

        return session;
    }

    private async Task<SessionViewDescriptor?> ResolveParentFromCachedStoreAsync(string sessionId, CancellationToken cancellationToken)
    {
        // Finishing path: directly await the shared cached store, never a catalog-list producer.
        var metadata = await _sessionViewCatalog.JournalStore.CreateSessionStore()
            .GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (metadata is null) return null;
        var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var session = TryCreateRecoverableSession(metadata, projects);
        if (session is not null)
        {
            if (metadata.ViewState is not null) ApplyCachedSessionLocalState(session, metadata.ViewState);
            else await ApplyPersistedSessionLocalStateAsync(session, cancellationToken).ConfigureAwait(false);
        }
        return session;
    }

    private async Task ApplyLocalSessionStateAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
    {
        try
        {
            var localState = await _sessionViewCatalog.JournalStore
                .ReadLatestStateAsync(session.SessionId, session.CreatedAt, cancellationToken)
                .ConfigureAwait(false);
            if (localState is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(localState.ParentSessionId))
            {
                session.ParentSessionId = localState.ParentSessionId;
            }

            if (localState.CreatedBy is not null)
            {
                session.CreatedBy = localState.CreatedBy;
            }

            if (localState.Archived)
            {
                session.Status = SessionViewStatus.Archived;
            }

            if (localState.MessageCount is not null)
            {
                session.MessageCount = localState.MessageCount;
            }

            if (!string.IsNullOrWhiteSpace(localState.AgentPromptId))
            {
                session.AgentPromptId = ResolveKnownAgentPromptId(localState.AgentPromptId, session.WorkingDirectory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        {
        }
    }

    private async Task UpsertSessionMetadataAsync(
        SessionViewDescriptor session,
        SessionExecutionOptions options,
        string? title,
        AgentSessionTitleSource titleSource,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId) || session.CreatedAt == default)
        {
            return;
        }

        var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
        // The family is the one the runtime recorded when it started or resumed the session: the key of a provider
        // (a second Claude Code account) is not its family, and a summary that disagrees with the provider state
        // makes the session need recovery before a move or a switch.
        var recorded = await store.GetSessionSummaryAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        var protocolFamily = recorded is not null && !string.IsNullOrWhiteSpace(recorded.ProtocolFamily)
            && string.Equals(recorded.ProviderKey, session.ResolvedProviderKey, StringComparison.OrdinalIgnoreCase)
                ? recorded.ProtocolFamily
                : options.ProviderId.Value;
        await store.UpsertSessionAsync(
                new AgentSessionSummary
                {
                    SessionId = session.SessionId,
                    ProviderId = new ModelProviderId(options.ProviderId.Value),
                    ProtocolFamily = protocolFamily,
                    ProviderKey = session.ResolvedProviderKey,
                    ModelId = options.Model,
                    ReasoningEffort = options.ReasoningEffort,
                    AgentPromptId = NormalizeOptionalText(session.AgentPromptId) ?? AgentPromptCatalog.DefaultPromptName,
                    WorkingDirectory = session.WorkingDirectory,
                    WorktreeDirectory = NormalizeOptionalText(session.WorktreeDirectory),
                    Title = title,
                    TitleSource = titleSource,
                    Summary = session.LatestSummary,
                    ParentSessionId = NormalizeOptionalText(session.ParentSessionId),
                    CreatedBySessionId = NormalizeOptionalText(session.CreatedBy?.SourceSessionId ?? session.ParentSessionId),
                    ForkedFrom = session.ForkedFrom,
                    RewindCount = session.RewindCount,
                    CreatedAt = session.CreatedAt,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The worktree a session records, while its folder exists; null otherwise.</summary>
    internal static string? ExistingWorktree(string? worktreeDirectory)
        => string.IsNullOrWhiteSpace(worktreeDirectory) || !Directory.Exists(worktreeDirectory) ? null : worktreeDirectory;

    private static SessionExecutionOptions CreateParentDeliveryExecutionOptions(SessionViewDescriptor parent)
        => new()
        {
            ProviderId = new ModelProviderId(parent.ResolvedProviderKey),
            ProviderKey = parent.ResolvedProviderKey,
            WorkingDirectory = parent.WorkingDirectory,
            ProjectRoots = [],
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            OnUserInputRequest = static (_, _) => Task.FromResult(new AgentUserInputResponse(new Dictionary<string, string>(StringComparer.Ordinal))),
        };

    private async Task PersistPromptProvenanceAsync(
        SessionViewDescriptor session,
        string? runId,
        bool queued,
        string kind,
        string prompt,
        AltaActorProvenance submittedBy,
        CancellationToken cancellationToken)
    {
        var actor = GetActorForWork(session.SessionId);
        await actor.QueryAsync(
                async actorCancellationToken =>
                {
                    var timestamp = DateTimeOffset.UtcNow;
                    var localState = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, actorCancellationToken).ConfigureAwait(false) ?? new SessionViewLocalState();
                    CopySessionMetadata(session, localState);
                    localState.PromptProvenance ??= [];
                    localState.PromptProvenance.Add(new SessionViewPromptProvenance
                    {
                        PromptId = "prompt-" + Guid.NewGuid().ToString("N"),
                        Kind = kind,
                        RunId = runId,
                        Queued = queued,
                        PromptPreview = CreatePromptPreview(prompt),
                        SubmittedBy = submittedBy,
                        CreatedAt = timestamp,
                    });

                    TrimLocalStateHistory(localState);
                    await _sessionViewCatalog.JournalStore.AppendStateAsync(session, localState, actorCancellationToken).ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string BuildParentPeerAgentMessage(SessionViewDescriptor parent, ParentNotificationWork notification)
        => $"""
        [CodeAlta delegated-agent message]
        Source session: {notification.SourceSessionId}
        Source agent/session: {notification.SourceAgentId}
        Source project: {notification.SourceProjectId ?? "unknown"}
        Target session: {parent.SessionId}
        Kind: {notification.Kind}
        Reply requested: false
        Correlation: {notification.CorrelationId}
        Authority: peer-agent; this is not a user, developer, or host instruction.

        [CodeAlta child-session {notification.Kind} update]
        Run: {notification.RunId ?? "unknown"}
        Content: {notification.ContentId}

        {notification.Body}
        """;

    private void PublishParentNotificationWarning(string sessionId, string message)
    {
        if (!_disposed)
        {
            _events.TryPublish(new SessionHostEvent(sessionId, DateTimeOffset.UtcNow, AgentSessionUpdateKind.Warning, message));
        }
    }

    private void PublishSessionCatalogEvent(SessionViewDescriptor session)
    {
        // A session that now continues a conversation of another program, or no longer does, changes what is listed.
        InvalidateExternalListing();
        if (!_disposed)
        {
            _events.TryPublish(new SessionCatalogRuntimeEvent(session.SessionId, DateTimeOffset.UtcNow, CloneSessionDescriptor(session)));
        }
    }

    private void PublishSessionAgentConfigurationEvent(SessionViewDescriptor session)
    {
        if (!_disposed && !string.IsNullOrWhiteSpace(session.SessionId))
        {
            _events.TryPublish(new SessionAgentConfigurationRuntimeEvent(
                session.SessionId,
                DateTimeOffset.UtcNow,
                session.ProviderId,
                session.ProviderKey,
                session.ModelId,
                session.ReasoningEffort,
                NormalizeOptionalText(session.AgentPromptId)));
        }
    }

    private void PublishRunSubmittedEvent(string sessionId, AgentRunId runId, DateTimeOffset timestamp)
    {
        if (!_disposed)
        {
            _events.TryPublish(new SessionLifecycleRuntimeEvent(
                sessionId,
                timestamp,
                new SessionLifecycleEvent
                {
                    SessionId = sessionId,
                    Kind = SessionLifecycleEventKind.RunSubmitted,
                    RunId = runId.Value,
                    Message = "Runtime run submitted.",
                }));
        }
    }

    private async Task PublishRuntimeFailureEventAsync(SessionViewDescriptor session, Exception exception,
        LiveEventObservationPrerequisite prerequisite)
    {
        if (_disposed || string.IsNullOrWhiteSpace(session.SessionId))
        {
            return;
        }

        var timestamp = DateTimeOffset.UtcNow;
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? "Runtime request failed."
            : exception.Message;
        var ProviderId = string.IsNullOrWhiteSpace(session.ProviderId)
            ? ModelProviderIds.Codex
            : new ModelProviderId(session.ProviderId);
        var publishedEvent = new AgentErrorEvent(ProviderId, session.SessionId, timestamp, message, exception);
        var envelope = CapturePluginEvent(publishedEvent, session.SessionId, session.ProjectRef, session.WorkingDirectory);
        var publication = new LiveEventPublication(this) { ObservationPrerequisite = prerequisite };
        await publication.CompleteAsync(mark =>
        {
        _events.TryPublish(new SessionAgentEvent(envelope.SessionId, publishedEvent));
        mark(envelope);
        return Task.CompletedTask;
        }, static () => { }, ObserveLivePluginEventAsync, () =>
        {
        _events.TryPublish(new SessionLifecycleRuntimeEvent(
            envelope.SessionId,
            timestamp,
            new SessionLifecycleEvent
            {
                SessionId = envelope.SessionId,
                Kind = SessionLifecycleEventKind.RunFailed,
                Message = message,
            }));
        return Task.CompletedTask;
        }, _forwarding.RetainDependencies).ConfigureAwait(false);
    }

    private void PublishRunFinishedEvent(
        string sessionId,
        AgentRunId? runId,
        SessionLifecycleEventKind kind,
        string message,
        DateTimeOffset timestamp)
    {
        if (_disposed || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _events.TryPublish(new SessionLifecycleRuntimeEvent(
            sessionId,
            timestamp,
            new SessionLifecycleEvent
            {
                SessionId = sessionId,
                Kind = kind,
                RunId = runId?.Value,
                Message = message,
            }));
    }

    private void PublishSessionLifecycleEvent(string sessionId)
    {
        _events.TryPublish(new SessionLifecycleRuntimeEvent(
            sessionId,
            DateTimeOffset.UtcNow,
            new SessionLifecycleEvent
            {
                SessionId = sessionId,
                Kind = SessionLifecycleEventKind.SessionStarted,
                Message = "Runtime session started.",
            }));
    }

    private static SessionViewDescriptor CloneSessionDescriptor(SessionViewDescriptor session)
        => new()
        {
            SessionId = session.SessionId,
            Kind = session.Kind,
            ProviderId = session.ProviderId,
            ProviderKey = session.ProviderKey,
            ProjectRef = session.ProjectRef,
            ParentSessionId = session.ParentSessionId,
            CreatedBy = session.CreatedBy,
            ForkedFrom = session.ForkedFrom,
            RewindCount = session.RewindCount,
            SpaceId = session.SpaceId,
            WorkingDirectory = session.WorkingDirectory,
            WorktreeDirectory = session.WorktreeDirectory,
            Title = session.Title,
            Status = session.Status,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
            LastActiveAt = session.LastActiveAt,
            StartedAt = session.StartedAt,
            LatestSummary = session.LatestSummary,
            ModelId = session.ModelId,
            ReasoningEffort = session.ReasoningEffort,
            PermissionMode = session.PermissionMode,
            Sandbox = session.Sandbox,
            TitleMode = session.TitleMode,
            TitleSource = session.TitleSource,
            AgentPromptId = session.AgentPromptId,
            MessageCount = session.MessageCount,
            SourcePath = session.SourcePath,
            MarkdownBody = session.MarkdownBody,
        };

    private SessionViewDescriptor? TryCreateRecoverableSession(
        AgentSessionMetadata session,
        IReadOnlyList<ProjectDescriptor> projects,
        KnownPromptNames? knownPrompts = null)
    {
        if (string.IsNullOrWhiteSpace(session.ProviderKey))
        {
            return null;
        }

        var providerKey = session.ProviderKey.Trim();
        var cwd = session.Context?.Cwd ?? session.WorkspacePath;
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return null;
        }

        var normalizedCwd = NormalizePath(cwd);
        var parentSessionId = ResolveParentSessionId(session.ParentSessionId, session.CreatedBySessionId, session.ForkedFrom is not null);
        if (string.Equals(normalizedCwd, NormalizePath(_catalogOptions.GlobalRoot), StringComparison.OrdinalIgnoreCase))
        {
            return new SessionViewDescriptor
            {
                SessionId = session.SessionId,
                Kind = SessionViewKind.GlobalSession,
                ProviderId = providerKey,
                ProviderKey = providerKey,
                WorkingDirectory = normalizedCwd,
                Title = ListedTitleOrNull(session, SessionViewKind.GlobalSession, null) ?? UnnamedGlobalSessionTitle,
                TitleSource = (session.Details as RawApiSessionMetadataDetails)?.TitleSource,
                Status = SessionViewStatus.Active,
                ParentSessionId = parentSessionId,
                ForkedFrom = session.ForkedFrom,
                RewindCount = session.RewindCount,
                SpaceId = session.SpaceId,
                CreatedAt = session.CreatedAt,
                UpdatedAt = session.UpdatedAt,
                LastActiveAt = session.UpdatedAt,
                StartedAt = session.CreatedAt,
                LatestSummary = session.Summary,
                ModelId = session.ModelId,
                ReasoningEffort = session.ReasoningEffort,
                AgentPromptId = ResolveKnownAgentPromptId(session.AgentPromptId, projectRoot: null, knownPrompts),
            };
        }

        var project = projects.FirstOrDefault(candidate =>
            string.Equals(NormalizePath(candidate.ProjectPath), normalizedCwd, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            return null;
        }

        return new SessionViewDescriptor
        {
            SessionId = session.SessionId,
            Kind = SessionViewKind.ProjectSession,
            ProviderId = providerKey,
            ProviderKey = providerKey,
            ProjectRef = project.Id,
            WorkingDirectory = normalizedCwd,
            // As recorded: whether the folder is still there is looked at where the session is about to work.
            WorktreeDirectory = NormalizeOptionalText(session.WorktreePath),
            Title = ListedTitleOrNull(session, SessionViewKind.ProjectSession, project) ?? project.DisplayName,
            TitleSource = (session.Details as RawApiSessionMetadataDetails)?.TitleSource,
            Status = SessionViewStatus.Active,
            ParentSessionId = parentSessionId,
            ForkedFrom = session.ForkedFrom,
            RewindCount = session.RewindCount,
            SpaceId = session.SpaceId,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
            LastActiveAt = session.UpdatedAt,
            StartedAt = session.CreatedAt,
            LatestSummary = session.Summary,
            ModelId = session.ModelId,
            ReasoningEffort = session.ReasoningEffort,
            AgentPromptId = ResolveKnownAgentPromptId(session.AgentPromptId, project.ProjectPath, knownPrompts),
        };
    }

    internal IReadOnlyList<AgentPromptDescriptor> ListOwnedPrompts(string? projectRoot, SpaceFilter? space)
    {
        ValidateDiscoveryProjectRoot(projectRoot);
        return AgentPromptCatalog.InSpace((PromptCatalog ?? new AgentPromptCatalog()).ListEffectivePrompts(new AgentPromptCatalogQuery
        {
            ProjectRoot = projectRoot,
            ProjectPromptResourcesTrusted = !string.IsNullOrWhiteSpace(projectRoot),
            UserCodeAltaRoot = _catalogOptions.GlobalRoot,
            UserProfileRoot = UserHomeRoot,
        }), space);
    }

    /// <summary>Gets the space a session of a project is in when it records <paramref name="spaceId"/>, or when it is created in it.</summary>
    /// <param name="spaceId">The space the session is created in; <see langword="null"/> for none.</param>
    /// <param name="project">The project of the session, or <see langword="null"/> for a chat.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The space, the default one when no other fits.</returns>
    internal async Task<SpaceDescriptor> ResolveSpaceAsync(string? spaceId, ProjectDescriptor? project, CancellationToken cancellationToken)
    {
        var spaces = await _spaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var id = SpaceCatalog.ResolveSessionSpace(spaceId, project, spaces);
        return spaces.First(space => string.Equals(space.Id, id, StringComparison.Ordinal));
    }

    /// <summary>Reads the global configuration's assignments for a space.</summary>
    /// <exception cref="InvalidDataException">The global configuration cannot be parsed.</exception>
    /// <exception cref="IOException">The global configuration cannot be read.</exception>
    internal SpaceFilter LoadSpaceFilter(string spaceId) => SpaceFilter.Load(_configStore, spaceId);

    private string? ResolveKnownAgentPromptId(string? promptId, string? projectRoot, KnownPromptNames? knownPrompts = null)
    {
        ValidateDiscoveryProjectRoot(projectRoot);
        var normalized = NormalizeOptionalText(promptId);
        if (normalized is null)
        {
            return null;
        }

        var names = knownPrompts is null ? ListKnownPromptNames(projectRoot) : knownPrompts.Of(projectRoot);
        return names.Contains(normalized) ? normalized : AgentPromptCatalog.DefaultPromptName;
    }

    private HashSet<string> ListKnownPromptNames(string? projectRoot)
    {
        var query = new AgentPromptCatalogQuery
        {
            ProjectRoot = projectRoot,
            ProjectPromptResourcesTrusted = !string.IsNullOrWhiteSpace(projectRoot),
            UserCodeAltaRoot = _catalogOptions.GlobalRoot,
            UserProfileRoot = UserHomeRoot,
        };
        return (PromptCatalog ?? new AgentPromptCatalog()).ListEffectivePrompts(query)
            .Select(static prompt => prompt.PromptName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // The names of the prompts of each folder, read once for one listing of the sessions.
    private sealed class KnownPromptNames(SessionRuntimeService runtime)
    {
        private readonly Dictionary<string, HashSet<string>> _names = new(StringComparer.Ordinal);

        public HashSet<string> Of(string? projectRoot)
        {
            var key = projectRoot ?? string.Empty;
            if (!_names.TryGetValue(key, out var names)) _names[key] = names = runtime.ListKnownPromptNames(projectRoot);
            return names;
        }
    }

    private AgentPromptUsageInfo? ResolveAgentPromptUsage(SystemPromptBundle? promptBundle, string? projectRoot)
    {
        ValidateDiscoveryProjectRoot(projectRoot);
        var promptName = NormalizeOptionalText(promptBundle?.Manifest.Composition.AgentPromptName);
        if (promptName is null)
        {
            return null;
        }

        var query = new AgentPromptCatalogQuery
        {
            ProjectRoot = projectRoot,
            ProjectPromptResourcesTrusted = !string.IsNullOrWhiteSpace(projectRoot),
            UserCodeAltaRoot = _catalogOptions.GlobalRoot,
            UserProfileRoot = UserHomeRoot,
        };
        var descriptor = (PromptCatalog ?? new AgentPromptCatalog()).ResolvePrompt(query, promptName);
        if (descriptor is null)
        {
            return new AgentPromptUsageInfo(promptName, null, null);
        }

        return new AgentPromptUsageInfo(
            descriptor.PromptName,
            NormalizeOptionalText(descriptor.DisplayName),
            FormatAgentPromptSourcePathForTimeline(descriptor.SourcePath, projectRoot));
    }

    internal static string? FormatAgentPromptSourcePathForTimeline(string? sourcePath, string? projectRoot)
    {
        var normalizedSourcePath = NormalizeOptionalText(sourcePath);
        if (normalizedSourcePath is null)
        {
            return null;
        }

        var sourceFullPath = NormalizePath(normalizedSourcePath);
        var normalizedProjectRoot = NormalizeOptionalText(projectRoot);
        if (normalizedProjectRoot is null)
        {
            return sourceFullPath;
        }

        var projectFullPath = NormalizePath(normalizedProjectRoot);
        var relativePath = Path.GetRelativePath(projectFullPath, sourceFullPath);
        return IsProjectRelativePath(relativePath) ? relativePath : sourceFullPath;
    }

    private static bool IsProjectRelativePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            return false;
        }

        return !string.Equals(relativePath, "..", StringComparison.Ordinal) &&
            !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private const string UnnamedGlobalSessionTitle = "Global Session";
    private const string UnnamedGlobalSessionSummary = "Global overview and coordination session.";

    private static string UnnamedProjectSessionSummary(ProjectDescriptor project) => $"Project session for {project.DisplayName}.";

    // The first line of a summary, as the title of a session that follows its last message.
    private static string? SummaryTitle(string? summary) => AgentSessionTitles.FromMessage(summary);

    // The title a stored session is attached with and who chose it; null for a session that is not stored. A session
    // written before the source of a title was recorded is decided here, once: its attachment saves the answer. A
    // title that is one a session is created with was never chosen: the session keeps the title it is listed under
    // (the first line of its summary) until its mode gives it another.
    private async Task<StoredSessionTitle?> StoredTitleAsync(SessionViewDescriptor session, ProjectDescriptor? project, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId)) return null;
        var metadata = await _sessionViewCatalog.JournalStore.CreateSessionStore()
            .GetSessionAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        if (metadata is null) return null;
        var details = metadata.Details as RawApiSessionMetadataDetails;
        var saved = NormalizeOptionalText(details?.Title);
        if (details?.TitleSource is { } source) return new(saved, source);
        return TitleWasChosen(metadata, session.Kind, project)
            ? new(saved, AgentSessionTitleSource.User)
            : new(SummaryTitle(metadata.Summary) ?? saved, AgentSessionTitleSource.Auto);
    }

    private readonly record struct StoredSessionTitle(string? Saved, AgentSessionTitleSource Source);

    // Whether somebody chose the saved title of a stored session. A session written before the source of a title
    // was recorded says nothing: its title was chosen unless it is one a session is created with.
    private static bool TitleWasChosen(AgentSessionMetadata metadata, SessionViewKind? kind, ProjectDescriptor? project)
    {
        var details = metadata.Details as RawApiSessionMetadataDetails;
        if (details?.TitleSource is { } source) return source is AgentSessionTitleSource.User;
        return NormalizeOptionalText(details?.Title) is { } title && !IsCreationTitle(title, kind, project);
    }

    // The titles a session is created with: "Global Session" or the name of its project, or the first line of the
    // summary it is created with. Only what was written before the source of a title was recorded is compared with
    // them, and a title a frontend gave without saying who chose it.
    private static bool IsCreationTitle(string title, SessionViewKind? kind, ProjectDescriptor? project) => kind switch
    {
        null => false,
        SessionViewKind.GlobalSession => title is UnnamedGlobalSessionTitle || title == SummaryTitle(UnnamedGlobalSessionSummary),
        _ => project is not null && (title == project.DisplayName || title == SummaryTitle(UnnamedProjectSessionSummary(project))),
    };

    private static ProjectDescriptor? ProjectOf(SessionViewDescriptor session, IReadOnlyList<ProjectDescriptor> projects)
        => session.Kind == SessionViewKind.GlobalSession ? null
            : projects.FirstOrDefault(candidate => string.Equals(candidate.Id, session.ProjectRef, StringComparison.Ordinal));

    /// <summary>
    /// The title a list of sessions shows for a stored session, and the one its deletion is confirmed with: its saved
    /// title, which only the session itself and a Rename write. A session written before the source of a title was
    /// recorded is listed as it always was until it runs again: by its saved title when somebody chose it, and
    /// otherwise by the first line of its summary (80 characters at most). A session without any of them is listed
    /// by its id.
    /// </summary>
    /// <param name="session">The stored session.</param>
    /// <param name="kind">Whether the session is a global one or the session of a project; <see langword="null" /> when that is not known: a saved title without a source is then taken as chosen.</param>
    /// <param name="project">The project of a project session.</param>
    /// <returns>The title of the session in a list.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session" /> is <see langword="null" />.</exception>
    public static string ListedTitle(AgentSessionMetadata session, SessionViewKind? kind, ProjectDescriptor? project)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ListedTitleOrNull(session, kind, project) ?? session.SessionId;
    }

    private static string? ListedTitleOrNull(AgentSessionMetadata session, SessionViewKind? kind, ProjectDescriptor? project)
    {
        var details = session.Details as RawApiSessionMetadataDetails;
        var saved = details?.Title;
        if (string.IsNullOrWhiteSpace(saved)) saved = null;
        if (details?.TitleSource is not null && saved is not null) return saved;
        return saved is not null && !IsCreationTitle(saved.Trim(), kind, project) ? saved : SummaryTitle(session.Summary) ?? saved;
    }

    // The session of the agent a prompt comes from; null for a prompt of a person, of a reminder or of the host.
    private static string? AgentSource(AltaActorProvenance? submittedBy)
        => string.Equals(submittedBy?.Kind, "agent", StringComparison.OrdinalIgnoreCase) ? NormalizeOptionalText(submittedBy!.SourceSessionId) : null;

    // A session an agent created without naming a parent is the sub-agent of that agent. Not a fork, nor the session
    // that keeps what a rewind removed: whoever made it, it continues another session and reports to none, unless it
    // was given a parent. Listed as a child of its creator, it would get the instructions of a sub-agent.
    private static string? ResolveParentSessionId(string? parentSessionId, string? createdBySessionId, bool forked = false)
        => NormalizeOptionalText(parentSessionId) ?? (forked ? null : NormalizeOptionalText(createdBySessionId));

    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = @"\\" + trimmed[8..];
        }
        else if (trimmed.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[4..];
        }

        return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> CreateToolSignatures(IReadOnlyList<AgentToolDefinition>? tools)
    {
        if (tools is not { Count: > 0 })
        {
            return [];
        }

        return tools
            .Select(static tool => string.Join(
                '\u001f',
                tool.Spec.Name,
                tool.Spec.Description,
                tool.Spec.InputSchema.GetRawText(),
                tool.Spec.Deferred ? "deferred" : string.Empty))
            .OrderBy(static signature => signature, StringComparer.Ordinal)
            .ToArray();
    }

    private static AgentReasoningEffort? ParseReasoningEffort(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "minimal" => AgentReasoningEffort.Minimal,
            "low" => AgentReasoningEffort.Low,
            "medium" => AgentReasoningEffort.Medium,
            "high" => AgentReasoningEffort.High,
            "xhigh" => AgentReasoningEffort.XHigh,
            "max" => AgentReasoningEffort.Max,
            _ => null,
        };
    }

    private Task UpdateSessionLocalStateAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
        => UpdateSessionLocalStateAsync(session, cancellationToken, null);

    // A session that is no longer in the catalog is hidden: its local state says it is archived.
    private Task HideSessionLocalStateAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
        => UpdateSessionLocalStateAsync(session, cancellationToken, null, archive: true);

    // createdPermissionMode, createdTitleMode: the modes of a session that starts, when it has them. Any other
    // update leaves the saved modes as they are: only the owner of the session changes them.
    private async Task UpdateSessionLocalStateAsync(SessionViewDescriptor session, CancellationToken cancellationToken, string? createdPermissionMode,
        string? createdTitleMode = null, bool archive = false, bool? createdReviewBeforeCommit = null, string? createdSandbox = null)
    {
        using var retentionWork = ClaimRetentionWork(session.SessionId, session.ParentSessionId, session.CreatedBy?.SourceSessionId);
        var localState = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false) ?? new SessionViewLocalState();
        if (createdPermissionMode is not null) localState.PermissionMode = createdPermissionMode;
        if (createdSandbox is not null) localState.Sandbox = createdSandbox;
        if (createdTitleMode is not null) localState.TitleMode = createdTitleMode;
        if (createdReviewBeforeCommit is not null) localState.ReviewBeforeCommit = createdReviewBeforeCommit;
        localState.ProviderKey = session.ResolvedProviderKey;
        localState.ModelId = session.ModelId;
        localState.ReasoningEffort = session.ReasoningEffort;
        localState.AgentPromptId = NormalizeOptionalText(session.AgentPromptId);
        // Only an archive or a restore changes whether the session is archived (see CopySessionMetadata).
        if (archive && !localState.Archived)
        {
            localState.Archived = true;
            localState.ArchivedAt = DateTimeOffset.UtcNow;
        }

        localState.MessageCount = session.MessageCount;
        localState.ParentSessionId = session.ParentSessionId;
        localState.CreatedBy = session.CreatedBy;
        await _sessionViewCatalog.JournalStore.AppendStateAsync(session, localState, cancellationToken).ConfigureAwait(false);
    }

    // The model and effort a session takes at its next turn; the effort is null for a model that reports none.
    private sealed record PendingModelChoice(string ModelId, AgentReasoningEffort? ReasoningEffort);

    private sealed class RuntimeSessionEntry
    {
        public RuntimeSessionEntry(
            string sessionId,
            AgentSessionHandleId sessionHandleId,
            SessionViewKind kind,
            SessionViewStatus status,
            ModelProviderId providerId,
            string providerKey,
            string? projectId,
            string? parentSessionId,
            AltaActorProvenance? createdBy,
            DateTimeOffset createdAt,
            string title,
            string workingDirectory,
            string? model,
            AgentReasoningEffort? reasoningEffort,
            string? agentPromptId,
            string? additionalSystemMessage,
            string? additionalDeveloperInstructions,
            IReadOnlyList<string> projectRoots,
            IReadOnlyList<AgentToolDefinition>? tools,
            IReadOnlyList<string> toolSignatures,
            AgentPermissionRequestHandler onPermissionRequest,
            AgentUserInputRequestHandler? onUserInputRequest,
            SessionInstructionProcessor? instructionProcessor,
            EventProjector projector,
            OwnedProviderEventForwarding.Attachment attachment)
        {
            SessionId = sessionId;
            SessionHandleId = sessionHandleId;
            Kind = kind;
            Status = status;
            ProviderId = providerId;
            ProviderKey = providerKey;
            ProjectId = projectId;
            ParentSessionId = parentSessionId;
            CreatedBy = createdBy;
            CreatedAt = createdAt;
            Title = title;
            WorkingDirectory = workingDirectory;
            Model = model;
            ReasoningEffort = reasoningEffort;
            AgentPromptId = NormalizeOptionalText(agentPromptId);
            AdditionalSystemMessage = additionalSystemMessage;
            AdditionalDeveloperInstructions = additionalDeveloperInstructions;
            ProjectRoots = projectRoots.ToArray();
            Tools = tools?.ToArray() ?? [];
            ToolSignatures = toolSignatures;
            OnPermissionRequest = onPermissionRequest;
            OnUserInputRequest = onUserInputRequest;
            InstructionProcessor = instructionProcessor;
            Projector = projector;
            Attachment = attachment;
        }

        public string SessionId { get; }

        public AgentSessionHandleId SessionHandleId { get; }

        public SessionViewKind Kind { get; }

        public SessionViewStatus Status { get; }

        public ModelProviderId ProviderId { get; }

        public string ProviderKey { get; }

        public string? ProjectId { get; }

        public string? ParentSessionId { get; }

        public AltaActorProvenance? CreatedBy { get; }

        public DateTimeOffset CreatedAt { get; }

        public string Title { get; set; }

        public string WorkingDirectory { get; }

        /// <summary>The git worktree this attachment runs in; null when it runs in <see cref="WorkingDirectory"/>.</summary>
        public string? WorktreeDirectory { get; init; }

        /// <summary>Where the session comes from when it starts with the turns of another one.</summary>
        public CodeAlta.Agent.Runtime.AgentSessionForkOrigin? ForkedFrom { get; init; }

        /// <summary>The space the session records, or null for a session that records none.</summary>
        public string? SpaceId { get; init; }

        /// <summary>The identity the attachment runs under, read when it attached.</summary>
        public SessionIdentity Identity { get; init; } = SessionIdentity.Usual;

        // What the programs of the attachment start with: the environment of the identity and the variables of the session.
        public AgentProcessEnvironment ProcessEnvironment { get; init; } = AgentProcessEnvironment.Empty;

        /// <summary>The built-in tools the attachment runs without.</summary>
        public IReadOnlySet<string> DisabledTools { get; init; } = System.Collections.Frozen.FrozenSet<string>.Empty;

        // The patterns over tool names the attachment was given (SetDisabledToolPatterns).
        public IReadOnlyList<string> DisabledToolPatterns { get; init; } = [];

        /// <summary>The transition that published this attachment: the request that joins it reuses it as it is.</summary>
        public Task? PublishedBy { get; init; }

        /// <summary>The agent prompt the instructions of this attachment were composed with.</summary>
        public string? ComposedAgentPromptId { get; init; }

        /// <summary>The version of the registration of the provider the runtime of this attachment was created from.</summary>
        public long ProviderRegistrationVersion { get; init; }

        /// <summary>The permission mode chosen for the session, or null for the one of its provider. Read from any thread.</summary>
        public string? PermissionMode
        {
            get => Volatile.Read(ref _permissionMode);
            set => Volatile.Write(ref _permissionMode, value);
        }

        private string? _permissionMode;

        /// <summary>
        /// The sandbox chosen for the session (a profile name, or <c>off</c>), or null when its scopes decide. Read from any
        /// thread: the tools of the session read it at each call.
        /// </summary>
        public string? OwnSandbox
        {
            get => Volatile.Read(ref _ownSandbox);
            set => Volatile.Write(ref _ownSandbox, value);
        }

        private string? _ownSandbox;

        /// <summary>How the session titles itself when it does not follow the setting. Read and written on the actor of the session.</summary>
        public AgentSessionTitleMode? OwnTitleMode { get; set; }

        /// <summary>The mode the attached session was last told. Read and written on the actor of the session.</summary>
        public AgentSessionTitleMode TitleMode { get; set; }

        /// <summary>
        /// Whether the user reviews the changes of the session before commit, when the session has its own choice;
        /// null when it follows the setting. Read from any thread: the commit guard reads it at each command.
        /// </summary>
        public bool? OwnReviewBeforeCommit
        {
            get => Volatile.Read(ref _ownReviewBeforeCommit) switch { 1 => false, 2 => true, _ => null };
            set => Volatile.Write(ref _ownReviewBeforeCommit, value switch { false => 1, true => 2, null => 0 });
        }

        private int _ownReviewBeforeCommit;

        /// <summary>
        /// What the agent was last told of the review before commit; null when nothing, or when a compaction may have
        /// dropped it. Read and written on the actor of the session, and reset by the events of the session.
        /// </summary>
        public bool? ReviewNoted
        {
            get => Volatile.Read(ref _reviewNoted) switch { 1 => false, 2 => true, _ => null };
            set => Volatile.Write(ref _reviewNoted, value switch { false => 1, true => 2, null => 0 });
        }

        private int _reviewNoted;

        public string? Model { get; }

        public AgentReasoningEffort? ReasoningEffort { get; }

        public string? AgentPromptId { get; }

        public string? PendingAgentPromptId { get; set; }

        // A model chosen for the next turn while the attachment runs with another one: the drain of the queue attaches
        // the session again with it, as it does for a pending agent prompt. A send that runs on this attachment drops it.
        public PendingModelChoice? PendingModel { get; set; }

        public string? AdditionalSystemMessage { get; }

        public string? AdditionalDeveloperInstructions { get; }

        public IReadOnlyList<string> ProjectRoots { get; }

        public IReadOnlyList<AgentToolDefinition> Tools { get; }

        public IReadOnlyList<string> ToolSignatures { get; }

        public AgentPermissionRequestHandler OnPermissionRequest { get; }

        public AgentUserInputRequestHandler? OnUserInputRequest { get; }

        public SessionInstructionProcessor? InstructionProcessor { get; }

        public OwnedProviderEventForwarding.Attachment Attachment { get; }

        public EventProjector Projector { get; }

        public bool IsTerminated { get; private set; }

        public SessionRuntimeUsageObservation? LastObservedUsage { get; set; }
        public long UsageSequence { get; set; }
        public long OmittedUsageEvents { get; set; }
        private volatile bool _activityOpen;

        /// <summary>
        /// Whether provider callbacks arriving now count as activity: true once the transition that created
        /// this attachment has completed. Read on the provider's callback thread.
        /// </summary>
        public bool ActivityOpen => _activityOpen;

        public void OpenActivity() => _activityOpen = true;

        public DateTimeOffset? ActivityTimestamp { get; set; }
        public long ActivityEvents { get; set; }
        public long OmittedActivityEvents { get; set; }

        public AgentRunId? ActiveRunId { get; private set; }

        // The last run this attachment saw start, kept after it ended.
        public AgentRunId? LastRunId { get; private set; }

        public DateTimeOffset LastTerminalEventAt { get; private set; } = DateTimeOffset.MinValue;

        public bool QueueDrainInProgress { get; private set; }
        internal OwnedQueuedExecution? OwnedQueue { get; set; }

        private ParentFinalNotificationCandidate? _lastParentFinalCandidate;

        private string? _lastParentIdleRunId;

        private readonly HashSet<string> _sentParentProgressKeys = new(StringComparer.Ordinal);

        private readonly HashSet<string> _sentParentFinalKeys = new(StringComparer.Ordinal);

        public bool HasActiveRun => ActiveRunId is not null;

        /// <summary>Whether the last run of this attachment ended with an error, until another run starts.</summary>
        public bool LastRunFailed { get; private set; }

        public SessionViewDescriptor ToDescriptor()
            => new()
            {
                SessionId = SessionId,
                Kind = Kind,
                ProviderId = ProviderId.Value,
                ProviderKey = ProviderKey,
                ProjectRef = ProjectId,
                ParentSessionId = ParentSessionId,
                CreatedBy = CreatedBy,
                ForkedFrom = ForkedFrom,
                SpaceId = SpaceId,
                WorkingDirectory = WorkingDirectory,
                WorktreeDirectory = WorktreeDirectory,
                Title = Title,
                Status = IsTerminated ? SessionViewStatus.Archived : Status,
                CreatedAt = CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastActiveAt = LastTerminalEventAt == DateTimeOffset.MinValue ? CreatedAt : LastTerminalEventAt,
                ModelId = PendingModel is { } pending ? pending.ModelId : Model,
                ReasoningEffort = PendingModel is { } pendingEffort ? pendingEffort.ReasoningEffort : ReasoningEffort,
                AgentPromptId = PendingAgentPromptId ?? AgentPromptId,
            };

        public SessionExecutionOptions ToExecutionOptions()
            => new()
            {
                ProviderId = ProviderId,
                ProviderKey = ProviderKey,
                WorkingDirectory = WorkingDirectory,
                ProjectRoots = ProjectRoots,
                Model = PendingModel is { } pending ? pending.ModelId : Model,
                ReasoningEffort = PendingModel is { } pendingEffort ? pendingEffort.ReasoningEffort : ReasoningEffort,
                AgentPromptId = PendingAgentPromptId ?? AgentPromptId,
                Tools = Tools,
                AdditionalSystemMessage = AdditionalSystemMessage,
                AdditionalDeveloperInstructions = AdditionalDeveloperInstructions,
                InstructionProcessor = InstructionProcessor,
                OnPermissionRequest = OnPermissionRequest,
                OnUserInputRequest = OnUserInputRequest,
            };

        public bool Matches(SessionExecutionOptions options, string? agentPromptId)
        {
            var resolvedAgentPromptId = NormalizeOptionalText(agentPromptId) ?? NormalizeOptionalText(options.AgentPromptId);
            return !IsTerminated
                && string.Equals(ProviderId.Value, options.ProviderId.Value, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ProviderKey, options.ProviderKey ?? options.ProviderId.Value, StringComparison.OrdinalIgnoreCase)
                && string.Equals(WorkingDirectory, options.WorkingDirectory, StringComparison.Ordinal)
                && string.Equals(Model, options.Model, StringComparison.Ordinal)
                && ReasoningEffort == options.ReasoningEffort
                && string.Equals(AgentPromptId, resolvedAgentPromptId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(AdditionalSystemMessage, options.AdditionalSystemMessage, StringComparison.Ordinal)
                && string.Equals(AdditionalDeveloperInstructions, options.AdditionalDeveloperInstructions, StringComparison.Ordinal)
                && (InstructionProcessor is null) == (options.InstructionProcessor is null)
                && ToolSignatures.SequenceEqual(CreateToolSignatures(options.Tools), StringComparer.Ordinal);
        }

        public void ObserveEvent(AgentEvent @event)
        {
            // A compaction may drop the note of the review before commit: the next prompt tells it again.
            if (@event is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.CompactionCompleted }) ReviewNoted = null;

            if (@event.RunId is { } runId && ShouldTrackRunId(@event))
            {
                // A run that goes on after a failed one makes that failure a thing of the past.
                if (ActiveRunId is null && @event is not AgentErrorEvent) LastRunFailed = false;
                // What was suggested answered the turn before the run that starts.
                if (ActiveRunId is null) PromptSuggestion = null;
                ActiveRunId = runId;
                LastRunId = runId;
            }

            if (@event is AgentErrorEvent) LastRunFailed = true;

            if (@event is AgentBackgroundTasksEvent tasks)
            {
                ObserveBackgroundTasks(tasks);
            }

            if (@event is AgentRemoteControlEvent remote)
            {
                RemoteControl = remote.RemoteControl;
            }

            // The session tells a suggestion only between its runs; one that reaches this entry after the next run
            // started all the same is not kept through it.
            if (@event is AgentPromptSuggestionEvent suggested && ActiveRunId is null)
            {
                PromptSuggestion = suggested.Suggestion;
            }

            if (@event is AgentErrorEvent or AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle or AgentSessionUpdateKind.Shutdown })
            {
                ActiveRunId = null;
                LastTerminalEventAt = @event.Timestamp;
            }

            if (@event is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Shutdown })
            {
                IsTerminated = true;
                BackgroundTasks = [];
                RemoteControl = AgentRemoteControl.Off;
                PromptSuggestion = null;
            }
        }

        /// <summary>The background tasks of the provider: those that go on, then the last that failed or were stopped.</summary>
        public IReadOnlyList<SessionRuntimeBackgroundTask> BackgroundTasks { get; private set; } = [];

        /// <summary>The remote control of the session, as its provider last told it.</summary>
        public AgentRemoteControl RemoteControl { get; private set; } = AgentRemoteControl.Off;

        /// <summary>What the provider suggested as the next prompt once the last turn ended; null once a run started since.</summary>
        public string? PromptSuggestion { get; private set; }

        // The tasks that go on are the ones the event lists, all of them. A task that failed or was stopped is
        // kept after them for the call that started it, which otherwise only says that it returned; a task that
        // ran to its end leaves nothing, and neither does one that runs again.
        private void ObserveBackgroundTasks(AgentBackgroundTasksEvent @event)
        {
            // A task says that it ended just before the list that no longer has it: it is not shown as going on meanwhile.
            var live = @event.Tasks.Where(task => !@event.Ended.Any(ended => ended.TaskId == task.TaskId)).Take(MaximumBackgroundTasks)
                .Select(static task => new SessionRuntimeBackgroundTask(task.TaskId, task.Kind, task.Description, task.ToolCallId, task.StartedAt, null))
                .ToArray();
            var ended = @event.Ended
                .Where(static task => task.Outcome is not AgentBackgroundTaskOutcome.Completed && task.ToolCallId is not null)
                .Select(task => new SessionRuntimeBackgroundTask(task.TaskId, BackgroundTasks.FirstOrDefault(known => known.TaskId == task.TaskId)?.Kind ?? "task",
                    task.Summary, task.ToolCallId, null, task.Outcome))
                .Concat(BackgroundTasks.Where(static task => task.Outcome is not null))
                .Where(task => !Array.Exists(live, running => running.TaskId == task.TaskId || running.ToolCallId == task.ToolCallId))
                .DistinctBy(static task => task.TaskId)
                .Take(MaximumBackgroundTasks);
            BackgroundTasks = [.. live, .. ended];
        }

        private const int MaximumBackgroundTasks = 16;

        public IReadOnlyList<ParentNotificationWork> TakeParentNotifications(AgentEvent? @event)
        {
            if (string.IsNullOrWhiteSpace(ParentSessionId) || @event is null)
            {
                return [];
            }

            if (@event is AgentContentCompletedEvent { Kind: AgentContentKind.Assistant } completed)
            {
                var notifications = new List<ParentNotificationWork>();
                var strippedContent = StripParentNotificationBlocks(completed.Content);
                _lastParentFinalCandidate = new ParentFinalNotificationCandidate(
                    completed.RunId?.Value,
                    completed.ContentId,
                    strippedContent);

                var explicitUpdates = ExtractParentNotificationBlocks(completed.Content);
                for (var index = 0; index < explicitUpdates.Count; index++)
                {
                    var update = explicitUpdates[index];
                    var key = string.Concat(completed.ContentId, ":", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (_sentParentProgressKeys.Add(key))
                    {
                        notifications.Add(CreateParentNotification(update.Kind, update.Body, completed.RunId?.Value, completed.ContentId));
                    }
                }

                // Event callbacks are forwarded independently; an idle update can reach the
                // mailbox before the completed content from the same run.
                if (completed.RunId?.Value is { } runId && string.Equals(_lastParentIdleRunId, runId, StringComparison.Ordinal))
                    notifications.AddRange(TakeFinalParentNotification(runId));

                return notifications;
            }

            if (@event is AgentErrorEvent error)
            {
                var key = "error:" + (error.RunId?.Value ?? error.Timestamp.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (!_sentParentFinalKeys.Add(key))
                {
                    return [];
                }

                var body = string.Concat("Delegated session failed or was cancelled before a final assistant reply: ", error.Message);
                return [CreateParentNotification("error", body, error.RunId?.Value, key)];
            }

            if (@event is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle } idle)
            {
                _lastParentIdleRunId = idle.RunId?.Value;
                return TakeFinalParentNotification(_lastParentIdleRunId);
            }

            return [];
        }

        private IReadOnlyList<ParentNotificationWork> TakeFinalParentNotification(string? runId)
        {
            if (_lastParentFinalCandidate is not { } candidate
                || (runId is not null && candidate.RunId is not null && !string.Equals(runId, candidate.RunId, StringComparison.Ordinal))
                || string.IsNullOrWhiteSpace(candidate.Content))
                return [];

            var key = candidate.RunId ?? candidate.ContentId;
            if (!_sentParentFinalKeys.Add(key)) return [];
            return [CreateParentNotification("answer", candidate.Content, candidate.RunId, candidate.ContentId)];
        }

        private ParentNotificationWork CreateParentNotification(string kind, string body, string? runId, string contentId)
            => new(
                SourceSessionId: SessionId,
                SourceProjectId: ProjectId,
                SourceAgentId: SessionHandleId.ToString(),
                ParentSessionId: ParentSessionId!,
                Kind: kind,
                Body: body,
                RunId: runId,
                ContentId: contentId,
                CorrelationId: "auto-parent-" + Guid.NewGuid().ToString("N"));

        private static bool ShouldTrackRunId(AgentEvent @event)
        {
            if (@event is not AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.CompactionStarted or AgentSessionUpdateKind.CompactionCompleted } &&
                @event is not AgentActivityEvent { Kind: AgentActivityKind.Compaction })
            {
                return true;
            }

            return false;
        }

        public bool MarkActiveRunIfStillInFlight(AgentRunId runId, DateTimeOffset runStartedAt)
        {
            if (LastTerminalEventAt >= runStartedAt)
            {
                return false;
            }

            ActiveRunId = runId;
            LastRunId = runId;
            return true;
        }

        public AgentRunId? ClearActiveRun()
        {
            var activeRunId = ActiveRunId;
            ActiveRunId = null;
            LastTerminalEventAt = DateTimeOffset.UtcNow;
            return activeRunId;
        }

        public void BeginQueueDrain()
            => QueueDrainInProgress = true;

        public void CompleteQueueDrain()
            => QueueDrainInProgress = false;

    }

    private sealed record QueuedPromptDrainWork(AgentSessionHandleId SessionHandleId, SessionViewQueuedPrompt? Prompt,
        RuntimeSessionEntry Entry, OwnedProviderEventForwarding.Use Use, OwnedQueuedExecution? Owned = null);

    private sealed record ParentNotificationPayload(string Kind, string Body);

    private sealed record ParentFinalNotificationCandidate(string? RunId, string ContentId, string Content);

    internal sealed record ParentNotificationWork(
        string SourceSessionId,
        string? SourceProjectId,
        string SourceAgentId,
        string ParentSessionId,
        string Kind,
        string Body,
        string? RunId,
        string ContentId,
        string CorrelationId);

    private sealed class EventProjector
    {
        private readonly string _sessionId;
        private readonly Action<SessionRuntimeEvent> _publish;
        private readonly Dictionary<string, StreamedAssistantText> _content = new(StringComparer.Ordinal);

        private readonly Action<AgentEvent> _observeRuntimeSessionEvent;
        public RuntimeSessionEntry? Entry { get; set; }

        public EventProjector(string sessionId, Action<SessionRuntimeEvent> publish, Action<AgentEvent> observeRuntimeSessionEvent)
        {
            ArgumentNullException.ThrowIfNull(publish);
            ArgumentNullException.ThrowIfNull(observeRuntimeSessionEvent);

            _sessionId = sessionId;
            _publish = publish;
            _observeRuntimeSessionEvent = observeRuntimeSessionEvent;
        }

        public AgentEvent? Project(AgentEvent @event)
        {
            _observeRuntimeSessionEvent(@event);

            if (TrySanitize(@event, out var sanitized) && sanitized is not null)
            {
                _publish(new SessionAgentEvent(_sessionId, sanitized));
                return sanitized;
            }

            return null;
        }

        public IReadOnlyList<AgentEvent> ProjectHistory(IReadOnlyList<AgentEvent> history)
        {
            // History is projected on its own: what streams now keeps the text it streamed so far.
            var content = new Dictionary<string, StreamedAssistantText>(StringComparer.Ordinal);
            var results = new List<AgentEvent>(history.Count);
            foreach (var @event in history)
            {
                if (TrySanitize(@event, content, out var sanitized) && sanitized is not null)
                {
                    results.Add(sanitized);
                }
            }

            return results;
        }

        private bool TrySanitize(AgentEvent @event, out AgentEvent? sanitized)
            => TrySanitize(@event, _content, out sanitized);

        private static bool TrySanitize(AgentEvent @event, Dictionary<string, StreamedAssistantText> content, out AgentEvent? sanitized)
        {
            switch (@event)
            {
                case AgentContentDeltaEvent delta when delta.Kind == AgentContentKind.Assistant:
                    sanitized = SanitizeDelta(delta, content);
                    return sanitized is not null;
                case AgentContentCompletedEvent completed when completed.Kind == AgentContentKind.Assistant:
                    sanitized = SanitizeCompleted(completed, content);
                    return sanitized is not null;
                case AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle }:
                    // The run is over: a text that never completed streams no more.
                    content.Clear();
                    sanitized = @event;
                    return true;
                default:
                    sanitized = @event;
                    return true;
            }
        }

        private static AgentEvent? SanitizeDelta(AgentContentDeltaEvent delta, Dictionary<string, StreamedAssistantText> content)
        {
            if (!content.TryGetValue(delta.ContentId, out var state))
            {
                state = new StreamedAssistantText();
                content[delta.ContentId] = state;
            }

            var deltaText = state.Append(delta.Delta);
            return string.IsNullOrEmpty(deltaText) ? null : delta with { Delta = deltaText };
        }

        private static AgentEvent? SanitizeCompleted(AgentContentCompletedEvent completed, Dictionary<string, StreamedAssistantText> content)
        {
            // The completed text replaces what streamed: nothing of the stream is kept once it is complete.
            content.Remove(completed.ContentId);
            var stripped = StreamedAssistantText.StripScheduleBlocks(completed.Content);
            return string.IsNullOrWhiteSpace(stripped)
                ? null
                : completed with { Content = stripped };
        }
    }
}
