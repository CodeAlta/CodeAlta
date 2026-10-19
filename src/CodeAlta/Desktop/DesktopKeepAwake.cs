using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CodeAlta.Desktop;

/// <summary>
/// How the computer is kept from going to sleep on its own: an assertion of the operating system that holds until
/// it is released.
/// </summary>
internal interface IDesktopKeepAwakeBackend
{
    /// <summary>
    /// Asks the system not to sleep while the computer is idle. The display may still turn off and the screen lock;
    /// closing the lid or asking for sleep still sleeps.
    /// </summary>
    /// <param name="reason">Why, as the system's tools show it.</param>
    /// <returns>What releases the assertion; null when the system has none or refused it.</returns>
    IDisposable? Acquire(string reason);
}

/// <summary>
/// Keeps the computer from going to idle sleep while CodeAlta Desktop runs and the user's setting is on, so that
/// automations, reminders, background jobs and running sessions keep going with the window hidden. The setting
/// applies at once; the assertion is released when it is turned off and when the application exits, and the
/// operating system releases it if the process ends first.
/// </summary>
internal sealed class DesktopKeepAwake : IDisposable
{
    /// <summary>What the system's tools (<c>pmset -g assertions</c>, <c>powercfg /requests</c>) show.</summary>
    internal const string Reason = "CodeAlta keeps the computer awake for its sessions, automations and reminders";

    private readonly IDesktopKeepAwakeBackend _backend;
    private readonly string _reason;
    private readonly Action<string> _warn;
    private readonly Lock _gate = new();
    private IDisposable? _assertion;
    private bool _failed;
    private bool _disposed;

    /// <param name="backend">The assertions of the operating system.</param>
    /// <param name="warn">Writes a warning to the log.</param>
    /// <param name="reason">Why, as the system's tools show it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="backend"/> or <paramref name="warn"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is blank.</exception>
    internal DesktopKeepAwake(IDesktopKeepAwakeBackend backend, Action<string> warn, string reason = Reason)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(warn);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _backend = backend;
        _warn = warn;
        _reason = reason;
    }

    /// <summary>The assertions of the operating system the application runs on.</summary>
    internal static IDesktopKeepAwakeBackend PlatformBackend()
        => OperatingSystem.IsMacOS() ? new MacKeepAwake() : OperatingSystem.IsWindows() ? new WindowsKeepAwake() : new LinuxKeepAwake();

    /// <summary>Whether the computer is kept awake now.</summary>
    internal bool IsActive { get { lock (_gate) return _assertion is not null; } }

    /// <summary>
    /// Keeps the computer awake (<paramref name="on"/>) or lets it sleep again. A failure is logged once and leaves
    /// the computer as the system decides; it never stops the application.
    /// </summary>
    internal void Set(bool on)
    {
        IDisposable? released = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (!on)
            {
                (released, _assertion) = (_assertion, null);
            }
            else if (_assertion is null)
            {
                try
                {
                    _assertion = _backend.Acquire(_reason);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    _assertion = null;
                    if (!_failed) _warn($"The computer cannot be kept awake: {exception.Message}");
                    _failed = true;
                }
            }
        }

        Release(released);
    }

    /// <summary>Releases the assertion: the computer sleeps again as the system decides.</summary>
    public void Dispose()
    {
        IDisposable? released;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            (released, _assertion) = (_assertion, null);
        }

        Release(released);
    }

    private void Release(IDisposable? assertion)
    {
        try
        {
            assertion?.Dispose();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _warn($"The computer could not be let sleep again: {exception.Message}");
        }
    }

    // macOS: an IOKit assertion that prevents idle system sleep, not display sleep (`pmset -g assertions`).
    private sealed class MacKeepAwake : IDesktopKeepAwakeBackend
    {
        private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8
        private const uint LevelOn = 255; // kIOPMAssertionLevelOn

        public IDisposable? Acquire(string reason)
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
    private sealed class WindowsKeepAwake : IDesktopKeepAwakeBackend
    {
        private const uint SimpleString = 0x1; // POWER_REQUEST_CONTEXT_SIMPLE_STRING
        private const int SystemRequired = 1; // PowerRequestSystemRequired

        public IDisposable? Acquire(string reason)
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
    private sealed class LinuxKeepAwake : IDesktopKeepAwakeBackend
    {
        public IDisposable? Acquire(string reason)
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
