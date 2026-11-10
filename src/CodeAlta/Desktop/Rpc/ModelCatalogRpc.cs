using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Agent;
using CodeAlta.Agent.ModelCatalog;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Rpc;

// Explicit, bounded, read-only projections of the host's provider initialization service.
// Never return a provider error message, credential, arbitrary capability dictionary or default as a model.
[NeoRpcService("modelCatalog", Version = 1)]
internal sealed class ModelCatalogService(
    ModelProviderRegistry? registry = null,
    ModelProviderInitializationService? initialization = null,
    string? epoch = null,
    DesktopDefaultProvider? defaultProvider = null,
    ModelsDevCatalogService? modelsDev = null)
{
    // Every model a real provider reports (Azure OpenAI lists several hundred), far below the bridge's frame limit.
    internal const int MaximumModels = 2048;
    private const int MaximumHiddenModels = 1024;
    // ,"hidden":[] in the answer.
    private const int HiddenPropertyBytes = 12;
    internal const int MaximumModelsResponseBytes = 2 * 1024 * 1024;
    private readonly object _probeGate = new();
    private readonly HashSet<Task> _probes = [];
    private bool _closed;
    private Task _initialization = Task.CompletedTask;
    private bool _initializationStarted;

    // Startup owns this work, just as the TUI does. Inventory reads never launch probes.
    internal Task StartInitialization() => StartInitialization(Task.CompletedTask);

    /// <summary>The most providers prepared at the same time: each can start a CLI or an agent, or download its models.</summary>
    internal const int InitializationConcurrency = 3;

    // The providers are prepared once the window shows the application (`after`), a few at a time: the page asks for the
    // models of a provider it needs meanwhile, which prepares that one at once.
    internal Task StartInitialization(Task after)
    {
        ArgumentNullException.ThrowIfNull(after);
        lock (_probeGate)
        {
            if (!_closed && !_initializationStarted)
            {
                _initializationStarted = true;
                _initialization = InitializeAsync(after);
            }
            return _initialization;
        }
    }

    private async Task InitializeAsync(Task after)
    {
        try
        {
            // A window that closes before it shows the application prepares nothing.
            await Task.WhenAny(after, _closing.Task).ConfigureAwait(false);
            lock (_probeGate) if (_closed) return;
            await initialization!.InitializeAllAsync(InitializationConcurrency, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // Individual provider failures are exposed as readiness states by the service.
            XenoAtom.Logging.LogManager.GetLogger("CodeAlta.Desktop").Error(failure, "Provider initialization failed");
        }
    }

    [NeoRpcMethod("providers")]
    public ModelCatalogProvidersResponse Providers(ModelCatalogProvidersRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var denied = CheckEpoch(request.ExpectedEpoch);
        if (denied is not null) return new(denied, epoch, [], false);
        // One provider is the default: the one a new session starts with.
        var startsWith = defaultProvider?.Of(registry!.ListProviders())?.ProviderId.Value;
        lock (_probeGate)
        {
            if (_closed) return new("closed", epoch, [], false);
            var descriptors = registry!.ListProviders(includeDisabled: true);
            var visible = descriptors.Where(descriptor => ValidId(descriptor.ProviderId.Value)).Take(33).ToArray();
            var states = initialization!.CurrentStates.ToDictionary(state => state.ProviderId.Value, StringComparer.OrdinalIgnoreCase);
            return new("ok", epoch, visible.Take(32).Select(descriptor => new ModelCatalogProvider(
                descriptor.ProviderId.Value, Bound(descriptor.DisplayName, 256), descriptor.IsEnabled,
                states.TryGetValue(descriptor.ProviderId.Value, out var state) ? state.Availability.ToString() : "Unknown")
            {
                Type = Bound(descriptor.ProviderType, 256),
                IsDefault = defaultProvider is null ? descriptor.IsDefault : string.Equals(descriptor.ProviderId.Value, startsWith, StringComparison.Ordinal),
                DefaultModel = descriptor.DefaultModelId is null ? null : Bound(descriptor.DefaultModelId, 256),
                ObservedAt = state is { Availability: not ModelProviderAvailability.Unknown } ? state.ObservedAt : null,
                Reason = state is null ? null : Reason(state),
                Capabilities = state is { Capabilities.Count: > 0 }
                    ? [.. state.Capabilities.Take(16).Select(static capability => new ModelCatalogCapability(Bound(capability.Id, 64), Bound(capability.Label, 64), capability.Supported) { Detail = capability.Detail is null ? null : Bound(capability.Detail, 512) })]
                    : null,
            }).ToArray(),
                descriptors.Count > 32 || descriptors.Count != visible.Length);
        }
    }

    [NeoRpcMethod("probe")]
    public async Task<ModelCatalogProbeResponse> Probe(ModelCatalogProbeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var denied = CheckEpoch(request.ExpectedEpoch);
        if (denied is not null) return new(denied, epoch, null, "Unknown");
        if (!ValidId(request.ProviderId)) return new("invalid_request", epoch, null, "Unknown");
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<ModelCatalogProbeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        ModelProviderDescriptor? descriptor;
        lock (_probeGate)
        {
            if (_closed) return new("closed", epoch, request.ProviderId, "Unknown");
            descriptor = registry!.ListProviders(includeDisabled: true).Where(value => ValidId(value.ProviderId.Value))
                .Take(32).FirstOrDefault(value => string.Equals(value.ProviderId.Value, request.ProviderId, StringComparison.Ordinal));
            if (descriptor is null) return new("not_found", epoch, null, "Unknown");
            if (_probes.Count != 0) return new("busy", epoch, descriptor.ProviderId.Value, "Unknown");
            _probes.Add(completion.Task);
        }
        // A caller abandoning its wait never cancels an admitted provider operation. Host shutdown joins it.
        _ = ProbeOwnedAsync(descriptor, completion);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ProbeOwnedAsync(ModelProviderDescriptor descriptor, TaskCompletionSource<ModelCatalogProbeResponse> completion)
    {
        ModelCatalogProbeResponse result;
        try
        {
            await initialization!.RefreshProviderAsync(descriptor.ProviderId, CancellationToken.None).ConfigureAwait(false);
            var state = initialization.CurrentStates.FirstOrDefault(value => value.ProviderId == descriptor.ProviderId);
            result = state is null ? new("probe_failed", epoch, descriptor.ProviderId.Value, "Unknown")
                : new("ok", epoch, descriptor.ProviderId.Value, state.Availability.ToString()) { Reason = Reason(state) };
        }
        catch (Exception) { result = new("probe_failed", epoch, descriptor.ProviderId.Value, "Unknown"); }
        completion.TrySetResult(result);
        lock (_probeGate) _probes.Remove(completion.Task);
    }

    private readonly TaskCompletionSource _closing = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void CloseAdmission() { lock (_probeGate) _closed = true; _closing.TrySetResult(); }
    internal async Task DrainAsync()
    {
        Task[] work;
        lock (_probeGate) { _closed = true; work = [.. _probes, _initialization]; }
        _closing.TrySetResult();
        await Task.WhenAll(work).ConfigureAwait(false);
    }

    [NeoRpcMethod("models")]
    public async Task<ModelCatalogModelsResponse> Models(ModelCatalogModelsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var denied = CheckEpoch(request.ExpectedEpoch);
        if (denied is not null) return new(denied, epoch, null, "Unknown", [], false);
        if (!ValidId(request.ProviderId)) return new("invalid_request", epoch, null, "Unknown", [], false);
        var descriptor = registry!.ListProviders(includeDisabled: true).FirstOrDefault(value =>
            string.Equals(value.ProviderId.Value, request.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null) return new("not_found", epoch, null, "Unknown", [], false);
        try
        {
            // The user explicitly opened this provider; this service never probes all providers on navigation.
            await initialization!.GetModelsAsync(descriptor.ProviderId, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var state = initialization.CurrentStates.FirstOrDefault(value => value.ProviderId == descriptor.ProviderId);
            if (state is null) return new("unavailable", epoch, descriptor.ProviderId.Value, "Unknown", [], false);
            var availability = state.Availability.ToString();
            if (state.Availability != ModelProviderAvailability.Ready)
                return new("unavailable", epoch, descriptor.ProviderId.Value, availability, [], false) { Reason = Reason(state) };
            var valid = state.Models.Where(model => ValidId(model.Id)).Take(MaximumModels + 1).ToArray();
            var models = new List<ModelCatalogModel>(Math.Min(valid.Length, MaximumModels));
            var order = ModelOrdering.Format(descriptor.ModelOrder);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new ModelCatalogModelsResponse("ok", epoch, descriptor.ProviderId.Value, availability, [], false) { ModelsOrder = order },
                DesktopJsonContext.Default.ModelCatalogModelsResponse).Length;
            foreach (var model in valid.Take(MaximumModels))
            {
                var projected = ProjectModel(model, descriptor.DefaultContextSize);
                var nextBytes = JsonSerializer.SerializeToUtf8Bytes(projected, DesktopJsonContext.Default.ModelCatalogModel).Length;
                if (bytes + nextBytes + (models.Count > 0 ? 1 : 0) > MaximumModelsResponseBytes) break;
                bytes += nextBytes + (models.Count > 0 ? 1 : 0);
                models.Add(projected);
            }

            // The models its rules hide, by id and name, for the page of the provider to show what they hide: after the models it
            // offers, in what is left of the same budget (the name of the property and its brackets included).
            var hiddenModels = state.HiddenModels.Where(model => ValidId(model.Id)).ToArray();
            var hidden = new List<ModelCatalogHiddenModel>(Math.Min(hiddenModels.Length, MaximumHiddenModels));
            bytes += HiddenPropertyBytes;
            foreach (var model in hiddenModels.Take(MaximumHiddenModels))
            {
                var entry = new ModelCatalogHiddenModel(Bound(model.Id, 256), Bound(model.DisplayName ?? model.Id, 128));
                var nextBytes = JsonSerializer.SerializeToUtf8Bytes(entry, DesktopJsonContext.Default.ModelCatalogHiddenModel).Length;
                if (bytes + nextBytes + (hidden.Count > 0 ? 1 : 0) > MaximumModelsResponseBytes) break;
                bytes += nextBytes + (hidden.Count > 0 ? 1 : 0);
                hidden.Add(entry);
            }

            return new("ok", epoch, descriptor.ProviderId.Value, availability,
                models, valid.Length > models.Count || hiddenModels.Length > hidden.Count) { Hidden = hidden.Count > 0 ? hidden : null, ModelsOrder = order };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new("read_failed", epoch, descriptor.ProviderId.Value, "Unknown", [], false); }
    }

    // The most rules of each kind a preview reads, the longest rule, the models it names, and how long it matches.
    private const int MaximumPreviewRules = 256;
    private const int MaximumPreviewRuleLength = 1024;
    private const int MaximumPreviewHidden = 200;
    private static readonly TimeSpan PreviewTimeLimit = TimeSpan.FromSeconds(1);

    /// <summary>
    /// What rules of the page of a provider would hide among the models it reported, saved or not: matched here, as the provider
    /// will match them (.NET regular expressions, each match bounded in time), never in the page. Reads only what the provider
    /// already reported: it is never prepared for this.
    /// </summary>
    [NeoRpcMethod("visibility")]
    public ModelCatalogVisibilityResponse Visibility(ModelCatalogVisibilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var denied = CheckEpoch(request.ExpectedEpoch);
        if (denied is not null) return new(denied, epoch, 0, 0, [], false);
        if (!ValidId(request.ProviderId) || request.Hide is { Count: > MaximumPreviewRules } || request.Show is { Count: > MaximumPreviewRules }
            || (request.Hide ?? []).Concat(request.Show ?? []).Any(static rule => rule is null || rule.Length > MaximumPreviewRuleLength))
            return new("invalid_request", epoch, 0, 0, [], false);
        var state = initialization!.CurrentStates.FirstOrDefault(value => string.Equals(value.ProviderId.Value, request.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (state is null || state.Availability != ModelProviderAvailability.Ready) return new("unavailable", epoch, 0, 0, [], false);
        ModelVisibility visibility;
        try
        {
            visibility = ModelVisibility.Parse(request.Hide, request.Show);
        }
        catch (ArgumentException exception)
        {
            // The message of the rule, without the name of the parameter .NET appends to it.
            var message = exception.ParamName is { } name ? exception.Message.Replace($" (Parameter '{name}')", "", StringComparison.Ordinal) : exception.Message;
            return new("invalid", epoch, 0, 0, [], false) { Problem = Bound(message, 512) };
        }

        var models = state.Models.Concat(state.HiddenModels).Where(model => ValidId(model.Id)).ToArray();
        var hidden = new List<ModelCatalogHiddenModel>();
        var count = 0;
        var complete = true;
        var started = System.Diagnostics.Stopwatch.StartNew();
        // A regular expression that backtracks without end stops the preview, not the window: each match is bounded, the time is
        // checked between two, and the count says when it is partial.
        bool Matches(IReadOnlyList<ModelRule> rules, string id)
        {
            foreach (var rule in rules)
            {
                if (started.Elapsed > PreviewTimeLimit) { complete = false; return false; }
                if (rule.Matches(id)) return true;
            }

            return false;
        }

        foreach (var model in models)
        {
            var hides = Matches(visibility.Hide, model.Id) && !Matches(visibility.Show, model.Id);
            if (!complete) break;
            if (!hides) continue;
            count++;
            if (hidden.Count < MaximumPreviewHidden) hidden.Add(new(Bound(model.Id, 256), Bound(model.DisplayName ?? model.Id, 128)));
        }

        return new("ok", epoch, models.Length, count, hidden, complete);
    }

    // Why a provider is not ready, as one of the codes the page has a text for: the message of the provider stays here.
    internal static string? Reason(ModelProviderStateSnapshot state)
        => state.Availability is ModelProviderAvailability.Failed or ModelProviderAvailability.Unsupported
            ? state.ErrorCategory switch
            {
                "claude-code-signed-out" => "claude-code-signed-out",
                "claude-code-not-found" => "claude-code-not-found",
                "claude-code-unavailable" => "claude-code-unavailable",
                "acp-signed-out" => "acp-signed-out",
                "acp-not-installed" => "acp-not-installed",
                "acp-unavailable" => "acp-unavailable",
                "codex-cli-signed-out" => "codex-cli-signed-out",
                "codex-cli-not-found" => "codex-cli-not-found",
                "codex-cli-unavailable" => "codex-cli-unavailable",
                _ => null,
            }
            : null;

    private string? CheckEpoch(string expected) => epoch is null || registry is null || initialization is null ? "unconfigured"
        : !ValidId(expected) ? "invalid_request" : !string.Equals(expected, epoch, StringComparison.Ordinal) ? "stale_epoch" : null;
    private static bool ValidId(string? value) => value is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(value)
        && value.Trim() == value && !value.Any(char.IsControl);
    private static string Bound(string? value, int length) => value is { Length: > 0 } ? value[..Math.Min(value.Length, length)] : "Unknown";

    private ModelCatalogModel ProjectModel(AgentModelInfo model, long? defaultContextSize)
    {
        // A provider that describes its models by none of models.dev (the agent of a plugin) has them described by
        // their vendor, for this page only.
        if (modelsDev is not null && !TryRead(model.Capabilities, "modelsDevModelId", out _))
        {
            model = AgentModelMetadataEnricher.EnrichModel(model, modelsDev, modelsDevProviderId: null, overrides: null);
        }

        var metadata = model.Capabilities;
        var inputCost = Price(metadata, AgentModelMetadataEnricher.InputCostKey);
        var outputCost = Price(metadata, AgentModelMetadataEnricher.OutputCostKey);
        return new(Bound(model.Id, 256), Bound(model.DisplayName ?? model.Id, 256),
            model.Description is null ? null : Bound(model.Description, 1024),
            model.SupportedReasoningEfforts?.Take(8).Select(static value => value.ToString()).ToArray() ?? [],
            model.DefaultReasoningEffort?.ToString(),
            Limit(metadata, "contextWindow", "contextWindowTokens", "context_length", "contextLength", "tokenLimit"),
            Limit(metadata, "inputTokenLimit", "maxInputTokens"),
            Limit(metadata, "outputTokenLimit", "maxOutputTokens", "maxTokens"),
            model.SupportedReasoningEfforts is { Count: > 0 } || model.DefaultReasoningEffort is not null
                ? true : Flag(metadata, "supportsReasoning", "reasoning"),
            Flag(metadata, "supportsToolCall", "toolCall", "tool_call"),
            Flag(metadata, "supportsStructuredOutput", "structuredOutput", "structured_output"),
            AgentImageInputCapability.Read(model))
        {
            EffortsComplete = AgentReasoningEffortCapability.IsListComplete(model) ? true : null,
            ContextSizes = ContextSizeContract.Of(model.ContextSizes),
            ContextSize = model.ContextSizes is { Count: > 0 } ? ContextSizeContract.Of(model.ChooseContextSize(defaultContextSize) ?? model.GetContextSize()) : null,
            FastMode = model.FastMode,
            Deprecated = IsDeprecated(metadata) ? true : null,
            RetiresAt = IsDeprecated(metadata) ? Date(metadata, "retiresAt") : null,
            Price = inputCost is null && outputCost is null ? null : new ModelCatalogPrice(inputCost, outputCost,
                Price(metadata, AgentModelMetadataEnricher.CacheReadCostKey), Price(metadata, AgentModelMetadataEnricher.CacheWriteCostKey),
                ProviderName(Text(metadata, AgentModelMetadataEnricher.CostProviderKey, 64))),
            Knowledge = Text(metadata, "knowledge", 32),
            ReleaseDate = Text(metadata, "releaseDate", 32),
            InputModalities = Texts(metadata, "inputModalities"),
            OpenWeights = Flag(metadata, "openWeights"),
            Source = ProviderName(Text(metadata, "modelsDevProviderId", 64)),
        };
    }

    // The name models.dev gives a provider of its catalog (`anthropic` is "Anthropic").
    private string? ProviderName(string? providerId)
        => providerId is null ? null
            : modelsDev?.TryGetProvider(providerId, out var provider) == true && !string.IsNullOrWhiteSpace(provider.Name) ? Bound(provider.Name, 64) : providerId;

    private static decimal? Price(IReadOnlyDictionary<string, object?>? metadata, string key)
    {
        if (!TryRead(metadata, key, out var raw)) return null;
        decimal? value = raw switch
        {
            decimal number => number,
            double number when double.IsFinite(number) => (decimal)number,
            long number => number,
            int number => number,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetDecimal(out var number) => number,
            _ => null,
        };
        return value is >= 0 and <= 1_000_000 ? value : null;
    }

    private static string? Text(IReadOnlyDictionary<string, object?>? metadata, string key, int length)
        => TryRead(metadata, key, out var raw) && (raw is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : raw as string) is { Length: > 0 } text
            && !text.Any(char.IsControl) ? Bound(text, length) : null;

    private static IReadOnlyList<string>? Texts(IReadOnlyDictionary<string, object?>? metadata, string key)
    {
        if (!TryRead(metadata, key, out var raw)) return null;
        IEnumerable<string?> values = raw switch
        {
            IEnumerable<string> list => list,
            JsonElement { ValueKind: JsonValueKind.Array } element => element.EnumerateArray().Select(static item => item.ValueKind == JsonValueKind.String ? item.GetString() : null),
            _ => [],
        };
        var texts = values.Where(static value => value is { Length: > 0 and <= 32 } && !value.Any(char.IsControl)).Take(8).Select(static value => value!).ToArray();
        return texts.Length == 0 ? null : texts;
    }

    private static bool IsDeprecated(IReadOnlyDictionary<string, object?>? metadata)
        => TryRead(metadata, "status", out var raw) &&
            string.Equals(raw is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : raw as string, "deprecated", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? Date(IReadOnlyDictionary<string, object?>? metadata, string key)
        => !TryRead(metadata, key, out var raw) ? null
            : raw switch
            {
                DateTimeOffset date => date,
                DateTime date => new DateTimeOffset(date),
                JsonElement { ValueKind: JsonValueKind.String } element when element.TryGetDateTimeOffset(out var date) => date,
                string text when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) => date,
                _ => null,
            };

    private static int? Limit(IReadOnlyDictionary<string, object?>? metadata, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!TryRead(metadata, key, out var raw)) continue;
            var text = raw is JsonElement element ? element.ToString() : Convert.ToString(raw, CultureInfo.InvariantCulture);
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is > 0 and <= 1_000_000_000
                ? (int)number : null;
        }
        return null;
    }

    private static bool? Flag(IReadOnlyDictionary<string, object?>? metadata, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!TryRead(metadata, key, out var raw)) continue;
            return raw switch { bool value => value, JsonElement element when element.ValueKind == JsonValueKind.True => true,
                JsonElement element when element.ValueKind == JsonValueKind.False => false, _ => null };
        }
        return null;
    }

    private static bool TryRead(IReadOnlyDictionary<string, object?>? metadata, string key, out object? value)
    {
        if (metadata is not null)
        {
            if (metadata.TryGetValue(key, out value)) return true;
            foreach (var entry in metadata)
                if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
                { value = entry.Value; return true; }
        }
        value = null;
        return false;
    }
}

internal sealed record ModelCatalogProvidersRequest(string ExpectedEpoch);
internal sealed record ModelCatalogProvidersResponse(string Status, string? Epoch, IReadOnlyList<ModelCatalogProvider> Providers, bool Truncated);
internal sealed record ModelCatalogProvider(string Id, string Name, bool Enabled, string Availability)
{
    public string Type { get; init; } = "Unknown";
    public bool IsDefault { get; init; }
    public string? DefaultModel { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    // Why the provider is not ready, as one of the codes of the probe (claude-code-signed-out...); absent otherwise.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    // What the provider can and cannot do, when it depends on what runs behind it (an ACP agent); absent otherwise.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ModelCatalogCapability>? Capabilities { get; init; }
}

/// <summary>One thing a provider can or cannot do, shown with it as a tag.</summary>
internal sealed record ModelCatalogCapability(string Id, string Label, bool Supported)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }
}
internal sealed record ModelCatalogProbeRequest(string ExpectedEpoch, string ProviderId);
internal sealed record ModelCatalogProbeResponse(string Status, string? Epoch, string? ProviderId, string Availability)
{
    public string? Reason { get; init; }
}
internal sealed record ModelCatalogModelsRequest(string ExpectedEpoch, string ProviderId);
internal sealed record ModelCatalogModelsResponse(string Status, string? Epoch, string? ProviderId, string Availability, IReadOnlyList<ModelCatalogModel> Models, bool Truncated)
{
    // The order the provider's settings put the models in (provider, name, name_version): a list in the order of the
    // provider keeps it as it is. Absent from a response that is not "ok".
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelsOrder { get; init; }

