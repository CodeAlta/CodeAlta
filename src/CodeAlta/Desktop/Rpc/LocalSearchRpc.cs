using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Bookmarks;
using CodeAlta.Catalog.Search;
using CodeAlta.Desktop.Search;
using CodeAlta.Desktop.Search.Embedding;
using CodeAlta.Search;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The page's side of the local search: the queries of the search window and of the Search canvas, where a message
/// found is in its journal, what the index holds, its settings and the projects and spaces kept out of it, and the
/// commands of the window, which the page gives so that they are found too.
/// </summary>
/// <remarks>Named <c>localSearch</c>: the search window also searches the files of a project, which is another service.</remarks>
[NeoRpcService("localSearch", Version = 1)]
internal sealed class LocalSearchService
{
    /// <summary>The most hits of one answer.</summary>
    internal const int MaximumHits = 100;

    /// <summary>The most commands of the window the page gives.</summary>
    internal const int MaximumCommands = 1000;

    private readonly DesktopSearch? _search;
    private readonly Func<string?, CancellationToken, Task<SpaceFilter?>>? _spaceFilter;
    private readonly Func<string, CancellationToken, Task<IReadOnlySet<string>?>>? _spaceProjects;
    private readonly IBookmarkJournal? _journal;
    private readonly string? _epoch;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal LocalSearchService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="search">The search of the window.</param>
    /// <param name="spaceFilter">What a space lets its sessions see (skills, MCP servers, plugins); null for the default space.</param>
    /// <param name="spaceProjects">The projects of a space other than the default one.</param>
    /// <param name="journal">Finds a message in the journal of its session.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    internal LocalSearchService(DesktopSearch search, Func<string?, CancellationToken, Task<SpaceFilter?>> spaceFilter,
        Func<string, CancellationToken, Task<IReadOnlySet<string>?>> spaceProjects, IBookmarkJournal journal, string epoch)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(spaceFilter);
        ArgumentNullException.ThrowIfNull(spaceProjects);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_search, _spaceFilter, _spaceProjects, _journal, _epoch) = (search, spaceFilter, spaceProjects, journal, epoch);
    }

    /// <summary>Finds what a query names, in the space the window shows.</summary>
    [NeoRpcMethod("query")]
    public async Task<LocalSearchQueryResponse> QueryAsync(LocalSearchQueryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, [], false, "off", 0);
        var query = request.Query?.Trim() ?? string.Empty;
        if (query.Length is 0 or > 500) return new("invalid_request", [], false, "off", 0);
        HashSet<SearchKind>? kinds = null;
        foreach (var name in request.Kinds ?? [])
        {
            if (!SearchNames.TryParseKind(name, out var kind)) return new("invalid_request", [], false, "off", 0);
            (kinds ??= []).Add(kind);
        }

        var settings = _search!.Host.GetSettings();
        if (!settings.Enabled) return new("disabled", [], false, "off", 0);
        try
        {
            var scope = await ScopeAsync(request.SpaceId, kinds, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(request.ProjectId)) scope = scope with { ProjectId = request.ProjectId };
            var response = await _search.Service.SearchAsync(new SearchRequest(query, scope)
            {
                Origin = "window",
                Limit = Math.Clamp(request.Limit ?? 20, 1, MaximumHits),
                PrefixLastTerm = request.Typing,
                Mode = request.Mode switch { "words" => SearchMode.Keyword, "meaning" => SearchMode.Meaning, _ => SearchMode.Auto },
            }, cancellationToken).ConfigureAwait(false);
            var hits = response.Hits.Select(static (hit, index) => Hit(hit, index)).ToList();
            return new("ok", hits, response.Truncated, Meaning(response.Meaning), (int)Math.Ceiling(response.WordsMilliseconds + response.MeaningMilliseconds));
        }
        catch (Exception exception) when (Failure(exception, cancellationToken))
        {
            return new("read_failed", [], false, "off", 0);
        }
    }

    /// <summary>Where a message found is in the journal of its session now, for the timeline to show it.</summary>
    [NeoRpcMethod("locate")]
    public async Task<LocalSearchLocateResponse> LocateAsync(LocalSearchRefRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null, null);
        if (request.Ref is not { Length: > 4 and <= 600 } reference || !reference.StartsWith("msg:", StringComparison.Ordinal))
            return new("invalid_request", null, null, null);
        try
        {
            if (await _search!.Service.FindAsync(reference, cancellationToken).ConfigureAwait(false) is not { SessionId: { } sessionId } item)
                return new("not_found", null, null, null);
            var slash = reference.IndexOf('/', 4);
            if (slash < 0) return new("invalid_request", null, null, null);
            var contentId = reference[(slash + 1)..];
            var found = await _journal!.FindAsync(sessionId, item.RunId, contentId, item.Role == SearchRole.Assistant ? "assistant" : "user", cancellationToken).ConfigureAwait(false);
            return found.Status switch
            {
                BookmarkJournalStatus.Found => new("ok", sessionId, item.ProjectId, found.Message!.Offset.ToString(CultureInfo.InvariantCulture)),
                BookmarkJournalStatus.SessionMissing => new("session_missing", sessionId, item.ProjectId, null),
                // The session is there and the message is not any more (a rewind): the session is still shown.
                _ => new("gone", sessionId, item.ProjectId, null),
            };
        }
        catch (Exception exception) when (Failure(exception, cancellationToken))
        {
            return new("read_failed", null, null, null);
        }
    }

    /// <summary>What the index holds and where its indexer is.</summary>
    [NeoRpcMethod("status")]
    public async Task<LocalSearchStatusResponse> StatusAsync(LocalSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return LocalSearchStatusResponse.Refused(refused);
        try
        {
            var status = await _search!.Service.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            var settings = _search.Host.GetSettings();
            var counts = status.Counts;
            return new("ok", Settings(settings), MeaningOf(settings, counts), status.Indexer.Busy, status.Indexer.Ready, status.Indexer.JournalsDone, status.Indexer.JournalsTotal,
                status.Indexer.LastPass, status.Indexer.LastError, Meaning(status.Meaning), (int)Math.Round(status.Coverage * 100),
                counts.Items.ToDictionary(static pair => SearchNames.Of(pair.Key), static pair => Count(pair.Value)), Count(counts.Chunks), Count(counts.Sessions), Count((counts.Bytes + 1023) / 1024),
                counts.Projects.ToDictionary(static pair => pair.Key, static pair => Count(pair.Value)),
                [.. counts.Days.Select(static day => new LocalSearchDay(day.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Count(day.Turns)))]);
        }
        catch (Exception exception) when (Failure(exception, cancellationToken))
        {
            return LocalSearchStatusResponse.Refused("read_failed");
        }
    }

    /// <summary>Turns search by meaning on with a model: what is missing is downloaded, then the chunks are embedded in the background.</summary>
    [NeoRpcMethod("turnOnMeaning")]
    public LocalSearchStatusOnlyResponse TurnOnMeaning(LocalSearchModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (_search!.Meaning is not { } meaning) return new("unavailable");
        if (EmbeddingModelCatalog.Find(request.ModelId) is null) return new("invalid_request");
        try { meaning.TurnOn(request.ModelId!); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { return new("write_failed"); }
        return new("ok");
    }

    /// <summary>Turns search by meaning off: the worker ends; the model and its vectors stay.</summary>
    [NeoRpcMethod("turnOffMeaning")]
    public LocalSearchStatusOnlyResponse TurnOffMeaning(LocalSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (_search!.Meaning is not { } meaning) return new("unavailable");
        try { meaning.TurnOff(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { return new("write_failed"); }
        return new("ok");
    }

    /// <summary>Pauses the indexing by meaning, or lets it go on; queries are still answered.</summary>
    [NeoRpcMethod("pauseMeaning")]
    public LocalSearchStatusOnlyResponse PauseMeaning(LocalSearchPauseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (_search!.Meaning is not { } meaning) return new("unavailable");
        meaning.Paused = request.Paused;
        return new("ok");
    }

    /// <summary>Stops the download of a model; what is on disk is resumed by the next one.</summary>
    [NeoRpcMethod("cancelDownload")]
    public LocalSearchStatusOnlyResponse CancelDownload(LocalSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (_search!.Meaning is not { } meaning) return new("unavailable");
        meaning.Installer.Cancel();
        return new("ok");
    }

    /// <summary>Removes a model and its vectors from the disk; refused for the model in use.</summary>
    [NeoRpcMethod("deleteModel")]
    public async Task<LocalSearchStatusOnlyResponse> DeleteModelAsync(LocalSearchModelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (_search!.Meaning is not { } meaning) return new("unavailable");
        if (EmbeddingModelCatalog.Find(request.ModelId) is null) return new("invalid_request");
        try { return new(await meaning.DeleteModelAsync(request.ModelId!, cancellationToken).ConfigureAwait(false) ? "ok" : "in_use"); }
        catch (Exception exception) when (Failure(exception, cancellationToken)) { return new("write_failed"); }
    }

    /// <summary>Answers the offer of search by meaning: <c>later</c> (asked again in 30 days) or <c>never</c>.</summary>
    [NeoRpcMethod("answerOffer")]
    public LocalSearchSettingsResponse AnswerOffer(LocalSearchOfferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null);
        if (request.Answer is not ("later" or "never")) return new("invalid_request", null);
        try
        {
            return new("ok", Settings(_search!.AnswerMeaningOffer(request.Answer, DateOnly.FromDateTime(DateTime.Now))));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new("write_failed", null);
        }
    }

    /// <summary>What a space can see in the index, and how its sources are read: the overview of the Search canvas.</summary>
    [NeoRpcMethod("overview")]
    public async Task<LocalSearchOverviewResponse> OverviewAsync(LocalSearchOverviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return LocalSearchOverviewResponse.Refused(refused);
        try
        {
            var scope = await ScopeAsync(request.SpaceId, null, cancellationToken).ConfigureAwait(false);
            var overview = await _search!.Service.GetOverviewAsync(scope, DateTimeOffset.UtcNow.AddDays(-7), cancellationToken).ConfigureAwait(false);
            var counts = overview.Counts;
            return new("ok", counts.Items.ToDictionary(static pair => SearchNames.Of(pair.Key), static pair => Count(pair.Value)), Count(counts.Chunks), Count(counts.Sessions),
                Count(counts.Recent), Count((counts.Bytes + 1023) / 1024), counts.Projects.ToDictionary(static pair => pair.Key, static pair => Count(pair.Value)),
                [.. counts.Days.Select(static day => new LocalSearchDay(day.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Count(day.Turns)))],
                [.. overview.Sources.Select(static source => new LocalSearchSource(source.Family, Count(source.Sources), source.LastCheck, Count(source.Failed), source.LastError))],
                overview.Meaning is { } meaning ? new LocalSearchCoverage(Count(meaning.Coverage.Chunks), Count(meaning.Coverage.Embedded),
                    [.. meaning.Days.Select(static day => new LocalSearchDayCoverage(day.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Count(day.Coverage.Chunks), Count(day.Coverage.Embedded)))]) : null);
        }
        catch (Exception exception) when (Failure(exception, cancellationToken))
        {
            return LocalSearchOverviewResponse.Refused("read_failed");
        }
    }

    /// <summary>How search was used in the last 90 days: the searches of each day by who asked, those that found nothing, and the median times.</summary>
    [NeoRpcMethod("activity")]
    public async Task<LocalSearchActivityResponse> ActivityAsync(LocalSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return LocalSearchActivityResponse.Refused(refused);
        try
        {
            var summary = await _search!.Store.GetActivityAsync(DateTimeOffset.UtcNow - SearchIndexStore.ActivityRetention, cancellationToken).ConfigureAwait(false);
            static int? Milliseconds(double? value) => value is { } milliseconds ? (int)Math.Ceiling(milliseconds) : null;
            return new("ok", [.. summary.Days.Select(static day => new LocalSearchActivityDay(day.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    day.Window, day.Agent, day.ToolSearch, day.Other, day.Empty))],
                summary.Searches, summary.Empty, Milliseconds(summary.MedianWordsMilliseconds), Milliseconds(summary.MedianMeaningMilliseconds), Count(summary.Tokens));
        }
        catch (Exception exception) when (Failure(exception, cancellationToken))
        {
            return LocalSearchActivityResponse.Refused("read_failed");
        }
    }

    /// <summary>Lists what is indexed of one kind without a query, in the space the window shows: the newest first, then by title.</summary>
    [NeoRpcMethod("browse")]
    public async Task<LocalSearchQueryResponse> BrowseAsync(LocalSearchBrowseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, [], false, "off", 0);
        if (!SearchNames.TryParseKind(request.Kind ?? string.Empty, out var kind)) return new("invalid_request", [], false, "off", 0);
        if (!_search!.Host.GetSettings().Enabled) return new("disabled", [], false, "off", 0);
        try
        {
            var scope = await ScopeAsync(request.SpaceId, new HashSet<SearchKind> { kind }, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(request.ProjectId)) scope = scope with { ProjectId = request.ProjectId };
            var (hits, truncated) = await _search.Service.BrowseAsync(scope, Math.Clamp(request.Limit ?? 50, 1, MaximumHits), cancellationToken).ConfigureAwait(false);
            return new("ok", [.. hits.Select(static (hit, index) => Hit(hit, index))], truncated, "off", 0);
        }
        catch (Exception exception) when (Failure(exception, cancellationToken))
        {
            return new("read_failed", [], false, "off", 0);
        }
    }

    /// <summary>Saves the settings of search: on or off, the sessions, the projects and spaces kept out, the batches embedded at once.</summary>
    [NeoRpcMethod("saveSettings")]
    public LocalSearchSettingsResponse SaveSettings(LocalSearchSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null);
        if (request.Settings is not { } wanted) return new("invalid_request", null);
        try
        {
            var saved = _search!.Host.UpdateSettings(current => current with
            {
                Enabled = wanted.Enabled, Sessions = wanted.Sessions, Semantic = wanted.Semantic,
                ExcludedProjects = SearchSettings.Normalize(wanted.ExcludedProjects), ExcludedSpaces = SearchSettings.Normalize(wanted.ExcludedSpaces),
                // Search by meaning takes a change of it during the indexing (DesktopSearchMeaning.ApplyParallelism).
                Parallelism = Math.Clamp(wanted.Parallelism, 1, SearchSettings.MaximumParallelism(Environment.ProcessorCount)),
            });

            return new("ok", Settings(saved));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new("write_failed", null);
        }
    }

    /// <summary>Keeps a project or a space out of search, or lets it in again.</summary>
    [NeoRpcMethod("setExcluded")]
    public LocalSearchSettingsResponse SetExcluded(LocalSearchExcludeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null);
        if (request.Id is not { Length: > 0 and <= 128 } id || request.Kind is not ("project" or "space")) return new("invalid_request", null);
        try
        {
            IReadOnlyList<string> Toggle(IReadOnlyList<string> ids) => request.Excluded
                ? SearchSettings.Normalize([.. ids, id])
                : [.. ids.Where(other => !string.Equals(other, id, StringComparison.OrdinalIgnoreCase))];
            var saved = _search!.Host.UpdateSettings(current => request.Kind == "project"
                ? current with { ExcludedProjects = Toggle(current.ExcludedProjects) }
                : current with { ExcludedSpaces = Toggle(current.ExcludedSpaces) });

            return new("ok", Settings(saved));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new("write_failed", null);
        }
    }

    /// <summary>Reads the index of some kinds again, or everything.</summary>
    [NeoRpcMethod("reindex")]
    public LocalSearchStatusOnlyResponse Reindex(LocalSearchReindexRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        _search!.Indexer.SignalPass();
        return new("ok");
    }

    /// <summary>The commands of the window, as the page lists them: they are found by the search as well.</summary>
    [NeoRpcMethod("registerCommands")]
    public LocalSearchStatusOnlyResponse RegisterCommands(LocalSearchCommandsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (request.Commands is not { Count: <= MaximumCommands } commands) return new("invalid_request");
        var documents = new List<SearchDocument>(commands.Count);
        foreach (var command in commands)
        {
            if (command.Id is not { Length: > 0 and <= 200 } || string.IsNullOrWhiteSpace(command.Title)) continue;
            var text = string.Join("\n", new[] { command.Category, command.Description, command.Keywords, command.Shortcut }.Where(static value => !string.IsNullOrWhiteSpace(value)));
            documents.Add(new SearchDocument("command:" + command.Id, SearchKind.Command, Bounded(command.Title, 200), Bounded(text, 2000), DateTimeOffset.UnixEpoch)
            {
                Extra = DesktopSearchSources.Extra(("id", command.Id), ("shortcut", command.Shortcut ?? string.Empty)),
            });
        }

        _search!.Sources.WindowCommands = documents;
        _search.Indexer.Signal("commands");
        return new("ok");
    }

    /// <summary>Tells when the index or its settings changed: the page then asks for the status again.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<LocalSearchEvent> Watch(LocalSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchAsync(request, cancellationToken), DesktopJsonContext.Default.LocalSearchEvent);
    }

    internal async IAsyncEnumerable<LocalSearchEvent> WatchAsync(LocalSearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refuse(request.ExpectedEpoch) is not null) yield break;
        var notices = Channel.CreateBounded<int>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        void Notify() => notices.Writer.TryWrite(0);
        void SettingsChanged(SearchSettings before, SearchSettings after) => Notify();
        var search = _search!;
        search.Indexer.Changed += Notify;
        search.Indexer.StatusChanged += Notify;
        search.Host.SettingsChanged += SettingsChanged;
        var meaning = search.Meaning;
        if (meaning is not null) meaning.Changed += Notify;
        // A request to open the dialog of search by meaning is told with the next event, never dropped.
        var open = 0;
        void OpenMeaning()
        {
            Volatile.Write(ref open, 1);
            Notify();
        }

        search.MeaningDialogRequested += OpenMeaning;
        try
        {
            var revision = 0;
            yield return new LocalSearchEvent(revision, null);
            await foreach (var _ in notices.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return new LocalSearchEvent(++revision, Interlocked.Exchange(ref open, 0) == 1 ? "meaning" : null);
                // A busy indexer changes often: the page reads at most twice a second.
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            search.Indexer.Changed -= Notify;
            search.Indexer.StatusChanged -= Notify;
            search.Host.SettingsChanged -= SettingsChanged;
            if (meaning is not null) meaning.Changed -= Notify;
            search.MeaningDialogRequested -= OpenMeaning;
        }
    }

    private async Task<SearchScope> ScopeAsync(string? spaceId, IReadOnlySet<SearchKind>? kinds, CancellationToken cancellationToken)
    {
        var scope = new SearchScope { Kinds = kinds };
        var space = string.IsNullOrWhiteSpace(spaceId) ? SpaceDescriptor.DefaultId : spaceId;
        scope = scope with { SpaceId = space };
        if (!string.Equals(space, SpaceDescriptor.DefaultId, StringComparison.Ordinal))
            scope = scope with { Projects = await _spaceProjects!(space, cancellationToken).ConfigureAwait(false) };
        if (await _spaceFilter!(space, cancellationToken).ConfigureAwait(false) is { } filter)
        {
            scope = scope with
            {
                CatalogFilter = hit =>
                {
                    var extra = ReadExtra(hit.Extra);
                    return hit.Kind switch
                    {
                        SearchKind.Skill when extra.TryGetValue("name", out var name) => filter.Allows(SpaceResourceKind.Skill, name),
                        SearchKind.Mcp when extra.TryGetValue("server", out var server) => filter.Allows(SpaceResourceKind.McpServer, server),
                        SearchKind.Plugin when extra.TryGetValue("id", out var id) => filter.Allows(SpaceResourceKind.Plugin, id),
                        SearchKind.Setting when extra.TryGetValue("plugin", out var plugin) => filter.Allows(SpaceResourceKind.Plugin, plugin),
                        // A script of a project is in the spaces of its sessions; yours and the samples as the user assigned them.
                        SearchKind.Script when extra.TryGetValue("name", out var name) && !(extra.TryGetValue("scope", out var where) && where == "project")
                            => filter.Allows(SpaceResourceKind.Script, name),
                        _ => true,
                    };
                },
            };
        }

        return scope;
    }

    private static Dictionary<string, string> ReadExtra(string? extra)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(extra)) return values;
        try
        {
            using var document = JsonDocument.Parse(extra);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return values;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String) values[property.Name] = property.Value.GetString()!;
            }
        }
        catch (JsonException)
        {
        }

        return values;
    }

    // What the page shows of search by meaning: its state, its models, the download, and whether it is offered now.
    private LocalSearchMeaning MeaningOf(SearchSettings settings, SearchIndexCounts counts)
    {
        var meaning = _search!.Meaning;
        var installer = meaning?.Installer;
        var status = meaning?.Status;
        var download = installer?.Current ?? EmbeddingInstallSnapshotIdle;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var worthIt = DesktopSearch.IsMeaningWorthOffering(counts, today);
        var models = EmbeddingModelCatalog.Presets.Select(model => new LocalSearchModel(model.Id, model.Name, model.Author, model.Summary, model.License,
            Count((model.Size + 1023) / 1024), installer?.IsInstalled(model) == true)).ToList();
        return new(meaning is not null && installer!.Supported, settings.Semantic, status?.State ?? "off", settings.Model, Count(status?.Embedded ?? 0), Count(status?.Total ?? 0),
            status?.SecondsLeft, (int)Math.Round((status?.ChunksPerSecond ?? 0) * 10), status?.Error,
            new LocalSearchDownload(download.State, download.ModelId, Count((download.ReceivedBytes + 1023) / 1024), Count((download.TotalBytes + 1023) / 1024), download.Error),
            installer?.RuntimeLibrary() is not null, models, meaning is not null && worthIt && settings.MayOfferMeaning(today), Count(counts.Sessions));
    }

    private static readonly EmbeddingInstallSnapshot EmbeddingInstallSnapshotIdle = EmbeddingInstallSnapshot.Idle;

    private static LocalSearchHit Hit(SearchHit hit, int index) => new(
        SearchNames.Of(hit.Kind), hit.Ref, hit.Title, hit.Snippet, [.. hit.Marks.Select(static mark => new LocalSearchMark(mark.Start, mark.Length))],
        hit.At, Match(hit.Match), hit.WordsRank, hit.MeaningRank, index + 1, hit.SessionId, hit.ProjectId, hit.RunId, Role(hit.Role), hit.Extra);

    private static LocalSearchSettings Settings(SearchSettings settings)
        => new(settings.Enabled, settings.Sessions, settings.Semantic, settings.Model, settings.ExcludedProjects, settings.ExcludedSpaces,
            settings.EffectiveParallelism(Environment.ProcessorCount), SearchSettings.MaximumParallelism(Environment.ProcessorCount));

    private static string Match(SearchMatch match) => match switch { SearchMatch.Meaning => "meaning", SearchMatch.Both => "both", _ => "words" };

    private static string? Role(SearchRole role) => role switch { SearchRole.User => "user", SearchRole.Assistant => "assistant", SearchRole.Agent => "agent", _ => null };

    private static string Meaning(SearchMeaningState state) => state switch
    {
        SearchMeaningState.Ready => "ready", SearchMeaningState.Indexing => "indexing", SearchMeaningState.Warming => "warming", _ => "off",
    };

    private static int Count(long value) => (int)Math.Min(value, int.MaxValue);

    private static string Bounded(string text, int length) => text.Length <= length ? text : text[..length];

    private string? Refuse(string? expectedEpoch)
        => _search is null ? "unavailable" : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";

    private static bool Failure(Exception exception, CancellationToken cancellationToken)
        => exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidDataException or JsonException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);
}

