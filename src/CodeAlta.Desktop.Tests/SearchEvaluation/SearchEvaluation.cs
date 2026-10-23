using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CodeAlta.Catalog.Search;
using CodeAlta.Desktop.Search;
using CodeAlta.Desktop.Search.Embedding;
using CodeAlta.Search;

namespace CodeAlta.Desktop.Tests.SearchEvaluation;

/// <summary>A query of an evaluation set and the refs that answer it.</summary>
/// <param name="Query">The query, as a user types it.</param>
/// <param name="Type"><c>exact</c>, <c>paraphrase</c>, <c>cross</c>, or what a private set says; <c>other</c> when it says nothing.</param>
/// <param name="Relevant">The refs of the items that answer it.</param>
internal sealed record EvaluationQuery(string Query, string Type, IReadOnlyList<string> Relevant);

/// <summary>The quality of one way of answering over a set of queries.</summary>
/// <param name="Queries">The queries.</param>
/// <param name="Recall">The mean share of the relevant refs found in the first 10 hits.</param>
/// <param name="Mrr">The mean of 1 / the rank of the first relevant hit, 0 beyond 10.</param>
/// <param name="MedianMilliseconds">The median time of a query.</param>
internal sealed record EvaluationScore(int Queries, double Recall, double Mrr, double MedianMilliseconds);

/// <summary>The files of the evaluation of search: the synthetic corpus and its queries, and a private set.</summary>
internal static class EvaluationData
{
    /// <summary>The space of the synthetic sessions.</summary>
    private const string Space = "default";

    /// <summary>Gets the folder of the committed corpus, next to the tests.</summary>
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "SearchEvaluation");

    /// <summary>
    /// Reads the corpus as the index holds it: the prompt and the answer of each turn as messages (<c>msg:&lt;session&gt;/&lt;n&gt;p</c>
    /// and <c>&lt;n&gt;a</c>), each session with its first prompt (<c>session:&lt;id&gt;</c>), and the docs, tools and skills with their refs.
    /// The fields are those the journal reader gives: the title of the session on its messages, and its project in the title embedded.
    /// </summary>
    public static IReadOnlyList<SearchDocument> ReadCorpus(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        var documents = new List<SearchDocument>();
        foreach (var session in root.GetProperty("sessions").EnumerateArray())
        {
            var id = session.GetProperty("id").GetString()!;
            var title = session.GetProperty("title").GetString()!;
            var project = session.GetProperty("project").GetString()!;
            var at = DateTimeOffset.Parse(session.GetProperty("at").GetString()!, CultureInfo.InvariantCulture);
            var embeddingTitle = $"{title} · {project}";
            var turn = 0;
            string? first = null;
            foreach (var element in session.GetProperty("turns").EnumerateArray())
            {
                turn++;
                var prompt = element.GetProperty("prompt").GetString()!;
                first ??= prompt;
                var started = at.AddMinutes(15 * (turn - 1));
                documents.Add(Message($"msg:{id}/{turn}p", prompt, started, SearchRole.User));
                documents.Add(Message($"msg:{id}/{turn}a", element.GetProperty("answer").GetString()!, started.AddMinutes(5), SearchRole.Assistant));

                SearchDocument Message(string reference, string text, DateTimeOffset when, SearchRole role) => new(reference, SearchKind.Message, title, text, when)
                {
                    SpaceId = Space, ProjectId = project, SessionId = id, RunId = $"{id}-run{turn}", Role = role, EmbeddingTitle = embeddingTitle,
                };
            }

            if (first is not null)
            {
                documents.Add(new($"session:{id}", SearchKind.Session, title, first.Length <= 2000 ? first : first[..2000], at)
                {
                    SpaceId = Space, ProjectId = project, SessionId = id, EmbeddingTitle = embeddingTitle,
                });
            }
        }

        var written = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var (name, kind) in new[] { ("docs", SearchKind.Doc), ("tools", SearchKind.Tool), ("skills", SearchKind.Skill) })
        {
            if (!root.TryGetProperty(name, out var list)) continue;
            foreach (var item in list.EnumerateArray())
                documents.Add(new(item.GetProperty("ref").GetString()!, kind, item.GetProperty("title").GetString()!, item.GetProperty("text").GetString()!, written));
        }

        return documents;
    }

    /// <summary>Reads a set of queries: one JSON object per line, <c>{"query": "…", "relevant": ["ref", …], "type": "…"}</c>, <c>type</c> optional.</summary>
    public static IReadOnlyList<EvaluationQuery> ReadQueries(string path)
    {
        var queries = new List<EvaluationQuery>();
        var number = 0;
        foreach (var line in File.ReadLines(path))
        {
            number++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            var query = root.TryGetProperty("query", out var text) ? text.GetString() : null;
            var relevant = root.TryGetProperty("relevant", out var refs) && refs.ValueKind == JsonValueKind.Array
                ? refs.EnumerateArray().Select(static value => value.GetString()!).Where(static value => !string.IsNullOrEmpty(value)).ToArray()
                : [];
            if (string.IsNullOrWhiteSpace(query) || relevant.Length == 0) throw new InvalidDataException($"{path}:{number}: a query needs its text and at least one relevant ref.");
            var type = root.TryGetProperty("type", out var kind) && kind.GetString() is { Length: > 0 } named ? named : "other";
            queries.Add(new(query, type, relevant));
        }

        return queries;
    }

    /// <summary>The settings of a harness: search on, with meaning.</summary>
    public static SearchSettings Settings { get; } = SearchSettings.Default with { Semantic = true };
}

