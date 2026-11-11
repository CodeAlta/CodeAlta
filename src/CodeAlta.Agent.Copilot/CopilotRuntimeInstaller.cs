using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeAlta.NativeEngines;

namespace CodeAlta.Agent.Copilot;

/// <summary>What a download of the pinned Copilot runtime reports.</summary>
/// <param name="Message">What it does, as a sentence.</param>
/// <param name="Received">The bytes of the package on disk, or <see langword="null"/>.</param>
/// <param name="Total">The bytes of the package, or <see langword="null"/>.</param>
public sealed record CopilotRuntimeInstallProgress(string Message, long? Received = null, long? Total = null);

/// <summary>An installed Copilot runtime.</summary>
/// <param name="Executable">The full path of <c>copilot-runtime</c>, beside its <c>runtime.node</c>.</param>
/// <param name="Version">The version of the runtime.</param>
public sealed record CopilotRuntimeInstallation(string Executable, string Version);

/// <summary>A download of the pinned Copilot runtime failed.</summary>
public sealed class CopilotRuntimeInstallException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="CopilotRuntimeInstallException"/> class.</summary>
    /// <param name="code"><c>network</c>, <c>not_found</c>, <c>checksum</c>, <c>busy_elsewhere</c>, <c>disk</c>, <c>package</c> or <c>unsupported_platform</c>.</param>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The cause, or <see langword="null"/>.</param>
    public CopilotRuntimeInstallException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Gets why the download failed.</summary>
    public string Code { get; }
}

/// <summary>
/// Downloads the runtime of the pinned Copilot CLI release (<see cref="CopilotPinnedRuntime"/>) into the cache of
/// CodeAlta, the first time a Copilot provider needs it. It keeps what the Copilot SDK keeps of the package for an
/// application that embeds it (<c>GitHub.Copilot.SDK.targets</c>): the runtime and the files beside it, not the
/// terminal CLI. One download runs at a time for this installer, and the lock of <see cref="PinnedDownloader"/> keeps
/// the normal and the developer instance from writing the same files: an instance that finds the other one
/// downloading waits for it.
/// </summary>
public sealed class CopilotRuntimeInstaller : IDisposable
{
    private const string RuntimeLibraryName = "runtime.node";

    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BusyTimeout = TimeSpan.FromMinutes(15);

    // What the SDK leaves out of the root of the package: the terminal CLI and what only it uses.
    private static readonly HashSet<string> SkippedRootFiles = new(StringComparer.Ordinal)
    {
        "app.js", "changelog.json", "cli-main.js", "copilot", "copilot.exe", "index.js", "LICENSE.md", "npm-loader.js",
        "package.json", "README.md", "sea-loader.js", "SHA256SUMS.txt",
    };

    private static readonly HashSet<string> SkippedRootFolders = new(StringComparer.Ordinal)
    {
        "assets", "foundry-local-sdk", "napi-oop-runtime", "pvrecorder", "queries", "webview",
    };

    private readonly string _root;
    private readonly PinnedDownloader _downloads;
    private readonly CopilotRuntimePackage? _package;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the installer of this computer, on the network.</summary>
    /// <param name="root">The folder of the Copilot runtime in the cache of CodeAlta (<c>~/.alta/cache/copilot-runtime</c>).</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null or empty.</exception>
    public CopilotRuntimeInstaller(string root)
        : this(root, null, CopilotPinnedRuntime.FindPackage(CopilotPinnedRuntime.CurrentPlatform()))
    {
    }

