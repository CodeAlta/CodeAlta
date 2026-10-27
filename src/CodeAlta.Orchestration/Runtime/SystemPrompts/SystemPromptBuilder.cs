using System.Collections.Frozen;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime.SystemPrompts;

/// <summary>
/// Builds file-backed CodeAlta system prompt bundles.
/// </summary>
public sealed class SystemPromptBuilder
{
    /// <summary>The trailer that names CodeAlta as a co-author of a commit of its agents.</summary>
    public const string CoAuthorTrailer = "Co-authored-by: CodeAlta <noreply@codealta.io>";

    // The line of the Runtime Context that asks for the trailer, beside those the harness of a provider asks for.
    internal const string CoAuthorLine = $"- Commit messages: end the message of every git commit you create with the trailer `{CoAuthorTrailer}`, in its last paragraph with the other trailers. Add it beside the co-author or attribution lines your own tools ask for, never in place of them.";

    private const string DefaultName = "default";
    private const int MaximumSpaceDescriptionLength = 300;
    private static readonly string[] KnownTopLevelEntries = ["system", "agents"];
    private readonly ISystemPromptContentLocator _contentLocator;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemPromptBuilder"/> class.
    /// </summary>
    /// <param name="contentLocator">Content locator used to resolve prompt roots.</param>
    public SystemPromptBuilder(ISystemPromptContentLocator? contentLocator = null)
    {
        _contentLocator = contentLocator ?? new FileSystemPromptContentLocator();
    }

    /// <summary>
    /// Builds a system prompt bundle from file-backed resources and generated runtime parts.
    /// </summary>
    /// <param name="request">Build inputs.</param>
    /// <returns>The composed prompt bundle.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when required prompt content is missing or invalid.</exception>
    /// <exception cref="ArgumentException">A supplied scoped working or project path is invalid or outside the instruction boundary.</exception>
    public SystemPromptBundle Build(SystemPromptBuildRequest request)
        => Build(request, new AgentInstructionFileReader());

    internal SystemPromptBundle Build(SystemPromptBuildRequest request, AgentInstructionFileReader instructionFiles)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(instructionFiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProviderKey);
        ArgumentNullException.ThrowIfNull(request.Session);
        ValidateDiscoveryPaths(request);

        var diagnostics = new List<SystemPromptDiagnostic>();
        var projectRoot = NormalizeOptionalRoot(request.Project?.ProjectPath ?? FirstNonBlank(request.ProjectRoots));
        var roots = _contentLocator.GetRoots(new SystemPromptDiscoveryContext
        {
            UserProfileRoot = UserHomeOf(request),
            UserCodeAltaRoot = request.UserCodeAltaRoot,
            ProjectRoot = projectRoot,
            ProjectPromptResourcesTrusted = projectRoot is not null,
        });

        ValidateRoots(roots, diagnostics);
        WarnForIgnoredFiles(roots, diagnostics);

        var composition = ResolveCompositionRequest(request);
        var promptResolution = ResolveResource(roots, "agents", ".prompt.md", composition.AgentPromptName, diagnostics, SystemPromptResourceKind.AgentPrompt);
        if (promptResolution.Selected is null && !string.Equals(composition.AgentPromptName, DefaultName, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(SystemPromptDiagnostic.Warning("agent_prompt_fallback_default", $"Agent prompt 'agents/{composition.AgentPromptName}.prompt.md' was not found; using the default agent prompt.", null));
            promptResolution = ResolveResource(roots, "agents", ".prompt.md", DefaultName, diagnostics, SystemPromptResourceKind.AgentPrompt);
            composition = composition with { AgentPromptName = DefaultName, AgentPromptReason = "fallback-default" };
        }

        if (promptResolution.Selected is null)
        {
            throw new InvalidOperationException($"Missing required agent prompt 'agents/{composition.AgentPromptName}.prompt.md'.");
        }

        // The agent prompts the selected one inherits come first, each as it is in effect; the files of the selected
        // prompt come last, so that what they set wins.
        var inherited = ResolveInherited(roots, composition.AgentPromptName, promptResolution, diagnostics);
        var composedApplied = inherited.SelectMany(static prompt => prompt.Resolution.Applied).Concat(promptResolution.Applied).ToArray();

        // What the session does without: what the host says (the user's settings, the space), and what the agent
        // prompt turns off, with the files it adds to and the prompts it inherits.
        var disabledTools = new HashSet<string>(request.DisabledTools ?? new HashSet<string>(), StringComparer.Ordinal);
        foreach (var applied in composedApplied)
        {
            disabledTools.UnionWith(applied.Resource.DisabledTools);
        }

        var selectedSystemOverride = NormalizeName(request.SelectedBaseName);
        var agentSystemPromptName = LastNonBlank(composedApplied.Select(static resource => resource.Resource.SystemPromptName));
        var selectedSystemName = selectedSystemOverride ?? agentSystemPromptName ?? DefaultName;
        var selectedSystemReason = selectedSystemOverride is not null
            ? "runtime"
            : agentSystemPromptName is not null
                ? $"agent:{composition.AgentPromptName}"
                : "default-name";
        var effectivePartOptions = SystemPromptPartOptions.Default;
        foreach (var applied in composedApplied)
        {
            effectivePartOptions = applied.Resource.PartOptions.MergeOver(effectivePartOptions);
        }

        effectivePartOptions = request.PartOptionsOverride?.MergeOver(effectivePartOptions) ?? effectivePartOptions;
        var systemResolution = ResolveResource(roots, "system", ".system-prompt.md", selectedSystemName, diagnostics, SystemPromptResourceKind.SystemPrompt);

        if (systemResolution.Selected is null)
        {
            throw new InvalidOperationException($"Missing required system prompt 'system/{selectedSystemName}.system-prompt.md'.");
        }

        var parts = new List<SystemPromptManifestPart>();
        var systemMessage = PromptToolConditions.Apply(systemResolution.Body!, disabledTools).Trim();
        foreach (var applied in systemResolution.Applied)
        {
            parts.Add(CreateResourcePart(applied.Resource, "system", selectedSystemName, "system", 100, applied.Status, applied.Replaces));
        }

        foreach (var skipped in systemResolution.Skipped)
        {
            parts.Add(CreateResourcePart(skipped.Resource, "system", selectedSystemName, "system", 100, skipped.Status, null));
        }

        var developerParts = new List<RenderedPromptPart>();
        foreach (var (name, resolution) in inherited)
        {
            foreach (var applied in resolution.Applied)
            {
                parts.Add(CreateResourcePart(applied.Resource, "agent_prompt", name, "developer", 300, applied.Status == "selected" ? "inherited" : applied.Status, applied.Replaces));
            }
        }

        foreach (var applied in promptResolution.Applied)
        {
            parts.Add(CreateResourcePart(applied.Resource, "agent_prompt", composition.AgentPromptName, "developer", 300, applied.Status, applied.Replaces));
        }

        var agentPromptBody = JoinPromptBodies(inherited.Select(static prompt => prompt.Resolution.Body!).Append(promptResolution.Body!));
        developerParts.Add(new RenderedPromptPart($"agents/{composition.AgentPromptName}", RenderSection("Agent Prompt", PromptToolConditions.Apply(agentPromptBody, disabledTools))));
        foreach (var skipped in promptResolution.Skipped)
        {
            parts.Add(CreateResourcePart(skipped.Resource, "agent_prompt", composition.AgentPromptName, "developer", 300, skipped.Status, null));
        }

        composition = composition with { SystemPromptName = selectedSystemName, SystemPromptReason = selectedSystemReason, PartOptions = effectivePartOptions };

        if (composition.PartOptions.RuntimeContext)
        {
            AddGeneratedPart(developerParts, parts, "runtime.context", "runtime_context", "Runtime Context", 400, BuildRuntimeContext(request, projectRoot, roots, disabledTools));
        }

        if (composition.PartOptions.ToolGuidance)
        {
            AddGeneratedPart(developerParts, parts, "tool.guidance", "tool_guidance", "Tool Guidance", 500, BuildToolGuidance(request));
            if (BuildCodeModeGuidance(request, disabledTools) is { } codeMode)
            {
                AddGeneratedPart(developerParts, parts, "tool.codemode", "code_mode", "Code Mode", 510, codeMode);
            }

            var agentPromptGuidance = BuildAgentPromptGuidance(request, projectRoot, composition.AgentPromptName);
            if (!string.IsNullOrWhiteSpace(agentPromptGuidance))
            {
                AddGeneratedPart(developerParts, parts, "prompt.discovery", "agent_prompts", "Agent Prompts", 525, agentPromptGuidance);
            }
        }

        if (composition.PartOptions.Skills && !string.IsNullOrWhiteSpace(request.AvailableSkillsMarkdown))
        {
            AddGeneratedPart(developerParts, parts, "skills.available", "available_skills", "Available Skills", 550, request.AvailableSkillsMarkdown!);
        }

        if (composition.PartOptions.Skills && !string.IsNullOrWhiteSpace(request.ActiveSkillsMarkdown))
        {
            AddGeneratedPart(developerParts, parts, "skills.active", "active_skills", "Active Skills", 575, request.ActiveSkillsMarkdown!);
        }

        if (composition.PartOptions.ProjectContext)
        {
            var projectContext = BuildProjectContext(request, projectRoot, diagnostics, instructionFiles, out var projectContextFiles);
            if (!string.IsNullOrWhiteSpace(projectContext))
            {
                var projectPart = AddGeneratedPart(developerParts, parts, "project.context", "project_context", "Project Context", 600, projectContext);
                projectPart = projectPart with { SourcePaths = projectContextFiles };
                parts[^1] = projectPart;
            }
        }

        diagnostics.AddRange(instructionFiles.Diagnostics.Select(static diagnostic =>
            SystemPromptDiagnostic.Warning("unreadable_project_context_file", diagnostic.Message, diagnostic.Path)));

        var developerInstructions = developerParts.Count == 0
            ? null
            : string.Join(Environment.NewLine + Environment.NewLine, developerParts.Select(static part => part.Markdown.Trim()));
        var statistics = SystemPromptStatistics.FromMessages(systemMessage, developerInstructions, parts);
        var effectiveHash = ComputeHash(systemMessage, developerInstructions, request.ProviderKey, request.ProviderType, request.ProtocolFamily, request.Model, "native-system-and-developer");
        var provider = new SystemPromptProviderMapping(
            request.ProviderKey,
            request.ProviderType,
            request.ProtocolFamily,
            request.Model,
            "native-system-and-developer",
            AppliedToProvider: true,
            Lossy: false,
            Notes: []);
        var manifest = new SystemPromptManifest(
            Version: 1,
            PromptId: CreatePromptId(effectiveHash),
            EffectivePromptHash: effectiveHash,
            Composition: composition.ToManifest(),
            Provider: provider,
            Parts: parts,
            Statistics: statistics,
            Diagnostics: diagnostics);

        return new SystemPromptBundle
        {
            SystemMessage = systemMessage,
            DeveloperInstructions = developerInstructions,
            EffectivePromptHash = effectiveHash,
            Manifest = manifest,
            Statistics = statistics,
            Diagnostics = diagnostics,
            DisabledTools = disabledTools.ToFrozenSet(StringComparer.Ordinal),
        };
    }

