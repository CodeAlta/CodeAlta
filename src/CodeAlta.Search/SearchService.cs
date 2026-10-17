using System.Diagnostics;
using System.Text.Json;
using CodeAlta.Agent;

namespace CodeAlta.Search;

/// <summary>Search by meaning, as the query service uses it.</summary>
public interface ISearchMeaning
{
    /// <summary>Gets its state.</summary>
    SearchMeaningState State { get; }

    /// <summary>Gets the share of chunks that have a vector, from 0 to 1.</summary>
    double Coverage { get; }

    /// <summary>Finds the chunks closest to a query in a scope, the best first.</summary>
    /// <param name="query">The query.</param>
    /// <param name="scope">The scope.</param>
    /// <param name="codeQuery">True when the query looks for tools or code.</param>
    /// <param name="limit">The most chunks.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The chunks and their similarity; empty when it cannot answer now.</returns>
    ValueTask<IReadOnlyList<(long ChunkId, float Similarity)>> SearchAsync(string query, SearchScope scope, bool codeQuery, int limit, CancellationToken cancellationToken);
}

/// <summary>A turn of a session, as <c>alta search show</c> gives it.</summary>
/// <param name="RunId">Its run.</param>
/// <param name="At">When it started.</param>
/// <param name="Messages">Its prompt and final answer; with <c>full</c>, every message of the turn.</param>
public sealed record SearchTurn(string? RunId, DateTimeOffset At, IReadOnlyList<SearchTurnMessage> Messages);

/// <summary>A message of a turn.</summary>
/// <param name="Role">Who wrote it.</param>
/// <param name="ContentId">Its content id.</param>
/// <param name="Text">Its text.</param>
/// <param name="At">When.</param>
public sealed record SearchTurnMessage(SearchRole Role, string ContentId, string Text, DateTimeOffset At);

/// <summary>A hit read whole.</summary>
public sealed record SearchShowResult
{
    /// <summary>Gets the item.</summary>
    public required SearchHit Hit { get; init; }

    /// <summary>Gets its whole indexed text.</summary>
    public required string Text { get; init; }

    /// <summary>Gets, for a message, its turn and the turns around it; empty otherwise.</summary>
    public IReadOnlyList<SearchTurn> Turns { get; init; } = [];

    /// <summary>Gets the index of the turn of the hit in <see cref="Turns"/>, or -1.</summary>
    public int HitTurn { get; init; } = -1;
}

/// <summary>What the index knows of an item, without its text.</summary>
/// <param name="Ref">Its ref.</param>
/// <param name="Kind">Its kind.</param>
/// <param name="Title">Its title.</param>
/// <param name="At">When it was written.</param>
public sealed record SearchItemInfo(string Ref, SearchKind Kind, string Title, DateTimeOffset At)
{
    /// <summary>Gets its space, or null.</summary>
    public string? SpaceId { get; init; }

    /// <summary>Gets its project, or null.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets its session, for a message, a session or notes.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets the run of a message.</summary>
    public string? RunId { get; init; }

    /// <summary>Gets who wrote a message.</summary>
    public SearchRole Role { get; init; }

    /// <summary>Gets what else it says, as a JSON object.</summary>
    public string? Extra { get; init; }
}

/// <summary>Where search is, for <c>alta search status</c>, the settings page and the canvas.</summary>
public sealed record SearchStatus
{
    /// <summary>Gets whether the index is on.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets the indexer.</summary>
    public required SearchIndexerStatus Indexer { get; init; }

    /// <summary>Gets what the index holds.</summary>
    public required SearchIndexCounts Counts { get; init; }

    /// <summary>Gets the state of search by meaning.</summary>
    public SearchMeaningState Meaning { get; init; }

    /// <summary>Gets the share of chunks that have a vector.</summary>
    public double Coverage { get; init; }
}

/// <summary>What a scope of the index holds, for the canvas of a space.</summary>
public sealed record SearchOverview
{
    /// <summary>Gets what the scope holds; the catalog kinds counted as the space sees them.</summary>
    public required SearchIndexCounts Counts { get; init; }

    /// <summary>Gets the sources by family.</summary>
    public required IReadOnlyList<SearchSourceSummary> Sources { get; init; }

    /// <summary>Gets how much of the scope is searchable by meaning, or null while search by meaning is off or has no vectors yet.</summary>
    public SearchScopeCoverage? Meaning { get; init; }
}

