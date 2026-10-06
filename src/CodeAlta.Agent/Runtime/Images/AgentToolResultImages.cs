using System.Globalization;

namespace CodeAlta.Agent.Runtime.Images;

/// <summary>
/// What the runtime and the providers share about the images of a tool result: their text form, and the
/// conversation rewritten for an API whose tool results are text only.
/// </summary>
internal static class AgentToolResultImages
{
    /// <summary>First line of the user message that carries the images of tool results.</summary>
    public const string AttachedImagesHeading = "Images returned by the tool calls above:";

    /// <summary>Tells whether an item is an image a model looks at.</summary>
    /// <param name="item">An item of a tool result.</param>
    /// <returns>True for encoded bytes and for a saved image; false for text and for an image URL.</returns>
    public static bool IsImage(AgentToolResultItem item)
        => item is AgentToolResultItem.Image or AgentToolResultItem.LocalImage;

    /// <summary>Tells whether a tool result has an image.</summary>
    /// <param name="result">The result.</param>
    /// <returns>True when one of its items is an image.</returns>
    public static bool HasImages(AgentToolResult result)
    {
        foreach (var item in result.Items)
        {
            if (IsImage(item)) return true;
        }

        return false;
    }

    /// <summary>Tells whether a conversation has a tool result with an image.</summary>
    /// <param name="conversation">The messages.</param>
    /// <returns>True when a tool result of a message has an image.</returns>
    public static bool HasImages(IReadOnlyList<AgentConversationMessage> conversation)
    {
        foreach (var message in conversation)
        {
            foreach (var part in message.Parts)
            {
                if (part is AgentMessagePart.ToolResult toolResult && HasImages(toolResult.Result)) return true;
            }
        }

        return false;
    }

    /// <summary>Names an image in one line: what a text-only surface shows in its place.</summary>
    /// <param name="item">An image item.</param>
    /// <returns><c>[Image: name (image/png, 1280x720)]</c>, without what is unknown.</returns>
    public static string Describe(AgentToolResultItem item) => $"[Image: {Name(item)}]";

