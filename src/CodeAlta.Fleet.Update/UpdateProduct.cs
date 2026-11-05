using System.Runtime.InteropServices;

namespace CodeAlta.Fleet.Update;

/// <summary>
/// An executable that updates itself: the .NET tool package it ships in (<c>CodeAlta.Node</c>, <c>CodeAlta.Server</c>)
/// and its entry assembly (<c>altanode</c>, <c>altaserver</c>). A tool package with runtimes has one package per
/// runtime (<c>CodeAlta.Node.win-x64</c>), whose <c>tools/&lt;framework&gt;/&lt;runtime&gt;/</c> folder is the application,
/// framework-dependent: that is what an update downloads, without the .NET SDK.
/// </summary>
/// <param name="PackageId">The id of the tool package.</param>
/// <param name="AssemblyName">The name of the entry assembly and of its app host, without extension.</param>
public sealed record UpdateProduct(string PackageId, string AssemblyName)
{
    /// <summary>CodeAlta Node.</summary>
    public static UpdateProduct Node { get; } = new("CodeAlta.Node", "altanode");

    /// <summary>CodeAlta Server.</summary>
    public static UpdateProduct Server { get; } = new("CodeAlta.Server", "altaserver");

    /// <summary>The runtimes the tool packages are published for (their <c>RuntimeIdentifiers</c>).</summary>
    public static IReadOnlyList<string> PublishedRuntimes { get; } =
        ["linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64", "win-x64", "win-arm64", "osx-arm64", "osx-x64"];

    /// <summary>Gets the id of the package of a runtime: <c>CodeAlta.Node.win-x64</c>.</summary>
    /// <param name="runtime">The runtime identifier.</param>
    /// <returns>The package id.</returns>
    public string RuntimePackageId(string runtime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);
        return PackageId + "." + runtime;
    }

    /// <summary>Gets the file name of the app host on an OS: <c>altanode.exe</c> on Windows, <c>altanode</c> elsewhere.</summary>
    /// <param name="windows">Whether the OS is Windows.</param>
    /// <returns>The file name.</returns>
    public string AppHostFileName(bool windows) => windows ? AssemblyName + ".exe" : AssemblyName;

    /// <summary>Gets the file name of the entry assembly: <c>altanode.dll</c>.</summary>
    public string AssemblyFileName => AssemblyName + ".dll";

    /// <summary>
    /// Gets the runtime of this process among those the packages are published for: the runtime .NET reports when it is
    /// one of them (a portable runtime), otherwise the OS and the architecture (a runtime built by a distribution).
    /// </summary>
    /// <returns>The runtime identifier, or null on an OS or an architecture no package is published for.</returns>
    public static string? CurrentRuntime()
    {
        if (PublishedRuntimes.Contains(RuntimeInformation.RuntimeIdentifier, StringComparer.Ordinal)) return RuntimeInformation.RuntimeIdentifier;
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (architecture is null) return null;
        if (OperatingSystem.IsWindows()) return "win-" + architecture;
        if (OperatingSystem.IsMacOS()) return "osx-" + architecture;
        if (!OperatingSystem.IsLinux()) return null;
        var musl = RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase) || File.Exists("/etc/alpine-release");
        return (musl ? "linux-musl-" : "linux-") + architecture;
    }
}
