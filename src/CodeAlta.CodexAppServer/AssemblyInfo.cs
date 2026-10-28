using System.Diagnostics.CodeAnalysis;

// The converters of the generated unions read and write their values with the options of CodexProtocolJsonContext,
// whose resolver is the source-generated one: nothing is resolved by reflection.
[assembly: UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026", Justification = "The options come from the JSON source generator.")]
[assembly: UnconditionalSuppressMessage("ReflectionAnalysis", "IL3050", Justification = "The options come from the JSON source generator.")]