    /// <summary>Names an image by what is known of it.</summary>
    /// <param name="item">An image item.</param>
    /// <returns><c>name (image/png, 1280x720)</c>, without what is unknown.</returns>
    public static string Name(AgentToolResultItem item)
    {
        var (name, mediaType, width, height) = item switch
        {
            AgentToolResultItem.LocalImage local => (local.DisplayName, local.MediaType, local.Width, local.Height),
            AgentToolResultItem.Image image => (image.DisplayName, image.MediaType, 0, 0),
            _ => (null, null, 0, 0),
        };
        var facts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(mediaType)) facts.Add(mediaType.Trim());
        if (width > 0 && height > 0) facts.Add(string.Create(CultureInfo.InvariantCulture, $"{width}x{height}"));
        var label = string.IsNullOrWhiteSpace(name) ? "image" : name.Trim();
        return facts.Count == 0 ? label : $"{label} ({string.Join(", ", facts)})";
    }

    /// <summary>Renders a tool result as text: its text items, and one line for each image.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The lines joined by a line break; the error when the result has no item.</returns>
    public static string RenderText(AgentToolResult result)
    {
        if (result.Items.Count == 0)
        {
            return result.Error ?? string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            result.Items.Select(static item => item switch
            {
                AgentToolResultItem.Text text => text.Value,
                AgentToolResultItem.ImageUrl imageUrl => imageUrl.Url,
                AgentToolResultItem.Image or AgentToolResultItem.LocalImage => Describe(item),
                _ => string.Empty,
            }).Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    /// <summary>
    /// Rewrites a conversation for an API whose tool results are text: each image leaves its tool result, which
    /// keeps a line that names it, and the images of consecutive tool results are attached to one user message
    /// placed after them.
    /// </summary>
    /// <param name="conversation">The conversation, with images as <see cref="AgentToolResultItem.Image"/>.</param>
    /// <returns>The rewritten conversation; <paramref name="conversation"/> itself when it has no such image.</returns>
    public static IReadOnlyList<AgentConversationMessage> MoveToUserMessages(IReadOnlyList<AgentConversationMessage> conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (!HasImages(conversation)) return conversation;

        var result = new List<AgentConversationMessage>(conversation.Count + 1);
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var attached = new List<AgentMessagePart>();
        foreach (var message in conversation)
        {
            if (message.Role != AgentConversationRole.Tool)
            {
                Flush();
                foreach (var part in message.Parts)
                {
                    if (part is AgentMessagePart.ToolCall toolCall) toolNames[toolCall.CallId] = toolCall.Name;
                }

                result.Add(message);
                continue;
            }

            var parts = new List<AgentMessagePart>(message.Parts.Count);
            foreach (var part in message.Parts)
            {
                if (part is not AgentMessagePart.ToolResult toolResult || !toolResult.Result.Items.Any(static item => item is AgentToolResultItem.Image))
                {
                    parts.Add(part);
                    continue;
                }

                var items = new List<AgentToolResultItem>(toolResult.Result.Items.Count);
                foreach (var item in toolResult.Result.Items)
                {
                    if (item is not AgentToolResultItem.Image image)
                    {
                        items.Add(item);
                        continue;
                    }

                    var description = Describe(image);
                    items.Add(new AgentToolResultItem.Text($"{description} The image is attached to the message that follows the tool results."));
                    attached.Add(new AgentMessagePart.Text(
                        toolNames.TryGetValue(toolResult.CallId, out var toolName) ? $"{description} from {toolName}:" : $"{description}:"));
                    attached.Add(new AgentMessagePart.Data(image.Base64Data, image.MediaType, image.DisplayName));
                }

                parts.Add(toolResult with { Result = toolResult.Result with { Items = items } });
            }

            result.Add(new AgentConversationMessage(message.Role, parts));
        }

        Flush();
        return result;

        void Flush()
        {
            if (attached.Count == 0) return;
            result.Add(new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text(AttachedImagesHeading), .. attached]));
            attached.Clear();
        }
    }

    /// <summary>Tells whether a message is the one <see cref="MoveToUserMessages"/> adds.</summary>
    /// <param name="message">A message of a rewritten conversation.</param>
    /// <returns>True for a user message that starts with <see cref="AttachedImagesHeading"/>.</returns>
    public static bool IsAttachedImagesMessage(AgentConversationMessage message)
        => message.Role == AgentConversationRole.User && message.Parts.Count > 0 &&
           message.Parts[0] is AgentMessagePart.Text { Value: AttachedImagesHeading };

    /// <summary>
    /// Replaces the images of tool results: <paramref name="replace"/> returns the item to keep in place of an
    /// image, or the image itself.
    /// </summary>
    /// <param name="messages">The messages.</param>
    /// <param name="include">Chooses the messages rewritten; the others are kept as they are.</param>
    /// <param name="replace">Maps an image item to the item kept.</param>
    /// <returns>The rewritten messages; <paramref name="messages"/> itself when nothing changed.</returns>
    public static IReadOnlyList<AgentConversationMessage> ReplaceImages(
        IReadOnlyList<AgentConversationMessage> messages,
        Func<AgentConversationMessage, bool> include,
        Func<AgentToolResultItem, AgentToolResultItem> replace)
    {
        AgentConversationMessage[]? rewritten = null;
        for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
        {
            var message = messages[messageIndex];
            if (message.Role != AgentConversationRole.Tool || !include(message)) continue;
            List<AgentMessagePart>? parts = null;
            for (var partIndex = 0; partIndex < message.Parts.Count; partIndex++)
            {
                if (message.Parts[partIndex] is not AgentMessagePart.ToolResult toolResult) continue;
                List<AgentToolResultItem>? items = null;
                for (var itemIndex = 0; itemIndex < toolResult.Result.Items.Count; itemIndex++)
                {
                    var item = toolResult.Result.Items[itemIndex];
                    if (!IsImage(item)) continue;
                    var replacement = replace(item);
                    if (ReferenceEquals(replacement, item)) continue;
                    items ??= [.. toolResult.Result.Items];
                    items[itemIndex] = replacement;
                }

                if (items is null) continue;
                parts ??= [.. message.Parts];
                parts[partIndex] = toolResult with { Result = toolResult.Result with { Items = items } };
            }

            if (parts is null) continue;
            rewritten ??= [.. messages];
            rewritten[messageIndex] = new AgentConversationMessage(message.Role, parts);
        }

        return rewritten ?? messages;
    }
}
