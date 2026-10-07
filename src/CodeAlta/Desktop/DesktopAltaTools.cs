using System.Collections;
using CodeAlta.Agent;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Prompts;

namespace CodeAlta.Desktop;

/// <summary>
/// Gives the sessions of the desktop host the same <c>alta</c> tool as the terminal UI: notes, sessions and
/// sub-sessions, reminders, skills, projects, models and prompts, all served in-process by the host.
/// </summary>
/// <remarks>
/// <para>
/// <c>alta diff show</c> and <c>alta editor open</c> exist only here: they open the changes tab and the
/// code editor of a project in the window. So do the <c>alta terminal</c> commands, which create, read, type in
/// and close the terminals of the window.
/// </para>
/// <para>
/// Three things are specific to the desktop. <c>alta ask</c> goes to the window's ask panel through the run
/// that asks, so only a run started from the window can ask, once. Sessions that alta commands create or
/// drive use the host's own permission defaults, which lets the window keep sending to them. Reminders are
/// the ones the Reminders view shows and delivers.
/// </para>
/// <para>
/// The commands of the host's active plugins (<c>alta mcp</c>, <c>alta statistics</c>) are part of the tool,
/// and sessions that alta commands send to get the same plugin tools and instructions as sends from the window.
/// </para>
/// <para>
/// A host that started its plugins lets <c>alta plugin</c> create a source plugin, build it and replace it while
/// the host runs, and open its folder in the code editor of the window.
/// </para>
/// </remarks>
internal static class DesktopAltaTools
{
    /// <summary>Composes the alta services over a host and makes them the session tool of its owned sessions.</summary>
    /// <param name="host">The running host.</param>
    /// <param name="reminders">The reminder service the window lists and delivers from.</param>
    /// <param name="pluginAlta">The bridge through which the host's plugins invoke alta commands, if they can.</param>
    /// <param name="changes">Where <c>alta diff show</c> asks the window to show changed files; without it the command does not exist.</param>
    /// <param name="editor">Where <c>alta editor open</c> asks the window to open the code editor; without it the command does not exist.</param>
    /// <param name="terminals">The terminals the <c>alta terminal</c> commands use; without them the commands do not exist.</param>
    /// <param name="automations">The automations the <c>alta automation</c> commands use; without them the commands do not exist.</param>
    /// <param name="worktrees">The worktrees <c>alta session create --worktree</c> creates; the commands make their own without it.</param>
    /// <param name="plugins">What <c>alta plugin</c> does with the source plugins of the host; without it the commands only list the active plugins.</param>
    /// <param name="workItems">The tasks and the plans the <c>alta task</c> and <c>alta plan</c> commands use; without them the commands do not exist.</param>
    /// <returns>The dispatcher of the commands, for the other callers of the host (its MCP server).</returns>
    /// <exception cref="ArgumentNullException">The host or the reminders are null.</exception>
    internal static AltaCommandDispatcher Attach(CodeAltaHost host, AltaReminderService reminders, PluginAltaServiceBridge? pluginAlta = null, IAltaChangesView? changes = null,
        IAltaEditorView? editor = null, IAltaTerminals? terminals = null, IAltaAutomations? automations = null,
        CodeAlta.Catalog.Worktrees.GitWorktreeService? worktrees = null, AltaPluginWorkshop? plugins = null,
        CodeAlta.Catalog.WorkItems.WorkItemService? workItems = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(reminders);
        var dispatcher = Compose(host, reminders, changes, editor, terminals, automations, worktrees, plugins, workItems);
        pluginAlta?.SetDispatcher(dispatcher);
        host.Commands.SessionTools = CreateSessionTools(dispatcher);
        return dispatcher;
    }

