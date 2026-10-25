using System.Text.Json.Serialization;

namespace CodeAlta.Catalog;

// The small JSON files other processes read beside the journals: the session leases.
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionLeaseStore.LeaseContent))]
internal sealed partial class SessionFilesJsonContext : JsonSerializerContext;
