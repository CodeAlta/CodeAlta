using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CodeAlta.Catalog;
using CodeAlta.Hosting;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The Claude Code CLI CodeAlta pins: whether it was downloaded, the Claude Code the user installed, and the download
/// of the pinned version into the shared cache. Nothing is downloaded before <c>install</c> is called.
/// </summary>
[NeoRpcService("claudeCli", Version = 1)]
internal sealed class ClaudeCliService
{
    private readonly ConfiguredClaudeCodeCli? _installer;
    private readonly Func<string?>? _findInstalled;
    private readonly Func<string, CancellationToken, Task<string?>>? _readVersion;
    private readonly string? _epoch;
    private readonly SemaphoreSlim _versionGate = new(1, 1);
    private (string Path, DateTime Written, string? Version)? _version;

    /// <summary>Creates the service of a window that downloads nothing.</summary>
    internal ClaudeCliService() { }

    /// <summary>Creates the service on the network and on the CLI of this computer.</summary>
    internal ClaudeCliService(CatalogOptions options, string epoch)
        : this(new ConfiguredClaudeCodeCli(options.ClaudeCodeRoot), epoch, ConfiguredClaudeCodeCli.FindUserInstall, ConfiguredClaudeCodeCli.ReadVersionAsync) { }

    /// <summary>Creates the service on given parts.</summary>
    /// <param name="installer">The installer of the pinned version.</param>
    /// <param name="epoch">The epoch of the host.</param>
    /// <param name="findInstalled">Finds the Claude Code the user installed.</param>
    /// <param name="readVersion">Reads the version of an executable.</param>
    internal ClaudeCliService(ConfiguredClaudeCodeCli installer, string epoch, Func<string?> findInstalled, Func<string, CancellationToken, Task<string?>> readVersion)
    {
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _installer = installer;
        _epoch = epoch;
        _findInstalled = findInstalled;
        _readVersion = readVersion;
    }

    /// <summary>Gets the installer, for tests.</summary>
    internal ConfiguredClaudeCodeCli? Installer => _installer;

    /// <summary>
    /// Gets or initializes what reads a provider of the configuration by its key: null when there is none. Without it,
    /// <c>check</c> knows no provider.
    /// </summary>
    internal Func<string, ClaudeCliProvider?>? FindProvider { get; init; }

    /// <summary>
    /// Says whether a provider runs Claude Code and, for one, which executable it would run, without starting it: the
    /// composer and the timeline propose the download where a provider has none.
    /// </summary>
    [NeoRpcMethod("check")]
    public async Task<ClaudeCliCheckResponse> CheckAsync(ClaudeCliCheckRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } status) return ClaudeCliCheckResponse.Refused(status, request.ProviderKey);
        if (string.IsNullOrWhiteSpace(request.ProviderKey) || request.ProviderKey.Length > 64) return ClaudeCliCheckResponse.Refused("invalid_request", null);
        var installer = _installer!;
        return await Task.Run(() =>
        {
            ClaudeCliProvider? provider;
            try { provider = FindProvider?.Invoke(request.ProviderKey.Trim()); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                return ClaudeCliCheckResponse.Refused("read_failed", request.ProviderKey);
            }

            if (provider is null) return ClaudeCliCheckResponse.Refused("unknown_provider", request.ProviderKey);
            var claude = provider.Type == "claude-code";
            return new ClaudeCliCheckResponse("ok", request.ProviderKey, claude, claude ? installer.ResolveExecutable(provider.Command) : null,
                claude ? provider.ConfigDir : null, installer.IsSupported, ConfiguredClaudeCodeCli.PinnedVersion, installer.Size,
                OperatingSystem.IsWindows(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the pinned version and whether it is in the cache, and the Claude Code the user installed with its version.
    /// </summary>
    [NeoRpcMethod("read")]
    public async Task<ClaudeCliStatusResponse> ReadAsync(ClaudeCliRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } status) return ClaudeCliStatusResponse.Refused(status);
        var installer = _installer!;
        var (pinned, installed) = await Task.Run(() => (installer.InstalledPath, _findInstalled!()), cancellationToken).ConfigureAwait(false);
        var installedVersion = installed is null ? null : await ReadVersionAsync(installed, cancellationToken).ConfigureAwait(false);
        return new("ok", installer.IsSupported, ConfiguredClaudeCodeCli.PinnedVersion, installer.Size, pinned, installed, installedVersion,
            OperatingSystem.IsWindows(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>Starts the download of the pinned version. It goes on when the page looks away.</summary>
    [NeoRpcMethod("install")]
    public ClaudeCliCommandResponse Install(ClaudeCliRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } status) return new(status);
        return new(!_installer!.IsSupported ? "unsupported" : _installer.TryStart() ? "ok" : "busy");
    }

    /// <summary>Stops the running download. What it wrote stays, and the next one goes on from it.</summary>
    [NeoRpcMethod("cancelInstall")]
    public ClaudeCliCommandResponse CancelInstall(ClaudeCliRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } status) return new(status);
        _installer!.Cancel();
        return new("ok");
    }

    /// <summary>Replays and follows the download. Closing the stream never stops it.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<ClaudeCliInstallState> Watch(ClaudeCliRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchAsync(request, cancellationToken), DesktopJsonContext.Default.ClaudeCliInstallState);
    }

    internal async IAsyncEnumerable<ClaudeCliInstallState> WatchAsync(ClaudeCliRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refuse(request.ExpectedEpoch) is not null) yield break;
        // Only the latest state matters: the bytes received so far, or how it ended.
        var snapshots = Channel.CreateBounded<ConfiguredClaudeCodeCliState>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        using var subscription = _installer!.Subscribe(snapshot => snapshots.Writer.TryWrite(snapshot));
        await foreach (var snapshot in snapshots.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return new(snapshot.State, snapshot.ReceivedBytes, snapshot.TotalBytes, snapshot.Error);
    }

    /// <summary>Stops the download and waits for it.</summary>
    internal Task CloseAsync() => _installer?.CloseAsync() ?? Task.CompletedTask;

    // The version of an executable is read once while the file stays the same.
    private async Task<string?> ReadVersionAsync(string path, CancellationToken cancellationToken)
    {
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
        await _versionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_version is { } known && known.Path == path && known.Written == written) return known.Version;
            var version = await _readVersion!(path, cancellationToken).ConfigureAwait(false);
            _version = (path, written, version);
            return version;
        }
        finally { _versionGate.Release(); }
    }

    private string? Refuse(string? epoch) => _installer is null ? "unavailable" : epoch == _epoch ? null : "stale_epoch";
}

