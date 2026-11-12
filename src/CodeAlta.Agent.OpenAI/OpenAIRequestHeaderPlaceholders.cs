using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.OpenAI;

/// <summary>
/// The placeholders of the header values of a provider, filled for each turn: <c>${session.id}</c>, <c>${run.id}</c>,
/// <c>${model.id}</c> and <c>${provider.key}</c>. A provider such as OpenCode Go asks for a value that stays the same for
/// one conversation and differs between conversations (<c>x-opencode-session = "${session.id}"</c>).
/// </summary>
internal static class OpenAIRequestHeaderPlaceholders
{
    /// <summary>Whether a value names a placeholder, known or not.</summary>
    public static bool Contains(string? value) => AgentRequestHeaderPlaceholders.Contains(value);

    /// <summary>Whether a header of a set has a value that names a placeholder.</summary>
    public static bool AnyIn(IReadOnlyDictionary<string, string>? headers) => AgentRequestHeaderPlaceholders.AnyIn(headers);

    /// <summary>
    /// The headers of a turn: the provider's, changed by the model's request overrides, with their placeholders filled.
    /// Null when the provider's headers apply as they are (no override of the model and no placeholder).
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ForTurn(
        IReadOnlyDictionary<string, string>? providerHeaders,
        AgentModelRequestOverride? modelRequest,
        AgentTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var headers = OpenAIModelRequestOverrides.MergeHeaders(providerHeaders, modelRequest)
                      ?? (AnyIn(providerHeaders) ? providerHeaders : null);
        return headers is null || !AnyIn(headers)
            ? headers
            : Fill(headers, request.SessionId, request.RunId.Value, request.ModelId, request.Provider.ProviderKey);
    }

    /// <summary>
    /// The headers of a request outside a turn, such as the list of the models: a header whose value names a placeholder
    /// has nothing to be filled with there, and is left out rather than sent as written.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? OutsideTurn(IReadOnlyDictionary<string, string>? headers)
        => AgentRequestHeaderPlaceholders.OutsideTurn(headers);

    /// <summary>Fills the placeholders of every value of a set of headers.</summary>
    internal static IReadOnlyDictionary<string, string> Fill(
        IReadOnlyDictionary<string, string> headers,
        string? sessionId,
        string? runId,
        string? modelId,
        string? providerKey)
        => AgentRequestHeaderPlaceholders.Fill(headers, sessionId, runId, modelId, providerKey);

    /// <summary>
    /// Fills the placeholders of one value. A placeholder that is not known, or a <c>${</c> that is not closed, is kept as
    /// written, so that a value that only looks like one is sent as it was.
    /// </summary>
    internal static string Fill(string value, string? sessionId, string? runId, string? modelId, string? providerKey)
        => AgentRequestHeaderPlaceholders.Fill(value, sessionId, runId, modelId, providerKey);
}
