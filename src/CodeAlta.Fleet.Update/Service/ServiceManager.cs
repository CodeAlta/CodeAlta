using System.Text;
using System.Xml.Linq;

namespace CodeAlta.Fleet.Update.Service;

/// <summary>A service <c>altanode install</c> or <c>altaserver install</c> refuses to set up, and why, for a person.</summary>
/// <param name="message">Why, for a person.</param>
public sealed class ServiceException(string message) : Exception(message);

/// <summary>What <c>install</c> and <c>uninstall</c> of <c>altanode</c> or <c>altaserver</c> are asked for.</summary>
public sealed record ServiceRequest
{
    /// <summary>Gets the product the service runs.</summary>
    public required UpdateProduct Product { get; init; }

    /// <summary>Gets the name of the service of the profile or the data root (<see cref="ServiceDefinition.ServiceName"/>).</summary>
    public required string Name { get; init; }

    /// <summary>Gets whom it runs as.</summary>
    public ServiceScope Scope { get; init; } = ServiceScope.User;

    /// <summary>Gets the account of a system service.</summary>
    public string? Account { get; init; }

    /// <summary>Gets the executable the service starts.</summary>
    public required ServiceExecutable Executable { get; init; }

    /// <summary>Gets the arguments of <c>run</c>, <c>run</c> first.</summary>
    public required IReadOnlyList<string> RunArguments { get; init; }

    /// <summary>Gets the state root of the profile (the data root of a server), for a service of the user; null for a system service of the default one.</summary>
    public string? StateRoot { get; init; }

    /// <summary>
    /// Gets the state root below the home of the account, for a system service without <c>--data-root</c> (<c>.alta</c>,
    /// <c>.alta/dev</c>, <c>.altaserver</c>).
    /// </summary>
    public string? StateRootInHome { get; init; }
}

/// <summary>One command of the service manager a plan runs.</summary>
/// <param name="FileName">The command.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="MayFail">Whether its failure is expected (stopping a service that does not run).</param>
public sealed record ServiceCommand(string FileName, IReadOnlyList<string> Arguments, bool MayFail = false)
{
    /// <summary>
    /// Gets a command run again after this one, while it succeeds, before the next command: the job launchd still holds
    /// after a <c>bootout</c>, while its process stops; null for none.
    /// </summary>
    public ServiceCommand? ThenWaitWhile { get; init; }

    /// <summary>Gets the command as a person would type it.</summary>
    /// <returns>The command line.</returns>
    public override string ToString() => string.Join(' ', new[] { FileName }.Concat(Arguments).Select(static word => word.Contains(' ', StringComparison.Ordinal) ? $"\"{word}\"" : word));
}

/// <summary>What installing or removing the service of a node or a server does on this machine.</summary>
public sealed record ServicePlan
{
    /// <summary>Gets the OS.</summary>
    public required ServicePlatform Platform { get; init; }

    /// <summary>Gets the service.</summary>
    public required ServiceSpec Spec { get; init; }

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
    public required IReadOnlyList<ServiceCommand> Install { get; init; }

    /// <summary>Gets the commands that stop and unregister it, before the definition is deleted.</summary>
    public required IReadOnlyList<ServiceCommand> Uninstall { get; init; }

    /// <summary>Gets the command that tells whether it is registered, or null when the definition file tells.</summary>
    public ServiceCommand? Query { get; init; }

    /// <summary>
    /// Gets whether removing the service leaves its process running, for <c>uninstall</c> to stop it: Task Scheduler ends
    /// <c>conhost.exe</c>, not the node or the server under it. launchd and systemd stop it with SIGTERM, which it drains.
    /// </summary>
    public bool LeavesProcessRunning { get; init; }

    /// <summary>Gets the user whose lingering a <c>systemd --user</c> unit needs, or null.</summary>
    public string? LingerUser { get; init; }