/// <summary>The host of a harness: its settings, and no project or session of a catalog.</summary>
internal sealed class EvaluationHost : ISearchHost
{
    public SearchSettings GetSettings() => EvaluationData.Settings;
    public ValueTask<IReadOnlyList<SearchProjectInfo>> GetProjectsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<SearchProjectInfo>>([]);
    public ValueTask<IReadOnlyDictionary<string, SearchSessionInfo>> GetSessionsAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyDictionary<string, SearchSessionInfo>>(new Dictionary<string, SearchSessionInfo>());
    public bool IsProjectExcludedByItself(SearchProjectInfo project) => false;
}

/// <summary>Search by meaning with a model loaded in the process, over the active vector set of a store; it can keep the vector of each query.</summary>
internal sealed class EvaluationMeaning(IEmbeddingEngine engine, EmbeddingModelDefinition model, SearchMeaningIndex index) : ISearchMeaning
{
    private readonly Dictionary<(string, bool), float[]> _vectors = [];

    /// <summary>Gets or sets whether the vector of a query is embedded once and kept.</summary>
    public bool KeepVectors { get; set; }

    /// <summary>Gets the time of each query embedded, in milliseconds.</summary>
    public List<double> EmbedMilliseconds { get; } = [];

    public SearchMeaningState State => SearchMeaningState.Ready;

    public double Coverage => 1;

    /// <summary>Gets the vector of a query, with the query prompt of the model.</summary>
    public float[] Embed(string query, bool code)
    {
        if (KeepVectors && _vectors.TryGetValue((query, code), out var kept)) return kept;
        var started = Stopwatch.GetTimestamp();
        var vector = engine.Embed([model.Query(query, code)])[0];
        EmbedMilliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (KeepVectors) _vectors[(query, code)] = vector;
        return vector;
    }

