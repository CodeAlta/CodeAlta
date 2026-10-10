using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Plugin.Statistics;
using CodeAlta.Plugins.Abstractions;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Timeline cards that plugins derive from the events of a session, such as the Statistics plugin's summary
/// of each completed turn. The terminal host gets them from the plugin runtime as events arrive; the desktop
/// host derives them from the journal when the page asks: the Statistics cards with a projection of its own,
/// the cards of the other plugins with the projections the plugin runtime holds.
/// </summary>
/// <remarks>
/// A turn can only be summarized from all of its events, so the journal is read backwards from its end until
/// the oldest run the page shows is complete. The walk is bounded: a window that reaches further back than
/// <see cref="MaximumPages"/> pages gets cards for its newer turns only.
/// </remarks>
[NeoRpcService("sessionPluginEvents", Version = 1)]
internal sealed class SessionPluginEventsService
{
    /// <summary>Plugin id of the built-in Statistics plugin, as used in the configuration.</summary>
    internal const string StatisticsPluginId = "statistics";

    /// <summary>Largest number of cards in one response; the newest ones are kept.</summary>
    internal const int MaximumEvents = 32;

    /// <summary>Largest number of journal pages read for one request (100 records each at most).</summary>
    internal const int MaximumPages = 64;

    internal const int MaximumMarkdownUnits = 4096;
    internal const int MaximumDetailUnits = 16 * 1024;
    internal const int MaximumDetailSections = 4;
    internal const int MaximumHtmlUnits = 16 * 1024;
    private const int MaximumHeaderUnits = 128;
    private const int MaximumResponseUnits = 96 * 1024;

    private static readonly PluginContributionHandle StatisticsHandle = new()
    {
        PluginRuntimeKey = "builtin:" + StatisticsPluginId,
        PluginTypeName = typeof(StatisticsPlugin).FullName!,
        Point = PluginPoint.SessionEventProjection,
        RuntimeContributionKey = StatisticsPluginId,
        NaturalName = StatisticsPluginId,
    };

    private readonly Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>>? _read;
    private readonly Func<string?, CancellationToken, Task<bool>>? _statisticsEnabled;
    private readonly string? _epoch;
    private readonly ProjectCatalog? _projects;
    private readonly CodeAlta.Plugins.PluginRuntimeManager? _plugins;
    private readonly DesktopPluginModules? _modules;
    // One instance for the host: it keeps the summary of each completed turn it has already built.
    private readonly PluginSessionEventProjectionContribution[] _statistics = [.. new StatisticsPlugin().GetSessionEventProjections()];

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal SessionPluginEventsService()
    {
    }

    /// <summary>Creates the service over literal reads.</summary>
    /// <param name="read">Reads one reverse journal page of a session; a null cursor means its end.</param>
    /// <param name="statisticsEnabled">Whether the Statistics plugin is enabled for a project id (null for global sessions).</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="projects">The project catalog that resolves the project of a request for the other plugins, or null.</param>
    /// <param name="plugins">The plugin runtime whose other projections are run, or null.</param>
    /// <exception cref="ArgumentNullException">A callback is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal SessionPluginEventsService(
        Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>> read,
        Func<string?, CancellationToken, Task<bool>> statisticsEnabled, string epoch,
        ProjectCatalog? projects = null, CodeAlta.Plugins.PluginRuntimeManager? plugins = null, DesktopPluginModules? modules = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(statisticsEnabled);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _read = read;
        _statisticsEnabled = statisticsEnabled;
        _epoch = epoch;
        _projects = projects;
        _plugins = plugins;
        _modules = modules;
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="reads">The host's admitted session reads.</param>
    /// <param name="projects">The host's project catalog; its global root holds the configuration.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="plugins">The host's plugin runtime, or null when the host runs no plugin.</param>
    /// <param name="modules">The server of the scripts of cards, or null for none.</param>
    internal SessionPluginEventsService(CodeAlta.Orchestration.Runtime.OwnedSessionWorkspace reads, ProjectCatalog projects, string epoch,
        CodeAlta.Plugins.PluginRuntimeManager? plugins = null, DesktopPluginModules? modules = null)
        : this((reads ?? throw new ArgumentNullException(nameof(reads))).ReadTimelinePageAsync, StatisticsEnablement(projects), epoch, projects, plugins, modules)
    {
    }