    /// <summary>Gets what a person should know before relying on the service (warnings).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Gets the commands that start the service, once it is registered and its logon set (a Windows service); empty when <see cref="Install"/> starts it.</summary>
    public IReadOnlyList<ServiceCommand> Start { get; init; } = [];

    /// <summary>Gets the named account a Windows service logs on as, whose password <c>install</c> needs (<c>--password-stdin</c>); null for none.</summary>
    public string? LogonAccount { get; init; }

    /// <summary>Gets the folder of the data of a service of its own account, made by <c>install</c> before the service is given it; null for none.</summary>
    public string? DataRoot { get; init; }

    /// <summary>
    /// Gets the command that tells whether the login session the service is loaded into exists (the <c>gui</c> domain of launchd):
    /// without one, the definition is written and the service starts at the next login; null for none.
    /// </summary>
    public ServiceCommand? SessionProbe { get; init; }
}

/// <summary>What the service of a node or a server is on this machine, as <c>status</c> reads it.</summary>
/// <param name="Product">The product it runs.</param>
/// <param name="Platform">The OS.</param>
/// <param name="Scope">Whom it runs as.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Location">Where it is registered.</param>
/// <param name="User">The user (or the account) it runs as.</param>
/// <param name="Installed">Whether it is registered.</param>
/// <param name="Enabled">Whether the service manager will start it (a task enabled, an agent loaded, a unit enabled).</param>
/// <param name="Lingering">On Linux, whether the services of the user run without a login; null elsewhere.</param>
/// <param name="AutoLoginUser">The user the machine signs in automatically at startup, or null.</param>
public sealed record ServiceFacts(UpdateProduct Product, ServicePlatform Platform, ServiceScope Scope, string Kind, string Location, string User, bool Installed,
    bool Enabled, bool? Lingering, string? AutoLoginUser);

/// <summary>Installs, removes and reads the service of a node or a server with the service manager of the OS.</summary>
/// <param name="environment">The machine the manager works on.</param>
/// <exception cref="ArgumentNullException"><paramref name="environment"/> is null.</exception>
public sealed class ServiceManager(ServiceEnvironment environment)
{
    /// <summary>The exit code of a command that succeeded, as <c>altanode</c> and <c>altaserver</c> give it.</summary>
    public const int Success = 0;

    /// <summary>The exit code of a command that failed, as <c>altanode</c> and <c>altaserver</c> give it.</summary>
    public const int Failure = 1;

