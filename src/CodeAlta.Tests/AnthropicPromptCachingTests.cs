using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic.Models.Messages;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Runtime;
using Microsoft.Extensions.AI;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AnthropicPromptCachingTests
{
    private const string MarkerKey = "anthropic:cache_control";

    [TestMethod]
    public void MarkBreakpoints_MarksTheToolsTheSystemPromptAndTheEndOfThisAndThePreviousRequest()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextContent("Read the files.")]),
            new(ChatRole.Assistant, [new TextReasoningContent("thinking") { ProtectedData = "signature" }, new FunctionCallContent("call-1", "read_file")]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "first file")]),
            new(ChatRole.Assistant, [new TextContent("Next."), new FunctionCallContent("call-2", "read_file")]),
            new(ChatRole.Tool, [new FunctionResultContent("call-2", "second file")]),
        };
        var options = new ChatOptions { Instructions = "You are a test agent.", Tools = [Declare("list_dir"), Declare("read_file")] };

        var (marked, markedOptions) = AnthropicPromptCache.MarkBreakpoints(messages, options);

        // The instructions become the system content, which is what takes a marker.
        Assert.IsNull(markedOptions!.Instructions);
        Assert.AreEqual(ChatRole.System, marked[0].Role);
        Assert.AreEqual("You are a test agent.", ((TextContent)marked[0].Contents.Single()).Text);
        Assert.IsTrue(IsMarked(marked[0].Contents[0]));

        var tools = markedOptions.Tools!.Cast<AIFunctionDeclaration>().ToArray();
        CollectionAssert.AreEqual(new[] { "list_dir", "read_file" }, tools.Select(static tool => tool.Name).ToArray());
        Assert.IsFalse(tools[0].AdditionalProperties.ContainsKey(nameof(Tool.CacheControl)));
        Assert.IsInstanceOfType<CacheControlEphemeral>(tools[1].AdditionalProperties[nameof(Tool.CacheControl)]);
        Assert.AreEqual(options.Tools[1].Description, tools[1].Description);
        Assert.AreEqual(((AIFunctionDeclaration)options.Tools[1]).JsonSchema.GetRawText(), tools[1].JsonSchema.GetRawText());

        // The last message, and the one that ended the previous request: the result before the last assistant message.
        var markedMessages = marked.Skip(1).Select(static message => message.Contents.Any(IsMarked)).ToArray();
        CollectionAssert.AreEqual(new[] { false, false, true, false, true }, markedMessages);
        Assert.AreEqual(4, marked.SelectMany(static message => message.Contents).Count(IsMarked) + 1, "A request takes at most four markers.");
    }

    [TestMethod]
    public void MarkBreakpoints_FirstRequestOfATurnMarksTheMessageThatEndedThePreviousTurn()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextContent("First question.")]),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "read_file")]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "file")]),
            new(ChatRole.Assistant, [new TextContent("The answer.")]),
            new(ChatRole.User, [new TextContent("Second question.")]),
        };

        var (marked, _) = AnthropicPromptCache.MarkBreakpoints(messages, new ChatOptions());

        CollectionAssert.AreEqual(
            new[] { false, false, true, false, true },
            marked.Select(static message => message.Contents.Any(IsMarked)).ToArray());
    }

    [TestMethod]
    public void MarkBreakpoints_FirstRequestOfASessionMarksItsOnlyMessage()
    {
        var (marked, options) = AnthropicPromptCache.MarkBreakpoints(
            [new ChatMessage(ChatRole.User, [new TextContent("Hello")])],
            options: null);

        Assert.IsNull(options);
        Assert.AreEqual(1, marked.Count);
        Assert.IsTrue(IsMarked(marked[0].Contents[0]));
    }

    [TestMethod]
    public void MarkBreakpoints_SkipsContentThatTakesNoMarker()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextContent("Question."), new DataContent(new byte[] { 1 }, "application/octet-stream")]),
            new(ChatRole.Assistant, [new TextContent("Answer."), new TextReasoningContent("thinking") { ProtectedData = "signature" }]),
            new(ChatRole.User, [new TextContent("   ")]),
        };

        var (marked, _) = AnthropicPromptCache.MarkBreakpoints(messages, new ChatOptions());

        // Empty text is not sent and thinking takes no marker: the marker goes on what comes before.
        Assert.IsFalse(marked[2].Contents.Any(IsMarked));
        Assert.IsTrue(IsMarked(marked[1].Contents[0]));
        Assert.IsFalse(IsMarked(marked[1].Contents[1]));
        Assert.IsTrue(IsMarked(marked[0].Contents[0]));
        Assert.IsFalse(IsMarked(marked[0].Contents[1]));
    }

    [TestMethod]
    public void MarkBreakpoints_LeavesTheOptionsOfTheCallerAsTheyWere()
    {
        var tool = Declare("read_file");
        var options = new ChatOptions { Instructions = "Instructions.", Tools = [tool] };

        _ = AnthropicPromptCache.MarkBreakpoints([new ChatMessage(ChatRole.User, [new TextContent("Hello")])], options);

        Assert.AreEqual("Instructions.", options.Instructions);
        Assert.AreSame(tool, options.Tools.Single());
    }

    [TestMethod]
    public async Task AnthropicTurn_MarksBreakpoints_UnlessTheProfileSaysTheEndpointTakesNone()
    {
        var marked = await SendTurnAsync(profile: null).ConfigureAwait(false);

        Assert.IsNull(marked.LastOptions!.Instructions);
        Assert.AreEqual(ChatRole.System, marked.LastMessages![0].Role);
        Assert.IsTrue(IsMarked(marked.LastMessages[0].Contents[0]));
        Assert.IsTrue(IsMarked(marked.LastMessages[^1].Contents[^1]));

        var plain = await SendTurnAsync(new AgentProviderProfile { SupportsDeveloperRole = false, SupportsCacheControl = false }).ConfigureAwait(false);

        Assert.AreEqual("You are a test agent.", plain.LastOptions!.Instructions);
        Assert.IsFalse(plain.LastMessages!.SelectMany(static message => message.Contents).Any(IsMarked));
    }

    [TestMethod]
    public async Task AnthropicTurn_ReportsWhatWasReadFromAndWrittenToThePromptCache()
    {
        var usage = new UsageDetails
        {
            // The Anthropic client adds what was read from and written to the cache to the input.
            InputTokenCount = 9_304,
            CachedInputTokenCount = 9_000,
            OutputTokenCount = 7,
            TotalTokenCount = 9_311,
            AdditionalCounts = new() { ["CacheCreationInputTokens"] = 300 },
        };
        var client = new RecordingChatClient(
        [
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)]),
        ]);
        var executor = AnthropicModelProviderRuntime.CreateTurnExecutor(new AnthropicProviderOptions
        {
            ProviderKey = "anthropic",
            ChatClientFactory = () => client,
        });

        var response = await executor.ExecuteTurnAsync(CreateRequest(profile: null), static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        var operation = response.Usage!.LastOperation!;
        Assert.AreEqual(9_304L, operation.InputTokens);
        Assert.AreEqual(9_000L, operation.CachedInputTokens);
        Assert.AreEqual(300L, operation.CacheWriteTokens);
    }

    [TestMethod]
    public async Task AnthropicTurn_WritesNoCacheForARequestThatStandsAlone()
    {
        var client = new RecordingChatClient([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")])]);
        var executor = AnthropicModelProviderRuntime.CreateTurnExecutor(new AnthropicProviderOptions
        {
            ProviderKey = "anthropic",
            ChatClientFactory = () => client,
        });

        // The summary of a compaction is asked once: a cache write for it is paid and never read.
        _ = await executor.ExecuteTurnAsync(CreateRequest(profile: null) with { IsStandalone = true }, static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        Assert.AreEqual("You are a test agent.", client.LastOptions!.Instructions);
        Assert.IsFalse(client.LastMessages!.SelectMany(static message => message.Contents).Any(IsMarked));
    }

    private static async Task<RecordingChatClient> SendTurnAsync(AgentProviderProfile? profile)
    {
        var client = new RecordingChatClient([new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")])]);
        var executor = AnthropicModelProviderRuntime.CreateTurnExecutor(new AnthropicProviderOptions
        {
            ProviderKey = "anthropic",
            ChatClientFactory = () => client,
        });
        _ = await executor.ExecuteTurnAsync(CreateRequest(profile), static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);
        return client;
    }

    private static AgentTurnRequest CreateRequest(AgentProviderProfile? profile)
        => new()
        {
            Provider = new ModelProviderRuntimeDescriptor
            {
                ProtocolFamily = "anthropic-messages",
                ProviderKey = "anthropic",
                DisplayName = "Anthropic",
                TransportKind = AgentTransportKind.AnthropicMessages,
                Profile = profile,
            },
            ProviderId = new ModelProviderId("anthropic"),
            SessionId = "session-test",
            RunId = new AgentRunId("run-test"),
            ModelId = "claude-sonnet-test",
            ModelInfo = new AgentModelInfo("claude-sonnet-test"),
            MaxOutputTokens = 4096,
            SystemMessage = "You are a test agent.",
            Conversation = [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Hello")])],
            Tools = [],
            State = new AgentSessionState { SessionId = "session-test", UpdatedAt = DateTimeOffset.UtcNow },
        };

    private static AIFunctionDeclaration Declare(string name)
        => AIFunctionFactory.CreateDeclaration(
            name,
            $"The {name} tool.",
            JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""").RootElement);

    private static bool IsMarked(AIContent content)
        => content.AdditionalProperties?.TryGetValue(MarkerKey, out var marker) == true && marker is CacheControlEphemeral;

    private sealed class RecordingChatClient(IReadOnlyList<ChatResponseUpdate> updates) : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }

        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public void Dispose()
        {
        }

        public object? GetService(System.Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(updates.ToChatResponse());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToArray();
            LastOptions = options;
            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update.Clone();
                await Task.Yield();
            }
        }
    }
}
