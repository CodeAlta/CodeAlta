using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Orchestration.Runtime.SystemPrompts;

/// <summary>
/// Discovers file-backed CodeAlta agent prompts from built-in, user-global, and project-local prompt roots, and
/// the custom agents of GitHub Copilot (<c>.github/agents</c>, <c>~/.copilot/agents</c>) beside them.
/// </summary>
public sealed class AgentPromptCatalog
{
    /// <summary>The default agent prompt identifier.</summary>
    public const string DefaultPromptName = "default";

    private readonly ISystemPromptContentLocator _contentLocator;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentPromptCatalog"/> class.
    /// </summary>
    /// <param name="contentLocator">Optional content locator used to resolve prompt roots.</param>
    public AgentPromptCatalog(ISystemPromptContentLocator? contentLocator = null)
    {
        _contentLocator = contentLocator ?? new FileSystemPromptContentLocator();
    }

    /// <summary>
    /// Lists every valid agent prompt file in display/source order.
    /// </summary>
    /// <param name="query">Discovery inputs.</param>
    /// <returns>All valid prompt descriptors, including shadowed lower-precedence prompts.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<AgentPromptDescriptor> ListPrompts(AgentPromptCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var roots = ResolveRoots(query);
        var prompts = EnumeratePromptRoots(roots)
            .SelectMany(static root => LoadAgentPromptResources(root))
            .OrderBy(static prompt => prompt.SourceKind.IsCopilot())
            .ThenBy(static prompt => prompt.Precedence)
            .ThenBy(static prompt => prompt.PromptName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static prompt => prompt.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (prompts.Length == 0)
        {
            return [];
        }

        var shadowedByPath = BuildShadowedPathMap(prompts);

        return prompts
            .Select(prompt =>
            {
                var isShadowed = shadowedByPath.TryGetValue(prompt.SourcePath, out var shadowedBy);
                return ToDescriptor(prompt, isShadowed, shadowedBy);
            })
            .ToArray();
    }

    /// <summary>
    /// Lists every valid system prompt file in display/source order.
    /// </summary>
    /// <param name="query">Discovery inputs.</param>
    /// <returns>All valid system prompt descriptors, including shadowed lower-precedence prompts.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<SystemPromptDescriptor> ListSystemPrompts(AgentPromptCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var roots = ResolveRoots(query);
        var prompts = EnumerateSystemPromptRoots(roots)
            .SelectMany(static root => LoadSystemPromptResources(root))
            .OrderBy(static prompt => prompt.Precedence)
            .ThenBy(static prompt => prompt.PromptName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static prompt => prompt.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (prompts.Length == 0)
        {
            return [];
        }

        var shadowedByPath = BuildShadowedPathMap(prompts);

        return prompts
            .Select(prompt =>
            {
                var isShadowed = shadowedByPath.TryGetValue(prompt.SourcePath, out var shadowedBy);
                return ToDescriptor(prompt, isShadowed, shadowedBy);
            })
            .ToArray();
    }

    /// <summary>
    /// Lists effective agent prompts after applying source precedence.
    /// </summary>
    /// <param name="query">Discovery inputs.</param>
    /// <returns>The effective prompts, one per prompt identifier.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<AgentPromptDescriptor> ListEffectivePrompts(AgentPromptCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var roots = ResolveRoots(query);
        return EnumeratePromptRoots(roots)
            .SelectMany(static root => LoadAgentPromptResources(root))
            .GroupBy(static prompt => prompt.PromptName, StringComparer.OrdinalIgnoreCase)
            .Select(static group => ComposeEffectivePrompt(group))
            .OrderBy(static prompt => prompt.IsCopilot)
            .ThenBy(static prompt => prompt.Precedence)
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
    public AgentPromptDescriptor? ResolvePrompt(AgentPromptCatalogQuery query, string? promptName)
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
    /// Resolves the global agent prompt directory for the query.
    /// </summary>
    /// <param name="query">Discovery inputs.</param>
    /// <returns>The absolute global prompt directory path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is <see langword="null"/>.</exception>
    public string ResolveGlobalPromptDirectory(AgentPromptCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ResolveRoots(query).GlobalPromptRoot;
    }

