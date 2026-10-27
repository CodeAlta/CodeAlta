using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.CodeMode;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.LiveTool.Scripts;

/// <summary>
/// Adds the tools of Code Mode to the tools a host gives a session, while Code Mode is on: a host asks again at every
/// send, so a change of the setting reaches a session at its next send.
/// </summary>
/// <param name="options">The options of the host's Code Mode tools.</param>
/// <param name="mode">Says the mode of Code Mode now: <c>off</c>, <c>on</c>, or <c>only</c>.</param>
public sealed class CodeModeSessionTools(CodeModeToolOptions options, Func<string> mode)
{
    private readonly CodeModeToolOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly Func<string> _mode = mode ?? throw new ArgumentNullException(nameof(mode));

    /// <summary>Gets the options of the host's Code Mode tools.</summary>
    public CodeModeToolOptions Options => _options;

    /// <summary>
    /// Creates the Code Mode tools of a host that follows the user's file (<c>[code_mode] mode</c>): read again at every
    /// send, and off when the file cannot be read.
    /// </summary>
    /// <param name="runs">The runs of the host.</param>
    /// <param name="catalog">The roots of the host, whose configuration file says the mode.</param>
    /// <param name="scratchpadRoot">The scratchpad of the host, where the source of each run is kept; null keeps none.</param>
    /// <returns>The tools.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runs"/> or <paramref name="catalog"/> is null.</exception>
    public static CodeModeSessionTools FromSettings(CodeModeRunRegistry runs, CatalogOptions catalog, string? scratchpadRoot)
        => FromSettings(runs, catalog, scratchpadRoot, agents: null);

    /// <summary>
    /// Creates the Code Mode tools of a host that follows the user's file, as
    /// <see cref="FromSettings(CodeModeRunRegistry, CatalogOptions, string?)"/> does, whose scripts start sessions.
    /// </summary>
    /// <param name="runs">The runs of the host.</param>
    /// <param name="catalog">The roots of the host, whose configuration file says the mode.</param>
    /// <param name="scratchpadRoot">The scratchpad of the host, where the source of each run is kept; null keeps none.</param>
    /// <param name="agents">The sessions scripts start with <c>agent()</c>, or null.</param>
    /// <returns>The tools.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runs"/> or <paramref name="catalog"/> is null.</exception>
    public static CodeModeSessionTools FromSettings(CodeModeRunRegistry runs, CatalogOptions catalog, string? scratchpadRoot, ICodeModeAgents? agents)
        => FromSettings(runs, catalog, scratchpadRoot, agents, library: null);

    /// <summary>
    /// Creates the Code Mode tools of a host that follows the user's file, whose scripts start sessions and run the scripts
    /// of a library.
    /// </summary>
    /// <param name="runs">The runs of the host.</param>
    /// <param name="catalog">The roots of the host, whose configuration file says the mode.</param>
    /// <param name="scratchpadRoot">The scratchpad of the host, where the source of each run is kept; null keeps none.</param>
    /// <param name="agents">The sessions scripts start with <c>agent()</c>, or null.</param>
    /// <param name="library">The library <c>workflow("name")</c> runs from, or null.</param>
    /// <returns>The tools.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runs"/> or <paramref name="catalog"/> is null.</exception>
    public static CodeModeSessionTools FromSettings(CodeModeRunRegistry runs, CatalogOptions catalog, string? scratchpadRoot, ICodeModeAgents? agents,
        CodeModeScriptLibrary? library)
        => FromSettings(runs, catalog, scratchpadRoot, agents, library, questions: null);

