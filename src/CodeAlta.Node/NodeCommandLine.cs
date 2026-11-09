using CodeAlta.Hosting;
using XenoAtom.CommandLine;

namespace CodeAlta.Node;

/// <summary>The profile options every command that reads a profile has: <c>--dev</c> and <c>--data-root</c>.</summary>
internal sealed class NodeProfileOptions
{
    /// <summary>Gets or sets whether the developer instance's state root is used, as <c>alta --dev</c> and <c>altatui --dev</c>.</summary>
    public bool Developer { get; set; }

    /// <summary>Gets or sets the profile directory in place of <c>~/.alta</c>; null for <c>~/.alta</c>.</summary>
    public string? DataRoot { get; set; }

    /// <summary>Resolves the profile the options name.</summary>
    /// <param name="error">Why the options are not valid; null when they are.</param>
    /// <returns>The profile, or null with <paramref name="error"/>.</returns>
    public CodeAltaInstanceProfile? Resolve(out string? error)
    {
        error = null;
        if (DataRoot is null) return CodeAltaInstanceProfile.Create(CodeAltaInstanceProfile.GetDefaultGlobalRoot(), Developer);
        if (string.IsNullOrWhiteSpace(DataRoot) || !Path.IsPathFullyQualified(DataRoot))
        {
            error = $"--data-root needs an absolute path: '{DataRoot}'.";
            return null;
        }

        return CodeAltaInstanceProfile.Create(Path.TrimEndingDirectorySeparator(Path.GetFullPath(DataRoot)), Developer);
    }
}

/// <summary>The options of <c>altanode run</c>.</summary>
internal sealed class NodeRunOptions
{
    /// <summary>Gets the profile options.</summary>
    public NodeProfileOptions Profile { get; } = new();

    /// <summary>Gets or sets whether the catalog of models.dev is left as it is.</summary>
    public bool Offline { get; set; }

    /// <summary>Gets or sets whether no plugin starts.</summary>
    public bool NoPlugins { get; set; }

    /// <summary>Gets or sets the port of the listener, as typed; null for 2585.</summary>
    public string? Port { get; set; }

    /// <summary>Gets or sets the address the listener binds, as typed; null for loopback.</summary>
    public string? Bind { get; set; }

    /// <summary>Gets or sets whether the listener binds every interface.</summary>
    public bool ListenAll { get; set; }

    /// <summary>Gets or sets whether the node listens for no device.</summary>
    public bool NoListen { get; set; }

    /// <summary>Gets or sets the name of the node shown to the devices that pair with it; null for the name of the machine.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets whether the node runs as the Windows service <c>install --system</c> registered, answering the Service Control Manager.</summary>
    public bool Service { get; set; }
}

