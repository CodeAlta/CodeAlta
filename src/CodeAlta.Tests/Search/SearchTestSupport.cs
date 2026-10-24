using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog.Search;
using CodeAlta.Search;

namespace CodeAlta.Tests.Search;

/// <summary>Writes synthetic journals in the shape CodeAlta writes them.</summary>
internal sealed class JournalWriter
{
    private readonly StringBuilder _text = new();
    private DateTimeOffset _time;

    public JournalWriter(string sessionId, DateTimeOffset start, string? projectId = "p1", string space = "default", DateTimeOffset? countedFrom = null)
    {
        SessionId = sessionId;
        _time = start;
        var header = new Dictionary<string, object?>
        {
            ["schema_version"] = 1, ["session_id"] = sessionId, ["kind"] = "ProjectSession", ["project_ref"] = projectId, ["space_id"] = space,
            ["created_at"] = start.ToString("O"),
        };
        if (countedFrom is { } from) header["forked_from"] = new Dictionary<string, object?> { ["session_id"] = "source", ["counted_from"] = from.ToString("O") };
        Line(new Dictionary<string, object?> { ["$type"] = "raw", ["backendEventType"] = "codealta.sessionHeader", ["raw"] = header });
    }

    public string SessionId { get; }

    public string Text => _text.ToString();

    public JournalWriter User(string run, string id, string content, bool fromAgent = false)
    {
        var record = Record("contentCompleted", "User", run);
        record["contentId"] = id;
        record["content"] = content;
        if (fromAgent) record["source_session_id"] = "parent";
        return Line(record);
    }

    public JournalWriter Assistant(string run, string id, string content)
    {
        var record = Record("contentCompleted", "Assistant", run);
        record["contentId"] = id;
        record["content"] = content;
        return Line(record);
    }

    public JournalWriter ToolOutput(string run, int size)
    {
        var record = Record("contentCompleted", "ToolOutput", run);
        record["contentId"] = "tool:" + run;
        record["content"] = new string('x', size);
        return Line(record);
    }

    public JournalWriter Idle(string run) => Line(Record("sessionUpdate", "Idle", run));

    public JournalWriter Notes(string markdown) => Line(new Dictionary<string, object?> { ["$type"] = "notes", ["kind"] = "Set", ["markdown"] = markdown, ["timestamp"] = Next() });

    public JournalWriter Raw(string line)
    {
        _text.Append(line);
        return this;
    }

    private Dictionary<string, object?> Record(string type, string kind, string run) => new()
    {
        ["$type"] = type, ["kind"] = kind, ["sessionId"] = SessionId, ["timestamp"] = Next(), ["runId"] = run,
    };

    private string Next()
    {
        _time = _time.AddSeconds(10);
        return _time.ToString("O");
    }

    private JournalWriter Line(Dictionary<string, object?> record)
    {
        _text.Append(JsonSerializer.Serialize(record)).Append('\n');
        return this;
    }
}

/// <summary>A folder of journals behind <see cref="ISessionJournalCatalog"/>.</summary>
internal sealed class FolderJournals(string root) : ISessionJournalCatalog
{
    public string Path(string sessionId) => System.IO.Path.Combine(root, sessionId + ".jsonl");

    public void Write(JournalWriter journal) => File.WriteAllText(Path(journal.SessionId), journal.Text);

    public async IAsyncEnumerable<SessionJournalFile> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        foreach (var file in Directory.EnumerateFiles(root, "*.jsonl").Select(static path => new FileInfo(path)).OrderByDescending(static info => info.LastWriteTimeUtc))
        {
            yield return new SessionJournalFile(System.IO.Path.GetFileNameWithoutExtension(file.Name), file.FullName, file.Length, file.LastWriteTimeUtc);
        }
    }

    public ValueTask<SessionJournalFile?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(Path(sessionId));
        return ValueTask.FromResult(info.Exists ? new SessionJournalFile(sessionId, info.FullName, info.Length, info.LastWriteTimeUtc) : null);
    }

    public ValueTask<Stream?> OpenAsync(string sessionId, long offset = 0, CancellationToken cancellationToken = default)
    {
        var path = Path(sessionId);
        if (!File.Exists(path)) return ValueTask.FromResult<Stream?>(null);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Position = offset;
        return ValueTask.FromResult<Stream?>(stream);
    }
}

/// <summary>An application as search sees it, set by the test.</summary>
internal sealed class FakeSearchHost : ISearchHost
{
    public SearchSettings Settings { get; set; } = SearchSettings.Default;
    public List<SearchProjectInfo> Projects { get; } = [new("p1", "Alpha", "/tmp/alpha", false), new("p2", "Beta", "/tmp/beta", false)];
    public Dictionary<string, SearchSessionInfo> Sessions { get; } = new(StringComparer.Ordinal);
    public HashSet<string> ExcludedByThemselves { get; } = [];

    public SearchSettings GetSettings() => Settings;
    public ValueTask<IReadOnlyList<SearchProjectInfo>> GetProjectsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<SearchProjectInfo>>(Projects);
    public ValueTask<IReadOnlyDictionary<string, SearchSessionInfo>> GetSessionsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyDictionary<string, SearchSessionInfo>>(Sessions);
    public bool IsProjectExcludedByItself(SearchProjectInfo project) => ExcludedByThemselves.Contains(project.ProjectId);

    /// <summary>Gets or sets the recent use of the skills, and records what it was asked for.</summary>
    public IReadOnlyDictionary<string, double>? SkillUsage { get; set; }

    public List<(string? ProjectId, string SpaceId)> SkillUsageAsked { get; } = [];

    public IReadOnlyDictionary<string, double>? GetSkillUsage(string? projectId, string spaceId)
    {
        SkillUsageAsked.Add((projectId, spaceId));
        return SkillUsage;
    }
}

/// <summary>A temporary folder with an index, journals and a host, for one test.</summary>
internal sealed class SearchFixture : IAsyncDisposable
{
    private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-search-" + Guid.NewGuid().ToString("N"));

    public SearchFixture(params ISearchSource[] sources)
    {
        Directory.CreateDirectory(System.IO.Path.Combine(_root, "journals"));
        Journals = new FolderJournals(System.IO.Path.Combine(_root, "journals"));
        Store = new SearchIndexStore(System.IO.Path.Combine(_root, "data", "search.sqlite3"));
        Sources = [.. sources];
        Indexer = new SearchIndexer(Store, Journals, Host, () => Sources, TimeProvider.System,
            new SearchIndexerOptions { StartDelay = TimeSpan.FromHours(1), QuietStart = TimeSpan.Zero, PassInterval = TimeSpan.FromHours(1), FileQuiet = TimeSpan.FromMilliseconds(10) });
        Service = new SearchService(Store, Indexer, Host, Journals);
    }

    public string Root => _root;
    public FolderJournals Journals { get; }
    public FakeSearchHost Host { get; } = new();
    public SearchIndexStore Store { get; }
    public List<ISearchSource> Sources { get; }
    public SearchIndexer Indexer { get; }
    public SearchService Service { get; }

    public async Task PassAsync()
    {
        Indexer.Start();
        Indexer.SignalPass();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Task.Delay(20, timeout.Token);
        await Indexer.WaitIdleAsync(timeout.Token);
    }

    public Task<SearchResponse> SearchAsync(string query, SearchScope? scope = null, int limit = 10)
        => Service.SearchAsync(new SearchRequest(query, scope ?? new SearchScope()) { Limit = limit });

    public async ValueTask DisposeAsync()
    {
        await Indexer.DisposeAsync();
        await Store.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
