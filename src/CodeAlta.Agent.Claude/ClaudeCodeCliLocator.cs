namespace CodeAlta.Agent.Claude;

/// <summary>
/// The Claude Code executable CodeAlta runs, or why there is none.
/// </summary>
/// <param name="Path">The full path of the executable, when it was found.</param>
/// <param name="Error">What to tell the user when it was not.</param>
internal sealed record ClaudeCodeCliResolution(string? Path, string? Error)
{
    public bool Found => Path is not null;
}

/// <summary>
/// The machine the executable is looked for on. Tests describe another one.
/// </summary>
internal sealed record ClaudeCodeCliEnvironment(
    bool IsWindows,
    string? PathVariable,
    string? HomeDirectory,
    string? RoamingAppData,
    Func<string, bool> FileExists)
{
    public static ClaudeCodeCliEnvironment Current() => new(
        OperatingSystem.IsWindows(),
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        File.Exists);
}

/// <summary>
/// Finds the <c>claude</c> executable the user installed. CodeAlta ships none and never installs one.
/// </summary>
internal static class ClaudeCodeCliLocator
{
    private const string InstallHint =
        "Install Claude Code (https://code.claude.com/docs/en/setup), sign in by running `claude` in a terminal, " +
        "or set `command` of the provider to the path of the executable.";

    public static ClaudeCodeCliResolution Resolve(string? configuredCommand)
        => Resolve(configuredCommand, ClaudeCodeCliEnvironment.Current());

    public static ClaudeCodeCliResolution Resolve(string? configuredCommand, ClaudeCodeCliEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var command = string.IsNullOrWhiteSpace(configuredCommand) ? null : configuredCommand.Trim();
        if (command is not null && IsPath(command))
        {
            var path = ExpandHome(command, environment.HomeDirectory);
            if (!environment.FileExists(path))
            {
                return new(null, $"The Claude Code executable '{path}' set as `command` was not found.");
            }

            return Accept(path, environment);
        }

        var name = command ?? "claude";
        string? shim = null;
        foreach (var candidate in EnumerateCandidates(name, environment, searchInstallFolders: command is null))
        {
            if (!environment.FileExists(candidate))
            {
                continue;
            }

            if (environment.IsWindows && IsBatchScript(candidate))
            {
                // A native executable in a later folder is preferred to a shim in an earlier one.
                shim ??= candidate;
                continue;
            }

            return new(candidate, null);
        }

        return shim is not null
            ? Accept(shim, environment)
            : new(null, $"Claude Code was not found: no `{name}` executable is on PATH. {InstallHint}");
    }

    private static ClaudeCodeCliResolution Accept(string path, ClaudeCodeCliEnvironment environment)
    {
        if (environment.IsWindows && IsBatchScript(path))
        {
            // Windows runs a .cmd through cmd.exe, which parses the whole command line again: an argument
            // cannot be escaped reliably for it. The npm shim is such a script.
            return new(null,
                $"'{path}' is a batch script, which CodeAlta does not run. Install the native Claude Code " +
                "(PowerShell: irm https://claude.ai/install.ps1 | iex) or set `command` of the provider to a claude.exe.");
        }

        return new(path, null);
    }

    private static IEnumerable<string> EnumerateCandidates(string name, ClaudeCodeCliEnvironment environment, bool searchInstallFolders)
    {
        var names = FileNames(name, environment.IsWindows);
        var separator = environment.IsWindows ? ';' : ':';
        foreach (var folder in (environment.PathVariable ?? string.Empty).Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // A relative entry of PATH resolves against the current folder, which is not the user's choice.
            if (!Path.IsPathRooted(folder))
            {
                continue;
            }

            foreach (var fileName in names)
            {
                yield return Path.Combine(folder, fileName);
            }
        }

        if (!searchInstallFolders)
        {
            yield break;
        }

        // A desktop application does not get the PATH of the user's shell: the folders the installers use are
        // looked at as well.
        foreach (var folder in InstallFolders(environment))
        {
            foreach (var fileName in names)
            {
                yield return Path.Combine(folder, fileName);
            }
        }
    }

    private static IEnumerable<string> InstallFolders(ClaudeCodeCliEnvironment environment)
    {
        var home = string.IsNullOrWhiteSpace(environment.HomeDirectory) ? null : environment.HomeDirectory;
        if (environment.IsWindows)
        {
            if (home is not null)
            {
                yield return Path.Combine(home, ".local", "bin");
            }

            if (!string.IsNullOrWhiteSpace(environment.RoamingAppData))
            {
                yield return Path.Combine(environment.RoamingAppData, "npm");
            }

            yield break;
        }

        if (home is not null)
        {
            yield return Path.Combine(home, ".local", "bin");
            yield return Path.Combine(home, ".claude", "local");
        }

        yield return "/opt/homebrew/bin";
        yield return "/usr/local/bin";
        if (home is not null)
        {
            yield return Path.Combine(home, ".npm-global", "bin");
            yield return Path.Combine(home, ".bun", "bin");
            yield return Path.Combine(home, ".volta", "bin");
            yield return Path.Combine(home, ".yarn", "bin");
        }
    }

    private static string[] FileNames(string name, bool isWindows)
    {
        if (!isWindows || Path.HasExtension(name))
        {
            return [name];
        }

        return [name + ".exe", name + ".cmd", name + ".bat"];
    }

    private static bool IsPath(string command)
        => Path.IsPathRooted(command) ||
           command.Contains('/') ||
           command.Contains('\\') ||
           command.StartsWith('~');

    private static string ExpandHome(string path, string? home)
    {
        if (string.IsNullOrWhiteSpace(home) || !path.StartsWith('~'))
        {
            return Path.GetFullPath(path);
        }

        var rest = path[1..].TrimStart('/', '\\');
        return Path.GetFullPath(Path.Combine(home, rest));
    }

    internal static bool IsBatchScript(string path)
    {
        var extension = Path.GetExtension(path.TrimEnd('.', ' '));
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }
}
