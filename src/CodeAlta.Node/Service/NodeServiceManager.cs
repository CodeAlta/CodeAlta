using System.Text;
using System.Xml.Linq;

namespace CodeAlta.Node.Service;

/// <summary>A service <c>altanode install</c> refuses to set up, and why, for a person.</summary>
internal sealed class NodeServiceException(string message) : Exception(message);

/// <summary>What <c>altanode install</c> and <c>uninstall</c> are asked for.</summary>
internal sealed record NodeServiceRequest
{
    /// <summary>Gets the name of the service of the profile (<see cref="NodeServiceDefinition.ServiceName"/>).</summary>
    public required string Name { get; init; }

    /// <summary>Gets whom it runs as.</summary>
    public NodeServiceScope Scope { get; init; } = NodeServiceScope.User;

    /// <summary>Gets the account of a system service.</summary>
    public string? Account { get; init; }

    /// <summary>Gets the executable the service starts.</summary>
    public required NodeExecutable Executable { get; init; }

    /// <summary>Gets the arguments of <c>run</c>, <c>run</c> first.</summary>
    public required IReadOnlyList<string> RunArguments { get; init; }

    /// <summary>Gets the state root of the profile, for a service of the user; null for a system service of the default profile.</summary>
    public string? StateRoot { get; init; }

    /// <summary>Gets the state root below the home of the account, for a system service without <c>--data-root</c> (<c>.alta</c> or <c>.alta/dev</c>).</summary>
    public string? StateRootInHome { get; init; }
}

/// <summary>One command of the service manager a plan runs.</summary>
/// <param name="FileName">The command.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="MayFail">Whether its failure is expected (stopping a service that does not run).</param>
internal sealed record NodeServiceCommand(string FileName, IReadOnlyList<string> Arguments, bool MayFail = false)
{
    /// <summary>Gets the command as a person would type it.</summary>
    public override string ToString() => string.Join(' ', new[] { FileName }.Concat(Arguments).Select(static word => word.Contains(' ', StringComparison.Ordinal) ? $"\"{word}\"" : word));
}

/// <summary>What installing or removing the service of a node does on this machine.</summary>
internal sealed record NodeServicePlan
{
    /// <summary>Gets the OS.</summary>
    public required NodeServicePlatform Platform { get; init; }

    /// <summary>Gets the service.</summary>
    public required NodeServiceSpec Spec { get; init; }

    /// <summary>Gets what the service is, for a person (<c>a task at logon of alice</c>).</summary>
    public required string Kind { get; init; }

    /// <summary>Gets where it is registered: the task, the property list or the unit.</summary>
    public required string Location { get; init; }

    /// <summary>Gets the file of the definition: the unit, the property list, or the task XML given to <c>schtasks</c> and deleted after.</summary>
    public required string DefinitionPath { get; init; }

    /// <summary>Gets whether the definition is a temporary file, given to the service manager then deleted (the task XML).</summary>
    public bool DefinitionIsTemporary { get; init; }

    /// <summary>Gets the definition.</summary>
    public required string Definition { get; init; }

