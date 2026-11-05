using System.Diagnostics;
using System.Runtime.Versioning;

namespace CodeAlta.Fleet.Update.Service;

/// <summary>How a command of the service manager ended.</summary>
/// <param name="ExitCode">Its exit code; -1 when it could not start.</param>
/// <param name="Output">What it wrote, standard output then standard error.</param>
public sealed record ServiceCommandResult(int ExitCode, string Output)
{
    /// <summary>Gets whether it succeeded.</summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// The machine <c>altanode install</c> and <c>altaserver install</c> work on: the OS, the user, the folders the service
/// managers read, and how their commands run. Tests give one of their own, with temporary folders and recorded commands,
/// so that nothing is installed.
/// </summary>
public sealed class ServiceEnvironment
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Gets the OS.</summary>
    public required ServicePlatform Platform { get; init; }

    /// <summary>Gets the home folder of the user.</summary>
    public required string Home { get; init; }

    /// <summary>Gets the name of the user (<c>alice</c>), as lingering and automatic login name them.</summary>
    public required string UserName { get; init; }

    /// <summary>Gets the user as people read it (<c>DOMAIN\alice</c> on Windows); the user name elsewhere.</summary>
    public string? UserDisplayName { get; init; }

    /// <summary>Gets the id of the user: the SID on Windows, the uid elsewhere; null when unknown.</summary>
    public string? UserId { get; init; }

    /// <summary>Gets whether the process runs as root (<c>sudo</c>), which a system service needs.</summary>
    public bool IsRoot { get; init; }

    /// <summary>Gets the configuration folder of the user (<c>XDG_CONFIG_HOME</c>, else <c>~/.config</c>).</summary>
    public string? ConfigHome { get; init; }

    /// <summary>Gets the folder where systemd-logind marks the users whose services run without a login.</summary>
    public string LingerDirectory { get; init; } = "/var/lib/systemd/linger";

    /// <summary>Gets the folder of the system units.</summary>
    public string SystemUnitDirectory { get; init; } = "/etc/systemd/system";

    /// <summary>Gets the folder of the LaunchDaemons.</summary>
    public string LaunchDaemonDirectory { get; init; } = "/Library/LaunchDaemons";

    /// <summary>Gets the Windows folder (<c>C:\Windows</c>), where <c>conhost.exe</c> is.</summary>
    public string? WindowsDirectory { get; init; }

    /// <summary>Gets the folder of temporary files.</summary>
    public string TempDirectory { get; init; } = Path.GetTempPath();

    /// <summary>Gets the variables the service is given, read from this process: <c>PATH</c> (agents find git, gh, dotnet and node there) and <c>DOTNET_ROOT</c>.</summary>
    public IReadOnlyDictionary<string, string> ServiceVariables { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the clock of the definitions, local time (the start of a repeating task).</summary>
    public Func<DateTime> Now { get; init; } = static () => DateTime.Now;

    /// <summary>Gets what runs a command of the service manager and waits for it.</summary>
    public required Func<string, IReadOnlyList<string>, ServiceCommandResult> Run { get; init; }

    /// <summary>Gets what waits between two commands, such as two reads of a service that stops.</summary>
    public Action<TimeSpan> Sleep { get; init; } = static delay => Thread.Sleep(delay);

    /// <summary>Gets what reads the user the machine signs in automatically at startup, or null.</summary>
    public Func<string?> AutoLoginUser { get; init; } = static () => null;

    /// <summary>Gets what reads the home folder of an account, or null when there is no such account.</summary>
    public Func<string, string?> HomeOfAccount { get; init; } = static _ => null;

    /// <summary>Reads the machine this process runs on.</summary>
    /// <returns>The environment, or null on an OS without a known service manager.</returns>
    public static ServiceEnvironment? Current()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "PATH", "DOTNET_ROOT" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) variables[name] = value;
        }

        if (OperatingSystem.IsWindows())
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new ServiceEnvironment
            {
                Platform = ServicePlatform.Windows, Home = home, UserName = Environment.UserName, UserDisplayName = identity.Name,
                UserId = identity.User?.Value, WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                // The task gets the environment of the user's logon: nothing to copy.
                Run = RunProcess, AutoLoginUser = ReadWindowsAutoLogin,
            };
        }

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return null;
        var uid = RunProcess("id", ["-u"]) is { Succeeded: true } id ? id.Output.Trim() : null;
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg && Path.IsPathFullyQualified(xdg) ? xdg : Path.Combine(home, ".config");
        return new ServiceEnvironment
        {
            Platform = OperatingSystem.IsMacOS() ? ServicePlatform.MacOS : ServicePlatform.Linux, Home = home, UserName = Environment.UserName,
            UserId = uid, IsRoot = uid == "0", ConfigHome = configHome, ServiceVariables = variables, Run = RunProcess,
            AutoLoginUser = OperatingSystem.IsMacOS() ? ReadMacAutoLogin : static () => null,
            HomeOfAccount = OperatingSystem.IsMacOS() ? ReadMacHome : ReadLinuxHome,
        };
    }

    /// <summary>Runs a command and waits for it, with a bound.</summary>
    /// <param name="fileName">The command.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>How it ended.</returns>
    public static ServiceCommandResult RunProcess(string fileName, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return new ServiceCommandResult(-1, $"{fileName} did not start.");
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(CommandTimeout))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                return new ServiceCommandResult(-1, $"{fileName} did not end within {CommandTimeout.TotalSeconds} s.");
            }

            process.WaitForExit();
            return new ServiceCommandResult(process.ExitCode, (output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult()).Trim());
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ServiceCommandResult(-1, $"{fileName} could not start: {exception.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadWindowsAutoLogin()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
            return key?.GetValue("AutoAdminLogon") as string == "1" && key.GetValue("DefaultUserName") is string { Length: > 0 } user ? user : null;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string? ReadMacAutoLogin()
        => RunProcess("defaults", ["read", "/Library/Preferences/com.apple.loginwindow", "autoLoginUser"]) is { Succeeded: true } result
           && result.Output.Trim() is { Length: > 0 } user ? user : null;

    private static string? ReadMacHome(string account)
    {
        var result = RunProcess("dscl", [".", "-read", "/Users/" + account, "NFSHomeDirectory"]);
        if (!result.Succeeded) return null;
        var value = result.Output.Split(':', 2) is [_, var path] ? path.Trim() : null;
        return value is { Length: > 0 } && Path.IsPathFullyQualified(value) ? value : null;
    }

    private static string? ReadLinuxHome(string account)
    {
        var result = RunProcess("getent", ["passwd", account]);
        if (!result.Succeeded) return null;
        var fields = result.Output.Split('\n')[0].Split(':');
        return fields.Length >= 6 && Path.IsPathFullyQualified(fields[5]) ? fields[5] : null;
    }
}