    private static void ValidateRoots(SystemPromptContentRoots roots, List<SystemPromptDiagnostic> diagnostics)
    {
        if (!Directory.Exists(roots.ShippedPromptRoot))
        {
            diagnostics.Add(SystemPromptDiagnostic.Error("missing_shipped_content_root", $"Shipped prompt root '{roots.ShippedPromptRoot}' was not found.", roots.ShippedPromptRoot));
            throw new InvalidOperationException($"Shipped prompt root '{roots.ShippedPromptRoot}' was not found.");
        }

        var systemPath = Path.Combine(roots.ShippedPromptRoot, "system", "default.system-prompt.md");
        if (!File.Exists(systemPath))
        {
            diagnostics.Add(SystemPromptDiagnostic.Error("missing_shipped_system", $"Required shipped system prompt '{systemPath}' was not found.", systemPath));
            throw new InvalidOperationException($"Required shipped system prompt '{systemPath}' was not found.");
        }

        var promptPath = Path.Combine(roots.ShippedPromptRoot, "agents", "default.prompt.md");
        if (!File.Exists(promptPath))
        {
            diagnostics.Add(SystemPromptDiagnostic.Error("missing_shipped_prompt", $"Required shipped default agent prompt '{promptPath}' was not found.", promptPath));
            throw new InvalidOperationException($"Required shipped default agent prompt '{promptPath}' was not found.");
        }
    }

