using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Agent;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.Desktop.Ui;

/// <summary>
/// The sessions that turned the UI tools on, and where each of them works.
/// </summary>
/// <remarks>
/// A session has the tools from the moment it asks for them until it gives them back or the application exits:
/// like the MCP servers a session activates, this is not kept from one run of the application to the next.
/// </remarks>
internal sealed class DesktopUiSessions
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private Func<string, CancellationToken, ValueTask<string?>>? _workFolder;

    /// <summary>
    /// Gets or sets how the folder a session works in is found: its git worktree, or the folder of its project.
    /// Null until the host that knows the sessions exists.
    /// </summary>
    internal Func<string, CancellationToken, ValueTask<string?>>? WorkFolder
    {
        get => Volatile.Read(ref _workFolder);
        set => Volatile.Write(ref _workFolder, value);
    }

    /// <summary>Whether a session has the tools.</summary>
    internal bool IsActive(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (_gate) return _active.Contains(sessionId);
    }

    /// <summary>Gives a session the tools, or takes them back.</summary>
    /// <returns>Whether this changed anything.</returns>
    internal bool Set(string sessionId, bool active)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate) return active ? _active.Add(sessionId) : _active.Remove(sessionId);
    }

    /// <summary>
    /// Where a session may have a tool save a file: the folder it works in, which a relative path starts from,
    /// and the folder of the tools.
    /// </summary>
    /// <param name="ui">The tools.</param>
    /// <param name="sessionId">The session; null for a caller that is none.</param>
    /// <param name="fallback">The folder to use when the one of the session is not known.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    internal async ValueTask<DesktopUiFiles> FilesAsync(IDesktopUi ui, string? sessionId, string? fallback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ui);
        string? folder = null;
        if (!string.IsNullOrWhiteSpace(sessionId) && WorkFolder is { } resolve)
        {
            try
            {
                folder = await resolve(sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The session is not known any more: it writes where every caller may.
            }
        }

        folder = string.IsNullOrWhiteSpace(folder) ? fallback : folder;
        return string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)
            ? new DesktopUiFiles(ui.FilesDirectory, [ui.FilesDirectory])
            : new DesktopUiFiles(folder, [folder, ui.FilesDirectory]);
    }
}

/// <summary>
/// The built-in plugin of the desktop application that gives a session the tools that see and drive the window
/// it runs in, when the session asks for them.
/// </summary>
/// <remarks>
/// <para>
/// The tools are real tools, not <c>alta</c> commands: their results hold images, which a command cannot
/// return. They are a few dozen, so a session does not carry them until it calls <c>alta ui activate</c>; they
/// then join the running turn and come with each later run of that session. A line of the instructions says
/// whether the session has them and how to get them.
/// </para>
/// <para>
/// A plugin reaches every session of the host the same way: a session the window sends to, and a session that
/// another one creates and drives with <c>alta session</c>.
/// </para>
/// </remarks>
[Plugin(Id, DisplayName = "UI tools", Description = "Gives a session that asks for them the tools that see and drive the CodeAlta Desktop window.")]
internal sealed class DesktopUiPlugin : PluginBase
{
    /// <summary>The id of the plugin, as in <c>[plugins.ui]</c> of the configuration.</summary>
    internal const string Id = "ui";

    private readonly IDesktopUi _ui;
    private readonly DesktopUiSessions _sessions;

