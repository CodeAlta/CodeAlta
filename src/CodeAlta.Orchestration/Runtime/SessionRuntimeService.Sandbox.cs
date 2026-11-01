using CodeAlta.Catalog;
using CodeAlta.Sandbox;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// The sandbox of a session as it applies now: whether it is sandboxed and with which profile, the scope that decided
/// it, and what is wrong when the profile cannot be used.
/// </summary>
/// <param name="Choice">The profile, or none, and the scope that decided it.</param>
/// <param name="Profile">The resolved profile when the session is sandboxed and it resolves; null otherwise.</param>
/// <param name="Problem">Why the sandbox cannot run the session (an unknown or invalid profile); null when nothing is wrong.
/// A sandboxed session with a problem runs no command: it is never run without its sandbox.</param>
/// <param name="ProjectSetsSandbox">Whether the configuration file of the project has a <c>[sandbox]</c> table, which is ignored.</param>
public sealed record SessionSandbox(SandboxChoice Choice, ResolvedSandboxProfile? Profile, string? Problem, bool ProjectSetsSandbox = false)
{
    /// <summary>Gets the sandbox of a session that no scope sandboxes.</summary>
    public static SessionSandbox None { get; } = new(SandboxChoice.None, null, null);

    /// <summary>Gets whether the session is sandboxed.</summary>
    public bool IsSandboxed => Choice.IsSandboxed;
}

/// <summary>
/// What the sandbox of a session lets a program that CodeAlta runs for it outside its container reach: the browser of a
/// sandboxed session reads and writes these folders only, and reaches the network through a proxy of these rules.
/// </summary>
/// <param name="Profile">The name of the profile.</param>
/// <param name="Read">The folders it may read.</param>
/// <param name="Write">The folders it may write.</param>
/// <param name="Network">What the network of the profile reaches.</param>
/// <param name="Hosts">The host patterns it reaches, or null for every host of the internet; empty with no network.</param>
/// <param name="LocalPorts">The ports of this computer it reaches.</param>
/// <param name="ReservedPorts">The ports of this computer it never reaches: CodeAlta's own.</param>
public sealed record SessionSandboxReach(string Profile, IReadOnlyList<string> Read, IReadOnlyList<string> Write, SandboxNetwork Network,
    IReadOnlyList<string>? Hosts, IReadOnlyList<int> LocalPorts, IReadOnlySet<int> ReservedPorts)
{
    /// <summary>Gets the folders and files never read or written, also inside <see cref="Read"/> and <see cref="Write"/>.</summary>
    public IReadOnlyList<string> Denied { get; init; } = [];

    /// <summary>
    /// Gets the entries inside <see cref="Write"/> that are never written: what other programs run later (git hooks and
    /// configuration, the settings of CodeAlta and of coding agents).
    /// </summary>
    public IReadOnlyList<string> Protected { get; init; } = [];

    /// <summary>Starts a proxy of the network of the profile on a free port of the loopback: the program goes through it only.</summary>
    /// <returns>The proxy, which its owner disposes.</returns>
    /// <exception cref="System.Net.Sockets.SocketException">No port could be listened on.</exception>
    public CodeAlta.Sandbox.Network.SandboxNetworkProxy StartProxy() => CodeAlta.Sandbox.Network.SandboxNetworkProxy.Start(Hosts, LocalPorts, ReservedPorts);
}

public sealed partial class SessionRuntimeService
{
    /// <summary>Reads the sandbox settings of the user: the setting of every session, and the profiles, mapped for the sandbox.</summary>
    /// <returns>The value of every session (a profile name, <c>off</c>, or null) and the profiles of the user.</returns>
    public (string? AllSessions, IReadOnlyList<SandboxProfile> Profiles) ReadSandboxSettings()
    {
        CodeAltaSandboxSettings settings;
        try
        {
            settings = _configStore.LoadGlobalSandbox();
            Volatile.Write(ref _lastSandboxSettings, settings);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // A configuration that cannot be read now is not one without a sandbox: the last one read applies.
            settings = Volatile.Read(ref _lastSandboxSettings) ?? CodeAltaSandboxSettings.Empty;
        }

        return (settings.AllSessions, [.. settings.Profiles.Select(ToSandboxProfile)]);
    }

    // The sandbox settings and the providers read last: what applies while the configuration cannot be read, so that
    // a file broken for a moment, or by a session, never turns a sandbox off.
    private CodeAltaSandboxSettings? _lastSandboxSettings;
    private IReadOnlyDictionary<string, CodeAltaProviderDocument>? _lastSandboxProviders;

