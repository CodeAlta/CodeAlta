namespace CodeAlta.Catalog;

/// <summary>
/// Describes a space: a named group of projects the user works on together. Only one space is shown at a time,
/// and a project can be in several. It is not what the code calls a workspace, which is the catalog of projects
/// and sessions, and the main area of the window.
/// </summary>
/// <remarks>
/// A space is the file <c>spaces/&lt;id&gt;.md</c> of the catalog: its front matter, and its body, which
/// is the description. The projects of a space are the ones whose own file names it; the default
/// space holds every project and exists without a file.
/// </remarks>
public sealed class SpaceDescriptor
{
    /// <summary>The identifier of the default space, which holds every project and cannot be deleted.</summary>
    public const string DefaultId = "default";

    /// <summary>The name of the default space until the user gives it another one.</summary>
    public const string DefaultName = "Default";

    /// <summary>The longest name of a space, in UTF-16 units.</summary>
    public const int MaximumNameLength = 64;

    /// <summary>The longest description of a space, in UTF-16 units.</summary>
    public const int MaximumDescriptionLength = 2000;

    /// <summary>
    /// Gets or sets the identifier: worked out from the name when the space is created, and kept when the
    /// space is renamed.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the name shown to the user.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets what the space is for, for the user and for the agents; <see langword="null"/> when nothing is said.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the name of the icon, as the application names its icons; <see langword="null"/> for the usual one.</summary>
    public string? Icon { get; set; }

    /// <summary>Gets or sets the color, as <c>#rgb</c> or <c>#rrggbb</c>; <see langword="null"/> for none.</summary>
    public string? Color { get; set; }

    /// <summary>Gets or sets the place among the spaces: the lower comes first, after the default space.</summary>
    public int Order { get; set; }

    /// <summary>Gets or sets the file the space was read from; <see langword="null"/> for a space without a file.</summary>
    public string? SourcePath { get; set; }

    /// <summary>Gets whether this is the default space.</summary>
    public bool IsDefault => string.Equals(Id, DefaultId, StringComparison.Ordinal);

    /// <summary>Creates the default space as it is while no file describes it.</summary>
    /// <returns>The default space.</returns>
    public static SpaceDescriptor CreateDefault() => new() { Id = DefaultId, Name = DefaultName };

    /// <summary>Validates the descriptor.</summary>
    /// <exception cref="ArgumentException">Thrown when a value is missing or invalid.</exception>
    public void Validate()
    {
        if (!IsValidId(Id)) throw new ArgumentException($"Space id '{Id}' is not valid.", nameof(Id));
        if (!IsValidName(Name)) throw new ArgumentException("A space name is 1 to 64 characters on one line, without spaces around it.", nameof(Name));
        if (Description is { Length: > MaximumDescriptionLength }) throw new ArgumentException("A space description is at most 2000 characters.", nameof(Description));
        if (Icon is not null && !IsValidIcon(Icon)) throw new ArgumentException($"Space icon '{Icon}' is not a valid icon name.", nameof(Icon));
        if (Color is not null && !IsValidColor(Color)) throw new ArgumentException($"Space color '{Color}' is not #rgb or #rrggbb.", nameof(Color));
    }

    /// <summary>Tells whether a text can be the identifier of a space.</summary>
    /// <param name="id">The text.</param>
    /// <returns><see langword="true"/> for a slug of 2 to 64 lower-case letters, digits, <c>-</c>, <c>_</c> and <c>.</c>.</returns>
    public static bool IsValidId(string? id) => CatalogSlugValidator.IsValid(id);

    /// <summary>Tells whether a text can be the name of a space.</summary>
    /// <param name="name">The text.</param>
    /// <returns><see langword="true"/> for 1 to 64 characters on one line, without spaces around them.</returns>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumNameLength || name != name.Trim()) return false;
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsControl(name[i])) return false;
            if (!char.IsSurrogate(name[i])) continue;
            if (!char.IsHighSurrogate(name[i]) || ++i == name.Length || !char.IsLowSurrogate(name[i])) return false;
        }

        return true;
    }

    /// <summary>Tells whether a text can be the name of an icon.</summary>
    /// <param name="icon">The text.</param>
    /// <returns><see langword="true"/> for 1 to 64 lower-case letters, digits and <c>-</c>.</returns>
    public static bool IsValidIcon(string? icon)
    {
        if (icon is not { Length: >= 1 and <= 64 }) return false;
        foreach (var ch in icon)
        {
            if (ch is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')) return false;
        }

        return true;
    }

    /// <summary>Tells whether a text is a color written <c>#rgb</c> or <c>#rrggbb</c>.</summary>
    /// <param name="color">The text.</param>
    /// <returns><see langword="true"/> for such a color.</returns>
    public static bool IsValidColor(string? color)
    {
        if (color is not { Length: 4 or 7 } || color[0] != '#') return false;
        for (var i = 1; i < color.Length; i++)
        {
            if (!char.IsAsciiHexDigit(color[i])) return false;
        }

        return true;
    }

    /// <summary>
    /// Works out the identifier a space gets from its name: lower case, what is neither a letter nor a
    /// digit as one <c>-</c>, at most 64 characters, and <c>space</c> when nothing usable is left.
    /// </summary>
    /// <param name="name">The name of the space.</param>
    /// <returns>An identifier, which may already be taken.</returns>
    public static string IdFromName(string? name)
    {
        var builder = new System.Text.StringBuilder();
        var hyphen = false;
        foreach (var ch in (name ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (hyphen && builder.Length > 0) builder.Append('-');
                hyphen = false;
                builder.Append(ch);
            }
            else
            {
                hyphen = true;
            }
        }

        if (builder.Length > 64) builder.Length = 64;
        var id = builder.ToString().TrimEnd('-');
        return IsValidId(id) ? id : "space";
    }

    /// <summary>
    /// Reads the spaces a project file names: the valid identifiers, each once, in the order of the file.
    /// The default space is never among them: every project is in it.
    /// </summary>
    /// <param name="ids">The identifiers as written; <see langword="null"/> for none.</param>
    /// <returns>The identifiers kept.</returns>
    public static List<string> NormalizeIds(IEnumerable<string?>? ids)
    {
        var kept = new List<string>();
        if (ids is null) return kept;
        foreach (var value in ids)
        {
            var id = value?.Trim();
            if (IsValidId(id) && id != DefaultId && !kept.Contains(id!, StringComparer.Ordinal)) kept.Add(id!);
        }

        return kept;
    }
}
