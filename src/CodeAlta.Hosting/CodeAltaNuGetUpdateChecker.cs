using System.IO.Compression;
using System.Text.Json;
using NuGet.Versioning;

namespace CodeAlta.Hosting;

/// <summary>What nuget.org lists for a CodeAlta package, compared with the running version.</summary>
/// <param name="PackageId">The package that was looked up.</param>
/// <param name="CurrentVersion">The running version.</param>
/// <param name="LatestVersion">The newest listed version of the kind asked for; null when there is none.</param>
/// <param name="PackageFound">Whether the package is published at all.</param>
/// <param name="HasNewerVersion">Whether <paramref name="LatestVersion"/> is newer than the running version.</param>
/// <param name="IncludePrerelease">Whether prerelease versions were considered.</param>
public sealed record CodeAltaNuGetUpdateCheckResult(
    string PackageId,
    NuGetVersion CurrentVersion,
    NuGetVersion? LatestVersion,
    bool PackageFound,
    bool HasNewerVersion,
    bool IncludePrerelease)
{
    /// <summary>The running version in its normalized form.</summary>
    public string CurrentVersionText => CurrentVersion.ToNormalizedString();

    /// <summary>The newest listed version in its normalized form, or null.</summary>
    public string? LatestVersionText => LatestVersion?.ToNormalizedString();
}

/// <summary>
/// Looks up the published versions of a CodeAlta package on nuget.org. The terminal application and the
/// desktop share it: each checks its own package once at start and tells the user how to update.
/// </summary>
public static class CodeAltaNuGetUpdateChecker
{
    /// <summary>The project's page, where each release has its notes.</summary>
    public const string GitHubProjectUri = "https://github.com/CodeAlta/CodeAlta";

    private static readonly Uri NuGetRegistrationBaseUri = new("https://api.nuget.org/v3/registration5-gz-semver2/");

    /// <summary>The command that updates a globally installed tool to the newest version of that kind.</summary>
    /// <exception cref="ArgumentException"><paramref name="packageId"/> is blank.</exception>
    public static string UpdateCommand(string packageId, bool prerelease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        return prerelease ? $"dotnet tool update -g {packageId} --prerelease" : $"dotnet tool update -g {packageId}";
    }

    /// <summary>The address of a version's release notes; null without a version.</summary>
    public static string? ReleaseNotesUri(string? versionText)
        => string.IsNullOrWhiteSpace(versionText) ? null : $"{GitHubProjectUri}/releases/tag/{Uri.EscapeDataString(versionText.Trim())}";

    /// <summary>Compares the running version with what nuget.org lists, with a ten-second limit.</summary>
    /// <exception cref="ArgumentException"><paramref name="packageId"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="currentVersion"/> is null.</exception>
    /// <exception cref="HttpRequestException">nuget.org could not be read.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled, or the request timed out.</exception>
    public static async Task<CodeAltaNuGetUpdateCheckResult> CheckNuGetOrgAsync(
        string packageId,
        NuGetVersion currentVersion,
        bool includePrerelease = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(currentVersion);

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        return await CheckNuGetOrgAsync(packageId, currentVersion, includePrerelease, httpClient, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Compares the running version with what nuget.org lists, through a supplied client.</summary>
    /// <exception cref="ArgumentException"><paramref name="packageId"/> is blank.</exception>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="HttpRequestException">nuget.org could not be read.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled, or the request timed out.</exception>
    public static async Task<CodeAltaNuGetUpdateCheckResult> CheckNuGetOrgAsync(
        string packageId,
        NuGetVersion currentVersion,
        bool includePrerelease,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(httpClient);

        var listedVersions = await GetListedVersionsAsync(httpClient, packageId, cancellationToken).ConfigureAwait(false);
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

    private static async Task<NuGetVersion[]> GetListedVersionsAsync(HttpClient httpClient, string packageId, CancellationToken cancellationToken)
    {
        var registrationUri = new Uri(NuGetRegistrationBaseUri, $"{packageId.ToLowerInvariant()}/index.json");
        using var response = await httpClient.GetAsync(registrationUri, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        using var document = await ReadJsonDocumentAsync(response, cancellationToken).ConfigureAwait(false);

        var versions = new List<NuGetVersion>();
        await AddListedVersionsAsync(httpClient, document.RootElement, versions, cancellationToken).ConfigureAwait(false);
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

            using var response = await httpClient.GetAsync(pageUri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var pageDocument = await ReadJsonDocumentAsync(response, cancellationToken).ConfigureAwait(false);
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
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var decodedStream = CreateDecodedStream(stream, response.Content.Headers.ContentEncoding);
        return await JsonDocument.ParseAsync(decodedStream, cancellationToken: cancellationToken).ConfigureAwait(false);
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
