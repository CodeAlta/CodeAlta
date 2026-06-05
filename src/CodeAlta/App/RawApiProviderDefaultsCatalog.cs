using System.Text.Json.Serialization;
using CodeAlta.Agent.Runtime;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal static class RawApiProviderDefaultsCatalog
{
    private const string ProviderDefaultsRelativePath = "ProviderDefaults/provider_defaults.toml";
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.ProviderDefaults");

    public static AgentProviderProfile ApplyProfileDefaults(
        AgentTransportKind transportKind,
        string providerKey,
        Uri? baseUri,
        AgentProviderProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentNullException.ThrowIfNull(profile);

        var context = new RawApiProviderDefaultsContext(transportKind, providerKey.Trim(), baseUri);
        foreach (var rule in LoadRules())
        {
            if (IsMatch(rule, context) && rule.Profile is not null)
            {
                profile = ApplyProfile(profile, rule.Profile);
            }
        }

        return profile;
    }

    public static IReadOnlyDictionary<string, object?>? ApplyOpenAIExtraBodyDefaults(
        AgentTransportKind transportKind,
        string providerKey,
        Uri? baseUri,
        IReadOnlyDictionary<string, object?>? extraBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        var context = new RawApiProviderDefaultsContext(transportKind, providerKey.Trim(), baseUri);
        foreach (var rule in LoadRules())
        {
            if (IsMatch(rule, context) && rule.OpenAI?.ExtraBodyDefaults is { Count: > 0 } defaults)
            {
                extraBody = MergeExtraBody(extraBody, ConvertTomlTable(defaults));
            }
        }

        return extraBody;
    }

    public static IReadOnlyDictionary<string, object?>? CreateOpenAIExtraBodyDefaults(
        AgentTransportKind transportKind,
        string providerKey,
        Uri? baseUri)
        => ApplyOpenAIExtraBodyDefaults(transportKind, providerKey, baseUri, null);

    public static IReadOnlyDictionary<string, string>? CreateHeaderDefaults(
        AgentTransportKind transportKind,
        string providerKey,
        Uri? baseUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        IReadOnlyDictionary<string, string>? headers = null;
        var context = new RawApiProviderDefaultsContext(transportKind, providerKey.Trim(), baseUri);
        foreach (var rule in LoadRules())
        {
            if (IsMatch(rule, context) && rule.Headers is not null)
            {
                headers = MergeHeaders(headers, rule.Headers.Default, rule.Headers.Remove);
            }
        }

        return headers;
    }

    private static IReadOnlyList<RawApiProviderDefaultsRule> LoadRules()
    {
        var path = Path.Combine(AppContext.BaseDirectory, ProviderDefaultsRelativePath);
        if (!File.Exists(path))
        {
            Logger.Warn($"Provider defaults content file '{path}' was not found; using built-in compatibility fallback defaults.");
            return CreateFallbackRules();
        }

        try
        {
            var document = TomlSerializer.Deserialize(
                File.ReadAllText(path),
                RawApiProviderDefaultsTomlContext.Default.RawApiProviderDefaultsDocument);
            var rules = document?.Rules?
                .Where(static rule => !string.IsNullOrWhiteSpace(rule.Id))
                .ToArray();
            return rules is { Length: > 0 }
                ? rules
                : CreateFallbackRules();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TomlException or InvalidOperationException or FormatException)
        {
            Logger.Warn($"Failed to load provider defaults content file '{path}': {ex.Message}; using built-in compatibility fallback defaults.");
            return CreateFallbackRules();
        }
    }

    private static IReadOnlyList<RawApiProviderDefaultsRule> CreateFallbackRules()
        =>
        [
            new RawApiProviderDefaultsRule
            {
                Id = "alibaba-dashscope-openai-chat",
                DisplayName = "Alibaba DashScope OpenAI Chat",
                Types = ["openai-chat"],
                ProviderKeys = ["alibaba", "dashscope", "aliyun"],
                HostSuffixes = [
                    "dashscope.aliyuncs.com",
                    "dashscope-intl.aliyuncs.com",
                    "dashscope-us.aliyuncs.com",
                    "cn-hongkong.dashscope.aliyuncs.com",
                    "coding-intl.dashscope.aliyuncs.com",
                    "maas.aliyuncs.com",
                ],
                Profile = new RawApiProviderDefaultsProfile
                {
                    SupportsDeveloperRole = false,
                    SupportsStore = false,
                    SupportsReasoningEffort = false,
                    MaxTokensFieldName = "max_tokens",
                },
                OpenAI = new RawApiProviderDefaultsOpenAI
                {
                    ExtraBodyDefaults = new TomlTable(true)
                    {
                        ["stream_options"] = new TomlTable(true)
                        {
                            ["include_usage"] = true,
                        },
                        ["enable_thinking"] = true,
                        ["preserve_thinking"] = true,
                    },
                },
            },
            new RawApiProviderDefaultsRule
            {
                Id = "minimax-openai-chat",
                DisplayName = "MiniMax OpenAI Chat",
                Types = ["openai-chat"],
                ProviderKeys = ["minimax"],
                HostSuffixes = ["minimax.io", "minimaxi.com"],
                Profile = new RawApiProviderDefaultsProfile
                {
                    SupportsDeveloperRole = false,
                    ReasoningFieldNamesPrepend = ["reasoning_details[0].text"],
                },
                OpenAI = new RawApiProviderDefaultsOpenAI
                {
                    ExtraBodyDefaults = new TomlTable(true)
                    {
                        ["reasoning_split"] = true,
                    },
                },
            },
            new RawApiProviderDefaultsRule
            {
                Id = "deepseek-openai-chat",
                DisplayName = "DeepSeek OpenAI Chat",
                Types = ["openai-chat"],
                ProviderKeys = ["deepseek"],
                HostSuffixes = ["deepseek.com"],
                Profile = new RawApiProviderDefaultsProfile
                {
                    SupportsDeveloperRole = false,
                    SupportsStore = false,
                    MaxTokensFieldName = "max_tokens",
                    ReasoningInputFieldName = "reasoning_content",
                },
                OpenAI = new RawApiProviderDefaultsOpenAI
                {
                    ExtraBodyDefaults = new TomlTable(true)
                    {
                        ["thinking"] = new TomlTable(true)
                        {
                            ["type"] = "enabled",
                        },
                    },
                },
            },
            new RawApiProviderDefaultsRule
            {
                Id = "xiaomi-openai-chat",
                DisplayName = "Xiaomi OpenAI Chat",
                Types = ["openai-chat"],
                ProviderKeys = ["xiaomi"],
                HostSuffixes = ["xiaomimimo.com"],
                Profile = new RawApiProviderDefaultsProfile
                {
                    SupportsDeveloperRole = false,
                    SupportsStore = false,
                    SupportsReasoningEffort = false,
                    SupportsParallelToolCalls = false,
                    SupportsStrictTools = false,
                    MaxTokensFieldName = "max_tokens",
                },
            },
        ];

    private static bool IsMatch(RawApiProviderDefaultsRule rule, RawApiProviderDefaultsContext context)
    {
        if (rule.Types is { Count: > 0 } types &&
            !types.Any(type => IsTransportTypeMatch(type, context.TransportKind)))
        {
            return false;
        }

        var hasProviderKeyCriteria = rule.ProviderKeys is { Count: > 0 };
        var hasHostCriteria = rule.HostSuffixes is { Count: > 0 };
        if (!hasProviderKeyCriteria && !hasHostCriteria)
        {
            return true;
        }

        return (hasProviderKeyCriteria && rule.ProviderKeys!.Any(providerKey =>
                   string.Equals(providerKey?.Trim(), context.ProviderKey, StringComparison.OrdinalIgnoreCase))) ||
               (hasHostCriteria && rule.HostSuffixes!.Any(hostSuffix => HasHost(context.BaseUri, hostSuffix)));
    }

    private static bool IsTransportTypeMatch(string? configuredType, AgentTransportKind transportKind)
    {
        var normalized = configuredType?.Trim().ToLowerInvariant();
        return transportKind switch
        {
            AgentTransportKind.OpenAIChatCompletions => normalized is "openai-chat" or "openai" or "chat",
            AgentTransportKind.OpenAIResponses => normalized is "openai-responses" or "responses",
            AgentTransportKind.AnthropicMessages => normalized is "anthropic" or "anthropic-messages",
            AgentTransportKind.GoogleGeminiApi => normalized is "google" or "google-genai" or "gemini",
            AgentTransportKind.GoogleVertexAI => normalized is "vertex" or "vertex-ai" or "google-vertex",
            _ => false,
        };
    }

    private static AgentProviderProfile ApplyProfile(
        AgentProviderProfile profile,
        RawApiProviderDefaultsProfile defaults)
    {
        var reasoningFieldNames = profile.ReasoningFieldNames;
        if (defaults.ReasoningFieldNamesPrepend is { Count: > 0 } prepend)
        {
            reasoningFieldNames = PrependDistinct(reasoningFieldNames, [.. prepend]);
        }

        if (defaults.ReasoningFieldNamesAppend is { Count: > 0 } append)
        {
            reasoningFieldNames = AppendDistinct(reasoningFieldNames, [.. append]);
        }

        return profile with
        {
            SupportsDeveloperRole = defaults.SupportsDeveloperRole ?? profile.SupportsDeveloperRole,
            SupportsStore = defaults.SupportsStore ?? profile.SupportsStore,
            SupportsReasoningEffort = defaults.SupportsReasoningEffort ?? profile.SupportsReasoningEffort,
            SupportsParallelToolCalls = defaults.SupportsParallelToolCalls ?? profile.SupportsParallelToolCalls,
            StreamsUsage = defaults.StreamsUsage ?? profile.StreamsUsage,
            SupportsThoughtSignatures = defaults.SupportsThoughtSignatures ?? profile.SupportsThoughtSignatures,
            RequiresToolResultName = defaults.RequiresToolResultName ?? profile.RequiresToolResultName,
            RequiresAssistantAfterToolResult = defaults.RequiresAssistantAfterToolResult ?? profile.RequiresAssistantAfterToolResult,
            SupportsCacheControl = defaults.SupportsCacheControl ?? profile.SupportsCacheControl,
            SupportsStrictTools = defaults.SupportsStrictTools ?? profile.SupportsStrictTools,
            ThinkingFormat = string.IsNullOrWhiteSpace(defaults.ThinkingFormat)
                ? profile.ThinkingFormat
                : defaults.ThinkingFormat.Trim(),
            MaxTokensFieldName = string.IsNullOrWhiteSpace(defaults.MaxTokensFieldName)
                ? profile.MaxTokensFieldName
                : defaults.MaxTokensFieldName.Trim(),
            ReasoningFieldNames = defaults.ReasoningFieldNames is { Count: > 0 } reasoningFieldNamesOverride
                ? [.. reasoningFieldNamesOverride.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value.Trim())]
                : reasoningFieldNames,
            ReasoningInputFieldName = string.IsNullOrWhiteSpace(defaults.ReasoningInputFieldName)
                ? profile.ReasoningInputFieldName
                : defaults.ReasoningInputFieldName.Trim(),
        };
    }

    private static bool HasHost(Uri? baseUri, string? expectedHost)
    {
        if (string.IsNullOrWhiteSpace(expectedHost))
        {
            return false;
        }

        var host = baseUri?.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalizedExpectedHost = expectedHost.Trim();
        return host.Equals(normalizedExpectedHost, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith($".{normalizedExpectedHost}", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> PrependDistinct(
        IReadOnlyList<string> existingValues,
        params string[] newValues)
    {
        ArgumentNullException.ThrowIfNull(existingValues);
        ArgumentNullException.ThrowIfNull(newValues);

        return [.. newValues.Concat(existingValues).Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value.Trim()).Distinct(StringComparer.Ordinal)];
    }

    private static IReadOnlyList<string> AppendDistinct(
        IReadOnlyList<string> existingValues,
        params string[] newValues)
    {
        ArgumentNullException.ThrowIfNull(existingValues);
        ArgumentNullException.ThrowIfNull(newValues);

        return [.. existingValues.Concat(newValues).Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value.Trim()).Distinct(StringComparer.Ordinal)];
    }

    private static IReadOnlyDictionary<string, object?>? MergeExtraBody(
        IReadOnlyDictionary<string, object?>? configured,
        IReadOnlyDictionary<string, object?> defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        if (configured is null || configured.Count == 0)
        {
            return defaults.Count == 0
                ? null
                : new Dictionary<string, object?>(defaults, StringComparer.Ordinal);
        }

        var merged = new Dictionary<string, object?>(defaults, StringComparer.Ordinal);
        foreach (var entry in configured)
        {
            merged[entry.Key] = entry.Value;
        }

        return merged;
    }

    private static IReadOnlyDictionary<string, string>? MergeHeaders(
        IReadOnlyDictionary<string, string>? configured,
        Dictionary<string, string>? defaults,
        List<string>? remove)
    {
        Dictionary<string, string>? merged = configured is null || configured.Count == 0
            ? null
            : new Dictionary<string, string>(configured, StringComparer.OrdinalIgnoreCase);

        if (defaults is { Count: > 0 })
        {
            merged ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in defaults)
            {
                if (!string.IsNullOrWhiteSpace(entry.Key))
                {
                    merged[entry.Key.Trim()] = entry.Value ?? string.Empty;
                }
            }
        }

        if (merged is not null && remove is { Count: > 0 })
        {
            foreach (var header in remove)
            {
                if (!string.IsNullOrWhiteSpace(header))
                {
                    merged.Remove(header.Trim());
                }
            }
        }

        return merged is null || merged.Count == 0 ? null : merged;
    }

    private static IReadOnlyDictionary<string, object?> ConvertTomlTable(TomlTable table)
    {
        var converted = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in table)
        {
            if (!string.IsNullOrWhiteSpace(entry.Key))
            {
                converted[entry.Key.Trim()] = ConvertTomlValue(entry.Value);
            }
        }

        return converted;
    }

    private static object? ConvertTomlValue(object? value)
        => value switch
        {
            TomlTable table => ConvertTomlTable(table),
            TomlArray array => array.Select(ConvertTomlValue).ToArray(),
            _ => value,
        };

    private readonly record struct RawApiProviderDefaultsContext(
        AgentTransportKind TransportKind,
        string ProviderKey,
        Uri? BaseUri);
}

