using System.Collections.Frozen;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Cli;

/// <summary>
/// What a run of <c>altacli</c> takes from its process: the home folder, the current folder, the environment, the temporary
/// folder. The process has <see cref="FromProcess"/>; a test gives folders of its own and a provider of its own, so that it
/// never reads or writes the user's profile.
/// </summary>
internal sealed record CliEnvironment
{
    /// <summary>The variable that names a whole separate profile, as <c>--profile</c> does.</summary>
    public const string HomeVariable = "CODEALTA_HOME";

    /// <summary>Gets the home folder of the user; the profile is its <c>.alta</c> folder.</summary>
    public required string UserProfile { get; init; }

    /// <summary>Gets the folder the command runs in: the default project folder.</summary>
    public required string CurrentDirectory { get; init; }

    /// <summary>Gets the folder of the temporary files of the run: its private database, its scratchpads, an ephemeral state root.</summary>
    public required string TempRoot { get; init; }

    /// <summary>Gets the environment variables.</summary>
    public required Func<string, string?> GetVariable { get; init; }

    /// <summary>Gets whether the run sets up the logging of the process (a test leaves the process-wide logging alone).</summary>
    public bool InitializeLogging { get; init; }

    /// <summary>Gets whether SIGINT, SIGTERM and Ctrl+C abort the run (a test cancels its token instead).</summary>
    public bool HandleSignals { get; init; }

    /// <summary>Gets what registers the providers in place of the configured ones, for a test; null for the configured providers.</summary>
    public Action<ModelProviderRegistry>? ConfigureModelProviders { get; init; }

    /// <summary>Gets the home and instruction boundary of the sessions, for a test; null for the user's.</summary>
    public SessionDiscoveryScope? DiscoveryScope { get; init; }

    /// <summary>Gets the folder of the built-in skills, for a test; null for the one shipped with the application.</summary>
    public string? BuiltInSkillRoot { get; init; }

    /// <summary>Gets the environment given to plugins, for a test; null for the one of the process.</summary>
    public IReadOnlyDictionary<string, string?>? PluginEnvironment { get; init; }

    /// <summary>Gets how long an aborted run is waited for before its result is reported; null for the default of the driver.</summary>
    public TimeSpan? AbortGrace { get; init; }

    /// <summary>Gets what is told the host of a run once it is created, for a test that drives it; null for nothing.</summary>
    public Action<CodeAlta.Orchestration.Hosting.CodeAltaHost>? HostCreated { get; init; }

    /// <summary>Gets the profile of the user: <c>~/.alta</c>.</summary>
    public string DefaultProfile => Path.Combine(UserProfile, ".alta");

    /// <summary>The environment of the process.</summary>
    /// <exception cref="InvalidOperationException">The home folder of the user is unknown.</exception>
    public static CliEnvironment FromProcess()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) throw new InvalidOperationException("The home folder of the user is unknown: CodeAlta cannot find its profile.");
        return new CliEnvironment
        {
            UserProfile = home,
            CurrentDirectory = Environment.CurrentDirectory,
            TempRoot = Path.GetTempPath(),
            GetVariable = Environment.GetEnvironmentVariable,
            InitializeLogging = true,
            HandleSignals = true,
        };
    }

    /// <summary>An empty environment, for <see cref="PluginEnvironment"/>.</summary>
    public static IReadOnlyDictionary<string, string?> NoVariables { get; } = FrozenDictionary<string, string?>.Empty;
}
