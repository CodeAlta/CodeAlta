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
    public const string Version = "2.1.328";

    /// <summary>The name of the file that marks a complete installation in its folder.</summary>
    public const string MarkerFileName = "claude-code.json";

    private const string ReleaseAddress = "https://github.com/anthropics/claude-code/releases/download/v" + Version + "/";

    private static readonly FrozenDictionary<string, ClaudeCodeCliAsset> AssetsByRuntime = new ClaudeCodeCliAsset[]
    {
        new("win-x64", "claude-win32-x64.zip", 172_890_679, "20d444ebbf729ce29693cfafb98131f207e83f16ed57a743913a5a9ee14c49cb"),
        new("win-arm64", "claude-win32-arm64.zip", 165_995_318, "b1a63ecc29b9236362a6ef2f25d29a531e992480a03c6ec9990a6831fe628ecc"),
        new("osx-x64", "claude-darwin-x64.tar.gz", 165_646_440, "620b53b98de2ddf4d96311b2e575fb529b202de88c8666e87db20ba945f80f76"),
        new("osx-arm64", "claude-darwin-arm64.tar.gz", 157_813_278, "04f981829d2749bac30a4fee1e533e2c8f826071926703d351fc1a30807b2655"),
        new("linux-x64", "claude-linux-x64.tar.gz", 170_350_304, "4404b3dc35afe7dde16c572197f69c210dffdb038f05ee3c532e683732f5aa9f"),
        new("linux-arm64", "claude-linux-arm64.tar.gz", 169_822_366, "e95be009ef9548aec0bd34fe2d2736e9b887aef7452b31a8cd3d00cfe0ed7382"),
        new("linux-musl-x64", "claude-linux-x64-musl.tar.gz", 162_693_090, "41543d09a326ff4a07fa2b0de551983d89f04adc0f7777e2af6374c8dd8f3761"),
        new("linux-musl-arm64", "claude-linux-arm64-musl.tar.gz", 161_885_329, "fa4d3dd5e05c8025e830b574a9269fbc5f832d155548df59f464a952ef9dfbca"),
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
