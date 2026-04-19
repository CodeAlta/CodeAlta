using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Compaction;

namespace CodeAlta.Tests;

[TestClass]
public sealed class LocalAgentRuntimeContractsTests
{
    [TestMethod]
    public void LocalAgentRuntimePathLayout_UsesDateShardedSessionJournalStructure()
    {
        var layout = new LocalAgentRuntimePathLayout(@"C:\codealta-test-root\.alta");
        var createdAt = DateTimeOffset.Parse("2026-04-06T14:15:00+00:00");

        var providerRoot = layout.GetProviderRootPath("openai", "openrouter");
        var providerDescriptorPath = layout.GetProviderDescriptorPath("openai", "openrouter");
        var sessionFile = layout.GetSessionFilePath("session-123", createdAt);

        Assert.AreEqual(@"C:\codealta-test-root\.alta\providers\openai\openrouter", providerRoot);
        Assert.AreEqual(Path.Combine(providerRoot, "provider.json"), providerDescriptorPath);
        Assert.AreEqual(
            @"C:\codealta-test-root\.alta\sessions\2026\04\06\session-123.jsonl",
            sessionFile);
    }

    [TestMethod]
    public void LocalAgentProviderDescriptor_ToJson_SerializesProfile()
    {
        var descriptor = new LocalAgentProviderDescriptor
        {
            ProtocolFamily = "openai",
            ProviderKey = "openai",
            DisplayName = "OpenAI",
            BackendId = AgentBackendIds.OpenAIResponses,
            TransportKind = LocalAgentTransportKind.OpenAIResponses,
            BaseUri = new Uri("https://api.openai.com/v1"),
            IsDefault = true,
            Profile = new LocalAgentProviderProfile
            {
                SupportsDeveloperRole = true,
                SupportsStore = true,
                SupportsReasoningEffort = true,
                MaxTokensFieldName = "max_completion_tokens",
                ReasoningFieldNames = ["reasoning"],
            },
            Compaction = LocalAgentCompactionSettings.Default,
        };

        using var document = JsonDocument.Parse(descriptor.ToJson());
        var root = document.RootElement;

        Assert.AreEqual("openai", root.GetProperty("protocolFamily").GetString());
        Assert.AreEqual("openai-responses", root.GetProperty("backendId").GetString());
        Assert.AreEqual("OpenAIResponses", root.GetProperty("transportKind").GetString());
        Assert.AreEqual("max_completion_tokens", root.GetProperty("profile").GetProperty("maxTokensFieldName").GetString());
        Assert.AreEqual(0.85d, root.GetProperty("compaction").GetProperty("triggerThreshold").GetDouble(), 0.0001d);
        Assert.AreEqual(20000, root.GetProperty("compaction").GetProperty("recentSuffixTargetTokens").GetInt32());
        Assert.AreEqual(1024, root.GetProperty("compaction").GetProperty("summaryOutputTokens").GetInt32());
    }

    [TestMethod]
    public void LocalAgentSessionState_ToJson_SerializesProviderState()
    {
        using var providerState = JsonDocument.Parse("""{"responseId":"resp_123","cursor":17}""");
        var state = new LocalAgentSessionState
        {
            SessionId = "session-1",
            ProtocolFamily = "openai",
            ProviderKey = "openai",
            ProviderSessionId = "resp_123",
            CompactionEventOffset = 17,
            InstructionHash = "sha256:abc",
            CompactionCheckpointEventId = "compaction:1",
            LastCompactedAt = DateTimeOffset.Parse("2026-04-06T14:29:00+00:00"),
            LastCompactionTrigger = "threshold",
            LastCompactionTokensBefore = 2048,
            LastCompactionTokensAfter = 1024,
            ProviderState = providerState.RootElement.Clone(),
            UpdatedAt = DateTimeOffset.Parse("2026-04-06T14:30:00+00:00"),
        };

        using var document = JsonDocument.Parse(state.ToJson());
        var root = document.RootElement;

        Assert.AreEqual("resp_123", root.GetProperty("providerSessionId").GetString());
        Assert.AreEqual(17, root.GetProperty("compactionEventOffset").GetInt64());
        Assert.AreEqual("compaction:1", root.GetProperty("compactionCheckpointEventId").GetString());
        Assert.AreEqual("sha256:abc", root.GetProperty("instructionHash").GetString());
        Assert.AreEqual("threshold", root.GetProperty("lastCompactionTrigger").GetString());
        Assert.AreEqual(2048, root.GetProperty("lastCompactionTokensBefore").GetInt64());
        Assert.AreEqual(1024, root.GetProperty("lastCompactionTokensAfter").GetInt64());
        Assert.AreEqual(17, root.GetProperty("providerState").GetProperty("cursor").GetInt32());
    }
}
