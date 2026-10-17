using System.Globalization;
using System.Text;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Search;

namespace CodeAlta.Desktop.Search;

/// <summary>
/// Search, as a plugin of the window: it tells the index when a prompt is written and when a turn ends, so that both
/// are found within seconds instead of at the next pass. It also declares the Search canvas, the command that opens it,
/// and a card of the landing page while the index is first built or has failed.
/// </summary>
/// <remarks>
/// The index and its queries are the window's (<see cref="DesktopSearch"/>); the plugin holds no state of its own. The
/// canvas is drawn by a module of the application's own build (<c>src/CodeAlta/frontend/src/search/canvas</c>), which
/// asks the <c>localSearch</c> service of the window for what it shows.
/// </remarks>
[Plugin(PluginId, DisplayName = "Search", Description = Summary)]
internal sealed class DesktopSearchPlugin : PluginBase
{
    /// <summary>The id of the plugin.</summary>
    internal const string PluginId = "search";

    /// <summary>The key of the plugin, as the canvases and the commands name it.</summary>
    internal const string PluginKey = "builtin:" + PluginId;

    /// <summary>The id of the canvas: <c>alta canvas open search</c>.</summary>
    internal const string CanvasId = "search";

    /// <summary>The name of the module of the application's build that draws the canvas.</summary>
    internal const string AppModule = "search";

    /// <summary>The name of the command that opens the canvas; <c>/search_index</c>, since <c>/search</c> is the search window.</summary>
    internal const string OpenCommandName = "search_index";

    /// <summary>The id of the card of the landing page.</summary>
    internal const string LandingCardId = "index";

    private const string Summary = "Local search of CodeAlta Desktop: the prompts and final answers of the sessions, the user guide, the skills, the tools and the plugins, for the search window and alta search.";

    private readonly DesktopSearchRelay _relay;
    private int _cardKind = -1;

    private DesktopSearchPlugin(DesktopSearchRelay relay) => _relay = relay;

    /// <summary>The definition a window lists among its built-in plugins.</summary>
    /// <param name="relay">Where the events of the sessions go.</param>
    internal static BuiltInPluginDefinition Definition(DesktopSearchRelay relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        return new()
        {
            Id = PluginId, DisplayName = "Search", Description = Summary,
            PluginType = typeof(DesktopSearchPlugin), Factory = () => new DesktopSearchPlugin(relay),
        };
    }

    /// <inheritdoc />
    public override IEnumerable<PluginSystemPromptContribution> GetSystemPromptContributions()
    {
        // One line, the same from turn to turn while search is on: provider prompt caches survive.
        yield return Prompt.Dynamic(
            PluginPromptChannel.Developer,
            (_, _) => new ValueTask<string?>(_relay.Enabled() ? Guidance : null),
            title: "Search",
            kind: PluginPromptPartKind.ToolGuidance,
            order: 55);
    }

    /// <summary>What the instructions of a session say about search while it is on.</summary>
    internal const string Guidance = "Search: `alta search <need>` finds earlier conversations, plans and tasks, skills, tools, MCP servers and the user guide on this computer; " +
        "`--kind skill,tool,mcp,doc` keeps to what can be used, and `alta search show <ref>` reads a hit whole.";

