using System.IO.Compression;

namespace CodeAlta.Fleet.Update;

/// <summary>What an update asks for. The fleet update passes the same request, with the hash a controller device signed.</summary>
public sealed record UpdateRequest
{
    /// <summary>Gets the exact version; null for the latest the feed lists (stable, unless prereleases are allowed).</summary>
    public string? Version { get; init; }

    /// <summary>Gets the feed; null for the configured one, or nuget.org.</summary>
    public string? Source { get; init; }

    /// <summary>Gets the SHA-512 the package must have (base64, hexadecimal, or after <c>sha512:</c>); null to rely on the feed's.</summary>
    public string? ExpectedSha512 { get; init; }

    /// <summary>Gets whether a prerelease may be chosen when the running version is a release.</summary>
    public bool Prerelease { get; init; }

    /// <summary>Gets whether the switch does not wait for running work.</summary>
    public bool Now { get; init; }

    /// <summary>Gets who asks: the owner on the machine, or the fleet.</summary>
    public UpdateOrigin Origin { get; init; } = UpdateOrigin.Local;
}

/// <summary>What a feed offers against what runs.</summary>
/// <param name="Source">The feed read.</param>
/// <param name="PackageId">The runtime package read (<c>CodeAlta.Node.win-x64</c>).</param>
/// <param name="Running">The version that runs.</param>
/// <param name="Latest">The latest version the feed lists that this machine may take; null when it lists none.</param>
/// <param name="Target">The version to install; null when the machine is up to date or the feed has nothing for it.</param>
/// <param name="Note">Why there is no target, or what to know about it (a version rolled back before); null when nothing.</param>
public sealed record UpdateCheck(string Source, string PackageId, UpdateVersion Running, UpdateVersion? Latest, UpdateVersion? Target, string? Note);

/// <summary>A version downloaded, checked and unpacked in its folder, ready to be selected.</summary>
/// <param name="Version">The version.</param>
/// <param name="Path">Its folder.</param>
/// <param name="Hash">The SHA-512 of its package.</param>
/// <param name="Checked">What checked the hash: <c>feed</c>, <c>expected</c>, both, or <c>local folder</c> when only a folder of this machine vouches for it.</param>
/// <param name="AlreadyStaged">Whether the folder was there from an earlier update.</param>
public sealed record StagedUpdate(UpdateVersion Version, string Path, PackageHash Hash, string Checked, bool AlreadyStaged);

/// <summary>What the folder of a staged version records about its package (<c>.package.json</c>).</summary>
internal sealed class StagedVersionInfo
{
    public string? PackageId { get; set; }

    public string? Version { get; set; }

    public string? Source { get; set; }

    public string? Sha512 { get; set; }

    public DateTimeOffset StagedAt { get; set; }
}

/// <summary>The options of a <see cref="SelfUpdater"/>.</summary>
public sealed class SelfUpdaterOptions
{
    /// <summary>Gets the product that updates itself.</summary>
    public required UpdateProduct Product { get; init; }

    /// <summary>Gets the folder of versions of the machine.</summary>
    public required UpdateVersionStore Store { get; init; }

    /// <summary>Gets the version that runs.</summary>
    public required UpdateVersion Running { get; init; }

    /// <summary>Gets which feeds are allowed.</summary>
    public required UpdateSourcePolicy Policy { get; init; }

    /// <summary>Gets the HTTP client of the feeds.</summary>
    public required HttpClient Http { get; init; }

    /// <summary>Gets the runtime whose package is downloaded; null for the one of this process (<see cref="UpdateProduct.CurrentRuntime"/>).</summary>
    public string? Runtime { get; init; }
}

/// <summary>
/// Finds, downloads, checks and unpacks a version of a product into its folder of versions. It does not switch: the
/// running process does, when it may (<see cref="UpdateVersionStore.RequestSwitch"/>).
/// </summary>
public sealed class SelfUpdater
{
    /// <summary>The most bytes a package may unpack to.</summary>
    internal const long MaxUnpackedBytes = 2L * 1024 * 1024 * 1024;

    private readonly SelfUpdaterOptions _options;

    /// <summary>Initializes a new instance of the <see cref="SelfUpdater"/> class.</summary>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public SelfUpdater(SelfUpdaterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Gets the runtime whose package is read.</summary>
    /// <exception cref="UpdateException">No package is published for this machine.</exception>
    public string Runtime => _options.Runtime ?? UpdateProduct.CurrentRuntime()
        ?? throw new UpdateException(UpdateErrorCodes.UnsupportedRuntime, $"No package of {_options.Product.PackageId} is published for this OS and architecture.");

