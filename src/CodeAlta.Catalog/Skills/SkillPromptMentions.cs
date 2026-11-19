using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace CodeAlta.Catalog.Skills;

/// <summary>A skill a prompt names as <c>/name</c>.</summary>
/// <param name="Start">The index of the slash in the prompt.</param>
/// <param name="Length">The length of the slash and the name.</param>
/// <param name="Name">The name, as written.</param>
public readonly record struct SkillPromptMention(int Start, int Length, string Name);

/// <summary>
/// Finds the skills a prompt names as <c>/name</c>, as the composer of CodeAlta Desktop inserts them: a slash at the
/// start of the prompt, after white space or an opening bracket, then the name, ended by white space, a punctuation mark
/// or the end of the text. A path such as <c>src/app</c> or <c>/usr/bin</c> names nothing.
/// </summary>
/// <remarks>
/// <para>
/// A prompt is Markdown (CommonMark), and only its plain text names a skill: what the timeline shows as code (a fenced or
/// indented block, a <c>`code span`</c>), a link (its text, its address, a reference definition), an image or HTML names
/// nothing. The catalog decides which names are skills, and the others stay text.
/// </para>
/// <para>
/// A name is made of letters and decimal digits of any script, joined by <c>-</c>, <c>_</c>, <c>.</c> or <c>:</c>
/// (<c>code-review</c>, <c>plugin:skill</c>); it starts and ends with a letter or a digit. The page reads the same
/// names in a rendered message (<c>skillMentions.ts</c>).
/// </para>
/// </remarks>
public static class SkillPromptMentions
{
    /// <summary>The longest name a mention can have: a skill name has at most 64 characters, one of a plugin more.</summary>
    public const int MaximumNameLength = 128;

    // CommonMark alone, with the positions of each inline in the text: an immutable pipeline, shared by every read.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePreciseSourceLocation().Build();

    /// <summary>
    /// Gets the names a prompt mentions, in the order of their first mention, each once whatever its case.
    /// </summary>
    /// <param name="text">The prompt text.</param>
    /// <returns>The mentioned names, possibly empty.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> Find(string text)
    {
        var mentions = FindMentions(text);
        if (mentions.Count == 0) return [];
        var names = new List<string>(mentions.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mention in mentions)
        {
            if (seen.Add(mention.Name)) names.Add(mention.Name);
        }

        return names;
    }

    /// <summary>Gets every mention of a prompt with its place in the text, in order.</summary>
    /// <param name="text">The prompt text.</param>
    /// <returns>The mentions, possibly empty.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<SkillPromptMention> FindMentions(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains('/', StringComparison.Ordinal)) return [];
        var plain = PlainText(text);
        var mentions = new List<SkillPromptMention>();
        for (var at = plain.IndexOf('/'); at >= 0; at = at + 1 < plain.Length ? plain.IndexOf('/', at + 1) : -1)
        {
            if (at > 0 && !char.IsWhiteSpace(plain[at - 1]) && plain[at - 1] is not ('(' or '[' or '{')) continue;
            var length = NameLength(plain.AsSpan(at + 1));
            if (length is 0 or > MaximumNameLength) continue;
            mentions.Add(new(at, 1 + length, text.Substring(at + 1, length)));
            at += length;
        }

