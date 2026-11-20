using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>
/// Prepares the .NET installation CodeAlta runs on for its next major version, which runs on .NET 11. That version is
/// installed by <c>dotnet tool update</c> with the .NET SDK of this installation, which must then be a .NET 11 SDK: a
/// .NET 10 SDK cannot install a tool built for .NET 11. The .NET 11 SDK, with its runtimes, is installed where CodeAlta's
/// own .NET is: with Microsoft's dotnet-install script when the user can write there, without asking anything; with
/// Microsoft's installer otherwise, which asks the system for permission (UAC on Windows, the password of an
/// administrator on macOS). A system installation of Linux belongs to its package manager: the user installs it there.
/// </summary>
internal sealed class NextDotnet
{
    /// <summary>The channel of the .NET the next major version of CodeAlta runs on.</summary>
    internal const string Channel = "11.0";

    /// <summary>The major version of that .NET.</summary>
    internal const int Major = 11;

    private const string LoggerName = "CodeAlta.Desktop.NextDotnet";
    private readonly string? _root;
    private readonly Func<string, bool> _writable;
    private readonly Func<NextDotnetInstall, CancellationToken, Task<NextDotnetOutcome>> _run;
    private readonly Lock _gate = new();
    private Task<NextDotnetState>? _installing;
    private NextDotnetState? _last;

    /// <summary>Creates the preparation of the .NET installation this process runs on.</summary>
    internal NextDotnet() : this(DesktopIntegration.DotnetRoot(), Writable, RunAsync)
    {
    }

    /// <param name="root">The folder of the .NET installation; null when this process does not run on one (a published app).</param>
    /// <param name="writable">Whether the user can write in a folder without asking the system.</param>
    /// <param name="run">Runs an installation; its tests give their own.</param>
    internal NextDotnet(string? root, Func<string, bool> writable, Func<NextDotnetInstall, CancellationToken, Task<NextDotnetOutcome>> run)
    {
        ArgumentNullException.ThrowIfNull(writable);
        ArgumentNullException.ThrowIfNull(run);
        _root = root;
        _writable = writable;
        _run = run;
    }

    /// <summary>
    /// What the installation has: <c>installed</c> when it holds a .NET 11 SDK (nothing to do), <c>missing</c> with where
    /// it would be installed, <c>installing</c>, <c>failed</c> or <c>canceled</c> after an installation of this run that
    /// did not complete, <c>manual</c> for a system installation of Linux, and <c>unavailable</c> where CodeAlta does
    /// not run on an installation of .NET.
    /// </summary>
    internal NextDotnetState State()
    {
        if (_root is null) return new("unavailable", null);
        lock (_gate)
        {
            if (_installing is { IsCompleted: false }) return new("installing", ScopeOf(_root));
        }

        if (HasSdk(_root)) return new("installed", ScopeOf(_root));
        var scope = ScopeOf(_root);
        if (scope == "system" && OperatingSystem.IsLinux()) return new("manual", scope);
        lock (_gate) return _last is { Status: "failed" or "canceled" } last ? last : new("missing", scope);
    }

    /// <summary>
    /// Installs the .NET 11 SDK in the installation, once at a time: a second call joins the one that runs. Ends with the
    /// state after it (<see cref="State"/>).
    /// </summary>
    /// <param name="cancellationToken">Stops waiting; an installation that started goes on.</param>
    internal Task<NextDotnetState> InstallAsync(CancellationToken cancellationToken)
    {
        Task<NextDotnetState> installing;
        lock (_gate)
        {
            if (_installing is not { IsCompleted: false })
            {
                var state = State();
                if (state.Status is not ("missing" or "failed" or "canceled") || _root is null) return Task.FromResult(state);
                _installing = InstallCoreAsync(_root, state.Scope!);
            }

            installing = _installing;
        }

        return installing.WaitAsync(cancellationToken);
    }