internal sealed record LocalSearchRequest(string? ExpectedEpoch);

/// <param name="Typing">Whether the last word is being typed: it then matches the start of words.</param>
/// <param name="Mode"><c>auto</c>, <c>words</c> or <c>meaning</c>.</param>
internal sealed record LocalSearchQueryRequest(string? ExpectedEpoch, string? Query, IReadOnlyList<string>? Kinds, string? SpaceId, string? ProjectId,
    int? Limit, bool Typing, string? Mode);

internal sealed record LocalSearchMark(int Start, int Length);

internal sealed record LocalSearchHit(string Kind, string Ref, string Title, string Snippet, IReadOnlyList<LocalSearchMark> Marks, DateTimeOffset At,
    string Match, int? WordsRank, int? MeaningRank, int Rank, string? SessionId, string? ProjectId, string? RunId, string? Role, string? Extra);

internal sealed record LocalSearchQueryResponse(string Status, IReadOnlyList<LocalSearchHit> Hits, bool Truncated, string Meaning, int Milliseconds);

internal sealed record LocalSearchRefRequest(string? ExpectedEpoch, string? Ref);

internal sealed record LocalSearchLocateResponse(string Status, string? SessionId, string? ProjectId, string? Offset);

/// <param name="Parallelism">The batches search by meaning embeds at once, each in a worker of its own, as this computer runs them.</param>
/// <param name="MaximumParallelism">The most on this computer: half of its cores. Ignored when saved.</param>
internal sealed record LocalSearchSettings(bool Enabled, bool Sessions, bool Semantic, string Model, IReadOnlyList<string> ExcludedProjects, IReadOnlyList<string> ExcludedSpaces,
    int Parallelism, int MaximumParallelism);

