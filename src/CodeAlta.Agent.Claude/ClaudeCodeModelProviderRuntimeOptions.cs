using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// Options of the model provider that drives the Claude Code CLI: the version CodeAlta pins, downloaded on first use,
/// or one the user installed.
/// </summary>
/// <remarks>
/// CodeAlta starts the unmodified <c>claude</c> executable and talks to it over its stream-json input and
/// output. It never stores or forwards credentials: the CLI signs in with whatever the user set up for it
/// (<c>claude</c> then <c>/login</c>, an API key, or a cloud provider), and its usage is billed to that account.
/// The one exception is <c>ANTHROPIC_API_KEY</c>, which the CLI started by CodeAlta would use without asking:
/// <see cref="ApiKeyPolicy"/> says whether the CLI is started with it.
/// </remarks>
public sealed class ClaudeCodeModelProviderRuntimeOptions
{
    /// <summary>
    /// The permission modes of the CLI a provider can be configured with, and a session given. The provider reports
    /// them (<see cref="ModelProviderDescriptor.PermissionModes"/>).
    /// </summary>
    public static IReadOnlyList<string> PermissionModes { get; } = ["default", "acceptEdits", "plan", "auto", "dontAsk", "bypassPermissions"];

    /// <summary>
    /// Gets or initializes the provider key.
    /// </summary>
    public required string ProviderKey { get; init; }

    /// <summary>
    /// Gets or initializes the display name of the provider. The provider key is used when it is empty.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Gets or initializes the path or the name of the <c>claude</c> executable. When it is empty the version CodeAlta
    /// pins is run when it was downloaded (<see cref="PinnedCliRoot"/>), else the executable is looked for on
    /// <c>PATH</c> and in the folders its installers use.
    /// </summary>
    public string? Command { get; init; }

    /// <summary>
    /// Gets or initializes the configuration folder of Claude Code the CLI is started with (<c>CLAUDE_CONFIG_DIR</c>):
    /// its login, settings, memory and transcripts. Several providers with their own folder are several Claude
    /// accounts. A leading <c>~</c> is the home folder. When it is empty the CLI uses its own folder, and CodeAlta
    /// reads the same one (<see cref="ClaudeCodeProjectFolders.ResolveConfigRoot()"/>).
    /// </summary>
    public string? ConfigDirectory { get; init; }

    /// <summary>
    /// Gets or initializes the variables the CLI is started with besides those CodeAlta inherits (<c>env</c> of the
    /// provider): a token of the account (<c>CLAUDE_CODE_OAUTH_TOKEN</c>) signs it in where its login cannot be read, as
    /// in a sandbox on macOS. A sandbox gives them to the CLI with their values. What CodeAlta sets for the CLI itself
    /// (<c>CLAUDE_CONFIG_DIR</c> of <see cref="ConfigDirectory"/>, its own flags) is not replaced.
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or initializes the folder of Claude Code in the cache of CodeAlta, where the version CodeAlta pins
    /// (<see cref="ClaudeCodePinnedCli"/>) is downloaded; null to not look there. When <see cref="Command"/> is empty,
    /// that version is run before a Claude Code the user installed.
    /// </summary>
    public string? PinnedCliRoot { get; init; }

    /// <summary>
    /// Gets or initializes arguments added to the command line of the CLI, after those of CodeAlta.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>
    /// Gets or initializes the permission mode the CLI starts with (one of <see cref="PermissionModes"/>).
    /// When it is empty the CLI uses the mode of the user's settings. A session given a mode of its own
    /// (<see cref="Runtime.AgentTurnRequest.PermissionMode"/>) runs in that one instead.
    /// </summary>
    public string? PermissionMode { get; init; }

    /// <summary>
    /// Gets or initializes whether the CLI is given the API key of the environment (<c>ANTHROPIC_API_KEY</c>).
    /// </summary>
    public ClaudeCodeApiKeyPolicy ApiKeyPolicy { get; init; }

