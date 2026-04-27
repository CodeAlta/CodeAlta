using CodeAlta.Agent;
using CodeAlta.Presentation.Chat;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentModelCapabilityHelperTests
{
    [TestMethod]
    public void SupportsImageInput_UsesModelsDevInputModalities()
    {
        var model = new AgentModelInfo(
            "gpt-vision",
            Capabilities: new Dictionary<string, object?>
            {
                ["inputModalities"] = new[] { "text", "image" },
            });

        Assert.IsTrue(AgentModelCapabilityHelper.SupportsImageInput(new AgentBackendId("local"), model));
    }

    [TestMethod]
    public void SupportsImageInput_HonorsExplicitNegativeCapability()
    {
        var model = new AgentModelInfo(
            "text-only",
            Capabilities: new Dictionary<string, object?>
            {
                ["supportsImageInput"] = false,
                ["inputModalities"] = new[] { "text", "image" },
            });

        Assert.IsFalse(AgentModelCapabilityHelper.SupportsImageInput(new AgentBackendId("local"), model));
    }

    [TestMethod]
    public void SupportsImageInput_RejectsTextOnlyModalitiesForNonCodexBackend()
    {
        var model = new AgentModelInfo(
            "text-only",
            Capabilities: new Dictionary<string, object?>
            {
                ["inputModalities"] = new[] { "text" },
            });

        Assert.IsFalse(AgentModelCapabilityHelper.SupportsImageInput(new AgentBackendId("local"), model));
    }
}
