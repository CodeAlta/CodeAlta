using System.IO.Compression;
using System.Net;
using System.Text.Json;

namespace CodeAlta.Fleet.Update;

/// <summary>A version a feed has of a package.</summary>
/// <param name="Version">The version.</param>
/// <param name="Listed">Whether the feed lists it (an unlisted version is installed only when asked by name).</param>
public sealed record FeedVersion(UpdateVersion Version, bool Listed);

/// <summary>What a download brought: the hash the feed publishes for the package, if it publishes one.</summary>
/// <param name="FeedHash">The SHA-512 the feed gives (the <c>packageHash</c> of its catalog, the <c>.sha512</c> beside a file); null when it gives none.</param>
public sealed record FeedDownload(PackageHash? FeedHash);

/// <summary>A source of packages: a NuGet v3 feed (nuget.org, a mirror) or a folder of <c>.nupkg</c> files.</summary>
public interface IUpdateFeed
{
    /// <summary>Gets the source as it was given, normalized (<see cref="UpdateFeeds.Normalize"/>).</summary>
    string Source { get; }

    /// <summary>Gets whether the source is a folder of this machine.</summary>
    bool IsLocal { get; }

    /// <summary>Lists the versions of a package.</summary>
    /// <param name="packageId">The package id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The versions; empty when the feed does not have the package.</returns>
    /// <exception cref="UpdateException">The feed did not answer, or answered what is not a feed.</exception>
    Task<IReadOnlyList<FeedVersion>> ListVersionsAsync(string packageId, CancellationToken cancellationToken);

