using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeAlta.NativeEngines;

namespace CodeAlta.Agent.Claude;

/// <summary>Where the download of the pinned Claude Code CLI is.</summary>
/// <param name="State"><c>idle</c>, <c>downloading</c>, <c>installing</c>, <c>done</c>, <c>failed</c> or <c>canceled</c>.</param>
/// <param name="ReceivedBytes">The bytes of the archive on disk, of <paramref name="TotalBytes"/>.</param>
/// <param name="TotalBytes">The bytes of the archive.</param>
/// <param name="Error">For <c>failed</c>: <c>network</c>, <c>not_found</c>, <c>checksum</c>, <c>busy_elsewhere</c>, <c>disk</c> or <c>unsupported_platform</c>.</param>
public sealed record ClaudeCodeCliInstallSnapshot(string State, long ReceivedBytes, long TotalBytes, string? Error)
{
    /// <summary>Gets the state before any download.</summary>
    public static ClaudeCodeCliInstallSnapshot Idle { get; } = new("idle", 0, 0, null);

    /// <summary>Gets whether a download runs.</summary>
    public bool IsRunning => State is "downloading" or "installing";
}

/// <summary>
/// Downloads the Claude Code CLI CodeAlta pins (<see cref="ClaudeCodePinnedCli"/>) into the cache of CodeAlta, from
/// the GitHub release of Claude Code. Nothing is downloaded before <see cref="TryStart"/> is called. The download
/// belongs to the application, not to a window: a subscriber follows it and never ends it.
/// </summary>
/// <remarks>
/// The archive is written as <c>name.part</c> in a <c>.partial</c> folder beside the final one, resumed where it
/// stopped, checked against the pinned SHA-256, and only then given its name. The executable is taken out of it,
/// the archive is removed, and the folder gets its own name once its marker file is written, so a folder under its
/// name is complete. A lock in the <c>.partial</c> folder keeps the normal and the developer instance from writing
/// the same files.
/// </remarks>
public sealed class ClaudeCodeCliInstaller
{

    private readonly string _root;
    private readonly PinnedDownloader _downloads;
    private readonly ClaudeCodeCliAsset? _asset;
    private readonly Lock _gate = new();
    private readonly List<Action<ClaudeCodeCliInstallSnapshot>> _observers = [];
    private ClaudeCodeCliInstallSnapshot _snapshot = ClaudeCodeCliInstallSnapshot.Idle;
    private CancellationTokenSource? _cancellation;
    private Task _active = Task.CompletedTask;
    private bool _closed;

    /// <summary>Creates the installer of this computer, on the network.</summary>
    /// <param name="root">The folder of Claude Code in the cache of CodeAlta.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null or empty.</exception>
    public ClaudeCodeCliInstaller(string root)
        : this(root, null, ClaudeCodePinnedCli.CurrentRuntimeIdentifier())
    {
    }

    /// <summary>Creates the installer of a platform.</summary>
    /// <param name="root">The folder of Claude Code in the cache of CodeAlta.</param>
    /// <param name="handler">The handler of its requests, or null for the network.</param>
    /// <param name="runtimeIdentifier">The platform whose CLI is installed, or null when Claude Code has none for it.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null or empty.</exception>
    public ClaudeCodeCliInstaller(string root, HttpMessageHandler? handler, string? runtimeIdentifier)
        : this(root, handler, ClaudeCodePinnedCli.FindAsset(runtimeIdentifier))
    {
    }

