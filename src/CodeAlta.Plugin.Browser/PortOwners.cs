using System.Diagnostics;
using System.Globalization;
using System.Net;
using CodeAlta.Browser.Tools;

namespace CodeAlta.Plugin.Browser;

/// <summary>The program that listens on a port of this computer.</summary>
/// <param name="ProcessId">Its process.</param>
/// <param name="Command">Its command line, as the system shows it.</param>
/// <param name="WorkingDirectory">The folder it runs in, or null when the system does not say.</param>
internal sealed record PortOwner(int ProcessId, string Command, string? WorkingDirectory)
{
    /// <summary>
    /// The folder of the program inside the folder of a project, or null when it does not run there. Where the system
    /// does not tell the folder of another process (Windows), see <see cref="FolderIn(string, bool)"/>.
    /// </summary>
    /// <param name="project">The folder of the project, as <see cref="BrowserFiles.RealPath"/> gives it.</param>
    public string? FolderIn(string project) => FolderIn(project, byCommandLine: OperatingSystem.IsWindows());

    /// <summary>
    /// The folder of the program inside the folder of a project: the folder it runs in when the system tells it.
    /// Otherwise, with <paramref name="byCommandLine"/>, a program whose command line names a file or a folder of the
    /// project, as the script of a dev server (<c>node C:\app\node_modules\vite\bin\vite.js</c>) or a program built
    /// there (<c>C:\app\bin\Debug\App.exe</c>) do, counts as one of the project, with the folder of the project as its
    /// folder. A program started with paths relative to its folder only (<c>python manage.py runserver</c>) is then not
    /// found.
    /// </summary>
    internal string? FolderIn(string project, bool byCommandLine)
    {
        if (WorkingDirectory is { } directory)
        {
            var folder = Real(directory);
            return folder is not null && BrowserFiles.IsWithin(folder, project) ? folder : null;
        }

        if (!byCommandLine) return null;
        foreach (var path in SystemPortOwners.CommandLinePaths(Command))
        {
            if ((File.Exists(path) || Directory.Exists(path)) && Real(path) is { } real && BrowserFiles.IsWithin(real, project)) return project;
        }

        return null;
    }

