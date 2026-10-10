using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CodeAlta.Desktop;

/// <summary>
/// Reads the SVG file that a plugin names as the icon of a button or of a canvas, and returns a data URL that cannot run
/// or fetch anything: the page draws it as a mask in the color of the text.
/// </summary>
/// <remarks>
/// The file is rebuilt, not filtered: only the elements and attributes of a short list of shapes survive, with no script,
/// style, link, image or reference to another document. A file that is not an SVG, that is too large or that cannot be
/// read is refused. Results are kept while the file does not change.
/// </remarks>
internal sealed class PluginIcons
{
    /// <summary>Largest size of an icon file, in bytes.</summary>
    internal const int MaximumFileBytes = 32 * 1024;

    /// <summary>Largest number of elements of an icon.</summary>
    internal const int MaximumElements = 400;

    private const int MaximumCached = 128;
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private static readonly FrozenSet<string> Elements = new[]
    {
        "svg", "g", "path", "circle", "ellipse", "rect", "line", "polyline", "polygon", "defs", "clipPath", "mask", "title", "desc",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Geometry and presentation only. href, style, class, id references to other documents and every on* handler are left out.
    private static readonly FrozenSet<string> Attributes = new[]
    {
        "viewBox", "width", "height", "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry", "d", "points", "transform", "id",
        "fill", "fill-opacity", "fill-rule", "clip-rule", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin", "stroke-miterlimit",
        "stroke-dasharray", "stroke-dashoffset", "stroke-opacity", "opacity", "clip-path", "mask", "maskUnits", "clipPathUnits", "preserveAspectRatio",
        "pathLength",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (long Length, DateTime Written, string? Url)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a text names a file of the plugin rather than an icon of the libraries.</summary>
    /// <param name="icon">The icon as the plugin gave it.</param>
    internal static bool IsFile(string? icon)
        => icon is not null && (icon.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || icon.Contains('/') || icon.Contains('\\'));

    /// <summary>Reads the icon file of a plugin package.</summary>
    /// <param name="packageDirectory">The folder of the package, or null for a plugin that has none.</param>
    /// <param name="relativePath">The path in the package, as the plugin wrote it.</param>
    /// <returns>A <c>data:image/svg+xml</c> URL, or null when the file is not there, not allowed or not a clean SVG.</returns>
    internal string? Read(string? packageDirectory, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(packageDirectory) || string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 260
            || relativePath.Any(char.IsControl) || Path.IsPathRooted(relativePath)) return null;
        try
        {
            var root = Path.GetFullPath(packageDirectory);
            var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('\\', '/')));
            var rooted = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rooted, StringComparison.OrdinalIgnoreCase) || !full.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return null;
            var info = new FileInfo(full);
            // A link could lead out of the package: the icon is a plain file.
            if (!info.Exists || info.Length > MaximumFileBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null) return null;
            lock (_gate)
            {
                if (_cache.TryGetValue(full, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc) return known.Url;
            }

            string? url;
            try { url = Sanitize(File.ReadAllText(full, Encoding.UTF8)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
            lock (_gate)
            {
                if (_cache.Count >= MaximumCached) _cache.Clear();
                _cache[full] = (info.Length, info.LastWriteTimeUtc, url);
            }

            return url;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Rebuilds an SVG document with the elements and attributes of the list, as a data URL.</summary>
    /// <param name="text">The SVG document.</param>
    /// <returns>The data URL, or null when the text is not an SVG that the list can express.</returns>
    internal static string? Sanitize(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumFileBytes) return null;
        XDocument document;
        try
        {
            // No DTD, no entity, no resolver: nothing in the document can reach a file or a server.
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0, MaxCharactersInDocument = MaximumFileBytes * 2L };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException) { return null; }

        if (document.Root is not { } root || root.Name != Svg + "svg") return null;
        var count = 0;
        var clean = Rebuild(root, ref count);
        if (clean is null || count > MaximumElements) return null;
        if (clean.Attribute("viewBox") is null && !AddViewBox(clean)) return null;
        var markup = clean.ToString(SaveOptions.DisableFormatting);
        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(markup));
    }

    private static XElement? Rebuild(XElement source, ref int count)
    {
        if (source.Name.Namespace != Svg || !Elements.Contains(source.Name.LocalName)) return null;
        if (++count > MaximumElements) return null;
        var clean = new XElement(source.Name);
        foreach (var attribute in source.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                // The SVG namespace only.
                if (attribute.Name.LocalName is "xmlns" && attribute.Value == Svg.NamespaceName) clean.Add(new XAttribute(attribute.Name, attribute.Value));
                continue;
            }

            if (attribute.Name.Namespace != XNamespace.None || !Attributes.Contains(attribute.Name.LocalName) || !SafeValue(attribute.Value)) continue;
            clean.Add(new XAttribute(attribute.Name, attribute.Value));
        }

        foreach (var node in source.Nodes())
        {
            switch (node)
            {
                case XElement child:
                    // An element outside the list (script, style, image, use, foreignObject...) is dropped with what it holds.
                    if (Rebuild(child, ref count) is { } rebuilt) clean.Add(rebuilt);
                    else if (count > MaximumElements) return null;
                    break;
                case XText text when source.Name.LocalName is "title" or "desc":
                    clean.Add(new XText(text.Value));
                    break;
            }
        }

        return clean;
    }

    // A value may name a part of the same document (url(#a)) and nothing else: no address, no data, no script.
    private static bool SafeValue(string value)
    {
        if (value.Length > 8 * 1024 || value.Any(static character => character is '<' or '>' or '\0')) return false;
        var lowered = value.ToLowerInvariant();
        if (lowered.Contains("javascript:", StringComparison.Ordinal) || lowered.Contains("data:", StringComparison.Ordinal) || lowered.Contains("@import", StringComparison.Ordinal)
            || lowered.Contains("expression(", StringComparison.Ordinal)) return false;
        var index = 0;
        while ((index = lowered.IndexOf("url(", index, StringComparison.Ordinal)) >= 0)
        {
            var close = lowered.IndexOf(')', index);
            if (close < 0) return false;
            var target = lowered[(index + 4)..close].Trim().Trim('\'', '"').Trim();
            if (!target.StartsWith('#')) return false;
            index = close;
        }

        return true;
    }

    // An icon without a viewBox is drawn at the size of its width and height; the mask needs a box to scale.
    private static bool AddViewBox(XElement svg)
    {
        if (!TryNumber(svg.Attribute("width")?.Value, out var width) || !TryNumber(svg.Attribute("height")?.Value, out var height)) return false;
        svg.SetAttributeValue("viewBox", string.Create(CultureInfo.InvariantCulture, $"0 0 {width} {height}"));
        return true;
    }

    private static bool TryNumber(string? text, out double value)
    {
        value = 0;
        if (text is null) return false;
        var digits = text.Trim();
        if (digits.EndsWith("px", StringComparison.Ordinal)) digits = digits[..^2];
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value is > 0 and <= 4096;
    }
}
