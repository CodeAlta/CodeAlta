using System.Runtime.Versioning;
using Microsoft.Win32;

namespace CodeAlta.Desktop.Terminals;

/// <summary>The command interpreters a terminal knows how to start with its integration.</summary>
internal enum TerminalShellKind
{
    /// <summary>A program the terminal starts as it is.</summary>
    Other,
    /// <summary>PowerShell 7 and later (<c>pwsh</c>).</summary>
    PowerShell,
    /// <summary>Windows PowerShell 5.1 (<c>powershell.exe</c>).</summary>
    WindowsPowerShell,
    /// <summary>The Command Prompt of Windows (<c>cmd.exe</c>).</summary>
    CommandPrompt,
    /// <summary>bash, Git Bash included.</summary>
    Bash,
    /// <summary>zsh.</summary>
    Zsh,
    /// <summary>fish.</summary>
    Fish,
}

/// <summary>A program a terminal can start: a command interpreter found on this system.</summary>
/// <param name="Id">A short name that stays the same from one start of the application to the next.</param>
/// <param name="Name">The name shown to the user.</param>
/// <param name="FileName">The full path of the program.</param>
/// <param name="Arguments">The arguments it is started with.</param>
/// <param name="Kind">Which interpreter it is.</param>
internal sealed record TerminalProfile(string Id, string Name, string FileName, IReadOnlyList<string> Arguments, TerminalShellKind Kind);

/// <summary>Finds the command interpreters of this system.</summary>
internal sealed class TerminalProfiles
{
    private readonly Func<string, string?> _variable;
    private readonly Func<string, bool> _exists;
    private readonly Func<IReadOnlyList<string>> _distributions;
    private readonly Func<string, IReadOnlyList<string>> _lines;
    private readonly bool _windows;
    private readonly bool _mac;

    /// <summary>Looks at this system.</summary>
    internal TerminalProfiles()
        : this(Environment.GetEnvironmentVariable, File.Exists, OperatingSystem.IsWindows() ? Distributions : static () => [], ReadLines, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS())
    {
    }

    /// <summary>Looks at a system described by its parts.</summary>
    /// <param name="variable">Gives an environment variable.</param>
    /// <param name="exists">Tells whether a file exists.</param>
    /// <param name="distributions">Gives the names of the Linux distributions installed on Windows.</param>
    /// <param name="lines">Gives the lines of a text file, or none.</param>
    /// <param name="windows">Whether the system is Windows.</param>
    /// <param name="mac">Whether the system is macOS.</param>
    internal TerminalProfiles(Func<string, string?> variable, Func<string, bool> exists, Func<IReadOnlyList<string>> distributions,
        Func<string, IReadOnlyList<string>> lines, bool windows, bool mac)
    {
        (_variable, _exists, _distributions, _lines, _windows, _mac) = (variable, exists, distributions, lines, windows, mac);
    }

    /// <summary>The interpreters found, the one a terminal starts by default first.</summary>
    public IReadOnlyList<TerminalProfile> List() => _windows ? Windows() : Unix();

