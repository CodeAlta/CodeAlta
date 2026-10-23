using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// One archive of a release of Claude Code on GitHub: the CLI of one platform.
/// </summary>
/// <param name="RuntimeIdentifier">The .NET runtime identifier of the platform, such as <c>win-x64</c> or <c>linux-musl-arm64</c>.</param>
/// <param name="FileName">The name of the archive in the release, such as <c>claude-win32-x64.zip</c>.</param>
/// <param name="Size">The size of the archive, in bytes.</param>
/// <param name="Sha256">The SHA-256 of the archive, in lowercase hexadecimal, as <c>SHASUMS256.txt</c> of the release lists it.</param>
public sealed record ClaudeCodeCliAsset(string RuntimeIdentifier, string FileName, long Size, string Sha256)
{
    /// <summary>Gets the name of the executable in the archive: <c>claude.exe</c> on Windows, else <c>claude</c>.</summary>
    public string ExecutableName => RuntimeIdentifier.StartsWith("win-", StringComparison.Ordinal) ? "claude.exe" : "claude";

    /// <summary>Gets whether the archive is a zip file; the others are gzip-compressed tar files.</summary>
    public bool IsZip => FileName.EndsWith(".zip", StringComparison.Ordinal);
}

/// <summary>
/// The version of the Claude Code CLI this build of CodeAlta is tested with, and its archives. CodeAlta proposes to
/// download it on first use, into a cache shared by the normal and the developer instance, and runs it before a CLI
/// the user installed: a newer CLI may change its JSON input and output.
/// </summary>
/// <remarks>
/// Bumping the pin is a step of the release: the version, then the size and the SHA-256 of each archive from the
/// assets of the release (<c>gh release view v&lt;version&gt; --repo anthropics/claude-code --json assets</c>), then the
/// live CLI tests. Each archive holds the executable alone, at its root.
/// </remarks>
public static class ClaudeCodePinnedCli
{
    /// <summary>The version of Claude Code CodeAlta runs.</summary>
    public const string Version = "2.1.309";

    /// <summary>The name of the file that marks a complete installation in its folder.</summary>
    public const string MarkerFileName = "claude-code.json";

    private const string ReleaseAddress = "https://github.com/anthropics/claude-code/releases/download/v" + Version + "/";

    private static readonly FrozenDictionary<string, ClaudeCodeCliAsset> AssetsByRuntime = new ClaudeCodeCliAsset[]
    {
        new("win-x64", "claude-win32-x64.zip", 134_579_782, "ec8a68c3dc4579b04949d02116ea9968b7d1e65ebe4fc3f67c35cf1aa5eb205c"),
        new("win-arm64", "claude-win32-arm64.zip", 128_942_141, "0b17cf289cbfcce4bd2c95652f09600bedd547d1f444df449fdd0826e3a22668"),
        new("osx-x64", "claude-darwin-x64.tar.gz", 126_403_334, "12a9e671ac54b4d3fdb5a8e5ff94a94c5d2f08eb5fa481c2ccc3da5446a84657"),
        new("osx-arm64", "claude-darwin-arm64.tar.gz", 120_575_700, "09a74f9e0493ebea7e8867565ae04c8989575e6ef1d0a84e3b392c8065284395"),
        new("linux-x64", "claude-linux-x64.tar.gz", 131_003_256, "6259b381b9501b0b2b5d3ce4c068595bf32a59f1ca131ed69a1b1ae25b891cad"),
        new("linux-arm64", "claude-linux-arm64.tar.gz", 130_709_321, "8ecd1f2d06ccbd3f89c8886e840e42b57a19063f3de9cfe5f7d90fe875ba99ef"),
        new("linux-musl-x64", "claude-linux-x64-musl.tar.gz", 128_752_353, "51a0657730ec280a43fee47650fe1c412b07ec425ce496f133bac4bcb81d5e05"),
        new("linux-musl-arm64", "claude-linux-arm64-musl.tar.gz", 128_208_448, "839abf16c093427670f9f33a196afdb41d520883240809db531c8928a84389bd"),
    }.ToFrozenDictionary(static asset => asset.RuntimeIdentifier, StringComparer.Ordinal);

