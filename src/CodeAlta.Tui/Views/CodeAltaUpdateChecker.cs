using System.Reflection;
using CodeAlta.Hosting;
using NuGet.Versioning;

namespace CodeAlta.Tui.Views;

/// <summary>Checks the terminal application's own package; the lookup itself is shared with the desktop.</summary>
internal static class CodeAltaUpdateChecker
{
    public const string PackageId = "CodeAlta.Tui";

    public static async Task<CodeAltaNuGetUpdateCheckResult> CheckCurrentAssemblyAsync(
        Assembly? assembly = null,
        bool? includePrerelease = null,
        CancellationToken cancellationToken = default)
    {
        var currentVersion = GetCurrentAssemblyNuGetVersion(assembly);
        var effectiveIncludePrerelease = includePrerelease ?? currentVersion.IsPrerelease;
        return await CheckNuGetOrgAsync(PackageId, currentVersion, effectiveIncludePrerelease, cancellationToken);
    }

    public static Task<CodeAltaNuGetUpdateCheckResult> CheckNuGetOrgAsync(
        string packageId,
        NuGetVersion currentVersion,
        bool includePrerelease = false,
        CancellationToken cancellationToken = default)
        => CodeAltaNuGetUpdateChecker.CheckNuGetOrgAsync(packageId, currentVersion, includePrerelease, cancellationToken);

    internal static Task<CodeAltaNuGetUpdateCheckResult> CheckNuGetOrgAsync(
        string packageId,
        NuGetVersion currentVersion,
        bool includePrerelease,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
        => CodeAltaNuGetUpdateChecker.CheckNuGetOrgAsync(packageId, currentVersion, includePrerelease, httpClient, cancellationToken);

    public static NuGetVersion GetCurrentAssemblyNuGetVersion(Assembly? assembly = null)
    {
        var versionInfo = CodeAltaApplicationInfo.GetVersionInfo(assembly);
        if (NuGetVersion.TryParse(versionInfo.InformationalVersion, out var parsed))
        {
            return parsed;
        }

        if (NuGetVersion.TryParse(versionInfo.PackageVersion, out parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Unable to determine the current CodeAlta NuGet version from '{versionInfo.InformationalVersion}'.");
    }
}
