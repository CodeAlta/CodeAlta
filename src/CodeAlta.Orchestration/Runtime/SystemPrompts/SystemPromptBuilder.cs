using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime.SystemPrompts;

/// <summary>
/// Builds file-backed CodeAlta system prompt bundles.
/// </summary>
public sealed class SystemPromptBuilder
{
    private const string DefaultName = "default";
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
    public SystemPromptBundle Build(SystemPromptBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProviderKey);
        ArgumentNullException.ThrowIfNull(request.Session);

        var diagnostics = new List<SystemPromptDiagnostic>();
        var projectRoot = NormalizeOptionalRoot(request.Project?.ProjectPath ?? FirstNonBlank(request.ProjectRoots));
        var roots = _contentLocator.GetRoots(new SystemPromptDiscoveryContext
        {
            UserProfileRoot = request.UserProfileRoot,
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

        var selectedSystemOverride = NormalizeName(request.SelectedBaseName);
        var agentSystemPromptName = ResolveEffectiveSystemPromptName(promptResolution);
        var selectedSystemName = selectedSystemOverride ?? agentSystemPromptName ?? DefaultName;
        var selectedSystemReason = selectedSystemOverride is not null
            ? "runtime"
            : agentSystemPromptName is not null
                ? $"agent:{composition.AgentPromptName}"
                : "default-name";
        var effectivePartOptions = SystemPromptPartOptions.Default;
        foreach (var applied in promptResolution.Applied)
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
        var systemMessage = systemResolution.Body!.Trim();
        foreach (var applied in systemResolution.Applied)
        {
            parts.Add(CreateResourcePart(applied.Resource, "system", selectedSystemName, "system", 100, applied.Status, applied.Replaces));
        }

        foreach (var skipped in systemResolution.Skipped)
        {
            parts.Add(CreateResourcePart(skipped.Resource, "system", selectedSystemName, "system", 100, skipped.Status, null));
        }

        var developerParts = new List<RenderedPromptPart>();
        foreach (var applied in promptResolution.Applied)
        {
            parts.Add(CreateResourcePart(applied.Resource, "agent_prompt", composition.AgentPromptName, "developer", 300, applied.Status, applied.Replaces));
        }

        developerParts.Add(new RenderedPromptPart($"agents/{composition.AgentPromptName}", RenderSection("Agent Prompt", promptResolution.Body!)));
        foreach (var skipped in promptResolution.Skipped)
        {
            parts.Add(CreateResourcePart(skipped.Resource, "agent_prompt", composition.AgentPromptName, "developer", 300, skipped.Status, null));
        }

        composition = composition with { SystemPromptName = selectedSystemName, SystemPromptReason = selectedSystemReason, PartOptions = effectivePartOptions };

        if (composition.PartOptions.RuntimeContext)
        {
            AddGeneratedPart(developerParts, parts, "runtime.context", "runtime_context", "Runtime Context", 400, BuildRuntimeContext(request, projectRoot));
        }

        if (composition.PartOptions.ToolGuidance)
        {
            AddGeneratedPart(developerParts, parts, "tool.guidance", "tool_guidance", "Tool Guidance", 500, BuildToolGuidance(request));
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
            var projectContext = BuildProjectContext(request, projectRoot, diagnostics, out var projectContextFiles);
            if (!string.IsNullOrWhiteSpace(projectContext))
            {
                var projectPart = AddGeneratedPart(developerParts, parts, "project.context", "project_context", "Project Context", 600, projectContext);
                projectPart = projectPart with { SourcePaths = projectContextFiles };
                parts[^1] = projectPart;
            }
        }

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

    private static ResourceResolution ResolveResource(SystemPromptContentRoots roots, string folder, string suffix, string name, List<SystemPromptDiagnostic> diagnostics, SystemPromptResourceKind resourceKind)
    {
        var candidates = EnumerateExistingRoots(roots)
            .Select(root => LoadResource(root, folder, suffix, name, diagnostics, resourceKind))
            .Where(static resource => resource is not null)
            .Cast<PromptResource>()
            .OrderBy(static resource => resource.Precedence)
            .ToArray();
        if (candidates.Length == 0)
        {
            diagnostics.Add(SystemPromptDiagnostic.Error("missing_prompt_resource", $"Prompt resource '{folder}/{name}{suffix}' was not found.", null));
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
            ? new HashSet<string>(["name", "description", "system", "skills", "project_context", "runtime_context", "tool_guidance", "mode", "append", "version", "max_tokens", "id", "kind", "path"], StringComparer.OrdinalIgnoreCase)
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
        return new PromptResource(root.SourceKind, root.Precedence, path, trimmed, frontmatter.GetValueOrDefault("description"), displayName, systemPromptName, partOptions, HashText(trimmed), mode.Value);
    }

    private static Dictionary<string, string> ParseFlatKeyValueFile(string text, List<SystemPromptDiagnostic> diagnostics, string path, bool allowFrontmatterDelimiters)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

            var colonIndex = line.IndexOf(':', StringComparison.Ordinal);
            if (colonIndex <= 0)
            {
                diagnostics.Add(SystemPromptDiagnostic.Error("invalid_yaml", $"File '{path}' has invalid key/value syntax on line {lineNumber}.", path));
                continue;
            }

            var key = line[..colonIndex].Trim();
            var value = line[(colonIndex + 1)..].Trim().Trim('"', '\'');
            values[key] = value;
        }

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

    private static string BuildRuntimeContext(SystemPromptBuildRequest request, string? projectRoot)
    {
        var lines = new List<string>
        {
            $"- Current date: {DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            $"- Platform: {GetPlatformLabel()}",
            $"- Default shell for shell commands: {GetDefaultShellLabel()}",
        };
        var workingDirectory = NormalizeOptionalRoot(request.Session.WorkingDirectory) ?? NormalizeOptionalRoot(request.WorkingDirectory);
        if (workingDirectory is not null)
        {
            lines.Add($"- Current working directory: {MarkdownCode(workingDirectory)}");
        }

        if (projectRoot is not null)
        {
            lines.Add($"- Project root: {MarkdownCode(projectRoot)}");
        }

        if (!string.IsNullOrWhiteSpace(request.Session.ParentSessionId))
        {
            lines.Add($"- Parent session: {request.Session.ParentSessionId}");
        }

        lines.Add($"- Session kind: {request.Session.Kind.ToString().ToLowerInvariant()}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildToolGuidance(SystemPromptBuildRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Tool schemas and handlers are supplied separately by the host. Use only tools that are actually available in the current runtime.");
        builder.AppendLine("Treat tool results as evidence, do not fabricate tool output, and respect host permission or approval prompts before acting.");
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

    private string? BuildAgentPromptGuidance(SystemPromptBuildRequest request, string? projectRoot, string currentPromptName)
    {
        var prompts = new AgentPromptCatalog(_contentLocator).ListEffectivePrompts(new AgentPromptCatalogQuery
        {
            UserProfileRoot = request.UserProfileRoot,
            UserCodeAltaRoot = request.UserCodeAltaRoot,
            ProjectRoot = projectRoot,
            ProjectPromptResourcesTrusted = projectRoot is not null,
        });
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

    private static string? ResolveEffectiveSystemPromptName(ResourceResolution promptResolution)
        => LastNonBlank(promptResolution.Applied.Select(static resource => resource.Resource.SystemPromptName));

    private static string? BuildProjectContext(SystemPromptBuildRequest request, string? projectRoot, List<SystemPromptDiagnostic> diagnostics, out IReadOnlyList<string> files)
    {
        var selectedFiles = EnumerateProjectInstructionFiles(request.Session.WorkingDirectory ?? request.WorkingDirectory, request.ProjectRoots.Count > 0 ? request.ProjectRoots : projectRoot is null ? [] : [projectRoot]);
        files = selectedFiles;
        if (selectedFiles.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var path in selectedFiles)
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > 64 * 1024)
            {
                diagnostics.Add(SystemPromptDiagnostic.Warning("large_project_context_file", $"Project instruction/context file '{path}' is unusually large.", path));
            }

            var content = File.ReadAllText(path).Trim();
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

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static IReadOnlyList<string> EnumerateProjectInstructionFiles(string? workingDirectory, IReadOnlyList<string> projectRoots)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidateRelativePaths = new[]
        {
            "AGENTS.md",
            "CLAUDE.md",
            Path.Combine(".github", "copilot-instructions.md"),
        };

        void AddWalk(string? root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return;
            }

            var current = Path.GetFullPath(root);
            var stack = new Stack<string>();
            while (!string.IsNullOrWhiteSpace(current))
            {
                stack.Push(current);
                var parent = Directory.GetParent(current);
                if (parent is null)
                {
                    break;
                }

                current = parent.FullName;
            }

            while (stack.Count > 0)
            {
                var directory = stack.Pop();
                var selectedFile = candidateRelativePaths
                    .Select(relativePath => Path.Combine(directory, relativePath))
                    .Where(File.Exists)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(static file => file.Length)
                    .ThenBy(static file => file.FullName, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (selectedFile is not null && seen.Add(selectedFile.FullName))
                {
                    files.Add(selectedFile.FullName);
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
    private sealed record PromptResource(string SourceKind, int Precedence, string Path, string Body, string? Description, string? DisplayName, string? SystemPromptName, PartialSystemPromptPartOptions PartOptions, string Hash, PromptCompositionMode Mode);
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

    /// <summary>Gets the optional CodeAlta user-global root used to resolve prompt overrides.</summary>
    public string? UserCodeAltaRoot { get; init; }

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

    /// <summary>Gets active-skills markdown restored from session state.</summary>
    public string? ActiveSkillsMarkdown { get; init; }
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