    public async ValueTask<IReadOnlyList<(long ChunkId, float Similarity)>> SearchAsync(string query, SearchScope scope, bool codeQuery, int limit, CancellationToken cancellationToken)
        => await index.SearchAsync(Embed(query, codeQuery), scope, limit, cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// The evaluation of one index: the vectors of each preset embedded in it, and the queries answered by words, by meaning
/// and both, with the ranking of the application and with variants of it; what it measures is written as Markdown tables.
/// A hit counts as the ref <paramref name="equivalents"/> gives it, when it gives one: a session is its first prompt.
/// </summary>
internal sealed class EvaluationRun(SearchIndexStore store, IReadOnlyList<EvaluationQuery> queries, string modelRoot, string runtime, TextWriter output,
    IReadOnlyDictionary<string, string>? equivalents = null)
{
    /// <summary>The hits a query is judged on.</summary>
    public const int Depth = 10;

    private static readonly (string Name, SearchMode Mode)[] Modes = [("words", SearchMode.Keyword), ("meaning", SearchMode.Meaning), ("both", SearchMode.Auto)];

    private readonly OnnxEmbeddingEngineFactory _factory = new();

    /// <summary>Gets the scores by preset, mode and type of query (<c>all</c> for every query).</summary>
    public Dictionary<(string Preset, string Mode, string Type), EvaluationScore> Scores { get; } = [];

    /// <summary>Gets the mean share of the first 10 chunks by float32 vectors that the 8-bit codes find, by preset.</summary>
    public Dictionary<string, (double Mean, double Min)> Overlap { get; } = [];

    /// <summary>Gets the chunks embedded per second, by preset.</summary>
    public Dictionary<string, double> ChunksPerSecond { get; } = [];

    /// <summary>The folder of the files of a preset under the model root, as the opt-in tests name them.</summary>
    public static string FolderOf(string preset) => preset == EmbeddingModelCatalog.DefaultId ? "eg2" : "granite";

    /// <summary>Embeds the index with each preset and answers the queries; prints the tables.</summary>
    public async Task RunAsync(IReadOnlyList<string> presets, bool variants, CancellationToken cancellationToken)
    {
        var types = queries.Select(static query => query.Type).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var hashes = await ReadChunkHashesAsync(cancellationToken);
        output.WriteLine($"{queries.Count} queries ({string.Join(", ", types.Select(type => $"{type} {queries.Count(query => query.Type == type)}"))}); {hashes.Count} chunks; {Environment.ProcessorCount} cores");
        var lines = new List<string>();
        var variantLines = new List<string>();
        foreach (var preset in presets)
        {
            var model = EmbeddingModelCatalog.Find(preset) ?? throw new ArgumentException($"Unknown preset '{preset}'.", nameof(presets));
            var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            using var engine = _factory.Load(new EmbeddingWorkerLoad(model.Id, Path.Combine(modelRoot, FolderOf(model.Id)), runtime, threads));
            var floats = await EmbedAsync(model, engine, threads, cancellationToken);
            await using var index = new SearchMeaningIndex(store);
            await index.LoadAsync(cancellationToken);
            var meaning = new EvaluationMeaning(engine, model, index);
            var service = new SearchService(store, null, new EvaluationHost(), null) { Meaning = meaning };

            // Warm the model and the store once, then time every query.
            await service.SearchAsync(new SearchRequest("warm up", new SearchScope()) { Limit = Depth }, cancellationToken);
            meaning.EmbedMilliseconds.Clear();
            var answers = new List<List<(EvaluationQuery Query, IReadOnlyList<string> Hits, double Milliseconds)>>();
            foreach (var (name, mode) in Modes)
            {
                var results = await AnswerAsync(service, mode, cancellationToken);
                answers.Add(results);
                foreach (var type in types.Prepend("all"))
                {
                    var score = Score(results.Where(result => type == "all" || result.Query.Type == type).ToList());
                    Scores[(model.Id, name, type)] = score;
                    lines.Add($"| {model.Id} | {name} | {type} | {score.Queries} | {score.Recall:F3} | {score.Mrr:F3} | {score.MedianMilliseconds:F1} |");
                }
            }

            if (Environment.GetEnvironmentVariable("CODEALTA_SEARCH_EVAL_DETAILS") is { Length: > 0 })
            {
                // The rank of the first relevant hit of each query in each mode, and the first hits of both.
                output.WriteLine($"{model.Id}: first relevant rank (words / meaning / both), then the first hits of both");
                for (var i = 0; i < queries.Count; i++)
                {
                    var ranks = answers.Select(results => results[i].Hits.ToList().FindIndex(queries[i].Relevant.Contains) is var rank and >= 0 ? (rank + 1).ToString(CultureInfo.InvariantCulture) : "-");
                    output.WriteLine($"  [{queries[i].Type}] {queries[i].Query} — {string.Join(" / ", ranks)} — {string.Join(", ", answers[2][i].Hits.Take(5))}");
                }
            }

            var embedMedian = Median(meaning.EmbedMilliseconds);
            Overlap[model.Id] = await OverlapAsync(meaning, index, floats, hashes, cancellationToken);
            output.WriteLine($"{model.Id}: {ChunksPerSecond[model.Id]:F1} chunks/s with {threads} threads; query embedding median {embedMedian:F1} ms; "
                + $"8-bit top {Depth} overlap with float32: mean {Overlap[model.Id].Mean:P1}, min {Overlap[model.Id].Min:P0}");
            if (variants)
            {
                meaning.KeepVectors = true;
                foreach (var (name, ranking, mode) in Variants())
                {
                    service.Ranking = ranking;
                    var results = await AnswerAsync(service, mode, cancellationToken);
                    var cells = types.Prepend("all").Select(type => Score(results.Where(result => type == "all" || result.Query.Type == type).ToList()))
                        .Select(static score => $"{score.Recall:F3} / {score.Mrr:F3}");
                    variantLines.Add($"| {model.Id} | {name} | {string.Join(" | ", cells)} |");
                }

                service.Ranking = SearchRanking.Default;
            }
        }

        output.WriteLine();
        output.WriteLine("| Preset | Mode | Queries | n | Recall@10 | MRR@10 | Median ms |");
        output.WriteLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var line in lines) output.WriteLine(line);
        if (variantLines.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"Variants of the ranking (recall@10 / MRR@10):");
            output.WriteLine();
            output.WriteLine($"| Preset | Variant | {string.Join(" | ", types.Prepend("all"))} |");
            output.WriteLine($"| --- | --- |{string.Concat(Enumerable.Repeat(" --- |", types.Count + 1))}");
            foreach (var line in variantLines) output.WriteLine(line);
        }
    }

