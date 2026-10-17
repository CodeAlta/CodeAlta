using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeAlta.Agent;

/// <summary>
/// What a provider knows about a failure beyond its message (its error code, the HTTP status, the request id, the
/// payload it received), kept in <see cref="Exception.Data"/> under these keys. The error event of a run keeps the
/// entries of each exception (<see cref="AgentExceptionInfo.Data"/>), so the timeline, the log and <c>alta</c> show
/// them for any provider, without a protocol trace.
/// </summary>
public static partial class AgentErrorData
{
    /// <summary>The error code the provider returned (<c>rate_limit_exceeded</c>, <c>server_error</c>...).</summary>
    public const string Code = "code";

    /// <summary>The error type the provider returned (<c>invalid_request_error</c>...).</summary>
    public const string Type = "type";

    /// <summary>The request parameter the provider names in its error.</summary>
    public const string Param = "param";

    /// <summary>The HTTP status of the response, or the one an error event of a stream carries.</summary>
    public const string HttpStatus = "httpStatus";

    /// <summary>The id the provider gave the request, to quote to its support.</summary>
    public const string RequestId = "requestId";

    /// <summary>The transport of the request (<c>http</c>, <c>websocket</c>...).</summary>
    public const string Transport = "transport";

    /// <summary>How many times the request was sent before the failure was reported.</summary>
    public const string Attempts = "attempts";

    /// <summary>The body or the event the provider returned with the error, cut to a bounded length.</summary>
    public const string Payload = "payload";

    /// <summary>The text that replaces a secret.</summary>
    public const string Redacted = "[REDACTED]";

    // The keys that are recorded, in the order of a record: the payload last.
    private static readonly string[] RecordedKeys = [Code, Type, Param, HttpStatus, RequestId, Transport, Attempts, Payload];

    internal const int MaxValueLength = 2048;
    internal const int MaxPayloadLength = 8192;

    /// <summary>Sets an entry of <paramref name="exception"/> when <paramref name="value"/> says something.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty.</exception>
    public static TException Set<TException>(TException exception, string key, string? value)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            exception.Data[key] = value;
        }

        return exception;
    }

    /// <summary>Sets a number entry of <paramref name="exception"/> when <paramref name="value"/> is known.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty.</exception>
    public static TException Set<TException>(TException exception, string key, int? value)
        where TException : Exception
        => value is { } number
            ? Set(exception, key, number.ToString(CultureInfo.InvariantCulture))
            : exception;

    /// <summary>Sets an entry of <paramref name="exception"/> unless one of the exceptions it wraps has it.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> or <paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty.</exception>
    public static TException SetIfMissing<TException>(TException exception, string key, string? value)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Data.Contains(key))
            {
                return exception;
            }
        }

        return Set(exception, key, value);
    }

    /// <summary>
    /// Returns the entries of one exception that are recorded: the keys of this class (a provider may keep other
    /// entries, such as the raw headers of a response, for its own use: they are not recorded) and the status of an
    /// <see cref="HttpRequestException"/>, with secrets redacted and every value cut to a bounded length;
    /// <see langword="null"/> when there are none. It never throws: what could not be read is left out.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? Collect(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Dictionary<string, string>? data = null;
        try
        {
            foreach (var key in RecordedKeys)
            {
                var entry = exception.Data[key];
                if (entry is null && key == HttpStatus && exception is HttpRequestException { StatusCode: { } status })
                {
                    entry = (int)status;
                }

                var text = entry switch
                {
                    string value => value,
                    bool value => value ? "true" : "false",
                    IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(text))
                {
                    Add(ref data, key, text);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The details are a help: the error is recorded with what could be read.
        }

        return data;
    }

    /// <summary>
    /// Writes, in the details of a retry or a fallback, what failed: the message of the exception and what its chain
    /// says of the error (<see cref="Code"/> as <c>providerCode</c>, the type, the status, the request id, a payload),
    /// bounded and redacted.
    /// </summary>
    internal static void WriteDiagnostics(Utf8JsonWriter writer, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(exception);

        writer.WriteString("error", Bound(RedactText($"{exception.GetType().Name}: {exception.Message}"), MaxValueLength));
        var written = new HashSet<string>(StringComparer.Ordinal);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (Collect(current) is not { } data)
            {
                continue;
            }

            foreach (var key in (ReadOnlySpan<string>)[Code, Type, Param, HttpStatus, RequestId, Payload])
            {
                if (data.TryGetValue(key, out var value) && written.Add(key))
                {
                    writer.WriteString(key == Code ? "providerCode" : key, Bound(value, MaxValueLength));
                }
            }
        }
    }

    /// <summary>Removes the secrets a text may carry: a bearer token, a JWT.</summary>
    internal static string RedactText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var redacted = BearerPattern().Replace(text, "${prefix}" + Redacted);
        return JwtPattern().Replace(redacted, Redacted);
    }

    private static void Add(ref Dictionary<string, string>? data, string key, string value)
    {
        data ??= new Dictionary<string, string>(StringComparer.Ordinal);
        // A secret cut at the limit would no longer be recognized: it is redacted first.
        data[key] = Bound(RedactText(value), string.Equals(key, Payload, StringComparison.Ordinal) ? MaxPayloadLength : MaxValueLength);
    }

    private static string Bound(string value, int limit)
        => value.Length <= limit
            ? value
            : string.Concat(value.AsSpan(0, char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit), "…");

    [GeneratedRegex(@"(?<prefix>\bBearer\s+)[^\s""',;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();
}
