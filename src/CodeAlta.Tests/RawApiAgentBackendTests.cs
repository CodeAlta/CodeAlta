using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.GoogleGenAI;
using CodeAlta.Agent.LocalRuntime;
using Microsoft.Extensions.AI;

namespace CodeAlta.Tests;

[TestClass]
public sealed class RawApiAgentBackendTests
{
    [TestMethod]
    public async Task AnthropicAgentBackend_UsesInjectedChatClientForSessionsAndModelListing()
    {
        using var temp = TestTempDirectory.Create();
        var client = new RecordingChatClient(
            [
                new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("thinking") { ProtectedData = "sig-1" }])
                {
                    MessageId = "anthropic-message",
                    ResponseId = "anthropic-response",
                    ConversationId = "anthropic-conversation",
                    ModelId = "claude-sonnet-test",
                },
                new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Anthropic answer.")])
                {
                    MessageId = "anthropic-message",
                    ResponseId = "anthropic-response",
                    ConversationId = "anthropic-conversation",
                    ModelId = "claude-sonnet-test",
                },
                new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails
                {
                    InputTokenCount = 18,
                    OutputTokenCount = 6,
                    TotalTokenCount = 24,
                })])
                {
                    MessageId = "anthropic-message",
                    ResponseId = "anthropic-response",
                    ConversationId = "anthropic-conversation",
                    ModelId = "claude-sonnet-test",
                },
            ]);
        await using var backend = new AnthropicAgentBackend(new AnthropicAgentBackendOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new AnthropicProviderOptions
                {
                    ProviderKey = "anthropic",
                    IsDefault = true,
                    ChatClientFactory = () => client,
                    ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
                    [
                        new AgentModelInfo(
                            "claude-sonnet-test",
                            DisplayName: "Claude Sonnet Test",
                            Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["maxInputTokens"] = 200000L,
                            }),
                    ]),
                },
            },
        });

        var models = await backend.ListModelsAsync().ConfigureAwait(false);
        Assert.AreEqual(1, models.Count);
        Assert.AreEqual("claude-sonnet-test", models[0].Id);

        await using var session = await backend.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "claude-sonnet-test",
            WorkingDirectory = temp.Path,
            SystemMessage = "System instructions",
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Hello")]),
        }).ConfigureAwait(false);

        var history = await session.GetHistoryAsync().ConfigureAwait(false);
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(static e => e.Kind == AgentContentKind.Reasoning && e.Content == "thinking"));
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(static e => e.Kind == AgentContentKind.Assistant && e.Content == "Anthropic answer."));
        var usageEvent = history.OfType<AgentSessionUpdateEvent>().Single(static e => e.Kind == AgentSessionUpdateKind.UsageUpdated);
        Assert.AreEqual(24L, usageEvent.Usage?.CurrentTokens);
        Assert.AreEqual(200000L, usageEvent.Usage?.TokenLimit);
        Assert.IsNotNull(client.LastOptions);
        StringAssert.Contains(client.LastOptions.Instructions, "System instructions");

        await using var resumed = await backend.ResumeSessionAsync(
            session.SessionId,
            new AgentSessionResumeOptions
            {
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            }).ConfigureAwait(false);
        var resumedHistory = await resumed.GetHistoryAsync().ConfigureAwait(false);
        Assert.IsTrue(resumedHistory.OfType<AgentContentCompletedEvent>().Any(static e => e.Kind == AgentContentKind.Reasoning && e.Content == "thinking"));
        Assert.IsTrue(resumedHistory.OfType<AgentContentCompletedEvent>().Any(static e => e.Kind == AgentContentKind.Assistant && e.Content == "Anthropic answer."));
    }

    [TestMethod]
    public async Task GoogleGenAIAgentBackend_PreservesThoughtSignaturesInSessionHistory()
    {
        using var temp = TestTempDirectory.Create();
        var client = new RecordingChatClient(
            [
                new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(null) { ProtectedData = "google-signature" }])
                {
                    MessageId = "google-message",
                    ResponseId = "google-response",
                    ConversationId = "google-conversation",
                    ModelId = "gemini-test",
                },
                new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Google answer.")])
                {
                    MessageId = "google-message",
                    ResponseId = "google-response",
                    ConversationId = "google-conversation",
                    ModelId = "gemini-test",
                },
                new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails
                {
                    InputTokenCount = 30,
                    OutputTokenCount = 12,
                    TotalTokenCount = 42,
                })])
                {
                    MessageId = "google-message",
                    ResponseId = "google-response",
                    ConversationId = "google-conversation",
                    ModelId = "gemini-test",
                },
            ]);
        await using var backend = new GoogleGenAIAgentBackend(new GoogleGenAIAgentBackendOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new GoogleGenAIProviderOptions
                {
                    ProviderKey = "google",
                    IsDefault = true,
                    ChatClientFactory = () => client,
                    ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
                    [
                        new AgentModelInfo(
                            "gemini-test",
                            DisplayName: "Gemini Test",
                            Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["inputTokenLimit"] = 1000000L,
                            }),
                    ]),
                },
            },
        });

        await using var session = await backend.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "gemini-test",
            WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Hello")]),
        }).ConfigureAwait(false);

        var history = await session.GetHistoryAsync().ConfigureAwait(false);
        var reasoningEvent = history.OfType<AgentContentCompletedEvent>().Single(static e => e.Kind == AgentContentKind.Reasoning);
        Assert.AreEqual(string.Empty, reasoningEvent.Content);
        Assert.IsTrue(reasoningEvent.Details.HasValue);
        Assert.AreEqual("google-signature", reasoningEvent.Details.Value.GetProperty("protectedData").GetString());
        var usageEvent = history.OfType<AgentSessionUpdateEvent>().Single(static e => e.Kind == AgentSessionUpdateKind.UsageUpdated);
        Assert.AreEqual(42L, usageEvent.Usage?.CurrentTokens);
        Assert.AreEqual(1000000L, usageEvent.Usage?.TokenLimit);

        var rawAssistant = history.OfType<AgentRawEvent>().Single(static e => e.BackendEventType == "local.assistantMessage");
        var message = rawAssistant.Raw.Deserialize(AgentJsonSerializerContext.Default.LocalAgentConversationMessage);
        Assert.IsNotNull(message);
        var reasoningPart = Assert.IsInstanceOfType<LocalAgentMessagePart.Reasoning>(message.Parts[0]);
        Assert.AreEqual("google-signature", reasoningPart.ProtectedData);
    }

    private sealed class RecordingChatClient(IReadOnlyList<ChatResponseUpdate> updates) : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }

        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

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
            LastMessages = messages.Select(static message => message.Clone()).ToArray();
            LastOptions = options?.Clone();
            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update.Clone();
                await Task.Yield();
            }
        }
    }
}
