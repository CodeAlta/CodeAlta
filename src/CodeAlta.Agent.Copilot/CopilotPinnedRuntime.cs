using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace CodeAlta.Agent.Copilot;

/// <summary>The package of the Copilot runtime of one platform in a release of the Copilot CLI on GitHub.</summary>
/// <param name="Platform">The platform the release names it with, such as <c>win32-x64</c> or <c>darwin-arm64</c>.</param>
/// <param name="Size">The size of the package, in bytes.</param>
/// <param name="Sha256">The SHA-256 of the package, in lowercase hexadecimal, as the <c>digest</c> of the asset gives it.</param>
public sealed record CopilotRuntimePackage(string Platform, long Size, string Sha256)
{
    /// <summary>Gets the name of the package in the release.</summary>
    public string FileName => $"github-copilot-{CopilotPinnedRuntime.Version}-{Platform}.tgz";
}

/// <summary>
/// The release of the Copilot runtime that the Copilot SDK of this build pins (<c>CopilotCliVersion</c> of
/// <c>GitHub.Copilot.SDK.props</c>). CodeAlta does not ship it: it downloads it the first time a Copilot provider
/// needs the managed settings of its account, into a cache shared by the normal and the developer instance.
/// </summary>
/// <remarks>
/// Bumping the SDK bumps this pin: the version of its props file, then the size and the <c>digest</c> of each
/// <c>github-copilot-&lt;version&gt;-&lt;platform&gt;.tgz</c> asset
/// (<c>gh release view v&lt;version&gt; -R github/copilot-cli --json assets</c>).
/// </remarks>
public static class CopilotPinnedRuntime
{
    /// <summary>The version of the Copilot runtime CodeAlta runs.</summary>
    public const string Version = "1.0.109";

    /// <summary>The name of the file that marks a complete installation in its folder.</summary>
    public const string MarkerFileName = "codealta-copilot-runtime.json";

    private const string ReleaseAddress = "https://github.com/github/copilot-cli/releases/download/v" + Version + "/";

    private static readonly FrozenDictionary<string, CopilotRuntimePackage> PackagesByPlatform = new CopilotRuntimePackage[]
    {
        new("darwin-arm64", 100_847_453, "0e315203a2a90f78dd11def8b44c02083518ac7a94276aa56ca3584a630ff083"),
        new("darwin-x64", 110_154_801, "622c642422124e362bd95c00dad4a27dcc2c18b3399ceee82af4745597ad4996"),
        new("linux-arm64", 108_301_533, "8cb2f4fd8e59401ca581de603c7886903940042c0ae5bb49cc568e24477078a8"),
        new("linux-x64", 104_126_196, "d08b7a68fc0ca1f3167e22794e04d6aecc44ddc9e70f4482ae3b3d28104af03a"),
        new("linuxmusl-arm64", 95_855_744, "942e7b5fc39aabddf3ea9e9b5ad7f63c2f117a3b18a1eb0cfe71d224fcdb34c0"),
        new("linuxmusl-x64", 99_694_658, "6c1a297b8baf282e4f536377acb60aa38c3cb26514601e49b1d9ad83862c023a"),
        new("win32-arm64", 102_256_939, "04b2821e5541f1bc6fca29365385921d3ea72c61f05210ebcb004a207a18aa48"),
        new("win32-x64", 96_624_417, "5ba9bfc3104d8e37462e9a5214a3cc88e6e2201b972981b6bdf447ee2f8f49bc"),
    }.ToFrozenDictionary(static package => package.Platform, StringComparer.Ordinal);

    /// <summary>Gets the packages of the pinned release, one per platform.</summary>
    public static IReadOnlyCollection<CopilotRuntimePackage> Packages => PackagesByPlatform.Values;

    /// <summary>Gets the name of the runtime's entry point on this system, beside which <c>runtime.node</c> lies.</summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "copilot-runtime.exe" : "copilot-runtime";

    /// <summary>Gets the package of a platform.</summary>
    /// <param name="platform">A platform such as <c>win32-x64</c>, or <see langword="null"/>.</param>
    /// <returns>The package, or <see langword="null"/> when the release has none for it.</returns>
    public static CopilotRuntimePackage? FindPackage(string? platform)
        => platform is not null && PackagesByPlatform.TryGetValue(platform, out var package) ? package : null;

    /// <summary>Gets the address a package of the pinned release is downloaded from.</summary>
    /// <param name="package">The package.</param>
    /// <returns>Its address in the GitHub release.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is null.</exception>
    public static Uri AddressOf(CopilotRuntimePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new Uri(ReleaseAddress + package.FileName);
    }

    /// <summary>
    /// Gets the platform of this computer: the architecture of the system, not of this process; on Linux, the musl
    /// build where the system's C library is musl.
    /// </summary>
    /// <returns>The platform, or <see langword="null"/> on a platform the release has no package for.</returns>
    public static string? CurrentPlatform()
    {
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (architecture is null) return null;
        if (OperatingSystem.IsWindows()) return "win32-" + architecture;
        if (OperatingSystem.IsMacOS()) return "darwin-" + architecture;
        if (OperatingSystem.IsLinux()) return (RuntimeInformation.RuntimeIdentifier.StartsWith("linux-musl", StringComparison.Ordinal) ? "linuxmusl-" : "linux-") + architecture;
        return null;
    }

    /// <summary>Gets the folder the pinned package of a platform is installed in.</summary>
    /// <param name="root">The folder of the Copilot runtime in the cache of CodeAlta.</param>
    /// <param name="platform">The platform.</param>
    /// <returns><c>&lt;root&gt;/&lt;version&gt;/&lt;platform&gt;</c>.</returns>
    /// <exception cref="ArgumentException">An argument is null or blank.</exception>
    public static string GetInstallDirectory(string root, string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        return Path.Combine(root, Version, platform);
    }
}
