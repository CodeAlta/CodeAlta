using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Search;

/// <summary>The text work of the index: chunks, split identifiers, hashes, the FTS5 query and snippets.</summary>
public static class SearchText
{
    /// <summary>The longest chunk, in characters: about 350 tokens.</summary>
    public const int ChunkCharacters = 1400;

    /// <summary>The most chunks of one item; what follows is not indexed.</summary>
    public const int MaximumChunks = 24;

    /// <summary>The start of a matched word in a snippet of SQLite.</summary>
    public const char MarkStart = '\u0001';

    /// <summary>The end of a matched word in a snippet of SQLite.</summary>
    public const char MarkEnd = '\u0002';

    private const int MaximumWordsCharacters = 4096;

    // Words that carry no meaning of their own in a query: left out of the words that must all match.
    private static readonly FrozenSet<string> StopWords = new[]
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "can", "did", "do", "does", "for", "from", "had", "has", "have", "how",
        "i", "if", "in", "into", "is", "it", "its", "me", "my", "of", "on", "or", "our", "so", "that", "the", "their", "then", "there",
        "these", "this", "to", "was", "we", "were", "what", "when", "where", "which", "who", "why", "will", "with", "you", "your",
        "au", "aux", "avec", "ce", "ces", "comment", "dans", "de", "des", "du", "elle", "en", "est", "et", "il", "je", "la", "le", "les",
        "leur", "mais", "ne", "nous", "on", "ou", "par", "pas", "pour", "pourquoi", "qu", "que", "qui", "quoi", "sa", "se", "ses", "son",
        "sur", "un", "une", "vous", "été", "avons", "avez", "ont",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cuts a text into chunks at paragraphs, keeping fenced code blocks whole when they fit.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The chunks, at most <see cref="MaximumChunks"/>; one empty chunk for an empty text.</returns>
    public static IReadOnlyList<string> Chunk(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        if (text.Length <= ChunkCharacters) return [text];
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var block in Blocks(text))
        {
            if (chunks.Count >= MaximumChunks) break;
            if (current.Length > 0 && current.Length + block.Length + 2 > ChunkCharacters)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }

            if (block.Length <= ChunkCharacters)
            {
                if (current.Length > 0) current.Append("\n\n");
                current.Append(block);
                continue;
            }

