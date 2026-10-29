using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeAlta.NativeEngines;

namespace CodeAlta.CodexAppServer;

/// <summary>What a download of the pinned app-server reports.</summary>
/// <param name="Message">What it does, as a sentence.</param>
/// <param name="Received">The bytes of the package on disk, or <see langword="null"/>.</param>
/// <param name="Total">The bytes of the package, or <see langword="null"/>.</param>
public sealed record CodexInstallProgress(string Message, long? Received = null, long? Total = null);

/// <summary>An installed Codex app-server.</summary>
/// <param name="Executable">The full path of <c>codex-app-server</c>.</param>
/// <param name="PathDirectory">The folder the package puts first on the <c>PATH</c> of its commands (<c>rg</c>), or <see langword="null"/>.</param>
/// <param name="Version">The version of the package.</param>
public sealed record CodexAppServerInstallation(string Executable, string? PathDirectory, string Version);

/// <summary>A download of the pinned app-server failed.</summary>
public sealed class CodexInstallException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="CodexInstallException"/> class.</summary>
    /// <param name="code"><c>network</c>, <c>not_found</c>, <c>checksum</c>, <c>busy_elsewhere</c>, <c>disk</c>, <c>package</c> or <c>unsupported_platform</c>.</param>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The cause, or <see langword="null"/>.</param>
    public CodexInstallException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Gets why the download failed.</summary>
    public string Code { get; }
}

/// <summary>
/// Downloads the standalone app-server package of the pinned Codex release (<see cref="CodexPinnedRelease"/>) into the
/// cache of CodeAlta, the first time it is needed. One download runs at a time for this installer, and the lock of
/// <see cref="PinnedDownloader"/> keeps the normal and the developer instance from writing the same files: an instance
/// that finds the other one downloading waits for it.
/// </summary>
public sealed class CodexAppServerInstaller : IDisposable
{
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BusyTimeout = TimeSpan.FromMinutes(15);

    private readonly string _root;
    private readonly PinnedDownloader _downloads;
    private readonly CodexAppServerPackage? _package;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the installer of this computer, on the network.</summary>
    /// <param name="root">The folder of the Codex app-server in the cache of CodeAlta (<c>~/.alta/cache/codex-cli</c>).</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null or empty.</exception>
    public CodexAppServerInstaller(string root)
        : this(root, null, CodexPinnedRelease.FindPackage(CodexPinnedRelease.CurrentTarget()))
    {
    }