    /// <summary>The interpreter a terminal starts: the one asked for when it is found, the first one otherwise.</summary>
    /// <returns>The profile, or null on a system where none was found.</returns>
    public TerminalProfile? Choose(string? id)
    {
        var profiles = List();
        return profiles.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)) ?? profiles.FirstOrDefault();
    }

    private List<TerminalProfile> Windows()
    {
        var profiles = new List<TerminalProfile>();
        var system = Path.Combine(_variable("SystemRoot") ?? @"C:\Windows", "System32");
        var home = _variable("USERPROFILE");
        var local = _variable("LOCALAPPDATA");
        string?[] programs = [_variable("ProgramW6432"), _variable("ProgramFiles"), _variable("ProgramFiles(x86)")];

        // PowerShell 7 and later: installed for the machine, from the Store, as a .NET tool, by Scoop.
        var candidates = new List<string>();
        foreach (var folder in programs)
        {
            if (folder is null) continue;
            for (var version = 9; version >= 6; version--) candidates.Add(Path.Combine(folder, "PowerShell", version.ToString(System.Globalization.CultureInfo.InvariantCulture), "pwsh.exe"));
        }
        if (local is not null) candidates.Add(Path.Combine(local, "Microsoft", "WindowsApps", "pwsh.exe"));
        if (home is not null)
        {
            candidates.Add(Path.Combine(home, ".dotnet", "tools", "pwsh.exe"));
            candidates.Add(Path.Combine(home, "scoop", "apps", "pwsh", "current", "pwsh.exe"));
        }
        if (candidates.FirstOrDefault(_exists) is { } pwsh) profiles.Add(new("pwsh", "PowerShell", pwsh, ["-NoLogo"], TerminalShellKind.PowerShell));

        var powershell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (_exists(powershell)) profiles.Add(new("powershell", "Windows PowerShell", powershell, ["-NoLogo"], TerminalShellKind.WindowsPowerShell));
        var command = Path.Combine(system, "cmd.exe");
        if (_exists(command)) profiles.Add(new("cmd", "Command Prompt", command, [], TerminalShellKind.CommandPrompt));

        var git = new List<string>();
        foreach (var folder in programs)
        {
            if (folder is not null) git.Add(Path.Combine(folder, "Git", "bin", "bash.exe"));
        }
        if (local is not null) git.Add(Path.Combine(local, "Programs", "Git", "bin", "bash.exe"));
        if (git.FirstOrDefault(_exists) is { } bash) profiles.Add(new("git-bash", "Git Bash", bash, ["--login", "-i"], TerminalShellKind.Bash));

        var wsl = Path.Combine(system, "wsl.exe");
        if (_exists(wsl))
        {
            foreach (var name in _distributions())
            {
                // Those of Docker Desktop are not for a person.
                if (name.Length is 0 or > 64 || name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)) continue;
                profiles.Add(new("wsl:" + name, name + " (WSL)", wsl, ["-d", name], TerminalShellKind.Other));
            }
        }
        return profiles;
    }

    private List<TerminalProfile> Unix()
    {
        var profiles = new List<TerminalProfile>();
        // The shell of the user first, then those the system lists.
        var candidates = new List<string>();
        if (_variable("SHELL") is { Length: > 0 } own) candidates.Add(own);
        foreach (var line in _lines("/etc/shells"))
        {
            var path = line.Trim();
            if (path.StartsWith('/')) candidates.Add(path);
        }
        candidates.Add("/bin/bash");
        candidates.Add("/bin/zsh");
        candidates.Add("/bin/sh");
        foreach (var path in candidates)
        {
            var name = Path.GetFileName(path);
            if (name.Length == 0 || name is "false" or "nologin" || profiles.Any(profile => profile.Id == name) || !_exists(path)) continue;
            var kind = name switch
            {
                "bash" => TerminalShellKind.Bash,
                "zsh" => TerminalShellKind.Zsh,
                "fish" => TerminalShellKind.Fish,
                "pwsh" => TerminalShellKind.PowerShell,
                _ => TerminalShellKind.Other,
            };
            // On macOS a terminal starts a login shell: an application started from the Dock has not read the profile of the user.
            string[] arguments = kind == TerminalShellKind.PowerShell ? ["-NoLogo"] : _mac && kind is TerminalShellKind.Bash or TerminalShellKind.Zsh or TerminalShellKind.Fish ? ["-l"] : [];
            profiles.Add(new(name, name, path, arguments, kind));
        }
        return profiles;
    }

    private static IReadOnlyList<string> ReadLines(string path)
    {
        try { return File.Exists(path) ? File.ReadAllLines(path) : []; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> Distributions()
    {
        var names = new List<string>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (root is null) return names;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("DistributionName") is string distribution) names.Add(distribution);
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // None can be listed: none is offered.
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }
}
