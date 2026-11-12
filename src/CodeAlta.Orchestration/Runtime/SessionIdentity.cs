using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// The identity a session works under: the one of its space, with the CLI profiles of its project over it, as they
/// were when the session attached. It holds only references (names, emails, paths, names of variables), never a secret.
/// </summary>
/// <param name="SpaceId">The space the identity is of; <see langword="null"/> for the usual settings of a host without spaces.</param>
/// <param name="Identity">The identity of the space with the profiles of the project; <see langword="null"/> when it sets nothing.</param>
/// <param name="Environment">The variables the processes of the session start with.</param>
public sealed record SessionIdentity(string? SpaceId, SpaceIdentity? Identity, AgentProcessEnvironment Environment)
{
    /// <summary>Gets the identity that changes nothing: the user's usual Git, GitHub, Jira and CLI settings.</summary>
    public static SessionIdentity Usual { get; } = new(null, null, AgentProcessEnvironment.Empty);

    /// <summary>Gets whether the identity changes nothing.</summary>
    public bool IsUsual => Identity is null || Identity.IsEmpty;

    /// <summary>
    /// Gets the names of the CLI profiles of <see cref="Identity"/> that the project of the session gives or changes;
    /// empty when they are all the space's.
    /// </summary>
    public IReadOnlyList<string> ProjectProfiles { get; init; } = [];

    /// <summary>
    /// Gets what the processes of the session start without: a sentence for each secret that asks before use whose use the user
    /// denied or did not confirm in time when the session attached (<see cref="SessionIdentityResolver.ResolveForAttachmentAsync"/>);
    /// empty when nothing is missing.
    /// </summary>
    public IReadOnlyList<string> SecretProblems { get; init; } = [];

    /// <summary>
    /// Gets what each secret that asks before use ended with when the session attached, by the name of the secret, whatever its
    /// case: true when the processes of the session were given it, false when they started without it. A session this one starts
    /// takes these answers instead of asking the user again (<see cref="SessionSecretAttachment.Inherited"/>). Empty when the
    /// environment of the session names no such secret.
    /// </summary>
    public IReadOnlyDictionary<string, bool> SecretAnswers { get; init; } = System.Collections.Frozen.FrozenDictionary<string, bool>.Empty;
}

/// <summary>How a session attaches, for the secrets that ask before use that its environment names.</summary>
/// <param name="Unattended">
/// Whether nobody is in front of the session, such as a run of an automation and the sessions it starts: nobody is asked, and a
/// secret whose use is not confirmed is left out at once.
/// </param>
/// <param name="Inherited">
/// What the session that created this one was answered (<see cref="SessionIdentity.SecretAnswers"/>): this one is given the secrets
/// that one was given, and starts without the others, without asking the user again: also without a secret it does not name,
/// which that session was never asked for. Null when no session created it, or when that session is not attached: the user is
/// then asked as for any session. Whoever gives it answers for the
/// session these answers come from: it has the secrets they give, so it must be the session the host recorded as the creator,
/// never a parent the new session only names.
/// </param>
public sealed record SessionSecretAttachment(bool Unattended, IReadOnlyDictionary<string, bool>? Inherited)
{
    /// <summary>Gets the attachment of a session the user is in front of, that no session started.</summary>
    public static SessionSecretAttachment Attended { get; } = new(false, null);
}

/// <summary>What the secrets that ask before use ended with for a session that attached.</summary>
/// <param name="Answers">Each secret by its name: true when it was given (<see cref="SessionIdentity.SecretAnswers"/>).</param>
/// <param name="Problems">A sentence for each secret the session starts without, naming it (<see cref="SessionIdentity.SecretProblems"/>).</param>
public sealed record SessionSecretOutcome(IReadOnlyDictionary<string, bool> Answers, IReadOnlyList<string> Problems);

/// <summary>
/// Decides, when a session attaches, what its shell gets of the secrets that ask before use: the host shows the user the uses
/// that wait, who confirms or denies them; nobody is asked for a session nobody is in front of, and a session another one started
/// takes the answers of that one.
/// </summary>
public interface ISessionSecretConfirmations
{
    /// <summary>Starts the attachment of a session, before its environment is built for the first time.</summary>
    /// <param name="scope">The work of the session, whose run is the session: the instance its environment is built with.</param>
    /// <param name="attachment">How it attaches.</param>
    void Begin(SecretScope scope, SessionSecretAttachment attachment);

