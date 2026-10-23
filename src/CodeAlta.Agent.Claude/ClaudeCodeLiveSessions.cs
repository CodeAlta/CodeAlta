using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// Whether another Claude process holds a conversation of the CLI.
/// </summary>
internal enum ClaudeCodeSessionHoldStatus
{
    /// <summary>No other live process holds it.</summary>
    None,

    /// <summary>Another live process holds it (<see cref="ClaudeCodeSessionHold.Holder"/>).</summary>
    Held,

    /// <summary>
    /// The registry could not be read or has a shape this side does not understand: the caller resumes the conversation
    /// and logs <see cref="ClaudeCodeSessionHold.Reason"/>.
    /// </summary>
    Unknown,
}

/// <summary>
/// Where the process that holds a conversation runs, as the user knows it.
/// </summary>
internal enum ClaudeCodeHolderOrigin
{
    /// <summary>The Claude Code extension of VS Code (<c>claude-vscode</c>).</summary>
    VsCode,

    /// <summary>The CLI in a terminal (<c>cli</c>).</summary>
    Terminal,

    /// <summary>Another application that runs the CLI (an SDK, the desktop app, ...).</summary>
    OtherApplication,

    /// <summary>A live process whose entry could not be read.</summary>
    Unknown,
}

/// <summary>
/// The process that holds a conversation, as its entry of the registry describes it.
/// </summary>
/// <param name="Pid">The identifier of the process.</param>
/// <param name="Entrypoint">How it was started (<c>cli</c>, <c>claude-vscode</c>, <c>sdk-cli</c>, ...); null when unknown.</param>
/// <param name="Kind">Its kind (<c>interactive</c>, <c>bg</c>, <c>daemon</c>, ...); null when unknown.</param>
/// <param name="Status">What it does (<c>busy</c>, <c>idle</c>, <c>waiting</c>, ...); null when unknown.</param>
/// <param name="WorkingDirectory">Its working directory; null when unknown.</param>
/// <param name="Name">The name of its session; null when unknown.</param>
internal sealed record ClaudeCodeSessionHolder(int Pid, string? Entrypoint, string? Kind, string? Status, string? WorkingDirectory, string? Name)
{
    /// <summary>Gets a value indicating whether the entry was read (false: only its live process is known).</summary>
    public bool IsDescribed { get; init; } = true;

    /// <summary>Gets where the process runs.</summary>
    public ClaudeCodeHolderOrigin Origin => !IsDescribed
        ? ClaudeCodeHolderOrigin.Unknown
        : Entrypoint switch
        {
            "claude-vscode" => ClaudeCodeHolderOrigin.VsCode,
            "cli" => ClaudeCodeHolderOrigin.Terminal,
            _ => ClaudeCodeHolderOrigin.OtherApplication,
        };

    /// <summary>Gets where the process runs, in words: <c>VS Code</c>, <c>a terminal</c>, <c>another application</c>.</summary>
    public string OriginText => Origin switch
    {
        ClaudeCodeHolderOrigin.VsCode => "VS Code",
        ClaudeCodeHolderOrigin.Terminal => "a terminal",
        ClaudeCodeHolderOrigin.OtherApplication => Entrypoint is { Length: > 0 } entrypoint ? $"another application ({entrypoint})" : "another application",
        _ => "another Claude process",
    };
}

/// <summary>
/// The answer to "who holds this conversation".
/// </summary>
/// <param name="Status">Whether it is held.</param>
/// <param name="Holder">The process that holds it, when <paramref name="Status"/> is <see cref="ClaudeCodeSessionHoldStatus.Held"/>.</param>
/// <param name="Reason">Why nothing could be known, when <paramref name="Status"/> is <see cref="ClaudeCodeSessionHoldStatus.Unknown"/>.</param>
internal sealed record ClaudeCodeSessionHold(ClaudeCodeSessionHoldStatus Status, ClaudeCodeSessionHolder? Holder = null, string? Reason = null)
{
    /// <summary>Gets the answer for a conversation no other process holds.</summary>
    public static ClaudeCodeSessionHold None { get; } = new(ClaudeCodeSessionHoldStatus.None);
}

/// <summary>
/// When a process started: what the CLI compares with the token of its entry to tell a live process from another one
/// that was given the same identifier.
/// </summary>
/// <param name="StartTime">The start time, as <see cref="Process.StartTime"/> gives it (local time), on Windows.</param>
/// <param name="Token">The token as the CLI writes it on this system, on Linux and macOS.</param>
internal sealed record ClaudeCodeProcessStart(DateTime? StartTime, string? Token)
{
    // FILETIME counts from 1601 and is about 1.3e17 today; .NET ticks count from year 1 and are above 6e17.
    private const long TicksThreshold = 600_000_000_000_000_000;

