using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeAlta.Browser.Annotations;

/// <summary>
/// What may leave a page in the text of an annotation. The prompt is kept in the session and shown in its timeline, so
/// only the attributes that say what an element is go into it, an address loses its query and fragment (where tokens
/// travel), and a value that looks like a credential is replaced by <see cref="Redacted"/>. The toolbar applies the same
/// rules; the host applies them again to what it receives, which it does not trust.
/// </summary>
internal static partial class AnnotationRedaction
{
    /// <summary>The text that replaces a value that may be a secret.</summary>
    public const string Redacted = "[redacted]";

    // The most attributes kept on one tag, and the longest value of one.
    private const int MaximumAttributes = 30;
    private const int MaximumAttribute = 300;

    // The attributes kept in the HTML of an element, with every aria-* one. Of the data-* attributes, which a page may fill
    // with anything, only the hooks of tests are kept: they name an element, and its source can be found by them.
    private static readonly FrozenSet<string> SafeAttributes = FrozenSet.Create(StringComparer.Ordinal,
        "id", "class", "name", "type", "role", "href", "src", "alt", "title", "placeholder", "for", "action", "method",
        "data-testid", "data-test", "data-test-id", "data-cy", "data-qa");

    // Attributes that name an element rather than carry a value: name="password" or class="api-key" says what a field is,
    // and is kept.
    private static readonly FrozenSet<string> NamingAttributes = FrozenSet.Create(StringComparer.Ordinal,
        "id", "class", "name", "type", "role", "for", "method", "data-testid", "data-test", "data-test-id", "data-cy", "data-qa");

    private static readonly FrozenSet<string> UrlAttributes = FrozenSet.Create(StringComparer.Ordinal, "href", "src", "action");

