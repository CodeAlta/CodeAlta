using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Orchestration.Runtime.SystemPrompts;

/// <summary>
/// Discovers file-backed CodeAlta user prompts from built-in, user-global, and project-local instruction roots.
/// </summary>
public sealed class UserPromptCatalog
{
    /// <summary>The default user prompt identifier.</summary>
    public const string DefaultPromptName = "default";

    private readonly ISystemPromptContentLocator _contentLocator;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserPromptCatalog"/> class.
    /// </summary>
    /// <param name="contentLocator">Optional content locator used to resolve instruction roots.</param>
    public UserPromptCatalog(ISystemPromptContentLocator? contentLocator = null)
    {
        _contentLocator = contentLocator ?? new FileSystemPromptContentLocator();
    }

    /// <summary>
    /// Lists every valid user prompt file in display/source order.
    /// </summary>
    /// <param name="query">Discovery inputs.</param>
    /// <returns>All valid prompt descriptors, including shadowed lower-precedence prompts.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<UserPromptDescriptor> ListPrompts(UserPromptCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var roots = ResolveRoots(query);
        var prompts = EnumeratePromptRoots(roots)
            .SelectMany(static root => LoadPrompts(root))
            .OrderBy(static prompt => prompt.Precedence)
            .ThenBy(static prompt => prompt.PromptName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static prompt => prompt.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (prompts.Length == 0)
        {
            return prompts;
        }

        var effectiveByName = prompts
            .GroupBy(static prompt => prompt.PromptName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(static prompt => prompt.Precedence).Last(),
                StringComparer.OrdinalIgnoreCase);

        return prompts
            .Select(prompt =>
            {
                var effective = effectiveByName[prompt.PromptName];
                return ReferenceEquals(prompt, effective)
                    ? prompt
                    : prompt with { IsShadowed = true, ShadowedByPath = effective.SourcePath };
            })
            .ToArray();
    }

    /// <summary>
    /// Lists every valid system prompt file in display/source order.
    /// </summary>
    /// <param name="query">Discovery inputs.</param>
    /// <returns>All valid system prompt descriptors, including shadowed lower-precedence prompts.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<SystemPromptDescriptor> ListSystemPrompts(UserPromptCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var roots = ResolveRoots(query);
        var prompts = EnumerateSystemPromptRoots(roots)
            .SelectMany(static root => LoadSystemPrompts(root))
            .OrderBy(static prompt => prompt.Precedence)
            .ThenBy(static prompt => prompt.PromptName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static prompt => prompt.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (prompts.Length == 0)
        {
            return prompts;
        }

        var effectiveByName = prompts
            .GroupBy(static prompt => prompt.PromptName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(static prompt => prompt.Precedence).Last(),
                StringComparer.OrdinalIgnoreCase);

        return prompts
            .Select(prompt =>
            {
                var effective = effectiveByName[prompt.PromptName];
                return ReferenceEquals(prompt, effective)
                    ? prompt
                    : prompt with { IsShadowed = true, ShadowedByPath = effective.SourcePath };
            })
            .ToArray();
    }

