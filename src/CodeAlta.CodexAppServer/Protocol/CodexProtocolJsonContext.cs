using System.Text.Json.Serialization;

namespace CodeAlta.CodexAppServer.Protocol;

/// <summary>
/// The serializer of the protocol types. Absent values are not written; the kind of a union may come after its other
/// properties, as a server may write them in any order.
/// </summary>
/// <remarks>
/// The types are registered by the generated part only: the JSON source generator runs once for each part that
/// registers types, and two runs collide.
/// </remarks>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, AllowOutOfOrderMetadataProperties = true)]
public sealed partial class CodexProtocolJsonContext : JsonSerializerContext;