    /// <summary>
    /// Reads the feed and chooses the version to install: one that both the tool package and the runtime package of this
    /// machine list.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the requests to the feed.</param>
    /// <returns>What the feed offers.</returns>
    /// <exception cref="UpdateException">The feed is not allowed or did not answer, or the version asked for is not valid or not in both packages.</exception>
    public async Task<UpdateCheck> CheckAsync(UpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = _options.Policy.Resolve(request.Source, request.Origin);
        var feed = UpdateFeeds.Open(source, _options.Http);
        var runtimePackageId = _options.Product.RuntimePackageId(Runtime);
        var toolLookup = feed.ListVersionsAsync(_options.Product.PackageId, cancellationToken);
        var runtimeLookup = feed.ListVersionsAsync(runtimePackageId, cancellationToken);
        await Task.WhenAll(toolLookup, runtimeLookup).ConfigureAwait(false);
        return Choose(source, _options.Product.PackageId, await toolLookup.ConfigureAwait(false), runtimePackageId, await runtimeLookup.ConfigureAwait(false), request,
            _options.Running, _options.Store.Read());
    }

    /// <summary>
    /// Chooses the version to install. A version is available once both packages of the release list it: the tool
    /// package and the runtime package of this machine. The packages of a release appear on nuget.org one after the other
    /// (over 20 minutes to an hour), and a tool package that is unlisted withdraws the release; only the runtime package
    /// is downloaded, since it holds the application. The same rule as CodeAlta Desktop's own update check
    /// (<c>CodeAltaNuGetUpdateChecker.CheckNuGetOrgAsync</c> in CodeAlta.Hosting, which the server must not reference).
    /// </summary>
    internal static UpdateCheck Choose(string source, string toolPackageId, IReadOnlyList<FeedVersion> toolVersions, string runtimePackageId,
        IReadOnlyList<FeedVersion> runtimeVersions, UpdateRequest request, UpdateVersion running, UpdateState state)
    {
        var packageId = runtimePackageId;
        if (request.Version is { } asked)
        {
            if (!UpdateVersion.TryParse(asked, out var exact)) throw new UpdateException(UpdateErrorCodes.NotFound, $"'{asked}' is not a version.");
            var tool = toolVersions.FirstOrDefault(found => found.Version.Equals(exact));
            var runtime = runtimeVersions.FirstOrDefault(found => found.Version.Equals(exact));
            if (tool is null || runtime is null)
            {
                var missing = (tool, runtime) switch
                {
                    (null, null) => $"neither {toolPackageId} {exact} nor {runtimePackageId} {exact}",
                    (null, _) => $"{runtimePackageId} {exact} but not its tool package {toolPackageId} {exact}: the release is not complete yet, or was withdrawn",
                    _ => $"{toolPackageId} {exact} but not {runtimePackageId} {exact}, the package of this machine: the release is not complete yet",
                };
                throw new UpdateException(UpdateErrorCodes.NotFound, $"{source} has {missing}.");
            }

            var note = exact.Equals(running) ? $"{exact} already runs."
                : state.IsBad(exact) ? $"{exact} was rolled back on this machine before; it is tried again because it was asked for."
                : !tool.Listed || !runtime.Listed ? $"{exact} is unlisted on {source}; it is installed because it was asked for."
                : null;
            return new UpdateCheck(source, packageId, running, exact, exact.Equals(running) ? null : exact, note);
        }

        // A machine on a prerelease follows the prereleases; a release follows releases unless asked.
        var prerelease = request.Prerelease || running.IsPrerelease;
        var listedRuntime = runtimeVersions.Where(static found => found.Listed).Select(static found => found.Version).ToHashSet();
        var candidates = toolVersions.Where(found => found.Listed && listedRuntime.Contains(found.Version) && (prerelease || !found.Version.IsPrerelease))
            .Select(static found => found.Version).ToArray();
        var latest = candidates.Where(version => !state.IsBad(version)).DefaultIfEmpty().Max();
        var newest = candidates.DefaultIfEmpty().Max();
        string? skipped = newest is not null && state.IsBad(newest) ? $"{newest} was rolled back on this machine: it is skipped unless named with --version." : null;
        if (latest is null)
            return new UpdateCheck(source, packageId, running, null, null, skipped ?? $"{source} lists no {(prerelease ? "" : "stable ")}version of {toolPackageId} with its package {packageId}.");
        if (!(latest > running)) return new UpdateCheck(source, packageId, running, latest, null, skipped ?? $"{running} is up to date.");
        return new UpdateCheck(source, packageId, running, latest, latest, skipped);
    }

