using System.Runtime.CompilerServices;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using AnthropicEffort = Anthropic.Models.Messages.Effort;
using MicrosoftReasoningEffort = Microsoft.Extensions.AI.ReasoningEffort;

namespace CodeAlta.Agent.Anthropic;

internal sealed class AnthropicAdaptiveThinkingChatClient(IChatClient inner) : IChatClient
{
    private const int DefaultMaxTokens = 1024;

    public void Dispose() => inner.Dispose();

    public object? GetService(System.Type serviceType, object? serviceKey = null)
        => inner.GetService(serviceType, serviceKey);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => inner.GetResponseAsync(messages, CreateOptions(options), cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in inner.GetStreamingResponseAsync(messages, CreateOptions(options), cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private static ChatOptions? CreateOptions(ChatOptions? options)
    {
        if (options?.Reasoning?.Effort is not { } reasoningEffort ||
            reasoningEffort == MicrosoftReasoningEffort.None ||
            !SupportsAdaptiveThinking(options.ModelId))
        {
            return options;
        }

        var originalRawRepresentationFactory = options.RawRepresentationFactory;
        var adjusted = options.Clone();
        adjusted.RawRepresentationFactory = implementation =>
        {
            var originalRawRepresentation = originalRawRepresentationFactory?.Invoke(implementation);
            var createParams = originalRawRepresentation as MessageCreateParams;
            if (createParams?.Thinking is not null)
            {
                return createParams;
            }

            createParams ??= new MessageCreateParams
            {
                MaxTokens = options.MaxOutputTokens ?? DefaultMaxTokens,
                Messages = [],
                Model = options.ModelId!,
            };

            return createParams with
            {
                Thinking = new ThinkingConfigAdaptive
                {
                    Display = options.Reasoning.Output == ReasoningOutput.None
                        ? Display.Omitted
                        : Display.Summarized,
                },
                OutputConfig = (createParams.OutputConfig ?? new OutputConfig()) with
                {
                    Effort = ToAnthropicEffort(options.ModelId, reasoningEffort),
                },
            };
        };

        return adjusted;
    }

    private static bool SupportsAdaptiveThinking(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var normalized = modelId.ToLowerInvariant();
        // The Anthropic MEAI adapter maps generic ReasoningOptions to legacy
        // thinking.type=enabled. Newer Claude models reject that shape and require
        // thinking.type=adaptive plus output_config.effort instead.
        return ContainsAny(normalized,
            "fable-5",
            "mythos-5",
            "mythos-preview",
            "opus-4-6",
            "opus-4.6",
            "4-6-opus",
            "4.6-opus",
            "opus-4-7",
            "opus-4.7",
            "4-7-opus",
            "4.7-opus",
            "opus-4-8",
            "opus-4.8",
            "4-8-opus",
            "4.8-opus",
            "sonnet-4-6",
            "sonnet-4.6",
            "4-6-sonnet",
            "4.6-sonnet");
    }

    private static AnthropicEffort ToAnthropicEffort(string? modelId, MicrosoftReasoningEffort reasoningEffort)
        => reasoningEffort switch
        {
            MicrosoftReasoningEffort.Low => AnthropicEffort.Low,
            MicrosoftReasoningEffort.Medium => AnthropicEffort.Medium,
            MicrosoftReasoningEffort.ExtraHigh => SupportsOpus47AdaptiveEffort(modelId)
                ? AnthropicEffort.Xhigh
                : AnthropicEffort.Max,
            _ => AnthropicEffort.High,
        };

    private static bool SupportsOpus47AdaptiveEffort(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var normalized = modelId.ToLowerInvariant();
        return ContainsAny(normalized, "opus-4-7", "opus-4.7", "4-7-opus", "4.7-opus");
    }

    private static bool ContainsAny(string value, params ReadOnlySpan<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (value.Contains(candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