    /// <summary>
    /// Tells whether the token of an entry is this start: the FILETIME of the creation on Windows (.NET ticks when the
    /// value is that large), the text the CLI reads on the other systems.
    /// </summary>
    public bool Matches(string recorded)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        if (Token is not null)
        {
            return string.Equals(Token.Trim(), recorded.Trim(), StringComparison.Ordinal);
        }

        if (StartTime is not { } startTime || !long.TryParse(recorded.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        try
        {
            return value >= TicksThreshold ? value == startTime.Ticks : value == startTime.ToFileTimeUtc();
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>Tells whether two reads of a start are the same start.</summary>
    public bool IsSameStart(ClaudeCodeProcessStart other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Token is not null || other.Token is not null
            ? string.Equals(Token, other.Token, StringComparison.Ordinal)
            : StartTime == other.StartTime;
    }
}

/// <summary>
/// Looks at the processes of this system, as the registry of live sessions needs it. Tests replace it.
/// </summary>
internal interface IClaudeCodeProcessProbe
{
    /// <summary>
    /// Gets the domain of the identifiers of processes of this system, as the CLI writes it in <c>pidDomain</c>
    /// (<c>win32:&lt;host name&gt;</c> on Windows); null when it cannot be known.
    /// </summary>
    string? PidDomain { get; }

    /// <summary>Tells whether a process with this identifier runs.</summary>
    bool IsRunning(int pid);

    /// <summary>Reads when a running process started; null when it cannot be read.</summary>
    ClaudeCodeProcessStart? GetStart(int pid);
}

/// <summary>
/// Looks at the processes of this system through <see cref="Process"/> and <c>/proc</c>.
/// </summary>
internal sealed class ClaudeCodeProcessProbe : IClaudeCodeProcessProbe
{
    private readonly Lazy<string?> _pidDomain = new(ReadPidDomain);

    /// <summary>Gets the probe of this system.</summary>
    public static ClaudeCodeProcessProbe Instance { get; } = new();

    /// <inheritdoc />
    public string? PidDomain => _pidDomain.Value;

    /// <inheritdoc />
    public bool IsRunning(int pid)
    {
        if (pid <= 1)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
        {
            // The process exists, but this side may not look at it (another user, an elevated one).
            return true;
        }
    }

    /// <inheritdoc />
    public ClaudeCodeProcessStart? GetStart(int pid)
    {
        if (pid <= 1)
        {
            return null;
        }

        try
        {
            if (OperatingSystem.IsLinux())
            {
                return ReadLinuxStart(pid) is { } token ? new ClaudeCodeProcessStart(null, token) : null;
            }

            using var process = Process.GetProcessById(pid);
            var startTime = process.StartTime;
            if (OperatingSystem.IsWindows())
            {
                return new ClaudeCodeProcessStart(startTime, null);
            }

            // macOS: what `ps -o lstart=` writes with LC_ALL=C and TZ=UTC (the "%c" of C), read without starting ps.
            if (OperatingSystem.IsMacOS())
            {
                var utc = startTime.ToUniversalTime();
                var token = string.Create(CultureInfo.InvariantCulture,
                    $"{utc:ddd} {utc:MMM} {utc.Day,2} {utc:HH:mm:ss} {utc:yyyy}");
                return new ClaudeCodeProcessStart(null, token);
            }

            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Field 22 of /proc/<pid>/stat (the start time in clock ticks since boot), as the CLI reads it: the name of the
    // command is between parentheses and may hold spaces, so the fields are counted after the last ')'.
    internal static string? ParseLinuxStat(string stat)
    {
        ArgumentNullException.ThrowIfNull(stat);
        var end = stat.LastIndexOf(')');
        if (end < 0 || end + 2 > stat.Length)
        {
            return null;
        }

        var fields = stat[(end + 2)..].Split(' ');
        return fields.Length > 19 && fields[19].Length > 0 ? fields[19] : null;
    }

    private static string? ReadLinuxStart(int pid)
    {
        var path = $"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat";
        return File.Exists(path) ? ParseLinuxStat(File.ReadAllText(path)) : null;
    }

    // As the CLI and its VS Code extension (2.1.296) build it: the platform of Node and, on Windows, the host name in
    // lower case; on Linux the machine id and the pid namespace; elsewhere the platform alone.
    private static string? ReadPidDomain()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return "win32:" + System.Net.Dns.GetHostName().ToLowerInvariant();
            }

            if (OperatingSystem.IsLinux())
            {
                var machineId = File.Exists("/etc/machine-id") ? File.ReadAllText("/etc/machine-id").Trim() : string.Empty;
                var pidNamespace = new FileInfo("/proc/self/ns/pid").LinkTarget ?? string.Empty;
                return $"linux:{machineId}:{pidNamespace}";
            }

            return OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsFreeBSD() ? "freebsd" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            return null;
        }
    }
}

/// <summary>
/// The CLI processes CodeAlta runs: an entry of the registry of one of them is CodeAlta's own, not another process
/// that holds the conversation. The options of a provider keep one, which all its runtimes share.
/// </summary>
internal sealed class ClaudeCodeOwnProcesses
{
    private readonly IClaudeCodeProcessProbe _probe;
    private readonly object _gate = new();
    private readonly Dictionary<int, Registration> _processes = [];

    public ClaudeCodeOwnProcesses(IClaudeCodeProcessProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <summary>
    /// Records a process CodeAlta started, until the returned registration is disposed (when its connection closes).
    /// </summary>
    /// <param name="pid">The identifier of the process; null records nothing.</param>
    /// <returns>The registration; null when nothing was recorded.</returns>
    public IDisposable? Track(int? pid)
    {
        if (pid is not { } id || id <= 0)
        {
            return null;
        }

        var registration = new Registration(this, id, _probe.GetStart(id));
        lock (_gate)
        {
            _processes[id] = registration;
        }

        return registration;
    }

    /// <summary>Tells whether a process is one CodeAlta started: its identifier is recorded, with the same start.</summary>
    /// <param name="pid">The identifier of the process.</param>
    /// <param name="start">When the process that has it now started, when it could be read.</param>
    public bool Contains(int pid, ClaudeCodeProcessStart? start)
    {
        Registration? registration;
        lock (_gate)
        {
            _processes.TryGetValue(pid, out registration);
        }

        return registration is not null && (registration.Start is null || start is null || registration.Start.IsSameStart(start));
    }

    /// <summary>Gets the identifiers of the processes recorded.</summary>
    public IReadOnlyList<int> Snapshot()
    {
        lock (_gate)
        {
            return [.. _processes.Keys];
        }
    }

    private void Remove(Registration registration)
    {
        lock (_gate)
        {
            // Another process may have been recorded with the same identifier since.
            if (_processes.TryGetValue(registration.Pid, out var current) && ReferenceEquals(current, registration))
            {
                _processes.Remove(registration.Pid);
            }
        }
    }

    private sealed class Registration(ClaudeCodeOwnProcesses owner, int pid, ClaudeCodeProcessStart? start) : IDisposable
    {
        private int _disposed;

        public int Pid { get; } = pid;

        public ClaudeCodeProcessStart? Start { get; } = start;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Remove(this);
            }
        }
    }
}

/// <summary>
/// Reads the registry of live sessions the CLI keeps (<c>&lt;config&gt;/sessions/&lt;pid&gt;.json</c>, one file per
/// running process, written by every CLI and removed when it exits) to tell whether another process holds a
/// conversation.
/// </summary>
/// <remarks>
/// The format is not documented: the rules are those of the CLI and its VS Code extension (2.1.296). An entry holds the
/// conversation when its <c>sessionId</c> is it, its <c>pidDomain</c> is absent or the one of this system, its process
/// runs, is not one CodeAlta started, and started when the token of the entry (<c>procStartFt ?? procStart</c>) says;
/// an entry without a token needs only its process to run. A killed process leaves its entry behind, and its
/// identifier may be given to another process later: the token tells them apart. An entry that cannot be read counts
/// as held when its process runs and the file changed less than 30 seconds ago (a process that is writing it). This
/// side never writes nor deletes an entry: removing stale ones is the CLI's job.
/// </remarks>
internal sealed class ClaudeCodeLiveSessions
{
    /// <summary>The largest entry that is read, as the VS Code extension reads it.</summary>
    public const int MaxEntryBytes = 256 * 1024;