internal sealed record LocalSearchDay(string Day, int Turns);

/// <param name="Supported">Whether this window has search by meaning: an ONNX Runtime exists for its platform.</param>
/// <param name="Enabled">Whether it is turned on.</param>
/// <param name="State"><c>off</c>, <c>downloading</c>, <c>loading</c>, <c>indexing</c>, <c>paused</c>, <c>ready</c> or <c>failed</c>.</param>
/// <param name="ChunksPerSecondTenths">The speed of the indexing, in tenths of chunks per second.</param>
/// <param name="RuntimeInstalled">Whether the ONNX Runtime is on disk (speech may have brought it).</param>
/// <param name="Offer">Whether it is offered now: there is enough to search, and the offer was not refused nor put off.</param>
/// <param name="Sessions">The sessions indexed, for the estimates of the dialog.</param>
internal sealed record LocalSearchMeaning(bool Supported, bool Enabled, string State, string ModelId, int Embedded, int Total, int? SecondsLeft,
    int ChunksPerSecondTenths, string? Error, LocalSearchDownload Download, bool RuntimeInstalled, IReadOnlyList<LocalSearchModel> Models, bool Offer, int Sessions);

internal sealed record LocalSearchDownload(string State, string? ModelId, int ReceivedKilobytes, int TotalKilobytes, string? Error);

