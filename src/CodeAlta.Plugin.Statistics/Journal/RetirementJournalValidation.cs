using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Plugin.Statistics.Journal;

// The normal reader deliberately tolerates provider/legacy data. That tolerance is not evidence for destroying a source.
internal static class RetirementJournalValidation
{
    public static bool IsValid(JsonElement root, JournalRecordKind kind, string sessionId)
    {
        var envelope = false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name)) return false;
            if (property.NameEquals("backendId")) envelope = true;
            if (envelope && property.Name is not ("backendId" or "sessionId" or "timestamp" or "runId")) return false;
        }

        if (!envelope) return false;
        AgentJournalValidation.ValidateEvent(root);
        if (kind == JournalRecordKind.Header)
        {
            if (root.GetProperty("backendEventType").GetString() != "codealta.sessionHeader") return false;
            var header = root.GetProperty("raw").Deserialize(RetirementCatalogJsonContext.Default.SessionViewJournalHeader);
            if (header is null || header.SessionId != sessionId || header.SchemaVersion != 1) return false;
        }
        else if (kind == JournalRecordKind.State)
        {
            if (root.GetProperty("backendEventType").GetString() != "codealta.sessionState") return false;
            if (root.GetProperty("raw").Deserialize(RetirementCatalogJsonContext.Default.SessionViewLocalState) is null) return false;
        }

        // Dynamic 'details' JSON has no CLR schema. Validate the fields read by the reducer, not opaque tool-result content.
        if (root.TryGetProperty("details", out var details) && details.ValueKind != JsonValueKind.Null)
        {
            if (details.ValueKind != JsonValueKind.Object) return false;
            if (!Strings(details, "toolName", "server", "mcpServerName", "mcpToolName", "tool", "diff", "providerKey", "modelId", "reasoningEffort", "agentPromptId", "trigger")) return false;
            if (!Integers(details, "tokensBefore", "tokensAfter")) return false;
            foreach (var name in new[] { "readFiles", "modifiedFiles" })
            {
                if (details.TryGetProperty(name, out var files) && files.ValueKind != JsonValueKind.Null
                    && (files.ValueKind != JsonValueKind.Array || files.EnumerateArray().Any(file => file.ValueKind != JsonValueKind.String))) return false;
            }

            if (details.TryGetProperty("items", out var items) && items.ValueKind != JsonValueKind.Null
                && (items.ValueKind != JsonValueKind.Array || items.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("$type", out var type) || type.ValueKind != JsonValueKind.String))) return false;
            if (details.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("success", out var success) && success.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) return false;
        }

        return true;
    }

    private static bool Strings(JsonElement value, params string[] names)
        => names.All(name => !value.TryGetProperty(name, out var field) || field.ValueKind is JsonValueKind.String or JsonValueKind.Null);

    private static bool Integers(JsonElement value, params string[] names)
        => names.All(name => !value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null || (field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out _)));
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SessionViewJournalHeader))]
[JsonSerializable(typeof(SessionViewLocalState))]
internal sealed partial class RetirementCatalogJsonContext : JsonSerializerContext;