    private static readonly TimeSpan UnreadableEntryFreshness = TimeSpan.FromSeconds(30);

    private readonly string _configRoot;
    private readonly ClaudeCodeOwnProcesses _ownProcesses;
    private readonly IClaudeCodeProcessProbe _probe;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a reader of the registry of a configuration folder of Claude Code.</summary>
    /// <param name="configRoot">The configuration folder (see <see cref="ClaudeCodeProjectFolders.ResolveConfigRoot()"/>).</param>
    /// <param name="ownProcesses">The processes CodeAlta started.</param>
    /// <param name="probe">Looks at the processes of the system.</param>
    /// <param name="timeProvider">Gives the time the age of an entry is measured with.</param>
    /// <exception cref="ArgumentException"><paramref name="configRoot"/> is blank.</exception>
    /// <exception cref="ArgumentNullException">Another argument is null.</exception>
    public ClaudeCodeLiveSessions(string configRoot, ClaudeCodeOwnProcesses ownProcesses, IClaudeCodeProcessProbe probe, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configRoot);
        _configRoot = configRoot;
        _ownProcesses = ownProcesses ?? throw new ArgumentNullException(nameof(ownProcesses));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Gets the folder of the registry.</summary>
    public string RegistryFolder => Path.Combine(_configRoot, "sessions");

