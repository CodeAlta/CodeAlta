using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
#if !NETFRAMEWORK
using System.Formats.Tar;
#endif

namespace CodeAlta.CodexSdk;

/// <summary>
/// Shared defaults for the pinned Codex CLI used by the SDK generator and runtime adapter.
/// </summary>
public static class CodexPinnedRelease
{
    /// <summary>
    /// Gets the pinned Codex release tag used by the SDK generator.
    /// </summary>
    public const string DefaultTag = "rust-v0.124.0";
}

/// <summary>
/// Describes installation progress while downloading or extracting a pinned Codex release.
/// </summary>
/// <param name="Stage">The current installation stage.</param>
/// <param name="Tag">The Codex release tag.</param>
/// <param name="AssetName">The selected platform asset name.</param>
/// <param name="Message">The human-readable progress message.</param>
/// <param name="BytesDownloaded">The number of bytes downloaded so far.</param>
/// <param name="TotalBytes">The total number of bytes to download when known.</param>
public sealed record CodexInstallProgress(
    CodexInstallStage Stage,
    string Tag,
    string AssetName,
    string Message,
    long BytesDownloaded = 0,
    long? TotalBytes = null);

/// <summary>
/// Represents the current stage of Codex installation.
/// </summary>
public enum CodexInstallStage
{
    Resolving,
    Downloading,
    Extracting,
    Ready,
}

internal enum CodexPlatform
{
    Windows,
    MacOS,
    Linux,
}

internal readonly record struct CodexReleaseAsset(
    string AssetName,
    string ExecutableName,
    CodexPlatform Platform,
    Architecture Architecture,
    bool IsMusl);

internal readonly record struct CodexResolvedInstallation(
    string Tag,
    string InstallDirectory,
    string ExecutablePath,
    string AssetName);

internal static class CodexReleaseInstaller
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InstallLocks = new(StringComparer.Ordinal);
    private static readonly HttpClient HttpClient = CreateHttpClient();

    internal static async Task<CodexResolvedInstallation> EnsureInstalledAsync(
        CodexProcessOptions? options,
        CancellationToken cancellationToken = default)
    {
        options ??= new CodexProcessOptions();

        var tag = string.IsNullOrWhiteSpace(options.ReleaseTag)
            ? CodexPinnedRelease.DefaultTag
            : options.ReleaseTag.Trim();
        var asset = ResolveAssetForCurrentRuntime();
        var installDirectory = GetInstallDirectory(options.LocalRootPath, tag);
        var executablePath = Path.Combine(installDirectory, asset.ExecutableName);

        if (File.Exists(executablePath))
        {
            EnsureExecutablePermissions(executablePath);
            ReportProgress(options.Progress, CodexInstallStage.Ready, tag, asset.AssetName, $"Using Codex {tag}.");
            return new CodexResolvedInstallation(tag, installDirectory, executablePath, asset.AssetName);
        }

        var installLock = InstallLocks.GetOrAdd(installDirectory, static _ => new SemaphoreSlim(1, 1));
        await installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(executablePath))
            {
                EnsureExecutablePermissions(executablePath);
                ReportProgress(options.Progress, CodexInstallStage.Ready, tag, asset.AssetName, $"Using Codex {tag}.");
                return new CodexResolvedInstallation(tag, installDirectory, executablePath, asset.AssetName);
            }

            ReportProgress(
                options.Progress,
                CodexInstallStage.Resolving,
                tag,
                asset.AssetName,
                $"Installing Codex {tag} for {DescribeAsset(asset)}...");

            if (Directory.Exists(installDirectory))
            {
                Directory.Delete(installDirectory, recursive: true);
            }

            Directory.CreateDirectory(installDirectory);

            var archivePath = Path.Combine(installDirectory, asset.AssetName);
            var downloadUri = BuildAssetUri(tag, asset.AssetName);
            await DownloadArchiveAsync(downloadUri, archivePath, tag, asset.AssetName, options.Progress, cancellationToken).ConfigureAwait(false);

            ReportProgress(
                options.Progress,
                CodexInstallStage.Extracting,
                tag,
                asset.AssetName,
                $"Extracting Codex {tag}...");

            ExtractArchive(archivePath, installDirectory);

            var extractedExecutablePath = ResolveExecutablePath(installDirectory, asset.ExecutableName);
            EnsureExecutablePermissions(extractedExecutablePath);

            try
            {
                File.Delete(archivePath);
            }
            catch
            {
                // Best effort cleanup; the installed executable is the authoritative artifact.
            }

            ReportProgress(options.Progress, CodexInstallStage.Ready, tag, asset.AssetName, $"Codex {tag} is ready.");
            return new CodexResolvedInstallation(tag, installDirectory, extractedExecutablePath, asset.AssetName);
        }
        finally
        {
            installLock.Release();
        }
    }

    internal static CodexReleaseAsset ResolveAssetForCurrentRuntime()
    {
        var platform = GetCurrentPlatform();
        var runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        return ResolveAsset(platform, RuntimeInformation.OSArchitecture, runtimeIdentifier);
    }

    internal static CodexReleaseAsset ResolveAsset(
        CodexPlatform platform,
        Architecture architecture,
        string runtimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);

        var archToken = architecture switch
        {
            Architecture.Arm64 => "aarch64",
            Architecture.X64 => "x86_64",
            _ => throw new PlatformNotSupportedException($"Unsupported Codex architecture '{architecture}'."),
        };

        var isMusl = platform == CodexPlatform.Linux &&
                     runtimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase);

        var suffix = platform switch
        {
            CodexPlatform.Windows => "pc-windows-msvc.exe.zip",
            CodexPlatform.MacOS => "apple-darwin.tar.gz",
            CodexPlatform.Linux when isMusl => "unknown-linux-musl.tar.gz",
            CodexPlatform.Linux => "unknown-linux-gnu.tar.gz",
            _ => throw new PlatformNotSupportedException($"Unsupported Codex platform '{platform}'."),
        };

        var assetName = $"codex-{archToken}-{suffix}";
        var executableName = platform == CodexPlatform.Windows
            ? assetName[..^".zip".Length]
            : assetName[..^".tar.gz".Length];
        return new CodexReleaseAsset(assetName, executableName, platform, architecture, isMusl);
    }

    internal static string GetInstallDirectory(string? localRootPath, string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        var localRoot = NormalizeLocalRoot(localRootPath);
        return Path.Combine(localRoot, "bin", "codex", tag);
    }

    internal static Uri BuildAssetUri(string tag, string assetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);

        return new Uri(
            $"https://github.com/openai/codex/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}",
            UriKind.Absolute);
    }

    private static async Task DownloadArchiveAsync(
        Uri uri,
        string destinationPath,
        string tag,
        string assetName,
        IProgress<CodexInstallProgress>? progress,
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
                CodexInstallStage.Downloading,
                tag,
                assetName,
                BuildDownloadMessage(tag, bytesDownloaded, totalBytes),
                bytesDownloaded,
                totalBytes);
        }
    }

    private static void ExtractArchive(string archivePath, string destinationDirectory)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destinationDirectory, overwriteFiles: true);
            return;
        }

        if (!archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Unsupported Codex archive format '{archivePath}'.");
        }

