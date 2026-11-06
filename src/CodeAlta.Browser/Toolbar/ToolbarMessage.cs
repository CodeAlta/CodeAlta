using System.Text.Json;
using CodeAlta.Browser.Annotations;

namespace CodeAlta.Browser.Toolbar;

/// <summary>What the CodeAlta toolbar of a document reported.</summary>
/// <param name="Type">
/// <c>ready</c> (a document started), <c>theme</c> (the user chose the dark or the light look of the toolbar),
/// <c>position</c> (the user moved the toolbar), or a message of one of its tools: <c>annotation</c> (the user saved a
/// note), <c>edit</c> (the user changed the note of one), <c>remove</c>, or <c>send</c> (the user sends the annotations
/// to the session, with the text in <see cref="Note"/>).
/// </param>
/// <param name="ContextId">The isolated world of the document, which the host calls back.</param>
/// <param name="Url">The address the document says it has.</param>
/// <param name="Id">The id of the annotation.</param>
/// <param name="Note">The note the user wrote.</param>
/// <param name="Elements">The elements of the annotation, in the order of the page.</param>
internal sealed record ToolbarMessage(string Type, int ContextId, string? Url, string? Id, string? Note, IReadOnlyList<AnnotatedElement>? Elements)
{
    /// <summary>The most elements one annotation keeps.</summary>
    public const int MaximumElements = 12;

    /// <summary>Gets the part of the page an <c>annotation</c> is about: a rectangle the user drew, or the box around its elements.</summary>
    public AnnotationArea? Area { get; init; }

    /// <summary>Gets the look of the toolbar a <c>theme</c> message asks for: <c>dark</c> or <c>light</c>.</summary>
    public string? Theme { get; init; }

    /// <summary>
    /// Gets where a <c>position</c> message says the user put the toolbar: its distance from the right and the bottom of
    /// the viewport, in CSS pixels; null when the user put it back in its place.
    /// </summary>
    public (double Right, double Bottom)? Position { get; init; }

    /// <summary>The message of a payload of the binding, or null when it is not one.</summary>
    public static ToolbarMessage? TryParse(string payload, int contextId)
    {
        if (string.IsNullOrEmpty(payload) || payload.Length > 256 * 1024) return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeValue) || typeValue.GetString() is not { } type) return null;
            double? Number(JsonElement owner, string name) => owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out var number) && double.IsFinite(number) ? Math.Clamp(number, 0, 100_000) : null;
            string? Text(string name, int limit) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() is { } text && text.Length > limit ? text[..limit] : value.GetString()
                : null;
            return type switch
            {
                "ready" => new ToolbarMessage(type, contextId, Text("url", 8192), null, null, null),
                "remove" when Text("id", 128) is { } id => new ToolbarMessage(type, contextId, null, id, null, null),
                "position" when root.TryGetProperty("position", out var position) && position.ValueKind == JsonValueKind.Null
                    => new ToolbarMessage(type, contextId, null, null, null, null),
                "position" when root.TryGetProperty("position", out var position) && position.ValueKind == JsonValueKind.Object
                    && Number(position, "right") is { } right && Number(position, "bottom") is { } bottom
                    => new ToolbarMessage(type, contextId, null, null, null, null) { Position = (right, bottom) },
                "theme" when Text("theme", 8) is "dark" or "light" => new ToolbarMessage(type, contextId, null, null, null, null) { Theme = Text("theme", 8) },
                "edit" when Text("id", 128) is { } id && Text("note", 4000) is { Length: > 0 } note => new ToolbarMessage(type, contextId, null, id, note, null),
                "send" => new ToolbarMessage(type, contextId, null, null, Text("text", 20_000) ?? string.Empty, null),
                "annotation" when Text("id", 128) is { } id && Text("note", 4000) is { Length: > 0 } note && Area(root) is { } area
                    && Elements(root) is var elements && (elements.Count != 0 || area.Drawn)
                    => new ToolbarMessage(type, contextId, Text("url", 8192), id, note, elements) { Area = area },
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }

        static IReadOnlyList<AnnotatedElement> Elements(JsonElement root)
            => root.TryGetProperty("elements", out var elements) && elements.ValueKind == JsonValueKind.Array
                ? [.. elements.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.Object).Take(MaximumElements).Select(AnnotatedElement.Read)]
                : [];

        static AnnotationArea? Area(JsonElement root)
        {
            if (!root.TryGetProperty("area", out var area) || area.ValueKind != JsonValueKind.Object) return null;
            double? Value(string name) => area.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
                && double.IsFinite(number) ? Math.Clamp(number, 0, 1_000_000) : null;
            if (Value("x") is not { } x || Value("y") is not { } y || Value("width") is not { } width || Value("height") is not { } height) return null;
            var drawn = area.TryGetProperty("drawn", out var flag) && flag.ValueKind == JsonValueKind.True;
            return new AnnotationArea(x, y, width, height, drawn);
        }
    }
}
