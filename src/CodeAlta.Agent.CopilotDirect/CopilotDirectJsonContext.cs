using System.Text.Json.Serialization;

namespace CodeAlta.Agent.CopilotDirect;

[JsonSerializable(typeof(CopilotTokenResponse))]
[JsonSerializable(typeof(CopilotDeviceCodeResponse))]
[JsonSerializable(typeof(GitHubDeviceTokenResponse))]
[JsonSerializable(typeof(CopilotDirectCredentialCache))]
[JsonSerializable(typeof(CopilotModelsResponse))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, object?>[]))]
[JsonSerializable(typeof(List<Dictionary<string, object?>>))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class CopilotDirectJsonContext : JsonSerializerContext;