    /// <summary>
    /// Lists effective user prompts after applying source precedence.
    /// </summary>
    /// <param name="query">Discovery inputs.</param>
    /// <returns>The effective prompts, one per prompt identifier.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<UserPromptDescriptor> ListEffectivePrompts(UserPromptCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ListPrompts(query)
            .Where(static prompt => !prompt.IsShadowed)
            .OrderBy(static prompt => prompt.Precedence)
            .ThenBy(static prompt => prompt.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static prompt => prompt.PromptName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Resolves an effective prompt by name, falling back to the default prompt when the requested prompt is absent.
    /// </summary>
    /// <param name="query">Discovery inputs.</param>
    /// <param name="promptName">Optional prompt identifier.</param>
    /// <returns>The resolved prompt descriptor, or <see langword="null"/> when no default prompt exists.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is <see langword="null"/>.</exception>
    public UserPromptDescriptor? ResolvePrompt(UserPromptCatalogQuery query, string? promptName)
    {
        ArgumentNullException.ThrowIfNull(query);
        var effectivePrompts = ListEffectivePrompts(query);
        var requestedName = NormalizePromptName(promptName) ?? DefaultPromptName;
        var selected = effectivePrompts.FirstOrDefault(prompt => string.Equals(prompt.PromptName, requestedName, StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
        {
            return selected;
        }

        return effectivePrompts.FirstOrDefault(prompt => string.Equals(prompt.PromptName, DefaultPromptName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves the global user prompt directory for the query.
    /// </summary>
    /// <param name="query">Discovery inputs.</param>
    /// <returns>The absolute global prompt directory path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is <see langword="null"/>.</exception>
    public string ResolveUserPromptDirectory(UserPromptCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ResolveRoots(query).UserPromptRoot;
    }

    /// <summary>
    /// Resolves the project-local prompt directory for the query.
    /// </summary>
    /// <param name="query">Discovery inputs.</param>
    /// <returns>The project prompt directory path, or <see langword="null"/> when no project root is available.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is <see langword="null"/>.</exception>
    public string? ResolveProjectPromptDirectory(UserPromptCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ResolveRoots(query).ProjectPromptRoot;
    }

    /// <summary>
    /// Resolves the global system prompt directory for the query.
    /// </summary>
    /// <param name="query">Discovery inputs.</param>
    /// <returns>The absolute global system prompt directory path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is <see langword="null"/>.</exception>
    public string ResolveUserSystemPromptDirectory(UserPromptCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Path.Combine(ResolveRoots(query).UserPromptRoot, "system");
    }

    /// <summary>
    /// Resolves the project-local system prompt directory for the query.
    /// </summary>
    /// <param name="query">Discovery inputs.</param>
    /// <returns>The project system prompt directory path, or <see langword="null"/> when no project root is available.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is <see langword="null"/>.</exception>
    public string? ResolveProjectSystemPromptDirectory(UserPromptCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var root = ResolveRoots(query).ProjectPromptRoot;
        return root is null ? null : Path.Combine(root, "system");
    }

    private SystemPromptContentRoots ResolveRoots(UserPromptCatalogQuery query)
        => _contentLocator.GetRoots(new SystemPromptDiscoveryContext
        {
            AppBaseDirectory = query.AppBaseDirectory,
            UserProfileRoot = query.UserProfileRoot,
            UserCodeAltaRoot = query.UserCodeAltaRoot,
            ProjectRoot = query.ProjectRoot,
            ProjectPromptResourcesTrusted = query.ProjectPromptResourcesTrusted || !string.IsNullOrWhiteSpace(query.ProjectRoot),
        });

    private static IEnumerable<UserPromptRoot> EnumeratePromptRoots(SystemPromptContentRoots roots)
    {
        if (Directory.Exists(Path.Combine(roots.ShippedPromptRoot, "prompts")))
        {
            yield return new UserPromptRoot(UserPromptSourceKind.BuiltIn, 0, Path.Combine(roots.ShippedPromptRoot, "prompts"));
        }

        if (Directory.Exists(Path.Combine(roots.UserPromptRoot, "prompts")))
        {
            yield return new UserPromptRoot(UserPromptSourceKind.UserGlobal, 1, Path.Combine(roots.UserPromptRoot, "prompts"));
        }

        if (roots.ProjectPromptResourcesTrusted && roots.ProjectPromptRoot is not null && Directory.Exists(Path.Combine(roots.ProjectPromptRoot, "prompts")))
        {
            yield return new UserPromptRoot(UserPromptSourceKind.Project, 2, Path.Combine(roots.ProjectPromptRoot, "prompts"));
        }
    }

    private static IEnumerable<UserPromptRoot> EnumerateSystemPromptRoots(SystemPromptContentRoots roots)
    {
        if (Directory.Exists(Path.Combine(roots.ShippedPromptRoot, "system")))
        {
            yield return new UserPromptRoot(UserPromptSourceKind.BuiltIn, 0, Path.Combine(roots.ShippedPromptRoot, "system"));
        }

        if (Directory.Exists(Path.Combine(roots.UserPromptRoot, "system")))
        {
            yield return new UserPromptRoot(UserPromptSourceKind.UserGlobal, 1, Path.Combine(roots.UserPromptRoot, "system"));
        }

        if (roots.ProjectPromptResourcesTrusted && roots.ProjectPromptRoot is not null && Directory.Exists(Path.Combine(roots.ProjectPromptRoot, "system")))
        {
            yield return new UserPromptRoot(UserPromptSourceKind.Project, 2, Path.Combine(roots.ProjectPromptRoot, "system"));
        }
    }

    private static IEnumerable<UserPromptDescriptor> LoadPrompts(UserPromptRoot root)
    {
        foreach (var path in Directory.EnumerateFiles(root.Path, "*.prompt.md", SearchOption.TopDirectoryOnly))
        {
            if (TryLoadPrompt(root, path, out var descriptor))
            {
                yield return descriptor;
            }
        }
    }

    private static IEnumerable<SystemPromptDescriptor> LoadSystemPrompts(UserPromptRoot root)
    {
        foreach (var path in Directory.EnumerateFiles(root.Path, "*.system-prompt.md", SearchOption.TopDirectoryOnly))
        {
            if (TryLoadSystemPrompt(root, path, out var descriptor))
            {
                yield return descriptor;
            }
        }
    }

    private static bool TryLoadPrompt(UserPromptRoot root, string path, out UserPromptDescriptor descriptor)
    {
        descriptor = null!;
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var (frontmatter, body) = SplitFrontmatter(text);
        var displayName = NormalizeRequiredText(frontmatter.GetValueOrDefault("name"));
        if (displayName is null || string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var promptName = Path.GetFileName(path);
        promptName = promptName.EndsWith(".prompt.md", StringComparison.OrdinalIgnoreCase)
            ? promptName[..^".prompt.md".Length]
            : Path.GetFileNameWithoutExtension(path);
        var normalizedPromptName = NormalizePromptName(promptName);
        if (normalizedPromptName is null)
        {
            return false;
        }

        var systemPromptName = NormalizePromptName(frontmatter.GetValueOrDefault("system")) ?? DefaultPromptName;
        var trimmedBody = body.Trim();
        descriptor = new UserPromptDescriptor(
            PromptName: normalizedPromptName,
            DisplayName: displayName,
            Description: NormalizeOptionalText(frontmatter.GetValueOrDefault("description")),
            SystemPromptName: systemPromptName,
            Body: trimmedBody,
            SourceKind: root.SourceKind,
            Precedence: root.Precedence,
            SourcePath: Path.GetFullPath(path),
            ContentHash: HashText(trimmedBody),
            IsShadowed: false,
            ShadowedByPath: null);
        return true;
    }

    private static bool TryLoadSystemPrompt(UserPromptRoot root, string path, out SystemPromptDescriptor descriptor)
    {
        descriptor = null!;
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var (_, body) = SplitFrontmatter(text);
        var promptName = Path.GetFileName(path);
        promptName = promptName.EndsWith(".system-prompt.md", StringComparison.OrdinalIgnoreCase)
            ? promptName[..^".system-prompt.md".Length]
            : Path.GetFileNameWithoutExtension(path);
        var normalizedPromptName = NormalizePromptName(promptName);
        var trimmedBody = body.Trim();
        if (normalizedPromptName is null || string.IsNullOrWhiteSpace(trimmedBody))
        {
            return false;
        }

        descriptor = new SystemPromptDescriptor(
            PromptName: normalizedPromptName,
            Body: trimmedBody,
            SourceKind: root.SourceKind,
            Precedence: root.Precedence,
            SourcePath: Path.GetFullPath(path),
            ContentHash: HashText(trimmedBody),
            IsShadowed: false,
            ShadowedByPath: null);
        return true;
    }

    private static (Dictionary<string, string> Frontmatter, string Body) SplitFrontmatter(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), text);
        }

        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), text);
        }

        var frontmatterText = normalized[4..end];
        var body = normalized[(end + 5)..];
        return (ParseFlatKeyValueFile(frontmatterText), body);
    }

    private static Dictionary<string, string> ParseFlatKeyValueFile(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line == "---")
            {
                continue;
            }

            var colonIndex = line.IndexOf(':', StringComparison.Ordinal);
            if (colonIndex <= 0)
            {
                continue;
            }

            var key = line[..colonIndex].Trim();
            var value = line[(colonIndex + 1)..].Trim().Trim('"', '\'');
            values[key] = value;
        }

