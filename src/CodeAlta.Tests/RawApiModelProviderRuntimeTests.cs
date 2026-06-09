using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.GoogleGenAI;
using CodeAlta.Agent.Runtime;
using Microsoft.Extensions.AI;
using AnthropicDisplay = Anthropic.Models.Messages.Display;
using AnthropicEffort = Anthropic.Models.Messages.Effort;
using AnthropicMessageCreateParams = Anthropic.Models.Messages.MessageCreateParams;

namespace CodeAlta.Tests;

[TestClass]
public sealed class RawApiModelProviderRuntimeTests
{
    [TestMethod]
    public async Task AnthropicModelProviderRuntime_UsesInjectedChatClientForSessionsAndModelListing()
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
        await using var providerRuntime = new AnthropicModelProviderRuntime(new AnthropicModelProviderRuntimeOptions
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

        var models = await providerRuntime.ListModelsAsync().ConfigureAwait(false);
        Assert.AreEqual(1, models.Count);
        Assert.AreEqual("claude-sonnet-test", models[0].Id);

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
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
        Assert.IsTrue(usageEvent.Usage?.CurrentTokens > 24L);
        Assert.AreEqual(24L, usageEvent.Usage?.LastOperation?.InputTokens + usageEvent.Usage?.LastOperation?.OutputTokens);
        Assert.AreEqual(200000L, usageEvent.Usage?.TokenLimit);
        Assert.IsNotNull(client.LastOptions);
        StringAssert.Contains(client.LastOptions.Instructions, "System instructions");

        await using var resumed = await providerRuntime.ResumeSessionAsync(
            session.SessionId,
            new AgentSessionResumeOptions
            {
                WorkingDirectory = temp.Path,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            }).ConfigureAwait(false);
        var resumedHistory = await resumed.GetHistoryAsync().ConfigureAwait(false);
        Assert.IsTrue(resumedHistory.OfType<AgentContentCompletedEvent>().Any(static e => e.Kind == AgentContentKind.Reasoning && e.Content == "thinking"));
        Assert.IsTrue(resumedHistory.OfType<AgentContentCompletedEvent>().Any(static e => e.Kind == AgentContentKind.Assistant && e.Content == "Anthropic answer."));
    }