    /// <summary>Downloads a package to a file.</summary>
    /// <param name="packageId">The package id.</param>
    /// <param name="version">Its version.</param>
    /// <param name="destinationPath">The file to write.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The hash the feed gives for it, if any.</returns>
    /// <exception cref="UpdateException">The feed does not have the package, or the download failed.</exception>
    Task<FeedDownload> DownloadAsync(string packageId, UpdateVersion version, string destinationPath, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the SHA-512 of a package without installing it: the one the feed publishes (the <c>packageHash</c> of its
    /// catalog, the <c>.sha512</c> beside a file), or the hash of the package downloaded when the feed publishes none.
    /// What a controller device signs in the manifest of a campaign.
    /// </summary>
    /// <param name="packageId">The package id.</param>
    /// <param name="version">Its version.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The hash, or null when the feed does not have the package.</returns>
    /// <exception cref="UpdateException">The feed did not answer, or its hash is not a SHA-512.</exception>
    Task<PackageHash?> GetHashAsync(string packageId, UpdateVersion version, CancellationToken cancellationToken);
}

/// <summary>Opens the feeds of the updates.</summary>
public static class UpdateFeeds
{
    /// <summary>The feed of the published packages: nuget.org.</summary>
    public const string NuGetOrg = "https://api.nuget.org/v3/index.json";

    /// <summary>The largest package a feed may send, in bytes: a runtime package of the node is a few dozen MB.</summary>
    public const long MaxPackageBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Normalizes a source, so that two spellings of it compare equal: an <c>https://</c> address as written without a
    /// trailing slash, a folder as its full path without a trailing separator.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <returns>The normalized source.</returns>
    /// <exception cref="UpdateException">The source is neither an address nor an absolute folder.</exception>
    public static string Normalize(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var text = source.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return uri.GetLeftPart(UriPartial.Query).TrimEnd('/');
        if (Uri.TryCreate(text, UriKind.Absolute, out uri) && uri.IsFile) text = uri.LocalPath;
        if (!Path.IsPathFullyQualified(text))
            throw new UpdateException(UpdateErrorCodes.InvalidSource, $"'{source}' is not a feed: give an https:// address of a NuGet v3 feed or the absolute path of a folder.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(text));
    }

    /// <summary>Gets whether two sources are the same once normalized (paths without case on Windows and macOS).</summary>
    /// <param name="left">A normalized source.</param>
    /// <param name="right">Another.</param>
    /// <returns>Whether they are the same.</returns>
    public static bool SameSource(string left, string right)
    {
        var url = left.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        var ignoreCase = url || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        return string.Equals(left, right, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Opens a feed.</summary>
    /// <param name="source">An <c>https://</c> address of a NuGet v3 service index (<c>http://</c> on the loopback only), or a folder.</param>
    /// <param name="http">The client of an HTTP feed.</param>
    /// <returns>The feed.</returns>
    /// <exception cref="UpdateException">The source is not valid, or the folder does not exist.</exception>
    public static IUpdateFeed Open(string source, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var normalized = Normalize(source);
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            // Without TLS, a package and its hash could both be changed on the way; the loopback is the machine itself.
            if (uri.Scheme == "http" && !uri.IsLoopback)
                throw new UpdateException(UpdateErrorCodes.InvalidSource, $"'{source}' is plain HTTP: a feed of another machine needs https://.");
            return new NuGetHttpFeed(normalized, uri, http);
        }

        if (!Directory.Exists(normalized)) throw new UpdateException(UpdateErrorCodes.InvalidSource, $"The folder '{normalized}' does not exist.");
        return new LocalFolderFeed(normalized);
    }
}

/// <summary>
/// A folder of packages, flat (<c>CodeAlta.Node.win-x64.4.0.1.nupkg</c>, as <c>dotnet pack -o</c> writes them) or
/// hierarchical (<c>codealta.node.win-x64/4.0.1/codealta.node.win-x64.4.0.1.nupkg</c> with its <c>.sha512</c>, as
/// <c>nuget add</c> writes them).
/// </summary>
internal sealed class LocalFolderFeed(string root) : IUpdateFeed
{
    public string Source => root;

    public bool IsLocal => true;

    public Task<IReadOnlyList<FeedVersion>> ListVersionsAsync(string packageId, CancellationToken cancellationToken)
    {
        var versions = Find(packageId).Select(static found => new FeedVersion(found.Version, true)).ToArray();
        return Task.FromResult<IReadOnlyList<FeedVersion>>(versions);
    }

    public async Task<FeedDownload> DownloadAsync(string packageId, UpdateVersion version, string destinationPath, CancellationToken cancellationToken)
    {
        var file = Find(packageId).FirstOrDefault(found => found.Version.Equals(version)).Path
            ?? throw new UpdateException(UpdateErrorCodes.NotFound, $"The folder {root} has no {packageId} {version}.");
        var length = new FileInfo(file).Length;
        if (length > UpdateFeeds.MaxPackageBytes) throw new UpdateException(UpdateErrorCodes.DownloadFailed, $"{file} is larger than {UpdateFeeds.MaxPackageBytes / (1024 * 1024)} MB.");
        await using (var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        await using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        PackageHash? hash = null;
        var hashFile = file + ".sha512";
        if (File.Exists(hashFile) && !PackageHash.TryParse(await File.ReadAllTextAsync(hashFile, cancellationToken).ConfigureAwait(false), out hash))
            throw new UpdateException(UpdateErrorCodes.HashMismatch, $"{hashFile} is not a SHA-512 hash.");
        return new FeedDownload(hash);
    }

    public async Task<PackageHash?> GetHashAsync(string packageId, UpdateVersion version, CancellationToken cancellationToken)
    {
        var file = Find(packageId).FirstOrDefault(found => found.Version.Equals(version)).Path;
        if (file is null) return null;
        var hashFile = file + ".sha512";
        if (File.Exists(hashFile))
        {
            return PackageHash.TryParse(await File.ReadAllTextAsync(hashFile, cancellationToken).ConfigureAwait(false), out var published)
                ? published
                : throw new UpdateException(UpdateErrorCodes.HashMismatch, $"{hashFile} is not a SHA-512 hash.");
        }

        return await PackageHash.ComputeAsync(file, cancellationToken).ConfigureAwait(false);
    }

    private IEnumerable<(UpdateVersion Version, string Path)> Find(string packageId)
    {
        var prefix = packageId + ".";
        foreach (var file in Directory.EnumerateFiles(root, "*.nupkg"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && UpdateVersion.TryParse(name[prefix.Length..], out var version)) yield return (version, file);
        }

        var folder = Path.Combine(root, packageId.ToLowerInvariant());
        if (!Directory.Exists(folder)) yield break;
        foreach (var versionFolder in Directory.EnumerateDirectories(folder))
        {
            if (!UpdateVersion.TryParse(Path.GetFileName(versionFolder), out var version)) continue;
            var file = Path.Combine(versionFolder, $"{packageId.ToLowerInvariant()}.{version}.nupkg");
            if (File.Exists(file)) yield return (version, file);
        }
    }
}

/// <summary>
/// A NuGet v3 feed: its service index names the flat container (<c>PackageBaseAddress/3.0.0</c>), which lists the
/// versions and serves the packages, and the registrations, whose leaves say whether a version is listed and point to
/// the catalog entry that holds its <c>packageHash</c>. A feed without registrations lists every version and gives no
/// hash; the caller's expected hash then checks the package.
/// </summary>
internal sealed class NuGetHttpFeed(string source, Uri serviceIndex, HttpClient http) : IUpdateFeed
{
    private (Uri? PackageBase, Uri? Registrations)? _resources;

    public string Source => source;

    public bool IsLocal => false;

    public async Task<IReadOnlyList<FeedVersion>> ListVersionsAsync(string packageId, CancellationToken cancellationToken)
    {
        var (packageBase, registrations) = await ResourcesAsync(cancellationToken).ConfigureAwait(false);
        var id = packageId.ToLowerInvariant();
        if (registrations is not null)
        {
            using var index = await GetJsonAsync(new Uri(registrations, $"{id}/index.json"), allowMissing: true, cancellationToken).ConfigureAwait(false);
            if (index is null) return [];
            var versions = new List<FeedVersion>();
            foreach (var page in Items(index.RootElement))
            {
                if (page.TryGetProperty("items", out _))
                {
                    AddLeaves(page, versions);
                    continue;
                }

                // A large registration keeps its pages apart: each is read from its own address.
                if (!page.TryGetProperty("@id", out var pageId) || !Uri.TryCreate(pageId.GetString(), UriKind.Absolute, out var pageUri)) continue;
                using var pageDocument = await GetJsonAsync(pageUri, allowMissing: false, cancellationToken).ConfigureAwait(false);
                AddLeaves(pageDocument!.RootElement, versions);
            }

            return versions;
        }

        using var flat = await GetJsonAsync(new Uri(packageBase!, $"{id}/index.json"), allowMissing: true, cancellationToken).ConfigureAwait(false);
        if (flat is null) return [];
        return [.. flat.RootElement.TryGetProperty("versions", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(static item => UpdateVersion.TryParse(item.GetString(), out var version) ? new FeedVersion(version, true) : null).OfType<FeedVersion>()
            : []];
    }

    public async Task<FeedDownload> DownloadAsync(string packageId, UpdateVersion version, string destinationPath, CancellationToken cancellationToken)
    {
        var (packageBase, registrations) = await ResourcesAsync(cancellationToken).ConfigureAwait(false);
        var id = packageId.ToLowerInvariant();
        var address = new Uri(packageBase!, $"{id}/{version}/{id}.{version}.nupkg");
        using (var response = await SendAsync(address, cancellationToken).ConfigureAwait(false))
        {
            if (response.StatusCode == HttpStatusCode.NotFound) throw new UpdateException(UpdateErrorCodes.NotFound, $"{source} has no {packageId} {version}.");
            EnsureSuccess(response, address);
            if (response.Content.Headers.ContentLength > UpdateFeeds.MaxPackageBytes) throw TooLarge(address);
            await using var content = Decode(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response, address);
            await using var file = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            try
            {
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > UpdateFeeds.MaxPackageBytes) throw TooLarge(address);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (InvalidDataException exception)
            {
                throw new UpdateException(UpdateErrorCodes.DownloadFailed, $"{address} sent a compressed answer that cannot be read: {exception.Message}", exception);
            }
        }

        return new FeedDownload(registrations is null ? null : await ReadHashAsync(registrations, id, version, cancellationToken).ConfigureAwait(false));
    }

    public async Task<PackageHash?> GetHashAsync(string packageId, UpdateVersion version, CancellationToken cancellationToken)
    {
        var (_, registrations) = await ResourcesAsync(cancellationToken).ConfigureAwait(false);
        var id = packageId.ToLowerInvariant();
        if (registrations is not null && await ReadHashAsync(registrations, id, version, cancellationToken).ConfigureAwait(false) is { } published) return published;
        // A feed without a catalog: the package itself, downloaded to a temporary file and hashed.
        var temporary = Path.Combine(Path.GetTempPath(), "codealta-update-" + Guid.NewGuid().ToString("N") + ".nupkg");
        try
        {
            try
            {
                await DownloadAsync(packageId, version, temporary, cancellationToken).ConfigureAwait(false);
            }
            catch (UpdateException exception) when (exception.Code == UpdateErrorCodes.NotFound)
            {
                return null;
            }

            return await PackageHash.ComputeAsync(temporary, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    // The packageHash of the catalog entry the registration leaf points to; null when the feed has no catalog.
    private async Task<PackageHash?> ReadHashAsync(Uri registrations, string id, UpdateVersion version, CancellationToken cancellationToken)
    {
        using var leaf = await GetJsonAsync(new Uri(registrations, $"{id}/{version}.json"), allowMissing: true, cancellationToken).ConfigureAwait(false);
        if (leaf is null || !leaf.RootElement.TryGetProperty("catalogEntry", out var entry)) return null;
        JsonDocument? catalog = null;
        try
        {
            var element = entry;
            if (entry.ValueKind == JsonValueKind.String)
            {
                if (!Uri.TryCreate(entry.GetString(), UriKind.Absolute, out var catalogUri)) return null;
                catalog = await GetJsonAsync(catalogUri, allowMissing: true, cancellationToken).ConfigureAwait(false);
                if (catalog is null) return null;
                element = catalog.RootElement;
            }

            if (!element.TryGetProperty("packageHash", out var hash) || hash.ValueKind != JsonValueKind.String) return null;
            if (element.TryGetProperty("packageHashAlgorithm", out var algorithm) && !string.Equals(algorithm.GetString(), "SHA512", StringComparison.OrdinalIgnoreCase)) return null;
            return PackageHash.TryParse(hash.GetString(), out var parsed)
                ? parsed
                : throw new UpdateException(UpdateErrorCodes.HashMismatch, $"{source} gives a packageHash for {id} {version} that is not a SHA-512.");
        }
        finally
        {
            catalog?.Dispose();
        }
    }

    private static void AddLeaves(JsonElement page, List<FeedVersion> versions)
    {
        foreach (var leaf in Items(page))
        {
            if (!leaf.TryGetProperty("catalogEntry", out var entry) || entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("version", out var text) || !UpdateVersion.TryParse(text.GetString(), out var version)) continue;
            var listed = !entry.TryGetProperty("listed", out var flag) || flag.ValueKind != JsonValueKind.False;
            versions.Add(new FeedVersion(version, listed));
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement element)
        => element.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];

    private async Task<(Uri? PackageBase, Uri? Registrations)> ResourcesAsync(CancellationToken cancellationToken)
    {
        if (_resources is { } known) return known;
        using var index = await GetJsonAsync(serviceIndex, allowMissing: false, cancellationToken).ConfigureAwait(false);
        Uri? packageBase = null;
        Uri? registrations = null;
        var registrationRank = int.MaxValue;
        string[] registrationTypes = ["RegistrationsBaseUrl/3.6.0", "RegistrationsBaseUrl/3.4.0", "RegistrationsBaseUrl/3.0.0-rc", "RegistrationsBaseUrl"];
        foreach (var resource in index!.RootElement.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array ? resources.EnumerateArray() : [])
        {
            if (!resource.TryGetProperty("@type", out var typeElement) || !resource.TryGetProperty("@id", out var idElement)) continue;
            if (!Uri.TryCreate(idElement.GetString()?.TrimEnd('/') + "/", UriKind.Absolute, out var address)) continue;
            var type = typeElement.GetString();
            if (type == "PackageBaseAddress/3.0.0") packageBase ??= address;
            var rank = Array.IndexOf(registrationTypes, type);
            if (rank >= 0 && rank < registrationRank)
            {
                registrations = address;
                registrationRank = rank;
            }
        }

        if (packageBase is null) throw new UpdateException(UpdateErrorCodes.InvalidSource, $"{source} is not a NuGet v3 feed: its service index has no PackageBaseAddress/3.0.0.");
        _resources = (packageBase, registrations);
        return (packageBase, registrations);
    }

    private async Task<JsonDocument?> GetJsonAsync(Uri address, bool allowMissing, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(address, cancellationToken).ConfigureAwait(false);
        if (allowMissing && response.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureSuccess(response, address);
        try
        {
            await using var stream = Decode(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response, address);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new UpdateException(UpdateErrorCodes.FeedUnavailable, $"{address} did not answer JSON: {exception.Message}", exception);
        }
    }

    // The content as sent, decoded: nuget.org answers its registrations (RegistrationsBaseUrl/3.6.0) in gzip whatever the
    // request asks, and the client given may not decompress (an HttpClient does not by default). A handler that does
    // removes the header, so nothing is decoded twice.
    private static Stream Decode(Stream content, HttpResponseMessage response, Uri address)
    {
        var decoded = content;
        foreach (var encoding in response.Content.Headers.ContentEncoding.Reverse())
        {
            decoded = encoding.ToLowerInvariant() switch
            {
                "gzip" or "x-gzip" => new GZipStream(decoded, CompressionMode.Decompress),
                "deflate" => new ZLibStream(decoded, CompressionMode.Decompress),
                "br" => new BrotliStream(decoded, CompressionMode.Decompress),
                "identity" => decoded,
                _ => throw new UpdateException(UpdateErrorCodes.FeedUnavailable, $"{address} answered in an encoding this update cannot read: '{encoding}'."),
            };
        }

        return decoded;
    }

    private async Task<HttpResponseMessage> SendAsync(Uri address, CancellationToken cancellationToken)
    {
        try
        {
            return await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new UpdateException(UpdateErrorCodes.FeedUnavailable, $"{address} did not answer: {exception.Message}", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException(UpdateErrorCodes.FeedUnavailable, $"{address} did not answer in time.", exception);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, Uri address)
    {
        if (!response.IsSuccessStatusCode)
            throw new UpdateException(UpdateErrorCodes.FeedUnavailable, $"{address} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    private static UpdateException TooLarge(Uri address)
        => new(UpdateErrorCodes.DownloadFailed, $"{address} is larger than {UpdateFeeds.MaxPackageBytes / (1024 * 1024)} MB.");
}