    /// <summary>Gets the encoding of the definition file (UTF-16 for Task Scheduler).</summary>
    public Encoding DefinitionEncoding { get; init; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Gets the commands that register and start the service, once the definition is written.</summary>
    public required IReadOnlyList<NodeServiceCommand> Install { get; init; }

    /// <summary>Gets the commands that stop and unregister it, before the definition is deleted.</summary>
    public required IReadOnlyList<NodeServiceCommand> Uninstall { get; init; }

    /// <summary>Gets the command that tells whether it is registered, or null when the definition file tells.</summary>
    public NodeServiceCommand? Query { get; init; }

    /// <summary>
    /// Gets whether removing the service leaves its node running, for <c>altanode uninstall</c> to stop it: Task Scheduler
    /// ends <c>conhost.exe</c>, not the node under it. launchd and systemd stop the node with SIGTERM, which it drains.
    /// </summary>
    public bool LeavesNodeRunning { get; init; }

    /// <summary>Gets the user whose lingering a <c>systemd --user</c> unit needs, or null.</summary>
    public string? LingerUser { get; init; }

    /// <summary>Gets what a person should know before relying on the service (warnings).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>What the service of a node is on this machine, as <c>altanode status</c> reads it.</summary>
/// <param name="Platform">The OS.</param>
/// <param name="Scope">Whom it runs as.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Location">Where it is registered.</param>
/// <param name="User">The user (or the account) it runs as.</param>
/// <param name="Installed">Whether it is registered.</param>
/// <param name="Enabled">Whether the service manager will start it (a task enabled, an agent loaded, a unit enabled).</param>
/// <param name="Lingering">On Linux, whether the services of the user run without a login; null elsewhere.</param>
/// <param name="AutoLoginUser">The user the machine signs in automatically at startup, or null.</param>
internal sealed record NodeServiceFacts(NodeServicePlatform Platform, NodeServiceScope Scope, string Kind, string Location, string User, bool Installed,
    bool Enabled, bool? Lingering, string? AutoLoginUser);

/// <summary>Installs, removes and reads the service of a node with the service manager of the OS.</summary>
internal sealed class NodeServiceManager(NodeServiceEnvironment environment)
{
    /// <summary>Gets the machine the manager works on.</summary>
    public NodeServiceEnvironment Environment { get; } = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>Plans the service of a node: its definition and the commands that register it.</summary>
    /// <param name="request">The service.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="NodeServiceException">This OS or this account cannot have that service.</exception>
    public NodeServicePlan Plan(NodeServiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var env = Environment;
        var system = request.Scope == NodeServiceScope.System;
        var notes = new List<string>();
        string workingDirectory = env.Home, stateRoot = request.StateRoot ?? "";
        if (system)
        {
            if (env.Platform == NodeServicePlatform.Windows)
            {
                throw new NodeServiceException("altanode install --system is not available on Windows yet: a Windows service runs in Session 0, apart from "
                    + "the logins of a user, and CodeAlta Node does not run as a Windows service yet. On a machine that uses deploy keys only, sign in "
                    + "as the account that should run the node and use altanode install --user there.");
            }

            if (!NodeServiceDefinition.IsValidAccount(request.Account))
                throw new NodeServiceException($"--system needs --account <name>, the account the node runs as: '{request.Account}' is not an account name.");
            if (!env.IsRoot)
            {
                throw new NodeServiceException(env.Platform == NodeServicePlatform.MacOS
                    ? "A LaunchDaemon is installed by root: run it again with sudo."
                    : "A system unit is installed by root: run it again with sudo.");
            }

            var home = env.HomeOfAccount(request.Account!) ?? throw new NodeServiceException($"No account named '{request.Account}' exists on this machine: create it first.");
            workingDirectory = home;
            stateRoot = request.StateRoot ?? Path.Combine(home, request.StateRootInHome ?? ".alta");
            if (request.Executable.Path.StartsWith(env.Home, StringComparison.Ordinal) && env.Home.Length > 1)
            {
                notes.Add($"The executable is in the home folder of {env.UserName}: {request.Account} must be able to read and run it. A self-contained "
                    + "archive installed outside of home folders (/opt, /usr/local) suits a system service better.");
            }
        }
        else if (request.Account is not null)
        {
            throw new NodeServiceException("--account goes with --system only: a service of the user runs as the current user.");
        }

        var spec = new NodeServiceSpec
        {
            Name = request.Name,
            Executable = request.Executable.Path,
            Arguments = [.. request.Executable.PrefixArguments, .. request.RunArguments],
            WorkingDirectory = workingDirectory,
            Environment = env.ServiceVariables,
            Scope = request.Scope,
            Account = request.Account,
            StandardErrorPath = env.Platform == NodeServicePlatform.MacOS && stateRoot.Length > 0 ? Path.Combine(stateRoot, "logs", "altanode.stderr.log") : null,
        };
        return env.Platform switch
        {
            NodeServicePlatform.Windows => PlanWindows(spec, notes),
            NodeServicePlatform.MacOS => PlanMac(spec, notes),
            _ => PlanLinux(spec, notes),
        };
    }

    private NodeServicePlan PlanWindows(NodeServiceSpec spec, List<string> notes)
    {
        var env = Environment;
        var task = NodeServiceDefinition.WindowsTaskName(spec.Name);
        var user = env.UserId ?? env.UserDisplayName ?? env.UserName;
        // A path of Windows, written with its separator whatever the OS that makes the plan.
        var conhost = (env.WindowsDirectory ?? @"C:\Windows").TrimEnd('\\', '/') + @"\System32\conhost.exe";
        var file = Path.Combine(env.TempDirectory, $"altanode-task-{Guid.NewGuid():N}.xml");
        return new NodeServicePlan
        {
            Platform = NodeServicePlatform.Windows, Spec = spec, Kind = $"a task at logon of {env.UserDisplayName ?? env.UserName}", Location = $"Task Scheduler {task}",
            DefinitionPath = file, DefinitionIsTemporary = true, Definition = NodeServiceDefinition.WindowsTaskXml(spec, user, conhost, TruncateToMinute(env.Now())), DefinitionEncoding = Encoding.Unicode,
            Install = [new("schtasks", ["/Create", "/TN", task, "/XML", file, "/F"]), new("schtasks", ["/Run", "/TN", task])],
            // Deleted first, so that it does not start the node again; the node is then stopped through its local control.
            Uninstall = [new("schtasks", ["/Delete", "/TN", task, "/F"])],
            Query = new("schtasks", ["/Query", "/TN", task, "/XML"]),
            LeavesNodeRunning = true,
            Notes = notes,
        };
    }

    private NodeServicePlan PlanMac(NodeServiceSpec spec, List<string> notes)
    {
        var env = Environment;
        var system = spec.Scope == NodeServiceScope.System;
        var label = NodeServiceDefinition.LaunchdLabel(system ? NodeServiceDefinition.SystemServiceName(spec.Name, spec.Account!) : spec.Name);
        var domain = system ? "system" : $"gui/{env.UserId ?? throw new NodeServiceException("The uid of the user cannot be read (id -u).")}";
        var file = system ? Path.Combine(env.LaunchDaemonDirectory, label + ".plist") : Path.Combine(env.Home, "Library", "LaunchAgents", label + ".plist");
        return new NodeServicePlan
        {
            Platform = NodeServicePlatform.MacOS, Spec = spec, Kind = system ? $"a LaunchDaemon run as {spec.Account}" : $"a LaunchAgent of {env.UserName}",
            Location = file, DefinitionPath = file, Definition = NodeServiceDefinition.LaunchdPlist(spec),
            Install =
            [
                new("launchctl", ["bootout", $"{domain}/{label}"], MayFail: true),
                new("launchctl", ["enable", $"{domain}/{label}"]),
                new("launchctl", ["bootstrap", domain, file]),
            ],
            Uninstall = [new("launchctl", ["bootout", $"{domain}/{label}"], MayFail: true)],
            Query = new("launchctl", ["print", $"{domain}/{label}"]),
            Notes = notes,
        };
    }

    private NodeServicePlan PlanLinux(NodeServiceSpec spec, List<string> notes)
    {
        var env = Environment;
        var system = spec.Scope == NodeServiceScope.System;
        var unit = (system ? NodeServiceDefinition.SystemServiceName(spec.Name, spec.Account!) : spec.Name) + ".service";
        var file = system ? Path.Combine(env.SystemUnitDirectory, unit) : Path.Combine(env.ConfigHome ?? Path.Combine(env.Home, ".config"), "systemd", "user", unit);
        string[] scope = system ? [] : ["--user"];
        return new NodeServicePlan
        {
            Platform = NodeServicePlatform.Linux, Spec = spec, Kind = system ? $"a system unit run as {spec.Account}" : $"a systemd --user unit of {env.UserName}",
            Location = file, DefinitionPath = file, Definition = NodeServiceDefinition.SystemdUnit(spec),
            Install =
            [
                new("systemctl", [.. scope, "daemon-reload"]),
                new("systemctl", [.. scope, "enable", unit]),
                new("systemctl", [.. scope, "restart", unit]),
            ],
            Uninstall = [new("systemctl", [.. scope, "disable", "--now", unit], MayFail: true)],
            Query = new("systemctl", [.. scope, "is-enabled", unit]),
            LingerUser = system ? null : env.UserName,
            Notes = notes,
        };
    }

    /// <summary>Writes the definition and registers and starts the service.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="output">Where progress goes.</param>
    /// <param name="error">Where failures go.</param>
    /// <returns>The exit code.</returns>
    public int Install(NodeServicePlan plan, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plan.DefinitionPath)!);
            if (plan.Spec.StandardErrorPath is { } errorLog && plan.Spec.Scope == NodeServiceScope.User) Directory.CreateDirectory(Path.GetDirectoryName(errorLog)!);
            File.WriteAllText(plan.DefinitionPath, plan.Definition, plan.DefinitionEncoding);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"altanode install: cannot write {plan.DefinitionPath}: {exception.Message}");
            return NodeExitCodes.Failure;
        }

        try
        {
            if (!RunAll(plan.Install, "install", error)) return NodeExitCodes.Failure;
        }
        finally
        {
            if (plan.DefinitionIsTemporary) TryDelete(plan.DefinitionPath);
        }

        output.WriteLine($"Installed {plan.Kind}: {plan.Location}.");
        output.WriteLine($"It runs: {string.Join(' ', new[] { plan.Spec.Executable }.Concat(plan.Spec.Arguments))}");
        if (plan.LingerUser is { } user && !IsLingering(user))
        {
            var linger = Environment.Run("loginctl", ["enable-linger", user]);
            if (linger.Succeeded)
            {
                output.WriteLine($"Lingering is now on for {user}: the node starts at boot and keeps running after logouts.");
            }
            else
            {
                output.WriteLine($"Lingering is off for {user} and loginctl enable-linger {user} needs privileges here ({FirstLine(linger.Output)}).");
                output.WriteLine($"Until then the node runs only while {user} is logged in. Turn it on with: sudo loginctl enable-linger {user}");
            }
        }

        foreach (var note in plan.Notes) output.WriteLine("Note: " + note);
        return NodeExitCodes.Success;
    }

    /// <summary>Stops and unregisters the service, and deletes its definition; the profile stays.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="output">Where progress goes.</param>
    /// <param name="error">Where failures go.</param>
    /// <returns>The exit code: success also when nothing was installed.</returns>
    public int Uninstall(NodeServicePlan plan, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (!Query(plan).Installed)
        {
            output.WriteLine($"No service of this profile is installed ({plan.Location}).");
            return NodeExitCodes.Success;
        }

        if (!RunAll(plan.Uninstall, "uninstall", error)) return NodeExitCodes.Failure;
        if (!plan.DefinitionIsTemporary && File.Exists(plan.DefinitionPath))
        {
            try
            {
                File.Delete(plan.DefinitionPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"altanode uninstall: cannot delete {plan.DefinitionPath}: {exception.Message}");
                return NodeExitCodes.Failure;
            }
        }

        if (plan.Platform == NodeServicePlatform.Linux)
            Environment.Run("systemctl", plan.Spec.Scope == NodeServiceScope.System ? ["daemon-reload"] : ["--user", "daemon-reload"]);
        output.WriteLine($"Removed {plan.Kind}: {plan.Location}. The profile stays.");
        if (plan.LingerUser is { } user && IsLingering(user))
            output.WriteLine($"Lingering stays on for {user} (other services may need it): sudo loginctl disable-linger {user} turns it off.");
        return NodeExitCodes.Success;
    }

    /// <summary>Reads whether the service is registered and will start.</summary>
    /// <param name="plan">The plan of the service.</param>
    /// <returns>What it is now.</returns>
    public NodeServiceFacts Query(NodeServicePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var env = Environment;
        bool installed, enabled;
        switch (plan.Platform)
        {
            case NodeServicePlatform.Windows:
            {
                var query = env.Run(plan.Query!.FileName, plan.Query.Arguments);
                installed = query.Succeeded;
                enabled = installed && IsTaskEnabled(query.Output);
                break;
            }
            case NodeServicePlatform.MacOS:
                installed = File.Exists(plan.DefinitionPath);
                enabled = installed && env.Run(plan.Query!.FileName, plan.Query.Arguments).Succeeded;
                break;
            default:
                installed = File.Exists(plan.DefinitionPath);
                enabled = installed && env.Run(plan.Query!.FileName, plan.Query.Arguments) is { } state
                    && state.Output.Trim().Split('\n')[0].Trim() is "enabled" or "enabled-runtime";
                break;
        }

        var user = plan.Spec.Scope == NodeServiceScope.System ? plan.Spec.Account! : env.UserDisplayName ?? env.UserName;
        return new NodeServiceFacts(plan.Platform, plan.Spec.Scope, plan.Kind, plan.Location, user, installed, enabled,
            plan.Platform == NodeServicePlatform.Linux && plan.Spec.Scope == NodeServiceScope.User ? IsLingering(env.UserName) : null,
            plan.Platform == NodeServicePlatform.Linux ? null : env.AutoLoginUser());
    }

    /// <summary>
    /// Answers "Will it still run after a restart or a logout?" for a person: what the service is, then one line for a
    /// restart of the machine and one for a logout of the user.
    /// </summary>
    /// <param name="facts">The service as it is.</param>
    /// <returns>The lines.</returns>
    public static IReadOnlyList<string> Describe(NodeServiceFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!facts.Installed)
        {
            return
            [
                "Service: not installed (altanode install sets it up).",
                "After a restart or a logout: nothing starts the node again.",
            ];
        }

        if (!facts.Enabled)
        {
            return
            [
                $"Service: {facts.Kind} ({facts.Location}), but the service manager will not start it ({DisabledReason(facts.Platform)}).",
                "After a restart or a logout: nothing starts the node again until it is enabled (altanode install again does it).",
            ];
        }

        var lines = new List<string> { $"Service: {facts.Kind} ({facts.Location})." };
        if (facts.Scope == NodeServiceScope.System)
        {
            lines.Add($"After a restart: it starts at boot as {facts.User}, without a login.");
            lines.Add("After a logout: it keeps running; it does not depend on a login.");
            return lines;
        }

        var autoLogin = facts.AutoLoginUser is { } auto && SameUser(auto, facts.User);
        switch (facts.Platform)
        {
            case NodeServicePlatform.Windows:
                lines.Add(autoLogin
                    ? $"After a restart: it starts when Windows signs {facts.User} in, which it does automatically."
                    : $"After a restart: it waits until {facts.User} signs in; nothing runs it before (automatic sign-in is off).");
                lines.Add("After a logout: it stops with the session, and starts again at the next sign-in. Locking the screen does not stop it.");
                break;
            case NodeServicePlatform.MacOS:
                lines.Add(autoLogin
                    ? $"After a restart: it starts when macOS logs {facts.User} in, which it does automatically."
                    : $"After a restart: it waits until {facts.User} logs in (automatic login is off; FileVault does not allow it).");
                lines.Add("After a logout: it stops with the session, and starts again at the next login. Locking the screen does not stop it.");
                break;
            default:
                if (facts.Lingering == true)
                {
                    lines.Add($"After a restart: it starts at boot, without a login (lingering is on for {facts.User}).");
                    lines.Add("After a logout: it keeps running.");
                }
                else
                {
                    lines.Add($"After a restart: it waits until {facts.User} logs in (lingering is off: sudo loginctl enable-linger {facts.User} starts it at boot).");
                    lines.Add($"After a logout: it stops once the last session of {facts.User} ends, unless lingering is turned on.");
                }

                break;
        }

        return lines;
    }

    /// <summary>Gets whether the services of a user run without a login (systemd-logind lingering).</summary>
    /// <param name="user">The user.</param>
    /// <returns>Whether lingering is on.</returns>
    public bool IsLingering(string user) => File.Exists(Path.Combine(Environment.LingerDirectory, user));

    // Reads <Settings><Enabled> of a task exported by schtasks /Query /XML: absent means enabled.
    internal static bool IsTaskEnabled(string xml)
    {
        try
        {
            var document = XDocument.Parse(xml);
            var settings = document.Root?.Elements().FirstOrDefault(static element => element.Name.LocalName == "Settings");
            var enabled = settings?.Elements().FirstOrDefault(static element => element.Name.LocalName == "Enabled");
            return enabled is null || !string.Equals(enabled.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    private bool RunAll(IReadOnlyList<NodeServiceCommand> commands, string verb, TextWriter error)
    {
        foreach (var command in commands)
        {
            var result = Environment.Run(command.FileName, command.Arguments);
            if (result.Succeeded || command.MayFail) continue;
            error.WriteLine($"altanode {verb}: {command} failed ({result.ExitCode}): {FirstLine(result.Output)}");
            return false;
        }

        return true;
    }

    private static DateTime TruncateToMinute(DateTime time) => new(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0, time.Kind);

    private static string DisabledReason(NodeServicePlatform platform) => platform switch
    {
        NodeServicePlatform.Windows => "the task is disabled",
        NodeServicePlatform.MacOS => "the agent is not loaded",
        _ => "the unit is not enabled",
    };

    // DOMAIN\alice and alice are the same user for automatic sign-in, which records the name alone.
    private static bool SameUser(string autoLogin, string user)
    {
        static string Bare(string name) => name[(name.LastIndexOf('\\') + 1)..];
        return string.Equals(Bare(autoLogin), Bare(user), StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstLine(string text)
    {
        var line = text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? "no output" : line;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
