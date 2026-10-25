using System.Runtime.CompilerServices;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Sandbox;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The sandbox of the sessions for the window: whether it can run here and its check, the setting of every session,
/// the profiles of the user, where a sandbox is chosen (providers, models, spaces, projects), and the sandbox of one
/// session or of the one about to be created.
/// </summary>
[NeoRpcService("sandboxSettings", Version = 1)]
internal sealed partial class SandboxSettingsService
{
    private readonly CodeAltaConfigStore? _store;
    private readonly SessionRuntimeService? _runtime;
    private readonly OwnedSessionCommandService? _commands;
    private readonly ProjectCatalog? _projects;
    private readonly SpaceCatalog? _spaces;
    private readonly string? _epoch;
    private readonly Lock _gate = new();

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal SandboxSettingsService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal SandboxSettingsService(CodeAltaConfigStore store, SessionRuntimeService runtime, OwnedSessionCommandService commands, ProjectCatalog projects,
        SpaceCatalog spaces, string epoch)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(spaces);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_store, _runtime, _commands, _projects, _spaces, _epoch) = (store, runtime, commands, projects, spaces, epoch);
    }

    /// <summary>Reads what the Sandbox page shows: the availability, the setting of every session, the profiles and the scopes that choose one.</summary>
    [NeoRpcMethod("read")]
    public async Task<SandboxSettingsResponse> ReadAsync(SandboxSettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refused(request.ExpectedEpoch) is { } status) return SandboxSettingsResponse.Failed(status);
        CodeAltaSandboxSettings settings;
        try
        {
            lock (_gate) settings = _store!.LoadGlobalSandbox();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return SandboxSettingsResponse.Failed("config_invalid");
        }

        var availability = _runtime!.GetSandboxAvailability();
        var userProfiles = _runtime.ReadSandboxSettings().Profiles;
        var profiles = new List<SandboxProfileItem>();
        foreach (var name in SandboxProfiles.Names([]))
        {
            if (settings.Profiles.Any(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            profiles.Add(SandboxProfileItem.FromResolved(SandboxProfiles.Resolve(name, []), builtIn: true));
        }

        foreach (var written in settings.Profiles)
        {
            string? problem = null;
            try
            {
                _ = SandboxProfiles.Resolve(written.Name, userProfiles);
            }
            catch (SandboxProfileException exception)
            {
                problem = exception.Message;
            }

            profiles.Add(SandboxProfileItem.FromWritten(written, problem));
        }

        return new("ok", new SandboxAvailabilityItem(availability.IsAvailable, availability.Backend, availability.Reason, availability.Setup,
                [.. availability.Networks.Order().Select(static network => network.ToString().ToLowerInvariant())]),
            settings.AllSessions, profiles, await ScopesAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Saves the sandbox of every session: a profile, or null for none.</summary>
    [NeoRpcMethod("saveAllSessions")]
    public SandboxStatusResponse SaveAllSessions(SandboxSaveAllSessionsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refused(request.ExpectedEpoch) is { } refused) return new(refused);
        var profile = Text(request.Profile);
        if (profile is not null && !IsProfile(profile)) return new("invalid", $"'{profile}' is not a sandbox profile.");
        return Mutate(settings => settings with { AllSessions = profile });
    }

    /// <summary>Saves a profile of the user, under a new name or under the one it had; a profile that does not resolve is not kept.</summary>
    [NeoRpcMethod("saveProfile")]
    public SandboxStatusResponse SaveProfile(SandboxSaveProfileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Profile);
        if (Refused(request.ExpectedEpoch) is { } refused) return new(refused);
        var item = request.Profile;
        var name = Text(item.Name);
        if (name is null || !SandboxProfiles.IsValidName(name)) return new("invalid", "A profile is named with letters, digits, `-` and `_`.");
        if (SandboxProfiles.Names([]).Contains(name, StringComparer.OrdinalIgnoreCase)) return new("invalid", $"'{name}' is a profile of CodeAlta: name the new profile otherwise, and have it extend '{name}'.");
        var original = Text(request.OriginalName);
        var written = item.ToSettings(name);
        return Mutate(settings =>
        {
            if ((original is null || !string.Equals(original, name, StringComparison.OrdinalIgnoreCase))
                && settings.Profiles.Any(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new SandboxSettingsException("exists", $"A profile is already named '{name}'.");
            var others = settings.Profiles.Where(profile => !string.Equals(profile.Name, original ?? name, StringComparison.OrdinalIgnoreCase));
            var renamed = original is not null && !string.Equals(original, name, StringComparison.OrdinalIgnoreCase);
            return settings with
            {
                Profiles = [.. others, written],
                AllSessions = renamed && string.Equals(settings.AllSessions, original, StringComparison.OrdinalIgnoreCase) ? name : settings.AllSessions,
            };
        }, check: name);
    }

    /// <summary>Removes a profile of the user. The sessions that a scope still sandboxes with it run no command until it is fixed.</summary>
    [NeoRpcMethod("deleteProfile")]
    public SandboxStatusResponse DeleteProfile(SandboxDeleteProfileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refused(request.ExpectedEpoch) is { } refused) return new(refused);
        var name = Text(request.Name);
        if (name is null) return new("invalid", "Name the profile to remove.");
        return Mutate(settings => settings with { Profiles = [.. settings.Profiles.Where(profile => !string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))] });
    }

    /// <summary>Chooses the sandbox of a provider, of one of its models, of a space or of a project: a profile, <c>off</c>, or null for none.</summary>
    [NeoRpcMethod("setScope")]
    public async Task<SandboxStatusResponse> SetScopeAsync(SandboxSetScopeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refused(request.ExpectedEpoch) is { } refused) return new(refused);
        var id = Text(request.Id);
        var sandbox = Text(request.Sandbox);
        if (id is null) return new("invalid", "Name what the sandbox is chosen for.");
        if (sandbox is not null && !sandbox.Equals(SandboxProfiles.Off, StringComparison.OrdinalIgnoreCase) && !IsProfile(sandbox))
            return new("invalid", $"'{sandbox}' is not a sandbox profile.");
        try
        {
            switch (request.Kind)
            {
                case "provider":
                case "model":
                    bool saved;
                    lock (_gate) saved = _store!.SaveGlobalProviderSandbox(id, request.Kind == "model" ? Text(request.ModelId) ?? throw new ArgumentException("A model is named.") : null, sandbox);
                    return saved ? new("ok") : new("not_found", $"No provider is '{id}'.");
                case "space":
                    var space = await _spaces!.UpdateSettingsAsync(id, settings => settings with
                    {
                        NewSessions = (settings.NewSessions ?? new SpaceNewSessions()) with { Sandbox = sandbox } is { IsEmpty: false } kept ? kept : null,
                    }, cancellationToken).ConfigureAwait(false);
                    return space.Status == SpaceChangeStatus.Ok ? new("ok") : new("not_found", $"No space is '{id}'.");
                case "project":
                    return await _projects!.UpdateSandboxAsync(id, sandbox, cancellationToken).ConfigureAwait(false) switch
                    {
                        ProjectSpacesStatus.Updated or ProjectSpacesStatus.Unchanged => new("ok"),
                        ProjectSpacesStatus.NotFound => new("not_found", $"No project is '{id}'."),
                        ProjectSpacesStatus.Conflict => new("conflict", "The file of the project kept changing: try again."),
                        _ => new("unsupported", "The file of the project cannot be changed without changing something else in it."),
                    };
                default:
                    return new("invalid", "A sandbox is chosen for a provider, a model, a space or a project.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return new("write_failed", exception.Message);
        }
    }

    /// <summary>Reads the sandbox of a session: the one it runs in, where it comes from, its own choice, and the choices.</summary>
    [NeoRpcMethod("session")]
    public async Task<SandboxSessionResponse> SessionAsync(SandboxSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refused(request.ExpectedEpoch) is { } refused) return SandboxSessionResponse.Failed(refused);
        if (Text(request.SessionId) is not { } sessionId) return SandboxSessionResponse.Failed("invalid");
        return Describe(await _runtime!.GetSessionSandboxAsync(sessionId, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Chooses the sandbox of a session: a profile, <c>off</c>, or <c>scope</c> for what the settings decide.</summary>
    [NeoRpcMethod("setSession")]
    public async Task<SandboxSessionResponse> SetSessionAsync(SandboxSetSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refused(request.ExpectedEpoch) is { } refused) return SandboxSessionResponse.Failed(refused);
        if (Text(request.SessionId) is not { } sessionId || Text(request.Value) is not { } value) return SandboxSessionResponse.Failed("invalid");
        var saved = await _commands!.SetSandboxAsync(sessionId, value, cancellationToken).ConfigureAwait(false);
        return saved.Status == "ok"
            ? Describe(await _runtime!.GetSessionSandboxAsync(sessionId, cancellationToken).ConfigureAwait(false))
            : SandboxSessionResponse.Failed(saved.Status);
    }

    /// <summary>Reads the sandbox a new session would run in, with the provider, the model, the project and the space of the form.</summary>
    [NeoRpcMethod("draft")]
    public async Task<SandboxSessionResponse> DraftAsync(SandboxDraftRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refused(request.ExpectedEpoch) is { } refused) return SandboxSessionResponse.Failed(refused);
        return Describe(await _runtime!.ResolveSandboxAsync(Text(request.Own), Text(request.ProviderKey), Text(request.ModelId), Text(request.ProjectId),
            Text(request.SpaceId), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Checks a profile on this computer, step by step: the sandbox, the start of a worker, the developer tools, a write
    /// in a test project, a file of CodeAlta that stays out of reach, the network. The stream ends with <c>done</c>.
    /// </summary>
    [NeoRpcMethod("check")]
    public NeoRpcChannel<SandboxCheckEvent> Check(SandboxCheckRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(CheckAsync(request, cancellationToken), DesktopJsonContext.Default.SandboxCheckEvent);
    }

    internal async IAsyncEnumerable<SandboxCheckEvent> CheckAsync(SandboxCheckRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refused(request.ExpectedEpoch) is { } refused)
        {
            yield return new(refused, null, null, null);
            yield break;
        }

        var passed = true;
        await foreach (var step in _runtime!.CheckSandboxAsync(Text(request.Profile) ?? SandboxProfiles.Workspace, cancellationToken).ConfigureAwait(false))
        {
            passed &= step.Passed;
            yield return new("step", step.Id, step.Passed, step.Detail);
        }

        yield return new("done", null, passed, null);
    }

    private SandboxSessionResponse Describe(SessionSandbox sandbox)
        => new("ok", sandbox.Choice.Source == SandboxScope.Session ? sandbox.Choice.Profile ?? SandboxProfiles.Off : null, sandbox.Choice.Profile,
            SourceName(sandbox.Choice.Source), sandbox.Problem, [SandboxProfiles.Off, .. SandboxProfiles.Names(_runtime!.ReadSandboxSettings().Profiles)]);

    // Where a sandbox is chosen: the providers and models of config.toml, the spaces and the projects.
    private async Task<IReadOnlyList<SandboxScopeItem>> ScopesAsync(CancellationToken cancellationToken)
    {
        var scopes = new List<SandboxScopeItem>();
        try
        {
            foreach (var (key, provider) in _store!.LoadGlobal().Providers ?? [])
            {
                if (Text(provider.Sandbox) is { } sandbox) scopes.Add(new("provider", key, Text(provider.DisplayName) ?? key, null, sandbox));
                foreach (var (model, entry) in provider.ModelOverrides ?? [])
                {
                    if (Text(entry.Sandbox) is { } modelSandbox) scopes.Add(new("model", key, Text(provider.DisplayName) ?? key, model, modelSandbox));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
        }

        foreach (var space in await _spaces!.LoadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Text(space.Settings.NewSessions?.Sandbox) is { } sandbox) scopes.Add(new("space", space.Id, space.Name, null, sandbox));
        }

        foreach (var project in await _projects!.LoadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Text(project.Sandbox) is { } sandbox) scopes.Add(new("project", project.Id, project.DisplayName, null, sandbox));
        }

        return scopes;
    }

    // Saves the settings changed by a function; with `check`, the profile of that name must resolve afterwards, or the
    // settings go back to what they were.
    private SandboxStatusResponse Mutate(Func<CodeAltaSandboxSettings, CodeAltaSandboxSettings> change, string? check = null)
    {
        lock (_gate)
        {
            try
            {
                var before = _store!.LoadGlobalSandbox();
                _store.SaveGlobalSandbox(change(before));
                if (check is not null)
                {
                    try
                    {
                        _ = SandboxProfiles.Resolve(check, _runtime!.ReadSandboxSettings().Profiles);
                    }
                    catch (SandboxProfileException exception)
                    {
                        _store.SaveGlobalSandbox(before);
                        return new("invalid", exception.Message);
                    }
                }

                return new("ok");
            }
            catch (SandboxSettingsException exception)
            {
                return new(exception.Status, exception.Message);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                return new("write_failed", exception.Message);
            }
        }
    }

    private bool IsProfile(string name) => SandboxProfiles.Names(_runtime!.ReadSandboxSettings().Profiles).Contains(name, StringComparer.OrdinalIgnoreCase);

    private string? Refused(string? expectedEpoch)
        => _store is null || _runtime is null ? "unavailable" : !string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? "stale_epoch" : null;

    internal static string SourceName(SandboxScope scope) => scope switch
    {
        SandboxScope.Session => "session",
        SandboxScope.Model => "model",
        SandboxScope.Provider => "provider",
        SandboxScope.Project => "project",
        SandboxScope.Space => "space",
        SandboxScope.AllSessions => "all_sessions",
        _ => "none",
    };

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class SandboxSettingsException(string status, string message) : Exception(message)
    {
        public string Status { get; } = status;
    }
}

/// <summary>A request that names the host epoch.</summary>
internal sealed record SandboxSettingsRequest(string? ExpectedEpoch);

/// <summary>What the Sandbox page shows.</summary>
internal sealed record SandboxSettingsResponse(string Status, SandboxAvailabilityItem? Availability, string? AllSessions, IReadOnlyList<SandboxProfileItem> Profiles,
    IReadOnlyList<SandboxScopeItem> Scopes)
{
    public static SandboxSettingsResponse Failed(string status) => new(status, null, null, [], []);
}

/// <summary>Whether the sandbox can run here, and the networks it gives.</summary>
internal sealed record SandboxAvailabilityItem(bool Available, string Backend, string? Reason, string? Setup, IReadOnlyList<string> Networks);

/// <summary>A profile: one of CodeAlta as it resolves, or one of the user as it is written (a null value is inherited).</summary>
internal sealed record SandboxProfileItem(string Name, bool BuiltIn, string? Extends, string? Network, string? Outside, IReadOnlyList<string> Hosts,
    IReadOnlyList<string> Write, IReadOnlyList<string> Read, IReadOnlyList<string> Deny, IReadOnlyList<int> LocalPorts, IReadOnlyList<string> Env,
    IReadOnlyList<string> McpServers, string? Problem = null)
{
    internal static SandboxProfileItem FromResolved(ResolvedSandboxProfile profile, bool builtIn) => new(profile.Name, builtIn, null,
        profile.Network.ToString().ToLowerInvariant(), profile.Outside.ToString().ToLowerInvariant(), profile.Hosts, profile.Write, profile.Read, profile.Deny,
        profile.LocalPorts, profile.Env, profile.McpServers);

    internal static SandboxProfileItem FromWritten(CodeAltaSandboxProfileSettings profile, string? problem) => new(profile.Name, false, profile.Extends,
        profile.Network, profile.Outside, profile.Hosts ?? [], profile.Write ?? [], profile.Read ?? [], profile.Deny ?? [], profile.LocalPorts ?? [],
        profile.Env ?? [], profile.McpServers ?? [], problem);

    // The page writes every list it shows; an empty one inherits nothing.
    internal CodeAltaSandboxProfileSettings ToSettings(string name) => new(name, Clean(Extends), Strings(Write), Strings(Read), Strings(Deny), Clean(Network),
        Strings(Hosts), LocalPorts is { Count: > 0 } ports ? [.. ports.Distinct()] : null, Strings(Env), Strings(McpServers), Clean(Outside));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string>? Strings(IReadOnlyList<string>? values)
        => values?.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value.Trim()).Distinct(StringComparer.Ordinal).ToArray() is { Length: > 0 } kept ? kept : null;
}

/// <summary>A place where a sandbox is chosen.</summary>
/// <param name="Kind"><c>provider</c>, <c>model</c>, <c>space</c> or <c>project</c>.</param>
/// <param name="Id">The provider key, the space or the project.</param>
/// <param name="Name">What the window names it.</param>
/// <param name="ModelId">The model, for a model.</param>
/// <param name="Profile">The profile, or <c>off</c>.</param>
internal sealed record SandboxScopeItem(string Kind, string Id, string Name, string? ModelId, string Profile);

/// <summary>The outcome of a change.</summary>
internal sealed record SandboxStatusResponse(string Status, string? Message = null);

/// <summary>Saves the sandbox of every session.</summary>
internal sealed record SandboxSaveAllSessionsRequest(string? ExpectedEpoch, string? Profile);

/// <summary>Saves a profile of the user.</summary>
internal sealed record SandboxSaveProfileRequest(string? ExpectedEpoch, string? OriginalName, SandboxProfileItem Profile);

/// <summary>Removes a profile of the user.</summary>
internal sealed record SandboxDeleteProfileRequest(string? ExpectedEpoch, string? Name);

/// <summary>Chooses the sandbox of a provider, a model, a space or a project.</summary>
internal sealed record SandboxSetScopeRequest(string? ExpectedEpoch, string Kind, string? Id, string? ModelId, string? Sandbox);

/// <summary>Names a session.</summary>
internal sealed record SandboxSessionRequest(string? ExpectedEpoch, string? SessionId);

/// <summary>Chooses the sandbox of a session.</summary>
internal sealed record SandboxSetSessionRequest(string? ExpectedEpoch, string? SessionId, string? Value);

/// <summary>What a new session would be created with.</summary>
internal sealed record SandboxDraftRequest(string? ExpectedEpoch, string? ProviderKey, string? ModelId, string? ProjectId, string? SpaceId, string? Own);

/// <summary>The sandbox of a session.</summary>
/// <param name="Status"><c>ok</c>, or why it cannot be read or changed.</param>
/// <param name="Own">The choice of the session itself (a profile or <c>off</c>); null when its scopes decide.</param>
/// <param name="Profile">The profile it runs in; null when it is not sandboxed.</param>
/// <param name="Source">Where that comes from: <c>session</c>, <c>model</c>, <c>provider</c>, <c>project</c>, <c>space</c>, <c>all_sessions</c> or <c>none</c>.</param>
/// <param name="Problem">Why the profile cannot be used, when it cannot.</param>
/// <param name="Choices"><c>off</c> and the profiles.</param>
internal sealed record SandboxSessionResponse(string Status, string? Own, string? Profile, string Source, string? Problem, IReadOnlyList<string> Choices)
{
    public static SandboxSessionResponse Failed(string status) => new(status, null, null, "none", null, []);
}

/// <summary>Checks a profile.</summary>
internal sealed record SandboxCheckRequest(string? ExpectedEpoch, string? Profile);

/// <summary>A step of the check (<c>step</c>), its end (<c>done</c>), or why it did not run.</summary>
internal sealed record SandboxCheckEvent(string Status, string? Id, bool? Passed, string? Detail);
