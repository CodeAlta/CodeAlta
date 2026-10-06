using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CodeAlta.Hosting;

namespace CodeAlta.Desktop;

/// <summary>
/// Gives a terminal its prompt back when <c>alta</c> is started from it. A shell waits for the program it
/// starts, and on Windows the launcher of the installed tool is a script, which waits for a windowed program
/// as well: the terminal stayed busy until CodeAlta exited. Such a start runs the application in a process
/// of its own, away from the terminal, waits until its window is shown, and ends.
/// </summary>
/// <remarks>
/// The two processes meet through a token: an empty file that the start creates and the application removes
/// once its window is shown. <see cref="Variable"/> gives the application its name. A start without a
/// terminal (the Start Menu, the Dock, a desktop entry), <c>alta --wait</c> and a start that cannot be handed
/// over run the application in the process that was started.
/// </remarks>
internal static class DesktopTerminalStart
{
    /// <summary>The environment variable that names, for the application, the token of the start waiting for it.</summary>
    internal const string Variable = "CODEALTA_START_TOKEN";

    private const int StandardInput = -10, StandardOutput = -11, StandardError = -12;

    // How long a start waits for the window before it leaves the application to itself.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // How often the token is looked at while the application starts.
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// In the application started by <see cref="TryHandOver"/>: takes the token and leaves the terminal, before
    /// anything is written or started.
    /// </summary>
    /// <returns>The token; null when this process was started in any other way.</returns>
    internal static string? Adopt()
    {
        var token = Environment.GetEnvironmentVariable(Variable);
        if (token is null) return null;
        // Not for the programs this one starts: an alta started from one of them is a start of its own.
        Environment.SetEnvironmentVariable(Variable, null);
        if (!IsToken(token)) return null;
        if (!OperatingSystem.IsWindows()) LeaveTerminal();
        return token;
    }

    /// <summary>Tells the start that waits for this application that its window is shown.</summary>
    /// <param name="token">The token of that start; null when none waits.</param>
    internal static void NotifyShown(string? token)
    {
        if (token is not null) Remove(TokenPath(token));
    }

    /// <summary>
    /// Whether a start with these options leaves its terminal: the normal starts only (<c>alta</c>,
    /// <c>alta --dev</c>). A start on explicit roots is a test or automation, which owns its process.
    /// </summary>
    internal static bool Applies(DesktopLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options is { Owned.Home: null, ExitRunning: false, Wait: false, StartToken: null };
    }

