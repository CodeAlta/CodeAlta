using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Update;
using CodeAlta.Fleet.Update.Service;
using CodeAlta.Hosting;

namespace CodeAlta.Node;

/// <summary>
/// The service that runs the node of a profile and whether it still runs after a restart or a logout, as <c>altanode status</c>
/// says it, for the page of the machine in the desktop (<c>control.status</c>): the system service of <c>install --system</c>
/// when there is one (a Windows service, or a LaunchDaemon or system unit run as this user), else the service of the user.
/// </summary>
internal static class NodeServiceStatus
{
    /// <summary>Reads the service of a profile.</summary>
    /// <param name="profile">The profile of the node.</param>
    /// <param name="environment">The machine; null for this one.</param>
    /// <returns>The service, or null on an OS without a service manager altanode knows.</returns>
    public static FleetNodeServiceStatus? Read(CodeAltaInstanceProfile profile, ServiceEnvironment? environment = null)
        => Read(profile, RunOptions(Environment.GetCommandLineArgs()), environment);

    /// <summary>Reads the service of a profile, with the options of <c>run</c> the command that makes it start at boot keeps.</summary>
    /// <param name="profile">The profile of the node.</param>
    /// <param name="runOptions">The options of <c>run</c> of this node (its port, where it listens, its profile).</param>
    /// <param name="environment">The machine; null for this one.</param>
    /// <returns>The service, or null on an OS without a service manager altanode knows.</returns>
    internal static FleetNodeServiceStatus? Read(CodeAltaInstanceProfile profile, IReadOnlyList<string> runOptions, ServiceEnvironment? environment)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if ((environment ?? ServiceEnvironment.Current()) is not { } env) return null;
        var manager = new ServiceManager(env);
        var defaultProfile = string.Equals(Path.GetFullPath(profile.GlobalRoot), Path.GetFullPath(CodeAltaInstanceProfile.GetDefaultGlobalRoot()),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        var name = ServiceDefinition.ServiceName(UpdateProduct.Node, profile.StateRoot, defaultProfile, profile.IsDeveloper);
        var plan = manager.Plan(new ServiceRequest
        {
            Product = UpdateProduct.Node, Name = name, Executable = new ServiceExecutable(ServiceExecutableKind.Executable, "altanode", []), RunArguments = ["run"],
            StateRoot = profile.StateRoot,
        });
        // The system service of install --system (Always on) first: a user service of the same profile is a leftover. A Windows
        // service of its own account keeps the default name and runs a profile under %ProgramData%: this one, when it names it.
        var facts = manager.QueryInstalled(plan, ServiceDefinition.ServiceName(UpdateProduct.Node, profile.StateRoot, true, profile.IsDeveloper), profile.GlobalRoot);

        var platform = env.Platform switch { ServicePlatform.Windows => "windows", ServicePlatform.MacOS => "macos", _ => "linux" };
        var startsAtBoot = ServiceManager.StartsAtBoot(facts);
        return new FleetNodeServiceStatus(facts.Installed, startsAtBoot, ServiceManager.Describe(facts), platform, facts.User)
        {
            Fix = startsAtBoot ? null : Fix(env, facts, runOptions),
        };
    }

    /// <summary>
    /// The commands, run on the machine, that make its node start at boot as the same person, with the same profile, port and
    /// listening: the node it replaces keeps its identity and its pairings.
    /// </summary>
    /// <param name="env">The machine.</param>
    /// <param name="facts">The service of the node as it is.</param>
    /// <param name="runOptions">The options of <c>run</c> of the node.</param>
    /// <returns>
    /// The commands: for PowerShell on Windows, one per line; for sh elsewhere, chained so that one that fails stops the next.
    /// Null on Windows when a value holds a character cmd.exe reads, which the batch file of a .NET tool would run as a command
    /// whatever PowerShell quotes: the lines of the status say what to run instead.
    /// </returns>
    internal static string? Fix(ServiceEnvironment env, ServiceFacts facts, IReadOnlyList<string> runOptions)
    {
        var windows = env.Platform == ServicePlatform.Windows;
        if (windows && !runOptions.Append(env.UserDisplayName ?? env.UserName).All(NodeSignIns.IsPlainForCmd)) return null;
        // Every value is quoted for the shell it is pasted in, whatever it holds (a name, a folder).
        string Word(string word) => windows ? PowerShellWord(word) : ShellWord(word);
        var options = string.Concat(runOptions.Select(word => " " + Word(word)));
        // The profile every command of the sequence names: the same node, never the default one.
        var profile = string.Concat(ProfileOptions(runOptions).Select(word => " " + Word(word)));
        return env.Platform switch
        {
            // At boot as the person (their password, given to Windows only): a service of its own account would get another profile.
            ServicePlatform.Windows => (facts.Installed ? $"altanode uninstall{profile}\n" : "")
                + $"altanode install --system --account {Word(env.UserDisplayName ?? env.UserName)} --password-stdin{options}",
            // A LaunchDaemon has no Keychain: the key of the secrets moves to a file first, then the LaunchAgent gives way to it,
            // each only once the one before succeeded.
            ServicePlatform.MacOS => $"altanode secrets key-file{profile} && " + (facts.Installed ? $"altanode uninstall{profile} && " : "")
                + $"sudo altanode install --system --account {Word(env.UserName)}{options}",
            _ => facts.Installed ? $"sudo loginctl enable-linger {Word(env.UserName)}" : $"altanode install{options}",
        };
    }

    // The options of run that take a value, whose value is never read as an option (a node named --dev).
    private static readonly System.Collections.Frozen.FrozenSet<string> ValueOptions =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(["--data-root", "--port", "--bind", "--name"], StringComparer.Ordinal);

    // The options of run that choose the profile, as the command line reads them: --data-root and its folder, --dev.
    internal static IEnumerable<string> ProfileOptions(IReadOnlyList<string> runOptions)
    {
        for (var index = 0; index < runOptions.Count; index++)
        {
            var word = runOptions[index];
            if (word == "--dev") yield return word;
            else if (word.StartsWith("--data-root=", StringComparison.Ordinal)) yield return word;
            else if (ValueOptions.Contains(word) && index + 1 < runOptions.Count)
            {
                var value = runOptions[++index];
                if (word == "--data-root") { yield return word; yield return value; }
            }
        }
    }

    // A word as PowerShell reads it: as is when it holds nothing PowerShell reads, else in single quotes, which take it literally.
    private static string PowerShellWord(string word)
        => word.Length > 0 && word.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '\\' or ':' or '=')
            ? word : "'" + word.Replace("'", "''", StringComparison.Ordinal) + "'";

    // The options of run on the command line of this process, without what only the service manager gives (--service).
    internal static IReadOnlyList<string> RunOptions(IReadOnlyList<string> arguments)
    {
        var run = arguments.ToList().IndexOf("run");
        return run < 0 ? [] : [.. arguments.Skip(run + 1).Where(static word => word != ServiceDefinition.WindowsServiceOption)];
    }

    // A word as sh reads it: as is when it holds nothing sh reads, else in single quotes.
    private static string ShellWord(string word)
        => word.Length > 0 && word.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '=' or '@' or ',')
            ? word : "'" + word.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