internal sealed record ClaudeCliRequest(string? ExpectedEpoch);

/// <summary>What <c>check</c> reads of a provider of the configuration.</summary>
/// <param name="Type">Its adapter type.</param>
/// <param name="Command">Its <c>command</c>, or null.</param>
/// <param name="ConfigDir">Its <c>config_dir</c> as the file writes it, or null.</param>
internal sealed record ClaudeCliProvider(string? Type, string? Command, string? ConfigDir);

internal sealed record ClaudeCliCheckRequest(string? ExpectedEpoch, string? ProviderKey);

/// <param name="Status"><c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid_request</c>, <c>unknown_provider</c> or <c>read_failed</c>.</param>
/// <param name="ProviderKey">The provider asked about.</param>
/// <param name="Claude">Whether the provider runs Claude Code.</param>
/// <param name="Executable">The executable it would run, or null when it has none: the download is to propose.</param>
/// <param name="ConfigDir">Its account folder as the file writes it, or null.</param>
/// <param name="Supported">Whether Claude Code has a CLI to download for this platform.</param>
/// <param name="Version">The version CodeAlta pins.</param>
/// <param name="Size">The size of its download, in bytes.</param>
/// <param name="Windows">Whether the commands to copy are for PowerShell; else for a POSIX shell.</param>
/// <param name="Home">The home folder, which those commands write as <c>$HOME</c>.</param>
internal sealed record ClaudeCliCheckResponse(string Status, string? ProviderKey, bool Claude, string? Executable, string? ConfigDir, bool Supported, string Version,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long Size, bool Windows, string? Home)
{
    internal static ClaudeCliCheckResponse Refused(string status, string? providerKey)
        => new(status, providerKey, false, null, null, false, ConfiguredClaudeCodeCli.PinnedVersion, 0, OperatingSystem.IsWindows(), null);
}

/// <param name="Status"><c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c>, <c>unsupported</c> or <c>busy</c>.</param>
internal sealed record ClaudeCliCommandResponse(string Status);

/// <param name="Status"><c>ok</c>, <c>unavailable</c> or <c>stale_epoch</c>.</param>
/// <param name="Supported">Whether Claude Code has a CLI for this platform.</param>
/// <param name="Version">The version CodeAlta pins.</param>
/// <param name="Size">The size of its download, in bytes.</param>
/// <param name="PinnedPath">The pinned executable in the cache, or null while it is not downloaded.</param>
/// <param name="InstalledPath">The Claude Code the user installed, or null when none was found.</param>
/// <param name="InstalledVersion">Its version, or null when it did not say.</param>
/// <param name="Windows">Whether the commands to copy are for PowerShell; else for a POSIX shell.</param>
/// <param name="Home">The home folder, which those commands write as <c>$HOME</c>.</param>
internal sealed record ClaudeCliStatusResponse(string Status, bool Supported, string Version,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long Size,
    string? PinnedPath, string? InstalledPath, string? InstalledVersion, bool Windows, string? Home)
{
    internal static ClaudeCliStatusResponse Refused(string status) => new(status, false, ConfiguredClaudeCodeCli.PinnedVersion, 0, null, null, null, OperatingSystem.IsWindows(), null);
}

/// <param name="State"><c>idle</c>, <c>downloading</c>, <c>installing</c>, <c>done</c>, <c>failed</c> or <c>canceled</c>.</param>
/// <param name="ReceivedBytes">The bytes on disk.</param>
/// <param name="TotalBytes">The bytes of the download.</param>
/// <param name="Error">For <c>failed</c>: <c>network</c>, <c>not_found</c>, <c>checksum</c>, <c>busy_elsewhere</c>, <c>disk</c> or <c>unsupported_platform</c>.</param>
internal sealed record ClaudeCliInstallState(string State,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long ReceivedBytes,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long TotalBytes, string? Error);