    /// <summary>Builds the dispatcher of the alta commands over a host's services.</summary>
    internal static AltaCommandDispatcher Compose(CodeAltaHost host, AltaReminderService reminders, IAltaChangesView? changes = null, IAltaEditorView? editor = null,
        IAltaTerminals? terminals = null, IAltaAutomations? automations = null, CodeAlta.Catalog.Worktrees.GitWorktreeService? worktrees = null,
        AltaPluginWorkshop? plugins = null, CodeAlta.Catalog.WorkItems.WorkItemService? workItems = null)
    {
        var permissions = host.RuntimeService.Permissions;
        var services = new AltaServiceCollection()
            .Add(host.CatalogOptions)
            .Add(host.SessionViewCatalog.TextFiles)
            .Add(host.ProjectCatalog)
            .Add(host.SessionViewCatalog)
            .Add(host.RuntimeService)
            .Add(host.RuntimeService.SkillCatalog)
            .Add(host.AgentHub)
            .Add(host.ModelProviderInitializationService)
            .Add(host.ModelProviderRegistry)
            .Add(host.ProjectFileSearchService)
            .Add<IAltaAskService>(new OwnedSessionAltaAskService(host.Commands.Asks))
            .Add<IAltaNotesService>(new RuntimeAltaNotesService(host.RuntimeService))
            .Add<IReadOnlyList<ModelProviderDescriptor>>(new RegisteredProviders(host.ModelProviderRegistry))
            .Add<IAltaSessionToolProviderPolicy>(new AltaSessionToolProviderPolicy())
            .Add<IAltaSessionInteractionDefaults>(new AltaSessionInteractionDefaults(
                permissions.OwnedDefaultPermissionHandler, permissions.OwnedDefaultUserInputHandler))
            .Add(reminders)
            // The plugins as they are when a command runs; without an active plugin both add nothing.
            .Add<IAltaPluginCatalog>(new RuntimeAltaPluginCatalog(host.PluginRuntime))
            .Add(host.PluginRuntime)
            .AddPluginRuntimeHooks(host.PluginRuntime);
        if (changes is not null) services.Add(changes);
        if (editor is not null) services.Add(editor);
        if (terminals is not null) services.Add(terminals);
        if (automations is not null) services.Add(automations);
        if (worktrees is not null) services.Add(worktrees);
        if (plugins is not null) services.Add(plugins);
        if (workItems is not null) services.Add(workItems);
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, services);
        services.Add(registry).Add(dispatcher);
        return dispatcher;
    }

    /// <summary>
    /// Returns the supplier of an owned session's tools: one <c>alta</c> tool per session, identical in name,
    /// description and schema to the one alta commands give the sessions they create.
    /// </summary>
    internal static Func<OwnedSessionToolRequest, IReadOnlyList<AgentToolDefinition>> CreateSessionTools(AltaCommandDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return request =>
        {
            // A session being created has no id yet: its tool learns it from its first call.
            var session = request.SessionId;
            var tool = AltaSessionToolFactory.Create(dispatcher, new AltaSessionToolOptions
            {
                SourceSessionIdProvider = () => Volatile.Read(ref session),
                SourceProjectId = request.ProjectId,
                WorkingDirectory = request.WorkingDirectory,
                DefaultMaxOutputRecords = 200,
                DefaultMaxOutputBytes = 64 * 1024,
                DefaultTimeout = TimeSpan.FromSeconds(120),
            });
            return
            [
                tool with
                {
                    Handler = (invocation, cancellationToken) =>
                    {
                        Interlocked.CompareExchange(ref session, invocation.SessionId, null);
                        return tool.Handler(invocation, cancellationToken);
                    },
                },
            ];
        };
    }

    // The providers as the registry lists them now: Settings can add, remove and re-register providers while
    // the host runs.
    private sealed class RegisteredProviders(ModelProviderRegistry registry) : IReadOnlyList<ModelProviderDescriptor>
    {
        public int Count => registry.ListProviders().Count;

        public ModelProviderDescriptor this[int index] => registry.ListProviders()[index];

        public IEnumerator<ModelProviderDescriptor> GetEnumerator() => registry.ListProviders().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
