namespace CodeAlta.Agent.Claude;

/// <summary>
/// Options of the model provider that drives the Claude Code CLI installed by the user.
/// </summary>
/// <remarks>
/// CodeAlta starts the unmodified <c>claude</c> executable and talks to it over its stream-json input and
/// output. It never reads, stores or forwards credentials: the CLI signs in with whatever the user set up for it
/// (<c>claude</c> then <c>/login</c>, an API key, or a cloud provider), and its usage is billed to that account.
/// </remarks>
public sealed class ClaudeCodeModelProviderRuntimeOptions
{
    /// <summary>
    /// The permission modes of the CLI a provider can be configured with.
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
    /// Gets or initializes the path or the name of the <c>claude</c> executable. When it is empty the executable is
    /// looked for on <c>PATH</c> and in the folders its installers use.
    /// </summary>
    public string? Command { get; init; }

    /// <summary>
    /// Gets or initializes arguments added to the command line of the CLI, after those of CodeAlta.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>
    /// Gets or initializes the permission mode the CLI starts with (one of <see cref="PermissionModes"/>).
    /// When it is empty the CLI uses the mode of the user's settings.
    /// </summary>
    public string? PermissionMode { get; init; }

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

    /// <summary>
    /// Gets or initializes a value indicating whether the models are sorted by name in selectors.
    /// </summary>
    public bool SortModels { get; init; }

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
    /// Gets or initializes the catalog of models.dev the models of the CLI are looked up in; null when there is none.
    /// </summary>
    public CodeAlta.Agent.ModelCatalog.ModelsDevCatalogService? ModelCatalog { get; init; }

    /// <summary>
    /// Gets or initializes the provider of models.dev whose models the CLI runs: the models of Claude Code are
    /// those of Anthropic.
    /// </summary>
    public string ModelsDevProviderId { get; init; } = "anthropic";

    /// <summary>
    /// Gets or initializes the factory of the processes. Tests replace it with a scripted CLI.
    /// </summary>
    internal IClaudeCodeTransportFactory? TransportFactory { get; init; }

    /// <summary>
    /// Gets or initializes the resolver of the executable. Tests replace it.
    /// </summary>
    internal Func<ClaudeCodeCliResolution>? ResolveCli { get; init; }
}
