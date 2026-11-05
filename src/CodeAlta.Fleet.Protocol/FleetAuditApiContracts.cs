using System.Globalization;
using System.Text;

namespace CodeAlta.Fleet.Protocol.Api;

/// <summary>
/// The filters of the audit log (<c>GET /api/v1/audit</c> and <c>GET /api/v1/audit/export</c>, <c>doc/fleet/security-and-privacy.md</c>,
/// audit log): each one narrows, an absent one says nothing. Written and read as the query of those requests, so that the
/// console, <c>alta fleet audit</c> and any client ask the same way.
/// </summary>
public sealed record FleetAuditQuery
{
    /// <summary>The longest text of a filter.</summary>
    public const int MaxTextLength = 256;

    /// <summary>The most action patterns of <see cref="Actions"/>.</summary>
    public const int MaxActions = 32;

    /// <summary>The most records of a page.</summary>
    public const int MaxPage = 500;

    /// <summary>Gets the actions, exact (<c>device.approve</c>) or by prefix (<c>device.</c>, <c>team.*</c>); a record matches one of them.</summary>
    public IReadOnlyList<string>? Actions { get; init; }

    /// <summary>Gets the person behind the records.</summary>
    public string? PersonId { get; init; }

    /// <summary>Gets the device that asked.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Gets the result: <see cref="FleetAuditResults.Success"/>, <see cref="FleetAuditResults.Failure"/> (anything else) or an error code.</summary>
    public string? Result { get; init; }

    /// <summary>Gets what the records were done to.</summary>
    public string? Target { get; init; }

    /// <summary>Gets the address the requests came from.</summary>
    public string? SourceAddress { get; init; }

    /// <summary>Gets the first time, included.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Gets the time the records end before, excluded.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Gets words the action, the target, the details, the address or the request id hold, as they are written.</summary>
    public string? Text { get; init; }

    /// <summary>Gets the cursor of the oldest first order: records after this sequence.</summary>
    public long? After { get; init; }

    /// <summary>Gets the cursor of the newest first order: records before this sequence.</summary>
    public long? Before { get; init; }

    /// <summary>Gets whether the newest come first.</summary>
    public bool Newest { get; init; }

    /// <summary>Gets the size of a page, 1 to <see cref="MaxPage"/>; null for 100.</summary>
    public int? Limit { get; init; }

    /// <summary>Gets whether a filter narrows the log (cursors and order excepted).</summary>
    public bool IsFiltered => Actions is { Count: > 0 } || PersonId is not null || DeviceId is not null || Result is not null || Target is not null
        || SourceAddress is not null || From is not null || To is not null || Text is not null;

