using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Agent;
using CodeAlta.Automations;
using CodeAlta.Catalog;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Hosting;
using XenoAtom.Logging;

namespace CodeAlta.Node;

/// <summary>
/// The local policy of a node for automations (<c>doc/fleet/access-control.md</c>, "What the node decides"), kept by its
/// owner in <c>fleet/automation-policy.json</c> of the state root and read again before each run starts, so that a change
/// applies at once. A file that is missing or cannot be read is the default policy.
/// </summary>
internal sealed record NodeAutomationPolicy
{
    /// <summary>The name of the file, in the fleet folder of the state root.</summary>
    public const string FileName = "automation-policy.json";

    /// <summary>Credentials of any kind: personal ones (the owner's own plans and logins) and account ones.</summary>
    public const string AnyCredentials = "any";

    /// <summary>Account credentials only (API keys, gateway keys, local models): never a person's plan.</summary>
    public const string AccountCredentials = "account";

    /// <summary>Gets whether the node runs automations at all. Default true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets the credentials its runs may use: <see cref="AnyCredentials"/> (the default, the owner's own machine) or <see cref="AccountCredentials"/>.</summary>
    public string Credentials { get; init; } = AnyCredentials;

    /// <summary>Reads the policy of a fleet folder; the default when the file is missing or cannot be read.</summary>
    internal static NodeAutomationPolicy Load(string fleetRoot)
    {
        try
        {
            var path = Path.Combine(fleetRoot, FileName);
            if (!File.Exists(path)) return new();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(stream, NodeAutomationJsonContext.Default.NodeAutomationPolicy) ?? new();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // An unreadable policy runs nothing it could have refused: the strictest reading.
            return new() { Credentials = AccountCredentials };
        }
    }

    /// <summary>Writes the policy of a fleet folder, replacing the file at once.</summary>
    internal static void Save(string fleetRoot, NodeAutomationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Directory.CreateDirectory(fleetRoot);
        var path = Path.Combine(fleetRoot, FileName);
        var staged = path + ".tmp";
        File.WriteAllText(staged, JsonSerializer.Serialize(policy, NodeAutomationJsonContext.Default.NodeAutomationPolicy));
        File.Move(staged, path, overwrite: true);
    }
}

/// <summary>Who assigned an automation to the node, and what they assigned.</summary>
/// <param name="DeviceId">The device that assigned it.</param>
/// <param name="DeviceName">Its name at that time.</param>
/// <param name="Fingerprint">The fingerprint of the definition it assigned: what the node runs, and nothing else.</param>
/// <param name="AssignedAt">When it was assigned.</param>
internal sealed record NodeAutomationAssignment(string DeviceId, string DeviceName, string Fingerprint, DateTimeOffset AssignedAt);

/// <summary>What <see cref="NodeAutomations"/> starts with.</summary>
internal sealed record NodeAutomationOptions
{
    /// <summary>Gets the clock of the schedules, the runs and the claims.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Gets the time zone the schedules are read in.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>Gets what tells the credential kind of a provider; null for <see cref="AutomationCredentials.Classify"/> on the configuration of the profile.</summary>
    public Func<ModelProviderDescriptor, AutomationCredentialKind>? CredentialKind { get; init; }

    /// <summary>Gets who claims the occurrences of the schedules: the id of this node.</summary>
    public string Holder { get; init; } = "node:" + Environment.MachineName;

    /// <summary>Gets whether the repositories of the projects are watched for the event triggers (issues, pull requests). Default true.</summary>
    public bool WatchRepositories { get; init; } = true;
}

/// <summary>
/// The automations of a node (<c>doc/fleet/node.md</c>, "Automations and statistics"): the engine of
/// <c>CodeAlta.Automations</c> over the runtime of the node, which fires the schedules and the triggers of the
/// automations of the profile and of those the paired devices assigned to it, kept in <c>fleet/assigned-automations.toml</c>
/// so that they fire while those devices and any server are away. Each due time is claimed once
/// (<c>fleet/automation-claims.json</c>); before a run starts, the node checks its own policy, the credential kind of the
/// provider the run would use, and, for an assigned automation, that the device that assigned it may still do so and
/// that the definition is the one it assigned.
/// </summary>
internal sealed class NodeAutomations : IFleetNodeAutomations, IAutomationRunGate, IAsyncDisposable
{
    /// <summary>The file of the automations the devices assigned, in the fleet folder of the state root.</summary>
    public const string AssignedFileName = "assigned-automations.toml";