    private static void WarnForIgnoredFiles(SystemPromptContentRoots roots, List<SystemPromptDiagnostic> diagnostics)
    {
        foreach (var root in EnumerateExistingRoots(roots))
        {
            foreach (var directory in Directory.EnumerateDirectories(root.Path))
            {
                var name = Path.GetFileName(directory);
                if (!string.Equals(name, "system", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "agents", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(SystemPromptDiagnostic.Warning("ignored_unknown_prompt_folder", $"Ignoring unknown prompt resource folder '{directory}'.", directory));
                }
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(root.Path))
            {
                if (KnownTopLevelEntries.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (File.Exists(entry))
                {
                    diagnostics.Add(SystemPromptDiagnostic.Warning("ignored_unknown_prompt_file", $"Ignoring unknown prompt resource file '{entry}'.", entry));
                }
            }

            WarnWrongSuffix(root.Path, "system", ".system-prompt.md", diagnostics);
            WarnWrongSuffix(root.Path, "agents", ".prompt.md", diagnostics);
        }
    }

    private static void WarnWrongSuffix(string root, string folder, string suffix, List<SystemPromptDiagnostic> diagnostics)
    {
        var directory = Path.Combine(root, folder);
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if (!file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(SystemPromptDiagnostic.Warning("ignored_wrong_suffix", $"Ignoring prompt resource file with wrong suffix '{file}'.", file));
            }
        }
    }

    private static SystemPromptResolvedComposition ResolveCompositionRequest(SystemPromptBuildRequest request)
    {
        var selectedPromptName = NormalizeName(request.SelectedPromptName) ?? NormalizeName(request.SelectedInstructionName);
        return new SystemPromptResolvedComposition(
            SystemPromptName: DefaultName,
            SystemPromptReason: "default-name",
            AgentPromptName: selectedPromptName ?? DefaultName,
            AgentPromptReason: selectedPromptName is null ? "default-name" : "runtime",
            PartOptions: SystemPromptPartOptions.Default);
    }

    private static bool? ParseFrontmatterBool(string value, string field, string path, List<SystemPromptDiagnostic> diagnostics, ref bool hasErrors)
    {
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        diagnostics.Add(SystemPromptDiagnostic.Error("invalid_agent_prompt_frontmatter", $"Agent prompt '{path}' frontmatter field '{field}' must be true or false.", path));
        hasErrors = true;
        return null;
    }

    private static PromptCompositionMode? ParsePromptCompositionMode(IReadOnlyDictionary<string, string> frontmatter, string path, List<SystemPromptDiagnostic> diagnostics, ref bool hasErrors)
    {
        bool? appendFlag = null;
        if (frontmatter.TryGetValue("append", out var appendValue))
        {
            if (bool.TryParse(appendValue, out var append))
            {
                appendFlag = append;
            }
            else
            {
                diagnostics.Add(SystemPromptDiagnostic.Error("invalid_prompt_frontmatter", $"Prompt resource '{path}' frontmatter field 'append' must be true or false.", path));
                hasErrors = true;
                return null;
            }
        }

        PromptCompositionMode? mode = null;
        if (frontmatter.TryGetValue("mode", out var modeValue))
        {
            mode = NormalizeName(modeValue)?.ToLowerInvariant() switch
            {
                null or "replace" => PromptCompositionMode.Replace,
                "append" => PromptCompositionMode.Append,
                _ => null,
            };
            if (mode is null)
            {
                diagnostics.Add(SystemPromptDiagnostic.Error("invalid_prompt_frontmatter", $"Prompt resource '{path}' frontmatter field 'mode' must be 'replace' or 'append'.", path));
                hasErrors = true;
                return null;
            }
        }

        if (appendFlag is not null && mode is not null && appendFlag.Value != (mode.Value == PromptCompositionMode.Append))
        {
            diagnostics.Add(SystemPromptDiagnostic.Error("invalid_prompt_frontmatter", $"Prompt resource '{path}' frontmatter fields 'append' and 'mode' conflict.", path));
            hasErrors = true;
            return null;
        }

        return mode ?? (appendFlag == true ? PromptCompositionMode.Append : PromptCompositionMode.Replace);
    }

    /// <summary>Largest depth of agent prompts that inherit prompts that inherit.</summary>
    private const int MaximumInheritanceDepth = 16;

    // The agent prompts the selected one inherits (`inherit`), in the order of their text: the prompts each one
    // inherits come before it, and a prompt met twice comes once, at its first place. A prompt that would inherit the
    // prompt that inherits it, or one that no file has, is left out with a warning.
    private static IReadOnlyList<(string Name, ResourceResolution Resolution)> ResolveInherited(SystemPromptContentRoots roots, string self,
        ResourceResolution selected, List<SystemPromptDiagnostic> diagnostics)
    {
        var order = new List<(string Name, ResourceResolution Resolution)>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { self };
        void Visit(string name, int depth)
        {
            if (placed.Contains(name)) return;
            if (visiting.Contains(name) || depth > MaximumInheritanceDepth)
            {
                diagnostics.Add(SystemPromptDiagnostic.Warning("circular_inherited_prompt", $"Agent prompt '{name}' is not inherited again: it inherits the prompt that inherits it.", null));
                return;
            }

            var resolution = ResolveResource(roots, "agents", ".prompt.md", name, diagnostics, SystemPromptResourceKind.AgentPrompt, required: false);
            if (resolution.Selected is null)
            {
                diagnostics.Add(SystemPromptDiagnostic.Warning("missing_inherited_prompt", $"Inherited agent prompt 'agents/{name}.prompt.md' was not found.", null));
                return;
            }

            visiting.Add(name);
            foreach (var inherited in Inherits(resolution)) Visit(inherited, depth + 1);
            visiting.Remove(name);
            placed.Add(name);
            order.Add((name, resolution));
        }

        foreach (var name in Inherits(selected)) Visit(name, 1);
        return order;

        static IEnumerable<string> Inherits(ResourceResolution resolution)
            => resolution.Applied.SelectMany(static applied => applied.Resource.Inherits).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static ResourceResolution ResolveResource(SystemPromptContentRoots roots, string folder, string suffix, string name, List<SystemPromptDiagnostic> diagnostics, SystemPromptResourceKind resourceKind,
        bool required = true)
    {
        var candidates = EnumerateExistingRoots(roots)
            .Select(root => LoadResource(root, folder, suffix, name, diagnostics, resourceKind))
            .Concat(resourceKind == SystemPromptResourceKind.AgentPrompt ? CopilotAgents(roots, name) : [])
            .Where(static resource => resource is not null)
            .Cast<PromptResource>()
            .OrderBy(static resource => resource.Precedence)
            .ToArray();
        if (candidates.Length == 0)
        {
            if (required) diagnostics.Add(SystemPromptDiagnostic.Error("missing_prompt_resource", $"Prompt resource '{folder}/{name}{suffix}' was not found.", null));
            return new ResourceResolution(null, null, [], []);
        }

        var selected = candidates[^1];
        if (!string.Equals(selected.SourceKind, "built-in", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(SystemPromptDiagnostic.Warning($"active_{folder}_override", $"Active {folder} resource comes from {selected.SourceKind}: {selected.Path}", selected.Path));
        }

        var chainStart = FindCompositionChainStart(candidates);
        var applied = candidates.Skip(chainStart)
            .Select((resource, index) => new AppliedResource(
                resource,
                index == 0 ? "selected" : "appended",
                index == 0 && chainStart > 0 ? candidates[chainStart - 1].Path : null))
            .ToArray();
        var skipped = candidates.Take(chainStart)
            .Select(static resource => new SkippedResource(resource, "replaced"))
            .ToArray();
        var body = JoinPromptBodies(applied.Select(static resource => resource.Resource.Body));
        return new ResourceResolution(selected, body, applied, skipped);
    }

    // The custom agents of GitHub Copilot with this id (`.github/agents`, `~/.copilot/agents`), below every prompt of
    // CodeAlta: the text of the file is the agent prompt.
    private static IEnumerable<PromptResource?> CopilotAgents(SystemPromptContentRoots roots, string name)
    {
        foreach (var (kind, precedence, folder) in CopilotAgentFiles.Folders(roots))
        {
            if (CopilotAgentFiles.Read(folder, name) is { } agent)
            {
                yield return new PromptResource(ToPromptSourceLabel(kind), precedence, agent.Path, agent.Body, agent.Description, agent.Name, null,
                    PartialSystemPromptPartOptions.Empty, HashText(agent.Body), PromptCompositionMode.Replace);
            }
        }
    }

    private static PromptResource? LoadResource(PromptRoot root, string folder, string suffix, string name, List<SystemPromptDiagnostic> diagnostics, SystemPromptResourceKind resourceKind)
    {
        var path = Path.Combine(root.Path, folder, name + suffix);
        if (!File.Exists(path))
        {
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(SystemPromptDiagnostic.Error("unreadable_prompt_resource", $"Prompt resource '{path}' could not be read: {ex.Message}", path));
            return null;
        }

        var (frontmatter, body) = SplitFrontmatter(text, path, diagnostics);
        var allowedFields = resourceKind == SystemPromptResourceKind.AgentPrompt
            ? new HashSet<string>(["name", "description", "system", "skills", "project_context", "runtime_context", "tool_guidance", "disabled_tools", "inherit", "mode", "append", "version", "max_tokens", "id", "kind", "path"], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(["description", "mode", "append", "version", "max_tokens", "id", "kind", "path"], StringComparer.OrdinalIgnoreCase);
        var partOptions = PartialSystemPromptPartOptions.Empty;
        var hasErrors = false;
        var mode = ParsePromptCompositionMode(frontmatter, path, diagnostics, ref hasErrors);
        foreach (var key in frontmatter.Keys)
        {
            if (!allowedFields.Contains(key))
            {
                diagnostics.Add(SystemPromptDiagnostic.Warning("unknown_frontmatter_field", $"Prompt resource '{path}' contains unknown frontmatter field '{key}'.", path));
            }
            else if (key is "id" or "kind" or "path")
            {
                diagnostics.Add(SystemPromptDiagnostic.Warning("derived_frontmatter_field", $"Prompt resource '{path}' frontmatter field '{key}' is ignored because kind/name/path are derived from the file location.", path));
            }
            else if (resourceKind == SystemPromptResourceKind.AgentPrompt)
            {
                switch (key.ToLowerInvariant())
                {
                    case "skills":
                        partOptions = partOptions with { Skills = ParseFrontmatterBool(frontmatter[key], key, path, diagnostics, ref hasErrors) };
                        break;
                    case "project_context":
                        partOptions = partOptions with { ProjectContext = ParseFrontmatterBool(frontmatter[key], key, path, diagnostics, ref hasErrors) };
                        break;
                    case "runtime_context":
                        partOptions = partOptions with { RuntimeContext = ParseFrontmatterBool(frontmatter[key], key, path, diagnostics, ref hasErrors) };
                        break;
                    case "tool_guidance":
                        partOptions = partOptions with { ToolGuidance = ParseFrontmatterBool(frontmatter[key], key, path, diagnostics, ref hasErrors) };
                        break;
                }
            }
        }

        if (hasErrors || mode is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            diagnostics.Add(SystemPromptDiagnostic.Error("empty_prompt_resource", $"Prompt resource '{path}' is empty.", path));
            return null;
        }

        var displayName = NormalizeName(frontmatter.GetValueOrDefault("name"));
        if (resourceKind == SystemPromptResourceKind.AgentPrompt && displayName is null && mode == PromptCompositionMode.Replace)
        {
            diagnostics.Add(SystemPromptDiagnostic.Error("missing_prompt_name", $"Agent prompt '{path}' is missing required frontmatter field 'name'.", path));
            return null;
        }

        var systemPromptName = resourceKind == SystemPromptResourceKind.AgentPrompt
            ? NormalizeName(frontmatter.GetValueOrDefault("system"))
            : null;
        var trimmed = body.Trim();
        return new PromptResource(root.SourceKind, root.Precedence, path, trimmed, frontmatter.GetValueOrDefault("description"), displayName, systemPromptName, partOptions, HashText(trimmed), mode.Value)
        {
            DisabledTools = resourceKind == SystemPromptResourceKind.AgentPrompt ? CodeAlta.Catalog.PromptFileFormat.ParseDisabledTools(frontmatter) : [],
            Inherits = resourceKind == SystemPromptResourceKind.AgentPrompt ? CodeAlta.Catalog.PromptFileFormat.ParseInherits(frontmatter) : [],
        };
    }

    private static Dictionary<string, string> ParseFlatKeyValueFile(string text, List<SystemPromptDiagnostic> diagnostics, string path, bool allowFrontmatterDelimiters)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = new CodeAlta.Catalog.PromptFileFormat.BlockList();
        var lineNumber = 0;
        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (allowFrontmatterDelimiters && line == "---")
            {
                continue;
            }

            // The items of a block list below a key without a value (`inherit:` then `- default` lines).
            if (allowFrontmatterDelimiters && list.TryAdd(line))
            {
                continue;
            }

            list.Close(values);

            var colonIndex = line.IndexOf(':', StringComparison.Ordinal);
            if (colonIndex <= 0)
            {
                diagnostics.Add(SystemPromptDiagnostic.Error("invalid_yaml", $"File '{path}' has invalid key/value syntax on line {lineNumber}.", path));
                continue;
            }

            var key = line[..colonIndex].Trim();
            var rawValue = line[(colonIndex + 1)..].Trim();
            var value = allowFrontmatterDelimiters
                ? CodeAlta.Catalog.PromptFileFormat.DecodeScalar(rawValue)
                : rawValue.Trim('"', '\'');
            values[key] = value;
            if (allowFrontmatterDelimiters && rawValue.Length == 0)
            {
                list.Open(key);
            }
        }

        list.Close(values);
        return values;
    }

    private static (Dictionary<string, string> Frontmatter, string Body) SplitFrontmatter(string text, string path, List<SystemPromptDiagnostic> diagnostics)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), text);
        }

        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            diagnostics.Add(SystemPromptDiagnostic.Error("invalid_frontmatter", $"Prompt resource '{path}' has unterminated frontmatter.", path));
            return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), text);
        }

        var frontmatterText = normalized[4..end];
        var body = normalized[(end + 5)..];
        return (ParseFlatKeyValueFile(frontmatterText, diagnostics, path, allowFrontmatterDelimiters: true), body);
    }

    private static SystemPromptManifestPart CreateResourcePart(PromptResource resource, string kind, string name, string target, int order, string status, string? replaces)
        => new(
            Key: kind == "system" ? $"system/{name}" : $"agents/{name}",
            Kind: kind,
            Name: name,
            Target: target,
            Order: order,
            SourceKind: resource.SourceKind,
            Path: resource.Path,
            Hash: resource.Hash,
            ApproxTokens: EstimateTokens(resource.Body),
            Status: status,
            Replaces: replaces,
            SourcePaths: []);

    private static SystemPromptManifestPart AddGeneratedPart(List<RenderedPromptPart> developerParts, List<SystemPromptManifestPart> manifestParts, string key, string kind, string title, int order, string body)
    {
        var trimmed = body.Trim();
        var part = new SystemPromptManifestPart(
            Key: key,
            Kind: kind,
            Name: null,
            Target: "developer",
            Order: order,
            SourceKind: "generated",
            Path: null,
            Hash: HashText(trimmed),
            ApproxTokens: EstimateTokens(trimmed),
            Status: "selected",
            Replaces: null,
            SourcePaths: []);
        manifestParts.Add(part);
        developerParts.Add(new RenderedPromptPart(key, RenderSection(title, trimmed)));
        return part;
    }

    private static string BuildRuntimeContext(SystemPromptBuildRequest request, string? projectRoot, SystemPromptContentRoots? roots = null,
        IReadOnlySet<string>? disabledTools = null)
    {
        var lines = new List<string>
        {
            $"- Current date: {DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            $"- Platform: {GetPlatformLabel()}",
        };
        // A session without the shell is not told which one it would have.
        if (disabledTools?.Contains(PromptToolConditions.ShellCommand) != true) lines.Add($"- Default shell for shell commands: {GetDefaultShellLabel()}");
        // A session in a git worktree works there: the folder of its project is then named as what it is, the
        // main checkout, which is not where this session writes.
        var worktree = ExistingWorktree(request.Session);
        var workingDirectory = worktree ?? NormalizeOptionalRoot(request.Session.WorkingDirectory) ?? NormalizeOptionalRoot(request.WorkingDirectory);
        if (workingDirectory is not null)
        {
            lines.Add($"- Current working directory: {MarkdownCode(workingDirectory)}");
        }

        if (worktree is not null)
        {
            lines.Add($"- Project root: {MarkdownCode(worktree)}");
            lines.Add(projectRoot is null
                ? "- Git worktree: the working directory is a git worktree, a checkout of its own with its own branch."
                : $"- Git worktree: the working directory is a git worktree of the project, a checkout of its own with its own branch. The main checkout of the project is {MarkdownCode(projectRoot)}: work in the working directory, and leave the main checkout as it is unless asked.");
        }
        else if (projectRoot is not null)
        {
            lines.Add($"- Project root: {MarkdownCode(projectRoot)}");
        }

        // How an answer names a file: the frontends open such a link on the file, at its line. A relative path is
        // read from the folder the session works in, which is the root named above.
        if (worktree is not null || projectRoot is not null || workingDirectory is not null)
        {
            var root = worktree is not null || projectRoot is not null ? "the project root" : "the current working directory";
            lines.Add($"- Links to files: to point the user to a file, write a Markdown link whose target is the path of the file relative to {root}, with forward slashes, and `#L<line>` after it for a place in the file, as in `[Program.cs](src/Program.cs#L42)`. The user opens the file from the link. Give a full path only to a file outside {root}.");
        }

        if (!string.IsNullOrWhiteSpace(request.Session.ParentSessionId))
        {
            lines.Add($"- Parent session: {request.Session.ParentSessionId}");
        }

        lines.Add($"- Session kind: {request.Session.Kind.ToString().ToLowerInvariant()}");
        if (request.Space is { } space)
        {
            lines.Add(SpaceLine(space));
        }

        if (SignOffLine(request.GitIdentity) is { } signOff)
        {
            lines.Add(signOff);
        }

        // CodeAlta is named as a co-author of the commits of its agents, as the harness of a provider names itself.
        if (request.CommitCoAuthor)
        {
            lines.Add(CoAuthorLine);
        }

        // The models the user prefers for each kind of work: an agent asks for a tier instead of naming a model.
        if (!string.IsNullOrWhiteSpace(request.ModelTiers))
        {
            lines.Add($"- Model tiers: {request.ModelTiers}. Give a session one with `alta session create --tier <name>` instead of naming a model."
                + (request.ModelFallback ? " When its provider fails (quota, outage), a session goes on with the next model of its tier." : string.Empty));
        }

        // Where an agent writes down long instructions of the user, so that they outlive a compaction of the context.
        // The agent names the file after the day and the request; the end of the name is a short hash of the session,
        // so that two agents never write the same file.
        if (ScratchpadFile(request) is { } scratchpad)
        {
            lines.Add($"- Scratchpad file: {MarkdownCode(scratchpad)}");
        }

        // The user guide that ships with the application: the agent answers questions about CodeAlta from it.
        if (roots is not null && File.Exists(Path.Combine(roots.ShippedUserGuideRoot, "readme.md")))
        {
            lines.Add($"- CodeAlta user guide: {MarkdownCode(roots.ShippedUserGuideRoot)} (start with `readme.md`; the pictures its pages name are in `img/`). For a question about CodeAlta itself (what a feature does, where a setting is, how to do something in its interface), read the guide and answer from it; you can guide the user step by step.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    // The space of the session, with what it is for on the same line: a description of several lines is joined, and
    // a long one is cut, since it is read on every turn.
    internal static string SpaceLine(SpaceDescriptor space)
    {
        var line = $"- Space: {space.Name} ({space.Id})";
        if (string.IsNullOrWhiteSpace(space.Description))
        {
            return line;
        }

        var about = string.Join(' ', space.Description.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (about.Length > MaximumSpaceDescriptionLength)
        {
            var end = MaximumSpaceDescriptionLength - 1;
            if (char.IsHighSurrogate(about[end - 1])) end--;
            about = about[..end].TrimEnd() + "…";
        }

        return $"{line}: {about}";
    }

    // The commits of a space that signs off carry the line of its author: Git adds it only when asked, at each commit.
    internal static string? SignOffLine(SpaceGitIdentity? git)
    {
        if (git?.SignOff != true)
        {
            return null;
        }

        return git.Name is { } name && git.Email is { } email
            ? $"- Commit with `git commit --signoff`: Signed-off-by: {name} <{email}>"
            : "- Commit with `git commit --signoff`.";
    }

    private static string? ScratchpadFile(SystemPromptBuildRequest request)
    {
        // A session that was forked keeps the name of the session it comes from: the conversation it inherited names
        // that file, and the instructions of the two are then the same text, which a provider reads from its cache.
        var session = (request.Session.ForkedFrom?.RootSessionId ?? request.Session.SessionId)?.Trim();
        if (string.IsNullOrEmpty(session))
        {
            return null;
        }

        // Instance-owned scratchpads are separate from shared prompt/skill resource discovery.
        // Older callers without a state root retain the normal-profile fallback.
        var root = NormalizeOptionalRoot(request.InstanceStateRoot) ?? NormalizeOptionalRoot(request.UserCodeAltaRoot);
        if (root is null)
        {
            var profile = NormalizeOptionalRoot(request.DiscoveryScope?.UserProfileRoot ?? request.UserProfileRoot)
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            root = string.IsNullOrWhiteSpace(profile) ? null : Path.Combine(profile, ".alta");
        }

        // Six characters of a hash of the id: the same for every prompt of the session, and a name of a file whatever the id is.
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(session)), 0, 3);
        var folder = NormalizeOptionalRoot(request.ScratchpadRoot) ?? (root is null ? null : Path.Combine(root, "scratchpad"));
        return folder is null ? null : Path.Combine(folder, $"<yyyy-mm-dd>-<short-name>-{hash}.md");
    }

    private static string BuildToolGuidance(SystemPromptBuildRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Tool schemas and handlers are supplied separately by the host. Use only tools that are actually available in the current runtime.");
        builder.AppendLine("Treat tool results as evidence, do not fabricate tool output, and respect host permission or approval prompts before acting.");
        builder.AppendLine("When you write in an external system on the user's behalf (a pull request comment or review, an issue, a reply in an issue tracker, chat or email), end the text with a short italic line saying that it comes from CodeAlta, so readers do not take it for the user's own words, for example `_This review was written by [CodeAlta](https://codealta.github.io), an AI coding agent._` or `_This issue was created by [CodeAlta](https://codealta.github.io), an AI coding agent._`; this does not apply to commit messages.");
        if (request.Tools.Count > 0)
        {
            builder.Append("Available host tool count: ").Append(request.Tools.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(".");
        }

        if (request.Tools.Any(static tool => string.Equals(tool.Spec.Name, "alta", StringComparison.OrdinalIgnoreCase)))
        {
            builder.AppendLine("Use the `alta` live tool for finite CodeAlta host/session/catalog operations. Start with args [\"--help\"], then narrower help such as [\"session\",\"--help\"] before invoking mutating commands.");
        }

        return builder.ToString().Trim();
    }

    /// <summary>What a session that has <c>exec</c> is told about using it; the same text for every session of a mode, so that prompt caches hold.</summary>
    internal const string CodeModeGuidance = """
        `exec` runs a JavaScript program that calls your tools, `alta` and sub-agents; the user watches each run live, as a graph of its steps and sessions. It is your way to run work that splits.

        - **Independent parts run together, in one `exec`.** When a request names several items to look into, change, check or compare (topics, projects, sources, modules, files, options, angles of a review), your first call is one `exec` that gives each item an `agent()` and runs them at once with `parallel()`; do not work through them yourself one after another. Items still to find (the files of a folder, the matches of a search) are found by that script (`tools.list_dir`, `tools.grep`), not by a call before it. A sub-agent has the tools you have (web search too, when you have it). Never start sub-agents one at a time with `alta session create`/`send`. A loop or batch of tool calls goes in a script too, not one call per turn.
        - **Write the script for the task**, without a library script; `workflow("name")` reuses one that fits (`codemode.search({ query: "workflow:" })`).
        - **Show the plan:** start with `export const meta = { title: "…", steps: [{ id: "…", title: "…" }] };`, declaring only the steps the script runs, and wrap each in `step("<id>", fn)` with those ids (`step`, `agent` and `parallel` are given: never define your own).
        - **Brief each sub-agent fully** (it sees nothing else): what to find or do, when to stop, and a short answer, or a `schema` when code combines the answers. `tier: "quick"` for lookups; `worktree: true` for parts that edit files at the same time. Use their answers as they are: do not redo their work to check it.
        - **Return a compact result**, it is all you read back: findings, decisions, branches, failures.
        - Call the tools directly for one or two calls, for items that each take a single call (make those calls together), and for steps that each need the previous result.

        ```js
        export const meta = { title: "Compare caches", steps: [{ id: "research", title: "Research each option" }, { id: "compare", title: "Compare" }] };
        const notes = await step("research", () => parallel(["Redis", "Memcached", "in-process LRU"].map((name) => () =>
          agent(`Research ${name} as a cache for <context>. Answer in 10 lines: strengths, limits, sources.`, { title: name, tier: "quick" }))));
        return step("compare", () => agent(`Recommend one, in 5 lines:\n${notes.join("\n---\n")}`, { title: "Compare" }));
        ```
        """;

    /// <summary>The line added for a session where <c>exec</c> takes over the other tools.</summary>
    internal const string CodeModeOnlyGuidance = "The tools of CodeAlta that are not in your tool list are called from scripts, as `tools.<name>(args)` in `exec`: batch them in one script instead of one `exec` per call.";

    /// <summary>The line added for a session a script started: each level of sub-agents is a whole session more.</summary>
    internal const string CodeModeSubAgentGuidance = "You are a sub-agent of a script: do your part yourself, with direct calls or a script of tool calls; start sub-agents only when your part is itself large and splits.";

    // The section of a session that has exec, or null for one that has not.
    private static string? BuildCodeModeGuidance(SystemPromptBuildRequest request, IReadOnlySet<string> disabledTools)
    {
        if (request.CodeMode is not ("on" or "only") || disabledTools.Contains("exec"))
        {
            return null;
        }

        var text = request.CodeMode == "only" ? CodeModeGuidance + "\n\n" + CodeModeOnlyGuidance : CodeModeGuidance;
        return request.Session.CreatedBy?.Kind == AltaActorProvenance.WorkflowKind ? text + "\n\n" + CodeModeSubAgentGuidance : text;
    }

    private string? BuildAgentPromptGuidance(SystemPromptBuildRequest request, string? projectRoot, string currentPromptName)
    {
        var prompts = new AgentPromptCatalog(_contentLocator).ListEffectivePrompts(new AgentPromptCatalogQuery
        {
            UserProfileRoot = UserHomeOf(request),
            UserCodeAltaRoot = request.UserCodeAltaRoot,
            ProjectRoot = projectRoot,
            ProjectPromptResourcesTrusted = projectRoot is not null,
        });
        // The prompts of the session's space, and the one it runs with, which it keeps.
        prompts = [.. prompts.Where(prompt => request.SpaceResources is not { } space
            || space.Allows(SpaceResourceKind.Prompt, prompt.PromptName)
            || string.Equals(prompt.PromptName, currentPromptName, StringComparison.OrdinalIgnoreCase))];
        if (prompts.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Agent prompt profiles available for this session:");
        foreach (var prompt in prompts)
        {
            var currentPrefix = string.Equals(prompt.PromptName, currentPromptName, StringComparison.OrdinalIgnoreCase)
                ? "- current: "
                : "- ";
            builder.Append(currentPrefix)
                .Append('`')
                .Append(EscapeBackticks(prompt.PromptName))
                .Append("` — ")
                .Append(CompactSingleLine(prompt.DisplayName))
                .AppendLine();
            builder.Append("  - Source: ")
                .Append(ToPromptSourceLabel(prompt.SourceKind))
                .Append("; system: `")
                .Append(EscapeBackticks(prompt.SystemPromptName))
                .AppendLine("`");
            var description = CompactSingleLine(prompt.Description);
            if (!string.IsNullOrWhiteSpace(description))
            {
                builder.Append("  - Description: ").AppendLine(description);
            }
        }

        builder.AppendLine("Switch current session: `alta session set_agent --prompt-id <id>`.");
        builder.Append("One-shot/child send: `alta session send <session-id> --prompt-id <id> --stdin`.");
        return builder.ToString();
    }

    private static void ValidateDiscoveryPaths(SystemPromptBuildRequest request)
    {
        if (request.DiscoveryScope is not { } scope)
        {
            return;
        }

        if (request.Session.WorkingDirectory is not null)
        {
            scope.ValidateProjectPath(request.Session.WorkingDirectory, nameof(request.Session.WorkingDirectory));
        }

        if (request.WorkingDirectory is not null)
        {
            scope.ValidateProjectPath(request.WorkingDirectory, nameof(request.WorkingDirectory));
        }

        if (request.Project?.ProjectPath is not null)
        {
            scope.ValidateProjectPath(request.Project.ProjectPath, nameof(request.Project.ProjectPath));
        }

        foreach (var root in request.ProjectRoots)
        {
            scope.ValidateProjectPath(root, nameof(request.ProjectRoots));
        }
    }

    private static string? BuildProjectContext(SystemPromptBuildRequest request, string? projectRoot, List<SystemPromptDiagnostic> diagnostics, AgentInstructionFileReader instructionFiles, out IReadOnlyList<string> files)
    {
        var projectRoots = request.ProjectRoots.Count > 0 ? request.ProjectRoots : projectRoot is null ? [] : [projectRoot];
        var selectedFiles = InWorktree(
            EnumerateProjectInstructionFiles(request.Session.WorkingDirectory ?? request.WorkingDirectory, projectRoots, request.DiscoveryScope, instructionFiles),
            request.Session).ToList();
        // The layout of GitHub Copilot beside CodeAlta's own: the instructions of the user for every project, and the
        // instructions of a project that are for some of its files only.
        if (CopilotUserInstructions(request, instructionFiles) is { } personal && !selectedFiles.Contains(personal, StringComparer.OrdinalIgnoreCase))
        {
            selectedFiles.Insert(0, personal);
        }

        var scoped = CopilotPathInstructions(ExistingWorktree(request.Session) is { } worktree ? [worktree] : projectRoots, instructionFiles);
        var includedFiles = new List<string>();
        files = includedFiles;
        if (selectedFiles.Count == 0 && scoped.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var path in selectedFiles)
        {
            var length = instructionFiles.GetLength(path, required: true);
            if (length is null)
            {
                continue;
            }

            var content = instructionFiles.ReadAllText(path);
            if (content is null)
            {
                continue;
            }

            includedFiles.Add(path);
            if (length > 64 * 1024)
            {
                diagnostics.Add(SystemPromptDiagnostic.Warning("large_project_context_file", $"Project instruction/context file '{path}' is unusually large.", path));
            }

            if (content.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine();
            }

            builder.AppendLine($"File: {MarkdownCode(path)}");
            builder.AppendLine();
            builder.AppendLine("<INSTRUCTIONS>");
            builder.AppendLine();
            builder.AppendLine(content);
            builder.AppendLine();
            builder.Append("</INSTRUCTIONS>");
        }

        if (scoped.Count > 0)
        {
            includedFiles.AddRange(scoped.Select(static item => item.Path));
            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine();
            }

            // They are named, not included: an agent reads the ones of the files it is about to change.
            builder.AppendLine("Instructions for some files only (`.github/instructions`). Before you change a file that matches a pattern, read the file of instructions and follow it:");
            foreach (var (path, applyTo) in scoped)
            {
                builder.AppendLine($"- {MarkdownCode(path)} applies to {MarkdownCode(applyTo)}");
            }

            builder.Length -= Environment.NewLine.Length;
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    // The home folder whose files of other tools the prompt reads; null for none.
    private static string? UserHomeOf(SystemPromptBuildRequest request)
        => request.ReadsUserHome ? request.DiscoveryScope?.UserProfileRoot ?? request.UserProfileRoot : null;

    // What the user wrote for GitHub Copilot in every project: `~/.copilot/copilot-instructions.md`.
    private static string? CopilotUserInstructions(SystemPromptBuildRequest request, AgentInstructionFileReader instructionFiles)
    {
        // The profile the host names: a request that names none is given no file of the real one.
        if (NormalizeOptionalRoot(UserHomeOf(request)) is not { } profile) return null;
        var path = Path.Combine(profile, ".copilot", "copilot-instructions.md");
        return instructionFiles.GetLength(path) is not null ? path : null;
    }

    private const int MaximumPathInstructions = 50;

    // The `*.instructions.md` files of `.github/instructions` that say which files they are for (`applyTo`), as GitHub
    // Copilot reads them. One that is not for a coding agent (`excludeAgent: coding-agent`) is left out.
    private static IReadOnlyList<(string Path, string ApplyTo)> CopilotPathInstructions(IReadOnlyList<string> roots, AgentInstructionFileReader instructionFiles)
    {
        var found = new List<(string Path, string ApplyTo)>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var folder = Path.Combine(root, ".github", "instructions");
            string[] candidates;
            try
            {
                if (!Directory.Exists(folder)) continue;
                candidates = Directory.GetFiles(folder, "*.instructions.md", SearchOption.AllDirectories);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                instructionFiles.ReportFailure(folder, "inspected", exception);
                continue;
            }

            foreach (var file in candidates.OrderBy(static file => file, StringComparer.OrdinalIgnoreCase))
            {
                if (found.Count >= MaximumPathInstructions) return found;
                try
                {
                    var info = new FileInfo(file);
                    if (info.LinkTarget is not null || info.Length > 256 * 1024) continue;
                    var (frontmatter, _) = PromptFileFormat.SplitFrontmatter(File.ReadAllText(file));
                    if (!frontmatter.TryGetValue("applyTo", out var applyTo) || string.IsNullOrWhiteSpace(applyTo)) continue;
                    if (frontmatter.TryGetValue("excludeAgent", out var excluded) && excluded.Contains("coding-agent", StringComparison.OrdinalIgnoreCase)) continue;
                    var patterns = string.Join(", ", applyTo.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    if (patterns.Length is > 0 and <= 400 && !patterns.Any(char.IsControl) && !patterns.Contains('`')) found.Add((file, patterns));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    instructionFiles.ReportFailure(file, "read", exception);
                }
            }
        }

        return found;
    }

    // A session in a git worktree reads the instruction files of that checkout: a file of the project's own
    // checkout gives way to the same file of the worktree, when the worktree has it (a file that git does not
    // track is only in the main checkout). The files above the repository stay as they are.
    private static IReadOnlyList<string> InWorktree(IReadOnlyList<string> files, SessionViewDescriptor session)
    {
        var worktree = ExistingWorktree(session);
        var project = NormalizeOptionalRoot(session.WorkingDirectory);
        if (worktree is null || project is null || files.Count == 0)
        {
            return files;
        }

        // The worktree folder of a session is the folder of its project in another checkout: the same path leads
        // from the root of each checkout to it.
        var (from, to) = (project, worktree);
        var checkout = CodeAlta.Catalog.Worktrees.GitWorktreeService.FindCheckoutRoot(worktree);
        var below = checkout is null ? "." : Path.GetRelativePath(checkout, worktree);
        if (checkout is not null && below != "." && project.EndsWith(Path.DirectorySeparatorChar + below, PathComparison))
        {
            (from, to) = (project[..^(below.Length + 1)], checkout);
        }

        var mapped = new List<string>(files.Count);
        var prefix = from + Path.DirectorySeparatorChar;
        foreach (var file in files)
        {
            var candidate = file.StartsWith(prefix, PathComparison) ? Path.Combine(to, file[prefix.Length..]) : file;
            candidate = File.Exists(candidate) ? candidate : file;
            if (!mapped.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                mapped.Add(candidate);
            }
        }

        return mapped;
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // The worktree a session works in: the one it records, while its folder is there.
    private static string? ExistingWorktree(SessionViewDescriptor session)
        => NormalizeOptionalRoot(session.WorktreeDirectory) is { } worktree && Directory.Exists(worktree) ? worktree : null;

    private static IReadOnlyList<string> EnumerateProjectInstructionFiles(string? workingDirectory, IReadOnlyList<string> projectRoots, SessionDiscoveryScope? discoveryScope, AgentInstructionFileReader instructionFiles)
    {
        if (discoveryScope is not null)
        {
            if (workingDirectory is not null)
            {
                discoveryScope.ValidateProjectPath(workingDirectory, nameof(workingDirectory));
            }

            foreach (var root in projectRoots)
            {
                discoveryScope.ValidateProjectPath(root, nameof(projectRoots));
            }
        }

        var files = new List<string>();
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddWalk(string? root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return;
            }

            foreach (var directory in SessionDiscoveryScope.GetInstructionAncestors(root, discoveryScope))
            {
                if (!seenDirectories.Add(directory)) continue;
                var selectedFile = instructionFiles.SelectLargestFile(directory);
                if (selectedFile is not null)
                {
                    files.Add(selectedFile);
                }
            }
        }

        AddWalk(workingDirectory);
        foreach (var projectRoot in projectRoots)
        {
            AddWalk(projectRoot);
        }

        return files;
    }

    private static IEnumerable<PromptRoot> EnumerateExistingRoots(SystemPromptContentRoots roots)
    {
        if (Directory.Exists(roots.ShippedPromptRoot))
        {
            yield return new PromptRoot("built-in", 0, roots.ShippedPromptRoot);
        }

        if (Directory.Exists(roots.GlobalPromptRoot))
        {
            yield return new PromptRoot("user-global", 1, roots.GlobalPromptRoot);
        }

        if (roots.ProjectPromptResourcesTrusted && roots.ProjectPromptRoot is not null && Directory.Exists(roots.ProjectPromptRoot))
        {
            yield return new PromptRoot("project", 2, roots.ProjectPromptRoot);
        }
    }

    private static int FindCompositionChainStart(IReadOnlyList<PromptResource> resources)
    {
        for (var index = resources.Count - 1; index >= 0; index--)
        {
            if (resources[index].Mode == PromptCompositionMode.Replace)
            {
                return index;
            }
        }

        return 0;
    }

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

    private static string RenderSection(string title, string body)
        => $"# {title}{Environment.NewLine}{Environment.NewLine}{body.Trim()}";

    private static string HashText(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "sha256:" + Convert.ToHexString(bytes);
    }

    private static string ComputeHash(string? systemMessage, string? developerInstructions, string providerKey, string? providerType, string? protocolFamily, string? model, string channelMapping)
        => HashText(string.Join("\n---\n", systemMessage ?? string.Empty, developerInstructions ?? string.Empty, providerKey, providerType ?? string.Empty, protocolFamily ?? string.Empty, model ?? string.Empty, channelMapping));

    private static string CreatePromptId(string hash)
        => "sp_" + hash.Replace("sha256:", string.Empty, StringComparison.Ordinal).ToLowerInvariant()[..16];

    private static int EstimateTokens(string? text)
        => checked((int)TokenEstimator.Estimate(text));

    private static string? NormalizeOptionalRoot(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string? NormalizeName(string? name)
        => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private static string? LastNonBlank(IEnumerable<string?> values)
        => values.LastOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? FirstNonBlank(IEnumerable<string> values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static string ToPromptSourceLabel(AgentPromptSourceKind sourceKind)
        => sourceKind switch
        {
            AgentPromptSourceKind.BuiltIn => "built-in",
            AgentPromptSourceKind.UserGlobal => "user-global",
            AgentPromptSourceKind.Project => "project",
            AgentPromptSourceKind.CopilotUser => "copilot-user",
            AgentPromptSourceKind.CopilotProject => "copilot-project",
            _ => sourceKind.ToString(),
        };

    private static string CompactSingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var previousWasWhiteSpace = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsControl(ch))
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!previousWasWhiteSpace)
                {
                    builder.Append(' ');
                    previousWasWhiteSpace = true;
                }

                continue;
            }

            builder.Append(ch);
            previousWasWhiteSpace = false;
        }

        return builder.ToString().Trim();
    }

    private static string EscapeBackticks(string value)
        => value.Replace("`", "'", StringComparison.Ordinal);

    private static string MarkdownCode(string value)
    {
        var maxBacktickRun = 0;
        var currentRun = 0;
        foreach (var ch in value)
        {
            if (ch == '`')
            {
                currentRun++;
                maxBacktickRun = Math.Max(maxBacktickRun, currentRun);
            }
            else
            {
                currentRun = 0;
            }
        }

        var delimiter = new string('`', maxBacktickRun + 1);
        return value.StartsWith('`') || value.EndsWith('`')
            ? $"{delimiter} {value} {delimiter}"
            : $"{delimiter}{value}{delimiter}";
    }

    private static string GetPlatformLabel()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        if (OperatingSystem.IsLinux())
        {
            return "Linux";
        }

        return RuntimeInformation.OSDescription.Trim();
    }

    private static string GetDefaultShellLabel()
    {
        if (OperatingSystem.IsWindows())
        {
            return "pwsh";
        }

        var shell = Environment.GetEnvironmentVariable("SHELL");
        return string.IsNullOrWhiteSpace(shell) ? "/bin/sh" : shell.Trim();
    }

    private sealed record PromptRoot(string SourceKind, int Precedence, string Path);
    private sealed record PromptResource(string SourceKind, int Precedence, string Path, string Body, string? Description, string? DisplayName, string? SystemPromptName, PartialSystemPromptPartOptions PartOptions, string Hash, PromptCompositionMode Mode)
    {
        public IReadOnlyList<string> DisabledTools { get; init; } = [];
        public IReadOnlyList<string> Inherits { get; init; } = [];
    }
    private sealed record ResourceResolution(PromptResource? Selected, string? Body, IReadOnlyList<AppliedResource> Applied, IReadOnlyList<SkippedResource> Skipped);
    private sealed record AppliedResource(PromptResource Resource, string Status, string? Replaces);
    private sealed record SkippedResource(PromptResource Resource, string Status);
    private sealed record RenderedPromptPart(string Key, string Markdown);
    private enum SystemPromptResourceKind { SystemPrompt, AgentPrompt }

    private sealed record SystemPromptResolvedComposition(string SystemPromptName, string SystemPromptReason, string AgentPromptName, string AgentPromptReason, SystemPromptPartOptions PartOptions)
    {
        public SystemPromptCompositionManifest ToManifest()
            => new(SystemPromptName, SystemPromptReason, AgentPromptName, AgentPromptReason, PartOptions);
    }
}

