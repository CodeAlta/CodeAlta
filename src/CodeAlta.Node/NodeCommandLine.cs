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
}

/// <summary>What the commands of <c>altanode</c> do once parsed.</summary>
/// <param name="Run">Runs the node in the foreground.</param>
/// <param name="Status">Tells whether an instance holds the profile.</param>
/// <param name="NotImplemented">A command of the surface that does not work yet, by its name.</param>
/// <param name="Pair">Makes a pairing link on the running node and waits for a device to use it.</param>
/// <param name="Machines">Lists the machines this profile paired with.</param>
/// <param name="Connect">Pairs this profile with a node from its pairing link.</param>
/// <param name="Remote">Reads or drives the sessions of a paired machine.</param>
internal sealed record NodeCommandHandlers(
    Func<NodeRunOptions, ValueTask<int>> Run,
    Func<NodeProfileOptions, ValueTask<int>> Status,
    Func<string, ValueTask<int>> NotImplemented,
    Func<NodePairOptions, ValueTask<int>> Pair,
    Func<NodeProfileOptions, ValueTask<int>> Machines,
    Func<NodeConnectOptions, ValueTask<int>> Connect,
    Func<NodeRemoteOptions, ValueTask<int>> Remote);

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
            { "offline", "Do not refresh the catalog of models.dev", v => run.Offline = v is not null },
            { "no-plugins", "Start no plugin", v => run.NoPlugins = v is not null },
            { "port=", "The {PORT} of the listener (default 2585; 0 picks a free one)", v => run.Port = v },
            { "bind=", "The {ADDRESS} the listener binds (default 127.0.0.1)", v => run.Bind = v },
            { "listen-all", "Listen on every interface (the LAN, a tailnet)", v => run.ListenAll = v is not null },
            { "no-listen", "Listen for no device: no pairing, no remote session", v => run.NoListen = v is not null },
            { "name=", "The {NAME} paired devices see (default: the machine name)", v => run.Name = v },
        };
        AddProfile(runCommand, run.Profile);
        runCommand.Add((_, _) => handlers.Run(run));

        var status = new NodeProfileOptions();
        var statusCommand = new Command("status", "Tell whether an instance holds the profile (exit 0 or 1)")
        {
            new CommandUsage("Usage: {NAME} [options]"),
            _,
            "Reads the lock of the profile: exit 0 when a CodeAlta instance (a node,",
            "CodeAlta Desktop or TUI) holds it, 1 when none does.",
            _,
            "Options:",
        };
        AddProfile(statusCommand, status);
        statusCommand.Add((_, _) => handlers.Status(status));

        var installCommand = Stub(handlers, "install", "Install and start the service of this OS",
            "Usage: {NAME} [--user | --system --account <name>]");
        installCommand.Add("user", "A service of the current user", static _ => { });
        installCommand.Add("system", "A system service", static _ => { });
        installCommand.Add("account=", "The {ACCOUNT} a system service runs as", static _ => { });
        installCommand.Add(new HelpOption());

        var uninstallCommand = Stub(handlers, "uninstall", "Stop and remove the service; keep the profile", "Usage: {NAME}");
        uninstallCommand.Add(new HelpOption());

        var pair = new NodePairOptions();
        var pairCommand = new Command("pair", "Show a pairing link of the running node and wait for a device")
        {
            new CommandUsage("Usage: {NAME} [options]"),
            _,
            "Asks the node that runs on the profile for a one-time pairing link (a",
            "codealta-pair:// link with its key, its addresses and a secret), prints",
            "it, and waits until a device uses it (altanode connect <link>) or it",
            "expires. The device then may view, drive and start sessions here; moving",
            "sessions here stays off. Runs beside the node, as its owner: it reads the",
            "device key of the node from the profile and talks to it on this machine.",
            _,
            "Options:",
            { "minutes=", "How many {MINUTES} the link is valid (default 10)", v => pair.Minutes = v },
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
            _,
            "The machine is the name or the device id altanode machines shows. Runs",
            "beside CodeAlta Desktop or a node on the same profile.",
            _,
            "Options:",
            { "limit=", "The most sessions {N} a list shows (default 50)", v => remote.Limit = v },
            { "cursor=", "The {CURSOR} of the history window to read", v => remote.Cursor = v },
            { "forward", "Read the history from its start", v => remote.Forward = v is not null },
            { "count=", "Stop watching after {N} updates", v => remote.Count = v },
            { "project=", "The project {ID} of a new session", v => remote.Project = v },
        };
        AddProfile(remoteCommand, remote.Profile);
        remoteCommand.Add("<>", "The machine, the command and its arguments");
        remoteCommand.Add((_, words) =>
        {
            remote.Words.AddRange(words);
            return handlers.Remote(remote);
        });

        var joinCommand = Stub(handlers, "join", "Enroll in a fleet", "Usage: {NAME} <server-url> [--token <enrollment>]");
        joinCommand.Add("token=", "The enrollment {TOKEN}", static _ => { });
        joinCommand.Add("<>", "The URL of the server");
        joinCommand.Add(new HelpOption());

        var logsCommand = Stub(handlers, "logs", "Show the node's log", "Usage: {NAME} [--follow]");
        logsCommand.Add("follow", "Follow the log as it grows", static _ => { });
        logsCommand.Add(new HelpOption());

        var updateCommand = Stub(handlers, "update", "Update to the version the fleet recommends, when idle", "Usage: {NAME}");
        updateCommand.Add(new HelpOption());

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
            pairCommand,
            joinCommand,
            machinesCommand,
            connectCommand,
            remoteCommand,
            logsCommand,
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

    private static Command Stub(NodeCommandHandlers handlers, string name, string description, string usage)
    {
        var command = new Command(name, description + " (not implemented yet)")
        {
            new CommandUsage(usage),
            _,
        };
        command.Add((_, _) => handlers.NotImplemented(name));
        return command;
    }

    private static void AddProfile(Command command, NodeProfileOptions options)
    {
        command.Add("dev", "The developer instance's state root (~/.alta/dev), as alta --dev", v => options.Developer = v is not null);
        command.Add("data-root=", "The profile {DIRECTORY} in place of ~/.alta (an absolute path)", v => options.DataRoot = v);
        command.Add(new HelpOption());
    }
}