    // The ranking of the application, then one change at a time.
    private static IEnumerable<(string Name, SearchRanking Ranking, SearchMode Mode)> Variants()
    {
        var current = SearchRanking.Default;
        yield return ("words, as shipped", current, SearchMode.Keyword);
        yield return ("words, no title bonus", current with { TitleBonus = 0 }, SearchMode.Keyword);
        yield return ("words, no kind quotas", current with { KindQuotas = false }, SearchMode.Keyword);
        yield return ("both, as shipped (k 60)", current, SearchMode.Auto);
        yield return ("both, relaxed words weight 1", current with { RelaxedWeight = 1 }, SearchMode.Auto);
        yield return ("both, relaxed words weight 0.25", current with { RelaxedWeight = 0.25 }, SearchMode.Auto);
        foreach (var k in new[] { 5.0, 20, 40, 100 }) yield return ($"both, k {k}", current with { FusionK = k }, SearchMode.Auto);
        yield return ("both, no title bonus", current with { TitleBonus = 0 }, SearchMode.Auto);
        yield return ("both, title bonus ×2", current with { TitleBonus = 2 }, SearchMode.Auto);
        yield return ("both, no kind quotas", current with { KindQuotas = false }, SearchMode.Auto);
        yield return ("both, 10 hits per session", current with { HitsPerSession = 10 }, SearchMode.Auto);
        yield return ("both, 30 candidates per list", current with { CandidatesPerList = 30 }, SearchMode.Auto);
        foreach (var weight in new[] { 0.5, 1.5, 2.0 }) yield return ($"both, meaning weight {weight}", current with { MeaningWeight = weight }, SearchMode.Auto);
        yield return ("both, no quotas, meaning ×1.5", current with { KindQuotas = false, MeaningWeight = 1.5 }, SearchMode.Auto);
    }

