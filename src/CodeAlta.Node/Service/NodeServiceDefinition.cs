using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodeAlta.Node.Service;

/// <summary>The operating systems whose service managers <c>altanode install</c> knows.</summary>
internal enum NodeServicePlatform
{
    /// <summary>Windows: a Task Scheduler task.</summary>
    Windows,

    /// <summary>macOS: a launchd LaunchAgent or LaunchDaemon.</summary>
    MacOS,

    /// <summary>Linux: a systemd unit.</summary>
    Linux,
}

/// <summary>Whom the service of a node runs as.</summary>
internal enum NodeServiceScope
{
    /// <summary>The current user, with the logins of their session (a task at logon, a LaunchAgent, a <c>systemd --user</c> unit).</summary>
    User,

    /// <summary>A dedicated account, started at boot without a login (a LaunchDaemon, a system unit), for machines that use deploy keys only.</summary>
    System,
}

/// <summary>What a service of a node runs: <c>altanode run</c> with the options given at install, from a stable path.</summary>
internal sealed record NodeServiceSpec
{
    /// <summary>Gets the name of the service: <c>altanode</c>, <c>altanode-dev</c>, or a name of its own for another profile.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the executable the service starts.</summary>
    public required string Executable { get; init; }

    /// <summary>Gets its arguments: <c>run</c> and its options (after the path of the assembly when the executable is <c>dotnet</c>).</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Gets the folder it starts in: the home of the account.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Gets the variables the service is given (<c>PATH</c>, <c>DOTNET_ROOT</c>), by name; launchd and systemd start with almost none.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets whom it runs as.</summary>
    public NodeServiceScope Scope { get; init; } = NodeServiceScope.User;

    /// <summary>Gets the account of a system service; null for a service of the user.</summary>
    public string? Account { get; init; }

    /// <summary>Gets where launchd writes what the node prints on standard error before its own log opens; null for nowhere.</summary>
    public string? StandardErrorPath { get; init; }
}

/// <summary>
/// The definitions of the service of a node, as the service managers read them: a Task Scheduler task, a launchd property
/// list, a systemd unit. Pure functions of a <see cref="NodeServiceSpec"/>, tested on every OS.
/// </summary>
internal static partial class NodeServiceDefinition
{
    /// <summary>The prefix of the launchd labels.</summary>
    public const string LaunchdLabelPrefix = "io.codealta.";

    /// <summary>The folder of the Task Scheduler tasks.</summary>
    public const string TaskFolder = "CodeAlta";

    /// <summary>
    /// How often the task starts the node again if it stopped, while the user is signed in; a start while it runs is
    /// ignored. A repeating time trigger does it: <c>conhost.exe --headless</c> ends with 0 whatever the node's exit code,
    /// so Task Scheduler never sees a failure to restart, and a logon trigger repeats only after a logon.
    /// </summary>
    public static readonly TimeSpan TaskRepetition = TimeSpan.FromMinutes(5);

