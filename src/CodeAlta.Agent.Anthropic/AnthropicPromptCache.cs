using System.Text.Json;
using Anthropic.Models.Messages;
using CodeAlta.Agent.Runtime;
using Microsoft.Extensions.AI;

namespace CodeAlta.Agent.Anthropic;

/// <summary>
/// Marks the prompt-cache breakpoints of an Anthropic Messages request. The Messages API caches nothing by
/// itself: without a <c>cache_control</c> marker, every request of a session pays its whole prompt again at the
/// full input price.
/// </summary>
/// <remarks>
/// A request takes at most four markers. They go on the last tool that is not deferred, on the system prompt, on the last message, and
/// on the message that ended the previous request: the one before the last assistant message. The marker of the
/// last message writes the prefix the next request reads, at the place where that request puts its own marker, so
/// the read does not depend on how many blocks a turn added. When the instructions end with those of the session
/// (<see cref="AgentTurnRequest.SessionInstructions"/>), these are a second system block, and the marker goes on the first:
/// the same text for every session of a prompt, whose marker covers the tools before it, which then take none.
/// </remarks>
internal static class AnthropicPromptCache
{
    /// <summary>
    /// How long the API keeps what a request read or wrote when the markers name no TTL: five minutes.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>How long the API keeps what a request read or wrote when the markers name the TTL <c>1h</c>.</summary>
    public static readonly TimeSpan LongLifetime = TimeSpan.FromHours(1);

    /// <summary>Returns the messages and the options of a request with its breakpoints marked, kept five minutes.</summary>
    /// <param name="messages">The messages of the request. They belong to it: the marker is set on their content.</param>
    /// <param name="options">The options of the request, which are left as they are; null for none.</param>
    /// <returns>The messages, with the instructions of the options as system content, and the options to send.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is null.</exception>
    public static (IReadOnlyList<ChatMessage> Messages, ChatOptions? Options) MarkBreakpoints(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options)
        => MarkBreakpoints(messages, options, Lifetime);

    /// <summary>Returns the messages and the options of a request with its breakpoints marked.</summary>
    /// <param name="messages">The messages of the request. They belong to it: the marker is set on their content.</param>
    /// <param name="options">The options of the request, which are left as they are; null for none.</param>
    /// <param name="lifetime">
    /// <see cref="Lifetime"/> or <see cref="LongLifetime"/>: every marker names the same TTL, since an entry kept longer
    /// may not follow one kept shorter.
    /// </param>
    /// <returns>The messages, with the instructions of the options as system content, and the options to send.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is null.</exception>
    public static (IReadOnlyList<ChatMessage> Messages, ChatOptions? Options) MarkBreakpoints(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        TimeSpan lifetime)
        => MarkBreakpoints(messages, options, lifetime, sessionInstructions: null);

    /// <summary>
    /// Returns the messages and the options of a request with its breakpoints marked, the instructions of the session
    /// apart: they end the instructions of the options (<see cref="AgentInstructionText.Compose"/>), and are sent as a
    /// system block of their own after the one that takes the marker. That block is then the same for every session of a
    /// prompt, which read it from the cache, and its marker covers the tools before it, which take none.
    /// </summary>
    /// <param name="messages">The messages of the request. They belong to it: the marker is set on their content.</param>
    /// <param name="options">The options of the request, which are left as they are; null for none.</param>
    /// <param name="lifetime"><see cref="Lifetime"/> or <see cref="LongLifetime"/>, as for <see cref="MarkBreakpoints(IEnumerable{ChatMessage}, ChatOptions?, TimeSpan)"/>.</param>
    /// <param name="sessionInstructions">
    /// The instructions of the session as the instructions of the options end with them (<see cref="AgentInstructionText.Session"/>);
    /// null, or text they do not end with, for one system block.
    /// </param>
    /// <returns>The messages, with the instructions of the options as system content, and the options to send.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is null.</exception>
    public static (IReadOnlyList<ChatMessage> Messages, ChatOptions? Options) MarkBreakpoints(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        TimeSpan lifetime,
        string? sessionInstructions)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var longLived = lifetime == LongLifetime;

        var marked = messages.ToList();
        var leadingSystemMessages = 0;
        while (leadingSystemMessages < marked.Count && marked[leadingSystemMessages].Role == ChatRole.System)
        {
            leadingSystemMessages++;
        }

