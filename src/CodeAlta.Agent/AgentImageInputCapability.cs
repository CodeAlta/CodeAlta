using System.Text.Json;

namespace CodeAlta.Agent;

/// <summary>Reads advertised model image-input metadata without activating or probing a provider.</summary>
public static class AgentImageInputCapability
{
    /// <summary>Returns true for advertised support, false for an explicit negative, or null for missing/unrecognized metadata.</summary>
    /// <param name="model">The current observed model, or null when unavailable.</param>
    /// <returns>The advertised capability; this is not a model-name heuristic or a runtime availability guarantee.</returns>
    public static bool? Read(AgentModelInfo? model)
    {
        var metadata = model?.Capabilities;
        foreach (var key in new[] { "supportsImageInput", "imageInput", "supportsImages", "supportsVision", "vision" })
        {
            if (!TryRead(key, out var value)) continue;
            return value switch { bool flag => flag, JsonElement { ValueKind: JsonValueKind.True } => true,
                JsonElement { ValueKind: JsonValueKind.False } => false, _ => null };
        }
        if (!TryRead("inputModalities", out var modalities) && !TryRead("input_modalities", out modalities)) return null;
        return modalities switch
        {
            IEnumerable<string> values => values.Any(IsImage),
            JsonElement { ValueKind: JsonValueKind.Array } array when array.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String)
                => array.EnumerateArray().Any(e => IsImage(e.GetString()!)),
            _ => null,
        };

        bool TryRead(string key, out object? value)
        {
            if (metadata is not null)
                foreach (var pair in metadata)
                    if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) { value = pair.Value; return true; }
            value = null; return false;
        }
    }

    private static bool IsImage(string value) => string.Equals(value, "image", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "vision", StringComparison.OrdinalIgnoreCase);
}
