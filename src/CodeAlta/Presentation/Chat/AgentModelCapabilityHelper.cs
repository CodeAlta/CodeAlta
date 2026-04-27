using System.Collections;
using System.Text.Json;
using CodeAlta.Agent;

namespace CodeAlta.Presentation.Chat;

internal static class AgentModelCapabilityHelper
{
    public static bool SupportsImageInput(AgentBackendId backendId, AgentModelInfo? model)
    {
        if (model?.Capabilities is { } capabilities)
        {
            if (TryGetBooleanCapability(capabilities, out var supported,
                    "supportsImageInput",
                    "imageInput",
                    "supportsImages",
                    "supportsVision",
                    "vision"))
            {
                return supported;
            }

            if (TryGetInputModalities(capabilities, out var inputModalities))
            {
                return inputModalities.Any(static modality =>
                    string.Equals(modality, "image", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(modality, "vision", StringComparison.OrdinalIgnoreCase));
            }
        }

        // Codex's native protocol accepts LocalImage user inputs. If metadata was not loaded yet, keep the UX usable for
        // the default Codex model while still honoring explicit negative metadata above.
        return string.Equals(backendId.Value, AgentBackendIds.Codex.Value, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(model?.Id, "codex-auto-review", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetBooleanCapability(
        IReadOnlyDictionary<string, object?> capabilities,
        out bool value,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (TryGetCapabilityValue(capabilities, key, out var raw) && TryConvertToBoolean(raw, out value))
            {
                return true;
            }
        }

        value = false;
        return false;
    }

    private static bool TryGetInputModalities(
        IReadOnlyDictionary<string, object?> capabilities,
        out IReadOnlyList<string> modalities)
    {
        if (TryGetCapabilityValue(capabilities, "inputModalities", out var value) ||
            TryGetCapabilityValue(capabilities, "input_modalities", out value))
        {
            modalities = EnumerateStrings(value).ToArray();
            return modalities.Count > 0;
        }

        modalities = [];
        return false;
    }

    private static bool TryGetCapabilityValue(IReadOnlyDictionary<string, object?> capabilities, string key, out object? value)
    {
        if (capabilities.TryGetValue(key, out value))
        {
            return true;
        }

        foreach (var entry in capabilities)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryConvertToBoolean(object? value, out bool result)
    {
        switch (value)
        {
            case bool boolean:
                result = boolean;
                return true;
            case string text when bool.TryParse(text, out var parsed):
                result = parsed;
                return true;
            case JsonElement { ValueKind: JsonValueKind.True }:
                result = true;
                return true;
            case JsonElement { ValueKind: JsonValueKind.False }:
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static IEnumerable<string> EnumerateStrings(object? value)
    {
        switch (value)
        {
            case null:
                yield break;
            case string text:
                yield return text;
                yield break;
            case JsonElement element when element.ValueKind == JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } itemText)
                    {
                        yield return itemText;
                    }
                }

                yield break;
            case JsonElement element when element.ValueKind == JsonValueKind.String:
                yield return element.GetString() ?? string.Empty;
                yield break;
            case IEnumerable enumerable:
                foreach (var item in enumerable)
                {
                    if (item is string itemText)
                    {
                        yield return itemText;
                    }
                }

                yield break;
        }
    }
}
