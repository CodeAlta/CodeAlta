namespace CodeAlta.Server.Hosting;

/// <summary>The files of a server in its data root.</summary>
public sealed class ServerPaths
{
    /// <summary>The default port of the server.</summary>
    public const int DefaultPort = 2586;

    /// <summary>Initializes a new instance of the <see cref="ServerPaths"/> class.</summary>
    /// <param name="dataRoot">The data root, an absolute path.</param>
    /// <exception cref="ArgumentException"><paramref name="dataRoot"/> is empty or not absolute.</exception>
    public ServerPaths(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        if (!Path.IsPathFullyQualified(dataRoot)) throw new ArgumentException($"The data root must be an absolute path: '{dataRoot}'.", nameof(dataRoot));
        DataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
    }

    /// <summary>Gets the default data root: <c>~/.altaserver</c>, apart from the profile of CodeAlta (<c>~/.alta</c>).</summary>
    /// <returns>The path.</returns>
    public static string GetDefaultDataRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".altaserver");

    /// <summary>Gets the data root.</summary>
    public string DataRoot { get; }

    /// <summary>Gets the SQLite database.</summary>
    public string DatabasePath => Path.Combine(DataRoot, "altaserver.sqlite3");

    /// <summary>Gets the file of the one-time setup code, while the server has no owner.</summary>
    public string SetupCodePath => Path.Combine(DataRoot, "setup-code.json");

    /// <summary>Gets the file a running server writes: its process and addresses.</summary>
    public string StatePath => Path.Combine(DataRoot, "altaserver.state.json");

    /// <summary>Gets the lock a running server holds, so that one server at a time uses the data root.</summary>
    public string LockPath => Path.Combine(DataRoot, "altaserver.lock");

    /// <summary>
    /// Gets the file that asks the running server to stop, as Ctrl+C would: <c>altaserver uninstall</c> writes it on
    /// Windows, where removing the task at logon leaves the server running and no signal reaches it.
    /// </summary>
    public string StopRequestPath => Path.Combine(DataRoot, "altaserver.stop");

    /// <summary>Gets the folder of the versions of the server (<c>altaserver update</c>): each beside the others, and the pointer.</summary>
    public string VersionsRoot => Path.Combine(DataRoot, "versions");

    /// <summary>Gets the settings of the updates of the server: the feed and the allowed feeds.</summary>
    public string UpdatePolicyPath => Path.Combine(DataRoot, "update.json");

    /// <summary>Gets the folder of the backups of the database taken before each update.</summary>
    public string BackupsRoot => Path.Combine(DataRoot, "backups");

    /// <summary>
    /// Gets the owner's telemetry setting of this server (<c>telemetry.json</c>), which wins over the fleet's: it can turn
    /// telemetry off, refuse the fleet's setting, or name a collector of its own.
    /// </summary>
    public string TelemetryPath => Path.Combine(DataRoot, CodeAlta.Fleet.Protocol.FleetLocalTelemetrySettings.FileName);

    /// <summary>
    /// Gets the folder of the key ring that seals the secrets the server uses itself (the company's client secret, the
    /// collector's headers); readable by the account of the server only, protected by DPAPI on Windows. A backup of the
    /// database keeps it beside: without it, those values are set again.
    /// </summary>
    public string KeysRoot => Path.Combine(DataRoot, "keys");

    /// <summary>
    /// Gets the certificate the server made for itself (<c>--own-certificate</c>), sealed with the key ring of
    /// <see cref="KeysRoot"/>: its clients pin its key, so it is kept, never replaced by itself.
    /// </summary>
    public string OwnCertificatePath => Path.Combine(DataRoot, "tls", "own-certificate.sealed");

    /// <summary>Gets the file of the id of this server in its telemetry (<c>service.instance.id</c>), made at its first start.</summary>
    public string InstanceIdPath => Path.Combine(DataRoot, "instance-id");
}
