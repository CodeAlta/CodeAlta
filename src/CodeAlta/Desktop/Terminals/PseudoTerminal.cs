namespace CodeAlta.Desktop.Terminals;

/// <summary>What a pseudo-terminal starts: a program in a folder, with its arguments, its environment and the size of its screen.</summary>
/// <param name="FileName">The full path of the program.</param>
/// <param name="Arguments">Its arguments, each as the program must receive it.</param>
/// <param name="WorkingDirectory">The folder it starts in.</param>
/// <param name="Environment">Its whole environment.</param>
/// <param name="Columns">The width of its screen, in characters.</param>
/// <param name="Rows">The height of its screen, in lines.</param>
internal sealed record PseudoTerminalStart(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    int Columns,
    int Rows);

/// <summary>
/// A program that runs behind a terminal of its own: what it shows is read here as the bytes a terminal
/// receives, and what is written here is what its keyboard sends. ConPTY on Windows, a pty elsewhere.
/// </summary>
internal abstract class PseudoTerminal : IDisposable
{
    /// <summary>The smallest and the largest screen a program is given, in characters.</summary>
    internal const int MinimumSize = 2, MaximumSize = 1000;

    /// <summary>The process of the program.</summary>
    public abstract int ProcessId { get; }

    /// <summary>Completes with the exit code of the program once it has ended.</summary>
    /// <remarks>What the program wrote last may still be read after that: <see cref="Read"/> returns 0 at the end.</remarks>
    public abstract Task<int> Exited { get; }

    /// <summary>Starts a program behind a new terminal.</summary>
    /// <exception cref="PseudoTerminalException">The terminal or the program could not be started.</exception>
    /// <exception cref="PlatformNotSupportedException">This system has no pseudo-terminal.</exception>
    public static PseudoTerminal Start(PseudoTerminalStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        var sized = start with { Columns = Clamp(start.Columns), Rows = Clamp(start.Rows) };
        if (OperatingSystem.IsWindows()) return WindowsPseudoTerminal.Launch(sized);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return UnixPseudoTerminal.Launch(sized);
        throw new PlatformNotSupportedException("Terminals are available on Windows, macOS and Linux.");
    }

    /// <summary>Waits for what the program shows next.</summary>
    /// <returns>The number of bytes read, or 0 once the program has ended and everything was read.</returns>
    public abstract int Read(Span<byte> buffer);

    /// <summary>Sends bytes to the program as its keyboard would. Nothing is sent to a program that has ended.</summary>
    public abstract void Write(ReadOnlySpan<byte> data);

    /// <summary>Changes the size of the screen of the program.</summary>
    public abstract void Resize(int columns, int rows);

    /// <summary>Ends the program and what it runs in its terminal. It returns at once; <see cref="Exited"/> tells when it is done.</summary>
    public abstract void Kill();

    /// <inheritdoc />
    public abstract void Dispose();

    /// <summary>A size a terminal accepts.</summary>
    internal static int Clamp(int size) => Math.Clamp(size, MinimumSize, MaximumSize);
}

/// <summary>A terminal or its program could not be started.</summary>
internal sealed class PseudoTerminalException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PseudoTerminalException(string message, Exception? innerException = null) : base(message, innerException) { }
}
