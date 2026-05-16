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
        Add(entries, ValidateVertexProject(item));
        Add(entries, ValidateVertexLocation(item));

        if (!item.IsReserved && item.ProviderType is "codex_cli" or "copilot_cli")
        {
            entries.Add(new ModelProviderDiagnosticEntry(
                ValidationSeverity.Error,
                "Only the reserved codex_cli/copilot_cli entries can use built-in CLI provider types."));
        }

        if (item.ProviderType == "vertex-ai" && item.Enabled)
        {
            entries.Add(new ModelProviderDiagnosticEntry(
                ValidationSeverity.Info,
                "Vertex AI uses Google application-default credentials from the current environment."));
        }

        if (item.ProviderType == "codex" && item.Enabled)
        {
            entries.Add(new ModelProviderDiagnosticEntry(
                item.Experimental ? ValidationSeverity.Warning : ValidationSeverity.Error,
                item.Experimental
                    ? "Experimental ChatGPT/Codex subscription access may count against plan limits; CodeAlta will not rotate accounts or bypass limits."
                    : "Enable the experimental opt-in before saving this ChatGPT/Codex subscription provider."));
        }

        if (item.ProviderType == "copilot" && item.Enabled)
        {
            entries.Add(new ModelProviderDiagnosticEntry(
                item.Experimental ? ValidationSeverity.Warning : ValidationSeverity.Error,
                item.Experimental
                    ? "Experimental Copilot access uses Copilot HTTP endpoints directly and may be affected by account or organization policy."
                    : "Enable the experimental opt-in before saving this Copilot provider."));
        }

        if (ShouldShowCustomApiUrlGuidance(item))
        {
            entries.Add(new ModelProviderDiagnosticEntry(
                ValidationSeverity.Info,
                "Custom API URL configured. Use Test Provider to verify endpoint reachability."));
        }

        if (item.LastTestState == ModelProviderLastTestState.Success &&
            !string.IsNullOrWhiteSpace(item.LastTestMessage))
        {
            entries.Insert(0, new ModelProviderDiagnosticEntry(
                ValidationSeverity.Info,
                $"Last test succeeded: {item.LastTestMessage!.Trim()}"));
        }
        else if (item.LastTestState == ModelProviderLastTestState.Failed &&
                 !string.IsNullOrWhiteSpace(item.LastTestMessage))
        {
            entries.Insert(0, new ModelProviderDiagnosticEntry(
                IsCodexSubscription(item) ? ValidationSeverity.Warning : ValidationSeverity.Error,
                $"Last test failed: {item.LastTestMessage!.Trim()}"));
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
            return new ValidationMessage(ValidationSeverity.Error, "Provider key is required.");
        }

        var normalized = item.ProviderKey.Trim().ToLowerInvariant();
        if (normalized.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
        {
            return new ValidationMessage(ValidationSeverity.Error, "Use lowercase letters, numbers, '-' or '_'.");
        }

        if (providers.Any(other => !ReferenceEquals(other, item) &&
                                   string.Equals(other.ProviderKey, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return new ValidationMessage(ValidationSeverity.Error, "Provider key is already used.");
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
            : new ValidationMessage(ValidationSeverity.Error, "Enter an API key or configure a non-empty API Key Env.");
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
            ? new ValidationMessage(ValidationSeverity.Warning, $"Environment variable {envName} is empty in this CodeAlta process.")
            : null;
    }

    public static ValidationMessage? ValidateApiUrl(ModelProviderEditorItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.UseDefaultApiUrl || string.IsNullOrWhiteSpace(item.ApiUrl))
        {
            return null;
        }

        return Uri.TryCreate(item.ApiUrl.Trim(), UriKind.Absolute, out _)
            ? null
            : new ValidationMessage(ValidationSeverity.Error, "Use an absolute URL.");
    }

    public static ValidationMessage? ValidateVertexProject(ModelProviderEditorItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.ProviderType != "vertex-ai" || !item.Enabled)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(item.Project)
            ? new ValidationMessage(ValidationSeverity.Error, "Project is required for enabled Vertex AI providers.")
            : null;
    }

    public static ValidationMessage? ValidateVertexLocation(ModelProviderEditorItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.ProviderType != "vertex-ai" || !item.Enabled)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(item.Location)
            ? new ValidationMessage(ValidationSeverity.Error, "Location is required for enabled Vertex AI providers.")
            : null;
    }

    public static bool RequiresApiKey(ModelProviderEditorItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.ProviderType is "openai-chat" or "openai-responses" or "anthropic" or "google-genai";
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
        if ((item.LastTestState == ModelProviderLastTestState.Failed && !IsCodexSubscription(item)) || hasErrors)
        {
            return ModelProviderUiStatusKind.Error;
        }

        if (!item.Enabled)
        {
            return ModelProviderUiStatusKind.Disabled;
        }

        if (hasWarnings || (item.LastTestState == ModelProviderLastTestState.Failed && IsCodexSubscription(item)))
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
            return "Tested successfully";
        }

        if (item.ProviderType == "codex" &&
            TryResolveCodexSubscriptionStatusText(item, entries, out var codexStatusText))
        {
            return codexStatusText;
        }

        if (item.LastTestState == ModelProviderLastTestState.Failed)
        {
            return "Test failed";
        }

        if (entries.Any(entry => entry.Severity == ValidationSeverity.Error &&
                                 entry.Message.Contains("API key", StringComparison.OrdinalIgnoreCase)))
        {
            return "Missing credentials";
        }

        if (entries.Any(entry => entry.Severity == ValidationSeverity.Warning &&
                                 entry.Message.Contains("Environment variable", StringComparison.OrdinalIgnoreCase)))
        {
            return "Env var missing";
        }

        if (entries.Any(entry => entry.Severity == ValidationSeverity.Error &&
                                 entry.Message.Contains("absolute URL", StringComparison.OrdinalIgnoreCase)))
        {
            return "Invalid API URL";
        }

        if (entries.Any(entry => entry.Severity == ValidationSeverity.Error &&
                                 entry.Message.Contains("Provider key", StringComparison.OrdinalIgnoreCase)))
        {
            return "Invalid provider key";
        }

        if (entries.Any(entry => entry.Severity == ValidationSeverity.Error &&
                                 entry.Message.Contains("Vertex AI", StringComparison.OrdinalIgnoreCase)))
        {
            return "Missing Vertex settings";
        }

        return statusKind switch
        {
            ModelProviderUiStatusKind.Disabled => "Disabled",
            ModelProviderUiStatusKind.Success => "Tested successfully",
            ModelProviderUiStatusKind.Warning => "Needs review",
            ModelProviderUiStatusKind.Error => "Needs attention",
            _ => "Ready to test",
        };
    }

    private static bool TryResolveCodexSubscriptionStatusText(
        ModelProviderEditorItemViewModel item,
        IReadOnlyList<ModelProviderDiagnosticEntry> entries,
        out string statusText)
    {
        statusText = string.Empty;
        if (!item.Enabled || !item.Experimental)
        {
            statusText = "Not configured";
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
                statusText = "Token expired; refresh available";
                return true;
            }

            if (failure.Contains("login is required", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("re-authentication", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("authentication failed", StringComparison.OrdinalIgnoreCase))
            {
                statusText = "Login required";
                return true;
            }

            if (failure.Contains("account", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("workspace", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("plan", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("policy", StringComparison.OrdinalIgnoreCase))
            {
                statusText = "Account/workspace selection required";
                return true;
            }

            if (failure.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("quota", StringComparison.OrdinalIgnoreCase))
            {
                statusText = "Rate or quota limited";
                return true;
            }

            if (failure.Contains("protocol", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("request shape", StringComparison.OrdinalIgnoreCase))
            {
                statusText = "Unsupported backend/protocol drift";
                return true;
            }

            statusText = "Last action needs review";
            return true;
        }

        if (entries.Any(static entry => entry.Severity == ValidationSeverity.Error))
        {
            return false;
        }

        statusText = "Ready";
        return true;
    }

    private static bool IsCodexSubscription(ModelProviderEditorItemViewModel item)
        => string.Equals(item.ProviderType, "codex", StringComparison.Ordinal);

    private static bool IsStatusNeutralDiagnostic(ModelProviderEditorItemViewModel item, ModelProviderDiagnosticEntry entry)
        => IsCodexSubscription(item) &&
           entry.Message.StartsWith("Experimental ChatGPT/Codex subscription access", StringComparison.Ordinal);
}
