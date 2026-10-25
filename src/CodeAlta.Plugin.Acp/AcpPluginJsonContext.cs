using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Plugin.Acp;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(AcpModelCache))]
[JsonSerializable(typeof(AcpInstallMarker))]
internal sealed partial class AcpPluginJsonContext : JsonSerializerContext;
