namespace CodeAlta.Orchestration.Runtime.SystemPrompts;

/// <summary>
/// Resolves CodeAlta system prompt content roots and shipped content paths.
/// </summary>
public interface ISystemPromptContentLocator
{
    /// <summary>
    /// Gets the prompt content roots for a build context.
    /// </summary>
    /// <param name="context">The discovery context.</param>
    /// <returns>The resolved prompt content roots.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is <see langword="null"/>.</exception>
    SystemPromptContentRoots GetRoots(SystemPromptDiscoveryContext context);

    /// <summary>
    /// Resolves a path under the shipped prompt resource root.
    /// </summary>
    /// <param name="relativePromptPath">Relative path under <c>content/system_prompts</c>.</param>
    /// <returns>The absolute path.</returns>
    /// <exception cref="ArgumentException">Thrown when the path is empty or escapes the prompt root.</exception>
    string ResolveBuiltInPromptPath(string relativePromptPath);

    /// <summary>
    /// Resolves a path under the shipped documentation root.
    /// </summary>
    /// <param name="fileName">Relative path under <c>content/docs</c>.</param>
    /// <returns>The absolute path.</returns>
    /// <exception cref="ArgumentException">Thrown when the path is empty or escapes the docs root.</exception>
    string ResolveBuiltInDocPath(string fileName);
}

/// <summary>
/// Inputs used when discovering prompt roots.
/// </summary>
public sealed class SystemPromptDiscoveryContext
{
    /// <summary>
    /// Gets or sets the application base directory. Defaults to <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    public string? AppBaseDirectory { get; init; }

    /// <summary>
    /// Gets or sets the user profile directory. Defaults to the current user profile.
    /// </summary>
    public string? UserProfileRoot { get; init; }

    /// <summary>
    /// Gets or sets the CodeAlta user-global root. Defaults to <c>~/.alta</c>.
    /// </summary>
    public string? UserCodeAltaRoot { get; init; }

    /// <summary>
    /// Gets or sets the project root for project-local prompt overrides.
    /// </summary>
    public string? ProjectRoot { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether project-local prompt resources are trusted.
    /// </summary>
    public bool ProjectPromptResourcesTrusted { get; init; }
}

/// <summary>
/// Resolved prompt and documentation content roots.
/// </summary>
/// <param name="ShippedPromptRoot">Built-in prompt resource root.</param>
/// <param name="ShippedDocsRoot">Built-in documentation root.</param>
/// <param name="UserPromptRoot">User-global prompt resource root.</param>
/// <param name="ProjectPromptRoot">Project-local prompt resource root, when available.</param>
/// <param name="ProjectPromptResourcesTrusted">Whether the project-local prompt root is trusted for prompt composition.</param>
public sealed record SystemPromptContentRoots(
    string ShippedPromptRoot,
    string ShippedDocsRoot,
    string UserPromptRoot,
    string? ProjectPromptRoot,
    bool ProjectPromptResourcesTrusted);

/// <summary>
/// Default file-system implementation of <see cref="ISystemPromptContentLocator"/>.
/// </summary>
public sealed class FileSystemPromptContentLocator : ISystemPromptContentLocator
{
    private readonly string? _appBaseDirectory;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSystemPromptContentLocator"/> class.
    /// </summary>
    /// <param name="appBaseDirectory">Optional application base directory override.</param>
    public FileSystemPromptContentLocator(string? appBaseDirectory = null)
    {
        _appBaseDirectory = NormalizeOptionalRoot(appBaseDirectory);
    }

    /// <inheritdoc />
    public SystemPromptContentRoots GetRoots(SystemPromptDiscoveryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var appBaseDirectory = NormalizeOptionalRoot(context.AppBaseDirectory) ?? _appBaseDirectory ?? Path.GetFullPath(AppContext.BaseDirectory);
        var userProfile = NormalizeOptionalRoot(context.UserProfileRoot) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userCodeAltaRoot = NormalizeOptionalRoot(context.UserCodeAltaRoot) ?? Path.Combine(userProfile, ".alta");
        var projectRoot = NormalizeOptionalRoot(context.ProjectRoot);

        return new SystemPromptContentRoots(
            ShippedPromptRoot: Path.Combine(appBaseDirectory, "content", "system_prompts"),
            ShippedDocsRoot: Path.Combine(appBaseDirectory, "content", "docs"),
            UserPromptRoot: Path.Combine(userCodeAltaRoot, "system_prompts"),
            ProjectPromptRoot: projectRoot is null ? null : Path.Combine(projectRoot, ".alta", "system_prompts"),
            ProjectPromptResourcesTrusted: context.ProjectPromptResourcesTrusted);
    }

    /// <inheritdoc />
    public string ResolveBuiltInPromptPath(string relativePromptPath)
        => ResolveUnderRoot(Path.Combine(GetAppBaseDirectory(), "content", "system_prompts"), relativePromptPath);

    /// <inheritdoc />
    public string ResolveBuiltInDocPath(string fileName)
        => ResolveUnderRoot(Path.Combine(GetAppBaseDirectory(), "content", "docs"), fileName);

    private string GetAppBaseDirectory()
        => _appBaseDirectory ?? Path.GetFullPath(AppContext.BaseDirectory);

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Path must be relative.", nameof(relativePath));
        }

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Path must stay under the content root.", nameof(relativePath));
        }

        return fullPath;
    }

    private static string? NormalizeOptionalRoot(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
