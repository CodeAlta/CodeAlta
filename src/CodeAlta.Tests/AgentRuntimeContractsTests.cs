using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentRuntimeContractsTests
{
    [TestMethod]
    public void AgentRuntimePathLayout_UsesDateShardedSessionJournalStructure()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "codealta-test-root", ".alta");
        var layout = new AgentRuntimePathLayout(rootPath);
        var createdAt = DateTimeOffset.Parse("2026-04-06T14:15:00+00:00");
        var sessionFile = layout.GetSessionFilePath("session-123", createdAt);

        Assert.AreEqual(
            Path.Combine(rootPath, "sessions", "2026", "04", "06", "session-123.jsonl"),
            sessionFile);
    }

    [TestMethod]
    public void ModelProviderRuntimeDescriptor_ToJson_SerializesProfile()
    {
        var descriptor = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = "openai",
            ProviderKey = "openai",
            DisplayName = "OpenAI",
            TransportKind = AgentTransportKind.OpenAIResponses,
            BaseUri = new Uri("https://api.openai.com/v1"),
            IsDefault = true,
            Profile = new AgentProviderProfile
            {
                SupportsDeveloperRole = true,
                SupportsStore = true,
                SupportsReasoningEffort = true,
                MaxTokensFieldName = "max_completion_tokens",
                ReasoningFieldNames = ["reasoning"],
            },
            Compaction = AgentCompactionSettings.Default,
        };

        using var document = JsonDocument.Parse(descriptor.ToJson());
        var root = document.RootElement;

        Assert.AreEqual("openai", root.GetProperty("protocolFamily").GetString());
        Assert.IsFalse(root.TryGetProperty("ProviderId", out _));
        Assert.AreEqual("OpenAIResponses", root.GetProperty("transportKind").GetString());
        Assert.AreEqual("max_completion_tokens", root.GetProperty("profile").GetProperty("maxTokensFieldName").GetString());
        Assert.AreEqual(0.95d, root.GetProperty("compaction").GetProperty("ratio").GetDouble(), 0.0001d);
    }

    [TestMethod]
    public void AgentSessionState_ToJson_SerializesProviderState()
    {
        using var providerState = JsonDocument.Parse("""{"responseId":"resp_123","cursor":17}""");
        var state = new AgentSessionState
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

    [TestMethod]
    public void AgentReasoningReplay_SameProviderAndModel_PreservesProtectedReasoning()
    {
        var request = CreateTurnRequest("openai", "openai", AgentTransportKind.OpenAIResponses, "gpt-5.5");
        var provenance = AgentReasoningReplay.CreateProvenance(request);
        var reasoning = new AgentMessagePart.Reasoning("summary", "ciphertext", provenance);
        var conversation = new[]
        {
            new AgentConversationMessage(AgentConversationRole.Assistant, [reasoning]),
        };

        var sanitized = AgentReasoningReplay.SanitizeForRequest(conversation, request);

        Assert.AreSame(conversation, sanitized);
        var sanitizedReasoning = (AgentMessagePart.Reasoning)sanitized[0].Parts[0];
        Assert.AreEqual("ciphertext", sanitizedReasoning.ProtectedData);
        Assert.AreEqual(provenance, sanitizedReasoning.Provenance);
    }

    [TestMethod]
    public void AgentReasoningReplay_DifferentProvider_DowngradesReasoningToText()
    {
        var source = CreateTurnRequest("openai-responses", "copilot", AgentTransportKind.OpenAIResponses, "gpt-5.5");
        var target = CreateTurnRequest("openai-responses", "codex", AgentTransportKind.OpenAIResponses, "gpt-5.5");
        var conversation = new[]
        {
            new AgentConversationMessage(
                AgentConversationRole.Assistant,
                [
                    new AgentMessagePart.Reasoning("summary", "ciphertext", AgentReasoningReplay.CreateProvenance(source)),
                    new AgentMessagePart.Text("visible answer"),
                ]),
        };

        var sanitized = AgentReasoningReplay.SanitizeForRequest(conversation, target);

        Assert.AreNotSame(conversation, sanitized);
        Assert.IsInstanceOfType<AgentMessagePart.Text>(sanitized[0].Parts[0]);
        Assert.AreEqual("<assistant_reasoning_summary>summary</assistant_reasoning_summary>", ((AgentMessagePart.Text)sanitized[0].Parts[0]).Value);
        Assert.AreEqual("visible answer", ((AgentMessagePart.Text)sanitized[0].Parts[1]).Value);
    }

    [TestMethod]
    public void AgentReasoningReplay_DifferentModel_DropsOpaqueReasoningWithoutSummary()
    {
        var source = CreateTurnRequest("anthropic", "anthropic", AgentTransportKind.AnthropicMessages, "claude-sonnet-4.5");
        var target = CreateTurnRequest("anthropic", "anthropic", AgentTransportKind.AnthropicMessages, "claude-opus-4.5");
        var conversation = new[]
        {
            new AgentConversationMessage(
                AgentConversationRole.Assistant,
                [
                    new AgentMessagePart.Reasoning(null, "signature", AgentReasoningReplay.CreateProvenance(source)),
                    new AgentMessagePart.ToolCall("call-1", "read_file", JsonDocument.Parse("{}").RootElement.Clone()),
                ]),
        };

        var sanitized = AgentReasoningReplay.SanitizeForRequest(conversation, target);

        Assert.AreEqual(1, sanitized[0].Parts.Count);
        Assert.IsInstanceOfType<AgentMessagePart.ToolCall>(sanitized[0].Parts[0]);
    }

    [TestMethod]
    public void AgentReasoningReplay_LegacyReasoningWithoutProvenance_DowngradesToText()
    {
        var target = CreateTurnRequest("openai-responses", "openai", AgentTransportKind.OpenAIResponses, "gpt-5.5");
        var conversation = new[]
        {
            new AgentConversationMessage(
                AgentConversationRole.Assistant,
                [new AgentMessagePart.Reasoning("legacy summary", "legacy-ciphertext")]),
        };

        var sanitized = AgentReasoningReplay.SanitizeForRequest(conversation, target);

        Assert.IsInstanceOfType<AgentMessagePart.Text>(sanitized[0].Parts[0]);
        Assert.AreEqual("<assistant_reasoning_summary>legacy summary</assistant_reasoning_summary>", ((AgentMessagePart.Text)sanitized[0].Parts[0]).Value);
    }

    private static AgentTurnRequest CreateTurnRequest(
        string protocolFamily,
        string providerKey,
        AgentTransportKind transportKind,
        string modelId)
    {
        var provider = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = protocolFamily,
            ProviderKey = providerKey,
            DisplayName = providerKey,
            TransportKind = transportKind,
        };

        return new AgentTurnRequest
        {
            Provider = provider,
            ProviderId = new ModelProviderId(provider.ProviderKey),
            SessionId = "session-1",
            RunId = new AgentRunId("run-1"),
            ModelId = modelId,
            Conversation = [],
            Tools = [],
            State = new AgentSessionState
            {
                SessionId = "session-1",
                ProtocolFamily = protocolFamily,
                ProviderKey = providerKey,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        };
    }
}
