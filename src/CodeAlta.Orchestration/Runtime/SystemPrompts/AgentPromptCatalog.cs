using System.Security.Cryptography;
using System.Text;
using CodeAlta.Catalog;

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
        var chains = LoadChains(ResolveRoots(query));
        return chains.Values
            .Select(chain => ComposeEffectivePrompt(chain, Linearize(chains, chain.Applied[^1].PromptName, chain.Inherits)))
            .OrderBy(static prompt => prompt.IsCopilot)
            .ThenBy(static prompt => prompt.Precedence)
            .ThenBy(static prompt => prompt.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static prompt => prompt.PromptName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Resolves the agent prompts a prompt is made of (<c>inherit</c>): each inherited prompt as it is in effect, after
    /// the prompts it inherits itself, each once, in the order their text comes before the text of the prompt.
    /// </summary>
    /// <param name="query">Discovery inputs.</param>
    /// <param name="promptName">The id of the prompt that inherits, which none of them may inherit back; <see langword="null"/> for a prompt not saved yet.</param>
    /// <param name="inherits">The ids the prompt inherits, in its order.</param>
    /// <returns>The inherited prompts in order, and the ids that are missing or that would inherit in a circle.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> or <paramref name="inherits"/> is <see langword="null"/>.</exception>
    public AgentPromptInheritance ResolveInheritance(AgentPromptCatalogQuery query, string? promptName, IReadOnlyList<string> inherits)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(inherits);
        var chains = LoadChains(ResolveRoots(query));
        var self = NormalizePromptName(promptName) ?? string.Empty;
        var linear = Linearize(chains, self, inherits);
        var prompts = linear.Order
            .Where(id => !string.Equals(id, self, StringComparison.OrdinalIgnoreCase))
            .Select(id =>
            {
                var applied = chains[id].Applied;
                var top = applied[^1];
                return new AgentPromptInheritedPrompt(top.PromptName, LastNonBlank(applied.Select(static prompt => prompt.DisplayName)) ?? top.PromptName,
                    top.SourceKind, JoinPromptBodies(applied.Select(static prompt => prompt.Body)));
            })
            .ToArray();
        var files = prompts.SelectMany(prompt => chains[prompt.PromptName].Applied).ToArray();
        return new AgentPromptInheritance(prompts, linear.Missing, linear.Circular)
        {
            SystemPromptName = LastNonBlank(files.Select(static prompt => prompt.SystemPromptName)),
            DisabledTools = [.. files.SelectMany(static prompt => prompt.DisabledTools).Distinct(StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// Keeps the prompts a space has. A prompt is assigned to spaces by its id, whatever folder it comes from: a
    /// project cannot bring into a space a prompt the space does not have.
    /// </summary>
    /// <param name="prompts">The prompts.</param>
    /// <param name="space">What the space of the session has; <see langword="null"/> to keep every prompt.</param>
    /// <returns>The prompts available in the space, in their order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="prompts"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<AgentPromptDescriptor> InSpace(IReadOnlyList<AgentPromptDescriptor> prompts, SpaceFilter? space)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        return space is null ? prompts : [.. prompts.Where(prompt => space.Allows(SpaceResourceKind.Prompt, prompt.PromptName))];
    }

    /// <summary>
    /// Chooses the agent prompt of a new session: the one asked for, else the one its space starts new sessions with
    /// when the space has it, else <see cref="DefaultPromptName"/> when the space has it, else the first one it has.
    /// </summary>
    /// <param name="requested">The prompt the user or the caller chose; <see langword="null"/> when none was chosen.</param>
    /// <param name="available">The ids of the prompts available in the space, in their order.</param>
    /// <param name="space">The space of the new session; <see langword="null"/> when it is not known.</param>
    /// <returns>The id of the prompt; <see cref="DefaultPromptName"/> when nothing else fits. A chosen prompt is returned as it is: its caller checks it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="available"/> is <see langword="null"/>.</exception>
    public static string ResolveNewSessionPromptId(string? requested, IReadOnlyList<string> available, SpaceDescriptor? space)
    {
        ArgumentNullException.ThrowIfNull(available);
        if (NormalizePromptName(requested) is { } chosen)
        {
            return chosen;
        }

        string? Find(string? id)
            => id is null ? null : available.FirstOrDefault(candidate => string.Equals(candidate, id, StringComparison.OrdinalIgnoreCase));
        return Find(space?.Settings.NewSessions?.AgentPrompt) ?? Find(DefaultPromptName) ?? available.FirstOrDefault() ?? DefaultPromptName;
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
            Mode: mode.Value)
        {
            DisabledTools = CodeAlta.Catalog.PromptFileFormat.ParseDisabledTools(frontmatter),
            Inherits = CodeAlta.Catalog.PromptFileFormat.ParseInherits(frontmatter),
        };
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

    // The files of each agent prompt id that are in effect: the last one that replaces, then the ones that add to it.
    private static Dictionary<string, PromptChain> LoadChains(SystemPromptContentRoots roots)
        => EnumeratePromptRoots(roots)
            .SelectMany(static root => LoadAgentPromptResources(root))
            .GroupBy(static prompt => prompt.PromptName, StringComparer.OrdinalIgnoreCase)
            .Select(static group =>
            {
                var ordered = group
                    .OrderBy(static prompt => prompt.Precedence)
                    .ThenBy(static prompt => prompt.SourcePath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var applied = ordered[FindCompositionChainStart(ordered)..];
                return new PromptChain(applied, [.. applied.SelectMany(static prompt => prompt.Inherits).Distinct(StringComparer.OrdinalIgnoreCase)]);
            })
            .ToDictionary(static chain => chain.Applied[^1].PromptName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Largest depth of prompts that inherit prompts that inherit.</summary>
    private const int MaximumInheritanceDepth = 16;

    // The prompts a prompt is made of, in the order of their text: the prompts each inherited one inherits come before
    // it, a prompt met twice comes once, at its first place, and the prompt itself comes last. A prompt that would
    // inherit one of the prompts that inherit it is left out.
    private static Linearization Linearize(Dictionary<string, PromptChain> chains, string self, IReadOnlyList<string> inherits)
    {
        var order = new List<string>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { self };
        var missing = new List<string>();
        var circular = new List<string>();
        void Visit(string id, int depth)
        {
            if (placed.Contains(id)) return;
            if (visiting.Contains(id) || depth > MaximumInheritanceDepth)
            {
                if (!circular.Contains(id, StringComparer.OrdinalIgnoreCase)) circular.Add(id);
                return;
            }

            if (!chains.TryGetValue(id, out var chain))
            {
                if (!missing.Contains(id, StringComparer.OrdinalIgnoreCase)) missing.Add(id);
                return;
            }

            visiting.Add(id);
            foreach (var inherited in chain.Inherits) Visit(inherited, depth + 1);
            visiting.Remove(id);
            placed.Add(id);
            order.Add(chain.Applied[^1].PromptName);
        }

        foreach (var id in inherits) Visit(id, 1);
        order.Add(self);
        return new Linearization(order, missing, circular, chains);
    }

    private static AgentPromptDescriptor ComposeEffectivePrompt(PromptChain chain, Linearization linear)
    {
        var applied = chain.Applied;
        var top = applied[^1];
        // The files of the inherited prompts, then those of the prompt: their texts in that order, and the last
        // system prompt that one of them names.
        var composed = linear.Order
            .Where(id => !string.Equals(id, top.PromptName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(id => linear.Chains(id))
            .Concat(applied)
            .ToArray();
        var body = JoinPromptBodies(composed.Select(static prompt => prompt.Body));
        var displayName = LastNonBlank(applied.Select(static prompt => prompt.DisplayName)) ?? top.PromptName;
        var description = LastNonBlank(applied.Select(static prompt => prompt.Description));
        var systemPromptName = LastNonBlank(composed.Select(static prompt => prompt.SystemPromptName)) ?? DefaultPromptName;
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
            ShadowedByPath: null)
        {
            // A file that adds to another one adds to what it turns off too, and so does a prompt it inherits.
            DisabledTools = [.. composed.SelectMany(static prompt => prompt.DisabledTools).Distinct(StringComparer.Ordinal)],
            Inherits = chain.Inherits,
            InheritedPrompts = [.. linear.Order.Where(id => !string.Equals(id, top.PromptName, StringComparison.OrdinalIgnoreCase))],
        };
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
            ShadowedByPath: shadowedByPath)
        {
            DisabledTools = prompt.DisabledTools,
            Inherits = prompt.Inherits,
        };

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
        PromptCompositionMode Mode) : ILoadedPromptResource
    {
        public IReadOnlyList<string> DisabledTools { get; init; } = [];

        public IReadOnlyList<string> Inherits { get; init; } = [];
    }

    private sealed record PromptChain(LoadedAgentPromptResource[] Applied, IReadOnlyList<string> Inherits);

    private sealed record Linearization(IReadOnlyList<string> Order, IReadOnlyList<string> Missing, IReadOnlyList<string> Circular,
        Dictionary<string, PromptChain> Source)
    {
        public IEnumerable<LoadedAgentPromptResource> Chains(string id) => Source.TryGetValue(id, out var chain) ? chain.Applied : [];
    }

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

    /// <summary>
    /// Gets the tools a session of this prompt does without (<c>disabled_tools</c> of its front matter), with those of
    /// the files it adds to.
    /// </summary>
    public IReadOnlyList<string> DisabledTools { get; init; } = [];

    /// <summary>Gets a value indicating whether the prompt is a custom agent of GitHub Copilot, which CodeAlta reads and does not write.</summary>
    public bool IsCopilot => SourceKind.IsCopilot();

    /// <summary>
    /// Gets the ids of the agent prompts this one inherits (<c>inherit</c> of its front matter), with those of the files
    /// it adds to; empty for none.
    /// </summary>
    public IReadOnlyList<string> Inherits { get; init; } = [];

    /// <summary>
    /// Gets the ids of the agent prompts an effective prompt is made of, in the order their text comes before its own:
    /// the prompts it inherits, after those they inherit themselves. Empty for a prompt of one file.
    /// </summary>
    public IReadOnlyList<string> InheritedPrompts { get; init; } = [];
}

/// <summary>The agent prompts a prompt inherits, as <see cref="AgentPromptCatalog.ResolveInheritance"/> finds them.</summary>
/// <param name="Prompts">The inherited prompts, in the order of their text.</param>
/// <param name="Missing">The inherited ids that no prompt has.</param>
/// <param name="Circular">The inherited ids left out because they would inherit the prompt that inherits them.</param>
public sealed record AgentPromptInheritance(IReadOnlyList<AgentPromptInheritedPrompt> Prompts, IReadOnlyList<string> Missing, IReadOnlyList<string> Circular)
{
    /// <summary>Gets the system prompt the inherited prompts choose, the last one that one of their files names; <see langword="null"/> for none.</summary>
    public string? SystemPromptName { get; init; }

    /// <summary>Gets the tools the inherited prompts turn off.</summary>
    public IReadOnlyList<string> DisabledTools { get; init; } = [];
}

/// <summary>One agent prompt that another one inherits.</summary>
/// <param name="PromptName">The id of the prompt.</param>
/// <param name="DisplayName">The name it is shown with.</param>
/// <param name="SourceKind">Where the file in effect for it is kept.</param>
/// <param name="Body">Its own text, with the files that add to it, without the text of the prompts it inherits itself.</param>
public sealed record AgentPromptInheritedPrompt(string PromptName, string DisplayName, AgentPromptSourceKind SourceKind, string Body);

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