/// <summary>
/// Inputs used to build a CodeAlta system prompt.
/// </summary>
public sealed class SystemPromptBuildRequest
{
    /// <summary>Gets the provider key used by the active session.</summary>
    public required string ProviderKey { get; init; }

    /// <summary>Gets the optional provider type.</summary>
    public string? ProviderType { get; init; }

    /// <summary>Gets the optional protocol family.</summary>
    public string? ProtocolFamily { get; init; }

    /// <summary>Gets the selected model id.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the active session view.</summary>
    public required SessionViewDescriptor Session { get; init; }

    /// <summary>Gets the owning project, if any.</summary>
    public ProjectDescriptor? Project { get; init; }

    /// <summary>Gets an optional selected base name override.</summary>
    public string? SelectedBaseName { get; init; }

    /// <summary>Gets an optional selected session instruction name override.</summary>
    public string? SelectedInstructionName { get; init; }

    /// <summary>Gets an optional selected agent prompt name override.</summary>
    public string? SelectedPromptName { get; init; }

    /// <summary>Gets the optional user profile root used to resolve <c>~/.alta</c>.</summary>
    public string? UserProfileRoot { get; init; }

    /// <summary>
    /// Gets whether the prompt reads what other tools keep in the home folder (<see cref="DiscoveryScope"/>, else
    /// <see cref="UserProfileRoot"/>): the agents of <c>~/.copilot/agents</c> and <c>~/.copilot/copilot-instructions.md</c>.
    /// True, the default; false for a separate profile, which reads none of them.
    /// </summary>
    public bool ReadsUserHome { get; init; } = true;

