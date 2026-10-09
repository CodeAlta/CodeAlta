using System.Text.Json;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace CodeAlta.Agent.Anthropic;

/// <summary>
/// Marks the prompt-cache breakpoints of an Anthropic Messages request. The Messages API caches nothing by
/// itself: without a <c>cache_control</c> marker, every request of a session pays its whole prompt again at the
/// full input price.
/// </summary>
/// <remarks>
/// A request takes at most four markers. They go on the last tool, on the system prompt, on the last message, and
/// on the message that ended the previous request: the one before the last assistant message. The marker of the
/// last message writes the prefix the next request reads, at the place where that request puts its own marker, so
/// the read does not depend on how many blocks a turn added.
/// </remarks>
internal static class AnthropicPromptCache
{
    /// <summary>Returns the messages and the options of a request with its breakpoints marked.</summary>
    /// <param name="messages">The messages of the request. They belong to it: the marker is set on their content.</param>
    /// <param name="options">The options of the request, which are left as they are; null for none.</param>
    /// <returns>The messages, with the instructions of the options as system content, and the options to send.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is null.</exception>
    public static (IReadOnlyList<ChatMessage> Messages, ChatOptions? Options) MarkBreakpoints(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var marked = messages.ToList();
        var leadingSystemMessages = 0;
        while (leadingSystemMessages < marked.Count && marked[leadingSystemMessages].Role == ChatRole.System)
        {
            leadingSystemMessages++;
        }

        if (options is not null && (options.Instructions is not null || options.Tools is { Count: > 0 }))
        {
            options = options.Clone();
            if (!string.IsNullOrWhiteSpace(options.Instructions))
            {
                // The instructions follow the leading system messages, as the Anthropic client orders them, but
                // as content: only content takes a marker.
                marked.Insert(leadingSystemMessages, new ChatMessage(ChatRole.System, [new TextContent(options.Instructions)]));
                leadingSystemMessages++;
                options.Instructions = null;
            }

            MarkLastTool(options);
        }

        for (var index = leadingSystemMessages - 1; index >= 0; index--)
        {
            if (TryMark(marked[index]))
            {
                break;
            }
        }

        var last = MarkLastMessage(marked, marked.Count - 1, leadingSystemMessages);
        var lastAssistant = last;
        while (lastAssistant >= leadingSystemMessages && marked[lastAssistant].Role != ChatRole.Assistant)
        {
            lastAssistant--;
        }

        if (lastAssistant >= leadingSystemMessages)
        {
            MarkLastMessage(marked, lastAssistant - 1, leadingSystemMessages);
        }

        return (marked, options);
    }

    // Marks the last message at or before an index that has content to mark, and returns its index.
    private static int MarkLastMessage(List<ChatMessage> messages, int from, int first)
    {
        for (var index = from; index >= first; index--)
        {
            if (messages[index].Role != ChatRole.System && TryMark(messages[index]))
            {
                return index;
            }
        }

        return first - 1;
    }

    private static void MarkLastTool(ChatOptions options)
    {
        if (options.Tools is not { Count: > 0 } tools)
        {
            return;
        }

        for (var index = tools.Count - 1; index >= 0; index--)
        {
            if (tools[index] is AIFunctionDeclaration declaration)
            {
                tools[index] = new CacheBreakpointTool(declaration);
                return;
            }
        }
    }

    private static bool TryMark(ChatMessage message)
    {
        for (var index = message.Contents.Count - 1; index >= 0; index--)
        {
            var content = message.Contents[index];
            if (IsCacheable(content))
            {
                content.WithCacheControl(new CacheControlEphemeral());
                return true;
            }
        }

        return false;
    }

    // What the Anthropic client sends as a block that takes a marker: thinking does not, and text that is empty is
    // not sent at all.
    private static bool IsCacheable(AIContent content)
        => content switch
        {
            TextContent text => !string.IsNullOrWhiteSpace(text.Text),
            FunctionCallContent or FunctionResultContent => true,
            DataContent data => data.HasTopLevelMediaType("image") || data.HasTopLevelMediaType("text") || IsPdf(data.MediaType),
            UriContent uri => uri.HasTopLevelMediaType("image") || IsPdf(uri.MediaType),
            _ => false,
        };

    private static bool IsPdf(string? mediaType)
        => string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase);

    // The Anthropic client reads the marker of a function tool from its additional properties.
    private sealed class CacheBreakpointTool(AIFunctionDeclaration inner) : AIFunctionDeclaration
    {
        public override string Name => inner.Name;

        public override string Description => inner.Description;

        public override JsonElement JsonSchema => inner.JsonSchema;

        public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; } =
            new Dictionary<string, object?>(inner.AdditionalProperties, StringComparer.Ordinal)
            {
                [nameof(Tool.CacheControl)] = new CacheControlEphemeral(),
            };

        public override object? GetService(System.Type serviceType, object? serviceKey = null)
            => inner.GetService(serviceType, serviceKey);
    }
}