    private static string? Real(string path)
    {
        try { return BrowserFiles.RealPath(path); }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>A port of this computer a program listens on, for the loopback.</summary>
/// <param name="Port">The port.</param>
/// <param name="Owner">The program.</param>
internal sealed record ListeningPort(int Port, PortOwner Owner);

/// <summary>A socket of this computer that listens: the address it is bound to, its port and its process.</summary>
/// <param name="ProcessId">The process of the socket.</param>
/// <param name="Address">The address as the system shows it: <c>*</c>, <c>0.0.0.0</c> or <c>[::]</c> for all of them, or one address.</param>
/// <param name="Port">The port.</param>
internal sealed record PortListener(int ProcessId, string Address, int Port);

/// <summary>Finds the program that listens on a port of the loopback.</summary>
internal interface IPortOwners
{
    /// <summary>
    /// The program that answers a page of the loopback at a host and a port, or null when none does, when several
    /// programs listen where the page could reach, or when the system does not say.
    /// </summary>
    /// <param name="host">The host of the page: <c>localhost</c> or an address of the loopback.</param>
    /// <param name="port">The port of the page.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    Task<PortOwner?> FindAsync(string host, int port, CancellationToken cancellationToken);

    /// <summary>The ports <c>localhost</c> reaches and the programs that listen on them, CodeAlta's own left out, in the order of the ports.</summary>
    Task<IReadOnlyList<ListeningPort>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The programs of this computer that listen on its ports, as <c>lsof</c> and <c>ps</c> tell on macOS and Linux, and
/// <c>netstat</c> and the command line of the process on Windows, which does not tell the folder of another process
/// without reading its memory (see <see cref="PortOwner.FolderIn(string, bool)"/>).
/// </summary>
/// <remarks>Nothing is remembered: a port can change hands at any time, and the page of a port is checked as it is now.</remarks>
internal sealed class SystemPortOwners : IPortOwners
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    public async Task<PortOwner?> FindAsync(string host, int port, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (port is <= 0 or > 65535) return null;
        var listeners = await ListenersAsync(port, cancellationToken).ConfigureAwait(false);
        if (OwnerOf(listeners, host, port, Environment.ProcessId) is not { } pid) return null;
        return (await DescribeAsync([pid], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ListeningPort>> ListAsync(CancellationToken cancellationToken)
    {
        var listeners = await ListenersAsync(null, cancellationToken).ConfigureAwait(false);
        // The start page links each server as http://localhost:port/.
        var ports = listeners.Select(static item => item.Port).Distinct().Order()
            .Select(port => (Port: port, Pid: OwnerOf(listeners, "localhost", port, Environment.ProcessId))).Where(static item => item.Pid is not null).ToList();
        if (ports.Count == 0) return [];
        var owners = (await DescribeAsync([.. ports.Select(static item => item.Pid!.Value).Distinct()], cancellationToken).ConfigureAwait(false)).ToDictionary(static owner => owner.ProcessId);
        return [.. ports.Select(item => new ListeningPort(item.Port, owners[item.Pid!.Value]))];
    }

    // The listening sockets of the system, of one port or of all.
    private static async Task<IReadOnlyList<PortListener>> ListenersAsync(int? port, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var table = await RunAsync("netstat", ["-ano"], cancellationToken).ConfigureAwait(false);
            return table is null ? [] : [.. ParseNetstat(table).Where(item => port is null || item.Port == port)];
        }

        string[] arguments = port is { } one ? ["-nP", $"-iTCP:{one.ToString(CultureInfo.InvariantCulture)}", "-sTCP:LISTEN", "-Fpn"] : ["-nP", "-iTCP", "-sTCP:LISTEN", "-Fpn"];
        var listening = await RunAsync("lsof", arguments, cancellationToken).ConfigureAwait(false);
        return listening is null ? [] : ParseLsof(listening);
    }

    /// <summary>
    /// The process that answers a page at a host and a port: the only one of the sockets bound where the page connects.
    /// Several processes there (one on all the addresses and one on the loopback, or two families) make the answer
    /// depend on the system and on the address the browser tries first: none is named, and neither is CodeAlta itself,
    /// which is never the server of a page of a project.
    /// </summary>
    internal static int? OwnerOf(IEnumerable<PortListener> listeners, string host, int port, int ownProcessId)
    {
        int? owner = null;
        foreach (var listener in listeners)
        {
            if (listener.Port != port || !Reaches(listener.Address, host) || listener.ProcessId == owner) continue;
            if (owner is not null || listener.ProcessId == ownProcessId) return null;
            owner = listener.ProcessId;
        }

        return owner;
    }

    /// <summary>
    /// Whether a page at a host connects to a socket bound to an address: a socket of all the addresses, of either
    /// family since a socket of IPv6 can take the connections of IPv4, or of the very address of the page, where
    /// <c>localhost</c> is <c>127.0.0.1</c> or <c>::1</c>.
    /// </summary>
    internal static bool Reaches(string bound, string host)
    {
        var listener = bound.Trim('[', ']');
        if (listener is "*" or "0.0.0.0" or "::") return true;
        if (!IPAddress.TryParse(listener, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var page = host.Trim('[', ']');
        if (string.Equals(page, "localhost", StringComparison.OrdinalIgnoreCase)) return address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback);
        if (!IPAddress.TryParse(page, out var target)) return false;
        if (target.IsIPv4MappedToIPv6) target = target.MapToIPv4();
        return address.Equals(target);
    }

    /// <summary>The listening sockets of the output of <c>lsof -nP -iTCP -sTCP:LISTEN -Fpn</c>.</summary>
    internal static IReadOnlyList<PortListener> ParseLsof(string output)
    {
        var listeners = new List<PortListener>();
        var pid = 0;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('p')) pid = int.TryParse(line.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
            else if (line.StartsWith('n') && pid > 0 && SplitAddress(line[1..]) is { } address && !listeners.Contains(new PortListener(pid, address.Host, address.Port)))
            {
                listeners.Add(new PortListener(pid, address.Host, address.Port));
            }
        }

        return listeners;
    }

    /// <summary>
    /// The listening sockets of TCP of the output of <c>netstat -ano</c> on Windows. The words of its state are those of
    /// the language of the system, so a socket that listens is told by the address of its other end, which is none:
    /// <c>0.0.0.0:0</c> or <c>[::]:0</c>.
    /// </summary>
    internal static IReadOnlyList<PortListener> ParseNetstat(string output)
    {
        var listeners = new List<PortListener>();
        foreach (var raw in output.Split('\n'))
        {
            var columns = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 4 || !string.Equals(columns[0], "TCP", StringComparison.OrdinalIgnoreCase) || columns[2] is not ("0.0.0.0:0" or "[::]:0")) continue;
            if (!int.TryParse(columns[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0 || SplitAddress(columns[1]) is not { } address) continue;
            var listener = new PortListener(pid, address.Host, address.Port);
            if (!listeners.Contains(listener)) listeners.Add(listener);
        }

        return listeners;
    }

    /// <summary>
    /// The absolute paths a command line names: its words, as Windows splits them at spaces outside double quotes, and
    /// the value of a word such as <c>--root=C:\app</c>. A path of a network share is left out: it is never touched.
    /// </summary>
    internal static IReadOnlyList<string> CommandLinePaths(string command) => CommandLinePaths(command, OperatingSystem.IsWindows());

    /// <summary>The absolute paths a command line names, as on Windows (<c>C:\app</c>) or as on the other systems (<c>/app</c>).</summary>
    internal static IReadOnlyList<string> CommandLinePaths(string command, bool windows)
    {
        var paths = new List<string>();
        foreach (var word in CommandLineWords(command))
        {
            foreach (var path in word.IndexOf('=') is var equals and > 0 ? [word, word[(equals + 1)..]] : new[] { word })
            {
                var absolute = windows
                    ? path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'
                    : path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal);
                if (absolute) paths.Add(path);
            }
        }

        return paths;
    }

    // The words of a command line, split at spaces outside double quotes, the quotes removed.
    internal static IEnumerable<string> CommandLineWords(string command)
    {
        var word = new System.Text.StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var character in command)
        {
            if (character == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (any) yield return word.ToString();
                word.Clear();
                any = false;
            }
            else
            {
                word.Append(character);
                any = true;
            }
        }

        if (any) yield return word.ToString();
    }

    /// <summary>The host and the port of an address such as <c>127.0.0.1:5173</c>, <c>[::1]:5173</c> or <c>*:5173</c>.</summary>
    internal static (string Host, int Port)? SplitAddress(string address)
    {
        var colon = address.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(address.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is <= 0 or > 65535) return null;
        return (address[..colon], port);
    }

    // The command line and the folder of processes, in the order of their ids.
    private static async Task<IReadOnlyList<PortOwner>> DescribeAsync(IReadOnlyList<int> pids, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var described = new List<PortOwner>(pids.Count);
            foreach (var id in pids) described.Add(new PortOwner(id, WindowsProcesses.CommandLine(id) ?? string.Empty, null));
            return described;
        }

        var ids = string.Join(',', pids.Select(static id => id.ToString(CultureInfo.InvariantCulture)));
        var folders = new Dictionary<int, string>();
        var cwd = await RunAsync("lsof", ["-a", "-p", ids, "-d", "cwd", "-Fpn"], cancellationToken).ConfigureAwait(false);
        var pid = 0;
        foreach (var raw in (cwd ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('p')) pid = int.TryParse(line.AsSpan(1), out var value) ? value : 0;
            else if (line.StartsWith('n') && pid > 0 && line.Length > 1) folders[pid] = line[1..];
        }

        var commands = new Dictionary<int, string>();
        var listed = await RunAsync("ps", ["-o", "pid=,command=", "-p", ids], cancellationToken).ConfigureAwait(false);
        foreach (var raw in (listed ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space > 0 && int.TryParse(line.AsSpan(0, space), out var id)) commands[id] = line[(space + 1)..].Trim();
        }

        return [.. pids.Select(id => new PortOwner(id, commands.GetValueOrDefault(id, string.Empty), folders.GetValueOrDefault(id)))];
    }

    private static async Task<string?> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(Timeout);
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var errors = process.StandardError.ReadToEndAsync(limit.Token);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* it ended */ }
                if (cancellationToken.IsCancellationRequested) throw;
                return null;
            }

            await errors.ConfigureAwait(false);
            return await output.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // The program is not there.
            return null;
        }
    }
}
