using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.CodexAppServer;
using CodeAlta.CodexAppServer.Protocol;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.CodexCli;

/// <summary>The models of Codex as CodeAlta lists them, and the efforts both name.</summary>
internal static class CodexCliModels
{
    /// <summary>Presents the models <c>model/list</c> gives: the hidden ones are left out, the default one first.</summary>
    public static IReadOnlyList<AgentModelInfo> Present(IReadOnlyList<Model> models, IPluginModelCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(models);
        return [.. models.Where(static model => !model.Hidden).OrderByDescending(static model => model.IsDefault).Select(model => Present(model, catalog))];
    }

    private static AgentModelInfo Present(Model model, IPluginModelCatalog? catalog)
    {
        var efforts = model.SupportedReasoningEfforts.Select(option => ToEffort(option.ReasoningEffort.Value)).OfType<AgentReasoningEffort>().Distinct().ToArray();
        var capabilities = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [AgentReasoningEffortCapability.CompleteListKey] = true,
        };
        if (model.InputModalities is { } modalities)
        {
            capabilities["inputModalities"] = modalities.Select(static modality => JsonSerializer.Serialize(modality, CodexProtocolJsonContext.Default.InputModality).Trim('"')).ToArray();
        }

        var info = new AgentModelInfo(
            model.Id,
            string.IsNullOrWhiteSpace(model.DisplayName) ? model.Id : model.DisplayName,
            string.IsNullOrWhiteSpace(model.Description) ? null : model.Description,
            Provider: "openai",
            DefaultReasoningEffort: ToEffort(model.DefaultReasoningEffort.Value),
            SupportedReasoningEfforts: efforts.Length == 0 ? null : efforts,
            Capabilities: capabilities);
        return catalog?.Describe(info, catalogModelId: null, catalogProviderId: "openai") ?? info;
    }

    /// <summary>Gets the effort of CodeAlta a Codex effort is, or <see langword="null"/> for one CodeAlta does not have (<c>ultra</c>).</summary>
    public static AgentReasoningEffort? ToEffort(string? effort) => effort?.ToLowerInvariant() switch
    {
        "none" => AgentReasoningEffort.None,
        "minimal" => AgentReasoningEffort.Minimal,
        "low" => AgentReasoningEffort.Low,
        "medium" => AgentReasoningEffort.Medium,
        "high" => AgentReasoningEffort.High,
        "xhigh" => AgentReasoningEffort.XHigh,
        "max" => AgentReasoningEffort.Max,
        _ => null,
    };

    /// <summary>Gets the Codex effort of an effort of CodeAlta.</summary>
    public static string? FromEffort(AgentReasoningEffort? effort) => effort switch
    {
        AgentReasoningEffort.None => "none",
        AgentReasoningEffort.Minimal => "minimal",
        AgentReasoningEffort.Low => "low",
        AgentReasoningEffort.Medium => "medium",
        AgentReasoningEffort.High => "high",
        AgentReasoningEffort.XHigh => "xhigh",
        AgentReasoningEffort.Max => "max",
        _ => null,
    };
}

/// <summary>The failures of Codex, as a session classifies them.</summary>
internal static class CodexCliFailures
{
    /// <summary>The category of a provider whose app-server cannot be found or downloaded.</summary>
    public const string NotFoundCategory = "codex-cli-not-found";

    /// <summary>The category of a provider that is not signed in.</summary>
    public const string SignedOutCategory = "codex-cli-signed-out";

    /// <summary>The category of a provider whose app-server does not start or answer.</summary>
    public const string UnavailableCategory = "codex-cli-unavailable";

    /// <summary>Gets the kind of an error of Codex: the name of its <c>codexErrorInfo</c>.</summary>
    public static string? KindOf(CodexErrorInfo? info)
    {
        if (info is not { } value)
        {
            return null;
        }

        return value.Value.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Object => value.Value.EnumerateObject().Select(static property => property.Name).FirstOrDefault(),
            _ => null,
        };
    }

    /// <summary>Creates the exception that ends a turn Codex failed.</summary>
    public static AgentProviderFailureException FromTurnError(TurnError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var kind = KindOf(error.CodexErrorInfo);
        var message = string.IsNullOrWhiteSpace(error.AdditionalDetails) ? error.Message : $"{error.Message} ({error.AdditionalDetails})";
        var failure = kind switch
        {
            "usageLimitExceeded" or "sessionBudgetExceeded" => AgentFailureKind.QuotaExhausted,
            "rateLimitExceeded" => AgentFailureKind.RateLimited,
            "unauthorized" => AgentFailureKind.Auth,
            "modelNotFound" => AgentFailureKind.ModelUnavailable,
            "serverOverloaded" or "internalServerError" or "httpConnectionFailed" or "responseStreamConnectionFailed" or "responseStreamDisconnected"
                or "responseTooManyFailedAttempts" or "flexUnavailable" => AgentFailureKind.Unavailable,
            _ => AgentFailureKind.Other,
        };
        if (failure == AgentFailureKind.Auth)
        {
            message = $"Codex is not signed in, or its sign-in expired. Sign in again from Settings > Providers, then send the message again. ({message})";
        }

        return new AgentProviderFailureException($"Codex: {message}", new AgentFailureInfo(failure));
    }

    /// <summary>Creates the exception of an error the app-server answered a request with.</summary>
    public static AgentProviderFailureException FromRequest(CodexJsonRpcException exception, string action)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = exception.Message.Contains("configuration", StringComparison.OrdinalIgnoreCase)
            ? $"Codex could not {action}: {exception.Message}. A policy of your workspace (requirements.toml, an MDM profile or the cloud configuration of a Business workspace) may forbid what CodeAlta runs Codex with: full access with its own sandbox off."
            : $"Codex could not {action}: {exception.Message}";
        return new AgentProviderFailureException(message, AgentFailureClassifier.Classify(exception) ?? new AgentFailureInfo(AgentFailureKind.Other), exception);
    }
}