    /// <summary>
    /// Tells whether another live process holds a conversation of the CLI. A process whose entry was read wins over
    /// one whose entry could not be read, which wins over an entry this side does not understand.
    /// </summary>
    /// <param name="cliSessionId">The identifier of the conversation.</param>
    /// <returns>Who holds it: none, a process, or unknown.</returns>
    /// <exception cref="ArgumentException"><paramref name="cliSessionId"/> is blank.</exception>
    public ClaudeCodeSessionHold FindHolder(string cliSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cliSessionId);
        var folder = RegistryFolder;
        List<string> files;
        try
        {
            files = [.. Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly)];
        }
        catch (DirectoryNotFoundException)
        {
            // No CLI ever ran with this profile, or the path is not a folder at all.
            return File.Exists(folder)
                ? new ClaudeCodeSessionHold(ClaudeCodeSessionHoldStatus.Unknown, Reason: $"The registry of live sessions of Claude Code '{folder}' is not a folder.")
                : ClaudeCodeSessionHold.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new ClaudeCodeSessionHold(ClaudeCodeSessionHoldStatus.Unknown, Reason: $"The registry of live sessions of Claude Code '{folder}' could not be listed: {ex.Message}");
        }

        ClaudeCodeSessionHolder? best = null;
        long bestUpdatedAt = long.MinValue;
        ClaudeCodeSessionHolder? unreadable = null;
        string? unknown = null;
        foreach (var file in files)
        {
            if (ParsePid(Path.GetFileNameWithoutExtension(file)) is not { } pid)
            {
                continue;
            }

            switch (ReadEntry(file, pid, cliSessionId, out var holder, out var updatedAt, out var reason))
            {
                case EntryResult.Holds when best is null || updatedAt > bestUpdatedAt:
                    best = holder;
                    bestUpdatedAt = updatedAt;
                    break;
                case EntryResult.Unreadable:
                    unreadable ??= holder;
                    break;
                case EntryResult.Unknown:
                    unknown ??= reason;
                    break;
            }
        }

        if ((best ?? unreadable) is { } found)
        {
            return new ClaudeCodeSessionHold(ClaudeCodeSessionHoldStatus.Held, found);
        }