    /// <summary>The file of who assigned them.</summary>
    public const string AssignmentsFileName = "assigned-automations.json";

    /// <summary>The file of the claimed occurrences.</summary>
    public const string ClaimsFileName = "automation-claims.json";

    private const int MaximumRuns = 200;

    private readonly CodeAltaHost _host;
    private readonly NodeAutomationOptions _options;
    private readonly string _fleetRoot;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, NodeAutomationAssignment> _assignments;
    private readonly Logger _logger = LogManager.GetLogger("CodeAlta.Node.Automations");
    private int _disposed;

    /// <summary>Creates the automations of a node; nothing fires before <see cref="Start"/>.</summary>
    /// <param name="host">The runtime of the node.</param>
    /// <param name="options">The clock and what tests replace.</param>
    internal NodeAutomations(CodeAltaHost host, NodeAutomationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _options = options ?? new();
        var catalog = host.CatalogOptions;
        _fleetRoot = catalog.FleetRoot;
        _assignments = LoadAssignments(Path.Combine(_fleetRoot, AssignmentsFileName));
        Engine = new AutomationService(catalog.ConfigPath, token => host.ProjectCatalog.LoadAsync(token),
            new AutomationStateStore(Path.Combine(catalog.StateRoot, "automations.json"), pausedByDefault: false),
            new AutomationRunner(host, this), _options.TimeProvider, _options.TimeZone,
            _options.WatchRepositories ? new GitAutomationFeed() : null,
            () => [.. (host.PluginRuntime?.ActivePlugins ?? []).Select(static plugin => plugin.Instance).OfType<CodeAlta.Plugins.Abstractions.IIssueEventSource>()],
            // A command runs with the identity of the space of its project, as in CodeAlta Desktop.
            new ShellAutomationCommands(projectId => host.Identities.ResolveAsync(null, projectId).GetAwaiter().GetResult().Environment),
            entry => entry.Definition.Script is { } script ? host.CodeModeScripts.FindHash(script, entry.ProjectPath) : null,
            new AutomationClaimStore(Path.Combine(_fleetRoot, ClaimsFileName), _options.Holder),
            Path.Combine(_fleetRoot, AssignedFileName));
    }

    /// <summary>Gets the engine.</summary>
    internal AutomationService Engine { get; }

    /// <summary>
    /// Gets or sets what finds a paired device by its id, as the node trusts it now; null until the fleet part of the node
    /// starts, and then an assigned automation does not run.
    /// </summary>
    internal Func<string, TrustedDevice?>? Devices { get; set; }

    /// <summary>Starts reading the automations and firing their triggers.</summary>
    internal void Start() => Engine.Start();

    /// <summary>Gets who assigned an automation; null for one the node was not assigned.</summary>
    internal NodeAutomationAssignment? AssignmentOf(string id)
    {
        lock (_gate) return _assignments.GetValueOrDefault(id);
    }

    /// <inheritdoc />
    public string? Check(AutomationEntry entry, ModelProviderDescriptor provider)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(provider);
        var policy = NodeAutomationPolicy.Load(_fleetRoot);
        if (!policy.Enabled) return "This machine runs no automations: its owner turned them off.";
        if (entry.Source.IsAssigned)
        {
            if (AssignmentOf(entry.Id) is not { } assignment) return "No device of yours assigned this automation to this machine.";
            if (!string.Equals(assignment.Fingerprint, entry.Definition.Fingerprint(), StringComparison.Ordinal))
                return "This automation changed on this machine since it was assigned: it runs only as its device assigned it.";
            if (Devices?.Invoke(assignment.DeviceId) is not { } device)
                return $"The device that assigned this automation ({assignment.DeviceName}) is no longer paired with this machine.";
            if (!device.Permissions.HasFlag(DevicePermissions.Automate))
                return $"The device that assigned this automation ({device.Name}) may no longer give automations to this machine.";
        }