    /// <summary>Waits, for a bounded time, for the answers to the uses that the environment built for a session waits for.</summary>
    /// <param name="scope">The work of the session, as given to <see cref="Begin"/>.</param>
    /// <param name="cancellationToken">A token to stop waiting: the uses not confirmed then are left out.</param>
    /// <returns>True when the user confirmed a use: the environment is built again, for the secrets confirmed.</returns>
    Task<bool> WaitAsync(SecretScope scope, CancellationToken cancellationToken);

    /// <summary>Ends the attachment, once the environment is built for the last time.</summary>
    /// <param name="scope">The work of the session, as given to <see cref="Begin"/>.</param>
    /// <returns>What each secret that asks before use ended with, and what the session is told.</returns>
    SessionSecretOutcome End(SecretScope scope);
}

/// <summary>
/// Gives the identity of the sessions of a space, read from the file of the space and from the configuration of the
/// project when asked: a session reads it once, when it attaches, and keeps it while it runs. A host without spaces
/// (the terminal application) gives the usual settings to every session.
/// </summary>
public sealed class SessionIdentityResolver
{
    private readonly SpaceCatalog? _spaces;
    private readonly Func<SpaceIdentity?, SecretScope, AgentProcessEnvironment> _environment;
    private readonly Func<string, IReadOnlyList<SpaceCliProfile>> _projectProfiles;

    /// <summary>Initializes a resolver.</summary>
    /// <param name="spaces">The spaces, or <see langword="null"/> for a host whose sessions keep the usual settings.</param>
    /// <param name="environment">Builds the environment of an identity; null for <see cref="SpaceIdentityEnvironment.Create(SpaceIdentity?)"/>.</param>
    /// <param name="projectProfiles">
    /// Reads the CLI profiles a project gives its sessions, from the folder of the project; null for none, such as
    /// <see cref="CodeAltaConfigStore.LoadProjectCliProfiles(string?)"/>. It never throws.
    /// </param>
    public SessionIdentityResolver(SpaceCatalog? spaces, Func<SpaceIdentity?, AgentProcessEnvironment>? environment = null,
        Func<string, IReadOnlyList<SpaceCliProfile>>? projectProfiles = null)
        : this(spaces, environment is null ? null : (identity, _) => environment(identity), projectProfiles, null)
    {
    }

    /// <summary>
    /// Initializes a resolver whose environments are built for the work of each session (<c>doc/fleet/secrets.md</c>,
    /// scopes): its space, its project, the environment it was admitted to, and the session as the run its uses are recorded for.
    /// </summary>
    /// <param name="spaces">The spaces, or <see langword="null"/> for a host whose sessions keep the usual settings.</param>
    /// <param name="environment">Builds the environment of an identity for the work of a session; null for <see cref="SpaceIdentityEnvironment.Create(SpaceIdentity?)"/>.</param>
    /// <param name="projectProfiles">Reads the CLI profiles a project gives its sessions; null for none. It never throws.</param>
    /// <param name="environments">The environments sessions were admitted to; null for none.</param>
    public SessionIdentityResolver(SpaceCatalog? spaces, Func<SpaceIdentity?, SecretScope, AgentProcessEnvironment>? environment,
        Func<string, IReadOnlyList<SpaceCliProfile>>? projectProfiles, SessionSecretEnvironments? environments)
    {
        _spaces = spaces;
        _environment = environment ?? (static (identity, _) => SpaceIdentityEnvironment.Create(identity));
        _projectProfiles = projectProfiles ?? (static _ => []);
        Environments = environments ?? new SessionSecretEnvironments();
    }

    /// <summary>Gets the environments sessions were admitted to, whose values their processes get.</summary>
    public SessionSecretEnvironments Environments { get; }

    /// <summary>
    /// Gets or sets what tells whether a session is the work of the owner of the machine: a session another person started
    /// (a member of a team on a team machine) resolves none of the owner's values, only those of the environment it was
    /// admitted to (<see cref="SecretScope.OwnerWork"/>). Null makes every session the owner's work, as on a desktop.
    /// </summary>
    public Func<string, bool>? IsOwnerWork { get; set; }

    /// <summary>
    /// Gets or sets what records that a session another session started is the work of the same person or run as that session,
    /// when that session is not the work of the owner (<see cref="IsOwnerWork"/>): a sub-session of a member's session, or of a run
    /// nobody attends that is not the owner's, resolves none of the owner's values either, and neither do the sessions it starts.
    /// Called with the session and the session that started it; null records nothing.
    /// </summary>
    public Action<string, string>? InheritWork { get; set; }