internal sealed record LocalSearchModel(string Id, string Name, string Author, string Summary, string License, int Kilobytes, bool Installed);

internal sealed record LocalSearchModelRequest(string? ExpectedEpoch, string? ModelId);

internal sealed record LocalSearchPauseRequest(string? ExpectedEpoch, bool Paused);

/// <param name="Answer"><c>later</c> or <c>never</c>.</param>
internal sealed record LocalSearchOfferRequest(string? ExpectedEpoch, string? Answer);

internal sealed record LocalSearchStatusResponse(string Status, LocalSearchSettings? Settings, LocalSearchMeaning? Semantic, bool Busy, bool Ready, int JournalsDone, int JournalsTotal,
    DateTimeOffset? LastPass, string? LastError, string Meaning, int CoveragePercent, IReadOnlyDictionary<string, int> Items, int Chunks, int Sessions, int Kilobytes,
    IReadOnlyDictionary<string, int> Projects, IReadOnlyList<LocalSearchDay> Days)
{
    internal static LocalSearchStatusResponse Refused(string status)
        => new(status, null, null, false, false, 0, 0, null, null, "off", 0, new Dictionary<string, int>(), 0, 0, 0, new Dictionary<string, int>(), []);
}

internal sealed record LocalSearchOverviewRequest(string? ExpectedEpoch, string? SpaceId);

