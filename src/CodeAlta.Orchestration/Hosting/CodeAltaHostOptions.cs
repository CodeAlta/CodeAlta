using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Orchestration.Hosting;

/// <summary>
/// Configures shared CodeAlta runtime composition for interactive and headless hosts.
/// </summary>
public sealed class CodeAltaHostOptions
{
    /// <summary>
    /// Gets the global CodeAlta data root. When unset, the host uses the user's <c>.alta</c> directory.
    /// </summary>
    public string? GlobalRoot { get; init; }

    /// <summary>
    /// Gets the root of the state this host alone writes (sessions, session cache, view state, prompt
    /// drafts). When unset, it is the global root.
    /// </summary>
    /// <remarks>
    /// A host with a separate state root runs beside the host that owns the global root: it shares that
    /// root's configuration, credentials, prompts, skills and project catalog, and does not rewrite the
    /// coordinator <c>AGENTS.md</c> another build maintains there.
    /// </remarks>
    public string? StateRoot { get; init; }

    /// <summary>Gets optional explicit home and lexical instruction ancestry limits.</summary>
    /// <remarks>Requires explicit absolute global/project roots; does not isolate plugins, providers, authentication or filesystem links.</remarks>
    public SessionDiscoveryScope? DiscoveryScope { get; init; }

    /// <summary>Gets an explicit absolute builtin skill root; null preserves application/source discovery.</summary>
    public string? BuiltInSkillRoot { get; init; }

    /// <summary>
    /// Gets how many command receipts the owner keeps: those of pending commands and, while there is room, of
    /// settled ones. A full owner makes room with its oldest settled receipt, whose key it still refuses to run
    /// again, and rejects a new command only when every kept receipt is pending.
    /// </summary>
    public int OwnedCommandReceiptCapacity { get; init; } = 256;

    /// <summary>
    /// Gets whether owned text sends may request bounded, manually resolved plain-command permissions.
    /// Defaults to false. Preparation and session-level callbacks still deny permission requests.
    /// </summary>
    /// <remarks>
    /// Only providers honoring the per-send permission callback can participate. This option supplies
    /// no presenter, automatic approval, renderer authorization, or restart recovery. A host enabling
    /// it must resolve pending requests through its trusted permission owner or cancel the operation.
    /// Owned sends reject a reused coordinator with different session-level interaction callbacks,
    /// even when this option is disabled, rather than inheriting another caller's default policy.
    /// </remarks>
    public bool ReviewOwnedCommandPermissions { get; init; }

    /// <summary>Gets whether the trusted host automatically grants owned tool permissions, like TUI AutoApprove. Default false.</summary>
    /// <remarks>This grants commands and file changes the host's privileges, not a sandbox. Explicit command review takes precedence.</remarks>
    public bool AutoApproveOwnedPermissions { get; init; }

    /// <summary>
    /// Gets a policy read again for every owned send, for a host whose user turns the review of commands and
    /// file changes on and off while it runs. Null, the default, keeps the two fixed options above.
    /// </summary>
    /// <remarks>
    /// When it is given it decides both: a send it answers true for is reviewed and grants nothing by itself,
    /// and a send it answers false for is automatically approved. One send reads it once, so its setup and its
    /// cleanup agree even when the policy changes while it runs.
    /// </remarks>
    public Func<bool>? ReviewOwnedPermissionsPolicy { get; init; }

    /// <summary>
    /// Gets whether the permission mode chosen for a session decides what the host does with its requests, before
    /// the policy of the host: a session in the mode that bypasses permissions is approved automatically, one in a
    /// mode that asks is reviewed. Default false: every session has the policy of the host.
    /// </summary>
    /// <remarks>
    /// With it a provider without permission modes of its own offers those of the host
    /// (<see cref="Runtime.SessionPermissionModes.HostModes"/>). A host that must review everything leaves it off.
    /// </remarks>
    public bool SessionPermissionModes { get; init; }

    /// <summary>
    /// Gets a policy read at every creation of a session by another session, for a host with
    /// <see cref="SessionPermissionModes"/>: whether the new session takes the permission mode of its creator.
    /// Null, the default, or false: a session another session creates does not ask.
    /// </summary>
    public Func<bool>? InheritPermissionModePolicy { get; init; }

    /// <summary>Gets whether owned sends expose the restricted, operation-bound ask producer. Default is false.</summary>
    public bool EnableOwnedAsks { get; init; }