    /// <summary>
    /// Gets the sandbox of a session as it applies now: its own choice, else the sandbox of its model, of its provider,
    /// of its project, of its space, else the setting of every session (<see cref="SandboxScopes.Resolve"/>). It is read
    /// again at each call, so that a change of the settings applies from the next command of the session.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sandbox; <see cref="SessionSandbox.None"/> for a session that is not known.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public async Task<SessionSandbox> GetSessionSandboxAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        string? own;
        string? providerKey;
        string? modelId;
        if (_entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated)
        {
            own = entry.OwnSandbox;
            providerKey = entry.ProviderKey;
            modelId = entry.Model;
        }
        else if (await ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false) is { } session)
        {
            own = session.Sandbox;
            providerKey = session.ResolvedProviderKey;
            modelId = session.ModelId;
        }
        else
        {
            return SessionSandbox.None;
        }

        var record = await GetSessionSpaceAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await ResolveSandboxAsync(own, providerKey, modelId, record?.ProjectId, record?.SpaceId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the sandbox a session would have: a session that is about to be created, or one about to change its
    /// provider or model.
    /// </summary>
    /// <param name="own">The choice made for the session itself, or null.</param>
    /// <param name="providerKey">Its provider.</param>
    /// <param name="modelId">Its model, or null.</param>
    /// <param name="projectId">Its project, or null.</param>
    /// <param name="spaceId">The space it records, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sandbox.</returns>
    public async Task<SessionSandbox> ResolveSandboxAsync(string? own, string? providerKey, string? modelId, string? projectId, string? spaceId, CancellationToken cancellationToken = default)
    {
        var (allSessions, profiles) = ReadSandboxSettings();
        var project = string.IsNullOrWhiteSpace(projectId) ? null : await _projectCatalog.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false);
        var resolvedSpace = SpaceCatalog.ResolveSessionSpace(spaceId, project, await _spaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false));
        var space = (await _spaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => string.Equals(candidate.Id, resolvedSpace, StringComparison.OrdinalIgnoreCase));
        var (providerSandbox, modelSandbox) = ProviderSandbox(providerKey, modelId);
        var choice = SandboxScopes.Resolve(new SandboxScopeValues
        {
            Session = own,
            Model = modelSandbox,
            Provider = providerSandbox,
            Project = project?.Sandbox,
            Space = space?.Settings.NewSessions?.Sandbox,
            AllSessions = allSessions,
        });
        var projectSetsSandbox = project is not null && _configStore.ProjectSetsSandbox(project.ProjectPath);
        if (choice.Profile is not { } name) return new(choice, null, null, projectSetsSandbox);
        try
        {
            return new(choice, SandboxProfiles.Resolve(name, profiles), null, projectSetsSandbox);
        }
        catch (SandboxProfileException exception)
        {
            return new(choice, null, exception.Message, projectSetsSandbox);
        }
    }

    /// <summary>
    /// Gets the sandbox a session is created with when another session creates it: the profile of its creator when
    /// the creator is sandboxed, saved as the new session's own choice so that no later setting makes it less
    /// sandboxed than the session that made it. Null when no sandboxed session creates it.
    /// </summary>
    /// <param name="creatorSessionId">The session that creates it, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The profile name, or null.</returns>
    public async Task<string?> GetCreatedSessionSandboxAsync(string? creatorSessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(creatorSessionId)) return null;
        var creator = await GetSessionSandboxAsync(creatorSessionId, cancellationToken).ConfigureAwait(false);
        return creator.Choice.Profile;
    }

    /// <summary>
    /// Saves the sandbox chosen for an owned session: from its next command on, it runs in that sandbox, or in none
    /// with <c>off</c>, whatever its scopes say. Null goes back to the scopes.
    /// </summary>
    /// <param name="session">The session, resolved by the owner.</param>
    /// <param name="sandbox">A profile name, <c>off</c>, or null.</param>
    /// <returns>A task that completes once the choice is saved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closing.</exception>
    internal Task SetOwnedSandboxAsync(SessionViewDescriptor session, string? sandbox)
    {
        ArgumentNullException.ThrowIfNull(session);
        sandbox = NormalizeOptionalText(sandbox);
        return AdmitAsync(async () =>
        {
            using var lease = HoldForWrite(session.SessionId);
            ObjectDisposedException.ThrowIf(_forwarding.IsClosed, this);
            await GetActorForWork(session.SessionId).QueryAsync(async actorCancellationToken =>
            {
                if (_entries.TryGetValue(session.SessionId, out var entry) && !entry.IsTerminated) entry.OwnSandbox = sandbox;
                var localState = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, actorCancellationToken).ConfigureAwait(false);
                if (!string.Equals(NormalizeOptionalText(localState?.Sandbox), sandbox, StringComparison.Ordinal))
                {
                    if (localState is null)
                    {
                        localState = new SessionViewLocalState();
                        CopySessionMetadata(session, localState);
                    }

                    localState.Sandbox = sandbox;
                    await _sessionViewCatalog.JournalStore.AppendStateAsync(session, localState, actorCancellationToken).ConfigureAwait(false);
                }

                session.Sandbox = sandbox;
                return true;
            }, CancellationToken.None).ConfigureAwait(false);
            PublishSessionCatalogEvent(session);
        }, CancellationToken.None);
    }

    // The profile a session keeps as its own choice when it changes provider: the one it is sandboxed with now, when
    // it has no choice of its own and the new provider or model would not sandbox it. Null when nothing is kept.
    private async Task<string?> KeptSandboxAsync(SessionViewDescriptor session, string providerKey, string? modelId, CancellationToken cancellationToken)
    {
        if (NormalizeOptionalText(session.Sandbox) is not null) return null;
        var current = await GetSessionSandboxAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        if (current.Choice.Profile is not { } profile) return null;
        var next = await ResolveSandboxAsync(null, providerKey, modelId, session.ProjectRef, session.SpaceId, cancellationToken).ConfigureAwait(false);
        return next.IsSandboxed ? null : profile;
    }

    // The sandbox a new session saves as its own: the one of the sandboxed session that creates it, whatever was asked
    // (an agent never makes a session less sandboxed than itself), else the one asked for, else none. A session an
    // agent creates takes what the agent asks only where the settings of the user do not sandbox it: an agent turns a
    // sandbox on, it never takes one away nor replaces the one the user chose.
    private async Task<string?> CreatedSandboxAsync(string? asked, AltaActorProvenance? createdBy, string? providerKey, string? modelId, string? projectId, string? spaceId,
        CancellationToken cancellationToken)
    {
        if (await GetCreatedSessionSandboxAsync(createdBy?.SourceSessionId, cancellationToken).ConfigureAwait(false) is { } creator) return creator;
        if (NormalizeOptionalText(asked) is not { } wanted) return null;
        if (string.IsNullOrWhiteSpace(createdBy?.SourceSessionId)) return wanted;
        if (string.Equals(wanted, SandboxProfiles.Off, StringComparison.OrdinalIgnoreCase)) return null;
        var scoped = await ResolveSandboxAsync(null, providerKey, modelId, projectId, spaceId, cancellationToken).ConfigureAwait(false);
        return scoped.IsSandboxed ? null : wanted;
    }

    // The sandbox of a provider, and of a model of it, as the user's configuration sets them.
    private (string? Provider, string? Model) ProviderSandbox(string? providerKey, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(providerKey)) return (null, null);
        IReadOnlyDictionary<string, CodeAltaProviderDocument>? providers;
        try
        {
            providers = _configStore.LoadGlobal().Providers;
            Volatile.Write(ref _lastSandboxProviders, providers ?? new Dictionary<string, CodeAltaProviderDocument>());
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            providers = Volatile.Read(ref _lastSandboxProviders);
        }

        if (providers is null || !providers.TryGetValue(providerKey, out var provider)) return (null, null);
        var model = !string.IsNullOrWhiteSpace(modelId) && provider.ModelOverrides is { } overrides && overrides.TryGetValue(modelId, out var modelOverride)
            ? modelOverride.Sandbox
            : null;
        return (provider.Sandbox, model);
    }

    private static SandboxProfile ToSandboxProfile(CodeAltaSandboxProfileSettings settings) => new()
    {
        Name = settings.Name,
        Extends = settings.Extends,
        Write = settings.Write,
        Read = settings.Read,
        Deny = settings.Deny,
        Network = settings.Network switch
        {
            null => null,
            "off" => SandboxNetwork.Off,
            "hosts" => SandboxNetwork.Hosts,
            "on" => SandboxNetwork.On,
            // A value no version knows is the safest one: nothing goes out.
            _ => SandboxNetwork.Off,
        },
        Hosts = settings.Hosts,
        LocalPorts = settings.LocalPorts,
        Env = settings.Env,
        McpServers = settings.McpServers,
        Outside = settings.Outside switch
        {
            null => null,
            "ask" => SandboxOutside.Ask,
            _ => SandboxOutside.Never,
        },
    };
}
