using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentToolDeferralTests
{
    [TestMethod]
    [DataRow("claude-opus-4-5-20251101", true)]
    [DataRow("claude-sonnet-4-5", true)]
    [DataRow("claude-haiku-4-5-20251001", true)]
    [DataRow("claude-opus-4-6", true)]
    [DataRow("claude-sonnet-5-5", true)]
    [DataRow("claude-fable-5-1", true)]
    [DataRow("claude-opus-5", true)]
    [DataRow("anthropic/claude-sonnet-4-5", true)]
    [DataRow("claude-opus-4-1-20250805", false)]
    [DataRow("claude-sonnet-4-20250514", false)]
    [DataRow("claude-3-7-sonnet-latest", false)]
    [DataRow("gpt-5.4", false)]
    [DataRow(null, false)]
    public void ClaudeModels_FromVersion45_TakeDeferredTools(string? model, bool expected)
        => Assert.AreEqual(expected, AgentToolDeferral.IsClaudeModelWithToolSearch(model), model);

    [TestMethod]
    public void OnlyTheClaudeApi_DefersTools_UnlessTheProfileSays()
    {
        static ModelProviderRuntimeDescriptor Provider(AgentTransportKind transport, Uri? baseUri = null, bool? supports = null) => new()
        {
            ProtocolFamily = "test", ProviderKey = "test", DisplayName = "Test", TransportKind = transport, BaseUri = baseUri,
            Profile = supports is null ? null : new AgentProviderProfile { SupportsToolDeferral = supports },
        };

        Assert.IsTrue(AgentToolDeferral.IsSupported(Provider(AgentTransportKind.AnthropicMessages), "claude-sonnet-4-5"));
        Assert.IsTrue(AgentToolDeferral.IsSupported(Provider(AgentTransportKind.AnthropicMessages, new Uri("https://api.anthropic.com")), "claude-opus-5-5"));
        Assert.IsFalse(AgentToolDeferral.IsSupported(Provider(AgentTransportKind.AnthropicMessages), "claude-3-7-sonnet"));
        Assert.IsFalse(AgentToolDeferral.IsSupported(Provider(AgentTransportKind.AnthropicMessages, new Uri("https://gateway.example/anthropic")), "claude-sonnet-4-5"));
        Assert.IsFalse(AgentToolDeferral.IsSupported(Provider(AgentTransportKind.OpenAIResponses), "claude-sonnet-4-5"));
        Assert.IsTrue(AgentToolDeferral.IsSupported(Provider(AgentTransportKind.AnthropicMessages, new Uri("https://gateway.example/anthropic"), supports: true), "any"));
        Assert.IsFalse(AgentToolDeferral.IsSupported(Provider(AgentTransportKind.AnthropicMessages, supports: false), "claude-sonnet-4-5"));
    }

    [TestMethod]
    public void AProviderThatDefersTools_IsSentThemAll_WithTheSearchTool_AndTheLoadsAsTheyAre()
    {
        var request = Request();

        var sent = AgentToolDeferral.Apply(request, defers: true);

        Assert.AreSame(request, sent);
        // Without a deferred tool, there is nothing to search.
        var plain = AgentToolDeferral.Apply(request with { Tools = [.. request.Tools.Where(static tool => !tool.Spec.Deferred)] }, defers: true);
        CollectionAssert.AreEqual(new[] { "alta" }, plain.Tools.Select(static tool => tool.Spec.Name).ToArray());
    }

    [TestMethod]
    public void AProviderWithoutDeferral_IsSentTheToolsTheConversationLoaded_AndTheLoadsAsText()
    {
        var sent = AgentToolDeferral.Apply(Request(), defers: false);

        CollectionAssert.AreEqual(new[] { "alta", "mcp__slack__post" }, sent.Tools.Select(static tool => tool.Spec.Name).ToArray());
        var items = sent.Conversation.SelectMany(static message => message.Parts).OfType<AgentMessagePart.ToolResult>().Single().Result.Items;
        Assert.IsFalse(items.OfType<AgentToolResultItem.ToolReference>().Any());
        CollectionAssert.AreEqual(new[] { "Loaded: mcp__slack__post", "Loaded tool: mcp__slack__post" }, items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value).ToArray());
        // A request with nothing deferred and no load is left as it is.
        var plain = Request() with { Tools = [Tool("alta")], Conversation = [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Hi")])] };
        Assert.AreSame(plain, AgentToolDeferral.Apply(plain, defers: false));
    }

    [TestMethod]
    public void TheJournal_KeepsALoad_InTheFormOfNoProvider()
    {
        var result = new AgentToolResult(true, [new AgentToolResultItem.Text("Loaded"), new AgentToolResultItem.ToolReference("mcp__slack__post")]);
        var json = JsonSerializer.Serialize(new AgentConversationMessage(AgentConversationRole.Tool, [new AgentMessagePart.ToolResult("call", result)]), AgentJsonSerializerContext.Default.AgentConversationMessage);

        StringAssert.Contains(json, "\"toolReference\"");
        var read = JsonSerializer.Deserialize(json, AgentJsonSerializerContext.Default.AgentConversationMessage)!;
        Assert.AreEqual("mcp__slack__post", read.Parts.OfType<AgentMessagePart.ToolResult>().Single().Result.Items.OfType<AgentToolResultItem.ToolReference>().Single().ToolName);
    }

    [TestMethod]
    public async Task ToolSearch_LoadsTheDeferredToolsItFinds_AndSaysWhichAreThereAlready()
    {
        var runTools = new AgentRunTools();
        runTools.Add([Tool("alta"), Tool("mcp__slack__post", deferred: true, "Posts a message to a Slack channel."), Tool("mcp__github__issue", deferred: true, "Creates a GitHub issue.")]);
        var search = AgentToolSearch.Create((query, _) => ValueTask.FromResult<IReadOnlyList<AgentToolSearchHit>>(
            query.Query == "slack" ? [new("mcp__slack__post", "Posts a message."), new("alta", null), new("mcp__other__x", null)] : []));
        Assert.IsTrue(search.Spec.SearchesDeferredTools);
        Assert.AreEqual(AgentToolSearch.ToolName, search.Spec.Name);

        var found = await InvokeAsync(search, runTools, new { query = "slack" });
        CollectionAssert.AreEqual(new[] { "mcp__slack__post" }, found.Items.OfType<AgentToolResultItem.ToolReference>().Select(static item => item.ToolName).ToArray());
        var text = found.Items.OfType<AgentToolResultItem.Text>().Single().Value;
        StringAssert.Contains(text, "Loaded: mcp__slack__post — Posts a message.");
        StringAssert.Contains(text, "Already loaded: alta");
        Assert.DoesNotContain("mcp__other__x", text, "A tool the session does not have is not offered.");

        // The index has nothing: the words of the deferred tools find it.
        var byWords = await InvokeAsync(search, runTools, new { query = "create an issue on github" });
        CollectionAssert.AreEqual(new[] { "mcp__github__issue" }, byWords.Items.OfType<AgentToolResultItem.ToolReference>().Select(static item => item.ToolName).ToArray());

        var none = await InvokeAsync(search, runTools, new { query = "translate poems" });
        Assert.IsTrue(none.Success);
        Assert.IsFalse(none.Items.OfType<AgentToolResultItem.ToolReference>().Any());
        Assert.IsFalse((await InvokeAsync(search, runTools, new { limit = 2 })).Success);
    }

    [TestMethod]
    public void RunTools_KnowTheirDeferredTools()
    {
        var runTools = new AgentRunTools();
        runTools.Add([Tool("alta"), Tool("mcp__slack__post", deferred: true)]);

        Assert.IsTrue(runTools.IsDeferred("mcp__slack__post"));
        Assert.IsFalse(runTools.IsDeferred("alta"));
        CollectionAssert.AreEqual(new[] { "mcp__slack__post" }, runTools.GetDeferredTools().Select(static spec => spec.Name).ToArray());
        // Registered again as loaded, it is not deferred any more.
        runTools.Set([Tool("mcp__slack__post")]);
        Assert.IsFalse(runTools.IsDeferred("mcp__slack__post"));
    }

    private static Task<AgentToolResult> InvokeAsync(AgentToolDefinition tool, AgentRunTools runTools, object arguments)
        => tool.Handler(new AgentToolInvocation(new ModelProviderId("test"), "session", "call", tool.Spec.Name, JsonSerializer.SerializeToElement(arguments)) { RunTools = runTools }, CancellationToken.None);

    internal static AgentToolDefinition Tool(string name, bool deferred = false, string? description = null)
        => new(new AgentToolSpec(name, description ?? $"The {name} tool.", JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone()) { Deferred = deferred, Group = deferred ? "mcp" : null },
            static (_, _) => Task.FromResult(new AgentToolResult(true, [])));

    private static AgentTurnRequest Request()
        => new()
        {
            Provider = new ModelProviderRuntimeDescriptor { ProtocolFamily = "test", ProviderKey = "test", DisplayName = "Test", TransportKind = AgentTransportKind.AnthropicMessages },
            ProviderId = new ModelProviderId("test"),
            SessionId = "session",
            RunId = new AgentRunId("run"),
            Conversation =
            [
                new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Post on slack")]),
                new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.ToolCall("call-1", AgentToolSearch.ToolName, JsonSerializer.SerializeToElement(new { query = "slack" }))]),
                new AgentConversationMessage(AgentConversationRole.Tool,
                    [new AgentMessagePart.ToolResult("call-1", new AgentToolResult(true, [new AgentToolResultItem.Text("Loaded: mcp__slack__post"), new AgentToolResultItem.ToolReference("mcp__slack__post")]))]),
            ],
            Tools = [Tool("alta"), AgentToolSearch.Create(static (_, _) => ValueTask.FromResult<IReadOnlyList<AgentToolSearchHit>>([])), Tool("mcp__slack__post", deferred: true), Tool("mcp__github__issue", deferred: true)],
            State = new AgentSessionState { SessionId = "session", UpdatedAt = DateTimeOffset.UtcNow },
        };
}
