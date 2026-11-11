using System.Text.Json.Serialization;

namespace CodeAlta.Catalog;

/// <summary>
/// Describes a project in the global catalog.
/// </summary>
public sealed class ProjectDescriptor
{
    /// <summary>
    /// Gets or sets the project identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the project slug.
    /// </summary>
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the project name used for checkout directories.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local project path.
    /// </summary>
    [JsonPropertyName("path")]
    public string ProjectPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the default branch.
    /// </summary>
    [JsonPropertyName("default_branch")]
    public string DefaultBranch { get; set; } = "main";

    /// <summary>
    /// Gets or sets the optional description shown to users.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets optional tags.
    /// </summary>
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// Gets or sets the identifiers of the spaces the project belongs to, besides the default space,
    /// which holds every project. An identifier no space has is kept and means nothing.
    /// </summary>
    [JsonPropertyName("spaces")]
    public List<string> Spaces { get; set; } = [];

    /// <summary>
    /// Gets or sets the identifiers of the labels of the project, in the order they were given. An identifier no label
    /// has is kept and means nothing.
    /// </summary>
    [JsonPropertyName("labels")]
    public List<string> Labels { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the project is archived.
    /// </summary>
    [JsonPropertyName("archived")]
    public bool Archived { get; set; }

    /// <summary>
    /// Gets or sets the sandbox of the sessions of the project: the name of a sandbox profile, or <c>off</c>; null for
    /// the one of its space or of every session. Kept in the user's file of the project, never in the repository.
    /// </summary>
    [JsonPropertyName("sandbox")]
    public string? Sandbox { get; set; }

    /// <summary>
    /// Gets or sets the folders of skills the user added for the project, in the order of the list (see
    /// <see cref="Skills.AddedSkillFolders"/>). Kept in the user's file of the project, never in the repository: a
    /// repository cannot make CodeAlta read skills from outside of it.
    /// </summary>
    [JsonPropertyName("skill_folders")]
    public List<string> SkillFolders { get; set; } = [];

    /// <summary>
    /// Gets or sets checkout settings.
    /// </summary>
    [JsonPropertyName("checkout")]
    public CheckoutRule Checkout { get; set; } = new();

    /// <summary>
    /// Gets or sets the repository project of a sub-project: the project whose map (<c>.alta/projects.toml</c>) declares
    /// its folder; null for a project that is no sub-project. Computed when the catalog loads, never written to the file.
    /// </summary>
    [JsonPropertyName("parent_id")]
    public string? ParentId { get; set; }

    /// <summary>
    /// Gets or sets the folder of a sub-project inside its repository, with <c>/</c> between the names, such as
    /// <c>services/billing</c>; null for a project that is no sub-project. Computed, never written.
    /// </summary>
    [JsonPropertyName("sub_path")]
    public string? SubPath { get; set; }

    /// <summary>
    /// Gets or sets the key of a sub-project in the map of its repository, which its identifier is derived from; null
    /// for a project that is no sub-project. Computed, never written.
    /// </summary>
    [JsonPropertyName("declared_key")]
    public string? DeclaredKey { get; set; }

    /// <summary>
    /// Gets or sets the labels the map of its repository gives to a sub-project, as identifiers of labels
    /// (<see cref="LabelCatalog"/>). Computed, never written: the user's own labels are <see cref="Labels"/>.
    /// </summary>
    [JsonPropertyName("declared_labels")]
    public List<string> DeclaredLabels { get; set; } = [];

    /// <summary>Gets or sets whether the map of its repository lists the sub-project only for those who add it. Computed, never written.</summary>
    [JsonPropertyName("declared_optional")]
    public bool DeclaredOptional { get; set; }

    /// <summary>Gets or sets what the sessions of a sub-project may write, as the map of its repository says. Computed, never written.</summary>
    [JsonPropertyName("write_scope")]
    public ProjectWriteScope WriteScope { get; set; }

    /// <summary>
    /// Gets or sets the folders a sub-project inherits its setup from, the nearest first: the declared sub-projects
    /// whose folder holds its own, then the folder of its repository. Empty for a project that is no sub-project.
    /// Computed, never written.
    /// </summary>
    [JsonPropertyName("inherited_roots")]
    public List<string> InheritedRoots { get; set; } = [];

    /// <summary>
    /// Gets or sets the sandbox the repository project chose, which a sub-project without a choice of its own uses
    /// (<see cref="EffectiveSandbox"/>). Computed, never written.
    /// </summary>
    [JsonPropertyName("inherited_sandbox")]
    public string? InheritedSandbox { get; set; }

    /// <summary>
    /// Gets or sets the folders of skills the user added for the repository project, which a sub-project without its
    /// own uses (<see cref="EffectiveSkillFolders"/>). Computed, never written.
    /// </summary>
    [JsonPropertyName("inherited_skill_folders")]
    public List<string> InheritedSkillFolders { get; set; } = [];

    /// <summary>Gets or sets the display name of the repository project of a sub-project; null otherwise. Computed, never written.</summary>
    [JsonPropertyName("repository_name")]
    public string? RepositoryName { get; set; }

    /// <summary>Gets or sets the slug of the repository project of a sub-project; null otherwise. Computed, never written.</summary>
    [JsonPropertyName("repository_slug")]
    public string? RepositorySlug { get; set; }

    /// <summary>Gets the folder of the repository of a sub-project, the one of the project whose map declares it; null otherwise.</summary>
    [JsonIgnore]
    public string? RepositoryPath => ParentId is null || InheritedRoots.Count == 0 ? null : InheritedRoots[^1];

    /// <summary>Gets the sandbox of the sessions of the project: its own choice, else the one of its repository.</summary>
    [JsonIgnore]
    public string? EffectiveSandbox => Sandbox ?? InheritedSandbox;

    /// <summary>Gets the folders of skills added for the project: its own, else those of its repository.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveSkillFolders => SkillFolders.Count > 0 ? SkillFolders : InheritedSkillFolders;

    /// <summary>Gets whether the project is a sub-project of a repository.</summary>
    [JsonIgnore]
    public bool IsSubProject => ParentId is not null;

    // What the file of a sub-project says, under what the map shows: a save writes these, never the map's values.
    internal List<string>? FileSpaces { get; set; }
    internal string? FileDisplayName { get; set; }
    internal string? FileDescription { get; set; }

    /// <summary>
    /// Gets the path to the source markdown file when loaded from disk.
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Gets or sets the markdown body loaded from disk.
    /// </summary>
    public string? MarkdownBody { get; set; }

    /// <summary>
    /// Gets the parsed identifier.
    /// </summary>
    /// <exception cref="FormatException">Thrown when the identifier is invalid.</exception>
    public ProjectId ProjectId => ProjectId.Parse(Id);

    /// <summary>
    /// Copies the descriptor, with its lists and its checkout rule: what a caller changes and saves again then keeps
    /// every value it did not change.
    /// </summary>
    /// <returns>An equal descriptor that shares nothing that can be changed with this one.</returns>
    public ProjectDescriptor Copy() => new()
    {
        Id = Id,
        Slug = Slug,
        Name = Name,
        DisplayName = DisplayName,
        ProjectPath = ProjectPath,
        DefaultBranch = DefaultBranch,
        Description = Description,
        Tags = [.. Tags],
        Spaces = [.. Spaces],
        Labels = [.. Labels],
        Archived = Archived,
        Sandbox = Sandbox,
        SkillFolders = [.. SkillFolders],
        Checkout = new CheckoutRule
        {
            PathTemplate = Checkout.PathTemplate,
            Depth = Checkout.Depth,
            Submodules = Checkout.Submodules,
        },
        ParentId = ParentId,
        SubPath = SubPath,
        DeclaredKey = DeclaredKey,
        DeclaredLabels = [.. DeclaredLabels],
        DeclaredOptional = DeclaredOptional,
        WriteScope = WriteScope,
        InheritedRoots = [.. InheritedRoots],
        InheritedSandbox = InheritedSandbox,
        InheritedSkillFolders = [.. InheritedSkillFolders],
        RepositoryName = RepositoryName,
        RepositorySlug = RepositorySlug,
        FileSpaces = FileSpaces is null ? null : [.. FileSpaces],
        FileDisplayName = FileDisplayName,
        FileDescription = FileDescription,
        SourcePath = SourcePath,
        MarkdownBody = MarkdownBody,
    };

    // The descriptor as its file must say it: a sub-project's file keeps the user's values, never those of the map.
    internal ProjectDescriptor ForFile()
    {
        if (ParentId is null) return this;
        var file = Copy();
        file.Spaces = FileSpaces is null ? [] : [.. FileSpaces];
        file.DisplayName = FileDisplayName ?? ProjectPathNameFormatter.InferDisplayName(ProjectPath);
        file.Description = FileDescription;
        return file;
    }

    /// <summary>
    /// Keeps what a list of folders of skills can hold: each path on one line, once, at most
    /// <see cref="Skills.AddedSkillFolders.MaximumFolders"/> of them.
    /// </summary>
    /// <param name="folders">The folders, as a file writes them.</param>
    /// <returns>The folders that are kept, in their order.</returns>
    public static IReadOnlyList<string> NormalizeSkillFolders(IEnumerable<string?> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        return [.. folders.Select(static folder => folder?.Trim() ?? string.Empty)
            .Where(static folder => SpaceSettings.IsLine(folder, Skills.AddedSkillFolders.MaximumPathLength))
            .Distinct(StringComparer.Ordinal).Take(Skills.AddedSkillFolders.MaximumFolders)];
    }

    /// <summary>
    /// Validates the descriptor.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when required values are missing or invalid.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            DisplayName = ProjectPathNameFormatter.InferDisplayName(ProjectPath);
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = ProjectPathNameFormatter.InferName(ProjectPath, DisplayName);
        }

        if (!ProjectId.TryParse(Id, out _))
        {
            throw new ArgumentException($"Project '{Slug}' has an invalid id '{Id}'.", nameof(Id));
        }

        CatalogSlugValidator.Validate(Slug, nameof(Slug));

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Project name is required.", nameof(Name));
        }

        if (!ProjectPathNameFormatter.IsValidProjectName(Name))
        {
            throw new ArgumentException("Project name must be a valid single directory name.", nameof(Name));
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            throw new ArgumentException("Project display name is required.", nameof(DisplayName));
        }

        if (string.IsNullOrWhiteSpace(ProjectPath))
        {
            throw new ArgumentException("Project path is required.", nameof(ProjectPath));
        }

        if (string.IsNullOrWhiteSpace(DefaultBranch))
        {
            throw new ArgumentException("Project default branch is required.", nameof(DefaultBranch));
        }
    }

}

