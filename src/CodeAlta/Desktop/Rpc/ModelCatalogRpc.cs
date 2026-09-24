using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// Explicit, bounded, read-only projections of the host's provider initialization service.
// Never return a provider error message, credential, arbitrary capability dictionary or default as a model.
[NeoRpcService("modelCatalog", Version = 1)]
internal sealed class ModelCatalogService(
    ModelProviderRegistry? registry = null,
    ModelProviderInitializationService? initialization = null,
    string? epoch = null)
{
    internal const int MaximumModelsResponseBytes = 96 * 1024;

    [NeoRpcMethod("providers")]
    public ModelCatalogProvidersResponse Providers(ModelCatalogProvidersRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var denied = CheckEpoch(request.ExpectedEpoch);
        if (denied is not null) return new(denied, epoch, [], false);
        var descriptors = registry!.ListProviders(includeDisabled: true);
        var visible = descriptors.Where(descriptor => ValidId(descriptor.ProviderId.Value)).Take(33).ToArray();
        var states = initialization!.CurrentStates.ToDictionary(state => state.ProviderId.Value, StringComparer.OrdinalIgnoreCase);
        return new("ok", epoch, visible.Take(32).Select(descriptor => new ModelCatalogProvider(
            descriptor.ProviderId.Value, Bound(descriptor.DisplayName, 256), descriptor.IsEnabled,
            states.TryGetValue(descriptor.ProviderId.Value, out var state) ? state.Availability.ToString() : "Unknown")).ToArray(),
            descriptors.Count > 32 || descriptors.Count != visible.Length);
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
                return new("unavailable", epoch, descriptor.ProviderId.Value, availability, [], false);
            var valid = state.Models.Where(model => ValidId(model.Id)).Take(129).ToArray();
            var models = new List<ModelCatalogModel>(Math.Min(valid.Length, 128));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new ModelCatalogModelsResponse("ok", epoch, descriptor.ProviderId.Value, availability, [], false),
                DesktopJsonContext.Default.ModelCatalogModelsResponse).Length;
            foreach (var model in valid.Take(128))
            {
                var projected = ProjectModel(model);
                var nextBytes = JsonSerializer.SerializeToUtf8Bytes(projected, DesktopJsonContext.Default.ModelCatalogModel).Length;
                if (bytes + nextBytes + (models.Count > 0 ? 1 : 0) > MaximumModelsResponseBytes) break;
                bytes += nextBytes + (models.Count > 0 ? 1 : 0);
                models.Add(projected);
            }
            return new("ok", epoch, descriptor.ProviderId.Value, availability,
                models, valid.Length > models.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new("read_failed", epoch, descriptor.ProviderId.Value, "Unknown", [], false); }
    }

    private string? CheckEpoch(string expected) => epoch is null || registry is null || initialization is null ? "unconfigured"
        : !ValidId(expected) ? "invalid_request" : !string.Equals(expected, epoch, StringComparison.Ordinal) ? "stale_epoch" : null;
    private static bool ValidId(string? value) => value is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(value)
        && value.Trim() == value && !value.Any(char.IsControl);
    private static string Bound(string? value, int length) => value is { Length: > 0 } ? value[..Math.Min(value.Length, length)] : "Unknown";

    private static ModelCatalogModel ProjectModel(AgentModelInfo model)
    {
        var metadata = model.Capabilities;
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
            Flag(metadata, "supportsImageInput", "imageInput", "supportsImages", "supportsVision", "vision")
                ?? ImageModalities(metadata));
    }

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

    private static bool? ImageModalities(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (!TryRead(metadata, "inputModalities", out var raw) &&
            !TryRead(metadata, "input_modalities", out raw)) return null;
        return raw switch
        {
            IEnumerable<string> values => values.Any(value => value.Equals("image", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("vision", StringComparison.OrdinalIgnoreCase)),
            JsonElement { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().Any(element =>
                element.ValueKind == JsonValueKind.String && (element.GetString()?.Equals("image", StringComparison.OrdinalIgnoreCase) == true ||
                element.GetString()?.Equals("vision", StringComparison.OrdinalIgnoreCase) == true)),
            _ => null,
        };
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
internal sealed record ModelCatalogProvider(string Id, string Name, bool Enabled, string Availability);
internal sealed record ModelCatalogModelsRequest(string ExpectedEpoch, string ProviderId);
internal sealed record ModelCatalogModelsResponse(string Status, string? Epoch, string? ProviderId, string Availability, IReadOnlyList<ModelCatalogModel> Models, bool Truncated);
internal sealed record ModelCatalogModel(string Id, string Name, string? Description, IReadOnlyList<string> Efforts, string? DefaultEffort,
    int? ContextTokens, int? InputTokens, int? OutputTokens, bool? Reasoning, bool? Tools, bool? StructuredOutput, bool? ImageInput);
