namespace CodeAlta.Catalog;

/// <summary>
/// The rule of the spaces of a resource (a provider, an MCP server, a plugin, a skill, an agent prompt): the list
/// <c>spaces</c> written beside its definition says where it is available.
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term>absent or empty</term><description>every space (the resource is shared)</description></item>
/// <item><term><c>["business"]</c></term><description>Business only, not the default space</description></item>
/// <item><term><c>["business", "default"]</c></term><description>Business and the default space</description></item>
/// </list>
/// The default space is a space like the others here: a resource listed for some spaces is in it only when
/// <c>default</c> is listed. A resource defined in a project follows its project, which is already assigned to
/// spaces; this rule is applied before any enablement, so that a project cannot widen a space.
/// </remarks>
public static class SpaceScope
{
    /// <summary>Tells whether a resource is available in the space of a session.</summary>
    /// <param name="spaces">The spaces the resource is assigned to; <see langword="null"/> or empty for every space.</param>
    /// <param name="sessionSpaceId">The space of the session; <see langword="null"/> or empty for the default space.</param>
    /// <returns><see langword="true"/> when the resource is available there.</returns>
    public static bool IsAvailable(IReadOnlyCollection<string>? spaces, string? sessionSpaceId)
    {
        if (spaces is null || spaces.Count == 0) return true;
        var space = string.IsNullOrWhiteSpace(sessionSpaceId) ? SpaceDescriptor.DefaultId : sessionSpaceId.Trim();
        return spaces.Contains(space, StringComparer.Ordinal);
    }

    /// <summary>
    /// Works out the list of a resource once it is made available in one more space. A shared resource is already
    /// in every space and stays shared.
    /// </summary>
    /// <param name="spaces">The spaces the resource is assigned to; <see langword="null"/> or empty for every space.</param>
    /// <param name="spaceId">The space it is made available in.</param>
    /// <returns>The new list; empty for every space.</returns>
    /// <exception cref="ArgumentException"><paramref name="spaceId"/> is not the identifier of a space.</exception>
    public static IReadOnlyList<string> With(IReadOnlyCollection<string>? spaces, string spaceId)
    {
        ValidateId(spaceId);
        if (spaces is null || spaces.Count == 0) return [];
        return spaces.Contains(spaceId, StringComparer.Ordinal) ? [.. spaces] : [.. spaces, spaceId];
    }

    /// <summary>
    /// Works out the list of a resource once it is no longer available in one space. A shared resource is then
    /// assigned to every other space that exists, the default space included: a space created later does not get it.
    /// </summary>
    /// <param name="spaces">The spaces the resource is assigned to; <see langword="null"/> or empty for every space.</param>
    /// <param name="spaceId">The space it leaves.</param>
    /// <param name="existingSpaceIds">The identifiers of the spaces that exist, the default one included.</param>
    /// <returns>
    /// The new list; <see langword="null"/> when the resource would be left in no space, which no list can say
    /// (an empty one means every space): such a change is refused, and the resource is turned off instead.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="spaceId"/> is not the identifier of a space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="existingSpaceIds"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string>? Without(IReadOnlyCollection<string>? spaces, string spaceId, IEnumerable<string> existingSpaceIds)
    {
        ValidateId(spaceId);
        ArgumentNullException.ThrowIfNull(existingSpaceIds);
        var from = spaces is null || spaces.Count == 0 ? existingSpaceIds : spaces;
        var kept = from.Where(id => !string.Equals(id, spaceId, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        return kept.Count == 0 ? null : kept;
    }

    /// <summary>
    /// Reads a list of spaces as it is written: each identifier once, trimmed, in its order. A value that is not
    /// the identifier of a space is kept: it names no space, so the resource is not available there, which is what
    /// the list says.
    /// </summary>
    /// <param name="spaces">The values written; <see langword="null"/> for none.</param>
    /// <returns>The list; empty for every space.</returns>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? spaces)
    {
        if (spaces is null) return [];
        var kept = new List<string>();
        foreach (var value in spaces)
        {
            var id = value?.Trim();
            if (!string.IsNullOrEmpty(id) && !kept.Contains(id, StringComparer.Ordinal)) kept.Add(id);
        }

        return kept;
    }

    private static void ValidateId(string spaceId)
    {
        if (!SpaceDescriptor.IsValidId(spaceId)) throw new ArgumentException($"'{spaceId}' is not the identifier of a space.", nameof(spaceId));
    }
}