    /// <summary>
    /// Resolves the project-local prompt directory for the query.
    /// </summary>
    /// <param name="query">Discovery inputs.</param>
    /// <returns>The project prompt directory path, or <see langword="null"/> when no project root is available.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is <see langword="null"/>.</exception>
    public string? ResolveProjectPromptDirectory(AgentPromptCatalogQuery query)
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
    public string ResolveGlobalSystemPromptDirectory(AgentPromptCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Path.Combine(ResolveRoots(query).GlobalPromptRoot, "system");
    }

    /// <summary>
    /// Resolves the project-local system prompt directory for the query.
    /// </summary>
    /// <param name="query">Discovery inputs.</param>
    /// <returns>The project system prompt directory path, or <see langword="null"/> when no project root is available.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is <see langword="null"/>.</exception>
    public string? ResolveProjectSystemPromptDirectory(AgentPromptCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var root = ResolveRoots(query).ProjectPromptRoot;
        return root is null ? null : Path.Combine(root, "system");
    }

    /// <summary>Resolves the same explicit roots used by discovery for trusted-backend management.</summary>
    /// <exception cref="ArgumentNullException">The query is null.</exception>
    public SystemPromptContentRoots ResolveRoots(AgentPromptCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _contentLocator.GetRoots(new SystemPromptDiscoveryContext
        {
            AppBaseDirectory = query.AppBaseDirectory,
            UserProfileRoot = query.UserProfileRoot,
            UserCodeAltaRoot = query.UserCodeAltaRoot,
            ProjectRoot = query.ProjectRoot,
            ProjectPromptResourcesTrusted = query.ProjectPromptResourcesTrusted || !string.IsNullOrWhiteSpace(query.ProjectRoot),
        });
    }

    private static IEnumerable<GlobalPromptRoot> EnumeratePromptRoots(SystemPromptContentRoots roots)
    {
        // The custom agents of GitHub Copilot come first: a prompt of CodeAlta with the same id replaces one.
        foreach (var (kind, precedence, folder) in CopilotAgentFiles.Folders(roots))
        {
            yield return new GlobalPromptRoot(kind, precedence, folder);
        }

        if (Directory.Exists(Path.Combine(roots.ShippedPromptRoot, "agents")))
        {
            yield return new GlobalPromptRoot(AgentPromptSourceKind.BuiltIn, 0, Path.Combine(roots.ShippedPromptRoot, "agents"));
        }

        if (Directory.Exists(Path.Combine(roots.GlobalPromptRoot, "agents")))
        {
            yield return new GlobalPromptRoot(AgentPromptSourceKind.UserGlobal, 1, Path.Combine(roots.GlobalPromptRoot, "agents"));
        }

        if (roots.ProjectPromptResourcesTrusted && roots.ProjectPromptRoot is not null && Directory.Exists(Path.Combine(roots.ProjectPromptRoot, "agents")))
        {
            yield return new GlobalPromptRoot(AgentPromptSourceKind.Project, 2, Path.Combine(roots.ProjectPromptRoot, "agents"));
        }
    }

    private static IEnumerable<GlobalPromptRoot> EnumerateSystemPromptRoots(SystemPromptContentRoots roots)
    {
        if (Directory.Exists(Path.Combine(roots.ShippedPromptRoot, "system")))
        {
            yield return new GlobalPromptRoot(AgentPromptSourceKind.BuiltIn, 0, Path.Combine(roots.ShippedPromptRoot, "system"));
        }

        if (Directory.Exists(Path.Combine(roots.GlobalPromptRoot, "system")))
        {
            yield return new GlobalPromptRoot(AgentPromptSourceKind.UserGlobal, 1, Path.Combine(roots.GlobalPromptRoot, "system"));
        }

        if (roots.ProjectPromptResourcesTrusted && roots.ProjectPromptRoot is not null && Directory.Exists(Path.Combine(roots.ProjectPromptRoot, "system")))
        {
            yield return new GlobalPromptRoot(AgentPromptSourceKind.Project, 2, Path.Combine(roots.ProjectPromptRoot, "system"));
        }
    }

