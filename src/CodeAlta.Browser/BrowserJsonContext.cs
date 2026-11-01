using System.Text.Json.Serialization;

namespace CodeAlta.Browser;

/// <summary>The source-generated JSON metadata of the values the browser tools write themselves.</summary>
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
internal sealed partial class BrowserJsonContext : JsonSerializerContext;
