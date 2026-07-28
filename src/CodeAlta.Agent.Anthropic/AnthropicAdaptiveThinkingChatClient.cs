using System.Runtime.CompilerServices;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using AnthropicEffort = Anthropic.Models.Messages.Effort;
using MicrosoftReasoningEffort = Microsoft.Extensions.AI.ReasoningEffort;

namespace CodeAlta.Agent.Anthropic;

internal sealed class AnthropicAdaptiveThinkingChatClient(IChatClient inner) : IChatClient
{
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
                MaxTokens = options.MaxOutputTokens ?? throw new InvalidOperationException(
                    "Anthropic adaptive thinking requires an explicit maximum output-token limit."),
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
        if (!TryGetClaudeModelId(modelId, out var normalizedClaudeModelId))
        {
            return false;
        }

        // The Anthropic MEAI adapter maps generic ReasoningOptions to legacy
        // thinking.type=enabled. Anthropic's adaptive-thinking guidance marks
        // older Claude models (Sonnet 4.5, Opus 4.5, etc.) as the manual-only
        // boundary. Prefer that version cutoff over a fixed allow-list so future
        // Claude releases default to adaptive without needing code changes.
        return normalizedClaudeModelId.Contains("mythos-preview", StringComparison.Ordinal) ||
            (TryReadClaudeVersion(normalizedClaudeModelId, out var major, out var minor) &&
                (major > 4 || major == 4 && minor >= 6));
    }

    private static AnthropicEffort ToAnthropicEffort(string? modelId, MicrosoftReasoningEffort reasoningEffort)
        => reasoningEffort switch
        {
            MicrosoftReasoningEffort.Low => AnthropicEffort.Low,
            MicrosoftReasoningEffort.Medium => AnthropicEffort.Medium,
            MicrosoftReasoningEffort.ExtraHigh => SupportsXHighAdaptiveEffort(modelId)
                ? AnthropicEffort.Xhigh
                : AnthropicEffort.Max,
            _ => AnthropicEffort.High,
        };

    private static bool SupportsXHighAdaptiveEffort(string? modelId)
    {
        if (!TryGetClaudeModelId(modelId, out var normalizedClaudeModelId) ||
            !TryReadClaudeVersion(normalizedClaudeModelId, out var major, out var minor))
        {
            return false;
        }

        // "max" is available on every adaptive model. Use the narrower
        // "xhigh" value only for families documented to accept it.
        if (major == 4)
        {
            return minor >= 7 && normalizedClaudeModelId.Contains("opus", StringComparison.Ordinal);
        }

        return major >= 5 &&
            (normalizedClaudeModelId.Contains("fable", StringComparison.Ordinal) ||
                normalizedClaudeModelId.Contains("mythos", StringComparison.Ordinal) ||
                normalizedClaudeModelId.Contains("opus", StringComparison.Ordinal));
    }

    private static bool TryGetClaudeModelId(string? modelId, out string normalizedClaudeModelId)
    {
        normalizedClaudeModelId = string.Empty;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var normalized = modelId.ToLowerInvariant();
        var claudeIndex = normalized.IndexOf("claude-", StringComparison.Ordinal);
        if (claudeIndex < 0)
        {
            return false;
        }

        normalizedClaudeModelId = normalized[claudeIndex..];
        return true;
    }

    private static bool TryReadClaudeVersion(string normalizedClaudeModelId, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        var hasMajor = false;

        for (var index = 0; index < normalizedClaudeModelId.Length; index++)
        {
            var current = normalizedClaudeModelId[index];
            if (current is < '0' or > '9')
            {
                continue;
            }

            var value = 0;
            var digitCount = 0;
            do
            {
                value = value * 10 + current - '0';
                digitCount++;
                index++;
                if (index >= normalizedClaudeModelId.Length)
                {
                    break;
                }

                current = normalizedClaudeModelId[index];
            }
            while (current is >= '0' and <= '9');

            if (digitCount > 2)
            {
                if (hasMajor)
                {
                    return true;
                }

                continue;
            }

            if (!hasMajor)
            {
                major = value;
                hasMajor = true;
            }
            else
            {
                minor = value;
                return true;
            }
        }

        return hasMajor;
    }
}
