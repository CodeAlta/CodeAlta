using System.Text.Json.Serialization;

namespace CodeAlta.CodeMode;

/// <summary>Source-generated JSON metadata of the values Code Mode serializes itself.</summary>
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class CodeModeJsonContext : JsonSerializerContext;