            // A block longer than a chunk is cut at its lines, then within a line when one is longer still.
            foreach (var piece in Split(block))
            {
                if (chunks.Count >= MaximumChunks) break;
                if (current.Length > 0 && current.Length + piece.Length + 1 > ChunkCharacters)
                {
                    chunks.Add(current.ToString().Trim());
                    current.Clear();
                }

                if (current.Length > 0) current.Append('\n');
                current.Append(piece);
            }
        }

        if (current.Length > 0 && chunks.Count < MaximumChunks) chunks.Add(current.ToString().Trim());
        return chunks;
    }

    private static IEnumerable<string> Blocks(string text)
    {
        var lines = text.Split('\n');
        var block = new StringBuilder();
        var inFence = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
            if (!inFence && line.Trim().Length == 0)
            {
                if (block.Length > 0)
                {
                    yield return block.ToString();
                    block.Clear();
                }

                continue;
            }

            if (block.Length > 0) block.Append('\n');
            block.Append(line);
        }

        if (block.Length > 0) yield return block.ToString();
    }

    private static IEnumerable<string> Split(string block)
    {
        foreach (var line in block.Split('\n'))
        {
            for (var start = 0; start < line.Length; start += ChunkCharacters)
            {
                yield return line.Substring(start, Math.Min(ChunkCharacters, line.Length - start));
            }
        }
    }

    /// <summary>
    /// Gets the parts of the identifiers of a text, lowercase and space-separated: <c>SessionJournalSqliteCache</c> gives
    /// <c>session journal sqlite cache</c>, <c>snake_case</c> and <c>a/b/c.cs</c> their parts. Words of one part are left out.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The parts, at most 4096 characters.</returns>
    public static string SplitIdentifiers(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var output = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parts = new List<string>();
        var index = 0;
        while (index < text.Length && output.Length < MaximumWordsCharacters)
        {
            if (!IsIdentifierCharacter(text[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < text.Length && (IsIdentifierCharacter(text[index]) || text[index] is '.' or '/' or '\\' or '-' or ':')) index++;
            var token = text.AsSpan(start, index - start).TrimEnd(".:-/\\");
            if (token.Length < 4) continue;
            parts.Clear();
            SplitToken(token, parts);
            if (parts.Count < 2) continue;
            var whole = string.Join(' ', parts);
            if (!seen.Add(whole)) continue;
            if (output.Length > 0) output.Append(' ');
            output.Append(whole);
        }

        return output.Length > MaximumWordsCharacters ? output.ToString(0, MaximumWordsCharacters) : output.ToString();
    }

    private static bool IsIdentifierCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

    private static void SplitToken(ReadOnlySpan<char> token, List<string> parts)
    {
        var i = 0;
        while (i < token.Length)
        {
            while (i < token.Length && !char.IsLetterOrDigit(token[i])) i++;
            if (i >= token.Length) break;
            var start = i++;
            while (i < token.Length && char.IsLetterOrDigit(token[i]))
            {
                char previous = token[i - 1], current = token[i];
                if (char.IsUpper(current) && char.IsLower(previous)) break;
                // An acronym before a word: HTTPServer gives http and server.
                if (char.IsUpper(current) && char.IsUpper(previous) && i + 1 < token.Length && char.IsLower(token[i + 1])) break;
                if (char.IsDigit(current) != char.IsDigit(previous)) break;
                i++;
            }

            parts.Add(token[start..i].ToString().ToLowerInvariant());
        }
    }

    /// <summary>Gets the hash of a text that names it in the index: the first 16 bytes of its SHA-256 in UTF-8.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The hash.</returns>
    public static byte[] Hash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Span<byte> full = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), full);
        return full[..16].ToArray();
    }

    /// <summary>Gets the words of a query: its phrases in quotes and its other words, as typed.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The terms; a phrase keeps its spaces.</returns>
    public static IReadOnlyList<string> Terms(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var terms = new List<string>();
        var index = 0;
        while (index < query.Length)
        {
            if (char.IsWhiteSpace(query[index]))
            {
                index++;
                continue;
            }

            if (query[index] == '"')
            {
                var end = query.IndexOf('"', index + 1);
                if (end < 0) end = query.Length;
                var phrase = query[(index + 1)..end].Trim();
                if (phrase.Length > 0) terms.Add(phrase);
                index = end + 1;
                continue;
            }

            var start = index;
            while (index < query.Length && !char.IsWhiteSpace(query[index])) index++;
            var word = query[start..index].Trim('"', '(', ')', '\'', ',', ';', '?', '!');
            if (word.Length > 0) terms.Add(word);
        }

        return terms;
    }

    /// <summary>
    /// Builds the FTS5 query of a text: every term quoted, so that nothing of the text is read as an operator. An
    /// identifier also matches its parts in the <c>words</c> column.
    /// </summary>
    /// <param name="query">The query, as typed.</param>
    /// <param name="any">True to match any term; false to require every term but the common words.</param>
    /// <param name="prefixLastTerm">True when the last term is being typed and matches as a prefix.</param>
    /// <returns>The FTS5 expression, or null when the query has no term.</returns>
    public static string? BuildMatch(string query, bool any, bool prefixLastTerm)
    {
        var terms = Terms(query).Where(static term => term.Any(char.IsLetterOrDigit)).ToList();
        if (terms.Count == 0) return null;
        var required = any ? terms : terms.Where(term => term.Contains(' ') || !StopWords.Contains(term)).ToList();
        if (required.Count == 0) required = terms;
        var expressions = new List<string>(required.Count);
        for (var i = 0; i < required.Count; i++)
        {
            var term = required[i];
            var last = prefixLastTerm && ReferenceEquals(term, terms[^1]) && !term.Contains(' ');
            var quoted = Quote(term) + (last ? "*" : "");
            var parts = new List<string>();
            SplitToken(term.AsSpan(), parts);
            expressions.Add(parts.Count >= 2 && !term.Contains(' ')
                ? $"({quoted} OR words : {Quote(string.Join(' ', parts))})"
                : quoted);
        }

        return string.Join(any ? " OR " : " AND ", expressions);
    }

    private static string Quote(string term) => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>Reads a snippet of SQLite: its text without the markers, and where they were.</summary>
    /// <param name="snippet">The snippet with <see cref="MarkStart"/> and <see cref="MarkEnd"/> around matched words.</param>
    /// <returns>The text and its marks.</returns>
    public static (string Text, IReadOnlyList<SearchMark> Marks) ReadSnippet(string snippet)
    {
        ArgumentNullException.ThrowIfNull(snippet);
        var text = new StringBuilder(snippet.Length);
        var marks = new List<SearchMark>();
        var start = -1;
        foreach (var character in snippet)
        {
            if (character == MarkStart)
            {
                start = text.Length;
            }
            else if (character == MarkEnd)
            {
                if (start >= 0 && text.Length > start) marks.Add(new(start, text.Length - start));
                start = -1;
            }
            else
            {
                text.Append(character is '\n' or '\r' or '\t' ? ' ' : character);
            }
        }

        return (text.ToString(), marks);
    }

    /// <summary>Gets the part of a text shown for a hit found by meaning: the window with the most words of the query.</summary>
    /// <param name="text">The text of the chunk.</param>
    /// <param name="query">The query.</param>
    /// <param name="length">The length of the window.</param>
    /// <returns>The window, with an ellipsis where it was cut.</returns>
    public static string Window(string text, string query, int length = 200)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(query);
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= length) return flat;
        var words = Terms(query).Where(term => term.Length > 2 && !StopWords.Contains(term)).Select(static term => term.ToLowerInvariant()).ToArray();
        var lower = flat.ToLowerInvariant();
        var best = 0;
        var bestScore = -1;
        for (var start = 0; start < flat.Length - length / 2; start += length / 4)
        {
            var window = lower.AsSpan(start, Math.Min(length, flat.Length - start));
            var score = 0;
            foreach (var word in words)
            {
                if (window.Contains(word, StringComparison.Ordinal)) score++;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = start;
            }
        }

        // Start at a word.
        if (best > 0)
        {
            var space = flat.IndexOf(' ', best);
            if (space > 0 && space - best < 20) best = space + 1;
        }

        var end = Math.Min(flat.Length, best + length);
        return (best > 0 ? "…" : "") + flat[best..end] + (end < flat.Length ? "…" : "");
    }
}
