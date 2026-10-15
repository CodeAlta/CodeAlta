using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using AnthropicEffort = Anthropic.Models.Messages.Effort;
using MicrosoftReasoningEffort = Microsoft.Extensions.AI.ReasoningEffort;

namespace CodeAlta.Agent.Anthropic;

/// <summary>
/// Gives adaptive thinking and its effort to the models that take it. On the Claude API it also asks that a replayed
/// thinking block the API cannot match to the conversation be dropped instead of failing the request
/// (<c>thinking.block_binding.prefix_mismatch_behavior: "drop_block"</c>, beta <see cref="ThinkingBindingBeta"/>): a
/// block signed for another account or organization (another API key), or before an earlier part of the request
/// changed (a compaction, the tools of the session), would otherwise fail every later request of the session.
/// </summary>
/// <param name="inner">The client that sends the requests.</param>
/// <param name="dropMismatchedThinking">Whether the endpoint is the Claude API, which takes the binding control.</param>
/// <param name="refusalFallback">
/// Whether a model that takes it is sent the server-side fallback of the Claude API (<c>fallbacks: "default"</c>,
/// beta <see cref="RefusalFallbackBeta"/>): a request its safety classifiers decline runs again on another model.
/// </param>
internal sealed class AnthropicAdaptiveThinkingChatClient(IChatClient inner, bool dropMismatchedThinking, bool refusalFallback = false) : IChatClient
{
    /// <summary>The beta that the thinking block binding control requires.</summary>
    internal const string ThinkingBindingBeta = "thinking-binding-controls-2026-08-01";

    /// <summary>The beta of the <c>"default"</c> form of the server-side fallback.</summary>
    internal const string RefusalFallbackBeta = "server-side-fallback-2026-07-01";

    /// <summary>
    /// The key of <see cref="ChatOptions.AdditionalProperties"/> that names the effort the highest effort is sent as,
    /// <c>xhigh</c> or <c>max</c>, from the capabilities the Models API gives the model.
    /// </summary>
    internal const string HighestEffortKey = "codealta.anthropic.highest_effort";

    private const string BetaHeader = "anthropic-beta";

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

    private ChatOptions? CreateOptions(ChatOptions? options)
    {
        var withThinking = CreateThinkingOptions(options);
        if (withThinking is null || !refusalFallback || !TakesDefaultFallback(withThinking.ModelId))
        {
            return withThinking;
        }

        // The "default" form routes by the category of the refusal, so no model list is kept here.
        var originalRawRepresentationFactory = withThinking.RawRepresentationFactory;
        var adjusted = withThinking.Clone();
        adjusted.RawRepresentationFactory = implementation =>
        {
            var createParams = originalRawRepresentationFactory?.Invoke(implementation) as MessageCreateParams ?? new MessageCreateParams
            {
                MaxTokens = withThinking.MaxOutputTokens ?? throw new InvalidOperationException(
                    "An Anthropic request requires an explicit maximum output-token limit."),
                Messages = [],
                Model = withThinking.ModelId!,
            };
            var body = new Dictionary<string, JsonElement>(createParams.RawBodyData, StringComparer.Ordinal)
            {
                ["fallbacks"] = ParseJson("\"default\""),
            };
            return AddBeta(MessageCreateParams.FromRawUnchecked(createParams.RawHeaderData, createParams.RawQueryData, body), RefusalFallbackBeta);
        };
        return adjusted;
    }

    private ChatOptions? CreateThinkingOptions(ChatOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        if (options.Reasoning?.Effort is { } reasoningEffort && reasoningEffort != MicrosoftReasoningEffort.None)
        {
            if (!SupportsAdaptiveThinking(options.ModelId))
            {
                return options;
            }

            return WithThinking(options, createParams => createParams with
            {
                Thinking = BindThinking(new ThinkingConfigAdaptive
                {
                    Display = options.Reasoning.Output == ReasoningOutput.None
                        ? Display.Omitted
                        : Display.Summarized,
                }),
                OutputConfig = (createParams.OutputConfig ?? new OutputConfig()) with
                {
                    Effort = ToAnthropicEffort(options, reasoningEffort),
                },
            });
        }

        // A model that thinks when it is sent no thinking (adaptive thinking is its default) signs its blocks all the
        // same: it is sent that default, with the binding control, and a summary of its reasoning to show, since its
        // default display leaves the reasoning empty.
        if (options.Reasoning is null && dropMismatchedThinking && ThinksByDefault(options.ModelId))
        {
            return WithThinking(options, createParams => createParams with
            {
                Thinking = BindThinking(new ThinkingConfigAdaptive { Display = Display.Summarized }),
            });
        }

        return options;
    }

    private ChatOptions WithThinking(ChatOptions options, Func<MessageCreateParams, MessageCreateParams> addThinking)
    {
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

            createParams = addThinking(createParams);
            return dropMismatchedThinking ? AddBeta(createParams, ThinkingBindingBeta) : createParams;
        };