/// <param name="Family">The start of the keys of the sources: <c>journal</c>, <c>plans</c>, <c>skills</c>, <c>docs</c>…</param>
internal sealed record LocalSearchSource(string Family, int Sources, DateTimeOffset? LastCheck, int Failed, string? LastError);

/// <param name="Recent">The items of the content kinds written in the last 7 days.</param>
/// <param name="Kilobytes">The size of the whole index on disk.</param>
internal sealed record LocalSearchOverviewResponse(string Status, IReadOnlyDictionary<string, int> Items, int Chunks, int Sessions, int Recent, int Kilobytes,
    IReadOnlyDictionary<string, int> Projects, IReadOnlyList<LocalSearchDay> Days, IReadOnlyList<LocalSearchSource> Sources, LocalSearchCoverage? Meaning)
{
    internal static LocalSearchOverviewResponse Refused(string status)
        => new(status, new Dictionary<string, int>(), 0, 0, 0, 0, new Dictionary<string, int>(), [], [], null);
}

/// <summary>How much of what a space sees is searchable by meaning.</summary>
/// <param name="Chunks">The passages of the space.</param>
/// <param name="Embedded">Those with a vector in the active set.</param>
/// <param name="Days">The passages of the messages of each day, the newest first, and those with a vector.</param>
internal sealed record LocalSearchCoverage(int Chunks, int Embedded, IReadOnlyList<LocalSearchDayCoverage> Days);