    /// <inheritdoc />
    public override ValueTask OnActivatedAsync(CancellationToken cancellationToken = default)
    {
        _relay.Changed += OnSearchChanged;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask OnDeactivatingAsync(CancellationToken cancellationToken = default)
    {
        _relay.Changed -= OnSearchChanged;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override IEnumerable<PluginCanvasContribution> GetCanvases()
    {
        yield return new PluginCanvasContribution
        {
            Id = CanvasId,
            Title = "Search",
            Description = "What the local search index holds for the space, and what a query finds in it and why.",
            Icon = "search",
            Scope = PluginCanvasScope.Application,
            Open = static (_, _) => new ValueTask<PluginCanvasView>(new PluginCanvasView { Script = PluginScript.App(AppModule) }),
            Describe = (_, _) => new ValueTask<string?>(Describe(_relay.Search)),
        };
    }

    /// <inheritdoc />
    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Shell(OpenCommandName, "Opens the Search canvas: what the local search index holds, and what a query finds in it.", OpenAsync) with
        {
            Label = "Search index",
            SearchText = "search index canvas overview explore",
        };
    }

    /// <inheritdoc />
    public override IEnumerable<PluginLandingCardContribution> GetLandingCards()
    {
        yield return new PluginLandingCardContribution
        {
            Id = LandingCardId,
            Title = "Search",
            Icon = "search",
            GetCard = (context, _) => new ValueTask<PluginLandingCard?>(LandingCard(_relay.Search)),
        };
    }

    /// <inheritdoc />
    public override ValueTask OnAgentEventAsync(PluginAgentEventContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        // A signal, nothing more: the index reads the journal a moment later, and the callback returns at once.
        _relay.Forward(context.Event);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The card of the landing page: only while there is something to say, the first build of the index or a failure
    /// of the indexer; otherwise there is no card.
    /// </summary>
    internal static PluginLandingCard? LandingCard(DesktopSearch? search)
    {
        if (search is null || !search.Host.GetSettings().Enabled) return null;
        var status = search.Indexer.Status;
        var open = PluginLandingCardAction.OpenCanvas("Open Search", CanvasId, icon: "search") with { Primary = true };
        PluginLandingCard Note(string text) => PluginLandingCard.Of($"<p class=\"{PluginHtml.MutedClass}\">{PluginHtml.Encode(text)}</p>", open);
        return CardKindOf(status) switch
        {
            1 => Note(status.JournalsTotal > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"Building the search index: {status.JournalsDone:N0} of {status.JournalsTotal:N0} sessions read.")
                    : "Building the search index.")
                with { Status = "Building", Tone = PluginStatusTone.Info },
            2 => Note("The search index could not read everything: " + status.LastError) with { Status = "Failed", Tone = PluginStatusTone.Error },
            _ => null,
        };
    }

    // 0 no card, 1 the first build, 2 a failure.
    private static int CardKindOf(SearchIndexerStatus status)
        => status.LastError is not null ? 2 : status.Busy && !status.Ready ? 1 : 0;

    internal static string Describe(DesktopSearch? search)
    {
        var builder = new StringBuilder("# Search\n\n");
        if (search is null)
        {
            builder.Append("The search index is not available in this window.");
        }
        else if (!search.Host.GetSettings().Enabled)
        {
            builder.Append("Search is turned off in Settings › Search.");
        }
        else
        {
            var status = search.Indexer.Status;
            builder.Append(status.Ready ? "The index is built and follows the sessions as they run." : status.Busy ? "The index is being built." : "The index starts shortly.");
            if (status.LastError is { } error) builder.Append(" Its last error: ").Append(error);
        }

        builder.Append("\n\nThe canvas has two pages: **Overview** (what the index holds for the space: items, sessions, the turns by day, the kinds, the projects and the state of the sources) ")
            .Append("and **Explore** (a query with filters, each hit with its rank by words). Search is by words only for now. ")
            .Append("The same facts are those of `alta search status`, and the same queries those of `alta search <query>`.");
        return builder.ToString();
    }

    // The card follows the state of the indexer: it is asked again when the state changes, not for each journal read.
    private void OnSearchChanged()
    {
        var search = _relay.Search;
        var kind = search is null || !search.Host.GetSettings().Enabled ? 0 : CardKindOf(search.Indexer.Status);
        if (Interlocked.Exchange(ref _cardKind, kind) != kind) Services.Ui.InvalidateLandingCards();
    }

    private async ValueTask<PluginCommandResult> OpenAsync(PluginCommandContext context, CancellationToken cancellationToken)
    {
        var result = await Services.Canvases.OpenAsync(CanvasId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Requested ? PluginCommandResult.Handled : PluginCommandResult.Message("The Search canvas cannot be shown here.");
    }
}