    /// <summary>Writes the query of a request, with a leading <c>?</c>, or an empty text when nothing is set.</summary>
    /// <returns>The query.</returns>
    public string ToQueryString()
    {
        var builder = new StringBuilder();
        void Add(string name, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            builder.Append(builder.Length == 0 ? '?' : '&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
        }

        if (Actions is { Count: > 0 } actions) Add("action", string.Join(',', actions));
        Add("person", PersonId);
        Add("device", DeviceId);
        Add("result", Result);
        Add("target", Target);
        Add("address", SourceAddress);
        Add("from", From?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        Add("to", To?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        Add("q", Text);
        Add("after", After?.ToString(CultureInfo.InvariantCulture));
        Add("before", Before?.ToString(CultureInfo.InvariantCulture));
        if (Newest) Add("order", "desc");
        Add("limit", Limit?.ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    /// <summary>Reads a query from the values of a request, by name.</summary>
    /// <param name="value">Gives the value of a parameter of the query, or null.</param>
    /// <param name="query">The query read, or null.</param>
    /// <param name="problem">What does not hold, for people, or null.</param>
    /// <returns>Whether every parameter holds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static bool TryParse(Func<string, string?> value, out FleetAuditQuery? query, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(value);
        query = null;
        problem = null;
        string? Text(string name)
        {
            var text = value(name)?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        var texts = new[] { "action", "person", "device", "result", "target", "address", "q" };
        foreach (var name in texts)
        {
            if (Text(name) is { } text && (text.Length > (name == "action" ? MaxTextLength * 4 : MaxTextLength) || text.Any(char.IsControl)))
            {
                problem = $"'{name}' has at most {MaxTextLength} characters, without control characters.";
                return false;
            }
        }

        string[]? actions = null;
        if (Text("action") is { } actionText)
        {
            actions = [.. actionText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];
            if (actions.Length > MaxActions || actions.Any(static action => action.Length > MaxTextLength))
            {
                problem = $"'action' names at most {MaxActions} actions or prefixes.";
                return false;
            }
        }

        if (!TryTime(Text("from"), out var from) || !TryTime(Text("to"), out var to))
        {
            problem = "'from' and 'to' are times, in milliseconds since 1970 or ISO 8601.";
            return false;
        }

        if (!TryLong(Text("after"), out var after) || !TryLong(Text("before"), out var before) || !TryLong(Text("limit"), out var limit))
        {
            problem = "'after', 'before' and 'limit' are positive numbers.";
            return false;
        }

        var order = Text("order");
        if (order is not (null or "asc" or "desc"))
        {
            problem = "'order' is 'asc' or 'desc'.";
            return false;
        }

        query = new FleetAuditQuery
        {
            Actions = actions, PersonId = Text("person"), DeviceId = Text("device"), Result = Text("result"), Target = Text("target"), SourceAddress = Text("address"),
            From = from, To = to, Text = Text("q"), After = after, Before = before, Newest = order == "desc",
            Limit = limit is null ? null : (int)Math.Clamp(limit.Value, 1, MaxPage),
        };
        return true;
    }

    /// <summary>Gets whether an action matches a pattern of <see cref="Actions"/>: equal, or after a prefix that ends with <c>.</c> or <c>*</c>.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="action">The action.</param>
    /// <returns>Whether it matches.</returns>
    public static bool Matches(string pattern, string action)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(action);
        if (pattern.EndsWith('*')) return action.StartsWith(pattern[..^1], StringComparison.Ordinal);
        return pattern.EndsWith('.') ? action.StartsWith(pattern, StringComparison.Ordinal) : string.Equals(pattern, action, StringComparison.Ordinal);
    }

    private static bool TryTime(string? text, out DateTimeOffset? time)
    {
        time = null;
        if (text is null) return true;
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds))
        {
            if (milliseconds > 253_402_300_799_999) return false;
            time = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            return true;
        }

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return false;
        time = parsed;
        return true;
    }

    private static bool TryLong(string? text, out long? number)
    {
        number = null;
        if (text is null) return true;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return false;
        number = value;
        return true;
    }
}

/// <summary>The results a filter of the audit log names beside error codes.</summary>
public static class FleetAuditResults
{
    /// <summary>The action succeeded.</summary>
    public const string Success = "success";

    /// <summary>Any result but a success: a refusal or an error, with its code.</summary>
    public const string Failure = "failure";
}

/// <summary>The formats of <c>GET /api/v1/audit/export</c>.</summary>
public static class FleetAuditExportFormats
{
    /// <summary>Comma-separated values with a header line, for spreadsheets: a cell a spreadsheet would read as a formula starts with an apostrophe.</summary>
    public const string Csv = "csv";

    /// <summary>One JSON object per line, every field as recorded: the format whose hashes verify elsewhere.</summary>
    public const string JsonLines = "jsonl";

    /// <summary>Gets whether a text names a format.</summary>
    /// <param name="format">The text.</param>
    /// <returns>Whether it is <see cref="Csv"/> or <see cref="JsonLines"/>.</returns>
    public static bool IsKnown(string? format) => format is Csv or JsonLines;
}
