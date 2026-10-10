using System.Text;
using System.Text.RegularExpressions;

namespace CodeAlta.Catalog.Documentation;

/// <summary>
/// Turns a page of the site into plain Markdown: the site runs templates over its pages (the screenshots in their
/// two versions, the marks of the comparison tables, the addresses of the site) and lays pictures out with HTML,
/// which an application that shows Markdown has no use for.
/// </summary>
internal static partial class ShippedDocumentationMarkup
{
    /// <summary>The site of the guide: an address of the site that names no shipped page goes there.</summary>
    internal const string WebSite = "https://codealta.github.io";

    private const int MaximumFigureLines = 400;
    private const int MaximumSvgLength = 256 * 1024;
    private const int MaximumAltLength = 400;

    /// <summary>Splits the front matter from a page, and reads its title.</summary>
    /// <returns>The title the front matter gives, or null, and the text after it with <c>\n</c> line ends.</returns>
    internal static (string? Title, string Body) SplitFrontMatter(string text)
    {
        var body = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!body.StartsWith("---\n", StringComparison.Ordinal)) return (null, body);
        var end = body.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return (null, body);
        var after = body.IndexOf('\n', end + 4);
        // The closing line is the three dashes alone.
        if (body.AsSpan(end + 4, (after < 0 ? body.Length : after) - end - 4).Trim().Length > 0) return (null, body);
        string? title = null;
        foreach (var line in body[4..(end + 1)].Split('\n'))
        {
            if (FrontMatterTitle().Match(line) is not { Success: true } match) continue;
            title = match.Groups[1].Value.Trim();
            if (title.Length >= 2 && title[0] == title[^1] && title[0] is '"' or '\'') title = title[1..^1];
            break;
        }

