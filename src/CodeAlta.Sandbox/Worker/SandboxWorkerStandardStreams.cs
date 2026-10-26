using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeAlta.Sandbox.Worker;

/// <summary>
/// Takes the standard streams of the worker process for its protocol, and leaves the null device in their place: the
/// commands the worker starts inherit its standard streams when they are not redirected, and would otherwise read and
/// write the protocol. Git for Windows also blocks at its start on a pipe another thread of the worker reads.
/// </summary>
internal static partial class SandboxWorkerStandardStreams
{
    private const int StdInput = -10;
    private const int StdOutput = -11;
    private const int StdError = -12;
    private const uint HandleFlagInherit = 1;

    /// <summary>Takes the standard input and output of the process; afterwards, its standard streams are the null device.</summary>
    /// <returns>The streams of the protocol.</returns>
    public static (Stream Input, Stream Output) Take()
        => OperatingSystem.IsWindows() ? TakeOnWindows() : TakeOnUnix();

    private static (Stream Input, Stream Output) TakeOnWindows()
    {
        var input = GetStdHandle(StdInput);
        var output = GetStdHandle(StdOutput);
        // Not inherited by what the worker starts, which gets the null device instead.
        SetHandleInformation(input, HandleFlagInherit, 0);
        SetHandleInformation(output, HandleFlagInherit, 0);
        var nul = File.OpenHandle("NUL", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var nulHandle = nul.DangerousGetHandle();
        // The null device stays open as the standard handles of the process for its whole life: nothing closes it.
        nul.SetHandleAsInvalid();
        SetHandleInformation(nulHandle, HandleFlagInherit, HandleFlagInherit);
        SetStdHandle(StdInput, nulHandle);
        SetStdHandle(StdOutput, nulHandle);
        SetStdHandle(StdError, nulHandle);
        return (new FileStream(new SafeFileHandle(input, ownsHandle: false), FileAccess.Read, 1), new FileStream(new SafeFileHandle(output, ownsHandle: false), FileAccess.Write, 1));
    }

    private static (Stream Input, Stream Output) TakeOnUnix()
    {
        const int CloseOnExec = 1;
        const int SetDescriptorFlags = 2;
        // Copies of the protocol descriptors, closed in what the worker executes; then the null device as 0, 1 and 2.
        var input = Dup(0);
        var output = Dup(1);
        _ = Fcntl(input, SetDescriptorFlags, CloseOnExec);
        _ = Fcntl(output, SetDescriptorFlags, CloseOnExec);
        var nul = Open("/dev/null", 2);
        if (input < 0 || output < 0 || nul < 0) throw new IOException("The worker could not take its standard streams.");
        _ = Dup2(nul, 0);
        _ = Dup2(nul, 1);
        _ = Dup2(nul, 2);
        return (new FileStream(new SafeFileHandle(input, ownsHandle: true), FileAccess.Read, 1), new FileStream(new SafeFileHandle(output, ownsHandle: true), FileAccess.Write, 1));
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int standardHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetStdHandle(int standardHandle, nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static partial int Dup(int descriptor);

    [LibraryImport("libc", EntryPoint = "dup2", SetLastError = true)]
    private static partial int Dup2(int descriptor, int target);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int Fcntl(int descriptor, int command, int argument);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);
}
