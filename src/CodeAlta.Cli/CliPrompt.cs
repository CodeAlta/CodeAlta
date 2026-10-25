using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Cli;

/// <summary>
/// The prompt of <c>altacli run</c>, by one rule:
/// <list type="bullet">
/// <item>the words of the command line, joined with spaces, are the prompt;</item>
/// <item><c>-</c> alone reads the prompt from standard input;</item>
/// <item>when standard input is redirected and words are given, what it holds (if anything) is appended to them in a
/// <c>&lt;stdin&gt;</c>...<c>&lt;/stdin&gt;</c> block, after a blank line;</item>
/// <item>when standard input is redirected and no word is given, it is the prompt.</item>
/// </list>
/// A byte order mark is honored by the reader of standard input and dropped; line endings are kept as they are.
/// </summary>
internal static class CliPrompt
{
    /// <summary>Reads the prompt.</summary>
    /// <returns>The prompt, or null with a message when there is none.</returns>
    public static async Task<(string? Prompt, string? Error)> ReadAsync(IReadOnlyList<string> words, CliStreams streams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(streams);
        if (words is ["-"])
        {
            var piped = await ReadStdinAsync(streams, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(piped) ? (null, "The prompt read from standard input is empty.") : (piped, null);
        }

        var argument = string.Join(' ', words);
        var stdin = streams.IsStdinRedirected ? await ReadStdinAsync(streams, cancellationToken).ConfigureAwait(false) : null;
        if (string.IsNullOrWhiteSpace(stdin)) stdin = null;
        if (string.IsNullOrWhiteSpace(argument))
        {
            return stdin is null
                ? (null, "No prompt: give it as arguments, with - to read it from standard input, or pipe it.")
                : (stdin, null);
        }

        return stdin is null ? (argument, null) : (Combine(argument, stdin), null);
    }

    /// <summary>The prompt of words given with standard input piped: the words, then the input in a delimited block.</summary>
    public static string Combine(string argument, string stdin)
        => argument + "\n\n<stdin>\n" + (stdin.EndsWith('\n') ? stdin : stdin + "\n") + "</stdin>";

    /// <summary>The files of <c>--file</c>, attached by reference; each must exist.</summary>
    /// <returns>The attachments, or null with a message naming a file that does not exist.</returns>
    public static (IReadOnlyList<SessionPromptAttachment>? Attachments, string? Error) Attach(IReadOnlyList<string> files, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(files);
        var attachments = new List<SessionPromptAttachment>(files.Count);
        foreach (var file in files)
        {
            var path = Path.GetFullPath(file, baseDirectory);
            if (!File.Exists(path) && !Directory.Exists(path)) return (null, $"The file '{file}' given with --file does not exist.");
            attachments.Add(new SessionPromptAttachment
            {
                AttachmentId = "file-" + attachments.Count,
                Path = path,
                DisplayName = Path.GetFileName(path),
            });
        }

        return (attachments, null);
    }

    private static async Task<string> ReadStdinAsync(CliStreams streams, CancellationToken cancellationToken)
    {
        var text = await streams.Stdin.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        // A reader that did not detect the mark (a test, a reader given the text) leaves it at the start.
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }
}
