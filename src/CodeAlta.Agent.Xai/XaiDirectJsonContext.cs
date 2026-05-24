using System.Text.Json.Serialization;

namespace CodeAlta.Agent.Xai;

[JsonSerializable(typeof(XaiDirectCredentialCache))]
[JsonSerializable(typeof(XaiModelsResponse))]
internal sealed partial class XaiDirectJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
