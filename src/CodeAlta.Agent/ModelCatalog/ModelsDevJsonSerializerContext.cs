using System.Text.Json.Serialization;

namespace CodeAlta.Agent.ModelCatalog;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(Dictionary<string, ModelsDevProviderDefinition>), TypeInfoPropertyName = "ModelsDevProviderMap")]
[JsonSerializable(typeof(ModelsDevProviderDefinition))]
[JsonSerializable(typeof(ModelsDevModelDefinition))]
[JsonSerializable(typeof(ModelsDevModalitiesDefinition))]
[JsonSerializable(typeof(ModelsDevCostDefinition))]
[JsonSerializable(typeof(ModelsDevCostTierDefinition))]
[JsonSerializable(typeof(ModelsDevCostThresholdDefinition))]
[JsonSerializable(typeof(ModelsDevLimitDefinition))]
[JsonSerializable(typeof(ModelsDevInterleavedDefinition))]
[JsonSerializable(typeof(ModelsDevReasoningOptionDefinition))]
internal partial class ModelsDevJsonSerializerContext : JsonSerializerContext
{
}
