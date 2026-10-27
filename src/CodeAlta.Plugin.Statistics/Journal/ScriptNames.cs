namespace CodeAlta.Plugin.Statistics.Journal;

/// <summary>
/// The names the statistics keep of the runs of scripts: the name of a library script, the id of a step, the name of an error.
/// A value is kept only when it is written as an identifier; anything else (a sentence, a path, a value) is text of a script and
/// is not kept.
/// </summary>
internal static class ScriptNames
{
    private const int MaxLength = 64;
    private const int MaxErrorLength = 48;

    /// <summary>Tells whether a text is an identifier: letters, digits, <c>_</c>, <c>-</c> and <c>.</c>, starting with a letter or a digit, at most 64.</summary>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> for an identifier.</returns>
    public static bool IsIdentifier(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty || text.Length > MaxLength || !char.IsAsciiLetterOrDigit(text[0]))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Gets the id of a step, or a status, when it is written as an identifier.</summary>
    /// <param name="text">The text as written.</param>
    /// <returns>The identifier; null for anything else.</returns>
    public static string? StepOf(string? text) => text is not null && IsIdentifier(text) ? text : null;

    /// <summary>Gets the name of a library script, as the library writes them: lowercase letters, digits and hyphens, starting with a letter.</summary>
    /// <param name="text">The text as written.</param>
    /// <returns>The name; null for anything else.</returns>
    public static string? ScriptOf(string? text)
    {
        if (text is not { Length: > 0 and <= MaxLength } || !char.IsAsciiLetterLower(text[0]))
        {
            return null;
        }

        foreach (var c in text)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
            {
                return null;
            }
        }

        return text;
    }

    /// <summary>Gets the name of the error a message of a failed run starts with (<c>TypeError: …</c> gives <c>TypeError</c>).</summary>
    /// <param name="message">The message as written.</param>
    /// <returns>The name; null when the message does not start with one.</returns>
    public static string? ErrorOf(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        // The registry writes `<name>: <message>`: a text without the separator is no name.
        var colon = message.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return null;
        }

        var name = message.AsSpan(0, colon);
        if (name.Length > MaxErrorLength || name.Length > MaxErrorLength || !char.IsAsciiLetter(name[0]))
        {
            return null;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return null;
            }
        }

        return name.ToString();
    }
}
