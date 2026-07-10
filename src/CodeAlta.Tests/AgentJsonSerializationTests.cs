using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentJsonSerializationTests
{
    [TestMethod]
    public void AgentEvent_ToJson_SerializesDerivedPermissionRequest()
    {
        AgentEvent @event = new AgentCommandPermissionRequest(
            new ModelProviderId("codex"),
            "session-1",
            DateTimeOffset.Parse("2026-03-07T10:15:00+00:00"),
            new AgentRunId("run-1"),
            "interaction-1",
            "approval-1",
            "rg TODO .",
            @"C:\repo",
            [new AgentCommandPreviewAction(AgentCommandPreviewKind.Search, "rg TODO .", Query: "TODO")],
            "Inspect TODOs.",
            new AgentNetworkAccessRequest("api.example.com", "https"),
            ["read-only"],
            [new AgentNetworkPolicyAmendment(AgentNetworkPolicyAction.Allow, "api.example.com")]);

        using var document = JsonDocument.Parse(@event.ToJson());
        var root = document.RootElement;

        Assert.AreEqual("permissionCommand", root.GetProperty("$type").GetString());
        Assert.AreEqual("codex", root.GetProperty("backendId").GetString());
        Assert.AreEqual("run-1", root.GetProperty("runId").GetString());
        Assert.AreEqual("rg TODO .", root.GetProperty("command").GetString());
        Assert.AreEqual("Search", root.GetProperty("actions")[0].GetProperty("kind").GetString());
        Assert.AreEqual("api.example.com", root.GetProperty("network").GetProperty("host").GetString());
    }

    [TestMethod]
    public void AgentEvent_ToJson_SerializesSystemPromptEvent()
    {
        AgentEvent @event = new AgentSystemPromptEvent(
            new ModelProviderId("local"),
            "session-1",
            DateTimeOffset.Parse("2026-04-28T12:34:56+00:00"),
            new AgentRunId("run-1"),
            "session_start",
            "sha256:abc",
            "default",
            "System text",
            "Developer text",
            new AgentSystemPromptProviderPayloadSummary("native-system-and-developer", true, false),
            null,
            new AgentSystemPromptStatistics(3, 4, 7, 11, 14),
            new AgentSystemPromptChangeSummary("initial", ["system"], [], []),
            new AgentPromptUsageInfo("default", "Default", @"C:\repo\.alta\prompts\agents\default.prompt.md"));

        using var document = JsonDocument.Parse(@event.ToJson());
        var root = document.RootElement;

        Assert.AreEqual("system_prompt", root.GetProperty("$type").GetString());
        Assert.AreEqual("sha256:abc", root.GetProperty("effectivePromptHash").GetString());
        Assert.AreEqual("default", root.GetProperty("agentPromptId").GetString());
        Assert.AreEqual("System text", root.GetProperty("systemMessage").GetString());
        Assert.AreEqual("Default", root.GetProperty("agentPromptUsage").GetProperty("displayName").GetString());
        Assert.AreEqual(@"C:\repo\.alta\prompts\agents\default.prompt.md", root.GetProperty("agentPromptUsage").GetProperty("sourcePath").GetString());
        Assert.AreEqual("native-system-and-developer", root.GetProperty("providerPayloadSummary").GetProperty("channelMapping").GetString());
        Assert.AreEqual(7, root.GetProperty("statistics").GetProperty("totalApproxTokens").GetInt32());
    }

    [TestMethod]
    public void AgentInput_ToJson_SerializesPolymorphicItems()
    {
        var input = new AgentInput(
        [
            new AgentInputItem.Text("hello"),
            new AgentInputItem.LocalImage(@"C:\images\screen.png", "Screen", "image/png"),
            new AgentInputItem.File("Program.cs", "Program.cs", new AgentLineRange(3, 9)),
            new AgentInputItem.Selection(
                "App.cs",
                "Selected block",
                "Console.WriteLine(\"hi\");",
                new AgentSelectionRange(
                    new AgentPosition(10, 2),
                    new AgentPosition(12, 1)))
        ]);

        using var document = JsonDocument.Parse(input.ToJson());
        var items = document.RootElement.GetProperty("items");

        Assert.AreEqual("text", items[0].GetProperty("$type").GetString());
        Assert.AreEqual("hello", items[0].GetProperty("value").GetString());
        Assert.AreEqual("localImage", items[1].GetProperty("$type").GetString());
        Assert.AreEqual(@"C:\images\screen.png", items[1].GetProperty("path").GetString());
        Assert.AreEqual("Screen", items[1].GetProperty("displayName").GetString());
        Assert.AreEqual("image/png", items[1].GetProperty("mediaType").GetString());
        Assert.AreEqual("file", items[2].GetProperty("$type").GetString());
        Assert.AreEqual(3, items[2].GetProperty("lineRange").GetProperty("startLine").GetInt32());
        Assert.AreEqual("selection", items[3].GetProperty("$type").GetString());
        Assert.AreEqual("App.cs", items[3].GetProperty("filePath").GetString());
    }

    [TestMethod]
    public void AgentSteerOptions_ToJson_SerializesExpectedRunId()
    {
        var options = new AgentSteerOptions
        {
            Input = AgentInput.Text("continue"),
            ExpectedRunId = new AgentRunId("turn-7")
        };

        using var document = JsonDocument.Parse(options.ToJson());
        var root = document.RootElement;

        Assert.AreEqual("turn-7", root.GetProperty("expectedRunId").GetString());
        Assert.AreEqual("text", root.GetProperty("input").GetProperty("items")[0].GetProperty("$type").GetString());
    }

    [TestMethod]
    public void AgentMcpServerConfig_ToJson_SerializesDerivedRemoteConfig()
    {
        AgentMcpServerConfig config = new AgentRemoteMcpServerConfig("https://example.com/mcp")
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["X-Test"] = "42"
            },
            EnabledTools = ["lookup"],
            ToolTimeout = TimeSpan.FromSeconds(12),
            Required = true
        };

        using var document = JsonDocument.Parse(config.ToJson());
        var root = document.RootElement;

        Assert.AreEqual("remote", root.GetProperty("$type").GetString());
        Assert.AreEqual("https://example.com/mcp", root.GetProperty("url").GetString());
        Assert.AreEqual("Http", root.GetProperty("transport").GetString());
        Assert.AreEqual("42", root.GetProperty("headers").GetProperty("X-Test").GetString());
        Assert.AreEqual("lookup", root.GetProperty("enabledTools")[0].GetString());
        Assert.IsTrue(root.GetProperty("required").GetBoolean());
    }

    [TestMethod]
    public void AgentToolResult_ToJson_SerializesDerivedItems()
    {
        var result = new AgentToolResult(
            true,
            [
                new AgentToolResultItem.Text("done"),
                new AgentToolResultItem.ImageUrl("https://example.com/image.png")
            ]);

        var json = result.ToJson(indented: true);

        Assert.IsTrue(json.Contains(Environment.NewLine, StringComparison.Ordinal));

        using var document = JsonDocument.Parse(json);
        var items = document.RootElement.GetProperty("items");
        Assert.AreEqual("text", items[0].GetProperty("$type").GetString());
        Assert.AreEqual("done", items[0].GetProperty("value").GetString());
        Assert.AreEqual("imageUrl", items[1].GetProperty("$type").GetString());
        Assert.AreEqual("https://example.com/image.png", items[1].GetProperty("url").GetString());
    }

    [TestMethod]
    public void AgentSessionMetadata_ToJson_SerializesProviderIdentityAndRawApiDetails()
    {
        var metadata = new AgentSessionMetadata(
            "session-1",
            DateTimeOffset.Parse("2026-04-06T10:00:00+00:00"),
            DateTimeOffset.Parse("2026-04-06T11:00:00+00:00"),
            Summary: "OpenAI provider-backed session",
            Context: new AgentSessionContext(Cwd: @"C:\code\CodeAlta"),
            WorkspacePath: @"C:\code\CodeAlta",
            Details: new RawApiSessionMetadataDetails(
                ProviderDisplayName: "OpenAI",
                ProviderBaseUri: "https://api.openai.com/v1",
                ProviderSessionId: "resp_123",
                Title: "Implement runtime"),
            ProtocolFamily: "openai",
            ProviderKey: "openai",
            ModelId: "gpt-5.4");

        using var document = JsonDocument.Parse(metadata.ToJson());
        var root = document.RootElement;

        Assert.AreEqual("openai", root.GetProperty("protocolFamily").GetString());
        Assert.AreEqual("openai", root.GetProperty("providerKey").GetString());
        Assert.AreEqual("gpt-5.4", root.GetProperty("modelId").GetString());
        Assert.AreEqual("rawApi", root.GetProperty("details").GetProperty("$type").GetString());
        Assert.AreEqual("resp_123", root.GetProperty("details").GetProperty("providerSessionId").GetString());
    }

    [TestMethod]
    public void AgentContentCompletedEvent_ToJson_SerializesStructuredContentDetails()
    {
        using var detailsDocument = JsonDocument.Parse(
            """
            {
              "providerItemId": "item_123",
              "thoughtSignature": "abc"
            }
            """);
        var @event = new AgentContentCompletedEvent(
            ModelProviderIds.GoogleGenAI,
            "session-1",
            DateTimeOffset.Parse("2026-04-06T10:30:00+00:00"),
            new AgentRunId("run-1"),
            AgentContentKind.Reasoning,
            "reasoning-1",
            null,
            "Reasoning complete.",
            detailsDocument.RootElement.Clone());

        using var document = JsonDocument.Parse(@event.ToJson());
        var root = document.RootElement;

        Assert.AreEqual("contentCompleted", root.GetProperty("$type").GetString());
        Assert.AreEqual("item_123", root.GetProperty("details").GetProperty("providerItemId").GetString());
        Assert.AreEqual("abc", root.GetProperty("details").GetProperty("thoughtSignature").GetString());
    }

    [TestMethod]
    public void AgentContentCompletedEvent_ToJson_SerializesAskIdForUserPromptCorrelation()
    {
        var @event = new AgentContentCompletedEvent(
            ModelProviderIds.Codex,
            "session-1",
            DateTimeOffset.Parse("2026-04-06T10:30:00+00:00"),
            new AgentRunId("run-1"),
            AgentContentKind.User,
            "user-1",
            null,
            "# Ask response\n\nProceed.",
            AskId: "ask-123");

        using var document = JsonDocument.Parse(@event.ToJson());
        var root = document.RootElement;

        Assert.AreEqual("contentCompleted", root.GetProperty("$type").GetString());
        Assert.AreEqual("ask-123", root.GetProperty("ask_id").GetString());
        Assert.AreEqual("# Ask response\n\nProceed.", root.GetProperty("content").GetString());
    }

    [TestMethod]
    public void AgentConversationMessage_ToJson_SerializesPolymorphicParts()
    {
        using var argumentsDocument = JsonDocument.Parse("""{"path":"Program.cs"}""");
        var message = new AgentConversationMessage(
            AgentConversationRole.Assistant,
            [
                new AgentMessagePart.Reasoning("Inspecting the file."),
                new AgentMessagePart.ToolCall("call-1", "read_file", argumentsDocument.RootElement.Clone()),
                new AgentMessagePart.ToolResult("call-1", new AgentToolResult(true, [new AgentToolResultItem.Text("done")]))
            ]);

        var json = JsonSerializer.Serialize(message, AgentJsonSerializerContext.Default.AgentConversationMessage);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.AreEqual("Assistant", root.GetProperty("role").GetString());
        Assert.AreEqual("reasoning", root.GetProperty("parts")[0].GetProperty("$type").GetString());
        Assert.AreEqual("toolCall", root.GetProperty("parts")[1].GetProperty("$type").GetString());
        Assert.AreEqual("Program.cs", root.GetProperty("parts")[1].GetProperty("arguments").GetProperty("path").GetString());
        Assert.AreEqual("toolResult", root.GetProperty("parts")[2].GetProperty("$type").GetString());
    }

    [TestMethod]
    public void AgentModelInfo_ToJson_SerializesCapabilitiesDictionary()
    {
        var model = new AgentModelInfo(
            "gpt-5-mini",
            DisplayName: "GPT-5 Mini",
            DefaultReasoningEffort: AgentReasoningEffort.Medium,
            SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.Medium],
            Capabilities: new Dictionary<string, object?>
            {
                ["isDefault"] = true,
                ["supportedReasoningEfforts"] = new[] { "low", "medium" },
                ["nested"] = new Dictionary<string, object?>
                {
                    ["provider"] = "openai",
                },
            });

        using var document = JsonDocument.Parse(model.ToJson());
        var root = document.RootElement;

        Assert.AreEqual("gpt-5-mini", root.GetProperty("id").GetString());
        Assert.AreEqual("Medium", root.GetProperty("defaultReasoningEffort").GetString());
        Assert.IsTrue(root.GetProperty("capabilities").GetProperty("isDefault").GetBoolean());
        Assert.AreEqual("medium", root.GetProperty("capabilities").GetProperty("supportedReasoningEfforts")[1].GetString());
        Assert.AreEqual("openai", root.GetProperty("capabilities").GetProperty("nested").GetProperty("provider").GetString());
    }

    [TestMethod]
    public void AgentErrorEvent_ToJson_SerializesExceptionDetails()
    {
        var error = new AgentErrorEvent(
            new ModelProviderId("copilot"),
            "session-1",
            DateTimeOffset.Parse("2026-03-07T10:30:00+00:00"),
            "boom",
            new InvalidOperationException("broken"),
            new AgentRunId("run-1"));

        using var document = JsonDocument.Parse(error.ToJson());
        var exception = document.RootElement.GetProperty("exceptionInfo");

        Assert.AreEqual("error", document.RootElement.GetProperty("$type").GetString());
        Assert.AreEqual("boom", document.RootElement.GetProperty("message").GetString());
        Assert.AreEqual("broken", exception.GetProperty("message").GetString());
        Assert.AreEqual(typeof(InvalidOperationException).FullName, exception.GetProperty("type").GetString());
    }

    [TestMethod]
    public void AgentEvent_ToJson_SerializesNormalizedUsageSnapshots()
    {
        var usage = new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(12345, 200000, 18, "Active context window"),
            LastOperation: new AgentOperationUsageSnapshot(
                Model: "gpt-5.4",
                InputTokens: 1200,
                OutputTokens: 300,
                Label: "Last API call"),
            RateLimits: new AgentRateLimitSummary(
                Name: "Requests",
                PlanType: "Pro",
                Primary: new AgentRateLimitWindow(42, DateTimeOffset.Parse("2026-03-18T21:00:00+00:00"), 60),
                Label: "Account rate limits"),
            Scope: AgentUsageScope.LastOperation,
            Source: AgentUsageSource.CopilotAssistantUsage,
            UpdatedAt: DateTimeOffset.Parse("2026-03-18T21:08:00+00:00"),
            Details: new CopilotSessionUsageDetails(
                QuotaSnapshots:
                [
                    new CopilotQuotaSnapshot(
                        "chat",
                        new CopilotRequestQuotaDetails(EntitlementRequests: 1500, UsedRequests: 477)),
                ]));
        var @event = new AgentSessionUpdateEvent(
            new ModelProviderId("copilot"),
            "session-1",
            DateTimeOffset.Parse("2026-03-18T21:08:00+00:00"),
            null,
            AgentSessionUpdateKind.UsageUpdated,
            "Usage updated.",
            Usage: usage);

        using var document = JsonDocument.Parse(@event.ToJson());
        var usageRoot = document.RootElement.GetProperty("usage");

        Assert.AreEqual("LastOperation", usageRoot.GetProperty("scope").GetString());
        Assert.AreEqual("CopilotAssistantUsage", usageRoot.GetProperty("source").GetString());
        Assert.AreEqual("Active context window", usageRoot.GetProperty("window").GetProperty("label").GetString());
        Assert.AreEqual("Last API call", usageRoot.GetProperty("lastOperation").GetProperty("label").GetString());
        Assert.AreEqual("request", usageRoot.GetProperty("details").GetProperty("quotaSnapshots")[0].GetProperty("details").GetProperty("$type").GetString());
    }

    [TestMethod]
    public void AgentSessionUsage_ProviderUsage_KeepsLegacySerializedSourceName()
    {
        var usage = new AgentSessionUsage(Source: AgentUsageSource.ProviderUsage);

        var json = JsonSerializer.Serialize(usage, AgentJsonSerializerContext.Default.AgentSessionUsage);
        var deserialized = JsonSerializer.Deserialize(
            """{"source":"LocalProviderUsage"}""",
            AgentJsonSerializerContext.Default.AgentSessionUsage);

        using var document = JsonDocument.Parse(json);
        Assert.AreEqual("LocalProviderUsage", document.RootElement.GetProperty("source").GetString());
        Assert.AreEqual(AgentUsageSource.ProviderUsage, deserialized?.Source);
    }

    [TestMethod]
    public void AgentTurnContracts_AndNamedCodexRateLimits_RoundTripAdditively()
    {
        var usage = new AgentSessionUsage(
            RateLimits: new AgentRateLimitSummary(Primary: new AgentRateLimitWindow(12)),
            Details: new CodexSessionUsageDetails(
                NamedRateLimits:
                [
                    new CodexRateLimitSnapshot(
                        "codex_other",
                        "Other quota",
                        "plus",
                        null,
                        new CodexRateLimitWindow(8, null, 60),
                        new CodexCreditsSnapshot(true, false, "42.5")),
                ]));
        var update = new AgentTurnSessionUpdate
        {
            Kind = AgentSessionUpdateKind.UsageUpdated,
            Message = "Usage updated.",
            Usage = usage,
        };
        var response = new AgentTurnResponse
        {
            AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, []),
            Usage = usage,
            RequiresProviderFollowUp = true,
        };

        var updateJson = JsonSerializer.Serialize(update, AgentJsonSerializerContext.Default.AgentTurnSessionUpdate);
        var responseJson = JsonSerializer.Serialize(response, AgentJsonSerializerContext.Default.AgentTurnResponse);
        var restoredUpdate = JsonSerializer.Deserialize(updateJson, AgentJsonSerializerContext.Default.AgentTurnSessionUpdate);
        var restoredResponse = JsonSerializer.Deserialize(responseJson, AgentJsonSerializerContext.Default.AgentTurnResponse);

        Assert.AreEqual("codex_other", ((CodexSessionUsageDetails?)restoredUpdate?.Usage?.Details)?.NamedRateLimits?[0].LimitId);
        Assert.AreEqual("42.5", ((CodexSessionUsageDetails?)restoredResponse?.Usage?.Details)?.NamedRateLimits?[0].Credits?.Balance);
        Assert.IsTrue(restoredResponse?.RequiresProviderFollowUp);
    }
}
