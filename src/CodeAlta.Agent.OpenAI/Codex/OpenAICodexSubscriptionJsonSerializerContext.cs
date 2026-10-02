using System.Text.Json.Serialization;
using System.Text.Json;

namespace CodeAlta.Agent.OpenAI.Codex;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(OpenAICodexSubscriptionCredential))]
internal sealed partial class OpenAICodexSubscriptionJsonSerializerContext : JsonSerializerContext;
