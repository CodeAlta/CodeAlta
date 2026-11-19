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
    public const string Version = "1.0.112";

    /// <summary>The name of the file that marks a complete installation in its folder.</summary>
    public const string MarkerFileName = "codealta-copilot-runtime.json";

    private const string ReleaseAddress = "https://github.com/github/copilot-cli/releases/download/v" + Version + "/";

    private static readonly FrozenDictionary<string, CopilotRuntimePackage> PackagesByPlatform = new CopilotRuntimePackage[]
    {
        new("darwin-arm64", 104_182_544, "3c924fb18d6b309adc21b592ff32c55916d43448df4107d37e5a9ad0daf9478f"),
        new("darwin-x64", 113_830_960, "b04105686981bf339dcc3bd8b5e44b83f6b00a36fa588fdd16581902cc2ccd5b"),
        new("linux-arm64", 113_072_638, "435dcf9907d4ba7aa5b52f5bb274d16b7c8d2143cff75226616cab39d017c7da"),
        new("linux-x64", 109_157_659, "c9dcab9dd34dd53663cee004e628a799539155850fcd4db6aa94e2615b0ce157"),
        new("linuxmusl-arm64", 100_642_789, "e3b07d912f2260d0699019b506361f3c2828bbb08c035a4c0acddb0fc9f64e92"),
        new("linuxmusl-x64", 104_804_518, "8fcd3568e0fde9d86eb704c9b2581f83479ddbf997c039a47d648ecb65af83d9"),
        new("win32-arm64", 105_555_544, "39b3a1c0b74dc7676b707eff0956aa88bec3517dd32c2045cfc9706442ecf4e5"),
        new("win32-x64", 100_918_446, "a60f1c1a2d831093f654cf22b56e1bf3ce4f2c4f00afeca699b4824c90abf40b"),
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