        var kind = _options.CredentialKind?.Invoke(provider) ?? Classify(provider);
        if (kind == AutomationCredentialKind.Unknown)
            return $"The credential the provider '{provider.ProviderId.Value}' runs with cannot be told: an unattended run uses a provider whose credential is known.";
        if (kind == AutomationCredentialKind.Personal && policy.Credentials != NodeAutomationPolicy.AnyCredentials)
            return $"The provider '{provider.ProviderId.Value}' runs with a personal plan, and this machine runs automations with account credentials only.";
        return null;
    }

    /// <inheritdoc />
    public async Task<FleetAutomationInfo> PutAsync(FleetAutomationCaller caller, FleetAutomationPutParams parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Definition is not { Length: > 0 and <= AutomationConfig.MaximumContentLength } text)
            throw Invalid("The automation is one table of a configuration file.");
        var (definitions, faults) = AutomationConfig.Read(text, new AutomationSource("automations.put", null, null) { IsAssigned = true });
        if (faults.Count > 0) throw Invalid(faults[0].Message);
        if (definitions.Count != 1) throw Invalid("The automation is one table of a configuration file.");
        var definition = definitions[0];
        // A command of a trigger runs on this machine without anybody to approve it: only its owner writes one here.
        if (definition.Triggers.Any(static trigger => trigger.IsCommand))
            throw Invalid("A command trigger runs a command on this machine: it is not taken from another device.");
        string? project = null;
        if (parameters.Repository is { } repository)
        {
            project = await FindProjectAsync(repository, cancellationToken).ConfigureAwait(false)
                ?? throw new FleetException(FleetNodeErrorCodes.ProjectNotFound, null,
                    $"This machine has no project of the repository {repository}: clone it there first (altanode projects clone).");
        }

        if (Engine.Snapshot.Find(definition.Id) is { Source.IsAssigned: false })
            throw Invalid("An automation of this machine has the identifier of this one.");
        if (await Engine.SaveAssignedAsync(definition with { Project = project }, cancellationToken).ConfigureAwait(false) is { } refused) throw Invalid(refused);
        if (Engine.Snapshot.Find(definition.Id) is not { Source.IsAssigned: true } saved) throw Invalid("The automation was not read back on this machine.");
        var assignment = new NodeAutomationAssignment(caller.DeviceId, caller.Name, saved.Definition.Fingerprint(), _options.TimeProvider.GetUtcNow());
        lock (_gate)
        {
            _assignments[saved.Id] = assignment;
            SaveAssignments();
        }

        Log($"{caller.Name} ({caller.DeviceId}) assigned the automation '{saved.Definition.Name}' ({saved.Id}).");
        return Describe(saved, assignment);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(FleetAutomationCaller caller, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var removed = false;
        if (Engine.Snapshot.Find(id) is { Source.IsAssigned: true })
        {
            if (await Engine.DeleteAsync(id, cancellationToken).ConfigureAwait(false) is { } refused) throw Invalid(refused);
            removed = true;
        }

        lock (_gate)
        {
            if (_assignments.Remove(id)) SaveAssignments();
        }

        if (removed) Log($"{caller.Name} ({caller.DeviceId}) removed the automation {id}.");
        return removed;
    }

    /// <inheritdoc />
    public IReadOnlyList<FleetAutomationInfo> List(FleetAutomationCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return [.. Engine.Snapshot.Entries.Where(static entry => entry.Source.IsAssigned).Select(entry => Describe(entry, AssignmentOf(entry.Id)))];
    }

    /// <inheritdoc />
    public async Task<FleetAutomationRunInfo> RunAsync(FleetAutomationCaller caller, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (Engine.Snapshot.Find(id) is not { Source.IsAssigned: true })
            throw new FleetException(FleetNodeErrorCodes.AutomationNotFound, null, "No automation of yours on this machine has that id.");
        var run = await Engine.RunAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new FleetException(FleetNodeErrorCodes.AutomationNotFound, null, "No automation of yours on this machine has that id.");
        Log($"{caller.Name} ({caller.DeviceId}) ran the automation {id}: {run.Status}.");
        return Describe(run);
    }

    /// <inheritdoc />
    public IReadOnlyList<FleetAutomationRunInfo> Runs(FleetAutomationCaller caller, string? id, int limit)
    {
        ArgumentNullException.ThrowIfNull(caller);
        limit = Math.Clamp(limit, 1, MaximumRuns);
        HashSet<string> assigned;
        lock (_gate) assigned = [.. _assignments.Keys];
        foreach (var entry in Engine.Snapshot.Entries.Where(static entry => entry.Source.IsAssigned)) assigned.Add(entry.Id);
        if (id is not null)
            return assigned.Contains(id) ? [.. Engine.Runs(id, limit).Select(Describe)] : [];
        // The runs of the automations of the profile stay with its owner on this machine.
        return [.. Engine.Runs(null, AutomationStateStore.MaximumRuns).Where(run => assigned.Contains(run.AutomationId)).Take(limit).Select(Describe)];
    }

    /// <summary>Stops the schedules and the triggers; sessions that run keep going.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await Engine.DisposeAsync().ConfigureAwait(false);
    }

    internal static FleetAutomationRunInfo Describe(AutomationRun run)
        => new(run.Id, run.AutomationId, run.StartedAt, run.Trigger, run.Status)
        {
            Name = run.Name, SessionId = run.SessionId, Detail = run.Detail, Message = run.Message, EndedAt = run.EndedAt,
        };

    private FleetAutomationInfo Describe(AutomationEntry entry, NodeAutomationAssignment? assignment)
        => new(entry.Id, entry.Definition.Name, entry.Definition.Enabled, entry.Definition.Fingerprint())
        {
            AssignedBy = assignment?.DeviceId, AssignedByName = assignment?.DeviceName, AssignedAt = assignment?.AssignedAt,
            Project = entry.ProjectPath, NextDue = Engine.NextDue(entry.Id), Running = Engine.IsRunning(entry.Id), Problem = entry.Problem,
        };

    private AutomationCredentialKind Classify(ModelProviderDescriptor provider)
    {
        CodeAltaProviderDocument? configuration = null;
        try
        {
            configuration = new CodeAltaConfigStore(_host.CatalogOptions).LoadGlobalProviderDefinitions(includeDisabled: true)
                .FirstOrDefault(candidate => string.Equals(candidate.ProviderKey, provider.ProviderId.Value, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Without the configuration, a provider whose kind depends on it is unknown, and refused.
        }

        return AutomationCredentials.Classify(provider.ProviderType, configuration);
    }

    // The project of the node whose repository has this key.
    private async Task<string?> FindProjectAsync(string repository, CancellationToken cancellationToken)
    {
        var key = RepositoryKey.Normalize(repository) ?? repository.Trim();
        foreach (var project in await _host.ProjectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (project.Archived || string.IsNullOrWhiteSpace(project.ProjectPath)) continue;
            if (string.Equals(await RepositoryKey.ReadAsync(project.ProjectPath, cancellationToken).ConfigureAwait(false), key, StringComparison.Ordinal))
                return project.ProjectPath;
        }

        return null;
    }

    private static FleetException Invalid(string message) => new(FleetNodeErrorCodes.AutomationInvalid, null, message);

    private static Dictionary<string, NodeAutomationAssignment> LoadAssignments(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = JsonSerializer.Deserialize(stream, NodeAutomationJsonContext.Default.DictionaryStringNodeAutomationAssignment);
            return new((read ?? []).Where(static pair => pair.Value is { DeviceId.Length: > 0, Fingerprint.Length: > 0 }), StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Without a record of who assigned them, the assigned automations do not run until assigned again.
            return new(StringComparer.Ordinal);
        }
    }

    // Under the gate.
    private void SaveAssignments()
    {
        try
        {
            Directory.CreateDirectory(_fleetRoot);
            var path = Path.Combine(_fleetRoot, AssignmentsFileName);
            var staged = path + ".tmp";
            File.WriteAllText(staged, JsonSerializer.Serialize(_assignments, NodeAutomationJsonContext.Default.DictionaryStringNodeAutomationAssignment));
            File.Move(staged, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"The assignments of the automations could not be written: {exception.Message}");
        }
    }

    private void Log(string message)
    {
        if (LogManager.IsInitialized) _logger.Info(message);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(NodeAutomationPolicy))]
[JsonSerializable(typeof(Dictionary<string, NodeAutomationAssignment>))]
internal sealed partial class NodeAutomationJsonContext : JsonSerializerContext;
