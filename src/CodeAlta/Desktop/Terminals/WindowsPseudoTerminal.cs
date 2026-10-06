using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodeAlta.Desktop.Terminals;

/// <summary>
/// A program behind a Windows pseudoconsole (ConPTY, Windows 10 1809 and later). The console host renders
/// what the program does to its console as the bytes a terminal receives, in UTF-8.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPseudoTerminal : PseudoTerminal
{
    // How long a program has to end once its console is closed before it is terminated.
    private static readonly TimeSpan KillPatience = TimeSpan.FromSeconds(5);
    // The size of the two pipes: what a program writes in one go is read in one go.
    private const int PipeSize = 128 * 1024;

    private readonly Lock _gate = new();
    private readonly SafeProcessHandle _process;
    private readonly SafeFileHandle _input;
    private readonly SafeFileHandle _output;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ProcessWaitHandle _wait;
    private readonly RegisteredWaitHandle _registration;
    private nint _console;
    private bool _disposed;

    private WindowsPseudoTerminal(SafeProcessHandle process, int processId, nint console, SafeFileHandle input, SafeFileHandle output)
    {
        _process = process;
        ProcessId = processId;
        _console = console;
        _input = input;
        _output = output;
        _wait = new ProcessWaitHandle(process);
        _registration = ThreadPool.RegisterWaitForSingleObject(_wait, static (state, _) => ((WindowsPseudoTerminal)state!).ProcessEnded(), this, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <inheritdoc />
    public override int ProcessId { get; }

    /// <inheritdoc />
    public override Task<int> Exited => _exited.Task;

    /// <summary>Starts a program behind a new pseudoconsole.</summary>
    internal static WindowsPseudoTerminal Launch(PseudoTerminalStart start)
    {
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        nint console = 0, attributes = 0;
        var initialized = false;
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, 0, PipeSize) || !CreatePipe(out outputRead, out outputWrite, 0, PipeSize)) throw Failure("The pipes of the terminal could not be created.");
            var result = CreatePseudoConsole(new Coordinates((short)start.Columns, (short)start.Rows), inputRead, outputWrite, 0, out console);
            if (result != 0) throw new PseudoTerminalException("The pseudoconsole could not be created.", Marshal.GetExceptionForHR(result));
            // The console host has its own copies of these two ends: ours are closed so that the pipes end with it.
            inputRead.Dispose();
            outputWrite.Dispose();

            nuint size = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal((nint)size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw Failure("The start of the program could not be prepared.");
            initialized = true;
            if (!UpdateProcThreadAttribute(attributes, 0, PseudoConsoleAttribute, console, (nuint)nint.Size, 0, 0)) throw Failure("The program could not be given its console.");

            var startup = new StartupInformationEx { Attributes = attributes };
            startup.Information.Size = Marshal.SizeOf<StartupInformationEx>();
            // With this flag and no handle, the program has the handles of its console: without it, a program
            // started by a process whose own output is redirected would write there.
            startup.Information.Flags = UseStandardHandles;
            var commandLine = new StringBuilder(CommandLine(start.FileName, start.Arguments));
            var environment = EnvironmentBlock(start.Environment);
            if (!CreateProcessW(null, commandLine, 0, 0, false, ExtendedStartupInformation | UnicodeEnvironment, environment, start.WorkingDirectory, ref startup, out var created))
            {
                throw Failure($"'{start.FileName}' could not be started.");
            }
            _ = CloseHandle(created.Thread);
            var terminal = new WindowsPseudoTerminal(new SafeProcessHandle(created.Process, ownsHandle: true), created.ProcessId, console, inputWrite, outputRead);
            console = 0;
            inputWrite = null;
            outputRead = null;
            return terminal;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new PlatformNotSupportedException("Terminals need Windows 10 version 1809 or later.", exception);
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
            if (console != 0) ClosePseudoConsole(console);
            inputRead?.Dispose();
            inputWrite?.Dispose();
            outputRead?.Dispose();
            outputWrite?.Dispose();
        }
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        try
        {
            // The pipe ends, with an error, when the console host has written everything and is gone.
            return ReadFile(_output, ref MemoryMarshal.GetReference(buffer), buffer.Length, out var read, 0) ? read : 0;
        }
        catch (ObjectDisposedException) { return 0; }
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> data)
    {
        try
        {
            while (!data.IsEmpty)
            {
                if (!WriteFile(_input, in MemoryMarshal.GetReference(data), data.Length, out var written, 0) || written <= 0) return;
                data = data[written..];
            }
        }
        catch (ObjectDisposedException)
        {
            // Nothing reads it anymore.
        }
    }

    /// <inheritdoc />
    public override void Resize(int columns, int rows)
    {
        lock (_gate)
        {
            if (_console != 0) _ = ResizePseudoConsole(_console, new Coordinates((short)Clamp(columns), (short)Clamp(rows)));
        }
    }

    /// <inheritdoc />
    public override void Kill()
    {
        if (_exited.Task.IsCompleted) return;
        // Closing the console ends the programs attached to it. Before Windows 11 24H2 the call waits until
        // what they wrote last was read, so it never runs on the caller's thread.
        _ = Task.Run(async () =>
        {
            CloseConsole();
            await Task.WhenAny(_exited.Task, Task.Delay(KillPatience)).ConfigureAwait(false);
            if (!_exited.Task.IsCompleted) Terminate();
        });
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Terminate();
        _ = Task.Run(() =>
        {
            // Closing the console can wait until what the program wrote last was read: it is read here, and dropped.
            var drained = Task.Run(Drain);
            CloseConsole();
            drained.Wait();
            _registration.Unregister(null);
            _wait.Dispose();
            _input.Dispose();
            _output.Dispose();
            _process.Dispose();
            _exited.TrySetResult(-1);
        });
    }

    // The program of the terminal has ended: its console is closed, which ends what else is attached to it
    // and then the output pipe, once everything written was read.
    private void ProcessEnded()
    {
        var code = -1;
        try
        {
            if (GetExitCodeProcess(_process, out var value)) code = unchecked((int)value);
        }
        catch (ObjectDisposedException)
        {
            // Disposed while it ended.
        }
        _exited.TrySetResult(code);
        CloseConsole();
    }

    private void CloseConsole()
    {
        nint console;
        lock (_gate)
        {
            console = _console;
            _console = 0;
        }
        if (console != 0) ClosePseudoConsole(console);
    }

    private void Drain()
    {
        var buffer = new byte[4096];
        while (Read(buffer) > 0)
        {
        }
    }

    private void Terminate()
    {
        try { _ = TerminateProcess(_process, 1); }
        catch (ObjectDisposedException)
        {
            // Already gone.
        }
    }

    private static PseudoTerminalException Failure(string message) => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    /// <summary>The command line a program parses back into these arguments, by the rules of the C runtime.</summary>
    internal static string CommandLine(string fileName, IReadOnlyList<string> arguments)
    {
        var line = new StringBuilder();
        Append(line, fileName);
        foreach (var argument in arguments)
        {
            line.Append(' ');
            Append(line, argument);
        }
        return line.ToString();

        static void Append(StringBuilder line, string argument)
        {
            if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
            {
                line.Append(argument);
                return;
            }
            line.Append('"');
            var backslashes = 0;
            foreach (var character in argument)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                // Backslashes are literal unless a quote follows them: then each is doubled, and the quote escaped.
                line.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                line.Append(character);
            }
            line.Append('\\', backslashes * 2).Append('"');
        }
    }

    /// <summary>An environment as CreateProcess reads it: sorted <c>name=value</c> strings, each ended by a null, and a last null.</summary>
    internal static char[] EnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var block = new StringBuilder();
        foreach (var (name, value) in environment.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (name.Length == 0 || name.Contains('\0') || value.Contains('\0')) continue;
            block.Append(name).Append('=').Append(value).Append('\0');
        }
        if (block.Length == 0) block.Append('\0');
        block.Append('\0');
        return block.ToString().ToCharArray();
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process) => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }

    private const uint ExtendedStartupInformation = 0x00080000, UnicodeEnvironment = 0x00000400;
    private const int UseStandardHandles = 0x00000100;
    private const nuint PseudoConsoleAttribute = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coordinates(short x, short y)
    {
        public readonly short X = x, Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInformation
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public nint Reserved2, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInformationEx
    {
        public StartupInformation Information;
        public nint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, int size);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int CreatePseudoConsole(Coordinates size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int ResizePseudoConsole(nint console, Coordinates size);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void ClosePseudoConsole(nint console);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, char[] environment, string? directory, ref StartupInformationEx startup, out ProcessInformation created);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle file, ref byte buffer, int count, out int read, nint overlapped);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle file, in byte buffer, int count, out int written, nint overlapped);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint code);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
