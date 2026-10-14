using System.Net;
using System.Net.Http;

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