    private async Task<List<(EvaluationQuery Query, IReadOnlyList<string> Hits, double Milliseconds)>> AnswerAsync(SearchService service, SearchMode mode, CancellationToken cancellationToken)
    {
        var results = new List<(EvaluationQuery, IReadOnlyList<string>, double)>(queries.Count);
        foreach (var query in queries)
        {
            var started = Stopwatch.GetTimestamp();
            var response = await service.SearchAsync(new SearchRequest(query.Query, new SearchScope()) { Mode = mode, Limit = Depth, Origin = "evaluation" }, cancellationToken);
            results.Add((query, [.. response.Hits.Select(hit => equivalents?.GetValueOrDefault(hit.Ref) ?? hit.Ref)], Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }

        return results;
    }

    /// <summary>Scores answers: recall and reciprocal rank within the first <see cref="Depth"/> hits.</summary>
    private static EvaluationScore Score(IReadOnlyList<(EvaluationQuery Query, IReadOnlyList<string> Hits, double Milliseconds)> results)
    {
        if (results.Count == 0) return new(0, 0, 0, 0);
        double recall = 0, mrr = 0;
        foreach (var (query, hits, _) in results)
        {
            var top = hits.Take(Depth).ToList();
            var relevant = query.Relevant.ToHashSet(StringComparer.Ordinal);
            recall += (double)relevant.Count(top.Contains) / relevant.Count;
            var first = top.FindIndex(relevant.Contains);
            if (first >= 0) mrr += 1.0 / (first + 1);
        }

        return new(results.Count, recall / results.Count, mrr / results.Count, Median([.. results.Select(static result => result.Milliseconds)]));
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    // Embeds every chunk with the document prompt, as the window does in batches, and makes the set active. The float
    // vectors are kept by the hash of their text, for the comparison with the 8-bit codes.
    private async Task<Dictionary<string, float[]>> EmbedAsync(EmbeddingModelDefinition model, IEmbeddingEngine engine, int threads, CancellationToken cancellationToken)
    {
        var set = await store.OpenVectorSetAsync(StoreMeaningVectors.Identity(model, DesktopSearchMeaning.OnnxRuntimeVersion), cancellationToken);
        var floats = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var watch = Stopwatch.StartNew();
        var count = 0;
        var characters = 0L;
        while (true)
        {
            var missing = await store.ListMissingVectorsAsync(set.Id, DesktopSearchMeaning.BatchSize, cancellationToken);
            if (missing.Count == 0) break;
            var vectors = engine.Embed([.. missing.Select(work => model.Document(work.Title, work.Text))]);
            await store.PutVectorsAsync(set.Id, [.. missing.Zip(vectors, static (work, vector) => new SearchVectorInput(work.EmbedHash, vector))], cancellationToken);
            for (var i = 0; i < missing.Count; i++) floats[Convert.ToHexString(missing[i].EmbedHash)] = vectors[i];
            count += missing.Count;
            characters += missing.Sum(static work => (long)work.Text.Length);
        }

        var seconds = watch.Elapsed.TotalSeconds;
        ChunksPerSecond[model.Id] = count / Math.Max(0.001, seconds);
        output.WriteLine($"{model.Id}: embedded {count} chunks ({characters / Math.Max(1, count)} characters on average) in {seconds:F1} s with {threads} threads");
        await store.CompleteVectorSetAsync(set.Id, cancellationToken);
        return floats;
    }

    private async Task<(double Mean, double Min)> OverlapAsync(EvaluationMeaning meaning, SearchMeaningIndex index, Dictionary<string, float[]> floats,
        IReadOnlyList<(long ChunkId, string Hash)> hashes, CancellationToken cancellationToken)
    {
        double sum = 0, min = 1;
        foreach (var query in queries)
        {
            var vector = meaning.Embed(query.Query, code: false);
            var exact = hashes.Where(chunk => floats.ContainsKey(chunk.Hash))
                .Select(chunk => (chunk.ChunkId, Score: Dot(floats[chunk.Hash], vector)))
                .OrderByDescending(static pair => pair.Score).ThenBy(static pair => pair.ChunkId).Take(Depth).Select(static pair => pair.ChunkId).ToHashSet();
            var codes = await index.SearchAsync(vector, new SearchScope(), Depth, cancellationToken);
            var share = (double)codes.Count(pair => exact.Contains(pair.ChunkId)) / Math.Max(1, exact.Count);
            sum += share;
            min = Math.Min(min, share);
        }

        return (sum / Math.Max(1, queries.Count), min);

        static float Dot(float[] left, float[] right)
        {
            var total = 0f;
            for (var i = 0; i < left.Length; i++) total += left[i] * right[i];
            return total;
        }
    }

    private async Task<IReadOnlyList<(long ChunkId, string Hash)>> ReadChunkHashesAsync(CancellationToken cancellationToken)
        => await store.ReadAsync<IReadOnlyList<(long, string)>>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, embed_hash FROM search_chunk ORDER BY id";
            var list = new List<(long, string)>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) list.Add((reader.GetInt64(0), Convert.ToHexString((byte[])reader[1])));
            return list;
        }, cancellationToken);
}
