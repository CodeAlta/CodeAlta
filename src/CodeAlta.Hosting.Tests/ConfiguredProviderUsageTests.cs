using System.Net;
using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Hosting.Tests;

/// <summary>Which providers have a usage to read, and what is answered before any of them is asked.</summary>
[TestClass]
public sealed class ConfiguredProviderUsageTests
{
    [TestMethod]
    public void OnlyTheSubscriptionProviders_HaveAUsageToRead()
    {
        foreach (var type in new[] { "codex", "copilot", "claude-code" }) Assert.IsTrue(ConfiguredProviderUsage.Supports(type), type);
        foreach (var type in new[] { "xai", "openai-chat", "openai-responses", "anthropic", "", null }) Assert.IsFalse(ConfiguredProviderUsage.Supports(type), type);
    }

    [TestMethod]
    public async Task AProviderWithoutASignIn_IsNotAsked()
    {
        var root = Directory.CreateTempSubdirectory("CodeAlta-provider-usage-").FullName;
        try
        {
            using var http = new HttpClient(new Refusing());

            var codex = await ConfiguredProviderUsage.ReadAsync(new CodeAltaProviderDocument { ProviderKey = "codex", ProviderType = "codex" }, root, http, default);
            Assert.AreEqual(AgentSubscriptionUsageReading.SignedOut, codex.Status);
            var copilot = await ConfiguredProviderUsage.ReadAsync(new CodeAltaProviderDocument { ProviderKey = "copilot", ProviderType = "copilot" }, root, http, default);
            Assert.AreEqual(AgentSubscriptionUsageReading.SignedOut, copilot.Status);
            // A Copilot token of the environment has no GitHub account to ask the quotas of.
            var token = await ConfiguredProviderUsage.ReadAsync(
                new CodeAltaProviderDocument { ProviderKey = "copilot", ProviderType = "copilot", AuthSource = "copilot_token_env" }, root, http, default);
            Assert.AreEqual(AgentSubscriptionUsageReading.Unavailable, token.Status);

            await Assert.ThrowsExactlyAsync<ArgumentException>(() => ConfiguredProviderUsage.ReadAsync(
                new CodeAltaProviderDocument { ProviderKey = "xai", ProviderType = "xai" }, root, http, default));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => ConfiguredProviderUsage.ReadAsync(
                new CodeAltaProviderDocument { ProviderKey = "codex", ProviderType = "codex" }, " ", http, default));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Refusing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new AssertFailedException("No provider is asked: " + request.RequestUri + " " + HttpStatusCode.OK);
    }
}
