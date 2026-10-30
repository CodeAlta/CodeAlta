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
    /// <summary>The file of its home in which Codex keeps the models of the account, as it last fetched them.</summary>
    public const string ModelsCacheFileName = "models_cache.json";

    /// <summary>
    /// Presents the models <c>model/list</c> gives: the hidden ones are left out, the default one first. A model of
    /// <paramref name="windows"/> takes the sizes of context Codex gives it.
    /// </summary>
    public static IReadOnlyList<AgentModelInfo> Present(IReadOnlyList<Model> models, IPluginModelCatalog? catalog,
        IReadOnlyDictionary<string, CodexCliContextWindow>? windows = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        return [.. models.Where(static model => !model.Hidden).OrderByDescending(static model => model.IsDefault)
            .Select(model => Present(model, catalog, windows is not null && windows.TryGetValue(model.Id, out var window) ? window : null))];
    }

    /// <summary>
    /// Reads the sizes of context of the models from the <c>models_cache.json</c> of a home: <c>model/list</c> does not
    /// give them. Only a cache that the running version of Codex wrote is read, as its format is Codex's own; empty
    /// when there is none or it cannot be read.
    /// </summary>
    /// <param name="home">The <c>CODEX_HOME</c> of the provider.</param>
    /// <param name="version">The version of the running app-server, or null when it is not known.</param>
    public static IReadOnlyDictionary<string, CodexCliContextWindow> ReadContextWindows(string home, string? version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        var windows = new Dictionary<string, CodexCliContextWindow>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(version))
        {
            return windows;
        }

        try
        {
            using var stream = File.OpenRead(Path.Combine(home, ModelsCacheFileName));
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("client_version", out var written) || written.ValueKind != JsonValueKind.String ||
                !string.Equals(written.GetString(), version, StringComparison.Ordinal) ||
                !root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                return windows;
            }

            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind == JsonValueKind.Object &&
                    model.TryGetProperty("slug", out var slug) && slug.ValueKind == JsonValueKind.String && slug.GetString() is { Length: > 0 } id &&
                    Tokens(model, "context_window") is { } window)
                {
                    windows[id] = new CodexCliContextWindow(window, Tokens(model, "max_context_window"));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            windows.Clear();
        }

        return windows;

        static long? Tokens(JsonElement model, string name)
            => model.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var tokens) && tokens > 0 ? tokens : null;
    }

    private static AgentModelInfo Present(Model model, IPluginModelCatalog? catalog, CodexCliContextWindow? window)
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

        // The context_window of Codex is the input it sends at most, under the larger one models.dev gives the model.
        if (window is not null)
        {
            capabilities["contextWindow"] = window.Window;
            capabilities["inputTokenLimit"] = window.Window;
            capabilities["maxInputTokens"] = window.Window;
        }

        var info = new AgentModelInfo(
            model.Id,
            string.IsNullOrWhiteSpace(model.DisplayName) ? model.Id : model.DisplayName,
            string.IsNullOrWhiteSpace(model.Description) ? null : model.Description,
            Provider: "openai",
            DefaultReasoningEffort: ToEffort(model.DefaultReasoningEffort.Value),
            SupportedReasoningEfforts: efforts.Length == 0 ? null : efforts,
            Capabilities: capabilities)
        {
            // A thread's model_context_window raises the context of a model up to its max_context_window.
            ContextSizes = window is { Max: { } max } && max > window.Window ? [window.Window, max] : null,
        };
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

/// <summary>The sizes of context Codex gives a model, in input tokens.</summary>
/// <param name="Window">The <c>context_window</c>: the size Codex runs the model with by default.</param>
/// <param name="Max">The <c>max_context_window</c>: the largest a thread can ask for, or null when it is not given.</param>
internal sealed record CodexCliContextWindow(long Window, long? Max);

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