internal sealed record LocalSearchDayCoverage(string Day, int Chunks, int Embedded);

/// <param name="Day">The local day, <c>yyyy-MM-dd</c>.</param>
/// <param name="Empty">The searches of the day that found nothing.</param>
internal sealed record LocalSearchActivityDay(string Day, int Window, int Agent, int ToolSearch, int Other, int Empty);

/// <param name="Days">The days with searches, the oldest first, in the last 90 days.</param>
/// <param name="MedianWordsMilliseconds">The median time of a search by words, rounded up, or null without searches.</param>
/// <param name="MedianMeaningMilliseconds">The median time of a search by meaning, rounded up, or null without one.</param>
/// <param name="Tokens">The tokens tool search kept out of prompts, estimated.</param>
internal sealed record LocalSearchActivityResponse(string Status, IReadOnlyList<LocalSearchActivityDay> Days, int Searches, int Empty, int? MedianWordsMilliseconds,
    int? MedianMeaningMilliseconds, int Tokens)
{
    internal static LocalSearchActivityResponse Refused(string status) => new(status, [], 0, 0, null, null, 0);
}

internal sealed record LocalSearchBrowseRequest(string? ExpectedEpoch, string? Kind, string? SpaceId, string? ProjectId, int? Limit);

internal sealed record LocalSearchSaveRequest(string? ExpectedEpoch, LocalSearchSettings? Settings);

internal sealed record LocalSearchSettingsResponse(string Status, LocalSearchSettings? Settings);

/// <param name="Kind"><c>project</c> or <c>space</c>.</param>
internal sealed record LocalSearchExcludeRequest(string? ExpectedEpoch, string? Kind, string? Id, bool Excluded);

internal sealed record LocalSearchReindexRequest(string? ExpectedEpoch, IReadOnlyList<string>? Kinds);

internal sealed record LocalSearchStatusOnlyResponse(string Status);

internal sealed record LocalSearchCommand(string? Id, string? Title, string? Category, string? Description, string? Keywords, string? Shortcut);

internal sealed record LocalSearchCommandsRequest(string? ExpectedEpoch, IReadOnlyList<LocalSearchCommand>? Commands);

/// <param name="Open">What the window is asked to open with this event: <c>meaning</c> for the dialog of search by meaning, or null.</param>
internal sealed record LocalSearchEvent(int Revision, string? Open);