    /// <summary>Creates the plugin over the tools of a window.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal DesktopUiPlugin(IDesktopUi ui, DesktopUiSessions sessions)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(sessions);
        _ui = ui;
        _sessions = sessions;
    }

    /// <summary>The definition a host lists among its built-in plugins.</summary>
    internal static BuiltInPluginDefinition Definition(IDesktopUi ui, DesktopUiSessions sessions)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(sessions);
        return new BuiltInPluginDefinition
        {
            Id = Id, DisplayName = "UI tools",
            Description = "Gives a session that asks for them the tools that see and drive the CodeAlta Desktop window.",
            PluginType = typeof(DesktopUiPlugin), Factory = () => new DesktopUiPlugin(ui, sessions),
        };
    }

    /// <inheritdoc />
    public override IEnumerable<PluginAltaCommandContribution> GetAltaCommands()
    {
        yield return new PluginAltaCommandContribution
        {
            Path = "ui",
            Description = "Turn on the tools that see and drive the CodeAlta Desktop window.",
            Policy = new PluginAltaCommandPolicy { RequiresInProcessRuntime = true, IsMutating = true, SupportsCatalogOnlyContext = false },
            CreateCommandNode = CreateCommand,
        };
    }

    /// <inheritdoc />
    public override IEnumerable<PluginSystemPromptContribution> GetSystemPromptContributions()
    {
        yield return Prompt.Dynamic(
            PluginPromptChannel.Developer,
            (context, _) => new ValueTask<string?>(Guidance(_sessions.IsActive(context.SessionId))),
            title: "UI tools",
            kind: PluginPromptPartKind.ToolGuidance,
            order: 60);
    }

    /// <inheritdoc />
    public override ValueTask<PluginBeforeAgentRunResult?> OnBeforeAgentRunAsync(PluginBeforeAgentRunContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ValueTask<PluginBeforeAgentRunResult?>(_sessions.IsActive(context.SessionId)
            ? new PluginBeforeAgentRunResult { AdditionalTools = CreateTools(_ui, _sessions, context.ProjectPath) }
            : null);
    }

    /// <summary>What the instructions of a session say about the tools.</summary>
    internal static string Guidance(bool active) => active
        ? "UI tools: active. `take_snapshot`, `take_screenshot`, `click`, `fill`, `press_key`, `evaluate_script` and the other tools named as in Chrome DevTools MCP " +
          "see and drive the window of CodeAlta Desktop, the application this session runs in. A snapshot gives the uid that the tools acting on an element take."
        : "UI tools: inactive. `alta ui activate` gives this session tools that see and drive the window of CodeAlta Desktop, the application it runs in " +
          "(`take_snapshot`, `take_screenshot`, `click`, `fill`, `press_key`, `evaluate_script` and more, named as in Chrome DevTools MCP). They join the running turn.";

    /// <summary>
    /// The tools as agent tools. Their names, descriptions and schemas are the same for every session, which is
    /// what lets a session keep its attachment from one send to the next.
    /// </summary>
    /// <param name="ui">The tools of the window.</param>
    /// <param name="sessions">Where each session works.</param>
    /// <param name="projectPath">The folder of the project of the session, used when the session is not known.</param>
    internal static IReadOnlyList<AgentToolDefinition> CreateTools(IDesktopUi ui, DesktopUiSessions sessions, string? projectPath)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(sessions);
        return [.. ui.Tools.Select(tool => new AgentToolDefinition(
            new AgentToolSpec(tool.Name, tool.Description, tool.InputSchema),
            async (invocation, cancellationToken) =>
            {
                var files = await sessions.FilesAsync(ui, invocation.SessionId, projectPath, cancellationToken).ConfigureAwait(false);
                var result = await ui.CallAsync(tool.Name, invocation.Arguments, files, cancellationToken).ConfigureAwait(false);
                return new AgentToolResult(
                    !result.IsError,
                    [
                        new AgentToolResultItem.Text(result.Text),
                        .. result.Images.Select(static (image, index) => (AgentToolResultItem)new AgentToolResultItem.Image(
                            Convert.ToBase64String(image.Data.Span), image.MediaType, index == 0 ? "screenshot" : $"screenshot-{index + 1}")),
                    ],
                    result.IsError ? result.Text : null);
            }))];
    }

    private Command CreateCommand(PluginAltaCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var command = Node("ui", "Turn on the tools that see and drive the CodeAlta Desktop window.");
        command.Add(CreateActivateCommand(context));
        command.Add(CreateDeactivateCommand(context));
        command.Add(CreateStatusCommand(context));
        AddHelpText(command,
            "The UI tools see and drive the window of CodeAlta Desktop this session runs in: take_snapshot, take_screenshot, click, fill, press_key, evaluate_script and more, with the names and the arguments of Chrome DevTools MCP.",
            "They are tools, not alta commands: `alta ui activate` registers them in the running turn, and you call them in the next step. They stay with the session until `alta ui deactivate`.",
            "take_snapshot lists the elements of the page with the uid the other tools take; take_screenshot gives a picture, or saves it with filePath (a path relative to the folder the session works in).",
            "Examples: `alta ui activate`; `alta ui status`; `alta ui deactivate`.");
        return command;
    }

    private Command CreateActivateCommand(PluginAltaCommandContext context)
    {
        var command = Node("activate", "Give this session the UI tools: they are registered in the running turn and on its later runs.");
        command.Add((_, _) =>
        {
            if (string.IsNullOrWhiteSpace(context.SourceSessionId)) return NoSession(context);
            _sessions.Set(context.SourceSessionId, active: true);
            // An agent run that called the command takes the tools at once: its next model request offers them.
            var tools = CreateTools(_ui, _sessions, context.WorkingDirectory);
            var registered = context.RunTools?.Add(tools) ?? [];
            var now = context.RunTools is { } run && tools.All(tool => run.Contains(tool.Spec.Name));
            WriteRecord(context.Stdout, new
            {
                type = "alta.ui.activate",
                version = 1,
                correlationId = context.CorrelationId,
                active = true,
                toolsAvailable = now ? "now" : "next_run",
                registeredToolCount = registered.Count,
                tools = tools.Select(static tool => tool.Spec.Name).ToArray(),
                note = now
                    ? "The tools are registered for this turn: call them in your next step, do not end the turn. They see and drive the window of CodeAlta Desktop this session runs in: " +
                      "take_screenshot gives a picture, and take_snapshot lists the elements with the uid that click, fill and hover take."
                    : "Activated: the tools come with the next agent run of this session.",
            });
            return ValueTask.FromResult(0);
        });
        AddHelpText(command, "Example: `alta ui activate`, then `take_snapshot` in the next step.");
        return command;
    }

    private Command CreateDeactivateCommand(PluginAltaCommandContext context)
    {
        var command = Node("deactivate", "Give the UI tools back: the next runs of this session do not carry them.");
        command.Add((_, _) =>
        {
            if (string.IsNullOrWhiteSpace(context.SourceSessionId)) return NoSession(context);
            var changed = _sessions.Set(context.SourceSessionId, active: false);
            WriteRecord(context.Stdout, new
            {
                type = "alta.ui.deactivate",
                version = 1,
                correlationId = context.CorrelationId,
                active = false,
                changed,
                note = "The tools of the running turn stay until it ends; the next runs of this session do not have them.",
            });
            return ValueTask.FromResult(0);
        });
        return command;
    }

    private Command CreateStatusCommand(PluginAltaCommandContext context)
    {
        var command = Node("status", "Say whether this session has the UI tools, and list them.");
        command.Add((_, _) =>
        {
            WriteRecord(context.Stdout, new
            {
                type = "alta.ui.status",
                version = 1,
                correlationId = context.CorrelationId,
                active = _sessions.IsActive(context.SourceSessionId),
                tools = _ui.Tools.Select(static tool => tool.Name).ToArray(),
            });
            return ValueTask.FromResult(0);
        });
        return command;
    }

    private static ValueTask<int> NoSession(PluginAltaCommandContext context)
    {
        WriteRecord(context.Stdout, new
        {
            type = "alta.ui.error",
            version = 1,
            correlationId = context.CorrelationId,
            code = "ui.noSession",
            message = "A session turns the UI tools on for itself, and no session calls this command.",
        });
        return ValueTask.FromResult(1);
    }

    private static Command Node(string name, string description) => new(name, description) { new CommandUsage(), new HelpOption() };

    private static void AddHelpText(Command command, params string[] lines)
    {
        command.Add("");
        foreach (var line in lines) command.Add(line);
    }

    private static void WriteRecord(TextWriter writer, object record)
    {
        writer.Write(JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        writer.WriteLine();
    }
}
