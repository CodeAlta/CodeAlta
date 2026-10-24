using System.IO.Compression;
using System.Net;

namespace CodeAlta.Acp.Generator;

internal static class AcpSchemaSourceResolver
{
    private const string ProtocolVersionFolder = "v1";

    public static async Task<AcpSchemaSourceInfo> ResolveAsync(
        GeneratorCliOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.SchemaFile is not null)
        {
            var schemaPath = Path.GetFullPath(options.SchemaFile);
            var metaPath = ResolveMetaPath(schemaPath, options.Surface);
            return new AcpSchemaSourceInfo(
                "local-file",
                schemaPath,
                $"https://github.com/{options.GithubRepo}",
                options.SourceRef,
                schemaPath,
                metaPath,
                Path.GetDirectoryName(schemaPath) ?? Directory.GetCurrentDirectory());
        }

        if (options.AcpRepoDir is not null)
        {
            var repoDir = Path.GetFullPath(options.AcpRepoDir);
            var schemaPath = ResolveRepoSchemaPath(repoDir, options.Surface);
            var metaPath = ResolveMetaPath(schemaPath, options.Surface);
            return new AcpSchemaSourceInfo(
                "local-repo",
                repoDir,
                $"https://github.com/{options.GithubRepo}",
                options.SourceRef,
                schemaPath,
                metaPath,
                repoDir);
        }

        if (options.ZipFile is not null)
        {
            var zipPath = Path.GetFullPath(options.ZipFile);
            var extractedRoot = ExtractZipToTemp(zipPath);
            var schemaPath = ResolveExtractedSchemaPath(extractedRoot, options.Surface);
            var metaPath = ResolveMetaPath(schemaPath, options.Surface);
            return new AcpSchemaSourceInfo(
                "local-zip",
                zipPath,
                $"https://github.com/{options.GithubRepo}",
                options.SourceRef,
                schemaPath,
                metaPath,
                extractedRoot);
        }

        if (options.GithubRef is not null)
        {
            return await DownloadGithubArchiveAsync(options, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("No ACP schema source was specified.");
    }

    private static async Task<AcpSchemaSourceInfo> DownloadGithubArchiveAsync(
        GeneratorCliOptions options,
        CancellationToken cancellationToken)
    {
        var cacheRoot = Path.GetFullPath(options.CacheDir ?? Path.Combine(Path.GetTempPath(), "CodeAlta.Acp.Generator"));
        Directory.CreateDirectory(cacheRoot);

        var safeRepo = options.GithubRepo.Replace("/", "_", StringComparison.Ordinal);
        var safeRef = options.GithubRef!.Replace("/", "_", StringComparison.Ordinal).Replace("\\", "_", StringComparison.Ordinal);
        var zipPath = Path.Combine(cacheRoot, $"{safeRepo}.{safeRef}.zip");

        if (!File.Exists(zipPath) || options.ForceDownload)
        {
            var tagUrl = $"https://github.com/{options.GithubRepo}/archive/refs/tags/{options.GithubRef}.zip";
            var branchUrl = $"https://github.com/{options.GithubRepo}/archive/refs/heads/{options.GithubRef}.zip";
            var downloaded = await TryDownloadAsync(tagUrl, zipPath, cancellationToken).ConfigureAwait(false);
            if (!downloaded)
            {
                downloaded = await TryDownloadAsync(branchUrl, zipPath, cancellationToken).ConfigureAwait(false);
            }

            if (!downloaded)
            {
                throw new FileNotFoundException(
                    $"Failed to download ACP archive for ref '{options.GithubRef}' from '{options.GithubRepo}'.");
            }
        }

        var extractedRoot = ExtractZipToTemp(zipPath);
        var schemaPath = ResolveExtractedSchemaPath(extractedRoot, options.Surface);
        var metaPath = ResolveMetaPath(schemaPath, options.Surface);
        return new AcpSchemaSourceInfo(
            "github-archive",
            zipPath,
            $"https://github.com/{options.GithubRepo}",
            options.GithubRef,
            schemaPath,
            metaPath,
            extractedRoot);
    }

    private static async Task<bool> TryDownloadAsync(
        string url,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        await using var destination = File.Create(destinationPath);
        await response.Content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // The repository keeps the schema of protocol version 1 in schema/v1 since the v2 draft; older checkouts have it in schema.
    private static string ResolveRepoSchemaPath(string repoDir, AcpSurface surface)
    {
        var schemaName = GetSchemaFileName(surface);
        var versionedPath = Path.Combine(repoDir, "schema", ProtocolVersionFolder, schemaName);
        if (File.Exists(versionedPath))
        {
            return versionedPath;
        }

        var schemaPath = Path.Combine(repoDir, "schema", schemaName);
        if (!File.Exists(schemaPath))
        {
            throw new FileNotFoundException($"ACP schema file not found: {versionedPath}");
        }

        return schemaPath;
    }

    private static string ResolveExtractedSchemaPath(string extractedRoot, AcpSurface surface)
    {
        var schemaName = GetSchemaFileName(surface);
        var all = Directory.EnumerateFiles(extractedRoot, schemaName, SearchOption.AllDirectories).ToArray();
        var candidates = all.Where(IsVersionedSchemaPath).ToArray();
        if (candidates.Length == 0)
        {
            candidates = all
                .Where(static path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "schema", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new FileNotFoundException($"Unable to locate '{schemaName}' under '{extractedRoot}'."),
            _ => candidates.OrderBy(static path => path.Length).First(),
        };
    }

    private static bool IsVersionedSchemaPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        return string.Equals(Path.GetFileName(directory), ProtocolVersionFolder, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "schema", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveMetaPath(string schemaPath, AcpSurface surface)
    {
        var metaPath = Path.Combine(Path.GetDirectoryName(schemaPath)!, GetMetaFileName(surface));
        if (!File.Exists(metaPath))
        {
            throw new FileNotFoundException($"ACP meta file not found: {metaPath}");
        }

        return metaPath;
    }

    private static string ExtractZipToTemp(string zipPath)
    {
        var targetRoot = Path.Combine(
            Path.GetTempPath(),
            "CodeAlta.Acp.Generator",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(targetRoot);
        ZipFile.ExtractToDirectory(zipPath, targetRoot);
        return targetRoot;
    }

    private static string GetSchemaFileName(AcpSurface surface)
        => surface == AcpSurface.Stable ? "schema.json" : "schema.unstable.json";

    private static string GetMetaFileName(AcpSurface surface)
        => surface == AcpSurface.Stable ? "meta.json" : "meta.unstable.json";
}
