using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Agent;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Sandbox;

namespace CodeAlta.Fleet.NodeApi;

// What the node decides from the signed policies of its servers (doc/fleet/security-and-privacy.md, policies and the kill
// switch): the kill switch refuses new work of the fleet and stops the work under way; the rules of the organization a space
// is linked to narrow what a session started from another device or by an agent of another machine may use, never widening
// what the node itself allows. What a person does at the machine is not the work of the fleet: it goes on.
public sealed partial class FleetNodeService
{
    private FleetWorkSessions? _work;

    /// <summary>Gets or sets the policies of the servers of the node, as it verified them; null for a node that joined none.</summary>
    public FleetPolicyGuard? Policy { get; set; }

    /// <summary>Gets the sessions this node started as work of the fleet, kept in its fleet folder.</summary>
    internal FleetWorkSessions Work => LazyInitializer.EnsureInitialized(ref _work, () => new FleetWorkSessions(_host.CatalogOptions.FleetRoot));

    /// <summary>
    /// Applies the policies as verified: when a stop reaches this machine, the sessions of the work of the fleet that run here
    /// are stopped. A node calls it once a link verified a newer version, and at its start with the versions it kept.
    /// </summary>
    /// <param name="cancellationToken">Cancels before the next session is stopped.</param>
    /// <returns>The sessions stopped.</returns>
    public async Task<IReadOnlyList<string>> ApplyPolicyAsync(CancellationToken cancellationToken)
    {
        if (Policy?.Halt is not { } halt) return [];
        var stopped = await StopWorkAsync(Work.List().Select(static item => item.SessionId), halt.Describe(_options.Name), cancellationToken).ConfigureAwait(false);
        if (stopped.Count > 0) LogInfo($"The kill switch of {halt.Server} stopped the work of the fleet here: {string.Join(", ", stopped)}.");
        return stopped;
    }

    /// <summary>
    /// Applies the policies the node holds to the work under way, whatever started it and whenever: while a stop reaches this
    /// machine, the sessions of the fleet at work are stopped (also those that started before the node read the stop, as at its
    /// start); otherwise those past the longest time of their policy. The node calls it on a timer.
    /// </summary>
    /// <param name="cancellationToken">Cancels before the next session is stopped.</param>
    /// <returns>The sessions stopped.</returns>
    public async Task<IReadOnlyList<string>> EnforcePolicyAsync(CancellationToken cancellationToken)
    {
        var halted = await ApplyPolicyAsync(cancellationToken).ConfigureAwait(false);
        var over = await EnforceSessionLengthAsync(cancellationToken).ConfigureAwait(false);
        return [.. halted, .. over];
    }

    /// <summary>Stops the sessions of the work of the fleet that ran longer than the policy of their space allows.</summary>
    /// <param name="cancellationToken">Cancels before the next session is stopped.</param>
    /// <returns>The sessions stopped.</returns>
    public async Task<IReadOnlyList<string>> EnforceSessionLengthAsync(CancellationToken cancellationToken)
    {
        if (Policy is not { } policy) return [];
        var now = _options.TimeProvider.GetUtcNow();
        var over = Work.List().Where(item => policy.RulesFor(item.SpaceId)?.MaxSessionMinutes is { } minutes && now - item.StartedAt > TimeSpan.FromMinutes(minutes))
            .Select(static item => item.SessionId).ToArray();
        if (over.Length == 0) return [];
        var stopped = await StopWorkAsync(over, "The policy of the organization stops a session of the fleet after its longest time.", cancellationToken).ConfigureAwait(false);
        if (stopped.Count > 0) LogInfo($"Sessions of the fleet past the longest time of their policy were stopped: {string.Join(", ", stopped)}.");
        return stopped;
    }

