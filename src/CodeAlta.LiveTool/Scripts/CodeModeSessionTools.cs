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
/// <param name="enabled">Says whether Code Mode is on now.</param>
public sealed class CodeModeSessionTools(CodeModeToolOptions options, Func<bool> enabled)
{
    private readonly CodeModeToolOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly Func<bool> _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));

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
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(catalog);
        var store = new CodeAltaConfigStore(catalog);
        var options = new CodeModeToolOptions
        {
            Runs = runs,
            Cache = new CodeModeScriptCache(),
            SourceFolder = scratchpadRoot is { Length: > 0 } scratchpad ? sessionId => Path.Combine(scratchpad, "codemode", sessionId) : null,
        };
        return new CodeModeSessionTools(options, () =>
        {
            try
            {
                return store.LoadCodeModeMode() == "on";
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return false;
            }
        });
    }

    /// <summary>Returns the tools of a session, with <c>exec</c> and <c>wait</c> after them while Code Mode is on.</summary>
    /// <param name="dispatcher">The dispatcher of <c>alta</c>.</param>
    /// <param name="caller">Who the session is, as for its <c>alta</c> tool.</param>
    /// <param name="tools">The other tools the host gives the session.</param>
    /// <returns>The tools of the session.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public IReadOnlyList<AgentToolDefinition> Create(AltaCommandDispatcher dispatcher, AltaSessionToolOptions caller, IReadOnlyList<AgentToolDefinition> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return _enabled() ? [.. tools, .. CodeModeToolFactory.Create(dispatcher, caller, _options)] : tools;
    }
}
