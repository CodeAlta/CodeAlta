using System.Text.Json;
using CodeAlta.Acp;
using CodeAlta.Agent;
using CodeAlta.Plugin.Acp;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AcpSessionUpdatesTests
{
    private static readonly Dictionary<string, AgentToolDefinition> NoTools = new(StringComparer.Ordinal);

    [TestMethod]
    public void Chunks_AreReadBackByTheClient()
    {
        var message = AcpSessionUpdates.AgentMessageChunk("Hello");
        var thought = AcpSessionUpdates.AgentThoughtChunk("Thinking");
        var user = AcpSessionUpdates.UserMessageChunk(ContentBlock.FromImage("aGVsbG8=", "image/png"));

        Assert.AreEqual("agent_message_chunk", message.Kind);
        Assert.AreEqual("Hello", AcpPrompts.RenderContentBlock(message.Value.GetProperty("content")));
        Assert.AreEqual("agent_thought_chunk", thought.Kind);
        Assert.AreEqual("Thinking", AcpPrompts.RenderContentBlock(thought.Value.GetProperty("content")));
        Assert.AreEqual("user_message_chunk", user.Kind);
        Assert.AreEqual("[image]", AcpPrompts.RenderContentBlock(user.Value.GetProperty("content")));
    }

    [TestMethod]
    public void ToolCall_IsMappedByTheClientFromItsKindAndLocations()
    {
        var update = AcpSessionUpdates.ToolCall(new ToolCall
        {
            ToolCallId = "call-1",
            Title = "Edit Program.cs",
            Kind = ToolKind.Edit,
            Status = ToolCallStatus.Pending,
            Locations = [new ToolCallLocation { Path = "/work/Program.cs", Line = 3 }],
            RawInput = AcpJson.Parse("""{"old":"a","new":"b"}"""),
        });

        Assert.AreEqual("tool_call", update.Kind);
        Assert.AreEqual("pending", AcpJson.GetString(update.Value, "status"));
        Assert.AreEqual(3, update.Value.GetProperty("locations")[0].GetProperty("line").GetInt32());
        var (name, arguments, codeAlta) = AcpToolCalls.Map(update.Value, NoTools, "/work");
        Assert.AreEqual("Edit", name);
        Assert.IsFalse(codeAlta);
        Assert.AreEqual("Edit Program.cs", AcpJson.GetString(arguments, "title"));
        Assert.AreEqual("a", AcpJson.GetString(arguments, "old"));
    }

    [TestMethod]
    public void ToolCall_LeavesOutAKindAndAStatusThatAreNotSet()
    {
        var update = AcpSessionUpdates.ToolCall(new ToolCall { ToolCallId = "call-1", Title = "Something" });

        Assert.IsFalse(update.Value.TryGetProperty("kind", out _));
        Assert.IsFalse(update.Value.TryGetProperty("status", out _));
        Assert.IsFalse(update.Value.TryGetProperty("content", out _));
    }

    [TestMethod]
    public void ToolCallUpdate_WithTextAndDiff_IsTheResultOfTheCall()
    {
        var update = AcpSessionUpdates.ToolCallUpdate(new ToolCallUpdate
        {
            ToolCallId = "call-1",
            Status = ToolCallStatus.Completed,
            Content =
            [
                ToolCallContent.FromText("Done."),
                ToolCallContent.FromDiff("/work/Program.cs", "a", "b"),
                ToolCallContent.FromDiff("/work/New.cs", null, "c"),
            ],
        });

        Assert.AreEqual("tool_call_update", update.Kind);
        Assert.AreEqual("completed", AcpJson.GetString(update.Value, "status"));
        Assert.IsFalse(update.Value.TryGetProperty("title", out _));
        var result = AcpToolCalls.CreateResult(update.Value, success: true);
        var text = ((AgentToolResultItem.Text)result.Items.Single()).Value;
        Assert.AreEqual("Done.\nEdited /work/Program.cs.\nCreated /work/New.cs.", text);
    }

    [TestMethod]
    public void Plan_IsRenderedAsAChecklist()
    {
        var update = AcpSessionUpdates.Plan(
        [
            new PlanEntry { Content = "Read", Status = PlanEntryStatus.Completed, Priority = PlanEntryPriority.High },
            new PlanEntry { Content = "Fix", Status = PlanEntryStatus.InProgress, Priority = PlanEntryPriority.Medium },
            new PlanEntry { Content = "Test", Status = PlanEntryStatus.Pending, Priority = PlanEntryPriority.Low },
        ]);

        Assert.AreEqual("- [x] Read\n- [~] Fix\n- [ ] Test", AcpToolCalls.RenderPlan(update.Value).ReplaceLineEndings("\n"));
        Assert.AreEqual("low", update.Value.GetProperty("entries")[2].GetProperty("priority").GetString());
    }

    [TestMethod]
    public void OtherUpdates_HaveTheirKindAndFields()
    {
        var commands = AcpSessionUpdates.AvailableCommands([new AvailableCommand { Name = "review", Description = "Review the diff" }]);
        var mode = AcpSessionUpdates.CurrentMode("plan");
        var usage = AcpSessionUpdates.Usage(1200, 200000, new Cost { Amount = 0.25, Currency = "USD" });
        var info = AcpSessionUpdates.SessionInfo("Fix the parser", updatedAt: null);

        Assert.AreEqual("available_commands_update", commands.Kind);
        Assert.AreEqual("review", commands.Value.GetProperty("availableCommands")[0].GetProperty("name").GetString());
        Assert.AreEqual("current_mode_update", mode.Kind);
        Assert.AreEqual("plan", AcpJson.GetString(mode.Value, "currentModeId"));
        Assert.AreEqual("usage_update", usage.Kind);
        Assert.AreEqual(1200, AcpJson.GetInt64(usage.Value, "used"));
        Assert.AreEqual(200000, AcpJson.GetInt64(usage.Value, "size"));
        Assert.AreEqual(0.25, AcpJson.GetDouble(usage.Value.GetProperty("cost"), "amount"));
        Assert.AreEqual("session_info_update", info.Kind);
        Assert.AreEqual("Fix the parser", AcpJson.GetString(info.Value, "title"));
        Assert.IsFalse(info.Value.TryGetProperty("updatedAt", out _));
    }

    [TestMethod]
    public void ConfigOptions_AreReadBackAsModelsAndEfforts()
    {
        var update = AcpSessionUpdates.ConfigOptions(
        [
            SessionConfigOption.Select("model", "Model", "model", "smart",
            [
                new SessionConfigSelectOption { Value = "fast", Name = "Fast" },
                new SessionConfigSelectOption { Value = "smart", Name = "Smart", Description = "The best one" },
            ]),
            SessionConfigOption.Select("reasoning", "Reasoning", "thought_level", "high",
            [
                new SessionConfigSelectOption { Value = "low", Name = "Low" },
                new SessionConfigSelectOption { Value = "high", Name = "High" },
            ]),
            SessionConfigOption.Select("mode", "Mode", "The agent prompt", "mode", "default",
            [
                new SessionConfigSelectOption { Value = "default", Name = "Default" },
                new SessionConfigSelectOption { Value = "plan", Name = "Plan" },
            ]),
        ]);

        Assert.AreEqual("config_option_update", update.Kind);
        var options = update.Value.GetProperty("configOptions");
        Assert.AreEqual("select", AcpJson.GetString(options[0], "type"));
        Assert.AreEqual("The agent prompt", AcpJson.GetString(options[2], "description"));
        var models = AcpConfigOptions.ReadModels(options);
        Assert.HasCount(2, models);
        Assert.AreEqual("smart", models[1].Id);
        Assert.AreEqual("The best one", models[1].Description);
        CollectionAssert.AreEqual(new[] { AgentReasoningEffort.Low, AgentReasoningEffort.High }, models[1].Efforts!.ToArray());
        Assert.AreEqual(AgentReasoningEffort.High, AcpConfigOptions.CurrentEffort(options));
        Assert.AreEqual("default", AcpJson.GetString(AcpConfigOptions.Find(options, "mode")!.Value, "currentValue"));
    }

    [TestMethod]
    public void Builders_RejectValuesThatAreNotDefined()
    {
        Assert.ThrowsExactly<ArgumentException>(() => AcpSessionUpdates.ToolCallUpdate(new ToolCallUpdate { ToolCallId = "call-1", Kind = default(ToolKind) }));
        Assert.ThrowsExactly<ArgumentException>(() => AcpSessionUpdates.ToolCallUpdate(new ToolCallUpdate { ToolCallId = "call-1", Content = [default] }));
        Assert.Throws<ArgumentException>(() => AcpSessionUpdates.ToolCall(new ToolCall { Title = "No id" }));
        Assert.ThrowsExactly<ArgumentException>(() => AcpSessionUpdates.Plan([new PlanEntry { Content = "No status", Priority = PlanEntryPriority.Low }]));
        Assert.ThrowsExactly<ArgumentException>(() => AcpSessionUpdates.AgentMessageChunk(default(ContentBlock)));
        Assert.ThrowsExactly<ArgumentException>(() => AcpSessionUpdates.ConfigOptions([default]));
        Assert.ThrowsExactly<ArgumentException>(() => SessionConfigOption.Select("model", "Model", "model", "missing",
            [new SessionConfigSelectOption { Value = "fast", Name = "Fast" }]));
        Assert.ThrowsExactly<ArgumentException>(() => SessionConfigOption.Select("model", "Model", "model", "fast", []));
    }

    [TestMethod]
    public void StopReason_IsWrittenAsItsName()
    {
        var options = AcpClient.CreateJsonSerializerOptions();

        var json = JsonSerializer.Serialize(new PromptResponse { StopReason = StopReason.Cancelled }, options);

        Assert.AreEqual("""{"stopReason":"cancelled"}""", json);
        Assert.IsFalse(default(StopReason).IsDefined);
        Assert.IsTrue(StopReason.MaxTokens.IsDefined);
    }

    [TestMethod]
    public void ContentParts_AreReadFromThePrompt()
    {
        var blocks = new List<ContentBlock>
        {
            ContentBlock.FromText("Fix it"),
            ContentBlock.FromImage("aGVsbG8=", "image/png"),
            ContentBlock.FromResourceLink("file:///work/a.cs", "a.cs"),
            new() { Value = AcpJson.Parse("""{"type":"resource","resource":{"uri":"file:///work/b.cs","mimeType":"text/x-csharp","text":"class B {}"}}""") },
            new() { Value = AcpJson.Parse("""{"type":"resource","resource":{"uri":"file:///work/c.bin","blob":"AAE="}}""") },
            new() { Value = AcpJson.Parse("""{"type":"audio","data":"UklGRg==","mimeType":"audio/wav"}""") },
            new() { Value = AcpJson.Parse("""{"type":"hologram"}""") },
        };

        var parts = AcpContentPart.ReadAll(blocks);

        Assert.HasCount(6, parts);
        Assert.AreEqual(new AcpContentPart.Text("Fix it"), parts[0]);
        Assert.AreEqual(new AcpContentPart.Image("aGVsbG8=", "image/png", null), parts[1]);
        Assert.AreEqual(new AcpContentPart.ResourceLink("file:///work/a.cs", "a.cs", null, null), parts[2]);
        Assert.AreEqual(new AcpContentPart.Resource("file:///work/b.cs", "text/x-csharp", "class B {}", null), parts[3]);
        Assert.AreEqual(new AcpContentPart.Resource("file:///work/c.bin", null, null, "AAE="), parts[4]);
        Assert.AreEqual(new AcpContentPart.Audio("UklGRg==", "audio/wav"), parts[5]);
    }

    [TestMethod]
    public void SetConfigOptionRequest_IsReadFromTheRequestTheClientSends()
    {
        var request = AcpConfigOptions.CreateSetRequest("session-1", "model", "smart");

        Assert.AreEqual("session-1", request.SessionId);
        Assert.AreEqual("model", request.ConfigId);
        Assert.AreEqual("smart", request.StringValue);
        Assert.AreEqual(JsonValueKind.String, request.OptionValue.ValueKind);
    }
}