    /// <summary>Gets the archives of the pinned version, one per platform.</summary>
    public static IReadOnlyCollection<ClaudeCodeCliAsset> Assets => AssetsByRuntime.Values;

    /// <summary>Gets the archive of a platform.</summary>
    /// <param name="runtimeIdentifier">A runtime identifier such as <c>win-x64</c>.</param>
    /// <returns>The archive, or null when Claude Code has none for the platform.</returns>
    public static ClaudeCodeCliAsset? FindAsset(string? runtimeIdentifier)
        => runtimeIdentifier is not null && AssetsByRuntime.TryGetValue(runtimeIdentifier, out var asset) ? asset : null;

    /// <summary>Gets the address an archive of the pinned version is downloaded from.</summary>
    /// <param name="asset">The archive.</param>
    /// <returns>Its address in the GitHub release.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="asset"/> is null.</exception>
    public static Uri AddressOf(ClaudeCodeCliAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return new Uri(ReleaseAddress + asset.FileName);
    }

    /// <summary>
    /// Gets the platform of this computer, as the archives name it: the architecture of the system, not of this
    /// process, and the C library of a Linux system.
    /// </summary>
    /// <returns>The runtime identifier, or null on a platform Claude Code is not built for.</returns>
    public static string? CurrentRuntimeIdentifier()
    {
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (architecture is null)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return "win-" + architecture;
        }

        if (OperatingSystem.IsMacOS())
        {
            return "osx-" + architecture;
        }

        if (OperatingSystem.IsLinux())
        {
            return (IsMusl() ? "linux-musl-" : "linux-") + architecture;
        }

        return null;
    }

    /// <summary>Gets the folder the pinned version of a platform is installed in.</summary>
    /// <param name="root">The folder of Claude Code in the cache of CodeAlta.</param>
    /// <param name="runtimeIdentifier">The platform.</param>
    /// <returns><c>&lt;root&gt;/&lt;version&gt;/&lt;runtime identifier&gt;</c>.</returns>
    /// <exception cref="ArgumentException">An argument is null or blank.</exception>
    public static string GetInstallDirectory(string root, string runtimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        return Path.Combine(root, Version, runtimeIdentifier);
    }

    /// <summary>Gets the pinned executable of this computer when it was downloaded.</summary>
    /// <param name="root">The folder of Claude Code in the cache of CodeAlta, or null.</param>
    /// <returns>The full path of the executable, or null when it is not in the cache.</returns>
    public static string? FindInstalled(string? root) => FindInstalled(root, CurrentRuntimeIdentifier(), File.Exists);

    /// <summary>Gets the pinned executable of a platform when it was downloaded.</summary>
    /// <param name="root">The folder of Claude Code in the cache of CodeAlta, or null.</param>
    /// <param name="runtimeIdentifier">The platform, or null.</param>
    /// <param name="fileExists">Tells whether a file exists.</param>
    /// <returns>The full path of the executable, or null when it is not in the cache.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileExists"/> is null.</exception>
    public static string? FindInstalled(string? root, string? runtimeIdentifier, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (string.IsNullOrWhiteSpace(root) || FindAsset(runtimeIdentifier) is not { } asset)
        {
            return null;
        }

        // The folder gets its name once its marker is written: a folder without it is not an installation.
        var directory = GetInstallDirectory(root, asset.RuntimeIdentifier);
        var executable = Path.Combine(directory, asset.ExecutableName);
        return fileExists(Path.Combine(directory, MarkerFileName)) && fileExists(executable) ? Path.GetFullPath(executable) : null;
    }

    private static bool IsMusl()
    {
        if (RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.Ordinal))
        {
            return true;
        }

        // A runtime built for glibc can still run on a musl system through a compatibility layer.
        try
        {
            return Directory.Exists("/lib") && Directory.EnumerateFiles("/lib", "ld-musl-*.so.1").Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