internal sealed class RawApiProviderDefaultsDocument
{
    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonPropertyName("rules")]
    public List<RawApiProviderDefaultsRule>? Rules { get; set; }
}

internal sealed class RawApiProviderDefaultsRule
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("types")]
    public List<string>? Types { get; set; }

    [JsonPropertyName("provider_keys")]
    public List<string>? ProviderKeys { get; set; }

    [JsonPropertyName("host_suffixes")]
    public List<string>? HostSuffixes { get; set; }

    [JsonPropertyName("profile")]
    public RawApiProviderDefaultsProfile? Profile { get; set; }

    [JsonPropertyName("openai")]
    public RawApiProviderDefaultsOpenAI? OpenAI { get; set; }

    [JsonPropertyName("headers")]
    public RawApiProviderDefaultsHeaders? Headers { get; set; }
}

internal sealed class RawApiProviderDefaultsHeaders
{
    [JsonPropertyName("default")]
    public Dictionary<string, string>? Default { get; set; }

    [JsonPropertyName("remove")]
    public List<string>? Remove { get; set; }
}

internal sealed class RawApiProviderDefaultsProfile
{
    [JsonPropertyName("supports_developer_role")]
    public bool? SupportsDeveloperRole { get; set; }

    [JsonPropertyName("supports_store")]
    public bool? SupportsStore { get; set; }

