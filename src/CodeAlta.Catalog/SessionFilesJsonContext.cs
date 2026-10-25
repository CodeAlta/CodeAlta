using System.Text.Json.Serialization;

namespace CodeAlta.Catalog;

// The small JSON files other processes read beside the journals: the session leases and markers.
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionLeaseStore.LeaseContent))]
[JsonSerializable(typeof(SessionMarker))]
internal sealed partial class SessionFilesJsonContext : JsonSerializerContext;
