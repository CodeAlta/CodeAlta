using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CodeAlta.Core.Power;

/// <summary>
/// Keeps the computer from going to sleep on its own: an assertion of the operating system that holds until it is
/// released, and that the system releases when the process ends. The display may still turn off and the screen lock;
/// closing the lid or asking for sleep still sleeps.
/// </summary>
public static class SleepAssertion
{
    /// <summary>Asks the system not to sleep while the computer is idle.</summary>
    /// <param name="reason">Why, as the system's tools show it (<c>pmset -g assertions</c>, <c>powercfg /requests</c>, <c>systemd-inhibit --list</c>).</param>
    /// <returns>What releases the assertion.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is blank.</exception>
    /// <exception cref="InvalidOperationException">The system has no such assertion or refused it.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Windows refused the power request.</exception>
    public static IDisposable Acquire(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (OperatingSystem.IsMacOS()) return Mac.Acquire(reason);
        if (OperatingSystem.IsWindows()) return Windows.Acquire(reason);
        return Linux.Acquire(reason);
    }

    // macOS: an IOKit assertion that prevents idle system sleep, not display sleep (`pmset -g assertions`).
    private static class Mac
    {
        private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8
        private const uint LevelOn = 255; // kIOPMAssertionLevelOn

        public static IDisposable Acquire(string reason)
        {
            var type = CFStringCreateWithCString(0, "PreventUserIdleSystemSleep", Utf8);
            var name = CFStringCreateWithCString(0, reason, Utf8);
            try
            {
                if (type == 0 || name == 0) throw new InvalidOperationException("CoreFoundation did not create the assertion's strings.");
                var result = IOPMAssertionCreateWithName(type, LevelOn, name, out var id);
                if (result != 0) throw new InvalidOperationException($"IOPMAssertionCreateWithName failed with 0x{result:X8}.");
                return new Assertion(id);
            }
            finally
            {
                if (name != 0) CFRelease(name);
                if (type != 0) CFRelease(type);
            }
        }

        private sealed class Assertion(uint id) : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0) _ = IOPMAssertionRelease(id);
            }
        }

        [DllImport(IOKit)]
        private static extern int IOPMAssertionCreateWithName(nint type, uint level, nint name, out uint id);

        [DllImport(IOKit)]
        private static extern int IOPMAssertionRelease(uint id);

        [DllImport(CoreFoundation)]
        private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(nint value);
    }

    // Windows: a power request, which belongs to no thread and shows in `powercfg /requests`.
    [SupportedOSPlatform("windows")]
    private static class Windows
    {
        private const uint SimpleString = 0x1; // POWER_REQUEST_CONTEXT_SIMPLE_STRING
        private const int SystemRequired = 1; // PowerRequestSystemRequired

        public static IDisposable Acquire(string reason)
        {
            var text = Marshal.StringToHGlobalUni(reason);
            try
            {
                var context = new ReasonContext { Version = 0, Flags = SimpleString, Reason = text };
                var request = PowerCreateRequest(ref context);
                if (request == 0 || request == -1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                if (!PowerSetRequest(request, SystemRequired))
                {
                    var error = Marshal.GetLastPInvokeError();
                    CloseHandle(request);
                    throw new System.ComponentModel.Win32Exception(error);
                }

                return new Request(request);
            }
            finally
            {
                Marshal.FreeHGlobal(text);
            }
        }

        private sealed class Request(nint handle) : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0) return;
                PowerClearRequest(handle, SystemRequired);
                CloseHandle(handle);
            }
        }

        // REASON_CONTEXT with a simple reason string.
        [StructLayout(LayoutKind.Sequential)]
        private struct ReasonContext
        {
            public uint Version;
            public uint Flags;
            public nint Reason;
        }

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern nint PowerCreateRequest(ref ReasonContext context);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PowerSetRequest(nint request, int type);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PowerClearRequest(nint request, int type);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);
    }

    // Linux, as far as systemd goes: an inhibitor held by a `systemd-inhibit` that waits on `cat`, whose input is
    // this process. It ends when it is released, and when this process ends, since its input closes then.
    private static class Linux
    {
        public static IDisposable Acquire(string reason)
        {
            var start = new ProcessStartInfo("systemd-inhibit")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            foreach (var argument in new[] { "--what=idle:sleep", "--who=CodeAlta", "--why=" + reason, "--mode=block", "cat" }) start.ArgumentList.Add(argument);
            Process process;
            try
            {
                process = Process.Start(start) ?? throw new InvalidOperationException("systemd-inhibit did not start.");
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                throw new InvalidOperationException($"systemd-inhibit is unavailable ({exception.Message}).", exception);
            }

            // Nothing is read from it, but its pipes must not fill up.
            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (process.WaitForExit(200))
            {
                var code = process.ExitCode;
                process.Dispose();
                throw new InvalidOperationException($"systemd-inhibit exited with code {code}.");
            }

            return new Inhibitor(process);
        }

        private sealed class Inhibitor(Process process) : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0) return;
                try
                {
                    process.StandardInput.Close(); // cat ends, and with it the inhibitor.
                    if (!process.WaitForExit(2_000)) process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
                {
                    // It has ended already.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }
}
