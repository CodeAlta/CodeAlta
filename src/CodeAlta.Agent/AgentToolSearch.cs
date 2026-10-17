using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent;

/// <summary>A search of the tool that finds deferred tools.</summary>
/// <param name="Query">What the model looks for, in its own words.</param>
/// <param name="Limit">The most tools to return.</param>
/// <param name="SessionId">The session that searches.</param>
public sealed record AgentToolSearchQuery(string Query, int Limit, string SessionId);

/// <summary>A tool a search found.</summary>
/// <param name="ToolName">Its name in the session (<see cref="AgentToolSpec.Name"/>).</param>
/// <param name="Description">What it does, in one line, or null.</param>
public sealed record AgentToolSearchHit(string ToolName, string? Description);

/// <summary>Finds the tools that match a search, best first.</summary>
/// <param name="query">The search.</param>
/// <param name="cancellationToken">Stops the search.</param>
/// <returns>The tools found, of any state: those not deferred in the session are said to be there already.</returns>
public delegate ValueTask<IReadOnlyList<AgentToolSearchHit>> AgentToolSearchHandler(AgentToolSearchQuery query, CancellationToken cancellationToken);

/// <summary>
/// The tool that finds deferred tools (<see cref="AgentToolSpec.Deferred"/>) by what they do and loads them: its result names
/// each one with an <see cref="AgentToolResultItem.ToolReference"/>, which the provider expands into the definition of the tool.
/// </summary>
/// <remarks>
/// Its name, description and schema never change, so that the tools of a request stay the same from one request to the next:
/// a provider caches them at the start of the prompt.
/// </remarks>
public static class AgentToolSearch
{
    /// <summary>The name of the tool.</summary>
    public const string ToolName = "tool_search";

    /// <summary>The description of the tool.</summary>
    public const string Description =
        "Find tools that are not loaded yet by what they do, and load them: the MCP tools of the configured servers and other tools of CodeAlta. " +
        "Describe the task in a few words (\"post a message on slack\", \"create a github issue\"). The tools found are loaded at once: call them in your next step. " +
        "Search again with other words when nothing fits.";

    private const int DefaultLimit = 5;
    private const int MaximumLimit = 10;

    /// <summary>Creates the tool.</summary>
    /// <param name="search">What finds the tools.</param>
    /// <returns>The tool, which searches deferred tools (<see cref="AgentToolSpec.SearchesDeferredTools"/>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="search"/> is null.</exception>
    public static AgentToolDefinition Create(AgentToolSearchHandler search)
    {
        ArgumentNullException.ThrowIfNull(search);
        using var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"query":{"type":"string","description":"What the tool you need does, in a few words."},"limit":{"type":"integer","description":"The most tools to load, 1 to 10; 5 when left out."}},"required":["query"],"additionalProperties":false}
            """);
        return new AgentToolDefinition(
            new AgentToolSpec(ToolName, Description, schema.RootElement.Clone()) { SearchesDeferredTools = true },
            (invocation, cancellationToken) => SearchAsync(search, invocation, cancellationToken));
    }

    private static async Task<AgentToolResult> SearchAsync(AgentToolSearchHandler search, AgentToolInvocation invocation, CancellationToken cancellationToken)
    {
        var arguments = invocation.Arguments;
        var query = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("query", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(query))
        {
            const string missing = "Give `query`: what the tool you need does.";
            return new AgentToolResult(false, [new AgentToolResultItem.Text(missing)], missing);
        }

        var limit = arguments.TryGetProperty("limit", out var limitValue) && limitValue.ValueKind == JsonValueKind.Number && limitValue.TryGetInt32(out var given)
            ? Math.Clamp(given, 1, MaximumLimit)
            : DefaultLimit;
        var runTools = invocation.RunTools;
        IReadOnlyList<AgentToolSearchHit> hits = await search(new AgentToolSearchQuery(query, limit, invocation.SessionId), cancellationToken).ConfigureAwait(false);
        // Where the index has nothing (it is off, or has not read a new list of tools yet), the words of the deferred tools do.
        if (runTools is not null && !hits.Any(hit => runTools.IsDeferred(hit.ToolName)))
        {
            hits = [.. hits, .. MatchWords(query, runTools.GetDeferredTools(), limit)];
        }

        var loaded = new List<AgentToolSearchHit>();
        var present = new List<AgentToolSearchHit>();
        foreach (var hit in hits)
        {
            if (loaded.Count + present.Count >= limit) break;
            if (runTools is null || !runTools.Contains(hit.ToolName)) continue;
            // Only a deferred tool is loaded; another one is already in the tools of the model.
            (runTools.IsDeferred(hit.ToolName) ? loaded : present).Add(hit);
        }

        var text = new StringBuilder();
        if (loaded.Count == 0 && present.Count == 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"No tool matched \"{query}\". Search again with other words, or look further with `alta search \"{query}\" --kind tool,mcp,skill`.");
            return new AgentToolResult(true, [new AgentToolResultItem.Text(text.ToString())]);
        }

        foreach (var hit in loaded) text.Append("Loaded: ").Append(Line(hit)).Append('\n');
        foreach (var hit in present) text.Append("Already loaded: ").Append(Line(hit)).Append('\n');
        return new AgentToolResult(true, [new AgentToolResultItem.Text(text.ToString().TrimEnd()), .. loaded.Select(static hit => new AgentToolResultItem.ToolReference(hit.ToolName))]);
    }

    // The deferred tools that have the most words of the query in their name and description.
    private static IEnumerable<AgentToolSearchHit> MatchWords(string query, IReadOnlyList<AgentToolSpec> tools, int limit)
    {
        var words = query.Split([' ', '\t', ',', '.', '_', '-', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(static word => word.Length > 2)
            .Select(static word => word.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (words.Length == 0) return [];
        return tools
            .Select(tool => (Tool: tool, Score: words.Count(word => tool.Name.Contains(word, StringComparison.OrdinalIgnoreCase) || tool.Description.Contains(word, StringComparison.OrdinalIgnoreCase))))
            .Where(static match => match.Score > 0)
            .OrderByDescending(static match => match.Score)
            .ThenBy(static match => match.Tool.Name, StringComparer.Ordinal)
            .Take(limit)
            .Select(static match => new AgentToolSearchHit(match.Tool.Name, FirstLine(match.Tool.Description)));
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\n', '\r']);
        var line = end < 0 ? text : text[..end];
        return line.Length > 200 ? line[..200] + "…" : line;
    }

    private static string Line(AgentToolSearchHit hit)
        => string.IsNullOrWhiteSpace(hit.Description) ? hit.ToolName : hit.ToolName + " — " + hit.Description.Trim();
}