    /// <summary>
    /// Gets or sets what decides what the shell of a session that attaches gets of the secrets that ask before use
    /// (<see cref="ResolveForAttachmentAsync(string?, string?, string, string?, SessionSecretAttachment, CancellationToken, CancellationToken)"/>);
    /// null waits for none, and those secrets are left out.
    /// </summary>
    public ISessionSecretConfirmations? Confirmations { get; set; }

    // The most times the environment of a session that attaches is built again after the user confirmed secrets.
    private const int MaximumConfirmationRounds = 16;

    /// <summary>Gets a new resolver that gives every session the usual settings (its <see cref="Environments"/> its own).</summary>
    public static SessionIdentityResolver None => new(null);

    /// <summary>Gets whether the sessions of this host take the identity of their space.</summary>
    public bool IsEnabled => _spaces is not null;

    /// <summary>
    /// Gives the identity of a session from what it records: its space, else the space of its project
    /// (<see cref="SpaceCatalog.ResolveSessionSpace(string?, ProjectDescriptor?, IReadOnlyList{SpaceDescriptor})"/>),
    /// with the CLI profiles of its project over the ones of the space.
    /// </summary>
    /// <param name="recordedSpaceId">The space the journal of the session records, or <see langword="null"/>.</param>
    /// <param name="projectId">The project of the session, or <see langword="null"/> for a chat.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identity; the usual one when the space and the project set none or cannot be read.</returns>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public Task<SessionIdentity> ResolveAsync(string? recordedSpaceId, string? projectId, CancellationToken cancellationToken = default)
        => ResolveAsync(recordedSpaceId, projectId, null, cancellationToken);

    /// <summary>
    /// Gives the identity of a session (<see cref="ResolveAsync(string?, string?, CancellationToken)"/>), whose environment is
    /// built for its work: its space, its project and the environment it was admitted to.
    /// </summary>
    /// <param name="recordedSpaceId">The space the journal of the session records, or <see langword="null"/>.</param>
    /// <param name="projectId">The project of the session, or <see langword="null"/> for a chat.</param>
    /// <param name="sessionId">The session, the run its uses of values are recorded for; null for none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identity; the usual one when the space and the project set none or cannot be read.</returns>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public Task<SessionIdentity> ResolveAsync(string? recordedSpaceId, string? projectId, string? sessionId, CancellationToken cancellationToken)
        => ResolveAsync(recordedSpaceId, projectId, sessionId, null, cancellationToken);

    /// <summary>
    /// Gives the identity of a session (<see cref="ResolveAsync(string?, string?, string?, CancellationToken)"/>) started by another
    /// session: one started by a session that is not the work of the owner is not the owner's work either (<see cref="InheritWork"/>).
    /// </summary>
    /// <param name="recordedSpaceId">The space the journal of the session records, or <see langword="null"/>.</param>
    /// <param name="projectId">The project of the session, or <see langword="null"/> for a chat.</param>
    /// <param name="sessionId">The session, the run its uses of values are recorded for; null for none.</param>
    /// <param name="parentSessionId">The session that started it; null for none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identity; the usual one when the space and the project set none or cannot be read.</returns>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async Task<SessionIdentity> ResolveAsync(string? recordedSpaceId, string? projectId, string? sessionId, string? parentSessionId, CancellationToken cancellationToken)
        => await ReadAsync(recordedSpaceId, projectId, sessionId, parentSessionId, cancellationToken).ConfigureAwait(false) is { } read
            ? Of(read.Space, read.Profiles, read.Scope)
            : SessionIdentity.Usual;

