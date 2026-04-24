using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;

namespace CodeAlta.Agent.OpenAI.CodexSubscription;

internal static class CodexSubscriptionStaticModelCatalog
{
    // Offline fallback snapshot only. Prefer authenticated Codex model discovery when it is available.
    private static readonly CodexStaticModel[] Models =
    [
        new("gpt-5.4", "GPT-5.4", Hidden: false, SupportsImageInput: true, DefaultReasoningEffort: AgentReasoningEffort.Medium),
        new("gpt-5.4-mini", "GPT-5.4 mini", Hidden: false, SupportsImageInput: true, DefaultReasoningEffort: AgentReasoningEffort.Medium),
        new("gpt-5.3-codex", "GPT-5.3 Codex", Hidden: false, SupportsImageInput: true, DefaultReasoningEffort: AgentReasoningEffort.High),
        new("gpt-5.2", "GPT-5.2", Hidden: false, SupportsImageInput: true, DefaultReasoningEffort: AgentReasoningEffort.Medium),
        new("codex-auto-review", "Codex auto review", Hidden: true, SupportsImageInput: false, DefaultReasoningEffort: AgentReasoningEffort.High),
    ];

    public static IReadOnlyList<AgentModelInfo> List(
        LocalAgentProviderDescriptor providerDescriptor,
        string? configuredModelId = null)
    {
        ArgumentNullException.ThrowIfNull(providerDescriptor);

        if (!string.IsNullOrWhiteSpace(configuredModelId))
        {
            var model = Models.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, configuredModelId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (model is null)
            {
                throw new InvalidOperationException(
                    $"Codex subscription model '{configuredModelId.Trim()}' is not in the static API-supported model catalog.");
            }

            return [CreateModelInfo(model, providerDescriptor)];
        }

        return Models
            .Where(static model => !model.Hidden)
            .Select(model => CreateModelInfo(model, providerDescriptor))
            .ToArray();
    }

    public static bool Contains(string modelId)
        => Models.Any(model => string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase));

    private static AgentModelInfo CreateModelInfo(
        CodexStaticModel model,
        LocalAgentProviderDescriptor providerDescriptor)
        => new(
            model.Id,
            DisplayName: model.DisplayName,
            Provider: providerDescriptor.ProviderKey,
            DefaultReasoningEffort: model.DefaultReasoningEffort,
            SupportedReasoningEfforts:
            [
                AgentReasoningEffort.Low,
                AgentReasoningEffort.Medium,
                AgentReasoningEffort.High,
                AgentReasoningEffort.XHigh,
            ],
            Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["source"] = "codex-static-fallback",
                ["supportedInApi"] = true,
                ["hidden"] = model.Hidden,
                ["supportsReasoningSummary"] = true,
                ["supportsEncryptedReasoning"] = true,
                ["supportsTextVerbosity"] = true,
                ["supportsTools"] = true,
                ["supportsImageInput"] = model.SupportsImageInput,
                ["requiresWebSocket"] = false,
            });

    private sealed record CodexStaticModel(
        string Id,
        string DisplayName,
        bool Hidden,
        bool SupportsImageInput,
        AgentReasoningEffort DefaultReasoningEffort);
}
