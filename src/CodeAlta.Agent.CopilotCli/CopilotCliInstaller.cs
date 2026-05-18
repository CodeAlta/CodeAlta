using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;

namespace CodeAlta.Agent.CopilotCli;

/// <summary>
/// Describes installation progress while downloading or extracting a pinned Copilot CLI package.
/// </summary>
/// <param name="Stage">The current installation stage.</param>
/// <param name="Version">The Copilot CLI version.</param>
/// <param name="PlatformName">The npm package platform name.</param>
/// <param name="Message">The human-readable progress message.</param>
/// <param name="BytesDownloaded">The number of bytes downloaded so far.</param>
/// <param name="TotalBytes">The total number of bytes to download when known.</param>
public sealed record CopilotCliInstallProgress(
    CopilotCliInstallStage Stage,
    string Version,
    string PlatformName,
    string Message,
    long BytesDownloaded = 0,
    long? TotalBytes = null);

/// <summary>
/// Represents the current stage of Copilot CLI installation.
/// </summary>
public enum CopilotCliInstallStage
{
    /// <summary>
    /// Resolving the target package and preparing the cache directory.
    /// </summary>
    Resolving,

    /// <summary>
    /// Downloading the npm package tarball.
    /// </summary>
    Downloading,

    /// <summary>
    /// Extracting the Copilot CLI executable from the package tarball.
    /// </summary>
    Extracting,

    /// <summary>
    /// The Copilot CLI executable is available in the local cache.
    /// </summary>
    Ready,
}

/// <summary>
/// Options used by <see cref="CopilotCliInstaller"/> when installing the Copilot CLI.
/// </summary>
public sealed class CopilotCliInstallOptions
{
    /// <summary>
    /// Gets the Copilot CLI version to install. When <see langword="null"/>, the version pinned at build time is used.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Gets the local CodeAlta cache root. When <see langword="null"/>, this defaults to <c>~/.alta/cache</c>.
    /// </summary>
    public string? LocalRootPath { get; init; }

    /// <summary>
    /// Gets the npm registry URL used to download Copilot CLI packages.
    /// When <see langword="null"/>, this defaults to <c>https://registry.npmjs.org</c>.
    /// </summary>
    public string? NpmRegistryUrl { get; init; }

    /// <summary>
    /// Gets an optional progress sink used while downloading or extracting the Copilot CLI package.
    /// </summary>
    public IProgress<CopilotCliInstallProgress>? Progress { get; init; }
}

/// <summary>
/// Describes a resolved Copilot CLI installation.
/// </summary>
/// <param name="Version">The Copilot CLI version.</param>
/// <param name="InstallDirectory">The directory containing the extracted executable.</param>
/// <param name="ExecutablePath">The full path to the Copilot CLI executable.</param>
/// <param name="PlatformName">The npm package platform name.</param>
public sealed record CopilotCliInstallation(
    string Version,
    string InstallDirectory,
    string ExecutablePath,
    string PlatformName);

internal enum CopilotCliPlatformKind
{
    Windows,
    MacOS,
    Linux,
}

internal readonly record struct CopilotCliPackage(
    string PlatformName,
    string BinaryName,
    CopilotCliPlatformKind Platform,
    Architecture Architecture);

/// <summary>
/// Installs the GitHub Copilot CLI from the platform-specific npm package into the local CodeAlta cache.
/// </summary>
public static class CopilotCliInstaller
{
    internal const string DefaultNpmRegistryUrl = "https://registry.npmjs.org";

    private static readonly TimeSpan InstallLockRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly HttpClient HttpClient = CreateHttpClient();

    /// <summary>
    /// Gets the Copilot CLI version pinned into this assembly at build time.
    /// </summary>
    public static string DefaultVersion => Copilot.CopilotAgentBackend.CopilotCliVersion;

    /// <summary>
    /// Ensures the pinned Copilot CLI is installed for the current runtime.
    /// </summary>
    /// <returns>The resolved Copilot CLI installation.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the pinned Copilot CLI version is unavailable.</exception>
    /// <exception cref="PlatformNotSupportedException">Thrown when the current platform is not supported by Copilot CLI packages.</exception>
    /// <exception cref="HttpRequestException">Thrown when the npm package download fails.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the Copilot CLI executable cannot be found in the package.</exception>
    public static Task<CopilotCliInstallation> EnsureInstalledAsync()
    {
        return EnsureInstalledAsync(null, CancellationToken.None);
    }