    /// <summary>How long a service manager waits for a graceful stop: the node gives turns 30 s, then the host 15 s.</summary>
    public const int StopTimeoutSeconds = 60;

    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>Names the service of a profile.</summary>
    /// <param name="stateRoot">The state root the node owns.</param>
    /// <param name="defaultProfile">Whether the profile is <c>~/.alta</c> (no <c>--data-root</c>).</param>
    /// <param name="developer">Whether it is the developer instance (<c>--dev</c>).</param>
    /// <returns><c>altanode</c>, <c>altanode-dev</c>, or <c>altanode-</c> and eight hex digits of the state root for another profile.</returns>
    public static string ServiceName(string stateRoot, bool defaultProfile, bool developer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        if (defaultProfile) return developer ? "altanode-dev" : "altanode";
        var normalized = Path.TrimEndingDirectorySeparator(stateRoot.Replace('\\', '/'));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) normalized = normalized.ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "altanode-" + Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }

    /// <summary>Gets whether a text is an account name a system service can run as.</summary>
    /// <param name="account">The name.</param>
    /// <returns>Whether it is 1 to 32 letters, digits, <c>_</c>, <c>.</c> or <c>-</c>, not starting with <c>-</c> or <c>.</c>.</returns>
    public static bool IsValidAccount(string? account) => account is not null && AccountPattern().IsMatch(account);

    /// <summary>Names the service of a profile for a system account: one per account.</summary>
    /// <param name="name">The name of the service of the profile.</param>
    /// <param name="account">The account.</param>
    /// <returns>The name.</returns>
    public static string SystemServiceName(string name, string account) => $"{name}-{account}";

    /// <summary>Gets the path of the task in Task Scheduler: <c>\CodeAlta\altanode</c>.</summary>
    /// <param name="name">The name of the service.</param>
    /// <returns>The path of the task.</returns>
    public static string WindowsTaskName(string name) => $@"\{TaskFolder}\{name}";

    /// <summary>Gets the label of the launchd job: <c>io.codealta.altanode</c>.</summary>
    /// <param name="name">The name of the service.</param>
    /// <returns>The label.</returns>
    public static string LaunchdLabel(string name) => LaunchdLabelPrefix + name;

    /// <summary>
    /// Writes the Task Scheduler task of a node: started at the logon of the user, as the user (their token, so their
    /// Credential Manager, ssh-agent and DPAPI keys), with the least privileges, never stopped for a time limit or a
    /// battery, started again every few minutes if it stopped (<see cref="TaskRepetition"/>), without a console window
    /// (<c>conhost.exe --headless</c>).
    /// </summary>
    /// <param name="spec">The service.</param>
    /// <param name="userId">The user: their SID, or <c>DOMAIN\name</c>.</param>
    /// <param name="conhostPath">The path of <c>conhost.exe</c>.</param>
    /// <param name="installedAt">When it is installed, local time: the start of the repetition.</param>
    /// <returns>The task XML, to save as UTF-16 for <c>schtasks /Create /XML</c>.</returns>
    /// <exception cref="ArgumentException">The service is not a service of the user.</exception>
    public static string WindowsTaskXml(NodeServiceSpec spec, string userId, string conhostPath, DateTime installedAt)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conhostPath);
        if (spec.Scope != NodeServiceScope.User) throw new ArgumentException("A task at logon runs as the user only.", nameof(spec));
        var t = TaskNamespace;
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(t + "Task", new XAttribute("version", "1.4"),
                new XElement(t + "RegistrationInfo",
                    new XElement(t + "Author", "CodeAlta"),
                    new XElement(t + "Description", "CodeAlta Node (altanode run), the headless CodeAlta host of this user. Written by altanode install; altanode uninstall removes it."),
                    new XElement(t + "URI", WindowsTaskName(spec.Name))),
                new XElement(t + "Triggers",
                    new XElement(t + "LogonTrigger",
                        new XElement(t + "Enabled", "true"),
                        new XElement(t + "UserId", userId)),
                    new XElement(t + "TimeTrigger",
                        new XElement(t + "Repetition",
                            new XElement(t + "Interval", Duration(TaskRepetition)),
                            new XElement(t + "StopAtDurationEnd", "false")),
                        new XElement(t + "StartBoundary", installedAt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
                        new XElement(t + "Enabled", "true"))),
                new XElement(t + "Principals",
                    new XElement(t + "Principal", new XAttribute("id", "Author"),
                        new XElement(t + "UserId", userId),
                        new XElement(t + "LogonType", "InteractiveToken"),
                        new XElement(t + "RunLevel", "LeastPrivilege"))),
                new XElement(t + "Settings",
                    new XElement(t + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(t + "DisallowStartIfOnBatteries", "false"),
                    new XElement(t + "StopIfGoingOnBatteries", "false"),
                    new XElement(t + "AllowHardTerminate", "true"),
                    new XElement(t + "StartWhenAvailable", "true"),
                    new XElement(t + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(t + "IdleSettings",
                        new XElement(t + "StopOnIdleEnd", "false"),
                        new XElement(t + "RestartOnIdle", "false")),
                    new XElement(t + "AllowStartOnDemand", "true"),
                    new XElement(t + "Enabled", "true"),
                    new XElement(t + "Hidden", "false"),
                    new XElement(t + "RunOnlyIfIdle", "false"),
                    new XElement(t + "WakeToRun", "false"),
                    new XElement(t + "ExecutionTimeLimit", "PT0S"),
                    new XElement(t + "Priority", "7"),
                    new XElement(t + "RestartOnFailure",
                        new XElement(t + "Interval", "PT1M"),
                        new XElement(t + "Count", "999"))),
                new XElement(t + "Actions", new XAttribute("Context", "Author"),
                    new XElement(t + "Exec",
                        new XElement(t + "Command", conhostPath),
                        new XElement(t + "Arguments", "--headless " + WindowsCommandLine([spec.Executable, .. spec.Arguments])),
                        new XElement(t + "WorkingDirectory", spec.WorkingDirectory)))));
        return document.Declaration + "\r\n" + document.ToString().ReplaceLineEndings("\r\n") + "\r\n";
    }

    /// <summary>
    /// Writes the launchd property list of a node: a LaunchAgent of the user (loaded in their login session, with their
    /// keychain), or a LaunchDaemon with <c>UserName</c> for a system service. Started at load, started again when it
    /// fails (not after a clean stop), given a minute to stop.
    /// </summary>
    /// <param name="spec">The service.</param>
    /// <returns>The property list.</returns>
    public static string LaunchdPlist(NodeServiceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var entries = new List<XElement>();
        void Add(string key, XElement value)
        {
            entries.Add(new XElement("key", key));
            entries.Add(value);
        }

        Add("Label", new XElement("string", LaunchdLabel(spec.Scope == NodeServiceScope.System ? SystemServiceName(spec.Name, spec.Account!) : spec.Name)));
        if (spec.Scope == NodeServiceScope.System) Add("UserName", new XElement("string", spec.Account));
        Add("ProgramArguments", new XElement("array", new[] { spec.Executable }.Concat(spec.Arguments).Select(static argument => new XElement("string", argument))));
        Add("WorkingDirectory", new XElement("string", spec.WorkingDirectory));
        if (spec.Environment.Count > 0)
        {
            Add("EnvironmentVariables", new XElement("dict", spec.Environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .SelectMany(static pair => new[] { new XElement("key", pair.Key), new XElement("string", pair.Value) })));
        }

        Add("RunAtLoad", new XElement("true"));
        Add("KeepAlive", new XElement("dict", new XElement("key", "SuccessfulExit"), new XElement("false")));
        Add("ThrottleInterval", new XElement("integer", "30"));
        Add("ExitTimeOut", new XElement("integer", StopTimeoutSeconds.ToString(CultureInfo.InvariantCulture)));
        // The node writes its own bounded log; standard error keeps what fails before it opens.
        Add("StandardOutPath", new XElement("string", "/dev/null"));
        if (spec.StandardErrorPath is { } errorPath) Add("StandardErrorPath", new XElement("string", errorPath));
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict", entries)));
        return document.Declaration + "\n" + document.ToString().ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>
    /// Writes the systemd unit of a node: a <c>systemd --user</c> unit of the user, or a system unit with <c>User=</c>.
    /// Restarted on failure (not after an invalid command line, which never fixes itself), stopped with SIGTERM to the
    /// node alone so that it drains its turns, its log kept by the node.
    /// </summary>
    /// <param name="spec">The service.</param>
    /// <returns>The unit.</returns>
    public static string SystemdUnit(NodeServiceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var system = spec.Scope == NodeServiceScope.System;
        var unit = new StringBuilder();
        unit.Append("# Written by altanode install; altanode uninstall removes it.\n");
        unit.Append("[Unit]\n");
        unit.Append("Description=CodeAlta Node (altanode)\n");
        unit.Append("Documentation=https://codealta.github.io/\n");
        if (system)
        {
            unit.Append("Wants=network-online.target\n");
            unit.Append("After=network-online.target\n");
        }

        unit.Append("\n[Service]\n");
        unit.Append("Type=simple\n");
        if (system) unit.Append("User=").Append(spec.Account).Append('\n');
        unit.Append("ExecStart=").Append(string.Join(' ', new[] { spec.Executable }.Concat(spec.Arguments).Select(static argument => SystemdQuote(argument, command: true)))).Append('\n');
        unit.Append("WorkingDirectory=").Append(system ? "~" : "%h").Append('\n');
        foreach (var (name, value) in spec.Environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            unit.Append("Environment=").Append(SystemdQuote($"{name}={value}", command: false)).Append('\n');
        unit.Append("Restart=on-failure\n");
        // A node refused because another instance holds the profile tries again, not in a tight loop.
        unit.Append("RestartSec=30\n");
        unit.Append("RestartPreventExitStatus=").Append(NodeExitCodes.InvalidUsage.ToString(CultureInfo.InvariantCulture)).Append('\n');
        unit.Append("KillMode=mixed\n");
        unit.Append("TimeoutStopSec=").Append(StopTimeoutSeconds.ToString(CultureInfo.InvariantCulture)).Append('\n');
        unit.Append("StandardOutput=null\n");
        unit.Append("StandardError=journal\n");
        unit.Append("\n[Install]\n");
        unit.Append("WantedBy=").Append(system ? "multi-user.target" : "default.target").Append('\n');
        return unit.ToString();
    }

    /// <summary>
    /// Quotes a word for a systemd unit: in double quotes when it needs them, with <c>\</c> and <c>"</c> escaped, <c>%</c>
    /// doubled (specifiers), and <c>$</c> doubled in a command line (variables).
    /// </summary>
    /// <param name="value">The word.</param>
    /// <param name="command">Whether it goes in <c>ExecStart=</c>, where <c>$</c> expands.</param>
    /// <returns>The quoted word.</returns>
    public static string SystemdQuote(string value, bool command)
    {
        ArgumentNullException.ThrowIfNull(value);
        var escaped = new StringBuilder(value.Length + 2);
        var needsQuotes = value.Length == 0;
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': escaped.Append(@"\\"); needsQuotes = true; break;
                case '"': escaped.Append("\\\""); needsQuotes = true; break;
                case '\'': escaped.Append("\\'"); needsQuotes = true; break;
                case '%': escaped.Append("%%"); break;
                case '$' when command: escaped.Append("$$"); break;
                case '\n': escaped.Append(@"\n"); needsQuotes = true; break;
                default:
                    if (char.IsWhiteSpace(c) || c == ';') needsQuotes = true;
                    escaped.Append(c);
                    break;
            }
        }

        return needsQuotes ? "\"" + escaped + "\"" : escaped.ToString();
    }

    /// <summary>Gets whether a Windows executable is a batch file (<c>.cmd</c>, <c>.bat</c>), which Windows runs through <c>cmd.exe /c</c>.</summary>
    /// <param name="path">The path of the executable.</param>
    /// <returns>Whether it is a batch file.</returns>
    public static bool IsWindowsBatchFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds a character <c>cmd.exe</c> would not pass as it is in a word of the command line of a batch file started
    /// by the task: <c>"</c> (its quotes are not escaped as <c>CommandLineToArgvW</c> escapes them), <c>%</c> (it
    /// expands variables) and control characters anywhere, and <c>^ &amp; | &lt; &gt;</c> in a word without a space,
    /// which <c>conhost.exe</c> passes without quotes.
    /// </summary>
    /// <param name="word">The word: the path of the batch file or one of its arguments.</param>
    /// <returns>The character, or null when <c>cmd.exe</c> passes the word as it is.</returns>
    public static char? WindowsBatchUnsafeCharacter(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        var quoted = word.Contains(' ', StringComparison.Ordinal);
        foreach (var c in word)
        {
            if (c is '"' or '%' || char.IsControl(c)) return c;
            if (!quoted && c is '^' or '&' or '|' or '<' or '>') return c;
        }

        return null;
    }

    /// <summary>Joins arguments into a Windows command line, as <c>CommandLineToArgvW</c> splits it.</summary>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The command line.</returns>
    public static string WindowsCommandLine(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return string.Join(' ', arguments.Select(WindowsQuote));
    }

    private static string WindowsQuote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return argument;
        var quoted = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes before a quote are doubled, and the quote escaped; elsewhere they stay as they are.
            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            quoted.Append(c);
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static string Duration(TimeSpan span) => $"PT{(int)span.TotalMinutes}M";

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,31}$")]
    private static partial Regex AccountPattern();
}