    internal ClaudeCodeCliInstaller(string root, HttpMessageHandler? handler, ClaudeCodeCliAsset? asset)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        _root = Path.GetFullPath(root);
        _downloads = new(handler, ProductInfoHeaderValue.TryParse(ClaudeCodeLauncher.DefaultClientApp, out var product) ? product : null);
        _asset = asset;
    }

    /// <summary>Gets the folder of Claude Code in the cache.</summary>
    public string Root => _root;

    /// <summary>Gets the archive of this platform, or null when Claude Code has none for it.</summary>
    public ClaudeCodeCliAsset? Asset => _asset;

    /// <summary>Gets whether Claude Code has a CLI for this platform.</summary>
    public bool IsSupported => _asset is not null;

    /// <summary>Gets the folder the CLI is installed in, or null when the platform has none.</summary>
    public string? InstallDirectory => _asset is null ? null : ClaudeCodePinnedCli.GetInstallDirectory(_root, _asset.RuntimeIdentifier);

    /// <summary>Gets the installed executable, or null when it was not downloaded.</summary>
    public string? InstalledPath => ClaudeCodePinnedCli.FindInstalled(_root, _asset?.RuntimeIdentifier, File.Exists);

    /// <summary>Gets the latest state.</summary>
    public ClaudeCodeCliInstallSnapshot Current { get { lock (_gate) return _snapshot; } }

    /// <summary>Follows the download. The observer gets the current state at once, then every change.</summary>
    /// <param name="observer">Called with each state, under the lock of the installer: it must not block.</param>
    /// <returns>Ends the subscription when disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="observer"/> is null.</exception>
    public IDisposable Subscribe(Action<ClaudeCodeCliInstallSnapshot> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            _observers.Add(observer);
            observer(_snapshot);
        }

        return new Subscription(this, observer);
    }

    /// <summary>Starts the download. False while one runs, or once the installer is closed.</summary>
    /// <returns>Whether a download started.</returns>
    public bool TryStart()
    {
        lock (_gate)
        {
            if (_closed || _snapshot.IsRunning)
            {
                return false;
            }

            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            SetLocked(new("downloading", 0, _asset?.Size ?? 0, null));
            _active = Task.Run(() => RunAsync(cancellation));
            return true;
        }
    }

    /// <summary>Stops the running download. What is on disk stays, and the next download goes on from it.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _cancellation?.Cancel();
        }
    }

    /// <summary>Stops the download and waits for it to end.</summary>
    /// <returns>The end of the download.</returns>
    public async Task CloseAsync()
    {
        Task active;
        lock (_gate)
        {
            _closed = true;
            _cancellation?.Cancel();
            active = _active;
        }

        await active.ConfigureAwait(false);
        _downloads.Dispose();
    }

    /// <summary>Waits for the running download, for tests.</summary>
    internal Task WaitAsync()
    {
        lock (_gate)
        {
            return _active;
        }
    }

    private async Task RunAsync(CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            if (_asset is not { } asset)
            {
                throw new ClaudeCodeCliInstallException("unsupported_platform");
            }

            var target = ClaudeCodePinnedCli.GetInstallDirectory(_root, asset.RuntimeIdentifier);
            var progress = new Progress(this, asset.Size);
            if (InstalledPath is null)
            {
                await InstallAsync(asset, target, progress, token).ConfigureAwait(false);
            }

            if (InstalledPath is null)
            {
                throw new ClaudeCodeCliInstallException("disk");
            }

            progress.Publish("done", asset.Size);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            End("canceled", null);
        }
        catch (ClaudeCodeCliInstallException exception)
        {
            End("failed", exception.Code);
        }
        catch (PinnedDownloadException exception)
        {
            End("failed", exception.Code);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        {
            End("failed", exception is HttpRequestException or OperationCanceledException ? "network" : "disk");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cancellation, cancellation))
                {
                    _cancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private Task InstallAsync(ClaudeCodeCliAsset asset, string target, Progress progress, CancellationToken cancellationToken)
        => _downloads.InstallFolderAsync(target, ClaudeCodePinnedCli.MarkerFileName,
            [(new PinnedFile(asset.FileName, asset.Size, asset.Sha256), ClaudeCodePinnedCli.AddressOf(asset))],
            async (partial, token) =>
            {
                var archive = Path.Combine(partial, asset.FileName);
                progress.Publish("installing", asset.Size);
                await ExtractAsync(archive, asset, Path.Combine(partial, asset.ExecutableName), token).ConfigureAwait(false);
                File.Delete(archive);
            },
            Marker(asset), progress, cancellationToken);

    /// <summary>Takes the executable out of an archive: the one file of that name, at its root or in a folder.</summary>
    internal static async Task ExtractAsync(string archive, ClaudeCodeCliAsset asset, string destination, CancellationToken cancellationToken)
    {
        if (asset.IsZip)
        {
            using var zip = ZipFile.OpenRead(archive);
            var entry = zip.Entries.Where(entry => string.Equals(Path.GetFileName(entry.FullName), asset.ExecutableName, StringComparison.Ordinal))
                .OrderBy(static entry => entry.FullName.Length).FirstOrDefault() ?? throw new ClaudeCodeCliInstallException("checksum");
            await entry.ExtractToFileAsync(destination, overwrite: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (input.ConfigureAwait(false))
        {
            var gzip = new GZipStream(input, CompressionMode.Decompress);
            await using (gzip.ConfigureAwait(false))
            {
                var reader = new TarReader(gzip);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
                    {
                        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                            !string.Equals(Path.GetFileName(entry.Name.TrimEnd('/')), asset.ExecutableName, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        await entry.ExtractToFileAsync(destination, overwrite: true, cancellationToken).ConfigureAwait(false);
                        if (!OperatingSystem.IsWindows())
                        {
                            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                        }

                        return;
                    }
                }
            }
        }

        throw new ClaudeCodeCliInstallException("checksum");
    }

    private static byte[] Marker(ClaudeCodeCliAsset asset)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("version", ClaudeCodePinnedCli.Version);
            writer.WriteString("runtimeIdentifier", asset.RuntimeIdentifier);
            writer.WriteString("archive", asset.FileName);
            writer.WriteString("sha256", asset.Sha256);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private void End(string state, string? error)
    {
        lock (_gate)
        {
            SetLocked(_snapshot with { State = state, Error = error });
        }
    }

    private void SetLocked(ClaudeCodeCliInstallSnapshot snapshot)
    {
        _snapshot = snapshot;
        foreach (var observer in _observers)
        {
            observer(snapshot);
        }
    }

    private sealed class Subscription(ClaudeCodeCliInstaller owner, Action<ClaudeCodeCliInstallSnapshot> observer) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._observers.Remove(observer);
            }
        }
    }

    /// <summary>The bytes of the archive on disk, published at most ten times a second.</summary>
    private sealed class Progress(ClaudeCodeCliInstaller owner, long total) : IPinnedDownloadProgress
    {
        private long _published;
        private long _received;

        public void Received(long bytes)
        {
            _received += bytes;
            Publish("downloading", _received);
        }

        public void Checking(PinnedFile file) => Publish("installing", total);

        public void Publish(string state, long received)
        {
            var now = Stopwatch.GetTimestamp();
            if (state == "downloading" && received < total && Stopwatch.GetElapsedTime(_published, now) < TimeSpan.FromMilliseconds(100))
            {
                return;
            }

            _published = now;
            lock (owner._gate)
            {
                owner.SetLocked(new(state, Math.Min(received, total), total, null));
            }
        }
    }
}

/// <summary>A download that ends with a reason the user is told.</summary>
internal sealed class ClaudeCodeCliInstallException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