/// <summary>The queries of the search index: by words, by meaning, and both merged by rank.</summary>
public sealed class SearchService
{
    private const int CandidatesPerList = 100;
    private const int HitsPerSession = 3;
    private const double FusionK = 60;

    private readonly SearchIndexStore _store;
    private readonly SearchIndexer? _indexer;
    private readonly ISearchHost _host;
    private readonly ISessionJournalCatalog? _journals;

    /// <summary>Creates the service.</summary>
    /// <param name="store">The index.</param>
    /// <param name="indexer">The indexer, for the check before a query, or null.</param>
    /// <param name="host">The application.</param>
    /// <param name="journals">The journals, to read the turns of a message, or null.</param>
    public SearchService(SearchIndexStore store, SearchIndexer? indexer, ISearchHost host, ISessionJournalCatalog? journals)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _indexer = indexer;
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _journals = journals;
    }

    /// <summary>Gets or sets search by meaning, once it is on; null for words only.</summary>
    public ISearchMeaning? Meaning { get; set; }

    /// <summary>Gets or sets what records a query in the activity, or null.</summary>
    public Func<SearchRequest, SearchResponse, ValueTask>? Recorded { get; set; }

    /// <summary>Answers a query.</summary>
    /// <param name="request">The query.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The answer.</returns>
    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = _host.GetSettings();
        var meaning = Meaning;
        var meaningState = settings.Semantic ? meaning?.State ?? SearchMeaningState.NotInstalled : SearchMeaningState.Off;
        if (!settings.Enabled || string.IsNullOrWhiteSpace(request.Query))
        {
            return new SearchResponse { Hits = [], Mode = SearchMode.Keyword, Meaning = meaningState };
        }

        var scope = request.Scope with
        {
            ExcludedProjects = Union(request.Scope.ExcludedProjects, settings.ExcludedProjects),
            ExcludedSpaces = Union(request.Scope.ExcludedSpaces, settings.ExcludedSpaces),
        };
        if (_indexer is not null)
        {
            // The sources that are files or lists (the catalog, and the plans and tasks of the projects); the journals
            // are signaled as they are written.
            var sourceKinds = scope.Kinds is null ? null : scope.Kinds.Where(static kind => kind is not (SearchKind.Message or SearchKind.Session or SearchKind.Note)).ToHashSet();
            if (sourceKinds is null || sourceKinds.Count > 0)
                await _indexer.EnsureFreshAsync(sourceKinds, TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }

        var limit = Math.Clamp(request.Limit, 1, 200);
        var started = Stopwatch.GetTimestamp();
        var words = new List<WordsCandidate>();
        if (request.Mode != SearchMode.Meaning || meaningState is not (SearchMeaningState.Ready or SearchMeaningState.Indexing))
        {
            if (SearchText.BuildMatch(request.Query, any: false, request.PrefixLastTerm) is { } strict)
                words.AddRange(await _store.SearchWordsAsync(strict, scope, CandidatesPerList, cancellationToken).ConfigureAwait(false));
            if (words.Count < limit && SearchText.Terms(request.Query).Count > 1 && SearchText.BuildMatch(request.Query, any: true, request.PrefixLastTerm) is { } relaxed)
            {
                var seen = words.Select(static candidate => candidate.ChunkId).ToHashSet();
                foreach (var candidate in await _store.SearchWordsAsync(relaxed, scope, CandidatesPerList, cancellationToken).ConfigureAwait(false))
                {
                    if (seen.Add(candidate.ChunkId)) words.Add(candidate);
                }
            }
        }

        var wordsMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        IReadOnlyList<(long ChunkId, float Similarity)> closest = [];
        var meaningMilliseconds = 0.0;
        if (request.Mode != SearchMode.Keyword && meaning is not null && meaningState is SearchMeaningState.Ready or SearchMeaningState.Indexing)
        {
            var meaningStarted = Stopwatch.GetTimestamp();
            var codeQuery = scope.Kinds is { } kinds && kinds.All(static kind => kind is SearchKind.Tool or SearchKind.Mcp or SearchKind.Skill);
            closest = await meaning.SearchAsync(request.Query, scope, codeQuery, CandidatesPerList, cancellationToken).ConfigureAwait(false);
            meaningMilliseconds = Stopwatch.GetElapsedTime(meaningStarted).TotalMilliseconds;
            if (closest.Count == 0 && meaning.State == SearchMeaningState.Warming) meaningState = SearchMeaningState.Warming;
        }

        var hits = await FuseAsync(request, scope, words, closest, limit, cancellationToken).ConfigureAwait(false);
        var response = new SearchResponse
        {
            Hits = hits.Hits,
            Truncated = hits.Truncated,
            Mode = closest.Count > 0 ? SearchMode.Auto : SearchMode.Keyword,
            Meaning = meaningState,
            Coverage = meaning?.Coverage ?? 0,
            WordsMilliseconds = wordsMilliseconds,
            MeaningMilliseconds = meaningMilliseconds,
        };
        if (Recorded is { } recorded)
        {
            try { await recorded(request, response).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException) { }
        }

        return response;
    }

    private static IReadOnlySet<string>? Union(IReadOnlySet<string>? first, IReadOnlyList<string> second)
    {
        if (second.Count == 0) return first;
        var set = new HashSet<string>(second, StringComparer.OrdinalIgnoreCase);
        if (first is not null) set.UnionWith(first);
        return set;
    }

    private async Task<(IReadOnlyList<SearchHit> Hits, bool Truncated)> FuseAsync(SearchRequest request, SearchScope scope, IReadOnlyList<WordsCandidate> words,
        IReadOnlyList<(long ChunkId, float Similarity)> closest, int limit, CancellationToken cancellationToken)
    {
        // Reciprocal rank fusion: no calibration is needed between a bm25 score and a similarity.
        var chunks = new Dictionary<long, Fused>();
        for (var rank = 0; rank < words.Count; rank++)
        {
            var candidate = words[rank];
            var fused = Get(candidate.ChunkId);
            fused.Score += 1 / (FusionK + rank + 1);
            fused.WordsRank ??= rank + 1;
            fused.ItemId = candidate.ItemId;
            fused.Snippet ??= candidate.Snippet;
        }

        for (var rank = 0; rank < closest.Count; rank++)
        {
            var fused = Get(closest[rank].ChunkId);
            fused.Score += 1 / (FusionK + rank + 1);
            fused.MeaningRank ??= rank + 1;
        }

        var missing = chunks.Values.Where(static fused => fused.ItemId == 0).Select(static fused => fused.ChunkId).ToList();
        var texts = await _store.GetChunksAsync(missing.Concat(chunks.Values.Where(static fused => fused.Snippet is null).Select(static fused => fused.ChunkId)).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
        foreach (var fused in chunks.Values)
        {
            if (texts.TryGetValue(fused.ChunkId, out var chunk))
            {
                if (fused.ItemId == 0) fused.ItemId = chunk.ItemId;
                fused.Text = chunk.Text;
            }
        }

        // Chunks fold into their item, which keeps its best chunk.
        var byItem = new Dictionary<long, Fused>();
        foreach (var fused in chunks.Values.Where(static fused => fused.ItemId != 0))
        {
            if (!byItem.TryGetValue(fused.ItemId, out var best))
            {
                fused.BestChunkScore = fused.Score;
                byItem[fused.ItemId] = fused;
                continue;
            }

            best.WordsRank = Min(best.WordsRank, fused.WordsRank);
            best.MeaningRank = Min(best.MeaningRank, fused.MeaningRank);
            if (fused.Score > best.BestChunkScore)
            {
                best.BestChunkScore = fused.Score;
                best.Snippet = fused.Snippet ?? best.Snippet;
                best.Text = fused.Text ?? best.Text;
            }

            best.Score = Math.Max(best.Score, fused.Score) + fused.Score * 0.1;
        }

        var items = await _store.GetItemsAsync(byItem.Keys, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var ranked = new List<SearchHit>();
        foreach (var (itemId, fused) in byItem)
        {
            if (!items.TryGetValue(itemId, out var item)) continue;
            var score = fused.Score;
            // A title that has the words of the query is what was looked for (a tool, a skill, a page): it counts as a
            // first rank by words, and a title that has some of them as part of one.
            if (fused.WordsRank is not null)
            {
                var coverage = SearchText.TitleCoverage(request.Query, item.Title, request.PrefixLastTerm);
                score += coverage * coverage / (FusionK + 1);
            }

            if (request.Recent) score /= 1 + Math.Max(0, (now - item.At).TotalDays) / 30;
            var (snippet, marks) = fused.Snippet is { } raw ? SearchText.ReadSnippet(raw) : (SearchText.Window(fused.Text ?? "", request.Query), Array.Empty<SearchMark>());
            var hit = new SearchHit
            {
                Kind = item.Kind, Ref = item.Ref, Title = item.Title, Snippet = snippet, Marks = marks, At = item.At, Score = score,
                Match = fused.WordsRank is null ? SearchMatch.Meaning : fused.MeaningRank is null ? SearchMatch.Words : SearchMatch.Both,
                WordsRank = fused.WordsRank, MeaningRank = fused.MeaningRank, SpaceId = item.SpaceId, ProjectId = item.ProjectId,
                SessionId = item.SessionId, RunId = item.RunId, Role = item.Role, Extra = item.Extra,
            };
            if (SearchNames.CatalogKinds.Contains(hit.Kind) && scope.CatalogFilter is { } filter && !filter(hit)) continue;
            ranked.Add(hit);
        }

        ranked.Sort(static (left, right) => right.Score.CompareTo(left.Score));
        // At most a few hits of one session, unless the query is about that session.
        var perSession = new Dictionary<string, int>(StringComparer.Ordinal);
        var kept = new List<SearchHit>(ranked.Count);
        foreach (var hit in ranked)
        {
            if (scope.SessionId is null && hit.SessionId is { } session)
            {
                perSession.TryGetValue(session, out var count);
                if (count >= HitsPerSession) continue;
                perSession[session] = count + 1;
            }

            kept.Add(hit);
        }

        // Several kinds share the limit: each gets a part before the rest is filled by score.
        var kinds = kept.Select(static hit => hit.Kind).Distinct().Count();
        IReadOnlyList<SearchHit> result;
        if (kinds > 1 && (scope.Kinds is null || scope.Kinds.Count > 1) && kept.Count > limit)
        {
            var quota = Math.Max(1, limit / kinds);
            var chosen = new List<SearchHit>(limit);
            var taken = new Dictionary<SearchKind, int>();
            foreach (var hit in kept)
            {
                taken.TryGetValue(hit.Kind, out var count);
                if (count >= quota) continue;
                taken[hit.Kind] = count + 1;
                chosen.Add(hit);
                if (chosen.Count == limit) break;
            }

            foreach (var hit in kept)
            {
                if (chosen.Count >= limit) break;
                if (!chosen.Contains(hit)) chosen.Add(hit);
            }

            chosen.Sort(static (left, right) => right.Score.CompareTo(left.Score));
            result = chosen;
        }
        else
        {
            result = kept.Take(limit).ToList();
        }

        return (result, kept.Count > result.Count);

        Fused Get(long chunkId)
        {
            if (!chunks.TryGetValue(chunkId, out var fused)) chunks[chunkId] = fused = new Fused { ChunkId = chunkId };
            return fused;
        }

        static int? Min(int? left, int? right) => left is null ? right : right is null ? left : Math.Min(left.Value, right.Value);
    }

    /// <summary>Reads a hit whole: its text, and for a message its turn with <paramref name="context"/> turns before and after.</summary>
    /// <param name="reference">The ref of the hit.</param>
    /// <param name="context">The turns before and after.</param>
    /// <param name="full">True for every message of the turns, read from the journal.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The hit, or null when the index does not hold it.</returns>
    public async Task<SearchShowResult?> ShowAsync(string reference, int context, bool full, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        if (await _store.GetItemAsync(reference, cancellationToken).ConfigureAwait(false) is not { } found) return null;
        var (item, text) = found;
        var hit = new SearchHit
        {
            Kind = item.Kind, Ref = item.Ref, Title = item.Title, Snippet = SearchText.Window(text, "", 200), At = item.At, SpaceId = item.SpaceId,
            ProjectId = item.ProjectId, SessionId = item.SessionId, RunId = item.RunId, Role = item.Role, Extra = item.Extra,
        };
        if (item.Kind != SearchKind.Message || item.SessionId is null || _journals is null) return new SearchShowResult { Hit = hit, Text = text };
        var turns = await ReadTurnsAsync(item.SessionId, full, cancellationToken).ConfigureAwait(false);
        var index = turns.FindIndex(turn => turn.RunId is not null && turn.RunId == item.RunId);
        if (index < 0) index = turns.FindIndex(turn => turn.Messages.Any(message => reference.EndsWith("/" + message.ContentId, StringComparison.Ordinal)));
        if (index < 0) return new SearchShowResult { Hit = hit, Text = text };
        context = Math.Clamp(context, 0, 10);
        var start = Math.Max(0, index - context);
        var end = Math.Min(turns.Count - 1, index + context);
        return new SearchShowResult { Hit = hit, Text = text, Turns = turns.GetRange(start, end - start + 1), HitTurn = index - start };
    }

    private async Task<List<SearchTurn>> ReadTurnsAsync(string sessionId, bool full, CancellationToken cancellationToken)
    {
        var turns = new List<SearchTurn>();
        var stream = await _journals!.OpenAsync(sessionId, 0, cancellationToken).ConfigureAwait(false);
        if (stream is null) return turns;
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream);
            string? openRun = null;
            List<SearchTurnMessage>? messages = null;
            var at = DateTimeOffset.MinValue;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                var user = line.StartsWith("{\"$type\":\"contentCompleted\",\"kind\":\"User\"", StringComparison.Ordinal);
                var assistant = line.StartsWith("{\"$type\":\"contentCompleted\",\"kind\":\"Assistant\"", StringComparison.Ordinal);
                var idle = line.StartsWith("{\"$type\":\"sessionUpdate\",\"kind\":\"Idle\"", StringComparison.Ordinal);
                if (!user && !assistant && !idle) continue;
                JsonElement root;
                try { root = JsonDocument.Parse(line).RootElement; }
                catch (JsonException) { continue; }
                var runId = root.TryGetProperty("runId", out var run) && run.ValueKind == JsonValueKind.String ? run.GetString() : null;
                var time = root.TryGetProperty("timestamp", out var stamp) && stamp.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(stamp.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : at;
                if (idle)
                {
                    Close();
                    continue;
                }

                var content = root.TryGetProperty("content", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                var contentId = root.TryGetProperty("contentId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "";
                if (user)
                {
                    if (runId is not null && runId == openRun)
                    {
                        if (full) messages!.Add(new(root.TryGetProperty("source_session_id", out _) ? SearchRole.Agent : SearchRole.User, contentId, content, time));
                        continue;
                    }

                    Close();
                    openRun = runId;
                    at = time;
                    messages = [new(root.TryGetProperty("source_session_id", out _) ? SearchRole.Agent : SearchRole.User, contentId, content, time)];
                }
                else if (messages is not null && content.Trim().Length > 0)
                {
                    if (!full && messages.Count > 1 && messages[^1].Role == SearchRole.Assistant) messages[^1] = new(SearchRole.Assistant, contentId, content, time);
                    else messages.Add(new(SearchRole.Assistant, contentId, content, time));
                }
            }

            Close();

            void Close()
            {
                if (messages is { Count: > 0 }) turns.Add(new SearchTurn(openRun, at, messages));
                messages = null;
                openRun = null;
            }
        }

        return turns;
    }

    /// <summary>Gets what the index knows of an item, without its text: its session, its turn and its role for a message.</summary>
    /// <param name="reference">The ref of the item.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The item, or null when the index has no such item.</returns>
    public async Task<SearchItemInfo?> FindAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        if (await _store.GetItemAsync(reference, cancellationToken).ConfigureAwait(false) is not { } found) return null;
        var item = found.Item;
        return new SearchItemInfo(item.Ref, item.Kind, item.Title, item.At)
        {
            SpaceId = item.SpaceId, ProjectId = item.ProjectId, SessionId = item.SessionId, RunId = item.RunId, Role = item.Role, Extra = item.Extra,
        };
    }

    /// <summary>Gets where search is.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The status.</returns>
    public async Task<SearchStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var settings = _host.GetSettings();
        var meaning = Meaning;
        return new SearchStatus
        {
            Enabled = settings.Enabled,
            Indexer = _indexer?.Status ?? new SearchIndexerStatus(),
            Counts = await _store.CountAsync(cancellationToken).ConfigureAwait(false),
            Meaning = settings.Semantic ? meaning?.State ?? SearchMeaningState.NotInstalled : SearchMeaningState.Off,
            Coverage = meaning?.Coverage ?? 0,
        };
    }

    /// <summary>Gets what a scope holds and how its sources are read: the overview of the Search canvas.</summary>
    /// <param name="scope">The scope, such as what a space can see; the projects and spaces kept out of search are kept out of it too.</param>
    /// <param name="since">The time from which the items of the content kinds count as recent, or null.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The overview.</returns>
    public async Task<SearchOverview> GetOverviewAsync(SearchScope scope, DateTimeOffset? since, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope = WithSettings(scope);
        var counts = await _store.CountAsync(scope, since, cancellationToken).ConfigureAwait(false);
        if (scope.CatalogFilter is { } filter)
        {
            // What a space lets its sessions see is decided item by item: the few items of the catalog are listed and counted here.
            var items = new Dictionary<SearchKind, long>(counts.Items);
            foreach (var kind in CatalogKindsOfSpace)
            {
                if (!scope.Includes(kind) || !items.ContainsKey(kind)) continue;
                var listed = await _store.ListItemsAsync(scope with { Kinds = new HashSet<SearchKind> { kind } }, MaximumCatalogItems, cancellationToken).ConfigureAwait(false);
                items[kind] = listed.Count(entry => filter(HitOf(entry.Item, string.Empty)));
            }

            counts = counts with { Items = items };
        }

        // What is searchable by meaning, in the active set of vectors, while search by meaning is on.
        SearchScopeCoverage? meaning = null;
        if (_host.GetSettings().Semantic && await _store.GetActiveVectorSetAsync(cancellationToken).ConfigureAwait(false) is { } set)
            meaning = await _store.GetVectorCoverageAsync(set.Id, scope, cancellationToken).ConfigureAwait(false);
        return new SearchOverview { Counts = counts, Sources = await _store.SummarizeSourcesAsync(cancellationToken).ConfigureAwait(false), Meaning = meaning };
    }

    /// <summary>Lists what a scope holds without a query, the newest first and then by title: what is indexed, kind by kind.</summary>
    /// <param name="scope">The scope; the projects and spaces kept out of search are kept out of it too.</param>
    /// <param name="limit">The most items, from 1 to 200.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The items as hits with no rank, their snippet the start of their text, and whether there are more.</returns>
    public async Task<(IReadOnlyList<SearchHit> Hits, bool Truncated)> BrowseAsync(SearchScope scope, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!_host.GetSettings().Enabled) return ([], false);
        scope = WithSettings(scope);
        limit = Math.Clamp(limit, 1, 200);
        var filter = scope.CatalogFilter;
        // The catalog filter is applied here: more is read than shown when it may take some away.
        var listed = await _store.ListItemsAsync(scope, filter is null ? limit + 1 : MaximumCatalogItems, cancellationToken).ConfigureAwait(false);
        var hits = new List<SearchHit>(Math.Min(listed.Count, limit + 1));
        foreach (var (item, text) in listed)
        {
            var hit = HitOf(item, text);
            if (filter is not null && SearchNames.CatalogKinds.Contains(hit.Kind) && !filter(hit)) continue;
            hits.Add(hit);
            if (hits.Count > limit) break;
        }

        return hits.Count > limit ? (hits[..limit], true) : (hits, false);
    }

    // The kinds whose items a space lets its sessions see or not.
    private static readonly SearchKind[] CatalogKindsOfSpace = [SearchKind.Skill, SearchKind.Mcp, SearchKind.Plugin];

    private const int MaximumCatalogItems = 5000;

    private SearchScope WithSettings(SearchScope scope)
    {
        var settings = _host.GetSettings();
        return scope with
        {
            ExcludedProjects = Union(scope.ExcludedProjects, settings.ExcludedProjects),
            ExcludedSpaces = Union(scope.ExcludedSpaces, settings.ExcludedSpaces),
        };
    }

    private static SearchHit HitOf(IndexedItem item, string text) => new()
    {
        Kind = item.Kind, Ref = item.Ref, Title = item.Title, Snippet = SearchText.Window(text, string.Empty), At = item.At,
        SpaceId = item.SpaceId, ProjectId = item.ProjectId, SessionId = item.SessionId, RunId = item.RunId, Role = item.Role, Extra = item.Extra,
    };

    private sealed class Fused
    {
        public long ChunkId { get; init; }
        public long ItemId { get; set; }
        public double Score { get; set; }
        public double BestChunkScore { get; set; }
        public int? WordsRank { get; set; }
        public int? MeaningRank { get; set; }
        public string? Snippet { get; set; }
        public string? Text { get; set; }
    }
}
