using System.Text;
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
    private const string Start = "${";

    /// <summary>Whether a value names a placeholder, known or not.</summary>
    public static bool Contains(string? value) => value?.Contains(Start, StringComparison.Ordinal) == true;

    /// <summary>Whether a header of a set has a value that names a placeholder.</summary>
    public static bool AnyIn(IReadOnlyDictionary<string, string>? headers)
        => headers is { Count: > 0 } && headers.Values.Any(Contains);

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
        => !AnyIn(headers)
            ? headers
            : headers!.Where(static header => !Contains(header.Value)).ToDictionary(static header => header.Key, static header => header.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Fills the placeholders of every value of a set of headers.</summary>
    internal static IReadOnlyDictionary<string, string> Fill(
        IReadOnlyDictionary<string, string> headers,
        string? sessionId,
        string? runId,
        string? modelId,
        string? providerKey)
    {
        var filled = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            filled[header.Key] = Fill(header.Value, sessionId, runId, modelId, providerKey);
        }

        return filled;
    }

    /// <summary>
    /// Fills the placeholders of one value. A placeholder that is not known, or a <c>${</c> that is not closed, is kept as
    /// written, so that a value that only looks like one is sent as it was.
    /// </summary>
    internal static string Fill(string value, string? sessionId, string? runId, string? modelId, string? providerKey)
    {
        if (!Contains(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 32);
        var index = 0;
        while (index < value.Length)
        {
            var start = value.IndexOf(Start, index, StringComparison.Ordinal);
            var end = start < 0 ? -1 : value.IndexOf('}', start + Start.Length);
            if (end < 0)
            {
                builder.Append(value, index, value.Length - index);
                break;
            }

            builder.Append(value, index, start - index);
            var name = value.AsSpan(start + Start.Length, end - start - Start.Length);
            string? filled = name switch
            {
                "session.id" => sessionId,
                "run.id" => runId,
                "model.id" => modelId,
                "provider.key" => providerKey,
                _ => null,
            };
            if (filled is null && !IsKnown(name))
            {
                builder.Append(value, start, end - start + 1);
            }
            else
            {
                AppendHeaderSafe(builder, filled);
            }

            index = end + 1;
        }

        return builder.ToString();
    }

    private static bool IsKnown(ReadOnlySpan<char> name)
        => name is "session.id" or "run.id" or "model.id" or "provider.key";

    // A value of a header is one line: an id never holds a line break, but nothing of one is sent if it did.
    private static void AppendHeaderSafe(StringBuilder builder, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (var character in value)
        {
            if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }
    }
}