        return values;
    }

    private static string HashText(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "sha256:" + Convert.ToHexString(bytes);
    }

    private static string? NormalizePromptName(string? name)
        => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private static string? NormalizeRequiredText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record UserPromptRoot(UserPromptSourceKind SourceKind, int Precedence, string Path);
}

/// <summary>
/// Inputs used when discovering user prompts.
/// </summary>
public sealed class UserPromptCatalogQuery
{
    /// <summary>Gets or initializes the application base directory. Defaults to <see cref="AppContext.BaseDirectory"/>.</summary>
    public string? AppBaseDirectory { get; init; }

    /// <summary>Gets or initializes the user profile directory. Defaults to the current user profile.</summary>
    public string? UserProfileRoot { get; init; }

    /// <summary>Gets or initializes the CodeAlta user-global root. Defaults to <c>~/.alta</c>.</summary>
    public string? UserCodeAltaRoot { get; init; }

    /// <summary>Gets or initializes the project root for project-local prompt overrides.</summary>
    public string? ProjectRoot { get; init; }

    /// <summary>Gets or initializes a value indicating whether project-local prompt resources are trusted.</summary>
    public bool ProjectPromptResourcesTrusted { get; init; }
}

/// <summary>
/// Identifies the source location for a discovered user prompt.
/// </summary>
public enum UserPromptSourceKind
{
    /// <summary>The prompt is shipped with CodeAlta.</summary>
    BuiltIn,

