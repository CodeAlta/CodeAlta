using CodeAlta.Catalog;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.ViewModels;

internal enum ModelProviderUiStatusKind
{
    Disabled,
    Configured,
    Warning,
    Error,
    Success,
}

internal enum ModelProviderLastTestState
{
    None,
    Testing,
    Success,
    Failed,
}

internal readonly record struct ModelProviderDiagnosticEntry(ValidationSeverity Severity, string Message);

internal sealed class ModelProviderDiagnosticsSnapshot
{
    public required ModelProviderUiStatusKind StatusKind { get; init; }

    public required string StatusText { get; init; }

    public required IReadOnlyList<ModelProviderDiagnosticEntry> Entries { get; init; }
}

internal static class ModelProviderEditorDiagnostics
{
    public static ModelProviderDiagnosticsSnapshot Analyze(
        ModelProviderEditorItemViewModel item,
        IReadOnlyList<ModelProviderEditorItemViewModel> providers)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(providers);

        var entries = new List<ModelProviderDiagnosticEntry>();

        Add(entries, ValidateProviderKey(item, providers));
        Add(entries, ValidateApiKey(item));
        Add(entries, ValidateApiKeyEnv(item));
        Add(entries, ValidateApiUrl(item));
        Add(entries, ValidateAzureOpenAIModel(item));
        Add(entries, ValidateVertexProject(item));
        Add(entries, ValidateVertexLocation(item));

        if (item.ProviderType == "vertex-ai" && item.Enabled)
        {
            entries.Add(new ModelProviderDiagnosticEntry(
                ValidationSeverity.Info,
                SR.T("Vertex AI uses Google application-default credentials from the current environment.")));
        }

        if (item.ProviderType == "azure-openai" && item.Enabled)
        {
            entries.Add(new ModelProviderDiagnosticEntry(
                ValidationSeverity.Info,
                SR.T("Azure OpenAI uses deployment names as model IDs; set Model or Single Model Id to your deployment name.")));
        }

        if (ShouldShowCustomApiUrlGuidance(item))
        {
            entries.Add(new ModelProviderDiagnosticEntry(
                ValidationSeverity.Info,
                SR.T("Custom API URL configured. Use Test Provider to verify endpoint reachability.")));
        }

        if (item.LastTestState == ModelProviderLastTestState.Success &&
            !string.IsNullOrWhiteSpace(item.LastTestMessage))
        {
            entries.Insert(0, new ModelProviderDiagnosticEntry(
                ValidationSeverity.Info,
                SR.T("Last test succeeded: {0}", item.LastTestMessage!.Trim())));
        }
        else if (item.LastTestState == ModelProviderLastTestState.Failed &&
                 !string.IsNullOrWhiteSpace(item.LastTestMessage))
        {
            entries.Insert(0, new ModelProviderDiagnosticEntry(
                ValidationSeverity.Error,
                SR.T("Last test failed: {0}", item.LastTestMessage!.Trim())));
        }
        else if (item.LastTestState == ModelProviderLastTestState.Testing)
        {
            entries.Insert(0, new ModelProviderDiagnosticEntry(
                ValidationSeverity.Info,
                string.IsNullOrWhiteSpace(item.LastTestMessage)
                    ? SR.T("Provider test is running.")
                    : item.LastTestMessage!.Trim()));
        }

        var hasErrors = entries.Any(static entry => entry.Severity == ValidationSeverity.Error);
        var hasWarnings = entries.Any(entry => entry.Severity == ValidationSeverity.Warning &&
                                               !IsStatusNeutralDiagnostic(item, entry));