        var sharedMarked = false;
        if (options is not null && (options.Instructions is not null || options.Tools is { Count: > 0 }))
        {
            options = options.Clone();
            if (!string.IsNullOrWhiteSpace(options.Instructions))
            {
                // The instructions follow the leading system messages, as the Anthropic client orders them, but
                // as content: only content takes a marker. Those of the session are a block of their own, after the
                // marked one.
                var (shared, session) = SplitSession(options.Instructions, sessionInstructions);
                var system = new ChatMessage(ChatRole.System, [new TextContent(shared)]);
                if (session is not null)
                {
                    system.Contents.Add(new TextContent(session));
                    sharedMarked = TryMarkContent(system.Contents[0], longLived);
                }

                marked.Insert(leadingSystemMessages, system);
                leadingSystemMessages++;
                options.Instructions = null;
            }

            // The marker of the shared instructions covers the tools, which come before them.
            if (!sharedMarked) MarkLastTool(options, longLived);
        }

        for (var index = leadingSystemMessages - 1; index >= 0 && !sharedMarked; index--)
        {
            if (TryMark(marked[index], longLived))
            {
                break;
            }
        }

        var last = MarkLastMessage(marked, marked.Count - 1, leadingSystemMessages, longLived);
        var lastAssistant = last;
        while (lastAssistant >= leadingSystemMessages && marked[lastAssistant].Role != ChatRole.Assistant)
        {
            lastAssistant--;
        }

        if (lastAssistant >= leadingSystemMessages)
        {
            MarkLastMessage(marked, lastAssistant - 1, leadingSystemMessages, longLived);
        }

        return (marked, options);
    }

    // Marks the last message at or before an index that has content to mark, and returns its index.
    private static int MarkLastMessage(List<ChatMessage> messages, int from, int first, bool longLived)
    {
        for (var index = from; index >= first; index--)
        {
            if (messages[index].Role != ChatRole.System && TryMark(messages[index], longLived))
            {
                return index;
            }
        }

        return first - 1;
    }

    private static void MarkLastTool(ChatOptions options, bool longLived)
    {
        if (options.Tools is not { Count: > 0 } tools)
        {
            return;
        }

        // A deferred tool takes no marker (the API refuses it): the marker goes on the last tool that is loaded.
        for (var index = tools.Count - 1; index >= 0; index--)
        {
            if (tools[index] is AIFunctionDeclaration declaration && !AnthropicToolDeferral.IsDeferred(declaration))
            {
                tools[index] = new CacheBreakpointTool(declaration, CreateMarker(longLived));
                return;
            }
        }
    }

    private static bool TryMark(ChatMessage message, bool longLived)
    {
        for (var index = message.Contents.Count - 1; index >= 0; index--)
        {
            if (TryMarkContent(message.Contents[index], longLived))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryMarkContent(AIContent content, bool longLived)
    {
        if (!IsCacheable(content)) return false;
        content.WithCacheControl(CreateMarker(longLived));
        return true;
    }

    // The instructions without those of the session, and those of the session; the whole text and null when the
    // instructions do not end with them.
    private static (string Shared, string? Session) SplitSession(string instructions, string? sessionInstructions)
    {
        if (string.IsNullOrWhiteSpace(sessionInstructions) || !instructions.EndsWith(sessionInstructions, StringComparison.Ordinal))
        {
            return (instructions, null);
        }

        var shared = instructions[..^sessionInstructions.Length].TrimEnd();
        return shared.Length == 0 ? (instructions, null) : (shared, sessionInstructions);
    }

    private static CacheControlEphemeral CreateMarker(bool longLived)
        => longLived ? new CacheControlEphemeral { Ttl = Ttl.Ttl1h } : new CacheControlEphemeral();

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
    private sealed class CacheBreakpointTool(AIFunctionDeclaration inner, CacheControlEphemeral marker) : AIFunctionDeclaration
    {
        public override string Name => inner.Name;

        public override string Description => inner.Description;

        public override JsonElement JsonSchema => inner.JsonSchema;

        public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; } =
            new Dictionary<string, object?>(inner.AdditionalProperties, StringComparer.Ordinal)
            {
                [nameof(Tool.CacheControl)] = marker,
            };

        public override object? GetService(System.Type serviceType, object? serviceKey = null)
            => inner.GetService(serviceType, serviceKey);
    }
}
