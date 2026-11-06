using System.Text.Json;
using CodeAlta.Browser.Annotations;

namespace CodeAlta.Browser.Toolbar;

/// <summary>What the CodeAlta toolbar of a document reported.</summary>
/// <param name="Type">
/// <c>ready</c> (a document started), <c>theme</c> (the user chose the dark or the light look of the toolbar),
/// <c>position</c> (the user moved the toolbar), <c>details</c> (the user turned the details of the page in the prompt on
/// or off), or a message of one of its tools: <c>annotation</c> (the user saved a note), <c>drawing</c> (the user saved a
/// drawing with a note: the toolbar shows it until the host took its picture), <c>image</c> (an image the user pasted into
/// the note of one), <c>edit</c> (the user changed the note of one), <c>remove</c>, or <c>send</c> (the user sends the
/// annotations to the session, with the text in <see cref="Note"/>).
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

    /// <summary>The most images the user pastes into the note of one annotation.</summary>
    public const int MaximumImages = 4;

    // The longest message: one image is the largest one, the others are bounded much lower.
    private const int MaximumPayload = 256 * 1024;
    private const int MaximumImagePayload = PastedImage.MaximumBytes / 3 * 4 + 64 * 1024;

    /// <summary>Gets the part of the page an <c>annotation</c> is about: a rectangle the user drew, or the box around its elements.</summary>
    public AnnotationArea? Area { get; init; }

    /// <summary>
    /// Gets what the user wants of an <c>annotation</c> or of the annotation of an <c>edit</c>: a change or an answer;
    /// null when the message does not say.
    /// </summary>
    public AnnotationIntent? Intent { get; init; }

    /// <summary>Gets the image an <c>image</c> message attaches to the annotation <see cref="Id"/>.</summary>
    public PastedImage? Image { get; init; }

    /// <summary>
    /// Gets the names of the images of the annotation an <c>edit</c> keeps, as the host listed them; null when the
    /// message does not say, and the annotation keeps them all.
    /// </summary>
    public IReadOnlyList<string>? KeptImages { get; init; }

    /// <summary>Gets the look of the toolbar a <c>theme</c> message asks for: <c>dark</c> or <c>light</c>.</summary>
    public string? Theme { get; init; }

    /// <summary>
    /// Gets where a <c>position</c> message says the user put the toolbar: its distance from the right and the bottom of
    /// the viewport, in CSS pixels; null when the user put it back in its place.
    /// </summary>
    public (double Right, double Bottom)? Position { get; init; }

    /// <summary>Gets whether a <c>details</c> message asks for the details of the page in the prompt.</summary>
    public bool Details { get; init; }

    /// <summary>The message of a payload of the binding, or null when it is not one.</summary>
    public static ToolbarMessage? TryParse(string payload, int contextId)
    {
        if (string.IsNullOrEmpty(payload) || payload.Length > MaximumImagePayload) return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeValue) || typeValue.GetString() is not { } type) return null;
            if (payload.Length > MaximumPayload && type != "image") return null;
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
                "details" when root.TryGetProperty("on", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False
                    => new ToolbarMessage(type, contextId, null, null, null, null) { Details = on.ValueKind == JsonValueKind.True },
                "theme" when Text("theme", 8) is "dark" or "light" => new ToolbarMessage(type, contextId, null, null, null, null) { Theme = Text("theme", 8) },
                "edit" when Text("id", 128) is { } id && Text("note", 4000) is { Length: > 0 } note
                    => new ToolbarMessage(type, contextId, null, id, note, null) { Intent = Intent(root), KeptImages = Names(root, "images") },
                "image" when Text("id", 128) is { } id && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String && PastedImage.TryRead(data.GetString()) is { } image
                    => new ToolbarMessage(type, contextId, null, id, null, null) { Image = image },
                "send" => new ToolbarMessage(type, contextId, null, null, Text("text", 20_000) ?? string.Empty, null),
                "annotation" when Text("id", 128) is { } id && Text("note", 4000) is { Length: > 0 } note && Area(root) is { } area
                    && Elements(root) is var elements && (elements.Count != 0 || area.Drawn)
                    => new ToolbarMessage(type, contextId, Text("url", 8192), id, note, elements) { Area = area, Intent = Intent(root) ?? AnnotationIntent.Change },
                // A drawing is about the part of the page it covers, and the elements its shapes surround or point at.
                "drawing" when Text("id", 128) is { } id && Text("note", 4000) is { Length: > 0 } note && Area(root) is { Width: >= 1, Height: >= 1 } area
                    => new ToolbarMessage(type, contextId, Text("url", 8192), id, note, Elements(root)) { Area = area with { Drawn = true }, Intent = Intent(root) ?? AnnotationIntent.Change },
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }

        static IReadOnlyList<string>? Names(JsonElement root, string name)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
                ? [.. value.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String).Take(MaximumImages).Select(static item => item.GetString()!).Where(static text => text.Length <= 64)]
                : null;

        static AnnotationIntent? Intent(JsonElement root)
            => root.TryGetProperty("intent", out var value) && value.ValueKind == JsonValueKind.String
                ? value.ValueEquals("question") ? AnnotationIntent.Question : AnnotationIntent.Change
                : null;

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