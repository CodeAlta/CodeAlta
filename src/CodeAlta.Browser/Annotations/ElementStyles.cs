using System.Collections.Frozen;
using System.Text.Json;

namespace CodeAlta.Browser.Annotations;

/// <summary>
/// The computed styles an annotation tells of an element: sixteen properties of its box, its colors and its font, without
/// the values that are the defaults of their property and so say nothing.
/// </summary>
internal static class ElementStyles
{
    // The longest value kept.
    private const int MaximumValue = 200;

    /// <summary>The properties, by their names in CSS, in the order a prompt shows them.</summary>
    public static IReadOnlyList<string> Properties { get; } =
    [
        "display", "position", "width", "height", "margin", "padding", "color", "background-color", "border", "border-radius",
        "font-family", "font-size", "font-weight", "line-height", "text-align", "z-index",
    ];

    private static readonly FrozenSet<string> Known = Properties.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Whether a value is the default of its property, or says nothing of the element.</summary>
    public static bool IsDefault(string property, string value)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || value is "auto" or "normal" or "none") return true;
        return property switch
        {
            "display" => value == "inline",
            "position" => value == "static",
            "background-color" => value is "transparent" or "rgba(0, 0, 0, 0)",
            "margin" or "padding" or "border-radius" => value == "0px",
            "border" => value.StartsWith("none", StringComparison.Ordinal) || value.StartsWith("0px", StringComparison.Ordinal)
                || value.StartsWith("medium none", StringComparison.Ordinal),
            "text-align" => value == "start",
            _ => false,
        };
    }

    /// <summary>
    /// The styles of an element in a message of the toolbar, which the host does not trust: only the known properties, in
    /// their order, each value on one line and bounded, the defaults left out.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Read(JsonElement styles)
    {
        if (styles.ValueKind != JsonValueKind.Object) return [];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = 0;
        foreach (var property in styles.EnumerateObject())
        {
            // A page cannot make the list long: the bar sends sixteen at most.
            if (++seen > 64) break;
            if (!Known.Contains(property.Name) || property.Value.ValueKind != JsonValueKind.String) continue;
            var value = property.Value.GetString() ?? string.Empty;
            value = AnnotationRedaction.OneLine(value.Length > 2 * MaximumValue ? value[..(2 * MaximumValue)] : value);
            if (value.Length > MaximumValue) value = value[..MaximumValue] + "…";
            if (!IsDefault(property.Name, value)) values[property.Name] = value;
        }

        return [.. Properties.Where(values.ContainsKey).Select(name => new KeyValuePair<string, string>(name, values[name]))];
    }
}