        var statusKind = ResolveStatusKind(item, hasErrors, hasWarnings);
        return new ModelProviderDiagnosticsSnapshot
        {
            StatusKind = statusKind,
            StatusText = ResolveStatusText(item, entries, statusKind),
            Entries = entries,
        };
    }

    public static ValidationMessage? ValidateProviderKey(
        ModelProviderEditorItemViewModel item,
        IReadOnlyList<ModelProviderEditorItemViewModel> providers)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(providers);

        if (string.IsNullOrWhiteSpace(item.ProviderKey))
        {
            return new ValidationMessage(ValidationSeverity.Error, SR.T("Provider key is required."));
        }

        var normalized = item.ProviderKey.Trim().ToLowerInvariant();
        if (normalized.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
        {
            return new ValidationMessage(ValidationSeverity.Error, SR.T("Use lowercase letters, numbers, '-' or '_'."));
        }

        if (providers.Any(other => !ReferenceEquals(other, item) &&
                                   string.Equals(other.ProviderKey, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return new ValidationMessage(ValidationSeverity.Error, SR.T("Provider key is already used."));
        }

        return null;
    }

    public static ValidationMessage? ValidateApiKey(ModelProviderEditorItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!RequiresApiKey(item) || !item.Enabled)
        {
            return null;
        }

        return HasResolvedApiCredential(item)
            ? null
            : new ValidationMessage(ValidationSeverity.Error, SR.T("Enter an API key or configure a non-empty API Key Env."));
    }

    public static ValidationMessage? ValidateApiKeyEnv(ModelProviderEditorItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var envName = GetConfiguredApiKeyEnvName(item);
        if (envName is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envName))
            ? new ValidationMessage(ValidationSeverity.Warning, SR.T("Environment variable {0} is empty in this CodeAlta process.", envName))
            : null;
    }

    public static ValidationMessage? ValidateApiUrl(ModelProviderEditorItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.ProviderType == "azure-openai" && item.Enabled &&
            (item.UseDefaultApiUrl || string.IsNullOrWhiteSpace(item.ApiUrl)))
        {
            return new ValidationMessage(ValidationSeverity.Error, SR.T("Azure OpenAI requires the resource endpoint in API URL."));
        }

        if (item.UseDefaultApiUrl || string.IsNullOrWhiteSpace(item.ApiUrl))
        {
            return null;
        }

        return Uri.TryCreate(item.ApiUrl.Trim(), UriKind.Absolute, out _)
            ? null
            : new ValidationMessage(ValidationSeverity.Error, SR.T("Use an absolute URL."));
    }

    public static ValidationMessage? ValidateVertexProject(ModelProviderEditorItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.ProviderType != "vertex-ai" || !item.Enabled)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(item.Project)
            ? new ValidationMessage(ValidationSeverity.Error, SR.T("Project is required for enabled Vertex AI providers."))
            : null;
    }

    public static ValidationMessage? ValidateAzureOpenAIModel(ModelProviderEditorItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.ProviderType != "azure-openai" || !item.Enabled)
        {
            return null;
        }

        var hasModel = !item.UseDefaultModel && !string.IsNullOrWhiteSpace(item.Model);
        var hasSingleModelId = !item.UseDefaultSingleModelId && !string.IsNullOrWhiteSpace(item.SingleModelId);
        return hasModel || hasSingleModelId
            ? null
            : new ValidationMessage(ValidationSeverity.Error, SR.T("Azure OpenAI requires Model or Single Model Id to be a deployment name."));
    }

    public static ValidationMessage? ValidateVertexLocation(ModelProviderEditorItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.ProviderType != "vertex-ai" || !item.Enabled)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(item.Location)
            ? new ValidationMessage(ValidationSeverity.Error, SR.T("Location is required for enabled Vertex AI providers."))
            : null;
    }

    public static bool RequiresApiKey(ModelProviderEditorItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.ProviderType is "openai-chat" or "openai-responses" or "azure-openai" or "anthropic" or "google-genai";
    }

    private static void Add(List<ModelProviderDiagnosticEntry> entries, ValidationMessage? message)
    {
        if (message is null)
        {
            return;
        }

        var text = message.Value.Content is TextBlock textBlock
            ? textBlock.Text ?? string.Empty
            : message.Value.Content.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        entries.Add(new ModelProviderDiagnosticEntry(message.Value.Severity, text));
    }

    private static bool HasResolvedApiCredential(ModelProviderEditorItemViewModel item)
        => HasLiteralApiKey(item) || HasResolvedApiKeyEnvValue(item);

    private static bool HasLiteralApiKey(ModelProviderEditorItemViewModel item)
        => !item.UseDefaultApiKey && !string.IsNullOrWhiteSpace(item.ApiKey);

    private static bool HasResolvedApiKeyEnvValue(ModelProviderEditorItemViewModel item)
    {
        var envName = GetConfiguredApiKeyEnvName(item);
        return envName is not null && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envName));
    }

    private static string? GetConfiguredApiKeyEnvName(ModelProviderEditorItemViewModel item)
        => item.UseDefaultApiKeyEnv || string.IsNullOrWhiteSpace(item.ApiKeyEnv)
            ? null
            : item.ApiKeyEnv.Trim();

    private static bool ShouldShowCustomApiUrlGuidance(ModelProviderEditorItemViewModel item)
        => item.Enabled &&
           !item.UseDefaultApiUrl &&
           !string.IsNullOrWhiteSpace(item.ApiUrl) &&
           ValidateApiUrl(item) is null;

    private static ModelProviderUiStatusKind ResolveStatusKind(
        ModelProviderEditorItemViewModel item,
        bool hasErrors,
        bool hasWarnings)
    {
        if (item.LastTestState == ModelProviderLastTestState.Failed || hasErrors)
        {
            return ModelProviderUiStatusKind.Error;
        }

        if (item.LastTestState == ModelProviderLastTestState.Testing)
        {
            return ModelProviderUiStatusKind.Warning;
        }

        if (!item.Enabled)
        {
            return ModelProviderUiStatusKind.Disabled;
        }

        if (hasWarnings)
        {
            return ModelProviderUiStatusKind.Warning;
        }

        if (item.LastTestState == ModelProviderLastTestState.Success)
        {
            return ModelProviderUiStatusKind.Success;
        }

        return ModelProviderUiStatusKind.Configured;
    }

    private static string ResolveStatusText(
        ModelProviderEditorItemViewModel item,
        IReadOnlyList<ModelProviderDiagnosticEntry> entries,
        ModelProviderUiStatusKind statusKind)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(entries);

        if (item.LastTestState == ModelProviderLastTestState.Success)
        {
            return SR.T("Tested successfully");
        }

        if (item.LastTestState == ModelProviderLastTestState.Testing)
        {
            return SR.T("Testing...");
        }

        if (item.ProviderType == "codex" &&
            TryResolveCodexSubscriptionStatusText(item, entries, out var codexStatusText))
        {
            return codexStatusText;
        }

        if (item.LastTestState == ModelProviderLastTestState.Failed)
        {
            return SR.T("Test failed");
        }

        if (entries.Any(entry => entry.Severity == ValidationSeverity.Error &&
                                 entry.Message.Contains("API key", StringComparison.OrdinalIgnoreCase)))
        {
            return SR.T("Missing credentials");
        }

        if (entries.Any(entry => entry.Severity == ValidationSeverity.Warning &&
                                 entry.Message.Contains("Environment variable", StringComparison.OrdinalIgnoreCase)))
        {
            return SR.T("Env var missing");
        }

        if (entries.Any(entry => entry.Severity == ValidationSeverity.Error &&
                                 entry.Message.Contains("absolute URL", StringComparison.OrdinalIgnoreCase)))
        {
            return SR.T("Invalid API URL");
        }

        if (entries.Any(entry => entry.Severity == ValidationSeverity.Error &&
                                 entry.Message.Contains("Provider key", StringComparison.OrdinalIgnoreCase)))
        {
            return SR.T("Invalid provider key");
        }

        if (entries.Any(entry => entry.Severity == ValidationSeverity.Error &&
                                 entry.Message.Contains("Vertex AI", StringComparison.OrdinalIgnoreCase)))
        {
            return SR.T("Missing Vertex settings");
        }

        return statusKind switch
        {
            ModelProviderUiStatusKind.Disabled => SR.T("Disabled"),
            ModelProviderUiStatusKind.Success => SR.T("Tested successfully"),
            ModelProviderUiStatusKind.Warning => SR.T("Needs review"),
            ModelProviderUiStatusKind.Error => SR.T("Needs attention"),
            _ => SR.T("Ready to test"),
        };
    }

    private static bool TryResolveCodexSubscriptionStatusText(
        ModelProviderEditorItemViewModel item,
        IReadOnlyList<ModelProviderDiagnosticEntry> entries,
        out string statusText)
    {
        statusText = string.Empty;
        if (!item.Enabled)
        {
            statusText = SR.T("Not configured");
            return true;
        }

        var failure = item.LastTestState == ModelProviderLastTestState.Failed
            ? item.LastTestMessage ?? string.Empty
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(failure))
        {
            if (failure.Contains("expired", StringComparison.OrdinalIgnoreCase) &&
                failure.Contains("refresh", StringComparison.OrdinalIgnoreCase))
            {
                statusText = SR.T("Token expired; refresh available");
                return true;
            }

            if (failure.Contains("login is required", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("re-authentication", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("authentication failed", StringComparison.OrdinalIgnoreCase))
            {
                statusText = SR.T("Login required");
                return true;
            }

            if (failure.Contains("account", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("workspace", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("plan", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("policy", StringComparison.OrdinalIgnoreCase))
            {
                statusText = SR.T("Account/workspace selection required");
                return true;
            }

            if (failure.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("quota", StringComparison.OrdinalIgnoreCase))
            {
                statusText = SR.T("Rate or quota limited");
                return true;
            }

            if (failure.Contains("protocol", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("request shape", StringComparison.OrdinalIgnoreCase))
            {
                statusText = SR.T("Unsupported provider/protocol drift");
                return true;
            }

            statusText = SR.T("Last action needs review");
            return true;
        }

        if (entries.Any(static entry => entry.Severity == ValidationSeverity.Error))
        {
            return false;
        }

        statusText = SR.T("Ready");
        return true;
    }

    private static bool IsCodexSubscription(ModelProviderEditorItemViewModel item)
        => string.Equals(item.ProviderType, "codex", StringComparison.Ordinal);

    private static bool IsStatusNeutralDiagnostic(ModelProviderEditorItemViewModel item, ModelProviderDiagnosticEntry entry)
        => false;
}
