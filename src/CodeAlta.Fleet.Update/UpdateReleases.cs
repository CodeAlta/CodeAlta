namespace CodeAlta.Fleet.Update;

/// <summary>
/// The releases a feed offers to a whole fleet: the versions every product of the fleet lists (the tool packages of the
/// node and of the server), and the hashes of the runtime packages of a version, which a controller device signs in the
/// manifest of a campaign so that each machine checks its own download against them.
/// </summary>
public static class UpdateReleases
{
    /// <summary>The products of a fleet: CodeAlta Node and CodeAlta Server.</summary>
    public static IReadOnlyList<UpdateProduct> FleetProducts { get; } = [UpdateProduct.Node, UpdateProduct.Server];

    /// <summary>Lists the versions every product lists on a feed, the newest first.</summary>
    /// <param name="feed">The feed.</param>
    /// <param name="products">The products.</param>
    /// <param name="prerelease">Whether prereleases are listed.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The versions.</returns>
    /// <exception cref="UpdateException">The feed did not answer.</exception>
    public static async Task<IReadOnlyList<UpdateVersion>> ListAsync(IUpdateFeed feed, IReadOnlyList<UpdateProduct> products, bool prerelease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(products);
        var lists = await Task.WhenAll(products.Select(product => feed.ListVersionsAsync(product.PackageId, cancellationToken))).ConfigureAwait(false);
        HashSet<UpdateVersion>? common = null;
        foreach (var list in lists)
        {
            var listed = list.Where(found => found.Listed && (prerelease || !found.Version.IsPrerelease)).Select(static found => found.Version).ToHashSet();
            if (common is null) common = listed;
            else common.IntersectWith(listed);
        }

        return [.. (common ?? []).OrderDescending()];
    }

    /// <summary>
    /// Reads the SHA-512 of the runtime package of each published runtime of each product, as the feed has them; a
    /// runtime whose package the feed lacks is left out (its machines refuse the campaign).
    /// </summary>
    /// <param name="feed">The feed.</param>
    /// <param name="products">The products.</param>
    /// <param name="version">The version.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The hashes in base64, by product id then runtime.</returns>
    /// <exception cref="UpdateException">The feed did not answer, or has no runtime package of a product at that version.</exception>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> ReadHashesAsync(IUpdateFeed feed, IReadOnlyList<UpdateProduct> products,
        UpdateVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(version);
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var product in products)
        {
            var hashes = await Task.WhenAll(UpdateProduct.PublishedRuntimes.Select(async runtime =>
                (Runtime: runtime, Hash: await feed.GetHashAsync(product.RuntimePackageId(runtime), version, cancellationToken).ConfigureAwait(false)))).ConfigureAwait(false);
            var found = hashes.Where(static item => item.Hash is not null).ToDictionary(static item => item.Runtime, static item => item.Hash!.Base64, StringComparer.Ordinal);
            if (found.Count == 0)
                throw new UpdateException(UpdateErrorCodes.NotFound, $"{feed.Source} has no runtime package of {product.PackageId} {version}: the release is not complete yet.");
            result[product.PackageId] = found;
        }

        return result;
    }
}
