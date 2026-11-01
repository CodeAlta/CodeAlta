using System.Text.Json;

namespace CodeAlta.Browser.Cdp.Generator;

/// <summary>
/// The commands and events CodeAlta uses, as <c>Domain.name</c>: only they and the types they reach are generated, so
/// the bindings stay a small part of the 49 domains of the protocol.
/// </summary>
/// <param name="Commands">The commands.</param>
/// <param name="Events">The events.</param>
internal sealed record CdpMethodMap(IReadOnlyList<string> Commands, IReadOnlyList<string> Events)
{
    /// <summary>Reads a map: <c>{ "commands": [...], "events": [...] }</c>.</summary>
    /// <exception cref="InvalidDataException">The file is not a map.</exception>
    public static CdpMethodMap Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return new CdpMethodMap(Names(document.RootElement, "commands"), Names(document.RootElement, "events"));
    }

    private static IReadOnlyList<string> Names(JsonElement root, string name)
        => root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(static item => item.GetString() ?? throw new InvalidDataException("A name of the method map is not a string.")).Distinct()]
            : throw new InvalidDataException($"The method map has no \"{name}\" array.");
}