/// <summary>What the commands of <c>altanode</c> do once parsed.</summary>
/// <param name="Run">Runs the node in the foreground.</param>
/// <param name="Status">Tells whether an instance holds the profile.</param>
/// <param name="NotImplemented">A command of the surface that does not work yet, by its name.</param>
/// <param name="Pair">Makes a pairing link on the running node and waits for a device to use it.</param>
/// <param name="Machines">Lists the machines this profile paired with.</param>
/// <param name="Connect">Pairs this profile with a node from its pairing link.</param>
/// <param name="Remote">Reads or drives the sessions of a paired machine.</param>
/// <param name="Install">Installs and starts the service of the node.</param>
/// <param name="Uninstall">Stops and removes the service of the node.</param>
/// <param name="Logs">Prints the log of the node.</param>
/// <param name="Devices">Lists the devices paired with the node.</param>
/// <param name="Revoke">Revokes a paired device.</param>
/// <param name="Permissions">Shows or changes what a paired device may do.</param>
/// <param name="Moves">Lists the moves of sessions this profile recorded.</param>
/// <param name="Projects">Lists, adds, clones and removes the projects of the node.</param>
/// <param name="Join">Enrolls the node in a CodeAlta Server and waits for its approval.</param>
/// <param name="Update">Downloads a version of the node and switches to it when idle.</param>
/// <param name="Automations">Lists the automations given to the node and their runs, and shows or sets its policy.</param>
internal sealed record NodeCommandHandlers(
    Func<NodeRunOptions, ValueTask<int>> Run,
    Func<NodeProfileOptions, ValueTask<int>> Status,
    Func<string, ValueTask<int>> NotImplemented,
    Func<NodePairOptions, ValueTask<int>> Pair,
    Func<NodeProfileOptions, ValueTask<int>> Machines,
    Func<NodeConnectOptions, ValueTask<int>> Connect,
    Func<NodeRemoteOptions, ValueTask<int>> Remote,
    Func<NodeInstallOptions, ValueTask<int>> Install,
    Func<NodeInstallOptions, ValueTask<int>> Uninstall,
    Func<NodeLogOptions, ValueTask<int>> Logs,
    Func<NodeDeviceOptions, ValueTask<int>> Devices,
    Func<NodeDeviceOptions, ValueTask<int>> Revoke,
    Func<NodeDeviceOptions, ValueTask<int>> Permissions,
    Func<NodeProfileOptions, ValueTask<int>> Moves,
    Func<NodeProjectOptions, ValueTask<int>> Projects,
    Func<NodeJoinOptions, ValueTask<int>> Join,
    Func<NodeUpdateOptions, ValueTask<int>> Update,
    Func<NodeAutomationCommandOptions, ValueTask<int>> Automations)
{
    /// <summary>Gets what lists, sets, rotates and deletes the secrets and variables of the machine; null for none.</summary>
    public Func<NodeSecretCommandOptions, ValueTask<int>>? Secrets { get; init; }

    /// <summary>Gets what lists and changes the labels of the machine; null for none.</summary>
    public Func<NodeLabelCommandOptions, ValueTask<int>>? Labels { get; init; }

    /// <summary>Gets what tells the model servers of the running node; null for none.</summary>
    public Func<NodeLlamaCommandOptions, ValueTask<int>>? Llama { get; init; }

    /// <summary>Gets what shows and changes the permission mode the sessions of the machine run in by default; null for none.</summary>
    public Func<NodePermissionModeCommandOptions, ValueTask<int>>? PermissionMode { get; init; }

    /// <summary>Gets what shows or changes whether agents of other machines may start sessions on the node; does nothing by default.</summary>
    public Func<NodeAgentCommandOptions, ValueTask<int>> Agents { get; init; } = static _ => ValueTask.FromResult(0);

    /// <summary>Handlers that do nothing, for a tree that only parses.</summary>
    public static NodeCommandHandlers Placeholders { get; } = new(static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0),
        static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0),
        static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0),
        static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0),
        static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0), static _ => ValueTask.FromResult(0));
}

/// <summary>The command tree of <c>altanode</c> (<c>doc/fleet/node.md</c>).</summary>
internal static class NodeCommandLine
{
    private const string _ = "";