    // The models the rules of the provider hide (models_hide, models_show); absent when they hide none.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ModelCatalogHiddenModel>? Hidden { get; init; }

    // Why the provider lists no model, as one of the codes of the probe; absent otherwise.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}
internal sealed record ModelCatalogVisibilityRequest(string ExpectedEpoch, string ProviderId, IReadOnlyList<string>? Hide, IReadOnlyList<string>? Show);
/// <param name="Total">The models the provider reported.</param>
/// <param name="HiddenCount">How many of them the rules hide.</param>
/// <param name="Hidden">The first of those, by id and name.</param>
/// <param name="Complete">False when the matching stopped before it read every model.</param>
internal sealed record ModelCatalogVisibilityResponse(string Status, string? Epoch, int Total, int HiddenCount, IReadOnlyList<ModelCatalogHiddenModel> Hidden, bool Complete)
{
    // Why the rules do not read (`invalid`), as .NET says it; absent otherwise.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Problem { get; init; }
}
/// <summary>A model the rules of its provider hide: its id and its name.</summary>
internal sealed record ModelCatalogHiddenModel(string Id, string Name);
internal sealed record ModelCatalogModel(string Id, string Name, string? Description, IReadOnlyList<string> Efforts, string? DefaultEffort,
    int? ContextTokens, int? InputTokens, int? OutputTokens, bool? Reasoning, bool? Tools, bool? StructuredOutput, bool? ImageInput)
{
    // True when Efforts is all the model has and its provider sends no other one (Claude Code); absent otherwise.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EffortsComplete { get; init; }

    // The sizes of context, in input tokens, a session can choose for the model; absent for a model that has one.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? ContextSizes { get; init; }

    // The size of a session that chose none: the one of its provider when the model has it, else its own.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContextSize { get; init; }

    // Whether a session that chose nothing runs the model in fast mode; absent for a model without one.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? FastMode { get; init; }

    // True when the provider or models.dev says the model is deprecated: it still runs until it retires; absent otherwise.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Deprecated { get; init; }

    // When a deprecated model retires, when its provider says it; absent otherwise.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? RetiresAt { get; init; }

    // What a million tokens cost, from models.dev; absent when it gives no price.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelCatalogPrice? Price { get; init; }

    // The end of the knowledge of the model ("2025-03"), from models.dev; absent otherwise.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Knowledge { get; init; }

    // When the model came out ("2025-05-22"), from models.dev; absent otherwise.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReleaseDate { get; init; }

    // What the model takes in (text, image, pdf, audio, video), from models.dev; absent otherwise.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? InputModalities { get; init; }

    // Whether the weights of the model are published, from models.dev; absent otherwise.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? OpenWeights { get; init; }

    // The provider of models.dev the model is described by (its own, or its vendor's); absent when none.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }
}

/// <summary>What a million tokens of a model cost in US dollars, and the provider of models.dev that gives the price.</summary>
internal sealed record ModelCatalogPrice(decimal? Input, decimal? Output, decimal? CacheRead, decimal? CacheWrite, string? Source);