    /// <summary>Gets optional explicit home and lexical instruction ancestry limits; not a filesystem sandbox.</summary>
    public SessionDiscoveryScope? DiscoveryScope { get; init; }

    /// <summary>Gets the optional CodeAlta user-global root used to resolve prompt overrides.</summary>
    public string? UserCodeAltaRoot { get; init; }

    /// <summary>Gets the owning instance's state root for scratchpads, independent of shared user resources.</summary>
    public string? InstanceStateRoot { get; init; }

    /// <summary>
    /// Gets the folder of the scratchpads, when the instance keeps them elsewhere than in the <c>scratchpad</c> folder of
    /// <see cref="InstanceStateRoot"/>; null for that folder.
    /// </summary>
    public string? ScratchpadRoot { get; init; }

    /// <summary>Gets optional prompt part overrides.</summary>
    public PartialSystemPromptPartOptions? PartOptionsOverride { get; init; }

    /// <summary>Gets the session working directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets configured project roots.</summary>
    public IReadOnlyList<string> ProjectRoots { get; init; } = [];

    /// <summary>Gets enabled tool definitions.</summary>
    public IReadOnlyList<AgentToolDefinition> Tools { get; init; } = [];

    /// <summary>Gets available-skills markdown produced by the skill catalog.</summary>
    public string? AvailableSkillsMarkdown { get; init; }

