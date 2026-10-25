using System.Text;

namespace CodeAlta.Cli;

/// <summary>
/// The standard streams of a run of <c>altacli</c>. Standard output carries the protocol alone (the final answer in text
/// mode, the notifications of <c>--format jsonl</c>); everything else (progress, diagnostics, logs) goes to standard error.
/// </summary>
internal sealed class CliStreams
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Gets standard input, read as text: a byte order mark (UTF-8, UTF-16) tells its encoding, UTF-8 otherwise.</summary>
    public required TextReader Stdin { get; init; }

    /// <summary>Gets whether standard input is redirected (a pipe or a file), rather than a terminal.</summary>
    public bool IsStdinRedirected { get; init; }

    /// <summary>
    /// Gets standard input as bytes, for <c>altacli acp</c>, which reads the messages of its client there; null when the run
    /// has none. Only one of it and <see cref="Stdin"/> is read by a run.
    /// </summary>
    public Stream? StdinStream { get; init; }

    /// <summary>Gets standard output, written in UTF-8 by <see cref="CliOutput"/> only.</summary>
    public required Stream Stdout { get; init; }

    /// <summary>Gets standard error, which <see cref="Console.Out"/> also writes to once the console is taken over.</summary>
    public required TextWriter Stderr { get; init; }

    /// <summary>
    /// Takes over the console of the process: standard output is kept for the protocol, and <see cref="Console.Out"/>
    /// writes to standard error, so that a stray write of a library or a plugin cannot corrupt the protocol.
    /// </summary>
    /// <returns>The streams of the process.</returns>
    public static CliStreams FromConsole()
    {
        var stdout = Console.OpenStandardOutput();
        var stderr = new StreamWriter(Console.OpenStandardError(), Utf8) { AutoFlush = true };
        var input = Console.OpenStandardInput();
        var stdin = new StreamReader(input, Utf8, detectEncodingFromByteOrderMarks: true);
        return new CliStreams
        {
            Stdin = stdin,
            IsStdinRedirected = Console.IsInputRedirected,
            StdinStream = input,
            Stdout = stdout,
            Stderr = TakeOverConsole(stderr),
        };
    }

    /// <summary>
    /// Makes <see cref="Console.Out"/> write to <paramref name="stderr"/>, through one synchronized writer that the caller
    /// uses for standard error too: the writes of both never interleave within a line.
    /// </summary>
    /// <param name="stderr">The writer of standard error.</param>
    /// <returns>The synchronized writer, set as <see cref="Console.Out"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stderr"/> is null.</exception>
    public static TextWriter TakeOverConsole(TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        var synchronized = TextWriter.Synchronized(stderr);
        Console.SetOut(synchronized);
        return synchronized;
    }
}