    /// <summary>
    /// Ensures the pinned Copilot CLI is installed for the current runtime.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the installation.</param>
    /// <returns>The resolved Copilot CLI installation.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the pinned Copilot CLI version is unavailable.</exception>
    /// <exception cref="OperationCanceledException">Thrown when installation is canceled.</exception>
    /// <exception cref="PlatformNotSupportedException">Thrown when the current platform is not supported by Copilot CLI packages.</exception>
    /// <exception cref="HttpRequestException">Thrown when the npm package download fails.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the Copilot CLI executable cannot be found in the package.</exception>
    public static Task<CopilotCliInstallation> EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        return EnsureInstalledAsync(null, cancellationToken);
    }

    /// <summary>
    /// Ensures the requested Copilot CLI is installed for the current runtime.
    /// </summary>
    /// <param name="options">Options controlling version, cache location, registry, and progress reporting.</param>
    /// <param name="cancellationToken">A token used to cancel the installation.</param>
    /// <returns>The resolved Copilot CLI installation.</returns>
    /// <exception cref="ArgumentException">Thrown when an option contains an invalid value.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the pinned Copilot CLI version is unavailable.</exception>
    /// <exception cref="OperationCanceledException">Thrown when installation is canceled.</exception>
    /// <exception cref="PlatformNotSupportedException">Thrown when the current platform is not supported by Copilot CLI packages.</exception>
    /// <exception cref="HttpRequestException">Thrown when the npm package download fails.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the Copilot CLI executable cannot be found in the package.</exception>
    public static async Task<CopilotCliInstallation> EnsureInstalledAsync(
        CopilotCliInstallOptions? options,
        CancellationToken cancellationToken = default)
    {
        options ??= new CopilotCliInstallOptions();

        var version = ResolveVersion(options.Version);
        var package = ResolvePackageForCurrentRuntime();
        var installDirectory = GetInstallDirectory(options.LocalRootPath, version, package.PlatformName);
        var executablePath = Path.Combine(installDirectory, package.BinaryName);

        if (File.Exists(executablePath))
        {
            EnsureExecutablePermissions(executablePath);
            ReportProgress(options.Progress, CopilotCliInstallStage.Ready, version, package.PlatformName, $"Using Copilot CLI {version}.");
            return new CopilotCliInstallation(version, installDirectory, executablePath, package.PlatformName);
        }

        using (await AcquireInstallLockAsync(installDirectory, cancellationToken).ConfigureAwait(false))
        {
            if (File.Exists(executablePath))
            {
                EnsureExecutablePermissions(executablePath);
                ReportProgress(options.Progress, CopilotCliInstallStage.Ready, version, package.PlatformName, $"Using Copilot CLI {version}.");
                return new CopilotCliInstallation(version, installDirectory, executablePath, package.PlatformName);
            }

            ReportProgress(
                options.Progress,
                CopilotCliInstallStage.Resolving,
                version,
                package.PlatformName,
                $"Installing Copilot CLI {version} for {DescribePackage(package)}...");

            if (Directory.Exists(installDirectory))
            {
                Directory.Delete(installDirectory, recursive: true);
            }

            Directory.CreateDirectory(installDirectory);

            var archiveName = $"copilot-{package.PlatformName}-{version}.tgz";
            var archivePath = Path.Combine(installDirectory, archiveName);
            var downloadUri = BuildPackageUri(options.NpmRegistryUrl, package.PlatformName, version);
            await DownloadArchiveAsync(
                    downloadUri,
                    archivePath,
                    version,
                    package.PlatformName,
                    options.Progress,
                    cancellationToken)
                .ConfigureAwait(false);

            ReportProgress(
                options.Progress,
                CopilotCliInstallStage.Extracting,
                version,
                package.PlatformName,
                $"Extracting Copilot CLI {version}...");

            await ExtractPackageBinaryAsync(archivePath, installDirectory, package.BinaryName, cancellationToken).ConfigureAwait(false);
            EnsureExecutablePermissions(executablePath);

            try
            {
                File.Delete(archivePath);
            }
            catch
            {
                // Best effort cleanup; the installed executable is the authoritative artifact.
            }

            ReportProgress(options.Progress, CopilotCliInstallStage.Ready, version, package.PlatformName, $"Copilot CLI {version} is ready.");
            return new CopilotCliInstallation(version, installDirectory, executablePath, package.PlatformName);
        }
    }

    private static async Task<InstallFileLockLease> AcquireInstallLockAsync(string installDirectory, CancellationToken cancellationToken)
    {
        var lockFilePath = Path.GetFullPath(installDirectory) + ".install.lock";
        var parentDirectory = Path.GetDirectoryName(lockFilePath);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                return new InstallFileLockLease(stream);
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(InstallLockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class InstallFileLockLease(FileStream stream) : IDisposable
    {
        public void Dispose() => stream.Dispose();
    }

    internal static CopilotCliPackage ResolvePackageForCurrentRuntime()
    {
        var platform = GetCurrentPlatform();
        var runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        return ResolvePackage(platform, RuntimeInformation.OSArchitecture, runtimeIdentifier);
    }

    internal static CopilotCliPackage ResolvePackage(
        CopilotCliPlatformKind platform,
        Architecture architecture,
        string runtimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);

        if (platform == CopilotCliPlatformKind.Linux &&
            runtimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase))
        {
            throw new PlatformNotSupportedException(
                "Copilot CLI does not publish musl Linux packages. Supported platforms: win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64.");
        }

        var architectureToken = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"Unsupported Copilot CLI architecture '{architecture}'."),
        };

        var osToken = platform switch
        {
            CopilotCliPlatformKind.Windows => "win32",
            CopilotCliPlatformKind.MacOS => "darwin",
            CopilotCliPlatformKind.Linux => "linux",
            _ => throw new PlatformNotSupportedException($"Unsupported Copilot CLI platform '{platform}'."),
        };

        var platformName = $"{osToken}-{architectureToken}";
        var binaryName = platform == CopilotCliPlatformKind.Windows ? "copilot.exe" : "copilot";
        return new CopilotCliPackage(platformName, binaryName, platform, architecture);
    }

    internal static string GetInstallDirectory(string? localRootPath, string version, string platformName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformName);

        var localRoot = NormalizeLocalRoot(localRootPath);
        return Path.Combine(localRoot, "bin", "copilot", version.Trim(), platformName.Trim());
    }

    internal static Uri BuildPackageUri(string? npmRegistryUrl, string platformName, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformName);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var normalizedRegistryUrl = NormalizeNpmRegistryUrl(npmRegistryUrl);
        return new Uri(
            $"{normalizedRegistryUrl}/@github/copilot-{Uri.EscapeDataString(platformName.Trim())}/-/copilot-{Uri.EscapeDataString(platformName.Trim())}-{Uri.EscapeDataString(version.Trim())}.tgz",
            UriKind.Absolute);
    }

    internal static async Task ExtractPackageBinaryAsync(
        string archivePath,
        string destinationDirectory,
        string binaryName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryName);

        var expectedEntryName = $"package/{binaryName}";
        Directory.CreateDirectory(destinationDirectory);

        await using var archiveStream = File.OpenRead(archivePath);
        await using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress, leaveOpen: false);
        using var reader = new TarReader(gzipStream, leaveOpen: false);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = reader.GetNextEntry();
            if (entry is null)
            {
                break;
            }

            if (entry.EntryType is not TarEntryType.RegularFile and not TarEntryType.V7RegularFile)
            {
                continue;
            }

            var entryName = entry.Name.Replace('\\', '/').TrimStart('/');
            while (entryName.StartsWith("./", StringComparison.Ordinal))
            {
                entryName = entryName[2..];
            }

            if (!string.Equals(entryName, expectedEntryName, StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.DataStream is null)
            {
                break;
            }

            var destinationPath = Path.Combine(destinationDirectory, binaryName);
            await using var destinationStream = File.Create(destinationPath);
            await entry.DataStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new FileNotFoundException(
            $"The Copilot CLI binary '{expectedEntryName}' was not found in package '{archivePath}'.",
            archivePath);
    }

    private static string ResolveVersion(string? version)
    {
        var resolved = string.IsNullOrWhiteSpace(version)
            ? DefaultVersion
            : version.Trim();

        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException("The Copilot CLI version is not available. Ensure the GitHub.Copilot.SDK props were imported during build.");
        }

        return resolved;
    }

    private static async Task DownloadArchiveAsync(
        Uri uri,
        string destinationPath,
        string version,
        string platformName,
        IProgress<CopilotCliInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destinationStream = File.Create(destinationPath);

        var buffer = new byte[81920];
        long bytesDownloaded = 0;
        while (true)
        {
            var bytesRead = await responseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            bytesDownloaded += bytesRead;
            ReportProgress(
                progress,
                CopilotCliInstallStage.Downloading,
                version,
                platformName,
                BuildDownloadMessage(version, bytesDownloaded, totalBytes),
                bytesDownloaded,
                totalBytes);
        }
    }

    private static string NormalizeLocalRoot(string? localRootPath)
    {
        if (!string.IsNullOrWhiteSpace(localRootPath))
        {
            return Path.GetFullPath(localRootPath);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".alta",
            "cache");
    }

    private static string NormalizeNpmRegistryUrl(string? npmRegistryUrl)
    {
        var registryUrl = string.IsNullOrWhiteSpace(npmRegistryUrl)
            ? DefaultNpmRegistryUrl
            : npmRegistryUrl.Trim();

        if (!Uri.TryCreate(registryUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"Copilot npm registry URL '{registryUrl}' must be an absolute HTTP or HTTPS URL.",
                nameof(npmRegistryUrl));
        }

        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static CopilotCliPlatformKind GetCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return CopilotCliPlatformKind.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return CopilotCliPlatformKind.MacOS;
        }

        if (OperatingSystem.IsLinux())
        {
            return CopilotCliPlatformKind.Linux;
        }

        throw new PlatformNotSupportedException("Copilot CLI auto-install is only supported on Windows, macOS, and Linux.");
    }

    private static void EnsureExecutablePermissions(string executablePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var currentMode = File.GetUnixFileMode(executablePath);
        var requiredMode = currentMode |
                           UnixFileMode.UserRead |
                           UnixFileMode.UserWrite |
                           UnixFileMode.UserExecute |
                           UnixFileMode.GroupRead |
                           UnixFileMode.GroupExecute |
                           UnixFileMode.OtherRead |
                           UnixFileMode.OtherExecute;
        if (requiredMode != currentMode)
        {
            File.SetUnixFileMode(executablePath, requiredMode);
        }
    }

    private static string DescribePackage(CopilotCliPackage package)
    {
        var platform = package.Platform switch
        {
            CopilotCliPlatformKind.Windows => "Windows",
            CopilotCliPlatformKind.MacOS => "macOS",
            CopilotCliPlatformKind.Linux => "Linux",
            _ => package.Platform.ToString(),
        };
        return $"{platform} {package.Architecture}";
    }

    private static string BuildDownloadMessage(string version, long bytesDownloaded, long? totalBytes)
    {
        if (totalBytes is > 0)
        {
            var percent = (double)bytesDownloaded / totalBytes.Value * 100d;
            return $"Downloading Copilot CLI {version}... {FormatBytes(bytesDownloaded)} / {FormatBytes(totalBytes.Value)} ({percent:0.#}%)";
        }

        return $"Downloading Copilot CLI {version}... {FormatBytes(bytesDownloaded)}";
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        decimal display = value;
        var unitIndex = 0;
        while (display >= 1024 && unitIndex < units.Length - 1)
        {
            display /= 1024;
            unitIndex++;
        }

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{display:0.#} {units[unitIndex]}");
    }

    private static void ReportProgress(
        IProgress<CopilotCliInstallProgress>? progress,
        CopilotCliInstallStage stage,
        string version,
        string platformName,
        string message,
        long bytesDownloaded = 0,
        long? totalBytes = null)
    {
        progress?.Report(new CopilotCliInstallProgress(stage, version, platformName, message, bytesDownloaded, totalBytes));
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CodeAlta", "1.0"));
        return client;
    }
}
