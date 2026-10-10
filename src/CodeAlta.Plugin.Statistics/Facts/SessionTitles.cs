using System.Text;

namespace CodeAlta.Plugin.Statistics.Facts;

/// <summary>
/// The form the title of a session is kept in. A title is made by the application from the start of the first prompt of the
/// session, so it is text the user wrote: the statistics keep it to name the session in a table, short and without the paths
/// it names.
/// </summary>
internal static class SessionTitles
{
    /// <summary>The most characters of a title that are kept: what the application gives a title when it makes one.</summary>
    public const int MaxLength = 80;

    private const char LeftOut = '…';

    /// <summary>
    /// Gets the form of a title that is kept: its words separated by one space, a word that holds a <c>/</c> or a <c>\</c> (a path,
    /// an address) replaced by an ellipsis, cut to <see cref="MaxLength"/> characters.
    /// </summary>
    /// <param name="title">The title the header of the session gives.</param>
    /// <returns>The title to keep; null when there is none, or when nothing of it is left.</returns>
    public static string? Clean(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(title.Length, MaxLength + 1));
        var kept = false;
        var index = 0;
        while (index < title.Length && builder.Length <= MaxLength)
        {
            while (index < title.Length && IsSeparator(title[index]))
            {
                index++;
            }

            var start = index;
            while (index < title.Length && !IsSeparator(title[index]))
            {
                index++;
            }

            if (index == start)
            {
                break;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            var word = title.AsSpan(start, index - start);
            if (word.ContainsAny('/', '\\'))
            {
                builder.Append(LeftOut);
            }
            else
            {
                builder.Append(word);
                kept = true;
            }
        }

        if (!kept)
        {
            return null;
        }

        if (builder.Length > MaxLength)
        {
            // Not in the middle of a character that takes two.
            var length = MaxLength - 1;
            if (char.IsHighSurrogate(builder[length - 1]))
            {
                length--;
            }

            builder.Length = length;
            while (builder.Length > 0 && builder[^1] == ' ')
            {
                builder.Length--;
            }

            builder.Append(LeftOut);
        }

        return builder.ToString();
    }

    // What separates two words: the white space of a text, and the control characters, which are no part of a word.
    private static bool IsSeparator(char value) => char.IsWhiteSpace(value) || char.IsControl(value);
}
