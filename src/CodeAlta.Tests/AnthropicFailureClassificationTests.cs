using System.Net;
using Anthropic.Exceptions;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AnthropicFailureClassificationTests
{
    // The SDK has already waited the retry-after of the API twice and gives no header with its exception: a 429 of the
    // per-minute limits is a short rate limit, which the fallback waits out instead of leaving Anthropic for 15 minutes.
    [TestMethod]
    public void ARateLimitWithoutRetryAfter_IsAShortRateLimit()
    {
        var limited = new AnthropicRateLimitException
        {
            StatusCode = HttpStatusCode.TooManyRequests,
            ResponseBody = """{"type":"error","error":{"type":"rate_limit_error","message":"This request would exceed the rate limit for your organization of 30,000 input tokens per minute."}}""",
        };

        var failure = AnthropicModelProviderRuntime.ClassifyFailure(limited);

        Assert.AreEqual(AgentFailureKind.RateLimited, failure?.Kind);
        Assert.IsTrue(failure?.RetryAfterSeconds is > 0 and < 60, $"A short wait, not an exhausted quota: {failure?.RetryAfterSeconds}.");
    }

    [TestMethod]
    public void ARateLimitErrorInTheStream_IsAShortRateLimit()
    {
        var failure = AnthropicModelProviderRuntime.ClassifyFailure(new AnthropicSseException("rate_limit_error: Number of request tokens has exceeded your per-minute rate limit"));

        Assert.AreEqual(AgentFailureKind.RateLimited, failure?.Kind);
        Assert.IsTrue(failure?.RetryAfterSeconds is > 0 and < 60);
    }

    [TestMethod]
    public void A429ThatSpeaksOfCredits_IsAnExhaustedQuota()
    {
        var limited = new AnthropicRateLimitException
        {
            StatusCode = HttpStatusCode.TooManyRequests,
            ResponseBody = """{"type":"error","error":{"type":"rate_limit_error","message":"Your credit balance is too low."}}""",
        };

        var failure = AnthropicModelProviderRuntime.ClassifyFailure(limited);

        Assert.AreEqual(AgentFailureKind.QuotaExhausted, failure?.Kind);
    }
}