#if NETFRAMEWORK
        throw new NotSupportedException("tar.gz extraction is not supported on this target framework.");
#else
        using var archiveStream = File.OpenRead(archivePath);
        using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress, leaveOpen: false);
        TarFile.ExtractToDirectory(gzipStream, destinationDirectory, overwriteFiles: true);
#endif
    }

    private static string ResolveExecutablePath(string installDirectory, string executableName)
    {
        var directPath = Path.Combine(installDirectory, executableName);
        if (File.Exists(directPath))
        {
            return directPath;
        }

        var discovered = Directory
            .EnumerateFiles(installDirectory, executableName, SearchOption.AllDirectories)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(discovered))
        {
            return discovered;
        }

        throw new FileNotFoundException(
            $"The Codex executable '{executableName}' was not found after extraction.",
            directPath);
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

    private static CodexPlatform GetCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return CodexPlatform.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return CodexPlatform.MacOS;
        }

        if (OperatingSystem.IsLinux())
        {
            return CodexPlatform.Linux;
        }

        throw new PlatformNotSupportedException("Codex auto-install is only supported on Windows, macOS, and Linux.");
    }

    private static string DescribeAsset(CodexReleaseAsset asset)
    {
        var platform = asset.Platform switch
        {
            CodexPlatform.Windows => "Windows",
            CodexPlatform.MacOS => "macOS",
            CodexPlatform.Linux when asset.IsMusl => "Linux musl",
            CodexPlatform.Linux => "Linux gnu",
            _ => asset.Platform.ToString(),
        };
        return $"{platform} {asset.Architecture}";
    }

    private static string BuildDownloadMessage(string tag, long bytesDownloaded, long? totalBytes)
    {
        if (totalBytes is > 0)
        {
            var percent = (double)bytesDownloaded / totalBytes.Value * 100d;
            return $"Downloading Codex {tag}... {FormatBytes(bytesDownloaded)} / {FormatBytes(totalBytes.Value)} ({percent:0.#}%)";
        }

        return $"Downloading Codex {tag}... {FormatBytes(bytesDownloaded)}";
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = value;
        var unitIndex = 0;
        decimal display = size;
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
        IProgress<CodexInstallProgress>? progress,
        CodexInstallStage stage,
        string tag,
        string assetName,
        string message,
        long bytesDownloaded = 0,
        long? totalBytes = null)
    {
        progress?.Report(new CodexInstallProgress(stage, tag, assetName, message, bytesDownloaded, totalBytes));
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CodeAlta", "1.0"));
        return client;
    }
}
