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

    // A request over the 32 MB the API takes (images or PDFs in the conversation) is not a reason to change provider,
    // and its message says what to do.
    [TestMethod]
    public void ARequestTooLarge_IsNotAnOutage_AndSaysWhatToDo()
    {
        var tooLarge = new Anthropic4xxException
        {
            StatusCode = HttpStatusCode.RequestEntityTooLarge,
            ResponseBody = """{"type":"error","error":{"type":"request_too_large","message":"Request exceeds the maximum allowed number of bytes."}}""",
        };

        Assert.AreEqual(AgentFailureKind.Other, AnthropicModelProviderRuntime.ClassifyFailure(tooLarge)?.Kind);
        var message = AnthropicModelProviderRuntime.DescribeFailure(tooLarge, AnthropicStopReasonTests.CreateRequest());
        StringAssert.Contains(message, "32 MB");
        StringAssert.Contains(message, "Compact the session");
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

    // The monthly spend cap of the organization comes as a rate_limit_error: it is not a short limit, and it says when it ends.
    [TestMethod]
    public void TheSpendCapOfTheOrganization_IsAnExhaustedQuota_UntilItsDate()
    {
        var capped = new AnthropicRateLimitException
        {
            StatusCode = HttpStatusCode.TooManyRequests,
            ResponseBody = """{"type":"error","error":{"type":"rate_limit_error","message":"You have reached your API usage limits: your organization has crossed its monthly API usage threshold. You will regain access on 2026-09-01 at 00:00 UTC.","details":{"error_code":"enforced_spend_limit_reached"}}}""",
        };

        var failure = AnthropicModelProviderRuntime.ClassifyFailure(capped);

        Assert.AreEqual(AgentFailureKind.QuotaExhausted, failure?.Kind);
        Assert.AreEqual(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), failure?.ResetsAt);
    }

    [TestMethod]
    [DataRow(400, """{"type":"error","error":{"type":"invalid_request_error","message":"Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits."}}""")]
    [DataRow(400, """{"type":"error","error":{"type":"invalid_request_error","message":"You have reached your specified workspace API usage limits. You will regain access on 2026-11-01 at 00:00 UTC."}}""")]
    [DataRow(403, """{"error":{"message":"You've reached your 5-hour usage limit. Your quota will reset when the current 5-hour window ends.","type":"permission_denied_error"}}""")]
    public void AnEmptyBalanceOrAUsageLimit_IsAnExhaustedQuota_WhateverItsStatus(int status, string body)
    {
        var failure = AnthropicModelProviderRuntime.ClassifyFailure(new Anthropic4xxException { StatusCode = (HttpStatusCode)status, ResponseBody = body });

        Assert.AreEqual(AgentFailureKind.QuotaExhausted, failure?.Kind);
    }

    // The weekly limit of Copilot comes through its Claude models too: hours, not the 30 seconds of a per-minute limit.
    [TestMethod]
    public void AWeeklyLimitOfCopilot_IsALongRateLimit()
    {
        var limited = new AnthropicRateLimitException
        {
            StatusCode = HttpStatusCode.TooManyRequests,
            ResponseBody = """{"error":{"message":"Sorry, you've exceeded your weekly rate limit. Please wait 5 hours 52 minutes before trying again or consider switching to Auto.","code":"user_weekly_rate_limited"}}""",
        };

        var failure = AnthropicModelProviderRuntime.ClassifyFailure(limited);

        Assert.AreEqual(AgentFailureKind.RateLimited, failure?.Kind);
        Assert.AreEqual(21_120, failure?.RetryAfterSeconds);
        Assert.IsNotNull(failure?.ResetsAt);
    }

    [TestMethod]
    public void AnOverloadedEngineAnsweredWithA429_IsAnOutage()
    {
        var failure = AnthropicModelProviderRuntime.ClassifyFailure(new AnthropicRateLimitException
        {
            StatusCode = HttpStatusCode.TooManyRequests,
            ResponseBody = """{"error":{"message":"The engine is currently overloaded, please try again later","type":"engine_overloaded_error"}}""",
        });

        Assert.AreEqual(AgentFailureKind.Unavailable, failure?.Kind);
    }

    [TestMethod]
    public void AModelTheAccountCannotUse_IsAnUnavailableModel()
    {
        var failure = AnthropicModelProviderRuntime.ClassifyFailure(new Anthropic4xxException
        {
            StatusCode = HttpStatusCode.BadRequest,
            ResponseBody = """{"error":{"message":"The requested model is not supported.","code":"model_not_supported","param":"model","type":"invalid_request_error"}}""",
        });

        Assert.AreEqual(AgentFailureKind.ModelUnavailable, failure?.Kind);
    }

    [TestMethod]
    [DataRow("""SSE error returned from server: '{"type":"error","error":{"type":"billing_error","message":"Billing issue."}}'""", AgentFailureKind.QuotaExhausted)]
    [DataRow("""SSE error returned from server: '{"type":"error","error":{"type":"timeout_error","message":"Timed out."}}'""", AgentFailureKind.Unavailable)]
    [DataRow("""SSE error returned from server: '{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}'""", AgentFailureKind.Unavailable)]
    [DataRow("""SSE error returned from server: '{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}'""", AgentFailureKind.Auth)]
    [DataRow("""SSE error returned from server: '{"type":"error","error":{"type":"invalid_request_error","message":"Bad."}}'""", AgentFailureKind.Other)]
    public void AnErrorInTheStream_HasTheKindOfItsType(string message, AgentFailureKind expected)
        => Assert.AreEqual(expected, AnthropicModelProviderRuntime.ClassifyFailure(new AnthropicSseException(message))?.Kind);
}