    /// <summary>
    /// Reads the <c>anthropic_api_key</c> setting of a provider: <c>use</c>, <c>ignore</c>, or empty to follow the
    /// answer Claude Code saved.
    /// </summary>
    /// <param name="value">The setting.</param>
    /// <returns>The policy; <see cref="ClaudeCodeApiKeyPolicy.FollowClaudeCode"/> for any other value.</returns>
    public static ClaudeCodeApiKeyPolicy ParseApiKeyPolicy(string? value)
        => value?.Trim() switch
        {
            "use" => ClaudeCodeApiKeyPolicy.Use,
            "ignore" => ClaudeCodeApiKeyPolicy.Ignore,
            _ => ClaudeCodeApiKeyPolicy.FollowClaudeCode,
        };

    /// <summary>
    /// Gets or initializes the only model of the provider, when it is pinned to one.
    /// </summary>
    public string? SingleModelId { get; init; }

    /// <summary>
    /// Gets or initializes a regular expression the identifiers of the listed models must match.
    /// </summary>
    public string? ModelsIncludeRegex { get; init; }

    /// <summary>
    /// Gets or initializes the default model of the provider.
    /// </summary>
    public string? DefaultModelId { get; init; }

    /// <summary>
    /// Gets or initializes the default reasoning effort of the provider.
    /// </summary>
    public AgentReasoningEffort? DefaultReasoningEffort { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether the provider is the default one.
    /// </summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether the provider is enabled.
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Gets or initializes the order the models are listed in; the order of the CLI by default.</summary>
    public ModelOrder ModelOrder { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether the models are sorted by name in selectors; true sets
    /// <see cref="ModelOrder"/> to <see cref="Agent.ModelOrder.Name"/>.
    /// </summary>
    [Obsolete("Use ModelOrder.", error: false)]
    public bool SortModels
    {
        get => ModelOrder != ModelOrder.Provider;
        init => ModelOrder = value ? ModelOrder.Name : ModelOrder.Provider;
    }

    /// <summary>
    /// Gets or initializes the name CodeAlta gives itself to the CLI (<c>name/version</c>).
    /// </summary>
    public string? ClientApp { get; init; }

    /// <summary>
    /// Gets or initializes how long the process of a session is kept after its last turn. The conversation is
    /// resumed from the CLI's own transcript by the next turn.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gets or initializes how long the CLI has to answer the first request after it starts.
    /// </summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or initializes how long the CLI has to answer any other control request.
    /// </summary>
    public TimeSpan ControlTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or initializes how long an interrupted turn has to end before the process is stopped.
    /// </summary>
    public TimeSpan InterruptTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or initializes how often a run that waits for another Claude process to release a conversation looks again
    /// at the registry of live sessions of the CLI.
    /// </summary>
    internal TimeSpan HeldConversationPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or initializes the catalog of models.dev the models of the CLI are looked up in; null when there is none.
    /// </summary>
    public CodeAlta.Agent.ModelCatalog.ModelsDevCatalogService? ModelCatalog { get; init; }

    /// <summary>
    /// Gets or initializes the provider of models.dev whose models the CLI runs: the models of Claude Code are
    /// those of Anthropic.
    /// </summary>
    public string ModelsDevProviderId { get; init; } = "anthropic";

    /// <summary>
    /// Gets or initializes where the headers of the transcripts of the conversations started in VS Code or a terminal are
    /// kept across the starts of CodeAlta (the application database of the instance), so that the listing of those
    /// conversations reads only the transcripts that changed; null to keep them in memory only.
    /// </summary>
    public IAgentExternalConversationHeaderStore? ExternalConversationHeaders { get; init; }

    /// <summary>
    /// Gets or initializes the processes the runtimes of the registry started (<see cref="ModelProviderRegistry.ProviderProcesses"/>),
    /// which outlive the options of one registration of the provider: an entry of the registry of live sessions of the
    /// CLI for one of them is CodeAlta's own, not another program that holds the conversation. Null keeps a set of
    /// these options only.
    /// </summary>
    public AgentProviderProcesses? ProviderProcesses { get; init; }

    /// <summary>
    /// Gets or initializes the factory of the processes. Tests replace it with a scripted CLI.
    /// </summary>
    internal IClaudeCodeTransportFactory? TransportFactory { get; init; }

    /// <summary>
    /// Gets or initializes what looks at the processes of the system for the registry of live sessions of the CLI.
    /// Tests replace it.
    /// </summary>
    internal IClaudeCodeProcessProbe? ProcessProbe { get; init; }

    private ClaudeCodeOwnProcesses? _ownProcesses;

    /// <summary>
    /// Gets the CLI processes CodeAlta runs, shared by every runtime made from these options and, through
    /// <see cref="ProviderProcesses"/>, by those of the other registrations: each attached session has a runtime of its
    /// own, while the registry of live sessions is read from another one, which must not take a process of CodeAlta for
    /// another program that holds a conversation.
    /// </summary>
    internal ClaudeCodeOwnProcesses OwnProcesses
        => LazyInitializer.EnsureInitialized(ref _ownProcesses,
            () => new ClaudeCodeOwnProcesses(ProcessProbe ?? ClaudeCodeProcessProbe.Instance, ProviderProcesses ?? new AgentProviderProcesses()));

    /// <summary>
    /// Gets or initializes what tells whether the CLI finds the transcript of a conversation it is to resume, from the
    /// working directory and the identifier of the conversation. Tests replace it; by default the folders of the
    /// configuration folder of Claude Code are looked at.
    /// </summary>
    internal Func<string, string, ClaudeCodeTranscriptPresence>? LocateTranscript { get; init; }

    /// <summary>
    /// Gets or initializes the resolver of the executable. Tests replace it.
    /// </summary>
    internal Func<ClaudeCodeCliResolution>? ResolveCli { get; init; }

    /// <summary>
    /// Gets or initializes the reader of the environment the CLI inherits. Tests replace it.
    /// </summary>
    internal Func<string, string?>? GetEnvironmentVariable { get; init; }

    /// <summary>
    /// Gets or initializes the reader of the configuration file of Claude Code (null when there is none). Tests replace it.
    /// </summary>
    internal Func<string?>? ReadClaudeConfig { get; init; }

    /// <summary>
    /// Gets or initializes the reader of the version of an executable the user installed. Tests replace it.
    /// </summary>
    internal Func<string, CancellationToken, Task<string?>>? ReadVersion { get; init; }

    /// <summary>Finds the executable the provider runs.</summary>
    internal ClaudeCodeCliResolution ResolveExecutable() => ResolveCli?.Invoke() ?? ClaudeCodeCliLocator.Resolve(Command, PinnedCliRoot);

    /// <summary>
    /// Gets the configuration folder the CLI is started with: <see cref="ConfigDirectory"/> with its <c>~</c>
    /// expanded, or null when the provider leaves it to the environment.
    /// </summary>
    internal string? ResolveConfigDirectory()
        => ClaudeCodeProjectFolders.ExpandConfigDirectory(ConfigDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Says how the account of the provider is signed in, as the start of a sentence: plain <c>claude</c> signs in
    /// the folder of Claude Code, not the folder of an account of its own.
    /// </summary>
    internal string SignInHint()
        => ResolveConfigDirectory() is { } folder
            ? $"Start Claude Code with CLAUDE_CONFIG_DIR={folder} in a terminal (the command is in the form of the provider, in Settings > Providers) and sign in with /login"
            : "Run `claude` in a terminal and sign in with /login";

    /// <summary>
    /// Reads the environment the CLI is started with: the one CodeAlta inherits, with the variables of the provider
    /// (<see cref="EnvironmentVariables"/>), and <c>CLAUDE_CONFIG_DIR</c> set to the folder of the provider when it has one.
    /// </summary>
    internal Func<string, string?> ReadEnvironment()
    {
        var system = GetEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var variables = EnvironmentVariables;
        Func<string, string?> inherited = variables.Count == 0 ? system : name => variables.TryGetValue(name, out var value) ? value : system(name);
        return ResolveConfigDirectory() is { } folder
            ? name => string.Equals(name, ClaudeCodeProjectFolders.ConfigDirectoryVariable, StringComparison.Ordinal) ? folder : inherited(name)
            : inherited;
    }

    /// <summary>
    /// Gets the configuration folder the CLI of the provider uses (<see cref="ReadEnvironment"/>): where it keeps the
    /// registry of live sessions and the transcripts CodeAlta reads.
    /// </summary>
    internal string ResolveConfigRoot() => ClaudeCodeProjectFolders.ResolveConfigRoot(ReadEnvironment());
}
