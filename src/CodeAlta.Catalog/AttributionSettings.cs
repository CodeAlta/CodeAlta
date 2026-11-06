namespace CodeAlta.Catalog;

/// <summary>
/// How agents say that CodeAlta took part in what they write, from the global <c>[commits]</c> and
/// <c>[external_writing]</c> tables: the trailer that ends the message of their commits, and the instruction about the
/// texts they write in an external system (a pull request comment, an issue, a chat message, an email).
/// </summary>
/// <param name="CoAuthor">Whether agents end the message of their commits with <paramref name="CoAuthorTrailer"/>.</param>
/// <param name="CoAuthorTrailer">The trailer that ends the message of a commit: one line, <c>Token: value</c>.</param>
/// <param name="ExternalWriting">Whether agents are told <paramref name="ExternalWritingInstruction"/>.</param>
/// <param name="ExternalWritingInstruction">What agents are told about the texts they write in an external system: one line.</param>
public sealed record AttributionSettings(bool CoAuthor, string CoAuthorTrailer, bool ExternalWriting, string ExternalWritingInstruction)
{
    /// <summary>The trailer that names CodeAlta as a co-author of a commit of its agents, unless the user writes another.</summary>
    public const string DefaultCoAuthorTrailer = "Co-authored-by: CodeAlta <noreply@codealta.io>";

    /// <summary>What agents are told about the texts they write in an external system, unless the user writes another instruction.</summary>
    public const string DefaultExternalWritingInstruction = "When you write in an external system on the user's behalf (a pull request comment or review, an issue, a reply in an issue tracker, chat or email), end the text with a short italic line saying that it comes from CodeAlta, so readers do not take it for the user's own words, for example `_This review was written by [CodeAlta](https://codealta.io)._` or `_This issue was created by [CodeAlta](https://codealta.io)._`; this does not apply to commit messages.";

    /// <summary>The longest trailer, in characters.</summary>
    public const int MaximumCoAuthorTrailerLength = 200;

    /// <summary>The longest instruction about external writing, in characters: it is read on every turn.</summary>
    public const int MaximumExternalWritingInstructionLength = 1000;

    /// <summary>Gets the choices when the file says nothing: both on, with the texts of CodeAlta.</summary>
    public static AttributionSettings Default { get; } = new(true, DefaultCoAuthorTrailer, true, DefaultExternalWritingInstruction);

    /// <summary>Reads the two tables; an absent or empty text is the default one.</summary>
    /// <param name="commits">The <c>[commits]</c> table, or null.</param>
    /// <param name="externalWriting">The <c>[external_writing]</c> table, or null.</param>
    /// <returns>The choices.</returns>
    /// <exception cref="InvalidDataException">A text is not one line, is too long, or the trailer is not <c>Token: value</c>.</exception>
    public static AttributionSettings Read(CodeAltaCommitSettingsDocument? commits, CodeAltaExternalWritingSettingsDocument? externalWriting)
    {
        var trailer = Normalize(commits?.CoAuthorTrailer) ?? DefaultCoAuthorTrailer;
        var instruction = Normalize(externalWriting?.Instruction) ?? DefaultExternalWritingInstruction;
        if ((GetCoAuthorTrailerError(trailer) ?? GetExternalWritingInstructionError(instruction)) is { } error) throw new InvalidDataException(error);
        return new(commits?.CoAuthor != false, trailer, externalWriting?.Attribution != false, instruction);
    }

    /// <summary>Gets why a trailer cannot end the message of a commit, or null when it can.</summary>
    /// <param name="trailer">The trailer, trimmed.</param>
    /// <returns>The reason, or null.</returns>
    public static string? GetCoAuthorTrailerError(string? trailer)
    {
        if (string.IsNullOrWhiteSpace(trailer)) return "The co-author trailer is empty.";
        if (trailer.Length > MaximumCoAuthorTrailerLength || trailer.Any(char.IsControl))
            return $"The co-author trailer takes at most {MaximumCoAuthorTrailerLength} characters, on one line.";

        // A Git trailer is a token of letters, digits and hyphens, a colon and a value: Git reads nothing else as one.
        var colon = trailer.IndexOf(':', StringComparison.Ordinal);
        var token = colon > 0 ? trailer[..colon] : string.Empty;
        return token.Length > 0 && token.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-') && token[0] != '-'
            && trailer[(colon + 1)..].Trim().Length > 0
                ? null
                : "The co-author trailer is a Git trailer, such as `Co-authored-by: Name <email>`.";
    }

    /// <summary>Gets why a text cannot be the instruction about external writing, or null when it can.</summary>
    /// <param name="instruction">The instruction, trimmed.</param>
    /// <returns>The reason, or null.</returns>
    public static string? GetExternalWritingInstructionError(string? instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction)) return "The instruction about external writing is empty.";
        return instruction.Length > MaximumExternalWritingInstructionLength || instruction.Any(char.IsControl)
            ? $"The instruction about external writing takes at most {MaximumExternalWritingInstructionLength} characters, on one line."
            : null;
    }

    // A text of the file is trimmed; an empty one says nothing.
    internal static string? Normalize(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
