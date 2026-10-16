using System.Net;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentFailureClassifierTests
{
    [TestMethod]
    [DataRow(401, null, AgentFailureKind.Auth)]
    [DataRow(403, null, AgentFailureKind.Auth)]
    [DataRow(402, null, AgentFailureKind.QuotaExhausted)]
    [DataRow(429, "Too many requests", AgentFailureKind.RateLimited)]
    [DataRow(429, "You have exhausted your monthly quota", AgentFailureKind.QuotaExhausted)]
    [DataRow(408, null, AgentFailureKind.Unavailable)]
    [DataRow(500, null, AgentFailureKind.Unavailable)]
    [DataRow(503, null, AgentFailureKind.Unavailable)]
    [DataRow(529, null, AgentFailureKind.Unavailable)]
    public void HttpStatus_GivesTheKindOfTheFailure(int status, string? message, AgentFailureKind expected)
        => Assert.AreEqual(expected, AgentFailureClassifier.FromHttpStatus(status, message, retryAfterSeconds: null)?.Kind);

    [TestMethod]
    public void Classify_WalksTheExceptionAndLeavesACancelUnclassified()
    {
        Assert.IsNull(AgentFailureClassifier.Classify(null));
        Assert.IsNull(AgentFailureClassifier.Classify(new InvalidOperationException("A tool failed.")));
        Assert.IsNull(AgentFailureClassifier.Classify(new OperationCanceledException()));
        Assert.IsNull(AgentFailureClassifier.Classify(new HttpRequestException("Bad request.", null, HttpStatusCode.BadRequest)));
        Assert.AreEqual(
            AgentFailureKind.Unavailable,
            AgentFailureClassifier.Classify(new TaskCanceledException("Timed out.", new TimeoutException()))?.Kind);
        Assert.AreEqual(
            AgentFailureKind.Auth,
            AgentFailureClassifier.Classify(new InvalidOperationException("Wrapped.", new HttpRequestException("No.", null, HttpStatusCode.Unauthorized)))?.Kind);

        var resets = DateTimeOffset.UtcNow.AddHours(2);
        var quota = new AgentProviderFailureException("Quota.", new AgentFailureInfo(AgentFailureKind.QuotaExhausted, resets));
        Assert.AreEqual(new AgentFailureInfo(AgentFailureKind.QuotaExhausted, resets), AgentFailureClassifier.Classify(new AggregateException(quota)));
        Assert.AreEqual(
            AgentFailureKind.RateLimited,
            AgentFailureClassifier.Classify(new AgentTurnExecutionException(new AgentTurnFailure("Slow down.", false, new AgentFailureInfo(AgentFailureKind.RateLimited))))?.Kind);
    }

    [TestMethod]
    [DataRow("Rate limit reached for gpt-5 on tokens per min (TPM). Please try again in 6.2s. Visit …", 6.2)]
    [DataRow("Please try again in 820ms.", 0.82)]
    [DataRow("Please try again in 1m30s.", 90.0)]
    [DataRow("You exceeded your current quota. Please retry in 37.5s.", 37.5)]
    [DataRow("Sorry, you've exceeded your weekly rate limit. Please wait 5 hours 52 minutes before trying again or consider switching to Auto.", 21120.0)]
    [DataRow("Please wait 463 hours before trying again.", 1666800.0)]
    [DataRow("Wait for resources to reset in the next 5-hour window.", null)]
    [DataRow("Too many requests.", null)]
    [DataRow(null, null)]
    public void TryParseRetryDelay_ReadsTheWaitAProviderWrites(string? text, double? expectedSeconds)
    {
        var parsed = AgentFailureClassifier.TryParseRetryDelay(text, out var delay);

        Assert.AreEqual(expectedSeconds is not null, parsed);
        if (expectedSeconds is { } seconds) Assert.AreEqual(seconds, delay.TotalSeconds, 0.001);
    }
}