        return adjusted;
    }

    private ThinkingConfigAdaptive BindThinking(ThinkingConfigAdaptive thinking)
    {
        if (!dropMismatchedThinking)
        {
            return thinking;
        }

        // The SDK gives block_binding to its beta types only: the field is added to the JSON of the thinking.
        var rawData = new Dictionary<string, JsonElement>(thinking.RawData, StringComparer.Ordinal)
        {
            ["block_binding"] = ParseJson("""{"prefix_mismatch_behavior":"drop_block"}"""),
        };
        return ThinkingConfigAdaptive.FromRawUnchecked(rawData);
    }

    // A beta goes with the others the request already names.
    private static MessageCreateParams AddBeta(MessageCreateParams createParams, string beta)
    {
        var headers = new Dictionary<string, JsonElement>(createParams.RawHeaderData, StringComparer.OrdinalIgnoreCase);
        var betas = new List<string>();
        if (headers.TryGetValue(BetaHeader, out var existing))
        {
            if (existing.ValueKind == JsonValueKind.Array)
            {
                betas.AddRange(existing.EnumerateArray().Select(static value => value.GetString()).OfType<string>());
            }
            else if (existing.ValueKind == JsonValueKind.String && existing.GetString() is { } value)
            {
                betas.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        if (!betas.Contains(beta, StringComparer.Ordinal))
        {
            betas.Add(beta);
        }

        headers[BetaHeader] = ParseJson($"\"{JsonEncodedText.Encode(string.Join(",", betas))}\"");
        return MessageCreateParams.FromRawUnchecked(headers, createParams.RawQueryData, createParams.RawBodyData);
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    // The models the Claude API runs the "default" fallback for: Claude Fable 5.1, Opus 5.5, Opus 5 and Sonnet 5.5.
    internal static bool TakesDefaultFallback(string? modelId)
    {
        if (!TryGetClaudeModelId(modelId, out var normalizedClaudeModelId) ||
            !TryReadClaudeVersion(normalizedClaudeModelId, out var major, out var minor))
        {
            return false;
        }

        return major == 5 &&
            (normalizedClaudeModelId.StartsWith("claude-fable-", StringComparison.Ordinal) && minor == 1 ||
                normalizedClaudeModelId.StartsWith("claude-opus-", StringComparison.Ordinal) && minor is 0 or 5 ||
                normalizedClaudeModelId.StartsWith("claude-sonnet-", StringComparison.Ordinal) && minor == 5);
    }

    // The Claude models from the fifth generation on run adaptive thinking when the request names no thinking.
    private static bool ThinksByDefault(string? modelId)
        => TryGetClaudeModelId(modelId, out var normalizedClaudeModelId) &&
            TryReadClaudeVersion(normalizedClaudeModelId, out var major, out _) &&
            major >= 5;

    private static bool SupportsAdaptiveThinking(string? modelId)
    {
        if (!TryGetClaudeModelId(modelId, out var normalizedClaudeModelId))
        {
            return false;
        }

        // The Anthropic MEAI adapter is created in its Extended mode
        // (AnthropicModelProviderRuntime), which maps generic ReasoningOptions to legacy
        // thinking.type=enabled. Anthropic's adaptive-thinking guidance marks
        // older Claude models (Sonnet 4.5, Opus 4.5, etc.) as the manual-only
        // boundary. Prefer that version cutoff over a fixed allow-list so future
        // Claude releases default to adaptive without needing code changes.
        return normalizedClaudeModelId.Contains("mythos-preview", StringComparison.Ordinal) ||
            (TryReadClaudeVersion(normalizedClaudeModelId, out var major, out var minor) &&
                (major > 4 || major == 4 && minor >= 6));
    }

    private static AnthropicEffort ToAnthropicEffort(ChatOptions options, MicrosoftReasoningEffort reasoningEffort)
        => reasoningEffort switch
        {
            MicrosoftReasoningEffort.Low => AnthropicEffort.Low,
            MicrosoftReasoningEffort.Medium => AnthropicEffort.Medium,
            MicrosoftReasoningEffort.ExtraHigh => (options.AdditionalProperties?.TryGetValue(HighestEffortKey, out var listed) == true ? listed as string : null) switch
            {
                // The effort the Models API lists for the model.
                "xhigh" => AnthropicEffort.Xhigh,
                "max" => AnthropicEffort.Max,
                _ => SupportsXHighAdaptiveEffort(options.ModelId) ? AnthropicEffort.Xhigh : AnthropicEffort.Max,
            },
            _ => AnthropicEffort.High,
        };

    // For a model the endpoint lists no effort of: the families documented to take xhigh. "max" is available on
    // every adaptive model.
    private static bool SupportsXHighAdaptiveEffort(string? modelId)
    {
        if (!TryGetClaudeModelId(modelId, out var normalizedClaudeModelId) ||
            !TryReadClaudeVersion(normalizedClaudeModelId, out var major, out var minor))
        {
            return false;
        }

        if (major == 4)
        {
            return minor >= 7 && normalizedClaudeModelId.Contains("opus", StringComparison.Ordinal);
        }

        // Every Claude model of the fifth generation takes xhigh: Fable, Mythos, Opus, Sonnet and Haiku.
        return major >= 5;
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