    /// <summary>
    /// Gives the identity a session attaches with (<see cref="ResolveAsync(string?, string?, string?, string?, CancellationToken)"/>):
    /// when its environment waits for the confirmation of a secret that asks before use, it waits for the user's answer
    /// (<see cref="Confirmations"/>), then builds the environment again with the secrets the user confirmed. A secret denied or not
    /// confirmed in time is left out, and named in <see cref="SessionIdentity.SecretProblems"/>. Both builds are given the same
    /// <see cref="SecretScope"/> instance, so that the builder can keep for the second what the first read. An identity resolved
    /// without attaching (<see cref="ResolveAsync(string?, string?, string?, string?, CancellationToken)"/>) asks the user nothing.
    /// </summary>
    /// <param name="recordedSpaceId">The space the journal of the session records, or <see langword="null"/>.</param>
    /// <param name="projectId">The project of the session, or <see langword="null"/> for a chat.</param>
    /// <param name="sessionId">The session, the run its uses of values are recorded for.</param>
    /// <param name="parentSessionId">The session that started it; null for none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identity; the usual one when the space and the project set none or cannot be read.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">The read or the wait was canceled.</exception>
    public Task<SessionIdentity> ResolveForAttachmentAsync(string? recordedSpaceId, string? projectId, string sessionId, string? parentSessionId,
        CancellationToken cancellationToken)
        => ResolveForAttachmentAsync(recordedSpaceId, projectId, sessionId, parentSessionId, CancellationToken.None, cancellationToken);