    /// <summary>
    /// Gets the tiers of models of the space of the session, described in one line for the agents, or null when the
    /// user set none.
    /// </summary>
    public string? ModelTiers { get; init; }

    /// <summary>Gets whether, in the space of the session, a session goes on with the next model of its tier when its provider fails.</summary>
    public bool ModelFallback { get; init; }

    /// <summary>
    /// Gets the mode of Code Mode the session has: <c>off</c> when the host gives none; <c>on</c> when it has <c>exec</c>; <c>only</c> when
    /// <c>exec</c> takes over its other tools. The instructions tell a session with <c>exec</c> when to use it.
    /// </summary>
    public string CodeMode { get; init; } = "off";

    /// <summary>Gets active-skills markdown restored from session state.</summary>
    public string? ActiveSkillsMarkdown { get; init; }

    /// <summary>
    /// Gets the space the session belongs to, which the Runtime Context names; null where the catalog has no space
    /// but the default one.
    /// </summary>
    public SpaceDescriptor? Space { get; init; }

    /// <summary>
    /// Gets what the space of the session has, which narrows the agent prompts the instructions list; null to list
    /// every prompt.
    /// </summary>
    public SpaceFilter? SpaceResources { get; init; }

    /// <summary>
    /// Gets the Git identity of the space of the session, the default space included: when it signs off commits,
    /// the Runtime Context says so, since Git has no setting for it.
    /// </summary>
    public SpaceGitIdentity? GitIdentity { get; init; }