    [JsonPropertyName("supports_reasoning_effort")]
    public bool? SupportsReasoningEffort { get; set; }

    [JsonPropertyName("supports_parallel_tool_calls")]
    public bool? SupportsParallelToolCalls { get; set; }

    [JsonPropertyName("streams_usage")]
    public bool? StreamsUsage { get; set; }

    [JsonPropertyName("supports_thought_signatures")]
    public bool? SupportsThoughtSignatures { get; set; }

    [JsonPropertyName("requires_tool_result_name")]
    public bool? RequiresToolResultName { get; set; }

    [JsonPropertyName("requires_assistant_after_tool_result")]
    public bool? RequiresAssistantAfterToolResult { get; set; }

    [JsonPropertyName("supports_cache_control")]
    public bool? SupportsCacheControl { get; set; }

    [JsonPropertyName("supports_strict_tools")]
    public bool? SupportsStrictTools { get; set; }

    [JsonPropertyName("thinking_format")]
    public string? ThinkingFormat { get; set; }

    [JsonPropertyName("max_tokens_field_name")]
    public string? MaxTokensFieldName { get; set; }

    [JsonPropertyName("reasoning_field_names")]
    public List<string>? ReasoningFieldNames { get; set; }

    [JsonPropertyName("reasoning_field_names_prepend")]
    public List<string>? ReasoningFieldNamesPrepend { get; set; }

    [JsonPropertyName("reasoning_field_names_append")]
    public List<string>? ReasoningFieldNamesAppend { get; set; }

    [JsonPropertyName("reasoning_input_field_name")]
    public string? ReasoningInputFieldName { get; set; }
}

internal sealed class RawApiProviderDefaultsOpenAI
{
    [JsonPropertyName("extra_body_defaults")]
    public TomlTable? ExtraBodyDefaults { get; set; }
}

[TomlSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = TomlIgnoreCondition.WhenWritingNull)]
[TomlSerializable(typeof(RawApiProviderDefaultsDocument))]
internal partial class RawApiProviderDefaultsTomlContext : TomlSerializerContext
{
}