    /// <summary>The prompt comes from the user-global <c>~/.alta/instructions/prompts</c> root.</summary>
    UserGlobal,

    /// <summary>The prompt comes from the project-local <c>.alta/instructions/prompts</c> root.</summary>
    Project,
}

/// <summary>
/// Describes a discovered user prompt resource.
/// </summary>
/// <param name="PromptName">The file-derived prompt identifier.</param>
/// <param name="DisplayName">The required display name from frontmatter.</param>
/// <param name="Description">The optional prompt description.</param>
/// <param name="SystemPromptName">The system prompt selected by this user prompt.</param>
/// <param name="Body">The prompt body.</param>
/// <param name="SourceKind">The prompt source kind.</param>
/// <param name="Precedence">The source precedence where larger values override smaller values.</param>
/// <param name="SourcePath">The absolute source file path.</param>
/// <param name="ContentHash">The SHA-256 content hash.</param>
/// <param name="IsShadowed">Whether a higher-precedence prompt with the same name overrides this prompt.</param>
/// <param name="ShadowedByPath">The overriding prompt path, when shadowed.</param>
public sealed record UserPromptDescriptor(
    string PromptName,
    string DisplayName,
    string? Description,
    string SystemPromptName,
    string Body,
    UserPromptSourceKind SourceKind,
    int Precedence,
    string SourcePath,
    string ContentHash,
    bool IsShadowed,
    string? ShadowedByPath)
{
    /// <summary>Gets a value indicating whether the prompt is built into CodeAlta.</summary>
    public bool IsBuiltIn => SourceKind == UserPromptSourceKind.BuiltIn;
}

/// <summary>
/// Describes a discovered system prompt resource.
/// </summary>
/// <param name="PromptName">The file-derived system prompt identifier.</param>
/// <param name="Body">The system prompt body.</param>
/// <param name="SourceKind">The prompt source kind.</param>
/// <param name="Precedence">The source precedence where larger values override smaller values.</param>
/// <param name="SourcePath">The absolute source file path.</param>
/// <param name="ContentHash">The SHA-256 content hash.</param>
/// <param name="IsShadowed">Whether a higher-precedence system prompt with the same name overrides this prompt.</param>
/// <param name="ShadowedByPath">The overriding prompt path, when shadowed.</param>
public sealed record SystemPromptDescriptor(
    string PromptName,
    string Body,
    UserPromptSourceKind SourceKind,
    int Precedence,
    string SourcePath,
    string ContentHash,
    bool IsShadowed,
    string? ShadowedByPath)
{
    /// <summary>Gets a value indicating whether the prompt is built into CodeAlta.</summary>
    public bool IsBuiltIn => SourceKind == UserPromptSourceKind.BuiltIn;
}