    /// <summary>Gets explicit opt-in to bounded nonsecret owned provider input. Default false, independent of asks and command review.</summary>
    /// <remarks>Requires per-send callbacks and actual Started binding. Session defaults still cancel.
    /// Answers may enter provider tool results and persisted history; this is not credential entry or command permission.</remarks>
    public bool EnableOwnedUserInput { get; init; }

    /// <summary>Gets an optional environment map copied for host-created plugin adapter operation options; null preserves the original ambient snapshot.</summary>
    /// <remarks>This does not isolate the process or provider environment.</remarks>
    public IReadOnlyDictionary<string, string?>? PluginEnvironment { get; init; }

    /// <summary>
    /// Gets the project path used to seed the project catalog. When unset, the current directory is used.
    /// </summary>
    public string? CurrentProjectPath { get; init; }

    /// <summary>
    /// Gets a value indicating whether the host is running without a frontend UI.
    /// </summary>
    public bool IsHeadless { get; init; }

    /// <summary>
    /// Gets a value indicating whether interactive UI services are available to plugins and adapters.
    /// </summary>
    public bool HasInteractiveUi { get; init; }

    /// <summary>
    /// Gets a value indicating whether plugin discovery should use safe mode.
    /// </summary>
    public bool PluginSafeMode { get; init; }

    /// <summary>
    /// Gets the raw command-line arguments forwarded to plugin bootstrap.
    /// </summary>
    public IReadOnlyList<string> RawArguments { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets a value indicating whether interactive plugin build output should wait for Enter after live builds complete.
    /// </summary>
    public bool WaitForEnterAfterPluginLiveOutput { get; init; }

    /// <summary>Gets borrowed plugin startup presentation; defaults to silent feedback for every host.</summary>
    /// <remarks>Validated even when plugins are prestarted or disabled; never disposed by the host.</remarks>
    public IPluginStartupFeedback PluginStartupFeedback { get; init; } = new SilentPluginStartupFeedback();

    /// <summary>Gets the source-plugin authoring profile for runtimes started by this host; defaults to Neutral.</summary>
    /// <remarks>Terminal hosts must opt in independently of interactivity. A supplied prestarted runtime remains borrowed and is not reprofiled.</remarks>
    public PluginAuthoringProfile PluginAuthoringProfile { get; init; } = PluginAuthoringProfile.Neutral;

    /// <summary>
    /// Gets the CodeAlta application of this host, used to leave out plugins that do not support it; the
    /// default is <see cref="PluginFrontends.None"/>, a host without a user interface.
    /// </summary>
    public PluginFrontends PluginFrontend { get; init; }

    /// <summary>
    /// Gets built-in plugins to activate as part of shared host composition.
    /// </summary>
    public IReadOnlyList<BuiltInPluginDefinition> PluginBuiltIns { get; init; } = Array.Empty<BuiltInPluginDefinition>();

    /// <summary>
    /// Gets host services exposed to plugins started by this host.
    /// </summary>
    public IPluginServices? PluginServices { get; init; }

    /// <summary>
    /// Gets an optional callback that registers host-specific model providers before the agent hub is created.
    /// </summary>
    public Action<ModelProviderRegistry>? ConfigureModelProviders { get; init; }

    /// <summary>
    /// Gets a prestarted plugin runtime supplied by the caller. When set, the host will not dispose it.
    /// </summary>
    public PluginRuntimeManager? PrestartedPluginRuntime { get; init; }

    /// <summary>
    /// Gets the application database of the state root, supplied by the caller. When set, the host will not dispose it.
    /// When it is not set, the host uses the one of <see cref="PrestartedPluginRuntime"/>, and otherwise opens and
    /// disposes the one of its own state root.
    /// </summary>
    public ApplicationDatabase? ApplicationDatabase { get; init; }

    /// <summary>
    /// Gets a value indicating whether the host should start a plugin runtime when one is not supplied.
    /// </summary>
    public bool StartPlugins { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the host should own process-wide logging initialization.
    /// </summary>
    public bool OwnsLogging { get; init; }

    /// <summary>Gets the optional failure policy for backend-owned live plugin observation.</summary>
    /// <remarks>Receives captured context and the escaping observer failure. Null uses an ordinary named
    /// host logger without initializing logging. The returned operation is owned through completion;
    /// explicit dependency-retention evidence cannot be handled away by successful reporting.</remarks>
    public Func<RuntimePluginAgentEventEnvelope, Exception, ValueTask>? PluginAgentEventFailurePolicy { get; init; }
}