        return mentions;
    }

    // The length of the name a text starts with, 0 for none: the longest run of letters, digits and joiners that starts
    // and ends with a letter or a digit, when white space, a punctuation mark or the end of the text follows it.
    private static int NameLength(ReadOnlySpan<char> text)
    {
        var length = 0;
        for (var at = 0; at < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text[at..], out var rune, out var consumed) == System.Buffers.OperationStatus.Done && Rune.IsLetterOrDigit(rune))
            {
                at += consumed;
                length = at;
            }
            else if (at > 0 && text[at] is '-' or '_' or '.' or ':')
            {
                at++;
            }
            else
            {
                break;
            }
        }

        if (length == 0 || length == text.Length) return length;
        var next = text[length];
        return char.IsWhiteSpace(next) || next is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '"' or '\'' ? length : 0;
    }

    /// <summary>
    /// Says whether the slash typed at <paramref name="offset"/> can begin the name of a skill, for the picker the composer
    /// opens on it: it is in the plain text of the prompt as typed up to it. It cannot in code (a fenced or indented block,
    /// a code span, also one whose closing backtick is not typed yet), in a link, in HTML, or after a backslash.
    /// </summary>
    /// <param name="text">The prompt text.</param>
    /// <param name="offset">The index of the slash in <paramref name="text"/>.</param>
    /// <returns><see langword="true"/> when a skill can be named there; <see langword="false"/> otherwise, also when no slash is there.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is <see langword="null"/>.</exception>
    public static bool CanBeginAt(string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (offset < 0 || offset >= text.Length || text[offset] != '/' || Escaped(text, offset)) return false;
        // The address of a link being typed, whose closing parenthesis is not there yet: `[text](/`.
        if (offset >= 2 && text[offset - 1] == '(' && text[offset - 2] == ']') return false;
        // What follows the slash is not typed yet, or is what the picker replaces: only the text before it says where it is.
        var typed = text[..(offset + 1)];
        var literals = Literals(typed);
        var block = literals.FirstOrDefault(literal => literal.Span.Start <= offset && offset <= literal.Span.End).Block;
        if (block is null) return false;
        // A backtick that is text in the block of the slash opens a code span that is not closed yet.
        foreach (var (owner, span) in literals)
        {
            if (!ReferenceEquals(owner, block)) continue;
            for (var at = Math.Max(span.Start, 0); at <= Math.Min(span.End, offset); at++)
            {
                if (typed[at] == '`' && !Escaped(typed, at)) return false;
            }
        }

        return true;
    }

    // Whether an odd number of backslashes precedes a character: it is escaped.
    private static bool Escaped(string text, int at)
    {
        var slashes = 0;
        while (at - slashes > 0 && text[at - slashes - 1] == '\\') slashes++;
        return slashes % 2 == 1;
    }

    // The text with all but its plain text hidden: each character becomes U+0000 unless it is white space or belongs to a
    // literal of the Markdown, so that the offsets stay those of `text` and no mention is found in code, a link or HTML.
    private static string PlainText(string text)
    {
        var plain = new char[text.Length];
        for (var at = 0; at < plain.Length; at++)
        {
            if (char.IsWhiteSpace(text[at])) plain[at] = text[at];
        }

        foreach (var (_, span) in Literals(text))
        {
            // An empty span ends before it starts.
            var end = Math.Min(span.End, text.Length - 1);
            for (var at = Math.Max(span.Start, 0); at <= end; at++) plain[at] = text[at];
        }

        return new string(plain);
    }

    // The literals of the Markdown of a text, each with its block and its place in the text, escapes included. What
    // Markdig does not report as a literal is not plain text: a syntax this does not know names no skill.
    private static List<(LeafBlock Block, SourceSpan Span)> Literals(string text)
    {
        var literals = new List<(LeafBlock Block, SourceSpan Span)>();
        MarkdownDocument document;
        try
        {
            document = Markdown.Parse(text, Pipeline);
        }
        // Markdig refuses a text whose blocks or inlines nest too deep (thousands of `>` or `[`): it names no skill.
        catch (ArgumentException)
        {
            return literals;
        }

        // Only a leaf block that reads inlines has any (a paragraph, a heading); code and HTML blocks have none.
        var pending = new Stack<ContainerInline>();
        foreach (var leaf in document.Descendants<LeafBlock>())
        {
            if (leaf.Inline is null) continue;
            pending.Push(leaf.Inline);
            while (pending.TryPop(out var container))
            {
                for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
                {
                    switch (inline)
                    {
                        case LiteralInline literal:
                            literals.Add((leaf, literal.Span));
                            break;
                        // A link or an image: the timeline shows its text as a link, never as a skill.
                        case LinkInline:
                            break;
                        // A delimiter left without its pair (`[`, `*`) is text, and so is what follows it.
                        case DelimiterInline delimiter:
                            literals.Add((leaf, delimiter.Span));
                            pending.Push(delimiter);
                            break;
                        case ContainerInline nested:
                            pending.Push(nested);
                            break;
                    }
                }
            }
        }

        return literals;
    }
}
