using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace CodeAlta.CodexAppServer;

/// <summary>The package of the Codex app-server of one platform in a release of Codex on GitHub.</summary>
/// <param name="Target">The target Codex names the platform with, such as <c>aarch64-apple-darwin</c>.</param>
/// <param name="Size">The size of the package, in bytes.</param>
/// <param name="Sha256">The SHA-256 of the package, in lowercase hexadecimal, as the <c>digest</c> of the asset gives it.</param>
public sealed record CodexAppServerPackage(string Target, long Size, string Sha256)
{
    /// <summary>Gets the name of the package in the release.</summary>
    public string FileName => $"codex-app-server-package-{Target}.tar.gz";
}

/// <summary>
/// The release of Codex this build of CodeAlta is tested with: the latest stable release when it was pinned, never a
/// pre-release. Its standalone <c>codex-app-server</c> package is downloaded the first time a provider needs it, into
/// a cache shared by the normal and the developer instance.
/// </summary>
/// <remarks>
/// Bumping the pin is a step of the release: the latest stable tag
/// (<c>gh release list -R openai/codex --exclude-pre-releases --limit 1</c>), the size and the <c>digest</c> of each
/// <c>codex-app-server-package-&lt;target&gt;.tar.gz</c> asset (<c>gh release view &lt;tag&gt; -R openai/codex --json assets</c>),
/// the protocol types generated again at that tag, then the live tests.
/// </remarks>
public static class CodexPinnedRelease
{
    /// <summary>The version of Codex CodeAlta runs.</summary>
    public const string Version = "0.170.0";

    /// <summary>The tag of the release in the Codex repository.</summary>
    public const string Tag = "rust-v" + Version;

    /// <summary>The name of the file that marks a complete installation in its folder.</summary>
    public const string MarkerFileName = "codealta-codex-app-server.json";

    /// <summary>The name of the file of the package that describes it.</summary>
    public const string PackageManifestFileName = "codex-package.json";

    private const string ReleaseAddress = "https://github.com/openai/codex/releases/download/" + Tag + "/";

    private static readonly FrozenDictionary<string, CodexAppServerPackage> PackagesByTarget = new CodexAppServerPackage[]
    {
        new("aarch64-apple-darwin", 139_243_476, "eb264af34296b0f42308c2bf8eaa6c5c2c8c7231aa861e9f4703ecfa897d8f3c"),
        new("x86_64-apple-darwin", 151_313_159, "902b7e507795dce2924fc9a056b8bd37443fdbfbab072a2c0cae109b6994be6a"),
        new("aarch64-unknown-linux-musl", 151_642_886, "19d8f3755806c0c466ae3cd342637d285ec42fdc7b72ecda6de1b33d9fc5fd18"),
        new("x86_64-unknown-linux-musl", 149_501_407, "ddbc8d1dde4fe25f515b5e3f2a2e5cdacc811af3728277aba8ef0c42fd4f21c1"),
        new("aarch64-pc-windows-msvc", 159_372_374, "a4f1075ac46a6a6df90df30afac20f0f7a6899d3c5fb9a5a0b7df3a7df9cd652"),
        new("x86_64-pc-windows-msvc", 172_034_993, "4d48f945cad5cf841846150e9d481aa64d2b5628d37d962a23ffd4038a568d00"),
    }.ToFrozenDictionary(static package => package.Target, StringComparer.Ordinal);

    /// <summary>Gets the packages of the pinned release, one per platform.</summary>
    public static IReadOnlyCollection<CodexAppServerPackage> Packages => PackagesByTarget.Values;

    /// <summary>Gets the package of a target.</summary>
    /// <param name="target">A target such as <c>x86_64-pc-windows-msvc</c>, or <see langword="null"/>.</param>
    /// <returns>The package, or <see langword="null"/> when Codex has none for it.</returns>
    public static CodexAppServerPackage? FindPackage(string? target)
        => target is not null && PackagesByTarget.TryGetValue(target, out var package) ? package : null;

    /// <summary>Gets the address a package of the pinned release is downloaded from.</summary>
    /// <param name="package">The package.</param>
    /// <returns>Its address in the GitHub release.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is null.</exception>
    public static Uri AddressOf(CodexAppServerPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new Uri(ReleaseAddress + package.FileName);
    }

    /// <summary>
    /// Gets the target of this computer: the architecture of the system, not of this process. Linux uses the static
    /// musl build, which runs on every distribution.
    /// </summary>
    /// <returns>The target, or <see langword="null"/> on a platform Codex is not built for.</returns>
    public static string? CurrentTarget()
    {
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            _ => null,
        };
        if (architecture is null)
        {
            return null;
        }

        if (OperatingSystem.IsWindows()) return architecture + "-pc-windows-msvc";
        if (OperatingSystem.IsMacOS()) return architecture + "-apple-darwin";
        if (OperatingSystem.IsLinux()) return architecture + "-unknown-linux-musl";
        return null;
    }

    /// <summary>Gets the folder the pinned package of a target is installed in.</summary>
    /// <param name="root">The folder of the Codex app-server in the cache of CodeAlta.</param>
    /// <param name="target">The target.</param>
    /// <returns><c>&lt;root&gt;/&lt;version&gt;/&lt;target&gt;</c>.</returns>
    /// <exception cref="ArgumentException">An argument is null or blank.</exception>
    public static string GetInstallDirectory(string root, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        return Path.Combine(root, Version, target);
    }

    /// <summary>Gets the name of the executable of the app-server on this system.</summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "codex-app-server.exe" : "codex-app-server";
}
