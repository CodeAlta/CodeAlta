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

    /// <summary>The name of the command that opens the dialog of search by meaning in the window: <c>/search_meaning</c>, and <b>Learn more</b> of the landing card.</summary>
    internal const string MeaningCommandName = "search_meaning";

    /// <summary>The name of the command of <b>Not now</b> on the landing card: the offer is put off for 30 days.</summary>
    internal const string MeaningLaterCommandName = "search_meaning_later";

    /// <summary>The id of the card of the landing page.</summary>
    internal const string LandingCardId = "index";

    private const string Summary = "Local search of CodeAlta Desktop: the prompts and final answers of the sessions, the user guide, the skills, the tools and the plugins, for the search window and alta search.";

    private readonly DesktopSearchRelay _relay;
    private string? _cardKey;

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
        yield return Command.Shell(MeaningCommandName, "Opens the dialog of search by meaning: what it gives, what it takes, and the switch that turns it on.", MeaningAsync) with
        {
            Label = "Search by meaning…",
            SearchText = "search by meaning semantic embedding model turn on",
        };
        // Run by the button "Not now" of the landing card only.
        yield return Command.Shell(MeaningLaterCommandName, "Puts off the offer of search by meaning for 30 days.", MeaningLaterAsync) with
        {
            Label = "Not now",
            ShowInCommandPalette = false,
            ShowInCommandBar = false,
            ShowInHelp = false,
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
            GetCard = (_, cancellationToken) => LandingCardAsync(_relay.Search, DateOnly.FromDateTime(DateTime.Now), cancellationToken),
        };
    }

    /// <inheritdoc />
    /// <remarks>The index reads the journal, which records no delta of streaming content: deltas are not needed.</remarks>
    public override bool ObservesContentDeltas => false;

    /// <inheritdoc />
    public override ValueTask OnAgentEventAsync(PluginAgentEventContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        // A signal, nothing more: the index reads the journal a moment later, and the callback returns at once.
        _relay.Forward(context.Event);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The card of the landing page: only while there is something to say. The download and the first indexing of search by
    /// meaning, or its failure; the first build of the index, or a failure of the indexer; and the offer of search by meaning
    /// once there is enough to search (never at the first start, never as a dialog). Otherwise there is no card.
    /// </summary>
    internal static async ValueTask<PluginLandingCard?> LandingCardAsync(DesktopSearch? search, DateOnly today, CancellationToken cancellationToken)
    {
        if (search is null || !search.Host.GetSettings().Enabled) return null;
        var open = PluginLandingCardAction.OpenCanvas("Open Search", CanvasId, icon: "search") with { Primary = true };
        PluginLandingCard Note(string text) => PluginLandingCard.Of($"<p class=\"{PluginHtml.MutedClass}\">{PluginHtml.Encode(text)}</p>", open);
        if (MeaningCard(search) is { } meaning)
        {
            return meaning.State switch
            {
                "downloading" => Note(meaning.Percent is { } received
                    ? string.Create(CultureInfo.InvariantCulture, $"Downloading the model of search by meaning: {received}%.") : "Downloading the model of search by meaning.")
                    with { Status = "Downloading", Tone = PluginStatusTone.Info },
                "loading" => Note("Loading the model of search by meaning.") with { Status = "Loading", Tone = PluginStatusTone.Info },
                "indexing" or "paused" => Note(string.Create(CultureInfo.InvariantCulture,
                        $"Indexing your work by meaning: {meaning.Status.Embedded:N0} of {meaning.Status.Total:N0} passages{Left(meaning.State == "indexing" ? meaning.Status.SecondsLeft : null)}."))
                    with { Status = meaning.State == "paused" ? "Paused" : "Indexing", Tone = meaning.State == "paused" ? PluginStatusTone.Muted : PluginStatusTone.Info },
                _ => Note("Search by meaning stopped: " + meaning.Status.Error) with { Status = "Failed", Tone = PluginStatusTone.Error },
            };
        }

        var status = search.Indexer.Status;
        switch (CardKindOf(status))
        {
            case 1:
                return Note(status.JournalsTotal > 0
                        ? string.Create(CultureInfo.InvariantCulture, $"Building the search index: {status.JournalsDone:N0} of {status.JournalsTotal:N0} sessions read.")
                        : "Building the search index.")
                    with { Status = "Building", Tone = PluginStatusTone.Info };
            case 2:
                return Note("The search index could not read everything: " + status.LastError) with { Status = "Failed", Tone = PluginStatusTone.Error };
        }

        if (!await search.IsMeaningOfferedAsync(today, cancellationToken).ConfigureAwait(false)) return null;
        return PluginLandingCard.Of($"<p><strong>Search your work by meaning</strong></p><p class=\"{PluginHtml.MutedClass}\">"
                + "Find past conversations, skills and tools from what they are about, not only from the words they contain. The model runs on this computer.</p>",
            PluginLandingCardAction.RunCommand("Learn more", MeaningCommandName, icon: "search") with { Primary = true },
            PluginLandingCardAction.RunCommand("Not now", MeaningLaterCommandName))
            with { Status = "New", Tone = PluginStatusTone.Info };

        static string Left(int? seconds) => seconds switch
        {
            null => string.Empty,
            < 90 => string.Create(CultureInfo.InvariantCulture, $" · about {Math.Max(1, seconds.Value)} s left"),
            < 90 * 60 => string.Create(CultureInfo.InvariantCulture, $" · about {(int)Math.Round(seconds.Value / 60d)} min left"),
            _ => string.Create(CultureInfo.InvariantCulture, $" · about {seconds.Value / 3600} h {seconds.Value % 3600 / 60} min left"),
        };
    }

    // Where search by meaning is while it has something to say on the card: on, and downloading, loading, indexing, paused or failed.
    private static (string State, MeaningStatus Status, int? Percent)? MeaningCard(DesktopSearch search)
    {
        if (search.Meaning is not { } meaning || !search.Host.GetSettings().Semantic) return null;
        var status = meaning.Status;
        if (status.State is not ("downloading" or "loading" or "indexing" or "paused" or "failed")) return null;
        int? percent = status.State switch
        {
            "downloading" => meaning.Installer.Current is { TotalBytes: > 0 } download ? (int)(download.ReceivedBytes * 100 / download.TotalBytes) : null,
            "indexing" or "paused" => status.Total > 0 ? (int)((long)status.Embedded * 100 / status.Total) : null,
            _ => null,
        };
        return (status.State, status, percent);
    }

    // 0 no card, 1 the first build, 2 a failure.
    private static int CardKindOf(SearchIndexerStatus status)
        => status.LastError is not null ? 2 : status.Busy && !status.Ready ? 1 : 0;

    /// <summary>
    /// What the card says, in a key that changes when it should be read again: a state, a whole percent of a progress, or that
    /// search by meaning may be offered. The numbers of a progress between two percents do not ask for it.
    /// </summary>
    internal static string CardKey(DesktopSearch? search, DateOnly today)
    {
        if (search is null || !search.Host.GetSettings().Enabled) return string.Empty;
        if (MeaningCard(search) is { } meaning) return string.Create(CultureInfo.InvariantCulture, $"meaning:{meaning.State}:{meaning.Percent}");
        var kind = CardKindOf(search.Indexer.Status);
        if (kind != 0) return "index:" + kind.ToString(CultureInfo.InvariantCulture);
        return search.Meaning is not null && search.Host.GetSettings().MayOfferMeaning(today) ? "offer" : string.Empty;
    }

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

        builder.Append("\n\nThe canvas has three pages: **Overview** (what the index holds for the space: items, sessions, the share searchable by meaning, the turns by day, the kinds, ")
            .Append("the projects and the state of the sources), **Explore** (a query with filters, by words, by meaning or both, each hit with its rank by words and by meaning) ")
            .Append("and **Activity** (the searches of the last 90 days by who asked them, those that found nothing and the median times; never the words searched). ")
            .Append("The same facts are those of `alta search status`, and the same queries those of `alta search <query>`.");
        return builder.ToString();
    }

    // The card follows the state of the indexer and of search by meaning: it is asked again when what it says changes, not for each journal read.
    private void OnSearchChanged()
    {
        var key = CardKey(_relay.Search, DateOnly.FromDateTime(DateTime.Now));
        if (!string.Equals(Interlocked.Exchange(ref _cardKey, key), key, StringComparison.Ordinal)) Services.Ui.InvalidateLandingCards();
    }

    private ValueTask<PluginCommandResult> MeaningAsync(PluginCommandContext context, CancellationToken cancellationToken)
    {
        if (_relay.Search is not { Meaning: not null } search) return new(PluginCommandResult.Message("Search by meaning is not available here."));
        return new(search.RequestMeaningDialog() ? PluginCommandResult.Handled : PluginCommandResult.Message("Search by meaning is turned on in Settings › Search."));
    }

    private ValueTask<PluginCommandResult> MeaningLaterAsync(PluginCommandContext context, CancellationToken cancellationToken)
    {
        if (_relay.Search is not { } search) return new(PluginCommandResult.Message("Search is not available here."));
        try
        {
            search.AnswerMeaningOffer("later", DateOnly.FromDateTime(DateTime.Now));
            return new(PluginCommandResult.Handled);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(PluginCommandResult.Message("The answer could not be saved: " + exception.Message));
        }
    }

    private async ValueTask<PluginCommandResult> OpenAsync(PluginCommandContext context, CancellationToken cancellationToken)
    {
        var result = await Services.Canvases.OpenAsync(CanvasId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Requested ? PluginCommandResult.Handled : PluginCommandResult.Message("The Search canvas cannot be shown here.");
    }
}
