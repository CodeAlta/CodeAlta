using System.Text.Json.Serialization;

namespace CodeAlta.LiveTool;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AltaAskRequest))]
[JsonSerializable(typeof(AltaAskQuestion))]
[JsonSerializable(typeof(AltaAskChoice))]
[JsonSerializable(typeof(AltaAskFreeform))]
[JsonSerializable(typeof(AltaAskFile))]
[JsonSerializable(typeof(AltaAskAnswer))]
[JsonSerializable(typeof(IReadOnlyList<AltaAskAnswer>))]
internal sealed partial class AltaAskJsonSerializerContext : JsonSerializerContext;