    /// <summary>
    /// Gives the identity a session attaches with (<see cref="ResolveForAttachmentAsync(string?, string?, string, string?, CancellationToken)"/>),
    /// whose wait for the confirmations ends early when <paramref name="stopWaiting"/> is canceled: the user stopped the session. The
    /// secrets that were not confirmed then are left out, and named in <see cref="SessionIdentity.SecretProblems"/>.
    /// </summary>
    /// <param name="recordedSpaceId">The space the journal of the session records, or <see langword="null"/>.</param>
    /// <param name="projectId">The project of the session, or <see langword="null"/> for a chat.</param>
    /// <param name="sessionId">The session, the run its uses of values are recorded for.</param>
    /// <param name="parentSessionId">The session that started it; null for none.</param>
    /// <param name="stopWaiting">Ends the wait for the confirmations, not the resolution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identity; the usual one when the space and the project set none or cannot be read.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<SessionIdentity> ResolveForAttachmentAsync(string? recordedSpaceId, string? projectId, string sessionId, string? parentSessionId,
        CancellationToken stopWaiting, CancellationToken cancellationToken)
        => ResolveForAttachmentAsync(recordedSpaceId, projectId, sessionId, parentSessionId, SessionSecretAttachment.Attended, stopWaiting, cancellationToken);

    /// <summary>
    /// Gives the identity a session attaches with (<see cref="ResolveForAttachmentAsync(string?, string?, string, string?, CancellationToken, CancellationToken)"/>),
    /// for a session nobody is in front of, or one another session started: nobody is asked for the first, which starts at once
    /// without the secrets whose use is not confirmed, and the second takes the answers of the session that started it. What each
    /// secret ended with is in <see cref="SessionIdentity.SecretAnswers"/>, for the sessions this one starts.
    /// </summary>
    /// <param name="recordedSpaceId">The space the journal of the session records, or <see langword="null"/>.</param>
    /// <param name="projectId">The project of the session, or <see langword="null"/> for a chat.</param>
    /// <param name="sessionId">The session, the run its uses of values are recorded for.</param>
    /// <param name="parentSessionId">The session that started it; null for none.</param>
    /// <param name="attachment">How it attaches, for the secrets that ask before use.</param>
    /// <param name="stopWaiting">Ends the wait for the confirmations, not the resolution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identity; the usual one when the space and the project set none or cannot be read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="attachment"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<SessionIdentity> ResolveForAttachmentAsync(string? recordedSpaceId, string? projectId, string sessionId, string? parentSessionId,
        SessionSecretAttachment attachment, CancellationToken stopWaiting, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(attachment);
        if (await ReadAsync(recordedSpaceId, projectId, sessionId, parentSessionId, cancellationToken).ConfigureAwait(false) is not { } read) return SessionIdentity.Usual;
        if (Confirmations is not { } confirmations) return Of(read.Space, read.Profiles, read.Scope);
        // Told before the first build: it decides which secrets the user is asked for.
        confirmations.Begin(read.Scope, attachment);
        var identity = Of(read.Space, read.Profiles, read.Scope);
        if (identity.Identity is null) return identity;
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopWaiting))
        {
            // Built again for the secrets the user confirmed: nothing was given for them before. A build can ask for a secret the
            // one before did not reach, such as the second of a value that names two: it is waited for in turn, and each round
            // needs a new confirmation of the user, so the rounds end.
            for (var round = 0; round < MaximumConfirmationRounds && await confirmations.WaitAsync(read.Scope, waiting.Token).ConfigureAwait(false); round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                identity = Of(read.Space, read.Profiles, read.Scope);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var outcome = confirmations.End(read.Scope);
        return outcome.Answers.Count == 0 && outcome.Problems.Count == 0
            ? identity
            : identity with { SecretProblems = outcome.Problems, SecretAnswers = outcome.Answers };
    }

    // The space, the profiles of the project and the scope of a session; null when the catalog cannot be read.
    private async Task<(SpaceDescriptor Space, IReadOnlyList<SpaceCliProfile> Profiles, SecretScope Scope)?> ReadAsync(string? recordedSpaceId, string? projectId,
        string? sessionId, string? parentSessionId, CancellationToken cancellationToken)
    {
        if (_spaces is null) return null;
        SpaceDescriptor space;
        IReadOnlyList<SpaceCliProfile> profiles = [];
        try
        {
            space = await _spaces.ResolveSessionSpaceAsync(recordedSpaceId, projectId, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(projectId)
                && await _spaces.Projects.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false) is { ProjectPath: { Length: > 0 } root })
            {
                profiles = _projectProfiles(root);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The catalog cannot be read now: nothing is set, as for a space without identity.
            return null;
        }

        return (space, profiles, new SecretScope(space.Id, string.IsNullOrWhiteSpace(projectId) ? null : projectId, sessionId is null ? null : Environments.ScopeOf(sessionId))
        {
            RunId = sessionId,
            OwnerWork = IsOwnersWork(sessionId, parentSessionId),
        });
    }

    // Whether a session is the work of the owner: neither recorded as another person's or a run's, nor started by such a session,
    // which it is then recorded as the work of, so that the sessions it starts in turn are not the owner's either.
    private bool IsOwnersWork(string? sessionId, string? parentSessionId)
    {
        if (sessionId is null || IsOwnerWork is not { } isOwnerWork) return true;
        if (!isOwnerWork(sessionId)) return false;
        if (string.IsNullOrWhiteSpace(parentSessionId) || string.Equals(parentSessionId, sessionId, StringComparison.Ordinal) || isOwnerWork(parentSessionId)) return true;
        InheritWork?.Invoke(sessionId, parentSessionId);
        return false;
    }

    /// <summary>Gives the identity of one space.</summary>
    /// <param name="spaceId">The identifier of the space.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identity; the usual one when no such space exists or it sets none.</returns>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async Task<SessionIdentity> ResolveSpaceAsync(string? spaceId, CancellationToken cancellationToken = default)
        => _spaces is null || string.IsNullOrWhiteSpace(spaceId)
            ? SessionIdentity.Usual
            : await ResolveAsync(spaceId, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Gives the identity of a space as it is described, without reading the catalog.</summary>
    /// <param name="space">The space.</param>
    /// <returns>Its identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="space"/> is <see langword="null"/>.</exception>
    public SessionIdentity Of(SpaceDescriptor space) => Of(space, []);

    /// <summary>
    /// Gives the identity of the sessions of a project in a space as they are described, without reading the catalog:
    /// the identity of the space with the CLI profiles of the project over it
    /// (<see cref="SpaceIdentity.WithProjectCli(IReadOnlyList{SpaceCliProfile}?)"/>).
    /// </summary>
    /// <param name="space">The space.</param>
    /// <param name="projectProfiles">The CLI profiles of the project; empty for none.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public SessionIdentity Of(SpaceDescriptor space, IReadOnlyList<SpaceCliProfile> projectProfiles)
        => Of(space, projectProfiles, new SecretScope(space?.Id, null, null));

    /// <summary>Gives the identity of the sessions of a project in a space for the work of a run, without reading the catalog.</summary>
    /// <param name="space">The space.</param>
    /// <param name="projectProfiles">The CLI profiles of the project; empty for none.</param>
    /// <param name="scope">The work of the run, whose values its environment gets.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public SessionIdentity Of(SpaceDescriptor space, IReadOnlyList<SpaceCliProfile> projectProfiles, SecretScope scope)
    {
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(projectProfiles);
        ArgumentNullException.ThrowIfNull(scope);
        var identity = (space.Settings.Identity ?? new SpaceIdentity()).WithProjectCli(projectProfiles);
        if (identity.IsEmpty) return new SessionIdentity(space.Id, null, AgentProcessEnvironment.Empty);
        return new SessionIdentity(space.Id, identity, _environment(identity, scope))
        {
            ProjectProfiles = [.. projectProfiles.Select(static profile => profile.Name)],
        };
    }
}
