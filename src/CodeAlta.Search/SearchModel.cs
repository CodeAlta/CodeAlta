using System.Collections.Frozen;

namespace CodeAlta.Search;

/// <summary>What an item of the index is.</summary>
public enum SearchKind : byte
{
    /// <summary>The prompt or the final answer of a turn of a session.</summary>
    Message = 1,

    /// <summary>A session: its title and its first prompt.</summary>
    Session = 2,

    /// <summary>The notes of a session.</summary>
    Note = 3,

    /// <summary>A plan of a project (<c>.alta/plans</c>).</summary>
    Plan = 4,

    /// <summary>A task of a project (<c>.alta/tasks</c>).</summary>
    Task = 5,

    /// <summary>A skill.</summary>
    Skill = 6,

    /// <summary>A tool: built-in, of a plugin, of the UI, an <c>alta</c> command, an agent prompt.</summary>
    Tool = 7,

    /// <summary>An MCP server or one of its tools.</summary>
    Mcp = 8,

    /// <summary>A plugin.</summary>
    Plugin = 9,

    /// <summary>A command of the window.</summary>
    Command = 10,

    /// <summary>A section of the user guide, or a type of the plugin API.</summary>
    Doc = 11,

    /// <summary>A script of the library of Code Mode: the samples, yours, and those of the projects.</summary>
    Script = 12,
}

/// <summary>Who wrote a message.</summary>
public enum SearchRole : byte
{
    /// <summary>Not a message.</summary>
    None = 0,

    /// <summary>A prompt the user typed.</summary>
    User = 1,

    /// <summary>The final answer of a turn.</summary>
    Assistant = 2,

    /// <summary>A prompt sent by another session.</summary>
    Agent = 3,
}

/// <summary>How a query is answered.</summary>
public enum SearchMode : byte
{
    /// <summary>By words, and by meaning when it is on.</summary>
    Auto = 0,

    /// <summary>By words only.</summary>
    Keyword = 1,

    /// <summary>By meaning only, when it is on.</summary>
    Meaning = 2,
}

/// <summary>How a hit was found.</summary>
public enum SearchMatch : byte
{
    /// <summary>By its words.</summary>
    Words = 1,

    /// <summary>By its meaning only.</summary>
    Meaning = 2,

    /// <summary>By both.</summary>
    Both = 3,
}

/// <summary>The names of the kinds and roles, as <c>alta search</c> and the window write them.</summary>
public static class SearchNames
{
    private static readonly FrozenDictionary<string, SearchKind> Kinds = new Dictionary<string, SearchKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["message"] = SearchKind.Message, ["messages"] = SearchKind.Message, ["turn"] = SearchKind.Message,
        ["session"] = SearchKind.Session, ["sessions"] = SearchKind.Session,
        ["note"] = SearchKind.Note, ["notes"] = SearchKind.Note,
        ["plan"] = SearchKind.Plan, ["plans"] = SearchKind.Plan,
        ["task"] = SearchKind.Task, ["tasks"] = SearchKind.Task,
        ["skill"] = SearchKind.Skill, ["skills"] = SearchKind.Skill,
        ["tool"] = SearchKind.Tool, ["tools"] = SearchKind.Tool,
        ["mcp"] = SearchKind.Mcp,
        ["plugin"] = SearchKind.Plugin, ["plugins"] = SearchKind.Plugin,
        ["command"] = SearchKind.Command, ["commands"] = SearchKind.Command,
        ["doc"] = SearchKind.Doc, ["docs"] = SearchKind.Doc,
        ["script"] = SearchKind.Script, ["scripts"] = SearchKind.Script,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets every kind, in the order they are listed.</summary>
    public static IReadOnlyList<SearchKind> AllKinds { get; } =
        [SearchKind.Message, SearchKind.Session, SearchKind.Note, SearchKind.Plan, SearchKind.Task, SearchKind.Skill,
            SearchKind.Tool, SearchKind.Mcp, SearchKind.Plugin, SearchKind.Command, SearchKind.Doc, SearchKind.Script];

    /// <summary>Gets the kinds that are content of sessions and projects, which exclusions remove.</summary>
    public static IReadOnlyList<SearchKind> ContentKinds { get; } = [SearchKind.Message, SearchKind.Session, SearchKind.Note, SearchKind.Plan, SearchKind.Task];

    /// <summary>Gets the kinds that describe what is installed, which tool search serves.</summary>
    public static IReadOnlyList<SearchKind> CatalogKinds { get; } = [SearchKind.Skill, SearchKind.Tool, SearchKind.Mcp, SearchKind.Plugin, SearchKind.Command, SearchKind.Doc, SearchKind.Script];

