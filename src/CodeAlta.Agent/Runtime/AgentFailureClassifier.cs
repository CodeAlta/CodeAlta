using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Says what kind of failure ended a run, from the exception that ended it: what a provider classified itself, or
/// an HTTP status. A run the user cancelled has no kind.
/// </summary>
public static class AgentFailureClassifier
{
    /// <summary>Classifies the exception that ended a run.</summary>
    /// <param name="exception">The exception.</param>
    /// <returns>The kind of failure, or <see langword="null"/> when the run was cancelled or nothing is known.</returns>
    public static AgentFailureInfo? Classify(Exception? exception)
    {
        AgentFailureInfo? found = null;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AgentTurnExecutionException { Failure.Failure: { } failure }:
                    return failure;
                case AgentProviderFailureException { Failure: var failure }:
                    return failure;
                case AggregateException aggregate when aggregate.InnerExceptions.Count > 0:
                    return Classify(aggregate.InnerExceptions[0]);
                case HttpRequestException { StatusCode: { } status } when found is null:
                    found = FromHttpStatus((int)status, current.Message, retryAfterSeconds: null);
                    break;
                case HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError } when found is null:
                    found = new AgentFailureInfo(AgentFailureKind.Unavailable);
                    break;
                case TimeoutException when found is null:
                    found = new AgentFailureInfo(AgentFailureKind.Unavailable);
                    break;
                case OperationCanceledException when current.InnerException is not TimeoutException:
                    return null;
            }
        }

        return found;
    }

    /// <summary>
    /// Classifies an HTTP status: 401 and 403 are <see cref="AgentFailureKind.Auth"/>, 402 an exhausted quota, 429 a
    /// rate limit (an exhausted quota when its message says so), 408 and the 5xx statuses an unavailable provider.
    /// </summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="message">The message or body of the response, which can tell a quota from a short limit.</param>
    /// <param name="retryAfterSeconds">The <c>Retry-After</c> of the response, when it has one.</param>
    /// <param name="resetsAt">When the quota resets, when the response says it.</param>
    /// <returns>The kind of failure, or <see langword="null"/> for a status that says nothing of it.</returns>
    public static AgentFailureInfo? FromHttpStatus(int status, string? message, int? retryAfterSeconds, DateTimeOffset? resetsAt = null)
        => status switch
        {
            (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden => new AgentFailureInfo(AgentFailureKind.Auth),
            (int)HttpStatusCode.PaymentRequired => new AgentFailureInfo(AgentFailureKind.QuotaExhausted, resetsAt, retryAfterSeconds),
            (int)HttpStatusCode.TooManyRequests => new AgentFailureInfo(
                IsQuotaMessage(message) ? AgentFailureKind.QuotaExhausted : AgentFailureKind.RateLimited,
                resetsAt,
                retryAfterSeconds),
            (int)HttpStatusCode.RequestTimeout or 529 or >= 500 and <= 599 => new AgentFailureInfo(AgentFailureKind.Unavailable, resetsAt, retryAfterSeconds),
            _ => null,
        };

    /// <summary>
    /// Gets whether a message of a provider speaks of an exhausted quota, allowance or credits rather than of a
    /// short rate limit.
    /// </summary>
    /// <param name="message">The message or body.</param>
    public static bool IsQuotaMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        return message.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase)
               || message.Contains("usage_limit_reached", StringComparison.OrdinalIgnoreCase)
               || message.Contains("usage_not_included", StringComparison.OrdinalIgnoreCase)
               || message.Contains("quota", StringComparison.OrdinalIgnoreCase)
               || message.Contains("credit", StringComparison.OrdinalIgnoreCase)
               || message.Contains("billing", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads how long a provider asks to wait from the text of its error: "try again in 6.2s", "try again in 1m30s",
    /// "Please retry in 37.4s", "Please wait 5 hours 52 minutes".
    /// </summary>
    /// <param name="text">The message or body of the error.</param>
    /// <param name="delay">The delay, when the text says one.</param>
    /// <returns><see langword="true"/> when the text says how long to wait.</returns>
    public static bool TryParseRetryDelay(string? text, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (string.IsNullOrEmpty(text)) return false;
        var match = RetryDelayPattern.Match(text);
        if (!match.Success) return false;

        var total = 0.0;
        foreach (Match part in RetryDelayPartPattern.Matches(match.Groups["delay"].Value))
        {
            if (!double.TryParse(part.Groups["value"].ValueSpan, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return false;
            var unit = part.Groups["unit"].Value.ToLowerInvariant();
            total += unit switch
            {
                "ms" or "millisecond" or "milliseconds" => value / 1000,
                "s" or "sec" or "secs" or "second" or "seconds" => value,
                "m" or "min" or "mins" or "minute" or "minutes" => value * 60,
                "h" or "hr" or "hrs" or "hour" or "hours" => value * 3600,
                _ => value * 86400,
            };
        }

        if (total <= 0 || total > TimeSpan.MaxValue.TotalSeconds) return false;
        delay = TimeSpan.FromSeconds(total);
        return true;
    }

    private const string RetryDelayPart = @"\d+(?:\.\d+)?\s*(?:ms|milliseconds?|seconds?|secs?|s|minutes?|mins?|m|hours?|hrs?|h|days?|d)(?![a-z])";

    // "try again in", "retry in", "retry after", "wait", followed by one or more parts of a duration.
    private static readonly Regex RetryDelayPattern = new(
        $@"(?:try\s+again|retry|wait)\s+(?:in\s+|after\s+|for\s+)?(?<delay>{RetryDelayPart}(?:[\s,]*(?:and\s+)?{RetryDelayPart})*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RetryDelayPartPattern = new(
        @"(?<value>\d+(?:\.\d+)?)\s*(?<unit>ms|milliseconds?|seconds?|secs?|s|minutes?|mins?|m|hours?|hrs?|h|days?|d)(?![a-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

/// <summary>
/// The exception a provider ends a run with when it knows what kind of failure it is (an exhausted quota, a
/// provider that stays unavailable...).
/// </summary>
public sealed class AgentProviderFailureException : InvalidOperationException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="message">The message shown for the failure.</param>
    /// <param name="failure">The kind of failure.</param>
    /// <param name="innerException">The exception that caused it, when there is one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="failure"/> is null.</exception>
    public AgentProviderFailureException(string message, AgentFailureInfo failure, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Failure = failure;
    }

    /// <summary>Gets the kind of failure.</summary>
    public AgentFailureInfo Failure { get; }
}
