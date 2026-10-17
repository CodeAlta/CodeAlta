using System.Text.RegularExpressions;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// A turn executor whose provider can be sent deferred tools (<see cref="AgentToolSpec.Deferred"/>): their definitions are in
/// the request, out of the model's context, and the provider loads one when a tool result references it.
/// </summary>
public interface IModelProviderToolDeferral
{
    /// <summary>Gets whether the provider of a request defers tools, for its model.</summary>
    /// <param name="request">The request, with all the tools of the session.</param>
    /// <returns>True to send the deferred tools as deferred and the tool that searches them; false to leave them out until loaded.</returns>
    bool DefersTools(AgentTurnRequest request);
}

/// <summary>
/// The rules of deferred tools: which providers defer them, and what a request of a provider without deferral is sent.
/// </summary>
public static partial class AgentToolDeferral
{
    // The text a ToolReference becomes for the client of a provider that defers tools, which its transport rewrites into the
    // block of its protocol. A private-use character keeps it from any text a tool could return.
    private const string ToolReferenceMarker = "tool_reference";

    /// <summary>
    /// Gets whether a provider defers tools for a model. <see cref="AgentProviderProfile.SupportsToolDeferral"/> decides when it
    /// is set; otherwise only the Claude API (Anthropic Messages, first party) with a Claude 4.5 model or later does, the
    /// models of its tool search.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="modelId">The model, or null when not known.</param>
    /// <returns>True when the provider defers tools for that model.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
    public static bool IsSupported(ModelProviderRuntimeDescriptor provider, string? modelId)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Profile?.SupportsToolDeferral is { } supported) return supported;
        return provider.TransportKind == AgentTransportKind.AnthropicMessages
            && (provider.BaseUri is null || string.Equals(provider.BaseUri.Host, "api.anthropic.com", StringComparison.OrdinalIgnoreCase))
            && IsClaudeModelWithToolSearch(modelId);
    }

    /// <summary>Gets whether a model id names a Claude model of version 4.5 or later, which take deferred tools.</summary>
    /// <param name="modelId">The model id, such as <c>claude-opus-4-5-20251101</c> or <c>claude-sonnet-5-5</c>.</param>
    /// <returns>True for Claude 4.5 or later; false for an older or unknown model.</returns>
    public static bool IsClaudeModelWithToolSearch(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        var match = ClaudeVersion().Match(modelId);
        if (!match.Success || !int.TryParse(match.Groups["major"].ValueSpan, out var major)) return false;
        var minor = match.Groups["minor"].Success && int.TryParse(match.Groups["minor"].ValueSpan, out var value) ? value : 0;
        return major > 4 || (major == 4 && minor >= 5);
    }

    /// <summary>Gets the names of the tools that the tool results of a conversation loaded (<see cref="AgentToolResultItem.ToolReference"/>).</summary>
    /// <param name="conversation">The conversation.</param>
    /// <returns>The names.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="conversation"/> is null.</exception>
    public static IReadOnlySet<string> LoadedTools(IReadOnlyList<AgentConversationMessage> conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in References(conversation)) names.Add(reference.ToolName);
        return names;
    }

    /// <summary>The line of text a <see cref="AgentToolResultItem.ToolReference"/> is for a provider without deferred tools.</summary>
    /// <param name="toolName">The name of the tool.</param>
    /// <returns>The line.</returns>
    public static string LoadedToolText(string toolName) => "Loaded tool: " + toolName;

    // What a request is sent, from all the tools of the session: a provider that defers them gets them all, the tool that
    // searches them while one is deferred; another provider gets the tools that are not deferred and those the conversation
    // loaded, and the loads of the conversation as text.
    internal static AgentTurnRequest Apply(AgentTurnRequest request, bool defers)
    {
        var anyDeferred = false;
        var anyLoaded = false;
        var anySearch = false;
        foreach (var tool in request.Tools)
        {
            anyDeferred |= tool.Spec.Deferred;
            anyLoaded |= !tool.Spec.Deferred && !tool.Spec.SearchesDeferredTools;
            anySearch |= tool.Spec.SearchesDeferredTools;
        }

        var hasReferences = References(request.Conversation).Any();
        if (!anyDeferred && !anySearch && !hasReferences) return request;
        // The search tool stays loaded, and a request needs a tool that is: without it, nothing is deferred.
        if (defers && anyDeferred && (anySearch || anyLoaded)) return request;
        if (defers && !anyDeferred) return request with { Tools = [.. request.Tools.Where(static tool => !tool.Spec.SearchesDeferredTools)] };

        var loaded = LoadedTools(request.Conversation);
        return request with
        {
            Tools = [.. request.Tools.Where(tool => !tool.Spec.SearchesDeferredTools && (!tool.Spec.Deferred || loaded.Contains(tool.Spec.Name)))],
            Conversation = hasReferences ? [.. request.Conversation.Select(ReferencesAsText)] : request.Conversation,
        };
    }

    // The text a chat client is given for a ToolReference, which the transport of a provider that defers tools rewrites.
    internal static string ToolReferenceText(string toolName) => ToolReferenceMarker + toolName;

    internal static bool TryReadToolReferenceText(string? text, out string toolName)
    {
        toolName = string.Empty;
        if (text is null || !text.StartsWith(ToolReferenceMarker, StringComparison.Ordinal)) return false;
        toolName = text[ToolReferenceMarker.Length..];
        return toolName.Length > 0;
    }

    private static IEnumerable<AgentToolResultItem.ToolReference> References(IReadOnlyList<AgentConversationMessage> conversation)
        => conversation.Where(static message => message.Role == AgentConversationRole.Tool)
            .SelectMany(static message => message.Parts)
            .OfType<AgentMessagePart.ToolResult>()
            .SelectMany(static part => part.Result.Items)
            .OfType<AgentToolResultItem.ToolReference>();

    private static AgentConversationMessage ReferencesAsText(AgentConversationMessage message)
    {
        if (message.Role != AgentConversationRole.Tool
            || !message.Parts.Any(static part => part is AgentMessagePart.ToolResult result && result.Result.Items.Any(static item => item is AgentToolResultItem.ToolReference)))
        {
            return message;
        }

        return message with
        {
            Parts = [.. message.Parts.Select(static part => part is AgentMessagePart.ToolResult result
                ? result with
                {
                    Result = result.Result with
                    {
                        Items = [.. result.Result.Items.Select(static item => item is AgentToolResultItem.ToolReference reference ? new AgentToolResultItem.Text(LoadedToolText(reference.ToolName)) : item)],
                    },
                }
                : part)],
        };
    }

    // claude-<family>-<major>[-<minor>], the minor of one or two digits: a date (-20250514) is no minor.
    [GeneratedRegex(@"claude-(?:[a-z]+-)?(?<major>\d{1,2})(?:[-.](?<minor>\d{1,2})(?!\d))?(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeVersion();
}