    /// <summary>Gets the name of a kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>Its name.</returns>
    public static string Of(SearchKind kind) => kind switch
    {
        SearchKind.Message => "message",
        SearchKind.Session => "session",
        SearchKind.Note => "note",
        SearchKind.Plan => "plan",
        SearchKind.Task => "task",
        SearchKind.Skill => "skill",
        SearchKind.Tool => "tool",
        SearchKind.Mcp => "mcp",
        SearchKind.Plugin => "plugin",
        SearchKind.Command => "command",
        SearchKind.Doc => "doc",
        SearchKind.Script => "script",
        _ => "unknown",
    };

    /// <summary>Gets the name of a role.</summary>
    /// <param name="role">The role.</param>
    /// <returns>Its name, or null for <see cref="SearchRole.None"/>.</returns>
    public static string? Of(SearchRole role) => role switch
    {
        SearchRole.User => "user",
        SearchRole.Assistant => "assistant",
        SearchRole.Agent => "agent",
        _ => null,
    };

    /// <summary>Gets the name of a match.</summary>
    /// <param name="match">The match.</param>
    /// <returns>Its name.</returns>
    public static string Of(SearchMatch match) => match switch { SearchMatch.Meaning => "meaning", SearchMatch.Both => "both", _ => "words" };

    /// <summary>Reads a kind by its name.</summary>
    /// <param name="name">The name.</param>
    /// <param name="kind">The kind.</param>
    /// <returns>True when the name is known.</returns>
    public static bool TryParseKind(string? name, out SearchKind kind)
    {
        kind = default;
        return name is not null && Kinds.TryGetValue(name.Trim(), out kind);
    }

    /// <summary>Reads a role by its name.</summary>
    /// <param name="name">The name.</param>
    /// <param name="role">The role.</param>
    /// <returns>True when the name is known.</returns>
    public static bool TryParseRole(string? name, out SearchRole role)
    {
        role = name?.Trim().ToLowerInvariant() switch
        {
            "user" => SearchRole.User,
            "assistant" => SearchRole.Assistant,
            "agent" => SearchRole.Agent,
            _ => SearchRole.None,
        };
        return role != SearchRole.None;
    }
}

/// <summary>Something to index, as a source gives it.</summary>
/// <param name="Ref">What names it in a hit, unique in the index: <c>msg:&lt;session&gt;/&lt;content&gt;</c>, <c>skill:&lt;scope&gt;/&lt;name&gt;</c>…</param>
/// <param name="Kind">What it is.</param>
/// <param name="Title">Its title: the title of its session, the name of its skill, the path of headings of its section.</param>
/// <param name="Text">The text that is matched.</param>
/// <param name="At">When it was written, for the order and the filters by period.</param>
public sealed record SearchDocument(string Ref, SearchKind Kind, string Title, string Text, DateTimeOffset At)
{
    /// <summary>Gets the space its session belongs to, or null.</summary>
    public string? SpaceId { get; init; }

    /// <summary>Gets the project it belongs to, or null.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets its session, or null.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets the run of its turn, or null.</summary>
    public string? RunId { get; init; }

    /// <summary>Gets who wrote it, for a message.</summary>
    public SearchRole Role { get; init; }

    /// <summary>Gets what the window and agents need to use a hit, as small JSON (a path, a state, a command), or null.</summary>
    public string? Extra { get; init; }

    /// <summary>Gets the text given to the embedding model as the title of the document, when it is not <see cref="Title"/>.</summary>
    public string? EmbeddingTitle { get; init; }
}

/// <summary>Where a query looks.</summary>
public sealed record SearchScope
{
    /// <summary>Gets the kinds, or null for all of them.</summary>
    public IReadOnlySet<SearchKind>? Kinds { get; init; }

    /// <summary>Gets the space of the caller: sessions of another space are not found. Null for every space.</summary>
    public string? SpaceId { get; init; }

    /// <summary>Gets the projects the caller can see (those of its space), or null for every project.</summary>
    public IReadOnlySet<string>? Projects { get; init; }

    /// <summary>Gets the one project to look in, or null.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets the one session to look in, or null.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets a session whose content is left out (the calling one), or null.</summary>
    public string? ExceptSessionId { get; init; }

    /// <summary>Gets the role of the messages, or <see cref="SearchRole.None"/> for every role.</summary>
    public SearchRole Role { get; init; }

    /// <summary>Gets the earliest time, or null.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Gets the latest time, or null.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Gets the projects kept out of search.</summary>
    public IReadOnlySet<string>? ExcludedProjects { get; init; }

    /// <summary>Gets the spaces kept out of search.</summary>
    public IReadOnlySet<string>? ExcludedSpaces { get; init; }

