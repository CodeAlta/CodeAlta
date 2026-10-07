using CodeAlta.Agent;
using CodeAlta.Desktop.Ui;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop.Mcp;

/// <summary>
/// The tools the MCP server offers: the ones that see and drive the window, and <c>alta</c>, which runs the
/// commands of the application.
/// </summary>
/// <remarks>
/// The tools an agent works on a project with (reading and writing files, running a shell) are not here: a
/// client of the server is an application that has its own.
/// </remarks>
internal static class DesktopMcpTools
{
    /// <summary>The kind of caller <c>alta</c> commands see for a client of the MCP server.</summary>
    internal const string CallerKind = "mcp";

    /// <summary>The tools of the window, for a caller that is no session of the application.</summary>
    /// <param name="ui">The tools of the window.</param>
    /// <param name="files">Where such a caller may have a tool save a file.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal static IReadOnlyList<DesktopMcpTool> Window(IDesktopUi ui, Func<CancellationToken, ValueTask<DesktopUiFiles>> files)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(files);
        return [.. ui.Tools.Select(tool => new DesktopMcpTool(tool.Name, tool.Description, tool.InputSchema, tool.ReadOnly,
            async (arguments, cancellationToken) => await ui.CallAsync(tool.Name, arguments, await files(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false)))];
    }

    /// <summary>
    /// The <c>alta</c> tool, with the name, the description and the arguments the sessions of the application
    /// have. Its caller belongs to no session: a command that acts on "the current session" says so.
    /// </summary>
    /// <param name="dispatcher">The commands of the host.</param>
    /// <param name="workingDirectory">The folder a command resolves a relative path from when the caller names none.</param>
    /// <param name="ran">
    /// Called after each command: a client creates sessions and sends to them without the window asking, and the
    /// window is told that its list may have changed.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dispatcher"/> is null.</exception>
    internal static DesktopMcpTool Alta(AltaCommandDispatcher dispatcher, string? workingDirectory, Action? ran = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        var tool = AltaSessionToolFactory.Create(dispatcher, new AltaSessionToolOptions
        {
            CallerKind = CallerKind,
            WorkingDirectory = workingDirectory,
            DefaultMaxOutputRecords = 200,
            DefaultMaxOutputBytes = 64 * 1024,
            DefaultTimeout = TimeSpan.FromSeconds(120),
        });
        return new DesktopMcpTool(tool.Spec.Name, tool.Spec.Description, tool.Spec.InputSchema, ReadOnly: false, async (arguments, cancellationToken) =>
        {
            try
            {
                var result = await tool.Handler(new AgentToolInvocation(new ModelProviderId(CallerKind), string.Empty, Guid.NewGuid().ToString("N"), tool.Spec.Name, arguments), cancellationToken)
                    .ConfigureAwait(false);
                return new DesktopUiToolResult(string.Join('\n', result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value)), [], !result.Success);
            }
            finally
            {
                ran?.Invoke();
            }
        });
    }
}