    [TestMethod]
    public async Task AnthropicModelProviderRuntime_MiniMaxCompatibility_FallsBackToNonStreamingChatResponse()
    {
        using var temp = TestTempDirectory.Create();
        var client = new NonStreamingOnlyChatClient(
        [
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("thinking") { ProtectedData = "sig-1" }])
            {
                MessageId = "anthropic-message",
                ResponseId = "anthropic-response",
                ConversationId = "anthropic-conversation",
                ModelId = "MiniMax-M2.7",
            },
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Anthropic answer.")])
            {
                MessageId = "anthropic-message",
                ResponseId = "anthropic-response",
                ConversationId = "anthropic-conversation",
                ModelId = "MiniMax-M2.7",
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
                ModelId = "MiniMax-M2.7",
            },
        ]);

        await using var providerRuntime = new AnthropicModelProviderRuntime(new AnthropicModelProviderRuntimeOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new AnthropicProviderOptions
                {
                    ProviderKey = "minimax",
                    DisplayName = "MiniMax 2.7",
                    BaseUri = new Uri("https://api.minimax.io/anthropic"),
                    SingleModelId = "MiniMax-M2.7",
                    IsDefault = true,
                    ChatClientFactory = () => client,
                    ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
                    [
                        new AgentModelInfo(
                            "MiniMax-M2.7",
                            DisplayName: "MiniMax-M2.7",
                            Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["maxInputTokens"] = 204800L,
                            }),
                    ]),
                },
            },
        });

        _ = await providerRuntime.ListModelsAsync().ConfigureAwait(false);

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "MiniMax-M2.7",
            WorkingDirectory = temp.Path,
            SystemMessage = "System instructions",
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Hello")]),
        }).ConfigureAwait(false);

        Assert.AreEqual(1, client.NonStreamingRequestCount);
        Assert.AreEqual(0, client.StreamingRequestCount);

        var history = await session.GetHistoryAsync().ConfigureAwait(false);
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(static e => e.Kind == AgentContentKind.Reasoning && e.Content == "thinking"));
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(static e => e.Kind == AgentContentKind.Assistant && e.Content == "Anthropic answer."));
    }

    [TestMethod]
    public async Task AnthropicModelProviderRuntime_UsesAdaptiveThinkingForOpus47Reasoning()
    {
        using var temp = TestTempDirectory.Create();
        var client = new RecordingChatClient(
        [
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Anthropic answer.")])
            {
                MessageId = "anthropic-message",
                ResponseId = "anthropic-response",
                ConversationId = "anthropic-conversation",
                ModelId = "claude-opus-4-7",
            },
        ]);

        await using var providerRuntime = new AnthropicModelProviderRuntime(new AnthropicModelProviderRuntimeOptions
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
                        new AgentModelInfo("claude-opus-4-7", DisplayName: "Claude Opus 4.7"),
                    ]),
                },
            },
        });

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "claude-opus-4-7",
            ReasoningEffort = AgentReasoningEffort.High,
            WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Hello")]),
        }).ConfigureAwait(false);

        Assert.IsNotNull(client.LastOptions?.RawRepresentationFactory);
        var createParams = Assert.IsInstanceOfType<AnthropicMessageCreateParams>(client.LastOptions.RawRepresentationFactory(client));
        Assert.IsNotNull(createParams.Thinking);
        Assert.IsTrue(createParams.Thinking.TryPickAdaptive(out var adaptive));
        Assert.IsNotNull(adaptive);
        Assert.AreEqual(AnthropicDisplay.Summarized, adaptive.Display?.Value());
        Assert.AreEqual(AnthropicEffort.High, createParams.OutputConfig?.Effort?.Value());
    }

    [TestMethod]
    public async Task AnthropicModelProviderRuntime_MapsOpus46XHighReasoningToAdaptiveMaxEffort()
    {
        using var temp = TestTempDirectory.Create();
        var client = new RecordingChatClient(
        [
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Anthropic answer.")])
            {
                MessageId = "anthropic-message",
                ResponseId = "anthropic-response",
                ConversationId = "anthropic-conversation",
                ModelId = "claude-opus-4-6",
            },
        ]);

        await using var providerRuntime = new AnthropicModelProviderRuntime(new AnthropicModelProviderRuntimeOptions
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
                        new AgentModelInfo("claude-opus-4-6", DisplayName: "Claude Opus 4.6"),
                    ]),
                },
            },
        });

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "claude-opus-4-6",
            ReasoningEffort = AgentReasoningEffort.XHigh,
            WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Hello")]),
        }).ConfigureAwait(false);

        Assert.IsNotNull(client.LastOptions?.RawRepresentationFactory);
        var createParams = Assert.IsInstanceOfType<AnthropicMessageCreateParams>(client.LastOptions.RawRepresentationFactory(client));
        Assert.IsNotNull(createParams.Thinking);
        Assert.IsTrue(createParams.Thinking.TryPickAdaptive(out _));
        Assert.AreEqual(AnthropicEffort.Max, createParams.OutputConfig?.Effort?.Value());
    }

    [TestMethod]
    [DataRow("claude-fable-5", "Claude Fable 5")]
    [DataRow("claude-mythos-5", "Claude Mythos 5")]
    public async Task AnthropicModelProviderRuntime_UsesAdaptiveThinkingForClaude5Reasoning(string modelId, string displayName)
    {
        using var temp = TestTempDirectory.Create();
        var client = new RecordingChatClient(
        [
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Anthropic answer.")])
            {
                MessageId = "anthropic-message",
                ResponseId = "anthropic-response",
                ConversationId = "anthropic-conversation",
                ModelId = modelId,
            },
        ]);

        await using var providerRuntime = new AnthropicModelProviderRuntime(new AnthropicModelProviderRuntimeOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new AnthropicProviderOptions
                {
                    ProviderKey = "anthropic",
                    IsDefault = true,
                    ChatClientFactory = () => client,
                    ModelListAsync = _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
                    [
                        new AgentModelInfo(modelId, DisplayName: displayName),
                    ]),
                },
            },
        });

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = modelId,
            ReasoningEffort = AgentReasoningEffort.High,
            WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Hello")]),
        }).ConfigureAwait(false);

        Assert.IsNotNull(client.LastOptions?.RawRepresentationFactory);
        var createParams = Assert.IsInstanceOfType<AnthropicMessageCreateParams>(client.LastOptions.RawRepresentationFactory(client));
        Assert.IsNotNull(createParams.Thinking);
        Assert.IsTrue(createParams.Thinking.TryPickAdaptive(out var adaptive));
        Assert.IsNotNull(adaptive);
        Assert.AreEqual(AnthropicDisplay.Summarized, adaptive.Display?.Value());
        Assert.AreEqual(AnthropicEffort.High, createParams.OutputConfig?.Effort?.Value());
    }

    [TestMethod]
    public async Task GoogleGenAIModelProviderRuntime_PreservesThoughtSignaturesInSessionHistory()
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
        await using var providerRuntime = new GoogleGenAIModelProviderRuntime(new GoogleGenAIModelProviderRuntimeOptions
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

        _ = await providerRuntime.ListModelsAsync().ConfigureAwait(false);

        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
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
        Assert.IsTrue(usageEvent.Usage?.CurrentTokens >= 42L);
        Assert.AreEqual(42L, usageEvent.Usage?.LastOperation?.InputTokens + usageEvent.Usage?.LastOperation?.OutputTokens);
        Assert.AreEqual(1000000L, usageEvent.Usage?.TokenLimit);

        var rawAssistant = history.OfType<AgentRawEvent>().Single(static e => e.BackendEventType == "local.assistantMessage");
        var message = rawAssistant.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentConversationMessage);
        Assert.IsNotNull(message);
        var reasoningPart = Assert.IsInstanceOfType<AgentMessagePart.Reasoning>(message.Parts[0]);
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

    private sealed class NonStreamingOnlyChatClient(IReadOnlyList<ChatResponseUpdate> updates) : IChatClient
    {
        public int NonStreamingRequestCount { get; private set; }

        public int StreamingRequestCount { get; private set; }

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            NonStreamingRequestCount++;
            return Task.FromResult(updates.ToChatResponse());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamingRequestCount++;
            await Task.Yield();
            throw new InvalidOperationException("Streaming should not be called for MiniMax Anthropic compatibility.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