    /// <summary>Creates the installer of a package, for tests.</summary>
    /// <param name="root">The folder of the Codex app-server in the cache.</param>
    /// <param name="handler">The handler of its requests, or <see langword="null"/> for the network.</param>
    /// <param name="package">The package of the platform, or <see langword="null"/> when Codex has none for it.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null or empty.</exception>
    public CodexAppServerInstaller(string root, HttpMessageHandler? handler, CodexAppServerPackage? package)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        _root = Path.GetFullPath(root);
        _downloads = new PinnedDownloader(handler, new ProductInfoHeaderValue("CodeAlta", "1.0"));
        _package = package;
    }

    /// <summary>Gets the folder of the Codex app-server in the cache.</summary>
    public string Root => _root;

    /// <summary>Gets the package of this platform, or <see langword="null"/> when Codex has none for it.</summary>
    public CodexAppServerPackage? Package => _package;

    /// <summary>Gets the pinned app-server when it was downloaded, or <see langword="null"/>.</summary>
    public CodexAppServerInstallation? Installed => _package is null ? null : FindInstalled(CodexPinnedRelease.GetInstallDirectory(_root, _package.Target));

    /// <summary>Gets the pinned app-server, and downloads it first when it is not in the cache.</summary>
    /// <param name="progress">Where the progress of a download goes, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Ends the download; what is on disk stays for the next attempt.</param>
    /// <returns>The installation.</returns>
    /// <exception cref="CodexInstallException">The download failed; its code says why.</exception>
    /// <exception cref="OperationCanceledException">The download was cancelled.</exception>
    public async Task<CodexAppServerInstallation> EnsureInstalledAsync(IProgress<CodexInstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (_package is not { } package)
        {
            throw new CodexInstallException("unsupported_platform", "Codex publishes no app-server for this platform.");
        }

        if (Installed is { } installed)
        {
            return installed;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = CodexPinnedRelease.GetInstallDirectory(_root, package.Target);
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                if (FindInstalled(target) is { } found)
                {
                    return found;
                }

                var attempt = new Attempt(progress, package, target);
                try
                {
                    // A folder with its marker but without a file of the package lost it after the install, to an
                    // antivirus for example: without its marker, the downloader removes it and installs it again.
                    // Checked again just before: the other instance may have completed the folder since.
                    var marker = Path.Combine(target, CodexPinnedRelease.MarkerFileName);
                    if (File.Exists(marker) && FindInstalled(target) is null)
                    {
                        attempt.Removing();
                        File.Delete(marker);
                    }

                    progress?.Report(new CodexInstallProgress($"Downloading Codex {CodexPinnedRelease.Version}…", 0, package.Size));
                    await InstallAsync(package, target, attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (PinnedDownloadException exception) when (exception.Code == "busy_elsewhere" && waited.Elapsed < BusyTimeout)
                {
                    // The other instance of CodeAlta downloads it: this one waits for its folder.
                    progress?.Report(new CodexInstallProgress($"Codex {CodexPinnedRelease.Version} is being downloaded by another CodeAlta…"));
                    await Task.Delay(BusyRetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (PinnedDownloadException exception)
                {
                    throw new CodexInstallException(exception.Code, Describe(exception.Code, attempt, exception.InnerException), exception);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    var code = exception is HttpRequestException ? "network" : "disk";
                    throw new CodexInstallException(code, Describe(code, attempt, exception), exception);
                }

                // An install that ends without a complete folder is not tried again: the next one would end the same.
                return Inspect(target, out var missing) ?? throw new CodexInstallException("disk",
                    $"Codex {CodexPinnedRelease.Version} is not complete in {target}: {missing} Something, such as an antivirus, may have removed it once unpacked.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _downloads.Dispose();
        _gate.Dispose();
    }

    /// <summary>Reads an installation folder: its marker, then the entry point its package names.</summary>
    /// <param name="directory">The folder.</param>
    /// <returns>The installation, or <see langword="null"/> when the folder is not a complete one.</returns>
    public static CodexAppServerInstallation? FindInstalled(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Inspect(directory, out _);
    }

    // Reads an installation folder; when it is not a complete one, says which of its files is missing, as a sentence.
    private static CodexAppServerInstallation? Inspect(string directory, out string missing)
    {
        var marker = Path.Combine(directory, CodexPinnedRelease.MarkerFileName);
        var manifestPath = Path.Combine(directory, CodexPinnedRelease.PackageManifestFileName);
        if (!File.Exists(marker))
        {
            missing = $"{marker} is missing.";
            return null;
        }

        if (ReadManifest(manifestPath) is not { } manifest)
        {
            missing = $"{manifestPath} is missing or invalid.";
            return null;
        }

        var executable = Path.GetFullPath(Path.Combine([directory, .. manifest.Entrypoint.Split('/', StringSplitOptions.RemoveEmptyEntries)]));
        if (!File.Exists(executable))
        {
            missing = $"{executable} is missing.";
            return null;
        }

        missing = string.Empty;
        var pathDirectory = manifest.PathDirectory is { } relative ? Path.GetFullPath(Path.Combine([directory, .. relative.Split('/', StringSplitOptions.RemoveEmptyEntries)])) : null;
        return new CodexAppServerInstallation(executable, pathDirectory is not null && Directory.Exists(pathDirectory) ? pathDirectory : null, manifest.Version);
    }

    private Task InstallAsync(CodexAppServerPackage package, string target, Attempt attempt, CancellationToken cancellationToken)
        => _downloads.InstallFolderAsync(
            target,
            CodexPinnedRelease.MarkerFileName,
            [(new PinnedFile(package.FileName, package.Size, package.Sha256), CodexPinnedRelease.AddressOf(package))],
            async (partial, token) =>
            {
                var archive = Path.Combine(partial, package.FileName);
                attempt.Unpacking(archive);
                await ExtractAsync(archive, partial, token).ConfigureAwait(false);
                File.Delete(archive);
                var manifest = ReadManifest(Path.Combine(partial, CodexPinnedRelease.PackageManifestFileName))
                    ?? throw new PinnedDownloadException("package");
                if (!string.Equals(manifest.Version, CodexPinnedRelease.Version, StringComparison.Ordinal) ||
                    !string.Equals(manifest.Target, package.Target, StringComparison.Ordinal))
                {
                    // The hash matched, so this is what the release holds: a pin that names another version is wrong.
                    throw new PinnedDownloadException("package");
                }

                attempt.Installing();
            },
            Marker(package),
            attempt,
            cancellationToken);

    /// <summary>Unpacks a <c>.tar.gz</c> package into a folder, keeping the executable bits of its files.</summary>
    internal static async Task ExtractAsync(string archive, string destination, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(destination);
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
                        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                        {
                            continue;
                        }

                        // The paths of a tar file use '/': each segment is combined for the separators of this system.
                        var path = Path.GetFullPath(Path.Combine([root, .. entry.Name.Split('/', StringSplitOptions.RemoveEmptyEntries)]));
                        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        {
                            throw new PinnedDownloadException("package");
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        await entry.ExtractToFileAsync(path, overwrite: true, cancellationToken).ConfigureAwait(false);
                        if (!OperatingSystem.IsWindows() && (entry.Mode & UnixFileMode.UserExecute) != 0)
                        {
                            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                        }
                    }
                }
            }
        }
    }

    // The reason of a failed attempt, then its cause: a failure of the disk says which step failed on which file.
    private static string Describe(string code, Attempt attempt, Exception? cause)
    {
        var reason = code switch
        {
            "network" => $"Codex {CodexPinnedRelease.Version} could not be downloaded from GitHub: check the connection.",
            "not_found" => $"The package of Codex {CodexPinnedRelease.Version} was not found on GitHub.",
            "checksum" => $"The package of Codex {CodexPinnedRelease.Version} does not match its pinned SHA-256: it was not used.",
            "busy_elsewhere" => $"Another CodeAlta is still downloading Codex {CodexPinnedRelease.Version}.",
            "package" => $"The package of Codex {CodexPinnedRelease.Version} is not the one CodeAlta pins.",
            _ => attempt.Failure,
        };
        return cause is null ? reason : $"{reason} ({DescribeCause(cause)})";
    }

    /// <summary>
    /// Describes an exception for the user: its type and its HRESULT, which say what failed even when the system gives
    /// no message, then its message.
    /// </summary>
    internal static string DescribeCause(Exception exception)
    {
        var kind = $"{exception.GetType().Name} 0x{exception.HResult:X8}";
        return string.IsNullOrWhiteSpace(exception.Message) ? kind : $"{kind}: {exception.Message.Trim()}";
    }

    private static byte[] Marker(CodexAppServerPackage package)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("version", CodexPinnedRelease.Version);
            writer.WriteString("target", package.Target);
            writer.WriteString("package", package.FileName);
            writer.WriteString("sha256", package.Sha256);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private sealed record Manifest(string Version, string Target, string Entrypoint, string? PathDirectory);

    private static Manifest? ReadManifest(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            string? Read(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return Read("version") is { } version && Read("target") is { } target && Read("entrypoint") is { Length: > 0 } entrypoint
                ? new Manifest(version, target, entrypoint, Read("pathDir"))
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // One attempt to install the package: it reports its progress, and knows its step to say what a failure of the
    // disk stopped.
    private sealed class Attempt(IProgress<CodexInstallProgress>? progress, CodexAppServerPackage package, string target) : IPinnedDownloadProgress
    {
        private readonly string _partial = target + ".partial";
        private long _received;
        private string? _failure;

        public string Failure => _failure ?? $"Codex {CodexPinnedRelease.Version} could not be written to {_partial}.";

        public void Received(long bytes)
        {
            var received = Interlocked.Add(ref _received, bytes);
            progress?.Report(new CodexInstallProgress($"Downloading Codex {CodexPinnedRelease.Version}…", received, package.Size));
        }

        public void Checking(PinnedFile file)
        {
            _failure = $"Codex {CodexPinnedRelease.Version} could not be checked in {_partial}.";
            progress?.Report(new CodexInstallProgress($"Checking Codex {CodexPinnedRelease.Version}…", package.Size, package.Size));
        }

        public void Unpacking(string archive)
        {
            _failure = $"Codex {CodexPinnedRelease.Version} could not be unpacked from {archive}.";
            progress?.Report(new CodexInstallProgress($"Unpacking Codex {CodexPinnedRelease.Version}…", package.Size, package.Size));
        }

        public void Installing() => _failure = $"Codex {CodexPinnedRelease.Version} was unpacked, but could not be moved to {target}.";

        public void Removing() => _failure = $"Codex {CodexPinnedRelease.Version} is not complete in {target}, and could not be removed to be installed again.";
    }
}
