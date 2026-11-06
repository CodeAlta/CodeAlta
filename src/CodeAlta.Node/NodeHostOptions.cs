using CodeAlta.Agent;
using CodeAlta.Fleet.Moves;
using CodeAlta.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Node;

/// <summary>What a <see cref="NodeHost"/> starts with: its profile, and what tests replace.</summary>
public sealed record NodeHostOptions
{
    /// <summary>How long running turns are given to finish when the node stops, by default.</summary>
    public static readonly TimeSpan DefaultDrainLimit = TimeSpan.FromSeconds(30);

    /// <summary>How long the host is waited for once the remaining work is stopped, by default.</summary>
    public static readonly TimeSpan DefaultShutdownLimit = TimeSpan.FromSeconds(15);

    /// <summary>Gets the profile the node owns: its global root and its state root, whose lock it takes.</summary>
    public required CodeAltaInstanceProfile Profile { get; init; }

    /// <summary>Gets the folder the host takes as its current project; null for the current directory.</summary>
    public string? CurrentProjectPath { get; init; }

    /// <summary>
    /// Gets what is called once the lock of the profile is held, before the host is created; null for nothing. The logging
    /// of the process starts there: the log of a state root belongs to the instance that holds it.
    /// </summary>
    public Action? Admitted { get; init; }

    /// <summary>Gets whether the host starts its plugins. Default true.</summary>
    public bool StartPlugins { get; init; } = true;

    /// <summary>Gets whether the host stays off the network for the catalog of models.dev. Default false.</summary>
    public bool Offline { get; init; }

    /// <summary>
    /// Gets what registers the model providers in place of the providers of the configuration, for tests; null, the
    /// default, for the configured providers and those of the plugins.
    /// </summary>
    public Action<ModelProviderRegistry>? ConfigureModelProviders { get; init; }

    /// <summary>Gets the limits of the discovery of instructions and skills, for tests; null for the user's home.</summary>
    public SessionDiscoveryScope? DiscoveryScope { get; init; }

    /// <summary>Gets an explicit folder of built-in skills, for tests; null for the one of the application.</summary>
    public string? BuiltInSkillRoot { get; init; }

    /// <summary>Gets how long running turns are given to finish when the node stops (<see cref="DefaultDrainLimit"/>).</summary>
    public TimeSpan DrainLimit { get; init; } = DefaultDrainLimit;

    /// <summary>Gets how long the host is waited for once the remaining work is stopped (<see cref="DefaultShutdownLimit"/>).</summary>
    public TimeSpan ShutdownLimit { get; init; } = DefaultShutdownLimit;

    /// <summary>
    /// Gets where the node clones a repository (<c>altanode projects clone</c>, a moved session whose repository it lacks):
    /// <c>&lt;root&gt;/&lt;host&gt;/&lt;owner&gt;/&lt;repo&gt;</c>, <c>~/code</c> by default; null clones nothing.
    /// </summary>
    public string? CloneRoot { get; init; } = FleetMoveReceiver.DefaultCloneRoot;

    /// <summary>
    /// Gets whether the node runs automations: those of its profile and those its paired devices assign to it
    /// (<see cref="NodeHost.Automations"/>). Default true.
    /// </summary>
    public bool RunAutomations { get; init; } = true;

    /// <summary>
    /// Gets whether a session no scope sandboxes runs in the sandbox of a node (<see cref="Fleet.NodeApi.FleetNodeSandbox"/>)
    /// where the sandbox can run. Default true; <c>[sandbox] all_sessions</c> of the profile decides first.
    /// </summary>
    public bool SandboxByDefault { get; init; } = true;

    /// <summary>Gets the options of the automations, for tests (a clock, the credential kinds); null for the defaults.</summary>
    internal NodeAutomationOptions? Automations { get; init; }
}