    /// <summary>Creates the installer of a package, for tests.</summary>
    /// <param name="root">The folder of the Copilot runtime in the cache.</param>
    /// <param name="handler">The handler of its requests, or <see langword="null"/> for the network.</param>
    /// <param name="package">The package of the platform, or <see langword="null"/> when the release has none for it.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null or empty.</exception>
    public CopilotRuntimeInstaller(string root, HttpMessageHandler? handler, CopilotRuntimePackage? package)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        _root = Path.GetFullPath(root);
        _downloads = new PinnedDownloader(handler, new ProductInfoHeaderValue("CodeAlta", "1.0"));
        _package = package;
    }

    /// <summary>Gets the folder of the Copilot runtime in the cache.</summary>
    public string Root => _root;

    /// <summary>Gets the package of this platform, or <see langword="null"/> when the release has none for it.</summary>
    public CopilotRuntimePackage? Package => _package;

    /// <summary>Gets the pinned runtime when it was downloaded, or <see langword="null"/>.</summary>
    public CopilotRuntimeInstallation? Installed => _package is null ? null : Inspect(CopilotPinnedRuntime.GetInstallDirectory(_root, _package.Platform), out _);

    /// <summary>Gets the pinned runtime, and downloads it first when it is not in the cache.</summary>
    /// <param name="progress">Where the progress of a download goes, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Ends the download; what is on disk stays for the next attempt.</param>
    /// <returns>The installation.</returns>
    /// <exception cref="CopilotRuntimeInstallException">The download failed; its code says why.</exception>
    /// <exception cref="OperationCanceledException">The download was cancelled.</exception>
    public async Task<CopilotRuntimeInstallation> EnsureInstalledAsync(IProgress<CopilotRuntimeInstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (_package is not { } package)
        {
            throw new CopilotRuntimeInstallException("unsupported_platform", "GitHub publishes no Copilot runtime for this platform.");
        }

        if (Installed is { } installed)
        {
            return installed;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = CopilotPinnedRuntime.GetInstallDirectory(_root, package.Platform);
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                if (Inspect(target, out _) is { } found)
                {
                    return found;
                }

                var attempt = new Attempt(progress, package, target);
                try
                {
                    // A folder with its marker but without a file of the runtime lost it after the install, to an
                    // antivirus for example: without its marker, the downloader removes it and installs it again.
                    var marker = Path.Combine(target, CopilotPinnedRuntime.MarkerFileName);
                    if (File.Exists(marker) && Inspect(target, out _) is null)
                    {
                        attempt.Removing();
                        File.Delete(marker);
                    }

                    progress?.Report(new CopilotRuntimeInstallProgress($"Downloading the Copilot runtime {CopilotPinnedRuntime.Version}…", 0, package.Size));
                    await InstallAsync(package, target, attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (PinnedDownloadException exception) when (exception.Code == "busy_elsewhere" && waited.Elapsed < BusyTimeout)
                {
                    // The other instance of CodeAlta downloads it: this one waits for its folder.
                    progress?.Report(new CopilotRuntimeInstallProgress($"The Copilot runtime {CopilotPinnedRuntime.Version} is being downloaded by another CodeAlta…"));
                    await Task.Delay(BusyRetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (PinnedDownloadException exception)
                {
                    throw new CopilotRuntimeInstallException(exception.Code, Describe(exception.Code, attempt, exception.InnerException), exception);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    var code = exception is HttpRequestException ? "network" : "disk";
                    throw new CopilotRuntimeInstallException(code, Describe(code, attempt, exception), exception);
                }

                // An install that ends without a complete folder is not tried again: the next one would end the same.
                return Inspect(target, out var missing) ?? throw new CopilotRuntimeInstallException("disk",
                    $"The Copilot runtime {CopilotPinnedRuntime.Version} is not complete in {target}: {missing} Something, such as an antivirus, may have removed it once unpacked.");
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

    /// <summary>
    /// Gets where a file of the package goes in the installation folder, as the Copilot SDK lays the runtime out for an
    /// application: the files of <c>prebuilds/&lt;platform&gt;</c> beside the other files of the root, without the
    /// terminal CLI; <see langword="null"/> for a file that is not kept.
    /// </summary>
    /// <param name="entryName">The name of the entry in the package, under its top folder (<c>package/</c>), which the SDK strips too.</param>
    /// <param name="platform">The platform of the package.</param>
    /// <returns>The relative path, its segments separated by <c>/</c>, or <see langword="null"/>.</returns>
    internal static string? Destination(string entryName, string platform)
    {
        var segments = entryName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments.Contains("..") || segments.Contains(".")) return null;
        var path = segments.AsSpan(1);
        if (path[0] == "prebuilds")
        {
            if (path.Length < 3 || path[1] != platform) return null;
            var name = path[2];
            return name is "cli-native.node" or "mediaremote-adapter" || name.StartsWith("copilot-runtime-bin", StringComparison.Ordinal)
                ? null
                : string.Join('/', path[2..].ToArray());
        }

        if (path.Length == 1)
        {
            var name = path[0];
            if (SkippedRootFiles.Contains(name)
                || (name.StartsWith("tree-sitter", StringComparison.Ordinal) && name.EndsWith(".wasm", StringComparison.Ordinal))
                || (name.StartsWith("voice-", StringComparison.Ordinal) && name.EndsWith(".js", StringComparison.Ordinal)))
            {
                return null;
            }
        }
        else if (SkippedRootFolders.Contains(path[0]))
        {
            return null;
        }

        return string.Join('/', path.ToArray());
    }

    // Reads an installation folder; when it is not a complete one, says which of its files is missing, as a sentence.
    private static CopilotRuntimeInstallation? Inspect(string directory, out string missing)
    {
        foreach (var name in (string[])[CopilotPinnedRuntime.MarkerFileName, CopilotPinnedRuntime.ExecutableName, RuntimeLibraryName])
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                missing = $"{path} is missing.";
                return null;
            }
        }

        missing = string.Empty;
        return new CopilotRuntimeInstallation(Path.Combine(directory, CopilotPinnedRuntime.ExecutableName), CopilotPinnedRuntime.Version);
    }

    private Task InstallAsync(CopilotRuntimePackage package, string target, Attempt attempt, CancellationToken cancellationToken)
        => _downloads.InstallFolderAsync(
            target,
            CopilotPinnedRuntime.MarkerFileName,
            [(new PinnedFile(package.FileName, package.Size, package.Sha256), CopilotPinnedRuntime.AddressOf(package))],
            async (partial, token) =>
            {
                var archive = Path.Combine(partial, package.FileName);
                attempt.Unpacking(archive);
                await ExtractAsync(archive, partial, package.Platform, token).ConfigureAwait(false);
                File.Delete(archive);
                if (!File.Exists(Path.Combine(partial, CopilotPinnedRuntime.ExecutableName)) || !File.Exists(Path.Combine(partial, RuntimeLibraryName)))
                {
                    // The hash matched, so this is what the release holds: a pin that names another layout is wrong.
                    throw new PinnedDownloadException("package");
                }

                attempt.Installing();
            },
            Marker(package),
            attempt,
            cancellationToken);

    /// <summary>Unpacks the files of the package the runtime needs into a folder, keeping their executable bits.</summary>
    internal static async Task ExtractAsync(string archive, string destination, string platform, CancellationToken cancellationToken)
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
                        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || Destination(entry.Name, platform) is not { } relative)
                        {
                            continue;
                        }

                        // The paths of a tar file use '/': each segment is combined for the separators of this system.
                        var path = Path.GetFullPath(Path.Combine([root, .. relative.Split('/')]));
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
            "network" => $"The Copilot runtime {CopilotPinnedRuntime.Version} could not be downloaded from GitHub: check the connection.",
            "not_found" => $"The package of the Copilot runtime {CopilotPinnedRuntime.Version} was not found on GitHub.",
            "checksum" => $"The package of the Copilot runtime {CopilotPinnedRuntime.Version} does not match its pinned SHA-256: it was not used.",
            "busy_elsewhere" => $"Another CodeAlta is still downloading the Copilot runtime {CopilotPinnedRuntime.Version}.",
            "package" => $"The package of the Copilot runtime {CopilotPinnedRuntime.Version} is not the one CodeAlta pins.",
            _ => attempt.Failure,
        };
        return cause is null ? reason : $"{reason} ({DescribeCause(cause)})";
    }

    // The type and the HRESULT of an exception, which say what failed even when the system gives no message, then its message.
    private static string DescribeCause(Exception exception)
    {
        var kind = $"{exception.GetType().Name} 0x{exception.HResult:X8}";
        return string.IsNullOrWhiteSpace(exception.Message) ? kind : $"{kind}: {exception.Message.Trim()}";
    }

    private static byte[] Marker(CopilotRuntimePackage package)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("version", CopilotPinnedRuntime.Version);
            writer.WriteString("platform", package.Platform);
            writer.WriteString("package", package.FileName);
            writer.WriteString("sha256", package.Sha256);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    // One attempt to install the package: it reports its progress, and knows its step to say what a failure of the
    // disk stopped.
    private sealed class Attempt(IProgress<CopilotRuntimeInstallProgress>? progress, CopilotRuntimePackage package, string target) : IPinnedDownloadProgress
    {
        private readonly string _partial = target + ".partial";
        private long _received;
        private string? _failure;

        public string Failure => _failure ?? $"The Copilot runtime {CopilotPinnedRuntime.Version} could not be written to {_partial}.";

        public void Received(long bytes)
        {
            var received = Interlocked.Add(ref _received, bytes);
            progress?.Report(new CopilotRuntimeInstallProgress($"Downloading the Copilot runtime {CopilotPinnedRuntime.Version}…", received, package.Size));
        }

        public void Checking(PinnedFile file)
        {
            _failure = $"The Copilot runtime {CopilotPinnedRuntime.Version} could not be checked in {_partial}.";
            progress?.Report(new CopilotRuntimeInstallProgress($"Checking the Copilot runtime {CopilotPinnedRuntime.Version}…", package.Size, package.Size));
        }

        public void Unpacking(string archive)
        {
            _failure = $"The Copilot runtime {CopilotPinnedRuntime.Version} could not be unpacked from {archive}.";
            progress?.Report(new CopilotRuntimeInstallProgress($"Unpacking the Copilot runtime {CopilotPinnedRuntime.Version}…", package.Size, package.Size));
        }

        public void Installing() => _failure = $"The Copilot runtime {CopilotPinnedRuntime.Version} was unpacked, but could not be moved to {target}.";

        public void Removing() => _failure = $"The Copilot runtime {CopilotPinnedRuntime.Version} is not complete in {target}, and could not be removed to be installed again.";
    }
}
