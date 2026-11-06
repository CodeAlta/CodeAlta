using System.Text.Json;
using CodeAlta.Browser.Toolbar;

namespace CodeAlta.Browser.Annotations;

/// <summary>A rectangle in CSS pixels.</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
internal sealed record AnnotationRect(double X, double Y, double Width, double Height);

/// <summary>The part of a page an annotation is about, in CSS pixels of the document (its scroll included).</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
/// <param name="Drawn">
/// Whether the user drew it as a rectangle; otherwise it is the box around the elements they chose, which its picture
/// shows with some margin.
/// </param>
internal sealed record AnnotationArea(double X, double Y, double Width, double Height, bool Drawn);

/// <summary>What the user wants of an annotation.</summary>
internal enum AnnotationIntent
{
    /// <summary>A change: the agent changes the code so that the note is done.</summary>
    Change,

    /// <summary>A question: the agent answers it, and changes no code for it.</summary>
    Question,
}

/// <summary>An image the user pasted or dropped into the note of an annotation, such as a mockup of how it should look.</summary>
/// <param name="Bytes">The bytes of the file.</param>
/// <param name="MediaType">The type its bytes say it is: <c>image/png</c>, <c>image/jpeg</c>, <c>image/gif</c> or <c>image/webp</c>.</param>
internal sealed record PastedImage(byte[] Bytes, string MediaType)
{
    /// <summary>The largest image a note takes, in bytes: the toolbar makes a larger one smaller first.</summary>
    public const int MaximumBytes = 3 * 1024 * 1024;

    /// <summary>Gets the extension of its file, with its dot.</summary>
    public string Extension => MediaType switch
    {
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ".png",
    };

