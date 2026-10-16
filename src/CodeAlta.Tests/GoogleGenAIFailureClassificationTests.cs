using CodeAlta.Agent;
using CodeAlta.Agent.GoogleGenAI;
using Google.GenAI;

namespace CodeAlta.Tests;

/// <summary>
/// The failures of the Gemini API reach the fallback: the SDK's ApiException hides the status of its
/// HttpRequestException, so the generic classifier sees none of them.
/// </summary>
[TestClass]
public sealed class GoogleGenAIFailureClassificationTests
{
    [TestMethod]
    public void AnInvalidKey_IsASignInFailure_ThoughItIsA400()
    {
        var invalid = new ClientError(
            "API key not valid. Please pass a valid API key.\nDetails: [{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\",\"reason\":\"API_KEY_INVALID\"}]",
            400,
            "INVALID_ARGUMENT");

        Assert.AreEqual(AgentFailureKind.Auth, GoogleGenAIModelProviderRuntime.ClassifyFailure(invalid)?.Kind);
    }

    [TestMethod]
    public void ALimitPerMinute_IsARateLimit_WithItsDelay()
    {
        var limited = new ClientError(
            "You exceeded your current quota, please check your plan and billing details.\n* Quota exceeded for metric: generativelanguage.googleapis.com/generate_content_free_tier_requests, limit: 10\nPlease retry in 37.4s.\nDetails: [{\"quotaId\":\"GenerateRequestsPerMinutePerProjectPerModel-FreeTier\"},{\"@type\":\"type.googleapis.com/google.rpc.RetryInfo\",\"retryDelay\":\"37s\"}]",
            429,
            "RESOURCE_EXHAUSTED");

        var failure = GoogleGenAIModelProviderRuntime.ClassifyFailure(limited);

        Assert.AreEqual(AgentFailureKind.RateLimited, failure?.Kind, "Every Gemini 429 says quota: a limit per minute is not an exhausted quota.");
        Assert.AreEqual(38, failure?.RetryAfterSeconds);
    }

    [TestMethod]
    public void ADailyQuota_IsAnExhaustedQuota()
    {
        var exhausted = new ClientError(
            "You exceeded your current quota.\nDetails: [{\"quotaId\":\"GenerateRequestsPerDayPerProjectPerModel-FreeTier\"}]",
            429,
            "RESOURCE_EXHAUSTED");

        Assert.AreEqual(AgentFailureKind.QuotaExhausted, GoogleGenAIModelProviderRuntime.ClassifyFailure(exhausted)?.Kind);
    }

    [TestMethod]
    [DataRow(503, "UNAVAILABLE")]
    [DataRow(500, "INTERNAL")]
    [DataRow(504, "DEADLINE_EXCEEDED")]
    public void AServerError_IsAnOutage(int status, string name)
    {
        var failure = GoogleGenAIModelProviderRuntime.ClassifyFailure(
            new InvalidOperationException("Wrapped.", new ServerError("The model is overloaded. Please try again later.", status, name)));

        Assert.AreEqual(AgentFailureKind.Unavailable, failure?.Kind);
    }

    [TestMethod]
    public void AModelThatIsNotFound_IsAnUnavailableModel()
    {
        var missing = new ClientError(
            "models/gemini-2.5-flash is not found for API version v1beta, or is not supported for generateContent.",
            404,
            "NOT_FOUND");

        Assert.AreEqual(AgentFailureKind.ModelUnavailable, GoogleGenAIModelProviderRuntime.ClassifyFailure(missing)?.Kind);
    }

    [TestMethod]
    public void ABadRequest_HasNoKindThatFallsBack()
        => Assert.IsNull(GoogleGenAIModelProviderRuntime.ClassifyFailure(new ClientError("Invalid value at 'contents'.", 400, "INVALID_ARGUMENT")));
}
