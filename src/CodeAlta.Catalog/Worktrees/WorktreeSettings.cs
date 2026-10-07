namespace CodeAlta.Catalog.Worktrees;

/// <summary>Where the worktrees CodeAlta creates are placed.</summary>
public enum WorktreeLocation
{
    /// <summary>Under the CodeAlta folder of the user: <c>~/.alta/worktrees/&lt;project&gt;/&lt;name&gt;</c>.</summary>
    Global,

    /// <summary>Inside the repository, where git ignores them: <c>&lt;repository&gt;/.alta/worktrees/&lt;name&gt;</c>.</summary>
    Project,

    /// <summary>Under a folder the user chose: <c>&lt;folder&gt;/&lt;project&gt;/&lt;name&gt;</c>.</summary>
    Custom,
}

/// <summary>
/// The user's choice of where new worktrees go: the <c>[worktrees]</c> table of the user's configuration file.
/// </summary>
/// <param name="Location">The kind of place.</param>
/// <param name="Folder">The folder of <see cref="WorktreeLocation.Custom"/>, absolute; null otherwise.</param>
public sealed record WorktreeSettings(WorktreeLocation Location, string? Folder)
{
    /// <summary>The name of <see cref="WorktreeLocation.Global"/> in the configuration file.</summary>
    public const string GlobalName = "global";

    /// <summary>The name of <see cref="WorktreeLocation.Project"/> in the configuration file.</summary>
    public const string ProjectName = "project";

    /// <summary>The name of <see cref="WorktreeLocation.Custom"/> in the configuration file.</summary>
    public const string CustomName = "custom";

    /// <summary>
    /// Reads the choice from a configuration document. A location that is not known, and a custom location
    /// without an absolute folder, read as <see cref="WorktreeLocation.Global"/>.
    /// </summary>
    /// <param name="document">The <c>[worktrees]</c> table, or null when the file has none.</param>
    /// <param name="userProfile">The folder a leading <c>~</c> of the custom folder stands for.</param>
    /// <returns>The choice.</returns>
    public static WorktreeSettings Read(CodeAltaWorktreeSettingsDocument? document, string? userProfile = null)
    {
        var location = document?.Location?.Trim();
        if (string.Equals(location, ProjectName, StringComparison.OrdinalIgnoreCase))
        {
            return new(WorktreeLocation.Project, null);
        }

        if (string.Equals(location, CustomName, StringComparison.OrdinalIgnoreCase) && ResolveFolder(document?.Folder, userProfile) is { } folder)
        {
            return new(WorktreeLocation.Custom, folder);
        }

        return new(WorktreeLocation.Global, null);
    }

    /// <summary>Gets the name of a location in the configuration file.</summary>
    /// <param name="location">The location.</param>
    /// <returns><c>global</c>, <c>project</c> or <c>custom</c>.</returns>
    public static string NameOf(WorktreeLocation location)
        => location switch
        {
            WorktreeLocation.Project => ProjectName,
            WorktreeLocation.Custom => CustomName,
            _ => GlobalName,
        };

    /// <summary>
    /// Resolves the folder of a custom location: an absolute path, in which a leading <c>~</c> stands for the
    /// folder of the user.
    /// </summary>
    /// <param name="folder">The folder as written.</param>
    /// <param name="userProfile">The folder of the user; the one of the current user when null.</param>
    /// <returns>The absolute folder, or null when what is written is not one.</returns>
    public static string? ResolveFolder(string? folder, string? userProfile = null)
    {
        var text = folder?.Trim();
        if (string.IsNullOrEmpty(text) || text.AsSpan().ContainsAnyInRange('\0', '\u001f'))
        {
            return null;
        }

        if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = string.IsNullOrWhiteSpace(userProfile) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : userProfile;
            if (string.IsNullOrWhiteSpace(home))
            {
                return null;
            }

            text = text.Length == 1 ? home : Path.Combine(home, text[2..]);
        }

        try
        {
            return Path.IsPathFullyQualified(text) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(text)) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
