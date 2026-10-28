#pragma warning disable OPENAI001

using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Tools;
using OpenAI.Responses;

namespace CodeAlta.Agent.OpenAI;

/// <summary>
/// The custom tools of the OpenAI Responses API: a tool whose input is one text (<see cref="AgentToolSpec.FreeformInput"/>)
/// is declared with <c>"type": "custom"</c> and its grammar, the model sends a <c>custom_tool_call</c> whose <c>input</c>
/// is that text, and the result goes back as a <c>custom_tool_call_output</c>. Codex declares its <c>exec</c> and
/// <c>apply_patch</c> this way, and the GPT-5 models and later are trained on them.
/// </summary>
internal static class OpenAIResponsesFreeformTools
{
    // The property a recorded raw-text call keeps its text in when its tool is no longer known.
    private const string FallbackArgumentName = "input";

    /// <summary>
    /// Gets the tools of the request to declare as custom tools, by their registered names, or null when the provider and
    /// the model take none: only OpenAI's own endpoints (the API and the ChatGPT subscription), and GPT-5 models or later.
    /// </summary>
    public static IReadOnlyDictionary<string, AgentToolFreeformInput>? Select(OpenAIProviderOptions provider, AgentTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);
        if (!IsSupported(provider, request.ModelId))
        {
            return null;
        }

        Dictionary<string, AgentToolFreeformInput>? tools = null;
        foreach (var tool in request.Tools)
        {
            if (tool.Spec.FreeformInput is { } input)
            {
                (tools ??= new(StringComparer.Ordinal))[AgentToolBridge.GetRegisteredToolName(tool.Spec.Name)] = input;
            }
        }

        return tools;
    }

    /// <summary>Gets whether the provider and the model take custom tools.</summary>
    public static bool IsSupported(OpenAIProviderOptions provider, string? modelId)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var isOpenAIEndpoint = provider.CodexSubscription is not null ||
            (!provider.IsAzureOpenAI &&
             (provider.BaseUri is null || string.Equals(provider.BaseUri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase)));
        if (!isOpenAIEndpoint || string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        // The models of the ChatGPT subscription are Codex models, all trained on them, whatever their name; on the API, a
        // model that does not name its generation (o3, gpt-4o) is older.
        return TryGetGptGeneration(modelId) is { } generation
            ? generation >= 5
            : provider.CodexSubscription is not null;
    }

    /// <summary>Creates the declaration of a tool as a custom tool.</summary>
    public static CustomTool CreateTool(AgentToolDefinition tool, AgentToolFreeformInput input)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(input);
        return new CustomTool(AgentToolBridge.GetRegisteredToolName(tool.Spec.Name))
        {
            ToolDescription = input.Description ?? tool.Spec.Description,
            ToolFormat = input.Syntax switch
            {
                AgentToolFreeformSyntax.Lark => new CustomToolGrammarFormat(input.Definition!, CustomToolGrammarFormatSyntax.Lark),
                AgentToolFreeformSyntax.Regex => new CustomToolGrammarFormat(input.Definition!, CustomToolGrammarFormatSyntax.Regex),
                _ => new CustomToolTextFormat(),
            },
        };
    }

    /// <summary>Creates the tool call a <c>custom_tool_call</c> of the model is for the session: its text in its JSON form.</summary>
    public static AgentMessagePart.ToolCall CreateToolCall(AgentTurnRequest request, CustomToolCallItem item)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(item);
        var input = item.Input ?? string.Empty;
        var tool = request.Tools.FirstOrDefault(tool =>
            tool.Spec.FreeformInput is not null &&
            string.Equals(AgentToolBridge.GetRegisteredToolName(tool.Spec.Name), item.ToolName, StringComparison.Ordinal));
        var arguments = tool?.Spec.FreeformInput is { } freeform
            ? freeform.CreateArguments(input)
            : AgentToolFreeformInput.CreateArguments(FallbackArgumentName, input);
        return new AgentMessagePart.ToolCall(item.CallId, item.ToolName, arguments) { Freeform = true };
    }

    /// <summary>
    /// Gets the text to send a recorded raw-text call again as a <c>custom_tool_call</c>: a call the model made that way,
    /// replayed to a request that declares custom tools. A call with JSON arguments goes back as a <c>function_call</c>.
    /// </summary>
    public static bool TryGetReplayText(
        AgentMessagePart.ToolCall toolCall,
        string registeredName,
        IReadOnlyDictionary<string, AgentToolFreeformInput>? freeformTools,
        out string text)
    {
        text = string.Empty;
        if (toolCall.Freeform != true || freeformTools is null)
        {
            return false;
        }

        if (freeformTools.TryGetValue(registeredName, out var input))
        {
            return input.TryGetText(toolCall.Arguments, out text);
        }

        // A tool the request no longer has (Code Mode turned off): its text is the only property of the arguments.
        if (toolCall.Arguments.ValueKind == JsonValueKind.Object)
        {
            using var properties = toolCall.Arguments.EnumerateObject();
            if (properties.MoveNext() && properties.Current.Value.ValueKind == JsonValueKind.String)
            {
                var value = properties.Current.Value.GetString() ?? string.Empty;
                if (!properties.MoveNext())
                {
                    text = value;
                    return true;
                }
            }
        }

        return false;
    }

    // "gpt-5.3-codex" is 5, "gpt-6-astra" is 6; null for a name without a generation.
    private static int? TryGetGptGeneration(string modelId)
    {
        var name = modelId.AsSpan().Trim();
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        if (!name.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        name = name[4..];
        var length = 0;
        while (length < name.Length && char.IsAsciiDigit(name[length]))
        {
            length++;
        }

        return length > 0 && int.TryParse(name[..length], NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            ? generation
            : null;
    }
}