    private static IEnumerable<LoadedAgentPromptResource> LoadAgentPromptResources(GlobalPromptRoot root)
    {
        if (root.SourceKind.IsCopilot())
        {
            foreach (var agent in CopilotAgentFiles.ReadAll(root.Path))
            {
                yield return new LoadedAgentPromptResource(agent.Id, agent.Name, agent.Description, null, agent.Body, root.SourceKind, root.Precedence,
                    agent.Path, HashText(agent.Body), PromptCompositionMode.Replace);
            }

            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(root.Path, "*.prompt.md", SearchOption.TopDirectoryOnly))
        {
            if (TryLoadPrompt(root, path, out var descriptor))
            {
                yield return descriptor;
            }
        }
    }

    private static IEnumerable<LoadedSystemPromptResource> LoadSystemPromptResources(GlobalPromptRoot root)
    {
        foreach (var path in Directory.EnumerateFiles(root.Path, "*.system-prompt.md", SearchOption.TopDirectoryOnly))
        {
            if (TryLoadSystemPrompt(root, path, out var descriptor))
            {
                yield return descriptor;
            }
        }
    }

    private static bool TryLoadPrompt(GlobalPromptRoot root, string path, out LoadedAgentPromptResource descriptor)
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

        var (frontmatter, body) = CodeAlta.Catalog.PromptFileFormat.SplitFrontmatter(text);
        var mode = ParseCompositionMode(frontmatter);
        if (mode is null)
        {
            return false;
        }

        var displayName = NormalizeRequiredText(frontmatter.GetValueOrDefault("name"));
        if ((displayName is null && mode == PromptCompositionMode.Replace) || string.IsNullOrWhiteSpace(body))
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