    /// <summary>
    /// Whether <paramref name="processPath"/> is the application's own executable, which can be started again
    /// as it is. The .NET host running the assembly (<c>dotnet alta.dll</c>) is not.
    /// </summary>
    internal static bool IsApplication(string? processPath) =>
        string.Equals(Path.GetFileNameWithoutExtension(processPath), typeof(DesktopTerminalStart).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Starts the application in a process of its own when this start comes from a terminal, and waits until
    /// its window is shown.
    /// </summary>
    /// <param name="options">The options of this start.</param>
    /// <param name="error">Where a failed start is reported.</param>
    /// <returns>
    /// The exit code of this start: zero once the window is shown, or what the application ended with before
    /// that. Null when this process is to run the application itself.
    /// </returns>
    internal static int? TryHandOver(DesktopLaunchOptions options, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(error);
        // A debugger follows the process it started.
        if (!Applies(options) || Debugger.IsAttached) return null;
        var executable = Environment.ProcessPath;
        if (!IsApplication(executable) || !StartedFromTerminal()) return null;
        var token = Guid.NewGuid().ToString("N");
        using var application = StartApplication(executable!, options.Developer, token);
        if (application is null) return null; // What cannot be handed over runs here.
        int code;
        try { code = Await(token, wait => application.WaitForExit(wait) ? application.ExitCode : null, Patience); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return 0; // It was started, and can no longer be watched.
        }
        if (code != 0) error.WriteLine(Failure(code, Path.Combine(options.DataRoot, "logs"), OperatingSystem.IsWindows()));
        return code;
    }

    // The same executable, options, working directory and environment, with the token of this start.
    private static Process? StartApplication(string executable, bool developer, string token)
    {
        try
        {
            File.Open(TokenPath(token), FileMode.CreateNew).Dispose();
            var start = new ProcessStartInfo(executable)
            {
                // Windows starts it as the Start Menu does: neither the console nor the pipes of a caller
                // that reads this program's output reach the application, which would keep them busy.
                UseShellExecute = OperatingSystem.IsWindows(),
                WorkingDirectory = Environment.CurrentDirectory,
            };
            if (developer) start.ArgumentList.Add(CodeAltaInstanceProfile.DeveloperOption);
            Environment.SetEnvironmentVariable(Variable, token);
            try
            {
                if (Process.Start(start) is { } application) return application;
            }
            finally { Environment.SetEnvironmentVariable(Variable, null); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception
            or InvalidOperationException or NotSupportedException)
        {
            // Nothing was started.
        }
        Remove(TokenPath(token));
        return null;
    }

    /// <summary>
    /// Waits for the application to remove the token of its start, in steps that <paramref name="exited"/>
    /// spends waiting for the application's process.
    /// </summary>
    /// <param name="token">The token of the start.</param>
    /// <param name="exited">Waits the given time for the application to end; its exit code once it has, else null.</param>
    /// <param name="patience">How long to wait at most.</param>
    /// <returns>
    /// Zero when the window is shown or the application is still starting after <paramref name="patience"/>;
    /// otherwise what the application ended with.
    /// </returns>
    internal static int Await(string token, Func<TimeSpan, int?> exited, TimeSpan patience)
    {
        ArgumentNullException.ThrowIfNull(exited);
        var path = TokenPath(token);
        var clock = Stopwatch.StartNew();
        try
        {
            while (File.Exists(path))
            {
                if (exited(Step) is { } code) return code;
                if (clock.Elapsed >= patience) return 0;
            }
            return 0;
        }
        finally { Remove(path); }
    }

    /// <summary>What a start says when the application ended before its window was shown.</summary>
    internal static string Failure(int code, string logs, bool windows) =>
        $"CodeAlta Desktop exited while starting (exit code {code}). Its log is in {logs}."
        // A windowed program writes nothing to a console on Windows.
        + (windows ? string.Empty : " Start it with alta --wait to see what it writes.");

    /// <summary>Whether <paramref name="value"/> has the form of a token: nothing else names a file.</summary>
    internal static bool IsToken(string? value) => value is { Length: 32 } && value.All(char.IsAsciiHexDigitLower);

    /// <summary>The file of a token.</summary>
    /// <exception cref="ArgumentException"><paramref name="token"/> is not a token.</exception>
    internal static string TokenPath(string token)
    {
        if (!IsToken(token)) throw new ArgumentException("A start token is 32 lowercase hexadecimal digits.", nameof(token));
        return Path.Combine(Path.GetTempPath(), "codealta-start-" + token);
    }

    private static void Remove(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The file is empty, and a start stops waiting by itself.
        }
    }

    private static bool StartedFromTerminal()
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? ParentHasConsole()
                : IsTerminal(0) == 1 || IsTerminal(1) == 1 || IsTerminal(2) == 1;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    // A windowed program has no console of its own: it is started from a terminal when what started it has
    // one. Attaching to a console replaces the standard handles, so they are put back as they were (a caller
    // that reads this program's output gave it pipes), and the console is left at once.
    [SupportedOSPlatform("windows")]
    private static bool ParentHasConsole()
    {
        var input = GetStdHandle(StandardInput);
        var output = GetStdHandle(StandardOutput);
        var error = GetStdHandle(StandardError);
        if (!AttachConsole(uint.MaxValue /* ATTACH_PARENT_PROCESS */)) return false;
        _ = FreeConsole();
        _ = SetStdHandle(StandardInput, input);
        _ = SetStdHandle(StandardOutput, output);
        _ = SetStdHandle(StandardError, error);
        return true;
    }

    // A session of its own: the terminal's signals (its closing, Ctrl+C) are not for this process. Nothing is
    // read from the terminal or written to it either, by this process or by those it starts; what the caller
    // redirected stays where it was sent.
    [UnsupportedOSPlatform("windows")]
    private static void LeaveTerminal()
    {
        try
        {
            _ = StartSession();
            using var none = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            for (var descriptor = 0; descriptor <= 2; descriptor++)
            {
                if (IsTerminal(descriptor) == 1) _ = Duplicate((int)none.DangerousGetHandle(), descriptor);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            // It stays as it was started.
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetStdHandle(int handle);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int handle, nint value);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("libc", EntryPoint = "isatty")]
    private static extern int IsTerminal(int descriptor);

    [DllImport("libc", EntryPoint = "setsid")]
    private static extern int StartSession();

    [DllImport("libc", EntryPoint = "dup2")]
    private static extern int Duplicate(int from, int to);
}
