using CodeAlta.Agent.Claude;

namespace CodeAlta.Hosting;

/// <summary>Where the download of the pinned Claude Code CLI is.</summary>
/// <param name="State"><c>idle</c>, <c>downloading</c>, <c>installing</c>, <c>done</c>, <c>failed</c> or <c>canceled</c>.</param>
/// <param name="ReceivedBytes">The bytes of the archive on disk, of <paramref name="TotalBytes"/>.</param>
/// <param name="TotalBytes">The bytes of the archive.</param>
/// <param name="Error">For <c>failed</c>: <c>network</c>, <c>not_found</c>, <c>checksum</c>, <c>busy_elsewhere</c>, <c>disk</c> or <c>unsupported_platform</c>.</param>
public sealed record ConfiguredClaudeCodeCliState(string State, long ReceivedBytes, long TotalBytes, string? Error);

/// <summary>
/// The Claude Code CLI CodeAlta pins, for a frontend that does not name the provider packages: its version, whether
/// it is in the cache, its download, and the Claude Code the user installed. Nothing is downloaded before
/// <see cref="TryStart"/> is called.
/// </summary>
public sealed class ConfiguredClaudeCodeCli
{
    private readonly ClaudeCodeCliInstaller _installer;

    /// <summary>Creates the CLI of this computer, downloaded from the network.</summary>
    /// <param name="root">The folder of Claude Code in the cache of CodeAlta (<c>CatalogOptions.ClaudeCodeRoot</c>).</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null or empty.</exception>
    public ConfiguredClaudeCodeCli(string root)
        : this(root, null, ClaudeCodePinnedCli.CurrentRuntimeIdentifier())
    {
    }

    /// <summary>Creates the CLI of a platform.</summary>
    /// <param name="root">The folder of Claude Code in the cache of CodeAlta.</param>
    /// <param name="handler">The handler of the requests of the download, or null for the network.</param>
    /// <param name="runtimeIdentifier">The platform, such as <c>win-x64</c>, or null when Claude Code has none for it.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null or empty.</exception>
    public ConfiguredClaudeCodeCli(string root, HttpMessageHandler? handler, string? runtimeIdentifier)
        => _installer = new ClaudeCodeCliInstaller(root, handler, runtimeIdentifier);

    /// <summary>Gets the version CodeAlta pins.</summary>
    public static string PinnedVersion => ClaudeCodePinnedCli.Version;

    /// <summary>Gets whether Claude Code has a CLI for the platform.</summary>
    public bool IsSupported => _installer.IsSupported;

    /// <summary>Gets the size of the download, in bytes; zero when the platform has none.</summary>
    public long Size => _installer.Asset?.Size ?? 0;

    /// <summary>Gets the pinned executable once it is in the cache, else null.</summary>
    public string? InstalledPath => _installer.InstalledPath;

    /// <summary>Gets the latest state of the download.</summary>
    public ConfiguredClaudeCodeCliState Current => Map(_installer.Current);

    /// <summary>Follows the download. The observer gets the current state at once, then every change.</summary>
    /// <param name="observer">Called with each state; it must not block.</param>
    /// <returns>Ends the subscription when disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="observer"/> is null.</exception>
    public IDisposable Subscribe(Action<ConfiguredClaudeCodeCliState> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return _installer.Subscribe(snapshot => observer(Map(snapshot)));
    }

    /// <summary>Starts the download. False while one runs.</summary>
    /// <returns>Whether a download started.</returns>
    public bool TryStart() => _installer.TryStart();

    /// <summary>Stops the running download; the next one goes on from what it wrote.</summary>
    public void Cancel() => _installer.Cancel();

    /// <summary>Stops the download and waits for it to end.</summary>
    /// <returns>The end of the download.</returns>
    public Task CloseAsync() => _installer.CloseAsync();

    /// <summary>Finds the Claude Code the user installed, on <c>PATH</c> or in the folders of its installers.</summary>
    /// <returns>Its full path, or null when none that CodeAlta runs was found.</returns>
    public static string? FindUserInstall() => ClaudeCodeInstalledCli.Find();

    /// <summary>Asks an executable for its version (<c>claude --version</c>).</summary>
    /// <param name="executable">The full path of the executable.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>The version, or null when it did not say in ten seconds.</returns>
    /// <exception cref="ArgumentException"><paramref name="executable"/> is null or blank.</exception>
    /// <exception cref="OperationCanceledException">The reading was canceled.</exception>
    public static Task<string?> ReadVersionAsync(string executable, CancellationToken cancellationToken)
        => ClaudeCodeCliVersion.ReadAsync(executable, cancellationToken);

    private static ConfiguredClaudeCodeCliState Map(ClaudeCodeCliInstallSnapshot snapshot)
        => new(snapshot.State, snapshot.ReceivedBytes, snapshot.TotalBytes, snapshot.Error);
}