    /// <summary>Gets whether an item of a catalog kind (a skill, a tool…) may be returned to the caller, or null for all.</summary>
    public Func<SearchHit, bool>? CatalogFilter { get; init; }

    /// <summary>Gets whether a kind is in the scope.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True when it is.</returns>
    public bool Includes(SearchKind kind) => Kinds is null || Kinds.Contains(kind);
}

/// <summary>A query.</summary>
/// <param name="Query">What is looked for, as typed.</param>
/// <param name="Scope">Where.</param>
public sealed record SearchRequest(string Query, SearchScope Scope)
{
    /// <summary>Gets how it is answered.</summary>
    public SearchMode Mode { get; init; }

    /// <summary>Gets the most hits returned.</summary>
    public int Limit { get; init; } = 10;

    /// <summary>Gets whether the last word is a prefix being typed, as in the search window.</summary>
    public bool PrefixLastTerm { get; init; }

    /// <summary>Gets whether hits by age get a decay, the newest first among equal ones.</summary>
    public bool Recent { get; init; }

    /// <summary>Gets who asks: <c>window</c>, <c>agent</c> or <c>tool_search</c>, for the activity.</summary>
    public string Origin { get; init; } = "agent";

    /// <summary>Gets the session that asks, for the activity, or null.</summary>
    public string? OriginSessionId { get; init; }
}

/// <summary>A part of a snippet that matched a word of the query.</summary>
/// <param name="Start">The offset in the snippet.</param>
/// <param name="Length">The length.</param>
public readonly record struct SearchMark(int Start, int Length);

/// <summary>One result.</summary>
public sealed record SearchHit
{
    /// <summary>Gets what it is.</summary>
    public required SearchKind Kind { get; init; }

    /// <summary>Gets what names it.</summary>
    public required string Ref { get; init; }

    /// <summary>Gets its title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the part of its text shown, with the matched words in <see cref="Marks"/>.</summary>
    public required string Snippet { get; init; }

    /// <summary>Gets the matched words of <see cref="Snippet"/>.</summary>
    public IReadOnlyList<SearchMark> Marks { get; init; } = [];

    /// <summary>Gets when it was written.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>Gets its score after fusion: higher is better.</summary>
    public double Score { get; init; }

    /// <summary>Gets how it was found.</summary>
    public SearchMatch Match { get; init; } = SearchMatch.Words;

    /// <summary>Gets its rank by words, from 1, or null.</summary>
    public int? WordsRank { get; init; }

    /// <summary>Gets its rank by meaning, from 1, or null.</summary>
    public int? MeaningRank { get; init; }

    /// <summary>Gets its space, or null.</summary>
    public string? SpaceId { get; init; }

    /// <summary>Gets its project, or null.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets its session, or null.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets the run of its turn, or null.</summary>
    public string? RunId { get; init; }

    /// <summary>Gets who wrote it.</summary>
    public SearchRole Role { get; init; }

    /// <summary>Gets what the source gave to use it, as small JSON, or null.</summary>
    public string? Extra { get; init; }
}

/// <summary>The state of search by meaning, as an answer reports it.</summary>
public enum SearchMeaningState : byte
{
    /// <summary>Search by meaning is off.</summary>
    Off = 0,

    /// <summary>It is on and every item has its vector.</summary>
    Ready = 1,

    /// <summary>It is on and items are still being embedded.</summary>
    Indexing = 2,

    /// <summary>The model is being loaded: this answer is by words only.</summary>
    Warming = 3,

    /// <summary>The model failed: answers are by words only.</summary>
    Failed = 4,

    /// <summary>It is on but its model is not downloaded.</summary>
    NotInstalled = 5,
}

/// <summary>The answer to a query.</summary>
public sealed record SearchResponse
{
    /// <summary>Gets the hits, the best first.</summary>
    public required IReadOnlyList<SearchHit> Hits { get; init; }

    /// <summary>Gets how it was answered: <see cref="SearchMode.Keyword"/>, or <see cref="SearchMode.Auto"/> for both.</summary>
    public SearchMode Mode { get; init; }

    /// <summary>Gets the state of search by meaning.</summary>
    public SearchMeaningState Meaning { get; init; }

    /// <summary>Gets the share of items that have a vector, from 0 to 1.</summary>
    public double Coverage { get; init; }

    /// <summary>Gets whether more hits exist than were returned.</summary>
    public bool Truncated { get; init; }

    /// <summary>Gets the milliseconds of the search by words.</summary>
    public double WordsMilliseconds { get; init; }

    /// <summary>Gets the milliseconds of the search by meaning, embedding of the query included, or zero.</summary>
    public double MeaningMilliseconds { get; init; }
}
