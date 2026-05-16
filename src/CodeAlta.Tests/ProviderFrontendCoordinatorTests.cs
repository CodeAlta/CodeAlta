using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Models;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ProviderFrontendCoordinatorTests
{
    [TestMethod]
    public void TryBuildActiveBackendTestResult_UsesReadyReservedBackendState()
    {
        var definition = new CodeAltaProviderDocument
        {
            ProviderKey = "copilot_cli",
            ProviderType = "copilot_cli",
        };
        var backendState = new ChatBackendState(AgentBackendIds.Copilot, "Copilot CLI")
        {
            Availability = ChatBackendAvailability.Ready,
            StatusMessage = "Ready",
        };
        backendState.Models.Add(new AgentModelInfo("gpt-4.1"));
        backendState.Models.Add(new AgentModelInfo("gpt-4o"));

        var reused = ProviderFrontendCoordinator.TryBuildActiveBackendTestResult(
            definition,
            new Dictionary<string, ChatBackendState>(StringComparer.OrdinalIgnoreCase)
            {
                [definition.ProviderKey] = backendState,
            },
            out var result);

        Assert.IsTrue(reused);
        Assert.IsTrue(result.Success);
        Assert.AreEqual(2, result.ModelCount);
        StringAssert.Contains(result.Message, "Using active provider backend");
    }

    [TestMethod]
    public void TryBuildActiveBackendTestResult_UsesFailureStatusForReservedBackend()
    {
        var definition = new CodeAltaProviderDocument
        {
            ProviderKey = "codex_cli",
            ProviderType = "codex_cli",
        };
        var backendState = new ChatBackendState(AgentBackendIds.Codex, "Codex")
        {
            Availability = ChatBackendAvailability.Failed,
            StatusMessage = "Codex startup failed.",
        };

        var reused = ProviderFrontendCoordinator.TryBuildActiveBackendTestResult(
            definition,
            new Dictionary<string, ChatBackendState>(StringComparer.OrdinalIgnoreCase)
            {
                [definition.ProviderKey] = backendState,
            },
            out var result);

        Assert.IsTrue(reused);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(0, result.ModelCount);
        Assert.AreEqual("Codex startup failed.", result.Message);
    }

    [TestMethod]
    public void TryBuildActiveBackendTestResult_DoesNotReuseNonReservedProvider()
    {
        var definition = new CodeAltaProviderDocument
        {
            ProviderKey = "openai",
            ProviderType = "openai-chat",
        };
        var backendState = new ChatBackendState(new AgentBackendId("openai"), "OpenAI")
        {
            Availability = ChatBackendAvailability.Ready,
            StatusMessage = "Ready",
        };
        backendState.Models.Add(new AgentModelInfo("gpt-4.1"));

        var reused = ProviderFrontendCoordinator.TryBuildActiveBackendTestResult(
            definition,
            new Dictionary<string, ChatBackendState>(StringComparer.OrdinalIgnoreCase)
            {
                [definition.ProviderKey] = backendState,
            },
            out var result);

        Assert.IsFalse(reused);
        Assert.AreEqual(default, result);
    }

    [TestMethod]
    public void IsTemporarilyDisabledCopilotProvider_AppliesOnlyToCliProvider()
    {
        Assert.IsTrue(ProviderFrontendCoordinator.IsTemporarilyDisabledCopilotProvider(new CodeAltaProviderDocument
        {
            ProviderKey = "copilot_cli",
            ProviderType = "copilot_cli",
        }));
        Assert.IsFalse(ProviderFrontendCoordinator.IsTemporarilyDisabledCopilotProvider(new CodeAltaProviderDocument
        {
            ProviderKey = "copilot",
            ProviderType = "copilot",
        }));
    }
}
