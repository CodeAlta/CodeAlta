using System.Collections.Frozen;

namespace CodeAlta.Pseudonymization.Presidio;

/// <summary>
/// The context of a match as Presidio's <c>LemmaContextAwareEnhancer</c> reads it by default: the five words before it
/// that are no stop words, in which a word of context is found as a substring ("card" in "creditcard").
/// </summary>
public static class PresidioContext
{
    /// <summary>How many words before a match make its context, as Presidio's default prefix count.</summary>
    public const int Words = 5;

    // How far back the words are looked for: a long run of stop words or symbols ends the context.
    private const int Window = 400;

    // The words spaCy and the French tokenizers count as stop words, which Presidio skips; a short list of the commonest.
    private static readonly FrozenSet<string> StopWords = new[]
    {
        "a", "an", "the", "and", "or", "of", "to", "in", "on", "at", "by", "for", "with", "from", "as", "is", "are", "was", "were",
        "be", "been", "my", "your", "his", "her", "its", "our", "their", "this", "that", "these", "those", "it", "i", "you", "he",
        "she", "we", "they", "me", "him", "us", "them", "has", "have", "had", "do", "does", "did", "not", "no", "so", "if", "but",
        "here", "there", "which", "who", "what", "when", "where", "please", "le", "la", "les", "un", "une", "des", "du", "de", "d",
        "l", "et", "ou", "au", "aux", "en", "est", "son", "sa", "ses", "mon", "ma", "mes", "votre", "vos", "notre", "nos", "ce",
        "cette", "ces", "pour", "par", "sur", "avec", "dans", "il", "elle", "nous", "vous", "ils", "elles", "je", "tu", "qui", "que",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Tells whether a word of context is among the words before a match.</summary>
    /// <param name="text">The text.</param>
    /// <param name="start">Where the match starts.</param>
    /// <param name="context">The words of context, lowercase.</param>
    /// <returns><see langword="true"/> when one is found.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="context"/> is <see langword="null"/>.</exception>
    public static bool Supports(string text, int start, IReadOnlyList<string> context)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(context);
        if (context.Count == 0) return false;
        var words = WordsBefore(text, start);
        if (words.Count == 0) return false;
        var joined = string.Join(' ', words);
        foreach (var word in context)
        {
            if (word.Contains(' ', StringComparison.Ordinal) ? joined.Contains(word, StringComparison.Ordinal) : words.Any(candidate => candidate.Contains(word, StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    /// <summary>Gets the words before a match that make its context, lowercase, in their order.</summary>
    /// <param name="text">The text.</param>
    /// <param name="start">Where the match starts.</param>
    /// <returns>At most <see cref="Words"/> words.</returns>
    public static IReadOnlyList<string> WordsBefore(string text, int start)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Read back from the match, word by word, until five words that are no stop words: the window is not copied.
        var limit = Math.Max(0, start - Window);
        var found = new List<string>(Words);
        for (var index = start; index > limit && found.Count < Words;)
        {
            while (index > limit && !IsWordCharacter(text[index - 1])) index--;
            var end = index;
            while (index > limit && IsWordCharacter(text[index - 1])) index--;
            // A word starts with a letter or a digit; the # and the apostrophes stay attached after it ("passport#", "driver's").
            var first = index;
            while (first < end && !char.IsLetterOrDigit(text[first])) first++;
            if (first == end) continue;
            var word = text[first..end].ToLowerInvariant();
            if (!StopWords.Contains(word)) found.Add(word);
        }

        found.Reverse();
        return found;
    }

    private static bool IsWordCharacter(char character) => char.IsLetterOrDigit(character) || character is '#' or '\'' or '’';
}
