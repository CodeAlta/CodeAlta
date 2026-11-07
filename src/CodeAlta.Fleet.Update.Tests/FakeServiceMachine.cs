using System.Text;
using CodeAlta.Fleet.Update.Service;

namespace CodeAlta.Fleet.Update.Tests;

/// <summary>
/// A machine for <see cref="ServiceManager"/> that installs nothing (linked into the tests of altanode and altaserver): its folders are temporary, and the commands of
/// its service managers (<c>schtasks</c>, <c>launchctl</c>, <c>systemctl</c>, <c>loginctl</c>) are recorded and answered
/// from a small state of their own.
/// </summary>
internal sealed class FakeServiceMachine : IDisposable
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _registered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _leaving = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _binPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _accounts = new(StringComparer.Ordinal);

    // The command line sc.exe create or config gives a service, which sc.exe qc prints.
    private void KeepBinPath(IReadOnlyList<string> arguments)
    {
        for (var index = 2; index + 1 < arguments.Count; index++)
        {
            if (arguments[index] == "binPath=") _binPaths[arguments[1]] = arguments[index + 1];
            if (arguments[index] == "obj=") _accounts[arguments[1]] = arguments[index + 1];
        }
    }

    public FakeServiceMachine(ServicePlatform platform)
    {
        Platform = platform;
        Root = Path.Combine(Path.GetTempPath(), "CodeAlta-service-" + Guid.NewGuid().ToString("N"));
        Home = Path.Combine(Root, "home", "alice");
        foreach (var directory in new[] { Home, LingerDirectory, SystemUnitDirectory, LaunchDaemonDirectory, TempDirectory }) Directory.CreateDirectory(directory);
    }

    public ServicePlatform Platform { get; }

    public string Root { get; }

    public string Home { get; }

    public string LingerDirectory => Path.Combine(Root, "linger");

    public string SystemUnitDirectory => Path.Combine(Root, "etc", "systemd", "system");

    public string LaunchDaemonDirectory => Path.Combine(Root, "Library", "LaunchDaemons");

    public string TempDirectory => Path.Combine(Root, "tmp");

    /// <summary>Gets or sets whether the process runs as root.</summary>
    public bool IsRoot { get; set; }

    /// <summary>Gets or sets whether <c>loginctl enable-linger</c> succeeds without privileges.</summary>
    public bool LingerAllowed { get; set; } = true;

    /// <summary>Gets or sets the user the machine signs in automatically.</summary>
    public string? AutoLoginUser { get; set; }

    /// <summary>
    /// Gets or sets whether the user is logged in at the screen of the Mac: its <c>gui</c> domain exists; true by default.
    /// </summary>
    public bool GuiSession { get; set; } = true;

    /// <summary>
    /// Gets or sets how many <c>launchctl print</c> a job still answers after its <c>bootout</c>: launchd keeps a job while its
    /// process stops, and refuses to bootstrap it again meanwhile (<c>Bootstrap failed: 5: Input/output error</c>).
    /// </summary>
    public int LaunchdPrintsWhileStopping { get; set; }

    /// <summary>Gets or sets what happens on the machine when a command runs, given as typed: a service that stops on its bootout.</summary>
    public Action<string>? CommandRan { get; set; }

    /// <summary>Gets how long the manager slept between its commands.</summary>
    public TimeSpan Slept { get; private set; }

    /// <summary>Gets the commands run, as typed.</summary>
    public List<string> Commands { get; } = [];

    /// <summary>Gets or sets whether protecting a folder fails, as it does when this process may not change it.</summary>
    public bool ProtectFails { get; set; }

    /// <summary>Gets the logons given to Windows services (service, account, password), through the API, never a command.</summary>
    public List<(string Service, string Account, string Password)> Logons { get; } = [];

    /// <summary>Gets the folder of the data of the system services (<c>%ProgramData%</c>).</summary>
    public string ProgramData => Path.Combine(Root, "ProgramData");

    /// <summary>Gets the task XML files <c>schtasks /Create</c> was given, with the encoding they were written in.</summary>
    public List<(string Text, Encoding Encoding)> TaskFiles { get; } = [];

    public ServiceEnvironment Environment => new()
    {
        Platform = Platform,
        Home = Home,
        UserName = "alice",
        UserDisplayName = Platform == ServicePlatform.Windows ? @"PC\alice" : "alice",
        UserId = Platform == ServicePlatform.Windows ? "S-1-5-21-1-2-3-1001" : IsRoot ? "0" : "501",
        IsRoot = IsRoot,
        ConfigHome = Path.Combine(Home, ".config"),
        LingerDirectory = LingerDirectory,
        SystemUnitDirectory = SystemUnitDirectory,
        LaunchDaemonDirectory = LaunchDaemonDirectory,
        WindowsDirectory = @"C:\Windows",
        TempDirectory = TempDirectory,
        ServiceVariables = Platform == ServicePlatform.Windows
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["PATH"] = "/home/alice/.dotnet/tools:/usr/bin:/bin", ["DOTNET_ROOT"] = "/home/alice/.dotnet" },
        Now = static () => new DateTime(2026, 11, 3, 9, 30, 42, DateTimeKind.Local),
        Run = Run,
        Sleep = delay =>
        {
            lock (_gate) Slept += delay;
        },
        AutoLoginUser = () => AutoLoginUser,
        HomeOfAccount = account => account is "codealta" or @".\codealta" ? Path.Combine(Root, "home", "codealta") : null,
        ProgramData = ProgramData,
        SetServiceLogon = (service, account, password) =>
        {
            // Windows refuses a logon with this password, as it does a wrong one.
            if (password == "wrong") throw new System.ComponentModel.Win32Exception(1326, "The user name or password is incorrect.");
            lock (_gate)
            {
                Logons.Add((service, account, password));
                _accounts[service] = account;
            }
        },
        ReadLines = static path => File.Exists(path) ? File.ReadAllLines(path) : [],
        ProtectFolder = (folder, account) =>
        {
            if (ProtectFails) throw new UnauthorizedAccessException("Access is denied.");
            lock (_gate) Commands.Add($"protect {folder} {account}");
        },
    };

    public bool IsRegistered(string name)
    {
        lock (_gate) return _registered.Contains(name);
    }

    private ServiceCommandResult Run(string fileName, IReadOnlyList<string> arguments)
    {
        lock (_gate)
        {
            Commands.Add(string.Join(' ', new[] { fileName }.Concat(arguments)));
            CommandRan?.Invoke(Commands[^1]);
            switch (fileName, arguments.FirstOrDefault(static argument => !argument.StartsWith("--", StringComparison.Ordinal)))
            {
                case ("schtasks", "/Create"):
                {
                    var file = arguments[4];
                    var bytes = File.ReadAllBytes(file);
                    var encoding = bytes is [0xFF, 0xFE, ..] ? Encoding.Unicode : Encoding.UTF8;
                    TaskFiles.Add((encoding.GetString(bytes).TrimStart('\uFEFF'), encoding));
                    _registered.Add(arguments[2]);
                    return new(0, "SUCCESS");
                }
                case ("schtasks", "/Query"):
                    return _registered.Contains(arguments[2])
                        ? new(0, "<?xml version=\"1.0\"?><Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Settings><Enabled>true</Enabled></Settings></Task>")
                        : new(1, "ERROR: The system cannot find the file specified.");
                case ("schtasks", "/Delete"):
                    return _registered.Remove(arguments[2]) ? new(0, "SUCCESS") : new(1, "ERROR");
                case ("launchctl", "bootstrap"):
                {
                    var label = Path.GetFileNameWithoutExtension(arguments[2]);
                    if (_registered.Contains(label) || _leaving.ContainsKey(label)) return new(5, "Bootstrap failed: 5: Input/output error");
                    _registered.Add(label);
                    return new(0, "");
                }
                case ("launchctl", "bootout"):
                {
                    var label = arguments[1][(arguments[1].LastIndexOf('/') + 1)..];
                    if (!_registered.Remove(label)) return new(113, "Could not find service");
                    if (LaunchdPrintsWhileStopping > 0) _leaving[label] = LaunchdPrintsWhileStopping;
                    return new(0, "");
                }
                case ("launchctl", "print") when arguments[1].Count(static character => character == '/') == 1:
                    // A domain alone (gui/<uid>): the login session of the user, there while someone is logged in at the screen.
                    return GuiSession ? new(0, "type = gui") : new(113, "Could not find domain for port identifier");
                case ("launchctl", "print"):
                {
                    var label = arguments[1][(arguments[1].LastIndexOf('/') + 1)..];
                    if (_registered.Contains(label)) return new(0, "state = running");
                    if (!_leaving.TryGetValue(label, out var left)) return new(113, "Could not find service");
                    if (left <= 1) _leaving.Remove(label);
                    else _leaving[label] = left - 1;
                    return new(0, "state = stopping");
                }
                case ("sc.exe", "create"):
                    if (!_registered.Add(arguments[1])) return new(1073, "[SC] CreateService FAILED 1073: The specified service already exists.");
                    KeepBinPath(arguments);
                    return new(0, "[SC] CreateService SUCCESS");
                case ("sc.exe", "config"):
                    KeepBinPath(arguments);
                    return new(0, "[SC] ChangeServiceConfig SUCCESS");
                case ("sc.exe", "qc"):
                    return _registered.Contains(arguments[1])
                        ? new(0, $"SERVICE_NAME: {arguments[1]}\n        START_TYPE         : 2   AUTO_START  (DELAYED)\n        BINARY_PATH_NAME   : {_binPaths.GetValueOrDefault(arguments[1])}\n        SERVICE_START_NAME : {_accounts.GetValueOrDefault(arguments[1], "LocalSystem")}\n")
                        : new(1060, "[SC] OpenService FAILED 1060: The specified service does not exist as an installed service.");
                case ("sc.exe", "delete"):
                    return _registered.Remove(arguments[1]) ? new(0, "[SC] DeleteService SUCCESS") : new(1060, "[SC] OpenService FAILED 1060");
                case ("systemctl", "enable"):
                    _registered.Add(arguments[^1]);
                    return new(0, "");
                case ("systemctl", "disable"):
                    _registered.Remove(arguments[^1]);
                    return new(0, "");
                case ("systemctl", "is-enabled"):
                    return _registered.Contains(arguments[^1]) ? new(0, "enabled\n") : new(1, "disabled\n");
                case ("loginctl", "enable-linger"):
                    if (!LingerAllowed) return new(1, "Could not enable linger: Access denied");
                    File.WriteAllText(Path.Combine(LingerDirectory, arguments[1]), "");
                    return new(0, "");
                default:
                    return new(0, "");
            }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
