using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace CodeAlta.Desktop.Terminals;

/// <summary>
/// A program behind a pty on Linux and macOS. The program is the leader of a session of its own, with the
/// pty as its controlling terminal: its job control and the signals of its keyboard work as in any terminal.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class UnixPseudoTerminal : PseudoTerminal
{
    // The program is started by a shell that makes the pty the terminal of the session (the first terminal a
    // session leader opens becomes its controlling terminal), enters the folder and becomes the program.
    // $1 is the pty, $2 the folder, the rest the program and its arguments.
    private const string Starter = "true 3<>\"$1\"; cd -- \"$2\" 2>/dev/null; shift 2; exec \"$@\"";
    // The shell must open the pty in a way that can make it its terminal: bash does, and the sh of Linux; the sh
    // of macOS can be set to zsh, which opens what it redirects in a way that cannot.
    private static string StarterShell => OperatingSystem.IsMacOS() && File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";

    // How long a read waits before it looks again at whether the program has ended or was asked to end.
    private const int ReadPatienceMilliseconds = 200;
    // How long a program has to end after a hangup before it is killed.
    private static readonly TimeSpan KillPatience = TimeSpan.FromSeconds(3);

    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _master;
    private int _users;
    private bool _closing;
    private bool _closed;

    private UnixPseudoTerminal(int master, int processId)
    {
        _master = master;
        ProcessId = processId;
        new Thread(Wait) { IsBackground = true, Name = "terminal wait" }.Start();
    }

    /// <inheritdoc />
    public override int ProcessId { get; }

    /// <inheritdoc />
    public override Task<int> Exited => _exited.Task;

    /// <summary>Starts a program behind a new pty.</summary>
    internal static UnixPseudoTerminal Launch(PseudoTerminalStart start)
    {
        var master = -1;
        nint actions = 0, attributes = 0, signals = 0, arguments = 0, environment = 0;
        var actionsReady = false;
        var attributesReady = false;
        try
        {
            // No other program this process starts keeps the pty open: Linux marks it so as it opens it, macOS after.
            master = posix_openpt(OpenReadWrite | OpenNoControllingTerminal | (OperatingSystem.IsLinux() ? LinuxOpenCloseOnExecute : 0));
            if (master < 0) throw Failure("The pty could not be created.");
            if (grantpt(master) != 0 || unlockpt(master) != 0) throw Failure("The pty could not be opened.");
            _ = ioctl(master, CloseOnExecute);
            var name = new byte[256];
            if (ptsname_r(master, name, (nuint)name.Length) != 0) throw Failure("The pty has no name.");
            var device = Encoding.UTF8.GetString(name, 0, Math.Max(0, Array.IndexOf(name, (byte)0)));
            SetSize(master, start.Columns, start.Rows);
            AcceptUtf8(master);

            // Their sizes differ from one system to the other and are not known here: each gets more than any needs.
            actions = Marshal.AllocHGlobal(Opaque);
            attributes = Marshal.AllocHGlobal(Opaque);
            signals = Marshal.AllocHGlobal(Opaque);
            Clear(actions);
            Clear(attributes);
            Clear(signals);
            Check(posix_spawn_file_actions_init(actions), "The start of the program could not be prepared.");
            actionsReady = true;
            Check(posix_spawn_file_actions_addopen(actions, 0, device, OpenReadWrite, 0), "The program could not be given its terminal.");
            Check(posix_spawn_file_actions_adddup2(actions, 0, 1), "The program could not be given its terminal.");
            Check(posix_spawn_file_actions_adddup2(actions, 0, 2), "The program could not be given its terminal.");
            if (OperatingSystem.IsLinux()) CloseTheRest(actions);
            Check(posix_spawnattr_init(attributes), "The start of the program could not be prepared.");
            attributesReady = true;
            // A session of its own, every signal as a program expects it at its start (this process ignores
            // some), and on macOS nothing open but its terminal. Linux starts the session before it opens the
            // pty, which makes the pty the terminal of the session; macOS opens it first, and the starter does it.
            var flags = SpawnSetSignalDefaults | SpawnSetSignalMask | (OperatingSystem.IsMacOS() ? MacSpawnSetSession | MacSpawnCloseByDefault : LinuxSpawnSetSession);
            Check(posix_spawnattr_setflags(attributes, (short)flags), "The start of the program could not be prepared.");
            _ = sigfillset(signals);
            Check(posix_spawnattr_setsigdefault(attributes, signals), "The start of the program could not be prepared.");
            _ = sigemptyset(signals);
            Check(posix_spawnattr_setsigmask(attributes, signals), "The start of the program could not be prepared.");

            var shell = StarterShell;
            string[] command = [shell, "-c", Starter, "sh", device, start.WorkingDirectory, start.FileName, .. start.Arguments];
            arguments = Strings(command);
            environment = Strings(start.Environment.Where(static pair => pair.Key.Length > 0 && !pair.Key.Contains('=')).Select(static pair => pair.Key + "=" + pair.Value));
            var result = posix_spawn(out var processId, shell, actions, attributes, arguments, environment);
            if (result != 0) throw new PseudoTerminalException($"'{start.FileName}' could not be started.", new Win32Exception(result));
            var terminal = new UnixPseudoTerminal(master, processId);
            master = -1;
            return terminal;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new PlatformNotSupportedException("This system has no pty the application can use.", exception);
        }
        finally
        {
            if (actionsReady) _ = posix_spawn_file_actions_destroy(actions);
            if (attributesReady) _ = posix_spawnattr_destroy(attributes);
            Free(arguments);
            Free(environment);
            if (actions != 0) Marshal.FreeHGlobal(actions);
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
            if (signals != 0) Marshal.FreeHGlobal(signals);
            if (master >= 0) _ = close(master);
        }
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty || !Enter()) return 0;
        try
        {
            while (true)
            {
                if (Volatile.Read(ref _closing)) return 0;
                var ready = Readable(_master, ReadPatienceMilliseconds);
                if (ready == 0)
                {
                    // Nothing more to read from a program that has ended: what it left running in the
                    // background can keep the pty open, and is not waited for.
                    if (_exited.Task.IsCompleted) return 0;
                    continue;
                }
                if (ready < 0)
                {
                    if (Marshal.GetLastPInvokeError() == Interrupted) continue;
                    return 0;
                }
                var read = (int)LibC.read(_master, ref MemoryMarshal.GetReference(buffer), (nuint)buffer.Length);
                if (read > 0) return read;
                if (read < 0 && Marshal.GetLastPInvokeError() is var error && (error == Interrupted || error == TryAgain)) continue;
                // The end of the file on macOS, an input/output error on Linux: nothing has the pty open anymore.
                return 0;
            }
        }
        finally { Leave(); }
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> data)
    {
        if (!Enter()) return;
        try
        {
            while (!data.IsEmpty && !Volatile.Read(ref _closing))
            {
                var written = (int)LibC.write(_master, in MemoryMarshal.GetReference(data), (nuint)data.Length);
                if (written > 0)
                {
                    data = data[written..];
                    continue;
                }
                if (written < 0 && Marshal.GetLastPInvokeError() is var error && (error == Interrupted || error == TryAgain)) continue;
                return;
            }
        }
        finally { Leave(); }
    }

    /// <inheritdoc />
    public override void Resize(int columns, int rows)
    {
        if (!Enter()) return;
        try { SetSize(_master, Clamp(columns), Clamp(rows)); }
        finally { Leave(); }
    }

    /// <inheritdoc />
    public override void Kill()
    {
        if (_exited.Task.IsCompleted) return;
        // What closing a terminal window does: a hangup for the program, which passes it on to what it runs.
        _ = kill(ProcessId, Hangup);
        _ = kill(ProcessId, Continue);
        _ = Task.Run(async () =>
        {
            await Task.WhenAny(_exited.Task, Task.Delay(KillPatience)).ConfigureAwait(false);
            if (!_exited.Task.IsCompleted) Terminate();
        });
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        lock (_gate)
        {
            if (_closing) return;
            _closing = true;
        }
        if (!_exited.Task.IsCompleted) Terminate();
        Close();
    }

    private void Terminate()
    {
        // The program leads its own process group; what it started in other groups ends with the terminal.
        _ = kill(-ProcessId, Kill9);
        _ = kill(ProcessId, Kill9);
    }

    // A descriptor is closed only when no call uses it: its number would be given to the next file opened.
    private bool Enter()
    {
        lock (_gate)
        {
            if (_closed || _closing) return false;
            _users++;
            return true;
        }
    }

    private void Leave()
    {
        lock (_gate) _users--;
        Close();
    }

    private void Close()
    {
        lock (_gate)
        {
            if (!_closing || _closed || _users > 0) return;
            _closed = true;
        }
        _ = close(_master);
    }

    private void Wait()
    {
        while (true)
        {
            var result = waitpid(ProcessId, out var status, 0);
            if (result == ProcessId)
            {
                // The exit code of a program that ended by itself, or 128 plus the signal that ended it, as a shell reports it.
                _exited.TrySetResult((status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f));
                return;
            }
            if (result < 0 && Marshal.GetLastPInvokeError() == Interrupted) continue;
            _exited.TrySetResult(-1);
            return;
        }
    }

    private static void SetSize(int descriptor, int columns, int rows)
    {
        var size = new WindowSize { Rows = (ushort)rows, Columns = (ushort)columns };
        // On Apple silicon the arguments after the named ones of a variadic function are on the stack: six
        // unused arguments fill the registers, so that the size is where ioctl reads it.
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64) _ = ioctl(descriptor, MacSetWindowSize, 0, 0, 0, 0, 0, 0, ref size);
        else _ = ioctl(descriptor, OperatingSystem.IsMacOS() ? MacSetWindowSize : LinuxSetWindowSize, ref size);
    }

    // Erasing a character in a line being typed erases all its bytes.
    private static void AcceptUtf8(int descriptor)
    {
        var settings = new byte[256];
        if (tcgetattr(descriptor, settings) != 0) return;
        // The input flags come first, 4 bytes on Linux and 8 on macOS: the flag is in the low ones on both.
        BinaryPrimitives.WriteUInt32LittleEndian(settings, BinaryPrimitives.ReadUInt32LittleEndian(settings) | InputIsUtf8);
        _ = tcsetattr(descriptor, 0, settings);
    }

    /// <summary>Waits until the descriptor has something to read. Returns 1 then, 0 when the time is over, -1 on an error.</summary>
    private static int Readable(int descriptor, int milliseconds)
    {
        // select: the one wait that works with a pty on every macOS. Its set holds 1024 descriptors, one bit each.
        if (descriptor >= 1024) return 1;
        var set = new byte[128];
        set[descriptor / 8] = (byte)(1 << (descriptor % 8));
        // Seconds then microseconds: two 8-byte integers on Linux, 8 then 4 bytes on macOS.
        var time = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(time.AsSpan(8), milliseconds * 1000);
        return select(descriptor + 1, set, 0, 0, time);
    }

    // On Linux a program inherits what this process has open and did not mark otherwise: a C library that can
    // close them all at the start of the program is asked to (glibc 2.34 and later, musl 1.2.4 and later).
    private static void CloseTheRest(nint actions)
    {
        try { _ = posix_spawn_file_actions_addclosefrom_np(actions, 3); }
        catch (EntryPointNotFoundException)
        {
            // An older C library: the program starts with them, as one started by .NET does.
        }
    }

    private static PseudoTerminalException Failure(string message) => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    private static void Check(int result, string message)
    {
        if (result != 0) throw new PseudoTerminalException(message, new Win32Exception(result));
    }

    private static void Clear(nint memory)
    {
        for (var offset = 0; offset < Opaque; offset += nint.Size) Marshal.WriteIntPtr(memory, offset, 0);
    }

    // A null-ended array of null-ended UTF-8 strings.
    private static nint Strings(IEnumerable<string> values)
    {
        var list = values.Where(static value => !value.Contains('\0')).ToArray();
        var array = Marshal.AllocHGlobal((list.Length + 1) * nint.Size);
        for (var index = 0; index <= list.Length; index++) Marshal.WriteIntPtr(array, index * nint.Size, 0);
        for (var index = 0; index < list.Length; index++) Marshal.WriteIntPtr(array, index * nint.Size, Marshal.StringToCoTaskMemUTF8(list[index]));
        return array;
    }

    private static void Free(nint array)
    {
        if (array == 0) return;
        for (var offset = 0; Marshal.ReadIntPtr(array, offset) is var text && text != 0; offset += nint.Size) Marshal.FreeCoTaskMem(text);
        Marshal.FreeHGlobal(array);
    }

    private const int Opaque = 1024;
    private const int OpenReadWrite = 2, LinuxOpenCloseOnExecute = 0x80000;
    private static int OpenNoControllingTerminal => OperatingSystem.IsMacOS() ? 0x20000 : 0x100;
    private static nuint CloseOnExecute => OperatingSystem.IsMacOS() ? 0x20006601u : 0x5451u;
    private const nuint LinuxSetWindowSize = 0x5414, MacSetWindowSize = 0x80087467;
    private const uint InputIsUtf8 = 0x4000;
    private const int SpawnSetSignalDefaults = 0x04, SpawnSetSignalMask = 0x08, LinuxSpawnSetSession = 0x80, MacSpawnSetSession = 0x0400, MacSpawnCloseByDefault = 0x4000;
    private const int Hangup = 1, Kill9 = 9, Interrupted = 4;
    private static int Continue => OperatingSystem.IsMacOS() ? 19 : 18;
    private static int TryAgain => OperatingSystem.IsMacOS() ? 35 : 11;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowSize
    {
        public ushort Rows, Columns, Width, Height;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_openpt(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int grantpt(int descriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlockpt(int descriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern int ptsname_r(int descriptor, byte[] name, nuint length);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int descriptor, nuint request);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int descriptor, nuint request, ref WindowSize size);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int descriptor, nuint request, nint unused2, nint unused3, nint unused4, nint unused5, nint unused6, nint unused7, ref WindowSize size);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int descriptor, byte[] settings);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int descriptor, int when, byte[] settings);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_init(nint actions);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_destroy(nint actions);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_addopen(nint actions, int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_adddup2(nint actions, int descriptor, int copy);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_addclosefrom_np(nint actions, int from);

    [DllImport("libc")]
    private static extern int posix_spawnattr_init(nint attributes);

    [DllImport("libc")]
    private static extern int posix_spawnattr_destroy(nint attributes);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setflags(nint attributes, short flags);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setsigdefault(nint attributes, nint signals);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setsigmask(nint attributes, nint signals);

    [DllImport("libc")]
    private static extern int sigfillset(nint signals);

    [DllImport("libc")]
    private static extern int sigemptyset(nint signals);

    [DllImport("libc")]
    private static extern int posix_spawn(out int processId, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint actions, nint attributes, nint arguments, nint environment);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int processId, out int status, int options);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);

    [DllImport("libc", SetLastError = true)]
    private static extern int select(int count, byte[] read, nint write, nint error, byte[] time);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);

    private static class LibC
    {
        [DllImport("libc", SetLastError = true)]
        internal static extern nint read(int descriptor, ref byte buffer, nuint count);

        [DllImport("libc", SetLastError = true)]
        internal static extern nint write(int descriptor, in byte buffer, nuint count);
    }
}
