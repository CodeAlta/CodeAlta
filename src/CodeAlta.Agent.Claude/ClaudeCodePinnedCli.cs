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
    public const string Version = "2.1.318";

    /// <summary>The name of the file that marks a complete installation in its folder.</summary>
    public const string MarkerFileName = "claude-code.json";

    private const string ReleaseAddress = "https://github.com/anthropics/claude-code/releases/download/v" + Version + "/";

    private static readonly FrozenDictionary<string, ClaudeCodeCliAsset> AssetsByRuntime = new ClaudeCodeCliAsset[]
    {
        new("win-x64", "claude-win32-x64.zip", 157_140_300, "09291c31ac37b9a905e0f0e76d4867b741589bb6b3ae9daa06943042ec72c619"),
        new("win-arm64", "claude-win32-arm64.zip", 150_529_105, "0ff65eff74358633abedb9d42608365ef18535001b38eadc05d4bd0fa5cf049c"),
        new("osx-x64", "claude-darwin-x64.tar.gz", 150_453_543, "ea3e05371b9e8760e5d366529db9c6a96d968f5d1a41ba42ad61687c9a3f8173"),
        new("osx-arm64", "claude-darwin-arm64.tar.gz", 142_991_302, "cd7fa988d60e7aef7ed453847650255138440b229d081496b8ed0ab75836603b"),
        new("linux-x64", "claude-linux-x64.tar.gz", 155_477_886, "e6d426ca0c123f61fad58a361da0329f2a93cd206bc87040ed8d9576dd9eb583"),
        new("linux-arm64", "claude-linux-arm64.tar.gz", 155_175_248, "0f4fb66a2af3bf02d114099727e4a7745f117b1e63d2f77c8aff165590197421"),
        new("linux-musl-x64", "claude-linux-x64-musl.tar.gz", 147_776_202, "6f6d85b6faf3f5ed2655e9aefd52289ca30647ed4503647c2bfd342e1a5431f9"),
        new("linux-musl-arm64", "claude-linux-arm64-musl.tar.gz", 147_025_238, "198e232519752b4f64a39252507cd693b16eb2107a36ecc4a2b78971d1ad36ef"),
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