    private async Task<NextDotnetState> InstallCoreAsync(string root, string scope)
    {
        // Not on the caller's thread: a download of a few hundred megabytes and an installer.
        await Task.Yield();
        NextDotnetOutcome outcome;
        try
        {
            outcome = await _run(Plan(root, scope), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or Win32Exception or InvalidOperationException
            or UnauthorizedAccessException or TaskCanceledException)
        {
            outcome = new(NextDotnetResult.Failed, exception.Message);
        }

        if (LogManager.IsInitialized)
            LogManager.GetLogger(LoggerName).Info($"The .NET {Channel} SDK in {root} ({scope}): {outcome.Result}{(outcome.Detail is { Length: > 0 } detail ? " - " + detail : "")}");
        var state = outcome.Result switch
        {
            NextDotnetResult.Canceled => new NextDotnetState("canceled", scope),
            // An installer that says it succeeded and left no SDK did not install it.
            _ when HasSdk(root) => new NextDotnetState("installed", scope),
            _ => new NextDotnetState("failed", scope) { Detail = outcome.Detail ?? $"No .NET {Channel} SDK is in {root} after the installation." },
        };
        lock (_gate) _last = state;
        return state;
    }

    /// <summary>What installs the SDK in an installation: the dotnet-install script, or the installer of the system.</summary>
    internal static NextDotnetInstall Plan(string root, string scope)
    {
        ArgumentNullException.ThrowIfNull(root);
        // The architecture of this process is the one of the installation it runs on (an x64 .NET of an Arm64 Windows).
        var arch = RuntimeInformation.ProcessArchitecture switch { Architecture.Arm64 => "arm64", Architecture.X86 => "x86", _ => "x64" };
        if (scope == "local")
        {
            return OperatingSystem.IsWindows()
                ? new(NextDotnetKind.Script, "https://dot.net/v1/dotnet-install.ps1", "dotnet-install.ps1", root, arch)
                : new(NextDotnetKind.Script, "https://dot.net/v1/dotnet-install.sh", "dotnet-install.sh", root, arch);
        }

        return OperatingSystem.IsMacOS()
            ? new(NextDotnetKind.MacInstaller, $"https://aka.ms/dotnet/{Channel}/dotnet-sdk-osx-{arch}.pkg", $"dotnet-sdk-{Channel}-osx-{arch}.pkg", root, arch)
            : new(NextDotnetKind.WindowsInstaller, $"https://aka.ms/dotnet/{Channel}/dotnet-sdk-win-{arch}.exe", $"dotnet-sdk-{Channel}-win-{arch}.exe", root, arch);
    }

    /// <summary>The program and the arguments that run a downloaded installation, as given to a process.</summary>
    internal static (string Program, IReadOnlyList<string> Arguments) Command(NextDotnetInstall install, string file)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(file);
        return install.Kind switch
        {
            NextDotnetKind.Script when OperatingSystem.IsWindows() => ("powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file, "-Channel", Channel, "-InstallDir", install.Root, "-Architecture", install.Architecture, "-NoPath", "-SkipNonVersionedFiles"]),
            // The dotnet of the installation stays as it is: it may be running, and the newest host it finds runs the SDK 11.
            NextDotnetKind.Script => ("/bin/bash", [file, "--channel", Channel, "--install-dir", install.Root, "--architecture", install.Architecture, "--no-path", "--skip-non-versioned-files"]),
            NextDotnetKind.WindowsInstaller => (file, ["/install", "/quiet", "/norestart"]),
            // The installer of macOS runs as root once an administrator gave their password in the dialog of the system.
            NextDotnetKind.MacInstaller => ("/usr/bin/osascript",
                ["-e", $"do shell script \"/usr/sbin/installer -pkg \" & quoted form of \"{AppleScriptText(file)}\" & \" -target /\" with administrator privileges"]),
            _ => throw new ArgumentOutOfRangeException(nameof(install)),
        };
    }

    /// <summary>Whether an installation holds a released .NET 11 SDK.</summary>
    internal static bool HasSdk(string root)
    {
        var sdks = Path.Combine(root, "sdk");
        if (!Directory.Exists(sdks)) return false;
        foreach (var folder in Directory.EnumerateDirectories(sdks))
        {
            // A preview (11.0.100-rc.2) does not install a released tool.
            var name = Path.GetFileName(folder);
            if (!name.Contains('-') && Version.TryParse(name, out var version) && version.Major == Major
                && File.Exists(Path.Combine(folder, "dotnet.dll"))) return true;
        }

        return false;
    }

    private string ScopeOf(string root) => _writable(root) ? "local" : "system";

    // Whether the user writes in the folder without asking the system: a file is made and removed there.
    private static bool Writable(string folder)
    {
        var probe = Path.Combine(folder, $".codealta-{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static string AppleScriptText(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    // Downloads the script or the installer in a folder of its own, runs it, and removes the folder.
    private static async Task<NextDotnetOutcome> RunAsync(NextDotnetInstall install, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "codealta-dotnet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, install.FileName);
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            await using (var target = File.Create(file))
            {
                using var response = await http.GetAsync(install.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await response.Content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            var (program, arguments) = Command(install, file);
            var start = new ProcessStartInfo(program) { UseShellExecute = install.Kind == NextDotnetKind.WindowsInstaller, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            if (install.Kind == NextDotnetKind.WindowsInstaller) start.Verb = "runas";
            else { start.RedirectStandardOutput = true; start.RedirectStandardError = true; }
            Process? process;
            try
            {
                process = Process.Start(start);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED: the user said no to the question of Windows.
                return new(NextDotnetResult.Canceled, null);
            }

            if (process is null) return new(NextDotnetResult.Failed, $"{program} did not start.");
            using (process)
            {
                var output = start.RedirectStandardError ? process.StandardOutput.ReadToEndAsync(cancellationToken) : Task.FromResult(string.Empty);
                var error = start.RedirectStandardError ? process.StandardError.ReadToEndAsync(cancellationToken) : Task.FromResult(string.Empty);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var said = (await error.ConfigureAwait(false)).Trim();
                _ = await output.ConfigureAwait(false);
                // 3010: installed, a restart of Windows completes it. -128: the user closed the password dialog of macOS.
                if (process.ExitCode is 0 or 3010) return new(NextDotnetResult.Completed, null);
                if (install.Kind == NextDotnetKind.WindowsInstaller && process.ExitCode == 1602 || said.Contains("(-128)", StringComparison.Ordinal))
                    return new(NextDotnetResult.Canceled, null);
                return new(NextDotnetResult.Failed, $"{Path.GetFileName(program)} ended with {process.ExitCode}{(said.Length > 0 ? ": " + Last(said) : "")}");
            }
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* A file the installer still holds: the temporary folder is cleaned later. */ }
        }
    }

    private static string Last(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? text : lines[^1];
    }
}

/// <summary>How the .NET 11 SDK is installed: Microsoft's script, or the installer of Windows or macOS.</summary>
internal enum NextDotnetKind
{
    /// <summary>dotnet-install, in a folder the user writes in.</summary>
    Script,

    /// <summary>The installer of Windows, which asks for permission.</summary>
    WindowsInstaller,

    /// <summary>The package of macOS, which asks for the password of an administrator.</summary>
    MacInstaller,
}

/// <summary>An installation of the .NET 11 SDK: what is downloaded, and in which .NET installation it goes.</summary>
/// <param name="Kind">How it installs.</param>
/// <param name="Url">The address of the script or the installer.</param>
/// <param name="FileName">The name it is saved under.</param>
/// <param name="Root">The .NET installation.</param>
/// <param name="Architecture">The architecture of the system (<c>x64</c>, <c>arm64</c>).</param>
internal sealed record NextDotnetInstall(NextDotnetKind Kind, string Url, string FileName, string Root, string Architecture);

/// <summary>How an installation ended.</summary>
internal enum NextDotnetResult
{
    /// <summary>The program ended without an error.</summary>
    Completed,

    /// <summary>The user declined the question of the system.</summary>
    Canceled,

    /// <summary>The download or the program failed.</summary>
    Failed,
}

/// <summary>How an installation ended, with what the program said when it failed.</summary>
internal sealed record NextDotnetOutcome(NextDotnetResult Result, string? Detail);

/// <summary>The state of the .NET 11 SDK in the installation CodeAlta runs on (<see cref="NextDotnet.State"/>).</summary>
/// <param name="Status"><c>installed</c>, <c>missing</c>, <c>installing</c>, <c>failed</c>, <c>canceled</c>, <c>manual</c> or <c>unavailable</c>.</param>
/// <param name="Scope"><c>local</c> when the user writes in the installation, <c>system</c> when it belongs to the system; null when unavailable.</param>
internal sealed record NextDotnetState(string Status, string? Scope)
{
    /// <summary>What the program said when an installation failed.</summary>
    public string? Detail { get; init; }
}
