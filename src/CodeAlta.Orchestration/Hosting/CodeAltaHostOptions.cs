using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Storage;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Sandbox;

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
    /// drafts and scratchpads). When unset, it is the global root.
    /// </summary>
    /// <remarks>
    /// A host with a separate state root runs beside the host that owns the global root: it shares that
    /// root's configuration, credentials, prompts, skills and project catalog, and does not rewrite the
    /// coordinator <c>AGENTS.md</c> another build maintains there.
    /// </remarks>
    public string? StateRoot { get; init; }

    /// <summary>
    /// Gets how the host reads and writes the configuration of the user (<see cref="CatalogOptions.Config"/>): the file as
    /// it is by default; overlays, a strict reading or no write for a process such as the command line.
    /// </summary>
    /// <remarks>
    /// Every store the host builds from its catalog options reads the configuration this way. Plugins that read their own
    /// tables of the file (MCP) read the file itself.
    /// </remarks>
    public CodeAltaConfigOptions Config { get; init; } = CodeAltaConfigOptions.Default;

    /// <summary>
    /// Gets whether the prompt of a session lists the skills it can use. Default true; false for a run that does without
    /// skills (<c>altacli run --no-skills</c>).
    /// </summary>
    public bool ListsSkills { get; init; } = true;

    /// <summary>
    /// Gets whether the sessions read what other tools keep in the home folder of the user (the one of
    /// <see cref="DiscoveryScope"/>, else the real one): the skills of <c>~/.agents/skills</c> and <c>~/.copilot/skills</c>,
    /// the agents of <c>~/.copilot/agents</c> and <c>~/.copilot/copilot-instructions.md</c>, for their prompts and for the
    /// <c>alta</c> commands. Default true; false for a separate profile (<c>altacli --profile</c>), which reads none of the
    /// user's home. The skills and prompts of the global root are read either way.
    /// </summary>
    public bool ReadsUserHome { get; init; } = true;

    /// <summary>
    /// Gets the parts of the workflow the prompts of the sessions leave out (<see cref="Runtime.SystemPrompts.PromptFeatures"/>):
    /// the scratchpad, the sticky notes, the follow-up tasks, the review by the user, the mode handoff and the title of a
    /// session. Null, the default, keeps them all, as CodeAlta Desktop and TUI do; <c>altacli run</c> leaves out those
    /// nobody would read. The sub-agents are not among them: they follow the options of each session
    /// (<see cref="CodeAlta.Agent.AgentSessionOptions.SubSessions"/>).
    /// </summary>
    public IReadOnlySet<string>? DisabledPromptFeatures { get; init; }

    /// <summary>
    /// Gets whether the host leaves the shared state of the global root as it is, for a process that runs beside CodeAlta
    /// Desktop or TUI on the same profile (a headless run). Default false.
    /// </summary>
    /// <remarks>
    /// The host then writes no coordinator <c>AGENTS.md</c>, seeds no view state, registers no project (a session in a
    /// folder outside the catalog is bound to a transient project of that folder, <see cref="ProjectCatalog.IsReadOnly"/>),
    /// refuses to change the spaces and the global prompts (<see cref="CatalogOptions.ReadOnlyGlobalState"/>), and starts no upkeep of the application database (no copy). It does not create the global root. What it still
    /// writes is the state of its sessions under <see cref="StateRoot"/> (their journals), its scratchpads
    /// (<see cref="ScratchpadRoot"/>), its application database, and what providers and plugins write themselves
    /// (sign-in tokens, caches). Such a host owns no startup scratchpad cleanup, and needs an application database of
    /// its own (<see cref="ApplicationDatabase"/>, for instance a <see cref="PrivateApplicationDatabase"/>) when its
    /// state root is shared with another instance.
    /// </remarks>
    public bool ReadOnlyGlobalState { get; init; }

    /// <summary>
    /// Gets the folder of the scratchpad files of the sessions, an absolute path; null, the default, for the
    /// <c>scratchpad</c> folder of the state root. A host whose state root is shared with CodeAlta Desktop names
    /// another folder: Desktop clears its scratchpad when it starts.
    /// </summary>
    public string? ScratchpadRoot { get; init; }

    /// <summary>
    /// Gets whether the sessions of the host are trusted fully, for a host without a user (a headless run): every permission
    /// request is granted whatever the mode of the session, the loads of skills that otherwise always ask included, and
    /// questions to the user are cancelled. Default false.
    /// </summary>
    /// <remarks>
    /// It implies <see cref="SessionPermissionModes"/> and <see cref="AutoApproveOwnedPermissions"/>, and overrides
    /// <see cref="ReviewOwnedCommandPermissions"/> and <see cref="ReviewOwnedPermissionsPolicy"/>: nothing is reviewed.
    /// This grants commands and file changes the privileges of the process, not a sandbox.
    /// </remarks>
    public bool FullTrust { get; init; }

    /// <summary>
    /// Gets whether the sessions of the host run without a user (a headless run): the end of their system prompt says that
    /// nobody can be asked and that the final answer is the result of the run
    /// (<see cref="Runtime.SessionRuntimeService.HeadlessGuidance"/>), and none of them reviews its changes before commit.
    /// Default false.
    /// </summary>
    public bool HeadlessInstructions { get; init; }

    /// <summary>
    /// Gets the kind of holder this host names in the leases it takes on the sessions it runs or shows
    /// (<see cref="Catalog.SessionLeaseOwner.DesktopKind"/>, <see cref="Catalog.SessionLeaseOwner.TuiKind"/>,
    /// <see cref="Catalog.SessionLeaseOwner.CliKind"/>), so that two processes on one sessions folder never run one session;
    /// null, the default, takes none.
    /// </summary>
    /// <remarks>
    /// With leases, a session another process holds is neither attached nor changed: the runtime refuses with
    /// <see cref="Runtime.SessionInUseException"/> or the status <c>session_in_use</c>.
    /// </remarks>
    public string? SessionLeaseOwner { get; init; }

    /// <summary>
    /// Gets whether the host lists the sessions another process writes into its sessions folder (the command line names each
    /// in a marker), and follows, read-only, the sessions another process holds while their journals grow. Default false; a
    /// host that owns the application database of its state root (CodeAlta Desktop and TUI) turns it on.
    /// </summary>
    public bool FollowExternalSessions { get; init; }

    /// <summary>Gets the instance-owned startup pass; null authorizes no deletion.</summary>
    public IStartupScratchpadCleanup? StartupScratchpadCleanup { get; init; }

    /// <summary>Gets what is told each step of the creation of the host, named for a start-up screen; null for nothing.</summary>
    /// <remarks>Called on the thread that creates the host. The scratchpad cleanup and the build of plugins report their own progress.</remarks>
    public Action<string>? StartupStep { get; init; }

    /// <summary>Gets the instance-owned retention admission registry to install before providers or plugins can start work.</summary>
    /// <remarks>When absent, the host creates an unknown, fail-closed registry. Supplying it does not authorize deletion.</remarks>
    public SessionRetentionProtection? RetentionProtection { get; init; }

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
    /// Gets a value indicating whether the host shows the prompts the model suggests
    /// (<see cref="CodeAlta.Agent.AgentPromptSuggestionEvent"/>). Default false: the model is not asked for
    /// suggestions nobody would see. Where it is true, the setting <c>[sessions] prompt_suggestions</c> of the user
    /// turns them on and off.
    /// </summary>
    public bool ShowsPromptSuggestions { get; init; }

    /// <summary>
    /// Gets a value indicating whether the sessions of the host work under the identity of their space: the Git
    /// author, SSH key and signing, the GitHub profile and the CLI profiles its file names
    /// (<see cref="CodeAlta.Catalog.SpaceIdentity"/>), with the CLI profiles of their project over them. Default false: every session keeps the user's usual settings, as in a host that shows no spaces.
    /// </summary>
    public bool AppliesSpaceIdentities { get; init; }

    /// <summary>
    /// Gets how the prompt of a session lists the skills, for its project and its space: <c>full</c>, <c>compact</c> or
    /// <c>auto</c> (see <see cref="CodeAlta.Orchestration.Runtime.AgentInstructionTemplateProvider.SkillPromptCatalog"/>).
    /// Null keeps the full list, as a host whose agents have no <c>alta search</c> must.
    /// </summary>
    public Func<CodeAlta.Catalog.ProjectDescriptor?, string?, string>? SkillPromptCatalog { get; init; }

    /// <summary>
    /// Gets the skills the prompt of a session lists first and in full, the most used first (see
    /// <see cref="CodeAlta.Orchestration.Runtime.AgentInstructionTemplateProvider.PreferredSkills"/>). It is asked each time the
    /// instructions are built, so it must give a session the same answer for as long as its prompt should not change. Null prefers none.
    /// </summary>
    public Func<CodeAlta.Orchestration.Runtime.PreferredSkillsRequest, IReadOnlyList<string>>? PreferredSkills { get; init; }

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

    /// <summary>
    /// Gets whether a run whose conversation another process holds (a Claude Code conversation open in VS Code or a
    /// terminal) asks the user what to do, through <see cref="Runtime.SessionRuntimeService.ConversationHolds"/>. Default
    /// false, for a host that does not show these questions: such a run fails with a message that says how to go on.
    /// </summary>
    /// <remarks>A host that enables it must show the pending questions and resolve them; a run waits until it does or ends.</remarks>
    public bool AskWhenConversationHeld { get; init; }

    /// <summary>
    /// Gets the sandbox the sessions of the host run in when they are sandboxed; null, the default, for the one of this
    /// computer (<see cref="Runtime.SessionRuntimeService.SandboxBackend"/>). Tests give another.
    /// </summary>
    public ISandboxBackend? SandboxBackend { get; init; }

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
    /// Gets an optional callback, called after <see cref="ConfigureModelProviders"/> once the plugins started, that
    /// registers the providers whose type a plugin contributes: it is given the plugin runtime.
    /// </summary>
    public Action<ModelProviderRegistry, PluginRuntimeManager>? ConfigurePluginModelProviders { get; init; }

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
