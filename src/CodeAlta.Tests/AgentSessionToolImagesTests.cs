using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Images;
using SkiaSharp;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentSessionToolImagesTests
{
    [TestMethod]
    public async Task Session_SavesAToolImageBesideTheJournal_AndSendsItOnlyDuringItsRun()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var original = Convert.ToBase64String(AgentToolImagesTests.CreateImage(3000, 1500, SKEncodedImageFormat.Png));
        var requests = new List<AgentTurnRequest>();
        await using var session = await CreateSessionAsync(temp.Path, store, "session-tool-image", [new AgentModelInfo("gpt-5.4", "GPT-5.4")], requests,
            new AgentToolResult(true, [new AgentToolResultItem.Text("Shot taken."), new AgentToolResultItem.Image(original, "image/png", "shot.png")]),
            Call("take_shot"), Say("I see a blue screen."), Say("Nothing more."));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Take a screenshot and look at it.") });

        // The request that follows the tool call carries the image, scaled to what a model accepts.
        var seen = requests[1].Conversation[^1].Parts.OfType<AgentMessagePart.ToolResult>().Single().Result;
        Assert.AreEqual("Shot taken.", Assert.IsInstanceOfType<AgentToolResultItem.Text>(seen.Items[0]).Value);
        var sent = Assert.IsInstanceOfType<AgentToolResultItem.Image>(seen.Items[1]);
        Assert.AreEqual("image/png", sent.MediaType);
        Assert.AreEqual("shot.png", sent.DisplayName);
        Assert.AreEqual((2048, 1024), AgentToolImagesTests.Measure(Convert.FromBase64String(sent.Base64Data)));

        // The image is a file of the session's attachment folder; the journal names it and holds no image data.
        var journal = Directory.EnumerateFiles(temp.Path, "session-tool-image.jsonl", SearchOption.AllDirectories).Single();
        var saved = Directory.EnumerateFiles(Path.ChangeExtension(journal, ".attachments")).Single();
        StringAssert.EndsWith(saved, ".png");
        StringAssert.Contains(Path.GetFileName(saved), "-tool-shot");
        Assert.AreEqual(sent.Base64Data, Convert.ToBase64String(await File.ReadAllBytesAsync(saved)));
        Assert.AreEqual(Path.ChangeExtension(journal, ".attachments"), await store.GetAttachmentDirectoryAsync("session-tool-image", CancellationToken.None));
        var journalText = await File.ReadAllTextAsync(journal);
        StringAssert.Contains(journalText, "\"$type\":\"localImage\"");
        StringAssert.Contains(journalText, JsonSerializer.Serialize(saved)[1..^1]);
        Assert.IsFalse(journalText.Contains("iVBORw0KGgo", StringComparison.Ordinal), "The journal holds no PNG data.");
        // What a text surface shows for the result.
        var history = await store.ReadEventsAsync("session-tool-image", CancellationToken.None);
        var output = history.OfType<AgentContentCompletedEvent>().Single(static content => content.Kind == AgentContentKind.ToolOutput);
        Assert.AreEqual($"Shot taken.{Environment.NewLine}[Image: shot.png (image/png, 2048x1024)]", output.Content);

        // A later run keeps a line that names the image, not the image.
        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Anything else?") });

        var later = requests[2].Conversation.SelectMany(static message => message.Parts).OfType<AgentMessagePart.ToolResult>().Single().Result;
        Assert.IsFalse(AgentToolResultImages.HasImages(later));
        Assert.AreEqual("[Image omitted from retained context: shot.png (image/png, 2048x1024).]",
            Assert.IsInstanceOfType<AgentToolResultItem.Text>(later.Items[1]).Value);
    }

    [TestMethod]
    public async Task Session_ReadsTheSavedImageAgain_WhenARunIsResumedFromTheJournal()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var image = Convert.ToBase64String(AgentToolImagesTests.CreateImage(64, 48, SKEncodedImageFormat.Png));
        var requests = new List<AgentTurnRequest>();
        // Two tool calls in one run: the second request of the run still carries the image of the first call.
        await using var session = await CreateSessionAsync(temp.Path, store, "session-tool-image-twice", [new AgentModelInfo("gpt-5.4", "GPT-5.4")], requests,
            new AgentToolResult(true, [new AgentToolResultItem.Image(image, "image/png", "shot.png")]),
            Call("take_shot"), Call("take_shot", "call-2"), Say("Both seen."));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Take two screenshots.") });

        var results = requests[2].Conversation.SelectMany(static message => message.Parts).OfType<AgentMessagePart.ToolResult>().ToArray();
        Assert.AreEqual(2, results.Length);
        foreach (var result in results)
        {
            Assert.AreEqual(image, Assert.IsInstanceOfType<AgentToolResultItem.Image>(result.Result.Items.Single()).Base64Data);
        }

        var journal = Directory.EnumerateFiles(temp.Path, "session-tool-image-twice.jsonl", SearchOption.AllDirectories).Single();
        Assert.AreEqual(2, Directory.EnumerateFiles(Path.ChangeExtension(journal, ".attachments")).Count());
    }

    [TestMethod]
    public async Task Session_GivesAModelWithoutImageInputANoteInsteadOfTheImage()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var image = Convert.ToBase64String(AgentToolImagesTests.CreateImage(64, 48, SKEncodedImageFormat.Png));
        var requests = new List<AgentTurnRequest>();
        AgentModelInfo[] models =
        [
            new("gpt-5.4", "GPT-5.4", Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal) { ["supportsImageInput"] = false }),
        ];
        await using var session = await CreateSessionAsync(temp.Path, store, "session-tool-image-text-model", models, requests,
            new AgentToolResult(true, [new AgentToolResultItem.Text("Shot taken."), new AgentToolResultItem.Image(image, "image/png", "shot.png")]),
            Call("take_shot"), Say("I cannot see it."));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Take a screenshot.") });

        var seen = requests[1].Conversation[^1].Parts.OfType<AgentMessagePart.ToolResult>().Single().Result;
        Assert.IsFalse(AgentToolResultImages.HasImages(seen));
        Assert.AreEqual("[Image not attached: shot.png. The current model does not accept image input.]",
            Assert.IsInstanceOfType<AgentToolResultItem.Text>(seen.Items[1]).Value);
        var journal = Directory.EnumerateFiles(temp.Path, "session-tool-image-text-model.jsonl", SearchOption.AllDirectories).Single();
        Assert.IsFalse(Directory.Exists(Path.ChangeExtension(journal, ".attachments")), "Nothing is saved for a model that gets no image.");
    }

    [TestMethod]
    public async Task Session_ReplacesAnImageThatCannotBeSentWithItsReason_AndKeepsTheTextOfTheResult()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var requests = new List<AgentTurnRequest>();
        await using var session = await CreateSessionAsync(temp.Path, store, "session-tool-image-corrupt", [new AgentModelInfo("gpt-5.4", "GPT-5.4")], requests,
            new AgentToolResult(true,
            [
                new AgentToolResultItem.Text("Two results."),
                new AgentToolResultItem.Image(Convert.ToBase64String("not an image at all"u8), "image/png", "broken.png"),
                new AgentToolResultItem.Image("%%%", "image/png", "garbled.png"),
            ]),
            Call("take_shot"), Say("Nothing to see."));

        _ = await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Take a screenshot.") });

        var seen = requests[1].Conversation[^1].Parts.OfType<AgentMessagePart.ToolResult>().Single().Result;
        Assert.IsTrue(seen.Success);
        var lines = seen.Items.Select(static item => Assert.IsInstanceOfType<AgentToolResultItem.Text>(item).Value).ToArray();
        Assert.AreEqual("Two results.", lines[0]);
        StringAssert.StartsWith(lines[1], "[Image omitted: broken.png. The file is not an image");
        Assert.AreEqual("[Image omitted: garbled.png. Its data is not valid base64.]", lines[2]);
    }

    private static Func<AgentTurnRequest, AgentTurnResponse> Call(string toolName, string callId = "call-1")
        => _ => new AgentTurnResponse
        {
            AssistantMessage = new AgentConversationMessage(
                AgentConversationRole.Assistant,
                [new AgentMessagePart.ToolCall(callId, toolName, JsonDocument.Parse("{}").RootElement.Clone())]),
        };

    private static Func<AgentTurnRequest, AgentTurnResponse> Say(string text)
        => _ => new AgentTurnResponse
        {
            AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(text)]),
        };

    private static async Task<AgentSession> CreateSessionAsync(
        string root,
        FileSystemAgentSessionStore store,
        string sessionId,
        IReadOnlyList<AgentModelInfo> models,
        List<AgentTurnRequest> requests,
        AgentToolResult toolResult,
        params Func<AgentTurnRequest, AgentTurnResponse>[] steps)
    {
        var provider = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = "openai-responses",
            ProviderKey = "openai",
            DisplayName = "OpenAI",
            TransportKind = AgentTransportKind.OpenAIResponses,
            BaseUri = new Uri("https://api.openai.com/v1"),
        };
        var created = DateTimeOffset.Parse("2026-04-06T10:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture);
        var summary = new AgentSessionSummary
        {
            SessionId = sessionId,
            ProviderId = ModelProviderIds.OpenAIResponses,
            ProtocolFamily = "openai-responses",
            ProviderKey = "openai",
            ModelId = "gpt-5.4",
            WorkingDirectory = root,
            CreatedAt = created,
            UpdatedAt = created,
        };
        var state = new AgentSessionState { SessionId = sessionId, ProtocolFamily = "openai-responses", ProviderKey = "openai", UpdatedAt = created };
        await store.UpsertSessionAsync(summary);
        await store.UpsertStateAsync(state);
        return new AgentSession(
            ModelProviderIds.OpenAIResponses,
            provider,
            summary,
            state,
            [],
            store,
            new RecordingTurnExecutor(models, requests, steps),
            new AgentSessionCreateOptions
            {
                ProviderKey = provider.ProviderKey,
                Model = "gpt-5.4",
                WorkingDirectory = root,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                Tools =
                [
                    new AgentToolDefinition(
                        new AgentToolSpec("take_shot", "Take a screenshot.", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
                        (_, _) => Task.FromResult(toolResult)),
                ],
            });
    }

    private sealed class RecordingTurnExecutor(
        IReadOnlyList<AgentModelInfo> models,
        List<AgentTurnRequest> requests,
        Func<AgentTurnRequest, AgentTurnResponse>[] steps) : IModelProviderTurnExecutor, IModelProviderModelCatalog
    {
        private int _next;

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(ModelProviderRuntimeDescriptor provider, CancellationToken cancellationToken = default)
            => Task.FromResult(models);

        public Task<AgentTurnResponse> ExecuteTurnAsync(
            AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            requests.Add(request);
            return Task.FromResult(steps[_next++](request));
        }
    }
}