    /// <summary>Returns the cards of the turns that have an event at or after <c>NotBefore</c>, oldest first.</summary>
    [NeoRpcMethod("read")]
    public async Task<SessionPluginEventsResponse> ReadAsync(SessionPluginEventsRequest request, CancellationToken cancellationToken)
    {
        var sessionId = Identity(request?.SessionId) ? request!.SessionId : null;
        SessionPluginEventsResponse Error(string status) => new(status, sessionId, []);
        if (_read is null || _statisticsEnabled is null) return Error("unavailable");
        if (sessionId is null || request!.ProjectId is { } project && !Identity(project)) return Error("invalid_request");
        if (!string.Equals(request.ExpectedHostEpoch, _epoch, StringComparison.Ordinal)) return Error("stale_epoch");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var statistics = await _statisticsEnabled(request.ProjectId, cancellationToken).ConfigureAwait(false);
            var others = await OtherProjectionsAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
            if (!statistics && others.Count == 0) return new("ok", sessionId, []);
            var events = await ReadTurnsAsync(_read, sessionId, request.NotBefore, cancellationToken).ConfigureAwait(false);
            if (events.Count == 0) return new("ok", sessionId, []);
            var cards = new List<SessionPluginEvent>();
            // The cards of a plugin share its package: its folder is looked at once for this read, not once for each card.
            var modules = _modules?.StartRead();
            PluginSessionEventProjectionContext Context(PluginContributionHandle handle, string? projectPath) => new()
            {
                Handle = handle,
                SessionId = sessionId,
                ProjectId = request.ProjectId,
                ProjectPath = projectPath,
                RuntimeSessionId = events[^1].SessionId,
                RunId = events.LastOrDefault(static item => item.RunId is not null)?.RunId?.Value,
                Events = events,
                IsReplay = true,
                IsCompleteBatch = true,
            };
            if (statistics)
            {
                foreach (var contribution in _statistics)
                {
                    var derived = await contribution.ProjectAsync(Context(StatisticsHandle, null), cancellationToken).ConfigureAwait(false);
                    cards.AddRange(derived.Where(static item => !item.Remove).Select(item => Project(item, StatisticsPluginId, events[^1].Timestamp, StatisticsHandle.PluginRuntimeKey, modules)));
                }
            }

            foreach (var (registration, projectPath) in others)
            {
                try
                {
                    var contribution = (PluginSessionEventProjectionContribution)registration.Contribution;
                    var derived = await contribution.ProjectAsync(Context(registration.Handle, projectPath), cancellationToken).ConfigureAwait(false);
                    cards.AddRange(derived.Where(static item => item is not null && !item.Remove)
                        .Select(item => Project(item, registration.Handle.PluginRuntimeKey, events[^1].Timestamp, registration.Handle.PluginRuntimeKey, modules)));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One plugin failing leaves the cards of the others.
                    LogManager.GetLogger("CodeAlta.Desktop.Plugins").Error(exception, $"Session event projection of '{registration.Handle.PluginRuntimeKey}' failed");
                }
            }

            // The newest turns matter most: keep them when the response would be too large.
            var kept = new List<SessionPluginEvent>();
            var budget = MaximumResponseUnits;
            foreach (var card in cards.Where(card => card.Timestamp >= request.NotBefore).OrderByDescending(static card => card.Timestamp))
            {
                var cost = 256 + card.EventId.Length + card.Markdown.Length + (card.Html?.Length ?? 0)
                    + card.Details.Sum(static detail => detail.Header.Length + detail.Markdown.Length + (detail.Html?.Length ?? 0));
                if (kept.Count == MaximumEvents || cost > budget) break;
                budget -= cost;
                kept.Add(card);
            }

            kept.Reverse();
            return new("ok", sessionId, [.. kept]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AgentSessionHistoryException exception)
        {
            return Error(exception.Code is "missing_session" or "history_changed" ? exception.Code : "read_failed");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return Error("missing_session"); }
        catch (ObjectDisposedException) { return Error("closed"); }
        catch (Exception) { return Error("read_failed"); } // Never serialize reader or plugin exception details.
    }

