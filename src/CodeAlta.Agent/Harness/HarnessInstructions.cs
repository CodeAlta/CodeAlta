using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Agent.Harness;

/// <summary>
/// Keeps an agent harness that holds its own conversation told of the instructions of CodeAlta: it is given them once,
/// and then only what changed, with the prompt that follows the change.
/// </summary>
public static class HarnessInstructions
{
    private const string UpdateOpening = "<codealta_instructions_update>";
    private const string UpdateClosing = "</codealta_instructions_update>";
    private const int RemovedParagraphPreview = 120;

    /// <summary>The value by which two versions of the instructions are told apart.</summary>
    /// <param name="instructions">The instructions.</param>
    /// <returns>The first 16 hexadecimal digits of their SHA-256.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="instructions" /> is <see langword="null" />.</exception>
    public static string Hash(string instructions)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instructions)), 0, 8);
    }

    /// <summary>
    /// Creates the note that tells a conversation what changed in the instructions of CodeAlta since it was given them.
    /// </summary>
    /// <param name="previous">The instructions the conversation was given, or <see langword="null" /> when they are not known.</param>
    /// <param name="current">The instructions of the prompt that starts.</param>
    /// <param name="givenAt">Where the conversation was given the instructions, as it is named to the model, such as
    /// "the `# CodeAlta` part of your system prompt".</param>
    /// <returns>The note, given before the prompt.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="current" /> or <paramref name="givenAt" /> is <see langword="null" />.</exception>
    public static string CreateUpdate(string? previous, string current, string givenAt)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(givenAt);

        var builder = new StringBuilder();
        builder.AppendLine(UpdateOpening);
        if (previous is null)
        {
            builder.AppendLine($"The instructions of CodeAlta for this session are no longer the ones this conversation was given. This note is not a request of the user. The text below replaces all the instructions of CodeAlta you were given before, {givenAt} and earlier notes like this one:");
            builder.AppendLine();
            builder.AppendLine(current.Trim());
        }
        else
        {
            // The parts are the paragraphs: what the text is made of does not have to be known to compare it.
            var before = SplitParagraphs(previous);
            var after = SplitParagraphs(current);
            var kept = before.ToHashSet(StringComparer.Ordinal);
            var present = after.ToHashSet(StringComparer.Ordinal);
            var removed = before.Where(paragraph => !present.Contains(paragraph)).ToArray();
            var added = after.Where(paragraph => !kept.Contains(paragraph)).ToArray();

            builder.AppendLine($"The instructions of CodeAlta for this session changed. This note is not a request of the user: it updates {givenAt}, and earlier notes like this one, for what follows. What it does not name still applies.");
            if (removed.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("These parts no longer apply (each is named by how it starts):");
                foreach (var paragraph in removed)
                {
                    builder.Append("- ").AppendLine(FirstWords(paragraph));
                }
            }

            if (added.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine(removed.Length > 0 ? "These parts are new, or replace the ones above:" : "These parts are new:");
                foreach (var paragraph in added)
                {
                    builder.AppendLine();
                    builder.AppendLine(paragraph);
                }
            }
        }

        builder.Append(UpdateClosing);
        return builder.ToString();
    }

    private static string[] SplitParagraphs(string text)
        => [.. text.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(static paragraph => paragraph.Trim()).Where(static paragraph => paragraph.Length > 0)];

    private static string FirstWords(string paragraph)
    {
        var line = paragraph.AsSpan();
        var end = line.IndexOf('\n');
        if (end >= 0)
        {
            line = line[..end];
        }

        return line.Length <= RemovedParagraphPreview ? line.ToString() : string.Concat(line[..RemovedParagraphPreview], " …");
    }
}
