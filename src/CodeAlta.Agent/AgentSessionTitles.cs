using System.Text;

namespace CodeAlta.Agent;

/// <summary>Who chose the title of a session.</summary>
public enum AgentSessionTitleSource
{
    /// <summary>CodeAlta chose it (the title a session is created with, or one it derived), and may replace it.</summary>
    Auto,

    /// <summary>A person or an agent chose it: it is never replaced automatically.</summary>
    User,
}

/// <summary>How CodeAlta titles a session nobody named.</summary>
public enum AgentSessionTitleMode
{
    /// <summary>From the first prompt of the session; the title then stays.</summary>
    FirstPrompt,

    /// <summary>From the first prompt, then from the first line of each assistant message.</summary>
    LastMessage,

    /// <summary>From the first prompt, then from the <c>&lt;session_title&gt;</c> tag the model writes.</summary>
    Model,
}

/// <summary>The rules of session titles that every frontend and provider shares.</summary>
public static class AgentSessionTitles
{
    /// <summary>The mode of a session when neither the session nor the settings choose one.</summary>
    public const AgentSessionTitleMode DefaultMode = AgentSessionTitleMode.Model;

    /// <summary>The longest title CodeAlta derives, in UTF-16 units.</summary>
    public const int MaxDerivedLength = 80;

    private const string HostEnvelopeStart = "[CodeAlta ";

    /// <summary>Gets the name of a mode in the configuration file and in commands.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns><c>first_prompt</c>, <c>last_message</c> or <c>model</c>.</returns>
    public static string NameOf(AgentSessionTitleMode mode) => mode switch
    {
        AgentSessionTitleMode.FirstPrompt => "first_prompt",
        AgentSessionTitleMode.LastMessage => "last_message",
        _ => "model",
    };

    /// <summary>Gets the name of a title source in commands.</summary>
    /// <param name="source">The source, or <see langword="null" /> for a session written before sources were recorded.</param>
    /// <returns><c>auto</c>, <c>user</c> or <c>unset</c>.</returns>
    public static string NameOf(AgentSessionTitleSource? source) => source switch
    {
        AgentSessionTitleSource.Auto => "auto",
        AgentSessionTitleSource.User => "user",
        _ => "unset",
    };

    /// <summary>Reads the name of a mode.</summary>
    /// <param name="name">A name as <see cref="NameOf(AgentSessionTitleMode)" /> writes it; case and surrounding spaces are ignored.</param>
    /// <returns>The mode, or <see langword="null" /> for anything else.</returns>
    public static AgentSessionTitleMode? ParseMode(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "first_prompt" => AgentSessionTitleMode.FirstPrompt,
        "last_message" => AgentSessionTitleMode.LastMessage,
        "model" => AgentSessionTitleMode.Model,
        _ => null,
    };

    /// <summary>
    /// Whether a prompt may name a session: one a person or an agent wrote. A turn the provider started by itself
    /// and a message CodeAlta wrote (it starts with a <c>[CodeAlta ...]</c> line: a delegated-agent message, an
    /// update of a child session, the result of a background job) do not.
    /// </summary>
    /// <param name="text">The text of the prompt.</param>
    /// <param name="providerInitiated">Whether the provider started the turn by itself.</param>
    /// <returns><see langword="true" /> when the prompt may name the session.</returns>
    public static bool PromptNames(string? text, bool providerInitiated)
        => !providerInitiated && text is not null && !text.AsSpan().TrimStart().StartsWith(HostEnvelopeStart, StringComparison.Ordinal);

    /// <summary>
    /// Derives a short title from a prompt: its first paragraph without a heading or list marker, cut at the end of
    /// its first sentence, on one line, <see cref="MaxDerivedLength" /> UTF-16 units at most.
    /// </summary>
    /// <param name="prompt">The text of the prompt.</param>
    /// <returns>The title, or <see langword="null" /> for a prompt without text.</returns>
    public static string? FromPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        var opening = OneLine(StripMarker(FirstParagraph(prompt.AsSpan().Trim())));
        if (opening.Length == 0) return null;
        var title = FirstSentence(opening);
        if (title.Length <= MaxDerivedLength) return title;

        // Leave room for the ellipsis, and never cut a surrogate pair.
        var cut = MaxDerivedLength - 1;
        if (char.IsHighSurrogate(title[cut - 1])) cut--;
        var word = title.LastIndexOf(' ', cut - 1, cut);
        return string.Concat(title.AsSpan(0, word >= MaxDerivedLength / 2 ? word : cut).TrimEnd(), "…");
    }

    /// <summary>
    /// Derives a title from the text of an assistant message: its first line, <see cref="MaxDerivedLength" /> UTF-16
    /// units at most.
    /// </summary>
    /// <param name="text">The text of the message.</param>
    /// <returns>The title, or <see langword="null" /> for a message without text.</returns>
    public static string? FromMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var firstLine = text.Trim().Split(['\r', '\n'], 2, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        if (firstLine.Length == 0) return null;
        // Cut between two characters and without a space at the end: a list shows this text as it is, and a
        // deletion is confirmed with it.
        return firstLine.Length <= MaxDerivedLength
            ? firstLine
            : firstLine[..(char.IsHighSurrogate(firstLine[MaxDerivedLength - 1]) ? MaxDerivedLength - 1 : MaxDerivedLength)].TrimEnd();
    }

    private static ReadOnlySpan<char> FirstParagraph(ReadOnlySpan<char> text)
    {
        // A paragraph ends at a line that is empty or holds only spaces.
        var position = 0;
        while (position < text.Length)
        {
            var lineEnd = text[position..].IndexOf('\n');
            if (lineEnd < 0) break;
            var next = position + lineEnd + 1;
            var following = text[next..];
            var followingEnd = following.IndexOf('\n');
            if (followingEnd >= 0 && following[..followingEnd].IsWhiteSpace()) return text[..(position + lineEnd)];
            position = next;
        }

        return text;
    }

    private static ReadOnlySpan<char> StripMarker(ReadOnlySpan<char> text)
    {
        var line = text.TrimStart();
        var index = 0;
        while (index < line.Length && index < 6 && line[index] == '#') index++;
        if (index is 0 && line.Length > 0 && line[0] is '-' or '*' or '+') index = 1;
        if (index is 0)
        {
            while (index < line.Length && char.IsAsciiDigit(line[index])) index++;
            if (index > 0 && index < line.Length && line[index] is '.' or ')') index++;
            else index = 0;
        }

        // A marker is followed by a space: "#123" or "-flag" is the text itself.
        return index > 0 && index < line.Length && char.IsWhiteSpace(line[index]) ? line[index..].TrimStart() : text;
    }

    private static string OneLine(ReadOnlySpan<char> text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space) builder.Append(' ');
            space = false;
            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string FirstSentence(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('.' or '!' or '?')) continue;
            // The end of a sentence is followed by a space or by nothing: "parser.cs" goes on.
            if (index + 1 < text.Length && text[index + 1] != ' ') continue;
            var sentence = text.AsSpan(0, index).TrimEnd(".!?").TrimEnd();
            return sentence.Length > 0 ? sentence.ToString() : text;
        }

        return text;
    }
}