    private static readonly FrozenSet<string> VoidTags = FrozenSet.Create(StringComparer.Ordinal,
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr");

    // Elements whose content is code or hidden markup, never what the element shows.
    private static readonly FrozenSet<string> OpaqueTags = FrozenSet.Create(StringComparer.Ordinal, "script", "style", "noscript", "template");

    [GeneratedRegex(@"access[_-]?token|auth[_-]?token|refresh[_-]?token|id[_-]?token|api[_-]?key|apikey|client[_-]?secret|oauth[_-]?state|x-amz-|session[_-]?id|sessionid|csrf|xsrf|secret|password|passwd|bearer\s",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex SecretPattern();

    // A value written after the name of a credential in a text, such as "token=…", "Password: hunter2" or "Authorization:
    // Bearer …", or after "Bearer": it goes whatever it looks like, since a weak password is a password all the same.
    [GeneratedRegex(@"((?:access[_-]?token|auth[_-]?token|refresh[_-]?token|id[_-]?token|session[_-]?token|api[_-]?key|apikey|client[_-]?secret|session[_-]?id|sessionid|csrf[_-]?token|xsrf[_-]?token|authorization|auth|secret|password|passwd|pwd|token|session|cookie|bearer)[""']?\s*[:=]\s*[""']?(?:(?:bearer|basic)\s+)?|\bbearer\s+)([^\s""'&<>,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex SecretValue();

    [GeneratedRegex(@"\b((?:https?|file)://[^\s?#""'<>]*)[?#][^\s""'<>]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex UrlInText();

    [GeneratedRegex(@"^([a-zA-Z][a-zA-Z0-9+.-]*):", RegexOptions.CultureInvariant)]
    private static partial Regex Scheme();

    [GeneratedRegex(@"^#[\w-]{1,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex Anchor();

    /// <summary>Whether a value holds what looks like the name of a credential.</summary>
    public static bool LooksSecret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            return SecretPattern().IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    /// <summary>
    /// A text of the page on one line: its whitespace collapsed, its addresses without query and fragment, and the values
    /// of credentials written in it replaced.
    /// </summary>
    public static string Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var line = OneLine(text);
        try
        {
            line = UrlInText().Replace(line, "$1");
            return SecretValue().Replace(line, "$1" + Redacted);
        }
        catch (RegexMatchTimeoutException)
        {
            return Redacted;
        }
    }

    /// <summary>
    /// An address in an attribute without its query and fragment; of another scheme than http, https or file only the
    /// scheme is kept (<c>javascript:…</c>, <c>data:…</c>, <c>mailto:…</c>). A link to an anchor of the page keeps it.
    /// </summary>
    public static string Url(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.Trim();
        if (Anchor().IsMatch(text) && !LooksSecret(text)) return text;
        if (Scheme().Match(text) is { Success: true } scheme && scheme.Groups[1].Value.ToLowerInvariant() is var name && name is not ("http" or "https" or "file")) return name + ":…";
        var cut = text.IndexOfAny(['?', '#']);
        return cut < 0 ? text : text[..cut];
    }

    /// <summary>
    /// The address of a page as a prompt shows it: without its query and fragment, where tokens travel, and with <c>?…</c>
    /// or <c>#…</c> when it had one. The browser tools still show the whole address of the tab.
    /// </summary>
    public static string PageAddress(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri) return Url(url.OriginalString);
        var address = url.GetLeftPart(UriPartial.Path);
        return url.Query.Length > 1 ? address + "?…" : url.Fragment.Length > 1 ? address + "#…" : address;
    }

    /// <summary>The value an attribute keeps, or null when the attribute is not one a prompt may show.</summary>
    public static string? Attribute(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        if (!IsSafeAttribute(name)) return null;
        var text = value.Length > 4 * MaximumAttribute ? value[..(4 * MaximumAttribute)] : value;
        if (UrlAttributes.Contains(name)) text = Url(text);
        if (!NamingAttributes.Contains(name) && LooksSecret(text)) return Redacted;
        text = OneLine(text);
        return text.Length > MaximumAttribute ? text[..MaximumAttribute] + "…" : text;
    }

    private static bool IsSafeAttribute(string name)
    {
        if (SafeAttributes.Contains(name)) return true;
        if (!name.StartsWith("aria-", StringComparison.Ordinal) || name.Length == 5) return false;
        foreach (var character in name.AsSpan(5))
        {
            if (character is not (>= 'a' and <= 'z' or '-')) return false;
        }

        return true;
    }

    /// <summary>
    /// The HTML of an element as the toolbar reported it, filtered again: only the attributes a prompt may show, with their
    /// values made safe, no comments, no content of scripts and styles, at most <paramref name="limit"/> characters.
    /// </summary>
    public static string Html(string html, int limit)
    {
        ArgumentNullException.ThrowIfNull(html);
        var builder = new StringBuilder(Math.Min(html.Length, limit) + 16);
        var index = 0;
        while (index < html.Length && builder.Length < limit)
        {
            var open = html.IndexOf('<', index);
            if (open < 0)
            {
                AppendText(builder, html.AsSpan(index));
                break;
            }

            AppendText(builder, html.AsSpan(index, open - index));
            if (html.AsSpan(open).StartsWith("<!--", StringComparison.Ordinal))
            {
                var end = html.IndexOf("-->", open + 4, StringComparison.Ordinal);
                index = end < 0 ? html.Length : end + 3;
                continue;
            }

            var closing = open + 1 < html.Length && html[open + 1] == '/';
            var nameStart = open + (closing ? 2 : 1);
            var nameEnd = nameStart;
            while (nameEnd < html.Length && (char.IsAsciiLetterOrDigit(html[nameEnd]) || html[nameEnd] is '-' or ':')) nameEnd++;
            if (nameEnd == nameStart || !char.IsAsciiLetter(html[nameStart]))
            {
                // A "<" that starts no tag is text.
                builder.Append("&lt;");
                index = open + 1;
                continue;
            }

            var tag = html[nameStart..nameEnd].ToLowerInvariant();
            if (closing)
            {
                var end = html.IndexOf('>', nameEnd);
                index = end < 0 ? html.Length : end + 1;
                builder.Append("</").Append(tag).Append('>');
                continue;
            }

            builder.Append('<').Append(tag);
            index = ReadAttributes(html, nameEnd, builder, out var selfClosing);
            builder.Append('>');
            if (OpaqueTags.Contains(tag) && !selfClosing)
            {
                // What a script or a style holds is left out, up to its end tag.
                var end = html.IndexOf("</" + tag, index, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                {
                    index = html.Length;
                }
                else
                {
                    var close = html.IndexOf('>', end);
                    index = close < 0 ? html.Length : close + 1;
                }

                builder.Append("</").Append(tag).Append('>');
            }
            else if (selfClosing && !VoidTags.Contains(tag))
            {
                builder.Append("</").Append(tag).Append('>');
            }
        }

        if (builder.Length > limit) return builder.ToString(0, limit) + "…";
        return builder.ToString();
    }

    // Reads the attributes of a tag from after its name to its end, and writes those a prompt may show.
    private static int ReadAttributes(string html, int index, StringBuilder builder, out bool selfClosing)
    {
        selfClosing = false;
        var kept = 0;
        while (index < html.Length)
        {
            while (index < html.Length && char.IsWhiteSpace(html[index])) index++;
            if (index >= html.Length) break;
            if (html[index] == '>') return index + 1;
            if (html[index] == '/')
            {
                selfClosing = index + 1 < html.Length && html[index + 1] == '>';
                index++;
                continue;
            }

            var nameStart = index;
            while (index < html.Length && !char.IsWhiteSpace(html[index]) && html[index] is not ('=' or '>' or '/')) index++;
            var name = html[nameStart..index].ToLowerInvariant();
            while (index < html.Length && char.IsWhiteSpace(html[index])) index++;
            var value = string.Empty;
            if (index < html.Length && html[index] == '=')
            {
                index++;
                while (index < html.Length && char.IsWhiteSpace(html[index])) index++;
                if (index < html.Length && html[index] is '"' or '\'')
                {
                    var quote = html[index];
                    var end = html.IndexOf(quote, index + 1);
                    if (end < 0) end = html.Length;
                    value = html[(index + 1)..end];
                    index = Math.Min(html.Length, end + 1);
                }
                else
                {
                    var valueStart = index;
                    while (index < html.Length && !char.IsWhiteSpace(html[index]) && html[index] != '>') index++;
                    value = html[valueStart..index];
                }
            }

            if (kept == MaximumAttributes || Attribute(name, WebUtility.HtmlDecode(value)) is not { } safe) continue;
            kept++;
            builder.Append(' ').Append(name).Append("=\"").Append(Escape(safe, quote: true)).Append('"');
        }

        return index;
    }

    // The text between tags: its addresses and credentials made safe, a stray ">" kept as an entity.
    private static void AppendText(StringBuilder builder, ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return;
        var decoded = WebUtility.HtmlDecode(text.ToString());
        string safe;
        try
        {
            safe = SecretValue().Replace(UrlInText().Replace(decoded, "$1"), "$1" + Redacted);
        }
        catch (RegexMatchTimeoutException)
        {
            safe = Redacted;
        }

        builder.Append(Escape(safe, quote: false));
    }

    private static string Escape(string text, bool quote)
    {
        var escaped = text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
        return quote ? escaped.Replace("\"", "&quot;", StringComparison.Ordinal) : escaped;
    }

    /// <summary>A text on one line: every run of whitespace, line breaks included, becomes one space.</summary>
    public static string OneLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character) || character is (char)0x200B or (char)0x2028 or (char)0x2029 or (char)0xFEFF)
            {
                space = builder.Length != 0;
                continue;
            }

            if (space) builder.Append(' ');
            space = false;
            builder.Append(character);
        }

        return builder.ToString();
    }
}