        var systemPromptName = NormalizePromptName(frontmatter.GetValueOrDefault("system"));
        var trimmedBody = body.Trim();
        descriptor = new LoadedAgentPromptResource(
            PromptName: normalizedPromptName,
            DisplayName: displayName,
            Description: NormalizeOptionalText(frontmatter.GetValueOrDefault("description")),
            SystemPromptName: systemPromptName,
            Body: trimmedBody,
            SourceKind: root.SourceKind,
            Precedence: root.Precedence,
            SourcePath: Path.GetFullPath(path),
            ContentHash: HashText(trimmedBody),
            Mode: mode.Value);
        return true;
    }

    private static bool TryLoadSystemPrompt(GlobalPromptRoot root, string path, out LoadedSystemPromptResource descriptor)
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

        var (frontmatter, body) = CodeAlta.Catalog.PromptFileFormat.SplitFrontmatter(text);
        var mode = ParseCompositionMode(frontmatter);
        if (mode is null)
        {
            return false;
        }

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

        descriptor = new LoadedSystemPromptResource(
            PromptName: normalizedPromptName,
            Body: trimmedBody,
            SourceKind: root.SourceKind,
            Precedence: root.Precedence,
            SourcePath: Path.GetFullPath(path),
            ContentHash: HashText(trimmedBody),
            Mode: mode.Value);
        return true;
    }

    private static AgentPromptDescriptor ComposeEffectivePrompt(IEnumerable<LoadedAgentPromptResource> group)
    {
        var ordered = group
            .OrderBy(static prompt => prompt.Precedence)
            .ThenBy(static prompt => prompt.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var chainStart = FindCompositionChainStart(ordered);
        var applied = ordered[chainStart..];
        var top = applied[^1];
        var body = JoinPromptBodies(applied.Select(static prompt => prompt.Body));
        var displayName = LastNonBlank(applied.Select(static prompt => prompt.DisplayName)) ?? top.PromptName;
        var description = LastNonBlank(applied.Select(static prompt => prompt.Description));
        var systemPromptName = LastNonBlank(applied.Select(static prompt => prompt.SystemPromptName)) ?? DefaultPromptName;
        return new AgentPromptDescriptor(
            PromptName: top.PromptName,
            DisplayName: displayName,
            Description: description,
            SystemPromptName: systemPromptName,
            Body: body,
            SourceKind: top.SourceKind,
            Precedence: top.Precedence,
            SourcePath: top.SourcePath,
            ContentHash: HashText(body),
            Mode: top.Mode,
            IsShadowed: false,
            ShadowedByPath: null);
    }

    private static AgentPromptDescriptor ToDescriptor(LoadedAgentPromptResource prompt, bool isShadowed, string? shadowedByPath)
        => new(
            PromptName: prompt.PromptName,
            DisplayName: prompt.DisplayName ?? prompt.PromptName,
            Description: prompt.Description,
            SystemPromptName: prompt.SystemPromptName ?? DefaultPromptName,
            Body: prompt.Body,
            SourceKind: prompt.SourceKind,
            Precedence: prompt.Precedence,
            SourcePath: prompt.SourcePath,
            ContentHash: prompt.ContentHash,
            Mode: prompt.Mode,
            IsShadowed: isShadowed,
            ShadowedByPath: shadowedByPath);

    private static SystemPromptDescriptor ToDescriptor(LoadedSystemPromptResource prompt, bool isShadowed, string? shadowedByPath)
        => new(
            PromptName: prompt.PromptName,
            Body: prompt.Body,
            SourceKind: prompt.SourceKind,
            Precedence: prompt.Precedence,
            SourcePath: prompt.SourcePath,
            ContentHash: prompt.ContentHash,
            Mode: prompt.Mode,
            IsShadowed: isShadowed,
            ShadowedByPath: shadowedByPath);

    private static Dictionary<string, string?> BuildShadowedPathMap<TPrompt>(IReadOnlyList<TPrompt> prompts)
        where TPrompt : ILoadedPromptResource
    {
        var shadowedByPath = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in prompts.GroupBy(static prompt => prompt.PromptName, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group
                .OrderBy(static prompt => prompt.Precedence)
                .ThenBy(static prompt => prompt.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var chainStart = FindCompositionChainStart(ordered);
            if (chainStart <= 0)
            {
                continue;
            }

            var shadowedBy = ordered[chainStart].SourcePath;
            foreach (var prompt in ordered.Take(chainStart))
            {
                shadowedByPath[prompt.SourcePath] = shadowedBy;
            }
        }

        return shadowedByPath;
    }

    private static int FindCompositionChainStart<TPrompt>(IReadOnlyList<TPrompt> prompts)
        where TPrompt : ILoadedPromptResource
    {
        for (var index = prompts.Count - 1; index >= 0; index--)
        {
            if (prompts[index].Mode == PromptCompositionMode.Replace)
            {
                return index;
            }
        }

        return 0;
    }

    private static PromptCompositionMode? ParseCompositionMode(IReadOnlyDictionary<string, string> frontmatter)
        => CodeAlta.Catalog.PromptFileFormat.ParseAppend(frontmatter) switch
        {
            true => PromptCompositionMode.Append,
            false => PromptCompositionMode.Replace,
            null => null,
        };

    private static string JoinPromptBodies(IEnumerable<string> bodies)
    {
        var builder = new StringBuilder();
        foreach (var body in bodies)
        {
            var trimmed = body.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine();
            }

            builder.Append(trimmed);
        }

        return builder.ToString();
    }

    private static string? LastNonBlank(IEnumerable<string?> values)
        => values.LastOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

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

    private sealed record GlobalPromptRoot(AgentPromptSourceKind SourceKind, int Precedence, string Path);

    private interface ILoadedPromptResource
    {
        string PromptName { get; }
        AgentPromptSourceKind SourceKind { get; }
        int Precedence { get; }
        string SourcePath { get; }
        PromptCompositionMode Mode { get; }
    }

    private sealed record LoadedAgentPromptResource(
        string PromptName,
        string? DisplayName,
        string? Description,
        string? SystemPromptName,
        string Body,
        AgentPromptSourceKind SourceKind,
        int Precedence,
        string SourcePath,
        string ContentHash,
        PromptCompositionMode Mode) : ILoadedPromptResource;

    private sealed record LoadedSystemPromptResource(
        string PromptName,
        string Body,
        AgentPromptSourceKind SourceKind,
        int Precedence,
        string SourcePath,
        string ContentHash,
        PromptCompositionMode Mode) : ILoadedPromptResource;
}

/// <summary>
/// Inputs used when discovering agent prompts.
/// </summary>
public sealed class AgentPromptCatalogQuery
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
/// Identifies the source location for a discovered agent prompt.
/// </summary>
public enum AgentPromptSourceKind
{
    /// <summary>The prompt is shipped with CodeAlta.</summary>
    BuiltIn,

    /// <summary>The prompt comes from the user-global <c>~/.alta/prompts/agents</c> root.</summary>
    UserGlobal,