        return (title, after < 0 ? string.Empty : body[(after + 1)..]);
    }

    /// <summary>The text of the first heading of a page, outside its code blocks; null when it has none.</summary>
    internal static string? FirstHeading(string body)
    {
        var fence = default(Fence);
        foreach (var line in body.Split('\n'))
        {
            if (fence.Step(line) || fence.Inside) continue;
            if (Heading().Match(line) is { Success: true } match) return match.Groups[2].Value;
        }

        return null;
    }

    /// <summary>Reads the body of a page into its parts.</summary>
    /// <param name="body">The text of the page after its front matter, with <c>\n</c> line ends.</param>
    /// <param name="pagePath">The path of the page below the folder of the guide.</param>
    /// <param name="resolvePage">Gives the path of a page as the guide writes it; null when the guide has no such page.</param>
    /// <param name="hasImage">Whether the guide ships a picture.</param>
    internal static IReadOnlyList<ShippedDocumentationBlock> Expand(string body, string pagePath, Func<string, string?> resolvePage, Func<string, bool> hasImage)
    {
        var context = new Context(pagePath, resolvePage, hasImage);
        var blocks = new List<ShippedDocumentationBlock>();
        var text = new List<string>();
        var afterFigure = false;
        var fence = default(Fence);
        var lines = body.Split('\n');

        void Flush(bool beforeFigure)
        {
            int start = 0, end = text.Count;
            // What only laid pictures out side by side goes with them; blank lines at both ends go anyway.
            while (start < end && (string.IsNullOrWhiteSpace(text[start]) || afterFigure && LayoutLine().IsMatch(text[start]))) start++;
            while (end > start && (string.IsNullOrWhiteSpace(text[end - 1]) || beforeFigure && LayoutLine().IsMatch(text[end - 1]))) end--;
            if (end > start) blocks.Add(new(string.Join('\n', text.Skip(start).Take(end - start)), null));
            text.Clear();
            afterFigure = false;
        }

        void AddFigure(ShippedDocumentationFigure? figure)
        {
            Flush(beforeFigure: true);
            // A picture the application does not ship (one of the other application) is left out with its caption.
            if (figure is not null) blocks.Add(new(null, figure));
            afterFigure = true;
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var inFence = fence.Inside;
            if (fence.Step(line) || inFence)
            {
                // The site runs its templates over code too; a link written in code stays as it is.
                text.Add(ExpandTemplates(line, context));
                continue;
            }

            if (Screenshot().Match(line) is { Success: true } shot)
            {
                var image = shot.Groups[1].Value;
                AddFigure(context.HasImage(image) ? new(image, null, Alt(shot.Groups[3].Value), Caption(shot.Groups[4].Value, context)) : null);
                continue;
            }

            var open = FigureOpen().Match(line);
            if (open.Success)
            {
                var builder = new StringBuilder();
                var last = -1;
                for (var next = index; next < lines.Length && next - index < MaximumFigureLines; next++)
                {
                    builder.Append(next == index ? lines[next][open.Index..] : lines[next]).Append('\n');
                    if (lines[next].Contains("</figure>", StringComparison.OrdinalIgnoreCase)) { last = next; break; }
                }

                if (last >= 0)
                {
                    if (!string.IsNullOrWhiteSpace(line[..open.Index])) text.Add(ExpandLine(line[..open.Index], context));
                    AddFigure(ReadFigure(builder.ToString(), context));
                    var close = lines[last].LastIndexOf("</figure>", StringComparison.OrdinalIgnoreCase) + "</figure>".Length;
                    if (!string.IsNullOrWhiteSpace(lines[last][close..])) text.Add(ExpandLine(lines[last][close..], context));
                    index = last;
                    continue;
                }
            }

            // The styles of the site are for the site.
            if (StyleOpen().IsMatch(line))
            {
                var last = index;
                while (last < lines.Length && !lines[last].Contains("</style>", StringComparison.OrdinalIgnoreCase)) last++;
                if (last < lines.Length) { index = last; continue; }
            }

            text.Add(ExpandLine(line, context));
        }

        Flush(beforeFigure: false);
        return blocks;
    }

    /// <summary>Finds a text in the lines of a page.</summary>
    internal static void Search(ShippedDocumentationPage page, string query, int maximumOfPage, int maximum, List<ShippedDocumentationSearchHit> hits)
    {
        var found = 0;
        string? heading = null;
        void Look(string line)
        {
            if (found >= maximumOfPage || found >= maximum) return;
            var plain = Plain(line);
            var at = plain.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return;
            const int Around = 90;
            var start = Math.Max(0, at - Around);
            var end = Math.Min(plain.Length, at + query.Length + Around);
            hits.Add(new(page.Path, page.Title, heading, (start > 0 ? "…" : string.Empty) + plain[start..end].Trim() + (end < plain.Length ? "…" : string.Empty)));
            found++;
        }

        foreach (var block in page.Blocks)
        {
            if (block.Figure is { } figure)
            {
                if (figure.Caption is { } caption) Look(caption);
                continue;
            }

            var fence = default(Fence);
            foreach (var line in block.Markdown!.Split('\n'))
            {
                if (fence.Step(line)) continue;
                if (!fence.Inside && Heading().Match(line) is { Success: true } match)
                {
                    heading = Plain(match.Groups[2].Value);
                    // A heading that has the text is a place of its own.
                }

                Look(line);
            }
        }
    }

    // A line as a reader sees it: without tags, the marks of Markdown and the targets of its links.
    private static string Plain(string line)
    {
        var text = MarkdownLink().Replace(Tag().Replace(line, string.Empty), "$1");
        text = LineMark().Replace(text, string.Empty).Replace("`", string.Empty, StringComparison.Ordinal).Replace("**", string.Empty, StringComparison.Ordinal);
        return Whitespace().Replace(System.Net.WebUtility.HtmlDecode(text), " ").Trim();
    }

    private static string Alt(string text)
    {
        var alt = ShippedDocumentation.PlainText(text) ?? string.Empty;
        return alt.Length <= MaximumAltLength ? alt : alt[..MaximumAltLength];
    }

    private static string? Caption(string html, Context context)
    {
        var caption = Whitespace().Replace(ExpandLine(html, context), " ").Trim();
        return caption.Length == 0 ? null : caption;
    }

    // A <figure> of a page: a picture of the guide, or a drawing, and a caption. Null for a picture the guide does not ship.
    private static ShippedDocumentationFigure? ReadFigure(string html, Context context)
    {
        var caption = FigureCaption().Match(html) is { Success: true } captioned ? Caption(captioned.Groups[1].Value, context) : null;
        if (Svg().Match(html) is { Success: true } svg)
        {
            if (svg.Length > MaximumSvgLength) return null;
            var title = SvgTitle().Match(svg.Value) is { Success: true } titled ? Alt(titled.Groups[1].Value) : string.Empty;
            return new(null, svg.Value, title, caption);
        }

        if (Image().Match(html) is not { Success: true } image) return null;
        if (SourceAttribute().Match(image.Value) is not { Success: true } source || SiteImage().Match(source.Groups[1].Value) is not { Success: true } named
            || !context.HasImage(named.Groups[1].Value)) return null;
        return new(named.Groups[1].Value, null, Alt(AltAttribute().Match(image.Value) is { Success: true } alt ? alt.Groups[1].Value : string.Empty), caption);
    }

    // A line of text: its templates, and the targets of its links outside the code it quotes.
    private static string ExpandLine(string line, Context context)
    {
        if (line.Length == 0) return line;
        if (!line.Contains('`')) return ExpandTemplates(RewriteLinks(line, context), context);
        var builder = new StringBuilder(line.Length + 16);
        var position = 0;
        foreach (Match span in CodeSpan().Matches(line))
        {
            builder.Append(RewriteLinks(line[position..span.Index], context)).Append(span.Value);
            position = span.Index + span.Length;
        }

        builder.Append(RewriteLinks(line[position..], context));
        return ExpandTemplates(builder.ToString(), context);
    }

    private static string RewriteLinks(string text, Context context)
    {
        if (text.Length == 0) return text;
        text = MarkdownTarget().Replace(text, match => RewriteTarget(match.Value, context));
        return HtmlTarget().Replace(text, match => RewriteTarget(match.Value, context));
    }

    // The target of a link: a page of the guide is named from the folder of the guide, whatever the page that links to it.
    private static string RewriteTarget(string target, Context context)
    {
        if (SiteAddress().Match(target) is { Success: true, Index: 0 } site && site.Length == target.Length) return SitePage(site.Groups[1].Value, context);
        if (target.Length == 0 || target[0] == '/' || Scheme().IsMatch(target) || target.Contains("{{", StringComparison.Ordinal)) return target;
        var hash = target.IndexOf('#');
        var path = hash < 0 ? target : target[..hash];
        var fragment = hash < 0 ? string.Empty : target[hash..];
        if (path.Length == 0) return context.PagePath + fragment;
        var slash = context.PagePath.LastIndexOf('/');
        var segments = new List<string>(slash < 0 ? [] : context.PagePath[..slash].Split('/'));
        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment != "..") { segments.Add(segment); continue; }
            // A path that leaves the guide names no page of it.
            if (segments.Count == 0) return target;
            segments.RemoveAt(segments.Count - 1);
        }

        return context.ResolvePage(string.Join('/', segments)) is { } page ? page + fragment : target;
    }

    // "/docs/plugins/#x" of the site is the page "plugins/readme.md#x" of the guide; what is no shipped page stays an address of the site.
    private static string SitePage(string address, Context context)
    {
        var hash = address.IndexOf('#');
        var path = hash < 0 ? address : address[..hash];
        var fragment = hash < 0 ? string.Empty : address[hash..];
        if (path == "/docs" || path.StartsWith("/docs/", StringComparison.Ordinal))
        {
            var inside = path.Length <= 6 ? string.Empty : path[6..].TrimEnd('/');
            var page = inside.Length == 0 ? context.ResolvePage(ShippedDocumentation.HomePage)
                : context.ResolvePage(inside + ".md") ?? context.ResolvePage(inside + "/" + ShippedDocumentation.HomePage) ?? (inside.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? context.ResolvePage(inside) : null);
            if (page is not null) return page + fragment;
        }

        return WebSite + address;
    }

    private static string ExpandTemplates(string line, Context context)
    {
        if (!line.Contains("{{", StringComparison.Ordinal)) return line;
        line = Mark().Replace(line, static match => match.Groups[1].Value switch { "yes" => "✓", "part" => "◐", _ => "–" });
        return SiteAddress().Replace(line, match => SitePage(match.Groups[1].Value, context));
    }

    private sealed record Context(string PagePath, Func<string, string?> ResolvePage, Func<string, bool> HasImage);

    // Whether the lines read so far are inside a fenced code block.
    private struct Fence
    {
        private char _mark;
        private int _length;

        public readonly bool Inside => _length > 0;

        /// <summary>Reads a line; true when the line opens or closes a fence.</summary>
        public bool Step(string line)
        {
            if (FenceLine().Match(line) is not { Success: true } match) return false;
            var run = match.Groups[1].Value;
            if (_length == 0)
            {
                // The info string of a fence of backticks has no backtick.
                if (run[0] == '`' && match.Groups[2].Value.Contains('`')) return false;
                (_mark, _length) = (run[0], run.Length);
                return true;
            }

            if (run[0] != _mark || run.Length < _length || match.Groups[2].Value.Trim().Length > 0) return false;
            _length = 0;
            return true;
        }
    }

    [GeneratedRegex(@"^title\s*:\s*(.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex FrontMatterTitle();

    [GeneratedRegex(@"^ {0,3}(#{1,6})[ \t]+(.*?)(?:[ \t]+#+)?[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex FenceLine();

    [GeneratedRegex(@"^\s*\{\{\s*alta_shot\s+""([^""]*)""\s+""([^""]*)""\s+""([^""]*)""\s+""([^""]*)""\s*\}\}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Screenshot();

    // A figure starts its line, as the pages write it: a page that only names the tag in a sentence has no figure there.
    [GeneratedRegex(@"(?<=^\s*)<figure\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FigureOpen();

    [GeneratedRegex(@"<figcaption\b[^>]*>(.*?)</figcaption>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex FigureCaption();

    [GeneratedRegex(@"<svg\b.*?</svg>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Svg();

    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SvgTitle();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Image();

    [GeneratedRegex(@"^\{\{\s*site\.basepath\s*\}\}/img/([A-Za-z0-9][A-Za-z0-9._-]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex SiteImage();

    [GeneratedRegex(@"\bsrc\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourceAttribute();

    [GeneratedRegex(@"\balt\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AltAttribute();

    [GeneratedRegex(@"^\s*<style\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StyleOpen();

    [GeneratedRegex(@"^\s*</?div\b[^>]*>\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LayoutLine();

    [GeneratedRegex(@"(?<!`)(`+)(?!`).+?(?<!`)\1(?!`)", RegexOptions.CultureInvariant)]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"(?<=\]\()[^()\s]+(?=[)\s])", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownTarget();

    [GeneratedRegex(@"(?<=\bhref="")[^""]*(?="")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTarget();

    [GeneratedRegex(@"\{\{\s*site\.basepath\s*\}\}((?:/[^\s)""'<>]*)?)", RegexOptions.CultureInvariant)]
    private static partial Regex SiteAddress();

    [GeneratedRegex(@"\{\{\s*alta_(yes|part|no)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Mark();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:", RegexOptions.CultureInvariant)]
    private static partial Regex Scheme();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"^\s*(?:#{1,6}\s+|[-*+]\s+|\d+\.\s+|>\s*)+", RegexOptions.CultureInvariant)]
    private static partial Regex LineMark();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