        return unknown is not null
            ? new ClaudeCodeSessionHold(ClaudeCodeSessionHoldStatus.Unknown, Reason: unknown)
            : ClaudeCodeSessionHold.None;
    }

    // Only <digits>.json is an entry, written with the identifier as the CLI prints it (no leading zero); the folder
    // holds other files (<pid>.<hash>.key).
    internal static int? ParsePid(string name)
    {
        if (name.Length is 0 or > 10 || name[0] == '0' || !name.All(char.IsAsciiDigit) ||
            !int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 1)
        {
            return null;
        }

        return pid;
    }

    private EntryResult ReadEntry(string file, int pid, string cliSessionId, out ClaudeCodeSessionHolder? holder, out long updatedAt, out string? reason)
    {
        holder = null;
        updatedAt = 0;
        reason = null;
        JsonElement entry;
        try
        {
            entry = ReadJson(file);
        }
        catch (FileNotFoundException)
        {
            // The process exited meanwhile.
            return EntryResult.Skip;
        }
        catch (DirectoryNotFoundException)
        {
            return EntryResult.Skip;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return IsFreshUnreadable(file, pid, out holder) ? EntryResult.Unreadable : EntryResult.Skip;
        }

        if (entry.ValueKind != JsonValueKind.Object)
        {
            return _probe.IsRunning(pid) ? Unknown($"the entry of process {pid} is not a JSON object", out reason) : EntryResult.Skip;
        }

        if (!entry.TryGetProperty("sessionId", out var sessionValue) || sessionValue.ValueKind is JsonValueKind.Null)
        {
            // A process that has no conversation yet.
            return EntryResult.Skip;
        }

        if (sessionValue.ValueKind != JsonValueKind.String)
        {
            return _probe.IsRunning(pid) ? Unknown($"the entry of process {pid} has a sessionId that is not a string", out reason) : EntryResult.Skip;
        }

        if (!string.Equals(sessionValue.GetString(), cliSessionId, StringComparison.OrdinalIgnoreCase))
        {
            return EntryResult.Skip;
        }

        if (!TryReadToken(entry, out var token) || !TryReadOptionalString(entry, "pidDomain", out var pidDomain))
        {
            return _probe.IsRunning(pid) ? Unknown($"the entry of process {pid} has a procStart or pidDomain this side does not understand", out reason) : EntryResult.Skip;
        }

        if (pidDomain is not null)
        {
            var ownDomain = _probe.PidDomain;
            if (ownDomain is null)
            {
                // Whether the identifier is one of this system cannot be told.
                return Unknown($"the domain of the process identifiers of this system is not known, and process {pid} has '{pidDomain}'", out reason);
            }

            if (!string.Equals(pidDomain, ownDomain, StringComparison.Ordinal))
            {
                // Another machine or another pid namespace that shares the profile.
                return EntryResult.Skip;
            }
        }

        if (!_probe.IsRunning(pid))
        {
            // A process that was killed leaves its entry behind.
            return EntryResult.Skip;
        }

        var start = _probe.GetStart(pid);
        if (_ownProcesses.Contains(pid, start))
        {
            return EntryResult.Skip;
        }

        // Another process was given the identifier of the one that wrote the entry.
        if (token is not null && start is not null && !start.Matches(token))
        {
            return EntryResult.Skip;
        }

        holder = new ClaudeCodeSessionHolder(
            pid,
            ClaudeCodeJson.GetString(entry, "entrypoint"),
            ClaudeCodeJson.GetString(entry, "kind"),
            ClaudeCodeJson.GetString(entry, "status"),
            ClaudeCodeJson.GetString(entry, "cwd"),
            ClaudeCodeJson.GetString(entry, "name"));
        updatedAt = ClaudeCodeJson.GetInt64(entry, "updatedAt") ?? ClaudeCodeJson.GetInt64(entry, "startedAt") ?? 0;
        return EntryResult.Holds;
    }

    private static EntryResult Unknown(string text, out string reason)
    {
        reason = $"The registry of live sessions of Claude Code could not be understood: {text}.";
        return EntryResult.Unknown;
    }

    // An entry that cannot be read may be one a process is writing: it counts while its process runs and for a while
    // after the file changed. Which conversation it holds is not known.
    private bool IsFreshUnreadable(string file, int pid, out ClaudeCodeSessionHolder? holder)
    {
        holder = null;
        if (!_probe.IsRunning(pid) || _ownProcesses.Contains(pid, null))
        {
            return false;
        }

        DateTime changed;
        try
        {
            changed = File.GetLastWriteTimeUtc(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var age = _timeProvider.GetUtcNow().UtcDateTime - changed;
        if (age.Duration() >= UnreadableEntryFreshness)
        {
            return false;
        }

        holder = new ClaudeCodeSessionHolder(pid, null, null, null, null, null) { IsDescribed = false };
        return true;
    }

    private static JsonElement ReadJson(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        if (stream.Length > MaxEntryBytes)
        {
            throw new IOException($"The entry '{file}' is larger than {MaxEntryBytes} bytes.");
        }

        var buffer = new byte[(int)stream.Length];
        stream.ReadExactly(buffer);
        // The CLI rewrites an entry in place: a shorter entry leaves NUL bytes after it, which are not part of it.
        var end = buffer.AsSpan().TrimEnd((byte)0).Length;
        using var document = JsonDocument.Parse(buffer.AsMemory(0, end));
        return document.RootElement.Clone();
    }

    // The token is a string (the CLI writes one); a number is taken as its text. Another kind is not understood.
    private static bool TryReadToken(JsonElement entry, out string? token)
    {
        token = null;
        foreach (var name in (ReadOnlySpan<string>)["procStartFt", "procStart"])
        {
            if (!entry.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.String when ClaudeCodeJson.ReadString(value) is { Length: > 0 } text:
                    token = text;
                    return true;
                case JsonValueKind.String:
                    continue;
                case JsonValueKind.Number:
                    token = value.GetRawText();
                    return true;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool TryReadOptionalString(JsonElement entry, string name, out string? text)
    {
        text = null;
        if (!entry.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        text = ClaudeCodeJson.ReadString(value);
        return true;
    }

    private enum EntryResult
    {
        Skip,
        Holds,
        Unreadable,
        Unknown,
    }
}