    /// <summary>Gets the machine the manager works on.</summary>
    public ServiceEnvironment Environment { get; } = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>Plans the service of a node or a server: its definition and the commands that register it.</summary>
    /// <param name="request">The service.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ServiceException">This OS or this account cannot have that service.</exception>
    public ServicePlan Plan(ServiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var env = Environment;
        var command = request.Product.AssemblyName;
        var noun = ServiceDefinition.Noun(request.Product);
        var system = request.Scope == ServiceScope.System;
        var notes = new List<string>();
        string workingDirectory = env.Home, stateRoot = request.StateRoot ?? "";
        List<string> extraArguments = [];
        string? dataRoot = null;
        if (system && env.Platform == ServicePlatform.Windows && request.Account is null)
        {
            // A Windows service of its own virtual account (NT SERVICE\<name>): no password, its data under %ProgramData%.
            if (!env.IsRoot) throw new ServiceException("A Windows service is installed by an administrator: run it again from an elevated terminal (Run as administrator).");
            stateRoot = request.StateRoot ?? Path.Combine(env.ProgramData ?? throw new ServiceException("The folder of the data of the system (%ProgramData%) cannot be read."),
                "CodeAlta", ServiceDefinition.Noun(request.Product) == "server" ? "Server" : "Node", request.Name);
            if (request.StateRoot is null) extraArguments.AddRange(["--data-root", stateRoot]);
            workingDirectory = stateRoot;
            dataRoot = stateRoot;
        }
        else if (system)
        {
            if (env.Platform == ServicePlatform.Windows ? !ServiceDefinition.IsValidWindowsAccount(request.Account) : !ServiceDefinition.IsValidAccount(request.Account))
                throw new ServiceException($"--system needs --account <name>, the account the {noun} runs as: '{request.Account}' is not an account name.");
            if (!env.IsRoot)
            {
                throw new ServiceException(env.Platform switch
                {
                    ServicePlatform.Windows => "A Windows service is installed by an administrator: run it again from an elevated terminal (Run as administrator).",
                    ServicePlatform.MacOS => "A LaunchDaemon is installed by root: run it again with sudo.",
                    _ => "A system unit is installed by root: run it again with sudo.",
                });
            }

            var home = env.HomeOfAccount(request.Account!) ?? throw new ServiceException($"No account named '{request.Account}' exists on this machine: create it first.");
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
            throw new ServiceException("--account goes with --system only: a service of the user runs as the current user.");
        }

        var spec = new ServiceSpec
        {
            Product = request.Product,
            Name = request.Name,
            Executable = request.Executable.Path,
            Arguments = [.. request.Executable.PrefixArguments, .. request.RunArguments, .. extraArguments],
            WorkingDirectory = workingDirectory,
            Environment = env.ServiceVariables,
            Scope = request.Scope,
            Account = request.Account,
            StandardErrorPath = env.Platform == ServicePlatform.MacOS && stateRoot.Length > 0 ? Path.Combine(stateRoot, "logs", command + ".stderr.log") : null,
        };
        return env.Platform switch
        {
            ServicePlatform.Windows when system => PlanWindowsService(spec, notes, dataRoot),
            ServicePlatform.Windows => PlanWindows(spec, notes),
            ServicePlatform.MacOS => PlanMac(spec, notes),
            _ => PlanLinux(spec, notes),
        };
    }

    private ServicePlan PlanWindows(ServiceSpec spec, List<string> notes)
    {
        var env = Environment;
        if (ServiceDefinition.IsWindowsBatchFile(spec.Executable))
        {
            // The shim of a .NET 10 tool (altanode.cmd, altaserver.cmd): Windows runs it through cmd.exe, which reads its command line.
            foreach (var word in new[] { spec.Executable }.Concat(spec.Arguments))
            {
                if (ServiceDefinition.WindowsBatchUnsafeCharacter(word) is not { } unsafeCharacter) continue;
                var what = char.IsControl(unsafeCharacter) ? "a control character" : $"'{unsafeCharacter}'";
                throw new ServiceException(word == spec.Executable
                    ? $"The task would start {spec.Executable} through cmd.exe, which cannot run a path with {what}: install {ServiceDefinition.DisplayName(spec.Product)} in another folder."
                    : $"The task would start {spec.Executable} through cmd.exe, which cannot pass {what} in '{word}': choose another value.");
            }
        }

        var task = ServiceDefinition.WindowsTaskName(spec.Name);
        var user = env.UserId ?? env.UserDisplayName ?? env.UserName;
        // A path of Windows, written with its separator whatever the OS that makes the plan.
        var conhost = (env.WindowsDirectory ?? @"C:\Windows").TrimEnd('\\', '/') + @"\System32\conhost.exe";
        var file = Path.Combine(env.TempDirectory, $"{spec.Product.AssemblyName}-task-{Guid.NewGuid():N}.xml");
        return new ServicePlan
        {
            Platform = ServicePlatform.Windows, Spec = spec, Kind = $"a task at logon of {env.UserDisplayName ?? env.UserName}", Location = $"Task Scheduler {task}",
            DefinitionPath = file, DefinitionIsTemporary = true, Definition = ServiceDefinition.WindowsTaskXml(spec, user, conhost, TruncateToMinute(env.Now())), DefinitionEncoding = Encoding.Unicode,
            Install = [new("schtasks", ["/Create", "/TN", task, "/XML", file, "/F"]), new("schtasks", ["/Run", "/TN", task])],
            // Deleted first, so that it does not start the process again, which uninstall then asks to stop.
            Uninstall = [new("schtasks", ["/Delete", "/TN", task, "/F"])],
            Query = new("schtasks", ["/Query", "/TN", task, "/XML"]),
            LeavesProcessRunning = true,
            Notes = notes,
        };
    }

