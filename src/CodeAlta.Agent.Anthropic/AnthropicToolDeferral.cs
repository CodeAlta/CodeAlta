using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Messages;
using CodeAlta.Agent.Runtime;
using Microsoft.Extensions.AI;

namespace CodeAlta.Agent.Anthropic;

/// <summary>
/// Deferred tools on the Claude API: the definition of a deferred tool is sent with <c>defer_loading: true</c>, which keeps it
/// out of the model's context and out of the cached prefix, and a tool result that loads it carries a <c>tool_reference</c>
/// block, which the API expands into the definition.
/// </summary>
/// <remarks>
/// The chat client of the SDK copies <c>defer_loading</c> from the additional properties of a declaration, but has no content
/// for a <c>tool_reference</c>: a <see cref="AgentToolResultItem.ToolReference"/> reaches it as a marked text
/// (<see cref="AgentToolDeferral"/>), which <see cref="ToolReferenceHandler"/> rewrites in the body of the request.
/// </remarks>
internal static class AnthropicToolDeferral
{
    /// <summary>Returns the options with the declarations of the deferred tools marked <c>defer_loading: true</c>.</summary>
    /// <param name="options">The options of the request.</param>
    /// <param name="deferred">The names of the deferred tools.</param>
    /// <returns>The options, or a clone with the marked declarations.</returns>
    public static ChatOptions MarkDeferred(ChatOptions options, IReadOnlySet<string> deferred)
    {
        if (deferred.Count == 0 || options.Tools is not { Count: > 0 } tools || !tools.Any(tool => tool is AIFunctionDeclaration declaration && deferred.Contains(declaration.Name)))
        {
            return options;
        }

        var marked = options.Clone();
        marked.Tools = [.. tools.Select(tool => tool is AIFunctionDeclaration declaration && deferred.Contains(declaration.Name) ? new DeferredTool(declaration) : tool)];
        return marked;
    }

    /// <summary>Whether a declaration is sent with <c>defer_loading: true</c>.</summary>
    public static bool IsDeferred(AITool tool)
        => tool.AdditionalProperties.TryGetValue(nameof(Tool.DeferLoading), out var value) && value is true;

    // The Anthropic client reads defer_loading from the additional properties of a function tool.
    private sealed class DeferredTool(AIFunctionDeclaration inner) : AIFunctionDeclaration
    {
        public override string Name => inner.Name;

        public override string Description => inner.Description;

        public override JsonElement JsonSchema => inner.JsonSchema;

        public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; } =
            new Dictionary<string, object?>(inner.AdditionalProperties, StringComparer.Ordinal)
            {
                [nameof(Tool.DeferLoading)] = true,
            };

        public override object? GetService(System.Type serviceType, object? serviceKey = null)
            => inner.GetService(serviceType, serviceKey);
    }

    /// <summary>
    /// Rewrites the body of a request: in a <c>tool_result</c>, each marked text of a tool reference becomes a
    /// <c>tool_reference</c> block when the request declares that tool, and a line of text otherwise (a tool that is gone,
    /// which the API would refuse). A result with references keeps only them, as the API documents it.
    /// </summary>
    /// <param name="body">The JSON body.</param>
    /// <returns>The body rewritten, or null when it has no reference.</returns>
    public static string? RewriteToolReferences(string body)
    {
        if (!body.Contains("\\uE000", StringComparison.OrdinalIgnoreCase) && !body.Contains('')) return null;
        if (JsonNode.Parse(body) is not JsonObject root || root["messages"] is not JsonArray messages) return null;
        var declared = new HashSet<string>(StringComparer.Ordinal);
        if (root["tools"] is JsonArray tools)
        {
            foreach (var tool in tools)
            {
                if (tool?["name"]?.GetValue<string>() is { } name) declared.Add(name);
            }
        }

        var changed = false;
        foreach (var message in messages)
        {
            if (message?["content"] is not JsonArray blocks) continue;
            foreach (var block in blocks)
            {
                if (block is JsonObject result && result["type"]?.GetValue<string>() == "tool_result" && result["content"] is JsonArray content)
                {
                    changed |= Rewrite(result, content, declared);
                }
            }
        }

        return changed ? root.ToJsonString() : null;
    }

    private static bool Rewrite(JsonObject result, JsonArray content, HashSet<string> declared)
    {
        var references = new List<JsonObject>();
        var others = new List<JsonNode>();
        JsonNode? marker = null;
        var any = false;
        foreach (var item in content)
        {
            if (item is null) continue;
            if (item is JsonObject text && text["type"]?.GetValue<string>() == "text" && AgentToolDeferral.TryReadToolReferenceText(text["text"]?.GetValue<string>(), out var name))
            {
                any = true;
                marker ??= text["cache_control"]?.DeepClone();
                if (declared.Contains(name)) references.Add(new JsonObject { ["type"] = "tool_reference", ["tool_name"] = name });
                else others.Add(new JsonObject { ["type"] = "text", ["text"] = AgentToolDeferral.LoadedToolText(name) + " (no longer available)" });
                continue;
            }

            if (item["cache_control"] is { } control) marker ??= control.DeepClone();
            others.Add(item.DeepClone());
        }

        if (!any) return false;
        content.Clear();
        if (references.Count > 0)
        {
            // The text of the result names what it loaded; the API gives the model the definitions instead.
            foreach (var reference in references) content.Add((JsonNode)reference);
            if (marker is not null && result["cache_control"] is null) result["cache_control"] = marker;
        }
        else
        {
            foreach (var other in others) content.Add(other);
        }

        return true;
    }

    /// <summary>The handler of the SDK client that rewrites the tool references of a request (<see cref="RewriteToolReferences"/>).</summary>
    public sealed class ToolReferenceHandler : DelegatingHandler
    {
        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.Content is { } content
                && request.RequestUri?.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal) == true)
            {
                var body = await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (RewriteToolReferences(body) is { } rewritten)
                {
                    var replaced = new StringContent(rewritten, Encoding.UTF8, content.Headers.ContentType?.MediaType ?? "application/json");
                    foreach (var header in content.Headers)
                    {
                        if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) && !string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                        {
                            replaced.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        }
                    }

                    request.Content = replaced;
                }
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