    /// <summary>
    /// Creates the Code Mode tools of a host that follows the user's file, whose scripts start sessions, run the scripts of a
    /// library and ask the user questions.
    /// </summary>
    /// <param name="runs">The runs of the host.</param>
    /// <param name="catalog">The roots of the host, whose configuration file says the mode.</param>
    /// <param name="scratchpadRoot">The scratchpad of the host, where the source of each run is kept; null keeps none.</param>
    /// <param name="agents">The sessions scripts start with <c>agent()</c>, or null.</param>
    /// <param name="library">The library <c>workflow("name")</c> runs from, or null.</param>
    /// <param name="questions">Where <c>ask()</c> reaches the user, or null for a host where nobody can be asked.</param>
    /// <returns>The tools.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runs"/> or <paramref name="catalog"/> is null.</exception>
    public static CodeModeSessionTools FromSettings(CodeModeRunRegistry runs, CatalogOptions catalog, string? scratchpadRoot, ICodeModeAgents? agents,
        CodeModeScriptLibrary? library, ICodeModeQuestions? questions)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(catalog);
        var store = new CodeAltaConfigStore(catalog);
        var options = new CodeModeToolOptions
        {
            Agents = agents,
            Questions = questions,
            Library = library,
            Runs = runs,
            Cache = new CodeModeScriptCache(),
            SourceFolder = scratchpadRoot is { Length: > 0 } scratchpad ? sessionId => Path.Combine(scratchpad, "codemode", sessionId) : null,
        };
        return new CodeModeSessionTools(options, () =>
        {
            try
            {
                return store.LoadCodeModeMode();
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return "off";
            }
        });
    }

    /// <summary>
    /// Returns the tools of a session, with <c>exec</c> and <c>wait</c> after them while Code Mode is on; in <c>only</c>,
    /// <c>exec</c> takes over the others, which the model then calls through scripts.
    /// </summary>
    /// <param name="dispatcher">The dispatcher of <c>alta</c>.</param>
    /// <param name="caller">Who the session is, as for its <c>alta</c> tool.</param>
    /// <param name="tools">The other tools the host gives the session.</param>
    /// <returns>The tools of the session.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public IReadOnlyList<AgentToolDefinition> Create(AltaCommandDispatcher dispatcher, AltaSessionToolOptions caller, IReadOnlyList<AgentToolDefinition> tools)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(tools);
        var mode = _mode();
        // The session of an automation that runs a script has exec, whatever the setting says.
        if (mode is not ("on" or "only") && _options.Runs.RequiresCodeMode(caller.SourceSessionIdProvider?.Invoke() ?? caller.SourceSessionId))
        {
            mode = "on";
        }

        return mode switch
        {
            "on" => [.. tools, .. CodeModeToolFactory.Create(dispatcher, caller, _options), .. ScriptTools(dispatcher, caller, tools)],
            "only" => [.. tools, .. CodeModeToolFactory.Create(dispatcher, caller, _options with { TakesOver = true }), .. ScriptTools(dispatcher, caller, tools)],
            _ => tools,
        };
    }

    /// <summary>Returns a copy whose runs read the space of a session by its id when its tools do not give it.</summary>
    /// <param name="spaceOf">Gives what the space of a session has.</param>
    /// <returns>The tools of Code Mode with that reader.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="spaceOf"/> is null.</exception>
    public CodeModeSessionTools WithSpaces(Func<string, CancellationToken, Task<SpaceFilter?>> spaceOf)
    {
        ArgumentNullException.ThrowIfNull(spaceOf);
        return new CodeModeSessionTools(_options with { SpaceOf = spaceOf }, _mode);
    }

    // The scripts of the library offered as tools, under a name no other tool of the session has.
    private IEnumerable<AgentToolDefinition> ScriptTools(AltaCommandDispatcher dispatcher, AltaSessionToolOptions caller, IReadOnlyList<AgentToolDefinition> tools)
        => CodeModeToolFactory.CreateScriptTools(dispatcher, caller, _options).Where(tool => tool.Spec.Name is not (CodeModeToolFactory.ExecToolName or CodeModeToolFactory.WaitToolName)
            && !tools.Any(other => string.Equals(other.Spec.Name, tool.Spec.Name, StringComparison.Ordinal)));
}
