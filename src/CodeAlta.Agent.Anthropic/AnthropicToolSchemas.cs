using System.Text.Json;
using CodeAlta.Agent.Runtime.Tools;
using Microsoft.Extensions.AI;

namespace CodeAlta.Agent.Anthropic;

/// <summary>
/// The tool schemas of an Anthropic Messages request, rewritten for what the API refuses: <c>anyOf</c>, <c>oneOf</c> or
/// <c>allOf</c> at the root of an <c>input_schema</c> ("input_schema does not support oneOf, allOf, or anyOf at the top
/// level"), which the SDK sends as it is. Such a schema, as an MCP server may declare one, is merged into one object
/// (<see cref="AgentToolRootChoices"/>); any other schema goes as it is.
/// </summary>
internal static class AnthropicToolSchemas
{
    /// <summary>
    /// Merges the choice at the root of the schema of each tool of a request that has one.
    /// </summary>
    /// <param name="options">The options of the request.</param>
    /// <returns>The same options when no schema changes, else a copy with the merged tools.</returns>
    public static ChatOptions MergeRootChoices(ChatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Tools is not { Count: > 0 } tools ||
            !tools.Any(static tool => tool is AIFunctionDeclaration declaration && AgentToolRootChoices.HasRootChoice(declaration.JsonSchema)))
        {
            return options;
        }

        var merged = options.Clone();
        merged.Tools = [.. tools.Select(static tool =>
            tool is AIFunctionDeclaration declaration && AgentToolRootChoices.TryMerge(declaration.JsonSchema, out var schema)
                ? new MergedTool(declaration, schema)
                : tool)];
        return merged;
    }

    private sealed class MergedTool(AIFunctionDeclaration inner, JsonElement schema) : AIFunctionDeclaration
    {
        public override string Name => inner.Name;

        public override string Description => inner.Description;

        public override JsonElement JsonSchema => schema;

        public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties => inner.AdditionalProperties;

        public override object? GetService(Type serviceType, object? serviceKey = null)
            => inner.GetService(serviceType, serviceKey);
    }
}
