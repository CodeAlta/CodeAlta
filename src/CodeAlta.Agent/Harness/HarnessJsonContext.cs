using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Agent.Harness;

[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(List<JsonElement>))]
internal sealed partial class HarnessJsonContext : JsonSerializerContext;