    /// <summary>
    /// Reads, in journal order, every event of the runs that have an event at or after <paramref name="notBefore"/>.
    /// A run whose beginning lies beyond the page limit is left out rather than summarized from a part of it.
    /// </summary>
    internal static async Task<IReadOnlyList<AgentEvent>> ReadTurnsAsync(
        Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>> read,
        string sessionId, DateTimeOffset notBefore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        var entries = new List<AgentSessionHistoryEntry>();
        var shown = new HashSet<string>(StringComparer.Ordinal);
        AgentSessionHistoryCursor? cursor = null;
        var complete = false;
        for (var pages = 0; pages < MaximumPages && !complete; pages++)
        {
            var page = await read(sessionId, cursor, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            entries.AddRange(page.Entries);
            foreach (var entry in page.Entries)
            {
                if (entry.Event.Timestamp >= notBefore && entry.Event.RunId is { } run) shown.Add(run.Value);
            }

            // Runs follow each other in the journal: an older record of a run that is not shown ends the walk.
            var oldest = page.Entries.MinBy(static entry => entry.Offset);
            complete = page.Next is null
                || oldest is not null && oldest.Event.Timestamp < notBefore && oldest.Event.RunId is { } older && !shown.Contains(older.Value);
            cursor = page.Next;
        }

        var ordered = entries.OrderBy(static entry => entry.Offset).Select(static entry => entry.Event).ToList();
        var first = ordered.FindIndex(item => item.RunId is { } run && shown.Contains(run.Value));
        if (first < 0) return [];
        ordered.RemoveRange(0, first);
        if (!complete && ordered[0].RunId is { } partial) ordered.RemoveAll(item => item.RunId == partial);
        return ordered;
    }

    /// <summary>
    /// The projections of the active plugins that apply to the project, the Statistics plugin left out: its
    /// cards come from this service's own instance, with its switch read from the configuration at each call.
    /// </summary>
    private async ValueTask<IReadOnlyList<(CodeAlta.Plugins.PluginContributionRegistration Registration, string? ProjectPath)>> OtherProjectionsAsync(
        string? projectId, CancellationToken cancellationToken)
    {
        if (_plugins is null || _projects is null) return [];
        var project = await SettingsProjectScope.ResolveAsync(_projects, projectId, allowArchived: true, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return [];
        var options = new CodeAlta.Plugins.PluginAdapterOperationOptions { ProjectId = projectId, ProjectPath = project.Root, HasInteractiveUi = true };
        return [.. _plugins.Adapter.GetContributions<PluginSessionEventProjectionContribution>(PluginPoint.SessionEventProjection, options)
            .Where(static registration => !string.Equals(registration.Handle.PluginRuntimeKey, StatisticsHandle.PluginRuntimeKey, StringComparison.Ordinal))
            .Select(registration => (registration, project.Root))];
    }

    private static SessionPluginEvent Project(PluginDerivedSessionEvent derived, string pluginId, DateTimeOffset fallback, string runtimeKey, DesktopPluginModules.Read? modules)
    {
        string? script = null, scriptProblem = null;
        if ((derived.DynamicContent?.Html ?? derived.Html) is not null && derived.Script is { HasEntry: true } wanted)
        {
            script = modules?.PublishFor(runtimeKey, wanted);
            if (script is null) scriptProblem = "The script of the card could not be found.";
        }

        var sections = derived.DynamicContent?.DetailSections is { Count: > 0 } dynamic ? dynamic : derived.DetailSections;
        var html = derived.DynamicContent?.Html ?? derived.Html;
        return new(Cut(derived.EventId, 512), Cut(pluginId, 512), derived.Timestamp ?? fallback,
            Cut(derived.DynamicContent?.Markdown ?? derived.Markdown ?? string.Empty, MaximumMarkdownUnits),
            [.. sections.Take(MaximumDetailSections).Select(static section =>
                new SessionPluginEventDetail(Cut(section.Header, MaximumHeaderUnits), Cut(section.Markdown, MaximumDetailUnits),
                    section.Html is null ? null : Cut(section.Html, MaximumHtmlUnits)))],
            html is null ? null : Cut(html, MaximumHtmlUnits)) { Script = script, ScriptProblem = scriptProblem };
    }

    // Cuts between characters, never inside a surrogate pair.
    private static string Cut(string value, int maximum)
        => value.Length <= maximum ? value : value[..(char.IsHighSurrogate(value[maximum - 1]) ? maximum - 1 : maximum)];

    private static bool Identity(string? value) => value is { Length: > 0 and <= 256 } && value == value.Trim() && !value.Any(char.IsControl);

    /// <summary>
    /// Reads the Statistics switch from the configuration at each call, by the rule the Plugins settings page
    /// shows: the global switch, then the project's, then enabled.
    /// </summary>
    internal static Func<string?, CancellationToken, Task<bool>> StatisticsEnablement(ProjectCatalog projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var store = new CodeAltaConfigStore(projects.Options);
        return async (projectId, cancellationToken) =>
        {
            var global = Configured(store.LoadGlobal());
            if (global is not null) return global.Value;
            var project = await SettingsProjectScope.ResolveAsync(projects, projectId, cancellationToken).ConfigureAwait(false);
            return project is { Status: "ok", Root: { } root } ? Configured(store.LoadProject(root)) ?? true : true;
        };
    }

    private static bool? Configured(CodeAltaConfigDocument? document)
        => document?.Plugins?.FirstOrDefault(static plugin => string.Equals(plugin.Key, StatisticsPluginId, StringComparison.OrdinalIgnoreCase)).Value?.Enabled;
}

/// <summary>Asks for the plugin cards of the turns shown from <paramref name="NotBefore"/> on.</summary>
/// <param name="ExpectedHostEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="SessionId">The session.</param>
/// <param name="ProjectId">The session's project, or null for a global session.</param>
/// <param name="NotBefore">Timestamp of the oldest timeline record the page has loaded.</param>
internal sealed record SessionPluginEventsRequest(string ExpectedHostEpoch, string SessionId, string? ProjectId, DateTimeOffset NotBefore);

/// <summary><c>ok</c> with the cards oldest first, or a refusal code with none.</summary>
internal sealed record SessionPluginEventsResponse(string Status, string? SessionId, SessionPluginEvent[] Events);

/// <summary>
/// One plugin-derived timeline card: a one-paragraph Markdown summary and collapsed Markdown details. When
/// the plugin gives an HTML fragment, the page shows it instead of the Markdown, which stays what Copy uses.
/// </summary>
internal sealed record SessionPluginEvent(string EventId, string PluginId, DateTimeOffset Timestamp, string Markdown, SessionPluginEventDetail[] Details,
    string? Html = null)
{
    /// <summary>The path of the module that draws the fragment of the card, on the origin of the application, or null for a fragment alone.</summary>
    public string? Script { get; init; }

    /// <summary>Why the script is not served, or null.</summary>
    public string? ScriptProblem { get; init; }
}

/// <summary>A titled detail section of a card: Markdown, and an HTML fragment shown instead when there is one.</summary>
internal sealed record SessionPluginEventDetail(string Header, string Markdown, string? Html = null);
