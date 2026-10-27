using Microsoft.Extensions.AI;

namespace CodeAlta.Agent.Anthropic;

/// <summary>
/// Gathers the results of the parallel tool calls of an assistant turn in one message. A session keeps each result in a
/// tool message of its own, and the Anthropic client sends each tool message as a user message: Anthropic asks for all
/// the <c>tool_result</c> blocks of a turn in one user message, and a model that reads them apart makes fewer parallel
/// calls.
/// </summary>
/// <remarks>
/// Every request of a session is merged the same way, so the history renders the same each time: what a request sent
/// stays the prefix of the next one, for the prompt cache and the thinking blocks it replays.
/// </remarks>
internal static class AnthropicToolResultMessages
{
    /// <summary>Returns the messages with each run of consecutive tool messages merged into one, in order.</summary>
    /// <param name="messages">The messages of a request; they are left as they are.</param>
    /// <returns>The messages, or the same list when no tool message follows another.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is null.</exception>
    public static IReadOnlyList<ChatMessage> MergeConsecutive(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        List<ChatMessage>? merged = null;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role != ChatRole.Tool || index == 0 || messages[index - 1].Role != ChatRole.Tool)
            {
                merged?.Add(message);
                continue;
            }

            merged ??= [.. messages.Take(index)];
            if (ReferenceEquals(merged[^1], messages[index - 1]))
            {
                // The first message of the run is the caller's: the results go into a copy of it.
                merged[^1] = Copy(merged[^1]);
            }

            foreach (var content in message.Contents)
            {
                merged[^1].Contents.Add(content);
            }
        }

        return merged ?? messages;
    }

    private static ChatMessage Copy(ChatMessage message)
    {
        var copy = message.Clone();
        copy.Contents = [.. message.Contents];
        return copy;
    }
}