    /// <summary>
    /// Downloads a version, checks its hash and unpacks the application of its runtime package into its folder. The
    /// package must match the feed's hash when the feed gives one, and the expected hash when the request gives one; a
    /// package from an HTTP feed that neither checks is refused.
    /// </summary>
    /// <param name="check">The check that chose the version.</param>
    /// <param name="request">The request (its expected hash).</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The staged version.</returns>
    /// <exception cref="UpdateException">The check has no target, the download failed, the hash does not match, or the package is not valid.</exception>
    public async Task<StagedUpdate> StageAsync(UpdateCheck check, UpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(request);
        var version = check.Target ?? throw new UpdateException(UpdateErrorCodes.NotFound, check.Note ?? "There is nothing to install.");
        PackageHash? expected = null;
        if (request.ExpectedSha512 is { } text && !PackageHash.TryParse(text, out expected))
            throw new UpdateException(UpdateErrorCodes.HashMismatch, "The expected hash is not a SHA-512 (64 bytes in base64, or 128 hexadecimal digits).");
        var store = _options.Store;
        var product = _options.Product;
        var target = store.VersionPath(version);
        var stagingRoot = Path.Combine(store.Root, UpdateVersionStore.StagingFolderName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        try
        {
            var package = Path.Combine(stagingRoot, "package.nupkg");
            var feed = UpdateFeeds.Open(check.Source, _options.Http);
            var download = await feed.DownloadAsync(check.PackageId, version, package, cancellationToken).ConfigureAwait(false);
            var actual = await PackageHash.ComputeAsync(package, cancellationToken).ConfigureAwait(false);
            if (download.FeedHash is { } feedHash && !feedHash.Equals(actual))
                throw new UpdateException(UpdateErrorCodes.HashMismatch, $"{check.PackageId} {version} does not match the hash {check.Source} gives for it: it was not installed.");
            if (expected is not null && !expected.Equals(actual))
                throw new UpdateException(UpdateErrorCodes.HashMismatch, $"{check.PackageId} {version} does not match the expected hash: it was not installed.");
            if (download.FeedHash is null && expected is null && !feed.IsLocal)
                throw new UpdateException(UpdateErrorCodes.HashMismatch,
                    $"{check.Source} gives no hash of {check.PackageId} {version}: pass the expected SHA-512 (--sha512) to install it.");
            var checkedBy = (download.FeedHash, expected) switch
            {
                (not null, not null) => "feed and expected hash",
                (not null, null) => "feed",
                (null, not null) => "expected hash",
                _ => "local folder",
            };

            if (store.IsStaged(version, product))
                return new StagedUpdate(version, target, actual, checkedBy, AlreadyStaged: true);

            var files = Path.Combine(stagingRoot, "files");
            Extract(package, files, product, Runtime);
            File.WriteAllBytes(Path.Combine(files, ".package.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new StagedVersionInfo
            {
                PackageId = check.PackageId, Version = version.ToString(), Source = check.Source, Sha512 = actual.Base64, StagedAt = store.Time.GetUtcNow(),
            }, UpdateJsonContext.Default.StagedVersionInfo));
            // A folder left by an interrupted earlier attempt holds no entry assembly: it is replaced.
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(files, target);
            return new StagedUpdate(version, target, actual, checkedBy, AlreadyStaged: false);
        }
        finally
        {
            try { Directory.Delete(stagingRoot, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Unpacks <c>tools/&lt;framework&gt;/&lt;runtime&gt;/</c> of a runtime package into a folder: the entries of that folder
    /// only, none outside the destination, bounded in size; the app host made executable on Unix.
    /// </summary>
    internal static void Extract(string package, string destination, UpdateProduct product, string runtime)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(package);
        }
        catch (InvalidDataException exception)
        {
            throw new UpdateException(UpdateErrorCodes.InvalidPackage, $"The package is not a NuGet package: {exception.Message}", exception);
        }

        using (archive)
        {
            var marker = $"/{runtime}/{product.AssemblyFileName}";
            var entry = archive.Entries.FirstOrDefault(candidate => candidate.FullName.StartsWith("tools/", StringComparison.Ordinal)
                && candidate.FullName.EndsWith(marker, StringComparison.OrdinalIgnoreCase) && candidate.FullName.Count(static c => c == '/') == 3)
                ?? throw new UpdateException(UpdateErrorCodes.InvalidPackage, $"The package has no tools/<framework>/{runtime}/{product.AssemblyFileName}: it is not the {runtime} package of {product.PackageId}.");
            var prefix = entry.FullName[..^product.AssemblyFileName.Length];
            var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            long total = 0;
            Directory.CreateDirectory(root);
            foreach (var item in archive.Entries)
            {
                if (!item.FullName.StartsWith(prefix, StringComparison.Ordinal) || item.FullName.Length == prefix.Length) continue;
                var relative = Uri.UnescapeDataString(item.FullName[prefix.Length..]);
                var path = Path.GetFullPath(Path.Combine(root, relative));
                if (!path.StartsWith(root, StringComparison.Ordinal))
                    throw new UpdateException(UpdateErrorCodes.InvalidPackage, $"The package has an entry outside its folder: {item.FullName}.");
                if (item.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(path);
                    continue;
                }

                total += item.Length;
                if (total > MaxUnpackedBytes) throw new UpdateException(UpdateErrorCodes.InvalidPackage, "The package unpacks to more than 2 GB.");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                item.ExtractToFile(path, overwrite: false);
            }

            if (!OperatingSystem.IsWindows())
            {
                // A package made on Windows carries no Unix permissions: the app host is made executable.
                var host = Path.Combine(root, product.AppHostFileName(windows: false));
                if (File.Exists(host))
                {
                    File.SetUnixFileMode(host, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                        | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
            }
        }
    }
}
