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
}
