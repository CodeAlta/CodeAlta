using System.Collections;
using System.Collections.Frozen;
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
/// and close the terminals of the window. <c>alta documentation</c> reads the user guide that ships with the
/// application, and shows a page of it in the Documentation tab.
/// </para>
/// <para>
/// Three things are specific to the desktop. <c>alta ask</c> goes to the window's ask panel through the run
/// that asks, so a run can ask once, while it runs, whatever started it. Sessions that alta commands create or
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
    /// <param name="appearance">How the window looks, for the <c>alta appearance</c> commands; without it the commands do not exist.</param>
    /// <param name="spaces">The space the window shows, for <c>alta space switch</c> and the current space of the commands; without it every command reads the whole catalog.</param>
    /// <param name="commands">What a caller may do that runs a command nobody reviews (a background job, terminal input, the command of an automation, a plugin build); without it every caller may.</param>
    /// <param name="canvases">The canvases of plugins the <c>alta canvas</c> commands use; without them the commands do not exist.</param>
    /// <param name="documentation">The user guide the <c>alta documentation</c> commands read; without it the commands do not exist.</param>
    /// <param name="documentationView">Where <c>alta documentation open</c> asks the window to show a page of the guide; without it that command does not exist.</param>
    /// <param name="bookmarks">The bookmarks the <c>alta bookmark</c> commands use; without them the commands do not exist.</param>
    /// <param name="search">The index the <c>alta search</c> commands read; without it the commands do not exist.</param>
    /// <param name="reviews">The reviews the <c>alta review</c> commands use; without them the commands do not exist.</param>
    /// <param name="reviewPolicy">Whether a session reviews its changes before commit, for <c>alta review status</c>.</param>
    /// <param name="windows">The windows the <c>alta window</c> commands use; without them the commands do not exist.</param>
    /// <returns>The dispatcher of the commands, for the other callers of the host (its MCP server).</returns>
    /// <exception cref="ArgumentNullException">The host or the reminders are null.</exception>
    internal static AltaCommandDispatcher Attach(CodeAltaHost host, AltaReminderService reminders, PluginAltaServiceBridge? pluginAlta = null, IAltaChangesView? changes = null,
        IAltaEditorView? editor = null, IAltaTerminals? terminals = null, IAltaAutomations? automations = null,
        CodeAlta.Catalog.Worktrees.GitWorktreeService? worktrees = null, AltaPluginWorkshop? plugins = null,
        CodeAlta.Catalog.WorkItems.WorkItemService? workItems = null, IAltaAppearance? appearance = null, IAltaSpaceView? spaces = null, AltaCommandReviewPolicy? commands = null,
        IAltaCanvasView? canvases = null, CodeAlta.Catalog.Documentation.ShippedDocumentation? documentation = null, IAltaDocumentationView? documentationView = null,
        CodeAlta.Catalog.Bookmarks.BookmarkService? bookmarks = null, Search.DesktopSearch? search = null,
        CodeAlta.Catalog.Reviews.ReviewService? reviews = null, IAltaReviewPolicy? reviewPolicy = null, IAltaWindows? windows = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(reminders);
        var dispatcher = Compose(host, reminders, changes, editor, terminals, automations, worktrees, plugins, workItems, appearance, spaces, commands, canvases, documentation, documentationView, bookmarks, search,
            reviews, reviewPolicy, windows);
        pluginAlta?.SetDispatcher(dispatcher);
        host.Commands.SessionTools = CreateSessionTools(dispatcher, search?.Service);
        return dispatcher;
    }

    /// <summary>Builds the dispatcher of the alta commands over a host's services.</summary>
    internal static AltaCommandDispatcher Compose(CodeAltaHost host, AltaReminderService reminders, IAltaChangesView? changes = null, IAltaEditorView? editor = null,
        IAltaTerminals? terminals = null, IAltaAutomations? automations = null, CodeAlta.Catalog.Worktrees.GitWorktreeService? worktrees = null,
        AltaPluginWorkshop? plugins = null, CodeAlta.Catalog.WorkItems.WorkItemService? workItems = null, IAltaAppearance? appearance = null,
        IAltaSpaceView? spaces = null, AltaCommandReviewPolicy? commands = null, IAltaCanvasView? canvases = null,
        CodeAlta.Catalog.Documentation.ShippedDocumentation? documentation = null, IAltaDocumentationView? documentationView = null,
        CodeAlta.Catalog.Bookmarks.BookmarkService? bookmarks = null, Search.DesktopSearch? search = null,
        CodeAlta.Catalog.Reviews.ReviewService? reviews = null, IAltaReviewPolicy? reviewPolicy = null, IAltaWindows? windows = null)
    {
        var permissions = host.RuntimeService.Permissions;
        var services = new AltaServiceCollection()
            .Add<ISessionRetentionAdmission>(host.RetentionProtection)
            .Add(host.CatalogOptions)
            .Add(host.SessionViewCatalog.TextFiles)
            .Add(host.ProjectCatalog)
            .Add(host.SpaceCatalog)
            .Add(host.SessionViewCatalog)
            .Add(host.RuntimeService)
            .Add(host.RuntimeService.SkillCatalog)
            .Add(host.AgentHub)
            .Add(host.ModelProviderInitializationService)
            .Add(host.ModelProviderRegistry)
            .Add(host.ProjectFileSearchService)
            .Add<IAltaAskService>(new OwnedSessionAltaAskService(host.Commands.Asks))
            // Archives through the owner of the sessions: a send it has under way for a session refuses its archive.
            .Add<ISessionArchive>(host.Commands)
            .Add<IAltaNotesService>(new RuntimeAltaNotesService(host.RuntimeService))
            .Add<IReadOnlyList<ModelProviderDescriptor>>(new RegisteredProviders(host.ModelProviderRegistry))
            .Add<IAltaSessionToolProviderPolicy>(new AltaSessionToolProviderPolicy())
            .Add<IAltaSessionInteractionDefaults>(new AltaSessionInteractionDefaults(
                permissions.OwnedDefaultPermissionHandler, permissions.OwnedDefaultUserInputHandler))
            .Add(reminders)
            // The plugins as they are when a command runs; without an active plugin both add nothing.
            .Add<IAltaPluginCatalog>(new RuntimeAltaPluginCatalog(host.PluginRuntime))
            .Add(host.PluginRuntime)
            // The bridge of the sends of the host: a send through `alta session send` gives the plugins what a send of the window does.
            .AddPluginRuntimeHooks(host.Commands.Plugins ?? new CodeAlta.Orchestration.Runtime.Plugins.PluginOrchestrationBridge(host.PluginRuntime.Adapter, () => host.PluginRuntime.ActivePlugins));
        if (changes is not null) services.Add(changes);
        if (editor is not null) services.Add(editor);
        if (terminals is not null) services.Add(terminals);
        if (automations is not null) services.Add(automations);
        if (worktrees is not null) services.Add(worktrees);
        if (plugins is not null) services.Add(plugins);
        if (workItems is not null) services.Add(workItems);
        if (appearance is not null) services.Add(appearance);
        if (spaces is not null) services.Add(spaces);
        if (commands is not null) services.Add(commands);
        if (canvases is not null) services.Add(canvases);
        if (documentation is not null) services.Add(documentation);
        if (documentation is not null && documentationView is not null) services.Add(documentationView);
        if (bookmarks is not null) services.Add(bookmarks);
        if (reviews is not null) services.Add(reviews);
        if (reviewPolicy is not null) services.Add(reviewPolicy);
        if (windows is not null) services.Add(windows);
        if (search is not null) services.Add(search.Service).Add(search.Indexer).Add(search.Store);
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
        => CreateSessionTools(dispatcher, search: null);

    /// <summary>
    /// Returns the supplier of an owned session's tools: one <c>alta</c> tool per session, and the <c>tool_search</c> tool,
    /// which finds deferred tools with the index of search. A session is sent <c>tool_search</c> only by a provider that defers
    /// tools, while some are (<see cref="CodeAlta.Agent.Runtime.AgentToolDeferral"/>).
    /// </summary>
    internal static Func<OwnedSessionToolRequest, IReadOnlyList<AgentToolDefinition>> CreateSessionTools(AltaCommandDispatcher dispatcher, CodeAlta.Search.SearchService? search)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        // One tool for every session: its name, description and schema never change.
        var toolSearch = AgentToolSearch.Create(search is null ? static (_, _) => ValueTask.FromResult<IReadOnlyList<AgentToolSearchHit>>([]) : ToolSearchOf(search));
        return request =>
        {
            // A session being created has no id yet: its tool learns it from its first call.
            var session = request.SessionId;
            var tool = AltaSessionToolFactory.Create(dispatcher, new AltaSessionToolOptions
            {
                SourceSessionIdProvider = () => Volatile.Read(ref session),
                SourceProjectId = request.ProjectId,
                WorkingDirectory = request.WorkingDirectory,
                // The tool outlives a worktree that is removed: its commands then start from the folder of the project.
                WorkingDirectoryProvider = request.ProjectDirectory is { } projectDirectory
                    ? () => Directory.Exists(request.WorkingDirectory) ? request.WorkingDirectory : projectDirectory
                    : null,
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
                toolSearch,
            ];
        };
    }

    private static readonly IReadOnlySet<CodeAlta.Search.SearchKind> ToolSearchKinds = new HashSet<CodeAlta.Search.SearchKind> { CodeAlta.Search.SearchKind.Tool, CodeAlta.Search.SearchKind.Mcp }.ToFrozenSet();

    // The tools and MCP tools of the index that match, by the name a session has them under: an MCP tool by its alias. The
    // session loads only those of its deferred tools: the space and the policy of the session decided which it has.
    private static AgentToolSearchHandler ToolSearchOf(CodeAlta.Search.SearchService search)
        => async (query, cancellationToken) =>
        {
            CodeAlta.Search.SearchResponse response;
            try
            {
                response = await search.SearchAsync(new CodeAlta.Search.SearchRequest(query.Query, new CodeAlta.Search.SearchScope { Kinds = ToolSearchKinds })
                {
                    Limit = Math.Min(50, query.Limit * 3), Origin = "tool_search", OriginSessionId = query.SessionId,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or ObjectDisposedException)
            {
                return [];
            }

            var hits = new List<AgentToolSearchHit>(response.Hits.Count);
            foreach (var hit in response.Hits)
            {
                var name = hit.Kind == CodeAlta.Search.SearchKind.Mcp ? ReadExtra(hit.Extra, "alias") : hit.Title;
                if (!string.IsNullOrWhiteSpace(name)) hits.Add(new AgentToolSearchHit(name, string.IsNullOrWhiteSpace(hit.Snippet) ? null : hit.Snippet));
            }

            return hits;
        };

    private static string? ReadExtra(string? extra, string name)
    {
        if (string.IsNullOrEmpty(extra)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(extra);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
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