    /// <summary>Creates the command tree. A tree is used once: its option callbacks fill the options of one invocation.</summary>
    public static CommandApp Create(NodeCommandHandlers handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        var run = new NodeRunOptions();
        var runCommand = new Command("run", "Run the node in the foreground (logs to the console)")
        {
            new CommandUsage("Usage: {NAME} [options]"),
            _,
            "Owns the profile (~/.alta, or its developer instance with --dev) as",
            "CodeAlta Desktop and TUI do: only one instance runs on a profile. Runs its",
            "sessions with the configured providers and the plugins made for hosts",
            "without a window. Permission requests are never granted by the node: they",
            "wait for a person. Logs go to logs/altanode.log of the state root and to",
            "the console. Ctrl+C or SIGTERM stops it: no new work, running turns get a",
            "bound to finish; a second one stops them at once, a third exits.",
            _,
            "Listens for the devices paired with it (altanode pair) on port 2585 of",
            "the loopback address only, unless --bind or --listen-all says otherwise.",
            _,
            "Options:",
        };
        AddRunOptions(runCommand, run);
        runCommand.Add("service", "Answer the Windows Service Control Manager (what install --system starts)", v => run.Service = v is not null);
        AddProfile(runCommand, run.Profile);
        runCommand.Add((_, _) => handlers.Run(run));

        var status = new NodeProfileOptions();
        var statusCommand = new Command("status", "Tell what the node of the profile does and whether it will still run after a restart")
        {
            new CommandUsage("Usage: {NAME} [options]"),
            _,
            "Reads the lock of the profile: exit 0 when a CodeAlta instance (a node,",
            "CodeAlta Desktop or TUI) holds it, 1 when none does. For a node that",
            "listens: where, its paired devices and its sessions at work. Then the",
            "service of the profile (altanode install), and whether the node will",
            "still run after a restart of the machine or a logout of the user.",
            _,
            "Options:",
        };
        AddProfile(statusCommand, status);
        statusCommand.Add((_, _) => handlers.Status(status));

        var install = new NodeInstallOptions();
        var installCommand = new Command("install", "Install and start the service of this OS")
        {
            new CommandUsage("Usage: {NAME} [--user] [run options] [options]"),
            new CommandUsage("       {NAME} --system [--account <name> [--password-stdin]] [run options] [options]"),
            _,
            "Registers a service that runs altanode run with the options given here,",
            "and starts it. --user (the default) runs it as you, with your logins:",
            "  Windows  a Task Scheduler task at your logon (no elevation needed)",
            "  macOS    a LaunchAgent in ~/Library/LaunchAgents",
            "  Linux    a systemd --user unit, with lingering so that it runs",
            "           without a login (loginctl enable-linger)",
            "--system runs it at boot, without a login, as --account: a LaunchDaemon",
            "or a system unit (with sudo), or a Windows service (from an elevated",
            "terminal), as its own account NT SERVICE\\altanode with its profile",
            "under %ProgramData%, or as --account with its password on standard input",
            "(--password-stdin; Windows keeps it, CodeAlta does not).",
            "A .NET tool is started from its shim (~/.dotnet/tools/altanode), so",
            "dotnet tool update changes the version the service runs. Run install",
            "again to change the options; altanode uninstall removes the service.",
            _,
            "Options:",
            { "user", "A service of the current user (the default)", v => install.User = v is not null },
            { "system", "A system service, started at boot without a login", v => install.System = v is not null },
            { "account=", "The {ACCOUNT} a system service runs as", v => install.Account = v },
            { "password-stdin", "Read the password of --account from standard input (Windows)", v => install.PasswordStdin = v is not null },
            { "dry-run", "Print the service and the commands; change nothing", v => install.DryRun = v is not null },
        };
        AddRunOptions(installCommand, install.Run);
        AddProfile(installCommand, install.Run.Profile);
        installCommand.Add((_, _) => handlers.Install(install));

        var uninstall = new NodeInstallOptions();
        var uninstallCommand = new Command("uninstall", "Stop and remove the service; keep the profile")
        {
            new CommandUsage("Usage: {NAME} [--system --account <name>] [options]"),
            _,
            "Stops the service of the profile and removes it; the profile, its",
            "sessions and its paired devices stay.",
            _,
            "Options:",
            { "system", "The system service", v => uninstall.System = v is not null },
            { "account=", "The {ACCOUNT} the system service runs as", v => uninstall.Account = v },
            { "dry-run", "Print the commands; change nothing", v => uninstall.DryRun = v is not null },
        };
        AddProfile(uninstallCommand, uninstall.Run.Profile);
        uninstallCommand.Add((_, _) => handlers.Uninstall(uninstall));

        var devices = new NodeDeviceOptions();
        var devicesCommand = new Command("devices", "List the devices paired with the node")
        {
            new CommandUsage("Usage: {NAME} [options]"),
            _,
            "Lists the devices paired with the node of the profile, the revoked ones",
            "too: name, device id, the start of the fingerprint of its key, what it",
            "may do, when it was paired, and whether it is connected or revoked.",
            _,
            "Options:",
        };
        AddProfile(devicesCommand, devices.Profile);
        devicesCommand.Add("<>", "No argument");
        devicesCommand.Add((_, words) =>
        {
            devices.Words.AddRange(words);
            return handlers.Devices(devices);
        });

        var revoke = new NodeDeviceOptions();
        var revokeCommand = new Command("revoke", "Revoke a paired device and disconnect it")
        {
            new CommandUsage("Usage: {NAME} <device> [options]"),
            _,
            "The device is its name, its device id, or the start of the id (at least",
            "four characters), as altanode devices shows them. The node refuses its",
            "key from now on and closes its open channels; pairing it again trusts it",
            "again. Without a running node, the change waits in the profile.",
            _,
            "Options:",
        };
        AddProfile(revokeCommand, revoke.Profile);
        revokeCommand.Add("<>", "The device");
        revokeCommand.Add((_, words) =>
        {
            revoke.Words.AddRange(words);
            return handlers.Revoke(revoke);
        });

        var permissions = new NodeDeviceOptions();
        var permissionsCommand = new Command("permissions", "Show or change what a paired device may do")
        {
            new CommandUsage("Usage: {NAME} <device> [--allow <list>] [--deny <list>] [options]"),
            _,
            "The permissions are view (see the sessions), drive (send prompts, answer",
            "questions and permission requests), start (start sessions here), move",
            "(move sessions here), automate (run its automations here) and manage",
            "(manage the node from its desktop: devices, projects, log, updates).",
            "Lists are separated by commas: --allow view,drive.",
            "Without --allow or --deny, shows what the device may do. A device that",
            "loses a permission is disconnected and reconnects with what it may do.",
            _,
            "Options:",
            { "allow=", "The permissions to give ({LIST}: view,drive,start,move,automate,manage)", v => permissions.Allow.Add(v!) },
            { "deny=", "The permissions to take away ({LIST})", v => permissions.Deny.Add(v!) },
        };
        AddProfile(permissionsCommand, permissions.Profile);
        permissionsCommand.Add("<>", "The device");
        permissionsCommand.Add((_, words) =>
        {
            permissions.Words.AddRange(words);
            return handlers.Permissions(permissions);
        });

        var pair = new NodePairOptions();
        var pairCommand = new Command("pair", "Show a pairing link of the running node and wait for a device")
        {
            new CommandUsage("Usage: {NAME} [options]"),
            _,
            "Asks the node that runs on the profile for a one-time pairing link (a",
            "codealta-pair:// link with its key, its addresses and a secret), prints",
            "it, and waits until a device uses it (altanode connect <link>) or it",
            "expires. The device then may view, drive and start sessions here; moving",
            "sessions here stays off. Only --manage lets it manage the node too (its",
            "devices, projects, secrets and updates), the first device included. Runs",
            "beside the node, as its owner: it reads the",
            "device key of the node from the profile and talks to it on this machine.",
            _,
            "Options:",
            { "minutes=", "How many {MINUTES} the link is valid (default 10)", v => pair.Minutes = v },
            { "manage", "The device that pairs manages the node from its desktop: what each device may do, projects, log, updates", v => pair.Manage = v is not null },
        };
        AddProfile(pairCommand, pair.Profile);
        pairCommand.Add((_, _) => handlers.Pair(pair));

        var machines = new NodeProfileOptions();
        var machinesCommand = new Command("machines", "List the machines this profile paired with")
        {
            new CommandUsage("Usage: {NAME} [options]"),
            _,
            "Reads fleet/paired-machines.json of the profile; runs beside CodeAlta",
            "Desktop or a node on the same profile.",
            _,
            "Options:",
        };
        AddProfile(machinesCommand, machines);
        machinesCommand.Add((_, _) => handlers.Machines(machines));

        var connect = new NodeConnectOptions();
        var connectCommand = new Command("connect", "Pair this profile with a node from its pairing link")
        {
            new CommandUsage("Usage: {NAME} <link> [options]"),
            _,
            "Connects to the node the link names (refusing any other key), redeems",
            "its secret and records the machine for altanode remote. Runs beside",
            "CodeAlta Desktop or a node on the same profile.",
            _,
            "Options:",
            { "name=", "The {NAME} the node shows for this device (default: the machine name)", v => connect.Name = v },
        };
        AddProfile(connectCommand, connect.Profile);
        connectCommand.Add("<>", "The pairing link");
        connectCommand.Add((_, words) =>
        {
            connect.Words.AddRange(words);
            return handlers.Connect(connect);
        });

        var remote = new NodeRemoteOptions();
        var remoteCommand = new Command("remote", "Read and drive the sessions of a paired machine")
        {
            new CommandUsage("Usage: {NAME} <machine> info"),
            new CommandUsage("       {NAME} <machine> sessions [--limit <n>]"),
            new CommandUsage("       {NAME} <machine> history <session> [--cursor <c>] [--forward]"),
            new CommandUsage("       {NAME} <machine> send <session> <prompt...>"),
            new CommandUsage("       {NAME} <machine> create [--project <id>] <prompt...>"),
            new CommandUsage("       {NAME} <machine> watch <session> [--count <n>]"),
            new CommandUsage("       {NAME} <machine> move <session> [--move-id <id>] [--clone]"),
            _,
            "The machine is the name or the device id altanode machines shows. Runs",
            "beside CodeAlta Desktop or a node on the same profile.",
            _,
            "move gives a session of the machine to the node of this profile, which",
            "must run (altanode run) and have a project of the session's repository",
            "(or clone it, with --clone). The machine must let this device move",
            "sessions (altanode permissions <device> --allow move, on the machine).",
            "A move that stopped goes on with the same --move-id.",
            _,
            "Options:",
            { "limit=", "The most sessions {N} a list shows (default 50)", v => remote.Limit = v },
            { "cursor=", "The {CURSOR} of the history window to read", v => remote.Cursor = v },
            { "forward", "Read the history from its start", v => remote.Forward = v is not null },
            { "count=", "Stop watching after {N} updates", v => remote.Count = v },
            { "project=", "The project {ID} of a new session", v => remote.Project = v },
            { "move-id=", "The {ID} of a move to go on with", v => remote.MoveId = v },
            { "clone", "Let the node clone the repository of the session when it has none", v => remote.Clone = v is not null },
        };
        AddProfile(remoteCommand, remote.Profile);
        remoteCommand.Add("<>", "The machine, the command and its arguments");
        remoteCommand.Add((_, words) =>
        {
            remote.Words.AddRange(words);
            return handlers.Remote(remote);
        });

        var moves = new NodeProfileOptions();
        var movesCommand = new Command("moves", "List the moves of sessions to and from this profile")
        {
            new CommandUsage("Usage: {NAME} [options]"),
            _,
            "Reads the records of the moves under fleet/moves of the state root, the",
            "latest first; runs beside CodeAlta Desktop or a node on the same profile.",
            _,
            "Options:",
        };
        AddProfile(movesCommand, moves);
        movesCommand.Add((_, _) => handlers.Moves(moves));

        var projects = new NodeProjectOptions();
        var projectsCommand = new Command("projects", "List, add, clone and remove the projects of the node")
        {
            new CommandUsage("Usage: {NAME} [list] [options]"),
            new CommandUsage("       {NAME} add <path> [--space <space>] [options]"),
            new CommandUsage("       {NAME} clone <url> [--space <space>] [options]"),
            new CommandUsage("       {NAME} remove <project> [options]"),
            _,
            "The projects of the node are those its sessions run in, and those the",
            "devices paired with it see and start sessions in.",
            "  list    the projects of the catalog, archived ones included",
            "  add     adds an existing folder of this machine",
            "  clone   clones a repository into ~/code/<host>/<owner>/<repo> and adds",
            "          it; a checkout of it already there is added as it is",
            "  remove  removes a project from the catalog (its id, slug or name);",
            "          its folder and files stay",
            "While a node runs on the profile, it makes the change, and does not",
            "remove a project that has sessions; otherwise the catalog files of the",
            "profile are changed directly. --space adds the project to a space too.",
            _,
            "Options:",
            { "space=", "The {SPACE} the project joins too, by id or name", v => projects.Space = v },
        };
        AddProfile(projectsCommand, projects.Profile);
        projectsCommand.Add("<>", "The command and its argument");
        projectsCommand.Add((_, words) =>
        {
            projects.Words.AddRange(words);
            return handlers.Projects(projects);
        });

        var automations = new NodeAutomationCommandOptions();
        var automationsCommand = new Command("automations", "List the automations given to the node, their runs, and its policy")
        {
            new CommandUsage("Usage: {NAME} [list] [options]"),
            new CommandUsage("       {NAME} runs [<automation>] [options]"),
            new CommandUsage("       {NAME} policy [on|off|credentials any|account] [options]"),
            _,
            "The automations other devices gave this machine (Runs on, in CodeAlta",
            "Desktop), directly or through a CodeAlta Server.",
            "  list      what was given, by which device, which way, which version",
            "  runs      their latest runs, of all or of one automation",
            "  policy    whether this machine runs them (on, off), and with which",
            "            credentials: any it has, or accounts only (API keys and",
            "            local models, never a personal plan)",
            "A node that runs reads the policy again before each run.",
            _,
            "Options:",
        };
        AddProfile(automationsCommand, automations.Profile);
        automationsCommand.Add("<>", "The command and its arguments");
        automationsCommand.Add((_, words) =>
        {
            automations.Words.AddRange(words);
            return handlers.Automations(automations);
        });

        var secrets = new NodeSecretCommandOptions();
        var secretsCommand = new Command("secrets", "List, set, rotate and delete the secrets and variables of this machine")
        {
            new CommandUsage("Usage: {NAME} [list] [options]"),
            new CommandUsage("       {NAME} set|rotate <name> [options]"),
            new CommandUsage("       {NAME} delete <name> [options]"),
            new CommandUsage("       {NAME} set-var <name> <value> [options]"),
            new CommandUsage("       {NAME} delete-var <name> [options]"),
            new CommandUsage("       {NAME} key-file [options]"),
            _,
            "The secrets and variables of this machine, which the configuration names",
            "with ${secret:NAME}, ${var:NAME} and api_key_secret, as CodeAlta Desktop",
            "shows them in Settings > Secrets. A secret is never shown again.",
            "  list        the secrets (name, version, when, by whom) and the variables",
            "  set         sets a secret, or gives it a new version; rotate needs it set",
            "  delete      deletes a secret",
            "  set-var     sets a variable, which is not secret",
            "  delete-var  deletes a variable",
            "  key-file    moves the key of the secrets from the Keychain or libsecret",
            "              to a file only you read, for a node started at boot",
            "              (install --system): run it as yourself, without sudo",
            "The value of a secret is read from the standard input, or typed at a",
            "prompt that does not show it: never given as an argument.",
            "  echo $TOKEN | altanode secrets set GRAFANA_TOKEN",
            _,
            "Options:",
        };
        AddProfile(secretsCommand, secrets.Profile);
        secretsCommand.Add("<>", "The command and its arguments");
        secretsCommand.Add((_, words) =>
        {
            secrets.Words.AddRange(words);
            return handlers.Secrets?.Invoke(secrets) ?? handlers.NotImplemented("secrets");
        });

        var labels = new NodeLabelCommandOptions();
        var labelsCommand = new Command("labels", "List and change the labels of this machine")
        {
            new CommandUsage("Usage: {NAME} [list] [options]"),
            new CommandUsage("       {NAME} add|remove <label>... [options]"),
            new CommandUsage("       {NAME} set [<label>...] [options]"),
            _,
            "The labels of this machine, such as gpu or prod: it publishes them to its",
            "servers, and an environment of the fleet runs only on the machines with",
            "one of its labels. CodeAlta Desktop changes them on the page of the machine.",
            "  list    the labels",
            "  add     adds labels",
            "  remove  removes labels",
            "  set     replaces the labels; set alone removes them all",
            _,
            "Options:",
        };
        AddProfile(labelsCommand, labels.Profile);
        labelsCommand.Add("<>", "The command and its labels");
        labelsCommand.Add((_, words) =>
        {
            labels.Words.AddRange(words);
            return handlers.Labels?.Invoke(labels) ?? handlers.NotImplemented("labels");
        });

        var llama = new NodeLlamaCommandOptions();
        var llamaCommand = new Command("llama", "Tell the llama.cpp builds, models and servers of the running node")
        {
            new CommandUsage("Usage: {NAME} [status] [options]"),
            _,
            "The GPUs of this machine, its llama.cpp builds and their update policy,",
            "the models on its disk and their downloads, and its model servers with",
            "what they load. CodeAlta Desktop changes them, on Settings > Fleet >",
            "Model servers; the servers run with the node.",
            _,
            "Options:",
        };
        AddProfile(llamaCommand, llama.Profile);
        llamaCommand.Add("<>", "The command: status");
        llamaCommand.Add((_, words) =>
        {
            llama.Words.AddRange(words);
            return handlers.Llama?.Invoke(llama) ?? handlers.NotImplemented("llama");
        });

        var permissionMode = new NodePermissionModeCommandOptions();
        var permissionModeCommand = new Command("permission-mode", "Show or change whether the sessions of this machine ask before they act")
        {
            new CommandUsage("Usage: {NAME} [bypass|ask] [options]"),
            _,
            "The permission mode of a session of this machine that has none of its",
            "own: a session started from another computer, a run of an automation.",
            "  bypass  runs commands and changes files without asking (the default)",
            "  ask     waits for a person, who answers from a paired computer",
            "A policy of the organization narrows it, and a session an agent of",
            "another machine starts has at most the mode of its parent. CodeAlta",
            "Desktop changes it on the page of the machine. Without a mode: shows it.",
            _,
            "Options:",
        };
        AddProfile(permissionModeCommand, permissionMode.Profile);
        permissionModeCommand.Add("<>", "bypass or ask");
        permissionModeCommand.Add((_, words) =>
        {
            permissionMode.Words.AddRange(words);
            return handlers.PermissionMode?.Invoke(permissionMode) ?? handlers.NotImplemented("permission-mode");
        });

        var agents = new NodeAgentCommandOptions();
        var agentsCommand = new Command("agents", "Show or change whether agents of other machines may start sessions here")
        {
            new CommandUsage("Usage: {NAME} [show] [options]"),
            new CommandUsage("       {NAME} allow --space <id> [--sandbox <profile>|none] [--bypass|--no-bypass] [options]"),
            new CommandUsage("       {NAME} deny (--space <id> | --all) [options]"),
            _,
            "An agent of a session of another machine of the same person may start a",
            "session here, the child of its own, and send prompts to the sessions",
            "here, only in the spaces allowed: none by default. Such a session has at",
            "most what its parent has (tools, permission mode, sandbox), runs in the",
            "sandbox named here at least, and never bypasses permissions unless",
            "--bypass allows it. A node that runs reads the policy at each request.",
            _,
            "Options:",
            { "space=", "A space ({ID}); may be repeated", v => agents.Spaces.Add(v!) },
            { "sandbox=", "The sandbox {PROFILE} their sessions run in at least, or none", v => agents.Sandbox = v },
            { "bypass", "Their sessions may bypass permissions when their parent does", v => agents.Bypass = v is not null ? true : null },
            { "no-bypass", "Their sessions never bypass permissions (the default)", v => agents.Bypass = v is not null ? false : null },
            { "all", "With deny: every space", v => agents.All = v is not null },
        };
        AddProfile(agentsCommand, agents.Profile);
        agentsCommand.Add("<>", "show, allow or deny");
        agentsCommand.Add((_, words) =>
        {
            agents.Words.AddRange(words);
            return handlers.Agents(agents);
        });

        var join = new NodeJoinOptions();
        var joinCommand = new Command("join", "Enroll the node in a CodeAlta Server and wait for its approval")
        {
            new CommandUsage("Usage: {NAME} <server-url> --token <enrollment-token> [options]"),
            _,
            "Registers the device key of the node with the server, with an enrollment",
            "token an admin made (Settings > Fleet > Server of CodeAlta Desktop, or the",
            "server's API), and waits until a controller device approves it. The node",
            "that runs on the profile then keeps a connection to the server: its",
            "presence, its machine and the facts of the sessions of the linked spaces",
            "(state, times, provider and model; never their content). Private",
            "sessions are never published. Runs beside the node, as its owner.",
            _,
            "Options:",
            { "token=", "The enrollment {TOKEN}", v => join.Token = v },
            { "name=", "The {NAME} of the node at the server (default: the machine name)", v => join.Name = v },
            { "space=", "A {SPACE} whose sessions it publishes, by id or name (default: the default space); repeatable", v => join.Spaces.Add(v!) },
            { "no-wait", "Return once enrolled, without waiting for the approval", v => join.NoWait = v is not null },
        };
        AddProfile(joinCommand, join.Profile);
        joinCommand.Add("<>", "The address of the server");
        joinCommand.Add((_, words) =>
        {
            join.Words.AddRange(words);
            return handlers.Join(join);
        });

        var logs = new NodeLogOptions();
        var logsCommand = new Command("logs", "Show the node's log")
        {
            new CommandUsage("Usage: {NAME} [--follow] [--lines <n>] [options]"),
            _,
            "Prints the last lines of logs/altanode.log of the state root, the log the",
            "node writes (rolled daily and bounded); with --follow, what it writes",
            "next until Ctrl+C.",
            _,
            "Options:",
            { "f|follow", "Follow the log as it grows", v => logs.Follow = v is not null },
            { "n|lines=", "How many of the last {N} lines (default 50)", v => logs.Lines = v },
        };
        AddProfile(logsCommand, logs.Profile);
        logsCommand.Add((_, _) => handlers.Logs(logs));

        var update = new NodeUpdateOptions();
        var updateCommand = new Command("update", "Update the node to a new version, when it is idle")
        {
            new CommandUsage("Usage: {NAME} [--version <v>] [--source <feed>] [--check] [--now] [options]"),
            _,
            "Downloads the package of this OS and architecture from a NuGet feed",
            "(nuget.org by default), checks its hash, and puts it beside the others in",
            "fleet/versions of the state root. The node of the profile switches to it",
            "once no session works: it takes no new work while it waits (10 minutes",
            "at most, then until the next idle moment), stops and starts the new",
            "version, which the service keeps running. Without a running node, the",
            "next altanode run starts it. The new version runs on trial: if it does",
            "not confirm that it works within 2 minutes, or crashes, the previous one",
            "comes back and the new one is marked bad. No .NET SDK is needed.",
            "Without --version: the latest release (a prerelease when one runs).",
            "fleet/update.json of the state root sets the feed and the allowed feeds:",
            "{ \"source\": \"<feed>\", \"allowedSources\": [\"<feed>\", ...] }.",
            _,
            "Options:",
            { "version=", "The {VERSION} to install, also an older one", v => update.Version = v },
            { "source=", "The {FEED}: a NuGet v3 index (https://) or a folder", v => update.Source = v },
            { "sha512=", "The {HASH} the package must have (base64 or hexadecimal)", v => update.Sha512 = v },
            { "check", "Only tell whether an update is available", v => update.Check = v is not null },
            { "now", "Switch without waiting for the sessions at work", v => update.Now = v is not null },
            { "prerelease", "Take a prerelease as the latest version", v => update.Prerelease = v is not null },
        };
        AddProfile(updateCommand, update.Profile);
        updateCommand.Add("<>", "No argument");
        updateCommand.Add((_, words) =>
        {
            update.Words.AddRange(words);
            return handlers.Update(update);
        });

        var app = new CommandApp("altanode", config: new CommandConfig { OutputFactory = static _ => DefaultCommandOutput.Instance })
        {
            new CommandUsage("Usage: {NAME} <command> [options]"),
            new CommandUsage("Usage: {NAME} <command> --help"),
            _,
            "CodeAlta Node: the headless CodeAlta host of an always-on machine, a build",
            "machine or a cloud VM, driven from CodeAlta Desktop.",
            _,
            "Commands:",
            runCommand,
            statusCommand,
            installCommand,
            uninstallCommand,
            logsCommand,
            pairCommand,
            devicesCommand,
            revokeCommand,
            permissionsCommand,
            projectsCommand,
            automationsCommand,
            secretsCommand,
            labelsCommand,
            llamaCommand,
            permissionModeCommand,
            agentsCommand,
            joinCommand,
            machinesCommand,
            connectCommand,
            remoteCommand,
            movesCommand,
            updateCommand,
            _,
            "Exit codes:",
            "  0 succeeded (status: the profile is held)   1 failed (status: not held)",
            "  2 invalid usage   3 the profile is held by another instance",
            "  4 not implemented yet",
            _,
            new HelpOption(),
            new VersionOption(),
            _,
        };
        return app;
    }

    private static void AddRunOptions(Command command, NodeRunOptions run)
    {
        command.Add("offline", "Do not refresh the catalog of models.dev", v => run.Offline = v is not null);
        command.Add("no-plugins", "Start no plugin", v => run.NoPlugins = v is not null);
        command.Add("port=", "The {PORT} of the listener (default 2585; 0 picks a free one)", v => run.Port = v);
        command.Add("bind=", "The {ADDRESS} the listener binds (default 127.0.0.1)", v => run.Bind = v);
        command.Add("listen-all", "Listen on every interface (the LAN, a tailnet)", v => run.ListenAll = v is not null);
        command.Add("no-listen", "Listen for no device: no pairing, no remote session", v => run.NoListen = v is not null);
        command.Add("name=", "The {NAME} paired devices see (default: the machine name)", v => run.Name = v);
    }

    private static void AddProfile(Command command, NodeProfileOptions options)
    {
        command.Add("dev", "The developer instance's state root (~/.alta/dev), as alta --dev", v => options.Developer = v is not null);
        command.Add("data-root=", "The profile {DIRECTORY} in place of ~/.alta (an absolute path)", v => options.DataRoot = v);
        command.Add(new HelpOption());
    }
}
