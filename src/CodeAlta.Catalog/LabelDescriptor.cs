namespace CodeAlta.Catalog;

/// <summary>
/// Describes a label: a name the user gives to some projects to find them together, shown as a badge of its own icon
/// and color. A label can be under another label, and a project can have several.
/// </summary>
/// <remarks>
/// A label is the file <c>labels/&lt;id&gt;.md</c> of the catalog: its front matter, and its body, which is the
/// description. The projects of a label are the ones whose own file names it. Only a top-level label has a space: its
/// sub-labels are in the same one.
/// </remarks>
public sealed class LabelDescriptor
{
    /// <summary>The longest name of a label, in UTF-16 units.</summary>
    public const int MaximumNameLength = 64;

    /// <summary>The longest description of a label, in UTF-16 units.</summary>
    public const int MaximumDescriptionLength = 2000;

    /// <summary>The most levels of labels: a top-level label is at level 1.</summary>
    public const int MaximumDepth = 4;

    /// <summary>
    /// Gets or sets the identifier: worked out from the name when the label is created, and kept when the label is
    /// renamed or moved.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the name shown to the user.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets what the label stands for, for the user and for the agents; <see langword="null"/> when nothing is said.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the name of the icon, as the application names its icons; <see langword="null"/> for the usual one.</summary>
    public string? Icon { get; set; }

    /// <summary>Gets or sets the color of its badge, as <c>#rgb</c> or <c>#rrggbb</c>; <see langword="null"/> for none.</summary>
    public string? Color { get; set; }

    /// <summary>Gets or sets the place among the labels of the same parent: the lower comes first.</summary>
    public int Order { get; set; }

    /// <summary>Gets or sets the identifier of the label it is under; <see langword="null"/> for a top-level label.</summary>
    public string? Parent { get; set; }

    /// <summary>Gets or sets whether the user keeps the label on top of the others.</summary>
    public bool Favorite { get; set; }

    /// <summary>
    /// Gets or sets the space of the label: the one the file of its top-level label names, for a sub-label too;
    /// <see langword="null"/> for a label of every space.
    /// </summary>
    public string? Space { get; set; }

    /// <summary>Gets or sets the file the label was read from; <see langword="null"/> before it is written.</summary>
    public string? SourcePath { get; set; }

    /// <summary>Tells whether the label is available in a space.</summary>
    /// <param name="spaceId">The space; <see langword="null"/> or empty for the default space.</param>
    /// <returns><see langword="true"/> when the label has no space, or this one.</returns>
    public bool IsIn(string? spaceId)
        => SpaceScope.IsAvailable(Space is null ? null : [Space], spaceId);

    /// <summary>Tells whether a text can be the identifier of a label.</summary>
    /// <param name="id">The text.</param>
    /// <returns><see langword="true"/> for a slug of 2 to 64 lower-case letters, digits, <c>-</c>, <c>_</c> and <c>.</c>.</returns>
    public static bool IsValidId(string? id) => CatalogSlugValidator.IsValid(id);

    /// <summary>
    /// Works out the identifier a label gets from its name: lower case, what is neither a letter nor a digit as one
    /// <c>-</c>, at most 64 characters, and <c>label</c> when nothing usable is left.
    /// </summary>
    /// <param name="name">The name of the label.</param>
    /// <returns>An identifier, which may already be taken.</returns>
    public static string IdFromName(string? name) => SpaceDescriptor.IdFromName(name, "label");

    /// <summary>Reads the labels a project file names: the valid identifiers, each once, in the order of the file.</summary>
    /// <param name="ids">The identifiers as written; <see langword="null"/> for none.</param>
    /// <returns>The identifiers kept.</returns>
    public static List<string> NormalizeIds(IEnumerable<string?>? ids)
    {
        var kept = new List<string>();
        if (ids is null) return kept;
        foreach (var value in ids)
        {
            var id = value?.Trim();
            if (IsValidId(id) && !kept.Contains(id!, StringComparer.Ordinal)) kept.Add(id!);
        }

        return kept;
    }

    /// <summary>Copies the label.</summary>
    /// <returns>An equal label that shares nothing with this one.</returns>
    public LabelDescriptor Copy() => (LabelDescriptor)MemberwiseClone();
}
