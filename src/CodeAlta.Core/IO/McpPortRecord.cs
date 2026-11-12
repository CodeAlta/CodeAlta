using System.Diagnostics;
using System.Globalization;

namespace CodeAlta.Core.IO;

/// <summary>
/// The loopback ports the MCP servers of the CodeAlta Desktop instances of this user listen on, whatever their profile:
/// each server records its own in a folder they share, as an empty file named <c>&lt;process id&gt;-&lt;port&gt;</c>, and
/// removes it when it stops. The browser and the sandboxes of every instance, and of <c>altacli</c>, keep away from all of
/// them: an instance on another profile listens on a free port when 2582 is taken, a port no other instance could guess.
/// </summary>
/// <remarks>
/// A file left by a process that ended is not counted, and is removed. The ports are read again at most once a second: a
/// guard asks for them at each request of a page.
/// </remarks>
public sealed class McpPortRecord
{
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(1);

    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly Func<int, bool> _isRunning;
    private readonly Lock _gate = new();
    private IReadOnlyList<int> _ports = [];
    private long _readAt;
    private bool _read;
    private string? _own;

    /// <summary>Creates the record of a folder.</summary>
    /// <param name="folder">The folder the instances share (<see cref="DefaultFolder"/>).</param>
    /// <exception cref="ArgumentException"><paramref name="folder"/> is blank.</exception>
    public McpPortRecord(string folder)
        : this(folder, TimeProvider.System, IsProcessRunning)
    {
    }

    // time and isRunning: the clock of the cache and whether a process runs, which tests give.
    internal McpPortRecord(string folder, TimeProvider time, Func<int, bool> isRunning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = Path.GetFullPath(folder);
        _time = time;
        _isRunning = isRunning;
    }

    /// <summary>Gets the folder of the record.</summary>
    public string Folder => _folder;

    /// <summary>
    /// Gets the folder the instances of this user share: <c>CodeAlta/mcp-ports</c> in the local application data, beside
    /// the data of their windows.
    /// </summary>
    /// <returns>The folder.</returns>
    /// <exception cref="InvalidOperationException">The folder of the user is unknown.</exception>
    public static string DefaultFolder()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home)) throw new InvalidOperationException("Unable to determine the user profile directory for CodeAlta.");
            localData = Path.Combine(home, ".local", "share");
        }

        return Path.Combine(localData, "CodeAlta", "mcp-ports");
    }

    /// <summary>Records the port the MCP server of this process listens on, in place of the one it recorded before.</summary>
    /// <param name="port">The port.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is not a port.</exception>
    public void Publish(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        Withdraw();
        var path = Path.Combine(_folder, string.Create(CultureInfo.InvariantCulture, $"{Environment.ProcessId}-{port}"));
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllBytes(path, []);
            lock (_gate)
            {
                _own = path;
                _read = false;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The other instances do not know the port: their guards keep 2582 and 2583, and the server refuses pages.
        }
    }

    /// <summary>Removes the port this process recorded, if any.</summary>
    public void Withdraw()
    {
        string? own;
        lock (_gate)
        {
            own = _own;
            _own = null;
            _read = false;
        }

        if (own is null) return;
        try { File.Delete(own); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Counted as long as this process runs. */ }
    }

    /// <summary>Gets the ports the running instances recorded, this one's included.</summary>
    /// <returns>The ports.</returns>
    public IReadOnlyList<int> Current()
    {
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            if (_read && _time.GetElapsedTime(_readAt, now) < Freshness) return _ports;
        }

        var ports = Read();
        lock (_gate)
        {
            (_ports, _readAt, _read) = (ports, now, true);
        }

        return ports;
    }

    private IReadOnlyList<int> Read()
    {
        string[] files;
        try
        {
            if (!Directory.Exists(_folder)) return [];
            files = Directory.GetFiles(_folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var ports = new SortedSet<int>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var dash = name.IndexOf('-', StringComparison.Ordinal);
            if (dash <= 0
                || !int.TryParse(name.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var process)
                || !int.TryParse(name.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || port is < 1 or > 65535)
            {
                continue;
            }

            if (process == Environment.ProcessId || _isRunning(process))
            {
                ports.Add(port);
                continue;
            }

            // Left by an instance that ended without stopping its server.
            try { File.Delete(file); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Another reader removed it. */ }
        }

        return [.. ports];
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }
}
