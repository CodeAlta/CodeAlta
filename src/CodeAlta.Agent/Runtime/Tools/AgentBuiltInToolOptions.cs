using System.Net.Http;

namespace CodeAlta.Agent.Runtime.Tools;

/// <summary>
/// Configures the default built-in tools for a local raw-API session.
/// </summary>
public sealed class AgentBuiltInToolOptions
{
    /// <summary>
    /// Gets or initializes the model provider identifier.
    /// </summary>
    public required ModelProviderId ProviderId { get; init; }

    /// <summary>
    /// Gets or initializes the session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets or initializes the default working directory used to resolve relative paths.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or initializes the environment variables <c>shell_command</c> sets for the commands it runs, over the
    /// environment of the application. Null sets none.
    /// </summary>
    public AgentProcessEnvironment? Environment { get; init; }

    /// <summary>
    /// Gets or initializes the permission request handler.
    /// </summary>
    public required AgentPermissionRequestHandler OnPermissionRequest { get; init; }

    /// <summary>
    /// Gets or initializes the optional user-input request handler.
    /// </summary>
    public AgentUserInputRequestHandler? OnUserInputRequest { get; init; }

    /// <summary>Gets explicit activation of the user-input built-in; defaults to false.</summary>
    /// <remarks>A selected callback is also required. Provider profiles may opt out but cannot activate
    /// this tool without this flag. This grants no command/file permission or lifecycle authority.</remarks>
    public bool EnableUserInputTool { get; init; }

    /// <summary>
    /// Gets or initializes the built-in tools the session does without, by name (such as <c>shell_command</c>);
    /// <see langword="null"/> or empty for none. A tool named here is left out whatever the provider profile says.
    /// </summary>
    public IReadOnlySet<string>? DisabledTools { get; init; }

    /// <summary>
    /// Gets where the file tools may write, for a session that does without <c>shell_command</c>; null where they
    /// write wherever the permission handler lets them.
    /// </summary>
    public AgentWriteGuard? WriteGuard { get; init; }

    /// <summary>Gets what checks each shell command before it runs, whatever the permission mode; null for no check.</summary>
    public AgentCommandGuard? CommandGuard { get; init; }

    /// <summary>
    /// Gets or initializes the shared HTTP client for web retrieval.
    /// </summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>
    /// Gets or initializes the provider descriptor used to tailor the built-in tool set.
    /// </summary>
    public ModelProviderRuntimeDescriptor? Provider { get; init; }

    /// <summary>
    /// Gets or initializes the default maximum number of lines returned by <c>read_file</c>.
    /// </summary>
    public int DefaultReadFileLineLimit { get; init; } = DefaultReadFileLines;

    /// <summary>
    /// Gets or initializes the maximum number of lines returned by <c>read_file</c>.
    /// </summary>
    public int MaxReadFileLineLimit { get; init; } = MaxReadFileLines;

    // The limits of lines of a session's read_file, which its guard against reads again knows too.
    internal const int DefaultReadFileLines = 2000;
    internal const int MaxReadFileLines = 5000;

    /// <summary>
    /// Gets or initializes the maximum number of characters of one line <c>read_file</c> gives the model: a longer line
    /// keeps its start and says how long it is. Zero or less for no limit. A script of Code Mode receives whole lines.
    /// </summary>
    public int MaxReadFileLineCharacters { get; init; } = 2_000;

    /// <summary>
    /// Gets or initializes the maximum number of characters one <c>read_file</c> gives the model: the read stops before
    /// the line that would pass it and names the offset to read on from. Zero or less for no limit. A script of Code
    /// Mode receives the lines read whole.
    /// </summary>
    public int MaxReadFileCharacters { get; init; } = 40_000;

    /// <summary>
    /// Gets or initializes the maximum number of grep matches returned.
    /// </summary>
    public int MaxGrepMatches { get; init; } = 200;

    /// <summary>
    /// Gets or initializes the maximum number of characters of one matching line <c>grep</c> gives: a longer line keeps
    /// the part around its first match and says how long it is. Zero or less for no limit.
    /// </summary>
    public int MaxGrepLineCharacters { get; init; } = 500;

    /// <summary>
    /// Gets or initializes the maximum number of characters one <c>grep</c> gives: the matches after it are left out,
    /// and the result says so. Zero or less for no limit.
    /// </summary>
    public int MaxGrepCharacters { get; init; } = 30_000;

    /// <summary>
    /// Gets or initializes the maximum number of characters of the output <c>shell_command</c> gives the model: a longer
    /// output keeps the head and the tail of each stream, and the whole of it is saved to a file the result names. Zero
    /// or less for no limit. The user is shown the whole output, and a script of Code Mode receives it.
    /// </summary>
    public int MaxShellCommandOutputCharacters { get; init; } = 30_000;

    /// <summary>
    /// Gets or initializes what gives the folder where <c>shell_command</c> saves the whole of an output it cuts, such
    /// as a folder of the session; null, or a null folder, for a folder of the session in the temporary folder.
    /// </summary>
    public Func<CancellationToken, ValueTask<string?>>? ResolveToolOutputDirectoryAsync { get; init; }

    /// <summary>
    /// Gets or initializes the maximum payload size fetched by <c>webget</c>.
    /// </summary>
    public int MaxWebGetBytes { get; init; } = 1_000_000;

    /// <summary>
    /// Gets or initializes the default timeout used by <c>webget</c>.
    /// </summary>
    public TimeSpan WebGetTimeout { get; init; } = TimeSpan.FromSeconds(20);
}
