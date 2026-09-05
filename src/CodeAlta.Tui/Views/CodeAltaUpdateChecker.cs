using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using NuGet.Versioning;

namespace CodeAlta.Tui.Views;

internal sealed record CodeAltaNuGetUpdateCheckResult(
    string PackageId,
    NuGetVersion CurrentVersion,
    NuGetVersion? LatestVersion,
    bool PackageFound,
    bool HasNewerVersion,
    bool IncludePrerelease)
{
    public string CurrentVersionText => CurrentVersion.ToNormalizedString();

    public string? LatestVersionText => LatestVersion?.ToNormalizedString();
}

internal static class CodeAltaUpdateChecker
{
    public const string PackageId = "CodeAlta.Tui";

    private static readonly Uri NuGetRegistrationBaseUri = new("https://api.nuget.org/v3/registration5-gz-semver2/");

    public static async Task<CodeAltaNuGetUpdateCheckResult> CheckCurrentAssemblyAsync(
        Assembly? assembly = null,
        bool? includePrerelease = null,
        CancellationToken cancellationToken = default)
    {
        var currentVersion = GetCurrentAssemblyNuGetVersion(assembly);
        var effectiveIncludePrerelease = includePrerelease ?? currentVersion.IsPrerelease;
        return await CheckNuGetOrgAsync(PackageId, currentVersion, effectiveIncludePrerelease, cancellationToken);
    }

    public static async Task<CodeAltaNuGetUpdateCheckResult> CheckNuGetOrgAsync(
        string packageId,
        NuGetVersion currentVersion,
        bool includePrerelease = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(currentVersion);

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        return await CheckNuGetOrgAsync(packageId, currentVersion, includePrerelease, httpClient, cancellationToken);
    }

    internal static async Task<CodeAltaNuGetUpdateCheckResult> CheckNuGetOrgAsync(
        string packageId,
        NuGetVersion currentVersion,
        bool includePrerelease,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(httpClient);

        var listedVersions = await GetListedVersionsAsync(httpClient, packageId, cancellationToken);
        if (listedVersions.Length == 0)
        {
            return new CodeAltaNuGetUpdateCheckResult(packageId, currentVersion, null, PackageFound: false, HasNewerVersion: false, includePrerelease);
        }

        var latestVersion = listedVersions
            .Where(version => includePrerelease || !version.IsPrerelease)
            .OrderByDescending(static version => version, VersionComparer.VersionRelease)
            .FirstOrDefault();
        if (latestVersion is null)
        {
            return new CodeAltaNuGetUpdateCheckResult(packageId, currentVersion, null, PackageFound: true, HasNewerVersion: false, includePrerelease);
        }

        var hasNewerVersion = VersionComparer.VersionRelease.Compare(latestVersion, currentVersion) > 0;
        return new CodeAltaNuGetUpdateCheckResult(packageId, currentVersion, latestVersion, PackageFound: true, hasNewerVersion, includePrerelease);
    }

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

    private static async Task<NuGetVersion[]> GetListedVersionsAsync(HttpClient httpClient, string packageId, CancellationToken cancellationToken)
    {
        var registrationUri = new Uri(NuGetRegistrationBaseUri, $"{packageId.ToLowerInvariant()}/index.json");
        using var response = await httpClient.GetAsync(registrationUri, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        using var document = await ReadJsonDocumentAsync(response, cancellationToken);

        var versions = new List<NuGetVersion>();
        await AddListedVersionsAsync(httpClient, document.RootElement, versions, cancellationToken);
        return versions.ToArray();
    }

    private static async Task AddListedVersionsAsync(HttpClient httpClient, JsonElement registrationPage, List<NuGetVersion> versions, CancellationToken cancellationToken)
    {
        if (!registrationPage.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("items", out var inlineItems) && inlineItems.ValueKind == JsonValueKind.Array)
            {
                AddListedVersions(inlineItems, versions);
                continue;
            }

            if (!item.TryGetProperty("@id", out var pageUriElement) || pageUriElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var pageUri = pageUriElement.GetString();
            if (string.IsNullOrWhiteSpace(pageUri))
            {
                continue;
            }

            using var response = await httpClient.GetAsync(pageUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var pageDocument = await ReadJsonDocumentAsync(response, cancellationToken);
            if (pageDocument.RootElement.TryGetProperty("items", out var pageItems) && pageItems.ValueKind == JsonValueKind.Array)
            {
                AddListedVersions(pageItems, versions);
            }
        }
    }

    private static void AddListedVersions(JsonElement items, List<NuGetVersion> versions)
    {
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("catalogEntry", out var catalogEntry) || catalogEntry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var listed = !catalogEntry.TryGetProperty("listed", out var listedElement) || listedElement.ValueKind != JsonValueKind.False;
            if (!listed || !catalogEntry.TryGetProperty("version", out var versionElement) || versionElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var versionText = versionElement.GetString();
            if (!string.IsNullOrWhiteSpace(versionText) && NuGetVersion.TryParse(versionText, out var version))
            {
                versions.Add(version);
            }
        }
    }

    private static async Task<JsonDocument> ReadJsonDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var decodedStream = CreateDecodedStream(stream, response.Content.Headers.ContentEncoding);
        return await JsonDocument.ParseAsync(decodedStream, cancellationToken: cancellationToken);
    }

    private static Stream CreateDecodedStream(Stream stream, ICollection<string> contentEncodings)
    {
        var decodedStream = stream;
        foreach (var contentEncoding in contentEncodings.Reverse())
        {
            decodedStream = contentEncoding switch
            {
                _ when contentEncoding.Equals("gzip", StringComparison.OrdinalIgnoreCase)
                    || contentEncoding.Equals("x-gzip", StringComparison.OrdinalIgnoreCase) => new GZipStream(decodedStream, CompressionMode.Decompress),
                _ when contentEncoding.Equals("deflate", StringComparison.OrdinalIgnoreCase) => new DeflateStream(decodedStream, CompressionMode.Decompress),
                _ when contentEncoding.Equals("br", StringComparison.OrdinalIgnoreCase) => new BrotliStream(decodedStream, CompressionMode.Decompress),
                _ when contentEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase) => decodedStream,
                _ => throw new InvalidOperationException($"Unsupported NuGet registration content encoding '{contentEncoding}'."),
            };
        }

        return decodedStream;
    }
}
