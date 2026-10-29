using System.Text.Json;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Claude;

// A capacity is selectable only through an enabled pair that initialize actually listed, not a model-name table.
// Keep the public model unchanged: the launch alone uses its matching CLI entry and process-local restriction.
internal static class ClaudeCodeContextSizes
{
    internal const string DisableVariable = "CLAUDE_CODE_DISABLE_1M_CONTEXT";
    internal const string ListedCapability = "claudeCodeListedModel";
    private const string StandardModelCapability = "claudeCode200KModel";
    private const string ExtendedModelCapability = "claudeCode1MModel";
    private const string ExtendedSuffix = "[1m]";

    internal static bool IsRestricted(ClaudeCodeModelProviderRuntimeOptions options)
        => IsEnabled((options.GetEnvironmentVariable ?? Environment.GetEnvironmentVariable)(DisableVariable)) ||
           options.EnvironmentVariables.Any(static variable => IsDisableVariable(variable.Key) && IsEnabled(variable.Value));

    internal static bool IsRestricted(IReadOnlyDictionary<string, string?> environment)
        => environment.Any(static variable => IsDisableVariable(variable.Key) && IsEnabled(variable.Value));

    private static bool IsDisableVariable(string name)
        => string.Equals(name, DisableVariable, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsEnabled(string? value)
        => !string.IsNullOrWhiteSpace(value) && value != "0" && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<AgentModelInfo> Describe(
        IReadOnlyList<AgentModelInfo> models, ClaudeCodeModelProviderRuntimeOptions options)
    {
        var result = models.Select(model => model with { Provider = options.ProviderKey.Trim() }).ToArray();
        if (IsRestricted(options)) return result;

        for (var index = 0; index < result.Length; index++)
        {
            var extended = result[index];
            if (!extended.Id.EndsWith(ExtendedSuffix, StringComparison.Ordinal) ||
                extended.Capabilities?.GetValueOrDefault(ListedCapability) is not true ||
                Read(extended, "resolvedModel") is not { Length: > 0 } extendedResolved) continue;
            var standardId = extended.Id[..^ExtendedSuffix.Length];
            var standardIndex = Array.FindIndex(result, model => string.Equals(model.Id, standardId, StringComparison.Ordinal));
            if (standardIndex < 0) continue;
            var standard = result[standardIndex];
            if (standard.Capabilities?.GetValueOrDefault(ListedCapability) is not true ||
                Read(standard, "resolvedModel") is not { Length: > 0 } standardResolved ||
                !string.Equals(WithoutSuffix(standardResolved), WithoutSuffix(extendedResolved), StringComparison.Ordinal)) continue;

            result[index] = WithChoices(extended, standard.Id, extended.Id, 1_000_000);
            result[standardIndex] = WithChoices(standard, standard.Id, extended.Id, null);
        }
        return result;
    }

    private static string WithoutSuffix(string value)
        => value.EndsWith(ExtendedSuffix, StringComparison.Ordinal) ? value[..^ExtendedSuffix.Length] : value;

    private static AgentModelInfo WithChoices(AgentModelInfo model, string standard, string extended, long? defaultTokens)
    {
        var capabilities = new Dictionary<string, object?>(model.Capabilities!, StringComparer.Ordinal)
        {
            [StandardModelCapability] = standard,
            [ExtendedModelCapability] = extended,
        };
        return model with
        {
            Capabilities = capabilities,
            ContextSizes = new(defaultTokens, [200_000, 1_000_000], IsProviderManaged: true),
        };
    }

    internal static (string? Model, long? Tokens) Resolve(AgentTurnRequest request, ClaudeCodeModelProviderRuntimeOptions options)
    {
        var publicModel = ClaudeCodeLauncher.ToModelOption(request.ModelId);
        if (request.ModelInfo is not { ContextSizes.IsProviderManaged: true, EffectiveContextWindowTokens: { } tokens } model)
            return (publicModel, null);
        if (!string.Equals(model.Id, request.ModelId, StringComparison.Ordinal) ||
            !string.Equals(model.Provider, options.ProviderKey.Trim(), StringComparison.Ordinal) ||
            !model.ContextSizes.Supports(tokens) ||
            Read(model, StandardModelCapability) is not { Length: > 0 } standard ||
            Read(model, ExtendedModelCapability) is not { Length: > 0 } extended ||
            tokens is not (200_000 or 1_000_000))
            throw new InvalidOperationException("The Claude Code context choice is no longer available. Refresh its models and select the context again.");

        if (tokens == 1_000_000 && IsRestricted(options))
            throw new InvalidOperationException("Claude Code's 1M context is disabled by the inherited or provider environment. Select Default or 200K.");
        return (tokens == 200_000 ? standard : extended, tokens);
    }

    private static string? Read(AgentModelInfo model, string key)
        => model.Capabilities?.GetValueOrDefault(key) switch
        {
            string value => value,
            JsonElement { ValueKind: JsonValueKind.String } value => value.GetString(),
            _ => null,
        };
}