    // A Windows service (sc.exe): started at boot, delayed so that the network is up, started again when it fails. Its process is the
    // executable itself, never the batch file of a .NET tool, which cmd.exe would run apart from the Service Control Manager.
    private ServicePlan PlanWindowsService(ServiceSpec spec, List<string> notes, string? dataRoot)
    {
        var executable = spec.Executable;
        if (ServiceDefinition.IsWindowsBatchFile(executable))
        {
            executable = ServiceExecutableLocator.ResolveBatchTarget(executable, Environment.ReadLines)
                ?? throw new ServiceException($"The service cannot start {executable}, the batch file of a .NET tool: install the self-contained archive of {ServiceDefinition.DisplayName(spec.Product)}, or run {spec.Product.AssemblyName} install from it.");
            notes.Add($"The service starts {executable}, in the folder of the .NET tool: stop the service before dotnet tool update, and run {spec.Product.AssemblyName} install --system again after it. "
                + $"{spec.Product.AssemblyName} update keeps working under the service.");
        }

        var name = spec.Name;
        var virtualAccount = spec.Account is null ? ServiceDefinition.WindowsVirtualAccount(name) : null;
        var binPath = ServiceDefinition.WindowsCommandLine([executable, .. spec.Arguments, ServiceDefinition.WindowsServiceOption]);
        var display = $"{ServiceDefinition.DisplayName(spec.Product)} ({name})";
        string[] settings = ["binPath=", binPath, "start=", "delayed-auto", "DisplayName=", display, .. virtualAccount is null ? Array.Empty<string>() : ["obj=", virtualAccount]];
        var install = new List<ServiceCommand>
        {
            // Created when it is not there yet; configured in every case, so that install again changes one that exists.
            new("sc.exe", ["create", name, .. settings], MayFail: true),
            new("sc.exe", ["config", name, .. settings]),
            new("sc.exe", ["description", name, $"{ServiceDefinition.DisplayName(spec.Product)} ({spec.Product.AssemblyName} run). Written by {spec.Product.AssemblyName} install --system; {spec.Product.AssemblyName} uninstall --system removes it."]),
            new("sc.exe", ["failure", name, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000"]),
        };
        // The virtual account of the service owns the data it writes; it exists once the service does.
        if (virtualAccount is not null && dataRoot is not null) install.Add(new("icacls.exe", [dataRoot, "/grant", $"{virtualAccount}:(OI)(CI)M", "/T", "/Q"]));
        return new ServicePlan
        {
            Platform = ServicePlatform.Windows, Spec = spec with { Executable = executable }, Kind = $"a Windows service run as {virtualAccount ?? spec.Account}",
            Location = $"Services {name}", DefinitionPath = binPath, DefinitionIsTemporary = true, Definition = $"sc.exe config {name} binPath= {binPath} start= delayed-auto"
                + (virtualAccount is null ? "" : $" obj= \"{virtualAccount}\""),
            Install = install,
            Start = [new("sc.exe", ["start", name])],
            LogonAccount = spec.Account,
            DataRoot = dataRoot,
            // Stopped (the launcher drains the version that runs), then deleted: Windows removes it once it stopped.
            Uninstall = [new("sc.exe", ["stop", name], MayFail: true), new("sc.exe", ["delete", name])],
            Query = new("sc.exe", ["qc", name]),
            Notes = notes,
        };
    }

    private ServicePlan PlanMac(ServiceSpec spec, List<string> notes)
    {
        var env = Environment;
        var system = spec.Scope == ServiceScope.System;
        var label = ServiceDefinition.LaunchdLabel(system ? ServiceDefinition.SystemServiceName(spec.Name, spec.Account!) : spec.Name);
        var domain = system ? "system" : $"gui/{env.UserId ?? throw new ServiceException("The uid of the user cannot be read (id -u).")}";
        var file = system ? Path.Combine(env.LaunchDaemonDirectory, label + ".plist") : Path.Combine(env.Home, "Library", "LaunchAgents", label + ".plist");
        // launchd answers a bootout before the process of the job ends, and keeps the job until it did (a node drains its
        // turns): a bootstrap meanwhile fails with "5: Input/output error". The job is read again until it is gone.
        var print = new ServiceCommand("launchctl", ["print", $"{domain}/{label}"]);
        var bootout = new ServiceCommand("launchctl", ["bootout", $"{domain}/{label}"], MayFail: true) { ThenWaitWhile = print };
        return new ServicePlan
        {
            Platform = ServicePlatform.MacOS, Spec = spec, Kind = system ? $"a LaunchDaemon run as {spec.Account}" : $"a LaunchAgent of {env.UserName}",
            Location = file, DefinitionPath = file, Definition = ServiceDefinition.LaunchdPlist(spec),
            Install =
            [
                bootout,
                new("launchctl", ["enable", $"{domain}/{label}"]),
                new("launchctl", ["bootstrap", domain, file]),
            ],
            Uninstall = [bootout],
            Query = print,
            Notes = notes,
            // Over SSH, with nobody logged in at the screen, the gui domain does not exist and a bootstrap fails.
            SessionProbe = system ? null : new ServiceCommand("launchctl", ["print", domain]),
        };
    }

    private ServicePlan PlanLinux(ServiceSpec spec, List<string> notes)
    {
        var env = Environment;
        var system = spec.Scope == ServiceScope.System;
        var unit = (system ? ServiceDefinition.SystemServiceName(spec.Name, spec.Account!) : spec.Name) + ".service";
        var file = system ? Path.Combine(env.SystemUnitDirectory, unit) : Path.Combine(env.ConfigHome ?? Path.Combine(env.Home, ".config"), "systemd", "user", unit);
        string[] scope = system ? [] : ["--user"];
        return new ServicePlan
        {
            Platform = ServicePlatform.Linux, Spec = spec, Kind = system ? $"a system unit run as {spec.Account}" : $"a systemd --user unit of {env.UserName}",
            Location = file, DefinitionPath = file, Definition = ServiceDefinition.SystemdUnit(spec),
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
    /// <returns>The exit code: <see cref="Success"/> or <see cref="Failure"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public int Install(ServicePlan plan, TextWriter output, TextWriter error) => Install(plan, output, error, password: null);

    /// <summary>Writes the definition, registers the service, sets its logon, and starts it.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="output">Where progress goes.</param>
    /// <param name="error">Where failures go.</param>
    /// <param name="password">The password of <see cref="ServicePlan.LogonAccount"/>, given to Windows only; null when the plan names none.</param>
    /// <returns>The exit code: <see cref="Success"/> or <see cref="Failure"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public int Install(ServicePlan plan, TextWriter output, TextWriter error, string? password)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        var command = plan.Spec.Product.AssemblyName;
        if (plan.LogonAccount is { } logon && string.IsNullOrEmpty(password))
        {
            error.WriteLine($"{command} install: the service runs as {logon}, whose password Windows needs: give it with --password-stdin (it goes to Windows only).");
            return Failure;
        }

        if (plan.Platform != ServicePlatform.Windows)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(plan.DefinitionPath)!);
                if (plan.Spec.StandardErrorPath is { } errorLog && plan.Spec.Scope == ServiceScope.User) Directory.CreateDirectory(Path.GetDirectoryName(errorLog)!);
                File.WriteAllText(plan.DefinitionPath, plan.Definition, plan.DefinitionEncoding);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"{command} install: cannot write {plan.DefinitionPath}: {exception.Message}");
                return Failure;
            }
        }
        else if (plan.Spec.Scope == ServiceScope.User)
        {
            try
            {
                File.WriteAllText(plan.DefinitionPath, plan.Definition, plan.DefinitionEncoding);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"{command} install: cannot write {plan.DefinitionPath}: {exception.Message}");
                return Failure;
            }
        }

        if (plan.SessionProbe is { } probe && !Environment.Run(probe.FileName, probe.Arguments).Succeeded)
        {
            output.WriteLine($"Installed {plan.Kind}: {plan.Location}.");
            output.WriteLine($"Nobody is logged in to the screen of this computer: it starts at the next login of {Environment.UserName}. A {ServiceDefinition.Noun(plan.Spec.Product)} "
                + $"that runs without a login is a system service ({command} install --system --account {Environment.UserName}, with sudo).");
            return Success;
        }

        try
        {
            if (plan.DataRoot is { } dataRoot) Directory.CreateDirectory(dataRoot);
            if (!RunAll(command, plan.Install, "install", error)) return Failure;
            if (plan.LogonAccount is { } account)
            {
                try
                {
                    (Environment.SetServiceLogon ?? throw new InvalidOperationException("This machine cannot set the logon of a service."))(plan.Spec.Name, account, password!);
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    error.WriteLine($"{command} install: Windows refused {account} with its password for the service: {exception.Message}");
                    return Failure;
                }
            }

            if (!RunAll(command, plan.Start, "install", error)) return Failure;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"{command} install: cannot make {plan.DataRoot}: {exception.Message}");
            return Failure;
        }
        finally
        {
            if (plan.DefinitionIsTemporary && plan.Platform == ServicePlatform.Windows && plan.Spec.Scope == ServiceScope.User) TryDelete(plan.DefinitionPath);
        }

        output.WriteLine($"Installed {plan.Kind}: {plan.Location}.");
        output.WriteLine($"It runs: {string.Join(' ', new[] { plan.Spec.Executable }.Concat(plan.Spec.Arguments))}");
        if (plan.LingerUser is { } user && !IsLingering(user))
        {
            var linger = Environment.Run("loginctl", ["enable-linger", user]);
            if (linger.Succeeded)
            {
                output.WriteLine($"Lingering is now on for {user}: the {ServiceDefinition.Noun(plan.Spec.Product)} starts at boot and keeps running after logouts.");
            }
            else
            {
                output.WriteLine($"Lingering is off for {user} and loginctl enable-linger {user} needs privileges here ({FirstLine(linger.Output)}).");
                output.WriteLine($"Until then the {ServiceDefinition.Noun(plan.Spec.Product)} runs only while {user} is logged in. Turn it on with: sudo loginctl enable-linger {user}");
            }
        }

        foreach (var note in plan.Notes) output.WriteLine("Note: " + note);
        return Success;
    }

    /// <summary>Stops and unregisters the service, and deletes its definition; the profile or the data root stays.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="output">Where progress goes.</param>
    /// <param name="error">Where failures go.</param>
    /// <returns>The exit code: <see cref="Success"/>, also when nothing was installed, or <see cref="Failure"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public int Uninstall(ServicePlan plan, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        var command = plan.Spec.Product.AssemblyName;
        var server = plan.Spec.Product == UpdateProduct.Server;
        if (!Query(plan).Installed)
        {
            output.WriteLine($"No service of this {(server ? "data root" : "profile")} is installed ({plan.Location}).");
            return Success;
        }

        if (!RunAll(command, plan.Uninstall, "uninstall", error)) return Failure;
        if (!plan.DefinitionIsTemporary && File.Exists(plan.DefinitionPath))
        {
            try
            {
                File.Delete(plan.DefinitionPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"{command} uninstall: cannot delete {plan.DefinitionPath}: {exception.Message}");
                return Failure;
            }
        }

        if (plan.Platform == ServicePlatform.Linux)
            Environment.Run("systemctl", plan.Spec.Scope == ServiceScope.System ? ["daemon-reload"] : ["--user", "daemon-reload"]);
        output.WriteLine($"Removed {plan.Kind}: {plan.Location}. The {(server ? "data root" : "profile")} stays.");
        // Lingering of one's own account is turned off without privileges, as install turned it on.
        if (plan.LingerUser is { } user && IsLingering(user))
            output.WriteLine($"Lingering stays on for {user} (other services may need it): loginctl disable-linger {user} turns it off.");
        return Success;
    }

    /// <summary>Reads whether the service is registered and will start.</summary>
    /// <param name="plan">The plan of the service.</param>
    /// <returns>What it is now.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    public ServiceFacts Query(ServicePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var env = Environment;
        bool installed, enabled;
        switch (plan.Platform)
        {
            case ServicePlatform.Windows:
            {
                var query = env.Run(plan.Query!.FileName, plan.Query.Arguments);
                installed = query.Succeeded;
                // A service starts at boot when its start type is automatic (delayed or not); a task when it is enabled.
                enabled = installed && (plan.Spec.Scope == ServiceScope.System ? query.Output.Contains("AUTO_START", StringComparison.Ordinal) : IsTaskEnabled(query.Output));
                break;
            }
            case ServicePlatform.MacOS:
                installed = File.Exists(plan.DefinitionPath);
                enabled = installed && env.Run(plan.Query!.FileName, plan.Query.Arguments).Succeeded;
                break;
            default:
                installed = File.Exists(plan.DefinitionPath);
                enabled = installed && env.Run(plan.Query!.FileName, plan.Query.Arguments) is { } state
                    && state.Output.Trim().Split('\n')[0].Trim() is "enabled" or "enabled-runtime";
                break;
        }

        var user = plan.Spec.Scope == ServiceScope.System ? plan.Spec.Account ?? ServiceDefinition.WindowsVirtualAccount(plan.Spec.Name) : env.UserDisplayName ?? env.UserName;
        return new ServiceFacts(plan.Spec.Product, plan.Platform, plan.Spec.Scope, plan.Kind, plan.Location, user, installed, enabled,
            plan.Platform == ServicePlatform.Linux && plan.Spec.Scope == ServiceScope.User ? IsLingering(env.UserName) : null,
            plan.Platform == ServicePlatform.Linux ? null : env.AutoLoginUser());
    }

    /// <summary>
    /// Answers "Will it still run after a restart or a logout?" for a person: what the service is, then one line for a
    /// restart of the machine and one for a logout of the user.
    /// </summary>
    /// <param name="facts">The service as it is.</param>
    /// <returns>The lines.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="facts"/> is null.</exception>
    public static IReadOnlyList<string> Describe(ServiceFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var command = facts.Product.AssemblyName;
        var noun = ServiceDefinition.Noun(facts.Product);
        if (!facts.Installed)
        {
            return
            [
                $"Service: not installed ({command} install sets it up).",
                $"After a restart or a logout: nothing starts the {noun} again.",
            ];
        }

        if (!facts.Enabled)
        {
            return
            [
                $"Service: {facts.Kind} ({facts.Location}), but the service manager will not start it ({DisabledReason(facts.Platform)}).",
                $"After a restart or a logout: nothing starts the {noun} again until it is enabled ({command} install again does it).",
            ];
        }

        var lines = new List<string> { $"Service: {facts.Kind} ({facts.Location})." };
        if (facts.Scope == ServiceScope.System)
        {
            lines.Add($"After a restart: it starts at boot as {facts.User}, without a login.");
            lines.Add("After a logout: it keeps running; it does not depend on a login.");
            return lines;
        }

        var autoLogin = facts.AutoLoginUser is { } auto && SameUser(auto, facts.User);
        switch (facts.Platform)
        {
            case ServicePlatform.Windows:
                lines.Add(autoLogin
                    ? $"After a restart: it starts when Windows signs {facts.User} in, which it does automatically."
                    : $"After a restart: it waits until {facts.User} signs in; nothing runs it before (automatic sign-in is off).");
                lines.Add("After a logout: it stops with the session, and starts again at the next sign-in. Locking the screen does not stop it.");
                break;
            case ServicePlatform.MacOS:
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

    /// <summary>Reads a Windows service (<c>sc.exe qc</c>) without a plan, as <c>status</c> reads one <c>install --system</c> registered.</summary>
    /// <param name="product">The product it runs.</param>
    /// <param name="name">The name of the service.</param>
    /// <returns>What it is; not installed when Windows has no service of that name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="product"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public ServiceFacts QueryWindowsService(UpdateProduct product, string name)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var query = Environment.Run("sc.exe", ["qc", name]);
        var account = ServiceDefinition.WindowsVirtualAccount(name);
        foreach (var line in query.Output.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("SERVICE_START_NAME", StringComparison.Ordinal) && trimmed.IndexOf(':') is var colon and > 0)
                account = trimmed[(colon + 1)..].Trim();
        }

        return new ServiceFacts(product, ServicePlatform.Windows, ServiceScope.System, $"a Windows service run as {account}", $"Services {name}", account,
            query.Succeeded, query.Succeeded && query.Output.Contains("AUTO_START", StringComparison.Ordinal), null, null);
    }

    /// <summary>Gets whether the service starts when the machine starts, with nobody logged in.</summary>
    /// <param name="facts">The service as it is.</param>
    /// <returns>Whether it does.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="facts"/> is null.</exception>
    public static bool StartsAtBoot(ServiceFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!facts.Installed || !facts.Enabled) return false;
        if (facts.Scope == ServiceScope.System) return true;
        return facts.Platform == ServicePlatform.Linux ? facts.Lingering == true : facts.AutoLoginUser is { } auto && SameUser(auto, facts.User);
    }

    /// <summary>Gets whether the services of a user run without a login (systemd-logind lingering).</summary>
    /// <param name="user">The user.</param>
    /// <returns>Whether lingering is on.</returns>
    public bool IsLingering(string user) => File.Exists(Path.Combine(Environment.LingerDirectory, user));

    /// <summary>Reads whether a task exported by <c>schtasks /Query /XML</c> is enabled: an absent <c>Settings/Enabled</c> means it is.</summary>
    /// <param name="xml">The task XML.</param>
    /// <returns>Whether the task is enabled; false for a text that is not XML.</returns>
    public static bool IsTaskEnabled(string xml)
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

    private bool RunAll(string product, IReadOnlyList<ServiceCommand> commands, string verb, TextWriter error)
    {
        foreach (var command in commands)
        {
            var result = Environment.Run(command.FileName, command.Arguments);
            if (!result.Succeeded && !command.MayFail)
            {
                error.WriteLine($"{product} {verb}: {command} failed ({result.ExitCode}): {FirstLine(result.Output)}");
                return false;
            }

            if (command.ThenWaitWhile is { } probe) WaitWhile(probe);
        }

        return true;
    }

    // Reads again while the probe succeeds, at most as long as the service manager gives a process to stop (then it kills it).
    private void WaitWhile(ServiceCommand probe)
    {
        var interval = TimeSpan.FromMilliseconds(250);
        var attempts = (int)(TimeSpan.FromSeconds(ServiceDefinition.StopTimeoutSeconds + 10) / interval);
        for (var attempt = 0; attempt < attempts && Environment.Run(probe.FileName, probe.Arguments).Succeeded; attempt++) Environment.Sleep(interval);
    }

    private static DateTime TruncateToMinute(DateTime time) => new(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0, time.Kind);

    private static string DisabledReason(ServicePlatform platform) => platform switch
    {
        ServicePlatform.Windows => "the task is disabled",
        ServicePlatform.MacOS => "the agent is not loaded",
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