    /// <summary>The prompt comes from the project-local <c>.alta/prompts/agents</c> root.</summary>
    Project,

    /// <summary>The prompt is a custom agent of GitHub Copilot of the user (<c>~/.copilot/agents</c>).</summary>
    CopilotUser,

    /// <summary>The prompt is a custom agent of GitHub Copilot of the project (<c>.github/agents</c>).</summary>
    CopilotProject,
}

/// <summary>Questions about a <see cref="AgentPromptSourceKind"/>.</summary>
public static class AgentPromptSourceKindExtensions
{
    /// <summary>Gets whether the prompt is a custom agent of GitHub Copilot.</summary>
    public static bool IsCopilot(this AgentPromptSourceKind kind) => kind is AgentPromptSourceKind.CopilotUser or AgentPromptSourceKind.CopilotProject;
}

/// <summary>
/// Describes how a prompt file composes with lower-precedence files that have the same prompt id.
/// </summary>
public enum PromptCompositionMode
{
    /// <summary>The prompt file replaces lower-precedence files with the same id.</summary>
    Replace,

    /// <summary>The prompt file appends its body to the nearest lower-precedence replacement chain with the same id.</summary>
    Append,
}

/// <summary>
/// Describes a discovered agent prompt resource.
/// </summary>
/// <param name="PromptName">The file-derived prompt identifier.</param>
/// <param name="DisplayName">The required display name from frontmatter.</param>
/// <param name="Description">The optional prompt description.</param>
/// <param name="SystemPromptName">The system prompt selected by this agent prompt.</param>
/// <param name="Body">The prompt body.</param>
/// <param name="SourceKind">The prompt source kind.</param>
/// <param name="Precedence">The source precedence where larger values compose after smaller values and replace them by default.</param>
/// <param name="SourcePath">The absolute source file path.</param>
/// <param name="ContentHash">The SHA-256 content hash.</param>
/// <param name="Mode">How the prompt file composes with lower-precedence files with the same id.</param>
/// <param name="IsShadowed">Whether a higher-precedence prompt with the same name overrides this prompt.</param>
/// <param name="ShadowedByPath">The overriding prompt path, when shadowed.</param>
public sealed record AgentPromptDescriptor(
    string PromptName,
    string DisplayName,
    string? Description,
    string SystemPromptName,
    string Body,
    AgentPromptSourceKind SourceKind,
    int Precedence,
    string SourcePath,
    string ContentHash,
    PromptCompositionMode Mode,
    bool IsShadowed,
    string? ShadowedByPath)
{
    /// <summary>Gets a value indicating whether the prompt is built into CodeAlta.</summary>
    public bool IsBuiltIn => SourceKind == AgentPromptSourceKind.BuiltIn;

    /// <summary>Gets a value indicating whether the prompt is a custom agent of GitHub Copilot, which CodeAlta reads and does not write.</summary>
    public bool IsCopilot => SourceKind.IsCopilot();
}

/// <summary>
/// Describes a discovered system prompt resource.
/// </summary>
/// <param name="PromptName">The file-derived system prompt identifier.</param>
/// <param name="Body">The system prompt body.</param>
/// <param name="SourceKind">The prompt source kind.</param>
/// <param name="Precedence">The source precedence where larger values compose after smaller values and replace them by default.</param>
/// <param name="SourcePath">The absolute source file path.</param>
/// <param name="ContentHash">The SHA-256 content hash.</param>
/// <param name="Mode">How the system prompt file composes with lower-precedence files with the same id.</param>
/// <param name="IsShadowed">Whether a higher-precedence system prompt with the same name overrides this prompt.</param>
/// <param name="ShadowedByPath">The overriding prompt path, when shadowed.</param>
public sealed record SystemPromptDescriptor(
    string PromptName,
    string Body,
    AgentPromptSourceKind SourceKind,
    int Precedence,
    string SourcePath,
    string ContentHash,
    PromptCompositionMode Mode,
    bool IsShadowed,
    string? ShadowedByPath)
{
    /// <summary>Gets a value indicating whether the prompt is built into CodeAlta.</summary>
    public bool IsBuiltIn => SourceKind == AgentPromptSourceKind.BuiltIn;
}