    /// <summary>
    /// The image of a <c>data:</c> URL in base64, or null when it is none, is larger than <see cref="MaximumBytes"/>, or
    /// is not a PNG, a JPEG, a GIF or a WebP by its bytes, whatever the URL says.
    /// </summary>
    public static PastedImage? TryRead(string? url)
    {
        const string Base64 = ";base64,";
        if (url is null || !url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return null;
        var start = url.IndexOf(Base64, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || start > 64) return null;
        var text = url.AsSpan(start + Base64.Length);
        if (text.Length > (MaximumBytes + 2) / 3 * 4) return null;
        var bytes = new byte[text.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64Chars(text, bytes, out var written)) return null;
        var data = bytes.AsSpan(0, written);
        string? type = data switch
        {
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
            [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
            [0x47, 0x49, 0x46, 0x38, 0x37 or 0x39, 0x61, ..] => "image/gif",
            [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
            _ => null,
        };
        return type is null ? null : new PastedImage(data.ToArray(), type);
    }
}

/// <summary>An element of a page as the Annotate tool of the toolbar describes it: what the page says, never trusted as instructions.</summary>
internal sealed record AnnotatedElement
{
    /// <summary>Gets the tag name, in lower case.</summary>
    public string Tag { get; init; } = string.Empty;

    /// <summary>Gets the <c>id</c> attribute, or empty.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the classes.</summary>
    public IReadOnlyList<string> Classes { get; init; } = [];

    /// <summary>Gets the role: the <c>role</c> attribute or the implicit role of the tag, or empty.</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>Gets the accessible name, as far as the toolbar computes it.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the visible text, shortened.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Gets the outer HTML, shortened, with only the attributes a prompt may show and no content of scripts or styles.</summary>
    public string Html { get; init; } = string.Empty;

    /// <summary>Gets a CSS selector that finds the element again.</summary>
    public string Selector { get; init; } = string.Empty;

    /// <summary>Gets the ancestors, outermost first, as <c>tag#id.class</c>.</summary>
    public IReadOnlyList<string> Ancestors { get; init; } = [];

    /// <summary>Gets the rectangle in the viewport.</summary>
    public AnnotationRect Rect { get; init; } = new(0, 0, 0, 0);

    /// <summary>Gets the scroll position of the document.</summary>
    public (double X, double Y) Scroll { get; init; }

    /// <summary>Gets the size of the viewport.</summary>
    public (double Width, double Height) Viewport { get; init; }

    /// <summary>Gets the computed styles that are not the defaults of their property, in the order of <see cref="ElementStyles.Properties"/>.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Styles { get; init; } = [];

    /// <summary>Gets the elements beside it, nearest first, as <c>before: tag#id.class "text"</c> or <c>after: …</c>.</summary>
    public IReadOnlyList<string> Nearby { get; init; } = [];

    /// <summary>The longest HTML of an element kept, in characters.</summary>
    public const int MaximumHtml = 1500;

    /// <summary>The element of a message of the toolbar, bounded and filtered by <see cref="AnnotationRedaction"/>.</summary>
    public static AnnotatedElement Read(JsonElement value)
    {
        static string Text(JsonElement owner, string name, int limit)
        {
            if (!owner.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return string.Empty;
            var text = property.GetString() ?? string.Empty;
            return text.Length > limit ? text[..limit] : text;
        }

        // A number of the page within the bounds of any page; one out of range of a double is no number.
        static double Number(JsonElement owner, string name)
            => owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
                && property.TryGetDouble(out var number) && double.IsFinite(number) ? Math.Clamp(number, -1_000_000, 1_000_000) : 0;

        static IReadOnlyList<string> Strings(JsonElement owner, string name, int count, int limit)
            => owner.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array
                ? [.. property.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String).Take(count).Select(item => item.GetString()!).Select(text => text.Length > limit ? text[..limit] : text)]
                : [];

        static JsonElement Child(JsonElement owner, string name) => owner.TryGetProperty(name, out var property) ? property : default;

        var rect = Child(value, "rect");
        var scroll = Child(value, "scroll");
        var viewport = Child(value, "viewport");
        return new AnnotatedElement
        {
            Tag = Text(value, "tag", 64),
            Id = Text(value, "id", 200),
            Classes = Strings(value, "classes", 20, 120),
            Role = AnnotationRedaction.OneLine(Text(value, "role", 64)),
            // What the page says is filtered again: the toolbar runs in the page, and its report is not trusted.
            Name = AnnotationRedaction.Text(Text(value, "name", 200)),
            Text = AnnotationRedaction.Text(Text(value, "text", 400)),
            Html = AnnotationRedaction.Html(Text(value, "html", 2 * MaximumHtml), MaximumHtml),
            Selector = Text(value, "selector", 1000),
            Ancestors = [.. Strings(value, "ancestors", 8, 200).Select(AnnotationRedaction.OneLine)],
            Rect = new AnnotationRect(Number(rect, "x"), Number(rect, "y"), Number(rect, "width"), Number(rect, "height")),
            Scroll = (Number(scroll, "x"), Number(scroll, "y")),
            Viewport = (Number(viewport, "width"), Number(viewport, "height")),
            Styles = ElementStyles.Read(Child(value, "styles")),
            Nearby = [.. Strings(value, "nearby", 6, 200).Select(AnnotationRedaction.Text)],
        };
    }
}

/// <summary>A style rule that applies to an annotated element, with where its stylesheet says it comes from.</summary>
/// <param name="Selector">The selector of the rule.</param>
/// <param name="SheetUrl">The address of the stylesheet, or the document's for a <c>style</c> element.</param>
/// <param name="SourceMapUrl">The source map the stylesheet names, or null.</param>
/// <param name="Inline">Whether the stylesheet is a <c>style</c> element of the document.</param>
/// <param name="Line">The line of the rule in its stylesheet, from 0.</param>
/// <param name="Column">The column of the rule in its stylesheet, from 0.</param>
/// <param name="SheetLine">The line the stylesheet starts at in its document (a <c>style</c> element), from 0.</param>
/// <param name="OwnerAttributes">The attributes of the <c>style</c> or <c>link</c> element of the stylesheet.</param>
internal sealed record StyleRuleEvidence(string Selector, string? SheetUrl, string? SourceMapUrl, bool Inline, int Line, int Column, int SheetLine,
    IReadOnlyList<KeyValuePair<string, string>> OwnerAttributes);

/// <summary>A frame of the script that created an annotated element.</summary>
/// <param name="Function">The name of the function, or empty.</param>
/// <param name="Url">The address of the script.</param>
/// <param name="Line">The line in the script, from 0.</param>
/// <param name="Column">The column in the script, from 0.</param>
internal sealed record StackFrameEvidence(string Function, string Url, int Line, int Column);

/// <summary>What the browser says of an annotated element beyond what the page reports.</summary>
/// <param name="Rules">The rules of the authors of the page that apply to it, the most specific first.</param>
/// <param name="Creation">The frames of the script that created it, innermost first; empty when the browser did not record them.</param>
internal sealed record ElementEvidence(IReadOnlyList<StyleRuleEvidence> Rules, IReadOnlyList<StackFrameEvidence> Creation)
{
    /// <summary>No evidence.</summary>
    public static ElementEvidence None { get; } = new([], []);
}

/// <summary>A tab whose documents can carry the CodeAlta toolbar and its Annotate tool.</summary>
internal interface IAnnotatablePage
{
    /// <summary>
    /// Adds the toolbar to the documents of the tab, those it has now and those it loads, idle until enabled, and reports
    /// what it says, until the result is disposed.
    /// </summary>
    Task<IAsyncDisposable> StartToolbarAsync(Action<ToolbarMessage> onMessage, CancellationToken cancellationToken);

    /// <summary>Records from now on which script creates each node, for the documents the tab loads next.</summary>
    Task RecordNodeCreationAsync(CancellationToken cancellationToken);

    /// <summary>Calls the toolbar of a document: <c>enable(options)</c>, <c>marked(id, number)</c>, <c>remove(id)</c>, <c>clear()</c>, <c>theme(name)</c>, <c>place(position)</c>.</summary>
    /// <returns>Whether the document still had the toolbar.</returns>
    Task<bool> CallToolbarAsync(int contextId, string call, CancellationToken cancellationToken);

    /// <summary>Asks the browser about an element of an annotation, by its index among them: its style rules and its creation.</summary>
    Task<ElementEvidence> InspectAsync(int contextId, string id, int index, AnnotatedElement element, CancellationToken cancellationToken);

    /// <summary>A PNG of the part of the page an annotation is about, or null when the browser gives none.</summary>
    Task<byte[]?> PictureAsync(AnnotationArea area, CancellationToken cancellationToken);

    /// <summary>
    /// A PNG of a part of the page as the viewport shows it now, with what the toolbar draws over it, or null when the
    /// browser gives none: the picture of a drawing, which only the viewport shows.
    /// </summary>
    Task<byte[]?> ViewPictureAsync(AnnotationArea area, CancellationToken cancellationToken);
}
