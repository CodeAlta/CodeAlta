using CodeAlta.Agent;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class ModelCatalogReasonTests
{
    [TestMethod]
    public void Reason_GivesThePageTheCodesOfTheAcpAgents_AndNoMessage()
    {
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("github-copilot-cli"), "Copilot");
        ModelProviderStateSnapshot State(ModelProviderAvailability availability, string? category)
            => new() { Descriptor = descriptor, Availability = availability, ErrorCategory = category, StatusMessage = "Copilot could not be started. secret detail" };

        Assert.AreEqual("acp-unavailable", ModelCatalogService.Reason(State(ModelProviderAvailability.Failed, "acp-unavailable")));
        Assert.AreEqual("acp-signed-out", ModelCatalogService.Reason(State(ModelProviderAvailability.Failed, "acp-signed-out")));
        Assert.AreEqual("acp-not-installed", ModelCatalogService.Reason(State(ModelProviderAvailability.Failed, "acp-not-installed")));
        Assert.AreEqual("claude-code-signed-out", ModelCatalogService.Reason(State(ModelProviderAvailability.Failed, "claude-code-signed-out")));
        Assert.IsNull(ModelCatalogService.Reason(State(ModelProviderAvailability.Failed, "plugin-own-category")));
        Assert.IsNull(ModelCatalogService.Reason(State(ModelProviderAvailability.Ready, "acp-unavailable")));
    }
}