    /// <summary>
    /// Gets whether the agent ends the message of its commits with <see cref="SystemPromptBuilder.CoAuthorTrailer"/>, as the
    /// setting <c>[commits] co_author</c> of the user says; the Runtime Context then asks for it.
    /// </summary>
    public bool CommitCoAuthor { get; init; }

    /// <summary>
    /// Gets the built-in tools the session does without, from the settings of the user and of its space; the agent
    /// prompt adds its own <c>disabled_tools</c>. The text that names a tool turned off is left out. Null for none.
    /// </summary>
    public IReadOnlySet<string>? DisabledTools { get; init; }
}

/// <summary>
/// Boolean controls for generated system prompt parts.
/// </summary>
/// <param name="Skills">Whether skills parts are included.</param>
/// <param name="ProjectContext">Whether project instruction/context files are included.</param>
/// <param name="RuntimeContext">Whether runtime facts are included.</param>
/// <param name="ToolGuidance">Whether generated tool-use guidance is included.</param>
public sealed record SystemPromptPartOptions(bool Skills, bool ProjectContext, bool RuntimeContext, bool ToolGuidance)
{
    /// <summary>Gets default prompt part options.</summary>
    public static SystemPromptPartOptions Default { get; } = new(true, true, true, true);
}

/// <summary>
/// Partial prompt part option override set.
/// </summary>
/// <param name="Skills">Optional skills toggle.</param>
/// <param name="ProjectContext">Optional project context toggle.</param>
/// <param name="RuntimeContext">Optional runtime context toggle.</param>
/// <param name="ToolGuidance">Optional tool guidance toggle.</param>
public sealed record PartialSystemPromptPartOptions(bool? Skills, bool? ProjectContext, bool? RuntimeContext, bool? ToolGuidance)
{
    /// <summary>Gets an empty partial option set.</summary>
    public static PartialSystemPromptPartOptions Empty { get; } = new(null, null, null, null);