    // The sessions among these that are at work, each stopped with its tree.
    private async Task<IReadOnlyList<string>> StopWorkAsync(IEnumerable<string> sessionIds, string reason, CancellationToken cancellationToken)
    {
        var ids = sessionIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return [];
        var atWork = await _host.Commands.ReadSessionsAtWorkAsync(ids).ConfigureAwait(false);
        var stopped = new List<string>();
        foreach (var sessionId in ids.Where(atWork.Contains))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _host.RuntimeService.AbortTreeAsync(sessionId, reason, cancellationToken).ConfigureAwait(false);
                stopped.Add(sessionId);
            }
            catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or IOException)
            {
                LogWarn($"The session {sessionId} could not be stopped by the policy: {exception.Message}");
            }
        }

        return stopped;
    }

    // New work of the fleet is refused while a stop reaches this machine.
    private void RequireNotHalted()
    {
        if (Policy?.Halt is { } halt) throw new FleetException(FleetNodeErrorCodes.FleetStopped, null, halt.Describe(_options.Name));
    }

    // The rules of the organization of a space for a start, checked before anything exists: who may start, the provider, the model.
    private FleetPolicyRules? RequirePolicyStart(string? spaceId, ModelProviderDescriptor provider, string? modelId, bool agent)
    {
        RequireNotHalted();
        if (Policy?.RulesFor(spaceId) is not { } rules) return null;
        if (!agent && rules.RemoteStarts == false)
            throw Refused("The policy of the organization lets nobody start a session on another machine: start it on that machine itself.");
        if (agent && rules.AgentStarts == false) throw Refused("The policy of the organization lets no agent start work on another machine.");
        if (FleetPolicyGuard.Refusal(rules, provider.ProviderType, provider.ProviderId.Value, modelId ?? provider.DefaultModelId) is { } refusal) throw Refused(refusal);
        if (rules.Network is not null && rules.Sandbox is null && !agent)
            throw Refused($"The policy of the organization limits the network of its work to '{rules.Network}': it names the sandbox it runs in.");
        return rules;
    }

    // A prompt is checked against the rules of the space of its session with the provider and the model it runs with: those it
    // names, else the session's. A selection that names another provider is checked as a start is, so that a session started
    // with an allowed provider cannot go on with one the rules do not allow.
    private void RequirePolicySelection(CodeAlta.Catalog.SessionViewDescriptor session, FleetModelSelection? selection)
    {
        RequireNotHalted();
        if (Policy?.RulesFor(session.SpaceId) is not { } rules) return;
        var providerKey = selection?.ProviderKey ?? session.ResolvedProviderKey;
        var provider = _host.ModelProviderRegistry.ListProviders().FirstOrDefault(value => string.Equals(value.ProviderId.Value, providerKey, StringComparison.Ordinal));
        var switched = !string.Equals(providerKey, session.ResolvedProviderKey, StringComparison.Ordinal);
        var model = selection?.ModelId ?? (switched ? provider?.DefaultModelId : null);
        if (FleetPolicyGuard.Refusal(rules, provider?.ProviderType, providerKey, model) is { } refusal) throw Refused(refusal);
    }

    // The widest permission mode the provider offers within the policy; null when its default already is.
    private string? PolicyPermissionMode(string providerKey, string? ceiling)
    {
        if (ceiling is null) return null;
        var rank = SessionPermissionModes.Rank(ceiling);
        var choices = _host.Commands.GetNewSessionPermissionModes(providerKey);
        if (SessionPermissionModes.Rank(choices.DefaultMode) <= rank) return null;
        return choices.Modes.Where(mode => SessionPermissionModes.Rank(mode) <= rank).OrderByDescending(SessionPermissionModes.Rank).FirstOrDefault()
            ?? throw Refused($"The provider '{providerKey}' offers no permission mode as narrow as '{ceiling}', which the policy of the organization requires.");
    }

    // Why a sandbox breaks the floor and the network of the rules; null when it holds.
    private string? PolicySandboxProblem(string? sandbox, FleetPolicyRules rules) => FleetPolicyGuard.SandboxProblem(sandbox, rules, _host.RuntimeService.ReadSandboxSettings().Profiles);

    // The sandbox of the rules, given to a session before its first prompt; a session that cannot have it is never sent one.
    private async Task ApplyPolicySandboxAsync(string sessionId, FleetPolicyRules rules)
    {
        if (rules.Sandbox is not { } sandbox) return;
        if (PolicySandboxProblem(sandbox, rules) is { } problem) throw Refused($"{_options.Name} cannot run the session: {problem}.");
        if (!_host.RuntimeService.GetSandboxAvailability().IsAvailable) throw Refused($"{_options.Name} cannot start a sandbox, which the policy of the organization requires.");
        var set = await _host.Commands.SetSandboxAsync(sessionId, sandbox).ConfigureAwait(false);
        if (set.Status != "ok") throw Refused($"{_options.Name} could not give the session the sandbox '{sandbox}' the policy of the organization requires ({set.Status}).");
    }

    private static FleetException Refused(string message) => new(FleetNodeErrorCodes.PolicyRefused, null, message);
}

/// <summary>
/// The sessions a node started as work of the fleet (from another device, for an agent of another machine), with their space
/// and their start, so that a stop of the kill switch and the longest time of a policy reach them, also after a restart.
/// Kept in the fleet folder of the node, bounded, one writer.
/// </summary>
internal sealed class FleetWorkSessions
{
    /// <summary>The most sessions kept; the oldest go first.</summary>
    public const int MaxSessions = 5000;

    private const string FileName = "fleet-work-sessions.json";

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly List<FleetWorkSession> _items;

    public FleetWorkSessions(string fleetRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(fleetRoot);
        _path = Path.Combine(fleetRoot, FileName);
        _items = Load(_path);
    }

    public IReadOnlyList<FleetWorkSession> List()
    {
        lock (_gate) return [.. _items];
    }

    public void Record(string sessionId, string? spaceId, string kind, DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            if (_items.Any(item => item.SessionId == sessionId)) return;
            _items.Add(new FleetWorkSession(sessionId, spaceId, kind, startedAt));
            if (_items.Count > MaxSessions) _items.RemoveRange(0, _items.Count - MaxSessions);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                Identity.FleetFiles.WriteAtomically(_path, JsonSerializer.SerializeToUtf8Bytes(_items, FleetWorkSessionJson.Default.ListFleetWorkSession));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Held in memory meanwhile; the next session written writes it.
            }
        }
    }

    private static List<FleetWorkSession> Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), FleetWorkSessionJson.Default.ListFleetWorkSession) ?? [];
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>A session of the work of the fleet.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="SpaceId">Its space; null for the default one.</param>
/// <param name="Kind"><c>remote</c> or <c>agent</c>.</param>
/// <param name="StartedAt">When it was started.</param>
internal sealed record FleetWorkSession(string SessionId, string? SpaceId, string Kind, DateTimeOffset StartedAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(List<FleetWorkSession>))]
internal sealed partial class FleetWorkSessionJson : JsonSerializerContext;