    /// <summary>Merges this partial override over lower-precedence options.</summary>
    /// <param name="lower">Lower-precedence options.</param>
    /// <returns>The merged options.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="lower"/> is <see langword="null"/>.</exception>
    public SystemPromptPartOptions MergeOver(SystemPromptPartOptions lower)
    {
        ArgumentNullException.ThrowIfNull(lower);
        return new SystemPromptPartOptions(
            Skills ?? lower.Skills,
            ProjectContext ?? lower.ProjectContext,
            RuntimeContext ?? lower.RuntimeContext,
            ToolGuidance ?? lower.ToolGuidance);
    }
}

/// <summary>
/// Composed prompt bundle and build receipt.
/// </summary>
public sealed class SystemPromptBundle
{
    /// <summary>Gets the logical system message.</summary>
    public string? SystemMessage { get; init; }

    /// <summary>Gets the logical developer instructions.</summary>
    public string? DeveloperInstructions { get; init; }

    /// <summary>Gets the stable effective prompt hash.</summary>
    public required string EffectivePromptHash { get; init; }

    /// <summary>Gets the prompt manifest.</summary>
    public required SystemPromptManifest Manifest { get; init; }

    /// <summary>Gets prompt statistics.</summary>
    public required SystemPromptStatistics Statistics { get; init; }

    /// <summary>Gets build diagnostics.</summary>
    public IReadOnlyList<SystemPromptDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// Gets the built-in tools the session does without: those of the request and the <c>disabled_tools</c> of the
    /// agent prompt. The session leaves them out of its tools.
    /// </summary>
    public IReadOnlySet<string> DisabledTools { get; init; } = FrozenSet<string>.Empty;
}

/// <summary>
/// Structured manifest describing a composed prompt.
/// </summary>
public sealed record SystemPromptManifest(
    int Version,
    string PromptId,
    string EffectivePromptHash,
    SystemPromptCompositionManifest Composition,
    SystemPromptProviderMapping Provider,
    IReadOnlyList<SystemPromptManifestPart> Parts,
    SystemPromptStatistics Statistics,
    IReadOnlyList<SystemPromptDiagnostic> Diagnostics);

/// <summary>
/// Prompt composition metadata.
/// </summary>
/// <param name="SystemPromptName">The selected system prompt id.</param>
/// <param name="SystemPromptReason">The reason/source for the selected system prompt id.</param>
/// <param name="AgentPromptName">The selected agent prompt id.</param>
/// <param name="AgentPromptReason">The reason/source for the selected agent prompt id.</param>
/// <param name="PartOptions">The effective generated prompt-part options.</param>
public sealed record SystemPromptCompositionManifest(
    string SystemPromptName,
    string SystemPromptReason,
    string AgentPromptName,
    string AgentPromptReason,
    SystemPromptPartOptions PartOptions);

/// <summary>
/// Provider mapping metadata for prompt audit.
/// </summary>
public sealed record SystemPromptProviderMapping(
    string ProviderKey,
    string? ProviderType,
    string? ProtocolFamily,
    string? Model,
    string ChannelMapping,
    bool AppliedToProvider,
    bool Lossy,
    IReadOnlyList<string> Notes);

/// <summary>
/// Manifest entry for one prompt part.
/// </summary>
public sealed record SystemPromptManifestPart(
    string Key,
    string Kind,
    string? Name,
    string Target,
    int Order,
    string SourceKind,
    string? Path,
    string Hash,
    int ApproxTokens,
    string Status,
    string? Replaces,
    IReadOnlyList<string> SourcePaths);

/// <summary>
/// Approximate prompt size statistics.
/// </summary>
public sealed record SystemPromptStatistics(int SystemApproxTokens, int DeveloperApproxTokens, int TotalApproxTokens, int SystemChars, int DeveloperChars, int PartCount)
{
    /// <summary>Creates statistics from logical messages and manifest parts.</summary>
    /// <param name="systemMessage">System message.</param>
    /// <param name="developerInstructions">Developer instructions.</param>
    /// <param name="parts">Manifest parts.</param>
    /// <returns>Prompt statistics.</returns>
    public static SystemPromptStatistics FromMessages(string? systemMessage, string? developerInstructions, IReadOnlyList<SystemPromptManifestPart> parts)
    {
        var systemChars = systemMessage?.Length ?? 0;
        var developerChars = developerInstructions?.Length ?? 0;
        var systemTokens = string.IsNullOrEmpty(systemMessage) ? 0 : Math.Max(1, (int)Math.Ceiling(systemChars / 4.0));
        var developerTokens = string.IsNullOrEmpty(developerInstructions) ? 0 : Math.Max(1, (int)Math.Ceiling(developerChars / 4.0));
        return new SystemPromptStatistics(systemTokens, developerTokens, systemTokens + developerTokens, systemChars, developerChars, parts.Count);
    }
}

/// <summary>
/// Prompt diagnostic severity.
/// </summary>
public enum SystemPromptDiagnosticSeverity
{
    /// <summary>An informational diagnostic.</summary>
    Info,

    /// <summary>A warning diagnostic.</summary>
    Warning,

    /// <summary>An error diagnostic.</summary>
    Error,
}

/// <summary>
/// Prompt build diagnostic.
/// </summary>
public sealed record SystemPromptDiagnostic(SystemPromptDiagnosticSeverity Severity, string Code, string Message, string? Path)
{
    /// <summary>Creates a warning diagnostic.</summary>
    public static SystemPromptDiagnostic Warning(string code, string message, string? path)
        => new(SystemPromptDiagnosticSeverity.Warning, code, message, path);

    /// <summary>Creates an error diagnostic.</summary>
    public static SystemPromptDiagnostic Error(string code, string message, string? path)
        => new(SystemPromptDiagnosticSeverity.Error, code, message, path);
}
